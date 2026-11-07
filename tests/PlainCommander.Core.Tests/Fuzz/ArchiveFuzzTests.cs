using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using PlainCommander.Core.Tests.Weird;
using SharpCompress.Common;
using SharpCompress.Writers.Tar;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>
/// Damaged archives through the managed reader (SharpCompress): listing and extraction fail per file with an error the
/// operation can report, never write outside the target folder and never hang. Formats 7z.dll would read on Windows
/// are skipped there; .tar.gz always stays with the managed reader.
/// </summary>
public sealed class ArchiveFuzzTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static readonly (string Name, string Text)[] Entries =
    [
        ("readme.txt", "Plain Commander"),
        ("docs/guide.md", string.Concat(Enumerable.Repeat("compressible line\n", 40))),
        ("docs/empty.txt", string.Empty),
        ("src/deep/a/b/c.cs", "class C {}"),
    ];

    private static readonly (string Name, string Text)[] Escaping =
    [
        ("ok.txt", "ok"),
        ("../evil.txt", "evil"),
        ("a/../../evil2.txt", "evil"),
        ("/abs.txt", "abs"),
        ("..\\evil3.txt", "evil"),
    ];

    private static byte[] Zip((string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var level = CompressionLevel.Optimal;
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                entry.LastWriteTime = new DateTimeOffset(2024, 1, 2, 3, 4, 6, TimeSpan.Zero); // the same bytes on every run
                level = level == CompressionLevel.Optimal ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                using var stream = entry.Open();
                stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
            }
        }

        return memory.ToArray();
    }

    private static byte[] Tar((string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var writer = new TarWriter(memory, new TarWriterOptions(CompressionType.None, finalizeArchiveOnClose: true) { LeaveStreamOpen = true }))
        {
            foreach (var (name, text) in entries)
            {
                using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
                writer.Write(name, content, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
        }

        return memory.ToArray();
    }

    private static byte[] Gzip(byte[] data)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return memory.ToArray();
    }

    /// <summary>Lists and extracts one damaged archive; returns what went wrong (empty when all is well).</summary>
    private string ListAndExtract(byte[] bytes, string extension)
    {
        var caseDir = _s.P("case");
        if (Directory.Exists(caseDir))
        {
            Directory.Delete(caseDir, recursive: true);
        }

        Directory.CreateDirectory(caseDir);
        var archive = Path.Combine(caseDir, "in" + extension);
        File.WriteAllBytes(archive, bytes);
        ArchiveCatalog.Forget(archive);

        var listing = Task.Run(() =>
        {
            var catalog = ArchiveCatalog.Open(archive);
            foreach (var folder in catalog.FoldersUnder(string.Empty).Select(f => f.Inner).Prepend(string.Empty))
            {
                _ = catalog.List(folder).ToList();
            }
        });

        // Generous: SharpCompress needs seconds for some damaged zips (see the report), but must not loop for ever.
        if (!((IAsyncResult)listing).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
        {
            return "listing hangs";
        }

        // NotAnArchiveException or another IOException is what the panel reports; anything else is a crash.
        if (listing.Exception?.InnerException is { } failure && failure is not (IOException or UnauthorizedAccessException))
        {
            return $"listing threw {failure.GetType().Name}: {failure.Message}{Environment.NewLine}{failure.StackTrace}";
        }

        var target = Directory.CreateDirectory(Path.Combine(caseDir, "a", "b", "out")).FullName;
        var prompts = new HookPrompts(conflict: _ => ConflictChoice.OverwriteAll, error: (_, _) => ErrorChoice.Skip);
        var run = new ArchiveExtractOperation([archive], new TransferTarget(target, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken, false));
        if (!((IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(30)))
        {
            return "extraction hangs";
        }

        if (run.IsFaulted)
        {
            var ex = run.Exception!.InnerException!;
            return $"extraction threw {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}";
        }

        var stray = Directory.EnumerateFileSystemEntries(caseDir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(caseDir, p).Replace('\\', '/'))
            .Where(p => p is not ("a" or "a/b") && p != "in" + extension && !p.StartsWith("a/b/out", StringComparison.Ordinal))
            .ToList();
        return stray.Count == 0 ? string.Empty : "written outside the target: " + string.Join(", ", stray);
    }

    private void Fuzz(int seed, byte[] valid, string extension, int iterations)
    {
        ListAndExtract(valid, extension).Should().BeEmpty("the valid archive extracts cleanly");
        FuzzKit.Check(seed, iterations, g => g.Mutate(valid), bytes => ListAndExtract(bytes, extension).Should().BeEmpty(), show: FuzzKit.Hex);
    }

    /// <summary>
    /// Few cases on purpose: SharpCompress spends seconds on a zip whose end record claims thousands of entries at an
    /// offset past the end (base seed 902 with 40 cases hits one, ~6 s for 562 bytes). It finishes and lists nothing,
    /// so it is slow, not wrong; run with PC_FUZZ_ITERATIONS=10 to search more.
    /// </summary>
    [Fact]
    public void A_damaged_zip_fails_cleanly()
    {
        Assert.SkipWhen(ArchiveReaders.UsesSevenZip("x.zip"), "7-Zip reads zip files here");
        Fuzz(901, Zip(Entries), ".zip", 20);
    }

    [Fact]
    public void A_damaged_zip_with_escaping_names_stays_inside()
    {
        Assert.SkipWhen(ArchiveReaders.UsesSevenZip("x.zip"), "7-Zip reads zip files here");
        Fuzz(922, Zip(Escaping), ".zip", 15);
    }

    [Fact]
    public void A_damaged_tar_fails_cleanly()
    {
        Assert.SkipWhen(ArchiveReaders.UsesSevenZip("x.tar"), "7-Zip reads tar files here");
        Fuzz(903, Tar(Entries), ".tar", 40);
        Fuzz(904, Tar(Escaping), ".tar", 25);
    }

    [Fact]
    public void A_damaged_tar_gz_fails_cleanly()
    {
        Fuzz(905, Gzip(Tar(Entries)), ".tar.gz", 45);
        Fuzz(906, Gzip(Tar(Escaping)), ".tar.gz", 25);
    }

    [Fact]
    public void Random_bytes_are_not_an_archive()
    {
        string[] extensions = [".zip", ".tar", ".tar.gz", ".gz"];
        foreach (var extension in extensions.Where(e => !ArchiveReaders.UsesSevenZip("x" + e)))
        {
            FuzzKit.Check(907, 15, g => g.Bytes(600), bytes => ListAndExtract(bytes, extension).Should().BeEmpty(), FuzzKit.ShrinkBytes, FuzzKit.Hex);
        }
    }

    /// <summary>Found by the fuzzer (seed 905): a gzip header with odd flags made SharpCompress throw OverflowException out of the listing.</summary>
    [Fact]
    public void Regression_tar_gz_with_an_odd_gzip_header_is_not_an_archive()
    {
        var file = _s.P("odd.tar.gz");
        File.WriteAllBytes(file, Convert.FromHexString("1F8B08971F96940D67DC44"));
        ArchiveCatalog.Forget(file);

        var act = () => ArchiveCatalog.Open(file);

        act.Should().Throw<NotAnArchiveException>();
    }

    /// <summary>
    /// Found by the fuzzer (seed 905): a tar.gz whose data does not inflate threw SharpCompress's ZlibException while
    /// extracting, which the operation did not treat as a file error: the whole operation crashed instead of
    /// reporting the archive and going on.
    /// </summary>
    [Fact]
    public void Regression_tar_gz_with_damaged_data_is_an_error_of_that_archive()
    {
        var damaged = _s.P("damaged.tar.gz");
        File.WriteAllBytes(damaged, Convert.FromHexString("1F8B0800000000000403FE4A4D4C"));
        ArchiveCatalog.Forget(damaged);
        var reader = ArchiveReaders.Open(damaged);
        var read = () =>
        {
            using (reader)
            {
                while (reader.MoveNext())
                {
                    using var entry = reader.OpenEntryStream();
                    entry.CopyTo(Stream.Null);
                }
            }
        };

        read.Should().Throw<IOException>();
        ListAndExtract(Convert.FromHexString("1F8B0800000000000403FE4A4D4C"), ".tar.gz").Should().BeEmpty();
    }

    /// <summary>Found by the fuzzer (seeds 901, 902): damaged zip headers threw IndexOutOfRangeException from the listing and from reading an entry.</summary>
    [Theory]
    [InlineData(338)]
    [InlineData(4176)]
    public void Regression_zip_with_damaged_headers_fails_as_a_damaged_archive(int mutation)
    {
        Assert.SkipWhen(ArchiveReaders.UsesSevenZip("x.zip"), "7-Zip reads zip files here");
        ListAndExtract(new Gen(mutation).Mutate(Zip(Entries)), ".zip").Should().BeEmpty();
    }
}
