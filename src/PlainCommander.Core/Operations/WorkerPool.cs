using System.Threading.Channels;

namespace PlainCommander.Core.Operations;

/// <summary>
/// A few workers draining a bounded queue. The first failure (abort, cancel, bug) stops the others
/// and is rethrown to the producer, so an operation never keeps copying after the user said stop.
/// </summary>
internal sealed class WorkerPool<T> : IAsyncDisposable
{
    private readonly Channel<T> _queue;
    private readonly CancellationTokenSource _stop;
    private readonly Task[] _workers;
    private Exception? _failure;

    public WorkerPool(int workers, Func<T, Task> work, CancellationToken cancellationToken)
    {
        _queue = Channel.CreateBounded<T>(new BoundedChannelOptions(workers * 4) { SingleWriter = true });
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _workers = Enumerable.Range(0, workers).Select(_ => Task.Run(() => RunAsync(work))).ToArray();
    }

    public async Task AddAsync(T item)
    {
        ThrowIfFailed();
        try
        {
            await _queue.Writer.WriteAsync(item, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ThrowIfFailed();
            throw;
        }
    }

    /// <summary>Waits for every queued item; rethrows the first failure.</summary>
    public async Task CompleteAsync()
    {
        _queue.Writer.TryComplete();
        await Task.WhenAll(_workers).ConfigureAwait(false);
        ThrowIfFailed();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(Func<T, Task> work)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await work(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested && _failure is not null)
        {
            // stopped because another worker failed
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _failure, ex, null);
            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }
}
