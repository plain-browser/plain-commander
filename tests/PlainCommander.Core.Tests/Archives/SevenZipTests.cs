using System.Diagnostics;
using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Archives.SevenZip;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Archives;

/// <summary>
/// 7-Zip (7z.dll) on Windows: writing and changing 7z, passwords, other formats, opening any file with Ctrl+PgDn.
/// The engine-independent parts (Alt+F9, the pipe between 7-Zip and readers) run everywhere.
/// </summary>
public sealed class SevenZipTests : IDisposable
{
    private static readonly DateTime Stamp = new(2024, 3, 4, 5, 6, 8, DateTimeKind.Local);

    private readonly Sandbox _s = new();

    public void Dispose()
    {
        ArchivePasswords.Prompt = null;
        _s.Dispose();
    }

    private static void RequireSevenZip() =>
        Assert.SkipUnless(ArchiveReaders.SevenZipAvailable, "7z.dll is only loaded on Windows");

    /// <summary>A folder "tree" with a nested file, an empty folder and a Polish name, stamped with <see cref="Stamp"/>.</summary>
    private string MakeTree()
    {
        var tree = _s.Dir("tree");
        foreach (var (key, text) in new[] { ("a.txt", "alpha"), ("sub/zażółć.txt", "gęś"), ("sub/deep/big.bin", new string('x', 3 << 20)) })
        {
            var file = Path.Combine(tree, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            File.SetLastWriteTime(file, Stamp);
        }

        Directory.CreateDirectory(Path.Combine(tree, "empty"));
        return tree;
    }

    private async Task<string> PackAsync(string name, string? password = null)
    {
        var tree = MakeTree();
        var archive = _s.P(name);
        var result = await Run.Async(new ArchivePackOperation([tree], archive, password));
        result.Outcome.Should().Be(OperationOutcome.Completed);
        ArchiveCatalog.Forget(archive);
        return archive;
    }

    [Fact]
    public async Task Packs_a_7z_that_lists_and_extracts_with_names_dates_and_empty_folders()
    {
        RequireSevenZip();
        var archive = await PackAsync("out.7z");

        var catalog = ArchiveCatalog.Open(archive);
        catalog.Format.Should().Be("7z");
        catalog.FilesUnder(string.Empty).Select(n => n.Inner).Should().BeEquivalentTo(["tree/a.txt", "tree/sub/zażółć.txt", "tree/sub/deep/big.bin"]);
        catalog.DirectoryExists("tree/empty").Should().BeTrue();
        catalog.Find("tree/a.txt")!.Modified.ToLocalTime().DateTime.Should().BeCloseTo(Stamp, TimeSpan.FromSeconds(1));
        catalog.Find("tree/sub/deep/big.bin")!.Size.Should().Be(3 << 20);

        var target = _s.Dir("x");
        var result = await Run.Async(new ArchiveExtractOperation([ArchivePath.Join(archive, "tree")], new TransferTarget(target, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllText(Path.Combine(target, "tree", "sub", "zażółć.txt")).Should().Be("gęś");
        new FileInfo(Path.Combine(target, "tree", "sub", "deep", "big.bin")).Length.Should().Be(3 << 20);
        Directory.Exists(Path.Combine(target, "tree", "empty")).Should().BeTrue();
        File.GetLastWriteTime(Path.Combine(target, "tree", "a.txt")).Should().BeCloseTo(Stamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_password_hides_the_names_and_is_asked_again_when_wrong()
    {
        RequireSevenZip();
        var archive = await PackAsync("secret.7z", password: "tajne");
        ArchivePasswords.Forget(archive);
        var prompt = new ScriptedPassword("zle", "tajne");
        ArchivePasswords.Prompt = prompt;

        // Opening runs on a worker thread, as the panels do; the prompt blocks it until answered.
        var catalog = await Task.Run(() => ArchiveCatalog.Open(archive));

        catalog.FilesUnder(string.Empty).Should().HaveCount(3);
        prompt.Asked.Should().Equal(false, true);
        var temp = await Task.Run(() => ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(archive, "tree/a.txt")));
        File.ReadAllText(temp).Should().Be("alpha");
        prompt.Asked.Should().HaveCount(2, "the right password is remembered for the session");
    }

    [Fact]
    public async Task Cancelling_the_password_reports_an_encrypted_archive()
    {
        RequireSevenZip();
        var archive = await PackAsync("closed.7z", password: "x");
        ArchivePasswords.Forget(archive);
        ArchivePasswords.Prompt = new ScriptedPassword();

        var act = () => Task.Run(() => ArchiveCatalog.Open(archive));

        (await act.Should().ThrowAsync<ArchivePasswordException>()).Which.Wrong.Should().BeFalse();
    }

    [Fact]
    public async Task Changes_to_an_encrypted_7z_keep_it_encrypted()
    {
        RequireSevenZip();
        var archive = await PackAsync("enc.7z", password: "pw");
        var added = _s.File("new.txt", "new");

        var result = await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add([added], "tree")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        ArchivePasswords.Forget(archive);
        ArchiveCatalog.Forget(archive);
        ArchivePasswords.Prompt = new ScriptedPassword();
        var act = () => Task.Run(() => ArchiveCatalog.Open(archive));
        await act.Should().ThrowAsync<ArchivePasswordException>("the names are still hidden");

        ArchivePasswords.Remember(archive, "pw");
        var temp = await Task.Run(() => ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(archive, "tree/new.txt")));
        File.ReadAllText(temp).Should().Be("new", "new files are encrypted with the same password");
    }

    [Fact]
    public async Task A_zip_with_a_password_is_AES_encrypted_and_asks_for_it()
    {
        RequireSevenZip();
        var archive = await PackAsync("secret.zip", password: "haslo");
        ArchivePasswords.Forget(archive);
        var prompt = new ScriptedPassword("zle", "haslo");
        ArchivePasswords.Prompt = prompt;

        var catalog = await Task.Run(() => ArchiveCatalog.Open(archive));
        catalog.FilesUnder(string.Empty).Should().HaveCount(3, "zip names stay readable");
        var temp = await Task.Run(() => ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(archive, "tree/a.txt")));

        File.ReadAllText(temp).Should().Be("alpha");
        prompt.Asked.Should().NotBeEmpty("the content is encrypted");
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        var raw = () => new StreamReader(zip.GetEntry("tree/a.txt")!.Open()).ReadToEnd();
        raw.Should().Throw<Exception>("a plain zip reader cannot read AES entries");
    }

    [Fact]
    public async Task A_zip_without_a_password_stays_a_plain_zip()
    {
        var archive = await PackAsync("plain.zip");

        using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
        new StreamReader(zip.GetEntry("tree/a.txt")!.Open()).ReadToEnd().Should().Be("alpha");
    }

    [Fact]
    public async Task Adding_to_an_encrypted_zip_checks_the_password_and_encrypts_the_new_file()
    {
        RequireSevenZip();
        var archive = await PackAsync("box.zip", password: "pw");
        ArchivePasswords.Forget(archive);
        ArchiveCatalog.Forget(archive);
        var prompt = new ScriptedPassword("wrong", "pw");
        ArchivePasswords.Prompt = prompt;
        var added = _s.File("added.txt", "added");

        var result = await Run.Async(new ArchiveUpdateOperation(archive, ArchiveEdit.Add([added], string.Empty)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        prompt.Asked.Should().Equal(false, true);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
        {
            var raw = () => new StreamReader(zip.GetEntry("added.txt")!.Open()).ReadToEnd();
            raw.Should().Throw<Exception>("the new file is encrypted like the rest");
        }

        File.ReadAllText(await Task.Run(() => ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(archive, "added.txt")))).Should().Be("added");
    }

    [Theory]
    [InlineData(".7z")]
    [InlineData(".zip")]
    public async Task Encrypt_puts_each_item_into_its_own_archive_next_to_it(string extension)
    {
        RequireSevenZip();
        var file = _s.File("umowa.pdf", "pdf");
        var folder = _s.Dir("skany");
        File.WriteAllText(Path.Combine(folder, "1.png"), "png");

        var result = await Run.Async(new EncryptOperation([file, folder], extension, "tajne"));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(2);
        File.Exists(file + extension).Should().BeTrue();
        File.Exists(folder + extension).Should().BeTrue();
        File.Exists(file).Should().BeTrue("the originals stay until the user decides");
        ArchivePasswords.Forget(file + extension);
        ArchiveCatalog.Forget(file + extension);
        ArchivePasswords.Remember(file + extension, "tajne");
        File.ReadAllText(await Task.Run(() => ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(file + extension, "umowa.pdf")))).Should().Be("pdf");
    }

    [Fact]
    public async Task Reads_a_cabinet_made_by_makecab()
    {
        RequireSevenZip();
        var source = _s.File("note.txt", "cabinet content");
        var cab = _s.P("note.cab");
        using (var process = Process.Start(new ProcessStartInfo("makecab.exe", $"\"{source}\" \"{cab}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        ArchivePath.IsArchiveName(cab).Should().BeTrue();
        var catalog = ArchiveCatalog.Open(cab);
        catalog.Format.Should().Be("Cab");
        var temp = ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(cab, "note.txt"));
        File.ReadAllText(temp).Should().Be("cabinet content");
    }

    [Fact]
    public async Task Ctrl_PgDn_opens_any_file_that_holds_an_archive()
    {
        var zip = _s.P("tool.whl"); // a Python wheel is a zip
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            await using var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open());
            await writer.WriteAsync("<w/>");
        }

        ArchivePath.Split(zip, File.Exists).Should().BeNull("Enter does not go into an unknown extension");
        ArchivePath.OpenExplicitly(_s.File("report.docx", "x")).Should().BeFalse("documents are never opened as archives");
        ArchivePath.OpenExplicitly(zip).Should().BeTrue();
        try
        {
            ArchivePath.Split(ArchivePath.Join(zip, "word"), File.Exists).Should().Be(new ArchiveLocation(zip, "word"));
            ArchiveCatalog.Open(zip).List("word").Single().Name.Should().Be("document.xml");
        }
        finally
        {
            ArchivePath.ForgetExplicit(zip);
        }
    }

    [Fact]
    public void A_file_that_is_no_archive_says_so()
    {
        RequireSevenZip();
        var file = _s.File("plain.iso", "just text, no file system");

        var act = () => ArchiveCatalog.Open(file);

        act.Should().Throw<NotAnArchiveException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Alt_F9_unpacks_whole_archives_into_own_folders_or_straight_in(bool ownFolders)
    {
        var first = _s.P("first.zip");
        var second = _s.P("second.tar.gz");
        using (var zip = ZipFile.Open(first, ZipArchiveMode.Create))
        {
            await using var writer = new StreamWriter(zip.CreateEntry("one.txt").Open());
            await writer.WriteAsync("1");
        }

        await using (var stream = File.Create(second))
        using (var tar = SharpCompress.Writers.WriterFactory.Open(stream, SharpCompress.Common.ArchiveType.Tar, new SharpCompress.Writers.WriterOptions(SharpCompress.Common.CompressionType.GZip)))
        {
            using var content = new MemoryStream("2"u8.ToArray());
            tar.Write("dir/two.txt", content, DateTime.Now);
        }

        var target = _s.Dir("out");
        var result = await Run.Async(new ArchiveExtractOperation([first, second], new TransferTarget(target, null), ownFolders));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        if (ownFolders)
        {
            File.ReadAllText(Path.Combine(target, "first", "one.txt")).Should().Be("1");
            File.ReadAllText(Path.Combine(target, "second", "dir", "two.txt")).Should().Be("2", "a.tar.gz unpacks into \"a\"");
        }
        else
        {
            File.ReadAllText(Path.Combine(target, "one.txt")).Should().Be("1");
            File.ReadAllText(Path.Combine(target, "dir", "two.txt")).Should().Be("2");
        }
    }

    [Fact]
    public async Task Reads_and_extracts_a_zstd_tarball()
    {
        var path = _s.P("logs.tar.zst");
        using var plain = new MemoryStream();
        using (var tar = SharpCompress.Writers.WriterFactory.Open(plain, SharpCompress.Common.ArchiveType.Tar, new SharpCompress.Writers.WriterOptions(SharpCompress.Common.CompressionType.None) { LeaveStreamOpen = true }))
        {
            using var content = new MemoryStream("zstd!"u8.ToArray());
            tar.Write("var/log/app.log", content, DateTime.Now);
        }

        await using (var file = File.Create(path))
        await using (var zstd = new SharpCompress.Compressors.ZStandard.CompressionStream(file))
        {
            plain.Position = 0;
            await plain.CopyToAsync(zstd, TestContext.Current.CancellationToken);
        }

        ArchivePath.IsArchiveName(path).Should().BeTrue();
        ArchiveCatalog.Open(path).FilesUnder(string.Empty).Single().Inner.Should().Be("var/log/app.log");
        File.ReadAllText(ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(path, "var/log/app.log"))).Should().Be("zstd!");
    }

    [Fact]
    public void Folder_names_for_unpacking_drop_the_archive_extensions()
    {
        ArchiveExtractOperation.FolderNameFor(Path.Combine("x", "photos.tar.gz")).Should().Be("photos");
        ArchiveExtractOperation.FolderNameFor("setup.7z").Should().Be("setup");
        ArchiveExtractOperation.FolderNameFor(".zip").Should().Be(".zip");
    }

    [Fact]
    public async Task The_pipe_hands_over_large_entries_in_order_and_drops_abandoned_ones()
    {
        var first = new EntryPipe();
        var second = new EntryPipe();
        var data = Enumerable.Range(0, 10 << 20).Select(i => (byte)i).ToArray();

        var producer = Task.Run(() =>
        {
            for (var offset = 0; offset < data.Length; offset += 65536)
            {
                first.Write(data.AsSpan(offset, Math.Min(65536, data.Length - offset))).Should().BeTrue();
            }

            first.Complete(null);
            for (var i = 0; i < 200; i++)
            {
                second.Write(new byte[65536]).Should().BeTrue("an abandoned entry swallows the rest instead of blocking");
            }

            second.Complete(null);
        });

        using var copy = new MemoryStream();
        await new EntryPipeStream(first, data.Length).CopyToAsync(copy);
        second.Abandon();
        await producer.WaitAsync(TimeSpan.FromSeconds(30));

        copy.ToArray().Should().Equal(data);
    }

    [Fact]
    public async Task A_failed_entry_surfaces_its_error_to_the_reader()
    {
        var pipe = new EntryPipe();
        pipe.Write("part"u8).Should().BeTrue();
        pipe.Complete(new InvalidDataException("CRC"));

        var act = async () => await new StreamReader(new EntryPipeStream(pipe, 10)).ReadToEndAsync();

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Cancel_releases_a_producer_waiting_for_room()
    {
        var pipe = new EntryPipe();
        var blocked = Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                if (!pipe.Write(new byte[65536]))
                {
                    return true;
                }
            }

            return false;
        });

        await Task.Delay(200, TestContext.Current.CancellationToken);
        pipe.Cancel();

        (await blocked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Should().BeTrue("the producer is told to stop");
    }

    private sealed class ScriptedPassword(params string[] answers) : IArchivePasswordPrompt
    {
        private readonly Queue<string> _answers = new(answers);

        public List<bool> Asked { get; } = [];

        public Task<string?> AskArchivePasswordAsync(string archiveFile, bool wrong, CancellationToken cancellationToken)
        {
            Asked.Add(wrong);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
        }
    }
}
