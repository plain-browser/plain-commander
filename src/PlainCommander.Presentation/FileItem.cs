using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Formatting;
using PlainCommander.Core.Model;

namespace PlainCommander.Presentation;

/// <summary>
/// One panel row. Display strings are computed on demand, so only rows the list actually
/// realizes pay for formatting; the icon is requested the first time a row asks for it.
/// </summary>
public sealed partial class FileItem : ObservableObject
{
    private readonly IIconProvider? _icons;
    private bool _iconRequested;
    private object? _icon;

    public FileItem(FileEntry entry, IIconProvider? icons = null)
    {
        Entry = entry;
        _icons = icons;
    }

    public FileEntry Entry { get; }

    public string Name => Entry.Name;

    /// <summary>Name column of the full view: the extension has its own column.</summary>
    public string DisplayName => Entry.NameWithoutExtension;

    public string Extension => Entry.Extension;

    /// <summary>Bytes, "&lt;DIR&gt;", or a folder's measured size after Space.</summary>
    public string SizeText => DirectorySize is { } size
        ? size.ToString("N0", CultureInfo.CurrentCulture)
        : EntryFormatter.Size(Entry, CultureInfo.CurrentCulture);

    /// <summary>Marked for an operation (Insert, Space, Num+…). Shown in the mark colour.</summary>
    [ObservableProperty]
    public partial bool IsMarked { get; set; }

    /// <summary>Total size of a folder's content once measured (Space), otherwise null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long? DirectorySize { get; set; }

    /// <summary>Bytes this row adds to a selection total.</summary>
    public long SelectionBytes => IsDirectory ? DirectorySize ?? 0 : Entry.Size;

    /// <summary>What the panel's detail column shows; the value is read the first time a row asks for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial Core.Details.DetailColumn DetailKind { get; set; }

    /// <summary>Properties read from the file (dimensions, photo date, duration, version); null until read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    public partial Core.Details.FileDetails? Details { get; set; }

    /// <summary>Set by the panel: queues this row for reading its properties.</summary>
    internal Action<FileItem>? RequestDetails { get; set; }

    private bool _detailsRequested;

    public string DetailText
    {
        get
        {
            if (DetailKind == Core.Details.DetailColumn.None || IsDirectory)
            {
                return string.Empty;
            }

            if (Details is null && !_detailsRequested && RequestDetails is { } request)
            {
                _detailsRequested = true;
                request(this);
            }

            return Core.Details.DetailFormatter.Text(Details, DetailKind, CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The date column shows the creation date (the panel is sorted by it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateText), nameof(ShortDateText))]
    public partial bool ShowCreated { get; set; }

    public string DateText => EntryFormatter.Date(Entry, CultureInfo.CurrentCulture, ShowCreated);

    /// <summary>The date alone: a narrow panel showing attributes has no room for the time.</summary>
    public string ShortDateText => EntryFormatter.DateOnly(Entry, CultureInfo.CurrentCulture, ShowCreated);

    public string AttributesText => EntryFormatter.Attributes(Entry);

    public bool IsDirectory => Entry.IsDirectory;

    /// <summary>A .pce container (Plain Commander's own encryption): drawn in its own colour, Enter decrypts it.</summary>
    public bool IsEncrypted => !Entry.IsDirectory && Core.Crypto.PceFormat.IsPceName(Entry.Name);

    /// <summary>Hidden or system entries are drawn faded (".." never is).</summary>
    public bool IsHidden => !Entry.IsParentLink && (Entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    public object? Icon
    {
        get
        {
            if (!_iconRequested && _icons is not null)
            {
                _iconRequested = true;
                _icon = _icons.GetIcon(Entry, image =>
                {
                    _icon = image;
                    OnPropertyChanged(nameof(Icon));
                });
            }

            return _icon;
        }
    }

    public override string ToString() => Name;
}
