using System.Globalization;
using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Formatting;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using PlainCommander.Core.Rename;
using PlainCommander.Core.Search;
using PlainCommander.Core.Sorting;
using PlainCommander.Core.Tests.Operations;
using PlainCommander.Core.Viewer;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>Text-driven features fed odd input: rename masks, folding, natural sort, decoding, diff, plural.</summary>
public sealed class TextFeaturesTests : IDisposable
{
    private static readonly DateTimeOffset When = new(2026, 3, 7, 9, 5, 4, TimeSpan.Zero);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static RenameSource Src(string name, bool dir = false) =>
        new(Path.Combine(Path.GetTempPath(), "album", name), name, dir, When);

    private static string Name(string name, RenameRules rules, long counter = 1, bool dir = false) =>
        MultiRename.Compute(Src(name, dir), rules, counter);

    private static IReadOnlyList<RenamePreview> Preview(RenameRules rules, params string[] names) =>
        MultiRename.Preview(names.Select(n => Src(n)).ToList(), rules, _ => false);

    // ---------------------------------------------------------------- multi-rename

    [Fact]
    public void Empty_masks_give_an_empty_name_problem()
    {
        Preview(new RenameRules(string.Empty, string.Empty), "a.txt").Single().Problem.Should().Be(RenameProblem.Empty);
    }

    [Fact]
    public void Whitespace_only_result_is_empty()
    {
        Preview(new RenameRules("   ", string.Empty), "a.txt").Single().Problem.Should().Be(RenameProblem.Empty);
    }

    [Fact]
    public void Only_extension_left_is_a_dot_name()
    {
        Name("a.txt", new RenameRules(string.Empty, "[E]")).Should().Be(".txt");
    }

    [Fact]
    public void Mask_producing_dot_or_dotdot_is_empty()
    {
        Preview(new RenameRules(".", string.Empty), "a.txt").Single().Problem.Should().Be(RenameProblem.Empty);
        Preview(new RenameRules("..", string.Empty), "a.txt").Single().Problem.Should().Be(RenameProblem.Empty);
    }

    [Fact]
    public void Same_target_for_two_items_marks_both_duplicates()
    {
        var p = Preview(new RenameRules("x"), "a.txt", "b.txt", "c.md");
        p[0].Problem.Should().Be(RenameProblem.DuplicateInBatch);
        p[1].Problem.Should().Be(RenameProblem.DuplicateInBatch);
        p[2].Problem.Should().Be(RenameProblem.None);
    }

    [Fact]
    public void Swap_through_a_backwards_counter_previews_clean_and_applies()
    {
        var one = _s.File("1.txt", "one");
        var two = _s.File("2.txt", "two");
        var sources = new List<RenameSource> { new(one, "1.txt", false, When), new(two, "2.txt", false, When) };
        var rules = new RenameRules("[C]", CounterStart: 2, CounterStep: -1);

        var preview = MultiRename.Preview(sources, rules, File.Exists);
        preview.Select(x => x.NewName).Should().Equal("2.txt", "1.txt");
        preview.Should().OnlyContain(x => x.Problem == RenameProblem.None);

        var outcome = MultiRename.Apply(preview);
        outcome.Failed.Should().BeEmpty();
        _s.Read("1.txt").Should().Be("two");
        _s.Read("2.txt").Should().Be("one");

        MultiRename.Undo(outcome).Failed.Should().BeEmpty();
        _s.Read("1.txt").Should().Be("one");
    }

    [Fact]
    public void Counter_with_zero_step_makes_duplicates()
    {
        var p = Preview(new RenameRules("[C]", CounterStep: 0), "a.txt", "b.txt");
        p.Should().OnlyContain(x => x.Problem == RenameProblem.DuplicateInBatch);
    }

    [Fact]
    public void Counter_digits_out_of_range_are_clamped()
    {
        Name("a.txt", new RenameRules("[C]", CounterDigits: 0), counter: 7).Should().Be("7.txt");
        Name("a.txt", new RenameRules("[C]", CounterDigits: -5), counter: 7).Should().Be("7.txt");
        Name("a.txt", new RenameRules("[C]", CounterDigits: int.MaxValue), counter: 7).Should().Be("000000000007.txt");
    }

    [Fact]
    public void Huge_counter_prints_in_full()
    {
        Name("a.txt", new RenameRules("[C]", CounterDigits: 3), counter: long.MaxValue)
            .Should().Be(long.MaxValue.ToString(CultureInfo.InvariantCulture) + ".txt");
    }

    [Fact]
    public void Regression_negative_counter_padding_puts_zeros_before_the_minus_sign()
    {
        // Start box has no minimum, so a negative counter is reachable. "-5" padded to 3 digits gives "0-5".
        Name("a.txt", new RenameRules("[C]", CounterDigits: 3), counter: -5).Should().Be("-05.txt");
    }

    [Fact]
    public void Invalid_regex_is_bad_regex_for_all()
    {
        var p = Preview(new RenameRules(Search: "(", Replace: "x", UseRegex: true), "a.txt", "b.txt");
        p.Should().OnlyContain(x => x.Problem == RenameProblem.BadRegex);
    }

    [Fact]
    public void Regex_syntax_is_literal_when_regex_is_off()
    {
        Name("a(.txt", new RenameRules(Search: "(", Replace: "_")).Should().Be("a_.txt");
    }

    [Fact(Timeout = 30_000)]
    public async Task Catastrophic_backtracking_never_hangs_the_preview()
    {
        var name = new string('a', 40) + "!.txt";
        var p = await Task.Run(() => Preview(new RenameRules(Search: "^(a+)+$", Replace: "x", UseRegex: true), name))
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        // Either it does not match at all (no change) or it times out into BadRegex.
        p.Single().Problem.Should().BeOneOf(RenameProblem.None, RenameProblem.BadRegex);
    }

    [Fact]
    public void Replacement_with_slash_or_nul_is_invalid()
    {
        Preview(new RenameRules(Search: "a", Replace: "x/y"), "a.txt").Single().Problem.Should().Be(RenameProblem.InvalidName);
        Preview(new RenameRules(Search: "a", Replace: "\0"), "a.txt").Single().Problem.Should().Be(RenameProblem.InvalidName);
    }

    [Fact]
    public void Regex_replacement_with_unknown_group_is_literal()
    {
        Name("abc.txt", new RenameRules(Search: "b", Replace: "$9", UseRegex: true)).Should().Be("a$9c.txt");
    }

    [Fact]
    public void Odd_tokens()
    {
        Name("a.txt", new RenameRules("[N")).Should().Be("[N.txt");
        Preview(new RenameRules("[X]"), "a.txt").Single().Problem.Should().Be(RenameProblem.BadMask);
        Preview(new RenameRules("[N0]"), "a.txt").Single().Problem.Should().Be(RenameProblem.BadMask);
        Preview(new RenameRules("[N-]"), "a.txt").Single().NewName.Should().Be("a.txt");
        Preview(new RenameRules("[N99999999999]"), "a.txt").Single().Problem.Should().Be(RenameProblem.BadMask);
    }

    [Fact]
    public void Range_past_the_end_or_reversed_is_empty()
    {
        Name("abc.txt", new RenameRules("[N5-9]x")).Should().Be("x.txt");
        Name("abc.txt", new RenameRules("[N3-1]x")).Should().Be("x.txt");
        Name("abc.txt", new RenameRules("[N2-100]")).Should().Be("bc.txt");
    }

    [Fact]
    public void Dot_names_and_trailing_dots_split_sensibly()
    {
        Name(".bashrc", new RenameRules("[N]_x")).Should().Be(".bashrc_x");
        Name("file.", new RenameRules("[N]_x")).Should().Be("file._x");
        Name("a.b.c", new RenameRules("[N]", "[E]_")).Should().Be("a.b.c_");
        Name("dir.d", new RenameRules("[N]-[E]"), dir: true).Should().Be("dir.d-");
    }

    [Theory]
    [InlineData("zażółć gęślą jaźń.TXT", CaseChange.Upper, "ZAŻÓŁĆ GĘŚLĄ JAŹŃ.TXT")]
    [InlineData("ZAŻÓŁĆ GĘŚLĄ JAŹŃ.TXT", CaseChange.Lower, "zażółć gęślą jaźń.txt")]
    [InlineData("łódź-ŁÓDŹ.pdf", CaseChange.WordsUpper, "Łódź-Łódź.Pdf")]
    [InlineData("ćma.txt", CaseChange.FirstUpper, "Ćma.txt")]
    [InlineData("straße.txt", CaseChange.Upper, "STRAßE.TXT")]
    public void Case_changes_with_national_letters(string name, CaseChange change, string expected)
    {
        using var _ = new CultureScope("pl-PL");
        MultiRename.Compute(Src(name), new RenameRules(Case: change), 1).Should().Be(expected);
    }

    [Fact]
    public void Case_change_on_emoji_and_digits()
    {
        Name("😀abc 1x.txt", new RenameRules(Case: CaseChange.WordsUpper)).Should().Be("😀Abc 1x.Txt");
        Name("😀abc.txt", new RenameRules(Case: CaseChange.FirstUpper)).Should().Be("😀abc.txt");
    }

    [Fact]
    public void Case_change_follows_the_current_culture_turkish_i()
    {
        using var _ = new CultureScope("tr-TR");
        Name("file.txt", new RenameRules(Case: CaseChange.Upper)).Should().Be("FİLE.TXT");
    }

    [Fact]
    public void Regression_transliterate_with_emoji_throws()
    {
        Name("😀 zdjęcie.jpg", new RenameRules(Transliterate: true)).Should().Be("😀 zdjecie.jpg");
    }

    [Fact]
    public void Regression_preview_promises_a_rename_onto_a_batch_member_that_stays()
    {
        // "ab" → "b", but "b" (also in the batch) cannot be renamed (its new name would be empty), so it stays put:
        // the preview must not say "ab" can become "b" (Apply then fails with "file exists").
        var p = MultiRename.Preview([Src("ab"), Src("b")], new RenameRules("[N2-]", string.Empty), _ => true);
        p[1].Problem.Should().Be(RenameProblem.Empty);
        p[0].NewName.Should().Be("b");
        p[0].Problem.Should().NotBe(RenameProblem.None);
    }

    // ---------------------------------------------------------------- transliteration

    [Fact]
    public void Every_polish_letter_transliterates()
    {
        Transliteration.ToLatin("ąćęłńóśźż ĄĆĘŁŃÓŚŹŻ").Should().Be("acelnoszz ACELNOSZZ");
    }

    [Theory]
    [InlineData("ẞ", "Ss")] // a lone capital spells like "Ж" → "Zh"
    [InlineData("ẞẞ", "SSSS")]
    [InlineData("Straße", "Strasse")]
    [InlineData("İstanbul ısı", "Istanbul isi")]
    [InlineData("ŒUVRE Æsir", "OEUVRE Aesir")]
    [InlineData("Щука ЩУКА", "Shchuka SHCHUKA")]
    [InlineData("Ελλάδα", "Ellada")]
    [InlineData("日本.txt", "日本.txt")]
    [InlineData("", "")]
    [InlineData("é", "e")]
    public void Odd_scripts_transliterate_or_pass_through(string text, string expected)
    {
        Transliteration.ToLatin(text).Should().Be(expected);
    }


    // ---------------------------------------------------------------- folding, quick search, filter

    [Fact]
    public void Regression_fold_throws_on_a_name_with_an_unpaired_surrogate()
    {
        // NTFS allows unpaired UTF-16 surrogates in names; quick search and the filter fold every name in the panel.
        var act = () => TextFold.Fold("a\uD800b.txt");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Żółć", "zolc")]
    [InlineData("ŁÓDŹ", "lodz")]
    [InlineData("Straße", "strasse")]
    [InlineData("😀Ab", "😀ab")]
    [InlineData("", "")]
    public void Fold(string text, string expected) => TextFold.Fold(text).Should().Be(expected);

    [Fact]
    public void Quick_search_with_diacritics_case_and_dots()
    {
        string[] names = ["Readme.md", "raport.2024.pdf", "Żółw.txt", ".gitignore"];
        QuickSearch.Find(names, n => n, "ZOL", 0).Should().Be(2);
        QuickSearch.Find(names, n => n, "żół", 0).Should().Be(2);
        QuickSearch.Find(names, n => n, ".2024", 0).Should().Be(1);
        QuickSearch.Find(names, n => n, ".", 0).Should().Be(3); // the name that starts with it wins
        QuickSearch.Find(names, n => n, "*.md", 0).Should().Be(0);
        QuickSearch.Find(names, n => n, "?", 2).Should().Be(2);
        QuickSearch.Find(names, n => n, "zzz", 0).Should().Be(-1);
        QuickSearch.Find(names, n => n, string.Empty, 0).Should().Be(-1);
        QuickSearch.Find(names, n => n, "r", int.MinValue).Should().BeInRange(0, 1);
        QuickSearch.Find(names, n => n, "r", -1, forward: false).Should().Be(1);
        QuickSearch.Count(names, n => n, "*").Should().Be(4);
        QuickSearch.Find(Array.Empty<string>(), n => n, "a", 0).Should().Be(-1);
    }

    [Fact]
    public void Quick_search_with_emoji_names()
    {
        string[] names = ["a.txt", "😀 party.jpg"];
        QuickSearch.Find(names, n => n, "😀", 0).Should().Be(1);
        QuickSearch.Find(names, n => n, "PARTY", 0).Should().Be(1);
    }

    private static FileEntry File_(string name, long size = 10, DateTimeOffset? modified = null) =>
        new() { Name = name, FullPath = "/x/" + name, IsDirectory = false, Size = size, Modified = modified ?? When };

    [Fact]
    public void Filter_with_blank_or_star_text()
    {
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);
        new PanelFilter { Name = "   " }.IsEmpty.Should().BeTrue();
        new PanelFilter { Name = "   " }.Matcher(now)(File_("a")).Should().BeTrue();
        new PanelFilter { Name = "*" }.Matcher(now)(File_("a")).Should().BeTrue();
        new PanelFilter { Name = "*.*" }.Matcher(now)(File_("noext")).Should().BeTrue();
        new PanelFilter { Name = "; ;" }.Matcher(now)(File_("a")).Should().BeFalse();
    }

    [Fact]
    public void Filter_size_bounds_zero_and_max()
    {
        var now = DateTime.Now;
        var f = new PanelFilter { MinSize = 0, MaxSize = 0 }.Matcher(now);
        f(File_("a", 0)).Should().BeTrue();
        f(File_("a", 1)).Should().BeFalse();
        var g = new PanelFilter { MinSize = long.MaxValue }.Matcher(now);
        g(File_("a", long.MaxValue)).Should().BeTrue();
        g(File_("a", long.MaxValue - 1)).Should().BeFalse();
        new PanelFilter { MinSize = 10, MaxSize = 5 }.Matcher(now)(File_("a", 7)).Should().BeFalse();
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1,5k", 1536L)]
    [InlineData("1.5K", 1536L)]
    [InlineData("  2 M ", 2097152L)]
    [InlineData("10b", 10L)]
    [InlineData("1e30G", long.MaxValue)]
    public void Parse_size(string text, long expected) => PanelFilter.ParseSize(text).Should().Be(expected);

    [Theory]
    [InlineData("-1")]
    [InlineData("K")]
    [InlineData("1T")]
    [InlineData("abc")]
    public void Parse_size_rejects(string text)
    {
        var act = () => PanelFilter.ParseSize(text);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Regression_parse_minutes_throws_non_format_exception()
    {
        // The filter dialog (DialogService) only catches FormatException; "NaN" gives ArgumentException,
        // "Infinity" or "1e300" OverflowException, which escape the click handler.
        var act = () => PanelFilter.ParseMinutes("1e300");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Regression_parse_size_accepts_nan_as_zero_bytes()
    {
        var act = () => PanelFilter.ParseSize("NaN");
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Filter_day_range_includes_the_whole_day_on_a_dst_change()
    {
        var day = new DateOnly(2026, 3, 29); // summer time starts in Europe
        var m = new PanelFilter { DatePreset = FilterDatePreset.Range, From = day, To = day }.Matcher(DateTime.Now);
        var startLocal = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
        var lastLocal = new DateTimeOffset(day.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Local));
        var nextMidnight = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));
        m(File_("a", modified: startLocal)).Should().BeTrue();
        m(File_("a", modified: lastLocal)).Should().BeTrue();
        m(File_("a", modified: nextMidnight)).Should().BeFalse();
        m(File_("a", modified: startLocal.AddTicks(-1))).Should().BeFalse();
    }

    [Fact]
    public void Filter_year_1601_and_unknown_date()
    {
        var m = new PanelFilter { DatePreset = FilterDatePreset.Range, To = new DateOnly(1601, 1, 2) }.Matcher(DateTime.Now);
        m(File_("a", modified: new DateTimeOffset(1601, 1, 1, 12, 0, 0, TimeSpan.Zero))).Should().BeTrue();
        m(File_("a", modified: DateTimeOffset.MinValue)).Should().BeFalse();
    }

    [Fact]
    public void Regression_filter_range_ending_on_the_last_day_of_9999_throws()
    {
        var filter = new PanelFilter { DatePreset = FilterDatePreset.Range, From = new DateOnly(2000, 1, 1), To = DateOnly.MaxValue };
        var act = () => filter.Matcher(DateTime.Now)(File_("a"));
        act.Should().NotThrow();
    }

    [Fact]
    public void Filter_combined_conditions_all_apply()
    {
        var now = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Local);
        var filter = new PanelFilter
        {
            Name = "raport zolc",
            Extensions = PanelFilter.ParseExtensions("*.PDF, .docx;;pdf"),
            MinSize = 5,
            DatePreset = FilterDatePreset.ThisYear,
        };
        filter.Extensions.Should().Equal("PDF", "docx");
        var m = filter.Matcher(now);
        m(File_("Żółć RAPORT.pdf", 10)).Should().BeTrue();
        m(File_("Żółć RAPORT.txt", 10)).Should().BeFalse();
        m(File_("Żółć RAPORT.pdf", 4)).Should().BeFalse();
        m(File_("Żółć.pdf", 10)).Should().BeFalse();
        m(File_("Żółć RAPORT.pdf", 10, new DateTimeOffset(2025, 12, 31, 12, 0, 0, TimeSpan.Zero))).Should().BeFalse();
        m(File_("sub/Żółć raport.docx", 10)).Should().BeTrue();
        m(new FileEntry { Name = "dir", FullPath = "/dir", IsDirectory = true }).Should().BeTrue();
    }

    // ---------------------------------------------------------------- natural sort

    [Theory]
    [InlineData("file2", "file10", -1)]
    [InlineData("file002", "file2", 0)]
    [InlineData("a00", "a0", 0)]
    [InlineData("x99999999999999999999999999", "x100000000000000000000000000", -1)]
    [InlineData("abc", "ABC", 0)]
    [InlineData("1", "a", -1)]
    [InlineData("", "a", -1)]
    [InlineData("a", "a1", -1)]
    public void Natural_order_cases(string a, string b, int expected)
    {
        var c = new NaturalStringComparer(CultureInfo.InvariantCulture);
        Math.Sign(c.Compare(a, b)).Should().Be(expected);
        Math.Sign(c.Compare(b, a)).Should().Be(-expected);
    }

    [Fact]
    public void Regression_natural_order_of_non_ascii_digits_is_not_numeric()
    {
        var c = new NaturalStringComparer(CultureInfo.InvariantCulture);

        // char.IsDigit accepts Arabic-Indic and fullwidth digits, but the run is then compared code unit by code unit:
        // fullwidth "１" (one) sorts after ASCII "2".
        Math.Sign(c.Compare("１", "2")).Should().Be(-1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pl-PL")]
    [InlineData("tr-TR")]
    public void Natural_order_is_a_consistent_total_preorder(string cultureName)
    {
        var c = new NaturalStringComparer(CultureInfo.GetCultureInfo(cultureName));
        string[] atoms = ["a", "A", "b", "0", "1", "9", "00", "10", "-", "_", " ", ".", "ą", "Ą", "ł", "ß", "ss", "é", "é", "٢", "１", "i", "I", "ı", "İ", "😀", "­", "z"];
        var random = new Random(12345);
        var words = new List<string>();
        for (var n = 0; n < 250; n++)
        {
            var sb = new StringBuilder();
            var len = random.Next(0, 5);
            for (var k = 0; k < len; k++)
            {
                sb.Append(atoms[random.Next(atoms.Length)]);
            }

            words.Add(sb.ToString());
        }

        var cmp = new int[words.Count, words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            for (var j = 0; j < words.Count; j++)
            {
                cmp[i, j] = Math.Sign(c.Compare(words[i], words[j]));
            }
        }

        var problems = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            if (cmp[i, i] != 0)
            {
                problems.Add($"reflexive {Show(words[i])}");
            }

            for (var j = 0; j < words.Count; j++)
            {
                if (cmp[i, j] != -cmp[j, i])
                {
                    problems.Add($"antisymmetry {Show(words[i])} {Show(words[j])}");
                }

                if (cmp[i, j] > 0)
                {
                    continue;
                }

                for (var k = 0; k < words.Count; k++)
                {
                    // i <= j and j <= k must give i <= k, strictly when either step is strict.
                    if (cmp[j, k] <= 0 && (cmp[i, k] > 0 || ((cmp[i, j] < 0 || cmp[j, k] < 0) && cmp[i, k] == 0)))
                    {
                        problems.Add($"transitivity {Show(words[i])} {Show(words[j])} {Show(words[k])}: {cmp[i, j]} {cmp[j, k]} {cmp[i, k]}");
                    }
                }
            }
        }

        problems.Take(5).Should().BeEmpty();
    }

    private static string Show(string s) =>
        "\"" + string.Concat(s.Select(ch => ch < 128 && !char.IsControl(ch) ? ch.ToString() : $"\\u{(int)ch:X4}")) + "\"";

    [Fact]
    public void File_entry_comparer_sorts_ties_on_every_column()
    {
        var list = Enumerable.Range(0, 200).Select(i => File_(i % 3 == 0 ? "A.txt" : i % 3 == 1 ? "a.txt" : "a.TXT", size: i % 2)).ToList();
        list.Add(new FileEntry { Name = "..", FullPath = "/", IsDirectory = true, IsParentLink = true });
        list.Add(new FileEntry { Name = "dir", FullPath = "/dir", IsDirectory = true });
        foreach (var column in Enum.GetValues<SortColumn>())
        {
            foreach (var direction in Enum.GetValues<SortDirection>())
            {
                var sorted = list.ToList();
                sorted.Sort(new FileEntryComparer(new SortSpec(column, direction)));
                sorted[0].IsParentLink.Should().BeTrue();
                sorted[1].Name.Should().Be("dir");
            }
        }
    }

    // ---------------------------------------------------------------- text decoder

    [Fact]
    public void Decoder_boms()
    {
        TextDecoder.Detect([0xEF, 0xBB, 0xBF]).Should().BeOfType<UTF8Encoding>();
        TextDecoder.Detect([0xFF, 0xFE]).CodePage.Should().Be(1200);
        TextDecoder.Detect([0xFE, 0xFF]).CodePage.Should().Be(1201);
    }

    [Fact]
    public void Decoder_tiny_samples()
    {
        TextDecoder.Detect([]).Should().BeOfType<UTF8Encoding>();
        TextDecoder.Detect([0x41]).Should().BeOfType<UTF8Encoding>();
        TextDecoder.Detect([0x00]).Should().BeOfType<UTF8Encoding>();
        TextDecoder.Detect([0xC3]).Should().BeOfType<UTF8Encoding>(); // a sequence cut by the sample end
        TextDecoder.LooksBinary([]).Should().BeFalse();
        TextDecoder.LooksBinary([0x00]).Should().BeTrue();
        TextDecoder.LooksBinary([0x41]).Should().BeFalse();
        TextDecoder.LooksBinary(new byte[100]).Should().BeTrue();
    }

    [Fact]
    public void Decoder_utf16_without_bom()
    {
        var le = Encoding.Unicode.GetBytes("Hello, plain world of text!");
        var be = Encoding.BigEndianUnicode.GetBytes("Hello, plain world of text!");
        TextDecoder.Detect(le).CodePage.Should().Be(1200);
        TextDecoder.Detect(be).CodePage.Should().Be(1201);
        TextDecoder.LooksBinary(le).Should().BeFalse();
        TextDecoder.LooksBinary(be).Should().BeFalse();
    }

    [Fact]
    public void Decoder_invalid_utf8()
    {
        TextDecoder.Detect([0x61, 0x62, 0xFF]).Should().NotBeOfType<UTF8Encoding>();
        TextDecoder.IsValidUtf8([0xC0, 0x80]).Should().BeFalse(); // overlong NUL
        TextDecoder.IsValidUtf8([0x80]).Should().BeFalse(); // lone continuation
        TextDecoder.IsValidUtf8([0xE2, 0x82]).Should().BeTrue(); // cut at the end
        TextDecoder.IsValidUtf8("zażółć"u8).Should().BeTrue();
    }

    [Fact]
    public void Regression_utf16_with_bom_but_few_ascii_letters_looks_binary()
    {
        // Notepad's "Unicode" (UTF-16 LE with BOM): Cyrillic text has no zero high bytes, so the UTF-16 statistics fail,
        // the BOM is ignored, and the zero bytes of spaces make it "binary": hex viewer, no content search, no diff.
        byte[] russian = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Привет мир, это текст")];
        TextDecoder.LooksBinary(russian).Should().BeFalse();
    }

    // ---------------------------------------------------------------- file search content

    private bool Contains(string name, byte[] content, string text, bool regex = false, bool caseSensitive = false)
    {
        var path = _s.P(name);
        File.WriteAllBytes(path, content);
        var criteria = new SearchCriteria(_s.Root, Text: text, TextRegex: regex, CaseSensitive: caseSensitive);
        return FileSearch.ContainsText(new FileInfo(path), criteria.TextMatcher()!, CancellationToken.None);
    }

    [Fact]
    public void Content_search_odd_files()
    {
        Contains("lf.txt", "one\ntwo"u8.ToArray(), "two").Should().BeTrue();
        Contains("crlf.txt", "one\r\ntwo\r\n"u8.ToArray(), "^two$", regex: true).Should().BeTrue();
        Contains("cr.txt", "one\rtwo"u8.ToArray(), "^two$", regex: true).Should().BeTrue();
        Contains("u16.txt", Encoding.Unicode.GetBytes("first line\r\nZażółć gęślą jaźń\r\n"), "GĘŚLĄ").Should().BeTrue();
        Contains("u16bom.txt", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("plain ascii text in utf-16")], "ASCII").Should().BeTrue();
        Contains("u8bom.txt", [.. Encoding.UTF8.GetPreamble(), .. "żółw"u8.ToArray()], "^żółw$", regex: true).Should().BeTrue();
        Contains("bin.dat", [0x41, 0x42, 0x00, 0x43], "AB").Should().BeFalse();
        Contains("empty.txt", [], "a").Should().BeFalse();
        Contains("case.txt", "Hello"u8.ToArray(), "hello", caseSensitive: true).Should().BeFalse();
        Contains("one.txt", [0x61], "a").Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task Content_search_with_catastrophic_regex_gives_up()
    {
        var path = _s.File("evil.txt", new string('a', 5000) + "!");
        var criteria = new SearchCriteria(_s.Root, Text: "^(a|aa)+$", TextRegex: true);
        var result = await Task.Run(() => FileSearch.ContainsText(new FileInfo(path), criteria.TextMatcher()!, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        result.Should().BeFalse();
    }

    [Fact]
    public void Invalid_search_regex_throws_argument_exception()
    {
        var text = () => new SearchCriteria(_s.Root, Text: "[", TextRegex: true).TextMatcher();
        text.Should().Throw<ArgumentException>();
        var name = () => new SearchCriteria(_s.Root, Masks: "(", NameRegex: true).NameMatcher();
        name.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------- text diff

    [Fact]
    public void Diff_empty_inputs()
    {
        TextDiff.Compare([], []).Identical.Should().BeTrue();
        TextDiff.Compare([], ["a"]).Added.Should().Be(1);
        TextDiff.Compare(["a"], []).Removed.Should().Be(1);
    }

    [Fact]
    public void Diff_whitespace_only_changes_are_changes()
    {
        TextDiff.Compare(["a b", "c"], ["a  b", "c "]).Changed.Should().Be(2);
    }

    [Fact]
    public async Task Diff_reads_crlf_and_lf_without_final_newline_as_the_same_lines()
    {
        var a = _s.File("a.txt", "one\r\ntwo\r\n");
        var b = _s.File("b.txt", "one\ntwo");
        var pair = await TextDiff.ReadPairAsync(a, b, TestContext.Current.CancellationToken);
        TextDiff.Compare(pair!.Value.Left, pair.Value.Right).Identical.Should().BeTrue();
    }

    [Fact]
    public async Task Diff_reads_utf16_and_utf8_bom_as_text()
    {
        var a = _s.P("a.txt");
        File.WriteAllText(a, "Zazolc\njazn\n", Encoding.Unicode);
        var b = _s.P("b.txt");
        File.WriteAllText(b, "Zażółć\njaźń\n", new UTF8Encoding(true));
        var pair = await TextDiff.ReadPairAsync(a, b, TestContext.Current.CancellationToken);
        pair!.Value.Left.Should().Equal("Zazolc", "jazn");
        pair.Value.Right.Should().Equal("Zażółć", "jaźń");
    }

    [Fact]
    public void Diff_huge_with_one_change()
    {
        var left = Enumerable.Range(0, 200_000).Select(i => "line " + i).ToArray();
        var right = left.ToArray();
        right[123_456] = "changed";
        var r = TextDiff.Compare(left, right);
        r.Changed.Should().Be(1);
        r.Rows.Should().HaveCount(200_000);
    }

    [Fact]
    public void Diff_randomized_rows_rebuild_both_sides_with_a_shortest_script()
    {
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var left = Enumerable.Range(0, random.Next(0, 15)).Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();
            var right = Enumerable.Range(0, random.Next(0, 15)).Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();
            var r = TextDiff.Compare(left, right);
            r.Rows.Where(x => x.LeftText is not null).Select(x => x.LeftText).Should().Equal(left);
            r.Rows.Where(x => x.RightText is not null).Select(x => x.RightText).Should().Equal(right);
            r.Rows.Count(x => x.Kind == DiffKind.Same).Should().Be(Lcs(left, right));
            r.Identical.Should().Be(left.SequenceEqual(right));
        }
    }

    private static int Lcs(string[] a, string[] b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
            }
        }

        return t[0, 0];
    }

    [Fact]
    public void Regression_diff_memory_grows_with_file_length_times_differences()
    {
        // 10 000 lines, every tenth changed: edit distance 2 000. Myers keeps a full copy of V (2 * 20 000 + 3 ints)
        // per step: ~320 MB here; 16 MB files with a few thousand changes need tens of GB.
        var left = Enumerable.Range(0, 10_000).Select(i => "line " + i).ToArray();
        var right = left.Select((l, i) => i % 10 == 5 ? l + " changed" : l).ToArray();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var r = TextDiff.Compare(left, right);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        r.Changed.Should().Be(1_000);
        allocated.Should().BeLessThan(100L * 1024 * 1024);
    }

    // ---------------------------------------------------------------- directory compare

    private sealed class FakeSide(params CompareEntry[] root) : ICompareSide
    {
        public Task<IReadOnlyList<CompareEntry>> ListAsync(string relative, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CompareEntry>>(relative.Length == 0 ? root : []);

        public Task<DateTimeOffset?> ExactModifiedAsync(string relative, CancellationToken cancellationToken) => Task.FromResult<DateTimeOffset?>(null);

        public Task<Stream> OpenReadAsync(string relative, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());
    }

    [Fact]
    public async Task Directory_compare_time_extremes_and_tolerance()
    {
        var left = new FakeSide(
            new CompareEntry("min", false, 1, DateTimeOffset.MinValue),
            new CompareEntry("tol", false, 1, When),
            new CompareEntry("sz", false, 1, When));
        var right = new FakeSide(
            new CompareEntry("min", false, 1, DateTimeOffset.MaxValue),
            new CompareEntry("tol", false, 1, When.AddSeconds(2)),
            new CompareEntry("sz", false, 2, When.AddSeconds(-1)));
        var r = await DirectoryCompare.RunAsync(left, right, new PlainCommander.Core.Compare.CompareOptions(), token: TestContext.Current.CancellationToken);
        r.Items["min"].Should().Be(Difference.RightNewer);
        r.Items.Should().NotContainKey("tol");
        r.Items["sz"].Should().Be(Difference.Different);
    }

    // ---------------------------------------------------------------- formatting, plural

    [Theory]
    [InlineData(0L, "plików")]
    [InlineData(1L, "plik")]
    [InlineData(2L, "pliki")]
    [InlineData(4L, "pliki")]
    [InlineData(5L, "plików")]
    [InlineData(12L, "plików")]
    [InlineData(13L, "plików")]
    [InlineData(22L, "pliki")]
    [InlineData(112L, "plików")]
    [InlineData(114L, "plików")]
    [InlineData(122L, "pliki")]
    [InlineData(1001L, "plików")]
    [InlineData(long.MaxValue, "plików")] // ...807
    public void Polish_plural(long count, string expected) =>
        Plural.Polish(count, "plik", "pliki", "plików").Should().Be(expected);

    [Theory]
    [InlineData(-1L)]
    [InlineData(-2L)]
    [InlineData(long.MinValue)]
    public void Polish_plural_of_negative_numbers_does_not_throw(long count)
    {
        var act = () => Plural.Polish(count, "plik", "pliki", "plików");
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(long.MaxValue, "8192 PB")]
    [InlineData(-1L, "-1 B")]
    public void Human_size_extremes(long bytes, string expected) =>
        EntryFormatter.HumanSize(bytes, CultureInfo.InvariantCulture).Should().Be(expected);

    [Fact]
    public void Dates_at_the_edges_format_without_throwing()
    {
        var culture = CultureInfo.InvariantCulture;
        foreach (var d in new[] { DateTimeOffset.MaxValue, new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(1, 1, 1, 0, 0, 1, TimeSpan.Zero) })
        {
            var e = File_("a", modified: d);
            var act = () => EntryFormatter.Date(e, culture) + EntryFormatter.DateOnly(e, culture);
            act.Should().NotThrow();
        }

        EntryFormatter.Date(File_("a", modified: DateTimeOffset.MinValue), culture).Should().BeEmpty();
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _old = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _old;
    }
}
