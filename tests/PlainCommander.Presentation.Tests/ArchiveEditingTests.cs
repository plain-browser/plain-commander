using System.Formats.Tar;
using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>F8, F5/F6, Shift+F6 and F7 inside a zip change the archive; formats without a writer refuse.</summary>
public sealed class ArchiveEditingTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _zip;
    private readonly string _disk;

    public ArchiveEditingTests()
    {
        _zip = Path.Combine(_tree.Root, "pack.zip");
        using (var archive = ZipFile.Open(_zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "docs/readme.txt", "docs/notes.txt", "top.txt" })
            {
                using var w = new StreamWriter(archive.CreateEntry(name).Open());
                w.Write(name);
            }
        }

        _disk = _tree.Dir("disk");
        File.WriteAllText(Path.Combine(_disk, "new.txt"), "new");
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), new FakeBin());
    }

    public void Dispose()
    {
        _vm.Dispose();
        ArchiveCatalog.Forget(_zip);
        _tree.Dispose();
    }

    private IEnumerable<string> Names(string inner) => ArchiveCatalog.Open(_zip).List(inner).Select(e => e.Name);

    private void Cursor(string name) => _vm.ActivePanel.CurrentIndex = _vm.ActivePanel.Items.ToList().FindIndex(i => i.Name == name);

    [Fact]
    public async Task F8_removes_entries_from_the_archive_after_confirmation()
    {
        await _vm.Left.NavigateAsync(ArchivePath.Join(_zip, "docs"));
        Cursor("notes.txt");

        await _vm.DeleteAsync(permanent: false);

        _dialogs.LastDeletePermanent.Should().BeTrue("nothing from an archive goes to the Recycle Bin");
        Names("docs").Should().Equal("readme.txt");
        _vm.Left.Items.Select(i => i.Name).Should().Equal("..", "readme.txt");
        _dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task F5_from_disk_adds_to_the_open_archive_folder_and_F6_removes_the_source()
    {
        await _vm.Left.NavigateAsync(_disk);
        await _vm.Right.NavigateAsync(ArchivePath.Join(_zip, "docs"));
        Cursor("new.txt");
        _dialogs.TransferAnswer = ArchivePath.Join(_zip, "docs") + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();

        Names("docs").Should().BeEquivalentTo(["readme.txt", "notes.txt", "new.txt"]);
        File.Exists(Path.Combine(_disk, "new.txt")).Should().BeTrue();
        _vm.Right.Items.Select(i => i.Name).Should().Contain("new.txt", "the archive panel refreshes");

        File.WriteAllText(Path.Combine(_disk, "moved.txt"), "m");
        await _vm.Left.RefreshAsync();
        Cursor("moved.txt");
        _dialogs.TransferAnswer = _zip + Path.DirectorySeparatorChar;
        await _vm.MoveAsync();

        Names(string.Empty).Should().Contain("moved.txt");
        File.Exists(Path.Combine(_disk, "moved.txt")).Should().BeFalse();
        _dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Shift_F6_renames_inside_the_archive_and_keeps_the_cursor_on_the_new_name()
    {
        await _vm.Left.NavigateAsync(_zip);
        Cursor("docs");
        _dialogs.NameAnswer = "papers";

        await _vm.RenameCurrentAsync();

        Names(string.Empty).Should().BeEquivalentTo(["papers", "top.txt"]);
        Names("papers").Should().BeEquivalentTo(["readme.txt", "notes.txt"]);
        _vm.Left.CurrentItem!.Name.Should().Be("papers");
    }

    [Fact]
    public async Task Shift_F6_with_a_separator_is_refused()
    {
        await _vm.Left.NavigateAsync(_zip);
        Cursor("top.txt");
        _dialogs.NameAnswer = "a/b.txt";

        await _vm.RenameCurrentAsync();

        _dialogs.Errors.Should().ContainSingle();
        Names(string.Empty).Should().Contain("top.txt");
    }

    [Fact]
    public async Task F7_creates_a_folder_inside_the_archive()
    {
        await _vm.Left.NavigateAsync(ArchivePath.Join(_zip, "docs"));
        _dialogs.NameAnswer = "drafts";

        await _vm.MakeDirectoryAsync();

        Names("docs").Should().Contain("drafts");
        _vm.Left.CurrentItem!.Name.Should().Be("drafts");
    }

    [Fact]
    public async Task Copying_between_two_archives_is_refused()
    {
        var other = Path.Combine(_tree.Root, "other.zip");
        using (var archive = ZipFile.Open(other, ZipArchiveMode.Create))
        {
            archive.CreateEntry("x.txt");
        }

        await _vm.Left.NavigateAsync(other);
        await _vm.Right.NavigateAsync(_zip);
        Cursor("x.txt");
        _dialogs.TransferAnswer = _zip + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();

        _dialogs.Errors.Should().ContainSingle().Which.Should().MatchRegex("z archiwum do archiwum|one archive into another");
        Names(string.Empty).Should().NotContain("x.txt");
        ArchiveCatalog.Forget(other);
    }

    [Fact]
    public async Task Archives_without_a_writer_stay_read_only()
    {
        // a plain tar under a .tar.bz2 name: the reader detects it, but there is no bzip2 writer, so it is not editable
        var gz = Path.Combine(_tree.Root, "one.tar.bz2");
        await using (var file = File.Create(gz))
        {
            await TarFile.CreateFromDirectoryAsync(_disk, file, includeBaseDirectory: false);
        }

        await _vm.Left.NavigateAsync(gz);
        if (!_vm.Left.IsInArchive)
        {
            return; // the reader does not open it on this platform; the Core tests cover CanEdit
        }

        Cursor("new.txt");
        await _vm.DeleteAsync(permanent: true);

        _dialogs.Errors.Should().ContainSingle().Which.Should().MatchRegex("tylko do odczytu|read-only");
        ArchiveCatalog.Forget(gz);
    }
}
