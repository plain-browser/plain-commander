using PlainCommander.Core.FileSystem;
namespace PlainCommander.Core.Operations;

/// <summary>Where F5/F6 put things: a folder, plus a new name when exactly one item is copied "as".</summary>
public sealed record TransferTarget(string Directory, string? NewName);

/// <summary>Interprets the target typed in the copy/move dialog.</summary>
public static class TargetResolver
{
    /// <remarks>
    /// - An existing folder, or text ending with a separator or "*.*", means "into this folder".
    /// - Otherwise, with one source, the last segment is the new name ("copy as", rename).
    /// - With several sources it is a folder that will be created.
    /// Relative input is relative to the source panel's folder. Rename masks such as "*.bak" are not supported yet.
    /// </remarks>
    public static TransferTarget Resolve(string input, string currentDirectory, int sourceCount)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = input.Trim().Trim('"');
        if (text.Length == 0)
        {
            throw new ArgumentException("Target is empty.", nameof(input));
        }

        var name = Path.GetFileName(text);
        var intoFolder = text.EndsWith(Path.DirectorySeparatorChar) || text.EndsWith(Path.AltDirectorySeparatorChar);
        if (name is "*.*" or "*")
        {
            text = text[..^name.Length];
            intoFolder = true;
        }
        else if (name.Contains('*', StringComparison.Ordinal) || name.Contains('?', StringComparison.Ordinal))
        {
            throw new NotSupportedException("Rename masks are not supported yet.");
        }

        var full = ExactPath.Full(Path.IsPathRooted(text) ? text : Path.Combine(currentDirectory, text));
        if (intoFolder || Directory.Exists(full) || sourceCount > 1)
        {
            return new TransferTarget(KeepRoot(full), null);
        }

        return new TransferTarget(Path.GetDirectoryName(full) ?? full, Path.GetFileName(full));
    }

    private static string KeepRoot(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
