using System.Security.Cryptography;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using PlainCommander.Core.Undo;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>The .pce container: round trips, the password check on block 0, and every way a file can be damaged.</summary>
public sealed class PceTests : IDisposable
{
    private static readonly PceOptions Fast = PceOptions.Fast;
    private static readonly int Chunk = Fast.ChunkSize;
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static byte[] Bytes(int count)
    {
        var data = new byte[count];
        new Random(count).NextBytes(data);
        return data;
    }

    private string Encrypted(string name, byte[] content, string password = "tajne", PceCompression compress = PceCompression.None)
    {
        var source = _s.P(name);
        File.WriteAllBytes(source, content);
        File.SetLastWriteTimeUtc(source, new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        var target = source + PceFormat.Extension;
        PceFile.Encrypt(source, target, password, Fast, compress, null, TestContext.Current.CancellationToken);
        return target;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64 * 1024 - 1)]
    [InlineData(64 * 1024)]
    [InlineData(64 * 1024 + 1)]
    [InlineData(3 * 64 * 1024 + 7)]
    public void Round_trips_a_file_of_any_size_with_its_metadata(int size)
    {
        var content = Bytes(size);
        var pce = Encrypted("dane.bin", content);

        var (header, metadata) = PceFile.Peek(pce, "tajne");
        header.IsFolder.Should().BeFalse();
        header.Kdf.Should().Be(PceKdf.Argon2id);
        metadata.Name.Should().Be("dane.bin");
        metadata.Size.Should().Be(size);
        metadata.ModifiedUtc.Should().Be(new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        var back = _s.P("out", "x.bin");
        Directory.CreateDirectory(_s.P("out"));
        PceFile.Decrypt(pce, "tajne", back, null, TestContext.Current.CancellationToken);
        File.ReadAllBytes(back).Should().Equal(content);
        File.GetLastWriteTimeUtc(back).Should().Be(metadata.ModifiedUtc);
    }

    [Fact]
    public void Compressed_content_round_trips()
    {
        var content = new byte[200_000];
        Array.Fill(content, (byte)'a');
        var pce = Encrypted("a.txt", content, compress: PceCompression.Always);
        new FileInfo(pce).Length.Should().BeLessThan(20_000, "200 000 identical bytes compress well");
        PceFile.Peek(pce, "tajne").Header.Flags.Should().HaveFlag(PceLayout.Zstd);

        var back = _s.P("a2.txt");
        PceFile.Decrypt(pce, "tajne", back, null, TestContext.Current.CancellationToken);
        File.ReadAllBytes(back).Should().Equal(content);
    }

    [Fact]
    public void Auto_compresses_text_but_not_random_data()
    {
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Plain Commander szyfruje pliki. ", 20_000)));
        var textPce = Encrypted("t.txt", text, compress: PceCompression.Auto);
        PceFile.Peek(textPce, "tajne").Header.IsCompressed.Should().BeTrue();
        new FileInfo(textPce).Length.Should().BeLessThan(text.Length / 10);

        var random = Encrypted("r.bin", Bytes(300_000), compress: PceCompression.Auto);
        PceFile.Peek(random, "tajne").Header.IsCompressed.Should().BeFalse("random bytes do not shrink");
        new FileInfo(random).Length.Should().BeLessThan(300_000 + 1000);

        var tiny = Encrypted("tiny.txt", "aaaa"u8.ToArray(), compress: PceCompression.Auto);
        PceFile.Peek(tiny, "tajne").Header.IsCompressed.Should().BeFalse("below the sample minimum");

        var back = _s.P("t2.txt");
        PceFile.Decrypt(textPce, "tajne", back, null, TestContext.Current.CancellationToken);
        File.ReadAllBytes(back).Should().Equal(text);
    }

    [Fact]
    public void Auto_samples_the_folder_files()
    {
        var folder = _s.Dir("docs");
        File.WriteAllText(Path.Combine(folder, "a.txt"), string.Concat(Enumerable.Repeat("tekst ", 5000)));
        File.WriteAllText(Path.Combine(folder, "b.txt"), string.Concat(Enumerable.Repeat("inny ", 5000)));
        var pce = _s.P("docs.pce");
        PceFile.Encrypt(folder, pce, "tajne", Fast, PceCompression.Auto, null, TestContext.Current.CancellationToken);
        PceFile.Peek(pce, "tajne").Header.IsCompressed.Should().BeTrue();
        var target = _s.P("out", "docs");
        PceFile.Decrypt(pce, "tajne", target, null, TestContext.Current.CancellationToken);
        File.ReadAllText(Path.Combine(target, "b.txt")).Should().StartWith("inny inny");
    }

    [Fact]
    public void Wrong_password_is_rejected_before_any_content_is_read()
    {
        var pce = Encrypted("x.txt", Bytes(1000));
        var act = () => PceFile.Peek(pce, "inne");
        act.Should().Throw<PceWrongPasswordException>();
    }

    [Fact]
    public void A_file_that_is_not_pce_is_refused()
    {
        var plain = _s.File("note.txt", "hello world, not encrypted at all");
        var act = () => PceFile.Peek(plain, "tajne");
        act.Should().Throw<PceFormatException>();
    }

    [Fact]
    public void Pbkdf2_and_chacha_are_accepted_as_alternatives()
    {
        Assert.SkipUnless(ChaCha20Poly1305.IsSupported, "ChaCha20-Poly1305 is not available on this system");
        var source = _s.File("c.txt", "chacha content");
        var target = source + PceFormat.Extension;
        var options = Fast with { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000, Cipher = PceCipherKind.ChaCha20Poly1305 };
        PceFile.Encrypt(source, target, "tajne", options, PceCompression.None, null, TestContext.Current.CancellationToken);

        var (header, _) = PceFile.Peek(target, "tajne");
        header.Kdf.Should().Be(PceKdf.Pbkdf2Sha512);
        header.Cipher.Should().Be(PceCipherKind.ChaCha20Poly1305);
        var back = _s.P("c2.txt");
        PceFile.Decrypt(target, "tajne", back, null, TestContext.Current.CancellationToken);
        File.ReadAllText(back).Should().Be("chacha content");
    }

    private static Action Decrypting(string pce, string target) => () => PceFile.Decrypt(pce, "tajne", target, null, TestContext.Current.CancellationToken);

    [Fact]
    public void A_truncated_file_is_rejected()
    {
        var pce = Encrypted("t.bin", Bytes(2 * Chunk + 10));
        var bytes = File.ReadAllBytes(pce);
        File.WriteAllBytes(pce, bytes[..^(10 + PceFormat.TagSize + 4)]); // the last block gone
        Decrypting(pce, _s.P("t2.bin")).Should().Throw<PceFormatException>().WithMessage("*cut short*");

        File.WriteAllBytes(pce, bytes[..^5]); // cut inside the last block
        Decrypting(pce, _s.P("t3.bin")).Should().Throw<PceFormatException>();
    }

    [Fact]
    public void A_flipped_byte_in_the_middle_is_rejected()
    {
        var pce = Encrypted("m.bin", Bytes(2 * Chunk + 10));
        var bytes = File.ReadAllBytes(pce);
        var block1 = PceFormat.HeaderSize + Block0Length(pce) + 4 + 100;
        bytes[block1] ^= 0x01;
        File.WriteAllBytes(pce, bytes);
        Decrypting(pce, _s.P("m2.bin")).Should().Throw<PceFormatException>().WithMessage("*block 1*");
    }

    [Fact]
    public void A_flipped_byte_in_the_header_is_rejected()
    {
        var pce = Encrypted("h.bin", Bytes(100));
        var bytes = File.ReadAllBytes(pce);
        bytes[4] ^= 0x02; // flags: claims to be compressed
        File.WriteAllBytes(pce, bytes);
        var act = () => PceFile.Peek(pce, "tajne");
        act.Should().Throw<PceWrongPasswordException>("the header is associated data of every block");
    }

    [Fact]
    public void Swapped_blocks_are_rejected()
    {
        var pce = Encrypted("s.bin", Bytes(3 * Chunk));
        var bytes = File.ReadAllBytes(pce);
        var blockSize = 4 + Chunk + PceFormat.TagSize;
        var first = PceFormat.HeaderSize + Block0Length(pce);
        var a = bytes[first..(first + blockSize)];
        var b = bytes[(first + blockSize)..(first + 2 * blockSize)];
        b.CopyTo(bytes, first);
        a.CopyTo(bytes, first + blockSize);
        File.WriteAllBytes(pce, bytes);
        Decrypting(pce, _s.P("s2.bin")).Should().Throw<PceFormatException>();
    }

    [Fact]
    public void Data_after_the_last_block_is_rejected()
    {
        var pce = Encrypted("e.bin", Bytes(50));
        File.AppendAllText(pce, "x");
        Decrypting(pce, _s.P("e2.bin")).Should().Throw<PceFormatException>().WithMessage("*after its last block*");
    }

    [Fact]
    public void A_file_cut_down_to_its_metadata_block_is_rejected_as_truncated()
    {
        var pce = Encrypted("z.bin", Bytes(50));
        var bytes = File.ReadAllBytes(pce);
        File.WriteAllBytes(pce, bytes[..(PceFormat.HeaderSize + Block0Length(pce))]);
        PceFile.Peek(pce, "tajne").Metadata.Name.Should().Be("z.bin", "block 0 is still intact");
        Decrypting(pce, _s.P("z2.bin")).Should().Throw<PceFormatException>().WithMessage("*last block is missing*");
    }

    /// <summary>Bytes of block 0 on disk: its length field says how long the metadata is.</summary>
    private static int Block0Length(string pce)
    {
        using var stream = File.OpenRead(pce);
        stream.Position = PceFormat.HeaderSize;
        Span<byte> length = stackalloc byte[4];
        stream.ReadExactly(length);
        return 4 + BitConverter.ToInt32(length) + PceFormat.TagSize;
    }

    [Fact]
    public void A_folder_round_trips_as_a_tar_inside()
    {
        var folder = _s.Dir("skany");
        File.WriteAllBytes(Path.Combine(folder, "1.png"), Bytes(70_000));
        Directory.CreateDirectory(Path.Combine(folder, "sub", "deep"));
        File.WriteAllText(Path.Combine(folder, "sub", "deep", "n.txt"), "nested");
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var pce = _s.P("skany.pce");
        long last = 0;
        PceFile.Encrypt(folder, pce, "tajne", Fast, PceCompression.None, total => last = total, TestContext.Current.CancellationToken);
        last.Should().Be(70_000 + 6);

        var (header, metadata) = PceFile.Peek(pce, "tajne");
        header.IsFolder.Should().BeTrue();
        metadata.Name.Should().Be("skany");
        metadata.Size.Should().Be(-1);

        var target = _s.P("out", "skany");
        PceFile.Decrypt(pce, "tajne", target, null, TestContext.Current.CancellationToken);
        File.ReadAllBytes(Path.Combine(target, "1.png")).Should().Equal(Bytes(70_000));
        File.ReadAllText(Path.Combine(target, "sub", "deep", "n.txt")).Should().Be("nested");
        Directory.Exists(Path.Combine(target, "empty")).Should().BeTrue();
    }

    [Fact]
    public void Folder_content_can_be_copied_out_as_a_plain_tar()
    {
        var folder = _s.Dir("f");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "A");
        var pce = _s.P("f.pce");
        PceFile.Encrypt(folder, pce, "tajne", Fast, PceCompression.None, null, TestContext.Current.CancellationToken);
        var tar = _s.P("f.tar");
        using (var output = File.Create(tar))
        {
            PceFile.CopyContent(pce, "tajne", output, null, TestContext.Current.CancellationToken);
        }

        new FileInfo(tar).Length.Should().BeGreaterThan(512);
        using var input = File.OpenRead(tar);
        using var reader = SharpCompress.Readers.Tar.TarReader.Open(input);
        reader.MoveToNextEntry().Should().BeTrue();
        reader.Entry.Key.Should().Be("a.txt");
    }

    [Fact]
    public void Random_names_are_pce_files_with_sixteen_safe_characters()
    {
        var name = PceFile.RandomName();
        name.Should().EndWith(".pce");
        name.Should().HaveLength(16 + 4);
        name.Should().MatchRegex("^[a-z0-9]{16}\\.pce$");
        PceFile.RandomName().Should().NotBe(name);
    }

    [Fact]
    public async Task Encrypt_operation_writes_pce_next_to_each_item_and_journals_it()
    {
        var file = _s.File("umowa.pdf", "pdf");
        var folder = _s.Dir("skany");
        File.WriteAllText(Path.Combine(folder, "1.png"), "png");

        var op = new EncryptOperation([file, folder], PceFormat.Extension, "tajne", options: Fast);
        var result = await Run.Async(op);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(2);
        File.Exists(file + ".pce").Should().BeTrue();
        File.Exists(folder + ".pce").Should().BeTrue();
        File.Exists(file).Should().BeTrue("the originals stay until the user decides");
        Directory.GetFiles(_s.Root, "*.part").Should().BeEmpty();
        op.Journal.Actions.Should().HaveCount(2).And.OnlyContain(a => a.Kind == UndoKind.Created);
        PceFile.Peek(file + ".pce", "tajne").Metadata.Name.Should().Be("umowa.pdf");
        PceFile.Peek(folder + ".pce", "tajne").Header.IsFolder.Should().BeTrue();
    }

    [Fact]
    public async Task Encrypt_operation_can_hide_the_name_and_asks_about_an_existing_target()
    {
        var file = _s.File("tajny.txt", "secret");
        var hidden = await Run.Async(new EncryptOperation([file], PceFormat.Extension, "tajne", hideName: true, options: Fast));
        hidden.Done.Should().Be(1);
        var created = Directory.GetFiles(_s.Root, "*.pce").Single();
        Path.GetFileName(created).Should().NotContain("tajny");
        PceFile.Peek(created, "tajne").Metadata.Name.Should().Be("tajny.txt");

        File.WriteAllText(file + ".pce", "old");
        var prompts = new ScriptedPrompts([ConflictChoice.Skip]);
        var skipped = await Run.Async(new EncryptOperation([file], PceFormat.Extension, "tajne", options: Fast), prompts);
        skipped.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        skipped.Done.Should().Be(0);
        File.ReadAllText(file + ".pce").Should().Be("old");
        prompts.Conflicts.Single().TargetPath.Should().EndWith("tajny.txt.pce");

        var overwritten = await Run.Async(new EncryptOperation([file], PceFormat.Extension, "tajne", options: Fast), new ScriptedPrompts([ConflictChoice.Overwrite]));
        overwritten.Done.Should().Be(1);
        PceFile.Peek(file + ".pce", "tajne").Metadata.Name.Should().Be("tajny.txt");
    }

    [Fact]
    public async Task Decrypt_operation_restores_files_and_folders_next_to_the_container()
    {
        var file = _s.File("umowa.pdf", "pdf");
        var folder = _s.Dir("skany");
        File.WriteAllText(Path.Combine(folder, "1.png"), "png");
        await Run.Async(new EncryptOperation([file, folder], PceFormat.Extension, "tajne", options: Fast));
        File.Delete(file);
        Directory.Delete(folder, recursive: true);

        var op = new DecryptOperation([file + ".pce", folder + ".pce"], "tajne");
        var result = await Run.Async(op);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(2);
        File.ReadAllText(file).Should().Be("pdf");
        File.ReadAllText(Path.Combine(folder, "1.png")).Should().Be("png");
        File.Exists(file + ".pce").Should().BeTrue("the container stays");
        op.Journal.Actions.Select(a => (a.Kind, a.IsDirectory)).Should().BeEquivalentTo([(UndoKind.Created, false), (UndoKind.Created, true)]);
    }

    [Fact]
    public async Task Decrypt_operation_reports_a_wrong_password_as_an_error_and_keeps_both_on_a_conflict()
    {
        var file = _s.File("a.txt", "new content");
        await Run.Async(new EncryptOperation([file], PceFormat.Extension, "tajne", options: Fast));

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var wrong = await Run.Async(new DecryptOperation([file + ".pce"], "zle"), prompts);
        wrong.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors.Single().Should().BeOfType<PceWrongPasswordException>();

        File.WriteAllText(file, "old content");
        var both = await Run.Async(new DecryptOperation([file + ".pce"], "tajne"), new ScriptedPrompts([ConflictChoice.KeepBoth]));
        both.Done.Should().Be(1);
        File.ReadAllText(file).Should().Be("old content");
        Directory.GetFiles(_s.Root, "a (*.txt").Should().ContainSingle().Which.Should().Satisfy<string>(p => File.ReadAllText(p).Should().Be("new content"));
    }

    [Fact]
    public async Task Cancelling_leaves_no_part_file()
    {
        var file = _s.P("big.bin");
        File.WriteAllBytes(file, Bytes(8 * Chunk));
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cts.Cancel());
        var result = await Run.Async(new EncryptOperation([file], PceFormat.Extension, "tajne", options: Fast), progress: progress, token: cts.Token);
        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        Directory.GetFiles(_s.Root, "*.part").Should().BeEmpty();
    }
}
