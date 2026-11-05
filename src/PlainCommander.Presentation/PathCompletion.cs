namespace PlainCommander.Presentation;

/// <summary>Suggestions for the path box (Ctrl+L): sub-folders of the typed parent whose name starts with the typed stem.</summary>
public static class PathCompletion
{
    public const int Limit = 20;

    public static IReadOnlyList<string> Suggest(string typed, int limit = Limit)
    {
        if (string.IsNullOrWhiteSpace(typed) || Core.Remote.RemotePath.IsRemote(typed))
        {
            return []; // server paths are not completed: it would mean a network round trip per key
        }

        try
        {
            var endsWithSeparator = typed.EndsWith(Path.DirectorySeparatorChar) || typed.EndsWith(Path.AltDirectorySeparatorChar);
            var parent = endsWithSeparator ? typed : Path.GetDirectoryName(typed);
            var stem = endsWithSeparator ? string.Empty : Path.GetFileName(typed);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return [];
            }

            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            return Directory.EnumerateDirectories(parent, "*", options)
                .Where(d => Path.GetFileName(d).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(d => Path.TrimEndingDirectorySeparator(d) + Path.DirectorySeparatorChar)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
