using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using Xunit;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>
/// The .pce container layout: listing and reading entries, changes in place (the file shrinks by what a deleted
/// entry took), and a crash injected at every write, flush and truncation of a change.
/// </summary>
public sealed class PceContainerTests
{
    private const string Password = "tajne";

    // PBKDF2 at its floor: these tests open containers hundreds of times and the key derivation is not their subject.
    private static readonly PceOptions Fast = new() { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000, ChunkSize = 4096 };
    private static readonly int Chunk = Fast.ChunkSize;
    private static readonly DateTime Stamp = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private static PceSource File(string path, byte[] content) => new(path, false, () => new MemoryStream(content), Stamp, FileAttributes.Archive);

    private static PceSource Folder(string path) => new(path, true, null, Stamp, FileAttributes.None);

    private static byte[] Read(PceContainer container, string path)
    {
        using var stream = container.OpenRead(container.Find(path)!);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static MemoryStorage Built(params (string Path, byte[] Content)[] files)
    {
        var storage = new MemoryStorage();
        using var container = PceContainer.Create(storage.Share(), Password, Fast);
        container.Add(files.Select(f => File(f.Path, f.Content)));
        return storage;
    }

    /// <summary>Header + every entry's stored bytes + index: nothing unused anywhere.</summary>
    private static void ShouldBeDense(PceContainer container) => container.Slack.Should().Be(0);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(3 * 4096 + 7)]
    public void Round_trips_a_file_of_any_size_with_its_metadata(int size)
    {
        var content = Bytes(size);
        var storage = Built(("dane.bin", content));

        using var container = PceContainer.Open(storage.Share(), Password);
        var entry = container.Entries.Should().ContainSingle().Subject;
        entry.Path.Should().Be("dane.bin");
        entry.Size.Should().Be(size);
        entry.ModifiedUtc.Should().Be(Stamp);
        entry.Attributes.Should().Be(FileAttributes.Archive);
        Read(container, "dane.bin").Should().Equal(content);
        ShouldBeDense(container);
    }

    [Fact]
    public void A_new_container_lays_its_data_out_in_one_piece()
    {
        var storage = Built(("a", Bytes(10_000)), ("b", Bytes(5)), ("c", Bytes(20_000)));

        using var container = PceContainer.Open(storage.Share(), Password);
        container.Entries.Should().OnlyContain(e => e.Extents.Count == 1);
        ShouldBeDense(container);
    }

    [Fact]
    public void Lists_folders_and_nested_paths_and_reads_one_entry_by_seeking()
    {
        var big = Bytes(5 * Chunk + 123);
        var storage = new MemoryStorage();
        using (var created = PceContainer.Create(storage.Share(), Password, Fast))
        {
            created.Add([File("docs\\umowa.pdf", big), Folder("docs/puste/"), File("zażółć gęślą.txt", Bytes(3))]);
        }

        using var container = PceContainer.Open(storage.Share(), Password);
        container.Entries.Select(e => e.Path).Should().Equal("docs/umowa.pdf", "docs/puste", "zażółć gęślą.txt");
        container.Find("DOCS/PUSTE")!.IsDirectory.Should().BeTrue();

        storage.Reads.Clear();
        using var stream = container.OpenRead(container.Find("docs/umowa.pdf")!);
        stream.CanSeek.Should().BeTrue();
        stream.Length.Should().Be(big.Length);
        stream.Position = 3 * Chunk + 100;
        var part = new byte[50];
        stream.ReadExactly(part);
        part.Should().Equal(big.AsSpan(3 * Chunk + 100, 50).ToArray());
        storage.Reads.Sum(r => r.Count).Should().Be(Chunk + 16, "only the one block is read");
    }

    [Fact]
    public void Compresses_when_asked_and_in_auto_mode_only_when_it_pays()
    {
        var text = new byte[40_000];
        Array.Fill(text, (byte)'a');
        var noise = Bytes(40_000);
        var storage = new MemoryStorage();
        using (var created = PceContainer.Create(storage.Share(), Password, Fast))
        {
            created.Add([File("tekst", text), File("szum", noise)], PceCompression.Auto);
            created.Add([File("zawsze", noise)], PceCompression.Always);
        }

        using var container = PceContainer.Open(storage.Share(), Password);
        container.Find("tekst")!.Compressed.Should().BeTrue();
        container.Find("tekst")!.StoredSize.Should().BeLessThan(2000);
        container.Find("szum")!.Compressed.Should().BeFalse();
        container.Find("zawsze")!.Compressed.Should().BeTrue();
        Read(container, "tekst").Should().Equal(text);
        Read(container, "szum").Should().Equal(noise);
        Read(container, "zawsze").Should().Equal(noise);
    }

    [Fact]
    public void A_wrong_password_is_known_from_the_header_alone()
    {
        var storage = Built(("a", Bytes(100_000)));
        storage.Reads.Clear();

        var open = () => PceContainer.Open(storage.Share(), "inne", fileName: "x.pce");

        open.Should().Throw<PceWrongPasswordException>().Which.File.Should().Be("x.pce");
        storage.Reads.Should().Equal([(0L, 94), (storage.Length - 142, 142)], "the header, then its copy, which is the same bytes and is not tried");
    }

    [Fact]
    public void Any_changed_byte_of_the_index_or_the_footer_is_refused()
    {
        var storage = Built(("a", Bytes(100)), ("b", Bytes(200)));
        var length = (int)storage.Length;
        var dataEnd = 94 + 100 + 16 + 200 + 16;
        var indexEnd = length - 48 - 94;
        foreach (var at in new[] { dataEnd, dataEnd + 1000, indexEnd - 17, indexEnd - 1, length - 48, length - 40, length - 32, length - 24, length - 9, length - 8, length - 1 })
        {
            var damaged = storage.Clone();
            damaged.Data[at] ^= 0x01;

            var open = () => PceContainer.Open(damaged, Password).Dispose();

            open.Should().Throw<PceFormatException>($"byte {at} was changed");
        }
    }

    [Fact]
    public void A_damaged_header_is_replaced_from_its_copy_and_both_damaged_are_refused()
    {
        var storage = Built(("a", Bytes(100)));
        var copyAt = (int)storage.Length - 48 - 94;
        for (var at = 0; at < 94; at++)
        {
            var damaged = storage.Clone();
            damaged.Data[at] ^= 0x01;
            using (var readOnly = PceContainer.Open(damaged.Share(), Password))
            {
                Read(readOnly, "a").Should().Equal(Bytes(100), $"byte {at} of the header was changed");
            }

            damaged.Data.Should().NotEqual(storage.Data, "reading repairs nothing");
            PceContainer.Open(damaged.Share(), Password, writable: true).Dispose();
            damaged.Data.Should().Equal(storage.Data, "opening for changes writes the header back");

            damaged.Data[copyAt + at] ^= 0x01;
            using (var copyDamaged = PceContainer.Open(damaged.Share(), Password))
            {
                Read(copyDamaged, "a").Should().Equal(Bytes(100), "a damaged copy does not matter while the header is whole");
            }

            damaged.Data[at] ^= 0x01;
            damaged.Data[copyAt + at] ^= 0x02;
            var open = () => PceContainer.Open(damaged.Share(), Password).Dispose();
            open.Should().Throw<IOException>($"byte {at} differs in both").Which.Should().Match(e => e is PceWrongPasswordException || e is PceFormatException);
        }
    }

    [Fact]
    public void A_changed_or_swapped_block_of_an_entry_fails_when_that_entry_is_read()
    {
        var a = Bytes(3 * Chunk);
        var storage = Built(("a", a), ("b", Bytes(3 * Chunk, seed: 1)));
        var block = Chunk + 16;

        var flipped = storage.Clone();
        flipped.Data[94 + block + 5] ^= 0x80;
        using (var container = PceContainer.Open(flipped, Password))
        {
            var read = () => Read(container, "a");
            read.Should().Throw<PceFormatException>();
            Read(container, "b").Length.Should().Be(3 * Chunk, "the other entry is untouched");
        }

        // Blocks 0 and 1 of "a" exchanged, then block 0 of "a" replaced by block 0 of "b".
        var swapped = storage.Clone();
        var first = swapped.Data.GetRange(94, block);
        swapped.Data.RemoveRange(94, block);
        swapped.Data.InsertRange(94 + block, first);
        using (var container = PceContainer.Open(swapped, Password))
        {
            var read = () => Read(container, "a");
            read.Should().Throw<PceFormatException>();
        }

        var foreign = storage.Clone();
        for (var i = 0; i < block; i++)
        {
            foreign.Data[94 + i] = storage.Data[94 + 3 * block + i];
        }

        using (var container = PceContainer.Open(foreign, Password))
        {
            var read = () => Read(container, "a");
            read.Should().Throw<PceFormatException>();
        }
    }

    [Fact]
    public void A_file_cut_short_or_without_its_footer_is_refused()
    {
        var storage = Built(("a", Bytes(1000)));
        foreach (var cut in new[] { 1, 8, 48, 49, 2000, (int)storage.Length - 94, (int)storage.Length - 50 })
        {
            var damaged = storage.Clone();
            damaged.Data.RemoveRange(damaged.Data.Count - cut, cut);

            var open = () => PceContainer.Open(damaged, Password).Dispose();

            open.Should().Throw<PceFormatException>($"{cut} bytes were cut");
        }
    }

    [Fact]
    public void A_flag_from_a_newer_version_is_reported_as_such()
    {
        var storage = Built(("a", Bytes(10)));
        storage.Data[4] |= 0x40;
        storage.Data[(int)storage.Length - 48 - 94 + 4] |= 0x40;

        var open = () => PceContainer.Open(storage, Password).Dispose();

        open.Should().Throw<PceFormatException>().WithMessage("*newer version*");
    }

    [Theory]
    [InlineData("a")] // first
    [InlineData("b")] // middle, larger than what follows
    [InlineData("c")] // middle, smaller than what follows
    [InlineData("d")] // last
    public void Deleting_an_entry_shrinks_the_file_by_what_it_took(string victim)
    {
        var files = new (string Path, byte[] Content)[]
        {
            ("a", Bytes(7000)), ("b", Bytes(50_000)), ("c", Bytes(300)), ("d", Bytes(9000)),
        };
        var storage = Built(files);
        long before = storage.Length;
        long took;
        using (var container = PceContainer.Open(storage.Share(), Password, writable: true))
        {
            took = container.Find(victim)!.StoredSize;

            container.Delete([victim]).Should().Be(1);

            ShouldBeDense(container);
        }

        storage.Length.Should().Be(before - took);
        using var reopened = PceContainer.Open(storage.Share(), Password);
        reopened.Find(victim).Should().BeNull();
        foreach (var (path, content) in files.Where(f => f.Path != victim))
        {
            Read(reopened, path).Should().Equal(content);
        }
    }

    [Fact]
    public void Deleting_moves_no_more_bytes_than_the_deleted_entry_took()
    {
        var storage = Built(("a", Bytes(3000)), ("b", Bytes(500_000)), ("c", Bytes(400_000)));
        using var container = PceContainer.Open(storage.Share(), Password, writable: true);
        var took = container.Find("a")!.StoredSize;
        var index = storage.Length - 94 - container.Entries.Sum(e => e.StoredSize);
        storage.Writes.Clear();

        container.Delete(["a"]);

        storage.Writes.Sum(w => (long)w.Count).Should().BeLessThanOrEqualTo(took + 3 * index);
        Read(container, "c").Should().Equal(Bytes(400_000));
    }

    [Fact]
    public void Deleting_a_folder_takes_everything_under_it_and_deleting_everything_leaves_an_empty_container()
    {
        var storage = Built(("docs/a", Bytes(5000)), ("docs/sub/b", Bytes(6000)), ("docsy", Bytes(100)), ("x", Bytes(7000)));
        using var container = PceContainer.Open(storage.Share(), Password, writable: true);

        container.Delete(["DOCS"]).Should().Be(2);

        container.Entries.Select(e => e.Path).Should().Equal("docsy", "x");
        ShouldBeDense(container);

        container.Delete(["docsy", "x", "nie ma"]).Should().Be(2);

        container.Entries.Should().BeEmpty();
        storage.Length.Should().Be(94 + 4096 + 16 + 94 + 48);
        container.Add([File("nowy", Bytes(10))]);
        Read(container, "nowy").Should().Equal(Bytes(10));
        ShouldBeDense(container);
    }

    [Fact]
    public void Adding_to_a_container_keeps_it_dense_and_replaces_an_entry_of_the_same_path()
    {
        var storage = Built(("a", Bytes(7000)), ("b", Bytes(9000)));
        using (var container = PceContainer.Open(storage.Share(), Password, writable: true))
        {
            container.Add([File("c", Bytes(20_000)), File("A", Bytes(11, seed: 5))]);

            container.Entries.Select(e => e.Path).Should().BeEquivalentTo("A", "b", "c");
            ShouldBeDense(container);
        }

        using var reopened = PceContainer.Open(storage.Share(), Password);
        Read(reopened, "a").Should().Equal(Bytes(11, seed: 5));
        Read(reopened, "b").Should().Equal(Bytes(9000));
        Read(reopened, "c").Should().Equal(Bytes(20_000));
    }

    [Fact]
    public void A_failed_or_cancelled_add_leaves_the_container_as_it_was()
    {
        var storage = Built(("a", Bytes(7000)));
        var before = storage.Data.ToArray();
        using var cancel = new CancellationTokenSource();
        using (var container = PceContainer.Open(storage.Share(), Password, writable: true))
        {
            var failing = new PceSource("zly", false, () => throw new IOException("nie da się"), Stamp, FileAttributes.None);
            var add = () => container.Add([File("b", Bytes(30_000)), failing]);
            add.Should().Throw<IOException>().WithMessage("nie da się");

            var cancelled = () => container.Add([File("c", Bytes(300_000))], PceCompression.None, _ => cancel.Cancel(), cancel.Token);
            cancelled.Should().Throw<OperationCanceledException>();

            container.Entries.Select(e => e.Path).Should().Equal("a");
        }

        storage.Data.Should().Equal(before);
    }

    [Fact]
    public void Renaming_moves_an_entry_or_a_folder_without_touching_data()
    {
        var storage = Built(("docs/a", Bytes(5000)), ("docs/sub/b", Bytes(6000)), ("x", Bytes(100)));
        using var container = PceContainer.Open(storage.Share(), Password, writable: true);

        container.Rename("docs", "archiwum/2024");
        container.Rename("x", "X");

        container.Entries.Select(e => e.Path).Should().Equal("archiwum/2024/a", "archiwum/2024/sub/b", "X");
        Read(container, "archiwum/2024/sub/b").Should().Equal(Bytes(6000));
        ShouldBeDense(container);
        var taken = () => container.Rename("X", "archiwum/2024/a");
        taken.Should().Throw<IOException>();
        var intoItself = () => container.Rename("archiwum", "archiwum/2024/inne");
        intoItself.Should().Throw<IOException>();
    }

    [Fact]
    public void A_rename_that_makes_the_index_grow_past_its_padding_still_ends_dense()
    {
        var storage = Built(Enumerable.Range(0, 40).Select(i => ($"f{i}", Bytes(10, seed: i))).ToArray());
        using var container = PceContainer.Open(storage.Share(), Password, writable: true);
        var before = storage.Length;

        container.Rename("f0", new string('n', 5000));

        storage.Length.Should().Be(before + 4096);
        ShouldBeDense(container);
        Read(container, "f39").Should().Equal(Bytes(10, seed: 39));
    }

    [Fact]
    public void Changing_the_password_leaves_the_data_alone_and_the_size_as_it_was()
    {
        var storage = Built(("a", Bytes(7000)));
        var before = storage.Length;
        var dataEnd = 94 + 7000 + 2 * 16;
        using (var container = PceContainer.Open(storage.Share(), Password, writable: true))
        {
            storage.Writes.Clear();

            container.ChangePassword("nowe hasło");

            storage.Writes.Where(w => w.Offset < dataEnd).Should().Equal([(0L, 94)]);
            storage.Writes[^1].Should().Be((0L, 94), "the header at the start goes last, when its copy is on disk");
            Read(container, "a").Should().Equal(Bytes(7000));
        }

        storage.Length.Should().Be(before);
        var old = () => PceContainer.Open(storage.Share(), Password).Dispose();
        old.Should().Throw<PceWrongPasswordException>();
        using var reopened = PceContainer.Open(storage.Share(), "nowe hasło");
        Read(reopened, "a").Should().Equal(Bytes(7000));
    }

    [Fact]
    public void A_crash_at_any_point_of_a_password_change_leaves_a_file_one_of_the_passwords_opens()
    {
        var clean = Built(("a", Bytes(7000)), ("b", Bytes(300)));
        var finished = false;
        for (var failAt = 1; !finished; failAt++)
        {
            failAt.Should().BeLessThan(50);
            var storage = clean.Clone();
            using (var container = PceContainer.Open(storage.Share(), Password, writable: true))
            {
                storage.FailAt = storage.Calls + failAt;
                try
                {
                    container.ChangePassword("nowe");
                    finished = true;
                }
                catch (IOException ex) when (ex.Message == MemoryStorage.Crash)
                {
                }
            }

            storage.FailAt = -1;
            foreach (var state in new[] { storage.Clone(), storage.AsOfLastFlush() })
            {
                var opened = 0;
                foreach (var password in new[] { Password, "nowe" })
                {
                    try
                    {
                        using var survivor = PceContainer.Open(state.Clone(), password);
                        Read(survivor, "a").Should().Equal(Bytes(7000));
                        Read(survivor, "b").Should().Equal(Bytes(300));
                        opened++;
                    }
                    catch (PceWrongPasswordException)
                    {
                    }
                }

                opened.Should().BeGreaterThan(0, $"a password must open the file after a crash at call {failAt}");
                if (finished)
                {
                    var old = () => PceContainer.Open(state.Clone(), Password).Dispose();
                    old.Should().Throw<PceWrongPasswordException>("a finished change leaves no way in for the old password");
                }
            }
        }

        // Opening with the new password after such a crash finishes the change: the old one stops working.
        var torn = clean.Clone();
        using (var container = PceContainer.Open(torn.Share(), Password, writable: true))
        {
            container.ChangePassword("nowe");
        }

        for (var i = 0; i < 94; i++)
        {
            torn.Data[i] = clean.Data[i]; // the old header came back, as if its overwrite never landed
        }

        PceContainer.Open(torn.Clone(), Password).Dispose();
        PceContainer.Open(torn.Share(), "nowe", writable: true).Dispose();
        var stale = () => PceContainer.Open(torn.Share(), Password).Dispose();
        stale.Should().Throw<PceWrongPasswordException>();
    }

    [Fact]
    public void A_stream_opened_before_a_change_refuses_to_read_moved_bytes()
    {
        var storage = Built(("a", Bytes(7000)), ("b", Bytes(3 * Chunk)));
        using var container = PceContainer.Open(storage.Share(), Password, writable: true);
        using var stream = container.OpenRead(container.Find("b")!);
        stream.ReadByte();

        container.Delete(["a"]);

        stream.Position = 2 * Chunk;
        var read = () => stream.ReadByte();
        read.Should().Throw<IOException>().WithMessage("*changed*");
    }

    [Fact]
    public void Entry_paths_that_are_not_plain_relative_paths_are_refused()
    {
        using var container = PceContainer.Create(new MemoryStorage(), Password, Fast);
        foreach (var path in new[] { "../a", "a/../b", "/a", "a//b", ".", "a/./b", "a\0b" })
        {
            var add = () => container.Add([File(path, Bytes(1))]);
            add.Should().Throw<ArgumentException>($"\"{path}\" is not a plain path");
        }

        container.Entries.Should().BeEmpty();
    }

    [Fact]
    public void A_read_only_container_refuses_changes()
    {
        var storage = Built(("a", Bytes(10)));
        using var container = PceContainer.Open(storage.Share(), Password);

        var delete = () => container.Delete(["a"]);

        delete.Should().Throw<InvalidOperationException>();
    }

    public static TheoryData<string> Changes => ["delete-first", "delete-middle", "delete-many", "add", "rename", "delete-all"];

    /// <summary>
    /// The change is run again and again, each time failing one storage call later, until it runs through. After
    /// every failure the file is checked twice — as it is (every write up to the failure landed, the failing one
    /// by half) and as of the last flush (nothing after it landed): it must open, and every entry the change did
    /// not delete must read back whole. Then a repair must leave it dense.
    /// </summary>
    [Theory]
    [MemberData(nameof(Changes))]
    public void A_crash_at_any_point_of_a_change_leaves_a_container_that_opens_and_loses_nothing_else(string change)
    {
        var original = new Dictionary<string, byte[]>
        {
            ["a"] = Bytes(9000), ["dir/b"] = Bytes(30_000), ["dir/c"] = Bytes(100), ["d"] = Bytes(14_000), ["e"] = Bytes(5000),
        };
        var clean = Built(original.Select(p => (p.Key, p.Value)).ToArray());
        var added = Bytes(12_000, seed: 9);
        string[] deleted = change switch
        {
            "delete-first" => ["a"],
            "delete-middle" => ["dir/b"],
            "delete-many" => ["a", "dir/c", "d"],
            "delete-all" => [.. original.Keys],
            _ => [],
        };

        var finished = false;
        for (var failAt = 1; !finished; failAt++)
        {
            failAt.Should().BeLessThan(200, "the change must run through eventually");
            var storage = clean.Clone();
            using (var container = PceContainer.Open(storage.Share(), Password, writable: true))
            {
                storage.FailAt = storage.Calls + failAt;
                try
                {
                    switch (change)
                    {
                        case "add":
                            container.Add([File("nowy", added)]);
                            break;
                        case "rename":
                            container.Rename("dir", new string('r', 5000));
                            break;
                        default:
                            container.Delete(deleted);
                            break;
                    }

                    finished = true;
                }
                catch (IOException ex) when (ex.Message == MemoryStorage.Crash)
                {
                }
            }

            storage.FailAt = -1;
            foreach (var state in new[] { storage.Clone(), storage.AsOfLastFlush() })
            {
                using (var survivor = PceContainer.Open(state.Share(), Password))
                {
                    foreach (var (path, content) in original)
                    {
                        var name = change == "rename" && survivor.Find(path) is null ? path.Replace("dir", new string('r', 5000)) : path;
                        if (survivor.Find(name) is null)
                        {
                            deleted.Should().Contain(path, $"only a deleted entry may be missing (call {failAt})");
                            continue;
                        }

                        Read(survivor, name).Should().Equal(content, $"\"{path}\" must be whole after a crash at call {failAt}");
                    }

                    if (survivor.Find("nowy") is not null)
                    {
                        Read(survivor, "nowy").Should().Equal(added);
                    }
                }

                using var repaired = PceContainer.Open(state.Share(), Password, writable: true);
                repaired.Repair();
                ShouldBeDense(repaired);
                foreach (var entry in repaired.Entries.Where(e => !e.IsDirectory))
                {
                    Read(repaired, entry.Path).Length.Should().Be((int)entry.Size);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Random_changes_match_a_model_and_the_file_stays_dense(int seed)
    {
        var random = new Random(seed);
        var model = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var storage = new MemoryStorage();
        PceContainer.Create(storage.Share(), Password, Fast).Dispose();
        for (var step = 0; step < 60; step++)
        {
            using (var container = PceContainer.Open(storage.Share(), Password, writable: true))
            {
                var action = random.Next(10);
                if (action < 5 || model.Count == 0)
                {
                    var batch = Enumerable.Range(0, random.Next(1, 4))
                        .Select(_ => $"d{random.Next(3)}/f{random.Next(12)}")
                        .Distinct()
                        .Select(p => (Path: p, Content: Bytes(random.Next(4) == 0 ? 0 : random.Next(1, 30_000), seed: step)))
                        .ToList();
                    container.Add(batch.Select(b => File(b.Path, b.Content)), (PceCompression)random.Next(3));
                    foreach (var (path, content) in batch)
                    {
                        model[path] = content;
                    }
                }
                else if (action < 8)
                {
                    var victims = model.Keys.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToList();
                    container.Delete(victims).Should().Be(victims.Count);
                    victims.ForEach(v => model.Remove(v));
                }
                else if (action == 8)
                {
                    var folder = $"d{random.Next(3)}";
                    container.Delete([folder]);
                    foreach (var key in model.Keys.Where(k => k.StartsWith(folder + "/", StringComparison.Ordinal)).ToList())
                    {
                        model.Remove(key);
                    }
                }
                else
                {
                    var from = model.Keys.OrderBy(_ => random.Next()).First();
                    var to = $"renamed/{step}";
                    container.Rename(from, to);
                    model[to] = model[from];
                    model.Remove(from);
                }

                ShouldBeDense(container);
            }

            using var check = PceContainer.Open(storage.Share(), Password);
            check.Entries.Select(e => e.Path).Should().BeEquivalentTo(model.Keys, $"step {step}");
            foreach (var (path, content) in model)
            {
                Read(check, path).Should().Equal(content, $"\"{path}\" at step {step}");
            }
        }
    }

    [Fact]
    public void Works_on_a_real_file_and_tells_the_two_layouts_apart()
    {
        using var sandbox = new Operations.Sandbox();
        var source = sandbox.P("src");
        Directory.CreateDirectory(Path.Combine(source, "pusty"));
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        System.IO.File.WriteAllBytes(Path.Combine(source, "a.bin"), Bytes(50_000));
        System.IO.File.WriteAllBytes(Path.Combine(source, "sub", "b.txt"), Bytes(700));
        var file = sandbox.P("x.pce");
        using (var created = PceContainer.Create(file, Password, Fast))
        {
            created.Add(PceSource.FromPath(source));
        }

        PceContainer.LayoutVersion(file).Should().Be(2);
        var before = new FileInfo(file).Length;
        using (var container = PceContainer.Open(file, Password, writable: true))
        {
            container.Entries.Select(e => e.Path).Should().BeEquivalentTo("src/a.bin", "src/sub/b.txt", "src/pusty");
            container.Find("src/pusty")!.IsDirectory.Should().BeTrue();
            container.Delete(["src/a.bin"]);
        }

        new FileInfo(file).Length.Should().Be(before - 50_000 - 13 * 16);
        using var reopened = PceContainer.Open(file, Password);
        Read(reopened, "src/sub/b.txt").Should().Equal(Bytes(700));

        var old = sandbox.P("stary.pce");
        System.IO.File.WriteAllBytes(sandbox.P("stary"), Bytes(10));
        PceFile.EncryptLegacy(sandbox.P("stary"), old, Password, PceOptions.Fast, PceCompression.None, null, TestContext.Current.CancellationToken);
        PceContainer.LayoutVersion(old).Should().Be(1);
        PceContainer.LayoutVersion(sandbox.P("stary")).Should().Be(0);
    }

    /// <summary>
    /// Storage in memory that records its calls and can fail at a chosen one: a failing write lands by half, and
    /// <see cref="AsOfLastFlush"/> gives the bytes as a disk that lost everything after the last flush would have them.
    /// </summary>
    private sealed class MemoryStorage : IPceStorage
    {
        public const string Crash = "injected crash";
        private byte[] _flushed = [];

        public List<byte> Data { get; private init; } = [];

        public List<(long Offset, int Count)> Reads { get; } = [];

        public List<(long Offset, int Count)> Writes { get; } = [];

        public int Calls { get; private set; }

        public int FailAt { get; set; } = -1;

        public long Length => Data.Count;

        /// <summary>The same bytes behind a handle whose Dispose leaves them alone, as closing a file does.</summary>
        public IPceStorage Share() => new Shared(this);

        public MemoryStorage Clone() => new() { Data = [.. Data], _flushed = [.. Data] };

        public MemoryStorage AsOfLastFlush() => new() { Data = [.. _flushed], _flushed = [.. _flushed] };

        public int Read(Span<byte> buffer, long offset)
        {
            var count = (int)Math.Max(0, Math.Min(buffer.Length, Data.Count - offset));
            Reads.Add((offset, count));
            for (var i = 0; i < count; i++)
            {
                buffer[i] = Data[(int)offset + i];
            }

            return count;
        }

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            var crash = ++Calls == FailAt;
            var count = crash ? buffer.Length / 2 : buffer.Length;
            Writes.Add((offset, buffer.Length));
            while (Data.Count < offset + count)
            {
                Data.Add(0);
            }

            for (var i = 0; i < count; i++)
            {
                Data[(int)offset + i] = buffer[i];
            }

            if (crash)
            {
                throw new IOException(Crash);
            }
        }

        public void Flush()
        {
            if (++Calls == FailAt)
            {
                throw new IOException(Crash);
            }

            _flushed = [.. Data];
        }

        public void SetLength(long length)
        {
            if (++Calls == FailAt)
            {
                throw new IOException(Crash);
            }

            if (length < Data.Count)
            {
                Data.RemoveRange((int)length, Data.Count - (int)length);
            }
            else
            {
                Data.AddRange(new byte[length - Data.Count]);
            }
        }

        public void Dispose()
        {
        }

        private sealed class Shared(MemoryStorage inner) : IPceStorage
        {
            public long Length => inner.Length;

            public int Read(Span<byte> buffer, long offset) => inner.Read(buffer, offset);

            public void Write(ReadOnlySpan<byte> buffer, long offset) => inner.Write(buffer, offset);

            public void Flush() => inner.Flush();

            public void SetLength(long length) => inner.SetLength(length);

            public void Dispose()
            {
            }
        }
    }
}
