using AwesomeAssertions;
using PlainCommander.Core.Remote;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

public sealed class RemotePathTests
{
    [Theory]
    [InlineData("sftp://user@Host.Example:2222/home/user", RemoteProtocol.Sftp, "user", "host.example", 2222, "/home/user")]
    [InlineData("ftp://host/pub/", RemoteProtocol.Ftp, "", "host", 21, "/pub")]
    [InlineData("ftps://jan%40firma.pl@ftp.firma.pl", RemoteProtocol.Ftps, "jan@firma.pl", "ftp.firma.pl", 21, "/")]
    [InlineData("SFTP://u@h/a/./b/../c", RemoteProtocol.Sftp, "u", "h", 22, "/a/c")]
    [InlineData(@"sftp://u@h/a\b", RemoteProtocol.Sftp, "u", "h", 22, @"/a\b")]
    [InlineData("sftp://u@[::1]:2022/x", RemoteProtocol.Sftp, "u", "::1", 2022, "/x")]
    public void Parses_addresses(string text, RemoteProtocol protocol, string user, string host, int port, string inner)
    {
        var location = RemotePath.Parse(text)!;

        location.Should().Be(new RemoteLocation(protocol, user, host, port, inner));
    }

    [Fact]
    public void Typed_addresses_take_backslashes_for_slashes() =>
        RemotePath.ParseTyped(@"sftp://u@h\a\b")!.Inner.Should().Be("/a/b");

    [Theory]
    [InlineData(@"C:\work")]
    [InlineData("http://host/x")]
    [InlineData("sftp://")]
    [InlineData("sftp://u@h:99999/")]
    [InlineData("sftp://u@h:abc/")]
    public void Rejects_what_is_not_a_server_path(string text) => RemotePath.Parse(text).Should().BeNull();

    [Fact]
    public void Root_parent_child_and_name_round_trip()
    {
        var location = RemotePath.Parse("sftp://jan%40firma.pl@host:22/var/www")!;

        location.Root.Should().Be("sftp://jan%40firma.pl@host:22");
        location.FullPath.Should().Be("sftp://jan%40firma.pl@host:22/var/www");
        RemotePath.Parse(location.FullPath).Should().Be(location);
        location.Name.Should().Be("www");
        location.Parent!.Inner.Should().Be("/var");
        location.Parent!.Parent!.IsRoot.Should().BeTrue();
        location.Parent!.Parent!.Parent.Should().BeNull();
        location.Parent!.Parent!.Name.Should().Be("host");
        location.Child("index.html").Inner.Should().Be("/var/www/index.html");
    }

    [Fact]
    public void Site_root_and_start_location()
    {
        var site = new RemoteSite { Protocol = RemoteProtocol.Ftps, Host = " FTP.Host ", User = "jan", StartPath = "pub/files" };

        site.EffectivePort.Should().Be(21);
        site.Root.Should().Be("ftps://jan@ftp.host:21");
        site.StartLocation.Should().Be("ftps://jan@ftp.host:21/pub/files");
    }
}
