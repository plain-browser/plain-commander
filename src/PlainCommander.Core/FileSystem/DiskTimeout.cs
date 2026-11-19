using System.Diagnostics;

namespace PlainCommander.Core.FileSystem;

/// <summary>
/// A disk that stopped answering (an unplugged external drive, a dead share) blocks a system call for minutes and
/// cannot be cancelled. These helpers give up waiting after <see cref="Limit"/>; the stuck call is left on its
/// worker thread to end whenever the system lets it.
/// </summary>
public static class DiskTimeout
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static int _paused;
    private static long _resumedAt;

    /// <summary>
    /// Stops the clock while the user is being asked something (a password, a login, a confirmation): a listing
    /// that waits for the answer is not a silent disk. The limit starts afresh once the last pause ends.
    /// </summary>
    public static IDisposable Pause()
    {
        Interlocked.Increment(ref _paused);
        return new Resume();
    }

    /// <summary>Waits for <paramref name="task"/> at most the limit; <see cref="TimeoutException"/> otherwise.</summary>
    public static Task<T> WithTimeoutAsync<T>(Task<T> task, CancellationToken token = default) => WithTimeoutAsync(task, Limit, token);

    internal static async Task<T> WithTimeoutAsync<T>(Task<T> task, TimeSpan limit, CancellationToken token = default)
    {
        while (true)
        {
            var started = Stopwatch.GetTimestamp();
            var done = await Task.WhenAny(task, Task.Delay(limit, token)).ConfigureAwait(false);
            if (done == task)
            {
                break;
            }

            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _paused) > 0)
            {
                await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(100), token)).ConfigureAwait(false);
                continue; // a question is open: wait for the answer, then give the work a full limit again
            }

            if (Volatile.Read(ref _resumedAt) > started)
            {
                continue; // a question was answered during this wait: the time spent on it does not count
            }

            _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("The disk did not respond in " + (int)limit.TotalSeconds + " seconds.");
        }

        return await task.ConfigureAwait(false);
    }

    private sealed class Resume : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Volatile.Write(ref _resumedAt, Stopwatch.GetTimestamp());
                Interlocked.Decrement(ref _paused);
            }
        }
    }

    /// <summary>Synchronous form for callers that already run off the UI thread; <paramref name="onTimeout"/> is the answer when the disk is silent.</summary>
    public static T Run<T>(Func<T> work, T onTimeout, TimeSpan? limit = null)
    {
        var task = Task.Run(work);
        return task.Wait(limit ?? Limit) ? task.GetAwaiter().GetResult() : onTimeout;
    }
}
