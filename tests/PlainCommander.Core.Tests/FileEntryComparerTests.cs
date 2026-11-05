using AwesomeAssertions;
using PlainCommander.Core.Model;
using PlainCommander.Core.Sorting;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class FileEntryComparerTests
{
    private static FileEntry File(string name, long size = 0) => new()
    {
        Name = name,
        FullPath = "/" + name,
        IsDirectory = false,
        Size = size,
    };

    private static FileEntry Dir(string name) => new()
    {
        Name = name,
        FullPath = "/" + name,
        IsDirectory = true,
    };

    [Fact]
    public void Parent_link_then_directories_then_files()
    {
        var items = new[] { File("a"), Dir("z"), FileEntry.ParentLink("/"), Dir("b"), File("B") };
        var sorted = items.Order(new FileEntryComparer(new SortSpec())).Select(e => e.Name);
        sorted.Should().Equal("..", "b", "z", "a", "B");
    }

    [Fact]
    public void Descending_size_keeps_directories_by_name()
    {
        var items = new[] { File("small", 1), File("big", 100), Dir("y"), Dir("x") };
        var spec = new SortSpec(SortColumn.Size, SortDirection.Descending);
        var sorted = items.Order(new FileEntryComparer(spec)).Select(e => e.Name);
        sorted.Should().Equal("x", "y", "big", "small");
    }

    [Fact]
    public void Ties_on_size_stay_in_ascending_name_order_even_when_descending()
    {
        var items = new[] { File("c", 5), File("a", 5), File("b", 9) };
        var spec = new SortSpec(SortColumn.Size, SortDirection.Descending);
        var sorted = items.Order(new FileEntryComparer(spec)).Select(e => e.Name);
        sorted.Should().Equal("b", "a", "c");
    }

    [Fact]
    public void Name_descending_reverses_files_only()
    {
        var items = new[] { File("a"), File("b"), Dir("x"), Dir("y") };
        var spec = new SortSpec(SortColumn.Name, SortDirection.Descending);
        var sorted = items.Order(new FileEntryComparer(spec)).Select(e => e.Name);
        sorted.Should().Equal("x", "y", "b", "a");
    }

    [Fact]
    public void Toggle_flips_direction_on_same_column()
    {
        var spec = new SortSpec(SortColumn.Name);
        spec.Toggle(SortColumn.Name).Direction.Should().Be(SortDirection.Descending);
        spec.Toggle(SortColumn.Size).Should().Be(new SortSpec(SortColumn.Size));
    }
}
