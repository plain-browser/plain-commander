using PlainCommander.Core.Crypto;
using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Operations;

/// <summary>
/// Files → Encrypt: every chosen file or folder goes into its own encrypted copy next to it. ".pce" is the native
/// container ("contract.pdf" → "contract.pdf.pce", or a random name when the name is hidden): AES-256-GCM with an
/// Argon2id key, zstd only when it pays (or when asked), written by this process alone. ".7z" / ".zip" wrap the item in a
/// 7-Zip archive (AES-256) for other programs. The originals stay; the caller asks whether to delete them. A target
/// that already exists is asked about like any other conflict.
/// </summary>
public sealed class EncryptOperation : IOperation, Undo.IUndoable
{
    /// <summary>The files this run created, for Ctrl+Z.</summary>
    public Undo.UndoJournal Journal { get; } = new();

    private readonly IReadOnlyList<string> _sources;
    private readonly string _extension;
    private readonly string _password;
    private readonly bool _hideName;
    private readonly PceCompression _compression;
    private readonly PceOptions? _options;
    private readonly string? _targetFolder;

    /// <param name="extension">".pce", ".7z" or ".zip".</param>
    /// <param name="hideName">.pce only: the file gets a random name; the real one is inside.</param>
    /// <param name="compression">.pce only: zstd before encrypting — never, when a sample says it pays, or always.</param>
    /// <param name="options">.pce only: key derivation, block size and zstd level; null for the defaults.</param>
    /// <param name="targetFolder">Where the encrypted copies go (created when missing); null for next to each original.</param>
    public EncryptOperation(IReadOnlyList<string> sources, string extension, string password, bool hideName = false, PceCompression compression = PceCompression.None, PceOptions? options = null, string? targetFolder = null)
    {
        _targetFolder = string.IsNullOrWhiteSpace(targetFolder) ? null : targetFolder;
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        if (extension is not (".7z" or ".zip" or PceFormat.Extension))
        {
            throw new ArgumentException("Encryption writes .pce, .7z or .zip.", nameof(extension));
        }

        ArgumentException.ThrowIfNullOrEmpty(password);
        _extension = extension;
        _password = password;
        _hideName = hideName;
        _compression = compression;
        _options = options;
    }

    public OperationKind Kind => OperationKind.Encrypt;

    /// <summary>Where the encrypted copy of <paramref name="source"/> is written (with the name visible).</summary>
    public static string TargetOf(string source, string extension) => Path.TrimEndingDirectorySeparator(source) + extension;

    /// <summary>Where the encrypted copy goes: <paramref name="folder"/> (null: next to the source) and the source's name plus the extension.</summary>
    public static string TargetOf(string source, string extension, string? folder) =>
        folder is null ? TargetOf(source, extension) : Path.Combine(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)) + extension);

    /// <summary>True when <paramref name="folder"/> is the source folder itself or lies inside it: the copy would be packed into itself.</summary>
    public static bool IsInside(string folder, string source)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _extension == PceFormat.Extension ? RunPceAsync(context) : RunArchivesAsync(context);
    }

    /// <summary>The chosen folder, created when it does not exist yet; null for next to each original.</summary>
    private string? Folder()
    {
        if (_targetFolder is null)
        {
            return null;
        }

        var folder = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(_targetFolder)));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private async Task<OperationResult> RunArchivesAsync(OperationContext context)
    {
        int done = 0, skipped = 0;
        var errors = new List<string>();
        var outcome = OperationOutcome.Completed;
        foreach (var source in _sources)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var pack = new ArchivePackOperation([source], TargetOf(source, _extension, Folder()), _password);
            var result = await pack.RunAsync(context).ConfigureAwait(false);
            Journal.AddRange(pack.Journal.Actions);
            done += result.Outcome == OperationOutcome.Completed && result.Skipped == 0 ? 1 : 0;
            skipped += result.Skipped;
            errors.AddRange(result.Errors);
            if (result.Outcome is OperationOutcome.Cancelled or OperationOutcome.Aborted or OperationOutcome.NeedsElevation)
            {
                outcome = result.Outcome;
                break; // cancelled, or stopped at an error the user did not skip
            }

            if (result.Outcome != OperationOutcome.Completed)
            {
                outcome = result.Outcome;
            }
        }

        return new OperationResult(outcome, done, skipped, errors);
    }

    private Task<OperationResult> RunPceAsync(OperationContext context)
    {
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunPceCoreAsync(context, runner)));
    }

    private async Task RunPceCoreAsync(OperationContext context, StepRunner runner)
    {
        // Sizes first, so the bar means something across many items.
        var plan = new List<(string Source, string Target, long Bytes)>();
        foreach (var source in _sources)
        {
            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            long bytes = 0;
            var isFolder = Directory.Exists(full);
            if (isFolder)
            {
                List<PlannedItem>? scanned = null;
                await runner.Run(full, () => scanned = TreeScanner.Scan(full, followLinks: false, runner.Token)).ConfigureAwait(false);
                bytes = scanned?.Where(i => !i.IsDirectory).Sum(i => i.Size) ?? 0;
            }
            else if (File.Exists(full))
            {
                bytes = new FileInfo(full).Length;
            }
            else
            {
                throw new FileNotFoundException(null, source);
            }

            var target = _hideName ? Path.Combine(Folder() ?? Path.GetDirectoryName(full)!, PceFile.RandomName()) : TargetOf(full, PceFormat.Extension, Folder());
            plan.Add((full, target, bytes));
        }

        var tracker = new ProgressTracker(context.Progress, plan.Sum(p => p.Bytes), plan.Count);
        tracker.Report(force: true);
        foreach (var (source, wanted, bytes) in plan)
        {
            runner.Token.ThrowIfCancellationRequested();
            var target = wanted;
            if (File.Exists(target) || Directory.Exists(target))
            {
                var resolved = await ResolveExistingAsync(context, runner, source, bytes, target).ConfigureAwait(false);
                if (resolved is null)
                {
                    tracker.CompleteSmallItem(source, bytes);
                    continue;
                }

                target = resolved;
            }

            tracker.StartItem(source, bytes);
            var ok = await runner.Run(source, () =>
            {
                tracker.RestartItem();
                long reported = 0;
                string? temp = null;
                try
                {
                    temp = TempNames.ReserveFile(target);
                    PceFile.Encrypt(source, temp, _password, _options, _compression, total =>
                    {
                        tracker.AddBytes(total - reported);
                        reported = total;
                        context.Pause.WaitAsync(runner.Token).GetAwaiter().GetResult();
                    }, runner.Token);
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
            if (ok)
            {
                runner.AddDone();
            }
        }

        tracker.Report(force: true);
    }

    /// <summary>The target exists: overwrite it, keep both (a free name) or skip this item.</summary>
    private static async Task<string?> ResolveExistingAsync(OperationContext context, StepRunner runner, string source, long bytes, string target)
    {
        var existing = new FileInfo(target);
        await runner.PromptGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        ConflictChoice choice;
        try
        {
            choice = await context.Prompts.ResolveConflictAsync(
                new ConflictInfo(source, bytes, File.GetLastWriteTimeUtc(source), ExactPath.Display(target), existing.Exists ? existing.Length : 0, existing.LastWriteTimeUtc),
                context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runner.PromptGate.Release();
        }

        switch (choice)
        {
            case ConflictChoice.Overwrite or ConflictChoice.OverwriteAll or ConflictChoice.OverwriteAllOlder:
                if (Directory.Exists(target))
                {
                    throw new OperationRuleException(RuleViolation.TypeMismatch, target);
                }

                PathRules.ClearReadOnly(target);
                return target;
            case ConflictChoice.KeepBoth or ConflictChoice.KeepBothAll:
                var free = PathRules.ReserveFreeName(target);
                File.Delete(free); // the placeholder; the file is moved into place at the end
                return free;
            case ConflictChoice.Skip or ConflictChoice.SkipAll:
                runner.AddSkipped();
                return null;
            default:
                throw new OperationAbortedException("Cancelled at a conflict.");
        }
    }
}
