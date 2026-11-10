using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Elevation;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Network;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Settings;
using Serilog;

namespace PlainCommander.Presentation;

/// <summary>What FTP/SFTP/S3 needs from the app: the login and host key dialogs and the password store.</summary>
public sealed record RemoteSetup(
    IRemotePrompts Prompts,
    ISecretStore Secrets,
    Func<RemoteSite, RemoteCredentials, Func<HostIdentity, bool>, IRemoteClient>? Factory = null);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly List<FileSystemWatcher> _uploadWatchers = [];
    private readonly SettingsStore _store;
    private readonly IDialogService _dialogs;
    private readonly ILauncher _launcher;
    private readonly IRecycleBin _bin;
    private readonly IElevation? _elevation;
    private readonly IClipboard? _clipboard;
    private string _lastMask = "*.*";

    public MainViewModel(
        IFileSystem fs,
        ILauncher launcher,
        SettingsStore store,
        IDialogService dialogs,
        IOperationPrompts prompts,
        IRecycleBin bin,
        IIconProvider? icons = null,
        RemoteSetup? remote = null,
        IElevation? elevation = null,
        IClipboard? clipboard = null,
        Core.Details.IFileDetailsProvider? details = null,
        IDiskImages? disks = null,
        Core.Undo.IRecycleBinRestore? restore = null,
        Func<IReadOnlyList<CloudFolder>>? cloud = null,
        Func<IReadOnlyList<string>>? wsl = null)
    {
        _disks = disks;
        _restore = restore;
        _clipboard = clipboard;
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _bin = bin ?? throw new ArgumentNullException(nameof(bin));
        _elevation = elevation;
        Operations = new OperationQueueViewModel(prompts, elevation, _dialogs.ShowErrorAsync);
        Operations.Finished += OnOperationFinished;
        Settings = store.Load();
        Remote = new RemoteConnections(
            remote?.Prompts ?? new NoRemotePrompts(),
            remote?.Secrets ?? new NoSecretStore(),
            Settings.TrustedHosts,
            () => Settings.RemoteSites,
            remote?.Factory);
        _fsForDrives = fs;
        var withServers = new RemoteFileSystem(fs, Remote);
        Left = new PanelViewModel(withServers, launcher, icons) { DetailsProvider = details };
        Right = new PanelViewModel(withServers, launcher, icons) { DetailsProvider = details };
        Left.CloudProvider = cloud;
        Right.CloudProvider = cloud;
        Left.WslProvider = wsl;
        Right.WslProvider = wsl;
        Left.OtherPanelPath = () => Right.Path;
        Right.OtherPanelPath = () => Left.Path;
        Left.EncryptedHandler = (item, browse) => HandleEncryptedAsync(Left, item, browse);
        Right.EncryptedHandler = (item, browse) => HandleEncryptedAsync(Right, item, browse);
        Left.ApplySettings(Settings.Left, Settings.ShowHidden);
        Right.ApplySettings(Settings.Right, Settings.ShowHidden);
        SetCompactRows(Settings.CompactRows);
        SetShowAttributes(Settings.ShowAttributes);
        SetFolderSizes(Settings.FolderSizes);
        SetFont(Settings.PanelFont, Settings.PanelFontSize);
        Theme = Settings.Theme;
        CommandLine.LoadHistory(Settings.CommandHistory);
        ActivePanel = Settings.ActivePanel == PanelSide.Right ? Right : Left;
        ActivePanel.IsActive = true;
    }

    /// <summary>Settings as loaded at startup; the window reads its saved placement from here.</summary>
    public AppSettings Settings { get; }

    /// <summary>Open FTP/SFTP/S3 servers and their logins.</summary>
    public RemoteConnections Remote { get; }

    /// <summary>
    /// Ctrl+F "Connect", a typed "sftp://user@host" (Ctrl+L, cd): the active panel opens the server. Without a path
    /// after the host the server's start folder (home) opens. The login dialog comes from the connection itself.
    /// </summary>
    public async Task<bool> ConnectAsync(string address)
    {
        var panel = ActivePanel;
        if (RemotePath.ParseTyped(address?.Trim()) is not { } location)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(new ArgumentException(address)));
            return false;
        }

        var target = location.FullPath;
        panel.Status = Texts.Connecting(location.Host);
        if (!HasExplicitPath(address!))
        {
            try
            {
                target = location.Root + await Remote.HomeAsync(location.Root, CancellationToken.None);
            }
            catch (RemoteException ex) when (ex.IsCancelled)
            {
                panel.Status = Texts.LoginCancelled;
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Cannot connect to {Root}", location.Root);
                panel.Status = string.Empty;
                await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
                return false;
            }
        }

        if (await panel.NavigateAsync(target))
        {
            return true;
        }

        if (panel.Status != Texts.LoginCancelled)
        {
            await _dialogs.ShowErrorAsync(panel.Status);
        }

        return false;
    }

    /// <summary>Opens a saved connection in the active panel.</summary>
    public Task<bool> ConnectAsync(RemoteSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return ConnectAsync(site.StartLocation);
    }

    /// <summary>Ctrl+Shift+F: closes the server the active panel shows; panels on it go back to their last disk folder.</summary>
    public async Task DisconnectAsync()
    {
        if (RemotePath.Parse(ActivePanel.Path) is not { } location)
        {
            return;
        }

        await Remote.DisconnectAsync(location.Root);
        foreach (var panel in new[] { Left, Right })
        {
            if (RemotePath.Parse(panel.Path) is { } shown && string.Equals(shown.Root, location.Root, StringComparison.OrdinalIgnoreCase))
            {
                await panel.NavigateWithFallbackAsync(NullIfEmpty(panel.LastLocalPath), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
        }
    }

    /// <summary>Saves settings now (the connection manager calls it after changes).</summary>
    public void SaveSettings()
    {
        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Cannot save settings to {Path}", _store.FilePath);
        }
    }

    /// <summary>"sftp://host/" and "sftp://host/x" name a folder; "sftp://host" means "where the server starts".</summary>
    private static bool HasExplicitPath(string address)
    {
        var start = address.IndexOf("://", StringComparison.Ordinal);
        return start >= 0 && address.IndexOfAny(['/', '\\'], start + 3) >= 0;
    }

    private async Task<bool> RefuseRemoteAsync(params PanelViewModel[] panels)
    {
        if (await RefuseNetworkListAsync(panels))
        {
            return true;
        }

        if (!panels.Any(p => p.IsRemote))
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(Texts.RemoteNotHere);
        return true;
    }

    /// <summary>Computers and share lists ("net://") hold no files: writing there is explained, not attempted.</summary>
    private async Task<bool> RefuseNetworkListAsync(params PanelViewModel[] panels)
    {
        if (!panels.Any(p => p.IsNetworkList))
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(Texts.NetworkListNotHere);
        return true;
    }

    private void PostToUi(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(static a => ((Action)a!).Invoke(), action);
        }
    }

    public CommandLineViewModel CommandLine { get; } = new();

    /// <summary>Raised when another window (search) wants the main window to bring the active panel forward.</summary>
    public event EventHandler? PanelFocusRequested;

    public void RequestPanelFocus() => PanelFocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Ctrl+U: the panels trade folders.</summary>
    public Task SwapPanelsAsync()
    {
        var left = Left.Path;
        var right = Right.Path;
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(Left.NavigateAsync(right), Right.NavigateAsync(left));
    }

    /// <summary>
    /// Ctrl+→ / Ctrl+←: the other panel opens the folder under the cursor, or this panel's folder when the
    /// cursor is on a file. The target panel is the one the arrow points at; pointing at the active panel
    /// is treated as pointing at the other one.
    /// </summary>
    public Task MirrorAsync(PanelSide target)
    {
        var destination = target == PanelSide.Right ? Right : Left;
        if (ReferenceEquals(destination, ActivePanel))
        {
            destination = InactivePanel;
        }

        var source = ActivePanel;
        var folder = source.CurrentItem is { IsDirectory: true, Entry.IsParentLink: false } dir ? dir.Entry.FullPath : source.Path;
        return string.IsNullOrEmpty(folder) ? Task.CompletedTask : destination.NavigateAsync(folder);
    }

    public IReadOnlyList<string> Favorites => Settings.Favorites;

    public bool IsFavorite(string path) => Settings.Favorites.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ctrl+D menu: adds the active panel's folder unless it is there already.</summary>
    public void AddFavorite(string path)
    {
        if (!string.IsNullOrEmpty(path) && !IsFavorite(path))
        {
            Settings.Favorites.Add(path);
        }
    }

    public void RemoveFavorite(string path) =>
        Settings.Favorites.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Alt+F5: packs the marked items (or the one under the cursor) into a new archive, by default in the other
    /// panel, named after the single item or the current folder. The typed name decides the format (zip, tar, tar.gz).
    /// </summary>
    public async Task PackAsync()
    {
        var source = ActivePanel;
        if (await RefuseRemoteAsync(source) || await RefuseInsideArchiveAsync(source))
        {
            return;
        }

        var items = source.OperationSources;
        if (items.Count == 0)
        {
            return;
        }

        var folder = string.IsNullOrEmpty(InactivePanel.Path) || InactivePanel.IsInArchive ? source.Path : InactivePanel.Path;
        var stem = items.Count == 1 ? items[0].Name : Path.GetFileName(Path.TrimEndingDirectorySeparator(source.Path));
        if (items.Count == 1 && !items[0].IsDirectory)
        {
            stem = Path.GetFileNameWithoutExtension(stem);
        }

        var answer = await _dialogs.AskPackTargetAsync(items.Count, items[0].Name, Path.Combine(folder, stem + ".zip"), ArchiveReaders.SevenZipAvailable);
        if (answer is null || string.IsNullOrWhiteSpace(answer.Target))
        {
            return;
        }

        var input = answer.Target.Trim();
        var archive = ArchivePackOperation.WithDefaultExtension(Path.IsPathRooted(input) ? input : Path.Combine(folder, input));
        if (ArchiveFileSystem.Locate(Path.GetDirectoryName(archive) ?? archive) is not null)
        {
            await _dialogs.ShowErrorAsync(Texts.ArchiveReadOnly);
            return;
        }

        if (!ArchivePackOperation.CanWrite(archive))
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(new OperationRuleException(RuleViolation.UnsupportedArchive, archive)));
            return;
        }

        var password = ArchivePackOperation.SupportsPassword(archive) ? answer.Password : null;
        var operation = new ArchivePackOperation(items.Select(static i => i.Entry.FullPath).ToArray(), archive, password);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), result => AfterOperationAsync(source, result));
    }

    /// <summary>
    /// Files → Encrypt: each marked file or folder (or the one under the cursor) goes into its own password-protected
    /// container next to it: .pce (native, AES-256-GCM + Argon2id), or a 7z / AES-256 zip archive for other programs.
    /// When all went well the user may delete the unencrypted originals.
    /// </summary>
    public async Task EncryptAsync()
    {
        var panel = ActivePanel;
        if (panel.IsRemote || panel.IsInArchive || panel.IsNetworkList)
        {
            await _dialogs.ShowErrorAsync(Texts.EncryptNotHere);
            return;
        }

        var items = panel.OperationSources;
        if (items.Count == 0)
        {
            return;
        }

        // Like Alt+F5: the copies go to the other panel's folder unless another one is typed; the root of a drive
        // (C:\) takes no new files from an ordinary user, so "next to the original" is no default.
        var defaultFolder = string.IsNullOrEmpty(InactivePanel.Path) || InactivePanel.IsInArchive || InactivePanel.IsRemote || InactivePanel.IsNetworkList ? panel.Path : InactivePanel.Path;
        var answer = await _dialogs.AskEncryptAsync(items.Count, items[0].Name, ArchiveReaders.SevenZipAvailable, defaultFolder);
        if (answer is null || answer.Password.Length == 0)
        {
            return;
        }

        if (answer.Extension != PceFormat.Extension && !ArchiveReaders.SevenZipAvailable)
        {
            await _dialogs.ShowErrorAsync(Texts.EncryptUnavailable);
            return;
        }

        var typed = answer.Folder?.Trim();
        var folder = string.IsNullOrEmpty(typed) ? null : Path.IsPathRooted(typed) ? typed : Path.Combine(panel.Path, typed);
        if (folder is not null && (RemotePath.IsRemote(folder) || NetworkPath.IsVirtual(folder) || ArchiveFileSystem.Locate(folder) is not null))
        {
            await _dialogs.ShowErrorAsync(Texts.EncryptNotHere);
            return;
        }

        var paths = items.Select(static i => i.Entry.FullPath).ToArray();
        if (folder is not null && paths.Any(p => Directory.Exists(p) && EncryptOperation.IsInside(folder, p)))
        {
            await _dialogs.ShowErrorAsync(Texts.EncryptIntoItself);
            return;
        }

        var operation = new EncryptOperation(paths, answer.Extension, answer.Password, answer.HideName, answer.Compression, targetFolder: folder);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), async result =>
        {
            await AfterOperationAsync(panel, result);
            if (result.Outcome == OperationOutcome.Completed && result.Done == paths.Length && await _dialogs.ConfirmDeleteOriginalsAsync(paths.Length))
            {
                // queued, not awaited: the queue runs it after this step finishes (waiting here would block the queue)
                var delete = new DeleteOperation(paths);
                _ = Operations.Enqueue(delete, Texts.OperationTitle(delete.Kind, paths.Length), r => AfterOperationAsync(panel, r));
            }
        });
    }

    /// <summary>Files → Decrypt: the marked .pce files (or the one under the cursor) back to what they hold, next to them.</summary>
    public async Task DecryptAsync()
    {
        var panel = ActivePanel;
        if (panel.IsRemote || panel.IsInArchive || panel.IsNetworkList)
        {
            await _dialogs.ShowErrorAsync(Texts.DecryptNotHere);
            return;
        }

        var files = panel.OperationSources.Where(static i => i.IsEncrypted).Select(static i => i.Entry.FullPath).ToArray();
        if (files.Length == 0)
        {
            await _dialogs.ShowErrorAsync(Texts.DecryptNothing);
            return;
        }

        await DecryptAsync(panel, files);
    }

    private async Task DecryptAsync(PanelViewModel panel, string[] files)
    {
        var verified = await AskVerifiedPasswordAsync(files[0]);
        if (verified is null)
        {
            return;
        }

        var operation = new DecryptOperation(files, verified.Value.Password);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, files.Length), result => AfterOperationAsync(panel, result));
    }

    /// <summary>
    /// Enter / Ctrl+PgDn on a .pce file: after the password its content opens like an archive — a folder's TAR, or a
    /// single file as a TAR of one entry — decrypted to a temp file the panel enters. Files and folders are copied
    /// out with F5; Files → Decrypt writes everything to disk instead.
    /// </summary>
    private async Task HandleEncryptedAsync(PanelViewModel panel, FileItem item, bool browse)
    {
        var file = item.Entry.FullPath;
        var verified = await AskVerifiedPasswordAsync(file);
        if (verified is null)
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "PlainCommander", "pce-" + Guid.NewGuid().ToString("N"));
        // The stored name is data from the file: a path in it ("..\..\x", "C:\x") must not move the TAR out of the temp folder.
        var tar = Path.Combine(dir, (PceFile.PlainName(verified.Value.Metadata.Name) ?? Path.GetFileNameWithoutExtension(item.Name)) + ".tar");
        panel.Status = Texts.Decrypting(item.Name);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                using var output = File.Create(tar);
                PceFile.CopyContentAsTar(file, verified.Value.Password, output, null, CancellationToken.None);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            panel.Status = Texts.EncryptedBroken(item.Name, ex);
            return;
        }

        PceBrowse.Register(tar, file);
        await panel.NavigateAsync(tar);
    }

    /// <summary>Passwords that opened a .pce file, by file: kept in memory until the program closes, so opening it again asks nothing.</summary>
    private readonly Dictionary<string, string> _pcePasswords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The remembered password when it still opens the file; otherwise asks until the password opens it (block 0
    /// authenticates) or the user gives up. A password that worked is remembered for this session.
    /// </summary>
    private async Task<(string Password, PceHeader Header, PceMetadata Metadata)?> AskVerifiedPasswordAsync(string file)
    {
        if (_pcePasswords.TryGetValue(file, out var remembered))
        {
            try
            {
                var peek = await Task.Run(() => PceFile.Peek(file, remembered));
                return (remembered, peek.Header, peek.Metadata);
            }
            catch (Exception ex) when (ex is PceWrongPasswordException or PceFormatException or IOException or UnauthorizedAccessException)
            {
                _pcePasswords.Remove(file); // the file was replaced since: ask again below
            }
        }

        var wrong = false;
        while (true)
        {
            var password = await _dialogs.AskDecryptPasswordAsync(file, wrong);
            if (string.IsNullOrEmpty(password))
            {
                return null;
            }

            try
            {
                var peek = await Task.Run(() => PceFile.Peek(file, password));
                _pcePasswords[file] = password;
                return (password, peek.Header, peek.Metadata);
            }
            catch (PceWrongPasswordException)
            {
                wrong = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _dialogs.ShowErrorAsync(Texts.EncryptedBroken(Path.GetFileName(file), ex));
                return null;
            }
        }
    }

    /// <summary>
    /// Alt+F9: unpacks the marked archives (or the one under the cursor) into the other panel's folder, each into a
    /// folder named after it unless the user unticks that. Files that are not archives are left out.
    /// </summary>
    public async Task UnpackAsync()
    {
        var source = ActivePanel;
        if (await RefuseRemoteAsync(source))
        {
            return;
        }

        if (source.IsInArchive)
        {
            await _dialogs.ShowErrorAsync(Texts.UnpackInsideArchive);
            return;
        }

        var archives = source.OperationSources
            .Where(static i => !i.IsDirectory && (ArchivePath.IsArchiveName(i.Name) || ArchivePath.IsOpenedExplicitly(i.Entry.FullPath)))
            .ToList();
        if (archives.Count == 0)
        {
            await _dialogs.ShowErrorAsync(Texts.UnpackNothing);
            return;
        }

        var folder = string.IsNullOrEmpty(InactivePanel.Path) || InactivePanel.IsInArchive || InactivePanel.IsRemote ? source.Path : InactivePanel.Path;
        var answer = await _dialogs.AskUnpackTargetAsync(archives.Count, archives[0].Name, folder);
        if (answer is null || string.IsNullOrWhiteSpace(answer.Target))
        {
            return;
        }

        var target = answer.Target.Trim();
        target = Path.IsPathRooted(target) ? target : Path.Combine(source.Path, target);
        if (RemotePath.IsRemote(target) || ArchiveFileSystem.Locate(target) is not null)
        {
            await _dialogs.ShowErrorAsync(Texts.ArchiveToArchive);
            return;
        }

        var operation = new ArchiveExtractOperation(archives.Select(static i => i.Entry.FullPath).ToArray(), new TransferTarget(target, null), answer.OwnFolders);
        await Operations.Enqueue(operation, Texts.UnpackTitle(archives.Count), result => AfterOperationAsync(source, result));
    }

    /// <summary>
    /// Shift+F2: compares the two panels' folders and marks what differs in each (only there, newer there, different),
    /// so F5 right after copies exactly the changes. Options (subfolders, by content) are asked once and remembered.
    /// When something differs the user may open the synchronize window instead: the returned model, or null.
    /// </summary>
    /// <summary>Shift+F2 is reading folders; Esc stops it (<see cref="CancelCompare"/>).</summary>
    [ObservableProperty]
    public partial bool IsComparing { get; private set; }

    private CancellationTokenSource? _compareCancel;

    public void CancelCompare() => _compareCancel?.Cancel();

    public async Task<SyncViewModel?> CompareDirectoriesAsync()
    {
        if (IsComparing || await RefuseNetworkListAsync(Left, Right))
        {
            return null;
        }

        if (Left.IsInArchive || Right.IsInArchive)
        {
            await _dialogs.ShowErrorAsync(Texts.CompareNeedsDisk);
            return null;
        }

        var options = await _dialogs.AskCompareOptionsAsync(new CompareOptions(Settings.CompareSubfolders, Settings.CompareByContent));
        if (options is null)
        {
            return null;
        }

        Settings.CompareSubfolders = options.Subfolders;
        Settings.CompareByContent = options.ByContent;
        CompareResult result;
        var leftSide = CompareSide(Left.Path);
        var rightSide = CompareSide(Right.Path);
        using var cancel = new CancellationTokenSource();
        _compareCancel = cancel;
        IsComparing = true;
        var shown = DateTime.MinValue;
        var progress = new Progress<CompareProgress>(p =>
        {
            if (!IsComparing || (DateTime.UtcNow - shown).TotalMilliseconds < 100)
            {
                return; // a late report, or too soon after the last one
            }

            shown = DateTime.UtcNow;
            Left.Status = Right.Status = Texts.Comparing(p);
        });
        try
        {
            result = await Task.Run(() => DirectoryCompare.RunAsync(leftSide, rightSide, options, progress, cancel.Token), cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Left.Status = Right.Status = Texts.CompareStopped; // until the next refresh puts the summary back
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Left.RestoreStatus();
            Right.RestoreStatus();
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return null;
        }
        finally
        {
            IsComparing = false;
            _compareCancel = null;
            await DisposeSideAsync(leftSide);
            await DisposeSideAsync(rightSide);
        }

        var leftMarked = Left.MarkOnly(result.MarkedOn(left: true));
        var rightMarked = Right.MarkOnly(result.MarkedOn(left: false));
        if (result.Count == 0)
        {
            await _dialogs.ShowInfoAsync(Texts.CompareTitle, Texts.CompareSame);
            return null;
        }

        Log.Information("Compared {Left} and {Right}: {Count} differences, {L} / {R} marked", Left.Path, Right.Path, result.Count, leftMarked, rightMarked);
        if (Left.IsRemote && Right.IsRemote)
        {
            await _dialogs.ShowInfoAsync(Texts.CompareTitle, Texts.CompareTwoServers);
            return null;
        }

        return await _dialogs.OfferSyncAsync(result.Count) ? new SyncViewModel(Left.Path, Right.Path, result) : null;
    }

    private ICompareSide CompareSide(string path) =>
        RemotePath.IsRemote(path) ? new RemoteCompareSide(Remote, path) : new DiskCompareSide(path, Left.ShowHidden);

    private static async Task DisposeSideAsync(ICompareSide side)
    {
        if (side is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>The synchronize window's text diff: a server file is read from a temp copy.</summary>
    public DiffViewModel CreateDiff(SyncRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new DiffViewModel(row.LeftPath, row.RightPath, (path, token) =>
            RemotePath.IsRemote(path) ? RemoteDownloadOperation.ToTempAsync(Remote, path, token) : Task.FromResult(path));
    }

    /// <summary>The synchronize window's "Synchronize": copies in the chosen directions, then both panels refresh unmarked.</summary>
    public Task RunSyncAsync(SyncViewModel sync)
    {
        ArgumentNullException.ThrowIfNull(sync);
        var operation = sync.BuildOperation(Settings.CopyParallelism, Remote);
        return Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, operation.Count), async result =>
        {
            if (result.Outcome == OperationOutcome.Completed)
            {
                Left.ClearMarks();
                Right.ClearMarks();
            }

            await Task.WhenAll(Left.RefreshAfterChangeAsync(), Right.RefreshAfterChangeAsync());
        });
    }

    /// <summary>
    /// Files → Permissions: chmod on the marked items (or the one under the cursor) of a server panel. The dialog starts
    /// with the first item's mode; folders may pass it on to their content.
    /// </summary>
    public async Task ChangePermissionsAsync()
    {
        var panel = ActivePanel;
        if (!panel.IsRemote)
        {
            await ChangeAttributesAsync(panel);
            return;
        }

        var items = panel.OperationSources;
        if (items.Count == 0)
        {
            return;
        }

        if (RemotePath.Parse(panel.Path) is { Protocol: RemoteProtocol.S3 })
        {
            await _dialogs.ShowErrorAsync(Texts.S3NoPermissions);
            return;
        }

        var first = items[0];
        var initial = first.Entry.Mode ?? (first.IsDirectory ? 0x1ED : 0x1A4); // 755 / 644 when the server does not say
        var answer = await _dialogs.AskPermissionsAsync(items.Count, first.Name, initial, items.Any(static i => i.IsDirectory));
        if (answer is null)
        {
            return;
        }

        var operation = new RemoteModeOperation(Remote, items.Select(static i => i.Entry.FullPath).ToArray(), answer.Mode, answer.Recursive);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), result => AfterOperationAsync(panel, result));
    }

    /// <summary>Files → Attributes on disk: read-only, hidden, system, archive and the modification time.</summary>
    private async Task ChangeAttributesAsync(PanelViewModel panel)
    {
        if (panel.IsInArchive)
        {
            await _dialogs.ShowErrorAsync(Texts.ArchiveReadOnly);
            return;
        }

        var items = panel.OperationSources;
        if (items.Count == 0)
        {
            return;
        }

        var first = items[0];
        var change = await _dialogs.AskAttributesAsync(items.Count, first.Name, first.Entry.Attributes, first.Entry.Modified, items.Any(static i => i.IsDirectory));
        if (change is null)
        {
            return;
        }

        var operation = new AttributesOperation(items.Select(static i => i.Entry.FullPath).ToArray(), change);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), result => AfterOperationAsync(panel, result));
    }

    /// <summary>Files → Create checksums: one file with the sums of the chosen files (and of the files inside chosen folders).</summary>
    public async Task CreateChecksumsAsync()
    {
        var panel = ActivePanel;
        if (panel.IsRemote || panel.IsInArchive)
        {
            await _dialogs.ShowErrorAsync(Texts.ChecksumsDiskOnly);
            return;
        }

        var items = panel.OperationSources;
        if (items.Count == 0)
        {
            return;
        }

        var stem = items.Count == 1 ? items[0].Name : Path.GetFileName(Path.TrimEndingDirectorySeparator(panel.Path));
        var answer = await _dialogs.AskChecksumAsync(items.Count, stem + ChecksumFormat.Extension(ChecksumKind.Sha256));
        if (answer is null || string.IsNullOrWhiteSpace(answer.FileName))
        {
            return;
        }

        var name = answer.FileName.Trim();
        if (ChecksumFormat.KindOf(name) != answer.Kind)
        {
            name += ChecksumFormat.Extension(answer.Kind);
        }

        var output = Path.IsPathFullyQualified(name) ? name : Path.Combine(panel.Path, name);
        var operation = new ChecksumCreateOperation(items.Select(static i => i.Entry.FullPath).ToArray(), output, answer.Kind);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), async result =>
        {
            await AfterOperationAsync(panel, result);
            await RefreshPanelsShowingAsync(panel.Path, panel, Path.GetFileName(output));
        });
    }

    /// <summary>Files → Verify checksums: the checksum file under the cursor; the result comes as a summary.</summary>
    public async Task VerifyChecksumsAsync()
    {
        var panel = ActivePanel;
        if (panel.IsRemote || panel.IsInArchive)
        {
            await _dialogs.ShowErrorAsync(Texts.ChecksumsDiskOnly);
            return;
        }

        if (panel.CurrentItem is not { IsDirectory: false } item || ChecksumFormat.KindOf(item.Name) is null)
        {
            await _dialogs.ShowErrorAsync(Texts.ChecksumNotAFile);
            return;
        }

        var operation = new ChecksumVerifyOperation(item.Entry.FullPath);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, 1), result =>
            result.Outcome == OperationOutcome.Cancelled ? Task.CompletedTask : _dialogs.ShowInfoAsync(Texts.ChecksumReportTitle, Texts.ChecksumReport(operation.Report)));
    }

    /// <summary>
    /// Commands → Compare files: two marked files in the active panel, or the file under the cursor in each panel.
    /// Null (after a hint) when there is no such pair. Files on a server are compared through temp copies.
    /// </summary>
    public async Task<DiffViewModel?> CompareFilesAsync()
    {
        var marked = ActivePanel.MarkedItems.Where(static i => !i.IsDirectory).ToList();
        (string Left, string Right)? pair = marked.Count == 2
            ? (marked[0].Entry.FullPath, marked[1].Entry.FullPath)
            : Left.CurrentItem is { IsDirectory: false, Entry.IsParentLink: false } l && Right.CurrentItem is { IsDirectory: false, Entry.IsParentLink: false } r
                ? (l.Entry.FullPath, r.Entry.FullPath)
                : null;
        if (pair is not { } files || ArchiveFileSystem.Locate(files.Left) is not null || ArchiveFileSystem.Locate(files.Right) is not null)
        {
            await _dialogs.ShowErrorAsync(Texts.CompareFilesHow);
            return null;
        }

        return new DiffViewModel(files.Left, files.Right, (path, token) =>
            RemotePath.IsRemote(path) ? RemoteDownloadOperation.ToTempAsync(Remote, path, token) : Task.FromResult(path));
    }

    /// <summary>Ctrl+Q: the other panel shows the file under the cursor.</summary>
    public QuickViewModel QuickView { get; } = new();

    /// <summary>Ctrl+Q again closes it. The view takes the place of the panel that is not active.</summary>
    public void ToggleQuickView()
    {
        if (QuickView.IsOpen)
        {
            QuickView.Close();
            return;
        }

        QuickView.Open(ActivePanel, replacesLeft: ReferenceEquals(ActivePanel, Right));
    }

    /// <summary>Ctrl+S: asks for a filter for the active panel; an empty answer removes it.</summary>
    public async Task FilterAsync()
    {
        var panel = ActivePanel;
        if (await panel.FilterByQuickSearchAsync())
        {
            return; // Ctrl+S while typing a quick search keeps just the matches
        }

        var filter = await _dialogs.AskFilterAsync(panel.Filter, createdKnown: !panel.IsRemote && !panel.IsInArchive);
        if (filter is not null)
        {
            await panel.SetFilterAsync(filter);
        }
    }

    /// <summary>Ctrl+B: all files below the folder in one list, or back.</summary>
    public async Task ToggleFlatViewAsync()
    {
        var panel = ActivePanel;
        if (!panel.IsFlat && (panel.IsRemote || panel.IsInArchive || panel.IsNetworkList))
        {
            await _dialogs.ShowErrorAsync(Texts.FlatNotHere);
            return;
        }

        await panel.ToggleFlatAsync();
    }

    /// <summary>Copies the names (or full paths) of the marked items, or of the one under the cursor, one per line.</summary>
    public void CopyNamesToClipboard(bool fullPaths)
    {
        var panel = ActivePanel;
        var items = panel.OperationSources;
        if (items.Count == 0 || _clipboard is null)
        {
            return;
        }

        var lines = items.Select(i => fullPaths ? i.Entry.FullPath : i.Name);
        _clipboard.SetText(string.Join(Environment.NewLine, lines) + (items.Count > 1 ? Environment.NewLine : string.Empty));
        panel.Status = Texts.CopiedToClipboard(items.Count, fullPaths);
    }

    /// <summary>Ctrl+T / Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab act on the active panel.</summary>
    public void NewTab() => ActivePanel.NewTab();

    public Task CloseTabAsync() => ActivePanel.CloseActiveTabAsync();

    public Task NextTabAsync(int step = 1) => ActivePanel.NextTabAsync(step);

    /// <summary>Enter on a folder with Ctrl: it opens in a new tab of this panel.</summary>
    public async Task OpenCurrentInNewTabAsync()
    {
        if (ActivePanel.CurrentItem is { IsDirectory: true, Entry.IsParentLink: false } item)
        {
            await ActivePanel.OpenInNewTabAsync(item.Entry.FullPath);
        }
    }

    /// <summary>Enter in the command line: "cd" and drive letters move the active panel, the rest runs in its folder.</summary>
    public async Task RunCommandLineAsync()
    {
        var action = CommandLine.Submit();
        switch (action.Kind)
        {
            case CommandKind.Navigate:
                await NavigateFromCommandAsync(action.Argument);
                break;
            case CommandKind.Run:
                try
                {
                    _launcher.RunCommand(action.Argument, ActivePanel.IsNetworkList ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : ActivePanel.Path);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
                {
                    Log.Warning(ex, "Cannot run {Command}", action.Argument);
                    await _dialogs.ShowErrorAsync(Texts.CannotRun(action.Argument, ex));
                }

                break;
        }
    }

    /// <summary>Ctrl+Enter: the name under the cursor goes to the command line.</summary>
    public void InsertCurrentName()
    {
        if (ActivePanel.CurrentItem is { Entry.IsParentLink: false } item)
        {
            CommandLine.Insert(item.Name);
        }
    }

    /// <summary>Ctrl+P: the active panel's folder goes to the command line.</summary>
    public void InsertCurrentPath() => CommandLine.Insert(ActivePanel.Path);

    private async Task NavigateFromCommandAsync(string argument)
    {
        if (argument.Length == 0)
        {
            await ActivePanel.NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return;
        }

        if (argument == "..")
        {
            await ActivePanel.GoUpAsync();
            return;
        }

        if (RemotePath.IsRemote(argument))
        {
            await ConnectAsync(argument);
            return;
        }

        // on a server panel "/x" is the server's folder, not the local disk
        if (RemotePath.Parse(ActivePanel.Path) is { } here && (argument.StartsWith('/') || !Path.IsPathRooted(argument)))
        {
            var remote = argument.StartsWith('/') ? here with { Inner = RemotePath.NormalizeInner(argument) } : here.Resolve(argument);
            await ActivePanel.NavigateAsync(remote.FullPath);
            return;
        }

        if (argument.Length == 3 && argument[1] == ':' && argument[2] == '\\')
        {
            await ActivePanel.ChangeDriveAsync(argument);
            return;
        }

        var target = Path.IsPathRooted(argument) ? argument : Path.Combine(ActivePanel.Path, argument);
        if (!await ActivePanel.NavigateAsync(target))
        {
            await _dialogs.ShowErrorAsync(Texts.CannotOpen(target, new DirectoryNotFoundException()));
        }
    }

    public OperationQueueViewModel Operations { get; }

    public PanelViewModel Left { get; }

    public PanelViewModel Right { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InactivePanel))]
    public partial PanelViewModel ActivePanel { get; private set; }

    public PanelViewModel InactivePanel => ReferenceEquals(ActivePanel, Left) ? Right : Left;

    public async Task InitializeAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(systemRoot))
        {
            systemRoot = home;
        }

        await Task.WhenAll(
            Left.NavigateWithFallbackAsync(NullIfEmpty(Settings.Left.Path), home),
            Right.NavigateWithFallbackAsync(NullIfEmpty(Settings.Right.Path), systemRoot),
            Left.RefreshDrivesAsync(),
            Right.RefreshDrivesAsync());
    }

    public void Activate(PanelViewModel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (ReferenceEquals(panel, ActivePanel))
        {
            return;
        }

        ActivePanel.IsActive = false;
        ActivePanel = panel;
        panel.IsActive = true;
    }

    public void SwitchPanel() => Activate(InactivePanel);

    /// <summary>Row density, the same in both panels.</summary>
    public void SetCompactRows(bool compact)
    {
        Left.CompactRows = compact;
        Right.CompactRows = compact;
    }

    public void ToggleCompactRows() => SetCompactRows(!Left.CompactRows);

    /// <summary>The attributes column, the same in both panels.</summary>
    public void SetShowAttributes(bool show)
    {
        Left.ShowAttributes = show;
        Right.ShowAttributes = show;
    }

    public void ToggleShowAttributes() => SetShowAttributes(!Left.ShowAttributes);

    private readonly IDiskImages? _disks;
    private readonly Core.Undo.IRecycleBinRestore? _restore;
    private readonly List<Core.Undo.UndoEntry> _undo = [];

    /// <summary>How many steps Ctrl+Z can go back (this session only).</summary>
    public const int UndoLimit = 50;

    public bool CanUndo => _undo.Count > 0;

    /// <summary>The step Ctrl+Z would reverse ("Kopiowanie: 3 elementy"), or null.</summary>
    public string? UndoTitle => _undo.Count > 0 ? _undo[^1].Title : null;

    private void RememberUndo(string title, IReadOnlyList<Core.Undo.UndoAction> actions)
    {
        if (actions.All(static a => a.Kind == Core.Undo.UndoKind.Overwritten))
        {
            return; // nothing that could be reversed
        }

        PostToUi(() =>
        {
            _undo.Add(new Core.Undo.UndoEntry(title, actions, DateTime.Now));
            if (_undo.Count > UndoLimit)
            {
                _undo.RemoveAt(0);
            }

            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(UndoTitle));
        });
    }

    /// <summary>Every finished operation that recorded what it did becomes an undo step (an undo itself does not).</summary>
    private void OnOperationFinished(object? sender, OperationFinishedEventArgs e)
    {
        if (e.Operation is Core.Undo.IUndoable { Journal: { IsEmpty: false } journal } && e.Operation is not Core.Undo.UndoOperation)
        {
            RememberUndo(e.Title, journal.Actions);
        }
    }

    /// <summary>
    /// Ctrl+Z: reverses the latest recorded step after saying what will happen (created items to the Recycle Bin,
    /// moved items back, recycled items restored, attributes back; overwritten content cannot return).
    /// </summary>
    public async Task UndoAsync()
    {
        if (_undo.Count == 0)
        {
            ActivePanel.Status = Texts.NothingToUndo;
            return;
        }

        var entry = _undo[^1];
        if (!await _dialogs.ConfirmUndoAsync(entry.Title, Texts.UndoDetails(entry)))
        {
            return;
        }

        _undo.RemoveAt(_undo.Count - 1);
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(UndoTitle));
        var operation = new Core.Undo.UndoOperation(entry, _bin, _restore);
        await Operations.Enqueue(operation, Texts.UndoOperationTitle(entry.Title), result => AfterOperationAsync(null, result));
    }
    private readonly IFileSystem _fsForDrives;

    private static readonly string[] ImageExtensions = [".iso", ".img", ".vhd", ".vhdx"];

    /// <summary>A disk image file Windows can attach as a drive.</summary>
    public static bool IsDiskImage(string name) => ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>How long to wait for the drive of a just attached image to appear.</summary>
    public TimeSpan AttachWait { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Files → Attach as drive: the image under the cursor becomes a drive (Windows' own mount; a VHD asks for
    /// administrator rights) and the panel opens it.
    /// </summary>
    public async Task AttachImageAsync()
    {
        var panel = ActivePanel;
        if (_disks is null || panel.CurrentItem is not { IsDirectory: false, Entry.IsParentLink: false } item || !IsDiskImage(item.Name)
            || panel.IsRemote || panel.IsInArchive || panel.IsNetworkList)
        {
            await _dialogs.ShowErrorAsync(Texts.NotADiskImage);
            return;
        }

        var before = (await Task.Run(_fsForDrives.GetDrives)).Select(static d => d.Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Run(() => _disks.Attach(item.Entry.FullPath));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warning(ex, "Cannot attach {Image}", item.Entry.FullPath);
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return;
        }

        var deadline = DateTime.UtcNow + AttachWait;
        while (DateTime.UtcNow < deadline)
        {
            var added = (await Task.Run(_fsForDrives.GetDrives)).FirstOrDefault(d => !before.Contains(d.Root) && d.IsReady);
            if (added is not null)
            {
                await Task.WhenAll(Left.RefreshDrivesAsync(), Right.RefreshDrivesAsync());
                await panel.ChangeDriveAsync(added.Root);
                panel.Status = Texts.ImageMounted(item.Name, added.Root);
                return;
            }

            await Task.Delay(300);
        }

        panel.Status = Texts.ImageMountedSomewhere(item.Name);
    }

    /// <summary>
    /// Files → Detach drive: the drive this panel shows (an attached image, a USB stick) is ejected; panels on it move
    /// to the user's folder first so nothing holds it open. The system drive is never offered.
    /// </summary>
    public async Task DetachDriveAsync()
    {
        var panel = ActivePanel;
        var root = panel.CurrentRoot;
        var system = Path.GetPathRoot(Environment.SystemDirectory);
        if (_disks is null || panel.IsRemote || panel.IsNetworkList || string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)
            || string.Equals(root, system, StringComparison.OrdinalIgnoreCase))
        {
            await _dialogs.ShowErrorAsync(Texts.CannotEject(root ?? panel.Path));
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[] { Left, Right }.Where(p => string.Equals(p.CurrentRoot, root, StringComparison.OrdinalIgnoreCase)))
        {
            await p.NavigateAsync(home);
        }

        try
        {
            await Task.Run(() => _disks.Detach(root));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Log.Warning(ex, "Cannot detach {Root}", root);
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return;
        }

        // Ejecting finishes a moment after the verb returns: wait for the drive to go before refreshing the lists.
        var deadline = DateTime.UtcNow + AttachWait;
        while (DateTime.UtcNow < deadline && (await Task.Run(_fsForDrives.GetDrives)).Any(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase)))
        {
            await Task.Delay(300);
        }

        await Task.WhenAll(Left.RefreshDrivesAsync(), Right.RefreshDrivesAsync());
    }

    /// <summary>Folder sizes measured in the background, the same in both panels.</summary>
    public void SetFolderSizes(bool on)
    {
        Left.FolderSizes = on;
        Right.FolderSizes = on;
    }

    public void ToggleFolderSizes() => SetFolderSizes(!Left.FolderSizes);

    /// <summary>Shows <paramref name="column"/> in the active panel's detail column, or hides it when it is already shown.</summary>
    public void ToggleDetail(Core.Details.DetailColumn column) =>
        ActivePanel.Detail = ActivePanel.Detail == column ? Core.Details.DetailColumn.None : column;

    /// <summary>Light, dark or Windows' choice; the windows follow this property live.</summary>
    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    /// <summary>Panel list font, the same in both panels. Size is kept within what a 22–26 px row can show.</summary>
    public void SetFont(string family, double size)
    {
        family = string.IsNullOrWhiteSpace(family) ? AppSettings.DefaultPanelFont : family.Trim();
        size = double.IsFinite(size) ? Math.Clamp(size, MinFontSize, MaxFontSize) : AppSettings.DefaultPanelFontSize;
        Left.FontFamily = family;
        Right.FontFamily = family;
        Left.FontSize = size;
        Right.FontSize = size;
    }

    public const double MinFontSize = 10;
    public const double MaxFontSize = 18;

    public Task ToggleHiddenAsync()
    {
        var show = !Left.ShowHidden;
        return Task.WhenAll(Left.SetShowHiddenAsync(show), Right.SetShowHiddenAsync(show));
    }

    /// <summary>F5.</summary>
    public Task CopyAsync() => TransferAsync(TransferKind.Copy);

    /// <summary>F6.</summary>
    public Task MoveAsync() => TransferAsync(TransferKind.Move);

    /// <summary>F7: new folder in the active panel; the cursor lands on it.</summary>
    /// <summary>F4: the file under the cursor in the editor from settings. Folders are left alone.</summary>
    public async Task EditCurrentAsync()
    {
        if (ActivePanel.CurrentItem is not { IsDirectory: false } item)
        {
            return;
        }

        if (RemotePath.IsRemote(item.Entry.FullPath))
        {
            string temp;
            try
            {
                ActivePanel.Status = Texts.Downloading(item.Name);
                temp = await RemoteDownloadOperation.ToTempAsync(Remote, item.Entry.FullPath, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
                return;
            }
            finally
            {
                await ActivePanel.RefreshAsync();
            }

            await EditAsync(temp);
            WatchForUpload(temp, item.Entry.FullPath);
            return;
        }

        await EditAsync(await MaterializeAsync(item));
    }

    /// <summary>F3: a local path for the viewer; a server file is downloaded to a temp copy first.</summary>
    public Task<string> ViewPathAsync(FileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RemotePath.IsRemote(item.Entry.FullPath)
            ? RemoteDownloadOperation.ToTempAsync(Remote, item.Entry.FullPath, CancellationToken.None)
            : MaterializeAsync(item);
    }

    /// <summary>
    /// F4 on a server file edits a temp copy. Each time the editor saves it, the user is asked whether to upload it back.
    /// The watch lasts until the app closes.
    /// </summary>
    private void WatchForUpload(string temp, string remotePath)
    {
        var location = RemotePath.Parse(remotePath)!;
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(temp)!, Path.GetFileName(temp))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        var uploaded = File.GetLastWriteTimeUtc(temp);
        var busy = 0;
        CancellationTokenSource? debounce = null;

        async Task AskAsync()
        {
            if (!File.Exists(temp) || File.GetLastWriteTimeUtc(temp) == uploaded || Interlocked.Exchange(ref busy, 1) == 1)
            {
                return;
            }

            try
            {
                uploaded = File.GetLastWriteTimeUtc(temp);
                if (!await _dialogs.ConfirmUploadBackAsync(location.Name, location.Host))
                {
                    return;
                }

                await RemoteUploadOperation.UploadFileAsync(Remote, temp, remotePath, CancellationToken.None);
                foreach (var panel in new[] { Left, Right })
                {
                    if (string.Equals(panel.Path, location.Parent?.FullPath, StringComparison.Ordinal))
                    {
                        await panel.RefreshAsync();
                        panel.Status = Texts.Uploaded(location.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(ex, "Upload back of {Path} failed", remotePath);
                await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            }
            finally
            {
                Volatile.Write(ref busy, 0);
            }
        }

        void Changed()
        {
            // editors write in bursts (truncate, write, rename): ask once things are quiet
            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref debounce, cts)?.Cancel();
            _ = Task.Delay(700, cts.Token).ContinueWith(
                t =>
                {
                    if (!t.IsCanceled)
                    {
                        PostToUi(() => _ = AskAsync());
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        watcher.Changed += (_, _) => Changed();
        watcher.Created += (_, _) => Changed();
        watcher.Renamed += (_, _) => Changed();
        watcher.EnableRaisingEvents = true;
        _uploadWatchers.Add(watcher);
    }

    /// <summary>A real path for the file under the cursor: inside an archive that means a temp copy.</summary>
    public static Task<string> MaterializeAsync(FileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ArchiveFileSystem.Locate(item.Entry.FullPath) is { IsRoot: false }
            ? Task.Run(() => ArchiveExtractOperation.ExtractToTemp(item.Entry.FullPath))
            : Task.FromResult(item.Entry.FullPath);
    }

    /// <summary>
    /// Search results can mix files on disk with files inside archives. Operations that write need real files,
    /// so they stop with an explanation when archive entries are among the selection.
    /// </summary>
    private async Task<bool> RefuseArchiveEntriesAsync(IReadOnlyList<FileItem> items)
    {
        if (ActivePanel.IsInArchive || !items.Any(i => ArchiveFileSystem.Locate(i.Entry.FullPath) is { IsRoot: false }))
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(Texts.ResultsInArchive);
        return true;
    }

    /// <summary>
    /// The archive a panel shows, when it can be changed (zip, tar, tar.gz); null after telling the user why not.
    /// Only call for a panel that is inside an archive.
    /// </summary>
    private async Task<ArchiveLocation?> EditableArchiveAsync(string path)
    {
        if (ArchiveFileSystem.Locate(path) is not { } location)
        {
            await _dialogs.ShowErrorAsync(Texts.ArchiveReadOnly);
            return null;
        }

        if (!ArchiveUpdateOperation.CanEdit(location.ArchiveFile))
        {
            await _dialogs.ShowErrorAsync(Texts.ArchiveFormatReadOnly);
            return null;
        }

        return location;
    }

    /// <summary>Inner path of an entry the panel lists inside an archive ("/"-separated).</summary>
    private static string InnerOf(FileItem item) => ArchiveFileSystem.Locate(item.Entry.FullPath)?.Inner ?? item.Name;

    private static string JoinInner(string folder, string name)
    {
        var tail = name.Trim().Replace('\\', '/').Trim('/');
        return folder.Length == 0 ? tail : folder.TrimEnd('/') + "/" + tail;
    }

    /// <summary>Things archives cannot do (Shift+F4 new file, Alt+F5, moving out of an archive) say so instead of failing later.</summary>
    private async Task<bool> RefuseInsideArchiveAsync(PanelViewModel panel)
    {
        if (!panel.IsInArchive)
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(Texts.ArchiveReadOnly);
        return true;
    }

    /// <summary>Shift+F4: a new empty file in the active panel's folder, opened in the editor right away.</summary>
    public async Task NewFileAsync()
    {
        var panel = ActivePanel;
        if (await RefuseRemoteAsync(panel) || await RefuseInsideArchiveAsync(panel) || await RefuseGoneFolderAsync(panel))
        {
            return;
        }

        string? full = null;
        var name = string.Empty;
        while (full is null)
        {
            name = await _dialogs.AskNameAsync(NamePrompt.NewFile, name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var typed = name;
            (full, var askAgain) = await RunNameCommandAsync(panel, () => SimpleCommands.CreateFile(panel.Path, typed), ElevatedJob.CreateFile(panel.Path, typed));
            if (full is null && !askAgain)
            {
                return;
            }
        }

        RememberUndo(Texts.NewFileStep(Path.GetFileName(full)), [new Core.Undo.UndoAction(Core.Undo.UndoKind.Created, full)]);

        await RefreshPanelsShowingAsync(panel.Path, panel, Path.GetFileName(full));
        await EditAsync(full);
    }

    private async Task EditAsync(string path)
    {
        var editor = string.IsNullOrWhiteSpace(Settings.Editor) ? "notepad.exe" : Settings.Editor;
        try
        {
            _launcher.Edit(editor, path, ActivePanel.Path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warning(ex, "Cannot start editor {Editor}", editor);
            await _dialogs.ShowErrorAsync(Texts.CannotRun(editor, ex));
        }
    }

    public async Task MakeDirectoryAsync()
    {
        var panel = ActivePanel;
        if (await RefuseNetworkListAsync(panel) || await RefuseGoneFolderAsync(panel))
        {
            return;
        }

        ArchiveLocation? archive = null;
        if (panel.IsInArchive && (archive = await EditableArchiveAsync(panel.Path)) is null)
        {
            return;
        }

        var name = await _dialogs.AskNameAsync(NamePrompt.NewDirectory, string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (RemotePath.Parse(panel.Path) is { } server)
        {
            await MakeRemoteDirectoryAsync(panel, server, name);
            return;
        }

        if (archive is not null)
        {
            try
            {
                name = SimpleCommands.CleanName(name, allowSeparators: true);
            }
            catch (ArgumentException ex)
            {
                await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
                return;
            }

            var inner = JoinInner(archive.Inner, name);
            var top = inner[(archive.Inner.Length == 0 ? 0 : archive.Inner.TrimEnd('/').Length + 1)..].Split('/')[0];
            var operation = new ArchiveUpdateOperation(archive.ArchiveFile, ArchiveEdit.MakeFolder(inner));
            await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, 1), _ => RefreshPanelsShowingAsync(panel.Path, panel, top));
            return;
        }

        // "a\b" creates both levels: the topmost one that did not exist is what undo takes away. Measured on the
        // resolved path, so "x\..\y" names "y", not "x".
        var firstNew = FirstMissingLevel(panel.Path, name);
        while (await RunNameCommandAsync(panel, () => SimpleCommands.CreateDirectory(panel.Path, name), ElevatedJob.CreateDirectory(panel.Path, name)) is (null, var askAgain))
        {
            if (!askAgain || string.IsNullOrWhiteSpace(name = await _dialogs.AskNameAsync(NamePrompt.NewDirectory, name)))
            {
                return;
            }

            firstNew = FirstMissingLevel(panel.Path, name);
        }

        var levels = name.Trim().Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        if (firstNew is not null)
        {
            RememberUndo(Texts.NewFolderStep(name.Trim()), [new Core.Undo.UndoAction(Core.Undo.UndoKind.Created, firstNew, IsDirectory: true)]);
        }

        var first = levels[0];
        await RefreshPanelsShowingAsync(panel.Path, panel, first);
    }

    private static string? FirstMissingLevel(string baseDirectory, string name)
    {
        string full;
        try
        {
            full = ExactPath.Display(ExactPath.Full(Path.Combine(baseDirectory, SimpleCommands.CleanName(name, allowSeparators: true))));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null; // creating it fails as well and reports why
        }

        string? missing = null;
        for (var dir = full; !string.IsNullOrEmpty(dir) && !Directory.Exists(ExactPath.Native(dir)); dir = Path.GetDirectoryName(dir))
        {
            missing = dir;
        }

        return missing;
    }

    /// <summary>F7 on a server: "a/b" creates both levels; an existing last level is an error.</summary>
    private async Task MakeRemoteDirectoryAsync(PanelViewModel panel, RemoteLocation here, string name)
    {
        string[] parts;
        try
        {
            parts = SimpleCommands.CleanName(name, allowSeparators: true).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return;
        }

        try
        {
            await Remote.UseAsync(here.Root, async client =>
            {
                var at = here;
                for (var i = 0; i < parts.Length; i++)
                {
                    if (parts[i] is "." or "..")
                    {
                        at = parts[i] == ".." ? at.Parent ?? at : at;
                        continue;
                    }

                    at = at.Child(parts[i]);
                    var existing = await client.StatAsync(at.Inner, CancellationToken.None);
                    if (existing is null)
                    {
                        await client.CreateDirectoryAsync(at.Inner, CancellationToken.None);
                    }
                    else if (i == parts.Length - 1 || !existing.IsDirectory)
                    {
                        throw new OperationRuleException(RuleViolation.AlreadyExists, at.Name);
                    }
                }

                return true;
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
        }

        await panel.RefreshAsync(parts.Length > 0 ? parts[0] : null);
    }

    /// <summary>Shift+F6 on a server: a rename in place; an existing name is not overwritten.</summary>
    private async Task RenameRemoteAsync(PanelViewModel panel, FileItem item, RemoteLocation source)
    {
        var name = await _dialogs.AskNameAsync(NamePrompt.Rename, item.Name);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), item.Name, StringComparison.Ordinal))
        {
            return;
        }

        string newName;
        try
        {
            newName = SimpleCommands.CleanName(name, allowSeparators: false);
            if (newName.Contains('/', StringComparison.Ordinal) || newName.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{newName}' contains characters that are not allowed in a name.");
            }
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return;
        }

        var target = source.Parent!.Child(newName);
        try
        {
            await Remote.UseAsync(source.Root, async client =>
            {
                if (!string.Equals(newName, item.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (await client.StatAsync(target.Inner, CancellationToken.None) is not null)
                    {
                        throw new OperationRuleException(RuleViolation.AlreadyExists, newName);
                    }

                    await client.RenameAsync(source.Inner, target.Inner, CancellationToken.None);
                    return true;
                }

                // A change of case only. On a case-sensitive server "A.txt" may be another file, which FTP and S3
                // renames replace: only the exact name in the listing tells. A case-insensitive server may see source
                // and target as one entry, so hop via a temporary name.
                var siblings = await client.ListAsync(source.Parent!.Inner, CancellationToken.None);
                if (siblings.Any(s => string.Equals(s.Name, newName, StringComparison.Ordinal)))
                {
                    throw new OperationRuleException(RuleViolation.AlreadyExists, newName);
                }

                var hop = source.Parent!.Child(newName + ".pc-rename-" + Guid.NewGuid().ToString("N")[..8]);
                await client.RenameAsync(source.Inner, hop.Inner, CancellationToken.None);
                try
                {
                    await client.RenameAsync(hop.Inner, target.Inner, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await client.RenameAsync(hop.Inner, source.Inner, CancellationToken.None);
                    throw;
                }

                return true;
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return;
        }

        await RefreshPanelsShowingAsync(panel.Path, panel, newName);
    }

    /// <summary>Shift+F6: rename the item under the cursor in place.</summary>
    public async Task RenameCurrentAsync()
    {
        var panel = ActivePanel;
        if (panel.CurrentItem is not { Entry.IsParentLink: false } item || await RefuseNetworkListAsync(panel) || await RefuseGoneFolderAsync(panel))
        {
            return;
        }

        if (panel.IsInArchive)
        {
            await RenameInArchiveAsync(panel, item);
            return;
        }

        if (panel.IsRemote && RemotePath.Parse(item.Entry.FullPath) is { } remoteItem)
        {
            await RenameRemoteAsync(panel, item, remoteItem);
            return;
        }

        if (await RefuseArchiveEntriesAsync([item]))
        {
            return;
        }

        // In search results the row shows a relative path; the dialog offers the file name only.
        var current = Path.GetFileName(item.Entry.FullPath);
        string? renamed = null;
        var name = current;
        while (renamed is null)
        {
            name = await _dialogs.AskNameAsync(NamePrompt.Rename, name);
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, current, StringComparison.Ordinal))
            {
                return;
            }

            var typed = name;
            (renamed, var askAgain) = await RunNameCommandAsync(panel, () => SimpleCommands.Rename(item.Entry.FullPath, typed), ElevatedJob.Rename(item.Entry.FullPath, typed));
            if (renamed is null && !askAgain)
            {
                return;
            }
        }

        if (string.Equals(Path.GetFileName(renamed), current, StringComparison.Ordinal))
        {
            return; // " a.txt " is cleaned back to the same name: nothing changed, nothing to undo
        }

        RememberUndo(Texts.RenameStep(current, Path.GetFileName(renamed)), [new Core.Undo.UndoAction(Core.Undo.UndoKind.Moved, renamed, item.Entry.FullPath, item.IsDirectory)]);

        panel.RenameResults([(item.Entry.FullPath, renamed)]);
        await RefreshPanelsShowingAsync(panel.Path, panel, panel.IsShowingResults ? Path.GetRelativePath(panel.Path, renamed) : Path.GetFileName(renamed));
    }

    /// <summary>Shift+F6 inside a zip/tar: the entry (a folder with everything below) gets the new name; the archive is rewritten.</summary>
    private async Task RenameInArchiveAsync(PanelViewModel panel, FileItem item)
    {
        if (await EditableArchiveAsync(panel.Path) is not { } archive)
        {
            return;
        }

        var name = await _dialogs.AskNameAsync(NamePrompt.Rename, item.Name);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), item.Name, StringComparison.Ordinal))
        {
            return;
        }

        string newName;
        try
        {
            newName = SimpleCommands.CleanName(name, allowSeparators: false);
            if (newName.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{newName}' contains characters that are not allowed in a name.");
            }
        }
        catch (ArgumentException ex)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return;
        }

        var operation = new ArchiveUpdateOperation(archive.ArchiveFile, ArchiveEdit.RenameEntry(InnerOf(item), newName));
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, 1), _ => RefreshPanelsShowingAsync(panel.Path, panel, newName));
    }

    /// <summary>F8/Del to the Recycle Bin; Shift+F8/Shift+Del permanently. Inside a zip/tar the entries are removed from the archive.</summary>
    public async Task DeleteAsync(bool permanent)
    {
        var panel = ActivePanel;
        var items = panel.OperationSources;
        if (await RefuseArchiveEntriesAsync(items))
        {
            return;
        }

        if (items.Count == 0)
        {
            return;
        }

        if (panel.IsInArchive)
        {
            if (await EditableArchiveAsync(panel.Path) is not { } archive)
            {
                return;
            }

            var folders = items.Count(static i => i.IsDirectory);
            if (!await _dialogs.ConfirmDeleteAsync(items.Count - folders, folders, items[0].Name, permanent: true))
            {
                return;
            }

            var edit = new ArchiveUpdateOperation(archive.ArchiveFile, ArchiveEdit.Remove(items.Select(InnerOf).ToArray()));
            await Operations.Enqueue(edit, Texts.OperationTitle(edit.Kind, items.Count), result => AfterOperationAsync(panel, result));
            return;
        }

        if (panel.IsRemote)
        {
            // servers have no Recycle Bin: always the permanent question
            var folders = items.Count(static i => i.IsDirectory);
            if (!await _dialogs.ConfirmDeleteAsync(items.Count - folders, folders, items[0].Name, permanent: true))
            {
                return;
            }

            var removal = new RemoteDeleteOperation(Remote, items.Select(static i => i.Entry.FullPath).ToArray());
            await Operations.Enqueue(removal, Texts.OperationTitle(removal.Kind, items.Count), result => AfterOperationAsync(panel, result));
            return;
        }

        var dirs = items.Count(static i => i.IsDirectory);
        if (!await _dialogs.ConfirmDeleteAsync(items.Count - dirs, dirs, items[0].Name, permanent))
        {
            return;
        }

        var paths = items.Select(static i => i.Entry.FullPath).ToArray();
        IOperation operation = permanent ? new DeleteOperation(paths) : new RecycleOperation(paths, _bin);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), result => AfterOperationAsync(panel, result));
    }

    /// <summary>Num+ (mark) / Num− (unmark) by a mask the user types; the last mask is offered again.</summary>
    public async Task MarkByMaskAsync(bool mark)
    {
        var mask = await _dialogs.AskMaskAsync(mark, _lastMask);
        if (string.IsNullOrWhiteSpace(mask))
        {
            return;
        }

        _lastMask = mask;
        ActivePanel.MarkByMask(mask, mark);
    }

    public const double MinPanelSplit = 0.2;
    public const double MaxPanelSplit = 0.8;

    public void Save(WindowSettings? window, double? panelSplit = null)
    {
        if (panelSplit is { } split && !double.IsNaN(split))
        {
            Settings.PanelSplit = Math.Clamp(split, MinPanelSplit, MaxPanelSplit);
        }

        Settings.Left = Left.CaptureSettings();
        Settings.Right = Right.CaptureSettings();
        Settings.ShowHidden = Left.ShowHidden;
        Settings.CompactRows = Left.CompactRows;
        Settings.ShowAttributes = Left.ShowAttributes;
        Settings.FolderSizes = Left.FolderSizes;
        Settings.Theme = Theme;
        Settings.PanelFont = Left.FontFamily;
        Settings.PanelFontSize = Left.FontSize;
        Settings.CommandHistory = CommandLine.History.ToList();
        Settings.ActivePanel = ReferenceEquals(ActivePanel, Right) ? PanelSide.Right : PanelSide.Left;
        if (window is not null)
        {
            Settings.Window = window;
        }

        try
        {
            _store.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Cannot save settings to {Path}", _store.FilePath);
        }
    }

    private Task TransferAsync(TransferKind kind)
    {
        var source = ActivePanel;
        var other = InactivePanel;
        var items = source.OperationSources;
        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        var defaultTarget = string.IsNullOrEmpty(other.Path) ? source.Path : WithSeparator(other.Path);
        if (kind == TransferKind.Move && items.Count == 1 && string.Equals(other.Path, source.Path, StringComparison.OrdinalIgnoreCase))
        {
            defaultTarget = items[0].Name; // both panels show the same folder: F6 is a rename
        }

        return TransferItemsAsync(kind, source, items, defaultTarget, other);
    }

    /// <summary>
    /// Drag and drop: <paramref name="paths"/> dropped on <paramref name="targetFolder"/> (a panel's folder or a folder row).
    /// The usual F5/F6 question follows with the target filled in, so a drop can still be checked or cancelled.
    /// <paramref name="from"/> is the panel the drag started in, or null for files from Explorer or the desktop.
    /// </summary>
    public Task DropAsync(IReadOnlyList<string> paths, string targetFolder, bool move, PanelViewModel? from)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrEmpty(targetFolder);
        if (NetworkPath.IsVirtual(targetFolder) || from is { IsNetworkList: true })
        {
            return _dialogs.ShowErrorAsync(Texts.NetworkListNotHere);
        }

        List<FileItem> items;
        if (from is not null)
        {
            var byPath = from.Items.Where(static i => !i.Entry.IsParentLink).ToDictionary(static i => i.Entry.FullPath, StringComparer.Ordinal);
            items = paths.Select(p => byPath.GetValueOrDefault(p)).OfType<FileItem>().ToList();
            Activate(from);
        }
        else
        {
            items = paths.Where(static p => !string.IsNullOrEmpty(p)).Select(static p => new FileItem(new FileEntry
            {
                Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(p)),
                FullPath = p,
                IsDirectory = Directory.Exists(p),
            })).ToList();
        }

        if (items.Count == 0)
        {
            return Task.CompletedTask;
        }

        var shownIn = new[] { Left, Right }.FirstOrDefault(p => string.Equals(p.Path, targetFolder, StringComparison.OrdinalIgnoreCase));
        return TransferItemsAsync(move ? TransferKind.Move : TransferKind.Copy, from, items, WithSeparator(targetFolder), shownIn);
    }

    /// <summary>F5/F6 and drops: ask for the target, then copy, move, extract, pack into an archive, upload or download.</summary>
    /// <param name="source">The panel the items come from; null for a drop from outside the app (local files).</param>
    /// <param name="other">The panel that shows the target, if any (for server targets and the "same folder" checks).</param>
    private async Task TransferItemsAsync(TransferKind kind, PanelViewModel? source, IReadOnlyList<FileItem> items, string defaultTarget, PanelViewModel? other)
    {
        if (kind == TransferKind.Move && source is not null && await RefuseInsideArchiveAsync(source))
        {
            return;
        }

        if (await RefuseArchiveEntriesAsync(items))
        {
            return;
        }

        var input = await _dialogs.AskTransferTargetAsync(kind, items.Count, items[0].Name, defaultTarget);
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (NetworkPath.IsVirtual(input.Trim()))
        {
            await _dialogs.ShowErrorAsync(Texts.NetworkListNotHere);
            return;
        }

        if (source is { IsRemote: true } || RemotePath.IsRemote(input.Trim()))
        {
            await RemoteTransferAsync(kind, source, other, items, input.Trim());
            return;
        }

        var sourceFolder = source?.Path ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(items[0].Entry.FullPath)) ?? string.Empty;
        TransferTarget target;
        try
        {
            target = TargetResolver.Resolve(input, sourceFolder, items.Count);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return;
        }

        if (ArchiveFileSystem.Locate(target.Directory) is not null)
        {
            await AddToArchiveAsync(kind, source, items, target);
            return;
        }

        var paths = items.Select(static i => i.Entry.FullPath).ToArray();
        IOperation operation = source is { IsInArchive: true }
            ? new ArchiveExtractOperation(paths, target)
            : new TransferOperation(kind, paths, target, Settings.CopyParallelism);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), result => AfterOperationAsync(source, result));
    }

    /// <summary>
    /// F5/F6 with a server on either side: download, upload, or (F6 within one server) a rename on the server.
    /// A typed target ending with "/" or "\", an existing folder, or several sources mean "into this folder";
    /// otherwise the last name is the new name. A relative target is relative to the source folder.
    /// </summary>
    private async Task RemoteTransferAsync(TransferKind kind, PanelViewModel? source, PanelViewModel? other, IReadOnlyList<FileItem> items, string input)
    {
        var move = kind == TransferKind.Move;
        var paths = items.Select(static i => i.Entry.FullPath).ToArray();
        var from = source is null ? null : RemotePath.Parse(source.Path);
        var to = RemotePath.ParseTyped(input) ?? (from is not null && !Path.IsPathRooted(input) ? from.Resolve(input) : null);

        if (to is null)
        {
            // server → disk
            if (from is null)
            {
                return;
            }

            TransferTarget target;
            try
            {
                target = TargetResolver.Resolve(input, other is null or { IsRemote: true } or { IsNetworkList: true } ? Environment.CurrentDirectory : other.Path, items.Count);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
                return;
            }

            if (ArchiveFileSystem.Locate(target.Directory) is not null)
            {
                await _dialogs.ShowErrorAsync(Texts.RemoteAndArchive);
                return;
            }

            var download = new RemoteDownloadOperation(Remote, paths, target, move);
            await Operations.Enqueue(download, Texts.OperationTitle(download.Kind, items.Count), result => AfterOperationAsync(source, result));
            return;
        }

        // the target is on a server: a folder, or a folder plus a new name
        var endsAsFolder = input.EndsWith('/') || input.EndsWith('\\') || items.Count > 1 || string.Equals(to.FullPath, RemotePath.Parse(other?.Path)?.FullPath, StringComparison.Ordinal);
        if (!endsAsFolder && !to.IsRoot)
        {
            try
            {
                endsAsFolder = await Remote.UseAsync(to.Root, c => c.StatAsync(to.Inner, CancellationToken.None), CancellationToken.None) is { IsDirectory: true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
                return;
            }
        }

        var folder = endsAsFolder || to.IsRoot ? to : to.Parent!;
        var newName = endsAsFolder || to.IsRoot ? null : to.Name;

        if (from is not null)
        {
            if (!string.Equals(from.Root, to.Root, StringComparison.OrdinalIgnoreCase))
            {
                await _dialogs.ShowErrorAsync(Texts.RemoteOtherServer);
                return;
            }

            if (!move)
            {
                await _dialogs.ShowErrorAsync(Texts.RemoteCopyOnServer);
                return;
            }

            await MoveOnServerAsync(source!, items, folder, newName);
            return;
        }

        if (source is { IsInArchive: true })
        {
            await _dialogs.ShowErrorAsync(Texts.RemoteAndArchive);
            return;
        }

        var upload = new RemoteUploadOperation(Remote, paths, folder.FullPath, newName, move);
        await Operations.Enqueue(upload, Texts.OperationTitle(upload.Kind, items.Count), result => AfterOperationAsync(source, result));
    }

    /// <summary>F6 within one server: each item is renamed into the target folder (no data moves).</summary>
    private async Task MoveOnServerAsync(PanelViewModel source, IReadOnlyList<FileItem> items, RemoteLocation folder, string? newName)
    {
        try
        {
            await Remote.UseAsync(folder.Root, async client =>
            {
                foreach (var item in items)
                {
                    var location = RemotePath.Parse(item.Entry.FullPath)!;
                    var target = folder.Child(newName ?? location.Name);
                    if (string.Equals(target.Inner, location.Inner, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (target.Inner.StartsWith(location.Inner + "/", StringComparison.Ordinal))
                    {
                        throw new OperationRuleException(RuleViolation.IntoItself, location.FullPath);
                    }

                    if (await client.StatAsync(target.Inner, CancellationToken.None) is not null)
                    {
                        throw new OperationRuleException(RuleViolation.AlreadyExists, target.Name);
                    }

                    await client.RenameAsync(location.Inner, target.Inner, CancellationToken.None);
                }

                return true;
            }, CancellationToken.None);
            source.ClearMarks();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
        }

        await Task.WhenAll(Left.RefreshAfterChangeAsync(), Right.RefreshAfterChangeAsync());
    }

    /// <summary>
    /// F5/F6 from disk into a panel inside a zip/tar: the files go into the archive folder (F6 removes them from disk
    /// once the new archive is in place). A typed new name becomes a folder inside the archive.
    /// </summary>
    private async Task AddToArchiveAsync(TransferKind kind, PanelViewModel? source, IReadOnlyList<FileItem> items, TransferTarget target)
    {
        if (source is { IsInArchive: true })
        {
            await _dialogs.ShowErrorAsync(Texts.ArchiveToArchive);
            return;
        }

        if (await EditableArchiveAsync(target.Directory) is not { } archive)
        {
            return;
        }

        var into = target.NewName is { } folder ? JoinInner(archive.Inner, folder) : archive.Inner;
        var paths = items.Select(static i => i.Entry.FullPath).ToArray();
        var operation = new ArchiveUpdateOperation(archive.ArchiveFile, ArchiveEdit.Add(paths, into), moveSources: kind == TransferKind.Move);
        await Operations.Enqueue(operation, Texts.OperationTitle(operation.Kind, items.Count), result => AfterOperationAsync(source, result));
    }

    /// <summary>Ctrl+M: a rename session for the marked items (or the one under the cursor). Null when there is nothing to rename.</summary>
    public async Task<MultiRenameViewModel?> CreateMultiRenameAsync()
    {
        var panel = ActivePanel;
        if (await RefuseRemoteAsync(panel))
        {
            return null;
        }

        if (panel.IsInArchive)
        {
            await _dialogs.ShowErrorAsync(Texts.ArchiveReadOnly);
            return null;
        }

        var items = panel.OperationSources;
        if (items.Count == 0 || await RefuseArchiveEntriesAsync(items))
        {
            return null;
        }

        return new MultiRenameViewModel(items);
    }

    /// <summary>After Ctrl+M or its undo: search results follow the new names, then both panels refresh.</summary>
    public Task AfterMultiRenameAsync(Core.Rename.RenameOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Done.Count > 0)
        {
            RememberUndo(Texts.MultiRenameStep(outcome.Done.Count), outcome.Done.Select(static d => new Core.Undo.UndoAction(Core.Undo.UndoKind.Moved, d.To, d.From, Directory.Exists(d.To))).ToList());
        }

        Left.RenameResults(outcome.Done);
        Right.RenameResults(outcome.Done);
        return Task.WhenAll(Left.RefreshAfterChangeAsync(), Right.RefreshAfterChangeAsync());
    }

    /// <summary>Alt+F7 "to panel": the active panel lists what the search found.</summary>
    public void ShowSearchResults(string what, string root, IEnumerable<Core.Model.FileEntry> entries)
    {
        ActivePanel.ShowResults(what, root, entries);
        RequestPanelFocus();
    }

    /// <summary>
    /// Runs a command on a typed name. On failure AskAgain tells whether the name was the problem (bad, too long,
    /// taken), so the prompt comes back with the typed text; a panel whose folder is gone moves up.
    /// </summary>
    private async Task<(string? Path, bool AskAgain)> RunNameCommandAsync(PanelViewModel panel, Func<string> command, ElevatedJob elevated)
    {
        var (path, error) = await RunCommandAsync(command, elevated);
        if (path is null && panel.IsFolderGone)
        {
            await panel.RefreshAsync();
            return (null, false);
        }

        return (path, error is not null && UserMessages.IsAboutName(error));
    }

    private async Task<bool> RefuseGoneFolderAsync(PanelViewModel panel)
    {
        if (!panel.IsFolderGone)
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(UserMessages.FolderGone(panel.Path));
        await panel.RefreshAsync();
        return true;
    }

    /// <summary>
    /// F7 / Shift+F4 / Shift+F6 on disk. On "access denied" offers to try again as administrator, in the
    /// helper process. Returns the resulting path, or null and the error that was shown (none when the user declined).
    /// </summary>
    private async Task<(string? Path, Exception? Error)> RunCommandAsync(Func<string> command, ElevatedJob elevated)
    {
        Exception error;
        try
        {
            return (command(), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex;
        }

        if (_elevation is null || !ElevationRules.IsAccessDenied(error))
        {
            await _dialogs.ShowErrorAsync(UserMessages.Describe(error));
            return (null, error);
        }

        if (!await _dialogs.ConfirmElevateAsync(UserMessages.Describe(error)))
        {
            return (null, null);
        }

        try
        {
            var helper = await _elevation.ConnectAsync(CancellationToken.None);
            return (await helper.RunCommandAsync(elevated), null);
        }
        catch (OperationCanceledException)
        {
            await _dialogs.ShowErrorAsync(Texts.ElevationRefused);
            return (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warning(ex, "Elevated {Kind} failed", elevated.Kind);
            await _dialogs.ShowErrorAsync(UserMessages.Describe(ex));
            return (null, ex);
        }
    }

    private async Task AfterOperationAsync(PanelViewModel? source, OperationResult result)
    {
        if (result.Outcome == OperationOutcome.Completed)
        {
            source?.ClearMarks();
        }

        await Task.WhenAll(Left.RefreshAfterChangeAsync(), Right.RefreshAfterChangeAsync());
    }

    private async Task RefreshPanelsShowingAsync(string path, PanelViewModel focusPanel, string focusName)
    {
        await focusPanel.RefreshAsync(focusName);
        var other = ReferenceEquals(focusPanel, Left) ? Right : Left;
        if (string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            await other.RefreshAfterChangeAsync();
        }
    }

    private static string WithSeparator(string path) =>
        RemotePath.IsRemote(path) ? (path.EndsWith('/') ? path : path + "/")
        : Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    public void Dispose()
    {
        _pcePasswords.Clear();
        Left.Dispose();
        Right.Dispose();
        foreach (var watcher in _uploadWatchers)
        {
            watcher.Dispose();
        }

        _uploadWatchers.Clear();
        _ = Remote.DisposeAsync().AsTask();
        QuickView.Dispose();
    }

    /// <summary>Without the app's dialogs every login is cancelled and every unknown server rejected.</summary>
    private sealed class NoRemotePrompts : IRemotePrompts
    {
        public Task<TrustChoice> ConfirmHostAsync(HostIdentity identity, string? known, CancellationToken cancellationToken) => Task.FromResult(TrustChoice.Reject);

        public Task<CredentialAnswer?> AskCredentialsAsync(RemoteSite site, bool passphrase, string? problem, CancellationToken cancellationToken) =>
            Task.FromResult<CredentialAnswer?>(null);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
