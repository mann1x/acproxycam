// SimpleConsoleUI.cs - Plain console implementation of IConsoleUI

namespace ACProxyCam.Client;

/// <summary>
/// Simple console implementation for automation and non-interactive terminals.
/// Works with expect, pipes, and terminals without ANSI support.
/// </summary>
public class SimpleConsoleUI : IConsoleUI
{
    public void WriteLine(string text = "")
    {
        Console.WriteLine(text);
    }

    public void WriteMarkup(string markup)
    {
        // Strip Spectre markup tags for plain output
        var plain = StripMarkup(markup);
        Console.WriteLine(plain);
    }

    public void WriteError(string message)
    {
        Console.WriteLine($"ERROR: {message}");
    }

    public void WriteWarning(string message)
    {
        Console.WriteLine($"WARNING: {message}");
    }

    public void WriteSuccess(string message)
    {
        Console.WriteLine($"OK: {message}");
    }

    public void WriteInfo(string message)
    {
        Console.WriteLine($"  {message}");
    }

    public void WriteHeader(string version)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("           ACProxyCam");
        Console.WriteLine($"          Version {version}");
        Console.WriteLine("========================================");
        Console.WriteLine();
    }

    public bool Confirm(string prompt, bool defaultValue = true)
    {
        var defaultStr = defaultValue ? "Y/n" : "y/N";
        Console.Write($"{prompt} [{defaultStr}]: ");
        Console.Out.Flush();

        var input = Console.ReadLine()?.Trim().ToLower();

        if (string.IsNullOrEmpty(input))
        {
            return defaultValue;
        }

        return input == "y" || input == "yes";
    }

    public string Ask(string prompt, string? defaultValue = null)
    {
        if (defaultValue != null)
        {
            Console.Write($"{prompt} [{defaultValue}]: ");
        }
        else
        {
            Console.Write($"{prompt}: ");
        }
        Console.Out.Flush();

        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input) && defaultValue != null)
        {
            return defaultValue;
        }

        return input ?? string.Empty;
    }

    public int AskInt(string prompt, int defaultValue)
    {
        Console.Write($"{prompt} [{defaultValue}]: ");
        Console.Out.Flush();

        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input))
        {
            return defaultValue;
        }

        if (int.TryParse(input, out var result))
        {
            return result;
        }

        return defaultValue;
    }

    public string AskSecret(string prompt, string? defaultValue = null)
    {
        // In simple mode, just read normally (no masking)
        return Ask(prompt, defaultValue);
    }

    public string SelectOne(string title, IEnumerable<string> choices)
    {
        var choiceList = choices.ToList();

        Console.WriteLine(title);
        for (int i = 0; i < choiceList.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {choiceList[i]}");
        }

        Console.Write($"Enter choice (1-{choiceList.Count}): ");
        Console.Out.Flush();

        var input = Console.ReadLine()?.Trim();

        if (int.TryParse(input, out var index) && index >= 1 && index <= choiceList.Count)
        {
            return choiceList[index - 1];
        }

        // Default to first choice
        return choiceList[0];
    }

    public string? SelectOneWithEscape(string title, IEnumerable<string> choices)
    {
        var (item, _) = SelectOneWithEscapeAndIndex(title, choices, 0);
        return item;
    }

    public (string? Item, int Index) SelectOneWithEscapeAndIndex(string title, IEnumerable<string> choices, int startIndex = 0)
    {
        var choiceList = choices.ToList();

        Console.WriteLine(title);
        for (int i = 0; i < choiceList.Count; i++)
        {
            // Mark the suggested starting position with asterisk
            var marker = i == startIndex ? "*" : " ";
            Console.WriteLine($" {marker}{i + 1}. {choiceList[i]}");
        }
        Console.WriteLine("  0. Cancel");

        // Show default based on startIndex
        var defaultChoice = startIndex >= 0 && startIndex < choiceList.Count ? (startIndex + 1).ToString() : "";
        Console.Write($"Enter choice (0-{choiceList.Count}) [{defaultChoice}]: ");
        Console.Out.Flush();

        var input = Console.ReadLine()?.Trim();

        // Use default if empty and startIndex is valid
        if (string.IsNullOrEmpty(input) && startIndex >= 0 && startIndex < choiceList.Count)
        {
            return (choiceList[startIndex], startIndex);
        }

        // Cancel on 0 or 'c'
        if (input == "0" || input?.ToLower() == "c")
        {
            return (null, -1);
        }

        if (int.TryParse(input, out var index) && index >= 1 && index <= choiceList.Count)
        {
            return (choiceList[index - 1], index - 1);
        }

        // Invalid input - treat as cancel
        return (null, -1);
    }

    public List<string> SelectMany(string title, IEnumerable<string> choices, string? instructions = null)
    {
        var choiceList = choices.ToList();

        Console.WriteLine(title);
        if (instructions != null)
        {
            Console.WriteLine(instructions);
        }
        for (int i = 0; i < choiceList.Count; i++)
        {
            Console.WriteLine($"  {i + 1}. {choiceList[i]}");
        }

        Console.Write($"Enter choices (comma-separated, e.g. 1,3): ");
        Console.Out.Flush();

        var input = Console.ReadLine()?.Trim();

        var result = new List<string>();

        if (string.IsNullOrEmpty(input))
        {
            return result;
        }

        var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (int.TryParse(part.Trim(), out var index) && index >= 1 && index <= choiceList.Count)
            {
                result.Add(choiceList[index - 1]);
            }
        }

        return result;
    }

    public void Clear()
    {
        // Don't clear in simple mode - keep output visible
        Console.WriteLine();
        Console.WriteLine("----------------------------------------");
        Console.WriteLine();
    }

    public void WaitForKey(string? message = null)
    {
        if (message != null)
        {
            Console.WriteLine(message);
        }
        Console.ReadKey(true);
    }

    public ConsoleKeyInfo ReadKey()
    {
        return Console.ReadKey(true);
    }

    public void WritePanel(string content)
    {
        Console.WriteLine("+--------------------------------------+");
        Console.WriteLine($"| {StripMarkup(content)}");
        Console.WriteLine("+--------------------------------------+");
        Console.WriteLine();
    }

    public void WriteRule(string? title = null)
    {
        if (title != null)
        {
            Console.WriteLine($"--- {title} ---");
        }
        else
        {
            Console.WriteLine("----------------------------------------");
        }
    }

    public void WriteTable(string[] headers, IEnumerable<string[]> rows)
    {
        // Simple tabular output
        Console.WriteLine(string.Join("\t", headers));
        Console.WriteLine(new string('-', 60));
        foreach (var row in rows)
        {
            Console.WriteLine(string.Join("\t", row.Select(StripMarkup)));
        }
        Console.WriteLine();
    }

    public void WriteGrid(IEnumerable<(string Label, string Value)> items)
    {
        foreach (var (label, value) in items)
        {
            Console.WriteLine($"  {label}: {StripMarkup(value)}");
        }
    }

    public async Task WithStatusAsync(string status, Func<Task> action)
    {
        Console.WriteLine(status);
        await action();
    }

    /// <summary>
    /// Remove Spectre.Console markup tags from text.
    /// </summary>
    private static string StripMarkup(string text)
    {
        // Simple regex-like removal of [color] and [/] tags
        var result = text;
        while (true)
        {
            var start = result.IndexOf('[');
            if (start < 0) break;

            var end = result.IndexOf(']', start);
            if (end < 0) break;

            result = result.Remove(start, end - start + 1);
        }
        return result;
    }
}
