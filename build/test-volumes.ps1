# Mounts small virtual disks with other file systems for tests/PlainCommander.Core.Tests/Volumes.
# Run as administrator on Windows 11. Disks are expandable VHDX files and use little real space.
#   .\build\test-volumes.ps1            create (first run) or re-attach (after a reboot)
#   .\build\test-volumes.ps1 -Remove    detach and delete them
# Then, in the same shell:
#   $env:PC_TEST_VOLUMES = "T:\;U:\;V:\;X:\"; $env:PC_TEST_FULL_VOLUME = "W:\"   # $env:PC_TEST_BIG = "1" for the >4 GB test
#   dotnet test --project tests\PlainCommander.Core.Tests --filter-class "*Volume*"
param([string]$Folder = 'C:\vhd', [switch]$Remove)
$ErrorActionPreference = 'Stop'

# ReFS can only be formatted as a Dev Drive on client Windows, and a Dev Drive needs at least 50 GB.
$disks = @(
    @{ Name = 'fat32'; SizeMB = 6000;  Fs = 'FAT32'; Letter = 'T' },
    @{ Name = 'exfat'; SizeMB = 6000;  Fs = 'exFAT'; Letter = 'U' },
    @{ Name = 'refs';  SizeMB = 51200; Fs = 'ReFS';  Letter = 'V' },
    @{ Name = 'tiny';  SizeMB = 40;    Fs = 'NTFS';  Letter = 'W' },
    @{ Name = 'fat16'; SizeMB = 200;   Fs = 'FAT';   Letter = 'X' }
)

function Run-DiskPart([string]$script) {
    $file = Join-Path $Folder 'diskpart.txt'
    Set-Content -Path $file -Value $script -Encoding ascii
    diskpart /s $file | Out-Null
}

New-Item -ItemType Directory -Force $Folder | Out-Null
foreach ($d in $disks) {
    $file = Join-Path $Folder ($d.Name + '.vhdx')
    if ($Remove) {
        if (Test-Path $file) {
            Run-DiskPart "select vdisk file=`"$file`"`ndetach vdisk"
            Remove-Item $file
            "$($d.Letter): removed"
        }
        continue
    }

    if (Test-Path ($d.Letter + ':\')) {
        "$($d.Letter): already mounted"
        continue
    }

    if (Test-Path $file) {
        Run-DiskPart "select vdisk file=`"$file`"`nattach vdisk"
        "$($d.Letter): re-attached"
        continue
    }

    Run-DiskPart "create vdisk file=`"$file`" maximum=$($d.SizeMB) type=expandable`nselect vdisk file=`"$file`"`nattach vdisk`ncreate partition primary`nassign letter=$($d.Letter)"
    if ($d.Fs -eq 'ReFS') {
        Format-Volume -DriveLetter $d.Letter -DevDrive -NewFileSystemLabel $d.Name -Confirm:$false -Force | Out-Null
    } else {
        Format-Volume -DriveLetter $d.Letter -FileSystem $d.Fs -NewFileSystemLabel $d.Name -Confirm:$false -Force | Out-Null
    }
    "$($d.Letter): $($d.Fs)"
}
