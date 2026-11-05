using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Formatting;
using PlainCommander.Core.Viewer;
using Serilog;

namespace PlainCommander.Presentation;

/// <summary>
/// F3: one file in one of three views. Text is decoded up to 16 MB with the encoding detected or chosen,
/// hex is formatted on demand from the file, images are left to the view (it has the decoder).
/// </summary>
public sealed partial class ViewerViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _loading;

    public ViewerViewModel(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        CanShowImage = ViewerModeDetector.IsImage(path);
    }

    public string Path { get; }

    public string FileName { get; }

    /// <summary>The image view is offered only for files that look like images by extension.</summary>
    public bool CanShowImage { get; }

    public static IReadOnlyList<Encoding> Encodings => TextDecoder.Choices;

    /// <summary>Short label for the encoding menu and footer ("UTF-8", "Windows-1250").</summary>
    public static string EncodingName(Encoding encoding) => Texts.EncodingName(encoding);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsHex), nameof(IsImage))]
    public partial ViewerMode Mode { get; private set; }

    public bool IsText => Mode == ViewerMode.Text;

    public bool IsHex => Mode == ViewerMode.Hex;

    public bool IsImage => Mode == ViewerMode.Image;

    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    private HexLines? _hex;

    /// <summary>Hex mode shows this page: the rows that fit on screen, starting at <see cref="HexTop"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> HexPage { get; private set; } = [];

    /// <summary>Index of the first row on screen.</summary>
    [ObservableProperty]
    public partial long HexTop { get; private set; }

    /// <summary>Rows in the whole file (16 bytes each).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HexMaxTop))]
    public partial long HexLineCount { get; private set; }

    /// <summary>Rows that fit on screen; the view sets it from its height.</summary>
    public int HexVisibleLines
    {
        get;
        set
        {
            value = Math.Max(1, value);
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(HexMaxTop));
                HexScrollTo(HexTop); // the old top may now leave empty rows at the bottom
                RefreshHexPage();
            }
        }
    }

    = 32;

    /// <summary>The last top row that still fills the screen (the scroll bar's maximum).</summary>
    public long HexMaxTop => Math.Max(0, HexLineCount - HexVisibleLines);

    public void HexScrollTo(long top)
    {
        if (_hex is null)
        {
            return;
        }

        top = Math.Clamp(top, 0, HexMaxTop);
        if (top != HexTop || HexPage.Count == 0)
        {
            HexTop = top;
            RefreshHexPage();
        }
    }

    public void HexScrollBy(long lines) => HexScrollTo(HexTop + lines);

    /// <summary>16 bytes a row, or 8 where the view is narrow (the quick view sets it from its width).</summary>
    public int HexBytesPerLine
    {
        get;
        set
        {
            value = value <= 8 ? 8 : 16;
            if (field == value)
            {
                return;
            }

            var old = field;
            field = value;
            if (_hex is null)
            {
                return;
            }

            // the same bytes stay at the top: reopen with the new width
            var firstByte = HexTop * old;
            DropHex();
            try
            {
                _hex = HexLines.Open(Path, value);
                HexLineCount = _hex.LineCount;
                HexTop = 0;
                HexScrollTo(firstByte / value);
                RefreshHexPage();
            }
            catch (IOException ex)
            {
                Error = UserMessages.Describe(ex);
            }
        }
    }

    = HexFormat.BytesPerLine;

    public void HexPageDown() => HexScrollBy(HexVisibleLines);

    public void HexPageUp() => HexScrollBy(-HexVisibleLines);

    public void HexHome() => HexScrollTo(0);

    public void HexEnd() => HexScrollTo(long.MaxValue);

    private void RefreshHexPage()
    {
        if (_hex is null)
        {
            HexPage = [];
            return;
        }

        try
        {
            HexPage = _hex.Page(HexTop, HexVisibleLines);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "Cannot read {Path}", Path);
            Error = UserMessages.Describe(ex);
        }
    }

    [ObservableProperty]
    public partial Encoding? Encoding { get; private set; }

    [ObservableProperty]
    public partial bool WordWrap { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The file is a cloud placeholder (OneDrive "online only"): opening it downloads it first.</summary>
    public bool IsCloud { get; private set; }

    public long FileLength { get; private set; }

    public bool Truncated { get; private set; }

    /// <summary>Opens the file in the mode its content calls for.</summary>
    public async Task LoadAsync()
    {
        try
        {
            // A cloud placeholder is downloaded by the first read. Everything that touches the content runs on a worker
            // thread, so the window (and the whole app: it shares the UI thread with the panels) stays alive meanwhile.
            IsCloud = await Task.Run(() => CloudFile.IsCloudOnly(Path));
            if (IsCloud)
            {
                IsLoading = true;
                Status = Texts.ViewerDownloading;
            }

            FileLength = new FileInfo(ExactPath.Native(Path)).Length;
            var sample = await Task.Run(() => TextContent.SampleAsync(Path, CancellationToken.None));
            await SetModeAsync(ViewerModeDetector.Detect(Path, sample));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Cannot view {Path}", Path);
            Error = UserMessages.Describe(ex);
            Status = string.Empty;
        }
    }

    public async Task SetModeAsync(ViewerMode mode)
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        Mode = mode;
        Error = null;
        IsLoading = true;
        try
        {
            switch (mode)
            {
                case ViewerMode.Text:
                    var content = await TextContent.LoadAsync(Path, Encoding, cts.Token);
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    Encoding = content.Encoding;
                    Truncated = content.Truncated;
                    FileLength = content.FileLength;
                    Text = content.Text;
                    DropHex();
                    break;
                case ViewerMode.Hex:
                    DropHex();
                    // Open and read the first page off the UI thread: for a cloud file that read is the download.
                    var lines = HexVisibleLines;
                    var width = HexBytesPerLine;
                    var (hex, first) = await Task.Run(() =>
                    {
                        var h = HexLines.Open(Path, width);
                        return (h, h.Page(0, lines));
                    });
                    if (cts.IsCancellationRequested)
                    {
                        hex.Dispose();
                        return;
                    }

                    _hex = hex;
                    FileLength = hex.Length;
                    HexLineCount = hex.LineCount;
                    HexTop = 0;
                    if (lines == HexVisibleLines)
                    {
                        HexPage = first;
                    }
                    else
                    {
                        RefreshHexPage(); // the view told its height while the file was opening
                    }

                    break;
                case ViewerMode.Image:
                    DropHex();
                    FileLength = new FileInfo(Path).Length;
                    break;
            }

            Status = Texts.ViewerStatus(mode, FileLength, Encoding, Truncated && mode == ViewerMode.Text);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Cannot view {Path}", Path);
            Error = UserMessages.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_loading, cts))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Re-decodes the text with another encoding (the detection was wrong).</summary>
    public Task SetEncodingAsync(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);
        Encoding = encoding;
        return SetModeAsync(ViewerMode.Text);
    }

    public void Dispose()
    {
        _loading?.Cancel();
        DropHex();
    }

    private void DropHex()
    {
        _hex?.Dispose();
        _hex = null;
        HexPage = [];
        HexLineCount = 0;
        HexTop = 0;
    }
}
