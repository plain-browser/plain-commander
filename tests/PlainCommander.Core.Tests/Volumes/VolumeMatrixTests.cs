using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Weird;
using Xunit;

namespace PlainCommander.Core.Tests.Volumes;

/// <summary>
/// The app's operations on file systems other than the NTFS system drive: FAT16, FAT32, exFAT, ReFS and an almost
/// full NTFS volume. They need real volumes, so they run only when the environment names them (the dev VM mounts
/// small VHDX files with build/test-volumes.ps1):
///   PC_TEST_VOLUMES       roots to test, e.g. "T:\;U:\;V:\;X:\"
///   PC_TEST_FULL_VOLUME   a tiny NTFS volume (about 40 MB) for the disk-full tests, e.g. "W:\"
///   PC_TEST_BIG=1         also the slow test that writes a file over 4 GB
/// Sources live on the system temp folder (NTFS), so every copy crosses file systems.
/// </summary>
public sealed class VolumeMatrixTests : IDisposable
{
    private const string NoVolumes = "PC_TEST_VOLUMES not set";
    private const int Streamed = (5 << 20) + 123; // above TransferOperation's CopyFileEx limit

    private static readonly string[] Names =
    [
        "zażółć gęślą jaźń.txt", "日本語のファイル.txt", "emoji 😀 plik.txt", "two  spaces.txt", "e\u0301 NFD.txt",
        "UPPER.TXT", "dots.in.the.name.tar.gz", "#hash %percent & [brackets] {braces}.txt", "  leading spaces.txt",
        new string('d', 180) + ".txt",
    ];

    private readonly WeirdBox _local = new();
    private readonly List<string> _cleanup = [];

    public static TheoryData<string> Volumes()
    {
        var data = new TheoryData<string>();
        foreach (var root in VolumeRoots())
        {
            data.Add(root);
        }

        if (data.Count == 0)
        {
            data.Add(string.Empty); // one skipped row instead of "no data" errors
        }

        return data;
    }

    private static string[] VolumeRoots() =>
        (Environment.GetEnvironmentVariable("PC_TEST_VOLUMES") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void Dispose()
    {
        foreach (var dir in _cleanup)
        {
            try
            {
                ClearAttributes(dir);
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _local.Dispose();
    }

    // ---------------------------------------------------------------- copy and move across file systems

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Tree_with_odd_names_copies_there_and_back_unchanged(string volume)
    {
        var work = Work(volume);
        var source = OddTree("src");

        var there = await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(work, null)));
        there.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, there));
        var back = await RunAsync(new TransferOperation(TransferKind.Copy, [Path.Combine(work, "src")], new TransferTarget(_local.Dir("back"), null)));
        back.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, back));

        Snapshot(Path.Combine(work, "src")).Should().Equal(Snapshot(source), $"copy on {Fs(volume)}");
        Snapshot(_local.P("back", "src")).Should().Equal(Snapshot(source), $"copy back from {Fs(volume)}");
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Copied_tree_compares_equal_to_its_source(string volume)
    {
        // FAT keeps times in 2 s steps, exFAT in 10 ms; the comparison must not call the copy newer or older.
        var work = Work(volume);
        var source = OddTree("src");
        var odd = new DateTime(2025, 3, 7, 10, 11, 13, 987, DateTimeKind.Local).AddTicks(4321);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTime(file, odd);
        }

        (await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(work, null)))).Outcome.Should().Be(OperationOutcome.Completed);

        var result = DirectoryCompare.Run(source, Path.Combine(work, "src"), new CompareOptions(Subfolders: true));
        result.Items.Select(p => $"{p.Key}: {p.Value}").Should().BeEmpty($"{Fs(volume)} rounds times but the files are the same");
        SyncPlan.From(result).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Continuing_a_copy_skips_files_already_there(string volume)
    {
        // What "continue as administrator" does: files the first run copied are taken as done, without questions.
        var work = Work(volume);
        var source = OddTree("src");
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        for (var i = 0; i < files.Length; i++)
        {
            // Odd and even seconds with every kind of fraction, so some round up and some down on FAT.
            File.SetLastWriteTimeUtc(files[i], new DateTime(2025, 3, 7, 10, 11, 10 + i, 100 + (i * 397 % 900), DateTimeKind.Utc));
        }

        (await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(work, null)))).Outcome.Should().Be(OperationOutcome.Completed);
        var copied = Directory.GetFiles(Path.Combine(work, "src"), "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        // No conflict answer is configured: a question here fails the test.
        var again = await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(work, null), skipIdentical: true));

        again.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, again));
        copied.Where(p => File.GetLastWriteTimeUtc(p.Key) != p.Value).Select(p => p.Key).Should().BeEmpty("nothing is copied twice");
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Tree_moves_onto_the_volume_and_within_it(string volume)
    {
        var work = Work(volume);
        var source = OddTree("src");
        var expected = Snapshot(source);

        var onto = await RunAsync(new TransferOperation(TransferKind.Move, [source], new TransferTarget(work, null)));
        onto.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, onto));
        Directory.Exists(source).Should().BeFalse();

        var inner = Path.Combine(work, "inner");
        Directory.CreateDirectory(inner);
        var within = await RunAsync(new TransferOperation(TransferKind.Move, [Path.Combine(work, "src")], new TransferTarget(inner, null)));
        within.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, within));

        Snapshot(Path.Combine(inner, "src")).Should().Equal(expected);
        Directory.Exists(Path.Combine(work, "src")).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Moving_back_to_ntfs_keeps_everything(string volume)
    {
        var work = Work(volume);
        var source = OddTree("src");
        var expected = Snapshot(source);
        (await RunAsync(new TransferOperation(TransferKind.Move, [source], new TransferTarget(work, null)))).Outcome.Should().Be(OperationOutcome.Completed);

        var back = await RunAsync(new TransferOperation(TransferKind.Move, [Path.Combine(work, "src")], new TransferTarget(_local.Dir("back"), null)));

        back.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, back));
        Snapshot(_local.P("back", "src")).Should().Equal(expected);
        Directory.Exists(Path.Combine(work, "src")).Should().BeFalse();
    }

    // ---------------------------------------------------------------- names and attributes

    [Theory]
    [MemberData(nameof(Volumes))]
    public void Case_only_rename_works(string volume)
    {
        var work = Work(volume);
        var file = Path.Combine(work, "readme.txt");
        File.WriteAllText(file, "case");
        var dir = Directory.CreateDirectory(Path.Combine(work, "folder")).FullName;

        var renamed = SimpleCommands.Rename(file, "README.TXT");
        var renamedDir = SimpleCommands.Rename(dir, "Folder");

        Directory.GetFileSystemEntries(work).Select(Path.GetFileName).Should().BeEquivalentTo(["README.TXT", "Folder"]);
        File.ReadAllText(renamed).Should().Be("case");
        Directory.Exists(renamedDir).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task File_with_alternate_stream_copies_its_main_data(string volume)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "alternate data streams are NTFS-only");
        var work = Work(volume);
        var file = _local.File("ads/tagged.txt", "main data");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
        File.WriteAllText(file + ":note", "side data");

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(work, null)), ErrorChoice.Skip, StreamsChoice.Continue);

        // FAT and exFAT have nowhere to keep the streams; the user said to go on without them.
        File.ReadAllText(Path.Combine(work, "tagged.txt")).Should().Be("main data", Describe(volume, result));
        result.Outcome.Should().BeOneOf([OperationOutcome.Completed], Describe(volume, result));
        if (Fs(volume) is "NTFS" or "ReFS")
        {
            File.ReadAllText(Path.Combine(work, "tagged.txt") + ":note").Should().Be("side data");
        }
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Large_file_with_alternate_stream_copies_its_main_data(string volume)
    {
        // Above the CopyFileEx limit the file is streamed and the alternate streams are copied by hand.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "alternate data streams are NTFS-only");
        var work = Work(volume);
        var data = Bytes.Pattern(Streamed, 21);
        var file = Path.Combine(_local.Dir("ads"), "big.bin");
        File.WriteAllBytes(file, data);
        File.WriteAllText(file + ":note", "side data");

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(work, null)), streams: StreamsChoice.Continue);

        result.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, result));
        File.ReadAllBytes(Path.Combine(work, "big.bin")).Should().Equal(data);
        if (Fs(volume) is "NTFS" or "ReFS")
        {
            File.ReadAllText(Path.Combine(work, "big.bin") + ":note").Should().Be("side data");
        }
    }

    /// <summary>A file with a real alternate stream, small or above the CopyFileEx limit.</summary>
    private string StreamFile(string folder, string name, bool large, string stream = ":note")
    {
        var file = Path.Combine(_local.Dir(folder), name);
        File.WriteAllBytes(file, Bytes.Pattern(large ? Streamed : 1000, 31));
        File.WriteAllText(file + stream, "side data");
        return file;
    }

    private static bool KeepsStreams(string volume) => Fs(volume) is "NTFS" or "ReFS";

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Copy_with_streams_asks_only_where_they_would_be_lost(string volume)
    {
        var work = Work(volume);
        var file = StreamFile("ads", "a.bin", large: false);
        var prompts = new HookPrompts(streams: _ => StreamsChoice.Continue);

        var result = await new TransferOperation(TransferKind.Copy, [file], new TransferTarget(work, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, result));
        prompts.LostStreams.Should().HaveCount(KeepsStreams(volume) ? 0 : 1, Fs(volume));
        if (!KeepsStreams(volume))
        {
            var info = prompts.LostStreams.Single();
            info.Streams.Should().Equal(":note");
            info.IsMove.Should().BeFalse();
            info.SourcePath.Should().Be(file);
        }

        File.Exists(Path.Combine(work, "a.bin")).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Large_file_asks_too_and_copies_after_continue(string volume)
    {
        var work = Work(volume);
        var file = StreamFile("ads", "big.bin", large: true);
        var prompts = new HookPrompts(streams: _ => StreamsChoice.Continue);

        var result = await new TransferOperation(TransferKind.Copy, [file], new TransferTarget(work, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, result));
        prompts.LostStreams.Should().HaveCount(KeepsStreams(volume) ? 0 : 1);
        File.ReadAllBytes(Path.Combine(work, "big.bin")).Should().Equal(File.ReadAllBytes(file));
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Skip_leaves_the_file_out_and_a_move_keeps_the_source(string volume)
    {
        Assert.SkipWhen(volume.Length > 0 && KeepsStreams(volume), "nothing is lost here, so nothing is asked");
        var work = Work(volume);
        var file = StreamFile("ads", "keep.bin", large: false);
        var other = Path.Combine(_local.P("ads"), "plain.txt");
        File.WriteAllText(other, "no streams");
        var prompts = new HookPrompts(streams: _ => StreamsChoice.Skip);

        var result = await new TransferOperation(TransferKind.Move, [file, other], new TransferTarget(work, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips, Describe(volume, result));
        result.Skipped.Should().Be(1);
        prompts.LostStreams.Should().ContainSingle().Which.IsMove.Should().BeTrue();
        File.Exists(Path.Combine(work, "keep.bin")).Should().BeFalse();
        File.ReadAllText(file + ":note").Should().Be("side data", "the skipped file stays whole where it was");
        File.ReadAllText(Path.Combine(work, "plain.txt")).Should().Be("no streams");
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Cancel_stops_the_operation_and_loses_nothing(string volume)
    {
        Assert.SkipWhen(volume.Length > 0 && KeepsStreams(volume), "nothing is lost here, so nothing is asked");
        var work = Work(volume);
        var file = StreamFile("ads", "stay.bin", large: false);
        var prompts = new HookPrompts(streams: _ => StreamsChoice.Cancel);

        var result = await new TransferOperation(TransferKind.Move, [file], new TransferTarget(work, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().BeOneOf(OperationOutcome.Cancelled, OperationOutcome.Aborted);
        File.ReadAllText(file + ":note").Should().Be("side data");
        Directory.GetFiles(work).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Continue_for_all_asks_one_time_for_many_files(string volume)
    {
        Assert.SkipWhen(volume.Length > 0 && KeepsStreams(volume), "nothing is lost here, so nothing is asked");
        var work = Work(volume);
        var folder = _local.Dir("many-ads");
        for (var i = 0; i < 5; i++)
        {
            var f = Path.Combine(folder, $"f{i}.bin");
            File.WriteAllText(f, "x" + i);
            File.WriteAllText(f + ":note", "s" + i);
        }

        var prompts = new HookPrompts(streams: _ => StreamsChoice.ContinueAll);

        var result = await new TransferOperation(TransferKind.Copy, [folder], new TransferTarget(work, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, result));
        prompts.LostStreams.Should().HaveCount(1);
        Directory.GetFiles(Path.Combine(work, "many-ads")).Should().HaveCount(5);
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Skip_for_all_leaves_every_such_file_out_but_copies_the_rest(string volume)
    {
        Assert.SkipWhen(volume.Length > 0 && KeepsStreams(volume), "nothing is lost here, so nothing is asked");
        var work = Work(volume);
        var folder = _local.Dir("some-ads");
        for (var i = 0; i < 4; i++)
        {
            var f = Path.Combine(folder, $"f{i}.bin");
            File.WriteAllText(f, "x" + i);
            if (i % 2 == 0)
            {
                File.WriteAllText(f + ":note", "s" + i);
            }
        }

        var prompts = new HookPrompts(streams: _ => StreamsChoice.SkipAll);

        var result = await new TransferOperation(TransferKind.Copy, [folder], new TransferTarget(work, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Skipped.Should().Be(2);
        prompts.LostStreams.Should().HaveCount(1, "SkipAll is remembered");
        Directory.GetFiles(Path.Combine(work, "some-ads")).Select(Path.GetFileName).Order().Should().Equal("f1.bin", "f3.bin");
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Internet_origin_marker_alone_is_dropped_without_a_question(string volume)
    {
        var work = Work(volume);
        var file = StreamFile("ads", "download.bin", large: true, stream: ":Zone.Identifier");
        var prompts = new HookPrompts(); // any question fails the test

        var result = await new TransferOperation(TransferKind.Copy, [file], new TransferTarget(work, null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, result));
        File.Exists(Path.Combine(work, "download.bin")).Should().BeTrue();
        if (KeepsStreams(volume))
        {
            File.ReadAllText(Path.Combine(work, "download.bin") + ":Zone.Identifier").Should().Be("side data");
        }
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Read_only_and_hidden_files_copy_and_delete(string volume)
    {
        var work = Work(volume);
        var dir = _local.Dir("attrs");
        var file = _local.File("attrs/locked.txt", "ro");
        File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);

        (await RunAsync(new TransferOperation(TransferKind.Copy, [dir], new TransferTarget(work, null)))).Outcome.Should().Be(OperationOutcome.Completed);
        var copy = Path.Combine(work, "attrs", "locked.txt");
        File.GetAttributes(copy).Should().HaveFlag(FileAttributes.ReadOnly).And.HaveFlag(FileAttributes.Hidden);

        var deleted = await RunAsync(new DeleteOperation([Path.Combine(work, "attrs")]));
        deleted.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, deleted));
        Directory.Exists(Path.Combine(work, "attrs")).Should().BeFalse();
        File.SetAttributes(file, FileAttributes.Normal);
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Attributes_dialog_changes_work_or_fail_cleanly(string volume)
    {
        var work = Work(volume);
        var file = Path.Combine(work, "a.txt");
        File.WriteAllText(file, "a");
        var when = new DateTimeOffset(2020, 2, 29, 12, 0, 1, TimeSpan.Zero);

        var result = await RunAsync(new AttributesOperation([file], new AttributeChange(FileAttributes.Hidden | FileAttributes.Archive, FileAttributes.None, when, Recursive: false)), ErrorChoice.Skip);

        result.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, result));
        File.GetAttributes(file).Should().HaveFlag(FileAttributes.Hidden);
        (File.GetLastWriteTimeUtc(file) - when.UtcDateTime).Duration().Should().BeLessThanOrEqualTo(CompareOptions.TimeTolerance);
    }

    // ---------------------------------------------------------------- encryption, archives, sizes

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Pce_round_trip_on_the_volume(string volume)
    {
        var work = Work(volume);
        var file = Path.Combine(work, "secret.bin");
        var data = Bytes.Pattern(300_000, 7);
        File.WriteAllBytes(file, data);

        var encrypted = await RunAsync(new EncryptOperation([file], ".pce", "hasło ąę", options: PceOptions.Fast));
        encrypted.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, encrypted));
        File.Delete(file);
        var decrypted = await RunAsync(new DecryptOperation([file + ".pce"], "hasło ąę"));

        decrypted.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, decrypted));
        File.ReadAllBytes(file).Should().Equal(data);
        Directory.GetFiles(work).Select(Path.GetFileName).Should().BeEquivalentTo(["secret.bin", "secret.bin.pce"], "no temporary files may stay behind");
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Zip_round_trip_on_the_volume(string volume)
    {
        var work = Work(volume);
        var source = OddTree("src");
        var zip = Path.Combine(work, "pack.zip");

        var packed = await RunAsync(new ArchivePackOperation([source], zip));
        packed.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, packed));
        var unpacked = await RunAsync(new ArchiveExtractOperation([zip], new TransferTarget(work, null)));

        unpacked.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, unpacked));
        Snapshot(Path.Combine(work, "pack", "src")).Should().Equal(Snapshot(source));
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Folder_size_counts_what_is_there(string volume)
    {
        var work = Work(volume);
        var source = OddTree("src");
        (await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(work, null)))).Outcome.Should().Be(OperationOutcome.Completed);

        var there = await DirectorySize.CalculateAsync(Path.Combine(work, "src"), TestContext.Current.CancellationToken);
        var here = await DirectorySize.CalculateAsync(source, TestContext.Current.CancellationToken);

        there.Should().Be(here);
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Many_files_in_one_folder(string volume)
    {
        var work = Work(volume);
        var source = _local.Dir("many");
        for (var i = 0; i < 1500; i++)
        {
            File.WriteAllText(Path.Combine(source, $"plik numer {i:D4} z długą nazwą.txt"), i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(work, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed, Describe(volume, result));
        Directory.GetFiles(Path.Combine(work, "many")).Should().HaveCount(1500);
    }

    // ---------------------------------------------------------------- limits of the file system

    [Theory]
    [MemberData(nameof(Volumes))]
    public async Task Fat16_root_folder_limit_fails_cleanly(string volume)
    {
        Skip(volume);
        Assert.SkipUnless(Fs(volume) == "FAT", "only FAT12/16 has a fixed-size root folder");
        var source = _local.Dir("root-flood");
        for (var i = 0; i < 600; i++)
        {
            File.WriteAllText(Path.Combine(source, $"r{i:D3}.txt"), "r");
        }

        var files = Directory.GetFiles(source);
        var before = Directory.GetFileSystemEntries(volume).Length;
        try
        {
            var result = await RunAsync(new TransferOperation(TransferKind.Copy, files, new TransferTarget(volume, null)), ErrorChoice.SkipAll);

            // FAT16 holds at most 512 root entries: some files fail, each one reported, none half-written.
            result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips, Describe(volume, result));
            var copied = Directory.GetFiles(volume, "r*.txt");
            copied.Length.Should().Be(result.Done);
            result.Done.Should().BeLessThan(600).And.BeGreaterThan(0);
            copied.Should().OnlyContain(f => File.ReadAllText(f) == "r");
        }
        finally
        {
            foreach (var f in Directory.GetFiles(volume, "r*.txt"))
            {
                File.Delete(f);
            }

            Directory.GetFileSystemEntries(volume).Length.Should().Be(before);
        }
    }

    [Fact]
    public async Task Copy_bigger_than_free_space_fails_without_leftovers()
    {
        var volume = Full();
        var work = Work(volume);
        var big = _local.Dir("big");
        File.WriteAllBytes(Path.Combine(big, "a-small.txt"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(big, "b-huge.bin"), Bytes.Pattern(new DriveInfo(volume).AvailableFreeSpace > 60 << 20 ? 120 << 20 : 60 << 20, 3));
        File.WriteAllBytes(Path.Combine(big, "c-small.txt"), [4, 5, 6]);
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        var result = await new TransferOperation(TransferKind.Copy, [big], new TransferTarget(work, null), parallelism: 1)
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips, Describe(volume, result));
        prompts.Errors.Should().ContainSingle().Which.Should().BeAssignableTo<IOException>();
        File.Exists(Path.Combine(work, "big", "b-huge.bin")).Should().BeFalse("a half-written copy must be removed");
        File.ReadAllBytes(Path.Combine(work, "big", "a-small.txt")).Should().Equal(1, 2, 3);
        File.ReadAllBytes(Path.Combine(work, "big", "c-small.txt")).Should().Equal(4, 5, 6);
        Directory.GetFiles(work, "*", SearchOption.AllDirectories).Should().HaveCount(2, "no temporary files may stay behind");
    }

    [Fact]
    public async Task Move_bigger_than_free_space_keeps_the_source()
    {
        var volume = Full();
        var work = Work(volume);
        var file = _local.File("move/huge.bin", string.Empty);
        var data = Bytes.Pattern(new DriveInfo(volume).AvailableFreeSpace > 60 << 20 ? 120 << 20 : 60 << 20, 5);
        File.WriteAllBytes(file, data);

        var result = await RunAsync(new TransferOperation(TransferKind.Move, [file], new TransferTarget(work, null)), ErrorChoice.Skip);

        result.Outcome.Should().NotBe(OperationOutcome.Completed);
        File.ReadAllBytes(file).Should().Equal(data, "the source must survive a failed move");
        Directory.GetFiles(work, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Encrypting_on_a_full_volume_keeps_the_original()
    {
        var volume = Full();
        var work = Work(volume);
        var file = Path.Combine(work, "fill.bin");
        var size = (int)Math.Min(new DriveInfo(volume).AvailableFreeSpace * 2 / 3, 100 << 20);
        var data = Bytes.Pattern(size, 9);
        File.WriteAllBytes(file, data);

        var result = await RunAsync(new EncryptOperation([file], ".pce", "x", options: PceOptions.Fast), ErrorChoice.Skip);

        result.Outcome.Should().NotBe(OperationOutcome.Completed);
        File.ReadAllBytes(file).Should().Equal(data);
        Directory.GetFiles(work).Select(Path.GetFileName).Should().Equal(["fill.bin"], "no partial .pce or temp file may remain");
    }

    [Fact]
    public async Task File_over_4_GB_to_FAT32_fails_cleanly()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("PC_TEST_BIG") == "1", "PC_TEST_BIG not set");
        var volume = VolumeRoots().FirstOrDefault(v => Directory.Exists(v) && Fs(v) == "FAT32");
        Assert.SkipWhen(volume is null, "no FAT32 volume in PC_TEST_VOLUMES");
        var work = Work(volume!);
        var file = _local.File("huge/over4g.bin", string.Empty);
        using (var stream = File.OpenWrite(file))
        {
            stream.SetLength((4L << 30) + (1 << 20));
        }

        var started = DateTime.UtcNow;
        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(work, null)), ErrorChoice.Skip);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips, Describe(volume!, result));
        Directory.GetFiles(work).Should().BeEmpty("the partial copy must be removed");
        TestContext.Current.SendDiagnosticMessage($"over-4GB copy to FAT32 took {(DateTime.UtcNow - started).TotalSeconds:F0} s");
    }

    // ---------------------------------------------------------------- helpers

    private string Work(string volume)
    {
        Skip(volume);
        var dir = Path.Combine(volume, "pc-vol-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _cleanup.Add(dir);
        return dir;
    }

    private static void Skip(string volume)
    {
        Assert.SkipWhen(volume.Length == 0, NoVolumes);
        Assert.SkipUnless(Directory.Exists(volume), volume + " is not mounted");
    }

    private static string Full()
    {
        var volume = Environment.GetEnvironmentVariable("PC_TEST_FULL_VOLUME") ?? string.Empty;
        Assert.SkipWhen(volume.Length == 0, "PC_TEST_FULL_VOLUME not set");
        Assert.SkipUnless(Directory.Exists(volume), volume + " is not mounted");
        return volume;
    }

    private static string Fs(string volume) => new DriveInfo(volume).DriveFormat;

    private static string Describe(string volume, OperationResult result) =>
        $"{volume} ({Fs(volume)}): {result.Outcome}, done {result.Done}, skipped {result.Skipped}; {string.Join("; ", result.Errors)}";

    private string OddTree(string name)
    {
        var root = _local.Dir(name);
        foreach (var n in Names)
        {
            File.WriteAllText(Path.Combine(root, n), "content of " + n);
        }

        var deep = root;
        for (var i = 0; i < 12; i++)
        {
            deep = Path.Combine(deep, $"poziom {i} ąę");
        }

        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "na dnie.txt"), "bottom");
        Directory.CreateDirectory(Path.Combine(root, "empty folder"));
        File.WriteAllBytes(Path.Combine(root, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(root, "streamed.bin"), Bytes.Pattern(Streamed, 11));
        return root;
    }

    /// <summary>Relative path → content hash for files, "dir" for folders, sorted.</summary>
    private static List<string> Snapshot(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p) + " = " + (Directory.Exists(p) ? "dir" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))[..16]))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static void ClearAttributes(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }
    }

    private static Task<OperationResult> RunAsync(IOperation op, ErrorChoice? onError = null, StreamsChoice? streams = null)
    {
        var prompts = new HookPrompts(
            error: onError is { } choice ? (_, _) => choice : null,
            streams: streams is { } answer ? _ => answer : null);
        return op.RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));
    }
}
