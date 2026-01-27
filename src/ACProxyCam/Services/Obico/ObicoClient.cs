// ObicoClient.cs - Main orchestrator for Obico integration per printer

using ACProxyCam.Models;
using System.Text.Json.Nodes;

namespace ACProxyCam.Services.Obico;

/// <summary>
/// Orchestrates Obico integration for a single printer.
/// Bridges Moonraker API with Obico server communication.
/// </summary>
public class ObicoClient : IDisposable
{
    private readonly PrinterConfig _printerConfig;
    private readonly MoonrakerApiClient _moonraker;
    private ObicoServerConnection? _obicoServer;

    private CancellationTokenSource? _cts;
    private Task? _statusUpdateTask;
    private Task? _snapshotTask;
    private Task? _reconnectionTask;
    private volatile bool _reconnecting;

    private volatile bool _isRunning;
    private volatile bool _isDisposed;

    // Janus WebRTC streaming
    private JanusClient? _janusClient;
    private JanusStreamer? _janusStreamer;      // MJPEG mode
    private H264RtpStreamer? _h264Streamer;     // H.264 mode
    private volatile bool _janusEnabled;
    private StreamingConfig? _serverStreamingConfig;

    // Reconnection settings
    private const int ReconnectDelaySeconds = 5;
    private const int MaxReconnectAttempts = 10;

    // Snapshot upload intervals (in seconds)
    // Cloud has rate limits, local/self-hosted does not
    private const double CloudFreeSnapshotInterval = 15.0;   // Cloud free tier
    private const double CloudProSnapshotInterval = 5.0;     // Cloud pro tier
    private const double LocalIdleSnapshotInterval = 1.0;    // Local server, not viewing
    private const double LocalViewingSnapshotInterval = 0.2; // Local server, user viewing (5 FPS)

    // Cached printer state
    private ObicoStatusUpdate _currentStatus = new();
    private readonly object _statusLock = new();
    private long? _currentPrintTs;
    private string? _currentFilename;
    private DateTime? _printStartTime;
    private int? _estimatedTotalTime;  // Total estimated print time in seconds

    // Viewing state - when user is actively watching the stream
    private volatile bool _isUserViewing;
    private DateTime _lastViewingUpdate = DateTime.MinValue;

    // Printer offline tracking - when Moonraker is disconnected
    private volatile bool _printerOffline;
    private DateTime _lastOfflineLogTime = DateTime.MinValue;

    // Verbose logging (set via ACPROXYCAM_VERBOSE=1 environment variable)
    private readonly bool _verbose;

    // Snapshot callback (set by PrinterThread if camera is enabled)
    private Func<byte[]?>? _getSnapshotCallback;

    // Decoder for H.264 packet sharing (set by PrinterThread if camera is enabled)
    private FfmpegDecoder? _ffmpegDecoder;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<ObicoClientState>? StateChanged;
    public event EventHandler? ConfigUpdated;
    /// <summary>
    /// Fired when Janus WebRTC viewer connects/disconnects. True = viewer connected, False = disconnected.
    /// Used to track actual streaming clients for keepalive and encoding rate control.
    /// </summary>
    public event EventHandler<bool>? JanusStreamingChanged;

    /// <summary>
    /// Fired when native Anycubic firmware needs to be synced (e.g., after print cancel from Obico).
    /// The handler should send the MQTT stop command to sync the native firmware state.
    /// </summary>
    public event EventHandler? NativePrintStopRequested;

    public ObicoClientState State { get; private set; } = ObicoClientState.Stopped;
    public bool IsLinked => _printerConfig.Obico.IsLinked;
    public string PrinterName => _printerConfig.Name;

    /// <summary>
    /// Sets the printer offline state. Called by PrinterThread when printer becomes unavailable.
    /// This suppresses Obico logging and status updates until the printer reconnects.
    /// </summary>
    public void SetPrinterOffline(bool offline)
    {
        if (_printerOffline == offline)
            return;

        _printerOffline = offline;

        if (offline)
        {
            Log("Printer marked offline - stopping Janus and suppressing status updates");

            // Stop Janus streaming when going offline
            _ = Task.Run(async () =>
            {
                try
                {
                    await StopJanusAsync();
                }
                catch (Exception ex)
                {
                    Log($"Error stopping Janus: {ex.Message}");
                }
            });

            // Update status to show printer offline
            lock (_statusLock)
            {
                _currentStatus.Status.State.Text = "Offline";
                _currentStatus.Status.State.Flags.Operational = false;
                _currentStatus.Status.State.Flags.Ready = false;
                _currentStatus.Status.State.Flags.ClosedOrError = true;
                _currentStatus.Status.State.Flags.Printing = false;
                _currentStatus.Status.State.Flags.Paused = false;
            }

            // Send offline status to Obico immediately
            SendStatusUpdate(force: true);
        }
        else
        {
            Log("Printer marked online - resuming status updates and restarting Janus");

            // Clear the offline-specific state but don't override actual printer state
            // The actual state (Operational, Error, etc.) should come from Moonraker status updates
            // Only clear the "Offline" text if that's what was set
            lock (_statusLock)
            {
                if (_currentStatus.Status.State.Text == "Offline")
                {
                    _currentStatus.Status.State.Text = "Operational";
                    _currentStatus.Status.State.Flags.Operational = true;
                    _currentStatus.Status.State.Flags.ClosedOrError = false;
                }
            }

            // Send status update to Obico to clear offline state
            SendStatusUpdate(force: true);

            // Restart Janus streaming when printer comes back online
            if (_printerConfig.CameraEnabled && _printerConfig.Obico.Enabled && _janusEnabled)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Small delay to let stream stabilize
                        await Task.Delay(2000);
                        if (!_printerOffline && _isRunning)
                        {
                            await StartJanusAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error restarting Janus: {ex.Message}");
                    }
                });
            }
        }
    }

    public ObicoClient(PrinterConfig printerConfig)
    {
        _printerConfig = printerConfig;
        _moonraker = new MoonrakerApiClient(printerConfig.Ip, printerConfig.Firmware.MoonrakerPort);
        _verbose = Environment.GetEnvironmentVariable("ACPROXYCAM_VERBOSE") == "1";

        // Wire up Moonraker events
        _moonraker.StatusUpdateReceived += OnMoonrakerStatusUpdate;
        _moonraker.KlippyStateChanged += OnKlippyStateChanged;
        _moonraker.PrintStateChanged += OnPrintStateChanged;
        _moonraker.ConnectionStateChanged += OnMoonrakerConnectionChanged;
    }

    /// <summary>
    /// Set callback to get snapshot from camera (if camera proxy is enabled).
    /// </summary>
    public void SetSnapshotCallback(Func<byte[]?> callback)
    {
        _getSnapshotCallback = callback;
    }

    /// <summary>
    /// Set the FfmpegDecoder for H.264 RTP streaming (shares source stream with MJPEG decoder).
    /// If Janus is already connected and H.264 mode is configured, starts RTP streaming.
    /// </summary>
    public async void SetDecoder(FfmpegDecoder decoder)
    {
        _ffmpegDecoder = decoder;

        // If Janus is enabled but H.264 streamer not started (waiting for decoder), start it now
        if (_janusEnabled && _janusClient != null && _h264Streamer == null &&
            _printerConfig.Obico.StreamMode == ObicoStreamMode.H264)
        {
            try
            {
                Log("Decoder now available - starting H.264 RTP streaming");
                _h264Streamer = new H264RtpStreamer(
                    _ffmpegDecoder,
                    GetJanusServerAddress()!,
                    _janusClient.VideoPort);
                _h264Streamer.Verbose = _verbose;
                _h264Streamer.StatusChanged += (s, msg) => Log(msg);
                await _h264Streamer.StartAsync();
                Log($"H.264 streaming started (stream_id={_janusClient.StreamId}, video_port={_janusClient.VideoPort})");
            }
            catch (Exception ex)
            {
                Log($"Failed to start H.264 streaming: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Start the Obico client.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning)
            return;

        if (!_printerConfig.Obico.Enabled)
        {
            Log("Obico not enabled for this printer");
            return;
        }

        if (!_printerConfig.Firmware.MoonrakerAvailable)
        {
            Log("Moonraker not available - Obico requires Rinkhals firmware");
            SetState(ObicoClientState.Failed);
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;

        try
        {
            SetState(ObicoClientState.Connecting);

            // Connect to Moonraker first
            Log("Connecting to Moonraker...");
            await _moonraker.ConnectWebSocketAsync(_cts.Token);
            await _moonraker.SubscribeToObicoObjectsAsync();
            Log("Connected to Moonraker");

            // Initialize status from current state
            await InitializeStatusAsync();

            // Connect to Obico server if linked
            if (IsLinked)
            {
                await ConnectToObicoAsync();
            }
            else
            {
                Log("Printer not linked to Obico - waiting for linking");
                SetState(ObicoClientState.WaitingForLink);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to start: {ex.Message}");
            SetState(ObicoClientState.Failed);
            throw;
        }
    }

    /// <summary>
    /// Connect to Obico server.
    /// </summary>
    private async Task ConnectToObicoAsync()
    {
        if (!IsLinked)
            return;

        Log("Connecting to Obico server...");

        _obicoServer?.Dispose();
        _obicoServer = new ObicoServerConnection(
            _printerConfig.Obico.ServerUrl,
            _printerConfig.Obico.AuthToken);

        // Enable verbose logging via environment variable (e.g., for systemd troubleshooting)
        _obicoServer.Verbose = Environment.GetEnvironmentVariable("ACPROXYCAM_VERBOSE") == "1";

        _obicoServer.PassthruCommandReceived += OnPassthruCommand;
        _obicoServer.ConnectionStateChanged += OnObicoConnectionChanged;
        _obicoServer.RemoteStatusReceived += OnRemoteStatusReceived;
        _obicoServer.JanusMessageReceived += OnJanusMessageFromServer;
        _obicoServer.StreamingConfigReceived += OnStreamingConfigReceived;
        _obicoServer.CommandReceived += OnObicoCommand;

        try
        {
            await _obicoServer.ConnectAsync(_cts!.Token);

            // Verify tier status and update FPS if needed
            await VerifyTierAndAdjustFpsAsync();

            // Start Janus for WebRTC streaming if camera is enabled
            // Note: Obico server doesn't send streaming config - we use configured Janus server
            if (_printerConfig.CameraEnabled && _getSnapshotCallback != null)
            {
                await StartJanusAsync();
            }

            // Send initial status with settings AFTER Janus is started
            // This ensures stream_id is included in webcam settings
            SendStatusUpdate(includeSettings: true);

            // Start background tasks
            _statusUpdateTask = Task.Run(() => StatusUpdateLoopAsync(_cts!.Token), _cts!.Token);

            if (_printerConfig.Obico.SnapshotsEnabled && _printerConfig.CameraEnabled)
            {
                _snapshotTask = Task.Run(() => SnapshotUploadLoopAsync(_cts!.Token), _cts!.Token);
            }

            Log("Connected to Obico server");
            SetState(ObicoClientState.Running);
        }
        catch (Exception ex)
        {
            Log($"Failed to connect to Obico: {ex.Message}");
            SetState(ObicoClientState.Reconnecting);
            // Will retry via reconnection logic
        }
    }

    /// <summary>
    /// Start Janus WebRTC gateway for real-time streaming.
    /// For self-hosted Obico: connects to Janus on the Obico server.
    /// For cloud Obico: Janus is not available (cloud handles streaming internally).
    /// </summary>
    private async Task StartJanusAsync()
    {
        try
        {
            // Cloud Obico (app.obico.io) handles WebRTC internally - no Janus needed
            if (IsCloudServer())
            {
                Log("Cloud Obico - streaming handled by cloud infrastructure");
                _janusEnabled = false;
                return;
            }

            // Check if Janus is explicitly disabled in config
            var janusServer = GetJanusServerAddress();
            if (string.IsNullOrEmpty(janusServer) || janusServer.Equals("disabled", StringComparison.OrdinalIgnoreCase))
            {
                Log("Janus streaming disabled by configuration");
                _janusEnabled = false;
                return;
            }

            var streamMode = _printerConfig.Obico.StreamMode;
            Log($"Starting Janus {streamMode} streaming to {janusServer}...");

            // Create Janus client with appropriate streaming mode
            _janusClient = new JanusClient(janusServer, streamMode);
            _janusClient.Verbose = _verbose;
            _janusClient.StatusChanged += (s, msg) => Log(msg);
            _janusClient.JanusMessageReceived += OnJanusMessageFromJanus;
            _janusClient.WebRtcStateChanged += OnWebRtcStateChanged;

            await _janusClient.StartAsync(_cts!.Token);

            // Start the appropriate streamer based on mode
            if (streamMode == ObicoStreamMode.H264)
            {
                // H.264 passthrough mode - use packets from shared FfmpegDecoder
                if (_ffmpegDecoder == null)
                {
                    // Decoder not available yet - streaming will be started when SetDecoder is called
                    Log("H.264 mode ready - waiting for decoder (will start when stream begins)");
                }
                else
                {
                    _h264Streamer = new H264RtpStreamer(
                        _ffmpegDecoder,
                        janusServer,
                        _janusClient.VideoPort);
                    _h264Streamer.Verbose = _verbose;
                    _h264Streamer.StatusChanged += (s, msg) => Log(msg);
                    await _h264Streamer.StartAsync();

                    Log($"H.264 streaming enabled (server={janusServer}, stream_id={_janusClient.StreamId}, video_port={_janusClient.VideoPort})");
                }
            }
            else
            {
                // MJPEG mode - base64 encoded JPEG over data channel
                var targetFps = _printerConfig.MaxFps > 0 ? _printerConfig.MaxFps : 25;
                _janusStreamer = new JanusStreamer(
                    janusServer,
                    _janusClient.DataPort,
                    targetFps,
                    _getSnapshotCallback!);
                _janusStreamer.StatusChanged += (s, msg) => Log(msg);
                _janusStreamer.Start();

                Log($"MJPEG streaming enabled (server={janusServer}, stream_id={_janusClient.StreamId}, data_port={_janusClient.DataPort})");
            }

            _janusEnabled = true;
            // Note: JanusStreamingChanged is fired in OnWebRtcStateChanged when a viewer actually connects
        }
        catch (Exception ex)
        {
            Log($"Failed to start Janus: {ex.Message}");
            Log("Real-time streaming will be unavailable, falling back to snapshot mode");
            _janusEnabled = false;
            JanusStreamingChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Get Janus server address from config, or extract from Obico server URL.
    /// </summary>
    private string? GetJanusServerAddress()
    {
        // Use configured Janus server if set
        var configuredServer = _printerConfig.Obico.JanusServer;
        if (!string.IsNullOrEmpty(configuredServer))
        {
            return configuredServer;
        }

        // Default: extract host from Obico server URL (assumes Janus is on same server)
        try
        {
            var serverUrl = _printerConfig.Obico.ServerUrl;
            if (!string.IsNullOrEmpty(serverUrl))
            {
                var uri = new Uri(serverUrl);
                return uri.Host;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Stop Janus WebRTC gateway.
    /// </summary>
    private async Task StopJanusAsync()
    {
        var wasEnabled = _janusEnabled;
        _janusEnabled = false;

        // Stop H.264 streamer if running
        if (_h264Streamer != null)
        {
            await _h264Streamer.StopAsync();
            _h264Streamer.Dispose();
            _h264Streamer = null;
        }

        // Stop MJPEG streamer if running
        if (_janusStreamer != null)
        {
            await _janusStreamer.StopAsync();
            _janusStreamer.Dispose();
            _janusStreamer = null;
        }

        if (_janusClient != null)
        {
            await _janusClient.StopAsync();
            _janusClient.Dispose();
            _janusClient = null;
        }

        if (wasEnabled)
        {
            JanusStreamingChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Handle Janus signaling message from Obico server (browser → Janus relay).
    /// The Obico server sends browser WebRTC signaling messages that need to be forwarded to Janus.
    /// This is required because the agent acts as a relay between Obico server and Janus.
    /// </summary>
    private async void OnJanusMessageFromServer(object? sender, string janusJson)
    {
        // Skip Janus relay when printer is offline - no point relaying if stream is down
        if (_printerOffline)
            return;

        if (_janusClient == null)
        {
            Log("Cannot relay Janus message - Janus client not connected");
            return;
        }

        try
        {
            // Relay the message to Janus as-is
            // The browser creates its own Janus session via this relay
            if (_verbose)
                Log($"Relaying Janus message from Obico to Janus: {janusJson.Substring(0, Math.Min(100, janusJson.Length))}...");
            await _janusClient.SendToJanusAsync(janusJson, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"Failed to relay Janus message: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle WebRTC state changes from Janus client.
    /// Fires JanusStreamingChanged to track actual viewer connections.
    /// For MJPEG mode: also pauses/resumes streaming to avoid broken frames during browser reconnection.
    /// </summary>
    private void OnWebRtcStateChanged(object? sender, bool webRtcUp)
    {
        // Notify that viewer connected/disconnected (for client tracking and keepalive logic)
        JanusStreamingChanged?.Invoke(this, webRtcUp);

        // Only pause/resume MJPEG streamer - H.264 streamer runs independently via FFmpeg
        if (_janusStreamer != null)
        {
            if (webRtcUp)
            {
                _janusStreamer.Resume();
            }
            else
            {
                _janusStreamer.Pause();
            }
        }
    }

    /// <summary>
    /// Handle Janus message from local Janus (to be relayed to Obico server).
    /// </summary>
    private void OnJanusMessageFromJanus(object? sender, System.Text.Json.Nodes.JsonNode janusMsg)
    {
        // Skip relay when printer is offline
        if (_printerOffline)
            return;

        if (_obicoServer?.IsConnected == true)
        {
            if (_verbose)
                Log($"Relaying Janus message from local Janus to Obico");
            _obicoServer.SendJanusMessage(janusMsg.ToJsonString());
        }
    }

    /// <summary>
    /// Stop the Obico client.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _isRunning = false;
        _cts?.Cancel();

        try
        {
            if (_statusUpdateTask != null)
                await _statusUpdateTask;
            if (_snapshotTask != null)
                await _snapshotTask;
            if (_reconnectionTask != null)
                await _reconnectionTask;
        }
        catch (OperationCanceledException) { }

        // Stop Janus streaming
        await StopJanusAsync();

        await _moonraker.DisconnectWebSocketAsync();

        if (_obicoServer != null)
        {
            await _obicoServer.DisconnectAsync();
        }

        SetState(ObicoClientState.Stopped);
        Log("Stopped");
    }

    /// <summary>
    /// Initialize status from current Moonraker state.
    /// </summary>
    private async Task InitializeStatusAsync()
    {
        try
        {
            var serverInfo = await _moonraker.GetServerInfoAsync();
            var klippyState = serverInfo?.KlippyState ?? "disconnected";

            // Query current printer objects including total_time for ETA, layer info, Z position
            var objects = new Dictionary<string, string[]?>
            {
                ["print_stats"] = null,  // Get all fields including info.current_layer
                ["extruder"] = new[] { "temperature", "target" },
                ["heater_bed"] = new[] { "temperature", "target" },
                ["gcode_move"] = null,  // Get all fields including gcode_position
                ["virtual_sdcard"] = null  // Get all fields including total_time, current_layer
            };

            var status = await _moonraker.QueryPrinterObjectsAsync(objects);
            var statusData = status?["status"];

            lock (_statusLock)
            {
                UpdateStatusFromMoonraker(statusData);

                // Only override Ready to false when klippy is not ready
                // (UpdateStatusFromMoonraker already set Ready based on print_stats.state == "standby")
                // Don't set Ready=true here - that would override the correct false value when printing!
                if (klippyState != "ready")
                {
                    _currentStatus.Status.State.Flags.Ready = false;
                }
                _currentStatus.Status.State.Flags.Error = klippyState == "error";
                _currentStatus.Status.State.Flags.ClosedOrError = klippyState != "ready";

                // Override State.Text based on klippy state when in error
                if (klippyState == "error")
                {
                    _currentStatus.Status.State.Text = "Error";
                }
            }

            // Check if a print is already in progress (e.g., service restart during print)
            var printStats = statusData?["print_stats"];
            if (printStats != null)
            {
                var state = printStats["state"]?.GetValue<string>();
                var filename = printStats["filename"]?.GetValue<string>();

                if (state == "printing" || state == "paused")
                {
                    // First, try to load saved print timestamp from previous session
                    // This ensures we use the same timestamp across service restarts
                    // IMPORTANT: We trust the saved timestamp when filename matches because:
                    // 1. Obico server uses timestamp as ext_id to identify prints
                    // 2. Moonraker on Anycubic changes start_time when pause/resume occurs
                    // 3. If we recalculate, we get a different timestamp that Obico won't recognize
                    var (savedFilename, savedTimestamp) = await LoadPrintStateAsync();
                    var printDuration = printStats["print_duration"]?.GetValue<double>() ?? 0;
                    var useSavedState = false;

                    if (savedTimestamp.HasValue && savedFilename == filename)
                    {
                        // Same filename - trust the saved timestamp
                        // Only reject if the print clearly just started (duration < 60s means it's a fresh print)
                        if (printDuration < 60)
                        {
                            // Fresh print with same filename - this is a new print, not continuation
                            Log($"Saved timestamp rejected (print just started: {printDuration:F0}s), calculating new timestamp");
                        }
                        else
                        {
                            // Ongoing print - use saved timestamp to maintain Obico tracking
                            _currentPrintTs = savedTimestamp.Value;
                            _printStartTime = DateTimeOffset.FromUnixTimeSeconds(savedTimestamp.Value).UtcDateTime;
                            Log($"Using saved print timestamp: {savedTimestamp.Value} for {filename} (print_duration={printDuration:F0}s)");
                            useSavedState = true;
                        }
                    }

                    if (!useSavedState)
                    {
                        // No saved state, different filename, or fresh print - calculate from Moonraker
                        var printStartTs = await FetchPrintStartTimeAsync();
                        if (printStartTs.HasValue)
                        {
                            _currentPrintTs = printStartTs.Value;
                            _printStartTime = DateTimeOffset.FromUnixTimeSeconds(printStartTs.Value).UtcDateTime;
                            // Save for future restarts
                            await SavePrintStateAsync(filename!, printStartTs.Value);
                        }
                        else
                        {
                            // Fallback to current time if job history unavailable (shouldn't happen)
                            Log("Warning: Could not fetch print start time from job history, using current time");
                            _currentPrintTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            _printStartTime = DateTime.UtcNow;
                            await SavePrintStateAsync(filename!, _currentPrintTs.Value);
                        }
                    }
                    _currentFilename = filename;

                    // Fetch estimated total time
                    var totalTime = statusData?["virtual_sdcard"]?["total_time"]?.GetValue<int>();
                    if (totalTime.HasValue && totalTime.Value > 0)
                    {
                        _estimatedTotalTime = totalTime.Value;
                        Log($"Estimated total print time: {_estimatedTotalTime / 60} minutes");
                    }

                    // Fetch file metadata for maxZ display
                    await FetchFileMetadataAsync();

                    // Update the status object with the print timestamp and job info
                    // This must be done after setting _currentPrintTs since UpdateStatusFromMoonraker
                    // was called before we detected the ongoing print
                    lock (_statusLock)
                    {
                        _currentStatus.CurrentPrintTs = _currentPrintTs;

                        // Set job info with filename - this is required by Obico server
                        // The server will reject the print if filename is missing
                        if (!string.IsNullOrEmpty(_currentFilename))
                        {
                            _currentStatus.Status.Job = new ObicoJob
                            {
                                File = new ObicoJobFile
                                {
                                    Name = _currentFilename,
                                    Display = _currentFilename,
                                    Path = _currentFilename
                                }
                            };
                        }
                    }

                    Log($"Detected ongoing print: {filename} (state: {state})");
                }
                else
                {
                    // Clear any stale print state from previous session
                    // This handles the case where printer was rebooted during a print
                    if (_currentPrintTs.HasValue)
                    {
                        Log($"Clearing stale print state (current state: {state})");
                    }
                    _currentPrintTs = null;
                    _printStartTime = null;
                    _currentFilename = null;
                    _estimatedTotalTime = null;
                    ClearPrintState();  // Clear persisted state file

                    // Also clear job info and progress in status
                    lock (_statusLock)
                    {
                        _currentStatus.Status.Job = null;
                        _currentStatus.Status.FileMetadata = null;
                        _currentStatus.Status.CurrentLayer = null;
                        _currentStatus.Status.CurrentZ = null;
                        _currentStatus.Status.Progress = new ObicoProgress();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to initialize status: {ex.Message}");
        }
    }

    #region Event Handlers

    private void OnMoonrakerStatusUpdate(object? sender, JsonNode status)
    {
        lock (_statusLock)
        {
            UpdateStatusFromMoonraker(status);
        }
    }

    private void UpdateStatusFromMoonraker(JsonNode? status)
    {
        if (status == null)
            return;

        // Update temperatures - only update fields that are actually present
        // This preserves previous values when Moonraker sends partial updates
        var extruder = status["extruder"];
        if (extruder != null)
        {
            var tempNode = extruder["temperature"];
            var targetNode = extruder["target"];

            if (tempNode != null)
                _currentStatus.Status.Temps.Tool0.Actual = tempNode.GetValue<double>();
            if (targetNode != null)
                _currentStatus.Status.Temps.Tool0.Target = targetNode.GetValue<double>();
        }

        var bed = status["heater_bed"];
        if (bed != null)
        {
            var tempNode = bed["temperature"];
            var targetNode = bed["target"];

            if (tempNode != null)
                _currentStatus.Status.Temps.Bed.Actual = tempNode.GetValue<double>();
            if (targetNode != null)
                _currentStatus.Status.Temps.Bed.Target = targetNode.GetValue<double>();
        }

        // Update print progress
        var printStats = status["print_stats"];
        if (printStats != null)
        {
            var stateNode = printStats["state"];
            if (stateNode != null)
            {
                var state = stateNode.GetValue<string>();
                _currentStatus.Status.State.Text = MapPrintState(state);
                _currentStatus.Status.State.Flags.Printing = state == "printing";
                _currentStatus.Status.State.Flags.Paused = state == "paused";

                // Set/clear error flags based on print_stats state
                // (error can occur from thermal runaway, filament error, etc.)
                if (state == "error")
                {
                    _currentStatus.Status.State.Flags.Error = true;
                    _currentStatus.Status.State.Flags.ClosedOrError = true;
                    _currentStatus.Status.State.Flags.Ready = false;
                    _currentStatus.Status.State.Flags.Operational = false;
                }
                else
                {
                    // Clear error flags and set operational when not in error
                    _currentStatus.Status.State.Flags.Error = false;
                    _currentStatus.Status.State.Flags.ClosedOrError = false;
                    // Ready = true for all idle states (standby, complete, cancelled)
                    // This matches moonraker-obico: ready when STATE_OPERATIONAL
                    _currentStatus.Status.State.Flags.Ready = state == "standby" || state == "complete" || state == "cancelled";
                    _currentStatus.Status.State.Flags.Operational = true;
                }
            }

            var filenameNode = printStats["filename"];
            if (filenameNode != null)
            {
                var filename = filenameNode.GetValue<string>();
                if (!string.IsNullOrEmpty(filename) && filename != _currentFilename)
                {
                    _currentFilename = filename;
                }
            }

            // Update job info with filename only when actively printing
            // (don't show job info when idle even if Moonraker has last printed filename)
            // NOTE: Use _currentStatus.CurrentPrintTs (set inside lock) instead of _currentPrintTs
            // to avoid race condition where _currentPrintTs visibility is not guaranteed
            if (!string.IsNullOrEmpty(_currentFilename) && _currentStatus.CurrentPrintTs.HasValue)
            {
                _currentStatus.Status.Job = new ObicoJob
                {
                    File = new ObicoJobFile
                    {
                        Name = _currentFilename,
                        Display = _currentFilename,
                        Path = _currentFilename
                    }
                };
            }
            else
            {
                _currentStatus.Status.Job = null;
            }

            // total_duration = total elapsed time (including pauses, heating)
            // print_duration = actual printing time (used for remaining time calculation)
            // Use print_duration for time remaining calculation since the estimate is based on printing time only
            var printDurationNode = printStats["print_duration"];
            if (printDurationNode != null)
            {
                _currentStatus.Status.Progress.PrintTime = (int)printDurationNode.GetValue<double>();
            }

            // Update layer info from print_stats.info
            var infoNode = printStats["info"];
            if (infoNode != null)
            {
                var currentLayer = infoNode["current_layer"]?.GetValue<int>();
                var totalLayer = infoNode["total_layer"]?.GetValue<int>();
                if (currentLayer.HasValue)
                    _currentStatus.Status.CurrentLayer = currentLayer.Value;
                if (totalLayer.HasValue)
                    SetTotalLayerCount(totalLayer.Value);
            }
        }

        // Update virtual_sdcard progress
        var vsd = status["virtual_sdcard"];
        if (vsd != null)
        {
            // Only update completion when progress is actually present in the update
            // WebSocket updates may not include all fields every time
            var progressNode = vsd["progress"];
            if (progressNode != null)
            {
                var progress = progressNode.GetValue<double>();
                _currentStatus.Status.Progress.Completion = progress * 100;
            }

            var filePosNode = vsd["file_position"];
            if (filePosNode != null)
                _currentStatus.Status.Progress.FilePos = filePosNode.GetValue<long>();

            // Get total estimated time from WebSocket update or use cached value
            var totalTime = vsd["total_time"]?.GetValue<int>() ?? _estimatedTotalTime;
            if (totalTime.HasValue && totalTime.Value > 0)
            {
                _estimatedTotalTime = totalTime.Value;  // Update cached value if available
            }

            // If we're printing but don't have estimated time yet, fetch it
            if (_currentStatus.Status.State.Flags.Printing && !_estimatedTotalTime.HasValue)
            {
                _ = FetchEstimatedTotalTimeAsync();  // Fire and forget, will be available on next update
            }

            // Calculate remaining time
            if (_estimatedTotalTime.HasValue && _currentStatus.Status.Progress.PrintTime.HasValue)
            {
                var elapsed = _currentStatus.Status.Progress.PrintTime.Value;
                var estimated = _estimatedTotalTime.Value;

                // Only adjust estimate if elapsed time exceeds original estimate (print running long)
                // Don't project from early progress - at low % the projection is unreliable
                // because it includes heating, homing, first layer calibration, etc.
                if (elapsed > estimated)
                {
                    // Print has exceeded original estimate - use projection if we have enough data
                    if (_currentStatus.Status.Progress.Completion.HasValue)
                    {
                        var progress = _currentStatus.Status.Progress.Completion.Value / 100.0;
                        if (progress > 0.10) // Only trust projection at 10%+ progress
                        {
                            var projectedEstimate = (int)(elapsed / progress);
                            estimated = projectedEstimate;
                            _estimatedTotalTime = estimated;
                        }
                        else
                        {
                            // Not enough progress for reliable projection, just extend a bit
                            estimated = elapsed + 60; // Add 1 minute buffer
                            _estimatedTotalTime = estimated;
                        }
                    }
                }

                var remaining = estimated - elapsed;
                _currentStatus.Status.Progress.PrintTimeLeft = remaining > 0 ? remaining : 0;
            }

            // Also get layer info from virtual_sdcard (always update if available)
            var currentLayerVsd = vsd["current_layer"]?.GetValue<int>();
            if (currentLayerVsd.HasValue)
                _currentStatus.Status.CurrentLayer = currentLayerVsd.Value;

            var totalLayerVsd = vsd["total_layer"]?.GetValue<int>();
            if (totalLayerVsd.HasValue)
                SetTotalLayerCount(totalLayerVsd.Value);
        }

        // Update Z position
        var gcodeMove = status["gcode_move"];
        if (gcodeMove != null)
        {
            var position = gcodeMove["gcode_position"];
            if (position is JsonArray posArray && posArray.Count >= 3)
            {
                _currentStatus.Status.CurrentZ = posArray[2]?.GetValue<double>();
            }
        }

        // Periodically fetch layer/Z info via REST while printing
        // WebSocket updates don't always include these fields, so we fetch them separately
        if (_currentStatus.Status.State.Flags.Printing || _currentStatus.Status.State.Flags.Paused)
        {
            _ = RefreshLayerAndZInfoAsync();  // Fire and forget
        }

        // Update timestamp
        _currentStatus.Status.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Only sync _currentStatus.CurrentPrintTs when _currentPrintTs has a valid value
        // This prevents race conditions where _currentPrintTs visibility is not guaranteed
        // (e.g., set by InitializeStatusAsync outside the lock but not yet visible to this thread)
        // The authoritative set happens in InitializeStatusAsync and HandlePrintStateChangeAsync
        if (_currentPrintTs.HasValue)
        {
            _currentStatus.CurrentPrintTs = _currentPrintTs;
        }
    }

    private string MapPrintState(string moonrakerState)
    {
        return moonrakerState switch
        {
            "printing" => "Printing",
            "paused" => "Paused",
            "complete" => "Operational",
            "cancelled" => "Operational",
            "error" => "Error",
            "standby" => "Operational",
            _ => "Operational"
        };
    }

    private void OnKlippyStateChanged(object? sender, string state)
    {
        lock (_statusLock)
        {
            // Only override Ready to false when klippy is not ready
            // Don't set Ready=true here - let UpdateStatusFromMoonraker handle it based on print_stats.state
            if (state != "ready")
            {
                _currentStatus.Status.State.Flags.Ready = false;
            }
            _currentStatus.Status.State.Flags.Error = state == "error";
            _currentStatus.Status.State.Flags.ClosedOrError = state != "ready";

            // Update State.Text based on klippy state
            if (state == "error")
            {
                _currentStatus.Status.State.Text = "Error";
            }
            else if (state == "ready" && !_currentStatus.Status.State.Flags.Printing)
            {
                _currentStatus.Status.State.Text = "Operational";
            }
        }

        Log($"Klippy state: {state}");
    }

    private async void OnPrintStateChanged(object? sender, PrintStateEventArgs e)
    {
        var previouslyPrinting = _currentPrintTs.HasValue;
        var nowPrinting = e.State == "printing";

        // Detect print start
        if (!previouslyPrinting && nowPrinting)
        {
            _currentPrintTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _printStartTime = DateTime.UtcNow;
            _currentFilename = e.Filename;

            // If filename is empty, fetch it from Moonraker (state change may arrive before filename)
            if (string.IsNullOrEmpty(_currentFilename))
            {
                try
                {
                    var result = await _moonraker.GetAsync<JsonNode>("/printer/objects/query?print_stats=filename");
                    _currentFilename = result?["status"]?["print_stats"]?["filename"]?.GetValue<string>();
                }
                catch (Exception ex)
                {
                    Log($"Failed to fetch filename: {ex.Message}");
                }
            }

            // Fetch estimated total time from virtual_sdcard
            await FetchEstimatedTotalTimeAsync();

            // Fetch file metadata for maxZ display
            await FetchFileMetadataAsync();

            SendPrintEvent("PrintStarted");
            Log($"Print started: {_currentFilename}");
        }
        // Detect print end
        else if (previouslyPrinting && (e.State == "complete" || e.State == "cancelled" || e.State == "error"))
        {
            var eventType = e.State switch
            {
                "complete" => "PrintDone",
                "cancelled" => "PrintCancelled",
                "error" => "PrintFailed",
                _ => "PrintDone"
            };

            SendPrintEvent(eventType);
            Log($"Print {eventType}: {_currentFilename}");

            // Clear print state BEFORE sending status update
            _currentPrintTs = null;
            _printStartTime = null;
            _currentFilename = null;
            _estimatedTotalTime = null;
            ClearPrintState();  // Clear persisted state file

            // Update state flags immediately to ensure Obico UI shows correct state
            lock (_statusLock)
            {
                _currentStatus.Status.State.Text = "Operational";
                _currentStatus.Status.State.Flags.Printing = false;
                _currentStatus.Status.State.Flags.Paused = false;
                _currentStatus.Status.State.Flags.Ready = true;
                _currentStatus.Status.CurrentLayer = null;
                _currentStatus.Status.CurrentZ = null;
                _currentStatus.Status.Job = null;
                _currentStatus.Status.FileMetadata = null;
                _currentStatus.Status.Progress = new ObicoProgress(); // Reset progress
            }

            // Send status update immediately to clear printing state in Obico UI
            SendStatusUpdate(force: true);

            // When cancelled, notify to sync native Anycubic firmware
            if (e.State == "cancelled")
            {
                Log("Requesting native firmware sync for print cancellation");
                NativePrintStopRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        // Detect pause/resume
        else if (e.State == "paused")
        {
            SendPrintEvent("PrintPaused");
            Log("Print paused");
        }
        else if (previouslyPrinting && e.State == "printing")
        {
            // Re-fetch estimated time if not set (can happen after service restart during pause)
            if (!_estimatedTotalTime.HasValue)
            {
                await FetchEstimatedTotalTimeAsync();
            }

            SendPrintEvent("PrintResumed");
            Log("Print resumed");
        }
    }

    private async Task FetchEstimatedTotalTimeAsync()
    {
        try
        {
            var result = await _moonraker.GetAsync<JsonNode>("/printer/objects/query?virtual_sdcard=total_time");
            var totalTime = result?["status"]?["virtual_sdcard"]?["total_time"]?.GetValue<int>();
            if (totalTime.HasValue && totalTime.Value > 0)
            {
                _estimatedTotalTime = totalTime.Value;
                Log($"Estimated total print time: {_estimatedTotalTime / 60} minutes");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to fetch estimated total time: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch the actual print start time from Moonraker's job history.
    /// This is critical for Obico integration - the server uses this timestamp as a unique identifier.
    ///
    /// For Anycubic/Rinkhals printers, Moonraker uses monotonic time (seconds since boot) instead of
    /// Unix epoch for timestamps. We convert this to Unix epoch by calculating the boot time.
    /// </summary>
    private async Task<long?> FetchPrintStartTimeAsync()
    {
        try
        {
            // Query the most recent job from Moonraker's history
            var result = await _moonraker.GetAsync<JsonNode>("/server/history/list?order=desc&limit=1");
            var jobs = result?["jobs"]?.AsArray();
            if (jobs != null && jobs.Count > 0)
            {
                var job = jobs[0];
                var startTime = job?["start_time"]?.GetValue<double>();
                if (startTime.HasValue && startTime.Value > 0)
                {
                    // Check if this looks like a relative time (monotonic/uptime) rather than Unix epoch
                    // Unix timestamps for 2020+ are > 1577836800, relative times would be much smaller
                    if (startTime.Value < 100000000) // Less than ~3 years in seconds
                    {
                        // This is likely monotonic time (seconds since boot), not Unix epoch
                        // Convert to Unix epoch using current time and eventtime (uptime)
                        var currentUptime = await GetCurrentUptimeAsync();
                        if (currentUptime.HasValue)
                        {
                            var bootTimeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)currentUptime.Value;
                            var printStartUnix = bootTimeUnix + (long)startTime.Value;
                            Log($"Print start time: {startTime.Value}s (uptime) -> {printStartUnix} (Unix epoch)");
                            return printStartUnix;
                        }
                        else
                        {
                            // Fallback: can't determine uptime, use relative time as-is
                            Log($"Warning: Could not convert relative start_time to Unix epoch, using as-is: {startTime.Value}");
                            return (long)startTime.Value;
                        }
                    }
                    else
                    {
                        // Already a Unix timestamp
                        Log($"Print start time from job history: {startTime.Value} (Unix timestamp)");
                        return (long)startTime.Value;
                    }
                }
            }
            Log("No jobs found in Moonraker history");
        }
        catch (Exception ex)
        {
            Log($"Failed to fetch print start time from job history: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Get current system uptime from Moonraker's eventtime.
    /// </summary>
    private async Task<double?> GetCurrentUptimeAsync()
    {
        try
        {
            var result = await _moonraker.GetAsync<JsonNode>("/printer/objects/query?print_stats");
            return result?["eventtime"]?.GetValue<double>();
        }
        catch
        {
            return null;
        }
    }

    #region Print State Persistence
    // Save/load print timestamp to survive service restarts
    // This ensures we send the same current_print_ts to Obico after restart

    private const string PrintStateDir = "/var/lib/acproxycam";

    private string GetPrintStateFilePath()
    {
        var safeName = _printerConfig.Name.Replace("/", "_").Replace("\\", "_");
        return Path.Combine(PrintStateDir, $"{safeName}_print.json");
    }

    private async Task SavePrintStateAsync(string filename, long timestamp)
    {
        try
        {
            if (!Directory.Exists(PrintStateDir))
                Directory.CreateDirectory(PrintStateDir);

            var state = new { filename, timestamp };
            var json = System.Text.Json.JsonSerializer.Serialize(state);
            await File.WriteAllTextAsync(GetPrintStateFilePath(), json);
            Log($"Saved print state: {filename} @ {timestamp}");
        }
        catch (Exception ex)
        {
            Log($"Failed to save print state: {ex.Message}");
        }
    }

    private async Task<(string? filename, long? timestamp)> LoadPrintStateAsync()
    {
        try
        {
            var path = GetPrintStateFilePath();
            if (!File.Exists(path))
                return (null, null);

            var json = await File.ReadAllTextAsync(path);
            var state = System.Text.Json.JsonSerializer.Deserialize<JsonNode>(json);
            var filename = state?["filename"]?.GetValue<string>();
            var timestamp = state?["timestamp"]?.GetValue<long>();
            return (filename, timestamp);
        }
        catch (Exception ex)
        {
            Log($"Failed to load print state: {ex.Message}");
            return (null, null);
        }
    }

    private void ClearPrintState()
    {
        try
        {
            var path = GetPrintStateFilePath();
            if (File.Exists(path))
            {
                File.Delete(path);
                Log("Cleared saved print state");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to clear print state: {ex.Message}");
        }
    }

    #endregion

    private DateTime _lastLayerZRefresh = DateTime.MinValue;

    private async Task RefreshLayerAndZInfoAsync()
    {
        // Throttle to once every 5 seconds
        if ((DateTime.UtcNow - _lastLayerZRefresh).TotalSeconds < 5)
            return;
        _lastLayerZRefresh = DateTime.UtcNow;

        try
        {
            // Include model_size_z in the query for maxZ updates (Anycubic printers populate this)
            var result = await _moonraker.GetAsync<JsonNode>(
                "/printer/objects/query?print_stats=info&virtual_sdcard=current_layer,total_layer,model_size_z&gcode_move=gcode_position");
            var status = result?["status"];
            if (status == null) return;

            lock (_statusLock)
            {
                // Get layer info
                var printStatsInfo = status["print_stats"]?["info"];
                if (printStatsInfo != null)
                {
                    var currentLayer = printStatsInfo["current_layer"]?.GetValue<int>();
                    var totalLayer = printStatsInfo["total_layer"]?.GetValue<int>();
                    if (currentLayer.HasValue)
                        _currentStatus.Status.CurrentLayer = currentLayer.Value;
                    if (totalLayer.HasValue)
                        SetTotalLayerCount(totalLayer.Value);
                }

                // Fallback to virtual_sdcard
                var vsd = status["virtual_sdcard"];
                if (vsd != null)
                {
                    if (_currentStatus.Status.CurrentLayer == null)
                    {
                        var currentLayer = vsd["current_layer"]?.GetValue<int>();
                        if (currentLayer.HasValue)
                            _currentStatus.Status.CurrentLayer = currentLayer.Value;
                    }
                    if (GetTotalLayerCount() == null)
                    {
                        var totalLayer = vsd["total_layer"]?.GetValue<int>();
                        if (totalLayer.HasValue)
                            SetTotalLayerCount(totalLayer.Value);
                    }

                    // Update maxZ from model_size_z if available and not already set
                    // This handles cases where the file metadata wasn't available at print start
                    // (e.g., files sent directly to printer via Anycubic slicer)
                    var modelSizeZ = vsd["model_size_z"]?.GetValue<double>();
                    if (modelSizeZ.HasValue && modelSizeZ.Value > 0)
                    {
                        var currentMaxZ = GetMaxZ();
                        if (currentMaxZ == null || currentMaxZ.Value <= 0)
                        {
                            SetMaxZ(modelSizeZ.Value);
                            Log($"Updated maxZ from virtual_sdcard: {modelSizeZ.Value:F2}mm");
                        }
                    }
                }

                // Get Z position
                var gcodeMove = status["gcode_move"];
                if (gcodeMove != null)
                {
                    var position = gcodeMove["gcode_position"];
                    if (position is JsonArray posArray && posArray.Count >= 3)
                    {
                        var zValue = posArray[2]?.GetValue<double>();
                        _currentStatus.Status.CurrentZ = zValue;
                        if (_verbose)
                            Log($"Z position updated: {zValue}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to refresh layer/Z info: {ex.Message}");
        }
    }

    private void OnMoonrakerConnectionChanged(object? sender, bool connected)
    {
        if (!connected && _isRunning)
        {
            Log("Moonraker disconnected - printer offline, will attempt reconnection");
            _printerOffline = true;

            // Update status to show printer offline
            lock (_statusLock)
            {
                _currentStatus.Status.State.Text = "Offline";
                _currentStatus.Status.State.Flags.Operational = false;
                _currentStatus.Status.State.Flags.Ready = false;
                _currentStatus.Status.State.Flags.ClosedOrError = true;
                _currentStatus.Status.State.Flags.Printing = false;
                _currentStatus.Status.State.Flags.Paused = false;
            }

            // Send offline status to Obico immediately
            SendStatusUpdate(force: true);

            SetState(ObicoClientState.Reconnecting);
            TriggerReconnection();
        }
        else if (connected && _printerOffline)
        {
            // Log only - the ReconnectionLoopAsync handles the full reconnection sequence:
            // InitializeStatusAsync, status flags reset, Obico status update, and Janus restart
            Log("Moonraker WebSocket reconnected - reconnection loop will complete initialization");
        }
    }

    private void OnObicoConnectionChanged(object? sender, bool connected)
    {
        if (!connected && _isRunning)
        {
            Log("Obico server disconnected - will attempt reconnection");
            SetState(ObicoClientState.Reconnecting);
            TriggerReconnection();
        }
    }

    private void OnRemoteStatusReceived(object? sender, RemoteStatus status)
    {
        var wasViewing = _isUserViewing;
        _isUserViewing = status.Viewing;
        _lastViewingUpdate = DateTime.UtcNow;

        if (wasViewing != status.Viewing)
        {
            var isCloud = IsCloudServer();
            if (status.Viewing)
            {
                Log($"User started viewing - boosting snapshot rate{(isCloud ? " (cloud rate limits apply)" : "")}");
            }
            else
            {
                Log("User stopped viewing - returning to idle snapshot rate");
            }
        }
    }

    private async void OnStreamingConfigReceived(object? sender, StreamingConfig config)
    {
        // Store the config for later use
        _serverStreamingConfig = config;

        Log($"Received streaming config from server: host={config.Host}, port={config.Port}, stream_id={config.StreamId}");

        // Start Janus if camera is enabled and we haven't started yet
        if (_printerConfig.CameraEnabled && _getSnapshotCallback != null && !_janusEnabled)
        {
            await StartJanusAsync();
        }
    }

    /// <summary>
    /// Handle commands from Obico server (AI-triggered pause/resume/cancel).
    /// </summary>
    private async void OnObicoCommand(object? sender, ObicoCommand command)
    {
        Log($"Received command: {command.Cmd} (initiator: {command.Initiator ?? "unknown"})");

        try
        {
            switch (command.Cmd.ToLower())
            {
                case "pause":
                    // AI-triggered pause with optional retraction/lift parameters
                    // For Klipper, we use the PAUSE macro which handles retraction internally
                    // If custom retraction is needed, we could send G-code before PAUSE
                    if (command.Retract.HasValue || command.LiftZ.HasValue)
                    {
                        Log($"Pause args: retract={command.Retract}mm, lift_z={command.LiftZ}mm, tools_off={command.ToolsOff}, bed_off={command.BedOff}");
                    }
                    await _moonraker.PauseAsync();

                    // Optionally turn off heaters if requested
                    if (command.ToolsOff == true)
                    {
                        await _moonraker.SetTemperatureAsync("extruder", 0);
                    }
                    if (command.BedOff == true)
                    {
                        await _moonraker.SetTemperatureAsync("heater_bed", 0);
                    }
                    break;

                case "resume":
                    await _moonraker.ResumeAsync();
                    break;

                case "cancel":
                    await _moonraker.CancelAsync();
                    break;

                default:
                    Log($"Unknown command: {command.Cmd}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log($"Command execution failed: {ex.Message}");
        }
    }

    private void TriggerReconnection()
    {
        if (_reconnecting || !_isRunning || _cts == null)
            return;

        _reconnecting = true;
        _reconnectionTask = Task.Run(() => ReconnectionLoopAsync(_cts.Token));
    }

    private async Task ReconnectionLoopAsync(CancellationToken ct)
    {
        var attempts = 0;

        while (!ct.IsCancellationRequested && _isRunning && attempts < MaxReconnectAttempts)
        {
            attempts++;
            Log($"Reconnection attempt {attempts}/{MaxReconnectAttempts}...");

            try
            {
                // Wait before reconnecting
                await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), ct);

                // Check if Moonraker connection needs reconnecting
                if (!_moonraker.IsConnected)
                {
                    Log("Reconnecting to Moonraker...");
                    await _moonraker.ConnectWebSocketAsync(ct);
                    await _moonraker.SubscribeToObicoObjectsAsync();

                    // Re-initialize status from current Moonraker state
                    // This clears stale "Offline" state and updates flags
                    await InitializeStatusAsync();

                    // Clear printer offline flag
                    _printerOffline = false;

                    // Only clear Offline-specific state, don't override actual printer state
                    // InitializeStatusAsync already set the correct state from Moonraker
                    lock (_statusLock)
                    {
                        // Only reset to Operational if we were showing Offline
                        // Preserve Error state if that's what Moonraker reported
                        if (_currentStatus.Status.State.Text == "Offline")
                        {
                            _currentStatus.Status.State.Text = "Operational";
                            _currentStatus.Status.State.Flags.Operational = true;
                            _currentStatus.Status.State.Flags.ClosedOrError = false;
                        }
                    }

                    // Send updated status to Obico immediately (if connected)
                    if (_obicoServer?.IsConnected == true)
                    {
                        SendStatusUpdate(includeSettings: true, force: true);
                    }

                    // Restart Janus streaming
                    if (_printerConfig.CameraEnabled && _printerConfig.Obico.Enabled)
                    {
                        Log("Restarting Janus after Moonraker reconnection...");
                        try
                        {
                            // Small delay to let stream stabilize
                            await Task.Delay(2000, ct);
                            if (_isRunning && !_printerOffline)
                            {
                                await StartJanusAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error restarting Janus: {ex.Message}");
                        }
                    }

                    Log("Reconnected to Moonraker");
                }

                // Check if Obico connection needs reconnecting
                if (_obicoServer != null && !_obicoServer.IsConnected)
                {
                    Log("Reconnecting to Obico server...");
                    await _obicoServer.ConnectAsync(ct);

                    // Verify tier status on reconnection
                    await VerifyTierAndAdjustFpsAsync();

                    // Send status update after reconnection
                    SendStatusUpdate(includeSettings: true);
                    Log("Reconnected to Obico server");
                }

                // Check if both are connected
                if (_moonraker.IsConnected && (_obicoServer?.IsConnected ?? false))
                {
                    Log("Reconnection successful");
                    SetState(ObicoClientState.Running);
                    _reconnecting = false;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Reconnection attempt {attempts} failed: {ex.Message}");
            }
        }

        if (attempts >= MaxReconnectAttempts)
        {
            Log($"Reconnection failed after {MaxReconnectAttempts} attempts");
            SetState(ObicoClientState.Failed);
        }

        _reconnecting = false;
    }

    #endregion

    #region Passthru Command Handling

    private async void OnPassthruCommand(object? sender, PassthruCommand command)
    {
        Log($"Received passthru: {command.Target}.{command.Function}");

        try
        {
            var (result, error) = await ExecutePassthruAsync(command);
            _obicoServer?.SendPassthruResponse(command.RefId ?? "", result, error);
        }
        catch (Exception ex)
        {
            Log($"Passthru error: {ex.Message}");
            _obicoServer?.SendPassthruResponse(command.RefId ?? "", null, ex.Message);
        }
    }

    private async Task<(object? result, string? error)> ExecutePassthruAsync(PassthruCommand command)
    {
        try
        {
            switch (command.Target)
            {
                case "_printer":
                    return await ExecutePrinterCommandAsync(command);

                case "moonraker_api":
                    return await ExecuteMoonrakerApiAsync(command);

                case "file_downloader":
                    return await ExecuteFileDownloaderAsync(command);

                case "file_operations":
                    return await ExecuteFileOperationsAsync(command);

                default:
                    return (null, $"Unknown target: {command.Target}");
            }
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(object? result, string? error)> ExecutePrinterCommandAsync(PassthruCommand command)
    {
        switch (command.Function)
        {
            case "pause":
                await _moonraker.PauseAsync();
                return (true, null);

            case "resume":
                await _moonraker.ResumeAsync();
                return (true, null);

            case "cancel":
                // Send native firmware stop immediately (don't wait for Moonraker state change)
                // This ensures native Anycubic firmware is synced even if Moonraker fails
                // (e.g., when print is paused due to filament error)
                Log("Cancel requested - sending immediate native firmware stop");
                NativePrintStopRequested?.Invoke(this, EventArgs.Empty);

                // Also cancel via Moonraker (may fail if already in error state)
                try
                {
                    await _moonraker.CancelAsync();
                }
                catch (Exception ex)
                {
                    Log($"Moonraker cancel failed (native firmware already notified): {ex.Message}");
                }
                return (true, null);

            case "home":
                // Home axes come in args[0] as ["x", "y", "z"] or in kwargs["axes"]
                var axesNode = command.Args is JsonArray homeArray && homeArray.Count > 0
                    ? homeArray[0]
                    : command.Kwargs?["axes"];
                var axes = axesNode?.AsArray()
                    .Select(x => x?.GetValue<string>() ?? "")
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToArray() ?? Array.Empty<string>();
                await _moonraker.HomeAsync(axes);
                return (true, null);

            case "jog":
                // Jog data comes in args[0] as {"x": val, "y": val, "z": val}
                var jogArgs = command.Args is JsonArray jogArray && jogArray.Count > 0
                    ? jogArray[0]
                    : command.Kwargs;
                var x = jogArgs?["x"]?.GetValue<double>();
                var y = jogArgs?["y"]?.GetValue<double>();
                var z = jogArgs?["z"]?.GetValue<double>();
                // Obico uses inverted Z convention (up button sends negative Z)
                // Negate Z to match printer convention where Z+ = nozzle up
                if (z.HasValue)
                    z = -z.Value;
                await _moonraker.JogAsync(x, y, z);
                return (true, null);

            case "set_temperature":
                // Temperature data comes in args as [heater, target_temp] e.g. ["tool0", 115]
                string heater = "extruder";
                double temp = 0;
                if (command.Args is JsonArray tempArgs && tempArgs.Count >= 2)
                {
                    heater = tempArgs[0]?.GetValue<string>() ?? "extruder";
                    temp = tempArgs[1]?.GetValue<double>() ?? 0;
                }
                else
                {
                    // Fallback to kwargs
                    heater = command.Kwargs?["heater"]?.GetValue<string>() ?? "extruder";
                    temp = command.Kwargs?["target"]?.GetValue<double>() ?? 0;
                }
                await _moonraker.SetTemperatureAsync(heater, temp);
                return (true, null);

            case "motors_off":
                await _moonraker.DisableMotorsAsync();
                return (true, null);

            case "commands":
                // Execute raw G-code commands - args[0] contains the gcode string
                var gcode = command.Args is JsonArray gcodeArgs && gcodeArgs.Count > 0
                    ? gcodeArgs[0]?.GetValue<string>()
                    : command.Kwargs?["commands"]?.GetValue<string>();

                if (string.IsNullOrEmpty(gcode))
                    return (null, "Missing G-code commands");

                Log($"Executing G-code: {gcode.Replace("\n", "\\n")}");
                try
                {
                    await _moonraker.ExecuteGcodeAsync(gcode);
                    Log("G-code executed successfully");
                }
                catch (Exception ex)
                {
                    Log($"G-code execution failed: {ex.Message}");
                    return (null, ex.Message);
                }
                return (true, null);

            default:
                return (null, $"Unknown printer function: {command.Function}");
        }
    }

    private async Task<(object? result, string? error)> ExecuteMoonrakerApiAsync(PassthruCommand command)
    {
        // Generic Moonraker API proxy
        // The func field contains the API path (e.g., "printer/gcode/script")
        // kwargs contains the parameters (e.g., {script: "M83..."} or {verb: "post", ...})
        var path = command.Function;
        var verb = command.Kwargs?["verb"]?.GetValue<string>()?.ToLower() ?? "get";  // Default to GET for most endpoints

        // Handle gcode script specially - it's always a POST with script parameter
        if (path == "printer/gcode/script")
        {
            var script = command.Kwargs?["script"]?.GetValue<string>();
            if (string.IsNullOrEmpty(script))
                return (null, "Missing script parameter");

            await _moonraker.ExecuteGcodeAsync(script);
            return (true, null);
        }

        // Build query parameters from kwargs (excluding verb and data)
        var queryParams = new List<string>();
        if (command.Kwargs != null)
        {
            foreach (var prop in command.Kwargs.AsObject())
            {
                if (prop.Key != "verb" && prop.Key != "data")
                {
                    var value = prop.Value?.ToString();
                    if (!string.IsNullOrEmpty(value))
                        queryParams.Add($"{prop.Key}={Uri.EscapeDataString(value)}");
                }
            }
        }

        var fullPath = queryParams.Count > 0 ? $"{path}?{string.Join("&", queryParams)}" : path;

        if (verb == "get")
        {
            var result = await _moonraker.GetAsync<JsonNode>(fullPath);
            return (result, null);
        }
        else if (verb == "post")
        {
            var data = command.Kwargs?["data"];
            var result = await _moonraker.PostAsync<JsonNode>(fullPath, data);
            return (result, null);
        }

        return (null, $"Unsupported verb: {verb}");
    }

    private async Task<(object? result, string? error)> ExecuteFileDownloaderAsync(PassthruCommand command)
    {
        if (command.Function != "download")
            return (null, $"Unknown file_downloader function: {command.Function}");

        // Parse args - it's an array with one object containing {id, url, filename, safe_filename}
        var args = command.Args;
        if (args == null || args is not JsonArray argsArray || argsArray.Count == 0)
            return (null, "Missing download arguments");

        var downloadInfo = argsArray[0];
        var url = downloadInfo?["url"]?.GetValue<string>();
        var filename = downloadInfo?["safe_filename"]?.GetValue<string>()
                    ?? downloadInfo?["filename"]?.GetValue<string>();

        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(filename))
            return (null, "Missing url or filename in download arguments");

        Log($"Downloading gcode: {filename} from {url}");

        try
        {
            // Download the gcode file from Obico server
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            // Add auth token to request
            if (!string.IsNullOrEmpty(_printerConfig.Obico.AuthToken))
            {
                httpClient.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Token", _printerConfig.Obico.AuthToken);
            }

            var gcodeData = await httpClient.GetByteArrayAsync(url);
            Log($"Downloaded {gcodeData.Length} bytes");

            // Upload to Moonraker
            Log($"Uploading to Moonraker: {filename}");
            await _moonraker.UploadFileAsync(filename, gcodeData);

            // Start the print
            Log($"Starting print: {filename}");
            await _moonraker.StartPrintAsync(filename);

            return (new { success = true, filename }, null);
        }
        catch (Exception ex)
        {
            Log($"File download/print failed: {ex.Message}");
            return (null, ex.Message);
        }
    }

    private async Task<(object? result, string? error)> ExecuteFileOperationsAsync(PassthruCommand command)
    {
        switch (command.Function)
        {
            case "start_printer_local_print":
                // Start printing a local file - args contain file info with url (path) and agent_signature
                var fileInfo = command.Args is JsonArray argsArray && argsArray.Count > 0
                    ? argsArray[0]
                    : command.Kwargs;

                var filepath = fileInfo?["url"]?.GetValue<string>();
                var agentSignature = fileInfo?["agent_signature"]?.GetValue<string>();

                if (string.IsNullOrEmpty(filepath))
                    return (null, "Missing filepath");

                // Verify file hasn't been modified if signature provided
                if (!string.IsNullOrEmpty(agentSignature))
                {
                    var isValid = await VerifyFileSignatureAsync(filepath, agentSignature);
                    if (!isValid)
                        return (null, "File has been modified! Did you move, delete, or overwrite this file?");
                }

                Log($"Starting local print: {filepath}");
                await _moonraker.StartPrintAsync(filepath);
                return ("Success", null);

            case "check_filepath_and_agent_signature":
                // Verify file integrity - args contain [filepath, server_signature]
                string checkFilepath;
                string checkSignature;

                if (command.Args is JsonArray checkArgs && checkArgs.Count >= 2)
                {
                    checkFilepath = checkArgs[0]?.GetValue<string>() ?? "";
                    checkSignature = checkArgs[1]?.GetValue<string>() ?? "";
                }
                else
                {
                    checkFilepath = command.Kwargs?["filepath"]?.GetValue<string>() ?? "";
                    checkSignature = command.Kwargs?["server_signature"]?.GetValue<string>() ?? "";
                }

                if (string.IsNullOrEmpty(checkFilepath))
                    return (false, null);

                var signatureValid = await VerifyFileSignatureAsync(checkFilepath, checkSignature);
                return (signatureValid, null);

            default:
                return (null, $"Unknown file_operations function: {command.Function}");
        }
    }

    private async Task<bool> VerifyFileSignatureAsync(string filepath, string expectedSignature)
    {
        try
        {
            // Query file metadata from Moonraker (GetAsync already extracts "result")
            var metadata = await _moonraker.GetAsync<JsonNode>($"/server/files/metadata?filename={Uri.EscapeDataString(filepath)}");
            var modified = metadata?["modified"]?.GetValue<double>();

            if (modified == null)
                return false;

            // Signature format is "ts:{modified_timestamp}"
            var actualSignature = $"ts:{modified}";
            return actualSignature == expectedSignature;
        }
        catch
        {
            return false; // File doesn't exist or can't be read
        }
    }

    #endregion

    #region Background Tasks

    private async Task StatusUpdateLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // When printer is offline, reduce update frequency to once per minute
                var delay = _printerOffline ? 60 : 10;
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);

                // Skip status updates when offline - the offline status was already sent
                // and we don't want to spam with stale data
                if (!_printerOffline)
                {
                    SendStatusUpdate();
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Status update error: {ex.Message}");
            }
        }
    }

    private async Task SnapshotUploadLoopAsync(CancellationToken ct)
    {
        // Determine if this is a cloud or local/self-hosted server
        // Cloud servers (obico.io) have rate limits, local servers do not
        var isCloud = IsCloudServer();
        var isPro = _printerConfig.Obico.IsPro;

        Log($"Snapshot upload loop started (isCloud={isCloud}, isPro={isPro}, viewing={_isUserViewing})");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Calculate interval based on server type and viewing state
                var intervalSeconds = GetSnapshotInterval(isCloud, isPro);

                // Use shorter delays when viewing to be more responsive to state changes
                var delayMs = _isUserViewing && !isCloud
                    ? Math.Min((int)(intervalSeconds * 1000), 200)
                    : (int)(intervalSeconds * 1000);

                await Task.Delay(delayMs, ct);

                if (_obicoServer?.IsConnected == true && _getSnapshotCallback != null)
                {
                    var snapshot = _getSnapshotCallback();
                    if (snapshot != null && snapshot.Length > 0)
                    {
                        // Include viewing boost flag for local servers
                        var viewingBoost = !isCloud && _isUserViewing;
                        await _obicoServer.PostSnapshotAsync(snapshot, isPrimary: true, viewingBoost: viewingBoost, ct: ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"Snapshot upload error: {ex.Message}");
                // On rate limit error, wait longer before retrying
                if (ex.Message.Contains("429"))
                {
                    Log("Rate limited - backing off for 60 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);
                }
            }
        }
    }

    /// <summary>
    /// Check if connected to Obico cloud (app.obico.io) vs self-hosted server.
    /// </summary>
    private bool IsCloudServer()
    {
        var serverUrl = _printerConfig.Obico.ServerUrl?.ToLowerInvariant() ?? "";
        return serverUrl.Contains("obico.io");
    }

    /// <summary>
    /// Get snapshot upload interval based on server type, tier, and viewing state.
    /// </summary>
    private double GetSnapshotInterval(bool isCloud, bool isPro)
    {
        if (isCloud)
        {
            // Cloud servers have rate limits
            return isPro ? CloudProSnapshotInterval : CloudFreeSnapshotInterval;
        }
        else
        {
            // Local/self-hosted servers have no rate limits
            // Use faster interval when user is actively viewing
            if (_isUserViewing)
            {
                // When viewing, use camera's max FPS or our limit (whichever is lower)
                // Cap at 5 FPS (0.2s interval) to avoid overwhelming the server
                var maxFps = _printerConfig.MaxFps > 0 ? _printerConfig.MaxFps : 10;
                var minInterval = 1.0 / maxFps;
                return Math.Max(minInterval, LocalViewingSnapshotInterval);
            }
            else
            {
                return LocalIdleSnapshotInterval;
            }
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Verify account tier from server and adjust FPS if needed.
    /// </summary>
    private async Task VerifyTierAndAdjustFpsAsync()
    {
        try
        {
            Log("Fetching printer info from Obico server...");
            var printerInfo = await _obicoServer!.GetPrinterInfoAsync(_cts!.Token);
            if (printerInfo == null)
            {
                Log("Printer info returned null from Obico server");
                return;
            }

            Log($"Obico printer info: id={printerInfo.Id}, name='{printerInfo.Name}', isPro={printerInfo.IsPro}");

            // Capture previous values for change detection
            var wasProBefore = _printerConfig.Obico.IsPro;
            var previousName = _printerConfig.Obico.ObicoName ?? "";
            var previousPrinterId = _printerConfig.Obico.ObicoPrinterId;

            // Update tier status
            _printerConfig.Obico.IsPro = printerInfo.IsPro;

            // Update printer ID (for server-side deletion)
            if (printerInfo.Id > 0)
            {
                _printerConfig.Obico.ObicoPrinterId = printerInfo.Id;
            }

            // Update Obico name if changed
            if (!string.IsNullOrEmpty(printerInfo.Name))
            {
                _printerConfig.Obico.ObicoName = printerInfo.Name;
            }

            // Note: TargetFps in config is informational only for display purposes
            // Actual snapshot upload rate is determined by account tier in SnapshotUploadLoopAsync:
            // Free: 0.1 FPS (1 frame every 10 seconds)
            // Pro: 0.5 FPS (1 frame every 2 seconds)
            _printerConfig.Obico.TargetFps = printerInfo.IsPro ? 1 : 1; // Display "1 FPS" but actual rate is lower

            var tierChanged = wasProBefore != printerInfo.IsPro;
            var nameChanged = previousName != (printerInfo.Name ?? "");
            var printerIdChanged = previousPrinterId != printerInfo.Id && printerInfo.Id > 0;

            if (tierChanged)
            {
                var tierName = printerInfo.IsPro ? "Pro" : "Free";
                Log($"Account tier: {tierName}");
            }

            if (nameChanged)
            {
                Log($"Obico name updated: '{previousName}' -> '{printerInfo.Name}'");
            }

            if (printerIdChanged)
            {
                Log($"Obico printer ID: {printerInfo.Id}");
            }

            // Notify caller to persist config changes
            if (tierChanged || nameChanged || printerIdChanged)
            {
                ConfigUpdated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to verify tier status: {ex.Message}");
            // Continue without tier verification - use existing config
        }
    }

    private void SendStatusUpdate(bool includeSettings = false, bool force = false)
    {
        if (_obicoServer?.IsConnected != true)
        {
            Log("Skipping status update - not connected to Obico");
            return;
        }

        ObicoStatusUpdate update;
        lock (_statusLock)
        {
            // Always update timestamp to current time before sending
            _currentStatus.Status.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            update = new ObicoStatusUpdate
            {
                // current_print_ts should only be set when actively printing
                // When null, ObicoServerConnection will omit it from the message
                CurrentPrintTs = _currentPrintTs,
                Status = _currentStatus.Status
            };
        }

        if (includeSettings)
        {
            var jobFilename = update.Status.Job?.File?.Name ?? "null";
            Log($"Sending initial status with settings (state={update.Status.State.Text}, ready={update.Status.State.Flags.Ready}, currentPrintTs={update.CurrentPrintTs?.ToString() ?? "null"}, printing={update.Status.State.Flags.Printing}, job.file.name={jobFilename})");
        }

        if (includeSettings)
        {
            update.Settings = CreateSettings();
        }

        _obicoServer.SendStatusUpdate(update, includeSettings, force);
    }

    private void SendPrintEvent(string eventType)
    {
        if (_obicoServer?.IsConnected != true)
            return;

        ObicoStatusUpdate update;
        lock (_statusLock)
        {
            update = new ObicoStatusUpdate
            {
                // current_print_ts is the timestamp when print started
                // When null (not printing), it will be omitted from the message
                CurrentPrintTs = _currentPrintTs,
                Status = _currentStatus.Status,
                Event = new ObicoEvent { EventType = eventType }
            };
        }

        _obicoServer.SendStatusUpdate(update);
    }

    private ObicoSettings CreateSettings()
    {
        var settings = new ObicoSettings
        {
            Agent = new ObicoAgent
            {
                Name = "moonraker_obico",  // Must match for Moonraker-style commands
                Version = GetVersion()
            },
            PlatformUname = new List<string>
            {
                "Linux",
                Environment.MachineName,
                "5.10.0",
                "ACProxyCam",
                "aarch64"
            }
        };

        // Add webcam info if camera is enabled
        if (_printerConfig.CameraEnabled)
        {
            // Determine stream mode string for Obico protocol
            string streamModeStr;
            if (_janusEnabled && _janusClient != null)
            {
                // WebRTC streaming active - report the correct mode
                streamModeStr = _janusClient.StreamMode == ObicoStreamMode.H264 ? "h264_copy" : "mjpeg_webrtc";
            }
            else
            {
                // No WebRTC, just MJPEG snapshots
                streamModeStr = "mjpeg";
            }

            var webcam = new ObicoWebcam
            {
                Name = _printerConfig.Name,
                IsPrimaryCamera = true,
                StreamMode = streamModeStr,
                StreamUrl = $"http://{{host}}:{_printerConfig.MjpegPort}/stream",
                SnapshotUrl = $"http://{{host}}:{_printerConfig.MjpegPort}/snapshot"
            };

            // Add stream_id when Janus is enabled for WebRTC streaming
            if (_janusEnabled && _janusClient != null)
            {
                webcam.StreamId = _janusClient.StreamId;
            }

            settings.Webcams.Add(webcam);
        }

        return settings;
    }

    private string GetVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version;
        return version?.ToString(3) ?? "1.0.0";
    }

    private void SetState(ObicoClientState newState)
    {
        if (State != newState)
        {
            State = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    private void Log(string message)
    {
        StatusChanged?.Invoke(this, $"[{_printerConfig.Name}] {message}");
    }

    /// <summary>
    /// Set total layer count in the nested file_metadata.obico structure.
    /// </summary>
    private void SetTotalLayerCount(int totalLayers)
    {
        _currentStatus.Status.FileMetadata ??= new ObicoFileMetadata();
        _currentStatus.Status.FileMetadata.Obico ??= new ObicoFileMeta();
        _currentStatus.Status.FileMetadata.Obico.TotalLayerCount = totalLayers;
    }

    /// <summary>
    /// Get total layer count from the nested file_metadata.obico structure.
    /// </summary>
    private int? GetTotalLayerCount()
    {
        return _currentStatus.Status.FileMetadata?.Obico?.TotalLayerCount;
    }

    /// <summary>
    /// Set maxZ in the nested file_metadata.analysis.printingArea structure.
    /// </summary>
    private void SetMaxZ(double maxZ)
    {
        _currentStatus.Status.FileMetadata ??= new ObicoFileMetadata();
        _currentStatus.Status.FileMetadata.Analysis ??= new ObicoFileAnalysis();
        _currentStatus.Status.FileMetadata.Analysis.PrintingArea ??= new ObicoPrintingArea();
        _currentStatus.Status.FileMetadata.Analysis.PrintingArea!.MaxZ = maxZ;
    }

    /// <summary>
    /// Get maxZ from the nested file_metadata.analysis.printingArea structure.
    /// </summary>
    private double? GetMaxZ()
    {
        return _currentStatus.Status.FileMetadata?.Analysis?.PrintingArea?.MaxZ;
    }

    /// <summary>
    /// Fetch file metadata (object_height) for maxZ display.
    /// Falls back to virtual_sdcard.model_size_z for Anycubic printers where
    /// files are sent directly to the printer and not through Moonraker's file system.
    /// </summary>
    private async Task FetchFileMetadataAsync()
    {
        if (string.IsNullOrEmpty(_currentFilename))
        {
            Log("FetchFileMetadata: No filename set");
            return;
        }

        // First try Moonraker's file metadata
        try
        {
            var metadata = await _moonraker.GetAsync<JsonNode>($"/server/files/metadata?filename={Uri.EscapeDataString(_currentFilename)}");
            var objectHeight = metadata?["object_height"]?.GetValue<double>();
            if (objectHeight.HasValue && objectHeight.Value > 0)
            {
                lock (_statusLock)
                {
                    SetMaxZ(objectHeight.Value);
                }
                Log($"File metadata: maxZ={objectHeight.Value:F2}mm");
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"Moonraker file metadata not available: {ex.Message}");
        }

        // Fallback: Try virtual_sdcard.model_size_z (Anycubic printers populate this)
        try
        {
            var result = await _moonraker.GetAsync<JsonNode>("/printer/objects/query?virtual_sdcard=model_size_z");
            var modelSizeZ = result?["status"]?["virtual_sdcard"]?["model_size_z"]?.GetValue<double>();
            if (modelSizeZ.HasValue && modelSizeZ.Value > 0)
            {
                lock (_statusLock)
                {
                    SetMaxZ(modelSizeZ.Value);
                }
                Log($"File metadata from virtual_sdcard: maxZ={modelSizeZ.Value:F2}mm");
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to fetch model_size_z from virtual_sdcard: {ex.Message}");
        }

        Log("FetchFileMetadata: maxZ not available from any source");
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
        _h264Streamer?.Dispose();
        _janusStreamer?.Dispose();
        _janusClient?.Dispose();
        _obicoServer?.Dispose();
        _moonraker.Dispose();
    }
}

public enum ObicoClientState
{
    Stopped,
    Connecting,
    WaitingForLink,
    Running,
    Reconnecting,
    Failed
}
