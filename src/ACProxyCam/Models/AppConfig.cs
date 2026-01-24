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

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

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
    /// Per-printer Obico integration settings.
    /// </summary>
    [JsonPropertyName("obico")]
    public PrinterObicoConfig Obico { get; set; } = new();

    /// <summary>
    /// Detected firmware information (auto-populated via SSH).
    /// </summary>
    [JsonPropertyName("firmware")]
    public FirmwareInfo Firmware { get; set; } = new();
}
