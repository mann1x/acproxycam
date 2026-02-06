// PrinterStatus.cs - Runtime status for printers

namespace ACProxyCam.Models;

/// <summary>
/// Runtime status of a printer (not persisted).
/// </summary>
public class PrinterStatus
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public int MjpegPort { get; set; }
    public string DeviceType { get; set; } = "";
    public PrinterState State { get; set; } = PrinterState.Stopped;
    public int ConnectedClients { get; set; }
    public int H264WebSocketClients { get; set; }
    public int HlsClients { get; set; }
    public int FlvClients { get; set; }
    public bool HlsReady { get; set; }
    public bool IsPaused { get; set; }

    // Performance settings
    public int CpuAffinity { get; set; } = -1;
    public int IncomingH264Fps { get; set; }  // Measured incoming H.264 stream FPS
    public int DeclaredH264Fps { get; set; }  // Declared FPS from stream metadata (may differ from actual)
    public int JpegQuality { get; set; }      // MJPEG encoding quality (from config)

    // Video source mode
    public string VideoSource { get; set; } = "h264";      // "h264" or "mjpeg"
    public string H264StreamerEncoderType { get; set; } = "";  // "gkcam", "rkmpi", "rkmpi-yuyv"

    // Streamer enable flags (from config)
    public bool H264StreamerEnabled { get; set; }
    public bool HlsEnabled { get; set; }
    public bool LlHlsEnabled { get; set; }
    public bool MjpegStreamerEnabled { get; set; }
    public bool FlvEnabled { get; set; }

    // Detailed status
    public bool IsOnline { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public DateTime? LastSeenOnline { get; set; }
    public DateTime? NextRetryAt { get; set; }

    // Connection details
    public SshStatus SshStatus { get; set; } = new();
    public MqttStatus MqttStatus { get; set; } = new();
    public StreamStatus StreamStatus { get; set; } = new();

    // Printer state from MQTT (e.g., "free", "printing", "paused")
    public string? PrinterMqttState { get; set; }

    // Camera LED status
    public LedStatus? CameraLed { get; set; }

    // Obico status (local server)
    public ObicoStatus ObicoStatus { get; set; } = new();

    // Obico Cloud status (app.obico.io)
    public ObicoStatus ObicoCloudStatus { get; set; } = new();
}

public enum PrinterState
{
    Stopped,
    Disabled,
    Initializing,
    Connecting,
    Running,
    Paused,
    Failed,
    Retrying
}

public class SshStatus
{
    public bool Connected { get; set; }
    public bool CredentialsRetrieved { get; set; }
    public string? Error { get; set; }
    public DateTime? LastAttempt { get; set; }
}

public class MqttStatus
{
    public bool Connected { get; set; }
    public bool DeviceIdDetected { get; set; }
    public bool ModelCodeDetected { get; set; }
    public bool CameraStarted { get; set; }
    public string? DetectedDeviceId { get; set; }
    public string? DetectedModelCode { get; set; }
    public string? Error { get; set; }
    public DateTime? LastAttempt { get; set; }
}

public class StreamStatus
{
    public bool Connected { get; set; }
    public int FramesDecoded { get; set; }
    public int FramesSent { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? DecoderStatus { get; set; }
    public string? Error { get; set; }
    public DateTime? LastFrameAt { get; set; }
}

/// <summary>
/// Camera LED status.
/// </summary>
public class LedStatus
{
    /// <summary>
    /// LED type (2 = camera LED).
    /// </summary>
    public int Type { get; set; } = 2;

    /// <summary>
    /// LED state: true = on, false = off.
    /// </summary>
    public bool IsOn { get; set; }

    /// <summary>
    /// LED brightness (0-100).
    /// </summary>
    public int Brightness { get; set; }
}

/// <summary>
/// Obico integration status.
/// </summary>
public class ObicoStatus
{
    /// <summary>
    /// Whether Obico integration is enabled for this printer.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Whether the printer is linked to Obico server.
    /// </summary>
    public bool IsLinked { get; set; }

    /// <summary>
    /// Current Obico client state.
    /// </summary>
    public string State { get; set; } = "Stopped";

    /// <summary>
    /// Whether connected to Obico server WebSocket.
    /// </summary>
    public bool ServerConnected { get; set; }

    /// <summary>
    /// Whether connected to Moonraker API.
    /// </summary>
    public bool MoonrakerConnected { get; set; }

    /// <summary>
    /// Obico account tier (Pro or Free).
    /// </summary>
    public bool IsPro { get; set; }

    /// <summary>
    /// Target FPS for snapshot uploads.
    /// </summary>
    public int TargetFps { get; set; }

    /// <summary>
    /// Whether snapshots are enabled.
    /// </summary>
    public bool SnapshotsEnabled { get; set; }

    /// <summary>
    /// Server URL (displayed for local instances, null for cloud).
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// Printer name as shown in Obico dashboard.
    /// </summary>
    public string? ObicoName { get; set; }

    /// <summary>
    /// Last error message if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Whether the last error was an HTTP error (403, 404, 500, etc.)
    /// HTTP errors indicate real problems vs transient network issues.
    /// </summary>
    public bool IsHttpError { get; set; }

    /// <summary>
    /// Whether connected to Janus WebRTC server.
    /// </summary>
    public bool JanusConnected { get; set; }

    /// <summary>
    /// Janus server address (effective, resolved from config).
    /// </summary>
    public string? JanusServer { get; set; }

    /// <summary>
    /// Whether Janus is enabled (not disabled in config).
    /// </summary>
    public bool JanusEnabled { get; set; }

    /// <summary>
    /// Whether Janus is actively streaming (WebRTC viewer connected).
    /// </summary>
    public bool JanusStreaming { get; set; }
}
