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
    public PrinterState State { get; set; } = PrinterState.Stopped;
    public int ConnectedClients { get; set; }
    public bool IsPaused { get; set; }

    // Detailed status
    public bool IsOnline { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSeenOnline { get; set; }
    public DateTime? NextRetryAt { get; set; }

    // Connection details
    public SshStatus SshStatus { get; set; } = new();
    public MqttStatus MqttStatus { get; set; } = new();
    public StreamStatus StreamStatus { get; set; } = new();
}

public enum PrinterState
{
    Stopped,
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
