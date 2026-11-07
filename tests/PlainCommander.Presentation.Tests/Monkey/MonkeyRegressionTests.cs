using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests.Monkey;

/// <summary>Bugs the random command sequences found, each reduced to the few steps that matter.</summary>
public sealed class MonkeyRegressionTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeBin _bin = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;
    private readonly List<OperationResult> _results = [];

    public MonkeyRegressionTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), _bin);
        _vm.Operations.Finished += (_, e) => _results.Add(e.Result);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task OpenAsync()
    {
        (await _vm.Left.NavigateAsync(_left)).Should().BeTrue();
        (await _vm.Right.NavigateAsync(_right)).Should().BeTrue();
    }

    [Fact]
    public async Task Regression_undo_of_a_new_folder_that_became_a_file_does_not_crash()
    {
        await OpenAsync();
        _dialogs.NameAnswer = "made";
        await _vm.MakeDirectoryAsync();
        var made = Path.Combine(_left, "made");
        Directory.Delete(made);
        File.WriteAllText(made, "another program put a file here");

        await _vm.UndoAsync();

        _results.Should().ContainSingle().Which.Outcome.Should().Be(OperationOutcome.Completed);
        _bin.Recycled.Should().Equal(made); // not ours any more, but nothing is deleted for good
    }

    [Fact]
    public async Task Regression_ctrl_b_in_a_folder_deleted_meanwhile_reports_instead_of_throwing()
    {
        var gone = _tree.Dir("left/gone");
        _tree.File("left/gone/a.txt", 1);
        (await _vm.Left.NavigateAsync(gone)).Should().BeTrue();
        Directory.Delete(gone, recursive: true);

        await _vm.ToggleFlatViewAsync();

        _vm.Left.IsFlat.Should().BeFalse();
        _vm.Left.IsLoading.Should().BeFalse();
        _vm.Left.Status.Should().Contain(gone);
    }

    [Fact]
    public async Task Regression_refresh_keeps_the_cursor_on_its_row_when_another_differs_only_in_case()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a case-sensitive folder");
        _tree.File("left/notes.txt", 1);
        _tree.File("left/NOTES.TXT", 2);
        await OpenAsync();
        foreach (var name in new[] { "notes.txt", "NOTES.TXT" })
        {
            _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == name);

            await _vm.Left.RefreshAsync();

            _vm.Left.CurrentItem!.Name.Should().Be(name);
        }
    }

    [Fact]
    public async Task Regression_rename_in_the_flat_view_renames_only_that_row_when_another_differs_in_case()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs a case-sensitive folder");
        _tree.File("left/sub/a.txt", 1);
        _tree.File("left/sub/A.txt", 2);
        await OpenAsync();
        await _vm.ToggleFlatViewAsync();
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == Path.Combine("sub", "a.txt"));
        _dialogs.NameAnswer = "b.txt";

        await _vm.RenameCurrentAsync();

        _vm.Left.Items.Select(static i => i.Name).Should().BeEquivalentTo("..", Path.Combine("sub", "A.txt"), Path.Combine("sub", "b.txt"));
    }
}
