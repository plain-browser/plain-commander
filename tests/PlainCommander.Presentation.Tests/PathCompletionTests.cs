using AwesomeAssertions;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class PathCompletionTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Suggests_subfolders_matching_the_typed_stem_with_a_trailing_separator()
    {
        _tree.Dir("Projects");
        _tree.Dir("Photos");
        _tree.Dir("Music");
        _tree.File("Pr.txt");

        var typed = Path.Combine(_tree.Root, "p");
        var suggestions = PathCompletion.Suggest(typed);

        suggestions.Should().Equal(
            Path.Combine(_tree.Root, "Photos") + Path.DirectorySeparatorChar,
            Path.Combine(_tree.Root, "Projects") + Path.DirectorySeparatorChar);
    }

    [Fact]
    public void A_trailing_separator_lists_everything_inside_and_bad_input_gives_nothing()
    {
        _tree.Dir("a");
        _tree.Dir("b");

        PathCompletion.Suggest(_tree.Root + Path.DirectorySeparatorChar).Should().HaveCount(2);
        PathCompletion.Suggest(Path.Combine(_tree.Root, "missing", "x")).Should().BeEmpty();
        PathCompletion.Suggest("   ").Should().BeEmpty();
        PathCompletion.Suggest(_tree.Root + Path.DirectorySeparatorChar, limit: 1).Should().HaveCount(1);
    }
}
