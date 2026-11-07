using System.Buffers.Binary;
using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Weird;
using Xunit;

namespace PlainCommander.Core.Tests.Volumes;

/// <summary>Bugs the file-system matrix found, reproduced without special volumes.</summary>
public sealed class VolumeFindingsTests : IDisposable
{
    private readonly WeirdBox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task Regression_zip_marks_non_ascii_names_as_utf8()
    {
        // Without the UTF-8 flag (general purpose bit 11) 7-Zip, Explorer and the app's own 7-Zip reader decode
        // the name in the OEM code page: "zażółć.txt" came back as "za┼╝├│┼é─ç.txt".
        var file = Path.Combine(_s.Dir("in"), "zażółć 日本 😀.txt");
        File.WriteAllText(file, "x");
        var zip = _s.P("names.zip");

        var result = await new ArchivePackOperation([file], zip)
            .RunAsync(new OperationContext(null, new HookPrompts(), new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        var bytes = File.ReadAllBytes(zip);
        BinaryPrimitives.ReadUInt32LittleEndian(bytes).Should().Be(0x04034b50u, "a local file header comes first");
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26));
        Encoding.UTF8.GetString(bytes, 30, nameLength).Should().Be("zażółć 日本 😀.txt");
        (flags & 0x0800).Should().Be(0x0800, "the name is UTF-8 and the header must say so");
    }

    [Fact]
    public async Task Regression_skip_identical_accepts_the_2_second_FAT_time_step()
    {
        // A file copied to FAT gets its time rounded to 2 s. Continuing as administrator (skipIdentical) took
        // such a copy for a different file and asked about overwriting it.
        var source = Path.Combine(_s.Dir("src"), "photo.jpg");
        File.WriteAllText(source, "same bytes");
        var at = new DateTime(2025, 3, 7, 10, 11, 12, 300, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, at);
        var target = Path.Combine(_s.Dir("dst"), "photo.jpg");
        File.WriteAllText(target, "same bytes");
        File.SetLastWriteTimeUtc(target, at.AddMilliseconds(1700));

        var result = await new TransferOperation(TransferKind.Copy, [source], new TransferTarget(_s.P("dst"), null), skipIdentical: true)
            .RunAsync(new OperationContext(null, new HookPrompts(), new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        File.GetLastWriteTimeUtc(target).Should().Be(at.AddMilliseconds(1700), "the copy is already there and is left alone");
    }
}
