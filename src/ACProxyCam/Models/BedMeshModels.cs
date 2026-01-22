// BedMeshModels.cs - Data structures for BedMesh calibration feature

using System.Text.Json.Serialization;

namespace ACProxyCam.Models;

/// <summary>
/// Status of a BedMesh calibration session.
/// </summary>
public enum CalibrationStatus
{
    Running,
    Success,
    Failed
}

/// <summary>
/// Represents an active or completed BedMesh calibration session.
/// </summary>
public class BedMeshSession
{
    public string DeviceId { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string? DeviceType { get; set; }
    /// <summary>
    /// Optional user-provided name (e.g., build plate identifier).
    /// </summary>
    public string? Name { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int HeatSoakMinutes { get; set; }
    public CalibrationStatus Status { get; set; } = CalibrationStatus.Running;
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Current step being executed (for display during calibration).
    /// </summary>
    [JsonIgnore]
    public string? CurrentStep { get; set; }

    /// <summary>
    /// The mesh data (populated after successful completion).
    /// </summary>
    public MeshData? MeshData { get; set; }

    /// <summary>
    /// Calculate duration of the calibration.
    /// </summary>
    [JsonIgnore]
    public TimeSpan Duration => (FinishedUtc ?? DateTime.UtcNow) - StartedUtc;

    /// <summary>
    /// Format duration as a human-readable string.
    /// </summary>
    [JsonIgnore]
    public string DurationFormatted
    {
        get
        {
            var d = Duration;
            if (d.TotalHours >= 1)
                return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";
            if (d.TotalMinutes >= 1)
                return $"{d.Minutes}m {d.Seconds}s";
            return $"{d.Seconds}s";
        }
    }
}

/// <summary>
/// Represents bed mesh data from printer_mutable.cfg.
/// </summary>
public class MeshData
{
    public string Algorithm { get; set; } = "bicubic";
    public double MinX { get; set; }
    public double MaxX { get; set; }
    public double MinY { get; set; }
    public double MaxY { get; set; }
    public int XCount { get; set; }
    public int YCount { get; set; }

    /// <summary>
    /// Mesh interpolation tension (from printer config).
    /// </summary>
    public double? Tension { get; set; }

    /// <summary>
    /// Mesh X points per segment for interpolation.
    /// </summary>
    public int? MeshXPps { get; set; }

    /// <summary>
    /// Mesh Y points per segment for interpolation.
    /// </summary>
    public int? MeshYPps { get; set; }

    /// <summary>
    /// Probe count X (from [bed_mesh] probe_count in printer.custom.cfg).
    /// </summary>
    public int? ProbeCountX { get; set; }

    /// <summary>
    /// Probe count Y (from [bed_mesh] probe_count in printer.custom.cfg).
    /// </summary>
    public int? ProbeCountY { get; set; }

    /// <summary>
    /// Probe Z offset at time of calibration.
    /// </summary>
    public double? ZOffset { get; set; }

    /// <summary>
    /// Nozzle diameter at time of calibration.
    /// </summary>
    public double? NozzleDiameter { get; set; }

    /// <summary>
    /// Nozzle material at time of calibration.
    /// </summary>
    public string? NozzleMaterial { get; set; }

    /// <summary>
    /// 2D array of Z values [row][col].
    /// </summary>
    public double[][] Points { get; set; } = Array.Empty<double[]>();

    /// <summary>
    /// Statistics calculated from the mesh points.
    /// </summary>
    public MeshStats? Stats { get; set; }
}

/// <summary>
/// Statistical analysis of mesh data.
/// </summary>
public class MeshStats
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Range { get; set; }
    public double Mean { get; set; }
    public double StandardDeviation { get; set; }

    /// <summary>
    /// X coordinate of the minimum Z value.
    /// </summary>
    public double MinAtX { get; set; }

    /// <summary>
    /// Y coordinate of the minimum Z value.
    /// </summary>
    public double MinAtY { get; set; }

    /// <summary>
    /// X coordinate of the maximum Z value.
    /// </summary>
    public double MaxAtX { get; set; }

    /// <summary>
    /// Y coordinate of the maximum Z value.
    /// </summary>
    public double MaxAtY { get; set; }

    /// <summary>
    /// Calculate statistics from mesh data.
    /// </summary>
    public static MeshStats Calculate(MeshData meshData)
    {
        var points = meshData.Points;
        var allPoints = points.SelectMany(row => row).ToList();
        if (allPoints.Count == 0)
            return new MeshStats();

        var min = allPoints.Min();
        var max = allPoints.Max();
        var mean = allPoints.Average();

        var sumSquaredDiffs = allPoints.Sum(p => Math.Pow(p - mean, 2));
        var stdDev = Math.Sqrt(sumSquaredDiffs / allPoints.Count);

        // Find coordinates for min and max values
        // Use probe grid coordinates if available (actual probe positions),
        // otherwise fall back to interpolated mesh grid
        double minAtX = 0, minAtY = 0, maxAtX = 0, maxAtY = 0;

        // Get the actual probe count (physical probing positions) or mesh count (interpolated)
        var probeCountX = meshData.ProbeCountX ?? meshData.XCount;
        var probeCountY = meshData.ProbeCountY ?? meshData.YCount;

        // Calculate step size based on mesh dimensions
        var xStep = meshData.XCount > 1 ? (meshData.MaxX - meshData.MinX) / (meshData.XCount - 1) : 0;
        var yStep = meshData.YCount > 1 ? (meshData.MaxY - meshData.MinY) / (meshData.YCount - 1) : 0;

        // Calculate probe step size for coordinate mapping
        var probeXStep = probeCountX > 1 ? (meshData.MaxX - meshData.MinX) / (probeCountX - 1) : 0;
        var probeYStep = probeCountY > 1 ? (meshData.MaxY - meshData.MinY) / (probeCountY - 1) : 0;

        for (int row = 0; row < points.Length; row++)
        {
            for (int col = 0; col < points[row].Length; col++)
            {
                var z = points[row][col];

                // Map mesh grid indices to probe grid coordinates
                // Mesh coordinate in the interpolated grid
                var meshX = meshData.MinX + col * xStep;
                var meshY = meshData.MinY + row * yStep;

                // Find nearest probe position
                double x, y;
                if (meshData.ProbeCountX.HasValue && meshData.ProbeCountY.HasValue && probeXStep > 0 && probeYStep > 0)
                {
                    // Snap to nearest probe point
                    var probeColIndex = Math.Round((meshX - meshData.MinX) / probeXStep);
                    var probeRowIndex = Math.Round((meshY - meshData.MinY) / probeYStep);
                    x = meshData.MinX + probeColIndex * probeXStep;
                    y = meshData.MinY + probeRowIndex * probeYStep;
                }
                else
                {
                    // Use mesh coordinates directly (no probe data)
                    x = meshX;
                    y = meshY;
                }

                if (z == min)
                {
                    minAtX = x;
                    minAtY = y;
                }
                if (z == max)
                {
                    maxAtX = x;
                    maxAtY = y;
                }
            }
        }

        return new MeshStats
        {
            Min = min,
            Max = max,
            Range = max - min,
            Mean = mean,
            StandardDeviation = stdDev,
            MinAtX = minAtX,
            MinAtY = minAtY,
            MaxAtX = maxAtX,
            MaxAtY = maxAtY
        };
    }

    /// <summary>
    /// Format a value in millimeters (matches Mainsail format).
    /// </summary>
    public static string FormatMm(double value) => $"{value:F3} mm";

    /// <summary>
    /// Format a coordinate pair (one decimal to match Mainsail).
    /// </summary>
    public static string FormatCoord(double x, double y) => $"{x:F1}, {y:F1}";
}

/// <summary>
/// Running session file stored at /etc/acproxycam/sessions/{deviceId}.running.json
/// </summary>
public class RunningSession
{
    public string DeviceId { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string? DeviceType { get; set; }
    /// <summary>
    /// Optional user-provided name (e.g., build plate identifier).
    /// </summary>
    public string? Name { get; set; }
    public string PrinterIp { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public int HeatSoakMinutes { get; set; }
}

/// <summary>
/// Summary of sessions for display in the UI.
/// </summary>
public class BedMeshSessionSummary
{
    public int ActiveCount { get; set; }
    public int CalibrationCount { get; set; }
    public int AnalysisCount { get; set; }
    public List<BedMeshSession> ActiveSessions { get; set; } = new();
    public List<BedMeshSessionInfo> Calibrations { get; set; } = new();
    public List<BedMeshSessionInfo> Analyses { get; set; } = new();
}

/// <summary>
/// Brief info about a saved session for list display.
/// </summary>
public class BedMeshSessionInfo
{
    public string FileName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string? DeviceType { get; set; }
    /// <summary>
    /// Optional user-provided name (e.g., build plate identifier).
    /// </summary>
    public string? Name { get; set; }
    public DateTime Timestamp { get; set; }
    public CalibrationStatus Status { get; set; }
    public double? MeshRange { get; set; }
}
