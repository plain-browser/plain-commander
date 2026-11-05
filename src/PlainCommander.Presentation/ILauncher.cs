namespace PlainCommander.Presentation;

/// <summary>Starts programs: a file with its associated program (Enter on a file) or a typed command.</summary>
public interface ILauncher
{
    void Open(string path, string workingDirectory);

    /// <summary>F4: opens the file in the configured editor (a program name or path from settings).</summary>
    void Edit(string editor, string path, string workingDirectory);

    /// <summary>Runs a command line through the shell (cmd /c) in the given folder; a console shows while it runs.</summary>
    void RunCommand(string command, string workingDirectory);
}
