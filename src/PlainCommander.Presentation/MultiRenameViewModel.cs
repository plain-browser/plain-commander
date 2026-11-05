using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Rename;
using Serilog;

namespace PlainCommander.Presentation;

/// <summary>One row of the Ctrl+M preview.</summary>
public sealed record RenameRow(string OldName, string NewName, string ProblemText, bool HasProblem, bool Changes);

/// <summary>
/// Ctrl+M: rename many files with masks, search/replace, case change and a counter. The preview is
/// recomputed on every change; nothing touches the disk until <see cref="ApplyAsync"/>, which refuses to run
/// while any row has a problem. The last batch can be undone.
/// </summary>
public sealed partial class MultiRenameViewModel : ObservableObject
{
    private static readonly HashSet<string> RuleProperties =
    [
        nameof(NameMask), nameof(ExtensionMask), nameof(Search), nameof(Replace), nameof(UseRegex), nameof(CaseSensitive),
        nameof(CaseIndex), nameof(CounterStart), nameof(CounterStep), nameof(CounterDigits), nameof(Transliterate),
    ];

    private readonly Func<string, bool> _exists;
    private List<RenameSource> _sources;
    private IReadOnlyList<RenamePreview> _plan = [];
    private RenameOutcome? _last;

    public MultiRenameViewModel(IReadOnlyList<FileItem> items, Func<string, bool>? exists = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        _exists = exists ?? (p => File.Exists(p) || Directory.Exists(p));
        _sources = items.Select(i => new RenameSource(i.Entry.FullPath, Path.GetFileName(i.Entry.FullPath), i.IsDirectory, i.Entry.Modified)).ToList();
        Recompute();
    }

    [ObservableProperty]
    public partial string NameMask { get; set; } = "[N]";

    [ObservableProperty]
    public partial string ExtensionMask { get; set; } = "[E]";

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Replace { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial bool CaseSensitive { get; set; }

    /// <summary>Latin letters only: no diacritics, Cyrillic and Greek spelled in Latin.</summary>
    [ObservableProperty]
    public partial bool Transliterate { get; set; }

    /// <summary>Index into <see cref="CaseChange"/> for a ComboBox.</summary>
    [ObservableProperty]
    public partial int CaseIndex { get; set; }

    [ObservableProperty]
    public partial double CounterStart { get; set; } = 1;

    [ObservableProperty]
    public partial double CounterStep { get; set; } = 1;

    [ObservableProperty]
    public partial double CounterDigits { get; set; } = 1;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanApply { get; private set; }

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public ObservableCollection<RenameRow> Rows { get; } = [];

    public int Count => _sources.Count;

    public RenameRules Rules => new(
        NameMask,
        ExtensionMask,
        Search,
        Replace,
        UseRegex,
        CaseSensitive,
        (CaseChange)Math.Clamp(CaseIndex, 0, 4),
        (long)(double.IsNaN(CounterStart) ? 1 : CounterStart),
        (long)(double.IsNaN(CounterStep) ? 1 : CounterStep),
        (int)Math.Clamp(double.IsNaN(CounterDigits) ? 1 : CounterDigits, 1, 12),
        Transliterate);

    public void Recompute()
    {
        _plan = MultiRename.Preview(_sources, Rules, _exists);
        Rows.Clear();
        foreach (var p in _plan)
        {
            Rows.Add(new RenameRow(p.Source.Name, p.NewName, Texts.RenameProblem(p.Problem), p.Problem != RenameProblem.None, p.Changes));
        }

        var changes = _plan.Count(p => p.Changes);
        var problems = _plan.Count(p => p.Problem != RenameProblem.None);
        Summary = Texts.RenameSummary(changes, problems);
        CanApply = !IsBusy && changes > 0 && problems == 0;
    }

    /// <summary>Renames everything in the preview. Returns what happened, or null when there was nothing to do.</summary>
    public async Task<RenameOutcome?> ApplyAsync()
    {
        if (!CanApply)
        {
            return null;
        }

        IsBusy = true;
        CanApply = false;
        try
        {
            var plan = _plan;
            var outcome = await Task.Run(() => MultiRename.Apply(plan));
            _last = outcome;
            Log.Information("Multi-rename: {Done} renamed, {Failed} failed", outcome.Done.Count, outcome.Failed.Count);
            Follow(outcome);
            ResetRules();
            Summary = Texts.RenameDone(outcome.Done.Count, outcome.Failed.Count);
            CanUndo = outcome.Done.Count > 0;
            return outcome;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<RenameOutcome?> UndoAsync()
    {
        if (_last is not { } last || !CanUndo)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var outcome = await Task.Run(() => MultiRename.Undo(last));
            Follow(outcome);
            Recompute();
            Summary = Texts.RenameUndone(outcome.Done.Count, outcome.Failed.Count);
            _last = null;
            CanUndo = false;
            return outcome;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && RuleProperties.Contains(name))
        {
            Recompute();
        }
    }

    /// <summary>The items now live under their new names; later previews and undo start from there.</summary>
    private void Follow(RenameOutcome outcome)
    {
        var moved = outcome.Done.ToDictionary(d => d.From, d => d.To, StringComparer.Ordinal);
        _sources = _sources
            .Select(s => moved.TryGetValue(s.FullPath, out var to) ? s with { FullPath = to, Name = Path.GetFileName(to) } : s)
            .ToList();
    }

    /// <summary>After a rename the masks go back to "no change", so the preview does not offer to rename twice.</summary>
    private void ResetRules()
    {
        NameMask = "[N]";
        ExtensionMask = "[E]";
        Search = string.Empty;
        Replace = string.Empty;
        CaseIndex = 0;
        Transliterate = false;
        Recompute();
    }
}
