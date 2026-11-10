using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.Core.Model;
using PlainCommander.Core.Navigation;
using PlainCommander.Presentation;
using Serilog;
using Windows.System;
using Windows.UI.Core;

namespace PlainCommander.App.Views;

/// <summary>Where and for what the Explorer menu should open. Anchor is in window DIPs; null means the mouse position.</summary>
public sealed record ContextMenuRequest(IReadOnlyList<string> Paths, Windows.Foundation.Point? Anchor);

public sealed partial class FilePanel : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(PanelViewModel), typeof(FilePanel), new PropertyMetadata(null, OnViewModelChanged));

    private readonly Stopwatch _renderWatch = new();
    private bool? _brief;
    private double _width = double.MaxValue;
    private Core.Details.DetailColumn _detail;
    private bool _showAttributes;
    private ColumnLayout _layout = ColumnLayout.Full; // what the header and template currently show

    /// <summary>Set by the main window: opens a typed or chosen "sftp://…" address in this panel (home when no path).</summary>
    public Func<string, Task<bool>>? OpenAddress { get; set; }

    /// <summary>Set by the main window: the servers with a live login, listed under the drives.</summary>
    public Func<IReadOnlyList<string>>? OpenServers { get; set; }

    /// <summary>Files were dropped on this panel (from the other panel, this one, or Explorer); the window runs the copy.</summary>
    public event EventHandler<DropRequest>? DropRequested;

    /// <summary>The panel a drag inside the app started in; custom data does not leave the process, so this is enough.</summary>
    private static FilePanel? s_dragSource;

    private const string PathsFormat = "PlainCommander.Paths";

    /// <summary>Shift seen while hovering with the button down: the drop itself reports no modifiers.</summary>
    private bool _dropMove;

    private static bool ShiftHeld(DragEventArgs e) =>
        e.Modifiers.HasFlag(Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.Shift) || IsDown(VirtualKey.Shift);

    /// <summary>
    /// A drag starts on a row: the marked items when that row is marked, else the row. Inside the app the paths travel as
    /// a property; for Explorer and the desktop, files on disk are also offered as storage items (copied there, never
    /// moved: only Copy is offered, so a drop elsewhere cannot take files away behind the panel's back).
    /// </summary>
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.OfType<FileItem>().FirstOrDefault(static i => !i.Entry.IsParentLink) is not { } dragged)
        {
            e.Cancel = true;
            return;
        }

        var items = dragged.IsMarked ? ViewModel.MarkedItems : [dragged];
        var paths = items.Select(static i => i.Entry.FullPath).ToList();
        e.Data.Properties[PathsFormat] = string.Join('\n', paths);
        e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        s_dragSource = this;
        if (paths.All(static p => !PlainCommander.Core.Remote.RemotePath.IsRemote(p) && PlainCommander.Core.Archives.ArchiveFileSystem.Locate(p) is null))
        {
            e.Data.SetDataProvider(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var storage = new List<Windows.Storage.IStorageItem>();
                    foreach (var path in paths)
                    {
                        storage.Add(System.IO.Directory.Exists(path)
                            ? await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path)
                            : await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                    }

                    request.SetData(storage);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Cannot offer dragged files to another program");
                }
                finally
                {
                    deferral.Complete();
                }
            });
        }
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (ReferenceEquals(s_dragSource, this))
        {
            s_dragSource = null;
        }
    }

    /// <summary>The folder a drop lands in: the folder row under the pointer ("..": the parent), else the panel's folder.</summary>
    private string DropTarget(DragEventArgs e)
    {
        // OriginalSource of a drag over a ListView is the list itself, not the row: hit-test at the pointer instead
        foreach (var element in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(e.GetPosition(null), List))
        {
            if (element is ListViewItem { Content: FileItem item })
            {
                return item.IsDirectory ? item.Entry.FullPath : ViewModel.Path;
            }
        }

        return ViewModel.Path;
    }

    private static string[]? InternalPaths(Windows.ApplicationModel.DataTransfer.DataPackageView view) =>
        view.Properties.TryGetValue(PathsFormat, out var value) && value is string text ? text.Split('\n') : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var inside = InternalPaths(e.DataView);
        var outside = inside is null && e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        if ((inside is null && !outside) || ViewModel.IsShowingResults || string.IsNullOrEmpty(ViewModel.Path))
        {
            return;
        }

        var target = DropTarget(e);
        if (inside is not null
            && ((ReferenceEquals(s_dragSource, this) && string.Equals(target, ViewModel.Path, StringComparison.OrdinalIgnoreCase))
                || inside.Any(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase))))
        {
            return; // onto its own folder, or a folder onto itself
        }

        // Always "Copy" toward the source: a move is done here, after the usual question (Shift), never by the sender.
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        // The last DragOver comes with the button already up and no modifiers; only a real hover decides the move.
        var buttons = Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.LeftButton
            | Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.RightButton
            | Windows.ApplicationModel.DataTransfer.DragDrop.DragDropModifiers.MiddleButton;
        if ((e.Modifiers & buttons) != 0)
        {
            _dropMove = ShiftHeld(e);
        }

        var move = _dropMove;
        e.DragUIOverride.Caption = Helpers.Loc.Format(move ? "Drop_Move" : "Drop_Copy", FolderName(target));
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var target = DropTarget(e);
        var move = _dropMove || ShiftHeld(e);
        _dropMove = false;
        PanelViewModel? from = null;
        IReadOnlyList<string> paths;
        if (InternalPaths(e.DataView) is { } inside)
        {
            paths = inside;
            from = s_dragSource?.ViewModel;
        }
        else if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            var deferral = e.GetDeferral();
            try
            {
                var storage = await e.DataView.GetStorageItemsAsync();
                paths = storage.Select(static i => i.Path).Where(static p => !string.IsNullOrEmpty(p)).ToList();
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Cannot read dropped files");
                return;
            }
            finally
            {
                deferral.Complete(); // before the question: Explorer waits for the drop to finish
            }
        }
        else
        {
            return;
        }

        s_dragSource = null;
        if (paths.Count > 0)
        {
            DropRequested?.Invoke(this, new DropRequest(paths, target, move, from));
        }
    }

    private static string FolderName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = PlainCommander.Core.Remote.RemotePath.IsRemote(trimmed) ? PlainCommander.Core.Remote.RemotePath.NameOf(trimmed) : System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>"Connect to a server…" at the bottom of the drive menu.</summary>
    public event EventHandler? ConnectRequested;

    public FilePanel()
    {
        InitializeComponent();
        // A click anywhere in the panel makes it the active one; Tab is handled by the window. Focus alone proves
        // nothing: when a navigation or refresh removes the focused row, WinUI parks focus on the next control it finds
        // (the other panel's breadcrumb or list), and reports it as "keyboard" or "pointer" after the last input device,
        // not after anything the user did. Until 1.13.7 that parked focus activated the other panel, so Backspace after
        // Enter walked the wrong side.
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ActivatedByUser?.Invoke(this, EventArgs.Empty)), true);
        GotFocus += (_, e) =>
        {
            var state = (e.OriginalSource as Control)?.FocusState;
            Log.Verbose("{Panel} got focus ({State}) on {Source}, active: {Active}", Name, state, e.OriginalSource?.GetType().Name, ViewModel?.IsActive);
            if (ViewModel?.IsActive == false && (IsDown(VirtualKey.LeftButton) || IsDown(VirtualKey.RightButton)))
            {
                // A row takes focus while the click is still on its way up to the panel's PointerPressed handler, so
                // the panel is not active yet; with a button down this is the user, not WinUI parking focus.
                ActivatedByUser?.Invoke(this, EventArgs.Empty);
            }
            else if (ViewModel?.IsActive == false && CanTakeFocus?.Invoke() != false)
            {
                Log.Debug("Focus strayed into the inactive {Panel} ({Source}); returning it", Name, e.OriginalSource?.GetType().Name);
                FocusStrayed?.Invoke(this, EventArgs.Empty);
            }
            else if (ViewModel?.IsActive == true && e.OriginalSource is ListViewItem)
            {
                ListFocused?.Invoke(this, EventArgs.Empty);
            }
        };
        // A click gives the row focus "by pointer", and WinUI draws no focus frame for that: the cursor moved but
        // nothing shows where it is. Once the click is done the row gets the same focus the arrow keys give it.
        List.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ShowFrameAfterClick)), true);
        List.LayoutUpdated += OnListLayoutUpdated;
        List.SizeChanged += OnListSizeChanged;
    }

    private const double NarrowWidth = 520;

    /// <summary>Below this even the narrow layout leaves the name no room: attributes go.</summary>
    private const double TinyWidth = 360;

    /// <summary>Raised on a click in the panel (and by its own controls when they act), so the window can make it active.</summary>
    public event EventHandler? ActivatedByUser;

    /// <summary>Raised when WinUI moved focus into this inactive panel on its own; the window sends it back.</summary>
    public event EventHandler? FocusStrayed;

    /// <summary>A row of this panel got focus while the panel is active (keys held meanwhile can go out).</summary>
    public event EventHandler? ListFocused;

    /// <summary>Set by the window: Esc in the list empties the command line; false when it was empty already.</summary>
    public Func<bool>? ClearCommandLine { get; set; }

    /// <summary>
    /// Set by the window: false while another window of the app is in front. Refreshes then keep the cursor
    /// in step without moving keyboard focus, which would bring the main window forward.
    /// </summary>
    public Func<bool>? CanTakeFocus { get; set; }

    public PanelViewModel ViewModel
    {
        get => (PanelViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Puts keyboard focus on the row under the cursor.</summary>
    public void FocusList()
    {
        var index = List.SelectedIndex >= 0 ? List.SelectedIndex : 0;
        FocusRow(index);
    }

    /// <summary>Focus back on the row at once when it exists (no visible flash elsewhere), the usual way otherwise.</summary>
    public void FocusListNow()
    {
        var index = List.SelectedIndex >= 0 ? List.SelectedIndex : 0;
        if (CanTakeFocus?.Invoke() != false && List.ContainerFromIndex(index) is ListViewItem row && row.Focus(FocusState.Keyboard))
        {
            return;
        }

        FocusRow(index);
    }

    /// <summary>Right click or Shift+F10: Explorer's menu for these paths at this client point (DIPs), or at the mouse when null.</summary>
    public event EventHandler<ContextMenuRequest>? ContextMenuRequested;

    /// <summary>Raised by the panel menu's view toggles: the window applies them to both panels.</summary>
    public event EventHandler<ViewOption>? ViewOptionRequested;

    /// <summary>Ctrl+L: the breadcrumb becomes a text box with the full path selected.</summary>
    public void BeginPathEdit()
    {
        PathBox.Text = ViewModel.Path;
        PathBox.Visibility = Visibility.Visible;
        Crumbs.Visibility = Visibility.Collapsed;
        // A control that was collapsed has no layout yet and refuses focus; lay it out first, then focus the
        // inner text box and select the whole path.
        PathBox.UpdateLayout();
        var box = FindTextBox(PathBox);
        var focused = box is not null ? box.Focus(FocusState.Programmatic) : PathBox.Focus(FocusState.Programmatic);
        box?.SelectAll();
        if (!focused)
        {
            Log.Debug("Path box did not take focus (inner box {Found})", box is not null);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                var inner = FindTextBox(PathBox);
                (inner as Control ?? PathBox).Focus(FocusState.Programmatic);
                inner?.SelectAll();
            });
        }
    }

    private void EndPathEdit(bool focusList)
    {
        if (PathBox.Visibility == Visibility.Collapsed)
        {
            return;
        }

        PathBox.Visibility = Visibility.Collapsed;
        Crumbs.Visibility = Visibility.Visible;
        if (focusList)
        {
            FocusList();
        }
    }

    private static TextBox? FindTextBox(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBox box)
            {
                return box;
            }

            if (FindTextBox(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>A click beside the crumbs (not on one) starts editing, like Explorer's address bar.</summary>
    private void OnPathAreaTapped(object sender, TappedRoutedEventArgs e)
    {
        if (PathBox.Visibility == Visibility.Visible)
        {
            return;
        }

        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is BreadcrumbBarItem)
            {
                return; // the crumb handles its own click
            }
        }

        ActivatedByUser?.Invoke(this, EventArgs.Empty);
        BeginPathEdit();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handler")]
    private void OnPathBoxTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = PathCompletion.Suggest(sender.Text);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "XAML event handler")]
    private void OnPathSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args) =>
        sender.Text = args.SelectedItem as string ?? sender.Text;

    private async void OnPathSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var target = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        if (target.Length == 0)
        {
            EndPathEdit(focusList: true);
            return;
        }

        if (target.Length > 3)
        {
            target = System.IO.Path.TrimEndingDirectorySeparator(target); // suggestions end with one; "C:\" keeps it
        }

        var opened = PlainCommander.Core.Remote.RemotePath.IsRemote(target) && OpenAddress is { } open
            ? await open(target)
            : await ViewModel.NavigateAsync(target);
        if (opened)
        {
            EndPathEdit(focusList: true);
        }
        else
        {
            FindTextBox(PathBox)?.SelectAll(); // not a folder: stay and let the user fix it
        }
    }

    private void OnPathBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !PathBox.IsSuggestionListOpen)
        {
            e.Handled = true;
            EndPathEdit(focusList: true);
        }
    }

    private void OnPathBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Focus moving into the suggestion list is still "ours"; anything else ends the edit.
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused, PathBox))
        {
            return;
        }

        EndPathEdit(focusList: false);
    }

    private static bool IsInside(DependencyObject node, DependencyObject root)
    {
        for (; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>History (Alt+↓) and favorites (Ctrl+D) drop down from the path, like the drive menu from its button.</summary>
    public void ShowPathMenu(MenuFlyout menu) => menu.ShowAt(Crumbs);

    /// <summary>Alt+F1 / Alt+F2: the drive button's menu, from the keyboard.</summary>
    public async void ShowDriveMenu()
    {
        await ViewModel.RefreshDrivesAsync();
        _driveMenuIsFresh = true;
        DriveFlyout.ShowAt(DriveButton);
    }

    /// <summary>The drive list was read just before the menu opens: no second read behind the open menu.</summary>
    private bool _driveMenuIsFresh;

    /// <summary>What the drive menu shows now; the open menu is rebuilt only when this changes.</summary>
    private string _driveMenuContent = string.Empty;

    private MenuFlyoutPresenter? _driveMenuPresenter;

    /// <summary>Opening by mouse: show what we know now, then refresh the drive list behind it.</summary>
    private async void OnDriveFlyoutOpening(object sender, object e)
    {
        FillDriveFlyout();
        if (_driveMenuIsFresh)
        {
            _driveMenuIsFresh = false;
            return;
        }

        await ViewModel.RefreshDrivesAsync();
        if (DriveMenuContent() == _driveMenuContent)
        {
            return;
        }

        // Replacing the items takes the keyboard away from the one that had it: focus left the menu, which stayed
        // open and deaf to letters and arrows. The item in the same place gets it back.
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as MenuFlyoutItemBase;
        var index = focused is null ? -1 : DriveFlyout.Items.IndexOf(focused);
        var state = (focused as Control)?.FocusState ?? FocusState.Unfocused;
        FillDriveFlyout();
        if (index >= 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (DriveFlyout.IsOpen && DriveFlyout.Items.Count > 0)
                {
                    DriveFlyout.Items[Math.Min(index, DriveFlyout.Items.Count - 1)].Focus(state == FocusState.Unfocused ? FocusState.Programmatic : state);
                }
            });
        }
    }

    /// <summary>
    /// Letters are heard on the menu itself, not on its items: a menu opened by mouse has no focused item, and neither
    /// has one whose items were just replaced.
    /// </summary>
    private void OnDriveFlyoutOpened(object sender, object e)
    {
        DependencyObject? element = DriveFlyout.Items.FirstOrDefault();
        while (element is not null and not MenuFlyoutPresenter)
        {
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }

        if (element is MenuFlyoutPresenter presenter && !ReferenceEquals(presenter, _driveMenuPresenter))
        {
            _driveMenuPresenter = presenter;
            presenter.PreviewKeyDown += OnDriveMenuKeyDown;
        }
    }

    private string DriveMenuContent() =>
        string.Join('\n', ViewModel.Drives.Select(d => $"{d.Root}|{d.MenuText}|{d.IsCurrent}").Concat(OpenServers?.Invoke() ?? []));

    private void FillDriveFlyout()
    {
        _driveMenuContent = DriveMenuContent();
        DriveFlyout.Items.Clear();
        foreach (var drive in ViewModel.Drives)
        {
            var item = new MenuFlyoutItem
            {
                Text = drive.MenuText,
                Icon = new FontIcon { Glyph = Helpers.Ui.DriveGlyph(drive.Drive) },
                FontWeight = drive.IsCurrent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            var root = drive.Root;
            item.Click += async (_, _) =>
            {
                await ViewModel.ChangeDriveAsync(root);
                FocusList();
            };
            DriveFlyout.Items.Add(item);
        }

        // the local network: computers, then their shared folders
        DriveFlyout.Items.Add(new MenuFlyoutSeparator());
        var network = new MenuFlyoutItem
        {
            Text = Helpers.Loc.Get("Drv_Network"),
            Icon = new FontIcon { Glyph = "\uE968" },
            FontWeight = ViewModel.IsNetworkList || PlainCommander.Core.Network.NetworkPath.UncServer(ViewModel.Path) is not null ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        network.Click += async (_, _) =>
        {
            await ViewModel.NavigateAsync(PlainCommander.Core.Network.NetworkPath.Root);
            FocusList();
        };
        DriveFlyout.Items.Add(network);

        // servers: the ones open now, then the connection manager
        DriveFlyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var root in OpenServers?.Invoke() ?? [])
        {
            var server = new MenuFlyoutItem
            {
                Text = ServerLabel(root),
                Icon = new FontIcon { Glyph = "\uE774" },
                FontWeight = string.Equals(ViewModel.CurrentRoot, root, StringComparison.OrdinalIgnoreCase) ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            };
            ToolTipService.SetToolTip(server, root);
            server.Click += async (_, _) =>
            {
                if (OpenAddress is { } open)
                {
                    await open(root);
                }

                FocusList();
            };
            DriveFlyout.Items.Add(server);
        }

        var connect = new MenuFlyoutItem
        {
            Text = Helpers.Loc.Get("Rmt_ConnectMenu"),
            Icon = new FontIcon { Glyph = "\uE710" },
            KeyboardAcceleratorTextOverride = "Ctrl+F",
        };
        connect.Click += (_, _) => ConnectRequested?.Invoke(this, EventArgs.Empty);
        DriveFlyout.Items.Add(connect);
    }

    /// <summary>"jan@host" (and ":port" when it is not the protocol's usual one), "· FTP" for the protocol.</summary>
    private static string ServerLabel(string root)
    {
        if (PlainCommander.Core.Remote.RemotePath.Parse(root) is not { } at)
        {
            return root;
        }

        var who = string.IsNullOrEmpty(at.User) ? at.Host : $"{at.User}@{at.Host}";
        var port = at.Port == PlainCommander.Core.Remote.RemotePath.DefaultPort(at.Protocol) ? string.Empty : $":{at.Port}";
        return $"{who}{port}  ·  {PlainCommander.Core.Remote.RemotePath.Scheme(at.Protocol).ToUpperInvariant()}";
    }

    private async void OnCloseResults(object sender, RoutedEventArgs e)
    {
        await ViewModel.CloseResultsAsync();
        FocusList();
    }

    /// <summary>In the drive menu a letter jumps straight to that drive: Alt+F1, D opens D:\.</summary>
    private async void OnDriveMenuKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is < VirtualKey.A or > VirtualKey.Z || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)
            || ViewModel.FindDrive((char)('A' + (e.Key - VirtualKey.A))) is not { } drive)
        {
            return;
        }

        e.Handled = true;
        DriveFlyout.Hide();
        await ViewModel.ChangeDriveAsync(drive.Root);
        FocusList();
    }

    private async void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is PathSegment segment)
        {
            await ViewModel.NavigateAsync(segment.FullPath, focusName: FolderBelow(segment));
            FocusList();
        }
    }

    /// <summary>Going up the breadcrumb puts the cursor on the folder we came from, as Backspace does.</summary>
    private string? FolderBelow(PathSegment segment)
    {
        var segments = ViewModel.PathSegments;
        var index = segments.ToList().IndexOf(segment);
        return index >= 0 && index + 1 < segments.Count ? segments[index + 1].Name : null;
    }

    private async void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        // ItemsRepeater does not set DataContext for compiled-binding templates; the tab travels in Tag.
        if ((sender as FrameworkElement)?.Tag is PanelTab tab)
        {
            ActivatedByUser?.Invoke(this, EventArgs.Empty);
            await ViewModel.SwitchTabAsync(ViewModel.Tabs.IndexOf(tab));
            FocusList();
        }
    }

    /// <summary>The × on a tab. Handled here so the click does not also switch to the tab that is being closed.</summary>
    private async void OnTabCloseTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is PanelTab tab)
        {
            await ViewModel.CloseTabAsync(ViewModel.Tabs.IndexOf(tab));
            FocusList();
        }
    }

    private async void OnGoRoot(object sender, RoutedEventArgs e)
    {
        ActivatedByUser?.Invoke(this, EventArgs.Empty);
        await ViewModel.GoRootAsync();
        FocusList();
    }

    private async void OnGoUp(object sender, RoutedEventArgs e)
    {
        ActivatedByUser?.Invoke(this, EventArgs.Empty);
        await ViewModel.GoUpAsync();
        FocusList();
    }

    /// <summary>A drive pill on the tab strip: the same as picking the drive from the drive menu.</summary>
    private async void OnDrivePillTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DriveItem drive)
        {
            ActivatedByUser?.Invoke(this, EventArgs.Empty);
            await ViewModel.ChangeDriveAsync(drive.Root);
            FocusList();
        }
    }

    // The filter box: typing filters the list by a name fragment or masks after a short pause. It edits only the
    // name part of the panel filter, so dates or sizes set with Ctrl+S stay; Esc clears it, Enter or Down goes to the list.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterTimer;
    private bool _syncingFilterBox;

    private void OnFilterBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingFilterBox)
        {
            return;
        }

        if (_filterTimer is null)
        {
            _filterTimer = DispatcherQueue.CreateTimer();
            _filterTimer.Interval = TimeSpan.FromMilliseconds(250);
            _filterTimer.IsRepeating = false;
            _filterTimer.Tick += async (_, _) => await ApplyFilterBoxAsync();
        }

        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async Task ApplyFilterBoxAsync()
    {
        _filterTimer?.Stop();
        var text = FilterBox.Text.Trim();
        var current = ViewModel.Filter ?? new PanelFilter();
        if (string.Equals(current.Name, text, StringComparison.Ordinal))
        {
            return;
        }

        await ViewModel.SetFilterAsync(current with { Name = text });
    }

    private async void OnFilterBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (FilterBox.Text.Length > 0)
                {
                    SetFilterBoxText(string.Empty);
                    await ApplyFilterBoxAsync();
                }

                FocusList();
                break;
            case VirtualKey.Enter:
            case VirtualKey.Down:
                e.Handled = true;
                await ApplyFilterBoxAsync();
                FocusList();
                break;
        }
    }

    /// <summary>The filter changed elsewhere (Ctrl+S, Esc in the list, another folder): the box shows its name part.</summary>
    private void SyncFilterBox()
    {
        var name = ViewModel.Filter?.Name ?? string.Empty;
        if (!string.Equals(FilterBox.Text.Trim(), name, StringComparison.Ordinal) && !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), FilterBox))
        {
            SetFilterBoxText(name);
        }
    }

    private void SetFilterBoxText(string text)
    {
        _syncingFilterBox = true;
        FilterBox.Text = text;
        _syncingFilterBox = false;
    }

    private void OnNewTab(object sender, RoutedEventArgs e)
    {
        ViewModel.NewTab();
        FocusList();
    }

    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        await ViewModel.CloseActiveTabAsync();
        FocusList();
    }

    private void OnToggleView(object sender, RoutedEventArgs e)
    {
        ViewModel.BriefView = !ViewModel.BriefView;
        FocusList();
    }

    private void OnToggleHidden(object sender, RoutedEventArgs e) => ViewOptionRequested?.Invoke(this, ViewOption.Hidden);

    private void OnToggleCompact(object sender, RoutedEventArgs e) => ViewOptionRequested?.Invoke(this, ViewOption.CompactRows);

    private void OnToggleAttributes(object sender, RoutedEventArgs e) => ViewOptionRequested?.Invoke(this, ViewOption.Attributes);

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (FilePanel)d;
        if (e.OldValue is PanelViewModel old)
        {
            old.FocusRequested -= panel.OnFocusRequested;
            old.PropertyChanged -= panel.OnViewModelPropertyChanged;
            old.Tabs.CollectionChanged -= panel.OnTabsChanged;
            panel.WatchTabs([]);
        }

        if (e.NewValue is PanelViewModel vm)
        {
            vm.FocusRequested += panel.OnFocusRequested;
            vm.PropertyChanged += panel.OnViewModelPropertyChanged;
            vm.Tabs.CollectionChanged += panel.OnTabsChanged;
            panel.WatchTabs(vm.Tabs);
            panel.ApplyViewMode(vm.BriefView);
            panel.ApplyDensity(vm.CompactRows);
            panel.ApplyAttributes(vm.ShowAttributes);
            panel.ApplyDetail(vm.Detail);
        }
    }

    private readonly HashSet<PanelTab> _watchedTabs = [];

    /// <summary>Each tab tells when it becomes the active one, so the strip can scroll to it.</summary>
    private void WatchTabs(IEnumerable<PanelTab> tabs)
    {
        foreach (var tab in _watchedTabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _watchedTabs.Clear();
        foreach (var tab in tabs)
        {
            if (_watchedTabs.Add(tab))
            {
                tab.PropertyChanged += OnTabPropertyChanged;
            }
        }
    }

    private void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        WatchTabs(ViewModel.Tabs);
        BringActiveTabIntoViewSoon();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelTab.IsActive) && sender is PanelTab { IsActive: true })
        {
            BringActiveTabIntoViewSoon();
        }
    }

    /// <summary>After layout: a new tab has no element yet when the collection reports it.</summary>
    private void BringActiveTabIntoViewSoon() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, BringActiveTabIntoView);

    private void BringActiveTabIntoView()
    {
        var index = ViewModel is { } vm ? vm.Tabs.ToList().FindIndex(static t => t.IsActive) : -1;
        if (index < 0)
        {
            return;
        }

        TabScroller.UpdateLayout();
        if (TabsRepeater.TryGetElement(index) is not FrameworkElement tab)
        {
            return;
        }

        var left = tab.TransformToVisual(TabsRepeater).TransformPoint(default).X;
        var right = left + tab.ActualWidth;
        var offset = TabScroller.HorizontalOffset;
        var width = TabScroller.ViewportWidth;
        const double Edge = 16; // clear of the "more tabs" marks
        if (left - Edge < offset)
        {
            TabScroller.ChangeView(Math.Max(0, left - Edge), null, null, disableAnimation: true);
        }
        else if (right + Edge > offset + width)
        {
            TabScroller.ChangeView(right + Edge - width, null, null, disableAnimation: true);
        }

        UpdateTabMarks();
    }

    /// <summary>The scroller may use the strip's width minus "+" (the Auto column would let it grow without end).</summary>
    private void OnTabStripSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TabScroller.MaxWidth = Math.Max(0, e.NewSize.Width - NewTabButton.Width - NewTabButton.Margin.Left - NewTabButton.Margin.Right);
        BringActiveTabIntoViewSoon();
    }

    private void OnTabScrollerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateTabMarks();

    /// <summary>Chevrons at the edges when tabs are hidden that side.</summary>
    private void UpdateTabMarks()
    {
        var offset = TabScroller.HorizontalOffset;
        TabsMoreLeft.Visibility = offset > 1 ? Visibility.Visible : Visibility.Collapsed;
        TabsMoreRight.Visibility = offset + TabScroller.ViewportWidth < TabScroller.ExtentWidth - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The header of the detail column follows the panel; the rows size their own column from their item.</summary>
    private void ApplyDetail(Core.Details.DetailColumn detail)
    {
        _detail = detail;
        ColumnHeader.ColumnDefinitions[5].Width = Helpers.Ui.DetailWidth(detail);
        DetailHeader.Visibility = detail == Core.Details.DetailColumn.None ? Visibility.Collapsed : Visibility.Visible;
        UpdateColumns();
    }

    private void OnSortByDetail(object sender, RoutedEventArgs e) => SortBy(SortColumn.Detail);

    private void OnDetailDimensions(object sender, RoutedEventArgs e) => ToggleDetail(Core.Details.DetailColumn.Dimensions);

    private void OnDetailTaken(object sender, RoutedEventArgs e) => ToggleDetail(Core.Details.DetailColumn.Taken);

    private void OnDetailDuration(object sender, RoutedEventArgs e) => ToggleDetail(Core.Details.DetailColumn.Duration);

    private void OnDetailVersion(object sender, RoutedEventArgs e) => ToggleDetail(Core.Details.DetailColumn.Version);

    private void ToggleDetail(Core.Details.DetailColumn column)
    {
        ViewModel.Detail = ViewModel.Detail == column ? Core.Details.DetailColumn.None : column;
        FocusList();
    }

    private void ApplyDensity(bool compact) =>
        List.ItemContainerStyle = (Style)Resources[compact ? "RowStyleCompact" : "RowStyle"];

    /// <summary>The attributes column shows only when asked for and the panel is wide enough.</summary>
    private void ApplyAttributes(bool show)
    {
        _showAttributes = show;
        UpdateColumns();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PanelViewModel.IsShowingResults):
                Crumbs.Visibility = ViewModel.IsShowingResults ? Visibility.Collapsed : Visibility.Visible;
                break;
            case nameof(PanelViewModel.BriefView):
                ApplyViewMode(ViewModel.BriefView);
                break;
            case nameof(PanelViewModel.Items):
                _renderWatch.Restart();
                break;
            case nameof(PanelViewModel.CompactRows):
                ApplyDensity(ViewModel.CompactRows);
                break;
            case nameof(PanelViewModel.ShowAttributes):
                ApplyAttributes(ViewModel.ShowAttributes);
                break;
            case nameof(PanelViewModel.Detail):
                ApplyDetail(ViewModel.Detail);
                break;
            case nameof(PanelViewModel.Filter):
                SyncFilterBox();
                break;
        }
    }

    private void OnListLayoutUpdated(object? sender, object e)
    {
        if (_renderWatch.IsRunning)
        {
            _renderWatch.Stop();
            Log.Debug("Rendered {Count} items in {Elapsed} ms", ViewModel?.Items.Count, _renderWatch.ElapsedMilliseconds);
        }
    }

    private void ShowFrameAfterClick()
    {
        if (XamlRoot is not null && ViewModel?.IsActive == true && FocusManager.GetFocusedElement(XamlRoot) is ListViewItem { FocusState: FocusState.Pointer } row)
        {
            row.Focus(FocusState.Keyboard);
        }
    }

    private void OnFocusRequested(object? sender, int index)
    {
        if (index < 0 || index >= List.Items.Count)
        {
            return;
        }

        List.SelectedIndex = index;
        List.ScrollIntoView(List.Items[index]);
        // typing in the filter box refreshes the list after each pause: the box keeps the keyboard
        if (ViewModel.IsActive && !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), FilterBox))
        {
            FocusRow(index);
        }
    }

    private void FocusRow(int index)
    {
        if (index < 0 || index >= List.Items.Count)
        {
            List.Focus(FocusState.Programmatic);
            return;
        }

        // The row container may not exist yet right after the source changed; let layout run first.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (CanTakeFocus?.Invoke() == false)
            {
                Log.Verbose("{Panel} FocusRow {Index}: cannot take focus now", Name, index);
                return; // focusing a row would pull the main window over the viewer, search or rename window
            }

            if (List.ContainerFromIndex(index) is null)
            {
                List.UpdateLayout(); // rows of a list that was just replaced (rename, refresh) are built by a layout pass
            }

            if (List.ContainerFromIndex(index) is ListViewItem row)
            {
                var ok = row.Focus(FocusState.Keyboard);
                Log.Verbose("{Panel} FocusRow {Index}: row focus {Result}", Name, index, ok);
            }
            else
            {
                var ok = List.Focus(FocusState.Programmatic);
                Log.Verbose("{Panel} FocusRow {Index}: no container, list focus {Result}", Name, index, ok);
                FocusRowWhenBuilt(index);
            }
        });
    }

    /// <summary>The row was not there yet: focus on the list alone shows no frame, so the row gets it once it exists.</summary>
    private void FocusRowWhenBuilt(int index)
    {
        void Handler(object? sender, object e)
        {
            if (List.ContainerFromIndex(index) is not ListViewItem row)
            {
                return; // wait for the pass that builds it
            }

            List.LayoutUpdated -= Handler;
            if (CanTakeFocus?.Invoke() != false && ViewModel?.IsActive == true && ViewModel.CurrentIndex == index)
            {
                row.Focus(FocusState.Keyboard);
            }
        }

        List.LayoutUpdated += Handler;
    }

    private void ApplyViewMode(bool brief)
    {
        if (_brief == brief)
        {
            return;
        }

        var first = _brief is null;
        _brief = brief;
        if (first && !brief)
        {
            return; // XAML already set up the full view; swapping the panel needlessly costs a re-layout
        }

        // Scroll direction first: if the wrap grid ever sees an unbounded height it stops virtualizing
        // and realizes every row (100k rows = gigabytes and a frozen UI).
        ScrollViewer.SetHorizontalScrollMode(List, brief ? ScrollMode.Enabled : ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(List, brief ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollMode(List, brief ? ScrollMode.Disabled : ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(List, brief ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        List.UpdateLayout();
        List.ItemTemplate = CurrentTemplate();
        List.ItemsPanel = (ItemsPanelTemplate)Resources[brief ? "BriefPanel" : "FullPanel"];
        if (ViewModel is { } vm)
        {
            OnFocusRequested(this, vm.CurrentIndex);
            RefocusAfterLayout(vm);
        }
    }

    /// <summary>
    /// A new items panel builds its rows on the next layout pass and drops the old ones, focused row included;
    /// WinUI then moved focus to the command line (Ctrl+F1/F2 left the keyboard there). Focus the row once it exists.
    /// </summary>
    private void RefocusAfterLayout(PanelViewModel vm)
    {
        if (!vm.IsActive)
        {
            return;
        }

        void Handler(object? sender, object e)
        {
            List.LayoutUpdated -= Handler;
            if (List.ContainerFromIndex(vm.CurrentIndex) is ListViewItem row)
            {
                row.Focus(FocusState.Keyboard);
            }
            else
            {
                List.Focus(FocusState.Programmatic);
            }
        }

        List.LayoutUpdated += Handler;
    }

    /// <summary>
    /// Full: every column. NarrowAttributes: a narrow panel showing attributes keeps the extension in the name and
    /// the date without the time. NoAttributes: attributes off, or too little room even for the narrow layout.
    /// NarrowDetail: the detail column in a narrow panel without attributes, again with the extension in the name and
    /// a short date, so the name keeps some room.
    /// </summary>
    private enum ColumnLayout
    {
        Full,
        NarrowAttributes,
        NoAttributes,
        NarrowDetail,
    }

    private DataTemplate CurrentTemplate() => (DataTemplate)Resources[_brief == true ? "BriefTemplate" : _layout switch
    {
        ColumnLayout.Full => "FullTemplate",
        ColumnLayout.NarrowAttributes => "FullNarrowAttrTemplate",
        ColumnLayout.NarrowDetail => "FullNarrowDetailTemplate",
        _ => "FullNarrowTemplate",
    }];

    private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _width = e.NewSize.Width;
        UpdateColumns();
    }

    /// <summary>The detail column takes its width plus the gap from the name, so the layouts switch that much earlier.</summary>
    private double DetailRoom => _detail == Core.Details.DetailColumn.None ? 0 : Helpers.Ui.DetailWidth(_detail).Value + 6;

    private ColumnLayout PickLayout()
    {
        var room = DetailRoom;
        if (_showAttributes && _width >= TinyWidth + room)
        {
            return _width >= NarrowWidth + room ? ColumnLayout.Full : ColumnLayout.NarrowAttributes;
        }

        return room > 0 && _width < NarrowWidth ? ColumnLayout.NarrowDetail : ColumnLayout.NoAttributes;
    }

    private void UpdateColumns()
    {
        var layout = PickLayout();
        if (layout == _layout)
        {
            return;
        }

        _layout = layout;
        var attributes = layout is ColumnLayout.Full or ColumnLayout.NarrowAttributes;
        var extension = layout is ColumnLayout.Full or ColumnLayout.NoAttributes;
        AttrHeader.Visibility = attributes ? Visibility.Visible : Visibility.Collapsed;
        ExtHeader.Visibility = extension ? Visibility.Visible : Visibility.Collapsed;
        ColumnHeader.ColumnDefinitions[2].Width = extension ? (GridLength)Resources["ColExt"] : new GridLength(0);
        ColumnHeader.ColumnDefinitions[4].Width = (GridLength)Resources[extension ? "ColDate" : "ColDateShort"];
        ColumnHeader.ColumnDefinitions[6].Width = attributes ? (GridLength)Resources["ColAttr"] : new GridLength(0);
        // A zero-width column still gets ColumnSpacing. At the right end (no attributes) that pushes the other
        // columns left, so the padding gives it back; the empty extension column only narrows the name.
        ColumnHeader.Padding = new Thickness(12, 3, attributes ? 22 : 16, 3);
        if (_brief != true)
        {
            List.ItemTemplate = CurrentTemplate();
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedIndex >= 0)
        {
            ViewModel.CurrentIndex = List.SelectedIndex;
        }
    }

    /// <summary>
    /// Right click: a marked item means the menu is for all marked items, an unmarked one gets the cursor and
    /// the menu for itself, empty space (or "..") means the folder.
    /// </summary>
    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        ActivatedByUser?.Invoke(this, EventArgs.Empty);
        var item = (e.OriginalSource as FrameworkElement)?.DataContext as FileItem;
        if (item is { Entry.IsParentLink: false })
        {
            var index = ViewModel.Items.ToList().IndexOf(item);
            if (index >= 0)
            {
                List.SelectedIndex = index;
                ViewModel.CurrentIndex = index;
            }
        }

        e.Handled = true;
        ContextMenuRequested?.Invoke(this, new ContextMenuRequest(PathsFor(item), null));
    }

    private string[] PathsFor(FileItem? item)
    {
        if (item is null || item.Entry.IsParentLink)
        {
            return string.IsNullOrEmpty(ViewModel.Path) ? [] : [ViewModel.Path];
        }

        var marked = ViewModel.MarkedItems;
        var set = item.IsMarked && marked.Count > 0 ? marked : [item];
        return set.Select(static i => i.Entry.FullPath).ToArray();
    }

    private void RequestContextMenuFromKeyboard()
    {
        var item = ViewModel.CurrentItem;
        Windows.Foundation.Point? anchor = null;
        if (ViewModel.CurrentIndex >= 0 && List.ContainerFromIndex(ViewModel.CurrentIndex) is FrameworkElement row)
        {
            anchor = row.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(row.ActualWidth / 3, row.ActualHeight));
        }

        ContextMenuRequested?.Invoke(this, new ContextMenuRequest(PathsFor(item), anchor));
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: FileItem item })
        {
            e.Handled = true;
            _ = ViewModel.OpenAsync(item);
        }
    }

    private void OnListPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var vm = ViewModel;
        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        var searching = vm.IsQuickSearchActive;

        switch (e.Key)
        {
            case VirtualKey.Application:
            case VirtualKey.F10 when IsDown(VirtualKey.Shift):
                e.Handled = true;
                vm.ClearQuickSearch();
                RequestContextMenuFromKeyboard();
                break;
            case VirtualKey.Enter when !ctrl && !alt:
                e.Handled = true;
                vm.ClearQuickSearch();
                _ = vm.OpenCurrentAsync();
                break;
            case VirtualKey.Back when searching:
                e.Handled = true;
                vm.QuickSearchBackspace();
                break;
            case VirtualKey.Back when !ctrl && !alt:
                e.Handled = true;
                _ = vm.GoUpAsync();
                break;
            case VirtualKey.Insert when !ctrl && !alt:
                e.Handled = true;
                vm.ClearQuickSearch();
                vm.MarkCurrentAndAdvance();
                break;
            case VirtualKey.Space when !ctrl && !alt:
                e.Handled = true;
                vm.ClearQuickSearch();
                _ = vm.MarkCurrentAndMeasureAsync();
                break;
            case VirtualKey.Escape when searching:
                e.Handled = true;
                vm.ClearQuickSearch();
                break;
            case VirtualKey.Escape when vm.Filter is not null:
                e.Handled = true;
                _ = vm.SetFilterAsync(default(PlainCommander.Core.Navigation.PanelFilter)); // Esc takes the Ctrl+S filter off
                break;
            case VirtualKey.Escape when !ctrl && !alt && ClearCommandLine?.Invoke() == true:
                e.Handled = true; // the name inserted with Ctrl+Enter goes again
                break;
            case VirtualKey.Down or VirtualKey.Up when searching && !ctrl:
                e.Handled = true;
                vm.QuickSearchNext(forward: e.Key == VirtualKey.Down);
                break;
            case VirtualKey.PageUp when ctrl:
                e.Handled = true;
                _ = vm.GoUpAsync();
                break;
            case VirtualKey.PageDown when ctrl:
                e.Handled = true;
                _ = vm.EnterCurrentDirectoryAsync();
                break;
            case VirtualKey.Space when ctrl && !alt:
                e.Handled = true; // the list would take the selection off the row: no cursor, and the next command jumps to the top
                break;
            case VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Left or VirtualKey.Right
                when searching:
                vm.ClearQuickSearch(); // leave the key to the list
                break;
        }

        if (!e.Handled && ctrl && !alt && e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
        {
            // With Ctrl the list moves only the focus frame and leaves the selection (our cursor) behind: the frame
            // showed one row while Enter, F5 or F8 acted on another. The cursor follows the frame.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SelectFocusedRow);
        }
    }

    private void SelectFocusedRow()
    {
        if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is ListViewItem row
            && List.IndexFromContainer(row) is var index && index >= 0 && List.SelectedIndex != index)
        {
            List.SelectedIndex = index;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        var c = e.Character;
        if (char.IsControl(c) || c == ' ')
        {
            return;
        }

        if (CanTakeFocus?.Invoke() == false)
        {
            e.Handled = true; // a dialog is opening: the key is held for it and typed again there, not searched for here
            return;
        }

        // Numpad + − * / are selection commands handled by the window, not search text.
        if (IsDown(VirtualKey.Add) || IsDown(VirtualKey.Subtract) || IsDown(VirtualKey.Multiply) || IsDown(VirtualKey.Divide))
        {
            e.Handled = true;
            return;
        }

        // Ctrl or Alt alone means a shortcut; both together is AltGr (ą, ę, ł… on a Polish keyboard).
        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        if (ctrl != alt)
        {
            return;
        }

        e.Handled = true;
        ViewModel.QuickSearchType(c);
    }

    private void OnSortByName(object sender, RoutedEventArgs e) => SortBy(SortColumn.Name);

    private void OnSortByExtension(object sender, RoutedEventArgs e) => SortBy(SortColumn.Extension);

    private void OnSortBySize(object sender, RoutedEventArgs e) => SortBy(SortColumn.Size);

    /// <summary>The date header sorts by the date it shows (creation date once sorted by it).</summary>
    private void OnSortByDate(object sender, RoutedEventArgs e) => SortBy(ViewModel.ShowsCreated ? SortColumn.Created : SortColumn.Modified);

    private void OnSortByModified(object sender, RoutedEventArgs e) => SortBy(SortColumn.Modified);

    private void OnSortByCreated(object sender, RoutedEventArgs e) => SortBy(SortColumn.Created);

    private void OnSortByAttributes(object sender, RoutedEventArgs e) => SortBy(SortColumn.Attributes);

    private async void SortBy(SortColumn column)
    {
        await ViewModel.SortByAsync(column);
        FocusList();
    }
}

/// <summary>What was dropped where: files, the target folder, Shift held (move), and the panel the drag came from (null: Explorer).</summary>
public sealed record DropRequest(IReadOnlyList<string> Paths, string Target, bool Move, PanelViewModel? From);
