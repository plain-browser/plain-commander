using System.Globalization;

namespace PlainCommander.Core.Remote;

/// <summary>Unix permission bits: the "rwxr-xr-x" text, the octal "755" and the "decimal digits" the libraries take.</summary>
public static class UnixMode
{
    public const int All = 0xFFF; // 07777

    /// <summary>"rwxr-xr-x"; setuid/setgid/sticky show as s/S and t/T in the execute places, as ls does.</summary>
    public static string Format(int mode)
    {
        Span<char> text = stackalloc char[9];
        for (var i = 0; i < 9; i++)
        {
            var bit = 1 << (8 - i);
            text[i] = (mode & bit) != 0 ? "rwx"[i % 3] : '-';
        }

        Special(text, 2, (mode & 0x800) != 0, 's');
        Special(text, 5, (mode & 0x400) != 0, 's');
        Special(text, 8, (mode & 0x200) != 0, 't');
        return new string(text);
    }

    /// <summary>chmod's "X" for folders: execute (enter) wherever read is set.</summary>
    public static int WithSearch(int mode) =>
        mode | ((mode & 0x100) != 0 ? 0x40 : 0) | ((mode & 0x20) != 0 ? 0x8 : 0) | ((mode & 0x4) != 0 ? 0x1 : 0);

    /// <summary>"755" or "0755" (up to four octal digits) → bits; false for anything else.</summary>
    public static bool TryParse(string? text, out int mode)
    {
        mode = 0;
        var t = text?.Trim() ?? string.Empty;
        if (t.Length is 0 or > 4 || t.Any(c => c is < '0' or > '7'))
        {
            return false;
        }

        mode = Convert.ToInt32(t, 8);
        return true;
    }

    /// <summary>Bits → "755" (four digits when a special bit is set).</summary>
    public static string ToOctal(int mode) => Convert.ToString(mode & All, 8).PadLeft((mode & 0xE00) != 0 ? 4 : 3, '0');

    /// <summary>Bits → 755 as a number, the CHMOD form SSH.NET and FluentFTP take.</summary>
    public static int ToDigits(int mode) => int.Parse(ToOctal(mode), CultureInfo.InvariantCulture);

    /// <summary>755 as a number (FluentFTP's listing) → bits.</summary>
    public static int FromDigits(int digits) => TryParse(digits.ToString(CultureInfo.InvariantCulture), out var mode) ? mode : 0;

    private static void Special(Span<char> text, int index, bool set, char letter)
    {
        if (set)
        {
            text[index] = text[index] == '-' ? char.ToUpperInvariant(letter) : letter;
        }
    }
}
