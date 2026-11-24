using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>A server that is a local folder; enough to drive the panels and commands without a network.</summary>
internal sealed class FolderServerClient(string root) : IRemoteClient
{
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<string> GetHomeAsync(CancellationToken cancellationToken) => Task.FromResult("/home/tester");

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var dir = new DirectoryInfo(Local(path));
        return dir.Exists
            ? Task.FromResult<IReadOnlyList<RemoteItem>>(dir.EnumerateFileSystemInfos().Select(Item).ToList())
            : throw new FileNotFoundException(null, path);
    }

    public Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        var local = Local(path);
        FileSystemInfo? info = Directory.Exists(local) ? new DirectoryInfo(local) : File.Exists(local) ? new FileInfo(local) : null;
        return Task.FromResult(info is null ? null : Item(info));
    }

    public async Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken cancellationToken) => (await StatAsync(path, cancellationToken))?.Modified;

    public async Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(Local(path));
        input.Seek(offset, SeekOrigin.Begin);
        await input.CopyToAsync(destination, cancellationToken);
    }

    public async Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(Local(path), append ? FileMode.Append : FileMode.Create, FileAccess.Write);
        await source.CopyToAsync(output, cancellationToken);
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        File.Delete(Local(path));
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Directory.Delete(Local(path));
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Local(path));
        return Task.CompletedTask;
    }

    public Task RenameAsync(string source, string target, CancellationToken cancellationToken)
    {
        if (Directory.Exists(Local(source)))
        {
            Directory.Move(Local(source), Local(target));
        }
        else
        {
            File.Move(Local(source), Local(target));
        }

        return Task.CompletedTask;
    }

    public Task SetModeAsync(string path, int mode, CancellationToken cancellationToken)
    {
        Modes[RemotePath.NormalizeInner(path)] = mode;
        return Task.CompletedTask;
    }

    /// <summary>chmod calls, by server path (the folder underneath has no Unix modes on Windows).</summary>
    public Dictionary<string, int> Modes { get; } = [];

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        File.SetLastWriteTimeUtc(Local(path), modified.UtcDateTime);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RemoteItem Item(FileSystemInfo info) => new(info.Name, info is DirectoryInfo, info is FileInfo f ? f.Length : 0, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

    private string Local(string path) => Path.Combine(root, RemotePath.NormalizeInner(path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
}

internal sealed class AnsweringRemotePrompts : IRemotePrompts
{
    public int Logins { get; private set; }

    public CredentialAnswer? Answer { get; set; } = new("secret", Remember: false);

    public Task<TrustChoice> ConfirmHostAsync(HostIdentity identity, string? known, CancellationToken cancellationToken) => Task.FromResult(TrustChoice.Once);

    public Task<CredentialAnswer?> AskCredentialsAsync(RemoteSite site, bool passphrase, string? problem, CancellationToken cancellationToken)
    {
        Logins++;
        return Task.FromResult(Answer);
    }
}

public sealed class RemotePanelTests : IDisposable
{
    private const string Server = "sftp://tester@srv:22";
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly AnsweringRemotePrompts _prompts = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly MainViewModel _vm;
    private readonly string _serverRoot;
    private readonly string _disk;
    private readonly List<FolderServerClient> _clients = [];

    public RemotePanelTests()
    {
        _serverRoot = _tree.Dir("server");
        Directory.CreateDirectory(Path.Combine(_serverRoot, "home", "tester", "docs"));
        File.WriteAllText(Path.Combine(_serverRoot, "home", "tester", "docs", "a.txt"), "server a");
        File.WriteAllText(Path.Combine(_serverRoot, "home", "tester", "readme.md"), "# hi");
        _disk = _tree.Dir("disk");
        File.WriteAllText(Path.Combine(_disk, "up.txt"), "from disk");
        _vm = new MainViewModel(new LocalFileSystem(), _launcher, new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), new FakeBin(),
            remote: new RemoteSetup(_prompts, new NoSecretStore(), (_, _, _) =>
            {
                var client = new FolderServerClient(_serverRoot);
                _clients.Add(client);
                return client;
            }));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task OpenServerOnLeftAsync()
    {
        await _vm.Right.NavigateAsync(_disk);
        await _vm.Left.NavigateAsync(_disk);
        _vm.ActivePanel.Should().BeSameAs(_vm.Left);
        (await _vm.ConnectAsync("sftp://tester@srv")).Should().BeTrue();
    }

    private static void Cursor(PanelViewModel panel, string name) => panel.CurrentIndex = panel.Items.ToList().FindIndex(i => i.Name == name);

    [Fact]
    public async Task An_address_without_a_path_opens_the_home_folder_after_one_login()
    {
        await OpenServerOnLeftAsync();

        _vm.Left.Path.Should().Be(Server + "/home/tester");
        _vm.Left.IsRemote.Should().BeTrue();
        _vm.Left.Items.Select(i => i.Name).Should().Equal("..", "docs", "readme.md");
        _vm.Left.DriveLabel.Should().Be("srv");
        _vm.Left.Breadcrumbs.Select(b => b.Name).Should().Equal("home", "tester");
        _prompts.Logins.Should().Be(1);

        _vm.Left.CurrentIndex = 1;
        await _vm.Left.OpenCurrentAsync();
        await _vm.Left.GoUpAsync();
        _vm.Left.CurrentItem!.Name.Should().Be("docs", "going up lands on the folder we came from");
        _prompts.Logins.Should().Be(1, "the login is reused");
    }

    [Fact]
    public async Task Cancelling_the_login_leaves_the_panel_where_it_was()
    {
        _prompts.Answer = null;
        await _vm.Left.NavigateAsync(_disk);

        (await _vm.ConnectAsync("sftp://tester@srv")).Should().BeFalse();

        _vm.Left.Path.Should().Be(_disk);
        _vm.Left.Status.Should().Be(Texts.LoginCancelled);
        _dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task F5_uploads_and_downloads_with_the_other_panel_as_target()
    {
        await OpenServerOnLeftAsync();

        // server → disk
        Cursor(_vm.Left, "readme.md");
        _dialogs.TransferAnswer = _disk + Path.DirectorySeparatorChar;
        await _vm.CopyAsync();
        File.ReadAllText(Path.Combine(_disk, "readme.md")).Should().Be("# hi");

        // disk → server
        _vm.SwitchPanel();
        Cursor(_vm.Right, "up.txt");
        _dialogs.TransferAnswer = _vm.Left.Path + Path.DirectorySeparatorChar;
        await _vm.CopyAsync();
        File.ReadAllText(Path.Combine(_serverRoot, "home", "tester", "up.txt")).Should().Be("from disk");
        _vm.Left.Items.Select(i => i.Name).Should().Contain("up.txt", "the server panel refreshes after the upload");
        _dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task F6_within_the_server_is_a_rename_and_F5_within_it_is_refused()
    {
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "readme.md");

        _dialogs.TransferAnswer = "docs/";
        await _vm.MoveAsync();

        File.Exists(Path.Combine(_serverRoot, "home", "tester", "docs", "readme.md")).Should().BeTrue();
        _vm.Left.Items.Select(i => i.Name).Should().NotContain("readme.md");

        Cursor(_vm.Left, "docs");
        _dialogs.TransferAnswer = "copy-of-docs";
        await _vm.CopyAsync();
        _dialogs.Errors.Should().ContainSingle().Which.Should().Be(Texts.RemoteCopyOnServer);
    }

    [Fact]
    public async Task F7_Shift_F6_and_F8_work_on_the_server()
    {
        await OpenServerOnLeftAsync();

        _dialogs.NameAnswer = "new/deeper";
        await _vm.MakeDirectoryAsync();
        Directory.Exists(Path.Combine(_serverRoot, "home", "tester", "new", "deeper")).Should().BeTrue();
        _vm.Left.CurrentItem!.Name.Should().Be("new");

        Cursor(_vm.Left, "readme.md");
        _dialogs.NameAnswer = "README.txt";
        await _vm.RenameCurrentAsync();
        File.Exists(Path.Combine(_serverRoot, "home", "tester", "README.txt")).Should().BeTrue();
        _vm.Left.CurrentItem!.Name.Should().Be("README.txt");

        Cursor(_vm.Left, "docs");
        _dialogs.NameAnswer = "README.txt";
        await _vm.RenameCurrentAsync();
        _dialogs.Errors.Should().ContainSingle("an existing name is not overwritten");

        Cursor(_vm.Left, "new");
        await _vm.DeleteAsync(permanent: false);
        _dialogs.LastDeletePermanent.Should().BeTrue("a server has no Recycle Bin");
        Directory.Exists(Path.Combine(_serverRoot, "home", "tester", "new")).Should().BeFalse();
    }

    [Fact]
    public async Task Commands_that_need_a_disk_explain_themselves()
    {
        await OpenServerOnLeftAsync();

        await _vm.PackAsync();
        await _vm.NewFileAsync();
        (await _vm.CreateMultiRenameAsync()).Should().BeNull();

        _dialogs.Errors.Should().HaveCount(3).And.AllSatisfy(e => e.Should().Be(Texts.RemoteNotHere));
    }

    [Fact]
    public async Task F4_edits_a_temp_copy_and_offers_to_upload_it_back()
    {
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "readme.md");
        _dialogs.UploadBackAnswer = true;

        await _vm.EditCurrentAsync();

        var temp = _launcher.Edited.Should().ContainSingle().Subject.Path;
        File.ReadAllText(temp).Should().Be("# hi");
        await File.WriteAllTextAsync(temp, "# edited", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddSeconds(5));

        // The upload may be writing the file at the very moment it is read here (a sharing violation on Windows): try again.
        var uploaded = Path.Combine(_serverRoot, "home", "tester", "readme.md");
        string? Content()
        {
            try
            {
                return File.ReadAllText(uploaded);
            }
            catch (IOException)
            {
                return null;
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Content() != "# edited" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        _dialogs.UploadBackQuestions.Should().Contain("readme.md");
        File.ReadAllText(Path.Combine(_serverRoot, "home", "tester", "readme.md")).Should().Be("# edited");
    }

    [Fact]
    public async Task Servers_are_not_reopened_at_startup_and_disconnect_returns_to_the_disk()
    {
        await OpenServerOnLeftAsync();

        _vm.Left.CaptureSettings().Path.Should().Be(_disk);

        await _vm.DisconnectAsync();

        _vm.Left.Path.Should().Be(_disk);
        _vm.Remote.IsOpen(Server).Should().BeFalse();
    }

    [Fact]
    public async Task Cd_with_an_address_and_relative_cd_on_the_server()
    {
        await _vm.Left.NavigateAsync(_disk);
        _vm.CommandLine.Text = "cd sftp://tester@srv/home";
        await _vm.RunCommandLineAsync();
        _vm.Left.Path.Should().Be(Server + "/home");

        _vm.CommandLine.Text = "cd tester/docs";
        await _vm.RunCommandLineAsync();
        _vm.Left.Path.Should().Be(Server + "/home/tester/docs");
    }

    [Fact]
    public void The_connection_manager_edits_saved_sites()
    {
        var secrets = new Dictionary<string, string>();
        var store = new DictionarySecrets(secrets);
        var sites = new List<RemoteSite>();
        var vm = new ConnectionsViewModel(sites, _vm.Remote, store);

        var row = vm.Add();
        row.Host = "example.org";
        row.User = "jan";
        row.ProtocolIndex = 2;
        row.Port = "990";
        vm.Duplicate();

        sites.Should().HaveCount(2);
        sites[0].Root.Should().Be("ftps://jan@example.org:990");
        vm.Address.Should().Be("ftps://jan@example.org:990");
        vm.QuickAddress = "sftp://x@y";
        vm.Address.Should().Be("sftp://x@y");

        vm.Add();
        vm.RemoveEmpty();
        sites.Should().HaveCount(2, "a site without a host is dropped");

        vm.Selected = vm.Sites[0];
        vm.Delete();
        sites.Should().ContainSingle();
    }

    [Fact]
    public void An_S3_connection_is_set_up_by_service_region_keys_and_bucket()
    {
        var secrets = new Dictionary<string, string>();
        var sites = new List<RemoteSite>();
        var vm = new ConnectionsViewModel(sites, _vm.Remote, new DictionarySecrets(secrets));

        var row = vm.Add();
        row.ProtocolIndex = 3;
        row.IsAws.Should().BeTrue("a new S3 connection starts as Amazon S3");
        row.Host.Should().Be("s3.amazonaws.com");

        row.Region = "eu-central-1";
        row.Host.Should().Be("s3.eu-central-1.amazonaws.com", "at Amazon the region makes the address");
        row.User = "AKIAEXAMPLE";
        row.Bucket = "/kopie/2026/";
        row.Bucket.Should().Be("kopie");
        row.BucketFolder.Should().Be("2026", "a pasted bucket/folder fills both fields");
        row.BucketFolder = "2026/listopad";
        sites[0].StartPath.Should().Be("/kopie/2026/listopad");
        vm.Address.Should().Be("s3://AKIAEXAMPLE@s3.eu-central-1.amazonaws.com:443/kopie/2026/listopad");
        row.Subtitle.Should().Be("Amazon S3 · kopie", "the list does not show the access key");

        row.Bucket = string.Empty;
        sites[0].StartPath.Should().BeEmpty("without a bucket the buckets are listed");
        row.HasBucket.Should().BeFalse();
    }

    [Fact]
    public void An_S3_server_of_ones_own_takes_a_pasted_address_apart()
    {
        var sites = new List<RemoteSite>();
        var vm = new ConnectionsViewModel(sites, _vm.Remote, new DictionarySecrets([]));
        var row = vm.Add();
        row.ProtocolIndex = 3;
        row.Region = "eu-west-1";

        row.ProviderIndex = S3Endpoints.ListIndex(S3Provider.Other);
        row.IsOwnServer.Should().BeTrue();
        row.Host.Should().BeEmpty("the Amazon address is not this server's");
        row.Endpoint = "http://192.168.1.10:9000/";
        (sites[0].Host, sites[0].Port, sites[0].PlainHttp).Should().Be(("192.168.1.10", 9000, true));
        row.IsUnencrypted.Should().BeTrue();

        row.ProviderIndex = S3Endpoints.ListIndex(S3Provider.CloudflareR2);
        (sites[0].Host, sites[0].Port, sites[0].PlainHttp, sites[0].Region).Should().Be((string.Empty, 0, false, string.Empty), "what the hidden fields held does not apply to a known service");
        row.Endpoint = "https://0123abcd.r2.cloudflarestorage.com";
        sites[0].Host.Should().Be("0123abcd.r2.cloudflarestorage.com");
        S3RemoteClient.RegionOf(sites[0]).Should().Be("auto");

        row.ProviderIndex = S3Endpoints.ListIndex(S3Provider.Aws);
        row.Host.Should().Be("s3.amazonaws.com");

        // a site saved by an earlier version: the service is read from its address, the region stays in it
        var old = new RemoteSite { Protocol = RemoteProtocol.S3, Host = "s3.eu-north-1.amazonaws.com", User = "AKIA" };
        var oldRow = new ConnectionsViewModel([old], _vm.Remote, new DictionarySecrets([])).Sites[0];
        oldRow.IsAws.Should().BeTrue();
        oldRow.Region.Should().Be("eu-north-1");
        oldRow.ProviderIndex = S3Endpoints.ListIndex(S3Provider.Aws);
        old.Host.Should().Be("s3.eu-north-1.amazonaws.com", "choosing the same service keeps the address");
        oldRow.Region = "eu-west-3";
        old.Host.Should().Be("s3.eu-west-3.amazonaws.com");
    }

    [Fact]
    public void The_S3_secret_key_typed_in_the_form_is_kept_and_follows_the_address()
    {
        var secrets = new Dictionary<string, string>();
        var sites = new List<RemoteSite>();
        var vm = new ConnectionsViewModel(sites, _vm.Remote, new DictionarySecrets(secrets));
        var row = vm.Add();
        row.ProtocolIndex = 3;
        row.ProviderIndex = S3Endpoints.ListIndex(S3Provider.Other);

        vm.SaveSecret(row, "tajny").Should().BeFalse("there is no address to keep it under yet");
        row.Endpoint = "minio.example.com";
        row.User = "klucz";
        vm.SaveSecret(row, "tajny").Should().BeTrue();
        row.HasPassword.Should().BeTrue();
        secrets.Should().ContainSingle().Which.Key.Should().Be(RemoteConnections.SecretKey("s3://klucz@minio.example.com:443"));

        row.Port = "9000";
        secrets.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>(RemoteConnections.SecretKey("s3://klucz@minio.example.com:9000"), "tajny"));
        row.HasPassword.Should().BeTrue("the secret key followed the new address");

        row.User = "inny";
        secrets.Should().BeEmpty("another access key has another secret");
        row.HasPassword.Should().BeFalse();

        // an FTP password never moves to another server
        var ftp = vm.Add();
        ftp.ProtocolIndex = 1;
        ftp.Host = "a.example.org";
        ftp.User = "jan";
        secrets[RemoteConnections.SecretKey(ftp.Site.Root)] = "haslo";
        ftp.Host = "b.example.org";
        secrets.Keys.Should().ContainSingle().Which.Should().Be(RemoteConnections.SecretKey("ftp://jan@a.example.org:21"));
        ftp.ProtocolIndex = 3;
        secrets.Keys.Should().ContainSingle().Which.Should().Be(RemoteConnections.SecretKey("ftp://jan@a.example.org:21"));
    }

    [Fact]
    public async Task Shift_F2_compares_a_disk_folder_with_the_server_and_synchronizes_both_ways()
    {
        var t0 = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var serverHome = Path.Combine(_serverRoot, "home", "tester");
        File.SetLastWriteTimeUtc(Path.Combine(serverHome, "readme.md"), t0);
        File.WriteAllText(Path.Combine(_disk, "readme.md"), "# hi, edited");
        File.SetLastWriteTimeUtc(Path.Combine(_disk, "readme.md"), t0.AddDays(1));
        await _vm.Right.NavigateAsync(_disk);
        (await _vm.ConnectAsync("sftp://tester@srv")).Should().BeTrue(); // left: the server home
        _dialogs.SyncAnswer = true;

        var sync = (await _vm.CompareDirectoriesAsync())!;

        _vm.Left.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["docs"]);
        _vm.Right.MarkedItems.Select(i => i.Name).Should().BeEquivalentTo(["readme.md", "up.txt"]);
        var readme = sync.Rows.Single(r => r.Relative == "readme.md");
        readme.Action.Should().Be(SyncAction.CopyToLeft);
        readme.LeftPath.Should().Be(Server + "/home/tester/readme.md");
        readme.LeftInfo.Should().StartWith("4", "the server's size comes from the comparison, not from the disk");
        readme.CanDiff.Should().BeTrue();

        var diff = _vm.CreateDiff(readme);
        await diff.LoadAsync(TestContext.Current.CancellationToken);
        diff.Error.Should().BeNull();
        diff.Rows.Should().NotBeEmpty();

        await _vm.RunSyncAsync(sync);

        File.ReadAllText(Path.Combine(serverHome, "readme.md")).Should().Be("# hi, edited");
        File.ReadAllText(Path.Combine(serverHome, "up.txt")).Should().Be("from disk");
        File.ReadAllText(Path.Combine(_disk, "docs", "a.txt")).Should().Be("server a");
        _vm.Left.Items.Select(i => i.Name).Should().Contain("up.txt", "the server panel refreshes");
    }

    [Fact]
    public async Task Permissions_change_on_the_server_starting_from_the_current_mode()
    {
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "readme.md");
        _dialogs.PermissionsAnswer = new PermissionsAnswer(0x180, Recursive: false);

        await _vm.ChangePermissionsAsync();

        _dialogs.LastPermissionsInitial.Should().Be(0x1A4, "no mode from this server: files start at 644");
        _clients.SelectMany(c => c.Modes).Should().Contain(new KeyValuePair<string, int>("/home/tester/readme.md", 0x180));
    }

    [Fact]
    public async Task On_disk_the_same_command_asks_for_attributes_instead_of_chmod()
    {
        await _vm.Left.NavigateAsync(_disk);
        Cursor(_vm.Left, "up.txt");

        await _vm.ChangePermissionsAsync();

        _dialogs.LastPermissionsInitial.Should().BeNull();
        _dialogs.LastAttributesInitial.Should().NotBeNull();
    }

    private sealed class DictionarySecrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? Read(string key) => values.GetValueOrDefault(key);

        public void Write(string key, string secret) => values[key] = secret;

        public void Delete(string key) => values.Remove(key);
    }
}
