using System.Globalization;
using System.Text;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>
/// A small seeded generator for fuzz and property tests (no packages). Every case gets its own seed derived from the
/// test's base seed and the case number, so a failure names the one seed that reproduces it: <c>new Gen(caseSeed)</c>
/// gives the same input again. PC_FUZZ_ITERATIONS scales the number of cases (e.g. 20 for a long local run); the
/// default keeps every test well under two seconds.
/// </summary>
internal static class FuzzKit
{
    /// <summary>Multiplier from the environment; 1 when unset.</summary>
    public static double Scale { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("PC_FUZZ_ITERATIONS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1;

    public static int Iterations(int count) => Math.Max(1, (int)(count * Scale));

    /// <summary>The seed of case <paramref name="i"/> of a test whose base seed is <paramref name="seed"/>.</summary>
    public static int CaseSeed(int seed, int i) => unchecked((seed * 7919) + (i * 104729) + 17);

    /// <summary>
    /// Runs <paramref name="property"/> on generated inputs. The property throws (an assertion or anything else) to
    /// fail; the failing input is shrunk with <paramref name="shrink"/> when given and reported with the case seed.
    /// </summary>
    public static void Check<T>(int seed, int iterations, Func<Gen, T> generate, Action<T> property, Func<T, IEnumerable<T>>? shrink = null, Func<T, string>? show = null)
    {
        show ??= Show;
        for (var i = 0; i < Iterations(iterations); i++)
        {
            var caseSeed = CaseSeed(seed, i);
            var input = generate(new Gen(caseSeed));
            if (Fails(property, input) is not { } error)
            {
                continue;
            }

            var (smallest, smallestError) = Shrink(input, error, property, shrink);
            throw new FuzzFailure(
                $"Fuzz case failed: base seed {seed}, case {i}, case seed {caseSeed} (new Gen({caseSeed})).{Environment.NewLine}" +
                $"Input: {show(input)}{Environment.NewLine}" +
                (shrink is null ? string.Empty : $"Shrunk: {show(smallest)}{Environment.NewLine}") +
                $"Error: {smallestError.GetType().Name}: {smallestError.Message}",
                smallestError);
        }
    }

    private static Exception? Fails<T>(Action<T> property, T input)
    {
        try
        {
            property(input);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Greedy shrink: take the first smaller candidate that still fails, until none does (bounded).</summary>
    private static (T Input, Exception Error) Shrink<T>(T input, Exception error, Action<T> property, Func<T, IEnumerable<T>>? shrink)
    {
        if (shrink is null)
        {
            return (input, error);
        }

        var steps = 0;
        var progress = true;
        while (progress && steps < 500)
        {
            progress = false;
            foreach (var candidate in shrink(input))
            {
                if (++steps >= 500)
                {
                    break;
                }

                if (Fails(property, candidate) is { } e)
                {
                    (input, error, progress) = (candidate, e, true);
                    break;
                }
            }
        }

        return (input, error);
    }

    /// <summary>Shorter strings: empty, halves, then one character less at each position.</summary>
    public static IEnumerable<string> ShrinkString(string s)
    {
        if (s.Length == 0)
        {
            yield break;
        }

        yield return string.Empty;
        if (s.Length > 1)
        {
            yield return s[..(s.Length / 2)];
            yield return s[(s.Length / 2)..];
        }

        for (var i = 0; i < s.Length && i < 200; i++)
        {
            yield return s.Remove(i, 1);
        }
    }

    public static IEnumerable<byte[]> ShrinkBytes(byte[] b)
    {
        if (b.Length == 0)
        {
            yield break;
        }

        yield return [];
        if (b.Length > 1)
        {
            yield return b[..(b.Length / 2)];
            yield return b[(b.Length / 2)..];
        }

        for (var i = 0; i < b.Length && i < 200; i++)
        {
            yield return [.. b[..i], .. b[(i + 1)..]];
        }
    }

    public static IEnumerable<List<T>> ShrinkList<T>(List<T> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            var copy = new List<T>(list);
            copy.RemoveAt(i);
            yield return copy;
        }
    }

    /// <summary>A C# literal with every non-printable or non-ASCII character escaped, so the input can be pasted back.</summary>
    public static string Literal(string? s)
    {
        if (s is null)
        {
            return "null";
        }

        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when c is >= ' ' and < (char)0x7F => c.ToString(),
                _ => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
            });
        }

        return sb.Append('"').ToString();
    }

    public static string Hex(byte[] bytes) => bytes.Length <= 512
        ? Convert.ToHexString(bytes)
        : Convert.ToHexString(bytes.AsSpan(0, 512)) + $"... ({bytes.Length} bytes)";

    public static string Show<T>(T value) => value switch
    {
        null => "null",
        string s => Literal(s),
        byte[] b => Hex(b),
        System.Collections.IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(Show)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

internal sealed class FuzzFailure(string message, Exception inner) : Exception(message, inner);

/// <summary>Deterministic generator of nasty inputs.</summary>
internal sealed class Gen(int seed)
{
#pragma warning disable CA5394 // a seeded, reproducible generator is the point; nothing here is security-related
    private readonly Random _random = new(seed);

    public int Seed { get; } = seed;

    public int Int(int maxExclusive) => _random.Next(maxExclusive);

    public int Int(int min, int maxExclusive) => _random.Next(min, maxExclusive);

    public long Long() => _random.NextInt64(long.MinValue, long.MaxValue);

    public double Double() => _random.NextDouble();

    public bool Bool(int percentTrue = 50) => _random.Next(100) < percentTrue;

    public T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    public byte[] Bytes(int maxLength)
    {
        var bytes = new byte[Int(maxLength + 1)];
        _random.NextBytes(bytes);
        return bytes;
    }
#pragma warning restore CA5394

    /// <summary>Building blocks that tend to break text and path handling.</summary>
    public static readonly string[] Nasty =
    [
        string.Empty, " ", "  ", "\t", "\r\n", "\n", "\0", "\u0001", "\u001F", "\u007F",
        ".", "..", "...", ". ", " .", "./", "../", "..\\", "/", "\\", "//", "\\\\", "\\\\?\\", "\\\\.\\", "\\\\server\\share",
        "C:", "c:\\", "C:x", "Z:/", "a:b", "file.txt:stream", "::$DATA",
        "CON", "con", "PRN", "AUX", "NUL", "nul.txt", "COM1", "LPT9", "COM\u00B9", "CONIN$",
        "%", "%20", "%2F", "%zz", "%%", "{", "}", "{0}", "{{", "}}", "[", "]", "[[", "]]", "[N]", "[E]", "[C]", "[N2-5]", "[N-]", "[YMD]", "[x",
        "*", "?", "*.*", "*?*", "**", "?*?*?*?*?*?*?*?*?*?*?*", "\"", "'", "`", "<", ">", "|", ":", ";", ",", "@", "#", "$", "&", "~", "^", "+", "-", "=", "!",
        "a", "A", "z", "Z", "0", "00", "007", "9", "99999999999999999999999999", "-1", "1e308", "NaN", "Infinity", "1,5", "1.5",
        "\u00E9", "e\u0301", "\u0301", "\u0301\u0301\u0301", "\u00C5", "A\u030A", "\u212B", "\uFB01", "\u1E9E", "\u00DF", "\u0130", "\u0131",
        "\u017B\u00F3\u0142\u0107", "Z\u0307o\u0301\u0142c\u0301", "\u0416\u0443\u043A", "\u0416\u0423\u041A", "\u0391\u03B8\u03AE\u03BD\u03B1", "\u4E2D\u6587", "\uAC00", "\u1100\u1161",
        "\uD83D\uDE00", "\uD83D", "\uDE00", "\uDE00\uD83D", "\uFFFD", "\uFEFF", "\uFFFE", "\uFFFF",
        "\u200B", "\u200C", "\u200D", "\u2060", "\u200E", "\u200F", "\u202E", "\u202A", "\u061C",
        "\u0660", "\u0669", "\u06F5", "\u0966", "\uFF11", "\uFF10\uFF10\uFF15", "\u00B2", "\u2460", "\u0BEF",
        "\u00A0", "\u2028", "\u3000",
    ];

    private static readonly char[] Interesting =
    [
        'a', 'b', 'Z', '0', '1', '9', '.', ' ', '/', '\\', ':', '*', '?', '"', '<', '>', '|', '%', '{', '}', '[', ']', '-', '_', '\0', '\t', '\n',
        '\u00E9', '\u0301', '\u0142', '\u0416', '\u03B1', '\u4E2D', '\uD83D', '\uDE00', '\u200B', '\u202E', '\uFEFF', '\u0660', '\uFF11', '\u00A0', '\uFFFF',
    ];

    private static readonly byte[] InterestingBytes = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0xFE, (byte)'/', (byte)'.', (byte)'\\', (byte)':'];

    /// <summary>A nasty string: a few building blocks, random characters or both; now and then very long.</summary>
    public string NastyString(int maxParts = 6)
    {
        var sb = new StringBuilder();
        var parts = Int(maxParts + 1);
        for (var p = 0; p < parts; p++)
        {
            switch (Int(5))
            {
                case 0:
                case 1:
                    sb.Append(Pick(Nasty));
                    break;
                case 2:
                    sb.Append(Pick(Interesting));
                    break;
                case 3:
                    sb.Append((char)Int(0x10000));
                    break;
                default:
                    sb.Append(Word());
                    break;
            }
        }

        if (Bool(2))
        {
            var unit = sb.Length == 0 ? "a" : sb.ToString();
            while (sb.Length < 5000)
            {
                sb.Append(unit);
            }
        }

        return sb.ToString();
    }

    /// <summary>The text with every lone surrogate replaced by '?', for formats (UTF-8, JSON) that cannot carry one.</summary>
    public static string WellFormed(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '?';
            }
        }

        return new string(chars);
    }

    /// <summary>Letters and digits in random case, so digit runs of random length (leading zeros too) are common.</summary>
    public string Word(int maxLength = 8)
    {
        var sb = new StringBuilder();
        var length = Int(maxLength + 1);
        for (var i = 0; i < length; i++)
        {
            sb.Append(Int(4) switch
            {
                0 => (char)('0' + Int(10)),
                1 => (char)('A' + Int(26)),
                _ => (char)('a' + Int(26)),
            });
        }

        return sb.ToString();
    }

    /// <summary>A string built from a small alphabet of pieces, so equal and near-equal values are common.</summary>
    public string FromAlphabet(IReadOnlyList<string> alphabet, int maxParts)
    {
        var sb = new StringBuilder();
        var parts = Int(maxParts + 1);
        for (var i = 0; i < parts; i++)
        {
            sb.Append(Pick(alphabet));
        }

        return sb.ToString();
    }

    /// <summary>A copy of <paramref name="valid"/> with 1–4 mutations: bit flips, truncation, insertion, duplication, deletion, overwrite, appended bytes.</summary>
    public byte[] Mutate(byte[] valid)
    {
        var bytes = new List<byte>(valid);
        var count = Int(1, 5);
        for (var n = 0; n < count; n++)
        {
            switch (Int(7))
            {
                case 0 when bytes.Count > 0:
                    bytes[Int(bytes.Count)] ^= (byte)(1 << Int(8));
                    break;
                case 1 when bytes.Count > 0:
                    var keep = Int(bytes.Count);
                    bytes.RemoveRange(keep, bytes.Count - keep);
                    break;
                case 2:
                    bytes.Insert(Int(bytes.Count + 1), (byte)Int(256));
                    break;
                case 3 when bytes.Count > 0:
                    var from = Int(bytes.Count);
                    var length = Math.Min(Int(1, 64), bytes.Count - from);
                    bytes.InsertRange(Int(bytes.Count + 1), bytes.GetRange(from, length));
                    break;
                case 4 when bytes.Count > 0:
                    var start = Int(bytes.Count);
                    bytes.RemoveRange(start, Math.Min(Int(1, 16), bytes.Count - start));
                    break;
                case 5 when bytes.Count > 0:
                    bytes[Int(bytes.Count)] = Pick(InterestingBytes);
                    break;
                default:
                    bytes.AddRange(Bytes(16));
                    break;
            }
        }

        return [.. bytes];
    }
}
