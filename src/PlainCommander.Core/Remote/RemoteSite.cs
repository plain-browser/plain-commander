using System.Text.Json.Serialization;

namespace PlainCommander.Core.Remote;

/// <summary>A saved connection (Ctrl+F). The password is not here: it lives in the Windows Credential Manager.</summary>
public sealed class RemoteSite
{
    public string Name { get; set; } = string.Empty;

    public RemoteProtocol Protocol { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string User { get; set; } = string.Empty;

    /// <summary>Folder to open after connecting; empty for the server's start folder (home).</summary>
    public string StartPath { get; set; } = string.Empty;

    /// <summary>SFTP: private key file (OpenSSH or PuTTY format); empty to log in with a password.</summary>
    public string KeyFile { get; set; } = string.Empty;

    /// <summary>FTP: active mode instead of passive (only for servers behind odd firewalls).</summary>
    public bool ActiveMode { get; set; }

    /// <summary>S3: plain HTTP instead of HTTPS (a local MinIO). Port 80 means HTTP as well.</summary>
    public bool PlainHttp { get; set; }

    /// <summary>S3: the region to sign for; empty takes it from an AWS host name, else "us-east-1".</summary>
    public string Region { get; set; } = string.Empty;

    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : RemotePath.DefaultPort(Protocol);

    [JsonIgnore]
    public string Root => RemotePath.RootOf(Protocol, User, Host.Trim(), EffectivePort);

    /// <summary>Where the panel goes on "Connect": the start folder, or the server root to be replaced by home.</summary>
    [JsonIgnore]
    public string StartLocation => string.IsNullOrWhiteSpace(StartPath) ? Root : Root + RemotePath.NormalizeInner(StartPath);

    public RemoteSite Clone() => (RemoteSite)MemberwiseClone();

    /// <summary>A site for a typed address ("sftp://user@host/dir") that is not saved.</summary>
    public static RemoteSite FromLocation(RemoteLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new RemoteSite
        {
            Name = location.Host,
            Protocol = location.Protocol,
            Host = location.Host,
            Port = location.Port,
            User = location.User,
        };
    }
}

/// <summary>What a login needs besides the site: the password, or the key's passphrase.</summary>
public sealed record RemoteCredentials(string Secret)
{
    public static readonly RemoteCredentials None = new(string.Empty);
}

/// <summary>One file or folder on a server.</summary>
/// <param name="Mode">Unix permission bits (0–07777) when the server reports them.</param>
public sealed record RemoteItem(string Name, bool IsDirectory, long Size, DateTimeOffset Modified, bool IsLink = false, int? Mode = null);
