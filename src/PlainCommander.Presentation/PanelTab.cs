using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Model;

namespace PlainCommander.Presentation;

/// <summary>
/// One tab of a panel: where it was, how it was sorted and viewed, and where the cursor stood. The panel itself shows
/// only the active tab; the others are just this record until the user switches to them.
/// </summary>
public sealed partial class PanelTab : ObservableObject
{
    [ObservableProperty]
    public partial string Path { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public SortSpec Sort { get; set; } = new();

    public bool BriefView { get; set; }

    public Core.Details.DetailColumn Detail { get; set; }

    /// <summary>Name under the cursor when the tab was left, so coming back lands on the same row.</summary>
    public string? CursorName { get; set; }

    /// <summary>"work" for C:\work, "C:" for a drive root, the archive name inside an archive.</summary>
    public static string TitleOf(string path)
    {
        var segments = PathSegment.Split(path);
        return segments.Count == 0 ? string.Empty : segments[^1].Name;
    }

    public void MoveTo(string path)
    {
        Path = path;
        Title = TitleOf(path);
    }
}
