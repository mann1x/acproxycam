// PrinterManager.cs - Manages all printer threads

using ACProxyCam.Models;

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
        _config = config;
    }

    public async Task StartAllAsync()
    {
        foreach (var printerConfig in _config.Printers)
        {
            await StartPrinterAsync(printerConfig);
        }
    }

    public async Task StopAllAsync()
    {
        List<PrinterThread> toStop;
        lock (_lock)
        {
            toStop = _printers.Values.ToList();
        }

        foreach (var printer in toStop)
        {
            await printer.StopAsync();
        }
    }

    public async Task RestartAllAsync()
    {
        await StopAllAsync();
        await StartAllAsync();
    }

    private async Task StartPrinterAsync(PrinterConfig config)
    {
        lock (_lock)
        {
            if (_printers.ContainsKey(config.Name))
            {
                return; // Already running
            }
        }

        var thread = new PrinterThread(config, _config.ListenInterfaces);
        lock (_lock)
        {
            _printers[config.Name] = thread;
        }

        await thread.StartAsync();
    }

    public List<PrinterStatus> GetAllStatus()
    {
        lock (_lock)
        {
            return _printers.Values.Select(p => p.GetStatus()).ToList();
        }
    }

    public PrinterStatus? GetStatus(string name)
    {
        lock (_lock)
        {
            return _printers.TryGetValue(name, out var printer) ? printer.GetStatus() : null;
        }
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

        // Start the printer thread
        await StartPrinterAsync(config);

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

    public async Task<IpcResponse> ModifyPrinterAsync(PrinterConfig newConfig)
    {
        // Find existing printer by name
        PrinterThread? existingThread;
        PrinterConfig? existingConfig;

        lock (_lock)
        {
            _printers.TryGetValue(newConfig.Name, out existingThread);
        }

        existingConfig = _config.Printers.FirstOrDefault(p => p.Name == newConfig.Name);

        if (existingThread == null && existingConfig == null)
        {
            return IpcResponse.Fail($"Printer '{newConfig.Name}' not found");
        }

        // Check if port is changing and validate new port
        var oldPort = existingConfig?.MjpegPort ?? existingThread?.Config.MjpegPort ?? 0;
        if (newConfig.MjpegPort != oldPort)
        {
            // Check if new port is already in use by another printer
            lock (_lock)
            {
                if (_printers.Values.Any(p => p.Config.Name != newConfig.Name && p.Config.MjpegPort == newConfig.MjpegPort))
                {
                    return IpcResponse.Fail($"MJPEG port {newConfig.MjpegPort} is already in use");
                }
            }

            if (_config.Printers.Any(p => p.Name != newConfig.Name && p.MjpegPort == newConfig.MjpegPort))
            {
                return IpcResponse.Fail($"MJPEG port {newConfig.MjpegPort} is already in use");
            }

            if (!IsPortAvailable(newConfig.MjpegPort))
            {
                return IpcResponse.Fail($"MJPEG port {newConfig.MjpegPort} is not available on the system");
            }
        }

        // Stop existing thread
        if (existingThread != null)
        {
            await existingThread.StopAsync();
            existingThread.Dispose();

            lock (_lock)
            {
                _printers.Remove(newConfig.Name);
            }
        }

        // Update config
        _config.Printers.RemoveAll(p => p.Name == newConfig.Name);
        _config.Printers.Add(newConfig);
        await ConfigManager.SaveAsync(_config);

        // Start with new config
        await StartPrinterAsync(newConfig);

        return IpcResponse.Ok();
    }

    public IpcResponse PausePrinter(string name)
    {
        lock (_lock)
        {
            if (!_printers.TryGetValue(name, out var printer))
            {
                return IpcResponse.Fail($"Printer '{name}' not found");
            }

            printer.Pause();
            return IpcResponse.Ok();
        }
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
