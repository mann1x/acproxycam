// FfmpegEncoder.cs - MJPEG to H.264 encoding service
// Decodes JPEG frames and re-encodes as H.264 using FFmpeg

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ACProxyCam.Daemon;

namespace ACProxyCam.Services;

/// <summary>
/// Encodes JPEG frames to H.264 using FFmpeg.
/// Supports hardware (VAAPI, V4L2M2M, NVENC, QSV) and software (libx264) encoders.
/// Thread-safe: PushJpegFrame can be called from multiple threads.
/// </summary>
public unsafe class FfmpegEncoder : IDisposable, IH264PacketSource
{
    // MJPEG decoder
    private AVCodecContext* _decoderCtx;
    private AVPacket* _decoderPacket;
    private AVFrame* _decodedFrame;

    // Pixel format conversion (YUVJ420P → YUV420P or NV12)
    private SwsContext* _swsContext;
    private AVFrame* _scaledFrame;
    private byte* _scaledBuffer;

    // H.264 encoder
    private AVCodecContext* _encoderCtx;
    private AVPacket* _encoderPacket;

    // VAAPI hardware context
    private AVBufferRef* _hwDeviceCtx;
    private bool _needsHwUpload;

    // State
    private readonly object _encodeLock = new();
    private bool _initialized;
    private bool _disposed;
    private bool _decoderInitFailed;
    private int _decodeFailCount;
    private int _width;
    private int _height;
    private long _frameCount;
    private DateTime _lastEncodeTime = DateTime.MinValue;
    private DateTime _firstFrameTime = DateTime.MinValue;
    private int _framesSinceEncoderInit;
    private readonly HashSet<string> _failedEncoders = new();

    // Configuration
    public string EncoderName { get; set; } = "auto";
    public int Bitrate { get; set; } = 1024; // kbps
    public string RateControl { get; set; } = "vbr";
    public int GopSize { get; set; } = 30;
    public string Preset { get; set; } = "medium";
    public string Profile { get; set; } = "main";
    public int MaxFps { get; set; } = 0; // 0 = no limit
    public int InputFps { get; set; } = 0; // measured input FPS (0 = not yet known, defer init)

    /// <summary>
    /// AVCC-format extradata containing SPS/PPS.
    /// Available after first successful encode.
    /// </summary>
    public byte[]? Extradata { get; private set; }

    /// <summary>
    /// Name of the encoder actually being used.
    /// </summary>
    public string? ActiveEncoderName { get; private set; }

    /// <summary>
    /// Whether the active encoder is hardware-accelerated.
    /// </summary>
    public bool IsHardwareEncoder { get; private set; }

    /// <summary>
    /// Raised when an encoded H.264 packet is available.
    /// Data is in AVCC format (4-byte length-prefixed NAL units).
    /// </summary>
    public event EventHandler<RawPacketEventArgs>? EncodedPacketReceived;

    // IH264PacketSource explicit implementation - maps to EncodedPacketReceived
    event EventHandler<RawPacketEventArgs>? IH264PacketSource.RawPacketReceived
    {
        add => EncodedPacketReceived += value;
        remove => EncodedPacketReceived -= value;
    }

    /// <summary>
    /// Push a JPEG frame for encoding to H.264.
    /// Thread-safe. Lazy-initializes encoder on first frame.
    /// </summary>
    public void PushJpegFrame(byte[] jpegData)
    {
        if (jpegData == null || jpegData.Length < 10)
            return;

        lock (_encodeLock)
        {
            if (_disposed)
                return;

            // Rate limiting
            if (MaxFps > 0 && _lastEncodeTime != DateTime.MinValue)
            {
                var minInterval = TimeSpan.FromSeconds(1.0 / MaxFps);
                if (DateTime.UtcNow - _lastEncodeTime < minInterval)
                    return;
            }

            try
            {
                // Validate JPEG framing
                if (_frameCount < 100 || _frameCount % 100 == 0)
                {
                    bool hasSOI = jpegData.Length >= 2 && jpegData[0] == 0xFF && jpegData[1] == 0xD8;
                    bool hasEOI = jpegData.Length >= 2 && jpegData[^2] == 0xFF && jpegData[^1] == 0xD9;
                    if (!hasSOI || !hasEOI)
                        Logger.Log($"FfmpegEncoder: JPEG frame #{_frameCount} invalid: SOI={hasSOI} EOI={hasEOI} len={jpegData.Length} first=[{jpegData[0]:X2} {jpegData[1]:X2}] last=[{jpegData[^2]:X2} {jpegData[^1]:X2}]");
                }

                // Decode JPEG
                var decoded = DecodeJpeg(jpegData);
                if (decoded == null)
                {
                    _decodeFailCount++;
                    if (_decodeFailCount == 1)
                        Logger.Log($"FfmpegEncoder: JPEG decode failed (decoderInitFailed={_decoderInitFailed})");
                    return;
                }
                _decodeFailCount = 0; // Reset on success

                // Lazy init encoder on first successful decode (we need resolution + input FPS)
                if (!_initialized)
                {
                    _width = decoded->width;
                    _height = decoded->height;

                    // Defer encoder init until we have a measured input FPS.
                    // InputFps is updated by PrinterThread from MjpegServer.MeasuredInputFps.
                    if (InputFps <= 0)
                    {
                        ffmpeg.av_frame_unref(decoded);
                        return; // wait for FPS measurement
                    }

                    if (!InitializeEncoder())
                    {
                        ffmpeg.av_frame_unref(decoded);
                        return;
                    }
                    _initialized = true;
                    _framesSinceEncoderInit = 0;
                }

                // Handle resolution change
                if (decoded->width != _width || decoded->height != _height)
                {
                    Logger.Log($"FfmpegEncoder: resolution changed {_width}x{_height} → {decoded->width}x{decoded->height}, reinitializing");
                    CleanupEncoder();
                    _width = decoded->width;
                    _height = decoded->height;
                    if (!InitializeEncoder())
                    {
                        ffmpeg.av_frame_unref(decoded);
                        return;
                    }
                    _initialized = true;
                }

                // Convert pixel format if needed
                var frameToEncode = ConvertPixelFormat(decoded);

                if (frameToEncode == null)
                {
                    ffmpeg.av_frame_unref(decoded);
                    TriggerEncoderFallback("pixel format conversion failed");
                    return;
                }

                // Unref decoded frame only if conversion produced a different frame
                bool decodedIsReused = (frameToEncode == decoded);
                if (!decodedIsReused)
                    ffmpeg.av_frame_unref(decoded);

                // Upload to hardware if VAAPI
                AVFrame* hwFrame = null;
                if (_needsHwUpload)
                {
                    hwFrame = UploadToHardware(frameToEncode);
                    if (frameToEncode != _scaledFrame)
                        ffmpeg.av_frame_unref(frameToEncode);
                    if (hwFrame == null)
                    {
                        TriggerEncoderFallback("hardware upload failed");
                        return;
                    }
                    frameToEncode = hwFrame;
                }

                // Set PTS using wall-clock time (millisecond timebase)
                var now = DateTime.UtcNow;
                if (_firstFrameTime == DateTime.MinValue)
                    _firstFrameTime = now;
                frameToEncode->pts = (long)(now - _firstFrameTime).TotalMilliseconds;
                frameToEncode->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;

                // Encode
                int ret = ffmpeg.avcodec_send_frame(_encoderCtx, frameToEncode);

                // Cleanup frames after send_frame has copied/referenced the data
                if (hwFrame != null)
                {
                    ffmpeg.av_frame_unref(hwFrame);
                    ffmpeg.av_frame_free(&hwFrame);
                }
                else if (decodedIsReused)
                {
                    ffmpeg.av_frame_unref(decoded);
                }
                // _scaledFrame is reused across calls, don't free it

                if (ret < 0)
                {
                    TriggerEncoderFallback($"send_frame error {ret}");
                    return;
                }

                // Receive encoded packets
                while (true)
                {
                    ret = ffmpeg.avcodec_receive_packet(_encoderCtx, _encoderPacket);
                    if (ret < 0)
                        break;

                    // Convert Annex B → AVCC format
                    var avccData = ConvertAnnexBToAvcc(_encoderPacket->data, _encoderPacket->size);
                    if (avccData != null)
                    {
                        bool isKeyframe = (_encoderPacket->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
                        long ptsMs = _encoderPacket->pts * 1000 * _encoderCtx->time_base.num / _encoderCtx->time_base.den;
                        long dtsMs = _encoderPacket->dts * 1000 * _encoderCtx->time_base.num / _encoderCtx->time_base.den;

                        EncodedPacketReceived?.Invoke(this,
                            new RawPacketEventArgs(avccData, isKeyframe, ptsMs, dtsMs));

                        // Extract extradata on first encode
                        if (Extradata == null && _encoderCtx->extradata != null && _encoderCtx->extradata_size > 0)
                        {
                            Extradata = new byte[_encoderCtx->extradata_size];
                            Marshal.Copy((IntPtr)_encoderCtx->extradata, Extradata, 0, _encoderCtx->extradata_size);
                        }
                    }

                    ffmpeg.av_packet_unref(_encoderPacket);
                }

                _lastEncodeTime = DateTime.UtcNow;
                _frameCount++;
                _framesSinceEncoderInit++;

                // Detect broken encoder: if no extradata after 30 frames, the encoder
                // is not producing output (e.g. h264_v4l2m2m on Pi 5 which has V4L2
                // decoder devices but no encoder support). Try the next encoder.
                if (Extradata == null && _framesSinceEncoderInit >= 30 && ActiveEncoderName != null)
                {
                    TriggerEncoderFallback("no output after 30 frames");
                }
            }
            catch (Exception ex)
            {
                if (ActiveEncoderName != null)
                {
                    TriggerEncoderFallback(ex.Message);
                }
                else
                {
                    // Exception before encoder init (e.g. FFmpeg library load failure, decoder error)
                    _decodeFailCount++;
                    if (_decodeFailCount == 1)
                        Logger.Log($"FfmpegEncoder: exception before encoder init: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Decode a JPEG frame using the MJPEG decoder.
    /// Returns the decoded frame (caller must unref) or null on failure.
    /// </summary>
    private AVFrame* DecodeJpeg(byte[] jpegData)
    {
        if (_decoderCtx == null)
        {
            if (_decoderInitFailed)
                return null;

            if (!InitializeDecoder())
            {
                _decoderInitFailed = true;
                return null;
            }
        }

        fixed (byte* data = jpegData)
        {
            _decoderPacket->data = data;
            _decoderPacket->size = jpegData.Length;

            int ret = ffmpeg.avcodec_send_packet(_decoderCtx, _decoderPacket);
            if (ret < 0)
                return null;

            ret = ffmpeg.avcodec_receive_frame(_decoderCtx, _decodedFrame);
            if (ret < 0)
                return null;

            return _decodedFrame;
        }
    }

    /// <summary>
    /// Initialize the MJPEG decoder.
    /// </summary>
    private bool InitializeDecoder()
    {
        // Ensure FFmpeg libraries are loaded before any FFmpeg calls
        FfmpegDecoder.Initialize();

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_MJPEG);
        if (codec == null)
        {
            Logger.Log("FfmpegEncoder: MJPEG decoder not found");
            return false;
        }

        _decoderCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_decoderCtx == null)
        {
            Logger.Log("FfmpegEncoder: failed to allocate MJPEG decoder context");
            return false;
        }

        _decoderCtx->thread_count = 1;

        int ret = ffmpeg.avcodec_open2(_decoderCtx, codec, null);
        if (ret < 0)
        {
            Logger.Log($"FfmpegEncoder: failed to open MJPEG decoder (error {ret})");
            fixed (AVCodecContext** ctx = &_decoderCtx)
                ffmpeg.avcodec_free_context(ctx);
            return false;
        }

        _decoderPacket = ffmpeg.av_packet_alloc();
        _decodedFrame = ffmpeg.av_frame_alloc();

        if (_decoderPacket == null || _decodedFrame == null)
        {
            Logger.Log("FfmpegEncoder: failed to allocate decoder packet/frame");
            CleanupDecoder();
            return false;
        }

        Logger.Log("FfmpegEncoder: MJPEG decoder initialized");
        return true;
    }

    /// <summary>
    /// Initialize the H.264 encoder based on configuration.
    /// </summary>
    private bool InitializeEncoder()
    {
        // Ensure FFmpeg is initialized
        FfmpegDecoder.Initialize();

        // Select encoder, skipping any that previously failed to produce output
        EncoderInfo? encoderInfo;
        if (_failedEncoders.Count > 0)
        {
            var candidates = FfmpegEncoderDetector.DetectEncoders()
                .Where(e => !_failedEncoders.Contains(e.Name))
                .ToList();

            if (!string.IsNullOrEmpty(EncoderName) && EncoderName != "auto")
            {
                encoderInfo = candidates.FirstOrDefault(e =>
                    e.Name.Equals(EncoderName, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault();
            }
            else
            {
                encoderInfo = candidates.FirstOrDefault();
            }
        }
        else
        {
            encoderInfo = FfmpegEncoderDetector.SelectBestEncoder(EncoderName);
        }

        if (encoderInfo == null)
        {
            Logger.Log("FfmpegEncoder: no H.264 encoder available");
            return false;
        }

        ActiveEncoderName = encoderInfo.Name;
        IsHardwareEncoder = encoderInfo.IsHardware;
        Logger.Log($"FfmpegEncoder: using {encoderInfo.Name} ({encoderInfo.Description})");

        var codec = ffmpeg.avcodec_find_encoder_by_name(encoderInfo.Name);
        if (codec == null)
            return false;

        _encoderCtx = ffmpeg.avcodec_alloc_context3(codec);
        if (_encoderCtx == null)
            return false;

        // Base encoder configuration
        _encoderCtx->width = _width;
        _encoderCtx->height = _height;
        _encoderCtx->time_base = new AVRational { num = 1, den = 1000 };
        // Framerate hint for rate control — must match actual input FPS for correct bit budgeting.
        // Actual frame timing is driven by wall-clock PTS, this only affects rate control.
        var fpsHint = InputFps > 0 ? InputFps : 30; // fallback 30 if not yet measured
        _encoderCtx->framerate = new AVRational { num = fpsHint, den = 1 };
        _encoderCtx->gop_size = GopSize;
        _encoderCtx->max_b_frames = 0;
        _encoderCtx->thread_count = 0; // auto
        _encoderCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        // Rate control
        int bitrateBps = Bitrate * 1000;
        _encoderCtx->bit_rate = bitrateBps;

        if (RateControl.ToLowerInvariant() == "cbr")
        {
            _encoderCtx->rc_max_rate = bitrateBps;
            _encoderCtx->rc_min_rate = bitrateBps;
            _encoderCtx->rc_buffer_size = bitrateBps;
        }
        else
        {
            // VBR: set max rate to 1.5x target
            _encoderCtx->rc_max_rate = bitrateBps * 3 / 2;
            _encoderCtx->rc_buffer_size = bitrateBps * 2;
        }

        // Encoder-specific configuration
        AVDictionary* opts = null;
        bool success;

        switch (encoderInfo.Name)
        {
            case "libx264":
                success = ConfigureLibx264(&opts);
                break;
            case "h264_vaapi":
                success = ConfigureVaapi(&opts);
                break;
            case "h264_v4l2m2m":
                success = ConfigureV4l2m2m(&opts);
                break;
            case "h264_nvenc":
                success = ConfigureNvenc(&opts);
                break;
            case "h264_qsv":
                success = ConfigureQsv(&opts);
                break;
            default:
                // Generic fallback
                _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                success = true;
                break;
        }

        if (!success)
        {
            if (opts != null)
                ffmpeg.av_dict_free(&opts);
            CleanupEncoder();
            return false;
        }

        // Open encoder
        int ret = ffmpeg.avcodec_open2(_encoderCtx, codec, &opts);
        if (opts != null)
            ffmpeg.av_dict_free(&opts);

        if (ret < 0)
        {
            Logger.Log($"FfmpegEncoder: failed to open {encoderInfo.Name} (error {ret})");
            CleanupEncoder();
            return false;
        }

        // Extract extradata if available
        if (_encoderCtx->extradata != null && _encoderCtx->extradata_size > 0)
        {
            Extradata = new byte[_encoderCtx->extradata_size];
            Marshal.Copy((IntPtr)_encoderCtx->extradata, Extradata, 0, _encoderCtx->extradata_size);
        }

        // Allocate encoder packet
        _encoderPacket = ffmpeg.av_packet_alloc();
        if (_encoderPacket == null)
        {
            CleanupEncoder();
            return false;
        }

        Logger.Log($"FfmpegEncoder: initialized {encoderInfo.Name} at {_width}x{_height}, " +
            $"{Bitrate}kbps {RateControl}, GOP={GopSize}, fps_hint={fpsHint}, threads={_encoderCtx->thread_count}");

        return true;
    }

    /// <summary>
    /// Configure libx264 software encoder.
    /// </summary>
    private bool ConfigureLibx264(AVDictionary** opts)
    {
        // YUV420P: the slicer's decoder assumes limited range, so signaling full range
        // (YUVJ420P) causes dark video. Keep YUV420P — technically mislabeled for MJPEG
        // full-range source, but displays correctly in all known consumers.
        _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

        // Preset (ultrafast recommended for software encoding on low-power hardware)
        var preset = string.IsNullOrEmpty(Preset) ? "medium" : Preset;
        ffmpeg.av_dict_set(opts, "preset", preset, 0);

        // Low-latency streaming without sliced-threads (which causes horizontal tearing).
        // zerolatency tune enables sliced-threads that split each frame into independent
        // horizontal slices — during fast motion, slice boundaries become visible artifacts.
        // Instead, set the individual low-latency options we need:
        // - rc-lookahead=0: no rate control lookahead (low latency)
        // - sync-lookahead=0: no sync lookahead
        // max_b_frames=0 is already set on the codec context (no frame reordering)
        ffmpeg.av_dict_set(opts, "x264-params", "rc-lookahead=0:sync-lookahead=0", 0);

        // Profile
        var profile = string.IsNullOrEmpty(Profile) ? "main" : Profile;
        ffmpeg.av_dict_set(opts, "profile", profile, 0);

        return true;
    }

    /// <summary>
    /// Configure VAAPI hardware encoder.
    /// </summary>
    private bool ConfigureVaapi(AVDictionary** opts)
    {
        const string vaapiDevice = "/dev/dri/renderD128";

        // Create hardware device context
        AVBufferRef* hwDeviceCtx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
            vaapiDevice, null, 0);
        if (ret < 0)
        {
            Logger.Log($"FfmpegEncoder: VAAPI device creation failed (error {ret})");
            return false;
        }
        _hwDeviceCtx = hwDeviceCtx;

        _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;
        _encoderCtx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDeviceCtx);

        // Create hardware frames context
        var hwFramesRef = ffmpeg.av_hwframe_ctx_alloc(_hwDeviceCtx);
        if (hwFramesRef == null)
        {
            Logger.Log("FfmpegEncoder: VAAPI hw frames alloc failed");
            return false;
        }

        var hwFramesCtx = (AVHWFramesContext*)hwFramesRef->data;
        hwFramesCtx->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
        hwFramesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        hwFramesCtx->width = _width;
        hwFramesCtx->height = _height;
        hwFramesCtx->initial_pool_size = 8;

        ret = ffmpeg.av_hwframe_ctx_init(hwFramesRef);
        if (ret < 0)
        {
            ffmpeg.av_buffer_unref(&hwFramesRef);
            Logger.Log($"FfmpegEncoder: VAAPI hw frames init failed (error {ret})");
            return false;
        }

        _encoderCtx->hw_frames_ctx = hwFramesRef;
        _needsHwUpload = true;

        return true;
    }

    /// <summary>
    /// Configure V4L2 M2M hardware encoder (Raspberry Pi).
    /// </summary>
    private bool ConfigureV4l2m2m(AVDictionary** opts)
    {
        _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        return true;
    }

    /// <summary>
    /// Configure NVENC hardware encoder.
    /// </summary>
    private bool ConfigureNvenc(AVDictionary** opts)
    {
        _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;

        // Map preset names to nvenc presets (p1=fastest, p7=slowest)
        var preset = (Preset?.ToLowerInvariant()) switch
        {
            "ultrafast" or "superfast" => "p1",
            "veryfast" or "faster" => "p2",
            "fast" => "p3",
            "medium" or "" or null => "p4",
            "slow" => "p5",
            "slower" => "p6",
            "veryslow" => "p7",
            _ => Preset // Allow direct nvenc preset names (p1-p7)
        };

        ffmpeg.av_dict_set(opts, "preset", preset, 0);
        ffmpeg.av_dict_set(opts, "tune", "ll", 0); // Low latency

        return true;
    }

    /// <summary>
    /// Configure QuickSync hardware encoder.
    /// </summary>
    private bool ConfigureQsv(AVDictionary** opts)
    {
        _encoderCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        ffmpeg.av_dict_set(opts, "preset", Preset ?? "medium", 0);
        return true;
    }

    /// <summary>
    /// Convert decoded frame pixel format for the encoder.
    /// MJPEG decoder outputs YUVJ420P; most encoders need YUV420P or NV12.
    /// </summary>
    private AVFrame* ConvertPixelFormat(AVFrame* decoded)
    {
        // Determine target pixel format
        var targetFmt = _encoderCtx->pix_fmt;

        // For VAAPI, the encoder pix_fmt is VAAPI but we need NV12 for CPU-side frames
        if (_needsHwUpload)
        {
            targetFmt = AVPixelFormat.AV_PIX_FMT_NV12;
        }

        // Always convert through sws_scale to ensure the encoder gets its own
        // independent pixel buffer copy. Passing the decoder's frame directly
        // can cause tearing if the encoder's internal threading references
        // the buffer while the next frame is being decoded.

        // Setup scaler if needed or if dimensions changed
        if (_swsContext == null || _scaledFrame == null)
        {
            if (!SetupScaler(decoded->width, decoded->height, (AVPixelFormat)decoded->format, targetFmt))
                return null;
        }

        // Convert
        ffmpeg.sws_scale(_swsContext,
            decoded->data, decoded->linesize, 0, decoded->height,
            _scaledFrame->data, _scaledFrame->linesize);

        _scaledFrame->width = decoded->width;
        _scaledFrame->height = decoded->height;
        _scaledFrame->format = (int)targetFmt;

        return _scaledFrame;
    }

    /// <summary>
    /// Set up pixel format conversion scaler.
    /// </summary>
    private bool SetupScaler(int width, int height, AVPixelFormat srcFmt, AVPixelFormat dstFmt)
    {
        // Free existing
        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }
        if (_scaledBuffer != null)
        {
            ffmpeg.av_free(_scaledBuffer);
            _scaledBuffer = null;
        }
        if (_scaledFrame == null)
        {
            _scaledFrame = ffmpeg.av_frame_alloc();
            if (_scaledFrame == null)
                return false;
        }

        int bufferSize = ffmpeg.av_image_get_buffer_size(dstFmt, width, height, 32);
        _scaledBuffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
        if (_scaledBuffer == null)
            return false;

        var dataPtr = new byte_ptrArray4();
        var linesizeArr = new int_array4();
        ffmpeg.av_image_fill_arrays(ref dataPtr, ref linesizeArr, _scaledBuffer, dstFmt, width, height, 32);

        for (uint i = 0; i < 4; i++)
        {
            _scaledFrame->data[i] = dataPtr[i];
            _scaledFrame->linesize[i] = linesizeArr[i];
        }

        _swsContext = ffmpeg.sws_getContext(
            width, height, srcFmt,
            width, height, dstFmt,
            ffmpeg.SWS_BILINEAR, null, null, null);

        return _swsContext != null;
    }

    /// <summary>
    /// Upload a CPU-side frame to VAAPI hardware surface.
    /// </summary>
    private AVFrame* UploadToHardware(AVFrame* cpuFrame)
    {
        var hwFrame = ffmpeg.av_frame_alloc();
        if (hwFrame == null)
            return null;

        int ret = ffmpeg.av_hwframe_get_buffer(_encoderCtx->hw_frames_ctx, hwFrame, 0);
        if (ret < 0)
        {
            ffmpeg.av_frame_free(&hwFrame);
            return null;
        }

        ret = ffmpeg.av_hwframe_transfer_data(hwFrame, cpuFrame, 0);
        if (ret < 0)
        {
            ffmpeg.av_frame_unref(hwFrame);
            ffmpeg.av_frame_free(&hwFrame);
            return null;
        }

        hwFrame->pts = cpuFrame->pts;
        return hwFrame;
    }

    /// <summary>
    /// Convert Annex B format (start-code separated NAL units) to AVCC format (4-byte length-prefixed).
    /// FFmpeg encoders output Annex B; PushH264Packet() expects AVCC.
    /// </summary>
    private static byte[]? ConvertAnnexBToAvcc(byte* data, int size)
    {
        if (data == null || size <= 0)
            return null;

        // Find NAL units by scanning for start codes (0x000001 or 0x00000001)
        var nalUnits = new List<(int offset, int length)>();
        int i = 0;

        while (i < size)
        {
            // Find next start code
            int startCodeLen = 0;
            if (i + 3 <= size && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                startCodeLen = 3;
            }
            else if (i + 4 <= size && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
            {
                startCodeLen = 4;
            }
            else
            {
                i++;
                continue;
            }

            int nalStart = i + startCodeLen;

            // Find end of this NAL (next start code or end of data)
            int nalEnd = size;
            for (int j = nalStart + 1; j < size - 2; j++)
            {
                if (data[j] == 0 && data[j + 1] == 0 &&
                    (data[j + 2] == 1 || (j + 3 < size && data[j + 2] == 0 && data[j + 3] == 1)))
                {
                    nalEnd = j;
                    break;
                }
            }

            int nalLength = nalEnd - nalStart;
            if (nalLength > 0)
            {
                nalUnits.Add((nalStart, nalLength));
            }

            i = nalEnd;
        }

        if (nalUnits.Count == 0)
            return null;

        // Calculate total AVCC size: 4 bytes length prefix per NAL + NAL data
        int totalSize = 0;
        foreach (var (_, length) in nalUnits)
            totalSize += 4 + length;

        var avcc = new byte[totalSize];
        int offset = 0;

        foreach (var (nalOffset, nalLength) in nalUnits)
        {
            // Filter out SPS/PPS NAL units - they're in extradata with GLOBAL_HEADER
            byte nalType = (byte)(data[nalOffset] & 0x1F);
            if (nalType == 7 || nalType == 8) // SPS or PPS
                continue;

            // Write 4-byte big-endian length
            avcc[offset++] = (byte)((nalLength >> 24) & 0xFF);
            avcc[offset++] = (byte)((nalLength >> 16) & 0xFF);
            avcc[offset++] = (byte)((nalLength >> 8) & 0xFF);
            avcc[offset++] = (byte)(nalLength & 0xFF);

            // Copy NAL data
            Marshal.Copy((IntPtr)(data + nalOffset), avcc, offset, nalLength);
            offset += nalLength;
        }

        if (offset == 0)
            return null;

        // Trim if SPS/PPS were filtered out
        if (offset < totalSize)
        {
            var trimmed = new byte[offset];
            Array.Copy(avcc, trimmed, offset);
            return trimmed;
        }

        return avcc;
    }

    /// <summary>
    /// Check if the current encoder should be considered failed and trigger fallback.
    /// Called on every encode failure path after encoder is initialized.
    /// </summary>
    private void TriggerEncoderFallback(string reason)
    {
        _framesSinceEncoderInit++;
        if (_framesSinceEncoderInit >= 5 && ActiveEncoderName != null)
        {
            Logger.Log($"FfmpegEncoder: {ActiveEncoderName} failed after {_framesSinceEncoderInit} frames ({reason}), trying next encoder");
            _failedEncoders.Add(ActiveEncoderName);
            CleanupEncoder();
            _initialized = false;
            Extradata = null;
            ActiveEncoderName = null;
            _firstFrameTime = DateTime.MinValue;
        }
    }

    private void CleanupDecoder()
    {
        if (_decoderPacket != null)
        {
            fixed (AVPacket** pkt = &_decoderPacket)
                ffmpeg.av_packet_free(pkt);
            _decoderPacket = null;
        }

        if (_decodedFrame != null)
        {
            fixed (AVFrame** frame = &_decodedFrame)
                ffmpeg.av_frame_free(frame);
            _decodedFrame = null;
        }

        if (_decoderCtx != null)
        {
            fixed (AVCodecContext** ctx = &_decoderCtx)
                ffmpeg.avcodec_free_context(ctx);
            _decoderCtx = null;
        }
    }

    private void CleanupEncoder()
    {
        if (_encoderPacket != null)
        {
            fixed (AVPacket** pkt = &_encoderPacket)
                ffmpeg.av_packet_free(pkt);
            _encoderPacket = null;
        }

        if (_scaledFrame != null)
        {
            fixed (AVFrame** frame = &_scaledFrame)
                ffmpeg.av_frame_free(frame);
            _scaledFrame = null;
        }

        if (_scaledBuffer != null)
        {
            ffmpeg.av_free(_scaledBuffer);
            _scaledBuffer = null;
        }

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        if (_encoderCtx != null)
        {
            fixed (AVCodecContext** ctx = &_encoderCtx)
                ffmpeg.avcodec_free_context(ctx);
            _encoderCtx = null;
        }

        if (_hwDeviceCtx != null)
        {
            var hwCtx = _hwDeviceCtx;
            ffmpeg.av_buffer_unref(&hwCtx);
            _hwDeviceCtx = null;
        }

        _needsHwUpload = false;
        _initialized = false;
        Extradata = null;
        ActiveEncoderName = null;
        IsHardwareEncoder = false;
        _firstFrameTime = DateTime.MinValue;
        _frameCount = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_encodeLock)
        {
            CleanupEncoder();
            CleanupDecoder();
        }

        GC.SuppressFinalize(this);
    }

    ~FfmpegEncoder()
    {
        Dispose();
    }
}
