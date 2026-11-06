using CommunityToolkit.Mvvm.ComponentModel;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Network;

namespace PlainCommander.Presentation;

public sealed partial class DriveItem : ObservableObject
{
    public DriveItem(DriveEntry drive)
    {
        Drive = drive;
    }

    public DriveEntry Drive { get; }

    public string Root => Drive.Root;

    /// <summary>A synced OneDrive folder (listed after the drives) rather than a drive.</summary>
    public bool IsCloud => Drive.CloudName is not null;

    /// <summary>A WSL distribution ("\\wsl.localhost\Ubuntu"), listed after the drives like OneDrive.</summary>
    public bool IsWsl => !IsCloud && WslDistros.DistroOf(Drive.Root) is not null;

    /// <summary>"C" for "C:\", the full root otherwise ("/" on Linux test runs), the folder's name for OneDrive, the distribution for WSL.</summary>
    public string Letter => IsCloud ? Drive.CloudName! : WslDistros.DistroOf(Drive.Root) ?? (Drive.Root.Length >= 2 && Drive.Root[1] == ':' ? Drive.Root[..1] : Drive.Root);

    /// <summary>The button text: the letter ("C"), for OneDrive "OneDrive" / the organisation, for WSL the distribution ("Ubuntu").</summary>
    public string PillText => IsCloud ? new CloudFolder(Drive.CloudName!, Drive.Root).ShortName : Letter;

    public string MenuText => Texts.DriveMenu(Drive);

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}
