// IpcMessages.cs - IPC protocol messages between CLI and daemon

using System.Text.Json.Serialization;

namespace ACProxyCam.Models;

/// <summary>
/// Base class for IPC requests.
/// </summary>
public class IpcRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// Base class for IPC responses.
/// </summary>
public class IpcResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    public static IpcResponse Ok(object? data = null) => new() { Success = true, Data = data };
    public static IpcResponse Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Daemon status response.
/// </summary>
public class DaemonStatusData
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("uptime")]
    public TimeSpan Uptime { get; set; }

    [JsonPropertyName("printerCount")]
    public int PrinterCount { get; set; }

    [JsonPropertyName("activeStreamers")]
    public int ActiveStreamers { get; set; }

    [JsonPropertyName("inactiveStreamers")]
    public int InactiveStreamers { get; set; }

    [JsonPropertyName("totalClients")]
    public int TotalClients { get; set; }

    [JsonPropertyName("listenInterfaces")]
    public List<string> ListenInterfaces { get; set; } = new();
}

/// <summary>
/// Request to add a printer.
/// </summary>
public class AddPrinterRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = "";

    [JsonPropertyName("mjpegPort")]
    public int MjpegPort { get; set; } = 8080;

    [JsonPropertyName("sshPort")]
    public int SshPort { get; set; } = 22;

    [JsonPropertyName("sshUser")]
    public string SshUser { get; set; } = "root";

    [JsonPropertyName("sshPassword")]
    public string SshPassword { get; set; } = "rockchip";

    [JsonPropertyName("mqttPort")]
    public int MqttPort { get; set; } = 9883;
}

/// <summary>
/// Request to modify a printer (supports rename via originalName).
/// </summary>
public class ModifyPrinterRequest
{
    [JsonPropertyName("originalName")]
    public string OriginalName { get; set; } = "";

    [JsonPropertyName("config")]
    public PrinterConfig Config { get; set; } = new();
}

/// <summary>
/// Request targeting a specific printer by name.
/// </summary>
public class PrinterNameRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

/// <summary>
/// Request to change listening interfaces.
/// </summary>
public class ChangeInterfacesRequest
{
    [JsonPropertyName("interfaces")]
    public List<string> Interfaces { get; set; } = new();
}

/// <summary>
/// IPC command names.
/// </summary>
public static class IpcCommands
{
    public const string GetStatus = "get_status";
    public const string ListPrinters = "list_printers";
    public const string GetPrinterDetails = "get_printer_details";
    public const string GetPrinterConfig = "get_printer_config";
    public const string AddPrinter = "add_printer";
    public const string DeletePrinter = "delete_printer";
    public const string ModifyPrinter = "modify_printer";
    public const string PausePrinter = "pause_printer";
    public const string ResumePrinter = "resume_printer";
    public const string ReloadConfig = "reload_config";
    public const string ChangeInterfaces = "change_interfaces";
    public const string StopService = "stop_service";
}
