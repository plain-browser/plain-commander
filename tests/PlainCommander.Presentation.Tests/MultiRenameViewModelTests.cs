using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class MultiRenameViewModelTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly PanelViewModel _panel = new(new LocalFileSystem(), new RecordingLauncher());

    public void Dispose()
    {
        _panel.Dispose();
        _tree.Dispose();
    }

    private async Task<MultiRenameViewModel> RenameAll(params string[] files)
    {
        foreach (var f in files)
        {
            _tree.File(f);
        }

        await _panel.NavigateAsync(_tree.Root);
        _panel.MarkAll();
        return new MultiRenameViewModel(_panel.OperationSources);
    }

    [Fact]
    public async Task Preview_follows_every_change_and_blocks_problems()
    {
        var vm = await RenameAll("a.txt", "b.txt");
        vm.CanApply.Should().BeFalse("nothing changes yet");

        vm.NameMask = "doc_[C]";
        vm.Rows.Select(r => r.NewName).Should().Equal("doc_1.txt", "doc_2.txt");
        vm.CanApply.Should().BeTrue();

        vm.NameMask = "same";
        vm.Rows.Should().OnlyContain(r => r.HasProblem);
        vm.CanApply.Should().BeFalse();

        vm.CounterDigits = 3;
        vm.CounterStart = 9;
        vm.NameMask = "doc_[C]";
        vm.Rows.Select(r => r.NewName).Should().Equal("doc_009.txt", "doc_010.txt");
    }

    [Fact]
    public async Task Apply_renames_resets_the_masks_and_undo_restores()
    {
        var vm = await RenameAll("one.txt", "two.txt");
        vm.CaseIndex = 2; // upper

        var outcome = await vm.ApplyAsync();

        outcome!.Done.Should().HaveCount(2);
        Directory.GetFiles(_tree.Root).Select(Path.GetFileName).Order().Should().Equal("ONE.TXT", "TWO.TXT");
        vm.NameMask.Should().Be("[N]");
        vm.CanApply.Should().BeFalse("the reset masks change nothing");
        vm.CanUndo.Should().BeTrue();

        await vm.UndoAsync();

        Directory.GetFiles(_tree.Root).Select(Path.GetFileName).Order().Should().Equal("one.txt", "two.txt");
        vm.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Search_and_replace_with_regex()
    {
        var vm = await RenameAll("IMG_2026_03.jpg");
        vm.Search = @"IMG_(\d+)_(\d+)";
        vm.Replace = "$2-$1";
        vm.UseRegex = true;

        vm.Rows.Single().NewName.Should().Be("03-2026.jpg");
    }
}
