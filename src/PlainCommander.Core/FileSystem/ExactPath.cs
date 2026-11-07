namespace PlainCommander.Core.FileSystem;

/// <summary>
/// Windows strips trailing dots and spaces from a name ("folder." becomes "folder") unless the path carries
/// the \\?\ prefix, and Path.GetFullPath does the same. These helpers keep such names intact: Full for the
/// path the app shows and stores, Native for the path handed to the file system, Display to take the prefix off.
/// </summary>
public static class ExactPath
{
    private const string Prefix = @"\\?\";
    private const string UncPrefix = @"\\?\UNC\";

    /// <summary>Absolute, normalized path with the last name kept exactly as typed (trailing dots and spaces included).</summary>
    public static string Full(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(path);
        }

        return UpperDrive(FullWindows(path));
    }

    /// <summary>"c:\x" typed in Ctrl+L or the command line shows as "C:\x", like everywhere else in Windows.</summary>
    public static string UpperDrive(string path) =>
        path.Length >= 2 && path[1] == ':' && char.IsLower(path[0]) ? char.ToUpperInvariant(path[0]) + path[1..] : path;

    private static string FullWindows(string path)
    {
        if (path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Display(path);
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (!NeedsPrefix(name))
        {
            return Path.GetFullPath(path);
        }

        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrEmpty(parent) ? Path.GetFullPath(path) : Path.Combine(Path.GetFullPath(parent), name);
    }

    /// <summary>The form to give File/Directory APIs: \\?\ when any name would otherwise lose a trailing dot or space.</summary>
    public static string Native(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() || path.StartsWith(Prefix, StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return path;
        }

        if (!path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(NeedsPrefix))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? UncPrefix + path[2..] : Prefix + path;
    }

    /// <summary>
    /// Always-prefixed form. Move/rename needs both sides in the same form: .NET refuses to move between
    /// "\\?\C:\a" and "C:\b" ("paths must have identical roots"), so when one side is prefixed, prefix both.
    /// </summary>
    public static string Prefixed(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() || path.StartsWith(Prefix, StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? UncPrefix + path[2..] : Prefix + path;
    }

    public static bool IsPrefixed(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Takes the \\?\ (or \\?\UNC\) prefix off a path that came back from the file system.</summary>
    public static string Display(string path)
    {
        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + path[UncPrefix.Length..];
        }

        return path.StartsWith(Prefix, StringComparison.Ordinal) ? path[Prefix.Length..] : path;
    }

    /// <summary>Strips the prefixes wherever they appear inside free text, e.g. an exception message quoting a path.</summary>
    public static string DisplayText(string text) =>
        string.IsNullOrEmpty(text) ? text : text.Replace(UncPrefix, @"\\", StringComparison.Ordinal).Replace(Prefix, string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// A name Windows reinterprets without \\?\: a trailing dot or space is dropped, and a device name ("NUL",
    /// "COM1", on older Windows also "nul.txt") turns the whole path into the device (\\.\NUL).
    /// </summary>
    public static bool NeedsPrefix(string name) => NeedsExactName(name) || IsDeviceName(name);

    private static bool NeedsExactName(string name) =>
        name.Length > 0 && name is not ("." or "..") && (name[^1] == '.' || name[^1] == ' ');

    private static bool IsDeviceName(string name)
    {
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        if (stem.Length == 3)
        {
            return stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL";
        }

        return stem.Length == 4
            && stem[..3].ToUpperInvariant() is "COM" or "LPT"
            && (stem[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3');
    }
}
