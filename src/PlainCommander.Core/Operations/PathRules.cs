using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Operations;

internal static class PathRules
{
    /// <summary>The same path text (letter case aside on Windows); "a." and "a" stay different, \\?\ or not.</summary>
    public static bool Same(string a, string b) =>
        TreeScanner.PathComparer.Equals(Comparable(a), Comparable(b));

    /// <summary>
    /// Both paths name one existing file, also through a link, junction, 8.3 name or hard link. Falls back to
    /// <see cref="Same"/> where the file system cannot tell.
    /// </summary>
    public static bool SameFile(string a, string b)
    {
        if (Same(a, b))
        {
            return true;
        }

        var first = NativeFile.Identity(a);
        return first is not null && first == NativeFile.Identity(b);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies anywhere below it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Comparable(path);
        var f = Comparable(folder);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.EndsInDirectorySeparator(f) ? f : f + Path.DirectorySeparatorChar; // a root keeps its separator
        return p.Equals(f, comparison) || p.StartsWith(prefix, comparison);
    }

    public static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Comparable(a)), Path.GetPathRoot(Comparable(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Full path without the \\?\ prefix and without a trailing separator (a root keeps its own).</summary>
    private static string Comparable(string path) => Path.TrimEndingDirectorySeparator(ExactPath.Full(path));

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Deletes an empty folder (or a link). On Windows, Directory.Delete reports "access denied" as a bare
    /// IOException without an error code; it is rethrown as what it is, so "as administrator" can be offered.
    /// </summary>
    public static void RemoveDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && ex.GetType() == typeof(IOException) && ex.HResult == unchecked((int)0x80131620))
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    /// <summary>"report.pdf" → "report (2).pdf", the first free name in the same folder.</summary>
    public static string FreeName(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Like <see cref="FreeName"/>, but claims the name by creating an empty file, so two parallel
    /// copies can never pick the same "(2)". The copy then overwrites the placeholder.
    /// </summary>
    public static string ReserveFreeName(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (Exists(candidate))
            {
                continue;
            }

            try
            {
                new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // someone else took it a moment ago: try the next number
            }
        }
    }

    /// <summary>Windows refuses to replace a hidden or system file as much as a read-only one.</summary>
    public static void ClearForOverwrite(string path)
    {
        const FileAttributes blocking = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
        var attributes = File.GetAttributes(path);
        if ((attributes & blocking) != 0)
        {
            File.SetAttributes(path, attributes & ~blocking);
        }
    }

    public static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
