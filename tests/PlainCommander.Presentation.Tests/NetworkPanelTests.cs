using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Network;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>Computers and shares in a panel ("net://"), and disk images attached as drives.</summary>
public sealed class NetworkPanelTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeDisk _disk = new();
    private readonly FakeImages _images;
    private readonly MainViewModel _vm;

    public NetworkPanelTests()
    {
        _images = new FakeImages(_disk);
        var fs = new NetworkFileSystem(_disk, new FakeBrowser());
        _vm = new MainViewModel(fs, new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s.json")), _dialogs, new NoPrompts(), new FakeBin(), disks: _images)
        {
            AttachWait = TimeSpan.FromSeconds(2),
        };
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private static string[] Names(PanelViewModel p) => p.Items.Select(i => i.Name).ToArray();

    [Fact]
    public async Task Enter_walks_from_computers_to_shares_to_files_and_backspace_walks_back()
    {
        var panel = _vm.Left;
        await panel.NavigateAsync(NetworkPath.Root);
        Names(panel).Should().Equal("nas");
        panel.IsNetworkList.Should().BeTrue();
        panel.PathSegments[0].Name.Should().Be(Texts.Network);

        await panel.OpenCurrentAsync();
        panel.Path.Should().Be("net://nas");
        Names(panel).Should().Equal("..", "media");

        panel.CurrentIndex = 1;
        await panel.OpenCurrentAsync();
        panel.Path.Should().Be(@"\\nas\media");
        panel.IsNetworkList.Should().BeFalse();
        Names(panel).Should().Equal("..", "film.mkv");

        await panel.GoUpAsync();
        panel.Path.Should().Be("net://nas");
        panel.CurrentItem!.Name.Should().Be("media", "the cursor returns to the share we came from");
        await panel.GoUpAsync();
        panel.Path.Should().Be("net://");
    }

    [Fact]
    public async Task A_typed_server_path_opens_its_share_list()
    {
        (await _vm.Left.NavigateAsync(@"\\nas")).Should().BeTrue();

        _vm.Left.Path.Should().Be("net://nas");
    }

    [Fact]
    public async Task Nothing_is_written_into_the_computer_or_share_list()
    {
        await _vm.Left.NavigateAsync("net://nas");
        _vm.Left.CurrentIndex = 1;

        _vm.Left.OperationSources.Should().BeEmpty("a share in the list is not a file to copy or delete");
        _dialogs.NameAnswer = "new";
        await _vm.MakeDirectoryAsync();
        await _vm.RenameCurrentAsync();

        _dialogs.Errors.Should().HaveCount(2).And.AllBe(Texts.NetworkListNotHere);
        _dialogs.LastNameInitial.Should().BeNull("no name was asked for");
    }

    [Fact]
    public async Task An_attached_image_opens_as_its_new_drive()
    {
        var iso = _tree.File("disk.iso", 10);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "disk.iso");

        await _vm.AttachImageAsync();

        _images.Attached.Should().Equal(iso);
        _vm.Left.Path.Should().Be(_disk.NewRoot);
        _vm.Left.Status.Should().Contain("disk.iso");
    }

    [Fact]
    public async Task Attach_needs_an_image_under_the_cursor()
    {
        _tree.File("notes.txt", 1);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "notes.txt");

        await _vm.AttachImageAsync();

        _images.Attached.Should().BeEmpty();
        _dialogs.Errors.Should().ContainSingle().Which.Should().Be(Texts.NotADiskImage);
    }

    private sealed class FakeBrowser : INetworkBrowser
    {
        public IReadOnlyList<string> Computers(CancellationToken cancellationToken) => ["nas"];

        public IReadOnlyList<NetworkShare> Shares(string server, bool includeHidden) => [new NetworkShare("media", string.Empty)];

        public bool Logon(string remoteName) => false;
    }

    /// <summary>The real disk for the temp tree, a fake share, and a drive that appears when an image is attached.</summary>
    private sealed class FakeDisk : IFileSystem
    {
        private readonly LocalFileSystem _local = new();

        public string NewRoot { get; } = Path.Combine(Path.GetTempPath(), "pc-fake-drive-" + Guid.NewGuid().ToString("N")[..6]);

        public bool ImageAttached { get; set; }

        public async IAsyncEnumerable<FileEntry> ListAsync(string path, bool includeHidden, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                await Task.Yield();
                yield return new FileEntry { Name = "film.mkv", FullPath = path + @"\film.mkv", IsDirectory = false };
                yield break;
            }

            await foreach (var e in _local.ListAsync(path, includeHidden, cancellationToken))
            {
                yield return e;
            }
        }

        public IReadOnlyList<DriveEntry> GetDrives() =>
            ImageAttached ? [.. _local.GetDrives(), new DriveEntry(NewRoot, "CD", DriveType.CDRom, 10, 0, true)] : _local.GetDrives();

        public DriveEntry? GetDrive(string root) => _local.GetDrive(root);

        public bool DirectoryExists(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || _local.DirectoryExists(path);

        public string? GetParent(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) ? null : _local.GetParent(path);

        public IDisposable? Watch(string path, Action onChanged) => null;
    }

    private sealed class FakeImages(FakeDisk disk) : IDiskImages
    {
        public List<string> Attached { get; } = [];

        public void Attach(string imageFile)
        {
            Attached.Add(imageFile);
            Directory.CreateDirectory(disk.NewRoot);
            disk.ImageAttached = true;
        }

        public void Detach(string driveRoot) => disk.ImageAttached = false;
    }
}
