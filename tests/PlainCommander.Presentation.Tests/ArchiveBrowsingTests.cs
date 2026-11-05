using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class ArchiveBrowsingTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _zip;

    public ArchiveBrowsingTests()
    {
        _zip = Path.Combine(_tree.Root, "pack.zip");
        using (var archive = ZipFile.Open(_zip, ZipArchiveMode.Create))
        {
            var e = archive.CreateEntry("docs/readme.txt");
            using var w = new StreamWriter(e.Open());
            w.Write("inside");
        }

        _tree.Dir("out");
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), _launcher, new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), new FakeBin());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    [Fact]
    public async Task Enter_on_an_archive_browses_it_like_a_folder_and_backspace_leaves_it()
    {
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == "pack.zip");

        await _vm.Left.OpenCurrentAsync();

        _vm.Left.Path.Should().Be(_zip);
        _vm.Left.IsInArchive.Should().BeTrue();
        _vm.Left.Items.Select(i => i.Name).Should().Equal("..", "docs");
        _vm.Left.Breadcrumbs[^1].Name.Should().Be("pack.zip");

        _vm.Left.CurrentIndex = 1;
        await _vm.Left.OpenCurrentAsync();
        _vm.Left.Items.Select(i => i.Name).Should().Equal("..", "readme.txt");

        await _vm.Left.GoUpAsync();
        await _vm.Left.GoUpAsync();
        _vm.Left.Path.Should().Be(_tree.Root);
        _vm.Left.IsInArchive.Should().BeFalse();
        _vm.Left.CurrentItem!.Name.Should().Be("pack.zip", "the cursor returns to the archive we came out of");
    }

    [Fact]
    public async Task F5_inside_an_archive_extracts_and_F6_out_of_it_is_refused()
    {
        await _vm.Left.NavigateAsync(ArchivePath.Join(_zip, "docs"));
        await _vm.Right.NavigateAsync(Path.Combine(_tree.Root, "out"));
        _vm.Left.CurrentIndex = 1;
        _dialogs.TransferAnswer = Path.Combine(_tree.Root, "out") + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();
        File.ReadAllText(Path.Combine(_tree.Root, "out", "readme.txt")).Should().Be("inside");

        await _vm.MoveAsync();
        await _vm.NewFileAsync();
        _dialogs.Errors.Should().HaveCount(2).And.AllSatisfy(e => e.Should().MatchRegex("wewnątrz archiwum|inside an archive"));
        ArchiveCatalog.Open(_zip).List("docs").Select(e => e.Name).Should().Equal("readme.txt");
    }

    [Fact]
    public async Task Enter_on_a_file_inside_an_archive_launches_a_temp_copy()
    {
        await _vm.Left.NavigateAsync(ArchivePath.Join(_zip, "docs"));
        _vm.Left.CurrentIndex = 1;

        await _vm.Left.OpenCurrentAsync();

        _launcher.Opened.Should().ContainSingle().Which.Should().EndWith("readme.txt").And.NotStartWith(_zip);
        File.ReadAllText(_launcher.Opened[0]).Should().Be("inside");
    }

    [Fact]
    public async Task Alt_F5_packs_into_the_other_panel_and_suggests_a_name()
    {
        var work = _tree.Dir("work"); File.WriteAllText(Path.Combine(work, "a.txt"), "a"); File.WriteAllText(Path.Combine(work, "b.txt"), "b");
        await _vm.Left.NavigateAsync(work);
        await _vm.Right.NavigateAsync(Path.Combine(_tree.Root, "out"));
        _vm.Left.MarkAll();
        _dialogs.PackAnswer = "stuff";

        await _vm.PackAsync();

        _dialogs.LastDefaultTarget.Should().Be(Path.Combine(_tree.Root, "out", "work.zip"), "a multi-item pack is named after the folder");
        var zip = Path.Combine(_tree.Root, "out", "stuff.zip");
        File.Exists(zip).Should().BeTrue("a bare name lands in the other panel with .zip");
        ArchiveCatalog.Open(zip).List(string.Empty).Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "b.txt"]);
        _vm.Left.MarkedItems.Should().BeEmpty("marks are cleared after a completed operation");
    }

    [Fact]
    public async Task Alt_F9_unpacks_the_marked_archives_into_the_other_panel_and_skips_other_files()
    {
        _tree.File("notes.txt", 3);
        await _vm.Left.NavigateAsync(_tree.Root);
        await _vm.Right.NavigateAsync(Path.Combine(_tree.Root, "out"));
        _vm.Left.MarkAll();
        _dialogs.UnpackAnswer = new UnpackAnswer(Path.Combine(_tree.Root, "out"), OwnFolders: true);

        await _vm.UnpackAsync();

        _dialogs.LastDefaultTarget.Should().Be(Path.Combine(_tree.Root, "out"));
        File.ReadAllText(Path.Combine(_tree.Root, "out", "pack", "docs", "readme.txt")).Should().Be("inside");
        Directory.EnumerateFileSystemEntries(Path.Combine(_tree.Root, "out")).Should().ContainSingle("only the archive is unpacked, not notes.txt");
    }

    [Fact]
    public async Task Alt_F9_on_a_plain_file_explains_instead_of_asking()
    {
        _tree.File("notes.txt", 3);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == "notes.txt");

        await _vm.UnpackAsync();

        _dialogs.Errors.Should().ContainSingle();
        _dialogs.LastDefaultTarget.Should().BeNull();
    }

    [Fact]
    public async Task Ctrl_PgDn_opens_a_zip_in_disguise_but_never_a_document_or_a_plain_file()
    {
        var docx = Path.Combine(_tree.Root, "report.docx");
        File.Copy(_zip, docx);
        var bundle = Path.Combine(_tree.Root, "tool.whl");
        File.Copy(_zip, bundle);
        _tree.File("plain.txt", 10);
        await _vm.Left.NavigateAsync(_tree.Root);

        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == "plain.txt");
        await _vm.Left.EnterCurrentDirectoryAsync();
        _vm.Left.Path.Should().Be(_tree.Root, "a file that is no archive does not open");
        _launcher.Opened.Should().BeEmpty("Ctrl+PgDn never launches");

        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == "report.docx");
        await _vm.Left.EnterCurrentDirectoryAsync();
        _vm.Left.Path.Should().Be(_tree.Root, "a document is not opened as an archive, even though a docx is a zip");
        _vm.Left.Status.Should().Contain("report.docx");

        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(static i => i.Name == "tool.whl");
        await _vm.Left.EnterCurrentDirectoryAsync();
        _vm.Left.Path.Should().Be(bundle);
        _vm.Left.IsInArchive.Should().BeTrue();
        _vm.Left.Items.Select(i => i.Name).Should().Equal("..", "docs");
        ArchivePath.ForgetExplicit(bundle);
    }
}
