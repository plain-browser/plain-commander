using System.Diagnostics;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>Throughput of the container on a 1 GB file; runs only with PC_PCE_BENCH set (writes the numbers next to the file).</summary>
public sealed class PceBenchmarkTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public void One_gigabyte_runs_at_disk_speed()
    {
        var output = Environment.GetEnvironmentVariable("PC_PCE_BENCH");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "set PC_PCE_BENCH=<result file> to run the benchmark");
        const long size = 1L << 30;
        var source = _s.P("big.bin");
        var block = new byte[1 << 20];
        new Random(1).NextBytes(block);
        using (var stream = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            for (long written = 0; written < size; written += block.Length)
            {
                stream.Write(block);
            }
        }

        var pce = source + ".pce";
        var token = TestContext.Current.CancellationToken;
        var encrypt = Stopwatch.StartNew();
        PceFile.Encrypt(source, pce, "tajne", null, PceCompression.Auto, null, token);
        encrypt.Stop();
        var back = _s.P("back.bin");
        var decrypt = Stopwatch.StartNew();
        PceFile.Decrypt(pce, "tajne", back, null, token);
        decrypt.Stop();
        var textSource = _s.P("text.txt");
        var line = System.Text.Encoding.UTF8.GetBytes("Plain Commander: dwupanelowy menedżer plików, wiersz ");
        using (var stream = new FileStream(textSource, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            for (long written = 0, i = 0; written < size / 4; i++)
            {
                stream.Write(line);
                stream.Write(System.Text.Encoding.UTF8.GetBytes(i + "\n"));
                written += line.Length + 8;
            }
        }

        var textPce = textSource + ".pce";
        var zstd = Stopwatch.StartNew();
        PceFile.Encrypt(textSource, textPce, "tajne", null, PceCompression.Auto, null, token);
        zstd.Stop();
        var textSize = new FileInfo(textSource).Length;
        var kdf = Stopwatch.StartNew();
        PceFile.Peek(pce, "tajne");
        kdf.Stop();

        var lines = new[]
        {
            $"cipher: {PceFile.Peek(pce, "tajne").Header.Cipher}",
            $"encrypt 1 GiB (auto, sample says no): {encrypt.Elapsed.TotalSeconds:F2} s = {size / encrypt.Elapsed.TotalSeconds / (1 << 20):F0} MiB/s (incl. key derivation)",
            $"decrypt 1 GiB: {decrypt.Elapsed.TotalSeconds:F2} s = {size / decrypt.Elapsed.TotalSeconds / (1 << 20):F0} MiB/s",
            $"key derivation (Argon2id 64 MiB, t=3, p=4): {kdf.Elapsed.TotalSeconds:F2} s",
            $"text {textSize / (1 << 20)} MiB with zstd (auto): {zstd.Elapsed.TotalSeconds:F2} s = {textSize / zstd.Elapsed.TotalSeconds / (1 << 20):F0} MiB/s, {new FileInfo(textPce).Length * 100.0 / textSize:F0}% of the size",
        };
        File.WriteAllLines(output!, lines);
        new FileInfo(back).Length.Should().Be(size);
    }

    /// <summary>The two layouts side by side on the same 1 GiB: to nowhere (reading and decrypting alone) and to a file.</summary>
    [Fact]
    public void Decrypting_a_container_keeps_pace_with_the_first_layout()
    {
        var output = Environment.GetEnvironmentVariable("PC_PCE_BENCH");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "set PC_PCE_BENCH=<result file> to run the benchmark");
        const long size = 1L << 30;
        var token = TestContext.Current.CancellationToken;
        var source = _s.P("big.bin");
        var block = new byte[1 << 20];
        new Random(1).NextBytes(block);
        using (var stream = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            for (long written = 0; written < size; written += block.Length)
            {
                stream.Write(block);
            }
        }

        var first = _s.P("first.pce");
        var container = _s.P("container.pce");
        var lines = new List<string>();
        var legacyTimes = new List<double>();
        var containerTimes = new List<double>();
        for (var run = 0; run < 3; run++)
        {
            var watch = Stopwatch.StartNew();
            PceFile.EncryptLegacy(source, first, "tajne", null, PceCompression.None, null, token);
            legacyTimes.Add(watch.Elapsed.TotalSeconds);
            watch.Restart();
            PceFile.Encrypt(source, container, "tajne", null, PceCompression.None, null, token);
            containerTimes.Add(watch.Elapsed.TotalSeconds);
        }

        lines.Add($"encrypt 1 GiB, first layout (left in the system cache): {string.Join(" / ", legacyTimes.Select(t => t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} s");
        lines.Add($"encrypt 1 GiB, container (flushed to disk): {string.Join(" / ", containerTimes.Select(t => t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} s");
        foreach (var (name, file) in new[] { ("first layout", first), ("container", container), ("first layout", first), ("container", container) })
        {
            var toNowhere = new List<double>();
            var toFile = new List<double>();
            for (var run = 0; run < 3; run++)
            {
                var watch = Stopwatch.StartNew();
                PceFile.CopyContent(file, "tajne", Stream.Null, null, token);
                toNowhere.Add(watch.Elapsed.TotalSeconds);
                var back = _s.P("back.bin");
                watch.Restart();
                PceFile.Decrypt(file, "tajne", back, null, token);
                toFile.Add(watch.Elapsed.TotalSeconds);
                File.Delete(back);
            }

            lines.Add($"decrypt 1 GiB, {name}: to nowhere {string.Join(" / ", toNowhere.Select(t => t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} s; to a file {string.Join(" / ", toFile.Select(t => t.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} s");
        }

        File.AppendAllLines(output!, lines);
    }

    /// <summary>The container layout: PC_PCE_BENCH_GIB sets the size of the large entries (default 2 × 1 GiB).</summary>
    [Fact]
    public void Deleting_from_a_large_container_costs_what_the_deleted_entry_took()
    {
        var output = Environment.GetEnvironmentVariable("PC_PCE_BENCH");
        Assert.SkipWhen(string.IsNullOrEmpty(output), "set PC_PCE_BENCH=<result file> to run the benchmark");
        var gib = int.TryParse(Environment.GetEnvironmentVariable("PC_PCE_BENCH_GIB"), out var g) ? g : 1;
        var token = TestContext.Current.CancellationToken;
        var block = new byte[1 << 20];
        new Random(1).NextBytes(block);
        string Make(string name, long size)
        {
            var path = _s.P(name);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            for (long written = 0; written < size; written += block.Length)
            {
                stream.Write(block);
            }

            return path;
        }

        var first = Make("first.bin", (long)gib << 30);
        var small = Make("small.bin", 10L << 20);
        var last = Make("last.bin", (long)gib << 30);
        var many = _s.P("many");
        Directory.CreateDirectory(many);
        for (var i = 0; i < 10_000; i++)
        {
            File.WriteAllBytes(Path.Combine(many, $"f{i:D5}.txt"), block.AsSpan(i, 200 + i % 300).ToArray());
        }

        var pce = _s.P("box.pce");
        var create = Stopwatch.StartNew();
        using (var container = PceContainer.Create(pce, "tajne"))
        {
            container.Add(PceSource.FromPath(first).Concat(PceSource.FromPath(small)).Concat(PceSource.FromPath(last)), PceCompression.Auto, null, token);
        }

        create.Stop();
        var total = new FileInfo(pce).Length;
        var open = Stopwatch.StartNew();
        using (PceContainer.Open(pce, "tajne"))
        {
        }

        open.Stop();
        var delete = Stopwatch.StartNew();
        using (var container = PceContainer.Open(pce, "tajne", writable: true))
        {
            container.Delete(["small.bin"], token);
        }

        delete.Stop();
        var afterDelete = new FileInfo(pce).Length;
        var extract = Stopwatch.StartNew();
        using (var container = PceContainer.Open(pce, "tajne"))
        using (var stream = container.OpenRead(container.Find("last.bin")!))
        {
            stream.Position = stream.Length - 5;
            stream.ReadByte();
        }

        extract.Stop();
        var manyPce = _s.P("many.pce");
        var createMany = Stopwatch.StartNew();
        using (var container = PceContainer.Create(manyPce, "tajne"))
        {
            container.Add(PceSource.FromPath(many), PceCompression.None, null, token);
        }

        createMany.Stop();
        var list = Stopwatch.StartNew();
        int count;
        using (var container = PceContainer.Open(manyPce, "tajne"))
        {
            count = container.Entries.Count;
        }

        list.Stop();
        var deleteMany = Stopwatch.StartNew();
        using (var container = PceContainer.Open(manyPce, "tajne", writable: true))
        {
            container.Delete(Enumerable.Range(0, 5000).Select(i => $"many/f{i * 2:D5}.txt"), token);
        }

        deleteMany.Stop();

        File.AppendAllLines(output!,
        [
            $"container: create {total / (1 << 20)} MiB: {create.Elapsed.TotalSeconds:F2} s = {total / create.Elapsed.TotalSeconds / (1 << 20):F0} MiB/s (incl. key derivation)",
            $"container: open (key derivation + index): {open.Elapsed.TotalSeconds:F2} s",
            $"container: delete 10 MiB from the middle of {total / (1 << 20)} MiB: {delete.Elapsed.TotalSeconds:F2} s (incl. open), file shrank by {(total - afterDelete) / 1024} KiB",
            $"container: open and read the last byte of a {gib} GiB entry: {extract.Elapsed.TotalSeconds:F2} s",
            $"container: create from {count} small files: {createMany.Elapsed.TotalSeconds:F2} s; open and list: {list.Elapsed.TotalSeconds:F2} s; delete every second one: {deleteMany.Elapsed.TotalSeconds:F2} s",
        ]);
        (total - afterDelete).Should().Be((10L << 20) + 10 * 16);
    }
}
