using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Network;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class NetworkTests
{
    [Fact]
    public void Network_places_and_unc_paths_are_told_apart()
    {
        NetworkPath.IsVirtual("net://").Should().BeTrue();
        NetworkPath.ServerOf("net://").Should().BeEmpty();
        NetworkPath.ServerOf("NET://nas/").Should().Be("nas");
        NetworkPath.Normalize("NET://nas/").Should().Be("net://nas");
        NetworkPath.ServerOf(@"C:\x").Should().BeNull();

        NetworkPath.UncServer(@"\\nas\media\x").Should().Be("nas");
        NetworkPath.UncServer(@"\\?\C:\x").Should().BeNull("a long-path prefix is not a server");
        NetworkPath.IsShareRoot(@"\\nas\media").Should().BeTrue();
        NetworkPath.IsShareRoot(@"\\nas\media\").Should().BeTrue();
        NetworkPath.IsShareRoot(@"\\nas\media\x").Should().BeFalse();
        NetworkPath.AsServerPlace(@"\\nas").Should().Be("net://nas");
        NetworkPath.AsServerPlace(@"\\nas\").Should().Be("net://nas");
        NetworkPath.AsServerPlace(@"\\nas\media").Should().BeNull();
    }

    [Fact]
    public async Task Lists_computers_then_shares_and_leads_back_up_from_a_share()
    {
        var browser = new FakeBrowser();
        var fs = new NetworkFileSystem(new FakeDisk(), browser, () => []);

        (await List(fs, "net://")).Select(e => (e.Name, e.FullPath)).Should().Equal(("nas", "net://nas"), ("pc", "net://pc"));
        var shares = await List(fs, "net://nas");
        shares.Select(e => e.Name).Should().Equal("..", "media", "public");
        shares[0].FullPath.Should().Be("net://");
        shares[1].FullPath.Should().Be(@"\\nas\media");

        var inside = await List(fs, @"\\nas\media");
        inside.Select(e => e.Name).Should().Equal("..", "film.mkv");
        inside[0].FullPath.Should().Be("net://nas", "the top of a share goes up to the server's shares");
        fs.GetParent(@"\\nas\media").Should().Be("net://nas");
        fs.GetParent("net://nas").Should().Be("net://");
        fs.GetParent("net://").Should().BeNull();
        fs.DirectoryExists("net://anything").Should().BeTrue();
        fs.Watch("net://", () => { }).Should().BeNull();
    }

    [Fact]
    public async Task Wsl_distributions_are_the_shares_of_wsl_localhost()
    {
        var fs = new NetworkFileSystem(new FakeDisk(), new FakeBrowser(), () => ["Ubuntu", "docker-desktop"]);

        (await List(fs, "net://")).Select(e => e.Name).Should().Equal("nas", "pc", "wsl.localhost");
        var distros = await List(fs, "net://wsl.localhost");
        distros.Select(e => (e.Name, e.FullPath)).Should().Equal(("..", "net://"), ("Ubuntu", @"\\wsl.localhost\Ubuntu"), ("docker-desktop", @"\\wsl.localhost\docker-desktop"));
        (await List(fs, @"\\wsl.localhost\Ubuntu"))[0].FullPath.Should().Be("net://wsl.localhost", "the top of a distribution goes up to the list");

        var none = new NetworkFileSystem(new FakeDisk(), new FakeBrowser(), () => []);
        (await List(none, "net://")).Select(e => e.Name).Should().Equal(["nas", "pc"], "without WSL there is no such computer");

        WslDistros.IsServer("WSL$").Should().BeTrue();
        WslDistros.Root("Ubuntu").Should().Be(@"\\wsl.localhost\Ubuntu");
        WslDistros.DistroOf(@"\\wsl$\Ubuntu\home\c").Should().Be("Ubuntu");
        WslDistros.DistroOf(@"\\wsl.localhost\").Should().BeNull();
        WslDistros.DistroOf(@"\\nas\media").Should().BeNull();
    }

    [Fact]
    public async Task A_server_opened_by_path_joins_the_computer_list()
    {
        var fs = new NetworkFileSystem(new FakeDisk(), new FakeBrowser(), () => []);

        await List(fs, @"\\backup\media");

        (await List(fs, "net://")).Select(e => e.Name).Should().Contain("backup", "discovery may miss it, the session remembers it");
    }

    [Fact]
    public async Task Access_denied_asks_for_a_logon_once_and_tries_again()
    {
        var browser = new FakeBrowser { LockedServer = "nas", LogonAnswer = true };
        var disk = new FakeDisk { Locked = @"\\nas\media" };
        var fs = new NetworkFileSystem(disk, browser);

        (await List(fs, "net://nas")).Should().HaveCount(3);
        disk.Unlock = () => browser.LoggedOn.Contains(@"\\nas\media");
        (await List(fs, @"\\nas\media")).Should().HaveCount(2);

        browser.LoggedOn.Should().Equal(@"\\nas\IPC$", @"\\nas\media");
    }

    [Fact]
    public async Task A_cancelled_logon_reports_access_denied()
    {
        var fs = new NetworkFileSystem(new FakeDisk { Locked = @"\\nas\media" }, new FakeBrowser { LogonAnswer = false });

        var act = () => List(fs, @"\\nas\media\x");

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static async Task<List<FileEntry>> List(NetworkFileSystem fs, string path)
    {
        var entries = new List<FileEntry>();
        await foreach (var e in fs.ListAsync(path, includeHidden: false, TestContext.Current.CancellationToken))
        {
            entries.Add(e);
        }

        return entries;
    }

    private sealed class FakeBrowser : INetworkBrowser
    {
        public string? LockedServer { get; set; }

        public bool LogonAnswer { get; set; } = true;

        public List<string> LoggedOn { get; } = [];

        public IReadOnlyList<string> Computers(CancellationToken cancellationToken) => ["pc", "nas"];

        public IReadOnlyList<NetworkShare> Shares(string server, bool includeHidden)
        {
            if (string.Equals(server, LockedServer, StringComparison.OrdinalIgnoreCase) && !LoggedOn.Contains($@"\\{server}\IPC$"))
            {
                throw new UnauthorizedAccessException();
            }

            return [new NetworkShare("media", string.Empty), new NetworkShare("public", "for all")];
        }

        public bool Logon(string remoteName)
        {
            if (LogonAnswer)
            {
                LoggedOn.Add(remoteName);
            }

            return LogonAnswer;
        }
    }

    /// <summary>UNC folders as plain strings, so the test runs anywhere.</summary>
    private sealed class FakeDisk : IFileSystem
    {
        public string? Locked { get; set; }

        public Func<bool>? Unlock { get; set; }

        public async IAsyncEnumerable<FileEntry> ListAsync(string path, bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Locked is not null && path.StartsWith(Locked, StringComparison.OrdinalIgnoreCase) && Unlock?.Invoke() != true)
            {
                throw new UnauthorizedAccessException(path);
            }

            yield return new FileEntry { Name = "film.mkv", FullPath = path.TrimEnd('\\') + @"\film.mkv", IsDirectory = false, Size = 7 };
        }

        public IReadOnlyList<DriveEntry> GetDrives() => [];

        public DriveEntry? GetDrive(string root) => null;

        public bool DirectoryExists(string path) => true;

        public string? GetParent(string path) => null;

        public IDisposable? Watch(string path, Action onChanged) => null;
    }
}
