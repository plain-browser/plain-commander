using System.Globalization;

namespace PlainCommander.Core.Navigation;

/// <summary>Which date of a file the filter looks at.</summary>
public enum FilterDateField
{
    Modified,
    Created,

    /// <summary>When a photo was taken (from the file's properties).</summary>
    Taken,
}

/// <summary>Ready-made date ranges; they move with the clock (a filter "last 7 days" stays right tomorrow).</summary>
public enum FilterDatePreset
{
    Any,
    Today,
    Last7Days,
    Last30Days,
    ThisYear,
    Range,
}

/// <summary>
/// Ctrl+S: what a panel shows. A name part (several words: all of them, any order, Polish letters optional) or masks
/// ("*.pdf;*.docx"), extensions, a date range on the modification or creation date, a size range, files only.
/// Folders stay listed (so one can still walk into them) unless <see cref="FilesOnly"/>; the date, size and
/// extension tests apply to files.
/// </summary>
public sealed record PanelFilter
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Without dots, any case ("pdf", "docx").</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    public FilterDateField DateField { get; init; } = FilterDateField.Modified;

    public FilterDatePreset DatePreset { get; init; } = FilterDatePreset.Any;

    /// <summary>First day of the range (local date), for <see cref="FilterDatePreset.Range"/>.</summary>
    public DateOnly? From { get; init; }

    /// <summary>Last day of the range, inclusive.</summary>
    public DateOnly? To { get; init; }

    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public bool FilesOnly { get; init; }

    /// <summary>Recordings at least / at most this long (audio and video).</summary>
    public TimeSpan? MinDuration { get; init; }

    public TimeSpan? MaxDuration { get; init; }

    public bool IsEmpty =>
        Name.Trim().Length == 0 && Extensions.Count == 0 && DatePreset == FilterDatePreset.Any && MinSize is null && MaxSize is null && !FilesOnly
        && MinDuration is null && MaxDuration is null;

    /// <summary>The test needs the files' properties (photo date, duration), which are read from each file first.</summary>
    public bool NeedsDetails =>
        (DateField == FilterDateField.Taken && DatePreset != FilterDatePreset.Any) || MinDuration is not null || MaxDuration is not null;

    /// <summary>"90" or "1:30" minutes, "1:02:00" hours; empty is null. Throws FormatException for anything else.</summary>
    public static TimeSpan? ParseMinutes(string text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var parts = t.Split(':');
        if (parts.Length > 3 || parts.Any(p => !double.TryParse(p.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0))
        {
            throw new FormatException(text);
        }

        var numbers = parts.Select(p => double.Parse(p.Replace(',', '.'), CultureInfo.InvariantCulture)).ToArray();
        var seconds = numbers.Length switch
        {
            1 => numbers[0] * 60,
            2 => (numbers[0] * 60) + numbers[1],
            _ => (numbers[0] * 3600) + (numbers[1] * 60) + numbers[2],
        };

        // NaN, Infinity and "1e300" would throw ArgumentException or OverflowException; the dialog expects FormatException.
        if (!double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            throw new FormatException(text);
        }

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The plain Ctrl+S text of earlier versions: a name part or masks.</summary>
    public static PanelFilter FromText(string text) => new() { Name = text?.Trim() ?? string.Empty };

    /// <summary>"pdf, .docx;txt" → ["pdf", "docx", "txt"].</summary>
    public static IReadOnlyList<string> ParseExtensions(string text) =>
        (text ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('*').TrimStart('.'))
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>"10k", "2,5M", "1G", "300" (bytes); empty is null. Throws FormatException for anything else.</summary>
    public static long? ParseSize(string text)
    {
        var t = (text ?? string.Empty).Trim();
        if (t.Length == 0)
        {
            return null;
        }

        var factor = 1L;
        switch (char.ToUpperInvariant(t[^1]))
        {
            case 'K': factor = 1L << 10; t = t[..^1]; break;
            case 'M': factor = 1L << 20; t = t[..^1]; break;
            case 'G': factor = 1L << 30; t = t[..^1]; break;
            case 'B': t = t[..^1]; break;
        }

        // "1,5" and "1.5" both mean one and a half, whatever the language of the system.
        if (!double.TryParse(t.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException(text);
        }

        // NaN would become 0 bytes; a huge finite size saturates at long.MaxValue.
        if (value < 0 || !double.IsFinite(value))
        {
            throw new FormatException(text);
        }

        return (long)Math.Round(value * factor);
    }

    /// <summary>The local days the date test accepts (both inclusive); null bounds are open. Null when there is no date test.</summary>
    public (DateOnly? First, DateOnly? Last)? DaysAt(DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        return DatePreset switch
        {
            FilterDatePreset.Today => (today, today),
            FilterDatePreset.Last7Days => (today.AddDays(-6), today),
            FilterDatePreset.Last30Days => (today.AddDays(-29), today),
            FilterDatePreset.ThisYear => (new DateOnly(today.Year, 1, 1), today),
            FilterDatePreset.Range when From is not null || To is not null => (From, To),
            _ => null,
        };
    }

    /// <summary>The test for rows whose properties are not needed (or not known).</summary>
    public Func<Model.FileEntry, bool> Matcher(DateTime nowLocal)
    {
        var matches = DetailMatcher(nowLocal);
        return entry => matches(entry, null);
    }

    /// <summary>
    /// The test for list rows, fixed at the moment it is made (the date presets read the clock once). Names are
    /// folded once per row, not once per word. Properties (photo date, duration) come with the row; a file without
    /// them does not pass a test on them.
    /// </summary>
    public Func<Model.FileEntry, Details.FileDetails?, bool> DetailMatcher(DateTime nowLocal)
    {
        var name = NameMatcher(Name.Trim());
        var extensions = Extensions.Count == 0 ? null : new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);
        var days = DaysAt(nowLocal);
        var firstUtc = days?.First is { } first ? first.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime() : (DateTime?)null;
        // The last day of 9999 has no next midnight: the range is open at the end.
        var endUtc = days?.Last is { } last && last != DateOnly.MaxValue ? last.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime() : (DateTime?)null;
        var hasDays = days is not null;
        var field = DateField;
        var minSize = MinSize;
        var maxSize = MaxSize;
        var filesOnly = FilesOnly;
        var minDuration = MinDuration;
        var maxDuration = MaxDuration;

        return (entry, details) =>
        {
            if (entry.IsParentLink)
            {
                return true;
            }

            if (entry.IsDirectory)
            {
                return !filesOnly;
            }

            var shortName = Path.GetFileName(entry.Name); // flat view and search results carry relative names
            if (name is not null && !name(shortName))
            {
                return false;
            }

            if (extensions is not null && !extensions.Contains(Path.GetExtension(shortName).TrimStart('.')))
            {
                return false;
            }

            if ((minSize is { } min && entry.Size < min) || (maxSize is { } max && entry.Size > max))
            {
                return false;
            }

            if (hasDays)
            {
                // A server or an archive knows no creation date: such files do not pass a creation-date test.
                DateTimeOffset? date = field switch
                {
                    FilterDateField.Created => entry.Created,
                    FilterDateField.Taken => details?.Taken,
                    _ => entry.Modified,
                };
                if (date is not { } d || d == DateTimeOffset.MinValue)
                {
                    return false;
                }

                var utc = d.UtcDateTime;
                if ((firstUtc is { } from && utc < from) || (endUtc is { } end && utc >= end))
                {
                    return false;
                }
            }

            if (minDuration is not null || maxDuration is not null)
            {
                if (details?.Duration is not { } length || (minDuration is { } shortest && length < shortest) || (maxDuration is { } longest && length > longest))
                {
                    return false;
                }
            }

            return true;
        };
    }

    /// <summary>Masks when the text has * or ?, otherwise words that must all be in the name.</summary>
    private static Func<string, bool>? NameMatcher(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        if (text.IndexOfAny(['*', '?']) >= 0)
        {
            return FileMask.Parse(text);
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(TextFold.Fold).ToArray();
        return n => TextFold.ContainsAll(TextFold.Fold(n), words);
    }
}
