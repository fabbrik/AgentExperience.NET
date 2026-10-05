using AgentExperience.Tests.Shared;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 2.5 end to end against a real PostgreSQL container: capture a run, finalize it through
/// Core's <see cref="ExperienceFinalizationService"/>, and read the durable record and its lifecycle
/// history back through the real store. Nothing here is faked below the service under test -- the
/// sanitizer, the capture service, the reflector, the lifecycle service, and the PostgreSQL store are
/// all the shipping implementations. Each test uses its own random tenant, so tests sharing the
/// container never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresFinalizationTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly ClosedVerificationRound Round = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), ArtifactRevision);

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "query" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 3,
            MaxFieldCount: 10,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 10_000,
            MaxFieldNameLength: 100),
    });

    private readonly PostgresExperienceRecordStore _store;
    private readonly InMemoryExperienceCaptureService _capture;
    private readonly ExperienceFinalizationService _finalization;

    public PostgresFinalizationTests(PostgresFixture fixture)
    {
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _capture = NewCapture();
        _finalization = Finalization(_capture, _store);
    }

    private static InMemoryExperienceCaptureService NewCapture() => new(
        new DefaultSanitizer(Sanitization),
        new CaptureLimits(MaxAttemptsPerRun: 10, MaxToolCallsPerAttempt: 50, MaxResultLength: 10_000, MaxErrorLength: 10_000));

    private static ExperienceFinalizationService Finalization(
        IExperienceCaptureService capture,
        IExperienceRecordStore store,
        ExperienceLifecycleService? lifecycle = null) => new(
            capture,
            new DefaultExperienceReflector(),
            store,
            lifecycle ?? new ExperienceLifecycleService(store));

    [Fact]
    public async Task A_captured_run_finalizes_into_a_durable_record_readable_with_its_history()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        var result = await _finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(1, result.Revision);

        // Read the record back through the real store.
        var read = await _store.GetAsync(auth, scope, result.ExperienceId!.Value, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        var stored = read.Record!;

        Assert.Equal(ExperienceStatus.Validated, stored.Status);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(runId, stored.SourceRunId);
        Assert.Equal("task-1", stored.TaskId);
        Assert.Equal(2d / 3d, stored.ReuseConfidence, precision: 12);
        Assert.Equal(1, stored.SupportingValidations);
        Assert.Equal(0, stored.Contradictions);
        Assert.Equal(TaskVerificationStatus.Verified, stored.Outcome.Status);
        Assert.Equal(1.0, stored.CompletionScore);

        // The reflection round-tripped whole, and is traceable to the evidence it was derived from.
        var reflection = Assert.IsType<Reflection>(stored.Reflection);
        Assert.Equal(runId, reflection.ExperienceRunId);
        Assert.Equal(ExperienceFinalizationService.ReflectionIdFor(runId), reflection.ReflectionId);
        Assert.Equal(TaskVerificationStatus.Verified, reflection.VerificationStatus);
        Assert.Equal(Assert.Single(stored.Outcome.Evidence).EvidenceId, Assert.Single(reflection.EvidenceIds));

        // The captured attempt and its tool call came through unchanged.
        var attempt = Assert.Single(stored.Attempts);
        Assert.Equal("done", attempt.Result);
        Assert.Equal("search", Assert.Single(attempt.ToolCalls).ToolName);

        // And so did the lifecycle history: exactly one initial event.
        var history = await _store.GetFirstHistoryPageAsync(auth, scope, stored.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(1, history.Revision);
        var stamped = Assert.Single(history.Events);
        var initial = stamped.Event;
        Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(runId, scope), initial.EventId);
        Assert.Equal(ExperienceStatus.Candidate, initial.PriorStatus); // the record was created as a Candidate
        Assert.Equal(ExperienceStatus.Validated, initial.CurrentStatus);
        Assert.Equal(0, initial.ExpectedRevision);
        Assert.Null(initial.ReplacementExperienceId);
        Assert.Equal(ExperienceFinalizationService.ProducerIdentity, initial.Producer);
        Assert.Equal(1, stamped.AppliedRevision);
        Assert.True(stamped.RecordedAt >= initial.OccurredAt);
    }

    [Fact]
    public async Task Finalizing_the_same_run_twice_leaves_one_record_at_revision_one_with_one_event()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        var first = await _finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);
        var second = await _finalization.FinalizeAsync(
            Request(runId, auth) with { FinalizedAt = DateTimeOffset.UtcNow.AddMinutes(10) },
            CancellationToken.None);

        Assert.Equal(FinalizationOutcome.Validated, first.Outcome);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, second.Outcome);
        Assert.Equal(first.ExperienceId, second.ExperienceId);
        Assert.Equal(ExperienceStatus.Validated, second.Status);
        Assert.Equal(1, second.Revision);

        var records = await _store.QueryAsync(auth, new ExperienceRecordQuery(scope), CancellationToken.None);
        Assert.Single(records.Records);

        var history = await _store.GetFirstHistoryPageAsync(auth, scope, first.ExperienceId!.Value, CancellationToken.None);
        Assert.Equal(1, history.Revision);
        Assert.Single(history.Events);
    }

    [Fact]
    public async Task A_denied_storage_decision_leaves_no_record_and_no_event_for_the_run()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        var result = await _finalization.FinalizeAsync(
            Request(runId, auth) with { StorageDecision = StorageDecision.Deny("host retention policy") },
            CancellationToken.None);

        Assert.Equal(FinalizationOutcome.StorageDenied, result.Outcome);
        Assert.Null(result.ExperienceId);
        Assert.Equal("host retention policy", result.Reason);

        var records = await _store.QueryAsync(auth, new ExperienceRecordQuery(scope), CancellationToken.None);
        Assert.Empty(records.Records);

        var history = await _store.GetFirstHistoryPageAsync(auth, scope, ExperienceFinalizationService.ExperienceIdFor(runId, scope), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, history.Outcome);
    }

    [Fact]
    public async Task An_unverified_run_finalizes_into_a_quarantined_record_with_no_reflection()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        var result = await _finalization.FinalizeAsync(
            Request(runId, auth) with { Evidence = [Evidence(CheckResult.Fail)] },
            CancellationToken.None);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);

        var stored = (await _store.GetAsync(auth, scope, result.ExperienceId!.Value, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Quarantined, stored.Status);
        Assert.Null(stored.Reflection);
        Assert.Equal(0d, stored.ReuseConfidence);
        Assert.Equal(TaskVerificationStatus.Failed, stored.Outcome.Status);
        Assert.Equal(ExperienceStatus.Quarantined, Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, stored.ExperienceId, CancellationToken.None)).Events).Event.CurrentStatus);
    }

    [Fact]
    public async Task A_store_failure_during_finalization_is_never_reported_as_durable_success()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        await using var unreachable = Unreachable();
        var offline = new ExperienceFinalizationService(
            _capture,
            new DefaultExperienceReflector(),
            new PostgresExperienceRecordStore(unreachable),
            new ExperienceLifecycleService(new PostgresExperienceRecordStore(unreachable)));

        var result = await offline.FinalizeAsync(Request(runId, auth), CancellationToken.None);

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(FinalizationStage.CreateRecord, result.Stage);
        Assert.False(result.IsDurable);
        Assert.IsType<ExperienceStoreException>(result.Failure!.Exception);

        // The captured snapshot is still available for the host to retry with -- and the retry, against
        // a reachable database, succeeds.
        Assert.True(_capture.TryGetRun(runId, out _));
        var retried = await _finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);
        Assert.Equal(FinalizationOutcome.Validated, retried.Outcome);
    }

    [Fact]
    public async Task A_record_created_but_never_confirmed_stays_a_Candidate_and_a_retry_completes_its_commit()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        // The create lands against the real database, but the commit cannot: the lifecycle service is
        // pointed at an unreachable one.
        await using var unreachable = Unreachable();
        var halfway = new ExperienceFinalizationService(
            _capture,
            new DefaultExperienceReflector(),
            _store,
            new ExperienceLifecycleService(new PostgresExperienceRecordStore(unreachable)));

        var interrupted = await halfway.FinalizeAsync(Request(runId, auth), CancellationToken.None);
        Assert.Equal(FinalizationOutcome.Failed, interrupted.Outcome);
        Assert.Equal(FinalizationStage.CommitInitialEvent, interrupted.Stage);
        Assert.False(interrupted.IsDurable);

        // What is stored is a Candidate at revision 0 with no history -- never a reusable record.
        var experienceId = ExperienceFinalizationService.ExperienceIdFor(runId, scope);
        var unconfirmed = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Candidate, unconfirmed.Status);
        Assert.Equal(0, unconfirmed.Revision);
        Assert.Empty((await _store.GetFirstHistoryPageAsync(auth, scope, experienceId, CancellationToken.None)).Events);

        // The retry re-derives the very same initial event -- including its OccurredAt, which has been
        // through PostgreSQL's microsecond truncation on the way back out -- and finishes that commit.
        var retried = await _finalization.FinalizeAsync(
            Request(runId, auth) with { FinalizedAt = ColumnTime.AddMinutes(30) },
            CancellationToken.None);

        Assert.Equal(FinalizationOutcome.Validated, retried.Outcome);
        Assert.Equal(1, retried.Revision);

        var confirmed = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Validated, confirmed.Status);
        Assert.Equal(1, confirmed.Revision);
        Assert.Equal(unconfirmed.CreatedAt, confirmed.CreatedAt); // the first call's timestamp, not the retry's

        var only = Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, experienceId, CancellationToken.None)).Events).Event;
        Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(runId, scope), only.EventId);
        Assert.Equal(ExperienceStatus.Candidate, only.PriorStatus);
        Assert.Equal(ExperienceStatus.Validated, only.CurrentStatus);
        Assert.Equal(unconfirmed.CreatedAt, only.OccurredAt);

        // And finalizing once more is now the plain already-finalized replay.
        var again = await _finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, again.Outcome);
        Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, experienceId, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task The_same_run_id_finalized_in_two_scopes_commits_under_two_different_initial_event_ids()
    {
        var runId = Guid.NewGuid();
        var results = new List<(Scope Scope, AuthorizationContext Auth, FinalizeExperienceResult Result)>();
        foreach (var tenant in new[] { NewTenant(), NewTenant() })
        {
            var scope = Scope(tenant);
            var auth = Authorize(tenant);
            var capture = NewCapture();
            await CaptureRunAsync(scope, runId, capture);
            var finalization = Finalization(capture, _store);
            results.Add((scope, auth, await finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None)));
        }

        foreach (var (scope, auth, result) in results)
        {
            Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
            var only = Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, result.ExperienceId!.Value, CancellationToken.None)).Events).Event;
            Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(runId, scope), only.EventId);
        }

        Assert.NotEqual(results[0].Result.Event!.EventId, results[1].Result.Event!.EventId);
    }

    [Fact]
    public async Task Another_scope_committing_events_under_this_runs_initial_event_ids_cannot_block_it()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        // lifecycle_events_pkey is global. Another tenant takes the run-only ID earlier releases derived,
        // and the ID it can derive for this run in its own scope, with events on a record of its own.
        var foreignTenant = NewTenant();
        var foreignAuth = Authorize(foreignTenant);
        var foreignScope = Scope(foreignTenant);
        var squatEvents = new[] { LegacyInitialEventIds.For(runId), ExperienceFinalizationService.InitialEventIdFor(runId, foreignScope) };
        foreach (var eventId in squatEvents)
        {
            var foreign = Minimal(foreignScope);
            Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(foreignAuth, foreign, CancellationToken.None)).Outcome);
            var squat = await _store.CommitLifecycleEventAsync(
                foreignAuth,
                foreignScope,
                Event(foreign.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Quarantined, 0, eventId),
                CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Committed, squat.Outcome);
        }

        var result = await _finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        var only = Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, result.ExperienceId!.Value, CancellationToken.None)).Events).Event;
        Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(runId, scope), only.EventId);
    }

    [Fact]
    public async Task A_record_an_earlier_release_finalized_under_the_run_only_event_id_replays_as_AlreadyFinalized()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);
        var experienceId = await CreateUnconfirmedAsync(runId, auth);

        // What an earlier release committed: the identical initial event, under the run-only ID.
        var record = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;
        var legacy = await _store.CommitLifecycleEventAsync(auth, scope, LegacyInitialEvent(runId, record), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, legacy.Outcome);

        var replay = await _finalization.FinalizeAsync(
            Request(runId, auth) with { FinalizedAt = ColumnTime.AddMinutes(30) },
            CancellationToken.None);

        Assert.Equal(FinalizationOutcome.AlreadyFinalized, replay.Outcome);
        Assert.True(replay.IsDurable);
        Assert.Equal(ExperienceStatus.Validated, replay.Status);
        Assert.Equal(1, replay.Revision);
        var only = Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, experienceId, CancellationToken.None)).Events).Event;
        Assert.Equal(LegacyInitialEventIds.For(runId), only.EventId);
    }

    [Fact]
    public async Task An_earlier_releases_initial_commit_landing_first_converges_on_AlreadyFinalized()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);
        var experienceId = await CreateUnconfirmedAsync(runId, auth);

        // An earlier release's retry commits the initial event under the run-only ID between this
        // retry's read of the Candidate and its own commit, which the store then refuses as stale.
        ExperienceStoreOutcome? landed = null;
        var racing = new RacingStore(_store, async (authorization, commitScope, mine) =>
        {
            landed = (await _store.CommitLifecycleEventAsync(
                authorization,
                commitScope,
                mine with { EventId = LegacyInitialEventIds.For(runId) },
                CancellationToken.None)).Outcome;
        });
        var finalization = Finalization(_capture, _store, new ExperienceLifecycleService(racing));

        var result = await finalization.FinalizeAsync(
            Request(runId, auth) with { FinalizedAt = ColumnTime.AddMinutes(30) },
            CancellationToken.None);

        // Asserted here, not inside the store, where a failed assertion would surface as a port failure.
        Assert.Equal(ExperienceStoreOutcome.Committed, landed);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceStatus.Validated, result.Status);
        Assert.Equal(1, result.Revision);
        var only = Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, experienceId, CancellationToken.None)).Events).Event;
        Assert.Equal(LegacyInitialEventIds.For(runId), only.EventId);
    }

    [Fact]
    public async Task A_Candidate_an_earlier_release_left_unconfirmed_whose_run_only_event_id_another_scope_took_is_confirmed_under_the_scoped_id()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var runId = await CaptureRunAsync(scope);

        // Seeded as 0.1.0-preview.6 left it, not created through current code: a Candidate at revision 0
        // under this run's record ID (whose derivation did not change), its initial commit never landed.
        var experienceId = ExperienceFinalizationService.ExperienceIdFor(runId, scope);
        var seeded = Minimal(scope, experienceId) with { SourceRunId = runId };
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, seeded, CancellationToken.None)).Outcome);

        // Meanwhile another tenant took the run-only initial event ID that release would commit under.
        var foreignTenant = NewTenant();
        var foreignAuth = Authorize(foreignTenant);
        var foreign = Minimal(Scope(foreignTenant));
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(foreignAuth, foreign, CancellationToken.None)).Outcome);
        var squat = await _store.CommitLifecycleEventAsync(
            foreignAuth,
            foreign.Scope,
            Event(foreign.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Quarantined, 0, LegacyInitialEventIds.For(runId)),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, squat.Outcome);

        var resumed = await _finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);

        // The seeded record carries no reflection, so its initial event quarantines it.
        Assert.Equal(FinalizationOutcome.Quarantined, resumed.Outcome);
        Assert.True(resumed.IsDurable);
        Assert.Equal(1, resumed.Revision);
        var only = Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, experienceId, CancellationToken.None)).Events).Event;
        Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(runId, scope), only.EventId);
    }

    /// <summary>Creates the run's record and leaves it at revision 0, as an interrupted earlier call did.</summary>
    private async Task<Guid> CreateUnconfirmedAsync(Guid runId, AuthorizationContext auth)
    {
        await using var unreachable = Unreachable();
        var halfway = Finalization(_capture, _store, new ExperienceLifecycleService(new PostgresExperienceRecordStore(unreachable)));
        var interrupted = await halfway.FinalizeAsync(Request(runId, auth), CancellationToken.None);
        Assert.Equal(FinalizationStage.CommitInitialEvent, interrupted.Stage);
        Assert.Equal(0, interrupted.Revision);
        return interrupted.ExperienceId!.Value;
    }

    /// <summary>The initial event an earlier release committed for <paramref name="record"/>: identical but for its ID.</summary>
    private static LifecycleEvent LegacyInitialEvent(Guid runId, ExperienceRecord record) => new(
        EventId: LegacyInitialEventIds.For(runId),
        ExperienceRecordId: record.ExperienceId,
        PriorStatus: ExperienceStatus.Candidate,
        CurrentStatus: ExperienceStatus.Validated,
        Reason: string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "Initial finalization: verification {0}, completion score {1}, reflection recorded.",
            record.Outcome.Status,
            record.CompletionScore.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        Producer: ExperienceFinalizationService.ProducerIdentity,
        OccurredAt: record.CreatedAt,
        ExpectedRevision: 0);

    [Fact]
    public async Task Under_ReuseEvidence_three_verified_runs_given_a_lesson_on_its_task_raise_its_confidence_every_time()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var capture = NewCapture();
        var finalization = new ExperienceFinalizationService(
            capture,
            new DefaultExperienceReflector(),
            _store,
            new ExperienceLifecycleService(_store, indexingService: null, new AgentExperience.Core.Confidence.ExperienceIndependenceOptions(), capture),
            indexingService: null,
            indexingTimeout: null,
            provenanceSigning: null,
            reflectionSanitizer: null,
            new ExperienceFinalizationOptions { ReuseEvidence = ReuseEvidenceMode.SameTask });

        var lessonRun = await CaptureRunAsync(scope, capture: capture);
        var lesson = await finalization.FinalizeAsync(Request(lessonRun, auth), CancellationToken.None);
        Assert.Equal(FinalizationOutcome.Validated, lesson.Outcome);
        Assert.Empty(lesson.ReuseEvidence);
        var lessonId = lesson.ExperienceId!.Value;

        var confidences = new List<double> { lesson.Record!.ReuseConfidence };
        FinalizeExperienceResult? last = null;
        for (var run = 0; run < 3; run++)
        {
            var current = (await _store.GetAsync(auth, scope, lessonId, CancellationToken.None)).Record!;
            var runId = await CaptureRunAsync(scope, capture: capture, exposures: [new RunExposure(lessonId, current.Revision)]);
            last = await finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);

            Assert.Equal(FinalizationOutcome.Validated, last.Outcome);
            var applied = Assert.Single(last.ReuseEvidence);
            Assert.Equal(lessonId, applied.ExperienceId);
            Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);
            Assert.True(applied.Counted);
            confidences.Add((await _store.GetAsync(auth, scope, lessonId, CancellationToken.None)).Record!.ReuseConfidence);
        }

        for (var i = 1; i < confidences.Count; i++)
        {
            Assert.True(confidences[i] > confidences[i - 1], $"confidence did not rise at run {i}: {string.Join(", ", confidences)}");
        }

        Assert.Equal(5d / 6d, confidences[^1], precision: 12);

        // A replay of the last run resubmits the same evidence: reported, not counted again.
        var replay = await finalization.FinalizeAsync(Request(last!.Record!.SourceRunId, auth), CancellationToken.None);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, replay.Outcome);
        var replayed = Assert.Single(replay.ReuseEvidence);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, replayed.Outcome);
        Assert.True(replayed.Replay);
        Assert.False(replayed.Counted);
        var stored = (await _store.GetAsync(auth, scope, lessonId, CancellationToken.None)).Record!;
        Assert.Equal(4, stored.SupportingValidations);
        Assert.Equal(5d / 6d, stored.ReuseConfidence, precision: 12);
    }

    private static Evidence Evidence(CheckResult result) => new(
        EvidenceId: Guid.NewGuid(),
        VerificationRoundId: Round.RoundId,
        ArtifactRevision: ArtifactRevision,
        CheckId: "unit-tests-pass",
        Kind: "TestResult",
        Result: result,
        Producer: "ci",
        Detail: "42 of 42 passed",
        CapturedAt: PayloadTime);

    private static FinalizeExperienceRequest Request(Guid runId, AuthorizationContext auth) => new(
        RunId: runId,
        Authorization: auth,
        ClosedRound: Round,
        RequiredChecks: [new RequiredCheck("unit-tests-pass", "TestResult")],
        Evidence: [Evidence(CheckResult.Pass)],
        CurrentArtifactRevision: ArtifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: ColumnTime);

    private async Task<Guid> CaptureRunAsync(
        Scope scope,
        Guid? fixedRunId = null,
        InMemoryExperienceCaptureService? capture = null,
        IReadOnlyList<RunExposure>? exposures = null)
    {
        var runId = fixedRunId ?? Guid.NewGuid();
        capture ??= _capture;
        var started = capture.StartRun(
            runId,
            taskId: "task-1",
            taskDescription: "resolve the ticket",
            scope: scope,
            environment: new EnvironmentFingerprint("worker-01", "net10.0", "linux-x64", "1.2.3", new Dictionary<string, string> { ["region"] = "us-east" }),
            provenance: new Provenance("integration-tests", "1.0.0", PayloadTime, "trace-1"),
            startedAt: PayloadTime);
        Assert.Equal(StartRunOutcome.Started, started.Outcome);

        if (exposures is { Count: > 0 })
        {
            Assert.Equal(RecordExposureOutcome.Recorded, capture.RecordExposure(runId, exposures).Outcome);
        }

        var appended = await capture.AppendAttemptAsync(runId, new AppendAttemptRequest(
            AttemptId: Guid.NewGuid(),
            StartedAt: PayloadTime,
            Duration: TimeSpan.FromSeconds(2),
            ToolCalls:
            [
                new RawToolCall(
                    ToolCallId: Guid.NewGuid(),
                    ToolName: "search",
                    Arguments: new Dictionary<string, object?> { ["query"] = "refund policy" },
                    StartedAt: PayloadTime,
                    Duration: TimeSpan.FromMilliseconds(120),
                    Result: "3 documents",
                    Error: null),
            ],
            Result: "done",
            Error: null));
        Assert.Equal(AppendAttemptOutcome.Recorded, appended.Outcome);

        var completed = await capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, PayloadTime.AddMinutes(1));
        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);

        return runId;
    }

    /// <summary>A store that runs <paramref name="racer"/> once, just before the first lifecycle commit it is handed, and is otherwise the inner store.</summary>
    private sealed class RacingStore(IExperienceRecordStore inner, Func<AuthorizationContext, Scope, LifecycleEvent, Task> racer) : IExperienceRecordStore
    {
        private Func<AuthorizationContext, Scope, LifecycleEvent, Task>? _racer = racer;

        public async Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _racer, null) is { } race)
            {
                await race(authorization, scope, lifecycleEvent);
            }

            return await inner.CommitLifecycleEventAsync(authorization, scope, lifecycleEvent, cancellationToken);
        }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            inner.CreateAsync(authorization, record, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            inner.GetAsync(authorization, scope, experienceId, cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            inner.GetAsync(authorization, scope, experienceId, options, cancellationToken);

        public Task<ExperienceRecordGetManyResult> GetManyAsync(AuthorizationContext authorization, Scope scope, IReadOnlyList<Guid> experienceIds, ExperienceReadOptions options, CancellationToken cancellationToken) =>
            inner.GetManyAsync(authorization, scope, experienceIds, options, cancellationToken);

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            inner.QueryAsync(authorization, query, cancellationToken);

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            inner.GetHistoryAsync(authorization, query, cancellationToken);

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            inner.CheckSupersessionAsync(authorization, scope, experienceId, replacementExperienceId, cancellationToken);
    }
}
