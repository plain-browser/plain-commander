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
}
