using System.Buffers.Binary;
using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>Guards for the archive and .pce fixes that came out of the odd-combinations report.</summary>
public sealed class ArchiveCryptoFixTests : IDisposable
{
    private const string Password = "tajne";
    private readonly Sandbox _s = new();
    private readonly List<string> _archives = [];

    public void Dispose()
    {
        foreach (var archive in _archives)
        {
            ArchiveCatalog.Forget(archive);
        }

        _s.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Zip(string name, params string[] keys)
    {
        var path = _s.P(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var key in keys)
            {
                using var writer = new StreamWriter(zip.CreateEntry(key).Open());
                writer.Write(key);
            }
        }

        _archives.Add(path);
        return path;
    }

    [Theory]
    [InlineData("a/b", true)]
    [InlineData("./a/./b/", true)]
    [InlineData("/a", true)]
    [InlineData("..", false)]
    [InlineData("a/../b", false)]
    [InlineData("a\\..\\..\\b", false)]
    public void Contained_path_refuses_parent_segments(string key, bool inside)
    {
        var root = _s.P("root");
        var joined = ContainedPath.Join(root, key);
        if (inside)
        {
            joined.Should().StartWith(root + Path.DirectorySeparatorChar);
        }
        else
        {
            joined.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("C:/x")]
    [InlineData("C:x")]
    [InlineData("a/.../b")]
    [InlineData(". ./b")]
    public void Contained_path_refuses_drives_and_dot_names_on_windows(string key)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive letters and trailing-dot trimming are Windows rules");
        ContainedPath.Join(@"D:\out", key).Should().BeNull();
    }

    [Fact]
    public void Contained_path_keeps_unc_looking_keys_inside()
    {
        var root = _s.P("root");
        ContainedPath.Join(root, ArchivePath.NormalizeKey(@"\\server\share\x")).Should().Be(Path.Combine(root, "server", "share", "x"));
    }

    [Fact]
    public async Task An_escaping_entry_is_reported_once_and_the_rest_is_extracted()
    {
        var zip = Zip(Path.Combine("in", "evil.zip"), "ok.txt", "../../escaped.txt", "sub/keep.txt");
        var target = _s.Dir("out");

        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);
        var result = await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null), ownFolders: false), prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors.Should().ContainSingle().Which.Should().BeOfType<ArchiveEntryOutsideException>();
        File.Exists(Path.Combine(target, "ok.txt")).Should().BeTrue();
        File.Exists(Path.Combine(target, "sub", "keep.txt")).Should().BeTrue();
        File.Exists(_s.P("escaped.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData(8, 2u * 1024 * 1024)] // Argon2 memory: 2 GiB
    [InlineData(12, 200u)] // Argon2 passes
    [InlineData(13, 200u)] // Argon2 lanes
    public void Crafted_argon2_cost_in_the_header_is_rejected_before_deriving(int offset, uint value)
    {
        var source = _s.File("k.txt", "secret");
        var pce = source + PceFormat.Extension;
        PceFile.Encrypt(source, pce, Password, PceOptions.Fast, PceCompression.None, null, Token);
        var bytes = File.ReadAllBytes(pce);
        if (offset == 8)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
        }
        else
        {
            bytes[offset] = (byte)value;
        }

        File.WriteAllBytes(pce, bytes);
        var act = () => PceFile.Peek(pce, Password);
        act.Should().Throw<PceFormatException>().WithMessage("*Unreasonable*");
    }

    [Fact]
    public void Crafted_pbkdf2_iterations_in_the_header_are_rejected_before_deriving()
    {
        var source = _s.File("p.txt", "secret");
        var pce = source + PceFormat.Extension;
        PceFile.Encrypt(source, pce, Password, PceOptions.Fast with { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000 }, PceCompression.None, null, Token);
        var bytes = File.ReadAllBytes(pce);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 40_000_000);
        File.WriteAllBytes(pce, bytes);
        var act = () => PceFile.Peek(pce, Password);
        act.Should().Throw<PceFormatException>().WithMessage("*Unreasonable*");
    }

    [Fact]
    public void The_default_cost_is_within_the_caps()
    {
        var o = PceOptions.Default;
        ((uint)o.Argon2MemoryKiB).Should().BeLessThan(PasswordKdf.MaxArgon2MemoryKiB);
        o.Argon2Time.Should().BeLessThan(PasswordKdf.MaxArgon2Time);
        o.Argon2Parallelism.Should().BeLessThan(PasswordKdf.MaxArgon2Parallelism);
        ((uint)o.Pbkdf2Iterations).Should().BeLessThan(PasswordKdf.MaxPbkdf2Iterations);
    }

    [Fact]
    public void Encrypt_with_an_empty_password_leaves_no_target()
    {
        var source = _s.File("e.txt", "x");
        var target = source + PceFormat.Extension;
        var act = () => PceFile.Encrypt(source, target, string.Empty, PceOptions.Fast, PceCompression.None, null, Token);
        act.Should().Throw<ArgumentException>();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public void Damaged_folder_container_over_an_existing_folder_leaves_its_files_untouched()
    {
        var folder = _s.Dir("docs");
        File.WriteAllBytes(Path.Combine(folder, "big.bin"), new byte[300 * 1024]);
        var pce = folder + PceFormat.Extension;
        PceFile.Encrypt(folder, pce, Password, PceOptions.Fast, PceCompression.None, null, Token);
        var bytes = File.ReadAllBytes(pce);
        bytes[^100] ^= 1;
        File.WriteAllBytes(pce, bytes);
        File.WriteAllText(Path.Combine(folder, "big.bin"), "the user's newer copy");

        var act = () => PceFile.Decrypt(pce, Password, folder, null, Token);

        act.Should().Throw<PceFormatException>();
        File.ReadAllText(Path.Combine(folder, "big.bin")).Should().Be("the user's newer copy");
        Directory.GetFileSystemEntries(_s.Root).Should().BeEquivalentTo([folder, pce], "no temporary folder is left next to it");
    }

    [Fact]
    public void Folder_container_merges_into_an_existing_folder()
    {
        var folder = _s.Dir("merge");
        _s.File(Path.Combine("merge", "a.txt"), "a1");
        _s.File(Path.Combine("merge", "sub", "b.txt"), "b1");
        var pce = folder + PceFormat.Extension;
        PceFile.Encrypt(folder, pce, Password, PceOptions.Fast, PceCompression.None, null, Token);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "changed");
        _s.File(Path.Combine("merge", "sub", "own.txt"), "own");

        PceFile.Decrypt(pce, Password, folder, null, Token);

        _s.Read(Path.Combine("merge", "a.txt")).Should().Be("a1");
        _s.Read(Path.Combine("merge", "sub", "b.txt")).Should().Be("b1");
        _s.Read(Path.Combine("merge", "sub", "own.txt")).Should().Be("own");
        Directory.GetFileSystemEntries(_s.Root).Should().BeEquivalentTo([folder, pce]);
    }

    [Fact]
    public async Task Encrypt_operation_keeps_an_unrelated_part_file()
    {
        var source = _s.File("film.mkv", "film");
        var users = _s.File("film.mkv.pce.part", "the user's own");

        var result = await Run.Async(new EncryptOperation([source], PceFormat.Extension, Password, options: PceOptions.Fast));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("film.mkv.pce.part").Should().Be("the user's own");
        File.Exists(users + ".pce").Should().BeFalse();
        Directory.GetFiles(_s.Root, "*.pc-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Pack_operation_keeps_an_unrelated_part_file()
    {
        var source = _s.File("a.txt", "a");
        _s.File("a.zip.part", "the user's own");

        var result = await Run.Async(new ArchivePackOperation([source], _s.P("a.zip")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("a.zip.part").Should().Be("the user's own");
        File.Exists(_s.P("a.zip")).Should().BeTrue();
        Directory.GetFiles(_s.Root, "*.pc-*").Should().BeEmpty();
    }
}
