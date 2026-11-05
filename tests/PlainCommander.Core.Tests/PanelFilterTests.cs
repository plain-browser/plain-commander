using AwesomeAssertions;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class PanelFilterTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Local);

    private static FileEntry File(string name, long size = 100, DateTime? modified = null, DateTime? created = null) => new()
    {
        Name = name,
        FullPath = "/x/" + name,
        IsDirectory = false,
        Size = size,
        Modified = modified ?? Now.AddDays(-100),
        Created = created,
    };

    private static FileEntry Folder(string name) => new() { Name = name, FullPath = "/x/" + name, IsDirectory = true };

    private static bool Passes(PanelFilter filter, FileEntry entry) => filter.Matcher(Now)(entry);

    [Fact]
    public void Several_words_must_all_be_in_the_name_in_any_order_without_Polish_letters()
    {
        var filter = PanelFilter.FromText("2026 raport");

        Passes(filter, File("Raport kwartalny 2026.pdf")).Should().BeTrue();
        Passes(filter, File("raport 2025.pdf")).Should().BeFalse();
        Passes(PanelFilter.FromText("zrodla"), File("Źródła.txt")).Should().BeTrue();
        Passes(PanelFilter.FromText("*.pdf;*.docx"), File("a.docx")).Should().BeTrue();
        Passes(PanelFilter.FromText("*.pdf;*.docx"), File("a.txt")).Should().BeFalse();
    }

    [Fact]
    public void Folders_stay_unless_files_only_and_dot_dot_always_stays()
    {
        var filter = new PanelFilter { Extensions = ["pdf"] };

        Passes(filter, Folder("docs")).Should().BeTrue();
        Passes(filter with { FilesOnly = true }, Folder("docs")).Should().BeFalse();
        Passes(filter with { FilesOnly = true }, FileEntry.ParentLink("/")).Should().BeTrue();
    }

    [Fact]
    public void Extensions_ignore_case_and_dots()
    {
        var filter = new PanelFilter { Extensions = PanelFilter.ParseExtensions(".PDF, docx;*.txt") };

        filter.Extensions.Should().Equal("PDF", "docx", "txt");
        Passes(filter, File("a.pdf")).Should().BeTrue();
        Passes(filter, File("b.Docx")).Should().BeTrue();
        Passes(filter, File("c.png")).Should().BeFalse();
        Passes(filter, File("pdf")).Should().BeFalse("no extension");
    }

    [Fact]
    public void Ready_date_ranges_follow_the_clock_and_count_whole_days()
    {
        var week = new PanelFilter { DatePreset = FilterDatePreset.Last7Days };

        Passes(week, File("a", modified: new DateTime(2026, 10, 30, 0, 0, 1, DateTimeKind.Local))).Should().BeTrue("6 days back, at the start of that day");
        Passes(week, File("b", modified: new DateTime(2026, 10, 29, 23, 59, 0, DateTimeKind.Local))).Should().BeFalse();
        Passes(new PanelFilter { DatePreset = FilterDatePreset.Today }, File("c", modified: Now.AddHours(-9))).Should().BeTrue();
        Passes(new PanelFilter { DatePreset = FilterDatePreset.ThisYear }, File("d", modified: new DateTime(2025, 12, 31, 12, 0, 0, DateTimeKind.Local))).Should().BeFalse();
    }

    [Fact]
    public void Creation_dates_are_their_own_test_and_unknown_ones_do_not_pass()
    {
        var created = new PanelFilter
        {
            DateField = FilterDateField.Created,
            DatePreset = FilterDatePreset.Range,
            From = new DateOnly(2026, 10, 1),
            To = new DateOnly(2026, 10, 31),
        };

        Passes(created, File("new", modified: Now, created: new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Local))).Should().BeTrue("the last day counts whole");
        Passes(created, File("old", modified: new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Local), created: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Local))).Should().BeFalse();
        Passes(created, File("server", modified: new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Local))).Should().BeFalse("a server file has no creation date");
        Passes(created with { From = null }, File("x", created: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local))).Should().BeTrue("open start");
    }

    [Fact]
    public void Sizes_parse_with_units_and_bound_both_ways()
    {
        PanelFilter.ParseSize("10k").Should().Be(10240);
        PanelFilter.ParseSize("1,5M").Should().Be(1572864);
        PanelFilter.ParseSize(" ").Should().BeNull();
        var act = () => PanelFilter.ParseSize("dużo");
        act.Should().Throw<FormatException>();

        var filter = new PanelFilter { MinSize = 1000, MaxSize = 2000 };
        Passes(filter, File("a", size: 1500)).Should().BeTrue();
        Passes(filter, File("b", size: 999)).Should().BeFalse();
        Passes(filter, File("c", size: 2001)).Should().BeFalse();
    }

    [Fact]
    public void Flat_view_names_are_matched_by_their_file_name()
    {
        Passes(PanelFilter.FromText("*.md"), File(Path.Combine("docs", "readme.md"))).Should().BeTrue();
        Passes(PanelFilter.FromText("docs"), File(Path.Combine("docs", "readme.md"))).Should().BeFalse("the folder part is not the name");
    }

    [Fact]
    public void An_empty_filter_is_no_filter()
    {
        new PanelFilter().IsEmpty.Should().BeTrue();
        PanelFilter.FromText("  ").IsEmpty.Should().BeTrue();
        new PanelFilter { FilesOnly = true }.IsEmpty.Should().BeFalse();
    }
}
