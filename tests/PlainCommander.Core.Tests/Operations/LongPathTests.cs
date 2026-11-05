using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Operations;

/// <summary>Paths far beyond the old 260-character MAX_PATH limit of Windows.</summary>
public sealed class LongPathTests : IDisposable
{
    private readonly Sandbox _s = new();
    private readonly string _deep;

    public LongPathTests()
    {
        // 6 levels of 50 characters: well over 300 characters below the sandbox.
        var segments = Enumerable.Range(1, 6).Select(i => $"{i}_" + new string('d', 48)).ToArray();
        _deep = Path.Combine([_s.P("src"), .. segments]);
        Directory.CreateDirectory(_deep);
        File.WriteAllText(Path.Combine(_deep, "file-at-the-bottom.txt"), "deep");
    }

    public void Dispose() => _s.Dispose();

    private string DeepFile => Path.Combine(_deep, "file-at-the-bottom.txt");

    [Fact]
    public void The_fixture_really_is_longer_than_max_path()
    {
        DeepFile.Length.Should().BeGreaterThan(300);
    }

    [Fact]
    public async Task Lists_a_folder_with_a_long_path()
    {
        var entries = await new LocalFileSystem().ListAsync(_deep, includeHidden: true).ToListAsync();
        entries.Should().Contain(e => e.Name == "file-at-the-bottom.txt");
    }

    [Fact]
    public async Task Copies_moves_measures_and_deletes_long_paths()
    {
        var copy = await Run.Async(new TransferOperation(TransferKind.Copy, [_s.P("src")], new TransferTarget(_s.Dir("copy"), null)));
        copy.Outcome.Should().Be(OperationOutcome.Completed);
        var copied = DeepFile.Replace(_s.P("src"), _s.P("copy", "src"), StringComparison.Ordinal);
        File.ReadAllText(copied).Should().Be("deep");

        (await DirectorySize.CalculateAsync(_s.P("copy"), TestContext.Current.CancellationToken)).Bytes.Should().Be(4);

        var renamed = SimpleCommands.Rename(copied, "renamed.txt");
        File.Exists(renamed).Should().BeTrue();

        var move = await Run.Async(new TransferOperation(TransferKind.Move, [_s.P("copy", "src")], new TransferTarget(_s.Dir("moved"), null)));
        move.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("copy", "src")).Should().BeFalse();

        var delete = await Run.Async(new DeleteOperation([_s.P("moved")]));
        delete.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.Exists(_s.P("moved")).Should().BeFalse();
    }
}
