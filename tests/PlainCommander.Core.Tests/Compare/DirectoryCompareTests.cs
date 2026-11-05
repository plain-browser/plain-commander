using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Compare;

public sealed class DirectoryCompareTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly Sandbox _s = new();
    private readonly string _l;
    private readonly string _r;

    public DirectoryCompareTests()
    {
        _l = _s.Dir("left");
        _r = _s.Dir("right");
    }

    public void Dispose() => _s.Dispose();

    private void Both(string name, string leftContent, string rightContent, DateTime leftTime, DateTime rightTime)
    {
        _s.File(Path.Combine("left", name), leftContent, leftTime);
        _s.File(Path.Combine("right", name), rightContent, rightTime);
    }

    [Fact]
    public void Classifies_unique_newer_equal_and_same_time_different_files()
    {
        _s.File(Path.Combine("left", "only-left.txt"), "a", T0);
        _s.File(Path.Combine("right", "only-right.txt"), "b", T0);
        Both("same.txt", "x", "x", T0, T0.AddSeconds(1));       // within the 2 s tolerance
        Both("l-newer.txt", "x", "x", T0.AddHours(1), T0);
        Both("r-newer.txt", "x", "x", T0, T0.AddHours(1));
        Both("diff.txt", "short", "much longer", T0, T0);

        var result = DirectoryCompare.Run(_l, _r, new CompareOptions());

        result.Items.Should().BeEquivalentTo(new Dictionary<string, Difference>
        {
            ["only-left.txt"] = Difference.OnlyLeft,
            ["only-right.txt"] = Difference.OnlyRight,
            ["l-newer.txt"] = Difference.LeftNewer,
            ["r-newer.txt"] = Difference.RightNewer,
            ["diff.txt"] = Difference.Different,
        });
        result.MarkedOn(left: true).Should().BeEquivalentTo(["only-left.txt", "l-newer.txt", "diff.txt"]);
        result.MarkedOn(left: false).Should().BeEquivalentTo(["only-right.txt", "r-newer.txt", "diff.txt"]);
    }

    [Fact]
    public void Content_mode_treats_equal_bytes_with_different_times_as_the_same()
    {
        Both("copy.txt", "identical", "identical", T0.AddHours(1), T0);
        Both("edited.txt", "version 1", "version 2", T0.AddHours(1), T0);

        DirectoryCompare.Run(_l, _r, new CompareOptions()).Items.Keys.Should().BeEquivalentTo(["copy.txt", "edited.txt"]);
        DirectoryCompare.Run(_l, _r, new CompareOptions(ByContent: true)).Items.Keys.Should().BeEquivalentTo(["edited.txt"]);
    }

    [Fact]
    public void Folders_match_by_name_and_a_file_against_a_folder_is_a_mismatch()
    {
        _s.Dir("left", "dir-both"); _s.Dir("right", "dir-both");
        _s.Dir("left", "dir-left");
        _s.File(Path.Combine("left", "thing"), "f", T0);
        _s.Dir("right", "thing");

        var result = DirectoryCompare.Run(_l, _r, new CompareOptions());

        result.Items.Should().BeEquivalentTo(new Dictionary<string, Difference>
        {
            ["dir-left"] = Difference.OnlyLeft,
            ["thing"] = Difference.TypeMismatch,
        });
    }

    [Fact]
    public void Subfolders_are_compared_and_the_top_level_folder_is_marked_when_something_below_differs()
    {
        _s.Dir("left", "pkg"); _s.Dir("right", "pkg");
        _s.File(Path.Combine("left", "pkg", "new.cs"), "n", T0);
        _s.File(Path.Combine("right", "pkg", "keep.cs"), "k", T0);
        _s.Dir("left", "calm"); _s.Dir("right", "calm");
        _s.File(Path.Combine("left", "calm", "a.txt"), "a", T0);
        _s.File(Path.Combine("right", "calm", "a.txt"), "a", T0);

        DirectoryCompare.Run(_l, _r, new CompareOptions()).Count.Should().Be(0, "without subfolders both 'pkg' are just folders present on both sides");

        var deep = DirectoryCompare.Run(_l, _r, new CompareOptions(Subfolders: true));

        deep.Items.Should().BeEquivalentTo(new Dictionary<string, Difference>
        {
            [@"pkg\new.cs"] = Difference.OnlyLeft,
            [@"pkg\keep.cs"] = Difference.OnlyRight,
        });
        deep.MarkedOn(left: true).Should().BeEquivalentTo(["pkg"]);
        deep.MarkedOn(left: false).Should().BeEquivalentTo(["pkg"]);
    }

    [Fact]
    public void A_missing_side_counts_as_empty_and_cancellation_is_honoured()
    {
        _s.File(Path.Combine("left", "a.txt"), "a", T0);

        DirectoryCompare.Run(_l, _s.P("nowhere"), new CompareOptions()).Items.Should().ContainKey("a.txt").WhoseValue.Should().Be(Difference.OnlyLeft);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => DirectoryCompare.Run(_l, _r, new CompareOptions(), cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }
}
