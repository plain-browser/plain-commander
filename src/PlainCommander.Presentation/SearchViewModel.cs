using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Search;
using Serilog;

namespace PlainCommander.Presentation;

/// <summary>
/// Alt+F7. Criteria are plain strings as typed; parsing happens when the search starts. Results stream in
/// while the walk runs, the status line tells how far it got, and Stop cancels without losing what was found.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    private readonly IIconProvider? _icons;
    private CancellationTokenSource? _running;

    public SearchViewModel(string root, IIconProvider? icons = null)
    {
        Root = root;
        _icons = icons;
    }

    [ObservableProperty]
    public partial string Root { get; set; }

    [ObservableProperty]
    public partial string Masks { get; set; } = "*.*";

    [ObservableProperty]
    public partial bool Subfolders { get; set; } = true;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CaseSensitive { get; set; }

    /// <summary>Sizes as typed: "10k", "2M", "500" (bytes). Empty means no bound.</summary>
    [ObservableProperty]
    public partial string MinSize { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MaxSize { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? ModifiedAfter { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? ModifiedBefore { get; set; }

    [ObservableProperty]
    public partial bool IncludeHidden { get; set; }

    /// <summary>The name field is a regular expression instead of masks.</summary>
    [ObservableProperty]
    public partial bool NameRegex { get; set; }

    /// <summary>The text field is a regular expression.</summary>
    [ObservableProperty]
    public partial bool TextRegex { get; set; }

    /// <summary>Look inside zip, 7z, tar… as if they were folders.</summary>
    [ObservableProperty]
    public partial bool InArchives { get; set; }

    /// <summary>The criteria of the last finished or running search, for the "results to panel" title.</summary>
    public SearchCriteria? LastCriteria { get; private set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public ObservableCollection<FileItem> Results { get; } = [];

    /// <summary>"10k" → 10 240, "2M" → 2 097 152, "500" → 500; null for blank, exception for nonsense.</summary>
    public static long? ParseSize(string text) => Core.Navigation.PanelFilter.ParseSize(text);

    public SearchCriteria BuildCriteria() => new(
        Root.Trim(),
        string.IsNullOrWhiteSpace(Masks) ? "*.*" : Masks.Trim(),
        Subfolders,
        string.IsNullOrEmpty(Text) ? null : Text,
        CaseSensitive,
        ParseSize(MinSize),
        ParseSize(MaxSize),
        ModifiedAfter,
        ModifiedBefore is { } before ? before.Date.AddDays(1).AddTicks(-1) : null, // "before 12.05" includes the whole 12.05
        IncludeHidden,
        NameRegex,
        TextRegex,
        InArchives);

    /// <summary>Runs the search; results arrive on the caller's context, so bind straight to <see cref="Results"/>.</summary>
    public async Task StartAsync()
    {
        Stop();
        Error = null;
        Results.Clear();
        SearchCriteria criteria;
        try
        {
            criteria = BuildCriteria();
            if (!Directory.Exists(criteria.Root))
            {
                Error = Texts.CannotOpen(criteria.Root, new DirectoryNotFoundException());
                return;
            }

            _ = criteria.NameMatcher(); // a broken regular expression is reported here, not halfway through the walk
            _ = criteria.TextMatcher();
        }
        catch (FormatException ex)
        {
            Error = Texts.BadSize(ex.Message);
            return;
        }
        catch (ArgumentException ex)
        {
            Error = Texts.BadRegex(ex.Message);
            return;
        }

        LastCriteria = criteria;

        var cts = _running = new CancellationTokenSource();
        IsRunning = true;
        Status = Texts.SearchProgress(0, 0, criteria.Root);
        var finished = false;
        var statusLock = new object();
        var progress = new Progress<SearchProgress>(p =>
        {
            // A late progress report must not overwrite the final "done" line. Without a UI context the report
            // runs on the thread pool, so the check and the write share a lock with the final line.
            lock (statusLock)
            {
                if (ReferenceEquals(_running, cts) && !finished)
                {
                    Status = Texts.SearchProgress(p.Folders, p.Matches, p.CurrentFolder);
                }
            }
        });
        var folders = 0;
        try
        {
            await foreach (var entry in FileSearch.RunAsync(criteria, progress, cts.Token))
            {
                Results.Add(new FileItem(entry, _icons));
            }

            lock (statusLock)
            {
                finished = true;
                Status = Texts.SearchDone(Results.Count, cancelled: false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (statusLock)
            {
                finished = true;
                Status = Texts.SearchDone(Results.Count, cancelled: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Search failed under {Root}", criteria.Root);
            Error = UserMessages.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                IsRunning = false;
                _running = null;
            }

            cts.Dispose();
            _ = folders;
        }
    }

    public void Stop()
    {
        _running?.Cancel();
    }

    public void Dispose() => Stop();
}
