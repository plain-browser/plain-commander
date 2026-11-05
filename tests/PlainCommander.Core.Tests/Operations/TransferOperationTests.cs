using AwesomeAssertions;
using PlainCommander.Core.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Operations;

public sealed class TransferOperationTests : IDisposable
{
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static TransferOperation Copy(TransferTarget target, params string[] sources) => new(TransferKind.Copy, sources, target);

    private static TransferOperation Move(TransferTarget target, params string[] sources) => new(TransferKind.Move, sources, target);

    private TransferTarget Into(string dir) => new(_s.Dir(dir), null);

    [Fact]
    public async Task Copies_a_file_with_content_and_timestamp()
    {
        var src = _s.File("a/report.txt", "hello", Old);

        var result = await Run.Async(Copy(Into("b"), src));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/report.txt").Should().Be("hello");
        File.GetLastWriteTimeUtc(_s.P("b", "report.txt")).Should().Be(Old);
        File.Exists(src).Should().BeTrue();
    }

    [Fact]
    public async Task Copies_a_tree_and_reports_progress_to_the_end()
    {
        _s.File("src/top.txt", "1");
        _s.File("src/sub/deep/inner.txt", "22");
        _s.Dir("src", "empty");
        var progress = new InlineProgress(_ => { });

        var result = await Run.Async(Copy(Into("dst"), _s.P("src")), progress: progress);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("dst/src/top.txt").Should().Be("1");
        _s.Read("dst/src/sub/deep/inner.txt").Should().Be("22");
        Directory.Exists(_s.P("dst", "src", "empty")).Should().BeTrue();
        var last = progress.Reports[^1];
        last.TotalBytesDone.Should().Be(3);
        last.TotalBytesTotal.Should().Be(3);
        last.ItemsDone.Should().Be(last.ItemsTotal);
    }

    [Fact]
    public async Task Copies_a_single_file_under_a_new_name()
    {
        var src = _s.File("a/one.txt", "1");

        await Run.Async(Copy(new TransferTarget(_s.Dir("a"), "two.txt"), src));

        _s.Read("a/two.txt").Should().Be("1");
    }

    [Fact]
    public async Task Conflict_skip_keeps_the_target()
    {
        var src = _s.File("a/f.txt", "new");
        _s.File("b/f.txt", "old");
        var prompts = new ScriptedPrompts([ConflictChoice.Skip]);

        var result = await Run.Async(Copy(Into("b"), src), prompts);

        _s.Read("b/f.txt").Should().Be("old");
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        result.Skipped.Should().Be(1);
        prompts.Conflicts.Single().TargetPath.Should().Be(_s.P("b", "f.txt"));
    }

    [Fact]
    public async Task Conflict_overwrite_replaces_even_a_read_only_target()
    {
        var src = _s.File("a/f.txt", "new");
        var target = _s.File("b/f.txt", "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        var result = await Run.Async(Copy(Into("b"), src), new ScriptedPrompts([ConflictChoice.Overwrite]));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/f.txt").Should().Be("new");
    }

    [Fact]
    public async Task Keep_both_writes_next_to_the_existing_file()
    {
        var src = _s.File("a/f.txt", "new");
        _s.File("b/f.txt", "old");
        _s.File("b/f (2).txt", "older copy");

        await Run.Async(Copy(Into("b"), src), new ScriptedPrompts([ConflictChoice.KeepBoth]));

        _s.Read("b/f.txt").Should().Be("old");
        _s.Read("b/f (3).txt").Should().Be("new");
    }

    [Fact]
    public async Task Overwrite_all_asks_only_once()
    {
        var a = _s.File("a/1.txt", "n1");
        var b = _s.File("a/2.txt", "n2");
        _s.File("b/1.txt", "o1");
        _s.File("b/2.txt", "o2");
        var prompts = new ScriptedPrompts([ConflictChoice.OverwriteAll]);

        await Run.Async(Copy(Into("b"), a, b), prompts);

        prompts.Conflicts.Should().HaveCount(1);
        _s.Read("b/1.txt").Should().Be("n1");
        _s.Read("b/2.txt").Should().Be("n2");
    }

    [Fact]
    public async Task Overwrite_older_keeps_newer_targets()
    {
        var a = _s.File("a/stale.txt", "fresh", New);
        var b = _s.File("a/current.txt", "fresh", Old);
        _s.File("b/stale.txt", "old", Old);
        _s.File("b/current.txt", "newer", New);

        var result = await Run.Async(Copy(Into("b"), a, b), new ScriptedPrompts([ConflictChoice.OverwriteAllOlder]));

        _s.Read("b/stale.txt").Should().Be("fresh");
        _s.Read("b/current.txt").Should().Be("newer");
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task Cancel_at_a_conflict_aborts_the_rest()
    {
        var a = _s.File("a/1.txt", "n1");
        var b = _s.File("a/2.txt", "n2");
        _s.File("b/1.txt", "o1");

        var sequential = new TransferOperation(TransferKind.Copy, [a, b], Into("b"), parallelism: 1);

        var result = await Run.Async(sequential, new ScriptedPrompts([ConflictChoice.Cancel]));

        result.Outcome.Should().Be(OperationOutcome.Aborted);
        File.Exists(_s.P("b", "2.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Refuses_to_copy_a_folder_into_itself()
    {
        var src = _s.Dir("a");
        _s.File("a/f.txt");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        var result = await Run.Async(Copy(new TransferTarget(_s.Dir("a", "inner"), null), src), prompts);

        prompts.Errors.Single().Should().BeOfType<OperationRuleException>()
            .Which.Violation.Should().Be(RuleViolation.IntoItself);
        result.Skipped.Should().Be(1);
        Directory.Exists(_s.P("a", "inner", "a")).Should().BeFalse();
    }

    [Fact]
    public async Task Refuses_to_copy_a_file_onto_itself()
    {
        var src = _s.File("a/f.txt", "keep");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        await Run.Async(Copy(Into("a"), src), prompts);

        prompts.Errors.Single().Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.SameFile);
        _s.Read("a/f.txt").Should().Be("keep");
    }

    [Fact]
    public async Task Cancelling_mid_file_removes_the_partial_copy()
    {
        var big = _s.P("a", "big.bin");
        Directory.CreateDirectory(_s.P("a"));
        File.WriteAllBytes(big, new byte[8 << 20]);
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p =>
        {
            if (p.ItemBytesDone > 0)
            {
                cts.Cancel();
            }
        });

        var result = await Run.Async(Copy(Into("b"), big), progress: progress, token: cts.Token);

        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        File.Exists(_s.P("b", "big.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Paused_operation_waits_until_resumed()
    {
        var src = _s.File("a/f.txt", "1");
        var gate = new PauseGate();
        gate.Pause();

        var running = Run.Async(Copy(Into("b"), src), gate: gate);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        running.IsCompleted.Should().BeFalse();

        gate.Resume();
        (await running).Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/f.txt").Should().Be("1");
    }

    [Fact]
    public async Task Move_on_one_drive_renames_the_whole_folder()
    {
        _s.File("a/dir/f.txt", "1");

        var result = await Run.Async(Move(Into("b"), _s.P("a", "dir")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("a", "dir")).Should().BeFalse();
        _s.Read("b/dir/f.txt").Should().Be("1");
    }

    [Fact]
    public async Task Move_into_an_existing_folder_merges_and_removes_the_source()
    {
        _s.File("a/dir/new.txt", "n");
        _s.File("b/dir/existing.txt", "e");

        var result = await Run.Async(Move(Into("b"), _s.P("a", "dir")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/dir/new.txt").Should().Be("n");
        _s.Read("b/dir/existing.txt").Should().Be("e");
        Directory.Exists(_s.P("a", "dir")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_keeps_source_folders_that_still_hold_skipped_files()
    {
        _s.File("a/dir/keep.txt", "src");
        _s.File("a/dir/go.txt", "go");
        _s.File("b/dir/keep.txt", "dst");

        var result = await Run.Async(Move(Into("b"), _s.P("a", "dir")), new ScriptedPrompts([ConflictChoice.Skip]));

        result.Skipped.Should().Be(1);
        _s.Read("a/dir/keep.txt").Should().Be("src");
        File.Exists(_s.P("a", "dir", "go.txt")).Should().BeFalse();
        _s.Read("b/dir/go.txt").Should().Be("go");
    }

    [Fact]
    public async Task Skip_all_errors_asks_once()
    {
        var a = _s.File("a/x", "1");
        var b = _s.File("a/y", "2");
        _s.Dir("b", "x"); // folders where files should go: type mismatch twice
        _s.Dir("b", "y");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.SkipAll]);

        var result = await Run.Async(Copy(Into("b"), a, b), prompts);

        prompts.Errors.Should().ContainSingle();
        result.Skipped.Should().Be(2);
        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public async Task Copy_follows_a_link_but_stops_at_a_loop()
    {
        _s.File("a/dir/f.txt", "1");
        Directory.CreateSymbolicLink(_s.P("a", "dir", "loop"), _s.P("a", "dir"));

        var result = await Run.Async(Copy(Into("b"), _s.P("a", "dir"))).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _s.Read("b/dir/f.txt").Should().Be("1");
        Directory.Exists(_s.P("b", "dir", "loop")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(_s.P("b", "dir", "loop")).Should().BeEmpty("the loop is cut instead of copied forever");
    }

    [Fact]
    public async Task Parallel_copy_of_many_small_files_is_complete()
    {
        for (var i = 0; i < 400; i++)
        {
            _s.File($"src/d{i % 7}/f{i}.txt", "content " + i);
        }

        var progress = new InlineProgress(_ => { });
        var result = await Run.Async(Copy(Into("dst"), _s.P("src")), progress: progress);

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(400 + 7 + 1, "files, sub-folders and the copied folder itself");
        for (var i = 0; i < 400; i++)
        {
            _s.Read($"dst/src/d{i % 7}/f{i}.txt").Should().Be("content " + i);
        }

        var last = progress.Reports[^1];
        last.ItemsDone.Should().Be(last.ItemsTotal);
        last.TotalBytesDone.Should().Be(last.TotalBytesTotal);
    }

    [Fact]
    public async Task Parallel_conflicts_with_overwrite_all_ask_exactly_once()
    {
        var sources = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            sources.Add(_s.File($"a/{i}.txt", "new"));
            _s.File($"b/{i}.txt", "old");
        }

        var prompts = new ScriptedPrompts([ConflictChoice.OverwriteAll]);
        var result = await Run.Async(new TransferOperation(TransferKind.Copy, sources, Into("b")), prompts);

        prompts.Conflicts.Should().ContainSingle();
        result.Done.Should().Be(40);
        Directory.GetFiles(_s.P("b")).Select(File.ReadAllText).Should().AllBe("new");
    }

    [Fact]
    public async Task Parallel_keep_both_never_loses_a_file_when_free_names_collide()
    {
        var a = _s.File("src/a.txt", "new a");
        var a2 = _s.File("src/a (2).txt", "new a2");
        _s.File("dst/a.txt", "old a");

        var result = await Run.Async(new TransferOperation(TransferKind.Copy, [a, a2], Into("dst")), new ScriptedPrompts([ConflictChoice.KeepBothAll]));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.GetFiles(_s.P("dst")).Select(File.ReadAllText)
            .Should().BeEquivalentTo(["old a", "new a", "new a2"]);
    }

    [Fact]
    public async Task Parallel_abort_stops_the_whole_operation()
    {
        var sources = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            sources.Add(_s.File($"a/{i:D3}.txt", "x"));
        }

        _s.Dir("b", "000.txt"); // a folder where the first file should go
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Abort]);

        var result = await Run.Async(new TransferOperation(TransferKind.Copy, sources, Into("b")), prompts)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OperationOutcome.Aborted);
        prompts.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task Parallel_cancellation_reports_cancelled()
    {
        var sources = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            sources.Add(_s.File($"a/{i}.txt", "x"));
        }

        using var cts = new CancellationTokenSource();
        // The first report comes after scanning and before any copy: cancelling there is deterministic.
        var progress = new InlineProgress(p =>
        {
            if (p.ItemsTotal > 0)
            {
                cts.Cancel();
            }
        });

        var result = await Run.Async(new TransferOperation(TransferKind.Copy, sources, Into("b")), progress: progress, token: cts.Token)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OperationOutcome.Cancelled);
    }
}
