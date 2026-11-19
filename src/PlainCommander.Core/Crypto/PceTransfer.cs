using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// What a .pce container can do, whichever way it is kept: one file changed in place (<see cref="PceContainer"/>)
/// or separate objects in a store like S3 (<see cref="PceObjectContainer"/>).
/// </summary>
public interface IPceContainer : IDisposable
{
    /// <summary>The entries as of the last change. The list is replaced, not edited, when the container changes.</summary>
    IReadOnlyList<PceEntry> Entries { get; }

    bool IsWritable { get; }

    /// <summary>The entry at <paramref name="path"/> (case-insensitive), or null.</summary>
    PceEntry? Find(string path);

    /// <summary>The entry's content; seekable unless the entry is compressed.</summary>
    Stream OpenRead(PceEntry entry);

    /// <summary>Adds entries; one whose path is taken replaces the old entry. All of them or none.</summary>
    void Add(IEnumerable<PceSource> sources, PceCompression compression = PceCompression.None, Action<long>? progress = null, CancellationToken token = default);

    /// <summary>Deletes the entries at <paramref name="paths"/> and everything under them; returns how many went.</summary>
    int Delete(IEnumerable<string> paths, CancellationToken token = default);

    /// <summary>Renames (or moves) an entry and everything under it.</summary>
    void Rename(string path, string newPath);

    void ChangePassword(string newPassword);
}

/// <summary>An entry as it is stored — its sealed blocks and the key they were sealed with — to be carried to another container.</summary>
/// <param name="Open">Opens the stored bytes (<see cref="PceEntry.StoredSize"/> of them); not called for a folder or an empty file.</param>
internal sealed record PceStored(PceEntry Entry, Func<IPceStoredBytes> Open);

/// <summary>
/// Carries a container between its two forms. Entry streams are the same bytes in both, so nothing is decrypted or
/// encrypted again: the sealed blocks are copied and the index written anew, with the entries' own keys. The copy
/// gets a new master key under the same password. A file of the first layout has no such streams and is encrypted
/// anew on the way.
/// </summary>
internal static class PceTransfer
{
    private const int CopyBuffer = 1 << 20;

    /// <summary>
    /// The .pce file (either layout) as a container of objects in <paramref name="store"/>, which must be empty.
    /// The file is not changed. A failure leaves the store empty again.
    /// </summary>
    /// <param name="progress">Called with the bytes carried over so far (stored bytes; plain ones for the first layout).</param>
    public static void ToObjects(string file, string password, IPceObjectStore store, PceOptions? options = null, PceSplitOptions? split = null, Action<long>? progress = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(store);
        var created = false;
        try
        {
            if (PceContainer.LayoutVersion(file) == 2)
            {
                using var source = PceContainer.Open(file, password);
                using var target = PceObjectContainer.Create(store, password, Matching(options, source.Header), split);
                created = true;
                Copy(source.Entries, source.OpenStored, target.AddStored, progress, token);
            }
            else
            {
                PceFile.CopyFirstLayout(
                    file,
                    password,
                    () =>
                    {
                        var target = PceObjectContainer.Create(store, password, options, split);
                        created = true;
                        return target;
                    },
                    progress,
                    token);
            }
        }
        catch when (created)
        {
            Wipe(store);
            throw;
        }
    }

    /// <summary>
    /// The container of objects as one .pce file, written beside <paramref name="file"/> and moved into place when
    /// complete (an existing file is replaced only then). The objects are not changed.
    /// </summary>
    /// <param name="progress">Called with the stored bytes carried over so far.</param>
    public static void ToFile(IPceObjectStore store, string password, string file, PceOptions? options = null, PceSplitOptions? split = null, Action<long>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(file);
        using var source = PceObjectContainer.Open(store, password, writable: false, Path.GetFileName(file), options, split);
        var temp = TempNames.ReserveFile(file);
        try
        {
            using (var target = PceContainer.Create(temp, password, Matching(options, source.Header)))
            {
                Copy(source.Entries, source.OpenStored, target.AddStored, progress, token);
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>Copies the stored bytes of <paramref name="source"/> to <paramref name="target"/> at their positions in the entry's stream.</summary>
    internal static void Pump(IPceStoredBytes source, long length, Action<ReadOnlySpan<byte>> target, ref long done, Action<long>? progress, CancellationToken token)
    {
        var buffer = new byte[(int)Math.Min(CopyBuffer, Math.Max(length, 1))];
        for (long at = 0; at < length;)
        {
            token.ThrowIfCancellationRequested();
            var take = (int)Math.Min(buffer.Length, length - at);
            source.Read(at, buffer.AsSpan(0, take));
            target(buffer.AsSpan(0, take));
            at += take;
            done += take;
            progress?.Invoke(done);
        }
    }

    /// <summary>Blocks are sealed for one cipher and one block size, so the copy must be a container of the same two.</summary>
    private static PceOptions Matching(PceOptions? options, PceContainerHeader header) =>
        (options ?? PceOptions.Default) with { Cipher = header.Cipher, ChunkSize = header.ChunkSize };

    private static void Copy(
        IReadOnlyList<PceEntry> entries,
        Func<PceEntry, IPceStoredBytes> open,
        Action<IEnumerable<PceStored>, Action<long>?, CancellationToken> add,
        Action<long>? progress,
        CancellationToken token)
    {
        if (entries.Count > 0)
        {
            add(entries.Select(e => new PceStored(e, () => open(e))), progress, token);
        }
    }

    /// <summary>The store was empty before: everything in it is the unfinished copy.</summary>
    private static void Wipe(IPceObjectStore store)
    {
        try
        {
            store.DiscardUploads(DateTime.MaxValue);
            foreach (var item in store.List(string.Empty))
            {
                store.Delete(item.Name);
            }
        }
        catch (IOException)
        {
            // what stays is a container without an index (or with an empty one): nothing opens it as the copy
        }
    }
}
