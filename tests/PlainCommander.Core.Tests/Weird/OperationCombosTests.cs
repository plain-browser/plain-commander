using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using PlainCommander.Core.Undo;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>File operations in odd combinations: self-targets, links, races, mixed answers, undo after the fact.</summary>
public sealed class OperationCombosTests : IDisposable
{
    private const int Mb = 1 << 20;
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Sandbox _s = new();
    private readonly DeletingBin _bin = new();

    public void Dispose() => _s.Dispose();

    private static TransferOperation Copy(TransferTarget target, params string[] sources) => new(TransferKind.Copy, sources, target);

    private static TransferOperation Move(TransferTarget target, params string[] sources) => new(TransferKind.Move, sources, target);

    private static TransferOperation CopySeq(TransferTarget target, params string[] sources) => new(TransferKind.Copy, sources, target, parallelism: 1);

    private static TransferOperation MoveSeq(TransferTarget target, params string[] sources) => new(TransferKind.Move, sources, target, parallelism: 1);

    private TransferTarget Into(params string[] dir) => new(_s.Dir(dir), null);

    private static Task<OperationResult> Go(IOperation op, IOperationPrompts? prompts = null, IProgress<OperationProgress>? progress = null, CancellationToken token = default) =>
        op.RunAsync(new OperationContext(progress, prompts ?? new HookPrompts(), new PauseGate(), token, false))
            .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

    private Task<OperationResult> UndoAsync(IUndoable op, IOperationPrompts? prompts = null) =>
        Go(new UndoOperation(new UndoEntry("step", op.Journal.Actions, DateTime.Now), _bin, null), prompts);

    private static bool LinksWork()
    {
        var probe = Path.Combine(Path.GetTempPath(), "pc-link-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.CreateSymbolicLink(probe, Path.GetTempPath());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- self targets -------------------------------------------------------------------------------------

    [Fact]
    public async Task Copying_a_folder_into_itself_is_refused_and_creates_nothing()
    {
        _s.File("d/f.txt", "1");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        var result = await Go(Copy(new TransferTarget(_s.P("d"), null), _s.P("d")), prompts);

        result.Skipped.Should().Be(1);
        prompts.Errors.Single().Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.IntoItself);
        Directory.EnumerateFileSystemEntries(_s.P("d")).Should().ContainSingle();
    }

    [Fact]
    public async Task Copying_a_folder_into_its_own_subfolder_is_refused()
    {
        _s.File("d/sub/f.txt", "1");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        await Go(Copy(new TransferTarget(_s.P("d", "sub"), null), _s.P("d")), prompts);

        prompts.Errors.Single().Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.IntoItself);
        Directory.Exists(_s.P("d", "sub", "d")).Should().BeFalse();
    }

    [Fact]
    public async Task Moving_a_folder_into_its_own_subfolder_is_refused_and_keeps_everything()
    {
        _s.File("d/sub/f.txt", "1");
        _s.File("d/g.txt", "2");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        await Go(Move(new TransferTarget(_s.P("d", "sub"), null), _s.P("d")), prompts);

        prompts.Errors.Should().ContainSingle();
        _s.Read("d/sub/f.txt").Should().Be("1");
        _s.Read("d/g.txt").Should().Be("2");
    }

    [Fact]
    public async Task Moving_a_folder_into_an_existing_folder_of_the_same_name_inside_itself_is_refused()
    {
        _s.File("d/f.txt", "1");
        _s.Dir("d", "d"); // the target d/d exists, so this is not a plain rename
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        await Go(Move(new TransferTarget(_s.P("d"), null), _s.P("d")), prompts);

        prompts.Errors.Single().Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.IntoItself);
        _s.Read("d/f.txt").Should().Be("1");
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    [InlineData(false, 5 * Mb)]
    [InlineData(true, 5 * Mb)]
    public async Task A_file_copied_or_moved_onto_itself_is_refused_and_untouched(bool move, int size)
    {
        var data = Bytes.Pattern(size);
        var f = _s.P("a", "f.bin");
        _s.Dir("a");
        await File.WriteAllBytesAsync(f, data, TestContext.Current.CancellationToken);
        var prompts = new HookPrompts(conflict: _ => ConflictChoice.Overwrite, error: (_, _) => ErrorChoice.Skip);
        var target = new TransferTarget(_s.P("a"), null);

        var result = await Go(move ? Move(target, f) : Copy(target, f), prompts);

        result.Skipped.Should().Be(1);
        prompts.Errors.Single().Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.SameFile);
        (await File.ReadAllBytesAsync(f, TestContext.Current.CancellationToken)).Should().Equal(data);
    }

    [Fact]
    public async Task Case_only_rename_by_move_keeps_the_content()
    {
        _s.File("a/a.txt", "content");

        var result = await Go(Move(new TransferTarget(_s.P("a"), "A.txt"), _s.P("a", "a.txt")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.GetFiles(_s.P("a")).Select(Path.GetFileName).Should().Equal("A.txt");
        _s.Read("a/A.txt").Should().Be("content");
    }

    [Fact]
    public void Case_only_rename_with_rename_command_keeps_the_content()
    {
        _s.File("a/a.txt", "content");
        _s.File("a/dir/x.txt", "x");

        SimpleCommands.Rename(_s.P("a", "a.txt"), "A.txt");
        SimpleCommands.Rename(_s.P("a", "dir"), "DIR");

        Directory.GetFileSystemEntries(_s.P("a")).Select(Path.GetFileName).Should().BeEquivalentTo(["A.txt", "DIR"]);
        _s.Read("a/A.txt").Should().Be("content");
        _s.Read("a/DIR/x.txt").Should().Be("x");
    }

    [Fact]
    public void Rename_onto_an_existing_other_file_is_refused()
    {
        _s.File("a/a.txt", "a");
        _s.File("a/b.txt", "b");

        var act = () => SimpleCommands.Rename(_s.P("a", "a.txt"), "b.txt");

        act.Should().Throw<OperationRuleException>();
        _s.Read("a/a.txt").Should().Be("a");
        _s.Read("a/b.txt").Should().Be("b");
    }

    // ---- file vs folder -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_meeting_a_folder_of_its_name_is_a_type_mismatch_and_both_survive(bool move)
    {
        _s.File("a/x", "file");
        _s.File("b/x/inner.txt", "inner");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);
        var target = new TransferTarget(_s.P("b"), null);

        await Go(move ? MoveSeq(target, _s.P("a", "x")) : CopySeq(target, _s.P("a", "x")), prompts);

        prompts.Errors.Single().Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.TypeMismatch);
        _s.Read("a/x").Should().Be("file");
        _s.Read("b/x/inner.txt").Should().Be("inner");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_folder_meeting_a_file_of_its_name_is_a_type_mismatch_and_both_survive(bool move)
    {
        _s.File("a/x/inner.txt", "inner");
        _s.File("b/x", "file");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);
        var target = new TransferTarget(_s.P("b"), null);

        var result = await Go(move ? MoveSeq(target, _s.P("a", "x")) : CopySeq(target, _s.P("a", "x")), prompts);

        result.Outcome.Should().NotBe(OperationOutcome.Completed);
        prompts.Errors.First().Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.TypeMismatch);
        _s.Read("a/x/inner.txt").Should().Be("inner");
        _s.Read("b/x").Should().Be("file");
    }

    // ---- conflicts ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_single_conflict_answer_in_one_run_does_what_it_says()
    {
        string[] names = ["1", "2", "3", "4"];
        foreach (var n in names)
        {
            _s.File($"a/{n}.txt", "new" + n, New);
            _s.File($"b/{n}.txt", "old" + n, Old);
        }

        var answers = new Queue<ConflictChoice>([ConflictChoice.Overwrite, ConflictChoice.Skip, ConflictChoice.KeepBoth, ConflictChoice.Skip]);
        var prompts = new HookPrompts(conflict: _ => answers.Dequeue());
        var sources = names.Select(n => _s.P("a", n + ".txt")).ToArray();

        var result = await Go(CopySeq(new TransferTarget(_s.P("b"), null), sources), prompts);

        result.Done.Should().Be(2);
        result.Skipped.Should().Be(2);
        _s.Read("b/1.txt").Should().Be("new1");
        _s.Read("b/2.txt").Should().Be("old2");
        _s.Read("b/3.txt").Should().Be("old3");
        _s.Read("b/3 (2).txt").Should().Be("new3");
        _s.Read("b/4.txt").Should().Be("old4");
    }

    [Theory]
    [InlineData(ConflictChoice.SkipAll)]
    [InlineData(ConflictChoice.OverwriteAll)]
    [InlineData(ConflictChoice.KeepBothAll)]
    [InlineData(ConflictChoice.OverwriteAllOlder)]
    public async Task An_all_answer_after_a_single_answer_covers_the_rest_without_asking(ConflictChoice all)
    {
        for (var i = 0; i < 20; i++)
        {
            // even: target older; odd: target newer
            _s.File($"a/t/{i:00}.txt", "new" + i, i % 2 == 0 ? New : Old);
            _s.File($"b/t/{i:00}.txt", "old" + i, i % 2 == 0 ? Old : New);
        }

        var asked = 0;
        var prompts = new HookPrompts(conflict: _ => Interlocked.Increment(ref asked) == 1 ? ConflictChoice.Skip : all);

        await Go(Copy(new TransferTarget(_s.P("b"), null), _s.P("a", "t")), prompts); // parallel workers

        asked.Should().Be(2, "after an \"… all\" nobody asks again");
        var skippedFirst = prompts.Conflicts[0].TargetPath;
        File.ReadAllText(skippedFirst).Should().StartWith("old");
        foreach (var i in Enumerable.Range(0, 20))
        {
            var target = _s.P("b", "t", $"{i:00}.txt");
            if (target == skippedFirst)
            {
                continue;
            }

            var expectNew = all switch
            {
                ConflictChoice.OverwriteAll => true,
                ConflictChoice.OverwriteAllOlder => i % 2 == 0,
                _ => false,
            };
            File.ReadAllText(target).Should().Be(expectNew ? "new" + i : "old" + i, target);
        }

        Directory.GetFiles(_s.P("b", "t")).Length.Should().Be(all == ConflictChoice.KeepBothAll ? 39 : 20);
    }

    [Fact]
    public async Task Cancel_at_the_second_conflict_of_a_move_loses_nothing()
    {
        for (var i = 0; i < 6; i++)
        {
            _s.File($"a/t/{i}.txt", "new" + i);
            _s.File($"b/t/{i}.txt", "old" + i);
        }

        _s.File("a/t/fresh.txt", "fresh");
        var asked = 0;
        var prompts = new HookPrompts(conflict: _ => ++asked == 1 ? ConflictChoice.Overwrite : ConflictChoice.Cancel);

        var result = await Go(MoveSeq(new TransferTarget(_s.P("b"), null), _s.P("a", "t")), prompts);

        result.Outcome.Should().Be(OperationOutcome.Aborted);
        for (var i = 0; i < 6; i++)
        {
            var inSource = File.Exists(_s.P("a", "t", $"{i}.txt"));
            var target = _s.Read($"b/t/{i}.txt");
            (inSource ? target == "old" + i : target == "new" + i).Should().BeTrue($"file {i} is in one place or the other");
        }

        (File.Exists(_s.P("a", "t", "fresh.txt")) || File.Exists(_s.P("b", "t", "fresh.txt"))).Should().BeTrue();
    }

    // ---- cancel ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_at_the_first_progress_report_leaves_every_source(bool move)
    {
        for (var i = 0; i < 50; i++)
        {
            _s.File($"a/t/s{i % 5}/{i}.txt", "x" + i);
        }

        _s.Dir("b", "t"); // an existing folder: the move goes item by item
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(_ => cts.Cancel());
        var target = new TransferTarget(_s.P("b"), null);

        var result = await Go(move ? Move(target, _s.P("a", "t")) : Copy(target, _s.P("a", "t")), progress: progress, token: cts.Token);

        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        for (var i = 0; i < 50; i++)
        {
            var rel = Path.Combine("t", $"s{i % 5}", $"{i}.txt");
            File.Exists(_s.P("a", rel)).Should().BeTrue(rel);
        }
    }

    [Fact]
    public async Task Cancel_mid_tree_of_a_move_leaves_each_file_in_exactly_one_place()
    {
        for (var i = 0; i < 300; i++)
        {
            _s.File($"a/t/s{i % 7}/{i}.txt", "x" + i);
        }

        _s.File("b/t/s3/3.txt", "old"); // the conflict is where we pull the plug
        using var cts = new CancellationTokenSource();
        var prompts = new HookPrompts(conflict: _ =>
        {
            cts.Cancel();
            return ConflictChoice.Overwrite;
        });

        var result = await Go(Move(new TransferTarget(_s.P("b"), null), _s.P("a", "t")), prompts, token: cts.Token);

        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        for (var i = 0; i < 300; i++)
        {
            var rel = Path.Combine("t", $"s{i % 7}", $"{i}.txt");
            var here = File.Exists(_s.P("a", rel));
            var there = File.Exists(_s.P("b", rel)) && File.ReadAllText(_s.P("b", rel)) == "x" + i;
            (here ^ there).Should().BeTrue(rel);
        }
    }

    // ---- sizes ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Mb - 1)]
    [InlineData(Mb)]
    [InlineData(Mb + 1)]
    [InlineData(2 * Mb)]
    [InlineData((4 * Mb) - 1)]
    [InlineData(4 * Mb)]
    [InlineData((4 * Mb) + 1)]
    [InlineData(5 * Mb)]
    public async Task Sizes_around_the_small_file_limit_and_buffer_copy_exactly(int size)
    {
        var data = Bytes.Pattern(size, size);
        _s.Dir("a");
        await File.WriteAllBytesAsync(_s.P("a", "f.bin"), data, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(_s.P("a", "f.bin"), Old);
        var progress = new InlineProgress(_ => { });

        var result = await Go(Copy(Into("b"), _s.P("a", "f.bin")), progress: progress);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        (await File.ReadAllBytesAsync(_s.P("b", "f.bin"), TestContext.Current.CancellationToken)).Should().Equal(data);
        File.GetLastWriteTimeUtc(_s.P("b", "f.bin")).Should().Be(Old);
        progress.Reports[^1].TotalBytesDone.Should().Be(size);
    }

    [Fact]
    public async Task Zero_byte_files_and_empty_folders_survive_a_move_merge()
    {
        _s.Dir("a", "t", "empty", "deeper");
        _s.File("a/t/zero.txt", string.Empty);
        _s.Dir("b", "t");

        var result = await Go(Move(new TransferTarget(_s.P("b"), null), _s.P("a", "t")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("b", "t", "empty", "deeper")).Should().BeTrue();
        new FileInfo(_s.P("b", "t", "zero.txt")).Length.Should().Be(0);
        Directory.Exists(_s.P("a", "t")).Should().BeFalse();
    }

    // ---- links ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_link_to_a_file_is_copied_as_its_content_and_moved_as_a_link()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("real/f.txt", "real");
        _s.Dir("a");
        File.CreateSymbolicLink(_s.P("a", "link.txt"), _s.P("real", "f.txt"));

        await Go(Copy(Into("c"), _s.P("a", "link.txt")));
        await Go(Move(Into("m"), _s.P("a", "link.txt")));

        _s.Read("c/link.txt").Should().Be("real");
        new FileInfo(_s.P("c", "link.txt")).LinkTarget.Should().BeNull("a copy is a real file");
        new FileInfo(_s.P("m", "link.txt")).LinkTarget.Should().NotBeNull("a move keeps the link");
        _s.Read("real/f.txt").Should().Be("real");
    }

    [Fact]
    public async Task A_dangling_link_in_a_copied_tree_is_one_error_and_the_rest_arrives()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("a/t/ok.txt", "ok");
        File.CreateSymbolicLink(_s.P("a", "t", "dangling.txt"), _s.P("nowhere.txt"));
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        var result = await Go(CopySeq(Into("b"), _s.P("a", "t")), prompts);

        result.Skipped.Should().Be(1);
        _s.Read("b/t/ok.txt").Should().Be("ok");
    }

    [Fact]
    public async Task A_dangling_link_moves_as_a_link()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.Dir("a", "t");
        File.CreateSymbolicLink(_s.P("a", "t", "dangling.txt"), _s.P("nowhere.txt"));
        _s.Dir("b", "t");

        var result = await Go(Move(new TransferTarget(_s.P("b"), null), _s.P("a", "t")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        new FileInfo(_s.P("b", "t", "dangling.txt")).LinkTarget.Should().Be(_s.P("nowhere.txt"));
    }

    [Fact]
    public async Task Mutual_link_loop_between_two_folders_ends()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("a/x/f.txt", "1");
        _s.File("a/y/g.txt", "2");
        Directory.CreateSymbolicLink(_s.P("a", "x", "toy"), _s.P("a", "y"));
        Directory.CreateSymbolicLink(_s.P("a", "y", "tox"), _s.P("a", "x"));

        var result = await Go(Copy(Into("b"), _s.P("a", "x")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/x/f.txt").Should().Be("1");
        _s.Read("b/x/toy/g.txt").Should().Be("2");
    }

    [Fact]
    public async Task Regression_copy_leaves_a_real_folder_empty_when_a_link_to_it_was_walked_first()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("a/t/real/data.txt", "data");
        // Enumeration order is up to the file system; with several names one of the links is bound to come first.
        foreach (var name in new[] { "0link", "alink", "link", "zlink", "~link" })
        {
            Directory.CreateSymbolicLink(_s.P("a", "t", name), _s.P("a", "t", "real"));
        }

        var result = await Go(Copy(Into("b"), _s.P("a", "t")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/t/real/data.txt").Should().Be("data", "the real folder must never come out empty");
    }

    [Fact]
    public async Task Regression_move_drops_a_link_to_a_folder_when_the_target_folder_exists()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("elsewhere/data.txt", "data");
        _s.Dir("a");
        Directory.CreateSymbolicLink(_s.P("a", "link"), _s.P("elsewhere"));
        _s.Dir("b", "link");

        var result = await Go(Move(new TransferTarget(_s.P("b"), null), _s.P("a", "link")), new HookPrompts(error: (_, _) => ErrorChoice.Skip));

        _s.Read("elsewhere/data.txt").Should().Be("data");
        var stillThere = Directory.Exists(_s.P("a", "link")) || new DirectoryInfo(_s.P("b", "link")).LinkTarget is not null;
        stillThere.Should().BeTrue($"the link may be refused or moved, never just dropped ({result.Outcome})");
    }

    [Fact]
    public async Task Regression_overwriting_a_big_file_with_itself_through_a_link_deletes_it()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        var big = Bytes.Pattern(5 * Mb, 2);
        _s.Dir("a");
        await File.WriteAllBytesAsync(_s.P("a", "big.bin"), big, TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(_s.P("alias"), _s.P("a")); // alias/big.bin is a/big.bin
        var prompts = new HookPrompts(conflict: _ => ConflictChoice.Overwrite, error: (_, _) => ErrorChoice.Skip);

        // The target cannot be opened (sharing violation: it is the open source); the clean-up then deletes "the partial copy".
        await Go(CopySeq(new TransferTarget(_s.P("alias"), null), _s.P("a", "big.bin")), prompts);

        File.Exists(_s.P("a", "big.bin")).Should().BeTrue("a copy that never started must not delete what is at the target");
    }

    [Fact]
    public async Task Overwriting_a_small_file_with_itself_through_a_link_keeps_it()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        var small = Bytes.Pattern(100, 1);
        _s.Dir("a");
        await File.WriteAllBytesAsync(_s.P("a", "small.bin"), small, TestContext.Current.CancellationToken);
        Directory.CreateSymbolicLink(_s.P("alias"), _s.P("a"));
        var prompts = new HookPrompts(conflict: _ => ConflictChoice.Overwrite, error: (_, _) => ErrorChoice.Skip);

        await Go(CopySeq(new TransferTarget(_s.P("alias"), null), _s.P("a", "small.bin")), prompts);

        (await File.ReadAllBytesAsync(_s.P("a", "small.bin"), TestContext.Current.CancellationToken)).Should().Equal(small);
    }

    [Fact]
    public async Task Regression_skip_identical_move_onto_itself_through_a_link_deletes_the_only_copy()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("a/f.txt", "only copy");
        Directory.CreateSymbolicLink(_s.P("alias"), _s.P("a"));
        var prompts = new HookPrompts(conflict: _ => ConflictChoice.Skip, error: (_, _) => ErrorChoice.Skip);

        // skipIdentical is what the elevated helper uses when a move continues as administrator.
        await Go(new TransferOperation(TransferKind.Move, [_s.P("a", "f.txt")], new TransferTarget(_s.P("alias"), null), 1, skipIdentical: true), prompts);

        File.Exists(_s.P("a", "f.txt")).Should().BeTrue("the \"identical\" target is the source itself");
    }

    [Fact]
    public async Task Regression_keep_both_leaves_an_empty_placeholder_when_the_copy_fails()
    {
        _s.File("a/f.txt", "new");
        _s.File("b/f.txt", "old");
        var prompts = new HookPrompts(
            conflict: _ =>
            {
                File.Delete(_s.P("a", "f.txt")); // the source goes away between the question and the copy
                return ConflictChoice.KeepBoth;
            },
            error: (_, _) => ErrorChoice.Skip);

        var result = await Go(CopySeq(Into("b"), _s.P("a", "f.txt")), prompts);

        result.Skipped.Should().Be(1);
        File.Exists(_s.P("b", "f (2).txt")).Should().BeFalse("an empty \"f (2).txt\" looks like a copy that worked");
    }

    [Fact]
    public async Task Overwriting_a_hard_link_of_the_source_keeps_the_content()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses ln");
        var big = Bytes.Pattern(5 * Mb, 3);
        _s.Dir("a");
        _s.Dir("b");
        await File.WriteAllBytesAsync(_s.P("a", "big.bin"), big, TestContext.Current.CancellationToken);
        using (var ln = System.Diagnostics.Process.Start("ln", [_s.P("a", "big.bin"), _s.P("b", "big.bin")]))
        {
            await ln.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var prompts = new HookPrompts(conflict: _ => ConflictChoice.Overwrite, error: (_, _) => ErrorChoice.Skip);

        await Go(CopySeq(new TransferTarget(_s.P("b"), null), _s.P("a", "big.bin")), prompts);

        (await File.ReadAllBytesAsync(_s.P("a", "big.bin"), TestContext.Current.CancellationToken)).Should().Equal(big);
    }

    [Fact]
    public async Task Moving_a_file_onto_itself_through_a_linked_folder_keeps_the_file()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("a/f.txt", "keep me");
        Directory.CreateSymbolicLink(_s.P("alias"), _s.P("a"));
        var prompts = new HookPrompts(conflict: _ => ConflictChoice.Overwrite, error: (_, _) => ErrorChoice.Skip);

        await Go(Move(new TransferTarget(_s.P("alias"), null), _s.P("a", "f.txt")), prompts);

        _s.Read("a/f.txt").Should().Be("keep me");
    }

    [Fact]
    public async Task Delete_of_a_tree_with_a_link_to_a_folder_leaves_the_target_alone()
    {
        Assert.SkipUnless(LinksWork(), "symbolic links need developer mode or admin");
        _s.File("precious/data.txt", "data");
        _s.File("a/t/f.txt", "f");
        Directory.CreateSymbolicLink(_s.P("a", "t", "link"), _s.P("precious"));
        File.CreateSymbolicLink(_s.P("a", "t", "flink"), _s.P("precious", "data.txt"));

        var result = await Go(new DeleteOperation([_s.P("a", "t")]));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("a", "t")).Should().BeFalse();
        _s.Read("precious/data.txt").Should().Be("data");
    }

    // ---- read-only ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(10)]
    [InlineData(5 * Mb)]
    public async Task A_read_only_file_copies_and_moves(int size)
    {
        var data = Bytes.Pattern(size);
        _s.Dir("a");
        await File.WriteAllBytesAsync(_s.P("a", "ro.bin"), data, TestContext.Current.CancellationToken);
        File.SetAttributes(_s.P("a", "ro.bin"), FileAttributes.ReadOnly);

        (await Go(Copy(Into("c"), _s.P("a", "ro.bin")))).Outcome.Should().Be(OperationOutcome.Completed);
        (await Go(Move(Into("m"), _s.P("a", "ro.bin")))).Outcome.Should().Be(OperationOutcome.Completed);

        (await File.ReadAllBytesAsync(_s.P("c", "ro.bin"), TestContext.Current.CancellationToken)).Should().Equal(data);
        (await File.ReadAllBytesAsync(_s.P("m", "ro.bin"), TestContext.Current.CancellationToken)).Should().Equal(data);
        File.Exists(_s.P("a", "ro.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task A_hidden_dot_file_above_the_small_file_limit_copies()
    {
        var data = Bytes.Pattern(5 * Mb);
        _s.Dir("a");
        await File.WriteAllBytesAsync(_s.P("a", ".hidden.bin"), data, TestContext.Current.CancellationToken);

        var result = await Go(Copy(Into("b"), _s.P("a", ".hidden.bin")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        (await File.ReadAllBytesAsync(_s.P("b", ".hidden.bin"), TestContext.Current.CancellationToken)).Should().Equal(data);
    }

    [Fact]
    public async Task An_unreadable_subfolder_is_kept_by_a_move_and_the_rest_moves()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions");
            return;
        }

        _s.File("a/t/ok.txt", "ok");
        _s.File("a/t/locked/secret.txt", "secret");
        File.SetUnixFileMode(_s.P("a", "t", "locked"), UnixFileMode.None);
        _s.Dir("b", "t");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);
        try
        {
            await Go(Move(new TransferTarget(_s.P("b"), null), _s.P("a", "t")), prompts);
        }
        finally
        {
            File.SetUnixFileMode(_s.P("a", "t", "locked"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _s.Read("b/t/ok.txt").Should().Be("ok");
        _s.Read("a/t/locked/secret.txt").Should().Be("secret");
    }

    // ---- races ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_that_vanishes_after_the_scan_is_one_error_and_the_rest_arrives(bool move)
    {
        _s.File("a/0.txt", "trigger");
        _s.File("a/t/2.txt", "two");
        _s.File("a/t/3.txt", "three");
        _s.File("b/0.txt", "old");
        var prompts = new HookPrompts(
            conflict: _ =>
            {
                File.Delete(_s.P("a", "t", "2.txt"));
                return ConflictChoice.Overwrite;
            },
            error: (_, _) => ErrorChoice.Skip);
        _s.Dir("b", "t"); // merge, so the folder goes item by item
        var target = new TransferTarget(_s.P("b"), null);
        string[] sources = [_s.P("a", "0.txt"), _s.P("a", "t")];

        // Every source is scanned before anything is transferred, so 2.txt is in the plan when the prompt deletes it.
        await Go(move ? MoveSeq(target, sources) : CopySeq(target, sources), prompts);

        _s.Read("b/0.txt").Should().Be("trigger");
        _s.Read("b/t/3.txt").Should().Be("three");
        prompts.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task A_small_file_that_grows_past_the_limit_after_the_scan_is_copied_whole()
    {
        _s.File("a/t/0.txt", "trigger");
        _s.File("a/t/grow.bin", "small");
        _s.File("b/t/0.txt", "old");
        var big = Bytes.Pattern(6 * Mb);
        var prompts = new HookPrompts(conflict: _ =>
        {
            File.WriteAllBytes(_s.P("a", "t", "grow.bin"), big);
            return ConflictChoice.Overwrite;
        });

        await Go(CopySeq(new TransferTarget(_s.P("b", "t"), null), _s.P("a", "t", "0.txt"), _s.P("a", "t", "grow.bin")), prompts);

        (await File.ReadAllBytesAsync(_s.P("b", "t", "grow.bin"), TestContext.Current.CancellationToken)).Should().Equal(big);
    }

    [Fact]
    public async Task A_big_file_that_shrinks_to_nothing_after_the_scan_is_copied_empty()
    {
        _s.File("a/t/0.txt", "trigger");
        await File.WriteAllBytesAsync(_s.P("a", "t", "shrink.bin"), Bytes.Pattern(6 * Mb), TestContext.Current.CancellationToken);
        _s.File("b/t/0.txt", "old");
        var prompts = new HookPrompts(conflict: _ =>
        {
            File.WriteAllBytes(_s.P("a", "t", "shrink.bin"), []);
            return ConflictChoice.Overwrite;
        });

        var result = await Go(CopySeq(new TransferTarget(_s.P("b", "t"), null), _s.P("a", "t", "0.txt"), _s.P("a", "t", "shrink.bin")), prompts);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        new FileInfo(_s.P("b", "t", "shrink.bin")).Length.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_file_deleted_after_the_scan_is_one_error_and_a_move_keeps_its_folder(bool move)
    {
        _s.File("a/t/0.txt", "trigger");
        _s.File("a/t/vanish.txt", "v");
        _s.File("a/t/stay.txt", "s");
        _s.File("b/t/0.txt", "old");
        var prompts = new HookPrompts(
            conflict: c =>
            {
                File.Delete(_s.P("a", "t", "vanish.txt"));
                return ConflictChoice.Overwrite;
            },
            error: (_, _) => ErrorChoice.Skip);
        var target = new TransferTarget(_s.P("b"), null);

        var result = await Go(move ? MoveSeq(target, _s.P("a", "t")) : CopySeq(target, _s.P("a", "t")), prompts);

        _s.Read("b/t/stay.txt").Should().Be("s");
        _s.Read("b/t/0.txt").Should().Be("trigger");
        // Enumeration order decides whether vanish.txt came before the trigger; either way nothing is lost.
        if (prompts.Errors.Count > 0)
        {
            result.Skipped.Should().Be(1);
        }
    }

    // ---- selection shapes -----------------------------------------------------------------------------------

    [Fact]
    public async Task Copying_a_folder_and_a_file_inside_it_gives_both()
    {
        _s.File("a/d/f.txt", "f");

        var result = await Go(Copy(Into("b"), _s.P("a", "d"), _s.P("a", "d", "f.txt")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/d/f.txt").Should().Be("f");
        _s.Read("b/f.txt").Should().Be("f");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Moving_a_folder_and_a_file_inside_it_loses_nothing(bool fileFirst)
    {
        _s.File("a/d/f.txt", "f");
        _s.File("a/d/g.txt", "g");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);
        string[] sources = fileFirst ? [_s.P("a", "d", "f.txt"), _s.P("a", "d")] : [_s.P("a", "d"), _s.P("a", "d", "f.txt")];

        await Go(Move(Into("b"), sources), prompts);

        (File.Exists(_s.P("b", "f.txt")) || File.Exists(_s.P("b", "d", "f.txt"))).Should().BeTrue();
        _s.Read("b/d/g.txt").Should().Be("g");
        Directory.Exists(_s.P("a", "d")).Should().BeFalse();
    }

    [Fact]
    public async Task A_duplicated_source_in_a_copy_is_a_conflict_with_itself()
    {
        _s.File("a/f.txt", "f");
        var prompts = new HookPrompts(conflict: _ => ConflictChoice.Overwrite);

        var result = await Go(Copy(Into("b"), _s.P("a", "f.txt"), _s.P("a", "f.txt")), prompts);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        prompts.Conflicts.Should().ContainSingle();
        _s.Read("b/f.txt").Should().Be("f");
    }

    [Fact]
    public async Task A_duplicated_source_in_a_move_is_one_error_and_the_file_arrives()
    {
        _s.File("a/f.txt", "f");
        _s.File("a/d/g.txt", "g");
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip, conflict: _ => ConflictChoice.Skip);

        await Go(Move(Into("b"), _s.P("a", "f.txt"), _s.P("a", "f.txt"), _s.P("a", "d"), _s.P("a", "d")), prompts);

        _s.Read("b/f.txt").Should().Be("f");
        _s.Read("b/d/g.txt").Should().Be("g");
    }

    [Fact]
    public async Task Many_small_files_copy_then_move_completely()
    {
        for (var i = 0; i < 3000; i++)
        {
            _s.File($"a/t/{i % 30}/{i}.txt", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var copy = await Go(Copy(Into("c"), _s.P("a", "t")));
        _s.Dir("m", "t");
        var move = await Go(Move(new TransferTarget(_s.P("m"), null), _s.P("a", "t")));

        copy.Done.Should().Be(3031);
        move.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("a", "t")).Should().BeFalse();
        Directory.GetFiles(_s.P("m", "t"), "*", SearchOption.AllDirectories).Length.Should().Be(3000);
        Directory.GetFiles(_s.P("c", "t"), "*", SearchOption.AllDirectories).Length.Should().Be(3000);
    }

    // ---- undo -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Undoing_a_move_after_the_file_was_changed_brings_back_the_changed_file()
    {
        _s.File("a/f.txt", "v1");
        var move = Move(Into("b"), _s.P("a", "f.txt"));
        await Go(move);
        File.WriteAllText(_s.P("b", "f.txt"), "v2");

        (await UndoAsync(move)).Outcome.Should().Be(OperationOutcome.Completed);

        _s.Read("a/f.txt").Should().Be("v2");
    }

    [Fact]
    public async Task Undoing_a_move_after_the_file_was_deleted_or_renamed_reports_it_and_does_the_rest()
    {
        _s.File("a/gone.txt", "1");
        _s.File("a/renamed.txt", "2");
        _s.File("a/fine.txt", "3");
        var move = MoveSeq(Into("b"), _s.P("a", "gone.txt"), _s.P("a", "renamed.txt"), _s.P("a", "fine.txt"));
        await Go(move);
        File.Delete(_s.P("b", "gone.txt"));
        File.Move(_s.P("b", "renamed.txt"), _s.P("b", "other.txt"));
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        var result = await UndoAsync(move, prompts);

        result.Skipped.Should().Be(2);
        prompts.Errors.Should().AllSatisfy(e => e.Should().BeOfType<UndoException>().Which.Problem.Should().Be(UndoProblem.Gone));
        _s.Read("a/fine.txt").Should().Be("3");
        _s.Read("b/other.txt").Should().Be("2");
    }

    [Fact]
    public async Task Undoing_the_same_move_twice_changes_nothing_the_second_time()
    {
        _s.File("a/t/f.txt", "1");
        _s.Dir("b", "t");
        var move = Move(new TransferTarget(_s.P("b"), null), _s.P("a", "t"));
        await Go(move);
        await UndoAsync(move);
        _s.Read("a/t/f.txt").Should().Be("1");

        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);
        await UndoAsync(move, prompts);

        _s.Read("a/t/f.txt").Should().Be("1");
        Directory.Exists(_s.P("b", "t")).Should().BeTrue("the folder that was there before stays");
        _bin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Undoing_a_copy_whose_result_was_overwritten_since_only_uses_the_bin()
    {
        _s.File("a/f.txt", "copy");
        var copy = Copy(Into("b"), _s.P("a", "f.txt"));
        await Go(copy);
        File.WriteAllText(_s.P("b", "f.txt"), "user's later work");

        await UndoAsync(copy);

        _bin.Recycled.Should().Equal(_s.P("b", "f.txt"));
        _s.Read("a/f.txt").Should().Be("copy");
    }

    [Fact]
    public async Task Undoing_a_rename_chain_newest_first_restores_the_first_name()
    {
        _s.File("a/A.txt", "content");
        var first = Move(new TransferTarget(_s.P("a"), "B.txt"), _s.P("a", "A.txt"));
        await Go(first);
        var second = Move(new TransferTarget(_s.P("a"), "C.txt"), _s.P("a", "B.txt"));
        await Go(second);

        await UndoAsync(second);
        await UndoAsync(first);

        Directory.GetFiles(_s.P("a")).Select(Path.GetFileName).Should().Equal("A.txt");
        _s.Read("a/A.txt").Should().Be("content");
    }

    [Fact]
    public async Task Undoing_the_older_rename_of_a_chain_first_is_refused_without_harm()
    {
        _s.File("a/A.txt", "content");
        var first = Move(new TransferTarget(_s.P("a"), "B.txt"), _s.P("a", "A.txt"));
        await Go(first);
        await Go(Move(new TransferTarget(_s.P("a"), "C.txt"), _s.P("a", "B.txt")));
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        await UndoAsync(first, prompts);

        prompts.Errors.Single().Should().BeOfType<UndoException>().Which.Problem.Should().Be(UndoProblem.Gone);
        _s.Read("a/C.txt").Should().Be("content");
    }

    [Fact]
    public async Task Undoing_a_swap_through_a_temporary_name_restores_both()
    {
        _s.File("a/A.txt", "a");
        _s.File("a/B.txt", "b");
        var steps = new[]
        {
            Move(new TransferTarget(_s.P("a"), "tmp"), _s.P("a", "A.txt")),
            Move(new TransferTarget(_s.P("a"), "A.txt"), _s.P("a", "B.txt")),
            Move(new TransferTarget(_s.P("a"), "B.txt"), _s.P("a", "tmp")),
        };
        foreach (var step in steps)
        {
            await Go(step);
        }

        _s.Read("a/A.txt").Should().Be("b");

        foreach (var step in steps.Reverse())
        {
            (await UndoAsync(step)).Outcome.Should().Be(OperationOutcome.Completed);
        }

        _s.Read("a/A.txt").Should().Be("a");
        _s.Read("a/B.txt").Should().Be("b");
    }

    [Fact]
    public async Task Undoing_a_swap_out_of_order_never_overwrites()
    {
        _s.File("a/A.txt", "a");
        _s.File("a/B.txt", "b");
        var toTmp = Move(new TransferTarget(_s.P("a"), "tmp"), _s.P("a", "A.txt"));
        await Go(toTmp);
        await Go(Move(new TransferTarget(_s.P("a"), "A.txt"), _s.P("a", "B.txt")));
        var prompts = new HookPrompts(error: (_, _) => ErrorChoice.Skip);

        await UndoAsync(toTmp, prompts); // A.txt is taken by the former B.txt

        prompts.Errors.Single().Should().BeOfType<UndoException>().Which.Problem.Should().Be(UndoProblem.PlaceTaken);
        _s.Read("a/A.txt").Should().Be("b");
        _s.Read("a/tmp").Should().Be("a");
    }

    [Fact]
    public async Task Undoing_an_overwriting_move_puts_the_file_back_and_keeps_its_folder()
    {
        _s.File("a/t/f.txt", "new");
        _s.File("b/t/f.txt", "old");
        var move = Move(new TransferTarget(_s.P("b"), null), _s.P("a", "t"));
        await Go(move, new HookPrompts(conflict: _ => ConflictChoice.Overwrite));

        await UndoAsync(move);

        _s.Read("a/t/f.txt").Should().Be("new");
        Directory.Exists(_s.P("b", "t")).Should().BeTrue("it was there before");
        _bin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Regression_undo_of_a_keep_both_copy_leaves_the_renamed_copy()
    {
        _s.File("a/f.txt", "new");
        _s.File("b/f.txt", "old");
        var copy = Copy(Into("b"), _s.P("a", "f.txt"));
        await Go(copy, new HookPrompts(conflict: _ => ConflictChoice.KeepBoth));
        _s.Read("b/f (2).txt").Should().Be("new");

        await UndoAsync(copy);

        File.Exists(_s.P("b", "f (2).txt")).Should().BeFalse("the copy made by keep both is new and goes to the bin on undo");
        _s.Read("b/f.txt").Should().Be("old");
    }

    [Fact]
    public async Task Undoing_attributes_set_twice_on_the_same_item_restores_the_first_state()
    {
        _s.File("a/d/f.txt", "f", Old);
        var op = new AttributesOperation([_s.P("a", "d"), _s.P("a", "d", "f.txt")], new AttributeChange(FileAttributes.ReadOnly, 0, New, Recursive: true));
        await Go(op);
        File.GetAttributes(_s.P("a", "d", "f.txt")).Should().HaveFlag(FileAttributes.ReadOnly);

        await UndoAsync(op);

        File.GetAttributes(_s.P("a", "d", "f.txt")).Should().NotHaveFlag(FileAttributes.ReadOnly);
        File.GetLastWriteTimeUtc(_s.P("a", "d", "f.txt")).Should().Be(Old);
    }

    // ---- sync -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Sync_of_identical_folders_finds_nothing_to_do()
    {
        _s.File("L/a.txt", "same", Old);
        _s.File("R/a.txt", "same", Old);
        _s.File("L/sub/b.txt", "same", Old);
        _s.File("R/sub/b.txt", "same", Old);

        var plan = SyncPlan.From(DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true)));
        var sync = new SyncOperation(_s.P("L"), _s.P("R"), plan);

        sync.Count.Should().Be(0);
        (await Go(sync)).Outcome.Should().Be(OperationOutcome.Completed);
    }

    [Fact]
    public async Task Sync_of_a_one_sided_tree_copies_it_over_and_then_matches()
    {
        _s.File("L/deep/er/a.txt", "a");
        _s.File("L/deep/b.txt", "b");
        _s.Dir("L", "empty");
        _s.Dir("R");

        var plan = SyncPlan.From(DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true)));
        var result = await Go(new SyncOperation(_s.P("L"), _s.P("R"), plan));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("R/deep/er/a.txt").Should().Be("a");
        DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true)).Count.Should().Be(0);
    }

    [Fact]
    public async Task Sync_of_a_timestamp_only_difference_copies_the_newer_one()
    {
        _s.File("L/a.txt", "same", New);
        _s.File("R/a.txt", "same", Old);

        var plan = SyncPlan.From(DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true)));
        plan.Should().ContainSingle().Which.Action.Should().Be(SyncAction.CopyToRight);
        await Go(new SyncOperation(_s.P("L"), _s.P("R"), plan));

        File.GetLastWriteTimeUtc(_s.P("R", "a.txt")).Should().Be(New);
        DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true)).Count.Should().Be(0);
    }

    [Fact]
    public void Sync_ignores_a_one_second_difference()
    {
        _s.File("L/a.txt", "same", Old.AddSeconds(1));
        _s.File("R/a.txt", "same", Old);

        var plan = SyncPlan.From(DirectoryCompare.Run(_s.P("L"), _s.P("R"), new CompareOptions(Subfolders: true)));

        new SyncOperation(_s.P("L"), _s.P("R"), plan).Count.Should().Be(0);
    }
}
