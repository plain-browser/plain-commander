using AwesomeAssertions;
using PlainCommander.Core.Rename;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class MultiRenameTests : IDisposable
{
    private static readonly DateTimeOffset When = new(2026, 3, 7, 9, 5, 4, TimeSpan.Zero);
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static RenameSource Src(string name, bool dir = false) =>
        new(Path.Combine(Path.GetTempPath(), "album", name), name, dir, When);

    private static string Name(string name, RenameRules rules, long counter = 1, bool dir = false) =>
        MultiRename.Compute(Src(name, dir), rules, counter);

    [Theory]
    [InlineData("[N]", "[E]", "photo.jpg", "photo.jpg")]
    [InlineData("[N]_[C]", "[E]", "photo.jpg", "photo_7.jpg")]
    [InlineData("[N1-3]", "[E]", "photograph.jpg", "pho.jpg")]
    [InlineData("[N2]", "[E]", "photo.jpg", "h.jpg")]
    [InlineData("[N3-]", "[E]", "photo.jpg", "oto.jpg")]
    [InlineData("[P]-[N]", "[E]", "x.jpg", "album-x.jpg")]
    [InlineData("[N]", "txt", "notes.md", "notes.txt")]
    [InlineData("[N]", "", "notes.md", "notes")]
    [InlineData("[[draft]] [N]", "[E]", "a.txt", "[draft] a.txt")]
    public void Masks(string nameMask, string extMask, string name, string expected)
    {
        Name(name, new RenameRules(nameMask, extMask), counter: 7).Should().Be(expected);
    }

    [Fact]
    public void Date_and_time_come_from_the_modification_time()
    {
        var local = When.ToLocalTime();
        Name("a.jpg", new RenameRules("[YMD]_[hms]")).Should().Be($"{local:yyyyMMdd}_{local:HHmmss}.jpg");
    }

    [Fact]
    public void Counter_uses_start_step_and_digits()
    {
        var rules = new RenameRules("IMG_[C]", CounterStart: 10, CounterStep: 5, CounterDigits: 4);
        var preview = MultiRename.Preview([Src("a.jpg"), Src("b.jpg"), Src("c.jpg")], rules, _ => false);
        preview.Select(p => p.NewName).Should().Equal("IMG_0010.jpg", "IMG_0015.jpg", "IMG_0020.jpg");
    }

    [Fact]
    public void Folders_have_no_extension()
    {
        Name("v1.2", new RenameRules("[N]-old"), dir: true).Should().Be("v1.2-old");
    }

    [Theory]
    [InlineData("draft", "final", false, false, "Draft report.txt", "final report.txt")]
    [InlineData("draft", "final", false, true, "Draft report.txt", "Draft report.txt")]
    [InlineData(@"(\d+)-(\d+)", "$2-$1", true, false, "2026-03.csv", "03-2026.csv")]
    public void Search_and_replace(string search, string replace, bool regex, bool caseSensitive, string name, string expected)
    {
        Name(name, new RenameRules(Search: search, Replace: replace, UseRegex: regex, CaseSensitive: caseSensitive)).Should().Be(expected);
    }

    [Theory]
    [InlineData(CaseChange.Lower, "Raport ROCZNY.PDF", "raport roczny.pdf")]
    [InlineData(CaseChange.Upper, "raport.pdf", "RAPORT.PDF")]
    [InlineData(CaseChange.FirstUpper, "rAPORT roczny.pdf", "Raport roczny.pdf")]
    [InlineData(CaseChange.WordsUpper, "raport roczny-2026.pdf", "Raport Roczny-2026.Pdf")]
    public void Case_changes(CaseChange change, string name, string expected)
    {
        Name(name, new RenameRules(Case: change)).Should().Be(expected);
    }

    [Fact]
    public void Problems_are_found_before_anything_happens()
    {
        var sources = new[] { Src("a.txt"), Src("b.txt"), Src("c.txt") };

        MultiRename.Preview(sources, new RenameRules("same"), _ => false)
            .Should().OnlyContain(p => p.Problem == RenameProblem.DuplicateInBatch);
        MultiRename.Preview(sources, new RenameRules("[X]"), _ => false)
            .Should().OnlyContain(p => p.Problem == RenameProblem.BadMask);
        MultiRename.Preview(sources, new RenameRules(Search: "(", UseRegex: true), _ => false)
            .Should().OnlyContain(p => p.Problem == RenameProblem.BadRegex);
        MultiRename.Preview([Src("a.txt")], new RenameRules("x|y"), _ => false)
            .Single().Problem.Should().Be(OperatingSystem.IsWindows() ? RenameProblem.InvalidName : RenameProblem.None);
        MultiRename.Preview([Src("a.txt")], new RenameRules(string.Empty, string.Empty), _ => false)
            .Single().Problem.Should().Be(RenameProblem.Empty);
        MultiRename.Preview([Src("a.txt")], new RenameRules("taken"), p => p.EndsWith("taken.txt", StringComparison.Ordinal))
            .Single().Problem.Should().Be(RenameProblem.ExistsOnDisk);
    }

    [Fact]
    public void A_name_taken_by_another_item_of_the_batch_is_not_a_conflict()
    {
        var a = Src("a.txt");
        var b = Src("b.txt");
        var rules = new RenameRules(Search: "a", Replace: "b");

        var preview = MultiRename.Preview([a], rules with { Search = "a", Replace = "b" }, p => p == b.FullPath);
        preview.Single().Problem.Should().Be(RenameProblem.ExistsOnDisk, "b.txt is not in this batch");

        var both = MultiRename.Preview([a, b], new RenameRules(Search: "[ab]", Replace: "$0", UseRegex: true), _ => true);
        both.Should().OnlyContain(p => p.Problem == RenameProblem.None, "nothing changes, nothing collides");
    }

    [Fact]
    public void Applies_with_swaps_and_undoes()
    {
        var a = _s.File("dir/a.txt", "A");
        var b = _s.File("dir/b.txt", "B");
        var sources = new[] { new RenameSource(a, "a.txt", false, When), new RenameSource(b, "b.txt", false, When) };
        // a ↔ b: each target exists until the other one has moved away.
        var swap = new RenameRules(Search: "^[ab]", UseRegex: true, Replace: "${x}");
        var plan = new[] { new RenamePreview(sources[0], "b.txt", RenameProblem.None), new RenamePreview(sources[1], "a.txt", RenameProblem.None) };
        MultiRename.Preview([sources[0]], swap with { Search = "^a", Replace = "b" }, File.Exists)[0].Problem
            .Should().Be(RenameProblem.ExistsOnDisk, "renaming only a.txt onto b.txt would collide");

        var outcome = MultiRename.Apply(plan);

        outcome.Failed.Should().BeEmpty();
        _s.Read("dir/a.txt").Should().Be("B");
        _s.Read("dir/b.txt").Should().Be("A");

        MultiRename.Undo(outcome).Failed.Should().BeEmpty();
        _s.Read("dir/a.txt").Should().Be("A");
        _s.Read("dir/b.txt").Should().Be("B");
        Directory.GetFiles(_s.P("dir")).Should().HaveCount(2, "no temporary names are left behind");
    }

    [Fact]
    public void Renames_a_real_folder_of_photos_with_a_counter()
    {
        var files = Enumerable.Range(0, 5).Select(i => _s.File($"photos/DSC{9000 + i}.JPG", i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var sources = files.Select(f => new RenameSource(f, Path.GetFileName(f), false, When)).ToArray();

        var plan = MultiRename.Preview(sources, new RenameRules("wakacje_[C]", Case: CaseChange.Lower, CounterDigits: 3), File.Exists);
        var outcome = MultiRename.Apply(plan);

        outcome.Done.Should().HaveCount(5);
        Directory.GetFiles(_s.P("photos")).Select(Path.GetFileName).Order()
            .Should().Equal("wakacje_001.jpg", "wakacje_002.jpg", "wakacje_003.jpg", "wakacje_004.jpg", "wakacje_005.jpg");
        _s.Read("photos/wakacje_003.jpg").Should().Be("2");
    }

    [Fact]
    public void Unchanged_names_are_skipped()
    {
        var a = _s.File("x/a.txt");
        var plan = MultiRename.Preview([new RenameSource(a, "a.txt", false, When)], new RenameRules(), File.Exists);

        plan.Single().Changes.Should().BeFalse();
        MultiRename.Apply(plan).Done.Should().BeEmpty();
    }
}
