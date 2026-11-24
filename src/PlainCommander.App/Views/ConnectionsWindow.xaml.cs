using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.Core.Remote;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;

namespace PlainCommander.App.Views;

/// <summary>
/// Ctrl+F: saved FTP/SFTP/S3 connections; S3 has a form of its own (service, keys, bucket). Fields save as they are left; "Connect" (or Enter, or a double click) opens the
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
        foreach (var provider in S3Endpoints.Listed)
        {
            ProviderBox.Items.Add(provider == S3Provider.Other ? Loc.Get("Con_S3ProviderOther") : S3Endpoints.Name(provider));
        }

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
        var height = Math.Min(720, work.Height * 85 / 100);
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
            CommitSecret(_watched);
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
            CommitSecret(_watched); // a secret key typed for the row being left
            SecretBox.Password = string.Empty;
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
                ProviderBox.SelectedIndex = row.ProviderIndex;
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

        ServerPanel.Visibility = PasswordRow.Visibility = Ui.VisibleIf(!row.IsS3);
        S3Panel.Visibility = Ui.VisibleIf(row.IsS3);
        PlainWarning.Visibility = Ui.VisibleIf(row.IsUnencrypted);
        PlainWarning.Message = Loc.Get(row.IsS3 ? "Rmt_PlainHttp" : "Con_PlainFtp");
        if (row.IsS3)
        {
            ShowS3(row);
            return;
        }

        KeyRow.Visibility = Ui.VisibleIf(row.IsSftp);
        ActiveBox.Visibility = Ui.VisibleIf(!row.IsSftp);
        PasswordState.Text = Loc.Get(row.HasPassword ? "Con_PasswordSaved" : "Con_PasswordNotSaved");
        ForgetButton.Visibility = Ui.VisibleIf(row.HasPassword);
    }

    /// <summary>
    /// The S3 form follows the service: Amazon is a region (the address comes from it), the other known services an
    /// address, a server of one's own also a port, a region and plain HTTP.
    /// </summary>
    private void ShowS3(SiteRow row)
    {
        _filling = true;
        try
        {
            ProviderBox.SelectedIndex = row.ProviderIndex; // read from the address when it was pasted
            HttpBox.IsChecked = row.PlainHttp;
        }
        finally
        {
            _filling = false;
        }

        EndpointRow.Visibility = Ui.VisibleIf(!row.IsAws);
        EndpointBox.PlaceholderText = row.IsOwnServer ? Loc.Get("Con_S3EndpointOwn") : S3Endpoints.Example(row.Provider);
        S3PortBox.Visibility = HttpBox.Visibility = Ui.VisibleIf(row.IsOwnServer);
        RegionBox.Visibility = Ui.VisibleIf(row.IsAws || row.IsOwnServer);
        RegionBox.PlaceholderText = Loc.Get(row.IsAws ? "Con_S3RegionAws" : "Con_S3RegionOwn");
        AwsAddress.Visibility = Ui.VisibleIf(row.IsAws);
        AwsAddress.Text = Loc.Format("Con_S3Address", row.Site.Host);
        SecretBox.PlaceholderText = Loc.Get(row.HasPassword ? "Con_S3SecretChange" : "Con_S3SecretEmpty");
        SecretState.Text = Loc.Get(row.User.Length == 0 ? "Con_S3Anonymous" : row.HasPassword ? "Con_S3SecretSaved" : "Con_S3SecretNotSaved");
        ForgetSecretButton.Visibility = Ui.VisibleIf(row.HasPassword);
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && ViewModel.Selected is { } row && ProviderBox.SelectedIndex >= 0)
        {
            row.ProviderIndex = ProviderBox.SelectedIndex;
        }
    }

    /// <summary>At Amazon the region box offers the regions that match what is typed; any other can still be typed.</summary>
    private void OnRegionTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            SuggestRegions(sender.Text);
        }
    }

    private void OnRegionGotFocus(object sender, RoutedEventArgs e)
    {
        if (RegionBox.Text.Length == 0)
        {
            SuggestRegions(string.Empty);
        }
    }

    private void SuggestRegions(string typed)
    {
        var regions = ViewModel.Selected is { IsAws: true }
            ? S3Endpoints.AwsRegions.Where(r => r.Contains(typed.Trim(), StringComparison.OrdinalIgnoreCase) && !string.Equals(r, typed.Trim(), StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        RegionBox.ItemsSource = regions;
        RegionBox.IsSuggestionListOpen = regions.Count > 0;
    }

    private void OnSecretLostFocus(object sender, RoutedEventArgs e)
    {
        CommitSecret(ViewModel.Selected);
        ShowState();
    }

    /// <summary>The secret key typed in the form goes to the Credential Manager and leaves the box.</summary>
    private void CommitSecret(SiteRow? row)
    {
        if (row is { IsS3: true } && SecretBox.Password.Length > 0 && ViewModel.SaveSecret(row, SecretBox.Password))
        {
            SecretBox.Password = string.Empty;
        }
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

        CommitSecret(ViewModel.Selected);

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
