namespace PlainCommander.Core.Operations;

/// <summary>Lets the UI pause a running operation between chunks and resume it later.</summary>
public sealed class PauseGate
{
    private readonly Lock _lock = new();
    private TaskCompletionSource? _paused;

    public bool IsPaused
    {
        get
        {
            lock (_lock)
            {
                return _paused is not null;
            }
        }
    }

    /// <summary>Raised after <see cref="Pause"/> or <see cref="Resume"/> changed the state (the elevated helper follows it).</summary>
    public event EventHandler? Changed;

    public void Pause()
    {
        bool changed;
        lock (_lock)
        {
            changed = _paused is null;
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? paused;
        lock (_lock)
        {
            paused = _paused;
            _paused = null;
        }

        paused?.TrySetResult();
        if (paused is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Completes immediately when running; waits while paused. Cancellation always wins.</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        Task? wait;
        lock (_lock)
        {
            wait = _paused?.Task;
        }

        return wait is null ? Task.CompletedTask : wait.WaitAsync(cancellationToken);
    }
}
