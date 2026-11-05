using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Tests.Operations;

/// <summary>Answers prompts from a script and records what was asked.</summary>
internal sealed class ScriptedPrompts : IOperationPrompts
{
    private readonly Queue<ConflictChoice> _conflicts;
    private readonly Queue<ErrorChoice> _errors;

    public ScriptedPrompts(IEnumerable<ConflictChoice>? conflicts = null, IEnumerable<ErrorChoice>? errors = null)
    {
        _conflicts = new Queue<ConflictChoice>(conflicts ?? []);
        _errors = new Queue<ErrorChoice>(errors ?? []);
    }

    public List<ConflictInfo> Conflicts { get; } = [];

    public List<Exception> Errors { get; } = [];

    /// <summary>For each error: whether "as administrator" was offered.</summary>
    public List<bool> Offers { get; } = [];

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken)
    {
        Conflicts.Add(conflict);
        return Task.FromResult(_conflicts.Count > 0 ? _conflicts.Dequeue() : throw new InvalidOperationException("Unexpected conflict: " + conflict.TargetPath));
    }

    public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken)
    {
        Errors.Add(exception);
        Offers.Add(canElevate);
        return Task.FromResult(_errors.Count > 0 ? _errors.Dequeue() : throw new InvalidOperationException("Unexpected error: " + exception.Message, exception));
    }
}

/// <summary>Synchronous IProgress, so a test can react to progress exactly when it happens.</summary>
internal sealed class InlineProgress(Action<OperationProgress> onReport) : IProgress<OperationProgress>
{
    public List<OperationProgress> Reports { get; } = [];

    public void Report(OperationProgress value)
    {
        Reports.Add(value);
        onReport(value);
    }
}

internal sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Directory.CreateTempSubdirectory("pc-ops-").FullName;
    }

    public string Root { get; }

    public string P(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Dir(params string[] parts) => Directory.CreateDirectory(P(parts)).FullName;

    public string File(string relative, string content = "x", DateTime? modifiedUtc = null)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        if (modifiedUtc is { } m)
        {
            System.IO.File.SetLastWriteTimeUtc(path, m);
        }

        return path;
    }

    public string Read(string relative) => System.IO.File.ReadAllText(P(relative));

    public void Dispose()
    {
        Unlock(new DirectoryInfo(Root));
        Directory.Delete(Root, recursive: true);
    }

    /// <summary>
    /// Makes everything deletable again. Walks by hand and never enters links: .NET's recursive
    /// enumeration follows directory links on Windows and loops forever on a link to a parent.
    /// </summary>
    private static void Unlock(DirectoryInfo dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var child in dir.EnumerateFileSystemInfos())
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (child is DirectoryInfo sub)
            {
                Unlock(sub);
            }
            else if (child.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                child.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }
}

internal static class Run
{
    public static Task<OperationResult> Async(IOperation op, ScriptedPrompts? prompts = null, IProgress<OperationProgress>? progress = null, PauseGate? gate = null, bool canElevate = false, CancellationToken token = default) =>
        op.RunAsync(new OperationContext(progress, prompts ?? new ScriptedPrompts(), gate ?? new PauseGate(), token, canElevate));
}
