using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Tests.Operations;
using SharpCompress.Archives.Tar;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>
/// <see cref="PceFile"/> over both layouts: what the app writes now is a container, files from earlier versions
/// are read as before and can be rewritten as containers.
/// </summary>
public sealed class PceFileLayoutTests : IDisposable
{
    private const string Password = "tajne";
    private static readonly PceOptions Fast = PceOptions.Fast;
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        new Random(count).NextBytes(data);
        return data;
    }

    private string SourceFile(string name, byte[] content)
    {
        var path = _s.P(name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, Stamp);
        return path;
    }

    private string SourceFolder(string name = "docs")
    {
        var folder = _s.Dir(name);
        Directory.CreateDirectory(Path.Combine(folder, "sub", "pusty"));
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), Bytes(200_000));
        File.WriteAllText(Path.Combine(folder, "sub", "zażółć.txt"), string.Concat(Enumerable.Repeat("tekst ", 5000)));
        File.SetLastWriteTimeUtc(Path.Combine(folder, "a.bin"), Stamp);
        return folder;
    }

    private static void ShouldMatch(string folder, string expected)
    {
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(folder, p)).Order()
            .Should().Equal(Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(expected, p)).Order());
        foreach (var file in Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories))
        {
            File.ReadAllBytes(Path.Combine(folder, Path.GetRelativePath(expected, file))).Should().Equal(File.ReadAllBytes(file));
        }
    }

    [Fact]
    public void A_file_is_written_as_a_container_and_comes_back_with_its_name_time_and_content()
    {
        var content = Bytes(150_000);
        var source = SourceFile("umowa.pdf", content);
        var pce = _s.P("x7k2.pce");

        PceFile.Encrypt(source, pce, Password, Fast, PceCompression.Auto, null, Token);

        PceContainer.LayoutVersion(pce).Should().Be(2);
        var (header, metadata) = PceFile.Peek(pce, Password);
        header.IsFolder.Should().BeFalse();
        metadata.Name.Should().Be("umowa.pdf", "the name comes from inside, not from the .pce file");
        metadata.Size.Should().Be(content.Length);
        metadata.ModifiedUtc.Should().Be(Stamp);
        var back = _s.P("back.pdf");
        long reported = 0;
        PceFile.Decrypt(pce, Password, back, total => reported = total, Token);
        File.ReadAllBytes(back).Should().Equal(content);
        File.GetLastWriteTimeUtc(back).Should().Be(Stamp);
        reported.Should().Be(content.Length);
        var wrong = () => PceFile.Peek(pce, "inne");
        wrong.Should().Throw<PceWrongPasswordException>();
    }

    [Fact]
    public void A_folder_is_written_as_entries_under_its_name_and_comes_back_as_its_content()
    {
        var folder = SourceFolder();
        var pce = folder + ".pce";

        PceFile.Encrypt(folder, pce, Password, Fast, PceCompression.Auto, null, Token);

        var (header, metadata) = PceFile.Peek(pce, Password);
        header.IsFolder.Should().BeTrue();
        header.IsCompressed.Should().BeTrue("the text file compresses");
        metadata.Name.Should().Be("docs");
        using (var container = PceContainer.Open(pce, Password))
        {
            container.Entries.Select(e => e.Path).Should().BeEquivalentTo("docs/a.bin", "docs/sub/zażółć.txt", "docs/sub/pusty");
            container.Find("docs/a.bin")!.Compressed.Should().BeFalse("compression is decided per file");
            container.Find("docs/sub/zażółć.txt")!.Compressed.Should().BeTrue();
        }

        var back = _s.P("out", "wynik");
        PceFile.Decrypt(pce, Password, back, null, Token);
        ShouldMatch(back, folder);
        File.GetLastWriteTimeUtc(Path.Combine(back, "a.bin")).Should().Be(Stamp);
    }

    [Fact]
    public void An_empty_folder_keeps_its_name()
    {
        var folder = _s.Dir("pusty");
        var pce = folder + ".pce";

        PceFile.Encrypt(folder, pce, Password, Fast, PceCompression.None, null, Token);

        PceFile.Peek(pce, Password).Metadata.Name.Should().Be("pusty");
        PceFile.Peek(pce, Password).Header.IsFolder.Should().BeTrue();
        var back = _s.P("back");
        PceFile.Decrypt(pce, Password, back, null, Token);
        Directory.Exists(back).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(back).Should().BeEmpty();
    }

    [Fact]
    public void A_container_with_several_top_level_names_is_a_folder_named_after_the_file()
    {
        var pce = _s.P("paczka.pce");
        using (var container = PceContainer.Create(pce, Password, Fast))
        {
            container.Add(PceSource.FromPath(SourceFile("a.txt", Bytes(10))).Concat(PceSource.FromPath(SourceFolder())));
        }

        var (header, metadata) = PceFile.Peek(pce, Password);
        header.IsFolder.Should().BeTrue();
        metadata.Name.Should().Be("paczka");
        var back = _s.P("back");
        PceFile.Decrypt(pce, Password, back, null, Token);
        File.ReadAllBytes(Path.Combine(back, "a.txt")).Should().Equal(Bytes(10));
        ShouldMatch(Path.Combine(back, "docs"), _s.P("docs"));
    }

    [Fact]
    public void The_browsing_tar_of_a_container_holds_the_same_entries_the_first_layout_gave()
    {
        var folder = SourceFolder();
        var file = SourceFile("sam.txt", Bytes(3000));
        PceFile.Encrypt(folder, folder + ".pce", Password, Fast, PceCompression.Auto, null, Token);
        PceFile.Encrypt(file, file + ".pce", Password, Fast, PceCompression.None, null, Token);

        using (var tar = new MemoryStream())
        {
            long reported = 0;
            PceFile.CopyContentAsTar(folder + ".pce", Password, tar, total => reported = total, Token);
            tar.Position = 0;
            using var archive = TarArchive.Open(tar);
            var entries = archive.Entries.ToDictionary(e => e.Key!.TrimEnd('/'), e => e);
            entries.Keys.Should().BeEquivalentTo("a.bin", "sub/zażółć.txt", "sub/pusty");
            entries["sub/pusty"].IsDirectory.Should().BeTrue();
            using var content = new MemoryStream();
            entries["sub/zażółć.txt"].OpenEntryStream().CopyTo(content);
            content.ToArray().Should().Equal(File.ReadAllBytes(Path.Combine(folder, "sub", "zażółć.txt")), "a compressed entry goes into the TAR decompressed");
            reported.Should().Be(200_000 + content.Length);
        }

        using (var tar = new MemoryStream())
        {
            PceFile.CopyContentAsTar(file + ".pce", Password, tar, null, Token);
            tar.Position = 0;
            using var archive = TarArchive.Open(tar);
            archive.Entries.Should().ContainSingle().Which.Key.Should().Be("sam.txt");
        }
    }

    [Theory]
    [InlineData(PceCompression.None)]
    [InlineData(PceCompression.Always)]
    public void A_file_of_the_first_layout_is_rewritten_as_a_container_with_the_same_content(PceCompression compression)
    {
        var folder = SourceFolder();
        var file = SourceFile("sam.txt", Bytes(3000));
        var empty = _s.Dir("nic");
        foreach (var source in new[] { folder, file, empty })
        {
            PceFile.EncryptLegacy(source, source + ".pce", Password, Fast, compression, null, Token);
            PceContainer.LayoutVersion(source + ".pce").Should().Be(1);
        }

        PceFile.Upgrade(folder + ".pce", Password, null, Token).Should().BeTrue();
        PceFile.Upgrade(file + ".pce", Password, null, Token).Should().BeTrue();
        PceFile.Upgrade(empty + ".pce", Password, null, Token).Should().BeTrue();
        PceFile.Upgrade(file + ".pce", Password, null, Token).Should().BeFalse("it is a container already");

        PceContainer.LayoutVersion(folder + ".pce").Should().Be(2);
        PceFile.Peek(folder + ".pce", Password).Metadata.Name.Should().Be("docs");
        PceFile.Decrypt(folder + ".pce", Password, _s.P("back"), null, Token);
        ShouldMatch(_s.P("back"), folder);
        var (header, metadata) = PceFile.Peek(file + ".pce", Password);
        header.IsFolder.Should().BeFalse();
        metadata.Should().BeEquivalentTo(new { Name = "sam.txt", Size = 3000L, ModifiedUtc = Stamp });
        PceFile.Decrypt(file + ".pce", Password, _s.P("sam.back"), null, Token);
        File.ReadAllBytes(_s.P("sam.back")).Should().Equal(Bytes(3000));
        PceFile.Peek(empty + ".pce", Password).Metadata.Name.Should().Be("nic");
        Directory.EnumerateFiles(_s.Root, "*.part").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_rewrite_leaves_the_old_file_as_it_was()
    {
        var file = SourceFile("sam.txt", Bytes(300_000));
        var pce = file + ".pce";
        PceFile.EncryptLegacy(file, pce, Password, Fast, PceCompression.None, null, Token);
        var before = File.ReadAllBytes(pce);
        using var cancel = new CancellationTokenSource();

        var cancelled = () => PceFile.Upgrade(pce, Password, _ => cancel.Cancel(), cancel.Token);
        var wrong = () => PceFile.Upgrade(pce, "inne", null, Token);

        cancelled.Should().Throw<OperationCanceledException>();
        wrong.Should().Throw<PceWrongPasswordException>();
        File.ReadAllBytes(pce).Should().Equal(before);
        Directory.EnumerateFiles(_s.Root, "*.part").Should().BeEmpty();
    }

    [Fact]
    public void Changing_the_password_works_for_both_layouts()
    {
        var file = SourceFile("sam.txt", Bytes(3000));
        var old = file + ".stary.pce";
        var current = file + ".pce";
        PceFile.EncryptLegacy(file, old, Password, Fast, PceCompression.None, null, Token);
        PceFile.Encrypt(file, current, Password, Fast, PceCompression.None, null, Token);
        var size = new FileInfo(current).Length;

        PceFile.ChangePassword(old, Password, "nowe", null, Token);
        PceFile.ChangePassword(current, Password, "nowe", null, Token);

        foreach (var pce in new[] { old, current })
        {
            var stale = () => PceFile.Peek(pce, Password);
            stale.Should().Throw<PceWrongPasswordException>();
            PceFile.Peek(pce, "nowe").Metadata.Name.Should().Be("sam.txt");
        }

        new FileInfo(current).Length.Should().Be(size);
        var wrong = () => PceFile.ChangePassword(current, "nie to", "x", null, Token);
        wrong.Should().Throw<PceWrongPasswordException>();
    }
}
