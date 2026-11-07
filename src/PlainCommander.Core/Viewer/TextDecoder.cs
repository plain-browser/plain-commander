using System.Globalization;
using System.Text;

namespace PlainCommander.Core.Viewer;

/// <summary>
/// Guesses a text file's encoding from its first bytes: BOM first, then valid UTF-8, then UTF-16 by the
/// pattern of zero bytes, otherwise the system ANSI code page (1250 on a Polish Windows).
/// </summary>
public static class TextDecoder
{
    // Declared first: field initializers run in order, and the code-page provider must be in place
    // before anything below asks for 1250 or 852.
    private static readonly bool Registered = Register();

    private static readonly Lazy<IReadOnlyList<Encoding>> ChoiceList = new(() =>
    [
        new UTF8Encoding(false),
        Encoding.Unicode,
        Encoding.BigEndianUnicode,
        Ansi(),
        Encoding.Latin1,
        Encoding.GetEncoding(852),
    ]);

    /// <summary>Encodings offered in the viewer's menu, the system code page among the single-byte ones.</summary>
    public static IReadOnlyList<Encoding> Choices => ChoiceList.Value;

    private static bool Register()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    }

    public static Encoding Ansi()
    {
        _ = Registered;
        var codePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        return codePage > 0 ? Encoding.GetEncoding(codePage) : Encoding.Latin1;
    }

    public static Encoding Detect(ReadOnlySpan<byte> sample)
    {
        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
        {
            return new UTF8Encoding(true);
        }

        if (sample.Length >= 2 && sample[0] == 0xFF && sample[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (sample.Length >= 2 && sample[0] == 0xFE && sample[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        if (LooksUtf16(sample, littleEndian: true))
        {
            return Encoding.Unicode;
        }

        if (LooksUtf16(sample, littleEndian: false))
        {
            return Encoding.BigEndianUnicode;
        }

        return IsValidUtf8(sample) ? new UTF8Encoding(false) : Ansi();
    }

    /// <summary>Zero bytes outside a UTF-16 pattern mean a binary file.</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty)
        {
            return false;
        }

        if (LooksUtf16(sample, true) || LooksUtf16(sample, false))
        {
            return false;
        }

        // A UTF-16 BOM decides even when the text has few ASCII letters (Cyrillic, CJK); only NUL characters make it binary.
        if (sample.Length >= 2 && ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF)))
        {
            return HasNulUnit(sample[2..]);
        }

        var zeros = 0;
        foreach (var b in sample)
        {
            if (b == 0)
            {
                zeros++;
            }
        }

        return zeros > 0;
    }

    private static bool HasNulUnit(ReadOnlySpan<byte> units)
    {
        for (var i = 0; i + 1 < units.Length; i += 2)
        {
            if (units[i] == 0 && units[i + 1] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Strict UTF-8 check on the sample; a multi-byte sequence cut at the end is allowed.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var b = bytes[i];
            int need;
            if (b < 0x80)
            {
                i++;
                continue;
            }
            else if ((b & 0xE0) == 0xC0 && b >= 0xC2)
            {
                need = 1;
            }
            else if ((b & 0xF0) == 0xE0)
            {
                need = 2;
            }
            else if ((b & 0xF8) == 0xF0 && b <= 0xF4)
            {
                need = 3;
            }
            else
            {
                return false;
            }

            for (var k = 1; k <= need; k++)
            {
                if (i + k >= bytes.Length)
                {
                    return true; // cut off by the sample size
                }

                if ((bytes[i + k] & 0xC0) != 0x80)
                {
                    return false;
                }
            }

            i += need + 1;
        }

        return true;
    }

    private static bool LooksUtf16(ReadOnlySpan<byte> sample, bool littleEndian)
    {
        if (sample.Length < 16)
        {
            return false; // too short for the statistics to mean anything
        }

        // ASCII text in UTF-16 has a zero in every other byte: count zeros at the high-byte positions.
        var pairs = sample.Length / 2;
        var highZeros = 0;
        var lowZeros = 0;
        for (var i = 0; i + 1 < sample.Length; i += 2)
        {
            var lo = littleEndian ? sample[i] : sample[i + 1];
            var hi = littleEndian ? sample[i + 1] : sample[i];
            if (hi == 0)
            {
                highZeros++;
            }

            if (lo == 0)
            {
                lowZeros++;
            }
        }

        return highZeros >= pairs * 7 / 10 && lowZeros <= pairs / 20;
    }
}
