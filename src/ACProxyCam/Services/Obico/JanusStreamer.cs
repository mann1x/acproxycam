// JanusStreamer.cs - Streams MJPEG frames to Janus via UDP

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Streams MJPEG frames to Janus WebRTC gateway via UDP.
/// Uses the same protocol as moonraker-obico for compatibility.
/// </summary>
public class JanusStreamer : IDisposable
{
    private readonly string _janusServer;
    private readonly int _dataPort;
    private readonly int _targetFps;
    private readonly Func<byte[]?> _getFrameCallback;

    private Socket? _udpSocket;
    private CancellationTokenSource? _cts;
    private Task? _streamTask;

    private volatile bool _isStreaming;
    private volatile bool _isDisposed;
    private volatile bool _isPaused;

    // Bandwidth throttle between UDP chunks - matches moonraker-obico default of 4ms
    // This prevents buffer overflow in Janus and ensures reliable delivery
    private readonly double _bandwidthThrottleMs;

    // Chunk size for UDP packets (same as moonraker-obico)
    private const int UDP_CHUNK_SIZE = 1400;

    // Default bandwidth throttle (4ms = 0.004s, same as moonraker-obico)
    private const double DEFAULT_BANDWIDTH_THROTTLE_MS = 4.0;

    public event EventHandler<string>? StatusChanged;

    public bool IsStreaming => _isStreaming;
    public bool IsPaused => _isPaused;

    /// <summary>
    /// Pause streaming (e.g., when WebRTC connection drops).
    /// Frames will not be sent to Janus while paused.
    /// </summary>
    public void Pause()
    {
        if (!_isPaused)
        {
            _isPaused = true;
            Log("Streaming paused (waiting for WebRTC reconnection)");
        }
    }

    /// <summary>
    /// Resume streaming (e.g., when WebRTC connection is re-established).
    /// </summary>
    public void Resume()
    {
        if (_isPaused)
        {
            _isPaused = false;
            Log("Streaming resumed");
        }
    }

    /// <summary>
    /// Create a Janus MJPEG streamer.
    /// </summary>
    /// <param name="janusServer">Janus server hostname or IP</param>
    /// <param name="dataPort">UDP port for MJPEG data</param>
    /// <param name="targetFps">Target FPS for streaming</param>
    /// <param name="getFrameCallback">Callback to get JPEG frames</param>
    /// <param name="bandwidthThrottleMs">Delay between UDP chunks in ms (default 4ms, same as moonraker-obico)</param>
    public JanusStreamer(string janusServer, int dataPort, int targetFps, Func<byte[]?> getFrameCallback, double bandwidthThrottleMs = DEFAULT_BANDWIDTH_THROTTLE_MS)
    {
        _janusServer = janusServer;
        _dataPort = dataPort;
        _targetFps = targetFps > 0 ? targetFps : 25;
        _getFrameCallback = getFrameCallback;
        _bandwidthThrottleMs = bandwidthThrottleMs;
    }

    /// <summary>
    /// Start streaming frames to Janus.
    /// </summary>
    public void Start()
    {
        if (_isStreaming)
            return;

        _cts = new CancellationTokenSource();

        // Create UDP socket with larger send buffer to prevent packet loss
        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpSocket.SendBufferSize = 1024 * 1024; // 1MB send buffer

        _isStreaming = true;
        _streamTask = Task.Run(() => StreamLoopAsync(_cts.Token));

        Log($"Started streaming to Janus at {_janusServer}:{_dataPort} at {_targetFps} FPS");
    }

    /// <summary>
    /// Stop streaming.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isStreaming)
            return;

        _isStreaming = false;
        _cts?.Cancel();

        if (_streamTask != null)
        {
            try { await _streamTask; }
            catch (OperationCanceledException) { }
        }

        _udpSocket?.Close();
        _udpSocket?.Dispose();
        _udpSocket = null;

        Log("Stopped streaming to Janus");
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        // Resolve hostname to IP address
        IPAddress serverIp;
        if (IPAddress.TryParse(_janusServer, out var parsedIp))
        {
            serverIp = parsedIp;
        }
        else
        {
            var addresses = await Dns.GetHostAddressesAsync(_janusServer, ct);
            serverIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException($"Could not resolve Janus server: {_janusServer}");
        }

        var endpoint = new IPEndPoint(serverIp, _dataPort);
        var minIntervalMs = 1000.0 / _targetFps;
        var lastFrameTime = DateTime.MinValue;
        var frameCount = 0;
        var lastLogTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested && _isStreaming)
        {
            try
            {
                // If paused (WebRTC down), wait for resume instead of sending frames
                if (_isPaused)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                // Frame rate limiting
                var elapsed = (DateTime.UtcNow - lastFrameTime).TotalMilliseconds;
                if (elapsed < minIntervalMs)
                {
                    var waitMs = (int)(minIntervalMs - elapsed);
                    if (waitMs > 0)
                        await Task.Delay(waitMs, ct);
                }

                lastFrameTime = DateTime.UtcNow;

                // Get frame
                var jpegData = _getFrameCallback();
                if (jpegData == null || jpegData.Length == 0)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                // Send frame to Janus
                await SendFrameAsync(jpegData, endpoint, ct);
                frameCount++;

                // Log stats periodically
                if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 30)
                {
                    var fps = frameCount / (DateTime.UtcNow - lastLogTime).TotalSeconds;
                    Log($"Streaming at {fps:F1} FPS ({frameCount} frames in 30s)");
                    frameCount = 0;
                    lastLogTime = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Stream error: {ex.Message}");
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>
    /// Send a single JPEG frame to Janus.
    /// Protocol: header + base64-encoded chunks
    /// Header format: \r\n{base64_length}:{original_length}\r\n
    /// </summary>
    private async Task SendFrameAsync(byte[] jpegData, IPEndPoint endpoint, CancellationToken ct)
    {
        if (_udpSocket == null)
            return;

        // Base64 encode the JPEG
        var base64Data = Convert.ToBase64String(jpegData);
        var base64Bytes = Encoding.UTF8.GetBytes(base64Data);

        // Send header: \r\n{base64_length}:{original_jpg_length}\r\n
        var header = $"\r\n{base64Bytes.Length}:{jpegData.Length}\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);

        try
        {
            _udpSocket.SendTo(headerBytes, endpoint);

            // Send base64 data in chunks with bandwidth throttle after each chunk
            // This matches moonraker-obico behavior and prevents Janus buffer overflow
            for (int i = 0; i < base64Bytes.Length; i += UDP_CHUNK_SIZE)
            {
                ct.ThrowIfCancellationRequested();

                var chunkSize = Math.Min(UDP_CHUNK_SIZE, base64Bytes.Length - i);
                var chunk = new byte[chunkSize];
                Array.Copy(base64Bytes, i, chunk, 0, chunkSize);

                _udpSocket.SendTo(chunk, endpoint);

                // Bandwidth throttle after EVERY chunk (matches moonraker-obico)
                // This ensures Janus has time to process each chunk before receiving the next
                if (_bandwidthThrottleMs > 0)
                {
                    await Task.Delay((int)_bandwidthThrottleMs, ct);
                }
            }
        }
        catch (SocketException ex)
        {
            Log($"UDP send error: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        StatusChanged?.Invoke(this, $"[JanusStream] {message}");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _isStreaming = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _udpSocket?.Close();
        _udpSocket?.Dispose();
    }
}
