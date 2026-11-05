using System.Buffers;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Operations;

/// <summary>
/// F5 from inside an archive: writes the chosen entries (files, or folders with everything below) to a
/// disk folder, with the same conflict questions as a copy. Solid archives are read in one pass, so
/// every selected entry is decompressed once. Alt+F9 passes whole archives (their root): the content goes
/// straight into the target or into a folder named after each archive.
/// </summary>
public sealed class ArchiveExtractOperation : IOperation, Undo.IUndoable
{
    /// <summary>The folders and files this extraction created, for Ctrl+Z.</summary>
    public Undo.UndoJournal Journal { get; } = new();

    private const int BufferSize = 1 << 20;
    private const int NoStickyChoice = -1;

    private readonly IReadOnlyList<string> _sources;
    private readonly TransferTarget _target;
    private readonly bool _ownFolders;
    private int _stickyConflict = NoStickyChoice;

    /// <param name="ownFolders">For whole archives: each one into a folder named after it instead of straight into the target.</param>
    public ArchiveExtractOperation(IReadOnlyList<string> sources, TransferTarget target, bool ownFolders = true)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _ownFolders = ownFolders;
    }

    /// <summary>"a.tar.gz" → "a", "b.zip" → "b": the folder an archive unpacks into.</summary>
    public static string FolderNameFor(string archiveFile)
    {
        var name = Path.GetFileName(archiveFile);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        return stem.Length == 0 ? name : stem;
    }

    public OperationKind Kind => OperationKind.Copy;

    private sealed record Plan(string Archive, ArchiveNode Node, string Destination)
    {
        public string Source => ArchivePath.Join(Archive, Node.Inner);
    }

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var plans = new List<Plan>();
        var folders = new List<string>();
        foreach (var source in _sources)
        {
            var location = ArchiveFileSystem.Locate(source) ?? throw new FileNotFoundException(null, source);
            ArchiveCatalog? opened = null;
            if (!await runner.Run(location.ArchiveFile, () => opened = ArchiveCatalog.Open(location.ArchiveFile)).ConfigureAwait(false))
            {
                continue; // damaged, encrypted without a password: the next archive still goes
            }

            var catalog = opened!;
            var isFolder = catalog.DirectoryExists(location.Inner);
            var topName = _target.NewName ?? (location.IsRoot ? (_ownFolders ? FolderNameFor(location.ArchiveFile) : string.Empty) : location.Name);
            if (isFolder)
            {
                // Folders first, by hand: an archive may hold empty folders, and the file loop never visits those.
                var top = Path.Combine(_target.Directory, topName);
                folders.Add(top);
                var cut = location.Inner.Length == 0 ? 0 : location.Inner.Length + 1;
                folders.AddRange(catalog.FoldersUnder(location.Inner).Select(n => Path.Combine(top, n.Inner[cut..].Replace('/', Path.DirectorySeparatorChar))));
            }

            foreach (var node in catalog.FilesUnder(location.Inner))
            {
                var relative = isFolder
                    ? node.Inner[(location.Inner.Length == 0 ? 0 : location.Inner.Length + 1)..].Replace('/', Path.DirectorySeparatorChar)
                    : string.Empty;
                var destination = isFolder ? Path.Combine(_target.Directory, topName, relative) : Path.Combine(_target.Directory, topName);
                plans.Add(new Plan(location.ArchiveFile, node, destination));
            }
        }

        var tracker = new ProgressTracker(context.Progress, plans.Sum(p => p.Node.Size), plans.Count + folders.Count);
        tracker.Report(force: true);

        foreach (var folder in folders)
        {
            var ok = await runner.Run(folder, () =>
            {
                var native = ExactPath.Native(folder);
                if (File.Exists(native))
                {
                    throw new OperationRuleException(RuleViolation.TypeMismatch, folder);
                }

                if (!Directory.Exists(native))
                {
                    Directory.CreateDirectory(native);
                    Journal.Created(folder, isDirectory: true);
                }
            }).ConfigureAwait(false);
            tracker.CompleteSmallItem(folder, 0);
            if (ok)
            {
                runner.AddDone();
            }
        }

        foreach (var group in plans.GroupBy(p => p.Archive, StringComparer.OrdinalIgnoreCase))
        {
            await ExtractFromAsync(context, runner, tracker, group.Key, group.ToList()).ConfigureAwait(false);
        }

        tracker.Report(force: true);
    }

    private async Task ExtractFromAsync(OperationContext context, StepRunner runner, ProgressTracker tracker, string archiveFile, List<Plan> plans)
    {
        var byKey = plans.ToDictionary(p => p.Node.Inner, StringComparer.OrdinalIgnoreCase);
        IArchiveEntryReader? opened = null;
        if (!await runner.Run(archiveFile, () => opened = ArchiveReaders.Open(archiveFile, byKey.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase))).ConfigureAwait(false))
        {
            return;
        }

        // One pass in archive order: a solid archive is decompressed once, whatever was chosen.
        using var reader = opened!;
        while (reader.MoveNext())
        {
            runner.Token.ThrowIfCancellationRequested();
            if (reader.IsDirectory || !byKey.TryGetValue(reader.Key, out var plan))
            {
                continue;
            }

            await ExtractOneAsync(context, runner, tracker, plan, reader.OpenEntryStream).ConfigureAwait(false);
        }
    }

    private async Task ExtractOneAsync(OperationContext context, StepRunner runner, ProgressTracker tracker, Plan plan, Func<Stream> open)
    {
        var skipped = false;
        tracker.StartItem(plan.Source, plan.Node.Size);
        var ok = await runner.RunAsync(plan.Source, async () =>
        {
            var destination = ExactPath.Native(plan.Destination);
            if (Directory.Exists(destination))
            {
                throw new OperationRuleException(RuleViolation.TypeMismatch, plan.Destination);
            }

            var replaces = false;
            if (File.Exists(destination))
            {
                var resolved = await ResolveConflictAsync(context, runner, plan, destination).ConfigureAwait(false);
                if (resolved is null)
                {
                    skipped = true;
                    return;
                }

                destination = resolved;
                replaces = File.Exists(destination); // overwrite keeps the name, "keep both" found a free one
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                using var input = open();
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), runner.Token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), runner.Token).ConfigureAwait(false);
                    tracker.AddBytes(read);
                    await runner.CheckpointAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (replaces)
            {
                Journal.Overwritten(destination);
            }
            else
            {
                Journal.Created(destination, isDirectory: false);
            }

            try
            {
                File.SetLastWriteTimeUtc(destination, plan.Node.Modified.UtcDateTime);
            }
            catch (IOException)
            {
                // the content is what matters
            }
        }).ConfigureAwait(false);

        tracker.FinishItem();
        if (skipped)
        {
            runner.AddSkipped();
        }
        else if (ok)
        {
            runner.AddDone();
        }
    }

    private async Task<string?> ResolveConflictAsync(OperationContext context, StepRunner runner, Plan plan, string destination)
    {
        var target = new FileInfo(destination);
        var choice = StickyChoice();
        if (choice is null)
        {
            await runner.PromptGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                choice = StickyChoice();
                if (choice is null)
                {
                    choice = await context.Prompts.ResolveConflictAsync(
                        new ConflictInfo(plan.Source, plan.Node.Size, plan.Node.Modified, ExactPath.Display(destination), target.Length, target.LastWriteTimeUtc),
                        context.CancellationToken).ConfigureAwait(false);
                    if (choice is ConflictChoice.OverwriteAll or ConflictChoice.OverwriteAllOlder or ConflictChoice.SkipAll or ConflictChoice.KeepBothAll)
                    {
                        Volatile.Write(ref _stickyConflict, (int)choice.Value);
                    }
                }
            }
            finally
            {
                runner.PromptGate.Release();
            }
        }

        switch (choice)
        {
            case ConflictChoice.Overwrite or ConflictChoice.OverwriteAll:
                PathRules.ClearReadOnly(destination);
                return destination;
            case ConflictChoice.OverwriteAllOlder:
                if (target.LastWriteTimeUtc >= plan.Node.Modified.UtcDateTime)
                {
                    return null;
                }

                PathRules.ClearReadOnly(destination);
                return destination;
            case ConflictChoice.KeepBoth or ConflictChoice.KeepBothAll:
                return PathRules.ReserveFreeName(destination);
            case ConflictChoice.Skip or ConflictChoice.SkipAll:
                return null;
            default:
                throw new OperationAbortedException("Cancelled at a conflict.");
        }
    }

    private ConflictChoice? StickyChoice()
    {
        var value = Volatile.Read(ref _stickyConflict);
        return value == NoStickyChoice ? null : (ConflictChoice)value;
    }

    /// <summary>F3/F4 inside an archive: one entry written to a temp file; the caller opens it and deletes it later.</summary>
    public static string ExtractToTemp(string virtualPath)
    {
        var location = ArchiveFileSystem.Locate(virtualPath) ?? throw new FileNotFoundException(null, virtualPath);
        var dir = Path.Combine(Path.GetTempPath(), "PlainCommander", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, location.Name);
        using var reader = ArchiveReaders.Open(location.ArchiveFile, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { location.Inner });
        while (reader.MoveNext())
        {
            if (!reader.IsDirectory && string.Equals(reader.Key, location.Inner, StringComparison.OrdinalIgnoreCase))
            {
                using var input = reader.OpenEntryStream();
                using var output = File.Create(ExactPath.Native(destination));
                input.CopyTo(output);
                return destination;
            }
        }

        throw new FileNotFoundException(null, virtualPath);
    }
}
