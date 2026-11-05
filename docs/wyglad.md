# Nowy wygląd Plain Commandera

Stan: propozycja z 2026-10-30, do wdrożenia po zamknięciu etapu 2. Makieta: `docs/wyglad-makieta.html`
(otwórz w przeglądarce; przełącznik motywu w prawym górnym rogu).

Jedyne założenie odziedziczone po starej wersji: dwa panele obok siebie. Reszta jest projektowana od zera
pod Windows 11.

## 1. Zasady

1. **Panele są treścią, wszystko inne jest ramą.** Okno to Mica, a każdy panel to osobna karta
   (`LayerFillColorDefault`, promień 8, obrys `CardStrokeColorDefault`). Między kartami 8 px przerwy.
   Aktywny panel ma pełną nieprzezroczystość i akcentową linię 2 px nad nagłówkiem; nieaktywny jest
   przygaszony do 70 % na tekście nagłówka. Koniec z niebieskim pasem ścieżki.
2. **Jeden pasek sterowania zamiast trzech.** Znika pasek menu, pasek dysków i osobna linia stanu okna.
   Zostają: wiersz tytułowy okna, dwa panele, pasek poleceń i pasek akcji (F2–F8).
   Menu jest pod F2 i pod przyciskiem „⋯” w wierszu tytułowym: `MenuFlyout` z tymi samymi grupami
   (Pliki, Zaznacz, Polecenia, Pokaż, Konfiguracja). Alt nadal otwiera je z klawiatury.
3. **Klawiatura ma pierwszeństwo.** Każdy element wizualny pokazuje swój skrót (kbd obok etykiety).
   Nic nie wymaga myszy, ale mysz dostaje nowoczesne afordancje: klikalne okruszki, nagłówki kolumn,
   przeciągnij-upuść między panelami.
4. **Gęstość pod gęste katalogi.** Wiersz 26 px (ustawienie „kompaktowo”: 22 px), czcionka 13 px
   Segoe UI Variable Text, cyfry tabelaryczne w kolumnach rozmiaru i daty.
5. **Kolor znaczy coś.** Akcent systemowy wskazuje aktywny panel, kursor i zaznaczenie.
   Kolory semantyczne (ostrzeżenie, błąd) tylko w dialogach i na pasku operacji. Zero czerwonego tekstu.

## 2. Układ okna (z góry na dół)

| Strefa | Wysokość | Zawartość |
|---|---|---|
| Wiersz tytułowy | 36 | Ikona i nazwa aplikacji, przycisk „⋯” (menu), po prawej wskaźnik operacji w tle, koło zębate (ustawienia), przyciski okna. Cały wiersz przeciąga okno (`SetTitleBar`). |
| Dwa panele | * | Karty opisane w sekcji 3. Rozdzielacz między nimi przeciągany (`GridSplitter` z CommunityToolkit lub własny), dwuklik przywraca 50/50. |
| Pasek poleceń | 36 | Lewa część: skrócona ścieżka aktywnego panelu jako prompt (`src ›`), dalej pole poleceń w Cascadia Mono. Prawa: chip bieżącej operacji (nazwa, licznik, mini pasek postępu). |
| Pasek akcji | 40 | Siedem przycisków-pigułek F2–F8 na całą szerokość: mały `kbd` z klawiszem i czasownik („F5 Kopiuj”). F1 Pomoc nie ma przycisku (skrót nadal działa). Przytrzymanie Shift, Ctrl lub Alt podmienia etykiety na warianty (Shift+F6 Zmień nazwę, Shift+F8 Usuń trwale, Alt+F7 Szukaj). |

## 3. Panel

```
┌──────────────────────────────────────────────────────────┐  ← linia akcentu 2 px (tylko aktywny)
│ [C: ▾]  work › src › many                       [≡] [⋯]  │  nagłówek: dysk, okruszki, widok, menu panelu
│ Nazwa ▲            Rozsz.       Rozmiar     Zmieniono     │  nagłówki kolumn (tylko widok pełny)
│ ▸ ..                                                      │
│ ● 📁 many                        <KAT>   30.10.2026 07:23 │  ● = zaznaczony (kropka akcentu + tło 8 %)
│   📁 nowy                        <KAT>   30.10.2026 07:32 │
│ ┃ 📄 big              bin      3,0 GB    30.10.2026 07:18 │  ┃ = kursor (pasek akcentu 3 px + tło)
│ ● 📄 x                txt         7 B    30.10.2026 07:18 │
│                                                           │
│ 2 z 5 zaznaczone · 3,0 GB         ▮▮▮▮▮▮▯▯▯ 157 GB wolne  │  stopka panelu
└──────────────────────────────────────────────────────────┘
```

**Nagłówek.** Przycisk dysku to `DropDownButton` z literą i ikoną typu nośnika; flyout listuje dyski
z etykietą i paskiem zajętości (zastępuje rząd przycisków i Alt+F1/F2 otwiera ten sam flyout).
Ścieżka to `BreadcrumbBar`: klik w segment przechodzi wyżej, klik w puste miejsce lub Ctrl+L
zamienia ją w pole tekstowe z autouzupełnianiem. Po prawej: przełącznik widoku (skrócony / pełny / szczegóły)
i menu panelu (sortowanie, ukryte, filtr, nowa karta).

**Kolumny.** Domyślnie: Nazwa, Rozszerzenie, Rozmiar, Zmieniono. Atrybuty wyłączone, włączane w menu panelu.
Szerokości zapamiętywane w ustawieniach; przeciąganie krawędzi nagłówka (etap 3). Rozmiary w jednostkach
czytelnych (3,0 GB), pełne bajty w dymku i w stopce. Katalogi: tekst `<KAT>` w kolorze drugorzędnym.

**Wiersze.** Ikona 16 px z powłoki (jak teraz), nazwa z przycinaniem wielokropkiem. Katalogi pogrubione
`SemiBold`. Element `..` ma strzałkę w górę zamiast ikony folderu.

**Stany wiersza** (zamiast czerwonego tekstu):

| Stan | Wygląd |
|---|---|
| zwykły | brak tła |
| pod kursorem (hover) | `SubtleFillColorSecondary` |
| kursor | pasek akcentu 3 px po lewej (`AccentFillColorDefault`), tło `SubtleFillColorSecondary`, promień 4 |
| zaznaczony | kropka akcentu 6 px w kolumnie ikony, tło `AccentFillColorDefault` 8 % (warstwa z `Opacity`), nazwa w akcencie |
| zaznaczony + kursor | oba naraz |
| ukryty/systemowy | nazwa 60 % krycia |

**Stopka.** Po lewej stan zaznaczenia („2 z 5 zaznaczone · 3,0 GB” albo „5 elementów · 3,0 GB”),
po prawej zajętość dysku jako pasek 60 px i tekst. Ładowanie: `ProgressRing` 14 px zamiast paska.
Szybkie szukanie: pływający pasek nad stopką, tło akrylowe, jak teraz.

**Karty.** Nagłówek jest tak zaprojektowany, żeby w etapie 5 nad nim pojawił się pasek kart
(`TabView` w trybie kompaktowym). Przy jednej karcie pasek jest ukryty.

## 4. Operacje

- Postęp nie przykrywa okna. Chip w pasku poleceń pokazuje nazwę operacji, licznik i mini pasek.
  Klik rozwija panel postępu jako `Flyout` zakotwiczony przy chipie: oba paski, prędkość, kolejka,
  przyciski Pauza / W tle / Anuluj. Nowa operacja startuje rozwinięta; „W tle” zwija do chipa.
- Konflikty i błędy pozostają `ContentDialog` (muszą blokować).
- Dialog F5/F6/F7: `ContentDialog` z polem ścieżki docelowej i jedną linią podsumowania
  („3 pliki, 2 katalogi, 3,0 GB → D:\backup”). Przycisk główny w stylu akcentowym.

## 5. Zasoby WinUI zamiast własnych kolorów

| Rola | Zasób |
|---|---|
| tło okna | `MicaBackdrop` (fallback `SolidBackgroundFillColorBase`) |
| karta panelu | `LayerFillColorDefaultBrush` + `CardStrokeColorDefaultBrush` |
| tekst główny / drugorzędny | `TextFillColorPrimaryBrush` / `TextFillColorSecondaryBrush` |
| akcent | `AccentFillColorDefaultBrush`, tekst `AccentTextFillColorPrimaryBrush` |
| tło kursora i hover | `SubtleFillColorSecondaryBrush` / `SubtleFillColorTertiaryBrush` |
| pasek akcji | `ControlFillColorDefaultBrush`, obrys `ControlStrokeColorDefaultBrush` |
| pasek poleceń | `ControlFillColorDefaultBrush`, czcionka `Cascadia Mono` |

Motyw ciemny i jasny wychodzą z tych zasobów bez osobnej palety. Własne kolory tylko dla
wartości, których Fluent nie ma: tło zaznaczenia (akcent z `Opacity="0.08"` na osobnym `Border`).

## 6. Co z klawiatury się zmienia

Nic w działaniu. Zmieniają się tylko miejsca, w których skróty są pokazywane:
Alt+F1/F2 otwiera flyout dysku panelu, F2 otwiera menu „⋯”, Ctrl+L edytuje ścieżkę,
Ctrl+F1/F2 przełącza widok (przycisk w nagłówku panelu pokazuje stan).

## 7. Kolejność wdrożenia

Każdy krok jest osobnym commitem i da się sprawdzić zrzutem na VM (zrzut ekranu maszyny testowej).

1. **Rama okna.** Usunięcie `MenuBar`, wiersz tytułowy 36 px z „⋯” i `SetTitleBar`, menu jako `MenuFlyout`
   (te same handlery, te same `x:Uid`). Pasek akcji jako pigułki z `kbd`. Podmiana etykiet przy Shift/Ctrl/Alt
   (`PreviewKeyDown`/`KeyUp` na Root, już jest tam obsługa klawiszy).
   Stan 2026-10-30: zrobione, zbudowane i uruchomione na maszynie testowej. Do `SetTitleBar` idzie tylko pusty `DragRegion`
   między przyciskami, inaczej przycisk „⋯” nie dostaje kliknięć; `CaptionPadding` dostaje szerokość
   `AppWindow.TitleBar.RightInset / RasterizationScale`. Klik w pigułkę szuka akcji w tej samej tabeli skrótów
   co klawiatura, z aktualnym modyfikatorem. Sprawdzone na ekranie: F2 otwiera menu, przytrzymany Shift
   przełącza F6/F8 na „Zmień nazwę” / „Usuń trwale”. Pułapka: `ResourceLoader.GetString` rzuca wyjątek dla
   brakującego klucza (wywalało aplikację przy Shift), stąd `Loc.TryGet` na `ResourceManager.MainResourceMap`.
   W wąskim oknie (poniżej ok. 1000 px) etykiety pigułek są przycinane; do rozważenia krótsze warianty.
2. **Karta panelu.** `Border` z `LayerFill`, linia akcentu, nagłówek z `DropDownButton` dysku
   i `BreadcrumbBar`. `PanelViewModel.Path` rozbity na `PathSegments` (testowalne w Presentation).
   Usunięcie `DriveBar` i `HeaderBackground`.
   Stan 2026-10-30: zrobione i sprawdzone na maszynie testowej (zrzuty `docs/redesign2-*.png`). `PathSegment.Split` działa na samym
   tekście, więc ścieżki Windows testują się w WSL (5 testów). Okruszki pokazują segmenty poniżej korzenia, bo dysk
   stoi już na przycisku obok; klik w okruszek stawia kursor na katalogu, z którego się wyszło. Menu dysku to jeden
   `MenuFlyout` wypełniany przy otwarciu (mysz) i przez Alt+F1/F2. Przełącznik widoku i menu panelu (sortowanie,
   ukryte) są w nagłówku; „ukryte” idzie przez zdarzenie do okna, bo dotyczy obu paneli. Pułapka: flyout otwarty
   skrótem z Alt połyka KeyUp modyfikatora, stąd timer 100 ms, który odczytuje stan klawiszy, póki modyfikator
   jest pokazany na pasku akcji. `DriveSummary` tymczasowo w stopce jako tekst; pasek zajętości to krok 4.
3. **Wiersze i stany.** Nowy `ItemContainerStyle` z paskiem akcentu kursora, warstwa zaznaczenia,
   usunięcie `MarkBrush`. Sprawdzić na 100 tys. wpisów, czy dodatkowy `Border` w szablonie nie psuje
   płynności (pomiar jak w etapie 1). Pamiętać o `CornerRadius="0.1"` na liście.
   Stan 2026-10-30: zrobione (zrzut `docs/redesign3-row-states.png`). Kursor to pigułka zaznaczenia samego
   `ListView` (WinUI rysuje ją domyślnie), więc bez własnego szablonu kontenera. Zaznaczenie: kropka 6 px w kolumnie
   ikony, tło z `SystemAccentColor` przy kryciu 10 % (jeden buforowany pędzel), nazwa w akcencie i SemiBold.
   Katalogi SemiBold, `<DIR>` i data w kolorze drugorzędnym, ukryte/systemowe wiersze 55 % krycia
   (`FileItem.IsHidden`). Wiersz 26 px. Pomiar: 100 001 wierszy renderuje się w 33 ms (wcześniej 22–25 ms),
   dół listy przycina się poprawnie. Przy okazji prompt linii poleceń pokazuje tylko nazwę bieżącego katalogu
   („src ›”, pełna ścieżka w dymku), więc długie ścieżki nie wypychają pola poza okno.
4. **Stopka panelu** z paskiem zajętości; usunięcie `DriveSummary` z nagłówka.
   Stan 2026-10-30: zrobione (zrzut `docs/redesign4-footer.png`). Pasek 64×4 px z `Texts.DriveUsedFraction`,
   kolor trzeciorzędny, od 90 % zajętości `SystemFillColorCaution`, od 95 % `SystemFillColorCritical`.
   Tekst obok to `Texts.DriveFree` bez etykiety woluminu; etykieta została w dymku i w menu dysku. 3 testy.
5. **Pasek poleceń i chip operacji.** Przeniesienie okna postępu do `Flyout`. Uwaga z pamięci projektu:
   nie przenosić fokusu programowo, gdy otwarty jest `ContentDialog`; flyout postępu musi się zamykać
   przed otwarciem dialogu konfliktu.
   Stan 2026-10-30: zrobione (zrzuty `docs/redesign5-*.png`). Zamiast `Flyout` karta w drzewie okna
   (`Border` z `ThemeShadow` nad prawym dolnym rogiem paneli): flyout jest zamykany przez utratę fokusu,
   więc każdy dialog konfliktu gubiłby stan „w tle / na wierzchu”. Nakładka przyciemniająca zniknęła.
   Chip w pasku poleceń (tytuł, licznik, pasek 3 px) jest widoczny przez cały czas trwania operacji
   (`IsRunning`); klik przełącza kartę i tło. Kliknięcie w panel przy otwartej karcie odsyła operację do tła.
   Klawiatura na karcie bez zmian: Tab po przyciskach, Esc = w tle. Sprawdzone na kopii 50 tys. plików.
6. **Rozdzielacz paneli** i zapamiętanie proporcji w `settings.json`.
   Stan 2026-10-30: zrobione (zrzut `docs/redesign6-splitter.png`). Własny `PanelSplitter : Control` z przechwyceniem
   wskaźnika; pakiet CommunityToolkit Sizers odpadł, bo ciągnie narzędzia MSIX, które psują build, a `Thumb`
   w WinUI jest zapieczętowany. Przeciąganie przelicza szerokości gwiazdkowe obu kolumn, `MinWidth="400"` pilnuje,
   żeby kolumny panelu się mieściły; dwuklik wraca do 50/50. `AppSettings.PanelSplit` (0,2–0,8) zapisuje się
   razem z oknem, test w `MainViewModelTests`. Sprawdzone myszą na maszynie testowej: 482 → 600 px, dwuklik → 500, zapis 0,568.
   Do testów myszy powstał lokalny skrypt (mysz maszyny wirtualnej ląduje w rogu ekranu).
7. **Ustawienia gęstości** (26/22 px) i kolumn, razem z oknem ustawień z etapu 3.
   Stan 2026-10-30: zrobione bez okna ustawień (zrzut `docs/redesign7-compact-attributes.png`): przełączniki
   „Kompaktowe wiersze” i „Kolumna atrybutów” w menu panelu i w menu Pokaż, wspólne dla obu paneli jak „ukryte”,
   zapisywane w `settings.json` (`CompactRows`, `ShowAttributes`). Gęstość to podmiana `ItemContainerStyle`
   (26 → 22 px), szablony wierszy nie mają własnej wysokości. Kolumna atrybutów jest domyślnie wyłączona
   i pokazuje się tylko w panelu szerszym niż 520 px. Okno ustawień z etapu 3 ma te opcje tylko powtórzyć.

## 8. Stan wdrożenia

Wszystkie siedem kroków jest wdrożonych i sprawdzonych na maszynie testowej (2026-10-30), zrzuty `docs/redesign1-*.png` …
`docs/redesign7-*.png`. Odstępstwa od pierwotnego planu: karta postępu zamiast `Flyout` (krok 5), własny
`PanelSplitter` zamiast pakietu z CommunityToolkit (krok 6), krok 7 bez okna ustawień. Do zrobienia później:
krótsze etykiety pigułek w wąskim oknie, pasek kart nad nagłówkiem panelu w etapie 5. Tryb edycji ścieżki
(Ctrl+L, klik obok okruszków) wszedł w etapie 3 jako `AutoSuggestBox` z podpowiedziami podkatalogów.

Ryzyka: `BreadcrumbBar` w WinUI 3 nie ma trybu edycji, trzeba go nałożyć własnym `TextBox`
przełączanym `Visibility`; `GridSplitter` wymaga CommunityToolkit.WinUI.Controls.Sizers
(nowa zależność) albo 40 linii własnego kodu na `Thumb`.

## 9. Wygląd „grafit” (2026-11-05)

Na podstawie makiety użytkownika (React/Tailwind) okno przeszło z Miki i zasobów Fluent na własną paletę
w `App.xaml` (`Pc…Brush`, każda w wersji ciemnej i jasnej, przełącznik motywu działa jak dotąd).
Zrzuty: `docs/wyglad2-dark.png`, `docs/wyglad2-light.png`.

- **Akcent** to cyjan (`SystemAccentColor` i odcienie nadpisane w `App.xaml`), więc pigułka kursora, paski
  postępu i przyciski akcentowe są cyjanowe w całej aplikacji, także w oknach pobocznych.
- **Rama okna**: warstwy płaskich odcieni łupku zamiast Miki — pasek tytułu z kafelkiem „PC” i plakietką wersji,
  nowy **pasek narzędzi** (Odśwież Ctrl+R, Nowy katalog F7, Szybki podgląd Ctrl+Q, Szukaj Alt+F7, Przełącz panel Tab,
  po prawej „Aktywny: panel lewy/prawy”), ciemniejsza scena pod kartami paneli, pasek poleceń z ikoną konsoli,
  cyjanowym promptem i przyciskiem „Wykonaj”, pasek F2–F8 z cyjanowym klawiszem i cyjanowym podświetleniem.
  Przyciski systemowe okna dostają kolory z motywu (`ApplyTheme` w `MainWindow`).
- **Panel**: karta z promieniem 12; aktywna ma cyjanowy obrys i słabą poświatę, nieaktywna jest lekko przygaszona (88 %).
  Pasek kart widoczny zawsze (karta aktywna z linią akcentu, „+” nowa karta), po prawej pigułki dysków (klik = zmiana dysku).
  Wiersz nawigacji: katalog główny dysku, katalog wyżej, pole ścieżki (przycisk dysku + okruszki w Cascadia Mono),
  **pole filtra** (fragment nazwy lub maski, po 250 ms; zmienia tylko część „nazwa” filtra Ctrl+S; Esc czyści, Enter/↓ wraca do listy;
  odświeżenie listy nie zabiera mu fokusu), widok i menu panelu.
- **Wiersze**: nazwy katalogów bursztynowe, `<DIR>` i daty wyciszone, zaznaczone na cyjanowej poświacie z jasnocyjanowym tekstem
  (znaczek ✓ na ikonie zostaje), ramka fokusu kursora w akcencie zamiast białej. Stopka: stan zaznaczenia w akcencie,
  pasek zajętości jako gradient cyjan→niebieski (od 90 % nadal ostrzeżenie/błąd).
- Pędzle podawane z kodu (`Ui.Resource`) biorą słownik motywu okna (`Ui.Theme`), nie systemu; przy zmianie motywu listy są
  odczytywane ponownie, żeby wiersze się przemalowały.

Pominięte z makiety: zegar (jest na pasku zadań), ikony typów plików w kolorach (zostają ikony powłoki), plakietka „sys”
(ukryte/systemowe są przygaszone), powiadomienia typu toast. Okna poboczne (ustawienia, podgląd, szukanie, Ctrl+Q)
mają na razie tylko nowy akcent.
