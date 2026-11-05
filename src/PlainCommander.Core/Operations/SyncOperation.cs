using PlainCommander.Core.Compare;
using PlainCommander.Core.Remote;

namespace PlainCommander.Core.Operations;

/// <summary>
/// Carries out a synchronization: every item copies from one side to the other, overwriting what is there
/// (the user picked the direction for each item). Items are grouped by target folder and copied with
/// <see cref="TransferOperation"/>, or the upload/download operations when one side is a server, so errors,
/// pause, cancel and continuing broken transfers behave like F5. Two servers cannot be synchronized directly.
/// </summary>
public sealed class SyncOperation : IOperation
{
    private readonly string _left;
    private readonly string _right;
    private readonly IReadOnlyList<SyncItem> _items;
    private readonly int _parallelism;
    private readonly RemoteConnections? _remote;

    /// <param name="remote">Needed when <paramref name="left"/> or <paramref name="right"/> is a server folder.</param>
    public SyncOperation(string left, string right, IReadOnlyList<SyncItem> items, int parallelism = TransferOperation.DefaultParallelism, RemoteConnections? remote = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);
        if (RemotePath.IsRemote(left) && RemotePath.IsRemote(right))
        {
            throw new ArgumentException("Two servers cannot be synchronized directly.", nameof(right));
        }

        if ((RemotePath.IsRemote(left) || RemotePath.IsRemote(right)) && remote is null)
        {
            throw new ArgumentNullException(nameof(remote), "A server side needs the connections.");
        }

        _remote = remote;
        _left = left;
        _right = right;
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _parallelism = parallelism;
    }

    public OperationKind Kind => OperationKind.Sync;

    public int Count => _items.Count(i => i.Action != SyncAction.None);

    public async Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Comparison results always use "\"; turn it into this system's separator before touching paths.
        var groups = _items
            .Where(i => i.Action != SyncAction.None)
            .GroupBy(i => (i.Action, Folder: FolderOf(i.Relative)))
            .ToList();

        int done = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var group in groups)
        {
            var (from, to) = group.Key.Action == SyncAction.CopyToRight ? (_left, _right) : (_right, _left);
            var targetFolder = Join(to, group.Key.Folder);
            var sources = group.Select(i => Join(from, i.Relative)).ToList();
            IOperation copy;
            if (RemotePath.IsRemote(to))
            {
                copy = new RemoteUploadOperation(_remote!, sources, targetFolder, presetConflict: ConflictChoice.OverwriteAll);
            }
            else
            {
                Directory.CreateDirectory(targetFolder);
                copy = RemotePath.IsRemote(from)
                    ? new RemoteDownloadOperation(_remote!, sources, new TransferTarget(targetFolder, null), presetConflict: ConflictChoice.OverwriteAll)
                    : new TransferOperation(TransferKind.Copy, sources, new TransferTarget(targetFolder, null), _parallelism, ConflictChoice.OverwriteAll);
            }

            var result = await copy.RunAsync(context).ConfigureAwait(false);
            done += result.Done;
            skipped += result.Skipped;
            errors.AddRange(result.Errors);
            if (result.Outcome is OperationOutcome.Cancelled or OperationOutcome.Aborted)
            {
                return new OperationResult(result.Outcome, done, skipped, errors);
            }
        }

        return new OperationResult(skipped > 0 ? OperationOutcome.CompletedWithSkips : OperationOutcome.Completed, done, skipped, errors);
    }

    /// <summary>The folder part of a relative path ("a\\b\\c.txt" → "a\\b"), with "\\" as the comparison writes it.</summary>
    private static string FolderOf(string relative)
    {
        var slash = relative.LastIndexOf('\\');
        return slash < 0 ? string.Empty : relative[..slash];
    }

    /// <summary>A comparison path ("\\"-separated) under a root on disk or on a server.</summary>
    public static string Join(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        if (relative.Length == 0)
        {
            return root;
        }

        return RemotePath.IsRemote(root)
            ? root.TrimEnd('/') + "/" + relative.Replace('\\', '/')
            : Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
    }
}
