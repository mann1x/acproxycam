// MjpegServer.cs - MJPEG streaming server for Linux
// Uses SkiaSharp for cross-platform JPEG encoding

using System.Net;
using System.Net.Sockets;
using System.Text;
using SkiaSharp;

namespace ACProxyCam.Services;

/// <summary>
/// HTTP server providing MJPEG streaming and snapshot endpoints.
/// </summary>
public class MjpegServer : IDisposable
{
    private TcpListener? _listener;
    private readonly List<ClientConnection> _clients = new();
    private readonly object _clientLock = new();
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;
    private bool _disposed;

    private byte[]? _lastJpegFrame;
    private readonly object _frameLock = new();
    private int _frameWidth;
    private int _frameHeight;
    private DateTime _lastEncodeTime = DateTime.MinValue;
    private int _framesSkipped;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? ErrorOccurred;
    /// <summary>
    /// Raised when a snapshot is requested but no frame is available.
    /// Allows the owner to try to restart the camera.
    /// </summary>
    public event EventHandler? SnapshotRequested;

    public int Port { get; private set; }
    public IPAddress BindAddress { get; private set; } = IPAddress.Any;
    public bool IsRunning { get; private set; }
    public int ConnectedClients
    {
        get
        {
            lock (_clientLock)
            {
                return _clients.Count;
            }
        }
    }
    public int JpegQuality { get; set; } = 80;

    /// <summary>
    /// Maximum frames per second to encode. 0 = unlimited.
    /// </summary>
    public int MaxFps { get; set; } = 10;

    /// <summary>
    /// FPS when no clients are connected (for snapshot availability). 0 = no encoding when idle.
    /// </summary>
    public int IdleFps { get; set; } = 1;

    // MJPEG boundary string
    private const string Boundary = "--mjpegboundary";
    private static readonly byte[] BoundaryBytes = Encoding.ASCII.GetBytes($"\r\n{Boundary}\r\n");
    private static readonly byte[] ContentTypeHeader = Encoding.ASCII.GetBytes("Content-Type: image/jpeg\r\n");

    /// <summary>
    /// Start the MJPEG server.
    /// </summary>
    public void Start(int port, IPAddress? bindAddress = null)
    {
        if (IsRunning)
            Stop();

        Port = port;
        BindAddress = bindAddress ?? IPAddress.Any;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(BindAddress, Port);

        try
        {
            _listener.Start();
            IsRunning = true;
            StatusChanged?.Invoke(this, $"MJPEG server started on {BindAddress}:{Port}");

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "MjpegAcceptThread"
            };
            _acceptThread.Start();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    /// <summary>
    /// Stop the MJPEG server.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        IsRunning = false;

        lock (_clientLock)
        {
            foreach (var client in _clients.ToList())
            {
                client.Dispose();
            }
            _clients.Clear();
        }

        _acceptThread?.Join(2000);
        _acceptThread = null;

        StatusChanged?.Invoke(this, "MJPEG server stopped");
    }

    /// <summary>
    /// Push a new frame to all connected clients.
    /// Frame data should be BGR24 format.
    /// Implements frame rate limiting and lazy encoding.
    /// </summary>
    public void PushFrame(byte[] bgrData, int width, int height, int stride)
    {
        try
        {
            var now = DateTime.UtcNow;
            var hasClients = ConnectedClients > 0;

            // Determine target FPS based on whether clients are connected
            var targetFps = hasClients ? MaxFps : IdleFps;

            // Skip frame if we're encoding too fast
            if (targetFps > 0)
            {
                var minInterval = TimeSpan.FromSeconds(1.0 / targetFps);
                if (now - _lastEncodeTime < minInterval)
                {
                    _framesSkipped++;
                    return;
                }
            }
            else if (!hasClients)
            {
                // IdleFps = 0 means no encoding when idle
                _framesSkipped++;
                return;
            }

            // Encode BGR24 to JPEG using SkiaSharp
            var jpegData = EncodeBgrToJpeg(bgrData, width, height, stride);
            if (jpegData == null)
                return;

            _lastEncodeTime = now;

            lock (_frameLock)
            {
                _lastJpegFrame = jpegData;
                _frameWidth = width;
                _frameHeight = height;
            }

            // Send to all streaming clients
            if (hasClients)
            {
                lock (_clientLock)
                {
                    var deadClients = new List<ClientConnection>();

                    foreach (var client in _clients)
                    {
                        if (client.IsStreaming)
                        {
                            if (!client.TrySendFrame(jpegData))
                            {
                                deadClients.Add(client);
                            }
                        }
                    }

                    // Remove disconnected clients
                    foreach (var dead in deadClients)
                    {
                        _clients.Remove(dead);
                        dead.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Encode BGR24 data to JPEG using SkiaSharp.
    /// </summary>
    private byte[]? EncodeBgrToJpeg(byte[] bgrData, int width, int height, int stride)
    {
        try
        {
            // Use BGRA8888 format - SkiaSharp's most compatible format
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);

            unsafe
            {
                var pixels = (byte*)bitmap.GetPixels().ToPointer();
                int srcOffset = 0;
                int dstOffset = 0;

                for (int y = 0; y < height; y++)
                {
                    srcOffset = y * stride;
                    dstOffset = y * width * 4;

                    for (int x = 0; x < width; x++)
                    {
                        // Input is BGR24, output is BGRA8888
                        // Copy BGR directly and add alpha
                        pixels[dstOffset++] = bgrData[srcOffset++]; // B
                        pixels[dstOffset++] = bgrData[srcOffset++]; // G
                        pixels[dstOffset++] = bgrData[srcOffset++]; // R
                        pixels[dstOffset++] = 255;                   // A
                    }
                }
            }

            // Encode to JPEG
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

            return data.ToArray();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new Exception($"JPEG encoding failed: {ex.Message}", ex));
            return null;
        }
    }

    /// <summary>
    /// Alternative encoding using direct pixel manipulation for better performance.
    /// </summary>
    private byte[]? EncodeBgrToJpegFast(byte[] bgrData, int width, int height, int stride)
    {
        try
        {
            // Use BGRA8888 format and set alpha to 255
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var bitmap = new SKBitmap(info);

            unsafe
            {
                var pixels = (byte*)bitmap.GetPixels().ToPointer();
                int srcOffset = 0;
                int dstOffset = 0;

                for (int y = 0; y < height; y++)
                {
                    srcOffset = y * stride;
                    dstOffset = y * width * 4;

                    for (int x = 0; x < width; x++)
                    {
                        // Copy BGR and add alpha
                        pixels[dstOffset++] = bgrData[srcOffset++]; // B
                        pixels[dstOffset++] = bgrData[srcOffset++]; // G
                        pixels[dstOffset++] = bgrData[srcOffset++]; // R
                        pixels[dstOffset++] = 255;                   // A
                    }
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

            return data.ToArray();
        }
        catch
        {
            // Fall back to safe version
            return EncodeBgrToJpeg(bgrData, width, height, stride);
        }
    }

    private void AcceptLoop()
    {
        var ct = _cts!.Token;

        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                if (!_listener.Pending())
                {
                    Thread.Sleep(10);
                    continue;
                }

                var tcpClient = _listener.AcceptTcpClient();
                var client = new ClientConnection(tcpClient);

                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    ErrorOccurred?.Invoke(this, ex);
                }
            }
        }
    }

    private void HandleClient(ClientConnection client)
    {
        try
        {
            // Read HTTP request
            var request = client.ReadRequest();
            if (request == null)
            {
                client.Dispose();
                return;
            }

            var path = ParseRequestPath(request);

            switch (path.ToLowerInvariant())
            {
                case "/stream":
                case "/mjpeg":
                    HandleStreamRequest(client);
                    break;

                case "/snapshot":
                case "/snap":
                case "/image":
                    HandleSnapshotRequest(client);
                    break;

                case "/status":
                    HandleStatusRequest(client);
                    break;

                default:
                    // Default to stream for root path
                    if (path == "/" || path == "")
                        HandleStreamRequest(client);
                    else
                        SendNotFound(client);
                    break;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            client.Dispose();
        }
    }

    private void HandleStreamRequest(ClientConnection client)
    {
        // Send MJPEG header
        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: multipart/x-mixed-replace;boundary=mjpegboundary\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        if (!client.TrySend(Encoding.ASCII.GetBytes(header)))
        {
            client.Dispose();
            return;
        }

        // Mark as streaming and add to client list
        client.IsStreaming = true;

        lock (_clientLock)
        {
            _clients.Add(client);
        }

        // Send initial frame if available
        byte[]? initialFrame;
        lock (_frameLock)
        {
            initialFrame = _lastJpegFrame;
        }

        if (initialFrame != null)
        {
            client.TrySendFrame(initialFrame);
        }
    }

    private void HandleSnapshotRequest(ClientConnection client)
    {
        byte[]? frame;
        lock (_frameLock)
        {
            frame = _lastJpegFrame;
        }

        if (frame == null)
        {
            // Notify that a snapshot was requested but no frame available
            // This allows the owner to try to restart the camera
            SnapshotRequested?.Invoke(this, EventArgs.Empty);
            SendServiceUnavailable(client);
            return;
        }

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: image/jpeg\r\n" +
                     $"Content-Length: {frame.Length}\r\n" +
                     "Cache-Control: no-cache\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(frame);
        client.Dispose();
    }

    private void HandleStatusRequest(ClientConnection client)
    {
        var status = new
        {
            running = IsRunning,
            clients = ConnectedClients,
            frameWidth = _frameWidth,
            frameHeight = _frameHeight,
            hasFrame = _lastJpegFrame != null,
            maxFps = MaxFps,
            idleFps = IdleFps,
            jpegQuality = JpegQuality,
            framesSkipped = _framesSkipped
        };

        var json = System.Text.Json.JsonSerializer.Serialize(status);
        var body = Encoding.UTF8.GetBytes(json);

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: application/json\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(body);
        client.Dispose();
    }

    private void SendNotFound(ClientConnection client)
    {
        var body = "Not Found";
        var header = "HTTP/1.1 404 Not Found\r\n" +
                     "Content-Type: text/plain\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header + body));
        client.Dispose();
    }

    private void SendServiceUnavailable(ClientConnection client)
    {
        var body = "No frame available";
        var header = "HTTP/1.1 503 Service Unavailable\r\n" +
                     "Content-Type: text/plain\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header + body));
        client.Dispose();
    }

    private static string ParseRequestPath(string request)
    {
        // Parse "GET /path HTTP/1.1"
        var lines = request.Split('\n');
        if (lines.Length == 0) return "/";

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return "/";

        var path = parts[1];
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
            path = path.Substring(0, queryIndex);

        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        GC.SuppressFinalize(this);
    }

    ~MjpegServer()
    {
        Dispose();
    }

    /// <summary>
    /// Represents a connected HTTP client.
    /// </summary>
    private class ClientConnection : IDisposable
    {
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _stream;
        private readonly object _sendLock = new();
        private bool _disposed;

        public bool IsStreaming { get; set; }

        public ClientConnection(TcpClient tcpClient)
        {
            _tcpClient = tcpClient;
            _tcpClient.NoDelay = true;
            _tcpClient.SendTimeout = 5000;
            _tcpClient.ReceiveTimeout = 5000;
            _stream = _tcpClient.GetStream();
        }

        public string? ReadRequest()
        {
            try
            {
                var buffer = new byte[4096];
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read == 0) return null;
                return Encoding.ASCII.GetString(buffer, 0, read);
            }
            catch
            {
                return null;
            }
        }

        public bool TrySend(byte[] data)
        {
            if (_disposed) return false;

            lock (_sendLock)
            {
                try
                {
                    _stream.Write(data, 0, data.Length);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool TrySendFrame(byte[] jpegData)
        {
            if (_disposed) return false;

            lock (_sendLock)
            {
                try
                {
                    // Send boundary
                    _stream.Write(BoundaryBytes, 0, BoundaryBytes.Length);

                    // Send content type
                    _stream.Write(ContentTypeHeader, 0, ContentTypeHeader.Length);

                    // Send content length
                    var lengthHeader = Encoding.ASCII.GetBytes($"Content-Length: {jpegData.Length}\r\n\r\n");
                    _stream.Write(lengthHeader, 0, lengthHeader.Length);

                    // Send frame data
                    _stream.Write(jpegData, 0, jpegData.Length);

                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _stream.Close();
                _tcpClient.Close();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
