using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Rename;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>Ctrl+M: any mask, counter and search text gives a name or a reported problem, never an exception.</summary>
public sealed class RenameFuzzTests
{
    private static readonly string[] MaskPieces =
    [
        "[N]", "[E]", "[C]", "[P]", "[YMD]", "[Y]", "[M]", "[D]", "[hms]", "[h]", "[m]", "[s]", "[N1]", "[N2-5]", "[N-3]", "[N3-]", "[N0]", "[N-]", "[N--]", "[N99999999999]",
        "[N2147483647-2147483647]", "[E1-1]", "[X]", "[]", "[[", "]]", "[", "]", "[N", "N]", "-", ".", " ", "x", "é", "Ж", "/", "\\", ":", "*", "\0", "\uD83D", "￾",
    ];

    private static readonly string[] Regexes = ["a", ".", "(a+)+$", "(", "[", "\\", "a|", "(?<n>x)", "^", "$", ".*", "(a*)*b", "\\d+", "(?i)İ", "\\p{L}", "x{2,}"];

    private static RenameRules Rules(Gen g)
    {
        var regex = g.Bool(30);
        return new RenameRules(
            NameMask: g.Bool(80) ? g.FromAlphabet(MaskPieces, 5) : g.NastyString(),
            ExtensionMask: g.Bool(80) ? g.FromAlphabet(MaskPieces, 3) : g.NastyString(2),
            Search: g.Bool(40) ? (regex ? g.Pick(Regexes) : g.NastyString(2)) : string.Empty,
            Replace: g.Bool() ? g.Pick(["", "$1", "${n}", "$$", "$", "${", "x", "\\"]) : g.NastyString(2),
            UseRegex: regex,
            CaseSensitive: g.Bool(),
            Case: (CaseChange)g.Int(5),
            CounterStart: g.Bool(80) ? g.Int(-5, 100) : g.Long(),
            CounterStep: g.Bool(80) ? g.Int(-3, 10) : g.Long(),
            CounterDigits: g.Pick([-5, 0, 1, 3, 12, 13, int.MaxValue, int.MinValue]),
            Transliterate: g.Bool(30));
    }

    private static RenameSource Source(Gen g, int i)
    {
        var name = g.Bool(70) ? g.Word() + g.Pick(["", ".txt", ".", ".tar.gz", "..", " "]) : g.NastyString(3);
        var modified = g.Pick(new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue, new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.FromHours(14)), new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(-12)) });
        return new RenameSource("/d" + i % 2 + "/" + name, name, g.Bool(20), modified);
    }

    [Fact]
    public void Any_rules_give_a_name_or_a_problem()
    {
        FuzzKit.Check(
            seed: 601,
            iterations: 1500,
            g => (Rules: Rules(g), Sources: Enumerable.Range(0, g.Int(1, 6)).Select(i => Source(g, i)).ToList()),
            input =>
            {
                var previews = MultiRename.Preview(input.Sources, input.Rules, _ => false);
                previews.Should().HaveCount(input.Sources.Count);
                foreach (var preview in previews.Where(p => p.Problem == RenameProblem.None))
                {
                    preview.NewName.Should().NotBeNullOrWhiteSpace();
                    preview.NewName.Should().NotBe(".").And.NotBe("..");
                    preview.NewName.IndexOfAny(Path.GetInvalidFileNameChars()).Should().Be(-1, FuzzKit.Literal(preview.NewName));
                    if (OperatingSystem.IsWindows())
                    {
                        string.Concat(preview.NewName.Where("<>:\"/\\|?*".Contains)).Should().BeEmpty();
                        preview.NewName.Should().NotEndWith(".").And.NotEndWith(" ");
                    }

                    Path.GetFileName(preview.NewPath).Should().Be(preview.NewName);
                }
            },
            show: i => $"{i.Rules} on [{string.Join(", ", i.Sources.Select(s => FuzzKit.Literal(s.Name) + (s.IsDirectory ? " dir" : string.Empty) + " " + s.Modified.ToString("o", System.Globalization.CultureInfo.InvariantCulture)))}]");
    }

    [Fact]
    public void Two_items_never_get_the_same_target()
    {
        FuzzKit.Check(
            seed: 602,
            iterations: 800,
            g => (Rules: Rules(g) with { NameMask = g.Pick(["x", "[N1]", "[C]", "[N]", "a[N2-3]"]), ExtensionMask = "[E]", Case = (CaseChange)g.Int(5) },
                Sources: Enumerable.Range(0, g.Int(2, 8)).Select(i => new RenameSource("/d/" + g.Pick(["a.txt", "A.txt", "b.txt", "ab.txt", "x.txt", "1.txt"]) + i, g.Pick(["a.txt", "A.txt", "b.txt", "ab.txt", "x.txt", "1.txt"]), false, DateTimeOffset.UnixEpoch)).ToList()),
            input =>
            {
                var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                var targets = MultiRename.Preview(input.Sources, input.Rules, _ => false).Where(p => p.Changes).Select(p => p.NewPath).ToList();
                targets.Should().OnlyHaveUniqueItems(t => comparer.GetHashCode(t));
            });
    }

    private static readonly string[] LatinCyrillicGreek =
    [
        "a", "Z", " ", "-", "1", "ą", "Ą", "ł", "Ł", "ż", "ó", "ß", "ẞ", "æ", "Æ", "ø", "Ø", "đ", "Đ", "ð",
        "ж", "Ж", "щ", "Щ", "ъ", "Ъ", "ё", "Ё", "ї", "Ї", "й", "Й", "ѓ", "Ѓ", "ӣ",
        "α", "Α", "ά", "Ά", "θ", "Θ", "ψ", "Ψ", "ς", "ϊ", "ΐ", "é", "́", "é", "É",
    ];

    [Fact]
    public void Transliteration_of_latin_cyrillic_and_greek_is_ascii()
    {
        FuzzKit.Check(
            seed: 603,
            iterations: 3000,
            g => g.FromAlphabet(LatinCyrillicGreek, 10),
            text =>
            {
                var latin = Transliteration.ToLatin(text);
                string.Concat(latin.Where(c => c > 127)).Should().BeEmpty();
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void Transliteration_never_throws_and_is_idempotent()
    {
        FuzzKit.Check(
            seed: 604,
            iterations: 3000,
            g => g.Bool(50) ? g.FromAlphabet(LatinCyrillicGreek, 10) : g.NastyString(),
            text =>
            {
                var once = Transliteration.ToLatin(text);
                Transliteration.ToLatin(once).Should().Be(once);
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void Transliteration_keeps_lone_surrogates_and_never_drops_a_character_the_tables_do_not_know()
    {
        FuzzKit.Check(
            seed: 605,
            iterations: 2000,
            g => g.NastyString(),
            text =>
            {
                var latin = Transliteration.ToLatin(text);
                var lone = text.Length - Gen.WellFormed(text).Zip(text).Count(p => p.First == p.Second);
                (latin.Length - Gen.WellFormed(latin).Zip(latin).Count(p => p.First == p.Second)).Should().Be(lone);
                _ = Encoding.UTF8.GetByteCount(latin);
            },
            FuzzKit.ShrinkString);
    }
}
