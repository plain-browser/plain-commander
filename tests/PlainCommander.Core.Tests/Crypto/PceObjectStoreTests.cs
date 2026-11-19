using System.Security.Cryptography;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>
/// The ground under a .pce container kept as separate objects (S3): the header flag and the index whose extents name
/// data objects, the object names, and the two stores the container is tested on.
/// </summary>
public sealed class PceObjectStoreTests : IDisposable
{
    private const string Password = "tajne";
    private const int HeaderSize = PceContainerFormat.HeaderSize;
    private const int FooterSize = PceContainerFormat.FooterSize;
    private static readonly PceOptions Fast = new() { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000, ChunkSize = 4096 };
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static readonly int[] UploadOrder = [5, 2, 7, 1, 6, 3, 4];

    public static TheoryData<string> Stores => ["memory", "directory"];

    private IPceObjectStore Store(string kind) =>
        kind == "memory" ? new MemoryObjectStore() : new DirectoryPceObjectStore(_s.P("store-" + Guid.NewGuid().ToString("N")[..8]));

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private static PceEntry Entry(string path, long size, params PceExtent[] extents) =>
        new(path, false, size, Stamp, FileAttributes.Archive, compressed: size % 2 == 1, RandomNumberGenerator.GetBytes(PceFormat.KeySize), [.. extents]);

    private static PceExtent In(PceObjectId id, long offset, long length) => new(offset, length) { Object = id };

    private static List<PceEntry> SplitEntries()
    {
        var big = PceObjectId.New();
        var pack = PceObjectId.New();
        return
        [
            Entry("duży.bin", 9_000_000, In(big, 0, 9_000_000 + 2198 * 16)),
            Entry("katalog/mały.txt", 301, In(pack, 0, 317)),
            Entry("katalog/drugi.txt", 5000, In(pack, 317, 4096 + 16), In(PceObjectId.New(), 12, 904 + 16)),
            new("pusty", true, 0, Stamp, FileAttributes.None, false, RandomNumberGenerator.GetBytes(PceFormat.KeySize), []),
        ];
    }

    private static (PceContainerHeader Header, byte[] Master) NewSplit()
    {
        var master = RandomNumberGenerator.GetBytes(PceFormat.KeySize);
        return (PceContainerFormat.NewHeader(Fast, Password, master, split: true), master);
    }

    [Fact]
    public void A_split_header_keeps_its_flag_and_unwraps_the_master_key()
    {
        var (header, master) = NewSplit();
        var bytes = PceContainerFormat.WriteHeader(header);
        bytes.Should().HaveCount(HeaderSize);
        var read = PceContainerFormat.ReadHeader(bytes, split: true);
        read.IsSplit.Should().BeTrue();
        read.Flags.Should().Be(PceLayout.NfcPassword | PceLayout.Split);
        PceContainerFormat.Unwrap(read, Password).Should().Equal(master);
        PceContainerFormat.Unwrap(read, "inne").Should().BeNull();
    }

    [Fact]
    public void The_flag_is_under_the_wrapped_key_tag()
    {
        var (header, _) = NewSplit();
        var bytes = PceContainerFormat.WriteHeader(header);
        bytes[4] ^= (byte)PceLayout.Split;
        PceContainerFormat.Unwrap(PceContainerFormat.ReadHeader(bytes), Password).Should().BeNull("a head object cannot be passed off as the start of a file");
    }

    [Fact]
    public void A_header_of_one_kind_is_refused_where_the_other_is_expected()
    {
        var (split, master) = NewSplit();
        var file = PceContainerFormat.NewHeader(Fast, Password, master);
        var asFile = () => PceContainerFormat.ReadHeader(PceContainerFormat.WriteHeader(split));
        asFile.Should().Throw<PceFormatException>().WithMessage("*separate objects*");
        var asSplit = () => PceContainerFormat.ReadHeader(PceContainerFormat.WriteHeader(file), split: true);
        asSplit.Should().Throw<PceFormatException>().WithMessage("*container file*");
    }

    [Fact]
    public void The_head_object_saved_as_a_file_does_not_open_as_a_container()
    {
        var (header, _) = NewSplit();
        var path = _s.P("head.pce");
        File.WriteAllBytes(path, PceContainerFormat.WriteHeader(header));
        var open = () => PceContainer.Open(path, Password);
        open.Should().Throw<PceFormatException>().WithMessage("*separate objects*");
        var peek = () => PceFile.Peek(path, Password);
        peek.Should().Throw<PceFormatException>().WithMessage("*separate objects*");
    }

    [Fact]
    public void A_split_index_round_trips_with_the_object_of_every_extent()
    {
        var (header, master) = NewSplit();
        var entries = SplitEntries();
        var sealedIndex = PceContainerFormat.SealIndex(header, master, entries, 0, 7);
        sealedIndex.Should().HaveCount((int)PceContainerFormat.SealedIndexSize(entries, split: true));
        var index = sealedIndex.AsSpan(0, sealedIndex.Length - HeaderSize - FooterSize);
        var footer = sealedIndex.AsSpan(sealedIndex.Length - FooterSize);
        sealedIndex.AsSpan(index.Length, HeaderSize).ToArray().Should().Equal(PceContainerFormat.WriteHeader(header));

        var read = PceContainerFormat.OpenIndex(header.Cipher, master, index, footer, split: true)!;
        read.Should().HaveCount(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            read[i].Path.Should().Be(entries[i].Path);
            read[i].IsDirectory.Should().Be(entries[i].IsDirectory);
            read[i].Size.Should().Be(entries[i].Size);
            read[i].ModifiedUtc.Should().Be(entries[i].ModifiedUtc);
            read[i].Attributes.Should().Be(entries[i].Attributes);
            read[i].Compressed.Should().Be(entries[i].Compressed);
            read[i].Key.Should().Equal(entries[i].Key);
            read[i].Extents.Select(e => (e.Object, e.Offset, e.Length)).Should().Equal(entries[i].Extents.Select(e => (e.Object, e.Offset, e.Length)));
            read[i].Extents.Should().OnlyContain(e => !e.Object.IsNone);
        }
    }

    [Fact]
    public void An_index_of_one_kind_does_not_open_as_the_other()
    {
        var (split, master) = NewSplit();
        var file = PceContainerFormat.NewHeader(Fast, Password, master);
        var entries = SplitEntries();
        var asSplit = PceContainerFormat.SealIndex(split, master, entries, 0, 1);
        var asFile = PceContainerFormat.SealIndex(file, master, entries, 94, 1);
        var open = (byte[] sealedIndex, bool expectSplit) => PceContainerFormat.OpenIndex(
            PceCipherKindOf(split),
            master,
            sealedIndex.AsSpan(0, sealedIndex.Length - HeaderSize - FooterSize),
            sealedIndex.AsSpan(sealedIndex.Length - FooterSize),
            expectSplit);
        open(asSplit, true).Should().NotBeNull();
        open(asFile, false).Should().NotBeNull();
        ((Action)(() => open(asSplit, false))).Should().Throw<PceFormatException>();
        ((Action)(() => open(asFile, true))).Should().Throw<PceFormatException>();
    }

    private static PceCipherKind PceCipherKindOf(PceContainerHeader header) => header.Cipher;

    [Fact]
    public void Changing_any_byte_of_an_index_object_is_detected()
    {
        var (header, master) = NewSplit();
        var sealedIndex = PceContainerFormat.SealIndex(header, master, SplitEntries(), 0, 3);
        var indexLength = sealedIndex.Length - HeaderSize - FooterSize;
        for (var i = 0; i < sealedIndex.Length; i++)
        {
            var damaged = (byte[])sealedIndex.Clone();
            damaged[i] ^= 0x40;
            if (i >= indexLength && i < indexLength + HeaderSize)
            {
                continue; // the header copy answers for itself: next test
            }

            var footer = damaged.AsSpan(damaged.Length - FooterSize);
            PceContainerFormat.OpenIndex(header.Cipher, master, damaged.AsSpan(0, indexLength), footer, split: true)
                .Should().BeNull($"byte {i} changed");
        }

        // A cut index, and one a byte longer.
        var whole = sealedIndex.AsSpan(sealedIndex.Length - FooterSize).ToArray();
        PceContainerFormat.OpenIndex(header.Cipher, master, sealedIndex.AsSpan(0, indexLength - 1), whole, split: true).Should().BeNull();
        PceContainerFormat.OpenIndex(header.Cipher, master, sealedIndex.AsSpan(0, indexLength + 1), whole, split: true).Should().BeNull();
        PceContainerFormat.OpenIndex(header.Cipher, master, [], whole, split: true).Should().BeNull();
    }

    [Fact]
    public void Changing_any_byte_of_the_head_object_is_detected()
    {
        var (header, _) = NewSplit();
        var bytes = PceContainerFormat.WriteHeader(header);
        for (var i = 0; i < bytes.Length; i++)
        {
            var damaged = (byte[])bytes.Clone();
            damaged[i] ^= 0x40;
            byte[]? master = null;
            try
            {
                master = PceContainerFormat.Unwrap(PceContainerFormat.ReadHeader(damaged, split: true), Password);
            }
            catch (PceFormatException)
            {
            }

            master.Should().BeNull($"byte {i} changed");
        }
    }

    [Fact]
    public void The_newest_index_sorts_first()
    {
        ulong[] generations = [0, 1, 2, 9, 10, 255, 256, 65_535, 1UL << 40, ulong.MaxValue - 1, ulong.MaxValue];
        var names = generations.Select(PceObjectNames.Index).ToList();
        names.Should().OnlyContain(n => n.Length == "index/".Length + 16);
        names.OrderBy(n => n, StringComparer.Ordinal).Should().Equal(names.AsEnumerable().Reverse());
        foreach (var generation in generations)
        {
            PceObjectNames.TryGeneration(PceObjectNames.Index(generation), out var back).Should().BeTrue();
            back.Should().Be(generation);
        }

        PceObjectNames.Index(1).Should().Be("index/fffffffffffffffe");
    }

    [Theory]
    [InlineData("index/FFFFFFFFFFFFFFFE")]
    [InlineData("index/fffffffffffffe")]
    [InlineData("index/fffffffffffffffe0")]
    [InlineData("index/ffffffffffffffg0")]
    [InlineData("index/")]
    [InlineData("data/fffffffffffffffe")]
    [InlineData("fffffffffffffffe")]
    [InlineData("head")]
    public void A_name_that_is_not_an_index_has_no_generation(string name) =>
        PceObjectNames.TryGeneration(name, out _).Should().BeFalse();

    [Fact]
    public void A_data_object_is_named_by_its_id()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => PceObjectId.New()).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(id => !id.IsNone);
        foreach (var id in ids)
        {
            var name = PceObjectNames.Data(id);
            name.Should().MatchRegex("^data/[0-9a-f]{32}$");
            PceObjectNames.TryData(name, out var back).Should().BeTrue();
            back.Should().Be(id);
            var bytes = new byte[PceObjectId.Size];
            id.Write(bytes);
            Convert.ToHexStringLower(bytes).Should().Be(name["data/".Length..], "the name is the bytes the index stores");
            PceObjectId.Read(bytes).Should().Be(id);
        }
    }

    [Theory]
    [InlineData("data/")]
    [InlineData("data/00000000000000000000000000000000")]
    [InlineData("data/0123456789ABCDEF0123456789abcdef")]
    [InlineData("data/0123456789abcdef0123456789abcde")]
    [InlineData("data/0123456789abcdef0123456789abcdef0")]
    [InlineData("data/0123456789abcdef0123456789abcdeg")]
    [InlineData("index/0123456789abcdef0123456789abcdef")]
    [InlineData("data/x/0123456789abcdef0123456789abcdef")]
    public void A_name_that_is_not_a_data_object_has_no_id(string name) =>
        PceObjectNames.TryData(name, out _).Should().BeFalse();

    [Theory]
    [MemberData(nameof(Stores))]
    public void An_object_is_stored_read_by_range_and_replaced(string kind)
    {
        var store = Store(kind);
        var content = Bytes(10_000);
        store.Get("head").Should().BeNull();
        store.Put("head", content);
        store.Get("head").Should().Equal(content);

        var buffer = new byte[300];
        store.Read("head", 0, buffer).Should().Be(300);
        buffer.Should().Equal(content[..300]);
        store.Read("head", 9_900, buffer).Should().Be(100);
        buffer[..100].Should().Equal(content[9_900..]);
        store.Read("head", 10_000, buffer).Should().Be(0);
        store.Read("head", 20_000, buffer).Should().Be(0);

        store.Put("head", [1, 2, 3]);
        store.Get("head").Should().Equal(1, 2, 3);
        store.Put("data/empty", []);
        store.Get("data/empty").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void Reading_a_missing_object_says_so(string kind)
    {
        var store = Store(kind);
        var read = () => store.Read("data/0123456789abcdef0123456789abcdef", 0, new byte[10]);
        read.Should().Throw<PceObjectMissingException>().Which.ObjectName.Should().Be("data/0123456789abcdef0123456789abcdef");
        store.Put("data/a", [1]);
        store.Delete("data/a");
        ((Action)(() => store.Read("data/a", 0, new byte[1]))).Should().Throw<PceObjectMissingException>();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void A_name_is_taken_only_once(string kind)
    {
        var store = Store(kind);
        var name = PceObjectNames.Index(5);
        store.PutNew(name, [1, 1]).Should().BeTrue();
        store.PutNew(name, [2, 2]).Should().BeFalse();
        store.Get(name).Should().Equal(1, 1);
        store.Delete(name);
        store.PutNew(name, [3]).Should().BeTrue();
        store.Get(name).Should().Equal(3);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void Of_many_writers_committing_one_generation_exactly_one_wins(string kind)
    {
        var store = Store(kind);
        for (var round = 0; round < 20; round++)
        {
            var name = PceObjectNames.Index((ulong)round);
            var wins = new int[16];
            Parallel.For(0, wins.Length, i => wins[i] = store.PutNew(name, [(byte)i, (byte)i, (byte)i]) ? 1 : 0);
            wins.Sum().Should().Be(1);
            var winner = Array.IndexOf(wins, 1);
            store.Get(name).Should().Equal((byte)winner, (byte)winner, (byte)winner);
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void An_upload_in_parts_appears_whole_when_completed(string kind)
    {
        var store = Store(kind);
        var parts = Enumerable.Range(1, 7).Select(n => Bytes(n == 7 ? 123 : 50_000, seed: n)).ToArray();
        var name = PceObjectNames.Data(PceObjectId.New());
        using (var upload = store.BeginUpload(name))
        {
            Parallel.ForEach(UploadOrder, n => upload.Write(n, parts[n - 1]));
            store.Get(name).Should().BeNull("nothing shows before the upload is completed");
            store.List("data/").Should().BeEmpty();
            upload.Complete();
        }

        store.Get(name).Should().Equal(parts.SelectMany(p => p));
        store.List("").Select(o => o.Name).Should().Equal(name);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void An_upload_that_is_not_completed_leaves_nothing(string kind)
    {
        var store = Store(kind);
        var name = PceObjectNames.Data(PceObjectId.New());
        using (var upload = store.BeginUpload(name))
        {
            upload.Write(1, Bytes(1000));
            upload.Write(2, Bytes(1000, seed: 1));
        }

        store.Get(name).Should().BeNull();
        store.List("").Should().BeEmpty();
        if (store is MemoryObjectStore memory)
        {
            memory.OpenUploads.Should().Be(0);
        }
        else
        {
            Directory.EnumerateFiles(_s.Root, "*", SearchOption.AllDirectories).Should().BeEmpty("the parts were temporary files");
        }
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public void Listing_takes_a_prefix_and_returns_names_in_order(string kind)
    {
        var store = Store(kind);
        var data = Enumerable.Range(0, 5).Select(_ => PceObjectNames.Data(PceObjectId.New())).ToList();
        foreach (var name in data)
        {
            store.Put(name, Bytes(10));
        }

        store.Put("head", Bytes(94));
        foreach (var generation in new ulong[] { 3, 1, 2 })
        {
            store.PutNew(PceObjectNames.Index(generation), Bytes(40 + (int)generation));
        }

        store.List("index/").Select(o => o.Name).Should().Equal(PceObjectNames.Index(3), PceObjectNames.Index(2), PceObjectNames.Index(1));
        var newest = store.List("index/", 1).Should().ContainSingle().Which;
        newest.Name.Should().Be(PceObjectNames.Index(3));
        newest.Size.Should().Be(43);
        newest.ModifiedUtc.Kind.Should().Be(DateTimeKind.Utc);
        store.List("data/").Select(o => o.Name).Should().Equal(data.OrderBy(n => n, StringComparer.Ordinal));
        store.List("").Should().HaveCount(9);
        store.List("nic/").Should().BeEmpty();

        store.Delete(PceObjectNames.Index(3));
        store.Delete(PceObjectNames.Index(3));
        store.Delete("nic/nic");
        store.List("index/", 1).Single().Name.Should().Be(PceObjectNames.Index(2));
    }

    [Theory]
    [InlineData("../poza")]
    [InlineData("data/../../poza")]
    [InlineData("data//x")]
    [InlineData("/head")]
    [InlineData(".tmp/x")]
    [InlineData("data\\x")]
    [InlineData("")]
    public void The_directory_store_keeps_names_inside_its_folder(string name)
    {
        var store = Store("directory");
        ((Action)(() => store.Put(name, [1]))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Get(name))).Should().Throw<ArgumentException>();
        ((Action)(() => store.Delete(name))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_directory_store_survives_being_reopened()
    {
        var root = _s.P("box.pce");
        new DirectoryPceObjectStore(root).Put("index/00000000000000ff", [9, 9]);
        var reopened = new DirectoryPceObjectStore(root);
        reopened.Get("index/00000000000000ff").Should().Equal([9, 9]);
        reopened.List("").Select(o => o.Name).Should().Equal("index/00000000000000ff");
        File.Exists(Path.Combine(root, "index", "00000000000000ff")).Should().BeTrue();
    }

    [Fact]
    public void A_request_lost_on_the_way_changes_nothing()
    {
        var store = new MemoryObjectStore();
        store.Put("head", [1]);
        store.FailAt = store.Requests + 1;
        ((Action)(() => store.Put("head", [2]))).Should().Throw<IOException>().WithMessage(MemoryObjectStore.Crash);
        store.Content("head").Should().Equal([1]);
        store.FailAt = store.Requests + 1;
        ((Action)(() => store.PutNew("index/x", [2]))).Should().Throw<IOException>();
        store.Content("index/x").Should().BeNull();
        store.FailAt = store.Requests + 1;
        ((Action)(() => store.Delete("head"))).Should().Throw<IOException>();
        store.Content("head").Should().Equal([1]);
        store.Put("head", [3]);
        store.Content("head").Should().Equal([3], "only the chosen request fails");
    }

    [Fact]
    public void A_request_whose_answer_is_lost_still_took_effect()
    {
        var store = new MemoryObjectStore { FailAfterEffect = true };
        store.FailAt = 1;
        ((Action)(() => store.PutNew("index/x", [7]))).Should().Throw<IOException>();
        store.Content("index/x").Should().Equal([7]);
        store.PutNew("index/x", [8]).Should().BeFalse("the retry finds its own object");
        store.FailAt = store.Requests + 1;
        ((Action)(() => store.Delete("index/x"))).Should().Throw<IOException>();
        store.Content("index/x").Should().BeNull();
    }

    [Fact]
    public void The_memory_store_counts_and_logs_requests_and_can_fail_inside_an_upload()
    {
        var store = new MemoryObjectStore();
        store.Put("head", [1]);
        store.Get("head");
        store.Read("head", 0, new byte[1]);
        store.List("index/", 1);
        store.Log.Should().Equal("PUT head", "GET head", "GET head 0+1", "LIST index/");
        store.Requests.Should().Be(4);

        store.FailAt = 7; // BEGIN is 5, part 1 is 6, part 2 is 7
        using (var upload = store.BeginUpload("data/a"))
        {
            upload.Write(1, new byte[] { 1 });
            ((Action)(() => upload.Write(2, new byte[] { 2 }))).Should().Throw<IOException>();
            store.OpenUploads.Should().Be(1);
        }

        store.OpenUploads.Should().Be(0);
        store.Names.Should().Equal("head");

        var later = store.Clone();
        later.Now = store.Now.AddHours(25);
        later.Put("data/b", [1]);
        later.List("").Select(o => o.ModifiedUtc).Should().Equal(later.Now, store.Now);
        store.Names.Should().Equal("head");
    }
}
