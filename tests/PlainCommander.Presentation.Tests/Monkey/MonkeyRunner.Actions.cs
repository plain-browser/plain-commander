using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;

namespace PlainCommander.Presentation.Tests.Monkey;

internal sealed partial class MonkeyRunner
{
    private static readonly string[] Masks = ["*.txt", "*.*", "a*", "*dir*", "*.bin;*.log", "?*1*", "ą*"];
    private static readonly string[] Filters = ["*.txt", "a", "dir", "", "*.bin", "report", "1"];
    private static readonly SortColumn[] SortColumns = [SortColumn.Name, SortColumn.Extension, SortColumn.Size, SortColumn.Modified, SortColumn.Attributes];

    /// <summary>
    /// Writing commands run only while both panels are in the world, so nothing may land next to it: a panel that
    /// wandered off (temp root, drive root) is brought back first.
    /// </summary>
    private async Task<bool> BackToWorldAsync()
    {
        foreach (var panel in new[] { _vm.Left, _vm.Right })
        {
            if (!_world.Inside(panel.Path))
            {
                // the world, or one of its folders, so the panels do not all end up in the same place
                var dirs = _world.Entries(directories: true);
                dirs.Insert(0, _world.World);
                await panel.NavigateAsync(dirs[_log.Count % Math.Min(dirs.Count, 4)]);
                _note += $" ({Side(panel)} back to the world first)";
            }
        }

        return _world.Inside(P.Path) && _world.Inside(Other.Path);
    }

    /// <summary>The panel is somewhere in the temp tree (not at the drive root, where Space or Ctrl+B would walk the disk).</summary>
    private bool Contained(PanelViewModel panel) => MonkeyWorld.IsUnder(panel.Path, _world.Root);

    private bool CanGoUp(PanelViewModel panel) =>
        panel.IsShowingResults || (Contained(panel) && !string.Equals(panel.Path, _world.Root, MonkeyWorld.PathComparison));

    private static T Pick<T>(IReadOnlyList<T> list, int draw) => list[(int)((uint)draw % list.Count)];

    private static string ItemName(FileItem? item) => item is null ? "(nothing)" : $"'{item.Name}'";

    private async Task<string> ExecuteAsync(MonkeyAction a, int step)
    {
        var p = P;
        var s = Side(p);
        switch (a.Kind)
        {
            case Act.Cursor:
                if (p.Items.Count == 0)
                {
                    return $"{s}: cursor (empty list)";
                }

                p.CurrentIndex = (int)((uint)a.A % p.Items.Count);
                return $"{s}: cursor to #{p.CurrentIndex} {ItemName(p.CurrentItem)}";

            case Act.ToggleMark:
                if (p.CurrentItem is not { } current)
                {
                    return $"{s}: Insert-toggle on nothing";
                }

                p.ToggleMark(current);
                return $"{s}: toggle mark {ItemName(current)}";

            case Act.MarkAdvance:
                {
                    var at = p.CurrentItem;
                    p.MarkCurrentAndAdvance();
                    return $"{s}: Insert on {ItemName(at)}";
                }

            case Act.MarkAll:
                p.MarkAll();
                return $"{s}: Ctrl+A";

            case Act.ClearMarks:
                p.ClearMarks();
                return $"{s}: clear marks";

            case Act.InvertMarks:
                p.InvertMarks();
                return $"{s}: Num* invert";

            case Act.MarkMask:
                {
                    var mark = a.B % 3 != 0;
                    _dialogs.MaskAnswer = a.C % 7 == 0 ? null : Pick(Masks, a.A);
                    await _vm.MarkByMaskAsync(mark);
                    return $"{s}: {(mark ? "Num+" : "Num-")} {_dialogs.MaskAnswer ?? "(cancelled)"}";
                }

            case Act.Space:
                {
                    if (!Contained(p))
                    {
                        return $"{s}: Space skipped (outside the temp tree)";
                    }

                    var at = p.CurrentItem;
                    await p.MarkCurrentAndMeasureAsync();
                    return $"{s}: Space on {ItemName(at)}";
                }

            case Act.Enter:
                {
                    var at = p.CurrentItem;
                    if (!Contained(p) || (at is { Entry.IsParentLink: true } && !CanGoUp(p)))
                    {
                        return $"{s}: Enter skipped on {ItemName(at)} (would leave the temp tree)";
                    }

                    await p.OpenCurrentAsync();
                    return $"{s}: Enter on {ItemName(at)} -> {_world.Rel(p.Path)}";
                }

            case Act.CtrlPgDn:
                {
                    var at = p.CurrentItem;
                    if (!Contained(p))
                    {
                        return $"{s}: Ctrl+PgDn skipped (outside the temp tree)";
                    }

                    await p.EnterCurrentDirectoryAsync();
                    return $"{s}: Ctrl+PgDn on {ItemName(at)} -> {_world.Rel(p.Path)}";
                }

            case Act.GoUp:
                if (!CanGoUp(p) || (string.Equals(p.Path, _world.World, MonkeyWorld.PathComparison) && !p.IsShowingResults && a.A % 4 != 0))
                {
                    return $"{s}: Backspace skipped (would leave the temp tree)";
                }

                await p.GoUpAsync();
                return $"{s}: Backspace -> {_world.Rel(p.Path)}";

            case Act.GoRoot:
                if (a.A % 4 != 0)
                {
                    await p.NavigateAsync(_world.World);
                    return $"{s}: go to the world root";
                }

                await p.GoRootAsync();
                return $"{s}: go to the drive root -> {_world.Rel(p.Path)}";

            case Act.GoTo:
                {
                    var target = GoToTarget(a.A, a.B);
                    var ok = await p.NavigateAsync(target);
                    return $"{s}: go to {_world.Rel(target)} -> {(ok ? "ok" : "failed")}, at {_world.Rel(p.Path)}";
                }

            case Act.CommandCd:
                {
                    if (!Contained(p) || !_world.Inside(p.Path))
                    {
                        return $"{s}: cd skipped (outside the world)";
                    }

                    var children = p.Items.Where(static i => !i.Entry.IsParentLink).ToList();
                    var argument = ((uint)a.A % 5) switch
                    {
                        0 when children.Count > 0 => Pick(children, a.B).Name,
                        1 when !string.Equals(p.Path, _world.World, MonkeyWorld.PathComparison) => "..",
                        2 => "no such " + (a.B % 100),
                        3 => GoToTarget(a.B, a.C),
                        _ => ".",
                    };
                    _vm.CommandLine.Text = "cd " + argument;
                    await _vm.RunCommandLineAsync();
                    return $"{s}: cd {_world.Rel(argument)} -> {_world.Rel(p.Path)}";
                }

            case Act.SwitchPanel:
                _vm.SwitchPanel();
                return $"Tab: active is {Side(P)}";

            case Act.NewTab:
                _vm.NewTab();
                return $"{s}: Ctrl+T ({p.Tabs.Count} tabs)";

            case Act.CloseTab:
                await _vm.CloseTabAsync();
                return $"{s}: Ctrl+W ({p.Tabs.Count} tabs) -> {_world.Rel(p.Path)}";

            case Act.NextTab:
                await _vm.NextTabAsync(a.A % 2 == 0 ? 1 : -1);
                return $"{s}: Ctrl+Tab -> tab {p.ActiveTabIndex} {_world.Rel(p.Path)}";

            case Act.SwitchTab:
                {
                    var index = (int)((uint)a.A % (p.Tabs.Count + 1)); // sometimes one past the end
                    await p.SwitchTabAsync(index);
                    return $"{s}: switch to tab {index} -> {_world.Rel(p.Path)}";
                }

            case Act.OpenInNewTab:
                {
                    if (!Contained(p))
                    {
                        return $"{s}: Ctrl+Enter skipped (outside the temp tree)";
                    }

                    var at = p.CurrentItem;
                    await _vm.OpenCurrentInNewTabAsync();
                    return $"{s}: open {ItemName(at)} in a new tab -> {_world.Rel(p.Path)}";
                }

            case Act.Sort:
                {
                    var column = Pick(SortColumns, a.A);
                    await p.SortByAsync(column);
                    return $"{s}: sort by {column} {p.Sort.Direction}";
                }

            case Act.QuickSearch:
                return QuickSearch(p, s, a);

            case Act.QuickFilter:
                {
                    var typed = QuickSearch(p, s, a);
                    await _vm.FilterAsync();
                    return typed + $", then Ctrl+S -> filter {p.Filter?.ToString() ?? "(none)"}";
                }

            case Act.PanelFilter:
                _dialogs.FilterAnswer = a.B % 6 == 0 ? null : Pick(Filters, a.A);
                p.ClearQuickSearch();
                await _vm.FilterAsync();
                return $"{s}: Ctrl+S filter '{_dialogs.FilterAnswer ?? "(cancelled)"}'";

            case Act.FilterOff:
                await p.SetFilterAsync((string?)null);
                return $"{s}: filter off";

            case Act.Refresh:
                await p.RefreshAsync();
                return $"{s}: Ctrl+R -> {_world.Rel(p.Path)}";

            case Act.Flat:
                if (!_world.Inside(p.Path))
                {
                    return $"{s}: Ctrl+B skipped (outside the world)";
                }

                await _vm.ToggleFlatViewAsync();
                return $"{s}: Ctrl+B -> {(p.IsFlat ? "flat" : "folder")}";

            case Act.NewFolder:
                {
                    if (!await BackToWorldAsync())
                    {
                        return $"{s}: F7 skipped (a panel is outside the world)";
                    }

                    var name = NewFolderName(p, a);
                    _dialogs.NameAnswer = name;
                    await _vm.MakeDirectoryAsync();
                    return $"{s}: F7 '{name ?? "(cancelled)"}'";
                }

            case Act.NewFile:
                {
                    if (!await BackToWorldAsync())
                    {
                        return $"{s}: Shift+F4 skipped (a panel is outside the world)";
                    }

                    var name = NewFileName(p, a);
                    _dialogs.NameAnswer = name;
                    await _vm.NewFileAsync();
                    return $"{s}: Shift+F4 '{name ?? "(cancelled)"}'";
                }

            case Act.Rename:
                {
                    if (!await BackToWorldAsync())
                    {
                        return $"{s}: Shift+F6 skipped (a panel is outside the world)";
                    }

                    var at = p.CurrentItem;
                    var name = RenameAnswer(p, a);
                    _dialogs.NameAnswer = name;
                    await _vm.RenameCurrentAsync();
                    return $"{s}: Shift+F6 {ItemName(at)} to '{name ?? "(cancelled)"}'";
                }

            case Act.Copy:
            case Act.Move:
                return await TransferAsync(p, s, a);

            case Act.Delete:
                {
                    if (!await BackToWorldAsync())
                    {
                        return $"{s}: F8 skipped (a panel is outside the world)";
                    }

                    PointAtSomething(p, a.B);
                    var sources = p.OperationSources.Select(static i => i.Entry.FullPath).ToList();
                    var permanent = a.A % 3 == 0;
                    _dialogs.DeleteAnswer = a.B % 5 != 0;
                    if (permanent && _dialogs.DeleteAnswer)
                    {
                        foreach (var source in sources)
                        {
                            AllowLoss(MonkeyWorld.IdsUnder(source));
                        }
                    }

                    var how = await RunOperationAsync(() => _vm.DeleteAsync(permanent), a.C, a.Prompt, sources);
                    return $"{s}: {(permanent ? "Shift+F8" : "F8")} {sources.Count} item(s) [{string.Join(", ", sources.Select(Path.GetFileName))}] answer {(_dialogs.DeleteAnswer ? "yes" : "no")}{how}";
                }

            case Act.Undo:
                {
                    if (!await BackToWorldAsync())
                    {
                        return "Ctrl+Z skipped (a panel is outside the world)";
                    }

                    var title = _vm.UndoTitle;
                    _dialogs.UndoAnswer = a.B % 5 != 0;
                    var how = await RunOperationAsync(_vm.UndoAsync, a.C, a.Prompt, [_world.World]);
                    return $"Ctrl+Z '{title ?? "(nothing)"}' answer {(_dialogs.UndoAnswer ? "yes" : "no")}{how}";
                }

            case Act.Repeat:
                if (step == 0 || _actions[step - 1].Kind == Act.Repeat)
                {
                    return "repeat: nothing";
                }

                return "repeat: " + await ExecuteAsync(_actions[step - 1], step - 1);

            case Act.Search:
                return await SearchAsync(p, s, a);

            case Act.History:
                {
                    if (p.History.Count == 0)
                    {
                        return $"{s}: history empty";
                    }

                    var target = Pick(p.History, a.A);
                    var ok = await p.NavigateAsync(target);
                    return $"{s}: history -> {_world.Rel(target)} {(ok ? "ok" : "failed")}";
                }

            case Act.Swap:
                await _vm.SwapPanelsAsync();
                return $"Ctrl+U: L={_world.Rel(_vm.Left.Path)} R={_world.Rel(_vm.Right.Path)}";

            case Act.Mirror:
                {
                    if (!Contained(p))
                    {
                        return "Ctrl+arrow skipped (outside the temp tree)";
                    }

                    await _vm.MirrorAsync(a.A % 2 == 0 ? PanelSide.Left : PanelSide.Right);
                    return $"{s}: Ctrl+arrow -> other at {_world.Rel(Other.Path)}";
                }

            case Act.SameFolder:
                await Other.NavigateAsync(p.Path);
                return $"{Side(Other)}: same folder as {s} ({_world.Rel(p.Path)})";

            case Act.External:
                {
                    var (what, destroyed) = await Task.Run(() => _world.Change(a.B, a.A, a.C, [_vm.Left.Path, _vm.Right.Path], []));
                    AllowLoss(destroyed);
                    return "disk: " + what;
                }

            case Act.Wait:
                await Task.Delay((int)((uint)a.A % 50));
                return "wait";

            default:
                throw new InvalidOperationException(a.Kind.ToString());
        }
    }

    private string GoToTarget(int a, int b)
    {
        var dirs = _world.Entries(directories: true);
        var files = _world.Entries(directories: false);
        return ((uint)a % 9) switch
        {
            0 or 1 when dirs.Count > 0 => Pick(dirs, b),
            2 => Path.Combine(_world.World, "missing " + (b % 50)),
            3 when dirs.Count > 0 => Path.Combine(Pick(dirs, b), "deeper missing", "x"),
            4 when files.Count > 0 => Pick(files, b),
            5 => _world.Root,
            6 when dirs.Count > 0 => Pick(dirs, b) + Path.DirectorySeparatorChar,
            7 => _world.Sentinel,
            _ => _world.World,
        };
    }

    private static string QuickSearch(PanelViewModel p, string s, MonkeyAction a)
    {
        var names = p.Items.Where(static i => !i.Entry.IsParentLink).Select(static i => i.Name).ToList();
        switch ((uint)a.B % 5)
        {
            case 0:
                p.QuickSearchBackspace();
                return $"{s}: quick search backspace -> '{p.QuickSearchText}'";
            case 1:
                p.QuickSearchNext(a.C % 2 == 0);
                return $"{s}: quick search next -> {ItemName(p.CurrentItem)}";
            case 2:
                p.ClearQuickSearch();
                return $"{s}: quick search cleared";
            default:
                {
                    var source = names.Count == 0 ? "zq" : Pick(names, a.A);
                    var count = 1 + (int)((uint)a.C % 3);
                    var typed = new string(source.Take(count).ToArray());
                    foreach (var c in typed)
                    {
                        p.QuickSearchType(c);
                    }

                    return $"{s}: quick search type '{typed}' -> '{p.QuickSearchText}' at {ItemName(p.CurrentItem)}";
                }
        }
    }

    private string? NewFolderName(PanelViewModel p, MonkeyAction a)
    {
        var sep = Path.DirectorySeparatorChar;
        var existing = p.Items.Where(static i => !i.Entry.IsParentLink).Select(static i => i.Name).ToList();
        var name = ((uint)a.A % 9) switch
        {
            0 => null,
            1 => $"{_world.NewName(a.B, file: false)}{sep}inner{sep}deep",
            2 when existing.Count > 0 => Pick(existing, a.B),
            3 => "bad<>|name?",
            4 => "  " + _world.NewName(a.B, file: false) + " .",
            5 => $"..{sep}{_world.NewName(a.B, file: false)}",
            6 => "",
            7 => new string('n', 300),
            _ => _world.NewName(a.B, file: false),
        };
        return InsideOrSafe(p, name, a.B);
    }

    private string? NewFileName(PanelViewModel p, MonkeyAction a)
    {
        var existing = p.Items.Where(static i => !i.Entry.IsParentLink).Select(static i => i.Name).ToList();
        var name = ((uint)a.A % 7) switch
        {
            0 => null,
            1 when existing.Count > 0 => Pick(existing, a.B),
            2 => "bad:name*?.txt",
            3 => " " + _world.NewName(a.B, file: true) + ". ",
            4 => "",
            5 when existing.Count > 0 => Pick(existing, a.B).ToUpperInvariant(),
            _ => _world.NewName(a.B, file: true),
        };
        return InsideOrSafe(p, name, a.B);
    }

    private string? RenameAnswer(PanelViewModel p, MonkeyAction a)
    {
        var current = p.CurrentItem?.Name ?? "x";
        var existing = p.Items.Where(static i => !i.Entry.IsParentLink).Select(static i => Path.GetFileName(i.Entry.FullPath)).ToList();
        var name = ((uint)a.A % 11) switch
        {
            0 => null,
            1 when existing.Count > 0 => Pick(existing, a.B),
            2 => SwapCase(Path.GetFileName(current)),
            3 => "bad<>|:name",
            4 => "  " + _world.NewName(a.B, file: true) + "  ",
            5 => _world.NewName(a.B, file: true) + ".",
            6 => "..",
            7 => new string('r', 300),
            8 => $"sub{Path.DirectorySeparatorChar}{_world.NewName(a.B, file: true)}",
            9 => Path.GetFileName(current),
            _ => _world.NewName(a.B, file: true),
        };
        return InsideOrSafe(p, name, a.B);
    }

    private static string SwapCase(string name) =>
        new(name.Select(static c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());

    /// <summary>A typed name that would land outside the world (via "..") becomes a plain new name.</summary>
    private string? InsideOrSafe(PanelViewModel p, string? name, int draw)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.Contains("..", StringComparison.Ordinal))
        {
            return name;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(p.Path, name.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return name;
        }

        return _world.Inside(full) && !string.Equals(full, _world.World, MonkeyWorld.PathComparison) ? name : _world.NewName(draw, file: false);
    }

    private async Task<string> TransferAsync(PanelViewModel p, string s, MonkeyAction a)
    {
        var verb = a.Kind == Act.Copy ? "F5" : "F6";
        if (!await BackToWorldAsync())
        {
            return $"{s}: {verb} skipped (a panel is outside the world)";
        }

        PointAtSomething(p, a.B);
        var items = p.OperationSources;
        var sources = items.Select(static i => i.Entry.FullPath).ToList();
        var answer = TransferAnswer(p, items, a.A, a.B);
        _dialogs.TransferAnswer = answer;
        var protect = new List<string>(sources);
        if (answer is not null)
        {
            try
            {
                var target = Path.GetFullPath(Path.Combine(p.Path, answer.Trim()));
                protect.Add(Path.TrimEndingDirectorySeparator(target));
                if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target)) is { } parent)
                {
                    protect.Add(parent);
                }

                if ((uint)a.C / 7 % 2 == 0)
                {
                    PlaceClashes(target, sources, a.B);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
            }
        }

        var how = await RunOperationAsync(a.Kind == Act.Copy ? _vm.CopyAsync : _vm.MoveAsync, a.C, a.Prompt, protect);
        return $"{s}: {verb} {sources.Count} item(s) [{string.Join(", ", sources.Select(Path.GetFileName))}] to '{(answer is null ? "(cancelled)" : _world.Rel(answer))}'{how}";
    }

    /// <summary>The cursor on "..": F5/F6/F8 would do nothing, so (half of the time) it moves to a real row first.</summary>
    private void PointAtSomething(PanelViewModel p, int draw)
    {
        if (p.OperationSources.Count > 0 || draw % 2 == 0)
        {
            return;
        }

        var rows = Enumerable.Range(0, p.Items.Count).Where(i => !p.Items[i].Entry.IsParentLink).ToList();
        if (rows.Count > 0)
        {
            p.CurrentIndex = Pick(rows, draw / 2);
            _note += $" (cursor to {ItemName(p.CurrentItem)} first)";
        }
    }

    /// <summary>"Another program" puts items with the sources' names into the target folder: conflicts follow.</summary>
    private void PlaceClashes(string target, IReadOnlyList<string> sources, int draw)
    {
        if (!Directory.Exists(target) || !_world.Inside(target))
        {
            return;
        }

        foreach (var source in sources.Take(2))
        {
            var clash = Path.Combine(target, Path.GetFileName(source));
            if (Path.Exists(clash))
            {
                continue;
            }

            if (Directory.Exists(source) && draw % 2 == 0)
            {
                Directory.CreateDirectory(clash);
                if (Directory.EnumerateFiles(source).FirstOrDefault() is { } inner)
                {
                    _world.WriteFile(Path.Combine(clash, Path.GetFileName(inner)), 50 + (int)((uint)draw % 300));
                }
            }
            else
            {
                _world.WriteFile(clash, 100 + (int)((uint)draw % 500));
            }

            _note += $" (clash {_world.Rel(clash)} placed first)";
        }
    }

    private string? TransferAnswer(PanelViewModel p, IReadOnlyList<FileItem> items, int a, int b)
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        var dirs = _world.Entries(directories: true);
        var files = _world.Entries(directories: false);
        var first = items.Count > 0 ? items[0] : null;
        string? answer = ((uint)a % 14) switch
        {
            0 or 1 or 2 => Other.Path + sep,
            3 => null,
            4 => "copy of " + _world.NewName(b, file: true),
            5 when first is not null => Path.GetFileName(first.Entry.FullPath),
            6 when dirs.Count > 0 => Pick(dirs, b) + sep,
            7 => Path.Combine(_world.World, _world.NewName(b, file: false)) + sep,
            8 => ".",
            9 when files.Count > 0 => Pick(files, b),
            10 => p.Path + sep,
            11 when first is not null => Path.Combine(first.Entry.FullPath, "inner") + sep,
            12 => "bad<>|:name",
            13 => ".." + sep,
            _ => Other.Path + sep,
        };

        if (answer is null)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(p.Path, answer.Trim()));
            return _world.Inside(full) ? answer : Other.Path + sep;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return answer;
        }
    }

    /// <summary>
    /// Starts a queued command and, by <paramref name="mode"/>, lets it run, pauses it while the disk changes and
    /// then resumes or cancels it, cancels it at once, or changes the disk from another thread while it runs.
    /// </summary>
    private async Task<string> RunOperationAsync(Func<Task> start, int mode, int draw, IReadOnlyList<string> protect)
    {
        var folders = new[] { _vm.Left.Path, _vm.Right.Path };
        var task = start();
        var op = _vm.Operations.Current;
        var how = string.Empty;
        var kind = (uint)mode % 6;
        if (kind is 0 or 1)
        {
            await task;
            return how;
        }

        if (op is null || task.IsCompleted)
        {
            await task;
            return " [done before it could be interrupted]";
        }

        switch (kind)
        {
            case 2:
                op.TogglePause();
                how = " [paused; " + await ChangeInBackgroundAsync(draw, folders, protect) + "; resumed]";
                op.TogglePause();
                break;
            case 3:
                op.TogglePause();
                how = " [paused; " + await ChangeInBackgroundAsync(draw, folders, protect) + "; cancelled]";
                op.Cancel();
                break;
            case 4:
                op.Cancel();
                how = " [cancelled at once]";
                break;
            default:
                {
                    var changes = Task.Run(() => ChangeSeveral(draw, folders, protect));
                    await task;
                    how = " [while running: " + await changes + "]";
                    break;
                }
        }

        await task;
        return how;
    }

    private Task<string> ChangeInBackgroundAsync(int draw, string[] folders, IReadOnlyList<string> protect) =>
        Task.Run(() => ChangeSeveral(draw, folders, protect));

    private string ChangeSeveral(int draw, string[] folders, IReadOnlyList<string> protect)
    {
        var rng = new Random(draw);
        var done = new List<string>();
        for (var i = 1 + rng.Next(3); i > 0; i--)
        {
            var (what, destroyed) = _world.Change(rng.Next(), rng.Next(), rng.Next(), folders, protect);
            AllowLoss(destroyed);
            done.Add(what);
        }

        return string.Join("; ", done);
    }

    private async Task<string> SearchAsync(PanelViewModel p, string s, MonkeyAction a)
    {
        var root = _world.Inside(p.Path) && !p.IsShowingResults ? p.Path : _world.World;
        using var search = new SearchViewModel(root) { Masks = Pick(Masks, a.A) };
        if (a.B % 4 == 0)
        {
            search.Text = "PCID:0000";
        }

        var running = search.StartAsync();
        var stopped = a.C % 3 == 0;
        if (stopped)
        {
            search.Stop();
        }

        await running;
        var found = search.Results.Select(static r => r.Entry).ToList();
        if (a.C % 2 == 0 && _world.Inside(p.Path))
        {
            _vm.ShowSearchResults(search.Masks, root, found);
            return $"{s}: Alt+F7 '{search.Masks}' in {_world.Rel(root)}{(stopped ? " stopped" : string.Empty)}: {found.Count} found, shown in the panel";
        }

        return $"{s}: Alt+F7 '{search.Masks}' in {_world.Rel(root)}{(stopped ? " stopped" : string.Empty)}: {found.Count} found";
    }
}
