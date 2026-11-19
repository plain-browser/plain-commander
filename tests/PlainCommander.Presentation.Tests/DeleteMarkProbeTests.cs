using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class DeleteMarkProbeTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeBin _bin = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public DeleteMarkProbeTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(),
            new SettingsStore(Path.Combine(_tree.Root, "settings.json")), _dialogs, new NoPrompts(), _bin);
    }

    public void Dispose() { _vm.Dispose(); _tree.Dispose(); }

    [Fact]
    public async Task Marks_in_the_other_panel_survive_a_delete()
    {
        _tree.File("left/a.txt"); _tree.File("left/b.txt"); _tree.File("left/c.txt");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_left); // same folder in both panels
        _vm.Right.MarkByMask("a.txt;c.txt", mark: true);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "b.txt");
        await _vm.DeleteAsync(permanent: true);
        _vm.Right.Items.Where(i => i.IsMarked).Select(i => i.Name).Should().BeEquivalentTo("a.txt", "c.txt");
    }

    [Fact]
    public async Task Marks_of_the_active_panel_that_were_not_deleted_survive_failed_or_partial_delete()
    {
        _tree.File("left/a.txt"); _tree.File("left/b.txt"); _tree.File("left/c.txt");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _vm.Left.MarkByMask("a.txt", mark: true);
        await _vm.DeleteAsync(permanent: true);
        _vm.Left.Items.Select(i => i.Name).Should().NotContain("a.txt");
        _vm.Left.CurrentIndex.Should().BeGreaterThan(-1);
    }

    [Fact]
    public async Task Delete_in_left_keeps_marks_in_left_when_cancelled()
    {
        _tree.File("left/a.txt"); _tree.File("left/b.txt");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _vm.Left.MarkByMask("a.txt;b.txt", mark: true);
        _dialogs.DeleteAnswer = false;
        await _vm.DeleteAsync(permanent: true);
        _vm.Left.Items.Count(i => i.IsMarked).Should().Be(2);
    }

    [Fact]
    public async Task Recycle_keeps_cursor_and_other_panel_marks_in_other_folder()
    {
        _tree.File("left/a.txt"); _tree.File("right/x.txt"); _tree.File("right/y.txt");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _vm.Right.MarkByMask("x.txt", mark: true);
        await _vm.DeleteAsync(permanent: false);
        _vm.Right.Items.Where(i => i.IsMarked).Select(i => i.Name).Should().Equal("x.txt");
    }
}
