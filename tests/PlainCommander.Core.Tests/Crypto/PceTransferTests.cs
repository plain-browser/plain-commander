using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>
/// A container carried between its two forms, a file and separate objects: the sealed blocks travel as they are
/// (same bytes, same entry keys), nothing is encrypted again, and a failed copy leaves nothing behind.
/// </summary>
public sealed class PceTransferTests : IDisposable
{
    private const string Password = "tajne";
    private static readonly PceOptions Fast = new() { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000, ChunkSize = 4096 };
    private static readonly PceSplitOptions Small = new() { SmallLimit = 20_000, PackSize = 60_000, PartSize = 32_768, MaxParts = 3, Parallelism = 4, ReadAhead = 16_384 };
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private static readonly byte[] Text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 4000).Select(i => $"wiersz {i} z powtarzalnym tekstem\n")));

    private static Dictionary<string, byte[]> Content() => new()
    {
        ["mały.txt"] = Bytes(700),
        ["dir/duży.bin"] = Bytes(250_000, 1),
        ["dir/średni.bin"] = Bytes(30_000, 2),
        ["dir/sub/tekst.txt"] = Text,
        ["dir/sub/pusty.txt"] = [],
        ["zażółć gęślą jaźń 日本語.bin"] = Bytes(5000, 3),
    };

    private static IEnumerable<PceSource> Sources(Dictionary<string, byte[]> content) =>
        content.Select(p => new PceSource(p.Key, false, () => new MemoryStream(p.Value), Stamp, FileAttributes.Archive | FileAttributes.ReadOnly))
            .Append(new PceSource("dir/pusty katalog", true, null, Stamp, FileAttributes.Hidden));

    private string FileContainer(string name = "box.pce")
    {
        var path = _s.P(name);
        using var container = PceContainer.Create(path, Password, Fast);
        container.Add(Sources(Content()), PceCompression.Auto);
        return path;
    }

    private static MemoryObjectStore ObjectContainer()
    {
        var store = new MemoryObjectStore();
        using var container = PceObjectContainer.Create(store, Password, Fast, Small);
        container.Add(Sources(Content()), PceCompression.Auto);
        return store;
    }

    private static byte[] Read(IPceContainer container, PceEntry entry)
    {
        using var stream = container.OpenRead(entry);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static byte[] Stored(IPceStoredBytes bytes, PceEntry entry)
    {
        using (bytes)
        {
            var stored = new byte[entry.StoredSize];
            bytes.Read(0, stored);
            return stored;
        }
    }

    /// <summary>The same entries in the same order: metadata, key, sealed bytes and content.</summary>
    private static void ShouldMatch(IPceContainer source, Func<PceEntry, byte[]> sourceStored, IPceContainer copy, Func<PceEntry, byte[]> copyStored)
    {
        copy.Entries.Select(e => e.Path).Should().Equal(source.Entries.Select(e => e.Path));
        foreach (var (a, b) in source.Entries.Zip(copy.Entries))
        {
            (b.IsDirectory, b.Size, b.ModifiedUtc, b.Attributes, b.Compressed, b.StoredSize).Should().Be((a.IsDirectory, a.Size, a.ModifiedUtc, a.Attributes, a.Compressed, a.StoredSize), a.Path);
            b.Key.Should().Equal(a.Key, "the entry keeps its key: " + a.Path);
            if (a.IsDirectory)
            {
                continue;
            }

            copyStored(b).AsSpan().SequenceEqual(sourceStored(a)).Should().BeTrue("the sealed blocks are copied as they are: " + a.Path);
            Read(copy, b).AsSpan().SequenceEqual(Read(source, a)).Should().BeTrue(a.Path);
        }
    }

    [Fact]
    public void A_file_becomes_objects_with_the_same_sealed_blocks_and_keys()
    {
        var file = FileContainer();
        var before = File.ReadAllBytes(file);
        var store = new MemoryObjectStore();
        var reported = new List<long>();
        PceTransfer.ToObjects(file, Password, store, Fast with { ChunkSize = 1 << 16 }, Small, reported.Add);
        File.ReadAllBytes(file).Should().Equal(before, "the file is only read");

        using var source = PceContainer.Open(file, Password);
        using var copy = PceObjectContainer.Open(store, Password, split: Small);
        ShouldMatch(source, e => Stored(source.OpenStored(e), e), copy, e => Stored(copy.OpenStored(e), e));
        copy.Header.ChunkSize.Should().Be(4096, "blocks are sealed for the source's block size, whatever was asked for");
        copy.Header.WrappedKey.Should().NotEqual(source.Header.WrappedKey, "the copy has its own master key");
        reported.Should().BeInAscendingOrder().And.HaveElementAt(reported.Count - 1, source.Entries.Sum(e => e.StoredSize));

        // The rules of the form apply to copied bytes as to fresh ones: small entries in a pack, the large one apart.
        copy.Find("mały.txt")!.Extents[0].Object.Should().Be(copy.Find("zażółć gęślą jaźń 日本語.bin")!.Extents[0].Object);
        copy.Find("dir/duży.bin")!.Extents.Should().HaveCount(3);
        copy.Find("dir/sub/tekst.txt")!.Compressed.Should().BeTrue();
        Read(copy, copy.Find("dir/sub/tekst.txt")!).Should().Equal(Text);
        store.OpenUploads.Should().Be(0);
    }

    [Fact]
    public void Objects_become_a_dense_file_with_the_same_sealed_blocks_and_keys()
    {
        var store = ObjectContainer();
        var names = store.Names;
        var file = _s.P("z s3.pce");
        File.WriteAllText(file, "poprzednia zawartość");
        PceTransfer.ToFile(store, Password, file, Fast, Small);
        store.Names.Should().Equal(names, "the objects are only read");
        Directory.GetFiles(_s.Root).Should().Equal(file);

        using var source = PceObjectContainer.Open(store, Password, split: Small);
        using var copy = PceContainer.Open(file, Password);
        ShouldMatch(source, e => Stored(source.OpenStored(e), e), copy, e => Stored(copy.OpenStored(e), e));
        copy.Slack.Should().Be(0);
        copy.Entries.Where(e => !e.IsDirectory && e.StoredSize > 0).Should().OnlyContain(e => e.Extents.Count == 1);

        // And it is an ordinary .pce file to everything else.
        var folder = _s.Dir("rozpakowane");
        PceFile.Decrypt(file, Password, folder, null, TestContext.Current.CancellationToken);
        File.ReadAllBytes(Directory.GetFiles(folder, "duży.bin", SearchOption.AllDirectories).Single()).Should().Equal(Content()["dir/duży.bin"]);
    }

    [Fact]
    public void There_and_back_gives_the_file_it_started_from_entry_for_entry()
    {
        var file = FileContainer();
        var store = new MemoryObjectStore();
        PceTransfer.ToObjects(file, Password, store, Fast, Small);
        var back = _s.P("z powrotem.pce");
        PceTransfer.ToFile(store, Password, back, Fast, Small);

        new FileInfo(back).Length.Should().Be(new FileInfo(file).Length);
        using var first = PceContainer.Open(file, Password);
        using var second = PceContainer.Open(back, Password);
        ShouldMatch(first, e => Stored(first.OpenStored(e), e), second, e => Stored(second.OpenStored(e), e));
        second.Entries.SelectMany(e => e.Extents).Select(e => (e.Offset, e.Length)).Should().Equal(first.Entries.SelectMany(e => e.Extents).Select(e => (e.Offset, e.Length)));
    }

    [Fact]
    public void A_changed_container_carries_over_what_it_holds_now()
    {
        var file = FileContainer();
        using (var container = PceContainer.Open(file, Password, writable: true))
        {
            container.Delete(["dir/średni.bin", "mały.txt"]);
            container.Rename("dir/sub", "inny");
            container.ChangePassword("nowe");
        }

        var store = new MemoryObjectStore();
        PceTransfer.ToObjects(file, "nowe", store, Fast, Small);
        using var copy = PceObjectContainer.Open(store, "nowe", split: Small);
        copy.Entries.Select(e => e.Path).Should().BeEquivalentTo("dir/duży.bin", "inny/tekst.txt", "inny/pusty.txt", "zażółć gęślą jaźń 日本語.bin", "dir/pusty katalog");
        Read(copy, copy.Find("dir/duży.bin")!).Should().Equal(Content()["dir/duży.bin"]);
        Read(copy, copy.Find("inny/tekst.txt")!).Should().Equal(Text);
        copy.Find("dir/pusty katalog")!.Attributes.Should().Be(FileAttributes.Hidden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_of_the_first_layout_is_encrypted_anew_on_the_way_and_stays_as_it_was(bool folder)
    {
        string source;
        if (folder)
        {
            source = _s.Dir("skany");
            File.WriteAllBytes(Path.Combine(source, "a.bin"), Bytes(40_000));
            Directory.CreateDirectory(Path.Combine(source, "sub", "pusty"));
            File.WriteAllBytes(Path.Combine(source, "sub", "tekst.txt"), Text);
        }
        else
        {
            source = _s.P("film.bin");
            File.WriteAllBytes(source, Bytes(120_000, 5));
        }

        var file = _s.P("stary.pce");
        var token = TestContext.Current.CancellationToken;
        PceFile.EncryptLegacy(source, file, Password, Fast, PceCompression.Auto, null, token);
        PceContainer.LayoutVersion(file).Should().Be(1);
        var before = File.ReadAllBytes(file);

        var store = new MemoryObjectStore();
        PceTransfer.ToObjects(file, Password, store, Fast, Small, null, token);
        File.ReadAllBytes(file).Should().Equal(before, "the file is not rewritten on the user's disk");
        Directory.GetFiles(_s.Root).Should().Equal(folder ? [file] : new[] { source, file }.Order());

        using var copy = PceObjectContainer.Open(store, Password, split: Small);
        if (folder)
        {
            copy.Entries.Select(e => e.Path).Should().BeEquivalentTo("skany/a.bin", "skany/sub/pusty", "skany/sub/tekst.txt");
            Read(copy, copy.Find("skany/a.bin")!).Should().Equal(Bytes(40_000));
            Read(copy, copy.Find("skany/sub/tekst.txt")!).Should().Equal(Text);
            copy.Find("skany/sub/pusty")!.IsDirectory.Should().BeTrue();
        }
        else
        {
            Read(copy, copy.Entries.Should().ContainSingle().Which).Should().Equal(Bytes(120_000, 5));
            copy.Entries[0].Path.Should().Be("film.bin");
        }
    }

    [Fact]
    public void A_wrong_password_or_a_taken_place_makes_nothing()
    {
        var file = FileContainer();
        var store = new MemoryObjectStore();
        ((Action)(() => PceTransfer.ToObjects(file, "złe", store, Fast, Small))).Should().Throw<PceWrongPasswordException>();
        store.Names.Should().BeEmpty();
        store.Requests.Should().Be(0);

        var legacy = _s.P("stary.pce");
        var plain = _s.File("plik.txt", "treść");
        PceFile.EncryptLegacy(plain, legacy, Password, Fast, PceCompression.None, null, TestContext.Current.CancellationToken);
        ((Action)(() => PceTransfer.ToObjects(legacy, "złe", store, Fast, Small))).Should().Throw<PceWrongPasswordException>();
        store.Names.Should().BeEmpty();

        var taken = ObjectContainer();
        var names = taken.Names;
        ((Action)(() => PceTransfer.ToObjects(file, Password, taken, Fast, Small))).Should().Throw<IOException>().WithMessage("*already*");
        taken.Names.Should().Equal(names, "what was there is not ours to remove");

        var target = _s.P("cel.pce");
        File.WriteAllText(target, "zostaje");
        ((Action)(() => PceTransfer.ToFile(taken, "złe", target, Fast, Small))).Should().Throw<PceWrongPasswordException>();
        ((Action)(() => PceTransfer.ToFile(new MemoryObjectStore(), Password, target, Fast, Small))).Should().Throw<PceFormatException>();
        File.ReadAllText(target).Should().Be("zostaje");
    }

    [Fact]
    public void A_copy_to_objects_that_fails_at_any_request_leaves_the_store_empty()
    {
        var file = FileContainer();
        int requests;
        {
            var whole = new MemoryObjectStore();
            PceTransfer.ToObjects(file, Password, whole, Fast, Small);
            requests = whole.Requests;
        }

        requests.Should().BeGreaterThan(10);
        foreach (var answerLost in new[] { false, true })
        {
            for (var fail = 1; fail <= requests; fail++)
            {
                var store = new MemoryObjectStore { FailAt = fail, FailAfterEffect = answerLost };
                var because = $"request {fail} of {requests} {(answerLost ? "lost its answer" : "never arrived")}";
                var failed = false;
                try
                {
                    PceTransfer.ToObjects(file, Password, store, Fast, Small);
                }
                catch (IOException)
                {
                    failed = true;
                }

                if (failed)
                {
                    store.Names.Should().BeEmpty(because);
                }
                else
                {
                    // Only a commit of an index can lose its answer and still count: the container reads the
                    // object back and finds its own bytes. Then the copy is whole.
                    answerLost.Should().BeTrue(because);
                    store.Log[fail - 1].Should().StartWith("PUTNEW index/", because);
                    using var made = PceObjectContainer.Open(store, Password, split: Small);
                    made.Entries.Should().HaveCount(7, because);
                }

                store.OpenUploads.Should().Be(0, because);
            }
        }
    }

    [Fact]
    public void A_copy_to_a_file_that_is_cancelled_or_fails_leaves_no_file()
    {
        var store = ObjectContainer();
        var target = _s.P("cel.pce");
        using var cancel = new CancellationTokenSource();
        var copy = () => PceTransfer.ToFile(
            store,
            Password,
            target,
            Fast,
            Small,
            done =>
            {
                if (done > 100_000)
                {
                    cancel.Cancel();
                }
            },
            cancel.Token);
        copy.Should().Throw<OperationCanceledException>();
        Directory.GetFileSystemEntries(_s.Root).Should().BeEmpty();

        File.WriteAllText(target, "zostaje");
        var broken = store.Clone();
        broken.Remove(broken.Names.First(n => n.StartsWith("data/", StringComparison.Ordinal)));
        ((Action)(() => PceTransfer.ToFile(broken, Password, target, Fast, Small))).Should().Throw<PceObjectMissingException>();
        File.ReadAllText(target).Should().Be("zostaje");
        Directory.GetFileSystemEntries(_s.Root).Should().Equal(target);
    }

    [Fact]
    public void An_empty_container_carries_over_as_an_empty_container()
    {
        var file = _s.P("pusty.pce");
        PceContainer.Create(file, Password, Fast).Dispose();
        var store = new MemoryObjectStore();
        PceTransfer.ToObjects(file, Password, store, Fast, Small);
        using (var copy = PceObjectContainer.Open(store, Password, split: Small))
        {
            copy.Entries.Should().BeEmpty();
        }

        var back = _s.P("z powrotem.pce");
        PceTransfer.ToFile(store, Password, back, Fast, Small);
        new FileInfo(back).Length.Should().Be(new FileInfo(file).Length);
        using var again = PceContainer.Open(back, Password);
        again.Entries.Should().BeEmpty();
    }
}
