using PlainCommander.Core.Model;

namespace PlainCommander.Core.FileSystem;

/// <summary>
/// Abstraction over a browsable tree. The local disk is the first implementation;
/// archives and remote hosts come later without touching the UI.
/// </summary>
public interface IFileSystem
{
    /// <summary>Streams entries so a caller can start work before a huge directory finishes.</summary>
    IAsyncEnumerable<FileEntry> ListAsync(string path, bool includeHidden, CancellationToken cancellationToken = default);

    /// <summary>All drives. Can block on slow or disconnected network drives, so call it off the UI thread.</summary>
    IReadOnlyList<DriveEntry> GetDrives();

    /// <summary>One drive by its root, or null when the root is not a drive (UNC share, unknown).</summary>
    DriveEntry? GetDrive(string root);

    bool DirectoryExists(string path);

    string? GetParent(string path);

    /// <summary>
    /// Calls <paramref name="onChanged"/> (on any thread) when the folder's direct content changes.
    /// Returns null when the location cannot be watched.
    /// </summary>
    IDisposable? Watch(string path, Action onChanged);
}
