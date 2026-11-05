using System.Runtime.CompilerServices;
using PlainCommander.Core.Model;

namespace PlainCommander.Core.FileSystem;

public sealed class LocalFileSystem : IFileSystem
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false, // the listed folder itself must report "access denied", not look empty
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public async IAsyncEnumerable<FileEntry> ListAsync(
        string path,
        bool includeHidden,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var parent = GetParent(path);
        if (parent is not null)
        {
            yield return FileEntry.ParentLink(parent);
        }

        // Enumeration is synchronous I/O; push it off the caller's thread and hand back batches.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<FileEntry>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var producer = Task.Run(() =>
        {
            try
            {
                foreach (var info in new DirectoryInfo(ExactPath.Native(path)).EnumerateFileSystemInfos("*", Options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!includeHidden && info.Attributes.HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }

                    channel.Writer.TryWrite(ToEntry(info));
                }

                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }

        await producer.ConfigureAwait(false);
    }

    public IReadOnlyList<DriveEntry> GetDrives() => DriveInfo.GetDrives().Select(ToDrive).ToList();

    public DriveEntry? GetDrive(string root)
    {
        try
        {
            return ToDrive(new DriveInfo(root));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IDisposable? Watch(string path, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                    | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            };
            watcher.Created += (_, _) => onChanged();
            watcher.Deleted += (_, _) => onChanged();
            watcher.Changed += (_, _) => onChanged();
            watcher.Renamed += (_, _) => onChanged();
            watcher.Error += (_, _) => onChanged(); // buffer overflow: we lost events, so refresh
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            return null;
        }
    }

    public string? GetParent(string path)
    {
        var full = ExactPath.Full(path);
        var parent = Path.GetDirectoryName(full);
        return parent;
    }

    private static DriveEntry ToDrive(DriveInfo d)
    {
        try
        {
            if (d.IsReady)
            {
                return new DriveEntry(d.Name, SafeLabel(d), d.DriveType, d.TotalSize, d.AvailableFreeSpace, true);
            }
        }
        catch (IOException)
        {
            // drive went away between IsReady and the size query
        }
        catch (UnauthorizedAccessException)
        {
            // mounted but not readable for this user
        }

        return new DriveEntry(d.Name, string.Empty, d.DriveType, 0, 0, false);
    }

    private static string SafeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>A file or folder on disk as a panel row.</summary>
    public static FileEntry ToEntry(FileSystemInfo info)
    {
        var isDir = info is DirectoryInfo;
        return new FileEntry
        {
            Name = info.Name,
            FullPath = ExactPath.Display(info.FullName),
            IsDirectory = isDir,
            Size = isDir ? 0 : ((FileInfo)info).Length,
            Modified = info.LastWriteTimeUtc,
            Created = info.CreationTimeUtc,
            Attributes = info.Attributes,
        };
    }
}
