namespace PlainCommander.Presentation;

/// <summary>Disk images attached as drives, the way Explorer's "Mount" and "Eject" do it.</summary>
public interface IDiskImages
{
    /// <summary>Attaches an .iso/.img (read-only) or .vhd/.vhdx; Windows may ask for administrator rights.</summary>
    void Attach(string imageFile);

    /// <summary>Detaches (ejects) the drive with this root ("E:\").</summary>
    void Detach(string driveRoot);
}
