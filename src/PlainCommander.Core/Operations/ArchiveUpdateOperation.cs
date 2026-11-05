using PlainCommander.Core.Archives;
using PlainCommander.Core.Archives.SevenZip;
using PlainCommander.Core.FileSystem;
using SharpCompress.Archives;
using SharpCompress.Readers;
using SharpCompress.Writers;

namespace PlainCommander.Core.Operations;

/// <summary>What to change inside an archive. Inner paths use "/" and no leading slash, like the catalog.</summary>
public sealed record ArchiveEdit
{
    private ArchiveEdit()
    {
    }

    public IReadOnlyList<string> Delete { get; private init; } = [];

    public IReadOnlyList<string> AddSources { get; private init; } = [];

    public string AddInto { get; private init; } = string.Empty;

    public (string Inner, string NewName)? Rename { get; private init; }

    public string? NewFolder { get; private init; }

    /// <summary>F8 / Shift+Del inside an archive: entries and everything below folders.</summary>
    public static ArchiveEdit Remove(IReadOnlyList<string> inners) => new() { Delete = inners };

    /// <summary>F5/F6 into an archive panel: files and folders from disk go below <paramref name="into"/>.</summary>
    public static ArchiveEdit Add(IReadOnlyList<string> diskSources, string into) => new() { AddSources = diskSources, AddInto = into };

    /// <summary>Shift+F6 inside an archive; a folder takes everything below it along.</summary>
    public static ArchiveEdit RenameEntry(string inner, string newName) => new() { Rename = (inner, newName) };

    /// <summary>F7 inside an archive.</summary>
    public static ArchiveEdit MakeFolder(string inner) => new() { NewFolder = inner };
}

/// <summary>
/// Changes an existing zip, tar, tar.gz or 7z: the archive is rewritten next to itself (".part") with entries left out,
/// renamed or added, and only a complete new archive replaces the old one, so a failure or Cancel changes nothing.
/// 7z goes through 7-Zip, which copies kept entries without recompressing them (unless their solid block changes);
/// rar, iso and the rest stay read-only. Time stamps of untouched entries are kept.
/// </summary>
public sealed class ArchiveUpdateOperation : IOperation
{
    private readonly string _archive;
    private readonly ArchiveEdit _edit;
    private readonly bool _moveSources;

    /// <param name="moveSources">F6: delete the added files from disk once the archive is safely replaced.</param>
    public ArchiveUpdateOperation(string archiveFile, ArchiveEdit edit, bool moveSources = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveFile);
        _archive = archiveFile;
        _edit = edit ?? throw new ArgumentNullException(nameof(edit));
        _moveSources = moveSources;
    }

    public OperationKind Kind => OperationKind.ArchiveUpdate;

    /// <summary>Zip, tar, tar.gz and 7z can be changed; everything else the panels can open is read-only.</summary>
    public static bool CanEdit(string archiveFile) => ArchivePackOperation.CanWrite(archiveFile);

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        (SharpCompress.Common.ArchiveType Type, SharpCompress.Common.CompressionType Compression) format = default;
        ArchiveCatalog catalog = null!;
        var deleted = _edit.Delete.Select(ArchivePath.NormalizeKey).ToList();
        string? renameFrom = null, renameTo = null;
        var newFolder = _edit.NewFolder is { } f ? ArchivePath.NormalizeKey(f) : null;

        // Checks go through the error prompt like everything else: a read-only format or a taken name is explained, not thrown.
        var valid = await runner.Run(_archive, () =>
        {
            if (!CanEdit(_archive))
            {
                throw new OperationRuleException(RuleViolation.UnsupportedArchive, _archive);
            }

            format = ArchivePackOperation.FormatOf(_archive) ?? default;
            catalog = ArchiveCatalog.Open(_archive);
            if (_edit.Rename is { } rename)
            {
                renameFrom = ArchivePath.NormalizeKey(rename.Inner);
                var parent = ArchivePath.ParentOf(renameFrom);
                renameTo = parent.Length == 0 ? rename.NewName : parent + "/" + rename.NewName;
                if (catalog.Find(renameTo) is not null && !string.Equals(renameTo, renameFrom, StringComparison.OrdinalIgnoreCase))
                {
                    throw new OperationRuleException(RuleViolation.AlreadyExists, ArchivePath.Join(_archive, renameTo));
                }
            }

            if (newFolder is not null && catalog.Find(newFolder) is not null)
            {
                throw new OperationRuleException(RuleViolation.AlreadyExists, ArchivePath.Join(_archive, newFolder));
            }
        }).ConfigureAwait(false);
        if (!valid)
        {
            return;
        }

        // Additions: scan, then settle every name that already exists before anything is written.
        var additions = new List<(PlannedItem Item, string Key)>();
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ConflictChoice? sticky = null;
        var into = ArchivePath.NormalizeKey(_edit.AddInto);
        foreach (var source in _edit.AddSources)
        {
            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            if (PathRules.Same(full, _archive))
            {
                throw new OperationRuleException(RuleViolation.IntoItself, source);
            }

            List<PlannedItem>? scanned = null;
            await runner.Run(full, () => scanned = TreeScanner.Scan(full, followLinks: false, runner.Token)).ConfigureAwait(false);
            foreach (var item in scanned ?? [])
            {
                if (item.IsDirectory && item.IsLink)
                {
                    continue;
                }

                var relative = item.Relative.Replace(Path.DirectorySeparatorChar, '/');
                var key = into.Length == 0 ? relative : into + "/" + relative;
                var existing = catalog.Find(key);
                if (existing is null)
                {
                    additions.Add((item, key));
                    continue;
                }

                if (item.IsDirectory)
                {
                    continue; // the folder is there already: its new content merges into it
                }

                if (existing.IsDirectory)
                {
                    throw new OperationRuleException(RuleViolation.TypeMismatch, ArchivePath.Join(_archive, key));
                }

                var choice = sticky ?? await AskAsync(context, runner, item, existing, key).ConfigureAwait(false);
                if (choice is ConflictChoice.OverwriteAll or ConflictChoice.SkipAll or ConflictChoice.KeepBothAll or ConflictChoice.OverwriteAllOlder)
                {
                    sticky = choice;
                }

                switch (choice)
                {
                    case ConflictChoice.Overwrite or ConflictChoice.OverwriteAll:
                        replaced.Add(key);
                        additions.Add((item, key));
                        break;
                    case ConflictChoice.OverwriteAllOlder:
                        if (existing.Modified < File.GetLastWriteTimeUtc(item.Source))
                        {
                            replaced.Add(key);
                            additions.Add((item, key));
                        }
                        else
                        {
                            runner.AddSkipped();
                        }

                        break;
                    case ConflictChoice.KeepBoth or ConflictChoice.KeepBothAll:
                        additions.Add((item, FreeKey(catalog, key)));
                        break;
                    case ConflictChoice.Skip or ConflictChoice.SkipAll:
                        runner.AddSkipped();
                        break;
                    default:
                        throw new OperationAbortedException("Cancelled at a conflict.");
                }
            }
        }

        // 7-Zip copies kept entries as they are and only reports the new files.
        // An encrypted zip changes through 7-Zip too: the managed writer could not read (or re-encrypt) its entries.
        var sevenZip = ArchivePackOperation.IsSevenZip(_archive) || (ArchivePackOperation.IsZip(_archive) && IsEncryptedZip(_archive));
        var addedBytes = additions.Where(a => !a.Item.IsDirectory).Sum(a => a.Item.Size);
        var tracker = sevenZip
            ? new ProgressTracker(context.Progress, addedBytes, additions.Count)
            : new ProgressTracker(context.Progress, catalog.FilesUnder(string.Empty).Sum(n => n.Size) + addedBytes, catalog.Count + additions.Count);
        tracker.Report(force: true);

        bool Dropped(string key) =>
            replaced.Contains(key) || deleted.Any(d => key.Equals(d, StringComparison.OrdinalIgnoreCase) || key.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase));

        string Renamed(string key) =>
            renameFrom is null ? key
            : key.Equals(renameFrom, StringComparison.OrdinalIgnoreCase) ? renameTo!
            : key.StartsWith(renameFrom + "/", StringComparison.OrdinalIgnoreCase) ? renameTo + key[renameFrom.Length..]
            : key;

        var rewritten = sevenZip
            ? await runner.Run(_archive, () => RewriteSevenZip(runner, tracker, additions, newFolder, Dropped, Renamed)).ConfigureAwait(false)
            : await runner.RunAsync(_archive, () => RewriteManagedAsync(format, runner, tracker, additions, newFolder, Dropped, Renamed)).ConfigureAwait(false);

        if (!rewritten)
        {
            return;
        }

        runner.AddDone(Math.Max(1, additions.Count + deleted.Count + (renameFrom is null ? 0 : 1) + (newFolder is null ? 0 : 1)));
        if (_moveSources && runner.Skipped == 0)
        {
            var removal = await new DeleteOperation(_edit.AddSources).RunAsync(context).ConfigureAwait(false);
            if (removal.Outcome != OperationOutcome.Completed)
            {
                runner.AddSkipped();
            }
        }

        tracker.Report(force: true);
    }

    /// <summary>Zip and tar: every kept entry is read and written again into the new archive.</summary>
    private async Task RewriteManagedAsync(
        (SharpCompress.Common.ArchiveType Type, SharpCompress.Common.CompressionType Compression) format,
        StepRunner runner,
        ProgressTracker tracker,
        List<(PlannedItem Item, string Key)> additions,
        string? newFolder,
        Func<string, bool> dropped,
        Func<string, string> renamed)
    {
        var temp = _archive + ".part";
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = WriterFactory.Open(output, format.Type, ArchivePackOperation.Options(format)))
            {
                foreach (var (key, isDirectory, modified, size, open) in Entries(_archive))
                {
                    runner.Token.ThrowIfCancellationRequested();
                    tracker.StartItem(ArchivePath.Join(_archive, key), 0);
                    if (!dropped(key))
                    {
                        if (isDirectory)
                        {
                            writer.WriteDirectory(renamed(key), modified);
                        }
                        else
                        {
                            using var stream = open();
                            Write(writer, renamed(key), stream, modified, size);
                        }
                    }

                    tracker.FinishItem();
                }

                if (newFolder is not null)
                {
                    writer.WriteDirectory(newFolder, DateTime.Now);
                }

                foreach (var (item, key) in additions)
                {
                    runner.Token.ThrowIfCancellationRequested();
                    tracker.StartItem(item.Source, item.Size);
                    if (item.IsDirectory)
                    {
                        writer.WriteDirectory(key, Directory.GetLastWriteTime(item.Source));
                    }
                    else
                    {
                        await using var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                        Write(writer, key, input, File.GetLastWriteTime(item.Source), input.Length);
                    }

                    tracker.FinishItem();
                }
            }

            File.Move(temp, _archive, overwrite: true);
            ArchiveCatalog.Forget(ExactPath.Display(_archive));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp); // never leave half an archive behind; the original is untouched
            }
        }
    }

    private static bool IsEncryptedZip(string archive)
    {
        if (!SevenZipWriter.CanWriteZip)
        {
            return false;
        }

        try
        {
            using var opened = SevenZipArchive.Open(archive);
            return opened.Items.Any(static i => i.Encrypted);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return false;
        }
    }

    private void RewriteSevenZip(StepRunner runner, ProgressTracker tracker, List<(PlannedItem Item, string Key)> additions, string? newFolder, Func<string, bool> dropped, Func<string, string> renamed)
    {
        var temp = _archive + ".part";
        try
        {
            using (var archive = SevenZipArchive.Open(_archive))
            {
                var kept = new List<SevenZipKeptItem>();
                foreach (var item in archive.Items)
                {
                    if (dropped(item.Key))
                    {
                        continue;
                    }

                    var key = renamed(item.Key);
                    kept.Add(new SevenZipKeptItem(item.Index, string.Equals(key, item.Key, StringComparison.Ordinal) ? null : key));
                }

                var added = new List<SevenZipNewItem>();
                if (newFolder is not null)
                {
                    added.Add(new SevenZipNewItem(newFolder, true, null, 0, DateTime.UtcNow, FileAttributes.Directory));
                }

                foreach (var (item, key) in additions)
                {
                    added.Add(new SevenZipNewItem(
                        key,
                        item.IsDirectory,
                        item.IsDirectory ? null : item.Source,
                        item.IsDirectory ? 0 : item.Size,
                        item.IsDirectory ? Directory.GetLastWriteTimeUtc(item.Source) : File.GetLastWriteTimeUtc(item.Source),
                        item.Attributes));
                }

                SevenZipWriter.Update(archive, temp, kept, added, new SevenZipProgress(tracker, runner));
            }

            File.Move(temp, _archive, overwrite: true);
            ArchiveCatalog.Forget(ExactPath.Display(_archive));
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp); // never leave half an archive behind; the original is untouched
            }
        }
    }

    /// <summary>Tar needs the size up front when the content comes from a stream that cannot seek (an entry being rewritten).</summary>
    private static void Write(IWriter writer, string key, Stream content, DateTime? modified, long size)
    {
        if (writer is SharpCompress.Writers.Tar.TarWriter tar)
        {
            tar.Write(key, content, modified, size);
        }
        else
        {
            writer.Write(key, content, modified);
        }
    }

    private static async Task<ConflictChoice> AskAsync(OperationContext context, StepRunner runner, PlannedItem item, ArchiveNode existing, string key)
    {
        await runner.PromptGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Prompts.ResolveConflictAsync(
                new ConflictInfo(item.Source, item.Size, File.GetLastWriteTimeUtc(item.Source), key, existing.Size, existing.Modified),
                context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runner.PromptGate.Release();
        }
    }

    private static string FreeKey(ArchiveCatalog catalog, string key)
    {
        var folder = ArchivePath.ParentOf(key);
        var name = key[(key.LastIndexOf('/') + 1)..];
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var n = 2; ; n++)
        {
            var candidate = (folder.Length == 0 ? string.Empty : folder + "/") + $"{stem} ({n}){ext}";
            if (catalog.Find(candidate) is null)
            {
                return candidate;
            }
        }
    }

    /// <summary>Every real entry of the archive, front to back, with a way to open its content.</summary>
    private static IEnumerable<(string Key, bool IsDirectory, DateTime? Modified, long Size, Func<Stream> Open)> Entries(string archive)
    {
        if (ArchiveCatalog.IsStreamOnly(archive))
        {
            using var stream = File.OpenRead(archive);
            using var reader = ReaderFactory.Open(stream);
            while (reader.MoveToNextEntry())
            {
                var key = ArchivePath.NormalizeKey(reader.Entry.Key);
                if (key.Length > 0)
                {
                    yield return (key, reader.Entry.IsDirectory, reader.Entry.LastModifiedTime, reader.Entry.Size, reader.OpenEntryStream);
                }
            }
        }
        else
        {
            using var opened = ArchiveFactory.Open(archive);
            foreach (var entry in opened.Entries)
            {
                var key = ArchivePath.NormalizeKey(entry.Key);
                if (key.Length > 0)
                {
                    yield return (key, entry.IsDirectory, entry.LastModifiedTime, entry.Size, entry.OpenEntryStream);
                }
            }
        }
    }
}
