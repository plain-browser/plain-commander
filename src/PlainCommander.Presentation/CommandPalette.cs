using PlainCommander.Core.Navigation;

namespace PlainCommander.Presentation;

/// <summary>One line of the command palette: a menu command with the menu it lives in and its shortcut.</summary>
public sealed record PaletteCommand(string Title, string Group, string? Shortcut);

/// <summary>
/// Ctrl+Shift+P: which commands match what was typed, best first. Every typed word has to occur in the title or
/// the menu name; letters match without their accents ("zmien" finds "Zmień"), like the quick search of a panel.
/// </summary>
public static class CommandPalette
{
    /// <returns>Indexes into <paramref name="commands"/>; all of them, in menu order, for an empty query.</returns>
    public static IReadOnlyList<int> Filter(IReadOnlyList<PaletteCommand> commands, string? query)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var words = TextFold.Fold(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return Enumerable.Range(0, commands.Count).ToArray();
        }

        var whole = string.Join(' ', words);
        var matches = new List<(int Index, int Rank)>();
        for (var i = 0; i < commands.Count; i++)
        {
            var title = TextFold.Fold(commands[i].Title);
            var everything = title + " " + TextFold.Fold(commands[i].Group);
            if (!words.All(w => everything.Contains(w, StringComparison.Ordinal)))
            {
                continue;
            }

            var titleWords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var rank = title.StartsWith(whole, StringComparison.Ordinal) ? 0
                : words.All(w => titleWords.Any(t => t.StartsWith(w, StringComparison.Ordinal))) ? 1
                : words.All(w => title.Contains(w, StringComparison.Ordinal)) ? 2
                : 3;
            matches.Add((i, rank));
        }

        return matches.OrderBy(static m => m.Rank).ThenBy(static m => m.Index).Select(static m => m.Index).ToArray();
    }
}
