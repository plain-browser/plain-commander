using System.Globalization;
using PlainCommander.Core.Model;

namespace PlainCommander.Core.Formatting;

/// <summary>Text for the panel columns. Pure functions so the UI stays dumb and the rules stay testable.</summary>
public static class EntryFormatter
{
    public const string DirectoryMarker = "<DIR>";

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(FileEntry entry, CultureInfo culture) =>
        entry.IsDirectory ? DirectoryMarker : entry.Size.ToString("N0", culture);

    /// <summary>The date without the time, for narrow panels; the creation date when <paramref name="created"/>.</summary>
    public static string DateOnly(FileEntry entry, CultureInfo culture, bool created = false) =>
        Shown(entry, created) is { } date ? date.ToLocalTime().ToString(culture.DateTimeFormat.ShortDatePattern, culture) : string.Empty;

    /// <summary>Date and time of the last change, or of the creation when <paramref name="created"/>.</summary>
    public static string Date(FileEntry entry, CultureInfo culture, bool created = false)
    {
        var f = culture.DateTimeFormat;
        return Shown(entry, created) is { } date ? date.ToLocalTime().ToString(f.ShortDatePattern + " " + f.ShortTimePattern, culture) : string.Empty;
    }

    /// <summary>No date for "..", for one that is not known (an S3 folder, a server root) or a creation date off disk.</summary>
    private static DateTimeOffset? Shown(FileEntry entry, bool created)
    {
        var date = created ? entry.Created : entry.Modified;
        return entry.IsParentLink || date is not { } d || d == DateTimeOffset.MinValue ? null : d;
    }

    /// <summary>"rahs" (read-only, archive, hidden, system; '-' when unset) on disk, "rwxr-xr-x" on a server.</summary>
    public static string Attributes(FileEntry entry)
    {
        if (entry.IsParentLink)
        {
            return string.Empty;
        }

        if (entry.Mode is { } mode)
        {
            return Remote.UnixMode.Format(mode); // on a server: "rwxr-xr-x"
        }

        var a = entry.Attributes;
        return new string(
        [
            a.HasFlag(FileAttributes.ReadOnly) ? 'r' : '-',
            a.HasFlag(FileAttributes.Archive) ? 'a' : '-',
            a.HasFlag(FileAttributes.Hidden) ? 'h' : '-',
            a.HasFlag(FileAttributes.System) ? 's' : '-',
        ]);
    }

    /// <summary>1024-based size with one decimal below 100 units, e.g. "48,2 GB" or "512 B".</summary>
    public static string HumanSize(long bytes, CultureInfo culture)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(culture) + " B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value < 100 ? "0.0" : "0";
        return value.ToString(format, culture) + " " + Units[unit];
    }
}
