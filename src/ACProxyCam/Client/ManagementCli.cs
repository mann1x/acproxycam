// ManagementCli.cs - Interactive terminal management interface

using ACProxyCam.Daemon;
using ACProxyCam.Models;
using Spectre.Console;

namespace ACProxyCam.Client;

/// <summary>
/// Interactive terminal management interface using IConsoleUI abstraction.
/// </summary>
public class ManagementCli
{
    private readonly IConsoleUI _ui;
    private IpcClient? _ipcClient;

    public ManagementCli(IConsoleUI ui)
    {
        _ui = ui;
    }

    public async Task<int> RunAsync()
    {
        // Display header
        _ui.WriteHeader(Program.Version);

        // Check if running as root/sudo
        if (!IsRunningAsRoot())
        {
            _ui.WriteError("Error: This application requires root privileges.");
            _ui.WriteWarning("Please run with sudo: sudo acproxycam");
            return 1;
        }

        // Check if daemon is running
        var daemonRunning = IpcClient.IsDaemonRunning();

        if (!daemonRunning)
        {
            // Check if service is installed
            var serviceInstalled = IsServiceInstalled();

            if (!serviceInstalled)
            {
                return await HandleInstallationAsync();
            }
            else
            {
                // Service installed but not running
                _ui.WriteWarning("Service is installed but not running.");
                var startService = _ui.Confirm("Would you like to start the service?");
                if (startService)
                {
                    var startError = await StartServiceAsync();
                    if (startError != null)
                    {
                        _ui.WriteError($"Error: {startError}");
                        _ui.WriteLine();

                        // Offer recovery options
                        var choice = _ui.SelectOne(
                            "What would you like to do?",
                            new[] { "Reinstall service", "Uninstall service", "Exit" });

                        switch (choice)
                        {
                            case "Reinstall service":
                                await UninstallServiceFilesAsync();
                                return await InstallAsync();
                            case "Uninstall service":
                                return await UninstallAsync(false);
                            default:
                                return 1;
                        }
                    }
                }
                else
                {
                    return 0;
                }
            }
        }

        // Connect to daemon
        _ipcClient = new IpcClient();
        if (!_ipcClient.Connect())
        {
            _ui.WriteError("Error: Cannot connect to daemon.");
            return 1;
        }

        // Enter management loop
        return await ManagementLoopAsync();
    }

    public async Task<int> InstallAsync()
    {
        _ui.WriteHeader(Program.Version);

        // Check if running as root/sudo
        if (!IsRunningAsRoot())
        {
            _ui.WriteError("Error: This application requires root privileges.");
            _ui.WriteWarning("Please run with sudo: sudo acproxycam --install");
            return 1;
        }

        // Check if already installed and running
        if (IpcClient.IsDaemonRunning())
        {
            _ui.WriteSuccess("Service is already installed and running.");
            return 0;
        }

        // Check if service file exists
        if (IsServiceInstalled())
        {
            _ui.WriteLine("Service is installed but not running. Starting...");
            var startError = await StartServiceAsync();
            if (startError != null)
            {
                _ui.WriteError($"Error starting service: {startError}");
                return 1;
            }
            _ui.WriteSuccess("Service started successfully.");
            return 0;
        }

        // Full installation
        _ui.WriteLine("Installing ACProxyCam...");
        _ui.WriteLine();

        // Step 1: Check FFmpeg
        _ui.WriteInfo("[1/5] Checking FFmpeg...");
        if (!await CheckAndInstallFfmpegAsync())
        {
            return 1;
        }

        // Step 2: Select listening interfaces
        _ui.WriteInfo("[2/5] Selecting listening interfaces...");
        var interfaces = await SelectInterfacesAsync();
        if (interfaces == null)
        {
            return 0; // User cancelled
        }

        // Step 3: Create user and directories
        _ui.WriteInfo("[3/5] Creating user and directories...");
        string? installError = await CreateUserAndDirectoriesAsync();
        if (installError != null)
        {
            _ui.WriteError($"Error creating user/directories: {installError}");
            _ui.WaitForKey("Press any key to exit...");
            return 1;
        }

        // Step 4: Create configuration
        _ui.WriteInfo("[4/5] Creating configuration file...");
        try
        {
            var config = new AppConfig
            {
                ListenInterfaces = interfaces
            };
            await ConfigManager.SaveAsync(config);
            _ui.WriteSuccess("  Created /etc/acproxycam/config.json");

            // Set ownership of config file to acproxycam user
            var chownProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/chown",
                Arguments = "acproxycam:acproxycam /etc/acproxycam/config.json",
                UseShellExecute = false
            });
            await chownProcess!.WaitForExitAsync();
            _ui.WriteSuccess("  Set config file ownership to acproxycam");
        }
        catch (Exception ex)
        {
            _ui.WriteError($"Error creating configuration: {ex.Message}");
            _ui.WaitForKey("Press any key to exit...");
            return 1;
        }

        // Step 5: Install systemd service
        _ui.WriteInfo("[5/5] Installing systemd service...");
        installError = await InstallSystemdServiceAsync();
        if (installError != null)
        {
            _ui.WriteError($"Error installing service: {installError}");
            _ui.WaitForKey("Press any key to exit...");
            return 1;
        }

        // Start service
        _ui.WriteInfo("Starting service...");
        installError = await StartServiceAsync();

        if (installError != null)
        {
            _ui.WriteError($"Error starting service: {installError}");
            _ui.WriteLine();

            // Offer recovery options
            var choice = _ui.SelectOne(
                "What would you like to do?",
                new[] { "Retry starting service", "Uninstall service", "Exit" });

            switch (choice)
            {
                case "Retry starting service":
                    var retryError = await StartServiceAsync();
                    if (retryError != null)
                    {
                        _ui.WriteError($"Error: {retryError}");
                        _ui.WaitForKey("Press any key to exit...");
                        return 1;
                    }
                    break;
                case "Uninstall service":
                    await UninstallServiceFilesAsync();
                    _ui.WriteSuccess("Service uninstalled.");
                    return 0;
                default:
                    return 1;
            }
        }

        _ui.WriteSuccess("Installation complete!");
        _ui.WriteLine();

        // Continue to management
        await Task.Delay(1000);
        _ipcClient = new IpcClient();
        _ipcClient.Connect();

        return await ManagementLoopAsync();
    }

    public async Task<int> UninstallAsync(bool keepConfig)
    {
        _ui.WriteHeader(Program.Version);

        // Check if running as root/sudo
        if (!IsRunningAsRoot())
        {
            _ui.WriteError("Error: This application requires root privileges.");
            _ui.WriteWarning("Please run with sudo: sudo acproxycam --uninstall");
            return 1;
        }

        if (!_ui.Confirm("Are you sure you want to uninstall ACProxyCam?", false))
        {
            return 0;
        }

        if (!keepConfig)
        {
            keepConfig = _ui.Confirm("Keep configuration files?", true);
        }

        _ui.WriteLine("Uninstalling ACProxyCam...");

        await _ui.WithStatusAsync("Stopping service...", async () =>
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "stop acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
        });

        await _ui.WithStatusAsync("Disabling service...", async () =>
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "disable acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
        });

        await _ui.WithStatusAsync("Removing service file...", async () =>
        {
            if (File.Exists("/etc/systemd/system/acproxycam.service"))
            {
                File.Delete("/etc/systemd/system/acproxycam.service");
            }
            await Task.CompletedTask;
        });

        await _ui.WithStatusAsync("Reloading systemd...", async () =>
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "daemon-reload",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
        });

        await _ui.WithStatusAsync("Removing binary...", async () =>
        {
            if (File.Exists("/usr/local/bin/acproxycam"))
            {
                File.Delete("/usr/local/bin/acproxycam");
            }
            await Task.CompletedTask;
        });

        if (!keepConfig)
        {
            await _ui.WithStatusAsync("Removing configuration...", async () =>
            {
                if (Directory.Exists("/etc/acproxycam"))
                {
                    Directory.Delete("/etc/acproxycam", true);
                }
                await Task.CompletedTask;
            });
        }
        else
        {
            _ui.WriteLine("Keeping configuration files.");
        }

        await _ui.WithStatusAsync("Removing logs...", async () =>
        {
            if (Directory.Exists("/var/log/acproxycam"))
            {
                Directory.Delete("/var/log/acproxycam", true);
            }
            if (Directory.Exists("/var/lib/acproxycam"))
            {
                Directory.Delete("/var/lib/acproxycam", true);
            }
            if (File.Exists("/etc/logrotate.d/acproxycam"))
            {
                File.Delete("/etc/logrotate.d/acproxycam");
            }
            await Task.CompletedTask;
        });

        await _ui.WithStatusAsync("Removing user...", async () =>
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-c \"userdel acproxycam 2>/dev/null || true\"",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
        });

        _ui.WriteLine();
        _ui.WriteSuccess("ACProxyCam has been uninstalled.");
        return 0;
    }

    private bool IsRunningAsRoot()
    {
        try
        {
            return Environment.GetEnvironmentVariable("EUID") == "0" ||
                   Environment.GetEnvironmentVariable("USER") == "root" ||
                   (Environment.GetEnvironmentVariable("SUDO_USER") != null);
        }
        catch
        {
            return false;
        }
    }

    private bool IsServiceInstalled()
    {
        return File.Exists("/etc/systemd/system/acproxycam.service");
    }

    private async Task<int> HandleInstallationAsync()
    {
        _ui.WriteWarning("ACProxyCam is not installed.");
        _ui.WriteLine();

        var choice = _ui.SelectOne(
            "What would you like to do?",
            new[] { "Install ACProxyCam", "Quit" });

        if (choice == "Quit")
        {
            return 0;
        }

        return await InstallAsync();
    }

    private async Task<bool> CheckAndInstallFfmpegAsync()
    {
        var ffmpegInstalled = File.Exists("/usr/bin/ffmpeg") || File.Exists("/usr/local/bin/ffmpeg");

        if (ffmpegInstalled)
        {
            _ui.WriteSuccess("FFmpeg is installed.");
            return true;
        }

        _ui.WriteWarning("FFmpeg is not installed.");

        // Detect package manager
        string? packageManager = null;
        string? installCommand = null;

        if (File.Exists("/usr/bin/apt"))
        {
            packageManager = "apt";
            installCommand = "apt install -y ffmpeg";
        }
        else if (File.Exists("/usr/bin/dnf"))
        {
            packageManager = "dnf";
            installCommand = "dnf install -y ffmpeg";
        }
        else if (File.Exists("/usr/bin/pacman"))
        {
            packageManager = "pacman";
            installCommand = "pacman -S --noconfirm ffmpeg";
        }

        if (packageManager != null)
        {
            var install = _ui.Confirm($"Would you like to install FFmpeg using {packageManager}?");
            if (install)
            {
                await _ui.WithStatusAsync("Installing FFmpeg...", async () =>
                {
                    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"{installCommand}\"",
                        UseShellExecute = false
                    });
                    await process!.WaitForExitAsync();
                });

                return true;
            }
        }

        _ui.WriteError("FFmpeg is required for ACProxyCam to function.");
        _ui.WriteWarning("Please install FFmpeg manually and try again.");
        return false;
    }

    private async Task<List<string>?> SelectInterfacesAsync()
    {
        var interfaces = GetNetworkInterfaces();

        // First, ask what to do with a single-select prompt (Escape to cancel)
        var actionChoices = new List<string> { "All interfaces (0.0.0.0)", "Select specific interfaces" };
        var action = _ui.SelectOneWithEscape("Select listening interfaces for MJPEG streams:", actionChoices);

        if (action == null)
        {
            return null;
        }

        if (action == "All interfaces (0.0.0.0)")
        {
            return new List<string> { "0.0.0.0" };
        }

        // Show multi-select for specific interfaces (without Cancel)
        var choices = new List<string> { "localhost (127.0.0.1)" };
        choices.AddRange(interfaces.Select(i => $"{i.Name} ({i.Address})"));

        var selected = _ui.SelectMany(
            "Select interfaces (Space to toggle, Enter to confirm):",
            choices,
            "(Press space to toggle, enter to accept)");

        if (selected.Count == 0)
        {
            _ui.WriteWarning("No interfaces selected. Operation cancelled.");
            await Task.Delay(1500);
            return null;
        }

        var result = new List<string>();

        if (selected.Contains("localhost (127.0.0.1)"))
        {
            result.Add("127.0.0.1");
        }

        foreach (var iface in interfaces)
        {
            if (selected.Contains($"{iface.Name} ({iface.Address})"))
            {
                result.Add(iface.Address);
            }
        }

        return result;
    }

    private List<(string Name, string Address)> GetNetworkInterfaces()
    {
        var result = new List<(string, string)>();

        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (iface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;

                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        result.Add((iface.Name, addr.Address.ToString()));
                    }
                }
            }
        }
        catch { }

        return result;
    }

    private async Task<string?> CreateUserAndDirectoriesAsync()
    {
        try
        {
            // Create acproxycam user if doesn't exist
            _ui.WriteInfo("Creating system user...");
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-c \"id acproxycam &>/dev/null || useradd -r -s /bin/false acproxycam\"",
                UseShellExecute = false,
                RedirectStandardError = true
            });
            await process!.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                return $"Failed to create user: {error}";
            }
            _ui.WriteSuccess("  Created user 'acproxycam'");

            // Create directories
            _ui.WriteInfo("Creating directories...");
            Directory.CreateDirectory("/etc/acproxycam");
            _ui.WriteSuccess("  Created /etc/acproxycam");
            Directory.CreateDirectory("/var/log/acproxycam");
            _ui.WriteSuccess("  Created /var/log/acproxycam");
            Directory.CreateDirectory("/var/lib/acproxycam");
            _ui.WriteSuccess("  Created /var/lib/acproxycam");

            // Set ownership
            _ui.WriteInfo("Setting directory ownership...");
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-c \"chown acproxycam:acproxycam /etc/acproxycam /var/log/acproxycam /var/lib/acproxycam\"",
                UseShellExecute = false,
                RedirectStandardError = true
            });
            await process!.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                return $"Failed to set ownership: {error}";
            }
            _ui.WriteSuccess("  Set ownership to acproxycam:acproxycam");

            return null; // Success
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> InstallSystemdServiceAsync()
    {
        try
        {
            var serviceContent = @"[Unit]
Description=ACProxyCam - Anycubic Camera Proxy
# Soft ordering only - no Requires/Wants/BindsTo that could block boot
After=network.target

[Service]
Type=simple
User=acproxycam
Group=acproxycam
Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/acproxycam
RuntimeDirectory=acproxycam
RuntimeDirectoryMode=0755
ExecStart=/usr/local/bin/acproxycam --daemon

# Short timeouts - never hang
TimeoutStartSec=30
TimeoutStopSec=10

# Rate-limited restarts - max 3 attempts per 5 minutes, then give up
Restart=on-failure
RestartSec=60
StartLimitIntervalSec=300
StartLimitBurst=3

[Install]
# Only multi-user target - never blocks boot
WantedBy=multi-user.target
";

            await File.WriteAllTextAsync("/etc/systemd/system/acproxycam.service", serviceContent);
            _ui.WriteSuccess("  Created /etc/systemd/system/acproxycam.service");

            // Copy binary
            _ui.WriteInfo("Installing binary...");
            var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe != null && currentExe != "/usr/local/bin/acproxycam")
            {
                File.Copy(currentExe, "/usr/local/bin/acproxycam", true);
                _ui.WriteSuccess("  Copied binary to /usr/local/bin/acproxycam");
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    Arguments = "+x /usr/local/bin/acproxycam",
                    UseShellExecute = false,
                    RedirectStandardError = true
                });
                await process!.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    return $"Failed to set executable permission: {error}";
                }
                _ui.WriteSuccess("  Set executable permission");
            }

            // Reload systemd
            _ui.WriteInfo("Configuring systemd...");
            var reloadProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "daemon-reload",
                UseShellExecute = false,
                RedirectStandardError = true
            });
            await reloadProcess!.WaitForExitAsync();
            if (reloadProcess.ExitCode != 0)
            {
                var error = await reloadProcess.StandardError.ReadToEndAsync();
                return $"Failed to reload systemd: {error}";
            }
            _ui.WriteSuccess("  Reloaded systemd daemon");

            // Enable service
            var enableProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "enable acproxycam",
                UseShellExecute = false,
                RedirectStandardError = true
            });
            await enableProcess!.WaitForExitAsync();
            if (enableProcess.ExitCode != 0)
            {
                var error = await enableProcess.StandardError.ReadToEndAsync();
                return $"Failed to enable service: {error}";
            }
            _ui.WriteSuccess("  Enabled service for autostart");

            // Install logrotate configuration
            _ui.WriteInfo("Installing logrotate configuration...");
            var logrotateContent = @"/var/log/acproxycam/*.log {
    daily
    missingok
    rotate 7
    compress
    delaycompress
    notifempty
    create 0640 acproxycam acproxycam
    sharedscripts
    postrotate
        /bin/systemctl reload acproxycam > /dev/null 2>&1 || true
    endscript
}
";
            await File.WriteAllTextAsync("/etc/logrotate.d/acproxycam", logrotateContent);
            _ui.WriteSuccess("  Created /etc/logrotate.d/acproxycam");

            return null; // Success
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task<string?> StartServiceAsync()
    {
        try
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "start acproxycam",
                UseShellExecute = false,
                RedirectStandardError = true
            });
            await process!.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                return $"Failed to start service: {error}";
            }
            _ui.WriteSuccess("  Service started");

            // Wait for daemon to be ready
            _ui.WriteInfo("Waiting for daemon to be ready...");
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                if (IpcClient.IsDaemonRunning())
                {
                    _ui.WriteSuccess("  Daemon is ready");
                    return null; // Success
                }
            }

            return "Service started but daemon not responding";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // Auto-refresh interval in milliseconds
    private const int RefreshIntervalMs = 2000;

    private enum MenuAction
    {
        None,
        Quit,
        ToggleService,
        RestartService,
        Uninstall,
        ChangeInterfaces,
        AddPrinter,
        DeletePrinter,
        ModifyPrinter,
        TogglePause,
        ShowDetails,
        ToggleLed,
        BedMesh
    }

    private async Task<int> ManagementLoopAsync()
    {
        while (true)
        {
            // Clear and render dashboard using Live display
            AnsiConsole.Clear();

            MenuAction action = MenuAction.None;

            await AnsiConsole.Live(Text.Empty)
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    var lastRefresh = DateTime.MinValue;

                    while (action == MenuAction.None)
                    {
                        // Build and render dashboard
                        var dashboard = await BuildDashboardAsync();
                        ctx.UpdateTarget(dashboard);
                        lastRefresh = DateTime.UtcNow;

                        // Wait for key input with timeout for auto-refresh
                        while (action == MenuAction.None)
                        {
                            // Check for timeout - need to refresh
                            if ((DateTime.UtcNow - lastRefresh).TotalMilliseconds >= RefreshIntervalMs)
                                break;

                            // Check for key input (non-blocking)
                            if (Console.KeyAvailable)
                            {
                                var key = Console.ReadKey(true).Key;
                                action = key switch
                                {
                                    ConsoleKey.Q => MenuAction.Quit,
                                    ConsoleKey.Escape => MenuAction.Quit,
                                    ConsoleKey.S => MenuAction.ToggleService,
                                    ConsoleKey.R => MenuAction.RestartService,
                                    ConsoleKey.U => MenuAction.Uninstall,
                                    ConsoleKey.L => MenuAction.ChangeInterfaces,
                                    ConsoleKey.A => MenuAction.AddPrinter,
                                    ConsoleKey.D => MenuAction.DeletePrinter,
                                    ConsoleKey.M => MenuAction.ModifyPrinter,
                                    ConsoleKey.Spacebar => MenuAction.TogglePause,
                                    ConsoleKey.Enter => MenuAction.ShowDetails,
                                    ConsoleKey.T => MenuAction.ToggleLed,
                                    ConsoleKey.B => MenuAction.BedMesh,
                                    _ => MenuAction.None
                                };
                                break; // Exit inner loop to refresh or handle action
                            }

                            // Small sleep to avoid busy-waiting
                            await Task.Delay(50);
                        }
                    }
                });

            // Now outside Live context - handle actions that need interactive prompts
            switch (action)
            {
                case MenuAction.Quit:
                    _ipcClient?.Dispose();
                    _ipcClient = null;
                    return 0;

                case MenuAction.ToggleService:
                    await ToggleServiceAsync();
                    break;

                case MenuAction.RestartService:
                    await RestartServiceAsync();
                    break;

                case MenuAction.Uninstall:
                    AnsiConsole.Clear();
                    var result = await UninstallFromMenuAsync();
                    if (result == 0) return 0;
                    break;

                case MenuAction.ChangeInterfaces:
                    AnsiConsole.Clear();
                    await ChangeInterfacesAsync();
                    break;

                case MenuAction.AddPrinter:
                    AnsiConsole.Clear();
                    await AddPrinterAsync();
                    break;

                case MenuAction.DeletePrinter:
                    AnsiConsole.Clear();
                    await DeletePrinterAsync();
                    break;

                case MenuAction.ModifyPrinter:
                    AnsiConsole.Clear();
                    await ModifyPrinterAsync();
                    break;

                case MenuAction.TogglePause:
                    AnsiConsole.Clear();
                    await TogglePrinterPauseAsync();
                    break;

                case MenuAction.ShowDetails:
                    AnsiConsole.Clear();
                    await ShowPrinterDetailsAsync();
                    break;

                case MenuAction.ToggleLed:
                    AnsiConsole.Clear();
                    await TogglePrinterLedAsync();
                    break;

                case MenuAction.BedMesh:
                    AnsiConsole.Clear();
                    await ShowBedMeshMenuFromMainAsync();
                    break;
            }
        }
    }

    private async Task<Rows> BuildDashboardAsync()
    {
        // Fetch all data first
        var (statusSuccess, status, _) = await _ipcClient!.GetStatusAsync();
        var (printersSuccess, printers, _) = await _ipcClient!.ListPrintersAsync();

        var renderables = new List<Spectre.Console.Rendering.IRenderable>();

        // === HEADER TABLE ===
        var serviceRunning = statusSuccess && status?.Running == true;
        var serviceStatus = serviceRunning ? "[green]● Running[/]" : "[red]● Stopped[/]";

        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Purple)
            .AddColumn(new TableColumn("[bold purple]ACProxyCam[/]").Centered())
            .AddColumn(new TableColumn("Service").Centered())
            .AddColumn(new TableColumn("Printers").Centered())
            .AddColumn(new TableColumn("Active").Centered())
            .AddColumn(new TableColumn("Clients").Centered());

        headerTable.AddRow(
            $"[grey]v{Program.Version}[/]",
            serviceStatus,
            $"[bold]{status?.PrinterCount ?? 0}[/]",
            $"[green]{status?.ActiveStreamers ?? 0}[/] / [grey]{status?.InactiveStreamers ?? 0}[/]",
            $"[cyan]{status?.TotalClients ?? 0}[/]"
        );

        renderables.Add(headerTable);

        // === SERVICE CONTROLS ===
        renderables.Add(new Markup(
            "[grey]Service:[/] [white][[S]][/][grey]top/Start[/]  [white][[R]][/][grey]estart[/]  [white][[U]][/][grey]ninstall[/]  [white][[L]][/][grey]isten[/]  [white][[Q]][/][grey]uit[/]"
        ));
        renderables.Add(Text.Empty);

        // === PRINTERS TABLE ===
        if (!printersSuccess || printers == null || printers.Count == 0)
        {
            renderables.Add(new Panel("[grey]No printers configured. Press [white][[A]][/] to add.[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Grey)
                .Header("[bold]Printers[/]"));
        }
        else
        {
            var printerTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Blue)
                .Title("[bold blue]Printers[/]")
                .AddColumn(new TableColumn("[bold]Name[/]"))
                .AddColumn(new TableColumn("[bold]Type[/]").Centered())
                .AddColumn(new TableColumn("[bold]IP/Hostname[/]"))
                .AddColumn(new TableColumn("[bold]Port[/]").Centered())
                .AddColumn(new TableColumn("[bold]Resolution[/]").Centered())
                .AddColumn(new TableColumn("[bold]FPS[/]").Centered())
                .AddColumn(new TableColumn("[bold]CPU[/]").Centered())
                .AddColumn(new TableColumn("[bold]LED[/]").Centered())
                .AddColumn(new TableColumn("[bold]Status[/]").Centered())
                .AddColumn(new TableColumn("[bold]Clients[/]").Centered());

            foreach (var p in printers)
            {
                var (statusColor, statusIcon) = p.State switch
                {
                    PrinterState.Running => ("green", "●"),
                    PrinterState.Paused => ("yellow", "◐"),
                    PrinterState.Failed => ("red", "✗"),
                    PrinterState.Retrying => ("orange3", "↻"),
                    PrinterState.Connecting => ("blue", "◌"),
                    _ => ("grey", "○")
                };

                var resolution = p.StreamStatus.Width > 0 && p.StreamStatus.Height > 0
                    ? $"{p.StreamStatus.Width}x{p.StreamStatus.Height}"
                    : "[grey]-[/]";

                var fpsDisplay = p.IsIdle
                    ? (p.CurrentFps == 0 ? "[grey]off[/]" : $"[grey]{p.CurrentFps}[/]")
                    : (p.CurrentFps == 0 ? "[green]max[/]" : $"[green]{p.CurrentFps}[/]");
                var cpuDisplay = p.CpuAffinity >= 0 ? $"[cyan]{p.CpuAffinity}[/]" : "[grey]-[/]";
                var clientsDisplay = p.ConnectedClients > 0
                    ? $"[green]{p.ConnectedClients}[/]"
                    : "[grey]0[/]";

                // LED status display
                var ledDisplay = p.CameraLed == null
                    ? "[grey]?[/]"
                    : p.CameraLed.IsOn
                        ? "[yellow]On[/]"
                        : "[grey]Off[/]";

                // Device type display
                var deviceTypeDisplay = string.IsNullOrEmpty(p.DeviceType)
                    ? "[grey]-[/]"
                    : $"[cyan]{Markup.Escape(p.DeviceType)}[/]";

                printerTable.AddRow(
                    $"[white]{Markup.Escape(p.Name)}[/]",
                    deviceTypeDisplay,
                    $"[grey]{Markup.Escape(p.Ip)}[/]",
                    $"[cyan]{p.MjpegPort}[/]",
                    resolution,
                    fpsDisplay,
                    cpuDisplay,
                    ledDisplay,
                    $"[{statusColor}]{statusIcon} {p.State}[/]",
                    clientsDisplay
                );
            }

            renderables.Add(printerTable);
        }

        // === PRINTER CONTROLS ===
        renderables.Add(new Markup(
            "[grey]Printers:[/] [white][[A]][/][grey]dd[/]  [white][[D]][/][grey]elete[/]  [white][[M]][/][grey]odify[/]  [white][[Space]][/][grey]Pause[/]  [white][[T]][/][grey]LED[/]  [white][[B]][/][grey]edMesh[/]  [white][[Enter]][/][grey]Details[/]"
        ));

        return new Rows(renderables);
    }

    private async Task ToggleServiceAsync()
    {
        var (success, status, _) = await _ipcClient!.GetStatusAsync();

        if (success && status?.Running == true)
        {
            // Stop service
            _ui.WriteWarning("Stopping service...");
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "stop acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
            _ipcClient = null;
            _ui.WriteSuccess("Service stopped.");
            await Task.Delay(1000);
        }
        else
        {
            // Start service
            var startError = await StartServiceAsync();
            if (startError != null)
            {
                _ui.WriteError($"Error: {startError}");
                await Task.Delay(2000);
                return;
            }
            _ipcClient = new IpcClient();
            _ipcClient.Connect();
        }
    }

    private async Task RestartServiceAsync()
    {
        await _ui.WithStatusAsync("Restarting service...", async () =>
        {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "restart acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();

            // Wait for daemon to be ready
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(500);
                if (IpcClient.IsDaemonRunning())
                    break;
            }
        });

        // Reconnect
        _ipcClient?.Disconnect();
        _ipcClient = new IpcClient();
        _ipcClient.Connect();
    }

    private async Task UninstallServiceFilesAsync()
    {
        await _ui.WithStatusAsync("Removing service...", async () =>
        {
            // Stop service
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "stop acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();

            // Disable service
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "disable acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();

            // Remove service file
            if (File.Exists("/etc/systemd/system/acproxycam.service"))
            {
                File.Delete("/etc/systemd/system/acproxycam.service");
            }

            // Reload systemd
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "daemon-reload",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();

            // Remove binary
            if (File.Exists("/usr/local/bin/acproxycam"))
            {
                File.Delete("/usr/local/bin/acproxycam");
            }

            // Remove logrotate config
            if (File.Exists("/etc/logrotate.d/acproxycam"))
            {
                File.Delete("/etc/logrotate.d/acproxycam");
            }
        });
    }

    private async Task<int> UninstallFromMenuAsync()
    {
        if (!_ui.Confirm("Are you sure you want to uninstall ACProxyCam?", false))
        {
            return 1;
        }

        var keepConfig = _ui.Confirm("Keep configuration files?", true);

        await _ui.WithStatusAsync("Uninstalling...", async () =>
        {
            // Stop service
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "stop acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();

            // Disable service
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "disable acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();

            // Remove service file
            if (File.Exists("/etc/systemd/system/acproxycam.service"))
            {
                File.Delete("/etc/systemd/system/acproxycam.service");
            }

            // Reload systemd
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "daemon-reload",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();

            // Remove binary
            if (File.Exists("/usr/local/bin/acproxycam"))
            {
                File.Delete("/usr/local/bin/acproxycam");
            }

            // Remove config if requested
            if (!keepConfig)
            {
                if (Directory.Exists("/etc/acproxycam"))
                {
                    Directory.Delete("/etc/acproxycam", true);
                }
            }

            // Remove log directory
            if (Directory.Exists("/var/log/acproxycam"))
            {
                Directory.Delete("/var/log/acproxycam", true);
            }

            // Remove user
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-c \"userdel acproxycam 2>/dev/null || true\"",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
        });

        _ui.WriteSuccess("ACProxyCam has been uninstalled.");
        return 0;
    }

    private async Task ChangeInterfacesAsync()
    {
        var interfaces = await SelectInterfacesAsync();
        if (interfaces == null)
            return;

        var (success, error) = await _ipcClient!.SetListenInterfacesAsync(interfaces);

        if (success)
        {
            _ui.WriteSuccess("Listening interfaces updated.");
            _ui.WriteWarning("Restart service for changes to take effect.");
        }
        else
        {
            _ui.WriteError($"Error: {error ?? "Unknown error"}");
        }

        await Task.Delay(2000);
    }

    private async Task AddPrinterAsync()
    {
        _ui.Clear();
        _ui.WriteRule("Add Printer");
        _ui.WriteLine();

        // Get printer name (validated)
        var name = AskValidatedString("Printer name (unique identifier):", ValidatePrinterName);
        if (name == null) return;

        // Get printer IP (validated)
        var ip = AskValidatedString("Printer IP/hostname:", ValidateIpAddress);
        if (ip == null) return;

        // Get MJPEG port (validated, with retry on conflict)
        var mjpegPort = await AskPortWithRetryAsync("MJPEG listening port:", 8080, name);
        if (mjpegPort == null) return;

        // SSH settings
        var sshPort = AskValidatedPort("SSH port:", 22);
        if (sshPort == null) return;

        var sshUser = AskValidatedString("SSH username:", ValidateUsername, "root");
        if (sshUser == null) return;

        var sshPassword = _ui.AskSecret("SSH password:", "rockchip");

        // MQTT settings
        var mqttPort = AskValidatedPort("MQTT port:", 9883);
        if (mqttPort == null) return;

        // Auto LAN Mode setting
        var autoLanMode = _ui.Confirm("Auto LAN Mode (enable LAN mode on printer if MQTT fails)?", true);

        // LED settings
        _ui.WriteLine();
        _ui.WriteInfo("LED settings:");
        var ledAutoControl = _ui.Confirm("LED Auto Control (automatically manage camera LED)?", false);
        var standbyLedTimeout = 20;
        if (ledAutoControl)
        {
            standbyLedTimeout = AskValidatedInt("Standby LED timeout (minutes):", 1, 1440, 20);
        }

        // Encoding settings (optional, show defaults)
        _ui.WriteLine();
        _ui.WriteInfo("Encoding settings (press Enter for defaults):");
        var maxFps = AskValidatedInt("Max FPS (0=unlimited):", 0, 120, 10);
        var idleFps = AskValidatedInt("Idle FPS (0=disabled):", 0, 30, 1);
        var jpegQuality = AskValidatedInt("JPEG quality:", 1, 100, 80);

        // Create printer config
        var config = new PrinterConfig
        {
            Name = name,
            Ip = ip,
            MjpegPort = mjpegPort.Value,
            SshPort = sshPort.Value,
            SshUser = sshUser,
            SshPassword = sshPassword,
            MqttPort = mqttPort.Value,
            AutoLanMode = autoLanMode,
            LedAutoControl = ledAutoControl,
            StandbyLedTimeoutMinutes = standbyLedTimeout,
            MaxFps = maxFps,
            IdleFps = idleFps,
            JpegQuality = jpegQuality
        };

        // Pre-flight check
        _ui.WriteLine();
        if (_ui.Confirm("Run pre-flight check to verify printer connectivity?", true))
        {
            var checkPassed = await RunPreflightCheckAsync(config);
            if (!checkPassed)
            {
                if (!_ui.Confirm("Pre-flight check failed. Add printer anyway?", false))
                {
                    return;
                }
            }
        }

        // Add printer
        var (success, error) = await _ipcClient!.AddPrinterAsync(config);

        if (success)
        {
            _ui.WriteSuccess("Printer added successfully!");
            _ui.WriteInfo($"MJPEG stream will be available at: http://<server>:{mjpegPort}/stream");
        }
        else
        {
            _ui.WriteError($"Error: {error ?? "Unknown error"}");
        }

        _ui.WaitForKey("Press any key to continue...");
    }

    private async Task DeletePrinterAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            _ui.WriteWarning("No printers to delete.");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => p.Name).ToList();

        var selected = _ui.SelectOneWithEscape("Select printer to delete:", choices);

        if (selected == null)
            return;

        if (!_ui.Confirm($"Delete printer '{selected}'?", false))
            return;

        var (deleteSuccess, error) = await _ipcClient.DeletePrinterAsync(selected);

        if (deleteSuccess)
        {
            _ui.WriteSuccess("Printer deleted.");
        }
        else
        {
            _ui.WriteError($"Error: {error ?? "Unknown error"}");
        }

        await Task.Delay(1500);
    }

    private async Task ModifyPrinterAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            _ui.WriteWarning("No printers to modify.");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => p.Name).ToList();

        var selected = _ui.SelectOneWithEscape("Select printer to modify:", choices);

        if (selected == null)
            return;

        // Get existing config
        var (configSuccess, existingConfig, _) = await _ipcClient.GetPrinterConfigAsync(selected);
        if (!configSuccess || existingConfig == null)
        {
            _ui.WriteError("Error: Could not get printer config.");
            await Task.Delay(1500);
            return;
        }

        var originalName = existingConfig.Name;
        var originalPort = existingConfig.MjpegPort;

        _ui.Clear();
        _ui.WriteRule($"Modify Printer: {selected}");
        _ui.WriteInfo("Press Enter to keep current value");
        _ui.WriteLine();

        // Allow name change with duplicate check
        var newName = await AskPrinterNameWithRetryAsync($"Printer name [{existingConfig.Name}]:", existingConfig.Name, originalName);
        if (newName == null) return;

        // Modify settings with validation
        var ip = AskValidatedString($"IP/hostname [{existingConfig.Ip}]:", ValidateIpAddress, existingConfig.Ip);
        if (ip == null) return;

        var mjpegPort = await AskPortWithRetryAsync($"MJPEG port [{existingConfig.MjpegPort}]:", existingConfig.MjpegPort, originalName, originalPort);
        if (mjpegPort == null) return;

        var sshPort = AskValidatedPort($"SSH port [{existingConfig.SshPort}]:", existingConfig.SshPort);
        if (sshPort == null) return;

        var sshUser = AskValidatedString($"SSH user [{existingConfig.SshUser}]:", ValidateUsername, existingConfig.SshUser);
        if (sshUser == null) return;

        var mqttPort = AskValidatedPort($"MQTT port [{existingConfig.MqttPort}]:", existingConfig.MqttPort);
        if (mqttPort == null) return;

        // Auto LAN Mode setting
        var autoLanMode = _ui.Confirm($"Auto LAN Mode [{(existingConfig.AutoLanMode ? "Yes" : "No")}]?", existingConfig.AutoLanMode);

        // LED settings
        _ui.WriteLine();
        _ui.WriteInfo("LED settings:");
        var ledAutoControl = _ui.Confirm($"LED Auto Control [{(existingConfig.LedAutoControl ? "Yes" : "No")}]?", existingConfig.LedAutoControl);
        var standbyLedTimeout = existingConfig.StandbyLedTimeoutMinutes;
        if (ledAutoControl)
        {
            standbyLedTimeout = AskValidatedInt($"Standby LED timeout (minutes) [{existingConfig.StandbyLedTimeoutMinutes}]:", 1, 1440, existingConfig.StandbyLedTimeoutMinutes);
        }

        // Encoding settings
        _ui.WriteLine();
        _ui.WriteInfo("Encoding settings:");
        var maxFps = AskValidatedInt($"Max FPS [{existingConfig.MaxFps}]:", 0, 120, existingConfig.MaxFps);
        var idleFps = AskValidatedInt($"Idle FPS [{existingConfig.IdleFps}]:", 0, 30, existingConfig.IdleFps);
        var jpegQuality = AskValidatedInt($"JPEG quality [{existingConfig.JpegQuality}]:", 1, 100, existingConfig.JpegQuality);

        // Update config
        existingConfig.Name = newName;
        existingConfig.Ip = ip;
        existingConfig.MjpegPort = mjpegPort.Value;
        existingConfig.SshPort = sshPort.Value;
        existingConfig.SshUser = sshUser;
        existingConfig.MqttPort = mqttPort.Value;
        existingConfig.AutoLanMode = autoLanMode;
        existingConfig.LedAutoControl = ledAutoControl;
        existingConfig.StandbyLedTimeoutMinutes = standbyLedTimeout;
        existingConfig.MaxFps = maxFps;
        existingConfig.IdleFps = idleFps;
        existingConfig.JpegQuality = jpegQuality;

        var (modifySuccess, error) = await _ipcClient.ModifyPrinterAsync(originalName, existingConfig);

        if (modifySuccess)
        {
            _ui.WriteSuccess("Printer modified.");
        }
        else
        {
            _ui.WriteError($"Error: {error ?? "Unknown error"}");
        }

        _ui.WaitForKey("Press any key to continue...");
    }

    private async Task TogglePrinterPauseAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            _ui.WriteWarning("No printers available.");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => $"{p.Name} ({(p.IsPaused ? "Paused" : "Running")})").ToList();

        var selected = _ui.SelectOneWithEscape("Select printer to pause/resume:", choices);

        if (selected == null)
            return;

        // Extract printer name
        var printerName = selected.Split(' ')[0];
        var printer = printers.First(p => p.Name == printerName);

        bool toggleSuccess;
        string? error;

        if (printer.IsPaused)
        {
            (toggleSuccess, error) = await _ipcClient.ResumePrinterAsync(printerName);
        }
        else
        {
            (toggleSuccess, error) = await _ipcClient.PausePrinterAsync(printerName);
        }

        if (toggleSuccess)
        {
            _ui.WriteSuccess($"Printer {(printer.IsPaused ? "resumed" : "paused")}.");
        }
        else
        {
            _ui.WriteError($"Error: {error ?? "Unknown error"}");
        }

        await Task.Delay(1000);
    }

    private async Task TogglePrinterLedAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            _ui.WriteWarning("No printers available.");
            await Task.Delay(1500);
            return;
        }

        // Build choices showing current LED status
        var choices = printers.Select(p =>
        {
            var ledStatus = p.CameraLed == null ? "?" : (p.CameraLed.IsOn ? "On" : "Off");
            return $"{p.Name} (LED: {ledStatus})";
        }).ToList();

        var selected = _ui.SelectOneWithEscape("Select printer to toggle LED:", choices);

        if (selected == null)
            return;

        // Extract printer name
        var printerName = selected.Split(' ')[0];
        var printer = printers.First(p => p.Name == printerName);

        // Toggle LED: if currently on or unknown, turn off; if off, turn on
        var turnOn = printer.CameraLed == null || !printer.CameraLed.IsOn;

        var (toggleSuccess, error) = await _ipcClient.SetLedAsync(printerName, turnOn);

        if (toggleSuccess)
        {
            _ui.WriteSuccess($"LED turned {(turnOn ? "on" : "off")}.");
        }
        else
        {
            _ui.WriteError($"Error: {error ?? "Unknown error"}");
        }

        await Task.Delay(1000);
    }

    private async Task ShowPrinterDetailsAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            _ui.WriteWarning("No printers available.");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => p.Name).ToList();

        var (selected, selectedIndex) = _ui.SelectOneWithEscapeAndIndex("Select printer to view:", choices, 0);

        if (selected == null)
            return;

        int currentIndex = selectedIndex;

        // Cycling through printers with Up/Down arrows
        while (true)
        {
            await RenderPrinterDetailsAsync(printers[currentIndex].Name, currentIndex, printers.Count);

            var key = Console.ReadKey(true).Key;

            switch (key)
            {
                case ConsoleKey.DownArrow:
                case ConsoleKey.RightArrow:
                    // Next printer, wrap to first if at end
                    currentIndex = (currentIndex + 1) % printers.Count;
                    break;

                case ConsoleKey.UpArrow:
                case ConsoleKey.LeftArrow:
                    // Previous printer, wrap to last if at start
                    currentIndex = currentIndex > 0 ? currentIndex - 1 : printers.Count - 1;
                    break;

                default:
                    // Any other key exits
                    return;
            }
        }
    }

    private async Task RenderPrinterDetailsAsync(string printerName, int currentIndex, int totalCount)
    {
        _ui.Clear();

        // Get detailed status and config
        var (statusSuccess, detailedStatus, _) = await _ipcClient!.GetPrinterStatusAsync(printerName);
        if (!statusSuccess || detailedStatus == null)
        {
            _ui.WriteError($"Failed to get status for {printerName}");
            return;
        }

        var (configSuccess, printerConfig, _) = await _ipcClient.GetPrinterConfigAsync(printerName);

        // Status indicators
        var (statusColor, statusIcon) = detailedStatus.State switch
        {
            PrinterState.Running => ("green", "●"),
            PrinterState.Paused => ("yellow", "◐"),
            PrinterState.Failed => ("red", "✗"),
            PrinterState.Retrying => ("orange3", "↻"),
            PrinterState.Connecting => ("blue", "◌"),
            _ => ("grey", "○")
        };

        var resolution = detailedStatus.StreamStatus.Width > 0
            ? $"{detailedStatus.StreamStatus.Width}x{detailedStatus.StreamStatus.Height}"
            : "-";

        // === HEADER ===
        var deviceTypeDisplay = string.IsNullOrEmpty(detailedStatus.DeviceType)
            ? ""
            : $" [cyan]({detailedStatus.DeviceType})[/]";

        var positionIndicator = totalCount > 1 ? $" [grey]({currentIndex + 1}/{totalCount})[/]" : "";

        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Blue)
            .AddColumn(new TableColumn($"[bold]{detailedStatus.Name}[/]{deviceTypeDisplay}{positionIndicator}").Centered());

        headerTable.AddRow($"[{statusColor}]{statusIcon} {detailedStatus.State}[/]");
        headerTable.AddRow($"[grey]{detailedStatus.Ip}:{detailedStatus.MjpegPort}[/] │ [cyan]{detailedStatus.ConnectedClients}[/] clients │ [white]{resolution}[/]");

        AnsiConsole.Write(headerTable);

        // === MAIN DETAILS TABLE (4 columns) ===
        var mainTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Connection[/]").Width(20))
            .AddColumn(new TableColumn("[bold]Stream[/]").Width(20))
            .AddColumn(new TableColumn("[bold]Encoding[/]").Width(18))
            .AddColumn(new TableColumn("[bold]LED[/]").Width(14));

        // Connection column
        var sshStatus = detailedStatus.SshStatus.Connected ? "[green]●[/] OK" : "[grey]○[/] -";
        var mqttStatus = detailedStatus.MqttStatus.Connected
            ? $"[green]●[/] {detailedStatus.MqttStatus.DetectedModelCode ?? "OK"}"
            : "[grey]○[/] -";
        var camStatus = detailedStatus.MqttStatus.CameraStarted ? "[green]●[/] Started" : "[grey]○[/] -";
        var lanMode = configSuccess && printerConfig != null && printerConfig.AutoLanMode ? "[green]Auto[/]" : "[grey]Manual[/]";

        // Stream column
        var streamStatus = detailedStatus.StreamStatus.Connected ? "[green]●[/] Streaming" : "[grey]○[/] Stopped";
        var framesDecoded = detailedStatus.StreamStatus.FramesDecoded.ToString("N0");

        // Encoding column (from config)
        var maxFps = configSuccess && printerConfig != null ? printerConfig.MaxFps.ToString() : "-";
        var idleFps = configSuccess && printerConfig != null ? printerConfig.IdleFps.ToString() : "-";
        var quality = configSuccess && printerConfig != null ? printerConfig.JpegQuality.ToString() : "-";
        var stopCmd = configSuccess && printerConfig != null && printerConfig.SendStopCommand ? "[green]Yes[/]" : "[grey]No[/]";

        // LED column
        var ledStatus = detailedStatus.CameraLed == null
            ? "[grey]?[/]"
            : detailedStatus.CameraLed.IsOn
                ? $"[yellow]● On[/] ({detailedStatus.CameraLed.Brightness}%)"
                : "[grey]○ Off[/]";
        var ledAuto = configSuccess && printerConfig != null && printerConfig.LedAutoControl ? "[green]✓[/]" : "[grey]✗[/]";
        var ledTimeout = configSuccess && printerConfig != null ? $"{printerConfig.StandbyLedTimeoutMinutes}m" : "-";

        // Build rows
        mainTable.AddRow($"SSH  {sshStatus}", streamStatus, $"Max FPS: {maxFps}", ledStatus);
        mainTable.AddRow($"MQTT {mqttStatus}", $"Frames: {framesDecoded}", $"Idle: {idleFps}", $"Auto: {ledAuto}");
        mainTable.AddRow($"Cam  {camStatus}", "", $"Quality: {quality}", $"Timeout: {ledTimeout}");
        mainTable.AddRow($"LAN  {lanMode}", "", $"Stop: {stopCmd}", "");

        AnsiConsole.Write(mainTable);

        // === URLs ===
        var urlTable = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn($"[bold]URLs[/]  [grey](http://<server>:{detailedStatus.MjpegPort}/)[/]").LeftAligned());

        urlTable.AddRow("[cyan]/stream[/]  [grey]│[/]  [cyan]/snapshot[/]  [grey]│[/]  [cyan]/status[/]  [grey]│[/]  [cyan]/led[/]  [cyan]/led/on[/]  [cyan]/led/off[/]");

        AnsiConsole.Write(urlTable);

        // === ERROR/RETRY INFO (only if relevant) ===
        if (detailedStatus.LastError != null || detailedStatus.State == PrinterState.Retrying)
        {
            AnsiConsole.WriteLine();
            if (detailedStatus.LastError != null)
            {
                var errorTime = detailedStatus.LastErrorAt.HasValue
                    ? $" [grey]({detailedStatus.LastErrorAt.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss})[/]"
                    : "";
                AnsiConsole.MarkupLine($"[red]Error:{errorTime}[/] [grey]{Markup.Escape(detailedStatus.LastError)}[/]");
            }

            // Show last online and next retry info
            var statusParts = new List<string>();
            if (detailedStatus.LastSeenOnline.HasValue)
            {
                statusParts.Add($"Last online: {detailedStatus.LastSeenOnline.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
            if (detailedStatus.NextRetryAt.HasValue && detailedStatus.State == PrinterState.Retrying)
            {
                statusParts.Add($"Next retry: [yellow]{detailedStatus.NextRetryAt.Value.ToLocalTime():HH:mm:ss}[/]");
            }
            if (statusParts.Count > 0)
            {
                AnsiConsole.MarkupLine($"[grey]{string.Join(" │ ", statusParts)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        if (totalCount > 1)
        {
            AnsiConsole.MarkupLine("[grey]↑/↓ Previous/Next printer  |  Any other key to return...[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[grey]Press any key to return...[/]");
        }
    }

    private async Task ShowBedMeshMenuFromMainAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            _ui.WriteWarning("No printers configured.");
            await Task.Delay(1500);
            return;
        }

        await ShowBedMeshMenuAsync(printers);
    }

    private async Task ShowBedMeshMenuAsync(List<PrinterStatus> printers)
    {
        int lastSelectedIndex = 0;
        var menuChoices = new[] { "Calibrate", "Analyse", "Sessions" };

        while (true)
        {
            _ui.Clear();
            _ui.WriteRule("BedMesh");
            _ui.WriteLine();

            var (choice, selectedIndex) = _ui.SelectOneWithEscapeAndIndex("Select action:", menuChoices, lastSelectedIndex);

            if (choice == null)
                return;

            lastSelectedIndex = selectedIndex;

            switch (choice)
            {
                case "Calibrate":
                    await RunCalibrationWizardAsync(printers);
                    break;

                case "Analyse":
                    await RunAnalysisWizardAsync(printers);
                    break;

                case "Sessions":
                    await ShowSessionsMenuAsync();
                    break;
            }
        }
    }

    private async Task RunCalibrationWizardAsync(List<PrinterStatus> printers)
    {
        _ui.Clear();
        _ui.WriteRule("BedMesh Calibration");
        _ui.WriteLine();

        // Select printer
        var choices = printers.Select(p =>
        {
            var status = p.State == PrinterState.Running ? "[green]●[/]" : "[grey]○[/]";
            return $"{status} {p.Name}";
        }).ToList();

        var selected = _ui.SelectOneWithEscape("Select printer:", choices);

        if (selected == null)
            return;

        // Extract printer name (remove status prefix)
        var printerName = selected.Substring(selected.IndexOf(' ') + 1);
        var printerStatus = printers.First(p => p.Name == printerName);

        _ui.Clear();
        _ui.WriteRule($"BedMesh Calibration - {printerName}");
        _ui.WriteLine();

        // Check if printer is connected
        if (printerStatus.State != PrinterState.Running)
        {
            _ui.WriteError("Printer must be connected and running to start calibration.");
            _ui.WaitForKey("Press any key to continue...");
            return;
        }

        // Check if calibration already running
        var (sessionsSuccess, sessions, _) = await _ipcClient!.GetBedMeshSessionsAsync();
        if (sessionsSuccess && sessions != null)
        {
            var (configSuccess, config, _) = await _ipcClient.GetPrinterConfigAsync(printerName);
            if (configSuccess && config != null && !string.IsNullOrEmpty(config.DeviceId))
            {
                var existingSession = sessions.ActiveSessions.FirstOrDefault(s => s.DeviceId == config.DeviceId);
                if (existingSession != null)
                {
                    _ui.WriteWarning($"Calibration already running for this printer (started {existingSession.StartedUtc.ToLocalTime():HH:mm:ss})");
                    _ui.WaitForKey("Press any key to continue...");
                    return;
                }
            }
        }

        // Heat soak selection
        _ui.WriteInfo("Heat soak warms the bed before calibration for more accurate results.");
        _ui.WriteLine();

        var heatSoakChoice = _ui.SelectOneWithEscape("Select heat soak duration:", new[]
        {
            "None (0 minutes)",
            "15 minutes",
            "30 minutes",
            "60 minutes",
            "90 minutes",
            "120 minutes",
            "Custom"
        });

        if (heatSoakChoice == null)
            return;

        int heatSoakMinutes;
        if (heatSoakChoice == "Custom")
        {
            var customMinutes = _ui.Ask("Enter heat soak duration in minutes:", "0");
            if (!int.TryParse(customMinutes, out heatSoakMinutes) || heatSoakMinutes < 0)
            {
                _ui.WriteError("Invalid value. Must be a positive integer.");
                _ui.WaitForKey("Press any key to continue...");
                return;
            }
        }
        else
        {
            heatSoakMinutes = heatSoakChoice switch
            {
                "15 minutes" => 15,
                "30 minutes" => 30,
                "60 minutes" => 60,
                "90 minutes" => 90,
                "120 minutes" => 120,
                _ => 0
            };
        }

        // Session name (optional)
        _ui.WriteLine();
        _ui.WriteInfo("Optionally enter a name for this calibration (e.g., build plate identifier).");
        var sessionName = _ui.Ask("Name (leave empty to skip):", "");
        sessionName = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName.Trim();

        // Confirmation
        _ui.WriteLine();
        _ui.WriteInfo($"Calibration will:");
        _ui.WriteLine($"  1. Turn camera LED on");
        if (heatSoakMinutes > 0)
        {
            _ui.WriteLine($"  2. Heat bed to 60°C and wait {heatSoakMinutes} minutes");
            _ui.WriteLine($"  3. Run preheating, wiping, and probing sequences");
        }
        else
        {
            _ui.WriteLine($"  2. Run preheating, wiping, and probing sequences");
        }
        _ui.WriteLine();

        if (!_ui.Confirm("Start calibration?", true))
            return;

        // Start calibration
        _ui.WriteInfo("Starting calibration...");
        var (success, error) = await _ipcClient.StartCalibrationAsync(printerName, heatSoakMinutes, sessionName);

        if (success)
        {
            _ui.WriteSuccess("Calibration started!");
            _ui.WriteInfo("You can monitor progress in the Sessions menu.");
        }
        else
        {
            _ui.WriteError($"Failed to start calibration: {error ?? "Unknown error"}");
        }

        _ui.WaitForKey("Press any key to continue...");
    }

    private async Task RunAnalysisWizardAsync(List<PrinterStatus> printers)
    {
        _ui.Clear();
        _ui.WriteRule("BedMesh Analysis");
        _ui.WriteLine();

        _ui.WriteInfo("Analysis runs multiple calibrations to detect outliers and compute statistics.");
        _ui.WriteInfo("Recommended: Run at least 10 calibrations for accurate results.");
        _ui.WriteLine();

        // Select printer
        var choices = printers.Select(p =>
        {
            var status = p.State == PrinterState.Running ? "[green]●[/]" : "[grey]○[/]";
            return $"{status} {p.Name}";
        }).ToList();

        var selected = _ui.SelectOneWithEscape("Select printer:", choices);

        if (selected == null)
            return;

        // Extract printer name (remove status prefix)
        var printerName = selected.Substring(selected.IndexOf(' ') + 1);
        var printerStatus = printers.First(p => p.Name == printerName);

        _ui.Clear();
        _ui.WriteRule($"BedMesh Analysis - {printerName}");
        _ui.WriteLine();

        // Check if printer is connected
        if (printerStatus.State != PrinterState.Running)
        {
            _ui.WriteError("Printer must be connected and running to start analysis.");
            _ui.WaitForKey("Press any key to continue...");
            return;
        }

        // Check if calibration/analysis already running
        var (sessionsSuccess, sessions, _) = await _ipcClient!.GetBedMeshSessionsAsync();
        if (sessionsSuccess && sessions != null)
        {
            var (configSuccess, config, _) = await _ipcClient.GetPrinterConfigAsync(printerName);
            if (configSuccess && config != null && !string.IsNullOrEmpty(config.DeviceId))
            {
                var existingSession = sessions.ActiveSessions.FirstOrDefault(s => s.DeviceId == config.DeviceId);
                if (existingSession != null)
                {
                    var sessionType = existingSession.IsAnalysis ? "Analysis" : "Calibration";
                    _ui.WriteWarning($"{sessionType} already running for this printer (started {existingSession.StartedUtc.ToLocalTime():HH:mm:ss})");
                    _ui.WaitForKey("Press any key to continue...");
                    return;
                }
            }
        }

        // Calibration count selection
        _ui.WriteInfo("How many calibrations to run?");
        _ui.WriteInfo("Minimum: 5 (for IQR accuracy), Recommended: 10 or more");
        _ui.WriteLine();

        var countChoice = _ui.SelectOneWithEscape("Select calibration count:", new[]
        {
            "5 calibrations (minimum)",
            "10 calibrations (recommended)",
            "15 calibrations",
            "20 calibrations",
            "Custom"
        });

        if (countChoice == null)
            return;

        int calibrationCount;
        if (countChoice == "Custom")
        {
            var customCount = _ui.Ask("Enter number of calibrations (minimum 5):", "10");
            if (!int.TryParse(customCount, out calibrationCount) || calibrationCount < 5)
            {
                _ui.WriteError("Invalid value. Must be at least 5 calibrations for IQR accuracy.");
                _ui.WaitForKey("Press any key to continue...");
                return;
            }
        }
        else
        {
            calibrationCount = countChoice switch
            {
                "5 calibrations (minimum)" => 5,
                "10 calibrations (recommended)" => 10,
                "15 calibrations" => 15,
                "20 calibrations" => 20,
                _ => 10
            };
        }

        // Heat soak selection (only before first calibration)
        _ui.WriteLine();
        _ui.WriteInfo("Heat soak warms the bed before the FIRST calibration.");
        _ui.WriteInfo("Subsequent calibrations run back-to-back with 1 minute pauses.");
        _ui.WriteLine();

        var heatSoakChoice = _ui.SelectOneWithEscape("Select heat soak duration:", new[]
        {
            "None (0 minutes)",
            "15 minutes",
            "30 minutes",
            "60 minutes",
            "90 minutes",
            "120 minutes",
            "Custom"
        });

        if (heatSoakChoice == null)
            return;

        int heatSoakMinutes;
        if (heatSoakChoice == "Custom")
        {
            var customMinutes = _ui.Ask("Enter heat soak duration in minutes:", "0");
            if (!int.TryParse(customMinutes, out heatSoakMinutes) || heatSoakMinutes < 0)
            {
                _ui.WriteError("Invalid value. Must be a positive integer.");
                _ui.WaitForKey("Press any key to continue...");
                return;
            }
        }
        else
        {
            heatSoakMinutes = heatSoakChoice switch
            {
                "15 minutes" => 15,
                "30 minutes" => 30,
                "60 minutes" => 60,
                "90 minutes" => 90,
                "120 minutes" => 120,
                _ => 0
            };
        }

        // Session name (optional)
        _ui.WriteLine();
        _ui.WriteInfo("Optionally enter a name for this analysis (e.g., build plate identifier).");
        var sessionName = _ui.Ask("Name (leave empty to skip):", "");
        sessionName = string.IsNullOrWhiteSpace(sessionName) ? null : sessionName.Trim();

        // Confirmation
        _ui.WriteLine();
        _ui.WriteInfo($"Analysis will:");
        _ui.WriteLine($"  1. Turn camera LED on");
        if (heatSoakMinutes > 0)
        {
            _ui.WriteLine($"  2. Heat bed to 60°C and wait {heatSoakMinutes} minutes");
            _ui.WriteLine($"  3. Run {calibrationCount} calibrations with 1 minute pauses between");
        }
        else
        {
            _ui.WriteLine($"  2. Run {calibrationCount} calibrations with 1 minute pauses between");
        }
        _ui.WriteLine($"  4. Calculate statistics and detect outliers using IQR method");
        _ui.WriteLine();

        if (!_ui.Confirm("Start analysis?", true))
            return;

        // Start analysis
        _ui.WriteInfo("Starting analysis...");
        var (success, error) = await _ipcClient.StartAnalysisAsync(printerName, heatSoakMinutes, calibrationCount, sessionName);

        if (success)
        {
            _ui.WriteSuccess("Analysis started!");
            _ui.WriteInfo("You can monitor progress in the Sessions menu.");
        }
        else
        {
            _ui.WriteError($"Failed to start analysis: {error ?? "Unknown error"}");
        }

        _ui.WaitForKey("Press any key to continue...");
    }

    private async Task ShowSessionsMenuAsync()
    {
        int lastSelectedIndex = 0;

        while (true)
        {
            _ui.Clear();
            _ui.WriteRule("BedMesh Sessions");
            _ui.WriteLine();

            var (success, sessions, _) = await _ipcClient!.GetBedMeshSessionsAsync();
            if (!success || sessions == null)
            {
                _ui.WriteError("Failed to get sessions");
                _ui.WaitForKey("Press any key to continue...");
                return;
            }

            // Show all sessions (not filtered by printer)
            var activeSessions = sessions.ActiveSessions;
            var calibrations = sessions.Calibrations;

            var choices = new List<string>
            {
                $"Active ({activeSessions.Count})",
                $"Saved calibrations ({calibrations.Count})",
                $"Saved analyses ({sessions.AnalysisCount})"
            };

            var (choice, selectedIndex) = _ui.SelectOneWithEscapeAndIndex("Select category:", choices, lastSelectedIndex);

            if (choice == null)
                return;

            lastSelectedIndex = selectedIndex;

            if (choice.StartsWith("Active"))
            {
                await ShowActiveSessionsAsync(activeSessions);
            }
            else if (choice.StartsWith("Saved calibrations"))
            {
                await ShowSavedCalibrationsAsync(calibrations);
            }
            else if (choice.StartsWith("Saved analyses"))
            {
                await ShowSavedAnalysesAsync(sessions.Analyses);
            }
        }
    }

    private async Task ShowActiveSessionsAsync(List<BedMeshSession> activeSessions)
    {
        if (activeSessions.Count == 0)
        {
            _ui.WriteInfo("No active sessions");
            _ui.WaitForKey("Press any key to continue...");
            return;
        }

        // Auto-refresh display
        var refreshInterval = TimeSpan.FromSeconds(5);
        var lastRefresh = DateTime.MinValue;
        var completedNotifications = new List<string>();
        var previousSessionIds = activeSessions.Select(s => s.DeviceId).ToHashSet();

        while (!Console.KeyAvailable)
        {
            if ((DateTime.UtcNow - lastRefresh) >= refreshInterval)
            {
                // Refresh data
                var (success, sessions, _) = await _ipcClient!.GetBedMeshSessionsAsync();
                if (success && sessions != null)
                {
                    // Check for completed sessions (were active before, not active now)
                    var currentIds = sessions.ActiveSessions.Select(s => s.DeviceId).ToHashSet();
                    foreach (var prevId in previousSessionIds)
                    {
                        if (!currentIds.Contains(prevId))
                        {
                            // Session completed - check if it's in saved calibrations
                            var completed = sessions.Calibrations
                                .FirstOrDefault(c => c.DeviceId == prevId);
                            if (completed != null)
                            {
                                var statusText = completed.Status == CalibrationStatus.Success
                                    ? "[green]SUCCESS[/]"
                                    : "[red]FAILED[/]";
                                var range = completed.MeshRange.HasValue
                                    ? $" | Range: {MeshStats.FormatMm(completed.MeshRange.Value)}"
                                    : "";
                                completedNotifications.Insert(0,
                                    $"{statusText} {completed.PrinterName} at {completed.Timestamp.ToLocalTime():HH:mm:ss}{range}");
                            }
                            else
                            {
                                // Session disappeared but not in saved - likely failed
                                var prevSession = activeSessions.FirstOrDefault(s => s.DeviceId == prevId);
                                if (prevSession != null)
                                {
                                    completedNotifications.Insert(0,
                                        $"[red]FAILED[/] {prevSession.PrinterName} - Session ended unexpectedly");
                                }
                            }
                        }
                    }

                    previousSessionIds = currentIds;
                    activeSessions = sessions.ActiveSessions;
                }

                // Clear screen and redraw everything
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine("[bold blue]───────────────────── Active Sessions ─────────────────────[/]");
                AnsiConsole.MarkupLine("[grey]Auto-refreshes every 5 seconds. Press any key to go back.[/]");
                AnsiConsole.WriteLine();

                // Show completion notifications at the top
                if (completedNotifications.Count > 0)
                {
                    AnsiConsole.MarkupLine("[bold]Recent completions:[/]");
                    foreach (var notification in completedNotifications.Take(5))
                    {
                        AnsiConsole.MarkupLine($"  {notification}");
                    }
                    AnsiConsole.WriteLine();
                }

                // Show active sessions
                if (activeSessions.Count > 0)
                {
                    foreach (var session in activeSessions)
                    {
                        var elapsed = session.DurationFormatted;
                        var step = session.CurrentStep ?? "Calibrating...";
                        var heatSoak = session.HeatSoakMinutes > 0 ? $" | Heat soak: {session.HeatSoakMinutes}min" : "";
                        var nameDisplay = !string.IsNullOrEmpty(session.Name) ? $" ({Markup.Escape(session.Name)})" : "";
                        var typeLabel = session.IsAnalysis ? "[cyan]Analysis[/] " : "";

                        AnsiConsole.MarkupLine($"  {typeLabel}[white]{Markup.Escape(session.PrinterName)}{nameDisplay}[/] - [yellow]{Markup.Escape(step)}[/]");
                        AnsiConsole.MarkupLine($"    [grey]Started: {session.StartedUtc.ToLocalTime():HH:mm:ss} | Elapsed: {elapsed}{heatSoak}[/]");
                        AnsiConsole.WriteLine();
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]No active sessions[/]");
                }

                lastRefresh = DateTime.UtcNow;
            }

            await Task.Delay(100);
        }

        Console.ReadKey(true); // Consume the key
    }

    private async Task ShowSavedCalibrationsAsync(List<BedMeshSessionInfo> calibrations)
    {
        int lastSelectedIndex = 0;
        int previousCount = calibrations.Count;

        while (true)
        {
            // Refresh calibrations list each iteration (in case of deletion)
            var (refreshSuccess, sessions, _) = await _ipcClient!.GetBedMeshSessionsAsync();
            if (refreshSuccess && sessions != null)
            {
                calibrations = sessions.Calibrations;
            }

            if (calibrations.Count == 0)
            {
                _ui.WriteInfo("No saved calibrations");
                _ui.WaitForKey("Press any key to continue...");
                return;
            }

            // Adjust position if items were deleted
            if (calibrations.Count < previousCount)
            {
                // Move cursor up proportionally if items were removed
                lastSelectedIndex = Math.Min(lastSelectedIndex, calibrations.Count - 1);
            }
            previousCount = calibrations.Count;

            _ui.Clear();
            _ui.WriteRule("Saved Calibrations");
            _ui.WriteLine();

            // Build choices from calibrations
            var choices = calibrations.Select(c =>
            {
                var status = c.Status == CalibrationStatus.Success ? "[green]✓[/]" : "[red]✗[/]";
                var range = c.MeshRange.HasValue ? $"Range: {MeshStats.FormatMm(c.MeshRange.Value)}" : "";
                // Show Name if set, otherwise show DeviceType
                var description = !string.IsNullOrEmpty(c.Name) ? c.Name : c.DeviceType ?? "";
                var descDisplay = !string.IsNullOrEmpty(description) ? $" | {description}" : "";
                return $"{status} {c.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} | {c.PrinterName}{descDisplay} | {range}";
            }).ToList();

            var (selected, selectedIndex) = _ui.SelectOneWithEscapeAndIndex("Select calibration to view:", choices, lastSelectedIndex);

            if (selected == null)
                return;

            lastSelectedIndex = selectedIndex;

            if (selectedIndex >= 0 && selectedIndex < calibrations.Count)
            {
                await ShowCalibrationDetailsAsync(calibrations[selectedIndex]);
            }
        }
    }

    private async Task ShowCalibrationDetailsAsync(BedMeshSessionInfo info)
    {
        var (success, session, _) = await _ipcClient!.GetCalibrationAsync(info.FileName);
        if (!success || session == null)
        {
            _ui.WriteError("Failed to load calibration details");
            _ui.WaitForKey("Press any key to continue...");
            return;
        }

        _ui.Clear();
        var titleSuffix = !string.IsNullOrEmpty(session.Name) ? $" ({session.Name})" : "";
        _ui.WriteRule($"Calibration Result - {session.PrinterName}{titleSuffix}");
        _ui.WriteLine();

        // Use the visual mesh renderer if mesh data is available
        if (session.MeshData != null && session.MeshData.Points.Length > 0)
        {
            MeshVisualizer.RenderCalibrationResult(session);
        }
        else
        {
            // Fallback to text-only display
            var statusDisplay = session.Status == CalibrationStatus.Success
                ? "[green]SUCCESS[/]"
                : "[red]FAILED[/]";
            AnsiConsole.MarkupLine($"  Status:    {statusDisplay}");
            if (!string.IsNullOrEmpty(session.Name))
                AnsiConsole.MarkupLine($"  Name:      [grey]{Markup.Escape(session.Name)}[/]");
            AnsiConsole.MarkupLine($"  Started:   [grey]{session.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
            if (session.FinishedUtc.HasValue)
                AnsiConsole.MarkupLine($"  Finished:  [grey]{session.FinishedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
            AnsiConsole.MarkupLine($"  Duration:  [grey]{session.DurationFormatted}[/]");
            if (session.HeatSoakMinutes > 0)
                AnsiConsole.MarkupLine($"  Heat Soak: [grey]{session.HeatSoakMinutes} min[/]");

            if (!string.IsNullOrEmpty(session.ErrorMessage))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"  [red]Error: {Markup.Escape(session.ErrorMessage)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey][[D]][/][grey]elete  |  Press any other key to go back...[/]");

        var key = Console.ReadKey(true).Key;
        if (key == ConsoleKey.D)
        {
            if (_ui.Confirm("Delete this calibration?", false))
            {
                var (deleteSuccess, error) = await _ipcClient.DeleteCalibrationAsync(info.FileName);
                if (deleteSuccess)
                {
                    _ui.WriteSuccess("Calibration deleted.");
                }
                else
                {
                    _ui.WriteError($"Failed to delete: {error ?? "Unknown error"}");
                }
                await Task.Delay(1500);
            }
        }
    }

    private async Task ShowSavedAnalysesAsync(List<BedMeshSessionInfo> analyses)
    {
        int lastSelectedIndex = 0;
        int previousCount = analyses.Count;

        while (true)
        {
            // Refresh analyses list each iteration (in case of deletion)
            var (refreshSuccess, sessions, _) = await _ipcClient!.GetBedMeshSessionsAsync();
            if (refreshSuccess && sessions != null)
            {
                analyses = sessions.Analyses;
            }

            if (analyses.Count == 0)
            {
                _ui.WriteInfo("No saved analyses");
                _ui.WaitForKey("Press any key to continue...");
                return;
            }

            // Adjust position if items were deleted
            if (analyses.Count < previousCount)
            {
                // Move cursor up proportionally if items were removed
                lastSelectedIndex = Math.Min(lastSelectedIndex, analyses.Count - 1);
            }
            previousCount = analyses.Count;

            _ui.Clear();
            _ui.WriteRule("Saved Analyses");
            _ui.WriteLine();

            // Build choices from analyses
            var choices = analyses.Select(a =>
            {
                var status = a.Status == CalibrationStatus.Success ? "[green]✓[/]" : "[red]✗[/]";
                var countStr = a.CalibrationCount.HasValue ? $"{a.CalibrationCount}x" : "";
                var range = a.MeshRange.HasValue ? $"Avg Range: {MeshStats.FormatMm(a.MeshRange.Value)}" : "";
                // Show Name if set, otherwise show DeviceType
                var description = !string.IsNullOrEmpty(a.Name) ? a.Name : a.DeviceType ?? "";
                var descDisplay = !string.IsNullOrEmpty(description) ? $" | {description}" : "";
                return $"{status} {a.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} | {a.PrinterName}{descDisplay} | {countStr} | {range}";
            }).ToList();

            var (selected, selectedIndex) = _ui.SelectOneWithEscapeAndIndex("Select analysis to view:", choices, lastSelectedIndex);

            if (selected == null)
                return;

            lastSelectedIndex = selectedIndex;

            if (selectedIndex >= 0 && selectedIndex < analyses.Count)
            {
                await ShowAnalysisDetailsAsync(analyses[selectedIndex]);
            }
        }
    }

    private async Task ShowAnalysisDetailsAsync(BedMeshSessionInfo info)
    {
        var (success, analysis, _) = await _ipcClient!.GetAnalysisAsync(info.FileName);
        if (!success || analysis == null)
        {
            _ui.WriteError("Failed to load analysis details");
            _ui.WaitForKey("Press any key to continue...");
            return;
        }

        while (true)
        {
            _ui.Clear();
            var titleSuffix = !string.IsNullOrEmpty(analysis.Name) ? $" ({analysis.Name})" : "";
            _ui.WriteRule($"Analysis Result - {analysis.PrinterName}{titleSuffix}");
            _ui.WriteLine();

            // Use the visual analysis renderer if average mesh data is available
            if (analysis.AverageMesh != null && analysis.AverageMesh.Points.Length > 0)
            {
                MeshVisualizer.RenderAnalysisResult(analysis);
            }
            else
            {
                // Fallback to text-only display
                var statusDisplay = analysis.Status == CalibrationStatus.Success
                    ? "[green]SUCCESS[/]"
                    : "[red]FAILED[/]";
                AnsiConsole.MarkupLine($"  Status:         {statusDisplay}");
                if (!string.IsNullOrEmpty(analysis.Name))
                    AnsiConsole.MarkupLine($"  Name:           [grey]{Markup.Escape(analysis.Name)}[/]");
                AnsiConsole.MarkupLine($"  Calibrations:   [grey]{analysis.CalibrationCount}[/]");
                AnsiConsole.MarkupLine($"  Started:        [grey]{analysis.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
                if (analysis.FinishedUtc.HasValue)
                    AnsiConsole.MarkupLine($"  Finished:       [grey]{analysis.FinishedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}[/]");
                AnsiConsole.MarkupLine($"  Duration:       [grey]{analysis.DurationFormatted}[/]");
                if (analysis.HeatSoakMinutes > 0)
                    AnsiConsole.MarkupLine($"  Heat Soak:      [grey]{analysis.HeatSoakMinutes} min[/]");

                if (analysis.AnalysisStats != null)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"  [bold]Statistics:[/]");
                    AnsiConsole.MarkupLine($"    Average Range: [grey]{MeshStats.FormatMm(analysis.AnalysisStats.AverageRange)}[/]");
                    AnsiConsole.MarkupLine($"    Min Range:     [grey]{MeshStats.FormatMm(analysis.AnalysisStats.MinRange)}[/]");
                    AnsiConsole.MarkupLine($"    Max Range:     [grey]{MeshStats.FormatMm(analysis.AnalysisStats.MaxRange)}[/]");
                    AnsiConsole.MarkupLine($"    Total Outliers:[grey]{analysis.AnalysisStats.TotalOutlierCount}[/]");
                }

                if (!string.IsNullOrEmpty(analysis.ErrorMessage))
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"  [red]Error: {Markup.Escape(analysis.ErrorMessage)}[/]");
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[grey][[V]][/][grey]iew individual calibrations  |  [[D]][/][grey]elete  |  Press any other key to go back...[/]");

            var key = Console.ReadKey(true).Key;
            if (key == ConsoleKey.D)
            {
                if (_ui.Confirm("Delete this analysis?", false))
                {
                    var (deleteSuccess, error) = await _ipcClient.DeleteAnalysisAsync(info.FileName);
                    if (deleteSuccess)
                    {
                        _ui.WriteSuccess("Analysis deleted.");
                    }
                    else
                    {
                        _ui.WriteError($"Failed to delete: {error ?? "Unknown error"}");
                    }
                    await Task.Delay(1500);
                    return;
                }
            }
            else if (key == ConsoleKey.V)
            {
                await ShowIndividualCalibrationsAsync(analysis);
            }
            else
            {
                return;
            }
        }
    }

    private Task ShowIndividualCalibrationsAsync(AnalysisSession analysis)
    {
        if (analysis.Calibrations.Count == 0)
        {
            _ui.WriteInfo("No individual calibration data available");
            _ui.WaitForKey("Press any key to continue...");
            return Task.CompletedTask;
        }

        // Start at first calibration and allow cycling with Up/Down arrows
        int currentIndex = 0;

        while (true)
        {
            ShowIndividualCalibrationWithNavigation(analysis, currentIndex);

            var key = Console.ReadKey(true).Key;

            switch (key)
            {
                case ConsoleKey.DownArrow:
                case ConsoleKey.RightArrow:
                    // Next calibration, wrap to first if at end
                    currentIndex = (currentIndex + 1) % analysis.Calibrations.Count;
                    break;

                case ConsoleKey.UpArrow:
                case ConsoleKey.LeftArrow:
                    // Previous calibration, wrap to last if at start
                    currentIndex = currentIndex > 0 ? currentIndex - 1 : analysis.Calibrations.Count - 1;
                    break;

                case ConsoleKey.Escape:
                case ConsoleKey.Backspace:
                case ConsoleKey.Q:
                    return Task.CompletedTask;

                default:
                    // Any other key exits
                    return Task.CompletedTask;
            }
        }
    }

    private void ShowIndividualCalibrationWithNavigation(AnalysisSession analysis, int calibrationIndex)
    {
        var calibration = analysis.Calibrations[calibrationIndex];

        _ui.Clear();
        var titleSuffix = !string.IsNullOrEmpty(analysis.Name) ? $" ({analysis.Name})" : "";
        _ui.WriteRule($"Calibration #{calibrationIndex + 1} of {analysis.Calibrations.Count} - {analysis.PrinterName}{titleSuffix}");
        _ui.WriteLine();

        // Use the visual mesh renderer
        if (calibration.Points.Length > 0)
        {
            MeshVisualizer.RenderIndividualCalibration(calibration, calibrationIndex + 1, analysis.AnalysisStats);
        }
        else
        {
            _ui.WriteInfo("No mesh data available for this calibration.");
        }

        _ui.WriteLine();
        AnsiConsole.MarkupLine("[grey]↑/↓ Previous/Next calibration  |  Any other key to go back...[/]");
    }


    #region Input Validation Helpers

    /// <summary>
    /// Validates a printer name: non-empty, alphanumeric with dashes/underscores.
    /// </summary>
    private string? ValidatePrinterName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Printer name cannot be empty";
        if (value.Length > 50)
            return "Printer name too long (max 50 characters)";
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z0-9_-]+$"))
            return "Printer name can only contain letters, numbers, dashes and underscores";
        return null; // Valid
    }

    /// <summary>
    /// Validates an IP address (IPv4/IPv6) or hostname.
    /// </summary>
    private string? ValidateIpAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Address cannot be empty";

        // Try parsing as IPv4 or IPv6
        if (System.Net.IPAddress.TryParse(value, out _))
            return null; // Valid IP address

        // Validate as hostname
        return ValidateHostname(value);
    }

    /// <summary>
    /// Validates a hostname format.
    /// </summary>
    private string? ValidateHostname(string value)
    {
        // Hostname max length is 253 characters
        if (value.Length > 253)
            return "Hostname too long (max 253 characters)";

        // Split into labels
        var labels = value.Split('.');

        foreach (var label in labels)
        {
            // Each label must be 1-63 characters
            if (string.IsNullOrEmpty(label) || label.Length > 63)
                return "Invalid hostname format (empty or too long label)";

            // Labels can only contain alphanumeric and hyphens
            if (!System.Text.RegularExpressions.Regex.IsMatch(label, @"^[a-zA-Z0-9-]+$"))
                return "Hostname contains invalid characters (only letters, numbers, hyphens allowed)";

            // Labels cannot start or end with hyphen
            if (label.StartsWith('-') || label.EndsWith('-'))
                return "Hostname labels cannot start or end with a hyphen";
        }

        return null; // Valid hostname
    }

    /// <summary>
    /// Validates a username: non-empty, reasonable characters.
    /// </summary>
    private string? ValidateUsername(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Username cannot be empty";
        if (value.Length > 32)
            return "Username too long (max 32 characters)";
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-zA-Z0-9_.-]+$"))
            return "Username contains invalid characters";
        return null; // Valid
    }

    /// <summary>
    /// Validates a TCP port (1-65535).
    /// </summary>
    private string? ValidatePort(int value)
    {
        if (value < 1 || value > 65535)
            return "Port must be between 1 and 65535";
        return null; // Valid
    }

    /// <summary>
    /// Ask for a string with validation and retry.
    /// </summary>
    private string? AskValidatedString(string prompt, Func<string, string?> validator, string? defaultValue = null)
    {
        const int maxRetries = 5;
        for (int i = 0; i < maxRetries; i++)
        {
            var value = defaultValue != null ? _ui.Ask(prompt, defaultValue) : _ui.Ask(prompt);

            var error = validator(value);
            if (error == null)
                return value;

            _ui.WriteError(error);
            if (i < maxRetries - 1)
                _ui.WriteWarning("Please try again.");
        }

        _ui.WriteError("Too many invalid attempts. Operation cancelled.");
        return null;
    }

    /// <summary>
    /// Ask for a validated TCP port.
    /// </summary>
    private int? AskValidatedPort(string prompt, int defaultValue)
    {
        const int maxRetries = 5;
        for (int i = 0; i < maxRetries; i++)
        {
            var value = _ui.AskInt(prompt, defaultValue);

            var error = ValidatePort(value);
            if (error == null)
                return value;

            _ui.WriteError(error);
            if (i < maxRetries - 1)
                _ui.WriteWarning("Please try again.");
        }

        _ui.WriteError("Too many invalid attempts. Operation cancelled.");
        return null;
    }

    /// <summary>
    /// Ask for a port with retry if already in use.
    /// </summary>
    private Task<int?> AskPortWithRetryAsync(string prompt, int defaultValue, string printerName)
    {
        return AskPortWithRetryAsync(prompt, defaultValue, printerName, null);
    }

    /// <summary>
    /// Ask for a port with retry if already in use.
    /// When currentPort is provided, that port is considered valid (for modify operations).
    /// </summary>
    private async Task<int?> AskPortWithRetryAsync(string prompt, int defaultValue, string printerName, int? currentPort)
    {
        const int maxRetries = 5;
        int currentDefault = defaultValue;

        for (int i = 0; i < maxRetries; i++)
        {
            var value = _ui.AskInt(prompt, currentDefault);

            // Validate port range
            var portError = ValidatePort(value);
            if (portError != null)
            {
                _ui.WriteError(portError);
                continue;
            }

            // Check if port is in use by another printer via IPC
            var (success, printers, _) = await _ipcClient!.ListPrintersAsync();
            if (success && printers != null)
            {
                var conflict = printers.FirstOrDefault(p => p.MjpegPort == value && p.Name != printerName);
                if (conflict != null)
                {
                    _ui.WriteError($"Port {value} is already in use by printer '{conflict.Name}'");
                    _ui.WriteWarning("Please choose a different port.");
                    currentDefault = value + 1; // Suggest next port
                    continue;
                }
            }

            // Check if port is available on the system
            // Skip this check if the port is the current port (modify operation)
            if (currentPort.HasValue && value == currentPort.Value)
            {
                // Port is already in use by this printer, no need to check system availability
                return value;
            }

            if (!IsPortAvailable(value))
            {
                _ui.WriteError($"Port {value} is not available on the system (already bound by another application)");
                _ui.WriteWarning("Please choose a different port.");
                currentDefault = value + 1;
                continue;
            }

            return value;
        }

        _ui.WriteError("Too many invalid attempts. Operation cancelled.");
        return null;
    }

    /// <summary>
    /// Ask for a printer name with retry if invalid or duplicate.
    /// When originalName is provided, that name is excluded from duplicate check (for modify operations).
    /// </summary>
    private async Task<string?> AskPrinterNameWithRetryAsync(string prompt, string defaultValue, string? originalName = null)
    {
        const int maxRetries = 5;

        for (int i = 0; i < maxRetries; i++)
        {
            var value = _ui.Ask(prompt, defaultValue);

            // Validate name format
            var nameError = ValidatePrinterName(value);
            if (nameError != null)
            {
                _ui.WriteError(nameError);
                if (i < maxRetries - 1)
                    _ui.WriteWarning("Please try again.");
                continue;
            }

            // Check if name is already used by another printer (unless it's the original name)
            if (value != originalName)
            {
                var (success, printers, _) = await _ipcClient!.ListPrintersAsync();
                if (success && printers != null)
                {
                    var conflict = printers.FirstOrDefault(p => p.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
                    if (conflict != null)
                    {
                        _ui.WriteError($"A printer named '{conflict.Name}' already exists");
                        _ui.WriteWarning("Please choose a different name.");
                        continue;
                    }
                }
            }

            return value;
        }

        _ui.WriteError("Too many invalid attempts. Operation cancelled.");
        return null;
    }

    /// <summary>
    /// Ask for an integer with min/max validation.
    /// </summary>
    private int AskValidatedInt(string prompt, int min, int max, int defaultValue)
    {
        const int maxRetries = 5;
        for (int i = 0; i < maxRetries; i++)
        {
            var value = _ui.AskInt(prompt, defaultValue);

            if (value >= min && value <= max)
                return value;

            _ui.WriteError($"Value must be between {min} and {max}");
            if (i < maxRetries - 1)
                _ui.WriteWarning("Please try again.");
        }

        _ui.WriteWarning($"Using default value: {defaultValue}");
        return defaultValue;
    }

    /// <summary>
    /// Check if a port is available on the system.
    /// </summary>
    private bool IsPortAvailable(int port)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Run pre-flight connectivity check for a printer.
    /// </summary>
    private async Task<bool> RunPreflightCheckAsync(PrinterConfig config)
    {
        _ui.WriteInfo("Running pre-flight check...");
        _ui.WriteLine();

        var allPassed = true;

        // Check 1: Ping
        _ui.WriteInfo("  [1/4] Checking network connectivity...");
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(config.Ip, 3000);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                _ui.WriteSuccess($"    Ping OK ({reply.RoundtripTime}ms)");
            }
            else
            {
                _ui.WriteError($"    Ping failed: {reply.Status}");
                allPassed = false;
            }
        }
        catch (Exception ex)
        {
            _ui.WriteError($"    Ping failed: {ex.Message}");
            allPassed = false;
        }

        // Check 2: SSH port
        _ui.WriteInfo($"  [2/4] Checking SSH port ({config.SshPort})...");
        if (await CheckTcpPortAsync(config.Ip, config.SshPort))
        {
            _ui.WriteSuccess("    SSH port is open");
        }
        else
        {
            _ui.WriteError("    SSH port is not reachable");
            allPassed = false;
        }

        // Check 3: MQTT port
        _ui.WriteInfo($"  [3/4] Checking MQTT port ({config.MqttPort})...");
        if (await CheckTcpPortAsync(config.Ip, config.MqttPort))
        {
            _ui.WriteSuccess("    MQTT port is open");
        }
        else
        {
            _ui.WriteError("    MQTT port is not reachable");
            allPassed = false;
        }

        // Check 4: HTTP stream port (18088)
        _ui.WriteInfo("  [4/4] Checking camera stream port (18088)...");
        if (await CheckTcpPortAsync(config.Ip, 18088))
        {
            _ui.WriteSuccess("    Camera stream port is open");
        }
        else
        {
            _ui.WriteWarning("    Camera stream port not reachable (camera may not be running yet)");
            // Don't fail on this - camera might start after MQTT command
        }

        _ui.WriteLine();
        if (allPassed)
        {
            _ui.WriteSuccess("Pre-flight check passed!");
        }
        else
        {
            _ui.WriteWarning("Pre-flight check had some failures.");
        }

        return allPassed;
    }

    /// <summary>
    /// Check if a TCP port is reachable.
    /// </summary>
    private async Task<bool> CheckTcpPortAsync(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(3000));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    #endregion
}
