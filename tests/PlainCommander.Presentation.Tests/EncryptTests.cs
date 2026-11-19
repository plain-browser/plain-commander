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
        var temp = Path.Combine(Path.GetTempPath(), "PlainCommander");
        var decryptedBefore = Directory.Exists(temp) ? Directory.GetDirectories(temp, "pce-*").Length : 0;
        await _vm.Left.EnterCurrentDirectoryAsync();

        _vm.Left.IsInArchive.Should().BeTrue();
        _vm.Left.Path.Should().EndWith(Path.Combine("skany.pce", "skany"), "a container holding one folder opens on that folder's content");
        _vm.Left.Items.Select(i => i.Name).Should().Contain("1.png");
        (Directory.Exists(temp) ? Directory.GetDirectories(temp, "pce-*").Length : 0).Should().Be(decryptedBefore, "nothing is decrypted to disk for browsing");

        await _vm.Left.GoUpAsync();
        _vm.Left.Path.Should().EndWith("skany.pce");
        _vm.Left.Items.Where(i => !i.Entry.IsParentLink).Select(i => i.Name).Should().Equal("skany");
    }

    private async Task<string> EncryptedFolderOpenInLeftAsync()
    {
        _tree.File(Path.Combine("skany", "1.png"), 300_000);
        _tree.File(Path.Combine("skany", "2.png"), 40_000);
        _tree.File(Path.Combine("skany", "glebiej", "3.png"), 50_000);
        await _vm.Left.NavigateAsync(_tree.Root);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "skany");
        _dialogs.EncryptAnswer = new EncryptAnswer(".pce", "tajne", Folder: _tree.Root);
        await _vm.EncryptAsync();
        await Until(() => _vm.Left.Items.Any(i => i.Name == "skany.pce"));
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "skany.pce");
        _dialogs.DecryptPasswords.Enqueue("tajne");
        await _vm.Left.EnterCurrentDirectoryAsync();
        _vm.Left.IsInArchive.Should().BeTrue();
        return Path.Combine(_tree.Root, "skany.pce");
    }

    private void Cursor(string name) => _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == name);

    [Fact]
    public async Task F8_inside_a_pce_always_asks_as_for_a_permanent_delete_and_the_file_shrinks_by_what_went()
    {
        var pce = await EncryptedFolderOpenInLeftAsync();
        var before = new FileInfo(pce).Length;
        long first, deeper;
        using (var container = Core.Crypto.PceContainer.Open(pce, "tajne"))
        {
            first = container.Find("skany/1.png")!.StoredSize;
            deeper = container.Find("skany/glebiej/3.png")!.StoredSize;
        }

        first.Should().BePositive();
        Cursor("1.png");

        _dialogs.DeleteAnswer = false;
        await _vm.DeleteAsync(permanent: false);
        _dialogs.LastDeletePermanent.Should().BeTrue("there is no Recycle Bin inside the file, and the question says so");
        new FileInfo(pce).Length.Should().Be(before, "answering no changes nothing");

        _dialogs.DeleteAnswer = true;
        await _vm.DeleteAsync(permanent: false);

        await Until(() => _vm.Left.Items.All(i => i.Name != "1.png"));
        new FileInfo(pce).Length.Should().Be(before - first, "the file shrinks by exactly what the entry took");
        _vm.Left.Items.Select(i => i.Name).Should().Contain(["2.png", "glebiej"]);

        Cursor("glebiej");
        await _vm.DeleteAsync(permanent: true);
        await Until(() => _vm.Left.Items.All(i => i.Name != "glebiej"));
        new FileInfo(pce).Length.Should().Be(before - first - deeper);
    }

    [Fact]
    public async Task Files_are_added_to_a_pce_a_folder_is_made_and_an_entry_renamed_in_place()
    {
        var pce = await EncryptedFolderOpenInLeftAsync();
        var extra = _tree.File("nowy.bin", 70_000);
        await _vm.Right.NavigateAsync(_tree.Root);
        _vm.Activate(_vm.Right);
        _vm.Right.CurrentIndex = _vm.Right.Items.ToList().FindIndex(i => i.Name == "nowy.bin");
        _dialogs.TransferAnswer = _vm.Left.Path;

        await _vm.CopyAsync();

        await Until(() => _vm.Left.Items.Any(i => i.Name == "nowy.bin"));
        File.Exists(extra).Should().BeTrue("F5 leaves the source");
        _vm.Activate(_vm.Left);

        _dialogs.NameAnswer = "podfolder";
        await _vm.MakeDirectoryAsync();
        await Until(() => _vm.Left.Items.Any(i => i.Name == "podfolder" && i.IsDirectory));

        Cursor("2.png");
        _dialogs.NameAnswer = "dwa.png";
        await _vm.RenameCurrentAsync();
        await Until(() => _vm.Left.Items.Any(i => i.Name == "dwa.png"));
        _vm.Left.Items.Select(i => i.Name).Should().NotContain("2.png");

        using var container = Core.Crypto.PceContainer.Open(pce, "tajne");
        container.Entries.Select(e => e.Path).Should().BeEquivalentTo("skany/1.png", "skany/dwa.png", "skany/glebiej/3.png", "skany/nowy.bin", "skany/podfolder");
        container.Find("skany/nowy.bin")!.Size.Should().Be(70_000);
    }

    [Fact]
    public async Task A_change_inside_a_pce_from_an_earlier_version_offers_to_rewrite_it_and_then_works_in_place()
    {
        var folder = _tree.Dir("stare");
        _tree.File(Path.Combine("stare", "a.txt"), 5);
        _tree.File(Path.Combine("stare", "sub", "b.txt"), 7);
        var pce = Path.Combine(_tree.Root, "stare.pce");
        Core.Crypto.PceFile.EncryptLegacy(folder, pce, "tajne", Core.Crypto.PceOptions.Fast, Core.Crypto.PceCompression.None, null, TestContext.Current.CancellationToken);
        var before = File.ReadAllBytes(pce);
        await _vm.Left.NavigateAsync(_tree.Root);
        Cursor("stare.pce");
        _dialogs.DecryptPasswords.Enqueue("tajne");
        await _vm.Left.EnterCurrentDirectoryAsync();
        Cursor("sub");
        await _vm.Left.OpenAsync(_vm.Left.CurrentItem!);
        Cursor("b.txt");

        _dialogs.UpgradeAnswer = false;
        await _vm.DeleteAsync(permanent: true);

        _dialogs.UpgradeQuestions.Should().Equal("stare.pce");
        _vm.Left.Items.Select(i => i.Name).Should().Contain("b.txt");
        File.ReadAllBytes(pce).Should().Equal(before, "declining changes nothing, not even the temporary copy");

        _dialogs.UpgradeAnswer = true;
        await _vm.DeleteAsync(permanent: true);

        Core.Crypto.PceContainer.LayoutVersion(pce).Should().Be(2);
        _vm.Left.Path.Should().Be(Path.Combine(pce, "stare", "sub"), "the panel shows the same place, now inside the file itself");
        _vm.Left.CurrentItem!.Name.Should().Be("b.txt");
        _vm.Left.Status.Should().Be(Texts.PceUpgraded("stare.pce"));
        _vm.Left.Items.Select(i => i.Name).Should().Contain("b.txt", "the command itself is repeated by the user");

        await _vm.DeleteAsync(permanent: true);

        await Until(() => _vm.Left.Items.All(i => i.Name != "b.txt"));
        _dialogs.UpgradeQuestions.Should().HaveCount(2, "a container is not asked about again");
        using var container = Core.Crypto.PceContainer.Open(pce, "tajne");
        container.Entries.Select(e => e.Path).Should().Equal("stare/a.txt");
    }

    [Fact]
    public async Task Change_password_asks_again_after_a_wrong_one_and_the_open_container_keeps_working()
    {
        var pce = await EncryptedFolderOpenInLeftAsync();
        await _vm.Right.NavigateAsync(_tree.Root);
        _vm.Activate(_vm.Right);
        _vm.Right.CurrentIndex = _vm.Right.Items.ToList().FindIndex(i => i.Name == "skany.pce");
        var size = new FileInfo(pce).Length;
        _dialogs.ChangePasswordAnswers.Enqueue(new ChangePasswordAnswer("nie to", "nowe"));
        _dialogs.ChangePasswordAnswers.Enqueue(new ChangePasswordAnswer("tajne", "nowe"));

        await _vm.ChangePcePasswordAsync();

        _dialogs.ChangePasswordQuestions.Should().Equal((false, false), (true, false));
        _dialogs.Errors.Should().BeEmpty();
        _dialogs.Infos.Should().Equal(Texts.PasswordChanged("skany.pce"));
        new FileInfo(pce).Length.Should().Be(size);
        var old = () => Core.Crypto.PceContainer.Open(pce, "tajne").Dispose();
        old.Should().Throw<Core.Crypto.PceWrongPasswordException>();
        Core.Crypto.PceContainer.Open(pce, "nowe").Dispose();

        // The left panel is inside the container: it goes on without asking for the new password.
        _vm.Activate(_vm.Left);
        Cursor("2.png");
        await _vm.DeleteAsync(permanent: true);
        await Until(() => _vm.Left.Items.All(i => i.Name != "2.png"));
        _dialogs.DecryptWrongFlags.Should().HaveCount(1);
    }

    [Fact]
    public async Task Change_password_rewrites_a_pce_from_an_earlier_version_and_wants_a_pce_under_the_cursor()
    {
        var plain = _tree.File("a.txt", 5);
        var pce = plain + ".pce";
        Core.Crypto.PceFile.EncryptLegacy(plain, pce, "tajne", Core.Crypto.PceOptions.Fast, Core.Crypto.PceCompression.None, null, TestContext.Current.CancellationToken);
        await _vm.Left.NavigateAsync(_tree.Root);
        Cursor("a.txt");

        await _vm.ChangePcePasswordAsync();

        _dialogs.Errors.Should().ContainSingle().Which.Should().Be(Texts.ChangePasswordNothing);
        Cursor("a.txt.pce");
        _dialogs.ChangePasswordAnswers.Enqueue(new ChangePasswordAnswer("tajne", "nowe"));

        await _vm.ChangePcePasswordAsync();

        _dialogs.ChangePasswordQuestions.Should().Equal((false, true));
        Core.Crypto.PceContainer.LayoutVersion(pce).Should().Be(2);
        Core.Crypto.PceFile.Peek(pce, "nowe").Metadata.Name.Should().Be("a.txt");
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
