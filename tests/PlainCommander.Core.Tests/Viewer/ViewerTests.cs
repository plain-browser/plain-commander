using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Viewer;
using Xunit;

namespace PlainCommander.Core.Tests.Viewer;

public sealed class ViewerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-viewer-").FullName;

    static ViewerTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // the tests below build 1250 bytes themselves
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Detects_utf8_with_and_without_bom()
    {
        TextDecoder.Detect(new UTF8Encoding(true).GetPreamble().Concat("zażółć"u8.ToArray()).ToArray()).GetPreamble().Should().NotBeEmpty();
        TextDecoder.Detect("zażółć gęślą jaźń"u8).WebName.Should().Be("utf-8");
    }

    [Fact]
    public void Detects_utf16_by_bom_and_by_zero_pattern()
    {
        TextDecoder.Detect(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hi")).ToArray()).Should().Be(Encoding.Unicode);
        TextDecoder.Detect(Encoding.Unicode.GetBytes("plain ascii text without a bom")).Should().Be(Encoding.Unicode);
        TextDecoder.Detect(Encoding.BigEndianUnicode.GetBytes("plain ascii text without a bom")).Should().Be(Encoding.BigEndianUnicode);
    }

    [Fact]
    public void Invalid_utf8_falls_back_to_the_ansi_code_page()
    {
        var cp1250 = Encoding.GetEncoding(1250).GetBytes("Zażółć gęślą jaźń");
        TextDecoder.IsValidUtf8(cp1250).Should().BeFalse();
        TextDecoder.Detect(cp1250).Should().Be(TextDecoder.Ansi());
    }

    [Fact]
    public void Binary_is_told_by_zero_bytes_but_utf16_is_not_binary()
    {
        TextDecoder.LooksBinary(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 }).Should().BeTrue();
        TextDecoder.LooksBinary("hello"u8).Should().BeFalse();
        TextDecoder.LooksBinary(Encoding.Unicode.GetBytes("hello world, utf-16")).Should().BeFalse();
        TextDecoder.LooksBinary([]).Should().BeFalse();
    }

    [Fact]
    public void Mode_is_image_by_extension_then_hex_for_binaries_then_text()
    {
        ViewerModeDetector.Detect(@"C:\x\photo.JPG", "not really"u8).Should().Be(ViewerMode.Image);
        ViewerModeDetector.Detect(@"C:\x\app.exe", new byte[] { 0x4D, 0x5A, 0, 0 }).Should().Be(ViewerMode.Hex);
        ViewerModeDetector.Detect(@"C:\x\notes.txt", "hello"u8).Should().Be(ViewerMode.Text);
    }

    [Fact]
    public void Hex_line_has_offset_two_groups_and_ascii_column()
    {
        var line = HexFormat.Line(0x10, "Hello, World!"u8);

        line.Should().Be("00000010  48 65 6C 6C 6F 2C 20 57  6F 72 6C 64 21           Hello, World!");
    }

    [Fact]
    public void Narrow_hex_lines_carry_eight_bytes()
    {
        var path = Write("eight.dat", Enumerable.Range(0, 20).Select(static i => (byte)(0x41 + i)).ToArray());

        using var lines = HexLines.Open(path, 8);

        lines.LineCount.Should().Be(3);
        lines.Line(1).Should().Be("00000008  49 4A 4B 4C 4D 4E 4F 50  IJKLMNOP");
        lines.Line(2).Should().Be("00000010  51 52 53 54              QRST");
    }

    [Fact]
    public void Hex_lines_cover_the_file_and_format_on_demand()
    {
        var bytes = Enumerable.Range(0, 40).Select(static i => (byte)i).ToArray();
        var path = Write("bin.dat", bytes);

        using var lines = HexLines.Open(path);

        lines.LineCount.Should().Be(3);
        lines.Line(0).Should().StartWith("00000000  00 01 02 03");
        lines.Line(2).Should().StartWith("00000020  20 21 22 23 24 25 26 27                           ");
        lines.Line(2).Should().EndWith(" !\"#$%&'");
        lines.Page(1, 10).Should().HaveCount(2, "the page is cut at the end of the file");
        lines.Page(1, 10)[0].Should().StartWith("00000010");
        lines.Page(3, 10).Should().BeEmpty();
    }

    [Fact]
    public async Task Text_content_strips_the_bom_and_reports_truncation()
    {
        var path = Write("bom.txt", new UTF8Encoding(true).GetPreamble().Concat("Zażółć"u8.ToArray()).ToArray());

        var content = await TextContent.LoadAsync(path, encoding: null, CancellationToken.None);

        content.Text.Should().Be("Zażółć");
        content.Truncated.Should().BeFalse();
        content.FileLength.Should().Be(3 + "Zażółć"u8.Length);
    }

    [Fact]
    public async Task Text_content_can_be_redecoded_with_a_chosen_encoding()
    {
        var path = Write("cp1250.txt", Encoding.GetEncoding(1250).GetBytes("Gęślą"));

        var wrong = await TextContent.LoadAsync(path, Encoding.Latin1, CancellationToken.None);
        var right = await TextContent.LoadAsync(path, Encoding.GetEncoding(1250), CancellationToken.None);

        wrong.Text.Should().NotBe("Gęślą");
        right.Text.Should().Be("Gęślą");
    }
}
