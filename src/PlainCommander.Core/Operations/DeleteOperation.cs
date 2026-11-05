using PlainCommander.Core.FileSystem;
namespace PlainCommander.Core.Operations;

/// <summary>
/// Permanent delete (Shift+Del). Removes content before folders, clears read-only, and removes links
/// themselves without ever touching what they point to.
/// </summary>
public sealed class DeleteOperation : IOperation, IElevatable
{
    private readonly IReadOnlyList<string> _sources;

    public DeleteOperation(IReadOnlyList<string> sources)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public OperationKind Kind => OperationKind.Delete;

    public Elevation.ElevatedJob ElevatedJob => new(Elevation.ElevatedJobKind.Delete, _sources);

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var plans = new List<(string Root, List<PlannedItem> Items)>();
        foreach (var source in _sources)
        {
            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            List<PlannedItem>? items = null;
            await runner.Run(full, () => items = TreeScanner.Scan(full, followLinks: false, runner.Token)).ConfigureAwait(false);
            if (items is not null)
            {
                plans.Add((full, items));
            }
        }

        var tracker = new ProgressTracker(context.Progress, plans.Sum(p => p.Items.Count), plans.Sum(p => p.Items.Count));
        foreach (var (root, items) in plans)
        {
            var keep = new HashSet<string>(TreeScanner.PathComparer);
            for (var i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i];
                tracker.StartItem(item.Source, 1);
                if (keep.Contains(item.Source))
                {
                    tracker.FinishItem();
                    continue; // something inside could not be deleted; the folder stays without a second error
                }

                var ok = await runner.Run(item.Source, () =>
                {
                    if (item.ScanError is not null && !item.IsLink)
                    {
                        throw item.ScanError;
                    }

                    if (item.IsDirectory)
                    {
                        RemoveReadOnly(item.Source, directory: true);
                        PathRules.RemoveDirectory(item.Source); // a link is removed, its target is not entered
                    }
                    else
                    {
                        RemoveReadOnly(item.Source, directory: false);
                        File.Delete(item.Source);
                    }
                }).ConfigureAwait(false);

                if (ok)
                {
                    runner.AddDone();
                }
                else
                {
                    for (var p = Path.GetDirectoryName(item.Source); p is not null && PathRules.IsInside(p, root); p = Path.GetDirectoryName(p))
                    {
                        keep.Add(p);
                    }
                }

                tracker.AddBytes(1);
                tracker.FinishItem();
            }
        }

        tracker.Report(force: true);
    }

    private static void RemoveReadOnly(string path, bool directory)
    {
        var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            info.Attributes &= ~FileAttributes.ReadOnly;
        }
    }
}
