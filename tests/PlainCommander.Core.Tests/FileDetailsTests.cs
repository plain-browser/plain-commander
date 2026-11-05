using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Details;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class FileDetailsTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public void Column_texts_and_order()
    {
        var culture = CultureInfo.InvariantCulture;
        DetailFormatter.Text(new FileDetails(4032, 3024), DetailColumn.Dimensions, culture).Should().Be("4032 × 3024");
        DetailFormatter.Text(new FileDetails(Duration: TimeSpan.FromSeconds(187)), DetailColumn.Duration, culture).Should().Be("3:07");
        DetailFormatter.Text(new FileDetails(Duration: new TimeSpan(1, 2, 45)), DetailColumn.Duration, culture).Should().Be("1:02:45");
        DetailFormatter.Text(FileDetails.None, DetailColumn.Version, culture).Should().BeEmpty();

        DetailFormatter.Compare(new FileDetails(Version: "10.0.1"), new FileDetails(Version: "9.2"), DetailColumn.Version).Should().BePositive("versions compare as numbers");
        DetailFormatter.Compare(new FileDetails(10, 10), new FileDetails(20, 2), DetailColumn.Dimensions).Should().BePositive("by pixel count");
        DetailFormatter.HasValue(FileDetails.None, DetailColumn.Dimensions).Should().BeFalse();
    }

    [Fact]
    public void The_filter_tests_photo_dates_and_lengths_from_the_details()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Local);
        var file = new FileEntry { Name = "a.jpg", FullPath = "/a.jpg", IsDirectory = false, Modified = now };
        var taken2024 = new PanelFilter
        {
            DateField = FilterDateField.Taken,
            DatePreset = FilterDatePreset.Range,
            From = new DateOnly(2024, 1, 1),
            To = new DateOnly(2024, 12, 31),
        };
        var match = taken2024.DetailMatcher(now);

        taken2024.NeedsDetails.Should().BeTrue();
        match(file, new FileDetails(Taken: new DateTimeOffset(new DateTime(2024, 7, 1, 10, 0, 0, DateTimeKind.Local)))).Should().BeTrue();
        match(file, new FileDetails(Taken: new DateTimeOffset(new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Local)))).Should().BeFalse();
        match(file, null).Should().BeFalse("a file without a photo date does not pass");

        var longer = new PanelFilter { MinDuration = TimeSpan.FromMinutes(10) }.DetailMatcher(now);
        longer(file, new FileDetails(Duration: TimeSpan.FromMinutes(12))).Should().BeTrue();
        longer(file, new FileDetails(Duration: TimeSpan.FromMinutes(3))).Should().BeFalse();
        new PanelFilter { MinDuration = TimeSpan.FromMinutes(1) }.IsEmpty.Should().BeFalse();
        new PanelFilter().NeedsDetails.Should().BeFalse();
    }

    [Fact]
    public void Lengths_are_typed_in_minutes()
    {
        PanelFilter.ParseMinutes("90").Should().Be(TimeSpan.FromMinutes(90));
        PanelFilter.ParseMinutes("1:30").Should().Be(TimeSpan.FromSeconds(90));
        PanelFilter.ParseMinutes("1:02:03").Should().Be(new TimeSpan(1, 2, 3));
        PanelFilter.ParseMinutes("2,5").Should().Be(TimeSpan.FromSeconds(150));
        PanelFilter.ParseMinutes("").Should().BeNull();
        var act = () => PanelFilter.ParseMinutes("długo");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Windows_reads_image_size_recording_length_and_program_version()
    {
        if (OperatingSystem.IsWindows())
        {
            ReadRealFiles();
        }
        else
        {
            Assert.Skip("the Windows property system");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ReadRealFiles()
    {
        var png = _s.P("picture.png");
        File.WriteAllBytes(png, Png(37, 21));
        var wav = _s.P("sound.wav");
        File.WriteAllBytes(wav, Wav(seconds: 3));
        var dll = Path.Combine(AppContext.BaseDirectory, "7z.dll");
        var provider = new WindowsFileDetails();

        var image = provider.Read(png);
        var sound = provider.Read(wav);
        var program = provider.Read(dll);
        var text = provider.Read(_s.File("note.txt", "x"));

        (image.Width, image.Height).Should().Be((37, 21));
        sound.Duration!.Value.TotalSeconds.Should().BeApproximately(3, 0.1);
        program.Version.Should().StartWith("26.");
        text.Should().Be(FileDetails.None);
        provider.Read(_s.P("missing.png")).Should().Be(FileDetails.None, "an unreadable file has no details, no exception");
    }

    /// <summary>A valid grey PNG of the given size.</summary>
    private static byte[] Png(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // greyscale
        Chunk(output, "IHDR", header);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(new byte[width]);
            }
        }

        Chunk(output, "IDAT", raw.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        output.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>Silence: 8 kHz, mono, 16-bit PCM.</summary>
    private static byte[] Wav(int seconds)
    {
        const int rate = 8000;
        var data = rate * 2 * seconds;
        using var output = new MemoryStream();
        using var w = new BinaryWriter(output);
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data);
        w.Write(new byte[data]);
        w.Flush();
        return output.ToArray();
    }
}
