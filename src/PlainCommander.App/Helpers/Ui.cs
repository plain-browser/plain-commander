using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using DriveType = System.IO.DriveType;

namespace PlainCommander.App.Helpers;

/// <summary>Functions for x:Bind, so XAML needs no converters.</summary>
public static class Ui
{
    public static ImageSource? Image(object? icon) => icon as ImageSource;

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The detail column: as wide as a date with time for a photo date, narrower otherwise, nothing when off.</summary>
    public static GridLength DetailWidth(Core.Details.DetailColumn column) => column switch
    {
        Core.Details.DetailColumn.None => new GridLength(0),
        Core.Details.DetailColumn.Taken => new GridLength(116),
        _ => new GridLength(92),
    };

    /// <summary>The 2 px line over the active panel's header.</summary>
    public static Brush AccentIf(bool active) =>
        active ? Resource<Brush>("AccentFillColorDefaultBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static double HeaderOpacity(bool active) => active ? 1.0 : 0.7;

    /// <summary>Segoe Fluent glyph for the drive type: hard disk, USB, disc, network, cloud.</summary>
    /// <summary>The drive button: a globe on a server, the drive's own icon elsewhere.</summary>
    public static string PanelGlyph(DriveEntry? drive, bool remote) => remote ? "\uE774" : DriveGlyph(drive);

    public static string DriveGlyph(DriveEntry? drive) => drive?.CloudName is not null ? "\uE753" : drive?.Type switch
    {
        DriveType.Removable => "\uE88E",
        DriveType.CDRom => "\uE958",
        DriveType.Network => "\uE968",
        DriveType.Ram => "\uEEA0",
        _ => "\uEDA2",
    };

    /// <summary>The view toggle shows what a click switches to.</summary>
    /// <summary>
    /// Minimise / maximise / close in the title bar of a window that draws its own title bar: they must read on the
    /// window's theme (Windows' own colours follow the system theme, which differs when the app is forced light or dark).
    /// </summary>
    public static void StyleCaptionButtons(Microsoft.UI.Xaml.Window window, ElementTheme requested)
    {
        var dark = requested != ElementTheme.Default
            ? requested == ElementTheme.Dark
            : Application.Current.RequestedTheme == ApplicationTheme.Dark;
        var bar = window.AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1) : Windows.UI.Color.FromArgb(0xFF, 0x33, 0x41, 0x55);
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0xFF, 0x64, 0x74, 0x8B) : Windows.UI.Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8);
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Windows.UI.Color.FromArgb(0x1F, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = bar.ButtonHoverForegroundColor;
    }

    public static string ViewGlyph(bool brief) => brief ? "\uE8FD" : "\uE8A4";

    // Row states. Marked rows: light accent text on an accent wash plus a check badge on the icon;
    // the cursor is the list's own selection pill. Folders have amber names, .pce files violet, sizes of folders and dates are muted.
    private static readonly Brush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static Brush MarkForeground(bool marked) =>
        Resource<Brush>(marked ? "PcMarkTextBrush" : "PcTextSecondaryBrush");

    public static Brush NameForeground(bool directory, bool marked, bool encrypted) =>
        Resource<Brush>(marked ? "PcMarkTextBrush" : directory ? "PcFolderTextBrush" : encrypted ? "PcEncryptedTextBrush" : "PcTextBrush");

    public static Brush MarkBackground(bool marked) => marked ? Resource<Brush>("PcMarkBrush") : Transparent;

    public static Visibility MarkDot(bool marked) => marked ? Visibility.Visible : Visibility.Collapsed;

    public static Windows.UI.Text.FontWeight RowWeight(bool directory, bool marked) =>
        directory || marked ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public static double RowOpacity(bool hidden) => hidden ? 0.55 : 1.0;

    /// <summary>Muted colour for "&lt;DIR&gt;" so sizes stand out.</summary>
    public static Brush SizeForeground(bool directory, bool marked) =>
        Resource<Brush>(marked ? "PcMarkTextBrush" : directory ? "PcTextMutedBrush" : "PcTextBrush");

    // Footer usage bar: 64 px wide, a cyan-to-blue gradient until the drive is nearly full.
    public const double UsageBarWidth = 64;

    public static double UsageWidth(double fraction) => Math.Round(UsageBarWidth * fraction);

    public static Brush UsageBrush(double fraction) => Resource<Brush>(
        fraction >= 0.95 ? "SystemFillColorCriticalBrush" :
        fraction >= 0.90 ? "SystemFillColorCautionBrush" : "PcUsageBrush");

    /// <summary>The panel card: full strength when active, slightly faded otherwise.</summary>
    public static double CardOpacity(bool active) => active ? 1.0 : 0.88;

    /// <summary>Footer text: the selection summary is in the accent while something is marked.</summary>
    public static Brush StatusForeground(bool marked) => Resource<Brush>(marked ? "PcAccentTextBrush" : "PcTextSecondaryBrush");

    public static Windows.UI.Text.FontWeight StatusWeight(bool marked) =>
        marked ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>Drive pills on the tab strip: the current drive in the accent.</summary>
    public static Brush DrivePillBackground(bool current) => Resource<Brush>(current ? "PcMarkBrush" : "PcChipBrush");

    public static Brush DrivePillForeground(bool current) => Resource<Brush>(current ? "PcAccentTextBrush" : "PcTextSecondaryBrush");

    public static Brush DrivePillStroke(bool current) => current ? Resource<Brush>("PcActiveStrokeBrush") : Transparent;

    /// <summary>"C:" for a drive letter, the root itself otherwise.</summary>
    public static Visibility CloudMark(bool cloud) => cloud ? Visibility.Visible : Visibility.Collapsed;

    public static string DriveLetter(string letter) => letter.Length == 1 ? letter + ":" : letter;

    /// <summary>"Active: left panel" on the toolbar.</summary>
    public static string ActiveSideText(bool leftActive) => Loc.Get(leftActive ? "Toolbar_ActiveLeft" : "Toolbar_ActiveRight");

    /// <summary>The app version for the title bar badge, "v1.10.0".</summary>
    public static string VersionText { get; } = "v" + (typeof(Ui).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?");

    public static bool NotNull(object? value) => value is not null;

    /// <summary>Ctrl+M preview: names that change stand out.</summary>
    public static Windows.UI.Text.FontWeight ChangedWeight(bool changes) =>
        changes ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    // Tab strip: the active tab sits on the card colour with an accent line on top and accent text.
    public static Brush TabBackground(bool active) => active ? Resource<Brush>("PcCardBrush") : Transparent;

    public static Brush TabForeground(bool active) => Resource<Brush>(active ? "PcAccentTextBrush" : "PcTextSecondaryBrush");

    public static Brush TabLine(bool active) => active ? Resource<Brush>("AccentFillColorDefaultBrush") : Transparent;

    public static Windows.UI.Text.FontWeight TabWeight(bool active) =>
        active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    /// <summary>The folder part of a path, for result lists.</summary>
    public static string Folder(string path) => System.IO.Path.GetDirectoryName(path) ?? path;

    /// <summary>Window and taskbar icon. WinUI does not pick up the exe's icon resource by itself.</summary>
    public static void ApplyIcon(Window window)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "PlainCommander.ico");
        if (System.IO.File.Exists(path))
        {
            window.AppWindow.SetIcon(path);
        }
    }

    public static ElementTheme ThemeOf(AppTheme theme) => theme switch
    {
        AppTheme.Light => ElementTheme.Light,
        AppTheme.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public static FontFamily Font(string family) => new(string.IsNullOrWhiteSpace(family) ? AppSettings.DefaultPanelFont : family);

    public static double ToDouble(long value) => value;

    public static TextWrapping Wrap(bool wrap) => wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public static string EncodingName(System.Text.Encoding? encoding) =>
        encoding is null ? string.Empty : PlainCommander.Presentation.ViewerViewModel.EncodingName(encoding);

    /// <summary>Command-line prompt: the current folder's name, the full path stays in the tooltip.</summary>
    public static string Prompt(string path)
    {
        var segments = PlainCommander.Presentation.PathSegment.Split(path);
        return segments.Count == 0 ? string.Empty : segments[^1].Name + " \u203A";
    }

    // Text diff colours: translucent, so they read on light and dark backgrounds alike.
    private static readonly SolidColorBrush DiffRemoved = new(Windows.UI.Color.FromArgb(0x40, 0xE8, 0x11, 0x23));
    private static readonly SolidColorBrush DiffAdded = new(Windows.UI.Color.FromArgb(0x40, 0x10, 0x89, 0x3E));
    private static readonly SolidColorBrush DiffChanged = new(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xB9, 0x00));
    private static readonly SolidColorBrush DiffNone = new(Microsoft.UI.Colors.Transparent);

    /// <summary>Background of one side of a diff row: removed lines red on the left, added green on the right, changed amber on both.</summary>
    public static Brush DiffBack(PlainCommander.Core.Compare.DiffKind kind, bool left) => kind switch
    {
        PlainCommander.Core.Compare.DiffKind.Removed => left ? DiffRemoved : DiffNone,
        PlainCommander.Core.Compare.DiffKind.Added => left ? DiffNone : DiffAdded,
        PlainCommander.Core.Compare.DiffKind.Changed => DiffChanged,
        _ => DiffNone,
    };

    /// <summary>A plain FTP site shows an open padlock: nothing is encrypted.</summary>
    public static string SiteGlyph(bool unencrypted) => unencrypted ? "\uE785" : "\uE72E";

    public static string LineNumber(int? number) => number?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty;

    public static string PauseLabel(bool paused) => Loc.Get(paused ? "Op_Resume" : "Op_Pause");

    public static string QueuedText(int pending) => pending > 0 ? Loc.Format("Op_Queued", pending) : string.Empty;

    /// <summary>
    /// The theme the main window is drawn in (set by the window: the setting can differ from the system theme).
    /// Brushes handed out from code come from this theme's dictionary; the app-wide lookup would give the system one.
    /// </summary>
    public static ElementTheme Theme { get; set; } = ElementTheme.Dark;

    internal static T Resource<T>(string key)
    {
        var resources = Application.Current.Resources;
        if (resources.ThemeDictionaries.TryGetValue(Theme == ElementTheme.Light ? "Light" : "Dark", out var d)
            && d is ResourceDictionary themed && themed.TryGetValue(key, out var value))
        {
            return (T)value;
        }

        return (T)resources[key];
    }
}
