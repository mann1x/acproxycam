// MqttCameraController.cs - MQTT-based camera controller for Anycubic printers

using System.Collections.Concurrent;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ACProxyCam.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace ACProxyCam.Services;

/// <summary>
/// Controls Anycubic printer cameras via MQTT.
/// The printer runs a Mochi MQTT broker on port 9883 (TLS).
/// </summary>
public class MqttCameraController : IAsyncDisposable
{
    private IMqttClient? _mqttClient;
    private readonly MqttFactory _mqttFactory;
    private string? _detectedModelCode;
    private TaskCompletionSource<string>? _modelDetectionTcs;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? MessageReceived;
    public event EventHandler<MqttMessageEventArgs>? MqttMessageReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler<string>? ModelCodeDetected;
    /// <summary>
    /// Raised when a camera stop command is detected from another source (slicer, etc.)
    /// </summary>
    public event EventHandler? CameraStopDetected;

    /// <summary>
    /// Raised when LED status is detected from MQTT messages.
    /// </summary>
    public event EventHandler<LedStatus>? LedStatusReceived;

    /// <summary>
    /// Raised when printer state is detected from MQTT messages.
    /// </summary>
    public event EventHandler<string>? PrinterStateReceived;

    public bool IsConnected => _mqttClient?.IsConnected ?? false;
    public string? DetectedModelCode => _detectedModelCode;

    // Default MQTT settings
    public int MqttPort { get; set; } = 9883;
    public bool UseTls { get; set; } = true;

    // Topic patterns: anycubic/anycubicCloud/v1/web/printer/{model}/{deviceId}/{type}
    private const string VideoTopicTemplate = "anycubic/anycubicCloud/v1/web/printer/{0}/{1}/video";
    private const string LightTopicTemplate = "anycubic/anycubicCloud/v1/web/printer/{0}/{1}/light";
    private const string InfoTopicTemplate = "anycubic/anycubicCloud/v1/web/printer/{0}/{1}/info";
    private const string TempTopicTemplate = "anycubic/anycubicCloud/v1/web/printer/{0}/{1}/tempature";

    // Response topic patterns
    private const string LightReportSuffix = "/light/report";
    private const string InfoReportSuffix = "/info/report";
    private const string TempReportSuffix = "/tempature/report";
    private const string ResponseSuffix = "/response";  // Anycubic sends responses here

    // Regex to extract model code from MQTT topics
    private static readonly Regex ModelCodeRegex = new(
        @"anycubic/anycubicCloud/v1/(?:printer/public|sever/printer|server/printer|web/printer)/(\d+)/",
        RegexOptions.Compiled);

    // Response tracking for async queries
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument?>> _pendingResponses = new();

    // State change tracking - only fire events when state actually changes
    private string? _lastPrinterState;
    private bool? _lastLedIsOn;
    private int? _lastLedBrightness;
    private readonly object _stateChangeLock = new();

    // Debounce for camera stop detection
    private DateTime _lastCameraStopTime = DateTime.MinValue;
    private readonly TimeSpan _cameraStopDebounce = TimeSpan.FromSeconds(2);

    public MqttCameraController()
    {
        _mqttFactory = new MqttFactory();
    }

    /// <summary>
    /// Connect to the printer's MQTT broker.
    /// </summary>
    public async Task ConnectAsync(
        string printerIp,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        _mqttClient = _mqttFactory.CreateMqttClient();

        _mqttClient.DisconnectedAsync += e =>
        {
            StatusChanged?.Invoke(this, $"MQTT Disconnected: {e.Reason}");
            return Task.CompletedTask;
        };

        _mqttClient.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            MessageReceived?.Invoke(this, $"[{topic}] {payload}");
            MqttMessageReceived?.Invoke(this, new MqttMessageEventArgs(topic, payload));

            // Try to detect model code from topic
            TryDetectModelCode(topic);

            // Detect camera stop commands from other sources (slicer, etc.)
            TryDetectStopCommand(topic, payload);

            // Try to detect LED status from messages
            TryDetectLedStatus(topic, payload);

            // Try to detect printer state from messages
            TryDetectPrinterState(topic, payload);

            // Handle responses for pending queries (LED, info)
            TryCompleteResponse(topic, payload);

            return Task.CompletedTask;
        };

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(printerIp, MqttPort)
            .WithCredentials(username, password)
            .WithClientId($"ACProxyCam_{Guid.NewGuid():N}")
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));

        if (UseTls)
        {
            optionsBuilder.WithTlsOptions(o =>
            {
                o.WithSslProtocols(SslProtocols.Tls12 | SslProtocols.Tls13);
                // Accept self-signed certificates from printer
                o.WithCertificateValidationHandler(_ => true);
            });
        }

        var options = optionsBuilder.Build();

        StatusChanged?.Invoke(this, $"Connecting to MQTT broker at {printerIp}:{MqttPort}...");

        try
        {
            var result = await _mqttClient.ConnectAsync(options, cancellationToken);

            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                StatusChanged?.Invoke(this, "MQTT Connected successfully");
            }
            else
            {
                throw new Exception($"MQTT connection failed: {result.ResultCode}");
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
            throw;
        }
    }

    /// <summary>
    /// Subscribe to all topics to discover model code.
    /// </summary>
    public async Task SubscribeToAllAsync(CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            throw new InvalidOperationException("Not connected to MQTT broker");
        }

        StatusChanged?.Invoke(this, "Subscribing to all topics (#)...");

        await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic("#")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build(), cancellationToken);

        StatusChanged?.Invoke(this, "Subscribed to all topics");
    }

    /// <summary>
    /// Try to detect the model code from an MQTT topic.
    /// </summary>
    private void TryDetectModelCode(string topic)
    {
        if (_detectedModelCode != null)
            return;

        var match = ModelCodeRegex.Match(topic);
        if (match.Success)
        {
            _detectedModelCode = match.Groups[1].Value;
            StatusChanged?.Invoke(this, $"Auto-detected model code: {_detectedModelCode}");
            ModelCodeDetected?.Invoke(this, _detectedModelCode);
            _modelDetectionTcs?.TrySetResult(_detectedModelCode);
        }
    }

    // Track our own message IDs to avoid reacting to our own commands
    private readonly HashSet<string> _ownMessageIds = new();
    private readonly object _ownMessageIdsLock = new();

    /// <summary>
    /// Try to detect camera stop commands from other sources.
    /// </summary>
    private void TryDetectStopCommand(string topic, string payload)
    {
        // Only check video topics
        if (!topic.Contains("/video"))
            return;

        try
        {
            // Parse the JSON payload
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // Check if it's a video command
            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() == "video" &&
                root.TryGetProperty("action", out var actionElement))
            {
                var action = actionElement.GetString();

                // Check if this is our own message
                if (root.TryGetProperty("msgid", out var msgIdElement))
                {
                    var msgId = msgIdElement.GetString();
                    if (msgId != null)
                    {
                        lock (_ownMessageIdsLock)
                        {
                            if (_ownMessageIds.Contains(msgId))
                            {
                                // This is our own message, ignore it
                                _ownMessageIds.Remove(msgId);
                                return;
                            }
                        }
                    }
                }

                if (action == "stopCapture")
                {
                    // Debounce to prevent duplicate stop events
                    lock (_stateChangeLock)
                    {
                        var now = DateTime.UtcNow;
                        if (now - _lastCameraStopTime < _cameraStopDebounce)
                        {
                            // Too soon after last stop - ignore duplicate
                            return;
                        }
                        _lastCameraStopTime = now;
                    }

                    StatusChanged?.Invoke(this, "Detected camera STOP command from external source!");
                    CameraStopDetected?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch
        {
            // Not valid JSON or doesn't have expected fields - ignore
        }
    }

    /// <summary>
    /// Try to detect LED status from incoming MQTT messages.
    /// Only fires event when status actually changes.
    /// </summary>
    private void TryDetectLedStatus(string topic, string payload)
    {
        // Look for light-related topics or payloads containing light data
        if (!topic.Contains("/light") && !payload.Contains("\"lights\""))
            return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // Check for lights array in data: {"data": {"lights": [{"type": 2, "status": 0|1, "brightness": 0-100}]}}
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("lights", out var lights) &&
                lights.ValueKind == JsonValueKind.Array)
            {
                foreach (var light in lights.EnumerateArray())
                {
                    if (light.TryGetProperty("type", out var typeEl) && typeEl.GetInt32() == 2)
                    {
                        var isOn = light.TryGetProperty("status", out var statusEl) && statusEl.GetInt32() == 1;
                        var brightness = light.TryGetProperty("brightness", out var brightnessEl) ? brightnessEl.GetInt32() : 0;

                        // Only fire event if status actually changed
                        lock (_stateChangeLock)
                        {
                            if (isOn != _lastLedIsOn || brightness != _lastLedBrightness)
                            {
                                _lastLedIsOn = isOn;
                                _lastLedBrightness = brightness;

                                var ledStatus = new LedStatus
                                {
                                    Type = 2,
                                    IsOn = isOn,
                                    Brightness = brightness
                                };
                                StatusChanged?.Invoke(this, $"LED status changed: {(ledStatus.IsOn ? "ON" : "OFF")}, brightness={ledStatus.Brightness}");
                                LedStatusReceived?.Invoke(this, ledStatus);
                            }
                        }
                        return;
                    }
                }
            }
        }
        catch
        {
            // Not valid JSON or doesn't have expected fields - ignore
        }
    }

    /// <summary>
    /// Try to detect printer state from MQTT message payload.
    /// State comes in messages with {"data": {"state": "free|printing|paused|..."}}
    /// Only fires event when state actually changes.
    /// </summary>
    private void TryDetectPrinterState(string topic, string payload)
    {
        // Only process topics that might contain state info
        if (!payload.Contains("\"state\""))
            return;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            // Check for state in data: {"data": {"state": "free"}}
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("state", out var stateEl) &&
                stateEl.ValueKind == JsonValueKind.String)
            {
                var state = stateEl.GetString();
                if (!string.IsNullOrEmpty(state))
                {
                    // Only fire event if state actually changed
                    lock (_stateChangeLock)
                    {
                        if (state != _lastPrinterState)
                        {
                            var oldState = _lastPrinterState ?? "unknown";
                            _lastPrinterState = state;
                            StatusChanged?.Invoke(this, $"Printer state changed: {oldState} → {state}");
                            PrinterStateReceived?.Invoke(this, state);
                        }
                    }
                }
            }
        }
        catch
        {
            // Not valid JSON or doesn't have expected fields - ignore
        }
    }

    /// <summary>
    /// Register a message ID as our own to avoid reacting to it.
    /// </summary>
    private void RegisterOwnMessageId(string msgId)
    {
        lock (_ownMessageIdsLock)
        {
            _ownMessageIds.Add(msgId);
            // Cleanup old entries (keep max 100)
            if (_ownMessageIds.Count > 100)
            {
                _ownMessageIds.Clear();
            }
        }
    }

    /// <summary>
    /// Try to complete a pending response based on the message ID.
    /// </summary>
    private void TryCompleteResponse(string topic, string payload)
    {
        // Only process report or response topics
        var isReportTopic = topic.EndsWith(LightReportSuffix) || topic.EndsWith(InfoReportSuffix) || topic.EndsWith(TempReportSuffix);
        var isResponseTopic = topic.EndsWith(ResponseSuffix);

        if (!isReportTopic && !isResponseTopic)
            return;

        try
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("msgid", out var msgIdElement))
            {
                var msgId = msgIdElement.GetString();
                if (msgId != null && _pendingResponses.TryRemove(msgId, out var tcs))
                {
                    // For /response topics, the printer only sends back msgid on success
                    // Create a success response with code 200
                    if (isResponseTopic && !doc.RootElement.TryGetProperty("code", out _))
                    {
                        // Response topic with just msgid = success
                        var successJson = JsonDocument.Parse("{\"code\":200,\"msgid\":\"" + msgId + "\"}");
                        doc.Dispose();
                        tcs.TrySetResult(successJson);
                        return;
                    }
                    tcs.TrySetResult(doc);
                    return;
                }
            }
            // If no msgid match, dispose the document
            doc.Dispose();
        }
        catch
        {
            // Not valid JSON - ignore
        }
    }

    /// <summary>
    /// Wait for model code to be detected from MQTT messages.
    /// </summary>
    public async Task<string?> WaitForModelDetectionAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (_detectedModelCode != null)
            return _detectedModelCode;

        _modelDetectionTcs = new TaskCompletionSource<string>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var completedTask = await Task.WhenAny(
                _modelDetectionTcs.Task,
                Task.Delay(timeout, cts.Token));

            if (completedTask == _modelDetectionTcs.Task)
                return await _modelDetectionTcs.Task;

            return null;
        }
        catch (OperationCanceledException)
        {
            return _detectedModelCode;
        }
        finally
        {
            _modelDetectionTcs = null;
        }
    }

    /// <summary>
    /// Build the MQTT topic for camera control.
    /// </summary>
    private static string BuildVideoTopic(string modelCode, string deviceId)
    {
        return string.Format(VideoTopicTemplate, modelCode, deviceId);
    }

    /// <summary>
    /// Build the MQTT topic for LED control.
    /// </summary>
    private static string BuildLightTopic(string modelCode, string deviceId)
    {
        return string.Format(LightTopicTemplate, modelCode, deviceId);
    }

    /// <summary>
    /// Build the MQTT topic for printer info.
    /// </summary>
    private static string BuildInfoTopic(string modelCode, string deviceId)
    {
        return string.Format(InfoTopicTemplate, modelCode, deviceId);
    }

    /// <summary>
    /// Build the MQTT topic for temperature control.
    /// </summary>
    private static string BuildTempTopic(string modelCode, string deviceId)
    {
        return string.Format(TempTopicTemplate, modelCode, deviceId);
    }

    /// <summary>
    /// Build the camera command payload.
    /// </summary>
    private static (string Payload, string MsgId) BuildVideoCommand(string action)
    {
        var msgId = Guid.NewGuid().ToString();
        var command = new Dictionary<string, object?>
        {
            ["type"] = "video",
            ["action"] = action,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["msgid"] = msgId,
            ["data"] = null
        };
        return (JsonSerializer.Serialize(command), msgId);
    }

    /// <summary>
    /// Start the camera stream.
    /// </summary>
    public async Task<bool> TryStartCameraAsync(
        string deviceId,
        string? modelCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            throw new InvalidOperationException("Not connected to MQTT broker");
        }

        var effectiveModelCode = modelCode ?? _detectedModelCode;
        if (string.IsNullOrEmpty(effectiveModelCode))
        {
            StatusChanged?.Invoke(this, "Model code not available");
            return false;
        }

        var topic = BuildVideoTopic(effectiveModelCode, deviceId);
        var (payload, msgId) = BuildVideoCommand("startCapture");

        // Register our message ID to avoid reacting to our own command
        RegisterOwnMessageId(msgId);

        StatusChanged?.Invoke(this, "Sending camera START command...");

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);
            StatusChanged?.Invoke(this, "Camera start command sent");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to send camera command: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stop the camera stream.
    /// </summary>
    public async Task<bool> TryStopCameraAsync(
        string deviceId,
        string? modelCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
        {
            throw new InvalidOperationException("Not connected to MQTT broker");
        }

        var effectiveModelCode = modelCode ?? _detectedModelCode;
        if (string.IsNullOrEmpty(effectiveModelCode))
        {
            StatusChanged?.Invoke(this, "Model code not available");
            return false;
        }

        var topic = BuildVideoTopic(effectiveModelCode, deviceId);
        var (payload, msgId) = BuildVideoCommand("stopCapture");

        // Register our message ID to avoid reacting to our own command
        RegisterOwnMessageId(msgId);

        StatusChanged?.Invoke(this, "Sending camera STOP command...");

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);
            StatusChanged?.Invoke(this, "Camera stop command sent");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to send camera command: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Query the current LED status.
    /// </summary>
    public async Task<LedStatus?> QueryLedStatusAsync(
        string deviceId,
        string? modelCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
            return null;

        var effectiveModelCode = modelCode ?? _detectedModelCode;
        if (string.IsNullOrEmpty(effectiveModelCode))
            return null;

        var topic = BuildLightTopic(effectiveModelCode, deviceId);
        var msgId = Guid.NewGuid().ToString();
        var command = new Dictionary<string, object?>
        {
            ["type"] = "light",
            ["action"] = "query",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["msgid"] = msgId,
            ["data"] = new { type = 2 }
        };
        var payload = JsonSerializer.Serialize(command);

        var tcs = new TaskCompletionSource<JsonDocument?>();
        _pendingResponses[msgId] = tcs;

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await tcs.Task.WaitAsync(cts.Token);
            if (response == null)
                return null;

            using (response)
            {
                // Parse response: {"data": {"lights": [{"type": 2, "status": 0|1, "brightness": 0-100}]}}
                if (response.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("lights", out var lights) &&
                    lights.GetArrayLength() > 0)
                {
                    foreach (var light in lights.EnumerateArray())
                    {
                        if (light.TryGetProperty("type", out var typeEl) && typeEl.GetInt32() == 2)
                        {
                            return new LedStatus
                            {
                                Type = 2,
                                IsOn = light.TryGetProperty("status", out var statusEl) && statusEl.GetInt32() == 1,
                                Brightness = light.TryGetProperty("brightness", out var brightnessEl) ? brightnessEl.GetInt32() : 0
                            };
                        }
                    }
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingResponses.TryRemove(msgId, out _);
        }
    }

    /// <summary>
    /// Set the camera LED on or off.
    /// </summary>
    public async Task<bool> SetLedAsync(
        string deviceId,
        bool on,
        int brightness = 100,
        string? modelCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
            return false;

        var effectiveModelCode = modelCode ?? _detectedModelCode;
        if (string.IsNullOrEmpty(effectiveModelCode))
            return false;

        var topic = BuildLightTopic(effectiveModelCode, deviceId);
        var msgId = Guid.NewGuid().ToString();
        var command = new Dictionary<string, object?>
        {
            ["type"] = "light",
            ["action"] = "control",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["msgid"] = msgId,
            ["data"] = new { type = 2, status = on ? 1 : 0, brightness = on ? brightness : 0 }
        };
        var payload = JsonSerializer.Serialize(command);

        var tcs = new TaskCompletionSource<JsonDocument?>();
        _pendingResponses[msgId] = tcs;

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await tcs.Task.WaitAsync(cts.Token);
            if (response == null)
                return false;

            using (response)
            {
                // Check for success: {"code": 200, ...}
                return response.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() == 200;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _pendingResponses.TryRemove(msgId, out _);
        }
    }

    /// <summary>
    /// Set heater temperatures via MQTT.
    /// </summary>
    /// <param name="deviceId">The printer device ID</param>
    /// <param name="bedTemp">Target bed temperature (0 to turn off)</param>
    /// <param name="nozzleTemp">Target nozzle temperature (0 to turn off)</param>
    /// <param name="modelCode">Optional model code (uses detected if not provided)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if command was sent successfully</returns>
    public async Task<bool> SetTemperatureAsync(
        string deviceId,
        int bedTemp,
        int nozzleTemp,
        string? modelCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
            return false;

        var effectiveModelCode = modelCode ?? _detectedModelCode;
        if (string.IsNullOrEmpty(effectiveModelCode))
            return false;

        var topic = BuildTempTopic(effectiveModelCode, deviceId);
        var msgId = Guid.NewGuid().ToString();

        // Determine type: 0 = nozzle only, 1 = bed only, 2 = both (based on which temps are non-zero)
        // From observation: type 0 sets nozzle, type 1 sets bed
        // We'll send separate commands if both need to be set
        var commands = new List<(int type, int bed, int nozzle)>();

        if (bedTemp > 0 || (bedTemp == 0 && nozzleTemp == 0))
        {
            // Set bed temperature (type 1)
            commands.Add((1, bedTemp, 0));
        }
        if (nozzleTemp > 0 || (bedTemp == 0 && nozzleTemp == 0))
        {
            // Set nozzle temperature (type 0)
            commands.Add((0, 0, nozzleTemp));
        }

        try
        {
            foreach (var (type, bed, nozzle) in commands)
            {
                var command = new Dictionary<string, object?>
                {
                    ["type"] = "tempature",
                    ["action"] = "set",
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["msgid"] = Guid.NewGuid().ToString(),
                    ["data"] = new { type, target_hotbed_temp = bed, target_nozzle_temp = nozzle }
                };
                var payload = JsonSerializer.Serialize(command);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message, cancellationToken);

                // Small delay between commands
                if (commands.Count > 1)
                    await Task.Delay(100, cancellationToken);
            }

            StatusChanged?.Invoke(this, $"Temperature command sent: bed={bedTemp}°C, nozzle={nozzleTemp}°C");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Failed to send temperature command: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Query current temperatures via MQTT.
    /// </summary>
    public async Task<(int bedTemp, int nozzleTemp, int targetBed, int targetNozzle)?> QueryTemperatureAsync(
        string deviceId,
        string? modelCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
            return null;

        var effectiveModelCode = modelCode ?? _detectedModelCode;
        if (string.IsNullOrEmpty(effectiveModelCode))
            return null;

        var topic = BuildTempTopic(effectiveModelCode, deviceId);
        var msgId = Guid.NewGuid().ToString();
        var command = new Dictionary<string, object?>
        {
            ["type"] = "tempature",
            ["action"] = "query",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["msgid"] = msgId,
            ["data"] = null
        };
        var payload = JsonSerializer.Serialize(command);

        var tcs = new TaskCompletionSource<JsonDocument?>();
        _pendingResponses[msgId] = tcs;

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await tcs.Task.WaitAsync(cts.Token);
            if (response == null)
                return null;

            using (response)
            {
                // Parse response: {"data": {"curr_hotbed_temp": X, "curr_nozzle_temp": X, "target_hotbed_temp": X, "target_nozzle_temp": X}}
                if (response.RootElement.TryGetProperty("data", out var data))
                {
                    var currBed = data.TryGetProperty("curr_hotbed_temp", out var cb) ? cb.GetInt32() : 0;
                    var currNozzle = data.TryGetProperty("curr_nozzle_temp", out var cn) ? cn.GetInt32() : 0;
                    var targetBed = data.TryGetProperty("target_hotbed_temp", out var tb) ? tb.GetInt32() : 0;
                    var targetNozzle = data.TryGetProperty("target_nozzle_temp", out var tn) ? tn.GetInt32() : 0;
                    return (currBed, currNozzle, targetBed, targetNozzle);
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingResponses.TryRemove(msgId, out _);
        }
    }

    /// <summary>
    /// Query printer info including state.
    /// </summary>
    public async Task<PrinterInfoResult?> QueryPrinterInfoAsync(
        string deviceId,
        string? modelCode = null,
        CancellationToken cancellationToken = default)
    {
        if (_mqttClient == null || !_mqttClient.IsConnected)
            return null;

        var effectiveModelCode = modelCode ?? _detectedModelCode;
        if (string.IsNullOrEmpty(effectiveModelCode))
            return null;

        var topic = BuildInfoTopic(effectiveModelCode, deviceId);
        var msgId = Guid.NewGuid().ToString();
        var command = new Dictionary<string, object?>
        {
            ["type"] = "info",
            ["action"] = "query",
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["msgid"] = msgId
        };
        var payload = JsonSerializer.Serialize(command);

        var tcs = new TaskCompletionSource<JsonDocument?>();
        _pendingResponses[msgId] = tcs;

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await _mqttClient.PublishAsync(message, cancellationToken);

            // Wait for response with timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var response = await tcs.Task.WaitAsync(cts.Token);
            if (response == null)
                return null;

            using (response)
            {
                // Parse response: {"data": {"state": "free", "temp": {...}, ...}}
                if (response.RootElement.TryGetProperty("data", out var data))
                {
                    var result = new PrinterInfoResult();

                    if (data.TryGetProperty("state", out var stateEl))
                        result.State = stateEl.GetString();

                    if (data.TryGetProperty("temp", out var tempEl))
                    {
                        if (tempEl.TryGetProperty("curr_nozzle_temp", out var nozzleTemp))
                            result.NozzleTemp = nozzleTemp.GetInt32();
                        if (tempEl.TryGetProperty("curr_hotbed_temp", out var bedTemp))
                            result.BedTemp = bedTemp.GetInt32();
                        if (tempEl.TryGetProperty("target_nozzle_temp", out var targetNozzle))
                            result.TargetNozzleTemp = targetNozzle.GetInt32();
                        if (tempEl.TryGetProperty("target_hotbed_temp", out var targetBed))
                            result.TargetBedTemp = targetBed.GetInt32();
                    }

                    return result;
                }
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingResponses.TryRemove(msgId, out _);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_mqttClient != null && _mqttClient.IsConnected)
        {
            await _mqttClient.DisconnectAsync();
            StatusChanged?.Invoke(this, "MQTT Disconnected");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_mqttClient != null)
        {
            if (_mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
            }
            _mqttClient.Dispose();
        }
    }
}

/// <summary>
/// Event args for MQTT messages.
/// </summary>
public class MqttMessageEventArgs : EventArgs
{
    public string Topic { get; }
    public string Payload { get; }

    public MqttMessageEventArgs(string topic, string payload)
    {
        Topic = topic;
        Payload = payload;
    }
}

/// <summary>
/// Result of printer info query via MQTT.
/// </summary>
public class PrinterInfoResult
{
    /// <summary>
    /// Printer state: "free" (idle), "printing", "paused", etc.
    /// </summary>
    public string? State { get; set; }

    public int NozzleTemp { get; set; }
    public int BedTemp { get; set; }
    public int TargetNozzleTemp { get; set; }
    public int TargetBedTemp { get; set; }
}
