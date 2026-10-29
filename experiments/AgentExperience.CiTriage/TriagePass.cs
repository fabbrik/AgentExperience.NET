using AgentExperience.CiTriage.GitHub;
using AgentExperience.CiTriage.Model;
using Microsoft.Extensions.AI;

namespace AgentExperience.CiTriage;

/// <summary>One pass: label the failures, replay them through the library, and estimate.</summary>
internal static class TriagePass
{
    /// <param name="gitHub">Runs, jobs and logs.</param>
    /// <param name="repository">Commits, for the labels and the tools.</param>
    /// <param name="model">The provider client (or the scripted stand-in), unmetered.</param>
    /// <param name="budget">The caps.</param>
    /// <param name="postgresConnectionString">PostgreSQL, already migrated; <see langword="null"/> for in-memory.</param>
    /// <param name="alreadyProcessed">Runs an earlier pass already handled (PostgreSQL only).</param>
    /// <param name="newId">Identifier source for capture; <see langword="null"/> for random ids.</param>
    /// <param name="cancellationToken">Stops the pass.</param>
    public static async Task<(IReadOnlyList<CiFailure> Failures, ReplayResult Result)> RunAsync(
        IGitHub gitHub,
        IRepository repository,
        IChatClient model,
        Budget budget,
        string? postgresConnectionString,
        IReadOnlySet<long> alreadyProcessed,
        Func<Guid>? newId,
        CancellationToken cancellationToken)
    {
        var runs = await gitHub.ListCompletedRunsAsync(cancellationToken).ConfigureAwait(false);
        var failures = await Labelling.LabelAsync(runs, repository, cancellationToken).ConfigureAwait(false);
        var start = failures.Count == 0 ? DateTimeOffset.UnixEpoch : failures.Min(failure => failure.Run.CompletedAt).AddMinutes(-1);
        var clock = new SimulatedClock(start);
        var workspace = new TriageWorkspace(repository);
        using var metered = new MeteredChatClient(model, budget);
        await using var host = TriageHost.Create(metered, workspace, clock, postgresConnectionString, newId);
        var replay = new Replay(gitHub, host, workspace, metered, clock);
        var result = await replay.RunAsync(failures, alreadyProcessed, cancellationToken).ConfigureAwait(false);
        return (failures, result);
    }

    /// <summary>A deterministic identifier source, so a scripted pass assigns the same arms every time.</summary>
    public static Func<Guid> SeededIds(int seed)
    {
        var random = new Random(seed);
        return () =>
        {
            Span<byte> bytes = stackalloc byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        };
    }
}
