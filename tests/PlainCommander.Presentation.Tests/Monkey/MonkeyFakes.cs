using PlainCommander.Core.Operations;

namespace PlainCommander.Presentation.Tests.Monkey;

/// <summary>
/// Conflict and error answers drawn from a seed that the runner resets before each step, so a replayed step gets
/// the same answers. Called from the operation's thread.
/// </summary>
internal sealed class MonkeyPrompts : IOperationPrompts
{
    private readonly Lock _lock = new();
    private Random _rng = new(0);
    private int _retries;

    public List<string> Log { get; } = [];

    /// <summary>Ids that an "overwrite" answer may destroy; null once an "… all" answer made that unknowable.</summary>
    public HashSet<string>? Overwritten { get; private set; } = [];

    public void Reset(int seed)
    {
        lock (_lock)
        {
            _rng = new Random(seed);
            _retries = 0;
            Log.Clear();
            Overwritten = [];
        }
    }

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ConflictChoice[] choices =
            [
                ConflictChoice.Overwrite, ConflictChoice.Skip, ConflictChoice.KeepBoth, ConflictChoice.Cancel,
                ConflictChoice.Overwrite, ConflictChoice.Skip, ConflictChoice.KeepBoth,
                ConflictChoice.OverwriteAll, ConflictChoice.SkipAll, ConflictChoice.KeepBothAll, ConflictChoice.OverwriteAllOlder,
            ];
            var choice = choices[_rng.Next(choices.Length)];
            Log.Add($"conflict {Path.GetFileName(conflict.TargetPath)} -> {choice}");
            if (choice == ConflictChoice.Overwrite)
            {
                Overwritten?.UnionWith(MonkeyWorld.IdsUnder(conflict.TargetPath));
            }
            else if (choice is ConflictChoice.OverwriteAll or ConflictChoice.OverwriteAllOlder)
            {
                Overwritten = null;
            }

            return Task.FromResult(choice);
        }
    }

    public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var choice = _rng.Next(4) switch
            {
                0 when _retries++ < 2 => ErrorChoice.Retry,
                0 or 1 => ErrorChoice.Skip,
                2 => ErrorChoice.SkipAll,
                _ => ErrorChoice.Abort,
            };
            Log.Add($"error {Path.GetFileName(path)} ({exception.GetType().Name}) -> {choice}");
            return Task.FromResult(choice);
        }
    }
}

/// <summary>
/// A Recycle Bin that keeps what it gets (in the world's bin folder), so recycled ids are not "lost", and can put
/// an item back for Ctrl+Z like the Windows one.
/// </summary>
internal sealed class MonkeyBin(string folder) : IRecycleBin, Core.Undo.IRecycleBinRestore
{
    private readonly Lock _lock = new();
    private readonly List<(string Original, string Kept, DateTime When)> _items = [];
    private int _next;

    public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            var original = Path.TrimEndingDirectorySeparator(path);
            var slot = Directory.CreateDirectory(Path.Combine(folder, Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
            var target = Path.Combine(slot, Path.GetFileName(original));
            if (Directory.Exists(original))
            {
                Directory.Move(original, target);
            }
            else
            {
                File.Move(original, target);
            }

            lock (_lock)
            {
                _items.Add((original, target, DateTime.UtcNow));
            }
        }

        return Task.CompletedTask;
    }

    public bool Restore(string originalPath, DateTime sinceUtc)
    {
        lock (_lock)
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                var (original, kept, when) = _items[i];
                if (!string.Equals(original, Path.TrimEndingDirectorySeparator(originalPath), MonkeyWorld.PathComparison) || when < sinceUtc)
                {
                    continue;
                }

                if (Path.Exists(originalPath) || !Path.Exists(kept))
                {
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                if (Directory.Exists(kept))
                {
                    Directory.Move(kept, original);
                }
                else
                {
                    File.Move(kept, original);
                }

                _items.RemoveAt(i);
                return true;
            }

            return false;
        }
    }
}
