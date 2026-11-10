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

/// <summary>
/// Two text files side by side, line numbers on each side: red lines only on the left, green only on the right,
/// amber changed. Alt+Down / Alt+Up (or F8 / Shift+F8) jump between blocks of changes; Esc closes.
/// </summary>
public sealed partial class DiffWindow : Window
{
    public DiffWindow(DiffViewModel viewModel, string name)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = Loc.Format("Diff_Title", name) + " - Plain Commander";
        TitleText.Text = Loc.Format("Diff_Title", name);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        Ui.ApplyIcon(this);
        Ui.FollowAppTheme(this, Root);
        TitleRow.Loaded += (_, _) => UpdateCaptionPadding();
        TitleRow.SizeChanged += (_, _) => UpdateCaptionPadding();
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(1500, work.Width * 90 / 100);
        var height = Math.Min(900, work.Height * 85 / 100);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
        Root.Loaded += OnLoaded;
        Ui.OnFirstActivation(this, () => Ui.FocusRow(List));
    }

    public DiffViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Diff failed");
        }

        List.Focus(FocusState.Programmatic);
        Jump(forward: true, from: -1);
    }

    private void UpdateCaptionPadding()
    {
        var scale = TitleRow.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private void Jump(bool forward, int? from = null)
    {
        var index = ViewModel.FindChange(from ?? List.SelectedIndex, forward);
        if (index < 0)
        {
            return;
        }

        List.SelectedIndex = index;

        // a few lines of context above the change
        List.ScrollIntoView(ViewModel.Rows[Math.Max(0, index - 3)], ScrollIntoViewAlignment.Leading);
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => Jump(forward: false);

    private void OnNext(object sender, RoutedEventArgs e) => Jump(forward: true);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Close();
                break;
            case VirtualKey.F8:
                Jump(forward: !shift);
                break;
            case VirtualKey.Down when alt:
                Jump(forward: true);
                break;
            case VirtualKey.Up when alt:
                Jump(forward: false);
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
