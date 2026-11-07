using System.Globalization;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using PlainCommander.Core.Undo;
using Xunit;

namespace PlainCommander.Presentation.Tests.Weird;

/// <summary>Bugs found by clicking through the real app (docs/testy-dziwne-kombinacje.md), fixed in the view models.</summary>
public sealed class GuiFindingsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;

    public GuiFindingsTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(),
            new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, new NoPrompts(), new FakeBin());
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _culture;
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task OpenAsync(string left, string? right = null)
    {
        (await _vm.Left.NavigateAsync(left)).Should().BeTrue();
        (await _vm.Right.NavigateAsync(right ?? _right)).Should().BeTrue();
    }

    private static void Cursor(PanelViewModel panel, string name)
    {
        var index = panel.Items.ToList().FindIndex(i => i.Name == name);
        index.Should().BeGreaterThanOrEqualTo(0, $"{name} should be listed");
        panel.CurrentIndex = index;
    }

    private static string[] Names(PanelViewModel panel) => panel.Items.Select(i => i.Name).ToArray();

    // ---------------------------------------------------------------- 1. a folder deleted from outside

    [Fact]
    public async Task Regression_refresh_of_a_folder_deleted_from_outside_moves_to_the_nearest_existing_parent()
    {
        var deep = _tree.Dir("left/a/b");
        _tree.File("left/a/b/x.txt");
        await OpenAsync(deep);
        Directory.Delete(Path.Combine(_left, "a"), recursive: true);

        (await _vm.Left.RefreshAsync()).Should().BeTrue();

        _vm.Left.Path.Should().Be(_left);
        Names(_vm.Left).Should().NotContain("x.txt");
    }

    [Fact]
    public async Task Regression_f7_in_a_folder_deleted_from_outside_does_not_recreate_it()
    {
        var deep = _tree.Dir("left/a/b");
        await OpenAsync(deep);
        Directory.Delete(Path.Combine(_left, "a"), recursive: true);
        _dialogs.NameAnswer = "new";

        await _vm.MakeDirectoryAsync();

        Directory.Exists(Path.Combine(_left, "a")).Should().BeFalse("the deleted folders must stay deleted");
        _dialogs.LastNameInitial.Should().BeNull("no name is asked for in a folder that is gone");
        _dialogs.Errors.Should().ContainSingle().Which.Should().Contain("is gone");
        _vm.Left.Path.Should().Be(_left);
    }

    [Fact]
    public async Task Regression_rename_in_a_folder_deleted_from_outside_changes_nothing()
    {
        var gone = _tree.Dir("left/gone");
        _tree.File("left/gone/x.txt");
        await OpenAsync(gone);
        Cursor(_vm.Left, "x.txt");
        Directory.Delete(gone, recursive: true);
        _dialogs.NameAnswer = "y.txt";

        await _vm.RenameCurrentAsync();

        Directory.Exists(gone).Should().BeFalse();
        _dialogs.LastNameInitial.Should().BeNull();
        _vm.Left.Path.Should().Be(_left);
        _vm.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Regression_new_file_in_a_folder_deleted_from_outside_does_not_recreate_it()
    {
        var gone = _tree.Dir("left/gone");
        await OpenAsync(gone);
        Directory.Delete(gone);
        _dialogs.NameAnswer = "y.txt";

        await _vm.NewFileAsync();

        Directory.Exists(gone).Should().BeFalse();
        _vm.Left.Path.Should().Be(_left);
        _vm.CanUndo.Should().BeFalse();
    }

    [Fact]
    public async Task Regression_nested_f7_in_an_existing_folder_still_creates_every_level()
    {
        await OpenAsync(_left);
        _dialogs.NameAnswer = "a/b/c";

        await _vm.MakeDirectoryAsync();

        Directory.Exists(Path.Combine(_left, "a", "b", "c")).Should().BeTrue();
        _dialogs.Errors.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- 3. names that fail: explained, typed text kept

    [Fact]
    public async Task Regression_rename_to_a_260_character_name_explains_and_asks_again_with_the_typed_text()
    {
        var file = _tree.File("left/x.txt");
        await OpenAsync(_left);
        Cursor(_vm.Left, "x.txt");
        var tooLong = new string('a', 260);
        _dialogs.NameAnswers.Enqueue(tooLong);
        _dialogs.NameAnswers.Enqueue("short.txt");

        await _vm.RenameCurrentAsync();

        _dialogs.Errors.Should().ContainSingle().Which.Should().Be("The name is too long: at most 255 characters are allowed.");
        _dialogs.NameInitials.Should().Equal("x.txt", tooLong);
        File.Exists(file).Should().BeFalse();
        File.Exists(Path.Combine(_left, "short.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Regression_f7_and_new_file_on_a_taken_name_ask_again_with_the_typed_text()
    {
        _tree.File("left/taken");
        await OpenAsync(_left);
        _dialogs.NameAnswers.Enqueue("taken");
        _dialogs.NameAnswers.Enqueue(null);
        _dialogs.NameAnswers.Enqueue("taken");
        _dialogs.NameAnswers.Enqueue(null);

        await _vm.MakeDirectoryAsync();
        await _vm.NewFileAsync();

        _dialogs.NameInitials.Should().Equal(string.Empty, "taken", string.Empty, "taken");
        _dialogs.Errors.Should().HaveCount(2).And.AllSatisfy(e => e.Should().Contain("already exists"));
    }

    [Fact]
    public void Regression_windows_name_errors_get_the_apps_own_words_not_the_system_text()
    {
        const string polishSystemText = "Nazwa pliku, nazwa katalogu lub składnia etykiety woluminu jest niepoprawna.";

        UserMessages.Describe(new IOException(polishSystemText, unchecked((int)0x8007007B))).Should().Be("Invalid name or path.");
        UserMessages.Describe(new IOException("x", unchecked((int)0x800700CE))).Should().Be("The name or path is too long.");
        UserMessages.Describe(new PathTooLongException("x")).Should().Be("The name or path is too long.");
        UserMessages.Describe(new IOException("x", unchecked((int)0x800700B7))).Should().Be("An item with this name already exists.");
        UserMessages.IsAboutName(new IOException(polishSystemText, unchecked((int)0x8007007B))).Should().BeTrue();
        UserMessages.IsAboutName(new IOException("busy", unchecked((int)0x80070020))).Should().BeFalse();
    }

    // ---------------------------------------------------------------- 4. trailing dot

    [Fact]
    public async Task Regression_rename_to_a_name_ending_in_a_dot_drops_the_dot_like_a_trailing_space()
    {
        _tree.File("left/x.txt");
        await OpenAsync(_left);
        Cursor(_vm.Left, "x.txt");
        _dialogs.NameAnswer = "y.";

        await _vm.RenameCurrentAsync();

        File.Exists(Path.Combine(_left, "y")).Should().BeTrue();
        _vm.Left.CurrentItem!.Name.Should().Be("y");
    }

    // ---------------------------------------------------------------- 5. English singular and plural

    [Fact]
    public void Regression_undo_details_agree_in_number_in_english()
    {
        static UndoEntry One(UndoKind kind) => new("t", [new UndoAction(kind, "/x/a")], DateTime.Now);
        static UndoEntry Two(UndoKind kind) => new("t", [new UndoAction(kind, "/x/a"), new UndoAction(kind, "/x/b")], DateTime.Now);

        Texts.UndoDetails(One(UndoKind.Moved)).Should().Be("• 1 item goes back to where it was, under its old name.");
        Texts.UndoDetails(Two(UndoKind.Moved)).Should().Be("• 2 items go back to where they were, under their old names.");
        Texts.UndoDetails(One(UndoKind.Created)).Should().StartWith("• 1 item it created goes to the Recycle Bin");
        Texts.UndoDetails(Two(UndoKind.Created)).Should().StartWith("• 2 items it created go to the Recycle Bin");
        Texts.UndoDetails(One(UndoKind.Recycled)).Should().Be("• 1 item comes back from the Recycle Bin.");
        Texts.UndoDetails(One(UndoKind.Overwritten)).Should().Be("• Note: 1 item was overwritten; its old content cannot be restored.");
    }

    [Fact]
    public void Regression_byte_counts_and_selection_agree_in_number_in_english()
    {
        Texts.ByteCount(1).Should().Be("1 byte");
        Texts.ByteCount(2048).Should().Be(2048.ToString("N0", CultureInfo.CurrentCulture) + " bytes");
        Texts.Selection(0, 1, 0, 5, 0, 1).Should().Be("Selected 0 of 1 file (0 B of 5 B), 0 of 1 folder");
    }

    // ---------------------------------------------------------------- 6. marks per tab

    [Fact]
    public async Task Regression_marks_survive_switching_tabs_and_back()
    {
        _tree.File("left/a.txt");
        _tree.File("left/b.txt");
        _tree.File("left/c.txt");
        _tree.Dir("left/other");
        await OpenAsync(_left);
        _vm.Left.MarkOnly(new HashSet<string> { "a.txt", "c.txt" });
        Cursor(_vm.Left, "b.txt");

        await _vm.Left.OpenInNewTabAsync(Path.Combine(_left, "other"));
        File.Delete(Path.Combine(_left, "c.txt"));
        await _vm.Left.NextTabAsync();

        _vm.Left.Path.Should().Be(_left);
        _vm.Left.MarkedItems.Select(i => i.Name).Should().Equal("a.txt");
        _vm.Left.CurrentItem!.Name.Should().Be("b.txt");

        _vm.Left.ClearMarks();
        await _vm.Left.NextTabAsync();
        await _vm.Left.NextTabAsync();
        _vm.Left.MarkedItems.Should().BeEmpty("marks cleared before leaving stay cleared");
    }
}
