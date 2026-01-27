// FfmpegDecoder.cs - FFmpeg-based FLV decoder for Linux
// Uses system-installed FFmpeg libraries

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ACProxyCam.Daemon;

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

    // Stream health monitoring
    private long _lastPts = long.MinValue;
    private DateTime _lastFrameTime = DateTime.MinValue;
    private int _staleFrameCount;
    private const int StaleFrameThreshold = 30; // Consider stream stalled after 30 frames with same PTS

    // Incoming H.264 FPS tracking (sliding window)
    private int _fpsFrameCount;
    private DateTime _fpsWindowStart = DateTime.MinValue;
    private int _measuredFps;
    private const int FpsWindowMs = 1000; // Calculate FPS over 1 second window

    public event EventHandler<FrameEventArgs>? FrameDecoded;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? DecodingStarted;
    public event EventHandler? DecodingStopped;
    /// <summary>
    /// Raised when stream is detected as stalled (no new frames or PTS not advancing).
    /// </summary>
    public event EventHandler? StreamStalled;
    /// <summary>
    /// Raised when a raw H.264 packet is received (before decoding).
    /// Used for RTP streaming to share the source stream.
    /// </summary>
    public event EventHandler<RawPacketEventArgs>? RawPacketReceived;

    public bool IsRunning => _isRunning;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FramesDecoded { get; private set; }

    /// <summary>
    /// When false, skips CPU-intensive H.264 decode (but still emits RawPacketReceived).
    /// Set to false when no MJPEG clients need decoded frames.
    /// </summary>
    public bool DecodeEnabled { get; set; } = true;

    /// <summary>
    /// H.264 extradata (contains SPS and PPS in AVCC format).
    /// Available after stream opens.
    /// </summary>
    public byte[]? Extradata { get; private set; }

    /// <summary>
    /// Current PTS (presentation timestamp) of the stream.
    /// </summary>
    public long CurrentPts => _lastPts;

    /// <summary>
    /// Time since last frame was decoded.
    /// Returns MaxValue if no frames have ever been decoded (to trigger stall detection).
    /// </summary>
    public TimeSpan TimeSinceLastFrame =>
        _lastFrameTime == DateTime.MinValue ? TimeSpan.MaxValue : DateTime.UtcNow - _lastFrameTime;

    /// <summary>
    /// Measured incoming H.264 stream FPS (calculated over 1 second window).
    /// </summary>
    public int MeasuredFps => _measuredFps;

    private static bool _ffmpegInitialized;
    private static readonly object _initLock = new();
    private static av_log_set_callback_callback? _logCallback;
    private static bool _logCallbackSet;

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

                Logger.Log($"FFmpeg initialized: avcodec {major}.{minor}.{micro}");

                // Set up log callback for throttled FFmpeg logging
                SetupLogCallback();

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
    /// Set up FFmpeg log callback to capture and throttle log messages.
    /// </summary>
    private static void SetupLogCallback()
    {
        if (_logCallbackSet)
            return;

        // Set FFmpeg log level to only show errors (suppress info/warning spam)
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

        _logCallback = (p0, level, format, vl) =>
        {
            // Only process errors and below
            if (level > ffmpeg.AV_LOG_ERROR)
                return;

            // Get the message from FFmpeg's format string
            var lineSize = 1024;
            var lineBuffer = stackalloc byte[lineSize];
            var printPrefix = 1;
            ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
            var message = Marshal.PtrToStringAnsi((IntPtr)lineBuffer)?.Trim();

            if (string.IsNullOrEmpty(message))
                return;

            // Use throttler to prevent log flooding
            var throttledMessage = FfmpegLogThrottler.ThrottleMessage(message);
            if (throttledMessage != null)
            {
                Logger.Debug($"[FFmpeg] {throttledMessage}");
            }
        };

        ffmpeg.av_log_set_callback(_logCallback);
        _logCallbackSet = true;
    }

    /// <summary>
    /// Reset FFmpeg log throttling (e.g., when starting a new stream).
    /// </summary>
    public static void ResetLogThrottling()
    {
        FfmpegLogThrottler.ResetAll();
    }

    /// <summary>
    /// Start decoding the FLV stream.
    /// </summary>
    public void Start(string url)
    {
        if (_isRunning)
            Stop();

        Initialize();

        // Reset stream health state
        _lastPts = long.MinValue;
        _lastFrameTime = DateTime.MinValue;
        _staleFrameCount = 0;

        // Reset log throttling for fresh start
        ResetLogThrottling();

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

            var stream = _formatContext->streams[_videoStreamIndex];
            StatusChanged?.Invoke(this, $"Decoding {Width}x{Height}, time_base={stream->time_base.num}/{stream->time_base.den}");
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
                        // Track incoming H.264 FPS
                        var now = DateTime.UtcNow;
                        if (_fpsWindowStart == DateTime.MinValue)
                        {
                            _fpsWindowStart = now;
                            _fpsFrameCount = 0;
                        }
                        _fpsFrameCount++;

                        var windowElapsed = (now - _fpsWindowStart).TotalMilliseconds;
                        if (windowElapsed >= FpsWindowMs)
                        {
                            _measuredFps = (int)Math.Round(_fpsFrameCount * 1000.0 / windowElapsed);
                            _fpsWindowStart = now;
                            _fpsFrameCount = 0;
                        }

                        // Emit raw packet for RTP streaming before decoding
                        if (RawPacketReceived != null)
                        {
                            byte[] packetData = new byte[packet->size];
                            Marshal.Copy((IntPtr)packet->data, packetData, 0, packet->size);
                            bool isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                            // Convert PTS from stream time_base to milliseconds
                            // This is critical for HLS duration calculations to match MPEG-TS timing
                            var videoStream = _formatContext->streams[_videoStreamIndex];
                            long ptsMs = packet->pts;
                            long dtsMs = packet->dts;
                            if (packet->pts != ffmpeg.AV_NOPTS_VALUE && videoStream->time_base.den > 0)
                            {
                                // pts_ms = pts * (num/den) * 1000 = pts * num * 1000 / den
                                ptsMs = packet->pts * videoStream->time_base.num * 1000 / videoStream->time_base.den;
                            }
                            if (packet->dts != ffmpeg.AV_NOPTS_VALUE && videoStream->time_base.den > 0)
                            {
                                dtsMs = packet->dts * videoStream->time_base.num * 1000 / videoStream->time_base.den;
                            }

                            RawPacketReceived?.Invoke(this, new RawPacketEventArgs(packetData, isKeyframe, ptsMs, dtsMs));
                        }

                        // Only decode if enabled (skip CPU-intensive decode when no MJPEG clients)
                        if (DecodeEnabled)
                        {
                            DecodePacket(packet);
                        }
                        else
                        {
                            // Still update timing to prevent stall detection when decode is disabled
                            _lastFrameTime = DateTime.UtcNow;
                        }
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

        // Extract extradata (contains SPS/PPS for H.264)
        if (_codecContext->extradata != null && _codecContext->extradata_size > 0)
        {
            Extradata = new byte[_codecContext->extradata_size];
            Marshal.Copy((IntPtr)_codecContext->extradata, Extradata, 0, _codecContext->extradata_size);
            StatusChanged?.Invoke(this, $"Extradata available: {_codecContext->extradata_size} bytes");
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

            // Track PTS for stream health monitoring
            var pts = _frame->pts;
            if (pts != ffmpeg.AV_NOPTS_VALUE)
            {
                if (pts == _lastPts)
                {
                    _staleFrameCount++;
                    if (_staleFrameCount >= StaleFrameThreshold)
                    {
                        // Stream might be stalled - same PTS for too many frames
                        StreamStalled?.Invoke(this, EventArgs.Empty);
                        _staleFrameCount = 0; // Reset to avoid repeated events
                    }
                }
                else
                {
                    _staleFrameCount = 0;
                    _lastPts = pts;
                }
            }
            _lastFrameTime = DateTime.UtcNow;

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

/// <summary>
/// Event args containing raw H.264 packet data (before decoding).
/// </summary>
public class RawPacketEventArgs : EventArgs
{
    /// <summary>
    /// Raw H.264 NAL unit data.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>
    /// Whether this packet contains a keyframe (IDR).
    /// </summary>
    public bool IsKeyframe { get; }

    /// <summary>
    /// Presentation timestamp.
    /// </summary>
    public long Pts { get; }

    /// <summary>
    /// Decoding timestamp.
    /// </summary>
    public long Dts { get; }

    public RawPacketEventArgs(byte[] data, bool isKeyframe, long pts, long dts)
    {
        Data = data;
        IsKeyframe = isKeyframe;
        Pts = pts;
        Dts = dts;
    }
}
