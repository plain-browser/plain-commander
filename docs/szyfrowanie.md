# Szyfrowanie plików — format `.pce`

Stan: wdrożone 2026-11-05 na gałęzi `etap-22` (szczegóły i wyniki w PLAN.md). Poniżej projekt, z którego
wyszło wdrożenie; różnice względem niego: nagłówek ma 42 B (bez flagi „nazwa w środku” — nazwa jest tam zawsze),
flaga ostatniego bloku to bit 31 licznika w nonce, blok 0 to osobny blok metadanych, TAR czytany własnym kodem,
kompresja to zstd (bit 2 flag; Deflate z bitu 1 tylko czytany) w trybie auto: próbka pierwszego megabajta musi
zmaleć poniżej 95 %, inaczej plik idzie bez kompresji.

## Dlaczego nie zostać przy 7z/zip

Etap 21 szyfruje przez spakowanie do 7z (AES‑256) lub zip (AES‑256). Wady:

- **Wolno.** 7z kompresuje LZMA przed szyfrowaniem: 10–30 MB/s na dużych plikach (filmy, obrazy dysków, już
  skompresowane dane). Sam AES na AES‑NI robi 2–5 GB/s.
- **Stara kryptografia.** AES‑CBC bez uwierzytelnienia całego pliku (7z) albo z HMAC‑SHA1 (zip AES).
  Klucz z hasła: SHA‑256 powtarzane 2^19 razy (7z) lub PBKDF2 1000 iteracji (zip) — oba odporne tylko na
  słabego napastnika z GPU.
- **Zip zdradza nazwy plików i rozmiary**, 7z z ukrytymi nazwami nie strumieniuje.
- Nie da się dodać postępu per bajt ani wznowić anulowanego szyfrowania bez zostawienia pół‑archiwum.

## Format `.pce` (Plain Commander Encrypted)

Jeden plik → jeden `.pce` obok (`umowa.pdf` → `umowa.pdf.pce`). Folder → najpierw TAR bez kompresji
(SharpCompress już jest w projekcie), potem `.pce`. Opcjonalnie kompresja zstd/deflate tylko gdy użytkownik
zaznaczy „kompresuj”.

```
nagłówek (jawny, uwierzytelniony jako AAD)
  "PCE1"                      4 B   magia + wersja
  flags                       1 B   bit0: nazwa pliku w środku, bit1: TAR, bit2: deflate
                                    (wdrożone: bit0 TAR, bit1 deflate, bit2 zstd, bit3 hasło w NFC)
  kdf                         1 B   0 = Argon2id
  argon2: m (KiB) u32, t u8, p u8
  salt                       16 B   losowa
  chunk size                  4 B   domyślnie 1 MiB
  stream nonce prefix         8 B   losowy

ciało: ciąg bloków
  dla bloku i (od 0):  nonce = prefix(8 B) ‖ i (u32 BE) ‖ flaga 0/1 "ostatni"  → 12 B dla AES‑GCM
  AES‑256‑GCM(klucz, nonce, AAD = nagłówek, jawny = do 1 MiB) → szyfrogram + tag 16 B
  blok 0 zawiera prefiks z metadanymi: oryginalna nazwa (UTF‑8), rozmiar u64, mtime u64
```

### Dlaczego tak

- **AES‑256‑GCM** z `System.Security.Cryptography.AesGcm` — w .NET 8/9 na Windows x64 sprzętowy (AES‑NI +
  PCLMUL), bez dodatkowych pakietów. Zero kompromisów z FIPS. Na maszynie bez AES‑NI (rzadkość) fallback
  `ChaCha20Poly1305` (też wbudowany, bit w `kdf`/`cipher`).
- **Bloki po 1 MiB** z licznikiem w nonce i flagą „ostatni”: strumieniowanie z pamięcią stałą, postęp co
  blok, wykrycie obcięcia pliku, podmiany kolejności bloków i doklejenia (konstrukcja jak w libsodium
  `secretstream` i formacie `age`). Jeden nonce nigdy się nie powtarza w strumieniu: prefix 64 bity losowy
  na plik + licznik.
- **Argon2id** (pakiet `Konscious.Security.Cryptography.Argon2`, czysty C#, MIT): domyślnie m = 64 MiB,
  t = 3, p = 4 — ok. 0,3 s na i5, a dla napastnika z GPU koszt pamięciowy zabija brute‑force. Parametry
  zapisane w nagłówku, więc można je później podnieść bez zmiany formatu. Alternatywa bez zależności:
  `Rfc2898DeriveBytes.Pbkdf2` SHA‑512 600 000 iteracji — słabsza, ale dopuszczalna jako `kdf = 1`.
- **Nazwa pliku ukryta** (opcjonalnie): wynik to `losowe‑16‑znaków.pce`, prawdziwa nazwa w bloku 0.
  Przy odszyfrowaniu wraca oryginał.
- **Kompresji nie ma domyślnie** — to ona zjada czas w 7z. Włączana świadomie.

### Wydajność (prognoza, maszyna testowa)

| operacja                 | 7z AES (etap 21) | `.pce`           |
|--------------------------|------------------|------------------|
| plik 4 GB, już spakowany | ~4–6 min         | ~5–10 s (dysk)   |
| 10 000 małych plików     | ~40 s            | ~8 s (TAR + GCM) |
| wyprowadzenie klucza     | ~0,1 s           | ~0,3 s (Argon2)  |

Granicą jest dysk, nie szyfr.

## W aplikacji

- **Pliki → Szyfruj (Ctrl+Alt+E)**: dialog z hasłem ×2 i wskaźnikiem siły (entropia zxcvbn‑lite, własna
  mała implementacja), przełączniki: „ukryj nazwę”, „kompresuj”, „usuń oryginały po zakończeniu”.
  Format 7z/zip zostaje w menu jako „Szyfruj do archiwum…” dla zgodności z innymi programami.
- **Odszyfruj**: Enter na `.pce` pyta o hasło i odszyfrowuje obok; Ctrl+PgDn na `.pce` otwiera jak
  archiwum (TAR w środku) w trybie tylko do odczytu — odszyfrowany do pliku tymczasowego w `%TEMP%`,
  kasowanego przy zamknięciu zakładki.
- **Błędne hasło** wykrywane już na bloku 0 (tag GCM), bez czytania całego pliku.
- **Ctrl+Z**: operacja dopisuje do `UndoJournal` (katalog `Undo/` z bieżącej gałęzi) utworzony `.pce` i
  ewentualnie skasowane oryginały (te z kosza).
- **Normalizacja hasła (od 1.14.0).** To samo widoczne hasło bywa różnymi bajtami: wpisane na Windows jest
  w Unicode NFC („ż” = U+017C), wklejone z macOS bywa w NFD („z” + U+0307). Dlatego:
  - nowe pliki mają w nagłówku **bit 3 flag (`NfcPassword`)** i klucz liczony z hasła w NFC; przy odczycie
    takiego pliku wpisane hasło zawsze jest najpierw sprowadzane do NFC (jedno wyprowadzenie klucza);
  - pliki bez tej flagi (zapisane przez 1.13.x, klucz z bajtów hasła takich, jak było wpisane) próbują po kolei:
    hasło dokładnie jak wpisane, jego NFC, jego NFD — każdą formę tylko wtedy, gdy daje inne bajty UTF‑8 niż
    poprzednie (hasło ASCII = jedna próba, typowe hasło z Windows = dwie). Dopiero gdy blok 0 nie przejdzie
    uwierzytelnienia z żadną formą, zgłaszane jest złe hasło;
  - tekstu z samotnym surogatem nie da się znormalizować — wtedy dana forma to hasło jak wpisane (bez wyjątku);
  - flaga leży w nagłówku, a cały nagłówek jest AAD każdego bloku, więc jej przestawienie kończy się błędem
    uwierzytelnienia bloku 0 (zgłaszanym jako złe hasło), jak przy każdej innej zmianie nagłówka;
  - **starsze wersje (1.13.x) a nowy plik:** czytnik nie sprawdza nieznanych bitów flag, więc bit 3 pomija
    i liczy klucz z hasła jak wpisane. Hasło wpisane na Windows jest już w NFC, więc taki plik zwykle się
    otworzy; hasło w NFD da w starej wersji „złe hasło” (bez szkody dla pliku).
- **Hasło w pamięci** jako `char[]`/`SecureString` zerowany po użyciu; klucz 32 B zerowany
  `CryptographicOperations.ZeroMemory`; dotyczy to też bajtów i bufora `char[]` każdej formy znormalizowanej
  oraz kluczy z form, które nie pasowały. Logi nigdy nie zawierają hasła ani salta.
- **Anulowanie** usuwa niedokończony `.pce.part` (zapis pod nazwą tymczasową, rename po ostatnim bloku).

## Warstwy kodu

```
PlainCommander.Core/Crypto/
  PceHeader.cs          – zapis/odczyt nagłówka, wersjonowanie
  PceStream.cs          – Stream do zapisu/odczytu blokami (AesGcm / ChaCha20Poly1305)
  PasswordKdf.cs        – Argon2id / PBKDF2 → 32 B
  PceWriter.cs, PceReader.cs – plik/folder ↔ .pce, postęp, anulowanie
PlainCommander.Core/Operations/
  EncryptOperation.cs   – rozszerzyć o tryb .pce (dziś tylko 7z/zip)
  DecryptOperation.cs   – nowa
PlainCommander.Core/Archives/
  PceArchive.cs         – adapter IArchive dla Ctrl+PgDn (TAR w środku)
```

Testy: wektory testowe AES‑GCM z NIST, round‑trip losowych danych 0 B / 1 B / 1 MiB / 1 MiB + 1 /
3 GB (tylko na VM), obcięty plik → błąd, podmieniony blok → błąd, złe hasło → błąd na bloku 0.

## Decyzje (przyjęte 2026-11-05)

1. Argon2id z pakietu NuGet `Konscious.Security.Cryptography.Argon2` (PBKDF2‑SHA512 jako `kdf = 1` w formacie).
2. Rozszerzenie `.pce`.
3. 7z/zip zostają w menu jako drugi i trzeci format; `.pce` jest domyślny.

## Zmierzone (maszyna testowa, VM 8 vCPU)

| operacja                          | czas   | przepustowość |
|-----------------------------------|--------|---------------|
| szyfrowanie 1 GiB (z Argon2)      | 1,64 s | ≈620 MiB/s    |
| odszyfrowanie 1 GiB               | 1,52 s | ≈670 MiB/s    |
| wyprowadzenie klucza (Argon2id)   | 0,07 s |               |
| 254 MiB tekstu, zstd (auto)       | 0,64 s | ≈400 MiB/s, wynik 1 % |

## Nowy układ `.pce`: kontener z indeksem (projekt 2026-11-19; rdzeń w `Core/Crypto` wdrożony, aplikacja jeszcze nie)

Plan wdrożenia: PLAN.md, etap 23. Nazwa i rozszerzenie zostają: to dalej „plik `.pce`”. Wersję rozróżniają tylko
pierwsze 4 bajty pliku (`PCE1` — układ opisany wyżej, `PCE2` — ten), których użytkownik nie widzi; „PCE2” nie pojawia
się w interfejsie.

### Po co

Układ `PCE1` to jeden ciągły strumień (TAR pocięty na bloki, licznik bloku w nonce, flaga „ostatni”). Nie da się z niego
niczego wyjąć ani usunąć bez odszyfrowania i zaszyfrowania całości, a otwarcie zawartości (Enter, Ctrl+PgDn)
rozszyfrowuje cały kontener do `%TEMP%`. Cel nowego układu: **usunięcie pliku zmniejsza kontener o jego rozmiar,
kosztem proporcjonalnym do usuwanego pliku, nie do kontenera** — a przy okazji lista bez odszyfrowywania treści,
wyjęcie jednego pliku, dopisywanie i zmiana hasła bez szyfrowania od nowa.

### Układ pliku

```
nagłówek (jawny, 94 B)
  "PCE2"                      4 B
  flags                       1 B   bit3: hasło w NFC (jak dziś); reszta zarezerwowana, nieznany bit = błąd
  kdf, cipher                 2 B   jak w PCE1 (Argon2id / PBKDF2, AES‑256‑GCM / ChaCha20‑Poly1305)
  parametry KDF, salt 16 B
  zawinięty klucz główny            nonce 12 B ‖ AEAD(klucz z hasła, klucz główny 32 B) ‖ tag 16 B,
                                    AAD = wcześniejsze bajty nagłówka

obszar danych (zawsze zwarty: po zakończonej operacji nie ma w nim wolnego miejsca)
  bloki wpisów: AEAD(klucz wpisu, nonce = numer bloku we wpisie u64 ‖ flaga „ostatni”, do 1 MiB) + tag 16 B
  bloki jednego wpisu tworzą jego strumień; strumień leży w jednym lub kilku fragmentach (offset, długość)

indeks (zaszyfrowany, jeden, za danymi)
  AEAD(klucz indeksu, AAD = stopka), treść dopełniona do 4 KiB:
  dla każdego wpisu: ścieżka względna (UTF‑8), typ (plik/katalog), rozmiar, mtime, atrybuty,
                     klucz wpisu 32 B (losowy), kompresja (brak/zstd), lista fragmentów

kopia nagłówka (94 B, między indeksem a stopką)
  te same bajty co nagłówek; używana, gdy pierwszy nagłówek nie otwiera pliku

stopka (48 B, ostatnie bajty pliku)
  offset i długość indeksu, numer generacji u64, salt indeksu 16 B, "PCE2END\0"
```

- **Klucz główny** jest losowy, a hasło tylko go zawija. Nagłówek nie jest AAD indeksu — inaczej zmiana hasła
  wymagałaby przepisania indeksu pod tym samym kluczem — a jego pola chroni tag zawiniętego klucza.
- **Druga kopia nagłówka** (z zawiniętym kluczem) leży przed stopką i jest zapisywana razem z indeksem. Zmiana hasła:
  najpierw nowy nagłówek jako kopia (zwykły zapis indeksu, flush), dopiero potem nadpisanie nagłówka na początku pliku.
  Awaria pomiędzy zostawia plik, który otwiera stare hasło (pierwszy nagłówek) i nowe (kopia); nagłówek urwany
  w połowie zapisu jest przy otwarciu do zapisu odtwarzany z kopii. Otwarcie próbuje kopii tylko wtedy, gdy pierwszy
  nagłówek nie otworzył pliku i kopia ma inne bajty, więc zwykłe złe hasło kosztuje jedno wyprowadzenie klucza.
  Uszkodzenie nagłówka albo kopii (jednego z dwóch) nie odbiera dostępu do pliku.
- **Klucz wpisu** jest losowy dla każdego wpisu i leży w indeksie. Dzięki temu blok wpisu nie zależy od położenia
  w pliku ani od innych wpisów — szyfrogram można przenosić zwykłym kopiowaniem bajtów. Licznik w nonce nigdy się nie
  powtórzy pod tym samym kluczem (wpis zapisany ponownie dostaje nowy klucz).
- **Klucz indeksu** = HKDF‑SHA256(klucz główny, salt indeksu ze stopki). Każdy zapis indeksu losuje nowy salt, więc
  indeks nigdy nie jest szyfrowany dwa razy tym samym kluczem.
- **Katalog** to wpisy ze ścieżkami względnymi, bez TAR. Pusty katalog ma własny wpis bez danych.
- **Kompresja** zstd jak dziś (tryb auto z próbką pierwszego megabajta), ale osobno dla każdego wpisu.
- **Złe hasło** wychodzi przy rozwijaniu klucza głównego (tag w nagłówku), bez czytania reszty.

### Usuwanie wpisu

Dziura po wpisie ma rozmiar H. Trzy kroki, po każdym flush (`FlushFileBuffers`):

1. **Dopisz na końcu nowy indeks bez usuwanego wpisu** (i nową stopkę). Wpis znika logicznie.
2. **Przenieś ostatnie H bajtów obszaru danych w dziurę** i dopisz indeks z poprawionymi fragmentami. Gdy za dziurą
   jest mniej niż H bajtów, wystarczy zsunąć ogon; gdy wpis był ostatni, nic się nie przenosi.
3. **Zapisz indeks tuż za danymi i obetnij plik** (`SetEndOfFile`).

Koszt: najwyżej H bajtów kopiowania (bez kryptografii) i trzy zapisy indeksu, niezależnie od rozmiaru kontenera.
Usuwanie kilku wpisów naraz to te same trzy kroki z kilkoma dziurami.

Przed każdym zapisem indeksu dane, do których się odwołuje, są już na dysku (flush), a zapis nigdy nie trafia w bajty,
które wskazuje ostatni zatwierdzony indeks. Po każdym kroku plik jest poprawny, bo czytnik bierze stopkę z końca
pliku, a ona wskazuje indeks zgodny z danymi. Gdy koniec pliku nie jest stopką (przerwane dopisywanie), czytnik szuka
wstecz ostatniej stopki, której indeks się uwierzytelnia i kończy dokładnie przed nią; otwarcie do zapisu obcina
resztę. Przerwanie (awaria, zanik zasilania) zostawia co najwyżej wolne miejsce: dziurę albo stary indeks przed
nowym. Wolne miejsce widać z indeksu (luki między fragmentami), czytnik je toleruje, a następna operacja zapisu je
domyka. Osobny dziennik nie jest potrzebny.

Dopisanie wpisu: bloki za stopką (stary indeks zostaje nietknięty), nowy indeks na końcu, po czym miejsce po starym
indeksie jest dziurą jak każda inna (kroki 2 i 3). Nieudane albo anulowane dopisywanie obcina plik do starej stopki.
Wyjątek: pusty kontener (świeżo utworzony albo po usunięciu wszystkiego) dostaje dane od razu za nagłówkiem, w miejscu
pustego indeksu, żeby nowy plik powstawał w jednym kawałku — awaria w tym momencie zostawia plik bez indeksu, ale
i bez treści do stracenia.

Gdy indeks rośnie (dłuższa nazwa, więcej fragmentów) i nie mieści się między danymi a zatwierdzonym indeksem, krok 3
najpierw dopisuje jeszcze jedną kopię indeksu na końcu; wtedy miejsce już wystarcza.

### Bezpieczeństwo

- **Co widać bez hasła:** łączny rozmiar pliku i parametry KDF. Nazwy, rozmiary, daty i liczba wpisów są w indeksie;
  dopełnienie do 4 KiB zaciera dokładną liczbę.
- **Integralność:** indeks uwierzytelnia klucze, fragmenty i rozmiary wpisów; bloki wykrywają podmianę, zmianę
  kolejności i obcięcie wpisu (licznik i flaga „ostatni”), jak w PCE1. Stopka i nagłówek są AAD indeksu. Plik bez
  stopki na końcu jest odrzucany.
- **Usunięte dane:** klucz wpisu znika z indeksu, a szyfrogram jest nadpisywany przeniesionymi danymi albo obcinany.
  Na SSD stare bloki fizyczne mogą przetrwać — bez hasła są bezużyteczne.
- **Czego układ nie wykrywa:** podmiany całego pliku na jego starszą kopię (wymagałoby stanu poza plikiem).
- **Zapis w miejscu:** nowy kontener nadal powstaje jako `.part` z podmianą na końcu, ale usuwanie i dopisywanie
  zmieniają istniejący plik. Bezpieczeństwo przy awarii opiera się na kolejności zapisów opisanej wyżej.

### Porównanie

| operacja                   | PCE1                               | PCE2                                    |
|----------------------------|------------------------------------|-----------------------------------------|
| usunięcie pliku            | przepakowanie całości              | kopiowanie ≤ rozmiaru usuwanego pliku   |
| lista zawartości           | odszyfrowanie całości do `%TEMP%`  | odczyt nagłówka, stopki i indeksu       |
| wyjęcie jednego pliku      | odszyfrowanie całości              | odczyt tylko jego bloków                |
| dopisanie pliku            | przepakowanie całości              | dopisanie bloków i indeksu              |
| zmiana hasła               | szyfrowanie od nowa                | przepisanie nagłówka                    |
| narzut                     | 42 B + 20 B na blok                | 236 B (nagłówek, kopia, stopka) + 16 B na blok + indeks (ok. 100 B na wpis, dopełniony do 4 KiB) |

### Ograniczenia

- Indeks jest przepisywany w całości przy każdej zmianie: 10 000 wpisów to ok. 1 MB, milion — ok. 100 MB. Powyżej
  kilkuset tysięcy wpisów potrzebny byłby indeks dzielony; poza zakresem.
- Po wielu usunięciach wpisy składają się z kilku fragmentów. Rozmiaru to nie zmienia; odczyt robi więcej skoków.
- Wersje do 1.14.x nie otworzą nowego pliku: zgłoszą „Not a Plain Commander encrypted file” (sprawdzają magię `PCE1`).
- Kontener na dysku synchronizowanym (OneDrive) po zmianie w miejscu jest wysyłany ponownie tak samo jak po
  przepakowaniu — zysk dotyczy czasu i zapisu lokalnego.

### Zgodność

- Pliki `PCE1` są czytane bez zmian (Enter, Ctrl+PgDn, Odszyfruj) i pozostają tylko do odczytu; próba zmiany w panelu
  kończy się komunikatem, a nie cichą zmianą tymczasowej kopii.
- Kontener w panelu jest archiwum (`PceArchive`): lista z indeksu, odczyt pojedynczych wpisów, F8/F7/Shift+F6/F5 do
  środka zmieniają plik w miejscu. Nic nie jest odszyfrowywane na dysk do przeglądania. Hasło siedzi w pamięci sesji
  (`ArchivePasswords`), jak dla zaszyfrowanych zip/7z.
- Jeden piszący, wielu czytających: czytnik otwarty w trakcie zmiany widzi ostatni zatwierdzony indeks.
- Nowe pliki są zawsze zapisywane w nowym układzie (od kroku 5 etapu 23: `PceFile.Encrypt`, czyli Zaszyfruj).
- `PceFile` (Peek, Decrypt, CopyContent, CopyContentAsTar) rozpoznaje układ po magii. Kontener z jednym plikiem to
  „plik”; z jedną nazwą najwyższego poziomu — „folder” o tej nazwie, odszyfrowywany jako jego zawartość; z kilkoma —
  „folder” nazwany jak plik `.pce` bez rozszerzenia. Kompresja jest dobierana osobno dla każdego pliku.
- `PceFile.Upgrade` przepisuje plik `PCE1` na kontener (strumieniowo, bez rozpakowywania na dysk; `.part` i podmiana),
  `PceFile.ChangePassword` zmienia hasło w obu układach (stary najpierw przepisuje).
- Usunięcie lub dopisanie w pliku `PCE1` wymaga jednorazowego przepisania go na nowy układ (za zgodą użytkownika,
  przez `.part` i podmianę); kolejne zmiany idą już w miejscu.

### Zmierzone (rdzeń, WSL na maszynie deweloperskiej, 2026-11-19)

| operacja                                              | czas   |
|-------------------------------------------------------|--------|
| utworzenie kontenera 2058 MiB (z Argon2)              | 2,90 s (≈710 MiB/s) |
| otwarcie: klucz i indeks                              | 0,23 s |
| usunięcie 10 MiB ze środka 2058 MiB (z otwarciem)     | 0,11 s, plik mniejszy o 10 240 KiB |
| otwarcie i odczyt ostatniego bajtu wpisu 1 GiB        | 0,05 s |
| 10 000 małych plików: utworzenie / lista / usunięcie co drugiego | 3,75 s / 0,06 s / 0,41 s |

### Zmierzone na VM (Windows 11, 8 vCPU, Release, 2026-11-19)

| operacja                                              | czas   |
|-------------------------------------------------------|--------|
| utworzenie kontenera 10 250 MiB (z Argon2, ze zrzutem na dysk) | 20,3 s (≈506 MiB/s) |
| otwarcie: klucz i indeks                              | 0,09 s |
| usunięcie 10 MiB ze środka 10 250 MiB (z otwarciem)   | 0,13 s, plik mniejszy o 10 240 KiB |
| otwarcie i odczyt ostatniego bajtu wpisu 5 GiB        | 0,09 s |
| 10 000 małych plików: utworzenie / lista / usunięcie co drugiego | 13,3 s / 0,10 s / 0,64 s |

Oba układy obok siebie na tym samym pliku 1 GiB, po trzy przebiegi (`Decrypting_a_container_keeps_pace_with_the_first_layout`):

| operacja                              | PCE1                    | kontener                |
|---------------------------------------|-------------------------|-------------------------|
| szyfrowanie                           | 1,19 / 1,37 / 1,35 s    | 2,36 / 2,27 / 2,36 s    |
| odszyfrowanie donikąd (odczyt i AES)  | 0,70 / 0,72 / 0,69 s    | 0,55 / 0,41 / 0,44 s    |
| odszyfrowanie do pliku                | 1,41 / 1,59 / 1,55 s    | 1,00 / 1,07 / 1,06 s    |

Szyfrowanie jest o ok. 1 s na GiB wolniejsze, bo kontener przed zapisaniem indeksu zrzuca dane na dysk
(`FlushFileBuffers`); PCE1 zostawiał je w buforze systemu, więc jego czas nie obejmował zapisu na nośnik.
Odszyfrowanie kontenera jest szybsze. Liczba 2,40 s z pierwszego pomiaru (`One_gigabyte_runs_at_disk_speed`) nie
była kosztem odczytu: ten test odszyfrowuje od razu po zapisaniu 2 GiB, gdy system jeszcze zrzuca bufor.

Usunięcie wymaga miejsca na jeden indeks (od ok. 4,3 KiB): na dysku bez ani jednego wolnego klastra kończy się błędem
i zostawia kontener nietknięty. Proces ubity w dowolnym momencie usuwania zostawia plik, który się otwiera; nieoddane
miejsce odzyskuje następne otwarcie do zapisu.

## `.pce` na S3: kontener rozłożony na obiekty (projekt 2026-11-19, niewdrożony)

Plan wdrożenia: PLAN.md, etap 24. Dla użytkownika to dalej „plik `.pce`” w panelu S3; rozłożenie na obiekty jest
szczegółem przechowywania.

### Po co

Obiekt w S3 jest niezmienny: nie ma zapisu pod offsetem ani obcięcia, więc zmiany w miejscu, na których stoi
kontener w pliku, tam nie istnieją. Kontener trzymany jako jeden obiekt da się czytać (zakres bajtów: stopka, indeks,
bloki wpisu), ale każda zmiana oznacza złożenie całego obiektu od nowa. W S3 tanie jest dopisanie i usunięcie
obiektu, a drogie jego przerobienie — dlatego to, co plik zmienia w miejscu, staje się osobnym, niezmiennym obiektem.
Cel: **usunięcie pliku i zmiana hasła kosztują jedno małe żądanie, a wysyłanie i pobieranie idą równolegle.**

### Układ

Kontener to prefiks `nazwa.pce/` w kubełku:

```
nazwa.pce/head                      nagłówek kontenera (94 B, te same bajty co w pliku; flaga „rozłożony”)
nazwa.pce/index/<odwrócona gen.>    indeks ‖ kopia nagłówka ‖ stopka — ten sam zapis co koniec pliku PCE2
nazwa.pce/data/<losowy id 128 b>    strumień jednego dużego wpisu albo paczka strumieni małych wpisów
```

- **`head`** zmienia się tylko przy zmianie hasła (jeden PUT 94 bajtów; PUT w S3 jest atomowy, więc nie ma nagłówka
  urwanego w połowie). Kopia w każdym indeksie zostaje jako zapas, gdyby `head` zniknął.
- **Indeks** jest niezmienny: każda zmiana dopisuje nowy obiekt z generacją o jeden większą. Nazwa to 16 cyfr
  szesnastkowych z `2^64 − 1 − generacja`, więc najnowszy indeks jest pierwszy na liście i wystarcza
  `LIST index/` z `max-keys=1`. Fragment wpisu wskazuje (id obiektu danych, offset, długość) zamiast (offset, długość).
- **Obiekty danych** nie zmieniają się nigdy: powstają i znikają. Wpis o rozmiarze od 8 MiB ma własny obiekt; mniejsze
  trafiają do paczek po ok. 32 MiB. Nazwy są losowe, więc z listy kubełka nie widać nazw plików ani katalogów.
- **Strumień wpisu jest bajt w bajt taki sam jak w pliku** (bloki AEAD pod kluczem wpisu, nonce = numer bloku
  i flaga „ostatni”). Przeniesienie między plikiem `.pce` a formą rozłożoną to kopiowanie strumieni i nowy indeks,
  bez szyfrowania od nowa.

### Operacje

| operacja             | żądania                                                                              |
|----------------------|---------------------------------------------------------------------------------------|
| otwarcie             | `LIST index/` (1 klucz), GET `head`, GET indeksu                                      |
| odczyt wpisu         | GET z zakresem, porcjami po 8 MiB, kilka naraz                                        |
| dopisanie            | obiekty danych (duże wieloczęściowo), na końcu PUT nowego indeksu                     |
| usunięcie dużego     | PUT nowego indeksu, potem `DeleteObject`                                              |
| usunięcie małego     | PUT nowego indeksu; paczka przepakowywana, gdy martwe dane przekroczą połowę          |
| zmiana nazwy         | PUT nowego indeksu                                                                    |
| zmiana hasła         | PUT `head`                                                                            |

- **Zatwierdzenie** to PUT indeksu z `If-None-Match: *`: obiekt o tej generacji powstaje albo nie. Drugi piszący
  dostaje 412, czyta świeży indeks i ponawia swoją zmianę na nim. Kolejność jak w pliku: najpierw dane, potem indeks,
  który się do nich odwołuje; usuwanie obiektów dopiero po zatwierdzeniu indeksu, który ich już nie wskazuje.
- **Przerwana operacja** zostawia tylko obiekty, do których nie prowadzi żaden indeks. Sprzątanie przy otwarciu do
  zapisu: obiekty danych spoza najnowszego indeksu starsze niż doba (młodszych może właśnie używać inny piszący),
  indeksy starsze niż dwa ostatnie, przerwane wysyłania wieloczęściowe.
- **Czytnik otwarty w trakcie zmiany** widzi indeks, który przeczytał; gdy jego obiekt danych zniknie, dostaje błąd
  „kontener się zmienił”, jak w pliku.

### Szybkość

- Bloki nie zależą od siebie (nonce to numer bloku), więc duży plik jest cięty na części wieloczęściowego wysyłania
  po 16 MiB (większe, gdy plik przekracza 10 000 części), szyfrowane i wysyłane po 8 naraz. AES‑GCM robi ponad
  1 GiB/s na rdzeń; ogranicza łącze.
- Pobieranie: zakresy po 8 MiB w kilku strumieniach, bloki odszyfrowywane w kolejności. F3 i wyjęcie fragmentu
  pobierają tylko potrzebne bloki.
- Małe pliki w paczkach: 10 000 plików to kilkanaście PUT‑ów, nie 10 000 (czas i opłaty za żądania).
- Zrzut na dysk i trzy zapisy indeksu z pliku nie mają tu odpowiednika: jedna zmiana to jeden PUT indeksu.

### Bezpieczeństwo

Prymitywy bez zmian (Argon2id, AES‑256‑GCM / ChaCha20‑Poly1305, losowy klucz każdego wpisu, klucz indeksu z HKDF).

- **Co widzi dostawca:** liczbę i rozmiary obiektów danych (czyli rozmiary dużych plików z dokładnością do bloku),
  chwile zmian i to, które zakresy są czytane. Nazw, dat, struktury katalogów i rozmiarów małych plików nie widzi.
- **Podmiana obiektu danych na inny** kończy się błędem uwierzytelnienia: każdy wpis ma własny klucz, a indeks wiąże
  wpis z obiektem, offsetem i długością. Osobne wiązanie id obiektu w blokach nie jest potrzebne (i zepsułoby
  przenoszenie strumieni między plikiem a S3).
- **Cofnięcie do starszego stanu** (ktoś z dostępem do kubełka usuwa najnowsze indeksy): aplikacja zapamiętuje
  najwyższą widzianą generację każdego kontenera i ostrzega, gdy serwer pokazuje niższą. Bez stanu po stronie klienta
  tego się nie wykryje; na innym komputerze chroni dopiero wersjonowanie albo Object Lock w kubełku.
- **Integralność w drodze:** suma CRC32C każdej części przy wysyłaniu.

### Ograniczenia

- Warunkowy zapis (`If-None-Match`) mają AWS (od 2024) i część serwerów zgodnych z S3. Gdzie go brak, kontener
  działa z jednym piszącym, a aplikacja mówi to przy otwarciu do zapisu. Do sprawdzenia na RustFS w kroku 3.
- Klasy archiwalne (Glacier) nie dają odczytu zakresami bez przywrócenia obiektu.
- Miejsce po małych plikach wraca dopiero przy przepakowaniu paczki; po dużych od razu.
- Indeks jest przepisywany w całości przy każdej zmianie, jak w pliku (10 000 wpisów ≈ 1 MB na żądanie).

### Odrzucone

- **Jeden obiekt składany przez `UploadPartCopy`**: każda zmiana kopiuje cały obiekt (po stronie serwera, ale
  proporcjonalnie do rozmiaru kontenera), części muszą mieć co najmniej 5 MiB, a małe wpisy i tak trzeba pobrać
  i wysłać ponownie.
- **Jeden obiekt na każdy plik**: 10 000 małych plików to 10 000 żądań i tyle samo rozmiarów widocznych dla dostawcy.
