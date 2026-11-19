using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Compressors.ZStandard;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// A .pce file in the container layout (<see cref="PceContainerFormat"/>): lists entries without reading their
/// content, reads one entry, and adds, deletes and renames entries in place. After every finished change the
/// file is dense: header, entry data, index, footer, with nothing unused between them, so deleting an entry
/// shrinks the file by what it took.
/// <para>
/// A change never overwrites bytes the last committed index refers to. It goes: (1) append a new index after
/// everything else and flush — the change is now in effect; (2) fill the holes in the data area with bytes taken
/// from its end, then append the index describing the moved bytes; (3) write the index right after the data and
/// cut the file there. The reader takes the footer from the end of the file (or, after an interrupted append,
/// the last one that authenticates), so the file opens after a crash at any point; what a crash leaves behind is
/// unused space, which the next change removes.
/// </para>
/// </summary>
public sealed class PceContainer : IDisposable
{
    private const int HeaderSize = PceContainerFormat.HeaderSize;
    private const int FooterSize = PceContainerFormat.FooterSize;
    private const int CopyBuffer = 1 << 20;

    private readonly Lock _gate = new();
    private readonly IPceStorage _storage;
    private readonly bool _writable;
    private readonly byte[] _master;
    private readonly PceOptions _options;
    private PceContainerHeader _header;
    private List<PceEntry> _entries;
    private long _indexOffset;
    private long _indexSize; // sealed index with the header copy and the footer
    private ulong _generation;
    private int _version;
    private bool _broken;
    private bool _disposed;

    private PceContainer(IPceStorage storage, bool writable, PceContainerHeader header, byte[] master, List<PceEntry> entries, PceOptions options)
    {
        _storage = storage;
        _writable = writable;
        _header = header;
        _master = master;
        _entries = entries;
        _options = options;
    }

    /// <summary>The entries as of the last change. The list is replaced, not edited, when the container changes.</summary>
    public IReadOnlyList<PceEntry> Entries => _entries;

    public bool IsWritable => _writable;

    internal PceContainerHeader Header => _header;

    /// <summary>Raised by every change, so streams opened before it know their bytes may have moved.</summary>
    internal int Version => Volatile.Read(ref _version);

    /// <summary>Unused bytes left by an interrupted change (zero for a file every change of which finished).</summary>
    internal long Slack
    {
        get
        {
            long used = 0;
            foreach (var entry in _entries)
            {
                used += entry.StoredSize;
            }

            return _storage.Length - HeaderSize - used - _indexSize;
        }
    }

    /// <summary>1 or 2 for the two .pce layouts, 0 when the file does not start with either magic.</summary>
    public static int LayoutVersion(string file)
    {
        Span<byte> magic = stackalloc byte[8];
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        if (input.ReadAtLeast(magic[..4], 4, throwOnEndOfStream: false) == 4)
        {
            if (magic[..4].SequenceEqual(PceContainerFormat.Magic))
            {
                return 2;
            }

            if (magic[..4].SequenceEqual("PCE1"u8))
            {
                return 1;
            }
        }

        // A container whose first header was damaged still ends with its footer (and carries a second header before it).
        if (input.Length >= 2 * HeaderSize + FooterSize)
        {
            input.Position = input.Length - magic.Length;
            if (input.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length && magic.SequenceEqual(PceContainerFormat.FooterMagic))
            {
                return 2;
            }
        }

        return 0;
    }

    /// <summary>A new, empty container at <paramref name="file"/> (created or truncated), open for changes.</summary>
    public static PceContainer Create(string file, string password, PceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var storage = new FilePceStorage(file, FileMode.Create, writable: true);
        try
        {
            return Create(storage, password, options);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    internal static PceContainer Create(IPceStorage storage, string password, PceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        options ??= PceOptions.Default;
        var master = RandomNumberGenerator.GetBytes(PceFormat.KeySize);
        var header = PceContainerFormat.NewHeader(options, password, master);
        var container = new PceContainer(storage, true, header, master, [], options);
        storage.SetLength(0);
        storage.Write(PceContainerFormat.WriteHeader(header), 0);
        container.WriteIndex(HeaderSize);
        storage.Flush();
        return container;
    }

    /// <summary>
    /// Opens a container: <see cref="PceWrongPasswordException"/> for a wrong password, <see cref="PceFormatException"/>
    /// for a file that is not one or is damaged. Only the header, the footer and the index are read.
    /// </summary>
    public static PceContainer Open(string file, string password, bool writable = false, PceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var storage = new FilePceStorage(file, FileMode.Open, writable);
        try
        {
            return Open(storage, password, writable, Path.GetFileName(file), options);
        }
        catch
        {
            storage.Dispose();
            throw;
        }
    }

    internal static PceContainer Open(IPceStorage storage, string password, bool writable = false, string? fileName = null, PceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var headerBytes = new byte[HeaderSize];
        if (storage.Read(headerBytes, 0) != HeaderSize)
        {
            throw new PceFormatException("Not a Plain Commander encrypted file (too short).");
        }

        // The header at the start decides. When it does not open the file — damaged, or the password is not the
        // one it was written for — the copy before the footer gets the same chance, unless it is the same bytes.
        PceContainerHeader? header = null;
        PceFormatException? invalid = null;
        byte[]? master = null;
        try
        {
            header = PceContainerFormat.ReadHeader(headerBytes);
            master = PceContainerFormat.Unwrap(header, password);
        }
        catch (PceFormatException ex)
        {
            invalid = ex; // not a header, a newer version's, or key derivation parameters out of range
        }

        var fromCopy = false;
        if (master is null && ReadHeaderCopy(storage, headerBytes) is { } copy)
        {
            try
            {
                master = PceContainerFormat.Unwrap(copy, password);
            }
            catch (PceFormatException)
            {
                // the copy is damaged too: what the first header said stands
            }

            if (master is not null)
            {
                header = copy;
                fromCopy = true;
            }
        }

        if (master is null || header is null)
        {
            throw invalid ?? (Exception)new PceWrongPasswordException(fileName ?? "file");
        }

        try
        {
            var container = new PceContainer(storage, writable, header, master, [], options ?? PceOptions.Default);
            container.LoadIndex();
            if (fromCopy && writable)
            {
                storage.Write(PceContainerFormat.WriteHeader(header), 0);
                storage.Flush();
            }

            return container;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(master);
            throw;
        }
    }

    /// <summary>The header copy before the footer at the end of the file; null when there is none or it equals <paramref name="first"/>.</summary>
    private static PceContainerHeader? ReadHeaderCopy(IPceStorage storage, ReadOnlySpan<byte> first)
    {
        var length = storage.Length;
        Span<byte> tail = stackalloc byte[HeaderSize + FooterSize];
        if (length < 2 * HeaderSize + FooterSize || storage.Read(tail, length - tail.Length) != tail.Length
            || !tail[^8..].SequenceEqual(PceContainerFormat.FooterMagic) || tail[..HeaderSize].SequenceEqual(first))
        {
            return null;
        }

        try
        {
            return PceContainerFormat.ReadHeader(tail[..HeaderSize]);
        }
        catch (PceFormatException)
        {
            return null;
        }
    }

    /// <summary>The entry at <paramref name="path"/> (case-insensitive), or null.</summary>
    public PceEntry? Find(string path)
    {
        var normalized = NormalizePath(path);
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
    /// The entry's content. Seekable unless the entry is compressed. Damage surfaces as <see cref="PceFormatException"/>
    /// while reading; a change to the container makes the stream fail rather than read moved bytes.
    /// </summary>
    public Stream OpenRead(PceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            EnsureUsable();
            if (entry.IsDirectory)
            {
                throw new ArgumentException("A folder has no content.", nameof(entry));
            }

            var blocks = new PceBlockReader(this, _storage, _version, _header.Cipher, entry, _header.ChunkSize);
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
    /// were written: a failure or a cancellation leaves the container as it was.
    /// </summary>
    /// <param name="progress">Called with the plain bytes read so far.</param>
    public void Add(IEnumerable<PceSource> sources, PceCompression compression = PceCompression.None, Action<long>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            EnsureWritable();

            // An empty container has nothing to protect, so its data starts where its (empty) index lies and the
            // file comes out in one piece; otherwise new data goes after everything, the index included.
            var fresh = _entries.Count == 0 && _indexOffset == HeaderSize;
            var start = fresh ? HeaderSize : _storage.Length;
            var position = start;
            var added = new List<PceEntry>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long done = 0;
            try
            {
                foreach (var source in sources)
                {
                    token.ThrowIfCancellationRequested();
                    var path = NormalizePath(source.Path);
                    if (!taken.Add(path))
                    {
                        throw new ArgumentException($"\"{path}\" is given twice.", nameof(sources));
                    }

                    added.Add(source.IsDirectory
                        ? new PceEntry(path, true, 0, source.ModifiedUtc.ToUniversalTime(), source.Attributes & ~FileAttributes.Directory, false, new byte[PceFormat.KeySize], [])
                        : WriteEntry(source, path, compression, ref position, ref done, progress, token));
                }

                token.ThrowIfCancellationRequested(); // a cancel during the last block still cancels
            }
            catch (Exception ex)
            {
                Undo(fresh, start, ex);
                throw;
            }

            if (added.Count == 0)
            {
                return;
            }

            Change(() =>
            {
                if (fresh)
                {
                    _storage.SetLength(position);
                }

                var entries = _entries.Where(e => !taken.Contains(e.Path)).ToList();
                entries.AddRange(added);
                _entries = entries;
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Deletes the entries at <paramref name="paths"/> and everything under them, and shrinks the file by what they
    /// took. Returns how many entries went. Cancelling stops the moving of data early: the entries are gone either
    /// way, and the space not yet reclaimed is reclaimed by the next change.
    /// </summary>
    public int Delete(IEnumerable<string> paths, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            EnsureWritable();
            var roots = paths.Select(NormalizePath).ToList();
            var keep = _entries.Where(e => !roots.Exists(r => IsAtOrUnder(e.Path, r))).ToList();
            var removed = _entries.Count - keep.Count;
            if (removed > 0)
            {
                token.ThrowIfCancellationRequested();
                Change(() => _entries = keep, token);
            }

            return removed;
        }
    }

    /// <summary>Renames (or moves) an entry and everything under it. Only the index is rewritten.</summary>
    public void Rename(string path, string newPath)
    {
        lock (_gate)
        {
            EnsureWritable();
            var from = NormalizePath(path);
            var to = NormalizePath(newPath);
            if (IsAtOrUnder(to, from) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"\"{from}\" cannot be moved into itself.");
            }

            var moved = _entries.Where(e => IsAtOrUnder(e.Path, from)).ToList();
            if (moved.Count == 0)
            {
                throw new FileNotFoundException($"\"{from}\" is not in the encrypted file.");
            }

            var renamed = moved.ToDictionary(e => e, e => to + e.Path[from.Length..]);
            var others = new HashSet<string>(_entries.Where(e => !renamed.ContainsKey(e)).Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var name in renamed.Values)
            {
                if (others.Contains(name) || Encoding.UTF8.GetByteCount(name) > ushort.MaxValue)
                {
                    throw new IOException($"\"{name}\" already exists in the encrypted file or is too long.");
                }
            }

            Change(() =>
            {
                // Entries are shared with whoever holds the old list, so the renamed ones are new objects.
                _entries = _entries
                    .Select(e => renamed.TryGetValue(e, out var name) ? new PceEntry(name, e.IsDirectory, e.Size, e.ModifiedUtc, e.Attributes, e.Compressed, e.Key, e.Extents) : e)
                    .ToList();
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Replaces the password: the master key is wrapped again under a key from the new password and a new salt.
    /// The new header first goes out as the copy before the footer (with a rewritten index, like any change), and
    /// only when that is on disk is the header at the start overwritten. A crash in between leaves a file that
    /// opens with the old password (first header) and with the new one (the copy); a first header torn by the
    /// crash is replaced from the copy the next time the file is opened for changes. The content is not touched.
    /// </summary>
    public void ChangePassword(string newPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        lock (_gate)
        {
            EnsureWritable();
            _header = PceContainerFormat.Wrap(_header, newPassword, _master);
            Change(static () => { }, CancellationToken.None);
            Guarded(() =>
            {
                _storage.Write(PceContainerFormat.WriteHeader(_header), 0);
                _storage.Flush();
            });
        }
    }

    /// <summary>Removes what an interrupted change left behind (unused space, a trailing unfinished write).</summary>
    public void Repair(CancellationToken token = default)
    {
        lock (_gate)
        {
            EnsureWritable();
            if (Slack != 0)
            {
                Change(static () => { }, token);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _version);
            CryptographicOperations.ZeroMemory(_master);
            _storage.Dispose();
        }
    }

    /// <summary>"a/b" for "a\b\" and the like; refuses names that are not plain relative paths.</summary>
    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(normalized) > ushort.MaxValue)
        {
            throw new ArgumentException($"\"{path.Replace('\0', '?')}\" is not a valid entry path.", nameof(path));
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException($"\"{path}\" is not a valid entry path.", nameof(path));
            }
        }

        return normalized;
    }

    private static bool IsAtOrUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == '/');

    private PceEntry WriteEntry(PceSource source, string path, PceCompression compression, ref long position, ref long done, Action<long>? progress, CancellationToken token)
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
            PceCompression.Auto => SamplePays(buffer.AsSpan(0, first)),
            _ => false,
        };

        using var blocks = new PceBlockWriter(_storage, position, _header.Cipher, key, _header.ChunkSize);
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
        var extents = blocks.Stored > 0 ? new List<PceExtent> { new(position, blocks.Stored) } : [];
        position += blocks.Stored;
        return new PceEntry(path, false, size, source.ModifiedUtc.ToUniversalTime(), source.Attributes & ~FileAttributes.Directory, compress, key, extents);
    }

    /// <summary>Auto mode: the first block must shrink below the threshold. Media, archives and encrypted data stay as they are.</summary>
    private bool SamplePays(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 256)
        {
            return false;
        }

        using var compressed = new MemoryStream(sample.Length);
        using (var zstd = new CompressionStream(compressed, _options.ZstdLevel, leaveOpen: true))
        {
            zstd.Write(sample);
        }

        return compressed.Length < sample.Length * _options.AutoThreshold;
    }

    /// <summary>Entries could not be written: the file goes back to what the committed index describes.</summary>
    private void Undo(bool fresh, long start, Exception cause)
    {
        try
        {
            if (fresh)
            {
                WriteIndex(HeaderSize);
                _storage.SetLength(HeaderSize + _indexSize);
            }
            else
            {
                _storage.SetLength(start);
            }

            _storage.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cause is what the user needs to see. A container with entries still opens (the bytes after its
            // footer are skipped); this object cannot be trusted with further changes.
            _broken = true;
            _ = cause;
        }
    }

    /// <summary>
    /// One change from start to finish: <paramref name="apply"/> edits the entry list, then the three steps of the
    /// class remarks. <paramref name="token"/> only cuts the moving of data short.
    /// </summary>
    private void Change(Action apply, CancellationToken token)
    {
        Guarded(() =>
        {
            apply();
            Interlocked.Increment(ref _version);
            Commit();
            if (Compact(token))
            {
                Commit();
            }

            Settle();
        });
        token.ThrowIfCancellationRequested();
    }

    /// <summary>A failed write leaves this object's picture of the file unreliable: it refuses further work.</summary>
    private void Guarded(Action write)
    {
        try
        {
            write();
        }
        catch
        {
            _broken = true;
            throw;
        }
    }

    /// <summary>Appends the index after everything in the file. Data first reaches the disk, then the index that refers to it.</summary>
    private void Commit()
    {
        _storage.Flush();
        WriteIndex(_storage.Length);
        _storage.Flush();
    }

    private void WriteIndex(long at)
    {
        var sealedIndex = PceContainerFormat.SealIndex(_header, _master, _entries, at, ++_generation);
        _storage.Write(sealedIndex, at);
        _indexOffset = at;
        _indexSize = sealedIndex.Length;
    }

    /// <summary>
    /// Fills the holes in the data area, lowest first, with bytes from the end of the last extent. A destination is
    /// always unused in the committed index and a source is never written to, so the committed index stays true
    /// until the next one is appended. Returns whether anything moved.
    /// </summary>
    private bool Compact(CancellationToken token)
    {
        var runs = new List<(PceExtent Extent, List<PceExtent> Moved)>();
        foreach (var entry in _entries)
        {
            foreach (var extent in entry.Extents)
            {
                runs.Add((extent, []));
            }
        }

        runs.Sort(static (a, b) => a.Extent.Offset.CompareTo(b.Extent.Offset));
        long cursor = HeaderSize;
        var low = 0;
        var high = runs.Count - 1;
        var moved = false;
        byte[]? buffer = null;
        while (low <= high && !token.IsCancellationRequested)
        {
            var front = runs[low].Extent;
            if (front.Offset == cursor)
            {
                cursor += front.Length;
                low++;
                continue;
            }

            var (tail, pieces) = runs[high];
            var count = Math.Min(front.Offset - cursor, tail.Length);
            buffer ??= new byte[CopyBuffer];
            Copy(tail.Offset + tail.Length - count, cursor, count, buffer);
            tail.Length -= count;
            pieces.Add(new PceExtent(cursor, count));
            cursor += count;
            moved = true;
            if (tail.Length == 0)
            {
                high--;
            }
        }

        if (!moved)
        {
            return false;
        }

        // A piece moved later lies earlier in its extent, so the pieces of one extent follow it in reverse.
        var piecesOf = runs.Where(r => r.Moved.Count > 0).ToDictionary(r => r.Extent, r => r.Moved);
        foreach (var entry in _entries)
        {
            if (!entry.Extents.Exists(piecesOf.ContainsKey))
            {
                continue;
            }

            var extents = new List<PceExtent>();
            foreach (var extent in entry.Extents)
            {
                Append(extents, extent);
                if (piecesOf.TryGetValue(extent, out var pieces))
                {
                    for (var i = pieces.Count - 1; i >= 0; i--)
                    {
                        Append(extents, pieces[i]);
                    }
                }
            }

            entry.Extents = extents;
        }

        return true;

        static void Append(List<PceExtent> extents, PceExtent extent)
        {
            if (extent.Length == 0)
            {
                return;
            }

            if (extents.Count > 0 && extents[^1].Offset + extents[^1].Length == extent.Offset)
            {
                extents[^1] = new PceExtent(extents[^1].Offset, extents[^1].Length + extent.Length);
            }
            else
            {
                extents.Add(extent);
            }
        }
    }

    private void Copy(long from, long to, long count, byte[] buffer)
    {
        while (count > 0)
        {
            var take = (int)Math.Min(buffer.Length, count);
            if (_storage.Read(buffer.AsSpan(0, take), from) != take)
            {
                throw new PceFormatException("The encrypted file is cut short.");
            }

            _storage.Write(buffer.AsSpan(0, take), to);
            from += take;
            to += take;
            count -= take;
        }
    }

    /// <summary>
    /// Puts the index right after the data and cuts the file there. The copy is written into unused space while the
    /// committed index still ends the file; when the space between the data and that index is too small for it,
    /// one more index is appended first, which makes the space large enough.
    /// </summary>
    private void Settle()
    {
        while (true)
        {
            var dataEnd = DataEnd();
            if (_indexOffset == dataEnd)
            {
                if (_storage.Length != dataEnd + _indexSize)
                {
                    _storage.SetLength(dataEnd + _indexSize);
                    _storage.Flush();
                }

                return;
            }

            var size = PceContainerFormat.SealedIndexSize(_entries);
            if (dataEnd + size <= _indexOffset)
            {
                WriteIndex(dataEnd);
                _storage.Flush();
                _storage.SetLength(dataEnd + _indexSize);
                _storage.Flush();
                return;
            }

            Commit();
        }
    }

    private long DataEnd()
    {
        long end = HeaderSize;
        foreach (var entry in _entries)
        {
            foreach (var extent in entry.Extents)
            {
                end = Math.Max(end, extent.Offset + extent.Length);
            }
        }

        return end;
    }

    /// <summary>
    /// Finds the committed index: the footer at the end of the file, or — when a write was interrupted and the end
    /// is not a footer — the last footer in the file whose index authenticates and ends where that footer starts.
    /// </summary>
    private void LoadIndex()
    {
        var length = _storage.Length;
        var footerAt = length - FooterSize;
        if (footerAt < 2 * HeaderSize || !TryLoadIndex(footerAt))
        {
            footerAt = FindEarlierFooter(length);
            if (footerAt < 0)
            {
                throw new PceFormatException("The encrypted file is cut short or damaged (no valid index).");
            }

            if (_writable)
            {
                _storage.SetLength(footerAt + FooterSize);
                _storage.Flush();
            }
        }
    }

    private long FindEarlierFooter(long length)
    {
        var magic = PceContainerFormat.FooterMagic;
        var buffer = new byte[CopyBuffer + magic.Length];
        var end = length - 1; // a footer ending exactly at the end was already tried
        while (end > HeaderSize)
        {
            var start = Math.Max(HeaderSize, end - CopyBuffer);
            var span = buffer.AsSpan(0, (int)Math.Min(end - start + magic.Length - 1, length - start));
            if (_storage.Read(span, start) != span.Length)
            {
                return -1;
            }

            for (var at = span.LastIndexOf(magic); at >= 0; at = span[..(at + magic.Length - 1)].LastIndexOf(magic))
            {
                var footerAt = start + at + magic.Length - FooterSize;
                if (footerAt >= 2 * HeaderSize && TryLoadIndex(footerAt))
                {
                    return footerAt;
                }
            }

            end = start;
        }

        return -1;
    }

    private bool TryLoadIndex(long footerAt)
    {
        Span<byte> footer = stackalloc byte[FooterSize];
        if (_storage.Read(footer, footerAt) != FooterSize || !footer[40..].SequenceEqual(PceContainerFormat.FooterMagic))
        {
            return false;
        }

        var offset = BinaryPrimitives.ReadInt64LittleEndian(footer);
        var size = BinaryPrimitives.ReadInt64LittleEndian(footer[8..]);
        if (offset < HeaderSize || size < PceFormat.TagSize || size > PceContainerFormat.MaxIndexSize || offset + size + HeaderSize != footerAt)
        {
            return false;
        }

        var sealedIndex = new byte[size];
        if (_storage.Read(sealedIndex, offset) != size)
        {
            return false;
        }

        var entries = PceContainerFormat.OpenIndex(_header.Cipher, _master, sealedIndex, footer);
        if (entries is null)
        {
            return false;
        }

        Validate(entries, offset);
        _entries = entries;
        _indexOffset = offset;
        _indexSize = size + HeaderSize + FooterSize;
        _generation = BinaryPrimitives.ReadUInt64LittleEndian(footer[16..]);
        return true;
    }

    /// <summary>The index authenticated, so this guards against our own mistakes: extents must lie in the data area and not overlap.</summary>
    private static void Validate(List<PceEntry> entries, long indexOffset)
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

        extents.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        long cursor = HeaderSize;
        foreach (var extent in extents)
        {
            if (extent.Length <= 0 || extent.Offset < cursor || extent.Length > indexOffset - extent.Offset)
            {
                throw new PceFormatException("Damaged index (entries overlap or lie outside the data).");
            }

            cursor = extent.Offset + extent.Length;
        }
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_broken)
        {
            throw new IOException("An earlier write to the encrypted file failed; open it again.");
        }
    }

    private void EnsureWritable()
    {
        EnsureUsable();
        if (!_writable)
        {
            throw new InvalidOperationException("The encrypted file is open for reading only.");
        }
    }
}
