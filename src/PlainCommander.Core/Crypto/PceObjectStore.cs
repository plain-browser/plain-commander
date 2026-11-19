using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// Where a .pce container kept as separate objects lives: a flat set of named, immutable objects, as in S3. An
/// object appears whole or not at all, cannot be changed in part, and can be read by byte range. The container's
/// safety rests on the order of requests (data first, then the index that refers to it, deletions last), so tests
/// swap in a store that fails at a chosen request. Design: docs/szyfrowanie.md, ".pce na S3".
/// </summary>
internal interface IPceObjectStore
{
    /// <summary>
    /// Whether <see cref="PutNew"/> is atomic on the server. Without it two writers can both believe they committed
    /// the same generation, so the container must be changed from one place at a time.
    /// </summary>
    bool ConditionalPut { get; }

    /// <summary>The whole object, or null when there is none. For the small ones: the header and an index.</summary>
    byte[]? Get(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads up to the buffer's size at <paramref name="offset"/>; fewer bytes only at the end of the object.
    /// Throws <see cref="PceObjectMissingException"/> when the object is gone.
    /// </summary>
    int Read(string name, long offset, Span<byte> buffer, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the object in one step.</summary>
    void Put(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Creates the object unless one with this name exists; false when it does. This is how an index is committed.</summary>
    bool PutNew(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Starts a large object sent in parts; nothing is visible under the name until it is completed.</summary>
    IPceObjectUpload BeginUpload(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards uploads in parts begun before <paramref name="startedBeforeUtc"/> and never completed (their
    /// writer died, or the answer to its request was lost). Returns how many.
    /// </summary>
    int DiscardUploads(DateTime startedBeforeUtc, CancellationToken cancellationToken = default);

    /// <summary>Removes the object; one that is not there is not an error.</summary>
    void Delete(string name, CancellationToken cancellationToken = default);

    /// <summary>Objects whose names start with <paramref name="prefix"/>, in ordinal order of the names, at most <paramref name="max"/>.</summary>
    IReadOnlyList<PceObjectInfo> List(string prefix, int max = int.MaxValue, CancellationToken cancellationToken = default);
}

/// <summary>An object being sent in parts. Disposing it before <see cref="Complete"/> discards what was sent.</summary>
internal interface IPceObjectUpload : IDisposable
{
    /// <summary>Sends part <paramref name="number"/> (from 1; the object is the parts in number order). Parts may be sent from several threads at once.</summary>
    void Write(int number, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Makes the object appear under its name.</summary>
    void Complete(CancellationToken cancellationToken = default);
}

/// <param name="ModifiedUtc">When the object was stored; cleaning up leaves young objects alone, as another writer may be about to commit them.</param>
internal readonly record struct PceObjectInfo(string Name, long Size, DateTime ModifiedUtc);

/// <summary>A read reached for an object that is not there: the container changed under the reader, or is damaged.</summary>
public sealed class PceObjectMissingException(string name) : IOException($"The encrypted container changed or is damaged (part \"{name}\" is missing).")
{
    public string ObjectName { get; } = name;
}

/// <summary>The name of a data object: 128 random bits, so the store's listing says nothing about what is inside.</summary>
internal readonly record struct PceObjectId(UInt128 Value)
{
    public const int Size = 16;

    public bool IsNone => Value == UInt128.Zero;

    public static PceObjectId New()
    {
        Span<byte> bytes = stackalloc byte[Size];
        PceObjectId id;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            id = Read(bytes);
        }
        while (id.IsNone);
        return id;
    }

    public static PceObjectId Read(ReadOnlySpan<byte> buffer) => new(BinaryPrimitives.ReadUInt128BigEndian(buffer));

    public void Write(Span<byte> buffer) => BinaryPrimitives.WriteUInt128BigEndian(buffer, Value);

    public static bool TryParse(ReadOnlySpan<char> text, out PceObjectId id)
    {
        id = default;
        if (!PceObjectNames.IsLowerHex(text, 2 * Size) || !UInt128.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        id = new PceObjectId(value);
        return !id.IsNone;
    }

    public override string ToString() => Value.ToString("x32", CultureInfo.InvariantCulture);
}

/// <summary>
/// The objects of one container, relative to its prefix ("name.pce/" in a bucket):
/// <code>
/// head                    the 94 B header with <see cref="PceLayout.Split"/>; replaced only by a password change
/// index/&lt;16 hex digits&gt;   a sealed index (index, header copy, footer), one per generation, never changed
/// data/&lt;32 hex digits&gt;    the stream of one large entry, or a pack of the streams of small ones, never changed
/// </code>
/// An index is named by 2^64 − 1 − generation, so the newest sorts first and one listing of a single key finds it.
/// </summary>
internal static class PceObjectNames
{
    public const string Head = "head";
    public const string IndexPrefix = "index/";
    public const string DataPrefix = "data/";

    public static string Index(ulong generation) => IndexPrefix + (ulong.MaxValue - generation).ToString("x16", CultureInfo.InvariantCulture);

    public static string Data(PceObjectId id) => DataPrefix + id;

    /// <summary>The generation an index object's name stands for; false for any other name.</summary>
    public static bool TryGeneration(string name, out ulong generation)
    {
        generation = 0;
        if (!name.StartsWith(IndexPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = name.AsSpan(IndexPrefix.Length);
        if (!IsLowerHex(digits, 16) || !ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var inverted))
        {
            return false;
        }

        generation = ulong.MaxValue - inverted;
        return true;
    }

    /// <summary>The id in a data object's name; false for any other name.</summary>
    public static bool TryData(string name, out PceObjectId id)
    {
        id = default;
        return name.StartsWith(DataPrefix, StringComparison.Ordinal) && PceObjectId.TryParse(name.AsSpan(DataPrefix.Length), out id);
    }

    internal static bool IsLowerHex(ReadOnlySpan<char> text, int length)
    {
        if (text.Length != length)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Objects as files under a folder ('/' in a name is a subfolder). The same layout on a disk: for tests against a
/// real file system and for a container in a synchronised folder, where only the changed objects travel.
/// </summary>
internal sealed class DirectoryPceObjectStore : IPceObjectStore
{
    private const string TempFolder = ".tmp";
    private readonly string _root;
    private readonly string _temp;

    public DirectoryPceObjectStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = Path.GetFullPath(root);
        _temp = Path.Combine(_root, TempFolder);
        Directory.CreateDirectory(_temp);
    }

    public bool ConditionalPut => true;

    public byte[]? Get(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return File.ReadAllBytes(PathOf(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public int Read(string name, long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var handle = File.OpenHandle(PathOf(name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var total = 0;
            while (total < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer[total..], offset + total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PceObjectMissingException(name);
        }
    }

    public void Put(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = PathOf(name);
        var temp = WriteTemp(content);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public bool PutNew(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = PathOf(name);
        var temp = WriteTemp(content);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Fails when the name is taken: the commit of an index. On Windows a move without replacing is one
            // atomic step; elsewhere .NET checks and then renames, so two writers could both win, and a hard link
            // (which fails on an existing name) takes the name instead.
            if (OperatingSystem.IsWindows())
            {
                File.Move(temp, target, overwrite: false);
            }
            else
            {
                if (link(CString(temp), CString(target)) != 0)
                {
                    throw new IOException($"Cannot store \"{name}\" (error {Marshal.GetLastPInvokeError()}).");
                }

                File.Delete(temp);
            }

            return true;
        }
        catch (IOException) when (File.Exists(target))
        {
            File.Delete(temp);
            return false;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public IPceObjectUpload BeginUpload(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new Upload(this, PathOf(name));
    }

    public int DiscardUploads(DateTime startedBeforeUtc, CancellationToken cancellationToken = default)
    {
        var discarded = 0;
        foreach (var file in new DirectoryInfo(_temp).EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc < startedBeforeUtc)
            {
                file.Delete();
                discarded++;
            }
        }

        return discarded;
    }

    public void Delete(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Delete(PathOf(name));
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public IReadOnlyList<PceObjectInfo> List(string prefix, int max = int.MaxValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = new List<PceObjectInfo>();
        foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(_root, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            if (!name.StartsWith(TempFolder + "/", StringComparison.Ordinal) && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                found.Add(new PceObjectInfo(name, file.Length, file.LastWriteTimeUtc));
            }
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return found.Count > max ? found.GetRange(0, max) : found;
    }

    /// <summary>Names are ours ("head", "index/…", "data/…"), but a name never leaves the folder whatever it is.</summary>
    private string PathOf(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment[0] == '.' || segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid object name \"{name}\".", nameof(name));
            }
        }

        return Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
    }

    [DllImport("libc", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
#pragma warning disable SA1300, IDE1006 // the C library's name
    private static extern int link(byte[] existing, byte[] created);
#pragma warning restore SA1300, IDE1006

    private static byte[] CString(string path) => System.Text.Encoding.UTF8.GetBytes(path + '\0');

    private string NewTemp() => Path.Combine(_temp, Guid.NewGuid().ToString("N"));

    private string WriteTemp(ReadOnlySpan<byte> content)
    {
        var temp = NewTemp();
        try
        {
            using var handle = File.OpenHandle(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            RandomAccess.Write(handle, content, 0);
            RandomAccess.FlushToDisk(handle);
            return temp;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private sealed class Upload(DirectoryPceObjectStore store, string target) : IPceObjectUpload
    {
        private readonly string _stem = store.NewTemp();
        private readonly SortedSet<int> _parts = [];
        private bool _done;

        public void Write(int number, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
            File.WriteAllBytes(Part(number), content.Span);
            lock (_parts)
            {
                _parts.Add(number);
            }
        }

        public void Complete(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var whole = _stem + ".whole";
            using (var output = new FileStream(whole, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                foreach (var number in _parts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var part = File.OpenRead(Part(number));
                    part.CopyTo(output);
                }

                output.Flush(flushToDisk: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(whole, target, overwrite: true);
            _done = true;
        }

        public void Dispose()
        {
            foreach (var number in _parts)
            {
                File.Delete(Part(number));
            }

            if (!_done)
            {
                File.Delete(_stem + ".whole");
            }
        }

        private string Part(int number) => _stem + "." + number.ToString(CultureInfo.InvariantCulture);
    }
}
