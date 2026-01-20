// CpuAffinityService.cs - CPU affinity management for Linux

using System.Runtime.InteropServices;

namespace ACProxyCam.Services;

/// <summary>
/// Manages CPU affinity for threads on Linux.
/// Distributes work across CPUs starting from the highest-numbered CPU
/// to avoid contention with system processes on lower CPUs.
/// </summary>
public static class CpuAffinityService
{
    // Linux syscall for setting thread CPU affinity
    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, IntPtr cpusetsize, ref ulong mask);

    // Get current thread ID on Linux
    [DllImport("libc")]
    private static extern int gettid();

    private static int[]? _availableCpus;
    private static readonly object _lock = new();

    /// <summary>
    /// Get the list of available CPU cores on the system.
    /// </summary>
    public static int[] GetAvailableCpus()
    {
        lock (_lock)
        {
            if (_availableCpus != null)
                return _availableCpus;

            _availableCpus = DetectCpus();
            return _availableCpus;
        }
    }

    /// <summary>
    /// Detect available CPUs by reading /sys/devices/system/cpu/
    /// </summary>
    private static int[] DetectCpus()
    {
        var cpus = new List<int>();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // On non-Linux, return Environment.ProcessorCount as fake CPU list
            for (int i = 0; i < Environment.ProcessorCount; i++)
                cpus.Add(i);
            return cpus.ToArray();
        }

        try
        {
            // Read from /sys/devices/system/cpu/online
            // Format: "0-3" or "0,2-4,6" etc.
            var onlinePath = "/sys/devices/system/cpu/online";
            if (File.Exists(onlinePath))
            {
                var content = File.ReadAllText(onlinePath).Trim();
                cpus = ParseCpuList(content);
            }
            else
            {
                // Fallback: enumerate cpu directories
                var cpuDir = "/sys/devices/system/cpu/";
                if (Directory.Exists(cpuDir))
                {
                    foreach (var dir in Directory.GetDirectories(cpuDir, "cpu*"))
                    {
                        var name = Path.GetFileName(dir);
                        if (name.StartsWith("cpu") && int.TryParse(name.AsSpan(3), out var num))
                        {
                            // Check if online
                            var onlineFile = Path.Combine(dir, "online");
                            if (!File.Exists(onlineFile) || File.ReadAllText(onlineFile).Trim() == "1")
                            {
                                cpus.Add(num);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback to ProcessorCount
        }

        if (cpus.Count == 0)
        {
            for (int i = 0; i < Environment.ProcessorCount; i++)
                cpus.Add(i);
        }

        cpus.Sort();
        return cpus.ToArray();
    }

    /// <summary>
    /// Parse CPU list format like "0-3" or "0,2-4,6"
    /// </summary>
    private static List<int> ParseCpuList(string input)
    {
        var result = new List<int>();
        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            if (part.Contains('-'))
            {
                var range = part.Split('-');
                if (range.Length == 2 &&
                    int.TryParse(range[0], out var start) &&
                    int.TryParse(range[1], out var end))
                {
                    for (int i = start; i <= end; i++)
                        result.Add(i);
                }
            }
            else if (int.TryParse(part, out var num))
            {
                result.Add(num);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculate CPU assignments for a given number of printers.
    /// Distributes printers starting from the last CPU, with overflow on the last CPU.
    /// </summary>
    /// <param name="printerCount">Number of printers to assign</param>
    /// <returns>Array of CPU numbers, one per printer</returns>
    public static int[] CalculateCpuAssignments(int printerCount)
    {
        var cpus = GetAvailableCpus();
        if (cpus.Length == 0 || printerCount == 0)
            return Array.Empty<int>();

        var assignments = new int[printerCount];

        // Start from the last CPU and work backwards
        // If more printers than CPUs, extras go to the last CPU
        for (int i = 0; i < printerCount; i++)
        {
            // Calculate which CPU to use
            // First pass: assign one per CPU from last to first
            // Overflow: assign to last CPU
            if (i < cpus.Length)
            {
                // Assign from last CPU backwards
                assignments[i] = cpus[cpus.Length - 1 - i];
            }
            else
            {
                // Overflow: assign to the last (highest numbered) CPU
                assignments[i] = cpus[cpus.Length - 1];
            }
        }

        return assignments;
    }

    /// <summary>
    /// Set CPU affinity for the current thread.
    /// </summary>
    /// <param name="cpuNumber">The CPU core to pin this thread to</param>
    /// <returns>True if successful, false otherwise</returns>
    public static bool SetThreadAffinity(int cpuNumber)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Windows uses different API, skip for now
            return false;
        }

        try
        {
            // Create CPU mask (bit N = CPU N)
            ulong mask = 1UL << cpuNumber;
            var result = sched_setaffinity(0, (IntPtr)sizeof(ulong), ref mask);
            return result == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get current thread's CPU affinity mask.
    /// </summary>
    public static ulong GetThreadAffinityMask()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return 0;

        try
        {
            ulong mask = 0;
            sched_getaffinity(0, (IntPtr)sizeof(ulong), ref mask);
            return mask;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Format CPU assignment for logging.
    /// </summary>
    public static string FormatAssignments(int[] assignments)
    {
        var cpuCounts = assignments.GroupBy(c => c)
            .OrderBy(g => g.Key)
            .Select(g => $"CPU{g.Key}={g.Count()}")
            .ToArray();
        return string.Join(", ", cpuCounts);
    }
}
