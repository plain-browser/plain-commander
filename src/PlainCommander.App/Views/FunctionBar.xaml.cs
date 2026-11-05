using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PlainCommander.App.Helpers;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>
/// Action bar F2–F8 (F1 has no button). While Shift, Ctrl or Alt is held the labels switch
/// to that variant, e.g. "Shift+F6 Rename". Variants come from strings FKey_{Mod}_F{n};
/// a key without a variant keeps its base label.
/// </summary>
public sealed partial class FunctionBar : UserControl
{
    private VirtualKeyModifiers _modifiers;

    public FunctionBar()
    {
        InitializeComponent();
        Apply();
    }

    /// <summary>A function key button was clicked: 2 for F2 … 8 for F8. Modifiers are read by the window.</summary>
    public event EventHandler<int>? KeyClicked;

    /// <summary>The modifier keys currently held; the window feeds this from its key events.</summary>
    public VirtualKeyModifiers Modifiers
    {
        get => _modifiers;
        set
        {
            if (_modifiers != value)
            {
                _modifiers = value;
                Apply();
            }
        }
    }

    private void Apply()
    {
        var prefix = _modifiers switch
        {
            VirtualKeyModifiers.Shift => "Shift",
            VirtualKeyModifiers.Control => "Ctrl",
            VirtualKeyModifiers.Menu => "Alt",
            _ => null,
        };

        for (var n = 2; n <= 8; n++)
        {
            var key = (TextBlock)FindName($"Key{n}");
            var label = (TextBlock)FindName($"Label{n}");
            var baseText = Loc.Get($"FKey_F{n}");
            var variant = prefix is null ? null : Loc.TryGet($"FKey_{prefix}_F{n}");
            key.Text = variant is null || prefix is null ? $"F{n}" : $"{prefix}+F{n}";
            label.Text = variant ?? baseText;
            label.Opacity = prefix is not null && variant is null ? 0.5 : 1;
        }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            KeyClicked?.Invoke(this, int.Parse(tag, CultureInfo.InvariantCulture));
        }
    }
}
