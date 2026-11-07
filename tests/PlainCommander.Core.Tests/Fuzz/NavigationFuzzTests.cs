using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>Masks, the panel filter's fields, quick search and folding: any typed text is handled, quickly.</summary>
public sealed class NavigationFuzzTests
{
    private static readonly string[] MaskPieces = ["*", "?", "*.*", ".", "a", "b", "ab", ";", " ", "*a*", "\\", "/", "<", ">", "\"", "[", "]", "é", "ł", "\uD83D", "\0"];

    private static readonly string[] NamePieces = ["a", "b", "ab", ".", " ", "x", "É", "Ł", "😀", "\uDE00", "\0", "*", "?"];

    [Fact]
    public void Masks_never_throw_and_stay_fast_on_long_names()
    {
        FuzzKit.Check(
            seed: 501,
            iterations: 1500,
            g => (Mask: g.Bool(70) ? g.FromAlphabet(MaskPieces, 12) : g.NastyString(), Name: g.Bool(80) ? g.FromAlphabet(NamePieces, 20) : g.NastyString()),
            input =>
            {
                var watch = Stopwatch.StartNew();
                var matches = FileMask.Parse(input.Mask);
                _ = matches(input.Name);
                _ = matches(string.Concat(Enumerable.Repeat("ab", 200)) + "c");
                watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
            },
            show: i => $"mask {FuzzKit.Literal(i.Mask)}, name {FuzzKit.Literal(i.Name)}");
    }

    [Fact]
    public void Star_dot_star_and_star_match_everything()
    {
        FuzzKit.Check(
            seed: 502,
            iterations: 1000,
            g => g.NastyString(),
            name =>
            {
                if (name.Length == 0)
                {
                    return; // no file has an empty name; the framework's matcher says no to it
                }

                FileMask.Parse("*.*")(name).Should().BeTrue();
                FileMask.Parse("*")(name).Should().BeTrue();
                FileMask.Parse(string.Empty)(name).Should().BeFalse();
            },
            FuzzKit.ShrinkString);
    }

    private static readonly string[] SizePieces = ["0", "1", "9", ".", ",", "5", "e", "E", "+", "-", "k", "K", "M", "g", "G", "b", "B", " ", "NaN", "Infinity", "1e308", "١", "１", "%", " "];

    [Fact]
    public void Size_parsing_gives_a_size_or_a_format_error()
    {
        FuzzKit.Check(
            seed: 503,
            iterations: 3000,
            g => g.Bool(80) ? g.FromAlphabet(SizePieces, 6) : g.NastyString(),
            text =>
            {
                long? size;
                try
                {
                    size = PanelFilter.ParseSize(text);
                }
                catch (FormatException)
                {
                    return;
                }

                if (text.Trim().Length == 0)
                {
                    size.Should().BeNull();
                }
                else
                {
                    size.Should().NotBeNull().And.BeGreaterThanOrEqualTo(0);
                }
            },
            FuzzKit.ShrinkString);
    }

    private static readonly string[] MinutePieces = ["0", "1", "5", "9", "59", "60", ":", ".", ",", "-", "-0", "e", "308", "1e308", "NaN", "Infinity", " ", "١", "999999999999"];

    [Fact]
    public void Duration_parsing_gives_a_duration_or_a_format_error()
    {
        FuzzKit.Check(
            seed: 504,
            iterations: 3000,
            g => g.Bool(85) ? g.FromAlphabet(MinutePieces, 6) : g.NastyString(),
            text =>
            {
                TimeSpan? duration;
                try
                {
                    duration = PanelFilter.ParseMinutes(text);
                }
                catch (FormatException)
                {
                    return;
                }

                if (text.Trim().Length == 0)
                {
                    duration.Should().BeNull();
                }
                else
                {
                    duration.Should().NotBeNull().And.BeGreaterThanOrEqualTo(TimeSpan.Zero);
                }
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void Extensions_come_back_clean_and_distinct()
    {
        FuzzKit.Check(
            seed: 505,
            iterations: 2000,
            g => g.Bool(70) ? g.FromAlphabet(["pdf", "PDF", ".", "*", ",", ";", " ", "docx", "*.txt", ".tar.gz", "é"], 8) : g.NastyString(),
            text =>
            {
                var extensions = PanelFilter.ParseExtensions(text);
                string.Join("|", extensions.Where(e => e.Length == 0 || e.StartsWith('.') || e.StartsWith('*') || e.Contains(',') || e.Contains(';') || e.Contains(' ')))
                    .Should().BeEmpty();
                extensions.Should().OnlyHaveUniqueItems(e => e.ToUpperInvariant());
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void A_filter_of_any_text_matches_any_entry_without_throwing()
    {
        var now = new DateTime(2025, 3, 4, 12, 0, 0, DateTimeKind.Local);
        FuzzKit.Check(
            seed: 506,
            iterations: 1500,
            g => (Filter: g.NastyString(), Name: g.NastyString(), Days: g.Int(6)),
            input =>
            {
                var filter = PanelFilter.FromText(input.Filter) with
                {
                    Extensions = PanelFilter.ParseExtensions(input.Filter),
                    DatePreset = (FilterDatePreset)input.Days,
                    From = input.Days == (int)FilterDatePreset.Range ? DateOnly.MinValue : null,
                    To = input.Days == (int)FilterDatePreset.Range ? DateOnly.MaxValue : null,
                };
                var matcher = filter.Matcher(now);
                foreach (var modified in new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue, new DateTimeOffset(now) })
                {
                    _ = matcher(new FileEntry { Name = input.Name, FullPath = "/" + input.Name, IsDirectory = false, Modified = modified });
                }
            },
            show: i => $"filter {FuzzKit.Literal(i.Filter)}, name {FuzzKit.Literal(i.Name)}, preset {i.Days}");
    }

    /// <summary>Found by the fuzzer (seed 507): string.Normalize throws on U+FFFE, which NTFS allows in a name, so quick search and the filter threw ArgumentException.</summary>
    [Theory]
    [InlineData("￾")]
    [InlineData("a￿b")]
    [InlineData("Żółć￾.txt")]
    public void Regression_noncharacters_in_a_name_do_not_break_folding(string name)
    {
        TextFold.Fold(name).Should().Contain(name.Contains('￾', StringComparison.Ordinal) ? "￾" : "￿");
        QuickSearch.Find([name], n => n, "a", 0).Should().BeInRange(-1, 0);
        PanelFilter.FromText("zolc").Matcher(DateTime.Now)(new FileEntry { Name = name, FullPath = "/" + name, IsDirectory = false });
        Rename.Transliteration.ToLatin(name).Should().Contain(name.Contains('￾', StringComparison.Ordinal) ? "￾" : "￿");
    }

    /// <summary>Found by the fuzzer (seed 505): "*.*" or ".*" in the extensions box became the extension "*", which no file has.</summary>
    [Theory]
    [InlineData("*.*")]
    [InlineData(".*")]
    [InlineData("*.*, .*")]
    public void Regression_star_extensions_mean_no_extension_test(string text)
    {
        PanelFilter.ParseExtensions(text).Should().BeEmpty();
    }

    [Fact]
    public void Folding_never_throws_and_is_idempotent()
    {
        FuzzKit.Check(
            seed: 507,
            iterations: 3000,
            g => g.NastyString(),
            text =>
            {
                var once = TextFold.Fold(text);
                TextFold.Fold(once).Should().Be(once);

                // Case is ignored for the scripts names are typed in (not a full Unicode case fold: "ϑ" upper-cases to "Θ").
                var plain = string.Concat(text.Where(c => char.IsAsciiLetterOrDigit(c) || "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ".Contains(c, StringComparison.Ordinal)));
                TextFold.Fold(plain.ToUpperInvariant()).Should().Be(TextFold.Fold(plain.ToLowerInvariant()), "folding ignores case");
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void Normalize_keeps_lone_surrogates_and_matches_the_framework_elsewhere()
    {
        FuzzKit.Check(
            seed: 508,
            iterations: 3000,
            g => g.NastyString(),
            text =>
            {
                foreach (var form in new[] { NormalizationForm.FormC, NormalizationForm.FormD })
                {
                    var normalized = TextFold.Normalize(text, form);
                    static int Kept(string s) => s.Count(c => c is '￾' or '￿') + (s.Length - Gen.WellFormed(s).Zip(s).Count(p => p.First == p.Second));
                    if (Kept(text) == 0)
                    {
                        normalized.Should().Be(text.Normalize(form));
                    }
                    else
                    {
                        Kept(normalized).Should().Be(Kept(text), "lone surrogates and noncharacters stay as they are");
                    }
                }
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void Quick_search_finds_an_index_in_range_and_the_count_agrees()
    {
        FuzzKit.Check(
            seed: 509,
            iterations: 1500,
            g => (Names: Enumerable.Range(0, g.Int(0, 12)).Select(_ => g.Bool(70) ? g.FromAlphabet(NamePieces, 6) : g.NastyString(3)).ToList(),
                Text: g.Bool(60) ? g.FromAlphabet(NamePieces, 3) : g.NastyString(2), Start: g.Int(-50, 50), Forward: g.Bool()),
            input =>
            {
                var found = QuickSearch.Find(input.Names, n => n, input.Text, input.Start, input.Forward);
                var next = QuickSearch.FindNext(input.Names, n => n, input.Text, input.Start, input.Forward);
                var count = QuickSearch.Count(input.Names, n => n, input.Text);
                found.Should().BeInRange(-1, input.Names.Count - 1);
                next.Should().BeInRange(-1, input.Names.Count - 1);
                (found >= 0).Should().Be(count > 0, "Find finds something exactly when something matches");
                (next >= 0).Should().Be(count > 0);
                if (found >= 0)
                {
                    QuickSearch.Matcher(input.Text)(input.Names[found]).Should().BeTrue();
                }
            },
            show: i => $"names [{string.Join(", ", i.Names.Select(FuzzKit.Literal))}], text {FuzzKit.Literal(i.Text)}, start {i.Start}, forward {i.Forward}");
    }
}
