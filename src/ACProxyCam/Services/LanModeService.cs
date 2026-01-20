// LanModeService.cs - Enables LAN mode on Anycubic printers via SSH

using Renci.SshNet;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACProxyCam.Services;

/// <summary>
/// Enables LAN mode on Anycubic printers via SSH tunnel.
/// Uses the printer's local API on port 18086.
/// </summary>
public class LanModeService
{
    private const int SshTimeoutSeconds = 10;
    private const int ApiPort = 18086;
    private const int LocalTunnelPort = 18086; // Local port for SSH tunnel
    private const int LanModeTimeoutSeconds = 60;
    private const int PollIntervalSeconds = 5;
    private const byte MessageTerminator = 0x03; // ETX byte

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Result of LAN mode operation.
    /// </summary>
    public class LanModeResult
    {
        public bool Success { get; set; }
        public bool WasAlreadyOpen { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Enable LAN mode on the printer if not already enabled.
    /// </summary>
    public async Task<LanModeResult> EnableLanModeAsync(
        string printerIp,
        int sshPort = 22,
        string sshUser = "root",
        string sshPassword = "rockchip",
        CancellationToken cancellationToken = default)
    {
        StatusChanged?.Invoke(this, $"Connecting to {printerIp}:{sshPort} via SSH to enable LAN mode...");

        return await Task.Run(async () =>
        {
            SshClient? client = null;
            ForwardedPortLocal? tunnel = null;

            try
            {
                client = new SshClient(printerIp, sshPort, sshUser, sshPassword);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);

                client.Connect();

                if (!client.IsConnected)
                {
                    return new LanModeResult
                    {
                        Success = false,
                        Error = "SSH connection failed"
                    };
                }

                StatusChanged?.Invoke(this, "SSH connected, setting up tunnel to local API...");

                // Create SSH tunnel to printer's local API port
                // Bind to localhost so we can connect to the API
                tunnel = new ForwardedPortLocal("127.0.0.1", LocalTunnelPort, "127.0.0.1", ApiPort);
                client.AddForwardedPort(tunnel);
                tunnel.Start();

                if (!tunnel.IsStarted)
                {
                    return new LanModeResult
                    {
                        Success = false,
                        Error = "Failed to start SSH tunnel"
                    };
                }

                StatusChanged?.Invoke(this, "SSH tunnel established, querying LAN mode status...");

                // Query current LAN mode status
                var status = await QueryLanModeStatusAsync(cancellationToken);

                if (status == null)
                {
                    return new LanModeResult
                    {
                        Success = false,
                        Error = "Failed to query LAN mode status"
                    };
                }

                if (status == "open")
                {
                    StatusChanged?.Invoke(this, "LAN mode is already enabled");
                    return new LanModeResult
                    {
                        Success = true,
                        WasAlreadyOpen = true
                    };
                }

                StatusChanged?.Invoke(this, $"LAN mode status: {status}. Sending OpenLanPrint command...");

                // Send OpenLanPrint command
                var openResult = await SendOpenLanPrintAsync(cancellationToken);

                if (!openResult)
                {
                    return new LanModeResult
                    {
                        Success = false,
                        Error = "Failed to send OpenLanPrint command"
                    };
                }

                // Wait for LAN mode to become active
                StatusChanged?.Invoke(this, "Waiting for LAN mode to activate...");

                var startTime = DateTime.UtcNow;
                var timeout = TimeSpan.FromSeconds(LanModeTimeoutSeconds);

                while (DateTime.UtcNow - startTime < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), cancellationToken);

                    status = await QueryLanModeStatusAsync(cancellationToken);

                    if (status == "open")
                    {
                        StatusChanged?.Invoke(this, "LAN mode activated successfully!");
                        return new LanModeResult
                        {
                            Success = true,
                            WasAlreadyOpen = false
                        };
                    }

                    var elapsed = (int)(DateTime.UtcNow - startTime).TotalSeconds;
                    StatusChanged?.Invoke(this, $"LAN mode status: {status ?? "unknown"} ({elapsed}s / {LanModeTimeoutSeconds}s)");
                }

                return new LanModeResult
                {
                    Success = false,
                    Error = $"LAN mode did not activate within {LanModeTimeoutSeconds} seconds"
                };
            }
            catch (Renci.SshNet.Common.SshAuthenticationException)
            {
                return new LanModeResult
                {
                    Success = false,
                    Error = $"SSH authentication failed (user: {sshUser})"
                };
            }
            catch (SocketException ex)
            {
                return new LanModeResult
                {
                    Success = false,
                    Error = $"Cannot connect to printer: {ex.Message}"
                };
            }
            catch (OperationCanceledException)
            {
                return new LanModeResult
                {
                    Success = false,
                    Error = "Operation cancelled"
                };
            }
            catch (Exception ex)
            {
                return new LanModeResult
                {
                    Success = false,
                    Error = $"Error: {ex.Message}"
                };
            }
            finally
            {
                if (tunnel != null)
                {
                    try
                    {
                        if (tunnel.IsStarted)
                            tunnel.Stop();
                        client?.RemoveForwardedPort(tunnel);
                    }
                    catch { }
                }

                if (client != null)
                {
                    try
                    {
                        if (client.IsConnected)
                            client.Disconnect();
                        client.Dispose();
                    }
                    catch { }
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Query the current LAN mode status via the local API.
    /// Returns "open" if LAN mode is enabled (open=1), "closed" if disabled (open=0), null on error.
    /// </summary>
    private async Task<string?> QueryLanModeStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = new LanApiRequest
            {
                Id = 2017,
                Method = "Printer/QueryLanPrintStatus",
                Params = new object()
            };

            var response = await SendApiRequestAsync(request, cancellationToken);

            if (response?.Result?.Open != null)
            {
                return response.Result.Open == 1 ? "open" : "closed";
            }

            return null;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Query error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Send the OpenLanPrint command via the local API.
    /// </summary>
    private async Task<bool> SendOpenLanPrintAsync(CancellationToken cancellationToken)
    {
        try
        {
            var request = new LanApiRequest
            {
                Id = 2015,
                Method = "Printer/OpenLanPrint",
                Params = new object()
            };

            var response = await SendApiRequestAsync(request, cancellationToken);

            // Success if we got any response (the printer may not return a result)
            return response != null;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"OpenLanPrint error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Send a request to the printer's local API via the SSH tunnel.
    /// </summary>
    private async Task<LanApiResponse?> SendApiRequestAsync(LanApiRequest request, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();

        await client.ConnectAsync("127.0.0.1", LocalTunnelPort, cancellationToken);

        using var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        stream.WriteTimeout = 5000;

        // Serialize request and add terminator
        var requestJson = JsonSerializer.Serialize(request);
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);
        var messageBytes = new byte[requestBytes.Length + 1];
        requestBytes.CopyTo(messageBytes, 0);
        messageBytes[^1] = MessageTerminator;

        // Send request
        await stream.WriteAsync(messageBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // Read response until terminator
        var responseBuffer = new List<byte>();
        var buffer = new byte[1024];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);

            if (bytesRead == 0)
                break;

            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == MessageTerminator)
                {
                    // Found terminator, parse response
                    var responseJson = Encoding.UTF8.GetString(responseBuffer.ToArray());
                    return JsonSerializer.Deserialize<LanApiResponse>(responseJson);
                }
                responseBuffer.Add(buffer[i]);
            }

            // Safety limit
            if (responseBuffer.Count > 65536)
                break;
        }

        return null;
    }
}

/// <summary>
/// Request structure for the printer's local API.
/// </summary>
internal class LanApiRequest
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public object Params { get; set; } = new();
}

/// <summary>
/// Response structure from the printer's local API.
/// </summary>
internal class LanApiResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("result")]
    public LanApiResult? Result { get; set; }

    [JsonPropertyName("error")]
    public LanApiError? Error { get; set; }
}

/// <summary>
/// Result portion of API response.
/// </summary>
internal class LanApiResult
{
    [JsonPropertyName("open")]
    public int? Open { get; set; }
}

/// <summary>
/// Error portion of API response.
/// </summary>
internal class LanApiError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
