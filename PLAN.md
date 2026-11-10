# Plain Commander — plan budowy

Dwupanelowy menedżer plików dla Windows 11, obsługiwany głównie z klawiatury.
Priorytet: obsługa klawiaturą, szybkość, prostota. Bez chmury, bez pluginów w pierwszej wersji.

## 1. Decyzje techniczne

| Obszar | Wybór | Uzasadnienie |
|---|---|---|
| Język / runtime | C# 14, .NET 10 LTS | Natywny dla Windows, dojrzałe API plików, szybki start |
| UI | WinUI 3 (Windows App SDK 2.6) | Natywny wygląd Win11 (Mica, Fluent), wymóg "tylko Windows 11" |
| Pakowanie | MSIX (unpackaged w trakcie dev) | Instalator i auto-update przez App Installer |
| Architektura UI | MVVM (CommunityToolkit.Mvvm) | Testowalność logiki poza UI |
| Testy | xunit v3 + AwesomeAssertions (Microsoft.Testing.Platform) | Logika plikowa testowana bez UI |
| Operacje plikowe | `System.IO` + Win32 (`SHFileOperation`/`IFileOperation` przez CsWin32) | Kosz, UAC, długie ścieżki, postęp kopiowania |
| Ikony | `SHGetFileInfo` / `IShellItemImageFactory` z cache | Takie same ikony jak w Eksploratorze |
| Archiwa | SharpCompress (zip/7z/tar/rar-read) | Przeglądanie archiwum jak katalogu |
| Logowanie | Serilog do `%LOCALAPPDATA%\PlainCommander\logs` | Diagnostyka operacji plikowych |

Alternatywa awaryjna: WPF na .NET 10. Przejście jest tanie, bo cała logika siedzi w projektach bez UI.
Decyzję potwierdzamy po etapie 1, jeśli WinUI 3 ListView nie udźwignie 100 tys. wpisów z płynnym przewijaniem.

Uwaga środowiskowa: ten katalog leży w WSL2. WinUI 3 buduje się tylko na Windows (VS 2026 lub samo `dotnet build`; testy jednostkowe działają też w WSL i na Linuxie).
Repozytorium trzeba sklonować lub zamontować po stronie Windows; WSL służy tylko do edycji i gita.

## 2. Zakres MVP (wersja 1.0)

Musi być:
- Dwa panele obok siebie, każdy z własnym katalogiem, sortowaniem i zaznaczeniem.
- Pasek dysków nad każdym panelem (Alt+F1 / Alt+F2), lista dysków z wolnym miejscem.
- Widok pełny (nazwa, rozszerzenie, rozmiar, data, atrybuty) i skrócony (same nazwy w kolumnach).
- Nawigacja klawiaturą: Tab, strzałki, Enter, Backspace, Home/End, PgUp/PgDn.
- Zaznaczanie: Insert, Spacja (z obliczaniem rozmiaru katalogu), Num+, Num-, Num*, Ctrl+A.
- Operacje: F3 podgląd, F4 edycja (zewnętrzny edytor), F5 kopiuj, F6 przenieś/zmień nazwę, F7 nowy katalog, F8/Del usuń (do kosza, Shift+Del na stałe).
- Dialog postępu z anulowaniem, obsługą konfliktów (nadpisz / pomiń / zmień nazwę / wszystkie) i kolejką.
- Linia poleceń na dole: wpisany tekst idzie do `cmd`/PowerShell w bieżącym katalogu.
- Pasek funkcyjny F1–F8 na dole, klikalny.
- Szybkie szukanie w panelu po wpisaniu liter.
- Historia katalogów (Alt+↓), ulubione katalogi (Ctrl+D), zamiana paneli (Ctrl+U), ten sam katalog w drugim panelu (Ctrl+→ / Ctrl+←).
- Zapamiętywanie stanu: katalogi, szerokości kolumn, sortowanie, rozmiar okna.
- Polski i angielski interfejs (resw).

Może być (1.x):
- Przeglądanie archiwów jak katalogów, Alt+F5 pakuj, Alt+F9 rozpakuj.
- Alt+F7 wyszukiwanie plików (nazwa, zawartość, data, rozmiar).
- Porównanie katalogów (Shift+F2), synchronizacja.
- Zakładki w panelach (Ctrl+T, Ctrl+W).
- Ctrl+M zmiana nazw grupowa.
- Wewnętrzny podgląd obrazów i hex.
- Panel FTP/SFTP.

Poza zakresem:
- Wtyczki, chmura, integracja z Office, starsze Windows, tryb wysokiego kontrastu niestandardowy.

## 3. Architektura

```
PlainCommander.slnx
├── src/
│   ├── PlainCommander.Core/          # czysty .NET, bez UI i bez Win32
│   │   ├── FileSystem/               # IFileSystem, LocalFileSystem (później ArchiveFileSystem)
│   │   ├── Formatting/               # teksty kolumn, rozmiary, polskie liczebniki
│   │   ├── Model/                    # FileEntry, DriveEntry, SortSpec
│   │   ├── Navigation/               # szybkie szukanie
│   │   ├── Operations/               # kopiuj/przenieś/usuń, kolejka, konflikty (etap 2)
│   │   ├── Settings/                 # AppSettings, SettingsStore (JSON, source generator)
│   │   └── Sorting/                  # sortowanie naturalne, komparator wpisów
│   ├── PlainCommander.Presentation/  # ViewModele bez WinUI: MainViewModel, PanelViewModel, FileItem
│   ├── PlainCommander.App/           # WinUI 3
│   │   ├── Views/                    # FilePanel, FunctionBar (MainWindow w katalogu głównym)
│   │   ├── Services/                 # IconService + NativeIcons (CsWin32), ShellLauncher
│   │   ├── Helpers/                  # funkcje dla x:Bind
│   │   ├── Strings/                  # pl-PL, en-US
│   │   └── Assets/
│   └── PlainCommander.Viewer/        # F3: tekst, hex, obraz (etap 3)
├── tests/
│   ├── PlainCommander.Core.Tests/
│   └── PlainCommander.Presentation.Tests/   # testy ViewModeli na prawdziwych katalogach
├── docs/
└── build/                            # skrypty MSIX, podpisywanie (etap 4)
```
Zasady:
- `Core` nie zna WinUI. Każda operacja plikowa to klasa z `IProgress<T>` i `CancellationToken`.
- `Presentation` nie zna WinUI, więc cała logika paneli jest testowana w WSL i na CI bez okna.
- Listowanie, sortowanie i budowa wierszy idą w tle; do wątku UI trafia jedna gotowa lista (100 tys. wpisów to jedna aktualizacja).
- Jedna abstrakcja `IFileSystem` pozwala później dodać archiwa i FTP bez zmian w UI.
- Skróty okna w jednej tabeli w `MainWindow`, skróty panelu w `FilePanel`. Konfigurowalny `KeyBindings.json` w etapie 3.
- Ikony z cache per rozszerzenie; wyjątek dla .exe/.lnk/.ico, które mają ikony własne.

## 4. Etapy

### Etap 0 — szkielet (1 tydzień)
- Solution, projekty, CI (GitHub Actions na `windows-latest`: build + testy).
- Puste okno WinUI 3 z Mica, dwa panele jako placeholdery, pasek F1–F8.
- Model `FileEntry`, `LocalFileSystem.ListAsync`, testy na katalogach tymczasowych.

### Etap 1 — nawigacja (2 tygodnie) — ukończony 2026-10-29
- Wirtualizowana lista plików, kolumny, sortowanie, widok pełny/skrócony.
- Pasek dysków, Alt+F1/F2, przełączanie paneli Tab, Enter/Backspace, szybkie szukanie.
- Ikony z shella, cache.
- Zapis i odczyt stanu paneli.
- Kryterium wyjścia: 100 tys. plików w katalogu listuje się i przewija bez zacięć.

Wynik na maszynie testowej (8 vCPU, 12 GB, NVMe), katalog ze 100 001 wpisami:

| Pomiar | Wartość |
|---|---|
| Listowanie, sortowanie, budowa wierszy | 500–520 ms |
| Pierwszy render listy | 25 ms |
| Pamięć procesu w widoku skróconym | ok. 200 MB |
| Sortowanie po rozmiarze (Ctrl+F6) | natychmiastowe w odbiorze |

Płynność przewijania sprawdzona klawiaturą (PageDown, End, strzałki) przez konsolę VM, bez pomiaru klatek.
Uwagi z etapu:
- Obejście błędu WinUI [microsoft-ui-xaml#11225](https://github.com/microsoft/microsoft-ui-xaml/issues/11225): `CornerRadius="0.1"` na liście, bo powyżej około 2²¹ DIP wysokości treści wiersze rysują się pod listą.
- Widok skrócony musi zmieniać kierunek przewijania przed podmianą panelu, inaczej traci wirtualizację.
- Skróty z Ctrl/Alt obsługuje `PreviewKeyDown`, bo `KeyboardAccelerator` na siatce okna nie dostawał ich z wiersza listy.
- Przewijanie w widoku skróconym nie wyrównuje się do kolumn; poprawka razem z konfigurowalną szerokością kolumn.

### Etap 2 — zaznaczanie i operacje (3 tygodnie) — ukończony 2026-10-30, scalony do master
- Zaznaczanie (Insert, Spacja, Num+/-/*, maski).
- F5/F6/F7/F8 z dialogami wzorowanymi na WC: pole ścieżki docelowej, możliwość edycji.
- Silnik operacji: kolejka, postęp (plik + całość), anulowanie, pauza, konflikty, błędy z opcją "pomiń / ponów / przerwij".
- Kosz przez `IFileOperation`, Shift+Del na stałe.
- Długie ścieżki (`\\?\`), pliki tylko do odczytu, uprawnienia (próba elevacji przez ponowne uruchomienie z `runas` tylko dla danej operacji — do zbadania).

Stan na 2026-10-30. Sprawdzone na maszynie testowej: zaznaczanie (Insert, Spacja, Num+/−/*, Ctrl+A),
F5 z konfliktem „zachowaj oba”, F6 przeniesienie katalogu, Shift+F6, F7 zagnieżdżony, F8 do kosza
(systemowe IFileOperation), Shift+Del z ostrzeżeniem, okno postępu z pauzą i pracą w tle,
automatyczne odświeżanie po zmianie z zewnątrz. 125 testów w Core i Presentation.

| Pomiar | Wynik |
|---|---|
| Kopia 3 GB na tym samym dysku NVMe | poniżej 2,5 s |
| Kopia 50 000 plików po 2 KB, przed optymalizacją | ok. 59 s (850 plików/s) |
| To samo po przejściu na kopiowanie systemowe poniżej 4 MB | 26,8 s (1 870 plików/s) |
| To samo z 8 wątkami dla małych plików | 18,3 s (2 730 plików/s); 4 i 16 wątków dają to samo |
| robocopy, 1 wątek / 8 wątków, te same pliki | 26,9 s / 15,9 s |

Zostało:
- Kopiowanie wielowątkowe zrobione: pliki poniżej 4 MB idą do 8 wątków (`copyParallelism` w settings.json, 1 dla dysków talerzowych),
  katalogi i duże pliki w kolejności. Pytania o konflikt i błąd są szeregowane, „… wszystkie” obowiązuje od razu we wszystkich wątkach.
  Do robocopy /MT:8 brakuje około 15 %; czas idzie w samym CopyFileEx, nie w skanowaniu (50 ms) ani w sprawdzaniu istnienia celu.

Sprawdzone 2026-10-30 na Windows:
- Anulowanie w połowie pliku 12 GB (najpierw pauza przy 1,5 GB, potem Anuluj): wynik „Cancelled”, niepełna kopia usunięta,
  miejsce na dysku odzyskane. Ważne, bo zarezerwowany z góry plik docelowy ma pełny rozmiar i wyglądałby na dobrą kopię.
- Ścieżki 318–335 znaków przy wyłączonym LongPathsEnabled: listowanie, F5, Shift+F6, F8 do kosza (IFileOperation), Shift+Del.
- Cały zestaw testów na Windows: Core 98 + 1 pominięty (uprawnienia uniksowe), Presentation 34. Przy okazji znaleziony błąd
  w sprzątaniu testów: rekurencyjne przeglądanie .NET wchodzi na Windows w dowiązania i krąży w pętli (poprawione w 249b4d8).
- Maski zmiany nazwy w F6 (`*.bak`) zgłaszają czytelny błąd; obsługa później.
- Elewacja uprawnień: w 1.0 tylko czytelny komunikat „Brak dostępu”.

Razem z etapem 2 wszedł nowy wygląd aplikacji (`docs/wyglad.md`, siedem kroków, zrzuty `docs/redesign*.png`).

### Etap 3 — podgląd, linia poleceń, wygoda (2 tygodnie) — ukończony 2026-10-30, scalony do master
- F3 podgląd: tekst (autowykrywanie kodowania), hex, obraz. **Zrobione 2026-10-30** (`docs/stage3-viewer-*.png`).
  Logika w `Core/Viewer` zamiast osobnego projektu: `TextDecoder` (BOM, poprawne UTF-8, wzór zer UTF-16, inaczej
  strona ANSI systemu), `ViewerModeDetector` (obraz po rozszerzeniu, bajty zerowe = hex, reszta tekst),
  `HexFormat`/`HexLines` (wiersze 16 B formatowane na żądanie ze strumienia), `TextContent` (do 16 MB,
  większe z notką). `ViewerViewModel` w Presentation, `ViewerWindow` w App: Mica, przełączniki Tekst/Hex/Obraz
  (klawisze 1/2/3), menu kodowania (UTF-8, UTF-16, 1250, Latin-1, 852), zawijanie (W), Esc/F3 zamyka.
  Pułapka: `ListView` z 201 mln wierszy hex (plik 3 GB) po End realizował elementy bez końca (3,5 GB RAM);
  hex to teraz strona widocznych wierszy w `ItemsRepeater` + własny `ScrollBar` nad offsetem, End na 3 GB
  działa natychmiast przy 160 MB RAM. Obraz: `BitmapImage` w `ScrollViewer` z zoomem Ctrl+kółko. 22 testy.
  **Poprawka 2026-11-02, pliki OneDrive (Files On-Demand)**: plik „tylko online” pobiera się przy pierwszym odczycie treści.
  Hex czytał pierwszą stronę synchronicznie na wątku interfejsu, więc cała aplikacja (panele i podgląd dzielą wątek) stała
  do końca pobierania, Windows oznaczał ją jako niereagującą i po odmrożeniu okno podglądu lądowało za głównym.
  Teraz `CloudFile.IsCloudOnly` (atrybuty OFFLINE, RECALL_ON_OPEN, RECALL_ON_DATA_ACCESS) włącza w stopce komunikat
  „Pobieranie pliku z chmury…”, a otwarcie, próbka i pierwsza strona hex idą na wątku roboczym. Dodatkowo `WindowFront.Bring`
  (Activate + topmost→normal + SetForegroundWindow) wynosi okna podglądu, wyszukiwania i ustawień na wierzch, a podgląd
  robi to jeszcze raz po załadowaniu, jeśli stracił pierwszy plan. Nie odtworzone na prawdziwym OneDrive (VM nie ma konta),
  sprawdzone: hex 3 GB, obraz, brak regresji, okno jest oknem pierwszoplanowym.
- F4 edytor zewnętrzny konfigurowalny (domyślnie Notepad). **Zrobione 2026-10-30** (`docs/stage3-f4-edit.png`):
  `ILauncher.Edit(editor, plik)` uruchamia program z `settings.json` (`editor`, domyślnie notepad.exe) ze ścieżką
  w cudzysłowie; na katalogu F4 nic nie robi. Shift+F4 „Nowy plik” (`SimpleCommands.CreateFile`, bez podkatalogów,
  nie nadpisuje) tworzy pusty plik, stawia na nim kursor i otwiera edytor. Pozycje Podgląd / Edytuj / Nowy plik
  w menu Pliki. Pole wyboru edytora przyjdzie z oknem ustawień.
- Linia poleceń, Ctrl+Enter wstawia nazwę pliku, Ctrl+P wstawia ścieżkę. **Zrobione 2026-10-30** (`docs/stage3-command-line.png`):
  `CommandLineViewModel` w Presentation (tekst, historia 50 wpisów bez duplikatów zapisywana w `settings.json`,
  rozbiór `cd x` / `cd..` / `..` / `d:` na nawigację, reszta to polecenie); `ILauncher.RunCommand` uruchamia
  `cmd /c` w katalogu aktywnego panelu w osobnej konsoli, jak WC. Alt+F8 przenosi fokus do linii, Esc czyści
  albo wraca do panelu, Up/Down po historii. Litery w panelu zostają szybkim szukaniem, więc do linii wchodzi się
  przez Alt+F8, Ctrl+P albo kliknięcie. 14 testów w `CommandLineTests`.
- Historia, ulubione (Ctrl+D), Ctrl+U, Ctrl+←/→. **Zrobione 2026-10-30** (`docs/stage3-history.png`, `docs/stage3-favorites.png`):
  każdy panel pamięta 30 odwiedzonych katalogów (`PanelViewModel.History`, odświeżenie nie dodaje wpisu);
  Alt+↓ rozwija je pod okruszkami, najnowsze na górze. Ctrl+D rozwija ulubione z `settings.json` i pozycję
  „Dodaj … do ulubionych” / „Usuń bieżący katalog”. Ctrl+U zamienia katalogi paneli, Ctrl+→ / Ctrl+← otwiera
  w drugim panelu katalog spod kursora (albo bieżący, gdy kursor stoi na pliku). Wszystko też w menu Polecenia.
  Pułapka: `PrimaryLanguageOverride` z App SDK odrzuca pusty tag (E_INVALIDARG), więc „jak w Windows” nie ustawia nic.
- Ctrl+L wpisywanie ścieżki (zaległość z nowego wyglądu). **Zrobione 2026-10-30** (`docs/stage3-path-edit.png`): okruszki
  zamieniają się w `AutoSuggestBox` z pełną ścieżką zaznaczoną, podpowiedzi to podkatalogi wpisanego rodzica
  (`PathCompletion` w Presentation, 2 testy), Enter przechodzi, zły katalog zostawia pole z zaznaczonym tekstem,
  Esc lub utrata fokusu chowa pole; klik w puste miejsce obok okruszków też otwiera edycję. Pułapka: kontrolka
  zwinięta przez `Visibility` nie przyjmie fokusu, dopóki nie dostanie układu (`UpdateLayout()` przed `Focus()`),
  a `IsTabStop="False"` blokuje `Focus()` całkowicie.
- Menu kontekstowe Eksploratora na prawym przycisku (`IContextMenu`). **Zrobione 2026-10-30** (`docs/stage3-context-menu*.png`):
  `Services/ShellContextMenu` buduje je jak Eksplorator: PIDL-e (`SHParseDisplayName`) → `IShellItemArray` →
  `BindToHandler(BHID_SFUIObject)` → `IContextMenu`, menu Win32 (`CreatePopupMenu`, `QueryContextMenu`,
  `TrackPopupMenuEx` z `TPM_RETURNCMD`), `InvokeCommand` z wybranym id. Na czas menu okno jest podklasowane
  (`SetWindowSubclass`) i przekazuje `WM_INITMENUPOPUP` / `WM_DRAWITEM` / `WM_MEASUREITEM` / `WM_MENUCHAR` do
  `IContextMenu3.HandleMenuMsg2`, dzięki czemu „Wyślij do” i „Otwórz za pomocą” się wypełniają. Prawy przycisk
  na zaznaczonym elemencie dotyczy wszystkich zaznaczonych, na niezaznaczonym przestawia kursor i dotyczy jego,
  na pustym miejscu lub „..” dotyczy katalogu. Shift+F10 i klawisz Menu otwierają menu przy wierszu kursora.
  Uwagi: `CMIC_MASK_*` nie ma w metadanych CsWin32 (stałe lokalne); `BindToHandler` zwraca `void*`, stąd
  `Marshal.GetObjectForIUnknown`; `HandleMenuMsg` rzuca zamiast zwracać HRESULT, więc jest w try/catch.
- Ustawienia: okno z zakładkami, kolory, czcionka, zachowania. **Zrobione 2026-10-30** (`docs/stage3-settings-*.png`):
  osobne okno na Mice z `NavigationView` (Wygląd / Zachowanie), każda zmiana działa od razu jak w Ustawieniach
  Windows, zapis przy zamknięciu. Wygląd: motyw (jak w Windows / jasny / ciemny, `RequestedTheme` na korzeniu
  każdego okna, na żywo), kompaktowe wiersze, kolumna atrybutów, czcionka paneli (nazwa + rozmiar 10–18).
  Zachowanie: edytor F4 z przeglądaniem, język (jak w Windows / polski / angielski, notka o restarcie),
  równoległość kopiowania 1–16. `SettingsViewModel` w Presentation (4 testy), koło zębate w wierszu tytułowym
  i pozycja Konfiguracja → Ustawienia. Pułapki: `Windows.Globalization.ApplicationLanguages` rzuca bez tożsamości
  pakietu, działa `Microsoft.Windows.Globalization.ApplicationLanguages`; edytowalny `ComboBox` i `NumberBox`
  trzeba inicjalizować w `Loaded` i bez TwoWay x:Bind, bo zwracają puste wartości do modelu; w konstruktorze
  modelu ustawień nic nie wolno stosować, póki wszystkie pola nie są skopiowane.

### Etap 4 — wydanie 1.0 (1 tydzień) — scalony do master 2026-10-30; do domknięcia certyfikat wydawniczy i uruchomienie wersji pakowanej
- Przegląd listy kontrolnej z sekcji 6: **zrobiony 2026-10-30**, dwie poprawki (katalog bez uprawnień, nazwy z kropką na końcu),
  dwa punkty do sprawdzenia ręcznie (DPI, dwa monitory).
- MSIX, podpis, ikona, strona z instalatorem. **Ikona i MSIX zrobione 2026-10-30**: `build/make-icons.ps1` rysuje ikonę
  (dwa panele na akcentowym kwadracie, wariant bez tła dla paska zadań) we wszystkich rozmiarach do `Assets` i `.ico` dla exe;
  `Package.appxmanifest` (Identity `PlainCommander.App`, Publisher `CN=PlainCommander`, `runFullTrust`, PL/EN);
  projekt pakuje tylko z `-p:Packaged=true`, pętla deweloperska bez zmian; `build/msix.ps1` buduje `build/out/*.msix`
  (71 MB, self-contained razem z .NET; bez `SelfContained` instalator żądał .NET 10 Desktop Runtime) i podpisuje `signtool`em samopodpisanym certyfikatem deweloperskim (zadanie MSBuild nie
  przyjmuje pfx z hasłem, APPX0105). Instalacja: `build/install-dev.ps1`; zaufanie certyfikatowi w
  `LocalMachine\TrustedPeople` wymaga jednorazowo podniesionej powłoki, czego nie da się zrobić z sesji SSH VM,
  więc uruchomienie wersji pakowanej czeka na ręczne sprawdzenie. Uwaga: aplikacja pakowana zapisuje `settings.json`
  w przekierowanym `AppData\Local` pakietu, więc ustawienia z wersji niepakowanej nie przenoszą się same.
  Do zrobienia: certyfikat wydawniczy (Publisher w manifeście musi mu odpowiadać).
  **Strona z instalatorem zrobiona 2026-10-30**: `site/index.html` (statyczna, PL, motyw jasny i ciemny, gotowa pod GitHub
  Pages): nagłówek z pobieraniem, funkcje, galeria, trzy kroki instalacji z jednorazowym zaufaniem certyfikatowi, tabela
  skrótów. `build/msix.ps1` kopiuje pakiet i certyfikat do `site/download/` (ignorowane przez git). Sprawdzona w Edge na
  maszyna testowa. Po publikacji repozytorium: adres w stopce, ewentualnie `.appinstaller` do automatycznych aktualizacji.
- Pełna lokalizacja PL/EN. **Przegląd 2026-10-30**: oba pliki `Resources.resw` mają te same 142 klucze, każdy `x:Uid`
  i każdy klucz `Loc` ma wpis; teksty z kodu (`Texts`, `UserMessages`) są dwujęzyczne przez `CurrentUICulture`, ustawianą
  razem z `PrimaryLanguageOverride` przy starcie. Angielski interfejs sprawdzony na maszynie testowej (okno, menu, dialog kopiowania).
  Luka: komunikaty „already exists” z `SimpleCommands` szły do użytkownika po angielsku, teraz to
  `OperationRuleException(AlreadyExists)` tłumaczone w `UserMessages`. Język wybiera się w Ustawieniach → Zachowanie.
- Testy ręczne wg listy kontrolnej z sekcji 6. **Zrobione 2026-10-30**, wyniki w sekcji 6.

### Etap 5 — 1.x — scalony do master 2026-11-02 (archiwa, pakowanie, wyszukiwanie, porównanie katalogów, zakładki); zmiana nazw wielu plików w etapie 6
- Archiwa (SharpCompress) jako `ArchiveFileSystem`. **Przeglądanie i wypakowywanie zrobione 2026-10-30**: `Core/Archives`
  (`ArchivePath` rozpoznaje granicę archiwum w ścieżce wirtualnej `C:\x\a.zip\docs`, `ArchiveCatalog` czyta spis treści raz
  i trzyma go, póki plik się nie zmieni, `ArchiveFileSystem` opakowuje dysk i odpowiada za ścieżki w archiwum),
  `ArchiveExtractOperation` (F5 z wnętrza archiwum: pliki i katalogi, także puste; konflikty jak przy kopiowaniu;
  archiwa solid czytane jednym przebiegiem). Enter i Ctrl+PgDn wchodzą do zip/7z/rar/tar/gz…, Backspace wychodzi
  z kursorem na archiwum, Enter/F3/F4 na pliku w archiwum działają przez kopię w `%TEMP%\PlainCommander` (sprzątaną
  przy starcie). F6/F7/F8/Shift+F4/zmiana nazwy w archiwum i kopiowanie do archiwum zgłaszają „tylko do odczytu”.
  Menu kontekstowe powłoki w archiwum jest wyłączone. Sprawdzone na maszynie testowej na ZIP; 9 testów (Core i Presentation).
  **Pakowanie Alt+F5 zrobione 2026-10-30**: `ArchivePackOperation` pisze zip (Deflate, Zip64), tar i tar.gz zależnie od
  wpisanej nazwy (bez rozszerzenia dodaje `.zip`), katalogi z zachowaniem struktury i pustymi katalogami, linki pomijane;
  zapis do `.part` i przeniesienie na koniec, istniejące archiwum pyta jak konflikt (nadpisz / zachowaj oba / anuluj);
  domyślny cel to drugi panel i nazwa elementu albo katalogu. Skompresowane tarballe (`.tar.gz`, `.tgz`, `.tar.xz`…)
  czyta się i wypakowuje czytnikiem strumieniowym, bo API archiwum widzi w nich jeden wpis gzip. Pozycja Spakuj…
  w menu Pliki, etykieta Alt+F5 na pasku akcji. SharpCompress nie zapisuje 7z ani rar.
  Do zrobienia: usuwanie z archiwum, dopisywanie do istniejącego, wejście do archiwum zagnieżdżonego.
- Alt+F7 wyszukiwanie, Shift+F2 porównanie, zakładki, Ctrl+M.
- **Zakładki zrobione 2026-10-30** (`docs/stage5-tabs.png`): każdy panel ma listę kart (`PanelTab`: ścieżka, sortowanie, widok,
  nazwa pod kursorem); `PanelViewModel` pozostaje widokiem aktywnej karty, więc lista, szukanie, operacje i okruszki działają
  bez zmian, a przełączenie karty to zapis stanu bieżącej i nawigacja do zapisanej. Ctrl+T duplikuje bieżącą kartę,
  Ctrl+W zamyka (ostatniej nie da się zamknąć), Ctrl+Tab / Ctrl+Shift+Tab przełączają z zawijaniem, klik w kartę
  i × w pasku, „Nowa karta / Zamknij kartę” w menu panelu i w menu Polecenia, `OpenCurrentInNewTabAsync` w logice.
  Pasek kart pokazuje się pod linią akcentu dopiero przy dwóch kartach lub więcej, tytuł to nazwa katalogu, dymek
  pokazuje pełną ścieżkę. Zapisywane w `settings.json` (`tabs`, `activeTab` w każdym panelu; plik sprzed kart nadal się
  wczytuje). Usunięty katalog karty wraca do najbliższego istniejącego rodzica. 5 testów.
  Pułapka: `ItemsRepeater` z szablonem `x:DataType` nie ustawia `DataContext`, element dostaje model przez `Tag="{x:Bind}"`.
  Do zrobienia: skrót do otwarcia folderu w nowej karcie (Ctrl+Enter jest zajęty wstawianiem nazwy), przeciąganie kart,
  Ctrl+1…9, narzędzie zmiany nazw wielu plików (Ctrl+M w WC).
- **Shift+F2 porównanie katalogów zrobione 2026-10-30** (`docs/stage5-compare.png`): `Core/Compare/DirectoryCompare`
  porównuje katalogi obu paneli po nazwie, rozmiarze i czasie (tolerancja 2 s na FAT), opcjonalnie z podkatalogami
  i po zawartości plików o tym samym rozmiarze (równe bajty przy różnym czasie nie liczą się jako różnica).
  Wynik to słownik różnic (tylko lewy / tylko prawy / lewy nowszy / prawy nowszy / inny rozmiar przy tym samym czasie /
  plik kontra katalog); `PanelViewModel.MarkOnly` zaznacza w każdym panelu to, co jest tylko tam, nowsze tam albo inne,
  a katalog najwyższego poziomu, gdy coś pod nim się różni. Zaznaczenie przestaje być zaznaczeniem „różnice w obu
  panelach” i od razu nadaje się do F5: zmiany kopiują się jednym ruchem, konflikty pytają jak zwykle. Okno opcji
  (z podkatalogami, po zawartości) pamięta ostatni wybór w `settings.json`; brak różnic daje komunikat; w archiwum
  odmowa. Pozycja Porównaj katalogi… w menu Polecenia, etykieta Shift+F2 na pasku akcji. 13 testów.
  Do zrobienia: widok wyników z podglądem różnic w pliku tekstowym, synchronizacja jednym poleceniem.
- **Alt+F7 zrobione 2026-10-30**: `Core/Search/FileSearch` strumieniuje trafienia (maski jak przy Num+, podkatalogi,
  tekst w treści z wykrywaniem kodowania i pominięciem plików binarnych, rozmiar, zakres dat, ukryte; linki
  nieodwiedzane, katalogi bez dostępu pomijane), `SearchViewModel` w Presentation (rozmiary „10k”, „2M”, status,
  Stop bez utraty wyników), `SearchWindow` w App: Enter w polu szuka, Enter na wyniku pokazuje plik w aktywnym panelu,
  F3 podgląd, Esc zatrzymuje albo zamyka. Pozycja Szukaj plików… w menu Polecenia. Igła wśród 50 tys. plików w < 1 s.
  Zrzut `docs/stage5-search.png`. Do zrobienia: „wyniki do panelu” (WC: feed to listbox), wyszukiwanie w archiwach, wyrażenia regularne.

### Etap 6 — Ctrl+M, rozszerzenia Alt+F7, testy całości — scalony do master 2026-11-04 (wydanie 1.1.0)
- **Ctrl+M zmiana nazw wielu plików** (`Core/Rename/MultiRename`, `MultiRenameViewModel`, `MultiRenameWindow`): maski
  `[N]`, `[N2-5]`, `[N3-]`, `[E]`, `[C]`, `[P]`, `[YMD]`, `[hms]` (podwójne nawiasy to zwykłe nawiasy), szukaj/zamień
  z wyrażeniami regularnymi, wielkość liter, licznik od/krok/cyfry. Podgląd na żywo blokuje wykonanie przy pustej
  lub niedozwolonej nazwie, duplikacie w partii, kolizji z istniejącym plikiem i złej masce. Zmiana w dwóch fazach
  (nazwy tymczasowe), więc zamiana a↔b działa; „Cofnij” odwraca ostatnią partię. Zrzut `docs/stage6-multirename.png`.
- **Alt+F7**: nazwa i tekst jako wyrażenia regularne, szukanie w archiwach (nazwy, rozmiar, data i treść wpisów;
  ścieżki wirtualne), „Do panelu”: aktywny panel pokazuje wyniki jak katalog (drogi względne, tytuł z liczbą,
  × albo „..”/Backspace wraca do katalogu). F5/F3/Shift+F6/Ctrl+M działają na wynikach; zapis do wpisów archiwów
  jest odmawiany z wyjaśnieniem. Zrzuty `docs/stage6-search-archives.png`, `docs/stage6-results-panel.png`.
- **Alt+F1/Alt+F2**: litera w menu dysków przechodzi od razu na dysk.

Test całości na maszynie testowej (klawiatura i mysz przez lokalne skrypty maszyny testowej): F5 z zaznaczeniem, F6, F7, Shift+F6,
F8 do kosza, Alt+F5, wejście do zipa i wypakowanie z konfliktem, Shift+F2, karty, F3 tekst/hex, F4, linia poleceń,
Ctrl+U, Ctrl+L, Alt+↓, Ctrl+D, Shift+F10, Ctrl+F1/F2, Spacja, Num+, okno ustawień, Ctrl+M z cofnięciem, Alt+F7.
Błędy znalezione i poprawione (92ddbcb):
- Po każdym dialogu (F5, F6, F7, F8, Shift+F6, Ctrl+M) w prawym panelu aktywny stawał się lewy: zamknięcie
  ContentDialog stawia fokus na pierwszej kontrolce okna (okruszek lewego panelu). Teraz fokus w czasie dialogu
  nie zmienia aktywnego panelu, a po dialogu wraca tam, gdzie był.
- Ctrl+F1/Ctrl+F2 zostawiały klawiaturę w linii poleceń; fokus wraca na wiersz po przebudowie listy.
- Odświeżenie panelu wyciągało okno główne nad Ctrl+M, podgląd i wyszukiwanie; panel ustawia fokus tylko wtedy,
  gdy okno główne jest na wierzchu.
- F3: klawisze 1/2/3/W nie działały zaraz po otwarciu (fokus w polu tekstu tylko do odczytu).
- Czas wpisów zip był czytany jako UTC (przesunięcie o godzinę w CET).
- Status wyszukiwania bywał nadpisany spóźnionym raportem postępu.
- Litera dysku wpisana małą literą (Ctrl+L „c:\t”) zostawała mała.

Do zrobienia później (zrobione w etapie 7): zaznaczony katalog z kursorem słabo odróżnia się od niezaznaczonego;
zmiana nazw w archiwach; podgląd różnic w porównaniu katalogów.

### Etap 7 — synchronizacja, zapis do archiwów, wygląd zaznaczenia — scalony do master 2026-11-04 (55a6a27)
- **Zaznaczenie pod kursorem**: zamiast kropki chowanej pod kursorem znaczek ✓ na rogu ikony, mocniejsze tło
  zaznaczenia (0,18) i pogrubiony akcent; zaznaczony wiersz z kursorem wyraźnie różni się od niezaznaczonego.
- **Synchronizacja po Shift+F2** (`Core/Compare/SyncPlan`, `SyncOperation`, `SyncViewModel`, `SyncWindow`): po
  porównaniu z różnicami pytanie „Synchronizuj… / Tylko zaznacz”. Okno: każda różnica z kierunkiem (nowszy
  i jednostronny w stronę braku; „różna zawartość” i plik/katalog bez zmian, aż użytkownik wybierze), klik na
  strzałce albo Spacja przełącza → ← nic, ←/→ wybiera kierunek, przyciski Domyślne / Wszystko → / Wszystko ← / Nic.
  „Synchronizuj” to jedna operacja w kolejce (kopie grupami, nadpisywanie bez pytań), potem oba panele bez zaznaczeń.
- **Podgląd różnic** (`Core/Compare/TextDiff`, `DiffViewModel`, `DiffWindow`): F3/Enter/dwuklik w oknie
  synchronizacji; Myers z przycięciem wspólnego początku i końca, kolumny z numerami linii, czerwone tylko po lewej,
  zielone tylko po prawej, bursztynowe zmienione; F8/Shift+F8 albo Alt+↓/↑ skacze między blokami. Pliki binarne,
  ponad 16 MB albo zbyt różne dostają wyjaśnienie zamiast podglądu.
- **Zapis do archiwów zip, tar, tar.gz** (`ArchiveUpdateOperation`): F8 usuwa wpisy (z potwierdzeniem trwałego
  usunięcia), F5/F6 z dysku do panelu z archiwum dopisuje (F6 usuwa źródła po udanym zapisie; konflikty przez zwykły
  dialog), Shift+F6 zmienia nazwę wpisu lub katalogu z zawartością, F7 tworzy katalog. Archiwum jest przepisywane
  obok (`.part`) i podmieniane dopiero w całości, więc błąd albo Anuluj niczego nie psuje; daty wpisów zostają.
  7z, rar i inne zostają tylko do odczytu; kopiowanie archiwum→archiwum, Shift+F4, Alt+F5 i Ctrl+M wewnątrz
  archiwum są odmawiane z wyjaśnieniem.

Test na maszynie testowej: znaczek zaznaczenia, Shift+F2 → synchronizacja w obie strony z jedną pozycją pominiętą Spacją,
podgląd różnic z polskimi znakami i skokami F8, w zipie F8, Shift+F6, F7, F5 i F6 z dysku; wynikowy zip czytany
przez `System.IO.Compression` i `Expand-Archive`. Testy: Core 191, Presentation 110 (Windows i WSL).

### Etap 8 — FTP/SFTP — scalony do master 2026-11-04
Zdalny serwer działa jak katalog w panelu, tak jak archiwum: ścieżki wirtualne `sftp://użytkownik@host:22/ścieżka`
i `ftp://…`, ten sam panel, kursor, zaznaczanie, sortowanie, F-klawisze. Biblioteki open source na licencji MIT:
**SSH.NET** (SFTP, hasło i klucz prywatny, weryfikacja klucza hosta) i **FluentFTP** (FTP i FTPS jawny/niejawny,
tryb pasywny, wznawianie).

1. **Core** — `Remote/`: `RemoteSite` (protokół, host, port, użytkownik, katalog startowy, klucz, kodowanie, tryb
   pasywny), `IRemoteClient` z dwiema implementacjami (`SftpRemoteClient`, `FtpRemoteClient`), `RemotePath`
   (podział ścieżki wirtualnej), `RemoteSessionPool` (jedno połączenie na serwer do listowania + osobne dla operacji,
   ponowne łączenie po zerwaniu, zamykanie bezczynnych), `RemoteFileSystem : IFileSystem` opakowujący archiwa
   i dysk. Operacje przez istniejący silnik (postęp, pauza, Anuluj, konflikty, błędy z Ponów/Pomiń):
   pobieranie (F5 z serwera na dysk), wysyłanie (F5 z dysku na serwer), F6 w obie strony (źródło usuwane po udanym
   skopiowaniu), F8 (rekurencyjnie), Shift+F6, F7, F3/F4 przez kopię tymczasową (F4: po zapisaniu w edytorze
   pytanie o wysłanie z powrotem). Pliki zapisywane jako `.part` i przemianowywane na końcu; daty zachowane, gdy
   serwer pozwala (SFTP setstat, FTP MFMT).
2. **Bezpieczeństwo** — hasła i hasła do kluczy w Menedżerze poświadczeń Windows (DPAPI, nie w settings.json);
   nieznany klucz hosta SFTP / certyfikat FTPS: dialog z odciskiem SHA256 „Ufaj zawsze / Tylko teraz / Rozłącz”,
   zaufane odciski w ustawieniach; zmiana klucza hosta = ostrzeżenie i blokada. Zwykły FTP oznaczony w UI jako
   nieszyfrowany.
3. **Presentation/App** — Ctrl+F „Połącz z serwerem”: lista zapisanych połączeń (dodaj, edytuj, kopiuj, usuń,
   „Połącz”), szybkie połączenie przez wpisanie adresu `sftp://…` w Ctrl+L lub linii poleceń; serwer w menu dysków
   (Alt+F1/F2) jako pozycja „Połączenia”; okruszek pokazuje `host › katalog`; rozłączenie Ctrl+Shift+F albo
   przyciskiem w okruszku. Operacje niedostępne zdalnie (Alt+F5, Ctrl+M, Shift+F2, szukanie w treści) odmawiają
   z wyjaśnieniem. Teksty PL/EN.
4. **Testy** — jednostkowe na atrapie `IRemoteClient` (ścieżki, pula, operacje, konflikty, zerwanie połączenia,
   Anuluj) oraz integracyjne z prawdziwymi serwerami w kontenerach Docker (SFTP: OpenSSH, FTP: vsftpd/pure-ftpd)
   uruchamiane w WSL; ręczny test aplikacji na maszynie testowej.

Decyzje: hasła w Menedżerze poświadczeń Windows (opcja „Zapamiętaj hasło”); ręczne testy na maszynie testowej z kontenerami
SFTP i FTP uruchomionymi na serwerze testowym tylko na czas testu.

**Stan (2026-11-04): zrobione, przetestowane, scalone do master.**
- Core `Remote/`: `RemotePath`/`RemoteLocation` (adresy `sftp|ftp|ftps://użytkownik@host:port/ścieżka`), `RemoteSite`,
  `IRemoteClient` + `SftpRemoteClient` (SSH.NET 2026.0.0: hasło, keyboard-interactive, klucz z hasłem) i
  `FtpRemoteClient` (FluentFTP 55.1.0; FTPS przez **FluentFTP.GnuTLS 1.0.40**, bo vsftpd wymaga wznowienia sesji TLS
  na połączeniach danych — SslStream tego nie umie; ALPN wyłączony, bo vsftpd 3.0.5 go odrzuca; dokładne czasy przez
  MDTM, bo LIST starszych plików ma tylko dzień), `RemoteConnections` (logowanie, hasło z Menedżera poświadczeń,
  ponowne pytanie po złym haśle, klucz hosta / certyfikat: znany → bez pytania, „Tylko teraz” do końca sesji,
  zmieniony → ostrzeżenie), `RemoteFileSystem`, operacje `RemoteDownloadOperation`, `RemoteUploadOperation`,
  `RemoteDeleteOperation` (pliki przez `.part`, konflikty i błędy przez zwykłe dialogi, Ponów łączy od nowa).
- Presentation: `MainViewModel.ConnectAsync/DisconnectAsync`, F5/F6 w obie strony, F6 w obrębie serwera = rename,
  F7 (także „a/b”), Shift+F6, F8 (zawsze trwale), F3/F4 przez kopię tymczasową z pytaniem o wysłanie po zapisie,
  `cd sftp://…` i względne `cd`, odmowy z wyjaśnieniem (Alt+F5, Shift+F4, Ctrl+M, Shift+F2, serwer↔serwer,
  serwer↔archiwum); serwerów nie przywraca się przy starcie (zapisuje się ostatni katalog z dysku);
  `ConnectionsViewModel`.
- App: Ctrl+F okno połączeń (lista, formularz, szybki adres), Ctrl+Shift+F rozłącz, pozycje w menu dysków
  (otwarte serwery + „Połącz z serwerem…”), globus na przycisku dysku, dialogi logowania i klucza hosta,
  `CredentialStore` (CredRead/CredWrite), teksty PL/EN.
- Testy: Core 234 (w tym 9 na prawdziwych serwerach, gdy ustawione `PC_REMOTE_TESTS`: SFTP, FTP, FTPS — przechodzą
  w WSL i na Windows), Presentation 120. Test aplikacji na maszynie testowej z kontenerami na serwerze testowym (64 MB limitu, usunięte):
  Ctrl+F → SFTP z hasłem i kluczem hosta (odcisk zgodny z ssh-keyscan), zapamiętane hasło i zaufanie po restarcie,
  F5 na serwer, F3, F4 z wysłaniem z powrotem, F6 z serwera z konfliktem, F7, Shift+F6, F8, FTPS z certyfikatem
  (odcisk zgodny z openssl), zwykły FTP z ostrzeżeniem, odmowa serwer→serwer, Ctrl+Shift+F, anulowanie logowania.
  Poprawione po teście: klawiatura w dialogu logowania (panel brał fokus za otwartym ContentDialogiem),
  „/” w celu kopiowania na serwer, okno wyciągane na wierzch przy pytaniu o wysłanie, „Tylko teraz” na całą sesję.

Poza zakresem pierwszej wersji: FXP (serwer→serwer), SCP, WebDAV, zdalne wyszukiwanie w treści, synchronizacja
z serwerem (Shift+F2), uprawnienia chmod (może w 8.1).

### Etap 9 — górne menu — scalony do master 2026-11-04
- Pasek menu w pasku tytułu zamiast przycisku ☰: **Pliki, Zaznacz, Polecenia, Sieć, Pokaż, Konfiguracja, Pomoc**.
  Budowany w kodzie z jednej tabeli (`MainWindow.Menu.cs`); F2 pokazuje te same menu jako jedno rozwijane.
- **Sieć**: Połącz z serwerem FTP/SFTP… (Ctrl+F), Rozłącz (Ctrl+Shift+F) i lista zapisanych połączeń (kłódka,
  protokół; klik łączy w aktywnym panelu), odświeżana po zamknięciu okna połączeń. Konfiguracja: Ustawienia,
  Połączenia FTP/SFTP. Pliki: … oraz Zakończ (Alt+F4).
- **Pomoc**: Skróty klawiszowe (F1, lista z tabeli menu + klawisze panelu), O programie (wersja, .NET, biblioteki).
- Znaczniki w „Pokaż” ustawiane z kodu przy każdej zmianie panelu (wiązanie OneWay ginęło po kliknięciu w menu).
- Bez klawiszy Alt+litera: Alt sam pokazuje warianty Alt na pasku F-klawiszy.
- Test na maszynie testowej: menu myszą i klawiaturą (←/→ między menu), F2, F1, O programie, Sieć z zapisanymi połączeniami,
  znacznik „Pliki ukryte” po kliknięciu i po Ctrl+H.

### Etap 10 — przeciąganie myszą — scalony do master 2026-11-04
- Z panelu: zaznaczone (gdy chwycony wiersz jest zaznaczony) albo chwycony wiersz. Upuszczenie na drugi panel albo na
  wiersz katalogu („..” = katalog nadrzędny) otwiera zwykły dialog F5, z Shift — F6, z wpisanym celem; działa też do
  archiwum i na serwer (te same ścieżki co F5/F6: `MainViewModel.DropAsync` → `TransferItemsAsync`).
- Z Eksploratora / pulpitu do panelu: kopiowanie (Shift: przeniesienie robione przez aplikację po pytaniu); okno
  wychodzi na wierzch, panel docelowy staje się aktywny.
- Z panelu do Eksploratora: pliki z dysku jako StorageItems, zawsze kopia (aplikacja oferuje tylko Copy, więc nic nie
  znika z panelu bez jej wiedzy); z archiwum i z serwera przeciąganie działa tylko wewnątrz aplikacji.
- Pułapki: `OriginalSource` w DragOver/Drop ListView to sama lista — wiersz pod kursorem przez
  `VisualTreeHelper.FindElementsInHostCoordinates`; ostatni DragOver przychodzi już bez przycisku i bez modyfikatorów,
  więc o przeniesieniu decyduje ostatni DragOver z wciśniętym przyciskiem.
- Testy: Presentation +5 (`DragDropTests`); na maszynie testowej wszystkie cztery kierunki przeciągnięte myszą
  (`mouse.sh drag/shiftdrag`, wolniejszy ruch i pauza przed puszczeniem dla pętli OLE).

### Etap 11 — uprawnienia administratora — scalony do master 2026-11-04
- Okno błędu przy „Brak dostępu” ma przycisk **Jako administrator** (z tarczą UAC). Wybór przerywa operację
  (`ErrorChoice.Elevate` → `OperationOutcome.NeedsElevation`), a resztę dokańcza proces pomocniczy z uprawnieniami
  administratora; karta postępu pokazuje „jako administrator”, pytania o konflikty i błędy zadaje dalej aplikacja.
- Dokończenie (`ElevatedJob.Resume`): przeniesione/usunięte już źródła są pomijane, pliki już skopiowane (ten sam
  rozmiar i data) nie wywołują pytania o nadpisanie; przy przenoszeniu usuwane jest tylko źródło.
- Obsługiwane: F5, F6, Shift+Del (oraz przeciąganie, które idzie przez F5/F6), F7, Shift+F4, Shift+F6. Kosz (F8)
  zostaje przy IFileOperation, które o uprawnienia pyta samo; archiwa i serwery nie dotyczą.
- Proces pomocniczy to ten sam exe z `--elevated <potok> <pid>` (własny `Program.Main`, `DISABLE_XAML_GENERATED_MAIN`),
  uruchamiany przez `runas`; w MSIX z uprawnieniem `allowElevation`. Jeden na sesję: kolejne operacje nie pytają
  ponownie UAC; kończy się z zamknięciem aplikacji albo po 10 min bezczynności.
- Bezpieczeństwo potoku: nazwa losowa, jedna instancja tworzona przed startem pomocnika, ACL tylko dla użytkownika
  i Administratorów; aplikacja przyjmuje tylko proces, który sama uruchomiła (`GetNamedPipeClientProcessId`), pomocnik
  tylko serwer o podanym PID działający z tego samego exe. Pomocnik wykonuje wyłącznie opisane zadania (`ElevatedJob`).
- Pułapka: `Directory.Delete` przy braku dostępu rzuca gołe `IOException` (bez kodu) — `PathRules.RemoveDirectory`
  zamienia je na `UnauthorizedAccessException`.
- Testy: Core +12 (`ElevationTests` przez prawdziwy potok, `ElevateChoiceTests`), Presentation +5; na maszynie testowej:
  F5/F7/Shift+Del w `C:\Program Files`, odmowa w UAC, karta postępu, ponowne użycie pomocnika, wersja z MSIX.

### Etap 12 — wznawianie przesyłania FTP/SFTP — scalony do master 2026-11-04
- Plik jedzie jako „nazwa.part” (na dysku przy pobieraniu, na serwerze przy wysyłaniu). Po zerwaniu połączenia,
  anulowaniu albo awarii część zostaje; kolejna próba — automatyczna, „Ponów” w oknie błędu albo to samo F5 później —
  dosyła tylko brakujące bajty (SFTP: przesunięcie w strumieniu / dopisywanie; FTP: REST przy RETR, APPE).
- Część jest używana tylko, gdy jest krótsza od źródła i zapisana po jego ostatniej zmianie; inaczej plik idzie od
  zera. Pusta część jest usuwana.
- Zerwanie w trakcie pliku (połączenie padło, a próba coś przesłała) jest ponawiane od razu do 3 razy (przerwy 2, 4,
  6 s), dopiero potem pytanie Ponów/Pomiń. Błąd przed pierwszym bajtem (np. brak uprawnień) pyta od razu.
- FTP: limity czasu odpowiedzi i danych 20 s (było 60 s), żeby martwe połączenie bez resetu wykryć szybko.
- Pasek postępu przy wznowieniu zaczyna od tego, co już jest (`ProgressTracker.RestartItem(kept)`).
- Testy: Core +10 (`RemoteResumeTests`, zrywanie w testowym serwerze), test serwerowy wznawiania dla SFTP/FTP/FTPS
  (WSL i Windows); próba z restartem kontenera w połowie pliku 400 MB: SFTP i FTP w obie strony wznawiają od miejsca
  przerwania, suma MD5 zgodna. Kontenery FTP potrzebują ≥ 256 MB (przy 64 MB jądro zabija vsftpd przy dużych plikach).

### Etap 13 — uprawnienia na serwerze, porównanie i synchronizacja z serwerem — scalony do master 2026-11-04
- Pliki → **Uprawnienia (chmod)…** (też w menu Sieć): okno z polem ósemkowym (fokus, „644” i Enter) i siatką rwx
  właściciel/grupa/pozostali, oba widoki zsynchronizowane; start od trybu pierwszego elementu. Dla katalogów opcja
  „również zawartość”: pliki dostają ten sam tryb, podkatalogi z „x” tam, gdzie jest „r” (jak chmod X); wybrane
  elementy dokładnie to, o co proszono. Zawartość przed swoim katalogiem (600 na katalogu nie blokuje reszty).
  SFTP: setstat (SSH.NET ChangePermissions), FTP: SITE CHMOD. Na dysku polecenie wyjaśnia, że działa na serwerze.
- Kolumna atrybutów na serwerze pokazuje `rwxr-xr-x` (z listingu SFTP / LIST FTP); szerokość kolumny 74.
- Shift+F2 działa, gdy jeden lub oba panele to serwer (`ICompareSide`: `DiskCompareSide`, `RemoteCompareSide`).
  FTP: dla plików obecnych po obu stronach dokładny czas przez MDTM (listing ma minuty albo sam dzień). Brak katalogu
  na serwerze = pusty; inny błąd przerywa porównanie (pusty listing zaproponowałby skopiowanie wszystkiego).
- Synchronizacja dysk ↔ serwer: `SyncOperation` używa `RemoteUploadOperation` / `RemoteDownloadOperation` z
  OverwriteAll (wznawianie z etapu 12 działa i tu). Dwa serwery: porównanie zaznacza różnice, synchronizacji nie ma.
  Okno synchronizacji pokazuje rozmiar i datę z porównania; F3 (różnice tekstu) pobiera plik z serwera do temp.
- Testy: Core +17 (`UnixModeTests`, `ServerCompareTests`), test serwerowy (SFTP/FTP/FTPS): synchronizacja w obie
  strony i ponowne porównanie bez różnic, chmod rekurencyjny; Presentation +3. Na maszynie testowej z kontenerem SFTP:
  kolumna uprawnień, okno chmod (mysz i klawiatura), Shift+F2, okno synchronizacji, F3 z plikiem z serwera,
  synchronizacja i ponowne Shift+F2 („nie różnią się”).

### Etap 14 — drobiazgi: postęp porównania, atrybuty w wąskim panelu, porządki — scalony do master 2026-11-04
- Shift+F2 pokazuje postęp w paskach statusu obu paneli („Porównywanie… katalog (N kat., różnic: M) — Esc przerywa”,
  najwyżej co 100 ms); Esc przerywa („Porównanie przerwane.”). `DirectoryCompare.RunAsync(…, IProgress<CompareProgress>, token)`.
- Kolumna atrybutów w panelu węższym niż 520 px: zamiast znikać, rozszerzenie zostaje w nazwie, data traci godzinę
  (`FullNarrowAttrTemplate`); dopiero poniżej 360 px atrybuty znikają.
- Usunięte wzmianki o Windows Commanderze z planu, `docs/wyglad.md` i komentarzy w kodzie.
- Testy: Core +1 (postęp i przerwanie porównania); na maszynie testowej: wąski panel z atrybutami, postęp i Esc przy porównaniu
  2×3000 katalogów po zawartości.

### Etap 15 — narzędzia panelu i plików, S3 — scalony do master 2026-11-04
- **Ctrl+Q szybki podgląd**: panel nieaktywny ustępuje miejsca podglądowi pliku pod kursorem (tekst, hex, obraz jak F3),
  z opóźnieniem 150 ms po ruchu kursora; katalog pokazuje liczbę plików i podkatalogów; plik z serwera/archiwum: F3.
  Tab nie przełącza paneli, gdy podgląd jest otwarty. `QuickViewModel`, `Views/QuickView`.
- **Ctrl+S filtr**: maska (`*.pdf;*.docx`) albo fragment nazwy; katalogi zostają; filtr trzyma się katalogu (odświeżenie
  go zachowuje, przejście gdzie indziej zdejmuje); pasek stanu „Filtr „…”: …”; puste pole zdejmuje.
- **Ctrl+B widok płaski**: wszystkie pliki z podkatalogów (ścieżki względne), dowiązania nie są przechodzone; tylko dysk;
  Ctrl+B wraca. Filtr działa także na nim.
- **Kopiowanie do schowka**: Zaznacz → nazwy / pełne ścieżki (Ctrl+Shift+C), po jednej w wierszu.
- **Atrybuty i data na dysku** (Pliki → Atrybuty i uprawnienia…; na serwerze to samo polecenie to chmod): R/H/S/A
  (przy wielu elementach pola trójstanowe „bez zmian”), data i godzina modyfikacji („Teraz”), opcjonalnie z zawartością.
  Zmieniane jest tylko to, czego użytkownik dotknął. `AttributesOperation`.
- **Sumy kontrolne**: Pliki → Utwórz (MD5/SHA-1/SHA-256/SHA-512, jeden plik „hash *ścieżka/względna”) i Sprawdź
  (plik pod kursorem; GNU i BSD; podsumowanie zgodne/niezgodne/brak). `ChecksumCreateOperation`, `ChecksumVerifyOperation`.
- **Porównanie plików** (Polecenia → Porównaj pliki): dwa zaznaczone pliki albo plik pod kursorem w każdym panelu; tekst
  w oknie różnic, binarne: pierwsza różnica (bajt, hex) albo „identyczne”. Pliki z serwera przez kopię tymczasową.
- **S3** (AWSSDK.S3 4.0.104.4): `s3://klucz@host:port/kubełek/…`; kubełki to katalogi najwyższego poziomu, „/” w kluczach
  to katalogi (pusty katalog = obiekt „nazwa/”). Klucz tajny w Menedżerze poświadczeń; region z nazwy hosta AWS lub
  ustawienia; HTTP dla lokalnego serwera (opcja połączenia albo port 80). Pobieranie wznawia się (zakres bajtów);
  wysyłanie bez „.part” (obiekt pojawia się w całości, duże pliki wieloczęściowo; `IRemoteClient.AtomicUploads`).
  Czas pliku w metadanych `mtime` (jak rclone), więc porównanie i synchronizacja nie widzą fałszywych różnic; lista
  pokazuje czas zapisu w S3. Zmiana nazwy = kopia + usunięcie. Bez chmod (komunikat).
- Testy: Core +11 (`FileToolsTests`), testy serwerowe także na S3 (RustFS; obrazy MinIO zniknęły z Docker Hub i quay.io),
  Presentation +8 (`PanelToolsTests`). Na maszynie testowej: szybki podgląd (hex, tekst, obraz), filtr, porównanie binarne, sumy
  (tworzenie i sprawdzenie z niezgodnym plikiem), atrybuty i data, widok płaski, schowek, S3: logowanie, F8, F5 drzewa,
  Shift+F2 i synchronizacja z kubełkiem, ponowne porównanie bez różnic.

### Etap 16 — drobiazgi po testach 1.7.0 — scalony do master 2026-11-04
- S3: lista pokazuje czas pliku z metadanych `mtime` (HEAD na plik, 8 naraz) w katalogach do 500 plików; większe
  pokazują czas zapisu w S3, żeby lista była szybka (`S3RemoteClient.ExactTimesLimit`).
- Szybki podgląd: węższy niż ~620 px pokazuje wiersze hex po 8 bajtów (`HexLines`/`HexFormat` z szerokością wiersza,
  `ViewerViewModel.HexBytesPerLine` zachowuje miejsce w pliku). Poprawiony wyścig: wysokość podana przez widok w trakcie
  otwierania pliku gubiła się i strona miała 1 wiersz (dotyczyło też F3).
- Esc w panelu zdejmuje filtr Ctrl+S.
- Okno sum kontrolnych: pole nazwy pierwsze (ma klawiaturę), Enter tworzy.
- FTPS: jedno automatyczne ponowienie przy „522 session reuse required” (bilet sesji TLS 1.3 bywa spóźniony; nic nie
  zostało jeszcze przesłane). Wyszło jako rzadki błąd testu serwerowego.

### Etap 17 — archiwa przez 7-Zip: 7z, rar, iso i reszta — scalony do master 2026-11-05 (wydanie 1.8.0)
- **Silnik 7-Zip** (`Core/Archives/SevenZip`): oficjalny `7z.dll` 26.05 (x64 i arm64 w `third_party/7-Zip`, skąd i jakie
  sumy SHA-256 — `README.md` tam; licencja LGPL + unRAR obok programu jako `7-Zip-License.txt`). Interfejsy COM przez
  `[GeneratedComInterface]` (`SevenZipInterop`), formaty z `GetHandlerProperty2` (`SevenZipLibrary`), otwieranie po
  rozszerzeniu, potem po sygnaturze, dla `.exe` szukanie archiwum SFX (`SevenZipArchive`). Wieloczęściowe rar
  (`part1.rar`, `r00`) przez wywołanie zwrotne woluminów. Bez biblioteki (Linux, testy w WSL) zostaje SharpCompress.
- **Czytanie**: `ArchiveReaders.Open` — jeden czytnik wpisów dla F5, F3/Enter, Alt+F7 w treści; 7-Zip wypycha dane,
  więc kanał `EntryPipe` (bufor 4 MB) zamienia je na strumień do czytania, porzucony wpis jest odrzucany bez blokowania.
  Spakowane tarballe (`.tar.gz/.bz2/.xz/.zst/.lz`, `.tzst`, `.tlz`) czyta dalej SharpCompress (7-Zip pokazałby jeden `.tar`).
  Enter wchodzi także do iso, udf, cab, wim, esd, arj, lzh, cpio, rpm, deb, xar, dmg, lzma, z, squashfs, cramfs.
  Czasy DOS (zip, rar4) 7-Zip liczy dzisiejszym przesunięciem strefy — poprawiane po znaczniku precyzji w PROPVARIANT
  (letnia data czytana zimą była przesunięta o godzinę). PROPVARIANT ma 24 bajty (16 rozbijało stos).
- **Ctrl+PgDn** na dowolnym pliku próbuje otworzyć go jak archiwum (instalator exe/msi, obraz dysku; po 1.9.0 bez
  PDF i dokumentów Office — decyzja użytkownika, `ArchivePath.IsDocument`);
  `ArchivePath.OpenExplicitly` czyni jego ścieżkę granicą archiwum, Enter nadal uruchamia plik.
- **Hasła**: `ArchivePasswords` pamięta hasła na czas sesji (nigdzie nie zapisuje), pytanie z okna (`IArchivePasswordPrompt`
  w `DialogService`), przy złym haśle pyta ponownie z komunikatem. Zaszyfrowane nagłówki 7z/rar5 pytają już przy wejściu.
- **Zapis 7z**: Alt+F5 (okno z listą formatów zip/7z/tar/tar.gz, która podąża za rozszerzeniem, i hasłem wpisywanym dwa
  razy — szyfruje też nazwy, `he=on`), F5/F6 do panelu z 7z, F8, Shift+F6, F7 w środku (`SevenZipWriter.Update`:
  zachowane wpisy kopiowane bez ponownej kompresji, chyba że zmienia się ich blok solid; zmienione zaszyfrowane
  archiwum zostaje zaszyfrowane tym samym hasłem). Zapis do `.part` i podmiana jak w zip.
- **Alt+F9 Rozpakuj** (Pliki → Rozpakuj…): zaznaczone archiwa do drugiego panelu, każde do katalogu nazwanego jak
  archiwum (`a.tar.gz` → `a`) albo wprost do celu; zwykłe pliki są pomijane. `ArchiveExtractOperation(…, ownFolders)`.
- Testy: Core +17 (`SevenZipTests`, 7z w teorii `ArchiveUpdateTests`; część tylko na Windows), Presentation +3.
  Na maszynie testowej: iso (IMAPI), F3 z iso, 7z z hasłem (złe → ponowne pytanie → dobre), F5 z zaszyfrowanego, msi i instalator
  SFX przez Ctrl+PgDn, rar5 z cyrylicą, Alt+F9 na iso+zip+7z+rar, Alt+F5 do 7z z hasłem (sprawdzone 7z.exe: LZMA2+AES,
  ukryte nazwy), F7 i F8 w zaszyfrowanym 7z. Wypakowanie 50 tys. plików z 7z: ~25 s, tyle co `7z.exe x`.
  Zrzuty `docs/stage17-*.png`.
- Zostało: 7z.001 (archiwa dzielone), archiwum w archiwum, zapis zip z hasłem, wybór stopnia kompresji.

### Etap 18 — szybkie wyszukiwanie i filtr — scalony do master 2026-11-05 (wydanie 1.9.0)
- **Litery w panelu** szukają fragmentu w dowolnym miejscu nazwy, bez wielkości liter i polskich znaków („zolc”
  znajduje „Żółć”, „lodz” — „Łódź”; `Core/Navigation/TextFold`, ł/đ/ø/ß mapowane ręcznie). Kursor skacze najpierw do
  nazwy zaczynającej się od tekstu, ↑/↓ chodzą po wszystkich trafieniach; `*` i `?` dalej działają jak maska od
  początku nazwy. Dymek z tekstem i liczbą trafień („view · 3”) stoi w stopce zamiast tekstu stanu, więc nie zasłania
  wiersza z kursorem. **Ctrl+S w trakcie** zamienia wpisany tekst w filtr (zostają tylko trafienia, kursor na miejscu).
- **Ctrl+S filtr** (`PanelFilter`): nazwa zawiera (kilka słów — wszystkie, w dowolnej kolejności) albo maski; rozszerzenia
  („pdf, docx”); data modyfikacji albo utworzenia: dowolna / dziś / ostatnie 7 dni / ostatnie 30 dni / ten rok / zakres
  od–do (dni całe, gotowe zakresy liczone od zegara przy każdym odświeżeniu); rozmiar od–do („10k”, „1,5M”); tylko pliki.
  Katalogi zostają (chyba że „tylko pliki”), testy daty, rozmiaru i rozszerzenia dotyczą plików. Okno pokazuje bieżący
  filtr do poprawy, „Wyłącz filtr” go zdejmuje, zły rozmiar zostawia okno otwarte z komunikatem. Data utworzenia jest
  tylko na dysku (na serwerze i w archiwum pole „według daty” jest wyłączone; pliki bez daty utworzenia nie przechodzą).
  Z **Ctrl+B** daje „wszystkie PDF-y z podkatalogów utworzone w tym tygodniu”. Stan: najpierw liczniki, potem „· filtr: …”
  (pełny tekst w dymku).
- **Data utworzenia**: `FileEntry.Created` (dysk), `SortColumn.Created`, Pokaż → Sortuj wg daty utworzenia; kolumna daty
  pokazuje wtedy datę utworzenia z nagłówkiem „Utworzono”, klik w nagłówek odwraca ten sam porządek.
- Testy: Core +10 (`PanelFilterTests`, `QuickSearchTests`), Presentation +4 (fragment i Ctrl+S, sortowanie po dacie
  utworzenia — tylko na Windows, filtr złożony, Ctrl+B z filtrem). Na maszynie testowej: szybkie wyszukiwanie z licznikiem w stopce,
  Ctrl+S z wpisanym tekstem, okno filtra z klawiatury (nazwa + png + utworzone w 30 dniach + ≥ 80k), sortowanie po dacie
  utworzenia. Zrzuty `docs/stage18-*.png`.

### Po 1.9.0 — bez otwierania PDF i dokumentów Office (2026-11-05)
- Decyzja użytkownika: program nie otwiera PDF ani plików Office w środku. Ctrl+PgDn na pdf, doc(x), xls(x), ppt(x),
  odt/ods/odp, rtf, vsd(x), pub, one pokazuje w stanie „to dokument, nie archiwum” i nic nie robi (`ArchivePath.IsDocument`,
  `OpenExplicitly` zwraca false). Enter dalej uruchamia je w programie systemowym.
- Podgląd PDF/Office w F3 i Ctrl+Q (pierwsza pozycja z przeglądu wtyczek) skreślony z planów.

### Etap 19 — kolumny: rozmiar katalogów i właściwości plików — scalony do master 2026-11-05 (wydanie 1.10.0)
Wybrane przez użytkownika 2026-11-05 z przeglądu wtyczek (pierwowzory: DirSizeCalc, ShellDetails, ImgSize, EXIF,
AudioInfo, MediaInfo). Nie wybrane: Android/WebDAV, podgląd multimediów i kodu, automatyzacja; wyszukiwanie w treści
PDF/Office odrzucone.
- **Rozmiary katalogów w tle** (Pokaż → Rozmiary katalogów, zapamiętywane, oba panele): po wejściu do katalogu
  podkatalogi liczą się po kolei (`DirectorySize`), nowa lista przerywa liczenie, policzone przechodzą przez odświeżenie.
  Nie na serwerze, w archiwum, w wynikach ani na dysku sieciowym. Sortowanie po rozmiarze ustawia też katalogi według
  rozmiaru (`ItemComparer`), po skończeniu liczenia lista sortuje się raz jeszcze.
- **Kolumna szczegółów** (jedna, między datą a atrybutami, osobno dla panelu i zakładki, zapisywana): wymiary obrazu
  i filmu, data zrobienia zdjęcia, długość nagrania, wersja programu (Pokaż → Kolumna: …, także w menu „…” panelu;
  ponowny wybór ją chowa). Wartości z systemu właściwości Windows (`Core/Details/WindowsFileDetails`,
  `SHGetPropertyStoreFromParsingName`, GPS_BESTEFFORT — długość WAV nie jest „szybką” właściwością). Widoczne wiersze
  czytane w tle (najnowsze najpierw), tylko na dysku. Klik w nagłówek / Pokaż → Sortuj wg dodatkowej kolumny: czyta
  wszystkie pliki (8 naraz, postęp w stopce; System32, 4,5 tys. plików: 1,3 s), pliki bez wartości zawsze na końcu.
- **Filtr Ctrl+S**: trzecia data „zrobienia zdjęcia” i długość od–do (minuty, „1:30”, „1:02:00”); filtr czyta właściwości
  przed pokazaniem listy. Ten sam filtr z Ctrl+B: np. filmy dłuższe niż 10 min w całym drzewie.
- Testy: Core +4 (`FileDetailsTests`: formatowanie, filtr, minuty, prawdziwe PNG/WAV/DLL na Windows), Presentation +6
  (`DetailColumnTests`). Na maszynie testowej: wymiary tapet, długości dźwięków systemowych, rozmiary katalogów z sortowaniem
  malejącym, wersje w System32 posortowane. Zrzuty `docs/stage19-*.png`.
- Wąski panel z kolumną szczegółów przechodzi na układ z rozszerzeniem w nazwie i datą bez godziny wcześniej
  (o szerokość tej kolumny); bez atrybutów ma własny szablon `FullNarrowDetailTemplate`. Złapane w wersji z pakietu:
  w panelu ~420 px nazwy znikały (`docs/stage19-narrow-detail.png` po poprawce).
- Pułapki: x:Bind z funkcją na `ColumnDefinition.Width` w szablonie wiersza działa (`Ui.DetailWidth(DetailKind)`);
  odczyt właściwości po jednym pliku był za wolny (20–90 s na System32 razem z liczeniem katalogów).

### Etap 20 — sieć lokalna i obrazy dysków — scalony do master 2026-11-05 (wydanie 1.11.0)
Wybrane przez użytkownika 2026-11-05 z przeglądu wtyczek (pierwowzory: LAN Seeker, Virtual Disk). Nie wybrane (drugi
raz): podgląd multimediów i kodu, Android/WebDAV, automatyzacja i narzędzia.
- **Sieć** (`Core/Network`): miejsca `net://` (komputery) i `net://serwer` (jego udziały) jako `NetworkFileSystem` przed
  dyskiem; udział to zwykła ścieżka UNC, a z jego korzenia „..”/Backspace wraca do listy udziałów. Komputery z folderu
  Sieć powłoki (wykrywanie sieci, to samo co Eksplorator; ~14 s na maszynie testowej) i równolegle z NetServerEnum, zbierane na
  bieżąco, po 15 s pokazuje się to, co przyszło; wynik pamiętany 2 min, serwery otwarte w sesji zawsze na liście.
  Udziały z NetShareEnum (ukryte `$` przy Ctrl+H). Brak dostępu → okno logowania Windows (WNetAddConnection3,
  CONNECT_INTERACTIVE|PROMPT, z „Zapamiętaj”) i ponowna próba. Menu dysków: pozycja „Sieć”. Ctrl+L `\\serwer` otwiera
  listę udziałów. Okruszki: Sieć › serwer › udział › …
  Na liście komputerów i udziałów nic się nie zapisuje (F5/F6 do niej, F7, Shift+F6, przeciąganie, Shift+F2, Ctrl+B —
  komunikat; zaznaczenie puste); linia poleceń działa wtedy w katalogu użytkownika; liczenie rozmiarów katalogów
  pomija UNC.
- **Obrazy dysków** (Pliki → Podłącz obraz jako dysk / Odłącz dysk): czasowniki Eksploratora `mount`/`eject` przez
  ShellExecuteEx (SEE_MASK_INVOKEIDLIST, wątek STA) — ISO bez administratora, VHD z UAC; okno Eksploratora, które
  „mount” otwiera, jest zamykane; panel przechodzi na nowy dysk (czeka do 20 s), listy dysków się odświeżają. Odłączenie
  przenosi panele z tego dysku do katalogu użytkownika i czeka, aż dysk zniknie; dysku systemowego nie odłącza.
- Testy: Core +5 (`NetworkTests`), Presentation +5 (`NetworkPanelTests`), `PathSegmentTests` (UNC prowadzi do Sieci).
  Na maszynie testowej: wykrycie serwera plików w sieci, lista udziałów i pliki `\\maszyna-testowa\testshare` (udział tymczasowy), podłączenie
  i odłączenie `dokumenty.iso` (G:). Zrzuty `docs/stage20-*.png`.
- Środowisko: w sesji pulpitu maszyny testowej nawet `net view \\serwer` (i po IP) daje błąd 53, w sesji SSH działa — udziałów serwera
  nie dało się obejrzeć z aplikacji; lokalny udział działa. Wspólny katalog roboczy z sesją „Nowy wygląd aplikacji”:
  jej niescommitowane zmiany (App.xaml, MainWindow, FilePanel.xaml, FunctionBar, Ui.cs, HasMarks/GoRootAsync) zostały
  poza commitem etapu.

### Etap 21 — transliteracja w Ctrl+M i szyfrowanie plików — scalony do master 2026-11-05 (wydanie 1.12.0)
Wybrane przez użytkownika 2026-11-05 z przeglądu wtyczek (pierwowzory: Translit, DarkCrypt). Nie wybrane: obsługa
istniejących wtyczek (.wcx64/.wlx64/.wfx64/.wdx64), katalog dysku. Szyfrowanie: 7z i zip tylko AES-256, bez rar.
- **Transliteracja** (`Core/Rename/Transliteration`, opcja `RenameRules.Transliterate`, pole „Bez polskich znaków
  (łacinka)” w Ctrl+M): litera po literze na tekście złożonym (й, ё, ї to osobne litery), znaki diakrytyczne usuwane
  (ą→a, ł→l, ß→ss, æ→ae, ø→o), cyrylica (ros., ukr., białorus., serb., mac., bułg.) i greka z tabel; wielkie litery
  zostają („Жук”→„Zhuk”, „ЖУК”→„ZHUK”). Działa przed zmianą wielkości liter.
- **Szyfrowanie**: zip z hasłem pisze 7-Zip z `em=AES256` i `mt=off` (zip wraca do pliku, którego nie skompresował,
  więc plik zostaje otwarty do końca jego wpisu; równoległe pakowanie zamykało go w trakcie — błąd 0x80131622);
  zip bez hasła nadal SharpCompress. Alt+F5: pole hasła dla 7z i zip (dla zip z uwagą o Eksploratorze).
  Pliki → Zaszyfruj… (`EncryptOperation`): każdy element do własnego `nazwa.7z`/`nazwa.zip` obok, potem pytanie
  „Usunąć oryginały?” (trwale; z uwagą o SSD i BitLockerze; domyślnie „Zostaw”), usuwanie dodawane do kolejki bez
  czekania (czekanie w kroku po operacji zakleszczyłoby kolejkę).
  Zaszyfrowany zip zmienia się przez 7-Zip (F5/F6/F8/F7/Shift+F6 w środku), a nowe wpisy dostają hasło archiwum.
  `SevenZipArchive.RequirePassword`: hasło sprawdzane próbnym rozpakowaniem najmniejszego zaszyfrowanego pliku, przy
  złym pytanie ponownie — przed dopisywaniem i przed rozpakowaniem (zip ma jawne nazwy, więc hasło pada dopiero tam).
- Testy: Core +17 (`TransliterationTests`, zip AES, dopisywanie do zaszyfrowanego zipa, `EncryptOperation` 7z/zip),
  Presentation +3 (`EncryptTests`; 7z.dll kopiowany też do testów Presentation). Na maszynie testowej: Ctrl+M z łacinką
  (polskie, cyrylica, greka), Zaszyfruj → `umowa.pdf.7z` (7z.exe: 7zAES, złe hasło odrzucone), usunięcie oryginału.
  Zrzuty `docs/stage21-*.png`.

### Etap 22 — cofanie operacji (Ctrl+Z) — gałąź `etap-22`
Wybrane przez użytkownika 2026-11-05 z przeglądu innych menedżerów (`docs/przeglad-menedzerow.md`; pierwowzory:
XYplorer, Directory Opus).
- **Dziennik** (`Core/Undo`): operacje zapisują, co zrobiły (`IUndoable.Journal`): kopiowanie i przenoszenie
  (utworzone pliki i katalogi, przeniesienia, usunięte katalogi źródłowe, nadpisane pliki), kosz (co i kiedy),
  atrybuty i daty (poprzednie wartości), Alt+F5, wypakowanie i Alt+F9, Zaszyfruj (utworzone archiwa). F7, Shift+F4,
  Shift+F6 i Ctrl+M dopisują krok same. Operacje na serwerach, w archiwach, trwałe usunięcie i praca pomocnika
  administratora nie dają kroku.
- **Ctrl+Z** (Polecenia → Cofnij ostatnią operację; w polu tekstowym Ctrl+Z cofa pisanie): okno mówi, co się stanie
  (z odmianą: „2 elementy wrócą”, uwaga o nadpisanych plikach), potem `UndoOperation` w kolejce: atrybuty, odtworzenie
  katalogów źródłowych, przeniesienia z powrotem (od najnowszego), przywrócenie z kosza, a utworzone elementy (tylko
  najwyższego poziomu) do kosza — pusty katalog po prostu usuwany. Nic nie jest usuwane trwale ani nadpisywane: zajęte
  miejsce albo brak elementu to pytanie „pomiń/ponów”, reszta idzie dalej. Historia 50 kroków, tylko w sesji.
- **Kosz** (`WindowsRecycleBinRestore`): pary `$R`/`$I` w `X:\$Recycle.Bin\<SID>`; `$I` (wersja 2: ścieżka i czas
  usunięcia) wskazuje najnowszy element z tej ścieżki, `$R` wraca na miejsce, `$I` jest usuwany — jak „Przywróć”
  w Eksploratorze. W sesji SSH Windows usuwa trwale zamiast do kosza, więc ten test idzie w sesji pulpitu
  (schtasks /it), a przez SSH się pomija.
- Testy: Core +9 (`UndoTests`), Presentation +4 (`UndoCommandTests`). Na maszynie testowej: F8 na pliku i katalogu → Ctrl+Z
  przywraca oba z prawdziwego kosza; F5 pliku i katalogu → Ctrl+Z usuwa kopie, źródła zostają. Zrzuty
  `docs/stage22-undo-*.png`.

#### Szyfrowanie `.pce` (własny format, 2026-11-05)
Projekt w `docs/szyfrowanie.md`. 7z/zip z etapu 21 zostają w menu jako format dla innych programów; `.pce` jest
domyślny i nie wymaga 7-Zip.
- **Format** (`Core/Crypto`): nagłówek 42 B jawny (magia `PCE1`, flagi katalog/deflate, KDF, szyfr, parametry
  Argon2, sól 16 B, rozmiar bloku, prefiks nonce 8 B) i łańcuch bloków po 1 MiB: `[u32 długość][szyfrogram][tag 16 B]`,
  AES‑256‑GCM (`AesGcm` z .NET, sprzętowy; ChaCha20‑Poly1305 bez AES‑NI), nagłówek jako AAD każdego bloku, nonce =
  prefiks ‖ licznik, ostatni blok z bitem 31 — obcięcie, zamiana kolejności i doklejenie są odrzucane. Blok 0 to
  metadane (nazwa, rozmiar, czas, atrybuty), więc złe hasło wychodzi od razu (`PceWrongPasswordException`), bez
  czytania treści. Klucz: Argon2id 64 MiB / t=3 / p=4 (pakiet Konscious), alternatywnie PBKDF2‑SHA512 600 k.
  Katalog idzie jako TAR (zapis SharpCompress, odczyt własny strumieniowy — `TarReader` SharpCompress wymaga
  przewijania). Kompresja zstd poziom 3 (z SharpCompress) przed szyfrowaniem w trybie automatycznym: próbka
  pierwszego megabajta (pliku albo plików katalogu po kolei) musi zmaleć poniżej 95 %, inaczej zapis bez kompresji;
  do wyboru też „zawsze” i „nigdy”. Flaga Deflate z pierwszej wersji nadal czytana.
- **Enter na `.pce` otwiera zawartość** (zmiana po uwadze: „po haśle chcę chodzić po zawartości i wyciągać katalog albo
  pojedynczy plik”): hasło → zawartość jak archiwum (katalog jako TAR, pojedynczy plik jako TAR z jedną pozycją,
  `PceFile.CopyContentAsTar`) w pliku tymczasowym; F5 wyciąga plik albo katalog, Backspace z korzenia wraca do folderu
  z `.pce` (`PceBrowse`). Odszyfrowanie całości na dysk: Pliki → Odszyfruj. Ctrl+PgDn robi to samo co Enter.
- **Pamięć haseł `.pce`**: hasło, które otworzyło plik, jest pamiętane (tylko w pamięci programu, per plik) do zamknięcia
  Plain Commandera; kolejne otwarcie (Enter, Ctrl+PgDn, Odszyfruj) nie pyta. Gdy plik podmieniono i hasło już nie
  pasuje, jest zapominane i program pyta od nowa.
- **Ikona na pasku zadań**: wariant „unplated” był ciemnoniebieski na przezroczystym tle (na ciemnym pasku nie było go
  widać) i zajmował ok. 60% pola. `build/make-icons.ps1`: jasny znak na ciemny pasek (`altform-unplated`), ciemny na
  jasny (`altform-lightunplated`), marginesy 5% (bez kwadratu) i 15% (z kwadratem).
- **Kursor a zaznaczenie** (po uwadze, że szary kursor w panelu nieaktywnym wygląda jak zaznaczenie): zaznaczona
  pozycja listy (kursor) nie ma szarego tła, tylko pasek akcentu z lewej (i ramkę, gdy panel ma klawiaturę);
  `FilePanel.xaml` nadpisuje `ListViewItemBackgroundSelected*`. Zaznaczenie plików (Insert) ma mocniejszy kolor:
  `PcMarkBrush` 0x66 (ciemny) i 0x52 (jasny) zamiast 0x33 i 0x26.
- **OneDrive na liście dysków** (po prośbie o bezpośredni dostęp do dwóch OneDrive): `Core/FileSystem/CloudFolders`
  czyta foldery z rejestru (`HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder`: Personal, potem Business1…)
  i ze zmiennych `OneDriveConsumer/Commercial/OneDrive`; panel dokłada je za dyskami (`DriveEntry.CloudName`): przyciski
  z chmurką („OneDrive”, firmowy pod nazwą organizacji) obok `C: D:` oraz pozycje w menu Alt+F1/F2. Klik prowadzi do
  górnego folderu OneDrive; w jego wnętrzu ten przycisk jest bieżący (zamiast dysku). Brak OneDrive — nic nie dochodzi.
- **Katalog docelowy i skrót** (po zgłoszeniu: „brak dostępu” przy `C:\2`, bo kopia szła do `C:\2.pce`, a zwykły użytkownik nie
  tworzy plików w głównym katalogu dysku): okno Zaszyfruj ma pole „Zapisz w katalogu” (domyślnie katalog drugiego
  panelu, jak Alt+F5; brakujący katalog jest tworzony), działa dla `.pce`, 7z i zip; katalog nie może być zaszyfrowany
  do siebie samego ani podkatalogu. **Ctrl+E** = Zaszyfruj. Odszyfrowanie nadal „obok kontenera”.
- **Operacje**: `EncryptOperation` z `.pce` (opcje: losowa nazwa z ukrytą prawdziwą w środku, kompresja; zapis do
  `.part` i podmiana; konflikt jak przy pakowaniu; Ctrl+Z przez dziennik), `DecryptOperation` (obok kontenera pod
  nazwą ze środka; katalog scalany do istniejącego po „Nadpisz”, inaczej wolna nazwa; złe hasło przy kolejnym
  pliku → pytanie pomiń/przerwij).
- **UI**: Pliki → Zaszyfruj… (format `.pce` pierwszy, 7z/zip gdy jest 7‑Zip), Pliki → Odszyfruj…, Enter na `.pce`
  pyta o hasło (ponawia przy złym) i odszyfrowuje obok, Ctrl+PgDn na `.pce` z katalogiem rozszyfrowuje TAR do
  `%TEMP%\PlainCommander\pce-*` i wchodzi do niego jak do archiwum (sprzątane przy starcie). Pliki `.pce` w panelach
  fioletowe (`PcEncryptedTextBrush`).
- **Wydajność** (maszyna testowa, `PceBenchmarkTests` z `PC_PCE_BENCH`): 1 GiB losowy, tryb auto (próbka mówi „nie”):
  szyfrowanie 1,4 s (≈740 MiB/s, w tym wyprowadzenie klucza), odszyfrowanie 1,5 s; Argon2id 0,07 s; 254 MiB tekstu
  z zstd: 0,64 s (≈400 MiB/s), wynik 1 % rozmiaru. Dla porównania 7z AES na tym samym pliku: minuty.
- Testy: Core +27 (`PceTests`: rozmiary 0…3 bloki+7, zstd zawsze i auto (tekst tak, losowe nie, katalog), PBKDF2/ChaCha, złe hasło, obcięcie, przestawiony
  bajt w bloku i w nagłówku, zamiana bloków, dopisane bajty, katalog, losowa nazwa, operacje, anulowanie bez `.part`),
  Presentation +3 (`.pce` bez 7‑Zip + Enter po złym haśle, Ctrl+PgDn, Odszyfruj bez `.pce`). Na maszyna testowa: Zaszyfruj
  dwóch elementów, Ctrl+PgDn z błędnym i dobrym hasłem, Enter → konflikt → „Zachowaj oba”. Zrzuty
  `docs/stage22-encrypt-pce.png`, `stage22-decrypt-password.png`, `stage22-browse-pce.png`.

### Po 1.13.6 — WSL w wyborze dysku

- Zgłoszenie: w menu Alt+F1/F2 nie było dystrybucji WSL2. `\\wsl.localhost` nie jest ani dyskiem (`DriveInfo`), ani
  komputerem z NetServerEnum, więc nie miało skąd się wziąć. `WslDistros.Find` czyta rejestr
  (`HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{guid}\DistributionName`, `State == 1`, domyślna pierwsza);
  `NetworkFileSystem` dokłada komputer `wsl.localhost` do `net://` i listuje dystrybucje jako jego udziały (zamiast
  NetShareEnum, które o nich nie wie). Panel dokłada dystrybucje do `Drives` za OneDrive (`DriveItem.IsWsl`,
  `Type = Network`, korzeń `\\wsl.localhost\Ubuntu`): pigułki z ikoną terminala obok `C: D:` i pozycje
  `Ubuntu   \\wsl.localhost\Ubuntu` w menu Alt+F1/F2 (pogrubione, gdy panel jest w tej dystrybucji); klik przez
  `ChangeDriveAsync`, więc wraca do ostatniego folderu na niej. Klucze `_lastPathPerDrive` dla UNC bez końcowego `\`
  (`RootKey`). Zatrzymana dystrybucja startuje przy pierwszym wejściu. Testy: Core +1, Presentation +1. Sprawdzone na
  maszynie testowej z WSL 3.0.3 i Ubuntu 26.04: pigułka i wpis w menu, korzeń dystrybucji, `..` → lista pod
  `wsl.localhost`, powrót przez pigułkę do ostatniego folderu (`home`).

### 1.13.8 — fokus uciekał do prawego panelu

- Zgłoszenie po 1.13.7: przy chodzeniu po folderach w lewym panelu prawy „mrugał”, a czasem stawał się aktywny i klawiatura
  szła do niego. Odtworzone na maszynie testowej z logiem fokusu: po Enter/Backspace wiersz z fokusem znika z listy
  (nowe `Items`), a WinUI przenosi fokus na następną kontrolkę, którą znajduje — okruszek prawego panelu
  (`BreadcrumbBarItem`) — i zgłasza go ze stanem `Keyboard`, bo `FocusState` odzwierciedla ostatnie urządzenie, nie
  intencję użytkownika. `GotFocus` z tym stanem aktywował prawy panel; kolejny klawisz (Backspace) szedł już w prawo.
  To nie było związane z WSL (działo się na `C:\`), tylko zbiegło się w czasie.
- Poprawka: panel staje się aktywny wyłącznie po kliknięciu w nim (`PointerPressed`, także obsłużone zdarzenia) albo
  jawnie (Tab, Alt+F1/F2, pigułki dysków). Fokus, który wszedł do nieaktywnego panelu, jest od razu zwracany
  aktywnemu (`FilePanel.FocusStrayed` → `MainWindow.ReturnStrayFocus` → `FocusListNow`), z wpisem w logu
  „Focus strayed into the inactive …”.
- Przy okazji: odświeżenie z obserwatora, które przynosi te same wpisy, nie podmienia listy (`SameListing` w
  `NavigateAsync` z `keepState`) — koniec mrugania przy fałszywych zdarzeniach; jawne `focusName` nadal przesuwa
  kursor. Udziały WSL nie dają powiadomień (`ReadDirectoryChangesW` → błąd 1 „Niepoprawna funkcja”): obserwator
  loguje to raz i nie wymusza ponownego odczytu (wcześniej każde wejście listowało katalog dwa razy).
- Testy: Presentation +1 (`Refresh_that_brings_the_same_entries_keeps_the_rows_and_the_cursor`). Na maszynie
  testowej: Enter/Backspace z klawiatury i dwuklikiem w lewym przy prawym na `C:\` i w WSL, Tab, klik w wiersz
  i w pole filtra prawego panelu.

### 1.14.0 — test dziwnych kombinacji
- Testy przypadków brzegowych (nazwy, ścieżki, operacje, szyfrowanie, archiwa, stany paneli, system plików Windows)
  i przeklikanie GUI na maszynie testowej: 45 błędów w logice i 9 w interfejsie, wszystkie naprawione. Opis błędów
  i napraw: docs/testy-dziwne-kombinacje.md; testy regresji w `tests/*/Weird/` (prefiks `Regression_`).
- Najważniejsze: zip-slip przy rozpakowywaniu, kasowanie celu po nieudanej kopii dużego pliku (ten sam plik przez
  junction/symlink/8.3 rozpoznawany po tożsamości), nadpisanie przy zmianie wielkości liter na serwerze, usuwanie na
  serwerze poza folderem, folder `aux`/`con` kopiowany do bieżącego katalogu, `null` w settings.json blokujący start.
- Interfejs: Alt+F4 przy działającej operacji pyta, klawisze w trakcie zamykania dialogu i parkowania fokusu są
  wstrzymywane i odtwarzane, aktywna karta zawsze widoczna, dialogi i okna w motywie aplikacji, kolory z kodu
  zmieniają się od razu z motywem, minimalny rozmiar okna, Esc czyści wiersz poleceń.
- `.pce`: hasło normalizowane do NFC (bit 3 flag nagłówka); pliki z 1.13.x próbują hasła jak wpisane, potem NFC i NFD.

### 1.14.1 — testy tłumaczeń, systemów plików, fuzzing, losowe sekwencje
- Nowe testy: spójność tłumaczeń (`tests/PlainCommander.Presentation.Tests/Resources`), macierz systemów plików
  FAT16/FAT32/exFAT/ReFS/pełny NTFS (`tests/PlainCommander.Core.Tests/Volumes`, dyski z `build/test-volumes.ps1`),
  fuzzing Core (`Fuzz/`) i losowe sekwencje poleceń na ViewModelach (`Monkey/`). Opis: docs/testy-dziwne-kombinacje.md, runda 2.
- Poprawki: flaga UTF-8 w nazwach zip, tolerancja 2 s FAT przy wznawianiu kopiowania, pytanie o utratę strumieni ADS
  przy kopiowaniu i przenoszeniu na FAT/exFAT (duże pliki z ADS wcześniej w ogóle się nie kopiowały), nazwa z `.pce` jako
  zwykła nazwa pliku, uszkodzone archiwa zgłaszane jako uszkodzone, ucieczka z folderu przez `dir/C:x`, Ctrl+Z/Ctrl+B/anulowanie
  w nietypowych stanach, zmiana na dysku w trakcie wczytywania panelu, F3 na pliku z serwera o nazwie `CON`/`report.`.

### 1.14.2 — przegląd funkcjonalny i klawiatura
- Klawiatura: Ctrl+↑/↓/Home/End przesuwało tylko ramkę fokusu, a kursor zostawał (Enter, F5, F8 działały na innym
  wierszu) — kursor idzie teraz za ramką; Ctrl+Spacja nie zdejmuje kursora z wiersza; Ctrl+←/→ w polu tekstowym
  (linia poleceń, filtr, ścieżka) przeskakuje o słowo zamiast otwierać ten sam folder w drugim panelu.
- Klik w nieaktywny panel aktywuje go (wiersz dostawał fokus przed PointerPressed panelu i fokus wracał).
- Daty w kolumnach mają stałą szerokość (`05.10.2026`, także gdy wzorzec systemu to `d.MM.yyyy`).
- Panel, którego folder zniknął, nie trafia do katalogu domowego, gdy pierwszą nawigację anulowała nowsza
  (niestabilny test losowy, seed 17).
- Znane: menu F2 otwarte w chwili, gdy kończy się poprzednie polecenie, zamyka się samo.

### 1.14.3 — wyszukiwanie po fragmencie nazwy
- Alt+F7: słowo bez gwiazdek jest fragmentem nazwy (`raport` znajduje `raport 2026.pdf`), jak w filtrze panelu;
  wcześniej wymagało `*raport*` i bez gwiazdek nie znajdowało nic. Maski działają jak dotąd.
- Alt+F7: strzałka w dół z pola tekstowego przenosi do listy wyników (Tab wymagał kilkunastu naciśnięć).
- Pola tekstowe nie podkreślają nazw plików, ścieżek i masek jako błędów pisowni.
- Sprawdzone w działającej aplikacji bez zarzutu: Ctrl+M, Ctrl+F, Ctrl+B, Ctrl+H, Ctrl+E, Shift+F2 z synchronizacją,
  konflikt kopiowania, karta postępu, Alt+F4 w trakcie operacji, podgląd pliku 8 GB, archiwa, linia poleceń.
- Alt+F7: nazwa wpisana od razu po skrócie nie ginie. Okno aktywne przed pierwszym układem treści gubiło klawisze
  (wszystkie przez ok. 0,3 s); teraz pokazuje się bez klawiatury, a klawisze wpisane w tym czasie główne okno
  wstrzymuje i odtwarza, gdy pole nazwy jest gotowe.
- Otwarty plik `.pce` pokazuje na karcie, w ścieżce i w linii poleceń własną nazwę zamiast `nazwa.tar`.
- Porównanie folderów (Shift+F2) pomija pliki ukryte, gdy panele ich nie pokazują.
- Powrót z listy płaskiej (Ctrl+B) i z wyników wyszukiwania zostawia kursor na pliku albo na folderze, który do niego prowadzi.
- Menu dysków: litery działają także po otwarciu myszą i nie głuchną po odświeżeniu listy.

## 5. Skróty klawiszowe

| Klawisz | Działanie |
|---|---|
| Tab | przełącz panel |
| Enter | otwórz katalog / uruchom plik |
| Backspace, Ctrl+PgUp | katalog nadrzędny |
| Ctrl+PgDn | wejdź do katalogu lub archiwum; instalator (exe, msi) albo obraz dysku otwórz jak archiwum (nie PDF ani Office); katalog w pliku `.pce` przeglądaj po haśle |
| Alt+F1 / Alt+F2 | wybór dysku lewy / prawy |
| F3 | podgląd |
| F4 | edycja |
| F5 | kopiuj |
| F6 | przenieś / zmień nazwę (Shift+F6 zmiana nazwy w miejscu) |
| F7 | nowy katalog |
| F8, Del | usuń do kosza; Shift+F8 / Shift+Del na stałe |
| Alt+F4 | zamknij |
| Insert, Spacja | zaznacz / odznacz |
| Num + / Num − / Num * | zaznacz wg maski / odznacz wg maski / odwróć |
| Ctrl+A | zaznacz wszystko |
| Ctrl+R | odśwież |
| Ctrl+U | zamień panele |
| Ctrl+← / Ctrl+→ | ten sam katalog w drugim panelu |
| Ctrl+D | ulubione katalogi |
| Alt+↓ | historia katalogów |
| Ctrl+F1 / Ctrl+F2 | widok skrócony / pełny |
| Ctrl+F3 / F4 / F5 / F6 | sortuj po nazwie / rozszerzeniu / dacie / rozmiarze; ponownie odwraca kierunek |
| Ctrl+H | pokaż ukryte |
| Ctrl+Z | cofnij ostatnią operację na plikach (z potwierdzeniem) |
| Ctrl+S | filtr: nazwa, rozszerzenia, data modyfikacji/utworzenia, rozmiar; Esc zdejmuje |
| Alt+F7 | szukaj (1.x) |
| Alt+F5 / Alt+F9 | pakuj (zip, 7z, tar, tar.gz) / rozpakuj zaznaczone archiwa |
| Alt+Enter | właściwości |
| Ctrl+Enter | wstaw nazwę do linii poleceń |
| Litery | szybkie szukanie: fragment nazwy; ↑/↓ kolejne trafienia; Ctrl+S zostawia same trafienia |

## 6. Lista kontrolna testów ręcznych

Przegląd 2026-10-30 na maszynie testowej (etap 4, gałąź `etap-4`). Dane testowe tworzone skryptem w `C:\work\check`.

| Punkt | Wynik | Uwagi |
|---|---|---|
| Kopiowanie 10 GB jednego pliku, anulowanie w połowie | ✅ (etap 2) | 12 GB: pauza, Anuluj, niepełna kopia usunięta, miejsce odzyskane |
| Kopiowanie 50 tys. małych plików, postęp | ✅ (etap 2) | 18,3 s, karta postępu i chip w etapie 3 |
| Konflikt nazw, każda opcja dialogu | ✅ | Nadpisz, Pomiń, Zachowaj oba, Nadpisuj tylko starsze, Anuluj; „… wszystkie” dla 2 plików bez drugiego pytania |
| Katalog ze 100 tys. wpisów | ✅ | 33 ms renderu, koniec listy przycina się poprawnie |
| Katalog bez uprawnień | ✅ po poprawce | Wcześniej pokazywał pustą listę (`IgnoreInaccessible`); teraz „Brak dostępu” i panel zostaje |
| Dysk sieciowy odłączony w trakcie | ✅ | Udział `\\localhost\chk` usunięty przy otwartym panelu: Ctrl+R zostawia listę i pokazuje „Nie można odnaleźć nazwy sieciowej”, wejście głębiej daje błąd zamiast zawieszenia. Mapowanie literą z sesji SSH jest niewidoczne w sesji interaktywnej, więc test przez UNC |
| Ścieżki powyżej 260 znaków | ✅ (etap 2) | 318–335 znaków bez LongPathsEnabled |
| Nazwy z Unicode i spacjami | ✅ | `zażółć gęślą jaźń.txt`, `日本語 файл.txt`, podwójne spacje |
| Nazwy z kropką na końcu | ✅ po poprawce | `kropka.` i `folder.`: `Path.GetFullPath` i Win32 obcinają kropkę, stąd `ExactPath` (Full/Native/Prefixed/Display) w listowaniu, nawigacji, kopiowaniu, usuwaniu i zmianie nazwy; kosz nie przyjmuje takich nazw nawet za `\\?\`, więc przed wyrzuceniem plik dostaje nazwę bez kropki. „Zachowaj oba” daje `kropka (2)` bez kropki (znane) |
| Pliki ukryte i systemowe | ✅ | Wygaszone przy Ctrl+H, atrybuty zachowane po kopii |
| Punkt połączenia (junction) z pętlą, dowiązanie | ✅ | Kopia drzewa z `loop → ..` kończy się bez pętli (link skopiowany jako pusty katalog, zawartość raz); F8 na junction i Shift+Del na dowiązaniu usuwają tylko link, cel nietknięty |
| Praca tylko klawiaturą przez całą sesję | ✅ | Wszystkie testy etapów 2–4 prowadzone klawiaturą przez skrypt wysyłający klawisze do maszyny testowej; mysz tylko do rozdzielacza i menu kontekstowego, które mają też Shift+F10 |
| Motyw jasny / ciemny | ✅ | Przełączany na żywo z okna ustawień |
| Skalowanie DPI 150 / 200 % | ⏳ nie sprawdzone | Zmiana skalowania na VM wymaga wylogowania sesji interaktywnej, z której działają skrypty; do sprawdzenia ręcznie na fizycznej maszynie |
| Dwa monitory | ⏳ nie sprawdzone | VM ma jeden ekran; zapis położenia okna jest sprawdzany względem `DisplayArea`, więc okno z odłączonego monitora wraca na główny |

Znane zachowania do decyzji przed 1.0: po operacji z pominięciami zaznaczenie zostaje na wszystkich elementach (również
skopiowanych), link kopiowany jest jako pusty katalog zamiast odtworzenia linku.

## 7. Ryzyka

- WinUI 3 ListView przy bardzo dużych katalogach: sprawdzone w etapie 1, 100 tys. wpisów działa. Ryzyko przesunięte na błędy samego WinUI (patrz uwagi do etapu 1).
- Ikony shella przy tysiącach plików blokują UI: mitygacja — ładowanie w tle, placeholder, cache.
- Operacje wymagające uprawnień administratora: rozwiązane w etapie 11 (proces pomocniczy przez UAC).
- Brak dostępu do środowiska Windows z WSL: build i testy UI tylko na maszynie Windows.

## 8. Do ustalenia

- ~~Czy podgląd F3 ma być wbudowany (własne okno), czy odpalać zewnętrzny program?~~ Wbudowany (etap 3).
- ~~Czy linia poleceń ma używać `cmd` czy PowerShell domyślnie?~~ `cmd /c` (etap 3); PowerShell do rozważenia jako ustawienie.
- Czy aplikacja ma być przenośna (`settings.json` obok exe) obok MSIX?
- Licencja i nazwa repozytorium.
