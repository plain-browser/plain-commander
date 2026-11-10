using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.Core.Compare;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>
/// After Shift+F2: every difference with the direction it will be copied. A click on the arrow or Space cycles
/// → ← nothing; the Right/Left arrow keys pick a direction; F3 or Enter shows the text differences of two files.
/// "Synchronize" queues one operation and closes the window; Esc closes without changes.
/// </summary>
public sealed partial class SyncWindow : Window
{
    private readonly MainViewModel _main;

    public SyncWindow(MainViewModel main, SyncViewModel viewModel)
    {
        _main = main;
        ViewModel = viewModel;
        InitializeComponent();
        Title = Loc.Get("Sync_Title") + " - Plain Commander";
        TitleText.Text = Loc.Format("Sync_TitleCount", viewModel.Rows.Count);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        Ui.ApplyIcon(this);
        Ui.FollowAppTheme(this, Root);
        TitleRow.Loaded += (_, _) => UpdateCaptionPadding();
        TitleRow.SizeChanged += (_, _) => UpdateCaptionPadding();
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(1100, work.Width * 80 / 100);
        var height = Math.Min(760, work.Height * 80 / 100);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
        List.SelectionChanged += (_, _) => DiffButton.IsEnabled = Selected is { CanDiff: true };
        Root.Loaded += (_, _) =>
        {
            if (ViewModel.Rows.Count > 0)
            {
                List.SelectedIndex = 0;
            }

            DiffButton.IsEnabled = Selected is { CanDiff: true };
            List.Focus(FocusState.Programmatic);
        };
        Ui.OnFirstActivation(this, () => Ui.FocusRow(List));
    }

    public SyncViewModel ViewModel { get; }

    private SyncRow? Selected => List.SelectedItem as SyncRow;

    private void UpdateCaptionPadding()
    {
        var scale = TitleRow.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private void OnCycle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SyncRow row })
        {
            row.Cycle();
            List.SelectedItem = row;
        }
    }

    private void OnDefaults(object sender, RoutedEventArgs e) => ViewModel.ResetDefaults();

    private void OnAllRight(object sender, RoutedEventArgs e) => ViewModel.SetAll(SyncAction.CopyToRight);

    private void OnAllLeft(object sender, RoutedEventArgs e) => ViewModel.SetAll(SyncAction.CopyToLeft);

    private void OnNone(object sender, RoutedEventArgs e) => ViewModel.SetAll(SyncAction.None);

    private void OnShowDiff(object sender, RoutedEventArgs e) => ShowDiff();

    private void OnListDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => ShowDiff();

    private void ShowDiff()
    {
        if (Selected is not { CanDiff: true } row)
        {
            return;
        }

        var window = new DiffWindow(_main.CreateDiff(row), row.Relative);
        WindowFront.Bring(window);
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        Close(); // the operation shows its own progress; the panels refresh when it ends
        try
        {
            await _main.RunSyncAsync(ViewModel);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Synchronization failed");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Selected is not { } row)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Space:
                row.Cycle();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                row.Action = SyncAction.CopyToRight;
                e.Handled = true;
                break;
            case VirtualKey.Left:
                row.Action = SyncAction.CopyToLeft;
                e.Handled = true;
                break;
            case VirtualKey.Back or VirtualKey.Delete:
                row.Action = SyncAction.None;
                e.Handled = true;
                break;
            case VirtualKey.Enter or VirtualKey.F3:
                ShowDiff();
                e.Handled = true;
                break;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
