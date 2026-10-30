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
