using System.Text;

namespace PlainCommander.Presentation.Tests.Monkey;

/// <summary>
/// The disk the monkey plays on: <c>world</c> is where everything happens; <c>sentinel</c> next to it must never
/// change; <c>bin</c> is the fake Recycle Bin. Every file the harness writes starts with a unique id line, so lost
/// files can be told apart from moved or copied ones.
/// </summary>
internal sealed class MonkeyWorld : IDisposable
{
    private const string IdPrefix = "PCID:";
    private int _nextId;
    private int _nextName;

    public MonkeyWorld()
    {
        Root = Path.TrimEndingDirectorySeparator(Directory.CreateTempSubdirectory("pc-monkey-").FullName);
        World = Directory.CreateDirectory(Path.Combine(Root, "world")).FullName;
        Sentinel = Directory.CreateDirectory(Path.Combine(Root, "sentinel")).FullName;
        Bin = Directory.CreateDirectory(Path.Combine(Root, "bin")).FullName;
        SettingsFile = Path.Combine(Root, "settings", "settings.json");
        File.WriteAllText(Path.Combine(Sentinel, "keep.txt"), "do not touch");
        Directory.CreateDirectory(Path.Combine(Sentinel, "inner"));
        File.WriteAllText(Path.Combine(Sentinel, "inner", "deep.txt"), "do not touch either");
    }

    public string Root { get; }

    public string World { get; }

    public string Sentinel { get; }

    public string Bin { get; }

    public string SettingsFile { get; }

    public static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer NameComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                Thread.Sleep(50); // a cancelled copy on Windows can hold a handle for a moment
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>A starting tree drawn from the seed: a few folders, small files and one larger one.</summary>
    public void Build(Random rng)
    {
        string[] tops = ["alpha", "beta", "gamma docs", "delta"];
        foreach (var top in tops.Take(2 + rng.Next(3)))
        {
            var dir = Directory.CreateDirectory(Path.Combine(World, top)).FullName;
            for (var i = rng.Next(4); i > 0; i--)
            {
                WriteFile(Path.Combine(dir, NewName(rng.Next(), file: true)), rng.Next(3) == 0 ? 0 : rng.Next(1, 4096));
            }

            for (var i = rng.Next(3); i > 0; i--)
            {
                var sub = Directory.CreateDirectory(Path.Combine(dir, NewName(rng.Next(), file: false))).FullName;
                for (var j = rng.Next(3); j > 0; j--)
                {
                    WriteFile(Path.Combine(sub, NewName(rng.Next(), file: true)), rng.Next(1, 2048));
                }
            }
        }

        for (var i = 2 + rng.Next(4); i > 0; i--)
        {
            WriteFile(Path.Combine(World, NewName(rng.Next(), file: true)), rng.Next(1, 1024));
        }

        WriteFile(Path.Combine(World, "big.bin"), 2 * 1024 * 1024 + rng.Next(1024)); // copies of it take a moment
    }

    private static readonly string[] Stems = ["a", "B", "report", "x y", "data", "Zeta", "notes", "ąę", "img"];
    private static readonly string[] Extensions = [".txt", ".TXT", ".bin", ".log", "", ".tar.gz.txt", ".md"];

    /// <summary>A fresh name (never a case-variant of an earlier one, so Windows and Linux see the same tree).</summary>
    public string NewName(int draw, bool file)
    {
        var n = Interlocked.Increment(ref _nextName);
        var stem = Stems[(uint)draw % Stems.Length];
        return file ? $"{stem}{n}{Extensions[(uint)draw / 7 % Extensions.Length]}" : $"{stem}{n} dir";
    }

    public string WriteFile(string path, int size)
    {
        var id = Interlocked.Increment(ref _nextId);
        WriteWithId(path, $"{id:D6}", size);
        return path;
    }

    private static void WriteWithId(string path, string id, int size)
    {
        var header = Encoding.ASCII.GetBytes(IdPrefix + id + "\n");
        var bytes = new byte[Math.Max(size, header.Length)];
        header.CopyTo(bytes, 0);
        for (var i = header.Length; i < bytes.Length; i++)
        {
            bytes[i] = (byte)('a' + (i % 26));
        }

        File.WriteAllBytes(path, bytes);
    }

    public static string? IdOf(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[IdPrefix.Length + 7];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            return text.StartsWith(IdPrefix, StringComparison.Ordinal) && read == buffer.Length ? text[IdPrefix.Length..^1] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Ids found in files under <paramref name="path"/> (a file or a folder).</summary>
    public static HashSet<string> IdsUnder(string path)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            if (IdOf(path) is { } id)
            {
                ids.Add(id);
            }

            return ids;
        }

        if (!Directory.Exists(path))
        {
            return ids;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                if (IdOf(file) is { } id)
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ids;
    }

    /// <summary>Every id that still exists, in the world or in the bin.</summary>
    public HashSet<string> AllIds()
    {
        var ids = IdsUnder(World);
        ids.UnionWith(IdsUnder(Bin));
        return ids;
    }

    /// <summary>Everything under the world, folders and files, in a stable order.</summary>
    public List<string> Entries(bool? directories = null)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            IEnumerable<string> all = directories switch
            {
                true => Directory.EnumerateDirectories(World, "*", options),
                false => Directory.EnumerateFiles(World, "*", options),
                null => Directory.EnumerateFileSystemEntries(World, "*", options),
            };
            var list = all.ToList();
            list.Sort(StringComparer.Ordinal);
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool Inside(string? path) =>
        !string.IsNullOrEmpty(path)
        && (string.Equals(Path.TrimEndingDirectorySeparator(path), World, PathComparison)
            || path.StartsWith(World + Path.DirectorySeparatorChar, PathComparison));

    public static bool IsUnder(string path, string folder) =>
        string.Equals(path, folder, PathComparison) || path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>Names, sizes and content of the sentinel plus the names next to the world: must never change.</summary>
    public string OutsideSnapshot()
    {
        var sb = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            sb.Append("root: ").AppendLine(entry);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(Sentinel, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sb.Append(Path.GetRelativePath(Sentinel, entry));
            if (File.Exists(entry))
            {
                sb.Append(" = ").Append(File.ReadAllText(entry));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string Rel(string? path) =>
        path is null ? "(none)"
        : Inside(path) ? "~" + path[World.Length..].Replace('\\', '/')
        : IsUnder(path, Root) ? "<root>" + path[Root.Length..].Replace('\\', '/')
        : path;

    // ---- changes made "by another program" ----

    /// <summary>
    /// One change on disk chosen by <paramref name="kind"/> and the draws. Paths under <paramref name="protect"/> are
    /// left alone except for new files. Returns what was done and the ids it destroyed on purpose.
    /// </summary>
    public (string What, HashSet<string> Destroyed) Change(int kind, int a, int b, IReadOnlyList<string> panelFolders, IReadOnlyList<string> protect)
    {
        var destroyed = new HashSet<string>(StringComparer.Ordinal);
        bool Touchable(string p) => !protect.Any(x => IsUnder(p, x) || IsUnder(x, p));

        string? Pick(bool? dirs, bool needTouchable = true)
        {
            var list = Entries(dirs).Where(p => !needTouchable || Touchable(p)).ToList();
            return list.Count == 0 ? null : list[(int)((uint)a % list.Count)];
        }

        string Folder()
        {
            var dirs = Entries(directories: true);
            dirs.Insert(0, World);
            return dirs[(int)((uint)a % dirs.Count)];
        }

        try
        {
            switch ((uint)kind % 12)
            {
                case 0:
                case 1:
                    {
                        var path = Path.Combine(Folder(), NewName(b, file: true));
                        WriteFile(path, (int)((uint)b % 5000));
                        return ("create file " + Rel(path), destroyed);
                    }

                case 2:
                    {
                        var path = Path.Combine(Folder(), NewName(b, file: false));
                        Directory.CreateDirectory(path);
                        return ("create folder " + Rel(path), destroyed);
                    }

                case 3:
                    {
                        if (Pick(dirs: false) is not { } file)
                        {
                            return ("delete file: none", destroyed);
                        }

                        destroyed.UnionWith(IdsUnder(file));
                        File.Delete(file);
                        return ("delete file " + Rel(file), destroyed);
                    }

                case 4:
                    {
                        if (Pick(dirs: true) is not { } dir)
                        {
                            return ("delete folder: none", destroyed);
                        }

                        destroyed.UnionWith(IdsUnder(dir));
                        Directory.Delete(dir, recursive: true);
                        return ("delete folder " + Rel(dir), destroyed);
                    }

                case 5:
                case 6:
                    {
                        // the folder a panel shows, or (6) its parent
                        var panel = panelFolders.Count == 0 ? null : panelFolders[(int)((uint)b % panelFolders.Count)];
                        var dir = panel;
                        if (kind % 12 == 6 && dir is not null)
                        {
                            dir = Path.GetDirectoryName(dir);
                        }

                        if (dir is null || !Inside(dir) || string.Equals(dir, World, PathComparison) || !Directory.Exists(dir) || !Touchable(dir))
                        {
                            return ("delete panel folder: not possible", destroyed);
                        }

                        destroyed.UnionWith(IdsUnder(dir));
                        Directory.Delete(dir, recursive: true);
                        return ((kind % 12 == 6 ? "delete the parent of a panel's folder " : "delete a panel's folder ") + Rel(dir), destroyed);
                    }

                case 7:
                    {
                        if (Pick(dirs: null) is not { } entry)
                        {
                            return ("rename: none", destroyed);
                        }

                        var isDir = Directory.Exists(entry);
                        var target = Path.Combine(Path.GetDirectoryName(entry)!, NewName(b, file: !isDir));
                        if (isDir)
                        {
                            Directory.Move(entry, target);
                        }
                        else
                        {
                            File.Move(entry, target);
                        }

                        return ($"rename {Rel(entry)} -> {Path.GetFileName(target)}", destroyed);
                    }

                case 8:
                    {
                        // a folder replaced by a file of the same name (or the panel's folder, when b says so)
                        var dir = b % 2 == 0 && panelFolders.FirstOrDefault(p => Inside(p) && !string.Equals(p, World, PathComparison) && Directory.Exists(p) && Touchable(p)) is { } shown
                            ? shown
                            : Pick(dirs: true);
                        if (dir is null)
                        {
                            return ("folder to file: none", destroyed);
                        }

                        destroyed.UnionWith(IdsUnder(dir));
                        Directory.Delete(dir, recursive: true);
                        WriteFile(dir, 10);
                        return ("replace folder with file " + Rel(dir), destroyed);
                    }

                case 9:
                    {
                        if (Pick(dirs: false) is not { } file)
                        {
                            return ("file to folder: none", destroyed);
                        }

                        destroyed.UnionWith(IdsUnder(file));
                        File.Delete(file);
                        Directory.CreateDirectory(file);
                        return ("replace file with folder " + Rel(file), destroyed);
                    }

                case 10:
                    {
                        if (Pick(dirs: false) is not { } file)
                        {
                            return ("resize: none", destroyed);
                        }

                        var id = IdOf(file);
                        if (id is null)
                        {
                            File.WriteAllBytes(file, Enumerable.Repeat((byte)'z', (int)((uint)b % 3000)).ToArray()); // not zeros: those make an empty tar
                        }
                        else
                        {
                            WriteWithId(file, id, (int)((uint)b % 6000));
                        }

                        return ("resize " + Rel(file), destroyed);
                    }

                default:
                    {
                        if (Pick(dirs: null) is not { } entry)
                        {
                            return ("touch: none", destroyed);
                        }

                        var when = new DateTime(2001 + (int)((uint)b % 30), 1 + (int)((uint)b % 12), 3, 4, 5, 6, DateTimeKind.Local);
                        if (Directory.Exists(entry))
                        {
                            Directory.SetLastWriteTime(entry, when);
                        }
                        else
                        {
                            File.SetLastWriteTime(entry, when);
                        }

                        return ("touch " + Rel(entry), destroyed);
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ($"change {kind % 12} failed: {ex.GetType().Name}", destroyed); // destroyed: whatever went before the failure
        }
    }
}
