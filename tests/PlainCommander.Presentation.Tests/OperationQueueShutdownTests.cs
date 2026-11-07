using AwesomeAssertions;
using PlainCommander.Core.Operations;
using Xunit;

namespace PlainCommander.Presentation.Tests;

/// <summary>Closing the window while operations run: everything stops before the app goes.</summary>
public sealed class OperationQueueShutdownTests
{
    [Fact]
    public async Task Cancel_all_stops_the_running_operation_and_drops_the_queue()
    {
        var queue = new OperationQueueViewModel(new NoPrompts());
        var completedSteps = 0;
        var running = queue.Enqueue(new WaitForCancel(), "A", _ => { completedSteps++; return Task.CompletedTask; });
        var queued = queue.Enqueue(new WaitForCancel(), "B", _ => { completedSteps++; return Task.CompletedTask; });
        await Task.Delay(50, TestContext.Current.CancellationToken);
        queue.IsBusy.Should().BeTrue();

        await queue.CancelAllAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        queue.IsBusy.Should().BeFalse();
        queue.Current.Should().BeNull();
        (await running).Outcome.Should().Be(OperationOutcome.Cancelled);
        (await queued).Outcome.Should().Be(OperationOutcome.Cancelled);
        completedSteps.Should().Be(0, "after-operation steps could open dialogs or queue more work while the app closes");
        (await queue.Enqueue(new WaitForCancel(), "C")).Outcome.Should().Be(OperationOutcome.Cancelled);
    }

    [Fact]
    public async Task Cancel_all_on_an_idle_queue_completes_at_once()
    {
        var queue = new OperationQueueViewModel(new NoPrompts());
        queue.IsBusy.Should().BeFalse();
        await queue.CancelAllAsync().WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    private sealed class WaitForCancel : IOperation
    {
        public OperationKind Kind => OperationKind.Copy;

        public async Task<OperationResult> RunAsync(OperationContext context)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            return new OperationResult(OperationOutcome.Cancelled, 0, 0, []);
        }
    }
}
