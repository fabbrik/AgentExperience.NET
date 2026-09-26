using System.Runtime.CompilerServices;
using AgentExperience.LiveReuse.Harness;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Tests;

/// <summary>A clock whose every timestamp read advances by one millisecond, so latencies are deterministic.</summary>
internal sealed class SteppingClock : TimeProvider
{
    private long _ticks;

    public static DateTimeOffset Now { get; } = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond);
}

internal static class TestSupport
{
    public static RunDescriptor ScriptedDescriptor { get; } = new("scripted", ScriptedOperatorModel.ModelId, "none (offline)", 0.25, 1.50, "test prices");

    public static Task<LiveExperimentResult> RunScriptedAsync(
        IChatClient model,
        LiveBudget? budget = null,
        MigrationTaskSet? taskSet = null,
        LivePreregistration? design = null) =>
        LiveReuseExperiment.RunAsync(new LiveExperimentOptions
        {
            Model = model,
            Descriptor = ScriptedDescriptor,
            Budget = budget,
            Clock = new SteppingClock(),
            TaskSet = taskSet ?? MigrationTaskSet.Current,
            Design = design ?? LivePreregistration.ReadEmbedded(),
        });

    /// <summary>The experiment project's directory, found from this source file's location.</summary>
    public static string ExperimentDirectory([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "AgentExperience.LiveReuse"));

    /// <summary>
    /// Every model call the scripted model received, grouped by the run that made it, using each run's recorded call
    /// count. Learning runs first, then trials, in the order they ran -- the order the calls were made in.
    /// </summary>
    public static IReadOnlyList<(RunRecord Run, IReadOnlyList<IReadOnlyList<ChatMessage>> Calls)> CallsByRun(LiveExperimentResult result, ScriptedOperatorModel model)
    {
        var grouped = new List<(RunRecord, IReadOnlyList<IReadOnlyList<ChatMessage>>)>();
        var offset = 0;
        foreach (var run in result.Learning.Concat(result.Trials))
        {
            grouped.Add((run, model.Calls.Skip(offset).Take(run.Usage.ModelCalls).ToList()));
            offset += run.Usage.ModelCalls;
        }

        Assert.Equal(model.Calls.Count, offset);
        return grouped;
    }
}
