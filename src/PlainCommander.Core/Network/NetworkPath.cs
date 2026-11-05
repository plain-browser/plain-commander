namespace PlainCommander.Core.Network;

/// <summary>
/// The two places of the local network that are not folders: "net://" (the computers) and "net://server" (its shared
/// folders). A share itself is an ordinary UNC path, "\\server\share", listed by the disk.
/// </summary>
public static class NetworkPath
{
    public const string Root = "net://";

    public static bool IsVirtual(string? path) => path is not null && path.StartsWith(Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>"net://pc" → "pc"; "" for the root; null when the path is not a network place.</summary>
    public static string? ServerOf(string? path) => IsVirtual(path) ? path![Root.Length..].Trim('/', '\\') : null;

    public static string ServerPath(string server) => Root + server.Trim('/', '\\');

    /// <summary>"net:\\pc", "NET://pc/" → "net://pc".</summary>
    public static string Normalize(string path)
    {
        var server = ServerOf(path) ?? throw new ArgumentException("Not a network place.", nameof(path));
        return server.Length == 0 ? Root : ServerPath(server);
    }

    public static string Share(string server, string share) => $@"\\{server}\{share}";

    /// <summary>The server of a UNC path ("\\pc\docs\x" → "pc"), or null.</summary>
    public static string? UncServer(string? path)
    {
        if (path is null || !path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return null;
        }

        var end = path.IndexOf('\\', 2);
        var server = end < 0 ? path[2..] : path[2..end];
        return server.Length == 0 ? null : server;
    }

    /// <summary>"\\pc" or "\\pc\" typed as a path: the computer itself, shown as its share list ("net://pc").</summary>
    public static string? AsServerPlace(string? path) =>
        UncServer(path) is { } server && path!.TrimEnd('\\').Length == server.Length + 2 ? ServerPath(server) : null;

    /// <summary>True for "\\pc\docs" and "\\pc\docs\": the top of a share, whose parent is the server's share list.</summary>
    public static bool IsShareRoot(string? path)
    {
        if (UncServer(path) is not { } server)
        {
            return false;
        }

        var rest = path![(2 + server.Length)..].Trim('\\');
        return rest.Length > 0 && !rest.Contains('\\', StringComparison.Ordinal);
    }
}
