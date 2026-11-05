namespace PlainCommander.Core.Operations;

/// <summary>
/// A progress snapshot. <see cref="TotalBytesTotal"/> is 0 when the amount of work is unknown
/// (recycle bin), which the UI shows as an indeterminate bar.
/// </summary>
public sealed record OperationProgress(
    string CurrentItem,
    long ItemBytesDone,
    long ItemBytesTotal,
    long TotalBytesDone,
    long TotalBytesTotal,
    int ItemsDone,
    int ItemsTotal);
