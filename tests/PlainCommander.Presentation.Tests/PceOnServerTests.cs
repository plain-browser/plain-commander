using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>
/// Encrypted containers on an S3 server through the commands of the panels: F5 of a .pce file asks how to keep it
/// there, a container is entered like a folder, comes back as a .pce file, is made by Encrypt and gets a new
/// password. Opt-in, on a real server: PC_PCE_S3 = "http://host:port" (keys "tester" / "secretsecret").
/// </summary>
public sealed class PceOnServerTests : IAsyncDisposable
{
    private const string Password = "tajne";
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("PC_PCE_S3");
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly List<string> _remembered = [];
    private MainViewModel? _vm;
    private string _disk = string.Empty;
    private string _server = string.Empty;

    public async ValueTask DisposeAsync()
    {
        if (_vm is not null)
        {
            try
            {
                var location = RemotePath.Parse(_server)!;
                await _vm.Remote.UseAsync(location.Root, c => Wipe(c, location.Inner), CancellationToken.None);
            }
            catch (IOException)
            {
            }

            _vm.Dispose();
        }

        foreach (var path in _remembered)
        {
            ArchivePasswords.Forget(path);
            PceKnownGenerations.Forget(path);
        }

        _tree.Dispose();
    }

    /// <summary>Removes a folder of the test with everything in it, containers included (their content first, as F8 does).</summary>
    private static async Task<bool> Wipe(IRemoteClient client, string inner)
    {
        foreach (var item in await client.ListAsync(inner, CancellationToken.None))
        {
            var child = inner + "/" + item.Name;
            if (item.IsDirectory)
            {
                await Wipe(client, child);
            }
            else
            {
                await client.DeleteFileAsync(child, CancellationToken.None);
            }
        }

        await client.DeleteDirectoryAsync(inner, CancellationToken.None);
        return true;
    }

    private void Remember(string path, string password)
    {
        ArchivePasswords.Remember(path, password);
        _remembered.Add(path);
    }

    /// <summary>The disk on the right, a fresh folder of the test bucket on the left; the left panel is active.</summary>
    private async Task<MainViewModel> StartAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Endpoint), "Set PC_PCE_S3 to the endpoint of the S3 test server.");
        var endpoint = new Uri(Endpoint!);
        var bucket = RemotePath.Parse($"s3://tester@{endpoint.Host}:{endpoint.Port}/pc-test")!;
        var secrets = new Secrets();
        secrets.Write(RemoteConnections.SecretKey(bucket.Root), "secretsecret");
        _vm = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), new FakeBin(),
            remote: new RemoteSetup(new AnsweringRemotePrompts(), secrets));
        _vm.Settings.RemoteSites.Add(new RemoteSite { Protocol = RemoteProtocol.S3, Host = bucket.Host, Port = bucket.Port, User = bucket.User, PlainHttp = true });

        _disk = _tree.Dir("disk");
        _server = bucket.Child("pc-vm-" + Guid.NewGuid().ToString("N")[..8]).FullPath;
        await _vm.Remote.UseAsync(bucket.Root, async c =>
        {
            if (await c.StatAsync(bucket.Inner, CancellationToken.None) is null)
            {
                await c.CreateDirectoryAsync(bucket.Inner, CancellationToken.None);
            }

            await c.CreateDirectoryAsync(RemotePath.Parse(_server)!.Inner, CancellationToken.None);
            return true;
        }, CancellationToken.None);
        (await _vm.Right.NavigateAsync(_disk)).Should().BeTrue();
        (await _vm.Left.NavigateAsync(_server)).Should().BeTrue();
        _vm.ActivePanel.Should().BeSameAs(_vm.Left);
        return _vm;
    }

    private static void Cursor(PanelViewModel panel, string name)
    {
        panel.CurrentIndex = panel.Items.ToList().FindIndex(i => i.Name == name);
        panel.CurrentItem!.Name.Should().Be(name);
    }

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    /// <summary>A .pce file on the disk holding the folder "skany" with two files.</summary>
    private string MakePce(string name = "box.pce")
    {
        var source = Path.Combine(_tree.Root, "skany");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.bin"), Bytes(40_000));
        File.WriteAllText(Path.Combine(source, "umowa.txt"), "tajna treść");
        var file = Path.Combine(_disk, name);
        PceFile.Encrypt(source, file, Password, PceOptions.Fast, PceCompression.Auto, null, CancellationToken.None);
        Remember(file, Password);
        return file;
    }

    [Fact]
    public async Task F5_of_a_pce_file_asks_and_keeps_it_as_a_container_that_is_entered_and_fetched_back_as_a_file()
    {
        var vm = await StartAsync();
        var file = MakePce();
        await vm.Right.RefreshAsync();

        vm.SwitchPanel();
        Cursor(vm.Right, "box.pce");
        _dialogs.TransferAnswer = _server + "/";
        _dialogs.PceUploadAnswer = PceUploadChoice.Container;
        await vm.CopyAsync();
        _dialogs.Errors.Should().BeEmpty();
        _dialogs.PceUploadQuestions.Should().Equal((1, "box.pce"));
        _remembered.Add(_server + "/box.pce");

        await vm.Left.RefreshAsync();
        var onServer = vm.Left.Items.Single(i => i.Name == "box.pce");
        onServer.Entry.IsDirectory.Should().BeTrue("the container is a folder of objects");
        ArchivePasswords.Remembered(_server + "/box.pce").Should().Be(Password, "the copy opens with the password of the file");

        // Inside: the decrypted names, as in any folder.
        (await vm.Left.NavigateAsync(_server + "/box.pce/skany")).Should().BeTrue();
        vm.Left.Items.Select(i => i.Name).Should().BeEquivalentTo("..", "a.bin", "umowa.txt");
        vm.Left.Items.Single(i => i.Name == "a.bin").Entry.Size.Should().Be(40_000);

        // A file copied into the container: no question, it is just a file going into a folder.
        File.WriteAllText(Path.Combine(_disk, "nowy.txt"), "dopisany");
        File.WriteAllBytes(Path.Combine(_disk, "inny.pce"), [1, 2, 3]);
        await vm.Right.RefreshAsync();
        Cursor(vm.Right, "nowy.txt");
        _dialogs.TransferAnswer = _server + "/box.pce/skany/";
        await vm.CopyAsync();
        Cursor(vm.Right, "inny.pce");
        await vm.CopyAsync();
        _dialogs.PceUploadQuestions.Should().HaveCount(1, "inside a container a .pce file is an ordinary file");
        await vm.Left.RefreshAsync();
        vm.Left.Items.Select(i => i.Name).Should().BeEquivalentTo("..", "a.bin", "umowa.txt", "nowy.txt", "inny.pce");

        // One file out of it.
        vm.SwitchPanel();
        Cursor(vm.Left, "umowa.txt");
        _dialogs.TransferAnswer = _disk + Path.DirectorySeparatorChar;
        await vm.CopyAsync();
        File.ReadAllText(Path.Combine(_disk, "umowa.txt")).Should().Be("tajna treść");

        // The container itself comes back as a .pce file, not as a folder of plain files.
        (await vm.Left.NavigateAsync(_server)).Should().BeTrue();
        Cursor(vm.Left, "box.pce");
        var back = _tree.Dir("back");
        _dialogs.TransferAnswer = back + Path.DirectorySeparatorChar;
        await vm.CopyAsync();
        _dialogs.Errors.Should().BeEmpty();
        var fetched = Path.Combine(back, "box.pce");
        _remembered.Add(fetched);
        File.Exists(fetched).Should().BeTrue();
        Directory.Exists(fetched).Should().BeFalse();
        using var container = PceContainer.Open(fetched, Password);
        container.Entries.Select(e => e.Path).Should().BeEquivalentTo("skany/a.bin", "skany/umowa.txt", "skany/nowy.txt", "skany/inny.pce");
        using var stream = container.OpenRead(container.Find("skany/nowy.txt")!);
        new StreamReader(stream).ReadToEnd().Should().Be("dopisany");
        File.Exists(file).Should().BeTrue("F5 leaves the original");
    }

    [Fact]
    public async Task F5_of_a_pce_file_as_a_plain_copy_stays_one_file_and_cancelling_copies_nothing()
    {
        var vm = await StartAsync();
        MakePce();
        File.WriteAllText(Path.Combine(_disk, "zwykły.txt"), "x");
        await vm.Right.RefreshAsync();
        vm.SwitchPanel();
        Cursor(vm.Right, "box.pce");
        _dialogs.TransferAnswer = _server + "/";

        _dialogs.PceUploadAnswer = null;
        await vm.CopyAsync();
        await vm.Left.RefreshAsync();
        vm.Left.Items.Select(i => i.Name).Should().Equal("..");

        _dialogs.PceUploadAnswer = PceUploadChoice.PlainCopy;
        await vm.CopyAsync();
        await vm.Left.RefreshAsync();
        var copy = vm.Left.Items.Single(i => i.Name == "box.pce");
        copy.Entry.IsDirectory.Should().BeFalse();
        copy.Entry.Size.Should().Be(new FileInfo(Path.Combine(_disk, "box.pce")).Length);

        // A file that is not .pce is never asked about.
        Cursor(vm.Right, "zwykły.txt");
        await vm.CopyAsync();
        _dialogs.PceUploadQuestions.Should().HaveCount(2);
        _dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Encrypt_with_the_server_as_the_target_makes_a_container_there_and_its_password_can_be_changed()
    {
        var vm = await StartAsync();
        var folder = Path.Combine(_disk, "dokumenty");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllBytes(Path.Combine(folder, "sub", "b.bin"), Bytes(70_000, 2));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "jawne przed zaszyfrowaniem");
        await vm.Right.RefreshAsync();
        vm.SwitchPanel();
        Cursor(vm.Right, "dokumenty");

        _dialogs.EncryptAnswer = new EncryptAnswer(PceFormat.Extension, Password, HideName: true, Folder: _server);
        await vm.EncryptAsync();
        _dialogs.Errors.Should().Equal(Texts.EncryptOnServer);
        _dialogs.Errors.Clear();

        _dialogs.EncryptAnswer = new EncryptAnswer(PceFormat.Extension, Password, Folder: _server);
        await vm.EncryptAsync();
        _dialogs.Errors.Should().BeEmpty();
        var box = _server + "/dokumenty.pce";
        _remembered.Add(box);
        _dialogs.DeleteOriginalsQuestions.Should().Be(1);
        Directory.Exists(folder).Should().BeTrue("the answer was no");

        (await vm.Left.NavigateAsync(box + "/dokumenty")).Should().BeTrue();
        vm.Left.Items.Select(i => i.Name).Should().BeEquivalentTo("..", "a.txt", "sub");

        // Files → Change password, with the cursor on the container in the server's folder.
        (await vm.Left.NavigateAsync(_server)).Should().BeTrue();
        vm.SwitchPanel();
        Cursor(vm.Left, "dokumenty.pce");
        _dialogs.ChangePasswordAnswers.Enqueue(new ChangePasswordAnswer("złe", "nowe"));
        _dialogs.ChangePasswordAnswers.Enqueue(new ChangePasswordAnswer(Password, "nowe"));
        await vm.ChangePcePasswordAsync();
        _dialogs.ChangePasswordQuestions.Should().Equal((false, false), (true, false));
        _dialogs.Infos.Should().ContainSingle();
        _dialogs.Errors.Should().BeEmpty();
        ArchivePasswords.Remembered(box).Should().Be("nowe");
        (await vm.Left.NavigateAsync(box + "/dokumenty/sub")).Should().BeTrue();
        vm.Left.Items.Select(i => i.Name).Should().BeEquivalentTo("..", "b.bin");

        // The old password no longer opens it (and nobody is there to be asked for another).
        // The browsing connection keeps the container open; a new connection has to open it again.
        ArchivePasswords.Remember(box, Password);
        var location = RemotePath.Parse(box)!;
        var fresh = await vm.Remote.OpenAsync(location.Root, CancellationToken.None);
        await using (fresh)
        {
            var open = async () => await fresh.ListAsync(location.Inner, CancellationToken.None);
            (await open.Should().ThrowAsync<ArchivePasswordException>()).Which.Wrong.Should().BeTrue();
        }

        ArchivePasswords.Remember(box, "nowe");

        // The change numbers seen went to the settings.
        vm.Settings.PceGenerations.Should().ContainKey(box);
    }

    private sealed class Secrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = [];

        public string? Read(string key) => _values.GetValueOrDefault(key);

        public void Write(string key, string secret) => _values[key] = secret;

        public void Delete(string key) => _values.Remove(key);
    }
}
