using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PlainCommander.App.Helpers;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Navigation;
using PlainCommander.Core.Operations;
using PlainCommander.Presentation;

namespace PlainCommander.App.Services;

/// <summary>
/// All dialogs: command questions (F5…F8) and the prompts a running operation asks from its worker thread.
/// WinUI allows one ContentDialog at a time, so every dialog goes through one gate.
/// </summary>
internal sealed class DialogService : IDialogService, IOperationPrompts, PlainCommander.Core.Remote.IRemotePrompts, PlainCommander.Core.Archives.IArchivePasswordPrompt, IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DialogService(DispatcherQueue ui)
    {
        _ui = ui;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>A dialog is on screen; the window must not take the keyboard away from it.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Set by the main window once its content exists.</summary>
    public Window? Owner { get; set; }

    public Task<string?> AskTransferTargetAsync(TransferKind kind, int count, string firstName, string defaultTarget)
    {
        var copy = kind == TransferKind.Copy;
        var prompt = count == 1
            ? Loc.Format(copy ? "Dlg_CopyOne" : "Dlg_MoveOne", firstName)
            : Loc.Format(copy ? "Dlg_CopyMany" : "Dlg_MoveMany", count);
        return AskTextAsync(Loc.Get(copy ? "Dlg_CopyTitle" : "Dlg_MoveTitle"), prompt, defaultTarget, selectStem: false);
    }

    private static readonly (string Label, string Extension)[] PackFormats = [("ZIP", ".zip"), ("7z", ".7z"), ("TAR", ".tar"), ("TAR.GZ", ".tar.gz")];

    /// <summary>The archive's extension as the format list knows it ("" when typed otherwise).</summary>
    private static string PackExtensionOf(string name) =>
        name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ".tar.gz"
        : PackFormats.Select(f => f.Extension).OrderByDescending(e => e.Length).FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

    /// <summary>
    /// Alt+F5: the archive name, a format list that follows (and sets) its extension, and for 7z a password typed twice.
    /// </summary>
    public Task<PackAnswer?> AskPackTargetAsync(int count, string firstName, string defaultTarget, bool sevenZip) => OnUiAsync(async () =>
    {
        var formats = PackFormats.Where(f => sevenZip || f.Extension != ".7z").ToList();
        var body = new StackPanel { Spacing = 10, MinWidth = 460 };
        body.Children.Add(new TextBlock { Text = count == 1 ? Loc.Format("Dlg_PackOne", firstName) : Loc.Format("Dlg_PackMany", count), TextWrapping = TextWrapping.Wrap });
        var name = new TextBox { Text = defaultTarget };
        var format = new ComboBox { Header = Loc.Get("Dlg_PackFormat"), ItemsSource = formats.Select(f => f.Label).ToList(), MinWidth = 160 };
        var password = new PasswordBox { Header = Loc.Get("Dlg_PackPassword"), PlaceholderText = Loc.Get("Dlg_PackPasswordNone") };
        var repeat = new PasswordBox { Header = Loc.Get("Dlg_PackPasswordRepeat") };
        var secret = new StackPanel { Spacing = 8 };
        secret.Children.Add(password);
        secret.Children.Add(repeat);
        body.Children.Add(name);
        body.Children.Add(format);
        body.Children.Add(secret);

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Dlg_PackTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Dlg_Ok"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var syncing = false;
        bool Encryptable() => sevenZip && PackExtensionOf(name.Text) is ".7z" or ".zip";

        void Refresh()
        {
            var encryptable = Encryptable();
            secret.Visibility = encryptable ? Visibility.Visible : Visibility.Collapsed;
            password.Header = Loc.Get(PackExtensionOf(name.Text) == ".zip" ? "Dlg_PackPasswordZip" : "Dlg_PackPassword");
            dialog.IsPrimaryButtonEnabled = !encryptable || password.Password == repeat.Password;
        }

        void SelectFromName()
        {
            syncing = true;
            format.SelectedIndex = formats.FindIndex(f => f.Extension == PackExtensionOf(name.Text));
            syncing = false;
            Refresh();
        }

        format.SelectionChanged += (_, _) =>
        {
            if (syncing || format.SelectedIndex < 0)
            {
                return;
            }

            // keep the name, swap the extension
            var text = name.Text.TrimEnd();
            var old = PackExtensionOf(text);
            var stem = old.Length > 0 ? text[..^(text.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? 4 : old.Length)] : text;
            syncing = true;
            name.Text = stem + formats[format.SelectedIndex].Extension;
            syncing = false;
            Refresh();
        };
        name.TextChanged += (_, _) =>
        {
            if (!syncing)
            {
                SelectFromName();
            }
        };
        password.PasswordChanged += (_, _) => Refresh();
        repeat.PasswordChanged += (_, _) => Refresh();
        name.Loaded += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            var cut = name.Text.Length - PackExtensionOf(name.Text).Length;
            var slash = name.Text.LastIndexOfAny(['\\', '/']) + 1;
            name.Select(slash, Math.Max(0, cut - slash)); // the name without folder and extension, ready to retype
        };
        SelectFromName();

        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        return new PackAnswer(name.Text, Encryptable() && password.Password.Length > 0 ? password.Password : null);
    });

    /// <summary>Files → Encrypt: .pce (always), 7z or AES-256 zip (with 7-Zip); the password twice, OK only when both match and are not empty.</summary>
    public Task<EncryptAnswer?> AskEncryptAsync(int count, string firstName, bool sevenZip, string defaultFolder) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 460 };
        body.Children.Add(new TextBlock { Text = count == 1 ? Loc.Format("Enc_One", firstName) : Loc.Format("Enc_Many", count), TextWrapping = TextWrapping.Wrap });
        string[] extensions = sevenZip ? [PceFormat.Extension, ".7z", ".zip"] : [PceFormat.Extension];
        string[] labels = sevenZip ? [Loc.Get("Enc_FormatPce"), Loc.Get("Enc_Format7z"), Loc.Get("Enc_FormatZip")] : [Loc.Get("Enc_FormatPce")];
        var format = new ComboBox { Header = Loc.Get("Enc_Format"), ItemsSource = labels, SelectedIndex = 0, MinWidth = 300 };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        var hideName = new CheckBox { Content = Loc.Get("Enc_HideName") };
        var compress = new ComboBox { Header = Loc.Get("Enc_Compress"), ItemsSource = new[] { Loc.Get("Enc_CompressAuto"), Loc.Get("Enc_CompressAlways"), Loc.Get("Enc_CompressNever") }, SelectedIndex = 0, MinWidth = 300 };
        var folder = new TextBox { Header = Loc.Get("Enc_Folder"), Text = defaultFolder, PlaceholderText = defaultFolder };
        var password = new PasswordBox { Header = Loc.Get("Enc_Password") };
        var repeat = new PasswordBox { Header = Loc.Get("Dlg_PackPasswordRepeat") };
        body.Children.Add(folder);
        body.Children.Add(format);
        body.Children.Add(note);
        body.Children.Add(hideName);
        body.Children.Add(compress);
        if (!sevenZip)
        {
            body.Children.Add(new TextBlock { Text = Loc.Get("Enc_NoSevenZip"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 });
        }

        body.Children.Add(password);
        body.Children.Add(repeat);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Enc_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Enc_Button"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        void Refresh()
        {
            var extension = extensions[Math.Max(0, format.SelectedIndex)];
            note.Text = Loc.Get(extension switch { ".zip" => "Enc_NoteZip", ".7z" => "Enc_Note7z", _ => "Enc_NotePce" });
            var pce = extension == PceFormat.Extension;
            hideName.Visibility = pce ? Visibility.Visible : Visibility.Collapsed;
            compress.Visibility = pce ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = password.Password.Length > 0 && password.Password == repeat.Password && !string.IsNullOrWhiteSpace(folder.Text);
        }

        folder.TextChanged += (_, _) => Refresh();
        format.SelectionChanged += (_, _) => Refresh();
        password.PasswordChanged += (_, _) => Refresh();
        repeat.PasswordChanged += (_, _) => Refresh();
        // The dialog puts its own focus on the first control (the combo box) after Loaded; ours comes after that.
        password.Loaded += (_, _) => _ui.TryEnqueue(DispatcherQueuePriority.Low, () => password.Focus(FocusState.Programmatic));
        Refresh();
        return await ShowAsync(dialog) == ContentDialogResult.Primary
            ? new EncryptAnswer(extensions[Math.Max(0, format.SelectedIndex)], password.Password, hideName.IsChecked == true, compress.SelectedIndex switch { 1 => PceCompression.Always, 2 => PceCompression.None, _ => PceCompression.Auto }, folder.Text)
            : null;
    });

    /// <summary>Enter on a .pce file or Files → Decrypt: the password, with a note when the previous one was wrong.</summary>
    public Task<string?> AskDecryptPasswordAsync(string file, bool wrong) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = Loc.Format("Dlg_DecryptPrompt", Path.GetFileName(file)), TextWrapping = TextWrapping.Wrap });
        if (wrong)
        {
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = Loc.Get("Dlg_ArchivePasswordWrong") });
        }

        var box = new PasswordBox();
        body.Children.Add(box);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Dlg_DecryptTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Dlg_Ok"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Password : null;
    });

    /// <summary>Ctrl+Z: what will be reversed and how; "Cofnij" is the default button.</summary>
    public Task<bool> ConfirmUndoAsync(string operationTitle, string details) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 8, MaxWidth = 520 };
        body.Children.Add(new TextBlock { Text = operationTitle, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Undo_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Undo_Button"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    });

    /// <summary>After encrypting: delete the originals for good, or keep them. Says plainly what deletion cannot promise.</summary>
    public Task<bool> ConfirmDeleteOriginalsAsync(int count) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        body.Children.Add(new TextBlock { Text = Loc.Format("Enc_DeleteQuestion", count), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = Loc.Get("Enc_DeleteNote") });
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Enc_DeleteTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Enc_DeleteButton"),
            CloseButtonText = Loc.Get("Enc_KeepButton"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    });

    /// <summary>Alt+F9: the target folder and whether each archive gets its own folder.</summary>
    public Task<UnpackAnswer?> AskUnpackTargetAsync(int count, string firstName, string defaultTarget) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 460 };
        body.Children.Add(new TextBlock { Text = count == 1 ? Loc.Format("Dlg_UnpackOne", firstName) : Loc.Format("Dlg_UnpackMany", count), TextWrapping = TextWrapping.Wrap });
        var target = new TextBox { Text = defaultTarget };
        var own = new CheckBox { Content = Loc.Get("Dlg_UnpackOwnFolders"), IsChecked = true };
        body.Children.Add(target);
        body.Children.Add(own);
        target.Loaded += (_, _) =>
        {
            target.Focus(FocusState.Programmatic);
            target.SelectAll();
        };
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Dlg_UnpackTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Dlg_UnpackButton"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? new UnpackAnswer(target.Text, own.IsChecked == true) : null;
    });

    /// <summary>An encrypted archive (opening it, F3, extracting): its password, asked again when wrong.</summary>
    public Task<string?> AskArchivePasswordAsync(string archiveFile, bool wrong, CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = Loc.Format("Dlg_ArchivePasswordPrompt", Path.GetFileName(archiveFile)), TextWrapping = TextWrapping.Wrap });
        if (wrong)
        {
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = Loc.Get("Dlg_ArchivePasswordWrong") });
        }

        var box = new PasswordBox();
        body.Children.Add(box);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Dlg_ArchivePasswordTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Dlg_Ok"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Password : null;
    });

    public Task<string?> AskNameAsync(NamePrompt prompt, string initial) => prompt switch
    {
        NamePrompt.Rename => AskTextAsync(Loc.Get("Dlg_RenameTitle"), Loc.Get("Dlg_RenamePrompt"), initial, selectStem: true),
        NamePrompt.NewFile => AskTextAsync(Loc.Get("Dlg_NewFileTitle"), Loc.Get("Dlg_NewFilePrompt"), initial, selectStem: false),
        _ => AskTextAsync(Loc.Get("Dlg_NewDirTitle"), Loc.Get("Dlg_NewDirPrompt"), initial, selectStem: false),
    };

    public Task<bool> ConfirmDeleteAsync(int files, int directories, string firstName, bool permanent) => OnUiAsync(async () =>
    {
        var single = files + directories == 1;
        var text = permanent
            ? (single ? Loc.Format("Dlg_DeleteOne", firstName) : Loc.Format("Dlg_DeleteMany", files, directories))
            : (single ? Loc.Format("Dlg_RecycleOne", firstName) : Loc.Format("Dlg_RecycleMany", files, directories));
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (permanent)
        {
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = Loc.Get("Dlg_NoUndo") });
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get(permanent ? "Dlg_DeleteForeverTitle" : "Dlg_DeleteTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Dlg_DeleteButton"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    });

    /// <summary>
    /// Ctrl+S: name part or masks, extensions, a date (ready ranges or from–to; modified or created), sizes and
    /// "files only". "Clear" answers an empty filter (removes it); a size that does not parse keeps the dialog open.
    /// </summary>
    public Task<PanelFilter?> AskFilterAsync(PanelFilter? initial, bool createdKnown) => OnUiAsync(async () =>
    {
        var start = initial ?? new PanelFilter();
        var body = new StackPanel { Spacing = 10, MinWidth = 460 };
        var name = new TextBox { Header = Loc.Get("Flt_Name"), PlaceholderText = Loc.Get("Flt_NameHint"), Text = start.Name };
        var extensions = new TextBox { Header = Loc.Get("Flt_Extensions"), PlaceholderText = "pdf, docx", Text = string.Join(", ", start.Extensions) };

        FilterDatePreset[] presets = [FilterDatePreset.Any, FilterDatePreset.Today, FilterDatePreset.Last7Days, FilterDatePreset.Last30Days, FilterDatePreset.ThisYear, FilterDatePreset.Range];
        var preset = new ComboBox
        {
            Header = Loc.Get("Flt_Date"),
            ItemsSource = presets.Select(p => Loc.Get("Flt_Date" + p)).ToList(),
            SelectedIndex = Array.IndexOf(presets, start.DatePreset),
            MinWidth = 200,
        };
        var field = new ComboBox
        {
            Header = Loc.Get("Flt_DateField"),
            ItemsSource = new[] { Loc.Get("Flt_Modified"), Loc.Get("Flt_Created"), Loc.Get("Flt_Taken") },
            SelectedIndex = createdKnown ? (int)start.DateField : 0,
            IsEnabled = createdKnown,
            MinWidth = 160,
        };
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        dateRow.Children.Add(preset);
        dateRow.Children.Add(field);

        static DateTimeOffset? Day(DateOnly? d) => d is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)) : null;
        var from = new CalendarDatePicker { Header = Loc.Get("Flt_From"), Date = Day(start.From), MinWidth = 160 };
        var to = new CalendarDatePicker { Header = Loc.Get("Flt_To"), Date = Day(start.To), MinWidth = 160 };
        var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        rangeRow.Children.Add(from);
        rangeRow.Children.Add(to);
        void ShowRange() => rangeRow.Visibility = preset.SelectedIndex == presets.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        preset.SelectionChanged += (_, _) => ShowRange();
        ShowRange();

        static string Size(long? bytes) => bytes is { } b ? (b % (1 << 20) == 0 && b > 0 ? $"{b >> 20}M" : b % 1024 == 0 && b > 0 ? $"{b >> 10}k" : b.ToString(CultureInfo.InvariantCulture)) : string.Empty;
        var minSize = new TextBox { Header = Loc.Get("Flt_MinSize"), PlaceholderText = "10k", Text = Size(start.MinSize), MinWidth = 140 };
        var maxSize = new TextBox { Header = Loc.Get("Flt_MaxSize"), PlaceholderText = "2M", Text = Size(start.MaxSize), MinWidth = 140 };
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        sizeRow.Children.Add(minSize);
        sizeRow.Children.Add(maxSize);

        static string Minutes(TimeSpan? t) => t is { } v ? (v.TotalHours >= 1 ? v.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : v.Seconds == 0 ? ((int)v.TotalMinutes).ToString(CultureInfo.InvariantCulture) : v.ToString(@"m\:ss", CultureInfo.InvariantCulture)) : string.Empty;
        var minLength = new TextBox { Header = Loc.Get("Flt_MinLength"), PlaceholderText = "10", Text = Minutes(start.MinDuration), MinWidth = 140, IsEnabled = createdKnown };
        var maxLength = new TextBox { Header = Loc.Get("Flt_MaxLength"), PlaceholderText = "1:30", Text = Minutes(start.MaxDuration), MinWidth = 140, IsEnabled = createdKnown };
        var lengthRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        lengthRow.Children.Add(minLength);
        lengthRow.Children.Add(maxLength);

        var filesOnly = new CheckBox { Content = Loc.Get("Flt_FilesOnly"), IsChecked = start.FilesOnly };
        var error = new InfoBar { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error, Message = Loc.Get("Flt_BadValue") };

        body.Children.Add(name);
        body.Children.Add(extensions);
        body.Children.Add(dateRow);
        body.Children.Add(rangeRow);
        body.Children.Add(sizeRow);
        body.Children.Add(lengthRow);
        body.Children.Add(filesOnly);
        body.Children.Add(error);
        name.Loaded += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Dlg_FilterTitle"),
            Content = new ScrollViewer { Content = body },
            PrimaryButtonText = Loc.Get("Flt_Apply"),
            SecondaryButtonText = Loc.Get("Flt_Clear"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        PanelFilter? result = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var chosen = presets[Math.Max(0, preset.SelectedIndex)];
                DateOnly? Pick(CalendarDatePicker picker) => picker.Date is { } d ? DateOnly.FromDateTime(d.Date) : null;
                result = new PanelFilter
                {
                    Name = name.Text.Trim(),
                    Extensions = PanelFilter.ParseExtensions(extensions.Text),
                    DatePreset = chosen,
                    DateField = (FilterDateField)Math.Max(0, field.SelectedIndex),
                    From = chosen == FilterDatePreset.Range ? Pick(from) : null,
                    To = chosen == FilterDatePreset.Range ? Pick(to) : null,
                    MinSize = PanelFilter.ParseSize(minSize.Text),
                    MaxSize = PanelFilter.ParseSize(maxSize.Text),
                    FilesOnly = filesOnly.IsChecked == true,
                    MinDuration = PanelFilter.ParseMinutes(minLength.Text),
                    MaxDuration = PanelFilter.ParseMinutes(maxLength.Text),
                };
            }
            catch (FormatException)
            {
                error.IsOpen = true;
                args.Cancel = true;
            }
        };

        return await ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => result,
            ContentDialogResult.Secondary => new PanelFilter(),
            _ => null,
        };
    });

    public Task<string?> AskMaskAsync(bool mark, string initial) =>
        AskTextAsync(Loc.Get(mark ? "Dlg_MarkTitle" : "Dlg_UnmarkTitle"), Loc.Get("Dlg_MaskHint"), initial, selectStem: false);

    public Task ShowErrorAsync(string message) => OnUiAsync(async () =>
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Dlg_ErrorTitle"),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Loc.Get("Dlg_Ok"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog);
        return true;
    });

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var culture = CultureInfo.CurrentCulture;
        var newer = conflict.SourceModified > conflict.TargetModified;
        var body = new StackPanel { Spacing = 6, MinWidth = 440 };
        body.Children.Add(new TextBlock { Text = conflict.TargetPath, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(new TextBlock
        {
            Text = Loc.Format("Cf_Source", conflict.SourceSize.ToString("N0", culture), conflict.SourceModified.ToLocalTime().ToString("G", culture))
                + (newer ? " " + Loc.Get("Cf_Newer") : string.Empty),
        });
        body.Children.Add(new TextBlock
        {
            Text = Loc.Format("Cf_Target", conflict.TargetSize.ToString("N0", culture), conflict.TargetModified.ToLocalTime().ToString("G", culture))
                + (!newer && conflict.SourceModified != conflict.TargetModified ? " " + Loc.Get("Cf_Newer") : string.Empty),
        });

        var choice = ConflictChoice.Cancel;
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Cf_Title"),
            Content = body,
            CloseButtonText = Loc.Get("Dlg_Cancel"),
        };

        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        (string Key, ConflictChoice Choice)[] buttons =
        [
            ("Cf_Overwrite", ConflictChoice.Overwrite), ("Cf_OverwriteAll", ConflictChoice.OverwriteAll),
            ("Cf_Skip", ConflictChoice.Skip), ("Cf_SkipAll", ConflictChoice.SkipAll),
            ("Cf_KeepBoth", ConflictChoice.KeepBoth), ("Cf_KeepBothAll", ConflictChoice.KeepBothAll),
            ("Cf_OverwriteOlder", ConflictChoice.OverwriteAllOlder),
        ];
        for (var i = 0; i < buttons.Length; i++)
        {
            var (key, value) = buttons[i];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = new Button { Content = Loc.Get(key), HorizontalAlignment = HorizontalAlignment.Stretch };
            if (i == 0)
            {
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }

            button.Click += (_, _) =>
            {
                choice = value;
                dialog.Hide();
            };
            Grid.SetRow(button, i / 2);
            Grid.SetColumn(button, i % 2);
            grid.Children.Add(button);
        }

        body.Children.Add(grid);
        await ShowAsync(dialog);
        return choice;
    });

    public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var choice = ErrorChoice.Abort;
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = UserMessages.Describe(exception), TextWrapping = TextWrapping.Wrap });
        ContentDialog? shown = null;
        if (canElevate)
        {
            body.Children.Add(new TextBlock { Text = Loc.Get("Elev_Hint"), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Resource<Microsoft.UI.Xaml.Media.Brush>("TextFillColorSecondaryBrush") });
            var elevate = ElevateButton();
            elevate.Click += (_, _) =>
            {
                choice = ErrorChoice.Elevate;
                shown?.Hide();
            };
            body.Children.Add(elevate);
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Er_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Er_Retry"),
            SecondaryButtonText = Loc.Get("Er_Skip"),
            CloseButtonText = Loc.Get("Er_Abort"),
            DefaultButton = ContentDialogButton.Secondary,
        };
        shown = dialog;
        var skipAll = new HyperlinkButton { Content = Loc.Get("Er_SkipAll") };
        skipAll.Click += (_, _) =>
        {
            choice = ErrorChoice.SkipAll;
            dialog.Hide();
        };
        body.Children.Add(skipAll);

        var result = await ShowAsync(dialog);
        return choice is ErrorChoice.SkipAll or ErrorChoice.Elevate ? choice : result switch
        {
            ContentDialogResult.Primary => ErrorChoice.Retry,
            ContentDialogResult.Secondary => ErrorChoice.Skip,
            _ => ErrorChoice.Abort,
        };
    });

    public Task<AttributeChange?> AskAttributesAsync(int count, string firstName, FileAttributes attributes, DateTimeOffset modified, bool anyFolder) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 400 };
        body.Children.Add(new TextBlock { Text = count == 1 ? Loc.Format("Attr_One", firstName) : Loc.Format("Attr_Many", count), TextWrapping = TextWrapping.Wrap });

        // One item: its attributes. Several: every box starts "as it is" (the dash), so only what is clicked changes.
        (FileAttributes Flag, string Key)[] flags =
        [
            (FileAttributes.ReadOnly, "Attr_ReadOnly"),
            (FileAttributes.Hidden, "Attr_Hidden"),
            (FileAttributes.System, "Attr_System"),
            (FileAttributes.Archive, "Attr_Archive"),
        ];
        var boxes = new List<(FileAttributes Flag, CheckBox Box, bool? Initial)>();
        foreach (var (flag, key) in flags)
        {
            bool? initial = count == 1 ? attributes.HasFlag(flag) : null;
            var box = new CheckBox { Content = Loc.Get(key), IsThreeState = count > 1, IsChecked = initial };
            boxes.Add((flag, box, initial));
            body.Children.Add(box);
        }

        var local = modified.ToLocalTime();
        var changeDate = new CheckBox { Content = Loc.Get("Attr_ChangeDate"), Margin = new Thickness(0, 8, 0, 0) };
        var date = new CalendarDatePicker { Date = local, IsEnabled = false };
        var time = new TimePicker { Time = local.TimeOfDay, ClockIdentifier = "24HourClock", IsEnabled = false, MinWidth = 0 };
        var now = new Button { Content = Loc.Get("Attr_Now"), IsEnabled = false };
        changeDate.Click += (_, _) => date.IsEnabled = time.IsEnabled = now.IsEnabled = changeDate.IsChecked == true;
        now.Click += (_, _) =>
        {
            date.Date = DateTimeOffset.Now;
            time.Time = DateTime.Now.TimeOfDay;
        };
        var when = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        when.Children.Add(date);
        when.Children.Add(time);
        when.Children.Add(now);
        body.Children.Add(changeDate);
        body.Children.Add(when);

        CheckBox? recursive = null;
        if (anyFolder)
        {
            recursive = new CheckBox { Content = Loc.Get("Attr_Recursive"), Margin = new Thickness(0, 8, 0, 0) };
            body.Children.Add(recursive);
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Attr_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Perm_Apply"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        FileAttributes set = 0, clear = 0;
        foreach (var (flag, box, initial) in boxes)
        {
            if (box.IsChecked == initial)
            {
                continue; // untouched
            }

            if (box.IsChecked == true)
            {
                set |= flag;
            }
            else if (box.IsChecked == false)
            {
                clear |= flag;
            }
        }

        DateTimeOffset? newTime = null;
        if (changeDate.IsChecked == true && date.Date is { } day)
        {
            var at = day.Date + time.Time;
            newTime = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Local));
        }

        return (AttributeChange?)new AttributeChange(set, clear, newTime, recursive?.IsChecked == true);
    });

    private static readonly string[] ChecksumNames = ["MD5", "SHA-1", "SHA-256", "SHA-512"];

    public Task<ChecksumAnswer?> AskChecksumAsync(int count, string defaultName) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 440 };
        body.Children.Add(new TextBlock { Text = count == 1 ? Loc.Format("Sum_One", defaultName[..defaultName.LastIndexOf('.')]) : Loc.Format("Sum_Many", count), TextWrapping = TextWrapping.Wrap });
        ChecksumKind[] kinds = [ChecksumKind.Md5, ChecksumKind.Sha1, ChecksumKind.Sha256, ChecksumKind.Sha512];
        var algorithm = new ComboBox { Header = Loc.Get("Sum_Algorithm"), ItemsSource = ChecksumNames, SelectedIndex = 2, MinWidth = 160 };
        var name = new TextBox { Header = Loc.Get("Sum_File"), Text = defaultName };
        algorithm.SelectionChanged += (_, _) =>
        {
            // keep the name, follow the algorithm with the extension
            var kind = kinds[Math.Max(0, algorithm.SelectedIndex)];
            var text = name.Text;
            if (ChecksumFormat.KindOf(text) is not null)
            {
                text = text[..text.LastIndexOf('.')];
            }

            name.Text = text + ChecksumFormat.Extension(kind);
        };
        name.Loaded += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            var dot = name.Text.LastIndexOf('.');
            name.Select(0, dot > 0 ? dot : name.Text.Length);
        };
        body.Children.Add(name); // first, so it has the keyboard
        body.Children.Add(algorithm);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Sum_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Sum_Create"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(name.Text)
            ? new ChecksumAnswer(kinds[Math.Max(0, algorithm.SelectedIndex)], name.Text.Trim())
            : null;
    });

    public Task<PermissionsAnswer?> AskPermissionsAsync(int count, string firstName, int mode, bool anyFolder) => OnUiAsync(async () =>
    {
        var special = mode & 0xE00; // setuid/setgid/sticky stay unless the octal text says otherwise
        var body = new StackPanel { Spacing = 12, MinWidth = 380 };
        body.Children.Add(new TextBlock { Text = count == 1 ? Loc.Format("Perm_One", firstName) : Loc.Format("Perm_Many", count), TextWrapping = TextWrapping.Wrap });

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var c = 0; c < 3; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var r = 0; r < 4; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        string[] columns = ["Perm_Read", "Perm_Write", "Perm_Exec"];
        string[] rows = ["Perm_Owner", "Perm_Group", "Perm_Others"];
        for (var c = 0; c < 3; c++)
        {
            var header = new TextBlock { Text = Loc.Get(columns[c]), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(header, c + 1);
            grid.Children.Add(header);
        }

        var boxes = new CheckBox[9];
        // first in the dialog, so it has the keyboard: typing 644 is the quickest way
        var octal = new TextBox { Header = Loc.Get("Perm_Octal"), Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
        var updating = false;
        int Bits()
        {
            var bits = special;
            for (var i = 0; i < 9; i++)
            {
                if (boxes[i].IsChecked == true)
                {
                    bits |= 1 << (8 - i);
                }
            }

            return bits;
        }

        void Show(int bits)
        {
            updating = true;
            for (var i = 0; i < 9; i++)
            {
                boxes[i].IsChecked = (bits & (1 << (8 - i))) != 0;
            }

            updating = false;
        }

        for (var r = 0; r < 3; r++)
        {
            var label = new TextBlock { Text = Loc.Get(rows[r]), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r + 1);
            grid.Children.Add(label);
            for (var c = 0; c < 3; c++)
            {
                var box = new CheckBox { MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Center };
                box.Click += (_, _) =>
                {
                    if (!updating)
                    {
                        octal.Text = PlainCommander.Core.Remote.UnixMode.ToOctal(Bits());
                    }
                };
                AutomationProperties.SetName(box, Loc.Get(rows[r]) + " " + Loc.Get(columns[c]));
                Grid.SetRow(box, r + 1);
                Grid.SetColumn(box, c + 1);
                grid.Children.Add(box);
                boxes[(r * 3) + c] = box;
            }
        }

        body.Children.Add(octal);
        body.Children.Add(grid);
        CheckBox? recursive = null;
        if (anyFolder)
        {
            recursive = new CheckBox { Content = new TextBlock { Text = Loc.Get("Perm_Recursive"), TextWrapping = TextWrapping.Wrap } };
            body.Children.Add(recursive);
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Perm_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Perm_Apply"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        octal.TextChanged += (_, _) =>
        {
            var valid = PlainCommander.Core.Remote.UnixMode.TryParse(octal.Text, out var parsed);
            dialog.IsPrimaryButtonEnabled = valid;
            if (valid && !updating)
            {
                special = parsed & 0xE00;
                Show(parsed);
            }
        };
        Show(mode);
        octal.Text = PlainCommander.Core.Remote.UnixMode.ToOctal(mode);
        octal.Loaded += (_, _) =>
        {
            octal.Focus(FocusState.Programmatic);
            octal.SelectAll();
        };

        if (await ShowAsync(dialog) != ContentDialogResult.Primary || !PlainCommander.Core.Remote.UnixMode.TryParse(octal.Text, out var chosen))
        {
            return null;
        }

        return new PermissionsAnswer(chosen, recursive?.IsChecked == true);
    });

    public Task<bool> ConfirmElevateAsync(string problem) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = Loc.Get("Elev_Hint"), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Elev_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Elev_Button"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    });

    /// <summary>"As administrator" with the UAC shield, as Windows labels buttons that ask for elevation.</summary>
    private static Button ElevateButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = "\uEA18", FontSize = 14 });
        content.Children.Add(new TextBlock { Text = Loc.Get("Elev_Button") });
        return new Button { Content = content, Style = Ui.Resource<Style>("AccentButtonStyle") };
    }

    private Task<string?> AskTextAsync(string title, string prompt, string initial, bool selectStem) => OnUiAsync(async () =>
    {
        var box = new TextBox { Text = initial, MinWidth = 440 };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            var dot = initial.LastIndexOf('.');
            if (selectStem && dot > 0)
            {
                box.Select(0, dot); // rename: the name without its extension, like Explorer
            }
            else
            {
                box.SelectAll();
            }
        };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = Loc.Get("Dlg_Ok"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text : null;
    });

    public Task ShowInfoAsync(string title, string message) => OnUiAsync(async () =>
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Loc.Get("Dlg_Ok"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog);
        return true;
    });

    public Task<PlainCommander.Core.Compare.CompareOptions?> AskCompareOptionsAsync(PlainCommander.Core.Compare.CompareOptions initial) => OnUiAsync(async () =>
    {
        var subfolders = new CheckBox { Content = Loc.Get("Cmp_Subfolders"), IsChecked = initial.Subfolders };
        var content = new CheckBox { Content = Loc.Get("Cmp_ByContent"), IsChecked = initial.ByContent };
        var body = new StackPanel { Spacing = 6, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = Loc.Get("Cmp_Intro"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        body.Children.Add(subfolders);
        body.Children.Add(content);
        body.Children.Add(new TextBlock { Text = Loc.Get("Cmp_Hint"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0) });
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Cmp_Title"),
            Content = body,
            PrimaryButtonText = Loc.Get("Cmp_Run"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary
            ? new PlainCommander.Core.Compare.CompareOptions(subfolders.IsChecked == true, content.IsChecked == true)
            : null;
    });

    public Task<bool> OfferSyncAsync(int differences) => OnUiAsync(async () =>
    {
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Cmp_Title"),
            Content = new TextBlock { Text = Loc.Format("Sync_Offer", differences), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = Loc.Get("Sync_Open"),
            CloseButtonText = Loc.Get("Sync_MarksOnly"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    });

    /// <summary>F1: a two-column list of keys and what they do.</summary>
    public Task ShowKeysAsync(string title, IReadOnlyList<(string Key, string What)> rows) => OnUiAsync(async () =>
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = rows[i].Key, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5 };
            var what = new TextBlock { Text = rows[i].What, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(key, i);
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            grid.Children.Add(key);
            grid.Children.Add(what);
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = grid, MaxHeight = 520, Padding = new Thickness(0, 0, 16, 0) },
            CloseButtonText = Loc.Get("Dlg_Ok"),
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowAsync(dialog);
        return true;
    });

    public Task<bool> ConfirmUploadBackAsync(string name, string server) => OnUiAsync(async () =>
    {
        if (Owner is { } owner)
        {
            WindowFront.Bring(owner); // the user is in the editor; the question must not wait unseen behind it
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Rmt_UploadBackTitle"),
            Content = new TextBlock { Text = Loc.Format("Rmt_UploadBack", name, server), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = Loc.Get("Rmt_Upload"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    });

    /// <summary>The login: who and where, the password (or key passphrase), "remember", and why it is asked again.</summary>
    public Task<PlainCommander.Core.Remote.CredentialAnswer?> AskCredentialsAsync(
        PlainCommander.Core.Remote.RemoteSite site, bool passphrase, string? problem, CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 10, MinWidth = 420 };
        var s3 = site.Protocol == PlainCommander.Core.Remote.RemoteProtocol.S3;
        var prompt = passphrase ? "Rmt_KeyPrompt" : s3 ? "Rmt_SecretPrompt" : "Rmt_PasswordPrompt";
        body.Children.Add(new TextBlock { Text = Loc.Format(prompt, site.User, site.Host.Trim()), TextWrapping = TextWrapping.Wrap });
        if (passphrase)
        {
            body.Children.Add(new TextBlock { Text = site.KeyFile, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        if (problem is not null)
        {
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = problem });
        }

        var box = new PasswordBox { PlaceholderText = Loc.Get(passphrase ? "Rmt_KeyPlaceholder" : s3 ? "Rmt_SecretPlaceholder" : "Rmt_PasswordPlaceholder") };
        var remember = new CheckBox { Content = Loc.Get("Rmt_Remember") };
        body.Children.Add(box);
        body.Children.Add(remember);
        if (site.Protocol == PlainCommander.Core.Remote.RemoteProtocol.Ftp || (s3 && (site.PlainHttp || site.EffectivePort == 80)))
        {
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = Loc.Get(s3 ? "Rmt_PlainHttp" : "Rmt_PlainFtp") });
        }

        var dialog = new ContentDialog
        {
            Title = Loc.Get("Rmt_LoginTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Rmt_Login"),
            CloseButtonText = Loc.Get("Dlg_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic); // as in the other text dialogs: Opened is too early
        return await ShowAsync(dialog) == ContentDialogResult.Primary
            ? new PlainCommander.Core.Remote.CredentialAnswer(box.Password, remember.IsChecked == true)
            : null;
    });

    /// <summary>An unknown server key or certificate, or one that changed since it was trusted.</summary>
    public Task<PlainCommander.Core.Remote.TrustChoice> ConfirmHostAsync(
        PlainCommander.Core.Remote.HostIdentity identity, string? known, CancellationToken cancellationToken) => OnUiAsync(async () =>
    {
        var body = new StackPanel { Spacing = 10, MaxWidth = 520 };
        body.Children.Add(new TextBlock { Text = Loc.Format(known is null ? "Rmt_HostUnknown" : "Rmt_HostChanged", identity.Host, identity.Port), TextWrapping = TextWrapping.Wrap });
        if (known is not null)
        {
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Message = Loc.Get("Rmt_HostChangedWarning") });
        }

        body.Children.Add(new TextBlock { Text = identity.Kind, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = identity.Fingerprint,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        if (known is not null)
        {
            body.Children.Add(new TextBlock { Text = Loc.Format("Rmt_HostBefore", known), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        body.Children.Add(new TextBlock { Text = Loc.Get("Rmt_HostHint"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            Title = Loc.Get(known is null ? "Rmt_HostTitle" : "Rmt_HostChangedTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("Rmt_TrustAlways"),
            SecondaryButtonText = Loc.Get("Rmt_TrustOnce"),
            CloseButtonText = Loc.Get("Rmt_Reject"),

            // a changed key should not be accepted with a reflexive Enter
            DefaultButton = known is null ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };
        return await ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => PlainCommander.Core.Remote.TrustChoice.Always,
            ContentDialogResult.Secondary => PlainCommander.Core.Remote.TrustChoice.Once,
            _ => PlainCommander.Core.Remote.TrustChoice.Reject,
        };
    });

    private async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot = Owner?.Content.XamlRoot;
            var before = dialog.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
            IsOpen = true;
            var result = await dialog.ShowAsync();

            // Closing a ContentDialog leaves focus on the first control of the window, not where it was.
            if (before is { XamlRoot: not null })
            {
                before.Focus(FocusState.Programmatic);
            }

            return result;
        }
        finally
        {
            IsOpen = false;
            _gate.Release();
        }
    }

    /// <summary>Runs on the UI thread; operation prompts arrive from worker threads.</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> show)
    {
        if (_ui.HasThreadAccess)
        {
            return show();
        }

        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.TryEnqueue(async () =>
        {
            try
            {
                result.SetResult(await show());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task;
    }
}
