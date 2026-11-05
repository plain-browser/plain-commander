using AwesomeAssertions;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class SearchViewModelTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Sizes_parse_with_suffixes_and_reject_nonsense()
    {
        SearchViewModel.ParseSize("").Should().BeNull();
        SearchViewModel.ParseSize("500").Should().Be(500);
        SearchViewModel.ParseSize("10k").Should().Be(10 * 1024);
        SearchViewModel.ParseSize("2M").Should().Be(2 * 1024 * 1024);
        SearchViewModel.ParseSize("1.5G").Should().Be((long)(1.5 * (1L << 30)));
        var act = () => SearchViewModel.ParseSize("abc");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task Search_streams_results_and_reports_status()
    {
        _tree.File("a.txt"); _tree.File("b.md"); _tree.Dir("sub"); _tree.File(Path.Combine("sub", "c.txt"));
        using var vm = new SearchViewModel(_tree.Root) { Masks = "*.txt" };

        await vm.StartAsync();

        vm.Results.Select(r => r.Name).Should().BeEquivalentTo(["a.txt", "c.txt"]);
        vm.IsRunning.Should().BeFalse();
        vm.Status.Should().MatchRegex("Gotowe|Done").And.Contain("2");
        vm.Error.Should().BeNull();
    }

    [Fact]
    public async Task Bad_input_shows_an_error_instead_of_running()
    {
        using var vm = new SearchViewModel(Path.Combine(_tree.Root, "missing"));
        await vm.StartAsync();
        vm.Error.Should().NotBeNullOrEmpty();

        using var bad = new SearchViewModel(_tree.Root) { MinSize = "lots" };
        await bad.StartAsync();
        bad.Error.Should().Contain("lots");
        bad.IsRunning.Should().BeFalse();
    }
}
