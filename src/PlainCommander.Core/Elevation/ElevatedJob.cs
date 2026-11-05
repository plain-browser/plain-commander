using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Elevation;

public enum ElevatedJobKind
{
    Copy,
    Move,
    Delete,
    CreateDirectory,
    CreateFile,
    Rename,
}

/// <summary>
/// Work for the elevated helper, as plain data that crosses the pipe. The helper rebuilds the same
/// operation from it, so it never runs anything but these few file commands.
/// </summary>
/// <param name="Sources">Copy/Move/Delete: the items; Rename: the one item.</param>
/// <param name="Directory">Copy/Move: the target folder; CreateDirectory/CreateFile: the folder to create in.</param>
/// <param name="Name">Copy/Move: the new name of a single item (null keeps it); the other commands: the typed name.</param>
/// <param name="Resume">
/// The job continues an operation that stopped at "access denied": sources already moved or deleted are
/// gone and are not errors, and files already copied (same size and date) are not asked about again.
/// </param>
public sealed record ElevatedJob(
    ElevatedJobKind Kind,
    IReadOnlyList<string> Sources,
    string? Directory = null,
    string? Name = null,
    int Parallelism = 1,
    ConflictChoice? PresetConflict = null,
    bool Resume = false)
{
    /// <summary>The single-step commands (F7, Shift+F4, Shift+F6) answer with a path instead of an operation result.</summary>
    public bool IsCommand => Kind is ElevatedJobKind.CreateDirectory or ElevatedJobKind.CreateFile or ElevatedJobKind.Rename;

    public OperationKind OperationKind => Kind switch
    {
        ElevatedJobKind.Copy => OperationKind.Copy,
        ElevatedJobKind.Delete => OperationKind.Delete,
        _ => OperationKind.Move,
    };

    public static ElevatedJob CreateDirectory(string baseDirectory, string name) => new(ElevatedJobKind.CreateDirectory, [], baseDirectory, name);

    public static ElevatedJob CreateFile(string baseDirectory, string name) => new(ElevatedJobKind.CreateFile, [], baseDirectory, name);

    public static ElevatedJob Rename(string path, string newName) => new(ElevatedJobKind.Rename, [path], null, newName);

    /// <summary>The operation this job describes, built in the helper. Commands have none.</summary>
    internal IOperation CreateOperation()
    {
        var sources = Resume && Kind is ElevatedJobKind.Move or ElevatedJobKind.Delete
            ? Sources.Where(PathRules.Exists).ToArray()
            : Sources;
        return Kind switch
        {
            ElevatedJobKind.Copy or ElevatedJobKind.Move => new TransferOperation(
                Kind == ElevatedJobKind.Copy ? TransferKind.Copy : TransferKind.Move,
                sources,
                new TransferTarget(Directory ?? throw new InvalidOperationException("A transfer needs a target."), sources.Count == 1 ? Name : null),
                Parallelism,
                PresetConflict,
                skipIdentical: Resume),
            ElevatedJobKind.Delete => new DeleteOperation(sources),
            _ => throw new InvalidOperationException($"{Kind} is a command, not an operation."),
        };
    }

    /// <summary>Runs a single-step command and returns the path it created or renamed to.</summary>
    internal string RunCommand() => Kind switch
    {
        ElevatedJobKind.CreateDirectory => SimpleCommands.CreateDirectory(Directory!, Name!),
        ElevatedJobKind.CreateFile => SimpleCommands.CreateFile(Directory!, Name!),
        ElevatedJobKind.Rename => SimpleCommands.Rename(Sources[0], Name!),
        _ => throw new InvalidOperationException($"{Kind} is an operation, not a command."),
    };
}

public static class ElevationRules
{
    /// <summary>
    /// "Access denied" or "a privilege is not held": errors that running as administrator may fix.
    /// Read-only files are not among them; the operations clear that attribute themselves.
    /// </summary>
    public static bool IsAccessDenied(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,
        IOException io => (io.HResult >>> 16) == 0x8007 && (io.HResult & 0xFFFF) is 5 or 1314,
        _ => false,
    };
}
