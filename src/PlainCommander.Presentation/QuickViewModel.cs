using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Remote;

namespace PlainCommander.Presentation;

/// <summary>Puts text on the clipboard (the app implements it with the Windows clipboard).</summary>
public interface IClipboard
{
    void SetText(string text);
}

/// <summary>
/// Ctrl+Q: the other panel shows the file under the cursor (text, hex or image, as F3 would), following the cursor
/// after a short pause so fast scrolling does not read every file on the way. A folder shows what it holds; files on
/// servers and in archives are left to F3, which knows how to fetch them.
/// </summary>
public sealed partial class QuickViewModel : ObservableObject, IDisposable
{
    private PanelViewModel? _source;
    private CancellationTokenSource? _pending;

    /// <summary>Wait after the last cursor move before reading the file.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(150);

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The view takes the left panel's place (the source is the right panel).</summary>
    [ObservableProperty]
    public partial bool ReplacesLeft { get; private set; }

    /// <summary>The file being shown, or null when <see cref="Message"/> says why not.</summary>
    [ObservableProperty]
    public partial ViewerViewModel? Viewer { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    public void Open(PanelViewModel source, bool replacesLeft)
    {
        ArgumentNullException.ThrowIfNull(source);
        Close();
        _source = source;
        ReplacesLeft = replacesLeft;
        IsOpen = true;
        source.CurrentChanged += OnCurrentChanged;
        _ = ShowAsync(source.CurrentItem, CancellationToken.None);
    }

    public void Close()
    {
        if (_source is not null)
        {
            _source.CurrentChanged -= OnCurrentChanged;
            _source = null;
        }

        _pending?.Cancel();
        _pending = null;
        Replace(null);
        Title = string.Empty;
        Message = string.Empty;
        IsOpen = false;
    }

    public void Dispose() => Close();

    private async void OnCurrentChanged(object? sender, EventArgs e)
    {
        _pending?.Cancel();
        var pending = new CancellationTokenSource();
        _pending = pending;
        try
        {
            await Task.Delay(Delay, pending.Token);
            await ShowAsync(_source?.CurrentItem, pending.Token);
        }
        catch (OperationCanceledException)
        {
            // the cursor moved on
        }
    }

    private async Task ShowAsync(FileItem? item, CancellationToken token)
    {
        if (item is null || item.Entry.IsParentLink)
        {
            Replace(null);
            Title = string.Empty;
            Message = string.Empty;
            return;
        }

        Title = item.Name;
        var path = item.Entry.FullPath;
        if (RemotePath.IsRemote(path) || ArchiveFileSystem.Locate(path) is not null)
        {
            Replace(null);
            Message = Texts.QuickViewNotHere;
            return;
        }

        if (item.IsDirectory)
        {
            Replace(null);
            var (files, folders) = await Task.Run(() => Count(path), token);
            token.ThrowIfCancellationRequested();
            Message = Texts.QuickViewFolder(files, folders);
            return;
        }

        Message = string.Empty;
        var viewer = new ViewerViewModel(path);
        Replace(viewer);
        await viewer.LoadAsync();
    }

    private void Replace(ViewerViewModel? viewer)
    {
        var old = Viewer;
        Viewer = viewer;
        old?.Dispose();
    }

    private static (int Files, int Folders) Count(string path)
    {
        int files = 0, folders = 0;
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                if (entry is DirectoryInfo)
                {
                    folders++;
                }
                else
                {
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // what could be counted
        }

        return (files, folders);
    }
}
