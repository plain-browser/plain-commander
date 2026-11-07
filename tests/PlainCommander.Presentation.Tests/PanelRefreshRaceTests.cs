using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class PanelRefreshRaceTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly SlowDisk _disk = new();
    private readonly PanelViewModel _panel;

    public PanelRefreshRaceTests()
    {
        _panel = new PanelViewModel(_disk, new RecordingLauncher()) { AutoRefreshDelay = TimeSpan.FromMilliseconds(10) };
    }

    public void Dispose()
    {
        _panel.Dispose();
        _tree.Dispose();
    }

    [Fact]
    public async Task Regression_change_while_the_panel_is_loading_is_not_lost()
    {
        // The listing has read the folder, a file appears, the watcher fires, and only then does the listing finish.
        // The change event arrived while IsLoading was true and was dropped, so the new file stayed invisible until
        // the next change.
        _tree.File("old.txt");
        await _panel.NavigateAsync(_tree.Root);
        _disk.HoldAfterListing = new TaskCompletionSource();

        var refreshing = _panel.RefreshAsync();
        await _disk.ListingDone.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _panel.IsLoading.Should().BeTrue();
        _tree.File("new.txt");
        _disk.RaiseChanged();
        await Task.Delay(100, TestContext.Current.CancellationToken); // longer than the debounce: the event is handled while still loading
        _disk.HoldAfterListing.SetResult();
        await refreshing;

        await WaitForAsync(() => _panel.Items.Any(i => i.Name == "new.txt"));
        _panel.Items.Select(i => i.Name).Should().Contain(["old.txt", "new.txt"]);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The real disk, except that a listing can be held back after it has read the folder.</summary>
    private sealed class SlowDisk : IFileSystem
    {
        private readonly LocalFileSystem _inner = new();
        private Action? _changed;

        public TaskCompletionSource? HoldAfterListing { get; set; }

        public TaskCompletionSource ListingDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RaiseChanged() => _changed?.Invoke();

        public async IAsyncEnumerable<FileEntry> ListAsync(string path, bool includeHidden, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var entry in _inner.ListAsync(path, includeHidden, cancellationToken))
            {
                yield return entry;
            }

            if (HoldAfterListing is { } hold)
            {
                ListingDone.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken);
            }
        }

        public IReadOnlyList<DriveEntry> GetDrives() => _inner.GetDrives();

        public DriveEntry? GetDrive(string root) => _inner.GetDrive(root);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public string? GetParent(string path) => _inner.GetParent(path);

        public IDisposable? Watch(string path, Action onChanged)
        {
            _changed = onChanged;
            return null;
        }
    }
}
