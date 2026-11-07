using System.Buffers;
using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Operations;

public enum TransferKind
{
    Copy,
    Move,
}

/// <summary>
/// F5 copy / F6 move. Moves within one drive are renames (instant); across drives, or into an existing
/// folder, items are copied and the source removed only after its copy succeeded.
/// </summary>
public sealed class TransferOperation : IOperation, IElevatable, Undo.IUndoable
{
    /// <summary>What this copy or move did, for Ctrl+Z (empty when it ran in the elevated helper).</summary>
    public Undo.UndoJournal Journal { get; } = new();

    /// <summary>Workers for small files when nothing else is configured; robocopy /MT uses 8 too.</summary>
    public const int DefaultParallelism = 8;

    private const int BufferSize = 1 << 20;

    /// <summary>Below this size the OS copy is used, in parallel, instead of streaming with progress.</summary>
    private const long SmallFileLimit = 4 << 20;

    private const int NoStickyChoice = -1;

    private readonly TransferKind _kind;
    private readonly IReadOnlyList<string> _sources;
    private readonly TransferTarget _target;
    private readonly int _parallelism;
    private readonly ConflictChoice? _presetConflict;
    private readonly bool _skipIdentical;
    private int _stickyConflict = NoStickyChoice;

    /// <param name="parallelism">Small files copied at once. 1 = strictly one after another (spinning disks).</param>
    /// <param name="presetConflict">An answer given up front for every conflict (sync uses OverwriteAll: the direction was already chosen).</param>
    /// <param name="skipIdentical">
    /// A target file with the source's size and date is taken as already done, without a question (a move then
    /// only removes the source). Used when an operation continues as administrator after "access denied".
    /// </param>
    public TransferOperation(TransferKind kind, IReadOnlyList<string> sources, TransferTarget target, int parallelism = DefaultParallelism, ConflictChoice? presetConflict = null, bool skipIdentical = false)
    {
        _presetConflict = presetConflict;
        _skipIdentical = skipIdentical;
        if (presetConflict is { } preset)
        {
            _stickyConflict = (int)preset;
        }

        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(target);
        if (target.NewName is not null && sources.Count != 1)
        {
            throw new ArgumentException("A new name needs exactly one source.", nameof(target));
        }

        _kind = kind;
        _sources = sources;
        _target = target;
        _parallelism = Math.Clamp(parallelism, 1, 64);
    }

    public OperationKind Kind => _kind == TransferKind.Copy ? OperationKind.Copy : OperationKind.Move;

    public Elevation.ElevatedJob ElevatedJob => new(
        _kind == TransferKind.Copy ? Elevation.ElevatedJobKind.Copy : Elevation.ElevatedJobKind.Move,
        _sources,
        _target.Directory,
        _target.NewName,
        _parallelism,
        _presetConflict);

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        if (!await EnsureTargetDirectoryAsync(runner).ConfigureAwait(false))
        {
            return;
        }

        var scanWatch = System.Diagnostics.Stopwatch.StartNew();
        var plans = new List<(string Source, string Destination, List<PlannedItem> Items)>();
        foreach (var source in _sources)
        {
            // Native forms from here on: names ending in a dot or space survive only behind \\?\ (ExactPath).
            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            var destination = ExactPath.Native(Path.Combine(_target.Directory, _target.NewName ?? Path.GetFileName(full)));

            if (await TryFastMoveAsync(runner, full, destination).ConfigureAwait(false))
            {
                continue;
            }

            List<PlannedItem>? items = null;
            await runner.Run(full, () =>
            {
                Validate(full, destination);
                items = TreeScanner.Scan(full, followLinks: _kind == TransferKind.Copy, runner.Token);
            }).ConfigureAwait(false);
            if (items is not null)
            {
                plans.Add((full, destination, items));
            }
        }

        Serilog.Log.Debug("Scanned {Count} items in {Elapsed} ms", plans.Sum(p => p.Items.Count), scanWatch.ElapsedMilliseconds);
        var tracker = new ProgressTracker(
            context.Progress,
            plans.Sum(p => p.Items.Sum(i => i.Size)),
            plans.Sum(p => p.Items.Count));
        tracker.Report(force: true);

        foreach (var (source, destination, items) in plans)
        {
            await TransferTreeAsync(context, runner, tracker, source, destination, items).ConfigureAwait(false);
        }

        tracker.Report(force: true);
    }

    /// <summary>A target folder that is not there yet is created, as TargetResolver promises for several sources.</summary>
    private async Task<bool> EnsureTargetDirectoryAsync(StepRunner runner)
    {
        var directory = ExactPath.Native(ExactPath.Full(_target.Directory));
        if (Directory.Exists(directory))
        {
            return true;
        }

        return await runner.Run(directory, () =>
        {
            if (File.Exists(directory))
            {
                throw new OperationRuleException(RuleViolation.TypeMismatch, directory);
            }

            // Journal the topmost folder created, so undo takes away all of it.
            var topmost = directory;
            for (var parent = Path.GetDirectoryName(topmost); parent is not null && !Directory.Exists(parent); parent = Path.GetDirectoryName(parent))
            {
                topmost = parent;
            }

            Directory.CreateDirectory(directory);
            Journal.Created(topmost, isDirectory: true);
        }).ConfigureAwait(false);
    }

    private async Task<bool> TryFastMoveAsync(StepRunner runner, string source, string destination)
    {
        if (_kind != TransferKind.Move || !PathRules.SameVolume(source, destination))
        {
            return false;
        }

        var caseOnly = !string.Equals(source, destination, StringComparison.Ordinal) && PathRules.Same(source, destination);
        if (PathRules.Exists(destination) && !caseOnly)
        {
            return false; // merge or conflict: go item by item
        }

        var moved = false;
        await runner.Run(source, () =>
        {
            Validate(source, destination);
            var isDirectory = Directory.Exists(source);
            SimpleCommands.MoveEntry(source, destination);
            moved = true;
            Journal.Moved(source, destination, isDirectory);
        }).ConfigureAwait(false);
        if (moved)
        {
            runner.AddDone();
        }

        return true; // moved or skipped by the user either way
    }

    private void Validate(string source, string destination)
    {
        if (PathRules.Same(source, destination) && string.Equals(source, destination, StringComparison.Ordinal))
        {
            throw new OperationRuleException(RuleViolation.SameFile, source);
        }

        if (Directory.Exists(source) && PathRules.IsInside(destination, source) && !PathRules.Same(source, destination))
        {
            throw new OperationRuleException(RuleViolation.IntoItself, source);
        }

        if (_kind == TransferKind.Move && Directory.Exists(source) && !PathRules.SameVolume(source, destination)
            && TreeScanner.IsLink(new DirectoryInfo(source)))
        {
            throw new OperationRuleException(RuleViolation.LinkAcrossVolumes, source);
        }
    }

    private async Task TransferTreeAsync(
        OperationContext context,
        StepRunner runner,
        ProgressTracker tracker,
        string sourceRoot,
        string destinationRoot,
        List<PlannedItem> items)
    {
        var sameVolume = PathRules.SameVolume(sourceRoot, destinationRoot);
        var createdDirectories = new List<(PlannedItem Item, string Destination)>();
        var failedUnder = new HashSet<string>(TreeScanner.PathComparer); // display forms: children may carry \\?\ where the root does not
        var rootName = Path.GetFileName(sourceRoot);

        void MarkFailedSafe(string path)
        {
            lock (failedUnder)
            {
                MarkFailed(failedUnder, path, sourceRoot);
            }
        }

        // Small files (and same-drive moves, which are renames) go to a few workers; folders and big
        // files stay here, in order, so a folder always exists before anything is written into it.
        await using var pool = new WorkerPool<(PlannedItem Item, string Destination)>(
            _parallelism,
            async work =>
            {
                var ok = await TransferFileAsync(context, runner, tracker, work.Item, work.Destination, sameVolume).ConfigureAwait(false);
                if (!ok)
                {
                    MarkFailedSafe(work.Item.Source);
                }

                tracker.CompleteSmallItem(work.Item.Source, work.Item.Size);
            },
            runner.Token);

        foreach (var item in items)
        {
            // Plain text, not Path.GetRelativePath: that resolves against the current directory, where "aux" is a device.
            var destination = item.Relative == rootName
                ? destinationRoot
                : ExactPath.Native(Path.Combine(destinationRoot, rootName.Length == 0 ? item.Relative : item.Relative[(rootName.Length + 1)..]));

            if (!item.IsDirectory && _parallelism > 1 && (item.Size < SmallFileLimit || (_kind == TransferKind.Move && sameVolume)))
            {
                await pool.AddAsync((item, destination)).ConfigureAwait(false);
                continue;
            }

            tracker.StartItem(item.Source, item.Size);
            bool ok;
            if (item.IsDirectory && item.IsLink && _kind == TransferKind.Move)
            {
                ok = await MoveLinkAsync(runner, item, destination, sameVolume).ConfigureAwait(false);
            }
            else if (item.IsDirectory)
            {
                ok = await runner.Run(item.Source, () =>
                {
                    if (item.ScanError is not null)
                    {
                        throw item.ScanError;
                    }

                    if (File.Exists(destination))
                    {
                        throw new OperationRuleException(RuleViolation.TypeMismatch, destination);
                    }

                    if (!Directory.Exists(destination))
                    {
                        Directory.CreateDirectory(destination);
                        Journal.Created(destination, isDirectory: true);
                    }
                }).ConfigureAwait(false);
                if (ok)
                {
                    createdDirectories.Add((item, destination));
                    runner.AddDone();
                }
            }
            else
            {
                ok = await TransferFileAsync(context, runner, tracker, item, destination, sameVolume).ConfigureAwait(false);
            }

            if (!ok)
            {
                MarkFailedSafe(item.Source);
            }

            tracker.FinishItem();
        }

        await pool.CompleteAsync().ConfigureAwait(false);

        // Folder dates last, so writing the content does not overwrite them.
        foreach (var (item, destination) in Enumerable.Reverse(createdDirectories))
        {
            TryCopyTimes(item.Source, destination, directory: true);
        }

        if (_kind == TransferKind.Move)
        {
            foreach (var (item, _) in Enumerable.Reverse(createdDirectories))
            {
                if (!failedUnder.Contains(ExactPath.Display(item.Source)))
                {
                    await runner.Run(item.Source, () =>
                    {
                        PathRules.ClearReadOnly(item.Source); // a read-only folder cannot be removed on Windows
                        PathRules.RemoveDirectory(item.Source);
                        Journal.RemovedDirectory(item.Source);
                    }).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// A link to a folder inside a merged move goes as the link itself: copying it would mean a folder
    /// with nothing in it (links are not entered on a move), and the link would be gone.
    /// </summary>
    private async Task<bool> MoveLinkAsync(StepRunner runner, PlannedItem item, string destination, bool sameVolume)
    {
        var ok = await runner.Run(item.Source, () =>
        {
            if (!sameVolume)
            {
                throw new OperationRuleException(RuleViolation.LinkAcrossVolumes, item.Source);
            }

            if (PathRules.Exists(destination))
            {
                throw new OperationRuleException(RuleViolation.AlreadyExists, destination);
            }

            SimpleCommands.MoveEntry(item.Source, destination);
            Journal.Moved(item.Source, destination, isDirectory: true);
        }).ConfigureAwait(false);
        if (ok)
        {
            runner.AddDone();
        }

        return ok;
    }

    private async Task<bool> TransferFileAsync(
        OperationContext context,
        StepRunner runner,
        ProgressTracker tracker,
        PlannedItem item,
        string destination,
        bool sameVolume)
    {
        var skippedByConflict = false;
        var alreadyDone = false;
        var ok = await runner.RunAsync(item.Source, async () =>
        {
            if (Directory.Exists(destination))
            {
                throw new OperationRuleException(RuleViolation.TypeMismatch, destination);
            }

            var replaces = false;
            string? placeholder = null;
            if (File.Exists(destination))
            {
                // Also the same file under another spelling (link, junction, 8.3 name, hard link): "overwriting"
                // it would destroy the only copy, and a skipIdentical move would delete it.
                if (PathRules.SameFile(item.Source, destination))
                {
                    throw new OperationRuleException(RuleViolation.SameFile, item.Source);
                }

                if (_skipIdentical && LooksCopied(item.Source, destination))
                {
                    alreadyDone = true; // counted by the run that copied it
                    if (_kind == TransferKind.Move)
                    {
                        PathRules.ClearReadOnly(item.Source);
                        File.Delete(item.Source);
                    }

                    return;
                }

                var resolved = await ResolveConflictAsync(context, runner, item, destination).ConfigureAwait(false);
                if (resolved is null)
                {
                    skippedByConflict = true;
                    return;
                }

                // "Overwrite" kept this name; "keep both" reserved a free one with an empty placeholder.
                replaces = string.Equals(resolved, destination, StringComparison.Ordinal);
                placeholder = replaces ? null : resolved;
                destination = resolved;
            }

            try
            {
                if (_kind == TransferKind.Move && sameVolume)
                {
                    File.Move(item.Source, destination, overwrite: true);
                    Record(item.Source, destination, replaces);
                    return;
                }

                await CopyFileAsync(runner, tracker, item, destination).ConfigureAwait(false);
            }
            catch when (placeholder is not null)
            {
                TryDelete(placeholder); // an empty "f (2).txt" would look like a copy that worked
                throw;
            }

            if (_kind == TransferKind.Move)
            {
                PathRules.ClearReadOnly(item.Source);
                File.Delete(item.Source);
            }

            Record(item.Source, destination, replaces);
        }).ConfigureAwait(false);

        if (skippedByConflict)
        {
            runner.AddSkipped();
            return false;
        }

        if (ok && !alreadyDone)
        {
            runner.AddDone();
        }

        return ok;
    }

    /// <summary>A copied file can be taken away again, a moved one can go back; the content it replaced cannot return.</summary>
    private void Record(string source, string destination, bool replaced)
    {
        if (replaced)
        {
            Journal.Overwritten(destination);
        }

        if (_kind == TransferKind.Move)
        {
            Journal.Moved(source, destination, isDirectory: false);
        }
        else if (!replaced)
        {
            Journal.Created(destination, isDirectory: false);
        }
    }

    /// <summary>Same size and same date (to the second): what this operation leaves behind after copying a file.</summary>
    private static bool LooksCopied(string source, string destination)
    {
        var s = new FileInfo(source);
        var d = new FileInfo(destination);
        return s.Length == d.Length && Math.Abs((s.LastWriteTimeUtc - d.LastWriteTimeUtc).TotalSeconds) < 1;
    }

    /// <summary>Returns the path to write to, or null to skip this file.</summary>
    private async Task<string?> ResolveConflictAsync(OperationContext context, StepRunner runner, PlannedItem item, string destination)
    {
        var target = new FileInfo(destination);
        var choice = StickyChoice();
        if (choice is null)
        {
            // One question at a time; whoever waited re-checks, because the answer may have been "… all".
            await runner.PromptGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            try
            {
                choice = StickyChoice();
                if (choice is null)
                {
                    var source = new FileInfo(item.Source);
                    choice = await context.Prompts.ResolveConflictAsync(
                        new ConflictInfo(item.Source, source.Length, source.LastWriteTimeUtc, destination, target.Length, target.LastWriteTimeUtc),
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
                PathRules.ClearForOverwrite(destination);
                return destination;
            case ConflictChoice.OverwriteAllOlder:
                if (target.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(item.Source))
                {
                    return null;
                }

                PathRules.ClearForOverwrite(destination);
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

    private static async Task CopyFileAsync(StepRunner runner, ProgressTracker tracker, PlannedItem item, string destination)
    {
        if (item.Size < SmallFileLimit)
        {
            // Small files: the OS copy (CopyFileEx on Windows) is far cheaper per file than streaming,
            // and progress inside a file this small is invisible anyway. CopyFileEx keeps attributes and
            // the modification time itself; elsewhere the time is set explicitly.
            File.Copy(item.Source, destination, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(item.Source));
            }

            return; // the bytes are counted by whoever finishes the item (FinishItem or the worker pool)
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var completed = false;
        var written = false; // until the target is opened for writing, whatever is there is not ours to delete
        var reported = 0L;
        try
        {
            await using (var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                written = true;
                if (input.Length > 0)
                {
                    output.SetLength(input.Length); // one allocation instead of growing; also fails fast when the disk is full
                    output.Position = 0;
                }

                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), runner.Token).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), runner.Token).ConfigureAwait(false);
                    tracker.AddBytes(read);
                    if (reported == 0 && input.Length > BufferSize)
                    {
                        tracker.Report(force: true); // a big file shows movement right away
                    }

                    reported += read;
                    await runner.CheckpointAsync().ConfigureAwait(false);
                }

                output.SetLength(output.Position); // the source may have shrunk while we read it
            }

            NativeFile.CopyStreams(item.Source, destination); // CopyFileEx does this for small files; Zone.Identifier matters
            TryCopyTimes(item.Source, destination, directory: false);
            File.SetAttributes(destination, item.Attributes & ~(FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted | FileAttributes.SparseFile));
            completed = true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!completed)
            {
                if (written)
                {
                    TryDelete(destination); // never leave a half-written file that looks like a good copy
                }

                tracker.AddBytes(-reported);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the original error is what matters
        }
    }

    private static void TryCopyTimes(string source, string destination, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.SetCreationTimeUtc(destination, Directory.GetCreationTimeUtc(source));
                Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
            }
            else
            {
                File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // timestamps are best effort (e.g. FAT/network targets)
        }
    }

    private static void MarkFailed(HashSet<string> failedUnder, string path, string root)
    {
        // Every folder above a failed item keeps its source on a move.
        for (var p = path; p is not null && PathRules.IsInside(p, root); p = Path.GetDirectoryName(p))
        {
            failedUnder.Add(ExactPath.Display(p));
        }
    }
}
