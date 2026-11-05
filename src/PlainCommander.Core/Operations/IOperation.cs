namespace PlainCommander.Core.Operations;

public enum OperationKind
{
    Copy,
    Move,
    Delete,
    Recycle,
    Pack,
    Sync,

    /// <summary>Adding, deleting or renaming entries inside an existing archive.</summary>
    ArchiveUpdate,

    /// <summary>chmod on a server.</summary>
    Permissions,

    /// <summary>Attributes and dates on disk.</summary>
    Attributes,

    /// <summary>Creating or checking checksum files.</summary>
    Checksum,

    /// <summary>Each file or folder into its own archive with a password.</summary>
    Encrypt,

    /// <summary>.pce files back to the files and folders they hold.</summary>
    Decrypt,

    /// <summary>Ctrl+Z: reversing an earlier operation.</summary>
    Undo,
}

/// <summary>A long-running file operation (copy, move, delete). Runs off the UI thread.</summary>
public interface IOperation
{
    OperationKind Kind { get; }

    Task<OperationResult> RunAsync(OperationContext context);
}

/// <param name="CanElevate">An "access denied" error may offer to continue as administrator.</param>
public sealed record OperationContext(
    IProgress<OperationProgress>? Progress,
    IOperationPrompts Prompts,
    PauseGate Pause,
    CancellationToken CancellationToken,
    bool CanElevate = false);

/// <summary>An operation that can be handed to the elevated helper process, as a plain description.</summary>
public interface IElevatable
{
    /// <summary>The same work as a job the helper can rebuild; null when this instance cannot be elevated.</summary>
    Elevation.ElevatedJob? ElevatedJob { get; }
}

public enum OperationOutcome
{
    Completed,
    CompletedWithSkips,
    Cancelled,
    Aborted,

    /// <summary>Stopped at an "access denied" error because the user chose to continue as administrator.</summary>
    NeedsElevation,
}

/// <param name="Done">Items processed successfully.</param>
/// <param name="Skipped">Items skipped by a conflict or error choice.</param>
/// <param name="Errors">Paths and messages of items that failed and were skipped.</param>
public sealed record OperationResult(OperationOutcome Outcome, int Done, int Skipped, IReadOnlyList<string> Errors);
