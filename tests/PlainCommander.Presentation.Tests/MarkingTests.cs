using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class MarkingTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly PanelViewModel _panel = new(new LocalFileSystem(), new RecordingLauncher());

    public void Dispose()
    {
        _panel.Dispose();
        _tree.Dispose();
    }

    private FileItem Item(string name) => _panel.Items.Single(i => i.Name == name);

    private void Cursor(string name) => _panel.CurrentIndex = _panel.Items.ToList().FindIndex(i => i.Name == name);

    [Fact]
    public async Task Insert_marks_and_moves_down_but_never_marks_the_parent_link()
    {
        _tree.File("a.txt");
        _tree.File("b.txt");
        await _panel.NavigateAsync(_tree.Root);

        _panel.MarkCurrentAndAdvance(); // on ".."
        _panel.MarkCurrentAndAdvance(); // on a.txt

        _panel.Items[0].IsMarked.Should().BeFalse();
        Item("a.txt").IsMarked.Should().BeTrue();
        _panel.CurrentItem!.Name.Should().Be("b.txt");
    }

    [Fact]
    public async Task Space_on_a_folder_marks_it_and_shows_its_size()
    {
        _tree.File("dir/one.bin", 1000);
        _tree.File("dir/sub/two.bin", 24);
        await _panel.NavigateAsync(_tree.Root);
        Cursor("dir");

        await _panel.MarkCurrentAndMeasureAsync();

        Item("dir").IsMarked.Should().BeTrue();
        Item("dir").DirectorySize.Should().Be(1024);
        Item("dir").SizeText.Should().NotBe("<DIR>");
        _panel.Status.Should().ContainAny("Selected 0 of 0 files (1.0 KB of 1.0 KB), 1 of 1 folder", "Zaznaczono 0 z 0 plików (1,0 KB z 1,0 KB), 1 z 1 katalogu");
    }

    [Fact]
    public async Task Masks_invert_all_and_clear()
    {
        _tree.File("a.txt");
        _tree.File("b.txt");
        _tree.File("c.md");
        _tree.Dir("d.txt");
        await _panel.NavigateAsync(_tree.Root);

        _panel.MarkByMask("*.txt", mark: true).Should().Be(2, "folders are not marked by masks");
        _panel.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["a.txt", "b.txt"]);

        _panel.InvertMarks();
        _panel.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["c.md"]);

        _panel.MarkAll();
        _panel.MarkedItems.Should().HaveCount(4);

        _panel.MarkByMask("*.*", mark: false);
        _panel.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["d.txt"], "unmark by mask also leaves folders alone");

        _panel.ClearMarks();
        _panel.MarkedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Operation_sources_are_the_marks_or_the_cursor_never_the_parent_link()
    {
        _tree.File("a.txt");
        _tree.File("b.txt");
        await _panel.NavigateAsync(_tree.Root);

        _panel.OperationSources.Should().BeEmpty("the cursor is on ..");

        Cursor("b.txt");
        _panel.OperationSources.Select(i => i.Name).Should().Equal("b.txt");

        _panel.ToggleMark(Item("a.txt"));
        _panel.OperationSources.Select(i => i.Name).Should().Equal("a.txt");
    }

    [Fact]
    public async Task Refresh_keeps_marks_and_sizes_and_the_cursor_position_when_its_item_is_gone()
    {
        _tree.File("a.txt");
        var doomed = _tree.File("b.txt");
        _tree.File("c.txt");
        _tree.File("dir/x.bin", 10);
        await _panel.NavigateAsync(_tree.Root);
        _panel.ToggleMark(Item("c.txt"));
        Cursor("dir");
        await _panel.MarkCurrentAndMeasureAsync();
        Cursor("b.txt");
        var index = _panel.CurrentIndex;

        File.Delete(doomed);
        await _panel.RefreshAsync();

        Item("c.txt").IsMarked.Should().BeTrue();
        Item("dir").DirectorySize.Should().Be(10);
        _panel.CurrentIndex.Should().Be(index);
    }

    [Fact]
    public async Task Changes_on_disk_show_up_without_a_manual_refresh()
    {
        _panel.AutoRefreshDelay = TimeSpan.FromMilliseconds(50);
        await _panel.NavigateAsync(_tree.Root);

        _tree.File("appeared.txt");

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_panel.Items.Any(i => i.Name == "appeared.txt") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        _panel.Items.Should().Contain(i => i.Name == "appeared.txt");
    }
}
