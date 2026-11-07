using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Viewer;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>The line diff reproduces both sides; the viewer's detection and hex paging take any bytes.</summary>
public sealed class TextAndViewerFuzzTests
{
    private static List<string> Lines(Gen g, int max) =>
        Enumerable.Range(0, g.Int(0, max)).Select(_ => g.Bool(90) ? g.Pick(["a", "b", "c", "d", string.Empty, "a "]) : g.NastyString(2)).ToList();

    /// <summary>Right is left edited: lines deleted, inserted, changed, moved.</summary>
    private static List<string> Edit(Gen g, List<string> left)
    {
        var right = new List<string>(left);
        var edits = g.Int(0, 6);
        for (var i = 0; i < edits; i++)
        {
            switch (g.Int(4))
            {
                case 0 when right.Count > 0:
                    right.RemoveAt(g.Int(right.Count));
                    break;
                case 1:
                    right.Insert(g.Int(right.Count + 1), g.Pick(["a", "x", "y", string.Empty]));
                    break;
                case 2 when right.Count > 0:
                    right[g.Int(right.Count)] = g.Pick(["z", "a", "b"]);
                    break;
                default:
                    if (right.Count > 1)
                    {
                        var at = g.Int(right.Count);
                        var line = right[at];
                        right.RemoveAt(at);
                        right.Insert(g.Int(right.Count + 1), line);
                    }

                    break;
            }
        }

        return right;
    }

    private static void CheckDiff(List<string> left, List<string> right)
    {
        var result = TextDiff.Compare(left, right);
        var rows = result.Rows;

        // Each side read back from the rows is that side, numbered 1, 2, 3 ...
        rows.Where(r => r.LeftNumber is not null).Select(r => r.LeftText).Should().Equal(left);
        rows.Where(r => r.RightNumber is not null).Select(r => r.RightText).Should().Equal(right);
        rows.Where(r => r.LeftNumber is not null).Select(r => r.LeftNumber!.Value).Should().Equal(Enumerable.Range(1, left.Count));
        rows.Where(r => r.RightNumber is not null).Select(r => r.RightNumber!.Value).Should().Equal(Enumerable.Range(1, right.Count));

        var wrong = rows.Where(r => r.Kind switch
        {
            DiffKind.Same => r.LeftText != r.RightText || r.LeftNumber is null || r.RightNumber is null,
            DiffKind.Changed => r.LeftNumber is null || r.RightNumber is null || r.LeftText == r.RightText,
            DiffKind.Removed => r.LeftNumber is null || r.RightNumber is not null,
            _ => r.LeftNumber is not null || r.RightNumber is null,
        }).Select(r => r.ToString()).ToList();
        string.Join("; ", wrong).Should().BeEmpty();

        result.Added.Should().Be(rows.Count(r => r.Kind == DiffKind.Added));
        result.Removed.Should().Be(rows.Count(r => r.Kind == DiffKind.Removed));
        result.Changed.Should().Be(rows.Count(r => r.Kind == DiffKind.Changed));
        result.Identical.Should().Be(left.SequenceEqual(right));

        // The script is minimal in the number of lines kept: as many "same" rows as the longest common subsequence.
        rows.Count(r => r.Kind == DiffKind.Same).Should().Be(Lcs(left, right));
    }

    private static int Lcs(List<string> a, List<string> b)
    {
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        {
            for (var j = b.Count - 1; j >= 0; j--)
            {
                table[i, j] = a[i] == b[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        return table[0, 0];
    }

    [Fact]
    public void The_diff_reproduces_both_sides_with_consistent_counts()
    {
        FuzzKit.Check(
            seed: 701,
            iterations: 1500,
            g =>
            {
                var left = Lines(g, 25);
                return (Left: left, Right: g.Bool(80) ? Edit(g, left) : Lines(g, 25));
            },
            input => CheckDiff(input.Left, input.Right),
            input => FuzzKit.ShrinkList(input.Left).Select(l => (l, input.Right)).Concat(FuzzKit.ShrinkList(input.Right).Select(r => (input.Left, r))),
            input => $"left [{string.Join(", ", input.Left.Select(FuzzKit.Literal))}], right [{string.Join(", ", input.Right.Select(FuzzKit.Literal))}]");
    }

    private static readonly byte[][] Starts =
    [
        [], [0xEF], [0xEF, 0xBB], [0xEF, 0xBB, 0xBF], [0xFF], [0xFF, 0xFE], [0xFE, 0xFF], [0xFF, 0xFE, 0x00], [0xFF, 0xFE, 0x00, 0x00], [0xC3], [0xE2, 0x82], [0xF0, 0x9F, 0x98],
        [0xED, 0xA0, 0x80], [0xE0, 0x80, 0x80], [0xC0, 0xAF], [0xF4, 0x90, 0x80, 0x80], [0xF8, 0x88, 0x80, 0x80, 0x80],
    ];

    private static byte[] TextLike(Gen g)
    {
        var text = string.Concat(Enumerable.Range(0, g.Int(0, 30)).Select(_ => g.Pick(["abc", " ", "\r\n", "ął", "Ж", "😀", "中", "\t"])));
        var encoding = g.Pick(new Encoding[] { new UTF8Encoding(false), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.Latin1 });
        var bytes = encoding.GetBytes(text);
        if (g.Bool(30))
        {
            bytes = [.. encoding.GetPreamble(), .. bytes];
        }

        if (g.Bool(30) && bytes.Length > 0)
        {
            bytes = bytes[..g.Int(bytes.Length)]; // cut inside a character
        }

        return g.Bool(30) ? [.. g.Pick(Starts), .. bytes] : bytes;
    }

    [Fact]
    public void Detection_takes_any_bytes_and_decoding_never_throws()
    {
        FuzzKit.Check(
            seed: 702,
            iterations: 3000,
            g => g.Int(3) switch
            {
                0 => g.Bytes(64),
                1 => g.Mutate(TextLike(g)),
                _ => TextLike(g),
            },
            bytes =>
            {
                var encoding = TextDecoder.Detect(bytes);
                _ = TextDecoder.LooksBinary(bytes);
                _ = encoding.GetString(bytes);
                foreach (var choice in TextDecoder.Choices)
                {
                    _ = choice.GetString(bytes);
                }
            },
            FuzzKit.ShrinkBytes,
            FuzzKit.Hex);
    }

    /// <summary>"Strict UTF-8 check": what it accepts (complete sequences) decodes without a replacement character.</summary>
    [Fact]
    public void Valid_utf8_by_the_check_is_valid_utf8()
    {
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        FuzzKit.Check(
            seed: 703,
            iterations: 5000,
            g => g.Bool() ? [.. g.Pick(Starts), .. g.Bytes(6)] : g.Bytes(8),
            bytes =>
            {
                if (!TextDecoder.IsValidUtf8(bytes))
                {
                    return;
                }

                // The check allows a sequence cut by the end of the sample: drop an unfinished tail before decoding.
                var end = bytes.Length;
                for (var back = 1; back <= Math.Min(3, bytes.Length); back++)
                {
                    var b = bytes[^back];
                    if ((b & 0xC0) == 0x80)
                    {
                        continue;
                    }

                    var need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                    if (need > back)
                    {
                        end = bytes.Length - back;
                    }

                    break;
                }

                var act = () => strict.GetString(bytes, 0, end);
                act.Should().NotThrow();
            },
            FuzzKit.ShrinkBytes,
            FuzzKit.Hex);
    }

    /// <summary>
    /// Found by the fuzzer (seed 703, input E08080): the "strict" check only looked at the lead byte and the 10xxxxxx
    /// pattern, so Windows-1250 text such as "ŕ€€" (E0 80 80) was detected as UTF-8 and shown as replacement characters.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xE0, 0x80, 0x80 })] // overlong "\0"
    [InlineData(new byte[] { 0xC0, 0xAF })] // overlong "/"
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })] // a UTF-16 surrogate encoded in UTF-8
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // above U+10FFFF
    [InlineData(new byte[] { 0xF0, 0x80, 0x80, 0x80 })] // overlong four-byte form
    public void Regression_overlong_surrogate_and_out_of_range_utf8_is_not_utf8(byte[] bytes)
    {
        byte[] sample = [.. "plik "u8, .. bytes, .. " koniec"u8];
        TextDecoder.IsValidUtf8(sample).Should().BeFalse();
    }

    [Fact]
    public void Hex_pages_cover_the_file_exactly()
    {
        FuzzKit.Check(
            seed: 704,
            iterations: 400,
            g => (Bytes: g.Bytes(g.Pick([0, 1, 7, 8, 15, 16, 17, 100, 1000])), Width: g.Bool() ? 8 : 16, First: g.Int(-2, 80), Count: g.Int(-2, 80)),
            input =>
            {
                using var lines = new HexLines(new MemoryStream(input.Bytes), input.Width);
                lines.LineCount.Should().Be((input.Bytes.Length + input.Width - 1) / input.Width);
                if (input.First < 0 || input.Count < 0)
                {
                    var bad = () => lines.Page(input.First, input.Count);
                    bad.Should().Throw<ArgumentOutOfRangeException>();
                    return;
                }

                var page = lines.Page(input.First, input.Count);
                page.Length.Should().Be((int)Math.Max(0, Math.Min(input.Count, lines.LineCount - input.First)));
                var all = lines.Page(0, int.MaxValue);
                all.Length.Should().Be((int)lines.LineCount);
                var hexWidth = (input.Width * 3) + (input.Width > 8 ? 1 : 0);
                var shown = all.Sum(l => l.Substring(10, hexWidth).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                shown.Should().Be(input.Bytes.Length);
                all.Select(l => l.Length).Distinct().Count().Should().BeLessThanOrEqualTo(2, "every line but the last has the same width");
            },
            show: i => $"{i.Bytes.Length} bytes, width {i.Width}, page {i.First}+{i.Count}");
    }
}
