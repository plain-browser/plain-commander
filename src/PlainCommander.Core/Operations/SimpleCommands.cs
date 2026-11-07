using PlainCommander.Core.FileSystem;
namespace PlainCommander.Core.Operations;

/// <summary>Quick, synchronous commands: F7 new folder, Shift+F4 new file, Shift+F6 rename.</summary>
public static class SimpleCommands
{
    /// <summary>Creates a folder (nested "a\b\c" allowed) and returns its full path.</summary>
    public static string CreateDirectory(string baseDirectory, string name)
    {
        var clean = CleanName(name, allowSeparators: true);
        RequireFolder(baseDirectory);
        var full = ExactPath.Native(ExactPath.Full(Path.Combine(baseDirectory, clean)));
        if (File.Exists(full))
        {
            throw new OperationRuleException(RuleViolation.AlreadyExists, clean);
        }

        Directory.CreateDirectory(full);
        return ExactPath.Display(full);
    }

    /// <summary>Shift+F4: creates an empty file and returns its full path; an existing file is left alone and reported.</summary>
    public static string CreateFile(string baseDirectory, string name)
    {
        var clean = CleanName(name, allowSeparators: false);
        RequireFolder(baseDirectory);
        var full = ExactPath.Native(ExactPath.Full(Path.Combine(baseDirectory, clean)));
        if (Directory.Exists(full))
        {
            throw new OperationRuleException(RuleViolation.AlreadyExists, clean);
        }

        if (File.Exists(full))
        {
            throw new OperationRuleException(RuleViolation.AlreadyExists, clean);
        }

        using (File.Open(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        return ExactPath.Display(full);
    }

    /// <summary>Renames a file or folder in place and returns the new full path. Case-only renames work.</summary>
    public static string Rename(string path, string newName)
    {
        var clean = CleanName(newName, allowSeparators: false);
        var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(path)));
        var destination = ExactPath.Native(Path.Combine(ExactPath.Display(Path.GetDirectoryName(full) ?? string.Empty), clean));
        MoveEntry(full, destination);
        return ExactPath.Display(destination);
    }

    /// <summary>File.Move/Directory.Move that also handles a change of letter case only.</summary>
    internal static void MoveEntry(string source, string destination)
    {
        if (ExactPath.IsPrefixed(source) || ExactPath.IsPrefixed(destination))
        {
            source = ExactPath.Prefixed(source);
            destination = ExactPath.Prefixed(destination);
        }

        var caseOnly = !string.Equals(source, destination, StringComparison.Ordinal) && PathRules.Same(source, destination);
        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return;
        }

        if (caseOnly)
        {
            // Case-insensitive file systems see source and destination as the same entry: hop via a temporary name.
            var hop = source + ".pc-rename-" + Guid.NewGuid().ToString("N")[..8];
            MoveRaw(source, hop);
            try
            {
                MoveRaw(hop, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In a case-sensitive folder the other spelling can be a second file: put this one back.
                MoveRaw(hop, source);
                throw;
            }

            return;
        }

        if (PathRules.Exists(destination))
        {
            throw new OperationRuleException(RuleViolation.AlreadyExists, Path.GetFileName(ExactPath.Display(destination)));
        }

        MoveRaw(source, destination);
    }

    private static void MoveRaw(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    /// <summary>The longest name NTFS (and most Linux file systems) allow for one file or folder, in UTF-16 units.</summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// The typed name trimmed (and unquoted), each level without trailing dots or spaces (Windows drops them, so a
    /// name keeping them is one Explorer cannot open). Throws <see cref="InvalidNameException"/> when it is empty,
    /// too long or has characters a name cannot have.
    /// </summary>
    public static string CleanName(string name, bool allowSeparators)
    {
        ArgumentNullException.ThrowIfNull(name);
        var clean = name.Trim().Trim('"');
        if (clean.Length == 0 || clean is "." or "..")
        {
            throw new InvalidNameException(NameProblem.Empty, clean);
        }

        var invalid = Path.GetInvalidFileNameChars().Where(c => !allowSeparators || (c != '\\' && c != '/'));
        if (clean.IndexOfAny(invalid.ToArray()) >= 0 || (OperatingSystem.IsWindows() && clean.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0))
        {
            throw new InvalidNameException(NameProblem.BadCharacters, clean);
        }

        var levels = allowSeparators ? clean.Split('\\', '/') : [clean];
        var separators = allowSeparators ? clean.Where(static c => c is '\\' or '/').ToArray() : [];
        var result = new System.Text.StringBuilder(clean.Length);
        for (var i = 0; i < levels.Length; i++)
        {
            var level = levels[i];
            if (level is not ("." or ".."))
            {
                level = level.TrimEnd('.', ' ');
                if (level.Length == 0 && levels[i].Length > 0)
                {
                    throw new InvalidNameException(NameProblem.Empty, levels[i]);
                }

                if (level.Length > MaxNameLength)
                {
                    throw new InvalidNameException(NameProblem.TooLong, level);
                }
            }

            result.Append(level);
            if (i < separators.Length)
            {
                result.Append(separators[i]);
            }
        }

        return result.ToString();
    }

    /// <summary>The panel's folder must still be there: creating into a deleted folder would quietly bring it back.</summary>
    private static void RequireFolder(string baseDirectory)
    {
        if (!Directory.Exists(ExactPath.Native(ExactPath.Full(baseDirectory))))
        {
            throw new OperationRuleException(RuleViolation.FolderGone, ExactPath.Display(Path.TrimEndingDirectorySeparator(baseDirectory)));
        }
    }
}
