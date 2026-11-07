namespace PlainCommander.Presentation.Tests.Monkey;

/// <summary>What the monkey can do. Everything goes through the same view model calls the window makes.</summary>
internal enum Act
{
    Cursor,
    ToggleMark,
    MarkAdvance,
    MarkAll,
    ClearMarks,
    InvertMarks,
    MarkMask,
    Space,
    Enter,
    CtrlPgDn,
    GoUp,
    GoRoot,
    GoTo,
    CommandCd,
    SwitchPanel,
    NewTab,
    CloseTab,
    NextTab,
    SwitchTab,
    OpenInNewTab,
    Sort,
    QuickSearch,
    QuickFilter,
    PanelFilter,
    FilterOff,
    Refresh,
    Flat,
    NewFolder,
    NewFile,
    Rename,
    Copy,
    Move,
    Delete,
    Undo,
    Repeat,
    Search,
    History,
    Swap,
    Mirror,
    SameFolder,
    External,
    Wait,
}

/// <summary>
/// One step: the kind plus raw draws. The draws are mapped onto whatever the panels show when the step runs, so a
/// list of actions can be replayed (and shortened) without the random generator.
/// </summary>
internal readonly record struct MonkeyAction(Act Kind, int A, int B, int C, int Prompt)
{
    private static readonly (Act Kind, int Weight)[] Weights =
    [
        (Act.Cursor, 8), (Act.ToggleMark, 4), (Act.MarkAdvance, 3), (Act.MarkAll, 2), (Act.ClearMarks, 2),
        (Act.InvertMarks, 2), (Act.MarkMask, 2), (Act.Space, 2), (Act.Enter, 6), (Act.CtrlPgDn, 1), (Act.GoUp, 4),
        (Act.GoRoot, 1), (Act.GoTo, 4), (Act.CommandCd, 2), (Act.SwitchPanel, 4), (Act.NewTab, 2), (Act.CloseTab, 2),
        (Act.NextTab, 2), (Act.SwitchTab, 1), (Act.OpenInNewTab, 1), (Act.Sort, 2), (Act.QuickSearch, 3),
        (Act.QuickFilter, 1), (Act.PanelFilter, 2), (Act.FilterOff, 1), (Act.Refresh, 2), (Act.Flat, 1),
        (Act.NewFolder, 3), (Act.NewFile, 3), (Act.Rename, 4), (Act.Copy, 5), (Act.Move, 5), (Act.Delete, 4),
        (Act.Undo, 4), (Act.Repeat, 2), (Act.Search, 2), (Act.History, 2), (Act.Swap, 1), (Act.Mirror, 1),
        (Act.SameFolder, 1), (Act.External, 8), (Act.Wait, 1),
    ];

    public static List<MonkeyAction> Generate(int seed, int steps)
    {
        var rng = new Random(seed);
        var total = Weights.Sum(static w => w.Weight);
        var list = new List<MonkeyAction>(steps);
        for (var i = 0; i < steps; i++)
        {
            var pick = rng.Next(total);
            var kind = Weights[^1].Kind;
            foreach (var (k, w) in Weights)
            {
                if (pick < w)
                {
                    kind = k;
                    break;
                }

                pick -= w;
            }

            list.Add(new MonkeyAction(kind, rng.Next(), rng.Next(), rng.Next(), rng.Next()));
        }

        return list;
    }

    /// <summary>C# to paste into a regression test or a replay.</summary>
    public string ToCode() => $"new(Act.{Kind}, {A}, {B}, {C}, {Prompt})";
}
