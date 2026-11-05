using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private readonly TempTree _tree = new();

    private SettingsStore Store => new(Path.Combine(_tree.Root, "settings", "settings.json"));

    public void Dispose() => _tree.Dispose();

    private MainViewModel Create() => new(new LocalFileSystem(), new RecordingLauncher(), Store, new FakeDialogs(), new NoPrompts(), new FakeBin());

    [Fact]
    public void Starts_with_left_panel_active_and_switches()
    {
        using var vm = Create();
        vm.ActivePanel.Should().BeSameAs(vm.Left);
        vm.Left.IsActive.Should().BeTrue();

        vm.SwitchPanel();

        vm.ActivePanel.Should().BeSameAs(vm.Right);
        vm.InactivePanel.Should().BeSameAs(vm.Left);
        vm.Left.IsActive.Should().BeFalse();
        vm.Right.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task State_survives_a_restart()
    {
        var left = _tree.Dir("left");
        var right = _tree.Dir("right");

        using (var first = Create())
        {
            await first.InitializeAsync();
            await first.Left.NavigateAsync(left);
            await first.Right.NavigateAsync(right);
            await first.Right.SortByAsync(SortColumn.Modified);
            first.Right.BriefView = true;
            first.SwitchPanel();
            await first.ToggleHiddenAsync();
            first.ToggleCompactRows();
            first.ToggleShowAttributes();
            first.Right.CompactRows.Should().BeTrue("view options apply to both panels");
            first.Save(new WindowSettings { X = 5, Y = 6, Width = 1200, Height = 800 });
        }

        using var second = Create();
        await second.InitializeAsync();

        second.Left.Path.Should().Be(left);
        second.Right.Path.Should().Be(right);
        second.Right.Sort.Should().Be(new SortSpec(SortColumn.Modified));
        second.Right.BriefView.Should().BeTrue();
        second.ActivePanel.Should().BeSameAs(second.Right);
        second.Left.ShowHidden.Should().BeTrue();
        second.Left.CompactRows.Should().BeTrue();
        second.Right.ShowAttributes.Should().BeTrue();
        second.Settings.Window!.Width.Should().Be(1200);
    }

    [Fact]
    public void Panel_split_is_saved_and_clamped()
    {
        using (var first = Create())
        {
            first.Save(window: null, panelSplit: 0.35);
        }

        using (var second = Create())
        {
            second.Settings.PanelSplit.Should().BeApproximately(0.35, 0.0001);
            second.Save(window: null, panelSplit: 0.05); // narrower than a usable panel
        }

        using var third = Create();
        third.Settings.PanelSplit.Should().Be(MainViewModel.MinPanelSplit);
        third.Save(window: null); // no split given keeps the saved one
        Create().Settings.PanelSplit.Should().Be(MainViewModel.MinPanelSplit);
    }

    [Fact]
    public async Task Missing_saved_folder_falls_back_to_its_parent()
    {
        var gone = _tree.Dir("gone");
        using (var first = Create())
        {
            await first.InitializeAsync();
            await first.Left.NavigateAsync(gone);
            first.Save(null);
        }

        Directory.Delete(gone);
        using var second = Create();
        await second.InitializeAsync();

        second.Left.Path.Should().Be(_tree.Root);
    }
}
