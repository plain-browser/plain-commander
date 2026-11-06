# Builds the unsigned MSIX packages for the Microsoft Store (the Store signs them). Run on Windows from the repo root:
#   powershell -File build\msix-store.ps1 [-Platforms x64,ARM64] [-Wack]
# The Store identity (Package/Identity/Name, Publisher, PublisherDisplayName from Partner Center -> Product identity)
# is read from build\store-identity.json (see store-identity.example.json). Package.appxmanifest itself keeps the
# dev identity, so build\msix.ps1 and the dev certificate keep working; a copy with the Store identity is built from.
# -Wack runs the Windows App Certification Kit on each package (needs the Windows SDK; takes a few minutes per package).
param([string[]]$Platforms = @('x64', 'ARM64'), [string]$Configuration = 'Release', [switch]$Wack)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$identityFile = "$PSScriptRoot\store-identity.json"
if (-not (Test-Path $identityFile)) { throw "Missing $identityFile - copy store-identity.example.json and fill in the values from Partner Center." }
$id = Get-Content $identityFile -Raw | ConvertFrom-Json
foreach ($k in 'Name', 'Publisher', 'PublisherDisplayName') { if (-not $id.$k) { throw "store-identity.json: missing $k" } }

# Manifest copy with the Store identity (same folder depth is irrelevant: asset paths are package-relative).
$source = "$root\src\PlainCommander.App\Package.appxmanifest"
$xml = [xml](Get-Content $source -Raw)
$xml.Package.Identity.Name = [string]$id.Name
$xml.Package.Identity.Publisher = [string]$id.Publisher
$xml.Package.Properties.PublisherDisplayName = [string]$id.PublisherDisplayName
$version = $xml.Package.Identity.Version
if ($version -notmatch '\.0$') { throw "Package version $version must end with .0 for the Store" }
$storeDir = "$PSScriptRoot\out\store"; New-Item -ItemType Directory -Force $storeDir | Out-Null
$manifest = "$storeDir\Package.appxmanifest"; $xml.Save($manifest)
Write-Host "Store identity: $($id.Name) / $($id.Publisher) / $($id.PublisherDisplayName), version $version"

$packages = @()
foreach ($platform in $Platforms) {
  $outDir = "$storeDir\$platform\"
  dotnet build "$root\src\PlainCommander.App" -c $Configuration -p:Platform=$platform -p:Packaged=true `
    -p:AppxManifestPath=$manifest -p:AppxPackageDir=$outDir -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=false -nologo -v q
  if ($LASTEXITCODE -ne 0) { throw "build failed ($platform)" }
  $msix = Get-ChildItem $outDir -Recurse -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  $final = "$storeDir\PlainCommander-$($version -replace '\.0$', '')-$platform-store.msix"
  Copy-Item $msix.FullName $final -Force
  $packages += $final
}

if ($Wack) {
  $appcert = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName
  if (-not $appcert) { throw "appcert.exe (Windows App Certification Kit) not found - install the Windows SDK" }
  foreach ($pkg in $packages) {
    $report = [IO.Path]::ChangeExtension($pkg, '.wack.xml')
    Write-Host "WACK: $pkg"
    & $appcert reset | Out-Null
    & $appcert test -appxpackagepath $pkg -reportoutputpath $report | Out-Null
    $result = ([xml](Get-Content $report -Raw)).REPORT.OVERALL_RESULT
    Write-Host "WACK result: $result ($report)"
  }
}
Write-Host "Upload to Partner Center (Packages section):"
$packages
