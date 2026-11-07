using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests.Weird;

/// <summary>A server whose rename replaces an existing target, as FTP (RNTO) and S3 (copy + delete) do.</summary>
internal sealed class OverwritingServerClient(string root) : IRemoteClient
{
    private readonly FolderServerClient _inner = new(root);

    public bool IsConnected => _inner.IsConnected;

    public Task ConnectAsync(CancellationToken cancellationToken) => _inner.ConnectAsync(cancellationToken);

    public Task<string> GetHomeAsync(CancellationToken cancellationToken) => Task.FromResult("/");

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) => _inner.ListAsync(path, cancellationToken);

    public Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken) => _inner.StatAsync(path, cancellationToken);

    public Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken cancellationToken) => _inner.GetModifiedAsync(path, cancellationToken);

    public Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken) => _inner.DownloadAsync(path, destination, offset, cancellationToken);

    public Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken) => _inner.UploadAsync(source, path, append, cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => _inner.DeleteFileAsync(path, cancellationToken);

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.DeleteDirectoryAsync(path, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => _inner.CreateDirectoryAsync(path, cancellationToken);

    public Task RenameAsync(string source, string target, CancellationToken cancellationToken)
    {
        File.Move(Local(source), Local(target), overwrite: true);
        return Task.CompletedTask;
    }

    public Task SetModeAsync(string path, int mode, CancellationToken cancellationToken) => _inner.SetModeAsync(path, mode, cancellationToken);

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) => _inner.SetModifiedAsync(path, modified, cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private string Local(string path) => Path.Combine(root, RemotePath.NormalizeInner(path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>One panel in an archive, search results or on a server, the other on disk: commands must pick the right side.</summary>
public sealed class MixedPanelsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeBin _bin = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;
    private readonly string _server;
    private readonly string _zip;

    public MixedPanelsTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _server = _tree.Dir("server");
        _zip = Path.Combine(_tree.Root, "pack.zip");
        using (var archive = ZipFile.Open(_zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "docs/readme.txt", "top.txt" })
            {
                using var w = new StreamWriter(archive.CreateEntry(name).Open());
                w.Write(name);
            }
        }

        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(),
            new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), _bin,
            remote: new RemoteSetup(new AnsweringRemotePrompts(), new NoSecretStore(), (_, _, _) => new OverwritingServerClient(_server)));
    }

    public void Dispose()
    {
        _vm.Dispose();
        ArchiveCatalog.Forget(_zip);
        _tree.Dispose();
    }

    private static void Cursor(PanelViewModel panel, string name)
    {
        var index = panel.Items.ToList().FindIndex(i => i.Name == name);
        index.Should().BeGreaterThanOrEqualTo(0, $"{name} should be listed");
        panel.CurrentIndex = index;
    }

    private static string[] Names(PanelViewModel panel) => panel.Items.Select(i => i.Name).ToArray();

    private IEnumerable<string> ZipNames(string inner) => ArchiveCatalog.Open(_zip).List(inner).Select(e => e.Name);

    // ---- archive on one side ----

    [Fact]
    public async Task F6_out_of_an_archive_is_refused_and_the_entry_stays()
    {
        await _vm.Left.NavigateAsync(_zip);
        await _vm.Right.NavigateAsync(_right);
        Cursor(_vm.Left, "top.txt");
        _dialogs.TransferAnswer = _right + Path.DirectorySeparatorChar;

        await _vm.MoveAsync();

        _dialogs.Errors.Should().ContainSingle();
        ZipNames(string.Empty).Should().Contain("top.txt");
        File.Exists(Path.Combine(_right, "top.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task F5_from_an_archive_into_the_same_archive_is_refused()
    {
        await _vm.Left.NavigateAsync(_zip);
        await _vm.Right.NavigateAsync(ArchivePath.Join(_zip, "docs"));
        Cursor(_vm.Left, "top.txt");
        _dialogs.TransferAnswer = ArchivePath.Join(_zip, "docs") + Path.DirectorySeparatorChar;

        await _vm.CopyAsync();

        _dialogs.Errors.Should().ContainSingle();
        ZipNames("docs").Should().Equal("readme.txt");
    }

    [Fact]
    public async Task Commands_that_need_a_disk_explain_themselves_inside_an_archive()
    {
        await _vm.Left.NavigateAsync(_zip);
        await _vm.Right.NavigateAsync(_right);
        Cursor(_vm.Left, "top.txt");

        await _vm.EncryptAsync();
        await _vm.PackAsync();
        (await _vm.CreateMultiRenameAsync()).Should().BeNull();
        await _vm.NewFileAsync();
        (await _vm.CompareDirectoriesAsync()).Should().BeNull();
        (await _vm.Left.ToggleFlatAsync()).Should().BeFalse();

        _dialogs.Errors.Should().HaveCount(5);
        ZipNames(string.Empty).Should().BeEquivalentTo(["docs", "top.txt"]);
    }

    [Fact]
    public async Task Pack_with_the_other_panel_in_an_archive_offers_the_source_folder()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "a");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_zip);
        Cursor(_vm.Left, "a.txt");

        await _vm.PackAsync();

        _dialogs.LastDefaultTarget.Should().Be(Path.Combine(_left, "a.zip"));
    }

    [Fact]
    public async Task Encrypt_with_the_other_panel_in_an_archive_or_on_a_network_list_offers_the_source_folder()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "a");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_zip);
        Cursor(_vm.Left, "a.txt");

        await _vm.EncryptAsync();

        _dialogs.LastEncryptFolder.Should().Be(_left);
    }

    // ---- search results on one side ----

    private void ShowResults(PanelViewModel panel, string root, params string[] relative)
    {
        _vm.Activate(panel);
        _vm.ShowSearchResults("*", root, relative.Select(r => LocalFileSystem.ToEntry(new FileInfo(Path.Combine(root, r)))));
    }

    [Fact]
    public async Task F5_into_a_panel_showing_results_targets_the_searched_folder()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "a");
        _tree.File("right/sub/found.txt");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        ShowResults(_vm.Right, _right, Path.Combine("sub", "found.txt"));
        _vm.Activate(_vm.Left);
        Cursor(_vm.Left, "a.txt");
        _dialogs.TransferAnswer = null;

        await _vm.CopyAsync();

        _dialogs.LastDefaultTarget.Should().Be(_right + Path.DirectorySeparatorChar);
    }

    [Fact]
    public async Task F6_from_results_moves_the_found_file_and_the_results_forget_it()
    {
        _tree.File("left/sub/found.txt", 4);
        _tree.File("left/sub/other.txt", 4);
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        ShowResults(_vm.Left, _left, Path.Combine("sub", "found.txt"), Path.Combine("sub", "other.txt"));
        Cursor(_vm.Left, Path.Combine("sub", "found.txt"));
        _dialogs.TransferAnswer = _right + Path.DirectorySeparatorChar;

        await _vm.MoveAsync();

        File.Exists(Path.Combine(_right, "found.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_left, "sub", "found.txt")).Should().BeFalse();
        _vm.Left.IsShowingResults.Should().BeTrue();
        Names(_vm.Left).Should().Equal("..", Path.Combine("sub", "other.txt"));
    }

    [Fact]
    public async Task Rename_in_results_then_delete_acts_on_the_renamed_file()
    {
        _tree.File("left/sub/found.txt", 4);
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        ShowResults(_vm.Left, _left, Path.Combine("sub", "found.txt"));
        Cursor(_vm.Left, Path.Combine("sub", "found.txt"));
        _dialogs.LastNameInitial.Should().BeNull();
        _dialogs.NameAnswer = "renamed.txt";

        await _vm.RenameCurrentAsync();
        _dialogs.LastNameInitial.Should().Be("found.txt");
        _vm.Left.CurrentItem!.Name.Should().Be(Path.Combine("sub", "renamed.txt"));
        await _vm.DeleteAsync(permanent: false);

        _bin.Recycled.Should().Equal(Path.Combine(_left, "sub", "renamed.txt"));
        Names(_vm.Left).Should().Equal("..");
    }

    [Fact]
    public async Task Filter_and_marks_in_results_then_closing_them_leaves_no_hidden_marks()
    {
        _tree.File("left/a.txt");
        _tree.File("left/sub/b.log");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        ShowResults(_vm.Left, _left, "a.txt", Path.Combine("sub", "b.log"));
        _vm.Left.MarkAll();
        await _vm.Left.SetFilterAsync("*.txt");
        Names(_vm.Left).Should().Equal("..", "a.txt");

        await _vm.DeleteAsync(permanent: false);
        await _vm.Left.GoUpAsync();

        _bin.Recycled.Should().Equal(Path.Combine(_left, "a.txt"));
        File.Exists(Path.Combine(_left, "sub", "b.log")).Should().BeTrue();
        _vm.Left.IsShowingResults.Should().BeFalse();
        _vm.Left.MarkedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Compare_with_both_panels_on_one_folder_finds_nothing_and_marks_nothing()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "a");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_left);

        (await _vm.CompareDirectoriesAsync()).Should().BeNull();

        _dialogs.Infos.Should().ContainSingle().Which.Should().Be(Texts.CompareSame);
        _vm.Left.MarkedItems.Should().BeEmpty();
        _vm.Right.MarkedItems.Should().BeEmpty();
    }

    [Fact]
    public async Task Compare_unmarks_items_marked_by_hand_before()
    {
        File.WriteAllText(Path.Combine(_left, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_right, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_left, "only.txt"), "x");
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
        _vm.Left.MarkAll();
        _vm.Right.MarkAll();

        (await _vm.CompareDirectoriesAsync()).Should().BeNull();

        _vm.Left.MarkedItems.Select(i => i.Name).Should().Equal("only.txt");
        _vm.Right.MarkedItems.Should().BeEmpty();
    }

    // ---- server on one side ----

    private async Task OpenServerOnLeftAsync()
    {
        await _vm.Right.NavigateAsync(_right);
        await _vm.Left.NavigateAsync(_left);
        (await _vm.ConnectAsync("ftp://tester@srv/")).Should().BeTrue();
        _vm.Left.IsRemote.Should().BeTrue();
    }

    [Fact]
    public async Task F6_on_a_server_into_a_subfolder_of_the_moved_folder_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_server, "d", "sub"));
        File.WriteAllText(Path.Combine(_server, "d", "sub", "x.txt"), "x");
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "d");
        _dialogs.TransferAnswer = "d/sub/";

        await _vm.MoveAsync();

        _dialogs.Errors.Should().ContainSingle();
        File.Exists(Path.Combine(_server, "d", "sub", "x.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task F6_on_a_server_onto_an_existing_name_is_refused()
    {
        File.WriteAllText(Path.Combine(_server, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_server, "b.txt"), "B");
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.TransferAnswer = "b.txt";

        await _vm.MoveAsync();

        File.ReadAllText(Path.Combine(_server, "b.txt")).Should().Be("B");
        File.ReadAllText(Path.Combine(_server, "a.txt")).Should().Be("A");
    }

    [Fact]
    public async Task Rename_on_a_server_onto_an_existing_name_is_refused()
    {
        File.WriteAllText(Path.Combine(_server, "a.txt"), "A");
        File.WriteAllText(Path.Combine(_server, "b.txt"), "B");
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = "b.txt";

        await _vm.RenameCurrentAsync();

        _dialogs.Errors.Should().ContainSingle();
        File.ReadAllText(Path.Combine(_server, "b.txt")).Should().Be("B");
    }

    /// <summary>
    /// BUG: the existence check is skipped for a change of case only, but servers are case-sensitive: "A.txt" is another
    /// file there, and FTP/S3 rename replaces it.
    /// </summary>
    [Fact]
    public async Task Regression_case_only_rename_on_a_server_overwrites_the_other_case_file()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the folder behind the fake server must hold both names
        }

        File.WriteAllText(Path.Combine(_server, "a.txt"), "lower");
        File.WriteAllText(Path.Combine(_server, "A.txt"), "UPPER");
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "a.txt");
        _dialogs.NameAnswer = "A.txt";

        await _vm.RenameCurrentAsync();

        File.ReadAllText(Path.Combine(_server, "A.txt")).Should().Be("UPPER", "an existing file must not be replaced by a rename");
    }

    [Fact]
    public async Task Server_panel_commands_with_only_dot_dot_focused_do_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_server, "empty"));
        await OpenServerOnLeftAsync();
        Cursor(_vm.Left, "empty");
        await _vm.Left.OpenCurrentAsync();
        _vm.Left.CurrentItem!.Entry.IsParentLink.Should().BeTrue();
        _dialogs.TransferAnswer = _right;
        _dialogs.NameAnswer = "x";

        await _vm.CopyAsync();
        await _vm.MoveAsync();
        await _vm.DeleteAsync(permanent: true);
        await _vm.RenameCurrentAsync();

        _dialogs.LastDefaultTarget.Should().BeNull();
        _dialogs.LastNameInitial.Should().BeNull();
        _dialogs.LastDeletePermanent.Should().BeNull();
        Directory.Exists(Path.Combine(_server, "empty")).Should().BeTrue();
    }

    [Fact]
    public async Task F7_on_a_server_with_an_existing_name_is_an_error_and_nested_names_work()
    {
        Directory.CreateDirectory(Path.Combine(_server, "d"));
        await OpenServerOnLeftAsync();

        _dialogs.NameAnswer = "d";
        await _vm.MakeDirectoryAsync();
        _dialogs.Errors.Should().ContainSingle();

        _dialogs.NameAnswer = "d/e/f";
        await _vm.MakeDirectoryAsync();
        Directory.Exists(Path.Combine(_server, "d", "e", "f")).Should().BeTrue();
        _vm.Left.CurrentItem!.Name.Should().Be("d");
    }
}
