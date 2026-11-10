using AwesomeAssertions;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class PathSegmentTests
{
    [Fact]
    public void Windows_path_splits_into_drive_and_folders_with_cumulative_paths()
    {
        var segments = PathSegment.Split(@"C:\work\src");

        segments.Select(s => s.Name).Should().Equal("C:", "work", "src");
        segments.Select(s => s.FullPath).Should().Equal(@"C:\", @"C:\work", @"C:\work\src");
    }

    [Fact]
    public void The_temporary_archive_of_an_encrypted_file_shows_the_encrypted_files_name()
    {
        PlainCommander.Core.Crypto.PceBrowse.Register(@"C:\temp\pce-1\notes.txt.tar", @"D:\secret\notes.txt.pce");

        var segments = PathSegment.Split(@"C:\temp\pce-1\notes.txt.tar\inner");

        segments.Select(s => s.Name).Should().Equal("C:", "temp", "pce-1", "notes.txt.pce", "inner");
        segments[3].FullPath.Should().Be(@"C:\temp\pce-1\notes.txt.tar", "the crumb still leads into the archive");
        PanelTab.TitleOf(@"C:\temp\pce-1\notes.txt.tar").Should().Be("notes.txt.pce");
    }

    [Fact]
    public void Drive_root_is_a_single_segment()
    {
        var segments = PathSegment.Split(@"D:\");

        segments.Should().ContainSingle().Which.Should().Be(new PathSegment("D:", @"D:\"));
    }

    [Fact]
    public void Unc_path_leads_up_to_the_server_and_the_network()
    {
        var segments = PathSegment.Split(@"\\nas\media\films\2026");

        segments.Select(s => s.Name).Should().Equal(Texts.Network, "nas", "media", "films", "2026");
        segments.Select(s => s.FullPath).Should().Equal("net://", "net://nas", @"\\nas\media\", @"\\nas\media\films", @"\\nas\media\films\2026");
    }

    [Fact]
    public void Network_places_split_into_network_and_server()
    {
        PathSegment.Split("net://").Select(s => s.FullPath).Should().Equal("net://");
        PathSegment.Split("net://nas").Select(s => s.Name).Should().Equal(Texts.Network, "nas");
    }

    [Fact]
    public void Unix_path_starts_at_root()
    {
        var segments = PathSegment.Split("/tmp/x");

        segments.Select(s => s.Name).Should().Equal("/", "tmp", "x");
        segments.Select(s => s.FullPath).Should().Equal("/", "/tmp", "/tmp/x");
    }

    [Fact]
    public void Empty_path_gives_no_segments()
    {
        PathSegment.Split(string.Empty).Should().BeEmpty();
    }
}
