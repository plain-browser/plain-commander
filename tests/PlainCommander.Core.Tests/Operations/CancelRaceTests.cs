using AwesomeAssertions;
using PlainCommander.Core.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Operations;

/// <summary>Found by the random command sequences (Presentation tests, Monkey): Cancel pressed while a step fails.</summary>
public sealed class CancelRaceTests
{
    private sealed class NoQuestions : IOperationPrompts
    {
        public int Asked { get; private set; }

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict, CancellationToken cancellationToken) =>
            Task.FromResult(ConflictChoice.Skip);

        public Task<ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(ErrorChoice.Skip);
        }
    }

    [Fact]
    public async Task Regression_a_step_failing_after_cancel_ends_as_cancelled_not_as_a_crash()
    {
        using var cts = new CancellationTokenSource();
        var prompts = new NoQuestions();
        var runner = new StepRunner(new OperationContext(null, prompts, new PauseGate(), cts.Token));

        // F5 onto the file itself, Cancel pressed at once: the check passed the checkpoint, then the rule failed
        var result = await StepRunner.GuardAsync(runner, () => runner.Run("a.txt", () =>
        {
            cts.Cancel();
            throw new OperationRuleException(RuleViolation.SameFile, "a.txt");
        }));

        result.Outcome.Should().Be(OperationOutcome.Cancelled);
        prompts.Asked.Should().Be(0, "nobody is asked about a step of a cancelled operation");
    }
}
