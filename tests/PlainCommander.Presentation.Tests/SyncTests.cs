using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class SyncTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public SyncTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "settings.json")), _dialogs, new NoPrompts(), new FakeBin());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private static void Write(string folder, string name, string content, DateTime utc)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, utc);
    }

    private async Task<SyncViewModel?> CompareAsync(bool accept)
    {
        Write(_left, "only-left.txt", "a", T0);
        Write(_right, "only-right.txt", "b", T0);
        Write(_left, "newer.txt", "one\ntwo, edited\nthree\n", T0.AddHours(1));
        Write(_right, "newer.txt", "one\ntwo\nthree\n", T0);
        Write(_left, "clash.txt", "left", T0);
        Write(_right, "clash.txt", "right!", T0);
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _dialogs.SyncAnswer = accept;
        return await _vm.CompareDirectoriesAsync();
    }

    [Fact]
    public async Task Compare_offers_sync_and_declining_keeps_only_the_marks()
    {
        var sync = await CompareAsync(accept: false);

        sync.Should().BeNull();
        _dialogs.SyncOffers.Should().Be(1);
        _vm.Left.MarkedItems.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Identical_folders_do_not_offer_sync()
    {
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _dialogs.SyncAnswer = true;

        (await _vm.CompareDirectoriesAsync()).Should().BeNull();
        _dialogs.SyncOffers.Should().Be(0);
    }

    [Fact]
    public async Task Sync_window_rows_default_to_copying_the_newer_side_and_cycle()
    {
        var sync = (await CompareAsync(accept: true))!;

        sync.Rows.Select(r => (r.Relative, r.Action)).Should().Equal(
            ("clash.txt", SyncAction.None),
            ("newer.txt", SyncAction.CopyToRight),
            ("only-left.txt", SyncAction.CopyToRight),
            ("only-right.txt", SyncAction.CopyToLeft));
        sync.Summary.Should().MatchRegex("2.*1.*1");
        sync.Rows[1].CanDiff.Should().BeTrue();
        sync.Rows[2].CanDiff.Should().BeFalse();

        var clash = sync.Rows[0];
        clash.Cycle();
        clash.ActionGlyph.Should().Be("→");
        clash.Cycle();
        clash.Action.Should().Be(SyncAction.CopyToLeft);
        clash.Cycle();
        clash.Action.Should().Be(SyncAction.None);

        sync.SetAll(SyncAction.None);
        sync.CanSync.Should().BeFalse();
        sync.ResetDefaults();
        sync.CanSync.Should().BeTrue();
        sync.ActionCount.Should().Be(3);
    }

    [Fact]
    public async Task Synchronize_copies_both_ways_and_unmarks_both_panels()
    {
        var sync = (await CompareAsync(accept: true))!;
        sync.Rows.Single(r => r.Relative == "clash.txt").Action = SyncAction.CopyToLeft;

        await _vm.RunSyncAsync(sync);

        File.ReadAllText(Path.Combine(_right, "only-left.txt")).Should().Be("a");
        File.ReadAllText(Path.Combine(_left, "only-right.txt")).Should().Be("b");
        File.ReadAllText(Path.Combine(_right, "newer.txt")).Should().Contain("edited");
        File.ReadAllText(Path.Combine(_left, "clash.txt")).Should().Be("right!");
        _vm.Left.MarkedItems.Should().BeEmpty();
        _vm.Right.MarkedItems.Should().BeEmpty();
        DirectoryCompare.Run(_left, _right, new CompareOptions()).Count.Should().Be(0);
    }

    [Fact]
    public async Task Diff_shows_changed_lines_and_jumps_between_blocks()
    {
        var sync = (await CompareAsync(accept: true))!;
        var row = sync.Rows.Single(r => r.Relative == "newer.txt");
        var diff = new DiffViewModel(row.LeftPath, row.RightPath);

        await diff.LoadAsync(TestContext.Current.CancellationToken);

        diff.Error.Should().BeNull();
        diff.Rows.Select(r => r.Kind).Should().Equal(DiffKind.Same, DiffKind.Changed, DiffKind.Same);
        diff.Summary.Should().MatchRegex("1");
        diff.FindChange(-1, forward: true).Should().Be(1);
        diff.FindChange(1, forward: true).Should().Be(-1);
        diff.FindChange(3, forward: false).Should().Be(1);
    }

    [Fact]
    public async Task Diff_of_a_binary_file_explains_instead_of_showing_garbage()
    {
        var a = Path.Combine(_left, "x.bin");
        var b = Path.Combine(_right, "x.bin");
        await File.WriteAllBytesAsync(a, [0, 1, 2, 0, 255], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(b, [0, 1, 3, 0, 255], TestContext.Current.CancellationToken);
        var diff = new DiffViewModel(a, b);

        await diff.LoadAsync(TestContext.Current.CancellationToken);

        diff.Error.Should().NotBeNullOrEmpty();
        diff.Rows.Should().BeEmpty();
    }
}
