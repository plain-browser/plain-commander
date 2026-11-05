using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using SharpCompress.Common;
using SharpCompress.Writers;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class ArchiveUpdateTests : IDisposable
{
    private readonly Sandbox _s = new();
    private readonly List<string> _archives = [];

    public void Dispose()
    {
        foreach (var a in _archives)
        {
            ArchiveCatalog.Forget(a);
        }

        _s.Dispose();
    }

    private static readonly DateTime Stamp = new(2025, 6, 7, 8, 9, 10, DateTimeKind.Local);

    /// <summary>docs/readme.txt, docs/deep/notes.md, top.txt (and the folders) in a zip, tar.gz or 7z.</summary>
    private string MakeArchive(string name)
    {
        var path = _s.P(name);
        _archives.Add(path);
        if (name.EndsWith(".7z", StringComparison.Ordinal))
        {
            var stage = _s.Dir("stage-" + name);
            foreach (var (key, text) in new[] { ("docs/readme.txt", "readme"), ("docs/deep/notes.md", "notes"), ("top.txt", "top") })
            {
                var file = Path.Combine(stage, key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, text);
                File.SetLastWriteTime(file, Stamp);
            }

            var sources = new[] { Path.Combine(stage, "docs"), Path.Combine(stage, "top.txt") };
            Run.Async(new ArchivePackOperation(sources, path)).GetAwaiter().GetResult().Outcome.Should().Be(OperationOutcome.Completed);
            return path;
        }

        var type = name.EndsWith(".zip", StringComparison.Ordinal) ? ArchiveType.Zip : ArchiveType.Tar;
        var compression = type == ArchiveType.Zip ? CompressionType.Deflate : CompressionType.GZip;
        using var stream = File.Create(path);
        using var writer = WriterFactory.Open(stream, type, new WriterOptions(compression));
        void Put(string key, string text)
        {
            using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
            writer.Write(key, content, Stamp);
        }

        Put("docs/readme.txt", "readme");
        Put("docs/deep/notes.md", "notes");
        Put("top.txt", "top");
        return path;
    }

    private static string[] Keys(string archive)
    {
        ArchiveCatalog.Forget(archive);
        var catalog = ArchiveCatalog.Open(archive);
        return catalog.FilesUnder(string.Empty).Select(n => n.Inner).Order(StringComparer.Ordinal).ToArray();
    }

    private static string Content(string archive, string key)
    {
        var temp = ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(archive, key));
        return File.ReadAllText(temp);
    }

    /// <summary>7z joins in where 7-Zip is there (Windows).</summary>
    public static TheoryData<string> Formats => ArchiveReaders.SevenZipAvailable ? ["pack.zip", "pack.tar.gz", "pack.7z"] : ["pack.zip", "pack.tar.gz"];

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Deletes_a_file_and_a_whole_folder(string name)
    {
        var archive = MakeArchive(name);

        var result = await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Remove(["top.txt", "docs/deep"])));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Keys(archive).Should().Equal("docs/readme.txt");
        File.Exists(archive + ".part").Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Adds_files_and_folders_into_a_subfolder_and_keeps_old_entries_and_dates(string name)
    {
        var archive = MakeArchive(name);
        var file = _s.File("in/new.txt", "fresh");
        _s.File("in/dir/inner.txt", "inner");

        await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add([file, _s.P("in", "dir")], "docs")));

        Keys(archive).Should().Equal("docs/deep/notes.md", "docs/dir/inner.txt", "docs/new.txt", "docs/readme.txt", "top.txt");
        Content(archive, "docs/new.txt").Should().Be("fresh");
        Content(archive, "docs/readme.txt").Should().Be("readme");
        ArchiveCatalog.Open(archive).Find("top.txt")!.Modified.ToLocalTime().DateTime.Should().BeCloseTo(Stamp, TimeSpan.FromSeconds(2));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Existing_names_go_through_the_conflict_prompt(string name)
    {
        var archive = MakeArchive(name);
        var replacement = _s.File("in/top.txt", "replacement");

        var skip = new ScriptedPrompts([ConflictChoice.Skip]);
        await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add([replacement], string.Empty)), skip);
        Content(archive, "top.txt").Should().Be("top");

        await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add([replacement], string.Empty)), new ScriptedPrompts([ConflictChoice.Overwrite]));
        Content(archive, "top.txt").Should().Be("replacement");
        Keys(archive).Count(k => k == "top.txt").Should().Be(1, "the old entry is replaced, not duplicated");

        await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add([replacement], string.Empty)), new ScriptedPrompts([ConflictChoice.KeepBoth]));
        Keys(archive).Should().Contain(["top.txt", "top (2).txt"]);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Renames_a_file_and_a_folder_with_its_content(string name)
    {
        var archive = MakeArchive(name);

        await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.RenameEntry("top.txt", "first.txt")));
        await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.RenameEntry("docs", "documents")));

        Keys(archive).Should().Equal("documents/deep/notes.md", "documents/readme.txt", "first.txt");
        Content(archive, "documents/deep/notes.md").Should().Be("notes");
    }

    [Fact]
    public async Task Renaming_onto_an_existing_name_is_refused_and_changes_nothing()
    {
        var archive = MakeArchive("pack.zip");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        var result = await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.RenameEntry("top.txt", "docs")), prompts);

        result.Outcome.Should().NotBe(OperationOutcome.Completed);
        Keys(archive).Should().Contain("top.txt");
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Creates_a_folder(string name)
    {
        var archive = MakeArchive(name);

        await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.MakeFolder("docs/new folder")));

        ArchiveCatalog.Forget(archive);
        ArchiveCatalog.Open(archive).DirectoryExists("docs/new folder").Should().BeTrue();
    }

    [Fact]
    public async Task Move_into_an_archive_removes_the_sources_afterwards()
    {
        var archive = MakeArchive("pack.zip");
        var file = _s.File("in/moved.txt", "m");

        var result = await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add([file], string.Empty), moveSources: true));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.Exists(file).Should().BeFalse();
        Content(archive, "moved.txt").Should().Be("m");
    }

    [Fact]
    public async Task Read_only_formats_are_refused()
    {
        var path = _s.File("old.rar", "not really rar");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        ArchiveUpdateOperation.CanEdit(path).Should().BeFalse();
        var result = await Run.Async(new ArchiveUpdateOperation(path, ArchiveEdit.Remove(["x"])), prompts);

        result.Outcome.Should().NotBe(OperationOutcome.Completed);
        File.ReadAllText(path).Should().Be("not really rar");
    }
}
