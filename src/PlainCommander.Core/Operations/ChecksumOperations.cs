using System.Security.Cryptography;
using System.Text;
using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Operations;

public enum ChecksumKind
{
    Md5,
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// Checksum files as sha256sum and friends write them: "hash *name" per line (the star marks binary mode; two
/// spaces are read as well), names relative to the file's folder with "/". BSD lines ("SHA256 (name) = hash")
/// are understood when checking.
/// </summary>
public static class ChecksumFormat
{
    public static string Extension(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Md5 => ".md5",
        ChecksumKind.Sha1 => ".sha1",
        ChecksumKind.Sha512 => ".sha512",
        _ => ".sha256",
    };

    /// <summary>By the file's extension; null when it is not a checksum file.</summary>
    public static ChecksumKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md5" => ChecksumKind.Md5,
        ".sha1" => ChecksumKind.Sha1,
        ".sha256" => ChecksumKind.Sha256,
        ".sha512" => ChecksumKind.Sha512,
        _ => null,
    };

    internal static HashAlgorithmName Algorithm(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Md5 => HashAlgorithmName.MD5,
        ChecksumKind.Sha1 => HashAlgorithmName.SHA1,
        ChecksumKind.Sha512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    };

    public static string Line(string hash, string relative) => $"{hash} *{relative.Replace('\\', '/')}";

    /// <summary>The (hash, name) pairs of a checksum file; lines it cannot read are skipped.</summary>
    public static IReadOnlyList<(string Hash, string Name)> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var entries = new List<(string, string)>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            // BSD: "SHA256 (name) = hash"
            var open = line.IndexOf(" (", StringComparison.Ordinal);
            var close = line.LastIndexOf(") = ", StringComparison.Ordinal);
            if (open > 0 && close > open && IsHex(line[(close + 4)..]))
            {
                entries.Add((line[(close + 4)..].ToLowerInvariant(), line[(open + 2)..close]));
                continue;
            }

            // GNU: "hash *name" or "hash  name"
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && IsHex(line[..space]) && space + 1 < line.Length)
            {
                var name = line[(space + 1)..];
                if (name.Length > 0 && name[0] is '*' or ' ')
                {
                    name = name[1..];
                }

                if (name.Length > 0)
                {
                    entries.Add((line[..space].ToLowerInvariant(), name));
                }
            }
        }

        return entries;
    }

    private static bool IsHex(string text) => text.Length >= 32 && text.All(Uri.IsHexDigit);

    /// <summary>The hash of a file as lower-case hex, read in blocks so pause, cancel and progress work.</summary>
    internal static async Task<string> HashFileAsync(string path, ChecksumKind kind, ProgressTracker tracker, StepRunner runner)
    {
        using var hash = IncrementalHash.CreateHash(Algorithm(kind));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, runner.Token).ConfigureAwait(false)) > 0)
        {
            await runner.CheckpointAsync().ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            tracker.AddBytes(read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

/// <summary>
/// Files → Create checksums: one checksum file for the chosen files and the files in the chosen folders, with names
/// relative to the folder the checksum file is written to. Written next to its final name and moved into place.
/// </summary>
public sealed class ChecksumCreateOperation : IOperation
{
    private readonly IReadOnlyList<string> _sources;
    private readonly string _output;
    private readonly ChecksumKind _kind;

    public ChecksumCreateOperation(IReadOnlyList<string> sources, string output, ChecksumKind kind)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        _output = output;
        _kind = kind;
    }

    public OperationKind Kind => OperationKind.Checksum;

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var output = ExactPath.Native(ExactPath.Full(_output));
        var folder = Path.GetDirectoryName(output)!;
        var files = new List<PlannedItem>();
        foreach (var source in _sources)
        {
            var full = ExactPath.Native(Path.TrimEndingDirectorySeparator(ExactPath.Full(source)));
            List<PlannedItem>? scanned = null;
            await runner.Run(full, () => scanned = TreeScanner.Scan(full, followLinks: false, runner.Token)).ConfigureAwait(false);
            files.AddRange((scanned ?? []).Where(i => !i.IsDirectory && !PathRules.Same(i.Source, output)));
        }

        var tracker = new ProgressTracker(context.Progress, files.Sum(f => f.Size), files.Count);
        tracker.Report(force: true);
        var lines = new StringBuilder();
        foreach (var file in files)
        {
            tracker.StartItem(file.Source, file.Size);
            string? hash = null;
            var ok = await runner.RunAsync(file.Source, async () =>
            {
                tracker.RestartItem();
                hash = await ChecksumFormat.HashFileAsync(file.Source, _kind, tracker, runner).ConfigureAwait(false);
            }).ConfigureAwait(false);
            tracker.FinishItem();
            if (ok && hash is not null)
            {
                lines.Append(ChecksumFormat.Line(hash, Path.GetRelativePath(folder, file.Source))).Append('\n');
                runner.AddDone();
            }
        }

        await runner.RunAsync(output, async () =>
        {
            var temp = output + ".part";
            await File.WriteAllTextAsync(temp, lines.ToString(), new UTF8Encoding(false), runner.Token).ConfigureAwait(false);
            File.Move(temp, output, overwrite: true);
        }).ConfigureAwait(false);
        tracker.Report(force: true);
    }
}

/// <summary>What checking a checksum file found.</summary>
public sealed record ChecksumReport(IReadOnlyList<string> Matched, IReadOnlyList<string> Mismatched, IReadOnlyList<string> Missing);

/// <summary>Files → Verify checksums: reads a checksum file and checks every file it names (relative to its folder).</summary>
public sealed class ChecksumVerifyOperation : IOperation
{
    private readonly string _checksumFile;

    public ChecksumVerifyOperation(string checksumFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checksumFile);
        _checksumFile = checksumFile;
    }

    public OperationKind Kind => OperationKind.Checksum;

    /// <summary>Filled in when the run ends (also when it was cancelled: what was checked so far).</summary>
    public ChecksumReport Report { get; private set; } = new([], [], []);

    public Task<OperationResult> RunAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runner = new StepRunner(context);
        return Task.Run(() => StepRunner.GuardAsync(runner, () => RunCoreAsync(context, runner)));
    }

    private async Task RunCoreAsync(OperationContext context, StepRunner runner)
    {
        var path = ExactPath.Native(ExactPath.Full(_checksumFile));
        var kind = ChecksumFormat.KindOf(path) ?? throw new OperationRuleException(RuleViolation.UnsupportedArchive, path);
        var folder = Path.GetDirectoryName(path)!;
        IReadOnlyList<(string Hash, string Name)> entries = [];
        await runner.RunAsync(path, async () => entries = ChecksumFormat.Parse(await File.ReadAllTextAsync(path, runner.Token).ConfigureAwait(false))).ConfigureAwait(false);

        var targets = entries.Select(e => (e.Hash, e.Name, Full: Path.GetFullPath(Path.Combine(folder, e.Name.Replace('/', Path.DirectorySeparatorChar))))).ToList();
        var tracker = new ProgressTracker(context.Progress, targets.Sum(t => File.Exists(t.Full) ? new FileInfo(t.Full).Length : 0), targets.Count);
        tracker.Report(force: true);
        List<string> matched = [], mismatched = [], missing = [];
        try
        {
            foreach (var (expected, name, full) in targets)
            {
                if (!File.Exists(full))
                {
                    missing.Add(name);
                    tracker.CompleteSmallItem(full, 0);
                    continue;
                }

                var size = new FileInfo(full).Length;
                tracker.StartItem(full, size);
                string? actual = null;
                var ok = await runner.RunAsync(full, async () =>
                {
                    tracker.RestartItem();
                    actual = await ChecksumFormat.HashFileAsync(full, kind, tracker, runner).ConfigureAwait(false);
                }).ConfigureAwait(false);
                tracker.FinishItem();
                if (!ok || actual is null)
                {
                    continue; // skipped after an error: neither good nor bad
                }

                (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? matched : mismatched).Add(name);
                runner.AddDone();
            }
        }
        finally
        {
            Report = new ChecksumReport(matched, mismatched, missing);
        }

        tracker.Report(force: true);
    }
}
