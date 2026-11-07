namespace PlainCommander.Core.Operations;

public enum ConflictChoice
{
    Overwrite,
    OverwriteAll,

    /// <summary>Overwrite this and every later conflict where the target is older than the source.</summary>
    OverwriteAllOlder,
    Skip,
    SkipAll,

    /// <summary>Keep both: the copy gets a free name such as "report (2).pdf".</summary>
    KeepBoth,
    KeepBothAll,
    Cancel,
}

public enum ErrorChoice
{
    Retry,
    Skip,
    SkipAll,
    Abort,

    /// <summary>Access was denied: stop here and run the rest of the operation as administrator.</summary>
    Elevate,
}

/// <summary>What to do with a file whose alternate data streams the target volume cannot keep.</summary>
public enum StreamsChoice
{
    /// <summary>Copy or move the file without its streams.</summary>
    Continue,
    ContinueAll,

    /// <summary>Leave this file out (a move keeps it where it is).</summary>
    Skip,
    SkipAll,
    Cancel,
}

/// <param name="Streams">Names with their leading colon, e.g. ":note". The internet-origin marker ":Zone.Identifier" is not listed.</param>
/// <param name="IsMove">The source is deleted afterwards, so the streams are gone for good.</param>
public sealed record LostStreamsInfo(string SourcePath, string TargetPath, IReadOnlyList<string> Streams, bool IsMove);

public sealed record ConflictInfo(
    string SourcePath,
    long SourceSize,
    DateTimeOffset SourceModified,
    string TargetPath,
    long TargetSize,
    DateTimeOffset TargetModified);

/// <summary>Questions an operation asks the user. Called from a background thread.</summary>
public interface IOperationPrompts
{
    Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken);

    /// <param name="canElevate">The error is "access denied" and the operation can continue as administrator: offer <see cref="ErrorChoice.Elevate"/>.</param>
    Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken);

    /// <summary>
    /// The target volume (FAT, exFAT, some network shares) cannot keep a file's alternate data streams. Asked once per
    /// file until an "… all" answer. Implementations that cannot ask carry on without the streams.
    /// </summary>
    Task<StreamsChoice> ResolveLostStreamsAsync(LostStreamsInfo info, CancellationToken cancellationToken) => Task.FromResult(StreamsChoice.Continue);
}

public enum RuleViolation
{
    /// <summary>Source and target are the same file.</summary>
    SameFile,

    /// <summary>A folder would be copied or moved into itself.</summary>
    IntoItself,

    /// <summary>A link/junction would be moved to another drive, which would empty its target.</summary>
    LinkAcrossVolumes,

    /// <summary>A new folder, file or name collides with something that is already there.</summary>
    AlreadyExists,

    /// <summary>The archive name has an extension we cannot write (only zip, tar, tar.gz).</summary>
    UnsupportedArchive,

    /// <summary>File and folder with the same name collide.</summary>
    TypeMismatch,

    /// <summary>The folder to create in (the panel's folder) no longer exists.</summary>
    FolderGone,
}

/// <summary>An operation refused to touch an item to protect data. Reported through the error prompt.</summary>
public sealed class OperationRuleException : IOException
{
    public OperationRuleException(RuleViolation violation, string path)
        : base($"{violation}: {path}")
    {
        Violation = violation;
        Path = path;
    }

    public OperationRuleException()
    {
    }

    public OperationRuleException(string message)
        : base(message)
    {
    }

    public OperationRuleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RuleViolation Violation { get; }

    public string Path { get; } = string.Empty;
}

/// <summary>The user chose Abort in an error prompt or Cancel in a conflict prompt.</summary>
internal sealed class OperationAbortedException : Exception
{
    public OperationAbortedException()
    {
    }

    public OperationAbortedException(string message)
        : base(message)
    {
    }

    public OperationAbortedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The user chose <see cref="ErrorChoice.Elevate"/>; the operation stops and reports <see cref="OperationOutcome.NeedsElevation"/>.</summary>
internal sealed class ElevationRequestedException : Exception
{
    public ElevationRequestedException()
    {
    }

    public ElevationRequestedException(string message)
        : base(message)
    {
    }

    public ElevationRequestedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
