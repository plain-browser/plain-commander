using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Crypto;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>
/// A .pce container kept as objects, seen as a server folder: what the remote operations ask of it one item at a
/// time, and how those items are gathered into few commits.
/// </summary>
public sealed class PceRemoteViewTests : IDisposable
{
    private const string Password = "tajne";
    private static readonly PceOptions Fast = new() { Kdf = PceKdf.Pbkdf2Sha512, Pbkdf2Iterations = 1000, ChunkSize = 4096 };
    private static readonly PceSplitOptions Small = new() { SmallLimit = 20_000, PackSize = 60_000, PartSize = 32_768, MaxParts = 3, Parallelism = 4, ReadAhead = 16_384 };
    private static readonly DateTimeOffset T0 = new(2024, 3, 4, 5, 6, 7, TimeSpan.Zero);
    private readonly string _path = "s3://tester@test:9000/pc-test/" + Guid.NewGuid().ToString("N")[..8] + "/box.pce";
    private readonly MemoryObjectStore _store = new();

    public PceRemoteViewTests()
    {
        PceObjectContainer.Create(_store, Password, Fast, Small).Dispose();
        ArchivePasswords.Remember(_path, Password);
    }

    public void Dispose()
    {
        ArchivePasswords.Forget(_path);
        PceKnownGenerations.Forget(_path);
    }

    private PceRemoteView View() => new(_store, _path, Fast, Small);

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private static byte[] Download(PceRemoteView view, string path, long offset = 0)
    {
        using var copy = new MemoryStream();
        view.Download(path, copy, offset, CancellationToken.None);
        return copy.ToArray();
    }

    private int Commits() => _store.Log.Count(l => l.StartsWith("PUTNEW index/", StringComparison.Ordinal));

    [Fact]
    public void Folders_are_what_the_paths_of_the_entries_imply()
    {
        using var view = View();
        view.Upload(new MemoryStream(Bytes(100)), "a.txt", CancellationToken.None);
        view.Upload(new MemoryStream(Bytes(40_000, 1)), "dir/sub/duży.bin", CancellationToken.None);
        view.Upload(new MemoryStream([]), "dir/pusty.txt", CancellationToken.None);
        view.CreateDirectory("dir/katalog");
        view.SetModified("a.txt", T0);

        view.List(string.Empty)!.Select(i => (i.Name, i.IsDirectory, i.Size)).Should().BeEquivalentTo([("a.txt", false, 100L), ("dir", true, 0L)]);
        view.List("dir")!.Select(i => (i.Name, i.IsDirectory)).Should().BeEquivalentTo([("sub", true), ("pusty.txt", false), ("katalog", true)]);
        view.List("DIR/Sub")!.Should().ContainSingle().Which.Should().Match<RemoteItem>(i => i.Name == "duży.bin" && i.Size == 40_000);
        view.List("dir/katalog")!.Should().BeEmpty();
        view.List("nie ma").Should().BeNull();
        view.List("a.txt").Should().BeNull("a file is not a folder");

        view.Stat("a.txt")!.Modified.Should().Be(T0);
        view.Stat("dir")!.IsDirectory.Should().BeTrue();
        view.Stat("dir/sub")!.Name.Should().Be("sub");
        view.Stat("dir/katalog")!.IsDirectory.Should().BeTrue();
        view.Stat("dir/su").Should().BeNull("a name that only starts like another is not it");
        view.Stat("nie ma").Should().BeNull();
    }

    [Fact]
    public void Items_sent_one_by_one_are_committed_together_and_small_ones_share_packs()
    {
        using (var view = View())
        {
            _store.Log.Clear();
            for (var i = 0; i < 40; i++)
            {
                view.Upload(new MemoryStream(Bytes(3000 + i, i)), $"dir/f{i:D2}.bin", CancellationToken.None);
                view.SetModified($"dir/f{i:D2}.bin", T0.AddMinutes(i));
                view.Stat($"dir/f{i:D2}.bin")!.Size.Should().Be(3000 + i, "what was sent is there to the one who sent it");
            }

            Commits().Should().Be(0, "nothing is committed item by item");
            view.Flush();
            Commits().Should().Be(1);
            _store.Log.Count(l => l.StartsWith("PUT data/", StringComparison.Ordinal)).Should().Be(3, "40 files of about 3 kB in packs of up to 60 kB");
        }

        using var again = View();
        var listed = again.List("dir")!;
        listed.Should().HaveCount(40);
        listed.Single(i => i.Name == "f07.bin").Modified.Should().Be(T0.AddMinutes(7));
        Download(again, "dir/f07.bin").Should().Equal(Bytes(3007, 7));
        Download(again, "dir/f39.bin").Should().Equal(Bytes(3039, 39));
    }

    [Fact]
    public void Reading_content_commits_first_and_an_offset_continues_a_broken_download()
    {
        using var view = View();
        var plain = Bytes(50_000);
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"wiersz {i} powtarzalny\n")));
        view.Upload(new MemoryStream(plain), "a.bin", CancellationToken.None);
        view.Upload(new MemoryStream(text), "tekst.txt", CancellationToken.None);
        Commits().Should().Be(1, "only the creation so far");
        Download(view, "a.bin").Should().Equal(plain);
        Commits().Should().Be(2);
        Download(view, "a.bin", 12_345).Should().Equal(plain[12_345..]);
        Download(view, "tekst.txt", 1000).Should().Equal(text[1000..], "a compressed entry is read up to the offset instead of sought");
        Download(view, "a.bin", 50_000).Should().BeEmpty();
        ((Action)(() => Download(view, "nie ma"))).Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Deleting_renaming_and_making_folders_follow_the_rules_of_a_server_folder()
    {
        using (var view = View())
        {
            view.Upload(new MemoryStream(Bytes(100)), "dir/a.txt", CancellationToken.None);
            view.Upload(new MemoryStream(Bytes(200)), "dir/sub/b.txt", CancellationToken.None);
            view.Upload(new MemoryStream(Bytes(300)), "c.txt", CancellationToken.None);

            ((Action)(() => view.DeleteDirectory("dir"))).Should().Throw<IOException>().WithMessage("*not empty*");
            ((Action)(() => view.DeleteFile("dir"))).Should().Throw<FileNotFoundException>();
            ((Action)(() => view.DeleteFile("nie ma"))).Should().Throw<FileNotFoundException>();
            ((Action)(() => view.CreateDirectory("dir"))).Should().Throw<IOException>().WithMessage("*already*");
            ((Action)(() => view.CreateDirectory("c.txt"))).Should().Throw<IOException>().WithMessage("*file*");

            view.DeleteFile("dir/sub/b.txt");
            view.Stat("dir/sub").Should().BeNull("a folder exists by what is in it, unless it was made on purpose");
            view.CreateDirectory("dir/nowy");
            view.DeleteDirectory("dir/nowy");
            view.Stat("dir/nowy").Should().BeNull();
            view.DeleteDirectory("nigdy nie było");

            view.Rename("dir", "inny");
            view.Rename("c.txt", "inny/d.txt");
            ((Action)(() => view.Rename("nie ma", "x"))).Should().Throw<FileNotFoundException>();
            view.Flush();
        }

        using var again = View();
        again.List(string.Empty)!.Select(i => i.Name).Should().Equal("inny");
        again.List("inny")!.Select(i => i.Name).Should().BeEquivalentTo("a.txt", "d.txt");
        Download(again, "inny/d.txt").Should().Equal(Bytes(300));
    }

    [Fact]
    public void Changes_not_flushed_are_dropped_with_what_was_stored_for_them()
    {
        var names = _store.Names;
        using (var view = View())
        {
            view.Upload(new MemoryStream(Bytes(250_000)), "duży.bin", CancellationToken.None);
            view.Upload(new MemoryStream(Bytes(100)), "mały.bin", CancellationToken.None);
            _store.Names.Count.Should().BeGreaterThan(names.Count, "the large file went out as it was read");
        }

        _store.Names.Should().Equal(names);
        using var again = View();
        again.List(string.Empty)!.Should().BeEmpty();
    }

    [Fact]
    public void A_file_sent_twice_in_one_batch_leaves_only_its_last_content()
    {
        using var view = View();
        view.Upload(new MemoryStream(Bytes(40_000)), "a.bin", CancellationToken.None);
        view.Upload(new MemoryStream(Bytes(45_000, 1)), "A.BIN", CancellationToken.None);
        view.Upload(new MemoryStream(Bytes(30_000, 2)), "b.bin", CancellationToken.None);
        view.DeleteFile("b.bin");
        view.Flush();
        view.List(string.Empty)!.Should().ContainSingle().Which.Size.Should().Be(45_000);
        Download(view, "a.bin").Should().Equal(Bytes(45_000, 1));
        _store.Names.Count(n => n.StartsWith("data/", StringComparison.Ordinal)).Should().Be(1, "the first content and the deleted file never reached an index and were removed");
    }

    [Fact]
    public void A_listing_shows_what_another_writer_committed()
    {
        using var view = View();
        view.List(string.Empty)!.Should().BeEmpty();
        using (var other = View())
        {
            other.Upload(new MemoryStream(Bytes(100)), "od innego.txt", CancellationToken.None);
            other.Flush();
        }

        view.List(string.Empty)!.Select(i => i.Name).Should().Equal("od innego.txt");
        view.Upload(new MemoryStream(Bytes(100, 1)), "mój.txt", CancellationToken.None);
        view.Flush();
        using var third = View();
        third.List(string.Empty)!.Select(i => i.Name).Should().BeEquivalentTo("od innego.txt", "mój.txt");
    }

    [Fact]
    public void The_password_is_the_one_remembered_for_the_path_and_changing_it_remembers_the_new_one()
    {
        using (var view = View())
        {
            view.Upload(new MemoryStream(Bytes(100)), "a.txt", CancellationToken.None);
            view.ChangePassword("nowe");
            view.List(string.Empty)!.Should().ContainSingle("the pending file was committed before the change");
        }

        ArchivePasswords.Remembered(_path).Should().Be("nowe");
        using (var again = View())
        {
            again.List(string.Empty)!.Should().ContainSingle();
        }

        // A wrong remembered password and nobody to ask (no prompt is set in tests).
        ArchivePasswords.Remember(_path, "złe");
        using var locked = View();
        var refused = ((Action)(() => locked.List(string.Empty))).Should().Throw<ArchivePasswordException>().Which;
        refused.Wrong.Should().BeTrue();
        refused.ArchiveFile.Should().Be(_path);
        ArchivePasswords.Remembered(_path).Should().BeNull("a password that did not fit is not kept");
    }

    [Fact]
    public void A_container_put_back_to_an_older_state_is_refused_until_the_user_accepts_it()
    {
        using (var view = View())
        {
            view.Upload(new MemoryStream(Bytes(100)), "a.txt", CancellationToken.None);
            view.Flush();
            view.Upload(new MemoryStream(Bytes(100)), "b.txt", CancellationToken.None);
            view.Flush();
        }

        PceKnownGenerations.Get(_path).Should().Be(3);
        _store.Remove(PceObjectNames.Index(3)).Should().BeTrue();
        using (var fooled = View())
        {
            ((Action)(() => fooled.List(string.Empty))).Should().Throw<PceRolledBackException>();
        }

        PceKnownGenerations.Forget(_path);
        using var accepted = View();
        accepted.List(string.Empty)!.Select(i => i.Name).Should().Equal("a.txt");
    }

    [Fact]
    public void Remembered_change_numbers_only_grow_and_survive_a_reload()
    {
        var key = _path + "/inny";
        var raised = 0;
        void Count() => raised++;
        PceKnownGenerations.Changed += Count;
        try
        {
            PceKnownGenerations.Saw(key, 5);
            PceKnownGenerations.Saw(key, 3);
            PceKnownGenerations.Saw(key, 5);
            PceKnownGenerations.Get(key).Should().Be(5);
            raised.Should().Be(1);
            PceKnownGenerations.Load([new(key, 2), new(key + "2", 9)]);
            PceKnownGenerations.Get(key).Should().Be(5);
            PceKnownGenerations.Get(key + "2").Should().Be(9);
            PceKnownGenerations.Snapshot().Should().Contain(new KeyValuePair<string, ulong>(key, 5));
        }
        finally
        {
            PceKnownGenerations.Changed -= Count;
            PceKnownGenerations.Forget(key);
            PceKnownGenerations.Forget(key + "2");
        }
    }
}
