using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace PlainCommander.Core.Undo;

/// <summary>
/// Restores from the Recycle Bin the way Explorer's "Restore" does: every recycled item is a pair in
/// "X:\$Recycle.Bin\&lt;user SID&gt;\": "$Rxxxxxx" (the item itself) and "$Ixxxxxx" (where it came from and when).
/// The newest pair for the path is moved back and its "$I" record removed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBinRestore : IRecycleBinRestore
{
    public bool Restore(string originalPath, DateTime sinceUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalPath);
        var root = Path.GetPathRoot(originalPath);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrEmpty(root) || sid is null)
        {
            return false;
        }

        var bin = Path.Combine(root, "$Recycle.Bin", sid);
        if (!Directory.Exists(bin))
        {
            return false;
        }

        var target = Path.TrimEndingDirectorySeparator(originalPath);
        (string Info, DateTime Deleted)? best = null;
        foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
        {
            if (Read(info) is { } record && string.Equals(record.Path, target, StringComparison.OrdinalIgnoreCase)
                && record.DeletedUtc >= sinceUtc && (best is null || record.DeletedUtc > best.Value.Deleted))
            {
                best = (info, record.DeletedUtc);
            }
        }

        if (best is not { } found)
        {
            return false;
        }

        var item = Path.Combine(bin, "$R" + Path.GetFileName(found.Info)[2..]);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new UndoException(UndoProblem.PlaceTaken, target);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(item))
        {
            Directory.Move(item, target);
        }
        else if (File.Exists(item))
        {
            File.Move(item, target);
        }
        else
        {
            return false;
        }

        File.Delete(found.Info);
        return true;
    }

    /// <summary>
    /// "$I" layout: version (8 bytes; 1 = Vista–8.1, 2 = Windows 10+), size (8), deletion time as FILETIME (8), then
    /// the path: version 1 a fixed 260-character field, version 2 a character count (4) and the characters.
    /// </summary>
    internal static (string Path, DateTime DeletedUtc)? Read(string infoFile)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoFile);
            if (bytes.Length < 24)
            {
                return null;
            }

            var version = BitConverter.ToInt64(bytes, 0);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(bytes, 16));
            string path;
            if (version == 2 && bytes.Length >= 28)
            {
                var chars = BitConverter.ToInt32(bytes, 24);
                path = Encoding.Unicode.GetString(bytes, 28, Math.Min(chars * 2, bytes.Length - 28));
            }
            else if (version == 1)
            {
                path = Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24));
            }
            else
            {
                return null;
            }

            return (path.TrimEnd('\0'), deleted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
