using PlainCommander.Core.Archives;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Remote;

/// <summary>
/// Whole .pce containers between a disk and an S3 server, where a container is a folder of objects
/// (<see cref="PceObjectContainer"/>):
/// <list type="bullet">
/// <item><see cref="Upload"/>: .pce files become containers of objects — the encrypted blocks are sent as they are;</item>
/// <item><see cref="Download"/>: containers of objects become .pce files, the same way back;</item>
/// <item><see cref="Encrypt"/>: files and folders are encrypted straight into a new container on the server.</item>
/// </list>
/// Passwords of existing containers come from <see cref="ArchivePasswords"/> (asked once, on this worker thread).
/// A target that exists is an error for that item, never overwritten.
/// </summary>
public sealed class PceRemoteOperation : IOperation
{
    private enum Mode
    {
        Upload,
        Download,
        Encrypt,
    }

    private readonly RemoteConnections _connections;
    private readonly Mode _mode;
    private readonly IReadOnlyList<string> _sources;
    private readonly string _target;
    private readonly string? _password;
    private readonly PceCompression _compression;
    private readonly PceOptions? _options;
    private string? _newName;

    private PceRemoteOperation(RemoteConnections connections, Mode mode, IReadOnlyList<string> sources, string target, string? password = null, PceCompression compression = PceCompression.None, PceOptions? options = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ArgumentException.ThrowIfNullOrEmpty(target);
        _mode = mode;
        _target = target;
        _password = password;
        _compression = compression;
        _options = options;
    }

    public OperationKind Kind => _mode == Mode.Encrypt ? OperationKind.Encrypt : OperationKind.Copy;

    /// <summary>Each .pce file in <paramref name="files"/> becomes the container "<paramref name="remoteFolder"/>/name.pce".</summary>
    /// <param name="newName">Another name for the container, when there is one file.</param>
    public static PceRemoteOperation Upload(RemoteConnections connections, IReadOnlyList<string> files, string remoteFolder, string? newName = null) =>
        new(connections, Mode.Upload, files, remoteFolder) { _newName = Single(files, newName) };

    /// <summary>Each container folder in <paramref name="containers"/> (full server paths) becomes a .pce file of its name in <paramref name="localFolder"/>.</summary>
    /// <param name="newName">Another name for the file, when there is one container.</param>
    public static PceRemoteOperation Download(RemoteConnections connections, IReadOnlyList<string> containers, string localFolder, string? newName = null) =>
        new(connections, Mode.Download, containers, localFolder) { _newName = Single(containers, newName) };

    private static string? Single(IReadOnlyList<string> sources, string? newName) =>
        string.IsNullOrEmpty(newName) ? null : sources.Count == 1 ? newName : throw new ArgumentException("A new name fits one item only.", nameof(newName));

    /// <summary>Each file or folder in <paramref name="sources"/> is encrypted into its own container "<paramref name="remoteFolder"/>/name.pce".</summary>
    /// <param name="options">Key derivation and block size; null for the defaults.</param>
    public static PceRemoteOperation Encrypt(RemoteConnections connections, IReadOnlyList<string> sources, string remoteFolder, string password, PceCompression compression = PceCompression.Auto, PceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return new PceRemoteOperation(connections, Mode.Encrypt, sources, remoteFolder, password, compression, options);
    }

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var remote = RemotePath.Parse(_mode == Mode.Download ? (_sources.Count > 0 ? _sources[0] : string.Empty) : _target)
            ?? throw new ArgumentException("Not a server path.");
        if (remote.Protocol != RemoteProtocol.S3)
        {
            throw new RemoteException("An encrypted container can be kept as objects on an S3 server only.");
        }

        IRemoteClient? opened = null;
        if (!await runner.RunAsync(remote.Root, async () => opened = await _connections.OpenAsync(remote.Root, runner.Token).ConfigureAwait(false)).ConfigureAwait(false)
            || opened is not S3RemoteClient client)
        {
            if (opened is not null)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        await using (client.ConfigureAwait(false))
        {
            var plan = new List<(string Source, long Bytes)>();
            foreach (var source in _sources)
            {
                long bytes = 0;
                await runner.Run(source, () => bytes = SizeOf(client, source, runner.Token)).ConfigureAwait(false);
                plan.Add((source, bytes));
            }

            var tracker = new ProgressTracker(context.Progress, plan.Sum(p => p.Bytes), plan.Count);
            tracker.Report(force: true);
            foreach (var (source, bytes) in plan)
            {
                runner.Token.ThrowIfCancellationRequested();
                tracker.StartItem(source, bytes);
                var ok = await runner.Run(source, () =>
                {
                    tracker.RestartItem();
                    long reported = 0;
                    void Progress(long total)
                    {
                        tracker.AddBytes(total - reported);
                        reported = total;
                        context.Pause.WaitAsync(runner.Token).GetAwaiter().GetResult();
                    }

                    switch (_mode)
                    {
                        case Mode.Upload:
                            UploadOne(client, source, Progress, runner.Token);
                            break;
                        case Mode.Download:
                            DownloadOne(client, source, Progress, runner.Token);
                            break;
                        default:
                            EncryptOne(client, source, Progress, runner.Token);
                            break;
                    }
                }).ConfigureAwait(false);
                tracker.FinishItem();
                if (ok)
                {
                    runner.AddDone();
                }
            }

            tracker.Report(force: true);
        }
    }

    private long SizeOf(S3RemoteClient client, string source, CancellationToken token)
    {
        if (_mode == Mode.Download)
        {
            return Store(client, source, token).List(PceObjectNames.DataPrefix, int.MaxValue, token).Sum(o => o.Size);
        }

        var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
        if (Directory.Exists(full))
        {
            return _mode == Mode.Encrypt
                ? TreeScanner.Scan(full, followLinks: false, token).Where(i => !i.IsDirectory).Sum(i => i.Size)
                : throw new IOException($"{Path.GetFileName(full)} is a folder, not an encrypted file.");
        }

        return File.Exists(full) ? new FileInfo(full).Length : throw new FileNotFoundException(null, source);
    }

    private static IPceObjectStore Store(S3RemoteClient client, string containerPath, CancellationToken token)
    {
        var location = RemotePath.Parse(containerPath) ?? throw new ArgumentException($"Not a server path: {containerPath}");
        return client.ContainerStoreAsync(location.Inner, token).GetAwaiter().GetResult();
    }

    private string RemoteTarget(string name) => RemotePath.Parse(_target)!.Child(name).FullPath;

    private void UploadOne(S3RemoteClient client, string file, Action<long> progress, CancellationToken token)
    {
        var full = ExactPath.Native(ExactPath.Full(file));
        var target = RemoteTarget(_newName ?? Path.GetFileName(full));
        var store = Store(client, target, token);
        var password = WithPassword(full, p => PceTransfer.ToObjects(full, p, store, _options, null, progress, token));
        ArchivePasswords.Remember(target, password);
    }

    private void DownloadOne(S3RemoteClient client, string container, Action<long> progress, CancellationToken token)
    {
        var folder = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(_target)));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, _newName ?? RemotePath.LocalName(RemotePath.Parse(container)!.Name));
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"{Path.GetFileName(target)} already exists.");
        }

        client.FlushContainerAsync(RemotePath.Parse(container)!.Inner, token).GetAwaiter().GetResult();
        var store = Store(client, container, token);
        var password = WithPassword(container, p => PceTransfer.ToFile(store, p, target, _options, null, progress, token));
        ArchivePasswords.Remember(target, password);
    }

    private void EncryptOne(S3RemoteClient client, string source, Action<long> progress, CancellationToken token)
    {
        var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
        var target = RemoteTarget(Path.GetFileName(full) + PceFormat.Extension);
        var store = Store(client, target, token);
        using (var container = PceObjectContainer.Create(store, _password!, _options))
        {
            try
            {
                container.Add(PceSource.FromPath(full), _compression, progress, token);
            }
            catch
            {
                // A container that did not get its content is not left for the user to find empty.
                try
                {
                    foreach (var item in store.List(string.Empty, int.MaxValue, CancellationToken.None))
                    {
                        store.Delete(item.Name, CancellationToken.None);
                    }
                }
                catch (IOException)
                {
                }

                throw;
            }
        }

        ArchivePasswords.Remember(target, _password!);
    }

    /// <summary>Runs <paramref name="work"/> with the password remembered for <paramref name="container"/>, asking again after a wrong one; returns the one that fit.</summary>
    private static string WithPassword(string container, Action<string> work)
    {
        var wrong = false;
        while (true)
        {
            var password = ArchivePasswords.Get(container, wrong) ?? throw new ArchivePasswordException(container, wrong);
            try
            {
                work(password);
                return password;
            }
            catch (PceWrongPasswordException)
            {
                ArchivePasswords.Forget(container);
                wrong = true;
            }
        }
    }
}
