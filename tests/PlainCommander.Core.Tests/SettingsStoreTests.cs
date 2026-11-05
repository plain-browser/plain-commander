using AwesomeAssertions;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-settings-").FullName;

    private string FilePath => Path.Combine(_dir, "nested", "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var settings = new SettingsStore(FilePath).Load();
        settings.Left.Path.Should().BeEmpty();
        settings.Left.Sort.Should().Be(new SortSpec());
    }

    [Fact]
    public void Round_trips_everything_that_matters()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings
        {
            Left = new PanelSettings { Path = @"C:\Users", Sort = new SortSpec(SortColumn.Size, SortDirection.Descending), BriefView = true },
            Right = new PanelSettings { Path = @"D:\" },
            ActivePanel = PanelSide.Right,
            ShowHidden = true,
            Window = new WindowSettings { X = 10, Y = 20, Width = 1400, Height = 900, IsMaximized = true },
        });

        var loaded = store.Load();

        loaded.Left.Path.Should().Be(@"C:\Users");
        loaded.Left.Sort.Should().Be(new SortSpec(SortColumn.Size, SortDirection.Descending));
        loaded.Left.BriefView.Should().BeTrue();
        loaded.Right.Path.Should().Be(@"D:\");
        loaded.ActivePanel.Should().Be(PanelSide.Right);
        loaded.ShowHidden.Should().BeTrue();
        loaded.Window.Should().BeEquivalentTo(new WindowSettings { X = 10, Y = 20, Width = 1400, Height = 900, IsMaximized = true });
        File.ReadAllText(FilePath).Should().Contain("\"Size\"", "enums are stored as readable strings");
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_is_kept_aside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ not json");

        var settings = new SettingsStore(FilePath).Load();

        settings.Left.Path.Should().BeEmpty();
        File.Exists(FilePath + ".bad").Should().BeTrue();
        File.Exists(FilePath).Should().BeFalse();
    }
}
