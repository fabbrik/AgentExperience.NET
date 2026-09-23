using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Covers every row of Story 2.5's I/O and edge-case matrix against fakes: the happy path, an
/// unverified run, a throwing reflector, a host storage denial, a run beyond the caller's authority,
/// an unknown run, an unfinished run, a replay, and a store failure in each of the two stages that
/// touch the database. Capture and the lifecycle service are the real implementations; only the
/// record store and (where a failure is being forced) the reflector are doubles.
/// </summary>
public class ExperienceFinalizationServiceTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly DateTimeOffset Now = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:write"], Now);
    private static readonly ClosedVerificationRound Round = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), ArtifactRevision);

    private static readonly SanitizationOptions PermissiveOptions = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "query" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 3,
            MaxFieldCount: 10,
            MaxValueLength: 1_000,
            MaxFieldNameLength: 100),
        ["ToolResult"] = new SanitizationPolicy(
            AllowedFieldNames: new HashSet<string>(StringComparer.Ordinal) { "value" },
            SecretFieldNames: new HashSet<string>(StringComparer.Ordinal),
            MaxDepth: 2,
            MaxFieldCount: 5,
            MaxValueLength: 1_000,
            MaxFieldNameLength: 100),
    });

    private static Evidence PassingEvidence(string checkId = "tests", CheckResult result = CheckResult.Pass) => new(
        EvidenceId: Guid.NewGuid(),
        VerificationRoundId: Round.RoundId,
        ArtifactRevision: ArtifactRevision,
        CheckId: checkId,
        Kind: "TestResult",
        Result: result,
        Producer: "ci",
        Detail: null,
        CapturedAt: Now);

    // ---------------------------------------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verified_run_a_successful_reflection_and_a_permitting_decision_produce_a_Validated_record()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.Equal(FinalizationStage.CommitInitialEvent, result.Stage);
        Assert.True(result.IsDurable);
        Assert.Null(result.Failure);

        var record = Assert.IsType<ExperienceRecord>(result.Record);
        Assert.Equal(ExperienceStatus.Validated, record.Status);
        Assert.Equal(2d / 3d, record.ReuseConfidence);
        Assert.Equal(1, record.SupportingValidations);
        Assert.Equal(0, record.Contradictions);
        Assert.Equal(TaskVerificationStatus.Verified, record.Outcome.Status);
        Assert.NotNull(record.Reflection);
        Assert.Equal(harness.RunId, record.SourceRunId);

        // One record, at revision 1, with exactly one lifecycle event.
        Assert.Equal(1, result.Revision);

        // The record is *created* as a Candidate; the initial event performs the real transition, so a
        // commit that never lands can only ever leave a Candidate behind.
        Assert.Equal(ExperienceStatus.Candidate, Assert.Single(harness.Store.Creates).Status);

        var committed = Assert.Single(harness.Store.Commits);
        Assert.Equal(ExperienceStatus.Candidate, committed.PriorStatus);
        Assert.Equal(ExperienceStatus.Validated, committed.CurrentStatus);
        Assert.Equal(0, committed.ExpectedRevision);
        Assert.Equal(ExperienceFinalizationService.ProducerIdentity, committed.Producer);
        Assert.Equal(1, harness.Store.RevisionOf(record.ExperienceId));
        Assert.Equal(ExperienceStatus.Validated, harness.Store.StatusOf(record.ExperienceId));
    }

    [Fact]
    public async Task The_record_copies_the_captured_attempts_unchanged()
    {
        var harness = await Harness.WithCompletedRunAsync();
        var captured = harness.CapturedRun();

        var result = await harness.FinalizeAsync();

        // Finalization never sanitizes: capture already rejected anything unsafe.
        Assert.Equal(captured.Attempts, result.Record!.Attempts);
        Assert.Equal(captured.TaskId, result.Record.TaskId);
        Assert.Equal(captured.TaskDescription, result.Record.TaskSummary);
        Assert.Same(captured.Environment, result.Record.Environment);
        Assert.Same(captured.Provenance, result.Record.Provenance);
        Assert.Same(captured.Scope, result.Record.Scope);
    }

    // ---------------------------------------------------------------------------------------------
    // Unverified
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(CheckResult.Fail, TaskVerificationStatus.Failed)]
    [InlineData(CheckResult.Unknown, TaskVerificationStatus.Unknown)]
    public async Task An_unverified_run_is_quarantined_with_failure_metadata_and_no_reflection(
        CheckResult evidenceResult,
        TaskVerificationStatus expectedStatus)
    {
        var reflector = new CountingReflector();
        var harness = await Harness.WithCompletedRunAsync(reflector: reflector);

        var result = await harness.FinalizeAsync(evidence: [PassingEvidence(result: evidenceResult)]);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceStatus.Quarantined, result.Record!.Status);
        Assert.Null(result.Record.Reflection);
        Assert.Equal(0d, result.Record.ReuseConfidence);
        Assert.Equal(0, result.Record.SupportingValidations);
        Assert.Equal(expectedStatus, result.Record.Outcome.Status);

        // Safe failure metadata: which stage decided it, in content-free prose.
        var failure = Assert.IsType<FinalizationFailure>(result.Failure);
        Assert.Equal(FinalizationStage.Evaluate, failure.Stage);
        Assert.Contains(expectedStatus.ToString(), failure.Reason, StringComparison.Ordinal);

        // An unverified run is never reflected on, so no unreflected lesson can reach the record.
        Assert.Equal(0, reflector.Calls);
        Assert.Equal(ExperienceStatus.Quarantined, Assert.Single(harness.Store.Commits).CurrentStatus);
    }

    [Fact]
    public async Task A_run_with_no_closed_round_is_quarantined_rather_than_validated()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync(noRound: true);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Equal(TaskVerificationStatus.Unknown, result.Evaluation!.Outcome.Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Reflection fails
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_throwing_reflector_quarantines_the_record_names_the_stage_and_is_not_rethrown()
    {
        var harness = await Harness.WithCompletedRunAsync(reflector: new ThrowingReflector());

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Equal(ExperienceStatus.Quarantined, result.Record!.Status);
        Assert.Null(result.Record.Reflection);

        var failure = Assert.IsType<FinalizationFailure>(result.Failure);
        Assert.Equal(FinalizationStage.Reflect, failure.Stage);
        Assert.IsType<InvalidOperationException>(failure.Exception);

        // The record is still committed, and the verification it was judged against is preserved.
        Assert.Equal(TaskVerificationStatus.Verified, result.Record.Outcome.Status);
        Assert.Single(harness.Store.Commits);
        Assert.Equal(1, result.Revision);
    }

    [Fact]
    public async Task A_reflector_that_returns_no_reflection_quarantines_the_record_and_names_the_stage()
    {
        // Returning null is not the same failure mode as throwing, and it is the one a lenient custom
        // reflector is most likely to produce.
        var harness = await Harness.WithCompletedRunAsync(reflector: new NullReturningReflector());

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Equal(ExperienceStatus.Quarantined, result.Record!.Status);
        Assert.Null(result.Record.Reflection);
        Assert.Equal(FinalizationStage.Reflect, result.Failure!.Stage);
        Assert.Null(result.Failure.Exception);
    }

    // ---------------------------------------------------------------------------------------------
    // Storage denied
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_decision_that_denies_storage_writes_nothing_and_returns_a_structured_denial()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync(decision: StorageDecision.Deny("retention policy"));

        Assert.Equal(FinalizationOutcome.StorageDenied, result.Outcome);
        Assert.Equal(FinalizationStage.Authorize, result.Stage);
        Assert.False(result.IsDurable);
        Assert.Null(result.Record);
        Assert.Null(result.ExperienceId); // no record ID is issued
        Assert.Null(result.Event);
        Assert.Equal("retention policy", result.Reason);
        Assert.Empty(harness.Store.Creates);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Storage_is_denied_whatever_the_verification_says()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var denied = await harness.FinalizeAsync(
            evidence: [PassingEvidence(result: CheckResult.Fail)],
            decision: StorageDecision.Deny());

        Assert.Equal(FinalizationOutcome.StorageDenied, denied.Outcome);
        Assert.Empty(harness.Store.Creates);
    }

    // ---------------------------------------------------------------------------------------------
    // Beyond authority
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_outside_the_authorization_is_denied_before_any_store_call()
    {
        var harness = await Harness.WithCompletedRunAsync();
        var otherTenant = new AuthorizationContext("tenant-2", "host-principal", ["experience:write"], Now);

        var result = await harness.FinalizeAsync(authorization: otherTenant);

        Assert.Equal(FinalizationOutcome.NotAuthorized, result.Outcome);
        Assert.Equal(FinalizationStage.Authorize, result.Stage);
        Assert.Null(result.Record);
        Assert.Empty(harness.Store.Creates);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task A_refused_run_is_never_handed_to_the_reflector()
    {
        // IExperienceReflector is the documented seam for a model-backed reflector, so both gates run
        // before it: a run the host is about to refuse never has its content handed over.
        var denied = new CountingReflector();
        var harnessDenied = await Harness.WithCompletedRunAsync(reflector: denied);
        await harnessDenied.FinalizeAsync(decision: StorageDecision.Deny("retention policy"));
        Assert.Equal(0, denied.Calls);

        var unauthorized = new CountingReflector();
        var harnessUnauthorized = await Harness.WithCompletedRunAsync(reflector: unauthorized);
        await harnessUnauthorized.FinalizeAsync(
            authorization: new AuthorizationContext("tenant-2", "host-principal", ["experience:write"], Now));
        Assert.Equal(0, unauthorized.Calls);
    }

    // ---------------------------------------------------------------------------------------------
    // Unknown and unfinished runs
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_run_is_a_structured_not_found_result()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync(runId: Guid.NewGuid());

        Assert.Equal(FinalizationOutcome.RunNotFound, result.Outcome);
        Assert.Equal(FinalizationStage.Load, result.Stage);
        Assert.Null(result.Evaluation);
        Assert.Empty(harness.Store.Creates);
    }

    [Fact]
    public async Task A_run_with_no_execution_status_is_a_structured_invalid_result_and_writes_nothing()
    {
        var harness = Harness.Create();
        var runId = harness.StartRun();
        await harness.AppendAttemptAsync(runId);
        // Deliberately not completed.

        var result = await harness.FinalizeAsync(runId: runId);

        Assert.Equal(FinalizationOutcome.RunNotFinished, result.Outcome);
        Assert.Equal(FinalizationStage.Load, result.Stage);
        Assert.Empty(harness.Store.Creates);
        Assert.Empty(harness.Store.Commits);
    }

    // ---------------------------------------------------------------------------------------------
    // Replay
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Finalizing_the_same_run_twice_creates_one_record_at_revision_one_with_one_event()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var first = await harness.FinalizeAsync();
        var second = await harness.FinalizeAsync(finalizedAt: Now.AddMinutes(5)); // a later retry

        Assert.Equal(FinalizationOutcome.Validated, first.Outcome);
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, second.Outcome);

        // The second call reports the first call's outcome...
        Assert.Equal(first.ExperienceId, second.ExperienceId);
        Assert.Equal(ExperienceStatus.Validated, second.Status);
        Assert.Equal(1, second.Revision);
        Assert.True(second.IsDurable);

        // ...and writes nothing: no second record, no second initial event.
        Assert.Single(harness.Store.Creates);
        Assert.Single(harness.Store.Commits);
        Assert.Equal(1, harness.Store.RevisionOf(first.ExperienceId!.Value));
    }

    [Fact]
    public async Task A_quarantined_replay_still_names_the_stage_that_quarantined_it()
    {
        var harness = await Harness.WithCompletedRunAsync();
        var unverified = new[] { PassingEvidence(result: CheckResult.Fail) };

        var first = await harness.FinalizeAsync(evidence: unverified);
        Assert.Equal(FinalizationOutcome.Quarantined, first.Outcome);

        var second = await harness.FinalizeAsync(evidence: unverified, finalizedAt: Now.AddMinutes(5));

        Assert.Equal(FinalizationOutcome.AlreadyFinalized, second.Outcome);
        Assert.Equal(ExperienceStatus.Quarantined, second.Status);

        // A quarantine always names the stage that decided it, replayed or not.
        Assert.Equal(FinalizationStage.Evaluate, second.Failure!.Stage);
    }

    [Fact]
    public async Task A_resumed_commit_on_a_quarantined_record_names_the_stage_that_quarantined_it()
    {
        var harness = await Harness.WithCompletedRunAsync();
        var unverified = new[] { PassingEvidence(result: CheckResult.Fail) };
        harness.Store.ThrowOnCommit = () => new ExperienceStoreException("commit unavailable");
        await harness.FinalizeAsync(evidence: unverified);

        harness.Store.ThrowOnCommit = null;
        var resumed = await harness.FinalizeAsync(evidence: unverified, finalizedAt: Now.AddMinutes(5));

        Assert.Equal(FinalizationOutcome.Quarantined, resumed.Outcome);
        Assert.Equal(FinalizationStage.Evaluate, resumed.Failure!.Stage);
        Assert.Single(harness.Store.Commits);
    }

    [Fact]
    public async Task The_record_and_initial_event_ids_derive_from_the_run()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync();

        Assert.Equal(ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope), result.ExperienceId);
        Assert.Equal(ExperienceFinalizationService.InitialEventIdFor(harness.RunId), result.Event!.EventId);
        Assert.Equal(ExperienceFinalizationService.ReflectionIdFor(harness.RunId), result.Record!.Reflection!.ReflectionId);

        // Derivation is stable across calls and distinct per purpose and per run.
        Assert.Equal(
            ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope),
            ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope));
        Assert.NotEqual(
            ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope),
            ExperienceFinalizationService.InitialEventIdFor(harness.RunId));
        Assert.NotEqual(
            ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope),
            ExperienceFinalizationService.ExperienceIdFor(Guid.NewGuid(), TestScope));
        Assert.NotEqual(Guid.Empty, ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope));
    }

    [Fact]
    public void A_derived_record_id_is_distinct_per_scope_so_no_other_scope_can_squat_it()
    {
        var runId = Guid.NewGuid();
        var mine = ExperienceFinalizationService.ExperienceIdFor(runId, TestScope);

        // One differing field is enough, required or optional, and an absent optional field is not the
        // empty string: each of these is a different scope and must derive a different ID.
        foreach (var other in new[]
        {
            new Scope("tenant-9", "app-1", "project-1"),
            new Scope("tenant-1", "app-9", "project-1"),
            new Scope("tenant-1", "app-1", "project-9"),
            new Scope("tenant-1", "app-1", "project-1", TeamId: "team-1"),
            new Scope("tenant-1", "app-1", "project-1", AgentId: "agent-1"),
            new Scope("tenant-1", "app-1", "project-1", UserId: "user-1"),
            new Scope("tenant-1", "app-1", "project-1", TeamId: string.Empty),
        })
        {
            Assert.NotEqual(mine, ExperienceFinalizationService.ExperienceIdFor(runId, other));
        }

        // Adjacent fields cannot be re-divided into the same byte sequence, which is what the length
        // prefixes buy: ("a", "bc") and ("ab", "c") are different scopes.
        Assert.NotEqual(
            ExperienceFinalizationService.ExperienceIdFor(runId, new Scope("a", "bc", "p")),
            ExperienceFinalizationService.ExperienceIdFor(runId, new Scope("ab", "c", "p")));

        Assert.Throws<ArgumentNullException>(() => ExperienceFinalizationService.ExperienceIdFor(runId, null!));
    }

    [Fact]
    public async Task A_retry_after_a_failed_initial_commit_finishes_that_commit_rather_than_starting_over()
    {
        var harness = await Harness.WithCompletedRunAsync();
        harness.Store.ThrowOnCommit = () => new ExperienceStoreException("commit unavailable");

        var failed = await harness.FinalizeAsync();
        Assert.Equal(FinalizationOutcome.Failed, failed.Outcome);
        Assert.Equal(FinalizationStage.CommitInitialEvent, failed.Stage);

        // The record exists but is still a Candidate, so nothing can reuse it in the meantime.
        Assert.Equal(ExperienceStatus.Candidate, harness.Store.StatusOf(failed.ExperienceId!.Value));

        harness.Store.ThrowOnCommit = null;
        var retried = await harness.FinalizeAsync(finalizedAt: Now.AddMinutes(5));

        Assert.Equal(FinalizationOutcome.Validated, retried.Outcome);
        Assert.Equal(1, retried.Revision);
        Assert.Single(harness.Store.Creates); // still exactly one record
        Assert.Single(harness.Store.Commits); // and exactly one initial event
    }

    [Fact]
    public async Task Another_scope_cannot_block_a_run_by_taking_the_id_it_will_finalize_under()
    {
        var harness = await Harness.WithCompletedRunAsync();

        // The squat this test used to pin: a foreign scope writing a record under the ID the run was
        // going to finalize under, which left the run permanently unable to finalize and -- because
        // CreateAsync's conflict is deliberately scope-blind -- with no way to find out why. The ID is
        // now derived from the scope as well as the run, so a writer in another scope does not have it:
        // the ID it can derive from this run is a different one, and taking that one blocks nothing.
        var foreignScope = new Scope("tenant-9", "app-1", "project-1");
        harness.Store.Seed(TestRecord(
            ExperienceFinalizationService.ExperienceIdFor(harness.RunId, foreignScope),
            foreignScope));

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Validated, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope), result.ExperienceId);
        Assert.Single(harness.Store.Commits);
    }

    // A derived ID already taken *inside* this scope is not a squat and is not a failure: it is this
    // run's own earlier attempt, which finalization resumes rather than starting over. That path is
    // pinned by A_retry_after_a_failed_initial_commit_finishes_that_commit_rather_than_starting_over.

    // ---------------------------------------------------------------------------------------------
    // Store failures
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_store_failure_while_creating_is_a_failed_stage_and_the_run_stays_retrievable()
    {
        var harness = await Harness.WithCompletedRunAsync();
        harness.Store.ThrowOnCreate = () => new ExperienceStoreException("database unavailable");

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(FinalizationStage.CreateRecord, result.Stage);
        Assert.False(result.IsDurable);
        Assert.Null(result.Record);
        Assert.IsType<ExperienceStoreException>(result.Failure!.Exception);
        Assert.Empty(harness.Store.Commits);

        // The captured snapshot is still there for the host to retry against.
        Assert.True(harness.Capture.TryGetRun(harness.RunId, out _));
    }

    [Fact]
    public async Task A_store_failure_while_committing_is_never_reported_as_durable_success()
    {
        var harness = await Harness.WithCompletedRunAsync();
        harness.Store.ThrowOnCommit = () => new ExperienceStoreException("database unavailable");

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(FinalizationStage.CommitInitialEvent, result.Stage);
        Assert.False(result.IsDurable);
        Assert.Equal(0, result.Revision);
        Assert.IsType<ExperienceStoreException>(result.Failure!.Exception);
        Assert.True(harness.Capture.TryGetRun(harness.RunId, out _));

        // The record that now exists is reported, so the host can reconcile it rather than guess -- and
        // it is still a Candidate, so nothing can reuse it.
        Assert.Equal(ExperienceStatus.Candidate, result.Record!.Status);
        Assert.Equal(ExperienceStatus.Candidate, harness.Store.StatusOf(result.ExperienceId!.Value));
    }

    [Fact]
    public async Task A_store_that_reports_the_record_invalid_is_a_failed_stage_carrying_its_errors()
    {
        var harness = await Harness.WithCompletedRunAsync();
        harness.Store.CreateResult = new ExperienceRecordCreateResult(
            ExperienceStoreOutcome.Invalid,
            [new StoreValidationError("TaskId", "must not be empty or whitespace.")]);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(FinalizationStage.CreateRecord, result.Stage);
        Assert.Equal("TaskId", Assert.Single(result.Failure!.Errors).Path);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task A_store_that_denies_the_create_is_reported_as_not_authorized()
    {
        var harness = await Harness.WithCompletedRunAsync();
        harness.Store.CreateResult = new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, []);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.NotAuthorized, result.Outcome);
        Assert.Equal(FinalizationStage.CreateRecord, result.Stage);
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task A_lifecycle_commit_that_does_not_commit_is_a_failed_stage()
    {
        var harness = await Harness.WithCompletedRunAsync();
        harness.Store.CommitResult = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, 4, null, []);

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(FinalizationStage.CommitInitialEvent, result.Stage);
        Assert.Contains("StaleRevision", result.Failure!.Reason, StringComparison.Ordinal);
        Assert.Equal(ExperienceStatus.Candidate, result.Record!.Status);
    }

    [Fact]
    public async Task A_refused_commit_on_a_quarantined_record_still_names_the_stage_that_quarantined_it()
    {
        var harness = await Harness.WithCompletedRunAsync();
        harness.Store.CommitResult = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Invalid, 0, null, []);

        var result = await harness.FinalizeAsync(evidence: [PassingEvidence(result: CheckResult.Fail)]);

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(ExperienceStatus.Candidate, result.Record!.Status);

        // The reason the record was going to be quarantined is not dropped in favour of the commit's
        // own refusal -- the host still learns why no lesson was recorded.
        Assert.Equal(FinalizationStage.Evaluate, result.Failure!.Stage);
    }

    [Fact]
    public async Task A_record_finalized_concurrently_converges_on_AlreadyFinalized_rather_than_failing_forever()
    {
        var harness = await Harness.WithCompletedRunAsync();

        // The create lands, then someone else commits the initial event before this call's own commit,
        // so the store reports a stale revision against a record that is now durably finalized.
        harness.Store.BeforeCommit = store =>
        {
            store.BeforeCommit = null;
            store.ForceFinalize(ExperienceFinalizationService.ExperienceIdFor(harness.RunId, TestScope), ExperienceStatus.Validated);
        };

        var result = await harness.FinalizeAsync();

        Assert.Equal(FinalizationOutcome.AlreadyFinalized, result.Outcome);
        Assert.True(result.IsDurable);
        Assert.Equal(ExperienceStatus.Validated, result.Status);
        Assert.Equal(1, result.Revision);
    }

    [Fact]
    public async Task An_exception_that_is_not_an_ExperienceStoreException_is_still_a_structured_failed_stage()
    {
        // "Every stage failure comes back as a structured result" is not limited to the store's own
        // documented exception type.
        var creating = await Harness.WithCompletedRunAsync();
        creating.Store.ThrowOnCreate = () => new ObjectDisposedException("data source");
        var createResult = await creating.FinalizeAsync();
        Assert.Equal(FinalizationOutcome.Failed, createResult.Outcome);
        Assert.Equal(FinalizationStage.CreateRecord, createResult.Stage);
        Assert.IsType<ObjectDisposedException>(createResult.Failure!.Exception);

        var committing = await Harness.WithCompletedRunAsync();
        committing.Store.ThrowOnCommit = () => new ObjectDisposedException("data source");
        var commitResult = await committing.FinalizeAsync();
        Assert.Equal(FinalizationOutcome.Failed, commitResult.Outcome);
        Assert.Equal(FinalizationStage.CommitInitialEvent, commitResult.Stage);
        Assert.IsType<ObjectDisposedException>(commitResult.Failure!.Exception);

        var loading = Harness.Create(captureService: new ThrowingCaptureService());
        var loadResult = await loading.Service.FinalizeAsync(loading.Request(runId: Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(FinalizationOutcome.Failed, loadResult.Outcome);
        Assert.Equal(FinalizationStage.Load, loadResult.Stage);
        Assert.IsType<InvalidOperationException>(loadResult.Failure!.Exception);
    }

    // ---------------------------------------------------------------------------------------------
    // Evaluation ownership, arguments, cancellation
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Malformed_verification_inputs_end_the_evaluate_stage_rather_than_throwing()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync(requiredChecks: [new RequiredCheck("tests"), new RequiredCheck("tests")]);

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(FinalizationStage.Evaluate, result.Stage);
        Assert.IsType<ArgumentException>(result.Failure!.Exception);
        Assert.Empty(harness.Store.Creates);
    }

    [Fact]
    public async Task Evidence_of_a_kind_the_required_check_does_not_expect_never_validates_the_record()
    {
        var harness = await Harness.WithCompletedRunAsync();
        var approval = PassingEvidence() with { Kind = "HumanApproval" };

        var result = await harness.FinalizeAsync(
            requiredChecks: [new RequiredCheck("tests", "TestResult")],
            evidence: [approval]);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Equal(TaskVerificationStatus.Unknown, result.Record!.Outcome.Status);
    }

    [Fact]
    public async Task Evidence_from_another_round_never_finalizes_this_run_as_validated()
    {
        var harness = await Harness.WithCompletedRunAsync();
        var otherRound = PassingEvidence() with { VerificationRoundId = Guid.NewGuid() };

        var result = await harness.FinalizeAsync(evidence: [otherRound]);

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
    }

    [Fact]
    public async Task A_round_the_host_closed_for_another_artifact_revision_is_stale_and_never_validates()
    {
        var harness = await Harness.WithCompletedRunAsync();

        var result = await harness.FinalizeAsync(closedRound: new ClosedVerificationRound(Round.RoundId, "rev-2"));

        Assert.Equal(FinalizationOutcome.Quarantined, result.Outcome);
        Assert.Equal(TaskVerificationStatus.Unknown, result.Evaluation!.Outcome.Status);
    }

    [Fact]
    public async Task Null_arguments_throw_ArgumentNullException()
    {
        var harness = await Harness.WithCompletedRunAsync();

        Assert.Throws<ArgumentNullException>(() => new ExperienceFinalizationService(null!, new DefaultExperienceReflector(), harness.Store, new ExperienceLifecycleService(harness.Store)));
        Assert.Throws<ArgumentNullException>(() => new ExperienceFinalizationService(harness.Capture, null!, harness.Store, new ExperienceLifecycleService(harness.Store)));
        Assert.Throws<ArgumentNullException>(() => new ExperienceFinalizationService(harness.Capture, new DefaultExperienceReflector(), null!, new ExperienceLifecycleService(harness.Store)));
        Assert.Throws<ArgumentNullException>(() => new ExperienceFinalizationService(harness.Capture, new DefaultExperienceReflector(), harness.Store, null!));

        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.FinalizeAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.FinalizeAsync(harness.Request() with { Authorization = null! }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.FinalizeAsync(harness.Request() with { RequiredChecks = null! }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.FinalizeAsync(harness.Request() with { Evidence = null! }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Service.FinalizeAsync(harness.Request() with { StorageDecision = null! }, CancellationToken.None));
    }

    [Fact]
    public async Task A_malformed_request_is_rejected_before_anything_is_stored()
    {
        var harness = await Harness.WithCompletedRunAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.FinalizeAsync(harness.Request() with { RunId = Guid.Empty }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.FinalizeAsync(harness.Request() with { CurrentArtifactRevision = "  " }, CancellationToken.None));

        // An unset FinalizedAt would create a record whose every commit -- including every retry -- the
        // store then rejects as Invalid forever, because an unset OccurredAt is not a valid event.
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.FinalizeAsync(harness.Request() with { FinalizedAt = default }, CancellationToken.None));

        Assert.Empty(harness.Store.Creates);
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_becoming_a_structured_result()
    {
        var harness = await Harness.WithCompletedRunAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.FinalizeAsync(harness.Request(), cts.Token));
        Assert.Empty(harness.Store.Creates);
    }

    [Fact]
    public async Task A_reflector_that_cancels_for_its_own_reasons_propagates_rather_than_quarantining_silently()
    {
        // One rule for every stage: an OperationCanceledException always propagates, whoever raised it.
        var harness = await Harness.WithCompletedRunAsync(reflector: new CancellingReflector());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.FinalizeAsync(harness.Request(), CancellationToken.None));
        Assert.Empty(harness.Store.Creates);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static ExperienceRecord TestRecord(Guid experienceId, Scope scope) => new(
        ExperienceId: experienceId,
        SourceRunId: Guid.NewGuid(),
        Scope: scope,
        TaskId: "task-1",
        TaskSummary: null,
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Unknown, [], null, Now),
        CompletionScore: 0,
        Reflection: null,
        Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        Provenance: new Provenance("tests", null, Now, null),
        Status: ExperienceStatus.Candidate,
        ReuseConfidence: 0,
        SupportingValidations: 0,
        Contradictions: 0,
        Revision: 0,
        CreatedAt: Now,
        UpdatedAt: Now);

    /// <summary>Real capture plus a real lifecycle service over an in-memory fake store.</summary>
    private sealed class Harness
    {
        public required InMemoryExperienceCaptureService Capture { get; init; }

        public required FakeStore Store { get; init; }

        public required ExperienceFinalizationService Service { get; init; }

        public Guid RunId { get; private set; }

        public static Harness Create(IExperienceReflector? reflector = null, IExperienceCaptureService? captureService = null)
        {
            var capture = new InMemoryExperienceCaptureService(
                new DefaultSanitizer(PermissiveOptions),
                new CaptureLimits(50, 50, 10_000, 10_000));
            var store = new FakeStore();

            return new Harness
            {
                Capture = capture,
                Store = store,
                Service = new ExperienceFinalizationService(
                    captureService ?? capture,
                    reflector ?? new DefaultExperienceReflector(),
                    store,
                    new ExperienceLifecycleService(store)),
            };
        }

        public static async Task<Harness> WithCompletedRunAsync(IExperienceReflector? reflector = null)
        {
            var harness = Create(reflector);
            harness.RunId = harness.StartRun();
            await harness.AppendAttemptAsync(harness.RunId);
            var completed = await harness.Capture.CompleteRunAsync(harness.RunId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddMinutes(1));
            Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
            return harness;
        }

        public Guid StartRun()
        {
            var runId = Guid.NewGuid();
            RunId = runId;
            var started = Capture.StartRun(
                runId,
                taskId: "task-1",
                taskDescription: "a test task",
                scope: TestScope,
                environment: new EnvironmentFingerprint("host-1", "net10.0", "test-os", null, new Dictionary<string, string>()),
                provenance: new Provenance("unit-tests", "1.0.0", Now, null),
                startedAt: Now);
            Assert.Equal(StartRunOutcome.Started, started.Outcome);
            return runId;
        }

        public async Task AppendAttemptAsync(Guid runId)
        {
            var appended = await Capture.AppendAttemptAsync(
                runId,
                new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null));
            Assert.Equal(AppendAttemptOutcome.Recorded, appended.Outcome);
        }

        public ExperienceRun CapturedRun()
        {
            Assert.True(Capture.TryGetRun(RunId, out var run));
            return run;
        }

        public FinalizeExperienceRequest Request(
            Guid? runId = null,
            AuthorizationContext? authorization = null,
            ClosedVerificationRound? closedRound = null,
            bool noRound = false,
            IReadOnlyList<RequiredCheck>? requiredChecks = null,
            IReadOnlyList<Evidence>? evidence = null,
            StorageDecision? decision = null,
            DateTimeOffset? finalizedAt = null) => new(
                RunId: runId ?? RunId,
                Authorization: authorization ?? Authorization,
                ClosedRound: noRound ? null : closedRound ?? Round,
                RequiredChecks: requiredChecks ?? [new RequiredCheck("tests")],
                Evidence: evidence ?? [PassingEvidence()],
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: decision ?? StorageDecision.Permit,
                FinalizedAt: finalizedAt ?? Now.AddMinutes(2));

        public Task<FinalizeExperienceResult> FinalizeAsync(
            Guid? runId = null,
            AuthorizationContext? authorization = null,
            ClosedVerificationRound? closedRound = null,
            bool noRound = false,
            IReadOnlyList<RequiredCheck>? requiredChecks = null,
            IReadOnlyList<Evidence>? evidence = null,
            StorageDecision? decision = null,
            DateTimeOffset? finalizedAt = null) =>
            Service.FinalizeAsync(
                Request(runId, authorization, closedRound, noRound, requiredChecks, evidence, decision, finalizedAt),
                CancellationToken.None);
    }

    private sealed class CountingReflector : IExperienceReflector
    {
        private readonly DefaultExperienceReflector _inner = new();

        public int Calls { get; private set; }

        public Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.ReflectAsync(request, cancellationToken);
        }
    }

    private sealed class ThrowingReflector : IExperienceReflector
    {
        public Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the reflector template failed");
    }

    /// <summary>A lenient custom reflector that declines rather than throwing.</summary>
    private sealed class NullReturningReflector : IExperienceReflector
    {
        public Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<Reflection>(null!);
    }

    /// <summary>A reflector that cancels on a token of its own, not the caller's.</summary>
    private sealed class CancellingReflector : IExperienceReflector
    {
        public Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException("the reflector's own budget expired");
    }

    /// <summary>A capture service whose snapshot read fails with something other than a store exception.</summary>
    private sealed class ThrowingCaptureService : IExperienceCaptureService
    {
        public StartRunResult StartRun(Guid runId, string taskId, string? taskDescription, Scope scope, EnvironmentFingerprint environment, Provenance provenance, DateTimeOffset startedAt) =>
            throw new InvalidOperationException("Finalization must not start runs.");

        public Task<AppendAttemptResult> AppendAttemptAsync(Guid runId, AppendAttemptRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Finalization must not append attempts.");

        public Task<CompleteRunResult> CompleteRunAsync(Guid runId, Guid completionEventId, RunExecutionStatus executionStatus, DateTimeOffset endedAt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Finalization must not complete runs.");

        public bool TryGetRun(Guid runId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExperienceRun? run) =>
            throw new InvalidOperationException("the capture snapshot store is disposed");
    }

    /// <summary>
    /// A minimal in-memory <see cref="IExperienceRecordStore"/>: create-only inserts, scoped reads,
    /// and event-ID-idempotent, revision-checked lifecycle commits -- just enough of the port's real
    /// contract that replay and failure behaviour are exercised rather than assumed. Query and history
    /// are out of this story's scope and fail loudly if finalization ever calls them.
    /// </summary>
    private sealed class FakeStore : IExperienceRecordStore
    {
        private readonly Dictionary<Guid, ExperienceRecord> _records = [];
        private readonly Dictionary<Guid, (LifecycleEvent Event, long AppliedRevision)> _events = [];

        public List<ExperienceRecord> Creates { get; } = [];

        public List<LifecycleEvent> Commits { get; } = [];

        public Func<Exception>? ThrowOnCreate { get; set; }

        public Func<Exception>? ThrowOnCommit { get; set; }

        public ExperienceRecordCreateResult? CreateResult { get; set; }

        public ExperienceLifecycleCommitResult? CommitResult { get; set; }

        /// <summary>Runs just before a commit is applied, so a test can simulate a concurrent writer.</summary>
        public Action<FakeStore>? BeforeCommit { get; set; }

        public void Seed(ExperienceRecord record) => _records[record.ExperienceId] = record;

        public long RevisionOf(Guid experienceId) => _records[experienceId].Revision;

        public ExperienceStatus StatusOf(Guid experienceId) => _records[experienceId].Status;

        /// <summary>Applies someone else's initial commit to a stored record, exactly as a racing caller would.</summary>
        public void ForceFinalize(Guid experienceId, ExperienceStatus status)
        {
            var record = _records[experienceId];
            _records[experienceId] = record with { Status = status, Revision = record.Revision + 1 };
            _events[Guid.NewGuid()] = (Event(experienceId, record.Status, status, record.Revision), record.Revision + 1);
        }

        private static LifecycleEvent Event(Guid experienceId, ExperienceStatus? prior, ExperienceStatus current, long expectedRevision) =>
            new(Guid.NewGuid(), experienceId, prior, current, "concurrent finalization", "another host", Now, expectedRevision);

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            cancellationToken.ThrowIfCancellationRequested();

            if (ThrowOnCreate is not null)
            {
                throw ThrowOnCreate();
            }

            if (CreateResult is not null)
            {
                return Task.FromResult(CreateResult);
            }

            if (_records.ContainsKey(record.ExperienceId))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []));
            }

            if (!authorization.Permits(record.Scope))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, []));
            }

            _records[record.ExperienceId] = record;
            Creates.Add(record);
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, []));
        }

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            cancellationToken.ThrowIfCancellationRequested();

            // A record in another scope is indistinguishable from a missing one.
            return Task.FromResult(_records.TryGetValue(experienceId, out var record) && record.Scope == scope
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [])
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));
        }

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            cancellationToken.ThrowIfCancellationRequested();

            if (ThrowOnCommit is not null)
            {
                throw ThrowOnCommit();
            }

            BeforeCommit?.Invoke(this);

            if (CommitResult is not null)
            {
                return Task.FromResult(CommitResult);
            }

            if (_events.TryGetValue(lifecycleEvent.EventId, out var stored))
            {
                // Identical replay reports the original commit and writes nothing; any differing field conflicts.
                return Task.FromResult(stored.Event == lifecycleEvent
                    ? new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, stored.AppliedRevision, null, [])
                    : new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Conflict, 0, null, []));
            }

            if (!_records.TryGetValue(lifecycleEvent.ExperienceRecordId, out var record) || record.Scope != scope)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.NotFound, 0, null, []));
            }

            if (record.Revision != lifecycleEvent.ExpectedRevision)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, record.Revision, null, []));
            }

            if (lifecycleEvent.PriorStatus is { } prior && record.Status != prior)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, []));
            }

            var applied = lifecycleEvent.ExpectedRevision + 1;
            _records[record.ExperienceId] = record with
            {
                Status = lifecycleEvent.CurrentStatus,
                Revision = applied,
                UpdatedAt = lifecycleEvent.OccurredAt,
            };
            _events[lifecycleEvent.EventId] = (lifecycleEvent, applied);
            Commits.Add(lifecycleEvent);

            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, applied, null, []));
        }

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization must not query records.");

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization must not read history.");

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Finalization must not check supersession.");
    }
}
