using System.Diagnostics;
using AwesomeAssertions;
using PlainCommander.Core.Details;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Rename;
using PlainCommander.Core.Search;
using PlainCommander.Core.Undo;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>
/// Odd Windows file-system combinations pushed through the app's own Core APIs: reserved device names, names ending
/// in a dot or space, very long paths, letter case, alternate data streams, hidden/system/read-only, junctions, locked,
/// sparse and compressed files, 8.3 short names and drive-relative paths. Every test runs on Windows only.
/// </summary>
public sealed class WindowsFileSystemTests : IDisposable
{
    private const string WindowsOnly = "Windows file-system behaviour";
    private const int Big = (5 << 20) + 123; // above TransferOperation's 4 MB limit: streamed, not CopyFileEx

    private readonly WeirdBox _s = new();

    public void Dispose() => _s.Dispose();

    private static void Windows() => Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

    // ---------------------------------------------------------------- reserved device names

    public static TheoryData<string> ReservedNames => ["CON", "COM1", "LPT1", "AUX", "PRN", "con.txt", "nul.tar.gz", "nul.txt"];

    [Theory]
    [MemberData(nameof(ReservedNames))]
    public async Task Reserved_name_is_listed_with_its_name_and_size(string name)
    {
        Windows();
        _s.File($"d/{name}", "device-named");

        var entries = await new LocalFileSystem().ListAsync(_s.P("d"), includeHidden: true, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        var entry = entries.Should().ContainSingle(e => e.Name == name).Subject;
        entry.Size.Should().Be("device-named".Length);
        entry.FullPath.Should().Be(_s.P("d", name));
    }

    [Theory]
    [MemberData(nameof(ReservedNames))]
    public async Task Reserved_name_copies_to_a_real_file(string name)
    {
        Windows();
        var source = _s.File($"src/{name}", "device-named");
        var dst = _s.Dir("dst");

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(dst, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        WeirdBox.ReadExact(_s.P("dst", name)).Should().Be("device-named");
        WeirdBox.ReadExact(source).Should().Be("device-named");
    }

    [Theory]
    [MemberData(nameof(ReservedNames))]
    public async Task Reserved_name_moves_into_another_folder(string name)
    {
        Windows();
        var source = _s.File($"src/{name}", "device-named");
        var dst = _s.Dir("dst");

        var result = await RunAsync(new TransferOperation(TransferKind.Move, [source], new TransferTarget(dst, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        WeirdBox.ReadExact(_s.P("dst", name)).Should().Be("device-named");
        WeirdBox.ExistsExact(source).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ReservedNames))]
    public async Task Reserved_name_is_deleted(string name)
    {
        Windows();
        var source = _s.File($"src/{name}", "device-named");

        var result = await RunAsync(new DeleteOperation([source]));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        WeirdBox.ExistsExact(source).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ReservedNames))]
    public void Reserved_name_renames(string name)
    {
        Windows();
        var source = _s.File($"src/{name}", "device-named");

        var renamed = SimpleCommands.Rename(source, "renamed.txt");

        renamed.Should().Be(_s.P("src", "renamed.txt"));
        WeirdBox.ReadExact(renamed).Should().Be("device-named");
        WeirdBox.ExistsExact(source).Should().BeFalse();
    }

    [Fact]
    public async Task Reserved_name_folder_tree_copies_and_deletes()
    {
        Windows();
        _s.File("src/top/LPT1/COM1/CON", "inner");
        var dst = _s.Dir("dst");

        var copy = await RunAsync(new TransferOperation(TransferKind.Copy, [_s.P("src", "top")], new TransferTarget(dst, null)));
        copy.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", copy.Errors));
        WeirdBox.ReadExact(_s.P("dst", "top", "LPT1", "COM1", "CON")).Should().Be("inner");

        var delete = await RunAsync(new DeleteOperation([_s.P("src", "top"), _s.P("dst", "top", "LPT1")]));
        delete.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", delete.Errors));
        WeirdBox.ExistsExact(_s.P("src", "top")).Should().BeFalse();
        WeirdBox.ExistsExact(_s.P("dst", "top", "LPT1")).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_of_a_folder_named_aux_writes_its_content_into_the_current_directory()
    {
        Windows();
        var unique = "pc-weird-" + Guid.NewGuid().ToString("N")[..8];
        _s.File($"src/aux/{unique}/f.txt", "inner");
        var dst = _s.Dir("dst");
        var stray = Path.Combine(Environment.CurrentDirectory, "aux", unique);

        try
        {
            await RunAsync(new TransferOperation(TransferKind.Copy, [_s.P("src", "aux")], new TransferTarget(dst, null)), errors: ErrorChoice.Skip);

            WeirdBox.ExistsExact(stray).Should().BeFalse("nothing may be written outside the target folder");
            WeirdBox.ReadExact(_s.P("dst", "aux", unique, "f.txt")).Should().Be("inner");
        }
        finally
        {
            if (Directory.Exists(WeirdBox.X(stray)))
            {
                Directory.Delete(WeirdBox.X(stray), recursive: true);
                var auxDir = WeirdBox.X(Path.Combine(Environment.CurrentDirectory, "aux"));
                if (!Directory.EnumerateFileSystemEntries(auxDir).Any())
                {
                    Directory.Delete(auxDir);
                }
            }
        }
    }

    [Fact]
    public async Task A_file_named_NUL_cannot_be_deleted_because_its_path_turns_into_the_NUL_device()
    {
        Windows();
        var nul = _s.File("d/NUL", "left behind by a stray \"> nul\" in a Unix shell");

        var result = await RunAsync(new DeleteOperation([nul]), errors: ErrorChoice.Skip);

        result.Errors.Should().BeEmpty();
        WeirdBox.ExistsExact(nul).Should().BeFalse();
    }

    // ---------------------------------------------------------------- trailing dots and spaces

    [Fact]
    public async Task Dotted_and_spaced_names_list_copy_move_and_delete()
    {
        Windows();
        _s.File("src/a.", "dot");
        _s.File("src/b ", "space");
        _s.File("src/d./f.txt", "inside");
        _s.File("src/a", "twin of a.");

        var listed = await new LocalFileSystem().ListAsync(_s.P("src"), includeHidden: true, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        listed.Select(e => e.Name).Should().Contain(["a.", "b ", "d.", "a"]);

        var dst = _s.Dir("dst");
        var copy = await RunAsync(new TransferOperation(TransferKind.Copy, [_s.P("src", "a."), _s.P("src", "b "), _s.P("src", "d.")], new TransferTarget(dst, null)));
        copy.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", copy.Errors));
        WeirdBox.ReadExact(_s.P("dst", "a.")).Should().Be("dot");
        WeirdBox.ReadExact(_s.P("dst", "b ")).Should().Be("space");
        WeirdBox.ReadExact(_s.P("dst", "d.", "f.txt")).Should().Be("inside");
        WeirdBox.ExistsExact(_s.P("dst", "a")).Should().BeFalse("only the dotted name was copied");

        var moved = _s.Dir("moved");
        var move = await RunAsync(new TransferOperation(TransferKind.Move, [_s.P("dst", "d.")], new TransferTarget(moved, null)));
        move.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", move.Errors));
        WeirdBox.ReadExact(_s.P("moved", "d.", "f.txt")).Should().Be("inside");

        var delete = await RunAsync(new DeleteOperation([_s.P("src", "a."), _s.P("src", "d.")]));
        delete.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", delete.Errors));
        WeirdBox.ExistsExact(_s.P("src", "a.")).Should().BeFalse();
        WeirdBox.ReadExact(_s.P("src", "a")).Should().Be("twin of a.", "deleting \"a.\" must not touch \"a\"");
    }

    [Fact]
    public async Task Copy_of_a_folder_cannot_read_names_inside_it_that_end_in_a_dot_or_space()
    {
        Windows();
        _s.File("src/d/f ", "with space");
        _s.File("src/d/g.", "with dot");
        var dst = _s.Dir("dst");

        var copy = await RunAsync(new TransferOperation(TransferKind.Copy, [_s.P("src", "d")], new TransferTarget(dst, null)), errors: ErrorChoice.Skip);

        copy.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", copy.Errors));
        Directory.GetFiles(WeirdBox.X(_s.P("dst", "d"))).Select(Path.GetFileName).Should().BeEquivalentTo(["f ", "g."]);
    }

    [Fact]
    public void Multi_rename_of_a_dotted_name_renames_the_wrong_file_when_its_undotted_twin_exists()
    {
        Windows();
        var dotted = _s.File("d/a.", "dotted");
        _s.File("d/a", "plain");
        var source = new RenameSource(dotted, "a.", IsDirectory: false, DateTimeOffset.Now);

        var plan = MultiRename.Preview([source], new RenameRules(NameMask: "renamed", ExtensionMask: ""), WeirdBox.ExistsExact);
        plan.Single().Problem.Should().Be(RenameProblem.None);
        MultiRename.Apply(plan);

        WeirdBox.ReadExact(_s.P("d", "a")).Should().Be("plain", "the file that was not selected keeps its name");
        WeirdBox.ReadExact(_s.P("d", "renamed")).Should().Be("dotted");
    }

    [Fact]
    public async Task Attributes_on_a_dotted_name_change_that_file_only_and_undo()
    {
        Windows();
        var dotted = _s.File("d/a.", "dotted");
        var plain = _s.File("d/a", "plain");

        var op = new AttributesOperation([dotted], new AttributeChange(FileAttributes.Hidden, 0, null, Recursive: false));
        (await RunAsync(op)).Outcome.Should().Be(OperationOutcome.Completed);

        File.GetAttributes(WeirdBox.X(dotted)).Should().HaveFlag(FileAttributes.Hidden);
        File.GetAttributes(WeirdBox.X(plain)).Should().NotHaveFlag(FileAttributes.Hidden);

        var undo = await RunAsync(new UndoOperation(new UndoEntry("attr", op.Journal.Actions, DateTime.Now), new NoBin(), null));
        undo.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", undo.Errors));
        File.GetAttributes(WeirdBox.X(dotted)).Should().NotHaveFlag(FileAttributes.Hidden);
    }

    [Fact]
    public async Task Undo_of_moving_a_dotted_folder_brings_it_back()
    {
        Windows();
        _s.File("src/d./f.", "x");
        var dst = _s.Dir("dst");
        var move = new TransferOperation(TransferKind.Move, [_s.P("src", "d.")], new TransferTarget(dst, null));
        (await RunAsync(move)).Outcome.Should().Be(OperationOutcome.Completed);
        WeirdBox.ExistsExact(_s.P("dst", "d.", "f.")).Should().BeTrue();

        var undo = await RunAsync(new UndoOperation(new UndoEntry("move", move.Journal.Actions, DateTime.Now), new NoBin(), null));

        undo.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", undo.Errors));
        WeirdBox.ReadExact(_s.P("src", "d.", "f.")).Should().Be("x");
    }

    [Fact]
    public async Task Search_finds_dotted_names_inside_a_dotted_root()
    {
        Windows();
        _s.File("root./sub /hit.", "needle");
        _s.File("root./sub /miss.txt", "hay");

        var found = await FileSearch.RunAsync(new SearchCriteria(_s.P("root."), Masks: "hit*", Text: "needle"), null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        found.Should().ContainSingle().Which.FullPath.Should().Be(_s.P("root.", "sub ", "hit."));
    }

    // ---------------------------------------------------------------- very long paths

    [Fact]
    public async Task A_tree_near_the_32k_path_limit_lists_searches_and_deletes()
    {
        Windows();
        var deep = _s.P("deep");
        var segment = new string('s', 240);
        while (deep.Length + segment.Length + 1 < 32000)
        {
            deep = Path.Combine(deep, segment);
        }

        Directory.CreateDirectory(WeirdBox.X(deep));
        File.WriteAllText(WeirdBox.X(Path.Combine(deep, "bottom.txt")), "bottom");
        deep.Length.Should().BeGreaterThan(31000);

        var listed = await new LocalFileSystem().ListAsync(deep, includeHidden: true, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        listed.Should().Contain(e => e.Name == "bottom.txt");

        var found = await FileSearch.RunAsync(new SearchCriteria(_s.P("deep"), Masks: "bottom.txt"), null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        found.Should().ContainSingle();

        (await DirectorySize.CalculateAsync(_s.P("deep"), TestContext.Current.CancellationToken)).Bytes.Should().Be(6);

        var delete = await RunAsync(new DeleteOperation([_s.P("deep")]));
        delete.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", delete.Errors));
        Directory.Exists(WeirdBox.X(_s.P("deep"))).Should().BeFalse();
    }

    [Fact]
    public async Task A_255_character_name_copies_renames_and_deletes()
    {
        Windows();
        var name = new string('n', 251) + ".txt";
        var source = _s.File($"src/{name}", "long name");
        var dst = _s.Dir(new string('d', 200));

        var copy = await RunAsync(new TransferOperation(TransferKind.Copy, [source], new TransferTarget(dst, null)));
        copy.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", copy.Errors));
        var copied = Path.Combine(dst, name);
        WeirdBox.ReadExact(copied).Should().Be("long name");

        var renamed = SimpleCommands.Rename(copied, new string('r', 255));
        WeirdBox.ReadExact(renamed).Should().Be("long name");

        (await RunAsync(new DeleteOperation([dst]))).Outcome.Should().Be(OperationOutcome.Completed);
        WeirdBox.ExistsExact(dst).Should().BeFalse();
    }

    // ---------------------------------------------------------------- letter case

    [Fact]
    public async Task Case_only_rename_through_move_with_a_new_name()
    {
        Windows();
        var file = _s.File("d/file.txt", "f");
        _s.Dir("d", "folder");

        var r1 = await RunAsync(new TransferOperation(TransferKind.Move, [file], new TransferTarget(_s.P("d"), "FILE.TXT")));
        var r2 = await RunAsync(new TransferOperation(TransferKind.Move, [_s.P("d", "folder")], new TransferTarget(_s.P("d"), "Folder")));

        r1.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", r1.Errors));
        r2.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", r2.Errors));
        Directory.GetFileSystemEntries(_s.P("d")).Select(Path.GetFileName).Should().BeEquivalentTo(["FILE.TXT", "Folder"]);
        File.ReadAllText(_s.P("d", "FILE.TXT")).Should().Be("f");
    }

    [Fact]
    public void Multi_rename_changes_letter_case_only()
    {
        Windows();
        var file = _s.File("d/report.txt", "r");
        var plan = MultiRename.Preview([new RenameSource(file, "report.txt", false, DateTimeOffset.Now)], new RenameRules(Case: CaseChange.Upper), WeirdBox.ExistsExact);

        var outcome = MultiRename.Apply(plan);

        outcome.Failed.Should().BeEmpty();
        Directory.GetFiles(_s.P("d")).Select(Path.GetFileName).Should().Equal("REPORT.TXT");
    }

    [Fact]
    public async Task Case_sensitive_folder_lists_both_twins_and_deletes_one_only()
    {
        Windows();
        var dir = CaseSensitiveDir("cs");
        _s.File("cs/a.txt", "lower");
        _s.File("cs/A.txt", "upper");

        var listed = await new LocalFileSystem().ListAsync(dir, includeHidden: true, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        listed.Where(e => !e.IsParentLink).Select(e => e.Name).Should().BeEquivalentTo(["a.txt", "A.txt"]);

        (await RunAsync(new DeleteOperation([_s.P("cs", "a.txt")]))).Outcome.Should().Be(OperationOutcome.Completed);

        Directory.GetFiles(dir).Select(Path.GetFileName).Should().Equal("A.txt");
        File.ReadAllText(_s.P("cs", "A.txt")).Should().Be("upper");
    }

    [Fact]
    public async Task Case_sensitive_twins_copied_to_a_normal_folder_never_silently_lose_one()
    {
        Windows();
        CaseSensitiveDir("cs");
        for (var i = 0; i < 40; i++)
        {
            _s.File($"cs/f{i}.txt", $"lower {i}");
            _s.File($"cs/F{i}.TXT", $"upper {i}");
        }

        for (var round = 0; round < 5; round++)
        {
            var dst = _s.Dir($"dst{round}");
            var prompts = new AnswerAll(ConflictChoice.KeepBoth);
            var result = await RunAsync(new TransferOperation(TransferKind.Copy, Directory.GetFiles(_s.P("cs")), new TransferTarget(dst, null)), prompts);

            result.Errors.Should().BeEmpty();
            Directory.GetFiles(dst).Should().HaveCount(80, $"round {round}: both twins must arrive (one under a free name); {prompts.Conflicts} conflicts were asked");
        }
    }

    [Fact]
    public void Case_only_rename_onto_an_existing_twin_strands_the_file_under_a_temporary_name()
    {
        Windows();
        CaseSensitiveDir("cs");
        _s.File("cs/a.txt", "lower");
        _s.File("cs/A.txt", "upper");

        var act = () => SimpleCommands.Rename(_s.P("cs", "a.txt"), "A.txt");

        act.Should().Throw<Exception>("A.txt is taken");
        Directory.GetFiles(_s.P("cs")).Select(Path.GetFileName).Should().BeEquivalentTo(["a.txt", "A.txt"], "a failed rename leaves the file where it was");
        File.ReadAllText(_s.P("cs", "A.txt")).Should().Be("upper");
    }

    // ---------------------------------------------------------------- alternate data streams

    [Fact]
    public async Task Listing_shows_a_file_with_streams_once_and_a_small_copy_keeps_them()
    {
        Windows();
        var file = _s.File("src/f.txt", "main");
        File.WriteAllText(file + ":extra", "stream");
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        var listed = await new LocalFileSystem().ListAsync(_s.P("src"), includeHidden: true, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        listed.Where(e => !e.IsParentLink).Should().ContainSingle().Which.Size.Should().Be(4);

        var dst = _s.Dir("dst");
        (await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(dst, null)))).Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllText(Path.Combine(dst, "f.txt:extra")).Should().Be("stream");

        var moved = _s.Dir("moved");
        File.WriteAllText(Path.Combine(moved, "f.txt"), "old");
        var move = await RunAsync(new TransferOperation(TransferKind.Move, [Path.Combine(dst, "f.txt")], new TransferTarget(moved, null)), new AnswerAll(ConflictChoice.Overwrite));
        move.Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllText(Path.Combine(moved, "f.txt:extra")).Should().Be("stream");
    }

    [Fact]
    public async Task Copy_of_a_large_file_drops_its_alternate_data_streams_and_mark_of_the_web()
    {
        Windows();
        var file = _s.P("src", "big.bin");
        Directory.CreateDirectory(_s.P("src"));
        File.WriteAllBytes(file, new byte[Big]);
        File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var dst = _s.Dir("dst");

        (await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(dst, null)))).Outcome.Should().Be(OperationOutcome.Completed);

        File.Exists(Path.Combine(dst, "big.bin:Zone.Identifier")).Should().BeTrue("a small file keeps its streams (CopyFileEx), so a big one should too");
    }

    [Fact]
    public void Rename_to_a_stream_name_is_refused()
    {
        Windows();
        var file = _s.File("d/f.txt", "main");

        var act = () => SimpleCommands.Rename(file, "f.txt:hidden");

        act.Should().Throw<ArgumentException>();
        File.ReadAllText(file).Should().Be("main");
    }

    // ---------------------------------------------------------------- hidden, system, read-only

    private const FileAttributes Hsr = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly;

    [Theory]
    [InlineData(10)]
    [InlineData(Big)]
    public async Task Copy_keeps_hidden_system_read_only(int size)
    {
        Windows();
        var file = _s.P("src", "hsr.bin");
        Directory.CreateDirectory(_s.P("src"));
        File.WriteAllBytes(file, new byte[size]);
        File.SetAttributes(file, Hsr);
        var dst = _s.Dir("dst");

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [_s.P("src")], new TransferTarget(dst, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        (File.GetAttributes(_s.P("dst", "src", "hsr.bin")) & Hsr).Should().Be(Hsr);
        new FileInfo(_s.P("dst", "src", "hsr.bin")).Length.Should().Be(size);
    }

    [Fact]
    public async Task Overwrite_of_a_hidden_target_fails_with_access_denied()
    {
        Windows();
        var file = _s.File("src/f.txt", "new");
        var target = _s.File("dst/f.txt", "old");
        File.SetAttributes(target, FileAttributes.Hidden);

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(_s.P("dst"), null)), new AnswerAll(ConflictChoice.Overwrite), ErrorChoice.Skip);

        result.Errors.Should().BeEmpty();
        File.ReadAllText(target).Should().Be("new");
    }

    [Fact]
    public async Task Moving_a_read_only_folder_into_an_existing_folder_leaves_the_emptied_source_behind()
    {
        Windows();
        _s.File("src/d/f.txt", "f");
        File.SetAttributes(_s.P("src", "d"), FileAttributes.Directory | FileAttributes.ReadOnly); // e.g. a folder customized through desktop.ini
        _s.Dir("dst", "d");

        var move = await RunAsync(new TransferOperation(TransferKind.Move, [_s.P("src", "d")], new TransferTarget(_s.P("dst"), null)), errors: ErrorChoice.Skip);

        move.Errors.Should().BeEmpty();
        File.ReadAllText(_s.P("dst", "d", "f.txt")).Should().Be("f");
        Directory.Exists(_s.P("src", "d")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_removes_hidden_system_read_only_trees()
    {
        Windows();
        _s.File("dst/d/f.txt", "f");
        File.SetAttributes(_s.P("dst", "d", "f.txt"), Hsr);
        File.SetAttributes(_s.P("dst", "d"), FileAttributes.Directory | Hsr);
        File.SetAttributes(_s.P("dst"), FileAttributes.Directory | Hsr);

        var delete = await RunAsync(new DeleteOperation([_s.P("dst")]));

        delete.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", delete.Errors));
        Directory.Exists(_s.P("dst")).Should().BeFalse();
    }

    // ---------------------------------------------------------------- junctions and links

    [Fact]
    public async Task Delete_never_enters_a_junction_to_outside_a_loop_or_a_dangling_one()
    {
        Windows();
        _s.File("outside/precious.txt", "keep me");
        _s.File("victim/own.txt", "go");
        Junction(_s.P("victim", "to-outside"), _s.P("outside"));
        Junction(_s.P("victim", "loop"), _s.P("victim"));
        _s.Dir("gone");
        Junction(_s.P("victim", "dangling"), _s.P("gone"));
        Directory.Delete(_s.P("gone"));

        var result = await RunAsync(new DeleteOperation([_s.P("victim")]));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        Directory.Exists(_s.P("victim")).Should().BeFalse();
        File.ReadAllText(_s.P("outside", "precious.txt")).Should().Be("keep me");
    }

    [Fact]
    public async Task Deleting_a_junction_itself_keeps_its_target()
    {
        Windows();
        _s.File("outside/precious.txt", "keep me");
        Junction(_s.P("link"), _s.P("outside"));
        File.SetAttributes(_s.P("link"), FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly);

        var result = await RunAsync(new DeleteOperation([_s.P("link")]));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        Directory.Exists(_s.P("link")).Should().BeFalse();
        File.ReadAllText(_s.P("outside", "precious.txt")).Should().Be("keep me");
    }

    [Fact]
    public async Task Copy_follows_a_junction_and_stops_at_a_junction_loop()
    {
        Windows();
        _s.File("outside/o.txt", "o");
        _s.File("src/s.txt", "s");
        Junction(_s.P("src", "out"), _s.P("outside"));
        Junction(_s.P("src", "loop"), _s.P("src"));
        Junction(_s.P("outside", "back"), _s.P("src"));
        var dst = _s.Dir("dst");

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [_s.P("src")], new TransferTarget(dst, null))).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        File.ReadAllText(_s.P("dst", "src", "out", "o.txt")).Should().Be("o");
        new DirectoryInfo(_s.P("dst", "src", "out")).Attributes.Should().NotHaveFlag(FileAttributes.ReparsePoint);
    }

    [Fact]
    public async Task Move_into_an_existing_folder_turns_a_junction_into_an_empty_folder()
    {
        Windows();
        _s.File("outside/o.txt", "o");
        _s.File("src/proj/code.txt", "c");
        Junction(_s.P("src", "proj", "deps"), _s.P("outside"));
        _s.Dir("dst", "proj"); // existing folder: the move merges item by item

        var result = await RunAsync(new TransferOperation(TransferKind.Move, [_s.P("src", "proj")], new TransferTarget(_s.P("dst"), null)));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        File.ReadAllText(_s.P("outside", "o.txt")).Should().Be("o", "the junction's target is never touched");
        var moved = new DirectoryInfo(_s.P("dst", "proj", "deps"));
        (moved.Attributes.HasFlag(FileAttributes.ReparsePoint) || File.Exists(Path.Combine(moved.FullName, "o.txt")))
            .Should().BeTrue("the moved junction should still be a link (or at least carry the content it showed), not an empty folder");
    }

    [Fact]
    public async Task Fast_move_keeps_a_junction_and_its_target()
    {
        Windows();
        _s.File("outside/o.txt", "o");
        _s.File("src/proj/code.txt", "c");
        Junction(_s.P("src", "proj", "deps"), _s.P("outside"));
        var dst = _s.Dir("dst");

        var result = await RunAsync(new TransferOperation(TransferKind.Move, [_s.P("src", "proj")], new TransferTarget(dst, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        new DirectoryInfo(_s.P("dst", "proj", "deps")).Attributes.Should().HaveFlag(FileAttributes.ReparsePoint);
        File.ReadAllText(_s.P("dst", "proj", "deps", "o.txt")).Should().Be("o");
    }

    [Fact]
    public async Task Recursive_attributes_and_search_do_not_go_through_junctions()
    {
        Windows();
        _s.File("outside/o.txt", "needle");
        _s.File("tree/t.txt", "needle");
        Junction(_s.P("tree", "out"), _s.P("outside"));
        Junction(_s.P("tree", "loop"), _s.P("tree"));

        var op = new AttributesOperation([_s.P("tree")], new AttributeChange(FileAttributes.ReadOnly, 0, null, Recursive: true));
        (await RunAsync(op)).Outcome.Should().Be(OperationOutcome.Completed);
        File.GetAttributes(_s.P("tree", "t.txt")).Should().HaveFlag(FileAttributes.ReadOnly);
        File.GetAttributes(_s.P("outside", "o.txt")).Should().NotHaveFlag(FileAttributes.ReadOnly);

        var found = await FileSearch.RunAsync(new SearchCriteria(_s.P("tree"), Masks: "*.txt", Text: "needle"), null, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        found.Select(f => f.Name).Should().Equal("t.txt");
    }

    [Fact]
    public async Task Moving_a_folder_into_a_junction_that_points_inside_it_never_orphans_it()
    {
        Windows();
        _s.File("a/f.txt", "precious");
        _s.Dir("a", "sub");
        Junction(_s.P("j"), _s.P("a", "sub"));

        var result = await RunAsync(new TransferOperation(TransferKind.Move, [_s.P("a")], new TransferTarget(_s.P("j"), null)), errors: ErrorChoice.Skip);

        result.Should().NotBeNull();
        var reachable = File.Exists(_s.P("a", "f.txt"))
            || Directory.EnumerateFiles(_s.Root, "f.txt", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, AttributesToSkip = 0 }).Any();
        reachable.Should().BeTrue("the folder must not vanish into a cycle");
    }

    [Theory]
    [InlineData(10, TransferKind.Copy)]
    [InlineData(10, TransferKind.Move)]
    [InlineData(Big, TransferKind.Move)]
    public async Task Overwriting_a_file_with_itself_through_a_junction_keeps_its_content(int size, TransferKind kind)
    {
        Windows();
        var file = _s.P("a", "f.bin");
        Directory.CreateDirectory(_s.P("a"));
        var content = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(file, content);
        Junction(_s.P("j"), _s.P("a"));

        await RunAsync(new TransferOperation(kind, [file], new TransferTarget(_s.P("j"), null)), new AnswerAll(ConflictChoice.Overwrite), ErrorChoice.Skip);

        File.Exists(file).Should().BeTrue("copying or moving a file onto itself must not delete it");
        File.ReadAllBytes(file).Should().Equal(content);
    }

    [Theory]
    [InlineData(10, TransferKind.Copy)]
    [InlineData(10, TransferKind.Move)]
    public async Task Overwriting_a_file_with_its_own_hard_link_keeps_its_content(int size, TransferKind kind)
    {
        Windows();
        var file = _s.P("a", "f.bin");
        Directory.CreateDirectory(_s.P("a"));
        Directory.CreateDirectory(_s.P("b"));
        var content = Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(file, content);
        Shell("cmd.exe", "/c", "mklink", "/H", _s.P("b", "f.bin"), file);

        await RunAsync(new TransferOperation(kind, [file], new TransferTarget(_s.P("b"), null)), new AnswerAll(ConflictChoice.Overwrite), ErrorChoice.Skip);

        File.ReadAllBytes(_s.P("b", "f.bin")).Should().Equal(content);
    }

    [Fact]
    public async Task Big_copy_onto_itself_through_a_junction_deletes_the_only_copy()
    {
        Windows();
        var file = _s.P("a", "f.bin");
        Directory.CreateDirectory(_s.P("a"));
        File.WriteAllBytes(file, new byte[Big]);
        Junction(_s.P("j"), _s.P("a")); // j\f.bin is a\f.bin under another spelling (an 8.3 name or a hard link does the same)

        await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(_s.P("j"), null)), new AnswerAll(ConflictChoice.Overwrite), ErrorChoice.Skip);

        File.Exists(file).Should().BeTrue("a failed copy may only clean up what it wrote itself");
    }

    // ---------------------------------------------------------------- locked files

    [Theory]
    [InlineData(10)]
    [InlineData(Big)]
    public async Task Copy_of_a_locked_file_reports_an_error_and_leaves_no_partial_copy(int size)
    {
        Windows();
        var file = _s.P("src", "locked.bin");
        Directory.CreateDirectory(_s.P("src"));
        File.WriteAllBytes(file, new byte[size]);
        var dst = _s.Dir("dst");

        OperationResult result;
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(dst, null)), errors: ErrorChoice.Skip);
        }

        result.Outcome.Should().NotBe(OperationOutcome.Completed);
        result.Errors.Should().NotBeEmpty();
        File.Exists(Path.Combine(dst, "locked.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_and_delete_keep_a_locked_file_and_its_folders()
    {
        Windows();
        _s.File("src/d/locked.txt", "locked");
        _s.File("src/d/free.txt", "free");
        _s.Dir("dst", "d");

        using (new FileStream(_s.P("src", "d", "locked.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var move = await RunAsync(new TransferOperation(TransferKind.Move, [_s.P("src", "d")], new TransferTarget(_s.P("dst"), null)), errors: ErrorChoice.Skip);
            move.Errors.Should().NotBeEmpty();
            File.Exists(_s.P("dst", "d", "free.txt")).Should().BeTrue();
            File.Exists(_s.P("src", "d", "free.txt")).Should().BeFalse();

            var delete = await RunAsync(new DeleteOperation([_s.P("src")]), errors: ErrorChoice.Skip);
            delete.Errors.Should().NotBeEmpty();
        }

        File.ReadAllText(_s.P("src", "d", "locked.txt")).Should().Be("locked");
    }

    [Fact]
    public async Task Overwriting_a_locked_target_reports_an_error_and_keeps_the_target()
    {
        Windows();
        var file = _s.File("src/f.txt", "new");
        var target = _s.File("dst/f.txt", "old");

        OperationResult result;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await RunAsync(new TransferOperation(TransferKind.Copy, [file], new TransferTarget(_s.P("dst"), null)), new AnswerAll(ConflictChoice.Overwrite), ErrorChoice.Skip);
        }

        result.Errors.Should().NotBeEmpty();
        File.ReadAllText(target).Should().Be("old");
    }

    // ---------------------------------------------------------------- sparse and compressed

    [Fact]
    public async Task Sparse_and_compressed_files_copy_and_move_with_identical_content()
    {
        Windows();
        var sparse = _s.P("src", "sparse.bin");
        Directory.CreateDirectory(_s.P("src"));
        using (var fs = new FileStream(sparse, FileMode.CreateNew))
        {
            fs.WriteByte(1);
        }

        Shell("fsutil", "sparse", "setflag", sparse);
        using (var fs = new FileStream(sparse, FileMode.Open))
        {
            fs.SetLength(64L << 20);
            fs.Position = (64L << 20) - 1;
            fs.WriteByte(9);
        }

        var compressed = _s.P("src", "compressed.bin");
        File.WriteAllBytes(compressed, Enumerable.Repeat((byte)'z', Big).ToArray());
        Shell("compact", "/c", compressed);
        File.GetAttributes(sparse).Should().HaveFlag(FileAttributes.SparseFile);
        File.GetAttributes(compressed).Should().HaveFlag(FileAttributes.Compressed);

        var dst = _s.Dir("dst");
        var copy = await RunAsync(new TransferOperation(TransferKind.Copy, [sparse, compressed], new TransferTarget(dst, null)));
        copy.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", copy.Errors));
        SameBytes(sparse, Path.Combine(dst, "sparse.bin")).Should().BeTrue();
        SameBytes(compressed, Path.Combine(dst, "compressed.bin")).Should().BeTrue();

        var moved = _s.Dir("moved");
        var move = await RunAsync(new TransferOperation(TransferKind.Move, [Path.Combine(dst, "sparse.bin"), Path.Combine(dst, "compressed.bin")], new TransferTarget(moved, null)));
        move.Outcome.Should().Be(OperationOutcome.Completed);
        SameBytes(sparse, Path.Combine(moved, "sparse.bin")).Should().BeTrue();
    }

    // ---------------------------------------------------------------- 8.3 short names

    [Fact]
    public async Task Short_name_paths_list_and_search()
    {
        Windows();
        _s.File("Long Folder Name Here/inner file.txt", "x");
        var shortPath = ShortName(_s.P("Long Folder Name Here"));
        Assert.SkipWhen(shortPath.EndsWith("Long Folder Name Here", StringComparison.Ordinal), "8.3 names are off on this volume");

        var listed = await new LocalFileSystem().ListAsync(shortPath, includeHidden: true, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        listed.Should().Contain(e => e.Name == "inner file.txt");

        var found = await FileSearch.RunAsync(new SearchCriteria(shortPath, Masks: "inner*"), null, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        found.Should().ContainSingle();
    }

    [Theory]
    [InlineData(TransferKind.Copy)]
    [InlineData(TransferKind.Move)]
    public async Task Overwriting_a_file_with_itself_through_its_short_name_keeps_its_content(TransferKind kind)
    {
        Windows();
        var file = _s.File("d/Long File Name.txt", "precious");
        var shortName = Path.GetFileName(ShortName(file));
        Assert.SkipWhen(shortName == "Long File Name.txt", "8.3 names are off on this volume");

        await RunAsync(new TransferOperation(kind, [file], new TransferTarget(_s.P("d"), shortName)), new AnswerAll(ConflictChoice.Overwrite), ErrorChoice.Skip);

        Directory.GetFiles(_s.P("d")).Should().ContainSingle();
        File.ReadAllText(Directory.GetFiles(_s.P("d")).Single()).Should().Be("precious");
    }

    [Fact]
    public async Task Copying_a_folder_into_itself_through_its_short_name_is_refused()
    {
        Windows();
        _s.File("Long Folder Name/f.txt", "x");
        var shortPath = ShortName(_s.P("Long Folder Name"));
        Assert.SkipWhen(shortPath.EndsWith("Long Folder Name", StringComparison.Ordinal), "8.3 names are off on this volume");

        var result = await RunAsync(new TransferOperation(TransferKind.Copy, [_s.P("Long Folder Name")], new TransferTarget(shortPath, "inside")), errors: ErrorChoice.Skip);

        result.Errors.Should().NotBeEmpty("a folder cannot be copied into itself, whatever spelling the target uses");
        Directory.Exists(_s.P("Long Folder Name", "inside")).Should().BeFalse();
    }

    // ---------------------------------------------------------------- drive roots, relative drives, forward slashes

    [Fact]
    public async Task Drive_roots_and_forward_slashes_normalize()
    {
        Windows();
        var drive = Path.GetPathRoot(_s.Root)!; // "C:\"
        ExactPath.Full(drive).Should().Be(drive);
        ExactPath.Full(drive.ToLowerInvariant()).Should().Be(drive);
        new LocalFileSystem().GetParent(drive).Should().BeNull();
        ExactPath.Full(drive[..2] + ".").Should().Be(Path.GetFullPath(drive[..2] + "."), "\"C:.\" is the current folder of drive C, like everywhere in Windows");

        var listed = await new LocalFileSystem().ListAsync(drive.Replace('\\', '/'), includeHidden: false, TestContext.Current.CancellationToken).Take(3).ToListAsync(TestContext.Current.CancellationToken);
        listed.Should().NotBeEmpty();

        _s.File("src/a.", "dot");
        _s.File("src/f.txt", "f");
        _s.Dir("dst");
        var slashed = _s.P("src").Replace('\\', '/');
        var target = TargetResolver.Resolve(_s.P("dst").Replace('\\', '/') + "/", _s.Root, 2);
        var copy = await RunAsync(new TransferOperation(TransferKind.Copy, [slashed + "/a.", slashed + "/f.txt"], target));

        copy.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", copy.Errors));
        WeirdBox.ReadExact(_s.P("dst", "a.")).Should().Be("dot");
        File.ReadAllText(_s.P("dst", "f.txt")).Should().Be("f");
    }

    [Fact]
    public async Task Copying_several_files_into_a_new_folder_fails_for_every_file()
    {
        Windows();
        var a = _s.File("src/a.txt", "a");
        var b = _s.File("src/b.txt", "b");
        var target = TargetResolver.Resolve(_s.P("new"), _s.Root, 2); // "with several sources it is a folder that will be created"

        var copy = await RunAsync(new TransferOperation(TransferKind.Copy, [a, b], target), errors: ErrorChoice.Skip);

        copy.Errors.Should().BeEmpty();
        File.ReadAllText(_s.P("new", "a.txt")).Should().Be("a");
    }

    // ---------------------------------------------------------------- Windows property system

    [Fact]
    public async Task File_details_survive_weird_paths()
    {
        Windows();
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _s.File("d/NUL", "x");
        _s.File("d/a.", "x");
        var ads = _s.File("d/s.txt", "x");
        File.WriteAllText(ads + ":stream", "s");
        Junction(_s.P("d", "loop"), _s.P("d"));
        var deep = _s.P("deep", new string('x', 200), new string('y', 200), "z.jpg");
        _s.File(deep[(_s.Root.Length + 1)..], "not a jpeg");

        string[] paths = [_s.P("d", "NUL"), @"\\.\NUL", _s.P("d", "a."), ads + ":stream", _s.P("d", "loop"), deep, _s.P("missing.jpg"), _s.P("d")];
        var details = new WindowsFileDetails();
        var read = Task.Run(() => paths.Select(p => OperatingSystem.IsWindows() ? details.Read(p) : FileDetails.None).ToList(), TestContext.Current.CancellationToken);

        var all = await read.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        all.Should().HaveCount(paths.Length);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<OperationResult> RunAsync(IOperation op, AnswerAll? prompts = null, ErrorChoice? errors = null)
    {
        prompts ??= new AnswerAll(null);
        prompts.Error = errors;
        return op.RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));
    }

    private string CaseSensitiveDir(string relative)
    {
        var dir = _s.Dir(relative);
        var (code, output) = Shell("fsutil", "file", "setCaseSensitiveInfo", dir, "enable");
        Assert.SkipUnless(code == 0, "case-sensitive folders not available: " + output);
        return dir;
    }

    private static void Junction(string link, string target)
    {
        var (code, output) = Shell("cmd.exe", "/c", "mklink", "/J", link, target);
        code.Should().Be(0, output);
    }

    private static string ShortName(string path)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c for %I in (\"{path}\") do @echo %~sI")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    private static (int Code, string Output) Shell(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static bool SameBytes(string a, string b)
    {
        using var x = File.OpenRead(a);
        using var y = File.OpenRead(b);
        if (x.Length != y.Length)
        {
            return false;
        }

        var bx = new byte[1 << 16];
        var by = new byte[1 << 16];
        int n;
        while ((n = x.Read(bx)) > 0)
        {
            y.ReadExactly(by.AsSpan(0, n));
            if (!bx.AsSpan(0, n).SequenceEqual(by.AsSpan(0, n)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Answers every conflict the same way (or fails on one when none is given) and every error with <see cref="Error"/>.</summary>
    private sealed class AnswerAll(ConflictChoice? conflict) : IOperationPrompts
    {
        public ErrorChoice? Error { get; set; }

        public int Conflicts { get; private set; }

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo info, CancellationToken cancellationToken)
        {
            Conflicts++;
            return Task.FromResult(conflict ?? throw new InvalidOperationException("Unexpected conflict: " + info.TargetPath));
        }

        public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken) =>
            Task.FromResult(Error ?? throw new InvalidOperationException("Unexpected error: " + exception.Message, exception));
    }

    private sealed class NoBin : IRecycleBin
    {
        public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => throw new InvalidOperationException("nothing should be recycled");
    }
}

/// <summary>A temp folder that can hold names Windows normally refuses, and still be removed afterwards.</summary>
internal sealed class WeirdBox : IDisposable
{
    public WeirdBox()
    {
        Root = Directory.CreateTempSubdirectory("pc-weird-").FullName;
    }

    public string Root { get; }

    /// <summary>The \\?\ form: nothing in the name is reinterpreted.</summary>
    public static string X(string path) => OperatingSystem.IsWindows() && !path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + path : path;

    public string P(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Dir(params string[] parts)
    {
        var path = P(parts);
        Directory.CreateDirectory(X(path));
        return path;
    }

    public string File(string relative, string content = "x")
    {
        var path = P(relative.Split('/'));
        Directory.CreateDirectory(X(path[..path.LastIndexOf('\\')]));
        System.IO.File.WriteAllText(X(path), content);
        return path;
    }

    public static string ReadExact(string path) => System.IO.File.ReadAllText(X(path));

    public static bool ExistsExact(string path) => System.IO.File.Exists(X(path)) || Directory.Exists(X(path));

    public void Dispose()
    {
        try
        {
            Unlock(new DirectoryInfo(X(Root)));
            Directory.Delete(X(Root), recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort: a temp folder left behind is not a test failure
        }
    }

    private static void Unlock(DirectoryInfo dir)
    {
        foreach (var child in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }).ToList())
        {
            if ((child.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
            {
                child.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            }

            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (child is DirectoryInfo)
                {
                    Directory.Delete(child.FullName); // the link only, never its target
                }

                continue;
            }

            if (child is DirectoryInfo sub)
            {
                Unlock(sub);
            }
        }
    }
}
