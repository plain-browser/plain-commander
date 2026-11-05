using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class CompareTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public CompareTests()
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

    [Fact]
    public async Task Shift_F2_marks_what_differs_on_each_side_and_remembers_the_options()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Write(_left, "only-left.txt", "a", t0);
        Write(_right, "only-right.txt", "b", t0);
        Write(_left, "newer-left.txt", "x, edited", t0.AddHours(1));
        Write(_right, "newer-left.txt", "x", t0);
        Write(_left, "same.txt", "s", t0);
        Write(_right, "same.txt", "s", t0);
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _vm.Left.MarkAll(); // stale marks must go
        _dialogs.CompareAnswer = new CompareOptions(Subfolders: true, ByContent: true);

        await _vm.CompareDirectoriesAsync();

        _vm.Left.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["only-left.txt", "newer-left.txt"]);
        _vm.Right.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["only-right.txt"]);
        _vm.Settings.CompareSubfolders.Should().BeTrue();
        _vm.Settings.CompareByContent.Should().BeTrue();
        _dialogs.Infos.Should().BeEmpty();
    }

    [Fact]
    public async Task Identical_folders_say_so_and_cancel_or_archives_do_nothing()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Write(_left, "a.txt", "a", t0);
        Write(_right, "a.txt", "a", t0);
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);

        await _vm.CompareDirectoriesAsync();
        _dialogs.Infos.Should().ContainSingle().Which.Should().MatchRegex("nie różnią|do not differ");
        _vm.Left.MarkedItems.Should().BeEmpty();

        _dialogs.CompareAnswer = null;
        _dialogs.Infos.Clear();
        await _vm.CompareDirectoriesAsync();
        _dialogs.Infos.Should().BeEmpty("cancelling the options dialog changes nothing");
    }
}
