using System.Collections.Concurrent;
using PlainCommander.Core.Model;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace PlainCommander.Core.Archives;

/// <summary>One entry of an archive as the catalog sees it; folders that exist only implicitly are included.</summary>
public sealed record ArchiveNode(string Inner, bool IsDirectory, long Size, DateTimeOffset Modified);

/// <summary>
/// The table of contents of one archive, read once and kept while the file on disk stays the same
/// (size and write time). Listing a folder inside the archive is then a dictionary lookup.
/// </summary>
public sealed class ArchiveCatalog
{
    private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, ArchiveCatalog Catalog)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ArchiveNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ArchiveNode>> _children = new(StringComparer.OrdinalIgnoreCase);

    private ArchiveCatalog(string archiveFile)
    {
        ArchiveFile = archiveFile;
        _children[string.Empty] = [];
    }

    public string ArchiveFile { get; }

    public int Count => _nodes.Count;

    /// <summary>The format name 7-Zip recognized ("7z", "Rar5", "Iso"…); null when the managed reader read it.</summary>
    public string? Format { get; private set; }

    /// <summary>Opens (or returns the cached) catalog. Throws like the archive library does for unreadable files.</summary>
    public static ArchiveCatalog Open(string archiveFile)
    {
        var info = new FileInfo(archiveFile);
        if (!info.Exists)
        {
            throw new FileNotFoundException(null, archiveFile);
        }

        if (Cache.TryGetValue(archiveFile, out var cached) && cached.Length == info.Length && cached.Written == info.LastWriteTimeUtc)
        {
            return cached.Catalog;
        }

        var catalog = new ArchiveCatalog(archiveFile);
        if (ArchiveReaders.UsesSevenZip(archiveFile))
        {
            using var archive = SevenZip.SevenZipArchive.Open(archiveFile);
            foreach (var item in archive.Items)
            {
                var modified = item.Modified is { } m ? new DateTimeOffset(m) : new DateTimeOffset(info.LastWriteTimeUtc);
                catalog.Add(new ArchiveNode(item.Key, item.IsDirectory, item.IsDirectory ? 0 : item.Size, modified));
            }

            catalog.Format = archive.Format.Name;
        }
        else
        {
            try
            {
                catalog.ReadManaged(archiveFile, info.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ArchiveReaders.Damaged.IsDamage(ex))
            {
                // The managed reader knows no format for it, or its parser broke on damage (IndexOutOfRange, Overflow …).
                throw new NotAnArchiveException(archiveFile, ex);
            }
        }

        Cache[archiveFile] = (info.Length, info.LastWriteTimeUtc, catalog);
        return catalog;
    }

    /// <summary>Reads the table of contents with SharpCompress (no 7-Zip, or a compressed tarball).</summary>
    private void ReadManaged(string archiveFile, DateTime written)
    {
        if (IsStreamOnly(archiveFile))
        {
            // .tar.gz and friends: the archive API sees one gzip member; the reader walks the tar inside it.
            using var stream = File.OpenRead(archiveFile);
            using var reader = ReaderFactory.Open(stream);
            while (reader.MoveToNextEntry())
            {
                AddEntry(reader.Entry, written);
            }
        }
        else
        {
            using var archive = ArchiveFactory.Open(archiveFile);
            foreach (var entry in archive.Entries)
            {
                AddEntry(entry, written);
            }
        }
    }

    public static void Forget(string archiveFile) => Cache.TryRemove(archiveFile, out _);

    /// <summary>Compressed tarballs have no random access: they are read front to back with the reader API.</summary>
    private static readonly string[] StreamOnlyExtensions = [".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tbz", ".tb2", ".tar.xz", ".txz", ".tar.zst", ".tzst", ".tar.lz", ".tlz", ".gz", ".bz2", ".xz", ".zst", ".lz"];

    public static bool IsStreamOnly(string archiveFile) =>
        StreamOnlyExtensions.Any(e => archiveFile.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private void AddEntry(IEntry entry, DateTime fallbackModified)
    {
        var key = ArchivePath.NormalizeKey(entry.Key);
        if (key.Length == 0)
        {
            return;
        }

        var modified = entry.LastModifiedTime is { } t ? new DateTimeOffset(EntryTime(t)) : fallbackModified;
        Add(new ArchiveNode(key, entry.IsDirectory, entry.IsDirectory ? 0 : entry.Size, modified));
    }

    /// <summary>
    /// ZIP stores DOS times in local time without a zone, so an unspecified time from a zip is local;
    /// tar and 7z store UTC. Treating zip times as UTC showed every file an hour or two off.
    /// </summary>
    private DateTime EntryTime(DateTime t)
    {
        if (t.Kind != DateTimeKind.Unspecified)
        {
            return t;
        }

        return DateTime.SpecifyKind(t, ZipFamily.Contains(Path.GetExtension(ArchiveFile)) ? DateTimeKind.Local : DateTimeKind.Utc);
    }

    private static readonly HashSet<string> ZipFamily = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".jar", ".nupkg", ".vsix", ".apk", ".epub" };

    public bool DirectoryExists(string inner) => inner.Length == 0 || (_nodes.TryGetValue(inner, out var n) && n.IsDirectory);

    public ArchiveNode? Find(string inner) => _nodes.GetValueOrDefault(inner);

    /// <summary>Direct children of a folder inside the archive, as panel rows with virtual paths.</summary>
    public IEnumerable<FileEntry> List(string inner)
    {
        if (!_children.TryGetValue(inner, out var children))
        {
            yield break;
        }

        foreach (var node in children)
        {
            yield return new FileEntry
            {
                Name = node.Inner[(node.Inner.LastIndexOf('/') + 1)..],
                FullPath = ArchivePath.Join(ArchiveFile, node.Inner),
                IsDirectory = node.IsDirectory,
                Size = node.Size,
                Modified = node.Modified,
                Attributes = node.IsDirectory ? FileAttributes.Directory | FileAttributes.ReadOnly : FileAttributes.ReadOnly,
            };
        }
    }

    /// <summary>Every folder below a folder, so extraction can recreate empty ones too.</summary>
    public IEnumerable<ArchiveNode> FoldersUnder(string inner)
    {
        var prefix = inner.Length == 0 ? string.Empty : inner + "/";
        return _nodes.Values.Where(n => n.IsDirectory && (prefix.Length == 0 || n.Inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Every file below a folder (or the file itself), for extraction.</summary>
    public IEnumerable<ArchiveNode> FilesUnder(string inner)
    {
        if (inner.Length > 0 && _nodes.TryGetValue(inner, out var node) && !node.IsDirectory)
        {
            return [node];
        }

        var prefix = inner.Length == 0 ? string.Empty : inner + "/";
        return _nodes.Values.Where(n => !n.IsDirectory && (prefix.Length == 0 || n.Inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    private void Add(ArchiveNode node)
    {
        // Parents first, so a file deep in the tree creates the folders the archive never listed.
        var parent = ArchivePath.ParentOf(node.Inner);
        if (parent.Length > 0 && !_nodes.ContainsKey(parent))
        {
            Add(new ArchiveNode(parent, true, 0, node.Modified));
        }

        if (_nodes.TryGetValue(node.Inner, out var existing))
        {
            if (existing.IsDirectory && node.IsDirectory)
            {
                return; // the explicit folder entry of a folder we already inferred
            }
        }

        _nodes[node.Inner] = node;
        if (!_children.TryGetValue(parent, out var siblings))
        {
            siblings = [];
            _children[parent] = siblings;
        }

        if (existing is null)
        {
            siblings.Add(node);
        }
        else
        {
            siblings[siblings.FindIndex(s => string.Equals(s.Inner, node.Inner, StringComparison.OrdinalIgnoreCase))] = node;
        }

        if (node.IsDirectory && !_children.ContainsKey(node.Inner))
        {
            _children[node.Inner] = [];
        }
    }
}
