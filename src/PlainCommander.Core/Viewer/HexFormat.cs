using System.Text;

namespace PlainCommander.Core.Viewer;

/// <summary>Classic hex dump lines: offset, bytes (16 in two groups of eight, or 8 for narrow places), printable ASCII.</summary>
public static class HexFormat
{
    public const int BytesPerLine = 16;

    public static string Line(long offset, ReadOnlySpan<byte> bytes, int bytesPerLine = BytesPerLine)
    {
        var sb = new StringBuilder(80);
        sb.Append(offset.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("  ");
        for (var i = 0; i < bytesPerLine; i++)
        {
            if (i == 8)
            {
                sb.Append(' ');
            }

            if (i < bytes.Length)
            {
                sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("  ");
            }

            sb.Append(' ');
        }

        sb.Append(' ');
        foreach (var b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }

        return sb.ToString();
    }
}
