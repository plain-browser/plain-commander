using System.Collections.Concurrent;

namespace PlainCommander.Presentation.Tests.Monkey;

/// <summary>
/// A single "UI thread" for the view models: everything they post (watcher refreshes, undo steps, continuations)
/// runs one at a time on this thread, as in the app, instead of racing on the thread pool.
/// </summary>
internal sealed class UiThread : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly List<Exception> _crashes = [];
    private int _threadId;

    /// <summary>Exceptions thrown by posted callbacks (in the app they would bring the UI thread down).</summary>
    public IReadOnlyList<Exception> Crashes
    {
        get
        {
            lock (_crashes)
            {
                return _crashes.ToArray();
            }
        }
    }

    /// <summary>Callbacks waiting to run.</summary>
    public int Pending => _queue.Count;

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // the run is over: a late watcher event has nowhere to go (the app's window is closed too)
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
        if (error is not null)
        {
            throw error;
        }
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="body"/> on a new thread with this context and pumps until it completes.</summary>
    public static T Run<T>(Func<UiThread, Task<T>> body)
    {
        var context = new UiThread();
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            context._threadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(context);
            Task<T> task;
            try
            {
                task = body(context);
            }
            catch (Exception ex)
            {
                failure = ex;
                return;
            }

            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
            {
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    lock (context._crashes)
                    {
                        context._crashes.Add(ex);
                    }
                }
            }

            try
            {
                result = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "monkey UI",
        };
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }
}
