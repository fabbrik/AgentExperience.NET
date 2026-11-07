using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Tests.Shared;

/// <summary>
/// Story 28.7: a run captured by one service provider (one process) is finalized by another over the same storage, once
/// the first is gone -- durable capture's round trip, the same steps whichever storage the caller registers.
/// </summary>
internal static class DurableCaptureScenario
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly ClosedVerificationRound Round = new(Guid.Parse("7b0c1f1e-0d3a-4f7e-9a51-2c2b7f0e6a11"), "rev-1");

    /// <summary>What each process saw.</summary>
    internal sealed record Outcome(
        Guid RunId,
        CompleteRunResult Completed,
        bool SecondProcessHeldRunInMemory,
        IReadOnlyList<StoredExperienceRun> WaitingBefore,
        FinalizeExperienceResult Finalized,
        FinalizeExperienceResult Replayed,
        IReadOnlyList<StoredExperienceRun> WaitingAfter,
        ExperienceRecordGetResult Record);

    /// <summary>
    /// The first process only: captures and completes a run in a provider of its own, then disposes it. The run is then in
    /// the run store and nowhere else.
    /// </summary>
    internal static async Task<(Guid RunId, Guid CompletionEventId, CompleteRunResult Completed)> CaptureAsync(Action<IServiceCollection> addStorage, Scope scope)
    {
        var runId = Guid.NewGuid();
        var completionEventId = Guid.NewGuid();
        await using var first = Build(addStorage);
        var capture = first.GetRequiredService<IExperienceCaptureService>();
        capture.StartRun(
            runId,
            "fix-the-build",
            "Fix the failing build",
            scope,
            new EnvironmentFingerprint("host-1", "net10.0", "linux", "1.0.0", new Dictionary<string, string>()),
            new Provenance("durable-capture", "1.0.0", Now, null),
            Now);
        await capture.AppendAttemptAsync(
            runId,
            new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null));
        var completed = await capture.CompleteRunAsync(runId, completionEventId, RunExecutionStatus.Completed, Now.AddSeconds(10));
        return (runId, completionEventId, completed);
    }

    /// <summary>A passing finalization request for <paramref name="runId"/>, under <paramref name="decision"/> (default: permit).</summary>
    internal static FinalizeExperienceRequest Request(Guid runId, AuthorizationContext authorization, StorageDecision? decision = null) => new(
        runId,
        authorization,
        Round,
        [new RequiredCheck("build", "BuildResult")],
        [new Evidence(Guid.NewGuid(), Round.RoundId, Round.ArtifactRevision, "build", "BuildResult", CheckResult.Pass, "ci", null, Now.AddMinutes(5))],
        Round.ArtifactRevision,
        decision ?? StorageDecision.Permit,
        Now.AddMinutes(6));

    /// <summary>
    /// A run captured by one process is finalized by a second under a storage decision that refuses it: the outcome, and
    /// whether the run store still holds the run afterwards.
    /// </summary>
    internal static async Task<(FinalizeExperienceResult Result, ExperienceRunStoreOutcome StoredAfter)> DenyAsync(
        Action<IServiceCollection> addStorage, AuthorizationContext authorization, Scope scope, Action? betweenProcesses = null)
    {
        var (runId, _, _) = await CaptureAsync(addStorage, scope);
        betweenProcesses?.Invoke();
        await using var second = Build(addStorage);
        var result = await second.GetRequiredService<ExperienceFinalizationService>()
            .FinalizeAsync(Request(runId, authorization, new StorageDecision(false, "the host keeps no failed builds")));
        var stored = await second.GetRequiredService<IExperienceRunStore>().GetAsync(authorization, runId, CancellationToken.None);
        return (result, stored.Outcome);
    }

    /// <summary>Two later processes finalize the same stored run at once: both outcomes, and how many records exist.</summary>
    internal static async Task<(FinalizationOutcome[] Outcomes, ExperienceRecordGetResult Record, ExperienceRunGetResult Stored)> FinalizeConcurrentlyAsync(
        Action<IServiceCollection> addStorage, AuthorizationContext authorization, Scope scope, Action? betweenProcesses = null)
    {
        var (runId, _, _) = await CaptureAsync(addStorage, scope);
        betweenProcesses?.Invoke();
        await using var second = Build(addStorage);
        await using var third = Build(addStorage);
        var outcomes = await Task.WhenAll(
            Task.Run(() => second.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(Request(runId, authorization))),
            Task.Run(() => third.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(Request(runId, authorization))));
        var record = await second.GetRequiredService<IExperienceRecordStore>().GetAsync(
            authorization, scope, ExperienceFinalizationService.ExperienceIdFor(runId, scope), CancellationToken.None);
        var stored = await second.GetRequiredService<IExperienceRunStore>().GetAsync(authorization, runId, CancellationToken.None);
        return ([.. outcomes.Select(result => result.Outcome)], record, stored);
    }

    /// <summary>
    /// One process captures, finalizes and then has the record erased through <paramref name="erase"/>; completing the run
    /// again with the same completion event (a capture retry) must not bring the stored run back.
    /// </summary>
    internal static async Task<(CompleteRunResult Retried, ExperienceRunStoreOutcome StoredAfter)> EraseThenRecompleteAsync(
        Action<IServiceCollection> addStorage, AuthorizationContext authorization, Scope scope, Func<IServiceProvider, Guid, Task> erase)
    {
        await using var process = Build(addStorage);
        var capture = process.GetRequiredService<IExperienceCaptureService>();
        var runId = Guid.NewGuid();
        var completion = Guid.NewGuid();
        capture.StartRun(runId, "fix-the-build", null, scope, new EnvironmentFingerprint("host-1", "net10.0", "linux", null, new Dictionary<string, string>()), new Provenance("durable-capture", null, Now, null), Now);
        await capture.AppendAttemptAsync(runId, new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null));
        await capture.CompleteRunAsync(runId, completion, RunExecutionStatus.Completed, Now.AddSeconds(10));
        var finalized = await process.GetRequiredService<ExperienceFinalizationService>().FinalizeAsync(Request(runId, authorization));
        if (finalized.Outcome != FinalizationOutcome.Validated)
        {
            throw new InvalidOperationException($"Finalize: {finalized.Outcome}");
        }

        await erase(process, finalized.Record!.ExperienceId);
        var retried = await capture.CompleteRunAsync(runId, completion, RunExecutionStatus.Completed, Now.AddSeconds(10));
        var stored = await process.GetRequiredService<IExperienceRunStore>().GetAsync(authorization, runId, CancellationToken.None);
        return (retried, stored.Outcome);
    }

    /// <summary>A fresh provider (a process) over the storage under test.</summary>
    internal static ServiceProvider Process(Action<IServiceCollection> addStorage) => Build(addStorage);

    /// <summary>Captures and completes a run in one provider, disposes it, then finalizes the run in a fresh one.</summary>
    /// <param name="addStorage">Registers the storage under test, the run store included; called once per provider.</param>
    /// <param name="authorization">The host's authorization in both processes.</param>
    /// <param name="scope">The run's scope.</param>
    /// <param name="betweenProcesses">Runs after the first provider is disposed (to release pooled connections, say).</param>
    internal static async Task<Outcome> RunAsync(
        Action<IServiceCollection> addStorage,
        AuthorizationContext authorization,
        Scope scope,
        Action? betweenProcesses = null)
    {
        var runId = Guid.NewGuid();
        CompleteRunResult completed;

        // The first process: capture a failing run that was fixed, and complete it. Nothing is finalized here.
        await using (var first = Build(addStorage))
        {
            var capture = first.GetRequiredService<IExperienceCaptureService>();
            var started = capture.StartRun(
                runId,
                "fix-the-build",
                "Fix the failing build",
                scope,
                new EnvironmentFingerprint("host-1", "net10.0", "linux", "1.0.0", new Dictionary<string, string>()),
                new Provenance("durable-capture", "1.0.0", Now, null),
                Now);
            if (started.Outcome != StartRunOutcome.Started)
            {
                throw new InvalidOperationException($"StartRun: {started.Outcome}");
            }

            var appended = await capture.AppendAttemptAsync(
                runId,
                new AppendAttemptRequest(
                    Guid.NewGuid(),
                    Now,
                    TimeSpan.FromSeconds(5),
                    [
                        new RawToolCall(Guid.NewGuid(), "dotnet_build", new Dictionary<string, object?> { ["project"] = "src/App" }, Now, TimeSpan.FromSeconds(1), null, "error CS0246: The type 'Foo' could not be found"),
                        new RawToolCall(Guid.NewGuid(), "dotnet_build", new Dictionary<string, object?> { ["project"] = "src/App" }, Now.AddSeconds(2), TimeSpan.FromSeconds(1), "Build succeeded", null),
                    ],
                    "Build succeeded after adding the missing using",
                    null));
            if (appended.Outcome != AppendAttemptOutcome.Recorded)
            {
                throw new InvalidOperationException($"AppendAttempt: {appended.Outcome}");
            }

            completed = await capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddSeconds(10));
        }

        betweenProcesses?.Invoke();

        // The second process, once the verdict has arrived: it never saw the run.
        await using var second = Build(addStorage);
        var held = second.GetRequiredService<IExperienceCaptureService>().TryGetRun(runId, out _);
        var runs = second.GetRequiredService<IExperienceRunStore>();
        var waitingBefore = (await runs.ListUnfinalizedAsync(authorization, new ExperienceRunQuery(scope), CancellationToken.None)).Runs;

        var finalization = second.GetRequiredService<ExperienceFinalizationService>();
        var request = new FinalizeExperienceRequest(
            runId,
            authorization,
            Round,
            [new RequiredCheck("build", "BuildResult")],
            [new Evidence(Guid.NewGuid(), Round.RoundId, Round.ArtifactRevision, "build", "BuildResult", CheckResult.Pass, "ci", null, Now.AddMinutes(5))],
            Round.ArtifactRevision,
            StorageDecision.Permit,
            Now.AddMinutes(6));
        var finalized = await finalization.FinalizeAsync(request);
        var replayed = await finalization.FinalizeAsync(request);
        var waitingAfter = (await runs.ListUnfinalizedAsync(authorization, new ExperienceRunQuery(scope), CancellationToken.None)).Runs;
        var record = await second.GetRequiredService<IExperienceRecordStore>().GetAsync(
            authorization, scope, ExperienceFinalizationService.ExperienceIdFor(runId, scope), CancellationToken.None);

        return new Outcome(runId, completed, held, waitingBefore, finalized, replayed, waitingAfter, record);
    }

    private static ServiceProvider Build(Action<IServiceCollection> addStorage)
    {
        var services = new ServiceCollection();
        services.AddAgentExperienceCore(AgentExperienceDefaults.SanitizationAllowing("project"), AgentExperienceDefaults.CaptureLimits);
        addStorage(services);
        return services.BuildServiceProvider();
    }
}
