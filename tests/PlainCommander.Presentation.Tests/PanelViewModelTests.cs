using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class PanelViewModelTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly RecordingLauncher _launcher = new();
    private readonly PanelViewModel _panel;

    public PanelViewModelTests()
    {
        _panel = new PanelViewModel(new LocalFileSystem(), _launcher);
    }

    public void Dispose()
    {
        _panel.Dispose();
        _tree.Dispose();
    }

    private string[] Names => _panel.Items.Select(i => i.Name).ToArray();

    [Fact]
    public async Task Navigate_lists_parent_then_directories_then_files_with_cursor_on_top()
    {
        _tree.File("b.txt");
        _tree.File("a.txt");
        _tree.Dir("zeta");
        _tree.Dir("alpha");

        (await _panel.NavigateAsync(_tree.Root)).Should().BeTrue();

        Names.Should().Equal("..", "alpha", "zeta", "a.txt", "b.txt");
        _panel.Path.Should().Be(_tree.Root);
        _panel.CurrentIndex.Should().Be(0);
        _panel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Status_counts_files_bytes_and_directories()
    {
        _tree.File("a.bin", 2048);
        _tree.File("b.bin", 1024);
        _tree.Dir("d");

        await _panel.NavigateAsync(_tree.Root);

        _panel.Status.Should().ContainAny("2 files (3.0 KB), 1 folder", "2 pliki (3,0 KB), 1 katalog");
    }

    [Fact]
    public async Task Has_marks_follows_marking()
    {
        _tree.File("a.txt");
        await _panel.NavigateAsync(_tree.Root);
        _panel.HasMarks.Should().BeFalse();

        _panel.MarkAll();
        _panel.HasMarks.Should().BeTrue();

        _panel.ClearMarks();
        _panel.HasMarks.Should().BeFalse();
    }

    [Fact]
    public async Task Go_root_opens_the_root_of_the_drive_and_stops_there()
    {
        var deep = _tree.Dir(System.IO.Path.Combine("a", "b"));
        await _panel.NavigateAsync(deep);

        (await _panel.GoRootAsync()).Should().BeTrue();

        _panel.Path.Should().Be(System.IO.Path.GetPathRoot(deep));
        (await _panel.GoRootAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Going_up_puts_the_cursor_on_the_folder_we_came_from()
    {
        _tree.Dir("aaa");
        var target = _tree.Dir("mmm");
        _tree.Dir("zzz");
        await _panel.NavigateAsync(target);

        (await _panel.GoUpAsync()).Should().BeTrue();

        _panel.Path.Should().Be(_tree.Root);
        _panel.CurrentItem!.Name.Should().Be("mmm");
    }

    [Fact]
    public async Task Opening_parent_link_and_directory_navigates_and_files_are_launched()
    {
        var sub = _tree.Dir("sub");
        var file = _tree.File(Path.Combine("sub", "doc.txt"));
        await _panel.NavigateAsync(_tree.Root);

        await _panel.OpenAsync(_panel.Items.Single(i => i.Name == "sub"));
        _panel.Path.Should().Be(sub);

        await _panel.OpenAsync(_panel.Items.Single(i => i.Name == "doc.txt"));
        _launcher.Opened.Should().Equal(file);

        await _panel.OpenAsync(_panel.Items[0]);
        _panel.Path.Should().Be(_tree.Root);
    }

    [Fact]
    public async Task Failed_navigation_keeps_the_current_listing_and_reports_status()
    {
        _tree.File("keep.txt");
        await _panel.NavigateAsync(_tree.Root);

        (await _panel.NavigateAsync(Path.Combine(_tree.Root, "missing"))).Should().BeFalse();

        _panel.Path.Should().Be(_tree.Root);
        Names.Should().Contain("keep.txt");
        _panel.Status.Should().ContainAny("not found", "nie istnieje");
        _panel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Fallback_navigation_walks_up_to_the_nearest_existing_parent()
    {
        var existing = _tree.Dir("exists");

        await _panel.NavigateWithFallbackAsync(Path.Combine(existing, "gone", "deeper"), _tree.Root);

        _panel.Path.Should().Be(existing);
    }

    [Fact]
    public async Task Sorting_keeps_the_cursor_on_the_same_item_and_toggles_direction()
    {
        _tree.File("small.txt", 1);
        _tree.File("big.txt", 100);
        _tree.File("medium.txt", 10);
        await _panel.NavigateAsync(_tree.Root);
        _panel.CurrentIndex = Array.IndexOf(Names, "medium.txt");

        await _panel.SortByAsync(SortColumn.Size);
        Names.Should().Equal("..", "small.txt", "medium.txt", "big.txt");
        _panel.CurrentItem!.Name.Should().Be("medium.txt");
        _panel.SizeSortGlyph.Should().Be("▲");
        _panel.NameSortGlyph.Should().BeEmpty();

        await _panel.SortByAsync(SortColumn.Size);
        Names.Should().Equal("..", "big.txt", "medium.txt", "small.txt");
        _panel.SizeSortGlyph.Should().Be("▼");
    }

    [Fact]
    public async Task Refresh_keeps_the_cursor_on_the_same_name()
    {
        _tree.File("a.txt");
        _tree.File("c.txt");
        await _panel.NavigateAsync(_tree.Root);
        _panel.CurrentIndex = Array.IndexOf(Names, "c.txt");
        _tree.File("b.txt");

        await _panel.RefreshAsync();

        Names.Should().Contain("b.txt");
        _panel.CurrentItem!.Name.Should().Be("c.txt");
    }

    [Fact]
    public async Task Quick_search_jumps_ignores_dead_keys_and_cycles_matches()
    {
        _tree.File("data1.csv");
        _tree.File("data2.csv");
        _tree.File("readme.md");
        await _panel.NavigateAsync(_tree.Root);

        _panel.QuickSearchType('d').Should().BeTrue();
        _panel.CurrentItem!.Name.Should().Be("data1.csv");
        _panel.IsQuickSearchActive.Should().BeTrue();

        _panel.QuickSearchType('x').Should().BeFalse("no name contains 'dx'");
        _panel.QuickSearchText.Should().Be("d");

        _panel.QuickSearchMatches.Should().Be(3, "readme.md has a d too");
        _panel.QuickSearchNext(forward: true);
        _panel.CurrentItem!.Name.Should().Be("data2.csv");
        _panel.QuickSearchNext(forward: true);
        _panel.CurrentItem!.Name.Should().Be("readme.md", "a fragment anywhere in the name matches");
        _panel.QuickSearchNext(forward: true);
        _panel.CurrentItem!.Name.Should().Be("data1.csv", "the search wraps around");

        _panel.QuickSearchBackspace();
        _panel.IsQuickSearchActive.Should().BeFalse();
    }

    [Fact]
    public async Task Navigation_clears_quick_search()
    {
        _tree.Dir("docs");
        await _panel.NavigateAsync(_tree.Root);
        _panel.QuickSearchType('d');

        await _panel.OpenCurrentAsync();

        _panel.QuickSearchText.Should().BeEmpty();
    }

    [Fact]
    public async Task Hidden_entries_appear_only_when_enabled()
    {
        var hidden = _tree.File(".secret");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        await _panel.NavigateAsync(_tree.Root);
        Names.Should().NotContain(".secret");

        await _panel.SetShowHiddenAsync(true);

        Names.Should().Contain(".secret");
    }

    [Fact]
    public async Task Focus_requests_report_the_cursor_index()
    {
        _tree.Dir("a");
        var target = _tree.Dir("b");
        await _panel.NavigateAsync(target);
        var requested = new List<int>();
        _panel.FocusRequested += (_, i) => requested.Add(i);

        await _panel.GoUpAsync();

        requested.Should().Equal(Array.IndexOf(Names, "b"));
    }

    [Fact]
    public async Task Enter_directory_never_launches_files()
    {
        _tree.File("file.txt");
        await _panel.NavigateAsync(_tree.Root);
        _panel.CurrentIndex = Array.IndexOf(Names, "file.txt");

        await _panel.EnterCurrentDirectoryAsync();

        _launcher.Opened.Should().BeEmpty();
        _panel.Path.Should().Be(_tree.Root);
    }

    [Fact]
    public async Task Drive_menu_letters_find_drives_case_insensitively()
    {
        await _panel.RefreshDrivesAsync();
        var first = _panel.Drives.FirstOrDefault(d => d.Letter.Length == 1 && char.IsLetter(d.Letter[0]));
        if (first is null)
        {
            _panel.FindDrive('c').Should().BeNull("no lettered drives on this system (Linux)");
            return;
        }

        _panel.FindDrive(char.ToLowerInvariant(first.Letter[0])).Should().BeSameAs(first);
        _panel.FindDrive(char.ToUpperInvariant(first.Letter[0])).Should().BeSameAs(first);
    }

    [Fact]
    public async Task Quick_search_finds_a_fragment_and_Ctrl_S_keeps_only_the_matches()
    {
        _tree.File("Kwartalny raport.docx");
        _tree.File("Raport 2026.pdf");
        _tree.File("notes.txt");
        _tree.Dir("archiwum");
        await _panel.NavigateAsync(_tree.Root);

        foreach (var c in "rap")
        {
            _panel.QuickSearchType(c).Should().BeTrue();
        }

        _panel.CurrentItem!.Name.Should().Be("Raport 2026.pdf", "a name that starts with the text wins");
        _panel.QuickSearchSummary.Should().Contain("2");

        (await _panel.FilterByQuickSearchAsync()).Should().BeTrue();

        _panel.IsQuickSearchActive.Should().BeFalse();
        _panel.Items.Select(i => i.Name).Should().Equal("..", "archiwum", "Kwartalny raport.docx", "Raport 2026.pdf");
        _panel.CurrentItem!.Name.Should().Be("Raport 2026.pdf", "the cursor stays on the found file");
        _panel.Status.Should().Contain("rap");
    }

    [Fact]
    public async Task Sorting_by_creation_date_shows_it_in_the_date_column()
    {
        var older = _tree.File("older.txt");
        var newer = _tree.File("newer.txt");
        File.SetCreationTime(older, new DateTime(2020, 1, 2, 3, 4, 0));
        File.SetCreationTime(newer, new DateTime(2024, 5, 6, 7, 8, 0));
        File.SetLastWriteTime(older, new DateTime(2025, 1, 1));
        File.SetLastWriteTime(newer, new DateTime(2021, 1, 1));
        await _panel.NavigateAsync(_tree.Root);
        Assert.SkipWhen(File.GetCreationTime(older).Year != 2020, "this file system keeps no creation time");

        await _panel.SortByAsync(Core.Model.SortColumn.Created);

        _panel.Items.Skip(1).Select(i => i.Name).Should().Equal("older.txt", "newer.txt");
        _panel.DateColumnTitle.Should().NotBe(Texts.DateColumn(false));
        _panel.Items[1].DateText.Should().Contain("2020");
        await _panel.SortByAsync(Core.Model.SortColumn.Name);
        _panel.Items[1].DateText.Should().Contain("2021", "by name the column shows the modification date again");
    }

    [Fact]
    public async Task Refresh_that_brings_the_same_entries_keeps_the_rows_and_the_cursor()
    {
        _tree.File("a.txt");
        _tree.File("b.txt");
        _tree.Dir("d");
        await _panel.NavigateAsync(_tree.Root, "b.txt");
        var shown = _panel.Items;
        var focusRequests = 0;
        _panel.FocusRequested += (_, _) => focusRequests++;

        (await _panel.RefreshAsync()).Should().BeTrue();

        _panel.Items.Should().BeSameAs(shown, "nothing changed, so the list is not rebuilt (no flicker, no focus moves)");
        _panel.CurrentItem!.Name.Should().Be("b.txt");
        focusRequests.Should().Be(0);

        _tree.File("c.txt");
        await _panel.RefreshAsync();

        _panel.Items.Should().NotBeSameAs(shown);
        _panel.Items.Select(i => i.Name).Should().Contain("c.txt");
        _panel.CurrentItem!.Name.Should().Be("b.txt");
    }

    [Fact]
    public async Task Wsl_distributions_come_with_the_drive_refresh()
    {
        _panel.WslProvider = () => ["Ubuntu", "Debian"];
        await _panel.NavigateAsync(_tree.Root);

        await _panel.RefreshDrivesAsync();

        var wsl = _panel.Drives.Where(d => d.IsWsl).ToArray();
        wsl.Select(d => d.PillText).Should().Equal("Ubuntu", "Debian");
        wsl.Select(d => d.Root).Should().Equal(@"\\wsl.localhost\Ubuntu", @"\\wsl.localhost\Debian");
        wsl.Select(d => d.MenuText).Should().Equal(@"Ubuntu   \\wsl.localhost\Ubuntu", @"Debian   \\wsl.localhost\Debian");
        _panel.Drives.Last().IsWsl.Should().BeTrue("WSL comes after the drives");
        wsl.Select(d => d.IsCurrent).Should().Equal(false, false);
        _panel.FindDrive('U').Should().BeNull("a distribution is not a drive letter");
    }

    [Fact]
    public async Task OneDrive_folders_follow_the_drives_and_open_at_their_top_folder()
    {
        var personal = _tree.Dir("OneDrive");
        var work = _tree.Dir("OneDrive - Firma");
        var inside = _tree.Dir(Path.Combine("OneDrive - Firma", "Dokumenty"));
        _panel.CloudProvider = () => [new CloudFolder("OneDrive", personal), new CloudFolder("OneDrive - Firma", work)];
        await _panel.NavigateAsync(_tree.Root);

        await _panel.RefreshDrivesAsync();

        var cloud = _panel.Drives.Where(d => d.IsCloud).ToArray();
        cloud.Select(d => d.PillText).Should().Equal("OneDrive", "Firma");
        _panel.Drives.Last().IsCloud.Should().BeTrue("OneDrive comes after the drives");
        cloud.Select(d => d.IsCurrent).Should().Equal(false, false);

        await _panel.NavigateAsync(inside);
        await _panel.ChangeDriveAsync(personal);
        _panel.Path.Should().Be(personal);
        _panel.Drives.Single(d => d.IsCurrent).Root.Should().Be(personal, "inside OneDrive its button is the current one, not the drive that holds it");

        await _panel.NavigateAsync(inside);
        _panel.Drives.Single(d => d.IsCurrent).Root.Should().Be(work);
        await _panel.ChangeDriveAsync(work);
        _panel.Path.Should().Be(work, "the button leads to the top folder even from inside it");
    }

    [Fact]
    public async Task ChangeDrive_meets_the_other_panel_in_its_folder()
    {
        var theirs = _tree.Dir("theirs");
        var root = Path.GetPathRoot(_tree.Root)!;
        _panel.OtherPanelPath = () => theirs;

        // a panel not yet on that drive (here: showing nothing) lands where the other panel is
        (await _panel.ChangeDriveAsync(root)).Should().BeTrue();
        _panel.Path.Should().Be(theirs);

        // already on the drive: its button still goes to the root
        await _panel.ChangeDriveAsync(root);
        _panel.Path.Should().Be(root);
    }
}
