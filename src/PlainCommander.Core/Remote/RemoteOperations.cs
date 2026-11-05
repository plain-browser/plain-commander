using System.Buffers;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Remote;

/// <summary>Conflict questions for the remote operations: one at a time, "… all" answers stick.</summary>
internal sealed class ConflictGate
{
    private const int None = -1;
    private int _sticky = None;

    /// <param name="preset">An answer for every conflict, given up front (synchronize: the direction was chosen).</param>
    public ConflictGate(ConflictChoice? preset = null)
    {
        if (preset is { } choice)
        {
            _sticky = (int)choice;
        }
    }

    public async Task<ConflictChoice> AskAsync(OperationContext context, StepRunner runner, ConflictInfo conflict)
    {
        if (Sticky() is { } early)
        {
            return early;
        }

        await runner.PromptGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            if (Sticky() is { } late)
            {
                return late;
            }

            var choice = await context.Prompts.ResolveConflictAsync(conflict, context.CancellationToken).ConfigureAwait(false);
            if (choice is ConflictChoice.OverwriteAll or ConflictChoice.OverwriteAllOlder or ConflictChoice.SkipAll or ConflictChoice.KeepBothAll)
            {
                Volatile.Write(ref _sticky, (int)choice);
            }

            return choice;
        }
        finally
        {
            runner.PromptGate.Release();
        }
    }

    private ConflictChoice? Sticky()
    {
        var value = Volatile.Read(ref _sticky);
        return value == None ? null : (ConflictChoice)value;
    }
}

/// <summary>What to do with a file that is already at the target.</summary>
internal enum ConflictAction
{
    Write,
    WriteFreeName,
    Skip,
}

internal static class ConflictRules
{
    public static ConflictAction Decide(ConflictChoice choice, DateTimeOffset source, DateTimeOffset target) => choice switch
    {
        ConflictChoice.Overwrite or ConflictChoice.OverwriteAll => ConflictAction.Write,
        ConflictChoice.OverwriteAllOlder => target < source ? ConflictAction.Write : ConflictAction.Skip,
        ConflictChoice.KeepBoth or ConflictChoice.KeepBothAll => ConflictAction.WriteFreeName,
        ConflictChoice.Skip or ConflictChoice.SkipAll => ConflictAction.Skip,
        _ => throw new OperationAbortedException("Cancelled at a conflict."),
    };
}

/// <summary>
/// Continuing broken transfers. A file travels as "name.part" and keeps that name until it is complete, so a part
/// left by a dropped connection, a cancel or a crash is ours: the next attempt (Retry, the same F5 later) adds only
/// what is missing. A dropped connection in the middle of a file is retried right away a few times, each attempt
/// continuing where the last one stopped, before the user is asked.
/// </summary>
internal static class Resume
{
    /// <summary>Automatic attempts after a break, as long as each one moved the file forward.</summary>
    public const int AutoRetries = 3;

    public static TimeSpan Pause { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How much of an existing part can be kept: it must be shorter than the source and written after the source
    /// last changed (otherwise it may hold an older version). Zero means start over.
    /// </summary>
    public static long Usable(long partSize, DateTimeOffset partWritten, long sourceSize, DateTimeOffset sourceModified) =>
        partSize > 0 && partSize < sourceSize && (sourceModified == DateTimeOffset.MinValue || partWritten == DateTimeOffset.MinValue || partWritten >= sourceModified)
            ? partSize
            : 0;

    /// <summary>A break worth an automatic retry: the connection failed after this attempt moved some bytes.</summary>
    public static bool ShouldRetry(Exception exception, long moved, int attempt, CancellationToken token) =>
        exception is RemoteException { IsAuthentication: false, IsCancelled: false, IsUntrusted: false }
        && moved > 0 && attempt <= AutoRetries && !token.IsCancellationRequested;

    /// <summary>Runs attempts until one completes; an attempt reports the bytes it moved through <c>moved</c>.</summary>
    public static async Task RunAsync(string what, Func<Action<long>, Task> attempt, CancellationToken token)
    {
        for (var number = 1; ; number++)
        {
            long moved = 0;
            try
            {
                await attempt(m => moved = m).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ShouldRetry(ex, moved, number, token))
            {
                Serilog.Log.Information(ex, "{What}: broke after {Moved} bytes, continuing (attempt {Attempt})", what, moved, number + 1);
                await Task.Delay(Pause * number, token).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Counts bytes for the progress window and honours pause/cancel between blocks, in either direction.</summary>
internal sealed class TransferStream(Stream inner, ProgressTracker tracker, StepRunner runner) : Stream
{
    /// <summary>Bytes that went through this stream (this attempt only).</summary>
    public long Transferred { get; private set; }

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        runner.Token.ThrowIfCancellationRequested();
        var read = inner.Read(buffer, offset, count);
        Transferred += read;
        tracker.AddBytes(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await runner.CheckpointAsync().ConfigureAwait(false);
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Transferred += read;
        tracker.AddBytes(read);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        runner.Token.ThrowIfCancellationRequested();
        inner.Write(buffer, offset, count);
        Transferred += count;
        tracker.AddBytes(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await runner.CheckpointAsync().ConfigureAwait(false);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Transferred += buffer.Length;
        tracker.AddBytes(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);
}

/// <summary>
/// F5/F6 from a server panel to disk. Folders come down with everything below; each file is written to "name.part"
/// and renamed when complete, so a broken transfer never leaves a truncated file under the real name. Times are kept.
/// F6 deletes each server file once its copy is safe, and the server folders when they end up empty.
/// </summary>
public sealed class RemoteDownloadOperation : IOperation
{
    private readonly RemoteConnections _connections;
    private readonly IReadOnlyList<string> _sources;
    private readonly TransferTarget _target;
    private readonly bool _move;
    private readonly ConflictGate _conflicts;

    /// <param name="presetConflict">An answer for every conflict (synchronize uses OverwriteAll).</param>
    public RemoteDownloadOperation(RemoteConnections connections, IReadOnlyList<string> sources, TransferTarget target, bool move = false, ConflictChoice? presetConflict = null)
    {
        _conflicts = new ConflictGate(presetConflict);
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _move = move;
    }

    public OperationKind Kind => _move ? OperationKind.Move : OperationKind.Copy;

    private sealed record Item(RemoteLocation Source, string Destination, bool IsDirectory, long Size, DateTimeOffset Modified);

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    /// <summary>F3/F4 on a server file: a copy in a fresh temp folder; the caller opens it.</summary>
    public static async Task<string> ToTempAsync(RemoteConnections connections, string virtualPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var location = RemotePath.Parse(virtualPath) ?? throw new ArgumentException($"Not a remote path: {virtualPath}", nameof(virtualPath));
        var dir = Path.Combine(Path.GetTempPath(), "PlainCommander", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var destination = Path.Combine(dir, location.Name);
        var client = await connections.OpenAsync(location.Root, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            await using (output.ConfigureAwait(false))
            {
                await client.DownloadAsync(location.Inner, output, 0, cancellationToken).ConfigureAwait(false);
            }

            if (await client.StatAsync(location.Inner, cancellationToken).ConfigureAwait(false) is { Modified: var modified } && modified > DateTimeOffset.MinValue)
            {
                File.SetLastWriteTimeUtc(destination, modified.UtcDateTime);
            }
        }

        return destination;
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var locations = _sources.Select(s => RemotePath.Parse(s) ?? throw new InvalidOperationException($"Not a remote path: {s}")).ToList();
        if (locations.Count == 0)
        {
            return;
        }

        IRemoteClient? opened = null;
        if (!await runner.RunAsync(locations[0].Root, async () => opened = await _connections.OpenAsync(locations[0].Root, runner.Token).ConfigureAwait(false)).ConfigureAwait(false))
        {
            return;
        }

        var client = opened!;
        await using (client.ConfigureAwait(false))
        {
            var items = new List<Item>();
            foreach (var location in locations)
            {
                await runner.RunAsync(location.FullPath, async () => await PlanAsync(client, location, items, runner.Token).ConfigureAwait(false)).ConfigureAwait(false);
            }

            var files = items.Where(i => !i.IsDirectory).ToList();
            var tracker = new ProgressTracker(context.Progress, files.Sum(i => i.Size), items.Count);
            tracker.Report(force: true);
            var failedFolders = new HashSet<string>(StringComparer.Ordinal);

            foreach (var folder in items.Where(i => i.IsDirectory))
            {
                var ok = await runner.Run(folder.Source.FullPath, () =>
                {
                    var native = ExactPath.Native(folder.Destination);
                    if (File.Exists(native))
                    {
                        throw new OperationRuleException(RuleViolation.TypeMismatch, folder.Destination);
                    }

                    Directory.CreateDirectory(native);
                }).ConfigureAwait(false);
                tracker.CompleteSmallItem(folder.Source.FullPath, 0);
                if (ok)
                {
                    runner.AddDone();
                }
                else
                {
                    failedFolders.Add(folder.Source.Inner);
                }
            }

            foreach (var file in files)
            {
                var done = await DownloadOneAsync(context, runner, tracker, client, file).ConfigureAwait(false);
                if (!done)
                {
                    failedFolders.Add(RemotePath.ParentInner(file.Source.Inner));
                    continue;
                }

                if (_move)
                {
                    await runner.RunAsync(file.Source.FullPath, () => client.DeleteFileAsync(file.Source.Inner, runner.Token)).ConfigureAwait(false);
                }
            }

            if (_move)
            {
                // deepest first; a folder that kept something (skipped, failed) stays
                foreach (var folder in items.Where(i => i.IsDirectory).OrderByDescending(i => i.Source.Inner.Length))
                {
                    if (failedFolders.Any(f => f == folder.Source.Inner || f.StartsWith(folder.Source.Inner + "/", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    try
                    {
                        await client.DeleteDirectoryAsync(folder.Source.Inner, runner.Token).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // not empty after all: leave it
                    }
                }
            }

            tracker.Report(force: true);
        }
    }

    private async Task PlanAsync(IRemoteClient client, RemoteLocation location, List<Item> items, CancellationToken token)
    {
        var stat = await client.StatAsync(location.Inner, token).ConfigureAwait(false) ?? throw new FileNotFoundException(null, location.FullPath);
        var top = Path.Combine(_target.Directory, _target.NewName ?? location.Name);
        if (!stat.IsDirectory)
        {
            items.Add(new Item(location, top, false, stat.Size, stat.Modified));
            return;
        }

        items.Add(new Item(location, top, true, 0, stat.Modified));
        var pending = new Queue<(RemoteLocation Folder, string Local)>();
        pending.Enqueue((location, top));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (folder, local) = pending.Dequeue();
            foreach (var child in await client.ListAsync(folder.Inner, token).ConfigureAwait(false))
            {
                var childLocation = folder.Child(child.Name);
                var childLocal = Path.Combine(local, child.Name);
                items.Add(new Item(childLocation, childLocal, child.IsDirectory, child.Size, child.Modified));
                if (child.IsDirectory && !child.IsLink)
                {
                    pending.Enqueue((childLocation, childLocal));
                }
            }
        }
    }

    private async Task<bool> DownloadOneAsync(OperationContext context, StepRunner runner, ProgressTracker tracker, IRemoteClient client, Item file)
    {
        var skipped = false;
        tracker.StartItem(file.Source.FullPath, file.Size);
        var ok = await runner.RunAsync(file.Source.FullPath, async () =>
        {
            tracker.RestartItem();
            var destination = ExactPath.Native(file.Destination);
            if (Directory.Exists(destination))
            {
                throw new OperationRuleException(RuleViolation.TypeMismatch, file.Destination);
            }

            if (File.Exists(destination))
            {
                var existing = new FileInfo(destination);
                var choice = await _conflicts.AskAsync(context, runner, new ConflictInfo(
                    file.Source.FullPath, file.Size, file.Modified, ExactPath.Display(destination), existing.Length, existing.LastWriteTimeUtc)).ConfigureAwait(false);
                switch (ConflictRules.Decide(choice, file.Modified, existing.LastWriteTimeUtc))
                {
                    case ConflictAction.Skip:
                        skipped = true;
                        return;
                    case ConflictAction.WriteFreeName:
                        destination = PathRules.ReserveFreeName(destination);
                        break;
                    default:
                        PathRules.ClearReadOnly(destination);
                        break;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var part = destination + ".part";
            await Resume.RunAsync(file.Source.FullPath, async moved =>
            {
                var existing = new FileInfo(part);
                var offset = existing.Exists
                    ? Resume.Usable(existing.Length, new DateTimeOffset(existing.LastWriteTimeUtc, TimeSpan.Zero), file.Size, file.Modified)
                    : 0;
                tracker.RestartItem(offset);
                if (offset > 0)
                {
                    Serilog.Log.Information("{Part}: continuing after {Offset} bytes", part, offset);
                }

                try
                {
                    var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                    await using (output.ConfigureAwait(false))
                    {
                        var counting = new TransferStream(output, tracker, runner);
                        await using (counting.ConfigureAwait(false))
                        {
                            try
                            {
                                await client.DownloadAsync(file.Source.Inner, counting, offset, runner.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                moved(counting.Transferred);
                            }
                        }
                    }
                }
                catch
                {
                    DropIfEmpty(part); // a part with data stays for the next attempt
                    throw;
                }
            }, runner.Token).ConfigureAwait(false);

            File.Move(part, destination, overwrite: true);

            var modified = await client.GetModifiedAsync(file.Source.Inner, runner.Token).ConfigureAwait(false) ?? file.Modified;
            if (modified > DateTimeOffset.MinValue)
            {
                try
                {
                    File.SetLastWriteTimeUtc(destination, modified.UtcDateTime);
                }
                catch (IOException)
                {
                    // the content is what matters
                }
            }
        }).ConfigureAwait(false);

        tracker.FinishItem();
        if (skipped)
        {
            runner.AddSkipped();
            return false;
        }

        if (ok)
        {
            runner.AddDone();
        }

        return ok;
    }

    private static void DropIfEmpty(string path)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: 0 })
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // a leftover .part is better than hiding the real error
        }
    }
}

/// <summary>
/// F5/F6 from disk into a server panel. Uploads go to "name.part" and are renamed when complete; the remote time is
/// set to the local one when the server allows it. F6 deletes each local file once it is safely on the server.
/// </summary>
public sealed class RemoteUploadOperation : IOperation
{
    private readonly RemoteConnections _connections;
    private readonly IReadOnlyList<string> _sources;
    private readonly RemoteLocation _folder;
    private readonly string? _newName;
    private readonly bool _move;
    private readonly ConflictGate _conflicts;

    /// <param name="targetFolder">The server folder (virtual path) the sources go into.</param>
    /// <param name="newName">One source only: its name on the server.</param>
    /// <param name="presetConflict">An answer for every conflict (synchronize uses OverwriteAll).</param>
    public RemoteUploadOperation(RemoteConnections connections, IReadOnlyList<string> sources, string targetFolder, string? newName = null, bool move = false, ConflictChoice? presetConflict = null)
    {
        _conflicts = new ConflictGate(presetConflict);
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _folder = RemotePath.Parse(targetFolder) ?? throw new ArgumentException($"Not a remote path: {targetFolder}", nameof(targetFolder));
        _newName = newName;
        _move = move;
    }

    public OperationKind Kind => _move ? OperationKind.Move : OperationKind.Copy;

    private sealed record Item(PlannedItem Local, string Inner, DateTimeOffset Modified);

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    /// <summary>F4 on a server file: the edited temp copy goes back where it came from.</summary>
    public static async Task UploadFileAsync(RemoteConnections connections, string localPath, string virtualPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var location = RemotePath.Parse(virtualPath) ?? throw new ArgumentException($"Not a remote path: {virtualPath}", nameof(virtualPath));
        var client = await connections.OpenAsync(location.Root, cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
            await using (input.ConfigureAwait(false))
            {
                await client.UploadAsync(input, location.Inner, false, cancellationToken).ConfigureAwait(false);
            }

            await client.SetModifiedAsync(location.Inner, File.GetLastWriteTimeUtc(localPath), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var items = new List<Item>();
        foreach (var source in _sources)
        {
            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            List<PlannedItem>? scanned = null;
            await runner.Run(full, () => scanned = TreeScanner.Scan(full, followLinks: false, runner.Token)).ConfigureAwait(false);
            foreach (var item in scanned ?? [])
            {
                var relative = item.Relative.Replace(Path.DirectorySeparatorChar, '/');
                if (_newName is not null)
                {
                    var slash = relative.IndexOf('/', StringComparison.Ordinal);
                    relative = slash < 0 ? _newName : _newName + relative[slash..];
                }

                items.Add(new Item(item, RemotePath.JoinInner(_folder.Inner, relative), item.IsDirectory ? DateTimeOffset.MinValue : new DateTimeOffset(File.GetLastWriteTimeUtc(item.Source), TimeSpan.Zero)));
            }
        }

        IRemoteClient? opened = null;
        if (items.Count == 0 || !await runner.RunAsync(_folder.Root, async () => opened = await _connections.OpenAsync(_folder.Root, runner.Token).ConfigureAwait(false)).ConfigureAwait(false))
        {
            return;
        }

        var client = opened!;
        await using (client.ConfigureAwait(false))
        {
            var tracker = new ProgressTracker(context.Progress, items.Where(i => !i.Local.IsDirectory).Sum(i => i.Local.Size), items.Count);
            tracker.Report(force: true);
            var keptFolders = new List<string>();
            foreach (var item in items)
            {
                if (item.Local.IsDirectory)
                {
                    var ok = await runner.RunAsync(item.Local.Source, async () =>
                    {
                        var existing = await client.StatAsync(item.Inner, runner.Token).ConfigureAwait(false);
                        if (existing is { IsDirectory: false })
                        {
                            throw new OperationRuleException(RuleViolation.TypeMismatch, _folder.Root + item.Inner);
                        }

                        if (existing is null)
                        {
                            await client.CreateDirectoryAsync(item.Inner, runner.Token).ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);
                    tracker.CompleteSmallItem(item.Local.Source, 0);
                    if (ok)
                    {
                        runner.AddDone();
                    }
                    else
                    {
                        keptFolders.Add(item.Local.Source);
                    }

                    continue;
                }

                if (await UploadOneAsync(context, runner, tracker, client, item).ConfigureAwait(false))
                {
                    if (_move)
                    {
                        await runner.Run(item.Local.Source, () => File.Delete(item.Local.Source)).ConfigureAwait(false);
                    }
                }
                else
                {
                    keptFolders.Add(Path.GetDirectoryName(item.Local.Source) ?? string.Empty);
                }
            }

            if (_move)
            {
                foreach (var folder in items.Where(i => i.Local.IsDirectory).Select(i => i.Local.Source).OrderByDescending(s => s.Length))
                {
                    if (keptFolders.Any(k => PathRules.Same(k, folder) || PathRules.IsInside(k, folder)))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(folder);
                    }
                    catch (IOException)
                    {
                        // something new appeared in it: leave it
                    }
                }
            }

            tracker.Report(force: true);
        }
    }

    private async Task<bool> UploadOneAsync(OperationContext context, StepRunner runner, ProgressTracker tracker, IRemoteClient client, Item item)
    {
        var skipped = false;
        tracker.StartItem(item.Local.Source, item.Local.Size);
        var ok = await runner.RunAsync(item.Local.Source, async () =>
        {
            tracker.RestartItem();
            var target = item.Inner;
            var existing = await client.StatAsync(target, runner.Token).ConfigureAwait(false);
            if (existing is { IsDirectory: true })
            {
                throw new OperationRuleException(RuleViolation.TypeMismatch, _folder.Root + target);
            }

            if (existing is not null)
            {
                var choice = await _conflicts.AskAsync(context, runner, new ConflictInfo(
                    item.Local.Source, item.Local.Size, item.Modified, _folder.Root + target, existing.Size, existing.Modified)).ConfigureAwait(false);
                switch (ConflictRules.Decide(choice, item.Modified, existing.Modified))
                {
                    case ConflictAction.Skip:
                        skipped = true;
                        return;
                    case ConflictAction.WriteFreeName:
                        target = await FreeNameAsync(client, target, runner.Token).ConfigureAwait(false);
                        existing = null;
                        break;
                }
            }

            if (client.AtomicUploads)
            {
                // S3: the object appears only when complete, so it goes straight to its name (no part, no append)
                await Resume.RunAsync(item.Local.Source, async moved =>
                {
                    tracker.RestartItem();
                    var input = new FileStream(item.Local.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using (input.ConfigureAwait(false))
                    {
                        var counting = new TransferStream(input, tracker, runner);
                        await using (counting.ConfigureAwait(false))
                        {
                            try
                            {
                                await client.UploadAsync(counting, target, false, runner.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                moved(counting.Transferred);
                            }
                        }
                    }
                }, runner.Token).ConfigureAwait(false);
                await client.SetModifiedAsync(target, item.Modified, runner.Token).ConfigureAwait(false);
                return;
            }

            var part = target + ".part";
            await Resume.RunAsync(item.Local.Source, async moved =>
            {
                var partStat = await client.StatAsync(part, runner.Token).ConfigureAwait(false);
                var offset = partStat is { IsDirectory: false }
                    ? Resume.Usable(partStat.Size, partStat.Modified, item.Local.Size, item.Modified)
                    : 0;
                tracker.RestartItem(offset);
                if (offset > 0)
                {
                    Serilog.Log.Information("{Part}: continuing after {Offset} bytes", part, offset);
                }

                long sent = 0;
                try
                {
                    var input = new FileStream(item.Local.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using (input.ConfigureAwait(false))
                    {
                        input.Seek(offset, SeekOrigin.Begin);
                        var counting = new TransferStream(input, tracker, runner);
                        await using (counting.ConfigureAwait(false))
                        {
                            try
                            {
                                await client.UploadAsync(counting, part, offset > 0, runner.Token).ConfigureAwait(false);
                            }
                            finally
                            {
                                sent = counting.Transferred;
                                moved(sent);
                            }
                        }
                    }
                }
                catch (Exception) when (offset == 0 && sent == 0)
                {
                    await TryDeleteAsync(client, part).ConfigureAwait(false); // nothing worth keeping
                    throw;
                }
            }, runner.Token).ConfigureAwait(false);

            if (existing is not null)
            {
                await client.DeleteFileAsync(target, runner.Token).ConfigureAwait(false);
            }

            await client.RenameAsync(part, target, runner.Token).ConfigureAwait(false);

            await client.SetModifiedAsync(target, item.Modified, runner.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        tracker.FinishItem();
        if (skipped)
        {
            runner.AddSkipped();
            return false;
        }

        if (ok)
        {
            runner.AddDone();
        }

        return ok;
    }

    /// <summary>"/x/report.pdf" → "/x/report (2).pdf", the first name the server does not have.</summary>
    private static async Task<string> FreeNameAsync(IRemoteClient client, string inner, CancellationToken token)
    {
        var folder = RemotePath.ParentInner(inner);
        var name = RemotePath.NameOf(inner);
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
        for (var n = 2; ; n++)
        {
            var candidate = RemotePath.JoinInner(folder, $"{stem} ({n}){ext}");
            if (await client.StatAsync(candidate, token).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }
    }

    private static async Task TryDeleteAsync(IRemoteClient client, string inner)
    {
        try
        {
            await client.DeleteFileAsync(inner, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // nothing was written, or the connection is gone
        }
    }
}

/// <summary>
/// Files → Permissions on a server panel: chmod on each chosen item, exactly as given. With <c>recursive</c> the
/// content of chosen folders follows too: files get the same mode, folders get it with x wherever r is set
/// (like chmod's "X"), so 644 on a tree still leaves its folders enterable.
/// </summary>
public sealed class RemoteModeOperation : IOperation
{
    private readonly RemoteConnections _connections;
    private readonly IReadOnlyList<string> _sources;
    private readonly int _mode;
    private readonly int _folderMode;
    private readonly bool _recursive;

    public RemoteModeOperation(RemoteConnections connections, IReadOnlyList<string> sources, int mode, bool recursive = false)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _mode = mode & UnixMode.All;
        _folderMode = UnixMode.WithSearch(_mode);
        _recursive = recursive;
    }

    public OperationKind Kind => OperationKind.Permissions;

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var locations = _sources.Select(s => RemotePath.Parse(s) ?? throw new InvalidOperationException($"Not a remote path: {s}")).ToList();
        IRemoteClient? opened = null;
        if (locations.Count == 0 || !await runner.RunAsync(locations[0].Root, async () => opened = await _connections.OpenAsync(locations[0].Root, runner.Token).ConfigureAwait(false)).ConfigureAwait(false))
        {
            return;
        }

        var client = opened!;
        await using (client.ConfigureAwait(false))
        {
            var plan = new List<(RemoteLocation Location, int Mode)>();
            foreach (var location in locations)
            {
                await runner.RunAsync(location.FullPath, async () =>
                {
                    var stat = await client.StatAsync(location.Inner, runner.Token).ConfigureAwait(false) ?? throw new FileNotFoundException(null, location.FullPath);
                    plan.Add((location, _mode));
                    if (_recursive && stat.IsDirectory)
                    {
                        await CollectAsync(client, location, plan, runner.Token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
            }

            var tracker = new ProgressTracker(context.Progress, 0, plan.Count);
            tracker.Report(force: true);
            // content before its folder: a folder that loses x (e.g. 600) could not be entered for the rest
            foreach (var (location, mode) in Enumerable.Reverse(plan))
            {
                var ok = await runner.RunAsync(location.FullPath, () => client.SetModeAsync(location.Inner, mode, runner.Token)).ConfigureAwait(false);
                tracker.CompleteSmallItem(location.FullPath, 0);
                if (ok)
                {
                    runner.AddDone();
                }
            }

            tracker.Report(force: true);
        }
    }

    private async Task CollectAsync(IRemoteClient client, RemoteLocation folder, List<(RemoteLocation, int)> plan, CancellationToken token)
    {
        foreach (var child in await client.ListAsync(folder.Inner, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (child.IsLink)
            {
                continue; // chmod on a link changes its target, which may be anywhere
            }

            var location = folder.Child(child.Name);
            plan.Add((location, child.IsDirectory ? _folderMode : _mode));
            if (child.IsDirectory)
            {
                await CollectAsync(client, location, plan, token).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>F8 on a server panel: files, and folders with everything below (always permanent: servers have no bin).</summary>
public sealed class RemoteDeleteOperation : IOperation
{
    private readonly RemoteConnections _connections;
    private readonly IReadOnlyList<string> _sources;

    public RemoteDeleteOperation(RemoteConnections connections, IReadOnlyList<string> sources)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public OperationKind Kind => OperationKind.Delete;

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var locations = _sources.Select(s => RemotePath.Parse(s) ?? throw new InvalidOperationException($"Not a remote path: {s}")).ToList();
        IRemoteClient? opened = null;
        if (locations.Count == 0 || !await runner.RunAsync(locations[0].Root, async () => opened = await _connections.OpenAsync(locations[0].Root, runner.Token).ConfigureAwait(false)).ConfigureAwait(false))
        {
            return;
        }

        var client = opened!;
        await using (client.ConfigureAwait(false))
        {
            // files and folders, children before their folder
            var plan = new List<(RemoteLocation Location, bool IsDirectory)>();
            foreach (var location in locations)
            {
                await runner.RunAsync(location.FullPath, async () =>
                {
                    var stat = await client.StatAsync(location.Inner, runner.Token).ConfigureAwait(false) ?? throw new FileNotFoundException(null, location.FullPath);
                    await CollectAsync(client, location, stat.IsDirectory, plan, runner.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            var tracker = new ProgressTracker(context.Progress, 0, plan.Count);
            tracker.Report(force: true);
            var kept = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (location, isDirectory) in plan)
            {
                if (isDirectory && kept.Any(k => k.StartsWith(location.Inner + "/", StringComparison.Ordinal)))
                {
                    kept.Add(location.Inner); // something below stayed, so this folder cannot go either
                    tracker.CompleteSmallItem(location.FullPath, 0);
                    continue;
                }

                var ok = await runner.RunAsync(location.FullPath, () => isDirectory
                    ? client.DeleteDirectoryAsync(location.Inner, runner.Token)
                    : client.DeleteFileAsync(location.Inner, runner.Token)).ConfigureAwait(false);
                tracker.CompleteSmallItem(location.FullPath, 0);
                if (ok)
                {
                    runner.AddDone();
                }
                else
                {
                    kept.Add(location.Inner);
                }
            }

            tracker.Report(force: true);
        }
    }

    private static async Task CollectAsync(IRemoteClient client, RemoteLocation location, bool isDirectory, List<(RemoteLocation, bool)> plan, CancellationToken token)
    {
        if (isDirectory)
        {
            foreach (var child in await client.ListAsync(location.Inner, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();

                // a link to a folder is removed as a link, never entered
                await CollectAsync(client, location.Child(child.Name), child.IsDirectory && !child.IsLink, plan, token).ConfigureAwait(false);
            }
        }

        plan.Add((location, isDirectory));
    }
}
