using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Operations;
using Serilog;

namespace PlainCommander.Presentation;

/// <summary>
/// Runs file operations one after another. Starting F5 while a copy runs queues the new one instead of
/// letting two operations fight over the same disk.
/// </summary>
public sealed partial class OperationQueueViewModel : ObservableObject
{
    private readonly IOperationPrompts _prompts;
    private readonly Core.Elevation.IElevation? _elevation;
    private readonly Func<string, Task>? _showError;
    private readonly Lock _lock = new();
    private readonly Queue<Job> _pending = new();
    private bool _running;

    /// <param name="elevation">Lets "access denied" continue as administrator; null when not offered (tests, an elevated app).</param>
    /// <param name="showError">Shows why the administrator part could not run (UAC declined).</param>
    public OperationQueueViewModel(IOperationPrompts prompts, Core.Elevation.IElevation? elevation = null, Func<string, Task>? showError = null)
    {
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _elevation = elevation;
        _showError = showError;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogVisible), nameof(IsIndicatorVisible), nameof(IsRunning))]
    public partial OperationViewModel? Current { get; private set; }

    [ObservableProperty]
    public partial int PendingCount { get; private set; }

    /// <summary>The progress dialog is up: an operation runs and was not sent to the background.</summary>
    public bool IsDialogVisible => Current is { IsInBackground: false };

    /// <summary>The small "working…" indicator: an operation runs in the background.</summary>
    public bool IsIndicatorVisible => Current is { IsInBackground: true };

    /// <summary>An operation runs, in front or in the background (the command-bar chip).</summary>
    public bool IsRunning => Current is not null;

    /// <summary>An operation ended (any outcome), before its own completion step runs. The undo history listens.</summary>
    public event EventHandler<OperationFinishedEventArgs>? Finished;

    /// <summary>Queues an operation. The task completes with its result after <paramref name="completed"/> ran.</summary>
    public Task<OperationResult> Enqueue(IOperation operation, string title, Func<OperationResult, Task>? completed = null)
    {
        var job = new Job(operation, title, completed, new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        bool start;
        lock (_lock)
        {
            _pending.Enqueue(job);
            PendingCount = _pending.Count;
            start = !_running;
            _running = true;
        }

        if (start)
        {
            _ = RunLoopAsync();
        }

        return job.Done.Task;
    }

    private async Task RunLoopAsync()
    {
        while (true)
        {
            Job job;
            lock (_lock)
            {
                if (!_pending.TryDequeue(out job!))
                {
                    _running = false;
                    PendingCount = 0;
                    return;
                }

                PendingCount = _pending.Count;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var vm = new OperationViewModel(job.Operation, job.Title);
            vm.PropertyChanged += OnCurrentPropertyChanged;
            Current = vm;
            OperationResult result;
            try
            {
                result = await vm.RunAsync(_prompts, _elevation, _showError);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Operation {Title} crashed", job.Title);
                result = new OperationResult(OperationOutcome.Aborted, 0, 0, [ex.Message]);
            }

            vm.PropertyChanged -= OnCurrentPropertyChanged;
            Current = null;
            vm.Dispose();
            Log.Information("{Title}: {Outcome}, {Done} done, {Skipped} skipped in {Elapsed} ms", job.Title, result.Outcome, result.Done, result.Skipped, watch.ElapsedMilliseconds);

            try
            {
                Finished?.Invoke(this, new OperationFinishedEventArgs(job.Operation, job.Title, result));
                if (job.Completed is not null)
                {
                    await job.Completed(result);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "After-operation step for {Title} failed", job.Title);
            }

            job.Done.TrySetResult(result);
        }
    }

    private void OnCurrentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationViewModel.IsInBackground))
        {
            OnPropertyChanged(nameof(IsDialogVisible));
            OnPropertyChanged(nameof(IsIndicatorVisible));
        }
    }

    private sealed record Job(IOperation Operation, string Title, Func<OperationResult, Task>? Completed, TaskCompletionSource<OperationResult> Done);
}

public sealed class OperationFinishedEventArgs(Core.Operations.IOperation operation, string title, Core.Operations.OperationResult result) : EventArgs
{
    public Core.Operations.IOperation Operation { get; } = operation;

    public string Title { get; } = title;

    public Core.Operations.OperationResult Result { get; } = result;
}
