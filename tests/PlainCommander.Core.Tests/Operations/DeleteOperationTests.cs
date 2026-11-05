using AwesomeAssertions;
using PlainCommander.Core.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Operations;

public sealed class DeleteOperationTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task Deletes_a_tree_with_read_only_files()
    {
        _s.File("victim/a.txt");
        var ro = _s.File("victim/sub/ro.txt");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var lone = _s.File("lone.txt");

        var result = await Run.Async(new DeleteOperation([_s.P("victim"), lone]));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("victim")).Should().BeFalse();
        File.Exists(lone).Should().BeFalse();
    }

    [Fact]
    public async Task Never_deletes_through_a_link()
    {
        var precious = _s.File("elsewhere/precious.txt", "keep me");
        _s.Dir("victim");
        Directory.CreateSymbolicLink(_s.P("victim", "link"), _s.P("elsewhere"));

        var result = await Run.Async(new DeleteOperation([_s.P("victim")]));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("victim")).Should().BeFalse();
        File.ReadAllText(precious).Should().Be("keep me");
    }

    [Fact]
    public async Task A_failed_item_keeps_its_folders_without_extra_errors()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix permissions to make a delete fail.");
            return;
        }

        _s.File("victim/locked/f.txt");
        _s.File("victim/free.txt");
        File.SetUnixFileMode(_s.P("victim", "locked"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        var result = await Run.Async(new DeleteOperation([_s.P("victim")]), prompts);

        prompts.Errors.Should().ContainSingle("only the file that failed is reported, not its parent folders");
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        File.Exists(_s.P("victim", "free.txt")).Should().BeFalse();
        File.Exists(_s.P("victim", "locked", "f.txt")).Should().BeTrue();
    }
}

public sealed class RecycleOperationTests
{
    private sealed class FakeBin(Exception? failure = null) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];

        public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                throw failure;
            }

            Recycled.AddRange(paths);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Hands_all_paths_to_the_bin()
    {
        var bin = new FakeBin();

        var result = await Run.Async(new RecycleOperation(["/a", "/b"], bin));

        bin.Recycled.Should().Equal("/a", "/b");
        result.Done.Should().Be(2);
    }

    [Fact]
    public async Task Bin_failure_goes_through_the_error_prompt()
    {
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Abort]);

        var result = await Run.Async(new RecycleOperation(["/a"], new FakeBin(new IOException("shell said no"))), prompts);

        result.Outcome.Should().Be(OperationOutcome.Aborted);
        prompts.Errors.Single().Message.Should().Be("shell said no");
    }
}
