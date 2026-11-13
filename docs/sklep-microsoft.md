# Plain Commander w Microsoft Store

Procedura publikacji i aktualizacji w Sklepie Microsoft (Partner Center). Teksty do listingu są w
`docs/store/listing-en.md` i `docs/store/listing-pl.md`, polityka prywatności w `site/privacy.html`.

## 1. Jednorazowo: konto i tożsamość

1. Konto deweloperskie w [Partner Center](https://partner.microsoft.com/dashboard) (program Windows, konto
   indywidualne; opłata jednorazowa).
2. **Apps and games → New product → MSIX or PWA app**, zarezerwowana nazwa **Plain Commander**.
3. **Product management → Product identity** podaje trzy wartości:
   `Package/Identity/Name`, `Package/Identity/Publisher` (postać `CN=<GUID>`) i `Package/Properties/PublisherDisplayName`.
   Wpisz je do `build/store-identity.json` (wzór: `build/store-identity.example.json`; plik jest ignorowany przez git).
   `Package.appxmanifest` zostaje z tożsamością deweloperską (`CN=PlainCommander`), więc `build\msix.ps1` i
   certyfikat deweloperski działają jak dotąd. Pakiet ze Sklepu i pakiet deweloperski to dla Windows dwie różne
   aplikacje; ustawienia z pakietu dev można przekopiować z `%LOCALAPPDATA%\Packages\PlainCommander.App_*\LocalCache\Local\PlainCommander\`.

## 2. Każde wydanie: pakiety

Na maszynie z Windows (Windows SDK z Windows App Certification Kit), w katalogu repozytorium:

    powershell -File build\msix-store.ps1 -Wack

Skrypt buduje **niepodpisane** pakiety x64 i ARM64 (Sklep podpisuje je sam) z tożsamością ze `store-identity.json`
do `build\out\store\PlainCommander-<wersja>-<platforma>-store.msix` i uruchamia na nich Windows App Certification Kit.
Wynik WACK musi być PASS (raport `.wack.xml` obok pakietu); tylko ostrzeżenia są dopuszczalne.
Wersja w manifeście musi kończyć się `.0` i być wyższa niż ostatnia przesłana do Sklepu.

## 3. Zgłoszenie (submission) w Partner Center

Zakładki od góry do dołu; w nawiasach, co wybrać dla Plain Commandera.

- **Pricing and availability:** Markets „All worldwide markets”; Visibility „Public audience”, „available and
  discoverable”; Schedule „as soon as possible” / „never”; Pricing → grupa Default: waluta USD, cena **Free**;
  Free trial i Sale pricing bez zmian; Organizational licensing domyślne.
- **Properties:** kategoria **Utilities & tools** (podkategoria File managers); Privacy policy URL → https://plain-browser.github.io/plain-commander/privacy.html (strona z `site/privacy.html`, wdrażana workflow `pages.yml`); Website i Support contact → adres repozytorium i jego Issues;
  System requirements: Windows 11 22H2 (minimum), x64 lub ARM64; Product declarations: zaznaczyć
  „This app depends on non-Microsoft drivers or NT services” **nie**, „accessibility” wedle uznania.
- **Age ratings:** kwestionariusz IARC; wszystkie odpowiedzi „nie” (narzędzie, bez treści, bez komunikacji między
  użytkownikami, bez zakupów) → ocena „Everyone / 3+”.
- **Packages:** przeciągnąć oba pliki `...-x64-store.msix` i `...-ARM64-store.msix`. Partner Center wykryje
  języki (en-US, pl-PL) i architektury. Device family: tylko Windows 10/11 desktop.
- **Store listings:** Partner Center wymaga listingu dla każdego języka z pakietu, więc **English (United States)** i
  **Polish**, teksty z `docs/store/`. Zrzuty ekranu PNG o szerokości co najmniej 1366 px (najlepiej 1920×1080),
  od 1 do 10; logo Sklepu 1:1 300×300 (można pominąć, wtedy użyje logo z pakietu).
- **Submission options:** w **Notes for certification** wkleić uzasadnienie uprawnień (poniżej). Publish „as soon
  as it passes certification” lub ręcznie.

### Notes for certification (do wklejenia)

    Plain Commander is a dual-pane file manager (full-trust desktop app, WinUI 3, .NET, self-contained).
    runFullTrust: the app manages files on local disks, network shares and removable drives, calls shell
    APIs (IFileOperation, context menus, Recycle Bin) and opens files with their default programs.
    The app has no telemetry and no account; it connects only to servers the user enters (FTP/SFTP/S3).
    Nothing to log in to; all features are available without configuration. Test data: any folder; archives
    can be created from the Files menu (Pack). Languages: English and Polish (follows the Windows language).

### Uprawnienia zastrzeżone

Pakiet ze Sklepu deklaruje tylko `runFullTrust`. `allowElevation` certyfikacja odrzuciła (2026-11-13), więc `build\msix-store.ps1`
usuwa je z kopii manifestu i dokłada plik `no-elevation.flag`, po którym aplikacja nie oferuje „Jako administrator”.
Pakiet deweloperski (GitHub) zachowuje `allowElevation`. Z „Notes for certification” usunąć akapit o `allowElevation`.

## 4. Po publikacji

- Link do Sklepu ma postać `https://apps.microsoft.com/detail/<ProductId>` (ProductId z Product identity);
  Plain Commander: Store ID `9NJMJ1VQ1X87`, https://apps.microsoft.com/detail/9NJMJ1VQ1X87, rodzina pakietu
  `PlainBrowser.PlainCommander_7m7psx7yqqmx8`. Dodać przycisk „Pobierz ze Sklepu Microsoft” na `site/index.html` i w README (sekcja Installation) jako główny
  sposób instalacji; instrukcję z certyfikatem deweloperskim zostawić jako alternatywę.
- Aktualizacja = krok 2 (wyższa wersja) i nowe zgłoszenie z nowymi pakietami i tekstem „What's new”. Teksty listingu
  i cena przenoszą się z poprzedniego zgłoszenia.
- Automatyzację (GitHub Actions z `microsoft/setup-msstore-cli` i `msstore publish`) rozważyć po kilku ręcznych
  wydaniach; wymaga Azure AD i klucza API w sekretach repozytorium.
