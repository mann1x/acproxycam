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
                .Title(title)
                .AddChoices(choices));
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
