using System.Runtime.CompilerServices;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;

namespace PlainCommander.Core.Remote;

/// <summary>
/// Answers for "sftp://…" and "ftp://…" paths from the server and hands everything else to the wrapped file system
/// (disk and archives). Names starting with "." count as hidden, as on Unix.
/// </summary>
public sealed class RemoteFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;

    public RemoteFileSystem(IFileSystem inner, RemoteConnections connections)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    public RemoteConnections Connections { get; }

    public async IAsyncEnumerable<FileEntry> ListAsync(string path, bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var location = RemotePath.Parse(path);
        if (location is null)
        {
            await foreach (var entry in _inner.ListAsync(path, includeHidden, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }

            yield break;
        }

        var items = await Connections.UseAsync(location.Root, c => c.ListAsync(location.Inner, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (location.Parent is { } parent)
        {
            yield return FileEntry.ParentLink(parent.FullPath);
        }

        foreach (var item in items)
        {
            var hidden = item.Name.StartsWith('.');
            if (hidden && !includeHidden)
            {
                continue;
            }

            yield return ToEntry(location, item);
        }
    }

    public static FileEntry ToEntry(RemoteLocation folder, RemoteItem item)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(item);
        var attributes = item.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
        if (item.Name.StartsWith('.'))
        {
            attributes |= FileAttributes.Hidden;
        }

        if (item.IsLink)
        {
            attributes |= FileAttributes.ReparsePoint;
        }

        return new FileEntry
        {
            Name = item.Name,
            FullPath = folder.Child(item.Name).FullPath,
            IsDirectory = item.IsDirectory,
            Size = item.Size,
            Modified = item.Modified,
            Attributes = attributes,
            Mode = item.Mode,
        };
    }

    public IReadOnlyList<DriveEntry> GetDrives() => _inner.GetDrives();

    public DriveEntry? GetDrive(string root) => RemotePath.IsRemote(root) ? null : _inner.GetDrive(root);

    /// <summary>Remote folders are not checked here (it would block on the network); listing them tells.</summary>
    public bool DirectoryExists(string path) => RemotePath.IsRemote(path) || _inner.DirectoryExists(path);

    public string? GetParent(string path) => RemotePath.Parse(path) is { } location ? location.Parent?.FullPath : _inner.GetParent(path);

    public IDisposable? Watch(string path, Action onChanged) => RemotePath.IsRemote(path) ? null : _inner.Watch(path, onChanged);
}
