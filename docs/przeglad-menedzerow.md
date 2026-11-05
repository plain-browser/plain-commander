# Przegląd innych menedżerów plików (2026-11-05)

Porównanie z Directory Opus 13, XYplorer, Files (v4), OneCommander, Double Commander i Multi Commander oraz z tym,
o co użytkownicy proszą (wątki życzeń, listy próśb do Eksploratora Windows). Celem jest lista funkcji, których
Plain Commander jeszcze nie ma. Pominięte są rzeczy już odrzucone: podgląd PDF/Office, podgląd multimediów i kodu,
Android/WebDAV, automatyzacja i skrypty.

## Co Plain Commander już ma (dla porządku)

Dwa panele z kartami, kolejka operacji z pauzą, konflikty, kosz, praca jako administrator, archiwa przez 7-Zip
(z hasłami i szyfrowaniem), FTP/SFTP/FTPS/S3, porównanie i synchronizacja katalogów z podglądem różnic, zmiana nazw
wielu plików z cofaniem i łacinką, wyszukiwanie (treść, wyrażenia regularne, archiwa), szybki podgląd, filtr z datami,
rozmiarami i długością, widok płaski, szybkie wyszukiwanie po fragmencie, rozmiary katalogów w tle, kolumna szczegółów
(wymiary, data zdjęcia, długość, wersja), sumy kontrolne, porównanie plików, sieć lokalna, obrazy dysków, motyw
jasny i ciemny.

## Czego brakuje

| # | Funkcja | Kto ma | Co daje | Nakład |
|---|---|---|---|---|
| 1 | **Cofanie operacji na plikach** (kopiuj, przenieś, zmień nazwę, usuń do kosza, nowy katalog), wielopoziomowe | XYplorer (256 kroków), Directory Opus (lista 10 ostatnich + dziennik 1000 operacji) | najczęstsza „pomyłka jednym klawiszem” przestaje boleć; Windows nie udostępnia cofania Eksploratora, każdy program pisze własne | średni |
| 2 | **Wyszukiwarka duplikatów** (po nazwie, rozmiarze, sumie kontrolnej; opcjonalnie podobne obrazy) | Directory Opus, XYplorer | porządki na dysku, zdjęcia z kilku kopii zapasowych | średni |
| 3 | **Natychmiastowe wyszukiwanie przez Everything** (indeks NTFS, cały dysk w ułamku sekundy; także natychmiastowe rozmiary katalogów) | Directory Opus 13 | Alt+F7 na całym dysku bez czekania, gdy Everything jest zainstalowany | mały–średni |
| 4 | **Etykiety kolorów i komentarze** do plików, wyszukiwanie i filtr po nich | XYplorer, Files, OneCommander | „do zrobienia”, „ważne”, „wysłane” bez przenoszenia plików | średni |
| 5 | **Kolorowanie wierszy według reguł** (typ, wiek, rozmiar, atrybuty) | XYplorer, OneCommander (wiek pliku) | nowe pliki, duże pliki, obrazy widać od razu | mały |
| 6 | **Drzewo katalogów** (pełne albo „mini”, tylko odwiedzone ścieżki) | XYplorer, Files 4.2, Double Commander; częsta prośba | nawigacja po głębokich strukturach | średni |
| 7 | **Paleta poleceń** (Ctrl+Shift+P: wpisz nazwę polecenia) i **własne skróty klawiszowe** | Files (Omnibar), XYplorer, Directory Opus | każda funkcja dostępna bez szukania w menu; zmiana skrótów | mały (paleta), średni (skróty) |
| 8 | **Przywracanie zamkniętej karty** (Ctrl+Shift+T) i **zapisane zestawy kart** | Files, XYplorer (tabsets) | odtworzenie przypadkiem zamkniętej karty, przełączanie „projektów” | mały |
| 9 | **Kopiowanie z weryfikacją** (porównanie sum po skopiowaniu) | XYplorer | pewność przy kopiach zapasowych i kartach pamięci | mały |
| 10 | **Stan repozytorium Git** (zmienione pliki zaznaczone w panelu, gałąź w stopce) | Files | dla programistów | średni |
| 11 | **Widok kolumnowy** (Miller columns, każdy katalog w kolejnej kolumnie) | OneCommander, Files | inny sposób nawigacji; mniej pasuje do dwóch paneli | duży |
| 12 | **Bezpieczne usuwanie** (nadpisanie przed usunięciem) | XYplorer | na dyskach SSD niewiarygodne, więc wartość mała; uczciwiej pominąć | mały |

## Rekomendacja

1. **Cofanie operacji (1)** — największa korzyść na co dzień, brak u większości konkurentów poza dwoma płatnymi.
2. **Paleta poleceń i przywracanie kart (7, 8)** — mały nakład, odczuwalna wygoda; dobre na drobne wydanie.
3. **Duplikaty (2)** i **Everything (3)** — narzędzia, po które ludzie sięgają do osobnych programów.
4. **Etykiety i kolorowanie (4, 5)** — razem jako „organizacja plików”.

Pominąłbym 11 (nie pasuje do dwóch paneli) i 12 (fałszywe poczucie bezpieczeństwa na SSD).

## Źródła

- Directory Opus 13: [nowości](https://chaoses-ib.gitbook.io/directory-opus/manual/release_history/opus13/page1),
  [cofanie operacji](https://chaoses-ib.gitbook.io/directory-opus/manual/file_operations/tracking_and_undoing_file_operations),
  [omówienie wydania](https://betanews.com/2024/01/24/directory-opus-13-finally-released-unveils-facelift-and-dozens-of-further-improvements)
- XYplorer: [lista funkcji](https://www.xyplorer.com/features.php), [cofanie wielopoziomowe](https://www.xyplorer.com/tour.php?page=undo)
- Files: [strona projektu](https://files.community/), [wydanie 4.0](https://files.community/blog/posts/v4-0-0)
- OneCommander: [strona](https://onecommander.com/index.php), [recenzja](https://maketecheasier.com/onecommander-file-explorer-alternative)
- Double Commander: [synchronizacja katalogów](https://doublecmd.github.io/doc/en/syncdirs.html), [Wikipedia](https://wikipedia.com/wiki/Double_Commander)
- Multi Commander: [wątek życzeń użytkowników](https://forum.multicommander.com/forum/index.php/topic,503.0.html)
- Prośby do Eksploratora Windows: [Neowin, top 10 z Feedback Hub](https://www.neowin.net/news/top-10-features-and-changes-windows-11-users-want-for-file-explorer/)
