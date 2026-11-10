using System.Globalization;
using AwesomeAssertions;
using PlainCommander.Core.Formatting;
using PlainCommander.Core.Model;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class EntryFormatterTests
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    private static FileEntry File(long size = 0, FileAttributes attributes = FileAttributes.Normal) => new()
    {
        Name = "a.txt",
        FullPath = "/a.txt",
        IsDirectory = false,
        Size = size,
        Attributes = attributes,
    };

    [Fact]
    public void Size_uses_thousand_separators_and_marks_directories()
    {
        EntryFormatter.Size(File(1234567), CultureInfo.InvariantCulture).Should().Be("1,234,567");
        EntryFormatter.Size(new FileEntry { Name = "d", FullPath = "/d", IsDirectory = true }, Pl).Should().Be("<DIR>");
    }

    [Fact]
    public void Date_is_local_short_date_and_time()
    {
        var local = new DateTime(2026, 10, 29, 15, 23, 0, DateTimeKind.Local);
        var entry = File() with { Modified = new DateTimeOffset(local) };
        EntryFormatter.Date(entry, Pl).Should().Be("29.10.2026 15:23");
    }

    [Fact]
    public void Date_has_two_digit_day_month_and_hour_so_the_column_lines_up()
    {
        // ICU gives pl-PL "d.MM.yyyy", which left "5.10.2026" next to "16.10.2026".
        var culture = (CultureInfo)Pl.Clone();
        culture.DateTimeFormat.ShortDatePattern = "d.M.yyyy";
        culture.DateTimeFormat.ShortTimePattern = "H:mm";
        var entry = File() with { Modified = new DateTimeOffset(new DateTime(2026, 1, 5, 9, 7, 0, DateTimeKind.Local)) };
        EntryFormatter.Date(entry, culture).Should().Be("05.01.2026 09:07");
        EntryFormatter.DateOnly(entry, culture).Should().Be("05.01.2026");
    }

    [Theory]
    [InlineData("M/d/yyyy", "MM/dd/yyyy")]
    [InlineData("yyyy-MM-dd", "yyyy-MM-dd")]
    [InlineData("d MMMM yyyy", "dd MMMM yyyy")]
    [InlineData("dddd, d 'd' M", "dddd, dd 'd' MM")]
    [InlineData("h:mm tt", "hh:mm tt")]
    public void Column_pattern_pads_only_lone_fields(string shortDate, string expected)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.DateTimeFormat.ShortDatePattern = shortDate;
        EntryFormatter.ColumnDatePattern(culture).Should().Be(expected);
    }

    [Fact]
    public void Parent_link_has_no_date_and_no_attributes()
    {
        var up = FileEntry.ParentLink("/");
        EntryFormatter.Date(up, Pl).Should().BeEmpty();
        EntryFormatter.Attributes(up).Should().BeEmpty();
    }

    [Theory]
    [InlineData(FileAttributes.Normal, "----")]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Archive, "ra--")]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, "--hs")]
    public void Attributes_follow_rahs_order(FileAttributes attributes, string expected)
    {
        EntryFormatter.Attributes(File(attributes: attributes)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1536L, "1,5 KB")]
    [InlineData(51_758_407_270L, "48,2 GB")]
    [InlineData(214_748_364_800L, "200 GB")]
    public void Human_size_is_1024_based(long bytes, string expected)
    {
        EntryFormatter.HumanSize(bytes, Pl).Should().Be(expected);
    }
}
