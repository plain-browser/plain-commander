using System.Runtime.CompilerServices;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;

namespace PlainCommander.Core.Archives;

/// <summary>
/// Wraps the disk file system and answers for paths that lead into an archive from the archive's
/// table of contents. Archives are read-only here; writing is a later stage.
/// </summary>
public sealed class ArchiveFileSystem : IFileSystem
{
    private readonly IFileSystem _disk;

    public ArchiveFileSystem(IFileSystem disk)
    {
        _disk = disk ?? throw new ArgumentNullException(nameof(disk));
    }

    public static ArchiveLocation? Locate(string path) => ArchivePath.Split(path, File.Exists);

    public async IAsyncEnumerable<FileEntry> ListAsync(string path, bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var location = Locate(path);
        if (location is null)
        {
            await foreach (var entry in _disk.ListAsync(path, includeHidden, cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }

            yield break;
        }

        var catalog = await Task.Run(() => ArchiveCatalog.Open(location.ArchiveFile), cancellationToken).ConfigureAwait(false);
        if (!catalog.DirectoryExists(location.Inner))
        {
            throw new DirectoryNotFoundException(path);
        }

        var parent = GetParent(path);
        if (parent is not null)
        {
            yield return FileEntry.ParentLink(parent);
        }

        foreach (var entry in catalog.List(location.Inner))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return entry;
        }
    }

    public IReadOnlyList<DriveEntry> GetDrives() => _disk.GetDrives();

    public DriveEntry? GetDrive(string root) => _disk.GetDrive(root);

    public bool DirectoryExists(string path)
    {
        var location = Locate(path);
        if (location is null)
        {
            return _disk.DirectoryExists(path);
        }

        try
        {
            return ArchiveCatalog.Open(location.ArchiveFile).DirectoryExists(location.Inner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    public string? GetParent(string path)
    {
        var location = Locate(path);
        if (location is null)
        {
            return _disk.GetParent(path);
        }

        return location.IsRoot
            ? Path.GetDirectoryName(location.ArchiveFile)
            : ArchivePath.Join(location.ArchiveFile, ArchivePath.ParentOf(location.Inner));
    }

    /// <summary>Inside an archive nothing changes under us, so there is nothing to watch.</summary>
    public IDisposable? Watch(string path, Action onChanged) => Locate(path) is null ? _disk.Watch(path, onChanged) : null;
}
