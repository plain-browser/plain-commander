using System.Diagnostics;
using System.Globalization;
using System.Text;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Model;
using PlainCommander.Core.Settings;
using Serilog.Core;
using Serilog.Events;

namespace PlainCommander.Presentation.Tests.Monkey;

/// <summary>An invariant broke (or a command threw) at some step.</summary>
internal sealed class MonkeyFailure(string invariant, string message) : Exception(message)
{
    /// <summary>Which rule broke; a shortened sequence must break the same one.</summary>
    public string Invariant { get; } = invariant;
}

internal sealed record MonkeyResult(int Seed, bool Watchers, int Steps, MonkeyFailure? Failure, int FailedStep, IReadOnlyList<string> Log)
{
    public string Describe(IReadOnlyList<MonkeyAction> actions, MonkeyResult? shortened = null, IReadOnlyList<MonkeyAction>? shortActions = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Monkey seed {Seed} ({(Watchers ? "with" : "without")} watchers) failed at step {FailedStep}: [{Failure!.Invariant}] {Failure.Message}");
        sb.AppendLine("Steps:");
        foreach (var line in Log)
        {
            sb.Append("  ").AppendLine(line);
        }

        if (shortened is { Failure: not null } && shortActions is not null)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Shortened to {shortActions.Count} steps, failing at step {shortened.FailedStep}: [{shortened.Failure.Invariant}] {shortened.Failure.Message}");
            foreach (var line in shortened.Log)
            {
                sb.Append("  ").AppendLine(line);
            }

            sb.AppendLine("Replay: MonkeyRunner.Run(" + Seed + ", [" + string.Join(", ", shortActions.Select(static a => a.ToCode())) + "], watchers: " + (Watchers ? "true" : "false") + ")");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Replay: PC_MONKEY_SEED={Seed} PC_MONKEY_STEPS={actions.Count}");
        }

        return sb.ToString();
    }
}

/// <summary>Runs a list of actions against a real <see cref="MainViewModel"/> over a temp world, checking after each step.</summary>
internal sealed partial class MonkeyRunner : IDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);

    private readonly int _seed;
    private readonly bool _watchers;
    private readonly UiThread _ui;
    private readonly MonkeyWorld _world;
    private readonly FakeDialogs _dialogs = new();
    private readonly MonkeyPrompts _prompts = new();
    private readonly MonkeyBin _bin;
    private readonly RecordingLauncher _launcher = new();
    private readonly MainViewModel _vm;
    private readonly List<string> _log = [];
    private readonly List<string> _errors = [];
    private readonly HashSet<string> _allowedLoss = new(StringComparer.Ordinal);
    private readonly Lock _lossLock = new();
    private HashSet<string> _ids = [];
    private string _outside = string.Empty;
    private IReadOnlyList<MonkeyAction> _actions = [];
    private string _note = string.Empty;

    private MonkeyRunner(int seed, bool watchers, UiThread ui, MonkeyWorld world)
    {
        _seed = seed;
        _watchers = watchers;
        _ui = ui;
        _world = world;
        _bin = new MonkeyBin(world.Bin);
        _vm = new MainViewModel(new ArchiveFileSystem(new LocalFileSystem()), _launcher, new SettingsStore(world.SettingsFile), _dialogs, _prompts, _bin, restore: _bin);
        foreach (var panel in new[] { _vm.Left, _vm.Right })
        {
            panel.AutoRefreshDelay = watchers ? TimeSpan.FromMilliseconds(15) : Timeout.InfiniteTimeSpan;
        }
    }

    public void Dispose() => _vm.Dispose();

    private PanelViewModel P => _vm.ActivePanel;

    private PanelViewModel Other => _vm.InactivePanel;

    private string Side(PanelViewModel panel) => ReferenceEquals(panel, _vm.Left) ? "L" : "R";

    // ---- entry points ----

    public static MonkeyResult RunSeed(int seed, int steps, out IReadOnlyList<MonkeyAction> actions)
    {
        actions = MonkeyAction.Generate(seed, steps);
        return Run(seed, actions, watchers: seed % 2 == 1);
    }

    /// <summary>Plays <paramref name="actions"/> in a fresh world built from <paramref name="seed"/>; stops at the first failure.</summary>
    public static MonkeyResult Run(int seed, IReadOnlyList<MonkeyAction> actions, bool watchers)
    {
        using var world = new MonkeyWorld();
        world.Build(new Random(unchecked(seed * 7919 + 17)));
        var unobserved = new List<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.Add(e.Exception.ToString());
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        var previousLogger = Serilog.Log.Logger;
        try
        {
            var result = UiThread.Run(async ui =>
            {
                var runner = new MonkeyRunner(seed, watchers, ui, world);
                ErrorSink.Current = runner._errors;
                Serilog.Log.Logger = new Serilog.LoggerConfiguration().MinimumLevel.Error().WriteTo.Sink(ErrorSink.Instance).CreateLogger();
                try
                {
                    return await runner.PlayAsync(actions);
                }
                finally
                {
                    await runner._vm.Operations.CancelAllAsync();
                    runner.Dispose();
                }
            });

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            lock (unobserved)
            {
                if (result.Failure is null && unobserved.Count > 0)
                {
                    return result with { Failure = new MonkeyFailure("unobserved task exception", string.Join(Environment.NewLine, unobserved)), FailedStep = actions.Count };
                }
            }

            return result;
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            ErrorSink.Current = null;
            Serilog.Log.Logger = previousLogger;
        }
    }

    /// <summary>
    /// Drops chunks of actions (then single ones) while the same invariant still breaks. Bounded by
    /// <paramref name="budget"/>; a step that does not fail reliably simply stays in.
    /// </summary>
    public static (IReadOnlyList<MonkeyAction> Actions, MonkeyResult Result) Shrink(int seed, IReadOnlyList<MonkeyAction> actions, MonkeyResult failed, TimeSpan budget)
    {
        var watch = Stopwatch.StartNew();
        var best = actions.Take(failed.FailedStep + 1).ToList();
        var bestResult = failed;
        var chunk = Math.Max(1, best.Count / 2);
        while (chunk >= 1 && watch.Elapsed < budget)
        {
            var removedAny = false;
            for (var start = 0; start < best.Count && watch.Elapsed < budget;)
            {
                var candidate = best.Take(start).Concat(best.Skip(start + chunk)).ToList();
                if (candidate.Count == 0)
                {
                    start += chunk;
                    continue;
                }

                var result = Run(seed, candidate, failed.Watchers);
                if (result.Failure is { } f && f.Invariant == failed.Failure!.Invariant)
                {
                    best = candidate.Take(result.FailedStep + 1).ToList();
                    bestResult = result;
                    removedAny = true;
                }
                else
                {
                    start += chunk;
                }
            }

            if (!removedAny)
            {
                chunk /= 2;
            }
        }

        return (best, bestResult);
    }

    // ---- the run ----

    private async Task<MonkeyResult> PlayAsync(IReadOnlyList<MonkeyAction> actions)
    {
        _actions = actions;
        var step = -1;
        try
        {
            (await _vm.Left.NavigateAsync(_world.World)).MustBeTrue("the left panel opens the world");
            var first = _world.Entries(directories: true).FirstOrDefault() ?? _world.World;
            (await _vm.Right.NavigateAsync(first)).MustBeTrue("the right panel opens a folder");
            await SettleAsync();
            _outside = _world.OutsideSnapshot();
            _ids = _world.AllIds();

            for (step = 0; step < actions.Count; step++)
            {
                var action = actions[step];
                _prompts.Reset(action.Prompt);
                lock (_lossLock)
                {
                    _allowedLoss.Clear();
                }

                string what;
                _note = string.Empty;
                try
                {
                    var run = ExecuteAsync(action, step);
                    if (await Task.WhenAny(run, Task.Delay(StepTimeout)) != run)
                    {
                        throw new MonkeyFailure("hang", $"{action.Kind} did not finish in {StepTimeout.TotalSeconds} s");
                    }

                    what = await run;
                }
                catch (MonkeyFailure)
                {
                    _log.Add($"{step}: {action.Kind} {Draws(action)}");
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Add($"{step}: {action.Kind} {Draws(action)}");
                    throw new MonkeyFailure("command threw", ex.ToString());
                }

                await SettleAsync();
                var prompts = _prompts.Log.Count == 0 ? string.Empty : " {" + string.Join("; ", _prompts.Log) + "}";
                _log.Add($"{step}: {what}{_note}{prompts}");
                await CheckAsync(fullRefresh: (uint)action.C % 3 == 0 || step == actions.Count - 1);
            }

            return new MonkeyResult(_seed, _watchers, actions.Count, null, -1, _log);
        }
        catch (MonkeyFailure failure)
        {
            _log.Add($"   state: {State()}");
            return new MonkeyResult(_seed, _watchers, actions.Count, failure, step, _log);
        }
    }

    private static string Draws(MonkeyAction a) => $"({a.A}, {a.B}, {a.C})";

    private string State() =>
        $"active {Side(P)}; L={_world.Rel(_vm.Left.Path)} [{Names(_vm.Left)}] cursor {_vm.Left.CurrentIndex}; R={_world.Rel(_vm.Right.Path)} [{Names(_vm.Right)}] cursor {_vm.Right.CurrentIndex}";

    private static string Names(PanelViewModel panel) =>
        string.Join(", ", panel.Items.Take(30).Select(static i => (i.IsMarked ? "*" : string.Empty) + i.Name)) + (panel.Items.Count > 30 ? ", …" : string.Empty);

    /// <summary>Waits until no operation runs, no panel loads and the UI thread has nothing queued.</summary>
    private async Task SettleAsync()
    {
        var watch = Stopwatch.StartNew();
        var idleRounds = 0;
        while (idleRounds < 2)
        {
            if (watch.Elapsed > StepTimeout)
            {
                throw new MonkeyFailure("hang", $"did not settle: busy={_vm.Operations.IsBusy} loading L={_vm.Left.IsLoading} R={_vm.Right.IsLoading} queued={_ui.Pending}");
            }

            if (_vm.Operations.IsBusy || _vm.Left.IsLoading || _vm.Right.IsLoading)
            {
                idleRounds = 0;
                await Task.Delay(1);
                continue;
            }

            idleRounds = _ui.Pending == 0 ? idleRounds + 1 : 0;
            await Task.Yield();
        }
    }

    private void AllowLoss(IEnumerable<string> ids)
    {
        lock (_lossLock)
        {
            _allowedLoss.UnionWith(ids);
        }
    }

    private sealed class ErrorSink : ILogEventSink
    {
        public static readonly ErrorSink Instance = new();

        public static List<string>? Current { get; set; }

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level >= LogEventLevel.Error && Current is { } errors)
            {
                lock (errors)
                {
                    errors.Add(logEvent.RenderMessage(CultureInfo.InvariantCulture) + (logEvent.Exception is { } ex ? Environment.NewLine + ex : string.Empty));
                }
            }
        }
    }
}

internal static class MonkeyAssert
{
    public static void MustBeTrue(this bool value, string because)
    {
        if (!value)
        {
            throw new MonkeyFailure("setup", because);
        }
    }

    public static void Check(bool condition, string invariant, string message)
    {
        if (!condition)
        {
            throw new MonkeyFailure(invariant, message);
        }
    }
}
