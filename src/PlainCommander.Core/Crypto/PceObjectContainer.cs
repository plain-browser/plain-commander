using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using SharpCompress.Compressors.ZStandard;

namespace PlainCommander.Core.Crypto;

/// <summary>Sizes that shape a container kept as objects. The defaults are for S3; tests shrink them.</summary>
internal sealed record PceSplitOptions
{
    public static PceSplitOptions Default { get; } = new();

    /// <summary>An entry whose stored bytes are fewer than this shares a pack object with other small entries.</summary>
    public int SmallLimit { get; init; } = 8 << 20;

    /// <summary>A pack is stored when the next small entry would take it past this.</summary>
    public int PackSize { get; init; } = 32 << 20;

    /// <summary>Part of an upload in parts; not below <see cref="SmallLimit"/> (and S3 wants at least 5 MiB).</summary>
    public int PartSize { get; init; } = 16 << 20;

    /// <summary>Parts one object may have (S3: 10 000). A longer entry continues in another object.</summary>
    public int MaxParts { get; init; } = 10_000;

    /// <summary>Parts sent at once.</summary>
    public int Parallelism { get; init; } = 8;

    /// <summary>The most a sequential read fetches ahead in one request.</summary>
    public int ReadAhead { get; init; } = 8 << 20;

    /// <summary>Cleaning up leaves unreferenced data objects younger than this: another writer may be about to commit them.</summary>
    public TimeSpan OrphanAge { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Newest indexes kept whatever their age.</summary>
    public int KeptIndexes { get; init; } = 2;

    /// <summary>
    /// Cleaning up deletes an index only when it is older than this. "Create only if absent" protects a generation
    /// only while its index object exists, so an index must outlive every writer that might still commit over it:
    /// see <see cref="ViewLease"/>.
    /// </summary>
    public TimeSpan IndexAge { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// A writer whose picture of the container is older than this reads the newest index again before it commits.
    /// Far below <see cref="IndexAge"/>, so the generation it is about to take cannot have been used and cleaned away.
    /// </summary>
    public TimeSpan ViewLease { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>This computer's clock (tests move it).</summary>
    public Func<DateTime> UtcNow { get; init; } = static () => DateTime.UtcNow;
}

/// <summary>What <see cref="PceObjectContainer.Clean"/> did.</summary>
/// <param name="Orphans">Data objects no index refers to, deleted.</param>
/// <param name="Indexes">Old index objects deleted.</param>
/// <param name="Repacked">Packs rewritten without the bytes of deleted entries.</param>
public readonly record struct PceCleanResult(int Orphans, int Indexes, int Repacked);

/// <summary>
/// The store shows an older state than this container was last seen in: its newest indexes were removed. Someone
/// with access to the store can do that; without the remembered generation it cannot be told from the truth.
/// </summary>
public sealed class PceRolledBackException(ulong known, ulong found)
    : IOException($"The encrypted container is older than when it was last opened here (change {found}, last seen {known}).")
{
    public ulong Known { get; } = known;

    public ulong Found { get; } = found;
}

/// <summary>
/// A .pce container kept as separate immutable objects (<see cref="PceObjectNames"/>), for stores like S3 where an
/// object cannot be changed in place. Entry streams are the same bytes as in a container file; what differs is
/// that every change is one new index object:
/// <para>
/// data objects are stored first, then the index that refers to them is committed with "create only if absent" —
/// it exists or it does not, and of two writers committing the same generation one loses, reloads and applies its
/// change again. Objects the new index no longer refers to are deleted after the commit. An interrupted change
/// therefore leaves only objects no index refers to, which <see cref="Clean"/> removes, along with indexes that
/// are no longer the newest and are old enough that no writer can still be committing over them.
/// </para>
/// </summary>
public sealed class PceObjectContainer : IPceContainer
{
    private const int HeaderSize = PceContainerFormat.HeaderSize;
    private const int FooterSize = PceContainerFormat.FooterSize;
    private const int CommitAttempts = 16;

    private readonly Lock _gate = new();
    private readonly IPceObjectStore _store;
    private readonly bool _writable;
    private readonly byte[] _master;
    private readonly PceOptions _options;
    private readonly PceSplitOptions _split;
    private PceContainerHeader _header;
    private List<PceEntry> _entries;
    private ulong _generation;
    private DateTime _viewed; // when the entries were known to be the newest
    private bool _stale;
    private bool _disposed;

    private PceObjectContainer(IPceObjectStore store, bool writable, PceContainerHeader header, byte[] master, PceOptions options, PceSplitOptions split)
    {
        if (split.SmallLimit > split.PartSize || split.SmallLimit > split.PackSize || split.MaxParts < 1 || split.Parallelism < 1 || split.KeptIndexes < 1)
        {
            throw new ArgumentException("Inconsistent sizes.", nameof(split));
        }

        _store = store;
        _writable = writable;
        _header = header;
        _master = master;
        _options = options;
        _split = split;
        _entries = [];
    }

    /// <summary>The entries as of the last change made or seen here. The list is replaced, not edited.</summary>
    public IReadOnlyList<PceEntry> Entries => _entries;

    public bool IsWritable => _writable;

    /// <summary>
    /// The number of the last change. Remembered outside the store and passed back to <see cref="Open"/>, it is
    /// what reveals a container put back to an older state.
    /// </summary>
    public ulong Generation => _generation;

    /// <summary>Whether two writers at once are safe on this store.</summary>
    public bool SafeForSeveralWriters => _store.ConditionalPut;

    internal PceContainerHeader Header => _header;

    /// <summary>A new, empty container in an empty store, open for changes.</summary>
    internal static PceObjectContainer Create(IPceObjectStore store, string password, PceOptions? options = null, PceSplitOptions? split = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        options ??= PceOptions.Default;
        if (store.List(string.Empty, 1).Count > 0)
        {
            throw new IOException("There is already something where the encrypted container would go.");
        }

        var master = RandomNumberGenerator.GetBytes(PceFormat.KeySize);
        try
        {
            var header = PceContainerFormat.NewHeader(options, password, master, split: true);
            var container = new PceObjectContainer(store, true, header, master, options, split ?? PceSplitOptions.Default);
            container._viewed = container._split.UtcNow(); // the store was empty a moment ago
            try
            {
                store.Put(PceObjectNames.Head, PceContainerFormat.WriteHeader(header));
                container.Commit(static _ => []);
            }
            catch (IOException)
            {
                // Without its first index there is no container: the header alone would only be in the way of
                // the next attempt, which wants the place empty.
                try
                {
                    store.Delete(PceObjectNames.Head);
                }
                catch (IOException)
                {
                }

                throw;
            }

            return container;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(master);
            throw;
        }
    }

    /// <summary>
    /// Opens a container with three requests: the newest index's name, the header, the index.
    /// <see cref="PceWrongPasswordException"/> for a wrong password, <see cref="PceFormatException"/> for something
    /// that is not a container or is damaged, <see cref="PceRolledBackException"/> when the newest index is older
    /// than <paramref name="knownGeneration"/>.
    /// </summary>
    /// <param name="name">What to call the container in messages.</param>
    internal static PceObjectContainer Open(
        IPceObjectStore store,
        string password,
        bool writable = false,
        string? name = null,
        PceOptions? options = null,
        PceSplitOptions? split = null,
        ulong knownGeneration = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var viewed = (split ?? PceSplitOptions.Default).UtcNow();
        var (generation, sealedIndex) = Newest(store);
        var copyBytes = sealedIndex.AsSpan(sealedIndex.Length - FooterSize - HeaderSize, HeaderSize).ToArray();

        // The head object decides. Only when it is gone or not a header does the copy inside the newest index
        // stand in — never for a wrong password, or a copy written before a password change would keep the old
        // password alive.
        PceContainerHeader? header = null;
        var fromCopy = false;
        if (store.Get(PceObjectNames.Head) is { Length: HeaderSize } headBytes)
        {
            try
            {
                header = PceContainerFormat.ReadHeader(headBytes, split: true);
            }
            catch (PceFormatException)
            {
            }
        }

        if (header is null)
        {
            header = PceContainerFormat.ReadHeader(copyBytes, split: true);
            fromCopy = true;
        }

        var master = PceContainerFormat.Unwrap(header, password) ?? throw new PceWrongPasswordException(name ?? "the encrypted container");
        try
        {
            var container = new PceObjectContainer(store, writable, header, master, options ?? PceOptions.Default, split ?? PceSplitOptions.Default);
            container._entries = container.OpenIndex(generation, sealedIndex);
            container._generation = generation;
            container._viewed = viewed;
            if (generation < knownGeneration)
            {
                throw new PceRolledBackException(knownGeneration, generation);
            }

            if (fromCopy && writable)
            {
                store.Put(PceObjectNames.Head, copyBytes);
            }

            return container;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(master);
            throw;
        }
    }

    /// <summary>The entry at <paramref name="path"/> (case-insensitive), or null.</summary>
    public PceEntry? Find(string path)
    {
        var normalized = PceContainer.NormalizePath(path);
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Path, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// The entry's content. Seekable unless the entry is compressed; reading on fetches ahead, a seek fetches only
    /// the blocks it lands on. Damage surfaces as <see cref="PceFormatException"/>, an entry deleted meanwhile as
    /// <see cref="PceObjectMissingException"/>.
    /// </summary>
    public Stream OpenRead(PceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (entry.IsDirectory)
            {
                throw new ArgumentException("A folder has no content.", nameof(entry));
            }

            var blocks = new PceBlockReader(new ObjectExtents(_store, entry, _split.ReadAhead), _header.Cipher, entry, _header.ChunkSize);
            if (entry.Compressed)
            {
                return new PceFile.CorruptAsFormatStream(new DecompressionStream(blocks, leaveOpen: false));
            }

            if (blocks.Length != entry.Size)
            {
                blocks.Dispose();
                throw new PceFormatException($"Entry \"{entry.Path}\" is damaged (its size does not match its content).");
            }

            return blocks;
        }
    }

    /// <summary>
    /// Adds entries; one whose path is already taken replaces the old entry. Nothing changes unless all of them
    /// were stored: a failure or a cancellation leaves the container as it was.
    /// </summary>
    /// <param name="progress">Called with the plain bytes read so far.</param>
    public void Add(IEnumerable<PceSource> sources, PceCompression compression = PceCompression.None, Action<long>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            EnsureWritable();
            var added = new List<PceEntry>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long done = 0;
            using var writer = new DataWriter(_store, _split, token);
            try
            {
                foreach (var source in sources)
                {
                    token.ThrowIfCancellationRequested();
                    var path = PceContainer.NormalizePath(source.Path);
                    if (!taken.Add(path))
                    {
                        throw new ArgumentException($"\"{path}\" is given twice.", nameof(sources));
                    }

                    added.Add(source.IsDirectory
                        ? new PceEntry(path, true, 0, source.ModifiedUtc.ToUniversalTime(), source.Attributes & ~FileAttributes.Directory, false, new byte[PceFormat.KeySize], [])
                        : WriteEntry(writer, source, path, compression, ref done, progress, token));
                }

                writer.Finish();
                token.ThrowIfCancellationRequested(); // a cancel during the last block still cancels
            }
            catch
            {
                writer.Abandon();
                throw;
            }

            if (added.Count > 0)
            {
                // From here the data stays whatever happens: when the commit's outcome is unknown the index may
                // refer to it, and when it is known to have failed Clean removes what nothing refers to.
                Commit(entries => [.. entries.Where(e => !taken.Contains(e.Path)), .. added]);
            }
        }
    }

    /// <summary>The entry's sealed blocks as they lie in the data objects, for carrying to another container.</summary>
    internal IPceStoredBytes OpenStored(PceEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ObjectExtents(_store, entry, _split.ReadAhead);
    }

    /// <summary>
    /// Adds entries that are already sealed (in a container of the same cipher and block size): their bytes are
    /// stored as they are, large ones in objects of their own and small ones in packs, and they keep their keys.
    /// Otherwise as <see cref="Add"/>: all of them or none.
    /// </summary>
    /// <param name="progress">Called with the stored bytes copied so far.</param>
    internal void AddStored(IEnumerable<PceStored> items, Action<long>? progress = null, CancellationToken token = default)
    {
        lock (_gate)
        {
            EnsureWritable();
            var added = new List<PceEntry>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long done = 0;
            using var writer = new DataWriter(_store, _split, token);
            try
            {
                foreach (var item in items)
                {
                    token.ThrowIfCancellationRequested();
                    var entry = item.Entry;
                    if (!taken.Add(entry.Path))
                    {
                        throw new ArgumentException($"\"{entry.Path}\" is given twice.", nameof(items));
                    }

                    if (entry.StoredSize > 0)
                    {
                        using var bytes = item.Open();
                        PceTransfer.Pump(bytes, entry.StoredSize, writer.Write, ref done, progress, token);
                    }

                    added.Add(new PceEntry(entry.Path, entry.IsDirectory, entry.Size, entry.ModifiedUtc, entry.Attributes, entry.Compressed, entry.Key, writer.EndEntry()));
                }

                writer.Finish();
                token.ThrowIfCancellationRequested();
            }
            catch
            {
                writer.Abandon();
                throw;
            }

            if (added.Count > 0)
            {
                Commit(entries => [.. entries.Where(e => !taken.Contains(e.Path)), .. added]);
            }
        }
    }

    /// <summary>
    /// Deletes the entries at <paramref name="paths"/> and everything under them. Returns how many entries went.
    /// An object none of whose entries remain is deleted at once; a pack with other entries in it keeps the bytes
    /// until <see cref="Clean"/> rewrites it.
    /// </summary>
    public int Delete(IEnumerable<string> paths, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            EnsureWritable();
            var roots = paths.Select(PceContainer.NormalizePath).ToList();
            token.ThrowIfCancellationRequested();
            var removed = 0;
            Commit(entries =>
            {
                var keep = entries.Where(e => !roots.Exists(r => PceContainer.IsAtOrUnder(e.Path, r))).ToList();
                removed = entries.Count - keep.Count;
                return removed > 0 ? keep : null;
            });
            return removed;
        }
    }

    /// <summary>Renames (or moves) an entry and everything under it: one new index.</summary>
    public void Rename(string path, string newPath)
    {
        lock (_gate)
        {
            EnsureWritable();
            Commit(entries => PceContainer.Renamed(entries, path, newPath));
        }
    }

    /// <summary>
    /// Replaces the password: the master key is wrapped again under a key from the new password and stored as the
    /// head object, in one request. The content and the index are not touched.
    /// </summary>
    public void ChangePassword(string newPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        lock (_gate)
        {
            EnsureWritable();
            var header = PceContainerFormat.Wrap(_header, newPassword, _master);
            _store.Put(PceObjectNames.Head, PceContainerFormat.WriteHeader(header));
            _header = header;
        }
    }

    /// <summary>Takes up the changes made from elsewhere since this container was opened or last changed.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Reload();
        }
    }

    /// <summary>
    /// Removes what interrupted changes and deletions left in the store: old indexes, data objects no index refers
    /// to (once they are old enough not to belong to a change in progress elsewhere), and the dead bytes of packs
    /// more than half of which belongs to deleted entries.
    /// </summary>
    public PceCleanResult Clean(CancellationToken token = default)
    {
        lock (_gate)
        {
            EnsureWritable();
            Reload();
            var indexes = _store.List(PceObjectNames.IndexPrefix, int.MaxValue, token);
            var data = _store.List(PceObjectNames.DataPrefix, int.MaxValue, token);
            if (indexes.Count > 0 && PceObjectNames.TryGeneration(indexes[0].Name, out var newest) && newest > _generation)
            {
                Reload(); // a change landed between the two listings; what it refers to is already listed or younger
            }

            // "Now" is the store's own clock: the youngest object it holds. It is never ahead of the real time, so
            // nothing is taken for older than it is.
            var now = indexes.Concat(data).Select(o => o.ModifiedUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            var removedIndexes = 0;
            var kept = 0;
            foreach (var index in indexes)
            {
                if (!PceObjectNames.TryGeneration(index.Name, out var generation) || generation > _generation || ++kept <= _split.KeptIndexes
                    || now - index.ModifiedUtc < _split.IndexAge)
                {
                    continue;
                }

                token.ThrowIfCancellationRequested();
                _store.Delete(index.Name, token);
                removedIndexes++;
            }

            if (now - DateTime.MinValue > _split.OrphanAge)
            {
                _store.DiscardUploads(now - _split.OrphanAge, token);
            }

            var live = new Dictionary<PceObjectId, long>();
            foreach (var extent in _entries.SelectMany(e => e.Extents))
            {
                live[extent.Object] = live.GetValueOrDefault(extent.Object) + extent.Length;
            }

            var orphans = 0;
            var sparse = new HashSet<PceObjectId>();
            foreach (var item in data)
            {
                token.ThrowIfCancellationRequested();
                if (!PceObjectNames.TryData(item.Name, out var id))
                {
                    continue; // not ours to judge
                }

                if (!live.TryGetValue(id, out var used))
                {
                    if (now - item.ModifiedUtc >= _split.OrphanAge)
                    {
                        _store.Delete(item.Name, token);
                        orphans++;
                    }
                }
                else if (used * 2 < item.Size)
                {
                    sparse.Add(id);
                }
            }

            return new PceCleanResult(orphans, removedIndexes, Repack(sparse, token));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                CryptographicOperations.ZeroMemory(_master);
            }
        }
    }

    /// <summary>The newest index object: its generation (from the name) and its bytes.</summary>
    private static (ulong Generation, byte[] Sealed) Newest(IPceObjectStore store)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var newest = store.List(PceObjectNames.IndexPrefix, 1);
            if (newest.Count == 0)
            {
                throw new PceFormatException("Not a Plain Commander encrypted container (it has no index).");
            }

            if (!PceObjectNames.TryGeneration(newest[0].Name, out var generation))
            {
                throw new PceFormatException($"The encrypted container holds something that is not its own (\"{newest[0].Name}\").");
            }

            // Gone between the listing and the read only when two changes and a clean-up landed in between: look again.
            if (store.Get(newest[0].Name) is { } sealedIndex)
            {
                if (sealedIndex.Length < PceFormat.TagSize + HeaderSize + FooterSize)
                {
                    throw new PceFormatException("The encrypted container's index is cut short.");
                }

                return (generation, sealedIndex);
            }
        }

        throw new IOException("The encrypted container keeps changing; try again.");
    }

    /// <summary>
    /// The entries of an index object. Its footer must name the generation the object's name stands for: an old
    /// index copied to a newer name does not pass.
    /// </summary>
    private List<PceEntry> OpenIndex(ulong generation, ReadOnlySpan<byte> sealedIndex)
    {
        var footer = sealedIndex[^FooterSize..];
        var index = sealedIndex[..^(HeaderSize + FooterSize)];
        var entries = footer[40..].SequenceEqual(PceContainerFormat.FooterMagic)
            && BinaryPrimitives.ReadInt64LittleEndian(footer) == 0
            && BinaryPrimitives.ReadInt64LittleEndian(footer[8..]) == index.Length
            && BinaryPrimitives.ReadUInt64LittleEndian(footer[16..]) == generation
                ? PceContainerFormat.OpenIndex(_header.Cipher, _master, index, footer, split: true)
                : null;
        if (entries is null)
        {
            throw new PceFormatException("The encrypted container's index is damaged or is not its own.");
        }

        Validate(entries);
        return entries;
    }

    /// <summary>The index authenticated, so this guards against our own mistakes: every extent names an object and none overlap.</summary>
    private static void Validate(List<PceEntry> entries)
    {
        var extents = new List<PceExtent>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory && entry.Extents.Count > 0)
            {
                throw new PceFormatException("Damaged index (a folder with content).");
            }

            extents.AddRange(entry.Extents);
        }

        extents.Sort(static (a, b) => a.Object != b.Object ? a.Object.Value.CompareTo(b.Object.Value) : a.Offset.CompareTo(b.Offset));
        PceExtent? previous = null;
        foreach (var extent in extents)
        {
            if (extent.Object.IsNone || extent.Length <= 0 || extent.Offset < 0 || extent.Length > long.MaxValue - extent.Offset
                || (previous is not null && previous.Object == extent.Object && extent.Offset < previous.Offset + previous.Length))
            {
                throw new PceFormatException("Damaged index (entries overlap or lie nowhere).");
            }

            previous = extent;
        }
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writable)
        {
            throw new InvalidOperationException("The encrypted container is open for reading only.");
        }
    }

    private void Reload()
    {
        var viewed = _split.UtcNow();
        var (generation, sealedIndex) = Newest(_store);
        if (generation < _generation)
        {
            throw new PceRolledBackException(_generation, generation);
        }

        if (generation > _generation || _stale)
        {
            _entries = OpenIndex(generation, sealedIndex);
            _generation = generation;
        }

        _viewed = viewed;
        _stale = false;
    }

    /// <summary>
    /// One change: <paramref name="edit"/> turns the current entries into the new ones (null: nothing to do) and the
    /// index with them is committed as the next generation. When someone else took that generation the entries are
    /// read again and the edit applied to them, so it must not depend on anything but its argument.
    /// </summary>
    private void Commit(Func<IReadOnlyList<PceEntry>, List<PceEntry>?> edit)
    {
        var age = _split.UtcNow() - _viewed;
        if (_stale || age < TimeSpan.Zero || age >= _split.ViewLease)
        {
            Reload();
        }

        for (var attempt = 0; ; attempt++)
        {
            var viewed = _split.UtcNow();
            var before = _entries;
            if (edit(before) is not { } after)
            {
                return;
            }

            var generation = _generation + 1;
            var name = PceObjectNames.Index(generation);
            var sealedIndex = PceContainerFormat.SealIndex(_header, _master, after, 0, generation);
            bool won;
            try
            {
                won = _store.PutNew(name, sealedIndex);
            }
            catch (IOException ex)
            {
                // The request may have been carried out and only its answer lost: our bytes under the name mean it was.
                _stale = true;
                if (!Stored(name, sealedIndex))
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }

                _stale = false;
                won = true;
            }

            if (won)
            {
                _entries = after;
                _generation = generation;
                _viewed = viewed;
                Sweep(before, after);
                return;
            }

            if (attempt == CommitAttempts)
            {
                throw new IOException("The encrypted container is being changed from elsewhere; try again.");
            }

            Reload();
        }
    }

    private bool Stored(string name, ReadOnlySpan<byte> content)
    {
        try
        {
            return _store.Get(name) is { } there && there.AsSpan().SequenceEqual(content);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// After a commit: deletes the data objects the new index no longer refers to. The change is in effect, so a
    /// failure here is not the change's failure — <see cref="Clean"/> finds what is left.
    /// </summary>
    private void Sweep(List<PceEntry> before, List<PceEntry> after)
    {
        try
        {
            var kept = after.SelectMany(e => e.Extents).Select(e => e.Object).ToHashSet();
            foreach (var id in before.SelectMany(e => e.Extents).Select(e => e.Object).Distinct())
            {
                if (!kept.Contains(id))
                {
                    _store.Delete(PceObjectNames.Data(id));
                }
            }
        }
        catch (IOException)
        {
        }
    }

    private PceEntry WriteEntry(DataWriter writer, PceSource source, string path, PceCompression compression, ref long done, Action<long>? progress, CancellationToken token)
    {
        if (source.Open is null)
        {
            throw new ArgumentException($"\"{path}\" has no content to read.", nameof(source));
        }

        var key = RandomNumberGenerator.GetBytes(PceFormat.KeySize);
        using var input = source.Open();
        var buffer = new byte[Math.Max(_header.ChunkSize, 1 << 16)];
        var first = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var compress = compression switch
        {
            PceCompression.Always => true,
            PceCompression.Auto => PceContainer.SamplePays(buffer.AsSpan(0, first), _options),
            _ => false,
        };

        using var blocks = new PceBlockWriter(writer, _header.Cipher, key, _header.ChunkSize);
        long size = 0;
        Stream sink = compress ? new CompressionStream(blocks, _options.ZstdLevel, leaveOpen: true) : blocks;
        try
        {
            for (var read = first; read > 0; read = input.Read(buffer))
            {
                token.ThrowIfCancellationRequested();
                sink.Write(buffer, 0, read);
                size += read;
                done += read;
                progress?.Invoke(done);
            }
        }
        finally
        {
            if (compress)
            {
                sink.Dispose(); // the compressor's tail goes into the blocks
            }
        }

        blocks.Complete();
        return new PceEntry(path, false, size, source.ModifiedUtc.ToUniversalTime(), source.Attributes & ~FileAttributes.Directory, compress, key, writer.EndEntry());
    }

    /// <summary>
    /// Copies the live extents of <paramref name="sparse"/> objects into new packs (ciphertext as it is: blocks do
    /// not depend on where they lie) and commits the index that points there; the old objects then go like any
    /// object nothing refers to. Returns how many objects were rewritten.
    /// </summary>
    private int Repack(HashSet<PceObjectId> sparse, CancellationToken token)
    {
        // Only packs: an object holding a piece too large for a pack is a large entry's own.
        sparse.ExceptWith(_entries.SelectMany(e => e.Extents).Where(e => e.Length > _split.PackSize).Select(e => e.Object).ToList());
        if (sparse.Count == 0)
        {
            return 0;
        }

        var moved = new Dictionary<(PceObjectId Object, long Offset), PceExtent>();
        using var writer = new DataWriter(_store, _split, token);
        try
        {
            foreach (var extent in _entries.SelectMany(e => e.Extents).Where(e => sparse.Contains(e.Object)))
            {
                token.ThrowIfCancellationRequested();
                var bytes = new byte[extent.Length];
                if (_store.Read(PceObjectNames.Data(extent.Object), extent.Offset, bytes, token) != bytes.Length)
                {
                    throw new PceFormatException("The encrypted container is damaged (a part is cut short).");
                }

                moved[(extent.Object, extent.Offset)] = writer.Pack(bytes);
            }

            writer.Finish();
        }
        catch
        {
            writer.Abandon();
            throw;
        }

        // Applied to whatever the entries are by then: an entry deleted meanwhile just leaves its copy unused.
        Commit(entries =>
        {
            var any = false;
            var result = new List<PceEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (!entry.Extents.Exists(e => moved.ContainsKey((e.Object, e.Offset))))
                {
                    result.Add(entry);
                    continue;
                }

                any = true;
                var extents = entry.Extents.Select(e => moved.GetValueOrDefault((e.Object, e.Offset)) ?? e).ToList();
                result.Add(new PceEntry(entry.Path, entry.IsDirectory, entry.Size, entry.ModifiedUtc, entry.Attributes, entry.Compressed, entry.Key, extents));
            }

            return any ? result : null;
        });
        return sparse.Count;
    }

    /// <summary>
    /// Turns the sealed blocks of the entries of one change into data objects. An entry's bytes gather in a part
    /// buffer: when it fills, the entry is a large one and goes out as an upload in parts (several parts on the
    /// way at once); when the entry ends first, it is stored whole, or — below the small limit — appended to the
    /// pack shared with other small entries.
    /// </summary>
    private sealed class DataWriter(IPceObjectStore store, PceSplitOptions sizes, CancellationToken token) : IPceBlockSink, IDisposable
    {
        private readonly List<PceObjectId> _created = [];
        private readonly ConcurrentBag<byte[]> _buffers = [];
        private readonly SemaphoreSlim _slots = new(sizes.Parallelism);
        private readonly List<Task> _sends = [];
        private readonly MemoryStream _pack = new();
        private PceObjectId _packId;
        private byte[] _part = new byte[sizes.PartSize];
        private int _filled;
        private IPceObjectUpload? _upload;
        private PceObjectId _uploadId;
        private int _parts;
        private long _uploaded;
        private List<PceExtent> _extents = [];
        private Exception? _failure;

        public void Write(ReadOnlySpan<byte> sealedBlock)
        {
            while (sealedBlock.Length > 0)
            {
                if (_filled == _part.Length)
                {
                    SendPart();
                }

                var take = Math.Min(sealedBlock.Length, _part.Length - _filled);
                sealedBlock[..take].CopyTo(_part.AsSpan(_filled));
                _filled += take;
                sealedBlock = sealedBlock[take..];
            }
        }

        /// <summary>Where the bytes written since the last call ended up.</summary>
        public List<PceExtent> EndEntry()
        {
            if (_upload is not null)
            {
                if (_filled > 0)
                {
                    SendPart();
                }

                if (_upload is not null)
                {
                    CompleteUpload();
                }
            }
            else if (_filled >= sizes.SmallLimit)
            {
                var id = NewObject();
                store.Put(PceObjectNames.Data(id), _part.AsSpan(0, _filled), token);
                _extents.Add(new PceExtent(0, _filled) { Object = id });
            }
            else if (_filled > 0)
            {
                _extents.Add(Pack(_part.AsSpan(0, _filled)));
            }

            _filled = 0;
            var extents = _extents;
            _extents = [];
            return extents;
        }

        /// <summary>Appends stored bytes to the current pack, storing it first when they would not fit.</summary>
        public PceExtent Pack(ReadOnlySpan<byte> stored)
        {
            if (_pack.Length > 0 && _pack.Length + stored.Length > sizes.PackSize)
            {
                StorePack();
            }

            if (_packId.IsNone)
            {
                _packId = NewObject();
            }

            var extent = new PceExtent(_pack.Length, stored.Length) { Object = _packId };
            _pack.Write(stored);
            return extent;
        }

        /// <summary>Stores the pack still in memory. After this every extent handed out refers to a stored object.</summary>
        public void Finish() => StorePack();

        /// <summary>The change will not happen: what was stored for it is deleted, as far as the store lets us.</summary>
        public void Abandon()
        {
            WaitForSends();
            _upload?.Dispose();
            _upload = null;
            foreach (var id in _created)
            {
                try
                {
                    store.Delete(PceObjectNames.Data(id), CancellationToken.None);
                }
                catch (IOException)
                {
                    // Clean removes it later: nothing refers to it
                }
            }

            _created.Clear();
        }

        public void Dispose()
        {
            WaitForSends();
            _upload?.Dispose();
            _slots.Dispose();
            _pack.Dispose();
        }

        private PceObjectId NewObject()
        {
            var id = PceObjectId.New();
            _created.Add(id);
            return id;
        }

        private void StorePack()
        {
            if (_pack.Length > 0)
            {
                store.Put(PceObjectNames.Data(_packId), _pack.GetBuffer().AsSpan(0, (int)_pack.Length), token);
                _pack.SetLength(0);
            }

            _packId = default;
        }

        private void SendPart()
        {
            ThrowIfFailed();
            if (_upload is null)
            {
                _uploadId = NewObject();
                _upload = store.BeginUpload(PceObjectNames.Data(_uploadId), token);
                _parts = 0;
                _uploaded = 0;
            }

            var upload = _upload;
            var buffer = _part;
            var count = _filled;
            var number = ++_parts;
            _uploaded += count;
            _slots.Wait(token);
            _part = _buffers.TryTake(out var free) ? free : new byte[sizes.PartSize];
            _filled = 0;
            _sends.Add(Task.Run(
                () =>
                {
                    try
                    {
                        upload.Write(number, buffer.AsMemory(0, count), token);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref _failure, ex, null);
                    }
                    finally
                    {
                        _buffers.Add(buffer);
                        _slots.Release();
                    }
                },
                CancellationToken.None));
            if (_parts == sizes.MaxParts)
            {
                CompleteUpload(); // the object is full: the entry goes on in another
            }
        }

        private void CompleteUpload()
        {
            WaitForSends();
            ThrowIfFailed();
            _upload!.Complete(token);
            _upload.Dispose();
            _upload = null;
            _extents.Add(new PceExtent(0, _uploaded) { Object = _uploadId });
        }

        private void WaitForSends()
        {
            Task.WaitAll([.. _sends], CancellationToken.None); // the tasks catch what they throw
            _sends.Clear();
        }

        private void ThrowIfFailed()
        {
            if (_failure is { } failure)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    /// <summary>
    /// An entry's extents in data objects, read by range. Reading on from where the last request ended fetches
    /// more each time (up to the read-ahead limit), so a whole file costs few requests; a jump fetches only what
    /// was asked for, so a look at the end of a large file costs one small one.
    /// </summary>
    private sealed class ObjectExtents : IPceStoredBytes
    {
        private const int FirstAhead = 1 << 16;
        private readonly IPceObjectStore _store;
        private readonly int _readAhead;
        private readonly string[] _names;
        private readonly long[] _offsets;
        private readonly long[] _starts; // position of each extent in the entry's stored stream
        private byte[] _window = [];
        private long _windowStart;
        private int _windowLength;
        private int _ahead;

        public ObjectExtents(IPceObjectStore store, PceEntry entry, int readAhead)
        {
            _store = store;
            _readAhead = readAhead;
            _names = new string[entry.Extents.Count];
            _offsets = new long[entry.Extents.Count];
            _starts = new long[entry.Extents.Count + 1];
            for (var i = 0; i < entry.Extents.Count; i++)
            {
                _names[i] = PceObjectNames.Data(entry.Extents[i].Object);
                _offsets[i] = entry.Extents[i].Offset;
                _starts[i + 1] = _starts[i] + entry.Extents[i].Length;
            }
        }

        public void Read(long start, Span<byte> target)
        {
            while (target.Length > 0)
            {
                if (start >= _windowStart && start < _windowStart + _windowLength)
                {
                    var from = (int)(start - _windowStart);
                    var take = Math.Min(target.Length, _windowLength - from);
                    _window.AsSpan(from, take).CopyTo(target);
                    target = target[take..];
                    start += take;
                    continue;
                }

                if (start >= _starts[^1])
                {
                    throw new PceFormatException("The encrypted container is cut short.");
                }

                var extent = Array.BinarySearch(_starts, 0, _names.Length, start);
                if (extent < 0)
                {
                    extent = ~extent - 1;
                }

                while (_starts[extent + 1] <= start)
                {
                    extent++;
                }

                _ahead = start == _windowStart + _windowLength ? Math.Min(Math.Max(_ahead * 2, FirstAhead), _readAhead) : 0;
                var want = (int)Math.Min(Math.Max(target.Length, _ahead), _starts[extent + 1] - start);
                if (_window.Length < want)
                {
                    _window = new byte[want];
                }

                _windowLength = 0;
                if (_store.Read(_names[extent], _offsets[extent] + (start - _starts[extent]), _window.AsSpan(0, want)) != want)
                {
                    throw new PceFormatException("The encrypted container is damaged (a part is cut short).");
                }

                _windowStart = start;
                _windowLength = want;
            }
        }

        public void Dispose()
        {
        }
    }
}
