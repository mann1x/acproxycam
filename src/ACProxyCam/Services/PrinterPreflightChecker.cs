// PrinterPreflightChecker.cs - Preflight checks for printer video source detection

using System.Text.Json;
using ACProxyCam.Models;

namespace ACProxyCam.Services;

/// <summary>
/// Performs preflight checks to detect available video endpoints and recommend configuration.
/// </summary>
public class PrinterPreflightChecker
{
    private readonly HttpClient _httpClient;
    private const int TimeoutMs = 3000;

    public PrinterPreflightChecker()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs)
        };
    }

    /// <summary>
    /// Perform preflight checks for a printer.
    /// </summary>
    /// <param name="ipAddress">Printer IP address</param>
    /// <param name="existingControlPort">Known h264-streamer control port (if previously configured)</param>
    /// <returns>Preflight result with recommendations</returns>
    public async Task<PrinterPreflightResult> CheckAsync(
        string ipAddress,
        int? existingControlPort = null)
    {
        var result = new PrinterPreflightResult();

        // 1. Check native H.264 endpoint
        result.NativeH264Available = await CheckEndpointAsync(
            $"http://{ipAddress}:18088/flv");

        // 2. Check h264-streamer control endpoint
        int controlPort = existingControlPort ?? 8081;
        var configResult = await TryGetStreamerConfigAsync(ipAddress, controlPort);

        if (configResult == null && existingControlPort == null)
        {
            // Default port 8081 failed and no known port - not detected
            result.H264StreamerDetected = false;
        }
        else if (configResult != null)
        {
            result.H264StreamerDetected = true;
            result.ControlPort = controlPort;
            result.StreamerConfig = configResult;
            result.StreamingPort = configResult.StreamingPort;
        }

        // 3. Check MJPEG endpoints
        int streamingPort = result.StreamerConfig?.StreamingPort ?? 8080;

        // Run MJPEG and snapshot checks in parallel
        var mjpegTask = CheckEndpointAsync($"http://{ipAddress}:{streamingPort}/stream");
        var snapshotTask = CheckEndpointAsync($"http://{ipAddress}:{streamingPort}/snapshot");

        await Task.WhenAll(mjpegTask, snapshotTask);

        result.MjpegStreamAvailable = mjpegTask.Result;
        result.SnapshotAvailable = snapshotTask.Result;

        if (!result.H264StreamerDetected)
        {
            result.StreamingPort = streamingPort;
        }

        // 4. Determine recommendation
        DetermineRecommendation(result);

        return result;
    }

    /// <summary>
    /// Try to detect h264-streamer on a specific port.
    /// </summary>
    public async Task<H264StreamerConfig?> TryGetStreamerConfigAsync(string ipAddress, int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeoutMs);
            var response = await _httpClient.GetAsync(
                $"http://{ipAddress}:{port}/api/config",
                cts.Token);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<H264StreamerConfig>(json);
            }
        }
        catch
        {
            // Timeout or connection error
        }
        return null;
    }

    /// <summary>
    /// Check if an HTTP endpoint is reachable.
    /// </summary>
    private async Task<bool> CheckEndpointAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeoutMs);

            // Use HEAD request to minimize data transfer
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _httpClient.SendAsync(request, cts.Token);

            // For streaming endpoints, 200 OK means available
            // Some servers might return other success codes
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Connection failed or timed out
            return false;
        }
    }

    /// <summary>
    /// Determine the recommended video source based on detected endpoints.
    /// </summary>
    private void DetermineRecommendation(PrinterPreflightResult result)
    {
        if (result.H264StreamerDetected && result.StreamerConfig != null)
        {
            var encoder = result.StreamerConfig.EncoderType;

            if (encoder == "rkmpi")
            {
                // rkmpi = HW MJPEG only, no hardware H.264
                // Recommend MJPEG source to avoid transcoding overhead
                result.RecommendedVideoSource = "mjpeg";
                result.RecommendationReason =
                    $"h264-streamer using {encoder} encoder (HW MJPEG) - native MJPEG is most efficient";
            }
            else if (encoder == "rkmpi-yuyv" || encoder == "gkcam")
            {
                // HW H.264 available, recommend H.264 source
                result.RecommendedVideoSource = "h264";
                result.RecommendationReason =
                    $"h264-streamer using {encoder} encoder (HW H.264) - native H.264 is most efficient";
            }
            else
            {
                // Unknown encoder, default to H.264
                result.RecommendedVideoSource = "h264";
                result.RecommendationReason =
                    $"h264-streamer detected with unknown encoder '{encoder}'";
            }
        }
        else if (result.NativeH264Available)
        {
            result.RecommendedVideoSource = "h264";
            result.RecommendationReason = "Native H.264 stream available (no h264-streamer detected)";
        }
        else if (result.MjpegStreamAvailable)
        {
            result.RecommendedVideoSource = "mjpeg";
            result.RecommendationReason = "MJPEG stream available (no native H.264)";
        }
        else
        {
            // Nothing detected, default to H.264 (user will need to start services)
            result.RecommendedVideoSource = "h264";
            result.RecommendationReason = "No endpoints detected - defaulting to H.264 (start printer services)";
        }
    }

    /// <summary>
    /// Check if using the specified video source would be suboptimal.
    /// </summary>
    public bool IsSuboptimal(PrinterPreflightResult result, string videoSource)
    {
        if (!result.H264StreamerDetected || result.StreamerConfig == null)
            return false;

        var encoder = result.StreamerConfig.EncoderType;

        if (videoSource == "mjpeg")
        {
            // MJPEG is suboptimal when encoder provides HW H.264
            return encoder == "rkmpi-yuyv" || encoder == "gkcam";
        }
        else if (videoSource == "h264")
        {
            // H.264 is suboptimal when encoder only provides HW MJPEG
            return encoder == "rkmpi";
        }

        return false;
    }

    /// <summary>
    /// Get a human-readable warning for suboptimal configuration.
    /// </summary>
    public string? GetSuboptimalWarning(PrinterPreflightResult result, string videoSource)
    {
        if (!IsSuboptimal(result, videoSource))
            return null;

        var encoder = result.StreamerConfig?.EncoderType ?? "unknown";

        if (videoSource == "mjpeg")
        {
            return $"h264-streamer encoder '{encoder}' provides hardware H.264. " +
                   "Using MJPEG source requires transcoding which uses more CPU.";
        }
        else
        {
            return $"h264-streamer encoder '{encoder}' provides native MJPEG. " +
                   "Using H.264 source may not be available or requires extra processing.";
        }
    }
}
