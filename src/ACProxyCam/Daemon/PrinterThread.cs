// PrinterThread.cs - Single printer worker thread

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ACProxyCam.Models;
using ACProxyCam.Services;
using ACProxyCam.Services.Obico;

namespace ACProxyCam.Daemon;

/// <summary>
/// Worker thread for a single printer.
/// Handles SSH, MQTT, FFmpeg decoding, and MJPEG streaming.
/// Implements state machine with watchdog and retry logic.
/// </summary>
public class PrinterThread : IDisposable
{
    public PrinterConfig Config { get; private set; }
    private readonly List<string> _listenInterfaces;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private bool _disposed;

    private volatile PrinterState _state = PrinterState.Stopped;
    private volatile bool _isPaused;
    private string? _lastError;
    private DateTime? _lastErrorAt;
    private DateTime? _lastSeenOnline;
    private DateTime? _nextRetryAt;

    // CPU affinity for this thread (-1 = no affinity set)
    private int _cpuAffinity = -1;
    public int CpuAffinity => _cpuAffinity;

    // Services
    private SshCredentialService? _sshService;
    private LanModeService? _lanModeService;
    private MqttCameraController? _mqttController;
    private FfmpegDecoder? _decoder;
    private MjpegServer? _mjpegServer;
    private ObicoClient? _obicoClient;

    // Status details
    private readonly SshStatus _sshStatus = new();
    private readonly MqttStatus _mqttStatus = new();
    private readonly StreamStatus _streamStatus = new();
    private readonly Models.ObicoStatus _obicoStatus = new();

    // LED auto-control state
    private string? _printerMqttState;
    private LedStatus? _cachedLedStatus;
    private DateTime? _ledOnIdleSince;
    private readonly object _ledStateLock = new();

    // Log throttling
    private readonly LogThrottler _logThrottler;

    // Stream recovery settings
    private const int StreamRecoveryDelayMs = 2000;      // 2 seconds between recovery attempts
    private const int QuickRecoveryPeriodMinutes = 5;    // First 5 minutes: quick retry
    private const int StreamStabilizationSeconds = 3;    // Wait 3 seconds before considering stream stable
    private const int LanModeRetryThresholdSeconds = 30; // Try LAN mode after 30s of failed recovery
    private DateTime _streamFailedAt = DateTime.MinValue; // When the stream first failed
    private DateTime _streamStartedAt = DateTime.MinValue; // When the stream started (for stabilization)
    private DateTime _lastLanModeAttempt = DateTime.MinValue; // When we last tried enabling LAN mode
    private volatile bool _streamStalled;                 // Flag for stream stall detection

    // Watchdog settings
    private const int RetryDelayResponsive = 5;   // 5 seconds if host responds
    private const int RetryDelayOffline = 30;     // 30 seconds if host is offline
    private const int MqttDetectionTimeout = 10;  // 10 seconds to detect model code

    // Camera stream URL pattern
    private const string FlvUrlTemplate = "http://{0}:18088/flv";

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Raised when the printer configuration has changed (e.g., device type detected).
    /// Handler should persist the updated config.
    /// </summary>
    public event EventHandler? ConfigChanged;

    public PrinterThread(PrinterConfig config, List<string> listenInterfaces)
    {
        Config = config;
        _listenInterfaces = listenInterfaces;
        _logThrottler = new LogThrottler(msg =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            Console.WriteLine($"[{timestamp}] [{Config.Name}] {msg}");
            StatusChanged?.Invoke(this, msg);
        });
    }

    /// <summary>
    /// Update configuration for this printer thread.
    /// </summary>
    public void UpdateConfig(PrinterConfig config)
    {
        Config = config;
    }

    /// <summary>
    /// Set CPU affinity for this printer's worker thread.
    /// Call before StartAsync for best effect.
    /// </summary>
    public void SetCpuAffinity(int cpuNumber)
    {
        _cpuAffinity = cpuNumber;
    }

    public async Task StartAsync()
    {
        if (_workerTask != null && !_workerTask.IsCompleted)
            return;

        _isPaused = false;
        _cts = new CancellationTokenSource();
        _state = PrinterState.Initializing;
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_workerTask != null)
        {
            try
            {
                await _workerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Force stop - cleanup will happen
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        await CleanupServicesAsync();
        _state = PrinterState.Stopped;
    }

    public async Task PauseAsync()
    {
        _isPaused = true;
        _state = PrinterState.Paused;
        _cts?.Cancel();

        // Wait for worker to actually stop (with timeout)
        if (_workerTask != null && !_workerTask.IsCompleted)
        {
            try
            {
                await _workerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Worker didn't stop in time, state is already set to Paused
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
    }

    // Keep sync version for backward compatibility
    public void Pause()
    {
        _isPaused = true;
        _state = PrinterState.Paused;
        _cts?.Cancel();
    }

    public async Task ResumeAsync()
    {
        if (_isPaused)
        {
            _isPaused = false;
            await StartAsync();
        }
    }

    public PrinterStatus GetStatus()
    {
        lock (_ledStateLock)
        {
            return new PrinterStatus
            {
                Name = Config.Name,
                Ip = Config.Ip,
                MjpegPort = Config.MjpegPort,
                DeviceType = Config.DeviceType,
                State = _state,
                ConnectedClients = (_mjpegServer?.ConnectedClients ?? 0) + (_mjpegServer?.H264WebSocketClients ?? 0) + (_mjpegServer?.HasExternalStreamingClient == true ? 1 : 0),
                H264WebSocketClients = _mjpegServer?.H264WebSocketClients ?? 0,
                HlsReady = _mjpegServer?.HlsReady ?? false,
                IsPaused = _isPaused,
                CpuAffinity = _cpuAffinity,
                CurrentFps = ((_mjpegServer?.ConnectedClients ?? 0) > 0 || (_mjpegServer?.H264WebSocketClients ?? 0) > 0 || _mjpegServer?.HasExternalStreamingClient == true) ? Config.MaxFps : Config.IdleFps,
                IsIdle = (_mjpegServer?.ConnectedClients ?? 0) == 0 && (_mjpegServer?.H264WebSocketClients ?? 0) == 0 && _mjpegServer?.HasExternalStreamingClient != true,
                IsOnline = _state == PrinterState.Running,
                LastError = _lastError,
                LastErrorAt = _lastErrorAt,
                LastSeenOnline = _lastSeenOnline,
                NextRetryAt = _nextRetryAt,
                SshStatus = _sshStatus,
                MqttStatus = _mqttStatus,
                StreamStatus = _streamStatus,
                PrinterMqttState = _printerMqttState,
                CameraLed = _cachedLedStatus,
                ObicoStatus = _obicoStatus
            };
        }
    }

    /// <summary>
    /// Query the current LED status via MQTT.
    /// </summary>
    public async Task<LedStatus?> GetLedStatusAsync(CancellationToken ct = default)
    {
        if (_mqttController == null || !_mqttController.IsConnected ||
            string.IsNullOrEmpty(Config.DeviceId))
            return _cachedLedStatus;

        try
        {
            var status = await _mqttController.QueryLedStatusAsync(Config.DeviceId, Config.ModelCode, ct);
            if (status != null)
            {
                lock (_ledStateLock)
                {
                    _cachedLedStatus = status;
                }
            }
            return status ?? _cachedLedStatus;
        }
        catch
        {
            return _cachedLedStatus;
        }
    }

    /// <summary>
    /// Set the camera LED on or off via MQTT.
    /// </summary>
    public async Task<bool> SetLedAsync(bool on, CancellationToken ct = default)
    {
        if (_mqttController == null || !_mqttController.IsConnected ||
            string.IsNullOrEmpty(Config.DeviceId))
            return false;

        try
        {
            var success = await _mqttController.SetLedAsync(Config.DeviceId, on, 100, Config.ModelCode, ct);
            if (success)
            {
                lock (_ledStateLock)
                {
                    _cachedLedStatus = new LedStatus { Type = 2, IsOn = on, Brightness = on ? 100 : 0 };

                    // Reset idle tracking when LED is toggled
                    if (on && IsPrinterIdle())
                    {
                        _ledOnIdleSince = DateTime.UtcNow;
                    }
                    else
                    {
                        _ledOnIdleSince = null;
                    }
                }
                LogStatus($"LED set to {(on ? "ON" : "OFF")}");
            }
            return success;
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to set LED: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Query printer info (state, temps) via MQTT.
    /// </summary>
    public async Task<PrinterInfoResult?> GetPrinterInfoAsync(CancellationToken ct = default)
    {
        if (_mqttController == null || !_mqttController.IsConnected ||
            string.IsNullOrEmpty(Config.DeviceId))
            return null;

        try
        {
            var info = await _mqttController.QueryPrinterInfoAsync(Config.DeviceId, Config.ModelCode, ct);
            if (info != null)
            {
                lock (_ledStateLock)
                {
                    _printerMqttState = info.State;
                }
            }
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if the printer is in an idle state (free/standby).
    /// </summary>
    private bool IsPrinterIdle()
    {
        var state = _printerMqttState?.ToLowerInvariant();
        return state == null || state == "free" || state == "standby" || state == "ready";
    }

    /// <summary>
    /// Expose the MjpegServer for HTTP endpoint extensions (LED control).
    /// </summary>
    internal MjpegServer? MjpegServer => _mjpegServer;

    /// <summary>
    /// Expose the MqttController for direct LED/info queries from HTTP endpoints.
    /// </summary>
    internal MqttCameraController? MqttController => _mqttController;

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        // Apply CPU affinity if set
        if (_cpuAffinity >= 0)
        {
            if (Services.CpuAffinityService.SetThreadAffinity(_cpuAffinity))
            {
                LogStatus($"Thread pinned to CPU {_cpuAffinity}");
            }
            else
            {
                LogStatus($"Failed to pin thread to CPU {_cpuAffinity} (may not be supported on this platform)");
            }
        }

        while (!ct.IsCancellationRequested && !_isPaused)
        {
            try
            {
                _state = PrinterState.Connecting;
                _lastError = null;
                _nextRetryAt = null;

                // Only log on first attempt or after success - throttling handles repetitive failures
                LogStatusThrottled("connection", $"Connecting to {Config.Ip}...");

                // Step 1: Check deviceId and credentials
                // Always verify deviceId on first connection - if it changed, printer was swapped or factory reset
                var needFullRetrieval = string.IsNullOrEmpty(Config.MqttUsername) || string.IsNullOrEmpty(Config.MqttPassword);

                if (!needFullRetrieval)
                {
                    // Check if printer's deviceId matches our config
                    var printerChanged = await CheckPrinterChangedAsync(ct);
                    if (printerChanged)
                    {
                        LogStatus("Printer deviceId changed - re-discovering credentials and device info");
                        // Clear old credentials since they belong to a different printer
                        Config.MqttUsername = "";
                        Config.MqttPassword = "";
                        Config.DeviceType = "";
                        Config.ModelCode = "";
                        needFullRetrieval = true;
                    }
                }

                if (needFullRetrieval)
                {
                    await RetrieveSshCredentialsAsync(ct);
                }
                else if (string.IsNullOrEmpty(Config.DeviceType) || string.IsNullOrEmpty(Config.ModelCode))
                {
                    // Credentials exist and deviceId matches, but device info missing - fetch it
                    await RefreshPrinterInfoAsync(ct);
                }

                // Step 2: MQTT - Connect and detect model code if needed (for camera)
                if (Config.CameraEnabled)
                {
                    await ConnectMqttAndStartCameraAsync(ct);
                }

                // Step 3: Start MJPEG server (if camera enabled)
                if (Config.CameraEnabled)
                {
                    StartMjpegServer();
                }

                // Step 4: Start FFmpeg decoder and streaming loop (if camera enabled)
                // Obico client will be started after the stream is running (see DecodingStarted event)
                if (Config.CameraEnabled)
                {
                    await RunStreamingLoopAsync(ct);
                }
                else
                {
                    // Camera disabled - start Obico client directly (Obico-only mode)
                    _state = PrinterState.Running;
                    _lastSeenOnline = DateTime.UtcNow;
                    LogStatus("Running in Obico-only mode (camera disabled)");

                    await StartObicoClientAsync(ct);

                    while (!ct.IsCancellationRequested && !_isPaused)
                    {
                        await Task.Delay(1000, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = DateTime.UtcNow;
                _state = PrinterState.Retrying;

                await CleanupServicesAsync();

                // Determine retry delay based on host responsiveness
                var isResponding = await IsHostRespondingAsync();
                var retryDelay = TimeSpan.FromSeconds(isResponding ? RetryDelayResponsive : RetryDelayOffline);
                _nextRetryAt = DateTime.UtcNow + retryDelay;

                // Use throttled logging for repetitive connection errors - same key as connection start
                _logThrottler.Log("connection", $"Connection failed: {ex.Message}. Retry in {retryDelay.TotalSeconds}s");

                try
                {
                    await Task.Delay(retryDelay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        await CleanupServicesAsync();
        _state = _isPaused ? PrinterState.Paused : PrinterState.Stopped;
    }

    private async Task RetrieveSshCredentialsAsync(CancellationToken ct)
    {
        _sshStatus.LastAttempt = DateTime.UtcNow;
        _sshStatus.Connected = false;
        _sshStatus.CredentialsRetrieved = false;

        LogStatus("Retrieving credentials via SSH...");

        _sshService = new SshCredentialService();
        _sshService.StatusChanged += (s, msg) => LogStatusThrottled("ssh_status", $"SSH: {msg}");

        var result = await _sshService.RetrieveCredentialsAsync(
            Config.Ip,
            Config.SshPort,
            Config.SshUser,
            Config.SshPassword,
            ct);

        _sshStatus.Connected = true;

        if (!result.Success)
        {
            _sshStatus.Error = result.Error;
            throw new Exception($"SSH credential retrieval failed: {result.Error}");
        }

        _sshStatus.CredentialsRetrieved = true;

        // Update config with retrieved credentials (null-coalesce to empty if somehow null)
        Config.MqttUsername = result.MqttUsername ?? "";
        Config.MqttPassword = result.MqttPassword ?? "";

        if (!string.IsNullOrEmpty(result.DeviceId))
            Config.DeviceId = result.DeviceId;

        if (!string.IsNullOrEmpty(result.DeviceType))
            Config.DeviceType = result.DeviceType;

        if (!string.IsNullOrEmpty(result.ModelCode))
            Config.ModelCode = result.ModelCode;

        LogStatus($"SSH: Credentials retrieved. DeviceId: {Config.DeviceId ?? "unknown"}, DeviceType: {Config.DeviceType ?? "unknown"}, ModelCode: {Config.ModelCode ?? "unknown"}");

        // Notify that config has changed (for persistence)
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Check if the printer's deviceId has changed (different printer or factory reset).
    /// Returns true if the deviceId is different from our saved config.
    /// </summary>
    private async Task<bool> CheckPrinterChangedAsync(CancellationToken ct)
    {
        try
        {
            _sshService = new SshCredentialService();
            _sshService.StatusChanged += (s, msg) => LogStatusThrottled("ssh_status", $"SSH: {msg}");

            var printerInfo = await _sshService.RetrievePrinterInfoAsync(
                Config.Ip,
                Config.SshPort,
                Config.SshUser,
                Config.SshPassword,
                ct);

            if (printerInfo == null || string.IsNullOrEmpty(printerInfo.DeviceId))
            {
                // Couldn't get deviceId - assume no change
                return false;
            }

            // If we don't have a saved deviceId, it's not a "change" - just a first-time discovery
            if (string.IsNullOrEmpty(Config.DeviceId))
            {
                return false;
            }

            // Compare deviceIds
            return printerInfo.DeviceId != Config.DeviceId;
        }
        catch (Exception ex)
        {
            LogStatusThrottled("deviceid_check", $"Could not check printer deviceId: {ex.Message}");
            return false; // Assume no change on error
        }
    }

    /// <summary>
    /// Refresh printer info (device type, model code) from printer via SSH.
    /// Used when credentials already exist but device info might be missing.
    /// </summary>
    private async Task RefreshPrinterInfoAsync(CancellationToken ct)
    {
        try
        {
            _sshService = new SshCredentialService();
            _sshService.StatusChanged += (s, msg) => LogStatusThrottled("ssh_status", $"SSH: {msg}");

            var printerInfo = await _sshService.RetrievePrinterInfoAsync(
                Config.Ip,
                Config.SshPort,
                Config.SshUser,
                Config.SshPassword,
                ct);

            if (printerInfo == null)
                return;

            var configUpdated = false;

            if (!string.IsNullOrEmpty(printerInfo.DeviceType))
            {
                Config.DeviceType = printerInfo.DeviceType;
                LogStatus($"Device type: {printerInfo.DeviceType}");
                configUpdated = true;
            }

            if (!string.IsNullOrEmpty(printerInfo.ModelCode))
            {
                Config.ModelCode = printerInfo.ModelCode;
                LogStatus($"Model code: {printerInfo.ModelCode}");
                configUpdated = true;
            }

            // Persist device info if updated
            if (configUpdated)
            {
                ConfigChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            // Don't fail connection if printer info refresh fails
            LogStatusThrottled("printer_info", $"Could not refresh printer info: {ex.Message}");
        }
    }

    private async Task ConnectMqttAndStartCameraAsync(CancellationToken ct)
    {
        _mqttStatus.LastAttempt = DateTime.UtcNow;
        _mqttStatus.Connected = false;
        _mqttStatus.DeviceIdDetected = !string.IsNullOrEmpty(Config.DeviceId);
        _mqttStatus.ModelCodeDetected = !string.IsNullOrEmpty(Config.ModelCode);

        _mqttController = new MqttCameraController();
        _mqttController.StatusChanged += (s, msg) => { }; // Suppress - main loop handles logging
        _mqttController.ErrorOccurred += (s, ex) => { }; // Suppress - main loop handles logging
        _mqttController.ModelCodeDetected += (s, code) =>
        {
            Config.ModelCode = code;
            _mqttStatus.ModelCodeDetected = true;
            _mqttStatus.DetectedModelCode = code;
            LogStatus($"MQTT: Auto-detected model code: {code}");
        };

        _mqttController.MqttPort = Config.MqttPort;

        // Subscribe to camera stop detection - immediately restart when external stop is detected
        _mqttController.CameraStopDetected += OnExternalCameraStopDetected;

        // Subscribe to LED status updates from MQTT messages
        _mqttController.LedStatusReceived += OnLedStatusReceived;

        // Subscribe to printer state updates from MQTT messages
        _mqttController.PrinterStateReceived += OnPrinterStateReceived;


        // When AutoLanMode is enabled, always enable LAN mode first
        // This ensures the HTTP streaming service (port 18088) is running
        // even if MQTT credentials are cached from a previous session
        if (Config.AutoLanMode)
        {
            await TryEnableLanModeAsync(ct);
        }

        try
        {
            await _mqttController.ConnectAsync(
                Config.Ip,
                Config.MqttUsername!,
                Config.MqttPassword!,
                ct);
        }
        catch when (Config.AutoLanMode)
        {
            // MQTT connection failed after LAN mode - try enabling again
            await TryEnableLanModeAsync(ct);

            // Retry MQTT connection after enabling LAN mode
            await _mqttController.ConnectAsync(
                Config.Ip,
                Config.MqttUsername!,
                Config.MqttPassword!,
                ct);
        }

        _mqttStatus.Connected = true;

        // Subscribe to all topics to detect model code and monitor for stop commands
        await _mqttController.SubscribeToAllAsync(ct);

        // Wait for model code detection if not already known
        if (string.IsNullOrEmpty(Config.ModelCode))
        {
            LogStatus("Waiting for model code detection...");
            var modelCode = await _mqttController.WaitForModelDetectionAsync(
                TimeSpan.FromSeconds(MqttDetectionTimeout), ct);

            if (string.IsNullOrEmpty(modelCode))
            {
                throw new Exception("Failed to auto-detect model code from MQTT");
            }

            Config.ModelCode = modelCode;
        }

        _mqttStatus.ModelCodeDetected = true;
        _mqttStatus.DetectedModelCode = Config.ModelCode;

        // Try to start the camera
        if (string.IsNullOrEmpty(Config.DeviceId))
        {
            throw new Exception("Device ID is required to start camera");
        }

        LogStatus("Sending camera start command...");

        var started = await _mqttController.TryStartCameraAsync(
            Config.DeviceId,
            Config.ModelCode,
            ct);

        if (!started)
        {
            throw new Exception("Failed to start camera via MQTT");
        }

        _mqttStatus.CameraStarted = true;
        LogStatus("Camera start command sent successfully");

        // Keep MQTT connected for instant stream recovery
        // When the slicer or another app stops the camera, we can restart it immediately
        LogStatus("MQTT staying connected for stream recovery");
    }

    /// <summary>
    /// Try to enable LAN mode on the printer via SSH.
    /// </summary>
    private async Task TryEnableLanModeAsync(CancellationToken ct)
    {
        LogStatusThrottled("lanmode", "Enabling LAN mode via SSH...");

        _lanModeService = new LanModeService();
        _lanModeService.StatusChanged += (s, msg) => { }; // Suppress - main loop handles logging

        var result = await _lanModeService.EnableLanModeAsync(
            Config.Ip,
            Config.SshPort,
            Config.SshUser,
            Config.SshPassword,
            ct);

        if (!result.Success)
        {
            throw new Exception($"Failed to enable LAN mode: {result.Error}");
        }

        if (!result.WasAlreadyOpen)
        {
            LogStatusThrottled("lanmode", "LAN mode enabled, waiting for services to start...");
            // LAN mode just enabled - wait for MQTT to become available
            await Task.Delay(5000, ct);
        }
        else
        {
            LogStatusThrottled("lanmode", "LAN mode already enabled");
        }
    }

    private void StartMjpegServer()
    {
        LogStatus($"Starting MJPEG server on port {Config.MjpegPort}...");

        _mjpegServer = new MjpegServer();
        _mjpegServer.StatusChanged += (s, msg) => LogStatus($"MJPEG: {msg}");
        _mjpegServer.ErrorOccurred += (s, ex) => LogStatus($"MJPEG Error: {ex.Message}");

        // Apply per-printer encoding settings
        _mjpegServer.MaxFps = Config.MaxFps;
        _mjpegServer.IdleFps = Config.IdleFps;
        _mjpegServer.JpegQuality = Config.JpegQuality;

        // Configure LL-HLS (Low-Latency HLS)
        _mjpegServer.ConfigureLlHls(Config.LlHlsEnabled, Config.HlsPartDurationMs);

        // Determine bind addresses from config
        var bindAddresses = new List<IPAddress>();
        if (_listenInterfaces.Count > 0 && !_listenInterfaces.Contains("0.0.0.0"))
        {
            foreach (var iface in _listenInterfaces)
            {
                if (IPAddress.TryParse(iface, out var addr))
                {
                    bindAddresses.Add(addr);
                }
            }
        }

        // Default to any if no valid addresses configured
        if (bindAddresses.Count == 0)
        {
            bindAddresses.Add(IPAddress.Any);
        }

        // Wire up LED control callbacks
        _mjpegServer.GetLedStatusAsync = GetLedStatusAsync;
        _mjpegServer.SetLedAsync = SetLedAsync;

        _mjpegServer.Start(Config.MjpegPort, bindAddresses);
        var addressList = string.Join(", ", bindAddresses.Select(a => $"{a}:{Config.MjpegPort}"));
        LogStatus($"MJPEG server listening on {addressList} (maxFps={Config.MaxFps}, idleFps={Config.IdleFps}, quality={Config.JpegQuality})");
        LogStatus($"H.264 WebSocket endpoint: ws://<ip>:{Config.MjpegPort}/h264");
        LogStatus($"HLS endpoint: http://<ip>:{Config.MjpegPort}/hls/playlist.m3u8");
    }

    /// <summary>
    /// Start the Obico client if enabled and firmware supports it.
    /// </summary>
    private async Task StartObicoClientAsync(CancellationToken ct)
    {
        // Update basic status
        _obicoStatus.Enabled = Config.Obico.Enabled;
        _obicoStatus.IsLinked = Config.Obico.IsLinked;
        _obicoStatus.IsPro = Config.Obico.IsPro;
        _obicoStatus.TargetFps = Config.Obico.TargetFps;

        if (!Config.Obico.Enabled)
        {
            _obicoStatus.State = "Disabled";
            LogStatus("Obico: Not enabled (use CLI to configure)");
            return;
        }

        if (!Config.Firmware.MoonrakerAvailable)
        {
            _obicoStatus.State = "No Moonraker";
            LogStatus("Obico: Skipping - Moonraker not available (requires Rinkhals firmware)");
            return;
        }

        if (!Config.Obico.IsLinked)
        {
            _obicoStatus.State = "Not Linked";
            LogStatus("Obico: Skipping - printer not linked to Obico server");
            return;
        }

        try
        {
            LogStatus("Starting Obico client...");
            _obicoStatus.State = "Starting";

            _obicoClient = new ObicoClient(Config);
            _obicoClient.StatusChanged += (s, msg) => LogStatus($"Obico: {msg}");
            _obicoClient.StateChanged += OnObicoStateChanged;
            _obicoClient.ConfigUpdated += OnObicoConfigUpdated;

            // Wire up Janus streaming state to MJPEG server for full-rate encoding
            if (_mjpegServer != null)
            {
                _obicoClient.JanusStreamingChanged += (s, streaming) =>
                {
                    _mjpegServer.HasExternalStreamingClient = streaming;
                    LogStatus($"Janus streaming {(streaming ? "started" : "stopped")} - MJPEG encoding at {(streaming ? "full" : "idle")} rate");
                };
            }

            // Wire up native firmware sync for print cancellation from Obico
            _obicoClient.NativePrintStopRequested += async (s, e) =>
            {
                if (_mqttController != null && !string.IsNullOrEmpty(Config.DeviceId))
                {
                    LogStatus("Syncing native Anycubic firmware: sending print stop via MQTT");
                    await _mqttController.SendPrintStopAsync(Config.DeviceId, Config.ModelCode);
                }
            };

            // Set snapshot callback if camera is enabled
            if (Config.CameraEnabled && _mjpegServer != null)
            {
                _obicoClient.SetSnapshotCallback(() => _mjpegServer.GetLastJpegFrame());
            }

            await _obicoClient.StartAsync(ct);

            _obicoStatus.State = _obicoClient.State.ToString();
            _obicoStatus.ServerConnected = _obicoClient.State == ObicoClientState.Running;
            LogStatus($"Obico client started (state: {_obicoClient.State})");
        }
        catch (Exception ex)
        {
            _obicoStatus.State = "Failed";
            _obicoStatus.LastError = ex.Message;
            LogStatus($"Obico client failed to start: {ex.Message}");
            // Don't throw - Obico failure shouldn't stop the camera
        }
    }

    private void OnObicoStateChanged(object? sender, ObicoClientState state)
    {
        _obicoStatus.State = state.ToString();
        _obicoStatus.ServerConnected = state == ObicoClientState.Running;

        // When ObicoClient fails (Moonraker not available), try to enable LAN mode
        // This handles the case where printer reboots and Moonraker doesn't start until LAN mode is enabled
        if (state == ObicoClientState.Failed && Config.AutoLanMode)
        {
            LogStatus("Obico client failed - attempting to enable LAN mode...");
            _ = TryEnableLanModeAndRestartObicoAsync();
        }
    }

    /// <summary>
    /// Try to enable LAN mode and restart Obico client.
    /// Called when Obico client fails due to Moonraker not being available.
    /// </summary>
    private async Task TryEnableLanModeAndRestartObicoAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await TryEnableLanModeAsync(cts.Token);

            // Wait a bit for Moonraker to start after LAN mode is enabled
            LogStatus("LAN mode enabled, waiting for Moonraker to start...");
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);

            // Restart Obico client
            if (_obicoClient != null)
            {
                LogStatus("Restarting Obico client...");
                await _obicoClient.StopAsync();
                await _obicoClient.StartAsync(cts.Token);
                LogStatus($"Obico client restarted (state: {_obicoClient.State})");
            }
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to enable LAN mode and restart Obico: {ex.Message}");
        }
    }

    private void OnObicoConfigUpdated(object? sender, EventArgs e)
    {
        // Forward to ConfigChanged to persist changes (e.g., tier update)
        _obicoStatus.IsPro = Config.Obico.IsPro;
        _obicoStatus.TargetFps = Config.Obico.TargetFps;
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunStreamingLoopAsync(CancellationToken ct)
    {
        _streamStatus.Connected = false;
        _streamStatus.FramesDecoded = 0;
        _streamStalled = false;
        _streamFailedAt = DateTime.MinValue;
        _streamStartedAt = DateTime.MinValue;

        LogStatus("Starting FFmpeg decoder...");

        _decoder = new FfmpegDecoder();
        _decoder.StatusChanged += (s, msg) =>
        {
            LogStatusThrottled("decoder_status", $"Decoder: {msg}");
            _streamStatus.DecoderStatus = msg;
        };
        _decoder.ErrorOccurred += (s, ex) => LogStatusThrottled("decoder_error", $"Decoder Error: {ex.Message}");
        _decoder.FrameDecoded += (s, args) =>
        {
            _streamStatus.FramesDecoded++;
            _streamStatus.Width = args.Width;
            _streamStatus.Height = args.Height;
            _mjpegServer?.PushFrame(args.Data, args.Width, args.Height, args.Stride);
        };
        _decoder.DecodingStarted += async (s, e) =>
        {
            _streamStatus.Connected = true;
            _streamStatus.Width = _decoder.Width;
            _streamStatus.Height = _decoder.Height;
            _state = PrinterState.Running;
            _lastSeenOnline = DateTime.UtcNow;
            _streamStartedAt = DateTime.UtcNow; // Track when stream started for stabilization
            _streamStalled = false;
            LogStatus($"Stream running: {_decoder.Width}x{_decoder.Height}");

            // Extract SPS/PPS and NAL length size from decoder extradata for H.264 streaming
            if (_decoder.Extradata != null && _decoder.Extradata.Length > 0)
            {
                var (sps, pps, nalLengthSize) = ParseAvccExtradata(_decoder.Extradata);
                _mjpegServer?.SetH264Parameters(sps, pps, nalLengthSize);
                if (sps != null && pps != null)
                {
                    // Log SPS details for debugging (NAL type should be 0x67 or 0x27)
                    var spsHex = sps.Length >= 4 ? $"[{sps[0]:X2} {sps[1]:X2} {sps[2]:X2} {sps[3]:X2}...]" : BitConverter.ToString(sps);
                    var ppsHex = pps.Length >= 2 ? $"[{pps[0]:X2} {pps[1]:X2}...]" : BitConverter.ToString(pps);
                    LogStatus($"H.264 parameters extracted (SPS: {sps.Length}B {spsHex}, PPS: {pps.Length}B {ppsHex}, NAL size: {nalLengthSize}B)");
                }
            }

            // Start Obico client now that the printer/stream is ready
            // This ensures LAN mode is properly enabled before Obico tries to connect to Moonraker
            if (_obicoClient == null && Config.Obico.Enabled)
            {
                try
                {
                    await StartObicoClientAsync(ct);
                }
                catch (Exception ex)
                {
                    LogStatus($"Failed to start Obico client: {ex.Message}");
                }
            }

            // Pass decoder to Obico client for H.264 RTP streaming (shares source stream)
            // Must be called after ObicoClient is started so Janus is ready
            _obicoClient?.SetDecoder(_decoder);
        };
        _decoder.DecodingStopped += (s, e) =>
        {
            _streamStatus.Connected = false;
            _streamStartedAt = DateTime.MinValue; // Reset stabilization tracking
            LogStatus("Stream stopped");
        };
        _decoder.StreamStalled += (s, e) =>
        {
            _streamStalled = true;
            LogStatus("Stream stalled (PTS not advancing)");
        };

        // Subscribe to raw H.264 packets for WebSocket and HLS streaming
        _decoder.RawPacketReceived += (s, args) =>
        {
            // Push to H.264 WebSocket clients (Mainsail/Fluidd jmuxer) and HLS
            _mjpegServer?.PushH264Packet(args.Data, args.IsKeyframe, args.Pts);
        };

        // Register callback for snapshot requests when no frame available
        if (_mjpegServer != null)
        {
            _mjpegServer.SnapshotRequested += OnSnapshotRequested;
        }

        // Build stream URL
        var streamUrl = string.Format(FlvUrlTemplate, Config.Ip);
        LogStatus($"Connecting to stream: {streamUrl}");

        _decoder.Start(streamUrl);

        // LED auto-control: poll interval (every 30 seconds)
        var lastLedAutoControlCheck = DateTime.MinValue;
        const int ledAutoControlIntervalSeconds = 30;
        var initialLedQueryDone = false;

        // Camera keepalive: periodic startCapture to prevent frame rate throttling
        var lastCameraKeepalive = DateTime.UtcNow; // Start from now since camera was just started

        // Initial connection grace period - give decoder time to connect before checking health
        const int InitialConnectionGraceSeconds = 5;
        var decoderStartTime = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested && !_isPaused)
            {
                // Check if we're still in initial connection grace period
                var timeSinceDecoderStart = (DateTime.UtcNow - decoderStartTime).TotalSeconds;
                var inInitialGracePeriod = timeSinceDecoderStart < InitialConnectionGraceSeconds;

                // Check if stream is healthy (running, not stalled, and stabilized)
                var streamRunning = _decoder.IsRunning && !_streamStalled;
                var streamStabilized = _streamStartedAt != DateTime.MinValue &&
                                       (DateTime.UtcNow - _streamStartedAt).TotalSeconds >= StreamStabilizationSeconds;
                var streamHealthy = streamRunning && streamStabilized;

                // Check if decoder is stuck (no frames received for too long)
                // This catches: 1) decoder stuck connecting, 2) decoder running but no frames
                var noFramesTimeout = _decoder.TimeSinceLastFrame.TotalSeconds > 10;
                if (noFramesTimeout && timeSinceDecoderStart > 10)
                {
                    // Decoder has been running for >10s but no frames - it's stuck
                    streamHealthy = false;
                    _streamStalled = true;
                    inInitialGracePeriod = false; // Override grace period - we've waited long enough
                }

                // During initial grace period or stabilization, just wait (unless stuck)
                if (inInitialGracePeriod || (streamRunning && !streamStabilized && !_streamStalled))
                {
                    await Task.Delay(500, ct);
                    continue;
                }

                if (streamHealthy)
                {
                    // Stream is stable - reset failure tracking and log throttling
                    if (_streamFailedAt != DateTime.MinValue)
                    {
                        ResetLogThrottling(); // Reset throttling on successful recovery
                        _streamFailedAt = DateTime.MinValue;
                        _lastLanModeAttempt = DateTime.MinValue; // Reset LAN mode tracking
                        _state = PrinterState.Running; // Ensure state is Running after recovery
                        _obicoClient?.SetPrinterOffline(false); // Printer is back online
                    }
                    _lastSeenOnline = DateTime.UtcNow;
                    _streamFailedAt = DateTime.MinValue; // Reset failure tracking
                    _lastLanModeAttempt = DateTime.MinValue; // Reset LAN mode tracking
                    _streamStalled = false;

                    // Query LED status once on startup
                    if (!initialLedQueryDone)
                    {
                        initialLedQueryDone = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await GetLedStatusAsync(ct);
                            }
                            catch { /* Ignore errors on initial query */ }
                        }, ct);
                    }

                    // LED auto-control check (every 30 seconds)
                    if (Config.LedAutoControl && (DateTime.UtcNow - lastLedAutoControlCheck).TotalSeconds >= ledAutoControlIntervalSeconds)
                    {
                        lastLedAutoControlCheck = DateTime.UtcNow;
                        await ProcessLedAutoControlAsync(ct);
                    }

                    // Camera keepalive: resend startCapture to prevent frame rate throttling
                    // Only when there are active consumers (MJPEG, H.264 WebSocket, HLS, or Janus viewers)
                    if (Config.CameraKeepaliveSeconds > 0 &&
                        (DateTime.UtcNow - lastCameraKeepalive).TotalSeconds >= Config.CameraKeepaliveSeconds)
                    {
                        lastCameraKeepalive = DateTime.UtcNow;
                        var mjpegClients = _mjpegServer?.ConnectedClients ?? 0;
                        var h264Clients = _mjpegServer?.H264WebSocketClients ?? 0;
                        var hasExternal = _mjpegServer?.HasExternalStreamingClient ?? false;
                        var hasHls = _mjpegServer?.HasHlsActivity ?? false;
                        var hasConsumers = mjpegClients > 0 || h264Clients > 0 || hasExternal || hasHls;

                        if (hasConsumers)
                        {
                            await SendCameraKeepaliveAsync(ct);
                        }
                        else
                        {
                            LogStatusThrottled("camera_keepalive_skip", $"Keepalive skipped: no consumers (MJPEG={mjpegClients}, H264={h264Clients}, External={hasExternal}, HLS={hasHls})");
                        }
                    }

                    await Task.Delay(1000, ct);
                }
                else
                {
                    // Stream failed or stalled - implement recovery logic
                    if (_streamFailedAt == DateTime.MinValue)
                    {
                        _streamFailedAt = DateTime.UtcNow;
                        _state = PrinterState.Retrying; // Update state so UI reflects recovery mode

                        // Mark printer offline to suppress Obico/Janus logging during recovery
                        _obicoClient?.SetPrinterOffline(true);
                    }

                    var failureDuration = DateTime.UtcNow - _streamFailedAt;
                    var inQuickRecoveryPeriod = failureDuration.TotalMinutes < QuickRecoveryPeriodMinutes;

                    if (inQuickRecoveryPeriod)
                    {
                        // First 5 minutes: quick 2-second retry (with throttled logging)
                        LogStatusThrottled("stream_recovery",
                            $"Stream recovery in progress ({failureDuration.TotalSeconds:F0}s)...");

                        var recoveryStarted = await TryStreamRecoveryAsync(streamUrl, ct);
                        if (recoveryStarted)
                        {
                            decoderStartTime = DateTime.UtcNow; // Reset grace period only if decoder was restarted
                        }
                        await Task.Delay(StreamRecoveryDelayMs, ct);
                    }
                    else
                    {
                        // After 5 minutes: check if printer is available
                        var printerAvailable = await IsPrinterAvailableAsync(ct);

                        if (printerAvailable)
                        {
                            // Printer is available - continue retry with SSH+LAN mode
                            LogStatusThrottled("stream_recovery",
                                $"Recovery: printer available, retrying ({failureDuration.TotalMinutes:F1}min)...");
                            var recoveryStarted = await TryStreamRecoveryAsync(streamUrl, ct);
                            if (recoveryStarted)
                            {
                                decoderStartTime = DateTime.UtcNow; // Reset grace period only if decoder was restarted
                            }
                            await Task.Delay(StreamRecoveryDelayMs, ct);
                        }
                        else
                        {
                            // Printer not available - notify Obico client and trigger full restart
                            _obicoClient?.SetPrinterOffline(true);
                            throw new Exception($"Stream failed and printer not available after {failureDuration.TotalMinutes:F1} minutes");
                        }
                    }
                }
            }
        }
        finally
        {
            if (_mjpegServer != null)
            {
                _mjpegServer.SnapshotRequested -= OnSnapshotRequested;
            }
            if (_decoder != null)
            {
                _decoder.StreamStalled -= (s, e) => _streamStalled = true;
            }
            _decoder?.Stop();
        }
    }

    /// <summary>
    /// Try to recover the stream by restarting camera and decoder.
    /// Returns true if recovery was attempted (MQTT succeeded), false if MQTT failed.
    /// </summary>
    private async Task<bool> TryStreamRecoveryAsync(string streamUrl, CancellationToken ct)
    {
        _streamStalled = false;

        // If stream has been failing for a while and AutoLanMode is enabled,
        // try enabling LAN mode even if MQTT is connected (camera service may need it)
        var failureDuration = _streamFailedAt != DateTime.MinValue
            ? (DateTime.UtcNow - _streamFailedAt).TotalSeconds
            : 0;
        var timeSinceLastLanModeAttempt = _lastLanModeAttempt != DateTime.MinValue
            ? (DateTime.UtcNow - _lastLanModeAttempt).TotalSeconds
            : double.MaxValue;

        if (Config.AutoLanMode &&
            failureDuration >= LanModeRetryThresholdSeconds &&
            timeSinceLastLanModeAttempt >= LanModeRetryThresholdSeconds)
        {
            LogStatus("Stream recovery: MQTT connected but stream not working - trying LAN mode...");
            _lastLanModeAttempt = DateTime.UtcNow;
            try
            {
                await TryEnableLanModeAsync(ct);
            }
            catch (Exception ex)
            {
                LogStatusThrottled("lan_mode_recovery", $"LAN mode enable failed: {ex.Message}");
            }
        }

        // Try to restart camera via MQTT (includes SSH+LAN mode retry if enabled)
        if (!await TryQuickCameraRestartAsync(ct))
        {
            // MQTT failed - don't start decoder, stream won't be available
            // Logging is handled by TryQuickCameraRestartAsync
            return false;
        }

        // MQTT succeeded, wait a moment for camera to start
        await Task.Delay(500, ct);

        // Restart decoder
        _decoder?.Stop();
        _decoder?.Start(streamUrl);

        // Give decoder a moment to connect
        await Task.Delay(1500, ct);
        return true;
    }

    /// <summary>
    /// Check if printer is available via MQTT and SSH.
    /// </summary>
    private async Task<bool> IsPrinterAvailableAsync(CancellationToken ct)
    {
        // Check 1: Is MQTT connected?
        if (_mqttController != null && _mqttController.IsConnected)
        {
            return true;
        }

        // Check 2: Can we reach the printer via TCP (SSH port)?
        if (await IsHostRespondingAsync())
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Called when LED status is received from MQTT messages.
    /// </summary>
    private void OnLedStatusReceived(object? sender, LedStatus status)
    {
        lock (_ledStateLock)
        {
            _cachedLedStatus = status;
        }
    }

    private void OnPrinterStateReceived(object? sender, string state)
    {
        lock (_ledStateLock)
        {
            _printerMqttState = state;
        }
    }

    /// <summary>
    /// Called when an external source (slicer, etc.) sends a camera stop command.
    /// Immediately restart the camera to maintain the stream.
    /// </summary>
    private async void OnExternalCameraStopDetected(object? sender, EventArgs e)
    {
        LogStatus("External camera stop detected! Immediately restarting camera...");

        // Small delay to let the stop command take effect
        await Task.Delay(500);

        // Restart the camera immediately
        if (_mqttController != null && _mqttController.IsConnected &&
            !string.IsNullOrEmpty(Config.DeviceId) && !string.IsNullOrEmpty(Config.ModelCode))
        {
            try
            {
                var started = await _mqttController.TryStartCameraAsync(
                    Config.DeviceId, Config.ModelCode, CancellationToken.None);

                if (started)
                {
                    LogStatus("Camera restarted successfully after external stop");
                    _mqttStatus.CameraStarted = true;
                }
                else
                {
                    LogStatus("Failed to restart camera after external stop");
                }
            }
            catch (Exception ex)
            {
                LogStatus($"Error restarting camera after external stop: {ex.Message}");
            }
        }
        else
        {
            LogStatus("Cannot restart camera - MQTT not connected or missing config");
        }
    }

    /// <summary>
    /// Called when a snapshot is requested but no frame is available.
    /// Tries to quickly restart the camera.
    /// </summary>
    private async void OnSnapshotRequested(object? sender, EventArgs e)
    {
        if (_state != PrinterState.Running || _streamStatus.Connected)
            return; // Camera is either not ready or already streaming

        LogStatusThrottled("snapshot_restart", "Snapshot requested, attempting to restart camera...");
        try
        {
            await TryQuickCameraRestartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStatusThrottled("snapshot_restart_error", $"Failed to restart camera for snapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to quickly restart the camera via MQTT using existing connection.
    /// When MQTT fails and AutoLanMode is enabled, tries SSH+LAN mode like initial connection.
    /// </summary>
    private async Task<bool> TryQuickCameraRestartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Config.DeviceId) || string.IsNullOrEmpty(Config.ModelCode))
        {
            LogStatusThrottled("quick_restart", "Quick restart: Missing device ID or model code");
            return false;
        }

        try
        {
            // Use existing MQTT controller if connected
            if (_mqttController != null && _mqttController.IsConnected)
            {
                var started = await _mqttController.TryStartCameraAsync(Config.DeviceId, Config.ModelCode, ct);

                if (started)
                {
                    LogStatus("Camera start command sent via MQTT");
                    _mqttStatus.CameraStarted = true;
                    return true;
                }
            }
            else
            {
                // MQTT disconnected - try to reconnect
                if (string.IsNullOrEmpty(Config.MqttUsername) || string.IsNullOrEmpty(Config.MqttPassword))
                {
                    LogStatusThrottled("quick_restart", "Recovery: No MQTT credentials available");
                    return false;
                }

                if (_mqttController == null)
                {
                    _mqttController = new MqttCameraController();
                    _mqttController.MqttPort = Config.MqttPort;
                    // Use strongly throttled logging for MQTT during recovery
                    _mqttController.StatusChanged += (s, msg) => { }; // Suppress during recovery
                    _mqttController.ErrorOccurred += (s, ex) => { }; // Suppress during recovery
                    _mqttController.CameraStopDetected += OnExternalCameraStopDetected;
                    _mqttController.LedStatusReceived += OnLedStatusReceived;
                    _mqttController.PrinterStateReceived += OnPrinterStateReceived;
                }

                // Try MQTT reconnection
                try
                {
                    await _mqttController.ConnectAsync(Config.Ip, Config.MqttUsername, Config.MqttPassword, ct);
                    await _mqttController.SubscribeToAllAsync(ct);
                    _mqttStatus.Connected = true;

                    var started = await _mqttController.TryStartCameraAsync(Config.DeviceId, Config.ModelCode, ct);
                    if (started)
                    {
                        LogStatus("Camera restarted via MQTT reconnection");
                        _mqttStatus.CameraStarted = true;
                        ResetLogThrottling(); // Connection succeeded, reset throttling
                        return true;
                    }
                }
                catch when (Config.AutoLanMode)
                {
                    // MQTT failed - try SSH+LAN mode like initial connection
                    try
                    {
                        await TryEnableLanModeAsync(ct);

                        // Retry MQTT after enabling LAN mode
                        await _mqttController.ConnectAsync(Config.Ip, Config.MqttUsername, Config.MqttPassword, ct);
                        await _mqttController.SubscribeToAllAsync(ct);
                        _mqttStatus.Connected = true;

                        var started = await _mqttController.TryStartCameraAsync(Config.DeviceId, Config.ModelCode, ct);
                        if (started)
                        {
                            LogStatus("Camera restarted after enabling LAN mode");
                            _mqttStatus.CameraStarted = true;
                            ResetLogThrottling();
                            return true;
                        }
                    }
                    catch
                    {
                        // LAN mode also failed - will be logged by caller
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogStatusThrottled("quick_restart", $"Recovery failed: {ex.Message}");
            _mqttStatus.Connected = false;
        }

        return false;
    }

    /// <summary>
    /// Process LED auto-control based on printer state.
    /// - Turns LED on when printer is active (not idle)
    /// - Turns LED off after timeout when printer is idle
    /// </summary>
    private async Task ProcessLedAutoControlAsync(CancellationToken ct)
    {
        try
        {
            // Try to query printer state (updates _printerMqttState if successful)
            // Don't fail if query returns null - use cached state instead
            _ = await GetPrinterInfoAsync(ct);

            // Get LED status (uses cached status if query fails)
            var ledStatus = await GetLedStatusAsync(ct);

            var isIdle = IsPrinterIdle();
            var ledIsOn = ledStatus?.IsOn ?? _cachedLedStatus?.IsOn ?? false;
            var currentState = _printerMqttState ?? "unknown";

            lock (_ledStateLock)
            {
                if (!isIdle)
                {
                    // Printer is active (printing, etc.) - turn LED on
                    _ledOnIdleSince = null;

                    if (!ledIsOn)
                    {
                        LogStatus($"LED Auto-control: Printer is active ({currentState}), turning LED on");
                        _ = SetLedAsync(true, ct);
                    }
                }
                else
                {
                    // Printer is idle
                    if (ledIsOn)
                    {
                        // LED is on while idle - check timeout
                        if (_ledOnIdleSince == null)
                        {
                            _ledOnIdleSince = DateTime.UtcNow;
                            LogStatus($"LED Auto-control: Printer is idle ({currentState}), LED on - starting timeout timer ({Config.StandbyLedTimeoutMinutes}min)");
                        }

                        if (Config.StandbyLedTimeoutMinutes > 0)
                        {
                            var idleMinutes = (DateTime.UtcNow - _ledOnIdleSince.Value).TotalMinutes;
                            if (idleMinutes >= Config.StandbyLedTimeoutMinutes)
                            {
                                LogStatus($"LED Auto-control: Idle timeout ({Config.StandbyLedTimeoutMinutes}min) reached, turning LED off");
                                _ = SetLedAsync(false, ct);
                                _ledOnIdleSince = null;
                            }
                        }
                    }
                    else
                    {
                        // LED is off while idle - reset tracking
                        _ledOnIdleSince = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogStatus($"LED Auto-control error: {ex.Message}");
        }
    }

    /// <summary>
    /// Send camera keepalive (startCapture) to maintain full frame rate.
    /// Anycubic cameras throttle to ~4fps after 30-60s without activity.
    /// </summary>
    private async Task SendCameraKeepaliveAsync(CancellationToken ct)
    {
        if (_mqttController == null || !_mqttController.IsConnected ||
            string.IsNullOrEmpty(Config.DeviceId) || string.IsNullOrEmpty(Config.ModelCode))
        {
            return;
        }

        try
        {
            var started = await _mqttController.TryStartCameraAsync(Config.DeviceId, Config.ModelCode, ct);
            if (started)
            {
                LogStatusThrottled("camera_keepalive", "Camera keepalive sent");
            }
        }
        catch (Exception ex)
        {
            LogStatusThrottled("camera_keepalive_error", $"Camera keepalive failed: {ex.Message}");
        }
    }

    private async Task<bool> IsHostRespondingAsync()
    {
        try
        {
            // Try a TCP connection to SSH port as a quick check
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(Config.Ip, Config.SshPort);
            var completed = await Task.WhenAny(connectTask, Task.Delay(2000));

            if (completed == connectTask && client.Connected)
            {
                return true;
            }
        }
        catch
        {
            // Ignore connection errors
        }

        // Also try ping
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(Config.Ip, 2000);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parse SPS and PPS NAL units from AVCC extradata format.
    /// Also extracts the NAL length size used in the stream.
    /// </summary>
    private static (byte[]? Sps, byte[]? Pps, int NalLengthSize) ParseAvccExtradata(byte[] extradata)
    {
        byte[]? spsNal = null;
        byte[]? ppsNal = null;
        int nalLengthSize = 4; // Default to 4 bytes

        if (extradata == null || extradata.Length < 8)
            return (null, null, nalLengthSize);

        try
        {
            // Extract NAL length size from byte 4 (lower 2 bits + 1)
            nalLengthSize = (extradata[4] & 0x03) + 1;

            int offset = 5; // Skip config header (version, profile, compat, level, nal_length_size)

            // Number of SPS (lower 5 bits)
            int numSps = extradata[offset] & 0x1F;
            offset++;

            for (int i = 0; i < numSps && offset + 2 <= extradata.Length; i++)
            {
                // SPS length (big-endian 16-bit)
                int spsLen = (extradata[offset] << 8) | extradata[offset + 1];
                offset += 2;

                if (offset + spsLen <= extradata.Length)
                {
                    spsNal = new byte[spsLen];
                    Array.Copy(extradata, offset, spsNal, 0, spsLen);
                    offset += spsLen;
                }
            }

            // Number of PPS
            if (offset < extradata.Length)
            {
                int numPps = extradata[offset];
                offset++;

                for (int i = 0; i < numPps && offset + 2 <= extradata.Length; i++)
                {
                    // PPS length (big-endian 16-bit)
                    int ppsLen = (extradata[offset] << 8) | extradata[offset + 1];
                    offset += 2;

                    if (offset + ppsLen <= extradata.Length)
                    {
                        ppsNal = new byte[ppsLen];
                        Array.Copy(extradata, offset, ppsNal, 0, ppsLen);
                        offset += ppsLen;
                    }
                }
            }
        }
        catch
        {
            // Failed to parse extradata
        }

        return (spsNal, ppsNal, nalLengthSize);
    }

    private async Task CleanupServicesAsync()
    {
        // Stop Obico client first (it may depend on other services)
        if (_obicoClient != null)
        {
            try
            {
                _obicoClient.StateChanged -= OnObicoStateChanged;
                _obicoClient.ConfigUpdated -= OnObicoConfigUpdated;
                await _obicoClient.StopAsync();
            }
            catch { }
            _obicoClient.Dispose();
            _obicoClient = null;
            _obicoStatus.State = "Stopped";
            _obicoStatus.ServerConnected = false;
        }

        // Stop decoder
        if (_decoder != null)
        {
            _decoder.Stop();
            _decoder.Dispose();
            _decoder = null;
        }

        // Stop MJPEG server
        if (_mjpegServer != null)
        {
            _mjpegServer.Stop();
            _mjpegServer.Dispose();
            _mjpegServer = null;
        }

        // Cleanup MQTT controller
        if (_mqttController != null)
        {
            // Unsubscribe from events first
            _mqttController.CameraStopDetected -= OnExternalCameraStopDetected;
            _mqttController.LedStatusReceived -= OnLedStatusReceived;
            _mqttController.PrinterStateReceived -= OnPrinterStateReceived;

            // Stop camera via MQTT only if SendStopCommand is enabled
            if (Config.SendStopCommand &&
                !string.IsNullOrEmpty(Config.DeviceId) && !string.IsNullOrEmpty(Config.ModelCode))
            {
                try
                {
                    if (_mqttController.IsConnected)
                    {
                        await _mqttController.TryStopCameraAsync(Config.DeviceId, Config.ModelCode);
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            try
            {
                await _mqttController.DisconnectAsync();
            }
            catch { }
            await _mqttController.DisposeAsync();
            _mqttController = null;
        }

        _sshService = null;
        _lanModeService = null;
    }

    private void LogStatus(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        Console.WriteLine($"[{timestamp}] [{Config.Name}] {message}");
        StatusChanged?.Invoke(this, message);
    }

    /// <summary>
    /// Log a status message with throttling for repetitive messages.
    /// </summary>
    private void LogStatusThrottled(string key, string message)
    {
        _logThrottler.Log(key, message);
    }

    /// <summary>
    /// Reset log throttling (e.g., when connection succeeds).
    /// </summary>
    private void ResetLogThrottling()
    {
        _logThrottler.ResetAll();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        CleanupServicesAsync().GetAwaiter().GetResult();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }

    ~PrinterThread()
    {
        Dispose();
    }
}
