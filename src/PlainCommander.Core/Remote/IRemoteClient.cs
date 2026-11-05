namespace PlainCommander.Core.Remote;

/// <summary>
/// One connection to a server. Paths are server paths ("/home/user/a.txt"). Every failure surfaces as an
/// <see cref="IOException"/> (often <see cref="RemoteException"/>) so the operation engine can offer Retry/Skip.
/// A client that lost its connection reconnects on the next call. Not thread-safe: one call at a time.
/// </summary>
public interface IRemoteClient : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>
    /// An upload appears only once it is complete (S3): it goes straight to its name, without a ".part" to continue
    /// later, and the store has no append. False for SFTP and FTP.
    /// </summary>
    bool AtomicUploads => false;

    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>The folder the server starts in (home), as an absolute path.</summary>
    Task<string> GetHomeAsync(CancellationToken cancellationToken);

    /// <summary>Folder content without "." and ".."; links to folders count as folders.</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>Null when nothing is there.</summary>
    Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken);

    /// <summary>The exact time of a file (FTP listings may show the day only); null when the server cannot tell.</summary>
    Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the file's bytes from <paramref name="offset"/> on to <paramref name="destination"/>
    /// (an offset continues a broken download: the caller already has that many bytes).
    /// </summary>
    Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="source"/> from its current position to the file: a new or overwritten file, or with
    /// <paramref name="append"/> added at the end of what is there (continuing a broken upload).
    /// </summary>
    Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken);

    /// <summary>Removes an empty folder.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Fails when <paramref name="target"/> exists (on most servers).</summary>
    Task RenameAsync(string source, string target, CancellationToken cancellationToken);

    /// <summary>chmod: Unix permission bits (e.g. 0o755). FTP needs SITE CHMOD, which most Unix servers have.</summary>
    Task SetModeAsync(string path, int mode, CancellationToken cancellationToken);

    /// <summary>Best effort: servers without MFMT/setstat keep the upload time.</summary>
    Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken);
}

/// <summary>A server said no, or the connection failed. The message is the server's (or the library's) text.</summary>
public sealed class RemoteException : IOException
{
    public RemoteException()
    {
    }

    public RemoteException(string message)
        : base(message)
    {
    }

    public RemoteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The login was refused: asking for the password again makes sense.</summary>
    public bool IsAuthentication { get; init; }

    /// <summary>The user closed the password dialog.</summary>
    public bool IsCancelled { get; init; }

    /// <summary>The user rejected the server's key or certificate.</summary>
    public bool IsUntrusted { get; init; }
}
