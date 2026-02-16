// DaemonService.cs - Main daemon service loop

using System.Runtime.InteropServices;
using ACProxyCam.Models;

namespace ACProxyCam.Daemon;

/// <summary>
/// Main daemon service that manages printer threads and IPC.
/// </summary>
public class DaemonService
{
    private readonly CancellationTokenSource _cts = new();
    private readonly DateTime _startTime = DateTime.UtcNow;
    private IpcServer? _ipcServer;
    private PrinterManager? _printerManager;
    private BedMeshManager? _bedMeshManager;
    private AppConfig? _config;

    // Signal handlers - must be stored to prevent GC
    private PosixSignalRegistration? _sigtermHandler;
    private PosixSignalRegistration? _sigintHandler;
    private PosixSignalRegistration? _sighupHandler;

    public TimeSpan Uptime => DateTime.UtcNow - _startTime;

    public async Task RunAsync()
    {
        // Initialize file logging
        Logger.Initialize("/var/log/acproxycam");
        Logger.Log($"ACProxyCam v{Program.Version} starting...");

        // Set up signal handlers for graceful shutdown
        SetupSignalHandlers();

        // Load configuration
        _config = await ConfigManager.LoadAsync();
        Logger.Log($"Configuration loaded: {_config.Printers.Count} printers");

        // Initialize printer manager
        _printerManager = new PrinterManager(_config);

        // Initialize BedMesh manager
        _bedMeshManager = new BedMeshManager(_printerManager);

        // Start IPC server
        _ipcServer = new IpcServer(this, _printerManager, _bedMeshManager);
        await _ipcServer.StartAsync();
        Logger.Log("IPC server started");

        // Start all configured printers
        await _printerManager.StartAllAsync();

        // Start BedMesh monitoring (after printers so we can find them for recovery)
        await _bedMeshManager.StartAsync();

        // Notify systemd we're ready (if running under systemd)
        NotifySystemdReady();

        Logger.Log("Daemon ready");

        // Main loop - wait for cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        // Graceful shutdown
        Logger.Log("Shutting down...");

        if (_bedMeshManager != null)
            await _bedMeshManager.StopAsync();
        await _printerManager.StopAllAsync();
        _ipcServer.Stop();

        Logger.Log("Daemon stopped");
        Logger.Shutdown();
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public DaemonStatusData GetStatus()
    {
        var printers = _printerManager?.GetAllStatus() ?? new List<PrinterStatus>();

        return new DaemonStatusData
        {
            Version = Program.Version,
            Running = true,
            Uptime = Uptime,
            PrinterCount = printers.Count,
            ActiveStreamers = printers.Count(p => p.State == PrinterState.Running),
            InactiveStreamers = printers.Count(p => p.State != PrinterState.Running),
            TotalClients = printers.Sum(p => p.ConnectedClients),
            ListenInterfaces = _config?.ListenInterfaces ?? new List<string>()
        };
    }

    public async Task ReloadConfigAsync()
    {
        Logger.Log("Reloading configuration from disk...");
        _config = await ConfigManager.LoadAsync();
        _printerManager?.UpdateConfig(_config);
        Logger.Log($"Configuration reloaded: {_config.Printers.Count} printers");
    }

    public async Task ChangeInterfacesAsync(List<string> interfaces)
    {
        if (_config != null)
        {
            var oldInterfaces = string.Join(", ", _config.ListenInterfaces);
            var newInterfaces = string.Join(", ", interfaces);
            Logger.Log($"Changing listen interfaces: [{oldInterfaces}] -> [{newInterfaces}]");

            _config.ListenInterfaces = interfaces;
            await ConfigManager.SaveAsync(_config);

            // Restart printer threads with new interfaces
            Logger.Log("Restarting all printers due to interface change...");
            await _printerManager?.RestartAllAsync()!;
        }
    }

    private void SetupSignalHandlers()
    {
        // Handle SIGTERM (systemd stop) - store to prevent GC
        _sigtermHandler = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            Logger.Log("Received SIGTERM");
            context.Cancel = true;  // Prevent default termination, let us shutdown gracefully
            _cts.Cancel();
        });

        // Handle SIGINT (Ctrl+C) - store to prevent GC
        _sigintHandler = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            Logger.Log("Received SIGINT");
            context.Cancel = true;  // Prevent default termination, let us shutdown gracefully
            _cts.Cancel();
        });

        // Handle SIGHUP (reload config) - store to prevent GC
        _sighupHandler = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            Logger.Log("Received SIGHUP - reloading config");
            _ = ReloadConfigAsync();
        });
    }

    private void NotifySystemdReady()
    {
        // sd_notify(0, "READY=1") equivalent
        var notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (!string.IsNullOrEmpty(notifySocket))
        {
            try
            {
                // This would need a proper implementation with Unix sockets
                // For now, just log that we would notify
                Logger.Log("Would notify systemd: READY=1");
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}
