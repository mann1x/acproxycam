// FileLogger.cs - File logging with rotation and log levels

namespace ACProxyCam.Daemon;

/// <summary>
/// Log severity levels.
/// </summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// Thread-safe file logger with automatic rotation and log levels.
/// </summary>
public class FileLogger : IDisposable
{
    private readonly string _logDirectory;
    private readonly string _baseFileName;
    private readonly long _maxFileSize;
    private readonly int _maxFiles;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentFilePath = string.Empty;
    private long _currentFileSize;
    private bool _disposed;

    /// <summary>
    /// Minimum log level to output. Messages below this level are ignored.
    /// </summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>
    /// Create a new file logger.
    /// </summary>
    /// <param name="logDirectory">Directory for log files</param>
    /// <param name="baseFileName">Base name for log files (without extension)</param>
    /// <param name="maxFileSizeMb">Maximum file size in MB before rotation (default: 10MB)</param>
    /// <param name="maxFiles">Maximum number of rotated files to keep (default: 7)</param>
    public FileLogger(string logDirectory, string baseFileName = "acproxycam", int maxFileSizeMb = 10, int maxFiles = 7)
    {
        _logDirectory = logDirectory;
        _baseFileName = baseFileName;
        _maxFileSize = maxFileSizeMb * 1024 * 1024;
        _maxFiles = maxFiles;

        EnsureDirectoryExists();
        OpenLogFile();
    }

    /// <summary>
    /// Log a message at the specified level.
    /// </summary>
    public void Log(LogLevel level, string message)
    {
        if (level < MinLevel) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var levelStr = level switch
        {
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "???"
        };
        var line = $"[{timestamp}] [{levelStr}] {message}";

        lock (_lock)
        {
            if (_disposed) return;

            try
            {
                // Write to console (for journald)
                Console.WriteLine(line);

                // Write to file
                _writer?.WriteLine(line);
                _writer?.Flush();

                _currentFileSize += line.Length + Environment.NewLine.Length;

                // Check if rotation is needed
                if (_currentFileSize >= _maxFileSize)
                {
                    RotateLogFile();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FileLogger error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Log a message at the specified level with context.
    /// </summary>
    public void Log(LogLevel level, string context, string message)
    {
        Log(level, $"[{context}] {message}");
    }

    /// <summary>
    /// Log at Info level.
    /// </summary>
    public void Log(string message) => Log(LogLevel.Info, message);

    /// <summary>
    /// Log at Info level with context.
    /// </summary>
    public void Log(string context, string message) => Log(LogLevel.Info, context, message);

    /// <summary>
    /// Log at Debug level.
    /// </summary>
    public void LogDebug(string message) => Log(LogLevel.Debug, message);

    /// <summary>
    /// Log at Debug level with context.
    /// </summary>
    public void LogDebug(string context, string message) => Log(LogLevel.Debug, context, message);

    /// <summary>
    /// Log an error message.
    /// </summary>
    public void LogError(string message) => Log(LogLevel.Error, message);

    /// <summary>
    /// Log an error with context.
    /// </summary>
    public void LogError(string context, string message) => Log(LogLevel.Error, context, message);

    /// <summary>
    /// Log a warning message.
    /// </summary>
    public void LogWarning(string message) => Log(LogLevel.Warning, message);

    /// <summary>
    /// Log a warning with context.
    /// </summary>
    public void LogWarning(string context, string message) => Log(LogLevel.Warning, context, message);

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot create log directory {_logDirectory}: {ex.Message}");
        }
    }

    private void OpenLogFile()
    {
        try
        {
            _currentFilePath = Path.Combine(_logDirectory, $"{_baseFileName}.log");

            // Get current file size if exists
            if (File.Exists(_currentFilePath))
            {
                var fileInfo = new FileInfo(_currentFilePath);
                _currentFileSize = fileInfo.Length;

                // Rotate if already too large
                if (_currentFileSize >= _maxFileSize)
                {
                    RotateLogFile();
                    return;
                }
            }
            else
            {
                _currentFileSize = 0;
            }

            _writer = new StreamWriter(_currentFilePath, append: true)
            {
                AutoFlush = false
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot open log file {_currentFilePath}: {ex.Message}");
        }
    }

    private void RotateLogFile()
    {
        try
        {
            // Close current writer
            _writer?.Close();
            _writer?.Dispose();
            _writer = null;

            // Delete oldest files if we have too many
            CleanupOldFiles();

            // Rename current log to timestamped name
            if (File.Exists(_currentFilePath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var rotatedPath = Path.Combine(_logDirectory, $"{_baseFileName}.{timestamp}.log");
                File.Move(_currentFilePath, rotatedPath);

                // Compress the rotated file (optional, skip if gzip not available)
                TryCompressFile(rotatedPath);
            }

            // Open new log file
            _currentFileSize = 0;
            _writer = new StreamWriter(_currentFilePath, append: false)
            {
                AutoFlush = false
            };

            Log(LogLevel.Info, "Log file rotated");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log rotation error: {ex.Message}");

            // Try to reopen the file anyway
            try
            {
                _writer = new StreamWriter(_currentFilePath, append: true)
                {
                    AutoFlush = false
                };
            }
            catch { }
        }
    }

    private void CleanupOldFiles()
    {
        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, $"{_baseFileName}.*.log*")
                .OrderByDescending(f => File.GetCreationTime(f))
                .Skip(_maxFiles - 1) // Keep maxFiles - 1 (plus the new one we're about to create)
                .ToList();

            foreach (var file in logFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private void TryCompressFile(string filePath)
    {
        // Skip compression - logrotate handles this if configured
        // This keeps the implementation simple and avoids gzip dependency
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _writer?.Flush();
                _writer?.Close();
                _writer?.Dispose();
                _writer = null;
            }
            catch { }
        }
    }
}

/// <summary>
/// Static logger accessor for global logging.
/// </summary>
public static class Logger
{
    private static FileLogger? _instance;
    private static readonly object _lock = new();
    private static bool _consoleOnly;

    /// <summary>
    /// Whether debug logging is enabled.
    /// </summary>
    public static bool DebugEnabled { get; set; }

    /// <summary>
    /// Whether to log only to console (no file logging). Used for Docker.
    /// </summary>
    public static bool ConsoleOnly
    {
        get => _consoleOnly;
        set => _consoleOnly = value;
    }

    /// <summary>
    /// Initialize the global logger.
    /// </summary>
    public static void Initialize(string logDirectory, string baseFileName = "acproxycam")
    {
        lock (_lock)
        {
            _instance?.Dispose();

            // Skip file logging if console-only mode
            if (_consoleOnly)
            {
                _instance = null;
                return;
            }

            _instance = new FileLogger(logDirectory, baseFileName);
            if (DebugEnabled)
            {
                _instance.MinLevel = LogLevel.Debug;
            }
        }
    }

    /// <summary>
    /// Log at specified level.
    /// </summary>
    public static void Log(LogLevel level, string message)
    {
        if (_instance != null)
        {
            _instance.Log(level, message);
        }
        else if (level >= (DebugEnabled ? LogLevel.Debug : LogLevel.Info))
        {
            var levelStr = level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "???"
            };
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{levelStr}] {message}");
        }
    }

    /// <summary>
    /// Log at specified level with context.
    /// </summary>
    public static void Log(LogLevel level, string context, string message)
    {
        Log(level, $"[{context}] {message}");
    }

    /// <summary>
    /// Log at Info level.
    /// </summary>
    public static void Log(string message) => Log(LogLevel.Info, message);

    /// <summary>
    /// Log at Info level with context.
    /// </summary>
    public static void Log(string context, string message) => Log(LogLevel.Info, context, message);

    /// <summary>
    /// Log at Debug level.
    /// </summary>
    public static void Debug(string message) => Log(LogLevel.Debug, message);

    /// <summary>
    /// Log at Debug level with context.
    /// </summary>
    public static void Debug(string context, string message) => Log(LogLevel.Debug, context, message);

    /// <summary>
    /// Log an error.
    /// </summary>
    public static void Error(string message) => Log(LogLevel.Error, message);

    /// <summary>
    /// Log an error with context.
    /// </summary>
    public static void Error(string context, string message) => Log(LogLevel.Error, context, message);

    /// <summary>
    /// Log a warning.
    /// </summary>
    public static void Warning(string message) => Log(LogLevel.Warning, message);

    /// <summary>
    /// Log a warning with context.
    /// </summary>
    public static void Warning(string context, string message) => Log(LogLevel.Warning, context, message);

    /// <summary>
    /// Shutdown the logger.
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _instance?.Dispose();
            _instance = null;
        }
    }
}
