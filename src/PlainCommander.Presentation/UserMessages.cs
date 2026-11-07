using System.Globalization;
using PlainCommander.Core.Operations;

namespace PlainCommander.Presentation;

/// <summary>Turns exceptions into sentences a user can act on, in Polish or English.</summary>
public static class UserMessages
{
    private static bool Polish => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Core.FileSystem.ExactPath.DisplayText(DescribeCore(exception));
    }

    private static string DescribeCore(Exception exception)
    {
        return exception switch
        {
            OperationRuleException { Violation: RuleViolation.SameFile } => Polish
                ? "Nie można skopiować pliku na samego siebie."
                : "A file cannot be copied onto itself.",
            OperationRuleException { Violation: RuleViolation.IntoItself } => Polish
                ? "Nie można skopiować ani przenieść katalogu do jego własnego podkatalogu."
                : "A folder cannot be copied or moved into itself.",
            OperationRuleException { Violation: RuleViolation.LinkAcrossVolumes } => Polish
                ? "Dowiązania nie da się przenieść na inny dysk, bo opróżniłoby to katalog, na który wskazuje."
                : "A link cannot be moved to another drive: that would empty the folder it points to.",
            OperationRuleException { Violation: RuleViolation.TypeMismatch } => Polish
                ? "W miejscu docelowym jest już plik lub katalog o tej nazwie, ale innego rodzaju."
                : "The target already has a file or folder of that name, of the other kind.",
            OperationRuleException { Violation: RuleViolation.UnsupportedArchive } e => Polish
                ? $"Nie umiem zapisać archiwum „{System.IO.Path.GetFileName(e.Path)}”: obsługiwane są zip, 7z, tar i tar.gz."
                : $"Cannot write the archive “{System.IO.Path.GetFileName(e.Path)}”: zip, 7z, tar and tar.gz are supported.",
            OperationRuleException { Violation: RuleViolation.AlreadyExists } e => Polish
                ? $"„{e.Path}” już istnieje."
                : $"“{e.Path}” already exists.",
            OperationRuleException { Violation: RuleViolation.FolderGone } e => FolderGone(e.Path),
            InvalidNameException { Problem: NameProblem.Empty } => Polish ? "Nazwa jest pusta." : "The name is empty.",
            InvalidNameException { Problem: NameProblem.TooLong } => Polish
                ? $"Nazwa jest za długa: można użyć najwyżej {SimpleCommands.MaxNameLength} znaków."
                : $"The name is too long: at most {SimpleCommands.MaxNameLength} characters are allowed.",
            InvalidNameException => Polish
                ? "Nazwa zawiera niedozwolone znaki (\\ / : * ? \" < > |)."
                : "The name contains characters that are not allowed (\\ / : * ? \" < > |).",
            Core.Remote.RemoteException { IsCancelled: true } => Polish ? "Logowanie anulowane." : "Login cancelled.",
            Core.Remote.RemoteException { IsUntrusted: true } => Polish
                ? "Połączenie przerwane: klucz lub certyfikat serwera nie został zaakceptowany."
                : "Disconnected: the server's key or certificate was not accepted.",
            Core.Remote.RemoteException { IsAuthentication: true } e => Polish ? $"Serwer odrzucił logowanie: {e.Message}" : $"The server refused the login: {e.Message}",
            Core.Remote.RemoteException e => Polish ? $"Serwer: {e.Message}" : $"Server: {e.Message}",
            Core.Archives.ArchivePasswordException { Wrong: true } e => Polish
                ? $"Złe hasło do archiwum „{System.IO.Path.GetFileName(e.ArchiveFile)}”."
                : $"Wrong password for the archive “{System.IO.Path.GetFileName(e.ArchiveFile)}”.",
            Core.Archives.ArchivePasswordException e => Polish
                ? $"Archiwum „{System.IO.Path.GetFileName(e.ArchiveFile)}” jest zaszyfrowane; bez hasła nie da się go otworzyć."
                : $"The archive “{System.IO.Path.GetFileName(e.ArchiveFile)}” is encrypted and cannot be opened without its password.",
            Core.Undo.UndoException { Problem: Core.Undo.UndoProblem.Gone } => Polish
                ? "Tego elementu nie ma już tam, gdzie go zostawiła operacja (przeniesiony albo usunięty później)."
                : "The item is no longer where the operation left it (moved or deleted since).",
            Core.Undo.UndoException { Problem: Core.Undo.UndoProblem.PlaceTaken } e => Polish
                ? $"W miejscu „{e.Path}” jest już coś innego; nic nie zostało nadpisane."
                : $"Something else is at “{e.Path}” now; nothing was overwritten.",
            Core.Undo.UndoException { Problem: Core.Undo.UndoProblem.NotInRecycleBin } => Polish
                ? "Tego elementu nie ma już w koszu (kosz opróżniony albo element przywrócony wcześniej)."
                : "The item is not in the Recycle Bin any more (emptied, or restored before).",
            ArchiveEntryOutsideException e => Polish
                ? $"Wpis „{e.Entry}” wskazuje poza folder docelowy, więc nie został rozpakowany."
                : $"The entry \"{e.Entry}\" points outside the target folder, so it was not extracted.",
            Core.Archives.NotAnArchiveException => Polish
                ? "To nie jest archiwum w żadnym znanym formacie."
                : "This is not an archive in any known format.",
            InvalidDataException e => Polish
                ? $"Archiwum jest uszkodzone albo ma nieobsługiwany format ({e.Message})"
                : $"The archive is damaged or in an unsupported format ({e.Message})",
            UnauthorizedAccessException => Polish ? "Brak dostępu." : "Access denied.",
            DirectoryNotFoundException or FileNotFoundException => Polish ? "Nie znaleziono." : "Not found.",
            NotSupportedException => Polish
                ? "Maski zmiany nazwy (np. *.bak) nie są jeszcze obsługiwane."
                : "Rename masks (such as *.bak) are not supported yet.",
            ArgumentException => Polish ? "Nieprawidłowa nazwa lub ścieżka." : "Invalid name or path.",
            PathTooLongException => Polish
                ? "Nazwa lub ścieżka jest za długa."
                : "The name or path is too long.",
            IOException io when Win32Code(io) is 206 => Polish
                ? "Nazwa lub ścieżka jest za długa."
                : "The name or path is too long.",
            IOException io when Win32Code(io) is 123 or 161 => Polish ? "Nieprawidłowa nazwa lub ścieżka." : "Invalid name or path.",
            IOException io when Win32Code(io) is 80 or 183 => Polish ? "Element o tej nazwie już istnieje." : "An item with this name already exists.",
            IOException io when Win32Code(io) is 2 or 3 => Polish ? "Nie znaleziono." : "Not found.",
            IOException io when Win32Code(io) is 5 => Polish ? "Brak dostępu." : "Access denied.",
            IOException io when Win32Code(io) is 112 or 39 => Polish ? "Brak miejsca na dysku." : "The disk is full.",
            IOException io when Win32Code(io) is 32 or 33 => Polish
                ? "Plik jest używany przez inny program."
                : "The file is in use by another program.",
            _ => exception.Message,
        };
    }

    /// <summary>The panel's folder was deleted or moved away meanwhile.</summary>
    public static string FolderGone(string path) => Core.FileSystem.ExactPath.DisplayText(Polish
        ? $"Folderu „{path}” już nie ma (usunięty albo przeniesiony), więc nic w nim nie zrobiono."
        : $"The folder “{path}” is gone (deleted or moved), so nothing was done in it.");

    /// <summary>The error is about the typed name (bad, too long, taken): worth asking for the name again.</summary>
    public static bool IsAboutName(Exception exception) => exception switch
    {
        ArgumentException => true,
        OperationRuleException { Violation: RuleViolation.AlreadyExists or RuleViolation.TypeMismatch } => true,
        PathTooLongException => true,
        IOException io => Win32Code(io) is 80 or 123 or 161 or 183 or 206,
        _ => false,
    };

    private static int Win32Code(IOException exception) => exception.HResult & 0xFFFF;
}
