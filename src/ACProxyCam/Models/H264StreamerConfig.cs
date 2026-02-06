// H264StreamerConfig.cs - Model for h264-streamer /api/config response

using System.Text.Json.Serialization;

namespace ACProxyCam.Models;

/// <summary>
/// Configuration returned by h264-streamer's /api/config endpoint.
/// Used for auto-detection and configuration recommendations.
/// </summary>
public class H264StreamerConfig
{
    /// <summary>
    /// Encoder type: "gkcam", "rkmpi", or "rkmpi-yuyv".
    /// - gkcam: Uses native camera H.264, transcodes to MJPEG via ffmpeg
    /// - rkmpi: Hardware MJPEG encoder (best for MJPEG source)
    /// - rkmpi-yuyv: Hardware H.264 + MJPEG encoder (best for H.264 source)
    /// </summary>
    [JsonPropertyName("encoder_type")]
    public string EncoderType { get; set; } = "";

    /// <summary>
    /// Port where streaming endpoints (/stream, /snapshot, /flv) are served.
    /// </summary>
    [JsonPropertyName("streaming_port")]
    public int StreamingPort { get; set; } = 8080;

    /// <summary>
    /// Port where control/API endpoints are served.
    /// </summary>
    [JsonPropertyName("control_port")]
    public int ControlPort { get; set; } = 8081;

    /// <summary>
    /// Whether H.264 encoding/streaming is enabled.
    /// </summary>
    [JsonPropertyName("h264_enabled")]
    public bool H264Enabled { get; set; } = true;

    /// <summary>
    /// H.264 encoding resolution (e.g., "1280x720").
    /// </summary>
    [JsonPropertyName("h264_resolution")]
    public string H264Resolution { get; set; } = "1280x720";

    /// <summary>
    /// H.264 encoding bitrate in kbps.
    /// </summary>
    [JsonPropertyName("h264_bitrate")]
    public int H264Bitrate { get; set; } = 512;

    /// <summary>
    /// Target MJPEG frame rate.
    /// </summary>
    [JsonPropertyName("mjpeg_fps")]
    public int MjpegFps { get; set; } = 10;

    /// <summary>
    /// JPEG encoding quality (1-99).
    /// </summary>
    [JsonPropertyName("jpeg_quality")]
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// Frame skip ratio for CPU management.
    /// </summary>
    [JsonPropertyName("skip_ratio")]
    public int SkipRatio { get; set; } = 4;

    /// <summary>
    /// Whether automatic skip ratio adjustment is enabled.
    /// </summary>
    [JsonPropertyName("auto_skip")]
    public bool AutoSkip { get; set; } = true;

    /// <summary>
    /// Target CPU usage percentage for auto-skip.
    /// </summary>
    [JsonPropertyName("target_cpu")]
    public int TargetCpu { get; set; } = 25;

    /// <summary>
    /// Whether display capture is enabled.
    /// </summary>
    [JsonPropertyName("display_enabled")]
    public bool DisplayEnabled { get; set; } = false;

    /// <summary>
    /// Display capture frame rate.
    /// </summary>
    [JsonPropertyName("display_fps")]
    public int DisplayFps { get; set; } = 5;

    /// <summary>
    /// Whether auto LAN mode is enabled.
    /// </summary>
    [JsonPropertyName("autolanmode")]
    public bool AutoLanMode { get; set; } = true;

    /// <summary>
    /// Camera device path (e.g., "/dev/video0").
    /// </summary>
    [JsonPropertyName("device")]
    public string? Device { get; set; }

    /// <summary>
    /// Camera capture width.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; set; } = 1280;

    /// <summary>
    /// Camera capture height.
    /// </summary>
    [JsonPropertyName("height")]
    public int Height { get; set; } = 720;

    /// <summary>
    /// Operating mode: "go-klipper" or "vanilla-klipper".
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "go-klipper";

    /// <summary>
    /// Whether advanced timelapse is enabled.
    /// </summary>
    [JsonPropertyName("timelapse_enabled")]
    public bool TimelapseEnabled { get; set; } = false;

    /// <summary>
    /// Timelapse mode: "layer" or "hyperlapse".
    /// </summary>
    [JsonPropertyName("timelapse_mode")]
    public string TimelapseMode { get; set; } = "layer";

    /// <summary>
    /// Session ID (timestamp-based, changes on restart).
    /// </summary>
    [JsonPropertyName("session_id")]
    public long SessionId { get; set; }

    /// <summary>
    /// Whether ACProxyCam FLV proxy mode is enabled on h264-streamer.
    /// When true, h264-streamer proxies ACProxyCam's /flv endpoint instead of encoding locally.
    /// </summary>
    [JsonPropertyName("acproxycam_flv_proxy")]
    public bool AcproxycamFlvProxy { get; set; } = false;

    /// <summary>
    /// Check if this encoder type produces hardware H.264.
    /// rkmpi-yuyv and gkcam provide H.264, rkmpi only provides MJPEG.
    /// </summary>
    [JsonIgnore]
    public bool SupportsHardwareH264 => EncoderType == "rkmpi-yuyv" || EncoderType == "gkcam";

    /// <summary>
    /// Check if this encoder is optimized for MJPEG output.
    /// rkmpi encoder produces native MJPEG without transcoding.
    /// </summary>
    [JsonIgnore]
    public bool IsOptimizedForMjpeg => EncoderType == "rkmpi";
}
