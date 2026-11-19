using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>
/// The .pce container kept as separate objects: what lands in the store, changes as single commits, a failure
/// injected at every request of a change, two writers at once, and what cleaning up removes.
/// </summary>
public sealed class PceObjectContainerTests
{
    private const string Password = "tajne";

    // PBKDF2 at its floor and sizes a thousand times smaller than the real ones: 4 KiB blocks, entries under 20 kB
    // go to packs of up to 60 kB, parts are 32 KiB and an object takes three of them.
    private static readonly PceOptions Fast = new() { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000, ChunkSize = 4096 };
    private static readonly PceSplitOptions Small = new() { SmallLimit = 20_000, PackSize = 60_000, PartSize = 32_768, MaxParts = 3, Parallelism = 4, ReadAhead = 16_384 };
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    /// <summary>This computer's clock as the containers under test see it.</summary>
    private DateTime _clock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private PceSplitOptions? _sizes;

    private PceSplitOptions Sizes => _sizes ??= Small with { UtcNow = () => _clock };

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private static PceSource File(string path, byte[] content) => new(path, false, () => new MemoryStream(content), Stamp, FileAttributes.Archive);

    private static PceSource Folder(string path) => new(path, true, null, Stamp, FileAttributes.None);

    private PceObjectContainer Create(MemoryObjectStore store) => PceObjectContainer.Create(store, Password, Fast, Sizes);

    private PceObjectContainer Open(MemoryObjectStore store, bool writable = false, string password = Password, ulong known = 0) =>
        PceObjectContainer.Open(store, password, writable, "box.pce", Fast, Sizes, known);

    private static byte[] Read(PceObjectContainer container, string path)
    {
        using var stream = container.OpenRead(container.Find(path)!);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private MemoryObjectStore Built(params (string Path, byte[] Content)[] files)
    {
        var store = new MemoryObjectStore();
        using var container = Create(store);
        container.Add(files.Select(f => File(f.Path, f.Content)));
        return store;
    }

    private static List<string> Data(MemoryObjectStore store) => store.Names.Where(n => n.StartsWith("data/", StringComparison.Ordinal)).ToList();

    private static List<string> Indexes(MemoryObjectStore store) => store.Names.Where(n => n.StartsWith("index/", StringComparison.Ordinal)).ToList();

    /// <summary>Head, at most two indexes, and exactly the data objects the entries refer to.</summary>
    private static void ShouldHoldNothingExtra(MemoryObjectStore store, PceObjectContainer container)
    {
        var referenced = container.Entries.SelectMany(e => e.Extents).Select(e => PceObjectNames.Data(e.Object)).Distinct().Order(StringComparer.Ordinal);
        Data(store).Should().Equal(referenced);
        Indexes(store).Should().Contain(PceObjectNames.Index(container.Generation));
        store.Names.Should().Contain("head");
        store.Names.Should().HaveCount(1 + Indexes(store).Count + Data(store).Count);
        store.OpenUploads.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(3 * 4096 + 7)]
    [InlineData(19_900)]
    [InlineData(20_100)]
    [InlineData(32_768)]
    [InlineData(100_000)]
    [InlineData(3 * 32_768)]
    [InlineData(250_000)]
    public void Round_trips_a_file_of_any_size_with_its_metadata(int size)
    {
        var content = Bytes(size);
        var store = Built(("dane.bin", content));
        using var container = Open(store);
        var entry = container.Entries.Should().ContainSingle().Which;
        entry.Path.Should().Be("dane.bin");
        entry.Size.Should().Be(size);
        entry.ModifiedUtc.Should().Be(Stamp);
        entry.Attributes.Should().Be(FileAttributes.Archive);
        entry.StoredSize.Should().Be(size + (size + 4095) / 4096 * 16);
        Read(container, "dane.bin").Should().Equal(content);
        ShouldHoldNothingExtra(store, container);
    }

    [Fact]
    public void Small_entries_share_a_pack_and_a_large_one_has_its_own_objects()
    {
        var store = Built(("a.txt", Bytes(5000)), ("duży.bin", Bytes(250_000, 1)), ("b.txt", Bytes(7000, 2)), ("pusty.txt", []), ("c.txt", Bytes(300, 3)));
        using var container = Open(store);
        var a = container.Find("a.txt")!.Extents.Should().ContainSingle().Which;
        var b = container.Find("b.txt")!.Extents.Should().ContainSingle().Which;
        var c = container.Find("c.txt")!.Extents.Should().ContainSingle().Which;
        b.Object.Should().Be(a.Object);
        c.Object.Should().Be(a.Object);
        (a.Offset, b.Offset, c.Offset).Should().Be((0L, a.Length, a.Length + b.Length));
        container.Find("pusty.txt")!.Extents.Should().BeEmpty();

        // 250 000 bytes + 62 tags = 250 992 stored: two full objects of three parts, then a piece of its own.
        var big = container.Find("duży.bin")!.Extents;
        big.Select(e => e.Length).Should().Equal(98_304, 98_304, 54_384);
        big.Select(e => e.Offset).Should().OnlyContain(o => o == 0);
        big.Select(e => e.Object).Should().OnlyHaveUniqueItems().And.NotContain(a.Object);
        Data(store).Should().HaveCount(4);
        ShouldHoldNothingExtra(store, container);
    }

    [Fact]
    public void A_pack_is_stored_when_the_next_entry_would_not_fit()
    {
        var store = Built(Enumerable.Range(0, 10).Select(i => ($"f{i}.bin", Bytes(15_000, i))).ToArray());
        using var container = Open(store);
        var packs = container.Entries.Select(e => e.Extents.Single().Object).Distinct().ToList();
        packs.Should().HaveCount(4, "three entries of 15 064 stored bytes fit under 60 000, the fourth does not");
        foreach (var name in Data(store))
        {
            store.Content(name)!.Length.Should().BeLessThanOrEqualTo(Small.PackSize);
        }

        for (var i = 0; i < 10; i++)
        {
            Read(container, $"f{i}.bin").Should().Equal(Bytes(15_000, i));
        }
    }

    [Fact]
    public void The_store_shows_no_names_and_no_content()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("tajny tekst, którego nie widać. ", 2000)));
        var store = Built(("umowa-kowalski.txt", text), ("katalog/zdjęcie.bin", Bytes(40_000)));
        store.Names.Should().OnlyContain(n => n == "head" || System.Text.RegularExpressions.Regex.IsMatch(n, "^(index/[0-9a-f]{16}|data/[0-9a-f]{32})$"));
        foreach (var name in store.Names)
        {
            var content = Encoding.Latin1.GetString(store.Content(name)!);
            content.Should().NotContain("kowalski").And.NotContain("katalog").And.NotContain("tajny tekst");
        }
    }

    [Fact]
    public void Folders_compression_and_seeking_work_as_in_a_container_file()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"wiersz {i} z powtarzalnym tekstem\n")));
        var random = Bytes(120_000);
        var store = new MemoryObjectStore();
        using (var created = Create(store))
        {
            created.Add([File("dir\\tekst.txt", text), File("dir/sub/losowe.bin", random), Folder("dir/pusty"), Folder("drugi\\")], PceCompression.Auto);
        }

        using var container = Open(store);
        container.Entries.Select(e => e.Path).Should().Equal("dir/tekst.txt", "dir/sub/losowe.bin", "dir/pusty", "drugi");
        container.Find("DIR/Pusty")!.IsDirectory.Should().BeTrue();
        var compressed = container.Find("dir/tekst.txt")!;
        compressed.Compressed.Should().BeTrue();
        compressed.StoredSize.Should().BeLessThan(text.Length / 4);
        Read(container, "dir/tekst.txt").Should().Equal(text);
        container.Find("dir/sub/losowe.bin")!.Compressed.Should().BeFalse();

        using var stream = container.OpenRead(container.Find("dir/sub/losowe.bin")!);
        stream.CanSeek.Should().BeTrue();
        stream.Length.Should().Be(random.Length);
        var buffer = new byte[5000];
        foreach (var position in new[] { 119_000, 0, 4090, 98_300, 65_536, 115_000 })
        {
            stream.Position = position;
            stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false).Should().Be(Math.Min(5000, random.Length - position));
            buffer.AsSpan(0, Math.Min(5000, random.Length - position)).ToArray().Should().Equal(random.Skip(position).Take(5000));
        }

        var open = () => container.OpenRead(container.Find("drugi")!);
        open.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Opening_takes_three_requests_whatever_the_container_holds()
    {
        var store = Built(("a.bin", Bytes(250_000)), ("b.bin", Bytes(100)), ("c.bin", Bytes(50_000, 1)));
        store.Log.Clear();
        using var container = Open(store);
        store.Log.Should().Equal("LIST index/", "GET " + PceObjectNames.Index(container.Generation), "GET head");
        container.Generation.Should().Be(2, "one commit made it, one added the files");
    }

    [Fact]
    public void Reading_on_fetches_ahead_and_a_jump_fetches_only_its_block()
    {
        var content = Bytes(97_000); // one object of three parts minus a little: 24 blocks
        var store = Built(("film.bin", content));
        using var container = Open(store);
        store.Log.Clear();
        Read(container, "film.bin").Should().Equal(content);
        var ranged = store.Log.Count;
        ranged.Should().BeLessThan(12, "24 blocks read in a row come in windows of up to 16 KiB, not one request each");

        store.Log.Clear();
        using var stream = container.OpenRead(container.Find("film.bin")!);
        stream.Position = content.Length - 5;
        stream.ReadByte().Should().Be(content[^5]);
        var request = store.Log.Should().ContainSingle().Which;
        var asked = int.Parse(request[(request.LastIndexOf('+') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        asked.Should().BeLessThanOrEqualTo(4096 + 16);
    }

    [Fact]
    public void Deleting_a_large_entry_is_one_new_index_and_its_objects_go_at_once()
    {
        var store = Built(("a.bin", Bytes(5000)), ("duży.bin", Bytes(150_000)), ("b.bin", Bytes(6000, 1)));
        using var container = Open(store, writable: true);
        var gone = container.Find("duży.bin")!.Extents.Select(e => PceObjectNames.Data(e.Object)).ToList();
        gone.Should().HaveCount(2);
        store.Log.Clear();
        container.Delete(["duży.bin"]).Should().Be(1);
        store.Log.Should().Equal(
            "PUTNEW " + PceObjectNames.Index(3),
            "DELETE " + gone[0],
            "DELETE " + gone[1]);
        container.Entries.Select(e => e.Path).Should().Equal("a.bin", "b.bin");
        ShouldHoldNothingExtra(store, container);
        using var reopened = Open(store);
        Read(reopened, "a.bin").Should().Equal(Bytes(5000));
        Read(reopened, "b.bin").Should().Equal(Bytes(6000, 1));
    }

    [Fact]
    public void Deleting_small_entries_frees_their_space_when_the_pack_is_rewritten()
    {
        var store = Built(("a.bin", Bytes(15_000)), ("b.bin", Bytes(15_000, 1)), ("c.bin", Bytes(15_000, 2)), ("d.bin", Bytes(4000, 3)));
        using var container = Open(store, writable: true);
        var pack = PceObjectNames.Data(container.Find("a.bin")!.Extents[0].Object);
        container.Delete(["a.bin"]).Should().Be(1);
        Data(store).Should().Contain(pack, "the pack still holds b and c");
        store.Content(pack)!.Length.Should().Be(3 * 15_064 + 4016);
        container.Clean().Should().Be(new PceCleanResult(0, 0, 0), "less than a third of the pack is dead, not more than half");

        container.Delete(["b.bin"]).Should().Be(1);
        var cleaned = container.Clean();
        cleaned.Repacked.Should().Be(1);
        Data(store).Should().NotContain(pack);
        Data(store).Sum(n => store.Content(n)!.Length).Should().Be(15_064 + 4016);
        Read(container, "c.bin").Should().Equal(Bytes(15_000, 2));
        Read(container, "d.bin").Should().Equal(Bytes(4000, 3));
        ShouldHoldNothingExtra(store, container);

        container.Delete(["c.bin", "d.bin"]).Should().Be(2);
        Data(store).Should().BeEmpty("a pack none of whose entries remain goes with the delete");
        container.Delete(["nie ma"]).Should().Be(0);
    }

    [Fact]
    public void Deleting_a_folder_takes_everything_under_it()
    {
        var store = Built(("dir/a.bin", Bytes(100)), ("dir/sub/b.bin", Bytes(40_000)), ("dirx/c.bin", Bytes(200)), ("d.bin", Bytes(300)));
        using var container = Open(store, writable: true);
        container.Delete(["DIR"]).Should().Be(2);
        container.Entries.Select(e => e.Path).Should().Equal("dirx/c.bin", "d.bin");
        Read(container, "dirx/c.bin").Should().Equal(Bytes(200));
    }

    [Fact]
    public void Renaming_is_one_new_index_and_moves_no_data()
    {
        var store = Built(("dir/a.bin", Bytes(100)), ("dir/b.bin", Bytes(40_000)), ("c.bin", Bytes(200)));
        using var container = Open(store, writable: true);
        var data = Data(store);
        store.Log.Clear();
        container.Rename("dir", "katalog/nowy");
        store.Log.Should().Equal("PUTNEW " + PceObjectNames.Index(3));
        Data(store).Should().Equal(data);
        container.Entries.Select(e => e.Path).Should().Equal("katalog/nowy/a.bin", "katalog/nowy/b.bin", "c.bin");
        Read(container, "katalog/nowy/b.bin").Should().Equal(Bytes(40_000));
        ((Action)(() => container.Rename("c.bin", "katalog/nowy/a.bin"))).Should().Throw<IOException>();
        ((Action)(() => container.Rename("nie ma", "x"))).Should().Throw<FileNotFoundException>();
        ((Action)(() => container.Rename("katalog", "katalog/nowy/w sobie"))).Should().Throw<IOException>();
    }

    [Fact]
    public void Adding_under_a_taken_path_replaces_the_entry_and_drops_its_object()
    {
        var store = Built(("a.bin", Bytes(40_000)), ("b.bin", Bytes(100)));
        using var container = Open(store, writable: true);
        var old = PceObjectNames.Data(container.Find("a.bin")!.Extents[0].Object);
        container.Add([File("A.BIN", Bytes(50_000, 9)), File("nowy/c.bin", Bytes(10, 1))]);
        container.Entries.Select(e => e.Path).Should().Equal("b.bin", "A.BIN", "nowy/c.bin");
        Read(container, "a.bin").Should().Equal(Bytes(50_000, 9));
        store.Names.Should().NotContain(old);
        ShouldHoldNothingExtra(store, container);
        ((Action)(() => container.Add([File("x", [1]), File("X", [2])]))).Should().Throw<ArgumentException>();
        ((Action)(() => container.Add([File("../x", [1])]))).Should().Throw<ArgumentException>();
        ShouldHoldNothingExtra(store, container);
    }

    [Fact]
    public void Changing_the_password_is_one_small_request()
    {
        var store = Built(("a.bin", Bytes(40_000)));
        using (var container = Open(store, writable: true))
        {
            store.Log.Clear();
            container.ChangePassword("nowe");
            store.Log.Should().Equal("PUT head");
            store.Content("head")!.Length.Should().Be(94);
            container.Add([File("b.bin", Bytes(10))]); // still works with the keys in hand
        }

        ((Action)(() => Open(store))).Should().Throw<PceWrongPasswordException>().Which.File.Should().Be("box.pce");
        using var reopened = Open(store, password: "nowe");
        Read(reopened, "a.bin").Should().Equal(Bytes(40_000));
        Read(reopened, "b.bin").Should().Equal(Bytes(10));
    }

    [Fact]
    public void A_lost_or_damaged_head_is_replaced_from_the_copy_in_the_newest_index()
    {
        var store = Built(("a.bin", Bytes(5000)));
        using (var container = Open(store, writable: true))
        {
            container.ChangePassword("nowe");
            container.Add([File("b.bin", Bytes(10))]); // the index written now carries the new header
        }

        var head = store.Content("head")!;
        foreach (var damage in new Action[] { () => store.Remove("head"), () => store.Plant("head", [1, 2, 3]), () => store.Plant("head", new byte[94]) })
        {
            damage();
            using (var reader = Open(store, password: "nowe"))
            {
                Read(reader, "a.bin").Should().Equal(Bytes(5000));
            }

            (store.Content("head") is { } left && left.AsSpan().SequenceEqual(head)).Should().BeFalse("reading does not repair");
            Open(store, writable: true, password: "nowe").Dispose();
            store.Content("head").Should().Equal(head);
        }

        // A head that is a header answers for the password alone: the copy is not a second chance for another one.
        ((Action)(() => Open(store))).Should().Throw<PceWrongPasswordException>();
    }

    [Fact]
    public void What_is_not_a_container_is_refused()
    {
        var empty = new MemoryObjectStore();
        ((Action)(() => Open(empty))).Should().Throw<PceFormatException>().WithMessage("*no index*");

        var store = Built(("a.bin", Bytes(5000)));
        ((Action)(() => Create(store))).Should().Throw<IOException>().WithMessage("*already*");

        var foreign = store.Clone();
        foreign.Plant("index/0000", [1]);
        ((Action)(() => Open(foreign))).Should().Throw<PceFormatException>().WithMessage("*not its own*");

        var cut = store.Clone();
        cut.Plant(PceObjectNames.Index(2), store.Content(PceObjectNames.Index(2))![..100]);
        ((Action)(() => Open(cut))).Should().Throw<PceFormatException>();

        // A head saved from a container file (no "split" flag) with the right password.
        var fileHead = store.Clone();
        var master = new byte[32];
        fileHead.Plant("head", PceContainerFormat.WriteHeader(PceContainerFormat.NewHeader(Fast, Password, master)));
        using var viaCopy = Open(fileHead);
        Read(viaCopy, "a.bin").Should().Equal(Bytes(5000), "a head that is not one counts as missing: the copy in the index stands in");
    }

    [Fact]
    public void Changing_any_byte_of_the_newest_index_is_detected()
    {
        var store = Built(("a.bin", Bytes(5000)), ("b.bin", Bytes(40_000)));
        var name = PceObjectNames.Index(2);
        var index = store.Content(name)!;
        for (var i = 0; i < index.Length; i += i < index.Length - 200 ? 37 : 1)
        {
            if (i >= index.Length - 48 - 94 && i < index.Length - 48)
            {
                continue; // the header copy is read only when the head is gone
            }

            var damaged = (byte[])index.Clone();
            damaged[i] ^= 0x04;
            var broken = store.Clone();
            broken.Plant(name, damaged);
            ((Action)(() => Open(broken))).Should().Throw<PceFormatException>($"byte {i} changed");
        }
    }

    [Fact]
    public void An_old_index_under_a_newer_name_is_refused_and_a_removed_newest_index_is_noticed_by_who_remembers()
    {
        var store = Built(("a.bin", Bytes(5000)));
        var old = store.Content(PceObjectNames.Index(2))!;
        ulong latest;
        using (var container = Open(store, writable: true))
        {
            container.Add([File("b.bin", Bytes(300))]);
            container.Delete(["a.bin"]);
            latest = container.Generation;
        }

        latest.Should().Be(4);
        var replayed = store.Clone();
        replayed.Plant(PceObjectNames.Index(5), old);
        ((Action)(() => Open(replayed))).Should().Throw<PceFormatException>("the footer says generation 2");

        // Whoever can delete objects can show an older state: generation 3 is still there and is a true index.
        var rolledBack = store.Clone();
        rolledBack.Remove(PceObjectNames.Index(4)).Should().BeTrue();
        using (var fooled = Open(rolledBack))
        {
            fooled.Generation.Should().Be(3);
            fooled.Entries.Select(e => e.Path).Should().Equal("a.bin", "b.bin");
        }

        var noticed = ((Action)(() => Open(rolledBack, known: latest))).Should().Throw<PceRolledBackException>().Which;
        (noticed.Known, noticed.Found).Should().Be((4UL, 3UL));
        Open(store, known: latest).Dispose();

        // An open container notices too.
        using var open = Open(store, writable: true);
        store.Remove(PceObjectNames.Index(4));
        ((Action)open.Refresh).Should().Throw<PceRolledBackException>();
    }

    [Fact]
    public void A_swapped_cut_or_missing_data_object_fails_the_read_of_its_entries_only()
    {
        var store = Built(("a.bin", Bytes(40_000)), ("b.bin", Bytes(40_000, 1)), ("c.bin", Bytes(500, 2)));
        using var container = Open(store);
        var a = PceObjectNames.Data(container.Find("a.bin")!.Extents[0].Object);
        var b = PceObjectNames.Data(container.Find("b.bin")!.Extents[0].Object);

        var swapped = store.Clone();
        swapped.Plant(a, store.Content(b)!);
        using (var reader = Open(swapped))
        {
            ((Action)(() => Read(reader, "a.bin"))).Should().Throw<PceFormatException>("b's blocks are sealed with b's key");
            Read(reader, "b.bin").Should().Equal(Bytes(40_000, 1));
            Read(reader, "c.bin").Should().Equal(Bytes(500, 2));
        }

        var cut = store.Clone();
        cut.Plant(a, store.Content(a)![..30_000]);
        using (var reader = Open(cut))
        {
            ((Action)(() => Read(reader, "a.bin"))).Should().Throw<PceFormatException>();
        }

        var flipped = store.Clone();
        var bytes = (byte[])store.Content(a)!.Clone();
        bytes[20_000] ^= 1;
        flipped.Plant(a, bytes);
        using (var reader = Open(flipped))
        {
            ((Action)(() => Read(reader, "a.bin"))).Should().Throw<PceFormatException>();
        }

        var missing = store.Clone();
        missing.Remove(a);
        using (var reader = Open(missing))
        {
            ((Action)(() => Read(reader, "a.bin"))).Should().Throw<PceObjectMissingException>();
            Read(reader, "b.bin").Should().Equal(Bytes(40_000, 1));
        }
    }

    [Fact]
    public void A_reader_sees_the_state_it_opened_until_the_bytes_it_needs_are_deleted()
    {
        var store = Built(("a.bin", Bytes(40_000)), ("b.bin", Bytes(40_000, 1)));
        using var reader = Open(store);
        using var kept = reader.OpenRead(reader.Find("b.bin")!);
        using var doomed = reader.OpenRead(reader.Find("a.bin")!);
        var start = new byte[1000];
        doomed.ReadExactly(start);
        using (var writer = Open(store, writable: true))
        {
            writer.Delete(["a.bin"]);
            writer.Add([File("c.bin", Bytes(10))]);
        }

        reader.Entries.Select(e => e.Path).Should().Equal("a.bin", "b.bin");
        var rest = new MemoryStream();
        kept.CopyTo(rest);
        rest.ToArray().Should().Equal(Bytes(40_000, 1));
        doomed.Position = 30_000;
        ((Action)(() => doomed.ReadByte())).Should().Throw<PceObjectMissingException>();
        reader.Refresh();
        reader.Entries.Select(e => e.Path).Should().Equal("b.bin", "c.bin");
    }

    [Fact]
    public void A_cancelled_or_failed_add_leaves_the_container_and_the_store_as_they_were()
    {
        var store = Built(("a.bin", Bytes(5000)));
        using var container = Open(store, writable: true);
        var names = store.Names;
        using var cancel = new CancellationTokenSource();
        var add = () => container.Add(
            [File("b.bin", Bytes(3000)), File("duży.bin", Bytes(250_000)), File("c.bin", Bytes(100))],
            PceCompression.None,
            done =>
            {
                if (done > 200_000)
                {
                    cancel.Cancel();
                }
            },
            cancel.Token);
        add.Should().Throw<OperationCanceledException>();
        store.Names.Should().Equal(names);
        store.OpenUploads.Should().Be(0);
        container.Entries.Select(e => e.Path).Should().Equal("a.bin");

        PceSource failing = new("zły.bin", false, () => new FailingStream(70_000), Stamp, FileAttributes.Normal);
        ((Action)(() => container.Add([File("b.bin", Bytes(3000)), failing]))).Should().Throw<InvalidDataException>();
        store.Names.Should().Equal(names);
        store.OpenUploads.Should().Be(0);
        container.Add([File("b.bin", Bytes(3000))]);
        Read(container, "b.bin").Should().Equal(Bytes(3000));
    }

    [Fact]
    public void A_container_open_for_reading_refuses_changes()
    {
        var store = Built(("a.bin", Bytes(5000)));
        using var container = Open(store);
        container.IsWritable.Should().BeFalse();
        ((Action)(() => container.Add([File("b", [1])]))).Should().Throw<InvalidOperationException>();
        ((Action)(() => container.Delete(["a.bin"]))).Should().Throw<InvalidOperationException>();
        ((Action)(() => container.Rename("a.bin", "b"))).Should().Throw<InvalidOperationException>();
        ((Action)(() => container.ChangePassword("x"))).Should().Throw<InvalidOperationException>();
        ((Action)(() => container.Clean())).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_commit_whose_answer_was_lost_is_recognised_as_made()
    {
        var store = Built(("a.bin", Bytes(5000)));
        using var container = Open(store, writable: true);
        store.FailAfterEffect = true;
        store.FailAt = store.Requests + 1; // the PUTNEW of the index
        container.Delete(["a.bin"]).Should().Be(1);
        container.Generation.Should().Be(3);
        store.FailAt = 0;
        using var reopened = Open(store);
        reopened.Entries.Should().BeEmpty();
        reopened.Generation.Should().Be(3);
        ShouldHoldNothingExtra(store, reopened);
    }

    [Fact]
    public void A_commit_that_never_arrived_fails_and_the_container_goes_on()
    {
        var store = Built(("a.bin", Bytes(5000)));
        using var container = Open(store, writable: true);
        store.FailAt = store.Requests + 1;
        ((Action)(() => container.Delete(["a.bin"]))).Should().Throw<IOException>().WithMessage(MemoryObjectStore.Crash);
        container.Entries.Select(e => e.Path).Should().Equal("a.bin");
        container.Rename("a.bin", "b.bin");
        container.Generation.Should().Be(3);
        using var reopened = Open(store);
        Read(reopened, "b.bin").Should().Equal(Bytes(5000));
    }

    /// <summary>The changes whose every request is made to fail, with a name for the failure message.</summary>
    public static TheoryData<string> Changes => ["add", "replace", "delete-large", "delete-small", "delete-folder", "rename", "password", "clean"];

    private static void Apply(string change, PceObjectContainer container)
    {
        switch (change)
        {
            case "add":
                container.Add([File("nowy/mały.bin", Bytes(900, 5)), File("nowy/duży.bin", Bytes(230_000, 6)), Folder("nowy/pusty"), File("nowy/średni.bin", Bytes(25_000, 7))]);
                break;
            case "replace":
                container.Add([File("dir/duży.bin", Bytes(70_000, 8)), File("mały1.bin", Bytes(50, 9))]);
                break;
            case "delete-large":
                container.Delete(["dir/duży.bin"]);
                break;
            case "delete-small":
                container.Delete(["mały1.bin", "mały2.bin"]);
                break;
            case "delete-folder":
                container.Delete(["dir"]);
                break;
            case "rename":
                container.Rename("dir", "inny");
                break;
            case "password":
                container.ChangePassword("nowe");
                break;
            default:
                container.Clean();
                break;
        }
    }

    /// <summary>A container with a pack two thirds dead, an orphan and a stale index, so every change (and Clean) has work.</summary>
    private (MemoryObjectStore Store, Dictionary<string, byte[]> Content) Seasoned()
    {
        var content = new Dictionary<string, byte[]>
        {
            ["mały1.bin"] = Bytes(4000, 1),
            ["mały2.bin"] = Bytes(4000, 2),
            ["dir/duży.bin"] = Bytes(120_000, 3),
            ["dir/sub/mały3.bin"] = Bytes(700, 4),
            ["x1.bin"] = Bytes(15_000, 5),
            ["x2.bin"] = Bytes(15_000, 6),
        };
        var store = new MemoryObjectStore();
        using (var container = Create(store))
        {
            container.Add(content.Select(p => File(p.Key, p.Value)));
            container.Delete(["x1.bin", "x2.bin"]);
        }

        content.Remove("x1.bin");
        content.Remove("x2.bin");
        store.Plant(PceObjectNames.Data(PceObjectId.New()), Bytes(3000));
        store.Now = store.Now.AddDays(2);
        return (store, content);
    }

    [Theory]
    [MemberData(nameof(Changes))]
    public void A_failure_at_any_request_of_a_change_leaves_a_container_that_opens_with_the_old_or_the_new_content(string change)
    {
        var (seasoned, before) = Seasoned();
        Dictionary<string, byte[]> after;
        int requests;
        {
            var store = seasoned.Clone();
            using var container = Open(store, writable: true);
            var start = store.Requests;
            Apply(change, container);
            requests = store.Requests - start;
            var password = change == "password" ? "nowe" : Password;
            using var result = Open(store, password: password);
            after = result.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, e => Read(result, e.Path));
        }

        requests.Should().BeGreaterThan(0);
        foreach (var answerLost in new[] { false, true })
        {
            for (var fail = 1; fail <= requests; fail++)
            {
                var because = $"{change}: request {fail} of {requests} {(answerLost ? "lost its answer" : "never arrived")}";
                var store = seasoned.Clone();
                store.FailAfterEffect = answerLost;
                using (var container = Open(store, writable: true))
                {
                    store.FailAt = store.Requests + fail;
                    try
                    {
                        Apply(change, container);
                    }
                    catch (IOException ex) when (ex.Message == MemoryObjectStore.Crash)
                    {
                    }
                    catch (AggregateException ex) when (ex.InnerExceptions.All(e => e.Message == MemoryObjectStore.Crash))
                    {
                    }
                }

                store.FailAt = 0;

                // Either password may hold after an interrupted password change, never neither.
                PceObjectContainer reopened;
                try
                {
                    reopened = Open(store, writable: true);
                }
                catch (PceWrongPasswordException) when (change == "password")
                {
                    reopened = Open(store, writable: true, password: "nowe");
                }

                using (reopened)
                {
                    var now = reopened.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, e => Read(reopened, e.Path));
                    var isOld = Same(now, before);
                    var isNew = Same(now, after);
                    (isOld || isNew).Should().BeTrue(because + ": the content is neither what it was nor what the change makes it");

                    // And the container goes on: another change, then cleaning up leaves nothing behind.
                    store.Now = store.Now.AddDays(2);
                    reopened.Add([File("po awarii.bin", Bytes(2000, 77))]);
                    reopened.Clean();
                    (reopened.Clean() with { Indexes = 0 }).Should().Be(new PceCleanResult(0, 0, 0), because + ": the first clean-up left nothing but an index its own repacking displaced");
                    ShouldHoldNothingExtra(store, reopened);
                    Indexes(store).Should().HaveCount(2, because);
                    Read(reopened, "po awarii.bin").Should().Equal(Bytes(2000, 77));
                    foreach (var (path, bytes) in now)
                    {
                        Read(reopened, path).Should().Equal(bytes, because);
                    }
                }
            }
        }
    }

    private static bool Same(Dictionary<string, byte[]> a, Dictionary<string, byte[]> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var other) && p.Value.AsSpan().SequenceEqual(other));

    [Fact]
    public void Cleaning_removes_old_orphans_and_indexes_and_leaves_young_orphans_alone()
    {
        var store = Built(("a.bin", Bytes(5000)));
        using var container = Open(store, writable: true);
        container.Rename("a.bin", "b.bin");
        container.Rename("b.bin", "a.bin");
        var old = PceObjectNames.Data(PceObjectId.New());
        store.Plant(old, Bytes(100));
        store.Plant("data/obcy plik", Bytes(100));
        container.Clean().Should().Be(new PceCleanResult(0, 0, 0), "four indexes, none an hour old");
        store.Now = store.Now.AddHours(25);
        var young = PceObjectNames.Data(PceObjectId.New());
        store.Plant(young, Bytes(100)); // another writer's upload, not yet committed
        container.Clean().Should().Be(new PceCleanResult(1, 2, 0));
        store.Names.Should().NotContain(old).And.Contain(young).And.Contain("data/obcy plik");
        Indexes(store).Should().Equal(PceObjectNames.Index(4), PceObjectNames.Index(3));

        store.Now = store.Now.AddHours(25);
        container.Add([File("b.bin", Bytes(10))]); // the store's clock is its youngest object
        container.Clean().Should().Be(new PceCleanResult(1, 1, 0));
        store.Names.Should().NotContain(young);
        Indexes(store).Should().Equal(PceObjectNames.Index(5), PceObjectNames.Index(4));
    }

    [Fact]
    public void A_second_writer_loses_the_commit_reloads_and_applies_its_change_again()
    {
        var store = Built(("a.bin", Bytes(5000)), ("b.bin", Bytes(40_000)));
        using var first = Open(store, writable: true);
        using var second = Open(store, writable: true);
        first.Add([File("od pierwszego.bin", Bytes(30_000, 1))]);
        second.Add([File("od drugiego.bin", Bytes(30_000, 2))]);
        second.Entries.Select(e => e.Path).Should().Equal("a.bin", "b.bin", "od pierwszego.bin", "od drugiego.bin");
        second.Generation.Should().Be(4);

        first.Delete(["a.bin"]).Should().Be(1);
        second.Delete(["a.bin", "b.bin"]).Should().Be(1, "a.bin was already gone when the second writer's turn came");
        first.Rename("od drugiego.bin", "c.bin");
        ((Action)(() => second.Rename("od drugiego.bin", "d.bin"))).Should().Throw<FileNotFoundException>();
        first.Refresh();
        first.Entries.Select(e => e.Path).Should().Equal("od pierwszego.bin", "c.bin");
        Read(first, "c.bin").Should().Equal(Bytes(30_000, 2));
        ShouldHoldNothingExtra(store, first);
    }

    [Fact]
    public async Task Writers_on_several_threads_lose_nothing()
    {
        var store = Built(("start.bin", Bytes(100)));
        const int writers = 6;
        const int rounds = 8;
        var token = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(
            () =>
            {
                using var container = Open(store, writable: true);
                for (var round = 0; round < rounds; round++)
                {
                    container.Add([File($"w{w}/r{round}.bin", Bytes(1000 + (w * 7000 + round * 3000) % 45_000, w * 100 + round))]);
                    if (round % 3 == 2)
                    {
                        container.Delete([$"w{w}/r{round - 1}.bin"]).Should().Be(1);
                    }
                }
            },
            token));
        await Task.WhenAll(tasks);

        using var result = Open(store, writable: true);
        var expected = Enumerable.Range(0, writers).SelectMany(w => Enumerable.Range(0, rounds).Where(r => r % 3 != 1 || r + 1 >= rounds).Select(r => (w, r))).ToList();
        result.Entries.Select(e => e.Path).Should().BeEquivalentTo(expected.Select(x => $"w{x.w}/r{x.r}.bin").Append("start.bin"));
        foreach (var (w, r) in expected)
        {
            Read(result, $"w{w}/r{r}.bin").Should().Equal(Bytes(1000 + (w * 7000 + r * 3000) % 45_000, w * 100 + r));
        }

        result.Generation.Should().Be((ulong)(2 + writers * (rounds + rounds / 3)));
        store.Now = store.Now.AddDays(2);
        result.Add([File("koniec.bin", [1])]);
        result.Clean();
        ShouldHoldNothingExtra(store, result);
        Indexes(store).Should().HaveCount(2);
    }

    /// <summary>
    /// "Create only if absent" guards a generation only while its index is there. A writer that slept through
    /// several changes and a clean-up would find its next generation's name free again — so it looks first.
    /// </summary>
    [Fact]
    public void A_writer_that_fell_far_behind_reads_the_newest_index_before_it_commits()
    {
        var store = Built(("a.bin", Bytes(5000)));
        using var sleeper = Open(store, writable: true);
        using (var busy = Open(store, writable: true))
        {
            busy.Add([File("b.bin", Bytes(100))]);
            busy.Add([File("c.bin", Bytes(100))]);
            busy.Add([File("d.bin", Bytes(100))]);
            busy.Clean().Indexes.Should().Be(0, "the sleeper may still be about to commit generation 3");
            store.Names.Should().Contain(PceObjectNames.Index(3));

            store.Now = store.Now.AddHours(2);
            _clock = _clock.AddHours(2);
            busy.Add([File("e.bin", Bytes(100))]);
            busy.Clean().Indexes.Should().Be(4);
            Indexes(store).Should().Equal(PceObjectNames.Index(6), PceObjectNames.Index(5));
        }

        sleeper.Generation.Should().Be(2);
        sleeper.Delete(["a.bin"]).Should().Be(1);
        sleeper.Generation.Should().Be(7, "it did not take generation 3, whose name was free");
        sleeper.Entries.Select(e => e.Path).Should().Equal("b.bin", "c.bin", "d.bin", "e.bin");
        using var reopened = Open(store);
        reopened.Entries.Select(e => e.Path).Should().Equal("b.bin", "c.bin", "d.bin", "e.bin");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Random_changes_match_a_model(int seed)
    {
        var random = new Random(seed);
        var store = new MemoryObjectStore();
        var model = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var container = Create(store);
        var password = Password;
        try
        {
            for (var step = 0; step < 120; step++)
            {
                var because = $"seed {seed}, step {step}";
                switch (random.Next(10))
                {
                    case 0 or 1 or 2 or 3:
                        var batch = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                        for (var i = random.Next(1, 4); i > 0; i--)
                        {
                            var size = random.Next(4) switch { 0 => random.Next(0, 300), 1 => random.Next(300, 19_000), 2 => random.Next(19_000, 70_000), _ => random.Next(70_000, 260_000) };
                            batch[$"d{random.Next(4)}/f{random.Next(12)}.bin"] = Bytes(size, step * 10 + i);
                        }

                        container.Add(batch.Select(p => File(p.Key, p.Value)), random.Next(3) == 0 ? PceCompression.Auto : PceCompression.None);
                        foreach (var (path, bytes) in batch)
                        {
                            model[path] = bytes;
                        }

                        break;
                    case 4 or 5 when model.Count > 0:
                        var victim = model.Keys.ElementAt(random.Next(model.Count));
                        container.Delete([victim]).Should().Be(1, because);
                        model.Remove(victim);
                        break;
                    case 6 when model.Count > 0:
                        var folder = model.Keys.ElementAt(random.Next(model.Count)).Split('/')[0];
                        var under = model.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                        container.Delete([folder]).Should().Be(under.Count, because);
                        under.ForEach(k => model.Remove(k));
                        break;
                    case 7 when model.Count > 0:
                        var from = model.Keys.ElementAt(random.Next(model.Count));
                        var to = $"r{random.Next(3)}/n{step}.bin";
                        container.Rename(from, to);
                        model[to] = model[from];
                        model.Remove(from);
                        break;
                    case 8:
                        store.Now = store.Now.AddHours(random.Next(1, 30));
                        container.Clean();
                        break;
                    default:
                        if (random.Next(4) == 0)
                        {
                            password += "x";
                            container.ChangePassword(password);
                        }

                        container.Dispose();
                        container = Open(store, writable: true, password: password);
                        break;
                }

                container.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).Should().BeEquivalentTo(model.Keys, because);
                if (step % 10 == 9)
                {
                    foreach (var (path, bytes) in model)
                    {
                        Read(container, path).Should().Equal(bytes, because);
                    }
                }
            }

            store.Now = store.Now.AddDays(2);
            container.Add([File("koniec.bin", [1])]);
            container.Clean();
            (container.Clean() with { Indexes = 0 }).Should().Be(new PceCleanResult(0, 0, 0));
            ShouldHoldNothingExtra(store, container);
            Indexes(store).Should().HaveCount(2);
            foreach (var (path, bytes) in model)
            {
                Read(container, path).Should().Equal(bytes);
            }

            // Nothing dead is left worth more than half of any object.
            foreach (var name in Data(store))
            {
                PceObjectNames.TryData(name, out var id).Should().BeTrue();
                var live = container.Entries.SelectMany(e => e.Extents).Where(e => e.Object == id).Sum(e => e.Length);
                (live * 2).Should().BeGreaterThanOrEqualTo(store.Content(name)!.Length);
            }
        }
        finally
        {
            container.Dispose();
        }
    }

    [Fact]
    public void The_same_container_lives_in_a_folder_on_disk()
    {
        var root = Directory.CreateTempSubdirectory("pc-pce-objects-").FullName;
        try
        {
            var store = new DirectoryPceObjectStore(Path.Combine(root, "box.pce"));
            using (var created = PceObjectContainer.Create(store, Password, Fast, Small))
            {
                created.Add([File("a.bin", Bytes(5000)), File("dir/duży.bin", Bytes(250_000, 1)), Folder("pusty")], PceCompression.Auto);
                created.Delete(["a.bin"]);
                created.Rename("dir", "katalog");
                created.ChangePassword("nowe");
            }

            using var container = PceObjectContainer.Open(new DirectoryPceObjectStore(Path.Combine(root, "box.pce")), "nowe", writable: true, split: Small);
            container.Entries.Select(e => e.Path).Should().Equal("katalog/duży.bin", "pusty");
            Read(container, "katalog/duży.bin").Should().Equal(Bytes(250_000, 1));
            container.Clean();
            Directory.EnumerateFiles(Path.Combine(root, "box.pce"), "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Path.Combine(root, "box.pce"), f).Replace('\\', '/'))
                .Should().OnlyContain(n => n == "head" || n.StartsWith("index/", StringComparison.Ordinal) || n.StartsWith("data/", StringComparison.Ordinal))
                .And.HaveCount(1 + 4 + 3, "the head, an index per change (none an hour old) and the three objects of the large file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Gives <paramref name="good"/> bytes and then fails, like a file that cannot be read to its end.</summary>
    private sealed class FailingStream(int good) : Stream
    {
        private int _left = good;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left == 0)
            {
                throw new InvalidDataException("unreadable");
            }

            var take = Math.Min(count, _left);
            _left -= take;
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
