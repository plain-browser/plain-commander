namespace PlainCommander.Core.FileSystem;

/// <summary>
/// Files On-Demand (OneDrive and similar): a file can be a placeholder whose content is downloaded on the first read,
/// which can take many seconds. Listing and metadata are free; opening the content is the slow part.
/// </summary>
public static class CloudFile
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>True when reading the content will first download it.</summary>
    public static bool IsCloudOnly(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    public static bool IsCloudOnly(string path)
    {
        try
        {
            return IsCloudOnly(File.GetAttributes(ExactPath.Native(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
