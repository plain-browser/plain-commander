using System.Globalization;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Formatting;
using PlainCommander.Core.Model;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Rename;

namespace PlainCommander.Presentation;

/// <summary>
/// The few strings ViewModels produce themselves (status lines, errors). XAML labels live in .resw.
/// Polish when the UI language is Polish, English otherwise.
/// </summary>
internal static class Texts
{
    private static bool Polish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Loading => Polish ? "Wczytywanie…" : "Loading…";

    public static string Summary(int files, long bytes, int dirs)
    {
        var size = EntryFormatter.HumanSize(bytes, Culture);
        return Polish
            ? string.Create(Culture, $"{files} {Plural.Polish(files, "plik", "pliki", "plików")} ({size}), {dirs} {Plural.Polish(dirs, "katalog", "katalogi", "katalogów")}")
            : string.Create(Culture, $"{files} {(files == 1 ? "file" : "files")} ({size}), {dirs} {(dirs == 1 ? "folder" : "folders")}");
    }

    public static string DriveSummary(DriveEntry drive)
    {
        var label = string.IsNullOrEmpty(drive.Label) ? string.Empty : drive.Label + "  ";
        if (!drive.IsReady)
        {
            return label + (Polish ? "niedostępny" : "not ready");
        }

        var free = EntryFormatter.HumanSize(drive.FreeBytes, Culture);
        var total = EntryFormatter.HumanSize(drive.TotalBytes, Culture);
        return label + (Polish ? $"{free} wolne z {total}" : $"{free} free of {total}");
    }

    /// <summary>Footer text next to the usage bar: free space only, the label lives in the drive menu and tooltip.</summary>
    public static string DriveFree(DriveEntry drive)
    {
        if (!drive.IsReady)
        {
            return Polish ? "niedostępny" : "not ready";
        }

        var free = EntryFormatter.HumanSize(drive.FreeBytes, Culture);
        var total = EntryFormatter.HumanSize(drive.TotalBytes, Culture);
        return Polish ? $"{free} wolne z {total}" : $"{free} free of {total}";
    }

    /// <summary>Used part of the drive, 0–1; 0 for a drive that is not ready or reports no size.</summary>
    public static double DriveUsedFraction(DriveEntry drive) =>
        !drive.IsReady || drive.TotalBytes <= 0 ? 0 : Math.Clamp(1.0 - ((double)drive.FreeBytes / drive.TotalBytes), 0, 1);

    public static string DriveMenu(DriveEntry drive)
    {
        if (drive.CloudName is not null)
        {
            return $"{drive.CloudName}   {drive.Root}";
        }

        if (Core.Network.WslDistros.DistroOf(drive.Root) is { } distro)
        {
            return $"{distro}   {drive.Root}";
        }

        var root = drive.Root.TrimEnd('\\');
        return drive.IsReady ? $"{root}   {DriveSummary(drive)}" : $"{root}   {(Polish ? "niedostępny" : "not ready")}";
    }

    /// <summary>Viewer footer: size, then encoding for text, then a note when only the first 16 MB are shown.</summary>
    public static string ViewerStatus(Core.Viewer.ViewerMode mode, long length, System.Text.Encoding? encoding, bool truncated)
    {
        var parts = new List<string> { EntryFormatter.HumanSize(length, Culture) + (length >= 1024 ? $" ({length.ToString("N0", Culture)} B)" : string.Empty) };
        if (mode == Core.Viewer.ViewerMode.Text && encoding is not null)
        {
            parts.Add(EncodingName(encoding));
        }

        if (truncated)
        {
            parts.Add(Polish ? "pokazano pierwsze 16 MB" : "first 16 MB shown");
        }

        return string.Join("  ·  ", parts);
    }

    public static string EncodingName(System.Text.Encoding encoding) => encoding.CodePage switch
    {
        65001 => encoding.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        28591 => "Latin-1",
        _ => $"{encoding.WebName.ToUpperInvariant()} ({encoding.EncodingName})",
    };

    public static string SearchProgress(int folders, int matches, string folder) => Polish
        ? string.Create(Culture, $"Przeszukano {folders} {Plural.Polish(folders, "katalog", "katalogi", "katalogów")}, znaleziono {matches} · {folder}")
        : string.Create(Culture, $"Searched {folders} folder{(folders == 1 ? "" : "s")}, found {matches} · {folder}");

    public static string SearchDone(int matches, bool cancelled) => Polish
        ? string.Create(Culture, $"{(cancelled ? "Zatrzymano" : "Gotowe")}: znaleziono {matches} {Plural.Polish(matches, "element", "elementy", "elementów")}")
        : string.Create(Culture, $"{(cancelled ? "Stopped" : "Done")}: {matches} item{(matches == 1 ? "" : "s")} found");

    public static string BadSize(string text) => Polish
        ? $"Nie rozumiem rozmiaru „{text}”. Wpisz liczbę z opcjonalnym k, M lub G, np. 10k albo 2M."
        : $"Cannot read the size “{text}”. Type a number with an optional k, M or G, e.g. 10k or 2M.";

    public static string ViewerDownloading => Polish
        ? "Pobieranie pliku z chmury… (plik jest tylko online, otworzy się po pobraniu)"
        : "Downloading the file from the cloud… (it is online-only and opens when the download ends)";

    public static string CompareTitle => Polish ? "Porównanie katalogów" : "Compare folders";

    public static string CompareNeedsDisk => Polish
        ? "Porównanie działa na katalogach na dysku i na serwerach. Wyjdź z archiwum w obu panelach."
        : "Comparing works on folders on disk and on servers. Leave the archive in both panels.";

    public static string Comparing(CompareProgress p) => Polish
        ? string.Create(Culture, $"Porównywanie… {(p.Folder.Length == 0 ? "." : p.Folder)}  ({p.Folders} kat., różnic: {p.Differences}) — Esc przerywa")
        : string.Create(Culture, $"Comparing… {(p.Folder.Length == 0 ? "." : p.Folder)}  ({p.Folders} {(p.Folders == 1 ? "folder" : "folders")}, {p.Differences} {(p.Differences == 1 ? "difference" : "differences")}) — Esc stops");

    public static string FilterSuffix(string filter) => Polish ? $" · filtr: {filter}" : $" · filter: {filter}";

    /// <summary>The filter in words for the status line: „raport”, pdf/docx, utworzone w ostatnich 7 dniach, ≥ 1 MB.</summary>
    public static string Describe(Core.Navigation.PanelFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var parts = new List<string>();
        if (filter.Name.Trim().Length > 0)
        {
            parts.Add(Polish ? $"„{filter.Name.Trim()}”" : $"“{filter.Name.Trim()}”");
        }

        if (filter.Extensions.Count > 0)
        {
            parts.Add(string.Join('/', filter.Extensions));
        }

        if (filter.DaysAt(DateTime.Now) is { } days)
        {
            var field = filter.DateField == Core.Navigation.FilterDateField.Created
                ? (Polish ? "utworzone" : "created")
                : (Polish ? "zmienione" : "modified");
            var when = filter.DatePreset switch
            {
                Core.Navigation.FilterDatePreset.Today => Polish ? "dziś" : "today",
                Core.Navigation.FilterDatePreset.Last7Days => Polish ? "w ostatnich 7 dniach" : "in the last 7 days",
                Core.Navigation.FilterDatePreset.Last30Days => Polish ? "w ostatnich 30 dniach" : "in the last 30 days",
                Core.Navigation.FilterDatePreset.ThisYear => Polish ? "w tym roku" : "this year",
                _ => (days.First, days.Last) switch
                {
                    ({ } a, { } b) => Polish ? $"od {Day(a)} do {Day(b)}" : $"from {Day(a)} to {Day(b)}",
                    ({ } a, null) => Polish ? $"od {Day(a)}" : $"since {Day(a)}",
                    (null, { } b) => Polish ? $"do {Day(b)}" : $"until {Day(b)}",
                    _ => string.Empty,
                },
            };
            parts.Add($"{field} {when}");
        }

        if (filter.MinSize is { } min)
        {
            parts.Add($"≥ {Core.Formatting.EntryFormatter.HumanSize(min, Culture)}");
        }

        if (filter.MaxSize is { } max)
        {
            parts.Add($"≤ {Core.Formatting.EntryFormatter.HumanSize(max, Culture)}");
        }

        if (filter.FilesOnly)
        {
            parts.Add(Polish ? "tylko pliki" : "files only");
        }

        return string.Join(", ", parts);
    }

    private static string Day(DateOnly day) => day.ToString(Culture.DateTimeFormat.ShortDatePattern, Culture);

    public static string DetailColumn(Core.Details.DetailColumn column) => column switch
    {
        Core.Details.DetailColumn.Dimensions => Polish ? "Wymiary" : "Dimensions",
        Core.Details.DetailColumn.Taken => Polish ? "Zdjęcie z" : "Taken",
        Core.Details.DetailColumn.Duration => Polish ? "Długość" : "Length",
        Core.Details.DetailColumn.Version => Polish ? "Wersja" : "Version",
        _ => string.Empty,
    };

    public static string Network => Polish ? "Sieć" : "Network";

    public static string NothingToUndo => Polish ? "Nie ma nic do cofnięcia." : "Nothing to undo.";

    public static string UndoOperationTitle(string step) => Polish ? $"Cofanie: {step}" : $"Undoing: {step}";

    public static string NewFileStep(string name) => Polish ? $"Nowy plik „{name}”" : $"New file “{name}”";

    public static string NewFolderStep(string name) => Polish ? $"Nowy katalog „{name}”" : $"New folder “{name}”";

    public static string RenameStep(string from, string to) => Polish ? $"Zmiana nazwy „{from}” → „{to}”" : $"Rename “{from}” → “{to}”";

    public static string MultiRenameStep(int count) => Polish
        ? string.Create(Culture, $"Zmiana nazw: {count} {Plural.Polish(count, "element", "elementy", "elementów")}")
        : string.Create(Culture, $"Rename: {count} {(count == 1 ? "item" : "items")}");

    /// <summary>What Ctrl+Z will do, one line per kind of change, for the confirmation.</summary>
    public static string UndoDetails(Core.Undo.UndoEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var lines = new List<string>();
        string Items(int n) => Polish
            ? string.Create(Culture, $"{n} {Plural.Polish(n, "element", "elementy", "elementów")}")
            : string.Create(Culture, $"{n} {(n == 1 ? "item" : "items")}");
        string Pl(int n, string one, string few, string many) => Plural.Polish(n, one, few, many);
        static string En(int n, string one, string many) => n == 1 ? one : many;
        if (entry.Count(Core.Undo.UndoKind.Created) is > 0 and var created)
        {
            lines.Add(Polish
                ? $"• {Items(created)} {Pl(created, "utworzony", "utworzone", "utworzonych")} przez tę operację {Pl(created, "trafi", "trafią", "trafi")} do kosza (pusty katalog zostanie po prostu usunięty)."
                : $"• {Items(created)} it created {En(created, "goes", "go")} to the Recycle Bin (an empty folder is simply removed).");
        }

        if (entry.Count(Core.Undo.UndoKind.Moved) is > 0 and var moved)
        {
            lines.Add(Polish
                ? $"• {Items(moved)} {Pl(moved, "wróci", "wrócą", "wróci")} na poprzednie miejsce i pod poprzednią nazwę."
                : $"• {Items(moved)} {En(moved, "goes back to where it was, under its old name", "go back to where they were, under their old names")}.");
        }

        if (entry.Count(Core.Undo.UndoKind.Recycled) is > 0 and var recycled)
        {
            lines.Add(Polish ? $"• {Items(recycled)} {Pl(recycled, "wróci", "wrócą", "wróci")} z kosza." : $"• {Items(recycled)} {En(recycled, "comes", "come")} back from the Recycle Bin.");
        }

        if (entry.Count(Core.Undo.UndoKind.AttributesChanged) is > 0 and var attributes)
        {
            lines.Add(Polish ? $"• {Items(attributes)}: atrybuty i data wrócą do poprzednich." : $"• {Items(attributes)}: attributes and date go back.");
        }

        if (entry.Count(Core.Undo.UndoKind.Overwritten) is > 0 and var overwritten)
        {
            lines.Add(Polish
                ? $"• Uwaga: {Items(overwritten)} {Pl(overwritten, "został nadpisany", "zostały nadpisane", "zostało nadpisanych")}; poprzedniej treści nie da się przywrócić."
                : $"• Note: {Items(overwritten)} {En(overwritten, "was overwritten; its", "were overwritten; their")} old content cannot be restored.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string EncryptNotHere => Polish
        ? "Szyfrować można pliki i katalogi na dysku (nie na serwerze, w archiwum ani na liście komputerów)."
        : "Files and folders on disk can be encrypted (not on a server, inside an archive or in the network list).";

    public static string EncryptIntoItself => Polish
        ? "Katalogu nie można zaszyfrować do niego samego ani do jego podkatalogu. Wskaż inny katalog docelowy."
        : "A folder cannot be encrypted into itself or one of its subfolders. Choose another target folder.";

    public static string EncryptUnavailable => Polish
        ? "Szyfrowanie do 7z i zip wymaga biblioteki 7-Zip, której ta instalacja nie ma. Format .pce działa zawsze."
        : "Encrypting to 7z and zip needs the 7-Zip library, which this installation lacks. The .pce format always works.";

    public static string DecryptNothing => Polish
        ? "Zaznacz zaszyfrowane pliki .pce do odszyfrowania na dysk (albo ustaw na nim kursor). Enter otwiera zawartość do przeglądania."
        : "Mark the encrypted .pce files to decrypt to disk (or put the cursor on one). Enter opens the content for browsing.";

    public static string DecryptNotHere => Polish
        ? "Odszyfrować można pliki .pce na dysku (nie na serwerze ani w archiwum)."
        : ".pce files on disk can be decrypted (not on a server or inside an archive).";

    public static string Decrypting(string name) => Polish ? $"Odszyfrowywanie „{name}”…" : $"Decrypting “{name}”…";

    public static string EncryptedBroken(string name, Exception ex) => Polish
        ? $"„{name}” nie da się otworzyć: {UserMessages.Describe(ex)}"
        : $"“{name}” cannot be opened: {UserMessages.Describe(ex)}";

    public static string NetworkListNotHere => Polish
        ? "Tu są komputery i ich udostępnione foldery, nie pliki. Wejdź do udostępnionego folderu (Enter), żeby kopiować, tworzyć albo usuwać."
        : "This lists computers and their shared folders, not files. Open a shared folder (Enter) to copy, create or delete.";

    public static string NotADiskImage => Polish
        ? "Ustaw kursor na obrazie dysku: .iso, .img, .vhd albo .vhdx."
        : "Put the cursor on a disk image: .iso, .img, .vhd or .vhdx.";

    public static string ImageMounted(string name, string root) => Polish ? $"„{name}” podłączony jako {root}" : $"“{name}” attached as {root}";

    public static string ImageMountedSomewhere(string name) => Polish
        ? $"„{name}” podłączony; nowego dysku nie widać jeszcze na liście (Alt+F1)."
        : $"“{name}” attached; the new drive is not in the list yet (Alt+F1).";

    public static string CannotEject(string root) => Polish
        ? $"Dysku {root} nie da się odłączyć stąd (to dysk systemowy albo nie jest dyskiem). Podłączone obrazy i pendrive'y odłącza się z ich katalogu."
        : $"Drive {root} cannot be detached from here (it is the system drive or not a drive). Attached images and USB drives detach from their folder.";

    public static string ReadingDetails(int done, int total) => total == 0
        ? (Polish ? "Odczytywanie właściwości plików…" : "Reading file properties…")
        : Polish
            ? string.Create(Culture, $"Odczytywanie właściwości plików… {done} z {total}")
            : string.Create(Culture, $"Reading file properties… {done} of {total}");

    public static string DateColumn(bool created) => created ? (Polish ? "Utworzono" : "Created") : (Polish ? "Data" : "Date");

    public static string QuickSearchSummary(string text, int matches) => string.Create(Culture, $"{text}  ·  {matches}");

    public static string FlatTitle(int count) => Polish
        ? string.Create(Culture, $"Wszystkie pliki z podkatalogów: {count}")
        : string.Create(Culture, $"All files in subfolders: {count}");

    public static string FlatNotHere => Polish
        ? "Widok płaski (Ctrl+B) działa w katalogach na dysku."
        : "The flat view (Ctrl+B) works in folders on disk.";

    public static string CopiedToClipboard(int count, bool paths) => Polish
        ? string.Create(Culture, $"Skopiowano do schowka: {count} {(paths ? Plural.Polish(count, "ścieżka", "ścieżki", "ścieżek") : Plural.Polish(count, "nazwa", "nazwy", "nazw"))}")
        : string.Create(Culture, $"Copied to the clipboard: {count} {(paths ? (count == 1 ? "path" : "paths") : (count == 1 ? "name" : "names"))}");

    public static string QuickViewFolder(int files, int folders) => Polish
        ? string.Create(Culture, $"Katalog: {files} {Plural.Polish(files, "plik", "pliki", "plików")}, {folders} {Plural.Polish(folders, "podkatalog", "podkatalogi", "podkatalogów")}")
        : string.Create(Culture, $"Folder: {files} {(files == 1 ? "file" : "files")}, {folders} {(folders == 1 ? "subfolder" : "subfolders")}");

    public static string QuickViewNotHere => Polish
        ? "Szybki podgląd pokazuje pliki na dysku. Plik z serwera lub archiwum otworzy F3."
        : "The quick view shows files on disk. F3 opens a file from a server or an archive.";

    public static string DiffBinarySame => Polish ? "Pliki binarne o identycznej zawartości." : "Binary files with identical content.";

    public static string DiffBinaryAt(long offset, long left, long right) => Polish
        ? string.Create(Culture, $"Pliki binarne. Pierwsza różnica na bajcie {offset:N0} (0x{offset:X}). Rozmiary: {left:N0} i {right:N0} B.")
        : string.Create(Culture, $"Binary files. The first difference is at byte {offset:N0} (0x{offset:X}). Sizes: {left:N0} and {right:N0} B.");

    public static string CompareFilesHow => Polish
        ? "Zaznacz dwa pliki w panelu albo ustaw kursor na pliku w każdym panelu."
        : "Mark two files in a panel, or put the cursor on a file in each panel.";

    public static string ChecksumsDiskOnly => Polish
        ? "Sumy kontrolne liczy się dla plików na dysku."
        : "Checksums are computed for files on disk.";

    public static string ChecksumNotAFile => Polish
        ? "Ustaw kursor na pliku sum kontrolnych (.md5, .sha1, .sha256, .sha512)."
        : "Put the cursor on a checksum file (.md5, .sha1, .sha256, .sha512).";

    public static string ChecksumReportTitle => Polish ? "Sprawdzenie sum kontrolnych" : "Checksum verification";

    public static string ChecksumReport(Core.Operations.ChecksumReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new System.Text.StringBuilder();
        text.Append(Polish ? $"Zgodne: {report.Matched.Count}" : $"Matching: {report.Matched.Count}");
        text.Append(Polish ? $", niezgodne: {report.Mismatched.Count}" : $", different: {report.Mismatched.Count}");
        text.Append(Polish ? $", brak pliku: {report.Missing.Count}." : $", missing: {report.Missing.Count}.");
        void List(string title, IReadOnlyList<string> names)
        {
            if (names.Count == 0)
            {
                return;
            }

            text.Append("\n\n").Append(title);
            foreach (var name in names.Take(20))
            {
                text.Append("\n  ").Append(name);
            }

            if (names.Count > 20)
            {
                text.Append(Polish ? $"\n  … i {names.Count - 20} więcej" : $"\n  … and {names.Count - 20} more");
            }
        }

        List(Polish ? "Niezgodne (plik zmieniony lub uszkodzony):" : "Different (changed or damaged):", report.Mismatched);
        List(Polish ? "Brak pliku:" : "Missing:", report.Missing);
        return text.ToString();
    }

    public static string S3NoPermissions => Polish
        ? "S3 nie ma uprawnień plików (chmod). Dostęp ustawia się w zasadach kubełka."
        : "S3 has no file permissions (chmod). Access is set in the bucket's policies.";

    public static string CompareStopped => Polish ? "Porównanie przerwane." : "Comparison stopped.";

    public static string CompareTwoServers => Polish
        ? "Różnice są zaznaczone. Dwóch serwerów nie da się zsynchronizować bezpośrednio — skopiuj przez dysk."
        : "The differences are marked. Two servers cannot be synchronized directly; copy through a disk.";

    public static string CompareSame => Polish
        ? "Katalogi nie różnią się: te same nazwy, rozmiary i czasy."
        : "The folders do not differ: same names, sizes and times.";

    public static string CompareResult(int left, int right) => Polish
        ? string.Create(Culture, $"Zaznaczono różnice: {left} w lewym panelu, {right} w prawym. Zaznaczone są elementy tylko tu, nowsze tu albo różniące się.")
        : string.Create(Culture, $"Differences marked: {left} in the left panel, {right} in the right. Marked are items only here, newer here or different.");

    public static string ArchiveReadOnly => Polish
        ? "Tej operacji nie ma wewnątrz archiwum. W archiwum można usuwać (F8), dodawać pliki z dysku (F5/F6 z drugiego panelu), zmieniać nazwy (Shift+F6), tworzyć katalogi (F7) i wypakowywać (F5)."
        : "This is not available inside an archive. In an archive you can delete (F8), add files from disk (F5/F6 from the other panel), rename (Shift+F6), create folders (F7) and extract (F5).";

    public static string ArchiveFormatReadOnly => Polish
        ? "To archiwum jest tylko do odczytu: zmieniać można archiwa zip, 7z, tar i tar.gz, a rar, iso i inne tylko przeglądać i wypakowywać."
        : "This archive is read-only: zip, 7z, tar and tar.gz can be changed; rar, iso and others can only be browsed and extracted.";

    public static string UnpackNothing => Polish
        ? "Zaznacz archiwa do rozpakowania (zip, 7z, rar, iso…). Instalator albo obraz dysku otwórz najpierw jako archiwum: Ctrl+PgDn."
        : "Mark the archives to unpack (zip, 7z, rar, iso…). Open an installer or disk image as an archive first: Ctrl+PgDn.";

    public static string UnpackInsideArchive => Polish
        ? "Wewnątrz archiwum wypakowuje się klawiszem F5 do drugiego panelu."
        : "Inside an archive, extract with F5 into the other panel.";

    public static string DocumentNotArchive(string name) => Polish
        ? $"„{name}” to dokument, nie archiwum — Ctrl+PgDn go nie otwiera."
        : $"“{name}” is a document, not an archive: Ctrl+PgDn does not open it.";

    public static string UnpackTitle(int count) => Polish
        ? string.Create(Culture, $"Rozpakowywanie: {count} {Plural.Polish(count, "archiwum", "archiwa", "archiwów")}")
        : string.Create(Culture, $"Unpacking {count} {(count == 1 ? "archive" : "archives")}");

    public static string ArchiveToArchive => Polish
        ? "Kopiowanie z archiwum do archiwum nie jest obsługiwane. Najpierw wypakuj pliki na dysk."
        : "Copying from one archive into another is not supported. Extract the files to disk first.";

    public static string NewConnection => Polish ? "Nowe połączenie" : "New connection";

    public static string ElevationRefused => Polish
        ? "Nie przyznano uprawnień administratora. Operacja została przerwana."
        : "Administrator rights were not granted. The operation was stopped.";

    public static string ElevationFailed(string reason) => Polish
        ? $"Nie udało się uruchomić procesu z uprawnieniami administratora: {reason}"
        : $"Could not start the administrator process: {reason}";

    public static string CopyOf(string name) => Polish ? $"{name} (kopia)" : $"{name} (copy)";

    public static string LoginCancelled => Polish ? "Logowanie anulowane." : "Login cancelled.";

    public static string Downloading(string name) => Polish ? $"Pobieranie „{name}”…" : $"Downloading “{name}”…";

    public static string Connecting(string host) => Polish ? $"Łączenie z {host}…" : $"Connecting to {host}…";

    public static string RemoteNotHere => Polish
        ? "Tej operacji nie ma na serwerze FTP/SFTP. Na serwerze działają: F3, F4, F5/F6 z dysku i na dysk, F7, F8 i Shift+F6."
        : "This is not available on an FTP/SFTP server. On a server you can use F3, F4, F5/F6 to and from disk, F7, F8 and Shift+F6.";

    public static string RemoteCopyOnServer => Polish
        ? "Kopiowania w obrębie serwera nie da się zrobić bez pobrania plików. Przenoszenie (F6) w obrębie tego samego serwera działa."
        : "Copying within a server would need a download and an upload. Moving (F6) within the same server works.";

    public static string RemoteOtherServer => Polish
        ? "Kopiowanie bezpośrednio między dwoma serwerami nie jest obsługiwane. Najpierw skopiuj pliki na dysk."
        : "Copying directly between two servers is not supported. Copy the files to disk first.";

    public static string RemoteAndArchive => Polish
        ? "Między archiwum a serwerem pliki muszą przejść przez dysk: najpierw je wypakuj albo pobierz."
        : "Between an archive and a server files have to go through the disk: extract or download them first.";

    public static string UploadBackTitle => Polish ? "Wysłać zmiany na serwer?" : "Upload the changes?";

    public static string UploadBack(string name, string server) => Polish
        ? $"Plik „{name}” zmienił się w edytorze. Wysłać go z powrotem na {server}?"
        : $"“{name}” changed in the editor. Upload it back to {server}?";

    public static string Uploaded(string name) => Polish ? $"Wysłano „{name}”." : $"Uploaded “{name}”.";

    public static string SyncTitle => Polish ? "Synchronizacja katalogów" : "Synchronize folders";

    public static string SyncSummary(int toRight, int toLeft, int none) => Polish
        ? string.Create(Culture, $"W prawo: {toRight}   W lewo: {toLeft}   Bez zmian: {none}")
        : string.Create(Culture, $"To the right: {toRight}   To the left: {toLeft}   Unchanged: {none}");

    public static string SyncDifference(Core.Compare.Difference d) => d switch
    {
        Core.Compare.Difference.OnlyLeft => Polish ? "tylko po lewej" : "only on the left",
        Core.Compare.Difference.OnlyRight => Polish ? "tylko po prawej" : "only on the right",
        Core.Compare.Difference.LeftNewer => Polish ? "nowszy po lewej" : "newer on the left",
        Core.Compare.Difference.RightNewer => Polish ? "nowszy po prawej" : "newer on the right",
        Core.Compare.Difference.TypeMismatch => Polish ? "plik i katalog" : "file and folder",
        _ => Polish ? "różna zawartość" : "different content",
    };

    public static string DiffSummary(int added, int removed, int changed) => added + removed + changed == 0
        ? (Polish ? "Pliki mają tę samą treść." : "The files have the same text.")
        : Polish
            ? string.Create(Culture, $"Zmienione: {changed}   Tylko po lewej: {removed}   Tylko po prawej: {added}")
            : string.Create(Culture, $"Changed: {changed}   Only left: {removed}   Only right: {added}");

    public static string DiffTooDifferent => Polish
        ? "Pliki różnią się zbyt mocno, żeby pokazać różnice linia po linii."
        : "The files differ too much to show line by line.";

    public static string CannotRun(string command, Exception ex) =>
        (Polish ? $"Nie można uruchomić: {command}" : $"Cannot run: {command}") + Environment.NewLine + ex.Message;

    public static string CannotOpen(string path, Exception ex) => Core.FileSystem.ExactPath.DisplayText(ex switch
    {
        UnauthorizedAccessException => Polish ? $"Brak dostępu: {path}" : $"Access denied: {path}",
        DirectoryNotFoundException => Polish ? $"Katalog nie istnieje: {path}" : $"Folder not found: {path}",
        Core.Archives.ArchivePasswordException or Core.Archives.NotAnArchiveException => UserMessages.Describe(ex),
        _ => ex.Message,
    });

    public static string CannotLaunch(string name, Exception ex) =>
        Polish ? $"Nie można otworzyć {name}: {ex.Message}" : $"Cannot open {name}: {ex.Message}";

    /// <summary>An exact size: "1 byte", "2,048 bytes" ("2 048 B" in Polish).</summary>
    public static string ByteCount(long bytes) => Polish
        ? bytes.ToString("N0", Culture) + " B"
        : bytes.ToString("N0", Culture) + (bytes == 1 ? " byte" : " bytes");

    public static string Selection(int markedFiles, int totalFiles, long markedBytes, long totalBytes, int markedDirs, int totalDirs)
    {
        var marked = EntryFormatter.HumanSize(markedBytes, Culture);
        var total = EntryFormatter.HumanSize(totalBytes, Culture);
        return Polish
            ? string.Create(Culture, $"Zaznaczono {markedFiles} z {totalFiles} {Plural.Polish(totalFiles, "pliku", "plików", "plików")} ({marked} z {total}), {markedDirs} z {totalDirs} {Plural.Polish(totalDirs, "katalogu", "katalogów", "katalogów")}")
            : string.Create(Culture, $"Selected {markedFiles} of {totalFiles} {(totalFiles == 1 ? "file" : "files")} ({marked} of {total}), {markedDirs} of {totalDirs} {(totalDirs == 1 ? "folder" : "folders")}");
    }

    public static string OperationTitle(OperationKind kind, int count)
    {
        var what = Polish
            ? string.Create(Culture, $"{count} {Plural.Polish(count, "element", "elementy", "elementów")}")
            : string.Create(Culture, $"{count} {(count == 1 ? "item" : "items")}");
        return kind switch
        {
            OperationKind.Copy => Polish ? $"Kopiowanie: {what}" : $"Copying {what}",
            OperationKind.Move => Polish ? $"Przenoszenie: {what}" : $"Moving {what}",
            OperationKind.Delete => Polish ? $"Usuwanie trwałe: {what}" : $"Deleting {what}",
            OperationKind.Pack => Polish ? $"Pakowanie: {what}" : $"Packing {what}",
            OperationKind.Sync => Polish ? $"Synchronizacja: {what}" : $"Synchronizing {what}",
            OperationKind.ArchiveUpdate => Polish ? $"Zmiana archiwum: {what}" : $"Updating the archive: {what}",
            OperationKind.Permissions => Polish ? $"Zmiana uprawnień: {what}" : $"Changing permissions: {what}",
            OperationKind.Attributes => Polish ? $"Zmiana atrybutów: {what}" : $"Changing attributes: {what}",
            OperationKind.Checksum => Polish ? $"Sumy kontrolne: {what}" : $"Checksums: {what}",
            OperationKind.Encrypt => Polish ? $"Szyfrowanie: {what}" : $"Encrypting {what}",
            OperationKind.Decrypt => Polish ? $"Odszyfrowywanie: {what}" : $"Decrypting {what}",
            _ => Polish ? $"Przenoszenie do kosza: {what}" : $"Moving {what} to the Recycle Bin",
        };
    }

    public static string BytesProgress(long done, long total) =>
        Polish
            ? $"{EntryFormatter.HumanSize(done, Culture)} z {EntryFormatter.HumanSize(total, Culture)}"
            : $"{EntryFormatter.HumanSize(done, Culture)} of {EntryFormatter.HumanSize(total, Culture)}";

    public static string ItemsProgress(int done, int total) =>
        Polish ? string.Create(Culture, $"{done} z {total}") : string.Create(Culture, $"{done} of {total}");

    public static string Speed(double bytesPerSecond, TimeSpan? remaining)
    {
        var speed = EntryFormatter.HumanSize((long)bytesPerSecond, Culture) + "/s";
        if (remaining is not { } r)
        {
            return speed;
        }

        var left = r.TotalHours >= 1 ? r.ToString(@"h\:mm\:ss", Culture) : r.ToString(@"m\:ss", Culture);
        return Polish ? $"{speed}, zostało {left}" : $"{speed}, {left} left";
    }

    public static string Paused => Polish ? "Wstrzymano" : "Paused";

    public static string RenameProblem(RenameProblem problem) => problem switch
    {
        Core.Rename.RenameProblem.Empty => Polish ? "pusta nazwa" : "empty name",
        Core.Rename.RenameProblem.InvalidName => Polish ? "niedozwolone znaki" : "characters not allowed",
        Core.Rename.RenameProblem.TooLong => Polish ? "nazwa dłuższa niż 255 znaków" : "name longer than 255 characters",
        Core.Rename.RenameProblem.DuplicateInBatch => Polish ? "ta sama nazwa dwa razy" : "same name twice",
        Core.Rename.RenameProblem.ExistsOnDisk => Polish ? "taki plik już istnieje" : "already exists",
        Core.Rename.RenameProblem.BadMask => Polish ? "nieznany znacznik w masce" : "unknown placeholder in the mask",
        Core.Rename.RenameProblem.BadRegex => Polish ? "błędne wyrażenie regularne" : "invalid regular expression",
        _ => string.Empty,
    };

    public static string RenameSummary(int changes, int problems)
    {
        if (problems > 0)
        {
            return Polish
                ? string.Create(Culture, $"Do zmiany: {changes}. Problemy: {problems} — popraw je, zanim zmienisz nazwy.")
                : string.Create(Culture, $"To rename: {changes}. Problems: {problems} — fix them before renaming.");
        }

        return changes == 0
            ? (Polish ? "Nic się nie zmieni." : "Nothing would change.")
            : Polish ? string.Create(Culture, $"Do zmiany: {changes}.") : string.Create(Culture, $"To rename: {changes}.");
    }

    public static string RenameDone(int done, int failed) => failed == 0
        ? (Polish ? string.Create(Culture, $"Zmieniono nazwy: {done}. „Cofnij” przywróci poprzednie.") : string.Create(Culture, $"Renamed: {done}. Undo restores the old names."))
        : (Polish ? string.Create(Culture, $"Zmieniono nazwy: {done}, nie udało się: {failed}.") : string.Create(Culture, $"Renamed: {done}, failed: {failed}."));

    public static string RenameUndone(int done, int failed) => failed == 0
        ? (Polish ? string.Create(Culture, $"Przywrócono poprzednie nazwy: {done}.") : string.Create(Culture, $"Restored old names: {done}."))
        : (Polish ? string.Create(Culture, $"Przywrócono: {done}, nie udało się: {failed}.") : string.Create(Culture, $"Restored: {done}, failed: {failed}."));

    public static string BadRegex(string detail) =>
        Polish ? $"Błędne wyrażenie regularne: {detail}" : $"Invalid regular expression: {detail}";

    /// <summary>Header of a panel that shows search results instead of a folder.</summary>
    public static string ResultsTitle(string what, int count) =>
        Polish ? string.Create(Culture, $"Wyniki wyszukiwania: {what} ({count})") : string.Create(Culture, $"Search results: {what} ({count})");

    public static string ResultsInArchive => Polish
        ? "Wśród wybranych są pliki z archiwów. Otwórz archiwum (Enter na pliku), żeby je wypakować."
        : "Some of the selected items are inside archives. Open the archive (Enter on the file) to extract them.";
}
