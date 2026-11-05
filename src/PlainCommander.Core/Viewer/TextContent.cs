using System.Text;

namespace PlainCommander.Core.Viewer;

/// <summary>A text file decoded for the viewer, with what was detected about it.</summary>
public sealed record TextContent(string Text, Encoding Encoding, bool Truncated, long FileLength)
{
    /// <summary>Text mode reads at most this much; bigger files open as hex and can be switched to text up to here.</summary>
    public const int MaxTextBytes = 16 * 1024 * 1024;

    public const int SampleBytes = 64 * 1024;

    public static async Task<TextContent> LoadAsync(string path, Encoding? encoding, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var toRead = (int)Math.Min(length, MaxTextBytes);
        var bytes = new byte[toRead];
        var read = await stream.ReadAtLeastAsync(bytes, toRead, throwOnEndOfStream: false, token);
        encoding ??= TextDecoder.Detect(bytes.AsSpan(0, Math.Min(read, SampleBytes)));
        var preamble = encoding.GetPreamble();
        var start = preamble.Length > 0 && bytes.AsSpan(0, read).StartsWith(preamble) ? preamble.Length : 0;
        var text = encoding.GetString(bytes, start, read - start);
        return new TextContent(text, encoding, length > toRead, length);
    }

    /// <summary>The first bytes, for mode and encoding detection without reading the whole file.</summary>
    public static async Task<byte[]> SampleAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var bytes = new byte[(int)Math.Min(stream.Length, SampleBytes)];
        var read = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, token);
        return read == bytes.Length ? bytes : bytes[..read];
    }
}
