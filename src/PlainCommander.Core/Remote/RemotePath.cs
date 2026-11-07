using System.Globalization;

namespace PlainCommander.Core.Remote;

public enum RemoteProtocol
{
    Sftp,

    /// <summary>Plain FTP: nothing is encrypted, the password included.</summary>
    Ftp,

    /// <summary>FTP over TLS. Port 990 means implicit TLS, any other port explicit (AUTH TLS).</summary>
    Ftps,

    /// <summary>Amazon S3 or a compatible store: buckets are the top folders, the user is the access key.</summary>
    S3,
}

/// <summary>
/// A place on a server as the panels show it: "sftp://user@host:22/home/user". <see cref="Inner"/> is the server path,
/// always absolute with "/" separators; <see cref="Root"/> ("sftp://user@host:22") names the connection.
/// </summary>
public sealed record RemoteLocation(RemoteProtocol Protocol, string User, string Host, int Port, string Inner)
{
    public string Root => RemotePath.RootOf(Protocol, User, Host, Port);

    public string FullPath => Root + Inner;

    public bool IsRoot => Inner == "/";

    /// <summary>Last name of the path; the host for the server root.</summary>
    public string Name => IsRoot ? Host : Inner[(Inner.LastIndexOf('/') + 1)..];

    /// <summary>
    /// One entry of this folder, taken literally: a listed name is a single segment even when it holds "\" or "..",
    /// which a Unix server allows. For a path the user typed use <see cref="Resolve"/>.
    /// </summary>
    public RemoteLocation Child(string name)
    {
        if (!RemotePath.IsSingleName(name))
        {
            throw new ArgumentException($"Not a single name: {name}", nameof(name));
        }

        return this with { Inner = Inner.TrimEnd('/') + "/" + name };
    }

    /// <summary>A relative path typed by the user ("a/b", "../c"), resolved against this folder.</summary>
    public RemoteLocation Resolve(string relative) => this with { Inner = RemotePath.JoinInner(Inner, relative.Replace('\\', '/')) };

    public RemoteLocation? Parent => IsRoot ? null : this with { Inner = RemotePath.ParentInner(Inner) };
}

/// <summary>Parses and builds remote paths. Pure string work, no network.</summary>
public static class RemotePath
{
    public static string Scheme(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => "sftp",
        RemoteProtocol.Ftps => "ftps",
        RemoteProtocol.S3 => "s3",
        _ => "ftp",
    };

    public static int DefaultPort(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.S3 => 443,
        _ => 21,
    };

    public static bool IsRemote(string? path) =>
        path is not null && (path.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("s3://", StringComparison.OrdinalIgnoreCase));

    public static string RootOf(RemoteProtocol protocol, string user, string host, int port)
    {
        var who = string.IsNullOrEmpty(user) ? string.Empty : Uri.EscapeDataString(user) + "@";
        var hostPart = host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host;
        return string.Create(CultureInfo.InvariantCulture, $"{Scheme(protocol)}://{who}{hostPart.ToLowerInvariant()}:{port}");
    }

    /// <summary>
    /// "sftp://user@host:2222/dir", "ftp://host/dir" (default port), "sftp://user@host" (server root). Null when the
    /// text is not a remote path or has no host. "." and ".." are resolved; a backslash is part of a name, as on the
    /// server, so a listed name like "..\x" round-trips through <see cref="RemoteLocation.FullPath"/>.
    /// </summary>
    public static RemoteLocation? Parse(string? path)
    {
        if (!IsRemote(path))
        {
            return null;
        }

        var schemeEnd = path!.IndexOf("://", StringComparison.Ordinal);
        var protocol = path[..schemeEnd].ToLowerInvariant() switch
        {
            "sftp" => RemoteProtocol.Sftp,
            "ftps" => RemoteProtocol.Ftps,
            "s3" => RemoteProtocol.S3,
            _ => RemoteProtocol.Ftp,
        };
        var rest = path[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        var inner = slash < 0 ? "/" : rest[slash..];

        var user = string.Empty;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            user = Uri.UnescapeDataString(authority[..at]);
            authority = authority[(at + 1)..];
        }

        string host;
        var port = DefaultPort(protocol);
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            host = authority[1..close];
            authority = authority[(close + 1)..];
            if (authority.StartsWith(':') && !TryPort(authority[1..], out port))
            {
                return null;
            }
        }
        else if (authority.IndexOf(':', StringComparison.Ordinal) != authority.LastIndexOf(':'))
        {
            host = authority; // an IPv6 literal without brackets has no room for a port
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            if (colon >= 0 && !TryPort(authority[(colon + 1)..], out port))
            {
                return null;
            }
        }

        return host.Length == 0 ? null : new RemoteLocation(protocol, user, host.ToLowerInvariant(), port, NormalizeInner(inner));
    }

    /// <summary>An address the user typed (Ctrl+L, cd, F5 target): "\" counts as "/" there, as in any Windows path box.</summary>
    public static RemoteLocation? ParseTyped(string? text) => Parse(text?.Replace('\\', '/'));

    /// <summary>"/a/./b/../c/" → "/a/c"; never climbs above "/".</summary>
    public static string NormalizeInner(string inner)
    {
        var parts = new List<string>();
        foreach (var part in inner.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    /// <summary>A name that <see cref="RemoteLocation.Child"/> takes: not empty, ".", ".." and without "/".</summary>
    public static bool IsSingleName(string? name) =>
        !string.IsNullOrEmpty(name) && name is not ("." or "..") && !name.Contains('/', StringComparison.Ordinal);

    /// <summary>
    /// A server name as a local file name: characters the local disk cannot have (on Windows "\", ":", "*" …)
    /// become "_", so a name like "..\x" stays one file inside the download folder.
    /// </summary>
    public static string LocalName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var invalid = Path.GetInvalidFileNameChars();
        var local = string.Create(name.Length, name, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = Array.IndexOf(invalid, source[i]) >= 0 ? '_' : source[i];
            }
        });
        return local is "." or ".." || local.Length == 0 ? "_" : local;
    }

    public static string JoinInner(string folder, string name) =>
        NormalizeInner(folder.TrimEnd('/') + "/" + name);

    public static string ParentInner(string inner)
    {
        var trimmed = inner.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    /// <summary>Last name of a remote path (virtual or inner).</summary>
    public static string NameOf(string path)
    {
        var location = Parse(path);
        if (location is not null)
        {
            return location.Name;
        }

        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;
}
