using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Tools;

/// <summary>Checksums, attributes and dates, binary comparison.</summary>
public sealed class FileToolsTests : IDisposable
{
    private static readonly DateTime T0 = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task Creates_a_checksum_file_with_relative_names_and_known_values()
    {
        _s.File("data/abc.txt", "abc");
        _s.File("data/sub/empty.txt", string.Empty);
        var output = _s.P("data", "data.sha256");

        var result = await Run.Async(new ChecksumCreateOperation([_s.P("data", "abc.txt"), _s.P("data", "sub")], output, ChecksumKind.Sha256));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllLines(output).Should().Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad *abc.txt",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 *sub/empty.txt");
    }

    [Theory]
    [InlineData(ChecksumKind.Md5, "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData(ChecksumKind.Sha1, "a9993e364706816aba3e25717850c26c9cd0d89d")]
    public async Task Other_algorithms_and_their_extensions(ChecksumKind kind, string hash)
    {
        _s.File("abc.txt", "abc");
        var output = _s.P("abc.txt" + ChecksumFormat.Extension(kind));

        await Run.Async(new ChecksumCreateOperation([_s.P("abc.txt")], output, kind));

        File.ReadAllText(output).Should().Be(hash + " *abc.txt\n");
        ChecksumFormat.KindOf(output).Should().Be(kind);
    }

    [Fact]
    public async Task Verifying_tells_good_changed_and_missing_files_apart()
    {
        _s.File("set/good.txt", "abc");
        _s.File("set/bad.txt", "abc");
        _s.File("set/deep/ok.txt", "abc");
        var sums = _s.P("set", "set.sha256");
        await Run.Async(new ChecksumCreateOperation([_s.P("set", "good.txt"), _s.P("set", "bad.txt"), _s.P("set", "deep")], sums, ChecksumKind.Sha256));
        File.WriteAllText(_s.P("set", "bad.txt"), "abd");
        File.AppendAllText(sums, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  gone.txt\n");

        var verify = new ChecksumVerifyOperation(sums);
        var result = await Run.Async(verify);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        verify.Report.Matched.Should().BeEquivalentTo(["good.txt", "deep/ok.txt"]);
        verify.Report.Mismatched.Should().Equal("bad.txt");
        verify.Report.Missing.Should().Equal("gone.txt");
    }

    [Fact]
    public void Reads_gnu_and_bsd_lines_and_skips_the_rest()
    {
        var entries = ChecksumFormat.Parse(
            "; comment\r\n" +
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD *a b.txt\r\n" +
            "SHA256 (c.txt) = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\n" +
            "not a checksum line\n");

        entries.Should().Equal(
            ("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "a b.txt"),
            ("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "c.txt"));
    }

    [Fact]
    public async Task Binary_compare_finds_the_first_difference()
    {
        File.WriteAllBytes(_s.P("a.bin"), [1, 2, 3, 4]);
        File.WriteAllBytes(_s.P("b.bin"), [1, 2, 9, 4]);
        File.WriteAllBytes(_s.P("c.bin"), [1, 2, 3, 4]);
        File.WriteAllBytes(_s.P("d.bin"), [1, 2, 3]);

        (await BinaryCompare.FirstDifferenceAsync(_s.P("a.bin"), _s.P("b.bin"))).Should().Be(2);
        (await BinaryCompare.FirstDifferenceAsync(_s.P("a.bin"), _s.P("c.bin"))).Should().BeNull();
        (await BinaryCompare.FirstDifferenceAsync(_s.P("a.bin"), _s.P("d.bin"))).Should().Be(3, "the shorter file ends there");
    }

    [Fact]
    public async Task Sets_read_only_and_the_date_through_a_tree()
    {
        _s.File("tree/a.txt");
        _s.File("tree/sub/b.txt");
        var change = new AttributeChange(FileAttributes.ReadOnly, 0, new DateTimeOffset(T0), Recursive: true);

        var result = await Run.Async(new AttributesOperation([_s.P("tree")], change));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(4);
        File.GetAttributes(_s.P("tree", "sub", "b.txt")).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        File.GetLastWriteTimeUtc(_s.P("tree", "sub", "b.txt")).Should().Be(T0);
        Directory.GetLastWriteTimeUtc(_s.P("tree", "sub")).Should().Be(T0);

        // and back: a read-only file still takes a new date
        var undo = new AttributeChange(0, FileAttributes.ReadOnly, new DateTimeOffset(T0.AddDays(1)), Recursive: true);
        await Run.Async(new AttributesOperation([_s.P("tree")], undo));
        File.GetAttributes(_s.P("tree", "a.txt")).HasFlag(FileAttributes.ReadOnly).Should().BeFalse();
        File.GetLastWriteTimeUtc(_s.P("tree", "a.txt")).Should().Be(T0.AddDays(1));
    }

    [Fact]
    public void Only_the_four_editable_attributes_change()
    {
        var change = new AttributeChange(FileAttributes.Hidden | FileAttributes.Compressed, FileAttributes.Archive | FileAttributes.Directory, null, false);

        change.Apply(FileAttributes.Directory | FileAttributes.Archive).Should().Be(FileAttributes.Directory | FileAttributes.Hidden);
    }
}
