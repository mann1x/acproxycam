// MjpegServer.cs - MJPEG streaming server for Linux
// Also provides WebSocket H.264 streaming for Mainsail/Fluidd jmuxer
// Uses SkiaSharp for cross-platform JPEG encoding

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACProxyCam.Models;
using SkiaSharp;

namespace ACProxyCam.Services;

/// <summary>
/// HTTP server providing MJPEG streaming, snapshot, and WebSocket H.264 endpoints.
/// WebSocket H.264 (/h264) is for Mainsail/Fluidd jmuxer - zero CPU, no transcoding.
/// </summary>
public class MjpegServer : IDisposable
{
    private readonly List<TcpListener> _listeners = new();
    private readonly List<ClientConnection> _clients = new();
    private readonly List<H264WebSocketClient> _h264Clients = new();
    private readonly List<FlvStreamClient> _flvClients = new();
    private readonly object _clientLock = new();
    private readonly object _h264ClientLock = new();
    private readonly object _flvClientLock = new();
    private CancellationTokenSource? _cts;
    private Thread? _acceptThread;
    private bool _disposed;

    private byte[]? _lastJpegFrame;
    private readonly object _frameLock = new();
    private int _frameWidth;
    private int _frameHeight;
    private DateTime _lastEncodeTime = DateTime.MinValue;
    private int _framesSkipped;

    // H.264 state for WebSocket streaming
    private byte[]? _spsNal;
    private byte[]? _ppsNal;
    private int _nalLengthSize = 4; // AVCC NAL unit length size (1-4 bytes, typically 4)
    private byte[]? _lastKeyframe; // Cache last keyframe for new clients
    private readonly object _keyframeLock = new();
    private static readonly byte[] H264StartCode = { 0x00, 0x00, 0x00, 0x01 };

    // HLS streaming service
    private readonly HlsStreamingService _hlsService;

    // On-demand H.264 keyframe decoder for /snapshot2
    private readonly H264SnapshotDecoder _snapshotDecoder;
    private byte[]? _cachedSnapshotJpeg;
    private byte[]? _cachedSnapshotKeyframeSource; // Track which keyframe was used
    private DateTime _lastSnapshotDecodeTime = DateTime.MinValue;
    private readonly object _snapshotCacheLock = new();
    private const int SnapshotMaxFps = 10; // Max 10 decodes per second

    // MJPEG source mode flag (set when using MjpegSourceClient instead of FFmpeg H.264 decoder)
    private bool _mjpegSourceMode;
    private bool _lastKeyframeIsJpeg; // When true, _lastKeyframe is JPEG, not H.264

    public event EventHandler<string>? StatusChanged;

    public MjpegServer()
    {
        _hlsService = new HlsStreamingService();
        _hlsService.StatusChanged += (s, msg) => StatusChanged?.Invoke(this, msg);
        _snapshotDecoder = new H264SnapshotDecoder();
    }
    public event EventHandler<Exception>? ErrorOccurred;
    /// <summary>
    /// Raised when a snapshot is requested but no frame is available.
    /// Allows the owner to try to restart the camera.
    /// </summary>
    public event EventHandler? SnapshotRequested;

    public int Port { get; private set; }
    public List<IPAddress> BindAddresses { get; private set; } = new();
    public bool IsRunning { get; private set; }
    /// <summary>
    /// Number of connected MJPEG streaming clients.
    /// </summary>
    public int ConnectedClients
    {
        get
        {
            lock (_clientLock)
            {
                return _clients.Count(c => c.IsStreaming);
            }
        }
    }

    /// <summary>
    /// Number of connected H.264 WebSocket clients.
    /// </summary>
    public int H264WebSocketClients
    {
        get
        {
            lock (_h264ClientLock)
            {
                return _h264Clients.Count;
            }
        }
    }

    /// <summary>
    /// Number of connected FLV streaming clients.
    /// </summary>
    public int FlvClients
    {
        get
        {
            lock (_flvClientLock)
            {
                return _flvClients.Count;
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

    /// <summary>
    /// Whether an external streaming client (e.g., Obico/Janus) needs full-rate frames.
    /// When true, frames are encoded at MaxFps even if no HTTP clients are connected.
    /// </summary>
    public bool HasExternalStreamingClient { get; set; } = false;

    /// <summary>
    /// Last time an HLS request was made (playlist, segment, or part).
    /// Used to determine if HLS clients are active.
    /// </summary>
    private volatile bool _hlsActivityFlag = false;
    private DateTime _hlsActivityExpiry = DateTime.MinValue;
    private readonly object _hlsActivityLock = new();

    /// <summary>
    /// Whether there has been recent HLS activity (within last 5 seconds).
    /// </summary>
    public bool HasHlsActivity
    {
        get
        {
            if (!_hlsActivityFlag) return false;
            lock (_hlsActivityLock)
            {
                if (DateTime.UtcNow > _hlsActivityExpiry)
                {
                    _hlsActivityFlag = false;
                    return false;
                }
                return true;
            }
        }
    }

    /// <summary>
    /// Mark HLS activity to trigger full frame rate encoding.
    /// </summary>
    private void MarkHlsActivity()
    {
        lock (_hlsActivityLock)
        {
            _hlsActivityExpiry = DateTime.UtcNow.AddSeconds(5);
            _hlsActivityFlag = true;
        }
    }

    /// <summary>
    /// Callback to get current LED status. Returns null if not available.
    /// </summary>
    public Func<CancellationToken, Task<LedStatus?>>? GetLedStatusAsync { get; set; }

    /// <summary>
    /// Callback to set LED state. Returns true on success.
    /// </summary>
    public Func<bool, CancellationToken, Task<bool>>? SetLedAsync { get; set; }

    /// <summary>
    /// Get the last encoded JPEG frame. Returns null if no frame available.
    /// Used by Obico client for snapshot uploads.
    /// </summary>
    public byte[]? GetLastJpegFrame()
    {
        lock (_frameLock)
        {
            return _lastJpegFrame;
        }
    }

    /// <summary>
    /// Whether HLS streaming is ready (has segments available).
    /// </summary>
    public bool HlsReady => _hlsService.IsReady;

    /// <summary>
    /// Whether LL-HLS is enabled.
    /// </summary>
    public bool LlHlsEnabled => _hlsService.LlHlsEnabled;

    /// <summary>
    /// Configure LL-HLS settings.
    /// </summary>
    /// <param name="enabled">Enable LL-HLS partial segments</param>
    /// <param name="partDurationMs">Part duration in milliseconds (100-500)</param>
    public void ConfigureLlHls(bool enabled, int partDurationMs = 200)
    {
        _hlsService.ConfigureLlHls(enabled, partDurationMs);
    }

    /// <summary>
    /// Set H.264 SPS/PPS NAL units (extracted from decoder extradata).
    /// Must be called before H.264 streaming starts.
    /// </summary>
    /// <param name="sps">SPS NAL unit</param>
    /// <param name="pps">PPS NAL unit</param>
    /// <param name="nalLengthSize">AVCC NAL unit length size (1-4 bytes, from extradata byte 4 lower 2 bits + 1)</param>
    public void SetH264Parameters(byte[]? sps, byte[]? pps, int nalLengthSize = 4)
    {
        _spsNal = sps;
        _ppsNal = pps;
        _nalLengthSize = nalLengthSize >= 1 && nalLengthSize <= 4 ? nalLengthSize : 4;
        _hlsService.SetH264Parameters(sps, pps, nalLengthSize);

        // Update FLV clients with new SPS/PPS
        if (sps != null && pps != null)
        {
            lock (_flvClientLock)
            {
                foreach (var client in _flvClients)
                    client.UpdateParameters(sps, pps);
            }
        }
    }

    /// <summary>
    /// Push an H.264 packet to all connected WebSocket and HLS clients.
    /// Called by PrinterThread when raw H.264 packet is received from decoder.
    /// Data is in AVCC format (length-prefixed NAL units).
    /// </summary>
    // H.264 diagnostic counters
    private int _h264PacketCount;
    private int _h264KeyframeCount;
    private int _h264NonKeyframeCount;
    private int _h264ParseFailCount;
    private DateTime _lastH264DiagLog = DateTime.MinValue;

    // Incoming H.264 FPS measurement (measured at packet level for accuracy)
    private int _inputFpsFrameCount;
    private DateTime _inputFpsWindowStart = DateTime.MinValue;
    private int _measuredInputFps;
    private const int InputFpsWindowMs = 1000; // Calculate FPS over 1 second window

    /// <summary>
    /// Measured incoming H.264 stream FPS (updated every second based on packet arrivals).
    /// </summary>
    public int MeasuredInputFps => _measuredInputFps;

    /// <summary>
    /// Video source mode: "h264" or "mjpeg".
    /// When "mjpeg", H.264/HLS endpoints return 503 Service Unavailable.
    /// </summary>
    public string VideoSourceMode
    {
        get => _mjpegSourceMode ? "mjpeg" : "h264";
        set => _mjpegSourceMode = value?.ToLowerInvariant() == "mjpeg";
    }

    /// <summary>
    /// Push a pre-encoded JPEG frame directly (from MJPEG source).
    /// No decoding or re-encoding needed - direct passthrough.
    /// </summary>
    /// <param name="jpegData">JPEG-encoded frame data</param>
    public void PushJpegFrame(byte[] jpegData)
    {
        if (jpegData == null || jpegData.Length == 0)
            return;

        var now = DateTime.UtcNow;

        // Update JPEG frame cache
        lock (_frameLock)
        {
            _lastJpegFrame = jpegData;
            _lastEncodeTime = now;
        }

        // Update keyframe cache for snapshot (JPEG is always a "keyframe")
        lock (_keyframeLock)
        {
            _lastKeyframe = jpegData;
            _lastKeyframeIsJpeg = true;
        }

        // Measure input FPS (only when no H.264 encoding active, to avoid double-counting
        // since PushH264Packet also increments the same counter)
        if (_h264PacketCount == 0)
        {
            if (_inputFpsWindowStart == DateTime.MinValue)
            {
                _inputFpsWindowStart = now;
                _inputFpsFrameCount = 0;
            }
            _inputFpsFrameCount++;

            var windowElapsed = (now - _inputFpsWindowStart).TotalMilliseconds;
            if (windowElapsed >= InputFpsWindowMs)
            {
                _measuredInputFps = (int)Math.Round(_inputFpsFrameCount * 1000.0 / windowElapsed);
                _inputFpsWindowStart = now;
                _inputFpsFrameCount = 0;
            }
        }

        // Send to all MJPEG streaming clients
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

    public void PushH264Packet(byte[] data, bool isKeyframe, long pts = 0)
    {
        // Always push to HLS so segments are ready when clients connect
        try
        {
            _hlsService.PushH264Packet(data, isKeyframe, pts);
        }
        catch
        {
            // Ignore HLS errors
        }

        // Parse NAL units ONCE from AVCC format
        var nalUnits = ParseNalUnits(data, _nalLengthSize);

        // Update diagnostics
        _h264PacketCount++;
        if (isKeyframe) _h264KeyframeCount++;
        else _h264NonKeyframeCount++;

        // Measure incoming H.264 FPS (always runs, regardless of clients)
        var now = DateTime.UtcNow;
        if (_inputFpsWindowStart == DateTime.MinValue)
        {
            _inputFpsWindowStart = now;
            _inputFpsFrameCount = 0;
        }
        _inputFpsFrameCount++;

        var windowElapsed = (now - _inputFpsWindowStart).TotalMilliseconds;
        if (windowElapsed >= InputFpsWindowMs)
        {
            _measuredInputFps = (int)Math.Round(_inputFpsFrameCount * 1000.0 / windowElapsed);
            _inputFpsWindowStart = now;
            _inputFpsFrameCount = 0;
        }

        if (nalUnits.Count == 0)
        {
            _h264ParseFailCount++;
            if (_h264ParseFailCount <= 3)
            {
                var preview = data.Length >= 8
                    ? $"{data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2} {data[4]:X2} {data[5]:X2} {data[6]:X2} {data[7]:X2}"
                    : BitConverter.ToString(data);
                StatusChanged?.Invoke(this, $"H.264 parse failed: len={data.Length}, key={isKeyframe}, bytes=[{preview}]");
            }
            return;
        }

        // Log diagnostics every 30 seconds
        if ((DateTime.UtcNow - _lastH264DiagLog).TotalSeconds >= 30)
        {
            var currentNalTypes = nalUnits.Select(n => n.Length > 0 ? (n[0] & 0x1F) : 0).ToList();
            var nalTypeSummary = string.Join(",", currentNalTypes);
            var frameSize = nalUnits.Sum(n => n.Length);
            StatusChanged?.Invoke(this, $"H.264 stats: pkts={_h264PacketCount} key={_h264KeyframeCount} nonKey={_h264NonKeyframeCount} fail={_h264ParseFailCount}, nalSize={_nalLengthSize}, lastFrame=[{nalTypeSummary}] {frameSize}B");
            _h264PacketCount = 0;
            _h264KeyframeCount = 0;
            _h264NonKeyframeCount = 0;
            _h264ParseFailCount = 0;
            _lastH264DiagLog = DateTime.UtcNow;
        }

        // Send to FLV clients (uses AVCC data directly, no conversion needed)
        List<FlvStreamClient>? flvClients = null;
        lock (_flvClientLock)
        {
            if (_flvClients.Count > 0)
                flvClients = _flvClients.ToList();
        }

        if (flvClients != null)
        {
            foreach (var flvClient in flvClients)
            {
                try
                {
                    flvClient.PushPacket(data, isKeyframe);
                }
                catch
                {
                    RemoveFlvClient(flvClient);
                }
            }
        }

        // Check if we need Annex B frame (for keyframe caching or WebSocket clients)
        List<H264WebSocketClient>? wsClients = null;
        lock (_h264ClientLock)
        {
            if (_h264Clients.Count > 0)
                wsClients = _h264Clients.ToList();
        }

        // Only build Annex B frame if needed (keyframe for snapshot, or WebSocket clients)
        bool needsAnnexB = isKeyframe || wsClients != null;
        if (!needsAnnexB)
            return;

        // Build Annex B frame ONCE
        using var ms = new System.IO.MemoryStream();

        // Add SPS/PPS before keyframes
        if (isKeyframe && _spsNal != null && _ppsNal != null)
        {
            ms.Write(H264StartCode, 0, H264StartCode.Length);
            ms.Write(_spsNal, 0, _spsNal.Length);
            ms.Write(H264StartCode, 0, H264StartCode.Length);
            ms.Write(_ppsNal, 0, _ppsNal.Length);
        }

        // Add all NAL units
        foreach (var nal in nalUnits)
        {
            ms.Write(H264StartCode, 0, H264StartCode.Length);
            ms.Write(nal, 0, nal.Length);
        }

        var frameData = ms.ToArray();

        // Cache keyframe for /snapshot endpoint
        if (isKeyframe)
        {
            lock (_keyframeLock)
            {
                _lastKeyframe = frameData;
            }
        }

        // Send to WebSocket clients
        if (wsClients != null)
        {
            foreach (var client in wsClients)
            {
                try
                {
                    client.SendBinaryFrame(frameData);
                }
                catch
                {
                    RemoveH264Client(client);
                }
            }
        }
    }

    /// <summary>
    /// Parse NAL units from AVCC format (length-prefixed).
    /// FFmpeg outputs AVCC format for FLV/MP4 containers.
    /// </summary>
    /// <param name="data">Raw packet data</param>
    /// <param name="nalLengthSize">AVCC NAL unit length prefix size (1-4 bytes)</param>
    private static List<byte[]> ParseNalUnits(byte[] data, int nalLengthSize)
    {
        var nalUnits = new List<byte[]>();

        if (data.Length < nalLengthSize)
        {
            return nalUnits;
        }

        // FFmpeg always outputs AVCC format for FLV streams, so use length-prefixed parsing
        // Don't try to detect Annex B - it causes false positives when NAL length is 256-511
        bool isAnnexB = false;

        if (isAnnexB)
        {
            // Parse Annex B (start code separated)
            int i = 0;
            while (i < data.Length)
            {
                int startCodeLen = 0;
                if (i + 2 < data.Length && data[i] == 0 && data[i + 1] == 0)
                {
                    if (data[i + 2] == 1) startCodeLen = 3;
                    else if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1) startCodeLen = 4;
                }

                if (startCodeLen == 0) { i++; continue; }

                int nalStart = i + startCodeLen;
                i = nalStart;

                int nalEnd = data.Length;
                while (i < data.Length - 2)
                {
                    if (data[i] == 0 && data[i + 1] == 0 &&
                        (data[i + 2] == 1 || (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)))
                    {
                        nalEnd = i;
                        break;
                    }
                    i++;
                }

                if (nalEnd > nalStart)
                {
                    var nal = new byte[nalEnd - nalStart];
                    Array.Copy(data, nalStart, nal, 0, nal.Length);
                    nalUnits.Add(nal);
                }
            }
        }
        else
        {
            // Parse AVCC (variable-length big-endian length prefix)
            int offset = 0;
            while (offset + nalLengthSize <= data.Length)
            {
                // Read NAL length based on configured size
                int nalLength = 0;
                for (int i = 0; i < nalLengthSize; i++)
                {
                    nalLength = (nalLength << 8) | data[offset + i];
                }
                offset += nalLengthSize;

                if (nalLength <= 0 || nalLength > data.Length - offset) break;

                var nal = new byte[nalLength];
                Array.Copy(data, offset, nal, 0, nalLength);
                nalUnits.Add(nal);
                offset += nalLength;
            }
        }

        return nalUnits;
    }

    private void SendH264NalUnit(H264WebSocketClient client, byte[] nal)
    {
        try
        {
            // Send NAL unit with Annex B start code as binary WebSocket frame
            var payload = new byte[H264StartCode.Length + nal.Length];
            Array.Copy(H264StartCode, 0, payload, 0, H264StartCode.Length);
            Array.Copy(nal, 0, payload, H264StartCode.Length, nal.Length);

            client.SendBinaryFrame(payload);
        }
        catch
        {
            // Remove failed client
            RemoveH264Client(client);
        }
    }

    private void RemoveH264Client(H264WebSocketClient client)
    {
        bool removed;
        int remaining;
        lock (_h264ClientLock)
        {
            removed = _h264Clients.Remove(client);
            remaining = _h264Clients.Count;
        }
        if (removed)
        {
            client.Dispose();
            StatusChanged?.Invoke(this, $"H.264 WebSocket client disconnected (total: {remaining})");
        }
    }

    private void RemoveFlvClient(FlvStreamClient client)
    {
        bool removed;
        int remaining;
        lock (_flvClientLock)
        {
            removed = _flvClients.Remove(client);
            remaining = _flvClients.Count;
        }
        if (removed)
        {
            client.Dispose();
            StatusChanged?.Invoke(this, $"FLV client disconnected (total: {remaining})");
        }
    }

    // MJPEG boundary string
    private const string Boundary = "--mjpegboundary";
    private static readonly byte[] BoundaryBytes = Encoding.ASCII.GetBytes($"\r\n{Boundary}\r\n");
    private static readonly byte[] ContentTypeHeader = Encoding.ASCII.GetBytes("Content-Type: image/jpeg\r\n");

    /// <summary>
    /// Start the MJPEG server on a single address.
    /// </summary>
    public void Start(int port, IPAddress? bindAddress = null)
    {
        var addresses = new List<IPAddress> { bindAddress ?? IPAddress.Any };
        Start(port, addresses);
    }

    /// <summary>
    /// Start the MJPEG server on multiple addresses.
    /// </summary>
    public void Start(int port, List<IPAddress> bindAddresses)
    {
        if (IsRunning)
            Stop();

        Port = port;
        BindAddresses = bindAddresses.Count > 0 ? bindAddresses : new List<IPAddress> { IPAddress.Any };

        _cts = new CancellationTokenSource();

        try
        {
            // Create a listener for each address
            foreach (var address in BindAddresses)
            {
                var listener = new TcpListener(address, Port);
                listener.Start();
                _listeners.Add(listener);
                StatusChanged?.Invoke(this, $"MJPEG server started on {address}:{Port}");
            }

            IsRunning = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "MjpegAcceptThread"
            };
            _acceptThread.Start();
        }
        catch (Exception ex)
        {
            // Clean up any listeners that were started
            foreach (var listener in _listeners)
            {
                try { listener.Stop(); } catch { }
            }
            _listeners.Clear();
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

        // Stop all listeners
        foreach (var listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
        _listeners.Clear();

        IsRunning = false;

        // Dispose MJPEG clients
        lock (_clientLock)
        {
            foreach (var client in _clients.ToList())
            {
                client.Dispose();
            }
            _clients.Clear();
        }

        // Dispose H.264 WebSocket clients
        lock (_h264ClientLock)
        {
            foreach (var client in _h264Clients.ToList())
            {
                client.Dispose();
            }
            _h264Clients.Clear();
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
            var hasClients = ConnectedClients > 0 || H264WebSocketClients > 0 || HasExternalStreamingClient || HasHlsActivity;

            // Determine target FPS based on whether clients are connected
            // (includes MJPEG, H.264 WebSocket, external streaming clients like Obico/Janus, and HLS)
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

        while (!ct.IsCancellationRequested && _listeners.Count > 0)
        {
            try
            {
                var anyPending = false;

                // Check each listener for pending connections
                foreach (var listener in _listeners.ToArray())
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        if (listener.Pending())
                        {
                            anyPending = true;
                            var tcpClient = listener.AcceptTcpClient();
                            var client = new ClientConnection(tcpClient);
                            ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                        }
                    }
                    catch (SocketException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (InvalidOperationException)
                    {
                        // Listener was stopped
                        break;
                    }
                }

                if (!anyPending && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(10);
                }
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

            // Handle CORS preflight requests
            if (request.StartsWith("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                HandleOptionsRequest(client);
                return;
            }

            var (path, method, body, queryParams) = ParseRequest(request);

            // Check for WebSocket upgrade on /h264 endpoint
            if (path.Equals("/h264", StringComparison.OrdinalIgnoreCase) &&
                request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                // H.264 WebSocket not available in MJPEG source mode
                if (_mjpegSourceMode)
                {
                    SendServiceUnavailable(client, "H.264 streaming not available in MJPEG source mode");
                    return;
                }
                HandleH264WebSocketUpgrade(client, request);
                return;
            }

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

                case "/h264":
                    // Non-WebSocket request to /h264 - return info
                    HandleH264InfoRequest(client);
                    break;

                case "/flv":
                    if (_mjpegSourceMode)
                    {
                        SendServiceUnavailable(client, "FLV streaming not available in MJPEG source mode");
                        break;
                    }
                    HandleFlvStreamRequest(client);
                    break;

                case "/status":
                    HandleStatusRequest(client);
                    break;

                case "/led":
                    HandleLedRequest(client, method, body);
                    break;

                case "/led/on":
                    HandleLedOnRequest(client, method);
                    break;

                case "/led/off":
                    HandleLedOffRequest(client, method);
                    break;

                case "/hls/playlist.m3u8":
                    if (_mjpegSourceMode)
                    {
                        SendServiceUnavailable(client, "HLS not available in MJPEG source mode");
                        break;
                    }
                    HandleHlsPlaylistRequestAsync(client, queryParams);
                    break;

                case "/hls/legacy.m3u8":
                    if (_mjpegSourceMode)
                    {
                        SendServiceUnavailable(client, "HLS not available in MJPEG source mode");
                        break;
                    }
                    HandleHlsLegacyPlaylistRequest(client);
                    break;

                default:
                    // Handle HLS partial segment requests (LL-HLS)
                    if (path.StartsWith("/hls/part", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_mjpegSourceMode)
                        {
                            SendServiceUnavailable(client, "HLS not available in MJPEG source mode");
                            break;
                        }
                        HandleHlsPartRequest(client, path);
                        break;
                    }

                    // Handle legacy HLS segment requests (with PTS adjustment for VLC)
                    if (path.StartsWith("/hls/legacy-segment", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_mjpegSourceMode)
                        {
                            SendServiceUnavailable(client, "HLS not available in MJPEG source mode");
                            break;
                        }
                        HandleHlsLegacySegmentRequest(client, path);
                        break;
                    }

                    // Handle HLS full segment requests
                    if (path.StartsWith("/hls/segment", StringComparison.OrdinalIgnoreCase) &&
                        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_mjpegSourceMode)
                        {
                            SendServiceUnavailable(client, "HLS not available in MJPEG source mode");
                            break;
                        }
                        HandleHlsSegmentRequest(client, path);
                        break;
                    }

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
        // Send MJPEG header with CORS support for Mainsail/Fluidd
        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: multipart/x-mixed-replace; boundary=mjpegboundary\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Access-Control-Allow-Methods: GET, OPTIONS\r\n" +
                     "Access-Control-Allow-Headers: Content-Type\r\n" +
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

    /// <summary>
    /// Handle /snapshot request - decodes H.264 keyframe on-demand to JPEG.
    /// This avoids continuous MJPEG encoding, saving CPU.
    /// Rate-limited to 10fps max - multiple concurrent requests share the same decoded frame.
    /// </summary>
    private void HandleSnapshotRequest(ClientConnection client)
    {
        // Get the cached keyframe
        byte[]? keyframeData;
        bool isJpeg;
        lock (_keyframeLock)
        {
            keyframeData = _lastKeyframe;
            isJpeg = _lastKeyframeIsJpeg;
        }

        if (keyframeData == null)
        {
            // No keyframe available yet
            SnapshotRequested?.Invoke(this, EventArgs.Empty);
            SendServiceUnavailable(client);
            return;
        }

        byte[]? jpegData;

        if (isJpeg)
        {
            // MJPEG source mode - keyframe is already JPEG, use directly
            jpegData = keyframeData;
        }
        else
        {
            // H.264 source mode - need to decode keyframe to JPEG
            lock (_snapshotCacheLock)
            {
                var now = DateTime.UtcNow;
                var minInterval = TimeSpan.FromSeconds(1.0 / SnapshotMaxFps);
                var cacheValid = _cachedSnapshotJpeg != null &&
                                 (now - _lastSnapshotDecodeTime) < minInterval &&
                                 ReferenceEquals(_cachedSnapshotKeyframeSource, keyframeData);

                if (cacheValid)
                {
                    // Use cached JPEG - rate limited
                    jpegData = _cachedSnapshotJpeg;
                }
                else
                {
                    // Need to decode - either cache expired or keyframe changed
                    _snapshotDecoder.JpegQuality = JpegQuality;
                    jpegData = _snapshotDecoder.DecodeKeyframeToJpeg(keyframeData);

                    if (jpegData != null)
                    {
                        _cachedSnapshotJpeg = jpegData;
                        _cachedSnapshotKeyframeSource = keyframeData;
                        _lastSnapshotDecodeTime = now;
                    }
                }
            }
        }

        if (jpegData == null)
        {
            SendError(client, 500, "Failed to decode keyframe");
            return;
        }

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: image/jpeg\r\n" +
                     $"Content-Length: {jpegData.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Expires: 0\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(jpegData);
        client.Dispose();
    }

    private void HandleStatusRequest(ClientConnection client)
    {
        var status = new
        {
            running = IsRunning,
            clients = ConnectedClients + H264WebSocketClients + FlvClients,
            mjpegClients = ConnectedClients,
            h264Clients = H264WebSocketClients,
            flvClients = FlvClients,
            frameWidth = _frameWidth,
            frameHeight = _frameHeight,
            hasFrame = _lastJpegFrame != null,
            maxFps = MaxFps,
            idleFps = IdleFps,
            jpegQuality = JpegQuality,
            framesSkipped = _framesSkipped,
            measuredInputFps = _measuredInputFps,
            h264PacketCount = _h264PacketCount
        };

        var json = System.Text.Json.JsonSerializer.Serialize(status);
        var body = Encoding.UTF8.GetBytes(json);

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: application/json\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Expires: 0\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(body);
        client.Dispose();
    }

    private void HandleOptionsRequest(ClientConnection client)
    {
        var header = "HTTP/1.1 204 No Content\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                     "Access-Control-Allow-Headers: Content-Type\r\n" +
                     "Access-Control-Max-Age: 86400\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.Dispose();
    }

    private void SendNotFound(ClientConnection client)
    {
        var body = "Not Found";
        var header = "HTTP/1.1 404 Not Found\r\n" +
                     "Content-Type: text/plain\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
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
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header + body));
        client.Dispose();
    }

    /// <summary>
    /// Handle LED GET (status) and POST (control) requests.
    /// HomeAssistant-compatible format.
    /// </summary>
    private void HandleLedRequest(ClientConnection client, string method, string? body)
    {
        if (method == "GET")
        {
            // Query LED status
            HandleLedStatusRequest(client);
        }
        else if (method == "POST")
        {
            // Set LED state from body: {"state": "on"|"off"}
            HandleLedControlRequest(client, body);
        }
        else
        {
            SendMethodNotAllowed(client);
        }
    }

    private void HandleLedStatusRequest(ClientConnection client)
    {
        if (GetLedStatusAsync == null)
        {
            SendServiceUnavailable(client, "LED control not available");
            return;
        }

        try
        {
            var status = GetLedStatusAsync(CancellationToken.None).GetAwaiter().GetResult();
            var response = new
            {
                state = status?.IsOn == true ? "on" : "off",
                brightness = status?.Brightness ?? 0
            };

            SendJsonResponse(client, response);
        }
        catch (Exception ex)
        {
            SendError(client, 500, $"Failed to query LED: {ex.Message}");
        }
    }

    private void HandleLedControlRequest(ClientConnection client, string? body)
    {
        if (SetLedAsync == null)
        {
            SendServiceUnavailable(client, "LED control not available");
            return;
        }

        try
        {
            bool turnOn;

            if (string.IsNullOrEmpty(body))
            {
                SendBadRequest(client, "Missing request body");
                return;
            }

            // Parse body: {"state": "on"|"off"} or {"state": true|false}
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("state", out var stateEl))
            {
                if (stateEl.ValueKind == JsonValueKind.String)
                {
                    var stateStr = stateEl.GetString()?.ToLowerInvariant();
                    turnOn = stateStr == "on" || stateStr == "true" || stateStr == "1";
                }
                else if (stateEl.ValueKind == JsonValueKind.True || stateEl.ValueKind == JsonValueKind.False)
                {
                    turnOn = stateEl.GetBoolean();
                }
                else
                {
                    SendBadRequest(client, "Invalid state value");
                    return;
                }
            }
            else
            {
                SendBadRequest(client, "Missing 'state' field");
                return;
            }

            var success = SetLedAsync(turnOn, CancellationToken.None).GetAwaiter().GetResult();
            if (success)
            {
                var response = new { state = turnOn ? "on" : "off", success = true };
                SendJsonResponse(client, response);
            }
            else
            {
                SendError(client, 500, "Failed to set LED");
            }
        }
        catch (JsonException)
        {
            SendBadRequest(client, "Invalid JSON body");
        }
        catch (Exception ex)
        {
            SendError(client, 500, $"Failed to set LED: {ex.Message}");
        }
    }

    private void HandleLedOnRequest(ClientConnection client, string method)
    {
        if (method != "POST")
        {
            SendMethodNotAllowed(client);
            return;
        }

        if (SetLedAsync == null)
        {
            SendServiceUnavailable(client, "LED control not available");
            return;
        }

        try
        {
            var success = SetLedAsync(true, CancellationToken.None).GetAwaiter().GetResult();
            if (success)
            {
                var response = new { state = "on", success = true };
                SendJsonResponse(client, response);
            }
            else
            {
                SendError(client, 500, "Failed to turn LED on");
            }
        }
        catch (Exception ex)
        {
            SendError(client, 500, $"Failed to turn LED on: {ex.Message}");
        }
    }

    private void HandleLedOffRequest(ClientConnection client, string method)
    {
        if (method != "POST")
        {
            SendMethodNotAllowed(client);
            return;
        }

        if (SetLedAsync == null)
        {
            SendServiceUnavailable(client, "LED control not available");
            return;
        }

        try
        {
            var success = SetLedAsync(false, CancellationToken.None).GetAwaiter().GetResult();
            if (success)
            {
                var response = new { state = "off", success = true };
                SendJsonResponse(client, response);
            }
            else
            {
                SendError(client, 500, "Failed to turn LED off");
            }
        }
        catch (Exception ex)
        {
            SendError(client, 500, $"Failed to turn LED off: {ex.Message}");
        }
    }

    private void SendJsonResponse(ClientConnection client, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var bodyBytes = Encoding.UTF8.GetBytes(json);

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: application/json\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Expires: 0\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(bodyBytes);
        client.Dispose();
    }

    private void SendBadRequest(ClientConnection client, string message)
    {
        SendError(client, 400, message);
    }

    private void SendMethodNotAllowed(ClientConnection client)
    {
        SendError(client, 405, "Method Not Allowed");
    }

    private void SendServiceUnavailable(ClientConnection client, string message)
    {
        SendError(client, 503, message);
    }

    private void SendError(ClientConnection client, int statusCode, string message)
    {
        var statusText = statusCode switch
        {
            400 => "Bad Request",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "Error"
        };

        var json = JsonSerializer.Serialize(new { error = message });
        var bodyBytes = Encoding.UTF8.GetBytes(json);

        var header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                     "Content-Type: application/json\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Expires: 0\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(bodyBytes);
        client.Dispose();
    }

    private static string ParseRequestPath(string request)
    {
        var (path, _, _, _) = ParseRequest(request);
        return path;
    }

    private static (string Path, string Method, string? Body, Dictionary<string, string> QueryParams) ParseRequest(string request)
    {
        // Parse "GET /path?query=params HTTP/1.1"
        var lines = request.Split('\n');
        if (lines.Length == 0) return ("/", "GET", null, new Dictionary<string, string>());

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return ("/", "GET", null, new Dictionary<string, string>());

        var method = parts[0].ToUpperInvariant();
        var fullPath = parts[1];
        var path = fullPath;
        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Extract query string
        var queryIndex = fullPath.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = fullPath.Substring(0, queryIndex);
            var queryString = fullPath.Substring(queryIndex + 1);

            // Parse query parameters
            foreach (var param in queryString.Split('&'))
            {
                var eqIndex = param.IndexOf('=');
                if (eqIndex > 0)
                {
                    var key = Uri.UnescapeDataString(param.Substring(0, eqIndex));
                    var value = Uri.UnescapeDataString(param.Substring(eqIndex + 1));
                    queryParams[key] = value;
                }
                else if (param.Length > 0)
                {
                    queryParams[Uri.UnescapeDataString(param)] = "";
                }
            }
        }

        // Extract body (after empty line)
        string? body = null;
        var bodyIndex = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (bodyIndex >= 0 && bodyIndex + 4 < request.Length)
        {
            body = request.Substring(bodyIndex + 4).Trim();
        }
        else
        {
            bodyIndex = request.IndexOf("\n\n", StringComparison.Ordinal);
            if (bodyIndex >= 0 && bodyIndex + 2 < request.Length)
            {
                body = request.Substring(bodyIndex + 2).Trim();
            }
        }

        return (path, method, body, queryParams);
    }

    /// <summary>
    /// Handle WebSocket upgrade request for H.264 streaming.
    /// Performs WebSocket handshake and adds client to H.264 client list.
    /// </summary>
    private void HandleH264WebSocketUpgrade(ClientConnection client, string request)
    {
        try
        {
            // Extract Sec-WebSocket-Key from request headers
            string? webSocketKey = null;
            var lines = request.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                {
                    webSocketKey = line.Substring(18).Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(webSocketKey))
            {
                SendBadRequest(client, "Missing Sec-WebSocket-Key header");
                return;
            }

            // Compute accept key per RFC 6455
            var acceptKey = ComputeWebSocketAcceptKey(webSocketKey);

            // Send WebSocket handshake response
            var response = "HTTP/1.1 101 Switching Protocols\r\n" +
                          "Upgrade: websocket\r\n" +
                          "Connection: Upgrade\r\n" +
                          $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                          "Access-Control-Allow-Origin: *\r\n" +
                          "\r\n";

            if (!client.TrySend(Encoding.ASCII.GetBytes(response)))
            {
                client.Dispose();
                return;
            }

            // Create WebSocket client and add to list
            var wsClient = new H264WebSocketClient(client);

            lock (_h264ClientLock)
            {
                _h264Clients.Add(wsClient);
            }

            StatusChanged?.Invoke(this, $"H.264 WebSocket client connected (total: {H264WebSocketClients})");

            // Send cached keyframe immediately (includes SPS/PPS) so client can start decoding right away
            byte[]? cachedKeyframe;
            lock (_keyframeLock)
            {
                cachedKeyframe = _lastKeyframe;
            }

            if (cachedKeyframe != null)
            {
                // Cached keyframe already has SPS/PPS prepended
                try
                {
                    wsClient.SendBinaryFrame(cachedKeyframe);
                }
                catch
                {
                    // Client disconnected during initial send
                    RemoveH264Client(wsClient);
                    return;
                }
            }
            else if (_spsNal != null && _ppsNal != null)
            {
                // No keyframe cached yet, just send SPS/PPS
                SendH264NalUnit(wsClient, _spsNal);
                SendH264NalUnit(wsClient, _ppsNal);
            }

            // Start ping/pong thread to keep connection alive and detect disconnects
            ThreadPool.QueueUserWorkItem(_ => MonitorH264WebSocketClient(wsClient));
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new Exception($"WebSocket handshake failed: {ex.Message}", ex));
            client.Dispose();
        }
    }

    /// <summary>
    /// Compute WebSocket accept key per RFC 6455.
    /// </summary>
    private static string ComputeWebSocketAcceptKey(string key)
    {
        const string guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        var combined = key + guid;
        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(combined));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Monitor H.264 WebSocket client connection.
    /// Reads from socket to detect close frames and disconnects.
    /// </summary>
    private void MonitorH264WebSocketClient(H264WebSocketClient client)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            // Read loop to detect disconnects and handle control frames
            while (!ct.IsCancellationRequested && client.IsConnected)
            {
                // Try to read WebSocket frames (will block until data or disconnect)
                // This also handles ping/pong and close frames
                if (!client.ProcessIncomingFrames())
                {
                    break; // Client disconnected or close frame received
                }
            }
        }
        catch
        {
            // Connection error
        }
        finally
        {
            RemoveH264Client(client);
        }
    }

    /// <summary>
    /// Handle non-WebSocket request to /h264 endpoint.
    /// Returns info about the H.264 WebSocket endpoint.
    /// </summary>
    private void HandleH264InfoRequest(ClientConnection client)
    {
        var info = new
        {
            endpoint = "/h264",
            protocol = "WebSocket",
            description = "H.264 Annex B stream for Mainsail/Fluidd jmuxer",
            usage = "Connect with WebSocket client to ws://<host>:<port>/h264",
            format = "Binary frames containing H.264 NAL units with Annex B start codes",
            connectedClients = H264WebSocketClients,
            hasSps = _spsNal != null,
            hasPps = _ppsNal != null
        };

        SendJsonResponse(client, info);
    }

    #region FLV Streaming

    /// <summary>
    /// Handle /flv endpoint - continuous FLV stream compatible with Anycubic slicer.
    /// Uses Content-Type: text/plain and large Content-Length to match gkcam format.
    /// </summary>
    private void HandleFlvStreamRequest(ClientConnection client)
    {
        // Create per-client FLV muxer and stream client
        var muxer = new FlvMuxer(_frameWidth, _frameHeight, _measuredInputFps > 0 ? _measuredInputFps : 15);
        var flvClient = new FlvStreamClient(client, muxer, _spsNal, _ppsNal, _nalLengthSize);

        // Send HTTP response header (matches gkcam format)
        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: text/plain\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Content-Length: 99999999999\r\n" +
                     "\r\n";

        if (!client.TrySend(Encoding.ASCII.GetBytes(header)))
        {
            client.Dispose();
            return;
        }

        // Send FLV header + metadata
        if (!client.TrySend(FlvMuxer.CreateHeader()) ||
            !client.TrySend(muxer.CreateMetadataTag()))
        {
            client.Dispose();
            return;
        }

        // Send AVC decoder config immediately if SPS/PPS available
        if (_spsNal != null && _ppsNal != null)
        {
            var configTag = muxer.CreateDecoderConfigTag(_spsNal, _ppsNal);
            if (!client.TrySend(configTag))
            {
                client.Dispose();
                return;
            }
        }

        // Register client for receiving H.264 packets
        lock (_flvClientLock)
        {
            _flvClients.Add(flvClient);
        }

        StatusChanged?.Invoke(this, $"FLV client connected (total: {FlvClients})");

        // Monitor connection in background thread
        ThreadPool.QueueUserWorkItem(_ => MonitorFlvClient(flvClient));
    }

    /// <summary>
    /// Monitor FLV client connection. Detects disconnection via periodic send checks.
    /// </summary>
    private void MonitorFlvClient(FlvStreamClient client)
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        try
        {
            while (!ct.IsCancellationRequested && client.IsConnected)
            {
                Thread.Sleep(1000);

                // Check if client is still alive by checking the underlying socket
                if (!client.IsConnected)
                    break;
            }
        }
        catch
        {
            // Connection error
        }
        finally
        {
            RemoveFlvClient(client);
        }
    }

    #endregion

    #region HLS Streaming

    /// <summary>
    /// Handle HLS playlist request with optional LL-HLS blocking support.
    /// </summary>
    private async void HandleHlsPlaylistRequestAsync(ClientConnection client, Dictionary<string, string> queryParams)
    {
        try
        {
            // Mark HLS activity for frame rate control
            MarkHlsActivity();
            // Check for LL-HLS blocking request parameters
            int requestedMsn = -1;
            int requestedPart = -1;

            if (queryParams.TryGetValue("_HLS_msn", out var msnStr) && int.TryParse(msnStr, out var msn))
            {
                requestedMsn = msn;
            }

            if (queryParams.TryGetValue("_HLS_part", out var partStr) && int.TryParse(partStr, out var part))
            {
                requestedPart = part;
            }

            // If this is a blocking request, wait for the requested part
            if (requestedMsn >= 0 && requestedPart >= 0 && _hlsService.LlHlsEnabled)
            {
                // Wait up to 30 seconds for the requested part
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var available = await _hlsService.WaitForPartAsync(requestedMsn, requestedPart, cts.Token);

                if (!available)
                {
                    // Timeout or cancelled - return current playlist anyway
                    StatusChanged?.Invoke(this, $"LL-HLS: Blocking request timeout for MSN={requestedMsn}, Part={requestedPart}");
                }
            }

            if (!_hlsService.IsReady)
            {
                SendServiceUnavailable(client, "HLS stream not ready - waiting for keyframe");
                return;
            }

            var playlist = _hlsService.GetPlaylist(requestedMsn, requestedPart);
            var body = Encoding.UTF8.GetBytes(playlist);

            var header = "HTTP/1.1 200 OK\r\n" +
                         "Content-Type: application/vnd.apple.mpegurl\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                         "Pragma: no-cache\r\n" +
                         "Expires: 0\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Connection: close\r\n" +
                         "\r\n";

            client.TrySend(Encoding.ASCII.GetBytes(header));
            client.TrySend(body);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new Exception($"HLS playlist error: {ex.Message}", ex));
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Handle legacy HLS playlist request for VLC and other players that don't support LL-HLS.
    /// </summary>
    private void HandleHlsLegacyPlaylistRequest(ClientConnection client)
    {
        try
        {
            // Mark HLS activity for frame rate control
            MarkHlsActivity();

            var playlist = _hlsService.GetLegacyPlaylist();
            var body = Encoding.UTF8.GetBytes(playlist);

            var header = "HTTP/1.1 200 OK\r\n" +
                         "Content-Type: application/vnd.apple.mpegurl\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                         "Pragma: no-cache\r\n" +
                         "Expires: 0\r\n" +
                         "Access-Control-Allow-Origin: *\r\n" +
                         "Connection: close\r\n" +
                         "\r\n";

            client.TrySend(Encoding.ASCII.GetBytes(header));
            client.TrySend(body);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new Exception($"HLS legacy playlist error: {ex.Message}", ex));
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Handle LL-HLS partial segment request.
    /// </summary>
    private void HandleHlsPartRequest(ClientConnection client, string path)
    {
        // Mark HLS activity for frame rate control
        MarkHlsActivity();

        // Parse segment number and part index from path: /hls/part-{sessionId}-{MSN}.{PartIndex}.ts
        var match = System.Text.RegularExpressions.Regex.Match(path, @"/hls/part-\d+-(\d+)\.(\d+)\.ts", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            SendNotFound(client);
            return;
        }

        if (!int.TryParse(match.Groups[1].Value, out var msn) ||
            !int.TryParse(match.Groups[2].Value, out var partIndex))
        {
            SendNotFound(client);
            return;
        }

        var part = _hlsService.GetPart(msn, partIndex);
        if (part == null)
        {
            SendNotFound(client);
            return;
        }

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: video/mp2t\r\n" +
                     $"Content-Length: {part.Data.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Expires: 0\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(part.Data);
        client.Dispose();
    }

    /// <summary>
    /// Handle HLS full segment request.
    /// </summary>
    private void HandleHlsSegmentRequest(ClientConnection client, string path)
    {
        // Mark HLS activity for frame rate control
        MarkHlsActivity();

        // Parse segment number from path: /hls/segment-{sessionId}-{N}.ts
        var match = System.Text.RegularExpressions.Regex.Match(path, @"/hls/segment-\d+-(\d+)\.ts", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            SendNotFound(client);
            return;
        }

        if (!int.TryParse(match.Groups[1].Value, out var segmentNumber))
        {
            SendNotFound(client);
            return;
        }

        var segment = _hlsService.GetSegment(segmentNumber);
        if (segment == null)
        {
            SendNotFound(client);
            return;
        }

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: video/mp2t\r\n" +
                     $"Content-Length: {segment.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Expires: 0\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(segment);
        client.Dispose();
    }

    /// <summary>
    /// Handle legacy HLS segment request with PTS adjustment for VLC compatibility.
    /// </summary>
    private void HandleHlsLegacySegmentRequest(ClientConnection client, string path)
    {
        // Mark HLS activity for frame rate control
        MarkHlsActivity();

        // Parse segment number from path: /hls/legacy-segment-{sessionId}-{N}.ts
        var match = System.Text.RegularExpressions.Regex.Match(path, @"/hls/legacy-segment-\d+-(\d+)\.ts", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            SendNotFound(client);
            return;
        }

        if (!int.TryParse(match.Groups[1].Value, out var segmentNumber))
        {
            SendNotFound(client);
            return;
        }

        // Get segment with adjusted PTS values for legacy players
        var segment = _hlsService.GetLegacySegment(segmentNumber);
        if (segment == null)
        {
            SendNotFound(client);
            return;
        }

        var header = "HTTP/1.1 200 OK\r\n" +
                     "Content-Type: video/mp2t\r\n" +
                     $"Content-Length: {segment.Length}\r\n" +
                     "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                     "Pragma: no-cache\r\n" +
                     "Expires: 0\r\n" +
                     "Access-Control-Allow-Origin: *\r\n" +
                     "Connection: close\r\n" +
                     "\r\n";

        client.TrySend(Encoding.ASCII.GetBytes(header));
        client.TrySend(segment);
        client.Dispose();
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _hlsService.Dispose();
        _snapshotDecoder.Dispose();
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
        public NetworkStream Stream => _stream;

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

        /// <summary>
        /// Check if the underlying TCP socket is still connected.
        /// Uses Socket.Poll to detect remote disconnection even when no data is being sent.
        /// </summary>
        public bool IsSocketAlive()
        {
            if (_disposed) return false;
            try
            {
                var socket = _tcpClient.Client;
                if (socket == null || !socket.Connected) return false;
                // Poll: if readable AND no data available, peer has disconnected
                return !(socket.Poll(0, System.Net.Sockets.SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
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

    /// <summary>
    /// Represents a connected H.264 WebSocket client.
    /// Sends H.264 NAL units as binary WebSocket frames.
    /// </summary>
    private class H264WebSocketClient : IDisposable
    {
        private readonly ClientConnection _connection;
        private readonly object _sendLock = new();
        private bool _disposed;

        public bool IsConnected => !_disposed;

        public H264WebSocketClient(ClientConnection connection)
        {
            _connection = connection;
        }

        /// <summary>
        /// Send binary data as a WebSocket frame.
        /// </summary>
        public void SendBinaryFrame(byte[] data)
        {
            if (_disposed) return;

            lock (_sendLock)
            {
                try
                {
                    var frame = CreateWebSocketFrame(data, 0x02); // Binary frame opcode
                    _connection.Stream.Write(frame, 0, frame.Length);
                }
                catch
                {
                    _disposed = true;
                    throw;
                }
            }
        }

        /// <summary>
        /// Send a WebSocket ping frame to check connection.
        /// </summary>
        public bool SendPing()
        {
            if (_disposed) return false;

            lock (_sendLock)
            {
                try
                {
                    var frame = CreateWebSocketFrame(Array.Empty<byte>(), 0x09); // Ping opcode
                    _connection.Stream.Write(frame, 0, frame.Length);
                    return true;
                }
                catch
                {
                    _disposed = true;
                    return false;
                }
            }
        }

        /// <summary>
        /// Process incoming WebSocket frames (ping, pong, close).
        /// Returns false if connection should be closed.
        /// </summary>
        public bool ProcessIncomingFrames()
        {
            if (_disposed) return false;

            try
            {
                var stream = _connection.Stream;

                // Try to read with timeout - this will detect disconnects
                // The socket ReceiveTimeout is 5 seconds
                var header = new byte[2];
                int bytesRead;

                try
                {
                    bytesRead = stream.Read(header, 0, 2);
                }
                catch (IOException)
                {
                    // Timeout or disconnect - send ping to check connection
                    if (_disposed) return false;
                    return SendPing();
                }

                if (bytesRead == 0)
                {
                    // Connection closed
                    _disposed = true;
                    return false;
                }

                if (bytesRead < 2)
                {
                    _disposed = true;
                    return false;
                }

                var opcode = header[0] & 0x0F;
                var masked = (header[1] & 0x80) != 0;
                var payloadLen = header[1] & 0x7F;

                // Read extended length if needed
                if (payloadLen == 126)
                {
                    var extLen = new byte[2];
                    stream.Read(extLen, 0, 2);
                    payloadLen = (extLen[0] << 8) | extLen[1];
                }
                else if (payloadLen == 127)
                {
                    var extLen = new byte[8];
                    stream.Read(extLen, 0, 8);
                    payloadLen = 0; // Just skip large payloads for control frames
                }

                // Read masking key if present (client frames are masked)
                if (masked)
                {
                    var maskKey = new byte[4];
                    stream.Read(maskKey, 0, 4);
                }

                // Read and discard payload (we don't process client data)
                if (payloadLen > 0 && payloadLen < 65536)
                {
                    var payload = new byte[payloadLen];
                    var totalRead = 0;
                    while (totalRead < payloadLen)
                    {
                        var read = stream.Read(payload, totalRead, payloadLen - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }
                }

                // Handle control frames
                switch (opcode)
                {
                    case 0x08: // Close
                        _disposed = true;
                        return false;

                    case 0x09: // Ping - send pong
                        lock (_sendLock)
                        {
                            var pong = CreateWebSocketFrame(Array.Empty<byte>(), 0x0A);
                            stream.Write(pong, 0, pong.Length);
                        }
                        break;

                    case 0x0A: // Pong - ignore
                        break;
                }

                return true;
            }
            catch
            {
                _disposed = true;
                return false;
            }
        }

        /// <summary>
        /// Create a WebSocket frame with the given payload and opcode.
        /// </summary>
        private static byte[] CreateWebSocketFrame(byte[] payload, byte opcode)
        {
            // WebSocket frame format:
            // Byte 0: FIN (1) + RSV (000) + Opcode (4 bits)
            // Byte 1: MASK (0 for server) + Payload length (7 bits or extended)
            // Extended length if needed
            // Payload data

            int headerLength;
            if (payload.Length < 126)
            {
                headerLength = 2;
            }
            else if (payload.Length <= 65535)
            {
                headerLength = 4;
            }
            else
            {
                headerLength = 10;
            }

            var frame = new byte[headerLength + payload.Length];

            // FIN bit (1) + opcode
            frame[0] = (byte)(0x80 | opcode);

            // Payload length (server doesn't mask)
            if (payload.Length < 126)
            {
                frame[1] = (byte)payload.Length;
            }
            else if (payload.Length <= 65535)
            {
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)(payload.Length & 0xFF);
            }
            else
            {
                frame[1] = 127;
                var len = (ulong)payload.Length;
                for (int i = 0; i < 8; i++)
                {
                    frame[9 - i] = (byte)(len & 0xFF);
                    len >>= 8;
                }
            }

            // Copy payload
            Array.Copy(payload, 0, frame, headerLength, payload.Length);

            return frame;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _connection.Dispose();
        }
    }

    /// <summary>
    /// Represents a connected FLV streaming client.
    /// Receives H.264 packets in AVCC format and sends them as FLV video tags.
    /// Only sends P-frames after a keyframe has been delivered.
    /// </summary>
    private class FlvStreamClient : IDisposable
    {
        private readonly ClientConnection _connection;
        private readonly FlvMuxer _muxer;
        private readonly object _sendLock = new();
        private bool _disposed;
        private bool _sentKeyframe;
        private byte[]? _spsNal;
        private byte[]? _ppsNal;
        private readonly int _nalLengthSize;

        public bool IsConnected => !_disposed && _connection.IsSocketAlive();

        public FlvStreamClient(ClientConnection connection, FlvMuxer muxer, byte[]? spsNal, byte[]? ppsNal, int nalLengthSize = 4)
        {
            _connection = connection;
            _muxer = muxer;
            _spsNal = spsNal;
            _ppsNal = ppsNal;
            _nalLengthSize = nalLengthSize;
        }

        /// <summary>
        /// Push an AVCC H.264 packet to this FLV client.
        /// Skips P-frames until a keyframe has been sent.
        /// </summary>
        public void PushPacket(byte[] avccData, bool isKeyframe)
        {
            if (_disposed) return;

            // Skip P-frames until we've sent a keyframe
            if (!_sentKeyframe && !isKeyframe)
                return;

            // If we haven't sent decoder config yet, try to send it on keyframe
            if (!_muxer.HasDecoderConfig)
            {
                if (isKeyframe && _spsNal != null && _ppsNal != null)
                {
                    var configTag = _muxer.CreateDecoderConfigTag(_spsNal, _ppsNal);
                    lock (_sendLock)
                    {
                        if (!_connection.TrySend(configTag))
                        {
                            _disposed = true;
                            return;
                        }
                    }
                }
                else
                {
                    // Skip all video data until decoder config is sent
                    return;
                }
            }

            // Mux and send (filters out SPS/PPS NALs)
            var flvTag = _muxer.MuxAvccPacket(avccData, isKeyframe, _nalLengthSize);
            if (flvTag == null)
                return; // No video NALs in this packet (SPS/PPS only)

            lock (_sendLock)
            {
                if (!_connection.TrySend(flvTag))
                {
                    _disposed = true;
                    return;
                }
            }

            if (isKeyframe)
                _sentKeyframe = true;
        }

        /// <summary>
        /// Update SPS/PPS NALs (called when encoder parameters change).
        /// </summary>
        public void UpdateParameters(byte[]? sps, byte[]? pps)
        {
            _spsNal = sps;
            _ppsNal = pps;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _connection.Dispose();
        }
    }
}
