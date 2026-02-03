// PrinterPreflightResult.cs - Model for printer preflight check results

namespace ACProxyCam.Models;

/// <summary>
/// Result of a printer preflight check.
/// Contains information about available endpoints and recommendations.
/// </summary>
public class PrinterPreflightResult
{
    /// <summary>
    /// Whether the native H.264 endpoint (:18088/flv) is reachable.
    /// </summary>
    public bool NativeH264Available { get; set; }

    /// <summary>
    /// Whether h264-streamer was detected (control endpoint responded).
    /// </summary>
    public bool H264StreamerDetected { get; set; }

    /// <summary>
    /// Port where h264-streamer control endpoint was found.
    /// </summary>
    public int ControlPort { get; set; }

    /// <summary>
    /// Configuration from h264-streamer's /api/config endpoint.
    /// Null if h264-streamer was not detected.
    /// </summary>
    public H264StreamerConfig? StreamerConfig { get; set; }

    /// <summary>
    /// Whether MJPEG stream endpoint is available.
    /// </summary>
    public bool MjpegStreamAvailable { get; set; }

    /// <summary>
    /// Whether snapshot endpoint is available.
    /// </summary>
    public bool SnapshotAvailable { get; set; }

    /// <summary>
    /// Port where streaming endpoints are available.
    /// </summary>
    public int StreamingPort { get; set; }

    /// <summary>
    /// Recommended video source based on detection results.
    /// Values: "h264" or "mjpeg"
    /// </summary>
    public string RecommendedVideoSource { get; set; } = "h264";

    /// <summary>
    /// Human-readable reason for the recommendation.
    /// </summary>
    public string RecommendationReason { get; set; } = "";

    /// <summary>
    /// Any errors encountered during preflight.
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Whether the preflight check completed successfully.
    /// </summary>
    public bool Success => Errors.Count == 0;

    /// <summary>
    /// Whether using MJPEG source would be suboptimal given the encoder type.
    /// True when h264-streamer has HW H.264 (rkmpi-yuyv, gkcam) but user wants MJPEG.
    /// </summary>
    public bool MjpegIsSuboptimal =>
        H264StreamerDetected &&
        StreamerConfig != null &&
        StreamerConfig.SupportsHardwareH264 &&
        RecommendedVideoSource == "mjpeg";

    /// <summary>
    /// Whether using H.264 source would be suboptimal given the encoder type.
    /// True when h264-streamer has HW MJPEG (rkmpi) but user wants H.264.
    /// </summary>
    public bool H264IsSuboptimal =>
        H264StreamerDetected &&
        StreamerConfig != null &&
        StreamerConfig.IsOptimizedForMjpeg &&
        RecommendedVideoSource == "h264";
}
