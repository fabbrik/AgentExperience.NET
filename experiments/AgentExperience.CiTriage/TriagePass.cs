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
    /// <param name="earlier">The ledger of earlier passes (PostgreSQL only; empty for a full in-memory replay).</param>
    /// <param name="newId">Identifier source for capture; <see langword="null"/> for random ids.</param>
    /// <param name="rows">Filled as the pass runs, so whatever ran can be written if it throws.</param>
    /// <param name="cancellationToken">Stops the pass.</param>
    public static async Task<(IReadOnlyList<CiFailure> Failures, ReplayResult Result)> RunAsync(
        IGitHub gitHub,
        IRepository repository,
        IChatClient model,
        Budget budget,
        string? postgresConnectionString,
        IReadOnlyList<LedgerRow> earlier,
        Func<Guid>? newId,
        Dictionary<long, LedgerRow> rows,
        CancellationToken cancellationToken)
    {
        var runs = await gitHub.ListCompletedRunsAsync(cancellationToken).ConfigureAwait(false);
        var failures = await Labelling.LabelAsync(runs, repository, cancellationToken).ConfigureAwait(false);
        var alreadyProcessed = earlier.Where(row => row.Final).Select(row => row.RunId).ToHashSet();
        var cutoff = ResumeCutoff(failures, earlier);
        var start = failures.Count == 0 ? DateTimeOffset.UnixEpoch : failures.Min(failure => failure.Run.CompletedAt).AddMinutes(-1);
        var clock = new SimulatedClock(start);
        var workspace = new TriageWorkspace(repository);
        using var metered = new MeteredChatClient(model, budget);
        await using var host = TriageHost.Create(metered, workspace, clock, postgresConnectionString, newId);
        var replay = new Replay(gitHub, host, workspace, metered, clock);
        var result = await replay.RunAsync(failures, alreadyProcessed, cutoff, rows, cancellationToken).ConfigureAwait(false);
        return (failures, result);
    }

    /// <summary>
    /// The latest finalize time (the green run's, never before the failure's) among failures an earlier pass triaged and finalized: the store holds lessons from up to
    /// then, so a resumed pass must not replay a failure from before it. <see langword="null"/> when nothing was finalized.
    /// </summary>
    public static DateTimeOffset? ResumeCutoff(IReadOnlyList<CiFailure> failures, IReadOnlyList<LedgerRow> earlier)
    {
        var finalized = earlier.Where(row => row.Finalized).Select(row => row.RunId).ToHashSet();
        var times = failures
            .Where(failure => finalized.Contains(failure.Run.Id) && failure.Label.Green is not null)
            .Select(failure => failure.Label.Green!.CompletedAt > failure.Run.CompletedAt ? failure.Label.Green.CompletedAt : failure.Run.CompletedAt)
            .ToList();
        return times.Count == 0 ? null : times.Max();
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
