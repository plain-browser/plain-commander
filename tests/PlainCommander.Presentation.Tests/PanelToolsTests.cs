using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>Ctrl+S filter, Ctrl+B flat view, copying names, Ctrl+Q quick view.</summary>
public sealed class PanelToolsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly MemoryClipboard _clipboard = new();
    private readonly MainViewModel _vm;
    private readonly string _left;
    private readonly string _right;

    public PanelToolsTests()
    {
        _left = _tree.Dir("left");
        _right = _tree.Dir("right");
        _tree.File("left/report.pdf", 10);
        _tree.File("left/notes.txt", 5);
        _tree.File("left/Report-2024.docx", 7);
        _tree.File("left/docs/a.pdf", 3);
        _tree.File("left/docs/deep/b.txt", 4);
        _vm = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "settings.json")), _dialogs, new NoPrompts(), new FakeBin(), clipboard: _clipboard);
        _vm.QuickView.Delay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tree.Dispose();
    }

    private async Task OpenAsync()
    {
        await _vm.Left.NavigateAsync(_left);
        await _vm.Right.NavigateAsync(_right);
    }

    private static void Cursor(PanelViewModel panel, string name) => panel.CurrentIndex = panel.Items.ToList().FindIndex(i => i.Name == name);

    [Fact]
    public async Task The_filter_combines_extensions_sizes_and_files_only_and_is_offered_again_for_editing()
    {
        await OpenAsync();
        _dialogs.FilterAnswerFull = new Core.Navigation.PanelFilter { Extensions = ["pdf", "docx"], MinSize = 8, FilesOnly = true };

        await _vm.FilterAsync();

        // folders hidden; Report-2024.docx (7 B) is below the minimum
        _vm.Left.Items.Select(i => i.Name).Should().Equal("..", "report.pdf");
        _vm.Left.Status.Should().Contain("pdf/docx");
        _dialogs.FilterAnswerFull = null;
        _dialogs.FilterAnswer = null; // cancel
        await _vm.FilterAsync();
        _dialogs.FilterInitial!.Extensions.Should().Equal("pdf", "docx");
        _vm.Left.Filter.Should().NotBeNull("cancel keeps the filter");
    }

    [Fact]
    public async Task Ctrl_B_with_a_filter_lists_matching_files_from_all_subfolders()
    {
        await OpenAsync();
        _dialogs.FilterAnswer = "*.pdf";
        await _vm.FilterAsync();

        await _vm.ToggleFlatViewAsync();

        _vm.Left.Items.Select(i => Path.GetFileName(i.Name)).Should().BeEquivalentTo(["..", "report.pdf", "a.pdf"]);
    }

    [Fact]
    public async Task A_mask_filter_keeps_folders_and_matching_files_until_the_folder_changes()
    {
        await OpenAsync();
        _dialogs.FilterAnswer = "*.pdf";

        await _vm.FilterAsync();

        _vm.Left.Items.Select(i => i.Name).Should().Equal("..", "docs", "report.pdf");
        _vm.Left.Status.Should().Contain("*.pdf");
        await _vm.Left.RefreshAsync();
        _vm.Left.Items.Should().HaveCount(3, "a refresh keeps the filter");
        await _vm.Left.NavigateAsync(Path.Combine(_left, "docs"));
        _vm.Left.Filter.Should().BeNull("another folder starts unfiltered");
        _vm.Left.Items.Select(i => i.Name).Should().Contain("deep");
    }

    [Fact]
    public async Task Plain_text_filters_by_part_of_the_name_and_an_empty_answer_removes_it()
    {
        await OpenAsync();
        _dialogs.FilterAnswer = "report";
        await _vm.FilterAsync();
        _vm.Left.Items.Select(i => i.Name).Should().BeEquivalentTo(["..", "docs", "report.pdf", "Report-2024.docx"]);

        _dialogs.FilterAnswer = string.Empty;
        await _vm.FilterAsync();

        _vm.Left.Filter.Should().BeNull();
        _vm.Left.Items.Should().HaveCount(5);
    }

    [Fact]
    public async Task Leaving_the_flat_view_keeps_the_cursor_on_the_file_or_on_the_folder_leading_to_it()
    {
        await OpenAsync();
        await _vm.ToggleFlatViewAsync();
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == Path.Combine("docs", "deep", "b.txt"));

        await _vm.ToggleFlatViewAsync();

        _vm.Left.CurrentItem!.Name.Should().Be("docs");

        await _vm.ToggleFlatViewAsync();
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "notes.txt");
        await _vm.ToggleFlatViewAsync();

        _vm.Left.CurrentItem!.Name.Should().Be("notes.txt");
    }

    [Fact]
    public async Task Flat_view_lists_every_file_below_with_relative_names_and_toggles_back()
    {
        await OpenAsync();

        await _vm.ToggleFlatViewAsync();

        _vm.Left.IsFlat.Should().BeTrue();
        _vm.Left.Items.Skip(1).Select(i => i.Name).Should().BeEquivalentTo(
            ["report.pdf", "notes.txt", "Report-2024.docx", Path.Combine("docs", "a.pdf"), Path.Combine("docs", "deep", "b.txt")]);
        _vm.Left.ResultsTitle.Should().Contain("5");

        _dialogs.FilterAnswer = "*.txt";
        await _vm.FilterAsync();
        _vm.Left.Items.Skip(1).Select(i => i.Name).Should().BeEquivalentTo(["notes.txt", Path.Combine("docs", "deep", "b.txt")], "the filter works on the flat list too");

        await _vm.ToggleFlatViewAsync();
        _vm.Left.IsFlat.Should().BeFalse();
        _vm.Left.Path.Should().Be(_left);
    }

    [Fact]
    public async Task Names_or_full_paths_of_the_marked_items_go_to_the_clipboard()
    {
        await OpenAsync();
        _vm.Left.ToggleMark(_vm.Left.Items.Single(i => i.Name == "notes.txt"));
        _vm.Left.ToggleMark(_vm.Left.Items.Single(i => i.Name == "report.pdf"));

        _vm.CopyNamesToClipboard(fullPaths: false);
        var names = _clipboard.Text;
        _vm.CopyNamesToClipboard(fullPaths: true);

        names.Should().Be("notes.txt" + Environment.NewLine + "report.pdf" + Environment.NewLine);
        _clipboard.Text.Should().Contain(Path.Combine(_left, "report.pdf"));
        _vm.Left.Status.Should().Contain("2");
    }

    [Fact]
    public async Task Quick_view_follows_the_cursor_and_shows_folders_as_counts()
    {
        _tree.File("left/zz.txt");
        File.WriteAllText(Path.Combine(_left, "zz.txt"), "hello quick view");
        await OpenAsync();
        Cursor(_vm.Left, "zz.txt");

        _vm.ToggleQuickView();
        await WaitForAsync(() => _vm.QuickView.Viewer?.Text.Contains("hello quick view") == true);

        _vm.QuickView.IsOpen.Should().BeTrue();
        _vm.QuickView.ReplacesLeft.Should().BeFalse("the left panel is active, so the view takes the right one's place");
        _vm.QuickView.Title.Should().Be("zz.txt");

        Cursor(_vm.Left, "docs");
        await WaitForAsync(() => _vm.QuickView.Message.Length > 0);
        _vm.QuickView.Viewer.Should().BeNull();
        _vm.QuickView.Message.Should().Contain("1");

        _vm.ToggleQuickView();
        _vm.QuickView.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task Attributes_dialog_starts_from_the_file_and_its_answer_is_applied()
    {
        await OpenAsync();
        Cursor(_vm.Left, "notes.txt");
        var when = new DateTimeOffset(2023, 5, 6, 7, 8, 0, TimeSpan.Zero);
        _dialogs.AttributesAnswer = new Core.Operations.AttributeChange(FileAttributes.ReadOnly, 0, when, Recursive: false);
        var file = Path.Combine(_left, "notes.txt");
        var before = File.GetLastWriteTimeUtc(file);

        await _vm.ChangePermissionsAsync();

        _dialogs.LastAttributesInitial!.Value.Modified.UtcDateTime.Should().BeCloseTo(before, TimeSpan.FromMilliseconds(1));
        File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
        File.GetLastWriteTimeUtc(file).Should().Be(when.UtcDateTime);
        File.SetAttributes(file, FileAttributes.Normal);
    }

    [Fact]
    public async Task Checksums_are_written_next_to_the_files_and_checked_with_a_summary()
    {
        await OpenAsync();
        _vm.Left.ToggleMark(_vm.Left.Items.Single(i => i.Name == "notes.txt"));
        _vm.Left.ToggleMark(_vm.Left.Items.Single(i => i.Name == "docs"));
        _dialogs.ChecksumAnswer = new ChecksumAnswer(Core.Operations.ChecksumKind.Md5, "left");

        await _vm.CreateChecksumsAsync();

        _dialogs.LastChecksumDefault.Should().Be("left.sha256", "several items: named after the folder");
        var sums = Path.Combine(_left, "left.md5");
        File.ReadAllLines(sums).Should().HaveCount(3).And.Contain(l => l.EndsWith("*docs/deep/b.txt", StringComparison.Ordinal));
        _vm.Left.CurrentItem!.Name.Should().Be("left.md5");

        await _vm.VerifyChecksumsAsync();

        _dialogs.Infos.Should().ContainSingle().Which.Should().Contain("3");
    }

    [Fact]
    public async Task Compare_files_takes_two_marked_files_or_one_from_each_panel()
    {
        File.WriteAllText(Path.Combine(_left, "notes.txt"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(_right, "notes.txt"), "one\nthree\n");
        await OpenAsync();
        Cursor(_vm.Left, "notes.txt");
        Cursor(_vm.Right, "notes.txt");

        var diff = await _vm.CompareFilesAsync();

        diff!.LeftPath.Should().Be(Path.Combine(_left, "notes.txt"));
        diff.RightPath.Should().Be(Path.Combine(_right, "notes.txt"));
        await diff.LoadAsync(TestContext.Current.CancellationToken);
        diff.Rows.Should().NotBeEmpty();

        Cursor(_vm.Right, "..");
        (await _vm.CompareFilesAsync()).Should().BeNull();
        _dialogs.Errors.Should().ContainSingle();

        File.WriteAllBytes(Path.Combine(_left, "x.bin"), [0, 1, 2, 0]);
        File.WriteAllBytes(Path.Combine(_left, "y.bin"), [0, 1, 3, 0]);
        await _vm.Left.RefreshAsync();
        _vm.Left.ToggleMark(_vm.Left.Items.Single(i => i.Name == "x.bin"));
        _vm.Left.ToggleMark(_vm.Left.Items.Single(i => i.Name == "y.bin"));
        var binary = (await _vm.CompareFilesAsync())!;
        await binary.LoadAsync(TestContext.Current.CancellationToken);
        binary.Error.Should().Contain("2", "the first difference is at byte 2");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue();
    }

    private sealed class MemoryClipboard : IClipboard
    {
        public string Text { get; private set; } = string.Empty;

        public void SetText(string text) => Text = text;
    }
}
