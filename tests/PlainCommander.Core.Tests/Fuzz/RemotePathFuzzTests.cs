using AwesomeAssertions;
using PlainCommander.Core.Remote;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>Remote paths are text the user types or a server lists: parsing never crashes and is stable.</summary>
public sealed class RemotePathFuzzTests
{
    private static readonly string[] Schemes = ["sftp://", "ftp://", "ftps://", "s3://", "SFTP://", "Ftp://", "http://", "sftp:/", "", "sftp:///", "s3://@"];

    private static readonly string[] AuthorityParts =
        ["user", "u%40x", "%", "%zz", "@", "@@", ":", "::", "host", "HOST", "hÓst", "[", "]", "[::1]", "[fe80::1%eth0]", "::1", "a:b:c", ":22", ":0", ":65535", ":65536", ":-1", ":99999999999", ":١٢", "İ", "\uD83D", ".", "..", " ", "\0", "\\", "?", "#"];

    private static readonly string[] InnerParts = ["/", "//", "a", "..", ".", "...", "\\", "..\\x", "%2F", " ", "é", "é", "\uD83D", "?", "#", "~", "\0", ":", "C:"];

    private static string RemoteText(Gen g)
    {
        if (g.Bool(15))
        {
            return g.NastyString();
        }

        return g.Pick(Schemes) + g.FromAlphabet(AuthorityParts, 4) + g.FromAlphabet(InnerParts, 6);
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Found by the fuzzer (seed 202): "ftp://::]" parsed to host "::]", written back as "[::]]" and read again as host "::".</summary>
    [Theory]
    [InlineData("ftp://::]")]
    [InlineData("sftp://a]b/x")]
    [InlineData("sftp://[[::1]]/x")]
    public void Regression_host_with_a_stray_bracket_is_not_a_location(string text)
    {
        RemotePath.Parse(text).Should().BeNull();
    }

    [Fact]
    public void Parsing_never_throws_and_a_parsed_location_is_well_formed()
    {
        FuzzKit.Check(
            seed: 201,
            iterations: 3000,
            RemoteText,
            text =>
            {
                foreach (var location in new[] { RemotePath.Parse(text), RemotePath.ParseTyped(text) })
                {
                    if (location is null)
                    {
                        continue;
                    }

                    location.Host.Should().NotBeEmpty();
                    location.Port.Should().BeInRange(1, 65535);
                    location.Inner.Should().StartWith("/");
                    location.Inner.Split('/').Skip(1).Should().NotContain(s => s == "." || s == "..");
                    (location.Inner == "/" || !location.Inner.EndsWith('/')).Should().BeTrue();
                    _ = location.Name;
                    _ = location.Root;
                }

                _ = RemotePath.IsRemote(text);
                _ = RemotePath.NameOf(text);
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void Parse_of_a_full_path_gives_the_same_location_back()
    {
        FuzzKit.Check(
            seed: 202,
            iterations: 3000,
            RemoteText,
            text =>
            {
                // A user name with a lone surrogate cannot be escaped as UTF-8: it comes back as U+FFFD (judged acceptable).
                if (RemotePath.Parse(text) is not { } location || HasLoneSurrogate(location.User))
                {
                    return;
                }

                var again = RemotePath.Parse(location.FullPath);
                again.Should().Be(location, $"FullPath {FuzzKit.Literal(location.FullPath)}");
                again!.FullPath.Should().Be(location.FullPath);
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void Resolve_never_climbs_above_the_server_root()
    {
        FuzzKit.Check(
            seed: 203,
            iterations: 3000,
            g => (Folder: g.FromAlphabet(["/", "a", "b", "..", "."], 6), Relative: g.Bool(20) ? g.NastyString() : g.FromAlphabet(["/", "\\", "..", ".", "x", "...", " "], 8)),
            input =>
            {
                var root = new RemoteLocation(RemoteProtocol.Sftp, "u", "host", 22, RemotePath.NormalizeInner(input.Folder));
                var resolved = root.Resolve(input.Relative);
                resolved.Inner.Should().StartWith("/");
                resolved.Inner.Split('/').Should().NotContain("..");
                resolved.Host.Should().Be("host");
                resolved.Root.Should().Be(root.Root);
                RemotePath.NormalizeInner(resolved.Inner).Should().Be(resolved.Inner, "normalizing is idempotent");
            });
    }

    [Fact]
    public void Child_and_parent_are_inverse_for_single_names()
    {
        FuzzKit.Check(
            seed: 204,
            iterations: 2000,
            g => g.NastyString(3),
            name =>
            {
                var folder = new RemoteLocation(RemoteProtocol.Ftp, string.Empty, "h", 21, "/a/b");
                if (!RemotePath.IsSingleName(name))
                {
                    var act = () => folder.Child(name);
                    act.Should().Throw<ArgumentException>();
                    return;
                }

                var child = folder.Child(name);
                child.Name.Should().Be(name);
                child.Parent.Should().Be(folder);
            },
            FuzzKit.ShrinkString);
    }

    [Fact]
    public void LocalName_is_one_name_that_stays_in_its_folder()
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        FuzzKit.Check(
            seed: 205,
            iterations: 3000,
            g => g.NastyString(),
            name =>
            {
                var local = RemotePath.LocalName(name);
                local.Should().NotBeEmpty();
                local.Should().NotBe(".").And.NotBe("..");
                local.Length.Should().Be(name is "" or "." or ".." ? 1 : name.Length);
                string.Concat(local.Where(invalid.Contains)).Should().BeEmpty();
                var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pc-download"));
                FileSystem.ExactPath.Full(Path.Combine(dir, local)).Should().StartWith(dir + Path.DirectorySeparatorChar);
            },
            FuzzKit.ShrinkString);
    }
}
