using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>Drops go through the F5/F6 question with the target filled in, then the usual operations.</summary>
public sealed class DragDropTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _a;
    private readonly string _b;

    public DragDropTests()
    {
        _a = _tree.Dir("a");
        _b = _tree.Dir("b");
        Directory.CreateDirectory(Path.Combine(_b, "inner"));
        File.WriteAllText(Path.Combine(_a, "one.txt"), "1");
        File.WriteAllText(Path.Combine(_a, "two.txt"), "2");
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), new FakeBin());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task OpenAsync()
    {
        await _vm.Left.NavigateAsync(_a);
        await _vm.Right.NavigateAsync(_b);
    }

    private static string Slash(string path) => path + Path.DirectorySeparatorChar;

    [Fact]
    public async Task Dragging_marked_files_to_the_other_panel_asks_with_that_folder_and_copies()
    {
        await OpenAsync();
        var paths = new[] { Path.Combine(_a, "one.txt"), Path.Combine(_a, "two.txt") };
        _dialogs.TransferAnswer = Slash(_b);

        await _vm.DropAsync(paths, _b, move: false, from: _vm.Left);

        _dialogs.LastDefaultTarget.Should().Be(Slash(_b));
        File.ReadAllText(Path.Combine(_b, "two.txt")).Should().Be("2");
        File.Exists(Path.Combine(_a, "one.txt")).Should().BeTrue();
        _vm.Right.Items.Select(i => i.Name).Should().Contain("one.txt", "the target panel refreshes");
    }

    [Fact]
    public async Task Dropping_on_a_folder_row_with_shift_moves_into_it()
    {
        await OpenAsync();
        var target = Path.Combine(_b, "inner");
        _dialogs.TransferAnswer = Slash(target);

        await _vm.DropAsync([Path.Combine(_a, "one.txt")], target, move: true, from: _vm.Left);

        File.Exists(Path.Combine(target, "one.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_a, "one.txt")).Should().BeFalse();
        _vm.Left.Items.Select(i => i.Name).Should().NotContain("one.txt");
    }

    [Fact]
    public async Task Files_from_Explorer_are_copied_into_the_panel()
    {
        await OpenAsync();
        var outside = _tree.Dir("desktop");
        File.WriteAllText(Path.Combine(outside, "photo.jpg"), "jpg");
        Directory.CreateDirectory(Path.Combine(outside, "album"));
        File.WriteAllText(Path.Combine(outside, "album", "x.jpg"), "x");
        _dialogs.TransferAnswer = Slash(_b);

        await _vm.DropAsync([Path.Combine(outside, "photo.jpg"), Path.Combine(outside, "album")], _b, move: false, from: null);

        _dialogs.LastDefaultTarget.Should().Be(Slash(_b));
        File.ReadAllText(Path.Combine(_b, "album", "x.jpg")).Should().Be("x");
        File.Exists(Path.Combine(outside, "photo.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task Cancelling_the_question_cancels_the_drop()
    {
        await OpenAsync();
        _dialogs.TransferAnswer = null;

        await _vm.DropAsync([Path.Combine(_a, "one.txt")], _b, move: true, from: _vm.Left);

        File.Exists(Path.Combine(_a, "one.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_b, "one.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Dropping_into_an_open_zip_adds_to_it()
    {
        var zip = Path.Combine(_tree.Root, "pack.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("keep.txt");
        }

        await _vm.Left.NavigateAsync(_a);
        await _vm.Right.NavigateAsync(zip);
        _dialogs.TransferAnswer = Slash(zip);

        await _vm.DropAsync([Path.Combine(_a, "one.txt")], zip, move: false, from: _vm.Left);

        ArchiveCatalog.Open(zip).List(string.Empty).Select(e => e.Name).Should().BeEquivalentTo(["keep.txt", "one.txt"]);
        ArchiveCatalog.Forget(zip);
    }
}
