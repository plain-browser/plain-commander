using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PlainCommander.Core.Rename;

public enum CaseChange
{
    Unchanged,
    Lower,
    Upper,

    /// <summary>First letter upper, the rest lower: "raport roczny.pdf" → "Raport roczny.pdf".</summary>
    FirstUpper,

    /// <summary>Every word starts upper: "raport roczny.pdf" → "Raport Roczny.Pdf".</summary>
    WordsUpper,
}

/// <summary>Ctrl+M settings. Masks use the usual rename placeholders: [N] [N2-5] [E] [C] [P] [YMD] [hms] …</summary>
public sealed record RenameRules(
    string NameMask = "[N]",
    string ExtensionMask = "[E]",
    string Search = "",
    string Replace = "",
    bool UseRegex = false,
    bool CaseSensitive = false,
    CaseChange Case = CaseChange.Unchanged,
    long CounterStart = 1,
    long CounterStep = 1,
    int CounterDigits = 1,
    bool Transliterate = false);

/// <summary>One item to rename, in the order the counter numbers them.</summary>
public sealed record RenameSource(string FullPath, string Name, bool IsDirectory, DateTimeOffset Modified);

public enum RenameProblem
{
    None,
    Empty,
    InvalidName,
    DuplicateInBatch,
    ExistsOnDisk,
    BadMask,
    BadRegex,
}

public sealed record RenamePreview(RenameSource Source, string NewName, RenameProblem Problem)
{
    public bool Changes => Problem == RenameProblem.None && !string.Equals(Source.Name, NewName, StringComparison.Ordinal);

    public string NewPath => Path.Combine(Path.GetDirectoryName(Source.FullPath) ?? string.Empty, NewName);
}

/// <summary>What was renamed (old → new full paths), for refreshing panels and for undo, plus what failed.</summary>
public sealed record RenameOutcome(IReadOnlyList<(string From, string To)> Done, IReadOnlyList<(string Path, Exception Error)> Failed);

public static class MultiRename
{
    private static readonly Regex Token = new(@"\[\[|\]\]|\[([^\[\]]*)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly System.Buffers.SearchValues<char> WindowsInvalid = System.Buffers.SearchValues.Create("<>:\"/\\|?*");

    private static StringComparer NameComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Computes every new name and what is wrong with it, without touching the disk except through
    /// <paramref name="exists"/>. A target that belongs to another item of the batch is not a conflict:
    /// that item moves away first.
    /// </summary>
    public static IReadOnlyList<RenamePreview> Preview(IReadOnlyList<RenameSource> sources, RenameRules rules, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(exists);

        Regex? search = null;
        var badRegex = false;
        if (rules.UseRegex && rules.Search.Length > 0)
        {
            try
            {
                search = new Regex(rules.Search, (rules.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                badRegex = true;
            }
        }

        var results = new RenamePreview[sources.Count];
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (badRegex)
            {
                results[i] = new RenamePreview(source, source.Name, RenameProblem.BadRegex);
                continue;
            }

            try
            {
                var newName = Compute(source, rules, rules.CounterStart + (i * rules.CounterStep), search);
                results[i] = new RenamePreview(source, newName, Validate(newName));
            }
            catch (FormatException)
            {
                results[i] = new RenamePreview(source, source.Name, RenameProblem.BadMask);
            }
            catch (RegexMatchTimeoutException)
            {
                results[i] = new RenamePreview(source, source.Name, RenameProblem.BadRegex);
            }
        }

        // Duplicates inside the batch, per folder.
        var seen = new Dictionary<string, int>(NameComparer);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i].Problem != RenameProblem.None)
            {
                continue;
            }

            var key = results[i].NewPath;
            if (seen.TryGetValue(key, out var first))
            {
                results[i] = results[i] with { Problem = RenameProblem.DuplicateInBatch };
                results[first] = results[first] with { Problem = RenameProblem.DuplicateInBatch };
            }
            else
            {
                seen[key] = i;
            }
        }

        // Collisions with files that are not part of the batch.
        var batch = new HashSet<string>(sources.Select(s => s.FullPath), NameComparer);
        for (var i = 0; i < results.Length; i++)
        {
            var r = results[i];
            if (r.Changes && !batch.Contains(r.NewPath) && !SameEntry(r.Source.FullPath, r.NewPath) && exists(r.NewPath))
            {
                results[i] = r with { Problem = RenameProblem.ExistsOnDisk };
            }
        }

        return results;
    }

    /// <summary>
    /// Renames every changing item in two phases (all to temporary names, then to the final ones), so swaps
    /// like a→b, b→a work. An item that fails goes back to its old name; the others carry on.
    /// </summary>
    public static RenameOutcome Apply(IReadOnlyList<RenamePreview> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Move(plan.Where(p => p.Changes).Select(p => (p.Source.FullPath, p.NewPath)).ToArray());
    }

    /// <summary>Reverses an earlier <see cref="Apply"/>.</summary>
    public static RenameOutcome Undo(RenameOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return Move(outcome.Done.Select(d => (d.To, d.From)).ToArray());
    }

    public static string Compute(RenameSource source, RenameRules rules, long counter, Regex? search = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        var (stem, ext) = Split(source);
        var name = Expand(rules.NameMask, source, stem, ext, counter, rules);
        var extension = Expand(rules.ExtensionMask, source, stem, ext, counter, rules);
        var full = extension.Length > 0 ? name + "." + extension : name;

        if (rules.Search.Length > 0)
        {
            search ??= rules.UseRegex
                ? new Regex(rules.Search, (rules.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                : null;
            full = search is not null
                ? search.Replace(full, rules.Replace)
                : full.Replace(rules.Search, rules.Replace, rules.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        if (rules.Transliterate)
        {
            full = Transliteration.ToLatin(full); // before the case change, which then sees Latin letters
        }

        return ApplyCase(full, rules.Case);
    }

    private static (string Stem, string Ext) Split(RenameSource source)
    {
        if (source.IsDirectory)
        {
            return (source.Name, string.Empty);
        }

        var dot = source.Name.LastIndexOf('.');
        return dot > 0 && dot < source.Name.Length - 1 ? (source.Name[..dot], source.Name[(dot + 1)..]) : (source.Name, string.Empty);
    }

    private static string Expand(string mask, RenameSource source, string stem, string ext, long counter, RenameRules rules)
    {
        var culture = CultureInfo.InvariantCulture;
        var local = source.Modified.ToLocalTime();
        return Token.Replace(mask, m =>
        {
            if (m.Value is "[[" or "]]")
            {
                return m.Value[..1]; // doubled brackets are literal brackets
            }

            var token = m.Groups[1].Value;
            return token switch
            {
                "N" => stem,
                "E" => ext,
                "C" => counter.ToString(culture).PadLeft(Math.Clamp(rules.CounterDigits, 1, 12), '0'),
                "P" => Path.GetFileName(Path.GetDirectoryName(source.FullPath)) ?? string.Empty,
                "YMD" => local.ToString("yyyyMMdd", culture),
                "Y" => local.ToString("yyyy", culture),
                "M" => local.ToString("MM", culture),
                "D" => local.ToString("dd", culture),
                "hms" => local.ToString("HHmmss", culture),
                "h" => local.ToString("HH", culture),
                "m" => local.ToString("mm", culture),
                "s" => local.ToString("ss", culture),
                _ when token.Length > 1 && token[0] is 'N' or 'E' => Range(token[0] == 'N' ? stem : ext, token[1..]),
                _ => throw new FormatException(m.Value),
            };
        });
    }

    /// <summary>"2-5" characters 2..5, "3" the third, "2-" from the second to the end (1-based, like WC).</summary>
    private static string Range(string text, string spec)
    {
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        int from, to;
        if (dash < 0)
        {
            from = to = ParsePosition(spec);
        }
        else
        {
            from = dash == 0 ? 1 : ParsePosition(spec[..dash]);
            to = dash == spec.Length - 1 ? text.Length : ParsePosition(spec[(dash + 1)..]);
        }

        if (from > text.Length || to < from)
        {
            return string.Empty;
        }

        to = Math.Min(to, text.Length);
        return text[(from - 1)..to];
    }

    private static int ParsePosition(string s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n : throw new FormatException(s);

    private static string ApplyCase(string text, CaseChange change) => change switch
    {
        CaseChange.Lower => text.ToLower(CultureInfo.CurrentCulture),
        CaseChange.Upper => text.ToUpper(CultureInfo.CurrentCulture),
        CaseChange.FirstUpper => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..].ToLower(CultureInfo.CurrentCulture),
        CaseChange.WordsUpper => WordsUpper(text),
        _ => text,
    };

    private static string WordsUpper(string text)
    {
        var sb = new StringBuilder(text.Length);
        var start = true;
        foreach (var c in text)
        {
            sb.Append(start ? char.ToUpper(c, CultureInfo.CurrentCulture) : char.ToLower(c, CultureInfo.CurrentCulture));
            start = !char.IsLetterOrDigit(c);
        }

        return sb.ToString();
    }

    private static RenameProblem Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return RenameProblem.Empty;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || (OperatingSystem.IsWindows() && (name.AsSpan().IndexOfAny(WindowsInvalid) >= 0 || name.EndsWith('.') || name.EndsWith(' '))))
        {
            return RenameProblem.InvalidName;
        }

        return RenameProblem.None;
    }

    private static bool SameEntry(string a, string b) => NameComparer.Equals(a, b);

    private static RenameOutcome Move(IReadOnlyList<(string From, string To)> moves)
    {
        var done = new List<(string From, string To)>();
        var failed = new List<(string Path, Exception Error)>();
        var staged = new List<(string From, string Temp, string To)>();

        foreach (var (from, to) in moves)
        {
            var temp = Path.Combine(Path.GetDirectoryName(from) ?? string.Empty, ".pc-rename-" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                MoveEntry(from, temp);
                staged.Add((from, temp, to));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((from, ex));
            }
        }

        foreach (var (from, temp, to) in staged)
        {
            try
            {
                MoveEntry(temp, to);
                done.Add((from, to));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((from, ex));
                try
                {
                    MoveEntry(temp, from);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    failed.Add((temp, back)); // left under the temporary name; reported so the user can find it
                }
            }
        }

        return new RenameOutcome(done, failed);
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }
}
