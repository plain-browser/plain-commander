using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Remote;

namespace PlainCommander.Presentation;

/// <summary>One saved connection in the Ctrl+F window; edits go straight to the settings object.</summary>
public sealed partial class SiteRow : ObservableObject
{
    public SiteRow(RemoteSite site, Func<string, bool> hasPassword, Action<SiteRow, string, string>? rootChanged = null)
    {
        Site = site ?? throw new ArgumentNullException(nameof(site));
        HasPasswordFor = hasPassword;
        RootChanged = rootChanged;
    }

    public RemoteSite Site { get; }

    private Func<string, bool> HasPasswordFor { get; }

    /// <summary>Called with the old root and access key when an edit changed how an S3 site logs in, so its secret key can follow.</summary>
    private Action<SiteRow, string, string>? RootChanged { get; }

    public string Title => string.IsNullOrWhiteSpace(Site.Name) ? (string.IsNullOrWhiteSpace(Site.Host) ? Texts.NewConnection : Site.Host) : Site.Name;

    /// <summary>The address under the name; for S3 the service (or its host) and the bucket, not an address with the access key in it.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Site.Host) ? string.Empty
        : !IsS3 ? Site.Root
        : (Provider == S3Provider.Other ? Site.Host : S3Endpoints.Name(Provider)) + (Bucket.Length == 0 ? string.Empty : " · " + Bucket);

    public bool IsUnencrypted => Site.Protocol == RemoteProtocol.Ftp || (Site.Protocol == RemoteProtocol.S3 && (Site.PlainHttp || Site.EffectivePort == 80));

    public bool HasPassword => HasPasswordFor(Site.Root);

    public string Name
    {
        get => Site.Name;
        set => Set(() => Site.Name = value ?? string.Empty);
    }

    /// <summary>0 SFTP, 1 FTP, 2 FTPS, 3 S3 — the order of the protocol list.</summary>
    public int ProtocolIndex
    {
        get => (int)Site.Protocol;
        set => Set(() =>
        {
            Site.Protocol = value is >= 0 and <= 3 ? (RemoteProtocol)value : RemoteProtocol.Sftp;
            if (IsS3 && Site.Host.Length == 0 && Site.Provider == S3Provider.FromAddress)
            {
                Site.Provider = S3Provider.Aws; // a new S3 connection starts as the most common one
                Site.Host = S3Endpoints.AwsHost(Site.Region);
            }
        });
    }

    public string Host
    {
        get => Site.Host;
        set => Set(() => Site.Host = (value ?? string.Empty).Trim());
    }

    /// <summary>Empty for the protocol's default port.</summary>
    public string Port
    {
        get => Site.Port > 0 ? Site.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        set => Set(() => Site.Port = int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536 ? p : 0);
    }

    public string User
    {
        get => Site.User;
        set => Set(() => Site.User = (value ?? string.Empty).Trim());
    }

    public string StartPath
    {
        get => Site.StartPath;
        set => Set(() => Site.StartPath = (value ?? string.Empty).Trim());
    }

    public string KeyFile
    {
        get => Site.KeyFile;
        set => Set(() => Site.KeyFile = (value ?? string.Empty).Trim().Trim('"'));
    }

    public bool ActiveMode
    {
        get => Site.ActiveMode;
        set => Set(() => Site.ActiveMode = value);
    }

    public bool IsSftp => Site.Protocol == RemoteProtocol.Sftp;

    public bool IsS3 => Site.Protocol == RemoteProtocol.S3;

    /// <summary>S3: HTTP instead of HTTPS (a MinIO on the local network).</summary>
    public bool PlainHttp
    {
        get => Site.PlainHttp;
        set => Set(() => Site.PlainHttp = value);
    }

    /// <summary>S3: the region to sign for. At Amazon it also makes the address; elsewhere empty takes it from the host name.</summary>
    public string Region
    {
        // an Amazon site saved with its address only shows the region that is in the address
        get => Site.Region.Length == 0 && IsAws && Site.Host != S3Endpoints.AwsHost(null) ? S3Endpoints.RegionOf(Site.Host) : Site.Region;
        set => Set(() =>
        {
            Site.Region = (value ?? string.Empty).Trim();
            if (IsS3 && Provider == S3Provider.Aws)
            {
                Site.Host = S3Endpoints.AwsHost(Site.Region);
            }
        });
    }

    /// <summary>S3: the service the form is laid out for.</summary>
    public S3Provider Provider => S3Endpoints.ProviderOf(Site);

    /// <summary>The position in the list of services (<see cref="S3Endpoints.Listed"/>).</summary>
    public int ProviderIndex
    {
        get => S3Endpoints.ListIndex(Provider);
        set => Set(() =>
        {
            var provider = value >= 0 && value < S3Endpoints.Listed.Count ? S3Endpoints.Listed[value] : S3Provider.Other;
            if (provider == Provider && Site.Provider != S3Provider.FromAddress)
            {
                return;
            }

            Site.Provider = provider;
            if (provider == S3Provider.Aws)
            {
                // the region alone makes the address, read from the old one when it was an AWS address already
                if (Site.Region.Length == 0 && S3Endpoints.FromHost(Site.Host) == S3Provider.Aws && Site.Host != S3Endpoints.AwsHost(null))
                {
                    Site.Region = S3Endpoints.RegionOf(Site.Host);
                }

                Site.Host = S3Endpoints.AwsHost(Site.Region);
            }
            else if (S3Endpoints.FromHost(Site.Host) != provider)
            {
                Site.Host = string.Empty; // another service's address
            }

            if (provider != S3Provider.Other)
            {
                // what only a server of one's own needs, and what would silently apply from the fields now hidden
                Site.Port = 0;
                Site.PlainHttp = false;
                if (provider != S3Provider.Aws)
                {
                    Site.Region = string.Empty;
                }
            }
        });
    }

    public bool IsAws => IsS3 && Provider == S3Provider.Aws;

    /// <summary>A server of one's own: the port, plain HTTP and the region are there to set.</summary>
    public bool IsOwnServer => IsS3 && Provider == S3Provider.Other;

    /// <summary>
    /// S3: the address of the service. A pasted URL is taken apart: "http://192.168.1.10:9000" gives the host, and for
    /// a server of one's own the port and plain HTTP as well.
    /// </summary>
    public string Endpoint
    {
        get => Site.Host;
        set => Set(() =>
        {
            var (host, port, http) = S3Endpoints.ParseEndpoint(value);
            Site.Host = host;
            if (Provider == S3Provider.Other)
            {
                Site.Port = port > 0 ? port : Site.Port;
                Site.PlainHttp = http ?? Site.PlainHttp;
            }
        });
    }

    /// <summary>S3: the bucket to open after connecting; empty lists the buckets. "bucket/folder" fills both fields.</summary>
    public string Bucket
    {
        get => SplitStart().Bucket;
        set => Set(() =>
        {
            var typed = (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
            var slash = typed.IndexOf('/', StringComparison.Ordinal);
            Site.StartPath = slash < 0 ? JoinStart(typed, SplitStart().Folder) : JoinStart(typed[..slash], typed[(slash + 1)..]);
        });
    }

    /// <summary>S3: the folder (key prefix) inside <see cref="Bucket"/> to open.</summary>
    public string BucketFolder
    {
        get => SplitStart().Folder;
        set => Set(() => Site.StartPath = JoinStart(SplitStart().Bucket, (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/')));
    }

    public bool HasBucket => Bucket.Length > 0;

    private (string Bucket, string Folder) SplitStart()
    {
        var inner = Site.StartPath.Trim().Replace('\\', '/').Trim('/');
        var slash = inner.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (inner, string.Empty) : (inner[..slash], inner[(slash + 1)..].Trim('/'));
    }

    private static string JoinStart(string bucket, string folder) =>
        bucket.Length == 0 ? string.Empty : folder.Length == 0 ? "/" + bucket : "/" + bucket + "/" + folder;

    public void NotifyPassword() => OnPropertyChanged(nameof(HasPassword));

    private void Set(Action change)
    {
        var s3 = IsS3;
        var root = Site.Root;
        var user = Site.User;
        change();
        if (s3 && IsS3 && !string.Equals(root, Site.Root, StringComparison.OrdinalIgnoreCase))
        {
            RootChanged?.Invoke(this, root, user);
        }

        OnPropertyChanged(string.Empty); // every derived text (title, address, padlock) may change
    }
}

/// <summary>
/// Ctrl+F: saved FTP/SFTP/S3 connections. Add, duplicate, delete (its remembered password goes too), forget a password,
/// connect. A quick address ("sftp://user@host") can be typed instead of saving anything.
/// </summary>
public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly List<RemoteSite> _settings;
    private readonly RemoteConnections _remote;
    private readonly ISecretStore _secrets;

    public ConnectionsViewModel(List<RemoteSite> settings, RemoteConnections remote, ISecretStore secrets)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _remote = remote ?? throw new ArgumentNullException(nameof(remote));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        foreach (var site in settings)
        {
            Sites.Add(Row(site));
        }

        Selected = Sites.FirstOrDefault();
    }

    public ObservableCollection<SiteRow> Sites { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanConnect))]
    public partial SiteRow? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    public partial string QuickAddress { get; set; } = string.Empty;

    public bool HasSelection => Selected is not null;

    public bool CanConnect => RemotePath.IsRemote(QuickAddress.Trim()) || Selected is { Site.Host.Length: > 0 };

    /// <summary>What "Connect" opens: the typed address when there is one, else the selected site.</summary>
    public string? Address => RemotePath.IsRemote(QuickAddress.Trim())
        ? QuickAddress.Trim()
        : Selected is { Site.Host.Length: > 0 } row ? row.Site.StartLocation : null;

    public SiteRow Add()
    {
        var site = new RemoteSite { Protocol = RemoteProtocol.Sftp };
        _settings.Add(site);
        var row = Row(site);
        Sites.Add(row);
        Selected = row;
        return row;
    }

    public void Duplicate()
    {
        if (Selected is not { } current)
        {
            return;
        }

        var copy = current.Site.Clone();
        copy.Name = string.IsNullOrWhiteSpace(copy.Name) ? string.Empty : Texts.CopyOf(copy.Name);
        _settings.Insert(_settings.IndexOf(current.Site) + 1, copy);
        var row = Row(copy);
        Sites.Insert(Sites.IndexOf(current) + 1, row);
        Selected = row;
    }

    public void Delete()
    {
        if (Selected is not { } current)
        {
            return;
        }

        var index = Sites.IndexOf(current);
        if (!_settings.Any(s => !ReferenceEquals(s, current.Site) && string.Equals(s.Root, current.Site.Root, StringComparison.OrdinalIgnoreCase)))
        {
            _remote.ForgetPassword(current.Site.Root); // no other site logs in the same way
        }

        _settings.Remove(current.Site);
        Sites.Remove(current);
        Selected = Sites.Count == 0 ? null : Sites[Math.Min(index, Sites.Count - 1)];
    }

    public void ForgetPassword()
    {
        if (Selected is { } current)
        {
            _remote.ForgetPassword(current.Site.Root);
            current.NotifyPassword();
        }
    }

    /// <summary>
    /// S3: the secret key typed in the form goes to the credential store, as if "remember" had been ticked at the
    /// login. False when there is nothing to keep it under yet (no address).
    /// </summary>
    public bool SaveSecret(SiteRow row, string secret)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(row.Site.Host))
        {
            return false;
        }

        _secrets.Write(RemoteConnections.SecretKey(row.Site.Root), secret);
        row.NotifyPassword();
        return true;
    }

    /// <summary>
    /// An S3 site's secret key is kept under its root (access key and address). When an edit changes the root the
    /// key follows, unless another saved site still logs in the old way (then it is copied). The secret key signs
    /// requests and is never sent, so a new address does not learn it. Another access key has another secret: the
    /// old one is not carried over.
    /// </summary>
    private void MoveSecret(SiteRow row, string oldRoot, string oldUser)
    {
        var oldKey = RemoteConnections.SecretKey(oldRoot);
        if (_secrets.Read(oldKey) is not { } secret)
        {
            return;
        }

        var newKey = RemoteConnections.SecretKey(row.Site.Root);
        if (row.Site.Host.Length > 0 && row.Site.User.Length > 0 && string.Equals(row.Site.User, oldUser, StringComparison.Ordinal) && _secrets.Read(newKey) is null)
        {
            _secrets.Write(newKey, secret);
        }

        if (!_settings.Any(s => !ReferenceEquals(s, row.Site) && string.Equals(s.Root, oldRoot, StringComparison.OrdinalIgnoreCase)))
        {
            _secrets.Delete(oldKey);
        }

        row.NotifyPassword();
    }

    /// <summary>Sites without a host are dropped when the window closes: nothing to connect to.</summary>
    public void RemoveEmpty()
    {
        foreach (var row in Sites.Where(r => string.IsNullOrWhiteSpace(r.Site.Host)).ToList())
        {
            _settings.Remove(row.Site);
            Sites.Remove(row);
        }
    }

    private SiteRow Row(RemoteSite site) => new(site, root => _secrets.Read(RemoteConnections.SecretKey(root)) is not null, MoveSecret);
}
