using System.Diagnostics;

namespace PlainCommander.Core.Operations;

/// <summary>
/// Accumulates progress and reports it at most every 50 ms, so a million tiny files do not flood the UI.
/// Thread-safe. The "item" fields follow streamed (big) files only; small files copied in parallel
/// count toward the totals without moving the per-file bar.
/// </summary>
internal sealed class ProgressTracker
{
    private static readonly long Interval = Stopwatch.Frequency / 20;

    private readonly IProgress<OperationProgress>? _progress;
    private readonly Lock _lock = new();
    private long _lastReport;
    private long _bytesDone;
    private int _itemsDone;
    private string _current = string.Empty;
    private long _itemDone;
    private long _itemTotal;

    public ProgressTracker(IProgress<OperationProgress>? progress, long totalBytes, int totalItems)
    {
        _progress = progress;
        TotalBytes = totalBytes;
        TotalItems = totalItems;
    }

    public long TotalBytes { get; }

    public int TotalItems { get; }

    /// <summary>A streamed item starts: it owns the per-file bar until <see cref="FinishItem"/>.</summary>
    public void StartItem(string path, long size)
    {
        lock (_lock)
        {
            _current = path;
            _itemDone = 0;
            _itemTotal = size;
            ReportLocked(force: false);
        }
    }

    /// <summary>A retry starts the streamed item over: what it reported so far no longer counts.</summary>
    /// <param name="kept">Bytes of the item that are already there (a broken transfer continues after them).</param>
    public void RestartItem(long kept = 0)
    {
        lock (_lock)
        {
            _bytesDone += kept - _itemDone;
            _itemDone = kept;
            ReportLocked(force: false);
        }
    }

    public void AddBytes(long bytes)
    {
        lock (_lock)
        {
            _itemDone += bytes;
            _bytesDone += bytes;
            ReportLocked(force: false);
        }
    }

    /// <summary>Marks the streamed item finished; bytes it did not report (skipped, renamed) count now.</summary>
    public void FinishItem()
    {
        lock (_lock)
        {
            _bytesDone += Math.Max(0, _itemTotal - _itemDone);
            _itemDone = _itemTotal;
            _itemsDone++;
            ReportLocked(force: false);
        }
    }

    /// <summary>A whole small item done in one step (parallel path), whether copied or skipped.</summary>
    public void CompleteSmallItem(string path, long size)
    {
        lock (_lock)
        {
            _current = path;
            _bytesDone += size;
            _itemsDone++;
            ReportLocked(force: false);
        }
    }

    public void Report(bool force)
    {
        lock (_lock)
        {
            ReportLocked(force);
        }
    }

    private void ReportLocked(bool force)
    {
        if (_progress is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastReport < Interval)
        {
            return;
        }

        _lastReport = now;
        _progress.Report(new OperationProgress(_current, _itemDone, _itemTotal, _bytesDone, TotalBytes, _itemsDone, TotalItems));
    }
}
