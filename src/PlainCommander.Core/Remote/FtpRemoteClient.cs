using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using FluentFTP;
using FluentFTP.Exceptions;

namespace PlainCommander.Core.Remote;

/// <summary>
/// FTP and FTPS through FluentFTP. Passive mode unless the site asks for active; port 990 means implicit TLS.
/// A certificate with chain errors (self-signed) is shown to the user once; a valid one is accepted silently.
/// Without a user name the login is anonymous.
/// </summary>
internal sealed class FtpRemoteClient : IRemoteClient
{
    private readonly RemoteSite _site;
    private readonly RemoteCredentials _credentials;
    private readonly Func<HostIdentity, bool> _verify;
    private AsyncFtpClient? _client;
    private bool _rejected;

    public FtpRemoteClient(RemoteSite site, RemoteCredentials credentials, Func<HostIdentity, bool> verify)
    {
        _site = site;
        _credentials = credentials;
        _verify = verify;
    }

    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposeClientAsync().ConfigureAwait(false);
        _rejected = false;
        var host = _site.Host.Trim();
        var port = _site.EffectivePort;
        var config = new FtpConfig
        {
            EncryptionMode = _site.Protocol != RemoteProtocol.Ftps ? FtpEncryptionMode.None : port == 990 ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit,
            DataConnectionType = _site.ActiveMode ? FtpDataConnectionType.AutoActive : FtpDataConnectionType.AutoPassive,
            ConnectTimeout = 20_000,
            // A connection that dies without a reset (server restarted, Wi-Fi gone) is noticed after 20 s; the
            // transfer then continues on a new one. 20 s is also what FileZilla waits.
            ReadTimeout = 20_000,
            DataConnectionConnectTimeout = 20_000,
            DataConnectionReadTimeout = 20_000,
            RetryAttempts = 1,
            TimeConversion = FtpDate.UTC,
        };
        if (config.EncryptionMode != FtpEncryptionMode.None)
        {
            // GnuTLS resumes the TLS session on data connections, which vsftpd and others require by default
            // (SslStream cannot: "522 SSL connection failed: session reuse required").
            config.CustomStream = typeof(FluentFTP.GnuTLS.GnuTlsStream);
            // no ALPN: vsftpd 3.0.5 rejects the handshake when it sees one ("ALPN rejection")
            config.CustomStreamConfig = new FluentFTP.GnuTLS.GnuConfig { SetALPNControlConnection = string.Empty, SetALPNDataConnection = string.Empty };
        }

        var anonymous = string.IsNullOrEmpty(_site.User);
        var client = new AsyncFtpClient(host, anonymous ? "anonymous" : _site.User, anonymous ? "anonymous@" : _credentials.Secret, port, config);
        client.ValidateCertificate += (_, e) =>
        {
            if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None)
            {
                e.Accept = true;
                return;
            }

            var fingerprint = "SHA256:" + Convert.ToHexString(SHA256.HashData(e.Certificate.GetRawCertData()));
            var trusted = _verify(new HostIdentity(host.ToLowerInvariant(), port, "TLS " + e.Certificate.Subject, fingerprint));
            _rejected |= !trusted;
            e.Accept = trusted;
        };

        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            _client = client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            client.Dispose();
            throw ex switch
            {
                _ when _rejected => new RemoteException(ex.Message, ex) { IsUntrusted = true },
                FtpAuthenticationException => new RemoteException(ex.Message, ex) { IsAuthentication = true },
                _ => Wrap(ex),
            };
        }
    }

    public Task<string> GetHomeAsync(CancellationToken cancellationToken) =>
        CallAsync(async c => RemotePath.NormalizeInner(await c.GetWorkingDirectory(cancellationToken).ConfigureAwait(false)), cancellationToken);

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) => CallAsync<IReadOnlyList<RemoteItem>>(async c =>
    {
        var listing = await c.GetListing(path, FtpListOption.AllFiles, cancellationToken).ConfigureAwait(false);
        var items = new List<RemoteItem>(listing.Length);
        foreach (var item in listing)
        {
            if (item.Name is "." or ".." || string.IsNullOrEmpty(item.Name))
            {
                continue;
            }

            var isDirectory = item.Type == FtpObjectType.Directory;
            if (item.Type == FtpObjectType.Link)
            {
                isDirectory = item.LinkObject?.Type == FtpObjectType.Directory
                    || await c.DirectoryExists(RemotePath.JoinInner(path, item.Name), cancellationToken).ConfigureAwait(false);
            }

            var modified = item.Modified == DateTime.MinValue ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(item.Modified, DateTimeKind.Utc));
            int? mode = item.Chmod > 0 ? UnixMode.FromDigits(item.Chmod) : null;
            items.Add(new RemoteItem(item.Name, isDirectory, isDirectory ? 0 : Math.Max(0, item.Size), modified, item.Type == FtpObjectType.Link, mode));
        }

        return items;
    }, cancellationToken);

    /// <summary>Through the parent's listing: MLST is not everywhere, LIST is.</summary>
    public async Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = RemotePath.NormalizeInner(path);
        if (normalized == "/")
        {
            return new RemoteItem("/", true, 0, DateTimeOffset.MinValue);
        }

        IReadOnlyList<RemoteItem> siblings;
        try
        {
            siblings = await ListAsync(RemotePath.ParentInner(normalized), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        var name = RemotePath.NameOf(normalized);
        var found = siblings.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
        if (found is { IsDirectory: false } && await GetModifiedAsync(normalized, cancellationToken).ConfigureAwait(false) is { } exact)
        {
            found = found with { Modified = exact };
        }

        return found;
    }

    /// <summary>MDTM: LIST shows old files with the day only, MDTM to the second.</summary>
    public async Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await CallAsync(async c =>
            {
                var time = await c.GetModifiedTime(path, cancellationToken).ConfigureAwait(false);
                return time == DateTime.MinValue ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null; // no MDTM here
        }
    }

    public Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        // offset > 0: REST before RETR, the server sends the rest of the file
        if (!await c.DownloadStream(destination, path, offset, null, cancellationToken).ConfigureAwait(false))
        {
            throw new RemoteException($"Download of {path} failed.");
        }

        return true;
    }, cancellationToken);

    public Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        if (append)
        {
            // APPE with our own copy loop: the library's resume modes re-check sizes and move the source themselves
            var remote = await c.OpenAppend(path, FtpDataType.Binary, -1, cancellationToken).ConfigureAwait(false);
            await using (remote.ConfigureAwait(false))
            {
                await source.CopyToAsync(remote, 1 << 16, cancellationToken).ConfigureAwait(false);
            }

            var reply = await c.GetReply(cancellationToken).ConfigureAwait(false);
            if (!reply.Success)
            {
                throw new RemoteException($"Upload of {path} failed: {reply.Message}");
            }

            return true;
        }

        var status = await c.UploadStream(source, path, FtpRemoteExists.NoCheck, false, null, cancellationToken).ConfigureAwait(false);
        if (status == FtpStatus.Failed)
        {
            throw new RemoteException($"Upload of {path} failed.");
        }

        return true;
    }, cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.DeleteFile(path, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.DeleteDirectory(path, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        if (!await c.CreateDirectory(path, false, cancellationToken).ConfigureAwait(false))
        {
            throw new RemoteException($"Cannot create {path}.");
        }

        return true;
    }, cancellationToken);

    public Task RenameAsync(string source, string target, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.Rename(source, target, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task SetModeAsync(string path, int mode, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.Chmod(path, UnixMode.ToDigits(mode), cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        try
        {
            await c.SetModifiedTime(path, modified.UtcDateTime, cancellationToken).ConfigureAwait(false);
        }
        catch (FtpException)
        {
            // no MFMT: the upload time stays
        }

        return true;
    }, cancellationToken);

    public async ValueTask DisposeAsync() => await DisposeClientAsync().ConfigureAwait(false);

    private async Task<T> CallAsync<T>(Func<AsyncFtpClient, Task<T>> call, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            try
            {
                return await call(_client!).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSessionReuseRefused(ex))
            {
                // vsftpd wants the data connection to resume the TLS session; with TLS 1.3 the ticket can come a
                // moment after the control handshake, and a data connection opened before it is refused (522).
                // Nothing was transferred yet, so one more try is safe.
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                return await call(_client!).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not RemoteException)
        {
            throw Wrap(ex);
        }
    }

    private static bool IsSessionReuseRefused(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is FtpCommandException { CompletionCode: "522" })
            {
                return true;
            }
        }

        return false;
    }

    private static Exception Wrap(Exception ex) => ex switch
    {
        // 550: no such file or no permission; FTP does not tell which
        FtpCommandException { CompletionCode: "550" } => new FileNotFoundException(ex.Message, ex),
        FtpException or SocketException or TimeoutException or AuthenticationException or FluentFTP.GnuTLS.GnuTlsException or ObjectDisposedException or InvalidOperationException or IOException
            => new RemoteException(ex.InnerException is { } inner && ex is FtpException ? $"{ex.Message} {inner.Message}" : ex.Message, ex),
        _ => ex,
    };

    private async Task DisposeClientAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
        {
            return;
        }

        try
        {
            if (client.IsConnected)
            {
                await client.Disconnect().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is FtpException or SocketException or IOException or ObjectDisposedException or TimeoutException)
        {
            // already gone
        }

        client.Dispose();
    }
}
