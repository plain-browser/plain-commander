using System.Diagnostics;
using PlainCommander.Presentation;

namespace PlainCommander.App.Services;

internal sealed class ShellLauncher : ILauncher
{
    public void Open(string path, string workingDirectory)
    {
        using var _ = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        });
    }

    public void Edit(string editor, string path, string workingDirectory)
    {
        using var _ = Process.Start(new ProcessStartInfo(editor)
        {
            Arguments = $"\"{path}\"",
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        });
    }

    /// <summary>
    /// cmd /c in its own console window: console programs show their output
    /// there, GUI programs start and the window goes away. Nothing is captured, so nothing can hang the UI.
    /// </summary>
    public void RunCommand(string command, string workingDirectory)
    {
        using var _ = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c " + command,
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        });
    }
}
