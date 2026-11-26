# Sends the Store packages to Partner Center as a new submission (Microsoft Store submission API) and starts certification.
#   pwsh -File build/store-submit.ps1 -ProductId 9NJMJ1VQ1X87 -Packages a-x64-store.msix,b-ARM64-store.msix
#     [-ReleaseNotesFile docs/store/whats-new-en.txt] [-NoCommit] [-ReplacePending]
#   pwsh -File build/store-submit.ps1 -ProductId 9NJMJ1VQ1X87 -Status      (only reads: what is published, what is open)
# Credentials of the Microsoft Entra application added in Partner Center (Account settings -> User management ->
# Microsoft Entra applications, role Manager) come from the environment: STORE_TENANT_ID, STORE_CLIENT_ID, STORE_CLIENT_SECRET.
# The new submission is a copy of the last published one (listing, price, notes for certification); only the packages
# and, with -ReleaseNotesFile, the "What's new" text of the en-us listing change.
# -NoCommit leaves the submission as a draft in Partner Center. -ReplacePending deletes a submission that is still open.
# -Status changes nothing: it prints the published submission and the state of the open one, if any.
param(
  [Parameter(Mandatory)][string]$ProductId,
  [string[]]$Packages,
  [string]$ReleaseNotesFile,
  [switch]$NoCommit,
  [switch]$ReplacePending,
  [switch]$Status)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
foreach ($name in 'STORE_TENANT_ID', 'STORE_CLIENT_ID', 'STORE_CLIENT_SECRET') {
  if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Missing environment variable $name" }
}
$files = @($Packages | Where-Object { $_ } | ForEach-Object { Get-Item $_ })
if (-not $Status -and $files.Count -eq 0) { throw 'No packages given' }

$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$($env:STORE_TENANT_ID)/oauth2/token" -Body @{
    grant_type = 'client_credentials'; client_id = $env:STORE_CLIENT_ID; client_secret = $env:STORE_CLIENT_SECRET
    resource = 'https://manage.devcenter.microsoft.com' }).access_token
$headers = @{ Authorization = "Bearer $token" }
$api = "https://manage.devcenter.microsoft.com/v1.0/my/applications/$ProductId"

function Invoke-Store([string]$Method, [string]$Path, $Body) {
  $arguments = @{ Method = $Method; Uri = "$api$Path"; Headers = $headers }
  if ($null -ne $Body) {
    $arguments.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 100 -Compress))
    $arguments.ContentType = 'application/json; charset=utf-8'
  }
  elseif ($Method -eq 'Post') {
    # The API refuses a POST without a JSON media type, even when there is nothing to send.
    $arguments.Body = [byte[]]::new(0); $arguments.ContentType = 'application/json'
  }
  try { Invoke-RestMethod @arguments }
  catch { throw "$Method $Path failed: $($_.Exception.Message) $($_.ErrorDetails.Message)" }
}

$app = Invoke-Store Get ''
Write-Host "Product: $($app.primaryName) ($ProductId)"
$pending = $app.pendingApplicationSubmission
if ($Status) {
  # Read-only: nothing below this block runs.
  $lines = @()
  $published = $app.lastPublishedApplicationSubmission
  $lines += if ($published) { "Published submission: $($published.id)" } else { 'Published submission: none' }
  if ($published) {
    try {
      $live = Invoke-Store Get "/submissions/$($published.id)"
      $liveNames = @($live.applicationPackages | Where-Object { $_.fileStatus -ne 'PendingDelete' } | ForEach-Object { "$($_.fileName) $($_.version)" })
      if ($liveNames) { $lines += "  packages: $($liveNames -join ', ')" }
      if ($live.targetPublishDate) { $lines += "  target publish date: $($live.targetPublishDate)" }
      if ($live.status) { $lines += "  submission status: $($live.status)" }
    }
    catch { $lines += "  (the published submission could not be read: $($_.Exception.Message))" }
  }
  if (-not $pending) {
    $lines += 'Open submission: none (nothing is waiting in Partner Center; a new submission can be sent)'
  }
  else {
    $state = Invoke-Store Get "/submissions/$($pending.id)/status"
    $lines += "Open submission: $($pending.id)"
    $lines += "Status: $($state.status)"
    foreach ($kind in 'errors', 'warnings') {
      foreach ($entry in @($state.statusDetails.$kind)) {
        if ($entry) { $lines += "  ${kind}: $($entry.code): $($entry.details)" }
      }
    }
    foreach ($report in @($state.statusDetails.certificationReports)) {
      if ($report) { $lines += "  certification report of $($report.date): $($report.reportUrl)" }
    }
    try {
      $open = Invoke-Store Get "/submissions/$($pending.id)"
      $names = @($open.applicationPackages | Where-Object { $_.fileStatus -ne 'PendingDelete' } | ForEach-Object { "$($_.fileName) $($_.version)" })
      if ($names) { $lines += "  packages: $($names -join ', ')" }
    }
    catch { $lines += "  (the packages of the open submission could not be read: $($_.Exception.Message))" }
  }
  $lines | ForEach-Object { Write-Host $_ }
  if ($env:GITHUB_STEP_SUMMARY) { ((@("Product: $($app.primaryName) ($ProductId)") + $lines) -join "`n`n") >> $env:GITHUB_STEP_SUMMARY }
  return
}
if ($pending) {
  if (-not $ReplacePending) {
    throw "Submission $($pending.id) is still open in Partner Center. Finish or delete it there, or run again with -ReplacePending."
  }
  Write-Host "Deleting the open submission $($pending.id)"
  Invoke-Store Delete "/submissions/$($pending.id)" | Out-Null
}

$submission = Invoke-Store Post '/submissions'
Write-Host "Created submission $($submission.id)"
foreach ($package in $submission.applicationPackages) { $package.fileStatus = 'PendingDelete' }
$submission.applicationPackages = @($submission.applicationPackages) + @($files | ForEach-Object {
    [pscustomobject]@{ fileName = $_.Name; fileStatus = 'PendingUpload'; minimumDirectXVersion = 'None'; minimumSystemRam = 'None' } })
if ($ReleaseNotesFile) {
  $notes = (Get-Content $ReleaseNotesFile -Raw).Trim()
  if ($notes.Length -gt 1500) { throw "$ReleaseNotesFile is longer than 1500 characters" }
  $listing = $submission.listings.'en-us'
  if (-not $listing) { throw 'The submission has no en-us listing' }
  $listing.baseListing.releaseNotes = $notes
  Write-Host "What's new (en-us): $($notes.Length) characters"
}

# The packages go in one zip (stored, the msix files are compressed already), uploaded to the blob in blocks.
$zip = Join-Path ([IO.Path]::GetTempPath()) "store-$($submission.id).zip"
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
  foreach ($file in $files) {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $file.Name, 'NoCompression')
  }
}
finally { $archive.Dispose() }
$uploadUrl = $submission.fileUploadUrl.Replace('+', '%2B')
$blobHeaders = @{ 'x-ms-blob-type' = 'BlockBlob' }
$stream = [IO.File]::OpenRead($zip)
try {
  $buffer = [byte[]]::new(16MB); $blockIds = @()
  while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
    $blockId = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(('{0:d6}' -f $blockIds.Count)))
    $chunk = if ($read -eq $buffer.Length) { $buffer } else { $buffer[0..($read - 1)] }
    Invoke-RestMethod -Method Put -Uri "$uploadUrl&comp=block&blockid=$([Uri]::EscapeDataString($blockId))" -Headers $blobHeaders `
      -Body $chunk -ContentType 'application/octet-stream' | Out-Null
    $blockIds += $blockId
  }
}
finally { $stream.Dispose() }
$blockList = '<?xml version="1.0" encoding="utf-8"?><BlockList>' + (($blockIds | ForEach-Object { "<Latest>$_</Latest>" }) -join '') + '</BlockList>'
Invoke-RestMethod -Method Put -Uri "$uploadUrl&comp=blocklist" -Body $blockList -ContentType 'application/xml' | Out-Null
Write-Host "Uploaded $($files.Name -join ', ') ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
Remove-Item $zip

Invoke-Store Put "/submissions/$($submission.id)" $submission | Out-Null
if ($NoCommit) {
  Write-Host "Submission $($submission.id) is left as a draft in Partner Center (not sent to certification)."
  return
}

Invoke-Store Post "/submissions/$($submission.id)/commit" | Out-Null
# (not called $status: PowerShell variable names ignore case, and -Status is a [switch] parameter)
# The commit is checked by the Store in the background (package validation); certification starts after it.
$deadline = (Get-Date).AddMinutes(30)
do {
  Start-Sleep -Seconds 20
  $outcome = Invoke-Store Get "/submissions/$($submission.id)/status"
  Write-Host "Status: $($outcome.status)"
} while ($outcome.status -eq 'CommitStarted' -and (Get-Date) -lt $deadline)
foreach ($entry in @($outcome.statusDetails.errors) + @($outcome.statusDetails.warnings)) {
  if ($entry) { Write-Host "  $($entry.code): $($entry.details)" }
}
if ($outcome.status -eq 'CommitStarted') { throw 'The Store is still checking the submission after 30 minutes; see Partner Center.' }
if ($outcome.status -match 'Failed|Canceled') { throw "Submission $($submission.id) failed: $($outcome.status)" }
Write-Host "Submission $($submission.id) is in certification. Progress: Partner Center -> Plain Commander."
