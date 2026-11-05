# Installs the dev-signed MSIX on this machine. Trusting the dev certificate needs an elevated shell once.
param([string]$Msix = (Get-ChildItem "$PSScriptRoot\out" -Recurse -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName))
$ErrorActionPreference = 'Stop'
$cer = "$PSScriptRoot\dev-cert.cer"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrators')
if ($isAdmin) {
  Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
} else {
  Write-Warning "Not elevated: the certificate must already be in LocalMachine\TrustedPeople (run once as administrator: Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople)"
}
Add-AppxPackage -Path $Msix -ForceApplicationShutdown
Get-AppxPackage -Name PlainCommander.App | Select-Object Name, Version, PackageFullName
