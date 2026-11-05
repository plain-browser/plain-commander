using System.IO.Enumeration;

namespace PlainCommander.Core.Navigation;

/// <summary>
/// Type-to-find in a panel. The typed text is found anywhere in a name, ignoring case and Polish letters
/// ("rap" finds "Raport.pdf" and "Kwartalny raport.docx"); a name that starts with it wins when the cursor
/// jumps. Text with * or ? is a mask from the start of the name ("*.md", "d?w").
/// </summary>
public static class QuickSearch
{
    /// <summary>
    /// Index of the item to jump to for <paramref name="text"/>, starting at <paramref name="start"/> (inclusive) and
    /// wrapping around: the first name that starts with the text, else the first that contains it. -1 when none.
    /// </summary>
    public static int Find<T>(IReadOnlyList<T> items, Func<T, string> name, string text, int start, bool forward = true)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        if (items.Count == 0 || string.IsNullOrEmpty(text))
        {
            return -1;
        }

        if (IsMask(text))
        {
            return Next(items, n => FileSystemName.MatchesSimpleExpression(text + "*", name(n), ignoreCase: true), start, forward);
        }

        var folded = TextFold.Fold(text);
        var prefix = Next(items, n => TextFold.Fold(name(n)).StartsWith(folded, StringComparison.Ordinal), start, forward);
        return prefix >= 0 ? prefix : Next(items, n => TextFold.Fold(name(n)).Contains(folded, StringComparison.Ordinal), start, forward);
    }

    /// <summary>Up/Down while searching: the next (previous) item that matches at all, prefix or not.</summary>
    public static int FindNext<T>(IReadOnlyList<T> items, Func<T, string> name, string text, int start, bool forward)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        if (items.Count == 0 || string.IsNullOrEmpty(text))
        {
            return -1;
        }

        var matches = Matcher(text);
        return Next(items, n => matches(name(n)), start, forward);
    }

    /// <summary>How many items match (shown next to the typed text).</summary>
    public static int Count<T>(IEnumerable<T> items, Func<T, string> name, string text)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var matches = Matcher(text);
        return items.Count(i => matches(name(i)));
    }

    /// <summary>The test a name passes: a mask from the start, or the folded text anywhere in it.</summary>
    public static Func<string, bool> Matcher(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsMask(text))
        {
            return n => FileSystemName.MatchesSimpleExpression(text + "*", n, ignoreCase: true);
        }

        var folded = TextFold.Fold(text);
        return n => TextFold.Fold(n).Contains(folded, StringComparison.Ordinal);
    }

    private static bool IsMask(string text) => text.IndexOfAny(['*', '?']) >= 0;

    private static int Next<T>(IReadOnlyList<T> items, Func<T, bool> matches, int start, bool forward)
    {
        var count = items.Count;
        start = ((start % count) + count) % count;
        for (var step = 0; step < count; step++)
        {
            var i = forward ? (start + step) % count : (start - step + count) % count;
            if (matches(items[i]))
            {
                return i;
            }
        }

        return -1;
    }
}
