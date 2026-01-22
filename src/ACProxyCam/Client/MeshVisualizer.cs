// MeshVisualizer.cs - Visual mesh heatmap renderer using Spectre.Console

using ACProxyCam.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace ACProxyCam.Client;

/// <summary>
/// Renders bed mesh data as a colored heatmap using Spectre.Console.
/// Color gradient matches Mainsail: red/orange (high) -> yellow/white -> blue (low)
/// </summary>
public static class MeshVisualizer
{
    // Color gradient stops (from high to low Z values)
    // Matches Mainsail heightmap: red -> orange -> yellow -> white -> light blue -> blue
    private static readonly (double position, byte r, byte g, byte b)[] GradientStops =
    {
        (1.0, 255, 60, 60),    // Red (highest)
        (0.8, 255, 140, 60),   // Orange
        (0.6, 255, 200, 100),  // Yellow-orange
        (0.5, 255, 255, 200),  // Light yellow/white (middle)
        (0.4, 180, 220, 255),  // Light blue
        (0.2, 100, 150, 255),  // Medium blue
        (0.0, 60, 80, 200),    // Deep blue (lowest)
    };

    /// <summary>
    /// Render a calibration session with mesh heatmap and stats in two columns.
    /// </summary>
    public static void RenderCalibrationResult(BedMeshSession session)
    {
        if (session.MeshData == null || session.MeshData.Points.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No mesh data available to visualize.[/]");
            return;
        }

        var mesh = session.MeshData;
        var stats = mesh.Stats;

        // Build the two-column layout using a Grid
        var grid = new Grid();
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().NoWrap().PadLeft(2));

        grid.AddRow(
            RenderMeshPanel(mesh),
            RenderStatsPanel(session, stats)
        );

        AnsiConsole.Write(grid);
    }

    /// <summary>
    /// Render just the mesh heatmap (for simpler display).
    /// </summary>
    public static void RenderMesh(MeshData mesh)
    {
        AnsiConsole.Write(RenderMeshPanel(mesh));
    }

    /// <summary>
    /// Create the mesh heatmap panel with axis labels.
    /// </summary>
    private static Panel RenderMeshPanel(MeshData mesh)
    {
        var points = mesh.Points;
        var pointRows = points.Length;
        var pointCols = pointRows > 0 ? points[0].Length : 0;

        if (pointRows == 0 || pointCols == 0)
            return new Panel("No mesh data").Header("Mesh");

        // Use the probe count for display, not the interpolated points array size
        // Prefer ProbeCountX/Y (actual probe grid) over XCount/YCount (which may be interpolated)
        var rows = mesh.ProbeCountY ?? (mesh.YCount > 0 ? mesh.YCount : pointRows);
        var cols = mesh.ProbeCountX ?? (mesh.XCount > 0 ? mesh.XCount : pointCols);

        // Calculate min/max for color scaling
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var row in points)
        {
            foreach (var z in row)
            {
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
        }
        var rangeZ = maxZ - minZ;
        if (rangeZ < 0.0001) rangeZ = 0.0001; // Avoid division by zero

        // Calculate cell size to use available console width
        // Reserve space for: Y-label (5) + space (1) + panel borders (4) + info panel (~50) + gap (2)
        var consoleWidth = Math.Max(Console.WindowWidth, 80);
        var yLabelWidth = 5;
        var infoPanelWidth = 50;
        var availableForMesh = consoleWidth - yLabelWidth - 1 - 4 - infoPanelWidth - 4;
        var cellWidth = Math.Max(4, availableForMesh / cols);
        // Terminal chars are ~2:1 aspect ratio, so height = width / 2 for square appearance
        var cellHeight = Math.Max(1, (cellWidth + 1) / 2);

        // Calculate Y coordinates for each row
        var yCoords = new double[rows];
        for (int r = 0; r < rows; r++)
        {
            // Mesh rows go from MinY to MaxY (bottom to top in real space)
            // But we render top to bottom, so invert
            var t = rows > 1 ? (double)(rows - 1 - r) / (rows - 1) : 0;
            yCoords[r] = mesh.MinY + t * (mesh.MaxY - mesh.MinY);
        }

        // Calculate X coordinates for labels
        var xCoords = new double[cols];
        for (int c = 0; c < cols; c++)
        {
            var t = cols > 1 ? (double)c / (cols - 1) : 0;
            xCoords[c] = mesh.MinX + t * (mesh.MaxX - mesh.MinX);
        }

        // Build mesh using string builder for efficiency
        var sb = new System.Text.StringBuilder();

        // Render mesh rows (top to bottom = high Y to low Y)
        for (int r = 0; r < rows; r++)
        {
            // Render multiple lines per cell for square appearance
            for (int h = 0; h < cellHeight; h++)
            {
                // Y-axis label only on first line of each cell (vertically centered)
                if (h == cellHeight / 2)
                {
                    var yLabel = yCoords[r].ToString("F0").PadLeft(yLabelWidth);
                    sb.Append($"[grey]{yLabel}[/] ");
                }
                else
                {
                    sb.Append(new string(' ', yLabelWidth + 1));
                }

                // Mesh cells
                for (int c = 0; c < cols; c++)
                {
                    // Sample from the points array, mapping display grid to actual array indices
                    var pointRow = rows > 1 ? (rows - 1 - r) * (pointRows - 1) / (rows - 1) : 0;
                    var pointCol = cols > 1 ? c * (pointCols - 1) / (cols - 1) : 0;
                    var z = points[Math.Min(pointRow, pointRows - 1)][Math.Min(pointCol, pointCols - 1)];
                    var color = GetGradientColor(z, minZ, maxZ);
                    var block = new string('█', cellWidth);
                    sb.Append($"[rgb({color.r},{color.g},{color.b})]{block}[/]");
                }
                sb.AppendLine();
            }
        }

        // Add X-axis labels row (right-aligned within each cell)
        sb.Append(new string(' ', yLabelWidth + 1)); // Padding for Y-axis label column
        for (int c = 0; c < cols; c++)
        {
            var xLabel = xCoords[c].ToString("F0");
            // Right-align: pad on left to fill cell width
            var padded = xLabel.PadLeft(cellWidth);
            sb.Append($"[grey]{padded}[/]");
        }
        sb.AppendLine();

        // Add color legend
        sb.AppendLine();
        var legendWidth = cols * cellWidth;
        sb.Append(new string(' ', yLabelWidth + 1));
        sb.Append(BuildColorLegend(minZ, maxZ, legendWidth));

        var content = new Markup(sb.ToString());
        var panel = new Panel(content);
        panel.Header = new PanelHeader("[bold]Bed Mesh Heatmap[/]");
        panel.Border = BoxBorder.Rounded;
        panel.Padding = new Padding(1, 0, 1, 0);

        return panel;
    }

    /// <summary>
    /// Create the stats panel for the right column.
    /// </summary>
    private static Panel RenderStatsPanel(BedMeshSession session, MeshStats? stats)
    {
        var mesh = session.MeshData!;
        var sb = new System.Text.StringBuilder();

        // Status
        var statusDisplay = session.Status == CalibrationStatus.Success
            ? "[green]SUCCESS[/]"
            : "[red]FAILED[/]";
        sb.AppendLine($"[bold]Status:[/]    {statusDisplay}");

        // Name (if set)
        if (!string.IsNullOrEmpty(session.Name))
            sb.AppendLine($"[bold]Name:[/]      [grey]{Markup.Escape(session.Name)}[/]");

        // Times
        sb.AppendLine($"[bold]Started:[/]   [grey]{session.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
        if (session.FinishedUtc.HasValue)
            sb.AppendLine($"[bold]Finished:[/]  [grey]{session.FinishedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
        sb.AppendLine($"[bold]Duration:[/]  [grey]{session.DurationFormatted}[/]");
        if (session.HeatSoakMinutes > 0)
            sb.AppendLine($"[bold]Heat Soak:[/] [grey]{session.HeatSoakMinutes} min[/]");

        sb.AppendLine(); // Spacer

        // Mesh info - prefer ProbeCount over XCount/YCount
        var displayCols = mesh.ProbeCountX ?? mesh.XCount;
        var displayRows = mesh.ProbeCountY ?? mesh.YCount;
        sb.AppendLine($"[bold]Mesh:[/] {displayCols}x{displayRows} points");
        sb.AppendLine($"       [grey]({mesh.MinX:F1},{mesh.MinY:F1}) to ({mesh.MaxX:F1},{mesh.MaxY:F1})[/]");

        if (stats != null)
        {
            sb.AppendLine(); // Spacer
            sb.AppendLine($"[cyan]Max[/] [[{stats.MaxAtX:F1}, {stats.MaxAtY:F1}]] {stats.Max:F3} mm");
            sb.AppendLine($"[cyan]Min[/] [[{stats.MinAtX:F1}, {stats.MinAtY:F1}]] {stats.Min:F3} mm");
            sb.AppendLine($"[cyan]Range[/] [bold]{stats.Range:F3} mm[/]");
        }

        // Printer config
        var configParts = new List<string>();
        if (!string.IsNullOrEmpty(mesh.Algorithm))
            configParts.Add($"algo={mesh.Algorithm}");
        if (mesh.ZOffset.HasValue)
            configParts.Add($"z_offset={mesh.ZOffset.Value:F4}");
        if (mesh.NozzleDiameter.HasValue)
            configParts.Add($"nozzle={mesh.NozzleDiameter.Value}mm");

        if (configParts.Count > 0)
        {
            sb.AppendLine(); // Spacer
            sb.AppendLine($"[grey]{string.Join(" | ", configParts)}[/]");
        }

        // Error message if any
        if (!string.IsNullOrEmpty(session.ErrorMessage))
        {
            sb.AppendLine(); // Spacer
            sb.AppendLine($"[red]Error:[/] {Markup.Escape(session.ErrorMessage)}");
        }

        var content = new Markup(sb.ToString().TrimEnd());
        var panel = new Panel(content);
        panel.Header = new PanelHeader("[bold]Calibration Info[/]");
        panel.Border = BoxBorder.Rounded;
        panel.Padding = new Padding(1, 0, 1, 0);

        return panel;
    }

    /// <summary>
    /// Build a horizontal color legend bar.
    /// </summary>
    private static string BuildColorLegend(double minZ, double maxZ, int width)
    {
        var sb = new System.Text.StringBuilder();

        // Min value label
        sb.Append($"[grey]{minZ:F3}[/] ");

        // Color bar - use remaining width
        var barWidth = Math.Max(10, width - 18); // Account for labels
        for (int i = 0; i < barWidth; i++)
        {
            var t = (double)i / (barWidth - 1);
            var color = InterpolateGradient(t);
            sb.Append($"[rgb({color.r},{color.g},{color.b})]█[/]");
        }

        // Max value label
        sb.Append($" [grey]{maxZ:F3}[/]");

        return sb.ToString();
    }

    /// <summary>
    /// Get color for a Z value based on the gradient.
    /// </summary>
    private static (byte r, byte g, byte b) GetGradientColor(double z, double minZ, double maxZ)
    {
        var range = maxZ - minZ;
        if (range < 0.0001) range = 0.0001;

        // Normalize Z to 0-1 range (0 = min/low/blue, 1 = max/high/red)
        var t = (z - minZ) / range;
        t = Math.Clamp(t, 0, 1);

        return InterpolateGradient(t);
    }

    /// <summary>
    /// Interpolate the color gradient at position t (0-1).
    /// </summary>
    private static (byte r, byte g, byte b) InterpolateGradient(double t)
    {
        t = Math.Clamp(t, 0, 1);

        // Find the two gradient stops to interpolate between
        for (int i = 0; i < GradientStops.Length - 1; i++)
        {
            var (pos1, r1, g1, b1) = GradientStops[i];
            var (pos2, r2, g2, b2) = GradientStops[i + 1];

            if (t >= pos2 && t <= pos1)
            {
                // Interpolate between these two stops
                var localT = (t - pos2) / (pos1 - pos2);
                return (
                    (byte)(r2 + (r1 - r2) * localT),
                    (byte)(g2 + (g1 - g2) * localT),
                    (byte)(b2 + (b1 - b2) * localT)
                );
            }
        }

        // Fallback to endpoints
        if (t >= 0.5)
            return (GradientStops[0].r, GradientStops[0].g, GradientStops[0].b);
        else
            return (GradientStops[^1].r, GradientStops[^1].g, GradientStops[^1].b);
    }
}
