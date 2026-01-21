// IpcServer.cs - Unix socket IPC server

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ACProxyCam.Models;

namespace ACProxyCam.Daemon;

/// <summary>
/// Unix socket server for IPC communication with the management CLI.
/// </summary>
public class IpcServer
{
    public const string SocketPath = "/run/acproxycam/acproxycam.sock";

    private readonly DaemonService _daemon;
    private readonly PrinterManager _printerManager;
    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public IpcServer(DaemonService daemon, PrinterManager printerManager)
    {
        _daemon = daemon;
        _printerManager = printerManager;
    }

    public async Task StartAsync()
    {
        // Remove stale socket file
        if (File.Exists(SocketPath))
        {
            File.Delete(SocketPath);
        }

        // Ensure directory exists
        var dir = Path.GetDirectoryName(SocketPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _cts = new CancellationTokenSource();
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        var endpoint = new UnixDomainSocketEndPoint(SocketPath);
        _socket.Bind(endpoint);
        _socket.Listen(10);

        // Set socket permissions (rw for owner and group)
        // chmod 660
        File.SetUnixFileMode(SocketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        _acceptTask = AcceptClientsAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _socket?.Close();

        try
        {
            if (File.Exists(SocketPath))
            {
                File.Delete(SocketPath);
            }
        }
        catch { }
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _socket!.AcceptAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"IPC accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line))
                return;

            var request = JsonSerializer.Deserialize<IpcRequest>(line);
            if (request == null)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(IpcResponse.Fail("Invalid request")));
                return;
            }

            var response = await ProcessRequestAsync(request);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IPC client error: {ex.Message}");
        }
    }

    private async Task<IpcResponse> ProcessRequestAsync(IpcRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommands.GetStatus:
                    return IpcResponse.Ok(_daemon.GetStatus());

                case IpcCommands.ListPrinters:
                    return IpcResponse.Ok(_printerManager.GetAllStatus());

                case IpcCommands.GetPrinterDetails:
                    var detailsReq = DeserializeData<PrinterNameRequest>(request.Data);
                    if (detailsReq == null) return IpcResponse.Fail("Invalid request data");
                    var details = _printerManager.GetStatus(detailsReq.Name);
                    return details != null ? IpcResponse.Ok(details) : IpcResponse.Fail("Printer not found");

                case IpcCommands.GetPrinterConfig:
                    var configReq = DeserializeData<PrinterNameRequest>(request.Data);
                    if (configReq == null) return IpcResponse.Fail("Invalid request data");
                    var config = _printerManager.GetConfig(configReq.Name);
                    return config != null ? IpcResponse.Ok(config) : IpcResponse.Fail("Printer not found");

                case IpcCommands.AddPrinter:
                    var addReq = DeserializeData<PrinterConfig>(request.Data);
                    if (addReq == null) return IpcResponse.Fail("Invalid request data");
                    return await _printerManager.AddPrinterAsync(addReq);

                case IpcCommands.DeletePrinter:
                    var deleteReq = DeserializeData<PrinterNameRequest>(request.Data);
                    if (deleteReq == null) return IpcResponse.Fail("Invalid request data");
                    return await _printerManager.DeletePrinterAsync(deleteReq.Name);

                case IpcCommands.ModifyPrinter:
                    var modifyReq = DeserializeData<ModifyPrinterRequest>(request.Data);
                    if (modifyReq == null) return IpcResponse.Fail("Invalid request data");
                    return await _printerManager.ModifyPrinterAsync(modifyReq.OriginalName, modifyReq.Config);

                case IpcCommands.PausePrinter:
                    var pauseReq = DeserializeData<PrinterNameRequest>(request.Data);
                    if (pauseReq == null) return IpcResponse.Fail("Invalid request data");
                    return await _printerManager.PausePrinterAsync(pauseReq.Name);

                case IpcCommands.ResumePrinter:
                    var resumeReq = DeserializeData<PrinterNameRequest>(request.Data);
                    if (resumeReq == null) return IpcResponse.Fail("Invalid request data");
                    return await _printerManager.ResumePrinterAsync(resumeReq.Name);

                case IpcCommands.ReloadConfig:
                    Logger.Log("IPC: Received reload config request");
                    await _daemon.ReloadConfigAsync();
                    return IpcResponse.Ok();

                case IpcCommands.ChangeInterfaces:
                    var ifaceReq = DeserializeData<ChangeInterfacesRequest>(request.Data);
                    if (ifaceReq == null) return IpcResponse.Fail("Invalid request data");
                    Logger.Log($"IPC: Received change interfaces request: [{string.Join(", ", ifaceReq.Interfaces)}]");
                    await _daemon.ChangeInterfacesAsync(ifaceReq.Interfaces);
                    return IpcResponse.Ok();

                case IpcCommands.StopService:
                    _daemon.Stop();
                    return IpcResponse.Ok();

                case IpcCommands.GetLedStatus:
                    var ledStatusReq = DeserializeData<PrinterNameRequest>(request.Data);
                    if (ledStatusReq == null) return IpcResponse.Fail("Invalid request data");
                    return await GetLedStatusAsync(ledStatusReq.Name);

                case IpcCommands.SetLed:
                    var setLedReq = DeserializeData<SetLedRequest>(request.Data);
                    if (setLedReq == null) return IpcResponse.Fail("Invalid request data");
                    return await SetLedAsync(setLedReq.Name, setLedReq.On);

                default:
                    return IpcResponse.Fail($"Unknown command: {request.Command}");
            }
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
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

    private async Task<IpcResponse> GetLedStatusAsync(string printerName)
    {
        var thread = _printerManager.GetPrinterThread(printerName);
        if (thread == null)
            return IpcResponse.Fail("Printer not found");

        var ledStatus = await thread.GetLedStatusAsync();
        return IpcResponse.Ok(ledStatus);
    }

    private async Task<IpcResponse> SetLedAsync(string printerName, bool on)
    {
        var thread = _printerManager.GetPrinterThread(printerName);
        if (thread == null)
            return IpcResponse.Fail("Printer not found");

        var success = await thread.SetLedAsync(on);
        if (success)
            return IpcResponse.Ok(new { state = on ? "on" : "off" });
        else
            return IpcResponse.Fail("Failed to set LED");
    }
}
