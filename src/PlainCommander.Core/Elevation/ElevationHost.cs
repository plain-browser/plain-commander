using System.Collections.Concurrent;
using PlainCommander.Core.Operations;
using Serilog;

namespace PlainCommander.Core.Elevation;

/// <summary>
/// The elevated helper's side of the pipe: runs the jobs the app sends and passes progress and questions
/// back. Ends when the app closes the pipe, or after <c>idle</c> with nothing to do, so an administrator
/// process does not outlive its use.
/// </summary>
public static class ElevationHost
{
    public static async Task RunAsync(Stream pipe, TimeSpan idle, CancellationToken cancellationToken = default)
    {
        await using var channel = new ElevationChannel(pipe);
        var jobs = new ConcurrentDictionary<int, HostedJob>();
        var questions = new ConcurrentDictionary<int, TaskCompletionSource<int>>();
        var nextQuestion = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = channel.ReadAsync(stop.Token);
        try
        {
            while (true)
            {
                if (jobs.IsEmpty)
                {
                    var timeout = Task.Delay(idle, stop.Token);
                    if (await Task.WhenAny(read, timeout).ConfigureAwait(false) == timeout)
                    {
                        Log.Information("Elevated helper idle, exiting");
                        return;
                    }
                }

                ElevationMessage? message;
                try
                {
                    message = await read.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException or InvalidDataException)
                {
                    Log.Warning(ex, "Elevated helper: pipe read failed");
                    return;
                }

                if (message is null)
                {
                    return; // the app closed the pipe
                }

                read = channel.ReadAsync(stop.Token);
                switch (message.Type)
                {
                    case MessageType.Run when message.Job is { } job && !jobs.ContainsKey(message.Id):
                        var hosted = new HostedJob(message.Id, job, channel, questions, () => Interlocked.Increment(ref nextQuestion));
                        jobs[message.Id] = hosted;
                        _ = hosted.RunAsync().ContinueWith(_ => jobs.TryRemove(message.Id, out var _), TaskScheduler.Default);
                        break;
                    case MessageType.Answer:
                        if (questions.TryRemove(message.Id, out var question))
                        {
                            question.TrySetResult(message.Choice);
                        }

                        break;
                    case MessageType.Pause when jobs.TryGetValue(message.Id, out var paused):
                        paused.Gate.Pause();
                        break;
                    case MessageType.Resume when jobs.TryGetValue(message.Id, out var resumed):
                        resumed.Gate.Resume();
                        break;
                    case MessageType.Cancel when jobs.TryGetValue(message.Id, out var cancelled):
                        cancelled.Cancel();
                        break;
                    default:
                        Log.Warning("Elevated helper: unexpected {Type} for {Id}", message.Type, message.Id);
                        break;
                }
            }
        }
        finally
        {
            foreach (var job in jobs.Values)
            {
                job.Cancel(); // the app is gone: nobody would see a question or the result
            }

            foreach (var question in questions.Values)
            {
                question.TrySetCanceled(CancellationToken.None);
            }

            await stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(jobs.Values.Select(j => j.Finished)).ConfigureAwait(false);
        }
    }

    private sealed class HostedJob : IOperationPrompts, IProgress<OperationProgress>, IDisposable
    {
        private readonly int _id;
        private readonly ElevatedJob _job;
        private readonly ElevationChannel _channel;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<int>> _questions;
        private readonly Func<int> _nextQuestion;
        private readonly CancellationTokenSource _cancel = new();
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HostedJob(int id, ElevatedJob job, ElevationChannel channel, ConcurrentDictionary<int, TaskCompletionSource<int>> questions, Func<int> nextQuestion)
        {
            _id = id;
            _job = job;
            _channel = channel;
            _questions = questions;
            _nextQuestion = nextQuestion;
        }

        public PauseGate Gate { get; } = new();

        public Task Finished => _finished.Task;

        public void Cancel()
        {
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // finished meanwhile
            }

            Gate.Resume();
        }

        public async Task RunAsync()
        {
            try
            {
                Log.Information("Elevated {Kind}: {Count} item(s) → {Target}", _job.Kind, _job.Sources.Count, _job.Directory ?? _job.Name);
                ElevationMessage done;
                if (_job.IsCommand)
                {
                    try
                    {
                        done = new ElevationMessage(MessageType.Done, _id, Value: _job.RunCommand());
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
                    {
                        done = new ElevationMessage(MessageType.Done, _id, Failure: FailureInfo.From(_job.Name ?? string.Empty, ex));
                    }
                }
                else
                {
                    OperationResult result;
                    try
                    {
                        result = await _job.CreateOperation().RunAsync(new OperationContext(this, this, Gate, _cancel.Token)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                    {
                        result = new OperationResult(OperationOutcome.Aborted, 0, 0, [ex.Message]);
                    }

                    done = new ElevationMessage(MessageType.Done, _id, Result: result);
                }

                await _channel.SendAsync(done).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                Log.Warning(ex, "Elevated job {Id}: result not delivered", _id);
            }
            finally
            {
                Dispose();
                _finished.TrySetResult();
            }
        }

        public void Dispose() => _cancel.Dispose();

        public void Report(OperationProgress value)
        {
            _ = SendQuietlyAsync(new ElevationMessage(MessageType.Progress, _id, Progress: value));
        }

        public async Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) =>
            (ConflictChoice)await AskAsync(new ElevationMessage(MessageType.Conflict, _id, Conflict: conflict), cancellationToken).ConfigureAwait(false);

        public async Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken)
        {
            var choice = (ErrorChoice)await AskAsync(new ElevationMessage(MessageType.Error, _id, Failure: FailureInfo.From(path, exception)), cancellationToken).ConfigureAwait(false);
            return choice == ErrorChoice.Elevate ? ErrorChoice.Abort : choice; // already elevated
        }

        private async Task<int> AskAsync(ElevationMessage message, CancellationToken cancellationToken)
        {
            var number = _nextQuestion();
            var answer = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _questions[number] = answer;
            try
            {
                await _channel.SendAsync(message with { Question = number }, cancellationToken).ConfigureAwait(false);
                return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _questions.TryRemove(number, out _);
            }
        }

        private async Task SendQuietlyAsync(ElevationMessage message)
        {
            try
            {
                await _channel.SendAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // the app went away; the read loop ends the job
            }
        }
    }
}
