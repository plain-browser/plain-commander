using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PlainCommander.App.Helpers;
using PlainCommander.Core.Details;
using PlainCommander.Core.Model;
using PlainCommander.Core.Remote;
using PlainCommander.Presentation;

namespace PlainCommander.App;

/// <summary>
/// The menu bar and the F2 menu come from one table, so they never drift apart. Labels are the resources the old
/// XAML menu used ("Menu_FilesCopy.Text"); the shortcut column is informative (the keys themselves are in the
/// shortcut table). "Network" lists the saved FTP/SFTP connections and is rebuilt when they change.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record MenuEntry(string Key, string? Shortcut = null, Action? Run = null, string? CheckedPath = null, string? Text = null, string? Glyph = null)
    {
        public static readonly MenuEntry Separator = new("-");

        public bool IsSeparator => Key == "-";
    }

    private IReadOnlyList<(string Key, IReadOnlyList<MenuEntry> Items)> MenuTable() =>
    [
        ("Menu_Files",
        [
            new("Menu_FilesView", "F3", ViewCurrent),
            new("Menu_FilesEdit", "F4", () => Run(ViewModel.EditCurrentAsync)),
            new("Menu_FilesNewFile", "Shift+F4", () => Run(ViewModel.NewFileAsync)),
            MenuEntry.Separator,
            new("Menu_FilesCopy", "F5", () => Run(ViewModel.CopyAsync)),
            new("Menu_FilesMove", "F6", () => Run(ViewModel.MoveAsync)),
            new("Menu_FilesRename", "Shift+F6", () => Run(ViewModel.RenameCurrentAsync)),
            new("Menu_FilesMultiRename", "Ctrl+M", OpenMultiRename),
            new("Menu_FilesMkDir", "F7", () => Run(ViewModel.MakeDirectoryAsync)),
            new("Menu_FilesPack", "Alt+F5", () => Run(ViewModel.PackAsync)),
            new("Menu_FilesUnpack", "Alt+F9", () => Run(ViewModel.UnpackAsync)),
            new("Menu_FilesEncrypt", "Ctrl+E", () => Run(ViewModel.EncryptAsync)),
            new("Menu_FilesDecrypt", null, () => Run(ViewModel.DecryptAsync)),
            new("Menu_FilesAttachImage", null, () => Run(ViewModel.AttachImageAsync)),
            new("Menu_FilesDetachDrive", null, () => Run(ViewModel.DetachDriveAsync)),
            new("Menu_FilesPermissions", null, () => Run(ViewModel.ChangePermissionsAsync)),
            new("Menu_FilesChecksumCreate", null, () => Run(ViewModel.CreateChecksumsAsync)),
            new("Menu_FilesChecksumVerify", null, () => Run(ViewModel.VerifyChecksumsAsync)),
            MenuEntry.Separator,
            new("Menu_FilesDelete", "F8", () => Run(() => ViewModel.DeleteAsync(permanent: false))),
            new("Menu_FilesDeleteForever", "Shift+Del", () => Run(() => ViewModel.DeleteAsync(permanent: true))),
            MenuEntry.Separator,
            new("Menu_Exit", "Alt+F4", Close),
        ]),
        ("Menu_Mark",
        [
            new("Menu_MarkPlus", "Num +", () => Run(() => ViewModel.MarkByMaskAsync(mark: true))),
            new("Menu_MarkMinus", "Num −", () => Run(() => ViewModel.MarkByMaskAsync(mark: false))),
            new("Menu_MarkInvert", "Num *", () => ViewModel.ActivePanel.InvertMarks()),
            MenuEntry.Separator,
            new("Menu_MarkAll", "Ctrl+A", () => ViewModel.ActivePanel.MarkAll()),
            new("Menu_MarkNone", null, () => ViewModel.ActivePanel.ClearMarks()),
            MenuEntry.Separator,
            new("Mark_CopyNames", null, () => ViewModel.CopyNamesToClipboard(fullPaths: false)),
            new("Mark_CopyPaths", "Ctrl+Shift+C", () => ViewModel.CopyNamesToClipboard(fullPaths: true)),
        ]),
        ("Menu_Commands",
        [
            new("Cmd_Undo", "Ctrl+Z", () => Run(ViewModel.UndoAsync)),
            MenuEntry.Separator,
            new("Cmd_Search", "Alt+F7", OpenSearch),
            new("Cmd_Compare", "Shift+F2", Compare),
            new("Cmd_CompareFiles", null, CompareFiles),
            MenuEntry.Separator,
            new("Cmd_NewTab", "Ctrl+T", () => { ViewModel.NewTab(); ActivePanelView.FocusList(); }),
            new("Cmd_CloseTab", "Ctrl+W", () => Run(ViewModel.CloseTabAsync)),
            new("Cmd_NextTab", "Ctrl+Tab", () => Run(() => ViewModel.NextTabAsync(1))),
            MenuEntry.Separator,
            new("Cmd_EditPath", "Ctrl+L", () => ActivePanelView.BeginPathEdit()),
            new("Cmd_History", "Alt+↓", ShowHistory),
            new("Cmd_Favorites", "Ctrl+D", ShowFavorites),
            MenuEntry.Separator,
            new("Cmd_Swap", "Ctrl+U", () => Run(ViewModel.SwapPanelsAsync)),
            new("Cmd_Mirror", "Ctrl+→ / Ctrl+←", () => Run(() => ViewModel.MirrorAsync(ReferenceEquals(ViewModel.ActivePanel, ViewModel.Left) ? Core.Settings.PanelSide.Right : Core.Settings.PanelSide.Left))),
            MenuEntry.Separator,
            new("Cmd_Refresh", "Ctrl+R", Refresh),
        ]),
        ("Menu_Net", NetworkEntries()),
        ("Menu_Show",
        [
            new("Show_Brief", "Ctrl+F1", () => SetBrief(true)),
            new("Show_Full", "Ctrl+F2", () => SetBrief(false)),
            MenuEntry.Separator,
            new("Show_SortName", "Ctrl+F3", () => Sort(SortColumn.Name)),
            new("Show_SortExt", "Ctrl+F4", () => Sort(SortColumn.Extension)),
            new("Show_SortDate", "Ctrl+F5", () => Sort(SortColumn.Modified)),
            new("Show_SortCreated", null, () => Sort(SortColumn.Created)),
            new("Show_SortSize", "Ctrl+F6", () => Sort(SortColumn.Size)),
            MenuEntry.Separator,
            new("Show_Hidden", "Ctrl+H", ToggleHidden, CheckedPath: "ShowHidden"),
            new("Show_Compact", null, () => ToggleViewOption(Views.ViewOption.CompactRows), CheckedPath: "CompactRows"),
            new("Show_Attributes", null, () => ToggleViewOption(Views.ViewOption.Attributes), CheckedPath: "ShowAttributes"),
            new("Show_FolderSizes", null, ViewModel.ToggleFolderSizes, CheckedPath: "FolderSizes"),
            MenuEntry.Separator,
            new("Show_DetailDimensions", null, () => ViewModel.ToggleDetail(DetailColumn.Dimensions), CheckedPath: "Detail:Dimensions"),
            new("Show_DetailTaken", null, () => ViewModel.ToggleDetail(DetailColumn.Taken), CheckedPath: "Detail:Taken"),
            new("Show_DetailDuration", null, () => ViewModel.ToggleDetail(DetailColumn.Duration), CheckedPath: "Detail:Duration"),
            new("Show_DetailVersion", null, () => ViewModel.ToggleDetail(DetailColumn.Version), CheckedPath: "Detail:Version"),
            new("Show_SortDetail", null, () => Sort(SortColumn.Detail)),
            MenuEntry.Separator,
            new("Show_QuickView", "Ctrl+Q", ToggleQuickView),
            new("Show_Filter", "Ctrl+S", () => Run(ViewModel.FilterAsync)),
            new("Show_Flat", "Ctrl+B", () => Run(ViewModel.ToggleFlatViewAsync)),
        ]),
        ("Menu_Configuration",
        [
            new("Menu_Settings", null, () => OnSettings(this, new RoutedEventArgs())),
            new("Net_Manage", "Ctrl+F", OpenConnections),
        ]),
        ("Menu_Help",
        [
            new("Help_Keys", "F1", ShowKeys),
            MenuEntry.Separator,
            new("Help_About", null, ShowAbout),
        ]),
    ];

    /// <summary>Connect, disconnect, then every saved connection (a click opens it in the active panel).</summary>
    private List<MenuEntry> NetworkEntries()
    {
        var entries = new List<MenuEntry>
        {
            new("Cmd_Connect", "Ctrl+F", OpenConnections),
            new("Cmd_Disconnect", "Ctrl+Shift+F", () => Run(ViewModel.DisconnectAsync)),
            new("Menu_FilesPermissions", null, () => Run(ViewModel.ChangePermissionsAsync)),
        };
        if (ViewModel.Settings.RemoteSites.Count > 0)
        {
            entries.Add(MenuEntry.Separator);
            foreach (var site in ViewModel.Settings.RemoteSites.Where(s => !string.IsNullOrWhiteSpace(s.Host)))
            {
                var title = string.IsNullOrWhiteSpace(site.Name) ? site.Host : site.Name;
                var glyph = Ui.SiteGlyph(unencrypted: site.Protocol == RemoteProtocol.Ftp || (site.Protocol == RemoteProtocol.S3 && site.PlainHttp));
                entries.Add(new MenuEntry("site", RemotePath.Scheme(site.Protocol).ToUpperInvariant(), () => Run(async () => await ViewModel.ConnectAsync(site)), Text: title, Glyph: glyph));
            }
        }

        return entries;
    }

    private static string MenuText(MenuEntry entry) => entry.Text ?? Loc.TryGet(entry.Key + "/Text") ?? Loc.Get(entry.Key);

    private readonly List<(WeakReference<ToggleMenuFlyoutItem> Item, string Path)> _menuToggles = [];

    private bool CheckedValue(string path) => path switch
    {
        nameof(PanelViewModel.ShowHidden) => ViewModel.Left.ShowHidden,
        nameof(PanelViewModel.CompactRows) => ViewModel.Left.CompactRows,
        nameof(PanelViewModel.ShowAttributes) => ViewModel.Left.ShowAttributes,
        nameof(PanelViewModel.FolderSizes) => ViewModel.Left.FolderSizes,
        _ when path.StartsWith(DetailPrefix, StringComparison.Ordinal) => ViewModel.ActivePanel.Detail.ToString() == path[DetailPrefix.Length..],
        _ => false,
    };

    /// <summary>The detail column belongs to a panel: its check marks follow the active one.</summary>
    private const string DetailPrefix = "Detail:";

    /// <summary>Keeps the check marks of the Show menu (bar and F2) in step with the panels, whoever changed them.</summary>
    private void OnLeftPanelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.Detail))
        {
            RefreshDetailChecks();
            return;
        }

        _menuToggles.RemoveAll(t => !t.Item.TryGetTarget(out _));
        foreach (var (reference, path) in _menuToggles)
        {
            if (path == e.PropertyName && reference.TryGetTarget(out var toggle))
            {
                toggle.IsChecked = CheckedValue(path);
            }
        }
    }

    /// <summary>After any change of a panel's detail column or of the active panel.</summary>
    private void RefreshDetailChecks()
    {
        _menuToggles.RemoveAll(t => !t.Item.TryGetTarget(out _));
        foreach (var (reference, path) in _menuToggles)
        {
            if (path.StartsWith(DetailPrefix, StringComparison.Ordinal) && reference.TryGetTarget(out var toggle))
            {
                toggle.IsChecked = CheckedValue(path);
            }
        }
    }

    private void BuildTopMenu()
    {
        TopMenu.Items.Clear();
        foreach (var (key, items) in MenuTable())
        {
            var top = new MenuBarItem { Title = Loc.TryGet(key + "/Text") ?? Loc.Get(key) };
            Fill(top.Items, items);
            TopMenu.Items.Add(top);
        }
    }

    private void Fill(IList<MenuFlyoutItemBase> target, IReadOnlyList<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                target.Add(new MenuFlyoutSeparator());
                continue;
            }

            MenuFlyoutItem item = entry.CheckedPath is { } path ? new ToggleMenuFlyoutItem() : new MenuFlyoutItem();
            item.Text = MenuText(entry);
            if (entry.Shortcut is { } shortcut)
            {
                item.KeyboardAcceleratorTextOverride = shortcut;
            }

            if (entry.Glyph is { } glyph)
            {
                item.Icon = new FontIcon { Glyph = glyph };
            }

            if (item is ToggleMenuFlyoutItem toggle)
            {
                // Set from code, not bound: a click toggles the item's own value, which would end a OneWay binding.
                // The view options are the same in both panels; the left one carries them.
                var checkedPath = entry.CheckedPath!;
                toggle.IsChecked = CheckedValue(checkedPath);
                _menuToggles.Add((new WeakReference<ToggleMenuFlyoutItem>(toggle), checkedPath));
            }

            var run = entry.Run;
            item.Click += (_, _) => run?.Invoke();
            target.Add(item);
        }
    }

    /// <summary>F2: the same menus as the bar, as one flyout under it (the bar has no "open" call).</summary>
    private void ShowMenu()
    {
        var flyout = new MenuFlyout();
        foreach (var (key, items) in MenuTable())
        {
            var sub = new MenuFlyoutSubItem { Text = Loc.TryGet(key + "/Text") ?? Loc.Get(key) };
            Fill(sub.Items, items);
            flyout.Items.Add(sub);
        }

        flyout.ShowAt(TopMenu, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    /// <summary>F1: every shortcut, from the menu table plus the panel keys that have no menu entry.</summary>
    private async void ShowKeys()
    {
        var rows = new List<(string Key, string What)>
        {
            ("Tab", Loc.Get("Keys_Tab")),
            ("Enter", Loc.Get("Keys_Enter")),
            ("Backspace", Loc.Get("Keys_Back")),
            ("Ctrl+PgDn", Loc.Get("Keys_CtrlPgDn")),
            ("Insert", Loc.Get("Keys_Insert")),
            (Loc.Get("Keys_SpaceKey"), Loc.Get("Keys_Space")),
            ("Alt+F1 / Alt+F2", Loc.Get("Keys_Drives")),
            ("Alt+F8", Loc.Get("Keys_CommandLine")),
            ("Ctrl+Enter / Ctrl+P", Loc.Get("Keys_InsertName")),
            (Loc.Get("Keys_LettersKey"), Loc.Get("Keys_QuickSearch")),
            ("F2", Loc.Get("Keys_Menu")),
        };
        foreach (var (_, items) in MenuTable())
        {
            rows.AddRange(items.Where(e => !e.IsSeparator && e.Shortcut is not null && e.Key != "site").Select(e => (e.Shortcut!, MenuText(e).TrimEnd('…'))));
        }

        await _dialogs.ShowKeysAsync(Loc.Get("Help_Keys/Text"), rows.DistinctBy(r => r.Item1).ToList());
        ActivePanelView.FocusList();
    }

    private async void ShowAbout()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        await _dialogs.ShowInfoAsync(Loc.Get("Help_About/Text").TrimEnd('…'), Loc.Format("Help_AboutText", version, Environment.Version.ToString(3)));
        ActivePanelView.FocusList();
    }
}
