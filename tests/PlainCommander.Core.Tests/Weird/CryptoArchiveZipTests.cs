using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>
/// Zip and tar(.gz) through the managed reader and writer (no 7z.dll): hostile entry names, duplicates, odd folder
/// entries, empty archives, updates with weird names and extraction over read-only files.
/// </summary>
public sealed class CryptoArchiveZipTests : IDisposable
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

    private static void SkipWith7Zip() =>
        Assert.SkipWhen(ArchiveReaders.SevenZipAvailable, "These cover the managed reader; 7-Zip reads archives when the library is there.");

    private string Zip(string name, params (string Key, string? Content)[] entries)
    {
        var path = _s.P(name);
        _archives.Add(path);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (key, content) in entries)
        {
            var entry = archive.CreateEntry(key);
            if (content is not null)
            {
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return path;
    }

    private string Tar(string name, params (string Key, TarEntryType Type, string? Content)[] entries)
    {
        var path = _s.P(name);
        _archives.Add(path);
        using var file = File.Create(path);
        using Stream outer = name.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionLevel.Fastest) : file;
        using var writer = new TarWriter(outer, TarEntryFormat.Gnu, leaveOpen: true);
        foreach (var (key, type, content) in entries)
        {
            var entry = new GnuTarEntry(type, key);
            if (content is not null)
            {
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            }

            writer.WriteEntry(entry);
        }

        return path;
    }

    private static string V(string archive, string inner) => ArchivePath.Join(archive, inner);

    private static List<string> FilesUnder(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

    // ---------- hostile names ----------

    /// <summary>
    /// BUG (zip-slip): ArchiveExtractOperation joins the entry key to the target with Path.Combine and never checks
    /// that the result stays inside it; ArchivePath.NormalizeKey keeps "..". Extracting a downloaded zip writes
    /// "../evil.txt" next to the extraction folder, and "../../../x" anywhere the user can write. A .tar.gz with a
    /// "../" entry escapes the same way (checked by hand).
    /// </summary>
    [Fact]
    public async Task Regression_zip_slip_entries_are_written_outside_the_extraction_folder()
    {
        SkipWith7Zip();
        var zip = Zip("slip.zip", ("good.txt", "good"), ("../evil.txt", "evil"), ("sub/../../evil2.txt", "evil2"));
        var target = _s.Dir("out");

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.SkipAll]);
        await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null)), prompts);

        _s.Read(Path.Combine("out", "slip", "good.txt")).Should().Be("good");
        File.Exists(Path.Combine(target, "evil.txt")).Should().BeFalse("an entry must never land outside out/slip");
        File.Exists(Path.Combine(target, "evil2.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Absolute_and_dot_slash_entry_names_are_made_relative()
    {
        SkipWith7Zip();
        var zip = Zip("abs.zip", ("/abs.txt", "abs"), ("./dot.txt", "dot"), ("\\back\\slash.txt", "bs"));
        var target = _s.Dir("out");

        var result = await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        FilesUnder(Path.Combine(target, "abs")).Should().Equal("abs.txt", "back/slash.txt", "dot.txt");
    }

    [Fact]
    public async Task Tar_links_are_written_as_plain_files_never_as_links()
    {
        SkipWith7Zip();
        var outside = _s.Dir("outside");
        var path = _s.P("links.tar");
        _archives.Add(path);
        using (var file = File.Create(path))
        using (var writer = new TarWriter(file, TarEntryFormat.Gnu))
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = outside });
            writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "link/through.txt") { DataStream = new MemoryStream("x"u8.ToArray()) });
        }

        var target = _s.Dir("out");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.SkipAll]);
        await Run.Async(new ArchiveExtractOperation([path], new TransferTarget(target, null)), prompts);

        File.Exists(Path.Combine(outside, "through.txt")).Should().BeFalse("nothing is written through a link from the archive");
        Directory.GetFileSystemEntries(_s.Root, "*", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p)).Where(i => i.LinkTarget is not null).Should().BeEmpty("no link is ever created");
    }

    // ---------- duplicates and case ----------

    [Fact]
    public async Task Duplicate_entry_names_show_once_and_extract_to_one_file()
    {
        SkipWith7Zip();
        var zip = Zip("dup.zip", ("a.txt", "first"), ("a.txt", "second"));
        ArchiveCatalog.Open(zip).List(string.Empty).Should().ContainSingle();
        var target = _s.Dir("out");

        // The second copy meets the file the first one just wrote: it is asked about like any conflict.
        var prompts = new ScriptedPrompts([ConflictChoice.Overwrite]);
        var result = await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null)), prompts);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        FilesUnder(Path.Combine(target, "dup")).Should().Equal("a.txt");
        _s.Read(Path.Combine("out", "dup", "a.txt")).Should().Be("second");
    }

    [Fact]
    public void Entries_differing_only_in_case_merge_in_the_case_insensitive_catalog()
    {
        SkipWith7Zip();
        var zip = Zip("case.zip", ("Readme.TXT", "upper"), ("readme.txt", "lower"));
        var catalog = ArchiveCatalog.Open(zip);
        catalog.List(string.Empty).Should().ContainSingle("Windows cannot hold both names in one folder");
        catalog.Find("README.txt").Should().NotBeNull();
    }

    [Fact]
    public void A_file_and_a_folder_with_the_same_name_do_not_crash_the_catalog()
    {
        SkipWith7Zip();
        var zip = Zip("clash.zip", ("x", "file"), ("x/inner.txt", "inner"));
        var act = () => ArchiveCatalog.Open(zip).List(string.Empty).ToList();
        act.Should().NotThrow();
    }

    // ---------- folder entries and empty archives ----------

    [Fact]
    public async Task Tar_folder_entries_without_trailing_slash_are_folders()
    {
        SkipWith7Zip();
        var tar = Tar("dirs.tar", ("emptydir", TarEntryType.Directory, null), ("full/f.txt", TarEntryType.RegularFile, "f"));
        var catalog = ArchiveCatalog.Open(tar);
        catalog.DirectoryExists("emptydir").Should().BeTrue();
        var target = _s.Dir("out");

        var result = await Run.Async(new ArchiveExtractOperation([tar], new TransferTarget(target, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(Path.Combine(target, "dirs", "emptydir")).Should().BeTrue();
        _s.Read(Path.Combine("out", "dirs", "full", "f.txt")).Should().Be("f");
    }

    [Fact]
    public async Task An_empty_zip_extracts_to_an_empty_folder()
    {
        SkipWith7Zip();
        var zip = Zip("empty.zip");
        ArchiveCatalog.Open(zip).Count.Should().Be(0);
        var target = _s.Dir("out");

        var result = await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.GetFileSystemEntries(Path.Combine(target, "empty")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_zero_byte_or_garbage_archive_is_reported_and_extracts_nothing()
    {
        SkipWith7Zip();
        var zero = _s.File("zero.zip", string.Empty);
        var garbage = _s.File("garbage.tar.gz", "not gzip at all");
        _archives.AddRange([zero, garbage]);
        var target = _s.Dir("out");

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip, ErrorChoice.Skip]);
        var result = await Run.Async(new ArchiveExtractOperation([zero, garbage], new TransferTarget(target, null)), prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors.Should().HaveCount(2);
        Directory.GetFileSystemEntries(target).Should().BeEmpty();
    }

    // ---------- updates ----------

    [Theory]
    [InlineData("w.zip")]
    [InlineData("w.tar")]
    [InlineData("w.tar.gz")]
    public async Task Updates_with_weird_names_add_rename_and_delete_cleanly(string name)
    {
        SkipWith7Zip();
        var archive = name.EndsWith(".zip", StringComparison.Ordinal)
            ? Zip(name, ("docs/a.txt", "a"), ("docs2/b.txt", "b"))
            : Tar(name, ("docs/a.txt", TarEntryType.RegularFile, "a"), ("docs2/b.txt", TarEntryType.RegularFile, "b"));
        var weird = new[] { "zażółć 日本 🙂.txt", new string('l', 150) + ".txt", "#%&{}[]'!@$^ .txt", "-dash.txt" };
        var sources = weird.Select(w => _s.File(Path.Combine("src", w), w)).ToList();

        (await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add(sources, "docs")))).Outcome.Should().Be(OperationOutcome.Completed);
        (await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.RenameEntry("docs/a.txt", "Ä ñ.txt")))).Outcome.Should().Be(OperationOutcome.Completed);
        (await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Remove(["docs2"])))).Outcome.Should().Be(OperationOutcome.Completed);
        (await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Remove(["doc"])))).Outcome.Should().Be(OperationOutcome.Completed);

        ArchiveCatalog.Forget(archive);
        var catalog = ArchiveCatalog.Open(archive);
        catalog.FilesUnder(string.Empty).Select(n => n.Inner).Order(StringComparer.Ordinal).Should()
            .Equal(weird.Select(w => "docs/" + w).Append("docs/Ä ñ.txt").Order(StringComparer.Ordinal), "\"doc\" matches nothing and \"docs2\" leaves \"docs\" alone");
        File.Exists(archive + ".part").Should().BeFalse();

        var target = _s.Dir("out-" + name);
        (await Run.Async(new ArchiveExtractOperation([archive], new TransferTarget(target, null)))).Outcome.Should().Be(OperationOutcome.Completed);
        foreach (var w in weird)
        {
            File.ReadAllText(Path.Combine(target, ArchiveExtractOperation.FolderNameFor(archive), "docs", w)).Should().Be(w);
        }
    }

    [Fact]
    public async Task Renaming_an_entry_only_by_case_works()
    {
        SkipWith7Zip();
        var zip = Zip("case-rename.zip", ("readme.txt", "r"));
        var result = await Run.Async(new ArchiveUpdateOperation(zip, ArchiveEdit.RenameEntry("readme.txt", "README.txt")));
        result.Outcome.Should().Be(OperationOutcome.Completed);
        ArchiveCatalog.Forget(zip);
        ArchiveCatalog.Open(zip).List(string.Empty).Single().Name.Should().Be("README.txt");
    }

    [Fact]
    public async Task Updating_an_archive_that_holds_dot_dot_entries_keeps_them_verbatim()
    {
        SkipWith7Zip();
        var zip = Zip("keep.zip", ("../up.txt", "up"), ("ok.txt", "ok"));
        var result = await Run.Async(new ArchiveUpdateOperation(zip, ArchiveEdit.Remove(["ok.txt"])));
        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.Exists(_s.P("up.txt")).Should().BeFalse("rewriting never extracts");
        using var check = ZipFile.OpenRead(zip);
        check.Entries.Select(e => e.FullName).Should().Equal("../up.txt");
    }

    // ---------- extraction over existing files ----------

    [Fact]
    public async Task Extracting_over_a_read_only_file_overwrites_it_on_request_and_keeps_both_otherwise()
    {
        SkipWith7Zip();
        var zip = Zip("ro.zip", ("ro.txt", "new"));
        var target = _s.Dir("out");
        var existing = _s.File(Path.Combine("out", "ro.txt"), "old");
        File.SetAttributes(existing, FileAttributes.ReadOnly);

        var both = await Run.Async(new ArchiveExtractOperation([V(zip, "ro.txt")], new TransferTarget(target, null)), new ScriptedPrompts([ConflictChoice.KeepBoth]));
        both.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read(Path.Combine("out", "ro.txt")).Should().Be("old");
        _s.Read(Path.Combine("out", "ro (2).txt")).Should().Be("new");

        var over = await Run.Async(new ArchiveExtractOperation([V(zip, "ro.txt")], new TransferTarget(target, null)), new ScriptedPrompts([ConflictChoice.Overwrite]));
        over.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read(Path.Combine("out", "ro.txt")).Should().Be("new");
    }

    [Fact]
    public async Task Extracting_a_file_where_a_folder_has_its_name_is_refused()
    {
        SkipWith7Zip();
        var zip = Zip("tm.zip", ("thing", "file"));
        var target = _s.Dir("out");
        _s.Dir("out", "thing");
        _s.File(Path.Combine("out", "thing", "keep.txt"), "keep");

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new ArchiveExtractOperation([V(zip, "thing")], new TransferTarget(target, null)), prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        _s.Read(Path.Combine("out", "thing", "keep.txt")).Should().Be("keep");
    }
}
