using PlainCommander.Core.Archives.SevenZip;

namespace PlainCommander.Core.Operations;

/// <summary>7-Zip's writer reports into the operation's progress and honours its pause and cancel.</summary>
internal sealed class SevenZipProgress(ProgressTracker tracker, StepRunner runner) : ISevenZipWriteObserver
{
    public void StartItem(string source, long size) => tracker.StartItem(source, size);

    public void AddBytes(int count) => tracker.AddBytes(count);

    public void FinishItem() => tracker.FinishItem();

    /// <summary>Runs on 7-Zip's thread, so waiting for "resume" here simply holds the packing.</summary>
    public void Checkpoint()
    {
        runner.Token.ThrowIfCancellationRequested();
        runner.CheckpointAsync().GetAwaiter().GetResult();
    }
}
