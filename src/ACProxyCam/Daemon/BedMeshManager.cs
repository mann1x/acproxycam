// BedMeshManager.cs - Manages BedMesh calibration lifecycle

using ACProxyCam.Models;
using ACProxyCam.Services;

namespace ACProxyCam.Daemon;

/// <summary>
/// Manages BedMesh calibration operations, integrating with PrinterManager for LED control and config access.
/// </summary>
public class BedMeshManager : IDisposable
{
    private readonly BedMeshService _service;
    private readonly PrinterManager _printerManager;
    private bool _disposed;

    public BedMeshManager(PrinterManager printerManager)
    {
        _printerManager = printerManager;
        _service = new BedMeshService();
        _service.StatusChanged += (s, msg) => Logger.Log($"BedMesh: {msg}");
        _service.SessionCompleted += OnSessionCompleted;
    }

    /// <summary>
    /// Start the BedMesh monitoring service and recover any running sessions.
    /// </summary>
    public async Task StartAsync()
    {
        // Recover any running sessions
        var recovered = await _service.RecoverSessionsAsync();

        foreach (var (deviceId, session) in recovered)
        {
            // Find the printer config by deviceId
            var printers = _printerManager.GetAllStatus();
            var matchingPrinter = printers.FirstOrDefault(p =>
            {
                var config = _printerManager.GetConfig(p.Name);
                return config?.DeviceId == deviceId;
            });

            if (matchingPrinter != null)
            {
                var config = _printerManager.GetConfig(matchingPrinter.Name);
                if (config != null)
                {
                    _service.AddRecoveredSession(config, session);
                    Logger.Log($"BedMesh: Resumed monitoring for {session.PrinterName}");
                }
            }
            else
            {
                Logger.Log($"BedMesh: Warning - could not find printer for recovered session (deviceId: {deviceId})");
            }
        }

        // Start monitoring loop
        _service.StartMonitoring();
        Logger.Log("BedMesh service started");
    }

    /// <summary>
    /// Stop the BedMesh monitoring service.
    /// </summary>
    public async Task StopAsync()
    {
        await _service.StopMonitoringAsync();
        Logger.Log("BedMesh service stopped");
    }

    /// <summary>
    /// Start a calibration for a printer.
    /// </summary>
    public async Task<IpcResponse> StartCalibrationAsync(string printerName, int heatSoakMinutes, string? name = null)
    {
        // Get printer config
        var config = _printerManager.GetConfig(printerName);
        if (config == null)
            return IpcResponse.Fail($"Printer '{printerName}' not found");

        if (string.IsNullOrEmpty(config.DeviceId))
            return IpcResponse.Fail($"Printer '{printerName}' has no deviceId - connect at least once first");

        // Get the printer thread for LED control
        var printerThread = _printerManager.GetPrinterThread(printerName);

        // Create LED turn-on callback
        Func<Task<bool>>? turnLedOn = null;
        if (printerThread != null)
        {
            turnLedOn = async () =>
            {
                try
                {
                    return await printerThread.SetLedAsync(true);
                }
                catch
                {
                    return false;
                }
            };
        }

        var (success, error) = await _service.StartCalibrationAsync(config, heatSoakMinutes, turnLedOn, name);

        if (success)
            return IpcResponse.Ok();
        else
            return IpcResponse.Fail(error ?? "Unknown error");
    }

    /// <summary>
    /// Get session summary (active and saved sessions).
    /// </summary>
    public async Task<IpcResponse> GetSessionsAsync()
    {
        var summary = await _service.GetSessionSummaryAsync();
        return IpcResponse.Ok(summary);
    }

    /// <summary>
    /// Get a saved calibration by filename.
    /// </summary>
    public async Task<IpcResponse> GetCalibrationAsync(string fileName)
    {
        var session = await _service.LoadCalibrationAsync(fileName);
        if (session == null)
            return IpcResponse.Fail("Calibration not found");
        return IpcResponse.Ok(session);
    }

    /// <summary>
    /// Delete a saved calibration.
    /// </summary>
    public IpcResponse DeleteCalibration(string fileName)
    {
        var success = _service.DeleteCalibration(fileName);
        if (success)
            return IpcResponse.Ok();
        else
            return IpcResponse.Fail("Failed to delete calibration");
    }

    /// <summary>
    /// Get active sessions (for display).
    /// </summary>
    public List<BedMeshSession> GetActiveSessions()
    {
        return _service.GetActiveSessions();
    }

    private void OnSessionCompleted(object? sender, BedMeshSession session)
    {
        var status = session.Status == CalibrationStatus.Success ? "succeeded" : "failed";
        Logger.Log($"BedMesh calibration {status} for {session.PrinterName}");

        if (session.Status == CalibrationStatus.Success && session.MeshData?.Stats != null)
        {
            Logger.Log($"  Mesh: {session.MeshData.XCount}x{session.MeshData.YCount}, Range: {MeshStats.FormatMm(session.MeshData.Stats.Range)}");
        }
        else if (!string.IsNullOrEmpty(session.ErrorMessage))
        {
            Logger.Log($"  Error: {session.ErrorMessage}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _service.Dispose();
        GC.SuppressFinalize(this);
    }
}
