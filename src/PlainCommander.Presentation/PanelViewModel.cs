using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Details;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using PlainCommander.Core.Network;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Settings;
using PlainCommander.Core.Sorting;
using Serilog;

namespace PlainCommander.Presentation;

/// <summary>
/// State and behaviour of one file panel. Methods are called on the UI thread; all disk work runs
/// on the thread pool and comes back in one step, so a 100k-entry folder costs one UI update.
/// </summary>
public sealed partial class PanelViewModel : ObservableObject, IDisposable
{
    private readonly IFileSystem _fs;
    private readonly ILauncher _launcher;
    private readonly IIconProvider? _icons;
    private readonly Dictionary<string, string> _lastPathPerDrive = new(StringComparer.OrdinalIgnoreCase);
    private readonly SynchronizationContext? _ui;
    private CancellationTokenSource? _listing;
    private List<FileEntry>? _results;
    private string _resultsWhat = string.Empty;
    private CancellationTokenSource? _debounce;
    private IDisposable? _watcher;
    private volatile bool _changedWhileLoading;

    public PanelViewModel(IFileSystem fs, ILauncher launcher, IIconProvider? icons = null)
    {
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
        _fs = fs;
        _launcher = launcher;
        _icons = icons;
        _ui = SynchronizationContext.Current;
    }

    /// <summary>Raised when the cursor should move to an index (after navigation, sort, quick search).</summary>
    public event EventHandler<int>? FocusRequested;

    [ObservableProperty]
    public partial string Path { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; internal set; } = string.Empty;

    /// <summary>True while something in the list is marked; the footer then shows the selection in the accent.</summary>
    [ObservableProperty]
    public partial bool HasMarks { get; private set; }

    [ObservableProperty]
    public partial string DriveSummary { get; private set; } = string.Empty;

    /// <summary>Footer: "157 GB wolne z 199 GB" without the volume label.</summary>
    [ObservableProperty]
    public partial string DriveFreeText { get; private set; } = string.Empty;

    /// <summary>Footer usage bar, 0–1.</summary>
    [ObservableProperty]
    public partial double DriveUsedFraction { get; private set; }

    /// <summary>The drive the panel is on, for the drive button (null until the first listing).</summary>
    [ObservableProperty]
    public partial DriveEntry? CurrentDrive { get; private set; }

    /// <summary>Breadcrumb pieces of <see cref="Path"/>; see <see cref="PathSegment.Split"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PathSegment> PathSegments { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortGlyph), nameof(ExtensionSortGlyph), nameof(SizeSortGlyph), nameof(DateSortGlyph), nameof(AttributesSortGlyph), nameof(DateColumnTitle), nameof(DetailSortGlyph))]
    public partial SortSpec Sort { get; private set; } = new();

    /// <summary>Sorted by creation date: the date column shows it too, so the order can be seen.</summary>
    public bool ShowsCreated => Sort.Column == SortColumn.Created;

    /// <summary>"Data" or "Utworzono", following <see cref="ShowsCreated"/>.</summary>
    public string DateColumnTitle => Texts.DateColumn(ShowsCreated);

    partial void OnSortChanged(SortSpec value) => PrepareItems(Items);

    partial void OnItemsChanged(IReadOnlyList<FileItem> value)
    {
        _detailQueue.Clear(); // rows of the previous list no longer need their properties
        PrepareItems(value);
    }

    /// <summary>Rows learn which date and which detail the columns show, and how to ask for their properties.</summary>
    private void PrepareItems(IReadOnlyList<FileItem> items)
    {
        var created = ShowsCreated;
        var detail = Detail;
        foreach (var item in items)
        {
            item.ShowCreated = created;
            item.DetailKind = detail;
            item.RequestDetails ??= QueueDetails;
        }
    }

    /// <summary>Reads file properties for the detail column, sorting and filtering; null shows nothing (tests, Linux).</summary>
    public IFileDetailsProvider? DetailsProvider { get; set; }

    /// <summary>The extra column: dimensions, photo date, duration or version; None hides it. Kept per tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailColumnTitle), nameof(DetailSortGlyph), nameof(HasDetailColumn))]
    public partial DetailColumn Detail { get; set; }

    public bool HasDetailColumn => Detail != DetailColumn.None;

    public string DetailColumnTitle => Texts.DetailColumn(Detail);

    public string DetailSortGlyph => Glyph(SortColumn.Detail);

    private bool _restoringView;

    /// <summary>A tab's sort, view and detail column, set together before its folder is listed (no re-sorting of the old list).</summary>
    private void RestoreView(PanelTab tab)
    {
        _restoringView = true;
        try
        {
            Detail = tab.Detail;
            Sort = tab.Sort.Column == SortColumn.Detail && tab.Detail == DetailColumn.None ? new SortSpec() : tab.Sort;
            BriefView = tab.BriefView;
        }
        finally
        {
            _restoringView = false;
        }
    }

    partial void OnDetailChanged(DetailColumn value)
    {
        PrepareItems(Items);
        if (_restoringView)
        {
            return;
        }

        if (value == DetailColumn.None && Sort.Column == SortColumn.Detail)
        {
            _ = ApplySortAsync(new SortSpec()); // the column it was sorted by is gone
        }
        else if (Sort.Column == SortColumn.Detail)
        {
            _ = ApplySortAsync(Sort);
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentStack<FileItem> _detailQueue = new();
    private int _detailWorker;

    /// <summary>Only files on disk have properties to read; on a server or in an archive the column stays empty.</summary>
    private bool CanReadDetails(FileItem item) =>
        DetailsProvider is not null && !item.IsDirectory && !item.Entry.IsParentLink
        && !RemotePath.IsRemote(item.Entry.FullPath) && ArchiveFileSystem.Locate(item.Entry.FullPath) is null;

    /// <summary>A row became visible: its properties are read in the background, the latest rows first.</summary>
    private void QueueDetails(FileItem item)
    {
        if (!CanReadDetails(item))
        {
            return;
        }

        _detailQueue.Push(item);
        if (Interlocked.Exchange(ref _detailWorker, 1) == 0)
        {
            _ = Task.Run(ReadQueuedDetails);
        }
    }

    private void ReadQueuedDetails()
    {
        while (true)
        {
            while (_detailQueue.TryPop(out var item))
            {
                if (item.Details is not null || DetailsProvider is not { } provider)
                {
                    continue;
                }

                var details = provider.Read(item.Entry.FullPath);
                PostToUi(() => item.Details = details);
            }

            Volatile.Write(ref _detailWorker, 0);
            if (_detailQueue.IsEmpty || Interlocked.Exchange(ref _detailWorker, 1) != 0)
            {
                return; // nothing came in after we stopped, or another worker took over
            }
        }
    }

    /// <summary>
    /// Sorting or filtering by a property needs it for every file: read on a worker thread, assigned by the caller's
    /// thread (rows on screen must change on the UI thread).
    /// </summary>
    private Dictionary<FileItem, FileDetails> ReadAllDetails(IEnumerable<FileItem> items, CancellationToken token, Action<int, int>? progress = null)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<FileItem, FileDetails>();
        if (DetailsProvider is not { } provider)
        {
            return [];
        }

        // Property handlers mostly wait for the disk, so several files are read at once.
        var todo = items.Where(i => i.Details is null && CanReadDetails(i)).ToList();
        var done = 0;
        Parallel.ForEach(
            todo,
            new ParallelOptions { MaxDegreeOfParallelism = DetailReaders, CancellationToken = token },
            item =>
            {
                result[item] = provider.Read(item.Entry.FullPath);
                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0)
                {
                    progress?.Invoke(n, todo.Count);
                }
            });
        return new Dictionary<FileItem, FileDetails>(result);
    }

    private const int DetailReaders = 8;

    private async Task EnsureDetailsAsync(IReadOnlyList<FileItem> items)
    {
        if (DetailsProvider is null || items.All(i => i.Details is not null || !CanReadDetails(i)))
        {
            return;
        }

        Status = Texts.ReadingDetails(0, 0);
        var watch = Stopwatch.StartNew();
        var read = await Task.Run(() => ReadAllDetails(items, CancellationToken.None, (done, total) => PostToUi(() => Status = Texts.ReadingDetails(done, total))));
        Log.Debug("Read properties of {Count} files in {Elapsed} ms", read.Count, watch.ElapsedMilliseconds);
        foreach (var (item, details) in read)
        {
            item.Details = details;
        }

        UpdateStatus();
    }

    /// <summary>Folder sizes are measured after a folder opens (local, non-network drives). Set from the settings.</summary>
    [ObservableProperty]
    public partial bool FolderSizes { get; set; }

    partial void OnFolderSizesChanged(bool value)
    {
        if (value)
        {
            StartFolderSizes();
        }
        else
        {
            _sizing?.Cancel();
        }
    }

    private CancellationTokenSource? _sizing;

    /// <summary>Measures every folder of the list one after another; a new listing stops it.</summary>
    private void StartFolderSizes()
    {
        _sizing?.Cancel();
        _sizing = null;
        if (!FolderSizes || IsRemote || IsInArchive || _results is not null || CurrentDrive is { Type: DriveType.Network } || IsNetworkList || NetworkPath.UncServer(Path) is not null)
        {
            return;
        }

        var folders = Items.Where(static i => i.IsDirectory && !i.Entry.IsParentLink && i.DirectorySize is null).ToList();
        if (folders.Count == 0)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _sizing = cts;
        var token = cts.Token;
        _ = Task.Run(
            async () =>
            {
                foreach (var folder in folders)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        var size = await DirectorySize.CalculateAsync(folder.Entry.FullPath, token).ConfigureAwait(false);
                        PostToUi(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                folder.DirectorySize = size.Bytes;
                                UpdateStatus();
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Debug(ex, "Cannot measure {Path}", folder.Entry.FullPath);
                    }
                }

                PostToUi(() =>
                {
                    if (!token.IsCancellationRequested && Sort.Column == SortColumn.Size)
                    {
                        _ = ApplySortAsync(Sort); // the folders take their places by size
                    }
                });
            },
            token);
    }

    [ObservableProperty]
    public partial bool ShowHidden { get; private set; }

    [ObservableProperty]
    public partial bool BriefView { get; set; }

    /// <summary>Window-wide view options, kept in sync on both panels by <see cref="MainViewModel"/>.</summary>
    [ObservableProperty]
    public partial bool CompactRows { get; set; }

    [ObservableProperty]
    public partial bool ShowAttributes { get; set; }

    [ObservableProperty]
    public partial string FontFamily { get; set; } = AppSettings.DefaultPanelFont;

    [ObservableProperty]
    public partial double FontSize { get; set; } = AppSettings.DefaultPanelFontSize;

    [ObservableProperty]
    public partial IReadOnlyList<FileItem> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickSearchActive), nameof(QuickSearchSummary))]
    public partial string QuickSearchText { get; private set; } = string.Empty;

    public bool IsQuickSearchActive => QuickSearchText.Length > 0;

    /// <summary>How many rows match the typed text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickSearchSummary))]
    public partial int QuickSearchMatches { get; private set; }

    /// <summary>The search box: the text and how many match ("rap · 3").</summary>
    public string QuickSearchSummary => QuickSearchText.Length == 0 ? string.Empty : Texts.QuickSearchSummary(QuickSearchText, QuickSearchMatches);

    /// <summary>The name quick search looks at; ".." is never a match.</summary>
    private static string SearchName(FileItem item) => item.Entry.IsParentLink ? string.Empty : item.Name;

    public ObservableCollection<DriveItem> Drives { get; } = [];

    public const int HistoryLimit = 30;

    private readonly List<string> _history = [];

    /// <summary>Folders this panel visited, most recent last; a revisit moves the entry to the end.</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>Cursor position; the view writes it back when the user moves the selection.</summary>
    public int CurrentIndex
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                CurrentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    = -1;

    /// <summary>The cursor moved to another row (the quick view follows it).</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>
    /// Ctrl+S: only what passes the filter stays in the list (name part or masks, extensions, dates, sizes; folders
    /// stay unless "files only"). Null: no filter. Kept while the panel stays in the folder.
    /// </summary>
    [ObservableProperty]
    public partial PanelFilter? Filter { get; private set; }

    /// <summary>Ctrl+B: the list shows every file below the folder, with relative names.</summary>
    public bool IsFlat => _results is not null && _resultsWhat == FlatMarker;

    private const string FlatMarker = "\0flat";

    public FileItem? CurrentItem => CurrentIndex >= 0 && CurrentIndex < Items.Count ? Items[CurrentIndex] : null;

    /// <summary>Delay between the last change on disk and the automatic refresh.</summary>
    public TimeSpan AutoRefreshDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    public IReadOnlyList<FileItem> MarkedItems => Items.Where(static i => i.IsMarked).ToArray();

    /// <summary>What F5/F6/F8 act on: the marked items, or else the item under the cursor (never "..").</summary>
    public IReadOnlyList<FileItem> OperationSources
    {
        get
        {
            if (IsNetworkList)
            {
                return []; // computers and share lists are not files to copy or delete
            }

            var marked = MarkedItems;
            if (marked.Count > 0)
            {
                return marked;
            }

            return CurrentItem is { Entry.IsParentLink: false } current ? [current] : [];
        }
    }

    public string? CurrentRoot => string.IsNullOrEmpty(Path) || NetworkPath.IsVirtual(Path) ? null : RemotePath.Parse(Path) is { } remote ? remote.Root : System.IO.Path.GetPathRoot(Path);

    /// <summary>The panel shows a folder on an FTP/SFTP/S3 server.</summary>
    public bool IsRemote => RemotePath.IsRemote(Path);

    /// <summary>Remote paths are kept as typed (normalized); disk paths go through the exact-name rules.</summary>
    private static string FullPathOf(string path) =>
        NetworkPath.IsVirtual(path) ? NetworkPath.Normalize(path)
        : NetworkPath.AsServerPlace(path) ?? RemotePath.Parse(path)?.FullPath
        ?? (NetworkPath.UncServer(path) is not null && !OperatingSystem.IsWindows() ? path : System.IO.Path.TrimEndingDirectorySeparator(ExactPath.Full(path))); // UNC means nothing off Windows (tests)

    /// <summary>The panel lists computers or a computer's shares: places, not folders, so nothing can be written there.</summary>
    public bool IsNetworkList => NetworkPath.IsVirtual(Path);

    /// <summary>The panel shows the inside of an archive: listing works, writing does not (yet).</summary>
    public bool IsInArchive => ArchiveFileSystem.Locate(Path) is not null;

    /// <summary>Set while the panel shows search results (Alt+F7 → "to panel") instead of a folder's content.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingResults))]
    public partial string? ResultsTitle { get; private set; }

    public bool IsShowingResults => ResultsTitle is not null;

    /// <summary>"C:" for the drive button; the whole root for UNC and "/" roots.</summary>
    public string DriveLabel => PathSegments.Count > 0 ? PathSegments[0].Name : string.Empty;

    /// <summary>The breadcrumb next to the drive button: the segments below the root.</summary>
    public IReadOnlyList<PathSegment> Breadcrumbs => PathSegments.Count > 1 ? PathSegments.Skip(1).ToArray() : [];

    public string NameSortGlyph => Glyph(SortColumn.Name);

    public string ExtensionSortGlyph => Glyph(SortColumn.Extension);

    public string SizeSortGlyph => Glyph(SortColumn.Size);

    public string DateSortGlyph => ShowsCreated ? Glyph(SortColumn.Created) : Glyph(SortColumn.Modified);

    public string AttributesSortGlyph => Glyph(SortColumn.Attributes);

    public Task<bool> NavigateAsync(string path, string? focusName = null) => NavigateAsync(path, focusName, keepState: false);

    private async Task<bool> NavigateAsync(string path, string? focusName, bool keepState)
    {
        _listing?.Cancel();
        _sizing?.Cancel();
        var cts = new CancellationTokenSource();
        _listing = cts;
        var token = cts.Token;

        IsLoading = true;
        Status = Texts.Loading;
        var watch = Stopwatch.StartNew();
        try
        {
            var full = FullPathOf(path);
            var sort = Sort;
            var showHidden = ShowHidden;
            var filter = keepState || string.Equals(full, Path, StringComparison.Ordinal) ? Filter : null;
            var detail = Detail;
            var (items, drive) = await Task.Run(() => LoadAsync(full, sort, detail, filter, showHidden, token), token);
            token.ThrowIfCancellationRequested();
            items = ApplyFilter(items, filter);
            Filter = filter;

            var previousIndex = CurrentIndex;
            if (keepState)
            {
                CarryOverState(Items, items);
            }

            if (!string.Equals(Path, full, StringComparison.Ordinal) || _watcher is null)
            {
                StartWatching(full);
            }

            if (keepState && string.Equals(Path, full, StringComparison.Ordinal) && SameListing(Items, items))
            {
                // A watcher event with nothing new (a WSL share after each listing, a file touched and restored):
                // the rows stay, so the panel does not flicker and neither the cursor nor the keyboard focus moves.
                UpdateDriveState(drive);
                UpdateStatus();
                if (focusName is not null && IndexOfName(focusName) is var at && at >= 0 && at != CurrentIndex)
                {
                    MoveCursor(at); // asked to land on a file (just written): that still happens
                }

                Log.Verbose("Unchanged {Path}: {Count} entries kept", full, items.Length);
                return true;
            }

            if (!keepState)
            {
                Remember(full);
            }

            _results = null;
            ResultsTitle = null;
            OnPropertyChanged(nameof(IsFlat));
            Path = full;
            Tabs[ActiveTabIndex].MoveTo(full);
            PathSegments = PathSegment.Split(full);
            OnPropertyChanged(nameof(DriveLabel));
            OnPropertyChanged(nameof(Breadcrumbs));
            OnPropertyChanged(nameof(IsInArchive));
            OnPropertyChanged(nameof(IsRemote));
            Items = items;
            RememberDrivePath(full);
            UpdateDriveState(drive);
            UpdateStatus();
            ClearQuickSearch();
            var index = focusName is null ? -1 : IndexOfName(focusName);
            if (index < 0 && keepState)
            {
                index = previousIndex; // the item went away (deleted, moved): stay at the same place
            }

            MoveCursor(Math.Max(index, 0));
            StartFolderSizes();
            Log.Debug("Listed {Path}: {Count} entries in {Elapsed} ms", full, items.Length, watch.ElapsedMilliseconds);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (RemoteException ex) when (ex.IsCancelled)
        {
            Status = Texts.LoginCancelled;
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.Warning(ex, "Cannot list {Path}", path);
            Status = Texts.CannotOpen(path, ex);
            return false;
        }
        finally
        {
            if (ReferenceEquals(_listing, cts))
            {
                _listing = null;
                IsLoading = false;
                if (_changedWhileLoading)
                {
                    _changedWhileLoading = false;
                    PostToUi(() => _ = RefreshAsync());
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Tries <paramref name="preferred"/>, then each of its parents, then <paramref name="fallback"/>.</summary>
    public async Task NavigateWithFallbackAsync(string? preferred, string fallback)
    {
        for (var p = preferred; !string.IsNullOrEmpty(p); p = _fs.GetParent(p))
        {
            if (_fs.DirectoryExists(p) && await NavigateAsync(p))
            {
                return;
            }
        }

        await NavigateAsync(fallback);
    }

    /// <summary>
    /// Re-reads the folder, keeping marks, measured sizes and the cursor (or its position). When the folder itself is
    /// gone (deleted or moved by another program, the other panel or an operation), the panel goes to the nearest
    /// folder that still exists instead of keeping the stale list, where F7 would quietly recreate the deleted path.
    /// </summary>
    public async Task<bool> RefreshAsync(string? focusName = null)
    {
        if (string.IsNullOrEmpty(Path))
        {
            return false;
        }

        if (_results is not null)
        {
            return await RefreshResultsAsync(focusName ?? CurrentItem?.Name);
        }

        if (await NavigateAsync(Path, focusName ?? CurrentItem?.Name, keepState: true))
        {
            return true;
        }

        if (IsLoading || !IsFolderGone)
        {
            return false; // another navigation took over, or the folder is there but unreadable: keep the list
        }

        await NavigateWithFallbackAsync(_fs.GetParent(Path), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return true;
    }

    /// <summary>The local folder or archive folder shown no longer exists. Servers and the network list are never "gone".</summary>
    public bool IsFolderGone =>
        _results is null && !string.IsNullOrEmpty(Path) && !IsRemote && !IsNetworkList && !_fs.DirectoryExists(Path);

    /// <summary>Re-reads the folder after a command or operation; see <see cref="RefreshAsync"/>.</summary>
    public Task<bool> RefreshAfterChangeAsync(string? focusName = null) => RefreshAsync(focusName);

    /// <summary>
    /// Shows found items in this panel like a folder: names relative to <paramref name="root"/>, operations work on
    /// them as usual, ".." or Backspace returns to the folder that was searched.
    /// </summary>
    public void ShowResults(string what, string root, IEnumerable<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _listing?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        var full = ExactPath.Full(root);
        _results = entries.ToList();
        Path = full;
        Tabs[ActiveTabIndex].MoveTo(full);
        PathSegments = PathSegment.Split(full);
        OnPropertyChanged(nameof(DriveLabel));
        OnPropertyChanged(nameof(Breadcrumbs));
        OnPropertyChanged(nameof(IsInArchive));
        _resultsWhat = what;
        ResultsTitle = what == FlatMarker ? Texts.FlatTitle(_results.Count) : Texts.ResultsTitle(what, _results.Count);
        OnPropertyChanged(nameof(IsFlat));
        Items = ApplyFilter(BuildResultItems(full, _results, Sort), Filter);
        UpdateStatus();
        ClearQuickSearch();
        MoveCursor(Items.Count > 1 ? 1 : 0);
    }

    /// <summary>Ctrl+S: sets (or with null/empty, removes) the filter and re-reads the list.</summary>
    public Task<bool> SetFilterAsync(PanelFilter? filter)
    {
        Filter = filter is null || filter.IsEmpty ? null : filter;
        return RefreshAsync();
    }

    /// <summary>The plain text filter: a name part or masks.</summary>
    public Task<bool> SetFilterAsync(string? filter) => SetFilterAsync(PanelFilter.FromText(filter ?? string.Empty));

    /// <summary>
    /// Ctrl+B: every file below the current folder in one list (names relative to it), or back to the folder when
    /// the list is already flat. Disk only. Links to folders are not followed. False when it cannot be shown here.
    /// </summary>
    public async Task<bool> ToggleFlatAsync()
    {
        if (IsFlat)
        {
            return await CloseResultsAsync();
        }

        if (IsRemote || IsInArchive || _results is not null || string.IsNullOrEmpty(Path))
        {
            return false;
        }

        var root = Path;
        var showHidden = ShowHidden;
        Status = Texts.Loading;
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() => CollectFiles(root, showHidden));
            if (!string.Equals(root, Path, StringComparison.Ordinal))
            {
                return false; // the panel moved on meanwhile
            }

            ShowResults(FlatMarker, root, entries);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the folder went away (or became unreadable) since it was listed
            Log.Warning(ex, "Cannot list the files below {Path}", root);
            Status = Texts.CannotOpen(root, ex);
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static List<FileEntry> CollectFiles(string root, bool showHidden)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | (showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System),
        };
        return new DirectoryInfo(ExactPath.Native(root)).EnumerateFiles("*", options).Select(LocalFileSystem.ToEntry).ToList();
    }

    private FileItem[] ApplyFilter(FileItem[] items, PanelFilter? filter)
    {
        if (filter is null)
        {
            return items;
        }

        if (filter.NeedsDetails)
        {
            // Folder listings arrive with their properties read; search results and the flat view read them here.
            foreach (var (item, details) in ReadAllDetails(items, CancellationToken.None))
            {
                item.Details = details;
            }
        }

        var matches = filter.DetailMatcher(DateTime.Now);
        return items.Where(i => matches(i.Entry, i.Details)).ToArray();
    }

    /// <summary>Leaves the results and shows the searched folder again.</summary>
    public Task<bool> CloseResultsAsync() => NavigateAsync(Path);

    /// <summary>Keeps the results in step with renames done by Shift+F6 or Ctrl+M.</summary>
    public void RenameResults(IEnumerable<(string From, string To)> renames)
    {
        if (_results is null)
        {
            return;
        }

        // paths compare as the disk does: on a case-sensitive one "a.txt" and "A.txt" are two files
        var map = renames.ToDictionary(r => r.From, r => r.To, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (var i = 0; i < _results.Count; i++)
        {
            if (map.TryGetValue(_results[i].FullPath, out var to))
            {
                _results[i] = _results[i] with { FullPath = to, Name = System.IO.Path.GetFileName(to) };
            }
        }
    }

    private async Task<bool> RefreshResultsAsync(string? focusName)
    {
        var root = Path;
        var before = _results!;
        var still = await Task.Run(() => before.Where(e => ArchiveFileSystem.Locate(e.FullPath) is { IsRoot: false } || File.Exists(e.FullPath) || Directory.Exists(e.FullPath)).ToList());
        if (!ReferenceEquals(before, _results))
        {
            return false; // the panel moved on meanwhile
        }

        _results = still;
        var previousIndex = CurrentIndex;
        var items = ApplyFilter(BuildResultItems(root, still, Sort), Filter);
        CarryOverState(Items, items);
        Items = items;
        ResultsTitle = _resultsWhat == FlatMarker ? Texts.FlatTitle(still.Count) : Texts.ResultsTitle(_resultsWhat, still.Count);
        UpdateStatus();
        var index = focusName is null ? -1 : IndexOfName(focusName);
        MoveCursor(index >= 0 ? index : previousIndex);
        return true;
    }

    private FileItem[] BuildResultItems(string root, IEnumerable<FileEntry> entries, SortSpec sort)
    {
        var rows = entries
            .Select(e => e with { Name = System.IO.Path.GetRelativePath(root, e.FullPath) })
            .OrderBy(static e => e, new FileEntryComparer(sort))
            .Prepend(FileEntry.ParentLink(root))
            .Select(e => new FileItem(e, _icons))
            .ToArray();
        return rows;
    }

    /// <summary>The root of the current drive or server (the "go to root" button); false when already there.</summary>
    public Task<bool> GoRootAsync() =>
        CurrentRoot is { } root && !string.Equals(root.TrimEnd('\\', '/'), Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? NavigateAsync(root)
            : Task.FromResult(false);

    /// <summary>Backspace / Ctrl+PgUp: parent folder, with the cursor on the folder we came from.</summary>
    public Task<bool> GoUpAsync()
    {
        if (string.IsNullOrEmpty(Path))
        {
            return Task.FromResult(false);
        }

        if (_results is not null)
        {
            return CloseResultsAsync(); // ".." of the results is the folder that was searched
        }

        if (Core.Crypto.PceBrowse.OriginOf(Path) is { } pce && System.IO.Path.GetDirectoryName(pce) is { } pceFolder)
        {
            return NavigateAsync(pceFolder, System.IO.Path.GetFileName(pce)); // the browsed .pce container: back to where it lies
        }

        var parent = _fs.GetParent(Path);
        if (parent is null)
        {
            return Task.FromResult(false);
        }

        var trimmed = Path.TrimEnd('\\', '/');
        var from = IsRemote
            ? RemotePath.NameOf(Path)
            : trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..]; // the folder, share or computer we leave
        return NavigateAsync(parent, from);
    }

    public Task OpenCurrentAsync() => CurrentItem is { } item ? OpenAsync(item) : Task.CompletedTask;

    /// <summary>
    /// Set by the main view model: a .pce file on disk under Enter (decrypt it next to itself) or Ctrl+PgDn
    /// (browse the folder inside). Without it such a file is launched like any other.
    /// </summary>
    public Func<FileItem, bool, Task> EncryptedHandler { get; set; } = static (_, _) => Task.CompletedTask;

    /// <summary>
    /// Ctrl+PgDn: like Enter, but never launches a file. Any other file on disk is tried as an archive (an installer,
    /// a disk image); if it does not open, the panel stays where it was. PDF and office documents are not tried.
    /// </summary>
    public async Task EnterCurrentDirectoryAsync()
    {
        if (CurrentItem is not { } item || item.Entry.IsParentLink)
        {
            return;
        }

        if (item.IsDirectory || IsEnterableArchive(item.Entry))
        {
            await OpenAsync(item);
            return;
        }

        var file = item.Entry.FullPath;
        if (RemotePath.IsRemote(file) || ArchiveFileSystem.Locate(file) is not null)
        {
            return; // a file inside an archive or on a server
        }

        if (item.IsEncrypted && EncryptedHandler is { } handler)
        {
            await handler(item, true);
            return;
        }

        if (!ArchivePath.OpenExplicitly(file))
        {
            Status = Texts.DocumentNotArchive(item.Name);
            return;
        }

        if (!await NavigateAsync(file))
        {
            ArchivePath.ForgetExplicit(file); // the status line already says why
        }
    }

    /// <summary>An archive file on disk opens like a folder; one nested inside another archive is just a file.</summary>
    private static bool IsEnterableArchive(FileEntry entry) =>
        !entry.IsDirectory && !entry.IsParentLink && ArchivePath.IsArchiveName(entry.Name) && ArchiveFileSystem.Locate(entry.FullPath) is { IsRoot: true };

    public async Task OpenAsync(FileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var entry = item.Entry;
        if (entry.IsParentLink)
        {
            await GoUpAsync();
            return;
        }

        if (entry.IsDirectory || IsEnterableArchive(entry))
        {
            await NavigateAsync(entry.FullPath);
            return;
        }

        if (item.IsEncrypted && EncryptedHandler is { } handler && !RemotePath.IsRemote(entry.FullPath) && ArchiveFileSystem.Locate(entry.FullPath) is null)
        {
            await handler(item, false);
            return;
        }

        try
        {
            // A file inside an archive or on a server is first written to a temp file; the program sees a normal path.
            string path;
            if (RemotePath.IsRemote(entry.FullPath) && _fs is RemoteFileSystem remote)
            {
                Status = Texts.Downloading(entry.Name);
                path = await RemoteDownloadOperation.ToTempAsync(remote.Connections, entry.FullPath, CancellationToken.None);
                UpdateStatus();
            }
            else
            {
                var inArchive = ArchiveFileSystem.Locate(entry.FullPath) is { IsRoot: false };
                path = inArchive ? await Task.Run(() => ArchiveExtractOperation.ExtractToTemp(entry.FullPath)) : entry.FullPath;
            }

            _launcher.Open(path, System.IO.Path.GetDirectoryName(path) ?? Path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Cannot launch {Path}", entry.FullPath);
            Status = Texts.CannotLaunch(entry.Name, ex);
        }
    }

    public Task SortByAsync(SortColumn column) => ApplySortAsync(Sort.Toggle(column));

    public async Task ApplySortAsync(SortSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var snapshot = Items;
        if (spec.Column == SortColumn.Detail)
        {
            if (Detail == DetailColumn.None)
            {
                return; // nothing to sort by
            }

            await EnsureDetailsAsync(snapshot);
        }

        Sort = spec;
        var current = CurrentItem;
        var comparer = new ItemComparer(spec, Detail);
        var sorted = await Task.Run(() =>
        {
            var copy = snapshot.ToArray();
            Array.Sort(copy, comparer);
            return copy;
        });

        if (!ReferenceEquals(snapshot, Items))
        {
            return; // a navigation replaced the list meanwhile; it already used the new sort
        }

        Items = sorted;
        MoveCursor(current is null ? 0 : Array.IndexOf(sorted, current));
    }

    public Task SetShowHiddenAsync(bool show)
    {
        if (ShowHidden == show)
        {
            return Task.CompletedTask;
        }

        ShowHidden = show;
        return RefreshAsync();
    }

    /// <summary>Finds the synced OneDrive folders; they are listed after the drives. Set by the main view model.</summary>
    public Func<IReadOnlyList<CloudFolder>>? CloudProvider { get; set; }

    /// <summary>Finds the WSL distributions; they come after OneDrive, each as the share \\wsl.localhost\name. Set by the main view model.</summary>
    public Func<IReadOnlyList<string>>? WslProvider { get; set; }

    public async Task RefreshDrivesAsync()
    {
        var drives = await Task.Run(_fs.GetDrives);
        var cloud = CloudProvider is { } provider ? await Task.Run(provider) : [];
        var wsl = WslProvider is { } wslProvider ? await Task.Run(wslProvider) : [];
        Drives.Clear();
        foreach (var d in drives)
        {
            Drives.Add(new DriveItem(d));
        }

        foreach (var c in cloud)
        {
            Drives.Add(new DriveItem(new DriveEntry(c.Path, c.Name, DriveType.Fixed, 0, 0, true, c.Name)));
        }

        foreach (var distro in wsl)
        {
            Drives.Add(new DriveItem(new DriveEntry(WslDistros.Root(distro), distro, DriveType.Network, 0, 0, true)));
        }

        MarkCurrentDrive();
    }

    /// <summary>The other panel's path: choosing the drive it stands on lands in its folder.</summary>
    public Func<string?>? OtherPanelPath { get; set; }

    /// <summary>The drive for a letter typed in the Alt+F1/F2 menu ("d" → D:\), or null.</summary>
    public DriveItem? FindDrive(char letter) =>
        Drives.FirstOrDefault(d => d.Letter.Length == 1 && char.ToUpperInvariant(d.Letter[0]) == char.ToUpperInvariant(letter));

    /// <summary>
    /// Drive button / Alt+F1: the current drive goes to its root; another drive lands in the folder
    /// the other panel shows on it, else in the folder this panel last showed there, else its root.
    /// </summary>
    public Task<bool> ChangeDriveAsync(string root)
    {
        if (Drives.Any(d => d.IsCloud && string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase)))
        {
            return NavigateAsync(root); // OneDrive: always its top folder (the drives remember where you were)
        }

        if (string.Equals(CurrentRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return NavigateAsync(root);
        }

        if (OtherPanelPath?.Invoke() is { Length: > 0 } other
            && !RemotePath.IsRemote(other) && !NetworkPath.IsVirtual(other) && ArchiveFileSystem.Locate(other) is null
            && string.Equals(RootKey(System.IO.Path.GetPathRoot(other) ?? string.Empty), RootKey(root), StringComparison.OrdinalIgnoreCase)
            && _fs.DirectoryExists(other))
        {
            return NavigateAsync(other); // the other panel is on that drive: meet it in its folder
        }

        return _lastPathPerDrive.TryGetValue(RootKey(root), out var last) && _fs.DirectoryExists(last)
            ? NavigateAsync(last)
            : NavigateAsync(root);
    }

    /// <summary>"C:\" stays; a UNC root is remembered without its trailing backslash ("\\wsl.localhost\Ubuntu").</summary>
    private static string RootKey(string root) => root.Length > 3 ? root.TrimEnd('\\') : root;

    public bool QuickSearchType(char c)
    {
        var text = QuickSearchText + c;
        var index = QuickSearch.Find(Items, SearchName, text, Math.Max(CurrentIndex, 0));
        if (index < 0)
        {
            return false; // a key that matches nothing is ignored
        }

        QuickSearchText = text;
        QuickSearchMatches = QuickSearch.Count(Items, SearchName, text);
        MoveCursor(index);
        return true;
    }

    public void QuickSearchBackspace()
    {
        if (QuickSearchText.Length > 0)
        {
            QuickSearchText = QuickSearchText[..^1];
            QuickSearchMatches = QuickSearch.Count(Items, SearchName, QuickSearchText);
        }
    }

    /// <summary>
    /// Ctrl+S while typing a quick search: the list keeps only the matches (the text becomes the filter).
    /// False when no quick search is going on.
    /// </summary>
    public async Task<bool> FilterByQuickSearchAsync()
    {
        if (!IsQuickSearchActive)
        {
            return false;
        }

        var text = QuickSearchText;
        var current = CurrentItem?.Name;
        Filter = PanelFilter.FromText(text);
        ClearQuickSearch();
        await RefreshAsync(current);
        return true;
    }

    /// <summary>Up/Down while searching: previous/next item matching the same text.</summary>
    public void QuickSearchNext(bool forward)
    {
        if (!IsQuickSearchActive || Items.Count == 0)
        {
            return;
        }

        var index = QuickSearch.FindNext(Items, SearchName, QuickSearchText, CurrentIndex + (forward ? 1 : -1), forward);
        if (index >= 0)
        {
            MoveCursor(index);
        }
    }

    public void ClearQuickSearch() => QuickSearchText = string.Empty;

    public void ToggleMark(FileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Entry.IsParentLink)
        {
            return;
        }

        item.IsMarked = !item.IsMarked;
        UpdateStatus();
    }

    /// <summary>Insert: toggle the mark and move down.</summary>
    public void MarkCurrentAndAdvance()
    {
        if (CurrentItem is { } item)
        {
            ToggleMark(item);
            MoveCursor(CurrentIndex + 1);
        }
    }

    /// <summary>Space: toggle the mark; on a folder also measure its size.</summary>
    public async Task MarkCurrentAndMeasureAsync()
    {
        if (CurrentItem is not { Entry.IsParentLink: false } item)
        {
            return;
        }

        ToggleMark(item);
        if (item.IsDirectory && item.DirectorySize is null)
        {
            try
            {
                var size = await DirectorySize.CalculateAsync(item.Entry.FullPath);
                item.DirectorySize = size.Bytes;
                UpdateStatus();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Cannot measure {Path}", item.Entry.FullPath);
            }
        }
    }

    /// <summary>Num+ / Num−: marks or unmarks files whose names match the mask. Returns how many changed.</summary>
    public int MarkByMask(string mask, bool mark)
    {
        var matches = FileMask.Parse(mask);
        var changed = 0;
        foreach (var item in Items)
        {
            if (!item.IsDirectory && item.IsMarked != mark && matches(item.Name))
            {
                item.IsMarked = mark;
                changed++;
            }
        }

        UpdateStatus();
        return changed;
    }

    /// <summary>Shift+F2: marks exactly these names (files and folders) and clears every other mark. ".." is never marked.</summary>
    public int MarkOnly(IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var marked = 0;
        foreach (var item in Items)
        {
            var mark = !item.Entry.IsParentLink && names.Contains(item.Name);
            item.IsMarked = mark;
            if (mark)
            {
                marked++;
            }
        }

        UpdateStatus();
        return marked;
    }

    /// <summary>Num*: inverts the marks on files.</summary>
    public void InvertMarks()
    {
        foreach (var item in Items)
        {
            if (!item.IsDirectory)
            {
                item.IsMarked = !item.IsMarked;
            }
        }

        UpdateStatus();
    }

    /// <summary>Ctrl+A: marks every file and folder.</summary>
    public void MarkAll()
    {
        foreach (var item in Items)
        {
            item.IsMarked = !item.Entry.IsParentLink;
        }

        UpdateStatus();
    }

    public void ClearMarks()
    {
        foreach (var item in Items)
        {
            item.IsMarked = false;
        }

        UpdateStatus();
    }

    public void ApplySettings(PanelSettings settings, bool showHidden)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Tabs.Clear();
        if (settings.Tabs is { Count: > 0 })
        {
            foreach (var saved in settings.Tabs)
            {
                Tabs.Add(new PanelTab { Path = saved.Path, Title = PanelTab.TitleOf(saved.Path), Sort = saved.Sort ?? new SortSpec(), BriefView = saved.BriefView, Detail = saved.Detail });
            }

            ActiveTabIndex = Math.Clamp(settings.ActiveTab, 0, Tabs.Count - 1);
        }
        else
        {
            Tabs.Add(new PanelTab { Path = settings.Path, Title = PanelTab.TitleOf(settings.Path), Sort = settings.Sort ?? new SortSpec(), BriefView = settings.BriefView, Detail = settings.Detail });
            ActiveTabIndex = 0;
        }

        Tabs[ActiveTabIndex].IsActive = true;
        RestoreView(Tabs[ActiveTabIndex]);
        ShowHidden = showHidden;
    }

    /// <summary>The tab strip. There is always at least one tab; the strip itself shows only with two or more.</summary>
    public ObservableCollection<PanelTab> Tabs { get; } = [new PanelTab { IsActive = true }];

    public int ActiveTabIndex { get; private set; }

    public bool HasTabs => Tabs.Count > 1;

    /// <summary>Ctrl+T: a new tab that starts as a copy of this one (same folder, sort and view) and becomes active.</summary>
    public void NewTab()
    {
        if (string.IsNullOrEmpty(Path))
        {
            return;
        }

        CaptureActiveTab();
        Tabs[ActiveTabIndex].IsActive = false;
        var tab = new PanelTab { Path = Path, Title = PanelTab.TitleOf(Path), Sort = Sort, BriefView = BriefView, Detail = Detail, CursorName = CurrentItem?.Name, IsActive = true };
        Tabs.Insert(++ActiveTabIndex, tab);
    }

    /// <summary>Opens a tab at another folder (e.g. Ctrl+Enter on a folder) without disturbing the current one.</summary>
    public Task<bool> OpenInNewTabAsync(string path)
    {
        NewTab();
        return NavigateAsync(path);
    }

    public async Task SwitchTabAsync(int index)
    {
        if (index < 0 || index >= Tabs.Count || index == ActiveTabIndex)
        {
            return;
        }

        var leaving = Path;
        CaptureActiveTab();
        Tabs[ActiveTabIndex].IsActive = false;
        ActiveTabIndex = index;
        Tabs[index].IsActive = true;
        await ShowTabAsync(Tabs[index], leaving);
    }

    public Task NextTabAsync(int step = 1) => Tabs.Count < 2 ? Task.CompletedTask : SwitchTabAsync(((ActiveTabIndex + step) % Tabs.Count + Tabs.Count) % Tabs.Count);

    /// <summary>Ctrl+W. The last tab cannot be closed; closing the active one shows its neighbour.</summary>
    public async Task CloseTabAsync(int index)
    {
        if (Tabs.Count <= 1 || index < 0 || index >= Tabs.Count)
        {
            return;
        }

        var wasActive = index == ActiveTabIndex;
        var leaving = Path;
        if (!wasActive)
        {
            CaptureActiveTab();
        }

        Tabs.RemoveAt(index);
        if (index < ActiveTabIndex)
        {
            ActiveTabIndex--;
        }
        else if (wasActive)
        {
            ActiveTabIndex = Math.Min(index, Tabs.Count - 1);
            Tabs[ActiveTabIndex].IsActive = true;
            await ShowTabAsync(Tabs[ActiveTabIndex], leaving);
        }
    }

    public Task CloseActiveTabAsync() => CloseTabAsync(ActiveTabIndex);

    private async Task ShowTabAsync(PanelTab tab, string fallback)
    {
        RestoreView(tab);
        if (!await NavigateAsync(tab.Path, tab.CursorName))
        {
            await NavigateWithFallbackAsync(tab.Path, fallback); // the folder is gone: nearest existing parent
        }
        else if (tab.MarkedNames.Count > 0)
        {
            MarkOnly(tab.MarkedNames);
        }
    }

    /// <summary>Writes what the panel shows into the active tab record, before leaving it or saving.</summary>
    private void CaptureActiveTab()
    {
        var tab = Tabs[ActiveTabIndex];
        if (!string.IsNullOrEmpty(Path))
        {
            tab.MoveTo(Path);
        }

        tab.Sort = Sort;
        tab.BriefView = BriefView;
        tab.Detail = Detail;
        tab.CursorName = CurrentItem is { Entry.IsParentLink: false } item ? item.Name : null;
        tab.MarkedNames = _results is null ? Items.Where(static i => i.IsMarked).Select(static i => i.Name).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();
    }

    public PanelSettings CaptureSettings()
    {
        CaptureActiveTab();
        // a server is not reopened at startup (it would ask for a password before the window is up): the disk folder it was opened from is
        return new PanelSettings
        {
            Path = LocalOr(Path),
            Sort = Sort,
            BriefView = BriefView,
            Detail = Detail,
            ActiveTab = ActiveTabIndex,
            Tabs = Tabs.Count > 1 ? Tabs.Select(t => new TabSettings { Path = LocalOr(t.Path), Sort = t.Sort, BriefView = t.BriefView, Detail = t.Detail }).ToList() : [],
        };
    }

    /// <summary>The last disk folder this panel showed, where it returns when a server is closed.</summary>
    public string LastLocalPath { get; private set; } = string.Empty;

    private string LocalOr(string path) => RemotePath.IsRemote(path) || NetworkPath.IsVirtual(path) ? LastLocalPath : path;

    public void Dispose()
    {
        _listing?.Cancel();
        _listing = null;
        _debounce?.Cancel();
        _watcher?.Dispose();
        _watcher = null;
    }

    private static void CarryOverState(IReadOnlyList<FileItem> before, FileItem[] after)
    {
        Dictionary<string, FileItem>? previous = null;
        foreach (var item in before)
        {
            if (item.IsMarked || item.DirectorySize is not null)
            {
                previous ??= new Dictionary<string, FileItem>(StringComparer.Ordinal);
                previous[item.Name] = item;
            }
        }

        if (previous is null)
        {
            return;
        }

        foreach (var item in after)
        {
            if (previous.TryGetValue(item.Name, out var old))
            {
                item.IsMarked = old.IsMarked;
                item.DirectorySize = old.DirectorySize;
            }
        }
    }

    /// <summary>True when a re-read brought the same entries with the same properties, in the same order.</summary>
    private static bool SameListing(IReadOnlyList<FileItem> shown, FileItem[] fresh)
    {
        if (shown.Count != fresh.Length)
        {
            return false;
        }

        for (var i = 0; i < fresh.Length; i++)
        {
            var a = shown[i];
            var b = fresh[i];
            if (a.Entry != b.Entry || a.DetailKind != b.DetailKind || !Equals(a.Details, b.Details))
            {
                return false;
            }
        }

        return true;
    }

    private void StartWatching(string path)
    {
        _watcher?.Dispose();
        _watcher = _fs.Watch(path, OnDirectoryChanged);
    }

    /// <summary>Called by the watcher on any thread; refreshes once things are quiet for <see cref="AutoRefreshDelay"/>.</summary>
    private void OnDirectoryChanged()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Delay(AutoRefreshDelay, cts.Token).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    PostToUi(() =>
                    {
                        if (!IsLoading)
                        {
                            Log.Verbose("Change in {Path}: refreshing", Path);
                            _ = RefreshAsync();
                        }
                        else
                        {
                            // The listing under way may have read the folder before this change: it is not known to
                            // include it, so the load that finishes next refreshes once more.
                            _changedWhileLoading = true;
                        }
                    });
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void PostToUi(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(static a => ((Action)a!).Invoke(), action);
        }
    }

    private async Task<(FileItem[] Items, DriveEntry? Drive)> LoadAsync(string path, SortSpec sort, DetailColumn detail, PanelFilter? filter, bool showHidden, CancellationToken token)
    {
        var entries = new List<FileEntry>();
        await foreach (var entry in _fs.ListAsync(path, showHidden, token).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        var items = new FileItem[entries.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new FileItem(entries[i], _icons);
        }

        // Sorting or filtering by a property reads it for every file first (fresh rows: no one watches them yet).
        if ((sort.Column == SortColumn.Detail && detail != DetailColumn.None) || filter?.NeedsDetails == true)
        {
            foreach (var (item, details) in ReadAllDetails(items, token))
            {
                item.Details = details;
            }
        }

        Array.Sort(items, new ItemComparer(sort, detail));

        var root = RemotePath.IsRemote(path) || NetworkPath.IsVirtual(path) ? null : System.IO.Path.GetPathRoot(path);
        var drive = string.IsNullOrEmpty(root) ? null : _fs.GetDrive(root);
        return (items, drive);
    }

    private void MoveCursor(int index)
    {
        CurrentIndex = Items.Count == 0 ? -1 : Math.Clamp(index, 0, Items.Count - 1);
        FocusRequested?.Invoke(this, CurrentIndex);
    }

    private int IndexOfName(string name)
    {
        // the exact name first: in a case-sensitive folder "a.txt" and "A.txt" are two rows
        for (var i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Puts the usual summary back after a temporary message (e.g. the comparison's progress).</summary>
    internal void RestoreStatus() => UpdateStatus();

    private void UpdateStatus()
    {
        int files = 0, dirs = 0, markedFiles = 0, markedDirs = 0;
        long bytes = 0, markedBytes = 0;
        foreach (var item in Items)
        {
            if (item.Entry.IsParentLink)
            {
                continue;
            }

            if (item.IsDirectory)
            {
                dirs++;
            }
            else
            {
                files++;
            }

            bytes += item.SelectionBytes;
            if (item.IsMarked)
            {
                markedBytes += item.SelectionBytes;
                if (item.IsDirectory)
                {
                    markedDirs++;
                }
                else
                {
                    markedFiles++;
                }
            }
        }

        HasMarks = markedFiles + markedDirs > 0;

        // Counts first: a long filter description would push them out of a narrow footer.
        Status = (markedFiles + markedDirs > 0
            ? Texts.Selection(markedFiles, files, markedBytes, bytes, markedDirs, dirs)
            : Texts.Summary(files, bytes, dirs)) + (Filter is { } filter ? Texts.FilterSuffix(Texts.Describe(filter)) : string.Empty);
    }

    private void Remember(string full)
    {
        _history.RemoveAll(h => string.Equals(h, full, StringComparison.OrdinalIgnoreCase));
        _history.Add(full);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveAt(0);
        }
    }

    private void RememberDrivePath(string full)
    {
        if (RemotePath.IsRemote(full) || NetworkPath.IsVirtual(full))
        {
            return;
        }

        LastLocalPath = full;
        var root = System.IO.Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root))
        {
            _lastPathPerDrive[RootKey(root)] = full;
        }
    }

    private void UpdateDriveState(DriveEntry? drive)
    {
        DriveSummary = drive is null ? string.Empty : Texts.DriveSummary(drive);
        DriveFreeText = drive is null ? string.Empty : Texts.DriveFree(drive);
        DriveUsedFraction = drive is null ? 0 : Texts.DriveUsedFraction(drive);
        CurrentDrive = drive;
        MarkCurrentDrive();
    }

    private void MarkCurrentDrive()
    {
        var root = CurrentRoot;
        // inside a OneDrive folder that button is the current one (not also the drive holding it)
        var cloud = Drives.Where(d => d.IsCloud && IsInside(Path, d.Root)).OrderByDescending(static d => d.Root.Length).FirstOrDefault();
        var distro = WslDistros.DistroOf(Path);
        foreach (var d in Drives)
        {
            d.IsCurrent = cloud is not null
                ? ReferenceEquals(d, cloud)
                : d.IsWsl
                    ? distro is not null && string.Equals(WslDistros.DistroOf(d.Root), distro, StringComparison.OrdinalIgnoreCase)
                    : !d.IsCloud && root is not null && string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsInside(string? path, string folder) =>
        !string.IsNullOrEmpty(path)
        && (string.Equals(path.TrimEnd('\\', '/'), folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private string Glyph(SortColumn column) =>
        Sort.Column != column ? string.Empty
        : Sort.Direction == SortDirection.Ascending ? "▲" : "▼";
}
