using System.Collections.Concurrent;

namespace PlainCommander.Core.Archives;

/// <summary>Where a path points inside an archive: the archive file on disk and the entry path inside it ("" is the root).</summary>
public sealed record ArchiveLocation(string ArchiveFile, string Inner)
{
    public bool IsRoot => Inner.Length == 0;

    public string Name => IsRoot ? Path.GetFileName(ArchiveFile) : Inner[(Inner.LastIndexOf('/') + 1)..];

    /// <summary>The virtual path the panels show: archive path, then the entry path with the OS separator.</summary>
    public string VirtualPath => ArchivePath.Join(ArchiveFile, Inner);
}

/// <summary>
/// Archives are browsed as folders: "C:\x\a.zip\docs\readme.txt" means the entry docs/readme.txt in a.zip.
/// The boundary is the first path segment that is an existing file with an archive extension.
/// </summary>
public static class ArchivePath
{
    /// <summary>Archives the managed reader opens too; Enter goes into them like into a folder.</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".tbz", ".tb2", ".xz", ".txz", ".zst", ".tzst", ".lz", ".tlz",
        ".jar", ".nupkg", ".vsix", ".apk", ".epub",
    };

    /// <summary>Archives only 7-Zip opens; Enter goes into them when the library is there.</summary>
    private static readonly HashSet<string> SevenZipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".udf", ".cab", ".wim", ".swm", ".esd", ".arj", ".lzh", ".lha", ".cpio", ".rpm", ".deb", ".xar", ".dmg",
        ".lzma", ".z", ".squashfs", ".cramfs",
    };

    /// <summary>Files opened as archives with Ctrl+PgDn (an installer, a disk image, a document); Enter still runs them.</summary>
    private static readonly ConcurrentDictionary<string, bool> OpenedExplicitly = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsArchiveName(string name)
    {
        var extension = Path.GetExtension(name);
        return Extensions.Contains(extension) || (ArchiveReaders.SevenZipAvailable && SevenZipExtensions.Contains(extension));
    }

    /// <summary>
    /// PDF and office documents are documents, not archives: Ctrl+PgDn leaves them alone even though some are zips
    /// inside (the user's decision, 2026-11-05).
    /// </summary>
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".xls", ".xlsx", ".xlsm", ".xlsb", ".xlt", ".xltx", ".xltm",
        ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".odt", ".ods", ".odp", ".odg", ".ott", ".ots", ".otp",
        ".rtf", ".vsd", ".vsdx", ".pub", ".one",
    };

    public static bool IsDocument(string name) => Documents.Contains(Path.GetExtension(name));

    /// <summary>
    /// Ctrl+PgDn on any file: from now on its path is an archive boundary, like an archive extension.
    /// False (and nothing changes) for PDF and office documents.
    /// </summary>
    public static bool OpenExplicitly(string file)
    {
        if (IsDocument(file))
        {
            return false;
        }

        OpenedExplicitly[file] = true;
        return true;
    }

    public static void ForgetExplicit(string file) => OpenedExplicitly.TryRemove(file, out _);

    public static bool IsOpenedExplicitly(string file) => OpenedExplicitly.ContainsKey(file);

    /// <summary>Splits a possibly virtual path; null when it is an ordinary disk path.</summary>
    public static ArchiveLocation? Split(string path, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var rest = path[root.Length..];
        var segments = rest.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = current.Length == 0 || current.EndsWith(Path.DirectorySeparatorChar) ? current + segments[i] : current + Path.DirectorySeparatorChar + segments[i];
            if ((IsArchiveName(segments[i]) || OpenedExplicitly.ContainsKey(current)) && fileExists(current))
            {
                var inner = string.Join('/', segments[(i + 1)..]);
                return new ArchiveLocation(current, inner);
            }
        }

        return null;
    }

    public static string Join(string archiveFile, string inner) =>
        inner.Length == 0 ? archiveFile : archiveFile + Path.DirectorySeparatorChar + inner.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>Entry keys normalized: forward slashes, no leading "./" or "/", no trailing slash.</summary>
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var k = key.Replace('\\', '/');
        while (k.StartsWith("./", StringComparison.Ordinal))
        {
            k = k[2..];
        }

        return k.Trim('/');
    }

    public static string ParentOf(string inner)
    {
        var slash = inner.LastIndexOf('/');
        return slash < 0 ? string.Empty : inner[..slash];
    }
}
