// H264SnapshotDecoder.cs - On-demand H.264 keyframe to JPEG decoder
// Decodes a single H.264 IDR frame to JPEG without continuous decoding

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace ACProxyCam.Services;

/// <summary>
/// Decodes H.264 keyframes on-demand to JPEG snapshots.
/// This avoids continuous MJPEG encoding when only snapshots are needed.
/// Thread-safe: can be called from multiple threads.
/// </summary>
public unsafe class H264SnapshotDecoder : IDisposable
{
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVFrame* _rgbFrame;
    private SwsContext* _swsContext;
    private byte* _rgbBuffer;
    private AVPacket* _packet;

    private readonly object _decodeLock = new();
    private bool _initialized;
    private bool _disposed;

    private int _width;
    private int _height;

    public int JpegQuality { get; set; } = 80;

    /// <summary>
    /// Decode an H.264 keyframe (Annex B format with SPS/PPS) to JPEG.
    /// </summary>
    /// <param name="annexBData">H.264 data in Annex B format (start codes + NAL units)</param>
    /// <returns>JPEG data, or null if decoding failed</returns>
    public byte[]? DecodeKeyframeToJpeg(byte[] annexBData)
    {
        if (annexBData == null || annexBData.Length < 10)
            return null;

        lock (_decodeLock)
        {
            if (_disposed)
                return null;

            try
            {
                // Initialize codec on first use
                if (!_initialized)
                {
                    if (!Initialize())
                        return null;
                }

                // Reset decoder state for clean decode
                ffmpeg.avcodec_flush_buffers(_codecContext);

                // Send the keyframe data to decoder
                fixed (byte* data = annexBData)
                {
                    _packet->data = data;
                    _packet->size = annexBData.Length;
                    _packet->flags = ffmpeg.AV_PKT_FLAG_KEY;

                    int ret = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                    if (ret < 0)
                        return null;
                }

                // Receive decoded frame
                int receiveRet = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                if (receiveRet < 0)
                    return null;

                // Check if dimensions changed (or first decode)
                if (_frame->width != _width || _frame->height != _height)
                {
                    _width = _frame->width;
                    _height = _frame->height;

                    // Recreate scaler and RGB buffer for new dimensions
                    if (!SetupScaler())
                    {
                        ffmpeg.av_frame_unref(_frame);
                        return null;
                    }
                }

                // Convert to BGR24
                ffmpeg.sws_scale(_swsContext,
                    _frame->data, _frame->linesize, 0, _height,
                    _rgbFrame->data, _rgbFrame->linesize);

                // Copy to managed array
                int stride = _rgbFrame->linesize[0];
                int size = stride * _height;
                byte[] bgrData = new byte[size];
                Marshal.Copy((IntPtr)_rgbFrame->data[0], bgrData, 0, size);

                ffmpeg.av_frame_unref(_frame);

                // Encode to JPEG
                return EncodeBgrToJpeg(bgrData, _width, _height, stride);
            }
            catch
            {
                return null;
            }
        }
    }

    private bool Initialize()
    {
        try
        {
            // Find H.264 decoder
            AVCodec* codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
                return false;

            // Allocate codec context
            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext == null)
                return false;

            // Low-latency options for single frame decode
            _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _codecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
            _codecContext->thread_count = 1; // Single frame, single thread

            // Open codec
            int ret = ffmpeg.avcodec_open2(_codecContext, codec, null);
            if (ret < 0)
            {
                fixed (AVCodecContext** ctx = &_codecContext)
                    ffmpeg.avcodec_free_context(ctx);
                return false;
            }

            // Allocate frames and packet
            _frame = ffmpeg.av_frame_alloc();
            _rgbFrame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();

            if (_frame == null || _rgbFrame == null || _packet == null)
            {
                Cleanup();
                return false;
            }

            _initialized = true;
            return true;
        }
        catch
        {
            Cleanup();
            return false;
        }
    }

    private bool SetupScaler()
    {
        // Free old scaler and buffer
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

        if (_width <= 0 || _height <= 0)
            return false;

        // Allocate RGB buffer
        int bufferSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGR24, _width, _height, 1);
        _rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
        if (_rgbBuffer == null)
            return false;

        // Set up RGB frame
        int stride = _width * 3;
        _rgbFrame->data[0] = _rgbBuffer;
        _rgbFrame->linesize[0] = stride;

        // Create scaler
        _swsContext = ffmpeg.sws_getContext(
            _width, _height, _codecContext->pix_fmt,
            _width, _height, AVPixelFormat.AV_PIX_FMT_BGR24,
            ffmpeg.SWS_BILINEAR, null, null, null);

        return _swsContext != null;
    }

    private byte[]? EncodeBgrToJpeg(byte[] bgrData, int width, int height, int stride)
    {
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);

            var pixels = (byte*)bitmap.GetPixels().ToPointer();
            int srcOffset = 0;
            int dstOffset = 0;

            for (int y = 0; y < height; y++)
            {
                srcOffset = y * stride;
                dstOffset = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    // Input is BGR24, output is BGRA8888
                    pixels[dstOffset++] = bgrData[srcOffset++]; // B
                    pixels[dstOffset++] = bgrData[srcOffset++]; // G
                    pixels[dstOffset++] = bgrData[srcOffset++]; // R
                    pixels[dstOffset++] = 255;                   // A
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

            return data.ToArray();
        }
        catch
        {
            return null;
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
                ffmpeg.av_frame_free(frame);
            _rgbFrame = null;
        }

        if (_frame != null)
        {
            fixed (AVFrame** frame = &_frame)
                ffmpeg.av_frame_free(frame);
            _frame = null;
        }

        if (_packet != null)
        {
            fixed (AVPacket** pkt = &_packet)
                ffmpeg.av_packet_free(pkt);
            _packet = null;
        }

        if (_codecContext != null)
        {
            fixed (AVCodecContext** ctx = &_codecContext)
                ffmpeg.avcodec_free_context(ctx);
            _codecContext = null;
        }

        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_decodeLock)
        {
            Cleanup();
        }

        GC.SuppressFinalize(this);
    }

    ~H264SnapshotDecoder()
    {
        Dispose();
    }
}
