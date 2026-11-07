using System.Buffers.Binary;
using System.Diagnostics;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>.pce containers: damaged files fail with the container's own exceptions and never yield wrong plaintext.</summary>
public sealed class PceFuzzTests : IDisposable
{
    private const string Password = "tajne";

    /// <summary>PBKDF2 at its floor and tiny blocks: many blocks per file, a millisecond per key.</summary>
    private static readonly PceOptions Cheap = new() { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000, ChunkSize = 64 };

    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static byte[] Encrypt(byte[] content, PceOptions options, PceLayout layout = PceLayout.None, string name = "f.bin", int[]? splits = null)
    {
        using var output = new MemoryStream();
        using (var pce = new PceEncryptStream(output, Password, new PceMetadata(name, content.Length, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc), FileAttributes.Normal), options, layout))
        {
            var at = 0;
            foreach (var split in splits ?? [])
            {
                var take = Math.Min(split, content.Length - at);
                pce.Write(content, at, take);
                at += take;
            }

            pce.Write(content, at, content.Length - at);
            pce.Complete();
        }

        return output.ToArray();
    }

    private static (PceMetadata Metadata, byte[] Content) Decrypt(byte[] file, string password = Password)
    {
        using var pce = new PceDecryptStream(new MemoryStream(file), password);
        using var plain = new MemoryStream();
        pce.CopyTo(plain);
        return (pce.Metadata, plain.ToArray());
    }

    /// <summary>A mutation must not make the header ask for an expensive key: costs are put back to the original (cheap) values.</summary>
    private static byte[] KeepKdfCheap(byte[] mutated, byte[] original)
    {
        if (mutated.Length >= PceFormat.HeaderSize && original.Length >= PceFormat.HeaderSize)
        {
            var cost = BinaryPrimitives.ReadUInt32LittleEndian(mutated.AsSpan(8));
            if (cost > 20_000 || mutated[12] > 2 || mutated[13] > 2)
            {
                original.AsSpan(8, 6).CopyTo(mutated.AsSpan(8));
            }
        }

        return mutated;
    }

    [Fact]
    public void Round_trips_any_size_and_any_write_pattern()
    {
        var chunk = Cheap.ChunkSize;
        int[] edges = [0, 1, chunk - 1, chunk, chunk + 1, (2 * chunk) - 1, 2 * chunk, (2 * chunk) + 1, 5 * chunk];
        FuzzKit.Check(
            seed: 101,
            iterations: 60,
            g =>
            {
                var size = g.Bool(40) ? g.Pick(edges) : g.Int(0, 10 * chunk);
                var content = new byte[size];
                new Random(g.Seed).NextBytes(content);
                var splits = Enumerable.Range(0, g.Int(4)).Select(_ => g.Int(0, (2 * chunk) + 2)).ToArray();
                return (content, splits);
            },
            input =>
            {
                var file = Encrypt(input.content, Cheap, splits: input.splits);
                var (metadata, back) = Decrypt(file);
                back.Should().Equal(input.content);
                metadata.Size.Should().Be(input.content.Length);
                metadata.Name.Should().Be("f.bin");
            },
            show: i => $"size {i.content.Length}, splits [{string.Join(',', i.splits)}]");
    }

    [Fact]
    public void Round_trips_the_stored_name()
    {
        FuzzKit.Check(
            seed: 102,
            iterations: 60,
            g => g.NastyString(4),
            name =>
            {
                var options = Cheap with { ChunkSize = 64 * 1024 };
                var file = Encrypt([1, 2, 3], options, name: name);
                var (metadata, _) = Decrypt(file);

                // UTF-8 cannot carry a lone surrogate: it comes back as U+FFFD, as any UTF-8 text would.
                var expected = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(name));
                metadata.Name.Should().Be(expected);
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void A_damaged_file_fails_cleanly_and_never_yields_other_plaintext()
    {
        var content = new byte[(3 * Cheap.ChunkSize) + 17];
        new Random(5).NextBytes(content);
        var valid = Encrypt(content, Cheap);
        FuzzKit.Check(
            seed: 103,
            iterations: 400,
            g => KeepKdfCheap(g.Mutate(valid), valid),
            mutated =>
            {
                if (mutated.AsSpan().SequenceEqual(valid))
                {
                    return;
                }

                byte[] back;
                try
                {
                    (_, back) = Decrypt(mutated);
                }
                catch (Exception ex) when (ex is PceFormatException or PceWrongPasswordException)
                {
                    return;
                }

                // A mutation that still opens must be one the format cannot see (none exist: every byte is authenticated).
                back.Should().Equal(content, "a damaged file that opens must give the original content");
                throw new InvalidOperationException("A modified file opened without an error.");
            },
            show: FuzzKit.Hex);
    }

    [Fact]
    public void Random_bytes_and_random_headers_fail_fast_with_a_format_error()
    {
        FuzzKit.Check(
            seed: 104,
            iterations: 300,
            g =>
            {
                var bytes = g.Bytes(200);
                if (g.Bool(80) && bytes.Length >= PceFormat.HeaderSize)
                {
                    "PCE1"u8.CopyTo(bytes);
                    bytes[5] = (byte)g.Int(3);
                    bytes[6] = (byte)g.Int(3);

                    // Keep accepted parameters cheap; rejected ones may be anything.
                    var cost = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
                    var argonOk = bytes[5] == 0 && cost <= PasswordKdf.MaxArgon2MemoryKiB && bytes[12] is > 0 and <= PasswordKdf.MaxArgon2Time && bytes[13] is > 0 and <= PasswordKdf.MaxArgon2Parallelism;
                    var pbkdfOk = bytes[5] == 1 && cost is >= 1000 and <= PasswordKdf.MaxPbkdf2Iterations;
                    if (argonOk || pbkdfOk)
                    {
                        if (g.Bool())
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), bytes[5] == 0 ? 64u : 1000u);
                            bytes[12] = 1;
                            bytes[13] = 1;
                        }
                        else
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), uint.MaxValue - (uint)g.Int(1000));
                        }
                    }

                    // Mostly small blocks, sometimes the largest the header allows (the buffers are sized from it).
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(30), g.Bool(90) ? (uint)g.Int(0, 300) : (uint)g.Int(0, int.MaxValue));
                }

                return bytes;
            },
            bytes =>
            {
                var watch = Stopwatch.StartNew();
                var act = () => Decrypt(bytes);
                act.Should().Throw<IOException>().Which.Should().Match<IOException>(e => e is PceFormatException || e is PceWrongPasswordException);
                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
            },
            show: FuzzKit.Hex);
    }

    [Fact]
    public void Header_costs_above_the_caps_are_refused_before_any_key_is_derived()
    {
        var valid = Encrypt([1, 2, 3], Cheap);
        foreach (var (kdf, cost, time, lanes) in new (byte, uint, byte, byte)[]
        {
            (0, PasswordKdf.MaxArgon2MemoryKiB + 1, 1, 1), (0, 64, PasswordKdf.MaxArgon2Time + 1, 1), (0, 64, 1, PasswordKdf.MaxArgon2Parallelism + 1),
            (0, 64, 0, 1), (0, 64, 1, 0), (0, 7, 1, 1), (0, uint.MaxValue, 255, 255),
            (1, PasswordKdf.MaxPbkdf2Iterations + 1, 0, 0), (1, 999, 0, 0), (1, uint.MaxValue, 0, 0), (1, 0, 0, 0),
        })
        {
            var bytes = (byte[])valid.Clone();
            bytes[5] = kdf;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), cost);
            bytes[12] = time;
            bytes[13] = lanes;
            var watch = Stopwatch.StartNew();
            var act = () => Decrypt(bytes);
            act.Should().Throw<PceFormatException>($"kdf {kdf}, cost {cost}, time {time}, lanes {lanes}");
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        }
    }

    [Fact]
    public void A_damaged_compressed_file_fails_cleanly()
    {
        var content = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Plain Commander zstd ", 200)));
        var source = _s.File("text.txt", System.Text.Encoding.UTF8.GetString(content));
        var pce = source + PceFormat.Extension;
        PceFile.Encrypt(source, pce, Password, Cheap with { ChunkSize = 256 }, PceCompression.Always, null, TestContext.Current.CancellationToken);
        var valid = File.ReadAllBytes(pce);
        var damaged = _s.P("damaged.pce");
        FuzzKit.Check(
            seed: 105,
            iterations: 150,
            g => KeepKdfCheap(g.Mutate(valid), valid),
            mutated =>
            {
                File.WriteAllBytes(damaged, mutated);
                using var output = new MemoryStream();
                try
                {
                    PceFile.CopyContent(damaged, Password, output, null, TestContext.Current.CancellationToken);
                }
                catch (Exception ex) when (ex is PceFormatException or PceWrongPasswordException)
                {
                    return;
                }

                output.ToArray().Should().Equal(content);
            },
            show: FuzzKit.Hex);
    }

    private static byte[] TarHeader(string name, char type, int size)
    {
        var header = new byte[512];
        System.Text.Encoding.UTF8.GetBytes(name).CopyTo(header, 0);
        System.Text.Encoding.ASCII.GetBytes(Convert.ToString(size, 8).PadLeft(11, '0')).CopyTo(header, 124);
        System.Text.Encoding.ASCII.GetBytes("00000000000").CopyTo(header, 136);
        header[156] = (byte)type;
        return header;
    }

    /// <summary>Found by the fuzzer (seed 106): a GNU long name with a NUL inside made Path.GetFullPath throw ArgumentException.</summary>
    [Fact]
    public void Regression_tar_long_name_with_a_NUL_is_a_format_error()
    {
        var longName = "a\0b"u8.ToArray();
        byte[] tar = [.. TarHeader("././@LongLink", 'L', longName.Length), .. longName, .. new byte[512 - longName.Length], .. TarHeader("x", '0', 0), .. new byte[1024]];
        var file = _s.P("nul.pce");
        File.WriteAllBytes(file, Encrypt(tar, Cheap, PceLayout.Folder, "src"));

        var act = () => PceFile.Decrypt(file, Password, _s.P("out"), null, TestContext.Current.CancellationToken);

        act.Should().Throw<PceFormatException>();
        Directory.Exists(_s.P("out")).Should().BeFalse();
    }

    /// <summary>
    /// Browsing a .pce (Enter) wrote the TAR to Path.Combine(temp, storedName + ".tar"): a stored name like "../../x"
    /// or "C:\x" put it outside the temp folder. The stored name is now reduced to a plain file name or refused.
    /// </summary>
    [Theory]
    [InlineData("../../x", "x")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("..", null)]
    [InlineData(".", null)]
    [InlineData("", null)]
    [InlineData("/", null)]
    [InlineData("x\0y", null)]
    [InlineData("plain.txt", "plain.txt")]
    public void Regression_stored_name_is_a_plain_file_name(string stored, string? expected)
    {
        PceFile.PlainName(stored).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"..\..\x", "x")]
    [InlineData(@"C:\Windows\x", "x")]
    [InlineData("C:x", null)]
    [InlineData("...", null)]
    [InlineData(". .", null)]
    [InlineData("a:b", null)]
    public void Regression_stored_name_is_a_plain_file_name_on_windows(string stored, string? expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows path rules");
        PceFile.PlainName(stored).Should().Be(expected);
    }

    [Fact]
    public void Stored_names_are_always_plain_or_refused()
    {
        FuzzKit.Check(
            seed: 107,
            iterations: 500,
            g => g.NastyString(),
            name =>
            {
                if (PceFile.PlainName(name) is not { } plain)
                {
                    return;
                }

                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pc-root"));
                FileSystem.ExactPath.Full(Path.Combine(root, plain + ".tar")).Should().StartWith(root + Path.DirectorySeparatorChar);
                Path.GetFileName(plain).Should().Be(plain);
            },
            FuzzKit.ShrinkString);
    }

    /// <summary>
    /// The TAR inside a folder container is authenticated, but whoever made the file (and gave out the password)
    /// wrote it: a crafted TAR must fail with a format error and never write outside the target folder.
    /// </summary>
    [Fact]
    public void A_crafted_folder_tar_fails_cleanly_and_stays_inside_the_target()
    {
        _s.File("src/a.txt", "alpha");
        _s.File("src/sub/b.txt", "beta");
        _s.Dir("src/empty");
        _s.File("src/" + new string('n', 120) + "/long.txt", "long name");
        var folderPce = _s.P("folder.pce");
        PceFile.Encrypt(_s.P("src"), folderPce, Password, Cheap with { ChunkSize = 4096 }, PceCompression.None, null, TestContext.Current.CancellationToken);
        using var tarStream = new MemoryStream();
        PceFile.CopyContent(folderPce, Password, tarStream, null, TestContext.Current.CancellationToken);
        var tar = tarStream.ToArray();
        var work = _s.Dir("work");
        var token = TestContext.Current.CancellationToken;

        FuzzKit.Check(
            seed: 106,
            iterations: 120,
            g =>
            {
                var mutated = g.Mutate(tar);
                if (g.Bool(30) && mutated.Length > 600)
                {
                    // Aim at a header's name field, with something path-like.
                    var at = 512 * g.Int(Math.Max(1, mutated.Length / 512));
                    var name = System.Text.Encoding.UTF8.GetBytes(g.Pick(["../x", "..\\x", "/etc/x", "a/../../x", "C:x", "C:\\x", "a\0b", "a:b", "...", " ", "a/./b", "\\\\srv\\s\\x", "x/"]));
                    name.AsSpan(0, Math.Min(name.Length, Math.Max(0, Math.Min(100, mutated.Length - at)))).CopyTo(mutated.AsSpan(at));
                }

                return mutated;
            },
            mutated =>
            {
                var caseDir = Path.Combine(work, "c");
                if (Directory.Exists(caseDir))
                {
                    Directory.Delete(caseDir, recursive: true);
                }

                Directory.CreateDirectory(Path.Combine(caseDir, "a", "b"));
                var file = Path.Combine(caseDir, "in.pce");
                File.WriteAllBytes(file, Encrypt(mutated, Cheap with { ChunkSize = 4096 }, PceLayout.Folder, "src"));
                var target = Path.Combine(caseDir, "a", "b", "out");
                try
                {
                    PceFile.Decrypt(file, Password, target, null, token);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // PceFormatException, or the disk refusing a name: a file where a folder is (IOException), a
                    // folder where a file is (UnauthorizedAccessException, as .NET reports opening a folder for writing)
                }

                var stray = Directory.EnumerateFileSystemEntries(caseDir, "*", SearchOption.AllDirectories)
                    .Select(p => Path.GetRelativePath(caseDir, p).Replace('\\', '/'))
                    .Where(p => p is not ("in.pce" or "a" or "a/b") && !p.StartsWith("a/b/out", StringComparison.Ordinal))
                    .ToList();
                string.Join(" | ", stray).Should().BeEmpty();
            },
            show: FuzzKit.Hex);
    }
}
