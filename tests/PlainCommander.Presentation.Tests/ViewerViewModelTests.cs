using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Viewer;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class ViewerViewModelTests : IDisposable
{
    private readonly TempTree _tree = new();

    static ViewerViewModelTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task Text_file_opens_as_text_with_the_detected_encoding()
    {
        var path = Path.Combine(_tree.Root, "notes.txt");
        File.WriteAllText(path, "Zażółć gęślą jaźń");
        using var vm = new ViewerViewModel(path);

        await vm.LoadAsync();

        vm.Mode.Should().Be(ViewerMode.Text);
        vm.Text.Should().Be("Zażółć gęślą jaźń");
        vm.Encoding!.WebName.Should().Be("utf-8");
        vm.Status.Should().Contain("UTF-8");
        vm.HexPage.Should().BeEmpty();
        vm.Error.Should().BeNull();
    }

    [Fact]
    public async Task Binary_file_opens_as_hex_and_can_switch_to_text_and_back()
    {
        var path = Path.Combine(_tree.Root, "app.bin");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0xB8]);
        using var vm = new ViewerViewModel(path);

        await vm.LoadAsync();
        vm.Mode.Should().Be(ViewerMode.Hex);
        vm.HexLineCount.Should().Be(2);
        vm.HexPage.Should().HaveCount(2);
        vm.HexPage[1].Should().StartWith("00000010  B8");
        vm.CanShowImage.Should().BeFalse();

        await vm.SetModeAsync(ViewerMode.Text);
        vm.Mode.Should().Be(ViewerMode.Text);
        vm.HexPage.Should().BeEmpty("the hex stream is closed when leaving hex mode");
        vm.Text.Should().StartWith("MZ");

        await vm.SetModeAsync(ViewerMode.Hex);
        vm.HexPage.Should().HaveCount(2);
    }

    [Fact]
    public async Task Hex_shows_one_page_and_scrolls_by_line_page_and_end()
    {
        var path = Path.Combine(_tree.Root, "big.bin");
        File.WriteAllBytes(path, new byte[16 * 100 + 5]); // 101 rows
        using var vm = new ViewerViewModel(path);
        await vm.SetModeAsync(ViewerMode.Hex);
        vm.HexVisibleLines = 10;

        vm.HexLineCount.Should().Be(101);
        vm.HexPage.Should().HaveCount(10);
        vm.HexMaxTop.Should().Be(91);

        vm.HexPageDown();
        vm.HexTop.Should().Be(10);
        vm.HexPage[0].Should().StartWith("000000A0");

        vm.HexScrollBy(-3);
        vm.HexTop.Should().Be(7);

        vm.HexEnd();
        vm.HexTop.Should().Be(91);
        vm.HexPage.Should().HaveCount(10);
        vm.HexPage[^1].Should().StartWith("00000640  00 00 00 00 00 ");

        vm.HexHome();
        vm.HexTop.Should().Be(0);

        vm.HexVisibleLines = 200;
        vm.HexPage.Should().HaveCount(101, "the whole file fits");
        vm.HexMaxTop.Should().Be(0);
    }

    [Fact]
    public async Task Image_by_extension_opens_in_image_mode()
    {
        var path = Path.Combine(_tree.Root, "shot.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        using var vm = new ViewerViewModel(path);

        await vm.LoadAsync();

        vm.Mode.Should().Be(ViewerMode.Image);
        vm.CanShowImage.Should().BeTrue();
        vm.Status.Should().Contain("8 B");
    }

    [Fact]
    public async Task Choosing_an_encoding_redecodes_the_text()
    {
        var path = Path.Combine(_tree.Root, "cp1250.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1250).GetBytes("Gęślą"));
        using var vm = new ViewerViewModel(path);
        await vm.LoadAsync();

        await vm.SetEncodingAsync(Encoding.GetEncoding(1250));

        vm.Text.Should().Be("Gęślą");
        vm.Status.Should().Contain("WINDOWS-1250");
    }

    [Fact]
    public async Task Missing_file_reports_an_error_instead_of_throwing()
    {
        using var vm = new ViewerViewModel(Path.Combine(_tree.Root, "gone.txt"));

        await vm.LoadAsync();

        vm.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Narrow_rows_keep_the_place_in_the_file()
    {
        var path = Path.Combine(_tree.Root, "data.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray());
        using var vm = new ViewerViewModel(path) { HexVisibleLines = 10 };

        await vm.LoadAsync();

        vm.HexPage.Should().HaveCount(10);
        vm.HexScrollTo(4); // row 4 = byte 64
        vm.HexBytesPerLine = 8;
        vm.HexPage[0].Should().StartWith("00000040");
        vm.HexLineCount.Should().Be(32);
    }
}
