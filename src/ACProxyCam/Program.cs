// ACProxyCam - Anycubic Camera Proxy for Linux
// Converts FLV camera streams to MJPEG for Mainsail/Fluidd/Moonraker compatibility

using System.Reflection;
using ACProxyCam.Client;
using ACProxyCam.Daemon;

namespace ACProxyCam;

public class Program
{
    public static readonly string Version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

    public static async Task<int> Main(string[] args)
    {
        // Check for flags (hidden options)
        var useSimpleUi = args.Contains("--simple-ui");
        var debugMode = args.Contains("--debug");
        var filteredArgs = args.Where(a => a != "--simple-ui" && a != "--debug").ToArray();

        // Set debug mode globally
        if (debugMode)
        {
            Daemon.Logger.DebugEnabled = true;
        }

        // Parse command line arguments
        if (filteredArgs.Length > 0)
        {
            switch (filteredArgs[0].ToLower())
            {
                case "-v":
                case "--version":
                    Console.WriteLine($"acproxycam {Version}");
                    return 0;

                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;

                case "--daemon":
                    // Run as daemon (called by systemd)
                    return await RunDaemonAsync(filteredArgs);

                case "--install":
                    // Installation (uses simple UI by default for scripting)
                    return await RunInstallAsync(useSimpleUi || !Console.IsInputRedirected);

                case "--uninstall":
                    // Uninstallation (uses simple UI by default for scripting)
                    return await RunUninstallAsync(
                        filteredArgs.Contains("--keep-config"),
                        useSimpleUi || !Console.IsInputRedirected);

                default:
                    Console.WriteLine($"Unknown argument: {filteredArgs[0]}");
                    PrintHelp();
                    return 1;
            }
        }

        // No arguments - enter management interface
        return await RunManagementAsync(useSimpleUi);
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"acproxycam {Version} - Anycubic Camera Proxy for Linux");
        Console.WriteLine();
        Console.WriteLine("Usage: acproxycam [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -v, --version    Show version information");
        Console.WriteLine("  -h, --help       Show this help message");
        Console.WriteLine("  --daemon         Run as daemon (used by systemd)");
        Console.WriteLine("  --install        Install service");
        Console.WriteLine("  --uninstall      Uninstall service");
        Console.WriteLine("    --keep-config  Keep config files when uninstalling");
        Console.WriteLine();
        Console.WriteLine("Without arguments, enters the interactive management interface.");
        Console.WriteLine();
        Console.WriteLine("For more information, see: https://github.com/yourusername/acproxycam");
    }

    private static async Task<int> RunDaemonAsync(string[] args)
    {
        try
        {
            var daemon = new DaemonService();
            await daemon.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Daemon error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunManagementAsync(bool useSimpleUi)
    {
        try
        {
            IConsoleUI ui = useSimpleUi ? new SimpleConsoleUI() : new SpectreConsoleUI();
            var cli = new ManagementCli(ui);
            return await cli.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunInstallAsync(bool useSimpleUi)
    {
        try
        {
            IConsoleUI ui = useSimpleUi ? new SimpleConsoleUI() : new SpectreConsoleUI();
            var cli = new ManagementCli(ui);
            return await cli.InstallAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunUninstallAsync(bool keepConfig, bool useSimpleUi)
    {
        try
        {
            IConsoleUI ui = useSimpleUi ? new SimpleConsoleUI() : new SpectreConsoleUI();
            var cli = new ManagementCli(ui);
            return await cli.UninstallAsync(keepConfig);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
