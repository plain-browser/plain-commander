using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Formatting;
using PlainCommander.Core.Operations;

namespace PlainCommander.Presentation;

/// <summary>One difference in the synchronize window; Space or a click cycles the direction.</summary>
public sealed partial class SyncRow : ObservableObject
{
    /// <param name="left">What the left side has (from the comparison; null: nothing).</param>
    /// <param name="right">What the right side has.</param>
    public SyncRow(SyncItem item, string leftRoot, string rightRoot, CompareEntry? left, CompareEntry? right)
    {
        ArgumentNullException.ThrowIfNull(item);
        Relative = item.Relative;
        Difference = item.Difference;
        DefaultAction = item.Action;
        Action = item.Action;
        LeftPath = SyncOperation.Join(leftRoot, item.Relative);
        RightPath = SyncOperation.Join(rightRoot, item.Relative);
        LeftInfo = Describe(left);
        RightInfo = Describe(right);
        CanDiff = left is { IsDirectory: false } && right is { IsDirectory: false };
    }

    public string Relative { get; }

    public Difference Difference { get; }

    public SyncAction DefaultAction { get; }

    public string LeftPath { get; }

    public string RightPath { get; }

    public string LeftInfo { get; }

    public string RightInfo { get; }

    public string DifferenceText => Texts.SyncDifference(Difference);

    /// <summary>Both sides are files: the text diff can show what changed.</summary>
    public bool CanDiff { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionGlyph))]
    public partial SyncAction Action { get; set; }

    public string ActionGlyph => Action switch
    {
        SyncAction.CopyToRight => "→",
        SyncAction.CopyToLeft => "←",
        _ => "·",
    };

    /// <summary>→, ←, nothing, → …</summary>
    public void Cycle() => Action = Action switch
    {
        SyncAction.CopyToRight => SyncAction.CopyToLeft,
        SyncAction.CopyToLeft => SyncAction.None,
        _ => SyncAction.CopyToRight,
    };

    public SyncItem ToItem() => new(Relative, Difference, Action);

    private static string Describe(CompareEntry? entry)
    {
        var culture = CultureInfo.CurrentCulture;
        return entry switch
        {
            null => "—",
            { IsDirectory: true } => EntryFormatter.DirectoryMarker,
            _ => $"{entry.Size.ToString("N0", culture)}  {entry.Modified.LocalDateTime.ToString(EntryFormatter.ColumnDateTimePattern(culture), culture)}",
        };
    }
}

/// <summary>
/// The synchronize window after Shift+F2: every difference with a direction (newer or only-here goes across by
/// default; "different" and file-versus-folder wait for the user), bulk choices, and the operation that carries it out.
/// </summary>
public sealed partial class SyncViewModel : ObservableObject
{
    public SyncViewModel(string left, string right, CompareResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Left = left;
        Right = right;
        foreach (var item in SyncPlan.From(result))
        {
            var (l, r) = result.EntriesOf(item.Relative);
            var row = new SyncRow(item, left, right, l, r);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }

        UpdateSummary();
    }

    public string Left { get; }

    public string Right { get; }

    public ObservableCollection<SyncRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanSync { get; private set; }

    public int ActionCount => Rows.Count(r => r.Action != SyncAction.None);

    public void SetAll(SyncAction action)
    {
        foreach (var row in Rows)
        {
            row.Action = action;
        }
    }

    public void ResetDefaults()
    {
        foreach (var row in Rows)
        {
            row.Action = row.DefaultAction;
        }
    }

    /// <param name="remote">The connections, when one side is a server.</param>
    public SyncOperation BuildOperation(int parallelism, Core.Remote.RemoteConnections? remote = null) =>
        new(Left, Right, Rows.Select(r => r.ToItem()).ToList(), parallelism, remote);

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SyncRow.Action))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        var right = Rows.Count(r => r.Action == SyncAction.CopyToRight);
        var left = Rows.Count(r => r.Action == SyncAction.CopyToLeft);
        Summary = Texts.SyncSummary(right, left, Rows.Count - right - left);
        CanSync = right + left > 0;
    }
}

/// <summary>Side-by-side text diff of two files, for the synchronize window.</summary>
public sealed partial class DiffViewModel : ObservableObject
{
    private readonly Func<string, CancellationToken, Task<string>>? _localCopy;

    /// <param name="localCopy">Turns a server path into a local copy to read (the synchronize window passes it).</param>
    public DiffViewModel(string leftPath, string rightPath, Func<string, CancellationToken, Task<string>>? localCopy = null)
    {
        LeftPath = leftPath;
        RightPath = rightPath;
        _localCopy = localCopy;
    }

    public string LeftPath { get; }

    public string RightPath { get; }

    [ObservableProperty]
    public partial IReadOnlyList<DiffRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    public async Task LoadAsync(CancellationToken token = default)
    {
        IsLoading = true;
        try
        {
            var left = _localCopy is null ? LeftPath : await _localCopy(LeftPath, token);
            var right = _localCopy is null ? RightPath : await _localCopy(RightPath, token);
            var pair = await TextDiff.ReadPairAsync(left, right, token);
            if (pair is not { } p)
            {
                // not text: say whether the bytes are the same, and where they part
                var first = await BinaryCompare.FirstDifferenceAsync(left, right, token);
                Error = first is { } offset ? Texts.DiffBinaryAt(offset, new FileInfo(left).Length, new FileInfo(right).Length) : Texts.DiffBinarySame;
                return;
            }

            var result = await Task.Run(() => TextDiff.Compare(p.Left, p.Right, token), token);
            Rows = result.Rows;
            Summary = Texts.DiffSummary(result.Added, result.Removed, result.Changed);
        }
        catch (InvalidOperationException)
        {
            Error = Texts.DiffTooDifferent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = UserMessages.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// First row of the next (or previous) block of changes after (before) <paramref name="from"/>; -1 when there is none.
    /// A block is a run of rows that are not <see cref="DiffKind.Same"/>.
    /// </summary>
    public int FindChange(int from, bool forward)
    {
        bool IsStart(int i) => Rows[i].Kind != DiffKind.Same && (i == 0 || Rows[i - 1].Kind == DiffKind.Same);
        if (forward)
        {
            for (var i = Math.Max(from + 1, 0); i < Rows.Count; i++)
            {
                if (IsStart(i))
                {
                    return i;
                }
            }
        }
        else
        {
            for (var i = Math.Min(from - 1, Rows.Count - 1); i >= 0; i--)
            {
                if (IsStart(i))
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
