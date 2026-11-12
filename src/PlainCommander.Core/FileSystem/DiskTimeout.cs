namespace PlainCommander.Core.FileSystem;

/// <summary>
/// A disk that stopped answering (an unplugged external drive, a dead share) blocks a system call for minutes and
/// cannot be cancelled. These helpers give up waiting after <see cref="Limit"/>; the stuck call is left on its
/// worker thread to end whenever the system lets it.
/// </summary>
public static class DiskTimeout
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>Waits for <paramref name="task"/> at most the limit; <see cref="TimeoutException"/> otherwise.</summary>
    public static async Task<T> WithTimeoutAsync<T>(Task<T> task, CancellationToken token = default)
    {
        var done = await Task.WhenAny(task, Task.Delay(Limit, token)).ConfigureAwait(false);
        if (done != task)
        {
            token.ThrowIfCancellationRequested();
            _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException("The disk did not respond in " + (int)Limit.TotalSeconds + " seconds.");
        }

        return await task.ConfigureAwait(false);
    }

    /// <summary>Synchronous form for callers that already run off the UI thread; <paramref name="onTimeout"/> is the answer when the disk is silent.</summary>
    public static T Run<T>(Func<T> work, T onTimeout, TimeSpan? limit = null)
    {
        var task = Task.Run(work);
        return task.Wait(limit ?? Limit) ? task.GetAwaiter().GetResult() : onTimeout;
    }
}
