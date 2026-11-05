using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

public sealed class UnixModeTests
{
    [Theory]
    [InlineData(0x1ED, "rwxr-xr-x", "755")]
    [InlineData(0x1A4, "rw-r--r--", "644")]
    [InlineData(0x3FF, "rwxrwxrwt", "1777")]
    [InlineData(0x9A4, "rwSr--r--", "4644")]
    [InlineData(0, "---------", "000")]
    public void Formats_like_ls(int mode, string text, string octal)
    {
        UnixMode.Format(mode).Should().Be(text);
        UnixMode.ToOctal(mode).Should().Be(octal);
        UnixMode.TryParse(octal, out var back).Should().BeTrue();
        back.Should().Be(mode);
    }

    [Fact]
    public void Converts_the_digit_form_the_libraries_use()
    {
        UnixMode.ToDigits(0x1ED).Should().Be(755);
        UnixMode.FromDigits(644).Should().Be(0x1A4);
        UnixMode.FromDigits(9).Should().Be(0, "9 is not an octal digit");
        UnixMode.TryParse("8", out _).Should().BeFalse();
        UnixMode.TryParse("12345", out _).Should().BeFalse();
    }

    [Fact]
    public void Folders_get_execute_where_read_is_set()
    {
        UnixMode.WithSearch(0x1A4).Should().Be(0x1ED); // 644 → 755
        UnixMode.WithSearch(0x180).Should().Be(0x1C0); // 600 → 700
    }
}

/// <summary>chmod, and Shift+F2 / synchronize with a server on one side (the folder server stands in for SSH.NET/FluentFTP).</summary>
public sealed class ServerCompareTests : IDisposable
{
    private static readonly DateTime T0 = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly Sandbox _server = new();
    private readonly Sandbox _local = new();

    public void Dispose()
    {
        _server.Dispose();
        _local.Dispose();
    }

    [Fact]
    public async Task Chmod_sets_the_mode_and_follows_into_folders_with_X_for_subfolders()
    {
        _server.File(Path.Combine("site", "index.html"));
        _server.File(Path.Combine("site", "css", "a.css"));
        _server.File("top.txt");
        await using var server = new FolderServer(_server.Root);

        var flat = await Run.Async(new RemoteModeOperation(server.Connections, [FolderServer.At("/top.txt")], 0x180));
        var deep = await Run.Async(new RemoteModeOperation(server.Connections, [FolderServer.At("/site")], 0x1A4, recursive: true));

        flat.Outcome.Should().Be(OperationOutcome.Completed);
        deep.Done.Should().Be(4);
        server.Modes.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["/top.txt"] = 0x180,
            ["/site"] = 0x1A4, // chosen items get exactly what was asked
            ["/site/index.html"] = 0x1A4,
            ["/site/css"] = 0x1ED, // 644 → 755 inside, or the tree could not be entered
            ["/site/css/a.css"] = 0x1A4,
        });
    }

    [Fact]
    public async Task Chmod_errors_go_to_the_usual_question()
    {
        await using var server = new FolderServer(_server.Root);
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        var result = await Run.Async(new RemoteModeOperation(server.Connections, [FolderServer.At("/missing")], 0x1A4), prompts);

        prompts.Errors.Should().ContainSingle();
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
    }

    [Fact]
    public async Task Compares_a_disk_folder_with_a_server_folder()
    {
        _local.File(Path.Combine("L", "same.txt"), "same", T0);
        _local.File(Path.Combine("L", "newer-here.txt"), "new", T0.AddDays(1));
        _local.File(Path.Combine("L", "only-here.txt"), "x", T0);
        _local.File(Path.Combine("L", "sub", "deep.txt"), "deep", T0);
        _server.File(Path.Combine("R", "same.txt"), "same", T0);
        _server.File(Path.Combine("R", "newer-here.txt"), "old", T0);
        _server.File(Path.Combine("R", "only-there.txt"), "y", T0);
        _server.File(Path.Combine("R", "sub", "deep.txt"), "deep", T0.AddDays(2));
        await using var server = new FolderServer(_server.Root);
        await using var right = new RemoteCompareSide(server.Connections, FolderServer.At("/R"));

        var result = await DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), right, new CompareOptions(Subfolders: true));

        result.Items.Should().BeEquivalentTo(new Dictionary<string, Difference>
        {
            ["newer-here.txt"] = Difference.LeftNewer,
            ["only-here.txt"] = Difference.OnlyLeft,
            ["only-there.txt"] = Difference.OnlyRight,
            ["sub\\deep.txt"] = Difference.RightNewer,
        });
        result.EntriesOf("newer-here.txt").Right!.Size.Should().Be(3);
        result.EntriesOf("only-there.txt").Left.Should().BeNull();
    }

    [Fact]
    public async Task By_content_reads_the_server_file()
    {
        _local.File(Path.Combine("L", "copy.txt"), "same bytes", T0);
        _local.File(Path.Combine("L", "edit.txt"), "aaaa", T0);
        _server.File(Path.Combine("R", "copy.txt"), "same bytes", T0.AddHours(1));
        _server.File(Path.Combine("R", "edit.txt"), "bbbb", T0.AddHours(1));
        await using var server = new FolderServer(_server.Root);
        await using var right = new RemoteCompareSide(server.Connections, FolderServer.At("/R"));

        var result = await DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), right, new CompareOptions(ByContent: true));

        result.Items.Keys.Should().BeEquivalentTo(["edit.txt"]);
    }

    [Fact]
    public async Task A_missing_server_folder_is_empty_but_a_broken_connection_stops_the_comparison()
    {
        _local.File(Path.Combine("L", "a.txt"));
        await using var server = new FolderServer(_server.Root);
        await using (var missing = new RemoteCompareSide(server.Connections, FolderServer.At("/nothing")))
        {
            (await DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), missing, new CompareOptions())).Items.Keys.Should().Equal("a.txt");
        }

        _server.Dir("R");
        server.DropNextCalls = 1;
        await using var broken = new RemoteCompareSide(server.Connections, FolderServer.At("/R"));
        var run = () => DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), broken, new CompareOptions());

        await run.Should().ThrowAsync<RemoteException>("an empty listing would offer to copy everything");
    }

    [Fact]
    public async Task Synchronizes_both_ways_with_a_server()
    {
        _local.File(Path.Combine("L", "up.txt"), "to server", T0.AddDays(1));
        _local.File(Path.Combine("L", "sub", "up2.txt"), "deep up", T0);
        _local.Dir("L", "sub");
        _server.File(Path.Combine("R", "up.txt"), "old", T0);
        _server.File(Path.Combine("R", "down.txt"), "to disk", T0);
        _server.Dir("R", "sub");
        await using var server = new FolderServer(_server.Root);
        await using var right = new RemoteCompareSide(server.Connections, FolderServer.At("/R"));
        var compared = await DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), right, new CompareOptions(Subfolders: true));

        var sync = new SyncOperation(_local.P("L"), FolderServer.At("/R"), SyncPlan.From(compared), remote: server.Connections);
        var result = await Run.Async(sync, new ScriptedPrompts());

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _server.Read(Path.Combine("R", "up.txt")).Should().Be("to server", "overwritten without a question: the direction was chosen");
        _server.Read(Path.Combine("R", "sub", "up2.txt")).Should().Be("deep up");
        _local.Read(Path.Combine("L", "down.txt")).Should().Be("to disk");
        await using var again = new RemoteCompareSide(server.Connections, FolderServer.At("/R"));
        (await DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), again, new CompareOptions(Subfolders: true))).Count.Should().Be(0, "times travel with the files");
    }

    [Fact]
    public async Task Progress_names_each_folder_and_cancel_stops()
    {
        _local.File(Path.Combine("L", "a", "x.txt"));
        _local.File(Path.Combine("L", "a", "b", "y.txt"));
        _local.File(Path.Combine("R", "a", "b", "y.txt"));
        var seen = new List<CompareProgress>();

        await DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), new DiskCompareSide(_local.P("R")), new CompareOptions(Subfolders: true), new InlineCompareProgress(seen.Add));

        seen.Select(p => p.Folder).Should().Equal("", "a", "a\\b");
        seen.Select(p => p.Folders).Should().Equal(1, 2, 3);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var stopped = () => DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), new DiskCompareSide(_local.P("R")), new CompareOptions(Subfolders: true), token: cancel.Token);
        await stopped.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class InlineCompareProgress(Action<CompareProgress> report) : IProgress<CompareProgress>
    {
        public void Report(CompareProgress value) => report(value);
    }

    [Fact]
    public void Two_servers_cannot_be_synchronized_directly()
    {
        var both = () => new SyncOperation(FolderServer.At("/a"), FolderServer.At("/b"), [], remote: null);

        both.Should().Throw<ArgumentException>();
        SyncOperation.Join("sftp://u@h:22/", "a\\b.txt").Should().Be("sftp://u@h:22/a/b.txt");
        SyncOperation.Join(_local.Root, "").Should().Be(_local.Root);
    }
}
