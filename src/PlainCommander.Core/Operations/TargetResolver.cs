using PlainCommander.Core.FileSystem;
namespace PlainCommander.Core.Operations;

/// <summary>
/// Where F5/F6 put things: a folder, plus a new name when exactly one item is copied "as", or a name mask
/// ("*.bak") that gives every item its new name.
/// </summary>
public sealed record TransferTarget(string Directory, string? NewName, string? Mask = null)
{
    /// <summary>The name an item called <paramref name="sourceName"/> gets in the target folder.</summary>
    public string NameFor(string sourceName) => NewName ?? (Mask is null ? sourceName : RenameMask.Apply(Mask, sourceName));
}

/// <summary>Interprets the target typed in the copy/move dialog.</summary>
public static class TargetResolver
{
    /// <remarks>
    /// - An existing folder, or text ending with a separator or "*.*", means "into this folder".
    /// - Otherwise, with one source, the last segment is the new name ("copy as", rename).
    /// - With several sources it is a folder that will be created.
    /// - A last segment with "*" or "?" is a name mask (<see cref="RenameMask"/>): the folder is what stands before it.
    /// Relative input is relative to the source panel's folder.
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
        else if (RenameMask.IsMask(name))
        {
            var folder = text[..^name.Length];
            var directory = ExactPath.Full(folder.Length == 0 ? currentDirectory : Path.IsPathRooted(folder) ? folder : Path.Combine(currentDirectory, folder));
            return new TransferTarget(KeepRoot(directory), null, name);
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
