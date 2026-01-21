// SshCredentialService.cs - Retrieves MQTT credentials from printer via SSH

using Renci.SshNet;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACProxyCam.Services;

/// <summary>
/// Retrieves MQTT credentials from Anycubic printers via SSH.
/// Credentials are stored in /userdata/app/gk/config/device_account.json on the printer.
/// </summary>
public class SshCredentialService
{
    private const int SshTimeoutSeconds = 10;

    // Possible paths where device_account.json might be located
    private static readonly string[] CredentialPaths =
    {
        "/userdata/app/gk/config/device_account.json",
        "/mnt/userdata/app/gk/config/device_account.json",
        "/data/app/gk/config/device_account.json",
    };

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Result of credential retrieval.
    /// </summary>
    public class CredentialResult
    {
        public bool Success { get; set; }
        public string? MqttUsername { get; set; }
        public string? MqttPassword { get; set; }
        public string? DeviceId { get; set; }
        public string? DeviceType { get; set; }
        public string? ModelCode { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Result of printer info query.
    /// </summary>
    public class PrinterInfoResult
    {
        public string? DeviceId { get; set; }
        public string? DeviceType { get; set; }
        public string? ModelCode { get; set; }
    }

    /// <summary>
    /// Retrieve MQTT credentials from the printer via SSH.
    /// </summary>
    public async Task<CredentialResult> RetrieveCredentialsAsync(
        string printerIp,
        int sshPort = 22,
        string sshUser = "root",
        string sshPassword = "rockchip",
        CancellationToken cancellationToken = default)
    {
        StatusChanged?.Invoke(this, $"Connecting to {printerIp}:{sshPort} via SSH...");

        return await Task.Run(() =>
        {
            try
            {
                using var client = new SshClient(printerIp, sshPort, sshUser, sshPassword);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);

                client.Connect();

                if (!client.IsConnected)
                {
                    return new CredentialResult
                    {
                        Success = false,
                        Error = "SSH connection failed"
                    };
                }

                StatusChanged?.Invoke(this, "SSH connected, retrieving credentials...");

                // Try each possible path
                foreach (var path in CredentialPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var command = client.RunCommand($"cat {path} 2>/dev/null");

                    if (command.ExitStatus == 0 && !string.IsNullOrWhiteSpace(command.Result))
                    {
                        StatusChanged?.Invoke(this, $"Found credentials at {path}");

                        var credentials = ParseDeviceAccount(command.Result);
                        if (credentials != null)
                        {
                            // Query local API for device type and model code
                            var printerInfo = QueryPrinterInfo(client);
                            if (printerInfo != null)
                            {
                                credentials.DeviceType = printerInfo.DeviceType;
                                credentials.ModelCode = printerInfo.ModelCode;
                            }
                            client.Disconnect();
                            return credentials;
                        }
                    }
                }

                // If not found at known paths, try to find it
                StatusChanged?.Invoke(this, "Searching for device_account.json...");
                var findCommand = client.RunCommand("find /userdata /data /mnt -name 'device_account.json' 2>/dev/null | head -1");

                if (findCommand.ExitStatus == 0 && !string.IsNullOrWhiteSpace(findCommand.Result))
                {
                    var foundPath = findCommand.Result.Trim();
                    StatusChanged?.Invoke(this, $"Found at {foundPath}");

                    var catCommand = client.RunCommand($"cat {foundPath}");
                    if (catCommand.ExitStatus == 0)
                    {
                        var credentials = ParseDeviceAccount(catCommand.Result);
                        if (credentials != null)
                        {
                            // Query local API for device type and model code
                            var printerInfo = QueryPrinterInfo(client);
                            if (printerInfo != null)
                            {
                                credentials.DeviceType = printerInfo.DeviceType;
                                credentials.ModelCode = printerInfo.ModelCode;
                            }
                            client.Disconnect();
                            return credentials;
                        }
                    }
                }

                client.Disconnect();
                return new CredentialResult
                {
                    Success = false,
                    Error = "device_account.json not found on printer"
                };
            }
            catch (Renci.SshNet.Common.SshAuthenticationException)
            {
                return new CredentialResult
                {
                    Success = false,
                    Error = $"SSH authentication failed (user: {sshUser})"
                };
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                return new CredentialResult
                {
                    Success = false,
                    Error = $"Cannot connect to printer: {ex.Message}"
                };
            }
            catch (OperationCanceledException)
            {
                return new CredentialResult
                {
                    Success = false,
                    Error = "Operation cancelled"
                };
            }
            catch (Exception ex)
            {
                return new CredentialResult
                {
                    Success = false,
                    Error = $"SSH error: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Parse the device_account.json content.
    /// </summary>
    private CredentialResult? ParseDeviceAccount(string json)
    {
        try
        {
            var deviceAccount = JsonSerializer.Deserialize<DeviceAccountJson>(json);

            if (deviceAccount == null ||
                string.IsNullOrEmpty(deviceAccount.Username) ||
                string.IsNullOrEmpty(deviceAccount.Password))
            {
                StatusChanged?.Invoke(this, "Invalid device_account.json format");
                return null;
            }

            return new CredentialResult
            {
                Success = true,
                MqttUsername = deviceAccount.Username,
                MqttPassword = deviceAccount.Password,
                DeviceId = deviceAccount.DeviceId ?? ""
            };
        }
        catch (JsonException ex)
        {
            StatusChanged?.Invoke(this, $"Failed to parse device_account.json: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Query printer info from local API and api.cfg via SSH.
    /// Returns device type (e.g., "Anycubic Kobra S1") and model code.
    /// </summary>
    private PrinterInfoResult? QueryPrinterInfo(SshClient client)
    {
        var result = new PrinterInfoResult();

        try
        {
            // 1. Query local API for device_type
            StatusChanged?.Invoke(this, "Querying printer info from local API...");

            var command = client.RunCommand(
                "printf '%s\\003' '{\"id\":1,\"method\":\"Query/K3cInfo\",\"params\":{}}' | nc -w 2 localhost 18086 2>/dev/null | tr -d '\\003'");

            if (command.ExitStatus == 0 && !string.IsNullOrWhiteSpace(command.Result))
            {
                var response = JsonSerializer.Deserialize<LocalApiResponse>(command.Result);
                if (response?.Result != null)
                {
                    result.DeviceType = response.Result.DeviceType;
                    if (!string.IsNullOrEmpty(result.DeviceType))
                        StatusChanged?.Invoke(this, $"Device type: {result.DeviceType}");
                }
            }

            // 2. Read model code from api.cfg
            StatusChanged?.Invoke(this, "Reading model code from api.cfg...");

            command = client.RunCommand("cat /userdata/app/gk/config/api.cfg 2>/dev/null");

            if (command.ExitStatus == 0 && !string.IsNullOrWhiteSpace(command.Result))
            {
                var apiConfig = JsonSerializer.Deserialize<ApiConfigJson>(command.Result);
                if (!string.IsNullOrEmpty(apiConfig?.Cloud?.ModelId))
                {
                    result.ModelCode = apiConfig.Cloud.ModelId;
                    StatusChanged?.Invoke(this, $"Model code: {result.ModelCode}");
                }
            }

            // Return result if we got at least one piece of info
            if (!string.IsNullOrEmpty(result.DeviceType) || !string.IsNullOrEmpty(result.ModelCode))
                return result;

            StatusChanged?.Invoke(this, "Could not retrieve printer info");
            return null;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Error querying printer info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Retrieve printer info (device ID, device type, and model code) from the printer via SSH.
    /// Useful for checking if printer changed and refreshing device info.
    /// </summary>
    public async Task<PrinterInfoResult?> RetrievePrinterInfoAsync(
        string printerIp,
        int sshPort = 22,
        string sshUser = "root",
        string sshPassword = "rockchip",
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var client = new SshClient(printerIp, sshPort, sshUser, sshPassword);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);

                client.Connect();

                if (!client.IsConnected)
                    return null;

                // First get the deviceId from device_account.json
                string? deviceId = null;
                foreach (var path in CredentialPaths)
                {
                    var command = client.RunCommand($"cat {path} 2>/dev/null");
                    if (command.ExitStatus == 0 && !string.IsNullOrWhiteSpace(command.Result))
                    {
                        try
                        {
                            var deviceAccount = JsonSerializer.Deserialize<DeviceAccountJson>(command.Result);
                            if (!string.IsNullOrEmpty(deviceAccount?.DeviceId))
                            {
                                deviceId = deviceAccount.DeviceId;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // Then get device type and model code
                var printerInfo = QueryPrinterInfo(client);
                client.Disconnect();

                if (printerInfo == null)
                    printerInfo = new PrinterInfoResult();

                printerInfo.DeviceId = deviceId;
                return printerInfo;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Test SSH connection without retrieving credentials.
    /// </summary>
    public async Task<(bool Success, string? Error)> TestConnectionAsync(
        string printerIp,
        int sshPort = 22,
        string sshUser = "root",
        string sshPassword = "rockchip",
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var client = new SshClient(printerIp, sshPort, sshUser, sshPassword);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);

                client.Connect();
                var connected = client.IsConnected;
                client.Disconnect();

                return connected ? (true, null) : (false, "Connection failed");
            }
            catch (Renci.SshNet.Common.SshAuthenticationException)
            {
                return (false, "Authentication failed");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                return (false, $"Cannot connect: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }, cancellationToken);
    }
}

/// <summary>
/// JSON structure of device_account.json on the printer.
/// </summary>
internal class DeviceAccountJson
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    [JsonPropertyName("gcodeUploadToken")]
    public string? GcodeUploadToken { get; set; }
}

/// <summary>
/// JSON response from the printer's local API (server/info).
/// </summary>
internal class LocalApiResponse
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("result")]
    public LocalApiResult? Result { get; set; }
}

internal class LocalApiResult
{
    [JsonPropertyName("device_type")]
    public string? DeviceType { get; set; }
}

/// <summary>
/// JSON structure of api.cfg on the printer.
/// </summary>
internal class ApiConfigJson
{
    [JsonPropertyName("cloud")]
    public ApiConfigCloud? Cloud { get; set; }
}

internal class ApiConfigCloud
{
    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }
}
