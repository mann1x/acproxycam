// MjpegSourceClient.cs - Client for consuming MJPEG streams from h264-streamer

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ACProxyCam.Services;

/// <summary>
/// Client for consuming MJPEG streams from h264-streamer or other sources.
/// Parses multipart MJPEG streams and extracts JPEG frames.
/// </summary>
public class MjpegSourceClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private bool _disposed;

    /// <summary>
    /// MJPEG stream URL (e.g., http://192.168.1.100:8080/stream)
    /// </summary>
    public string StreamUrl { get; }

    /// <summary>
    /// Snapshot URL for fetching single frames (e.g., http://192.168.1.100:8080/snapshot)
    /// </summary>
    public string SnapshotUrl { get; }

    /// <summary>
    /// Raised when a JPEG frame is received.
    /// </summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>
    /// Raised when connection to the stream is established.
    /// </summary>
    public event Action? Connected;

    /// <summary>
    /// Raised when connection to the stream is lost.
    /// </summary>
    public event Action? Disconnected;

    /// <summary>
    /// Raised when an error occurs.
    /// </summary>
    public event Action<string>? Error;

    /// <summary>
    /// Whether the client is currently connected to the stream.
    /// </summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Total number of frames received since start.
    /// </summary>
    public long FrameCount { get; private set; }

    /// <summary>
    /// Measured frames per second (updated every second).
    /// </summary>
    public double MeasuredFps { get; private set; }

    /// <summary>
    /// Time since last frame was received.
    /// </summary>
    public TimeSpan TimeSinceLastFrame => DateTime.UtcNow - _lastFrameTime;

    private DateTime _lastFrameTime = DateTime.MinValue;
    private int _reconnectAttempts;
    private const int MaxReconnectDelay = 30000; // 30 seconds max backoff

    public MjpegSourceClient(string streamUrl, string snapshotUrl)
    {
        StreamUrl = streamUrl;
        SnapshotUrl = snapshotUrl;

        _httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // Stream is continuous
        };
    }

    /// <summary>
    /// Start consuming the MJPEG stream.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (_readTask != null && !_readTask.IsCompleted)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readTask = ReadStreamLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop consuming the stream.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Fetch a single snapshot from the snapshot URL.
    /// </summary>
    public async Task<byte[]?> FetchSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(5000); // 5 second timeout for snapshot

            return await _httpClient.GetByteArrayAsync(SnapshotUrl, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Main loop for reading the MJPEG stream with automatic reconnection.
    /// </summary>
    private async Task ReadStreamLoopAsync(CancellationToken ct)
    {
        _reconnectAttempts = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _httpClient.GetAsync(
                    StreamUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct);

                response.EnsureSuccessStatusCode();

                // Parse Content-Type for boundary
                var contentType = response.Content.Headers.ContentType?.ToString() ?? "";
                var boundary = ExtractBoundary(contentType);

                if (string.IsNullOrEmpty(boundary))
                {
                    Error?.Invoke($"Invalid MJPEG stream: no boundary in Content-Type '{contentType}'");
                    await ReconnectDelayAsync(ct);
                    continue;
                }

                using var stream = await response.Content.ReadAsStreamAsync(ct);

                IsConnected = true;
                _reconnectAttempts = 0; // Reset on successful connection
                Connected?.Invoke();

                await ParseMjpegStreamAsync(stream, boundary, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                Disconnected?.Invoke();
                Error?.Invoke(ex.Message);

                await ReconnectDelayAsync(ct);
            }
        }

        IsConnected = false;
        Disconnected?.Invoke();
    }

    /// <summary>
    /// Parse the MJPEG multipart stream and extract frames.
    /// </summary>
    private async Task ParseMjpegStreamAsync(Stream stream, string boundary, CancellationToken ct)
    {
        var boundaryBytes = Encoding.ASCII.GetBytes("--" + boundary);
        var fpsStopwatch = Stopwatch.StartNew();
        var fpsFrameCount = 0;

        // Buffer for reading
        var buffer = new byte[256 * 1024]; // 256KB buffer
        var frameBuffer = new MemoryStream();

        // State machine for parsing multipart MJPEG
        // Format: \r\n--boundary\r\n Headers \r\n\r\n [binary body] \r\n--boundary\r\n ...
        var sawBoundary = false;
        var contentLength = -1;

        while (!ct.IsCancellationRequested)
        {
            // Read headers line by line (text mode)
            var line = await ReadLineAsync(stream, ct);
            if (line == null)
                break;

            // Check for boundary
            if (line.StartsWith("--") && line.Contains(boundary))
            {
                sawBoundary = true;
                contentLength = -1;
                frameBuffer.SetLength(0);
                continue;
            }

            // Parse headers (only after we've seen a boundary)
            if (sawBoundary)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var lengthStr = line.Substring(15).Trim();
                    int.TryParse(lengthStr, out contentLength);
                    continue;
                }

                if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Empty line after boundary+headers = end of headers, start of body
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (contentLength > 0)
                    {
                        // Read exact body bytes directly (binary, not line-by-line)
                        var frameData = new byte[contentLength];
                        var totalRead = 0;

                        while (totalRead < contentLength)
                        {
                            var bytesRead = await stream.ReadAsync(
                                frameData.AsMemory(totalRead, contentLength - totalRead),
                                ct);

                            if (bytesRead == 0)
                                break;

                            totalRead += bytesRead;
                        }

                        if (totalRead == contentLength)
                        {
                            ProcessFrame(frameData);
                            fpsFrameCount++;
                        }
                    }

                    // Reset for next part
                    sawBoundary = false;
                    contentLength = -1;
                }
                continue;
            }

            // Fallback: not inside a boundary part, ignore line
            // (handles separator CRLFs between parts)

            // Update FPS every second
            if (fpsStopwatch.ElapsedMilliseconds >= 1000)
            {
                MeasuredFps = fpsFrameCount * 1000.0 / fpsStopwatch.ElapsedMilliseconds;
                fpsFrameCount = 0;
                fpsStopwatch.Restart();
            }
        }
    }

    /// <summary>
    /// Process a received JPEG frame.
    /// </summary>
    private void ProcessFrame(byte[] jpegData)
    {
        FrameCount++;
        _lastFrameTime = DateTime.UtcNow;
        FrameReceived?.Invoke(jpegData);
    }

    /// <summary>
    /// Read a line from the stream (CRLF or LF terminated).
    /// </summary>
    private async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead == 0)
                return null; // End of stream

            var b = buffer[0];

            if (b == '\n')
            {
                // Remove trailing CR if present
                if (bytes.Count > 0 && bytes[^1] == '\r')
                    bytes.RemoveAt(bytes.Count - 1);

                break;
            }

            bytes.Add(b);

            // Prevent memory exhaustion on malformed streams
            if (bytes.Count > 10000)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Check if the data is a complete JPEG (starts with FFD8, ends with FFD9).
    /// </summary>
    private static bool IsCompleteJpeg(byte[] data)
    {
        if (data.Length < 4)
            return false;

        // Check JPEG start marker (FFD8)
        if (data[0] != 0xFF || data[1] != 0xD8)
            return false;

        // Check JPEG end marker (FFD9)
        if (data[^2] != 0xFF || data[^1] != 0xD9)
            return false;

        return true;
    }

    /// <summary>
    /// Extract the boundary string from Content-Type header.
    /// </summary>
    private static string ExtractBoundary(string contentType)
    {
        // Parse: multipart/x-mixed-replace; boundary=mjpegstream
        // or: multipart/x-mixed-replace;boundary=--myboundary
        var match = Regex.Match(contentType, @"boundary\s*=\s*""?([^""\s;]+)""?", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var boundary = match.Groups[1].Value;
            // Some servers include the -- prefix, some don't
            return boundary.TrimStart('-');
        }
        return "";
    }

    /// <summary>
    /// Wait before reconnecting with exponential backoff.
    /// </summary>
    private async Task ReconnectDelayAsync(CancellationToken ct)
    {
        _reconnectAttempts++;

        // Exponential backoff: 1s, 2s, 4s, 8s, 16s, 30s (max)
        var delayMs = Math.Min(1000 * (1 << Math.Min(_reconnectAttempts - 1, 4)), MaxReconnectDelay);

        try
        {
            await Task.Delay(delayMs, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }

    ~MjpegSourceClient()
    {
        Dispose();
    }
}
