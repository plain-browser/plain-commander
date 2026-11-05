namespace PlainCommander.Core.Compare;

public enum SyncAction
{
    /// <summary>Leave both sides as they are.</summary>
    None,
    CopyToRight,
    CopyToLeft,
}

/// <summary>One difference and what synchronizing will do with it. <see cref="Relative"/> uses "\" as the separator.</summary>
public sealed record SyncItem(string Relative, Difference Difference, SyncAction Action)
{
    /// <summary>
    /// Newer or only-here goes to the other side. "Different" (same time, other size or content) and a file facing
    /// a folder have no safe default: the user decides.
    /// </summary>
    public static SyncAction DefaultAction(Difference difference) => difference switch
    {
        Difference.OnlyLeft or Difference.LeftNewer => SyncAction.CopyToRight,
        Difference.OnlyRight or Difference.RightNewer => SyncAction.CopyToLeft,
        _ => SyncAction.None,
    };
}

public static class SyncPlan
{
    /// <summary>The comparison as a list of items with default actions, sorted by path.</summary>
    public static IReadOnlyList<SyncItem> From(CompareResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Items
            .OrderBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .Select(i => new SyncItem(i.Key, i.Value, SyncItem.DefaultAction(i.Value)))
            .ToList();
    }
}
