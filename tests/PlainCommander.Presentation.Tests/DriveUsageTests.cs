using AwesomeAssertions;
using PlainCommander.Core.Model;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class DriveUsageTests
{
    private static DriveEntry Drive(long total, long free, bool ready = true) =>
        new(@"C:\", "Windows", DriveType.Fixed, total, free, ready);

    [Fact]
    public void Used_fraction_is_one_minus_free_share()
    {
        Texts.DriveUsedFraction(Drive(200, 50)).Should().BeApproximately(0.75, 0.001);
    }

    [Fact]
    public void Not_ready_or_empty_drive_reports_nothing_used()
    {
        Texts.DriveUsedFraction(Drive(200, 50, ready: false)).Should().Be(0);
        Texts.DriveUsedFraction(Drive(0, 0)).Should().Be(0);
    }

    [Fact]
    public void Footer_text_has_no_volume_label()
    {
        Texts.DriveFree(Drive(200L << 30, 157L << 30)).Should().NotContain("Windows").And.Contain("157");
        Texts.DriveSummary(Drive(200L << 30, 157L << 30)).Should().StartWith("Windows");
    }
}
