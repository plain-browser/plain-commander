using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>Prompts answered by callbacks, so a test can change the disk at the exact moment a question comes.</summary>
internal sealed class HookPrompts(
    Func<ConflictInfo, ConflictChoice>? conflict = null,
    Func<string, Exception, ErrorChoice>? error = null) : IOperationPrompts
{
    public List<ConflictInfo> Conflicts { get; } = [];

    public List<Exception> Errors { get; } = [];

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo info, CancellationToken cancellationToken)
    {
        lock (Conflicts)
        {
            Conflicts.Add(info);
        }

        return Task.FromResult(conflict is null ? throw new InvalidOperationException("Unexpected conflict: " + info.TargetPath) : conflict(info));
    }

    public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken)
    {
        lock (Errors)
        {
            Errors.Add(exception);
        }

        return Task.FromResult(error is null ? throw new InvalidOperationException("Unexpected error: " + exception.Message, exception) : error(path, exception));
    }
}

/// <summary>A Recycle Bin that deletes for real and remembers what it got.</summary>
internal sealed class DeletingBin : IRecycleBin
{
    public List<string> Recycled { get; } = [];

    public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        foreach (var p in paths)
        {
            Recycled.Add(p);
            if (Directory.Exists(p) && !new DirectoryInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(p, recursive: true);
            }
            else if (Directory.Exists(p))
            {
                Directory.Delete(p);
            }
            else
            {
                File.Delete(p);
            }
        }

        return Task.CompletedTask;
    }
}

internal static class Bytes
{
    public static byte[] Pattern(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
