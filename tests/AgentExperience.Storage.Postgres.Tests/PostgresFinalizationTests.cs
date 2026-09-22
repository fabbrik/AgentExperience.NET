using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 2.5 end to end against a real PostgreSQL 16 container: capture a run, finalize it through
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
        _capture = new InMemoryExperienceCaptureService(
            new DefaultSanitizer(Sanitization),
            new CaptureLimits(MaxAttemptsPerRun: 10, MaxToolCallsPerAttempt: 50, MaxResultLength: 10_000, MaxErrorLength: 10_000));
        _finalization = new ExperienceFinalizationService(
            _capture,
            new DefaultExperienceReflector(),
            _store,
            new ExperienceLifecycleService(_store));
    }

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
        var history = await _store.GetHistoryAsync(auth, scope, stored.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(1, history.Revision);
        var initial = Assert.Single(history.Events);
        Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(runId), initial.EventId);
        Assert.Equal(ExperienceStatus.Candidate, initial.PriorStatus); // the record was created as a Candidate
        Assert.Equal(ExperienceStatus.Validated, initial.CurrentStatus);
        Assert.Equal(0, initial.ExpectedRevision);
        Assert.Equal(ExperienceFinalizationService.ProducerIdentity, initial.Producer);
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

        var history = await _store.GetHistoryAsync(auth, scope, first.ExperienceId!.Value, CancellationToken.None);
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

        var history = await _store.GetHistoryAsync(auth, scope, ExperienceFinalizationService.ExperienceIdFor(runId), CancellationToken.None);
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
        Assert.Equal(ExperienceStatus.Quarantined, Assert.Single((await _store.GetHistoryAsync(auth, scope, stored.ExperienceId, CancellationToken.None)).Events).CurrentStatus);
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
        var experienceId = ExperienceFinalizationService.ExperienceIdFor(runId);
        var unconfirmed = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Candidate, unconfirmed.Status);
        Assert.Equal(0, unconfirmed.Revision);
        Assert.Empty((await _store.GetHistoryAsync(auth, scope, experienceId, CancellationToken.None)).Events);

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

        var only = Assert.Single((await _store.GetHistoryAsync(auth, scope, experienceId, CancellationToken.None)).Events);
        Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(runId), only.EventId);
        Assert.Equal(ExperienceStatus.Candidate, only.PriorStatus);
        Assert.Equal(ExperienceStatus.Validated, only.CurrentStatus);
        Assert.Equal(unconfirmed.CreatedAt, only.OccurredAt);

        // And finalizing once more is now the plain already-finalized replay.
        var again = await _finalization.FinalizeAsync(Request(runId, auth), CancellationToken.None);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, again.Outcome);
        Assert.Single((await _store.GetHistoryAsync(auth, scope, experienceId, CancellationToken.None)).Events);
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

    private async Task<Guid> CaptureRunAsync(Scope scope)
    {
        var runId = Guid.NewGuid();
        var started = _capture.StartRun(
            runId,
            taskId: "task-1",
            taskDescription: "resolve the ticket",
            scope: scope,
            environment: new EnvironmentFingerprint("worker-01", "net10.0", "linux-x64", "1.2.3", new Dictionary<string, string> { ["region"] = "us-east" }),
            provenance: new Provenance("integration-tests", "1.0.0", PayloadTime, "trace-1"),
            startedAt: PayloadTime);
        Assert.Equal(StartRunOutcome.Started, started.Outcome);

        var appended = await _capture.AppendAttemptAsync(runId, new AppendAttemptRequest(
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

        var completed = await _capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, PayloadTime.AddMinutes(1));
        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);

        return runId;
    }
}
