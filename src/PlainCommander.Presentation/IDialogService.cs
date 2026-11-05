using PlainCommander.Core.Operations;

namespace PlainCommander.Presentation;

public enum NamePrompt
{
    NewDirectory,
    NewFile,
    Rename,
}

/// <summary>The checksum dialog's answer: the algorithm and the file to write.</summary>
public sealed record ChecksumAnswer(Core.Operations.ChecksumKind Kind, string FileName);

/// <summary>Alt+F5: where to write the archive (its extension picks the format) and the 7z password, if any.</summary>
public sealed record PackAnswer(string Target, string? Password = null);

/// <summary>Files → Encrypt: ".pce", ".7z" or ".zip" and the password (typed twice in the dialog).</summary>
/// <param name="HideName">.pce: the container gets a random name; the real one is inside.</param>
/// <param name="Compression">.pce: zstd before encrypting — never, automatically, always.</param>
/// <param name="Folder">Where the encrypted copies go; null or empty for next to each original.</param>
public sealed record EncryptAnswer(string Extension, string Password, bool HideName = false, Core.Crypto.PceCompression Compression = Core.Crypto.PceCompression.Auto, string? Folder = null);

/// <summary>Alt+F9: the folder to unpack into and whether each archive gets a folder of its own.</summary>
public sealed record UnpackAnswer(string Target, bool OwnFolders);

/// <summary>The permissions dialog's answer: the mode and whether the content of chosen folders follows.</summary>
public sealed record PermissionsAnswer(int Mode, bool Recursive);

/// <summary>Questions the commands ask before they act. Implemented with ContentDialogs in the app.</summary>
public interface IDialogService
{
    /// <summary>F5/F6 target, pre-filled with the other panel. Null when cancelled.</summary>
    Task<string?> AskTransferTargetAsync(TransferKind kind, int count, string firstName, string defaultTarget);

    /// <summary>Alt+F5: the archive to create, pre-filled in the other panel. Null when cancelled.</summary>
    /// <param name="sevenZip">7z can be written (offered as a format, with a password).</param>
    Task<PackAnswer?> AskPackTargetAsync(int count, string firstName, string defaultTarget, bool sevenZip);

    /// <summary>Files → Encrypt: format and password for <paramref name="count"/> items, each into its own container. Null when cancelled.</summary>
    /// <param name="sevenZip">7-Zip is there, so 7z and zip can be offered besides .pce.</param>
    /// <param name="defaultFolder">Where the copies go unless the user types another folder (the other panel, like Alt+F5).</param>
    Task<EncryptAnswer?> AskEncryptAsync(int count, string firstName, bool sevenZip, string defaultFolder);

    /// <summary>Enter on a .pce file or Files → Decrypt: the password, asked again when wrong. Null when cancelled.</summary>
    Task<string?> AskDecryptPasswordAsync(string file, bool wrong);

    /// <summary>Ctrl+Z: true to reverse <paramref name="operationTitle"/>; <paramref name="details"/> say what will happen.</summary>
    Task<bool> ConfirmUndoAsync(string operationTitle, string details);

    /// <summary>After encrypting: true to delete the unencrypted originals for good.</summary>
    Task<bool> ConfirmDeleteOriginalsAsync(int count);

    /// <summary>Alt+F9: where to unpack the chosen archives, pre-filled with the other panel. Null when cancelled.</summary>
    Task<UnpackAnswer?> AskUnpackTargetAsync(int count, string firstName, string defaultTarget);

    /// <summary>F7 folder name or Shift+F6 new name. Null when cancelled.</summary>
    Task<string?> AskNameAsync(NamePrompt prompt, string initial);

    Task<bool> ConfirmDeleteAsync(int files, int directories, string firstName, bool permanent);

    /// <summary>Ctrl+S filter, pre-filled with the current one (or none). Null when cancelled; an empty filter removes it.</summary>
    Task<Core.Navigation.PanelFilter?> AskFilterAsync(Core.Navigation.PanelFilter? initial, bool createdKnown);

    /// <summary>Num+ / Num− mask. Null when cancelled.</summary>
    Task<string?> AskMaskAsync(bool mark, string initial);

    Task ShowErrorAsync(string message);

    /// <summary>A plain notice with an OK button (nothing went wrong, e.g. "no differences").</summary>
    Task ShowInfoAsync(string title, string message);

    /// <summary>Shift+F2 options, pre-filled with the last ones. Null when cancelled.</summary>
    Task<PlainCommander.Core.Compare.CompareOptions?> AskCompareOptionsAsync(PlainCommander.Core.Compare.CompareOptions initial);

    /// <summary>F4 on a server file: the temp copy changed; true to upload it back.</summary>
    Task<bool> ConfirmUploadBackAsync(string name, string server);

    /// <summary>Files → Permissions on a server: the mode to set, pre-filled with the first item's. Null when cancelled.</summary>
    Task<PermissionsAnswer?> AskPermissionsAsync(int count, string firstName, int mode, bool anyFolder);

    /// <summary>Files → Attributes on disk, pre-filled with the first item's attributes and time. Null when cancelled.</summary>
    Task<Core.Operations.AttributeChange?> AskAttributesAsync(int count, string firstName, FileAttributes attributes, DateTimeOffset modified, bool anyFolder);

    /// <summary>Files → Create checksums: algorithm and file name (pre-filled). Null when cancelled.</summary>
    Task<ChecksumAnswer?> AskChecksumAsync(int count, string defaultName);

    /// <summary>F7 / Shift+F4 / Shift+F6 met "access denied": true to try again as administrator.</summary>
    Task<bool> ConfirmElevateAsync(string problem);

    /// <summary>After Shift+F2 found differences: true when the user wants the synchronize window.</summary>
    Task<bool> OfferSyncAsync(int differences);
}
