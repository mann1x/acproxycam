// BedMeshService.cs - BedMesh calibration execution and monitoring

using System.Text.Json;
using System.Text.RegularExpressions;
using ACProxyCam.Models;
using Renci.SshNet;

namespace ACProxyCam.Services;

/// <summary>
/// Handles BedMesh calibration execution via SSH and monitoring via MQTT polling.
/// </summary>
public class BedMeshService : IDisposable
{
    private const string SessionsDir = "/etc/acproxycam/sessions";
    private const string CalibrationsDir = "/etc/acproxycam/sessions/calibrations";
    private const string AnalysesDir = "/etc/acproxycam/sessions/analyses";
    private const string PrinterScriptPath = "/tmp/acproxycam_bedmesh.sh";
    private const string PrinterPidPath = "/tmp/acproxycam_bedmesh.pid";
    private const string PrinterOutputPath = "/tmp/acproxycam_bedmesh.out";
    private const string PrinterMutableCfgPath = "/userdata/app/gk/printer_data/config/printer_mutable.cfg";
    private const string PrinterGeneratedCfgPath = "/userdata/app/gk/printer_data/config/printer.generated.cfg";

    private const int SshTimeoutSeconds = 10;
    private const int MonitorIntervalSeconds = 15;
    private const int MaxRetryCount = 6;
    private const int RetryDelayMs = 5000;

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _disposed;

    /// <summary>
    /// Currently active sessions being monitored.
    /// </summary>
    private readonly Dictionary<string, BedMeshSession> _activeSessions = new();
    private readonly object _lock = new();

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<BedMeshSession>? SessionCompleted;

    /// <summary>
    /// Start the monitoring loop for active sessions.
    /// </summary>
    public void StartMonitoring()
    {
        if (_monitorTask != null && !_monitorTask.IsCompleted)
            return;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Stop the monitoring loop.
    /// </summary>
    public async Task StopMonitoringAsync()
    {
        _cts?.Cancel();
        if (_monitorTask != null)
        {
            try
            {
                await _monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
    }

    /// <summary>
    /// Recover any running sessions from disk on startup.
    /// </summary>
    public async Task<List<(string deviceId, RunningSession session)>> RecoverSessionsAsync()
    {
        var recovered = new List<(string deviceId, RunningSession session)>();

        try
        {
            EnsureDirectoriesExist();

            var runningFiles = Directory.GetFiles(SessionsDir, "*.running.json");
            foreach (var file in runningFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var session = JsonSerializer.Deserialize<RunningSession>(json);
                    if (session != null)
                    {
                        recovered.Add((session.DeviceId, session));
                        LogStatus($"Recovered running session for {session.PrinterName} ({session.DeviceId})");
                    }
                }
                catch (Exception ex)
                {
                    LogStatus($"Failed to recover session from {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to scan for running sessions: {ex.Message}");
        }

        return recovered;
    }

    /// <summary>
    /// Add a recovered session to monitoring.
    /// </summary>
    public void AddRecoveredSession(PrinterConfig config, RunningSession runningSession)
    {
        var session = new BedMeshSession
        {
            DeviceId = runningSession.DeviceId,
            PrinterName = runningSession.PrinterName,
            DeviceType = runningSession.DeviceType,
            StartedUtc = runningSession.StartedUtc,
            HeatSoakMinutes = runningSession.HeatSoakMinutes,
            Status = CalibrationStatus.Running,
            CurrentStep = "Monitoring..."
        };

        lock (_lock)
        {
            _activeSessions[session.DeviceId] = session;
        }
    }

    /// <summary>
    /// Start a new calibration for a printer.
    /// </summary>
    public async Task<(bool success, string? error)> StartCalibrationAsync(
        PrinterConfig config,
        int heatSoakMinutes,
        Func<Task<bool>>? turnLedOn = null,
        string? name = null)
    {
        if (string.IsNullOrEmpty(config.DeviceId))
            return (false, "Printer deviceId is required");

        // Check if calibration already running for this printer
        lock (_lock)
        {
            if (_activeSessions.ContainsKey(config.DeviceId))
                return (false, "Calibration already running for this printer");
        }

        try
        {
            EnsureDirectoriesExist();

            // Step 1: Turn LED on (via provided callback, typically MQTT)
            if (turnLedOn != null)
            {
                LogStatus($"Turning LED on for {config.Name}...");
                var ledResult = await turnLedOn();
                if (!ledResult)
                {
                    LogStatus($"Warning: Failed to turn LED on, continuing anyway");
                }
            }

            // Step 2: Create running session file
            var session = new BedMeshSession
            {
                DeviceId = config.DeviceId,
                PrinterName = config.Name,
                DeviceType = config.DeviceType,
                Name = name,
                StartedUtc = DateTime.UtcNow,
                HeatSoakMinutes = heatSoakMinutes,
                Status = CalibrationStatus.Running,
                CurrentStep = "Starting..."
            };

            var runningSession = new RunningSession
            {
                DeviceId = config.DeviceId,
                PrinterName = config.Name,
                DeviceType = config.DeviceType,
                Name = name,
                PrinterIp = config.Ip,
                StartedUtc = session.StartedUtc,
                HeatSoakMinutes = heatSoakMinutes
            };

            var runningPath = GetRunningSessionPath(config.DeviceId);
            await File.WriteAllTextAsync(runningPath, JsonSerializer.Serialize(runningSession, new JsonSerializerOptions { WriteIndented = true }));

            // Step 3: SSH to printer and run script
            var scriptResult = await ExecuteCalibrationScriptAsync(config, heatSoakMinutes);
            if (!scriptResult.success)
            {
                // Cleanup running session file on failure
                try { File.Delete(runningPath); } catch { }
                return (false, scriptResult.error);
            }

            // Step 4: Add to active sessions
            lock (_lock)
            {
                _activeSessions[config.DeviceId] = session;
            }

            LogStatus($"Calibration started for {config.Name}");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Get all active sessions.
    /// </summary>
    public List<BedMeshSession> GetActiveSessions()
    {
        lock (_lock)
        {
            return _activeSessions.Values.ToList();
        }
    }

    /// <summary>
    /// Get session summary (counts and lists).
    /// </summary>
    public async Task<BedMeshSessionSummary> GetSessionSummaryAsync()
    {
        var summary = new BedMeshSessionSummary();

        lock (_lock)
        {
            summary.ActiveCount = _activeSessions.Count;
            summary.ActiveSessions = _activeSessions.Values.ToList();
        }

        // Count saved calibrations
        try
        {
            if (Directory.Exists(CalibrationsDir))
            {
                var calibrationFiles = Directory.GetFiles(CalibrationsDir, "*.mesh");
                summary.CalibrationCount = calibrationFiles.Length;
                summary.Calibrations = await LoadSessionInfosAsync(calibrationFiles);
            }
        }
        catch { }

        // Count saved analyses (placeholder for now)
        try
        {
            if (Directory.Exists(AnalysesDir))
            {
                var analysisFiles = Directory.GetFiles(AnalysesDir, "*.analysis");
                summary.AnalysisCount = analysisFiles.Length;
                summary.Analyses = await LoadSessionInfosAsync(analysisFiles);
            }
        }
        catch { }

        return summary;
    }

    /// <summary>
    /// Load a saved calibration by filename.
    /// </summary>
    public async Task<BedMeshSession?> LoadCalibrationAsync(string fileName)
    {
        var path = Path.Combine(CalibrationsDir, fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var session = JsonSerializer.Deserialize<BedMeshSession>(json);

            // Recalculate stats if coordinate data is missing (for old calibrations)
            if (session?.MeshData?.Stats != null && session.MeshData.Points.Length > 0)
            {
                var stats = session.MeshData.Stats;
                var mesh = session.MeshData;

                // If coordinates are at default (0,0) but mesh doesn't start at 0, recalculate
                if (stats.MinAtX == 0 && stats.MinAtY == 0 && (mesh.MinX != 0 || mesh.MinY != 0))
                {
                    session.MeshData.Stats = MeshStats.Calculate(session.MeshData);
                }
            }

            return session;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Delete a saved calibration.
    /// </summary>
    public bool DeleteCalibration(string fileName)
    {
        var path = Path.Combine(CalibrationsDir, fileName);
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Execute the calibration shell script on the printer via SSH.
    /// </summary>
    private async Task<(bool success, string? error)> ExecuteCalibrationScriptAsync(PrinterConfig config, int heatSoakMinutes)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var client = new SshClient(config.Ip, config.SshPort, config.SshUser, config.SshPassword);
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);
                client.Connect();

                if (!client.IsConnected)
                    return (false, "SSH connection failed");

                LogStatus($"SSH connected to {config.Ip}, creating calibration script...");

                // Create the shell script
                var script = GenerateCalibrationScript(heatSoakMinutes);

                // Ensure Unix line endings (LF only, not CRLF)
                script = script.Replace("\r\n", "\n").Replace("\r", "\n");

                // Use base64 encoding to transfer the script (avoids heredoc/escaping issues)
                var scriptBytes = System.Text.Encoding.UTF8.GetBytes(script);
                var scriptBase64 = Convert.ToBase64String(scriptBytes);

                // Write script to file using base64 decode
                var writeCmd = client.RunCommand($"echo '{scriptBase64}' | base64 -d > {PrinterScriptPath}");
                if (writeCmd.ExitStatus != 0)
                    return (false, $"Failed to create script: {writeCmd.Error}");

                // Make executable
                client.RunCommand($"chmod +x {PrinterScriptPath}");

                // Run script in background with nohup
                LogStatus($"Starting calibration script on {config.Name}...");
                var runCmd = client.RunCommand($"nohup sh {PrinterScriptPath} > /dev/null 2>&1 &");

                // Verify script started - use Thread.Sleep since we're in Task.Run
                Thread.Sleep(500);
                var pidCheck = client.RunCommand($"cat {PrinterPidPath} 2>/dev/null");
                if (pidCheck.ExitStatus != 0 || string.IsNullOrWhiteSpace(pidCheck.Result))
                {
                    // Check if output file has error
                    var outCheck = client.RunCommand($"cat {PrinterOutputPath} 2>/dev/null");
                    return (false, $"Script failed to start. Output: {outCheck.Result}");
                }

                var pid = pidCheck.Result.Trim();
                LogStatus($"Calibration script started with PID {pid}");

                client.Disconnect();
                return (true, (string?)null);
            }
            catch (Exception ex)
            {
                return (false, $"SSH error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Generate the calibration shell script.
    /// </summary>
    private string GenerateCalibrationScript(int heatSoakMinutes)
    {
        // Heat soak section: set bed to 60C via Moonraker API, then wait heat soak duration
        var heatSoakSection = heatSoakMinutes > 0 ? $@"
# 2. Heat soak (if HEATSOAK_MINUTES > 0)
echo ""STEP: Heat soak - setting bed to 60C via Moonraker API"" >> ""$LOG""
wget -q -O /dev/null 'http://localhost:7125/printer/gcode/script?script=SET_HEATER_TEMPERATURE%20HEATER=heater_bed%20TARGET=60' 2>>""$LOG""

# Start heat soak countdown (bed will heat during this time)
echo ""STEP: Heat soak - waiting {heatSoakMinutes} minutes"" >> ""$LOG""
sleep $(({heatSoakMinutes} * 60))
echo ""STEP: Heat soak complete"" >> ""$LOG""
" : "";

        // Script uses longer timeouts for physical operations (wiping, probing)
        // which can take several minutes to complete
        return $@"#!/bin/sh
# ACProxyCam BedMesh Calibration Script
LOG=""{PrinterOutputPath}""
echo ""$$"" > {PrinterPidPath}
echo ""STARTED $(date -Iseconds)"" > ""$LOG""

# Error handler to log any unexpected exits
trap 'echo ""SCRIPT_EXIT: code=$? at $(date -Iseconds)"" >> ""$LOG""' EXIT

# API_CMD function with configurable timeout
# Usage: API_CMD <timeout_seconds> '<json_command>'
# Filters out verbose status updates, only keeping final result/error messages
API_CMD() {{
    local timeout=$1
    local cmd=$2
    printf '%s\003' ""$cmd"" | nc -w ""$timeout"" localhost 18086 2>>""$LOG"" | tr -d '\003' | grep -v 'process_status_update' | grep -v 'process_gcode_response' | tail -1
}}

# 1. Set Busy
echo ""STEP: Setting printer busy"" >> ""$LOG""
API_CMD 30 '{{""id"":1,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":1}}}}'
{heatSoakSection}
# 3. Preheating
echo ""STEP: Preheating"" >> ""$LOG""
RESULT=$(API_CMD 120 '{{""id"":3,""method"":""Leviq2/Preheating"",""params"":{{""script"":""LEVIQ2_PREHEATING""}}}}')
echo ""RESULT_PREHEAT: $RESULT"" >> ""$LOG""
if echo ""$RESULT"" | grep -q '""error""'; then
    echo ""ERROR: Preheating failed"" >> ""$LOG""
    API_CMD 30 '{{""id"":99,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'
    rm -f {PrinterPidPath}
    exit 1
fi

# 4. Wiping (can take 1-2 minutes)
echo ""STEP: Wiping"" >> ""$LOG""
RESULT=$(API_CMD 180 '{{""id"":4,""method"":""Leviq2/Wiping"",""params"":{{""script"":""LEVIQ2_WIPING""}}}}')
echo ""RESULT_WIPE: $RESULT"" >> ""$LOG""
if echo ""$RESULT"" | grep -q '""error""'; then
    echo ""ERROR: Wiping failed"" >> ""$LOG""
    API_CMD 30 '{{""id"":99,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'
    rm -f {PrinterPidPath}
    exit 1
fi

# 5. Probe (actual mesh creation - can take 5-60 minutes depending on mesh size)
echo ""STEP: Probing"" >> ""$LOG""
RESULT=$(API_CMD 3600 '{{""id"":5,""method"":""Leviq2/Probe"",""params"":{{""script"":""LEVIQ2_PROBE""}}}}')
echo ""RESULT_PROBE: $RESULT"" >> ""$LOG""
if echo ""$RESULT"" | grep -q '""error""'; then
    echo ""ERROR: Probing failed"" >> ""$LOG""
    API_CMD 30 '{{""id"":99,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'
    rm -f {PrinterPidPath}
    exit 1
fi

# 6. Save mesh to printer_mutable.cfg (SAVE_CONFIG)
echo ""STEP: Saving mesh configuration"" >> ""$LOG""
RESULT=$(API_CMD 60 '{{""id"":6,""method"":""Config/PrinterConfSave"",""params"":{{""script"":""SAVE_CONFIG""}}}}')
echo ""RESULT_SAVE: $RESULT"" >> ""$LOG""
sleep 2

# 7. Turn off heaters (nozzle and bed) via Moonraker API - no wait needed
echo ""STEP: Turning off heaters"" >> ""$LOG""
wget -q -O /dev/null 'http://localhost:7125/printer/gcode/script?script=SET_HEATER_TEMPERATURE%20HEATER=extruder%20TARGET=0' 2>>""$LOG""
wget -q -O /dev/null 'http://localhost:7125/printer/gcode/script?script=SET_HEATER_TEMPERATURE%20HEATER=heater_bed%20TARGET=0' 2>>""$LOG""

# 8. Set Free
echo ""STEP: Setting printer free"" >> ""$LOG""
API_CMD 30 '{{""id"":9,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'

# Write SUCCESS marker and sync to ensure it's flushed to disk
echo ""SUCCESS $(date -Iseconds)"" >> ""$LOG""
sync
sleep 1
rm -f {PrinterPidPath}
";
    }

    /// <summary>
    /// Monitoring loop for active sessions.
    /// </summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(MonitorIntervalSeconds), ct);

                List<string> toCheck;
                lock (_lock)
                {
                    toCheck = _activeSessions.Keys.ToList();
                }

                foreach (var deviceId in toCheck)
                {
                    if (ct.IsCancellationRequested) break;
                    await CheckSessionStatusAsync(deviceId, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogStatus($"Monitor loop error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Check the status of a running session.
    /// </summary>
    private async Task CheckSessionStatusAsync(string deviceId, CancellationToken ct)
    {
        BedMeshSession? session;
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(deviceId, out session))
                return;
        }

        // Grace period - don't check sessions that just started (allow script to initialize)
        var sessionAge = DateTime.UtcNow - session.StartedUtc;
        LogStatus($"Checking session {deviceId}: age={sessionAge.TotalSeconds:F1}s, StartedUtc={session.StartedUtc:O}, Now={DateTime.UtcNow:O}");
        if (sessionAge.TotalSeconds < 30)
        {
            LogStatus($"Session {deviceId} too young ({sessionAge.TotalSeconds:F1}s < 30s), skipping check");
            return; // Too early to check
        }

        try
        {
            // Load running session to get printer IP
            var runningPath = GetRunningSessionPath(deviceId);
            if (!File.Exists(runningPath))
            {
                // Running session file gone - mark as failed
                await CompleteSessionAsync(deviceId, false, "Running session file not found");
                return;
            }

            var runningJson = await File.ReadAllTextAsync(runningPath, ct);
            var running = JsonSerializer.Deserialize<RunningSession>(runningJson);
            if (running == null)
            {
                await CompleteSessionAsync(deviceId, false, "Invalid running session file");
                return;
            }

            // SSH to check PID file
            var (pidExists, output, meshData, error) = await CheckCalibrationStatusAsync(running, ct);

            if (pidExists)
            {
                // Still running - update current step from output
                session.CurrentStep = ParseCurrentStep(output);
                return;
            }

            // Script finished - check result
            if (output != null && output.Contains("SUCCESS"))
            {
                session.MeshData = meshData;
                await CompleteSessionAsync(deviceId, true, null, meshData);
            }
            else if (output != null && output.Contains("ERROR"))
            {
                var errorMsg = ParseErrorMessage(output);
                await CompleteSessionAsync(deviceId, false, errorMsg ?? "Calibration failed");
            }
            else
            {
                // No output or unexpected state
                await CompleteSessionAsync(deviceId, false, error ?? "Unknown calibration result");
            }

            // Cleanup temporary files on printer
            await CleanupPrinterFilesAsync(running.PrinterIp);
        }
        catch (Exception ex)
        {
            LogStatus($"Error checking session {deviceId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Check calibration status via SSH.
    /// </summary>
    private async Task<(bool pidExists, string? output, MeshData? meshData, string? error)> CheckCalibrationStatusAsync(
        RunningSession running, CancellationToken ct)
    {
        return await Task.Run<(bool, string?, MeshData?, string?)>(() =>
        {
            try
            {
                // Need printer config to get SSH credentials - read from running session
                // For now, use default SSH credentials
                using var client = new SshClient(running.PrinterIp, 22, "root", "rockchip");
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);
                client.Connect();

                if (!client.IsConnected)
                    return (false, null, null, "SSH connection failed");

                // Check PID file - first get the PID, then check if process running
                var pidRead = client.RunCommand($"cat {PrinterPidPath} 2>/dev/null");
                var pidValue = pidRead.Result.Trim();

                bool pidExists = false;
                string pidCheckResult = "";
                if (!string.IsNullOrEmpty(pidValue))
                {
                    // Use multiple methods to check if process is running
                    var processCheck = client.RunCommand($"ps | grep -w {pidValue} | grep -v grep || echo NOT_FOUND");
                    pidCheckResult = processCheck.Result.Trim();
                    pidExists = !pidCheckResult.Contains("NOT_FOUND") && pidCheckResult.Length > 0;
                }

                // Also check if output file was recently modified (within last 60 seconds)
                var statCheck = client.RunCommand($"stat -c %Y {PrinterOutputPath} 2>/dev/null");
                var fileModTime = statCheck.Result.Trim();
                var fileRecentlyModified = false;
                if (long.TryParse(fileModTime, out var modTimestamp))
                {
                    var modTime = DateTimeOffset.FromUnixTimeSeconds(modTimestamp).UtcDateTime;
                    fileRecentlyModified = (DateTime.UtcNow - modTime).TotalSeconds < 60;
                }

                // Read output file - only last 10000 chars to avoid huge reads
                var outCheck = client.RunCommand($"tail -c 10000 {PrinterOutputPath} 2>/dev/null");
                var output = outCheck.ExitStatus == 0 ? outCheck.Result : null;

                // Build debug info for logging
                var hasSuccess = output?.Contains("SUCCESS") == true;
                var hasError = output?.Contains("ERROR") == true;
                var debugInfo = $"PID={(!string.IsNullOrEmpty(pidValue) ? pidValue : "none")}, running={pidExists}, psResult={pidCheckResult.Replace("\n", " ").Substring(0, Math.Min(50, pidCheckResult.Length))}, fileRecent={fileRecentlyModified}, hasOutput={output != null}, SUCCESS={hasSuccess}, ERROR={hasError}";

                // If file was recently modified, assume script is still running
                if (fileRecentlyModified && !hasSuccess && !hasError)
                {
                    return (true, output, null, null); // Treat as still running
                }

                // If not running and no clear result, include the error info in the returned error string for debugging
                if (!pidExists && !hasSuccess && !hasError)
                {
                    return (pidExists, output, null, $"Script not running, no result. Debug: {debugInfo}");
                }

                MeshData? meshData = null;

                // If not running and we have SUCCESS, read mesh data
                if (!pidExists && output?.Contains("SUCCESS") == true)
                {
                    var meshCheck = client.RunCommand($"cat {PrinterMutableCfgPath} 2>/dev/null");
                    if (meshCheck.ExitStatus == 0)
                    {
                        meshData = ParseMeshData(meshCheck.Result);
                    }

                    // Also read printer.generated.cfg to get probe_count for accurate coordinate calculation
                    if (meshData != null)
                    {
                        var generatedCfgCheck = client.RunCommand($"cat {PrinterGeneratedCfgPath} 2>/dev/null");
                        if (generatedCfgCheck.ExitStatus == 0)
                        {
                            ParseProbeCount(generatedCfgCheck.Result, meshData);
                        }
                    }
                }

                client.Disconnect();
                return (pidExists, output, meshData, (string?)null);
            }
            catch (Exception ex)
            {
                return (false, null, null, ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Cleanup temporary files on the printer after calibration completes.
    /// </summary>
    private async Task CleanupPrinterFilesAsync(string printerIp)
    {
        await Task.Run(() =>
        {
            try
            {
                using var client = new SshClient(printerIp, 22, "root", "rockchip");
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);
                client.Connect();

                if (client.IsConnected)
                {
                    // First check if the calibration script is still running
                    var pidCheck = client.RunCommand($"cat {PrinterPidPath} 2>/dev/null && ps -p $(cat {PrinterPidPath} 2>/dev/null) > /dev/null 2>&1 && echo RUNNING");
                    if (pidCheck.Result.Contains("RUNNING"))
                    {
                        LogStatus($"Calibration still running on printer {printerIp}, skipping cleanup");
                        client.Disconnect();
                        return;
                    }

                    // Remove script, output, and PID files
                    client.RunCommand($"rm -f {PrinterScriptPath} {PrinterOutputPath} {PrinterPidPath}");
                    client.Disconnect();
                    LogStatus($"Cleaned up temporary files on printer {printerIp}");
                }
            }
            catch (Exception ex)
            {
                LogStatus($"Failed to cleanup printer files: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Complete a session (success or failure).
    /// </summary>
    private async Task CompleteSessionAsync(string deviceId, bool success, string? error, MeshData? meshData = null)
    {
        BedMeshSession? session;
        lock (_lock)
        {
            if (!_activeSessions.TryGetValue(deviceId, out session))
                return;

            _activeSessions.Remove(deviceId);
        }

        session.FinishedUtc = DateTime.UtcNow;
        session.Status = success ? CalibrationStatus.Success : CalibrationStatus.Failed;
        session.ErrorMessage = error;
        session.MeshData = meshData;

        if (meshData != null)
        {
            meshData.Stats = MeshStats.Calculate(meshData);
        }

        // Save to calibrations directory
        var timestamp = session.StartedUtc.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{deviceId}_{timestamp}.mesh";
        var savePath = Path.Combine(CalibrationsDir, fileName);

        try
        {
            var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(savePath, json);
            LogStatus($"Calibration saved: {fileName}");
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to save calibration: {ex.Message}");
        }

        // Delete running session file
        var runningPath = GetRunningSessionPath(deviceId);
        try
        {
            if (File.Exists(runningPath))
                File.Delete(runningPath);
        }
        catch { }

        LogStatus($"Calibration {(success ? "completed" : "failed")} for {session.PrinterName}");
        SessionCompleted?.Invoke(this, session);
    }

    /// <summary>
    /// Parse mesh data from printer_mutable.cfg content.
    /// </summary>
    private MeshData? ParseMeshData(string content)
    {
        try
        {
            // The file contains JSON with "bed_mesh default" section
            // Format: {"bed_mesh default": {"algo": "bicubic", "min_x": "4", ...}}
            var meshData = new MeshData();

            // Find bed_mesh default section
            var match = Regex.Match(content, @"""bed_mesh default""\s*:\s*\{([^}]+)\}", RegexOptions.Singleline);
            if (!match.Success)
                return null;

            var meshSection = match.Groups[1].Value;

            // Parse algorithm
            var algoMatch = Regex.Match(meshSection, @"""algo""\s*:\s*""([^""]+)""");
            if (algoMatch.Success)
                meshData.Algorithm = algoMatch.Groups[1].Value;

            // Parse bounds
            meshData.MinX = ParseDoubleField(meshSection, "min_x");
            meshData.MaxX = ParseDoubleField(meshSection, "max_x");
            meshData.MinY = ParseDoubleField(meshSection, "min_y");
            meshData.MaxY = ParseDoubleField(meshSection, "max_y");

            // Parse mesh interpolation settings
            var tensionValue = ParseDoubleField(meshSection, "tension");
            if (tensionValue != 0) meshData.Tension = tensionValue;

            var meshXPps = (int)ParseDoubleField(meshSection, "mesh_x_pps");
            if (meshXPps > 0) meshData.MeshXPps = meshXPps;

            var meshYPps = (int)ParseDoubleField(meshSection, "mesh_y_pps");
            if (meshYPps > 0) meshData.MeshYPps = meshYPps;

            // Parse points - get actual dimensions from the points array, not x_count/y_count
            var pointsMatch = Regex.Match(meshSection, @"""points""\s*:\s*""([^""]+)""");
            if (pointsMatch.Success)
            {
                var pointsStr = pointsMatch.Groups[1].Value;
                // Points are in format: "-0.044167, -0.036111, ...\n-0.050556, ..."
                var rows = pointsStr.Split(new[] { "\\n" }, StringSplitOptions.RemoveEmptyEntries);
                var points = new List<double[]>();

                foreach (var row in rows)
                {
                    var values = row.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => double.TryParse(s.Trim(), out var v) ? v : 0.0)
                        .ToArray();
                    if (values.Length > 0)
                        points.Add(values);
                }

                meshData.Points = points.ToArray();

                // Set X/Y count from actual points array dimensions
                meshData.YCount = points.Count;
                meshData.XCount = points.Count > 0 ? points[0].Length : 0;
            }

            // Parse extruder section for nozzle info
            var extruderMatch = Regex.Match(content, @"""extruder""\s*:\s*\{([^}]+)\}", RegexOptions.Singleline);
            if (extruderMatch.Success)
            {
                var extruderSection = extruderMatch.Groups[1].Value;
                var nozzleDia = ParseDoubleField(extruderSection, "nozzle_diameter");
                if (nozzleDia > 0) meshData.NozzleDiameter = nozzleDia;

                var nozzleMaterialMatch = Regex.Match(extruderSection, @"""nozzle_material""\s*:\s*""([^""]+)""");
                if (nozzleMaterialMatch.Success)
                    meshData.NozzleMaterial = nozzleMaterialMatch.Groups[1].Value;
            }

            // Parse probe section for z_offset
            var probeMatch = Regex.Match(content, @"""probe""\s*:\s*\{([^}]+)\}", RegexOptions.Singleline);
            if (probeMatch.Success)
            {
                var probeSection = probeMatch.Groups[1].Value;
                var zOffset = ParseDoubleField(probeSection, "z_offset");
                if (zOffset != 0) meshData.ZOffset = zOffset;
            }

            return meshData;
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to parse mesh data: {ex.Message}");
            return null;
        }
    }

    private double ParseDoubleField(string content, string fieldName)
    {
        var match = Regex.Match(content, $@"""{fieldName}""\s*:\s*""?([^"",}}]+)""?");
        if (match.Success && double.TryParse(match.Groups[1].Value.Trim(), out var value))
            return value;
        return 0;
    }

    /// <summary>
    /// Parse probe_count from [bed_mesh] section in printer.custom.cfg.
    /// Format: probe_count: 4,4 (or probe_count: 4, 4)
    /// </summary>
    private void ParseProbeCount(string content, MeshData meshData)
    {
        try
        {
            // Look for [bed_mesh] section and probe_count line
            // printer.custom.cfg uses INI format, not JSON
            var bedMeshMatch = Regex.Match(content, @"\[bed_mesh\](.*?)(?=\[|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!bedMeshMatch.Success)
                return;

            var bedMeshSection = bedMeshMatch.Groups[1].Value;

            // Match probe_count: X,Y (with optional spaces)
            var probeCountMatch = Regex.Match(bedMeshSection, @"probe_count\s*:\s*(\d+)\s*,\s*(\d+)", RegexOptions.IgnoreCase);
            if (probeCountMatch.Success)
            {
                if (int.TryParse(probeCountMatch.Groups[1].Value, out var probeX))
                    meshData.ProbeCountX = probeX;
                if (int.TryParse(probeCountMatch.Groups[2].Value, out var probeY))
                    meshData.ProbeCountY = probeY;

                LogStatus($"Parsed probe_count: {meshData.ProbeCountX},{meshData.ProbeCountY}");
            }
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to parse probe_count: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse current step from output file.
    /// </summary>
    private string ParseCurrentStep(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return "Starting...";

        // Find last STEP line
        var lines = output.Split('\n').Reverse();
        foreach (var line in lines)
        {
            if (line.StartsWith("STEP:"))
                return line.Substring(5).Trim();
        }

        return "Calibrating...";
    }

    /// <summary>
    /// Parse error message from output file.
    /// </summary>
    private string? ParseErrorMessage(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return null;

        var lines = output.Split('\n').Reverse();
        foreach (var line in lines)
        {
            if (line.StartsWith("ERROR:"))
                return line.Substring(6).Trim();
        }

        return null;
    }

    /// <summary>
    /// Load brief session info from saved files.
    /// </summary>
    private async Task<List<BedMeshSessionInfo>> LoadSessionInfosAsync(string[] files)
    {
        var infos = new List<BedMeshSessionInfo>();

        foreach (var file in files.OrderByDescending(f => f))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var session = JsonSerializer.Deserialize<BedMeshSession>(json);
                if (session != null)
                {
                    infos.Add(new BedMeshSessionInfo
                    {
                        FileName = Path.GetFileName(file),
                        DeviceId = session.DeviceId,
                        PrinterName = session.PrinterName,
                        DeviceType = session.DeviceType,
                        Name = session.Name,
                        Timestamp = session.StartedUtc,
                        Status = session.Status,
                        MeshRange = session.MeshData?.Stats?.Range
                    });
                }
            }
            catch { }
        }

        return infos;
    }

    private string GetRunningSessionPath(string deviceId) => Path.Combine(SessionsDir, $"{deviceId}.running.json");

    private void EnsureDirectoriesExist()
    {
        if (!Directory.Exists(SessionsDir))
            Directory.CreateDirectory(SessionsDir);
        if (!Directory.Exists(CalibrationsDir))
            Directory.CreateDirectory(CalibrationsDir);
        if (!Directory.Exists(AnalysesDir))
            Directory.CreateDirectory(AnalysesDir);
    }

    private void LogStatus(string message)
    {
        StatusChanged?.Invoke(this, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        GC.SuppressFinalize(this);
    }
}
