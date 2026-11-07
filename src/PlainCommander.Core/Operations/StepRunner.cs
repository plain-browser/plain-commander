namespace PlainCommander.Core.Operations;

/// <summary>
/// Shared retry/skip/abort handling and the bookkeeping behind <see cref="OperationResult"/>.
/// Thread-safe: parallel copy workers use one runner. Prompts are asked one at a time.
/// </summary>
internal sealed class StepRunner
{
    private readonly OperationContext _context;
    private readonly Lock _lock = new();
    private readonly List<string> _errors = [];
    private volatile bool _skipAllErrors;
    private int _done;
    private int _skipped;

    public StepRunner(OperationContext context)
    {
        _context = context;
    }

    public int Done => Volatile.Read(ref _done);

    public int Skipped => Volatile.Read(ref _skipped);

    public CancellationToken Token => _context.CancellationToken;

    /// <summary>One prompt on screen at a time, even with several workers hitting problems at once.</summary>
    public SemaphoreSlim PromptGate { get; } = new(1, 1);

    public void AddDone(int count = 1) => Interlocked.Add(ref _done, count);

    public void AddSkipped() => Interlocked.Increment(ref _skipped);

    /// <summary>Waits while paused and throws when cancelled.</summary>
    public async Task CheckpointAsync()
    {
        Token.ThrowIfCancellationRequested();
        await _context.Pause.WaitAsync(Token).ConfigureAwait(false);
    }

    /// <summary>Runs one step; on an I/O error asks the user. Returns false when the step was skipped.</summary>
    public async Task<bool> RunAsync(string path, Func<Task> step)
    {
        while (true)
        {
            await CheckpointAsync().ConfigureAwait(false);
            try
            {
                await step().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (IsRecoverable(ex) && Token.IsCancellationRequested)
            {
                // cancelled while the step ran: its failure is part of stopping, not a question (nor a crash)
                throw new OperationCanceledException(ex.Message, ex, Token);
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                switch (await AskAsync(path, ex).ConfigureAwait(false))
                {
                    case ErrorChoice.Retry:
                        continue;
                    case ErrorChoice.Skip or ErrorChoice.SkipAll:
                        AddSkipped();
                        lock (_lock)
                        {
                            _errors.Add($"{path}: {ex.Message}");
                        }

                        return false;
                    case ErrorChoice.Elevate:
                        throw new ElevationRequestedException(path, ex);
                    default:
                        throw new OperationAbortedException(ex.Message, ex);
                }
            }
        }
    }

    public Task<bool> Run(string path, Action step) => RunAsync(path, () =>
    {
        step();
        return Task.CompletedTask;
    });

    public OperationResult Result(OperationOutcome? forced = null)
    {
        lock (_lock)
        {
            return new OperationResult(
                forced ?? (Skipped > 0 ? OperationOutcome.CompletedWithSkips : OperationOutcome.Completed),
                Done,
                Skipped,
                _errors.ToArray());
        }
    }

    /// <summary>Wraps a whole operation: maps cancel/abort to outcomes instead of exceptions.</summary>
    public static async Task<OperationResult> GuardAsync(StepRunner runner, Func<Task> body)
    {
        try
        {
            await body().ConfigureAwait(false);
            return runner.Result();
        }
        catch (OperationCanceledException)
        {
            return runner.Result(OperationOutcome.Cancelled);
        }
        catch (OperationAbortedException)
        {
            return runner.Result(OperationOutcome.Aborted);
        }
        catch (ElevationRequestedException)
        {
            return runner.Result(OperationOutcome.NeedsElevation);
        }
    }

    private async Task<ErrorChoice> AskAsync(string path, Exception ex)
    {
        if (_skipAllErrors)
        {
            return ErrorChoice.Skip;
        }

        await PromptGate.WaitAsync(Token).ConfigureAwait(false);
        try
        {
            if (_skipAllErrors)
            {
                return ErrorChoice.Skip; // another worker's prompt answered "skip all" while we waited
            }

            var canElevate = _context.CanElevate && Elevation.ElevationRules.IsAccessDenied(ex);
            var choice = await _context.Prompts.ResolveErrorAsync(path, ex, canElevate, Token).ConfigureAwait(false);
            if (choice == ErrorChoice.Elevate && !canElevate)
            {
                choice = ErrorChoice.Abort;
            }

            if (choice == ErrorChoice.SkipAll)
            {
                _skipAllErrors = true;
            }

            return choice;
        }
        finally
        {
            PromptGate.Release();
        }
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException;
}
