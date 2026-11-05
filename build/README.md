# Budowanie wydania

| Skrypt | Co robi |
|---|---|
| `make-icons.ps1` | rysuje ikonę (dwa panele na akcentowym kwadracie) we wszystkich rozmiarach do `src/PlainCommander.App/Assets` oraz `PlainCommander.ico` dla exe |
| `msix.ps1 [-Platform x64]` | buduje podpisany pakiet MSIX do `build/out/`; przy pierwszym użyciu tworzy samopodpisany certyfikat deweloperski `dev-cert.pfx` / `dev-cert.cer` (ignorowane przez git) |
| `install-dev.ps1` | instaluje pakiet lokalnie; zaufanie certyfikatowi deweloperskiemu wymaga jednorazowo podniesionej powłoki |

Pętla deweloperska nadal buduje wersję niepakowaną (`dotnet build`), MSIX powstaje tylko z `-p:Packaged=true`.
Do publicznego wydania certyfikat deweloperski trzeba zastąpić właściwym: `Identity Publisher` w `Package.appxmanifest`
musi odpowiadać podmiotowi certyfikatu.

Uwaga: `build/out/` i certyfikat deweloperski (`dev-cert.pfx`) nie są w repozytorium; `msix.ps1` tworzy je od nowa, jeśli ich brakuje.
