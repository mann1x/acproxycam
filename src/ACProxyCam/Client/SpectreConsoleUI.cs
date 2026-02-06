// SpectreConsoleUI.cs - Spectre.Console implementation of IConsoleUI

using Spectre.Console;

namespace ACProxyCam.Client;

/// <summary>
/// Spectre.Console implementation for rich interactive terminal UI.
/// </summary>
public class SpectreConsoleUI : IConsoleUI
{
    public void WriteLine(string text = "")
    {
        AnsiConsole.WriteLine(text);
    }

    public void WriteMarkup(string markup)
    {
        AnsiConsole.MarkupLine(markup);
    }

    public void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]");
    }

    public void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(message)}[/]");
    }

    public void WriteSuccess(string message)
    {
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]");
    }

    public void WriteInfo(string message)
    {
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
    }

    public void WriteHeader(string version)
    {
        AnsiConsole.Write(new FigletText("ACProxyCam").Color(Color.Purple));
        AnsiConsole.MarkupLine($"[grey]Version {version}[/]");
        AnsiConsole.WriteLine();
    }

    public bool Confirm(string prompt, bool defaultValue = true)
    {
        return AnsiConsole.Confirm(Markup.Escape(prompt), defaultValue);
    }

    public string Ask(string prompt, string? defaultValue = null)
    {
        var escapedPrompt = Markup.Escape(prompt);
        if (defaultValue != null)
        {
            return AnsiConsole.Ask(escapedPrompt, defaultValue);
        }
        return AnsiConsole.Ask<string>(escapedPrompt);
    }

    public string? AskOptional(string prompt, string? currentValue = null)
    {
        var escapedPrompt = Markup.Escape(prompt);
        var textPrompt = new TextPrompt<string>(escapedPrompt)
            .AllowEmpty();

        if (!string.IsNullOrEmpty(currentValue))
        {
            textPrompt.DefaultValue(currentValue);
        }

        // Note: Spectre.Console doesn't natively support Esc to cancel in TextPrompt
        // The user can press Enter with empty input to keep current value
        return AnsiConsole.Prompt(textPrompt);
    }

    public int AskInt(string prompt, int defaultValue)
    {
        var escapedPrompt = Markup.Escape(prompt);
        return AnsiConsole.Ask(escapedPrompt, defaultValue);
    }

    public string AskSecret(string prompt, string? defaultValue = null)
    {
        var textPrompt = new TextPrompt<string>(Markup.Escape(prompt)).Secret(null);
        if (defaultValue != null)
        {
            textPrompt.DefaultValue(defaultValue);
        }
        return AnsiConsole.Prompt(textPrompt);
    }

    public string SelectOne(string title, IEnumerable<string> choices)
    {
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(Markup.Escape(title))
                .AddChoices(choices));
    }

    public string? SelectOneWithEscape(string title, IEnumerable<string> choices)
    {
        var (item, _) = SelectOneWithEscapeAndIndex(title, choices, 0);
        return item;
    }

    public (string? Item, int Index) SelectOneWithEscapeAndIndex(string title, IEnumerable<string> choices, int startIndex = 0)
    {
        var choiceList = choices.ToList();
        if (choiceList.Count == 0)
            return (null, -1);

        // Clamp startIndex to valid range
        int selectedIndex = Math.Max(0, Math.Min(startIndex, choiceList.Count - 1));

        // Write title
        AnsiConsole.MarkupLine(Markup.Escape(title));

        // Custom selection loop with Escape support
        while (true)
        {
            // Move cursor up to redraw options
            if (selectedIndex >= 0)
            {
                // Clear previous options
                var cursorTop = Console.CursorTop;
                for (int i = 0; i < choiceList.Count; i++)
                {
                    Console.SetCursorPosition(0, cursorTop + i);
                    Console.Write(new string(' ', Console.WindowWidth - 1));
                }
                Console.SetCursorPosition(0, cursorTop);
            }

            // Draw options (don't escape - choices may contain intentional markup)
            for (int i = 0; i < choiceList.Count; i++)
            {
                if (i == selectedIndex)
                    AnsiConsole.MarkupLine($"[cyan]> {choiceList[i]}[/]");
                else
                    AnsiConsole.MarkupLine($"  {choiceList[i]}");
            }

            // Position hint
            AnsiConsole.MarkupLine("[grey](↑/↓ to move, Enter to select, Esc to cancel)[/]");

            // Read key
            var keyInfo = Console.ReadKey(true);

            // Clear the hint line
            Console.SetCursorPosition(0, Console.CursorTop - 1);
            Console.Write(new string(' ', Console.WindowWidth - 1));
            Console.SetCursorPosition(0, Console.CursorTop);

            // Move cursor back up to option area for redraw
            for (int i = 0; i < choiceList.Count; i++)
            {
                Console.SetCursorPosition(0, Console.CursorTop - 1);
            }

            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    selectedIndex = selectedIndex > 0 ? selectedIndex - 1 : choiceList.Count - 1;
                    break;

                case ConsoleKey.DownArrow:
                    selectedIndex = selectedIndex < choiceList.Count - 1 ? selectedIndex + 1 : 0;
                    break;

                case ConsoleKey.Enter:
                    // Clear and show final selection
                    Console.SetCursorPosition(0, Console.CursorTop);
                    for (int i = 0; i < choiceList.Count + 1; i++)
                    {
                        Console.Write(new string(' ', Console.WindowWidth - 1));
                        if (i < choiceList.Count)
                            Console.SetCursorPosition(0, Console.CursorTop + 1);
                    }
                    Console.SetCursorPosition(0, Console.CursorTop - choiceList.Count);
                    AnsiConsole.MarkupLine($"[green]{Markup.Escape(choiceList[selectedIndex])}[/]");
                    return (choiceList[selectedIndex], selectedIndex);

                case ConsoleKey.Escape:
                    // Clear and show cancelled
                    Console.SetCursorPosition(0, Console.CursorTop);
                    for (int i = 0; i < choiceList.Count + 1; i++)
                    {
                        Console.Write(new string(' ', Console.WindowWidth - 1));
                        if (i < choiceList.Count)
                            Console.SetCursorPosition(0, Console.CursorTop + 1);
                    }
                    Console.SetCursorPosition(0, Console.CursorTop - choiceList.Count);
                    AnsiConsole.MarkupLine("[grey]Cancelled[/]");
                    return (null, -1);
            }
        }
    }

    public List<string> SelectMany(string title, IEnumerable<string> choices, string? instructions = null)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title(title)
            .AddChoices(choices);

        if (instructions != null)
        {
            prompt.InstructionsText(instructions);
        }

        return AnsiConsole.Prompt(prompt).ToList();
    }

    public void Clear()
    {
        Console.Clear();
    }

    public void WaitForKey(string? message = null)
    {
        if (message != null)
        {
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(message)}[/]");
        }
        Console.ReadKey(true);
    }

    public ConsoleKeyInfo ReadKey()
    {
        return Console.ReadKey(true);
    }

    public void WritePanel(string content)
    {
        var panel = new Panel(content)
        {
            Border = BoxBorder.Rounded
        };
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public void WriteRule(string? title = null)
    {
        if (title != null)
        {
            AnsiConsole.Write(new Rule($"[blue]{Markup.Escape(title)}[/]"));
        }
        else
        {
            AnsiConsole.Write(new Rule());
        }
    }

    public void WriteTable(string[] headers, IEnumerable<string[]> rows)
    {
        var table = new Table().Border(TableBorder.Rounded);

        foreach (var header in headers)
        {
            table.AddColumn(header);
        }

        foreach (var row in rows)
        {
            table.AddRow(row);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public void WriteGrid(IEnumerable<(string Label, string Value)> items)
    {
        var grid = new Grid().AddColumn().AddColumn();

        foreach (var (label, value) in items)
        {
            grid.AddRow($"[grey]{label}:[/]", value);
        }

        AnsiConsole.Write(grid);
    }

    public async Task WithStatusAsync(string status, Func<Task> action)
    {
        await AnsiConsole.Status()
            .StartAsync(status, async ctx =>
            {
                await action();
            });
    }
}
