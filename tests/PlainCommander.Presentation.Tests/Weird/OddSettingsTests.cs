using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests.Weird;

/// <summary>settings.json written by hand, by an older version or half by a crash: the app must still start.</summary>
public sealed class OddSettingsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _file;

    public OddSettingsTests()
    {
        _file = Path.Combine(_tree.Root, "s", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
    }

    public void Dispose() => _tree.Dispose();

    private MainViewModel Create() => new(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(_file), new FakeDialogs(), new NoPrompts(), new FakeBin());

    private async Task StartAndSaveAsync()
    {
        using var vm = Create();
        await vm.InitializeAsync();
        vm.IsFavorite(_tree.Root).Should().BeFalse();
        vm.NewTab();
        await vm.NextTabAsync();
        vm.Save(new WindowSettings(), panelSplit: double.NaN);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"theme\": \"Purple\"}")]
    [InlineData("{\"activePanel\": \"Middle\"}")]
    [InlineData("{\"panelSplit\": \"wide\"}")]
    public async Task Unreadable_content_falls_back_to_defaults(string json)
    {
        File.WriteAllText(_file, json);

        await StartAndSaveAsync();

        using var vm = Create();
        vm.Settings.PanelSplit.Should().Be(0.5);
    }

    [Fact]
    public async Task Extreme_numbers_are_kept_within_bounds()
    {
        File.WriteAllText(_file, """
            {
              "panelSplit": -7,
              "panelFontSize": -3,
              "copyParallelism": -1,
              "activePanel": 9,
              "theme": 42,
              "left": { "path": "", "activeTab": 99, "detail": 77, "sort": { "column": 99, "direction": 5 },
                        "tabs": [ { "path": "" }, { "path": "/definitely/not/here" } ] },
              "right": { "activeTab": -5, "tabs": [] },
              "window": { "x": -100000, "y": 99999999, "width": -5, "height": 0 }
            }
            """);

        using var vm = Create();
        await vm.InitializeAsync();

        vm.Left.FontSize.Should().Be(MainViewModel.MinFontSize);
        vm.Left.ActiveTabIndex.Should().Be(1);
        vm.Right.ActiveTabIndex.Should().Be(0);
        vm.ActivePanel.Should().BeSameAs(vm.Left);
        vm.Left.Path.Should().NotBeEmpty();
        await vm.Left.SortByAsync(SortColumn.Name);
        vm.Left.Sort.Column.Should().Be(SortColumn.Name);
        await vm.Left.SwitchTabAsync(0);
        vm.Left.Path.Should().NotBeEmpty("an empty saved tab falls back to a real folder");
        vm.Save(null, panelSplit: -1);
        vm.Settings.PanelSplit.Should().Be(MainViewModel.MinPanelSplit);
    }

    [Theory]
    [InlineData("{\"remoteSites\": null}")]
    [InlineData("{\"commandHistory\": [null]}")]
    [InlineData("{\"favorites\": [null]}")]
    [InlineData("{\"editor\": null, \"panelFont\": null, \"language\": null}")]
    [InlineData("{\"left\": {\"path\": null, \"sort\": null, \"tabs\": null}}")]
    public async Task Some_null_values_are_tolerated(string json)
    {
        File.WriteAllText(_file, json);

        await StartAndSaveAsync();
    }

    /// <summary>
    /// BUG: "a broken file never stops the app" (SettingsStore), yet a JSON null where a section or list is expected
    /// passes the store and makes the MainViewModel constructor throw, so the app does not start.
    /// </summary>
    [Theory]
    [InlineData("{\"left\": null}")]
    [InlineData("{\"right\": null}")]
    [InlineData("{\"favorites\": null}")]
    [InlineData("{\"commandHistory\": null}")]
    [InlineData("{\"trustedHosts\": null}")]
    [InlineData("{\"left\": {\"tabs\": [null]}}")]
    public async Task Regression_null_section_in_settings_json_crashes_startup(string json)
    {
        File.WriteAllText(_file, json);

        await StartAndSaveAsync();
    }

    /// <summary>BUG: an unreadable settings.json (access denied) throws UnauthorizedAccessException out of SettingsStore.Load.</summary>
    [Fact]
    public void Regression_unreadable_settings_json_crashes_startup()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        File.WriteAllText(_file, "{}");
        File.SetUnixFileMode(_file, UnixFileMode.None);

        var create = () => Create().Dispose();

        create.Should().NotThrow();
    }

    [Fact]
    public void Corrupt_file_is_set_aside()
    {
        File.WriteAllText(_file, "{ not json");

        using var vm = Create();

        File.Exists(_file + ".bad").Should().BeTrue();
        vm.Settings.Left.Path.Should().BeEmpty();
    }
}
