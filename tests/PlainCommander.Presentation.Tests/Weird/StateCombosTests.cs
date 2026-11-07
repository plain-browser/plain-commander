using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests.Weird;

/// <summary>Commands pressed in odd orders and odd states: nothing selected, "..", filters, same folders, undo, tabs.</summary>
public sealed class StateCombosTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeBin _bin = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public StateCombosTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(),
            new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), _bin);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task OpenAsync(string? left = null, string? right = null)
    {
        (await _vm.Left.NavigateAsync(left ?? _left)).Should().BeTrue();
        (await _vm.Right.NavigateAsync(right ?? _right)).Should().BeTrue();
    }

    private static void Cursor(PanelViewModel panel, string name)
    {
        var index = panel.Items.ToList().FindIndex(i => i.Name == name);
        index.Should().BeGreaterThanOrEqualTo(0, $"{name} should be listed");
        panel.CurrentIndex = index;
    }

    private static string[] Names(PanelViewModel panel) => panel.Items.Select(i => i.Name).ToArray();

    // ---- nothing selected / only ".." ----

    [Fact]
    public async Task Every_command_on_an_empty_folder_with_only_dot_dot_asks_nothing_and_changes_nothing()
    {
        var empty = _tree.Dir("left/empty");
        await OpenAsync(empty);
        _vm.Left.CurrentItem!.Entry.IsParentLink.Should().BeTrue();
        _dialogs.TransferAnswer = _right;
        _dialogs.NameAnswer = "x";

        await _vm.CopyAsync();
        await _vm.MoveAsync();
        await _vm.DeleteAsync(permanent: false);
        await _vm.DeleteAsync(permanent: true);
        await _vm.RenameCurrentAsync();
        await _vm.EncryptAsync();
        await _vm.PackAsync();
        (await _vm.CreateMultiRenameAsync()).Should().BeNull();

        _dialogs.LastDefaultTarget.Should().BeNull("no transfer or pack dialog for \"..\"");
        _dialogs.LastNameInitial.Should().BeNull();
        _dialogs.LastDeletePermanent.Should().BeNull();
        _dialogs.LastEncryptFolder.Should().BeNull();
        Directory.Exists(empty).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(_right).Should().BeEmpty();
        _bin.Recycled.Should().BeEmpty();
        _vm.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Dot_dot_focused_with_marks_elsewhere_acts_on_the_marks_only()
    {
        _tree.File("left/a.txt");
        _tree.File("left/b.txt");
        await OpenAsync();
        Cursor(_vm.Left, "b.txt");
        _vm.Left.ToggleMark(_vm.Left.CurrentItem!);
        Cursor(_vm.Left, "..");

        await _vm.DeleteAsync(permanent: false);

        _bin.Recycled.Should().Equal(Path.Combine(_left, "b.txt"));
        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
        Directory.Exists(_left).Should().BeTrue("\"..\" (the parent) must never be a source");
    }

    [Fact]
    public async Task Dot_dot_is_never_marked_by_any_marking_command()
    {
        _tree.File("left/a.txt");
        _tree.Dir("left/d");
        await OpenAsync();
        var up = _vm.Left.Items[0];
        up.Entry.IsParentLink.Should().BeTrue();

        _vm.Left.ToggleMark(up);
        _vm.Left.MarkAll();
        _vm.Left.InvertMarks();
        _vm.Left.InvertMarks();
        _vm.Left.MarkByMask("*", mark: true);
        _vm.Left.MarkByMask("..", mark: true);
        _vm.Left.MarkOnly(new HashSet<string> { ".." });
        Cursor(_vm.Left, "..");
        _vm.Left.MarkCurrentAndAdvance();
        Cursor(_vm.Left, "..");
        await _vm.Left.MarkCurrentAndMeasureAsync();

        up.IsMarked.Should().BeFalse();
        _vm.Left.OperationSources.Should().NotContain(i => i.Entry.IsParentLink);
    }

    [Fact]
    public async Task Cursor_beyond_the_list_is_no_source()
    {
        _tree.File("left/a.txt");
        await OpenAsync();
        _vm.Left.CurrentIndex = 99;
        _dialogs.TransferAnswer = _right;

        await _vm.CopyAsync();
        await _vm.DeleteAsync(permanent: true);
        await _vm.RenameCurrentAsync();

        _dialogs.LastDefaultTarget.Should().BeNull();
        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
    }

    // ---- marks vs filter, sort, navigation ----

    [Fact]
    public async Task Marks_hidden_by_a_filter_are_not_deleted()
    {
        _tree.File("left/a.txt");
        _tree.File("left/b.log");
        await OpenAsync();
        _vm.Left.MarkAll();
        await _vm.Left.SetFilterAsync("*.txt");
        Names(_vm.Left).Should().Equal("..", "a.txt");

        await _vm.DeleteAsync(permanent: false);

        _bin.Recycled.Should().Equal(Path.Combine(_left, "a.txt"));
        File.Exists(Path.Combine(_left, "b.log")).Should().BeTrue("a hidden item is not acted on");
    }

    [Fact]
    public async Task Inverting_marks_with_a_filter_touches_only_the_visible_files()
    {
        _tree.File("left/a.txt");
        _tree.File("left/b.txt");
        _tree.File("left/c.log");
        await OpenAsync();
        await _vm.Left.SetFilterAsync("*.txt");

        _vm.Left.InvertMarks();
        await _vm.Left.SetFilterAsync((string?)null);

        _vm.Left.Items.Single(i => i.Name == "c.log").IsMarked.Should().BeFalse();
        _vm.Left.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["a.txt", "b.txt"], "marks survive the filter being removed");
    }

    [Fact]
    public async Task Sort_change_keeps_the_marks_and_the_cursor_on_the_same_item()
    {
        _tree.File("left/a.txt", 30);
        _tree.File("left/b.txt", 10);
        _tree.File("left/c.txt", 20);
        await OpenAsync();
        Cursor(_vm.Left, "c.txt");
        _vm.Left.ToggleMark(_vm.Left.Items.Single(i => i.Name == "a.txt"));

        await _vm.Left.SortByAsync(SortColumn.Size);
        await _vm.Left.SortByAsync(SortColumn.Size);

        _vm.Left.CurrentItem!.Name.Should().Be("c.txt");
        _vm.Left.MarkedItems.Select(i => i.Name).Should().Equal("a.txt");
    }

    [Fact]
    public async Task Marks_are_dropped_when_leaving_the_folder_so_nothing_hidden_is_deleted_later()
    {
        _tree.File("left/a.txt");
        _tree.File("left/sub/x.txt");
        await OpenAsync();
        _vm.Left.MarkAll();
        Cursor(_vm.Left, "sub");
        await _vm.Left.OpenCurrentAsync();
        await _vm.Left.GoUpAsync();

        _vm.Left.MarkedItems.Should().BeEmpty();
        _vm.Left.CurrentItem!.Name.Should().Be("sub");
    }

    [Fact]
    public async Task Quick_search_then_filter_then_mark_by_mask_marks_only_visible_files()
    {
        _tree.File("left/alpha.txt");
        _tree.File("left/alpine.log");
        _tree.File("left/beta.txt");
        await OpenAsync();
        _vm.Left.QuickSearchType('a').Should().BeTrue();
        _vm.Left.QuickSearchType('l').Should().BeTrue();
        _vm.Left.QuickSearchType('z').Should().BeFalse("a key that matches nothing is ignored");

        (await _vm.Left.FilterByQuickSearchAsync()).Should().BeTrue();
        _vm.Left.IsQuickSearchActive.Should().BeFalse();
        Names(_vm.Left).Should().BeEquivalentTo(["..", "alpha.txt", "alpine.log"]);
        _vm.Left.MarkByMask("*.txt", mark: true);
        await _vm.Left.SortByAsync(SortColumn.Extension);

        _vm.Left.MarkedItems.Select(i => i.Name).Should().Equal("alpha.txt");
        (await _vm.Left.FilterByQuickSearchAsync()).Should().BeFalse("no quick search is going on");
    }

    [Fact]
    public async Task Quick_search_on_a_list_with_only_dot_dot_matches_nothing()
    {
        var empty = _tree.Dir("left/empty");
        await OpenAsync(empty);

        _vm.Left.QuickSearchType('.').Should().BeFalse("\"..\" is not searchable");
        _vm.Left.QuickSearchNext(forward: true);
        _vm.Left.QuickSearchBackspace();

        _vm.Left.IsQuickSearchActive.Should().BeFalse();
        _vm.Left.CurrentIndex.Should().Be(0);
    }

    // ---- same folder in both panels, into itself ----

    [Fact]
    public async Task F5_into_the_same_folder_keeps_the_file_intact()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "keep me");
        await OpenAsync(_left, _left);
        Cursor(_vm.Left, "a.txt");
        _dialogs.TransferAnswer = _left + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();

        File.ReadAllText(Path.Combine(_left, "a.txt")).Should().Be("keep me");
    }

    [Fact]
    public async Task F6_of_several_items_into_the_same_folder_loses_nothing()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_left, "b.txt"), "B");
        _tree.File("left/d/inner.txt", 3);
        await OpenAsync(_left, _left);
        _vm.Left.MarkAll();
        _dialogs.TransferAnswer = _left + Path.DirectorySeparatorChar;

        await _vm.MoveAsync();

        File.ReadAllText(Path.Combine(_left, "a.txt")).Should().Be("A");
        File.ReadAllText(Path.Combine(_left, "b.txt")).Should().Be("B");
        File.Exists(Path.Combine(_left, "d", "inner.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task F6_of_a_folder_into_its_own_child_is_refused_and_loses_nothing()
    {
        _tree.File("left/d/sub/inner.txt", 3);
        await OpenAsync();
        Cursor(_vm.Left, "d");
        _dialogs.TransferAnswer = Path.Combine(_left, "d", "sub") + Path.DirectorySeparatorChar;

        await _vm.MoveAsync();

        File.Exists(Path.Combine(_left, "d", "sub", "inner.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(_left, "d", "sub", "d")).Should().BeFalse();
    }

    [Fact]
    public async Task F5_of_a_folder_into_its_own_child_does_not_recurse_forever()
    {
        _tree.File("left/d/sub/inner.txt", 3);
        await OpenAsync();
        Cursor(_vm.Left, "d");
        _dialogs.TransferAnswer = Path.Combine(_left, "d", "sub") + Path.DirectorySeparatorChar;

        await _vm.CopyAsync().WaitAsync(TimeSpan.FromSeconds(30));

        File.Exists(Path.Combine(_left, "d", "sub", "inner.txt")).Should().BeTrue();
        Directory.Exists(Path.Combine(_left, "d", "sub", "d", "sub", "d")).Should().BeFalse();
    }

    [Fact]
    public async Task Dropping_a_folder_onto_itself_or_its_child_loses_nothing()
    {
        _tree.File("left/d/sub/inner.txt", 3);
        await OpenAsync();
        var d = Path.Combine(_left, "d");
        _dialogs.TransferAnswer = d + Path.DirectorySeparatorChar;

        await _vm.DropAsync([d], d, move: true, _vm.Left);
        _dialogs.TransferAnswer = Path.Combine(d, "sub") + Path.DirectorySeparatorChar;
        await _vm.DropAsync([d], Path.Combine(d, "sub"), move: true, _vm.Left);
        await _vm.DropAsync([d], Path.Combine(d, "sub"), move: true, from: null);

        File.Exists(Path.Combine(d, "sub", "inner.txt")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(d).Should().ContainSingle();
    }

    [Fact]
    public async Task Dropping_dot_dot_from_a_panel_does_nothing()
    {
        _tree.File("left/a.txt");
        await OpenAsync();
        _dialogs.TransferAnswer = _right;

        await _vm.DropAsync([_vm.Left.Items[0].Entry.FullPath], _right, move: true, _vm.Left);

        _dialogs.LastDefaultTarget.Should().BeNull();
        Directory.Exists(_left).Should().BeTrue();
    }

    // ---- undo ----

    [Fact]
    public async Task Undo_twice_walks_back_two_renames_in_order_even_after_changing_folder()
    {
        _tree.File("left/a.txt");
        await OpenAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = "b.txt";
        await _vm.RenameCurrentAsync();
        _dialogs.NameAnswer = "c.txt";
        await _vm.RenameCurrentAsync();
        await _vm.Left.NavigateAsync(_right);

        await _vm.UndoAsync();
        await _vm.UndoAsync();
        await _vm.UndoAsync();

        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(_left).Should().ContainSingle();
        _vm.CanUndo.Should().BeFalse();
        _vm.Left.Status.Should().Be(Texts.NothingToUndo);
    }

    [Fact]
    public async Task Undo_of_a_rename_does_not_overwrite_a_new_file_with_the_old_name()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "original");
        await OpenAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = "b.txt";
        await _vm.RenameCurrentAsync();
        File.WriteAllText(Path.Combine(_left, "a.txt"), "newer");

        await _vm.UndoAsync();

        File.ReadAllText(Path.Combine(_left, "a.txt")).Should().Be("newer");
        File.ReadAllText(Path.Combine(_left, "b.txt")).Should().Be("original");
    }

    [Fact]
    public async Task Declined_undo_keeps_the_step()
    {
        await OpenAsync();
        _dialogs.NameAnswer = "made";
        await _vm.MakeDirectoryAsync();
        _dialogs.UndoAnswer = false;

        await _vm.UndoAsync();

        Directory.Exists(Path.Combine(_left, "made")).Should().BeTrue();
        _vm.CanUndo.Should().BeTrue();
    }

    // ---- rename ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a.txt")]
    public async Task Rename_to_nothing_or_the_same_name_does_nothing(string? answer)
    {
        _tree.File("left/a.txt");
        await OpenAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = answer;

        await _vm.RenameCurrentAsync();

        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
        _dialogs.Errors.Should().BeEmpty();
        _vm.CanUndo.Should().BeFalse();
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("\"\"")]
    [InlineData("x/y")]
    [InlineData("a\0b")]
    public async Task Rename_to_an_impossible_name_reports_and_keeps_the_file(string answer)
    {
        _tree.File("left/a.txt");
        await OpenAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = answer;

        await _vm.RenameCurrentAsync();

        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
        _dialogs.Errors.Should().ContainSingle();
        _vm.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Rename_of_a_folder_to_the_name_of_an_existing_file_is_refused()
    {
        _tree.File("left/d/x.txt");
        File.WriteAllText(Path.Combine(_left, "f"), "file");
        await OpenAsync();
        Cursor(_vm.Left, "d");
        _dialogs.NameAnswer = "f";

        await _vm.RenameCurrentAsync();

        _dialogs.Errors.Should().ContainSingle();
        File.ReadAllText(Path.Combine(_left, "f")).Should().Be("file");
        File.Exists(Path.Combine(_left, "d", "x.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Regression_rename_to_the_same_name_with_spaces_records_a_phantom_undo_step()
    {
        _tree.File("left/a.txt");
        await OpenAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = " a.txt ";

        await _vm.RenameCurrentAsync();

        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
        _dialogs.Errors.Should().BeEmpty();
        _vm.CanUndo.Should().BeFalse("nothing was renamed, so there is nothing to undo");
    }

    // ---- new folder ----

    [Fact]
    public async Task F7_with_the_name_of_an_existing_folder_keeps_its_content_and_records_no_undo()
    {
        _tree.File("left/d/x.txt");
        await OpenAsync();
        _dialogs.NameAnswer = "d";

        await _vm.MakeDirectoryAsync();

        File.Exists(Path.Combine(_left, "d", "x.txt")).Should().BeTrue();
        _vm.CanUndo.Should().BeFalse();
        _vm.Left.CurrentItem!.Name.Should().Be("d");
    }

    [Fact]
    public async Task F7_with_the_name_of_an_existing_file_is_an_error()
    {
        File.WriteAllText(Path.Combine(_left, "f"), "file");
        await OpenAsync();
        _dialogs.NameAnswer = "f";

        await _vm.MakeDirectoryAsync();

        _dialogs.Errors.Should().ContainSingle();
        File.ReadAllText(Path.Combine(_left, "f")).Should().Be("file");
        _vm.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task F7_nested_below_an_existing_file_is_an_error_not_a_crash()
    {
        File.WriteAllText(Path.Combine(_left, "f"), "file");
        await OpenAsync();
        _dialogs.NameAnswer = "f/sub";

        await _vm.MakeDirectoryAsync();

        _dialogs.Errors.Should().ContainSingle();
        File.ReadAllText(Path.Combine(_left, "f")).Should().Be("file");
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("\"\"")]
    public async Task F7_with_an_impossible_name_reports_it(string name)
    {
        await OpenAsync();
        _dialogs.NameAnswer = name;

        await _vm.MakeDirectoryAsync();

        _dialogs.Errors.Should().ContainSingle();
        Directory.EnumerateFileSystemEntries(_left).Should().BeEmpty();
    }

    [Fact]
    public async Task Regression_F7_with_a_dot_dot_segment_records_undo_for_a_folder_never_created()
    {
        await OpenAsync();
        _dialogs.NameAnswer = "x/../y";

        await _vm.MakeDirectoryAsync();
        Directory.Exists(Path.Combine(_left, "y")).Should().BeTrue();
        await _vm.UndoAsync();

        Directory.Exists(Path.Combine(_left, "y")).Should().BeFalse("Ctrl+Z should take away the folder F7 created");
    }

    // ---- tabs ----

    [Fact]
    public async Task Closing_the_last_tab_does_nothing()
    {
        await OpenAsync();

        await _vm.CloseTabAsync();
        await _vm.Left.CloseTabAsync(-1);
        await _vm.Left.CloseTabAsync(5);
        await _vm.NextTabAsync(-7);

        _vm.Left.Tabs.Should().ContainSingle();
        _vm.Left.Path.Should().Be(_left);
    }

    [Fact]
    public async Task A_tab_whose_folder_was_deleted_opens_the_nearest_parent()
    {
        var deep = _tree.Dir("left/a/b");
        await OpenAsync();
        _vm.Left.NewTab();
        await _vm.Left.NavigateAsync(deep);
        await _vm.Left.SwitchTabAsync(0);
        Directory.Delete(Path.Combine(_left, "a"), recursive: true);

        await _vm.Left.SwitchTabAsync(1);

        _vm.Left.Path.Should().Be(_left);
        _vm.Left.Tabs[1].Path.Should().Be(_left);
    }

    [Fact]
    public async Task A_tab_duplicated_inside_an_archive_comes_back_inside_it()
    {
        var zip = Path.Combine(_left, "p.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("docs/readme.txt");
        }

        try
        {
            await OpenAsync(ArchivePath.Join(zip, "docs"));
            _vm.Left.NewTab();
            await _vm.Left.NavigateAsync(_right);

            await _vm.Left.SwitchTabAsync(0);

            _vm.Left.IsInArchive.Should().BeTrue();
            Names(_vm.Left).Should().Equal("..", "readme.txt");
            await _vm.Left.CloseTabAsync(0);
            _vm.Left.Path.Should().Be(_right);
        }
        finally
        {
            ArchiveCatalog.Forget(zip);
        }
    }

    [Fact]
    public async Task New_tab_from_search_results_reopens_the_searched_folder()
    {
        _tree.File("left/sub/x.txt");
        await OpenAsync();
        _vm.ShowSearchResults("*.txt", _left, [LocalFileSystem.ToEntry(new FileInfo(Path.Combine(_left, "sub", "x.txt")))]);
        _vm.NewTab();

        await _vm.Left.SwitchTabAsync(0);
        await _vm.Left.SwitchTabAsync(1);

        _vm.Left.IsShowingResults.Should().BeFalse();
        _vm.Left.Path.Should().Be(_left);
    }

    // ---- folders that go away / cannot be read / have no parent ----

    [Fact]
    public async Task The_root_has_no_parent()
    {
        var root = Path.GetPathRoot(_left)!;
        await _vm.Left.NavigateAsync(root);

        (await _vm.Left.GoUpAsync()).Should().BeFalse();
        (await _vm.Left.GoRootAsync()).Should().BeFalse();

        _vm.Left.Path.Should().Be(root);
    }

    [Fact]
    public async Task A_folder_without_read_permission_is_reported_and_the_panel_stays()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var locked = _tree.Dir("left/locked");
        await OpenAsync();
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Cursor(_vm.Left, "locked");
            await _vm.Left.OpenCurrentAsync();

            _vm.Left.Path.Should().Be(_left);
            _vm.Left.Status.Should().NotBeEmpty();
            _vm.Left.IsLoading.Should().BeFalse();
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_folder_deleted_while_viewed_still_has_a_working_way_up()
    {
        var gone = _tree.Dir("left/gone");
        _tree.File("left/gone/x.txt");
        await OpenAsync(gone);
        Directory.Delete(gone, recursive: true);

        // Refresh itself finds the way up now: a gone folder is never left on screen (F7 there would recreate it).
        (await _vm.Left.RefreshAsync()).Should().BeTrue();

        _vm.Left.Path.Should().Be(_left);
        Names(_vm.Left).Should().NotContain("gone");
        (await _vm.Left.GoUpAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Regression_panel_keeps_listing_a_folder_deleted_from_the_other_panel()
    {
        _tree.File("left/victim/x.txt");
        var victim = Path.Combine(_left, "victim");
        await OpenAsync(_left, victim);
        Cursor(_vm.Left, "victim");

        await _vm.DeleteAsync(permanent: true);

        Directory.Exists(victim).Should().BeFalse();
        Names(_vm.Right).Should().NotContain("x.txt", "the right panel must not keep listing a deleted folder");
    }

    // ---- command line ----

    [Fact]
    public async Task Cd_to_a_missing_folder_or_a_file_reports_and_stays()
    {
        _tree.File("left/a.txt");
        await OpenAsync();

        _vm.CommandLine.Text = "cd nowhere";
        await _vm.RunCommandLineAsync();
        _vm.CommandLine.Text = "cd a.txt";
        await _vm.RunCommandLineAsync();

        _dialogs.Errors.Should().HaveCount(2);
        _vm.Left.Path.Should().Be(_left);
    }

    [Fact]
    public async Task Cd_with_quotes_and_relative_parts_lands_in_the_folder()
    {
        _tree.Dir("left/with space/inner");
        await OpenAsync();

        _vm.CommandLine.Text = "cd \"with space\"";
        await _vm.RunCommandLineAsync();
        _vm.Left.Path.Should().Be(Path.Combine(_left, "with space"));

        _vm.CommandLine.Text = "cd inner/../../with space/./inner";
        await _vm.RunCommandLineAsync();
        _vm.Left.Path.Should().Be(Path.Combine(_left, "with space", "inner"));

        _vm.CommandLine.Text = "..";
        await _vm.RunCommandLineAsync();
        _vm.Left.Path.Should().Be(Path.Combine(_left, "with space"));
        _dialogs.Errors.Should().BeEmpty();
    }
}
