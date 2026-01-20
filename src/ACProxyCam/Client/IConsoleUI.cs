// IConsoleUI.cs - Interface for console UI abstraction

namespace ACProxyCam.Client;

/// <summary>
/// Interface for console UI operations, allowing different implementations
/// (Spectre.Console for interactive terminals, simple console for automation).
/// </summary>
public interface IConsoleUI
{
    /// <summary>
    /// Write a line of text.
    /// </summary>
    void WriteLine(string text = "");

    /// <summary>
    /// Write a line with markup/color. Implementations handle formatting.
    /// </summary>
    void WriteMarkup(string markup);

    /// <summary>
    /// Write an error message.
    /// </summary>
    void WriteError(string message);

    /// <summary>
    /// Write a warning message.
    /// </summary>
    void WriteWarning(string message);

    /// <summary>
    /// Write a success message.
    /// </summary>
    void WriteSuccess(string message);

    /// <summary>
    /// Write a grey/muted message.
    /// </summary>
    void WriteInfo(string message);

    /// <summary>
    /// Display the application header/banner.
    /// </summary>
    void WriteHeader(string version);

    /// <summary>
    /// Ask for confirmation (yes/no).
    /// </summary>
    bool Confirm(string prompt, bool defaultValue = true);

    /// <summary>
    /// Ask for a string input.
    /// </summary>
    string Ask(string prompt, string? defaultValue = null);

    /// <summary>
    /// Ask for an integer input.
    /// </summary>
    int AskInt(string prompt, int defaultValue);

    /// <summary>
    /// Ask for a secret/password input.
    /// </summary>
    string AskSecret(string prompt, string? defaultValue = null);

    /// <summary>
    /// Present a single-choice selection menu.
    /// </summary>
    string SelectOne(string title, IEnumerable<string> choices);

    /// <summary>
    /// Present a multi-choice selection menu.
    /// </summary>
    List<string> SelectMany(string title, IEnumerable<string> choices, string? instructions = null);

    /// <summary>
    /// Clear the console.
    /// </summary>
    void Clear();

    /// <summary>
    /// Wait for any key press.
    /// </summary>
    void WaitForKey(string? message = null);

    /// <summary>
    /// Read a single key press.
    /// </summary>
    ConsoleKeyInfo ReadKey();

    /// <summary>
    /// Display a status panel/header.
    /// </summary>
    void WritePanel(string content);

    /// <summary>
    /// Display a horizontal rule/separator.
    /// </summary>
    void WriteRule(string? title = null);

    /// <summary>
    /// Display a table with headers and rows.
    /// </summary>
    void WriteTable(string[] headers, IEnumerable<string[]> rows);

    /// <summary>
    /// Display key-value pairs in a grid.
    /// </summary>
    void WriteGrid(IEnumerable<(string Label, string Value)> items);

    /// <summary>
    /// Execute an action with a status/spinner display.
    /// </summary>
    Task WithStatusAsync(string status, Func<Task> action);
}
