using System.Collections.Concurrent;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// Which .pce file a temporary TAR in the panel came from, so that "up" from its root goes back to the folder
/// holding the .pce (not to the temp folder).
/// </summary>
public static class PceBrowse
{
    private static readonly ConcurrentDictionary<string, string> Origins = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string tar, string pceFile) => Origins[Path.TrimEndingDirectorySeparator(tar)] = pceFile;

    /// <summary>The .pce file behind the TAR at <paramref name="path"/> (its root), or null.</summary>
    public static string? OriginOf(string path) =>
        !string.IsNullOrEmpty(path) && Origins.TryGetValue(Path.TrimEndingDirectorySeparator(path), out var origin) ? origin : null;
}
