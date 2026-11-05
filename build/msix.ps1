# Builds the signed MSIX. Run on Windows from the repo root:  powershell -File build\msix.ps1 [-Platform x64|ARM64]
# The dev certificate (CN=PlainCommander, self-signed) is created on first use and kept in build\dev-cert.pfx (ignored by git).
param([string]$Platform = 'x64', [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$pfx = "$PSScriptRoot\dev-cert.pfx"; $cer = "$PSScriptRoot\dev-cert.cer"; $password = 'plain-commander-dev'

if (-not (Test-Path $pfx)) {
  $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=PlainCommander' -KeyUsage DigitalSignature -FriendlyName 'Plain Commander dev' `
    -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(5)
  Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force) | Out-Null
  Export-Certificate -Cert $cert -FilePath $cer | Out-Null
  Write-Host "Created dev certificate $pfx (thumbprint $($cert.Thumbprint))"
}

# The MSIX build task cannot use a password-protected pfx (APPX0105), so the package is signed with signtool afterwards.
dotnet build "$root\src\PlainCommander.App" -c $Configuration -p:Platform=$Platform -p:Packaged=true `
  -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$msix = Get-ChildItem "$PSScriptRoot\out" -Recurse -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
& $signtool sign /fd SHA256 /f $pfx /p $password $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "signing failed" }
Write-Host "Signed by $((Get-AuthenticodeSignature $msix).SignerCertificate.Subject)"
# The download page links to fixed names under site/download (ignored by git).
$download = "$root\site\download"; New-Item -ItemType Directory -Force $download | Out-Null
$version = ([xml](Get-Content "$root\src\PlainCommander.App\Package.appxmanifest")).Package.Identity.Version -replace '\.0$', ''
Copy-Item $msix "$download\PlainCommander-$version-$Platform.msix" -Force
Copy-Item $cer "$download\PlainCommander-dev.cer" -Force
$msix
