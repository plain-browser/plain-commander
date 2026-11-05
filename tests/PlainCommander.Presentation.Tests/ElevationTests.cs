using System.IO.Pipes;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Elevation;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Settings;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>
/// "As administrator": the operation stops at "access denied" and the rest runs in the helper. Here the
/// helper runs in-process, and "becoming administrator" means unlocking the folder (Unix permissions).
/// </summary>
public sealed class ElevationTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly ElevatePrompts _prompts = new();
    private readonly FakeElevation _elevation;
    private readonly MainViewModel _vm;
    private readonly string _a;
    private readonly string _locked;

    public ElevationTests()
    {
        _a = _tree.Dir("a");
        _locked = _tree.Dir("locked");
        File.WriteAllText(Path.Combine(_a, "one.txt"), "1");
        File.WriteAllText(Path.Combine(_a, "two.txt"), "2");
        _elevation = new FakeElevation(_locked);
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), new RecordingLauncher(), new SettingsStore(Path.Combine(_tree.Root, "s", "settings.json")), _dialogs, _prompts, new FakeBin(), elevation: _elevation);
    }

    public void Dispose()
    {
        _vm.Dispose();
        _elevation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Unlock(_locked);
        _tree.Dispose();
    }

    private static void Lock(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    private static void Unlock(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SkipOnWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses Unix permissions to deny access.");
    }

    [Fact]
    public async Task A_copy_continues_as_administrator_after_access_denied()
    {
        SkipOnWindows();
        Lock(_locked);
        await _vm.Left.NavigateAsync(_a);
        await _vm.Right.NavigateAsync(_locked);
        _dialogs.TransferAnswer = _locked + Path.DirectorySeparatorChar;
        _prompts.Answer = ErrorChoice.Elevate;

        await _vm.DropAsync([Path.Combine(_a, "one.txt"), Path.Combine(_a, "two.txt")], _locked, move: false, from: _vm.Left);

        _prompts.Offers.Should().StartWith(true);
        _elevation.Connects.Should().Be(1);
        File.ReadAllText(Path.Combine(_locked, "two.txt")).Should().Be("2");
        File.ReadAllText(Path.Combine(_locked, "one.txt")).Should().Be("1");
        _vm.Right.Items.Select(i => i.Name).Should().Contain("one.txt", "the panels refresh after the elevated part");
    }

    [Fact]
    public async Task Declining_UAC_stops_the_operation_with_a_message()
    {
        SkipOnWindows();
        Lock(_locked);
        _elevation.Decline = true;
        await _vm.Left.NavigateAsync(_a);
        _dialogs.TransferAnswer = _locked + Path.DirectorySeparatorChar;
        _prompts.Answer = ErrorChoice.Elevate;

        await _vm.DropAsync([Path.Combine(_a, "one.txt")], _locked, move: true, from: _vm.Left);

        _dialogs.Errors.Should().ContainSingle().Which.Should().Contain(Texts.ElevationRefused);
        File.Exists(Path.Combine(_a, "one.txt")).Should().BeTrue("nothing was moved");
    }

    [Fact]
    public async Task New_folder_where_access_is_denied_is_offered_as_administrator()
    {
        SkipOnWindows();
        Lock(_locked);
        await _vm.Left.NavigateAsync(_locked);
        _dialogs.NameAnswer = "made";
        _dialogs.ElevateAnswer = true;

        await _vm.MakeDirectoryAsync();

        _dialogs.ElevateQuestions.Should().ContainSingle();
        Directory.Exists(Path.Combine(_locked, "made")).Should().BeTrue();
        _vm.Left.Items.Select(i => i.Name).Should().Contain("made");
        _dialogs.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task Declining_the_question_leaves_things_as_they_were()
    {
        SkipOnWindows();
        File.WriteAllText(Path.Combine(_locked, "old.txt"), "o");
        Lock(_locked);
        await _vm.Left.NavigateAsync(_locked);
        _vm.Left.CurrentIndex = _vm.Left.Items.ToList().FindIndex(i => i.Name == "old.txt");
        _dialogs.NameAnswer = "new.txt";
        _dialogs.ElevateAnswer = false;

        await _vm.RenameCurrentAsync();

        _dialogs.ElevateQuestions.Should().ContainSingle();
        _elevation.Connects.Should().Be(0);
        File.Exists(Path.Combine(_locked, "old.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Other_errors_are_not_offered_elevation()
    {
        await _vm.Left.NavigateAsync(_a);
        _dialogs.NameAnswer = "one.txt"; // a file of that name exists

        await _vm.MakeDirectoryAsync();

        _dialogs.ElevateQuestions.Should().BeEmpty();
        _dialogs.Errors.Should().ContainSingle();
    }

    private sealed class ElevatePrompts : IOperationPrompts
    {
        public ErrorChoice Answer { get; set; } = ErrorChoice.Abort;

        public List<bool> Offers { get; } = [];

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) => Task.FromResult(ConflictChoice.Skip);

        public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken)
        {
            Offers.Add(canElevate);
            return Task.FromResult(canElevate ? Answer : ErrorChoice.Abort);
        }
    }

    /// <summary>An in-process helper; connecting "grants the rights" by unlocking the folder.</summary>
    private sealed class FakeElevation(string locked) : IElevation, IAsyncDisposable
    {
        private ElevationClient? _client;
        private Task? _host;

        public bool Decline { get; set; }

        public int Connects { get; private set; }

        public async Task<ElevationClient> ConnectAsync(CancellationToken cancellationToken)
        {
            if (Decline)
            {
                throw new OperationCanceledException("UAC declined");
            }

            if (_client is { IsConnected: true })
            {
                return _client;
            }

            Connects++;
            Unlock(locked);
            var name = "pc-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(server.WaitForConnectionAsync(cancellationToken), client.ConnectAsync(cancellationToken));
            _host = Task.Run(() => ElevationHost.RunAsync(client, TimeSpan.FromMinutes(1)), cancellationToken);
            _client = new ElevationClient(server);
            return _client;
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            if (_host is not null)
            {
                await _host;
            }
        }
    }
}
