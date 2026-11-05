using PlainCommander.Core.FileSystem;
namespace PlainCommander.Core.Operations;

/// <summary>Quick, synchronous commands: F7 new folder, Shift+F4 new file, Shift+F6 rename.</summary>
public static class SimpleCommands
{
    /// <summary>Creates a folder (nested "a\b\c" allowed) and returns its full path.</summary>
    public static string CreateDirectory(string baseDirectory, string name)
    {
        var clean = CleanName(name, allowSeparators: true);
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
            MoveRaw(hop, destination);
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

    /// <summary>The typed name trimmed (and unquoted); throws <see cref="ArgumentException"/> when it is empty or has characters a name cannot have.</summary>
    public static string CleanName(string name, bool allowSeparators)
    {
        ArgumentNullException.ThrowIfNull(name);
        var clean = name.Trim().Trim('"');
        if (clean.Length == 0 || clean is "." or "..")
        {
            throw new ArgumentException("The name is empty.", nameof(name));
        }

        var invalid = Path.GetInvalidFileNameChars().Where(c => !allowSeparators || (c != '\\' && c != '/'));
        if (clean.IndexOfAny(invalid.ToArray()) >= 0 || (OperatingSystem.IsWindows() && clean.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0))
        {
            throw new ArgumentException($"'{clean}' contains characters that are not allowed in a name.", nameof(name));
        }

        return clean;
    }
}
