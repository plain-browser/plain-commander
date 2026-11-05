using CommunityToolkit.Mvvm.ComponentModel;

namespace PlainCommander.Presentation;

/// <summary>What Enter in the command line should do.</summary>
public enum CommandKind
{
    None,
    /// <summary>Change the active panel's folder ("cd x", "..", "d:").</summary>
    Navigate,
    /// <summary>Run through the shell in the active panel's folder.</summary>
    Run,
}

public sealed record CommandAction(CommandKind Kind, string Argument);

/// <summary>
/// The command line under the panels: text, Up/Down history and the parsing of what was typed.
/// Running and navigating are done by <see cref="MainViewModel"/>, which knows the active panel.
/// </summary>
public sealed partial class CommandLineViewModel : ObservableObject
{
    public const int HistoryLimit = 50;

    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string _draft = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public IReadOnlyList<string> History => _history;

    /// <summary>Newest last, as saved in settings.</summary>
    public void LoadHistory(IEnumerable<string> entries)
    {
        _history.Clear();
        _history.AddRange(entries.Where(static e => !string.IsNullOrWhiteSpace(e)).TakeLast(HistoryLimit));
        _historyIndex = -1;
    }

    /// <summary>Appends text at the caret end, separated by a space; quotes it when it has spaces.</summary>
    public void Insert(string value)
    {
        var quoted = value.Contains(' ') ? $"\"{value}\"" : value;
        Text = Text.Length == 0 || Text.EndsWith(' ') ? Text + quoted : Text + " " + quoted;
        _historyIndex = -1;
    }

    /// <summary>Up (−1) / Down (+1) through the history; Down past the newest restores what was being typed.</summary>
    public void MoveHistory(int step)
    {
        if (_history.Count == 0)
        {
            return;
        }

        if (_historyIndex < 0)
        {
            if (step > 0)
            {
                return;
            }

            _draft = Text;
            _historyIndex = _history.Count;
        }

        var next = _historyIndex + step;
        if (next >= _history.Count)
        {
            _historyIndex = -1;
            Text = _draft;
            return;
        }

        _historyIndex = Math.Max(next, 0);
        Text = _history[_historyIndex];
    }

    /// <summary>Takes the text, remembers it and says what to do with it. Empty text does nothing.</summary>
    public CommandAction Submit()
    {
        var text = Text.Trim();
        Text = string.Empty;
        _historyIndex = -1;
        _draft = string.Empty;
        if (text.Length == 0)
        {
            return new CommandAction(CommandKind.None, string.Empty);
        }

        _history.Remove(text);
        _history.Add(text);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveAt(0);
        }

        return Parse(text);
    }

    /// <summary>"cd x", "cd", "..", "d:" change folders; everything else runs.</summary>
    public static CommandAction Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return new CommandAction(CommandKind.None, string.Empty);
        }

        if (text.Equals("cd", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandAction(CommandKind.Navigate, string.Empty); // home, decided by the caller
        }

        if (text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("cd\\", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("cd/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("cd.", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandAction(CommandKind.Navigate, text[2..].Trim().Trim('"'));
        }

        if (text == "..")
        {
            return new CommandAction(CommandKind.Navigate, "..");
        }

        if (text.Length == 2 && text[1] == ':' && char.IsLetter(text[0]))
        {
            return new CommandAction(CommandKind.Navigate, text.ToUpperInvariant() + "\\");
        }

        return new CommandAction(CommandKind.Run, text);
    }
}
