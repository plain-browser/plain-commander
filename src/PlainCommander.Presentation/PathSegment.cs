namespace PlainCommander.Presentation;

/// <summary>One clickable piece of the breadcrumb: "work" in C:\work\src, with the path it leads to.</summary>
public sealed record PathSegment(string Name, string FullPath)
{
    /// <summary>
    /// Splits a full path into breadcrumb segments. Works on the string alone (no file system), so
    /// Windows paths split the same way in WSL tests: "C:\work\src" → C:, work, src.
    /// UNC roots stay in one piece: "\\server\share\x" → \\server\share, x. "/" roots give "/" first.
    /// </summary>
    public static IReadOnlyList<PathSegment> Split(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        if (PlainCommander.Core.Remote.RemotePath.Parse(path) is { } remote)
        {
            // "sftp://jan@host:22/var/www" → host, var, www; the first piece opens the server root
            var pieces = new List<PathSegment> { new(remote.Host, remote.Root + "/") };
            var at = remote.Root;
            foreach (var name in remote.Inner.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                at += "/" + name;
                pieces.Add(new PathSegment(name, at));
            }

            return pieces;
        }

        if (PlainCommander.Core.Network.NetworkPath.ServerOf(path) is { } server)
        {
            // "net://pc" → Network, pc
            var network = new List<PathSegment> { new(Texts.Network, PlainCommander.Core.Network.NetworkPath.Root) };
            if (server.Length > 0)
            {
                network.Add(new PathSegment(server, PlainCommander.Core.Network.NetworkPath.ServerPath(server)));
            }

            return network;
        }

        var sep = path.Contains('\\') ? '\\' : '/';
        string rootName, rootPath, rest;
        var leading = new List<PathSegment>();
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // "\\pc\docs\x" → Network, pc, docs, x: the share list and the computers are one click up
            var parts = path[2..].Split(sep, 3, StringSplitOptions.None);
            leading.Add(new PathSegment(Texts.Network, PlainCommander.Core.Network.NetworkPath.Root));
            leading.Add(new PathSegment(parts[0], PlainCommander.Core.Network.NetworkPath.ServerPath(parts[0])));
            rootName = parts.Length >= 2 ? parts[1] : path;
            rootPath = parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" + sep : path;
            rest = parts.Length == 3 ? parts[2] : string.Empty;
        }
        else if (path.Length >= 2 && path[1] == ':')
        {
            rootName = path[..2];
            rootPath = rootName + sep;
            rest = path.Length > 3 ? path[3..] : string.Empty;
        }
        else if (path[0] == '/')
        {
            rootName = "/";
            rootPath = "/";
            rest = path[1..];
        }
        else
        {
            rootName = string.Empty;
            rootPath = string.Empty;
            rest = path;
        }

        var segments = leading;
        if (rootName.Length > 0)
        {
            segments.Add(new PathSegment(rootName, rootPath));
        }

        var current = rootPath;
        foreach (var name in rest.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 || current[^1] == sep ? current + name : current + sep + name;
            segments.Add(new PathSegment(name, current));
        }

        return segments;
    }
}
