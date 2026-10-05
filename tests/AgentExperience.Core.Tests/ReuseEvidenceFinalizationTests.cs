using System.Diagnostics;
using System.Reflection;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Letting verified reuse move confidence: with <see cref="ExperienceFinalizationOptions.ReuseEvidence"/> on, a run
/// finalized after it was given a lesson on the same task submits machine evidence about that lesson, bound to its
/// closed round, through the lifecycle service. One test per row of the matrix, end to end over the real capture
/// service, reflector, lifecycle service and in-memory store.
/// </summary>
public class ReuseEvidenceFinalizationTests
{
    private const string TaskOfRuns = "triage-refund";
    private const string ArtifactRevision = "rev-1";

    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:write"], Now);

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
        ["ToolResult"] = new(new HashSet<string>(StringComparer.Ordinal) { "value" }, new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
    });

    private static readonly ExperienceFinalizationOptions SameTask = new() { ReuseEvidence = ReuseEvidenceMode.SameTask };

    [Fact]
    public async Task Off_by_default_a_verified_run_that_was_given_a_lesson_submits_nothing()
    {
        var world = new World(options: null);
        var lesson = await world.LessonAsync();

        var result = await world.RunAsync(exposedTo: [lesson]);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(ReuseEvidenceMode.Off, world.Finalization.Options.ReuseEvidence);
        Assert.Empty(result.ReuseEvidence);
        var stored = await world.ReadAsync(lesson);
        Assert.Equal(1, stored.SupportingValidations);
        Assert.Equal(2d / 3d, stored.ReuseConfidence);
    }

    [Fact]
    public async Task On_the_same_task_a_verified_run_submits_supporting_evidence_and_the_lessons_confidence_rises()
    {
        var world = new World(SameTask);
        var lesson = await world.LessonAsync();

        var result = await world.RunAsync(exposedTo: [lesson]);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var applied = Assert.Single(result.ReuseEvidence);
        Assert.Equal(lesson.ExperienceId, applied.ExperienceId);
        Assert.Equal(ConfidenceEvidenceKind.Supporting, applied.Kind);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);
        Assert.True(applied.Counted);
        Assert.Null(applied.Refusal);
        Assert.Null(applied.ExceptionType);

        var stored = await world.ReadAsync(lesson);
        Assert.Equal(2, stored.SupportingValidations);
        Assert.Equal(3d / 4d, stored.ReuseConfidence, precision: 12);

        // Machine evidence, bound to this run and the round its finalization closed, under the derived identifiers.
        var history = await world.Store.GetFirstHistoryPageAsync(Authorization, TestScope, lesson.ExperienceId, CancellationToken.None);
        var evidence = history.Events[^1].Event;
        var runId = result.Record!.SourceRunId;
        Assert.Equal(ExperienceFinalizationService.ReuseEventIdFor(runId, TestScope, lesson.ExperienceId, ConfidenceEvidenceKind.Supporting), evidence.EventId);
        Assert.Equal(ExperienceFinalizationService.ReuseEvidenceProducer, evidence.Producer);
        Assert.Equal(result.Record.CreatedAt, evidence.OccurredAt);
        var update = evidence.Confidence!;
        Assert.Equal(ExperienceFinalizationService.ReuseEvidenceIdFor(runId, TestScope, lesson.ExperienceId, ConfidenceEvidenceKind.Supporting), update.EvidenceId);
        Assert.Equal(ConfidenceEvidenceSource.Machine, update.Source);
        Assert.Equal(runId, update.RunId);
        Assert.Equal(result.Record.ClosedRoundId, update.VerificationRoundId);
        Assert.Equal(ConfidenceEvidenceAdmission.Verified, update.Admission);
    }

    [Fact]
    public async Task A_lesson_on_another_task_gets_no_evidence()
    {
        var world = new World(SameTask);
        var other = await world.LessonAsync(taskId: "another-task");
        var same = await world.LessonAsync();

        var result = await world.RunAsync(exposedTo: [other, same]);

        Assert.Equal(2, result.ReuseEvidence.Count);
        var skipped = Assert.Single(result.ReuseEvidence, entry => entry.ExperienceId == other.ExperienceId);
        Assert.True(skipped.Skipped);
        Assert.Null(skipped.Outcome);
        Assert.True(Assert.Single(result.ReuseEvidence, entry => entry.ExperienceId == same.ExperienceId).Counted);
        Assert.Equal(1, (await world.ReadAsync(other)).SupportingValidations);
        Assert.Equal(2, (await world.ReadAsync(same)).SupportingValidations);
    }

    [Fact]
    public async Task A_failed_run_submits_nothing_unless_ContradictOnFailure_is_set()
    {
        var world = new World(SameTask);
        var lesson = await world.LessonAsync();

        var result = await world.RunAsync(exposedTo: [lesson], check: CheckResult.Fail);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Empty(result.ReuseEvidence);
        var stored = await world.ReadAsync(lesson);
        Assert.Equal(ExperienceStatus.Validated, stored.Status);
        Assert.Equal(0, stored.Contradictions);
    }

    [Fact]
    public async Task With_ContradictOnFailure_a_failed_run_contradicts_the_lesson_and_contests_it()
    {
        var world = new World(SameTask with { ContradictOnFailure = true });
        var lesson = await world.LessonAsync();

        var result = await world.RunAsync(exposedTo: [lesson], check: CheckResult.Fail);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        var applied = Assert.Single(result.ReuseEvidence);
        Assert.Equal(ConfidenceEvidenceKind.Contradicting, applied.Kind);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);
        Assert.True(applied.Counted);

        var stored = await world.ReadAsync(lesson);
        Assert.Equal(ExperienceStatus.Contested, stored.Status);
        Assert.Equal(1, stored.Contradictions);
        Assert.Equal(2d / 4d, stored.ReuseConfidence, precision: 12);
    }

    [Fact]
    public async Task An_unknown_verdict_submits_nothing_even_with_ContradictOnFailure()
    {
        var world = new World(SameTask with { ContradictOnFailure = true });
        var lesson = await world.LessonAsync();

        var result = await world.RunAsync(exposedTo: [lesson], check: CheckResult.Unknown);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Empty(result.ReuseEvidence);
    }

    [Fact]
    public async Task A_replayed_finalization_resubmits_the_same_evidence_and_counts_nothing_twice()
    {
        var world = new World(SameTask);
        var lesson = await world.LessonAsync();
        var first = await world.RunAsync(exposedTo: [lesson]);
        var runId = first.Record!.SourceRunId;

        var replay = await world.Finalization.FinalizeAsync(world.Request(runId, finalizedAt: Now.AddHours(1)), CancellationToken.None);

        Assert.Equal(FinalizationOutcome.AlreadyFinalized, replay.Outcome);
        var again = Assert.Single(replay.ReuseEvidence);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, again.Outcome);
        Assert.Equal(lesson.ExperienceId, again.ExperienceId);
        Assert.True(again.Replay);
        Assert.False(again.Counted);
        Assert.False(Assert.Single(first.ReuseEvidence).Replay);

        var stored = await world.ReadAsync(lesson);
        Assert.Equal(2, stored.SupportingValidations);
        Assert.Equal(3d / 4d, stored.ReuseConfidence, precision: 12);

        // One evidence event, not two.
        var history = await world.Store.GetFirstHistoryPageAsync(Authorization, TestScope, lesson.ExperienceId, CancellationToken.None);
        Assert.Single(history.Events, stored => stored.Event.Confidence is not null);
    }

    [Fact]
    public async Task A_refusal_is_reported_and_the_finalization_outcome_is_unchanged()
    {
        var world = new World(SameTask);
        var lesson = await world.LessonAsync();

        // An exposure at a revision the lesson never had: independence refuses it as not exposed.
        var result = await world.RunAsync(exposures: [new RunExposure(lesson.ExperienceId, lesson.Revision + 50)]);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        var refused = Assert.Single(result.ReuseEvidence);
        Assert.Equal(ConfidenceUpdateOutcome.Unverified, refused.Outcome);
        Assert.Equal(IndependenceRefusal.NotExposed, refused.Refusal);
        Assert.False(refused.Counted);
        Assert.NotNull(refused.Reason);
        Assert.Equal(1, (await world.ReadAsync(lesson)).SupportingValidations);
    }

    [Fact]
    public async Task A_lifecycle_call_that_throws_is_caught_and_reported_and_the_finalization_outcome_is_unchanged()
    {
        var world = new World(SameTask, new ThrowingEngine());
        var lesson = await world.LessonAsync();

        var result = await world.RunAsync(exposedTo: [lesson]);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        var failed = Assert.Single(result.ReuseEvidence);
        Assert.Null(failed.Outcome);
        Assert.Equal(typeof(InvalidOperationException).FullName, failed.ExceptionType);
        Assert.Contains(typeof(InvalidOperationException).FullName!, failed.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("engine down", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(1, (await world.ReadAsync(lesson)).SupportingValidations);
    }

    [Fact]
    public async Task Three_verified_runs_on_the_task_each_given_the_lesson_raise_its_confidence_every_time()
    {
        var world = new World(SameTask);
        var lesson = await world.LessonAsync();

        var confidences = new List<double> { (await world.ReadAsync(lesson)).ReuseConfidence };
        for (var run = 0; run < 3; run++)
        {
            var current = await world.ReadAsync(lesson);
            var result = await world.RunAsync(exposures: [new RunExposure(lesson.ExperienceId, current.Revision)]);
            Assert.True(Assert.Single(result.ReuseEvidence).Counted);
            confidences.Add((await world.ReadAsync(lesson)).ReuseConfidence);
        }

        for (var i = 1; i < confidences.Count; i++)
        {
            Assert.True(confidences[i] > confidences[i - 1], $"confidence did not rise at run {i}: {string.Join(", ", confidences)}");
        }

        Assert.Equal(5d / 6d, confidences[^1], precision: 12);
    }

    [Fact]
    public async Task A_cancellation_during_the_step_is_reported_the_list_is_truncated_and_the_record_stays_durable()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = new CallbackEngine();
        var world = new World(SameTask, engine);
        var first = await world.LessonAsync();
        var second = await world.LessonAsync();
        engine.OnScore = cancellation.Cancel;

        var result = await world.RunAsync(exposedTo: [first, second], cancellationToken: cancellation.Token);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.True(result.ReuseEvidenceTruncated);
        var last = result.ReuseEvidence[^1];
        Assert.Null(last.Outcome);
        Assert.NotNull(last.ExceptionType);
        Assert.StartsWith("Cancelled before", last.Reason, StringComparison.Ordinal);

        // The first apply cancelled the token; the step stops at the record it was on or the next one, and goes no further.
        Assert.InRange(result.ReuseEvidence.Count, 1, 2);
        Assert.Single(result.ReuseEvidence, entry => entry.ExceptionType is not null);
        Assert.Subset(new HashSet<Guid> { first.ExperienceId, second.ExperienceId }, result.ReuseEvidence.Select(entry => entry.ExperienceId).ToHashSet());
        Assert.Equal(ExperienceStatus.Validated, (await world.Store.GetAsync(Authorization, TestScope, result.ExperienceId!.Value, CancellationToken.None)).Record!.Status);
    }

    [Fact]
    public async Task The_step_is_bounded_by_its_timeout_and_a_timeout_truncates_the_list()
    {
        var engine = new CallbackEngine();
        var world = new World(SameTask with { ReuseEvidenceTimeout = TimeSpan.FromMilliseconds(1) }, engine);
        var first = await world.LessonAsync();
        var second = await world.LessonAsync();
        engine.OnScore = () => Thread.Sleep(50);

        var result = await world.RunAsync(exposedTo: [first, second]);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.ReuseEvidenceTruncated);
        Assert.StartsWith("Timed out", result.ReuseEvidence[^1].Reason, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceFinalizationOptions { ReuseEvidenceTimeout = TimeSpan.Zero });
        Assert.Equal(TimeSpan.FromSeconds(10), ExperienceFinalizationOptions.Default.ReuseEvidenceTimeout);
    }

    [Fact]
    public async Task Repeated_exposures_and_a_self_reference_are_filtered_and_the_rest_are_reported()
    {
        var world = new World(SameTask);
        var lesson = await world.LessonAsync();
        var runId = Guid.NewGuid();

        // A record in the scope that names this run as its source, written by hand: never credited.
        var sameRun = lesson with
        {
            ExperienceId = Guid.NewGuid(),
            SourceRunId = runId,
            Revision = 0,
            Status = ExperienceStatus.Candidate,
            Origin = ExperienceRecordOrigin.HostWritten,
            ProvenanceSignature = null,
        };
        Assert.Equal(ExperienceStoreOutcome.Created, (await world.Store.CreateAsync(Authorization, sameRun, CancellationToken.None)).Outcome);
        var missing = Guid.NewGuid();
        var own = ExperienceFinalizationService.ExperienceIdFor(runId, TestScope);

        var result = await world.RunAsync(
            exposures:
            [
                new RunExposure(lesson.ExperienceId, lesson.Revision),
                new RunExposure(own, 0),
                new RunExposure(sameRun.ExperienceId, 0),
                new RunExposure(missing, 0),
            ],
            runId: runId,
            moreExposures: [new RunExposure(lesson.ExperienceId, lesson.Revision)]);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        // One entry per distinct record, none for the run's own record.
        Assert.Equal(
            new HashSet<Guid> { lesson.ExperienceId, sameRun.ExperienceId, missing },
            result.ReuseEvidence.Select(entry => entry.ExperienceId).ToHashSet());
        Assert.Equal(3, result.ReuseEvidence.Count);
        var applied = Assert.Single(result.ReuseEvidence, entry => entry.ExperienceId == lesson.ExperienceId);
        Assert.True(applied.Counted);
        Assert.False(applied.Skipped);

        var fromRun = Assert.Single(result.ReuseEvidence, entry => entry.ExperienceId == sameRun.ExperienceId);
        Assert.True(fromRun.Skipped);
        Assert.Null(fromRun.Outcome);
        Assert.Contains("came from this run", fromRun.Reason, StringComparison.Ordinal);

        var notFound = Assert.Single(result.ReuseEvidence, entry => entry.ExperienceId == missing);
        Assert.True(notFound.Skipped);
        Assert.Contains(nameof(ExperienceStoreOutcome.NotFound), notFound.Reason, StringComparison.Ordinal);
        Assert.Equal(2, (await world.ReadAsync(lesson)).SupportingValidations);
    }

    [Fact]
    public async Task A_lesson_on_another_task_is_reported_as_skipped()
    {
        var world = new World(SameTask);
        var other = await world.LessonAsync(taskId: "another-task");

        var result = await world.RunAsync(exposedTo: [other]);

        var skipped = Assert.Single(result.ReuseEvidence);
        Assert.True(skipped.Skipped);
        Assert.Contains("another task", skipped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lesson_readable_only_through_a_grant_is_skipped_because_the_lending_scope_owns_its_confidence()
    {
        var inner = new InMemoryExperienceRecordStore();
        var granted = new HashSet<Guid>();
        var world = new World(SameTask, store: GrantingStore.Over(inner, granted), inMemory: inner);
        var lesson = await world.LessonAsync();
        granted.Add(lesson.ExperienceId);

        var result = await world.RunAsync(exposedTo: [lesson]);

        var skipped = Assert.Single(result.ReuseEvidence);
        Assert.True(skipped.Skipped);
        Assert.Contains("sharing grant", skipped.Reason, StringComparison.Ordinal);
        Assert.Equal(1, (await world.ReadAsync(lesson)).SupportingValidations);
    }

    [Fact]
    public async Task A_lesson_that_no_longer_accepts_evidence_is_reported_Ineligible()
    {
        var world = new World(SameTask);
        var quarantined = await world.RunAsync(check: CheckResult.Fail);
        Assert.Equal(FinalizationOutcome.Quarantined, quarantined.Outcome);

        var result = await world.RunAsync(exposedTo: [quarantined.Record!]);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        var ineligible = Assert.Single(result.ReuseEvidence);
        Assert.Equal(ConfidenceUpdateOutcome.Ineligible, ineligible.Outcome);
        Assert.False(ineligible.Counted);
    }

    [Fact]
    public async Task The_finalize_span_carries_how_many_records_were_submitted_for()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AgentExperience.Core",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);

        var world = new World(SameTask);
        var lesson = await world.LessonAsync();
        var other = await world.LessonAsync(taskId: "another-task");
        var result = await world.RunAsync(exposedTo: [lesson, other]);

        Activity span;
        lock (spans)
        {
            span = Assert.Single(spans, activity =>
                activity.OperationName == "agentexperience.finalize"
                && activity.GetTagItem("agentexperience.run_id") as string == result.Record!.SourceRunId.ToString("D"));
        }

        Assert.Equal(1, span.GetTagItem("agentexperience.reuse_evidence.submitted"));
    }

    [Fact]
    public void The_identifiers_are_pinned_to_known_answers()
    {
        // The first three were computed with the code before reuse evidence existed: the derivation change must not
        // move them. The reuse IDs follow the documented scheme (namespace, run, tag, scope fields, record, kind).
        var run = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        var record = Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678");
        var scope = new Scope("tenant-kat", "app-kat", "project-kat", "team-kat", null, "user-kat");

        Assert.Equal(Guid.Parse("f315873b-b892-8eff-9653-b74f08967da1"), ExperienceFinalizationService.ExperienceIdFor(run, scope));
        Assert.Equal(Guid.Parse("8ff9edd8-3875-8656-bbaf-26ad5eba43c1"), ExperienceFinalizationService.InitialEventIdFor(run, scope));
        Assert.Equal(Guid.Parse("0f3b59b7-7f0f-8d84-b0a2-f3353db99aba"), ExperienceFinalizationService.ReflectionIdFor(run));
        Assert.Equal(Guid.Parse("7073f81f-64d1-84de-b1ed-5b550d25a826"), ExperienceFinalizationService.ReuseEvidenceIdFor(run, scope, record, ConfidenceEvidenceKind.Supporting));
        Assert.Equal(Guid.Parse("046439e8-fcf9-8be8-b040-9f0ebdd1820f"), ExperienceFinalizationService.ReuseEventIdFor(run, scope, record, ConfidenceEvidenceKind.Supporting));
        Assert.Equal(Guid.Parse("feb345c7-99ab-8886-bf08-f2c1a1243fff"), ExperienceFinalizationService.ReuseEvidenceIdFor(run, scope, record, ConfidenceEvidenceKind.Contradicting));
        Assert.Equal(Guid.Parse("92c0c985-d5d5-8a85-ba01-4ab709882703"), ExperienceFinalizationService.ReuseEventIdFor(run, scope, record, ConfidenceEvidenceKind.Contradicting));

        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceFinalizationService.ReuseEvidenceIdFor(run, scope, record, (ConfidenceEvidenceKind)2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceFinalizationService.ReuseEventIdFor(run, scope, record, (ConfidenceEvidenceKind)2));
    }

    [Fact]
    public void An_undefined_mode_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceFinalizationOptions { ReuseEvidence = (ReuseEvidenceMode)2 });
        Assert.Equal(ReuseEvidenceMode.Off, ExperienceFinalizationOptions.Default.ReuseEvidence);
        Assert.False(ExperienceFinalizationOptions.Default.ContradictOnFailure);
    }

    [Fact]
    public void The_derived_identifiers_depend_on_the_run_the_scope_the_record_and_the_kind()
    {
        var run = Guid.NewGuid();
        var record = Guid.NewGuid();
        var supporting = ExperienceFinalizationService.ReuseEvidenceIdFor(run, TestScope, record, ConfidenceEvidenceKind.Supporting);

        Assert.Equal(supporting, ExperienceFinalizationService.ReuseEvidenceIdFor(run, TestScope, record, ConfidenceEvidenceKind.Supporting));
        Assert.Equal(supporting, ExperienceFinalizationService.ReuseEvidenceIdFor(run, TestScope with { }, record, ConfidenceEvidenceKind.Supporting));
        Assert.NotEqual(supporting, ExperienceFinalizationService.ReuseEvidenceIdFor(run, TestScope, record, ConfidenceEvidenceKind.Contradicting));
        Assert.NotEqual(supporting, ExperienceFinalizationService.ReuseEvidenceIdFor(Guid.NewGuid(), TestScope, record, ConfidenceEvidenceKind.Supporting));
        Assert.NotEqual(supporting, ExperienceFinalizationService.ReuseEvidenceIdFor(run, TestScope, Guid.NewGuid(), ConfidenceEvidenceKind.Supporting));
        Assert.NotEqual(supporting, ExperienceFinalizationService.ReuseEventIdFor(run, TestScope, record, ConfidenceEvidenceKind.Supporting));

        // Distinct per scope: a writer in another scope that knows the run and the record cannot derive either ID.
        var otherScopes = new[]
        {
            new Scope("tenant-2", "app-1", "project-1"),
            new Scope("tenant-1", "app-1", "project-2"),
            TestScope with { UserId = "user-1" },
            new Scope("tenant-1", "app-1", "project-1", TeamId: ""),
            new Scope("tenant-1a", "pp-1", "project-1"),
        };
        foreach (var other in otherScopes)
        {
            Assert.NotEqual(supporting, ExperienceFinalizationService.ReuseEvidenceIdFor(run, other, record, ConfidenceEvidenceKind.Supporting));
            Assert.NotEqual(
                ExperienceFinalizationService.ReuseEventIdFor(run, TestScope, record, ConfidenceEvidenceKind.Supporting),
                ExperienceFinalizationService.ReuseEventIdFor(run, other, record, ConfidenceEvidenceKind.Supporting));
        }

        // Neither collides with the run's own scoped IDs, and the scope is required.
        Assert.NotEqual(ExperienceFinalizationService.InitialEventIdFor(run, TestScope), ExperienceFinalizationService.ReuseEventIdFor(run, TestScope, record, ConfidenceEvidenceKind.Supporting));
        Assert.Throws<ArgumentNullException>(() => ExperienceFinalizationService.ReuseEvidenceIdFor(run, null!, record, ConfidenceEvidenceKind.Supporting));
        Assert.Throws<ArgumentNullException>(() => ExperienceFinalizationService.ReuseEventIdFor(run, null!, record, ConfidenceEvidenceKind.Supporting));
    }

    private sealed class ThrowingEngine : IExperienceConfidenceEngine
    {
        public string RuleId => "throwing";

        public string RuleVersion => "1.0";

        public double Score(ExperienceConfidenceInput input) => throw new InvalidOperationException("engine down");
    }

    /// <summary>The heuristic's score, with a hook run every time it is asked.</summary>
    private sealed class CallbackEngine : IExperienceConfidenceEngine
    {
        public Action? OnScore { get; set; }

        public string RuleId => "callback";

        public string RuleVersion => "1.0";

        public double Score(ExperienceConfidenceInput input)
        {
            OnScore?.Invoke();
            return (1d + input.SupportingValidations) / (2d + input.SupportingValidations + input.Contradictions);
        }
    }

    /// <summary>The in-memory store, except that the named records read back as shared by a grant.</summary>
    public class GrantingStore : DispatchProxy
    {
        private IExperienceRecordStore _inner = null!;
        private ISet<Guid> _granted = null!;

        public static IExperienceRecordStore Over(IExperienceRecordStore inner, ISet<Guid> granted)
        {
            var proxy = Create<IExperienceRecordStore, GrantingStore>();
            var self = (GrantingStore)(object)proxy;
            self._inner = inner;
            self._granted = granted;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(_inner, args);
            if (result is Task<ExperienceRecordGetResult> read && targetMethod.Name == nameof(IExperienceRecordStore.GetAsync))
            {
                return Mark(read);
            }

            return result;
        }

        private async Task<ExperienceRecordGetResult> Mark(Task<ExperienceRecordGetResult> read)
        {
            var result = await read;
            return result.Record is { } record && _granted.Contains(record.ExperienceId) ? result with { SharedByGrant = true } : result;
        }
    }

    private sealed class World
    {
        private static readonly Guid Round = Guid.Parse("22222222-2222-2222-2222-222222222222");

        public World(
            ExperienceFinalizationOptions? options,
            IExperienceConfidenceEngine? engine = null,
            IExperienceRecordStore? store = null,
            InMemoryExperienceRecordStore? inMemory = null)
        {
            Store = inMemory ?? new InMemoryExperienceRecordStore();
            var used = store ?? Store;
            Capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(10, 10, 1_000, 1_000));
            var lifecycle = new ExperienceLifecycleService(
                used,
                indexingService: null,
                new ExperienceIndependenceOptions(),
                Capture,
                deindexingTimeout: null,
                engine);
            Finalization = new ExperienceFinalizationService(
                Capture,
                new DefaultExperienceReflector(),
                used,
                lifecycle,
                indexingService: null,
                indexingTimeout: null,
                provenanceSigning: null,
                reflectionSanitizer: null,
                options);
        }

        public InMemoryExperienceRecordStore Store { get; }

        public InMemoryExperienceCaptureService Capture { get; }

        public ExperienceFinalizationService Finalization { get; }

        /// <summary>A verified run, given nothing, finalized into a Validated lesson.</summary>
        public async Task<ExperienceRecord> LessonAsync(string taskId = TaskOfRuns)
        {
            var result = await RunAsync(taskId: taskId);
            Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
            return result.Record!;
        }

        public Task<FinalizeExperienceResult> RunAsync(
            IReadOnlyList<ExperienceRecord>? exposedTo = null,
            CheckResult check = CheckResult.Pass,
            string taskId = TaskOfRuns,
            IReadOnlyList<RunExposure>? exposures = null,
            Guid? runId = null,
            IReadOnlyList<RunExposure>? moreExposures = null,
            CancellationToken cancellationToken = default) =>
            RunCoreAsync(
                exposures ?? [.. (exposedTo ?? []).Select(record => new RunExposure(record.ExperienceId, record.Revision))],
                check,
                taskId,
                runId ?? Guid.NewGuid(),
                moreExposures ?? [],
                cancellationToken);

        public async Task<ExperienceRecord> ReadAsync(ExperienceRecord record) =>
            (await Store.GetAsync(Authorization, TestScope, record.ExperienceId, CancellationToken.None)).Record!;

        public FinalizeExperienceRequest Request(Guid runId, CheckResult check = CheckResult.Pass, DateTimeOffset? finalizedAt = null) => new(
            RunId: runId,
            Authorization: Authorization,
            ClosedRound: new ClosedVerificationRound(Round, ArtifactRevision),
            RequiredChecks: [new RequiredCheck("tests", "TestResult")],
            Evidence: [new Evidence(Guid.NewGuid(), Round, ArtifactRevision, "tests", "TestResult", check, "ci", null, Now)],
            CurrentArtifactRevision: ArtifactRevision,
            StorageDecision: StorageDecision.Permit,
            FinalizedAt: finalizedAt ?? Now.AddMinutes(2));

        private async Task<FinalizeExperienceResult> RunCoreAsync(
            IReadOnlyList<RunExposure> exposures,
            CheckResult check,
            string taskId,
            Guid runId,
            IReadOnlyList<RunExposure> moreExposures,
            CancellationToken cancellationToken)
        {
            Assert.Equal(StartRunOutcome.Started, Capture.StartRun(
                runId,
                taskId,
                "refund a duplicate charge",
                TestScope,
                new EnvironmentFingerprint("host-1", "net10.0", "test-os", null, new Dictionary<string, string>()),
                new Provenance("unit-tests", "1.0.0", Now, null),
                Now).Outcome);

            if (exposures.Count > 0)
            {
                Assert.Equal(RecordExposureOutcome.Recorded, Capture.RecordExposure(runId, exposures).Outcome);
            }

            if (moreExposures.Count > 0)
            {
                Capture.RecordExposure(runId, moreExposures);
            }

            Assert.Equal(AppendAttemptOutcome.Recorded, (await Capture.AppendAttemptAsync(
                runId,
                new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null))).Outcome);
            Assert.Equal(CompleteRunOutcome.Recorded, (await Capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddMinutes(1))).Outcome);

            return await Finalization.FinalizeAsync(Request(runId, check), cancellationToken);
        }
    }
}
