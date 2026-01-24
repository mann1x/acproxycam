// DiscoveryServer.cs - HTTP server for Obico automatic discovery/linking

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ACProxyCam.Models;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// HTTP server for Obico automatic printer discovery.
/// Runs on port 46793 and handles discovery handshake for all printers.
/// </summary>
public class DiscoveryServer : IDisposable
{
    private readonly int _port;
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, DiscoveryPrinter> _printers = new();

    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<DiscoveryCompletedEventArgs>? DiscoveryCompleted;

    public bool IsRunning => _isRunning;

    public DiscoveryServer(int port = 46793)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_port}/");
    }

    /// <summary>
    /// Start the discovery server.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            return;

        try
        {
            _cts = new CancellationTokenSource();
            _listener.Start();
            _isRunning = true;

            _listenerTask = Task.Run(() => ListenerLoopAsync(_cts.Token));
            Log($"Discovery server started on port {_port}");
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5)
        {
            // Access denied - need to run as root or add URL reservation
            Log($"Failed to start discovery server: Access denied. Run as root or add URL reservation.");
            throw new InvalidOperationException(
                $"Cannot bind to port {_port}. Run as root or use: netsh http add urlacl url=http://+:{_port}/ user=everyone",
                ex);
        }
        catch (Exception ex)
        {
            Log($"Failed to start discovery server: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Stop the discovery server.
    /// </summary>
    public void Stop()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            _listener.Stop();
        }
        catch { }

        Log("Discovery server stopped");
    }

    /// <summary>
    /// Register a printer for discovery.
    /// </summary>
    public void RegisterPrinter(string deviceId, PrinterObicoConfig config, string printerName)
    {
        var printer = new DiscoveryPrinter
        {
            DeviceId = deviceId,
            DeviceSecret = GenerateDeviceSecret(),
            Config = config,
            PrinterName = printerName,
            RegisteredAt = DateTime.UtcNow
        };

        _printers[deviceId] = printer;
        Log($"Registered printer '{printerName}' for discovery (device_id: {deviceId})");
    }

    /// <summary>
    /// Unregister a printer from discovery.
    /// </summary>
    public void UnregisterPrinter(string deviceId)
    {
        if (_printers.TryRemove(deviceId, out var printer))
        {
            Log($"Unregistered printer '{printer.PrinterName}' from discovery");
        }
    }

    /// <summary>
    /// Get the device secret for a printer (for announcing to Obico server).
    /// </summary>
    public string? GetDeviceSecret(string deviceId)
    {
        return _printers.TryGetValue(deviceId, out var printer) ? printer.DeviceSecret : null;
    }

    /// <summary>
    /// Get discovery info for a printer.
    /// </summary>
    public DiscoveryPrinter? GetPrinter(string deviceId)
    {
        return _printers.TryGetValue(deviceId, out var printer) ? printer : null;
    }

    /// <summary>
    /// Mark a printer as linked.
    /// </summary>
    public void MarkLinked(string deviceId, string authToken)
    {
        if (_printers.TryGetValue(deviceId, out var printer))
        {
            printer.IsLinked = true;
            DiscoveryCompleted?.Invoke(this, new DiscoveryCompletedEventArgs
            {
                DeviceId = deviceId,
                PrinterName = printer.PrinterName,
                AuthToken = authToken
            });
        }
    }

    private async Task ListenerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) when (!_isRunning) { break; }
            catch (Exception ex)
            {
                Log($"Listener error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // Log request
            Log($"Request: {request.HttpMethod} {request.Url?.PathAndQuery} from {request.RemoteEndPoint}");

            // Handle CORS preflight
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Accept");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";

            if (path == "/plugin/obico/grab-discovery-secret" && request.HttpMethod == "GET")
            {
                await HandleGrabDiscoverySecretAsync(context);
            }
            else if (path == "/shutdown" && request.HttpMethod == "POST")
            {
                response.StatusCode = 200;
                response.Close();
                // Don't actually shutdown - this is for moonraker-obico compatibility
            }
            else
            {
                response.StatusCode = 404;
                response.Close();
            }
        }
        catch (Exception ex)
        {
            Log($"Request handling error: {ex.Message}");
            try
            {
                response.StatusCode = 500;
                response.Close();
            }
            catch { }
        }
    }

    private async Task HandleGrabDiscoverySecretAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        // Get device_id from query string
        var deviceId = request.QueryString["device_id"];

        if (string.IsNullOrEmpty(deviceId))
        {
            response.StatusCode = 400;
            await WriteJsonResponseAsync(response, new { error = "Missing device_id parameter" });
            return;
        }

        // Find the printer
        if (!_printers.TryGetValue(deviceId, out var printer))
        {
            response.StatusCode = 404;
            await WriteJsonResponseAsync(response, new { error = "Unknown device_id" });
            return;
        }

        // Validate request is from local network
        var remoteIp = GetRemoteIp(request);
        if (!IsLocalNetworkIp(remoteIp))
        {
            Log($"Rejected discovery request from non-local IP: {remoteIp}");
            response.StatusCode = 403;
            await WriteJsonResponseAsync(response, new { error = "Request must come from local network" });
            return;
        }

        Log($"Discovery secret requested for '{printer.PrinterName}' from {remoteIp}");

        // Add CORS headers for cross-origin requests
        response.Headers.Add("Access-Control-Allow-Origin", "*");
        response.Headers.Add("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");

        // Check Accept header to determine response format
        // Default to JSON for mobile apps (empty Accept, */*, or application/json)
        var accept = request.Headers["Accept"] ?? "";
        var wantsHtml = accept.Contains("text/html") && !accept.Contains("application/json");

        if (wantsHtml)
        {
            // Return HTML with postMessage for browser-based discovery
            var html = $@"<!DOCTYPE html>
<html>
<head><title>Obico Discovery</title></head>
<body>
<p>Handshake succeeded!</p>
<p>You can close this window now.</p>
<script>
window.opener.postMessage({{
    device_secret: '{printer.DeviceSecret}',
    device_id: '{deviceId}'
}}, '*');
</script>
</body>
</html>";

            response.ContentType = "text/html";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }
        else
        {
            // Return JSON response (default for mobile apps and API clients)
            await WriteJsonResponseAsync(response, new { device_secret = printer.DeviceSecret });
        }
    }

    private async Task WriteJsonResponseAsync(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json";
        // Ensure CORS headers are set for all JSON responses
        if (string.IsNullOrEmpty(response.Headers["Access-Control-Allow-Origin"]))
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
        }
        var json = JsonSerializer.Serialize(data);
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    private string GetRemoteIp(HttpListenerRequest request)
    {
        // Check X-Forwarded-For header first (for proxied requests)
        var forwarded = request.Headers["X-Forwarded-For"];
        if (!string.IsNullOrEmpty(forwarded))
        {
            var ips = forwarded.Split(',');
            if (ips.Length > 0)
            {
                return ips[0].Trim();
            }
        }

        // Fall back to direct remote endpoint
        return request.RemoteEndPoint?.Address.ToString() ?? "unknown";
    }

    private bool IsLocalNetworkIp(string ipStr)
    {
        if (string.IsNullOrEmpty(ipStr))
            return false;

        // Always allow localhost
        if (ipStr == "127.0.0.1" || ipStr == "::1" || ipStr == "localhost")
            return true;

        if (!IPAddress.TryParse(ipStr, out var ip))
            return false;

        // Check for private IP ranges
        var bytes = ip.GetAddressBytes();

        // IPv4 private ranges
        if (bytes.Length == 4)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;

            // Loopback 127.0.0.0/8
            if (bytes[0] == 127)
                return true;
        }

        // IPv6 link-local (fe80::/10)
        if (bytes.Length == 16 && bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            return true;

        return false;
    }

    private static string GenerateDeviceSecret()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void Log(string message)
    {
        StatusChanged?.Invoke(this, $"[Discovery] {message}");
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Stop();
        _cts?.Dispose();
        _listener.Close();
    }
}

public class DiscoveryPrinter
{
    public string DeviceId { get; set; } = "";
    public string DeviceSecret { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public PrinterObicoConfig Config { get; set; } = new();
    public DateTime RegisteredAt { get; set; }
    public bool IsLinked { get; set; }
}

public class DiscoveryCompletedEventArgs : EventArgs
{
    public string DeviceId { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string AuthToken { get; set; } = "";
}
