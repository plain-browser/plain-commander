using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Search;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests;

/// <summary>Alt+F7 additions: regular expressions and searching inside archives.</summary>
public sealed class SearchExtrasTests : IDisposable
{
    private readonly Sandbox _s = new();

    public SearchExtrasTests()
    {
        _s.File("readme.txt", "plain file on disk");
        _s.File("report-2026.txt", "quarterly numbers");
        var zip = _s.P("bundle.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        Write(archive, "docs/readme.txt", "hello   world from inside");
        Write(archive, "docs/deep/notes.md", "nothing here");
        Write(archive, "top.txt", "top level");
    }

    public void Dispose()
    {
        ArchiveCatalog.Forget(_s.P("bundle.zip"));
        _s.Dispose();
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    private async Task<List<string>> Find(SearchCriteria criteria)
    {
        var found = new List<string>();
        await foreach (var e in FileSearch.RunAsync(criteria, null, TestContext.Current.CancellationToken))
        {
            found.Add(Path.GetRelativePath(_s.Root, e.FullPath).Replace('\\', '/'));
        }

        return found;
    }

    [Fact]
    public async Task Archives_are_searched_only_when_asked()
    {
        (await Find(new SearchCriteria(_s.Root, "*.txt"))).Should().BeEquivalentTo(["readme.txt", "report-2026.txt"]);

        (await Find(new SearchCriteria(_s.Root, "*.txt", InArchives: true)))
            .Should().BeEquivalentTo(["readme.txt", "report-2026.txt", "bundle.zip/docs/readme.txt", "bundle.zip/top.txt"]);
    }

    [Fact]
    public async Task Content_inside_archives_is_searched()
    {
        (await Find(new SearchCriteria(_s.Root, "*.*", Text: "hello", InArchives: true)))
            .Should().Equal("bundle.zip/docs/readme.txt");
    }

    [Fact]
    public async Task Without_subfolders_only_the_archive_top_level_counts()
    {
        (await Find(new SearchCriteria(_s.Root, "*.txt", Subfolders: false, InArchives: true)))
            .Should().BeEquivalentTo(["readme.txt", "report-2026.txt", "bundle.zip/top.txt"]);
    }

    [Fact]
    public async Task Names_by_regular_expression()
    {
        (await Find(new SearchCriteria(_s.Root, @"^report-\d{4}\.txt$", NameRegex: true))).Should().Equal("report-2026.txt");
    }

    [Fact]
    public async Task Text_by_regular_expression()
    {
        (await Find(new SearchCriteria(_s.Root, "*.*", Text: @"hel+o\s+world", TextRegex: true, InArchives: true)))
            .Should().Equal("bundle.zip/docs/readme.txt");
        (await Find(new SearchCriteria(_s.Root, "*.*", Text: @"QUARTERLY", TextRegex: true, CaseSensitive: true))).Should().BeEmpty();
    }

    [Fact]
    public void Broken_regular_expressions_are_reported_before_the_search()
    {
        var act = () => new SearchCriteria(_s.Root, "([", NameRegex: true).NameMatcher();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Zip_entry_times_are_local_times()
    {
        var zip = _s.P("times.zip");
        var local = new DateTime(2026, 1, 2, 10, 30, 0, DateTimeKind.Local);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("a.txt");
            entry.LastWriteTime = new DateTimeOffset(local);
            using var w = new StreamWriter(entry.Open());
            w.Write("x");
        }

        try
        {
            var node = ArchiveCatalog.Open(zip).Find("a.txt")!;
            node.Modified.ToLocalTime().DateTime.Should().Be(local, "the panel must show the time the file had when it was zipped");
        }
        finally
        {
            ArchiveCatalog.Forget(zip);
        }
    }

    [Theory]
    [InlineData(@"c:\t\find", @"C:\t\find")]
    [InlineData(@"D:\x", @"D:\x")]
    [InlineData(@"\\server\share", @"\\server\share")]
    public void Drive_letters_are_upper_case(string typed, string expected)
    {
        PlainCommander.Core.FileSystem.ExactPath.UpperDrive(typed).Should().Be(expected);
    }
}
