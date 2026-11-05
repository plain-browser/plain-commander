using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>Ctrl+Z after F5, F7, Shift+F6: the step is described, confirmed and reversed.</summary>
public sealed class UndoCommandTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeBin _bin = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public UndoCommandTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s.json")), _dialogs, new NoPrompts(), _bin);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task OpenAsync()
    {
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
    }

    [Fact]
    public async Task Nothing_to_undo_says_so()
    {
        await OpenAsync();

        await _vm.UndoAsync();

        _vm.CanUndo.Should().BeFalse();
        _vm.Left.Status.Should().Be(Texts.NothingToUndo);
    }

    [Fact]
    public async Task Ctrl_Z_after_a_copy_takes_the_copies_away()
    {
        _tree.File("left/a.txt", 3);
        await OpenAsync();
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "a.txt");
        _dialogs.TransferAnswer = _right;
        await _vm.CopyAsync();
        File.Exists(Path.Combine(_right, "a.txt")).Should().BeTrue();
        _vm.CanUndo.Should().BeTrue();

        await _vm.UndoAsync();

        _dialogs.UndoDetails.Should().ContainSingle().Which.Should().Contain("1");
        File.Exists(Path.Combine(_right, "a.txt")).Should().BeFalse();
        _bin.Recycled.Should().ContainSingle();
        File.Exists(Path.Combine(_left, "a.txt")).Should().BeTrue();
        _vm.CanUndo.Should().BeFalse("the step is used up");
    }

    [Fact]
    public async Task Ctrl_Z_after_F7_removes_the_empty_folder_and_after_Shift_F6_renames_back()
    {
        _tree.File("left/old.txt", 3);
        await OpenAsync();
        _dialogs.NameAnswer = "nowy";
        await _vm.MakeDirectoryAsync();
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "old.txt");
        _dialogs.NameAnswer = "new.txt";
        await _vm.RenameCurrentAsync();
        _vm.UndoTitle.Should().Contain("new.txt");

        await _vm.UndoAsync();
        File.Exists(Path.Combine(_left, "old.txt")).Should().BeTrue("the latest step goes first");
        Directory.Exists(Path.Combine(_left, "nowy")).Should().BeTrue();

        await _vm.UndoAsync();
        Directory.Exists(Path.Combine(_left, "nowy")).Should().BeFalse();
        _bin.Recycled.Should().BeEmpty("an empty folder is removed, not recycled");
    }

    [Fact]
    public async Task Declining_keeps_the_step()
    {
        _tree.File("left/a.txt", 3);
        await OpenAsync();
        _dialogs.NameAnswer = "x";
        await _vm.MakeDirectoryAsync();
        _dialogs.UndoAnswer = false;

        await _vm.UndoAsync();

        Directory.Exists(Path.Combine(_left, "x")).Should().BeTrue();
        _vm.CanUndo.Should().BeTrue();
    }
}
