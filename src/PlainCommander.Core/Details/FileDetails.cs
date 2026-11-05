using System.Globalization;

namespace PlainCommander.Core.Details;

/// <summary>What the extra panel column can show.</summary>
public enum DetailColumn
{
    None,
    Dimensions,
    Taken,
    Duration,
    Version,
}

/// <summary>
/// Properties of a file beyond name, size and dates, as Windows knows them (the same values Explorer shows):
/// image size, when a photo was taken, how long a recording lasts, the version of a program.
/// </summary>
public sealed record FileDetails(int? Width = null, int? Height = null, DateTimeOffset? Taken = null, TimeSpan? Duration = null, string? Version = null)
{
    /// <summary>Asked and nothing known (a folder, a text file, a file on a server).</summary>
    public static FileDetails None { get; } = new();

    public long? Pixels => Width is { } w && Height is { } h ? (long)w * h : null;
}

/// <summary>Reads <see cref="FileDetails"/> of a local file. Called on worker threads; never throws for an unreadable file.</summary>
public interface IFileDetailsProvider
{
    FileDetails Read(string path);
}

/// <summary>Column text, header and ordering for <see cref="DetailColumn"/>.</summary>
public static class DetailFormatter
{
    public static string Text(FileDetails? details, DetailColumn column, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (details is null)
        {
            return string.Empty;
        }

        return column switch
        {
            DetailColumn.Dimensions when details.Width is { } w && details.Height is { } h => string.Create(culture, $"{w} × {h}"),
            DetailColumn.Taken when details.Taken is { } t => t.ToLocalTime().ToString(culture.DateTimeFormat.ShortDatePattern + " " + culture.DateTimeFormat.ShortTimePattern, culture),
            DetailColumn.Duration when details.Duration is { } d => Duration(d),
            DetailColumn.Version when details.Version is { Length: > 0 } v => v,
            _ => string.Empty,
        };
    }

    /// <summary>"3:07", "1:02:45": minutes and seconds, hours only when there are some.</summary>
    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{d.Minutes}:{d.Seconds:00}");

    /// <summary>Whether the file has a value for the column.</summary>
    public static bool HasValue(FileDetails? details, DetailColumn column) => details is not null && column switch
    {
        DetailColumn.Dimensions => details.Pixels is not null,
        DetailColumn.Taken => details.Taken is not null,
        DetailColumn.Duration => details.Duration is not null,
        DetailColumn.Version => details.Version is { Length: > 0 },
        _ => false,
    };

    /// <summary>Orders two files by the column's value; files without one go last.</summary>
    public static int Compare(FileDetails? x, FileDetails? y, DetailColumn column)
    {
        IComparable? Key(FileDetails? d) => d is null ? null : column switch
        {
            DetailColumn.Dimensions => d.Pixels,
            DetailColumn.Taken => d.Taken,
            DetailColumn.Duration => d.Duration,
            DetailColumn.Version => d.Version is { Length: > 0 } v ? VersionKey(v) : null,
            _ => null,
        };

        var a = Key(x);
        var b = Key(y);
        return (a, b) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => a.CompareTo(b),
        };
    }

    /// <summary>"10.0.2" after "9.1": numeric parts compared as numbers.</summary>
    private static Version VersionKey(string text) =>
        System.Version.TryParse(text.Split(' ')[0], out var version) ? version : new System.Version(0, 0);
}
