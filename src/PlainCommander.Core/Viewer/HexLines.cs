namespace PlainCommander.Core.Viewer;

/// <summary>
/// A file as hex-dump lines read on demand from a seekable stream. The viewer shows one page (the rows
/// that fit on screen) and moves a line offset, so a multi-gigabyte file costs one small read per page.
/// A list control holding all rows would realize them without end. UI-thread only.
/// </summary>
public sealed class HexLines : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;

    /// <param name="bytesPerLine">16 for the F3 window, 8 where the room is narrow (the quick view).</param>
    public HexLines(Stream stream, int bytesPerLine = HexFormat.BytesPerLine)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        BytesPerLine = bytesPerLine is 8 or 16 ? bytesPerLine : throw new ArgumentOutOfRangeException(nameof(bytesPerLine));
        _buffer = new byte[bytesPerLine];
        LineCount = (stream.Length + bytesPerLine - 1) / bytesPerLine;
    }

    public static HexLines Open(string path, int bytesPerLine = HexFormat.BytesPerLine) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess), bytesPerLine);

    public int BytesPerLine { get; }

    public long LineCount { get; }

    public long Length => _stream.Length;

    public string Line(long index)
    {
        if (index < 0 || index >= LineCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var offset = index * BytesPerLine;
        _stream.Position = offset;
        var read = _stream.ReadAtLeast(_buffer, BytesPerLine, throwOnEndOfStream: false);
        return HexFormat.Line(offset, _buffer.AsSpan(0, read), BytesPerLine);
    }

    /// <summary>Up to <paramref name="count"/> lines from <paramref name="first"/>; shorter at the end of the file.</summary>
    public string[] Page(long first, int count)
    {
        if (first < 0 || count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(first));
        }

        var n = (int)Math.Max(0, Math.Min(count, LineCount - first));
        var lines = new string[n];
        for (var i = 0; i < n; i++)
        {
            lines[i] = Line(first + i);
        }

        return lines;
    }

    public void Dispose() => _stream.Dispose();
}
