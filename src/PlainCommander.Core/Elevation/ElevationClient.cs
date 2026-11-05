using System.Collections.Concurrent;
using PlainCommander.Core.Operations;
using Serilog;

namespace PlainCommander.Core.Elevation;

/// <summary>Starts (or reuses) the elevated helper. The app implements it with UAC and a named pipe.</summary>
public interface IElevation
{
    /// <summary>
    /// A connected helper. Throws <see cref="OperationCanceledException"/> when the user said no to UAC, and
    /// <see cref="IOException"/> when the helper could not be started or did not answer.
    /// </summary>
    Task<ElevationClient> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The app's side of the pipe: sends jobs to the helper and answers its questions with the app's own
/// dialogs, so an elevated copy looks like any other.
/// </summary>
public sealed class ElevationClient : IAsyncDisposable
{
    private readonly ElevationChannel _channel;
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly Task _readLoop;
    private int _nextId;
    private volatile bool _closed;

    public ElevationClient(Stream pipe)
    {
        _channel = new ElevationChannel(pipe);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>False once the helper went away (it exits after a while with nothing to do).</summary>
    public bool IsConnected => !_closed;

    /// <summary>Runs a copy/move/delete in the helper with this context's progress, prompts, pause and cancel.</summary>
    public async Task<OperationResult> RunAsync(ElevatedJob job, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);
        var done = await SendJobAsync(job, context).ConfigureAwait(false);
        return done.Result ?? new OperationResult(OperationOutcome.Aborted, 0, 0, [done.Failure?.Message ?? "No result."]);
    }

    /// <summary>F7 / Shift+F4 / Shift+F6 in the helper: the path created or renamed to, or the helper's exception rebuilt.</summary>
    public async Task<string> RunCommandAsync(ElevatedJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var context = new OperationContext(null, NoQuestions.Instance, new PauseGate(), cancellationToken);
        var done = await SendJobAsync(job, context).ConfigureAwait(false);
        return done.Value ?? throw (done.Failure?.ToException() ?? new IOException("No result."));
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        await _channel.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // closing
        }
    }

    private async Task<ElevationMessage> SendJobAsync(ElevatedJob job, OperationContext context)
    {
        if (_closed)
        {
            throw new IOException("The elevated helper is not running.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending(context);
        _pending[id] = pending;

        void OnPause(object? sender, EventArgs e) =>
            _ = SendQuietlyAsync(new ElevationMessage(context.Pause.IsPaused ? MessageType.Pause : MessageType.Resume, id));

        context.Pause.Changed += OnPause;
        try
        {
            await using var cancel = context.CancellationToken.Register(() => _ = SendQuietlyAsync(new ElevationMessage(MessageType.Cancel, id)));
            // A pause that is already on goes with the job itself: sent as a separate message after Run it would
            // arrive when the helper has begun, and a small job could be done before the pause took hold.
            await _channel.SendAsync(new ElevationMessage(MessageType.Run, id, Job: job, Paused: context.Pause.IsPaused)).ConfigureAwait(false);

            return await pending.Done.Task.ConfigureAwait(false);
        }
        finally
        {
            context.Pause.Changed -= OnPause;
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _channel.ReadAsync().ConfigureAwait(false) is { } message)
            {
                if (!_pending.TryGetValue(message.Id, out var pending))
                {
                    continue; // a late progress report of a finished job
                }

                switch (message.Type)
                {
                    case MessageType.Progress when message.Progress is { } progress:
                        pending.Context.Progress?.Report(progress);
                        break;
                    case MessageType.Conflict when message.Conflict is { } conflict:
                        _ = AnswerAsync(message.Question, async () => (int)await pending.Context.Prompts.ResolveConflictAsync(conflict, pending.Context.CancellationToken).ConfigureAwait(false));
                        break;
                    case MessageType.Error when message.Failure is { } failure:
                        _ = AnswerAsync(message.Question, async () => (int)await pending.Context.Prompts.ResolveErrorAsync(failure.Path, failure.ToException(), false, pending.Context.CancellationToken).ConfigureAwait(false));
                        break;
                    case MessageType.Done:
                        pending.Done.TrySetResult(message);
                        break;
                    default:
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            if (!_closed)
            {
                Log.Warning(ex, "Elevated helper pipe broken");
            }
        }
        finally
        {
            _closed = true;
            foreach (var pending in _pending.Values)
            {
                pending.Done.TrySetException(new IOException("The elevated helper ended."));
            }
        }
    }

    private async Task AnswerAsync(int question, Func<Task<int>> ask)
    {
        int choice;
        try
        {
            choice = await ask().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // the job was cancelled; the helper stops on its own
        }

        await SendQuietlyAsync(new ElevationMessage(MessageType.Answer, question, Choice: choice)).ConfigureAwait(false);
    }

    private async Task SendQuietlyAsync(ElevationMessage message)
    {
        try
        {
            await _channel.SendAsync(message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Debug(ex, "Elevated helper: {Type} not sent", message.Type);
        }
    }

    private sealed record Pending(OperationContext Context)
    {
        public TaskCompletionSource<ElevationMessage> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class NoQuestions : IOperationPrompts
    {
        public static readonly NoQuestions Instance = new();

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) => Task.FromResult(ConflictChoice.Cancel);

        public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken) => Task.FromResult(ErrorChoice.Abort);
    }
}
