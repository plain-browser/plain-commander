using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Elevation;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;

namespace PlainCommander.Presentation;

/// <summary>One running operation as the progress dialog shows it: bars, texts, pause, cancel, background.</summary>
public sealed partial class OperationViewModel : ObservableObject, IDisposable
{
    private readonly IOperation _operation;
    private readonly CancellationTokenSource _cancel = new();
    private readonly PauseGate _gate = new();
    private readonly Stopwatch _clock = new();
    private bool _disposed;

    public OperationViewModel(IOperation operation, string title)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
        Title = title;
    }

    public string Title { get; }

    public OperationKind Kind => _operation.Kind;

    [ObservableProperty]
    public partial string CurrentItem { get; private set; } = string.Empty;

    /// <summary>0–100 for the current file.</summary>
    [ObservableProperty]
    public partial double ItemPercent { get; private set; }

    /// <summary>0–100 for the whole operation.</summary>
    [ObservableProperty]
    public partial double TotalPercent { get; private set; }

    /// <summary>True while the amount of work is unknown (scanning, recycle bin).</summary>
    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; } = true;

    [ObservableProperty]
    public partial string AmountText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SpeedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPaused { get; private set; }

    [ObservableProperty]
    public partial bool IsInBackground { get; private set; }

    [ObservableProperty]
    public partial bool IsCancelling { get; private set; }

    /// <summary>The rest of the operation runs in the administrator helper (the dialog shows a shield).</summary>
    [ObservableProperty]
    public partial bool IsElevated { get; private set; }

    public void TogglePause()
    {
        if (_gate.IsPaused)
        {
            _gate.Resume();
            _clock.Start();
        }
        else
        {
            _gate.Pause();
            _clock.Stop();
            SpeedText = Texts.Paused;
        }

        IsPaused = _gate.IsPaused;
    }

    public void Cancel()
    {
        if (_disposed)
        {
            return; // already finished
        }

        IsCancelling = true;
        _cancel.Cancel();
        _gate.Resume();
        IsPaused = false;
    }

    public void SendToBackground() => IsInBackground = true;

    public void BringToFront() => IsInBackground = false;

    /// <param name="elevation">When given, "access denied" offers to continue as administrator in the helper process.</param>
    /// <param name="showError">Tells the user why the administrator part did not run.</param>
    internal async Task<OperationResult> RunAsync(IOperationPrompts prompts, IElevation? elevation = null, Func<string, Task>? showError = null)
    {
        _clock.Start();
        try
        {
            var progress = new Progress<OperationProgress>(Apply);
            var job = elevation is null ? null : (_operation as IElevatable)?.ElevatedJob;
            var context = new OperationContext(progress, prompts, _gate, _cancel.Token, job is not null);
            var result = await _operation.RunAsync(context);
            if (result.Outcome != OperationOutcome.NeedsElevation || job is null || elevation is null)
            {
                return result;
            }

            string problem;
            try
            {
                IsIndeterminate = true;
                var helper = await elevation.ConnectAsync(_cancel.Token);
                IsElevated = true;
                var rest = await helper.RunAsync(job with { Resume = true }, context with { CanElevate = false });
                return new OperationResult(rest.Outcome, result.Done + rest.Done, result.Skipped + rest.Skipped, [.. result.Errors, .. rest.Errors]);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                return result with { Outcome = OperationOutcome.Cancelled };
            }
            catch (OperationCanceledException)
            {
                problem = Texts.ElevationRefused;
            }
            catch (IOException ex)
            {
                Serilog.Log.Warning(ex, "Elevated helper not available");
                problem = Texts.ElevationFailed(ex.Message);
            }

            if (showError is not null)
            {
                await showError(problem);
            }

            return result with { Outcome = OperationOutcome.Aborted, Errors = [.. result.Errors, problem] };
        }
        finally
        {
            _clock.Stop();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _cancel.Dispose();
    }

    private void Apply(OperationProgress p)
    {
        CurrentItem = ExactPath.Display(p.CurrentItem);
        IsIndeterminate = p.TotalBytesTotal <= 0;
        TotalPercent = p.TotalBytesTotal > 0 ? 100.0 * p.TotalBytesDone / p.TotalBytesTotal : 0;
        ItemPercent = p.ItemBytesTotal > 0 ? 100.0 * p.ItemBytesDone / p.ItemBytesTotal : 0;

        var byBytes = Kind is OperationKind.Copy or OperationKind.Move;
        AmountText = byBytes && p.TotalBytesTotal > 0
            ? Texts.BytesProgress(p.TotalBytesDone, p.TotalBytesTotal) + "   " + Texts.ItemsProgress(p.ItemsDone, p.ItemsTotal)
            : Texts.ItemsProgress(p.ItemsDone, p.ItemsTotal);

        if (byBytes && !IsPaused && _clock.Elapsed.TotalSeconds >= 1 && p.TotalBytesDone > 0)
        {
            var speed = p.TotalBytesDone / _clock.Elapsed.TotalSeconds;
            TimeSpan? left = speed > 0 && p.TotalBytesTotal > 0
                ? TimeSpan.FromSeconds((p.TotalBytesTotal - p.TotalBytesDone) / speed)
                : null;
            SpeedText = Texts.Speed(speed, left);
        }
    }
}
