using AwesomeAssertions;
using PlainCommander.Core.Navigation;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class QuickSearchTests
{
    private static readonly string[] Names = ["..", "Documents", "Downloads", "Music", "readme.md", "Źródła"];

    private static int Find(string text, int start = 0, bool forward = true) =>
        QuickSearch.Find(Names, n => n, text, start, forward);

    [Fact]
    public void Finds_prefix_ignoring_case()
    {
        Find("do").Should().Be(1);
        Find("MU").Should().Be(3);
        Find("źr").Should().Be(5);
    }

    [Fact]
    public void Starts_at_given_index_and_wraps()
    {
        Find("do", start: 2).Should().Be(2);
        Find("do", start: 3).Should().Be(1);
    }

    [Fact]
    public void Searches_backwards()
    {
        Find("do", start: 0, forward: false).Should().Be(2);
        Find("do", start: 1, forward: false).Should().Be(1);
    }

    [Fact]
    public void Supports_wildcards()
    {
        Find("*.md").Should().Be(4);
        Find("d?w").Should().Be(2);
    }

    [Fact]
    public void Returns_minus_one_when_nothing_matches()
    {
        Find("xyz").Should().Be(-1);
        Find(string.Empty).Should().Be(-1);
        QuickSearch.Find(Array.Empty<string>(), n => n, "a", 0).Should().Be(-1);
    }

    private static readonly string[] Reports = ["..", "Kwartalny raport.docx", "notatki.txt", "Raport 2026.pdf", "Żółć.txt", "Łódź-plan.png"];

    [Fact]
    public void Finds_a_fragment_anywhere_but_prefers_a_name_that_starts_with_it()
    {
        QuickSearch.Find(Reports, n => n, "rap", 0).Should().Be(3, "Raport… starts with the text");
        QuickSearch.Find(Reports, n => n, "kwart", 0).Should().Be(1);
        QuickSearch.Find(Reports, n => n, "tatk", 0).Should().Be(2, "nothing starts with it, so a name containing it");
        QuickSearch.FindNext(Reports, n => n, "rap", 4, forward: true).Should().Be(1, "Up/Down visit every match");
        QuickSearch.Count(Reports, n => n, "rap").Should().Be(2);
    }

    [Fact]
    public void Polish_letters_and_case_do_not_matter()
    {
        QuickSearch.Find(Reports, n => n, "zolc", 0).Should().Be(4);
        QuickSearch.Find(Reports, n => n, "lodz", 0).Should().Be(5, "ł has no decomposition and is mapped by hand");
        QuickSearch.Find(Reports, n => n, "ŻÓŁ", 0).Should().Be(4);
        TextFold.Fold("Straße ĐØ").Should().Be("strasse do");
    }
}
