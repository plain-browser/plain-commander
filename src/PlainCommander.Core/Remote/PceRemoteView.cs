using System.Collections.Concurrent;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Crypto;

namespace PlainCommander.Core.Remote;

/// <summary>
/// The highest change number each container kept as objects was seen at, by its path. A store that shows an older
/// state than that had its newest indexes removed; nothing inside the store can tell, so the memory lives here
/// (and, through the app's settings, across runs).
/// </summary>
public static class PceKnownGenerations
{
    private static readonly ConcurrentDictionary<string, ulong> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a container was seen at a higher change number than before.</summary>
    public static event Action? Changed;

    public static ulong Get(string container) => Known.GetValueOrDefault(container);

    public static void Saw(string container, ulong generation)
    {
        var raised = false;
        Known.AddOrUpdate(
            container,
            _ =>
            {
                raised = true;
                return generation;
            },
            (_, old) =>
            {
                raised = generation > old;
                return Math.Max(old, generation);
            });
        if (raised)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The user accepts the container as the store shows it (or it is gone).</summary>
    public static void Forget(string container)
    {
        if (Known.TryRemove(container, out _))
        {
            Changed?.Invoke();
        }
    }

    public static IReadOnlyDictionary<string, ulong> Snapshot() => new Dictionary<string, ulong>(Known, StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes up what an earlier run remembered; a higher number already known here stays.</summary>
    public static void Load(IEnumerable<KeyValuePair<string, ulong>> remembered)
    {
        ArgumentNullException.ThrowIfNull(remembered);
        foreach (var (container, generation) in remembered)
        {
            Known.AddOrUpdate(container, generation, (_, old) => Math.Max(old, generation));
        }
    }
}

/// <summary>
/// A .pce container kept as objects, seen the way a server shows a folder: listings, single files down and up,
/// deletes and renames — so everything that works on a server folder works inside the container. The password
/// comes from <see cref="ArchivePasswords"/> (asked once per session, by the container's path).
/// <para>
/// Changes arrive one item at a time, and an index per item would cost more than the items, so they are gathered
/// (<see cref="PceObjectContainer.Batch"/>) and committed together: when enough have gathered, when some time has
/// passed, before content is read, and by <see cref="Flush"/>, which the owner calls when its work is done.
/// </para>
/// Blocking, and one call at a time, like the client that owns it.
/// </summary>
internal sealed class PceRemoteView : IDisposable
{
    private const int FlushCount = 500;
    private static readonly TimeSpan FlushAfter = TimeSpan.FromSeconds(5);
    private readonly IPceObjectStore _store;
    private readonly string _path;
    private readonly PceOptions? _options;
    private readonly PceSplitOptions? _split;
    private PceObjectContainer? _container;
    private PceObjectContainer.Batch? _batch;
    private DateTime _pendingSince;

    /// <param name="path">The container's full path as the panels show it: names it in the password prompt and in the memory of change numbers.</param>
    public PceRemoteView(IPceObjectStore store, string path, PceOptions? options = null, PceSplitOptions? split = null)
    {
        _store = store;
        _path = path;
        _options = options;
        _split = split;
    }

    /// <summary>Whether two writers at once are safe on the store.</summary>
    public bool SafeForSeveralWriters => _store.ConditionalPut;

    private IReadOnlyList<PceEntry> Entries => _batch?.Entries ?? Container().Entries;

    /// <summary>The files and folders directly in <paramref name="folder"/> ("" is the container itself); null when there is no such folder.</summary>
    public IReadOnlyList<RemoteItem>? List(string folder)
    {
        var container = Container();
        if (_batch is null)
        {
            try
            {
                container.Refresh(); // someone else may have changed it since
            }
            catch (PceRolledBackException ex)
            {
                // Closed, so that once the user accepts the older state the next call opens it as the store shows it.
                _container?.Dispose();
                _container = null;
                throw new PceRolledBackException(ex.Known, ex.Found) { Container = _path };
            }

            PceKnownGenerations.Saw(_path, container.Generation);
        }

        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        var items = new Dictionary<string, RemoteItem>(StringComparer.OrdinalIgnoreCase);
        var exists = folder.Length == 0;
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Path, folder, StringComparison.OrdinalIgnoreCase))
            {
                exists |= entry.IsDirectory;
                continue;
            }

            if (!entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            exists = true;
            var rest = entry.Path[prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                items.TryAdd(rest[..slash], new RemoteItem(rest[..slash], true, 0, DateTimeOffset.MinValue));
            }
            else
            {
                items[rest] = ToItem(rest, entry);
            }
        }

        return exists ? [.. items.Values] : null;
    }

    public RemoteItem? Stat(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        RemoteItem? implied = null;
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return ToItem(name, entry);
            }

            if (implied is null && PceContainer.IsAtOrUnder(entry.Path, path))
            {
                implied = new RemoteItem(name, true, 0, DateTimeOffset.MinValue);
            }
        }

        return implied;
    }

    public void Download(string path, Stream destination, long offset, CancellationToken token)
    {
        Flush(); // the bytes of a file just sent may still be in memory
        var entry = Container().Find(path);
        if (entry is null || entry.IsDirectory)
        {
            throw new FileNotFoundException(null, path);
        }

        using var content = Container().OpenRead(entry);
        if (offset > 0)
        {
            if (content.CanSeek)
            {
                content.Position = offset;
            }
            else
            {
                Skip(content, offset, token);
            }
        }

        var buffer = new byte[1 << 16];
        for (int read; (read = content.Read(buffer)) > 0;)
        {
            token.ThrowIfCancellationRequested();
            destination.Write(buffer, 0, read);
        }
    }

    public void Upload(Stream source, string path, CancellationToken token)
    {
        Batch().Add(new PceSource(path, false, () => new Borrowed(source), DateTime.UtcNow, FileAttributes.Archive), PceCompression.Auto, null, token);
        Changed();
    }

    public void CreateDirectory(string path)
    {
        if (Stat(path) is { } there)
        {
            throw there.IsDirectory ? new IOException($"\"{path}\" already exists.") : new IOException($"\"{path}\" is a file.");
        }

        Batch().Add(new PceSource(path, true, null, DateTime.UtcNow, FileAttributes.None));
        Changed();
    }

    public void DeleteFile(string path)
    {
        if (Stat(path) is not { IsDirectory: false })
        {
            throw new FileNotFoundException(null, path);
        }

        Batch().Delete([path]);
        Changed();
    }

    /// <summary>Removes an empty folder, as a server does.</summary>
    public void DeleteDirectory(string path)
    {
        if (Entries.Any(e => e.Path.Length > path.Length && PceContainer.IsAtOrUnder(e.Path, path)))
        {
            throw new IOException($"\"{path}\" is not empty.");
        }

        if (Batch().Delete([path]) > 0)
        {
            Changed();
        }
    }

    public void Rename(string path, string newPath)
    {
        Batch().Rename(path, newPath);
        Changed();
    }

    public void SetModified(string path, DateTimeOffset modified)
    {
        Batch().Touch(path, modified.UtcDateTime);
        Changed();
    }

    public void ChangePassword(string newPassword)
    {
        Flush();
        Container().ChangePassword(newPassword);
        ArchivePasswords.Remember(_path, newPassword);
    }

    /// <summary>Commits what has gathered, then tidies the store when anything was committed.</summary>
    public void Flush()
    {
        if (_batch is not { Pending: > 0 } batch)
        {
            return;
        }

        batch.Commit();
        PceKnownGenerations.Saw(_path, Container().Generation);
        try
        {
            Container().Clean();
        }
        catch (IOException)
        {
            // the changes are in; what is left to tidy waits for the next time
        }
    }

    public void Dispose()
    {
        _batch?.Dispose();
        _batch = null;
        _container?.Dispose();
        _container = null;
    }

    private static RemoteItem ToItem(string name, PceEntry entry) =>
        new(name, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size, new DateTimeOffset(DateTime.SpecifyKind(entry.ModifiedUtc, DateTimeKind.Utc)));

    private static void Skip(Stream content, long count, CancellationToken token)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = content.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }

    private PceObjectContainer Container()
    {
        if (_container is not null)
        {
            return _container;
        }

        var wrong = false;
        while (true)
        {
            var password = ArchivePasswords.Get(_path, wrong) ?? throw new ArchivePasswordException(_path, wrong);
            try
            {
                _container = PceObjectContainer.Open(_store, password, writable: true, _path, _options, _split, PceKnownGenerations.Get(_path));
                PceKnownGenerations.Saw(_path, _container.Generation);
                return _container;
            }
            catch (PceWrongPasswordException)
            {
                ArchivePasswords.Forget(_path);
                wrong = true;
            }
            catch (PceRolledBackException ex)
            {
                throw new PceRolledBackException(ex.Known, ex.Found) { Container = _path };
            }
        }
    }

    private PceObjectContainer.Batch Batch() => _batch ??= Container().BeginBatch();

    private void Changed()
    {
        var now = DateTime.UtcNow;
        if (_batch!.Pending <= 1)
        {
            _pendingSince = now;
        }

        if (_batch.Pending >= FlushCount || now - _pendingSince >= FlushAfter || now < _pendingSince)
        {
            Flush();
        }
    }

    /// <summary>The caller's stream, read but not closed: whoever opened it closes it.</summary>
    private sealed class Borrowed(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
