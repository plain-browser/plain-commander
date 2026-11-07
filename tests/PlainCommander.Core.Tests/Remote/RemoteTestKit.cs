using PlainCommander.Core.Remote;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>A "server" that is a local folder: server path "/a/b" is Root/a/b. Checks the password and the host key like a real one.</summary>
internal sealed class FolderRemoteClient : IRemoteClient
{
    private readonly string _root;
    private readonly string _password;
    private readonly RemoteCredentials _credentials;
    private readonly Func<HostIdentity, bool> _verify;
    private readonly HostIdentity _identity;
    private readonly FolderServer _server;

    public FolderRemoteClient(FolderServer server, string root, string password, RemoteSite site, RemoteCredentials credentials, Func<HostIdentity, bool> verify, string fingerprint)
    {
        _server = server;
        _root = root;
        _password = password;
        _credentials = credentials;
        _verify = verify;
        _identity = new HostIdentity(site.Host, site.EffectivePort, "ssh-ed25519 256", fingerprint);
    }

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        _server.Connects++;
        if (!_verify(_identity))
        {
            throw new RemoteException("Host key rejected.") { IsUntrusted = true };
        }

        if (_credentials.Secret != _password)
        {
            throw new RemoteException("Permission denied (password).") { IsAuthentication = true };
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task<string> GetHomeAsync(CancellationToken cancellationToken) => Task.FromResult("/home");

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        Check();
        var dir = new DirectoryInfo(Local(path));
        if (!dir.Exists)
        {
            throw new FileNotFoundException(null, path);
        }

        return Task.FromResult<IReadOnlyList<RemoteItem>>(dir.EnumerateFileSystemInfos().Select(Item).ToList());
    }

    public Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        Check();
        var local = Local(path);
        FileSystemInfo? info = Directory.Exists(local) ? new DirectoryInfo(local) : File.Exists(local) ? new FileInfo(local) : null;
        return Task.FromResult(info is null ? null : Item(info));
    }

    public async Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken cancellationToken) =>
        (await StatAsync(path, cancellationToken))?.Modified;

    public async Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken)
    {
        Check();
        await using var input = File.OpenRead(Local(path));
        input.Seek(offset, SeekOrigin.Begin);
        _server.Offsets.Add(offset);
        await CopyAsync(input, destination, cancellationToken);
    }

    public async Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken)
    {
        Check();
        await using var output = new FileStream(Local(path), append ? FileMode.Append : FileMode.Create, FileAccess.Write);
        _server.Offsets.Add(append ? output.Length : 0);
        await CopyAsync(source, output, cancellationToken);
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        Check();
        File.Delete(Local(path));
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Check();
        Directory.Delete(Local(path));
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        Check();
        Directory.CreateDirectory(Local(path));
        return Task.CompletedTask;
    }

    public Task RenameAsync(string source, string target, CancellationToken cancellationToken)
    {
        Check();
        var from = Local(source);
        if (Directory.Exists(from))
        {
            Directory.Move(from, Local(target));
        }
        else
        {
            File.Move(from, Local(target));
        }

        return Task.CompletedTask;
    }

    public Task SetModeAsync(string path, int mode, CancellationToken cancellationToken)
    {
        Check();
        if (!File.Exists(Local(path)) && !Directory.Exists(Local(path)))
        {
            throw new RemoteException("No such file.");
        }

        _server.Modes[RemotePath.NormalizeInner(path)] = mode;
        return Task.CompletedTask;
    }

    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        Check();
        File.SetLastWriteTimeUtc(Local(path), modified.UtcDateTime);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>Copies in small blocks; breaks the connection after <see cref="FolderServer.BreakAfter"/> bytes, once.</summary>
    private async Task CopyAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        long copied = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (_server.BreakAfter is { } limit && copied + read > limit)
            {
                await to.WriteAsync(buffer.AsMemory(0, (int)(limit - copied)), cancellationToken);
                _server.Breaks--;
                if (_server.Breaks <= 0)
                {
                    _server.BreakAfter = null;
                }

                IsConnected = false;
                throw new RemoteException("Connection reset by peer.");
            }

            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
        }
    }

    private static RemoteItem Item(FileSystemInfo info) => new(
        info.Name,
        info is DirectoryInfo,
        info is FileInfo f ? f.Length : 0,
        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));

    // The exact form, so a server file named like a Windows device ("CON") is a file here too.
    private string Local(string path) => PlainCommander.Core.FileSystem.ExactPath.Native(Path.Combine(_root, RemotePath.NormalizeInner(path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Like the real clients: a lost connection is reopened on the next call.</summary>
    private void Check()
    {
        if (!IsConnected)
        {
            ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        if (_server.DropNextCalls > 0)
        {
            _server.DropNextCalls--;
            IsConnected = false;
            throw new RemoteException("Connection reset by peer.");
        }
    }
}

/// <summary>Answers login and host key questions from a script and records them.</summary>
internal sealed class ScriptedRemotePrompts : IRemotePrompts
{
    public Queue<CredentialAnswer?> Answers { get; } = new();

    public TrustChoice Trust { get; set; } = TrustChoice.Once;

    public List<(HostIdentity Identity, string? Known)> HostQuestions { get; } = [];

    public List<string?> CredentialProblems { get; } = [];

    public Task<TrustChoice> ConfirmHostAsync(HostIdentity identity, string? known, CancellationToken cancellationToken)
    {
        HostQuestions.Add((identity, known));
        return Task.FromResult(Trust);
    }

    public Task<CredentialAnswer?> AskCredentialsAsync(RemoteSite site, bool passphrase, string? problem, CancellationToken cancellationToken)
    {
        CredentialProblems.Add(problem);
        return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
    }
}

internal sealed class MemorySecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public string? Read(string key) => Values.GetValueOrDefault(key);

    public void Write(string key, string secret) => Values[key] = secret;

    public void Delete(string key) => Values.Remove(key);
}

/// <summary>A connection pool whose servers are folders of one sandbox.</summary>
internal sealed class FolderServer : IAsyncDisposable
{
    public const string Root = "sftp://tester@test.local:22";
    public const string Password = "secret";

    public FolderServer(string folder, string fingerprint = "SHA256:abc")
    {
        Folder = folder;
        Connections = new RemoteConnections(Prompts, Secrets, Trusted, () => [],
            (site, credentials, verify) => new FolderRemoteClient(this, folder, Password, site, credentials, verify, fingerprint));
        Secrets.Values[RemoteConnections.SecretKey(Root)] = Password;
        Prompts.Trust = TrustChoice.Always;
    }

    public string Folder { get; }

    /// <summary>Makes the next calls fail as a dropped connection would.</summary>
    public int DropNextCalls { get; set; }

    public int Connects { get; set; }

    /// <summary>chmod calls by server path (the folder underneath has no Unix modes on Windows).</summary>
    public Dictionary<string, int> Modes { get; } = [];

    /// <summary>A transfer breaks (connection lost) after this many bytes of the attempt, <see cref="Breaks"/> times.</summary>
    public long? BreakAfter { get; set; }

    public int Breaks { get; set; } = 1;

    /// <summary>Where each download started and how much each upload found already there.</summary>
    public List<long> Offsets { get; } = [];

    public ScriptedRemotePrompts Prompts { get; } = new();

    public MemorySecrets Secrets { get; } = new();

    public Dictionary<string, string> Trusted { get; } = [];

    public RemoteConnections Connections { get; }

    public static string At(string inner) => Root + inner;

    public ValueTask DisposeAsync() => Connections.DisposeAsync();
}
