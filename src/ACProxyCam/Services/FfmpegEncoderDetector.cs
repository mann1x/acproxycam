// FfmpegEncoderDetector.cs - Detect available H.264 encoders at runtime

using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using ACProxyCam.Daemon;

namespace ACProxyCam.Services;

/// <summary>
/// Information about a detected H.264 encoder.
/// </summary>
public class EncoderInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsHardware { get; set; }
    public bool IsVerified { get; set; }
}

/// <summary>
/// Detects available H.264 encoders using FFmpeg at runtime.
/// Checks for hardware encoders first, falls back to software.
/// </summary>
public static unsafe class FfmpegEncoderDetector
{
    /// <summary>
    /// Encoder candidates in priority order.
    /// </summary>
    private static readonly (string Name, string Description, bool IsHardware)[] EncoderCandidates =
    {
        ("h264_v4l2m2m", "V4L2 mem2mem H.264 encoder (Raspberry Pi)", true),
        ("h264_vaapi", "VA-API H.264 encoder (Intel/AMD GPU)", true),
        ("h264_nvenc", "NVENC H.264 encoder (NVIDIA GPU)", true),
        ("h264_qsv", "QuickSync H.264 encoder (Intel)", true),
        ("libx264", "H.264 software encoder", false),
    };

    /// <summary>
    /// Detect all available H.264 encoders on the system.
    /// For each candidate, checks if the encoder exists and optionally verifies it can open.
    /// </summary>
    /// <param name="verify">If true, attempt to open each encoder to verify it works.</param>
    /// <returns>List of available encoders, ordered by priority.</returns>
    public static List<EncoderInfo> DetectEncoders(bool verify = true)
    {
        var results = new List<EncoderInfo>();

        // Ensure FFmpeg is initialized
        FfmpegDecoder.Initialize();

        foreach (var (name, description, isHardware) in EncoderCandidates)
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null)
                continue;

            var info = new EncoderInfo
            {
                Name = name,
                Description = description,
                IsHardware = isHardware,
                IsVerified = false
            };

            if (verify)
            {
                // VAAPI needs special device check
                if (name == "h264_vaapi")
                {
                    info.IsVerified = VerifyVaapiEncoder(codec);
                }
                else
                {
                    info.IsVerified = VerifyEncoder(codec, name);
                }
            }
            else
            {
                // If not verifying, assume found = available
                info.IsVerified = true;
            }

            if (info.IsVerified)
            {
                results.Add(info);
                Logger.Debug($"Encoder detected: {name} ({description})");
            }
            else
            {
                Logger.Debug($"Encoder found but failed verification: {name}");
            }
        }

        return results;
    }

    /// <summary>
    /// Select the best available encoder.
    /// Returns the first verified encoder (priority order: HW first, then SW).
    /// </summary>
    /// <param name="preferredEncoder">Preferred encoder name, or "auto" for automatic selection.</param>
    /// <returns>Best encoder info, or null if none available.</returns>
    public static EncoderInfo? SelectBestEncoder(string preferredEncoder = "auto")
    {
        var encoders = DetectEncoders();

        if (encoders.Count == 0)
            return null;

        // If a specific encoder is requested, try to find it
        if (!string.IsNullOrEmpty(preferredEncoder) && preferredEncoder != "auto")
        {
            var preferred = encoders.FirstOrDefault(e =>
                e.Name.Equals(preferredEncoder, StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
                return preferred;

            Logger.Log($"Preferred encoder '{preferredEncoder}' not available, using auto-detection");
        }

        // Auto: return first verified (already in priority order)
        return encoders.FirstOrDefault();
    }

    /// <summary>
    /// Verify an encoder by attempting to open it with minimal parameters.
    /// </summary>
    private static bool VerifyEncoder(AVCodec* codec, string name)
    {
        AVCodecContext* ctx = null;
        try
        {
            ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (ctx == null)
                return false;

            ctx->width = 640;
            ctx->height = 480;
            ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            ctx->time_base = new AVRational { num = 1, den = 30 };
            ctx->framerate = new AVRational { num = 30, den = 1 };
            ctx->bit_rate = 500_000;
            ctx->max_b_frames = 0;
            ctx->gop_size = 30;

            // libx264 needs preset to open quickly
            if (name == "libx264")
            {
                AVDictionary* opts = null;
                ffmpeg.av_dict_set(&opts, "preset", "ultrafast", 0);
                ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
                int ret = ffmpeg.avcodec_open2(ctx, codec, &opts);
                ffmpeg.av_dict_free(&opts);
                return ret >= 0;
            }

            // v4l2m2m accepts CPU frames directly
            if (name == "h264_v4l2m2m")
            {
                ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                int ret = ffmpeg.avcodec_open2(ctx, codec, null);
                return ret >= 0;
            }

            // nvenc / qsv: try to open with default settings
            int result = ffmpeg.avcodec_open2(ctx, codec, null);
            return result >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (ctx != null)
            {
                var tmp = ctx;
                ffmpeg.avcodec_free_context(&tmp);
            }
        }
    }

    /// <summary>
    /// Verify VAAPI encoder by checking device and attempting to create hw context.
    /// </summary>
    private static bool VerifyVaapiEncoder(AVCodec* codec)
    {
        // Check for render device
        const string vaapiDevice = "/dev/dri/renderD128";
        if (!File.Exists(vaapiDevice))
        {
            Logger.Debug($"VAAPI: device {vaapiDevice} not found");
            return false;
        }

        AVBufferRef* hwDeviceCtx = null;
        try
        {
            // Try creating a hardware device context
            int ret = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
                vaapiDevice, null, 0);
            if (ret < 0)
            {
                Logger.Debug($"VAAPI: failed to create hw device context (error {ret})");
                return false;
            }

            // Try opening the encoder with hw context
            AVCodecContext* ctx = null;
            try
            {
                ctx = ffmpeg.avcodec_alloc_context3(codec);
                if (ctx == null)
                    return false;

                ctx->width = 640;
                ctx->height = 480;
                ctx->time_base = new AVRational { num = 1, den = 30 };
                ctx->framerate = new AVRational { num = 30, den = 1 };
                ctx->bit_rate = 500_000;
                ctx->max_b_frames = 0;
                ctx->gop_size = 30;
                ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_VAAPI;

                // Set up hardware frames context
                ctx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);

                var hwFramesRef = ffmpeg.av_hwframe_ctx_alloc(hwDeviceCtx);
                if (hwFramesRef == null)
                    return false;

                var hwFramesCtx = (AVHWFramesContext*)hwFramesRef->data;
                hwFramesCtx->format = AVPixelFormat.AV_PIX_FMT_VAAPI;
                hwFramesCtx->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
                hwFramesCtx->width = 640;
                hwFramesCtx->height = 480;
                hwFramesCtx->initial_pool_size = 4;

                ret = ffmpeg.av_hwframe_ctx_init(hwFramesRef);
                if (ret < 0)
                {
                    ffmpeg.av_buffer_unref(&hwFramesRef);
                    return false;
                }

                ctx->hw_frames_ctx = hwFramesRef;

                ret = ffmpeg.avcodec_open2(ctx, codec, null);
                return ret >= 0;
            }
            finally
            {
                if (ctx != null)
                {
                    var tmp = ctx;
                    ffmpeg.avcodec_free_context(&tmp);
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hwDeviceCtx != null)
                ffmpeg.av_buffer_unref(&hwDeviceCtx);
        }
    }
}
