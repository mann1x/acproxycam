// ManagementCli.cs - Interactive terminal management interface

using Spectre.Console;
using ACProxyCam.Daemon;
using ACProxyCam.Models;

namespace ACProxyCam.Client;

/// <summary>
/// Interactive terminal management interface using Spectre.Console.
/// </summary>
public class ManagementCli
{
    private IpcClient? _ipcClient;

    public async Task<int> RunAsync()
    {
        // Display header
        AnsiConsole.Write(new FigletText("ACProxyCam").Color(Color.Purple));
        AnsiConsole.MarkupLine($"[grey]Version {Program.Version}[/]");
        AnsiConsole.WriteLine();

        // Check if running as root/sudo
        if (!IsRunningAsRoot())
        {
            AnsiConsole.MarkupLine("[red]Error: This application requires root privileges.[/]");
            AnsiConsole.MarkupLine("[yellow]Please run with sudo: sudo acproxycam[/]");
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
                AnsiConsole.MarkupLine("[yellow]Service is installed but not running.[/]");
                var startService = AnsiConsole.Confirm("Would you like to start the service?");
                if (startService)
                {
                    await StartServiceAsync();
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
            AnsiConsole.MarkupLine("[red]Error: Cannot connect to daemon.[/]");
            return 1;
        }

        // Enter management loop
        return await ManagementLoopAsync();
    }

    private bool IsRunningAsRoot()
    {
        // Check if running as root (UID 0)
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
        AnsiConsole.MarkupLine("[yellow]ACProxyCam is not installed.[/]");
        AnsiConsole.WriteLine();

        var choices = new[] { "Install ACProxyCam", "Quit" };
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .AddChoices(choices));

        if (choice == "Quit")
        {
            return 0;
        }

        // Installation flow
        return await InstallServiceAsync();
    }

    private async Task<int> InstallServiceAsync()
    {
        AnsiConsole.MarkupLine("[blue]Installing ACProxyCam...[/]");
        AnsiConsole.WriteLine();

        // Step 1: Check FFmpeg
        if (!await CheckAndInstallFfmpegAsync())
        {
            return 1;
        }

        // Step 2: Select listening interfaces
        var interfaces = await SelectInterfacesAsync();
        if (interfaces == null)
        {
            return 0; // User cancelled
        }

        // Step 3: Create user and directories
        await AnsiConsole.Status()
            .StartAsync("Creating acproxycam user...", async ctx =>
            {
                await CreateUserAndDirectoriesAsync();
            });

        // Step 4: Create configuration
        await AnsiConsole.Status()
            .StartAsync("Creating configuration...", async ctx =>
            {
                var config = new AppConfig
                {
                    ListenInterfaces = interfaces
                };
                await ConfigManager.SaveAsync(config);
            });

        // Step 5: Install systemd service
        await AnsiConsole.Status()
            .StartAsync("Installing systemd service...", async ctx =>
            {
                await InstallSystemdServiceAsync();
            });

        // Step 6: Start service
        await AnsiConsole.Status()
            .StartAsync("Starting service...", async ctx =>
            {
                await StartServiceAsync();
            });

        AnsiConsole.MarkupLine("[green]Installation complete![/]");
        AnsiConsole.WriteLine();

        // Continue to management
        await Task.Delay(1000);
        _ipcClient = new IpcClient();
        _ipcClient.Connect();

        return await ManagementLoopAsync();
    }

    private async Task<bool> CheckAndInstallFfmpegAsync()
    {
        // Check if FFmpeg is installed
        var ffmpegInstalled = File.Exists("/usr/bin/ffmpeg") || File.Exists("/usr/local/bin/ffmpeg");

        if (ffmpegInstalled)
        {
            AnsiConsole.MarkupLine("[green]FFmpeg is installed.[/]");
            return true;
        }

        AnsiConsole.MarkupLine("[yellow]FFmpeg is not installed.[/]");

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
            var install = AnsiConsole.Confirm($"Would you like to install FFmpeg using {packageManager}?");
            if (install)
            {
                await AnsiConsole.Status()
                    .StartAsync("Installing FFmpeg...", async ctx =>
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

        AnsiConsole.MarkupLine("[red]FFmpeg is required for ACProxyCam to function.[/]");
        AnsiConsole.MarkupLine("[yellow]Please install FFmpeg manually and try again.[/]");
        return false;
    }

    private async Task<List<string>?> SelectInterfacesAsync()
    {
        AnsiConsole.MarkupLine("[blue]Select listening interfaces for MJPEG streams:[/]");
        AnsiConsole.WriteLine();

        // Get available interfaces
        var interfaces = GetNetworkInterfaces();

        var choices = new List<string> { "All interfaces (0.0.0.0)", "localhost (127.0.0.1)" };
        choices.AddRange(interfaces.Select(i => $"{i.Name} ({i.Address})"));
        choices.Add("Cancel");

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("Select interfaces (Space to toggle, Enter to confirm):")
                .AddChoices(choices)
                .InstructionsText("[grey](Press space to toggle, enter to accept)[/]"));

        if (selected.Contains("Cancel"))
        {
            return null;
        }

        var result = new List<string>();

        if (selected.Contains("All interfaces (0.0.0.0)"))
        {
            result.Add("0.0.0.0");
        }
        else
        {
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
        }

        if (result.Count == 0)
        {
            result.Add("127.0.0.1"); // Default to localhost
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

    private async Task CreateUserAndDirectoriesAsync()
    {
        // Create acproxycam user if doesn't exist
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-c \"id acproxycam &>/dev/null || useradd -r -s /bin/false acproxycam\"",
            UseShellExecute = false
        });
        await process!.WaitForExitAsync();

        // Create directories
        Directory.CreateDirectory("/etc/acproxycam");
        Directory.CreateDirectory("/var/log/acproxycam");

        // Set ownership
        process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = "-c \"chown acproxycam:acproxycam /etc/acproxycam /var/log/acproxycam\"",
            UseShellExecute = false
        });
        await process!.WaitForExitAsync();
    }

    private async Task InstallSystemdServiceAsync()
    {
        var serviceContent = @"[Unit]
Description=ACProxyCam - Anycubic Camera Proxy
After=network.target

[Service]
Type=notify
User=acproxycam
Group=acproxycam
ExecStart=/usr/local/bin/acproxycam --daemon
Restart=on-failure
RestartSec=5
WatchdogSec=30

[Install]
WantedBy=multi-user.target
";

        await File.WriteAllTextAsync("/etc/systemd/system/acproxycam.service", serviceContent);

        // Copy binary
        var currentExe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (currentExe != null && currentExe != "/usr/local/bin/acproxycam")
        {
            File.Copy(currentExe, "/usr/local/bin/acproxycam", true);
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/chmod",
                Arguments = "+x /usr/local/bin/acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
        }

        // Reload systemd
        var reloadProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/systemctl",
            Arguments = "daemon-reload",
            UseShellExecute = false
        });
        await reloadProcess!.WaitForExitAsync();

        // Enable service
        var enableProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/systemctl",
            Arguments = "enable acproxycam",
            UseShellExecute = false
        });
        await enableProcess!.WaitForExitAsync();

        // Install logrotate configuration
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
    }

    private async Task StartServiceAsync()
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/systemctl",
            Arguments = "start acproxycam",
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
    }

    private async Task<int> ManagementLoopAsync()
    {
        while (true)
        {
            Console.Clear();
            await DisplayHeaderAsync();
            await DisplayPrinterListAsync();
            DisplayKeyHelp();

            var key = Console.ReadKey(true);

            switch (char.ToUpper(key.KeyChar))
            {
                case 'Q':
                    return 0;

                case 'S':
                    await ToggleServiceAsync();
                    break;

                case 'R':
                    await RestartServiceAsync();
                    break;

                case 'U':
                    var result = await UninstallAsync();
                    if (result == 0) return 0;
                    break;

                case 'L':
                    await ChangeInterfacesAsync();
                    break;

                case 'A':
                    await AddPrinterAsync();
                    break;

                case 'D':
                    await DeletePrinterAsync();
                    break;

                case 'M':
                    await ModifyPrinterAsync();
                    break;

                case ' ':
                    await TogglePrinterPauseAsync();
                    break;

                case '\r': // Enter
                    await ShowPrinterDetailsAsync();
                    break;
            }
        }
    }

    private async Task DisplayHeaderAsync()
    {
        var (success, status, _) = await _ipcClient!.GetStatusAsync();

        var statusColor = success && status?.Running == true ? "green" : "red";
        var statusText = success && status?.Running == true ? "Running" : "Stopped";

        var header = new Panel(
            $"[bold]ACProxyCam[/] v{Program.Version}  |  Service: [{statusColor}]● {statusText}[/]  |  " +
            $"Printers: {status?.PrinterCount ?? 0}  |  Active: {status?.ActiveStreamers ?? 0}  |  " +
            $"Inactive: {status?.InactiveStreamers ?? 0}  |  Clients: {status?.TotalClients ?? 0}")
        {
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(header);
        AnsiConsole.WriteLine();
    }

    private async Task DisplayPrinterListAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No printers configured. Press [A] to add a printer.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("IP")
            .AddColumn("Port")
            .AddColumn("Status")
            .AddColumn("Clients");

        foreach (var printer in printers)
        {
            var statusColor = printer.State switch
            {
                PrinterState.Running => "green",
                PrinterState.Paused => "yellow",
                PrinterState.Failed => "red",
                PrinterState.Retrying => "orange3",
                _ => "grey"
            };

            var statusSymbol = printer.State switch
            {
                PrinterState.Running => "●",
                PrinterState.Paused => "◐",
                PrinterState.Failed => "○",
                PrinterState.Retrying => "◌",
                _ => "○"
            };

            table.AddRow(
                printer.Name,
                printer.Ip,
                printer.MjpegPort.ToString(),
                $"[{statusColor}]{statusSymbol} {printer.State}[/]",
                printer.ConnectedClients.ToString()
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private void DisplayKeyHelp()
    {
        AnsiConsole.MarkupLine("[grey]Service: [S]top/Start  [R]estart  [U]ninstall  [L]isten  |  Printers: [A]dd  [D]elete  [M]odify  [Space]Pause  [Enter]Details  |  [Q]uit[/]");
    }

    private async Task ToggleServiceAsync()
    {
        var (success, status, _) = await _ipcClient!.GetStatusAsync();

        if (success && status?.Running == true)
        {
            // Stop service
            AnsiConsole.MarkupLine("[yellow]Stopping service...[/]");
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/systemctl",
                Arguments = "stop acproxycam",
                UseShellExecute = false
            });
            await process!.WaitForExitAsync();
            _ipcClient = null;
            AnsiConsole.MarkupLine("[green]Service stopped.[/]");
            await Task.Delay(1000);
        }
        else
        {
            // Start service
            await StartServiceAsync();
            _ipcClient = new IpcClient();
            _ipcClient.Connect();
        }
    }

    private async Task RestartServiceAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Restarting service...", async ctx =>
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

    private async Task<int> UninstallAsync()
    {
        if (!AnsiConsole.Confirm("[red]Are you sure you want to uninstall ACProxyCam?[/]", false))
        {
            return 1;
        }

        var keepConfig = AnsiConsole.Confirm("Keep configuration files?", true);

        await AnsiConsole.Status()
            .StartAsync("Uninstalling...", async ctx =>
            {
                // Stop service
                ctx.Status("Stopping service...");
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/systemctl",
                    Arguments = "stop acproxycam",
                    UseShellExecute = false
                });
                await process!.WaitForExitAsync();

                // Disable service
                ctx.Status("Disabling service...");
                process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/systemctl",
                    Arguments = "disable acproxycam",
                    UseShellExecute = false
                });
                await process!.WaitForExitAsync();

                // Remove service file
                ctx.Status("Removing service file...");
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
                ctx.Status("Removing binary...");
                if (File.Exists("/usr/local/bin/acproxycam"))
                {
                    File.Delete("/usr/local/bin/acproxycam");
                }

                // Remove config if requested
                if (!keepConfig)
                {
                    ctx.Status("Removing configuration...");
                    if (Directory.Exists("/etc/acproxycam"))
                    {
                        Directory.Delete("/etc/acproxycam", true);
                    }
                }

                // Remove log directory
                ctx.Status("Removing logs...");
                if (Directory.Exists("/var/log/acproxycam"))
                {
                    Directory.Delete("/var/log/acproxycam", true);
                }

                // Remove user
                ctx.Status("Removing user...");
                process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "-c \"userdel acproxycam 2>/dev/null || true\"",
                    UseShellExecute = false
                });
                await process!.WaitForExitAsync();
            });

        AnsiConsole.MarkupLine("[green]ACProxyCam has been uninstalled.[/]");
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
            AnsiConsole.MarkupLine("[green]Listening interfaces updated.[/]");
            AnsiConsole.MarkupLine("[yellow]Restart service for changes to take effect.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error: {error}[/]");
        }

        await Task.Delay(2000);
    }

    private async Task AddPrinterAsync()
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[blue]Add Printer[/]"));
        AnsiConsole.WriteLine();

        // Get printer name
        var name = AnsiConsole.Ask<string>("Printer name (unique identifier):");
        if (string.IsNullOrWhiteSpace(name))
            return;

        // Get printer IP
        var ip = AnsiConsole.Ask<string>("Printer IP address:");
        if (string.IsNullOrWhiteSpace(ip))
            return;

        // Get MJPEG port
        var mjpegPort = AnsiConsole.Ask("MJPEG listening port:", 8080);

        // SSH settings
        var sshPort = AnsiConsole.Ask("SSH port:", 22);
        var sshUser = AnsiConsole.Ask("SSH username:", "root");
        var sshPassword = AnsiConsole.Prompt(
            new TextPrompt<string>("SSH password:")
                .DefaultValue("rockchip")
                .Secret(null));

        // MQTT settings
        var mqttPort = AnsiConsole.Ask("MQTT port:", 9883);

        // Create printer config
        var config = new PrinterConfig
        {
            Name = name,
            Ip = ip,
            MjpegPort = mjpegPort,
            SshPort = sshPort,
            SshUser = sshUser,
            SshPassword = sshPassword,
            MqttPort = mqttPort
        };

        // Add printer
        var (success, error) = await _ipcClient!.AddPrinterAsync(config);

        if (success)
        {
            AnsiConsole.MarkupLine("[green]Printer added successfully![/]");
            AnsiConsole.MarkupLine($"[grey]MJPEG stream will be available at: http://[server]:{mjpegPort}/stream[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error: {error}[/]");
        }

        await Task.Delay(2000);
    }

    private async Task DeletePrinterAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No printers to delete.[/]");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => p.Name).ToList();
        choices.Add("Cancel");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select printer to delete:")
                .AddChoices(choices));

        if (selected == "Cancel")
            return;

        if (!AnsiConsole.Confirm($"[red]Delete printer '{selected}'?[/]", false))
            return;

        var (deleteSuccess, error) = await _ipcClient.DeletePrinterAsync(selected);

        if (deleteSuccess)
        {
            AnsiConsole.MarkupLine("[green]Printer deleted.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error: {error}[/]");
        }

        await Task.Delay(1500);
    }

    private async Task ModifyPrinterAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No printers to modify.[/]");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => p.Name).ToList();
        choices.Add("Cancel");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select printer to modify:")
                .AddChoices(choices));

        if (selected == "Cancel")
            return;

        var printer = printers.First(p => p.Name == selected);

        // Get existing config
        var (configSuccess, existingConfig, _) = await _ipcClient.GetPrinterConfigAsync(selected);
        if (!configSuccess || existingConfig == null)
        {
            AnsiConsole.MarkupLine("[red]Error: Could not get printer config.[/]");
            await Task.Delay(1500);
            return;
        }

        Console.Clear();
        AnsiConsole.Write(new Rule($"[blue]Modify Printer: {selected}[/]"));
        AnsiConsole.MarkupLine("[grey]Press Enter to keep current value[/]");
        AnsiConsole.WriteLine();

        // Modify settings
        var ip = AnsiConsole.Ask($"IP address [{existingConfig.Ip}]:", existingConfig.Ip);
        var mjpegPort = AnsiConsole.Ask($"MJPEG port [{existingConfig.MjpegPort}]:", existingConfig.MjpegPort);
        var sshPort = AnsiConsole.Ask($"SSH port [{existingConfig.SshPort}]:", existingConfig.SshPort);
        var sshUser = AnsiConsole.Ask($"SSH user [{existingConfig.SshUser}]:", existingConfig.SshUser);
        var mqttPort = AnsiConsole.Ask($"MQTT port [{existingConfig.MqttPort}]:", existingConfig.MqttPort);

        // Update config
        existingConfig.Ip = ip;
        existingConfig.MjpegPort = mjpegPort;
        existingConfig.SshPort = sshPort;
        existingConfig.SshUser = sshUser;
        existingConfig.MqttPort = mqttPort;

        var (modifySuccess, error) = await _ipcClient.ModifyPrinterAsync(existingConfig);

        if (modifySuccess)
        {
            AnsiConsole.MarkupLine("[green]Printer modified.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error: {error}[/]");
        }

        await Task.Delay(1500);
    }

    private async Task TogglePrinterPauseAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No printers available.[/]");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => $"{p.Name} ({(p.IsPaused ? "Paused" : "Running")})").ToList();
        choices.Add("Cancel");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select printer to pause/resume:")
                .AddChoices(choices));

        if (selected == "Cancel")
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
            AnsiConsole.MarkupLine($"[green]Printer {(printer.IsPaused ? "resumed" : "paused")}.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error: {error}[/]");
        }

        await Task.Delay(1000);
    }

    private async Task ShowPrinterDetailsAsync()
    {
        var (success, printers, _) = await _ipcClient!.ListPrintersAsync();

        if (!success || printers == null || printers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No printers available.[/]");
            await Task.Delay(1500);
            return;
        }

        var choices = printers.Select(p => p.Name).ToList();
        choices.Add("Cancel");

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select printer to view:")
                .AddChoices(choices));

        if (selected == "Cancel")
            return;

        var printer = printers.First(p => p.Name == selected);

        Console.Clear();

        // Get detailed status
        var (statusSuccess, detailedStatus, _) = await _ipcClient.GetPrinterStatusAsync(selected);

        if (!statusSuccess || detailedStatus == null)
        {
            detailedStatus = printer;
        }

        // Display printer details
        var statusColor = detailedStatus.State switch
        {
            PrinterState.Running => "green",
            PrinterState.Paused => "yellow",
            PrinterState.Failed => "red",
            PrinterState.Retrying => "orange3",
            _ => "grey"
        };

        AnsiConsole.Write(new Rule($"[blue]{detailedStatus.Name}[/]"));
        AnsiConsole.WriteLine();

        var grid = new Grid()
            .AddColumn()
            .AddColumn();

        grid.AddRow("[grey]IP Address:[/]", detailedStatus.Ip);
        grid.AddRow("[grey]MJPEG Port:[/]", detailedStatus.MjpegPort.ToString());
        grid.AddRow("[grey]Status:[/]", $"[{statusColor}]{detailedStatus.State}[/]");
        grid.AddRow("[grey]Connected Clients:[/]", detailedStatus.ConnectedClients.ToString());

        if (detailedStatus.LastError != null)
        {
            grid.AddRow("[grey]Last Error:[/]", $"[red]{detailedStatus.LastError}[/]");
        }

        if (detailedStatus.LastSeenOnline.HasValue)
        {
            grid.AddRow("[grey]Last Online:[/]", detailedStatus.LastSeenOnline.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        if (detailedStatus.NextRetryAt.HasValue && detailedStatus.State == PrinterState.Retrying)
        {
            grid.AddRow("[grey]Next Retry:[/]", detailedStatus.NextRetryAt.Value.ToString("HH:mm:ss"));
        }

        AnsiConsole.Write(grid);

        // SSH Status
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[grey]SSH Status[/]"));
        var sshGrid = new Grid().AddColumn().AddColumn();
        sshGrid.AddRow("[grey]Connected:[/]", detailedStatus.SshStatus.Connected ? "[green]Yes[/]" : "[grey]No[/]");
        sshGrid.AddRow("[grey]Credentials Retrieved:[/]", detailedStatus.SshStatus.CredentialsRetrieved ? "[green]Yes[/]" : "[grey]No[/]");
        if (detailedStatus.SshStatus.Error != null)
        {
            sshGrid.AddRow("[grey]Error:[/]", $"[red]{detailedStatus.SshStatus.Error}[/]");
        }
        AnsiConsole.Write(sshGrid);

        // MQTT Status
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[grey]MQTT Status[/]"));
        var mqttGrid = new Grid().AddColumn().AddColumn();
        mqttGrid.AddRow("[grey]Connected:[/]", detailedStatus.MqttStatus.Connected ? "[green]Yes[/]" : "[grey]No[/]");
        mqttGrid.AddRow("[grey]Model Code:[/]", detailedStatus.MqttStatus.DetectedModelCode ?? "[grey]Unknown[/]");
        mqttGrid.AddRow("[grey]Camera Started:[/]", detailedStatus.MqttStatus.CameraStarted ? "[green]Yes[/]" : "[grey]No[/]");
        AnsiConsole.Write(mqttGrid);

        // Stream Status
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[grey]Stream Status[/]"));
        var streamGrid = new Grid().AddColumn().AddColumn();
        streamGrid.AddRow("[grey]Connected:[/]", detailedStatus.StreamStatus.Connected ? "[green]Yes[/]" : "[grey]No[/]");
        if (detailedStatus.StreamStatus.Width > 0)
        {
            streamGrid.AddRow("[grey]Resolution:[/]", $"{detailedStatus.StreamStatus.Width}x{detailedStatus.StreamStatus.Height}");
        }
        streamGrid.AddRow("[grey]Frames Decoded:[/]", detailedStatus.StreamStatus.FramesDecoded.ToString());
        if (detailedStatus.StreamStatus.DecoderStatus != null)
        {
            streamGrid.AddRow("[grey]Decoder Status:[/]", detailedStatus.StreamStatus.DecoderStatus);
        }
        AnsiConsole.Write(streamGrid);

        // URLs
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[grey]Stream URLs[/]"));
        AnsiConsole.MarkupLine($"[grey]MJPEG Stream:[/] http://[server]:{detailedStatus.MjpegPort}/stream");
        AnsiConsole.MarkupLine($"[grey]Snapshot:[/] http://[server]:{detailedStatus.MjpegPort}/snapshot");
        AnsiConsole.MarkupLine($"[grey]Status:[/] http://[server]:{detailedStatus.MjpegPort}/status");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to return...[/]");
        Console.ReadKey(true);
    }
}
