using System.Globalization;
using System.Text;

namespace PlainCommander.Core.Navigation;

/// <summary>
/// Text as quick search and the filter compare it: lower case, without diacritics, so "zolc" finds "Żółć.txt".
/// "ł" has no decomposition in Unicode and is mapped by hand (likewise đ, ø, ß).
/// </summary>
public static class TextFold
{
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsPlainAscii(text))
        {
            return text.ToLowerInvariant();
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (char.ToLowerInvariant(c))
            {
                case 'ł': builder.Append('l'); break;
                case 'đ': builder.Append('d'); break;
                case 'ø': builder.Append('o'); break;
                case 'ß': builder.Append("ss"); break;
                case var lower: builder.Append(lower); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Every word of <paramref name="foldedWords"/> is somewhere in the folded name, in any order.</summary>
    public static bool ContainsAll(string foldedName, IReadOnlyList<string> foldedWords)
    {
        ArgumentNullException.ThrowIfNull(foldedName);
        ArgumentNullException.ThrowIfNull(foldedWords);
        foreach (var word in foldedWords)
        {
            if (!foldedName.Contains(word, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlainAscii(string text)
    {
        foreach (var c in text)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }
}
