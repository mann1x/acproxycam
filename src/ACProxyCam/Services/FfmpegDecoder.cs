// FfmpegDecoder.cs - FFmpeg-based FLV decoder for Linux
// Uses system-installed FFmpeg libraries

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ACProxyCam.Services;

/// <summary>
/// FFmpeg-based video decoder optimized for low-latency FLV streaming.
/// Outputs raw BGR24 frames for encoding by SkiaSharp.
/// </summary>
public unsafe class FfmpegDecoder : IDisposable
{
    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVFrame* _rgbFrame;
    private SwsContext* _swsContext;
    private byte* _rgbBuffer;
    private int _videoStreamIndex = -1;

    private Thread? _decodingThread;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler<FrameEventArgs>? FrameDecoded;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? DecodingStarted;
    public event EventHandler? DecodingStopped;

    public bool IsRunning => _isRunning;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesDecoded { get; private set; }

    private static bool _ffmpegInitialized;
    private static readonly object _initLock = new();

    /// <summary>
    /// Initialize FFmpeg libraries.
    /// Must be called before using any decoder instances.
    /// </summary>
    public static void Initialize()
    {
        lock (_initLock)
        {
            if (_ffmpegInitialized)
                return;

            // Try common FFmpeg library paths on Linux
            string[] possiblePaths =
            {
                "/usr/lib/x86_64-linux-gnu",      // Debian/Ubuntu x64
                "/usr/lib/aarch64-linux-gnu",     // Debian/Ubuntu arm64
                "/usr/lib64",                      // Fedora/RHEL
                "/usr/lib",                        // Arch, generic
                "/usr/local/lib",                  // Custom installations
            };

            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path))
                {
                    var avcodecPath = Path.Combine(path, "libavcodec.so");
                    if (File.Exists(avcodecPath) || Directory.GetFiles(path, "libavcodec.so*").Length > 0)
                    {
                        ffmpeg.RootPath = path;
                        break;
                    }
                }
            }

            try
            {
                // Test that FFmpeg can be loaded
                var version = ffmpeg.avcodec_version();
                var major = version >> 16;
                var minor = (version >> 8) & 0xFF;
                var micro = version & 0xFF;

                Console.WriteLine($"FFmpeg initialized: avcodec {major}.{minor}.{micro}");
                _ffmpegInitialized = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "FFmpeg libraries not found. Please install FFmpeg:\n" +
                    "  Ubuntu/Debian: sudo apt install ffmpeg libavcodec-dev libavformat-dev libavutil-dev libswscale-dev\n" +
                    "  Fedora/RHEL: sudo dnf install ffmpeg ffmpeg-devel\n" +
                    "  Arch: sudo pacman -S ffmpeg\n\n" +
                    $"Error: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Start decoding the FLV stream.
    /// </summary>
    public void Start(string url)
    {
        if (_isRunning)
            Stop();

        Initialize();

        _cts = new CancellationTokenSource();
        _decodingThread = new Thread(() => DecodingLoop(url, _cts.Token))
        {
            IsBackground = true,
            Name = "FfmpegDecodingThread"
        };
        _decodingThread.Start();
    }

    /// <summary>
    /// Stop decoding.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning && _decodingThread == null)
            return; // Already stopped

        _cts?.Cancel();
        _decodingThread?.Join(2000);
        _decodingThread = null;
        // Note: _isRunning is set to false and DecodingStopped is invoked in DecodingLoop's finally block
    }

    private void DecodingLoop(string url, CancellationToken ct)
    {
        try
        {
            _isRunning = true;
            FramesDecoded = 0;
            StatusChanged?.Invoke(this, "Connecting to stream...");

            // Open input
            if (!OpenInput(url))
            {
                StatusChanged?.Invoke(this, "Failed to open stream");
                return;
            }

            // Find video stream
            if (!FindVideoStream())
            {
                StatusChanged?.Invoke(this, "No video stream found");
                CloseInput();
                return;
            }

            // Open decoder
            if (!OpenDecoder())
            {
                StatusChanged?.Invoke(this, "Failed to open decoder");
                CloseInput();
                return;
            }

            StatusChanged?.Invoke(this, $"Decoding {Width}x{Height}");
            DecodingStarted?.Invoke(this, EventArgs.Empty);

            // Allocate frames
            _frame = ffmpeg.av_frame_alloc();
            _rgbFrame = ffmpeg.av_frame_alloc();

            // Allocate RGB buffer
            int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR24, Width, Height, 1);
            _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);

            // Fill RGB frame data
            int stride = Width * 3; // BGR24 = 3 bytes per pixel
            _rgbFrame->data[0] = _rgbBuffer;
            _rgbFrame->linesize[0] = stride;

            // Create scaler (SWS_BILINEAR = 2)
            _swsContext = ffmpeg.sws_getContext(
                Width, Height, _codecContext->pix_fmt,
                Width, Height, AVPixelFormat.AV_PIX_FMT_BGR24,
                2, null, null, null);

            // Read and decode frames
            AVPacket* packet = ffmpeg.av_packet_alloc();
            int errorCount = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int ret = ffmpeg.av_read_frame(_formatContext, packet);
                    if (ret < 0)
                    {
                        errorCount++;
                        if (ret == ffmpeg.AVERROR_EOF)
                        {
                            StatusChanged?.Invoke(this, "End of stream");
                            break;
                        }
                        // For live streams, brief errors might be recoverable
                        Thread.Sleep(10);
                        continue;
                    }
                    errorCount = 0;

                    if (packet->stream_index == _videoStreamIndex)
                    {
                        DecodePacket(packet);
                    }

                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            StatusChanged?.Invoke(this, $"Error: {ex.Message}");
        }
        finally
        {
            Cleanup();
            _isRunning = false;
            DecodingStopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool OpenInput(string url)
    {
        AVFormatContext* formatContext = null;

        int ret = ffmpeg.avformat_open_input(&formatContext, url, null, null);
        if (ret < 0)
        {
            StatusChanged?.Invoke(this, $"Failed to open input: {GetErrorMessage(ret)}");
            return false;
        }

        _formatContext = formatContext;

        // Set low-latency options
        _formatContext->max_analyze_duration = 100000; // 100ms
        _formatContext->probesize = 32768;             // 32KB
        _formatContext->flags |= ffmpeg.AVFMT_FLAG_NOBUFFER;

        ret = ffmpeg.avformat_find_stream_info(_formatContext, null);
        if (ret < 0)
        {
            StatusChanged?.Invoke(this, $"Failed to find stream info: {GetErrorMessage(ret)}");
            return false;
        }

        return true;
    }

    private bool FindVideoStream()
    {
        for (int i = 0; i < _formatContext->nb_streams; i++)
        {
            if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                _videoStreamIndex = i;
                Width = _formatContext->streams[i]->codecpar->width;
                Height = _formatContext->streams[i]->codecpar->height;
                return true;
            }
        }
        return false;
    }

    private bool OpenDecoder()
    {
        AVCodecParameters* codecParams = _formatContext->streams[_videoStreamIndex]->codecpar;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
        if (codec == null)
        {
            StatusChanged?.Invoke(this, "Codec not found");
            return false;
        }

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(_codecContext, codecParams);

        // Low-latency decoder options
        _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        _codecContext->thread_count = Math.Min(Environment.ProcessorCount, 4);
        _codecContext->thread_type = ffmpeg.FF_THREAD_SLICE;

        int ret = ffmpeg.avcodec_open2(_codecContext, codec, null);
        if (ret < 0)
        {
            StatusChanged?.Invoke(this, $"Failed to open codec: {GetErrorMessage(ret)}");
            return false;
        }

        return true;
    }

    private void DecodePacket(AVPacket* packet)
    {
        int ret = ffmpeg.avcodec_send_packet(_codecContext, packet);
        if (ret < 0)
            return;

        while (ret >= 0)
        {
            ret = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0)
                break;

            // Convert to BGR24
            ffmpeg.sws_scale(_swsContext,
                _frame->data, _frame->linesize, 0, Height,
                _rgbFrame->data, _rgbFrame->linesize);

            // Copy to managed array
            int stride = _rgbFrame->linesize[0];
            int size = stride * Height;
            byte[] frameData = new byte[size];
            Marshal.Copy((IntPtr)_rgbFrame->data[0], frameData, 0, size);

            FramesDecoded++;

            // Raise event with frame data
            FrameDecoded?.Invoke(this, new FrameEventArgs(frameData, Width, Height, stride));

            ffmpeg.av_frame_unref(_frame);
        }
    }

    private void CloseInput()
    {
        if (_formatContext != null)
        {
            fixed (AVFormatContext** ctx = &_formatContext)
            {
                ffmpeg.avformat_close_input(ctx);
            }
            _formatContext = null;
        }
    }

    private void Cleanup()
    {
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_rgbBuffer != null)
        {
            ffmpeg.av_free(_rgbBuffer);
            _rgbBuffer = null;
        }

        if (_rgbFrame != null)
        {
            fixed (AVFrame** frame = &_rgbFrame)
            {
                ffmpeg.av_frame_free(frame);
            }
            _rgbFrame = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** frame = &_frame)
            {
                ffmpeg.av_frame_free(frame);
            }
            _frame = null;
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** ctx = &_codecContext)
            {
                ffmpeg.avcodec_free_context(ctx);
            }
            _codecContext = null;
        }

        CloseInput();
    }

    private static string GetErrorMessage(int error)
    {
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"Error {error}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        Cleanup();
        GC.SuppressFinalize(this);
    }

    ~FfmpegDecoder()
    {
        Dispose();
    }
}

/// <summary>
/// Event args containing decoded frame data.
/// </summary>
public class FrameEventArgs : EventArgs
{
    /// <summary>
    /// Raw BGR24 pixel data.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// Frame width in pixels.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Frame height in pixels.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Bytes per row (may include padding).
    /// </summary>
    public int Stride { get; }

    public FrameEventArgs(byte[] data, int width, int height, int stride)
    {
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
    }
}
