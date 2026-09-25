using AgentExperience.Core.Feedback;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 3.3 against a real PostgreSQL 16 container: the feedback ledger, its idempotency on the
/// feedback ID, the exposure rows written in the same transaction as the submission, the duplicate
/// evidence the independence rule declines to count, one record's failure leaving the rest applied, and
/// the database refusing to rewrite or remove a recorded submission. Each test uses its own random
/// tenant, so tests sharing the container never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresReuseFeedbackTests
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;
    private readonly PostgresExperienceReuseFeedbackStore _ledger;
    private readonly ExperienceLifecycleService _lifecycle;
    private readonly ExperienceReuseFeedbackService _feedback;

    public PostgresReuseFeedbackTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _ledger = new PostgresExperienceReuseFeedbackStore(fixture.DataSource);
        _lifecycle = new ExperienceLifecycleService(_store);
        _feedback = new ExperienceReuseFeedbackService(_ledger, _lifecycle);
    }

    [Fact]
    public async Task Exposure_with_no_attribution_is_durable_and_leaves_every_number_where_it_was()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]);
        var result = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Unknown, result.Benefit);
        Assert.Equal(ExperienceExposureDisposition.ExposureOnly, Assert.Single(result.Exposures).Disposition);

        // The row is there, with 'None' and 'Unknown' -- which the schema ties together -- and no
        // evidence ID, because no confidence submission happened.
        var stored = await ReadSubmissionAsync(feedback.FeedbackId);
        Assert.Equal("None", stored.AttributionSource);
        Assert.Equal("Unknown", stored.Benefit);
        Assert.Equal("Unknown", stored.ClaimedBenefit);
        Assert.Equal("task-success", stored.MeasureKind);
        Assert.Equal(1d, stored.MeasureValue);
        Assert.Equal("memory-enabled", stored.TrialLabel);

        var exposures = await ReadExposuresAsync(feedback.FeedbackId);
        Assert.Equal(record.ExperienceId, Assert.Single(exposures).ExperienceId);
        Assert.False(Assert.Single(exposures).Attributed);
        Assert.Null(Assert.Single(exposures).EvidenceId);

        // Nothing moved: not the score, not the counters, not the status.
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 2d / 3d, 1, 0, ExperienceStatus.Validated);
        Assert.Equal(0, await CountEvidenceAsync(record.ExperienceId));
    }

    [Fact]
    public async Task A_claimed_benefit_with_no_evidence_is_stored_and_still_moves_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]) with { ClaimedBenefit = ExperienceReuseBenefit.Improved };
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        var stored = await ReadSubmissionAsync(feedback.FeedbackId);
        Assert.Equal("Improved", stored.ClaimedBenefit);
        Assert.Equal("Unknown", stored.Benefit);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 2d / 3d, 1, 0, ExperienceStatus.Validated);
    }

    [Fact]
    public async Task An_authorized_human_assessment_supports_every_attributed_record_once()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var first = await ValidatedAsync(auth, scope);
        var second = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [first.ExperienceId, second.ExperienceId]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [first.ExperienceId, second.ExperienceId], "both lessons applied and the checks passed"),
        };

        var result = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Improved, result.Benefit);
        Assert.All(result.Exposures, exposure =>
        {
            Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition);
            Assert.True(exposure.Counted);
            Assert.Equal(3d / 4d, exposure.ReuseConfidence);
        });

        await AssertConfidenceAsync(auth, scope, first.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
        await AssertConfidenceAsync(auth, scope, second.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);

        // The reviewer stored on the submission is the host's principal, and the evidence rows are the
        // ones the exposures name.
        var stored = await ReadSubmissionAsync(feedback.FeedbackId);
        Assert.Equal("HumanAssessment", stored.AttributionSource);
        Assert.Equal("host-principal", stored.ReviewerIdentity);
        Assert.Null(stored.EvaluatorId);
        Assert.Null(stored.VerificationRoundId);
        // The host-established review the judgement came out of, and when it was made.
        Assert.Equal(feedback.HumanAssessment!.AssessmentId, stored.AssessmentId);
        Assert.Equal(ColumnTime, stored.AttributedAt);
        Assert.Empty(stored.EvidenceIds);

        foreach (var exposure in await ReadExposuresAsync(feedback.FeedbackId))
        {
            Assert.True(exposure.Attributed);
            Assert.Equal(
                ExperienceReuseFeedbackService.EvidenceIdFor(feedback.FeedbackId, exposure.ExperienceId),
                exposure.EvidenceId);
            Assert.Equal(1, await CountEvidenceAsync(exposure.ExperienceId));
        }
    }

    [Fact]
    public async Task A_comparative_evaluator_result_lands_as_machine_evidence_keyed_on_its_round()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]);
        var round = Guid.NewGuid();
        feedback = feedback with
        {
            ComparativeEvaluation = new(
                EvaluatorId: "baseline-comparator/1.0.0",
                RunId: feedback.RunId,
                VerificationRoundId: round,
                Benefit: ExperienceReuseBenefit.Improved,
                AttributedExperienceIds: [record.ExperienceId],
                Evidence: [new Evidence(Guid.NewGuid(), round, "rev-7", "task-success", "TestResult", CheckResult.Pass, "ci", null, PayloadTime)],
                Summary: "the memory-enabled arm passed and the baseline arm did not",
                // ColumnTime, not PayloadTime: this one is stored in a timestamptz column, which keeps
                // microseconds, so a 100 ns value would read back truncated.
                EvaluatedAt: ColumnTime),
        };

        var result = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, Assert.Single(result.Exposures).Disposition);
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);

        var stored = await ReadSubmissionAsync(feedback.FeedbackId);
        Assert.Equal("ComparativeEvaluation", stored.AttributionSource);
        Assert.Equal("baseline-comparator/1.0.0", stored.EvaluatorId);
        Assert.Equal(round, stored.VerificationRoundId);
        Assert.Null(stored.ReviewerIdentity);
        Assert.Null(stored.AssessmentId);
        // What the conclusion rested on, not only the evaluator's summary of it.
        Assert.Equal(
            feedback.ComparativeEvaluation!.Evidence.Select(evidence => evidence.EvidenceId),
            stored.EvidenceIds);
        Assert.Equal(ColumnTime, stored.AttributedAt);

        // The independence key the database generated is the machine one, over this run and this round.
        Assert.Equal(
            $"machine:{feedback.RunId:D}:{round:D}",
            await ReadIndependenceKeyAsync(ExperienceReuseFeedbackService.EvidenceIdFor(feedback.FeedbackId, record.ExperienceId)));
    }

    [Fact]
    public async Task Attributed_harm_contests_the_record_and_deletes_nothing()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]) with
        {
            RunOutcome = TaskVerificationStatus.Failed,
            HumanAssessment = Assessment(ExperienceReuseBenefit.Harmed, [record.ExperienceId], "the lesson sent the run down a dead end"),
        };

        var result = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseBenefit.Harmed, result.Benefit);
        Assert.Equal(ExperienceStatus.Contested, Assert.Single(result.Exposures).Status);

        // Contested, present, and with its whole history intact -- the reason rides on the event.
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 1d / 2d, 1, 1, ExperienceStatus.Contested);

        var history = await _store.GetFirstHistoryPageAsync(auth, scope, record.ExperienceId, CancellationToken.None);
        var contradiction = Assert.Single(history.Events, stored => stored.Event.Confidence is not null);

        Assert.Equal(ConfidenceEvidenceKind.Contradicting, contradiction.Event.Confidence!.Kind);
        Assert.Contains("harm", contradiction.Event.Reason, StringComparison.Ordinal);
        Assert.Equal("the lesson sent the run down a dead end", contradiction.Event.Confidence.Detail);
        Assert.NotNull((await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Record);
        Assert.Equal("Failed", (await ReadSubmissionAsync(feedback.FeedbackId)).RunOutcome);
    }

    [Fact]
    public async Task The_same_feedback_resubmitted_identically_writes_nothing_twice_and_counts_nothing_twice()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [record.ExperienceId], "it applied"),
        };

        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        var replay = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.AlreadyRecorded, replay.Outcome);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, Assert.Single(replay.Exposures).Disposition);

        // The whole point: the ledger has one submission, the evidence ledger one row, and the record's
        // counters moved exactly once.
        Assert.Equal(1, await CountSubmissionsAsync(feedback.FeedbackId));
        Assert.Equal(1, await CountExposuresAsync(feedback.FeedbackId));
        Assert.Equal(1, await CountEvidenceAsync(record.ExperienceId));
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
    }

    [Fact]
    public async Task The_same_feedback_ID_with_different_content_is_refused_with_nothing_written()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var other = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]);
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        // Different exposures under the same ID: a different submission, not a retry.
        var conflicting = await _feedback.RecordAsync(
            auth,
            feedback with { ExposedExperienceIds = [record.ExperienceId, other.ExperienceId] },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Conflict, conflicting.Outcome);
        // Nothing was written, and the records reported are the stored submission's, so a host whose
        // retry was refused can still see what the original named.
        Assert.Equal(record.ExperienceId, Assert.Single(conflicting.Exposures).ExperienceId);
        Assert.Equal(1, await CountExposuresAsync(feedback.FeedbackId));

        // And a different header column under the same ID is refused too.
        var relabelled = await _feedback.RecordAsync(
            auth,
            feedback with { TrialLabel = "memory-disabled" },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Conflict, relabelled.Outcome);
        Assert.Equal("memory-enabled", (await ReadSubmissionAsync(feedback.FeedbackId)).TrialLabel);
    }

    [Fact]
    public async Task Evidence_the_same_run_and_reviewer_already_produced_is_recorded_and_not_counted()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var runId = Guid.NewGuid();

        var first = Feedback(scope, [record.ExperienceId]) with
        {
            RunId = runId,
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [record.ExperienceId], "it applied"),
        };
        Assert.True(Assert.Single((await _feedback.RecordAsync(auth, first, CancellationToken.None)).Exposures).Counted);

        // A second, genuinely different submission about the same run by the same reviewer. It is stored
        // and it counts nothing: one reviewer's opinion about one run counts once.
        var second = first with
        {
            FeedbackId = Guid.NewGuid(),
            HumanAssessment = first.HumanAssessment! with { Rationale = "saying it again" },
        };

        var result = await _feedback.RecordAsync(auth, second, CancellationToken.None);
        var exposure = Assert.Single(result.Exposures);

        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition);
        Assert.False(exposure.Counted);
        Assert.Equal(2, await CountEvidenceAsync(record.ExperienceId));
        await AssertConfidenceAsync(auth, scope, record.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);

        // Both submissions are in the feedback ledger, because both happened.
        Assert.Equal(1, await CountSubmissionsAsync(first.FeedbackId));
        Assert.Equal(1, await CountSubmissionsAsync(second.FeedbackId));
    }

    [Fact]
    public async Task One_records_refusal_leaves_the_rest_applied_and_the_retry_converges()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var applied = await ValidatedAsync(auth, scope);
        var revoked = await ValidatedAsync(auth, scope);
        var missing = Guid.NewGuid();

        // Revoked between injection and feedback: it keeps its exposure and receives no submission.
        await CommitAsync(auth, scope, revoked.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 1);

        var feedback = Feedback(scope, [applied.ExperienceId, revoked.ExperienceId, missing]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [applied.ExperienceId, revoked.ExperienceId, missing], "all three were in the injected block"),
        };

        var result = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.False(result.IsRetryable);

        Assert.Equal(
            ExperienceExposureDisposition.EvidenceApplied,
            result.Exposures.Single(exposure => exposure.ExperienceId == applied.ExperienceId).Disposition);
        Assert.Equal(
            ExperienceExposureDisposition.Ineligible,
            result.Exposures.Single(exposure => exposure.ExperienceId == revoked.ExperienceId).Disposition);
        Assert.Equal(
            ExperienceExposureDisposition.Unresolved,
            result.Exposures.Single(exposure => exposure.ExperienceId == missing).Disposition);

        // The one eligible record moved; the other two wrote nothing but kept their exposure rows.
        await AssertConfidenceAsync(auth, scope, applied.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
        Assert.Equal(0, await CountEvidenceAsync(revoked.ExperienceId));
        Assert.Equal(3, await CountExposuresAsync(feedback.FeedbackId));

        // Resubmitting converges: the ledger is untouched and the counters do not move again.
        var retry = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.AlreadyRecorded, retry.Outcome);
        Assert.Equal(1, await CountEvidenceAsync(applied.ExperienceId));
        await AssertConfidenceAsync(auth, scope, applied.ExperienceId, 3d / 4d, 2, 0, ExperienceStatus.Validated);
    }

    [Fact]
    public async Task A_run_scope_outside_the_authorization_is_denied_before_anything_is_written()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]);
        var result = await _feedback.RecordAsync(Authorize(NewTenant()), feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Denied, result.Outcome);
        Assert.Equal(0, await CountSubmissionsAsync(feedback.FeedbackId));

        // And the ledger port refuses the same thing on its own, without Core in front of it.
        var direct = await _ledger.RecordAsync(
            Authorize(NewTenant()),
            Submission(feedback),
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Denied, direct.Outcome);
        Assert.Equal(0, await CountSubmissionsAsync(feedback.FeedbackId));
    }

    [Fact]
    public async Task A_recorded_submission_and_its_exposures_cannot_be_rewritten_or_removed()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [record.ExperienceId]);
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        // Promoting a recorded exposure into an attribution after the fact is exactly what the triggers
        // exist to stop: the score would move on a claim nobody evidenced.
        foreach (var sql in new[]
        {
            "UPDATE agent_experience.reuse_feedback SET benefit = 'Improved' WHERE feedback_id = @id",
            "DELETE FROM agent_experience.reuse_feedback WHERE feedback_id = @id",
            "UPDATE agent_experience.reuse_feedback_exposures SET attributed = true WHERE feedback_id = @id",
            "DELETE FROM agent_experience.reuse_feedback_exposures WHERE feedback_id = @id",
        })
        {
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql, feedback.FeedbackId));
            Assert.Equal("42501", refusal.SqlState);
        }

        Assert.Equal(1, await CountSubmissionsAsync(feedback.FeedbackId));
        Assert.Equal(1, await CountExposuresAsync(feedback.FeedbackId));
    }

    [Fact]
    public async Task The_database_refuses_a_row_that_claims_a_benefit_nothing_attributed()
    {
        // The two columns are one fact, and the CHECK is what keeps them from drifting apart -- including
        // for a writer that bypasses this package entirely.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO agent_experience.reuse_feedback (feedback_id, run_id, tenant_id, application_id, " +
            "project_id, run_outcome, claimed_benefit, benefit, attribution_source, measure_kind, " +
            "measure_value, observed_at, recorded_at) VALUES " +
            "(@id, gen_random_uuid(), 'tenant', 'app', 'project', 'Verified', 'Improved', 'Improved', " +
            "'None', 'task-success', 1, now(), now())",
            Guid.NewGuid()));

        Assert.Equal("23514", refusal.SqlState);
        Assert.Contains("benefit_needs_attribution", refusal.ConstraintName!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_database_refuses_an_exposure_whose_evidence_ID_and_attribution_disagree()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var feedback = Feedback(scope, [record.ExperienceId]);

        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        // An unattributed exposure carrying an evidence ID would claim a score moved for a record
        // nothing attributed anything to; an attributed one without it would have no submission to
        // point at. Both are refused for a writer that bypasses this package entirely.
        foreach (var (attributed, evidenceId) in new[] { ("false", "gen_random_uuid()"), ("true", "NULL") })
        {
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                "INSERT INTO agent_experience.reuse_feedback_exposures " +
                "(feedback_id, experience_id, ordinal, attributed, evidence_id) VALUES " +
                $"(@id, gen_random_uuid(), 1, {attributed}, {evidenceId})",
                feedback.FeedbackId));

            Assert.Equal("23514", refusal.SqlState);
            Assert.Contains("evidence_only_when_attributed", refusal.ConstraintName!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_deferred_foreign_key_still_binds_an_exposure_with_no_submission()
    {
        // NOT VALID skips the scan of rows that were already there; it does not stop checking new ones.
        // An exposure with no submission would be a record of what a run saw with no run attached.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO agent_experience.reuse_feedback_exposures " +
            "(feedback_id, experience_id, ordinal, attributed, evidence_id) VALUES " +
            "(@id, gen_random_uuid(), 0, false, NULL)",
            Guid.NewGuid()));

        Assert.Equal("23503", refusal.SqlState);
        Assert.Contains("submission_fkey", refusal.ConstraintName!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_database_refuses_two_exposures_sharing_one_derived_evidence_ID()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var feedback = Feedback(scope, [record.ExperienceId]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [record.ExperienceId], "it applied"),
        };

        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        var taken = ExperienceReuseFeedbackService.EvidenceIdFor(feedback.FeedbackId, record.ExperienceId);

        // The derivation is a pure function of the feedback and the record, so a collision means it was
        // bypassed -- not that two observations coincided.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO agent_experience.reuse_feedback_exposures " +
            "(feedback_id, experience_id, ordinal, attributed, evidence_id) VALUES " +
            $"(@id, gen_random_uuid(), 1, true, '{taken:D}'::uuid)",
            feedback.FeedbackId));

        Assert.Equal("23505", refusal.SqlState);
        Assert.Contains("ux_reuse_feedback_exposures_evidence", refusal.ConstraintName!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_database_refuses_an_ordinal_beyond_the_bound_Core_states()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await ValidatedAsync(auth, scope);
        var feedback = Feedback(scope, [record.ExperienceId]);

        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        // Ordinals are dense from zero and unique per submission, so this is the schema's mirror of
        // ExperienceReuseFeedback.MaxExposedRecords -- the only bound on a submission's fan-out.
        var refusal = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "INSERT INTO agent_experience.reuse_feedback_exposures " +
            "(feedback_id, experience_id, ordinal, attributed, evidence_id) VALUES " +
            $"(@id, gen_random_uuid(), {ExperienceReuseFeedback.MaxExposedRecords}, false, NULL)",
            feedback.FeedbackId));

        Assert.Equal("23514", refusal.SqlState);
        Assert.Contains("ordinal_in_range", refusal.ConstraintName!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_NUL_in_a_trial_label_is_Invalid_rather_than_a_driver_failure()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var feedback = Feedback(scope, [Guid.NewGuid()]) with { TrialLabel = "memory\u0000enabled" };

        // PostgreSQL cannot store U+0000 in text, so it has to be refused before the driver sees it.
        var result = await _feedback.RecordAsync(auth, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Path == "TrialLabel");
        Assert.Equal(0, await CountSubmissionsAsync(feedback.FeedbackId));
    }

    [Fact]
    public async Task The_same_records_in_a_different_order_converge_instead_of_colliding()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var first = await ValidatedAsync(auth, scope);
        var second = await ValidatedAsync(auth, scope);

        var feedback = Feedback(scope, [first.ExperienceId, second.ExperienceId]);
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await _feedback.RecordAsync(auth, feedback, CancellationToken.None)).Outcome);

        // A host that crashed mid-submission and retried with its records in another order must not be
        // locked out of the retry that is its only way to finish.
        var retry = await _feedback.RecordAsync(
            auth,
            feedback with { ExposedExperienceIds = [second.ExperienceId, first.ExperienceId] },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.AlreadyRecorded, retry.Outcome);
        Assert.Equal(2, await CountExposuresAsync(feedback.FeedbackId));
    }

    [Fact]
    public async Task A_malformed_submission_is_refused_by_the_ledger_itself_with_nothing_written()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var feedbackId = Guid.NewGuid();

        // A human attribution with no reviewer: the derived evidence would have no independence key, so
        // every resubmission of it would count.
        var result = await _ledger.RecordAsync(
            auth,
            new RecordedExperienceReuseFeedback(
                feedbackId,
                Guid.NewGuid(),
                scope,
                TaskVerificationStatus.Verified,
                ExperienceReuseBenefit.Unknown,
                ExperienceReuseBenefit.Improved,
                ReuseAttributionSource.HumanAssessment,
                ReviewerIdentity: null,
                EvaluatorId: null,
                VerificationRoundId: null,
                AssessmentId: null,
                Rationale: "it applied",
                EvidenceIds: [],
                AttributedAt: ColumnTime,
                new ReuseMeasure("task-success", 1),
                TrialLabel: null,
                ColumnTime,
                [new ExperienceReuseExposure(Guid.NewGuid(), Attributed: true, EvidenceId: null)]),
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Path == "ReviewerIdentity");
        Assert.Contains(result.Errors, error => error.Path == "AssessmentId");
        Assert.Contains(result.Errors, error => error.Path == "Exposures.EvidenceId");
        Assert.Equal(0, await CountSubmissionsAsync(feedbackId));
    }

    [Fact]
    public async Task An_unreachable_database_is_an_infrastructure_failure_rather_than_a_silent_success()
    {
        await using var unreachable = Unreachable();
        var offline = new PostgresExperienceReuseFeedbackStore(unreachable);
        var tenant = NewTenant();

        await Assert.ThrowsAsync<ExperienceStoreException>(() => offline.RecordAsync(
            Authorize(tenant),
            Submission(Feedback(Scope(tenant), [Guid.NewGuid()])),
            CancellationToken.None));
    }

    private static ExperienceReuseFeedback Feedback(Scope scope, IReadOnlyList<Guid> exposed) => new(
        FeedbackId: Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        Scope: scope,
        ExposedExperienceIds: exposed,
        RunOutcome: TaskVerificationStatus.Verified,
        Measure: new("task-success", 1),
        ObservedAt: ColumnTime,
        TrialLabel: "memory-enabled");

    /// <summary>
    /// A human assessment carrying the host-established review identity the shape now requires -- the
    /// one thing that keeps it from being the bare claim this story refuses from anyone else.
    /// </summary>
    private static HumanReuseAssessment Assessment(
        ExperienceReuseBenefit benefit,
        IReadOnlyList<Guid> attributed,
        string rationale,
        Guid? assessmentId = null) => new(
            assessmentId ?? Guid.NewGuid(),
            benefit,
            attributed,
            rationale,
            ColumnTime);

    /// <summary>The unattributed ledger shape of <paramref name="feedback"/>, for driving the port directly.</summary>
    private static RecordedExperienceReuseFeedback Submission(ExperienceReuseFeedback feedback) => new(
        feedback.FeedbackId,
        feedback.RunId,
        feedback.Scope,
        feedback.RunOutcome,
        feedback.ClaimedBenefit,
        ExperienceReuseBenefit.Unknown,
        ReuseAttributionSource.None,
        ReviewerIdentity: null,
        EvaluatorId: null,
        VerificationRoundId: null,
        AssessmentId: null,
        Rationale: null,
        EvidenceIds: [],
        AttributedAt: null,
        feedback.Measure,
        feedback.TrialLabel,
        feedback.ObservedAt,
        [.. feedback.ExposedExperienceIds.Select(id => new ExperienceReuseExposure(id, Attributed: false, EvidenceId: null))]);

    private async Task<ExperienceRecord> ValidatedAsync(AuthorizationContext auth, Scope scope)
    {
        var record = Minimal(scope) with
        {
            ReuseConfidence = 2d / 3d,
            SupportingValidations = 1,
            Contradictions = 0,
        };

        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        await CommitAsync(auth, scope, record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        return record;
    }

    private async Task CommitAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid experienceId,
        ExperienceStatus? prior,
        ExperienceStatus current,
        long expectedRevision)
    {
        var result = await _lifecycle.CommitAsync(
            auth,
            new CommitLifecycleTransitionRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: experienceId,
                Scope: scope,
                PriorStatus: prior,
                CurrentStatus: current,
                Reason: $"moved to {current}",
                Producer: "tests",
                OccurredAt: PayloadTime,
                ExpectedRevision: expectedRevision),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
    }

    private async Task AssertConfidenceAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid experienceId,
        double confidence,
        int supporting,
        int contradictions,
        ExperienceStatus status)
    {
        var stored = (await _store.GetAsync(auth, scope, experienceId, CancellationToken.None)).Record!;

        Assert.Equal(confidence, stored.ReuseConfidence);
        Assert.Equal(supporting, stored.SupportingValidations);
        Assert.Equal(contradictions, stored.Contradictions);
        Assert.Equal(status, stored.Status);
    }

    private async Task<(string RunOutcome, string ClaimedBenefit, string Benefit, string AttributionSource,
        string? ReviewerIdentity, string? EvaluatorId, Guid? VerificationRoundId, Guid? AssessmentId,
        Guid[] EvidenceIds, DateTimeOffset? AttributedAt, string MeasureKind, double MeasureValue,
        string? TrialLabel)> ReadSubmissionAsync(Guid feedbackId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT run_outcome, claimed_benefit, benefit, attribution_source, reviewer_identity, evaluator_id, " +
            "verification_round_id, assessment_id, evidence_ids, attributed_at, measure_kind, measure_value, trial_label " +
            "FROM agent_experience.reuse_feedback WHERE feedback_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", feedbackId));

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.IsDBNull(8) ? [] : reader.GetFieldValue<Guid[]>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetString(10),
            reader.GetDouble(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));
    }

    private async Task<List<(Guid ExperienceId, bool Attributed, Guid? EvidenceId)>> ReadExposuresAsync(Guid feedbackId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT experience_id, attributed, evidence_id FROM agent_experience.reuse_feedback_exposures " +
            "WHERE feedback_id = @id ORDER BY ordinal");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", feedbackId));

        var rows = new List<(Guid, bool, Guid?)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetGuid(2)));
        }

        return rows;
    }

    private Task<long> CountSubmissionsAsync(Guid feedbackId) =>
        CountAsync("SELECT count(*) FROM agent_experience.reuse_feedback WHERE feedback_id = @id", feedbackId);

    private Task<long> CountExposuresAsync(Guid feedbackId) =>
        CountAsync("SELECT count(*) FROM agent_experience.reuse_feedback_exposures WHERE feedback_id = @id", feedbackId);

    private Task<long> CountEvidenceAsync(Guid experienceId) =>
        CountAsync("SELECT count(*) FROM agent_experience.confidence_evidence WHERE experience_id = @id", experienceId);

    private async Task<long> CountAsync(string sql, Guid id)
    {
        await using var command = _fixture.DataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string?> ReadIndependenceKeyAsync(Guid evidenceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT independence_key FROM agent_experience.confidence_evidence WHERE evidence_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", evidenceId));
        return (string?)await command.ExecuteScalarAsync();
    }

    /// <summary>A hand-written statement, as the tables' owner: the guard under test must refuse a writer that holds the privilege.</summary>
    private async Task<int> ExecuteAsync(string sql, Guid id)
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        return await command.ExecuteNonQueryAsync();
    }
}
