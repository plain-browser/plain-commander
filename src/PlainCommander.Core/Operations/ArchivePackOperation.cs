using PlainCommander.Core.Archives;
using PlainCommander.Core.Archives.SevenZip;
using PlainCommander.Core.FileSystem;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;

namespace PlainCommander.Core.Operations;

/// <summary>
/// Alt+F5: packs files and folders into a new archive. ZIP by default (Deflate); ".7z" (7-Zip, optionally with a
/// password that also hides the names), ".tar.gz" / ".tgz" / ".tar" by the name typed. The archive is written next to its final name and moved into place at the end,
/// so a cancelled or failed run leaves nothing half-written. Entries keep the folders' relative paths.
/// </summary>
public sealed class ArchivePackOperation : IOperation, Undo.IUndoable
{
    /// <summary>The archive this run created (or replaced), for Ctrl+Z.</summary>
    public Undo.UndoJournal Journal { get; } = new();

    /// <summary>The finished archive takes its place; a new one can be taken away again by Ctrl+Z.</summary>
    private void PutInPlace(string temp, string finalPath)
    {
        var replaces = File.Exists(finalPath);
        File.Move(temp, finalPath, overwrite: true);
        if (replaces)
        {
            Journal.Overwritten(finalPath);
        }
        else
        {
            Journal.Created(finalPath, isDirectory: false);
        }
    }

    private readonly IReadOnlyList<string> _sources;
    private readonly string _archivePath;
    private readonly string? _password;

    /// <param name="password">7z: encrypts content and names; zip: AES-256 content. Null or empty for none.</param>
    public ArchivePackOperation(IReadOnlyList<string> sources, string archivePath, string? password = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        _archivePath = archivePath;
        _password = string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>A ".7z" name: written by 7-Zip.</summary>
    public static bool IsSevenZip(string archivePath) => archivePath.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);

    public static bool IsZip(string archivePath) => archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Alt+F5 can write this name: zip, tar, tar.gz, and 7z when 7-Zip is there.</summary>
    public static bool CanWrite(string archivePath) => FormatOf(archivePath) is not null || (IsSevenZip(archivePath) && SevenZipWriter.CanWrite);

    /// <summary>A password is offered for 7z and zip (AES-256, written by 7-Zip); tar has no encryption.</summary>
    public static bool SupportsPassword(string archivePath) =>
        (IsSevenZip(archivePath) && SevenZipWriter.CanWrite) || (IsZip(archivePath) && SevenZipWriter.CanWriteZip);

    /// <summary>7-Zip writes 7z, and zip when it is encrypted; plain zip and tar keep the managed writer.</summary>
    private static string? SevenZipFormatFor(string archivePath, string? password) =>
        IsSevenZip(archivePath) ? "7z" : IsZip(archivePath) && password is not null && SevenZipWriter.CanWriteZip ? "zip" : null;

    public OperationKind Kind => OperationKind.Pack;

    /// <summary>What the typed name will produce; null when the extension is not one we can write.</summary>
    public static (ArchiveType Type, CompressionType Compression)? FormatOf(string archivePath)
    {
        var name = Path.GetFileName(archivePath);
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            return (ArchiveType.Tar, CompressionType.GZip);
        }

        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            return (ArchiveType.Tar, CompressionType.None);
        }

        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return (ArchiveType.Zip, CompressionType.Deflate);
        }

        return null;
    }

    /// <summary>A typed name without a writable extension gets ".zip".</summary>
    public static string WithDefaultExtension(string archivePath) => CanWrite(archivePath) ? archivePath : archivePath + ".zip";

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        if (!CanWrite(_archivePath))
        {
            throw new OperationRuleException(RuleViolation.UnsupportedArchive, _archivePath);
        }

        var target = ExactPath.Native(_archivePath);
        if (Directory.Exists(target))
        {
            throw new OperationRuleException(RuleViolation.TypeMismatch, _archivePath);
        }

        var items = new List<PlannedItem>();
        foreach (var source in _sources)
        {
            if (PathRules.Same(source, _archivePath))
            {
                throw new OperationRuleException(RuleViolation.IntoItself, source);
            }

            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            List<PlannedItem>? scanned = null;
            await runner.Run(full, () => scanned = TreeScanner.Scan(full, followLinks: false, runner.Token)).ConfigureAwait(false);
            if (scanned is not null)
            {
                items.AddRange(scanned);
            }
        }

        var finalPath = target;
        if (File.Exists(target))
        {
            var resolved = await ResolveExistingAsync(context, runner, target, items).ConfigureAwait(false);
            if (resolved is null)
            {
                return; // skipped: nothing to do
            }

            finalPath = resolved;
        }

        var tracker = new ProgressTracker(context.Progress, items.Where(i => !i.IsDirectory).Sum(i => i.Size), items.Count);
        tracker.Report(force: true);

        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var temp = TempNames.ReserveFile(finalPath);
        try
        {
            if (SevenZipFormatFor(finalPath, _password) is { } sevenZipFormat)
            {
                await PackSevenZipAsync(runner, tracker, items, temp, finalPath, sevenZipFormat).ConfigureAwait(false);
                return;
            }

            var format = FormatOf(finalPath)!.Value;
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var writer = WriterFactory.Open(stream, format.Type, Options(format)))
            {
                foreach (var item in items)
                {
                    runner.Token.ThrowIfCancellationRequested();
                    var key = item.Relative.Replace(Path.DirectorySeparatorChar, '/');
                    if (item.IsDirectory)
                    {
                        if (item.IsLink)
                        {
                            tracker.CompleteSmallItem(item.Source, 0);
                            continue; // a link would pack its target's content under another name
                        }

                        var okDir = await runner.Run(item.Source, () => writer.WriteDirectory(key, Directory.GetLastWriteTime(item.Source))).ConfigureAwait(false);
                        tracker.CompleteSmallItem(item.Source, 0);
                        if (okDir)
                        {
                            runner.AddDone();
                        }

                        continue;
                    }

                    tracker.StartItem(item.Source, item.Size);
                    var ok = await runner.RunAsync(item.Source, async () =>
                    {
                        await using var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                        await using var counting = new CountingStream(input, tracker, runner);
                        await writer.WriteAsync(key, counting, File.GetLastWriteTime(item.Source), runner.Token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    tracker.FinishItem();
                    if (ok)
                    {
                        runner.AddDone();
                    }
                }
            }

            PutInPlace(temp, finalPath);
            ArchiveCatalog.Forget(ExactPath.Display(finalPath));
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // a leftover .part is better than hiding the original error
                }
            }
        }

        tracker.Report(force: true);
    }

    /// <summary>
    /// 7-Zip packs everything in one call, so a file that cannot be read stops the whole archive (with the usual
    /// retry / skip question; skip leaves no archive). Folder links are left out like in zip.
    /// </summary>
    private async Task PackSevenZipAsync(StepRunner runner, ProgressTracker tracker, List<PlannedItem> items, string temp, string finalPath, string formatName)
    {
        var entries = items
            .Where(i => !(i.IsDirectory && i.IsLink))
            .Select(i => new SevenZipNewItem(
                i.Relative.Replace(Path.DirectorySeparatorChar, '/'),
                i.IsDirectory,
                i.IsDirectory ? null : i.Source,
                i.IsDirectory ? 0 : i.Size,
                i.IsDirectory ? Directory.GetLastWriteTimeUtc(i.Source) : File.GetLastWriteTimeUtc(i.Source),
                i.Attributes))
            .ToList();
        var ok = await runner.Run(finalPath, () =>
        {
            tracker.RestartItem();
            SevenZipWriter.Create(formatName, temp, entries, _password, new SevenZipProgress(tracker, runner));
        }).ConfigureAwait(false);
        if (!ok)
        {
            return;
        }

        PutInPlace(temp, finalPath);
        ArchiveCatalog.Forget(ExactPath.Display(finalPath));
        if (_password is not null)
        {
            ArchivePasswords.Remember(ExactPath.Display(finalPath), _password);
        }

        runner.AddDone(entries.Count);
        tracker.Report(force: true);
    }

    /// <remarks>
    /// Zip names are written as UTF-8, and SharpCompress sets the UTF-8 flag (general purpose bit 11) only when the
    /// encoding is exactly Encoding.UTF8. Its default is a UTF-8 encoding that does not compare equal, which left
    /// the flag off: 7-Zip and Explorer then read "zażółć.txt" in the OEM code page as "za┼╝├│┼é─ç.txt".
    /// </remarks>
    internal static WriterOptions Options((ArchiveType Type, CompressionType Compression) format) =>
        format.Type == ArchiveType.Zip
            ? new ZipWriterOptions(CompressionType.Deflate, SharpCompress.Compressors.Deflate.CompressionLevel.Default)
            {
                UseZip64 = true,
                ArchiveEncoding = new ArchiveEncoding { Forced = System.Text.Encoding.UTF8 },
            }
            : new WriterOptions(format.Compression);

    /// <summary>The archive exists: overwrite it, keep both (a free name) or stop.</summary>
    private static async Task<string?> ResolveExistingAsync(OperationContext context, StepRunner runner, string target, List<PlannedItem> items)
    {
        var existing = new FileInfo(target);
        await runner.PromptGate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        ConflictChoice choice;
        try
        {
            var first = items.FirstOrDefault(i => !i.IsDirectory);
            choice = await context.Prompts.ResolveConflictAsync(
                new ConflictInfo(first?.Source ?? string.Empty, items.Where(i => !i.IsDirectory).Sum(i => i.Size), DateTimeOffset.UtcNow, ExactPath.Display(target), existing.Length, existing.LastWriteTimeUtc),
                context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runner.PromptGate.Release();
        }

        switch (choice)
        {
            case ConflictChoice.Overwrite or ConflictChoice.OverwriteAll or ConflictChoice.OverwriteAllOlder:
                PathRules.ClearReadOnly(target);
                return target;
            case ConflictChoice.KeepBoth or ConflictChoice.KeepBothAll:
                var free = PathRules.ReserveFreeName(target);
                File.Delete(free); // the placeholder; the archive is moved into place at the end
                return free;
            case ConflictChoice.Skip or ConflictChoice.SkipAll:
                runner.AddSkipped();
                return null;
            default:
                throw new OperationAbortedException("Cancelled at a conflict.");
        }
    }

    /// <summary>Reports bytes as the writer pulls them, and honours cancel/pause between reads.</summary>
    private sealed class CountingStream(Stream inner, ProgressTracker tracker, StepRunner runner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek; // the tar writer needs the length up front
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            runner.Token.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, count);
            tracker.AddBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await runner.CheckpointAsync().ConfigureAwait(false);
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            tracker.AddBytes(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
