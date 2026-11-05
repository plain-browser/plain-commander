using System.Collections.Concurrent;

namespace PlainCommander.Core.Archives;

/// <summary>Asks the user for an archive's password. Called from worker threads; null means "cancel".</summary>
public interface IArchivePasswordPrompt
{
    /// <param name="wrong">The previous password did not fit.</param>
    Task<string?> AskArchivePasswordAsync(string archiveFile, bool wrong, CancellationToken cancellationToken);
}

/// <summary>No archive format recognizes the file.</summary>
public sealed class NotAnArchiveException(string file, Exception? inner = null)
    : IOException($"{Path.GetFileName(file)} is not an archive that can be opened.", inner)
{
    public string File { get; } = file;
}

/// <summary>The archive is encrypted and no (right) password was given.</summary>
public sealed class ArchivePasswordException(string archiveFile, bool wrong)
    : IOException(wrong ? $"Wrong password for {archiveFile}." : $"{archiveFile} is encrypted.")
{
    public string ArchiveFile { get; } = archiveFile;

    public bool Wrong { get; } = wrong;
}

/// <summary>
/// Passwords of encrypted archives, remembered for the session (never written anywhere) so browsing, F3 and
/// extraction ask once per archive. The prompt is set by the app; without one, encrypted archives cannot be opened.
/// </summary>
public static class ArchivePasswords
{
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase);

    public static IArchivePasswordPrompt? Prompt { get; set; }

    public static string? Remembered(string archiveFile) => Known.GetValueOrDefault(archiveFile);

    public static void Remember(string archiveFile, string password) => Known[archiveFile] = password;

    public static void Forget(string archiveFile) => Known.TryRemove(archiveFile, out _);

    /// <summary>
    /// The remembered password, else the user's answer. Blocks the calling (worker) thread while the dialog is open;
    /// on a thread with a synchronization context (the UI thread) it never asks, because waiting there would hang.
    /// </summary>
    public static string? Get(string archiveFile, bool wrong)
    {
        if (!wrong && Known.TryGetValue(archiveFile, out var known))
        {
            return known;
        }

        if (Prompt is not { } prompt || SynchronizationContext.Current is not null)
        {
            return null;
        }

        var answer = prompt.AskArchivePasswordAsync(archiveFile, wrong, CancellationToken.None).GetAwaiter().GetResult();
        if (answer is null)
        {
            return null;
        }

        Known[archiveFile] = answer;
        return answer;
    }
}
