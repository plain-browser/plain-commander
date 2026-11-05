using System.IO.Pipes;
using AwesomeAssertions;
using PlainCommander.Core.Elevation;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Elevation;

/// <summary>
/// The app ↔ helper protocol over a real named pipe, with the helper in-process (no UAC here). What runs
/// in the helper is the same TransferOperation/DeleteOperation the other tests cover.
/// </summary>
public sealed class ElevationTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    private static async Task<(ElevationClient Client, Task Host)> ConnectAsync(TimeSpan? idle = null)
    {
        var name = "pc-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync());
        var host = Task.Run(() => ElevationHost.RunAsync(client, idle ?? TimeSpan.FromMinutes(1)));
        return (new ElevationClient(server), host);
    }

    private static OperationContext Context(ScriptedPrompts prompts, IProgress<OperationProgress>? progress = null, PauseGate? gate = null, CancellationToken token = default) =>
        new(progress, prompts, gate ?? new PauseGate(), token);

    [Fact]
    public async Task Copies_in_the_helper_and_reports_progress()
    {
        _s.File("src/a.txt", "alpha");
        _s.File("src/sub/b.txt", "beta");
        _s.Dir("dst");
        var (client, host) = await ConnectAsync();
        var reports = new List<OperationProgress>();
        var job = new TransferOperation(TransferKind.Copy, [_s.P("src")], new TransferTarget(_s.P("dst"), null)).ElevatedJob;

        var result = await client.RunAsync(job, Context(new ScriptedPrompts(), new InlineProgress(reports.Add)));
        await client.DisposeAsync();
        await host;

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(4);
        _s.Read("dst/src/sub/b.txt").Should().Be("beta");
        reports.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Conflicts_are_asked_in_the_app()
    {
        _s.File("src/a.txt", "new");
        _s.File("dst/a.txt", "old");
        var (client, host) = await ConnectAsync();
        var prompts = new ScriptedPrompts(conflicts: [ConflictChoice.Skip]);
        var job = new TransferOperation(TransferKind.Copy, [_s.P("src", "a.txt")], new TransferTarget(_s.P("dst"), null)).ElevatedJob;

        var result = await client.RunAsync(job, Context(prompts));
        await client.DisposeAsync();
        await host;

        prompts.Conflicts.Should().ContainSingle().Which.TargetPath.Should().EndWith("a.txt");
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        _s.Read("dst/a.txt").Should().Be("old");
    }

    [Fact]
    public async Task Errors_are_asked_in_the_app_without_offering_elevation_again()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix permissions to make a delete fail.");
            return;
        }

        _s.File("locked/f.txt");
        File.SetUnixFileMode(_s.P("locked"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var (client, host) = await ConnectAsync();
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Skip]);

        var result = await client.RunAsync(new DeleteOperation([_s.P("locked", "f.txt")]).ElevatedJob, Context(prompts));
        await client.DisposeAsync();
        await host;

        prompts.Errors.Should().ContainSingle().Which.Should().BeOfType<UnauthorizedAccessException>();
        prompts.Offers.Should().Equal(false);
        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
    }

    [Fact]
    public async Task Commands_return_the_path_or_rethrow_the_helpers_error()
    {
        _s.Dir("here/taken");
        var (client, host) = await ConnectAsync();

        var made = await client.RunCommandAsync(ElevatedJob.CreateDirectory(_s.P("here"), "new"));
        var renamed = await client.RunCommandAsync(ElevatedJob.Rename(made, "renamed"));
        var taken = () => client.RunCommandAsync(ElevatedJob.CreateFile(_s.P("here"), "taken"));

        (await taken.Should().ThrowAsync<OperationRuleException>()).Which.Violation.Should().Be(RuleViolation.AlreadyExists);
        await client.DisposeAsync();
        await host;
        renamed.Should().Be(_s.P("here", "renamed"));
        Directory.Exists(renamed).Should().BeTrue();
    }

    [Fact]
    public async Task Pause_and_cancel_reach_the_helper()
    {
        _s.File("src/a.txt");
        _s.Dir("dst");
        var (client, host) = await ConnectAsync();
        var gate = new PauseGate();
        gate.Pause();
        using var cancel = new CancellationTokenSource();
        var job = new TransferOperation(TransferKind.Copy, [_s.P("src")], new TransferTarget(_s.P("dst"), null)).ElevatedJob;

        var run = client.RunAsync(job, Context(new ScriptedPrompts(), gate: gate, token: cancel.Token));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        run.IsCompleted.Should().BeFalse("the helper waits while the app's dialog is paused");
        await cancel.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await client.DisposeAsync();
        await host;

        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        File.Exists(_s.P("dst", "src", "a.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task A_job_that_starts_paused_does_no_work_until_it_is_resumed()
    {
        // The pause travels with the Run message: a helper that began first and heard "pause" later could finish a small
        // job before it took hold. Many items make a head start visible.
        for (var i = 0; i < 40; i++)
        {
            _s.File($"src/f{i}.txt");
        }

        _s.Dir("dst");
        var (client, host) = await ConnectAsync();
        var gate = new PauseGate();
        gate.Pause();
        var job = new TransferOperation(TransferKind.Copy, [_s.P("src")], new TransferTarget(_s.P("dst"), null)).ElevatedJob;

        var run = client.RunAsync(job, Context(new ScriptedPrompts(), gate: gate));
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var copiedWhilePaused = Directory.Exists(_s.P("dst", "src")) ? Directory.GetFiles(_s.P("dst", "src")).Length : 0;
        gate.Resume();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await client.DisposeAsync();
        await host;

        copiedWhilePaused.Should().Be(0, "a paused job must not copy anything");
        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.GetFiles(_s.P("dst", "src")).Should().HaveCount(40);
    }

    [Fact]
    public async Task The_helper_leaves_when_idle()
    {
        var (client, host) = await ConnectAsync(TimeSpan.FromMilliseconds(200));

        await host.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        client.IsConnected.Should().BeFalse();
        await client.Invoking(c => c.RunCommandAsync(ElevatedJob.CreateDirectory(_s.Root, "x"))).Should().ThrowAsync<IOException>();
        await client.DisposeAsync();
    }

    [Fact]
    public void Failures_survive_the_trip()
    {
        var rule = FailureInfo.From("p", new OperationRuleException(RuleViolation.IntoItself, "C:\\a")).ToException();
        var io = FailureInfo.From("p", new IOException("in use", unchecked((int)0x80070020))).ToException();

        rule.Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.IntoItself);
        io.HResult.Should().Be(unchecked((int)0x80070020));
        FailureInfo.From("p", new UnauthorizedAccessException("no")).ToException().Should().BeOfType<UnauthorizedAccessException>();
    }

    [Fact]
    public void Access_denied_is_recognised()
    {
        ElevationRules.IsAccessDenied(new UnauthorizedAccessException()).Should().BeTrue();
        ElevationRules.IsAccessDenied(new IOException("x", unchecked((int)0x80070005))).Should().BeTrue();
        ElevationRules.IsAccessDenied(new IOException("x", unchecked((int)0x80070522))).Should().BeTrue();
        ElevationRules.IsAccessDenied(new IOException("x", unchecked((int)0x80070020))).Should().BeFalse("a file in use is not about rights");
        ElevationRules.IsAccessDenied(new FileNotFoundException()).Should().BeFalse();
    }
}

/// <summary>The Elevate answer and the resume rules, without a helper.</summary>
public sealed class ElevateChoiceTests : IDisposable
{
    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task Elevate_stops_the_operation_and_says_so()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Uses Unix permissions to make a delete fail.");
            return;
        }

        _s.File("locked/f.txt");
        File.SetUnixFileMode(_s.P("locked"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Elevate]);

        var result = await Run.Async(new DeleteOperation([_s.P("locked", "f.txt")]), prompts, canElevate: true);

        prompts.Offers.Should().Equal(true);
        result.Outcome.Should().Be(OperationOutcome.NeedsElevation);
    }

    [Fact]
    public async Task Elevation_is_offered_only_for_access_denied_and_only_when_allowed()
    {
        _s.File("a.txt");
        _s.Dir("dst/a.txt"); // a folder where the file should go: not about rights
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Elevate]);

        var result = await Run.Async(new TransferOperation(TransferKind.Copy, [_s.P("a.txt")], new TransferTarget(_s.P("dst"), null)), prompts, canElevate: true);

        prompts.Offers.Should().Equal(false);
        result.Outcome.Should().Be(OperationOutcome.Aborted, "Elevate without the offer counts as Abort");
    }

    [Fact]
    public async Task Resume_does_not_ask_about_files_already_copied()
    {
        var when = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        _s.File("src/done.txt", "same", when);
        _s.File("src/todo.txt", "new");
        _s.File("dst/src/done.txt", "same", when);
        var job = new TransferOperation(TransferKind.Move, [_s.P("src")], new TransferTarget(_s.P("dst"), null), 1).ElevatedJob with { Resume = true };

        var result = await Run.Async(job.CreateOperation(), new ScriptedPrompts());

        result.Outcome.Should().Be(OperationOutcome.Completed);
        result.Done.Should().Be(2, "the folder and todo.txt; done.txt was counted before");
        File.Exists(_s.P("dst", "src", "todo.txt")).Should().BeTrue();
        Directory.Exists(_s.P("src")).Should().BeFalse("a move removes what was already copied, too");
    }

    [Fact]
    public async Task Resume_skips_sources_that_are_already_gone()
    {
        _s.File("left.txt");
        var job = new DeleteOperation([_s.P("gone.txt"), _s.P("left.txt")]).ElevatedJob with { Resume = true };

        var result = await Run.Async(job.CreateOperation(), new ScriptedPrompts());

        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.Exists(_s.P("left.txt")).Should().BeFalse();
    }
}
