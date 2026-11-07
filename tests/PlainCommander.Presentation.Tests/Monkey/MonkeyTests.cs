using System.Globalization;
using Xunit;

namespace PlainCommander.Presentation.Tests.Monkey;

/// <summary>
/// The monkey runs alone: it swaps the global logger and counts unobserved task exceptions, which other tests
/// running at the same time would muddle.
/// </summary>
[CollectionDefinition("Monkey", DisableParallelization = true)]
public sealed class MonkeyGroup;

/// <summary>
/// Long random sequences of real commands while the disk changes underneath, with the invariants checked after
/// every step. Fixed seeds, so a failure replays. Longer runs: PC_MONKEY_SEEDS (how many), PC_MONKEY_FIRST_SEED,
/// PC_MONKEY_STEPS; one seed only: PC_MONKEY_SEED. PC_MONKEY_SHRINK=0 skips shortening a failing sequence.
/// </summary>
[Collection("Monkey")]
public sealed class MonkeyTests
{
    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        if (Environment.GetEnvironmentVariable("PC_MONKEY_SEED") is { Length: > 0 })
        {
            data.Add(Env("PC_MONKEY_SEED", 1));
            return data;
        }

        var first = Env("PC_MONKEY_FIRST_SEED", 1);
        for (var seed = first; seed < first + Env("PC_MONKEY_SEEDS", 20); seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_command_sequences_keep_panels_disk_and_queue_consistent(int seed)
    {
        var result = MonkeyRunner.RunSeed(seed, Env("PC_MONKEY_STEPS", 150), out var actions);
        if (Environment.GetEnvironmentVariable("PC_MONKEY_LOG") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, $"seed-{seed}.txt"), result.Log);
        }

        if (result.Failure is null)
        {
            return;
        }

        var report = result.Describe(actions);
        if (Environment.GetEnvironmentVariable("PC_MONKEY_SHRINK") != "0")
        {
            var (shortActions, shortResult) = MonkeyRunner.Shrink(seed, actions, result, TimeSpan.FromSeconds(Env("PC_MONKEY_SHRINK_SECONDS", 60)));
            report = result.Describe(actions, shortResult, shortActions);
        }

        Assert.Fail(report);
    }
}
