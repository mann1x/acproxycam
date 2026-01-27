// ObicoServerConnection.cs - WebSocket and REST client for Obico server

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Handles communication with the Obico server via WebSocket and REST API.
/// </summary>
public class ObicoServerConnection : IDisposable
{
    private readonly string _serverUrl;
    private readonly string _authToken;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _wsCts;
    private Task? _wsReceiveTask;
    private Task? _wsSendTask;

    private readonly BlockingCollection<string> _sendQueue = new(50);
    private readonly object _wsLock = new();

    private volatile bool _isConnected;
    private volatile bool _isDisposed;
    private DateTime _lastStatusUpdate = DateTime.MinValue;

    /// <summary>
    /// Enable verbose WebSocket logging for troubleshooting.
    /// </summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Fired when WebSocket connection state changes.
    /// </summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Fired when a passthru command is received from the server.
    /// </summary>
    public event EventHandler<PassthruCommand>? PassthruCommandReceived;

    /// <summary>
    /// Fired when a message is received from the server.
    /// </summary>
    public event EventHandler<JsonNode>? MessageReceived;

    /// <summary>
    /// Fired when remote status (viewing state) is received from the server.
    /// </summary>
    public event EventHandler<RemoteStatus>? RemoteStatusReceived;

    /// <summary>
    /// Fired when a Janus signaling message is received from the server.
    /// </summary>
    public event EventHandler<string>? JanusMessageReceived;

    /// <summary>
    /// Fired when streaming configuration is received from the server.
    /// </summary>
    public event EventHandler<StreamingConfig>? StreamingConfigReceived;

    /// <summary>
    /// Fired when a command (pause/resume/cancel) is received from the server.
    /// Used for AI-triggered actions.
    /// </summary>
    public event EventHandler<ObicoCommand>? CommandReceived;

    /// <summary>
    /// Whether the WebSocket connection is active.
    /// </summary>
    public bool IsConnected => _isConnected;

    public ObicoServerConnection(string serverUrl, string authToken)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _authToken = authToken;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_serverUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", authToken);
    }

    #region WebSocket Connection

    /// <summary>
    /// Connect to Obico server WebSocket.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_isConnected)
            return;

        lock (_wsLock)
        {
            if (_isConnected)
                return;

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();
            _webSocket.Options.SetRequestHeader("authorization", $"bearer {_authToken}");
            _wsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        try
        {
            var wsUrl = _serverUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws/dev/";
            await _webSocket!.ConnectAsync(new Uri(wsUrl), _wsCts.Token);
            _isConnected = true;
            ConnectionStateChanged?.Invoke(this, true);

            // Start receive and send loops
            _wsReceiveTask = Task.Run(() => WebSocketReceiveLoopAsync(_wsCts.Token), _wsCts.Token);
            _wsSendTask = Task.Run(() => WebSocketSendLoopAsync(_wsCts.Token), _wsCts.Token);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            ConnectionStateChanged?.Invoke(this, false);
            throw new ObicoApiException($"WebSocket connection failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Disconnect from Obico server WebSocket.
    /// </summary>
    public async Task DisconnectAsync()
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
    /// WebSocket receive loop.
    /// </summary>
    private async Task WebSocketReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16384];
        var messageBuffer = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Check for auth token conflict (code 4321)
                    if (_webSocket.CloseStatus == (WebSocketCloseStatus)4321)
                    {
                        throw new ObicoApiException("Auth token conflict - another client is using this token");
                    }
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
    /// WebSocket send loop - processes queued messages.
    /// </summary>
    private async Task WebSocketSendLoopAsync(CancellationToken ct)
    {
        try
        {
            foreach (var message in _sendQueue.GetConsumingEnumerable(ct))
            {
                if (_webSocket?.State != WebSocketState.Open)
                    break;

                var buffer = Encoding.UTF8.GetBytes(message);
                await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// <summary>
    /// Process incoming WebSocket message.
    /// </summary>
    private void ProcessWebSocketMessage(string message)
    {
        try
        {
            if (Verbose)
                Console.WriteLine($"[Obico WS] Received: {message.Substring(0, Math.Min(500, message.Length))}...");

            var json = JsonNode.Parse(message);
            if (json == null)
                return;

            MessageReceived?.Invoke(this, json);

            // Check for remote_status (viewing state from web UI)
            var remoteStatus = json["remote_status"];
            if (remoteStatus != null)
            {
                var viewing = remoteStatus["viewing"]?.GetValue<bool>() ?? false;
                RemoteStatusReceived?.Invoke(this, new RemoteStatus { Viewing = viewing });
            }

            // Check for streaming configuration (Janus host/port/stream_id)
            var streamingNode = json["streaming"];
            if (streamingNode != null)
            {
                try
                {
                    var config = new StreamingConfig
                    {
                        Host = streamingNode["host"]?.GetValue<string>() ?? "",
                        Port = streamingNode["port"]?.GetValue<int>() ?? 0,
                        StreamId = streamingNode["stream_id"]?.GetValue<int>() ?? 0
                    };

                    if (!string.IsNullOrEmpty(config.Host) && config.Port > 0)
                    {
                        StreamingConfigReceived?.Invoke(this, config);
                    }
                }
                catch
                {
                    // Ignore parse errors for streaming config
                }
            }

            // Check for Janus signaling message
            var janusMsg = json["janus"];
            if (janusMsg != null)
            {
                var janusStr = janusMsg.GetValue<string>();
                if (!string.IsNullOrEmpty(janusStr))
                {
                    JanusMessageReceived?.Invoke(this, janusStr);
                }
            }

            // Check for passthru command
            var passthru = json["passthru"];
            if (passthru != null)
            {
                var target = passthru["target"]?.GetValue<string>();
                var func = passthru["func"]?.GetValue<string>();
                var refId = passthru["ref"]?.GetValue<string>();

                if (target != null && func != null)
                {
                    var command = new PassthruCommand
                    {
                        Target = target,
                        Function = func,
                        RefId = refId,
                        Args = passthru["args"],
                        Kwargs = passthru["kwargs"]
                    };

                    PassthruCommandReceived?.Invoke(this, command);
                }
            }

            // Check for commands array (AI-triggered pause/resume/cancel)
            var commandsArray = json["commands"];
            if (commandsArray is JsonArray commands && commands.Count > 0)
            {
                foreach (var cmdNode in commands)
                {
                    if (cmdNode == null) continue;

                    var cmd = cmdNode["cmd"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(cmd)) continue;

                    var obicoCmd = new ObicoCommand
                    {
                        Cmd = cmd,
                        Initiator = cmdNode["initiator"]?.GetValue<string>()
                    };

                    // Parse args if present
                    var argsNode = cmdNode["args"];
                    if (argsNode != null)
                    {
                        obicoCmd.Retract = argsNode["retract"]?.GetValue<double>();
                        obicoCmd.LiftZ = argsNode["lift_z"]?.GetValue<double>();
                        obicoCmd.ToolsOff = argsNode["tools_off"]?.GetValue<bool>();
                        obicoCmd.BedOff = argsNode["bed_off"]?.GetValue<bool>();
                    }

                    if (Verbose)
                        Console.WriteLine($"[Obico WS] Command received: {cmd} (initiator: {obicoCmd.Initiator})");
                    CommandReceived?.Invoke(this, obicoCmd);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error processing Obico message: {ex.Message}");
        }
    }

    /// <summary>
    /// Send a message via WebSocket.
    /// </summary>
    public void SendMessage(object message)
    {
        if (!_isConnected)
        {
            if (Verbose)
                Console.WriteLine("[Obico WS] Cannot send - not connected");
            return;
        }

        var json = JsonSerializer.Serialize(message);
        if (Verbose)
            Console.WriteLine($"[Obico WS] Sending: {json.Substring(0, Math.Min(500, json.Length))}...");
        _sendQueue.TryAdd(json);
    }

    /// <summary>
    /// Send passthru response back to server.
    /// </summary>
    public void SendPassthruResponse(string refId, object? result = null, string? error = null)
    {
        var response = new Dictionary<string, object>
        {
            ["passthru"] = new Dictionary<string, object?>
            {
                ["ref"] = refId,
                ["ret"] = result,
                ["error"] = error
            }
        };

        SendMessage(response);
    }

    /// <summary>
    /// Send Janus signaling message to Obico server.
    /// </summary>
    public void SendJanusMessage(string janusJson)
    {
        var message = new Dictionary<string, object>
        {
            ["janus"] = janusJson
        };

        SendMessage(message);
    }

    #endregion

    #region Status Updates

    /// <summary>
    /// Send printer status update to Obico server.
    /// </summary>
    /// <param name="status">The status update to send.</param>
    /// <param name="includeSettings">Whether to include settings in the update.</param>
    /// <param name="force">Whether to bypass throttling.</param>
    public void SendStatusUpdate(ObicoStatusUpdate status, bool includeSettings = false, bool force = false)
    {
        // Throttle non-critical updates to 5 seconds (unless forced or has event)
        var now = DateTime.UtcNow;
        if (!force && (now - _lastStatusUpdate).TotalSeconds < 5 && !status.HasEvent)
        {
            return;
        }
        _lastStatusUpdate = now;

        // Build message - only include current_print_ts when actively printing
        // Obico server uses "if not msg.get('current_print_ts')" which treats 0 and null as falsy
        // moonraker-obico omits the field entirely when not printing
        var message = new Dictionary<string, object?>
        {
            ["status"] = status.Status
        };

        // Only include current_print_ts when there's an active print (non-null, non-zero value)
        if (status.CurrentPrintTs.HasValue && status.CurrentPrintTs.Value > 0)
        {
            message["current_print_ts"] = status.CurrentPrintTs.Value;
        }

        if (status.Event != null)
        {
            message["event"] = status.Event;
        }

        if (includeSettings && status.Settings != null)
        {
            message["settings"] = status.Settings;
        }

        SendMessage(message);
    }

    #endregion

    #region REST API - Linking

    /// <summary>
    /// Announce printer as unlinked to initiate discovery.
    /// </summary>
    public async Task<UnlinkedResponse?> AnnounceUnlinkedAsync(UnlinkedRequest request, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Use a separate HttpClient without auth for unlinked endpoint
            using var client = new HttpClient
            {
                BaseAddress = new Uri(_serverUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            var response = await client.PostAsync("/api/v1/octo/unlinked/", content, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<UnlinkedResponse>(responseJson);
        }
        catch (Exception ex)
        {
            throw new ObicoApiException($"Announce unlinked failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Verify 6-digit linking code.
    /// </summary>
    public async Task<VerifyResponse?> VerifyLinkCodeAsync(string code, CancellationToken ct = default)
    {
        try
        {
            // Use a separate HttpClient without auth for verify endpoint
            using var client = new HttpClient
            {
                BaseAddress = new Uri(_serverUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            var response = await client.GetAsync($"/api/v1/octo/verify/?code={code.Trim()}", ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<VerifyResponse>(responseJson);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // Invalid code
        }
        catch (Exception ex)
        {
            throw new ObicoApiException($"Verify link code failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Login to Obico server with email and password.
    /// Returns session cookies for authenticated requests.
    /// </summary>
    /// <param name="email">User's email address</param>
    /// <param name="password">User's password</param>
    /// <param name="allowSelfSignedCerts">Allow self-signed SSL certificates</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<ObicoLoginResult> LoginAsync(
        string email,
        string password,
        bool allowSelfSignedCerts = false,
        CancellationToken ct = default)
    {
        try
        {
            var cookieContainer = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AllowAutoRedirect = true
            };

            if (allowSelfSignedCerts)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(_serverUrl),
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Step 1: GET /accounts/login/ to obtain CSRF token from cookie
            var getResponse = await client.GetAsync("/accounts/login/", ct);
            getResponse.EnsureSuccessStatusCode();

            // Extract CSRF token from cookies
            var cookies = cookieContainer.GetCookies(new Uri(_serverUrl));
            var csrfToken = cookies["csrftoken"]?.Value;

            if (string.IsNullOrEmpty(csrfToken))
            {
                return new ObicoLoginResult
                {
                    Success = false,
                    Error = "Failed to obtain CSRF token from server"
                };
            }

            // Step 2: POST /accounts/login/ with credentials
            var formContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = email,
                ["password"] = password,
                ["csrfmiddlewaretoken"] = csrfToken
            });

            // Add Referer header (required by Django CSRF)
            client.DefaultRequestHeaders.Add("Referer", $"{_serverUrl}/accounts/login/");

            var postResponse = await client.PostAsync("/accounts/login/", formContent, ct);

            // Check if login was successful
            // Django redirects to next page on success, returns 200 with form on failure
            if (postResponse.IsSuccessStatusCode)
            {
                // Check if we got redirected (successful login)
                // Obico uses tsd_sessionid cookie instead of sessionid
                var sessionCookies = cookieContainer.GetCookies(new Uri(_serverUrl));
                var sessionCookie = sessionCookies["tsd_sessionid"] ?? sessionCookies["sessionid"];

                if (sessionCookie != null)
                {
                    // Refresh CSRF token from response (it may have changed)
                    var newCsrfToken = sessionCookies["csrftoken"]?.Value ?? csrfToken;

                    return new ObicoLoginResult
                    {
                        Success = true,
                        Cookies = cookieContainer,
                        CsrfToken = newCsrfToken
                    };
                }

                // Still on login page - credentials were wrong
                return new ObicoLoginResult
                {
                    Success = false,
                    Error = "Invalid email or password"
                };
            }

            return new ObicoLoginResult
            {
                Success = false,
                Error = $"Login failed: HTTP {(int)postResponse.StatusCode}"
            };
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            return new ObicoLoginResult
            {
                Success = false,
                Error = "SSL certificate error. Enable 'Allow self-signed certs' for self-hosted servers."
            };
        }
        catch (Exception ex)
        {
            return new ObicoLoginResult
            {
                Success = false,
                Error = $"Connection failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get a verification code for linking a new printer.
    /// Requires authenticated session from LoginAsync.
    /// </summary>
    /// <param name="cookies">Session cookies from LoginAsync</param>
    /// <param name="csrfToken">CSRF token from LoginAsync</param>
    /// <param name="allowSelfSignedCerts">Allow self-signed SSL certificates</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<VerificationCodeResult> GetVerificationCodeAsync(
        CookieContainer cookies,
        string csrfToken,
        bool allowSelfSignedCerts = false,
        CancellationToken ct = default)
    {
        try
        {
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true
            };

            if (allowSelfSignedCerts)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri(_serverUrl),
                Timeout = TimeSpan.FromSeconds(15)
            };

            // Add CSRF token header for API requests
            client.DefaultRequestHeaders.Add("X-CSRFToken", csrfToken);
            client.DefaultRequestHeaders.Add("Referer", _serverUrl);

            var response = await client.GetAsync("/api/v1/onetimeverificationcodes/", ct);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return new VerificationCodeResult
                    {
                        Success = false,
                        Error = "Session expired or unauthorized"
                    };
                }

                return new VerificationCodeResult
                {
                    Success = false,
                    Error = $"Failed to get verification code: HTTP {(int)response.StatusCode}"
                };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var codeResponse = JsonSerializer.Deserialize<VerificationCodeResponse>(json);

            if (codeResponse == null || string.IsNullOrEmpty(codeResponse.Code))
            {
                return new VerificationCodeResult
                {
                    Success = false,
                    Error = "Server did not return a verification code"
                };
            }

            return new VerificationCodeResult
            {
                Success = true,
                Code = codeResponse.Code,
                ExpiresAt = codeResponse.ExpiredAt
            };
        }
        catch (Exception ex)
        {
            return new VerificationCodeResult
            {
                Success = false,
                Error = $"Failed to get verification code: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Get linked printer information.
    /// </summary>
    public async Task<PrinterInfo?> GetPrinterInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/v1/octo/printer/", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);

            // Parse the response - printer info is nested inside "printer" object
            // Response format: {"user": {...}, "printer": {"id": ..., "name": ..., "is_pro": ...}}
            var jsonNode = JsonNode.Parse(json);
            var printerNode = jsonNode?["printer"];

            if (printerNode != null)
            {
                return JsonSerializer.Deserialize<PrinterInfo>(printerNode.ToJsonString(), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }

            // Fall back to trying to parse the root object directly
            return JsonSerializer.Deserialize<PrinterInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            throw new ObicoApiException($"Get printer info failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Delete a printer from the Obico server.
    /// Note: This uses /api/v1/printers/ which requires user session authentication.
    /// The printer auth_token cannot delete - this will return 403 Forbidden.
    /// For now, deletion must be done manually via the Obico web UI.
    /// </summary>
    /// <param name="printerId">The printer ID on the Obico server</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if deleted successfully, false otherwise</returns>
    public async Task<(bool Success, string? Error)> DeletePrinterAsync(int printerId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"/api/v1/printers/{printerId}/", ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (false, "Printer not found on server (may have been deleted already)");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return (false, "Obico API requires web login to delete printers");
            }

            return (false, $"Server returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to delete printer: {ex.Message}");
        }
    }

    /// <summary>
    /// Update printer name on the Obico server.
    /// Uses /api/v1/octo/printer/ endpoint which accepts printer auth_token.
    /// </summary>
    /// <param name="printerId">The printer ID (unused, kept for API compatibility)</param>
    /// <param name="name">The new printer name</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if updated successfully, false otherwise</returns>
    public async Task<(bool Success, string? Error)> UpdatePrinterNameAsync(int printerId, string name, CancellationToken ct = default)
    {
        try
        {
            var content = new StringContent(
                JsonSerializer.Serialize(new { name }),
                Encoding.UTF8,
                "application/json");

            // Use /api/v1/octo/printer/ which accepts printer auth_token (not /api/v1/printers/ which requires user session)
            var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/octo/printer/")
            {
                Content = content
            };

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (false, "Printer not found on server");
            }

            return (false, $"Server returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to update printer name: {ex.Message}");
        }
    }

    #endregion

    #region REST API - Snapshots

    /// <summary>
    /// Upload snapshot image to Obico server.
    /// </summary>
    /// <param name="jpegData">JPEG image data</param>
    /// <param name="isPrimary">Whether this is the primary camera</param>
    /// <param name="cameraName">Camera name (for non-primary cameras)</param>
    /// <param name="viewingBoost">Whether user is actively viewing (for local servers)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task PostSnapshotAsync(byte[] jpegData, bool isPrimary = true, string? cameraName = null, bool viewingBoost = false, CancellationToken ct = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            using var imageContent = new ByteArrayContent(jpegData);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            content.Add(imageContent, "pic", "snapshot.jpg");

            if (!isPrimary)
            {
                content.Add(new StringContent("false"), "is_primary_camera");
                if (!string.IsNullOrEmpty(cameraName))
                {
                    content.Add(new StringContent(cameraName), "camera_name");
                }
            }

            // Include viewing_boost flag when user is actively viewing
            if (viewingBoost)
            {
                content.Add(new StringContent("true"), "viewing_boost");
            }

            var response = await _httpClient.PostAsync("/api/v1/octo/pic/", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            if (Verbose)
                Console.WriteLine($"[Obico] Snapshot upload failed: {ex.Message}");
            throw new ObicoApiException($"Post snapshot failed: {ex.Message}", ex);
        }
    }

    #endregion

    #region REST API - Events

    /// <summary>
    /// Send printer event to Obico server.
    /// </summary>
    public async Task PostPrinterEventAsync(PrinterEvent printerEvent, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(printerEvent);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/v1/octo/printer_events/", content, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new ObicoApiException($"Post printer event failed: {ex.Message}", ex);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _sendQueue.CompleteAdding();
        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _webSocket?.Dispose();
        _httpClient.Dispose();
        _sendQueue.Dispose();
    }
}

#region Models

public class PassthruCommand
{
    public string Target { get; set; } = "";
    public string Function { get; set; } = "";
    public string? RefId { get; set; }
    public JsonNode? Args { get; set; }
    public JsonNode? Kwargs { get; set; }
}

public class ObicoStatusUpdate
{
    public long? CurrentPrintTs { get; set; }
    public ObicoStatus Status { get; set; } = new();
    public ObicoEvent? Event { get; set; }
    public ObicoSettings? Settings { get; set; }
    public bool HasEvent => Event != null;
}

public class ObicoStatus
{
    [JsonPropertyName("_ts")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [JsonPropertyName("state")]
    public ObicoState State { get; set; } = new();

    [JsonPropertyName("temperatures")]
    public ObicoTemps Temps { get; set; } = new();

    [JsonPropertyName("progress")]
    public ObicoProgress Progress { get; set; } = new();

    [JsonPropertyName("job")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObicoJob? Job { get; set; }

    [JsonPropertyName("currentZ")]
    public double? CurrentZ { get; set; }

    [JsonPropertyName("currentLayerHeight")]
    public int? CurrentLayer { get; set; }

    [JsonPropertyName("file_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObicoFileMetadata? FileMetadata { get; set; }
}

public class ObicoFileMetadata
{
    [JsonPropertyName("analysis")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObicoFileAnalysis? Analysis { get; set; }

    [JsonPropertyName("obico")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObicoFileMeta? Obico { get; set; }
}

public class ObicoFileAnalysis
{
    [JsonPropertyName("printingArea")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObicoPrintingArea? PrintingArea { get; set; }
}

public class ObicoPrintingArea
{
    [JsonPropertyName("maxZ")]
    public double? MaxZ { get; set; }
}

public class ObicoFileMeta
{
    [JsonPropertyName("totalLayerCount")]
    public int? TotalLayerCount { get; set; }
}

public class ObicoState
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "Operational";

    [JsonPropertyName("flags")]
    public ObicoStateFlags Flags { get; set; } = new();
}

public class ObicoStateFlags
{
    [JsonPropertyName("operational")]
    public bool Operational { get; set; } = true;

    [JsonPropertyName("printing")]
    public bool Printing { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("pausing")]
    public bool Pausing { get; set; }

    [JsonPropertyName("cancelling")]
    public bool Cancelling { get; set; }

    [JsonPropertyName("error")]
    public bool Error { get; set; }

    [JsonPropertyName("ready")]
    public bool Ready { get; set; } = true;

    [JsonPropertyName("closedOrError")]
    public bool ClosedOrError { get; set; }
}

public class ObicoTemps
{
    [JsonPropertyName("tool0")]
    public ObicoTempReading Tool0 { get; set; } = new();

    [JsonPropertyName("bed")]
    public ObicoTempReading Bed { get; set; } = new();
}

public class ObicoTempReading
{
    [JsonPropertyName("actual")]
    public double Actual { get; set; }

    [JsonPropertyName("target")]
    public double Target { get; set; }

    [JsonPropertyName("offset")]
    public double Offset { get; set; }
}

public class ObicoProgress
{
    [JsonPropertyName("completion")]
    public double? Completion { get; set; }

    [JsonPropertyName("printTime")]
    public int? PrintTime { get; set; }

    [JsonPropertyName("printTimeLeft")]
    public int? PrintTimeLeft { get; set; }

    [JsonPropertyName("filepos")]
    public long? FilePos { get; set; }
}

public class ObicoJob
{
    [JsonPropertyName("file")]
    public ObicoJobFile File { get; set; } = new();
}

public class ObicoJobFile
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("display")]
    public string? Display { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public class ObicoEvent
{
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "";
}

public class ObicoSettings
{
    [JsonPropertyName("webcams")]
    public List<ObicoWebcam> Webcams { get; set; } = new();

    [JsonPropertyName("agent")]
    public ObicoAgent Agent { get; set; } = new();

    [JsonPropertyName("platform_uname")]
    public List<string> PlatformUname { get; set; } = new();
}

public class ObicoWebcam
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    [JsonPropertyName("is_primary_camera")]
    public bool IsPrimaryCamera { get; set; } = true;

    [JsonPropertyName("stream_mode")]
    public string StreamMode { get; set; } = "mjpeg";

    [JsonPropertyName("stream_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StreamId { get; set; }

    [JsonPropertyName("stream_url")]
    public string? StreamUrl { get; set; }

    [JsonPropertyName("snapshot_url")]
    public string? SnapshotUrl { get; set; }

    [JsonPropertyName("rotation")]
    public int Rotation { get; set; } = 0;

    [JsonPropertyName("flipH")]
    public bool FlipH { get; set; } = false;

    [JsonPropertyName("flipV")]
    public bool FlipV { get; set; } = false;

    [JsonPropertyName("streamRatio")]
    public string StreamRatio { get; set; } = "16:9";
}

public class ObicoAgent
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "moonraker_obico";  // Must match for Moonraker-style commands

    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.6.5";  // Compatible moonraker-obico version
}

public class UnlinkedRequest
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("device_secret")]
    public string DeviceSecret { get; set; } = "";

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 46793;

    [JsonPropertyName("os")]
    public string Os { get; set; } = "";

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = "";

    [JsonPropertyName("host_or_ip")]
    public string HostOrIp { get; set; } = "";

    [JsonPropertyName("one_time_passcode")]
    public string? OneTimePasscode { get; set; }

    [JsonPropertyName("plugin_version")]
    public string PluginVersion { get; set; } = "1.0.0";

    [JsonPropertyName("agent")]
    public string Agent { get; set; } = "moonraker_obico";

    // Additional fields required by Obico Cloud
    [JsonPropertyName("printerprofile")]
    public string PrinterProfile { get; set; } = "Unknown";

    [JsonPropertyName("machine_type")]
    public string MachineType { get; set; } = "Klipper";

    [JsonPropertyName("rpi_model")]
    public string? RpiModel { get; set; }

    [JsonPropertyName("meta")]
    public Dictionary<string, object>? Meta { get; set; }
}

public class UnlinkedResponse
{
    [JsonPropertyName("one_time_passcode")]
    public string? OneTimePasscode { get; set; }

    [JsonPropertyName("one_time_passlink")]
    public string? OneTimePasslink { get; set; }

    [JsonPropertyName("verification_code")]
    public string? VerificationCode { get; set; }
}

public class VerifyResponse
{
    [JsonPropertyName("printer")]
    public PrinterInfo? Printer { get; set; }
}

public class PrinterInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("auth_token")]
    public string? AuthToken { get; set; }

    [JsonPropertyName("is_pro")]
    public bool IsPro { get; set; }
}

public class PrinterEvent
{
    [JsonPropertyName("event_title")]
    public string EventTitle { get; set; } = "";

    [JsonPropertyName("event_text")]
    public string EventText { get; set; } = "";

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "PRINTER_STATE";

    [JsonPropertyName("event_class")]
    public string EventClass { get; set; } = "INFO";
}

/// <summary>
/// Remote status received from Obico server (viewing state, etc.).
/// </summary>
public class RemoteStatus
{
    /// <summary>
    /// Whether a user is actively viewing the webcam stream.
    /// </summary>
    public bool Viewing { get; set; }
}

/// <summary>
/// Streaming configuration received from Obico server for Janus WebRTC.
/// </summary>
public class StreamingConfig
{
    /// <summary>
    /// Janus server hostname or IP.
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// Janus data port for MJPEG streaming (UDP).
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Stream ID assigned by the server.
    /// </summary>
    public int StreamId { get; set; }
}

/// <summary>
/// Command received from Obico server (AI-triggered pause/resume/cancel).
/// </summary>
public class ObicoCommand
{
    /// <summary>
    /// Command type: pause, resume, or cancel.
    /// </summary>
    public string Cmd { get; set; } = "";

    /// <summary>
    /// Who initiated the command: "ai_failure_detection", "web", etc.
    /// </summary>
    public string? Initiator { get; set; }

    /// <summary>
    /// Filament retraction distance in mm (for pause).
    /// </summary>
    public double? Retract { get; set; }

    /// <summary>
    /// Z-axis lift distance in mm (for pause).
    /// </summary>
    public double? LiftZ { get; set; }

    /// <summary>
    /// Turn off all heaters when pausing.
    /// </summary>
    public bool? ToolsOff { get; set; }

    /// <summary>
    /// Turn off bed heater when pausing.
    /// </summary>
    public bool? BedOff { get; set; }
}

public class ObicoApiException : Exception
{
    public ObicoApiException(string message) : base(message) { }
    public ObicoApiException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Result of login attempt to Obico server.
/// </summary>
public class ObicoLoginResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public CookieContainer? Cookies { get; set; }
    public string? CsrfToken { get; set; }
}

/// <summary>
/// Result of verification code request.
/// </summary>
public class VerificationCodeResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Code { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Response from /api/v1/onetimeverificationcodes/ endpoint.
/// </summary>
public class VerificationCodeResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("expired_at")]
    public DateTime? ExpiredAt { get; set; }

    [JsonPropertyName("verified_at")]
    public DateTime? VerifiedAt { get; set; }

    [JsonPropertyName("printer")]
    public PrinterInfo? Printer { get; set; }
}

#endregion
