using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class CommandTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeBin _bin = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public CommandTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(
            new LocalFileSystem(),
            _launcher,
            new SettingsStore(Path.Combine(_tree.Root, "settings.json")),
            _dialogs,
            new NoPrompts(),
            _bin);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task Open()
    {
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
    }

    private static void Cursor(PanelViewModel panel, string name) =>
        panel.CurrentIndex = panel.Items.ToList().FindIndex(i => i.Name == name);

    [Fact]
    public async Task F5_copies_marked_files_to_the_other_panel_and_unmarks_them()
    {
        _tree.File("left/a.txt", 3);
        _tree.File("left/b.txt", 4);
        _tree.File("left/c.txt", 5);
        await Open();
        _vm.Left.MarkByMask("a.txt;b.txt", mark: true);
        _dialogs.TransferAnswer = _right + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();

        _dialogs.LastDefaultTarget.Should().Be(_right + Path.DirectorySeparatorChar);
        File.Exists(Path.Combine(_right, "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_right, "b.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_right, "c.txt")).Should().BeFalse();
        _vm.Left.MarkedItems.Should().BeEmpty();
        _vm.Right.Items.Select(i => i.Name).Should().Contain(["a.txt", "b.txt"]);
    }

    [Fact]
    public async Task F6_with_both_panels_on_one_folder_offers_a_rename()
    {
        _tree.File("left/old.txt");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_left);
        Cursor(_vm.Left, "old.txt");
        _dialogs.TransferAnswer = "new.txt";

        await _vm.MoveAsync();

        _dialogs.LastDefaultTarget.Should().Be("old.txt");
        File.Exists(Path.Combine(_left, "new.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_left, "old.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Cancelled_dialog_does_nothing()
    {
        _tree.File("left/a.txt");
        await Open();
        Cursor(_vm.Left, "a.txt");
        _dialogs.TransferAnswer = null;

        await _vm.CopyAsync();

        Directory.EnumerateFileSystemEntries(_right).Should().BeEmpty();
    }

    [Fact]
    public async Task Unsupported_target_is_reported_not_thrown()
    {
        _tree.File("left/a.txt");
        await Open();
        Cursor(_vm.Left, "a.txt");
        _dialogs.TransferAnswer = "*.bak";

        await _vm.CopyAsync();

        _dialogs.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task F7_creates_a_folder_and_puts_the_cursor_on_it()
    {
        await Open();
        _dialogs.NameAnswer = Path.Combine("projekt", "src");

        await _vm.MakeDirectoryAsync();

        Directory.Exists(Path.Combine(_left, "projekt", "src")).Should().BeTrue();
        _vm.Left.CurrentItem!.Name.Should().Be("projekt");
    }

    [Fact]
    public async Task Shift_F6_renames_in_place()
    {
        _tree.File("left/draft.txt");
        await Open();
        Cursor(_vm.Left, "draft.txt");
        _dialogs.NameAnswer = "final.txt";

        await _vm.RenameCurrentAsync();

        _dialogs.LastNameInitial.Should().Be("draft.txt");
        _vm.Left.CurrentItem!.Name.Should().Be("final.txt");
    }

    [Fact]
    public async Task Rename_onto_an_existing_name_shows_an_error()
    {
        _tree.File("left/a.txt");
        _tree.File("left/b.txt");
        await Open();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = "b.txt";

        await _vm.RenameCurrentAsync();

        _dialogs.Errors.Should().ContainSingle();
        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task F8_recycles_after_confirmation_and_keeps_the_cursor_place()
    {
        _tree.File("left/a.txt");
        _tree.File("left/b.txt");
        _tree.File("left/c.txt");
        await Open();
        Cursor(_vm.Left, "b.txt");
        var index = _vm.Left.CurrentIndex;

        await _vm.DeleteAsync(permanent: false);

        _dialogs.LastDeletePermanent.Should().BeFalse();
        _bin.Recycled.Should().Equal(Path.Combine(_left, "b.txt"));
        _vm.Left.Items.Select(i => i.Name).Should().NotContain("b.txt");
        _vm.Left.CurrentIndex.Should().Be(index);
    }

    [Fact]
    public async Task Shift_Del_deletes_permanently_only_when_confirmed()
    {
        _tree.File("left/dir/x.txt");
        await Open();
        Cursor(_vm.Left, "dir");
        _dialogs.DeleteAnswer = false;

        await _vm.DeleteAsync(permanent: true);
        Directory.Exists(Path.Combine(_left, "dir")).Should().BeTrue();

        _dialogs.DeleteAnswer = true;
        await _vm.DeleteAsync(permanent: true);
        Directory.Exists(Path.Combine(_left, "dir")).Should().BeFalse();
        _bin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Num_plus_uses_the_mask_from_the_dialog()
    {
        _tree.File("left/a.cs");
        _tree.File("left/b.txt");
        await Open();
        _dialogs.MaskAnswer = "*.cs";

        await _vm.MarkByMaskAsync(mark: true);

        _vm.Left.MarkedItems.Select(i => i.Name).Should().Equal("a.cs");
    }

    [Fact]
    public async Task F4_opens_the_file_under_the_cursor_in_the_configured_editor()
    {
        _tree.File("a.txt");
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Settings.Editor = @"C:\Tools\code.cmd";

        await _vm.EditCurrentAsync();
        _launcher.Edited.Should().BeEmpty("the cursor is on '..'");

        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == "a.txt");
        await _vm.EditCurrentAsync();
        _launcher.Edited.Should().ContainSingle().Which.Should().Be((@"C:\Tools\code.cmd", Path.Combine(_tree.Root, "a.txt")));
    }

    [Fact]
    public async Task Shift_F4_creates_the_file_puts_the_cursor_on_it_and_opens_the_editor()
    {
        await _vm.Left.NavigateAsync(_tree.Root);
        _dialogs.NameAnswer = "todo.txt";

        await _vm.NewFileAsync();

        File.Exists(Path.Combine(_tree.Root, "todo.txt")).Should().BeTrue();
        _vm.Left.CurrentItem!.Name.Should().Be("todo.txt");
        _launcher.Edited.Should().ContainSingle().Which.Path.Should().Be(Path.Combine(_tree.Root, "todo.txt"));

        await _vm.NewFileAsync();
        _dialogs.Errors.Should().ContainSingle("the file already exists").Which.Should().Contain("todo.txt").And.MatchRegex("już istnieje|already exists");
    }

    [Fact]
    public async Task Panels_remember_where_they_were_and_can_swap_or_mirror()
    {
        var sub = _tree.Dir(Path.Combine("left", "sub"));
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        await _vm.Left.NavigateAsync(sub);
        await _vm.Left.GoUpAsync();
        await _vm.Left.RefreshAsync();

        _vm.Left.History.Should().Equal([sub, _left], "a revisit moves the folder to the end, a refresh adds nothing");

        await _vm.SwapPanelsAsync();
        _vm.Left.Path.Should().Be(_right);
        _vm.Right.Path.Should().Be(_left);

        await _vm.SwapPanelsAsync();
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == "sub");
        await _vm.MirrorAsync(PanelSide.Right);
        _vm.Right.Path.Should().Be(sub, "the folder under the cursor opens in the other panel");

        _vm.Left.CurrentIndex = 0;
        await _vm.MirrorAsync(PanelSide.Left);
        _vm.Right.Path.Should().Be(_left, "pointing at the active panel means the other one; '..' is not a target");
    }

    [Fact]
    public async Task Favorites_are_added_once_removed_and_saved()
    {
        await _vm.Left.NavigateAsync(_left);

        _vm.AddFavorite(_left);
        _vm.AddFavorite(_left.ToUpperInvariant());
        _vm.AddFavorite(_right);
        _vm.Favorites.Should().Equal(_left, _right);
        _vm.IsFavorite(_left.ToUpperInvariant()).Should().BeTrue();

        _vm.RemoveFavorite(_right);
        _vm.Save(null);

        using var second = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "settings.json")), new FakeDialogs(), new NoPrompts(), new FakeBin());
        second.Favorites.Should().Equal(_left);
    }
}

public sealed class OperationQueueTests
{
    private sealed class GateOperation(TaskCompletionSource release, List<string> log, string name) : IOperation
    {
        public OperationKind Kind => OperationKind.Copy;

        public async Task<OperationResult> RunAsync(OperationContext context)
        {
            log.Add("start " + name);
            await release.Task;
            log.Add("end " + name);
            return new OperationResult(OperationOutcome.Completed, 1, 0, []);
        }
    }

    [Fact]
    public async Task Runs_operations_one_at_a_time_in_order()
    {
        var queue = new OperationQueueViewModel(new NoPrompts());
        var log = new List<string>();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();

        var a = queue.Enqueue(new GateOperation(first, log, "a"), "A");
        var b = queue.Enqueue(new GateOperation(second, log, "b"), "B");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        log.Should().Equal("start a");
        queue.PendingCount.Should().Be(1);
        queue.IsDialogVisible.Should().BeTrue();

        second.SetResult();
        first.SetResult();
        await Task.WhenAll(a, b);

        log.Should().Equal("start a", "end a", "start b", "end b");
        queue.Current.Should().BeNull();
        queue.IsDialogVisible.Should().BeFalse();
    }

    [Fact]
    public async Task Background_hides_the_dialog_and_shows_the_indicator()
    {
        var queue = new OperationQueueViewModel(new NoPrompts());
        var release = new TaskCompletionSource();
        var running = queue.Enqueue(new GateOperation(release, [], "a"), "A");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        queue.Current!.SendToBackground();

        queue.IsDialogVisible.Should().BeFalse();
        queue.IsIndicatorVisible.Should().BeTrue();
        release.SetResult();
        await running;
    }

}
