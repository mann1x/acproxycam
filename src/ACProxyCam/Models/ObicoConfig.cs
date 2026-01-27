// ObicoConfig.cs - Obico integration configuration models

using System.Text.Json.Serialization;

namespace ACProxyCam.Models;

/// <summary>
/// Global Obico configuration settings.
/// </summary>
public class GlobalObicoConfig
{
    /// <summary>
    /// Port for the discovery server (default: 46793).
    /// Shared across all printers.
    /// </summary>
    [JsonPropertyName("discoveryPort")]
    public int DiscoveryPort { get; set; } = 46793;

    /// <summary>
    /// Default Obico server URL for new printers.
    /// </summary>
    [JsonPropertyName("defaultServerUrl")]
    public string DefaultServerUrl { get; set; } = "https://app.obico.io";
}

/// <summary>
/// Per-printer Obico configuration.
/// </summary>
public class PrinterObicoConfig
{
    /// <summary>
    /// Whether Obico integration is enabled for this printer.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Obico server URL (e.g., "https://app.obico.io" or "http://192.168.1.100:3334").
    /// </summary>
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "https://app.obico.io";

    /// <summary>
    /// Authentication token from Obico server. Encrypted in config file.
    /// </summary>
    [JsonPropertyName("authToken")]
    public string AuthToken { get; set; } = "";

    /// <summary>
    /// Unique device ID for this printer (UUID). Generated once and persisted.
    /// Used for discovery handshake with Obico.
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string ObicoDeviceId { get; set; } = "";

    /// <summary>
    /// Device secret for discovery handshake. Regenerated on each linking attempt.
    /// </summary>
    [JsonPropertyName("deviceSecret")]
    public string DeviceSecret { get; set; } = "";

    /// <summary>
    /// Target FPS for snapshot uploads. Auto-adjusted based on Obico tier.
    /// Free: 5 FPS max, Pro: 25 FPS max.
    /// </summary>
    [JsonPropertyName("targetFps")]
    public int TargetFps { get; set; } = 5;

    /// <summary>
    /// Whether to upload snapshots to Obico. Automatically false if camera is disabled.
    /// </summary>
    [JsonPropertyName("snapshotsEnabled")]
    public bool SnapshotsEnabled { get; set; } = true;

    /// <summary>
    /// Whether this is an Obico Pro account (auto-detected from server).
    /// </summary>
    [JsonPropertyName("isPro")]
    public bool IsPro { get; set; } = false;

    /// <summary>
    /// Printer name as shown in Obico dashboard.
    /// </summary>
    [JsonPropertyName("obicoName")]
    public string ObicoName { get; set; } = "";

    /// <summary>
    /// Printer ID on the Obico server. Used for deletion.
    /// </summary>
    [JsonPropertyName("obicoPrinterId")]
    public int ObicoPrinterId { get; set; } = 0;

    /// <summary>
    /// Janus WebRTC server address for real-time streaming.
    /// If empty, defaults to the host from ServerUrl (for self-hosted Obico with Janus on same server).
    /// Set to "disabled" to disable Janus streaming entirely.
    /// </summary>
    [JsonPropertyName("janusServer")]
    public string JanusServer { get; set; } = "";

    /// <summary>
    /// Streaming mode for WebRTC.
    /// H264 (default): Passthrough H.264 from camera via RTP - better quality, lower CPU.
    /// MJPEG: Base64-encoded JPEG over data channel - fallback if H.264 doesn't work.
    /// </summary>
    [JsonPropertyName("streamMode")]
    public ObicoStreamMode StreamMode { get; set; } = ObicoStreamMode.H264;

    // Read-only status fields (not persisted, but useful for display)

    /// <summary>
    /// Whether the printer is currently linked to Obico.
    /// </summary>
    [JsonIgnore]
    public bool IsLinked => !string.IsNullOrEmpty(AuthToken);
}

/// <summary>
/// Firmware information detected from the printer.
/// </summary>
public class FirmwareInfo
{
    /// <summary>
    /// Type of firmware detected.
    /// </summary>
    [JsonPropertyName("type")]
    public FirmwareType Type { get; set; } = FirmwareType.Unknown;

    /// <summary>
    /// Firmware version string (e.g., "20260105_01-2" for Rinkhals).
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>
    /// Whether Moonraker API is available.
    /// </summary>
    [JsonPropertyName("moonrakerAvailable")]
    public bool MoonrakerAvailable { get; set; } = false;

    /// <summary>
    /// Moonraker API port (default: 7125).
    /// </summary>
    [JsonPropertyName("moonrakerPort")]
    public int MoonrakerPort { get; set; } = 7125;

    /// <summary>
    /// Path to config directory on the printer.
    /// </summary>
    [JsonPropertyName("configPath")]
    public string ConfigPath { get; set; } = "";

    /// <summary>
    /// When the firmware was last detected.
    /// </summary>
    [JsonPropertyName("detectedAt")]
    public DateTime? DetectedAt { get; set; }
}

/// <summary>
/// Type of firmware running on the printer.
/// </summary>
public enum FirmwareType
{
    /// <summary>
    /// Unknown firmware type.
    /// </summary>
    Unknown,

    /// <summary>
    /// Stock Anycubic firmware (no Moonraker).
    /// </summary>
    StockAnycubic,

    /// <summary>
    /// Rinkhals custom firmware (Moonraker available).
    /// </summary>
    Rinkhals
}

/// <summary>
/// Obico session state for resuming after restart.
/// Persisted separately from main config.
/// </summary>
public class ObicoSessionState
{
    /// <summary>
    /// Printer name this session belongs to.
    /// </summary>
    [JsonPropertyName("printerName")]
    public string PrinterName { get; set; } = "";

    /// <summary>
    /// Current print timestamp (Obico session ID).
    /// </summary>
    [JsonPropertyName("currentPrintTs")]
    public long? CurrentPrintTs { get; set; }

    /// <summary>
    /// Current filename being printed.
    /// </summary>
    [JsonPropertyName("currentFilename")]
    public string? CurrentFilename { get; set; }

    /// <summary>
    /// When the print started.
    /// </summary>
    [JsonPropertyName("printStartTime")]
    public DateTime? PrintStartTime { get; set; }

    /// <summary>
    /// Last status update sent to server.
    /// </summary>
    [JsonPropertyName("lastStatusUpdate")]
    public DateTime? LastStatusUpdate { get; set; }
}

/// <summary>
/// WebRTC streaming mode for Obico.
/// </summary>
public enum ObicoStreamMode
{
    /// <summary>
    /// H.264 passthrough via RTP - sends H.264 directly from camera to Janus.
    /// Best quality, lowest CPU usage, recommended for most setups.
    /// </summary>
    H264,

    /// <summary>
    /// MJPEG over WebRTC data channel - base64-encoded JPEG frames.
    /// Fallback option if H.264 doesn't work with your setup.
    /// </summary>
    MJPEG
}
