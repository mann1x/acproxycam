// ObicoLinkingService.cs - Manages printer discovery and linking with Obico server

using ACProxyCam.Models;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Manages the printer linking process with Obico server.
/// Handles both automatic discovery and manual 6-digit code linking.
/// </summary>
public class ObicoLinkingService : IDisposable
{
    private readonly DiscoveryServer _discoveryServer;
    private readonly Dictionary<string, CancellationTokenSource> _discoveryTasks = new();
    private readonly object _lock = new();

    private volatile bool _isDisposed;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<LinkingCompletedEventArgs>? LinkingCompleted;

    public ObicoLinkingService(int discoveryPort = 46793)
    {
        _discoveryServer = new DiscoveryServer(discoveryPort);
        _discoveryServer.StatusChanged += (s, msg) => StatusChanged?.Invoke(this, msg);
        _discoveryServer.DiscoveryCompleted += OnDiscoveryCompleted;
    }

    /// <summary>
    /// Start the linking service.
    /// </summary>
    public void Start()
    {
        _discoveryServer.Start();
    }

    /// <summary>
    /// Stop the linking service.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            foreach (var cts in _discoveryTasks.Values)
            {
                cts.Cancel();
            }
            _discoveryTasks.Clear();
        }

        _discoveryServer.Stop();
    }

    /// <summary>
    /// Start discovery for a printer (automatic linking).
    /// </summary>
    public async Task<string?> StartDiscoveryAsync(
        PrinterConfig printerConfig,
        string serverUrl,
        CancellationToken ct = default)
    {
        // Generate or use existing device ID (must be hex without dashes, like uuid4().hex in Python)
        var deviceId = printerConfig.Obico.ObicoDeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N"); // "N" format = no dashes
            printerConfig.Obico.ObicoDeviceId = deviceId;
        }

        // Register printer with discovery server
        _discoveryServer.RegisterPrinter(deviceId, printerConfig.Obico, printerConfig.Name);

        var deviceSecret = _discoveryServer.GetDeviceSecret(deviceId);
        if (string.IsNullOrEmpty(deviceSecret))
        {
            Log($"Failed to get device secret for {printerConfig.Name}");
            return null;
        }

        // Create CTS for this discovery
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock)
        {
            if (_discoveryTasks.TryGetValue(deviceId, out var existingCts))
            {
                existingCts.Cancel();
            }
            _discoveryTasks[deviceId] = cts;
        }

        // Start announcement loop
        var announcementTask = Task.Run(async () =>
        {
            var server = new ObicoServerConnection(serverUrl, ""); // No auth token yet
            string? oneTimePasscode = null;

            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var request = new UnlinkedRequest
                        {
                            DeviceId = deviceId,
                            DeviceSecret = deviceSecret,
                            Hostname = Environment.MachineName,
                            Port = 46793,
                            Os = "Linux",
                            Arch = GetArchitecture(),
                            HostOrIp = GetLocalIpAddress(),
                            OneTimePasscode = oneTimePasscode,
                            PluginVersion = GetVersion(),
                            Agent = "moonraker_obico",  // Use moonraker_obico for compatibility
                            PrinterProfile = "Unknown",
                            MachineType = "Klipper",
                            RpiModel = GetPlatformModel()
                        };

                        var response = await server.AnnounceUnlinkedAsync(request, cts.Token);

                        if (response != null)
                        {
                            // Got one-time passcode
                            if (!string.IsNullOrEmpty(response.OneTimePasscode))
                            {
                                oneTimePasscode = response.OneTimePasscode;
                                Log($"[{printerConfig.Name}] One-time passcode: {FormatPasscode(oneTimePasscode)}");
                            }

                            // Got verification code - linking succeeded!
                            if (!string.IsNullOrEmpty(response.VerificationCode))
                            {
                                Log($"[{printerConfig.Name}] Received verification code, completing link...");

                                var verifyResponse = await server.VerifyLinkCodeAsync(response.VerificationCode, cts.Token);

                                if (verifyResponse?.Printer?.AuthToken != null)
                                {
                                    // Update printer name on server to match ACProxyCam config
                                    _ = TryUpdatePrinterNameAsync(
                                        serverUrl,
                                        verifyResponse.Printer.AuthToken,
                                        verifyResponse.Printer.Id,
                                        printerConfig.Name,
                                        verifyResponse.Printer.Name,
                                        cts.Token);

                                    _discoveryServer.MarkLinked(deviceId, verifyResponse.Printer.AuthToken);
                                    return verifyResponse.Printer.AuthToken;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log($"[{printerConfig.Name}] Discovery announcement failed: {ex.Message}");
                    }

                    // Wait before next announcement (2 seconds like moonraker-obico)
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                }
            }
            finally
            {
                server.Dispose();
            }

            return null;
        }, cts.Token);

        try
        {
            // Wait for discovery to complete (with timeout)
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(10), ct);
            var completedTask = await Task.WhenAny(announcementTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Log($"[{printerConfig.Name}] Discovery timeout - use manual 6-digit code");
                cts.Cancel();
                return null;
            }

            return await announcementTask;
        }
        finally
        {
            // Cleanup
            lock (_lock)
            {
                _discoveryTasks.Remove(deviceId);
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Stop discovery for a printer.
    /// </summary>
    public void StopDiscovery(string deviceId)
    {
        lock (_lock)
        {
            if (_discoveryTasks.TryGetValue(deviceId, out var cts))
            {
                cts.Cancel();
                _discoveryTasks.Remove(deviceId);
            }
        }

        _discoveryServer.UnregisterPrinter(deviceId);
    }

    /// <summary>
    /// Start manual linking for a printer.
    /// Announces to Obico server and waits for user to enter the code in Obico app.
    /// Does NOT require the discovery server to be running.
    /// </summary>
    /// <param name="printerConfig">Printer configuration</param>
    /// <param name="serverUrl">Obico server URL</param>
    /// <param name="onPasscodeReceived">Callback when one-time passcode is received</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>LinkingResult with all fields populated</returns>
    public async Task<LinkingResult> StartManualLinkingAsync(
        PrinterConfig printerConfig,
        string serverUrl,
        Action<string>? onPasscodeReceived = null,
        CancellationToken ct = default)
    {
        // Generate or use existing device ID
        var deviceId = printerConfig.Obico.ObicoDeviceId;
        if (string.IsNullOrEmpty(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N");
            printerConfig.Obico.ObicoDeviceId = deviceId;
        }

        // Generate device secret locally (no discovery server needed)
        var deviceSecret = GenerateDeviceSecret();
        printerConfig.Obico.DeviceSecret = deviceSecret;

        Log($"[{printerConfig.Name}] Starting manual linking...");

        using var server = new ObicoServerConnection(serverUrl, "");
        string? oneTimePasscode = null;
        string? lastReportedPasscode = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var request = new UnlinkedRequest
                    {
                        DeviceId = deviceId,
                        DeviceSecret = deviceSecret,
                        Hostname = Environment.MachineName,
                        Port = 0, // Not listening for discovery
                        Os = "Linux",
                        Arch = GetArchitecture(),
                        HostOrIp = GetLocalIpAddress(),
                        OneTimePasscode = oneTimePasscode,
                        PluginVersion = GetVersion(),
                        Agent = "moonraker_obico",
                        PrinterProfile = "Unknown",
                        MachineType = "Klipper",
                        RpiModel = GetPlatformModel()
                    };

                    var response = await server.AnnounceUnlinkedAsync(request, ct);

                    if (response != null)
                    {
                        // Got one-time passcode
                        if (!string.IsNullOrEmpty(response.OneTimePasscode))
                        {
                            oneTimePasscode = response.OneTimePasscode;

                            // Only report passcode once (or if it changed)
                            if (oneTimePasscode != lastReportedPasscode)
                            {
                                lastReportedPasscode = oneTimePasscode;
                                Log($"[{printerConfig.Name}] One-time passcode: {FormatPasscode(oneTimePasscode)}");
                                onPasscodeReceived?.Invoke(oneTimePasscode);
                            }
                        }

                        // Got verification code - linking succeeded!
                        if (!string.IsNullOrEmpty(response.VerificationCode))
                        {
                            Log($"[{printerConfig.Name}] Received verification code, completing link...");

                            var verifyResponse = await server.VerifyLinkCodeAsync(response.VerificationCode, ct);

                            if (verifyResponse?.Printer?.AuthToken != null)
                            {
                                // Update printer name on server to match ACProxyCam config
                                var updatedName = await TryUpdatePrinterNameAsync(
                                    serverUrl,
                                    verifyResponse.Printer.AuthToken,
                                    verifyResponse.Printer.Id,
                                    printerConfig.Name,
                                    verifyResponse.Printer.Name,
                                    ct);

                                Log($"[{printerConfig.Name}] Linked successfully to Obico as '{updatedName}'");

                                return new LinkingResult
                                {
                                    Success = true,
                                    AuthToken = verifyResponse.Printer.AuthToken,
                                    PrinterName = updatedName,
                                    PrinterId = verifyResponse.Printer.Id,
                                    IsPro = verifyResponse.Printer.IsPro
                                };
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"[{printerConfig.Name}] Announcement failed: {ex.Message}");
                }

                // Wait before next announcement
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }

        return new LinkingResult
        {
            Success = false,
            Error = ct.IsCancellationRequested ? "Cancelled" : "Timeout"
        };
    }

    private static string GenerateDeviceSecret()
    {
        var bytes = new byte[32];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Link printer using 6-digit code (manual linking).
    /// </summary>
    public async Task<LinkingResult> LinkWithCodeAsync(
        PrinterConfig printerConfig,
        string serverUrl,
        string code,
        CancellationToken ct = default)
    {
        Log($"[{printerConfig.Name}] Verifying code: {code}");

        try
        {
            // First test server connectivity
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(serverUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            try
            {
                var testResponse = await httpClient.GetAsync("/api/v1/", ct);
                // Any response is OK, just checking connectivity
            }
            catch (Exception ex)
            {
                return new LinkingResult
                {
                    Success = false,
                    Error = $"Cannot connect to Obico server: {ex.Message}"
                };
            }

            // Verify the code
            using var server = new ObicoServerConnection(serverUrl, "");
            var response = await server.VerifyLinkCodeAsync(code, ct);

            if (response?.Printer == null)
            {
                return new LinkingResult
                {
                    Success = false,
                    Error = "Invalid or expired code"
                };
            }

            if (string.IsNullOrEmpty(response.Printer.AuthToken))
            {
                return new LinkingResult
                {
                    Success = false,
                    Error = "Server did not return auth token"
                };
            }

            // Generate device ID if not set (must be hex without dashes)
            if (string.IsNullOrEmpty(printerConfig.Obico.ObicoDeviceId))
            {
                printerConfig.Obico.ObicoDeviceId = Guid.NewGuid().ToString("N");
            }

            // Update printer name on server to match ACProxyCam config
            var updatedName = await TryUpdatePrinterNameAsync(
                serverUrl,
                response.Printer.AuthToken,
                response.Printer.Id,
                printerConfig.Name,
                response.Printer.Name,
                ct);

            Log($"[{printerConfig.Name}] Linked successfully to Obico as '{updatedName}'");

            return new LinkingResult
            {
                Success = true,
                AuthToken = response.Printer.AuthToken,
                PrinterName = updatedName,
                PrinterId = response.Printer.Id,
                IsPro = response.Printer.IsPro
            };
        }
        catch (Exception ex)
        {
            return new LinkingResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Test connection to Obico server.
    /// </summary>
    public async Task<(bool Success, string? Error)> TestServerConnectionAsync(
        string serverUrl,
        CancellationToken ct = default)
    {
        try
        {
            using var httpClient = new HttpClient
            {
                BaseAddress = new Uri(serverUrl),
                Timeout = TimeSpan.FromSeconds(10)
            };

            var response = await httpClient.GetAsync("/api/v1/", ct);

            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            return (false, $"Server returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Link printer directly using Obico credentials.
    /// Bypasses browser-based auto-discovery by logging in and creating a verification code directly.
    /// </summary>
    /// <param name="printerConfig">The printer configuration to link</param>
    /// <param name="serverUrl">Obico server URL</param>
    /// <param name="email">User's email address</param>
    /// <param name="password">User's password</param>
    /// <param name="allowSelfSignedCerts">Allow self-signed SSL certificates</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<LinkingResult> LinkWithCredentialsAsync(
        PrinterConfig printerConfig,
        string serverUrl,
        string email,
        string password,
        bool allowSelfSignedCerts = false,
        CancellationToken ct = default)
    {
        try
        {
            using var server = new ObicoServerConnection(serverUrl, "");

            // Step 1: Login to Obico server
            Log($"[{printerConfig.Name}] Logging in to Obico server...");
            var loginResult = await server.LoginAsync(email, password, allowSelfSignedCerts, ct);

            if (!loginResult.Success)
            {
                Log($"[{printerConfig.Name}] Login failed: {loginResult.Error}");
                return new LinkingResult
                {
                    Success = false,
                    Error = loginResult.Error ?? "Login failed"
                };
            }

            Log($"[{printerConfig.Name}] Login successful");

            // Step 2: Get verification code
            Log($"[{printerConfig.Name}] Getting verification code...");
            var codeResult = await server.GetVerificationCodeAsync(
                loginResult.Cookies!,
                loginResult.CsrfToken!,
                allowSelfSignedCerts,
                ct);

            if (!codeResult.Success)
            {
                Log($"[{printerConfig.Name}] Failed to get verification code: {codeResult.Error}");
                return new LinkingResult
                {
                    Success = false,
                    Error = codeResult.Error ?? "Failed to get verification code"
                };
            }

            Log($"[{printerConfig.Name}] Got verification code: {codeResult.Code}");

            // Step 3: Verify code and get auth token
            Log($"[{printerConfig.Name}] Verifying code and linking printer...");
            var verifyResult = await server.VerifyLinkCodeAsync(codeResult.Code!, ct);

            if (verifyResult?.Printer?.AuthToken == null)
            {
                Log($"[{printerConfig.Name}] Verification failed");
                return new LinkingResult
                {
                    Success = false,
                    Error = "Verification failed - no auth token received"
                };
            }

            // Generate device ID if not set
            if (string.IsNullOrEmpty(printerConfig.Obico.ObicoDeviceId))
            {
                printerConfig.Obico.ObicoDeviceId = Guid.NewGuid().ToString("N");
            }

            // Update printer name on server to match ACProxyCam config
            var updatedName = await TryUpdatePrinterNameAsync(
                serverUrl,
                verifyResult.Printer.AuthToken,
                verifyResult.Printer.Id,
                printerConfig.Name,
                verifyResult.Printer.Name,
                ct);

            Log($"[{printerConfig.Name}] Linked successfully to Obico as '{updatedName}'");

            return new LinkingResult
            {
                Success = true,
                AuthToken = verifyResult.Printer.AuthToken,
                PrinterName = updatedName,
                PrinterId = verifyResult.Printer.Id,
                IsPro = verifyResult.Printer.IsPro
            };
        }
        catch (Exception ex)
        {
            Log($"[{printerConfig.Name}] Linking failed: {ex.Message}");
            return new LinkingResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Unlink a printer from Obico.
    /// </summary>
    public void Unlink(PrinterConfig printerConfig)
    {
        printerConfig.Obico.AuthToken = "";
        printerConfig.Obico.DeviceSecret = "";
        printerConfig.Obico.IsPro = false;
        printerConfig.Obico.ObicoName = "";

        Log($"[{printerConfig.Name}] Unlinked from Obico");
    }

    /// <summary>
    /// Try to update the printer name on the Obico server after successful linking.
    /// This is a best-effort operation - if it fails, linking still succeeded.
    /// </summary>
    private async Task<string> TryUpdatePrinterNameAsync(
        string serverUrl,
        string authToken,
        int printerId,
        string newName,
        string currentName,
        CancellationToken ct = default)
    {
        if (printerId <= 0 || string.IsNullOrEmpty(newName))
        {
            return currentName;
        }

        try
        {
            using var server = new ObicoServerConnection(serverUrl, authToken);
            var (success, error) = await server.UpdatePrinterNameAsync(printerId, newName, ct);

            if (success)
            {
                Log($"Printer name updated to '{newName}' on Obico server");
                return newName;
            }
            else
            {
                Log($"Could not update printer name: {error}");
            }
        }
        catch (Exception ex)
        {
            Log($"Could not update printer name: {ex.Message}");
        }

        return currentName;
    }

    private void OnDiscoveryCompleted(object? sender, DiscoveryCompletedEventArgs e)
    {
        LinkingCompleted?.Invoke(this, new LinkingCompletedEventArgs
        {
            DeviceId = e.DeviceId,
            PrinterName = e.PrinterName,
            AuthToken = e.AuthToken
        });
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            // Get the first non-loopback IPv4 address
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = iface.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ip = addr.Address.ToString();
                        if (!ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                        {
                            return ip;
                        }
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    private static string FormatPasscode(string passcode)
    {
        // Format as XXX-XXXX for readability
        if (passcode.Length == 7)
        {
            return $"{passcode[..3]}-{passcode[3..]}";
        }
        return passcode;
    }

    private static string GetVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString(3) ?? "1.0.0";
    }

    private static string GetArchitecture()
    {
        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "i686",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            System.Runtime.InteropServices.Architecture.Arm => "armv7l",
            _ => "unknown"
        };
    }

    private static string? GetPlatformModel()
    {
        try
        {
            // Try to read from device tree (Linux)
            const string modelPath = "/proc/device-tree/model";
            if (File.Exists(modelPath))
            {
                var model = File.ReadAllText(modelPath).TrimEnd('\0', '\n', '\r');
                if (!string.IsNullOrEmpty(model))
                    return model;
            }
        }
        catch { }

        return null;
    }

    private void Log(string message)
    {
        StatusChanged?.Invoke(this, message);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        Stop();
        _discoveryServer.Dispose();
    }
}

public class LinkingResult
{
    public bool Success { get; set; }
    public string? AuthToken { get; set; }
    public string? PrinterName { get; set; }
    public int PrinterId { get; set; }
    public bool IsPro { get; set; }
    public string? Error { get; set; }
}

public class LinkingCompletedEventArgs : EventArgs
{
    public string DeviceId { get; set; } = "";
    public string PrinterName { get; set; } = "";
    public string AuthToken { get; set; } = "";
}
