using AwesomeAssertions;
using PlainCommander.Core.Search;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Search;

public sealed class FileSearchTests : IDisposable
{
    private readonly Sandbox _s = new();

    public FileSearchTests()
    {
        _s.File("readme.md", "# Plain Commander\nhello world");
        _s.File("notes.txt", "nothing here", modifiedUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _s.Dir("src");
        _s.File(Path.Combine("src", "Program.cs"), "class Hello { }");
        _s.File(Path.Combine("src", "big.bin"), new string('x', 5000));
        _s.Dir("src", "deep");
        _s.File(Path.Combine("src", "deep", "hello.txt"), "HELLO again");
    }

    public void Dispose() => _s.Dispose();

    private static async Task<List<string>> Names(SearchCriteria c) =>
        await FileSearch.RunAsync(c, null).Select(e => e.Name).OrderBy(n => n).ToListAsync();

    [Fact]
    public async Task Masks_and_subfolders()
    {
        (await Names(new SearchCriteria(_s.Root, "*.txt"))).Should().Equal("hello.txt", "notes.txt");
        (await Names(new SearchCriteria(_s.Root, "*.txt", Subfolders: false))).Should().Equal("notes.txt");
        (await Names(new SearchCriteria(_s.Root, "*.cs;*.md"))).Should().Equal("Program.cs", "readme.md");
        (await Names(new SearchCriteria(_s.Root, "src"))).Should().Equal(["src"], "folders match by name");
    }

    [Fact]
    public async Task Text_search_reads_content_with_case_option_and_skips_binaries()
    {
        (await Names(new SearchCriteria(_s.Root, "*.*", Text: "hello"))).Should().Equal("hello.txt", "Program.cs", "readme.md");
        (await Names(new SearchCriteria(_s.Root, "*.*", Text: "hello", CaseSensitive: true))).Should().Equal("readme.md");
        _s.File("blob.dat", "he\0llo");
        (await Names(new SearchCriteria(_s.Root, "*.dat", Text: "llo"))).Should().BeEmpty("files with zero bytes are binary");
    }

    [Fact]
    public async Task Size_and_date_bounds()
    {
        (await Names(new SearchCriteria(_s.Root, MinBytes: 1000))).Should().Equal("big.bin");
        (await Names(new SearchCriteria(_s.Root, "*.*", MaxBytes: 20, Subfolders: false))).Should().Equal("notes.txt");
        (await Names(new SearchCriteria(_s.Root, "*.txt", ModifiedBefore: new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero)))).Should().Equal("notes.txt");
        (await Names(new SearchCriteria(_s.Root, "*.txt", ModifiedAfter: new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero)))).Should().Equal("hello.txt");
    }

    [Fact]
    public async Task Reports_progress_and_can_be_cancelled()
    {
        var reports = new List<SearchProgress>();
        var progress = new Progress<SearchProgress>(reports.Add);
        var all = await FileSearch.RunAsync(new SearchCriteria(_s.Root), progress).ToListAsync();
        await Task.Delay(50); // Progress<T> posts asynchronously
        all.Should().HaveCount(7);
        reports.Should().NotBeEmpty();
        reports[^1].Folders.Should().Be(3);
        reports[^1].Matches.Should().Be(7);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await FileSearch.RunAsync(new SearchCriteria(_s.Root), null, cts.Token).ToListAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
