using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Archives;

public sealed class ArchiveTests : IDisposable
{
    private readonly Sandbox _s = new();
    private readonly string _zip;

    public ArchiveTests()
    {
        _zip = _s.P("pack.zip");
        using var archive = ZipFile.Open(_zip, ZipArchiveMode.Create);
        Write(archive, "readme.txt", "hello");
        Write(archive, "docs/guide.md", "# guide");
        Write(archive, "docs/img/logo.png", "PNG");
        Write(archive, "src/main.cs", "class A {}");
        archive.CreateEntry("empty/"); // a folder with nothing in it
    }

    private static void Write(ZipArchive archive, string key, string content)
    {
        var entry = archive.CreateEntry(key);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    public void Dispose() => _s.Dispose();

    private string V(string inner) => ArchivePath.Join(_zip, inner);

    [Fact]
    public void Path_splits_at_the_first_existing_archive_file()
    {
        ArchivePath.Split(V("docs/img"), File.Exists).Should().Be(new ArchiveLocation(_zip, "docs/img"));
        ArchivePath.Split(_zip, File.Exists)!.IsRoot.Should().BeTrue();
        ArchivePath.Split(_s.Root, File.Exists).Should().BeNull("a plain folder");
        ArchivePath.Split(_s.P("missing.zip", "x"), File.Exists).Should().BeNull("the archive file does not exist");
        ArchivePath.IsArchiveName("a.7Z").Should().BeTrue();
        ArchivePath.NormalizeKey("./docs\\img/").Should().Be("docs/img");
    }

    [Fact]
    public void Catalog_lists_folders_including_implicit_ones()
    {
        var catalog = ArchiveCatalog.Open(_zip);

        catalog.List(string.Empty).Select(e => e.Name).Should().BeEquivalentTo(["readme.txt", "docs", "src", "empty"]);
        catalog.DirectoryExists("empty").Should().BeTrue();
        catalog.List("docs").Select(e => (e.Name, e.IsDirectory)).Should().BeEquivalentTo([("guide.md", false), ("img", true)]);
        catalog.List("docs/img").Single().FullPath.Should().Be(V("docs/img/logo.png"));
        catalog.DirectoryExists("docs/img").Should().BeTrue();
        catalog.DirectoryExists("docs/guide.md").Should().BeFalse();
        catalog.FilesUnder("docs").Select(n => n.Inner).Should().BeEquivalentTo(["docs/guide.md", "docs/img/logo.png"]);
        catalog.FilesUnder("readme.txt").Should().ContainSingle();
        ReferenceEquals(ArchiveCatalog.Open(_zip), catalog).Should().BeTrue("unchanged archives come from the cache");
    }

    [Fact]
    public async Task File_system_browses_into_the_archive_and_back_out()
    {
        var fs = new ArchiveFileSystem(new LocalFileSystem());

        var root = await fs.ListAsync(_zip, includeHidden: false).ToListAsync();
        root.Select(e => e.Name).Should().Equal("..", "readme.txt", "docs", "src", "empty");
        root[0].FullPath.Should().Be(_s.Root, "'..' of the archive root is the folder holding the archive");

        var docs = await fs.ListAsync(V("docs"), includeHidden: false).ToListAsync();
        docs.Select(e => e.Name).Should().Equal("..", "guide.md", "img");
        docs[0].FullPath.Should().Be(_zip);

        fs.DirectoryExists(V("docs/img")).Should().BeTrue();
        fs.DirectoryExists(V("docs/nope")).Should().BeFalse();
        fs.GetParent(V("docs/img")).Should().Be(V("docs"));
        fs.Watch(V("docs"), () => { }).Should().BeNull();
        fs.DirectoryExists(_s.Root).Should().BeTrue("disk paths still go to the disk");
    }

    [Fact]
    public async Task Extracts_a_folder_tree_and_single_files_with_conflicts_handled()
    {
        var target = _s.Dir("out");
        var op = new ArchiveExtractOperation([V("docs"), V("readme.txt"), V("empty")], new TransferTarget(target, null));

        var result = await Run.Async(op);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(6, "3 files, docs, docs/img and the empty folder");
        Directory.Exists(Path.Combine(target, "empty")).Should().BeTrue("empty folders are recreated");
        _s.Read(Path.Combine("out", "docs", "guide.md")).Should().Be("# guide");
        _s.Read(Path.Combine("out", "docs", "img", "logo.png")).Should().Be("PNG");
        _s.Read(Path.Combine("out", "readme.txt")).Should().Be("hello");

        // second run: readme exists → keep both, guide exists → skip all
        var again = new ArchiveExtractOperation([V("readme.txt"), V("docs")], new TransferTarget(target, null));
        var second = await Run.Async(again, new ScriptedPrompts([ConflictChoice.KeepBoth, ConflictChoice.SkipAll]));

        second.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        _s.Read(Path.Combine("out", "readme (2).txt")).Should().Be("hello");
        second.Skipped.Should().Be(2);
    }

    [Fact]
    public async Task A_new_name_renames_the_extracted_file()
    {
        var target = _s.Dir("out2");
        await Run.Async(new ArchiveExtractOperation([V("src/main.cs")], new TransferTarget(target, "program.cs")));

        _s.Read(Path.Combine("out2", "program.cs")).Should().Be("class A {}");
    }

    [Fact]
    public void Extract_to_temp_gives_a_real_file_for_the_viewer()
    {
        var temp = ArchiveExtractOperation.ExtractToTemp(V("docs/guide.md"));
        try
        {
            Path.GetFileName(temp).Should().Be("guide.md");
            File.ReadAllText(temp).Should().Be("# guide");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
        }
    }

    [Fact]
    public async Task Packs_folders_and_files_into_a_zip_that_the_catalog_reads_back()
    {
        var proj = _s.Dir("proj"); _s.File(Path.Combine("proj", "a.txt"), "A"); _s.Dir("proj", "sub"); _s.File(Path.Combine("proj", "sub", "b.txt"), "BB"); _s.Dir("proj", "empty");
        var note = _s.File("note.md", "note");
        var archive = _s.P("out", "bundle");

        var result = await Run.Async(new ArchivePackOperation([proj, note], ArchivePackOperation.WithDefaultExtension(archive)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.Exists(archive + ".zip").Should().BeTrue();
        File.Exists(archive + ".zip.part").Should().BeFalse();
        var catalog = ArchiveCatalog.Open(archive + ".zip");
        catalog.List(string.Empty).Select(e => e.Name).Should().BeEquivalentTo(["proj", "note.md"]);
        catalog.List("proj").Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "sub", "empty"]);
        catalog.Find("proj/sub/b.txt")!.Size.Should().Be(2);
        catalog.DirectoryExists("proj/empty").Should().BeTrue("empty folders are written as directory entries");

        // packing again onto the existing archive: keep both → a free name
        var second = await Run.Async(new ArchivePackOperation([note], archive + ".zip"), new ScriptedPrompts([ConflictChoice.KeepBoth]));
        second.Outcome.Should().Be(OperationOutcome.Completed);
        File.Exists(_s.P("out", "bundle (2).zip")).Should().BeTrue();
        ArchiveCatalog.Open(_s.P("out", "bundle (2).zip")).List(string.Empty).Select(e => e.Name).Should().Equal("note.md");
    }

    [Fact]
    public async Task Tar_gz_by_name_and_unknown_extensions_are_refused()
    {
        var note = _s.File("n.txt", "n");
        var tgz = _s.P("n.tar.gz");

        (await Run.Async(new ArchivePackOperation([note], tgz))).Outcome.Should().Be(OperationOutcome.Completed);
        ArchiveCatalog.Open(tgz).List(string.Empty).Single().Name.Should().Be("n.txt");
        var outDir = _s.Dir("tgz-out");
        (await Run.Async(new ArchiveExtractOperation([ArchivePath.Join(tgz, "n.txt")], new TransferTarget(outDir, null)))).Done.Should().Be(1);
        _s.Read(Path.Combine("tgz-out", "n.txt")).Should().Be("n");
        File.ReadAllText(ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(tgz, "n.txt"))).Should().Be("n");

        ArchivePackOperation.FormatOf("x.7z").Should().BeNull();
        ArchivePackOperation.WithDefaultExtension("x").Should().Be("x.zip");
        var bad = () => Run.Async(new ArchivePackOperation([note], _s.P("x.arj")));
        (await bad.Should().ThrowAsync<OperationRuleException>()).Which.Violation.Should().Be(RuleViolation.UnsupportedArchive);
    }
}
