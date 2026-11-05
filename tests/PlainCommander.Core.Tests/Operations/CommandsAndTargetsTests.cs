using AwesomeAssertions;
using PlainCommander.Core.Navigation;
using PlainCommander.Core.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Operations;

public sealed class CommandsAndTargetsTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public void Target_existing_folder_means_into_it()
    {
        var dst = _s.Dir("dst");
        TargetResolver.Resolve(dst, _s.Root, 1).Should().Be(new TransferTarget(dst, null));
    }

    [Fact]
    public void Target_with_trailing_separator_or_star_dot_star_is_a_folder()
    {
        var expected = new TransferTarget(_s.P("new"), null);
        TargetResolver.Resolve(_s.P("new") + Path.DirectorySeparatorChar, _s.Root, 1).Should().Be(expected);
        TargetResolver.Resolve(Path.Combine(_s.P("new"), "*.*"), _s.Root, 1).Should().Be(expected);
    }

    [Fact]
    public void Target_for_one_source_is_a_new_name_and_relative_to_the_panel()
    {
        TargetResolver.Resolve("copy.txt", _s.Root, 1).Should().Be(new TransferTarget(_s.Root, "copy.txt"));
    }

    [Fact]
    public void Target_for_several_sources_is_a_folder()
    {
        TargetResolver.Resolve("bundle", _s.Root, 3).Should().Be(new TransferTarget(_s.P("bundle"), null));
    }

    [Fact]
    public void Rename_masks_are_rejected_for_now()
    {
        var act = () => TargetResolver.Resolve("*.bak", _s.Root, 2);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Creates_nested_folders()
    {
        var created = SimpleCommands.CreateDirectory(_s.Root, Path.Combine("a", "b", "c"));
        created.Should().Be(_s.P("a", "b", "c"));
        Directory.Exists(created).Should().BeTrue();
    }

    [Fact]
    public void Creates_an_empty_file_and_refuses_to_replace_anything()
    {
        var created = SimpleCommands.CreateFile(_s.Root, "notes.txt");
        created.Should().Be(_s.P("notes.txt"));
        new FileInfo(created).Length.Should().Be(0);

        var again = () => SimpleCommands.CreateFile(_s.Root, "notes.txt");
        again.Should().Throw<IOException>();
        Directory.CreateDirectory(_s.P("dir"));
        var onDir = () => SimpleCommands.CreateFile(_s.Root, "dir");
        onDir.Should().Throw<IOException>();
        var nested = () => SimpleCommands.CreateFile(_s.Root, Path.Combine("a", "b.txt"));
        nested.Should().Throw<ArgumentException>("Shift+F4 makes one file, not folders");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("..")]
    public void Rejects_empty_names(string name)
    {
        var act = () => SimpleCommands.CreateDirectory(_s.Root, name);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Renames_and_refuses_to_overwrite()
    {
        var f = _s.File("old.txt", "1");
        _s.File("taken.txt", "2");

        SimpleCommands.Rename(f, "new.txt").Should().Be(_s.P("new.txt"));
        _s.Read("new.txt").Should().Be("1");

        var act = () => SimpleCommands.Rename(_s.P("new.txt"), "taken.txt");
        act.Should().Throw<IOException>();
        _s.Read("taken.txt").Should().Be("2");
    }

    [Fact]
    public void Renames_letter_case_only()
    {
        _s.File("readme.md", "1");

        SimpleCommands.Rename(_s.P("readme.md"), "README.md");

        Directory.GetFiles(_s.Root).Select(Path.GetFileName).Should().Equal("README.md");
    }

    [Theory]
    [InlineData("*.*", "noext", true)]
    [InlineData("*.txt", "A.TXT", true)]
    [InlineData("*.txt;*.md", "b.md", true)]
    [InlineData("*.txt *.md", "c.cs", false)]
    [InlineData("", "a", false)]
    public void Masks(string mask, string name, bool expected)
    {
        FileMask.Parse(mask)(name).Should().Be(expected);
    }

    [Fact]
    public async Task Folder_size_counts_everything_below_but_not_through_links()
    {
        _s.File("d/a.bin", "12345");
        _s.File("d/sub/b.bin", "123");
        _s.File("outside/huge.bin", new string('x', 1000));
        Directory.CreateSymbolicLink(_s.P("d", "link"), _s.P("outside"));

        var size = await DirectorySize.CalculateAsync(_s.P("d"), TestContext.Current.CancellationToken);

        size.Should().Be(new DirectorySizeResult(8, 2, 2));
    }
}
