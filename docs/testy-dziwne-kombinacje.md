# Test dziwnych kombinacji (2026-11-07)

Nowe testy leżą w `tests/PlainCommander.Core.Tests/Weird/` i `tests/PlainCommander.Presentation.Tests/Weird/`.
Każdy znaleziony błąd ma test regresji o nazwie zaczynającej się od `Regression_` (w `WindowsFileSystemTests` nazwa opisowa).

**Stan 2026-11-07: wszystkie błędy poniżej naprawione.** Testy przechodzą na WSL (Core 832, Presentation 286, zero błędów)
i na VM Windows (Core 837, Presentation 286, zero błędów, razem z 7z.dll). Naprawiono też dwie obserwacje:
limit kosztu KDF z nagłówka `.pce` i pusty plik po `PceFile.Encrypt` z pustym hasłem.
Bez zmian zostały celowo: normalizacja haseł (zepsułaby istniejące pliki) i pytanie o duplikaty w zip.

Tabela niżej pokazuje stan sprzed napraw.

| Zestaw | Testy | Przechodzą | Błędy | Pominięte |
|---|---|---|---|---|
| Core, WSL | 809 | 659 | 28 | 122 (Windows, 7z.dll, serwery) |
| Presentation, WSL | 286 | 269 | 14 | 3 |
| Core, VM Windows (tylko nowa klasa Windows) | 89 | 78 | 11 | 0 |

Ten sam błąd bywa pokazany kilkoma przypadkami testu, więc liczby nie równają się liczbie błędów poniżej.

## Utrata danych i bezpieczeństwo

1. **Zip-slip przy rozpakowywaniu.** Wpis `../../x` w zip lub tar trafia poza folder docelowy.
   `Operations/ArchiveExtractOperation.cs:83-91` składa ścieżkę bez sprawdzenia, `ArchivePath.NormalizeKey` zostawia `..`.
   `PceFile.Destination` robi to poprawnie i może posłużyć za wzór. Nie sprawdzono ścieżki przez 7z.dll.
2. **Nieudana kopia dużego pliku kasuje cel, którego nie zapisała.** `TransferOperation.cs:502-518` usuwa `destination` zawsze, gdy kopia się nie udała.
   Kopia pliku ≥4 MB na samego siebie przez junction, symlink, nazwę 8.3 lub dowiązanie twarde kasuje jedyną kopię.
   To samo przy nadpisaniu ukrytego lub systemowego celu oraz przy zablokowanym źródle z odpowiedzią „Nadpisz”, potem „Pomiń”.
   Potwierdzone na VM. `PathRules.Same` porównuje tylko tekst ścieżek.
3. **Przeniesienie ze `skipIdentical` na ten sam plik inną ścieżką kasuje jedyną kopię.** `TransferOperation.cs:313-320`. Dotyczy kontynuacji jako administrator.
4. **Folder `aux`, `con`, `nul`, `prn`, `COMn`, `LPTn` kopiuje się do bieżącego katalogu procesu.** `TransferOperation.cs:216`: `GetRelativePath` z `"aux"` daje `\\.\aux`.
   Przeniesienie z łączeniem skasowałoby potem źródło.
5. **Zmiana wielkości liter na serwerze nadpisuje drugi plik.** `MainViewModel.cs:1608-1613` pomija sprawdzenie istnienia, a FTP i S3 nadpisują cel.
   Serwer z `a.txt` i `A.txt`: Shift+F6 `a.txt` → `A.txt` po cichu niszczy `A.txt`.
6. **Usuwanie folderu na serwerze kasuje plik spoza niego.** Nazwa z listingu typu `..\keep.txt` jest rozwijana przez `RemoteLocation.Child` (`RemotePath.cs:34`, `JoinInner` :159).
   Na serwerze uniksowym taka nazwa jest dozwolona.
7. **Uszkodzony folder `.pce` zostawia częściowy folder i obcięty plik.** `PceFile.ReadTar` (`PceFile.cs:336-363`) pisze od razu do celu.
   Przy „Nadpisz” w istniejący folder obcina istniejące pliki.
8. **Plik tymczasowy `.part` niszczy plik użytkownika o tej nazwie.** `DecryptOperation.cs:91`, też `EncryptOperation.cs:352`, `ArchivePackOperation.cs:144`, `ArchiveUpdateOperation.cs:239/320`.
9. **Archiwum `...zip` rozpakowuje się do folderu nadrzędnego.** `ArchiveExtractOperation.cs:44` daje nazwę `..`.
10. **Kopia drzewa z linkiem do folderu gubi zawartość.** `TreeScanner.cs:115-118` pomija każdy już widziany folder, nie tylko przodków.
    Folder `real` obok linku `alink -> real` może wyjść pusty, a operacja kończy się sukcesem.
11. **Przeniesienie z łączeniem zamienia junction lub symlink do folderu w pusty folder.** `TransferOperation.cs:226-245` i `:281`. Potwierdzone na Linuksie i VM.
12. **Multi-rename `a.` zmienia nazwę pliku `a`.** `MultiRename.cs:349-357` nie używa `ExactPath`.
13. **Nazwy kończące się kropką lub spacją wewnątrz zwykłego folderu.** Kopia, przeniesienie i usuwanie trafiają w bliźniaka bez kropki albo kończą się „nie znaleziono”.
    `ExactPath.Native` prefiksuje tylko korzeń, `TreeScanner` podaje dzieci bez `\\?\`.

## Awarie aplikacji

1. **Transliteracja z emoji wywala aplikację.** `Rename/Transliteration.cs:63` normalizuje połówki surogatów osobno. Ctrl+M, „Transliteracja”, plik z emoji.
2. **`null` w settings.json blokuje start.** Sekcje `left`, `right`, `tabs: [null]`, `trustedHosts`, `commandHistory`, `favorites`. `SettingsStore.cs:31`.
3. **Nieczytelny settings.json blokuje start.** `SettingsStore.Load` nie łapie `UnauthorizedAccessException`.
4. **„NaN”, „Infinity”, „1e300” w polu czasu filtra Ctrl+S.** `PanelFilter.cs:86-88` rzuca wyjątek inny niż `FormatException`.
5. **Spreparowany `.pce` wywala operację.** Ogromny nagłówek długiej nazwy TAR (`PceFile.cs:327`) i błędny zstd (`PceFile.cs:174`) dają wyjątki, których `StepRunner` nie obsługuje.
6. **Porównanie plików zużywa pamięć rzędu długość × liczba różnic.** `Compare/TextDiff.cs:183` klonuje tablicę V w każdym kroku. 10 000 linii z co dziesiątą zmienioną to 322 MB.
7. **Filtr z datą końcową 9999-12-31.** `PanelFilter.cs:168`, prawdopodobnie nieosiągalne z wybieraka dat.
8. **`TextFold.Fold` na nazwie z niesparowanym surogatem.** `Navigation/TextFold.cs:20`. NTFS pozwala na takie nazwy.

## Błędne zachowanie

- **Plik o nazwie `NUL` nie da się skopiować, przenieść ani usunąć.** `ExactPath.cs:40/44`: `GetFullPath` zamienia go w urządzenie `\\.\NUL`.
  `SimpleCommands.CreateFile(dir, "NUL")` zgłasza sukces i nic nie tworzy.
- **Zmiana wielkości liter na istniejącego bliźniaka w folderze rozróżniającym wielkość liter** zostawia plik pod nazwą `*.pc-rename-xxxxxxxx`. `SimpleCommands.cs:70-74`.
- **Nadpisanie ukrytego lub systemowego pliku** kończy się „Brak dostępu”. `TransferOperation.cs:426/434` zdejmuje tylko „Tylko do odczytu”.
- **Przeniesienie folderu „Tylko do odczytu”** z łączeniem zostawia pusty folder źródłowy. `TransferOperation.cs:281`.
- **Kopia pliku ≥4 MB gubi strumienie ADS i znacznik pobrania z internetu.** Ścieżka strumieniowa `TransferOperation.cs:463-499`.
- **Kopia kilku plików do nieistniejącego folderu** kończy się błędem dla każdego pliku, mimo obietnicy w `TargetResolver`.
- **Plik UTF-16 z BOM bywa uznany za binarny.** `Viewer/TextDecoder.cs:73` nie patrzy na BOM. Otwiera się w hex, Alt+F7 go pomija.
- **Panel pokazuje folder usunięty z drugiego panelu.** `PanelViewModel.cs:603` zostawia stare elementy, F7 odtworzyłoby usuniętą ścieżkę.
- **`cd /abs/ścieżka` na panelu serwera** wychodzi na dysk lokalny. `MainViewModel.cs:961`.
- **Ctrl+Enter wstawia `R&D.txt` bez cudzysłowu**, więc cmd dzieli polecenie. `CommandLineViewModel.cs:45` cytuje tylko spacje.
- **Ctrl+Z po zachowaniu obu plików** zostawia `f (2).txt`. Zapisane jako nadpisanie zamiast utworzenia.
- **„Zachowaj oba” przy nieudanej kopii** zostawia pusty `f (2).txt`. `TransferOperation.cs:436-437`.
- **Anulowane `PceFile.Encrypt`** zostawia poprawny, ale obcięty plik. Tylko przez bezpośrednie API, operacja to sprząta.
- **Fantomowe kroki cofania.** Zmiana nazwy na tę samą ze spacjami (`MainViewModel.cs:1657`) i F7 z `x/../y` (`MainViewModel.cs:1519-1520`).
- **Podgląd multi-rename** akceptuje cel zajęty przez plik z partii, który zostaje na miejscu. `MultiRename.cs:142`. Przycisk i tak jest wtedy wyłączony.
- **Drobne:** Backspace po `cd C:\x\` nie wychodzi wyżej; `\\pc/share` uznany za komputer; `PathRules.IsInside` zawsze fałsz dla korzenia dysku;
  IPv6 bez nawiasów dzielony na ostatnim dwukropku; licznik ujemny jako `0-5`; `ParseSize("NaN")` daje 0; sortowanie naturalne nie liczy cyfr spoza ASCII.

## Obserwacje bez testu

- Hasła `.pce` nie są normalizowane: NFC i NFD tego samego tekstu dają różne klucze.
- Koszt KDF pochodzi z nieuwierzytelnionego nagłówka. Spreparowany plik może zawiesić aplikację po wpisaniu hasła.
- `PceFile.Encrypt` z pustym hasłem zostawia pusty plik celu (tylko bezpośrednie API).
- Zip z dwoma wpisami o tej samej nazwie pyta o konflikt z plikiem, który właśnie sam zapisał.

## Sprawdzone i solidne

Kopiowanie i przenoszenie folderu do samego siebie, odpowiedzi na konflikty z wieloma wątkami, anulowanie w połowie drzewa.
Zarezerwowane nazwy plików poza `NUL`, ścieżki do ~32 000 znaków, pliki zablokowane, rzadkie i skompresowane, usuwanie nigdy nie wchodzi w junction.
Format `.pce`: każde obcięcie, przestawienie bloków i odwrócony bit jest odrzucany, przejścia ścieżek w TAR są zablokowane.
Cofanie łańcuchów zmian nazw i zamian, filtry i zaznaczenia ukryte filtrem, karty z usuniętym folderem, sortowanie naturalne spójne na losowych danych.

# Test dziwnych kombinacji w interfejsie (VM, 2026-11-07)

Klikanie i klawisze w prawdziwej aplikacji 1.13.11 (Debug), na wersji sprzed napraw powyżej.

**Stan 2026-11-07: wszystkie błędy i drobiazgi poniżej naprawione**, poprawki 1–8 sprawdzone w GUI na VM (zrzuty „przed” i „po”).
Przy okazji naprawione: hasła `.pce` normalizowane do NFC (flaga w nagłówku, stare pliki próbują obu form), kolory z kodu
(wiersze, karty, przyciski dysków) zmieniają się od razu przy zmianie motywu, wyścig statusu w wyszukiwaniu (test bywał czerwony).
Zmiana zachowania: panel, którego folder zniknął (także niedostępny udział sieciowy), przechodzi do najbliższego istniejącego folderu.
W logu nie było nieobsłużonych wyjątków.

1. **Alt+F4 w trakcie kopiowania zamyka aplikację bez pytania.** Kopia zostaje w połowie (4952 z 10 000 plików), także gdy czeka okno konfliktu.
   `MainWindow.xaml.cs:108` w `AppWindow.Closing` tylko zapisuje ustawienia. Przy F6 zostałoby pół przeniesionego drzewa.
2. **Szybkie Enter i Backspace zmieniają folder w nieaktywnym panelu.** Klawisz trafia w okruszek drugiego panelu, zanim fokus wróci („Focus strayed…”). Powtórzone 2 z 4 razy.
3. **Klawisz do ~200 ms po zamknięciu dialogu ginie.** `OnRootPreviewKeyDown` odrzuca klawisze, dopóki `DialogService` nie zdejmie `IsOpen` po animacji.
   Skutek: Ctrl+Z, Enter, Ctrl+Z, Enter otwiera plik w Notatniku, a dialog cofania pojawia się później.
4. **Przy wielu kartach aktywna karta i „+” są niewidoczne.** `FilePanel.xaml:212`: ukryty pasek przewijania, nic nie przewija do aktywnej karty.
5. **F7 w folderze usuniętym z zewnątrz odtwarza usunięte foldery.** Panel pokazał „Nie znaleziono”, ale został na starej liście.
6. **Wpis zip `a\\back\\slash.txt` daje folder bez nazwy.** Wejście w niego zapętla się, a rozpakowanie po cichu pomija plik i zgłasza sukces.
7. **Dialogi są jasne przy ciemnym motywie aplikacji.** `DialogService` nie ustawia `RequestedTheme`.
8. **Pasek tytułu nachodzi na przyciski okna przy szerokości ~850 px** i brak minimalnego rozmiaru okna (da się zrobić 136×100).
9. **Błąd zmiany nazwy na 260 znaków** pokazuje surowy komunikat systemu i gubi wpisaną nazwę.

Drobne: zmiana nazwy na `x.` tworzy nazwę z kropką na końcu, a spacja na końcu jest obcinana; teksty „1 item go back”, „Incoming: 1 bytes”;
zaznaczenia giną po Ctrl+Tab i powrocie; junction wygląda jak zwykły folder; Esc nie czyści wiersza poleceń.

Działało dobrze: dwa dialogi naraz, kolejka kopii w tle, szybkie wyszukiwanie z filtrem i zaznaczaniem, spam Ctrl+T/Ctrl+W,
podgląd pustego i 300 MB pliku, `cd` przez pętlę junction, Tab podczas wczytywania 100 000 plików.
