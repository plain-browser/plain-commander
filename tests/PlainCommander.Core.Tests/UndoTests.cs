using AwesomeAssertions;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using PlainCommander.Core.Undo;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class UndoTests : IDisposable
{
    private readonly Sandbox _s = new();
    private readonly DeletingBin _bin = new();
    private readonly FakeRestore _restore = new();

    public void Dispose() => _s.Dispose();

    private static UndoEntry Entry(IUndoable op) => new("step", op.Journal.Actions, DateTime.Now);

    private Task<OperationResult> UndoAsync(IUndoable op, ScriptedPrompts? prompts = null) =>
        Run.Async(new UndoOperation(Entry(op), _bin, _restore), prompts);

    [Fact]
    public async Task Undoing_a_copy_sends_what_it_created_to_the_bin_topmost_items_only()
    {
        _s.File("src/a.txt", "a");
        _s.File("src/dir/b.txt", "b");
        var dst = _s.Dir("dst");
        var copy = new TransferOperation(TransferKind.Copy, [_s.P("src", "a.txt"), _s.P("src", "dir")], new TransferTarget(dst, null), parallelism: 1);
        await Run.Async(copy);

        (await UndoAsync(copy)).Outcome.Should().Be(OperationOutcome.Completed);

        _bin.Recycled.Select(Path.GetFileName).Should().BeEquivalentTo(["a.txt", "dir"], "the folder takes its content along");
        Directory.EnumerateFileSystemEntries(dst).Should().BeEmpty();
        File.Exists(_s.P("src", "dir", "b.txt")).Should().BeTrue("the sources are untouched");
    }

    [Fact]
    public async Task An_overwritten_file_is_left_and_only_new_files_go()
    {
        _s.File("src/x/old.txt", "new content");
        _s.File("src/x/fresh.txt", "fresh");
        _s.File("dst/x/old.txt", "old content");
        var copy = new TransferOperation(TransferKind.Copy, [_s.P("src", "x")], new TransferTarget(_s.P("dst"), null), parallelism: 1);
        await Run.Async(copy, new ScriptedPrompts([ConflictChoice.Overwrite]));

        copy.Journal.Actions.Should().Contain(a => a.Kind == UndoKind.Overwritten);
        await UndoAsync(copy);

        File.Exists(_s.P("dst", "x", "fresh.txt")).Should().BeFalse();
        _s.Read(Path.Combine("dst", "x", "old.txt")).Should().Be("new content", "overwritten content cannot come back; the file stays");
    }

    [Fact]
    public async Task Undoing_a_move_puts_everything_back_including_emptied_folders()
    {
        _s.File("src/tree/a.txt", "a");
        _s.File("src/tree/sub/b.txt", "b");
        _s.Dir("dst/tree"); // an existing folder: the move goes item by item and merges
        var move = new TransferOperation(TransferKind.Move, [_s.P("src", "tree")], new TransferTarget(_s.P("dst"), null), parallelism: 1);
        await Run.Async(move);
        Directory.Exists(_s.P("src", "tree")).Should().BeFalse();

        (await UndoAsync(move)).Outcome.Should().Be(OperationOutcome.Completed);

        _s.Read(Path.Combine("src", "tree", "sub", "b.txt")).Should().Be("b");
        _s.Read(Path.Combine("src", "tree", "a.txt")).Should().Be("a");
        Directory.Exists(_s.P("dst", "tree")).Should().BeTrue("it existed before the move");
        Directory.Exists(_s.P("dst", "tree", "sub")).Should().BeFalse("the move created it; empty now, it is removed");
        _bin.Recycled.Should().BeEmpty("nothing needed the bin");
    }

    [Fact]
    public async Task A_whole_folder_moved_at_once_goes_back_at_once()
    {
        _s.File("src/pack/a.txt", "a");
        var move = new TransferOperation(TransferKind.Move, [_s.P("src", "pack")], new TransferTarget(_s.Dir("dst"), null));
        await Run.Async(move);

        await UndoAsync(move);

        _s.Read(Path.Combine("src", "pack", "a.txt")).Should().Be("a");
        Directory.Exists(_s.P("dst", "pack")).Should().BeFalse();
    }

    [Fact]
    public async Task A_taken_place_is_reported_and_the_rest_still_goes_back()
    {
        _s.File("src/a.txt", "a");
        _s.File("src/b.txt", "b");
        var move = new TransferOperation(TransferKind.Move, [_s.P("src", "a.txt"), _s.P("src", "b.txt")], new TransferTarget(_s.Dir("dst"), null));
        await Run.Async(move);
        _s.File("src/a.txt", "someone else's a");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        var result = await UndoAsync(move, prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors.Should().ContainSingle().Which.Should().BeOfType<UndoException>().Which.Problem.Should().Be(UndoProblem.PlaceTaken);
        _s.Read(Path.Combine("src", "a.txt")).Should().Be("someone else's a", "nothing is overwritten");
        _s.Read(Path.Combine("src", "b.txt")).Should().Be("b");
    }

    [Fact]
    public async Task Attributes_and_dates_come_back()
    {
        var file = _s.File("f.txt", "f");
        var before = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, before);
        var change = new AttributesOperation([file], new AttributeChange(FileAttributes.ReadOnly, 0, DateTimeOffset.UtcNow, false));
        await Run.Async(change);
        File.GetAttributes(file).Should().HaveFlag(FileAttributes.ReadOnly);

        await UndoAsync(change);

        File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly).Should().BeFalse();
        File.GetLastWriteTimeUtc(file).Should().Be(before);
    }

    [Fact]
    public async Task A_new_archive_goes_to_the_bin()
    {
        var source = _s.File("doc.txt", "d");
        var pack = new ArchivePackOperation([source], _s.P("doc.zip"));
        await Run.Async(pack);

        await UndoAsync(pack);

        _bin.Recycled.Should().Equal(_s.P("doc.zip"));
        File.Exists(source).Should().BeTrue();
    }

    [Fact]
    public async Task Recycled_items_are_restored_through_the_bin()
    {
        var file = _s.File("gone.txt", "g");
        var recycle = new RecycleOperation([file], _bin);
        await Run.Async(recycle);
        recycle.Journal.Actions.Should().ContainSingle(a => a.Kind == UndoKind.Recycled && a.Path == file);

        (await UndoAsync(recycle)).Outcome.Should().Be(OperationOutcome.Completed);

        _restore.Restored.Should().Equal(file);
    }

    [Fact]
    public void Windows_brings_a_recycled_file_back_to_its_place()
    {
        if (OperatingSystem.IsWindows())
        {
            RestoreForReal();
        }
        else
        {
            Assert.Skip("the Recycle Bin is Windows-only");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RestoreForReal()
    {
        var file = _s.File("binned.txt", "from the bin");
        var since = DateTime.UtcNow;
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        File.Exists(file).Should().BeFalse();
        var bin = Path.Combine(Path.GetPathRoot(file)!, "$Recycle.Bin", System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value);
        Assert.SkipUnless(
            Directory.Exists(bin) && Directory.EnumerateFiles(bin, "$I*").Any(i => File.GetLastWriteTimeUtc(i) >= since.AddSeconds(-2)),
            "this session has no Recycle Bin (a remote shell deletes for good); checked in the app instead");

        new WindowsRecycleBinRestore().Restore(file, since.AddSeconds(-2)).Should().BeTrue();

        File.ReadAllText(file).Should().Be("from the bin");
        new WindowsRecycleBinRestore().Restore(file, since.AddSeconds(-2)).Should().BeFalse("it is no longer in the bin");
    }

    /// <summary>Deletes for real, so the tests can see what is left.</summary>
    private sealed class DeletingBin : IRecycleBin
    {
        public List<string> Recycled { get; } = [];

        public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            foreach (var p in paths)
            {
                Recycled.Add(p);
                if (Directory.Exists(p))
                {
                    Directory.Delete(p, recursive: true);
                }
                else
                {
                    File.Delete(p);
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeRestore : IRecycleBinRestore
    {
        public List<string> Restored { get; } = [];

        public bool Restore(string originalPath, DateTime sinceUtc)
        {
            Restored.Add(originalPath);
            return true;
        }
    }
}
