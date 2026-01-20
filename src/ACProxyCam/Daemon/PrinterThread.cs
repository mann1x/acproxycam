// PrinterThread.cs - Single printer worker thread

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ACProxyCam.Models;
using ACProxyCam.Services;

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

    // Status details
    private readonly SshStatus _sshStatus = new();
    private readonly MqttStatus _mqttStatus = new();
    private readonly StreamStatus _streamStatus = new();

    // LED auto-control state
    private string? _printerMqttState;
    private LedStatus? _cachedLedStatus;
    private DateTime? _ledOnIdleSince;
    private readonly object _ledStateLock = new();

    // Watchdog settings
    private const int RetryDelayResponsive = 5;   // 5 seconds if host responds
    private const int RetryDelayOffline = 30;     // 30 seconds if host is offline
    private const int MqttDetectionTimeout = 10;  // 10 seconds to detect model code

    // Camera stream URL pattern
    private const string FlvUrlTemplate = "http://{0}:18088/flv";

    public event EventHandler<string>? StatusChanged;

    public PrinterThread(PrinterConfig config, List<string> listenInterfaces)
    {
        Config = config;
        _listenInterfaces = listenInterfaces;
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
                State = _state,
                ConnectedClients = _mjpegServer?.ConnectedClients ?? 0,
                IsPaused = _isPaused,
                CpuAffinity = _cpuAffinity,
                CurrentFps = (_mjpegServer?.ConnectedClients ?? 0) > 0 ? Config.MaxFps : Config.IdleFps,
                IsIdle = (_mjpegServer?.ConnectedClients ?? 0) == 0,
                IsOnline = _state == PrinterState.Running,
                LastError = _lastError,
                LastSeenOnline = _lastSeenOnline,
                NextRetryAt = _nextRetryAt,
                SshStatus = _sshStatus,
                MqttStatus = _mqttStatus,
                StreamStatus = _streamStatus,
                PrinterMqttState = _printerMqttState,
                CameraLed = _cachedLedStatus
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

                LogStatus($"Starting connection to {Config.Ip}...");

                // Step 1: Check if we need SSH credentials
                if (string.IsNullOrEmpty(Config.MqttUsername) || string.IsNullOrEmpty(Config.MqttPassword))
                {
                    await RetrieveSshCredentialsAsync(ct);
                }

                // Step 2: MQTT - Connect and detect model code if needed
                await ConnectMqttAndStartCameraAsync(ct);

                // Step 3: Start MJPEG server
                StartMjpegServer();

                // Step 4: Start FFmpeg decoder and streaming loop
                await RunStreamingLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _state = PrinterState.Retrying;

                await CleanupServicesAsync();

                // Determine retry delay based on host responsiveness
                var isResponding = await IsHostRespondingAsync();
                var retryDelay = TimeSpan.FromSeconds(isResponding ? RetryDelayResponsive : RetryDelayOffline);
                _nextRetryAt = DateTime.UtcNow + retryDelay;

                LogStatus($"Error: {ex.Message}. Retrying in {retryDelay.TotalSeconds}s");

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
        _sshService.StatusChanged += (s, msg) => LogStatus($"SSH: {msg}");

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

        LogStatus($"SSH: Credentials retrieved. DeviceId: {Config.DeviceId ?? "unknown"}");
    }

    private async Task ConnectMqttAndStartCameraAsync(CancellationToken ct)
    {
        _mqttStatus.LastAttempt = DateTime.UtcNow;
        _mqttStatus.Connected = false;
        _mqttStatus.DeviceIdDetected = !string.IsNullOrEmpty(Config.DeviceId);
        _mqttStatus.ModelCodeDetected = !string.IsNullOrEmpty(Config.ModelCode);

        LogStatus("Connecting to MQTT broker...");

        _mqttController = new MqttCameraController();
        _mqttController.StatusChanged += (s, msg) => LogStatus($"MQTT: {msg}");
        _mqttController.ErrorOccurred += (s, ex) => LogStatus($"MQTT Error: {ex.Message}");
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

        try
        {
            await _mqttController.ConnectAsync(
                Config.Ip,
                Config.MqttUsername!,
                Config.MqttPassword!,
                ct);
        }
        catch (Exception ex) when (Config.AutoLanMode)
        {
            // MQTT connection failed - try to enable LAN mode
            LogStatus($"MQTT connection failed: {ex.Message}");
            LogStatus("AutoLanMode is enabled, attempting to enable LAN mode on printer...");

            await TryEnableLanModeAsync(ct);

            // Retry MQTT connection after enabling LAN mode
            LogStatus("Retrying MQTT connection...");
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
        _lanModeService = new LanModeService();
        _lanModeService.StatusChanged += (s, msg) => LogStatus($"LAN Mode: {msg}");

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

        if (result.WasAlreadyOpen)
        {
            LogStatus("LAN mode was already enabled on printer");
        }
        else
        {
            LogStatus("LAN mode enabled successfully, waiting 5 seconds for MQTT to become available...");
            await Task.Delay(5000, ct);
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

        // Determine bind address from config
        IPAddress bindAddress = IPAddress.Any;
        if (_listenInterfaces.Count > 0 && !_listenInterfaces.Contains("0.0.0.0"))
        {
            // Try to bind to first configured interface
            if (IPAddress.TryParse(_listenInterfaces[0], out var addr))
            {
                bindAddress = addr;
            }
        }

        // Wire up LED control callbacks
        _mjpegServer.GetLedStatusAsync = GetLedStatusAsync;
        _mjpegServer.SetLedAsync = SetLedAsync;

        _mjpegServer.Start(Config.MjpegPort, bindAddress);
        LogStatus($"MJPEG server listening on {bindAddress}:{Config.MjpegPort} (maxFps={Config.MaxFps}, idleFps={Config.IdleFps}, quality={Config.JpegQuality})");
    }

    private async Task RunStreamingLoopAsync(CancellationToken ct)
    {
        _streamStatus.Connected = false;
        _streamStatus.FramesDecoded = 0;

        LogStatus("Starting FFmpeg decoder...");

        _decoder = new FfmpegDecoder();
        _decoder.StatusChanged += (s, msg) =>
        {
            LogStatus($"Decoder: {msg}");
            _streamStatus.DecoderStatus = msg;
        };
        _decoder.ErrorOccurred += (s, ex) => LogStatus($"Decoder Error: {ex.Message}");
        _decoder.FrameDecoded += (s, args) =>
        {
            _streamStatus.FramesDecoded++;
            _streamStatus.Width = args.Width;
            _streamStatus.Height = args.Height;
            _mjpegServer?.PushFrame(args.Data, args.Width, args.Height, args.Stride);
        };
        _decoder.DecodingStarted += (s, e) =>
        {
            _streamStatus.Connected = true;
            _streamStatus.Width = _decoder.Width;
            _streamStatus.Height = _decoder.Height;
            _state = PrinterState.Running;
            _lastSeenOnline = DateTime.UtcNow;
            LogStatus($"Stream running: {_decoder.Width}x{_decoder.Height}");
        };
        _decoder.DecodingStopped += (s, e) =>
        {
            _streamStatus.Connected = false;
            LogStatus("Stream stopped");
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

        // Wait for cancellation or decoder to stop, with quick recovery on stream failure
        int quickRetryCount = 0;
        const int maxQuickRetries = 3;

        // LED auto-control: poll interval (every 30 seconds)
        var lastLedAutoControlCheck = DateTime.MinValue;
        const int ledAutoControlIntervalSeconds = 30;
        var initialLedQueryDone = false;

        try
        {
            while (!ct.IsCancellationRequested && !_isPaused)
            {
                if (_decoder.IsRunning)
                {
                    _lastSeenOnline = DateTime.UtcNow;
                    quickRetryCount = 0; // Reset on successful streaming

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

                    await Task.Delay(1000, ct);
                }
                else
                {
                    // Stream stopped - try quick recovery via MQTT restart
                    if (quickRetryCount < maxQuickRetries)
                    {
                        quickRetryCount++;
                        LogStatus($"Stream stopped, attempting quick recovery ({quickRetryCount}/{maxQuickRetries})...");

                        if (await TryQuickCameraRestartAsync(ct))
                        {
                            // Wait a moment for camera to start
                            await Task.Delay(2000, ct);

                            // Restart decoder
                            _decoder.Stop();
                            _decoder.Start(streamUrl);

                            // Wait for decoder to connect
                            await Task.Delay(3000, ct);

                            if (_decoder.IsRunning)
                            {
                                LogStatus("Quick recovery successful");
                                continue;
                            }
                        }
                    }

                    // Quick recovery failed - throw to trigger full retry
                    throw new Exception("Stream disconnected and quick recovery failed");
                }
            }
        }
        finally
        {
            if (_mjpegServer != null)
            {
                _mjpegServer.SnapshotRequested -= OnSnapshotRequested;
            }
            _decoder.Stop();
        }
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

        LogStatus("Snapshot requested, attempting to restart camera...");
        try
        {
            await TryQuickCameraRestartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to restart camera for snapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to quickly restart the camera via MQTT using existing connection.
    /// </summary>
    private async Task<bool> TryQuickCameraRestartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Config.DeviceId) || string.IsNullOrEmpty(Config.ModelCode))
        {
            LogStatus("Quick restart: Missing device ID or model code");
            return false;
        }

        try
        {
            // Use existing MQTT controller if connected
            if (_mqttController != null && _mqttController.IsConnected)
            {
                LogStatus("Quick restart: Sending camera start command via existing MQTT connection...");
                var started = await _mqttController.TryStartCameraAsync(Config.DeviceId, Config.ModelCode, ct);

                if (started)
                {
                    LogStatus("Quick restart: Camera start command sent");
                    _mqttStatus.CameraStarted = true;
                    return true;
                }
            }
            else
            {
                // MQTT disconnected - try to reconnect
                LogStatus("Quick restart: MQTT disconnected, reconnecting...");

                if (string.IsNullOrEmpty(Config.MqttUsername) || string.IsNullOrEmpty(Config.MqttPassword))
                {
                    LogStatus("Quick restart: No MQTT credentials available");
                    return false;
                }

                if (_mqttController == null)
                {
                    _mqttController = new MqttCameraController();
                    _mqttController.MqttPort = Config.MqttPort;
                    _mqttController.StatusChanged += (s, msg) => LogStatus($"MQTT: {msg}");
                    _mqttController.ErrorOccurred += (s, ex) => LogStatus($"MQTT Error: {ex.Message}");
                    _mqttController.CameraStopDetected += OnExternalCameraStopDetected;
                    _mqttController.LedStatusReceived += OnLedStatusReceived;
                    _mqttController.PrinterStateReceived += OnPrinterStateReceived;
                }

                await _mqttController.ConnectAsync(Config.Ip, Config.MqttUsername, Config.MqttPassword, ct);
                await _mqttController.SubscribeToAllAsync(ct); // Subscribe to detect stop commands
                _mqttStatus.Connected = true;

                LogStatus("Quick restart: Sending camera start command...");
                var started = await _mqttController.TryStartCameraAsync(Config.DeviceId, Config.ModelCode, ct);

                if (started)
                {
                    LogStatus("Quick restart: Camera start command sent");
                    _mqttStatus.CameraStarted = true;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogStatus($"Quick restart failed: {ex.Message}");
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

    private async Task CleanupServicesAsync()
    {
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
