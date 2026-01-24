// MoonrakerApiClient.cs - REST and WebSocket client for Moonraker API

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Client for communicating with Moonraker API via REST and WebSocket.
/// Provides both synchronous REST calls and real-time WebSocket subscriptions.
/// </summary>
public class MoonrakerApiClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly string _wsUrl;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _wsCts;
    private Task? _wsReceiveTask;

    private int _nextRpcId = 1;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pendingRequests = new();
    private readonly object _wsLock = new();

    private volatile bool _isConnected;
    private volatile bool _isDisposed;

    /// <summary>
    /// Fired when WebSocket connection state changes.
    /// </summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Fired when a status update notification is received from Moonraker.
    /// </summary>
    public event EventHandler<JsonNode>? StatusUpdateReceived;

    /// <summary>
    /// Fired when klippy state changes (ready, shutdown, error).
    /// </summary>
    public event EventHandler<string>? KlippyStateChanged;

    /// <summary>
    /// Fired when print state changes (e.g., printing started, paused, completed).
    /// </summary>
    public event EventHandler<PrintStateEventArgs>? PrintStateChanged;

    /// <summary>
    /// Whether the WebSocket connection is active.
    /// </summary>
    public bool IsConnected => _isConnected;

    public MoonrakerApiClient(string printerIp, int moonrakerPort = 7125)
    {
        _baseUrl = $"http://{printerIp}:{moonrakerPort}";
        _wsUrl = $"ws://{printerIp}:{moonrakerPort}/websocket";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    #region REST API

    /// <summary>
    /// GET request to Moonraker REST API.
    /// </summary>
    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync(endpoint, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<MoonrakerResponse<T>>(json);
            return result != null ? result.Result : default;
        }
        catch (Exception ex)
        {
            throw new MoonrakerApiException($"GET {endpoint} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// POST request to Moonraker REST API.
    /// </summary>
    public async Task<T?> PostAsync<T>(string endpoint, object? data = null, CancellationToken ct = default)
    {
        try
        {
            HttpContent? content = null;
            if (data != null)
            {
                var json = JsonSerializer.Serialize(data);
                content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.PostAsync(endpoint, content, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<MoonrakerResponse<T>>(responseJson);
            return result != null ? result.Result : default;
        }
        catch (Exception ex)
        {
            throw new MoonrakerApiException($"POST {endpoint} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Get server info from Moonraker.
    /// </summary>
    public async Task<ServerInfo?> GetServerInfoAsync(CancellationToken ct = default)
    {
        return await GetAsync<ServerInfo>("/server/info", ct);
    }

    /// <summary>
    /// Get list of available printer objects.
    /// </summary>
    public async Task<PrinterObjectsList?> GetPrinterObjectsListAsync(CancellationToken ct = default)
    {
        return await GetAsync<PrinterObjectsList>("/printer/objects/list", ct);
    }

    /// <summary>
    /// Query specific printer objects.
    /// </summary>
    public async Task<JsonNode?> QueryPrinterObjectsAsync(Dictionary<string, string[]?> objects, CancellationToken ct = default)
    {
        var queryParams = string.Join("&", objects.Select(kvp =>
        {
            if (kvp.Value == null || kvp.Value.Length == 0)
                return kvp.Key;
            return $"{kvp.Key}={string.Join(",", kvp.Value)}";
        }));

        return await GetAsync<JsonNode>($"/printer/objects/query?{queryParams}", ct);
    }

    /// <summary>
    /// Execute a G-code script.
    /// </summary>
    public async Task<string> ExecuteGcodeAsync(string script, CancellationToken ct = default)
    {
        // Use POST body for multi-line scripts (more reliable than URL encoding)
        Console.WriteLine($"[Moonraker] ExecuteGcodeAsync: {script.Replace("\n", "\\n")}");
        var payload = new { script };
        var response = await PostAsync<JsonNode>("/printer/gcode/script", payload, ct);
        Console.WriteLine($"[Moonraker] ExecuteGcodeAsync response: {response}");
        return response?.ToString() ?? "";
    }

    /// <summary>
    /// Execute a G-code script via WebSocket with custom timeout (for long-running commands).
    /// </summary>
    public async Task ExecuteGcodeWithTimeoutAsync(string script, int timeoutMs = 60000, CancellationToken ct = default)
    {
        if (!_isConnected)
        {
            // Fall back to HTTP if WebSocket not connected
            await ExecuteGcodeAsync(script, ct);
            return;
        }

        var request = new JsonRpcRequest
        {
            Method = "printer.gcode.script",
            Params = new Dictionary<string, object> { ["script"] = script }
        };

        await SendJsonRpcRequestAsync(request, timeoutMs);
    }

    /// <summary>
    /// Get print history list.
    /// </summary>
    public async Task<PrintHistoryList?> GetPrintHistoryAsync(int limit = 10, CancellationToken ct = default)
    {
        return await GetAsync<PrintHistoryList>($"/server/history/list?order=desc&limit={limit}", ct);
    }

    #endregion

    #region WebSocket Connection

    /// <summary>
    /// Connect to Moonraker WebSocket for real-time updates.
    /// </summary>
    public async Task ConnectWebSocketAsync(CancellationToken ct = default)
    {
        if (_isConnected)
            return;

        lock (_wsLock)
        {
            if (_isConnected)
                return;

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        try
        {
            await _webSocket!.ConnectAsync(new Uri(_wsUrl), _wsCts.Token);
            _isConnected = true;
            ConnectionStateChanged?.Invoke(this, true);

            // Start receive loop
            _wsReceiveTask = Task.Run(() => WebSocketReceiveLoopAsync(_wsCts.Token), _wsCts.Token);

            // Identify ourselves to Moonraker
            await IdentifyAsync();
        }
        catch (Exception ex)
        {
            _isConnected = false;
            ConnectionStateChanged?.Invoke(this, false);
            throw new MoonrakerApiException($"WebSocket connection failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Disconnect from Moonraker WebSocket.
    /// </summary>
    public async Task DisconnectWebSocketAsync()
    {
        _wsCts?.Cancel();

        if (_webSocket != null && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnecting", CancellationToken.None);
            }
            catch { }
        }

        _isConnected = false;
        ConnectionStateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Identify client to Moonraker server.
    /// </summary>
    private async Task IdentifyAsync()
    {
        var request = new JsonRpcRequest
        {
            Id = GetNextRpcId(),
            Method = "server.connection.identify",
            Params = new Dictionary<string, object>
            {
                ["client_name"] = "ACProxyCam",
                ["version"] = "1.0.0",
                ["type"] = "agent",
                ["url"] = "https://github.com/mann1x/acproxycam"
            }
        };

        await SendJsonRpcRequestAsync(request);
    }

    /// <summary>
    /// Subscribe to printer object updates.
    /// </summary>
    public async Task SubscribeToObjectsAsync(Dictionary<string, string[]?> objects)
    {
        if (!_isConnected)
            throw new InvalidOperationException("WebSocket not connected");

        var request = new JsonRpcRequest
        {
            Id = GetNextRpcId(),
            Method = "printer.objects.subscribe",
            Params = new Dictionary<string, object>
            {
                ["objects"] = objects
            }
        };

        await SendJsonRpcRequestAsync(request);
    }

    /// <summary>
    /// Subscribe to common objects for Obico integration.
    /// </summary>
    public async Task SubscribeToObicoObjectsAsync()
    {
        var objects = new Dictionary<string, string[]?>
        {
            ["webhooks"] = null,
            ["print_stats"] = null,
            ["virtual_sdcard"] = null,
            ["gcode_move"] = null,
            ["toolhead"] = null,
            ["extruder"] = null,
            ["heater_bed"] = null,
            ["fan"] = null,
            ["display_status"] = null
        };

        await SubscribeToObjectsAsync(objects);
    }

    /// <summary>
    /// Send JSON-RPC request over WebSocket.
    /// </summary>
    private async Task<JsonNode?> SendJsonRpcRequestAsync(JsonRpcRequest request, int timeoutMs = 10000)
    {
        if (!_isConnected || _webSocket == null)
            throw new InvalidOperationException("WebSocket not connected");

        var tcs = new TaskCompletionSource<JsonNode?>();
        _pendingRequests[request.Id] = tcs;

        try
        {
            var json = JsonSerializer.Serialize(request);
            var buffer = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, _wsCts!.Token);

            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        finally
        {
            _pendingRequests.TryRemove(request.Id, out _);
        }
    }

    /// <summary>
    /// WebSocket receive loop.
    /// </summary>
    private async Task WebSocketReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                messageBuffer.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var message = Encoding.UTF8.GetString(messageBuffer.ToArray());
                    messageBuffer.SetLength(0);

                    ProcessWebSocketMessage(message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            _isConnected = false;
            ConnectionStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Process incoming WebSocket message.
    /// </summary>
    private void ProcessWebSocketMessage(string message)
    {
        try
        {
            var json = JsonNode.Parse(message);
            if (json == null)
                return;

            // Check if it's a response to a pending request
            var id = json["id"]?.GetValue<int>();
            if (id.HasValue && _pendingRequests.TryRemove(id.Value, out var tcs))
            {
                var result = json["result"];
                var error = json["error"];

                if (error != null)
                {
                    tcs.TrySetException(new MoonrakerApiException($"RPC error: {error}"));
                }
                else
                {
                    tcs.TrySetResult(result);
                }
                return;
            }

            // Check if it's a notification
            var method = json["method"]?.GetValue<string>();
            if (method != null)
            {
                ProcessNotification(method, json["params"]);
            }
        }
        catch (Exception ex)
        {
            // Log error but don't crash
            System.Diagnostics.Debug.WriteLine($"Error processing WebSocket message: {ex.Message}");
        }
    }

    /// <summary>
    /// Process Moonraker notification.
    /// </summary>
    private void ProcessNotification(string method, JsonNode? paramsNode)
    {
        switch (method)
        {
            case "notify_status_update":
                if (paramsNode is JsonArray arr && arr.Count > 0)
                {
                    var status = arr[0];
                    if (status != null)
                    {
                        StatusUpdateReceived?.Invoke(this, status);
                        ProcessStatusUpdate(status);
                    }
                }
                break;

            case "notify_klippy_ready":
                KlippyStateChanged?.Invoke(this, "ready");
                break;

            case "notify_klippy_shutdown":
                KlippyStateChanged?.Invoke(this, "shutdown");
                break;

            case "notify_klippy_disconnected":
                KlippyStateChanged?.Invoke(this, "disconnected");
                break;
        }
    }

    /// <summary>
    /// Process status update to detect print state changes.
    /// </summary>
    private void ProcessStatusUpdate(JsonNode status)
    {
        // Check for print_stats changes
        var printStats = status["print_stats"];
        if (printStats != null)
        {
            var state = printStats["state"]?.GetValue<string>();
            var filename = printStats["filename"]?.GetValue<string>();

            if (state != null)
            {
                PrintStateChanged?.Invoke(this, new PrintStateEventArgs
                {
                    State = state,
                    Filename = filename
                });
            }
        }
    }

    private int GetNextRpcId()
    {
        return Interlocked.Increment(ref _nextRpcId);
    }

    #endregion

    #region Printer Control

    /// <summary>
    /// Pause the current print.
    /// </summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        await ExecuteGcodeAsync("PAUSE", ct);
    }

    /// <summary>
    /// Resume a paused print.
    /// </summary>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await ExecuteGcodeAsync("RESUME", ct);
    }

    /// <summary>
    /// Cancel the current print.
    /// </summary>
    public async Task CancelAsync(CancellationToken ct = default)
    {
        await ExecuteGcodeAsync("CANCEL_PRINT", ct);
    }

    /// <summary>
    /// Home specified axes.
    /// </summary>
    public async Task HomeAsync(string[] axes, CancellationToken ct = default)
    {
        var axesStr = axes.Length > 0 ? string.Join(" ", axes.Select(a => a.ToUpper())) : "";
        // Homing can take 30+ seconds, use WebSocket with longer timeout
        await ExecuteGcodeWithTimeoutAsync($"G28 {axesStr}".Trim(), 60000, ct);
    }

    /// <summary>
    /// Jog the printer head.
    /// </summary>
    public async Task JogAsync(double? x = null, double? y = null, double? z = null, double feedrate = 6000, CancellationToken ct = default)
    {
        var moves = new List<string>();

        await ExecuteGcodeAsync("G91", ct); // Relative positioning

        if (x.HasValue) moves.Add($"X{x.Value}");
        if (y.HasValue) moves.Add($"Y{y.Value}");
        if (z.HasValue) moves.Add($"Z{z.Value}");

        if (moves.Count > 0)
        {
            await ExecuteGcodeAsync($"G1 {string.Join(" ", moves)} F{feedrate}", ct);
        }

        await ExecuteGcodeAsync("G90", ct); // Back to absolute positioning
    }

    /// <summary>
    /// Set heater temperature.
    /// </summary>
    public async Task SetTemperatureAsync(string heater, double temperature, CancellationToken ct = default)
    {
        string gcode = heater.ToLower() switch
        {
            "extruder" or "tool" or "tool0" => $"M104 S{temperature}",
            "heater_bed" or "bed" => $"M140 S{temperature}",
            _ when heater.StartsWith("extruder") => $"M104 T{heater[8..]} S{temperature}",
            _ => $"SET_HEATER_TEMPERATURE HEATER={heater} TARGET={temperature}"
        };

        await ExecuteGcodeAsync(gcode, ct);
    }

    /// <summary>
    /// Disable stepper motors.
    /// </summary>
    public async Task DisableMotorsAsync(CancellationToken ct = default)
    {
        await ExecuteGcodeAsync("M84", ct);
    }

    /// <summary>
    /// Upload a gcode file to Moonraker.
    /// </summary>
    public async Task UploadFileAsync(string filename, byte[] fileData, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileData);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", filename);

            var response = await _httpClient.PostAsync("/server/files/upload", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new MoonrakerApiException($"File upload failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Start printing a file.
    /// </summary>
    public async Task StartPrintAsync(string filename, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PostAsync(
                $"/printer/print/start?filename={Uri.EscapeDataString(filename)}",
                null,
                ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new MoonrakerApiException($"Start print failed: {ex.Message}", ex);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _webSocket?.Dispose();
        _httpClient.Dispose();
    }
}

#region Models

public class MoonrakerResponse<T>
{
    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonNode? Error { get; set; }
}

public class ServerInfo
{
    [JsonPropertyName("klippy_connected")]
    public bool KlippyConnected { get; set; }

    [JsonPropertyName("klippy_state")]
    public string KlippyState { get; set; } = "";

    [JsonPropertyName("components")]
    public List<string> Components { get; set; } = new();

    [JsonPropertyName("moonraker_version")]
    public string MoonrakerVersion { get; set; } = "";

    [JsonPropertyName("api_version")]
    public List<int> ApiVersion { get; set; } = new();

    [JsonPropertyName("websocket_count")]
    public int WebsocketCount { get; set; }
}

public class PrinterObjectsList
{
    [JsonPropertyName("objects")]
    public List<string> Objects { get; set; } = new();
}

public class PrintHistoryList
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("jobs")]
    public List<PrintJob> Jobs { get; set; } = new();
}

public class PrintJob
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("start_time")]
    public double? StartTime { get; set; }

    [JsonPropertyName("end_time")]
    public double? EndTime { get; set; }

    [JsonPropertyName("print_duration")]
    public double? PrintDuration { get; set; }

    [JsonPropertyName("total_duration")]
    public double? TotalDuration { get; set; }

    [JsonPropertyName("filament_used")]
    public double? FilamentUsed { get; set; }
}

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public object? Params { get; set; }
}

public class PrintStateEventArgs : EventArgs
{
    public string State { get; set; } = "";
    public string? Filename { get; set; }
}

public class MoonrakerApiException : Exception
{
    public MoonrakerApiException(string message) : base(message) { }
    public MoonrakerApiException(string message, Exception inner) : base(message, inner) { }
}

#endregion
