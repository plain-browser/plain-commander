using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.Core.Settings;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>Settings: two sections, every change applies at once, settings.json is written when the window closes.</summary>
public sealed partial class SettingsWindow : Window
{
    private bool _ready;

    public SettingsWindow(MainViewModel main)
    {
        ViewModel = new SettingsViewModel(main);
        InitializeComponent();
        Title = Loc.Get("Settings_Title/Text") + " - Plain Commander";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        Ui.ApplyIcon(this);
        TitleRow.Loaded += (_, _) => UpdateCaptionPadding();
        TitleRow.SizeChanged += (_, _) => UpdateCaptionPadding();
        Ui.FollowAppTheme(this, Root);

        // Initial values go in by hand once the templates are applied: a TwoWay x:Bind on an editable
        // ComboBox or a NumberBox pushes the control's empty default back into the view model, and values
        // set before Loaded are reset when the editable ComboBox builds its text box.
        Root.Loaded += (_, _) =>
        {
            ThemeBox.SelectedIndex = (int)ViewModel.Theme;
            LanguageBox.SelectedIndex = Math.Max(0, SettingsViewModel.Languages.ToList().IndexOf(ViewModel.Language));
            FontBox.Text = ViewModel.PanelFont;
            SizeBox.Value = ViewModel.PanelFontSize;
            ParallelismBox.Value = ViewModel.CopyParallelism;
            _ready = true;
        };

        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(860, work.Width * 70 / 100);
        var height = Math.Min(640, work.Height * 75 / 100);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
        Closed += (_, _) => ViewModel.Save();
    }

    public SettingsViewModel ViewModel { get; }

    private void UpdateCaptionPadding()
    {
        var scale = TitleRow.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        LookPage.Visibility = tag == "behaviour" ? Visibility.Collapsed : Visibility.Visible;
        BehaviourPage.Visibility = tag == "behaviour" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && ThemeBox.SelectedIndex >= 0)
        {
            ViewModel.Theme = (AppTheme)ThemeBox.SelectedIndex;
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && LanguageBox.SelectedIndex >= 0)
        {
            ViewModel.Language = SettingsViewModel.Languages[LanguageBox.SelectedIndex];
        }
    }

    private void OnFontSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        ViewModel.PanelFont = args.Text;
        args.Handled = true;
    }

    private void OnFontSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && FontBox.SelectedItem is string family)
        {
            ViewModel.PanelFont = family;
        }
    }

    private void OnFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(args.NewValue))
        {
            ViewModel.PanelFontSize = args.NewValue;
        }
    }

    private void OnParallelismChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_ready && !double.IsNaN(args.NewValue))
        {
            ViewModel.CopyParallelism = (int)Math.Round(args.NewValue);
        }
    }

    private async void OnBrowseEditor(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".cmd");
        picker.FileTypeFilter.Add(".bat");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFileAsync() is { } file)
        {
            ViewModel.Editor = file.Path;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && FocusManager.GetFocusedElement(Content.XamlRoot) is not ComboBox { IsDropDownOpen: true })
        {
            e.Handled = true;
            Close();
        }
    }
}
