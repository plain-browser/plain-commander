using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class SyncAndDiffTests : IDisposable
{
    private static readonly DateTime Old = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public void Identical_texts_have_no_differences()
    {
        var result = TextDiff.Compare(["a", "b"], ["a", "b"]);
        result.Identical.Should().BeTrue();
        result.Rows.Should().HaveCount(2).And.OnlyContain(r => r.Kind == DiffKind.Same);
    }

    [Fact]
    public void Added_removed_and_changed_lines_line_up_side_by_side()
    {
        string[] left = ["header", "keep 1", "old value", "keep 2", "gone", "footer"];
        string[] right = ["header", "keep 1", "new value", "keep 2", "footer", "appended"];

        var result = TextDiff.Compare(left, right);

        result.Changed.Should().Be(1);
        result.Removed.Should().Be(1);
        result.Added.Should().Be(1);
        result.Rows.Should().ContainEquivalentOf(new DiffRow(DiffKind.Changed, 3, "old value", 3, "new value"));
        result.Rows.Should().ContainEquivalentOf(new DiffRow(DiffKind.Removed, 5, "gone", null, null));
        result.Rows.Should().ContainEquivalentOf(new DiffRow(DiffKind.Added, null, null, 6, "appended"));
        result.Rows.Where(r => r.Kind != DiffKind.Added).Select(r => r.LeftNumber).Should().BeInAscendingOrder();
        result.Rows.Where(r => r.Kind != DiffKind.Removed).Select(r => r.RightNumber).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Empty_side_is_all_additions()
    {
        var result = TextDiff.Compare([], ["x", "y"]);
        result.Added.Should().Be(2);
        result.Rows.Select(r => r.RightText).Should().Equal("x", "y");
    }

    [Fact]
    public async Task Binary_files_are_not_diffed_and_text_files_are_read_with_their_encoding()
    {
        var a = _s.File("a.txt", "zażółć\r\ngęślą\r\n");
        var b = _s.File("b.txt", "zażółć\r\njaźń\r\n");
        var bin = _s.P("x.bin");
        await File.WriteAllBytesAsync(bin, [0, 1, 2, 0, 0, 0, 5], TestContext.Current.CancellationToken);

        var pair = await TextDiff.ReadPairAsync(a, b, TestContext.Current.CancellationToken);
        pair!.Value.Left.Should().Equal("zażółć", "gęślą");
        TextDiff.Compare(pair.Value.Left, pair.Value.Right).Changed.Should().Be(1);

        (await TextDiff.ReadPairAsync(a, bin, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public void Default_actions_copy_the_newer_or_only_copy_and_leave_ties_to_the_user()
    {
        SyncItem.DefaultAction(Difference.OnlyLeft).Should().Be(SyncAction.CopyToRight);
        SyncItem.DefaultAction(Difference.RightNewer).Should().Be(SyncAction.CopyToLeft);
        SyncItem.DefaultAction(Difference.Different).Should().Be(SyncAction.None);
        SyncItem.DefaultAction(Difference.TypeMismatch).Should().Be(SyncAction.None);
    }

    [Fact]
    public async Task Hidden_files_take_part_only_when_the_panels_show_them()
    {
        _s.File("L/seen.txt", "x");
        _s.File("R/seen.txt", "x");
        var hidden = _s.File("L/.hidden.txt", "h"); // a dot file is hidden off Windows; on Windows the attribute says so
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        }

        var all = await DirectoryCompare.RunAsync(new DiskCompareSide(_s.P("L")), new DiskCompareSide(_s.P("R")), new CompareOptions(), token: TestContext.Current.CancellationToken);
        var shown = await DirectoryCompare.RunAsync(new DiskCompareSide(_s.P("L"), includeHidden: false), new DiskCompareSide(_s.P("R"), includeHidden: false), new CompareOptions(), token: TestContext.Current.CancellationToken);

        all.Count.Should().Be(1);
        shown.Count.Should().Be(0);
    }

    [Fact]
    public async Task Synchronizing_copies_both_ways_and_overwrites_without_asking()
    {
        _s.File("L/only-left.txt", "L");
        _s.File("L/sub/shared.txt", "left newer", New);
        _s.File("R/sub/shared.txt", "right older", Old);
        _s.File("R/only-right/deep.txt", "R");
        var result = DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true));
        var plan = SyncPlan.From(result);
        plan.Select(p => (p.Relative, p.Action)).Should().BeEquivalentTo(new[]
        {
            ("only-left.txt", SyncAction.CopyToRight),
            ("only-right", SyncAction.CopyToLeft),
            ("sub\\shared.txt", SyncAction.CopyToRight), // comparison paths always use "\\"
        });

        var outcome = await Run.Async(new SyncOperation(_s.P("L"), _s.P("R"), plan)); // no prompts scripted: any question would throw

        outcome.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("R/only-left.txt").Should().Be("L");
        _s.Read("R/sub/shared.txt").Should().Be("left newer");
        _s.Read("L/only-right/deep.txt").Should().Be("R");
        DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true)).Count.Should().Be(0);
    }

    [Fact]
    public async Task Items_set_to_none_are_left_alone()
    {
        _s.File("L/a.txt", "L");
        _s.Dir("R");
        var plan = SyncPlan.From(DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions())).Select(i => i with { Action = SyncAction.None }).ToList();

        await Run.Async(new SyncOperation(_s.P("L"), _s.P("R"), plan));

        File.Exists(_s.P("R", "a.txt")).Should().BeFalse();
    }
}
