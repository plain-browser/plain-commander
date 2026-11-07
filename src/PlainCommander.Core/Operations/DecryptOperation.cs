using PlainCommander.Core.Crypto;
using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Operations;

/// <summary>
/// Enter on a .pce file, or Files → Decrypt: every container is opened with the password and what it holds
/// is written next to it under its original name (a file, or a folder with its content). The container stays.
/// A wrong password for a later file (each has its own salt, so each is checked on its own) is reported through
/// the error prompt; a target that exists is asked about like any other conflict.
/// </summary>
public sealed class DecryptOperation : IOperation, Undo.IUndoable
{
    /// <summary>The files and folders this run created, for Ctrl+Z.</summary>
    public Undo.UndoJournal Journal { get; } = new();

    private readonly IReadOnlyList<string> _sources;
    private readonly string _password;
    private readonly string? _targetFolder;

    /// <param name="targetFolder">Where the content goes; null for next to each container.</param>
    public DecryptOperation(IReadOnlyList<string> sources, string password, string? targetFolder = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ArgumentException.ThrowIfNullOrEmpty(password);
        _password = password;
        _targetFolder = targetFolder;
    }

    public OperationKind Kind => OperationKind.Decrypt;

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var tracker = new ProgressTracker(context.Progress, _sources.Sum(s => File.Exists(s) ? new FileInfo(s).Length : 0), _sources.Count);
        tracker.Report(force: true);
        foreach (var source in _sources)
        {
            runner.Token.ThrowIfCancellationRequested();
            var file = ExactPath.Native(ExactPath.Full(source));
            var size = File.Exists(file) ? new FileInfo(file).Length : 0;
            tracker.StartItem(file, size);
            var produced = false;
            var ok = await runner.RunAsync(file, async () =>
            {
                tracker.RestartItem();
                var (header, metadata) = PceFile.Peek(file, _password);
                var folder = _targetFolder ?? Path.GetDirectoryName(file)!;
                var target = Path.Combine(folder, SafeName(metadata.Name));
                if (File.Exists(target) || Directory.Exists(target))
                {
                    var resolved = await ResolveExistingAsync(context, runner, file, metadata, target, header.IsFolder).ConfigureAwait(false);
                    if (resolved is null)
                    {
                        return;
                    }

                    target = resolved;
                }

                Directory.CreateDirectory(folder);
                long reported = 0;
                void Progress(long total)
                {
                    // Plain bytes out are a close proxy for container bytes in (no compression by default).
                    var scaled = header.IsCompressed ? reported : Math.Min(total, size);
                    tracker.AddBytes(scaled - reported);
                    reported = scaled;
                    context.Pause.WaitAsync(runner.Token).GetAwaiter().GetResult();
                }

                if (header.IsFolder)
                {
                    var existed = Directory.Exists(target);
                    PceFile.Decrypt(file, _password, target, Progress, runner.Token);
                    if (!existed)
                    {
                        Journal.Created(target, isDirectory: true);
                    }

                    produced = true;
                    return;
                }

                string? temp = null;
                try
                {
                    temp = TempNames.ReserveFile(target);
                    PceFile.Decrypt(file, _password, temp, Progress, runner.Token);
                    var replaces = File.Exists(target);
                    File.Move(temp, target, overwrite: true);
                    if (replaces)
                    {
                        Journal.Overwritten(target);
                    }
                    else
                    {
                        Journal.Created(target, isDirectory: false);
                    }

                    produced = true;
                }
                finally
                {
                    if (temp is not null && File.Exists(temp))
                    {
                        try
                        {
                            File.Delete(temp);
                        }
                        catch (IOException)
                        {
                            // a leftover .part is better than hiding the original error
                        }
                    }
                }
            }).ConfigureAwait(false);
            tracker.FinishItem();
            if (ok && produced)
            {
                runner.AddDone();
            }
        }

        tracker.Report(force: true);
    }

    /// <summary>The stored name is data from the file: only a plain name is used, never a path.</summary>
    private static string SafeName(string name)
    {
        var plain = Path.GetFileName(name.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        if (plain.Length == 0 || plain is "." or ".." || plain.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new PceFormatException($"The stored name \"{name}\" is not a valid file name.");
        }

        return plain;
    }

    /// <summary>The content's name is taken: overwrite (merge into an existing folder), keep both or skip.</summary>
    private static async Task<string?> ResolveExistingAsync(OperationContext context, StepRunner runner, string file, PceMetadata metadata, string target, bool isFolder)
    {
        var existing = new FileInfo(target);
        await runner.PromptGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        ConflictChoice choice;
        try
        {
            choice = await context.Prompts.ResolveConflictAsync(
                new ConflictInfo(file, Math.Max(0, metadata.Size), metadata.ModifiedUtc, ExactPath.Display(target), existing.Exists ? existing.Length : 0, existing.LastWriteTimeUtc),
                context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runner.PromptGate.Release();
        }

        switch (choice)
        {
            case ConflictChoice.Overwrite or ConflictChoice.OverwriteAll or ConflictChoice.OverwriteAllOlder:
                if (Directory.Exists(target) != isFolder)
                {
                    throw new OperationRuleException(RuleViolation.TypeMismatch, target);
                }

                if (!isFolder)
                {
                    PathRules.ClearReadOnly(target);
                }

                return target;
            case ConflictChoice.KeepBoth or ConflictChoice.KeepBothAll:
                var free = PathRules.ReserveFreeName(target);
                File.Delete(free); // the placeholder; the result is moved or extracted into place
                return free;
            case ConflictChoice.Skip or ConflictChoice.SkipAll:
                runner.AddSkipped();
                return null;
            default:
                throw new OperationAbortedException("Cancelled at a conflict.");
        }
    }
}
