using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class LocalFileSystemTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pc-tests-").FullName;
    private readonly LocalFileSystem _fs = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Lists_parent_link_files_and_directories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "hello");

        var entries = await _fs.ListAsync(_root, includeHidden: true).ToListAsync();

        entries.Should().HaveCount(3);
        entries[0].IsParentLink.Should().BeTrue();
        entries.Should().ContainSingle(e => e.Name == "sub" && e.IsDirectory);
        entries.Should().ContainSingle(e => e.Name == "a.txt" && !e.IsDirectory && e.Size == 5 && e.Extension == "txt");
    }

    [Fact]
    public async Task Skips_hidden_entries_when_asked()
    {
        var hidden = Path.Combine(_root, ".hidden");
        await File.WriteAllTextAsync(hidden, string.Empty);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var visible = await _fs.ListAsync(_root, includeHidden: false).ToListAsync();
        var all = await _fs.ListAsync(_root, includeHidden: true).ToListAsync();

        visible.Should().NotContain(e => e.Name == ".hidden");
        all.Should().Contain(e => e.Name == ".hidden");
    }

    [Fact]
    public async Task Cancellation_stops_enumeration()
    {
        for (var i = 0; i < 200; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, $"f{i}"), string.Empty);
        }

        using var cts = new CancellationTokenSource();
        var act = async () =>
        {
            await foreach (var _ in _fs.ListAsync(_root, true, cts.Token))
            {
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Root_has_no_parent()
    {
        _fs.GetParent(Path.GetPathRoot(_root)!).Should().BeNull();
    }

    [Theory]
    [InlineData("a.txt", "a", "txt")]
    [InlineData("archive.tar.gz", "archive.tar", "gz")]
    [InlineData(".gitignore", ".gitignore", "")]
    [InlineData("noext", "noext", "")]
    [InlineData("trailing.", "trailing.", "")]
    public void Splits_name_and_extension(string name, string stem, string extension)
    {
        var entry = new Model.FileEntry { Name = name, FullPath = "/" + name, IsDirectory = false };
        entry.NameWithoutExtension.Should().Be(stem);
        entry.Extension.Should().Be(extension);
    }

    [Fact]
    public void Directories_never_have_an_extension()
    {
        var entry = new Model.FileEntry { Name = "v1.2", FullPath = "/v1.2", IsDirectory = true };
        entry.Extension.Should().BeEmpty();
        entry.NameWithoutExtension.Should().Be("v1.2");
    }
}
