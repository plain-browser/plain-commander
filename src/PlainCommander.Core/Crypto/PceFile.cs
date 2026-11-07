using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompress.Common;
using SharpCompress.Compressors.ZStandard;
using SharpCompress.Writers.Tar;
using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Crypto;

/// <summary>What a .pce file holds, decrypted and decompressed: a single file's bytes or a TAR of a folder.</summary>
public sealed class PceContent : IDisposable
{
    internal PceContent(PceDecryptStream pce, Stream content)
    {
        Pce = pce;
        Content = content;
    }

    public PceDecryptStream Pce { get; }

    /// <summary>The plain content (through Deflate when the file is compressed).</summary>
    public Stream Content { get; }

    public PceHeader Header => Pce.Header;

    public PceMetadata Metadata => Pce.Metadata;

    public void Dispose()
    {
        Content.Dispose();
        Pce.Dispose();
    }
}

/// <summary>Files and folders in and out of .pce containers. Folders travel as a TAR inside; zstd is applied before encryption when asked or when a sample says it pays.</summary>
public static class PceFile
{
    private const int BufferSize = 1 << 20;

    /// <summary>A GNU long name is a path; anything near this size is a crafted header, not a name.</summary>
    private const int MaxLongNameBytes = 256 * 1024;

    /// <summary>A random 16-character name for "hide the file name": the real name is inside the container.</summary>
    public static string RandomName()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        return string.Create(16, bytes, static (span, b) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[b[i] % alphabet.Length];
            }
        }) + PceFormat.Extension;
    }

    /// <summary>
    /// Encrypts a file or a folder (TAR inside) to <paramref name="target"/>, which is created or truncated. When it
    /// fails or is cancelled, the unfinished target is deleted; it is never sealed as a complete container.
    /// </summary>
    /// <param name="compression">zstd before encrypting: never, when a sample says it pays, or always.</param>
    /// <param name="progress">Called with the plain bytes consumed so far.</param>
    public static void Encrypt(string source, string target, string password, PceOptions? options, PceCompression compression, Action<long>? progress, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(password);
        options ??= PceOptions.Default;
        var isFolder = Directory.Exists(source);
        var compress = compression switch
        {
            PceCompression.Always => true,
            PceCompression.Auto => SamplePays(source, isFolder, options, token),
            _ => false,
        };
        var layout = (isFolder ? PceLayout.Folder : PceLayout.None) | (compress ? PceLayout.Zstd : PceLayout.None);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        FileSystemInfo info = isFolder ? new DirectoryInfo(source) : new FileInfo(source);
        var metadata = new PceMetadata(name, info is FileInfo f ? f.Length : -1, info.LastWriteTimeUtc, info.Attributes & ~FileAttributes.Directory);

        var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
        var done = false;
        try
        {
            using var pce = new PceEncryptStream(output, password, metadata, options, layout);
            try
            {
                Stream sink = compress ? new CompressionStream(pce, options.ZstdLevel, leaveOpen: true) : pce;
                try
                {
                    var counter = new Counter(progress);
                    if (isFolder)
                    {
                        WriteTar(source, sink, counter, token);
                    }
                    else
                    {
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
                        Pump(input, sink, counter, token);
                    }
                }
                finally
                {
                    if (compress)
                    {
                        sink.Dispose(); // flushes the compressor's tail into the container
                    }
                }

                pce.Complete();
            }
            catch
            {
                pce.Abort();
                throw;
            }

            done = true;
        }
        finally
        {
            output.Dispose();
            if (!done)
            {
                TryDelete(target);
            }
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the original error matters more; an unsealed container is rejected anyway
        }
    }

    /// <summary>
    /// Auto mode: compresses up to the first megabyte (of the file, or of the folder's files in walking order) and
    /// decides by how much it shrank. Media, archives and already encrypted data stay as they are.
    /// </summary>
    internal static bool SamplePays(string source, bool isFolder, PceOptions options, CancellationToken token)
    {
        var sample = new byte[options.ChunkSize];
        var filled = 0;
        foreach (var file in isFolder ? WalkFiles(new DirectoryInfo(source)) : [source])
        {
            token.ThrowIfCancellationRequested();
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            int read;
            while (filled < sample.Length && (read = input.Read(sample, filled, sample.Length - filled)) > 0)
            {
                filled += read;
            }

            if (filled == sample.Length)
            {
                break;
            }
        }

        if (filled < 256)
        {
            return false; // nothing to gain, and a tiny file would only grow by the frame
        }

        using var compressed = new MemoryStream(filled);
        using (var zstd = new CompressionStream(compressed, options.ZstdLevel, leaveOpen: true))
        {
            zstd.Write(sample, 0, filled);
        }

        return compressed.Length < filled * options.AutoThreshold;
    }

    private static IEnumerable<string> WalkFiles(DirectoryInfo dir)
    {
        foreach (var child in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if (child is DirectoryInfo sub)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    foreach (var file in WalkFiles(sub))
                    {
                        yield return file;
                    }
                }
            }
            else
            {
                yield return child.FullName;
            }
        }
    }

    /// <summary>Opens the container: throws <see cref="PceWrongPasswordException"/> or <see cref="PceFormatException"/> at once.</summary>
    public static PceContent Open(string file, string password)
    {
        var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
        PceDecryptStream pce;
        try
        {
            pce = new PceDecryptStream(input, password, Path.GetFileName(file));
        }
        catch
        {
            input.Dispose();
            throw;
        }

        Stream content = (pce.Header.Flags & PceLayout.Zstd) != 0 ? new CorruptAsFormatStream(new DecompressionStream(pce, leaveOpen: true))
            : (pce.Header.Flags & PceLayout.Deflate) != 0 ? new CorruptAsFormatStream(new DeflateStream(pce, CompressionMode.Decompress, leaveOpen: true))
            : pce;
        return new PceContent(pce, content);
    }

    /// <summary>Header and metadata only (checks the password): what is inside and what it was called.</summary>
    public static (PceHeader Header, PceMetadata Metadata) Peek(string file, string password)
    {
        using var content = Open(file, password);
        return (content.Header, content.Metadata);
    }

    /// <summary>
    /// Writes the content back: a file to <paramref name="target"/> (created or truncated), a folder into the
    /// directory <paramref name="target"/> (created when missing; entries inside are overwritten). A folder is
    /// first written to a new folder next to the target and moved into place only when all of it authenticated,
    /// so damage leaves nothing behind and never touches files already in the target.
    /// </summary>
    /// <param name="progress">Called with the plain bytes written so far.</param>
    public static void Decrypt(string file, string password, string target, Action<long>? progress, CancellationToken token)
    {
        using var content = Open(file, password);
        var counter = new Counter(progress);
        if (content.Header.IsFolder)
        {
            if (File.Exists(target))
            {
                throw new IOException($"\"{target}\" is a file, not a folder.");
            }

            var parent = Path.GetDirectoryName(Path.GetFullPath(target));
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            var temp = TempNames.ReserveFolder(target);
            try
            {
                ReadTar(content.Content, temp, counter, token);
                if (Directory.Exists(target))
                {
                    MergeInto(temp, target);
                }
                else
                {
                    Directory.Move(temp, target);
                }
            }
            finally
            {
                if (Directory.Exists(temp))
                {
                    try
                    {
                        Directory.Delete(temp, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // a leftover temporary folder is better than hiding the original error
                    }
                }
            }

            return;
        }

        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
        {
            Pump(content.Content, output, counter, token);
        }

        File.SetLastWriteTimeUtc(target, content.Metadata.ModifiedUtc);
        var attributes = content.Metadata.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
        if (attributes != 0 && OperatingSystem.IsWindows())
        {
            File.SetAttributes(target, File.GetAttributes(target) | attributes);
        }
    }

    /// <summary>A fully decrypted folder over an existing one: files moved in (replacing same names), folders merged.</summary>
    private static void MergeInto(string from, string into)
    {
        Directory.CreateDirectory(into);
        foreach (var child in new DirectoryInfo(from).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            var destination = Path.Combine(into, child.Name);
            if (child is DirectoryInfo sub)
            {
                if (File.Exists(destination))
                {
                    throw new IOException($"\"{destination}\" is a file, not a folder.");
                }

                if (Directory.Exists(destination))
                {
                    MergeInto(sub.FullName, destination);
                }
                else
                {
                    Directory.Move(sub.FullName, destination);
                }
            }
            else
            {
                File.Move(child.FullName, destination, overwrite: true);
            }
        }
    }

    /// <summary>The plain content (a file's bytes, or the folder's TAR) into <paramref name="output"/>.</summary>
    public static void CopyContent(string file, string password, Stream output, Action<long>? progress, CancellationToken token)
    {
        using var content = Open(file, password);
        Pump(content.Content, output, new Counter(progress), token);
    }

    /// <summary>
    /// What the browsing panel reads: a folder's TAR as it is, a single file wrapped in a TAR of one entry
    /// (so both open like an archive and files can be copied out).
    /// </summary>
    public static void CopyContentAsTar(string file, string password, Stream output, Action<long>? progress, CancellationToken token)
    {
        using var content = Open(file, password);
        var counter = new Counter(progress);
        if (content.Header.IsFolder)
        {
            Pump(content.Content, output, counter, token);
            return;
        }

        using var plain = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.DeleteOnClose);
        Pump(content.Content, plain, counter, token);
        plain.Position = 0;
        using var writer = new TarWriter(output, new TarWriterOptions(CompressionType.None, finalizeArchiveOnClose: true) { LeaveStreamOpen = true });
        writer.Write(content.Metadata.Name, plain, content.Metadata.ModifiedUtc);
    }

    private static void Pump(Stream input, Stream output, Counter counter, CancellationToken token)
    {
        var buffer = new byte[BufferSize];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            counter.Add(read);
        }
    }

    private static void WriteTar(string folder, Stream sink, Counter counter, CancellationToken token)
    {
        using var writer = new TarWriter(sink, new TarWriterOptions(CompressionType.None, finalizeArchiveOnClose: true) { LeaveStreamOpen = true });
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        Walk(new DirectoryInfo(root));

        void Walk(DirectoryInfo dir)
        {
            token.ThrowIfCancellationRequested();
            var any = false;
            foreach (var child in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                any = true;
                var key = Path.GetRelativePath(root, child.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (child is DirectoryInfo sub)
                {
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue; // a link would pack its target under another name
                    }

                    Walk(sub);
                    continue;
                }

                token.ThrowIfCancellationRequested();
                using var input = new FileStream(child.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.SequentialScan);
                using var counting = new CountingStream(input, counter, token);
                writer.Write(key, counting, child.LastWriteTime);
            }

            if (!any && dir.FullName != root)
            {
                writer.WriteDirectory(Path.GetRelativePath(root, dir.FullName).Replace(Path.DirectorySeparatorChar, '/'), dir.LastWriteTime);
            }
        }
    }

    /// <summary>
    /// Reads the TAR the writer above produced (ustar headers, GNU "././@LongLink" for long names) straight from
    /// the decrypting stream, which cannot seek. Entries are written under <paramref name="target"/>; a name that
    /// would leave it is refused.
    /// </summary>
    private static void ReadTar(Stream content, string target, Counter counter, CancellationToken token)
    {
        var root = Path.GetFullPath(target);
        var header = new byte[512];
        var buffer = new byte[BufferSize];
        string? longName = null;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!ReadExactly(content, header))
            {
                return; // end of stream without the end-of-archive marker: tolerated like tar does
            }

            if (header.All(static b => b == 0))
            {
                return; // end-of-archive marker
            }

            var size = ParseOctal(header.AsSpan(124, 12));
            var type = (char)header[156];
            var name = longName ?? FullName(header);
            longName = null;
            var modified = DateTime.UnixEpoch.AddSeconds(ParseOctal(header.AsSpan(136, 12)));
            var padding = (512 - (int)(size % 512)) % 512;
            switch (type)
            {
                case 'L':
                    if (size > MaxLongNameBytes)
                    {
                        throw new PceFormatException("Damaged folder content (long name too large).");
                    }

                    var nameBytes = new byte[size];
                    if (!ReadExactly(content, nameBytes))
                    {
                        throw new PceFormatException("The folder content is cut short.");
                    }

                    longName = System.Text.Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                    Skip(content, padding, buffer);
                    continue;
                case '5':
                    Directory.CreateDirectory(Destination(root, name));
                    Skip(content, size + padding, buffer);
                    continue;
                case '0' or '\0' or '7':
                    var destination = Destination(root, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
                    {
                        var left = size;
                        while (left > 0)
                        {
                            var read = content.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (read <= 0)
                            {
                                throw new PceFormatException("The folder content is cut short.");
                            }

                            token.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            counter.Add(read);
                            left -= read;
                        }
                    }

                    File.SetLastWriteTimeUtc(destination, modified);
                    Skip(content, padding, buffer);
                    continue;
                default:
                    Skip(content, size + padding, buffer); // links and other kinds are not written by us
                    continue;
            }
        }
    }

    private static string FullName(byte[] header)
    {
        var name = Field(header, 0, 100);
        var prefix = header.AsSpan(257, 5).SequenceEqual("ustar"u8) ? Field(header, 345, 155) : string.Empty;
        return prefix.Length > 0 ? prefix + "/" + name : name;
    }

    private static string Field(byte[] header, int at, int length)
    {
        var span = header.AsSpan(at, length);
        var end = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    private static long ParseOctal(ReadOnlySpan<byte> field)
    {
        long value = 0;
        foreach (var b in field)
        {
            if (b is (byte)' ' or 0)
            {
                if (value > 0)
                {
                    break;
                }

                continue;
            }

            if (b is < (byte)'0' or > (byte)'7')
            {
                throw new PceFormatException("Damaged folder content (bad TAR header).");
            }

            value = value * 8 + (b - '0');
        }

        return value;
    }

    private static string Destination(string root, string key)
    {
        var relative = key.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        if (relative.Length == 0 || !destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new PceFormatException($"Entry \"{key}\" points outside the folder.");
        }

        return destination;
    }

    private static bool ReadExactly(Stream stream, byte[] target)
    {
        var got = 0;
        while (got < target.Length)
        {
            var n = stream.Read(target, got, target.Length - got);
            if (n <= 0)
            {
                return got == 0 ? false : throw new PceFormatException("The folder content is cut short.");
            }

            got += n;
        }

        return true;
    }

    private static void Skip(Stream stream, long bytes, byte[] buffer)
    {
        while (bytes > 0)
        {
            var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes));
            if (n <= 0)
            {
                throw new PceFormatException("The folder content is cut short.");
            }

            bytes -= n;
        }
    }

    /// <summary>
    /// Content that authenticated but does not decompress (zstd's own exception, Deflate's InvalidDataException) is
    /// damage like any other: reported as <see cref="PceFormatException"/>, which the operations treat per file.
    /// </summary>
    private sealed class CorruptAsFormatStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                return inner.Read(buffer);
            }
            catch (Exception ex) when (ex is not (IOException or OperationCanceledException or ObjectDisposedException))
            {
                throw new PceFormatException("Damaged compressed content.", ex);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class Counter(Action<long>? report)
    {
        private long _total;

        public void Add(long bytes)
        {
            _total += bytes;
            report?.Invoke(_total);
        }
    }

    /// <summary>Counts bytes the TAR writer pulls; seekable because the writer wants the length first.</summary>
    private sealed class CountingStream(Stream inner, Counter counter, CancellationToken token) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, count);
            counter.Add(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
