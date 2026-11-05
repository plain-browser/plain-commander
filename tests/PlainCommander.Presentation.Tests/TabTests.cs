using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class TabTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _a;
    private readonly string _b;

    public TabTests()
    {
        _a = _tree.Dir("alpha");
        _b = _tree.Dir("beta");
        _tree.File(Path.Combine("alpha", "one.txt"));
        _tree.File(Path.Combine("alpha", "two.txt"));
        _tree.File(Path.Combine("beta", "x.md"));
    }

    public void Dispose() => _tree.Dispose();

    private MainViewModel Create() => new(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), new FakeDialogs(), new NoPrompts(), new FakeBin());

    [Fact]
    public async Task A_new_tab_copies_the_current_one_and_switching_restores_each_tabs_state()
    {
        using var vm = Create();
        var panel = vm.Left;
        await panel.NavigateAsync(_a);
        panel.CurrentIndex = panel.Items.ToList().FindIndex(static i => i.Name == "two.txt");
        panel.HasTabs.Should().BeFalse("one tab shows no strip");

        panel.NewTab();
        panel.Tabs.Should().HaveCount(2);
        panel.ActiveTabIndex.Should().Be(1);
        panel.HasTabs.Should().BeTrue();
        panel.Tabs.Select(t => t.IsActive).Should().Equal(false, true);

        await panel.NavigateAsync(_b);
        await panel.SortByAsync(SortColumn.Size);
        panel.BriefView = true;
        panel.Tabs[1].Title.Should().Be("beta", "navigating renames the active tab");

        await panel.SwitchTabAsync(0);
        panel.Path.Should().Be(_a);
        panel.BriefView.Should().BeFalse();
        panel.Sort.Column.Should().Be(SortColumn.Name);
        panel.CurrentItem!.Name.Should().Be("two.txt", "the cursor comes back to the row it left");

        await panel.SwitchTabAsync(1);
        panel.Path.Should().Be(_b);
        panel.BriefView.Should().BeTrue();
        panel.Sort.Column.Should().Be(SortColumn.Size);
    }

    [Fact]
    public async Task Closing_tabs_keeps_one_and_picks_a_neighbour()
    {
        using var vm = Create();
        var panel = vm.Left;
        await panel.NavigateAsync(_a);
        panel.NewTab();
        await panel.NavigateAsync(_b);
        panel.NewTab();
        await panel.NavigateAsync(_tree.Root);
        panel.Tabs.Select(t => t.Title).Should().Equal("alpha", "beta", Path.GetFileName(_tree.Root));

        await panel.CloseTabAsync(0); // an inactive tab left of the active one
        panel.ActiveTabIndex.Should().Be(1);
        panel.Path.Should().Be(_tree.Root, "the active tab stays put");

        await panel.CloseActiveTabAsync();
        panel.Tabs.Should().HaveCount(1);
        panel.Path.Should().Be(_b);
        panel.HasTabs.Should().BeFalse();

        await panel.CloseActiveTabAsync();
        panel.Tabs.Should().HaveCount(1, "the last tab cannot be closed");
    }

    [Fact]
    public async Task Next_and_previous_wrap_around_and_a_deleted_folder_falls_back()
    {
        using var vm = Create();
        var panel = vm.Left;
        await panel.NavigateAsync(_a);
        panel.NewTab();
        await panel.NavigateAsync(_b);

        await panel.NextTabAsync();
        panel.Path.Should().Be(_a, "after the last tab comes the first");
        await panel.NextTabAsync(-1);
        panel.Path.Should().Be(_b);

        var gone = _tree.Dir("gone");
        await panel.NavigateAsync(gone);
        await panel.SwitchTabAsync(0);
        Directory.Delete(gone);
        await panel.SwitchTabAsync(1);
        panel.Path.Should().Be(_tree.Root, "the nearest existing parent");
    }

    [Fact]
    public async Task Open_in_new_tab_leaves_the_current_tab_where_it_was()
    {
        using var vm = Create();
        await vm.Left.NavigateAsync(_tree.Root);
        vm.Left.CurrentIndex = vm.Left.Items.ToList().FindIndex(static i => i.Name == "beta");

        await vm.OpenCurrentInNewTabAsync();

        vm.Left.Tabs.Select(t => t.Path).Should().Equal(_tree.Root, _b);
        vm.Left.Path.Should().Be(_b);
        await vm.Left.SwitchTabAsync(0);
        vm.Left.Path.Should().Be(_tree.Root);
    }

    [Fact]
    public async Task Tabs_survive_a_restart_with_their_sort_view_and_the_active_one()
    {
        using (var first = Create())
        {
            await first.Left.NavigateAsync(_a);
            first.Left.NewTab();
            await first.Left.NavigateAsync(_b);
            await first.Left.SortByAsync(SortColumn.Modified);
            first.Left.BriefView = true;
            first.Right.Tabs.Should().HaveCount(1);
            first.Save(null);
        }

        using var second = Create();
        await second.InitializeAsync();

        second.Left.Tabs.Select(t => t.Path).Should().Equal(_a, _b);
        second.Left.ActiveTabIndex.Should().Be(1);
        second.Left.Path.Should().Be(_b);
        second.Left.Sort.Column.Should().Be(SortColumn.Modified);
        second.Left.BriefView.Should().BeTrue();
        second.Left.Tabs[0].Sort.Column.Should().Be(SortColumn.Name);
        second.Right.Tabs.Should().HaveCount(1);
    }
}
