using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>Alt+F7. Enter on a result shows it in the active panel; F3 views it; Esc stops a running search, then closes.</summary>
public sealed partial class SearchWindow : Window
{
    private readonly MainViewModel _main;

    public SearchWindow(MainViewModel main, string root)
    {
        _main = main;
        ViewModel = new SearchViewModel(root, Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetService<IIconProvider>(App.Current.Services));
        InitializeComponent();
        Title = Loc.Get("Search_Title/Text") + " - Plain Commander";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        Ui.ApplyIcon(this);
        Ui.FollowAppTheme(this, Root);
        TitleRow.Loaded += (_, _) => UpdateCaptionPadding();
        TitleRow.SizeChanged += (_, _) => UpdateCaptionPadding();
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(1100, work.Width * 75 / 100);
        var height = Math.Min(760, work.Height * 80 / 100);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
        Closed += (_, _) => ViewModel.Dispose();
        Root.Loaded += (_, _) =>
        {
            MasksBox.Focus(FocusState.Programmatic);
            MasksBox.SelectAll();
        };
    }

    public SearchViewModel ViewModel { get; }

    private void UpdateCaptionPadding()
    {
        var scale = TitleRow.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartAsync();
        if (ViewModel.Results.Count > 0 && ViewModel.Error is null)
        {
            ResultList.SelectedIndex = Math.Max(ResultList.SelectedIndex, 0);
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Stop();

    /// <summary>"To panel": the active panel lists the results; this window closes.</summary>
    private void OnFeed(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Results.Count == 0 || ViewModel.LastCriteria is not { } criteria)
        {
            return;
        }

        ViewModel.Stop();
        var what = criteria.HasText ? $"{criteria.Masks} \u201E{criteria.Text}\u201D" : criteria.Masks;
        _main.ShowSearchResults(what, criteria.Root, ViewModel.Results.Select(r => r.Entry).ToList());
        Close();
    }

    private void OnAfterChanged(DatePicker sender, DatePickerSelectedValueChangedEventArgs args) => ViewModel.ModifiedAfter = args.NewDate;

    private void OnBeforeChanged(DatePicker sender, DatePickerSelectedValueChangedEventArgs args) => ViewModel.ModifiedBefore = args.NewDate;

    private void OnClearDates(object sender, RoutedEventArgs e)
    {
        AfterPicker.SelectedDate = null;
        BeforePicker.SelectedDate = null;
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileItem item)
        {
            _ = GoToAsync(item);
        }
    }

    private async void OnResultKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ResultList.SelectedItem is not FileItem item)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                await GoToAsync(item);
                break;
            case VirtualKey.F3:
                e.Handled = true;
                if (!item.IsDirectory)
                {
                    // Results inside archives are unpacked to a temp file first, like F3 in a panel.
                    WindowFront.Bring(new ViewerWindow(await MainViewModel.MaterializeAsync(item)));
                }

                break;
        }
    }

    /// <summary>The active panel opens the folder with the cursor on the found item; the search window stays for the next pick.</summary>
    private async Task GoToAsync(FileItem item)
    {
        var folder = System.IO.Path.GetDirectoryName(item.Entry.FullPath);
        if (folder is null)
        {
            return;
        }

        await _main.ActivePanel.NavigateAsync(folder, item.Name);
        _main.RequestPanelFocus();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (ViewModel.IsRunning)
                {
                    ViewModel.Stop();
                }
                else
                {
                    Close();
                }

                break;
            case VirtualKey.Enter when FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox:
                e.Handled = true;
                OnStart(sender, e);
                break;
        }
    }
}
