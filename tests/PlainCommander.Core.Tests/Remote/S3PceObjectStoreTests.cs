using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Remote;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>
/// The .pce container kept as objects, on a real S3 server. Opt-in: set PC_PCE_S3 to the endpoint of the RustFS
/// container of <see cref="RemoteServerTests"/>, e.g. "http://localhost:9000" (keys "tester" / "secretsecret").
/// Every test works under its own prefix of the bucket "pc-test" and removes it afterwards.
/// </summary>
public sealed class S3PceObjectStoreTests : IDisposable
{
    private const string Bucket = "pc-test";
    private const string Password = "tajne";
    private const int Mega = 1 << 20;
    private static readonly int[] Kept = [0, 1, 3, 4];
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("PC_PCE_S3");
    private static readonly PceOptions Fast = new() { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000 };

    // S3 takes no part under 5 MiB but the last, so these are the smallest sizes that still exercise everything:
    // entries under 1 MiB share packs of up to 4 MiB, an object has at most two parts.
    private static readonly PceSplitOptions Sizes = new() { SmallLimit = Mega, PackSize = 4 * Mega, PartSize = 5 * Mega, MaxParts = 2, Parallelism = 4, ReadAhead = 2 * Mega };
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private readonly string _prefix = "pc-pce-" + Guid.NewGuid().ToString("N")[..10] + "/box.pce";
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
            foreach (var item in store.List(string.Empty))
            {
                store.Delete(item.Name);
            }
        }
        catch (IOException)
        {
        }

        _client.Dispose();
    }

    private S3PceObjectStore Store()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Endpoint), "Set PC_PCE_S3 to the endpoint of the S3 test server.");
        if (_client is null)
        {
            _client = new AmazonS3Client(
                new BasicAWSCredentials("tester", "secretsecret"),
                new AmazonS3Config { ServiceURL = Endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true, Timeout = TimeSpan.FromSeconds(120), MaxErrorRetry = 2 });
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

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private static PceSource File(string path, byte[] content) => new(path, false, () => new MemoryStream(content), Stamp, FileAttributes.Archive);

    private static byte[] Read(PceObjectContainer container, string path)
    {
        using var stream = container.OpenRead(container.Find(path)!);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void An_object_is_stored_read_by_range_replaced_listed_and_deleted()
    {
        var store = Store();
        var content = Bytes(100_000);
        store.Get("head").Should().BeNull();
        store.Put("head", content);
        store.Get("head").Should().Equal(content);

        var buffer = new byte[300];
        store.Read("head", 0, buffer).Should().Be(300);
        buffer.Should().Equal(content[..300]);
        store.Read("head", 99_900, buffer).Should().Be(100);
        buffer[..100].Should().Equal(content[99_900..]);
        store.Read("head", 100_000, buffer).Should().Be(0);
        store.Read("head", 200_000, buffer).Should().Be(0);
        ((Action)(() => store.Read("data/nie-ma", 0, new byte[10]))).Should().Throw<PceObjectMissingException>();

        store.Put("head", [1, 2, 3]);
        store.Get("head").Should().Equal([1, 2, 3]);
        store.Put("data/empty", []);
        store.Get("data/empty").Should().BeEmpty();

        foreach (var generation in new ulong[] { 3, 1, 2 })
        {
            store.PutNew(PceObjectNames.Index(generation), Bytes(40 + (int)generation)).Should().BeTrue();
        }

        store.List("index/").Select(o => o.Name).Should().Equal(PceObjectNames.Index(3), PceObjectNames.Index(2), PceObjectNames.Index(1));
        var newest = store.List("index/", 1).Should().ContainSingle().Which;
        newest.Name.Should().Be(PceObjectNames.Index(3));
        newest.Size.Should().Be(43);
        newest.ModifiedUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(10), "the store stamps objects with its own clock, which the clean-up rules read");
        store.List(string.Empty).Select(o => o.Name).Should().Equal("data/empty", "head", PceObjectNames.Index(3), PceObjectNames.Index(2), PceObjectNames.Index(1));
        store.List("nic/").Should().BeEmpty();

        store.Delete(PceObjectNames.Index(3));
        store.Delete(PceObjectNames.Index(3));
        store.List("index/", 1).Single().Name.Should().Be(PceObjectNames.Index(2));
    }

    [Fact]
    public void The_server_honours_create_only_if_absent()
    {
        var store = Store();
        store.ConditionalPut.Should().BeTrue("without it two writers at once are not safe on this server");
        store.List(string.Empty).Should().BeEmpty("the probe leaves nothing behind");
        var name = PceObjectNames.Index(5);
        store.PutNew(name, [1, 1]).Should().BeTrue();
        store.PutNew(name, [2, 2]).Should().BeFalse();
        store.Get(name).Should().Equal([1, 1]);
        store.Delete(name);
        store.PutNew(name, [3]).Should().BeTrue();
    }

    [Fact]
    public void Of_many_writers_committing_one_generation_exactly_one_wins()
    {
        var store = Store();
        for (var round = 0; round < 8; round++)
        {
            var name = PceObjectNames.Index((ulong)round);
            var wins = new int[12];
            Parallel.For(0, wins.Length, new ParallelOptions { MaxDegreeOfParallelism = wins.Length }, i => wins[i] = store.PutNew(name, [(byte)i, (byte)i, (byte)i]) ? 1 : 0);
            wins.Sum().Should().Be(1, $"round {round}");
            var winner = (byte)Array.IndexOf(wins, 1);
            store.Get(name).Should().Equal([winner, winner, winner]);
        }
    }

    [Fact]
    public void An_upload_in_parts_appears_whole_when_completed_and_an_abandoned_one_is_discarded()
    {
        var store = Store();
        var parts = new[] { Bytes(5 * Mega, 1), Bytes(5 * Mega, 2), Bytes(123_456, 3) };
        var name = PceObjectNames.Data(PceObjectId.New());
        using (var upload = store.BeginUpload(name))
        {
            Parallel.ForEach([3, 1, 2], n => upload.Write(n, parts[n - 1]));
            store.Get(name).Should().BeNull("nothing shows before the upload is completed");
            upload.Complete();
        }

        var whole = store.Get(name)!;
        whole.Should().HaveCount(10 * Mega + 123_456);
        whole.AsSpan().SequenceEqual(parts.SelectMany(p => p).ToArray()).Should().BeTrue();
        var middle = new byte[1000];
        store.Read(name, 5 * Mega - 500, middle).Should().Be(1000);
        middle.Should().Equal(whole[(5 * Mega - 500)..(5 * Mega + 500)]);

        var abandoned = PceObjectNames.Data(PceObjectId.New());
        using (var upload = store.BeginUpload(abandoned))
        {
            upload.Write(1, Bytes(5 * Mega, 4));
        }

        store.Get(abandoned).Should().BeNull();
        store.DiscardUploads(DateTime.MaxValue).Should().Be(0, "disposing the upload discarded it");

        // A writer that died: its upload stays until it is old enough to be discarded.
        var leaked = store.BeginUpload(PceObjectNames.Data(PceObjectId.New()));
        leaked.Write(1, Bytes(1000));
        store.DiscardUploads(DateTime.UtcNow.AddDays(-1)).Should().Be(0);
        store.DiscardUploads(DateTime.MaxValue).Should().Be(1);
        store.List(string.Empty).Select(o => o.Name).Should().Equal(name);
    }

    [Fact]
    public void A_container_is_created_changed_and_reopened()
    {
        var store = Store();
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 60_000).Select(i => $"wiersz {i} z powtarzalnym tekstem\n")));
        var content = new Dictionary<string, byte[]>
        {
            ["zażółć gęślą jaźń.txt"] = Bytes(70_000),
            ["日本語/emoji 😀.bin"] = Bytes(900_000, 1),
            ["dir/duży.bin"] = Bytes(17 * Mega, 2),
            ["dir/średni.bin"] = Bytes(3 * Mega, 3),
            ["dir/tekst.txt"] = text,
            ["pusty.txt"] = [],
        };
        using (var created = PceObjectContainer.Create(store, Password, Fast, Sizes))
        {
            created.SafeForSeveralWriters.Should().BeTrue();
            created.Add(content.Select(p => File(p.Key, p.Value)), PceCompression.Auto);
        }

        using (var container = PceObjectContainer.Open(store, Password, writable: true, split: Sizes))
        {
            foreach (var (path, bytes) in content)
            {
                Read(container, path).AsSpan().SequenceEqual(bytes).Should().BeTrue(path);
            }

            // 17 MiB + tags: two objects of two 5 MiB parts would hold 20, so a full one and the rest.
            var big = container.Find("dir/duży.bin")!;
            big.Extents.Select(e => e.Length).Should().Equal(10L * Mega, big.StoredSize - 10L * Mega);
            container.Find("dir/tekst.txt")!.Compressed.Should().BeTrue();
            container.Find("zażółć gęślą jaźń.txt")!.Extents[0].Object.Should().Be(container.Find("日本語/emoji 😀.bin")!.Extents[0].Object, "small entries share a pack");

            using (var stream = container.OpenRead(big))
            {
                stream.Position = stream.Length - 5;
                stream.ReadByte().Should().Be(content["dir/duży.bin"][^5]);
            }

            var before = store.List("data/").Sum(o => o.Size);
            container.Delete(["dir/duży.bin"]).Should().Be(1);
            (before - store.List("data/").Sum(o => o.Size)).Should().Be(big.StoredSize, "the space comes back with the delete");
            container.Rename("dir", "katalog");
            container.ChangePassword("nowe");
            container.Add([File("nowy.bin", Bytes(200_000, 9))]);
            container.Clean().Orphans.Should().Be(0);
        }

        ((Action)(() => PceObjectContainer.Open(store, Password, split: Sizes))).Should().Throw<PceWrongPasswordException>();
        using var reopened = PceObjectContainer.Open(store, "nowe", split: Sizes);
        reopened.Entries.Select(e => e.Path).Should().BeEquivalentTo(
            "zażółć gęślą jaźń.txt", "日本語/emoji 😀.bin", "katalog/średni.bin", "katalog/tekst.txt", "pusty.txt", "nowy.bin");
        Read(reopened, "katalog/średni.bin").AsSpan().SequenceEqual(content["dir/średni.bin"]).Should().BeTrue();
        Read(reopened, "katalog/tekst.txt").AsSpan().SequenceEqual(text).Should().BeTrue();
        Read(reopened, "nowy.bin").Should().Equal(Bytes(200_000, 9));
        var referenced = reopened.Entries.SelectMany(e => e.Extents).Select(e => PceObjectNames.Data(e.Object)).Distinct().Order(StringComparer.Ordinal);
        store.List("data/").Select(o => o.Name).Should().Equal(referenced);
        store.List(string.Empty).Select(o => o.Name).Should().OnlyContain(n => n == "head" || n.StartsWith("index/", StringComparison.Ordinal) || n.StartsWith("data/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writers_on_several_threads_lose_nothing()
    {
        var store = Store();
        PceObjectContainer.Create(store, Password, Fast, Sizes).Dispose();
        const int writers = 4;
        const int rounds = 5;
        var token = TestContext.Current.CancellationToken;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(
            () =>
            {
                using var container = PceObjectContainer.Open(store, Password, writable: true, split: Sizes);
                for (var round = 0; round < rounds; round++)
                {
                    container.Add([File($"w{w}/r{round}.bin", Bytes(1000 + w * 7000 + round * 3000, w * 100 + round))]);
                    if (round == 3)
                    {
                        container.Delete([$"w{w}/r2.bin"]).Should().Be(1);
                    }
                }
            },
            token)));

        using var result = PceObjectContainer.Open(store, Password, split: Sizes);
        var expected = Enumerable.Range(0, writers).SelectMany(w => Kept.Select(r => (w, r))).ToList();
        result.Entries.Select(e => e.Path).Should().BeEquivalentTo(expected.Select(x => $"w{x.w}/r{x.r}.bin"));
        foreach (var (w, r) in expected)
        {
            Read(result, $"w{w}/r{r}.bin").Should().Equal(Bytes(1000 + w * 7000 + r * 3000, w * 100 + r));
        }

        result.Generation.Should().Be((ulong)(1 + writers * (rounds + 1)));
    }
}
