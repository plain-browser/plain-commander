using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PlainCommander.Core.Remote;

/// <summary>SFTP through SSH.NET: password (also keyboard-interactive) or private key login, host key check on connect.</summary>
internal sealed class SftpRemoteClient : IRemoteClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly RemoteSite _site;
    private readonly RemoteCredentials _credentials;
    private readonly Func<HostIdentity, bool> _verify;
    private SftpClient? _client;
    private bool _rejected;

    public SftpRemoteClient(RemoteSite site, RemoteCredentials credentials, Func<HostIdentity, bool> verify)
    {
        _site = site;
        _credentials = credentials;
        _verify = verify;
    }

    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        DisposeClient();
        _rejected = false;
        var host = _site.Host.Trim();
        var port = _site.EffectivePort;
        var info = new ConnectionInfo(host, port, _site.User, Methods()) { Timeout = Timeout };
        var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
        client.HostKeyReceived += (_, e) =>
        {
            var trusted = _verify(new HostIdentity(host.ToLowerInvariant(), port, $"{e.HostKeyName} {e.KeyLength}", "SHA256:" + e.FingerPrintSHA256));
            _rejected = !trusted;
            e.CanTrust = trusted;
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _client = client;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            client.Dispose();
            throw ex switch
            {
                _ when _rejected => new RemoteException(ex.Message, ex) { IsUntrusted = true },
                SshAuthenticationException or SshPassPhraseNullOrEmptyException => new RemoteException(ex.Message, ex) { IsAuthentication = true },
                InvalidOperationException when !string.IsNullOrEmpty(_site.KeyFile) => new RemoteException(ex.Message, ex) { IsAuthentication = true },
                _ => Wrap(ex),
            };
        }
    }

    public Task<string> GetHomeAsync(CancellationToken cancellationToken) => CallAsync(c => Task.FromResult(c.WorkingDirectory), cancellationToken);

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) => CallAsync<IReadOnlyList<RemoteItem>>(async c =>
    {
        var items = new List<RemoteItem>();
        await foreach (var file in c.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (file.Name is "." or "..")
            {
                continue;
            }

            var isDirectory = file.IsDirectory;
            var size = file.Length;
            var modified = file.LastWriteTimeUtc;
            if (file.IsSymbolicLink)
            {
                try
                {
                    var target = await c.GetAttributesAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                    isDirectory = target.IsDirectory;
                    size = target.Size;
                    modified = target.LastWriteTimeUtc;
                }
                catch (SshException)
                {
                    // a dangling link stays a small file
                }
            }

            items.Add(new RemoteItem(file.Name, isDirectory, isDirectory ? 0 : size, Utc(modified), file.IsSymbolicLink, ModeOf(file.Attributes)));
        }

        return items;
    }, cancellationToken);

    public Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        try
        {
            var a = await c.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return new RemoteItem(RemotePath.NameOf(path), a.IsDirectory, a.IsDirectory ? 0 : a.Size, Utc(a.LastWriteTimeUtc), Mode: ModeOf(a));
        }
        catch (SftpPathNotFoundException)
        {
            return (RemoteItem?)null;
        }
    }, cancellationToken);

    public async Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken cancellationToken) =>
        (await StatAsync(path, cancellationToken).ConfigureAwait(false))?.Modified;

    public Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var remote = await c.OpenAsync(path, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
        await using (remote.ConfigureAwait(false))
        {
            if (offset > 0)
            {
                remote.Seek(offset, SeekOrigin.Begin);
            }

            await remote.CopyToAsync(destination, 1 << 16, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }, cancellationToken);

    public Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var remote = await c.OpenAsync(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, cancellationToken).ConfigureAwait(false);
        await using (remote.ConfigureAwait(false))
        {
            await source.CopyToAsync(remote, 1 << 16, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }, cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.DeleteFileAsync(path, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.DeleteDirectoryAsync(path, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.CreateDirectoryAsync(path, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task RenameAsync(string source, string target, CancellationToken cancellationToken) =>
        CallAsync(async c => { await c.RenameFileAsync(source, target, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    public Task SetModeAsync(string path, int mode, CancellationToken cancellationToken) => CallAsync(c =>
    {
        c.ChangePermissions(path, (short)UnixMode.ToDigits(mode)); // SSH.NET wants the octal digits, e.g. 755
        return Task.FromResult(true);
    }, cancellationToken);

    /// <summary>The permission bits SSH.NET spreads over flags.</summary>
    private static int ModeOf(Renci.SshNet.Sftp.SftpFileAttributes a) =>
        (a.IsUIDBitSet ? 0x800 : 0) | (a.IsGroupIDBitSet ? 0x400 : 0) | (a.IsStickyBitSet ? 0x200 : 0)
        | (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0)
        | (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0)
        | (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0);

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) => CallAsync(c =>
    {
        try
        {
            c.SetLastWriteTimeUtc(path, modified.UtcDateTime);
        }
        catch (SshException)
        {
            // some servers refuse setstat; the file itself is fine
        }

        return Task.FromResult(true);
    }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        DisposeClient();
        return ValueTask.CompletedTask;
    }

    private AuthenticationMethod[] Methods()
    {
        if (!string.IsNullOrWhiteSpace(_site.KeyFile))
        {
            PrivateKeyFile key;
            try
            {
                key = string.IsNullOrEmpty(_credentials.Secret) ? new PrivateKeyFile(_site.KeyFile) : new PrivateKeyFile(_site.KeyFile, _credentials.Secret);
            }
            catch (SshPassPhraseNullOrEmptyException ex)
            {
                throw new RemoteException(ex.Message, ex) { IsAuthentication = true };
            }
            catch (SshException ex)
            {
                // a wrong passphrase shows up as an unreadable key
                throw new RemoteException(ex.Message, ex) { IsAuthentication = !string.IsNullOrEmpty(_credentials.Secret) || ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) };
            }

            return [new PrivateKeyAuthenticationMethod(_site.User, key)];
        }

        var password = new PasswordAuthenticationMethod(_site.User, _credentials.Secret);
        var interactive = new KeyboardInteractiveAuthenticationMethod(_site.User);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = _credentials.Secret;
            }
        };
        return [password, interactive];
    }

    private async Task<T> CallAsync<T>(Func<SftpClient, Task<T>> call, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await call(_client!).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not RemoteException)
        {
            throw ex is SftpPathNotFoundException ? new FileNotFoundException(ex.Message, ex) : Wrap(ex);
        }
    }

    private static Exception Wrap(Exception ex) => ex switch
    {
        SftpPermissionDeniedException => new UnauthorizedAccessException(ex.Message, ex),
        SshException or SocketException or ObjectDisposedException or InvalidOperationException or TimeoutException or IOException => new RemoteException(ex.Message, ex),
        _ => ex,
    };

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private void DisposeClient()
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
                client.Disconnect();
            }
        }
        catch (Exception ex) when (ex is SshException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // already gone
        }

        client.Dispose();
    }
}
