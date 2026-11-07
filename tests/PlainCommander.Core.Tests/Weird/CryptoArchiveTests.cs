using System.Formats.Tar;
using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>
/// Stress tests of the .pce container: odd sizes and passwords, every kind of damage, crafted folder content and
/// what the encrypt/decrypt operations leave on disk when something goes wrong.
/// </summary>
public sealed class CryptoArchiveTests : IDisposable
{
    private static readonly PceOptions Fast = PceOptions.Fast;
    private static readonly int Chunk = Fast.ChunkSize;
    private const int Mebibyte = 1024 * 1024;
    private const string Password = "tajne";
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count + seed).NextBytes(data);
        return data;
    }

    private string Encrypted(string name, byte[] content, string password = Password, PceCompression compress = PceCompression.None, PceOptions? options = null)
    {
        var source = _s.P(name);
        File.WriteAllBytes(source, content);
        var target = source + PceFormat.Extension;
        PceFile.Encrypt(source, target, password, options ?? Fast, compress, null, Token);
        return target;
    }

    private byte[] Decrypted(string pce, string password = Password)
    {
        var back = _s.P("back-" + Guid.NewGuid().ToString("N"));
        PceFile.Decrypt(pce, password, back, null, Token);
        return File.ReadAllBytes(back);
    }

    /// <summary>Offsets where each block starts (block 0 first), then the end of the last block.</summary>
    private static List<int> BlockStarts(byte[] file)
    {
        var starts = new List<int>();
        var at = PceFormat.HeaderSize;
        while (at < file.Length)
        {
            starts.Add(at);
            at += 4 + BitConverter.ToInt32(file, at) + PceFormat.TagSize;
        }

        starts.Add(at);
        return starts;
    }

    /// <summary>Decrypting must fail with a format or password error, never succeed.</summary>
    private void MustBeRejected(byte[] damaged, string because)
    {
        var pce = _s.P("damaged-" + Guid.NewGuid().ToString("N") + ".pce");
        File.WriteAllBytes(pce, damaged);
        var back = pce + ".out";
        Exception? caught = null;
        try
        {
            PceFile.Decrypt(pce, Password, back, null, Token);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull(because);
        caught.Should().Match<Exception>(e => e is PceFormatException || e is PceWrongPasswordException, $"{because}: got {caught?.GetType().Name}: {caught?.Message}");
    }

    // ---------- sizes ----------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Mebibyte - 1)]
    [InlineData(Mebibyte)]
    [InlineData(Mebibyte + 1)]
    [InlineData(2 * Mebibyte)]
    public void One_mebibyte_blocks_round_trip_around_the_block_boundary(int size)
    {
        var options = Fast with { ChunkSize = Mebibyte };
        var content = Bytes(size);
        var pce = Encrypted("mib.bin", content, options: options);

        Decrypted(pce).Should().Equal(content);
        var blocks = BlockStarts(File.ReadAllBytes(pce)).Count - 1;
        blocks.Should().Be(1 + (size / Mebibyte) + 1, "metadata block, full blocks, and a last (possibly empty) block");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Exact_multiples_of_the_block_size_end_with_an_empty_last_block(int blocks)
    {
        var content = Bytes(blocks * Chunk);
        var pce = Encrypted("m.bin", content);
        var file = File.ReadAllBytes(pce);
        var starts = BlockStarts(file);
        BitConverter.ToInt32(file, starts[^2]).Should().Be(0, "the last block is empty");
        Decrypted(pce).Should().Equal(content);
    }

    [Theory]
    [InlineData(0, PceCompression.Always)]
    [InlineData(255, PceCompression.Always)]
    [InlineData(255, PceCompression.Auto)]
    [InlineData(256, PceCompression.Auto)]
    public void Tiny_compressible_files_round_trip_in_every_mode(int size, PceCompression mode)
    {
        var content = Enumerable.Repeat((byte)'z', size).ToArray();
        var pce = Encrypted("tiny.txt", content, compress: mode);
        Decrypted(pce).Should().Equal(content);
    }

    [Fact]
    public void Auto_mode_decides_on_the_sample_only_and_round_trips_either_way()
    {
        // Random first block, then a long compressible tail: the sample says "no", content still round-trips.
        var tail = Enumerable.Repeat((byte)'a', 3 * Chunk).ToArray();
        var mixed = Bytes(Chunk).Concat(tail).ToArray();
        var pce = Encrypted("mixed.bin", mixed, compress: PceCompression.Auto);
        PceFile.Peek(pce, Password).Header.IsCompressed.Should().BeFalse();
        Decrypted(pce).Should().Equal(mixed);

        // Compressible sample, then incompressible data: compressed, still round-trips.
        var flipped = tail[..Chunk].Concat(Bytes(2 * Chunk + 3)).ToArray();
        var pce2 = Encrypted("flipped.bin", flipped, compress: PceCompression.Auto);
        PceFile.Peek(pce2, Password).Header.IsCompressed.Should().BeTrue();
        Decrypted(pce2).Should().Equal(flipped);
    }

    [Fact]
    public void Compressed_content_of_block_sized_lengths_round_trips()
    {
        foreach (var size in new[] { Chunk - 1, Chunk, Chunk + 1, 4 * Chunk })
        {
            var content = Enumerable.Range(0, size).Select(i => (byte)('a' + (i % 7))).ToArray();
            var pce = Encrypted($"c{size}.txt", content, compress: PceCompression.Always);
            Decrypted(pce).Should().Equal(content);
        }
    }

    // ---------- passwords ----------

    [Fact]
    public void Empty_password_is_refused_on_both_sides()
    {
        var source = _s.File("a.txt", "a");
        var encrypt = () => PceFile.Encrypt(source, _s.P("a.txt.pce"), string.Empty, Fast, PceCompression.None, null, Token);
        encrypt.Should().Throw<ArgumentException>();

        var pce = Encrypted("b.txt", Bytes(5));
        var decrypt = () => PceFile.Peek(pce, string.Empty);
        decrypt.Should().Throw<ArgumentException>();
        var op = () => new DecryptOperation([pce], string.Empty);
        op.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Very_long_and_unicode_passwords_round_trip()
    {
        var content = Bytes(1000);
        foreach (var password in new[] { new string('x', 100_000), "zażółć gęślą jaźń 🔐", "\u0000inside\u0000", "  spaces  " })
        {
            var pce = Encrypted("p.bin", content, password);
            Decrypted(pce, password).Should().Equal(content);
        }
    }

    [Fact]
    public void Passwords_are_exact_except_unicode_normalization_trailing_space_and_case_matter()
    {
        var nfc = "Zażółć".Normalize(NormalizationForm.FormC);
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        nfd.Should().NotBe(nfc);
        var pce = Encrypted("n.bin", Bytes(10), nfc);

        PceFile.Peek(pce, nfd).Metadata.Name.Should().Be("n.bin", "the password is normalized to NFC (NfcPassword flag)");
        ((Action)(() => PceFile.Peek(pce, nfc + " "))).Should().Throw<PceWrongPasswordException>();
        ((Action)(() => PceFile.Peek(pce, nfc.ToUpperInvariant()))).Should().Throw<PceWrongPasswordException>();
        PceFile.Peek(pce, nfc).Metadata.Name.Should().Be("n.bin");
    }

    // ---------- damage ----------

    [Fact]
    public void Zero_byte_and_header_only_files_are_rejected()
    {
        MustBeRejected([], "an empty .pce");
        MustBeRejected("PCE1"u8.ToArray(), "only the magic");
        var good = File.ReadAllBytes(Encrypted("h.bin", Bytes(10)));
        MustBeRejected(good[..PceFormat.HeaderSize], "only the header");
        MustBeRejected(good[..(PceFormat.HeaderSize + 2)], "half a length field");
        MustBeRejected(new byte[100], "zeros");
    }

    [Fact]
    public void Truncation_at_every_structural_boundary_is_rejected()
    {
        var good = File.ReadAllBytes(Encrypted("t.bin", Bytes(2 * Chunk + 10)));
        var cuts = new SortedSet<int>();
        foreach (var start in BlockStarts(good))
        {
            foreach (var delta in new[] { -17, -16, -1, 0, 1, 3, 4, 5, 20 })
            {
                var cut = start + delta;
                if (cut >= 0 && cut < good.Length)
                {
                    cuts.Add(cut);
                }
            }
        }

        foreach (var cut in cuts)
        {
            MustBeRejected(good[..cut], $"cut at {cut} of {good.Length}");
        }
    }

    [Fact]
    public void A_flipped_bit_anywhere_in_header_lengths_ciphertext_or_tags_is_rejected()
    {
        var good = File.ReadAllBytes(Encrypted("f.bin", Bytes(2 * Chunk + 10)));
        var starts = BlockStarts(good);
        var offsets = new SortedSet<int>(Enumerable.Range(0, PceFormat.HeaderSize));
        for (var b = 0; b < starts.Count - 1; b++)
        {
            var start = starts[b];
            var end = starts[b + 1];
            foreach (var at in new[] { start, start + 3, start + 4, (start + end) / 2, end - PceFormat.TagSize - 1, end - PceFormat.TagSize, end - 1 })
            {
                offsets.Add(at);
            }
        }

        foreach (var at in offsets)
        {
            // Bit 0 only in the KDF fields, so no flip asks Argon2 for gigabytes.
            var bit = at is >= 8 and <= 13 ? 0 : at % 8;
            var damaged = good.ToArray();
            damaged[at] ^= (byte)(1 << bit);
            MustBeRejected(damaged, $"bit {bit} of byte {at} flipped");
        }
    }

    [Fact]
    public void Reordered_duplicated_dropped_and_appended_blocks_are_rejected()
    {
        var good = File.ReadAllBytes(Encrypted("r.bin", Bytes(3 * Chunk + 5)));
        var s = BlockStarts(good); // b0 | b1 | b2 | b3 | b4 (last) | end
        byte[] Block(int i) => good[s[i]..s[i + 1]];
        byte[] Join(params int[] blocks) => [.. good[..PceFormat.HeaderSize], .. blocks.SelectMany(Block)];

        Join(0, 1, 2, 3, 4).Should().Equal(good, "the helper rebuilds the file");
        MustBeRejected(Join(0, 2, 1, 3, 4), "swapped 1 and 2");
        MustBeRejected(Join(0, 1, 1, 2, 3, 4), "block 1 twice");
        MustBeRejected(Join(0, 2, 3, 4), "block 1 dropped");
        MustBeRejected(Join(0, 1, 2, 3), "last block dropped");
        MustBeRejected(Join(0, 1, 2, 3, 4, 4), "last block twice");
        MustBeRejected(Join(1, 2, 3, 4), "metadata block dropped");
        MustBeRejected(Join(0, 0, 1, 2, 3, 4), "metadata block twice");
        MustBeRejected([.. good, 0], "one zero byte appended");
        MustBeRejected([.. good, .. "garbage"u8], "garbage appended");
    }

    [Fact]
    public void Blocks_from_another_file_with_the_same_password_are_rejected()
    {
        var a = File.ReadAllBytes(Encrypted("a.bin", Bytes(2 * Chunk, 1)));
        var b = File.ReadAllBytes(Encrypted("b.bin", Bytes(2 * Chunk, 2)));
        var sa = BlockStarts(a);
        var sb = BlockStarts(b);
        MustBeRejected([.. a[..sa[2]], .. b[sb[2]..sb[3]], .. a[sa[3]..]], "block 2 spliced in from another file");
        MustBeRejected([.. b[..PceFormat.HeaderSize], .. a[PceFormat.HeaderSize..]], "header of another file");
    }

    [Fact]
    public void A_damaged_compressed_file_or_folder_is_rejected()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("compressible line of text\n", 20_000)));
        var good = File.ReadAllBytes(Encrypted("z.txt", text, compress: PceCompression.Always));
        var starts = BlockStarts(good);
        MustBeRejected(good[..starts[^2]], "compressed, last block dropped");
        var flipped = good.ToArray();
        flipped[starts[1] + 10] ^= 1;
        MustBeRejected(flipped, "compressed, first data block damaged");

        var folder = _s.Dir("fold");
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), Bytes(3 * Chunk));
        var fpce = _s.P("fold.pce");
        PceFile.Encrypt(folder, fpce, Password, Fast, PceCompression.None, null, Token);
        var fgood = File.ReadAllBytes(fpce);
        var fflip = fgood.ToArray();
        fflip[BlockStarts(fgood)[2] + 50] ^= 1;
        MustBeRejected(fflip, "folder, data block damaged");
    }

    [Fact]
    public void Wrong_password_and_damage_are_told_apart()
    {
        var pce = Encrypted("w.bin", Bytes(2 * Chunk));
        ((Action)(() => PceFile.Peek(pce, "zle"))).Should().Throw<PceWrongPasswordException>();
        var bytes = File.ReadAllBytes(pce);
        bytes[BlockStarts(bytes)[1] + 8] ^= 0x80;
        File.WriteAllBytes(pce, bytes);
        ((Action)(() => PceFile.Peek(pce, Password))).Should().NotThrow("block 0 is intact");
        ((Action)(() => Decrypted(pce))).Should().Throw<PceFormatException>().WithMessage("*block 1*");
    }

    // ---------- operations on damaged input ----------

    [Fact]
    public async Task Decrypt_operation_on_a_damaged_file_leaves_neither_target_nor_part()
    {
        var pce = Encrypted("doc.txt", Bytes(3 * Chunk));
        File.Delete(_s.P("doc.txt"));
        var bytes = File.ReadAllBytes(pce);
        bytes[BlockStarts(bytes)[2] + 30] ^= 1;
        File.WriteAllBytes(pce, bytes);

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new DecryptOperation([pce], Password), prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors.Single().Should().BeOfType<PceFormatException>();
        File.Exists(_s.P("doc.txt")).Should().BeFalse("a damaged container produces nothing");
        File.Exists(_s.P("doc.txt.part")).Should().BeFalse();
    }

    [Fact]
    public async Task Decrypt_operation_on_a_zero_byte_pce_reports_an_error()
    {
        var pce = _s.File("empty.pce", string.Empty);
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new DecryptOperation([pce], Password), prompts);
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors.Single().Should().BeOfType<PceFormatException>();
        Directory.GetFileSystemEntries(_s.Root).Should().Equal([pce]);
    }

    [Fact]
    public async Task Decrypt_operation_where_a_folder_has_the_file_name_refuses_to_overwrite_it()
    {
        var pce = Encrypted("clash", Bytes(10));
        File.Delete(_s.P("clash"));
        var folder = _s.Dir("clash");
        _s.File(Path.Combine("clash", "keep.txt"), "keep");

        var prompts = new ScriptedPrompts([ConflictChoice.Overwrite], [ErrorChoice.Skip]);
        var result = await Run.Async(new DecryptOperation([pce], Password), prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        _s.Read(Path.Combine("clash", "keep.txt")).Should().Be("keep");
        Directory.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task Decrypt_operation_overwrites_an_existing_read_only_file_on_request()
    {
        var pce = Encrypted("ro.txt", "new"u8.ToArray());
        File.WriteAllText(_s.P("ro.txt"), "old");
        File.SetAttributes(_s.P("ro.txt"), FileAttributes.ReadOnly);

        var result = await Run.Async(new DecryptOperation([pce], Password), new ScriptedPrompts([ConflictChoice.Overwrite]));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("ro.txt").Should().Be("new");
    }

    [Fact]
    public async Task Decrypt_operation_cancelled_mid_file_leaves_no_part_and_no_target()
    {
        var pce = Encrypted("big.bin", Bytes(16 * Chunk));
        File.Delete(_s.P("big.bin"));
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p =>
        {
            if (p.TotalBytesDone == 0)
            {
                Thread.Sleep(60); // the tracker reports at most every 50 ms: make the next report go through
            }
            else
            {
                cts.Cancel();
            }
        });
        var result = await Run.Async(new DecryptOperation([pce], Password), progress: progress, token: cts.Token);
        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        File.Exists(_s.P("big.bin")).Should().BeFalse();
        Directory.GetFiles(_s.Root, "*.part").Should().BeEmpty();
    }

    [Fact]
    public async Task Encrypt_operation_cancelled_mid_folder_leaves_no_part_and_no_target()
    {
        var folder = _s.Dir("many");
        for (var i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"{i}.bin"), Bytes(2 * Chunk, i));
        }

        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p =>
        {
            if (p.TotalBytesDone == 0)
            {
                Thread.Sleep(60); // the tracker reports at most every 50 ms: make the next report go through
            }
            else
            {
                cts.Cancel();
            }
        });
        var result = await Run.Async(new EncryptOperation([folder], PceFormat.Extension, Password, compression: PceCompression.Always, options: Fast), progress: progress, token: cts.Token);
        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        File.Exists(folder + ".pce").Should().BeFalse();
        Directory.GetFiles(_s.Root, "*.part").Should().BeEmpty();
    }

    // ---------- folders ----------

    [Fact]
    public void Odd_folders_round_trip_unicode_long_names_deep_nesting_empty_files_and_empty_dirs()
    {
        var root = _s.Dir("odd");
        var nfd = "café".Normalize(NormalizationForm.FormD);
        var longName = new string('n', 150) + ".txt";
        var longUnicode = string.Concat(Enumerable.Repeat("ż", 120)); // 240 UTF-8 bytes
        var deep = Path.Combine([root, .. Enumerable.Range(0, 25).Select(i => $"level{i:00}")]);
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "bottom.txt"), "deep");
        File.WriteAllText(Path.Combine(root, "Zażółć gęślą jaźń — 日本語 🙂 ü.txt"), "unicode");
        File.WriteAllText(Path.Combine(root, nfd), "nfd");
        File.WriteAllText(Path.Combine(root, longName), "long");
        Directory.CreateDirectory(Path.Combine(root, longUnicode));
        File.WriteAllText(Path.Combine(root, longUnicode, longName), "long in long");
        File.WriteAllBytes(Path.Combine(root, "empty.bin"), []);
        Directory.CreateDirectory(Path.Combine(root, "only-empty", "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "only-empty", "c"));
        File.WriteAllText(Path.Combine(root, " lead and trail.txt"), "spaces");
        File.WriteAllText(Path.Combine(root, "#%&{}[]'!@$^.txt"), "symbols");

        var pce = _s.P("odd.pce");
        PceFile.Encrypt(root, pce, Password, Fast, PceCompression.Auto, null, Token);
        var back = _s.P("out", "odd");
        PceFile.Decrypt(pce, Password, back, null, Token);

        string Rel(string full) => Path.GetRelativePath(root, full);
        var expected = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(Rel).Order(StringComparer.Ordinal).ToList();
        var actual = Directory.GetFileSystemEntries(back, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(back, p)).Order(StringComparer.Ordinal).ToList();
        actual.Should().Equal(expected);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            File.ReadAllBytes(Path.Combine(back, Rel(file))).Should().Equal(File.ReadAllBytes(file), Rel(file));
        }
    }

    [Fact]
    public void An_empty_folder_round_trips_as_an_empty_folder()
    {
        var root = _s.Dir("nothing");
        var pce = _s.P("nothing.pce");
        PceFile.Encrypt(root, pce, Password, Fast, PceCompression.Auto, null, Token);
        var back = _s.P("out", "nothing");
        PceFile.Decrypt(pce, Password, back, null, Token);
        Directory.Exists(back).Should().BeTrue();
        Directory.GetFileSystemEntries(back).Should().BeEmpty();
    }

    [Fact]
    public void Links_in_a_folder_pack_file_targets_and_skip_folder_links()
    {
        var root = _s.Dir("links");
        var outside = _s.Dir("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");
        File.WriteAllText(Path.Combine(root, "real.txt"), "real");
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "file-link.txt"), Path.Combine(root, "real.txt"));
            Directory.CreateSymbolicLink(Path.Combine(root, "dir-link"), outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Symbolic links need privileges here.");
        }

        var pce = _s.P("links.pce");
        PceFile.Encrypt(root, pce, Password, Fast, PceCompression.None, null, Token);
        var back = _s.P("out", "links");
        PceFile.Decrypt(pce, Password, back, null, Token);
        File.ReadAllText(Path.Combine(back, "file-link.txt")).Should().Be("real", "a file link is packed as its target's content");
        new FileInfo(Path.Combine(back, "file-link.txt")).LinkTarget.Should().BeNull("never recreated as a link");
        Directory.Exists(Path.Combine(back, "dir-link")).Should().BeFalse("a folder link is skipped");
        File.Exists(Path.Combine(back, "dir-link", "secret.txt")).Should().BeFalse();
    }

    // ---------- crafted folder content (an attacker who knows the password) ----------

    private string CraftedFolder(string name, byte[] tar)
    {
        var pce = _s.P(name + ".pce");
        using (var output = File.Create(pce))
        using (var stream = new PceEncryptStream(output, Password, new PceMetadata(name, -1, DateTime.UtcNow, FileAttributes.Normal), Fast, PceLayout.Folder))
        {
            stream.Write(tar);
            stream.Complete();
        }

        return pce;
    }

    private static byte[] Tar(TarEntryFormat format, params (string Name, TarEntryType Type, string? Content, string? Link)[] entries)
    {
        using var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, format, leaveOpen: true))
        {
            foreach (var (name, type, content, link) in entries)
            {
                TarEntry entry = format switch
                {
                    TarEntryFormat.Gnu => new GnuTarEntry(type, name),
                    TarEntryFormat.Pax => new PaxTarEntry(type, name),
                    TarEntryFormat.Ustar => new UstarTarEntry(type, name),
                    _ => new V7TarEntry(type, name),
                };
                if (content is not null)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                }

                if (link is not null)
                {
                    entry.LinkName = link;
                }

                writer.WriteEntry(entry);
            }
        }

        return ms.ToArray();
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/tmp/pc-weird-evil.txt")]
    [InlineData("..")]
    [InlineData(".")]
    public void Crafted_folder_entries_that_leave_the_target_are_refused(string name)
    {
        var tar = Tar(TarEntryFormat.Gnu, (name, TarEntryType.RegularFile, "evil", null));
        var pce = CraftedFolder("crafted", tar);
        var target = _s.P("deep", "inside", "crafted");
        var act = () => PceFile.Decrypt(pce, Password, target, null, Token);
        act.Should().Throw<PceFormatException>().WithMessage("*outside*");
        Directory.GetFiles(_s.Root, "*evil*", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists("/tmp/pc-weird-evil.txt").Should().BeFalse();
    }

    [Fact]
    public void Crafted_long_name_entry_that_leaves_the_target_is_refused()
    {
        var name = "../" + new string('e', 120) + ".txt";
        var tar = Tar(TarEntryFormat.Gnu, (name, TarEntryType.RegularFile, "evil", null));
        var pce = CraftedFolder("longevil", tar);
        var act = () => PceFile.Decrypt(pce, Password, _s.P("in", "longevil"), null, Token);
        act.Should().Throw<PceFormatException>();
        Directory.GetFiles(_s.Root, "eeee*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void Crafted_links_are_skipped_and_never_followed()
    {
        var outside = _s.Dir("outside");
        var tar = Tar(
            TarEntryFormat.Gnu,
            ("link", TarEntryType.SymbolicLink, null, outside),
            ("hard", TarEntryType.HardLink, null, "../../outside/x"),
            ("link/through.txt", TarEntryType.RegularFile, "through", null));
        var pce = CraftedFolder("links", tar);
        var target = _s.P("in", "links");
        PceFile.Decrypt(pce, Password, target, null, Token);
        File.Exists(Path.Combine(outside, "through.txt")).Should().BeFalse("the link entry was not created, so nothing goes through it");
        File.ReadAllText(Path.Combine(target, "link", "through.txt")).Should().Be("through");
        File.Exists(Path.Combine(target, "hard")).Should().BeFalse();
    }

    /// <summary>A hand-made ustar header: name (100) and prefix (155) fields, no checksum (the reader ignores it).</summary>
    private static byte[] UstarEntry(string prefix, string name, char type, byte[] content)
    {
        var header = new byte[512];
        Encoding.UTF8.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(Convert.ToString(content.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
        Encoding.ASCII.GetBytes("00000000000\0").CopyTo(header, 136);
        header[156] = (byte)type;
        "ustar\000"u8.CopyTo(header.AsSpan(257));
        Encoding.UTF8.GetBytes(prefix).CopyTo(header, 345);
        var padded = new byte[(content.Length + 511) / 512 * 512];
        content.CopyTo(padded, 0);
        return [.. header, .. padded];
    }

    [Fact]
    public void Crafted_ustar_prefix_paths_are_read_and_contained()
    {
        var prefix = string.Join('/', Enumerable.Repeat("dir", 30)); // 119 chars
        var pce = CraftedFolder("ustar", [.. UstarEntry(prefix, "f.txt", '0', "u"u8.ToArray()), .. new byte[1024]]);
        var target = _s.P("in", "ustar");
        PceFile.Decrypt(pce, Password, target, null, Token);
        File.ReadAllText(Path.Combine(target, (prefix + "/f.txt").Replace('/', Path.DirectorySeparatorChar))).Should().Be("u");

        var evil = CraftedFolder("ustar-evil", [.. UstarEntry("../..", "evil.txt", '0', "e"u8.ToArray()), .. new byte[1024]]);
        var act = () => PceFile.Decrypt(evil, Password, _s.P("in", "ustar-evil"), null, Token);
        act.Should().Throw<PceFormatException>().WithMessage("*outside*");
        Directory.GetFiles(_s.Root, "evil.txt", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Crafted_metadata_name_with_a_path_is_reduced_to_a_plain_name()
    {
        var folder = _s.Dir("drop");
        var pce = Path.Combine(folder, "x.pce");
        using (var output = File.Create(pce))
        using (var stream = new PceEncryptStream(output, Password, new PceMetadata("../../escaped.txt", 3, DateTime.UtcNow, FileAttributes.Normal), Fast))
        {
            stream.Write("abc"u8);
        }

        var result = await Run.Async(new DecryptOperation([pce], Password));
        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllText(Path.Combine(folder, "escaped.txt")).Should().Be("abc");
        File.Exists(_s.P("escaped.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("/")]
    public async Task Crafted_metadata_name_that_is_no_name_is_reported(string name)
    {
        var folder = _s.Dir("drop2");
        var pce = Path.Combine(folder, "x.pce");
        using (var output = File.Create(pce))
        using (var stream = new PceEncryptStream(output, Password, new PceMetadata(name, 3, DateTime.UtcNow, FileAttributes.Normal), Fast))
        {
            stream.Write("abc"u8);
        }

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new DecryptOperation([pce], Password), prompts);
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        Directory.GetFileSystemEntries(folder).Should().Equal([pce]);
    }

    /// <summary>
    /// BUG: ReadTar allocates "new byte[size]" for a GNU long-name entry with the size straight from the header; a
    /// size over 2 GiB throws OverflowException, which is not an I/O error, so it escapes StepRunner and the whole
    /// DecryptOperation task faults instead of reporting the file. Needs a container made with the known password.
    /// </summary>
    [Fact]
    public async Task Regression_crafted_huge_long_name_header_crashes_the_decrypt_operation_with_OverflowException()
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes("././@LongLink").CopyTo(header, 0);
        Encoding.ASCII.GetBytes("77777777777\0").CopyTo(header, 124); // ~8 GiB name
        header[156] = (byte)'L';
        var pce = CraftedFolder("huge", [.. header, .. new byte[1024]]);
        Directory.CreateDirectory(_s.P("in"));
        File.Move(pce, _s.P("in", "huge.pce"));

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new DecryptOperation([_s.P("in", "huge.pce")], Password), prompts);
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
    }

    /// <summary>
    /// BUG: content that authenticates but is not valid zstd raises SharpCompress' ZstdException (not an IOException),
    /// which escapes StepRunner, so DecryptOperation faults instead of reporting the file (same for Deflate's
    /// InvalidDataException). Needs a container made with the known password.
    /// </summary>
    [Fact]
    public async Task Regression_crafted_bad_zstd_content_crashes_the_decrypt_operation_with_ZstdException()
    {
        var pce = _s.P("z", "garbage.bin.pce");
        Directory.CreateDirectory(_s.P("z"));
        using (var output = File.Create(pce))
        using (var stream = new PceEncryptStream(output, Password, new PceMetadata("garbage.bin", 100, DateTime.UtcNow, FileAttributes.Normal), Fast, PceLayout.Zstd))
        {
            stream.Write(Bytes(5000));
        }

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new DecryptOperation([pce], Password), prompts);
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        Directory.GetFileSystemEntries(_s.P("z")).Should().Equal([pce]);
    }

    // ---------- confirmed bugs (each test fails until fixed) ----------

    /// <summary>
    /// BUG: PceFile.Encrypt seals the last block from the "using" Dispose even when it is leaving with an exception
    /// (cancel, read error), so the target is a well-formed container holding only a prefix of the source, and it
    /// decrypts without any error. EncryptOperation hides it by deleting its .part file, but the public API does not.
    /// </summary>
    [Fact]
    public void Regression_cancelled_PceFile_Encrypt_leaves_a_valid_but_truncated_container()
    {
        var source = _s.P("src.bin");
        File.WriteAllBytes(source, Bytes(4 * Mebibyte));
        var target = _s.P("src.bin.pce");
        using var cts = new CancellationTokenSource();
        var act = () => PceFile.Encrypt(source, target, Password, Fast, PceCompression.None, total =>
        {
            if (total > 0)
            {
                cts.Cancel();
            }
        }, cts.Token);
        act.Should().Throw<OperationCanceledException>();

        if (File.Exists(target))
        {
            var decrypt = () => Decrypted(target);
            decrypt.Should().Throw<PceFormatException>("an interrupted container must not decrypt as if it were complete");
        }
    }

    /// <summary>
    /// BUG: a folder container is decrypted straight into the target (no temp folder, no cleanup): a block that fails
    /// authentication half-way leaves the folder with a truncated copy of the file it was writing.
    /// </summary>
    [Fact]
    public async Task Regression_damaged_folder_container_leaves_a_partial_folder_and_truncated_file()
    {
        var folder = _s.Dir("photos");
        File.WriteAllBytes(Path.Combine(folder, "big.bin"), Bytes(4 * Chunk));
        await Run.Async(new EncryptOperation([folder], PceFormat.Extension, Password, options: Fast));
        Directory.Delete(folder, recursive: true);
        var pce = folder + ".pce";
        var bytes = File.ReadAllBytes(pce);
        bytes[BlockStarts(bytes)[3] + 40] ^= 1; // block 3 is inside big.bin
        File.WriteAllBytes(pce, bytes);

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new DecryptOperation([pce], Password), prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors.Single().Should().BeOfType<PceFormatException>();
        File.Exists(Path.Combine(folder, "big.bin")).Should().BeFalse("a truncated big.bin must not be left behind as if decrypted");
    }

    /// <summary>
    /// BUG: the decrypt operation writes to "name.part" with FileMode.Create and deletes it afterwards, so a user's
    /// own file of that name (a half-downloaded "film.mkv.part") is overwritten and then deleted.
    /// </summary>
    [Fact]
    public async Task Regression_decrypt_operation_destroys_an_unrelated_existing_part_file()
    {
        var pce = Encrypted("film.mkv", Bytes(100));
        File.Delete(_s.P("film.mkv"));
        var users = _s.File("film.mkv.part", "the user's partial download");

        var result = await Run.Async(new DecryptOperation([pce], Password));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.Exists(users).Should().BeTrue("a file the operation did not create must survive");
    }
}
