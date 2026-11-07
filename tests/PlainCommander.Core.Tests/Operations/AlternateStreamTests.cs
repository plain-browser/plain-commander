using AwesomeAssertions;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Weird;
using Xunit;

namespace PlainCommander.Core.Tests.Operations;

/// <summary>Alternate data streams on the system drive (NTFS), which keeps them: nothing is asked, nothing is lost.</summary>
public sealed class AlternateStreamTests : IDisposable
{
    private readonly WeirdBox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task Default_answer_of_a_prompt_that_cannot_ask_is_to_continue()
    {
        IOperationPrompts prompts = new Silent();

        var answer = await prompts.ResolveLostStreamsAsync(new LostStreamsInfo("a", "b", [":x"], IsMove: false), TestContext.Current.CancellationToken);

        answer.Should().Be(StreamsChoice.Continue);
    }

    [Fact]
    public void Streams_are_listed_with_their_colon()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "alternate data streams are an NTFS feature");
        var file = _s.File("a/f.txt", "main");
        File.WriteAllText(file + ":note", "n");
        File.WriteAllText(file + ":Zone.Identifier", "z");

        NativeFile.NamedStreams(file).Should().BeEquivalentTo([":note", ":Zone.Identifier"]);
        NativeFile.NamedStreams(_s.File("a/plain.txt")).Should().BeEmpty();
        NativeFile.NamedStreams(_s.P("missing.txt")).Should().BeEmpty();
        NativeFile.SupportsStreams(file).Should().BeTrue();
        NativeFile.SupportsStreams(_s.P("not", "there", "yet.txt")).Should().BeTrue("a path that does not exist yet still has a volume");
    }

    [Theory]
    [InlineData(1000)]
    [InlineData((5 << 20) + 7)]
    public async Task Copy_to_ntfs_keeps_the_streams_and_asks_nothing(int size)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "alternate data streams are an NTFS feature");
        var file = _s.File("src/f.bin", "x");
        File.WriteAllBytes(file, Bytes.Pattern(size, 3));
        File.WriteAllText(file + ":note", "side");
        var prompts = new HookPrompts(); // a streams question would throw

        var result = await new TransferOperation(TransferKind.Copy, [file], new TransferTarget(_s.Dir("dst"), null))
            .RunAsync(new OperationContext(null, prompts, new PauseGate(), TestContext.Current.CancellationToken));

        result.Outcome.Should().Be(OperationOutcome.Completed, string.Join("; ", result.Errors));
        File.ReadAllText(_s.P("dst", "f.bin") + ":note").Should().Be("side");
        prompts.LostStreams.Should().BeEmpty();
    }

    private sealed class Silent : IOperationPrompts
    {
        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
