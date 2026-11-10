using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using PlainCommander.Core.Viewer;

namespace PlainCommander.Core.Search;

/// <summary>What Alt+F7 looks for. Null bounds mean "any".</summary>
public sealed record SearchCriteria(
    string Root,
    string Masks = "*.*",
    bool Subfolders = true,
    string? Text = null,
    bool CaseSensitive = false,
    long? MinBytes = null,
    long? MaxBytes = null,
    DateTimeOffset? ModifiedAfter = null,
    DateTimeOffset? ModifiedBefore = null,
    bool IncludeHidden = false,
    bool NameRegex = false,
    bool TextRegex = false,
    bool InArchives = false)
{
    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>Throws <see cref="ArgumentException"/> when a regular expression does not parse.</summary>
    public Func<string, bool> NameMatcher()
    {
        if (!NameRegex)
        {
            // A word without wildcards is a part of the name ("report" finds "report 2026.pdf"), as in the panel
            // filter; typed as an exact name it found nothing and the search looked broken.
            var parts = Masks.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static m => m.AsSpan().IndexOfAny('*', '?') < 0 ? $"*{m}*" : m);
            return FileMask.Parse(string.Join(';', parts));
        }

        var regex = new Regex(Masks, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return regex.IsMatch;
    }

    /// <summary>Line matcher for content search; null when no text is asked for.</summary>
    public Func<string, bool>? TextMatcher()
    {
        if (!HasText)
        {
            return null;
        }

        if (TextRegex)
        {
            var regex = new Regex(Text!, (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return regex.IsMatch;
        }

        var text = Text!;
        var comparison = CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return line => line.Contains(text, comparison);
    }
}

/// <summary>Where the search is: folders visited, matches so far, the folder being read.</summary>
public sealed record SearchProgress(int Folders, int Matches, string CurrentFolder);

/// <summary>
/// Walks a tree and streams the entries that match. Folders match by name only; files also by size,
/// date and, when text is given, by content (decoded with the viewer's encoding detection, read in one pass).
/// Links are not entered, unreadable folders are skipped, and the caller cancels through the token.
/// </summary>
public static class FileSearch
{
    private const long MaxTextScanBytes = 512L * 1024 * 1024;

    /// <summary>Archive entries are unpacked to memory for a content search; bigger ones are skipped.</summary>
    private const long MaxArchiveEntryScanBytes = 64L * 1024 * 1024;

    public static async IAsyncEnumerable<FileEntry> RunAsync(
        SearchCriteria criteria,
        IProgress<SearchProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var channel = Channel.CreateUnbounded<FileEntry>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = Task.Run(() =>
        {
            try
            {
                Walk(criteria, channel.Writer, progress, cancellationToken);
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }

        await producer.ConfigureAwait(false);
    }

    private static void Walk(SearchCriteria criteria, ChannelWriter<FileEntry> output, IProgress<SearchProgress>? progress, CancellationToken token)
    {
        var matchesName = criteria.NameMatcher();
        var matchesLine = criteria.TextMatcher();
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = criteria.IncludeHidden ? FileAttributes.None : FileAttributes.Hidden | FileAttributes.System,
        };
        var stack = new Stack<string>();
        stack.Push(ExactPath.Native(criteria.Root));
        var folders = 0;
        var matches = 0;
        var lastReport = DateTime.UtcNow;
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            folders++;
            if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
            {
                progress.Report(new SearchProgress(folders, matches, ExactPath.Display(dir)));
                lastReport = DateTime.UtcNow;
            }

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                token.ThrowIfCancellationRequested();
                var isDir = child is DirectoryInfo;
                var isLink = child.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isDir && criteria.Subfolders && !isLink)
                {
                    stack.Push(child.FullName);
                }

                if (!isDir && criteria.InArchives && ArchivePath.IsArchiveName(child.Name))
                {
                    matches += SearchArchive(ExactPath.Display(child.FullName), criteria, matchesName, matchesLine, output, token);
                }

                if (!matchesName(child.Name))
                {
                    continue;
                }

                if (isDir)
                {
                    if (criteria.HasText || criteria.MinBytes is not null || criteria.MaxBytes is not null)
                    {
                        continue; // size and content questions are about files
                    }
                }
                else
                {
                    var file = (FileInfo)child;
                    if (criteria.MinBytes is { } min && file.Length < min)
                    {
                        continue;
                    }

                    if (criteria.MaxBytes is { } max && file.Length > max)
                    {
                        continue;
                    }
                }

                var modified = new DateTimeOffset(child.LastWriteTimeUtc);
                if (criteria.ModifiedAfter is { } after && modified < after)
                {
                    continue;
                }

                if (criteria.ModifiedBefore is { } before && modified > before)
                {
                    continue;
                }

                if (!isDir && matchesLine is not null && !ContainsText((FileInfo)child, matchesLine, token))
                {
                    continue;
                }

                matches++;
                output.TryWrite(new FileEntry
                {
                    Name = child.Name,
                    FullPath = ExactPath.Display(child.FullName),
                    IsDirectory = isDir,
                    Size = isDir ? 0 : ((FileInfo)child).Length,
                    Modified = modified,
                    Attributes = child.Attributes,
                });
            }
        }

        progress?.Report(new SearchProgress(folders, matches, string.Empty));
    }

    /// <summary>Line by line with the detected encoding; a match spanning a line break is not found, which is the usual trade-off.</summary>
    public static bool ContainsText(FileInfo file, string text, bool caseSensitive, CancellationToken token)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return ContainsText(file, line => line.Contains(text, comparison), token);
    }

    public static bool ContainsText(FileInfo file, Func<string, bool> matchesLine, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length == 0 || file.Length > MaxTextScanBytes)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return ContainsText(stream, matchesLine, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Content search on a seekable stream: binary content is skipped, text is decoded like the viewer does.</summary>
    private static bool ContainsText(Stream stream, Func<string, bool> matchesLine, CancellationToken token)
    {
        try
        {
            var sample = new byte[(int)Math.Min(stream.Length, TextContent.SampleBytes)];
            var read = stream.Read(sample, 0, sample.Length);
            var span = sample.AsSpan(0, read);
            if (read == 0 || TextDecoder.LooksBinary(span))
            {
                return false;
            }

            var encoding = TextDecoder.Detect(span);
            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested();
                if (matchesLine(line))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Matches the entries of one archive like files on disk and writes them with virtual paths
    /// ("C:\x\a.zip\docs\b.txt"). Nested archives are not opened. Returns how many matched.
    /// </summary>
    private static int SearchArchive(
        string archiveFile,
        SearchCriteria criteria,
        Func<string, bool> matchesName,
        Func<string, bool>? matchesLine,
        ChannelWriter<FileEntry> output,
        CancellationToken token)
    {
        ArchiveCatalog catalog;
        try
        {
            catalog = ArchiveCatalog.Open(archiveFile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0; // damaged, encrypted or unsupported: the archive file itself can still match by name
        }

        var candidates = new List<ArchiveNode>();
        foreach (var node in catalog.FoldersUnder(string.Empty).Concat(catalog.FilesUnder(string.Empty)))
        {
            token.ThrowIfCancellationRequested();
            if (!criteria.Subfolders && node.Inner.Contains('/', StringComparison.Ordinal))
            {
                continue;
            }

            var name = node.Inner[(node.Inner.LastIndexOf('/') + 1)..];
            if (!matchesName(name))
            {
                continue;
            }

            if (node.IsDirectory)
            {
                if (criteria.HasText || criteria.MinBytes is not null || criteria.MaxBytes is not null)
                {
                    continue;
                }
            }
            else if ((criteria.MinBytes is { } min && node.Size < min) || (criteria.MaxBytes is { } max && node.Size > max))
            {
                continue;
            }

            if ((criteria.ModifiedAfter is { } after && node.Modified < after) || (criteria.ModifiedBefore is { } before && node.Modified > before))
            {
                continue;
            }

            candidates.Add(node);
        }

        if (matchesLine is not null)
        {
            var withText = EntriesContaining(archiveFile, candidates.Where(n => !n.IsDirectory).ToList(), matchesLine, token);
            candidates = candidates.Where(n => withText.Contains(n.Inner)).ToList();
        }

        foreach (var node in candidates)
        {
            output.TryWrite(new FileEntry
            {
                Name = node.Inner[(node.Inner.LastIndexOf('/') + 1)..],
                FullPath = ArchivePath.Join(archiveFile, node.Inner),
                IsDirectory = node.IsDirectory,
                Size = node.Size,
                Modified = node.Modified,
                Attributes = node.IsDirectory ? FileAttributes.Directory | FileAttributes.ReadOnly : FileAttributes.ReadOnly,
            });
        }

        return candidates.Count;
    }

    /// <summary>Reads the archive once and returns the entries whose text matches.</summary>
    private static HashSet<string> EntriesContaining(string archiveFile, List<ArchiveNode> files, Func<string, bool> matchesLine, CancellationToken token)
    {
        var wanted = files.Where(f => f.Size > 0 && f.Size <= MaxArchiveEntryScanBytes).Select(f => f.Inner).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return found;
        }

        bool Check(Func<Stream> open)
        {
            using var entry = open();
            using var buffer = new MemoryStream();
            entry.CopyTo(buffer);
            buffer.Position = 0;
            return ContainsText(buffer, matchesLine, token);
        }

        try
        {
            using var reader = ArchiveReaders.Open(archiveFile, wanted);
            while (reader.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                if (!reader.IsDirectory && wanted.Contains(reader.Key) && Check(reader.OpenEntryStream))
                {
                    found.Add(reader.Key);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // a damaged entry ends the content search in this archive; what was found stays
        }

        return found;
    }
}
