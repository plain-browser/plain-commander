using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private MainViewModel Create() => new(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), new FakeDialogs(), new NoPrompts(), new FakeBin());

    [Fact]
    public void Changes_apply_at_once_to_both_panels_and_survive_a_restart()
    {
        using (var main = Create())
        {
            var settings = new SettingsViewModel(main)
            {
                Theme = AppTheme.Dark,
                CompactRows = true,
                ShowAttributes = true,
                PanelFont = "Cascadia Mono",
                PanelFontSize = 15,
                Editor = @"C:\Tools\code.cmd",
                Language = "en-US",
                CopyParallelism = 4,
            };

            main.Theme.Should().Be(AppTheme.Dark);
            main.Right.CompactRows.Should().BeTrue();
            main.Right.ShowAttributes.Should().BeTrue();
            main.Right.FontFamily.Should().Be("Cascadia Mono");
            main.Right.FontSize.Should().Be(15);
            settings.LanguageNeedsRestart.Should().BeTrue();
            settings.Save();
        }

        using var second = Create();
        second.Theme.Should().Be(AppTheme.Dark);
        second.Left.CompactRows.Should().BeTrue();
        second.Left.FontFamily.Should().Be("Cascadia Mono");
        second.Left.FontSize.Should().Be(15);
        second.Settings.Editor.Should().Be(@"C:\Tools\code.cmd");
        second.Settings.Language.Should().Be("en-US");
        second.Settings.CopyParallelism.Should().Be(4);
        new SettingsViewModel(second).LanguageNeedsRestart.Should().BeFalse("the app started with this language");
    }

    [Fact]
    public void Opening_the_settings_changes_nothing()
    {
        using var main = Create();
        main.SetFont("Consolas", 14);

        var settings = new SettingsViewModel(main);

        settings.PanelFontSize.Should().Be(14);
        main.Left.FontSize.Should().Be(14, "copying the current values in must not apply a half-built state");
        main.Left.FontFamily.Should().Be("Consolas");
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_blanks_fall_back()
    {
        using var main = Create();
        var settings = new SettingsViewModel(main) { PanelFontSize = 40, CopyParallelism = 99, Editor = "  ", PanelFont = "" };

        main.Left.FontSize.Should().Be(MainViewModel.MaxFontSize);
        main.Settings.CopyParallelism.Should().Be(SettingsViewModel.MaxParallelism);
        main.Settings.Editor.Should().Be("notepad.exe");
        main.Left.FontFamily.Should().Be(AppSettings.DefaultPanelFont);
    }
}
