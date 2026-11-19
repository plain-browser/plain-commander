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

/// <summary>
/// Files and folders in and out of .pce files, whichever of the two layouts a file has. New files are written as
/// containers (<see cref="PceContainer"/>: entries with an index); files from before that ("PCE1": one stream, a
/// folder as a TAR inside) are still read, and <see cref="Upgrade"/> rewrites one as a container.
/// </summary>
public static class PceFile
{
    private const int BufferSize = 1 << 20;

    /// <summary>A GNU long name is a path; anything near this size is a crafted header, not a name.</summary>
    private const int MaxLongNameBytes = 256 * 1024;

    /// <summary>
    /// The stored name is data from the file: only its last segment, and only when that is a plain file name (not
    /// empty, ".", "..", dots and spaces only on Windows, or with characters the disk refuses). Null otherwise.
    /// </summary>
    public static string? PlainName(string storedName)
    {
        ArgumentNullException.ThrowIfNull(storedName);
        var trimmed = storedName.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        var plain = Path.GetFileName(trimmed);

        // On Windows Path.GetFileName reads "a:b" as drive "a:" plus name "b", which would pass; but the same text
        // given to the file APIs is a stream name ("a:b") or a drive-relative path, so a colon in the last segment refuses the name.
        var lastSegment = trimmed[(trimmed.LastIndexOf(Path.DirectorySeparatorChar) + 1)..];
        return plain.Length == 0 || plain is "." or ".." || plain.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || (OperatingSystem.IsWindows() && (plain.TrimEnd('.', ' ').Length == 0 || lastSegment.Contains(':', StringComparison.Ordinal)))
            ? null
            : plain;
    }

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
    /// Encrypts a file or a folder to <paramref name="target"/>, which is created or truncated: a container whose
    /// entries are the file, or the folder's files under the folder's name. When it fails or is cancelled, the
    /// unfinished target is deleted.
    /// </summary>
    /// <param name="compression">zstd before encrypting, decided per file: never, when its first block says it pays, or always.</param>
    /// <param name="progress">Called with the plain bytes consumed so far.</param>
    public static void Encrypt(string source, string target, string password, PceOptions? options, PceCompression compression, Action<long>? progress, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(password);
        var done = false;
        try
        {
            using var container = PceContainer.Create(target, password, options);
            container.Add(PceSource.FromPath(source), compression, progress, token);
            done = true;
        }
        finally
        {
            if (!done)
            {
                TryDelete(target);
            }
        }
    }

    /// <summary>
    /// Writes the first layout ("PCE1"): one stream, a folder as a TAR inside. The app no longer writes it; tests
    /// use this to make the files earlier versions left behind.
    /// </summary>
    internal static void EncryptLegacy(string source, string target, string password, PceOptions? options, PceCompression compression, Action<long>? progress, CancellationToken token)
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

    /// <summary>
    /// Opens a file of the first layout as one stream: throws <see cref="PceWrongPasswordException"/> or
    /// <see cref="PceFormatException"/> at once. A container is opened with <see cref="PceContainer.Open(string, string, bool, PceOptions?)"/>.
    /// </summary>
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
        if (IsContainer(file))
        {
            using var container = PceContainer.Open(file, password);
            return Describe(container, file);
        }

        using var content = Open(file, password);
        return (content.Header, content.Metadata);
    }

    private static bool IsContainer(string file) => PceContainer.LayoutVersion(file) == 2;

    /// <summary>
    /// A container in the terms of the first layout: one file when that is all it holds; otherwise a folder, named
    /// after the one top-level name its entries share, or after the .pce file when they share none.
    /// </summary>
    private static (PceHeader Header, PceMetadata Metadata) Describe(PceContainer container, string file)
    {
        var root = Root(container);
        var single = root is not null && container.Entries is [{ IsDirectory: false } only] && only.Path.Length == root.Length ? only : null;
        var flags = PceLayout.NfcPassword
            | (single is null ? PceLayout.Folder : PceLayout.None)
            | (container.Entries.Any(static e => e.Compressed) ? PceLayout.Zstd : PceLayout.None);
        var modified = single?.ModifiedUtc
            ?? (container.Entries.Count > 0 ? container.Entries.Max(static e => e.ModifiedUtc) : File.GetLastWriteTimeUtc(file));
        return (
            container.Header.ForKdf() with { Flags = flags },
            new PceMetadata(root ?? Path.GetFileNameWithoutExtension(file), single?.Size ?? -1, modified, single?.Attributes ?? FileAttributes.None));
    }

    /// <summary>The first path segment when every entry starts with it; null for an empty container or several top-level names.</summary>
    private static string? Root(PceContainer container)
    {
        string? root = null;
        foreach (var entry in container.Entries)
        {
            var slash = entry.Path.IndexOf('/', StringComparison.Ordinal);
            var first = slash < 0 ? entry.Path : entry.Path[..slash];
            if (root is not null && !string.Equals(root, first, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            root ??= first;
        }

        return root;
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
        var counter = new Counter(progress);
        if (IsContainer(file))
        {
            using var container = PceContainer.Open(file, password);
            var (header, metadata) = Describe(container, file);
            if (header.IsFolder)
            {
                // Entries of one top-level folder land in the target without that folder's name, as its content.
                var strip = Root(container) is { } root ? root.Length + 1 : 0;
                IntoFolder(target, temp => Extract(container, strip, temp, counter, token));
            }
            else
            {
                using var entry = container.OpenRead(container.Entries[0]);
                WriteFile(entry, target, metadata, counter, token);
            }

            return;
        }

        using var content = Open(file, password);
        if (content.Header.IsFolder)
        {
            IntoFolder(target, temp => ReadTar(content.Content, temp, counter, token));
        }
        else
        {
            WriteFile(content.Content, target, content.Metadata, counter, token);
        }
    }

    private static void WriteFile(Stream content, string target, PceMetadata metadata, Counter counter, CancellationToken token)
    {
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
        {
            Pump(content, output, counter, token);
        }

        File.SetLastWriteTimeUtc(target, metadata.ModifiedUtc);
        var attributes = metadata.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
        if (attributes != 0 && OperatingSystem.IsWindows())
        {
            File.SetAttributes(target, File.GetAttributes(target) | attributes);
        }
    }

    /// <summary>A folder's content is written to a new folder next to <paramref name="target"/> and moved in when all of it is there.</summary>
    private static void IntoFolder(string target, Action<string> fill)
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
            fill(temp);
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
    }

    /// <summary>Every entry under <paramref name="target"/>, its path shortened by <paramref name="strip"/> characters; a path that would leave the folder is refused.</summary>
    private static void Extract(PceContainer container, int strip, string target, Counter counter, CancellationToken token)
    {
        var root = Path.GetFullPath(target);
        foreach (var entry in container.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Path.Length <= strip)
            {
                continue; // the top-level folder itself: it is the target
            }

            var destination = Destination(root, entry.Path[strip..]);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = container.OpenRead(entry))
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                Pump(input, output, counter, token);
            }

            File.SetLastWriteTimeUtc(destination, entry.ModifiedUtc);
        }
    }

    /// <summary>
    /// Rewrites a file of the first layout as a container with the same content and password, in a new file that
    /// replaces the old one when it is complete. A container is left as it is. Returns whether anything was rewritten.
    /// </summary>
    /// <param name="progress">Called with the plain bytes carried over so far.</param>
    public static bool Upgrade(string file, string password, Action<long>? progress, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        if (IsContainer(file))
        {
            return false;
        }

        string? temp = null;
        try
        {
            using (var content = Open(file, password))
            {
                var name = PlainName(content.Metadata.Name) ?? throw new PceFormatException($"The stored name \"{content.Metadata.Name}\" is not a valid file name.");
                var compression = content.Header.IsCompressed ? PceCompression.Auto : PceCompression.None;
                temp = TempNames.ReserveFile(file);
                using var container = PceContainer.Create(temp, password);
                try
                {
                    if (content.Header.IsFolder)
                    {
                        container.Add(
                            TarItems(content.Content).Select(item => new PceSource(name + "/" + item.Name, item.IsDirectory, item.IsDirectory ? null : () => item.Body, item.ModifiedUtc, FileAttributes.None)),
                            compression,
                            progress,
                            token);
                        if (container.Entries.Count == 0)
                        {
                            container.Add([new PceSource(name, true, null, content.Metadata.ModifiedUtc, content.Metadata.Attributes)], PceCompression.None, null, token);
                        }
                    }
                    else
                    {
                        container.Add([new PceSource(name, false, () => new Slice(content.Content, null), content.Metadata.ModifiedUtc, content.Metadata.Attributes)], compression, progress, token);
                    }
                }
                catch (ArgumentException ex)
                {
                    throw new PceFormatException("Damaged folder content (an entry name is not a path).", ex);
                }
            }

            File.Move(temp, file, overwrite: true);
            return true;
        }
        finally
        {
            if (temp is not null && File.Exists(temp))
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>Replaces the password of a .pce file. A file of the first layout is rewritten as a container first.</summary>
    public static void ChangePassword(string file, string password, string newPassword, Action<long>? progress, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        Upgrade(file, password, progress, token);
        using var container = PceContainer.Open(file, password, writable: true);
        container.ChangePassword(newPassword);
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
        if (IsContainer(file))
        {
            using var container = PceContainer.Open(file, password);
            if (container.Entries is not [{ IsDirectory: false } only])
            {
                CopyContentAsTar(file, password, output, progress, token);
                return;
            }

            using var entry = container.OpenRead(only);
            Pump(entry, output, new Counter(progress), token);
            return;
        }

        using var content = Open(file, password);
        Pump(content.Content, output, new Counter(progress), token);
    }

    /// <summary>
    /// What the browsing panel reads: a folder's TAR as it is, a single file wrapped in a TAR of one entry
    /// (so both open like an archive and files can be copied out).
    /// </summary>
    public static void CopyContentAsTar(string file, string password, Stream output, Action<long>? progress, CancellationToken token)
    {
        var counter = new Counter(progress);
        if (IsContainer(file))
        {
            using var container = PceContainer.Open(file, password);
            var strip = Describe(container, file).Header.IsFolder && Root(container) is { } root ? root.Length + 1 : 0;
            using var tar = new TarWriter(output, new TarWriterOptions(CompressionType.None, finalizeArchiveOnClose: true) { LeaveStreamOpen = true });
            foreach (var entry in container.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Path.Length <= strip)
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    tar.WriteDirectory(entry.Path[strip..], entry.ModifiedUtc);
                    continue;
                }

                using var input = container.OpenRead(entry);
                using var counting = new CountingStream(new Slice(input, entry.Size), counter, token);
                tar.Write(entry.Path[strip..], counting, entry.ModifiedUtc);
            }

            return;
        }

        using var content = Open(file, password);
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
    /// Writes the entries of the TAR under <paramref name="target"/>; a name that would leave it is refused.
    /// </summary>
    private static void ReadTar(Stream content, string target, Counter counter, CancellationToken token)
    {
        var root = Path.GetFullPath(target);
        var buffer = new byte[BufferSize];
        foreach (var item in TarItems(content))
        {
            token.ThrowIfCancellationRequested();
            var destination = Destination(root, item.Name);
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                int read;
                while ((read = item.Body.Read(buffer)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    counter.Add(read);
                }
            }

            File.SetLastWriteTimeUtc(destination, item.ModifiedUtc);
        }
    }

    /// <summary>A file or a folder in the TAR; <see cref="Body"/> must be read before the next item is asked for.</summary>
    private readonly record struct TarItem(bool IsDirectory, string Name, DateTime ModifiedUtc, Stream Body);

    /// <summary>
    /// Walks the TAR the first layout holds (ustar headers, GNU "././@LongLink" for long names) straight from the
    /// decrypting stream, which cannot seek. Whatever of an item's content was not read is skipped before the next.
    /// </summary>
    private static IEnumerable<TarItem> TarItems(Stream content)
    {
        var header = new byte[512];
        var buffer = new byte[1 << 16];
        string? longName = null;
        while (true)
        {
            if (!ReadExactly(content, header))
            {
                yield break; // end of stream without the end-of-archive marker: tolerated like tar does
            }

            if (header.All(static b => b == 0))
            {
                yield break; // end-of-archive marker
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
                    yield return new TarItem(true, name, modified, Stream.Null);
                    Skip(content, size + padding, buffer);
                    continue;
                case '0' or '\0' or '7':
                    var body = new Slice(content, size);
                    yield return new TarItem(false, name, modified, body);
                    Skip(content, body.Left + padding, buffer);
                    continue;
                default:
                    Skip(content, size + padding, buffer); // links and other kinds are not written by us
                    continue;
            }
        }
    }

    /// <summary>
    /// The next <c>length</c> bytes of a stream (all of it when null), which stays open. Reports the length, so the
    /// TAR writer can take a stream that cannot seek; a stream that ends early is damage.
    /// </summary>
    private sealed class Slice(Stream inner, long? length) : Stream
    {
        public long Left { get; private set; } = length ?? 0;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length ?? throw new NotSupportedException();
        public override long Position { get => (length ?? 0) - Left; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (length is null)
            {
                return inner.Read(buffer);
            }

            if (Left == 0)
            {
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, Left)]);
            if (read <= 0)
            {
                throw new PceFormatException("The content is cut short.");
            }

            Left -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
        if (relative.Contains('\0', StringComparison.Ordinal))
        {
            throw new PceFormatException($"Entry \"{key.Replace('\0', '?')}\" is not a valid name."); // GetFullPath throws ArgumentException on it
        }

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
    internal sealed class CorruptAsFormatStream(Stream inner) : Stream
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
