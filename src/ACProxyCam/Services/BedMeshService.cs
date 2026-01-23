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
    /// Currently active calibration sessions being monitored.
    /// </summary>
    private readonly Dictionary<string, BedMeshSession> _activeSessions = new();

    /// <summary>
    /// Currently active analysis sessions being monitored.
    /// </summary>
    private readonly Dictionary<string, AnalysisSession> _activeAnalyses = new();

    private readonly object _lock = new();

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<BedMeshSession>? SessionCompleted;
    public event EventHandler<AnalysisSession>? AnalysisCompleted;

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
        if (runningSession.IsAnalysis)
        {
            // Recover as analysis session
            var analysisSession = new AnalysisSession
            {
                DeviceId = runningSession.DeviceId,
                PrinterName = runningSession.PrinterName,
                DeviceType = runningSession.DeviceType,
                Name = runningSession.Name,
                StartedUtc = runningSession.StartedUtc,
                HeatSoakMinutes = runningSession.HeatSoakMinutes,
                Status = CalibrationStatus.Running,
                CalibrationCount = runningSession.CalibrationCount,
                CurrentCalibration = runningSession.CurrentCalibration,
                CurrentStep = "Monitoring..."
            };

            lock (_lock)
            {
                _activeAnalyses[analysisSession.DeviceId] = analysisSession;
            }
        }
        else
        {
            // Recover as calibration session
            var session = new BedMeshSession
            {
                DeviceId = runningSession.DeviceId,
                PrinterName = runningSession.PrinterName,
                DeviceType = runningSession.DeviceType,
                Name = runningSession.Name,
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
    /// Start a new analysis (multiple calibrations) for a printer.
    /// </summary>
    public async Task<(bool success, string? error)> StartAnalysisAsync(
        PrinterConfig config,
        int heatSoakMinutes,
        int calibrationCount,
        Func<Task<bool>>? turnLedOn = null,
        string? name = null)
    {
        if (string.IsNullOrEmpty(config.DeviceId))
            return (false, "Printer deviceId is required");

        if (calibrationCount < 3)
            return (false, "Minimum 3 calibrations required for analysis");

        // Check if session already running for this printer
        lock (_lock)
        {
            if (_activeSessions.ContainsKey(config.DeviceId) || _activeAnalyses.ContainsKey(config.DeviceId))
                return (false, "Session already running for this printer");
        }

        try
        {
            EnsureDirectoriesExist();

            // Turn LED on
            if (turnLedOn != null)
            {
                LogStatus($"Turning LED on for {config.Name}...");
                await turnLedOn();
            }

            // Create analysis session
            var session = new AnalysisSession
            {
                DeviceId = config.DeviceId,
                PrinterName = config.Name,
                DeviceType = config.DeviceType,
                Name = name,
                StartedUtc = DateTime.UtcNow,
                HeatSoakMinutes = heatSoakMinutes,
                CalibrationCount = calibrationCount,
                Status = CalibrationStatus.Running,
                CurrentStep = "Starting analysis..."
            };

            // Create running session file
            var runningSession = new RunningSession
            {
                DeviceId = config.DeviceId,
                PrinterName = config.Name,
                DeviceType = config.DeviceType,
                Name = name,
                PrinterIp = config.Ip,
                StartedUtc = session.StartedUtc,
                HeatSoakMinutes = heatSoakMinutes,
                IsAnalysis = true,
                CalibrationCount = calibrationCount,
                CurrentCalibration = 0
            };

            var runningPath = GetRunningSessionPath(config.DeviceId);
            await File.WriteAllTextAsync(runningPath, JsonSerializer.Serialize(runningSession, new JsonSerializerOptions { WriteIndented = true }));

            // Execute analysis script
            var scriptResult = await ExecuteAnalysisScriptAsync(config, heatSoakMinutes, calibrationCount);
            if (!scriptResult.success)
            {
                try { File.Delete(runningPath); } catch { }
                return (false, scriptResult.error);
            }

            // Add to active analyses
            lock (_lock)
            {
                _activeAnalyses[config.DeviceId] = session;
            }

            LogStatus($"Analysis started for {config.Name} ({calibrationCount} calibrations)");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Get all active sessions (calibrations and analyses).
    /// </summary>
    public List<BedMeshSession> GetActiveSessions()
    {
        lock (_lock)
        {
            return _activeSessions.Values.ToList();
        }
    }

    /// <summary>
    /// Get all active analysis sessions.
    /// </summary>
    public List<AnalysisSession> GetActiveAnalyses()
    {
        lock (_lock)
        {
            return _activeAnalyses.Values.ToList();
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
            summary.ActiveCount = _activeSessions.Count + _activeAnalyses.Count;

            // Include both calibrations and analyses in active sessions
            var activeSessions = new List<BedMeshSession>();
            activeSessions.AddRange(_activeSessions.Values);

            // Convert active analyses to BedMeshSession for display
            foreach (var analysis in _activeAnalyses.Values)
            {
                var currentCalib = analysis.CurrentCalibration > 0 ? analysis.CurrentCalibration : 1;
                // Check if step already has [N/M] prefix from script output
                var stepAlreadyHasPrefix = analysis.CurrentStep?.StartsWith("[") == true;
                var stepDisplay = !string.IsNullOrEmpty(analysis.CurrentStep)
                    ? (stepAlreadyHasPrefix ? analysis.CurrentStep : $"[{currentCalib}/{analysis.CalibrationCount}] {analysis.CurrentStep}")
                    : $"Calibration {currentCalib}/{analysis.CalibrationCount}";

                activeSessions.Add(new BedMeshSession
                {
                    DeviceId = analysis.DeviceId,
                    PrinterName = analysis.PrinterName,
                    DeviceType = analysis.DeviceType,
                    Name = analysis.Name,
                    StartedUtc = analysis.StartedUtc,
                    FinishedUtc = analysis.FinishedUtc,
                    HeatSoakMinutes = analysis.HeatSoakMinutes,
                    Status = analysis.Status,
                    ErrorMessage = analysis.ErrorMessage,
                    IsAnalysis = true,
                    CurrentStep = stepDisplay
                });
            }

            summary.ActiveSessions = activeSessions;
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
    /// Load a saved analysis by filename.
    /// </summary>
    public async Task<AnalysisSession?> LoadAnalysisAsync(string fileName)
    {
        var path = Path.Combine(AnalysesDir, fileName);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<AnalysisSession>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Delete a saved analysis.
    /// </summary>
    public bool DeleteAnalysis(string fileName)
    {
        var path = Path.Combine(AnalysesDir, fileName);
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
    /// Execute the analysis shell script on the printer via SSH.
    /// </summary>
    private async Task<(bool success, string? error)> ExecuteAnalysisScriptAsync(PrinterConfig config, int heatSoakMinutes, int calibrationCount)
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

                LogStatus($"SSH connected to {config.Ip}, creating analysis script...");

                // Create the shell script for analysis
                var script = GenerateAnalysisScript(heatSoakMinutes, calibrationCount);
                script = script.Replace("\r\n", "\n").Replace("\r", "\n");

                var scriptBytes = System.Text.Encoding.UTF8.GetBytes(script);
                var scriptBase64 = Convert.ToBase64String(scriptBytes);

                var writeCmd = client.RunCommand($"echo '{scriptBase64}' | base64 -d > {PrinterScriptPath}");
                if (writeCmd.ExitStatus != 0)
                    return (false, $"Failed to create script: {writeCmd.Error}");

                client.RunCommand($"chmod +x {PrinterScriptPath}");

                LogStatus($"Starting analysis script on {config.Name} ({calibrationCount} calibrations)...");
                var runCmd = client.RunCommand($"nohup sh {PrinterScriptPath} > /dev/null 2>&1 &");

                Thread.Sleep(500);
                var pidCheck = client.RunCommand($"cat {PrinterPidPath} 2>/dev/null");
                if (pidCheck.ExitStatus != 0 || string.IsNullOrWhiteSpace(pidCheck.Result))
                {
                    var outCheck = client.RunCommand($"cat {PrinterOutputPath} 2>/dev/null");
                    return (false, $"Script failed to start. Output: {outCheck.Result}");
                }

                var pid = pidCheck.Result.Trim();
                LogStatus($"Analysis script started with PID {pid}");

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
    /// Generate the analysis shell script (multiple calibrations).
    /// </summary>
    private string GenerateAnalysisScript(int heatSoakMinutes, int calibrationCount)
    {
        // Heat soak section - only before first calibration
        var heatSoakSection = heatSoakMinutes > 0 ? $@"
# Heat soak (only before first calibration)
echo ""STEP: Heat soak - setting bed to 60C via Moonraker API"" >> ""$LOG""
wget -q -O /dev/null 'http://localhost:7125/printer/gcode/script?script=SET_HEATER_TEMPERATURE%20HEATER=heater_bed%20TARGET=60' 2>>""$LOG""
echo ""STEP: Heat soak - waiting {heatSoakMinutes} minutes"" >> ""$LOG""
sleep $(({heatSoakMinutes} * 60))
echo ""STEP: Heat soak complete"" >> ""$LOG""
" : "";

        return $@"#!/bin/sh
# ACProxyCam BedMesh Analysis Script - {calibrationCount} calibrations
LOG=""{PrinterOutputPath}""
echo ""$$"" > {PrinterPidPath}
echo ""STARTED $(date -Iseconds)"" > ""$LOG""
echo ""ANALYSIS_COUNT: {calibrationCount}"" >> ""$LOG""

trap 'echo ""SCRIPT_EXIT: code=$? at $(date -Iseconds)"" >> ""$LOG""' EXIT

API_CMD() {{
    local timeout=$1
    local cmd=$2
    printf '%s\003' ""$cmd"" | nc -w ""$timeout"" localhost 18086 2>>""$LOG"" | tr -d '\003' | grep -v 'process_status_update' | grep -v 'process_gcode_response' | tail -1
}}

# Set Busy
echo ""STEP: Setting printer busy"" >> ""$LOG""
API_CMD 30 '{{""id"":1,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":1}}}}'

# Set printer to PAUSE state
echo ""STEP: Setting PAUSE state"" >> ""$LOG""
API_CMD 30 '{{""id"":2,""method"":""gcode/script"",""params"":{{""script"":""PAUSE""}}}}'
{heatSoakSection}
CALIBRATION=1
while [ $CALIBRATION -le {calibrationCount} ]; do
    echo ""CALIBRATION_START: $CALIBRATION"" >> ""$LOG""
    echo ""STEP: Starting calibration $CALIBRATION of {calibrationCount}"" >> ""$LOG""

    # Ensure printer is busy and LED is on at start of each calibration
    API_CMD 30 '{{""id"":1,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":1}}}}'
    API_CMD 30 '{{""id"":2,""method"":""Multicolor/LedCtrl"",""params"":{{""ledState"":1}}}}'

    # Preheating
    echo ""STEP: [$CALIBRATION/{calibrationCount}] Preheating"" >> ""$LOG""
    RESULT=$(API_CMD 120 '{{""id"":3,""method"":""Leviq2/Preheating"",""params"":{{""script"":""LEVIQ2_PREHEATING""}}}}')
    echo ""RESULT_PREHEAT_$CALIBRATION: $RESULT"" >> ""$LOG""
    if echo ""$RESULT"" | grep -q '""error""'; then
        echo ""ERROR: Preheating failed on calibration $CALIBRATION"" >> ""$LOG""
        API_CMD 30 '{{""id"":99,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'
        rm -f {PrinterPidPath}
        exit 1
    fi

    # Wiping
    echo ""STEP: [$CALIBRATION/{calibrationCount}] Wiping"" >> ""$LOG""
    RESULT=$(API_CMD 180 '{{""id"":4,""method"":""Leviq2/Wiping"",""params"":{{""script"":""LEVIQ2_WIPING""}}}}')
    echo ""RESULT_WIPE_$CALIBRATION: $RESULT"" >> ""$LOG""
    if echo ""$RESULT"" | grep -q '""error""'; then
        echo ""ERROR: Wiping failed on calibration $CALIBRATION"" >> ""$LOG""
        API_CMD 30 '{{""id"":99,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'
        rm -f {PrinterPidPath}
        exit 1
    fi

    # Probe
    echo ""STEP: [$CALIBRATION/{calibrationCount}] Probing"" >> ""$LOG""
    RESULT=$(API_CMD 3600 '{{""id"":5,""method"":""Leviq2/Probe"",""params"":{{""script"":""LEVIQ2_PROBE""}}}}')
    echo ""RESULT_PROBE_$CALIBRATION: $RESULT"" >> ""$LOG""
    if echo ""$RESULT"" | grep -q '""error""'; then
        echo ""ERROR: Probing failed on calibration $CALIBRATION"" >> ""$LOG""
        API_CMD 30 '{{""id"":99,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'
        rm -f {PrinterPidPath}
        exit 1
    fi

    # Save mesh (SAVE_CONFIG)
    echo ""STEP: [$CALIBRATION/{calibrationCount}] Saving mesh"" >> ""$LOG""
    RESULT=$(API_CMD 60 '{{""id"":6,""method"":""Config/PrinterConfSave"",""params"":{{""script"":""SAVE_CONFIG""}}}}')
    echo ""RESULT_SAVE_$CALIBRATION: $RESULT"" >> ""$LOG""
    sync
    sleep 2

    # Copy mesh data to numbered file for later retrieval
    cp {PrinterMutableCfgPath} /tmp/acproxycam_mesh_$CALIBRATION.json 2>/dev/null
    echo ""MESH_SAVED: $CALIBRATION"" >> ""$LOG""
    echo ""CALIBRATION_END: $CALIBRATION"" >> ""$LOG""

    # 1 minute pause between calibrations (except after last one)
    if [ $CALIBRATION -lt {calibrationCount} ]; then
        echo ""STEP: Pausing 1 minute before next calibration"" >> ""$LOG""
        sleep 60
    fi

    CALIBRATION=$((CALIBRATION + 1))
done

# Turn off heaters (only after last calibration)
echo ""STEP: Turning off heaters"" >> ""$LOG""
wget -q -O /dev/null 'http://localhost:7125/printer/gcode/script?script=SET_HEATER_TEMPERATURE%20HEATER=extruder%20TARGET=0' 2>>""$LOG""
wget -q -O /dev/null 'http://localhost:7125/printer/gcode/script?script=SET_HEATER_TEMPERATURE%20HEATER=heater_bed%20TARGET=0' 2>>""$LOG""

# Set Free
echo ""STEP: Setting printer free"" >> ""$LOG""
API_CMD 30 '{{""id"":9,""method"":""Printer/ReportUIWorkStatus"",""params"":{{""busy"":0}}}}'

# Restart printer to clear PAUSE state
echo ""STEP: Restarting printer"" >> ""$LOG""
API_CMD 60 '{{""id"":10,""method"":""gcode/script"",""params"":{{""script"":""RESTART""}}}}'

echo ""CALIBRATIONS_COMPLETED: $(($CALIBRATION - 1))"" >> ""$LOG""
echo ""SUCCESS $(date -Iseconds)"" >> ""$LOG""
sync
sleep 1
rm -f {PrinterPidPath}
";
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

# 1b. Set printer to PAUSE state
echo ""STEP: Setting PAUSE state"" >> ""$LOG""
API_CMD 30 '{{""id"":2,""method"":""gcode/script"",""params"":{{""script"":""PAUSE""}}}}'
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

# 9. Restart printer to clear PAUSE state
echo ""STEP: Restarting printer"" >> ""$LOG""
API_CMD 60 '{{""id"":10,""method"":""gcode/script"",""params"":{{""script"":""RESTART""}}}}'

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

                List<string> calibrationsToCheck;
                List<string> analysesToCheck;
                lock (_lock)
                {
                    calibrationsToCheck = _activeSessions.Keys.ToList();
                    analysesToCheck = _activeAnalyses.Keys.ToList();
                }

                // Check calibrations
                foreach (var deviceId in calibrationsToCheck)
                {
                    if (ct.IsCancellationRequested) break;
                    await CheckSessionStatusAsync(deviceId, ct);
                }

                // Check analyses
                foreach (var deviceId in analysesToCheck)
                {
                    if (ct.IsCancellationRequested) break;
                    await CheckAnalysisStatusAsync(deviceId, ct);
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

                    // Remove script, output, PID files, and any temporary mesh files
                    client.RunCommand($"rm -f {PrinterScriptPath} {PrinterOutputPath} {PrinterPidPath} /tmp/acproxycam_mesh_*.json");
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
    /// Check the status of a running analysis session.
    /// </summary>
    private async Task CheckAnalysisStatusAsync(string deviceId, CancellationToken ct)
    {
        AnalysisSession? session;
        lock (_lock)
        {
            if (!_activeAnalyses.TryGetValue(deviceId, out session))
                return;
        }

        var sessionAge = DateTime.UtcNow - session.StartedUtc;
        if (sessionAge.TotalSeconds < 30)
            return;

        try
        {
            var runningPath = GetRunningSessionPath(deviceId);
            if (!File.Exists(runningPath))
            {
                await CompleteAnalysisAsync(deviceId, false, "Running session file not found", null);
                return;
            }

            var runningJson = await File.ReadAllTextAsync(runningPath, ct);
            var running = JsonSerializer.Deserialize<RunningSession>(runningJson);
            if (running == null)
            {
                await CompleteAnalysisAsync(deviceId, false, "Invalid running session file", null);
                return;
            }

            // Check status via SSH
            var (pidExists, output, error) = await CheckAnalysisOutputAsync(running, ct);

            if (pidExists)
            {
                // Update progress from output
                session.CurrentStep = ParseCurrentStep(output);

                // Update current calibration number from output
                if (output != null)
                {
                    var calibMatch = Regex.Match(output, @"CALIBRATION_START: (\d+)", RegexOptions.RightToLeft);
                    if (calibMatch.Success && int.TryParse(calibMatch.Groups[1].Value, out var currentCalib))
                    {
                        running.CurrentCalibration = currentCalib;
                        session.CurrentCalibration = currentCalib;
                        // Update the running file
                        await File.WriteAllTextAsync(runningPath, JsonSerializer.Serialize(running, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }
                return;
            }

            // Script finished - check result
            if (output != null && output.Contains("SUCCESS"))
            {
                // Parse all mesh data from the output
                var calibrations = ParseMultipleMeshData(output, running);
                await CompleteAnalysisAsync(deviceId, true, null, calibrations);
            }
            else if (output != null && output.Contains("ERROR"))
            {
                var errorMsg = ParseErrorMessage(output);
                await CompleteAnalysisAsync(deviceId, false, errorMsg ?? "Analysis failed", null);
            }
            else
            {
                await CompleteAnalysisAsync(deviceId, false, error ?? "Unknown analysis result", null);
            }

            await CleanupPrinterFilesAsync(running.PrinterIp);
        }
        catch (Exception ex)
        {
            LogStatus($"Error checking analysis {deviceId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Check analysis output via SSH.
    /// </summary>
    private async Task<(bool pidExists, string? output, string? error)> CheckAnalysisOutputAsync(
        RunningSession running, CancellationToken ct)
    {
        return await Task.Run<(bool, string?, string?)>(() =>
        {
            try
            {
                using var client = new SshClient(running.PrinterIp, 22, "root", "rockchip");
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);
                client.Connect();

                if (!client.IsConnected)
                    return (false, null, "SSH connection failed");

                var pidRead = client.RunCommand($"cat {PrinterPidPath} 2>/dev/null");
                var pidValue = pidRead.Result.Trim();

                bool pidExists = false;
                if (!string.IsNullOrEmpty(pidValue))
                {
                    var processCheck = client.RunCommand($"ps | grep -w {pidValue} | grep -v grep || echo NOT_FOUND");
                    pidExists = !processCheck.Result.Contains("NOT_FOUND") && processCheck.Result.Trim().Length > 0;
                }

                // Read full output for analysis (need all mesh data)
                var outCheck = client.RunCommand($"cat {PrinterOutputPath} 2>/dev/null");
                var output = outCheck.ExitStatus == 0 ? outCheck.Result : null;

                // Check file modification time
                var statCheck = client.RunCommand($"stat -c %Y {PrinterOutputPath} 2>/dev/null");
                if (long.TryParse(statCheck.Result.Trim(), out var modTimestamp))
                {
                    var modTime = DateTimeOffset.FromUnixTimeSeconds(modTimestamp).UtcDateTime;
                    if ((DateTime.UtcNow - modTime).TotalSeconds < 60 &&
                        output?.Contains("SUCCESS") != true &&
                        output?.Contains("ERROR") != true)
                    {
                        return (true, output, null);
                    }
                }

                client.Disconnect();
                return (pidExists, output, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Parse multiple mesh data entries from analysis by reading saved mesh files from printer.
    /// </summary>
    private List<MeshData> ParseMultipleMeshData(string output, RunningSession running)
    {
        var meshes = new List<MeshData>();

        try
        {
            // Find how many calibrations completed
            var completedMatch = Regex.Match(output, @"CALIBRATIONS_COMPLETED:\s*(\d+)");
            var calibrationCount = completedMatch.Success && int.TryParse(completedMatch.Groups[1].Value, out var count)
                ? count
                : running.CalibrationCount;

            if (calibrationCount == 0)
            {
                LogStatus("No calibrations completed");
                return meshes;
            }

            // Read each mesh file directly from the printer
            using var client = new SshClient(running.PrinterIp, 22, "root", "rockchip");
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(SshTimeoutSeconds);
            client.Connect();

            if (!client.IsConnected)
            {
                LogStatus("Failed to connect to printer to read mesh files");
                return meshes;
            }

            // Read probe count for coordinate calculation
            string? generatedCfg = null;
            var generatedCfgCheck = client.RunCommand($"cat {PrinterGeneratedCfgPath} 2>/dev/null");
            if (generatedCfgCheck.ExitStatus == 0)
            {
                generatedCfg = generatedCfgCheck.Result;
            }

            for (int i = 1; i <= calibrationCount; i++)
            {
                var meshPath = $"/tmp/acproxycam_mesh_{i}.json";
                var meshCheck = client.RunCommand($"cat {meshPath} 2>/dev/null");

                if (meshCheck.ExitStatus == 0 && !string.IsNullOrWhiteSpace(meshCheck.Result))
                {
                    var meshData = ParseMeshData(meshCheck.Result);
                    if (meshData != null)
                    {
                        if (generatedCfg != null)
                        {
                            ParseProbeCount(generatedCfg, meshData);
                        }
                        meshData.Stats = MeshStats.Calculate(meshData);
                        meshes.Add(meshData);
                        LogStatus($"Parsed mesh data for calibration {i}: {meshData.XCount}x{meshData.YCount} points");
                    }
                    else
                    {
                        LogStatus($"Failed to parse mesh data for calibration {i}");
                    }
                }
                else
                {
                    LogStatus($"Failed to read mesh file for calibration {i}: {meshPath}");
                }
            }

            // Clean up temporary mesh files
            for (int i = 1; i <= calibrationCount; i++)
            {
                client.RunCommand($"rm -f /tmp/acproxycam_mesh_{i}.json");
            }

            client.Disconnect();
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to parse multiple mesh data: {ex.Message}");
        }

        return meshes;
    }

    /// <summary>
    /// Complete an analysis session.
    /// </summary>
    private async Task CompleteAnalysisAsync(string deviceId, bool success, string? error, List<MeshData>? calibrations)
    {
        AnalysisSession? session;
        lock (_lock)
        {
            if (!_activeAnalyses.TryGetValue(deviceId, out session))
                return;

            _activeAnalyses.Remove(deviceId);
        }

        session.FinishedUtc = DateTime.UtcNow;
        session.Status = success ? CalibrationStatus.Success : CalibrationStatus.Failed;
        session.ErrorMessage = error;

        if (calibrations != null && calibrations.Count > 0)
        {
            session.Calibrations = calibrations;

            // Calculate average mesh
            session.AverageMesh = CalculateAverageMesh(calibrations);
            if (session.AverageMesh != null)
            {
                session.AverageMesh.Stats = MeshStats.Calculate(session.AverageMesh);
            }

            // Calculate analysis statistics
            session.AnalysisStats = AnalysisStats.Calculate(calibrations, session.AverageMesh!);
        }

        // Save to analyses directory
        var timestamp = session.StartedUtc.ToString("yyyyMMdd_HHmmss");
        var fileName = $"{deviceId}_{timestamp}.analysis";
        var savePath = Path.Combine(AnalysesDir, fileName);

        try
        {
            var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(savePath, json);
            LogStatus($"Analysis saved: {fileName}");
        }
        catch (Exception ex)
        {
            LogStatus($"Failed to save analysis: {ex.Message}");
        }

        // Delete running session file
        var runningPath = GetRunningSessionPath(deviceId);
        try { if (File.Exists(runningPath)) File.Delete(runningPath); } catch { }

        LogStatus($"Analysis {(success ? "completed" : "failed")} for {session.PrinterName} ({session.Calibrations.Count} calibrations)");
        AnalysisCompleted?.Invoke(this, session);
    }

    /// <summary>
    /// Calculate average mesh from multiple calibrations.
    /// </summary>
    private MeshData? CalculateAverageMesh(List<MeshData> calibrations)
    {
        if (calibrations.Count == 0)
            return null;

        var first = calibrations[0];
        var rows = first.Points.Length;
        var cols = rows > 0 ? first.Points[0].Length : 0;

        if (rows == 0 || cols == 0)
            return null;

        // Create average mesh with same structure
        var avgMesh = new MeshData
        {
            Algorithm = first.Algorithm,
            MinX = first.MinX,
            MaxX = first.MaxX,
            MinY = first.MinY,
            MaxY = first.MaxY,
            XCount = first.XCount,
            YCount = first.YCount,
            ProbeCountX = first.ProbeCountX,
            ProbeCountY = first.ProbeCountY,
            Tension = first.Tension,
            MeshXPps = first.MeshXPps,
            MeshYPps = first.MeshYPps,
            ZOffset = first.ZOffset,
            NozzleDiameter = first.NozzleDiameter,
            NozzleMaterial = first.NozzleMaterial,
            Points = new double[rows][]
        };

        // Calculate average for each point (including outliers)
        for (int r = 0; r < rows; r++)
        {
            avgMesh.Points[r] = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                var values = new List<double>();
                foreach (var mesh in calibrations)
                {
                    if (r < mesh.Points.Length && c < mesh.Points[r].Length)
                    {
                        values.Add(mesh.Points[r][c]);
                    }
                }
                avgMesh.Points[r][c] = values.Count > 0 ? values.Average() : 0;
            }
        }

        return avgMesh;
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
                var fileName = Path.GetFileName(file);
                var isAnalysis = fileName.EndsWith(".analysis", StringComparison.OrdinalIgnoreCase);

                if (isAnalysis)
                {
                    // Parse as AnalysisSession
                    var analysis = JsonSerializer.Deserialize<AnalysisSession>(json);
                    if (analysis != null)
                    {
                        infos.Add(new BedMeshSessionInfo
                        {
                            FileName = fileName,
                            DeviceId = analysis.DeviceId,
                            PrinterName = analysis.PrinterName,
                            DeviceType = analysis.DeviceType,
                            Name = analysis.Name,
                            Timestamp = analysis.StartedUtc,
                            Status = analysis.Status,
                            MeshRange = analysis.AverageMesh?.Stats?.Range,
                            IsAnalysis = true,
                            CalibrationCount = analysis.Calibrations.Count
                        });
                    }
                }
                else
                {
                    // Parse as BedMeshSession (calibration)
                    var session = JsonSerializer.Deserialize<BedMeshSession>(json);
                    if (session != null)
                    {
                        infos.Add(new BedMeshSessionInfo
                        {
                            FileName = fileName,
                            DeviceId = session.DeviceId,
                            PrinterName = session.PrinterName,
                            DeviceType = session.DeviceType,
                            Name = session.Name,
                            Timestamp = session.StartedUtc,
                            Status = session.Status,
                            MeshRange = session.MeshData?.Stats?.Range,
                            IsAnalysis = false
                        });
                    }
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
