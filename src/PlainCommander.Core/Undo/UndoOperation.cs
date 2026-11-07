using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Undo;

/// <summary>
/// Ctrl+Z: reverses one recorded step. Attributes come back first, then removed folders, then moved items go back
/// (newest first), recycled items are restored, and finally what the step created goes to the Recycle Bin (an empty
/// folder is just removed). Nothing is deleted for good. Items that changed meanwhile are explained, not forced:
/// a moved file whose old place is taken stays where it is.
/// </summary>
public sealed class UndoOperation : IOperation
{
    private readonly UndoEntry _entry;
    private readonly IRecycleBin _bin;
    private readonly IRecycleBinRestore? _restore;

    public UndoOperation(UndoEntry entry, IRecycleBin bin, IRecycleBinRestore? restore)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _bin = bin ?? throw new ArgumentNullException(nameof(bin));
        _restore = restore;
    }

    public OperationKind Kind => OperationKind.Undo;

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var actions = _entry.Actions;
        var tracker = new ProgressTracker(context.Progress, 0, actions.Count);
        tracker.Report(force: true);

        foreach (var a in actions.Where(static a => a.Kind == UndoKind.AttributesChanged).Reverse())
        {
            await Step(runner, tracker, a.Path, () => RestoreAttributes(a)).ConfigureAwait(false);
        }

        foreach (var a in actions.Where(static a => a.Kind == UndoKind.RemovedDirectory))
        {
            await Step(runner, tracker, a.Path, () => Directory.CreateDirectory(ExactPath.Native(a.Path))).ConfigureAwait(false);
        }

        foreach (var a in actions.Where(static a => a.Kind == UndoKind.Moved).Reverse())
        {
            await Step(runner, tracker, a.Path, () => MoveBack(a)).ConfigureAwait(false);
        }

        foreach (var a in actions.Where(static a => a.Kind == UndoKind.Recycled))
        {
            await Step(runner, tracker, a.Path, () =>
            {
                if (_restore is null || !_restore.Restore(a.Path, (a.When ?? DateTime.MinValue).AddSeconds(-2)))
                {
                    throw new UndoException(UndoProblem.NotInRecycleBin, a.Path);
                }
            }).ConfigureAwait(false);
        }

        await RemoveCreatedAsync(runner, tracker, actions.Where(static a => a.Kind == UndoKind.Created).ToList()).ConfigureAwait(false);
        tracker.Report(force: true);
    }

    private static async Task Step(StepRunner runner, ProgressTracker tracker, string path, Action action)
    {
        var ok = await runner.Run(path, action).ConfigureAwait(false);
        tracker.CompleteSmallItem(path, 0);
        if (ok)
        {
            runner.AddDone();
        }
    }

    private static void RestoreAttributes(UndoAction a)
    {
        var path = ExactPath.Native(a.Path);
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(null, a.Path);
        }

        info.Attributes &= ~FileAttributes.ReadOnly; // a read-only file refuses a new time
        if (a.When is { } modified)
        {
            info.LastWriteTimeUtc = modified;
        }

        info.Attributes = a.Attributes == 0 ? FileAttributes.Normal : a.Attributes;
    }

    private static void MoveBack(UndoAction a)
    {
        var now = ExactPath.Native(a.Path);
        var back = ExactPath.Native(a.From!);
        if (!PathRules.Exists(now))
        {
            throw new UndoException(UndoProblem.Gone, a.Path);
        }

        var caseOnly = PathRules.Same(now, back) && !string.Equals(now, back, StringComparison.Ordinal);
        if (PathRules.Exists(back) && !caseOnly)
        {
            throw new UndoException(UndoProblem.PlaceTaken, a.From!);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(back)!);
        SimpleCommands.MoveEntry(now, back);
    }

    /// <summary>A folder with nothing in it. Not a folder any more (replaced by a file meanwhile): false, it is recycled.</summary>
    private static bool IsEmptyDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The topmost created items only (a created folder takes its content along): empty folders are removed, the rest
    /// goes to the Recycle Bin in one go.
    /// </summary>
    private async Task RemoveCreatedAsync(StepRunner runner, ProgressTracker tracker, List<UndoAction> created)
    {
        var top = UndoEntry.Topmost(created).Where(c => PathRules.Exists(ExactPath.Native(c.Path))).ToList();
        var recycle = new List<string>();
        foreach (var c in top)
        {
            var native = ExactPath.Native(c.Path);
            if (c.IsDirectory && IsEmptyDirectory(native))
            {
                await Step(runner, tracker, c.Path, () => Directory.Delete(native)).ConfigureAwait(false);
            }
            else
            {
                recycle.Add(ExactPath.Display(c.Path));
            }
        }

        if (recycle.Count > 0)
        {
            var ok = await runner.RunAsync(recycle[0], () => _bin.RecycleAsync(recycle, runner.Token)).ConfigureAwait(false);
            tracker.CompleteSmallItem(recycle[0], 0);
            if (ok)
            {
                runner.AddDone(recycle.Count);
            }
        }
    }
}

public enum UndoProblem
{
    /// <summary>The moved item is no longer where the step put it.</summary>
    Gone,

    /// <summary>Something else now has the name the item would go back to.</summary>
    PlaceTaken,

    /// <summary>The recycled item is not in the Recycle Bin any more (emptied, restored by hand, or no bin on that drive).</summary>
    NotInRecycleBin,
}

/// <summary>A part of an undo that cannot be done; the user decides whether the rest goes on.</summary>
public sealed class UndoException(UndoProblem problem, string path) : IOException($"{problem}: {path}")
{
    public UndoProblem Problem { get; } = problem;

    public string Path { get; } = path;
}
