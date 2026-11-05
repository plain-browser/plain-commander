using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Operations;

/// <summary>What to do with the attributes and the date of each item.</summary>
/// <param name="Set">Attributes to turn on (read-only, hidden, system, archive).</param>
/// <param name="Clear">Attributes to turn off; anything in neither stays as it is.</param>
/// <param name="Modified">A new modification time, or null to keep it.</param>
/// <param name="Recursive">Folders pass the change on to everything inside them.</param>
public sealed record AttributeChange(FileAttributes Set, FileAttributes Clear, DateTimeOffset? Modified, bool Recursive)
{
    /// <summary>The attributes this dialog may touch; the rest (directory, reparse point, compressed…) are never changed.</summary>
    public const FileAttributes Editable = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive;

    public FileAttributes Apply(FileAttributes current) => (current & ~(Clear & Editable)) | (Set & Editable);
}

/// <summary>
/// Files → Attributes on disk: read-only, hidden, system, archive and the modification time, for the chosen items and,
/// when asked, everything below them. Links are changed themselves and never entered.
/// </summary>
public sealed class AttributesOperation : IOperation, Undo.IUndoable
{
    /// <summary>The attributes and times before the change, for Ctrl+Z.</summary>
    public Undo.UndoJournal Journal { get; } = new();

    private readonly IReadOnlyList<string> _sources;
    private readonly AttributeChange _change;

    public AttributesOperation(IReadOnlyList<string> sources, AttributeChange change)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _change = change ?? throw new ArgumentNullException(nameof(change));
    }

    public OperationKind Kind => OperationKind.Attributes;

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var plan = new List<string>();
        foreach (var source in _sources)
        {
            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            plan.Add(full);
            if (_change.Recursive && Directory.Exists(full) && !TreeScanner.IsLink(new DirectoryInfo(full)))
            {
                List<PlannedItem>? items = null;
                await runner.Run(full, () => items = TreeScanner.Scan(full, followLinks: false, runner.Token)).ConfigureAwait(false);
                plan.AddRange((items ?? []).Select(i => i.Source).Where(p => !PathRules.Same(p, full)));
            }
        }

        var tracker = new ProgressTracker(context.Progress, 0, plan.Count);
        tracker.Report(force: true);
        foreach (var path in plan)
        {
            var ok = await runner.Run(path, () => ApplyTo(path)).ConfigureAwait(false);
            tracker.CompleteSmallItem(path, 0);
            if (ok)
            {
                runner.AddDone();
            }
        }

        tracker.Report(force: true);
    }

    private void ApplyTo(string path)
    {
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException(null, path);
        }

        var before = info.Attributes;
        Journal.AttributesChanged(path, before, info.LastWriteTimeUtc);
        if (_change.Modified is { } modified)
        {
            // a read-only file refuses a new time: lift it for a moment, the new attributes follow anyway
            if (before.HasFlag(FileAttributes.ReadOnly) && info is FileInfo)
            {
                info.Attributes = before & ~FileAttributes.ReadOnly;
            }

            info.LastWriteTimeUtc = modified.UtcDateTime;
        }

        var after = _change.Apply(before);
        if (after != info.Attributes)
        {
            info.Attributes = after == 0 ? FileAttributes.Normal : after;
        }
    }
}
