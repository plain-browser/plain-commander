using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Compare;

/// <summary>How one name differs between the left and the right folder.</summary>
public enum Difference
{
    /// <summary>Only in the left folder.</summary>
    OnlyLeft,

    /// <summary>Only in the right folder.</summary>
    OnlyRight,

    /// <summary>Both sides have it and the left copy is newer.</summary>
    LeftNewer,

    /// <summary>Both sides have it and the right copy is newer.</summary>
    RightNewer,

    /// <summary>Same time, different size or content: nobody is "newer", so both sides count as different.</summary>
    Different,

    /// <summary>A file on one side, a folder on the other.</summary>
    TypeMismatch,
}

public sealed record CompareOptions(bool Subfolders = false, bool ByContent = false)
{
    /// <summary>FAT stores times in 2 s steps and copies between file systems round them; within this the times are equal.</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);
}

/// <summary>Differences keyed by path relative to the compared folders, with "\" as separator.</summary>
/// <summary>How far a comparison got: the folder being read ("" = the compared one) and the counts so far.</summary>
public sealed record CompareProgress(string Folder, int Folders, int Differences);

/// <summary>What one side has under a name: the facts the comparison and the synchronize window show.</summary>
public sealed record CompareEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified, bool IsLink = false);

/// <summary>Differences keyed by path relative to the compared folders, with "\" as separator.</summary>
public sealed class CompareResult
{
    private readonly Dictionary<string, Difference> _items = new(PathComparer);
    private readonly Dictionary<string, (CompareEntry? Left, CompareEntry? Right)> _entries = new(PathComparer);

    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyDictionary<string, Difference> Items => _items;

    public int Count => _items.Count;

    /// <summary>What each side has for a difference (null: nothing there).</summary>
    public (CompareEntry? Left, CompareEntry? Right) EntriesOf(string relative) =>
        _entries.TryGetValue(relative, out var pair) ? pair : (null, null);

    internal void Add(string relative, Difference difference, CompareEntry? left = null, CompareEntry? right = null)
    {
        _items[relative] = difference;
        _entries[relative] = (left, right);
    }

    /// <summary>
    /// Top-level names to mark on one side. A file is marked when it is only here, newer here, different or of the wrong
    /// type; a folder is marked when it is only here, of the wrong type, or when anything below it differs and
    /// it would be copied from here (something below is only here or newer here). Returns the names as they appear in the panel.
    /// </summary>
    public HashSet<string> MarkedOn(bool left)
    {
        var only = left ? Difference.OnlyLeft : Difference.OnlyRight;
        var newer = left ? Difference.LeftNewer : Difference.RightNewer;
        var marked = new HashSet<string>(PathComparer);
        foreach (var (path, difference) in _items)
        {
            if (difference != only && difference != newer && difference != Difference.Different && difference != Difference.TypeMismatch)
            {
                continue;
            }

            var slash = path.IndexOf('\\', StringComparison.Ordinal);
            marked.Add(slash < 0 ? path : path[..slash]);
        }

        return marked;
    }
}

/// <summary>One side of a comparison: a folder on disk or on a server.</summary>
public interface ICompareSide
{
    /// <summary>The content of a folder below the compared one ("" = the folder itself, "a\\b" deeper). Unreadable = empty.</summary>
    Task<IReadOnlyList<CompareEntry>> ListAsync(string relative, CancellationToken cancellationToken);

    /// <summary>
    /// The exact time of a file when the listing may round it (FTP shows minutes, or only the day for older
    /// files); null when the listing is already exact or the side cannot tell better.
    /// </summary>
    Task<DateTimeOffset?> ExactModifiedAsync(string relative, CancellationToken cancellationToken);

    /// <summary>The file's content, for "compare by content".</summary>
    Task<Stream> OpenReadAsync(string relative, CancellationToken cancellationToken);
}

/// <summary>A folder on disk.</summary>
public sealed class DiskCompareSide(string root) : ICompareSide
{
    private readonly string _root = ExactPath.Native(root);

    public Task<IReadOnlyList<CompareEntry>> ListAsync(string relative, CancellationToken cancellationToken)
    {
        var list = new List<CompareEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(Full(relative)).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None }))
            {
                list.Add(new CompareEntry(
                    info.Name,
                    info is DirectoryInfo,
                    info is FileInfo f ? f.Length : 0,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable side counts as empty: everything on the other side shows as "only there"
        }

        return Task.FromResult<IReadOnlyList<CompareEntry>>(list);
    }

    public Task<DateTimeOffset?> ExactModifiedAsync(string relative, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);

    public Task<Stream> OpenReadAsync(string relative, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new FileStream(Full(relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous));

    private string Full(string relative) => relative.Length == 0 ? _root : Path.Combine(_root, relative.Replace('\\', Path.DirectorySeparatorChar));
}

public static class DirectoryCompare
{
    /// <summary>Compares two folders on disk. Links are not followed; folders either side cannot read are treated as empty.</summary>
    public static CompareResult Run(string left, string right, CompareOptions options, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        return RunAsync(new DiskCompareSide(left), new DiskCompareSide(right), options, token: token).GetAwaiter().GetResult();
    }

    /// <summary>Compares two sides, each on disk or on a server. <paramref name="progress"/> hears of every folder entered.</summary>
    public static async Task<CompareResult> RunAsync(ICompareSide left, ICompareSide right, CompareOptions options, IProgress<CompareProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(options);
        var result = new CompareResult();
        var walk = new Walk(left, right, options, result, progress, token);
        await walk.FolderAsync(string.Empty).ConfigureAwait(false);
        return result;
    }

    private sealed class Walk(ICompareSide leftSide, ICompareSide rightSide, CompareOptions options, CompareResult result, IProgress<CompareProgress>? progress, CancellationToken token)
    {
        private int _folders;

        public async Task FolderAsync(string prefix)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new CompareProgress(prefix, ++_folders, result.Count));
            await WalkAsync(leftSide, rightSide, prefix, options, result, FolderAsync, token).ConfigureAwait(false);
        }
    }

    private static async Task WalkAsync(ICompareSide leftSide, ICompareSide rightSide, string prefix, CompareOptions options, CompareResult result, Func<string, Task> subfolder, CancellationToken token)
    {
        var leftItems = ByName(await leftSide.ListAsync(prefix, token).ConfigureAwait(false));
        var rightItems = ByName(await rightSide.ListAsync(prefix, token).ConfigureAwait(false));
        foreach (var (name, l) in leftItems)
        {
            var relative = prefix.Length == 0 ? name : prefix + "\\" + name;
            if (!rightItems.TryGetValue(name, out var r))
            {
                result.Add(relative, Difference.OnlyLeft, l, null);
                continue;
            }

            if (l.IsDirectory && r.IsDirectory)
            {
                if (options.Subfolders && !l.IsLink && !r.IsLink)
                {
                    await subfolder(relative).ConfigureAwait(false);
                }

                continue;
            }

            if (!l.IsDirectory && !r.IsDirectory)
            {
                var exactLeft = await Exact(leftSide, relative, l, token).ConfigureAwait(false);
                var exactRight = await Exact(rightSide, relative, r, token).ConfigureAwait(false);
                if (await CompareAsync(leftSide, rightSide, relative, exactLeft, exactRight, options.ByContent, token).ConfigureAwait(false) is { } difference)
                {
                    result.Add(relative, difference, exactLeft, exactRight);
                }

                continue;
            }

            result.Add(relative, Difference.TypeMismatch, l, r);
        }

        foreach (var (name, r) in rightItems.Where(i => !leftItems.ContainsKey(i.Key)))
        {
            result.Add(prefix.Length == 0 ? name : prefix + "\\" + name, Difference.OnlyRight, null, r);
        }
    }

    private static Dictionary<string, CompareEntry> ByName(IReadOnlyList<CompareEntry> entries)
    {
        var map = new Dictionary<string, CompareEntry>(CompareResult.PathComparer);
        foreach (var entry in entries)
        {
            map[entry.Name] = entry;
        }

        return map;
    }

    private static async Task<CompareEntry> Exact(ICompareSide side, string relative, CompareEntry entry, CancellationToken token) =>
        await side.ExactModifiedAsync(relative, token).ConfigureAwait(false) is { } exact ? entry with { Modified = exact } : entry;

    private static async Task<Difference?> CompareAsync(ICompareSide leftSide, ICompareSide rightSide, string relative, CompareEntry left, CompareEntry right, bool byContent, CancellationToken token)
    {
        var delta = left.Modified - right.Modified;
        var sameTime = delta.Duration() <= CompareOptions.TimeTolerance;
        if (left.Size == right.Size)
        {
            if (sameTime)
            {
                return null;
            }

            // Same size, different time: without the content check the newer one is the "different" one.
            if (byContent && await SameContentAsync(leftSide, rightSide, relative, token).ConfigureAwait(false))
            {
                return null;
            }

            return delta > TimeSpan.Zero ? Difference.LeftNewer : Difference.RightNewer;
        }

        if (sameTime)
        {
            return Difference.Different;
        }

        return delta > TimeSpan.Zero ? Difference.LeftNewer : Difference.RightNewer;
    }

    private static async Task<bool> SameContentAsync(ICompareSide leftSide, ICompareSide rightSide, string relative, CancellationToken token)
    {
        try
        {
            var sa = await leftSide.OpenReadAsync(relative, token).ConfigureAwait(false);
            await using (sa.ConfigureAwait(false))
            {
                var sb = await rightSide.OpenReadAsync(relative, token).ConfigureAwait(false);
                await using (sb.ConfigureAwait(false))
                {
                    var bufferA = new byte[1 << 16];
                    var bufferB = new byte[1 << 16];
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var readA = await sa.ReadAtLeastAsync(bufferA, bufferA.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
                        var readB = await sb.ReadAtLeastAsync(bufferB, bufferB.Length, throwOnEndOfStream: false, token).ConfigureAwait(false);
                        if (readA != readB || !bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                        {
                            return false;
                        }

                        if (readA == 0)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // cannot prove they are equal
        }
    }
}
