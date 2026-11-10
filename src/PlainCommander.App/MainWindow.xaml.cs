using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.App.Views;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using PlainCommander.Presentation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace PlainCommander.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<(VirtualKeyModifiers Modifiers, VirtualKey Key), Shortcut> _shortcuts;
    private readonly Services.DialogService _dialogs;
    private bool _isForeground = true;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _modifierTimer;

    public MainWindow()
    {
        ViewModel = App.Current.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
        _dialogs = (Services.DialogService)App.Current.Services.GetRequiredService<IDialogService>();
        _dialogs.Owner = this;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        BuildTopMenu();
        ViewModel.Left.PropertyChanged += OnLeftPanelChanged;
        ViewModel.Right.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PanelViewModel.Detail))
            {
                RefreshDetailChecks();
            }
        };
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActivePanel))
            {
                RefreshDetailChecks();
            }
        };
        Helpers.Ui.ApplyIcon(this);
        RestorePlacement(ViewModel.Settings.Window);
        ApplySplit(ViewModel.Settings.PanelSplit);
        Splitter.LeftColumn = LeftColumn;
        Splitter.RightColumn = RightColumn;
        // A flyout or dialog opened by a chord (Alt+F1) swallows the KeyUp of the modifier, so while a
        // modifier is shown on the action bar we poll the keyboard state until it is released.
        _modifierTimer = DispatcherQueue.CreateTimer();
        _modifierTimer.Interval = TimeSpan.FromMilliseconds(100);
        _modifierTimer.Tick += (_, _) => SyncModifiers();
        ApplyTheme();
        ViewModel.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Theme))
            {
                ApplyTheme();
                // rows take their colours from code when they are realized: re-read the lists to repaint them
                await Task.WhenAll(ViewModel.Left.RefreshAsync(), ViewModel.Right.RefreshAsync());
            }
        };
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        AppTitleBar.Loaded += (_, _) =>
        {
            UpdateCaptionPadding();
            ApplyMinimumSize();
            AppTitleBar.XamlRoot.Changed += (_, _) => ApplyMinimumSize(); // another monitor's scale
        };
        AppTitleBar.SizeChanged += (_, _) => UpdateCaptionPadding();
        TopMenu.SizeChanged += (_, _) => FitTitleBar(); // the menu's width depends on the language
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                FunctionKeys.Modifiers = VirtualKeyModifiers.None; // no KeyUp arrives after Alt+Tab
            }
        };

        // While a dialog is open or closing, focus wanders: WinUI parks it on the first control of the window
        // (the left panel's breadcrumb) when a ContentDialog closes, which made the left panel active after
        // F5, F7, F8… in the right one. Only focus outside dialogs picks the active panel.
        LeftPanel.ActivatedByUser += (_, _) =>
        {
            if (!_dialogs.IsOpen)
            {
                ActivatePanel(ViewModel.Left);
            }
        };
        RightPanel.ActivatedByUser += (_, _) =>
        {
            if (!_dialogs.IsOpen)
            {
                ActivatePanel(ViewModel.Right);
            }
        };
        LeftPanel.FocusStrayed += (_, _) => ReturnStrayFocus();
        RightPanel.FocusStrayed += (_, _) => ReturnStrayFocus();
        ViewModel.PanelFocusRequested += (_, _) =>
        {
            Helpers.WindowFront.Bring(this);
            ActivePanelView.FocusList();
        };
        LeftPanel.ContextMenuRequested += (_, request) => ShowShellMenu(request);
        RightPanel.ContextMenuRequested += (_, request) => ShowShellMenu(request);
        LeftPanel.ViewOptionRequested += (_, option) => ToggleViewOption(option);
        RightPanel.ViewOptionRequested += (_, option) => ToggleViewOption(option);
        AppWindow.Closing += OnAppWindowClosing;
        _dialogs.CanReplayKeys = () => _isForeground;
        _dialogs.AllClosed += (_, _) => ReplayKeysAfterDialog();
        _strayKeyTimer = DispatcherQueue.CreateTimer();
        _strayKeyTimer.Interval = TimeSpan.FromMilliseconds(150);
        _strayKeyTimer.IsRepeating = false;
        _strayKeyTimer.Tick += (_, _) => ReplayStrayKeys(tick: true);
        LeftPanel.ListFocused += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ReplayStrayKeys(tick: false));
        RightPanel.ListFocused += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ReplayStrayKeys(tick: false));
        Root.Loaded += OnLoaded;

        // Panels move keyboard focus only while this window is in front (see FilePanel.CanTakeFocus); when it
        // comes back (after Word or another program was closed), the active panel's row gets focus again unless
        // nothing needs it: a text box keeps its caret. Focus that is missing, rests on the list itself or on a row
        // that does not show the frame (it was last focused by pointer, or the window took it away) is set anew.
        Activated += (_, e) =>
        {
            _isForeground = e.WindowActivationState != WindowActivationState.Deactivated;
            if (_isForeground && Content.XamlRoot is { } root && !_dialogs.IsOpen && NeedsRowFocus(FocusManager.GetFocusedElement(root)))
            {
                Serilog.Log.Debug("Window activated with focus on {Source}; focusing the active panel's row", FocusManager.GetFocusedElement(root)?.GetType().Name ?? "nothing");
                ActivePanelView.FocusList();
            }
        };
        // ... and never while a dialog is open: a row focused behind a ContentDialog takes its keyboard
        // (the login dialog after Ctrl+F lost its password typing to the panel this way)
        LeftPanel.CanTakeFocus = () => _isForeground && !_dialogs.IsOpen && !_openingWindow;
        RightPanel.CanTakeFocus = () => _isForeground && !_dialogs.IsOpen && !_openingWindow;
        _openingTimer = DispatcherQueue.CreateTimer();
        _openingTimer.Interval = TimeSpan.FromSeconds(3);
        _openingTimer.IsRepeating = false;
        _openingTimer.Tick += (_, _) => WindowOpened(null, replay: false);
        foreach (var (view, panel) in new[] { (LeftPanel, ViewModel.Left), (RightPanel, ViewModel.Right) })
        {
            // Esc in the list clears the command line once there is no quick search or filter left to take off
            view.ClearCommandLine = () =>
            {
                if (string.IsNullOrEmpty(ViewModel.CommandLine.Text))
                {
                    return false;
                }

                ViewModel.CommandLine.Text = string.Empty;
                return true;
            };
            view.OpenServers = () => ViewModel.Remote.OpenRoots;
            view.OpenAddress = async address =>
            {
                ViewModel.Activate(panel);
                var ok = await ViewModel.ConnectAsync(address);
                ActivePanelView.FocusList();
                return ok;
            };
            view.DropRequested += (_, drop) =>
            {
                // a drop from Explorer leaves another window in front; the question must get the keyboard
                Helpers.WindowFront.Bring(this);
                if (drop.From is null)
                {
                    ViewModel.Activate(panel);
                }

                Run(() => ViewModel.DropAsync(drop.Paths, drop.Target, drop.Move, drop.From));
            };
            view.ConnectRequested += (_, _) =>
            {
                ViewModel.Activate(panel);
                OpenConnections();
            };
        }

        FunctionKeys.KeyClicked += (_, n) => OnFunctionKey(n);
        ViewModel.Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationQueueViewModel.IsDialogVisible) && ViewModel.Operations.IsDialogVisible)
            {
                // The progress card takes the keyboard: Tab walks its buttons, Esc sends it to the background.
                // Never while a conflict/error dialog is up: that would pull the keyboard out of the dialog.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (!_dialogs.IsOpen && ViewModel.Operations.IsDialogVisible)
                    {
                        BackgroundButton.Focus(FocusState.Programmatic);
                    }
                });
            }
        };

        // Window-wide shortcuts in one table. Panel-local keys
        // (Enter, Backspace, Insert, Space, quick search) live in FilePanel.
        // "Typing" shortcuts are skipped while a text box has focus, so Del and Num+ still edit text there.
        _shortcuts = new()
        {
            [(VirtualKeyModifiers.None, VirtualKey.Tab)] = new(SwitchPanel),
            [(VirtualKeyModifiers.Shift, VirtualKey.Tab)] = new(SwitchPanel),
            [(VirtualKeyModifiers.None, VirtualKey.F1)] = new(ShowKeys),
            [(VirtualKeyModifiers.None, VirtualKey.F2)] = new(ShowMenu),
            [(VirtualKeyModifiers.None, VirtualKey.F3)] = new(ViewCurrent),
            [(VirtualKeyModifiers.None, VirtualKey.F4)] = new(() => Run(ViewModel.EditCurrentAsync)),
            [(VirtualKeyModifiers.Shift, VirtualKey.F4)] = new(() => Run(ViewModel.NewFileAsync)),
            [(VirtualKeyModifiers.Shift, VirtualKey.F2)] = new(Compare),
            [(VirtualKeyModifiers.Control, VirtualKey.Q)] = new(ToggleQuickView),
            [(VirtualKeyModifiers.Control, VirtualKey.S)] = new(() => Run(ViewModel.FilterAsync)),
            [(VirtualKeyModifiers.Control, VirtualKey.B)] = new(() => Run(ViewModel.ToggleFlatViewAsync)),
            [(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.C)] = new(() => ViewModel.CopyNamesToClipboard(fullPaths: true)),
            [(VirtualKeyModifiers.Control, VirtualKey.T)] = new(() => { ViewModel.NewTab(); ActivePanelView.FocusList(); }),
            [(VirtualKeyModifiers.Control, VirtualKey.W)] = new(() => Run(ViewModel.CloseTabAsync)),
            [(VirtualKeyModifiers.Control, VirtualKey.Tab)] = new(() => Run(() => ViewModel.NextTabAsync(1))),
            [(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.Tab)] = new(() => Run(() => ViewModel.NextTabAsync(-1))),
            [(VirtualKeyModifiers.Control, VirtualKey.Enter)] = new(InsertName),
            [(VirtualKeyModifiers.Control, VirtualKey.P)] = new(() => { ViewModel.InsertCurrentPath(); FocusCommandLine(); }),
            [(VirtualKeyModifiers.Menu, VirtualKey.F8)] = new(FocusCommandLine),
            [(VirtualKeyModifiers.Menu, VirtualKey.F7)] = new(OpenSearch),
            [(VirtualKeyModifiers.Control, VirtualKey.M)] = new(OpenMultiRename, Typing: true),
            [(VirtualKeyModifiers.Control, VirtualKey.E)] = new(() => Run(ViewModel.EncryptAsync), Typing: true),
            [(VirtualKeyModifiers.Control, VirtualKey.F)] = new(OpenConnections, Typing: true),
            [(VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, VirtualKey.F)] = new(() => Run(ViewModel.DisconnectAsync), Typing: true),
            [(VirtualKeyModifiers.Menu, VirtualKey.Down)] = new(ShowHistory),
            [(VirtualKeyModifiers.Control, VirtualKey.L)] = new(() => ActivePanelView.BeginPathEdit()),
            [(VirtualKeyModifiers.Control, VirtualKey.D)] = new(ShowFavorites),
            [(VirtualKeyModifiers.Control, VirtualKey.U)] = new(() => Run(ViewModel.SwapPanelsAsync)),
            [(VirtualKeyModifiers.Control, VirtualKey.Right)] = new(() => Run(() => ViewModel.MirrorAsync(PanelSide.Right)), Typing: true), // in a text box Ctrl+arrow jumps a word
            [(VirtualKeyModifiers.Control, VirtualKey.Left)] = new(() => Run(() => ViewModel.MirrorAsync(PanelSide.Left)), Typing: true),
            [(VirtualKeyModifiers.Menu, VirtualKey.F1)] = new(() => ShowDriveMenu(ViewModel.Left, LeftPanel)),
            [(VirtualKeyModifiers.Menu, VirtualKey.F2)] = new(() => ShowDriveMenu(ViewModel.Right, RightPanel)),
            [(VirtualKeyModifiers.Control, VirtualKey.F1)] = new(() => SetBrief(true)),
            [(VirtualKeyModifiers.Control, VirtualKey.F2)] = new(() => SetBrief(false)),
            [(VirtualKeyModifiers.Control, VirtualKey.F3)] = new(() => Sort(SortColumn.Name)),
            [(VirtualKeyModifiers.Control, VirtualKey.F4)] = new(() => Sort(SortColumn.Extension)),
            [(VirtualKeyModifiers.Control, VirtualKey.F5)] = new(() => Sort(SortColumn.Modified)),
            [(VirtualKeyModifiers.Control, VirtualKey.F6)] = new(() => Sort(SortColumn.Size)),
            [(VirtualKeyModifiers.Control, VirtualKey.R)] = new(Refresh),
            [(VirtualKeyModifiers.Control, VirtualKey.H)] = new(ToggleHidden),
            [(VirtualKeyModifiers.None, VirtualKey.F5)] = new(() => Run(ViewModel.CopyAsync)),
            [(VirtualKeyModifiers.Menu, VirtualKey.F5)] = new(() => Run(ViewModel.PackAsync)),
            [(VirtualKeyModifiers.Menu, VirtualKey.F9)] = new(() => Run(ViewModel.UnpackAsync)),
            [(VirtualKeyModifiers.Control, VirtualKey.Z)] = new(() => Run(ViewModel.UndoAsync), Typing: true), // in a text box Ctrl+Z undoes typing
            [(VirtualKeyModifiers.None, VirtualKey.F6)] = new(() => Run(ViewModel.MoveAsync)),
            [(VirtualKeyModifiers.Shift, VirtualKey.F6)] = new(() => Run(ViewModel.RenameCurrentAsync)),
            [(VirtualKeyModifiers.None, VirtualKey.F7)] = new(() => Run(ViewModel.MakeDirectoryAsync)),
            [(VirtualKeyModifiers.None, VirtualKey.F8)] = new(() => Run(() => ViewModel.DeleteAsync(permanent: false))),
            [(VirtualKeyModifiers.Shift, VirtualKey.F8)] = new(() => Run(() => ViewModel.DeleteAsync(permanent: true))),
            [(VirtualKeyModifiers.None, VirtualKey.Delete)] = new(() => Run(() => ViewModel.DeleteAsync(permanent: false)), Typing: true),
            [(VirtualKeyModifiers.Shift, VirtualKey.Delete)] = new(() => Run(() => ViewModel.DeleteAsync(permanent: true)), Typing: true),
            [(VirtualKeyModifiers.None, VirtualKey.Add)] = new(() => Run(() => ViewModel.MarkByMaskAsync(mark: true)), Typing: true),
            [(VirtualKeyModifiers.None, VirtualKey.Subtract)] = new(() => Run(() => ViewModel.MarkByMaskAsync(mark: false)), Typing: true),
            [(VirtualKeyModifiers.None, VirtualKey.Multiply)] = new(() => ViewModel.ActivePanel.InvertMarks(), Typing: true),
            [(VirtualKeyModifiers.Control, VirtualKey.A)] = new(() => ViewModel.ActivePanel.MarkAll(), Typing: true),
        };
    }

    public MainViewModel ViewModel { get; }

    private FilePanel ActivePanelView => ReferenceEquals(ViewModel.ActivePanel, ViewModel.Left) ? LeftPanel : RightPanel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        ActivePanelView.FocusList();
    }

    private static VirtualKeyModifiers CurrentModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= VirtualKeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= VirtualKeyModifiers.Menu;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= VirtualKeyModifiers.Shift;
        }

        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        if (_dialogs.IsOpen)
        {
            // Keys belong to the dialog. One typed while it is still opening (or closing) reaches the window, where it
            // would act on the panel behind it; it waits and is typed again once the dialog (or the window) has the keyboard.
            var chord = CurrentModifiers();
            _ = _dialogs.HeldKeys.IsReplayed(e.Key); // a replayed key that meets a new dialog is held once more like any other
            if (!HeldKeys.IsExempt(e.Key, chord) && (_dialogs.IsClosing || Helpers.Ui.IsInside(focused, Root)))
            {
                e.Handled = true;
                _dialogs.HeldKeys.Hold(e.Key, chord);
            }

            return;
        }

        if (_openingWindow)
        {
            if (!HeldKeys.IsExempt(e.Key, CurrentModifiers()))
            {
                e.Handled = true;
                _dialogs.HeldKeys.Hold(e.Key, CurrentModifiers());
            }

            return;
        }

        var replayed = _dialogs.HeldKeys.IsReplayed(e.Key);
        if (!HeldKeys.IsExempt(e.Key, CurrentModifiers()) && (IsStray(focused) || (_dialogs.HeldKeys.Any && !replayed)))
        {
            // WinUI parked focus in the inactive panel (its breadcrumb) after the list changed, or earlier keys still
            // wait: the key belongs to the active panel and goes there, after the ones before it, once its row has focus.
            e.Handled = true;
            if (replayed)
            {
                _dialogs.HeldKeys.HoldAgain(e.Key, CurrentModifiers());
            }
            else
            {
                _dialogs.HeldKeys.Hold(e.Key, CurrentModifiers());
            }

            if (!_strayKeyTimer.IsRunning)
            {
                ActivePanelView.FocusList();
                _strayKeyTimer.Start();
            }

            return;
        }

        if (ViewModel.Operations.IsDialogVisible)
        {
            HandleProgressCardKey(e);
            return; // no panel shortcuts behind the progress card
        }

        if (e.Key == VirtualKey.Escape && ViewModel.IsComparing)
        {
            e.Handled = true;
            ViewModel.CancelCompare();
            return;
        }

        var modifiers = SyncModifiers();
        if (modifiers != VirtualKeyModifiers.None)
        {
            Serilog.Log.Verbose("Key {Modifiers}+{Key}", modifiers, e.Key);
        }

        if (_shortcuts.TryGetValue((modifiers, e.Key), out var shortcut)
            && !(shortcut.Typing && FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox))
        {
            e.Handled = true;
            shortcut.Action();
        }
    }

    private void OnRootPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogs.HeldKeys.SwallowRelease(e.Key))
        {
            e.Handled = true; // its press is held for later; the release alone must not act (it opened the menu bar)
        }

        SyncModifiers();
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _strayKeyTimer;

    /// <summary>Focus in the panel that is not active: nothing the user did puts it there (a click activates the panel).</summary>
    private bool IsStray(DependencyObject? focused)
    {
        if (focused is null || ViewModel.QuickView.IsOpen)
        {
            return false;
        }

        var inactive = ReferenceEquals(ViewModel.ActivePanel, ViewModel.Left) ? RightPanel : LeftPanel;
        return Helpers.Ui.IsInside(focused, inactive);
    }

    /// <summary>The keyboard is on the active panel's list (not its breadcrumb, where Enter would navigate).</summary>
    private bool IsActiveRow(DependencyObject? focused) => focused is ListViewItem or ListViewBase && Helpers.Ui.IsInside(focused, ActivePanelView);

    /// <summary>Timer ticks spent waiting for the panel to settle; held keys are dropped after too many.</summary>
    private int _heldKeyTicks;

    /// <summary>
    /// Keys held while focus was astray (or while a dialog closed) go out one at a time, each once the active panel's row
    /// has the keyboard and its listing is loaded, so Enter, Backspace, Enter… walk folders as they were typed. Keys that
    /// cannot be delivered within about a second (three while a folder loads) are dropped rather than sent elsewhere.
    /// </summary>
    private void ReplayStrayKeys(bool tick)
    {
        if (!_dialogs.HeldKeys.Any || _dialogs.IsOpen || _openingWindow)
        {
            _strayKeyTimer.Stop();
            _heldKeyTicks = 0;
            return; // a dialog replays them itself when it settles
        }

        if (tick)
        {
            _heldKeyTicks++;
        }

        var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        var loading = ViewModel.ActivePanel.IsLoading;
        if (_isForeground && !loading && IsActiveRow(focused))
        {
            _heldKeyTicks = 0;
            _dialogs.HeldKeys.ReplayNext();
            if (_dialogs.HeldKeys.Any)
            {
                _strayKeyTimer.Start(); // the next after this one has acted (its focus change usually comes first)
            }

            return;
        }

        if (_heldKeyTicks > (loading ? 20 : 6) || !_isForeground)
        {
            Serilog.Log.Warning("Focus did not come back to the active panel ({Focused}, loading: {Loading}); held keys dropped", focused?.GetType().Name ?? "nothing", loading);
            _dialogs.HeldKeys.Clear();
            _heldKeyTicks = 0;
            return;
        }

        if (!loading && !IsActiveRow(focused))
        {
            ActivePanelView.FocusList();
        }

        _strayKeyTimer.Start();
    }

    /// <summary>
    /// Keys typed while the last dialog closed: into a text box that has the keyboard again, else to the active panel the
    /// same way as stray keys (WinUI may have parked focus elsewhere when the list changed meanwhile).
    /// </summary>
    private void ReplayKeysAfterDialog()
    {
        if (!_dialogs.HeldKeys.Any || _dialogs.IsOpen || !_isForeground)
        {
            return;
        }

        if (FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox)
        {
            _dialogs.HeldKeys.Replay();
            return;
        }

        ReplayStrayKeys(tick: false);
    }

    private bool _closeConfirmed;
    private bool _askingToClose;
    private bool _saved;

    /// <summary>
    /// Alt+F4 or the close button. With operations running or queued the window stays and asks: keep working, or cancel
    /// them (cleanly, waiting until they stopped) and close. Closing abruptly would leave a copy or a move half done.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_closeConfirmed && ViewModel.Operations.IsBusy)
        {
            args.Cancel = true;
            AskToCloseDuringOperations();
            return;
        }

        SaveOnce();
    }

    private void SaveOnce()
    {
        if (!_saved)
        {
            _saved = true;
            ViewModel.Save(CapturePlacement(), CurrentSplit());
        }
    }

    private async void AskToCloseDuringOperations()
    {
        if (_askingToClose)
        {
            return;
        }

        _askingToClose = true;
        try
        {
            Helpers.WindowFront.Bring(this);
            var running = ViewModel.Operations.Current?.Title ?? string.Empty;
            if (!await _dialogs.ConfirmCloseDuringOperationsAsync(running, ViewModel.Operations.PendingCount))
            {
                Serilog.Log.Information("Close declined: operations keep running");
                ViewModel.Operations.Current?.SendToBackground();
                ActivePanelView.FocusList();
                return;
            }

            Serilog.Log.Information("Closing: cancelling the running and queued operations");
            await ViewModel.Operations.CancelAllAsync();
            Serilog.Log.Information("Operations stopped; closing");
            _closeConfirmed = true;
            SaveOnce();
            Close();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Closing during operations failed");
        }
        finally
        {
            _askingToClose = false;
        }
    }

    private VirtualKeyModifiers SyncModifiers()
    {
        var modifiers = CurrentModifiers();
        FunctionKeys.Modifiers = modifiers;
        if (modifiers == VirtualKeyModifiers.None)
        {
            _modifierTimer.Stop();
        }
        else if (!_modifierTimer.IsRunning)
        {
            _modifierTimer.Start();
        }

        return modifiers;
    }

    /// <summary>Keeps the title bar clear of the system caption buttons (RightInset is in physical pixels).</summary>
    private void UpdateCaptionPadding()
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionPadding.Width = AppWindow.TitleBar.RightInset / scale;
        FitTitleBar();
    }

    /// <summary>Width the app name and version badge take when shown (measured once while visible).</summary>
    private double _appNameWidth;

    /// <summary>
    /// The title bar's columns are Auto, so a window narrower than their sum pushed the menu and the settings button under
    /// the caption buttons. The name and version go first when room is short; a strip to drag the window by stays.
    /// </summary>
    private void FitTitleBar()
    {
        if (AppName.Visibility == Visibility.Visible && AppName.ActualWidth > 0)
        {
            _appNameWidth = AppName.ActualWidth + AppName.Margin.Left + AppName.Margin.Right + AppTitleBar.ColumnSpacing;
        }

        const double DragRoom = 48;
        var needed = AppTitleBar.Padding.Left + AppLogo.Width + TopMenu.ActualWidth + TopMenu.Margin.Left + SettingsButton.Width
            + CaptionPadding.Width + (4 * AppTitleBar.ColumnSpacing) + DragRoom;
        var show = needed + _appNameWidth <= AppTitleBar.ActualWidth;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (AppName.Visibility != visibility)
        {
            AppName.Visibility = visibility;
        }
    }

    /// <summary>Both panels at their minimum width (400 each) with the splitter, padding and some room; below that the layout breaks.</summary>
    private const double MinWindowWidth = 900;

    private const double MinWindowHeight = 480;

    /// <summary>The presenter's minimum is in physical pixels, so it follows the monitor's scale.</summary>
    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && AppTitleBar.XamlRoot is { } root)
        {
            presenter.PreferredMinimumWidth = (int)Math.Ceiling(MinWindowWidth * root.RasterizationScale);
            presenter.PreferredMinimumHeight = (int)Math.Ceiling(MinWindowHeight * root.RasterizationScale);
        }
    }

    /// <summary>Explorer's context menu. Errors from shell extensions are logged, never shown: the menu is theirs.</summary>
    private void ShowShellMenu(ContextMenuRequest request)
    {
        if (request.Paths.Count == 0 || _dialogs.IsOpen || ViewModel.ActivePanel.IsInArchive)
        {
            return; // the shell knows nothing about entries inside an archive
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var (x, y) = request.Anchor is { } anchor
            ? Services.ShellContextMenu.ToScreen(hwnd, anchor.X, anchor.Y, Content.XamlRoot.RasterizationScale)
            : Services.ShellContextMenu.CursorPosition();
        try
        {
            Services.ShellContextMenu.Show(hwnd, request.Paths, x, y);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException)
        {
            Serilog.Log.Warning(ex, "Shell context menu failed for {Paths}", request.Paths);
        }

        ActivePanelView.FocusList();
    }

    /// <summary>Alt+↓: the active panel's visited folders, newest first.</summary>
    private void ShowHistory()
    {
        var panel = ViewModel.ActivePanel;
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var path in panel.History.Reverse().Where(p => !string.Equals(p, panel.Path, StringComparison.OrdinalIgnoreCase)))
        {
            menu.Items.Add(PathItem(path, "\uE81C"));
        }

        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = Loc.Get("History_Empty"), IsEnabled = false });
        }

        ActivePanelView.ShowPathMenu(menu);
    }

    /// <summary>Ctrl+D: favorite folders, plus adding or removing the current one.</summary>
    private void ShowFavorites()
    {
        var panel = ViewModel.ActivePanel;
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        foreach (var path in ViewModel.Favorites)
        {
            menu.Items.Add(PathItem(path, "\uE734"));
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var current = panel.Path;
        if (ViewModel.IsFavorite(current))
        {
            var remove = new MenuFlyoutItem { Text = Loc.Get("Favorites_Remove"), Icon = new FontIcon { Glyph = "\uE74D" } };
            remove.Click += (_, _) => { ViewModel.RemoveFavorite(current); ActivePanelView.FocusList(); };
            menu.Items.Add(remove);
        }
        else
        {
            var add = new MenuFlyoutItem { Text = Loc.Format("Favorites_Add", Helpers.Ui.Prompt(current).TrimEnd(' ', '\u203A')), Icon = new FontIcon { Glyph = "\uE710" } };
            add.Click += (_, _) => { ViewModel.AddFavorite(current); ActivePanelView.FocusList(); };
            menu.Items.Add(add);
        }

        ActivePanelView.ShowPathMenu(menu);
    }

    private MenuFlyoutItem PathItem(string path, string glyph)
    {
        var item = new MenuFlyoutItem { Text = path, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => Run(async () =>
        {
            if (!await ViewModel.ActivePanel.NavigateAsync(path))
            {
                await _dialogs.ShowErrorAsync(Loc.Format("Dlg_FolderGone", path));
            }
        });
        return item;
    }

    private SettingsWindow? _settings;
    private SearchWindow? _search;

    /// <summary>A window of the app is being built and shown; keys typed meanwhile are held for it.</summary>
    private bool _openingWindow;

    private Window? _windowBeingOpened;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _openingTimer;

    /// <summary>
    /// Shows a new window of the app so that nothing typed while it opens is lost. A window that is active before its
    /// first layout swallows keys (about 0.3 s: a name typed straight after Alt+F7 or Ctrl+M vanished), so it is shown
    /// without the keyboard first. Keys typed meanwhile still reach this window, wait here, and are typed again once the
    /// new window has laid out, set its own focus and come to the front.
    /// </summary>
    private void ShowNewWindow(Window window)
    {
        if (window.Content is not FrameworkElement { IsLoaded: false } root)
        {
            Helpers.WindowFront.Bring(window);
            return;
        }

        _openingWindow = true;
        _windowBeingOpened = window;
        _openingTimer.Start();
        root.Loaded += (_, _) => WindowOpened(window, replay: true); // after the window's own handler, which sets its focus
        window.AppWindow.Show(activateWindow: false);
    }

    private void WindowOpened(Window? window, bool replay)
    {
        if (!_openingWindow || (window is not null && !ReferenceEquals(window, _windowBeingOpened)))
        {
            return;
        }

        _openingTimer.Stop();
        _openingWindow = false;
        var opened = _windowBeingOpened;
        _windowBeingOpened = null;
        if (opened is not null)
        {
            Helpers.WindowFront.Bring(opened);
        }

        if (replay)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _dialogs.HeldKeys.Replay());
        }
        else
        {
            _dialogs.HeldKeys.Clear(); // it never finished its layout: better dropped than typed somewhere else
        }
    }

    /// <summary>Alt+F7: one search window, starting in the active panel's folder.</summary>
    private void OpenSearch()
    {
        if (_search is null)
        {
            _search = new SearchWindow(ViewModel, ViewModel.ActivePanel.Path);
            _search.Closed += (_, _) => _search = null;
            ShowNewWindow(_search);
            return;
        }

        Helpers.WindowFront.Bring(_search);
    }

    /// <summary>Ctrl+M on the marked items (or the one under the cursor).</summary>
    private async void OpenMultiRename()
    {
        if (await ViewModel.CreateMultiRenameAsync() is { } session)
        {
            var window = new MultiRenameWindow(ViewModel, session);
            window.Closed += (_, _) => ActivePanelView.FocusList();
            ShowNewWindow(window);
        }
    }

    private ConnectionsWindow? _connections;

    /// <summary>Ctrl+F: the saved FTP/SFTP connections; one window at a time.</summary>
    private void OpenConnections()
    {
        if (_connections is null)
        {
            _connections = new ConnectionsWindow(ViewModel, new ConnectionsViewModel(ViewModel.Settings.RemoteSites, ViewModel.Remote, App.Current.Secrets));
            _connections.Closed += (_, _) =>
            {
                _connections = null;
                BuildTopMenu(); // the Network menu lists the saved connections
                ActivePanelView.FocusList();
            };
            ShowNewWindow(_connections);
            return;
        }

        Helpers.WindowFront.Bring(_connections);
    }

    /// <summary>One settings window at a time; it saves on close.</summary>
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(ViewModel);
            _settings.Closed += (_, _) => _settings = null;
            ShowNewWindow(_settings);
            return;
        }

        Helpers.WindowFront.Bring(_settings);
    }

    /// <summary>F3: the file under the cursor in the viewer; folders are left alone.</summary>
    private async void ViewCurrent()
    {
        if (ViewModel.ActivePanel.CurrentItem is not { IsDirectory: false } item)
        {
            return;
        }

        try
        {
            ShowNewWindow(new ViewerWindow(await ViewModel.ViewPathAsync(item)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
        }
    }

    /// <summary>Ctrl+Enter: the name under the cursor goes to the command line; the panel keeps the keyboard.</summary>
    private void InsertName() => ViewModel.InsertCurrentName();

    private void FocusCommandLine()
    {
        CommandLine.Focus(FocusState.Keyboard);
        CommandLine.SelectionStart = CommandLine.Text.Length;
    }

    /// <summary>Keys inside the command line. Shortcuts with modifiers still go through the window table.</summary>
    private async void OnCommandLineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CurrentModifiers() != VirtualKeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                await ViewModel.RunCommandLineAsync();
                ActivePanelView.FocusList();
                break;
            case VirtualKey.Up:
                e.Handled = true;
                ViewModel.CommandLine.MoveHistory(-1);
                CommandLine.SelectionStart = CommandLine.Text.Length;
                break;
            case VirtualKey.Down:
                e.Handled = true;
                ViewModel.CommandLine.MoveHistory(+1);
                CommandLine.SelectionStart = CommandLine.Text.Length;
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (CommandLine.Text.Length > 0)
                {
                    ViewModel.CommandLine.Text = string.Empty;
                }
                else
                {
                    ActivePanelView.FocusList();
                }

                break;
        }
    }

    /// <summary>Runs a command that may open dialogs, then gives the keyboard back to the active panel.</summary>
    private async void Run(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Command failed");
        }

        ActivePanelView.FocusList();
    }

    /// <summary>A click on the action bar does what the key would, including the held modifier (Shift+F6 = rename).</summary>
    private void OnFunctionKey(int key)
    {
        if (_shortcuts.TryGetValue((CurrentModifiers(), VirtualKey.F1 + key - 1), out var shortcut))
        {
            shortcut.Action();
        }
        else
        {
            ActivePanelView.FocusList();
        }
    }

    /// <summary>Shift+F2: compare and mark; when the user asks, the synchronize window opens over the result.</summary>
    private async void Compare()
    {
        SyncViewModel? sync = null;
        try
        {
            sync = await ViewModel.CompareDirectoriesAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Compare failed");
        }

        if (sync is null)
        {
            ActivePanelView.FocusList();
            return;
        }

        var window = new SyncWindow(ViewModel, sync);
        window.Closed += (_, _) => ActivePanelView.FocusList();
        ShowNewWindow(window);
    }

    /// <summary>Commands → Compare files: two marked files, or the file under the cursor in each panel.</summary>
    private async void CompareFiles()
    {
        try
        {
            if (await ViewModel.CompareFilesAsync() is { } diff)
            {
                var name = System.IO.Path.GetFileName(diff.LeftPath) + " ↔ " + System.IO.Path.GetFileName(diff.RightPath);
                var window = new DiffWindow(diff, name);
                window.Closed += (_, _) => ActivePanelView.FocusList();
                ShowNewWindow(window);
                return;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Compare files failed");
        }

        ActivePanelView.FocusList();
    }

    private void OnPauseOperation(object sender, RoutedEventArgs e) => ViewModel.Operations.Current?.TogglePause();

    private void OnCancelOperation(object sender, RoutedEventArgs e) => ViewModel.Operations.Current?.Cancel();

    private void OnBackgroundOperation(object sender, RoutedEventArgs e)
    {
        ViewModel.Operations.Current?.SendToBackground();
        ActivePanelView.FocusList();
    }

    /// <summary>The chip: card up → background, background → card up (with the keyboard on it).</summary>
    private void OnToggleOperation(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Operations.Current is not { } op)
        {
            return;
        }

        if (op.IsInBackground)
        {
            op.BringToFront();
        }
        else
        {
            op.SendToBackground();
            ActivePanelView.FocusList();
        }
    }

    /// <summary>Clicking into a panel while the progress card is up sends the operation to the background.</summary>
    private static bool NeedsRowFocus(object? focused) => focused switch
    {
        null => true,
        ListViewItem row => row.FocusState != FocusState.Keyboard,
        ListViewBase => true,
        _ => false, // a text box, the command line, a button: the user put focus there
    };

    /// <summary>Focus landed in the inactive panel without the user asking: back to the active panel's row.</summary>
    private void ReturnStrayFocus()
    {
        if (!_dialogs.IsOpen && !ViewModel.QuickView.IsOpen)
        {
            ActivePanelView.FocusListNow();
        }
    }

    private void ActivatePanel(PanelViewModel panel)
    {
        ViewModel.Activate(panel);
        if (ViewModel.Operations.IsDialogVisible && !_dialogs.IsOpen)
        {
            ViewModel.Operations.Current?.SendToBackground();
        }
    }

    /// <summary>Keyboard on the progress card: Tab cycles its buttons (focus stays on the card), Esc = background.</summary>
    private void HandleProgressCardKey(KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            ViewModel.Operations.Current?.SendToBackground();
            ActivePanelView.FocusList();
            return;
        }

        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = true;
            Button[] buttons = [PauseButton, BackgroundButton, CancelButton];
            var focused = FocusManager.GetFocusedElement(Content.XamlRoot) as Button;
            var index = Array.IndexOf(buttons, focused);
            var step = IsDown(VirtualKey.Shift) ? buttons.Length - 1 : 1;
            buttons[(Math.Max(index, 0) + step) % buttons.Length].Focus(FocusState.Keyboard);
        }
    }

    private void SwitchPanel()
    {
        if (!ViewModel.QuickView.IsOpen)
        {
            ViewModel.SwitchPanel(); // with the quick view up the other side is the view, not a panel
        }

        ActivePanelView.FocusList();
    }

    /// <summary>Ctrl+Q: the inactive panel gives its place to the quick view, or gets it back.</summary>
    private void ToggleQuickView()
    {
        ViewModel.ToggleQuickView();
        var open = ViewModel.QuickView.IsOpen;
        var replaced = ViewModel.QuickView.ReplacesLeft ? LeftPanel : RightPanel;
        Grid.SetColumn(QuickViewPane, ViewModel.QuickView.ReplacesLeft ? 0 : 2);
        QuickViewPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        LeftPanel.Visibility = open && ReferenceEquals(replaced, LeftPanel) ? Visibility.Collapsed : Visibility.Visible;
        RightPanel.Visibility = open && ReferenceEquals(replaced, RightPanel) ? Visibility.Collapsed : Visibility.Visible;
        ActivePanelView.FocusList();
    }

    private void ShowDriveMenu(PanelViewModel panel, FilePanel view)
    {
        ViewModel.Activate(panel);
        view.ShowDriveMenu();
    }

    /// <summary>
    /// The theme setting on the window's root, the same theme for brushes handed out from code (rows, tabs),
    /// and caption buttons that read on the title bar's colour.
    /// </summary>
    private void ApplyTheme()
    {
        var requested = Helpers.Ui.ThemeOf(ViewModel.Theme);
        Root.RequestedTheme = requested;
        var actual = requested != ElementTheme.Default ? requested
            : Root.IsLoaded ? Root.ActualTheme
            : Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        Helpers.Ui.Theme = actual;
        _dialogs.ApplyTheme(actual);

        Helpers.Ui.StyleCaptionButtons(this, actual);
    }

    // Toolbar buttons: the same actions as their keys.
    private void OnToolRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnToolNewFolder(object sender, RoutedEventArgs e) => Run(ViewModel.MakeDirectoryAsync);

    private void OnToolQuickView(object sender, RoutedEventArgs e) => ToggleQuickView();

    private void OnToolSearch(object sender, RoutedEventArgs e) => OpenSearch();

    private void OnToolSwitchPanel(object sender, RoutedEventArgs e) => SwitchPanel();

    /// <summary>"Run" beside the command line: the same as Enter in it.</summary>
    private async void OnRunCommandLine(object sender, RoutedEventArgs e)
    {
        await ViewModel.RunCommandLineAsync();
        ActivePanelView.FocusList();
    }

    // Menu items reuse the same actions.
    private async void Refresh()
    {
        await ViewModel.ActivePanel.RefreshAsync();
        ActivePanelView.FocusList();
    }

    private void ToggleViewOption(ViewOption option)
    {
        switch (option)
        {
            case ViewOption.Hidden: ToggleHidden(); break;
            case ViewOption.CompactRows: ViewModel.ToggleCompactRows(); ActivePanelView.FocusList(); break;
            case ViewOption.Attributes: ViewModel.ToggleShowAttributes(); ActivePanelView.FocusList(); break;
        }
    }

    private async void ToggleHidden()
    {
        await ViewModel.ToggleHiddenAsync();
        ActivePanelView.FocusList();
    }

    private void SetBrief(bool brief)
    {
        ViewModel.ActivePanel.BriefView = brief;
        ActivePanelView.FocusList();
    }

    private async void Sort(SortColumn column)
    {
        await ViewModel.ActivePanel.SortByAsync(column);
        ActivePanelView.FocusList();
    }

    private sealed record Shortcut(Action Action, bool Typing = false);

    /// <summary>Star widths in the saved ratio; the splitter then edits these two columns.</summary>
    private void ApplySplit(double split)
    {
        split = Math.Clamp(double.IsNaN(split) ? 0.5 : split, MainViewModel.MinPanelSplit, MainViewModel.MaxPanelSplit);
        LeftColumn.Width = new GridLength(split, GridUnitType.Star);
        RightColumn.Width = new GridLength(1 - split, GridUnitType.Star);
    }

    private double CurrentSplit()
    {
        var total = LeftColumn.ActualWidth + RightColumn.ActualWidth;
        return total > 0 ? LeftColumn.ActualWidth / total : ViewModel.Settings.PanelSplit;
    }

    private void OnSplitterDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ApplySplit(0.5);
    }

    private void RestorePlacement(WindowSettings? saved)
    {
        if (saved is { Width: > 200, Height: > 200 })
        {
            var rect = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null)
            {
                AppWindow.MoveAndResize(rect);
            }
        }
        else
        {
            // First run: 85 % of the primary work area, centred, capped at a comfortable size.
            var work = DisplayArea.Primary.WorkArea;
            var width = Math.Min(1600, work.Width * 85 / 100);
            var height = Math.Min(1000, work.Height * 85 / 100);
            AppWindow.MoveAndResize(new RectInt32(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height));
        }

        if (saved?.IsMaximized == true && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private WindowSettings? CapturePlacement()
    {
        var state = (AppWindow.Presenter as OverlappedPresenter)?.State;
        var previous = ViewModel.Settings.Window;
        if (state == OverlappedPresenterState.Minimized)
        {
            return previous; // minimized bounds are meaningless
        }

        if (state == OverlappedPresenterState.Maximized)
        {
            // Keep the restored bounds from last time so un-maximizing lands somewhere sensible.
            return previous is null
                ? new WindowSettings { IsMaximized = true }
                : new WindowSettings { X = previous.X, Y = previous.Y, Width = previous.Width, Height = previous.Height, IsMaximized = true };
        }

        var position = AppWindow.Position;
        var size = AppWindow.Size;
        return new WindowSettings { X = position.X, Y = position.Y, Width = size.Width, Height = size.Height };
    }
}
