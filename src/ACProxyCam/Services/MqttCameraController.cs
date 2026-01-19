// MqttCameraController.cs - MQTT-based camera controller for Anycubic printers

using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public bool IsConnected => _mqttClient?.IsConnected ?? false;
    public string? DetectedModelCode => _detectedModelCode;

    // Default MQTT settings
    public int MqttPort { get; set; } = 9883;
    public bool UseTls { get; set; } = true;

    // Topic pattern: anycubic/anycubicCloud/v1/web/printer/{model}/{deviceId}/video
    private const string TopicTemplate = "anycubic/anycubicCloud/v1/web/printer/{0}/{1}/video";

    // Regex to extract model code from MQTT topics
    private static readonly Regex ModelCodeRegex = new(
        @"anycubic/anycubicCloud/v1/(?:printer/public|sever/printer|server/printer|web/printer)/(\d+)/",
        RegexOptions.Compiled);

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
        return string.Format(TopicTemplate, modelCode, deviceId);
    }

    /// <summary>
    /// Build the camera command payload.
    /// </summary>
    private static string BuildVideoCommand(string action)
    {
        var command = new Dictionary<string, object?>
        {
            ["type"] = "video",
            ["action"] = action,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["msgid"] = Guid.NewGuid().ToString(),
            ["data"] = null
        };
        return JsonSerializer.Serialize(command);
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
        var payload = BuildVideoCommand("startCapture");

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
        var payload = BuildVideoCommand("stopCapture");

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
