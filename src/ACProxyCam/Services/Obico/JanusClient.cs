// JanusClient.cs - Manages connection to Janus WebRTC gateway

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ACProxyCam.Models;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Manages connection to Janus WebRTC gateway (typically on Obico server).
/// Supports both H.264 video streaming and MJPEG data channel streaming.
/// </summary>
public class JanusClient : IDisposable
{
    private readonly string _janusServer;
    private readonly int _janusWsPort;
    private readonly ObicoStreamMode _streamMode;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _keepAliveTask;

    // Janus session/handle state
    private long? _sessionId;
    private long? _handleId;
    private int _streamId;

    // Port assignments (assigned by Janus when creating mountpoint)
    private int _videoPort;      // H.264 RTP video port
    private int _videoRtcpPort;  // H.264 RTCP port
    private int _dataPort;       // Data channel port (for MJPEG or bidirectional data)

    private volatile bool _isConnected;
    private volatile bool _mountpointReady;
    private volatile bool _isDisposed;

    // For synchronizing request/response
    private readonly Dictionary<string, TaskCompletionSource<JsonNode>> _pendingRequests = new();
    private readonly object _requestLock = new();
    private int _transactionCounter;

    // WebRTC connection state tracking
    private volatile bool _webrtcUp;

    // Keepalive error suppression - avoid log spam when connection is down
    private int _consecutiveKeepaliveErrors;

    // Verbose logging for debugging
    public bool Verbose { get; set; }

    // Default Janus ports (as used by moonraker-obico)
    public const int DEFAULT_JANUS_WS_PORT = 8188;  // Standard Janus WebSocket port

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<JsonNode>? JanusMessageReceived;
    /// <summary>
    /// Fired when WebRTC connection state changes.
    /// True = WebRTC up (browser connected), False = hangup (browser disconnected).
    /// Used to pause/resume UDP streaming to avoid broken frames during reconnection.
    /// </summary>
    public event EventHandler<bool>? WebRtcStateChanged;

    public bool IsConnected => _isConnected && _mountpointReady;
    public string JanusServer => _janusServer;
    public int StreamId => _streamId;
    public ObicoStreamMode StreamMode => _streamMode;

    /// <summary>
    /// H.264 RTP video port (only valid when StreamMode is H264).
    /// </summary>
    public int VideoPort => _videoPort;

    /// <summary>
    /// Data channel port (for MJPEG frames or bidirectional data).
    /// </summary>
    public int DataPort => _dataPort;

    /// <summary>
    /// Whether WebRTC data channel is currently active (browser connected).
    /// </summary>
    public bool IsWebRtcUp => _webrtcUp;

    /// <summary>
    /// Create a Janus client to connect to a remote Janus server.
    /// </summary>
    /// <param name="janusServer">Janus server hostname or IP</param>
    /// <param name="streamMode">Streaming mode (H264 or MJPEG)</param>
    /// <param name="janusWsPort">Janus WebSocket port (default: 8188)</param>
    public JanusClient(string janusServer, ObicoStreamMode streamMode = ObicoStreamMode.H264, int janusWsPort = DEFAULT_JANUS_WS_PORT)
    {
        _janusServer = janusServer;
        _janusWsPort = janusWsPort;
        _streamMode = streamMode;
    }

    /// <summary>
    /// Connect to the remote Janus server and create streaming mountpoint.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Log($"Connecting to Janus server at {_janusServer}:{_janusWsPort}...");

        // Connect to Janus WebSocket with retries
        var connected = false;
        var maxAttempts = 5;
        for (int i = 0; i < maxAttempts && !connected; i++)
        {
            try
            {
                await ConnectWebSocketAsync(_cts.Token);
                connected = true;
            }
            catch (Exception ex) when (i < maxAttempts - 1)
            {
                Log($"Connection attempt {i + 1} failed: {ex.Message}, retrying...");
                await Task.Delay(2000, _cts.Token);
            }
        }

        if (!connected)
        {
            throw new InvalidOperationException($"Failed to connect to Janus server at {_janusServer}:{_janusWsPort}");
        }

        // Create Janus session
        await CreateSessionAsync(_cts.Token);

        // Attach to streaming plugin
        await AttachToStreamingPluginAsync(_cts.Token);

        // Create mountpoint based on streaming mode
        if (_streamMode == ObicoStreamMode.H264)
        {
            await CreateH264MountpointAsync(_cts.Token);
        }
        else
        {
            await CreateMjpegMountpointAsync(_cts.Token);
        }

        // Start keepalive task
        _keepAliveTask = Task.Run(() => KeepAliveLoopAsync(_cts.Token), _cts.Token);

        _mountpointReady = true;

        if (_streamMode == ObicoStreamMode.H264)
        {
            Log($"Janus H.264 streaming ready: stream_id={_streamId}, video_port={_videoPort}, data_port={_dataPort}");
        }
        else
        {
            Log($"Janus MJPEG streaming ready: stream_id={_streamId}, data_port={_dataPort}");
        }
    }

    /// <summary>
    /// Disconnect from Janus server and destroy mountpoint.
    /// </summary>
    public async Task StopAsync()
    {
        _mountpointReady = false;
        _cts?.Cancel();

        // Try to destroy the mountpoint gracefully
        if (_sessionId.HasValue && _handleId.HasValue && _streamId > 0)
        {
            try
            {
                await DestroyMountpointAsync(CancellationToken.None);
            }
            catch { /* Ignore cleanup errors */ }
        }

        if (_receiveTask != null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException) { }
        }

        if (_keepAliveTask != null)
        {
            try { await _keepAliveTask; }
            catch (OperationCanceledException) { }
        }

        await DisconnectWebSocketAsync();
        Log("Disconnected from Janus");
    }

    /// <summary>
    /// Send a message to Janus (for signaling relay).
    /// </summary>
    public async Task SendToJanusAsync(string message, CancellationToken ct = default)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            Log("Cannot send to Janus - WebSocket not connected");
            return;
        }

        try
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, ct);
            if (Verbose)
                Log($"Sent to Janus: {message.Substring(0, Math.Min(100, message.Length))}...");
        }
        catch (Exception ex)
        {
            Log($"Failed to send to Janus: {ex.Message}");
        }
    }

    #region Janus Protocol

    private async Task CreateSessionAsync(CancellationToken ct)
    {
        Log("Creating Janus session...");
        var response = await SendJanusRequestAsync(new JsonObject
        {
            ["janus"] = "create"
        }, ct);

        var data = response["data"];
        _sessionId = data?["id"]?.GetValue<long>();
        if (!_sessionId.HasValue)
            throw new InvalidOperationException("Failed to create Janus session - no session ID returned");

        Log($"Created Janus session: {_sessionId}");
    }

    private async Task AttachToStreamingPluginAsync(CancellationToken ct)
    {
        if (!_sessionId.HasValue)
            throw new InvalidOperationException("No Janus session");

        Log("Attaching to streaming plugin...");
        var response = await SendJanusRequestAsync(new JsonObject
        {
            ["janus"] = "attach",
            ["session_id"] = _sessionId.Value,
            ["plugin"] = "janus.plugin.streaming"
        }, ct);

        var data = response["data"];
        _handleId = data?["id"]?.GetValue<long>();
        if (!_handleId.HasValue)
            throw new InvalidOperationException("Failed to attach to streaming plugin - no handle ID returned");

        Log($"Attached to streaming plugin: handle={_handleId}");
    }

    /// <summary>
    /// Create H.264 video mountpoint with data channel.
    /// This is the preferred mode - passes H.264 directly from camera to browser.
    /// </summary>
    private async Task CreateH264MountpointAsync(CancellationToken ct)
    {
        if (!_sessionId.HasValue || !_handleId.HasValue)
            throw new InvalidOperationException("No Janus session/handle");

        Log("Creating H.264 video mountpoint...");

        // Generate a unique ID for this mountpoint
        var mountpointId = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100000) + 10000;

        var response = await SendJanusRequestAsync(new JsonObject
        {
            ["janus"] = "message",
            ["session_id"] = _sessionId.Value,
            ["handle_id"] = _handleId.Value,
            ["body"] = new JsonObject
            {
                ["request"] = "create",
                ["type"] = "rtp",
                ["id"] = mountpointId,
                ["name"] = $"acproxycam-h264-{mountpointId}",
                ["description"] = "ACProxyCam H.264 WebRTC stream",
                // Audio disabled
                ["audio"] = false,
                // Video enabled - H.264 over RTP
                ["video"] = true,
                ["videoport"] = 0,      // Let Janus assign port
                ["videortcpport"] = 0,  // Let Janus assign port
                ["videopt"] = 96,       // RTP payload type for H.264
                ["videortpmap"] = "H264/90000",
                ["videofmtp"] = "profile-level-id=42e01f;packetization-mode=1",
                ["videobufferkf"] = true,  // Buffer keyframes for late joiners
                // Data channel for bidirectional communication
                ["data"] = true,
                ["dataport"] = 0,       // Let Janus assign port
                ["datatype"] = "binary",
                ["databuffermsg"] = false
            }
        }, ct);

        // Get the streaming response from plugindata
        var pluginData = response["plugindata"]?["data"];
        var streaming = pluginData?["streaming"]?.GetValue<string>();

        if (streaming == "created")
        {
            _streamId = pluginData?["stream"]?["id"]?.GetValue<int>() ?? mountpointId;

            // Query info to get assigned ports
            await QueryMountpointPortsAsync(ct);

            Log($"Created H.264 mountpoint: id={_streamId}, video_port={_videoPort}, data_port={_dataPort}");
        }
        else
        {
            var error = pluginData?["error"]?.GetValue<string>() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to create H.264 mountpoint: {error}");
        }
    }

    /// <summary>
    /// Create MJPEG data-only mountpoint.
    /// Fallback mode - sends base64-encoded JPEG over data channel.
    /// </summary>
    private async Task CreateMjpegMountpointAsync(CancellationToken ct)
    {
        if (!_sessionId.HasValue || !_handleId.HasValue)
            throw new InvalidOperationException("No Janus session/handle");

        Log("Creating MJPEG data mountpoint...");

        // Generate a unique ID for this mountpoint
        var mountpointId = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 100000) + 20000;

        var response = await SendJanusRequestAsync(new JsonObject
        {
            ["janus"] = "message",
            ["session_id"] = _sessionId.Value,
            ["handle_id"] = _handleId.Value,
            ["body"] = new JsonObject
            {
                ["request"] = "create",
                ["type"] = "rtp",
                ["id"] = mountpointId,
                ["name"] = $"acproxycam-mjpeg-{mountpointId}",
                ["description"] = "ACProxyCam MJPEG WebRTC stream",
                ["audio"] = false,
                ["video"] = false,
                ["data"] = true,
                ["dataport"] = 0,  // Let Janus assign an available port
                ["datatype"] = "binary",
                ["databuffermsg"] = false
            }
        }, ct);

        // Get the streaming response from plugindata
        var pluginData = response["plugindata"]?["data"];
        var streaming = pluginData?["streaming"]?.GetValue<string>();

        if (streaming == "created")
        {
            _streamId = pluginData?["stream"]?["id"]?.GetValue<int>() ?? mountpointId;

            // Query info to get assigned port
            await QueryMountpointPortsAsync(ct);

            Log($"Created MJPEG mountpoint: id={_streamId}, data_port={_dataPort}");
        }
        else
        {
            var error = pluginData?["error"]?.GetValue<string>() ?? "Unknown error";
            throw new InvalidOperationException($"Failed to create MJPEG mountpoint: {error}");
        }
    }

    /// <summary>
    /// Query mountpoint info to get assigned ports.
    /// </summary>
    private async Task QueryMountpointPortsAsync(CancellationToken ct)
    {
        if (!_sessionId.HasValue || !_handleId.HasValue || _streamId <= 0)
            return;

        var response = await SendJanusRequestAsync(new JsonObject
        {
            ["janus"] = "message",
            ["session_id"] = _sessionId.Value,
            ["handle_id"] = _handleId.Value,
            ["body"] = new JsonObject
            {
                ["request"] = "info",
                ["id"] = _streamId
            }
        }, ct);

        // Get ports from media array: plugindata.data.info.media[]
        var info = response["plugindata"]?["data"]?["info"];
        var media = info?["media"];
        if (media is JsonArray mediaArray)
        {
            foreach (var item in mediaArray)
            {
                var type = item?["type"]?.GetValue<string>();
                var port = item?["port"]?.GetValue<int>() ?? 0;

                if (type == "video" && port > 0)
                {
                    _videoPort = port;
                    // RTCP port is usually video port + 1
                    _videoRtcpPort = port + 1;
                    Log($"H.264 video port: {_videoPort}");
                }
                else if (type == "data" && port > 0)
                {
                    _dataPort = port;
                    Log($"Data channel port: {_dataPort}");
                }
            }
        }
    }

    private async Task DestroyMountpointAsync(CancellationToken ct)
    {
        if (!_sessionId.HasValue || !_handleId.HasValue || _streamId <= 0)
            return;

        Log($"Destroying mountpoint {_streamId}...");

        try
        {
            await SendJanusRequestAsync(new JsonObject
            {
                ["janus"] = "message",
                ["session_id"] = _sessionId.Value,
                ["handle_id"] = _handleId.Value,
                ["body"] = new JsonObject
                {
                    ["request"] = "destroy",
                    ["id"] = _streamId
                }
            }, ct, timeout: TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log($"Error destroying mountpoint: {ex.Message}");
        }
    }

    private async Task SendKeepAliveAsync(CancellationToken ct)
    {
        if (!_sessionId.HasValue)
            return;

        try
        {
            await SendJanusRequestAsync(new JsonObject
            {
                ["janus"] = "keepalive",
                ["session_id"] = _sessionId.Value
            }, ct, timeout: TimeSpan.FromSeconds(5));

            // Reset error counter on success
            _consecutiveKeepaliveErrors = 0;
        }
        catch (Exception ex)
        {
            _consecutiveKeepaliveErrors++;

            // Only log the first error to avoid spam when connection is down
            if (_consecutiveKeepaliveErrors == 1)
            {
                Log($"Keepalive error: {ex.Message}");
            }
        }
    }

    private async Task<JsonNode> SendJanusRequestAsync(JsonObject request, CancellationToken ct, TimeSpan? timeout = null)
    {
        var transaction = $"t{Interlocked.Increment(ref _transactionCounter)}";
        request["transaction"] = transaction;

        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_requestLock)
        {
            _pendingRequests[transaction] = tcs;
        }

        try
        {
            var json = request.ToJsonString();
            var buffer = Encoding.UTF8.GetBytes(json);
            await _webSocket!.SendAsync(buffer, WebSocketMessageType.Text, true, ct);

            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(effectiveTimeout);

            var response = await tcs.Task.WaitAsync(timeoutCts.Token);

            // Check for error
            var janusType = response["janus"]?.GetValue<string>();
            if (janusType == "error")
            {
                var error = response["error"]?["reason"]?.GetValue<string>() ?? "Unknown error";
                throw new InvalidOperationException($"Janus error: {error}");
            }

            return response;
        }
        finally
        {
            lock (_requestLock)
            {
                _pendingRequests.Remove(transaction);
            }
        }
    }

    #endregion

    #region WebSocket Handling

    private async Task ConnectWebSocketAsync(CancellationToken ct)
    {
        _webSocket = new ClientWebSocket();
        _webSocket.Options.AddSubProtocol("janus-protocol");

        var uri = new Uri($"ws://{_janusServer}:{_janusWsPort}/");
        Log($"Connecting to Janus WebSocket at {uri}");

        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        await _webSocket.ConnectAsync(uri, linkedCts.Token);
        _isConnected = true;
        Log("Connected to Janus WebSocket");

        // Start receive loop
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts!.Token), _cts!.Token);
    }

    private async Task DisconnectWebSocketAsync()
    {
        _isConnected = false;

        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            catch { }

            _webSocket.Dispose();
            _webSocket = null;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];  // 64KB buffer for large SDP messages
        var messageBuffer = new MemoryStream();

        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log("Janus WebSocket closed by server");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    // Accumulate message fragments
                    messageBuffer.Write(buffer, 0, result.Count);

                    // Process only when we have the complete message
                    if (result.EndOfMessage)
                    {
                        var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.SetLength(0);  // Reset for next message
                        ProcessJanusMessage(message);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Janus receive error: {ex.Message}");
                break;
            }
        }

        _isConnected = false;
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        // Janus sessions timeout after 60 seconds by default
        // Send keepalive every 25 seconds
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                await SendKeepAliveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Keepalive loop error: {ex.Message}");
            }
        }
    }

    private void ProcessJanusMessage(string message)
    {
        try
        {
            var json = JsonNode.Parse(message);
            if (json == null) return;

            var janusType = json["janus"]?.GetValue<string>() ?? "";
            var transaction = json["transaction"]?.GetValue<string>();

            // Log interesting messages with appropriate detail level
            // Skip common message types that are handled elsewhere or not interesting:
            // - ack: just acknowledgments
            // - success/event: handled by pending requests
            // - keepalive: internal maintenance
            // - webrtcup/hangup: logged better below with more context
            if (janusType == "error")
            {
                // Log full error details
                var errorCode = json["error"]?["code"]?.GetValue<int>() ?? 0;
                var errorReason = json["error"]?["reason"]?.GetValue<string>() ?? "unknown";
                Log($"Janus error: code={errorCode}, reason={errorReason}");
            }

            // Track WebRTC connection state to pause/resume streaming
            // This prevents broken frames when browser reconnects
            if (janusType == "webrtcup")
            {
                // WebRTC connection established - browser connected and ready for data
                if (!_webrtcUp)
                {
                    _webrtcUp = true;
                    Log("WebRTC viewer connected");
                    WebRtcStateChanged?.Invoke(this, true);
                }
            }
            else if (janusType == "hangup")
            {
                // WebRTC connection dropped - browser disconnected or DTLS failed
                // Note: DTLS alert is normal when browser closes tab or navigates away
                var reason = json["reason"]?.GetValue<string>() ?? "unknown";
                if (_webrtcUp)
                {
                    _webrtcUp = false;
                    Log($"WebRTC viewer disconnected ({reason})");
                    WebRtcStateChanged?.Invoke(this, false);
                }
            }

            // Check if this is a response to one of our own requests
            bool isOwnRequest = false;
            if (!string.IsNullOrEmpty(transaction))
            {
                lock (_requestLock)
                {
                    isOwnRequest = _pendingRequests.ContainsKey(transaction);
                }
            }

            // Complete pending request if this is a response to our own request
            if (isOwnRequest && !string.IsNullOrEmpty(transaction) && (janusType == "success" || janusType == "error" || janusType == "event"))
            {
                TaskCompletionSource<JsonNode>? tcs = null;
                lock (_requestLock)
                {
                    _pendingRequests.TryGetValue(transaction, out tcs);
                }
                tcs?.TrySetResult(json);
            }

            // Forward ALL messages to Obico server for web UI relay, EXCEPT:
            // - Responses to our own requests (ACProxyCam session/handle management)
            // - "ack" messages (just acknowledgments, not meaningful)
            // - "keepalive" responses (internal maintenance)
            // This is critical for WebRTC signaling - web UI needs success/error responses
            if (!isOwnRequest && janusType != "ack" && janusType != "keepalive")
            {
                // Don't log every forwarded message - too verbose
                // Error and hangup are already logged above with more context
                JanusMessageReceived?.Invoke(this, json);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to process Janus message: {ex.Message}");
        }
    }

    #endregion

    private void Log(string message)
    {
        StatusChanged?.Invoke(this, $"[Janus] {message}");
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _mountpointReady = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _webSocket?.Dispose();

        lock (_requestLock)
        {
            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }
    }
}
