// LogThrottler.cs - Progressive log throttling to reduce log spam
// Throttles repetitive error messages using exponential backoff

using System.Collections.Concurrent;

namespace ACProxyCam.Services;

/// <summary>
/// Throttles repetitive log messages using progressive backoff:
/// - First 5 occurrences: log every time
/// - Next 20*20 occurrences: log every 20th time
/// - Next 100*100 occurrences: log every 100th time
/// - After that: log once every 24 hours
/// </summary>
public class LogThrottler
{
    private readonly ConcurrentDictionary<string, ThrottleState> _states = new();
    private readonly Action<string> _logAction;

    // Throttle thresholds
    private const int InitialThreshold = 5;       // First 5 occurrences logged
    private const int MediumInterval = 20;        // Then every 20th
    private const int MediumThreshold = 20 * 20;  // Until 400 total (5 + 395)
    private const int LargeInterval = 100;        // Then every 100th
    private const int LargeThreshold = 100 * 100; // Until 10000 total
    private static readonly TimeSpan DailyInterval = TimeSpan.FromHours(24);

    public LogThrottler(Action<string> logAction)
    {
        _logAction = logAction ?? throw new ArgumentNullException(nameof(logAction));
    }

    /// <summary>
    /// Log a message with throttling based on a key.
    /// </summary>
    /// <param name="key">Unique key to identify the error type/source</param>
    /// <param name="message">The message to log</param>
    public void Log(string key, string message)
    {
        var state = _states.GetOrAdd(key, _ => new ThrottleState());

        lock (state)
        {
            state.Count++;
            var now = DateTime.UtcNow;

            // Determine if we should log
            bool shouldLog = false;
            string? suffix = null;

            if (state.Count <= InitialThreshold)
            {
                // First 5: log every time
                shouldLog = true;
            }
            else if (state.Count <= InitialThreshold + MediumThreshold)
            {
                // Next phase: log every 20th
                var countInPhase = state.Count - InitialThreshold;
                if (countInPhase % MediumInterval == 0)
                {
                    shouldLog = true;
                    suffix = $" ({state.Count} times same error)";
                }
            }
            else if (state.Count <= InitialThreshold + MediumThreshold + LargeThreshold)
            {
                // Next phase: log every 100th
                var countInPhase = state.Count - InitialThreshold - MediumThreshold;
                if (countInPhase % LargeInterval == 0)
                {
                    shouldLog = true;
                    suffix = $" ({state.Count} times same error)";
                }
            }
            else
            {
                // Final phase: log once per 24 hours
                if (state.LastLogged == null || (now - state.LastLogged.Value) >= DailyInterval)
                {
                    shouldLog = true;
                    suffix = " (last 24h)";
                }
            }

            if (shouldLog)
            {
                state.LastLogged = now;
                var finalMessage = suffix != null ? message + suffix : message;
                _logAction(finalMessage);
            }
        }
    }

    /// <summary>
    /// Reset throttling state for a specific key (e.g., when connection succeeds).
    /// </summary>
    public void Reset(string key)
    {
        _states.TryRemove(key, out _);
    }

    /// <summary>
    /// Reset all throttling states.
    /// </summary>
    public void ResetAll()
    {
        _states.Clear();
    }

    /// <summary>
    /// Check if a message should be logged (without actually logging).
    /// Useful for external logging systems that need throttle decisions.
    /// </summary>
    public (bool ShouldLog, string? Suffix, int Count) CheckThrottle(string key)
    {
        var state = _states.GetOrAdd(key, _ => new ThrottleState());

        lock (state)
        {
            state.Count++;
            var now = DateTime.UtcNow;

            bool shouldLog = false;
            string? suffix = null;

            if (state.Count <= InitialThreshold)
            {
                shouldLog = true;
            }
            else if (state.Count <= InitialThreshold + MediumThreshold)
            {
                var countInPhase = state.Count - InitialThreshold;
                if (countInPhase % MediumInterval == 0)
                {
                    shouldLog = true;
                    suffix = $" ({state.Count} times same error)";
                }
            }
            else if (state.Count <= InitialThreshold + MediumThreshold + LargeThreshold)
            {
                var countInPhase = state.Count - InitialThreshold - MediumThreshold;
                if (countInPhase % LargeInterval == 0)
                {
                    shouldLog = true;
                    suffix = $" ({state.Count} times same error)";
                }
            }
            else
            {
                if (state.LastLogged == null || (now - state.LastLogged.Value) >= DailyInterval)
                {
                    shouldLog = true;
                    suffix = " (last 24h)";
                }
            }

            if (shouldLog)
            {
                state.LastLogged = now;
            }

            return (shouldLog, suffix, state.Count);
        }
    }

    /// <summary>
    /// Get the current count for a key without incrementing.
    /// </summary>
    public int GetCount(string key)
    {
        if (_states.TryGetValue(key, out var state))
        {
            lock (state)
            {
                return state.Count;
            }
        }
        return 0;
    }

    private class ThrottleState
    {
        public int Count;
        public DateTime? LastLogged;
    }
}

/// <summary>
/// Static helper for FFmpeg log throttling with stderr output.
/// More aggressive throttling than general logs since FFmpeg can spam thousands of errors on disconnect.
/// </summary>
public static class FfmpegLogThrottler
{
    private static readonly ConcurrentDictionary<string, ThrottleState> _states = new();
    private static readonly object _lock = new();

    // More aggressive throttling for FFmpeg - only log first occurrence, then summary
    private const int InitialThreshold = 1;       // Only log first occurrence
    private const int MediumInterval = 100;       // Then every 100th
    private const int MediumThreshold = 1000;     // Until 1000 total
    private const int LargeInterval = 1000;       // Then every 1000th
    private const int LargeThreshold = 100000;    // Until 100000 total
    private static readonly TimeSpan DailyInterval = TimeSpan.FromHours(1); // Suppress for 1 hour, not 24

    /// <summary>
    /// Check if an FFmpeg log message should be output and return the formatted message.
    /// Returns null if the message should be suppressed.
    /// </summary>
    public static string? ThrottleMessage(string message)
    {
        // Extract a key from the message - use the message pattern without specific values
        var key = ExtractMessageKey(message);

        var state = _states.GetOrAdd(key, _ => new ThrottleState());

        lock (state)
        {
            state.Count++;
            var now = DateTime.UtcNow;

            bool shouldLog = false;
            string? suffix = null;

            if (state.Count <= InitialThreshold)
            {
                shouldLog = true;
            }
            else if (state.Count <= InitialThreshold + MediumThreshold)
            {
                var countInPhase = state.Count - InitialThreshold;
                if (countInPhase % MediumInterval == 0)
                {
                    shouldLog = true;
                    suffix = $" (repeated {state.Count} times)";
                }
            }
            else if (state.Count <= InitialThreshold + MediumThreshold + LargeThreshold)
            {
                var countInPhase = state.Count - InitialThreshold - MediumThreshold;
                if (countInPhase % LargeInterval == 0)
                {
                    shouldLog = true;
                    suffix = $" (repeated {state.Count} times)";
                }
            }
            else
            {
                if (state.LastLogged == null || (now - state.LastLogged.Value) >= DailyInterval)
                {
                    shouldLog = true;
                    suffix = " (suppressed for 24h)";
                }
            }

            if (shouldLog)
            {
                state.LastLogged = now;
                return suffix != null ? message.TrimEnd() + suffix : message;
            }

            return null;
        }
    }

    /// <summary>
    /// Reset all FFmpeg log throttling states.
    /// </summary>
    public static void ResetAll()
    {
        _states.Clear();
    }

    /// <summary>
    /// Reset throttling for a specific message pattern.
    /// </summary>
    public static void Reset(string key)
    {
        _states.TryRemove(key, out _);
    }

    /// <summary>
    /// Extract a key from an FFmpeg message by removing variable parts (numbers, etc.)
    /// </summary>
    private static string ExtractMessageKey(string message)
    {
        // Remove numbers to group similar messages together
        // e.g., "Stream ends prematurely at 213108237" -> "Stream ends prematurely at"
        var key = System.Text.RegularExpressions.Regex.Replace(message, @"\d+", "#");

        // Limit key length to prevent memory issues
        if (key.Length > 100)
            key = key[..100];

        return key;
    }

    private class ThrottleState
    {
        public int Count;
        public DateTime? LastLogged;
    }
}
