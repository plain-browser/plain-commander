using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Settings;

namespace PlainCommander.Presentation;

/// <summary>
/// The settings window. Every change applies at once, as in Windows Settings; the window saves on close.
/// Language is the exception: resources load at startup, so it asks for a restart.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly string _startupLanguage;
    private readonly bool _ready;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        var s = main.Settings;
        _startupLanguage = s.Language;
        // Nothing is applied while the fields are being copied in: applying the font before its size is
        // set would push a clamped size back into the panels.
        Theme = main.Theme;
        CompactRows = main.Left.CompactRows;
        ShowAttributes = main.Left.ShowAttributes;
        PanelFont = main.Left.FontFamily;
        PanelFontSize = main.Left.FontSize;
        Editor = s.Editor;
        Language = s.Language;
        CopyParallelism = s.CopyParallelism;
        _ready = true;
    }

    public static IReadOnlyList<string> Languages { get; } = [string.Empty, "pl-PL", "en-US"];

    public const int MinParallelism = 1;
    public const int MaxParallelism = 16;

    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [ObservableProperty]
    public partial bool CompactRows { get; set; }

    [ObservableProperty]
    public partial bool ShowAttributes { get; set; }

    [ObservableProperty]
    public partial string PanelFont { get; set; }

    [ObservableProperty]
    public partial double PanelFontSize { get; set; }

    [ObservableProperty]
    public partial string Editor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageNeedsRestart))]
    public partial string Language { get; set; }

    [ObservableProperty]
    public partial int CopyParallelism { get; set; }

    /// <summary>The language differs from the one the app started with.</summary>
    public bool LanguageNeedsRestart => !string.Equals(Language, _startupLanguage, StringComparison.OrdinalIgnoreCase);

    partial void OnThemeChanged(AppTheme value)
    {
        if (_ready)
        {
            _main.Theme = value;
        }
    }

    partial void OnCompactRowsChanged(bool value)
    {
        if (_ready)
        {
            _main.SetCompactRows(value);
        }
    }

    partial void OnShowAttributesChanged(bool value)
    {
        if (_ready)
        {
            _main.SetShowAttributes(value);
        }
    }

    partial void OnPanelFontChanged(string value)
    {
        if (_ready)
        {
            _main.SetFont(value, PanelFontSize);
        }
    }

    partial void OnPanelFontSizeChanged(double value)
    {
        if (_ready)
        {
            _main.SetFont(PanelFont, value);
        }
    }

    partial void OnEditorChanged(string value)
    {
        if (_ready)
        {
            _main.Settings.Editor = string.IsNullOrWhiteSpace(value) ? "notepad.exe" : value.Trim();
        }
    }

    partial void OnLanguageChanged(string value)
    {
        if (_ready)
        {
            _main.Settings.Language = value ?? string.Empty;
        }
    }

    partial void OnCopyParallelismChanged(int value)
    {
        if (_ready)
        {
            _main.Settings.CopyParallelism = Math.Clamp(value, MinParallelism, MaxParallelism);
        }
    }

    /// <summary>Writes settings.json (window placement is left as it was).</summary>
    public void Save() => _main.Save(window: null);
}
