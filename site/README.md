# Strona z instalatorem

Statyczna strona (`index.html`) gotowa do publikacji na GitHub Pages lub dowolnym hostingu plików.
Pakiet (`PlainCommander-<wersja>-x64.msix`) i certyfikat deweloperski (`PlainCommander-dev.cer`) leżą w GitHub Releases;
linki na stronie wskazują `releases/latest/download/…`. Zrzuty w `img/` pochodzą z `docs/`.

Przed publicznym wydaniem: podmienić certyfikat deweloperski na wydawniczy (wtedy krok 2 instrukcji znika),
adres repozytorium jest w stopce; rozważyć plik `.appinstaller` z adresem strony, który da automatyczne aktualizacje.
