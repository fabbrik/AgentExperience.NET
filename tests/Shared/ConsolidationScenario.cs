using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;

namespace AgentExperience.Tests.Shared;

/// <summary>
/// Story 28.6 end to end over a real store: three verified runs that failed the same way and were fixed the same way (the
/// second and third given the first lesson, so reuse evidence makes its counters overlap theirs), one consolidation pass,
/// a second pass that does nothing, then a fourth run folded in by a third pass. The same steps whichever store is passed.
/// </summary>
internal static class ConsolidationScenario
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private const string Timeout = "TimeoutException: the test host did not answer after 30 seconds.";

    private static readonly ClosedVerificationRound Round = new(Guid.Parse("5d3c9e2a-28a6-4c57-9b1e-7e0a5d1e2861"), "rev-1");

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
        ["ToolResult"] = new(new HashSet<string>(StringComparer.Ordinal) { "value" }, new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
    });

    /// <summary>What the scenario saw.</summary>
    internal sealed record Outcome(
        IReadOnlyList<ExperienceRecord> Sources,
        ExperienceConsolidationResult First,
        ExperienceRecord Consolidated,
        IReadOnlyList<ExperienceRecord> SourcesAfter,
        IReadOnlyList<LifecycleEvent> SourcesLastEvents,
        ExperienceConsolidationResult Second,
        ExperienceRecord Later,
        ExperienceConsolidationResult Third,
        ExperienceRecord Folded,
        ExperienceRecord OldAfter,
        IReadOnlyList<Guid> Live)
    {
        /// <summary>What the lookup found for the erased source before it was erased; empty when nothing was erased.</summary>
        public IReadOnlyList<Guid> FoundBeforeErasure { get; init; } = [];

        /// <summary>Whether the first source was erased before the fourth run.</summary>
        public bool Erased { get; init; }
    }

    internal static async Task<Outcome> RunAsync(
        IExperienceRecordStore store, AuthorizationContext authorization, Scope scope, Func<Guid, Task>? erase = null)
    {
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 10, 1_000, 1_000));
        var lifecycle = new ExperienceLifecycleService(store, indexingService: null, new ExperienceIndependenceOptions(), capture);
        var finalization = new ExperienceFinalizationService(
            capture,
            new DefaultExperienceReflector(),
            store,
            lifecycle,
            indexingService: null,
            indexingTimeout: null,
            provenanceSigning: null,
            reflectionSanitizer: null,
            new ExperienceFinalizationOptions { ReuseEvidence = ReuseEvidenceMode.SameTask });
        var consolidation = new ExperienceConsolidationService(store, lifecycle, new FixedClock(Now.AddDays(30)));
        var options = new ExperienceConsolidationOptions();

        var first = await LessonAsync(Now, []);
        var second = await LessonAsync(Now.AddDays(1), [first]);
        var third = await LessonAsync(Now.AddDays(2), [first]);
        List<ExperienceRecord> sources = [await ReadAsync(first.ExperienceId), second, third];

        var pass = await consolidation.ConsolidateAsync(authorization, scope, options, CancellationToken.None);
        var id = pass.Groups.Single().ConsolidatedExperienceId;
        var consolidated = await ReadAsync(id);
        var after = new List<ExperienceRecord>();
        var events = new List<LifecycleEvent>();
        foreach (var source in sources)
        {
            after.Add(await ReadAsync(source.ExperienceId));
            var history = await store.GetFirstHistoryPageAsync(authorization, scope, source.ExperienceId, CancellationToken.None);
            events.Add(history.Events[^1].Event);
        }

        var again = await consolidation.ConsolidateAsync(authorization, scope, options, CancellationToken.None);
        IReadOnlyList<Guid> foundBeforeErasure = [];
        if (erase is not null)
        {
            // A host erasing a source first finds the consolidated records that copied it, and erases those too.
            foundBeforeErasure = (await consolidation.FindConsolidatedContainingAsync(authorization, scope, sources[0].ExperienceId, CancellationToken.None)).ExperienceIds;
            await erase(sources[0].ExperienceId);
        }

        var later = await LessonAsync(Now.AddDays(10), []);
        var third2 = await consolidation.ConsolidateAsync(authorization, scope, options, CancellationToken.None);
        var folded = await ReadAsync(third2.Groups.Single().ConsolidatedExperienceId);
        var live = await store.QueryAsync(authorization, new ExperienceRecordQuery(scope, ExperienceStatuses.EligibleForReuse), CancellationToken.None);

        return new(
            sources,
            pass,
            consolidated,
            after,
            events,
            again,
            later,
            third2,
            folded,
            await ReadAsync(id),
            [.. live.Records.Select(record => record.ExperienceId)])
        {
            FoundBeforeErasure = foundBeforeErasure,
            Erased = erase is not null,
        };

        async Task<ExperienceRecord> ReadAsync(Guid experienceId) =>
            (await store.GetAsync(authorization, scope, experienceId, CancellationToken.None)).Record
            ?? throw new InvalidOperationException($"Record {experienceId} was not found.");

        async Task<ExperienceRecord> LessonAsync(DateTimeOffset at, IReadOnlyList<ExperienceRecord> exposedTo)
        {
            var runId = Guid.NewGuid();
            capture.StartRun(
                runId,
                "keep-the-build-green",
                "Keep the build green",
                scope,
                new EnvironmentFingerprint("host-1", "net10.0", "linux", "1.0.0", new Dictionary<string, string>()),
                new Provenance("consolidation-scenario", "1.0.0", at, null),
                at);
            if (exposedTo.Count > 0)
            {
                capture.RecordExposure(runId, [.. exposedTo.Select(record => new RunExposure(record.ExperienceId, record.Revision))]);
            }

            await capture.AppendAttemptAsync(
                runId,
                new AppendAttemptRequest(
                    Guid.NewGuid(),
                    at,
                    TimeSpan.FromSeconds(1),
                    [
                        new RawToolCall(Guid.NewGuid(), "run_tests", new Dictionary<string, object?>(), at, TimeSpan.FromSeconds(1), null, Timeout),
                        new RawToolCall(Guid.NewGuid(), "run_tests", new Dictionary<string, object?>(), at, TimeSpan.FromSeconds(1), "passed", null),
                    ],
                    "done",
                    null));
            await capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, at.AddMinutes(1));
            var result = await finalization.FinalizeAsync(
                new FinalizeExperienceRequest(
                    runId,
                    authorization,
                    Round,
                    [new RequiredCheck("tests", "TestResult")],
                    [new Evidence(Guid.NewGuid(), Round.RoundId, Round.ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, at)],
                    Round.ArtifactRevision,
                    StorageDecision.Permit,
                    at.AddMinutes(2)),
                CancellationToken.None);
            return result.Outcome == FinalizationOutcome.Validated
                ? result.Record!
                : throw new InvalidOperationException($"Finalization ended {result.Outcome}: {result.Reason}");
        }
    }

    /// <summary>What every store must show for the scenario.</summary>
    internal static void Verify(Outcome outcome)
    {
        var group = Assert.Single(outcome.First.Groups);
        Assert.Equal(ExperienceConsolidationGroupOutcome.Created, group.Outcome);
        Assert.Equal(3, group.IndependentRuns);
        Assert.All(group.Sources, source => Assert.True(source.Superseded));

        // One lesson, saying how many runs confirmed it, carrying the three runs' distinct confirmations: the first
        // lesson's counters (3, its own and the two later runs' support) overlap the later runs' own validations.
        var consolidated = outcome.Consolidated;
        Assert.Equal(5, outcome.Sources.Sum(source => source.SupportingValidations));
        Assert.Equal(ExperienceStatus.Validated, consolidated.Status);
        Assert.True(ExperienceConsolidation.IsConsolidated(consolidated));
        Assert.Equal(outcome.Sources.Select(source => source.ExperienceId).Order(), ExperienceConsolidation.SourcesOf(consolidated));
        Assert.Equal(3, consolidated.SupportingValidations);
        Assert.Equal(4d / 5d, consolidated.ReuseConfidence, precision: 12);
        Assert.Equal(
            "Verified in 3 independent runs (latest 2026-11-01). Failed first with TimeoutException from run_tests in 3 of them. Worked: run_tests → run_tests.",
            consolidated.Reflection!.Lesson);
        Assert.Equal(outcome.Sources[^1].Attempts.Select(attempt => attempt.AttemptId), consolidated.Attempts.Select(attempt => attempt.AttemptId));

        Assert.All(outcome.SourcesAfter, source => Assert.Equal(ExperienceStatus.Superseded, source.Status));
        Assert.All(outcome.SourcesLastEvents, last =>
        {
            Assert.Equal(ExperienceStatus.Superseded, last.CurrentStatus);
            Assert.Equal(consolidated.ExperienceId, last.ReplacementExperienceId);
            Assert.Equal(ExperienceConsolidation.Producer, last.Producer);
        });

        Assert.Empty(outcome.Second.Groups);

        var fold = Assert.Single(outcome.Third.Groups);
        Assert.Equal(ExperienceConsolidationGroupOutcome.Created, fold.Outcome);
        Assert.True(fold.Complete);
        Assert.Equal(new[] { consolidated.ExperienceId, outcome.Later.ExperienceId }.Order(), fold.Sources.Select(source => source.ExperienceId));
        Assert.All(fold.Sources, source => Assert.True(source.Superseded));

        // With the first source erased, the fold-in leaves it out: three runs, three sources. Its counted support of
        // the first lesson (the later runs' reuse evidence) went with it, and their own validations still count.
        var runs = outcome.Erased ? 3 : 4;
        Assert.Equal(runs, fold.IndependentRuns);
        Assert.Equal(runs, ExperienceConsolidation.SourcesOf(outcome.Folded).Count);
        Assert.Equal(runs, outcome.Folded.SupportingValidations);
        Assert.StartsWith($"Verified in {runs} independent runs (latest 2026-11-09).", outcome.Folded.Reflection!.Lesson, StringComparison.Ordinal);
        if (outcome.Erased)
        {
            Assert.Equal([consolidated.ExperienceId], outcome.FoundBeforeErasure);
            Assert.DoesNotContain(outcome.Sources[0].ExperienceId, ExperienceConsolidation.SourcesOf(outcome.Folded));
        }

        Assert.Equal(ExperienceStatus.Superseded, outcome.OldAfter.Status);
        Assert.Equal([outcome.Folded.ExperienceId], outcome.Live);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
