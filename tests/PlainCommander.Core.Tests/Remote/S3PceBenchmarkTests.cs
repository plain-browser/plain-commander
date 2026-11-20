using System.Diagnostics;
using System.Globalization;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Remote;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>
/// Speed of the container kept as objects on a real S3 server. Opt-in: PC_PCE_S3 is the endpoint (as for
/// <see cref="S3PceObjectStoreTests"/>), PC_PCE_S3_BENCH the file the numbers are appended to, PC_PCE_S3_BENCH_GIB
/// the sizes of the large file in GiB (default "1"; "1,10" for the full run). Nothing is written to the local disk:
/// the content is generated, and what comes back is only counted and compared at its ends.
/// </summary>
public sealed class S3PceBenchmarkTests : IDisposable
{
    private const string Bucket = "pc-test";
    private const string Password = "tajne";
    private const int Mega = 1 << 20;
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("PC_PCE_S3");
    private static readonly string? Output = Environment.GetEnvironmentVariable("PC_PCE_S3_BENCH");
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private readonly string _prefix = "pc-bench-" + Guid.NewGuid().ToString("N")[..10] + "/box.pce";
    private AmazonS3Client? _client;

    public void Dispose()
    {
        if (_client is null)
        {
            return;
        }

        try
        {
            var store = new S3PceObjectStore(_client, Bucket, _prefix);
            store.DiscardUploads(DateTime.MaxValue);
            Parallel.ForEach(store.List(string.Empty), new ParallelOptions { MaxDegreeOfParallelism = 8 }, item => store.Delete(item.Name));
        }
        catch (Exception ex) when (ex is IOException or AggregateException)
        {
        }

        _client.Dispose();
    }

    private S3PceObjectStore Store()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Endpoint) || string.IsNullOrEmpty(Output), "Set PC_PCE_S3 (endpoint) and PC_PCE_S3_BENCH (result file) to run the benchmark.");
        if (_client is null)
        {
            _client = new AmazonS3Client(
                new BasicAWSCredentials("tester", "secretsecret"),
                new AmazonS3Config { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true, Timeout = TimeSpan.FromMinutes(5), MaxErrorRetry = 2 });
            try
            {
                _client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }).GetAwaiter().GetResult();
            }
            catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
            }
        }

        return new S3PceObjectStore(_client, Bucket, _prefix);
    }

    private static void Report(string line)
    {
        File.AppendAllText(Output!, line + Environment.NewLine);
        TestContext.Current.SendDiagnosticMessage(line);
    }

    private static string Rate(long bytes, TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{time.TotalSeconds:F2} s, {bytes / (double)Mega / time.TotalSeconds:F0} MiB/s");

    [Fact]
    public void A_large_file_goes_up_and_comes_down_in_several_streams()
    {
        var store = Store();
        var token = TestContext.Current.CancellationToken;
        var sizes = (Environment.GetEnvironmentVariable("PC_PCE_S3_BENCH_GIB") ?? "1").Split(',').Select(s => long.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Report($"# {DateTime.Now:yyyy-MM-dd HH:mm} endpoint {Endpoint}, {Environment.ProcessorCount} threads, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        var kdf = Stopwatch.StartNew();
        using (PceObjectContainer.Create(store, Password))
        {
        }

        Report($"create (Argon2id, head and first index): {kdf.Elapsed.TotalSeconds:F2} s");
        foreach (var gib in sizes)
        {
            var size = gib << 30;
            var name = $"big{gib}.bin";
            using (var container = PceObjectContainer.Open(store, Password, writable: true))
            {
                var up = Stopwatch.StartNew();
                container.Add([new PceSource(name, false, () => new Generated(size, (int)gib), Stamp, FileAttributes.Archive)], PceCompression.Auto, null, token);
                Report($"upload {gib} GiB, 8 parts at once: {Rate(size, up.Elapsed)}");
            }

            var data = store.List(PceObjectNames.DataPrefix).OrderByDescending(o => o.Size).First();
            var window = new byte[8 * Mega];
            foreach (var at in new[] { 0L, data.Size / 2, data.Size - window.Length })
            {
                var range = Stopwatch.StartNew();
                store.Read(data.Name, at, window).Should().Be(window.Length);
                Report($"one range of 8 MiB at {at / Mega} MiB of an object of {data.Size / Mega} MiB: {range.Elapsed.TotalMilliseconds:F0} ms");
            }

            foreach (var streams in new[] { 8, 1 })
            {
                using var container = PceObjectContainer.Open(store, Password, writable: false, split: new PceSplitOptions { Parallelism = streams });
                var down = Stopwatch.StartNew();
                using var stream = container.OpenRead(container.Find(name)!);
                Compare(stream, new Generated(size, (int)gib)).Should().Be(size);
                Report($"download {gib} GiB, {streams} stream(s): {Rate(size, down.Elapsed)}");
            }

            using (var container = PceObjectContainer.Open(store, Password, writable: true))
            {
                var small = new byte[10 * Mega];
                new Random(7).NextBytes(small);
                var add = Stopwatch.StartNew();
                container.Add([new PceSource("ten.bin", false, () => new MemoryStream(small), Stamp, FileAttributes.Archive)], PceCompression.None, null, token);
                add.Stop();
                var delete = Stopwatch.StartNew();
                container.Delete(["ten.bin"], token).Should().Be(1);
                delete.Stop();
                var tail = Stopwatch.StartNew();
                using (var stream = container.OpenRead(container.Find(name)!))
                {
                    stream.Seek(-Mega, SeekOrigin.End);
                    var end = new byte[Mega];
                    stream.ReadExactly(end);
                }

                tail.Stop();
                Report($"with {gib} GiB inside: add 10 MiB {add.Elapsed.TotalSeconds:F2} s, delete it {delete.Elapsed.TotalSeconds:F2} s, read the last 1 MiB {tail.Elapsed.TotalSeconds:F2} s");
                var open = Stopwatch.StartNew();
                using (PceObjectContainer.Open(store, Password, writable: false))
                {
                }

                Report($"open (Argon2id, head, newest index): {open.Elapsed.TotalSeconds:F2} s");
                var remove = Stopwatch.StartNew();
                container.Delete([name], token).Should().Be(1);
                var cleaned = container.Clean(token);
                Report($"delete the {gib} GiB file {remove.Elapsed.TotalSeconds:F2} s (cleaning included; {cleaned})");
            }
        }
    }

    [Fact]
    public void Ten_thousand_small_files()
    {
        var store = Store();
        var token = TestContext.Current.CancellationToken;
        const int count = 10_000;
        const int each = 2048;
        using (PceObjectContainer.Create(store, Password))
        {
        }

        var before = store.List(string.Empty).Count;
        using (var container = PceObjectContainer.Open(store, Password, writable: true))
        {
            var add = Stopwatch.StartNew();
            container.Add(Enumerable.Range(0, count).Select(i => new PceSource($"dir{i / 500:D2}/f{i:D5}.txt", false, () => new MemoryStream(Small(i, each)), Stamp, FileAttributes.Archive)), PceCompression.None, null, token);
            Report($"add {count} files of {each} B: {add.Elapsed.TotalSeconds:F2} s, {store.List(string.Empty).Count - before} new objects");
        }

        var open = Stopwatch.StartNew();
        using (var container = PceObjectContainer.Open(store, Password, writable: true))
        {
            Report($"open with {count} files (Argon2id included): {open.Elapsed.TotalSeconds:F2} s, {container.Entries.Count} entries");
            var one = Stopwatch.StartNew();
            using (var stream = container.OpenRead(container.Find("dir07/f03777.txt")!))
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.ToArray().Should().Equal(Small(3777, each));
            }

            one.Stop();
            var all = Stopwatch.StartNew();
            long total = 0;
            foreach (var entry in container.Entries.Where(e => !e.IsDirectory))
            {
                using var stream = container.OpenRead(entry);
                total += Compare(stream, null);
            }

            all.Stop();
            total.Should().Be((long)count * each);
            var delete = Stopwatch.StartNew();
            container.Delete(["dir07/f03777.txt"], token).Should().Be(1);
            delete.Stop();
            var folder = Stopwatch.StartNew();
            container.Delete(["dir03"], token).Should().Be(500);
            folder.Stop();
            Report($"with {count} files: read one {one.Elapsed.TotalSeconds:F2} s, read all one by one {all.Elapsed.TotalSeconds:F2} s, delete one {delete.Elapsed.TotalSeconds:F2} s, delete a folder of 500 {folder.Elapsed.TotalSeconds:F2} s");
        }
    }

    private static byte[] Small(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Reads <paramref name="stream"/> to its end; with <paramref name="expected"/> also compares the content. The length read.</summary>
    private static long Compare(Stream stream, Generated? expected)
    {
        var buffer = new byte[Mega];
        var other = new byte[Mega];
        long total = 0;
        while (true)
        {
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0)
            {
                return total;
            }

            if (expected is not null)
            {
                expected.ReadExactly(other, 0, read);
                if (!buffer.AsSpan(0, read).SequenceEqual(other.AsSpan(0, read)))
                {
                    throw new InvalidDataException($"The content differs near byte {total}.");
                }
            }

            total += read;
        }
    }

    /// <summary>Content that does not compress and differs from megabyte to megabyte, without holding it in memory.</summary>
    private sealed class Generated : Stream
    {
        private readonly byte[] _block = new byte[Mega];
        private readonly long _length;
        private long _position;

        public Generated(long length, int seed)
        {
            _length = length;
            new Random(seed).NextBytes(_block);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var take = (int)Math.Min(buffer.Length, _length - _position);
            for (var done = 0; done < take;)
            {
                var at = (int)((_position + done) % Mega);
                var piece = Math.Min(take - done, Mega - at);
                _block.AsSpan(at, piece).CopyTo(buffer[done..]);
                for (var i = at; i < 8 && i - at < piece; i++)
                {
                    buffer[done + i - at] = (byte)(((_position + done) / Mega) >> (8 * i)); // each megabyte is its own
                }

                done += piece;
            }

            _position += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
