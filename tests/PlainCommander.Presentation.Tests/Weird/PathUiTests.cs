using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests.Weird;

/// <summary>Breadcrumbs, path completion, the command line and panel navigation with unusual names. Regression_* tests fail on purpose.</summary>
public sealed class PathUiTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    // ---------------------------------------------------------------- breadcrumb

    [Fact]
    public void Breadcrumb_keeps_unicode_names_whole()
    {
        var segments = PathSegment.Split("C:\\\ud83d\ude00 x\\cafe\u0301\\z\u0302");

        segments.Select(s => s.Name).Should().Equal("C:", "\ud83d\ude00 x", "cafe\u0301", "z\u0302");
        segments[^1].FullPath.Should().Be("C:\\\ud83d\ude00 x\\cafe\u0301\\z\u0302");
    }

    [Fact]
    public void Breadcrumb_ignores_double_and_trailing_separators()
    {
        PathSegment.Split(@"C:\a\\b\").Select(s => s.FullPath).Should().Equal(@"C:\", @"C:\a", @"C:\a\b");
        PathSegment.Split("/").Should().ContainSingle().Which.Should().Be(new PathSegment("/", "/"));
        PathSegment.Split("//x//y/").Select(s => s.FullPath).Should().Equal("/", "/x", "/x/y");
        PathSegment.Split(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Breadcrumb_for_remote_paths_with_odd_authorities()
    {
        var ipv6 = PathSegment.Split("sftp://u@[::1]:2022/var/a b");
        ipv6.Select(s => s.Name).Should().Equal("::1", "var", "a b");
        ipv6[0].FullPath.Should().Be("sftp://u@[::1]:2022/");
        ipv6[^1].FullPath.Should().Be("sftp://u@[::1]:2022/var/a b");

        var mail = PathSegment.Split("ftps://jan%40firma.pl@host/");
        mail.Should().ContainSingle().Which.FullPath.Should().Be("ftps://jan%40firma.pl@host:21/");

        PathSegment.Split("s3://KEY@s3.example.com/bucket/\ud83d\ude00").Select(s => s.Name).Should().Equal("s3.example.com", "bucket", "\ud83d\ude00");
    }

    [Fact]
    public void Breadcrumb_for_a_bare_unc_server_and_a_share_root()
    {
        PathSegment.Split(@"\\nas\share").Select(s => s.FullPath).Should().Equal("net://", "net://nas", @"\\nas\share\");
        PathSegment.Split(@"\\nas\share\").Select(s => s.FullPath).Should().Equal("net://", "net://nas", @"\\nas\share\");
    }

    // ---------------------------------------------------------------- path completion

    [Fact]
    public void Completion_treats_wildcard_characters_and_unicode_literally()
    {
        _tree.Dir("[ab]");
        _tree.Dir("a");
        _tree.Dir("\ud83d\ude00 smile");
        _tree.Dir("100%");
        var sep = Path.DirectorySeparatorChar;

        PathCompletion.Suggest(_tree.Root + sep + "[").Should().Equal(Path.Combine(_tree.Root, "[ab]") + sep);
        PathCompletion.Suggest(_tree.Root + sep + "\ud83d\ude00").Should().Equal(Path.Combine(_tree.Root, "\ud83d\ude00 smile") + sep);
        PathCompletion.Suggest(_tree.Root + sep + "100%").Should().Equal(Path.Combine(_tree.Root, "100%") + sep);
        PathCompletion.Suggest(_tree.Root + sep + "*").Should().BeEmpty("'*' is a literal stem, not a pattern");
    }

    [Fact]
    public void Completion_with_double_separator_and_remote_and_network_text()
    {
        _tree.Dir("x");
        var sep = Path.DirectorySeparatorChar;

        PathCompletion.Suggest(_tree.Root + sep + sep).Should().ContainSingle();
        PathCompletion.Suggest("sftp://u@h/").Should().BeEmpty();
        PathCompletion.Suggest("net://").Should().BeEmpty();
        PathCompletion.Suggest("\0").Should().BeEmpty();
        PathCompletion.Suggest(new string('a', 5000) + sep).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- command line

    [Theory]
    [InlineData("cd \"a b\"", "a b")]
    [InlineData("CD..", "..")]
    [InlineData(@"cd..\..", @"..\..")]
    [InlineData("cd \ud83d\ude00", "\ud83d\ude00")]
    [InlineData("cd -", "-")]
    [InlineData("cd   ", "")]
    public void Cd_variants_navigate(string text, string argument) =>
        CommandLineViewModel.Parse(text).Should().Be(new CommandAction(CommandKind.Navigate, argument));

    [Fact]
    public void Cdrom_like_commands_still_run()
    {
        CommandLineViewModel.Parse("cdrecord x").Kind.Should().Be(CommandKind.Run);
        CommandLineViewModel.Parse("\u00e9:").Kind.Should().Be(CommandKind.Navigate, "char.IsLetter accepts it; harmless, it simply fails to open");
    }

    [Fact]
    public void Insert_quotes_names_with_spaces_and_keeps_emoji_whole()
    {
        var line = new CommandLineViewModel();
        line.Insert("my file.txt");
        line.Insert("\ud83d\ude00.txt");

        line.Text.Should().Be("\"my file.txt\" \ud83d\ude00.txt");
    }

    [Fact]
    public void Regression_CommandLine_Insert_leaves_an_ampersand_name_unquoted_so_cmd_splits_it_into_two_commands()
    {
        // The command line runs through "cmd.exe /c"; Ctrl+Enter on "R&D.txt" must not produce "R" followed by a second command "D.txt".
        var line = new CommandLineViewModel();

        line.Insert("R&D.txt");

        line.Text.Should().Be("\"R&D.txt\"");
    }

    // ---------------------------------------------------------------- panel navigation

    [Fact]
    public async Task Panel_lists_and_enters_folders_with_weird_names()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses names Windows forbids");
        var weird = _tree.Dir("?* [x] \ud83d\ude00 ");
        _tree.File(Path.Combine("?* [x] \ud83d\ude00 ", "\ud83d\ude00.txt"));
        using var panel = new PanelViewModel(new LocalFileSystem(), new RecordingLauncher());

        (await panel.NavigateAsync(weird)).Should().BeTrue();

        panel.Items.Select(i => i.Name).Should().Equal("..", "\ud83d\ude00.txt");
        (await panel.GoUpAsync()).Should().BeTrue();
        panel.Path.Should().Be(_tree.Root);
        panel.CurrentItem!.Name.Should().Be("?* [x] \ud83d\ude00 ");
    }

    [Fact]
    public async Task Regression_GoUp_from_a_path_typed_with_a_trailing_separator_stays_in_the_same_folder()
    {
        // Ctrl+L completion offers "…\src\" with a trailing separator; Backspace from there must reach the parent.
        var src = _tree.Dir("src");
        using var panel = new PanelViewModel(new LocalFileSystem(), new RecordingLauncher());
        await panel.NavigateAsync(src + Path.DirectorySeparatorChar);

        await panel.GoUpAsync();

        Path.TrimEndingDirectorySeparator(panel.Path!).Should().Be(_tree.Root);
    }

    [Fact]
    public async Task Regression_cd_with_an_absolute_unix_path_on_a_server_panel_leaves_the_server()
    {
        // On an SFTP panel "cd /home/tester/docs" means the server's folder; Path.IsPathRooted sends it to the local disk instead.
        var serverRoot = _tree.Dir("server");
        Directory.CreateDirectory(Path.Combine(serverRoot, "home", "tester", "docs"));
        var disk = _tree.Dir("disk");
        using var vm = new MainViewModel(new LocalFileSystem(), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), new FakeDialogs(), new NoPrompts(), new FakeBin(),
            remote: new RemoteSetup(new AnsweringRemotePrompts(), new NoSecretStore(), (_, _, _) => new FolderServerClient(serverRoot)));
        await vm.Right.NavigateAsync(disk);
        await vm.Left.NavigateAsync(disk);
        (await vm.ConnectAsync("sftp://tester@srv/home")).Should().BeTrue();

        vm.CommandLine.Text = "cd /home/tester/docs";
        await vm.RunCommandLineAsync();

        vm.Left.Path.Should().Be("sftp://tester@srv:22/home/tester/docs");
    }
}
