using System.Collections.Concurrent;
using Serilog;

namespace PlainCommander.Core.Remote;

/// <summary>
/// Open servers, one entry per root ("sftp://user@host:22"). Each keeps the login (asked once, or read from the
/// credential store) and one browsing connection the panels share, used one call at a time. Operations get their
/// own connection (<see cref="OpenAsync"/>) so a long copy does not freeze browsing. Host keys and certificates are
/// checked against the remembered fingerprints; an unknown or changed one goes to the user.
/// </summary>
public sealed class RemoteConnections : IAsyncDisposable
{
    private const int MaxLoginAttempts = 3;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<RemoteSite, RemoteCredentials, Func<HostIdentity, bool>, IRemoteClient> _factory;
    private readonly IRemotePrompts _prompts;
    private readonly ISecretStore _secrets;
    private readonly IDictionary<string, string> _trusted;
    private readonly Func<IEnumerable<RemoteSite>> _sites;
    private readonly Lock _trustLock = new();

    /// <summary>"This time only" answers: good until the app closes, never saved.</summary>
    private readonly Dictionary<string, string> _trustedForSession = new(StringComparer.Ordinal);

    /// <param name="trustedHosts">Remembered fingerprints by "host:port"; changed in place when the user trusts a server.</param>
    /// <param name="sites">The saved sites, to find a typed address's settings (key file, active mode).</param>
    /// <param name="factory">Creates a client; tests pass fakes.</param>
    public RemoteConnections(
        IRemotePrompts prompts,
        ISecretStore secrets,
        IDictionary<string, string> trustedHosts,
        Func<IEnumerable<RemoteSite>> sites,
        Func<RemoteSite, RemoteCredentials, Func<HostIdentity, bool>, IRemoteClient>? factory = null)
    {
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _trusted = trustedHosts ?? throw new ArgumentNullException(nameof(trustedHosts));
        _sites = sites ?? throw new ArgumentNullException(nameof(sites));
        _factory = factory ?? CreateClient;
    }

    /// <summary>Roots with a live login, for the drive menu.</summary>
    public IReadOnlyList<string> OpenRoots => _sessions.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IRemoteClient CreateClient(RemoteSite site, RemoteCredentials credentials, Func<HostIdentity, bool> verify)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Protocol switch
        {
            RemoteProtocol.Sftp => new SftpRemoteClient(site, credentials, verify),
            RemoteProtocol.S3 => new S3RemoteClient(site, credentials),
            _ => new FtpRemoteClient(site, credentials, verify),
        };
    }

    public static string SecretKey(string root) => "PlainCommander:" + root;

    public bool IsOpen(string root) => _sessions.TryGetValue(root, out var s) && s.Browser is not null;

    /// <summary>Runs <paramref name="call"/> on the shared browsing connection, logging in first when needed.</summary>
    public async Task<T> UseAsync<T>(string root, Func<IRemoteClient, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var session = SessionFor(root);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.Browser ??= await LoginAsync(session, cancellationToken).ConfigureAwait(false);
            try
            {
                return await call(session.Browser).ConfigureAwait(false);
            }
            catch (RemoteException) when (!session.Browser.IsConnected)
            {
                // the server dropped the idle connection: once more on a fresh one
                return await call(session.Browser).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>The server's start folder, asked once per login.</summary>
    public Task<string> HomeAsync(string root, CancellationToken cancellationToken) =>
        UseAsync(root, async c => SessionFor(root).Home ??= RemotePath.NormalizeInner(await c.GetHomeAsync(cancellationToken).ConfigureAwait(false)), cancellationToken);

    /// <summary>A separate, connected client for an operation; the caller disposes it.</summary>
    public async Task<IRemoteClient> OpenAsync(string root, CancellationToken cancellationToken)
    {
        var session = SessionFor(root);
        if (session.Credentials is null)
        {
            // the first login asks the questions; later ones reuse its answers
            await UseAsync(root, _ => Task.FromResult(true), cancellationToken).ConfigureAwait(false);
        }

        var client = _factory(session.Site, session.Credentials ?? RemoteCredentials.None, identity => Verify(identity, CancellationToken.None));
        try
        {
            await Task.Run(() => client.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes the browsing connection and forgets the in-memory login (the stored password stays).</summary>
    public async Task DisconnectAsync(string root)
    {
        if (_sessions.TryRemove(root, out var session) && session.Browser is { } browser)
        {
            await browser.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Removes a remembered password, e.g. when a saved site is deleted.</summary>
    public void ForgetPassword(string root) => _secrets.Delete(SecretKey(root));

    public async ValueTask DisposeAsync()
    {
        foreach (var root in _sessions.Keys.ToArray())
        {
            await DisconnectAsync(root).ConfigureAwait(false);
        }
    }

    private Session SessionFor(string root)
    {
        var location = RemotePath.Parse(root) ?? throw new ArgumentException($"Not a remote path: {root}", nameof(root));
        return _sessions.GetOrAdd(location.Root, key =>
        {
            var saved = _sites().FirstOrDefault(s => string.Equals(s.Root, key, StringComparison.OrdinalIgnoreCase));
            return new Session(saved?.Clone() ?? RemoteSite.FromLocation(location));
        });
    }

    private async Task<IRemoteClient> LoginAsync(Session session, CancellationToken cancellationToken)
    {
        var site = session.Site;
        var usesKey = site.Protocol == RemoteProtocol.Sftp && !string.IsNullOrWhiteSpace(site.KeyFile);
        var anonymous = site.Protocol != RemoteProtocol.Sftp && string.IsNullOrEmpty(site.User);
        if (site.Protocol == RemoteProtocol.Sftp && string.IsNullOrEmpty(site.User))
        {
            throw new RemoteException("SFTP needs a user name: sftp://user@host.");
        }

        var key = SecretKey(site.Root);
        var credentials = session.Credentials;
        var remember = false;
        if (credentials is null && !anonymous)
        {
            var stored = _secrets.Read(key);
            if (stored is not null)
            {
                credentials = new RemoteCredentials(stored);
            }
            else if (usesKey)
            {
                credentials = RemoteCredentials.None; // try the key without a passphrase first
            }
        }

        string? problem = null;
        for (var attempt = 0; ; attempt++)
        {
            if (credentials is null)
            {
                var answer = await _prompts.AskCredentialsAsync(site, usesKey, problem, cancellationToken).ConfigureAwait(false)
                    ?? throw new RemoteException("Login cancelled.") { IsCancelled = true };
                credentials = new RemoteCredentials(answer.Secret);
                remember = answer.Remember;
            }

            var client = _factory(site, credentials, identity => Verify(identity, cancellationToken));
            try
            {
                // off the calling thread: the host key check inside blocks on a dialog
                await Task.Run(() => client.ConnectAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteException ex) when (ex.IsAuthentication && !anonymous && attempt < MaxLoginAttempts)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                Log.Information("Login to {Root} refused: {Message}", site.Root, ex.Message);
                problem = ex.Message;
                credentials = null;
                continue;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            session.Credentials = credentials;
            if (remember && !string.IsNullOrEmpty(credentials.Secret))
            {
                _secrets.Write(key, credentials.Secret);
            }
            else if (problem is not null && _secrets.Read(key) is not null && !remember)
            {
                _secrets.Delete(key); // the stored one was wrong and the user chose not to keep the new one
            }

            Log.Information("Connected to {Root}", site.Root);
            return client;
        }
    }

    /// <summary>Called by the library during connect (a background thread): known → yes, otherwise the user decides.</summary>
    private bool Verify(HostIdentity identity, CancellationToken cancellationToken)
    {
        string? known;
        lock (_trustLock)
        {
            _trusted.TryGetValue(identity.Key, out known);
            if (string.Equals(known, identity.Fingerprint, StringComparison.Ordinal)
                || (_trustedForSession.TryGetValue(identity.Key, out var once) && string.Equals(once, identity.Fingerprint, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        var choice = _prompts.ConfirmHostAsync(identity, known, cancellationToken).GetAwaiter().GetResult();
        lock (_trustLock)
        {
            switch (choice)
            {
                case TrustChoice.Always:
                    _trusted[identity.Key] = identity.Fingerprint;
                    break;
                case TrustChoice.Once:
                    // operations open their own connections; "this time" covers them too, until the app closes
                    _trustedForSession[identity.Key] = identity.Fingerprint;
                    break;
            }
        }

        return choice != TrustChoice.Reject;
    }

    private sealed class Session(RemoteSite site)
    {
        public RemoteSite Site { get; } = site;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public RemoteCredentials? Credentials { get; set; }

        public IRemoteClient? Browser { get; set; }

        public string? Home { get; set; }
    }
}
