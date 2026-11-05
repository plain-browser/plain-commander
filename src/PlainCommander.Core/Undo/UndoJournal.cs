namespace PlainCommander.Core.Undo;

/// <summary>What an operation did that can be reversed.</summary>
public enum UndoKind
{
    /// <summary>A file or folder that did not exist before (copy, new folder, extraction, a new archive).</summary>
    Created,

    /// <summary>Moved or renamed from <see cref="UndoAction.From"/> to <see cref="UndoAction.Path"/>.</summary>
    Moved,

    /// <summary>A source folder emptied and removed by a move; it comes back before its files.</summary>
    RemovedDirectory,

    /// <summary>Sent to the Recycle Bin at <see cref="UndoAction.When"/>.</summary>
    Recycled,

    /// <summary>Attributes and modification time before a change.</summary>
    AttributesChanged,

    /// <summary>An existing file replaced: its old content cannot come back.</summary>
    Overwritten,
}

public sealed record UndoAction(UndoKind Kind, string Path, string? From = null, bool IsDirectory = false, DateTime? When = null, FileAttributes Attributes = 0);

/// <summary>Filled by an operation while it runs (from several workers at once); read after it finished.</summary>
public sealed class UndoJournal
{
    private readonly List<UndoAction> _actions = [];
    private readonly object _lock = new();

    public IReadOnlyList<UndoAction> Actions
    {
        get
        {
            lock (_lock)
            {
                return _actions.ToArray();
            }
        }
    }

    /// <summary>Nothing that undo could reverse (overwritten files alone cannot be).</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _actions.All(static a => a.Kind == UndoKind.Overwritten);
            }
        }
    }

    public void Created(string path, bool isDirectory) => Add(new UndoAction(UndoKind.Created, path, IsDirectory: isDirectory));

    public void Moved(string from, string to, bool isDirectory) => Add(new UndoAction(UndoKind.Moved, to, from, isDirectory));

    public void RemovedDirectory(string path) => Add(new UndoAction(UndoKind.RemovedDirectory, path, IsDirectory: true));

    public void Recycled(string path, bool isDirectory, DateTime whenUtc) => Add(new UndoAction(UndoKind.Recycled, path, IsDirectory: isDirectory, When: whenUtc));

    public void AttributesChanged(string path, FileAttributes before, DateTime modifiedUtc) =>
        Add(new UndoAction(UndoKind.AttributesChanged, path, Attributes: before, When: modifiedUtc));

    public void Overwritten(string path) => Add(new UndoAction(UndoKind.Overwritten, path));

    public void AddRange(IEnumerable<UndoAction> actions)
    {
        lock (_lock)
        {
            _actions.AddRange(actions);
        }
    }

    private void Add(UndoAction action)
    {
        lock (_lock)
        {
            _actions.Add(action);
        }
    }
}

/// <summary>An operation that records what it did, so Ctrl+Z can reverse it.</summary>
public interface IUndoable
{
    UndoJournal Journal { get; }
}

/// <summary>One step of the undo history: what the user did and how to reverse it.</summary>
public sealed record UndoEntry(string Title, IReadOnlyList<UndoAction> Actions, DateTime At)
{
    /// <summary>How many items of this kind; created items count only at the top (a copied folder is one item).</summary>
    public int Count(UndoKind kind) =>
        kind == UndoKind.Created ? Topmost(Actions.Where(static a => a.Kind == UndoKind.Created).ToList()).Count : Actions.Count(a => a.Kind == kind);

    /// <summary>Created items not inside another created folder.</summary>
    public static List<UndoAction> Topmost(IReadOnlyList<UndoAction> created)
    {
        var folders = created.Where(static c => c.IsDirectory).Select(static c => c.Path).ToList();
        return created.Where(c => !folders.Any(f => !Operations.PathRules.Same(f, c.Path) && Operations.PathRules.IsInside(c.Path, f))).ToList();
    }
}

/// <summary>Brings a recycled item back to where it was (the Recycle Bin is Windows-only).</summary>
public interface IRecycleBinRestore
{
    /// <summary>True when the newest item recycled from <paramref name="originalPath"/> after <paramref name="sinceUtc"/> is back.</summary>
    bool Restore(string originalPath, DateTime sinceUtc);
}
