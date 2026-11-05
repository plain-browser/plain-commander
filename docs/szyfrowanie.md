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
- **Hasło w pamięci** jako `char[]`/`SecureString` zerowany po użyciu; klucz 32 B zerowany
  `CryptographicOperations.ZeroMemory`. Logi nigdy nie zawierają hasła ani salta.
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
