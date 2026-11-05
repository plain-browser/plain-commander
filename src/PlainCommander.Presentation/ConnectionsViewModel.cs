using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Remote;

namespace PlainCommander.Presentation;

/// <summary>One saved connection in the Ctrl+F window; edits go straight to the settings object.</summary>
public sealed partial class SiteRow : ObservableObject
{
    public SiteRow(RemoteSite site, Func<string, bool> hasPassword)
    {
        Site = site ?? throw new ArgumentNullException(nameof(site));
        HasPasswordFor = hasPassword;
    }

    public RemoteSite Site { get; }

    private Func<string, bool> HasPasswordFor { get; }

    public string Title => string.IsNullOrWhiteSpace(Site.Name) ? (string.IsNullOrWhiteSpace(Site.Host) ? Texts.NewConnection : Site.Host) : Site.Name;

    public string Subtitle => string.IsNullOrWhiteSpace(Site.Host) ? string.Empty : Site.Root;

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
        set => Set(() => Site.Protocol = value is >= 0 and <= 3 ? (RemoteProtocol)value : RemoteProtocol.Sftp);
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

    /// <summary>S3: empty takes the region from an AWS host name.</summary>
    public string Region
    {
        get => Site.Region;
        set => Set(() => Site.Region = (value ?? string.Empty).Trim());
    }

    public void NotifyPassword() => OnPropertyChanged(nameof(HasPassword));

    private void Set(Action change)
    {
        change();
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

    /// <summary>Sites without a host are dropped when the window closes: nothing to connect to.</summary>
    public void RemoveEmpty()
    {
        foreach (var row in Sites.Where(r => string.IsNullOrWhiteSpace(r.Site.Host)).ToList())
        {
            _settings.Remove(row.Site);
            Sites.Remove(row);
        }
    }

    private SiteRow Row(RemoteSite site) => new(site, root => _secrets.Read(RemoteConnections.SecretKey(root)) is not null);
}
