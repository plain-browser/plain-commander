namespace PlainCommander.Core.Operations;

/// <summary>One file or folder found under an operation's source, in pre-order (folders before content).</summary>
internal sealed record PlannedItem(
    string Source,
    string Relative,
    bool IsDirectory,
    long Size,
    bool IsLink,
    FileAttributes Attributes,
    Exception? ScanError = null);

/// <summary>
/// Walks a source tree. Links/junctions are either followed with loop protection (copy) or never
/// entered (delete, move, size) — deleting "through" a junction would destroy its target.
/// Do not replace this with EnumerationOptions.RecurseSubdirectories: on Windows it follows directory
/// links and never ends on a link that points to a parent folder.
/// </summary>
internal static class TreeScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public static List<PlannedItem> Scan(string source, bool followLinks, CancellationToken cancellationToken)
    {
        var items = new List<PlannedItem>();
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var info = new DirectoryInfo(source);
        if (info.Exists)
        {
            Walk(info, name, followLinks, new HashSet<string>(PathComparer), items, cancellationToken);
        }
        else
        {
            var file = new FileInfo(source);
            if (!file.Exists)
            {
                throw new FileNotFoundException(null, source);
            }

            items.Add(new PlannedItem(source, name, false, file.Length, IsLink(file), file.Attributes));
        }

        return items;
    }

    /// <summary>Total bytes, files and folders under a folder, not entering links.</summary>
    public static (long Bytes, int Files, int Directories) Measure(string directory, CancellationToken cancellationToken)
    {
        long bytes = 0;
        int files = 0, dirs = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(directory));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos("*", Options).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue; // what we can see is still a useful answer
            }

            foreach (var child in children)
            {
                if (child is DirectoryInfo d)
                {
                    dirs++;
                    if (!IsLink(d))
                    {
                        stack.Push(d);
                    }
                }
                else
                {
                    files++;
                    bytes += ((FileInfo)child).Length;
                }
            }
        }

        return (bytes, files, dirs);
    }

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static void Walk(
        DirectoryInfo dir,
        string relative,
        bool followLinks,
        HashSet<string> visited,
        List<PlannedItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var isLink = IsLink(dir);
        items.Add(new PlannedItem(dir.FullName, relative, true, 0, isLink, dir.Attributes));
        if (isLink && !followLinks)
        {
            return;
        }

        if (!visited.Add(RealPath(dir)))
        {
            return; // a link back into a folder we are already copying: stop the loop here
        }

        List<FileSystemInfo> children;
        try
        {
            children = dir.EnumerateFileSystemInfos("*", Options).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            items[^1] = items[^1] with { ScanError = ex };
            return;
        }

        foreach (var child in children)
        {
            var childRelative = Path.Combine(relative, child.Name);
            if (child is DirectoryInfo d)
            {
                Walk(d, childRelative, followLinks, visited, items, cancellationToken);
            }
            else
            {
                items.Add(new PlannedItem(child.FullName, childRelative, false, ((FileInfo)child).Length, IsLink(child), child.Attributes));
            }
        }
    }

    private static string RealPath(DirectoryInfo dir)
    {
        if (!IsLink(dir))
        {
            return dir.FullName;
        }

        try
        {
            return dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir.FullName;
        }
        catch (IOException)
        {
            return dir.FullName;
        }
    }
}
