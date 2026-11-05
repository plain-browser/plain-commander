using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>Alt+F7 "to panel": a panel lists search results like a folder.</summary>
public sealed class SearchResultsPanelTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public SearchResultsPanelTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(),
            new SettingsStore(Path.Combine(_tree.Root, "settings.json")), _dialogs, new NoPrompts(), new FakeBin());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task<SearchViewModel> SearchLeft(string masks, bool inArchives = false)
    {
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        var search = new SearchViewModel(_left) { Masks = masks, InArchives = inArchives };
        await search.StartAsync();
        _vm.ShowSearchResults(masks, _left, search.Results.Select(r => r.Entry));
        return search;
    }

    private string[] LeftNames => _vm.Left.Items.Select(i => i.Name).ToArray();

    [Fact]
    public async Task Results_show_relative_paths_and_dot_dot_returns_to_the_folder()
    {
        _tree.File("left/a.txt");
        _tree.File("left/sub/b.txt");
        _tree.File("left/sub/c.md");

        using var _ = await SearchLeft("*.txt");

        _vm.Left.IsShowingResults.Should().BeTrue();
        _vm.Left.ResultsTitle.Should().Contain("(2)");
        LeftNames.Should().Equal("..", "a.txt", Path.Combine("sub", "b.txt"));

        await _vm.Left.GoUpAsync();

        _vm.Left.IsShowingResults.Should().BeFalse();
        _vm.Left.Path.Should().Be(_left);
        LeftNames.Should().Contain("sub");
    }

    [Fact]
    public async Task F5_from_results_copies_files_from_different_folders()
    {
        _tree.File("left/one/x.txt", 3);
        _tree.File("left/two/y.txt", 4);
        using var _ = await SearchLeft("*.txt");
        _vm.Left.MarkAll();
        _dialogs.TransferAnswer = _right + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();

        File.Exists(Path.Combine(_right, "x.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_right, "y.txt")).Should().BeTrue();
        _vm.Left.IsShowingResults.Should().BeTrue("refreshing after the copy keeps the results");
    }

    [Fact]
    public async Task Refresh_drops_items_that_are_gone_and_rename_keeps_them()
    {
        var doomed = _tree.File("left/a/doomed.txt");
        _tree.File("left/b/keep.txt");
        using var _ = await SearchLeft("*.txt");

        File.Delete(doomed);
        await _vm.Left.RefreshAsync();
        LeftNames.Should().Equal("..", Path.Combine("b", "keep.txt"));
        _vm.Left.ResultsTitle.Should().Contain("(1)");

        _vm.Left.CurrentIndex = 1;
        _dialogs.NameAnswer = "renamed.txt";
        await _vm.RenameCurrentAsync();

        _dialogs.LastNameInitial.Should().Be("keep.txt", "the dialog gets the file name, not the relative path");
        LeftNames.Should().Equal("..", Path.Combine("b", "renamed.txt"));
        _vm.Left.CurrentItem!.Name.Should().Be(Path.Combine("b", "renamed.txt"));
    }

    [Fact]
    public async Task Archive_entries_in_results_are_not_written_to()
    {
        var zip = Path.Combine(_left, "pack.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("inside.txt").Open());
            w.Write("x");
        }

        try
        {
            using var _ = await SearchLeft("inside.txt", inArchives: true);
            LeftNames.Should().Equal("..", Path.Combine("pack.zip", "inside.txt"));
            _vm.Left.CurrentIndex = 1;

            await _vm.DeleteAsync(permanent: true);

            _dialogs.Errors.Should().ContainSingle();
            File.Exists(zip).Should().BeTrue();
        }
        finally
        {
            ArchiveCatalog.Forget(zip);
        }
    }

    [Fact]
    public async Task Navigating_anywhere_leaves_the_results()
    {
        _tree.File("left/a.txt");
        using var _ = await SearchLeft("*.txt");

        await _vm.Left.NavigateAsync(_right);

        _vm.Left.IsShowingResults.Should().BeFalse();
        _vm.Left.ResultsTitle.Should().BeNull();
    }

    [Fact]
    public async Task A_broken_regular_expression_is_reported()
    {
        using var search = new SearchViewModel(_tree.Root) { Masks = "([", NameRegex = true };
        await search.StartAsync();
        search.Error.Should().NotBeNullOrEmpty();
        search.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task An_archive_file_on_disk_is_an_ordinary_file_for_F5()
    {
        var zip = Path.Combine(_left, "plain.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("x.txt");
        }

        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "plain.zip");
        _dialogs.TransferAnswer = _right + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();

        _dialogs.Errors.Should().BeEmpty();
        File.Exists(Path.Combine(_right, "plain.zip")).Should().BeTrue();
        (await MainViewModel.MaterializeAsync(_vm.Left.CurrentItem!)).Should().Be(zip, "F3 on the archive file views the file itself");
    }
}
