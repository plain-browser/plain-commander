using AwesomeAssertions;
using PlainCommander.Core.Details;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>The detail column (properties read from files), sorting and filtering by it, folder sizes in the background.</summary>
public sealed class DetailColumnTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDetails _details = new();
    private readonly PanelViewModel _panel;

    public DetailColumnTests()
    {
        _panel = new PanelViewModel(new LocalFileSystem(), new RecordingLauncher()) { DetailsProvider = _details };
        _tree.File("big.jpg", 10);
        _tree.File("small.jpg", 10);
        _tree.File("notes.txt", 10);
        _tree.File("song.mp3", 10);
        _details.Values["big.jpg"] = new FileDetails(4000, 3000, Taken: new DateTimeOffset(2024, 7, 1, 12, 0, 0, TimeSpan.Zero));
        _details.Values["small.jpg"] = new FileDetails(640, 480, Taken: new DateTimeOffset(2023, 1, 1, 12, 0, 0, TimeSpan.Zero));
        _details.Values["song.mp3"] = new FileDetails(Duration: TimeSpan.FromMinutes(4));
    }

    public void Dispose()
    {
        _panel.Dispose();
        _tree.Dispose();
    }

    private string[] Names => _panel.Items.Skip(1).Select(i => i.Name).ToArray();

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        condition().Should().BeTrue();
    }

    [Fact]
    public async Task Rows_read_their_properties_when_shown_and_only_for_the_chosen_column()
    {
        await _panel.NavigateAsync(_tree.Root);
        _panel.Items.Select(i => i.DetailText).Should().AllBe(string.Empty, "no detail column, nothing read");
        _details.Reads.Should().Be(0);

        _panel.Detail = DetailColumn.Dimensions;
        var big = _panel.Items.Single(i => i.Name == "big.jpg");
        _ = big.DetailText; // the row is shown

        await Until(() => big.DetailText == "4000 × 3000");
        _panel.DetailColumnTitle.Should().Be(Texts.DetailColumn(DetailColumn.Dimensions));
    }

    [Fact]
    public async Task Sorting_by_the_column_reads_every_file_and_puts_files_without_a_value_last()
    {
        await _panel.NavigateAsync(_tree.Root);
        _panel.Detail = DetailColumn.Dimensions;

        await _panel.SortByAsync(SortColumn.Detail);
        Names.Should().Equal("small.jpg", "big.jpg", "notes.txt", "song.mp3");

        await _panel.SortByAsync(SortColumn.Detail);
        Names.Should().Equal(["big.jpg", "small.jpg", "notes.txt", "song.mp3"], "descending keeps the empty ones last");

        await _panel.RefreshAsync();
        Names.Should().Equal(["big.jpg", "small.jpg", "notes.txt", "song.mp3"], "a fresh listing sorts the same way");
    }

    [Fact]
    public async Task The_filter_finds_photos_by_the_date_they_were_taken_and_recordings_by_length()
    {
        await _panel.NavigateAsync(_tree.Root);

        await _panel.SetFilterAsync(new PanelFilter { DateField = FilterDateField.Taken, DatePreset = FilterDatePreset.Range, From = new DateOnly(2024, 1, 1), To = new DateOnly(2024, 12, 31) });
        Names.Should().Equal("big.jpg");

        await _panel.SetFilterAsync(new PanelFilter { MinDuration = TimeSpan.FromMinutes(3) });
        Names.Should().Equal("song.mp3");
    }

    [Fact]
    public async Task Turning_the_column_off_while_sorted_by_it_sorts_by_name()
    {
        await _panel.NavigateAsync(_tree.Root);
        _panel.Detail = DetailColumn.Dimensions;
        await _panel.SortByAsync(SortColumn.Detail);

        _panel.Detail = DetailColumn.None;

        await Until(() => Names.SequenceEqual(["big.jpg", "notes.txt", "small.jpg", "song.mp3"]));
        _panel.Sort.Column.Should().Be(SortColumn.Name);
    }

    [Fact]
    public async Task Folder_sizes_are_measured_in_the_background_and_sort_folders_by_size()
    {
        _tree.File("few/a.bin", 10);
        _tree.File("many/a.bin", 5000);
        _tree.File("many/deep/b.bin", 5000);
        await _panel.SortByAsync(SortColumn.Size);
        _panel.FolderSizes = true;

        await _panel.NavigateAsync(_tree.Root);

        await Until(() => _panel.Items.Where(i => i.IsDirectory && !i.Entry.IsParentLink).All(i => i.DirectorySize is not null));
        _panel.Items.Single(i => i.Name == "many").DirectorySize.Should().Be(10000);
        await Until(() => _panel.Items[1].Name == "few");
        _panel.Items.Select(i => i.Name).Take(3).Should().Equal("..", "few", "many");
    }

    [Fact]
    public async Task Each_tab_keeps_its_own_column_and_the_settings_remember_it()
    {
        await _panel.NavigateAsync(_tree.Root);
        _panel.NewTab();
        _panel.Detail = DetailColumn.Duration;

        await _panel.SwitchTabAsync(0);
        _panel.Detail.Should().Be(DetailColumn.None);
        await _panel.SwitchTabAsync(1);
        _panel.Detail.Should().Be(DetailColumn.Duration);

        var saved = _panel.CaptureSettings();
        saved.Detail.Should().Be(DetailColumn.Duration);
        saved.Tabs.Select(t => t.Detail).Should().Equal(DetailColumn.None, DetailColumn.Duration);
    }

    private sealed class FakeDetails : IFileDetailsProvider
    {
        private int _reads;

        public Dictionary<string, FileDetails> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Reads => Volatile.Read(ref _reads);

        public FileDetails Read(string path)
        {
            Interlocked.Increment(ref _reads);
            return Values.GetValueOrDefault(Path.GetFileName(path)) ?? FileDetails.None;
        }
    }
}
