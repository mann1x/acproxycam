// IpcClient.cs - Unix socket IPC client

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ACProxyCam.Daemon;
using ACProxyCam.Models;

namespace ACProxyCam.Client;

/// <summary>
/// Client for communicating with the daemon via Unix socket.
/// </summary>
public class IpcClient : IDisposable
{
    private Socket? _socket;

    /// <summary>
    /// Check if the daemon is running by attempting to connect.
    /// </summary>
    public static bool IsDaemonRunning()
    {
        if (!File.Exists(IpcServer.SocketPath))
            return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(IpcServer.SocketPath));
            socket.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Connect to the daemon.
    /// </summary>
    public bool Connect()
    {
        try
        {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _socket.Connect(new UnixDomainSocketEndPoint(IpcServer.SocketPath));
            return true;
        }
        catch
        {
            _socket?.Dispose();
            _socket = null;
            return false;
        }
    }

    /// <summary>
    /// Disconnect from the daemon.
    /// </summary>
    public void Disconnect()
    {
        try
        {
            if (_socket != null && _socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
        }
        catch { /* Ignore shutdown errors */ }

        try
        {
            _socket?.Close();
        }
        catch { /* Ignore close errors */ }

        _socket?.Dispose();
        _socket = null;
    }

    /// <summary>
    /// Send a request and receive a response.
    /// Each request uses a new connection since the server closes after each request.
    /// </summary>
    public async Task<IpcResponse> SendAsync(string command, object? data = null)
    {
        try
        {
            // Create a new connection for each request
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(IpcServer.SocketPath));

            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var request = new IpcRequest { Command = command, Data = data };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));

            var responseLine = await reader.ReadLineAsync();
            if (string.IsNullOrEmpty(responseLine))
            {
                return IpcResponse.Fail("Empty response from daemon");
            }

            return JsonSerializer.Deserialize<IpcResponse>(responseLine) ?? IpcResponse.Fail("Invalid response");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail($"IPC error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get daemon status.
    /// </summary>
    public async Task<(bool Success, DaemonStatusData? Data, string? Error)> GetStatusAsync()
    {
        var response = await SendAsync(IpcCommands.GetStatus);
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<DaemonStatusData>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Get list of all printers with status.
    /// </summary>
    public async Task<(bool Success, List<PrinterStatus>? Data, string? Error)> ListPrintersAsync()
    {
        var response = await SendAsync(IpcCommands.ListPrinters);
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<List<PrinterStatus>>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Get detailed status for a printer.
    /// </summary>
    public async Task<(bool Success, PrinterStatus? Data, string? Error)> GetPrinterStatusAsync(string name)
    {
        var response = await SendAsync(IpcCommands.GetPrinterDetails, new PrinterNameRequest { Name = name });
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<PrinterStatus>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Get printer configuration.
    /// </summary>
    public async Task<(bool Success, PrinterConfig? Data, string? Error)> GetPrinterConfigAsync(string name)
    {
        var response = await SendAsync(IpcCommands.GetPrinterConfig, new PrinterNameRequest { Name = name });
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<PrinterConfig>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Add a new printer.
    /// </summary>
    public async Task<(bool Success, string? Error)> AddPrinterAsync(PrinterConfig config)
    {
        var response = await SendAsync(IpcCommands.AddPrinter, config);
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Delete a printer.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeletePrinterAsync(string name)
    {
        var response = await SendAsync(IpcCommands.DeletePrinter, new PrinterNameRequest { Name = name });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Modify a printer.
    /// </summary>
    public async Task<(bool Success, string? Error)> ModifyPrinterAsync(string originalName, PrinterConfig config)
    {
        var request = new ModifyPrinterRequest
        {
            OriginalName = originalName,
            Config = config
        };
        var response = await SendAsync(IpcCommands.ModifyPrinter, request);
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Pause a printer.
    /// </summary>
    public async Task<(bool Success, string? Error)> PausePrinterAsync(string name)
    {
        var response = await SendAsync(IpcCommands.PausePrinter, new PrinterNameRequest { Name = name });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Resume a printer.
    /// </summary>
    public async Task<(bool Success, string? Error)> ResumePrinterAsync(string name)
    {
        var response = await SendAsync(IpcCommands.ResumePrinter, new PrinterNameRequest { Name = name });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Change listening interfaces.
    /// </summary>
    public async Task<(bool Success, string? Error)> SetListenInterfacesAsync(List<string> interfaces)
    {
        var response = await SendAsync(IpcCommands.ChangeInterfaces, new ChangeInterfacesRequest { Interfaces = interfaces });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Stop the service.
    /// </summary>
    public async Task<(bool Success, string? Error)> StopServiceAsync()
    {
        var response = await SendAsync(IpcCommands.StopService);
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Reload configuration (daemon will reload config and restart printers as needed).
    /// </summary>
    public async Task<(bool Success, string? Error)> ReloadConfigAsync()
    {
        var response = await SendAsync(IpcCommands.ReloadConfig);
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Get LED status for a printer.
    /// </summary>
    public async Task<(bool Success, LedStatus? Data, string? Error)> GetLedStatusAsync(string name)
    {
        var response = await SendAsync(IpcCommands.GetLedStatus, new PrinterNameRequest { Name = name });
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<LedStatus>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Set LED state for a printer.
    /// </summary>
    public async Task<(bool Success, string? Error)> SetLedAsync(string name, bool on)
    {
        var response = await SendAsync(IpcCommands.SetLed, new SetLedRequest { Name = name, On = on });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Get BedMesh session summary.
    /// </summary>
    public async Task<(bool Success, BedMeshSessionSummary? Data, string? Error)> GetBedMeshSessionsAsync()
    {
        var response = await SendAsync(IpcCommands.GetBedMeshSessions);
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<BedMeshSessionSummary>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Start a BedMesh calibration.
    /// </summary>
    public async Task<(bool Success, string? Error)> StartCalibrationAsync(string printerName, int heatSoakMinutes, string? name = null)
    {
        var response = await SendAsync(IpcCommands.StartCalibration, new StartCalibrationRequest
        {
            PrinterName = printerName,
            HeatSoakMinutes = heatSoakMinutes,
            Name = name
        });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Get a saved calibration by filename.
    /// </summary>
    public async Task<(bool Success, BedMeshSession? Data, string? Error)> GetCalibrationAsync(string fileName)
    {
        var response = await SendAsync(IpcCommands.GetCalibration, new CalibrationFileRequest { FileName = fileName });
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<BedMeshSession>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Delete a saved calibration.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteCalibrationAsync(string fileName)
    {
        var response = await SendAsync(IpcCommands.DeleteCalibration, new CalibrationFileRequest { FileName = fileName });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Start a BedMesh analysis (multiple calibrations).
    /// </summary>
    public async Task<(bool Success, string? Error)> StartAnalysisAsync(string printerName, int heatSoakMinutes, int calibrationCount, string? name = null)
    {
        var response = await SendAsync(IpcCommands.StartAnalysis, new StartAnalysisRequest
        {
            PrinterName = printerName,
            HeatSoakMinutes = heatSoakMinutes,
            CalibrationCount = calibrationCount,
            Name = name
        });
        return (response.Success, response.Error);
    }

    /// <summary>
    /// Get a saved analysis by filename.
    /// </summary>
    public async Task<(bool Success, AnalysisSession? Data, string? Error)> GetAnalysisAsync(string fileName)
    {
        var response = await SendAsync(IpcCommands.GetAnalysis, new AnalysisFileRequest { FileName = fileName });
        if (!response.Success)
            return (false, null, response.Error);

        var data = DeserializeData<AnalysisSession>(response.Data);
        return (true, data, null);
    }

    /// <summary>
    /// Delete a saved analysis.
    /// </summary>
    public async Task<(bool Success, string? Error)> DeleteAnalysisAsync(string fileName)
    {
        var response = await SendAsync(IpcCommands.DeleteAnalysis, new AnalysisFileRequest { FileName = fileName });
        return (response.Success, response.Error);
    }

    private T? DeserializeData<T>(object? data) where T : class
    {
        if (data == null) return null;
        if (data is JsonElement element)
        {
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        }
        return data as T;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
