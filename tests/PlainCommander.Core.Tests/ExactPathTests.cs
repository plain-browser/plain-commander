using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class ExactPathTests
{
    [Fact]
    public void Full_keeps_a_trailing_dot_and_space_in_the_last_name()
    {
        var root = Path.GetTempPath();

        ExactPath.Full(Path.Combine(root, "folder.")).Should().EndWith("folder.");
        ExactPath.Full(Path.Combine(root, "name ")).Should().EndWith("name ");
        ExactPath.Full(Path.Combine(root, "plain")).Should().Be(Path.GetFullPath(Path.Combine(root, "plain")));
        ExactPath.Full(Path.Combine(root, "a", "..")).Should().Be(Path.GetFullPath(Path.Combine(root, "a", "..")), "dot segments are still resolved");
    }

    [Fact]
    public void Native_prefixes_only_on_windows_and_only_when_needed()
    {
        var plain = Path.Combine(Path.GetTempPath(), "plain", "file.txt");
        var dotted = Path.Combine(Path.GetTempPath(), "folder.", "file.txt");

        ExactPath.Native(plain).Should().Be(plain);
        if (OperatingSystem.IsWindows())
        {
            ExactPath.Native(dotted).Should().Be(@"\\?\" + dotted);
            ExactPath.Native(@"\\server\share\dir.").Should().Be(@"\\?\UNC\server\share\dir.");
        }
        else
        {
            ExactPath.Native(dotted).Should().Be(dotted);
        }
    }

    [Fact]
    public void Prefixed_always_adds_the_prefix_on_windows()
    {
        if (OperatingSystem.IsWindows())
        {
            ExactPath.Prefixed(@"C:\work\plain").Should().Be(@"\\?\C:\work\plain");
            ExactPath.Prefixed(@"\\?\C:\work").Should().Be(@"\\?\C:\work");
            ExactPath.Prefixed(@"\\srv\share").Should().Be(@"\\?\UNC\srv\share");
        }
        else
        {
            ExactPath.Prefixed("/tmp/x").Should().Be("/tmp/x");
        }
    }

    [Fact]
    public void DisplayText_cleans_prefixes_inside_messages()
    {
        ExactPath.DisplayText(@"Nie można odnaleźć: '\\?\UNC\srv\share\x' i '\\?\C:\a.'").Should().Be(@"Nie można odnaleźć: '\\srv\share\x' i 'C:\a.'");
    }

    [Fact]
    public void Display_strips_both_prefixes()
    {
        ExactPath.Display(@"\\?\C:\work\folder.").Should().Be(@"C:\work\folder.");
        ExactPath.Display(@"\\?\UNC\server\share\x").Should().Be(@"\\server\share\x");
        ExactPath.Display(@"C:\work").Should().Be(@"C:\work");
    }

    [Fact]
    public void Round_trip_through_the_file_system_keeps_the_dotted_name()
    {
        var dir = Directory.CreateTempSubdirectory("pc-exact-").FullName;
        try
        {
            var dotted = Path.Combine(dir, "kropka.");
            File.WriteAllText(ExactPath.Native(dotted), "x");

            File.Exists(ExactPath.Native(dotted)).Should().BeTrue();
            Directory.EnumerateFiles(ExactPath.Native(dir)).Select(ExactPath.Display).Should().ContainSingle().Which.Should().EndWith("kropka.");
        }
        finally
        {
            Directory.Delete(ExactPath.Native(dir), recursive: true);
        }
    }
}
