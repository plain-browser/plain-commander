using Xunit;
using AwesomeAssertions;
using PlainCommander.Core.FileSystem;

namespace PlainCommander.Core.Tests;

public class DiskTimeoutTests
{
    [Fact]
    public async Task A_call_that_never_returns_ends_in_a_timeout_instead_of_hanging()
    {
        var gate = new ManualResetEventSlim();
        var stuck = Task.Run(() => { gate.Wait(); return 1; });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var act = () => DiskTimeout.WithTimeoutAsync(stuck);

        await act.Should().ThrowAsync<TimeoutException>();
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(9)).And.BeLessThan(TimeSpan.FromSeconds(15));
        gate.Set();
    }

    [Fact]
    public async Task A_quick_call_returns_its_result()
    {
        (await DiskTimeout.WithTimeoutAsync(Task.Run(() => 42))).Should().Be(42);
        DiskTimeout.Run(() => "ok", "late").Should().Be("ok");
    }

    [Fact]
    public void A_silent_disk_gets_the_fallback_answer()
    {
        var gate = new ManualResetEventSlim();
        DiskTimeout.Run(() => { gate.Wait(); return "late"; }, "fallback", TimeSpan.FromMilliseconds(200)).Should().Be("fallback");
        gate.Set();
    }

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Time_spent_on_a_question_does_not_count()
    {
        // A roomy limit: a busy CI machine may stall this test for a few hundred milliseconds, and the work must
        // still be inside its fresh limit when the test looks at it again.
        var limit = TimeSpan.FromMilliseconds(600);
        var work = new TaskCompletionSource<int>();
        Task<int> waiting;
        using (DiskTimeout.Pause())
        {
            waiting = DiskTimeout.WithTimeoutAsync(work.Task, limit, TestContext.Current.CancellationToken);
            await Task.Delay(limit * 2, TestContext.Current.CancellationToken);
            waiting.IsCompleted.Should().BeFalse("the question is still open");
        }

        // answered: the work gets a full limit again
        await Task.Delay(Short / 4, TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeFalse();
        work.SetResult(7);
        (await waiting).Should().Be(7);
    }

    [Fact]
    public async Task After_the_answer_a_silent_call_still_times_out()
    {
        var never = new TaskCompletionSource<int>();
        Task<int> waiting;
        using (DiskTimeout.Pause())
        {
            waiting = DiskTimeout.WithTimeoutAsync(never.Task, Short, TestContext.Current.CancellationToken);
            await Task.Delay(Short * 2, TestContext.Current.CancellationToken);
        }

        var act = () => waiting;
        await act.Should().ThrowAsync<TimeoutException>();
    }
}
