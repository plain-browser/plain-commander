using static PlainCommander.Presentation.Tests.Monkey.MonkeyAssert;

namespace PlainCommander.Presentation.Tests.Monkey;

internal sealed partial class MonkeyRunner
{
    /// <summary>Everything that must hold once the step settled; with <paramref name="fullRefresh"/> also "the list is the disk".</summary>
    private async Task CheckAsync(bool fullRefresh)
    {
        Check(_ui.Crashes.Count == 0, "UI thread crash", string.Join(Environment.NewLine, _ui.Crashes));
        lock (_errors)
        {
            Check(_errors.Count == 0, "error logged", string.Join(Environment.NewLine, _errors));
        }

        var queue = _vm.Operations;
        Check(!queue.IsBusy && queue.Current is null && queue.PendingCount == 0, "queue idle", $"busy={queue.IsBusy} current={queue.Current?.Title} pending={queue.PendingCount}");

        var outside = _world.OutsideSnapshot();
        Check(outside == _outside, "nothing written outside the world", $"before:{Environment.NewLine}{_outside}after:{Environment.NewLine}{outside}");

        var ids = _world.AllIds();
        if (_prompts.Overwritten is { } overwritten)
        {
            List<string> lost;
            lock (_lossLock)
            {
                lost = _ids.Where(id => !ids.Contains(id) && !_allowedLoss.Contains(id) && !overwritten.Contains(id)).Order(StringComparer.Ordinal).ToList();
            }

            Check(lost.Count == 0, "no file lost", "files with these ids vanished (not deleted, not overwritten on request): " + string.Join(", ", lost));
        }

        _ids = ids;

        CheckPanel(_vm.Left);
        CheckPanel(_vm.Right);
        Check(_vm.Left.IsActive != _vm.Right.IsActive && _vm.ActivePanel.IsActive, "one active panel", $"L.IsActive={_vm.Left.IsActive} R.IsActive={_vm.Right.IsActive}");

        Check(_vm.CanUndo == (_vm.UndoTitle is not null), "undo state", $"CanUndo={_vm.CanUndo} UndoTitle={_vm.UndoTitle}");

        if (fullRefresh)
        {
            var before = new[] { Remember(_vm.Left), Remember(_vm.Right) };
            await Task.WhenAll(_vm.Left.RefreshAsync(), _vm.Right.RefreshAsync());
            await SettleAsync();
            CheckPanel(_vm.Left);
            CheckPanel(_vm.Right);
            CheckKept(_vm.Left, before[0]);
            CheckKept(_vm.Right, before[1]);
            CheckAgainstDisk(_vm.Left);
            CheckAgainstDisk(_vm.Right);
        }
    }

    private void CheckPanel(PanelViewModel panel)
    {
        var s = Side(panel);
        var items = panel.Items;

        Check(panel.Tabs.Count >= 1 && panel.ActiveTabIndex >= 0 && panel.ActiveTabIndex < panel.Tabs.Count, "tabs", $"{s}: {panel.Tabs.Count} tabs, active {panel.ActiveTabIndex}");
        Check(panel.Tabs.Count(static t => t.IsActive) == 1 && panel.Tabs[panel.ActiveTabIndex].IsActive, "tabs", $"{s}: active flags [{string.Join(", ", panel.Tabs.Select(static t => t.IsActive))}], index {panel.ActiveTabIndex}");
        Check(string.Equals(panel.Tabs[panel.ActiveTabIndex].Path, panel.Path, StringComparison.Ordinal), "tab follows the panel", $"{s}: tab says {_world.Rel(panel.Tabs[panel.ActiveTabIndex].Path)}, panel shows {_world.Rel(panel.Path)}");

        Check(Contained(panel) || IsDriveRoot(panel.Path), "panel stays in the temp tree", $"{s}: the panel went to {panel.Path}");

        Check(items.Count == 0 ? panel.CurrentIndex == -1 : panel.CurrentIndex >= 0 && panel.CurrentIndex < items.Count, "cursor in the list", $"{s}: cursor {panel.CurrentIndex} with {items.Count} rows");

        var parents = items.Select((item, index) => (item, index)).Where(static x => x.item.Entry.IsParentLink).ToList();
        Check(parents.Count <= 1 && (parents.Count == 0 || parents[0].index == 0), "'..' first and once", $"{s}: '..' at [{string.Join(", ", parents.Select(static x => x.index))}]");
        Check(!items.Any(static i => i.Entry.IsParentLink && i.IsMarked), "'..' never marked", $"{s}: '..' is marked");

        var duplicates = items.Where(static i => !i.Entry.IsParentLink).GroupBy(static i => i.Name, MonkeyWorld.NameComparer).Where(static g => g.Count() > 1).Select(static g => g.Key).ToList();
        Check(duplicates.Count == 0, "no duplicate rows", $"{s}: listed twice: {string.Join(", ", duplicates)}");

        if (!panel.IsShowingResults)
        {
            var strays = items.Where(i => !i.Entry.IsParentLink && !string.Equals(Path.GetDirectoryName(i.Entry.FullPath), Path.TrimEndingDirectorySeparator(panel.Path), MonkeyWorld.PathComparison)).Select(static i => i.Entry.FullPath).ToList();
            Check(strays.Count == 0, "rows belong to the folder", $"{s}: rows not in {_world.Rel(panel.Path)}: {string.Join(", ", strays.Select(_world.Rel))}");
        }

        if (!panel.IsShowingResults && !IsDriveRoot(panel.Path))
        {
            var comparer = new ItemComparer(panel.Sort, panel.Detail);
            var unsorted = Enumerable.Range(1, Math.Max(0, items.Count - 1)).Where(i => comparer.Compare(items[i - 1], items[i]) > 0).Select(i => $"{items[i - 1].Name} > {items[i].Name}").ToList();
            Check(unsorted.Count == 0, "rows in sort order", $"{s}: sorted by {panel.Sort.Column} {panel.Sort.Direction}, but {string.Join("; ", unsorted)}");
        }

        var history = panel.History.GroupBy(static h => h, StringComparer.OrdinalIgnoreCase).Where(static g => g.Count() > 1).Select(static g => g.Key).ToList();
        Check(history.Count == 0 && panel.History.Count <= PanelViewModel.HistoryLimit, "history", $"{s}: {panel.History.Count} entries, twice: {string.Join(", ", history)}");

        var marked = items.Any(static i => i.IsMarked && !i.Entry.IsParentLink);
        Check(panel.HasMarks == marked, "HasMarks matches the marks", $"{s}: HasMarks={panel.HasMarks}, marked rows: {marked}");

        var status = panel.Status;
        if (LooksLikeSummary(status))
        {
            var expected = ExpectedStatus(panel);
            Check(status == expected, "status line matches the list", $"{s}: status '{status}', the list says '{expected}'");
        }
    }

    private sealed record Shown(string Path, bool Results, HashSet<string> Marked, string? Cursor);

    private static Shown Remember(PanelViewModel panel) =>
        new(panel.Path, panel.IsShowingResults, panel.Items.Where(static i => i.IsMarked).Select(static i => i.Name).ToHashSet(StringComparer.Ordinal), panel.CurrentItem?.Name);

    /// <summary>A refresh of the same folder keeps the marks and the cursor on rows that are still there, and marks nothing new.</summary>
    private void CheckKept(PanelViewModel panel, Shown before)
    {
        if (!string.Equals(panel.Path, before.Path, StringComparison.Ordinal) || panel.IsShowingResults != before.Results)
        {
            return; // the folder went away: the panel moved up
        }

        var s = Side(panel);
        var names = panel.Items.Select(static i => i.Name).ToHashSet(StringComparer.Ordinal);
        var lost = before.Marked.Where(names.Contains).Where(n => !panel.Items.First(i => i.Name == n).IsMarked).ToList();
        Check(lost.Count == 0, "refresh keeps marks", $"{s}: marks lost by a refresh: {string.Join(", ", lost)}");
        var gained = panel.Items.Where(i => i.IsMarked && !before.Marked.Contains(i.Name)).Select(static i => i.Name).ToList();
        Check(gained.Count == 0, "refresh keeps marks", $"{s}: marked by a refresh: {string.Join(", ", gained)}");
        if (before.Cursor is { } cursor && names.Contains(cursor))
        {
            Check(panel.CurrentItem?.Name == cursor, "refresh keeps the cursor", $"{s}: cursor was on '{cursor}', after a refresh on '{panel.CurrentItem?.Name}'");
        }
    }

    private static bool IsDriveRoot(string path) =>
        string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSummary(string status) =>
        status.Length > 0 && (char.IsDigit(status[0]) || status.StartsWith("Selected ", StringComparison.Ordinal) || status.StartsWith("Zaznaczono ", StringComparison.Ordinal));

    private static string ExpectedStatus(PanelViewModel panel)
    {
        int files = 0, dirs = 0, markedFiles = 0, markedDirs = 0;
        long bytes = 0, markedBytes = 0;
        foreach (var item in panel.Items.Where(static i => !i.Entry.IsParentLink))
        {
            if (item.IsDirectory)
            {
                dirs++;
            }
            else
            {
                files++;
            }

            bytes += item.SelectionBytes;
            if (item.IsMarked)
            {
                markedBytes += item.SelectionBytes;
                if (item.IsDirectory)
                {
                    markedDirs++;
                }
                else
                {
                    markedFiles++;
                }
            }
        }

        return (markedFiles + markedDirs > 0 ? Texts.Selection(markedFiles, files, markedBytes, bytes, markedDirs, dirs) : Texts.Summary(files, bytes, dirs))
            + (panel.Filter is { } filter ? Texts.FilterSuffix(Texts.Describe(filter)) : string.Empty);
    }

    /// <summary>Right after a refresh: the folder exists and the rows are exactly what is on disk.</summary>
    private void CheckAgainstDisk(PanelViewModel panel)
    {
        var s = Side(panel);
        if (panel.IsInArchive)
        {
            return; // Ctrl+PgDn opened a file as an archive (an empty file of zeros is a valid tar)
        }

        if (panel.IsShowingResults)
        {
            var missing = panel.Items.Where(static i => !i.Entry.IsParentLink && !Path.Exists(i.Entry.FullPath)).Select(static i => i.Name).ToList();
            Check(missing.Count == 0, "results exist", $"{s}: results no longer on disk after a refresh: {string.Join(", ", missing)}");
            Check(panel.Items.Count > 0 && panel.Items[0].Entry.IsParentLink, "'..' in results", $"{s}: results without '..'");
            return;
        }

        Check(Directory.Exists(panel.Path), "panel folder exists after refresh", $"{s}: still shows {_world.Rel(panel.Path)}, which is gone (status '{panel.Status}')");
        if (!_world.Inside(panel.Path))
        {
            return; // the temp root or the drive root: hidden and system entries make a comparison meaningless
        }

        Check(panel.Items.Count > 0 && panel.Items[0].Entry.IsParentLink, "'..' in a subfolder", $"{s}: no '..' in {_world.Rel(panel.Path)}");
        var onDisk = Directory.EnumerateFileSystemEntries(panel.Path).Select(static e => Path.GetFileName(e)).ToHashSet(MonkeyWorld.NameComparer);
        var listed = panel.Items.Where(static i => !i.Entry.IsParentLink).Select(static i => i.Name).ToHashSet(MonkeyWorld.NameComparer);
        var extra = listed.Where(n => !onDisk.Contains(n)).Order(StringComparer.Ordinal).ToList();
        Check(extra.Count == 0, "list matches the disk", $"{s}: listed but not on disk in {_world.Rel(panel.Path)}: {string.Join(", ", extra)}");
        if (panel.Filter is null)
        {
            var absent = onDisk.Where(n => !listed.Contains(n)).Order(StringComparer.Ordinal).ToList();
            Check(absent.Count == 0, "list matches the disk", $"{s}: on disk but not listed in {_world.Rel(panel.Path)}: {string.Join(", ", absent)}");
        }

        var wrongKind = panel.Items.Where(static i => !i.Entry.IsParentLink && i.IsDirectory != Directory.Exists(i.Entry.FullPath)).Select(static i => i.Name).ToList();
        Check(wrongKind.Count == 0, "list matches the disk", $"{s}: shown as the wrong kind (file/folder): {string.Join(", ", wrongKind)}");
    }
}
