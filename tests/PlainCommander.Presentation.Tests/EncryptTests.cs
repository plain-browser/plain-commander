using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>Files → Encrypt: each item into its own password-protected archive, then the question about the originals.</summary>
public sealed class EncryptTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;

    public EncryptTests()
    {
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s.json")), _dialogs, new NoPrompts(), new FakeBin());
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue();
    }

    [Fact]
    public async Task Encrypts_each_file_and_deletes_the_originals_when_asked()
    {
        Assert.SkipUnless(ArchiveReaders.SevenZipAvailable, "7z.dll is only loaded on Windows");
        var a = _tree.File("a.txt", 5);
        var b = _tree.File("b.txt", 5);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.MarkAll();
        _dialogs.EncryptAnswer = new EncryptAnswer(".7z", "tajne");
        _dialogs.DeleteOriginalsAnswer = true;

        await _vm.EncryptAsync();

        File.Exists(a + ".7z").Should().BeTrue();
        File.Exists(b + ".7z").Should().BeTrue();
        _dialogs.DeleteOriginalsQuestions.Should().Be(1);
        await Until(() => !File.Exists(a) && !File.Exists(b));
    }

    [Fact]
    public async Task Keeps_the_originals_when_the_user_says_so()
    {
        Assert.SkipUnless(ArchiveReaders.SevenZipAvailable, "7z.dll is only loaded on Windows");
        var a = _tree.File("a.txt", 5);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "a.txt");
        _dialogs.EncryptAnswer = new EncryptAnswer(".zip", "tajne");

        await _vm.EncryptAsync();

        File.Exists(a + ".zip").Should().BeTrue();
        File.Exists(a).Should().BeTrue();
    }

    [Fact]
    public async Task Without_seven_zip_it_explains_instead_of_asking()
    {
        Assert.SkipWhen(ArchiveReaders.SevenZipAvailable, "7-Zip is there");
        _tree.File("a.txt", 5);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = 1;
        _dialogs.EncryptAnswer = new EncryptAnswer(".7z", "x");

        await _vm.EncryptAsync();

        _dialogs.Errors.Should().ContainSingle().Which.Should().Be(Texts.EncryptUnavailable);
    }

    [Fact]
    public async Task Pce_needs_no_seven_zip_and_enter_opens_a_single_file_after_a_wrong_password_and_F5_copies_it_out()
    {
        var a = _tree.File("a.txt", 5);
        var destination = _tree.Dir("out");
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "a.txt");
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "tajne", Folder: _tree.Root);
        _dialogs.DeleteOriginalsAnswer = true;

        await _vm.EncryptAsync();

        await Until(() => !File.Exists(a) && File.Exists(a + ".pce"));
        await Until(() => _vm.Left.Items.Any(i => i.Name == "a.txt.pce"));
        var container = _vm.Left.Items.Single(i => i.Name == "a.txt.pce");
        container.IsEncrypted.Should().BeTrue();

        _dialogs.DecryptPasswords.Enqueue("zle");
        _dialogs.DecryptPasswords.Enqueue("tajne");
        await _vm.Left.OpenAsync(container);

        _dialogs.DecryptWrongFlags.Should().Equal(false, true);
        _vm.Left.IsInArchive.Should().BeTrue("Enter opens the content like an archive");
        _vm.Left.Items.Select(i => i.Name).Should().Contain("a.txt");
        File.Exists(a).Should().BeFalse("nothing is written to disk until the user copies it out");

        await _vm.Right.NavigateAsync(destination);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "a.txt");
        _dialogs.TransferAnswer = destination;
        await _vm.CopyAsync();
        await Until(() => File.Exists(Path.Combine(destination, "a.txt")));
        new FileInfo(Path.Combine(destination, "a.txt")).Length.Should().Be(5);

        await _vm.Left.GoUpAsync();
        _vm.Left.Path.Should().Be(_tree.Root, "up from the content leads back to the folder holding the .pce");
        _vm.Left.CurrentItem!.Name.Should().Be("a.txt.pce");
    }

    [Fact]
    public async Task Ctrl_PgDn_browses_the_folder_inside_a_pce_file()
    {
        _tree.File(Path.Combine("skany", "1.png"), 3);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "skany");
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "tajne");
        _dialogs.DeleteOriginalsAnswer = false;
        await _vm.EncryptAsync();
        await Until(() => _vm.Left.Items.Any(i => i.Name == "skany.pce"));

        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "skany.pce");
        _dialogs.DecryptPasswords.Enqueue("tajne");
        await _vm.Left.EnterCurrentDirectoryAsync();

        _vm.Left.IsInArchive.Should().BeTrue();
        _vm.Left.Path.Should().EndWith("skany.tar");
        _vm.Left.Items.Select(i => i.Name).Should().Contain("1.png");
    }

    [Fact]
    public async Task Decrypt_command_wants_pce_files()
    {
        _tree.File("a.txt", 5);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "a.txt");

        await _vm.DecryptAsync();

        _dialogs.Errors.Should().ContainSingle().Which.Should().Be(Texts.DecryptNothing);
    }

    [Fact]
    public async Task Pce_goes_to_the_chosen_folder_by_default_the_other_panel_and_the_source_stays_put()
    {
        var folder = _tree.File(Path.Combine("2", "a.txt"), 5);
        var destination = _tree.Dir("out");
        await _vm.Left.NavigateAsync(_tree.Root);
        await _vm.Right.NavigateAsync(destination);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "2");
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "tajne", Folder: Path.Combine(destination, "nowy"));

        await _vm.EncryptAsync();

        _dialogs.LastEncryptFolder.Should().Be(destination, "the default is the other panel, like Alt+F5");
        var target = Path.Combine(destination, "nowy", "2.pce");
        await Until(() => File.Exists(target));
        File.Exists(Path.Combine(_tree.Root, "2.pce")).Should().BeFalse("nothing is written next to the original");
        File.Exists(folder).Should().BeTrue();
    }

    [Fact]
    public async Task Pce_refuses_a_target_inside_the_folder_being_encrypted()
    {
        _tree.File(Path.Combine("2", "a.txt"), 5);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "2");
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "tajne", Folder: Path.Combine(_tree.Root, "2", "sub"));

        await _vm.EncryptAsync();

        _dialogs.Errors.Should().ContainSingle().Which.Should().Be(Texts.EncryptIntoItself);
        Directory.Exists(Path.Combine(_tree.Root, "2", "sub")).Should().BeFalse();
    }

    [Fact]
    public async Task Files_from_the_folder_inside_a_pce_can_be_copied_to_the_other_panel()
    {
        _tree.File(Path.Combine("skany", "1.png"), 3);
        _tree.File(Path.Combine("skany", "glebiej", "2.png"), 4);
        var destination = _tree.Dir("out");
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "skany");
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "tajne", Folder: _tree.Root);
        await _vm.EncryptAsync();
        await Until(() => _vm.Left.Items.Any(i => i.Name == "skany.pce"));
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "skany.pce");
        _dialogs.DecryptPasswords.Enqueue("tajne");
        await _vm.Left.EnterCurrentDirectoryAsync();
        await _vm.Right.NavigateAsync(destination);
        _vm.Left.IsInArchive.Should().BeTrue();
        _vm.Left.MarkAll();
        _dialogs.TransferAnswer = destination;

        await _vm.CopyAsync();

        await Until(() => File.Exists(Path.Combine(destination, "1.png")) && File.Exists(Path.Combine(destination, "glebiej", "2.png")));
        new FileInfo(Path.Combine(destination, "glebiej", "2.png")).Length.Should().Be(4);
    }

    [Fact]
    public async Task A_password_that_opened_a_pce_is_remembered_until_the_file_stops_accepting_it()
    {
        _tree.File("a.txt", 5);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "a.txt");
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "tajne", Folder: _tree.Root);
        await _vm.EncryptAsync();
        await Until(() => _vm.Left.Items.Any(i => i.Name == "a.txt.pce"));
        var container = _vm.Left.Items.Single(i => i.Name == "a.txt.pce");

        _dialogs.DecryptPasswords.Enqueue("tajne");
        await _vm.Left.OpenAsync(container);
        _vm.Left.IsInArchive.Should().BeTrue();
        _dialogs.DecryptWrongFlags.Should().HaveCount(1);

        await _vm.Left.GoUpAsync();
        await _vm.Left.OpenAsync(_vm.Left.Items.Single(i => i.Name == "a.txt.pce"));

        _vm.Left.IsInArchive.Should().BeTrue("the second time no password is asked");
        _dialogs.DecryptWrongFlags.Should().HaveCount(1);

        // the file is replaced by one with another password: the remembered one no longer fits, so it asks again
        File.Delete(Path.Combine(_tree.Root, "a.txt.pce"));
        await _vm.Left.GoUpAsync();
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "inne", Folder: _tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "a.txt");
        await _vm.EncryptAsync();
        await Until(() => File.Exists(Path.Combine(_tree.Root, "a.txt.pce")));
        await _vm.Left.RefreshAsync();
        _dialogs.DecryptPasswords.Enqueue("inne");
        await _vm.Left.OpenAsync(_vm.Left.Items.Single(i => i.Name == "a.txt.pce"));

        _dialogs.DecryptWrongFlags.Should().HaveCount(2);
        _vm.Left.IsInArchive.Should().BeTrue();
    }
}
