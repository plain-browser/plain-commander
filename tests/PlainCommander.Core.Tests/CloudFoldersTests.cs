using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class CloudFoldersTests
{
    [Fact]
    public void The_same_folder_from_registry_and_environment_counts_once_and_missing_folders_are_left_out()
    {
        var home = Path.Combine(Path.GetTempPath(), "cloud-test");
        var personal = Path.Combine(home, "OneDrive");
        var work = Path.Combine(home, "OneDrive - Firma");
        string[] existing = [personal, work];

        var found = CloudFolders.Merge(
            [personal + Path.DirectorySeparatorChar, work, personal.ToLowerInvariant(), Path.Combine(home, "Gone")],
            p => existing.Contains(p, StringComparer.OrdinalIgnoreCase));

        found.Select(f => f.Name).Should().Equal("OneDrive", "OneDrive - Firma");
        found.Select(f => f.ShortName).Should().Equal("OneDrive", "Firma");
    }

    [Fact]
    public void Find_does_not_throw_off_Windows()
    {
        var act = () => CloudFolders.Find();

        act.Should().NotThrow();
    }
}
