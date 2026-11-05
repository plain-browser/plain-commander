namespace PlainCommander.Core.Operations;

/// <summary>Moves items to the Recycle Bin. The bin itself is a Windows shell service behind this interface.</summary>
public interface IRecycleBin
{
    /// <summary>Recycles the paths; throws when the shell reports a failure or the user aborted.</summary>
    Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>F8/Del. Progress is indeterminate: the shell does not report it item by item.</summary>
public sealed class RecycleOperation : IOperation, Undo.IUndoable
{
    /// <summary>What went to the bin and when, so Ctrl+Z can bring it back.</summary>
    public Undo.UndoJournal Journal { get; } = new();

    private readonly IReadOnlyList<string> _sources;
    private readonly IRecycleBin _bin;

    public RecycleOperation(IReadOnlyList<string> sources, IRecycleBin bin)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _bin = bin ?? throw new ArgumentNullException(nameof(bin));
    }

    public OperationKind Kind => OperationKind.Recycle;

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return StepRunner.GuardAsync(runner, async () =>
        {
            context.Progress?.Report(new OperationProgress(_sources[0], 0, 0, 0, 0, 0, _sources.Count));
            var folders = _sources.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var since = DateTime.UtcNow;
            var ok = await runner.RunAsync(_sources[0], () => _bin.RecycleAsync(_sources, runner.Token)).ConfigureAwait(false);
            if (ok)
            {
                runner.AddDone(_sources.Count);
                foreach (var source in _sources.Where(s => !File.Exists(s) && !Directory.Exists(s)))
                {
                    Journal.Recycled(source, folders.Contains(source), since);
                }
            }
        });
    }
}
