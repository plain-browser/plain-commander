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
        Shown(entry, created) is { } date ? date.ToLocalTime().ToString(ColumnDatePattern(culture), culture) : string.Empty;

    /// <summary>Date and time of the last change, or of the creation when <paramref name="created"/>.</summary>
    public static string Date(FileEntry entry, CultureInfo culture, bool created = false) =>
        Shown(entry, created) is { } date ? date.ToLocalTime().ToString(ColumnDateTimePattern(culture), culture) : string.Empty;

    /// <summary>The culture's short date with a two-digit day and month ("05.10.2026", not "5.10.2026"), so a column lines up.</summary>
    public static string ColumnDatePattern(CultureInfo culture) => FixedWidth(culture.DateTimeFormat.ShortDatePattern);

    /// <summary><see cref="ColumnDatePattern"/> plus the culture's short time with two-digit hours ("09:05").</summary>
    public static string ColumnDateTimePattern(CultureInfo culture) =>
        ColumnDatePattern(culture) + " " + FixedWidth(culture.DateTimeFormat.ShortTimePattern);

    /// <summary>Doubles lone d, M, H, h, m and s; leaves longer runs ("ddd", "MMMM", "yyyy") and quoted literals alone.</summary>
    private static string FixedWidth(string pattern)
    {
        var sb = new System.Text.StringBuilder(pattern.Length + 4);
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c is '\'' or '"')
            {
                var end = pattern.IndexOf(c, i + 1);
                end = end < 0 ? pattern.Length - 1 : end;
                sb.Append(pattern, i, end - i + 1);
                i = end;
                continue;
            }

            if (c == '\\' && i + 1 < pattern.Length)
            {
                sb.Append(c).Append(pattern[++i]);
                continue;
            }

            var run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c)
            {
                run++;
            }

            sb.Append(c, run == 1 && c is 'd' or 'M' or 'H' or 'h' or 'm' or 's' ? 2 : run);
            i += run - 1;
        }

        return sb.ToString();
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
