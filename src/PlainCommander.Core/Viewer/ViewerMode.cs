namespace PlainCommander.Core.Viewer;

public enum ViewerMode
{
    Text,
    Hex,
    Image,
}

/// <summary>Picks the view a file opens in: images by extension, binaries as hex, the rest as text.</summary>
public static class ViewerModeDetector
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff",
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static ViewerMode Detect(string path, ReadOnlySpan<byte> sample)
    {
        if (IsImage(path))
        {
            return ViewerMode.Image;
        }

        return TextDecoder.LooksBinary(sample) ? ViewerMode.Hex : ViewerMode.Text;
    }
}
