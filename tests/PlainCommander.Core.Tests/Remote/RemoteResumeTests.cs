using AwesomeAssertions;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>Broken transfers continue from where they stopped instead of starting over.</summary>
public sealed class RemoteResumeTests : IDisposable
{
    private static readonly DateTime T0 = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Content = "0123456789abcdefghijklmnopqrstuvwxyz";
    private readonly Sandbox _server = new();
    private readonly Sandbox _local = new();

    public RemoteResumeTests()
    {
        Resume.Pause = TimeSpan.Zero;
    }

    public void Dispose()
    {
        _server.Dispose();
        _local.Dispose();
    }

    private static TransferTarget Into(string folder) => new(folder, null);

    [Fact]
    public async Task A_download_that_breaks_midway_continues_on_its_own()
    {
        _server.File("big.bin", Content, T0);
        await using var server = new FolderServer(_server.Root) { BreakAfter = 10, Breaks = 2 };

        var result = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)));

        result.Outcome.Should().Be(OperationOutcome.Completed, "no question: each attempt moved the file forward");
        _local.Read("big.bin").Should().Be(Content);
        server.Offsets.Should().Equal(0, 10, 20);
        File.Exists(_local.P("big.bin.part")).Should().BeFalse();
        File.GetLastWriteTimeUtc(_local.P("big.bin")).Should().Be(T0);
    }

    [Fact]
    public async Task An_upload_that_breaks_midway_continues_on_its_own()
    {
        _local.File("big.bin", Content, T0);
        await using var server = new FolderServer(_server.Root) { BreakAfter = 7, Breaks = 3 };

        var result = await Run.Async(new RemoteUploadOperation(server.Connections, [_local.P("big.bin")], FolderServer.At("/")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _server.Read("big.bin").Should().Be(Content);
        server.Offsets.Should().Equal(0, 7, 14, 21);
        File.Exists(_server.P("big.bin.part")).Should().BeFalse();
    }

    [Fact]
    public async Task After_the_automatic_attempts_the_user_is_asked_and_Retry_still_continues()
    {
        _server.File("big.bin", Content, T0);
        await using var server = new FolderServer(_server.Root) { BreakAfter = 4, Breaks = Resume.AutoRetries + 1 };
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Retry]);

        var result = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)), prompts);

        prompts.Errors.Should().ContainSingle().Which.Should().BeOfType<RemoteException>();
        result.Outcome.Should().Be(OperationOutcome.Completed);
        _local.Read("big.bin").Should().Be(Content);
        server.Offsets.Should().Equal(0, 4, 8, 12, 16);
    }

    [Fact]
    public async Task A_part_left_by_an_earlier_run_is_continued()
    {
        _server.File("big.bin", Content, T0);
        _local.File("big.bin.part", Content[..20]); // written now, after the server file last changed
        await using var server = new FolderServer(_server.Root);

        var result = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _local.Read("big.bin").Should().Be(Content);
        server.Offsets.Should().Equal(20);
    }

    [Fact]
    public async Task A_part_of_an_upload_left_on_the_server_is_continued()
    {
        _local.File("big.bin", Content, T0);
        _server.File("big.bin.part", Content[..12]);
        await using var server = new FolderServer(_server.Root);

        var result = await Run.Async(new RemoteUploadOperation(server.Connections, [_local.P("big.bin")], FolderServer.At("/")));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _server.Read("big.bin").Should().Be(Content);
        server.Offsets.Should().Equal(12);
    }

    [Fact]
    public async Task A_part_older_than_the_source_is_not_trusted()
    {
        _server.File("big.bin", Content, DateTime.UtcNow.AddMinutes(5)); // the file changed after the part was written
        _local.File("big.bin.part", "stale content", T0);
        await using var server = new FolderServer(_server.Root);

        await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)));

        _local.Read("big.bin").Should().Be(Content);
        server.Offsets.Should().Equal(0);
    }

    [Fact]
    public async Task A_part_as_long_as_the_source_starts_over()
    {
        _server.File("big.bin", Content, T0);
        _local.File("big.bin.part", new string('x', Content.Length + 5));
        await using var server = new FolderServer(_server.Root);

        await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)));

        _local.Read("big.bin").Should().Be(Content);
        server.Offsets.Should().Equal(0);
    }

    [Fact]
    public async Task Skipping_a_broken_file_keeps_its_part_for_later()
    {
        _server.File("big.bin", Content, T0);
        await using var server = new FolderServer(_server.Root) { BreakAfter = 8, Breaks = Resume.AutoRetries + 1 };
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        var first = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)), prompts);
        var second = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)));

        first.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        second.Outcome.Should().Be(OperationOutcome.Completed);
        _local.Read("big.bin").Should().Be(Content);
        server.Offsets[^1].Should().Be(32, "four attempts of 8 bytes were kept");
    }

    [Fact]
    public async Task A_refusal_before_any_byte_is_asked_at_once()
    {
        _server.File("big.bin", Content, T0);
        await using var server = new FolderServer(_server.Root) { BreakAfter = 0, Breaks = 1 };
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Retry]);

        var result = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/big.bin")], Into(_local.Root)), prompts);

        prompts.Errors.Should().ContainSingle("nothing moved, so there was no automatic retry");
        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.Exists(_local.P("big.bin.part")).Should().BeFalse("an empty part is not kept");
        _local.Read("big.bin").Should().Be(Content);
    }

    [Fact]
    public void Only_a_shorter_and_newer_part_is_usable()
    {
        var source = new DateTimeOffset(T0);
        Resume.Usable(10, source.AddSeconds(1), 20, source).Should().Be(10);
        Resume.Usable(10, source.AddSeconds(-1), 20, source).Should().Be(0);
        Resume.Usable(20, source.AddSeconds(1), 20, source).Should().Be(0);
        Resume.Usable(0, source.AddSeconds(1), 20, source).Should().Be(0);
        Resume.Usable(10, DateTimeOffset.MinValue, 20, source).Should().Be(10, "a server that cannot tell the time is trusted on size");
    }
}
