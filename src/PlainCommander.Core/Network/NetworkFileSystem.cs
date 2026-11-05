using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;

namespace PlainCommander.Core.Network;

/// <summary>
/// Puts the local network in front of the disk: "net://" lists computers (found on the network, plus the servers
/// opened in this session), "net://server" its shares, and the top of a share leads back up to that list. A share
/// or server that answers "access denied" gets one logon through Windows' own dialog, then is asked again.
/// </summary>
public sealed class NetworkFileSystem : IFileSystem
{
    private readonly IFileSystem _disk;
    private readonly INetworkBrowser _browser;
    private readonly ConcurrentDictionary<string, bool> _seen = new(StringComparer.OrdinalIgnoreCase);
    private (DateTime At, IReadOnlyList<string> Names)? _discovered;

    /// <summary>Discovery takes seconds; its answer is reused this long (going up from a server is then instant).</summary>
    public TimeSpan DiscoveryCache { get; set; } = TimeSpan.FromMinutes(2);

    public NetworkFileSystem(IFileSystem disk, INetworkBrowser browser)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    }

    public async IAsyncEnumerable<FileEntry> ListAsync(string path, bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (NetworkPath.ServerOf(path) is { } server)
        {
            var entries = await Task.Run(() => server.Length == 0 ? ComputerEntries(cancellationToken) : ShareEntries(server, includeHidden), cancellationToken).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                yield return entry;
            }

            yield break;
        }

        if (NetworkPath.UncServer(path) is not { } uncServer)
        {
            await foreach (var entry in _disk.ListAsync(path, includeHidden, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }

            yield break;
        }

        _seen[uncServer] = true;
        if (NetworkPath.IsShareRoot(path))
        {
            yield return FileEntry.ParentLink(NetworkPath.ServerPath(uncServer));
        }

        var listed = await ListUncAsync(path, includeHidden, cancellationToken).ConfigureAwait(false);
        foreach (var entry in listed)
        {
            if (!entry.IsParentLink || !NetworkPath.IsShareRoot(path))
            {
                yield return entry;
            }
        }
    }

    /// <summary>A UNC folder, read whole so that a logon can happen and the listing start over.</summary>
    private async Task<List<FileEntry>> ListUncAsync(string path, bool includeHidden, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var entries = new List<FileEntry>();
                await foreach (var entry in _disk.ListAsync(path, includeHidden, cancellationToken).ConfigureAwait(false))
                {
                    entries.Add(entry);
                }

                return entries;
            }
            catch (Exception ex) when (attempt == 0 && NeedsLogon(ex) && ShareOf(path) is not null)
            {
                var share = ShareOf(path)!;
                if (!await Task.Run(() => _browser.Logon(share), cancellationToken).ConfigureAwait(false))
                {
                    throw;
                }

                // connected with the user's password: list again
            }
        }
    }

    private List<FileEntry> ComputerEntries(CancellationToken cancellationToken)
    {
        if (_discovered is not { } cached || DateTime.UtcNow - cached.At > DiscoveryCache)
        {
            cached = (DateTime.UtcNow, _browser.Computers(cancellationToken));
            _discovered = cached;
        }

        var names = new SortedSet<string>(cached.Names, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(_seen.Keys);
        return names.Select(n => new FileEntry
        {
            Name = n,
            FullPath = NetworkPath.ServerPath(n),
            IsDirectory = true,
            Attributes = FileAttributes.Directory,
        }).ToList();
    }

    private List<FileEntry> ShareEntries(string server, bool includeHidden)
    {
        IReadOnlyList<NetworkShare> shares;
        try
        {
            shares = _browser.Shares(server, includeHidden);
        }
        catch (UnauthorizedAccessException) when (_browser.Logon($@"\\{server}\IPC$"))
        {
            shares = _browser.Shares(server, includeHidden);
        }

        _seen[server] = true;
        var entries = new List<FileEntry> { FileEntry.ParentLink(NetworkPath.Root) };
        entries.AddRange(shares.Select(s => new FileEntry
        {
            Name = s.Name,
            FullPath = NetworkPath.Share(server, s.Name),
            IsDirectory = true,
            Attributes = FileAttributes.Directory | (s.Name.EndsWith('$') ? FileAttributes.Hidden : 0),
        }));
        return entries;
    }

    /// <summary>Access denied, wrong or missing password: Windows wants credentials for the share.</summary>
    private static bool NeedsLogon(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException io && (io.HResult & 0xFFFF) is 5 or 86 or 1326 or 1244 or 1312 or 2242);

    private static string? ShareOf(string path)
    {
        if (NetworkPath.UncServer(path) is not { } server)
        {
            return null;
        }

        var rest = path[(2 + server.Length)..].TrimStart('\\');
        var share = rest.Split('\\', 2)[0];
        return share.Length == 0 ? null : NetworkPath.Share(server, share);
    }

    public IReadOnlyList<DriveEntry> GetDrives() => _disk.GetDrives();

    public DriveEntry? GetDrive(string root) => NetworkPath.IsVirtual(root) ? null : _disk.GetDrive(root);

    public bool DirectoryExists(string path) => NetworkPath.IsVirtual(path) || _disk.DirectoryExists(path);

    public string? GetParent(string path)
    {
        if (NetworkPath.ServerOf(path) is { } server)
        {
            return server.Length == 0 ? null : NetworkPath.Root;
        }

        return NetworkPath.IsShareRoot(path) ? NetworkPath.ServerPath(NetworkPath.UncServer(path)!) : _disk.GetParent(path);
    }

    public IDisposable? Watch(string path, Action onChanged) => NetworkPath.IsVirtual(path) ? null : _disk.Watch(path, onChanged);
}
