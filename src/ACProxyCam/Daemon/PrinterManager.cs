// PrinterManager.cs - Manages all printer threads

using ACProxyCam.Models;
using ACProxyCam.Services;

namespace ACProxyCam.Daemon;

/// <summary>
/// Manages the lifecycle of all printer threads.
/// </summary>
public class PrinterManager
{
    private readonly Dictionary<string, PrinterThread> _printers = new();
    private readonly object _lock = new();
    private AppConfig _config;

    public PrinterManager(AppConfig config)
    {
        _config = config;
    }

    public void UpdateConfig(AppConfig config)
    {
        var oldConfig = _config;
        _config = config;

        // Update Obico config for each running printer thread
        lock (_lock)
        {
            foreach (var newPrinterConfig in config.Printers)
            {
                if (_printers.TryGetValue(newPrinterConfig.Name, out var thread))
                {
                    // Find old config for comparison
                    var oldPrinterConfig = oldConfig.Printers.FirstOrDefault(p => p.Name == newPrinterConfig.Name);

                    // Check if local Obico config changed
                    var obicoChanged = oldPrinterConfig == null ||
                                       oldPrinterConfig.Obico.AuthToken != newPrinterConfig.Obico.AuthToken ||
                                       oldPrinterConfig.Obico.Enabled != newPrinterConfig.Obico.Enabled ||
                                       oldPrinterConfig.Obico.ServerUrl != newPrinterConfig.Obico.ServerUrl;

                    if (obicoChanged)
                    {
                        // Fire and forget - update local Obico config asynchronously
                        _ = thread.UpdateObicoConfigAsync(newPrinterConfig.Obico);
                    }

                    // Check if Obico Cloud config changed
                    var obicoCloudChanged = oldPrinterConfig == null ||
                                            oldPrinterConfig.ObicoCloud.AuthToken != newPrinterConfig.ObicoCloud.AuthToken ||
                                            oldPrinterConfig.ObicoCloud.Enabled != newPrinterConfig.ObicoCloud.Enabled;

                    if (obicoCloudChanged)
                    {
                        // Fire and forget - update cloud Obico config asynchronously
                        _ = thread.UpdateObicoCloudConfigAsync(newPrinterConfig.ObicoCloud);
                    }
                }
            }
        }
    }

    public async Task StartAllAsync()
    {
        Logger.Log($"Starting all printers ({_config.Printers.Count} configured)...");

        // Calculate CPU affinity assignments
        var cpuAssignments = CpuAffinityService.CalculateCpuAssignments(_config.Printers.Count);
        var availableCpus = CpuAffinityService.GetAvailableCpus();

        if (cpuAssignments.Length > 0)
        {
            Logger.Debug($"[PrinterManager] Available CPUs: {string.Join(", ", availableCpus)}");
            Logger.Debug($"[PrinterManager] CPU assignments: {CpuAffinityService.FormatAssignments(cpuAssignments)}");
        }

        for (int i = 0; i < _config.Printers.Count; i++)
        {
            var printerConfig = _config.Printers[i];
            var cpuAffinity = i < cpuAssignments.Length ? cpuAssignments[i] : -1;
            await StartPrinterAsync(printerConfig, cpuAffinity);
        }

        Logger.Log($"All printers started");
    }

    public async Task StopAllAsync()
    {
        List<PrinterThread> toStop;
        lock (_lock)
        {
            toStop = _printers.Values.ToList();
            _printers.Clear();
        }

        if (toStop.Count > 0)
        {
            Logger.Log($"Stopping all printers ({toStop.Count})...");
        }

        foreach (var printer in toStop)
        {
            printer.ConfigChanged -= OnPrinterConfigChanged;
            await printer.StopAsync();
            printer.Dispose();
        }

        if (toStop.Count > 0)
        {
            Logger.Log("All printers stopped");
        }
    }

    public async Task RestartAllAsync()
    {
        Logger.Log("Restarting all printers...");
        await StopAllAsync();
        await StartAllAsync();
        Logger.Log("All printers restarted");
    }

    private async Task StartPrinterAsync(PrinterConfig config, int cpuAffinity = -1)
    {
        // Don't start disabled printers
        if (!config.Enabled)
        {
            Logger.Log($"Printer '{config.Name}' is disabled, skipping...");
            return;
        }

        lock (_lock)
        {
            if (_printers.ContainsKey(config.Name))
            {
                return; // Already running
            }
        }

        var thread = new PrinterThread(config, _config.ListenInterfaces);

        // Set CPU affinity before starting
        if (cpuAffinity >= 0)
        {
            thread.SetCpuAffinity(cpuAffinity);
        }

        // Subscribe to config changes (device type detected, etc.)
        thread.ConfigChanged += OnPrinterConfigChanged;

        lock (_lock)
        {
            _printers[config.Name] = thread;
        }

        await thread.StartAsync();
    }

    /// <summary>
    /// Called when a printer thread detects config changes (e.g., device type).
    /// Persists the updated config to disk.
    /// </summary>
    private async void OnPrinterConfigChanged(object? sender, EventArgs e)
    {
        if (sender is PrinterThread thread)
        {
            // Update the config in our list
            var existingIndex = _config.Printers.FindIndex(p => p.Name == thread.Config.Name);
            if (existingIndex >= 0)
            {
                _config.Printers[existingIndex] = thread.Config;
            }

            // Save to disk
            try
            {
                await ConfigManager.SaveAsync(_config);
                Logger.Log($"[PrinterManager] Config saved (device type updated for {thread.Config.Name}: {thread.Config.DeviceType})");
            }
            catch (Exception ex)
            {
                Logger.Error($"[PrinterManager] Failed to save config: {ex.Message}");
            }
        }
    }

    public List<PrinterStatus> GetAllStatus()
    {
        var result = new List<PrinterStatus>();

        lock (_lock)
        {
            // Add status for running printers
            foreach (var printer in _printers.Values)
            {
                result.Add(printer.GetStatus());
            }
        }

        // Add status for disabled printers (not running)
        foreach (var config in _config.Printers)
        {
            if (!config.Enabled)
            {
                result.Add(CreateDisabledStatus(config));
            }
        }

        return result;
    }

    /// <summary>
    /// Create a PrinterStatus for a disabled printer (no running thread).
    /// </summary>
    private static PrinterStatus CreateDisabledStatus(PrinterConfig config)
    {
        return new PrinterStatus
        {
            Name = config.Name,
            Ip = config.Ip,
            MjpegPort = config.MjpegPort,
            DeviceType = config.DeviceType,
            State = PrinterState.Disabled,
            JpegQuality = config.JpegQuality,
            H264StreamerEnabled = config.H264StreamerEnabled,
            HlsEnabled = config.HlsEnabled,
            LlHlsEnabled = config.LlHlsEnabled,
            MjpegStreamerEnabled = config.MjpegStreamerEnabled,
            ObicoStatus = new ObicoStatus
            {
                Enabled = config.Obico.Enabled,
                IsLinked = config.Obico.IsLinked,
                State = "Disabled",
                ServerUrl = config.Obico.ServerUrl,
                ObicoName = config.Obico.ObicoName
            },
            ObicoCloudStatus = new ObicoStatus
            {
                Enabled = config.ObicoCloud.Enabled,
                IsLinked = config.ObicoCloud.IsLinked,
                State = "Disabled",
                ObicoName = config.ObicoCloud.ObicoName
            }
        };
    }

    public PrinterStatus? GetStatus(string name)
    {
        lock (_lock)
        {
            if (_printers.TryGetValue(name, out var printer))
            {
                return printer.GetStatus();
            }
        }

        // Check if it's a disabled printer
        var config = _config.Printers.FirstOrDefault(p => p.Name == name);
        if (config != null && !config.Enabled)
        {
            return CreateDisabledStatus(config);
        }

        return null;
    }

    public PrinterConfig? GetConfig(string name)
    {
        lock (_lock)
        {
            if (_printers.TryGetValue(name, out var printer))
            {
                return printer.Config;
            }
        }

        // Also check saved config
        return _config.Printers.FirstOrDefault(p => p.Name == name);
    }

    /// <summary>
    /// Get the PrinterThread for a specific printer by name.
    /// </summary>
    public PrinterThread? GetPrinterThread(string name)
    {
        lock (_lock)
        {
            return _printers.TryGetValue(name, out var printer) ? printer : null;
        }
    }

    public async Task<IpcResponse> AddPrinterAsync(PrinterConfig config)
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            return IpcResponse.Fail("Printer name is required");
        }

        if (string.IsNullOrWhiteSpace(config.Ip))
        {
            return IpcResponse.Fail("Printer IP is required");
        }

        // Validate unique name
        lock (_lock)
        {
            if (_printers.ContainsKey(config.Name))
            {
                return IpcResponse.Fail($"Printer with name '{config.Name}' already exists");
            }
        }

        // Also check config file
        if (_config.Printers.Any(p => p.Name == config.Name))
        {
            return IpcResponse.Fail($"Printer with name '{config.Name}' already exists");
        }

        // Validate unique port
        lock (_lock)
        {
            if (_printers.Values.Any(p => p.Config.MjpegPort == config.MjpegPort))
            {
                return IpcResponse.Fail($"MJPEG port {config.MjpegPort} is already in use");
            }
        }

        // Also check config file
        if (_config.Printers.Any(p => p.MjpegPort == config.MjpegPort))
        {
            return IpcResponse.Fail($"MJPEG port {config.MjpegPort} is already in use");
        }

        // Validate port is available on the system
        if (!IsPortAvailable(config.MjpegPort))
        {
            return IpcResponse.Fail($"MJPEG port {config.MjpegPort} is not available on the system");
        }

        // Add to config and save
        _config.Printers.Add(config);
        await ConfigManager.SaveAsync(_config);

        // Only start if enabled
        if (config.Enabled)
        {
            // Calculate CPU affinity for the new printer
            // New printers get assigned based on total count
            var cpuAssignments = CpuAffinityService.CalculateCpuAssignments(_config.Printers.Count);
            var cpuAffinity = cpuAssignments.Length > 0 ? cpuAssignments[_config.Printers.Count - 1] : -1;

            // Start the printer thread
            await StartPrinterAsync(config, cpuAffinity);
        }

        return IpcResponse.Ok();
    }

    public async Task<IpcResponse> DeletePrinterAsync(string name)
    {
        PrinterThread? thread;
        lock (_lock)
        {
            _printers.TryGetValue(name, out thread);
        }

        // Stop the thread if running
        if (thread != null)
        {
            thread.ConfigChanged -= OnPrinterConfigChanged;
            await thread.StopAsync();
            thread.Dispose();

            lock (_lock)
            {
                _printers.Remove(name);
            }
        }

        // Remove from config
        var removed = _config.Printers.RemoveAll(p => p.Name == name) > 0;
        if (removed)
        {
            await ConfigManager.SaveAsync(_config);
            return IpcResponse.Ok();
        }

        if (thread == null)
        {
            return IpcResponse.Fail($"Printer '{name}' not found");
        }

        return IpcResponse.Ok();
    }

    public async Task<IpcResponse> ModifyPrinterAsync(string originalName, PrinterConfig newConfig)
    {
        // Find existing printer by original name
        PrinterThread? existingThread;
        PrinterConfig? existingConfig;

        lock (_lock)
        {
            _printers.TryGetValue(originalName, out existingThread);
        }

        existingConfig = _config.Printers.FirstOrDefault(p => p.Name == originalName);

        if (existingThread == null && existingConfig == null)
        {
            return IpcResponse.Fail($"Printer '{originalName}' not found");
        }

        // Check if name is being changed and validate new name is unique
        if (originalName != newConfig.Name)
        {
            lock (_lock)
            {
                if (_printers.ContainsKey(newConfig.Name))
                {
                    return IpcResponse.Fail($"Printer with name '{newConfig.Name}' already exists");
                }
            }

            if (_config.Printers.Any(p => p.Name == newConfig.Name))
            {
                return IpcResponse.Fail($"Printer with name '{newConfig.Name}' already exists");
            }
        }

        // Check if port is changing and validate new port
        var oldPort = existingConfig?.MjpegPort ?? existingThread?.Config.MjpegPort ?? 0;
        if (newConfig.MjpegPort != oldPort)
        {
            // Check if new port is already in use by another printer
            lock (_lock)
            {
                if (_printers.Values.Any(p => p.Config.Name != originalName && p.Config.MjpegPort == newConfig.MjpegPort))
                {
                    return IpcResponse.Fail($"MJPEG port {newConfig.MjpegPort} is already in use");
                }
            }

            if (_config.Printers.Any(p => p.Name != originalName && p.MjpegPort == newConfig.MjpegPort))
            {
                return IpcResponse.Fail($"MJPEG port {newConfig.MjpegPort} is already in use");
            }

            if (!IsPortAvailable(newConfig.MjpegPort))
            {
                return IpcResponse.Fail($"MJPEG port {newConfig.MjpegPort} is not available on the system");
            }
        }

        // Stop existing thread if running
        if (existingThread != null)
        {
            existingThread.ConfigChanged -= OnPrinterConfigChanged;
            await existingThread.StopAsync();
            existingThread.Dispose();

            lock (_lock)
            {
                _printers.Remove(originalName);
            }
        }

        // Update config (remove by original name, add with new config)
        _config.Printers.RemoveAll(p => p.Name == originalName);
        _config.Printers.Add(newConfig);
        await ConfigManager.SaveAsync(_config);

        // Only start if enabled
        if (newConfig.Enabled)
        {
            // Calculate CPU affinity for the restarted printer
            var printerIndex = _config.Printers.FindIndex(p => p.Name == newConfig.Name);
            var cpuAssignments = CpuAffinityService.CalculateCpuAssignments(_config.Printers.Count);
            var cpuAffinity = printerIndex >= 0 && printerIndex < cpuAssignments.Length
                ? cpuAssignments[printerIndex]
                : -1;

            // Start with new config
            await StartPrinterAsync(newConfig, cpuAffinity);
        }

        return IpcResponse.Ok();
    }

    public async Task<IpcResponse> PausePrinterAsync(string name)
    {
        PrinterThread? printer;
        lock (_lock)
        {
            if (!_printers.TryGetValue(name, out printer))
            {
                return IpcResponse.Fail($"Printer '{name}' not found");
            }
        }

        await printer.PauseAsync();
        return IpcResponse.Ok();
    }

    public async Task<IpcResponse> ResumePrinterAsync(string name)
    {
        PrinterThread? printer;
        lock (_lock)
        {
            if (!_printers.TryGetValue(name, out printer))
            {
                return IpcResponse.Fail($"Printer '{name}' not found");
            }
        }

        await printer.ResumeAsync();
        return IpcResponse.Ok();
    }

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
}
