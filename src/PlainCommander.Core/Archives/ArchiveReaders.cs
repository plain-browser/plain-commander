using PlainCommander.Core.Archives.SevenZip;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace PlainCommander.Core.Archives;

/// <summary>
/// Walks the entries of an archive front to back, once, and opens the content of the current one. Every archive
/// is read this way (extraction, F3, search in content): a solid archive is then decompressed only once.
/// </summary>
public interface IArchiveEntryReader : IDisposable
{
    bool MoveNext();

    /// <summary>Normalized key of the current entry ("docs/a.txt").</summary>
    string Key { get; }

    bool IsDirectory { get; }

    long Size { get; }

    DateTime? Modified { get; }

    /// <summary>The current entry's content; read it before <see cref="MoveNext"/>, at most once.</summary>
    Stream OpenEntryStream();
}

/// <summary>Picks the engine for an archive: 7-Zip when the library is there, the managed reader otherwise.</summary>
public static class ArchiveReaders
{
    /// <summary>
    /// True when 7-Zip reads this archive. Compressed tarballs stay with the managed reader, which walks the tar
    /// inside the gzip/bzip2/xz stream (7-Zip would show one ".tar" entry).
    /// </summary>
    public static bool UsesSevenZip(string archiveFile) => SevenZipLibrary.IsAvailable && !ArchiveCatalog.IsStreamOnly(archiveFile);

    /// <summary>Whether 7z.dll is loaded (formats beyond zip/tar/7z/rar, writing 7z).</summary>
    public static bool SevenZipAvailable => SevenZipLibrary.IsAvailable;

    /// <param name="wanted">Keys of the files to read; null for all. Others may be skipped without decompressing.</param>
    public static IArchiveEntryReader Open(string archiveFile, IReadOnlySet<string>? wanted = null) =>
        UsesSevenZip(archiveFile)
            ? new SevenZipEntryReader(archiveFile, wanted)
            : new DamageAsIOReader(archiveFile, Damaged.Guard(archiveFile, () => new ManagedEntryReader(archiveFile, wanted)));

    /// <summary>
    /// SharpCompress meets a damaged archive with whatever exception its parser runs into (IndexOutOfRange, Overflow,
    /// InvalidOperation, InvalidData, ArgumentOutOfRange …). Those become <see cref="NotAnArchiveException"/>, an
    /// IOException, so a damaged archive is an error of that file (skip, retry) and not a crash of the operation.
    /// </summary>
    internal static class Damaged
    {
        public static bool IsDamage(Exception ex) =>
            ex is not (IOException or UnauthorizedAccessException or OperationCanceledException or OutOfMemoryException or ObjectDisposedException);

        public static T Guard<T>(string archiveFile, Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex) when (IsDamage(ex))
            {
                throw new NotAnArchiveException(archiveFile, ex);
            }
        }
    }

    private sealed class DamageAsIOReader(string archiveFile, IArchiveEntryReader inner) : IArchiveEntryReader
    {
        public string Key => inner.Key;

        public bool IsDirectory => inner.IsDirectory;

        public long Size => inner.Size;

        public DateTime? Modified => inner.Modified;

        public bool MoveNext() => Damaged.Guard(archiveFile, inner.MoveNext);

        public Stream OpenEntryStream() => new DamageAsIOStream(archiveFile, Damaged.Guard(archiveFile, inner.OpenEntryStream));

        public void Dispose() => inner.Dispose();
    }

    /// <summary>An entry's content; a decompressor failing on damaged data reports it as <see cref="NotAnArchiveException"/>.</summary>
    private sealed class DamageAsIOStream(string archiveFile, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Damaged.Guard(archiveFile, () => inner.Read(buffer, offset, count));

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

    /// <summary>The managed (SharpCompress) reader: sequential for tarballs and solid archives, random access otherwise.</summary>
    private sealed class ManagedEntryReader : IArchiveEntryReader
    {
        private readonly Stream? _stream;
        private readonly IArchive? _archive;
        private readonly IReader? _reader;
        private readonly IEnumerator<IArchiveEntry>? _entries;
        private readonly IReadOnlySet<string>? _wanted;

        public ManagedEntryReader(string archiveFile, IReadOnlySet<string>? wanted)
        {
            _wanted = wanted;
            if (ArchiveCatalog.IsStreamOnly(archiveFile))
            {
                _stream = File.OpenRead(archiveFile);
                _reader = ReaderFactory.Open(_stream);
                return;
            }

            _archive = ArchiveFactory.Open(archiveFile);
            if (_archive.IsSolid)
            {
                _reader = _archive.ExtractAllEntries();
            }
            else
            {
                _entries = _archive.Entries.GetEnumerator();
            }
        }

        public string Key { get; private set; } = string.Empty;

        public bool IsDirectory { get; private set; }

        public long Size { get; private set; }

        public DateTime? Modified { get; private set; }

        public bool MoveNext()
        {
            while (_reader is not null ? _reader.MoveToNextEntry() : _entries!.MoveNext())
            {
                var entry = _reader is not null ? _reader.Entry : _entries!.Current;
                var key = ArchivePath.NormalizeKey(entry.Key);
                if (key.Length == 0 || (_wanted is not null && (entry.IsDirectory || !_wanted.Contains(key))))
                {
                    continue;
                }

                Key = key;
                IsDirectory = entry.IsDirectory;
                Size = entry.IsDirectory ? 0 : entry.Size;
                Modified = entry.LastModifiedTime;
                return true;
            }

            return false;
        }

        public Stream OpenEntryStream() => _reader is not null ? _reader.OpenEntryStream() : _entries!.Current.OpenEntryStream();

        public void Dispose()
        {
            _entries?.Dispose();
            _reader?.Dispose();
            _archive?.Dispose();
            _stream?.Dispose();
        }
    }
}
