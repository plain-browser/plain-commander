using System.Security.Cryptography;

namespace PlainCommander.Core.Operations;

/// <summary>Names an operation writes to before the result is complete: always new, so a user's own file is never touched.</summary>
internal static class TempNames
{
    /// <summary>Creates an empty file next to <paramref name="target"/> ("name.pc-1a2b3c4d.part") that did not exist before.</summary>
    public static string ReserveFile(string target)
    {
        while (true)
        {
            var candidate = Candidate(target);
            if (Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None).Dispose();
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                // taken a moment ago: draw another name
            }
        }
    }

    /// <summary>Creates an empty folder next to <paramref name="target"/> that did not exist before.</summary>
    public static string ReserveFolder(string target)
    {
        while (true)
        {
            var candidate = Candidate(target);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
    }

    private static string Candidate(string target) =>
        Path.TrimEndingDirectorySeparator(target) + ".pc-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".part";
}

/// <summary>Archive entry names are data from the file: joined to a folder only when they stay inside it.</summary>
internal static class ContainedPath
{
    /// <summary>
    /// <paramref name="root"/> joined with the key ("a/b", either slash), or null when the key would leave it: a ".."
    /// segment, a drive or other rooted form, or (on Windows) a name of dots and spaces only, which Windows reads as "..".
    /// Empty and "." segments are dropped. A key with nothing left is <paramref name="root"/> itself when allowed.
    /// A drive form is refused in any segment, not only the first: Path.Combine starts over at the last rooted part,
    /// so "a/C:x" would otherwise come out as "C:x", outside the root.
    /// </summary>
    public static string? Join(string root, string key, bool allowRoot = false)
    {
        var segments = new List<string>();
        foreach (var segment in key.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == ".." || (OperatingSystem.IsWindows() && (segment.TrimEnd('.', ' ').Length == 0 || Path.IsPathRooted(segment))))
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? (allowRoot ? root : null) : Path.Combine([root, .. segments]);
    }
}

/// <summary>An archive entry whose name would put it outside the folder it is extracted into; it is not written.</summary>
public sealed class ArchiveEntryOutsideException(string entry) : IOException($"Entry \"{entry}\" points outside the target folder.")
{
    public string Entry { get; } = entry;
}
