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
            IOException io when Win32Code(io) is 112 or 39 => Polish ? "Brak miejsca na dysku." : "The disk is full.",
            IOException io when Win32Code(io) is 32 or 33 => Polish
                ? "Plik jest używany przez inny program."
                : "The file is in use by another program.",
            _ => exception.Message,
        };
    }

    private static int Win32Code(IOException exception) => exception.HResult & 0xFFFF;
}
