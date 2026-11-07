using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>
/// Ctrl+F: saved FTP/SFTP connections. Fields save as they are left; "Connect" (or Enter, or a double click) opens the
/// selected one, or the typed address, in the active panel and closes the window. Esc closes.
/// </summary>
public sealed partial class ConnectionsWindow : Window
{
    private readonly MainViewModel _main;
    private bool _filling;

    public ConnectionsWindow(MainViewModel main, ConnectionsViewModel viewModel)
    {
        _main = main;
        ViewModel = viewModel;
        InitializeComponent();
        Title = Loc.Get("Con_Title") + " - Plain Commander";
        TitleText.Text = Loc.Get("Con_Title");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        Ui.ApplyIcon(this);
        Ui.FollowAppTheme(this, Root);
        TitleRow.Loaded += (_, _) => UpdateCaptionPadding();
        TitleRow.SizeChanged += (_, _) => UpdateCaptionPadding();
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min(900, work.Width * 80 / 100);
        var height = Math.Min(640, work.Height * 80 / 100);
        AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
        ViewModel.PropertyChanged += OnViewModelChanged;
        Root.Loaded += (_, _) =>
        {
            ShowSelected();
            if (ViewModel.Sites.Count == 0)
            {
                QuickBox.Focus(FocusState.Programmatic);
            }
            else
            {
                List.Focus(FocusState.Programmatic);
            }
        };
        Closed += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.RemoveEmpty();
            _main.SaveSettings();
        };
    }

    public ConnectionsViewModel ViewModel { get; }

    private void UpdateCaptionPadding()
    {
        var scale = TitleRow.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionsViewModel.Selected))
        {
            ShowSelected();
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => ShowState();

    private SiteRow? _watched;

    /// <summary>The combo box and check box are filled here (not TwoWay x:Bind), and the parts that depend on the protocol shown.</summary>
    private void ShowSelected()
    {
        if (_watched is not null)
        {
            _watched.PropertyChanged -= OnRowChanged;
        }

        _watched = ViewModel.Selected;
        if (_watched is not null)
        {
            _watched.PropertyChanged += OnRowChanged;
        }

        _filling = true;
        try
        {
            var row = ViewModel.Selected;
            Form.Visibility = Ui.VisibleIf(row is not null);
            EmptyHint.Visibility = Ui.VisibleIf(row is null);
            if (row is not null)
            {
                ProtocolBox.SelectedIndex = row.ProtocolIndex;
                ActiveBox.IsChecked = row.ActiveMode;
                HttpBox.IsChecked = row.PlainHttp;
            }
        }
        finally
        {
            _filling = false;
        }

        ShowState();
    }

    private void ShowState()
    {
        if (ViewModel.Selected is not { } row)
        {
            return;
        }

        KeyRow.Visibility = Ui.VisibleIf(row.IsSftp);
        ActiveBox.Visibility = Ui.VisibleIf(!row.IsSftp && !row.IsS3);
        RegionBox.Visibility = HttpBox.Visibility = S3Hint.Visibility = Ui.VisibleIf(row.IsS3);
        PlainWarning.Visibility = Ui.VisibleIf(row.IsUnencrypted);
        PasswordState.Text = Loc.Get(row.HasPassword ? "Con_PasswordSaved" : "Con_PasswordNotSaved");
        ForgetButton.Visibility = Ui.VisibleIf(row.HasPassword);
    }

    private void OnProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && ViewModel.Selected is { } row && ProtocolBox.SelectedIndex >= 0)
        {
            row.ProtocolIndex = ProtocolBox.SelectedIndex;
        }
    }

    private void OnHttpChanged(object sender, RoutedEventArgs e)
    {
        if (!_filling && ViewModel.Selected is { } row)
        {
            row.PlainHttp = HttpBox.IsChecked == true;
            ShowState();
        }
    }

    private void OnActiveChanged(object sender, RoutedEventArgs e)
    {
        if (!_filling && ViewModel.Selected is { } row)
        {
            row.ActiveMode = ActiveBox.IsChecked == true;
        }
    }

    private async void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        if (await picker.PickSingleFileAsync() is { } file && ViewModel.Selected is { } row)
        {
            row.KeyFile = file.Path;
        }
    }

    private void OnNew(object sender, RoutedEventArgs e) => ViewModel.Add();

    private void OnDuplicate(object sender, RoutedEventArgs e) => ViewModel.Duplicate();

    private void OnDelete(object sender, RoutedEventArgs e) => ViewModel.Delete();

    private void OnForget(object sender, RoutedEventArgs e)
    {
        ViewModel.ForgetPassword();
        ShowState();
    }

    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Connect();

    private void OnConnect(object sender, RoutedEventArgs e) => Connect();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void Connect()
    {
        if (ViewModel.Address is not { } address)
        {
            return;
        }

        Close();

        // the login dialog opens in the main window, which must have the keyboard when it does
        _main.RequestPanelFocus();
        try
        {
            await _main.ConnectAsync(address);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Connect failed");
        }

        _main.RequestPanelFocus();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;

            // Enter connects from the list and the address box; in the form it only commits the field
            case VirtualKey.Enter when FocusManager.GetFocusedElement(Root.XamlRoot) is ListViewItem || ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), QuickBox):
                e.Handled = true;
                Connect();
                break;
        }
    }
}
