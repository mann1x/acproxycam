// AppConfig.cs - Application configuration model

using System.Text.Json.Serialization;

namespace ACProxyCam.Models;

/// <summary>
/// Root configuration for ACProxyCam.
/// Stored at /etc/acproxycam/config.json
/// </summary>
public class AppConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("listenInterfaces")]
    public List<string> ListenInterfaces { get; set; } = new() { "127.0.0.1" };

    [JsonPropertyName("logToFile")]
    public bool LogToFile { get; set; } = true;

    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Global Obico integration settings.
    /// </summary>
    [JsonPropertyName("obico")]
    public GlobalObicoConfig Obico { get; set; } = new();

    [JsonPropertyName("printers")]
    public List<PrinterConfig> Printers { get; set; } = new();
}

/// <summary>
/// Configuration for a single printer.
/// </summary>
public class PrinterConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Whether this printer is enabled. When false, the printer thread is not started.
    /// Useful for long maintenance periods without removing the printer configuration.
    /// Default: true.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    /// <summary>
    /// HTTP server port for all streaming endpoints (snapshot, status, LED control, MJPEG, H.264, HLS).
    /// Note: Property name kept as "mjpegPort" for backwards compatibility with existing configs.
    /// </summary>
    [JsonPropertyName("mjpegPort")]
    public int MjpegPort { get; set; } = 8080;

    [JsonPropertyName("sshPort")]
    public int SshPort { get; set; } = 22;

    [JsonPropertyName("sshUser")]
    public string SshUser { get; set; } = "root";

    [JsonPropertyName("sshPassword")]
    public string SshPassword { get; set; } = ""; // Encrypted

    [JsonPropertyName("mqttPort")]
    public int MqttPort { get; set; } = 9883;

    [JsonPropertyName("mqttUsername")]
    public string MqttUsername { get; set; } = ""; // Encrypted

    [JsonPropertyName("mqttPassword")]
    public string MqttPassword { get; set; } = ""; // Encrypted

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("modelCode")]
    public string ModelCode { get; set; } = "";

    /// <summary>
    /// Printer device type (e.g., "K3", "KS1", "K3M"). Retrieved from printer's api.cfg.
    /// </summary>
    [JsonPropertyName("deviceType")]
    public string DeviceType { get; set; } = "";

    /// <summary>
    /// Maximum frames per second when clients are connected. Default: 0 (unlimited, use source FPS).
    /// </summary>
    [JsonPropertyName("maxFps")]
    public int MaxFps { get; set; } = 0;

    /// <summary>
    /// Frames per second when no clients connected (for snapshots). Default: 1. Set to 0 to disable idle encoding.
    /// </summary>
    [JsonPropertyName("idleFps")]
    public int IdleFps { get; set; } = 1;

    /// <summary>
    /// JPEG encoding quality (1-100). Default: 80.
    /// </summary>
    [JsonPropertyName("jpegQuality")]
    public int JpegQuality { get; set; } = 80;

    /// <summary>
    /// Send stopCapture command via MQTT when stopping/cleaning up.
    /// Default: false (disabled to avoid interfering with other software like slicers).
    /// </summary>
    [JsonPropertyName("sendStopCommand")]
    public bool SendStopCommand { get; set; } = false;

    /// <summary>
    /// Automatically enable LAN mode on the printer if MQTT connection fails.
    /// Uses the printer's local API on port 18086 via SSH tunnel.
    /// </summary>
    [JsonPropertyName("autoLanMode")]
    public bool AutoLanMode { get; set; } = false;

    /// <summary>
    /// Automatically control camera LED based on printer state.
    /// When enabled, LED turns on when printer is active (printing, etc.) and off after idle timeout.
    /// </summary>
    [JsonPropertyName("ledAutoControl")]
    public bool LedAutoControl { get; set; } = false;

    /// <summary>
    /// Minutes to wait before turning off LED when printer is idle (standby/free).
    /// Default: 20 minutes. Set to 0 to disable auto-off (LED stays on).
    /// </summary>
    [JsonPropertyName("standbyLedTimeoutMinutes")]
    public int StandbyLedTimeoutMinutes { get; set; } = 20;

    /// <summary>
    /// Whether camera proxy is enabled. When false, only Obico integration runs (if enabled).
    /// Default: true for backwards compatibility.
    /// </summary>
    [JsonPropertyName("cameraEnabled")]
    public bool CameraEnabled { get; set; } = true;

    /// <summary>
    /// Video source type: "h264" (default) for native H.264 stream, or "mjpeg" for h264-streamer MJPEG.
    /// When "mjpeg", connects to h264-streamer's /stream endpoint instead of native FLV.
    /// </summary>
    [JsonPropertyName("videoSource")]
    public string VideoSource { get; set; } = "h264";

    /// <summary>
    /// h264-streamer control endpoint port. 0 = not configured/detected.
    /// Used to query h264-streamer configuration via /api/config.
    /// </summary>
    [JsonPropertyName("h264StreamerControlPort")]
    public int H264StreamerControlPort { get; set; } = 0;

    /// <summary>
    /// h264-streamer streaming port (default 8080, obtained from /api/config).
    /// Used when VideoSource = "mjpeg" to construct stream URLs.
    /// </summary>
    [JsonPropertyName("h264StreamerStreamingPort")]
    public int H264StreamerStreamingPort { get; set; } = 8080;

    /// <summary>
    /// h264-streamer encoder type detected via /api/config.
    /// Values: "gkcam", "rkmpi", "rkmpi-yuyv", or empty if not using h264-streamer.
    /// Used to recommend video source: rkmpi = MJPEG preferred, gkcam/rkmpi-yuyv = H.264 preferred.
    /// </summary>
    [JsonPropertyName("h264StreamerEncoderType")]
    public string H264StreamerEncoderType { get; set; } = "";

    /// <summary>
    /// Custom MJPEG stream URL override. When set, used instead of default URL construction.
    /// Default format: http://{Ip}:{H264StreamerStreamingPort}/stream
    /// </summary>
    [JsonPropertyName("mjpegStreamUrl")]
    public string? MjpegStreamUrl { get; set; }

    /// <summary>
    /// Custom snapshot URL override. When set, used instead of default URL construction.
    /// Default format: http://{Ip}:{H264StreamerStreamingPort}/snapshot
    /// </summary>
    [JsonPropertyName("snapshotUrl")]
    public string? SnapshotUrl { get; set; }

    /// <summary>
    /// Enable H.264 WebSocket streaming endpoint (/h264).
    /// Used by Mainsail/Fluidd jmuxer for low-CPU streaming.
    /// Default: true.
    /// </summary>
    [JsonPropertyName("h264StreamerEnabled")]
    public bool H264StreamerEnabled { get; set; } = true;

    /// <summary>
    /// Enable HLS streaming endpoints (/hls/*).
    /// Provides standard HLS streaming for Home Assistant and browsers.
    /// Default: true.
    /// </summary>
    [JsonPropertyName("hlsEnabled")]
    public bool HlsEnabled { get; set; } = true;

    /// <summary>
    /// Enable Low-Latency HLS (LL-HLS) streaming. Reduces latency from ~4-5s to ~1-2s.
    /// Requires HLS v10 compatible player (Safari, hls.js). Falls back gracefully.
    /// Only effective when HlsEnabled is true.
    /// Default: true.
    /// </summary>
    [JsonPropertyName("llHlsEnabled")]
    public bool LlHlsEnabled { get; set; } = true;

    /// <summary>
    /// Enable MJPEG streaming endpoint (/stream).
    /// CPU-intensive as it requires decoding H.264 and encoding JPEG.
    /// Default: false (for new printers; existing configs default to true for backwards compatibility).
    /// </summary>
    [JsonPropertyName("mjpegStreamerEnabled")]
    public bool MjpegStreamerEnabled { get; set; } = true;

    /// <summary>
    /// Duration of LL-HLS partial segments in milliseconds.
    /// Smaller values = lower latency but more requests.
    /// Recommended range: 100-500ms. Default: 200ms (Apple recommendation).
    /// </summary>
    [JsonPropertyName("hlsPartDurationMs")]
    public int HlsPartDurationMs { get; set; } = 200;

    /// <summary>
    /// Interval in seconds to resend camera start command to prevent frame rate throttling.
    /// Anycubic cameras may throttle frame rate after some time without MQTT activity.
    /// Set to 20-30 seconds to attempt maintaining full frame rate. Default: 0 (disabled).
    /// Only sends keepalive when there are active stream consumers (HLS/MJPEG clients).
    /// Note: Effectiveness depends on printer firmware. May not work on all models.
    /// </summary>
    [JsonPropertyName("cameraKeepaliveSeconds")]
    public int CameraKeepaliveSeconds { get; set; } = 0;

    /// <summary>
    /// Enable MJPEG→H.264 encoding via FFmpeg on this server.
    /// When enabled, JPEG frames from MJPEG source are encoded to H.264 locally,
    /// enabling H.264 WebSocket and HLS endpoints even from MJPEG-only sources.
    /// Default: false.
    /// </summary>
    [JsonPropertyName("h264EncodingEnabled")]
    public bool H264EncodingEnabled { get; set; } = false;

    /// <summary>
    /// H.264 encoder to use for MJPEG→H.264 encoding.
    /// "auto" = detect best available (HW first, then SW).
    /// Specific names: "libx264", "h264_vaapi", "h264_v4l2m2m", "h264_nvenc", "h264_qsv".
    /// Default: "auto".
    /// </summary>
    [JsonPropertyName("h264Encoder")]
    public string H264Encoder { get; set; } = "auto";

    /// <summary>
    /// H.264 encoding bitrate in kbps. Default: 1024.
    /// </summary>
    [JsonPropertyName("h264Bitrate")]
    public int H264Bitrate { get; set; } = 1024;

    /// <summary>
    /// H.264 encoding rate control mode: "vbr" (variable) or "cbr" (constant).
    /// VBR allows quality to vary within bitrate budget. CBR maintains constant rate.
    /// Default: "vbr".
    /// </summary>
    [JsonPropertyName("h264RateControl")]
    public string H264RateControl { get; set; } = "vbr";

    /// <summary>
    /// H.264 encoding GOP (Group of Pictures) size - keyframe interval in frames.
    /// Smaller values = more keyframes = faster seek/recovery but higher bitrate.
    /// Default: 30.
    /// </summary>
    [JsonPropertyName("h264GopSize")]
    public int H264GopSize { get; set; } = 30;

    /// <summary>
    /// H.264 encoding preset. Controls speed/quality tradeoff.
    /// For libx264: ultrafast, superfast, veryfast, faster, fast, medium, slow, slower, veryslow.
    /// For NVENC: p1 (fastest) to p7 (slowest).
    /// Default: "medium".
    /// </summary>
    [JsonPropertyName("h264Preset")]
    public string H264Preset { get; set; } = "medium";

    /// <summary>
    /// H.264 encoding profile. Higher profiles = better compression but more CPU.
    /// Values: "baseline", "main", "high".
    /// Default: "main".
    /// </summary>
    [JsonPropertyName("h264Profile")]
    public string H264Profile { get; set; } = "main";

    /// <summary>
    /// Maximum encoding FPS. 0 = match source frame rate (no limit).
    /// Use to limit CPU usage when source provides high frame rate.
    /// Default: 0.
    /// </summary>
    [JsonPropertyName("h264EncodingMaxFps")]
    public int H264EncodingMaxFps { get; set; } = 0;

    /// <summary>
    /// Per-printer Obico integration settings (local self-hosted server).
    /// </summary>
    [JsonPropertyName("obico")]
    public PrinterObicoConfig Obico { get; set; } = new();

    /// <summary>
    /// Obico Cloud integration settings (app.obico.io).
    /// Runs independently and in parallel with local Obico instance.
    /// </summary>
    [JsonPropertyName("obicoCloud")]
    public PrinterObicoConfig ObicoCloud { get; set; } = new()
    {
        ServerUrl = ObicoCloudUrl,
        Enabled = false
    };

    /// <summary>
    /// Static URL for Obico Cloud - cannot be changed.
    /// </summary>
    public const string ObicoCloudUrl = "https://app.obico.io";

    /// <summary>
    /// Detected firmware information (auto-populated via SSH).
    /// </summary>
    [JsonPropertyName("firmware")]
    public FirmwareInfo Firmware { get; set; } = new();
}
