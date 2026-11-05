using System.Globalization;
using System.Text;

namespace PlainCommander.Core.Rename;

/// <summary>
/// Names in plain Latin letters: diacritics go ("Zażółć gęślą jaźń" → "Zazolc gesla jazn"), Cyrillic and Greek are
/// spelled in Latin ("Москва" → "Moskva", "Αθήνα" → "Athina"). Letters with no decomposition (ł, ø, đ, ß, æ…) and the
/// Cyrillic and Greek alphabets come from tables. Capitals stay capitals: "Жук" → "Zhuk", and inside a word written
/// in capitals the whole spelling is capital, "ЖУК" → "ZHUK".
/// </summary>
public static class Transliteration
{
    private static readonly Dictionary<char, string> Letters = new()
    {
        // Latin letters that FormD leaves alone
        ['ł'] = "l", ['ø'] = "o", ['đ'] = "d", ['ð'] = "d", ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe", ['þ'] = "th", ['ı'] = "i", ['ħ'] = "h", ['ŀ'] = "l",

        // Cyrillic (Russian, Ukrainian, Belarusian, Serbian, Macedonian, Bulgarian)
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "zh", ['з'] = "z",
        ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = string.Empty, ['ы'] = "y", ['ь'] = string.Empty, ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ґ'] = "g", ['ў'] = "u",
        ['ђ'] = "dj", ['ј'] = "j", ['љ'] = "lj", ['њ'] = "nj", ['ћ'] = "c", ['џ'] = "dz", ['ѓ'] = "gj", ['ќ'] = "kj", ['ѕ'] = "dz",

        // Greek (accents are removed first)
        ['α'] = "a", ['β'] = "v", ['γ'] = "g", ['δ'] = "d", ['ε'] = "e", ['ζ'] = "z", ['η'] = "i", ['θ'] = "th", ['ι'] = "i",
        ['κ'] = "k", ['λ'] = "l", ['μ'] = "m", ['ν'] = "n", ['ξ'] = "x", ['ο'] = "o", ['π'] = "p", ['ρ'] = "r", ['σ'] = "s",
        ['ς'] = "s", ['τ'] = "t", ['υ'] = "y", ['φ'] = "f", ['χ'] = "ch", ['ψ'] = "ps", ['ω'] = "o",
    };

    public static string ToLatin(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Letter by letter on the composed text: й, ё, ї are letters of their own, not и, е, і with a mark.
        var composed = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(composed.Length);
        for (var i = 0; i < composed.Length; i++)
        {
            var c = composed[i];
            var lower = char.ToLowerInvariant(c);
            if (Letters.TryGetValue(lower, out var latin))
            {
                if (lower == c || latin.Length == 0)
                {
                    builder.Append(latin);
                }
                else if (latin.Length == 1 || IsCapitalWord(composed, i))
                {
                    builder.Append(latin.ToUpperInvariant());
                }
                else
                {
                    builder.Append(char.ToUpperInvariant(latin[0])).Append(latin[1..]);
                }

                continue;
            }

            // Any other letter loses its marks: ą → a, é → e; Greek with accents then finds its table entry.
            foreach (var part in c.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                var partLower = char.ToLowerInvariant(part);
                if (part != c && Letters.TryGetValue(partLower, out var fromBase))
                {
                    builder.Append(partLower == part ? fromBase : char.ToUpperInvariant(fromBase[0]) + fromBase[1..]);
                }
                else
                {
                    builder.Append(part);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>The capital at <paramref name="at"/> is next to another capital letter: the word is written in capitals.</summary>
    private static bool IsCapitalWord(string text, int at)
    {
        static bool Capital(char c) => char.IsLetter(c) && char.IsUpper(c);
        return (at + 1 < text.Length && Capital(text[at + 1])) || (at > 0 && Capital(text[at - 1]));
    }
}
