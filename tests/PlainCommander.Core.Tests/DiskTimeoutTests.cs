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
}
