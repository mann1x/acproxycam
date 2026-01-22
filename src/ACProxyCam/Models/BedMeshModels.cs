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
    /// True if this is an analysis session (multiple calibrations).
    /// </summary>
    public bool IsAnalysis { get; set; }

    /// <summary>
    /// Current step being executed (for display during calibration).
    /// Not persisted to file but needed for IPC.
    /// </summary>
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

    /// <summary>
    /// True if this is an analysis session (multiple calibrations).
    /// </summary>
    public bool IsAnalysis { get; set; }

    /// <summary>
    /// Number of calibration cycles for analysis mode.
    /// </summary>
    public int CalibrationCount { get; set; }

    /// <summary>
    /// Current calibration number (1-based) for analysis mode progress tracking.
    /// </summary>
    public int CurrentCalibration { get; set; }
}

/// <summary>
/// Represents a completed analysis session with multiple calibration runs.
/// </summary>
public class AnalysisSession
{
    public string DeviceId { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string? DeviceType { get; set; }
    public string? Name { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public int HeatSoakMinutes { get; set; }
    public CalibrationStatus Status { get; set; } = CalibrationStatus.Running;
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Number of calibration cycles requested.
    /// </summary>
    public int CalibrationCount { get; set; }

    /// <summary>
    /// Current calibration number (1-based, for display during running).
    /// </summary>
    [JsonIgnore]
    public int CurrentCalibration { get; set; }

    /// <summary>
    /// Current step description (for display during running).
    /// </summary>
    [JsonIgnore]
    public string? CurrentStep { get; set; }

    /// <summary>
    /// Individual calibration results (mesh data from each run).
    /// </summary>
    public List<MeshData> Calibrations { get; set; } = new();

    /// <summary>
    /// Average mesh computed from all calibrations.
    /// </summary>
    public MeshData? AverageMesh { get; set; }

    /// <summary>
    /// Cross-calibration analysis statistics.
    /// </summary>
    public AnalysisStats? AnalysisStats { get; set; }

    [JsonIgnore]
    public TimeSpan Duration => (FinishedUtc ?? DateTime.UtcNow) - StartedUtc;

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
/// Cross-calibration analysis statistics using IQR-based outlier detection.
/// </summary>
public class AnalysisStats
{
    /// <summary>
    /// Range statistics across all calibrations.
    /// </summary>
    public double AverageRange { get; set; }
    public double MinRange { get; set; }
    public double MaxRange { get; set; }

    /// <summary>
    /// Total number of outlier readings detected.
    /// </summary>
    public int TotalOutlierCount { get; set; }

    /// <summary>
    /// Outliers grouped by position, sorted by frequency (most frequent first).
    /// </summary>
    public List<OutlierPosition> OutliersByPosition { get; set; } = new();

    /// <summary>
    /// Per-point statistics for detailed analysis.
    /// </summary>
    public List<PointStats> PointStatistics { get; set; } = new();

    /// <summary>
    /// Calculate analysis statistics from multiple calibration meshes.
    /// Uses IQR (Interquartile Range) for outlier detection.
    /// </summary>
    public static AnalysisStats Calculate(List<MeshData> calibrations, MeshData averageMesh)
    {
        if (calibrations.Count == 0)
            return new AnalysisStats();

        var stats = new AnalysisStats();

        // Calculate range statistics
        var ranges = calibrations
            .Where(c => c.Stats != null)
            .Select(c => c.Stats!.Range)
            .ToList();

        if (ranges.Count > 0)
        {
            stats.AverageRange = ranges.Average();
            stats.MinRange = ranges.Min();
            stats.MaxRange = ranges.Max();
        }

        // Get mesh dimensions from first calibration
        var firstMesh = calibrations[0];
        var rows = firstMesh.ProbeCountY ?? firstMesh.YCount;
        var cols = firstMesh.ProbeCountX ?? firstMesh.XCount;

        if (rows == 0 || cols == 0)
            return stats;

        // Calculate per-point statistics and detect outliers
        var outliersByPos = new Dictionary<(int row, int col), List<double>>();

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                // Collect Z values for this point across all calibrations
                var zValues = new List<double>();
                foreach (var mesh in calibrations)
                {
                    if (row < mesh.Points.Length && col < mesh.Points[row].Length)
                    {
                        zValues.Add(mesh.Points[row][col]);
                    }
                }

                if (zValues.Count < 3)
                    continue;

                // Calculate IQR-based statistics
                var sorted = zValues.OrderBy(v => v).ToList();
                var q1Index = (int)Math.Floor((sorted.Count - 1) * 0.25);
                var q3Index = (int)Math.Floor((sorted.Count - 1) * 0.75);
                var q1 = sorted[q1Index];
                var q3 = sorted[q3Index];
                var iqr = q3 - q1;
                var lowerBound = q1 - 1.5 * iqr;
                var upperBound = q3 + 1.5 * iqr;
                var median = sorted[sorted.Count / 2];

                // Detect outliers with minimum threshold for probing error detection
                // A point is only an outlier if:
                // 1. It's outside the IQR bounds, AND
                // 2. Its delta from median is at least 0.030mm (30 microns)
                //
                // Rationale: Strain gauge probes have ~5-8µm repeatability, Klipper considers
                // >25µm as "insufficient accuracy". We use 30µm as the threshold to detect
                // actual probing errors (crashes, missed probes) rather than normal variance.
                const double MinOutlierDelta = 0.030; // 30 microns minimum for probing error detection
                var outliers = zValues
                    .Where(v => (v < lowerBound || v > upperBound) && Math.Abs(v - median) >= MinOutlierDelta)
                    .ToList();

                // Calculate point coordinates
                var xStep = cols > 1 ? (firstMesh.MaxX - firstMesh.MinX) / (cols - 1) : 0;
                var yStep = rows > 1 ? (firstMesh.MaxY - firstMesh.MinY) / (rows - 1) : 0;
                var x = firstMesh.MinX + col * xStep;
                var y = firstMesh.MinY + row * yStep;

                var pointStat = new PointStats
                {
                    Row = row,
                    Col = col,
                    X = x,
                    Y = y,
                    Mean = zValues.Average(),
                    Median = sorted[sorted.Count / 2],
                    Q1 = q1,
                    Q3 = q3,
                    IQR = iqr,
                    Min = sorted.First(),
                    Max = sorted.Last(),
                    OutlierCount = outliers.Count,
                    OutlierValues = outliers
                };

                stats.PointStatistics.Add(pointStat);

                if (outliers.Count > 0)
                {
                    outliersByPos[(row, col)] = outliers;
                }
            }
        }

        // Build outlier summary sorted by average delta (most significant first)
        stats.OutliersByPosition = outliersByPos
            .Select(kvp =>
            {
                var (row, col) = kvp.Key;
                var pointStat = stats.PointStatistics.FirstOrDefault(p => p.Row == row && p.Col == col);
                var median = pointStat?.Median ?? 0;
                var avgDelta = kvp.Value.Count > 0
                    ? kvp.Value.Select(v => Math.Abs(v - median)).Average()
                    : 0;
                return new OutlierPosition
                {
                    Row = row,
                    Col = col,
                    X = pointStat?.X ?? 0,
                    Y = pointStat?.Y ?? 0,
                    Count = kvp.Value.Count,
                    Values = kvp.Value,
                    AverageDelta = avgDelta,
                    Median = median,
                    IQR = pointStat?.IQR ?? 0
                };
            })
            .OrderByDescending(o => o.AverageDelta) // Sort by delta significance, not just count
            .ThenByDescending(o => o.Count)
            .ToList();

        stats.TotalOutlierCount = stats.OutliersByPosition.Sum(o => o.Count);

        return stats;
    }
}

/// <summary>
/// Statistics for a single probe point across multiple calibrations.
/// </summary>
public class PointStats
{
    public int Row { get; set; }
    public int Col { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Mean { get; set; }
    public double Median { get; set; }
    public double Q1 { get; set; }
    public double Q3 { get; set; }
    public double IQR { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public int OutlierCount { get; set; }
    public List<double> OutlierValues { get; set; } = new();
}

/// <summary>
/// Outlier information for a specific probe position.
/// </summary>
public class OutlierPosition
{
    public int Row { get; set; }
    public int Col { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public int Count { get; set; }
    public List<double> Values { get; set; } = new();

    /// <summary>
    /// Average delta from the median value for outliers at this position.
    /// </summary>
    public double AverageDelta { get; set; }

    /// <summary>
    /// The median value at this position (for context).
    /// </summary>
    public double Median { get; set; }

    /// <summary>
    /// The IQR (variance measure) at this position.
    /// </summary>
    public double IQR { get; set; }
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

    /// <summary>
    /// True if this is an analysis session.
    /// </summary>
    public bool IsAnalysis { get; set; }

    /// <summary>
    /// Number of calibrations in an analysis session.
    /// </summary>
    public int? CalibrationCount { get; set; }
}
