using AwesomeAssertions;
using PlainCommander.Core.Crypto;
using Xunit;

namespace PlainCommander.Core.Tests.Volumes;

/// <summary>
/// The .pce container changed in place on file systems other than the system drive's NTFS, and on a volume with no
/// room left. Runs with the same environment as <see cref="VolumeMatrixTests"/> (PC_TEST_VOLUMES, PC_TEST_FULL_VOLUME);
/// a network share can be one of the roots.
/// </summary>
public sealed class PceContainerVolumeTests : IDisposable
{
    private const string Password = "tajne";
    private static readonly PceOptions Fast = PceOptions.Fast;
    private static readonly DateTime Stamp = new(2025, 3, 7, 10, 11, 12, DateTimeKind.Utc);
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (var dir in _cleanup)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private static PceSource Entry(string path, byte[] content) => new(path, false, () => new MemoryStream(content), Stamp, FileAttributes.Archive);

    private static byte[] Read(PceContainer container, string path)
    {
        using var stream = container.OpenRead(container.Find(path)!);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private string Work(string volume)
    {
        Assert.SkipWhen(volume.Length == 0, "PC_TEST_VOLUMES not set");
        Assert.SkipUnless(Directory.Exists(volume), volume + " is not mounted");
        var dir = Path.Combine(volume, "pc-pce-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _cleanup.Add(dir);
        return dir;
    }

    [Theory]
    [MemberData(nameof(VolumeMatrixTests.Volumes), MemberType = typeof(VolumeMatrixTests))]
    public void A_container_is_created_changed_in_place_and_reopened(string volume)
    {
        var file = Path.Combine(Work(volume), "box.pce");
        var content = new Dictionary<string, byte[]>
        {
            ["zażółć gęślą jaźń.txt"] = Bytes(70_000),
            ["日本語/emoji 😀.bin"] = Bytes(1_500_000),
            ["dir/" + new string('d', 180) + ".txt"] = Bytes(300),
            ["dir/sub/last.bin"] = Bytes(900_000),
        };
        using (var created = PceContainer.Create(file, Password, Fast))
        {
            created.Add(content.Select(p => Entry(p.Key, p.Value)));
        }

        var before = new FileInfo(file).Length;
        long took;
        using (var container = PceContainer.Open(file, Password, writable: true))
        {
            took = container.Find("日本語/emoji 😀.bin")!.StoredSize;
            container.Delete(["日本語"]).Should().Be(1);
            container.Slack.Should().Be(0);
        }

        new FileInfo(file).Length.Should().Be(before - took, $"the file shrinks by what the entry took on {volume}");
        using (var container = PceContainer.Open(file, Password, writable: true))
        {
            container.Add([Entry("nowy.bin", Bytes(200_000, seed: 3))]);
            container.Rename("dir", "katalog");
            container.ChangePassword("nowe");
            container.Slack.Should().Be(0);
        }

        using var reopened = PceContainer.Open(file, "nowe");
        reopened.Entries.Select(e => e.Path).Should().BeEquivalentTo(
            "zażółć gęślą jaźń.txt", "katalog/" + new string('d', 180) + ".txt", "katalog/sub/last.bin", "nowy.bin");
        Read(reopened, "zażółć gęślą jaźń.txt").Should().Equal(content["zażółć gęślą jaźń.txt"]);
        Read(reopened, "katalog/sub/last.bin").Should().Equal(content["dir/sub/last.bin"]);
        Read(reopened, "nowy.bin").Should().Equal(Bytes(200_000, seed: 3));
    }

    [Fact]
    public void On_a_full_volume_adding_fails_and_leaves_the_container_as_it_was_and_deleting_needs_room_for_one_index()
    {
        var volume = Environment.GetEnvironmentVariable("PC_TEST_FULL_VOLUME") ?? string.Empty;
        Assert.SkipWhen(volume.Length == 0, "PC_TEST_FULL_VOLUME not set");
        var work = Work(volume);
        var file = Path.Combine(work, "box.pce");
        var a = Bytes(2_000_000);
        var b = Bytes(3_000_000, seed: 1);
        using (var created = PceContainer.Create(file, Password, Fast))
        {
            created.Add([Entry("a.bin", a), Entry("b.bin", b)]);
        }

        var before = File.ReadAllBytes(file);

        // More than the volume holds: the add fails part-way and the file is cut back to what it was.
        using (var container = PceContainer.Open(file, Password, writable: true))
        {
            var add = () => container.Add([Entry("huge.bin", new byte[60_000_000])]);
            add.Should().Throw<IOException>();
        }

        File.ReadAllBytes(file).Should().Equal(before);

        // No free cluster at all: a delete has to append one index before it can give space back, so it fails,
        // and the container still opens with everything in it.
        var filler = Path.Combine(work, "filler.bin");
        Fill(filler, volume);
        using (var container = PceContainer.Open(file, Password, writable: true))
        {
            var delete = () => container.Delete(["a.bin"]);
            delete.Should().Throw<IOException>();
        }

        using (var container = PceContainer.Open(file, Password))
        {
            Read(container, "a.bin").Should().Equal(a);
            Read(container, "b.bin").Should().Equal(b);
        }

        // With a little room (64 KiB) the same delete goes through and frees what the entry took.
        using (var stream = new FileStream(filler, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(stream.Length - 65_536);
        }

        using (var container = PceContainer.Open(file, Password, writable: true))
        {
            container.Delete(["a.bin"]).Should().Be(1);
            container.Slack.Should().Be(0);
            Read(container, "b.bin").Should().Equal(b);
        }

        new FileInfo(file).Length.Should().Be(before.Length - 2_000_000 - 31 * 16);
    }

    /// <summary>Takes every free byte of the volume: one file grown in steps that halve when a step no longer fits.</summary>
    private static void Fill(string filler, string volume)
    {
        using var stream = new FileStream(filler, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        var block = new byte[1 << 20];
        for (var step = block.Length; step >= 512; step /= 2)
        {
            while (true)
            {
                try
                {
                    stream.Write(block, 0, step);
                    stream.Flush(flushToDisk: true);
                }
                catch (IOException)
                {
                    break;
                }
            }
        }

        new DriveInfo(volume).AvailableFreeSpace.Should().BeLessThan(4096);
    }
}
