using PlainCommander.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace PlainCommander.App.Services;

/// <summary>Text to the Windows clipboard; it stays there after the app closes.</summary>
internal sealed class ClipboardService : IClipboard
{
    public void SetText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
