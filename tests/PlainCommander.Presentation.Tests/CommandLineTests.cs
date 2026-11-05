using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class CommandLineTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly RecordingLauncher _launcher = new();

    public void Dispose() => _tree.Dispose();

    private MainViewModel Create() => new(new LocalFileSystem(), _launcher, new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), new FakeDialogs(), new NoPrompts(), new FakeBin());

    [Theory]
    [InlineData("dir /b", CommandKind.Run, "dir /b")]
    [InlineData("cd sub", CommandKind.Navigate, "sub")]
    [InlineData("CD \"my docs\"", CommandKind.Navigate, "my docs")]
    [InlineData("cd..", CommandKind.Navigate, "..")]
    [InlineData("cd", CommandKind.Navigate, "")]
    [InlineData("..", CommandKind.Navigate, "..")]
    [InlineData("d:", CommandKind.Navigate, @"D:\")]
    [InlineData("   ", CommandKind.None, "")]
    [InlineData("cdrom.exe", CommandKind.Run, "cdrom.exe")]
    public void Parse_tells_navigation_from_commands(string text, CommandKind kind, string argument)
    {
        CommandLineViewModel.Parse(text).Should().Be(new CommandAction(kind, argument));
    }

    [Fact]
    public void History_moves_with_up_and_down_and_keeps_the_draft()
    {
        var line = new CommandLineViewModel { Text = "first" };
        line.Submit();
        line.Text = "second";
        line.Submit();
        line.Text = "typing";

        line.MoveHistory(-1);
        line.Text.Should().Be("second");
        line.MoveHistory(-1);
        line.Text.Should().Be("first");
        line.MoveHistory(-1);
        line.Text.Should().Be("first", "the oldest entry stays");
        line.MoveHistory(+1);
        line.MoveHistory(+1);
        line.Text.Should().Be("typing", "down past the newest restores the draft");
    }

    [Fact]
    public void History_drops_duplicates_and_keeps_the_last_fifty()
    {
        var line = new CommandLineViewModel();
        for (var i = 0; i < 60; i++)
        {
            line.Text = $"cmd{i}";
            line.Submit();
        }

        line.Text = "cmd55";
        line.Submit();

        line.History.Should().HaveCount(CommandLineViewModel.HistoryLimit);
        line.History[^1].Should().Be("cmd55");
        line.History.Should().OnlyHaveUniqueItems();
        line.History[0].Should().Be("cmd10", "re-entering cmd55 moves it to the end without dropping anything");
    }

    [Fact]
    public void Insert_appends_with_a_space_and_quotes_names_with_spaces()
    {
        var line = new CommandLineViewModel { Text = "type" };
        line.Insert("my file.txt");
        line.Insert("b.txt");
        line.Text.Should().Be("type \"my file.txt\" b.txt");
    }

    [Fact]
    public async Task Enter_runs_in_the_active_panel_folder_and_cd_moves_the_panel()
    {
        var sub = _tree.Dir("sub");
        using var vm = Create();
        await vm.Left.NavigateAsync(_tree.Root);

        vm.CommandLine.Text = "echo hi";
        await vm.RunCommandLineAsync();
        _launcher.Commands.Should().ContainSingle().Which.Should().Be(("echo hi", _tree.Root));

        vm.CommandLine.Text = "cd sub";
        await vm.RunCommandLineAsync();
        vm.Left.Path.Should().Be(sub);

        vm.CommandLine.Text = "..";
        await vm.RunCommandLineAsync();
        vm.Left.Path.Should().Be(_tree.Root);
        vm.CommandLine.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task Ctrl_Enter_inserts_the_current_name_and_history_survives_a_restart()
    {
        _tree.File("report final.txt");
        using (var vm = Create())
        {
            await vm.Left.NavigateAsync(_tree.Root);
            vm.InsertCurrentName();
            vm.CommandLine.Text.Should().BeEmpty("the cursor is on '..'");
            vm.Left.CurrentIndex = 1;
            vm.InsertCurrentName();
            vm.CommandLine.Text.Should().Be("\"report final.txt\"");
            vm.InsertCurrentPath();
            vm.CommandLine.Text.Should().EndWith(_tree.Root.Contains(' ') ? $"\"{_tree.Root}\"" : _tree.Root);
            await vm.RunCommandLineAsync();
            vm.Save(null);
        }

        using var second = Create();
        second.CommandLine.History.Should().ContainSingle().Which.Should().StartWith("\"report final.txt\"");
    }
}
