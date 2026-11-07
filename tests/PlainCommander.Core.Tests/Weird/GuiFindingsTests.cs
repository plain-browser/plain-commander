using System.IO.Compression;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Rename;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>Bugs found by clicking through the real app (docs/testy-dziwne-kombinacje.md), fixed in Core.</summary>
public sealed class GuiFindingsTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    // ---------------------------------------------------------------- F7 / new file in a deleted folder

    [Fact]
    public void Regression_new_folder_in_a_deleted_folder_does_not_recreate_it()
    {
        var gone = _s.Dir("a", "b");
        Directory.Delete(_s.P("a"), recursive: true);

        var act = () => SimpleCommands.CreateDirectory(gone, "new");

        act.Should().Throw<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.FolderGone);
        Directory.Exists(_s.P("a")).Should().BeFalse("the deleted folders must stay deleted");
    }

    [Fact]
    public void Regression_new_file_in_a_deleted_folder_does_not_recreate_it()
    {
        var gone = _s.Dir("a");
        Directory.Delete(gone);

        var act = () => SimpleCommands.CreateFile(gone, "x.txt");

        act.Should().Throw<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.FolderGone);
        Directory.Exists(gone).Should().BeFalse();
    }

    [Fact]
    public void Regression_nested_new_folder_in_an_existing_folder_still_creates_every_level()
    {
        var here = _s.Dir("here");

        SimpleCommands.CreateDirectory(here, "a/b/c");

        Directory.Exists(Path.Combine(here, "a", "b", "c")).Should().BeTrue();
    }

    // ---------------------------------------------------------------- zip entries with empty segments

    private string ZipWith(params string[] keys)
    {
        var zip = _s.P("odd.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var key in keys)
        {
            using var writer = new StreamWriter(archive.CreateEntry(key).Open());
            writer.Write("content of " + key);
        }

        return zip;
    }

    [Theory]
    [InlineData("a\\\\back\\\\slash.txt", "a/back/slash.txt")]
    [InlineData("a//b.txt", "a/b.txt")]
    [InlineData("a/./b.txt", "a/b.txt")]
    [InlineData("../x", "../x")]
    public void Regression_normalize_key_drops_empty_segments(string key, string expected) =>
        ArchivePath.NormalizeKey(key).Should().Be(expected);

    [Fact]
    public void Regression_zip_entry_with_empty_segments_lists_without_a_nameless_folder()
    {
        var zip = ZipWith("a\\\\back\\\\slash.txt");
        ArchiveCatalog.Forget(zip);
        var catalog = ArchiveCatalog.Open(zip);

        catalog.List("a").Select(e => e.Name).Should().Equal("back");
        catalog.List("a/back").Select(e => e.Name).Should().Equal("slash.txt");
    }

    [Fact]
    public async Task Regression_zip_entry_with_empty_segments_extracts_instead_of_being_skipped()
    {
        var zip = ZipWith("a\\\\back\\\\slash.txt", "a//double.txt");
        ArchiveCatalog.Forget(zip);
        var target = _s.Dir("out");

        var result = await Run.Async(new ArchiveExtractOperation([ArchivePath.Join(zip, "a")], new TransferTarget(target, null)));

        result.Errors.Should().BeEmpty();
        File.ReadAllText(Path.Combine(target, "a", "back", "slash.txt")).Should().Be("content of a\\\\back\\\\slash.txt");
        File.ReadAllText(Path.Combine(target, "a", "double.txt")).Should().Be("content of a//double.txt");
    }

    [Fact]
    public async Task Regression_zip_slip_is_still_refused_after_empty_segments_are_collapsed()
    {
        var zip = ZipWith("ok.txt", "a//..//..//escaped.txt");
        ArchiveCatalog.Forget(zip);
        var target = _s.Dir("out", "here");
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip, ErrorChoice.Skip, ErrorChoice.Skip]);

        await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null), ownFolders: false), prompts);

        File.Exists(Path.Combine(target, "ok.txt")).Should().BeTrue();
        File.Exists(_s.P("escaped.txt")).Should().BeFalse();
        File.Exists(_s.P("out", "escaped.txt")).Should().BeFalse();
    }

    // ---------------------------------------------------------------- typed names: length and trailing dots

    [Fact]
    public void Regression_a_name_longer_than_255_characters_is_refused_before_touching_the_disk()
    {
        var file = _s.File("x.txt");
        var tooLong = new string('a', 256);

        var act = () => SimpleCommands.Rename(file, tooLong);

        act.Should().Throw<InvalidNameException>().Which.Problem.Should().Be(NameProblem.TooLong);
        File.Exists(file).Should().BeTrue();
        SimpleCommands.CleanName(new string('a', 255), allowSeparators: false).Should().HaveLength(255);
    }

    [Fact]
    public void Regression_a_too_long_level_in_a_nested_new_folder_is_refused()
    {
        var here = _s.Dir("here");

        var act = () => SimpleCommands.CreateDirectory(here, "ok/" + new string('b', 300));

        act.Should().Throw<InvalidNameException>().Which.Problem.Should().Be(NameProblem.TooLong);
        Directory.Exists(Path.Combine(here, "ok")).Should().BeFalse();
    }

    [Theory]
    [InlineData("x.", "x")]
    [InlineData("x. . ", "x")]
    [InlineData("x ", "x")]
    [InlineData("..a", "..a")]
    public void Regression_typed_names_lose_trailing_dots_like_trailing_spaces(string typed, string expected) =>
        SimpleCommands.CleanName(typed, allowSeparators: false).Should().Be(expected);

    [Theory]
    [InlineData("a./b.", "a/b")]
    [InlineData("x\\..\\y.", "x\\..\\y")]
    [InlineData("./new.", "./new")]
    public void Regression_trailing_dots_are_dropped_on_every_level_but_dot_levels_stay(string typed, string expected) =>
        SimpleCommands.CleanName(typed, allowSeparators: true).Should().Be(expected);

    [Fact]
    public void Regression_rename_to_a_name_with_a_trailing_dot_drops_the_dot()
    {
        var file = _s.File("old.txt");

        var renamed = SimpleCommands.Rename(file, "x.");

        Path.GetFileName(renamed).Should().Be("x");
        File.Exists(_s.P("x")).Should().BeTrue();
    }

    [Fact]
    public void Regression_an_existing_file_with_a_trailing_dot_can_still_be_renamed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Win32 cannot create the fixture without \\\\?\\");
        var odd = _s.File("odd.");

        var renamed = SimpleCommands.Rename(odd, "fine");

        File.Exists(renamed).Should().BeTrue();
        File.Exists(odd).Should().BeFalse();
    }

    [Fact]
    public void Regression_multi_rename_flags_a_name_longer_than_255_characters()
    {
        var source = new RenameSource(_s.File("a.txt"), "a.txt", false, DateTimeOffset.Now);

        var preview = MultiRename.Preview([source], new RenameRules(new string('n', 300)), _ => false);

        preview.Single().Problem.Should().Be(RenameProblem.TooLong);
    }
}
