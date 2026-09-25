using AgentExperience.Core.Confidence;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 3.3: recording what happened in a run that stored experience was injected into. One test per
/// row of the story's I/O matrix, against fakes, plus the derivation that makes a retry converge.
/// </summary>
/// <remarks>
/// The claim these tests exist to protect is the negative one: exposure is not attribution. Almost every
/// row below ends with nothing having moved, and that is the correct behaviour rather than a gap.
/// </remarks>
public class ExperienceReuseFeedbackServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "principal-7", ["experience:write"], Now);

    [Fact]
    public async Task Exposure_with_no_attribution_is_recorded_as_Unknown_and_moves_nothing()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));

        var result = await service.RecordAsync(Authorization, Feedback([experienceId]), CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Unknown, result.Benefit);
        Assert.Equal(ReuseAttributionSource.None, result.AttributionSource);
        Assert.False(result.IsRetryable);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(experienceId, exposure.ExperienceId);
        Assert.Equal(ExperienceExposureDisposition.ExposureOnly, exposure.Disposition);
        Assert.Null(exposure.EvidenceId);
        Assert.False(exposure.Counted);

        // The exposure is durable; the record is untouched. Not one read, not one commit.
        var stored = Assert.Single(ledger.Submissions);
        Assert.Equal(ExperienceReuseBenefit.Unknown, stored.Benefit);
        Assert.False(Assert.Single(stored.Exposures).Attributed);
        Assert.Null(Assert.Single(stored.Exposures).EvidenceId);
        Assert.Empty(records.Commits);
        Assert.False(records.Reads);
    }

    [Fact]
    public async Task A_caller_claiming_improvement_with_no_evidence_is_recorded_as_Unknown()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with { ClaimedBenefit = ExperienceReuseBenefit.Improved },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);

        // The claim is kept -- it is data about the caller -- and it is not the benefit.
        Assert.Equal(ExperienceReuseBenefit.Improved, Assert.Single(ledger.Submissions).ClaimedBenefit);
        Assert.Equal(ExperienceReuseBenefit.Unknown, Assert.Single(ledger.Submissions).Benefit);
        Assert.Equal(ExperienceReuseBenefit.Unknown, result.Benefit);
        Assert.Equal(ExperienceExposureDisposition.ExposureOnly, Assert.Single(result.Exposures).Disposition);
        Assert.Empty(records.Commits);
    }

    [Fact]
    public async Task An_authorized_human_assessment_supports_each_attributed_record_exactly_once()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));
        var feedback = Feedback([experienceId]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "the retry-after-lock lesson applied"),
        };

        var result = await service.RecordAsync(Authorization, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Improved, result.Benefit);
        Assert.Equal(ReuseAttributionSource.HumanAssessment, result.AttributionSource);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition);
        Assert.True(exposure.Counted);
        Assert.Equal(3d / 4d, exposure.ReuseConfidence);
        Assert.Equal(ExperienceStatus.Validated, exposure.Status);

        var confidence = Assert.Single(records.Commits).Event.Confidence;
        Assert.NotNull(confidence);
        Assert.Equal(ConfidenceEvidenceKind.Supporting, confidence.Kind);
        // The reviewer is the host's principal, never a field the submission got to name.
        Assert.Equal(ConfidenceEvidenceSource.Human, confidence.Source);
        Assert.Equal("principal-7", confidence.ReviewerIdentity);
        Assert.Null(confidence.VerificationRoundId);
        Assert.Equal(feedback.RunId, confidence.RunId);

        // The ledger names the same evidence the confidence path was handed.
        Assert.Equal(confidence.EvidenceId, Assert.Single(Assert.Single(ledger.Submissions).Exposures).EvidenceId);
    }

    [Fact]
    public async Task A_comparative_evaluator_result_supports_each_attributed_record_as_machine_evidence()
    {
        var experienceId = Guid.NewGuid();
        var (service, _, records) = Build(Validated(experienceId));
        var feedback = Feedback([experienceId]);
        var round = Guid.NewGuid();
        feedback = feedback with { ComparativeEvaluation = Comparative(feedback.RunId, round, [experienceId], ExperienceReuseBenefit.Improved) };

        var result = await service.RecordAsync(Authorization, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ReuseAttributionSource.ComparativeEvaluation, result.AttributionSource);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, Assert.Single(result.Exposures).Disposition);

        var confidence = Assert.Single(records.Commits).Event.Confidence;
        Assert.NotNull(confidence);
        Assert.Equal(ConfidenceEvidenceKind.Supporting, confidence.Kind);
        Assert.Equal(ConfidenceEvidenceSource.Machine, confidence.Source);
        Assert.Equal(round, confidence.VerificationRoundId);
        Assert.Null(confidence.ReviewerIdentity);
        Assert.Equal("baseline-comparator/1.0.0", Assert.Single(records.Commits).Event.Producer);
    }

    [Fact]
    public async Task Attributed_harm_contradicts_and_contests_each_record_without_deleting_anything()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));
        var feedback = Feedback([experienceId]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Harmed, [experienceId], "the lesson sent the run down a dead end"),
        };

        var result = await service.RecordAsync(Authorization, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseBenefit.Harmed, result.Benefit);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition);
        Assert.True(exposure.Counted);
        // Status, not score, is what takes it out of reuse -- and the record is still there.
        Assert.Equal(ExperienceStatus.Contested, exposure.Status);
        Assert.Equal(1d / 2d, exposure.ReuseConfidence);
        Assert.NotNull(records.Record);

        var committed = Assert.Single(records.Commits).Event;
        Assert.Equal(ConfidenceEvidenceKind.Contradicting, committed.Confidence!.Kind);
        Assert.Equal(ExperienceStatus.Validated, committed.PriorStatus);
        Assert.Equal(ExperienceStatus.Contested, committed.CurrentStatus);
        // The reason rides on the record's own history; nothing is deleted and nothing is a side channel.
        Assert.Contains("harm", committed.Reason, StringComparison.Ordinal);
        Assert.Equal("the lesson sent the run down a dead end", committed.Confidence.Detail);
        Assert.Single(ledger.Submissions);
    }

    [Fact]
    public async Task Resubmitting_the_same_feedback_identically_reports_the_original_and_writes_nothing_twice()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));
        var feedback = Feedback([experienceId]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
        };

        var first = await service.RecordAsync(Authorization, feedback, CancellationToken.None);
        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, first.Outcome);

        // The record has moved on exactly as the first submission left it, which is what a real retry
        // reads back.
        records.Record = Validated(experienceId, confidence: 3d / 4d, supporting: 2, revision: 2);
        records.CountEvidence = false;

        var second = await service.RecordAsync(Authorization, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.AlreadyRecorded, second.Outcome);
        Assert.Single(ledger.Submissions);

        var replayed = Assert.Single(second.Exposures);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, replayed.Disposition);
        // Accepted and counted zero times: the observation was already counted once.
        Assert.False(replayed.Counted);
        Assert.Equal(Assert.Single(first.Exposures).EvidenceId, replayed.EvidenceId);
        Assert.Equal(2, records.Commits.Count);
        Assert.Equal(records.Commits[0].Event.EventId, records.Commits[1].Event.EventId);
    }

    [Fact]
    public async Task The_same_feedback_ID_with_different_content_is_rejected_with_nothing_written()
    {
        var experienceId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));
        var feedback = Feedback([experienceId]);

        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await service.RecordAsync(Authorization, feedback, CancellationToken.None)).Outcome);

        records.Record = Validated(otherId);
        var conflicting = await service.RecordAsync(
            Authorization,
            feedback with { ExposedExperienceIds = [otherId] },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Conflict, conflicting.Outcome);
        // Nothing was written; the exposures reported are the stored submission's, not this call's.
        Assert.Equal(experienceId, Assert.Single(conflicting.Exposures).ExperienceId);
        Assert.Equal(new[] { experienceId }, Assert.Single(ledger.Submissions).Exposures.Select(exposure => exposure.ExperienceId));
        Assert.Empty(records.Commits);
    }

    [Fact]
    public async Task One_record_failing_leaves_the_rest_applied_and_is_reported_as_retryable()
    {
        var failing = Guid.NewGuid();
        var succeeding = Guid.NewGuid();
        var records = new FeedbackRecordStore(Validated(succeeding))
        {
            ThrowFor = failing,
        };
        records.Records[failing] = Validated(failing);
        records.Records[succeeding] = Validated(succeeding);

        var ledger = new FakeReuseFeedbackLedger();
        var service = new ExperienceReuseFeedbackService(ledger, Trusting(records));

        var feedback = Feedback([failing, succeeding]) with
        {
            HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [failing, succeeding], "both applied"),
        };

        var result = await service.RecordAsync(Authorization, feedback, CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.True(result.IsRetryable);

        var failed = result.Exposures.Single(exposure => exposure.ExperienceId == failing);
        Assert.Equal(ExperienceExposureDisposition.Failed, failed.Disposition);
        Assert.True(failed.Retryable);

        // The other record was still submitted, and the exposure for both is durable either way.
        var applied = result.Exposures.Single(exposure => exposure.ExperienceId == succeeding);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, applied.Disposition);
        Assert.True(applied.Counted);
        Assert.Equal(2, Assert.Single(ledger.Submissions).Exposures.Count);
    }

    [Fact]
    public async Task Evidence_the_same_run_already_produced_is_recorded_and_not_counted()
    {
        var experienceId = Guid.NewGuid();
        var (service, _, records) = Build(Validated(experienceId));
        records.CountEvidence = false;

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
            },
            CancellationToken.None);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition);
        Assert.False(exposure.Counted);
        Assert.NotNull(exposure.Reason);
    }

    [Theory]
    [InlineData(ExperienceStatus.Revoked)]
    [InlineData(ExperienceStatus.Quarantined)]
    [InlineData(ExperienceStatus.Candidate)]
    public async Task An_ineligible_record_keeps_its_exposure_and_receives_no_submission(ExperienceStatus status)
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId) with { Status = status });

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(ExperienceExposureDisposition.Ineligible, exposure.Disposition);
        Assert.Equal(status, exposure.Status);
        Assert.False(exposure.Retryable);
        Assert.Empty(records.Commits);
        Assert.True(Assert.Single(Assert.Single(ledger.Submissions).Exposures).Attributed);
    }

    [Fact]
    public async Task An_exposed_ID_that_does_not_exist_in_scope_is_recorded_as_unresolved()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(record: null);

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(ExperienceExposureDisposition.Unresolved, exposure.Disposition);
        Assert.False(exposure.Retryable);
        Assert.Empty(records.Commits);
        Assert.Single(ledger.Submissions);
    }

    [Fact]
    public async Task A_run_scope_beyond_the_authorization_is_denied_before_any_write()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));

        var result = await service.RecordAsync(
            new AuthorizationContext("other-tenant", "principal-7", [], Now),
            Feedback([experienceId]),
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Denied, result.Outcome);
        Assert.Empty(result.Exposures);
        Assert.Empty(ledger.Submissions);
        Assert.Empty(records.Commits);
    }

    [Fact]
    public async Task Attribution_may_only_name_records_the_run_was_exposed_to()
    {
        var exposedId = Guid.NewGuid();
        var (service, ledger, _) = Build(Validated(exposedId));

        var result = await service.RecordAsync(
            Authorization,
            Feedback([exposedId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [Guid.NewGuid()], "it applied"),
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Path.EndsWith("AttributedExperienceIds", StringComparison.Ordinal));
        Assert.Empty(ledger.Submissions);
    }

    [Fact]
    public async Task A_comparative_result_about_another_run_or_with_no_evidence_is_refused()
    {
        var experienceId = Guid.NewGuid();
        var feedback = Feedback([experienceId]);

        var (wrongRunService, wrongRunLedger, _) = Build(Validated(experienceId));
        var wrongRun = await wrongRunService.RecordAsync(
            Authorization,
            feedback with { ComparativeEvaluation = Comparative(Guid.NewGuid(), Guid.NewGuid(), [experienceId], ExperienceReuseBenefit.Improved) },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, wrongRun.Outcome);
        Assert.Contains(wrongRun.Errors, error => error.Path.EndsWith("RunId", StringComparison.Ordinal));
        Assert.Empty(wrongRunLedger.Submissions);

        // "Comparative" with no evidence behind it is an assertion wearing an evaluator's name. The
        // attribution is dropped -- but the exposure is a true fact about the run, so it is still
        // recorded, with benefit Unknown and a reason saying what was refused.
        var (noEvidenceService, noEvidenceLedger, noEvidenceRecords) = Build(Validated(experienceId));
        var noEvidence = await noEvidenceService.RecordAsync(
            Authorization,
            feedback with
            {
                ComparativeEvaluation = Comparative(feedback.RunId, Guid.NewGuid(), [experienceId], ExperienceReuseBenefit.Improved) with
                {
                    Evidence = [],
                },
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, noEvidence.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Unknown, noEvidence.Benefit);
        Assert.Equal(ReuseAttributionSource.None, noEvidence.AttributionSource);
        Assert.Contains("Evidence", noEvidence.Reason!, StringComparison.Ordinal);
        Assert.Equal(ExperienceExposureDisposition.ExposureOnly, Assert.Single(noEvidence.Exposures).Disposition);
        Assert.Equal(ReuseAttributionSource.None, Assert.Single(noEvidenceLedger.Submissions).AttributionSource);
        Assert.Empty(noEvidenceRecords.Commits);
    }

    [Fact]
    public async Task Evidence_from_another_verification_round_does_not_attribute_this_comparison()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));
        var feedback = Feedback([experienceId]);
        var round = Guid.NewGuid();

        var result = await service.RecordAsync(
            Authorization,
            feedback with
            {
                ComparativeEvaluation = Comparative(feedback.RunId, round, [experienceId], ExperienceReuseBenefit.Improved) with
                {
                    // Real evidence, but about some other round: it is not evidence about this comparison.
                    Evidence = [new Evidence(Guid.NewGuid(), Guid.NewGuid(), "rev-7", "task-success", "TestResult", CheckResult.Pass, "ci", null, Now)],
                },
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Unknown, result.Benefit);
        Assert.Empty(records.Commits);
        Assert.Equal(ReuseAttributionSource.None, Assert.Single(ledger.Submissions).AttributionSource);
    }

    [Fact]
    public async Task A_human_assessment_with_no_host_established_review_is_dropped_and_the_exposure_kept()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                // Everything an authorized caller already has -- a benefit, the record IDs and a string --
                // and nothing that ties the judgement to a review the host can produce.
                HumanAssessment = Assessment(ExperienceReuseBenefit.Harmed, [experienceId], "trust me", Guid.Empty),
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Unknown, result.Benefit);
        Assert.Contains("AssessmentId", result.Reason!, StringComparison.Ordinal);
        Assert.Empty(records.Commits);
        Assert.Equal(ReuseAttributionSource.None, Assert.Single(ledger.Submissions).AttributionSource);
    }

    [Fact]
    public async Task A_human_assessments_round_is_stored_for_audit_and_never_keys_its_evidence()
    {
        var experienceId = Guid.NewGuid();
        var round = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied") with
                {
                    VerificationRoundId = round,
                },
            },
            CancellationToken.None);

        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, Assert.Single(result.Exposures).Disposition);
        Assert.Equal(round, Assert.Single(ledger.Submissions).VerificationRoundId);

        // Audit only. Human evidence counts once per reviewer and run, so keying on a round the reviewer
        // chose would let one opinion about one run count once per round closed.
        var confidence = Assert.Single(records.Commits).Event.Confidence;
        Assert.Equal(ConfidenceEvidenceSource.Human, confidence!.Source);
        Assert.Null(confidence.VerificationRoundId);
    }

    [Fact]
    public async Task A_comparative_results_evidence_is_stored_so_an_auditor_sees_what_it_rested_on()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, _) = Build(Validated(experienceId));
        var feedback = Feedback([experienceId]);
        var round = Guid.NewGuid();
        var comparative = Comparative(feedback.RunId, round, [experienceId], ExperienceReuseBenefit.Improved);

        await service.RecordAsync(Authorization, feedback with { ComparativeEvaluation = comparative }, CancellationToken.None);

        var stored = Assert.Single(ledger.Submissions);
        Assert.Equal(comparative.Evidence.Select(evidence => evidence.EvidenceId), stored.EvidenceIds);
        Assert.Equal(comparative.EvaluatedAt, stored.AttributedAt);
        Assert.Null(stored.AssessmentId);
    }

    [Fact]
    public async Task Two_attributions_at_once_and_an_attribution_of_Unknown_are_both_refused()
    {
        var experienceId = Guid.NewGuid();
        var feedback = Feedback([experienceId]);
        var assessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied");

        var (bothService, bothLedger, _) = Build(Validated(experienceId));
        var both = await bothService.RecordAsync(
            Authorization,
            feedback with
            {
                HumanAssessment = assessment,
                ComparativeEvaluation = Comparative(feedback.RunId, Guid.NewGuid(), [experienceId], ExperienceReuseBenefit.Harmed),
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, both.Outcome);
        Assert.Empty(bothLedger.Submissions);

        // An attribution of Unknown is not an attribution -- but it is also not a reason to lose the
        // exposure, so it degrades rather than being refused.
        var (unknownService, unknownLedger, unknownRecords) = Build(Validated(experienceId));
        var unknown = await unknownService.RecordAsync(
            Authorization,
            feedback with { HumanAssessment = assessment with { Benefit = ExperienceReuseBenefit.Unknown } },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, unknown.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Unknown, unknown.Benefit);
        Assert.Contains("Benefit", unknown.Reason!, StringComparison.Ordinal);
        Assert.Empty(unknownRecords.Commits);
        Assert.Equal(ReuseAttributionSource.None, Assert.Single(unknownLedger.Submissions).AttributionSource);
    }

    [Fact]
    public async Task A_human_assessment_needs_a_reviewer_the_host_established()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, _) = Build(Validated(experienceId));

        var result = await service.RecordAsync(
            new AuthorizationContext("tenant-1", "  ", [], Now),
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(ExperienceReuseBenefit.Unknown, result.Benefit);
        Assert.Contains("Authorization.PrincipalId", result.Reason!, StringComparison.Ordinal);
        Assert.Equal(ReuseAttributionSource.None, Assert.Single(ledger.Submissions).AttributionSource);
    }

    [Fact]
    public void The_derived_IDs_are_a_pure_function_of_the_feedback_and_the_record()
    {
        var feedbackId = Guid.NewGuid();
        var experienceId = Guid.NewGuid();
        var otherExperienceId = Guid.NewGuid();

        // Same inputs, same IDs: this is the whole of why a retry converges rather than double-counting.
        Assert.Equal(
            ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId),
            ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId));
        Assert.Equal(
            ExperienceReuseFeedbackService.EventIdFor(feedbackId, experienceId),
            ExperienceReuseFeedbackService.EventIdFor(feedbackId, experienceId));

        // Different record, different submission, and the two purposes never collide with each other.
        Assert.NotEqual(
            ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId),
            ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, otherExperienceId));
        Assert.NotEqual(
            ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId),
            ExperienceReuseFeedbackService.EvidenceIdFor(Guid.NewGuid(), experienceId));
        Assert.NotEqual(
            ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId),
            ExperienceReuseFeedbackService.EventIdFor(feedbackId, experienceId));

        // Version 8 (RFC 9562 custom) and the RFC variant, like finalization's own derived IDs.
        var bytes = ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId).ToByteArray(bigEndian: true);
        Assert.Equal(0x80, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
        Assert.NotEqual(Guid.Empty, ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, experienceId));
    }

    [Fact]
    public async Task The_trial_label_and_the_measure_are_recorded_verbatim()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, _) = Build(Validated(experienceId));

        await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                Measure = new("tool-calls", 11),
                TrialLabel = "memory-disabled",
            },
            CancellationToken.None);

        var stored = Assert.Single(ledger.Submissions);
        Assert.Equal("tool-calls", stored.Measure.Kind);
        Assert.Equal(11, stored.Measure.Value);
        Assert.Equal("memory-disabled", stored.TrialLabel);
        Assert.Equal(TaskVerificationStatus.Verified, stored.RunOutcome);
    }

    [Fact]
    public async Task A_malformed_submission_names_every_field_and_writes_nothing()
    {
        var (service, ledger, records) = Build(Validated(Guid.NewGuid()));

        var result = await service.RecordAsync(
            Authorization,
            new ExperienceReuseFeedback(
                FeedbackId: Guid.Empty,
                RunId: Guid.Empty,
                Scope: TestScope,
                ExposedExperienceIds: [],
                RunOutcome: TaskVerificationStatus.Verified,
                Measure: new("   ", double.NaN),
                ObservedAt: default,
                TrialLabel: "   "),
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, result.Outcome);
        foreach (var path in new[] { "FeedbackId", "RunId", "ExposedExperienceIds", "Measure.Kind", "Measure.Value", "ObservedAt", "TrialLabel" })
        {
            Assert.Contains(result.Errors, error => error.Path == path);
        }

        Assert.Empty(ledger.Submissions);
        Assert.Empty(records.Commits);
    }

    [Fact]
    public async Task The_exposure_ledger_is_written_before_any_confidence_submission()
    {
        var experienceId = Guid.NewGuid();
        var order = new List<string>();
        var ledger = new FakeReuseFeedbackLedger { Order = order };
        var records = new FeedbackRecordStore(Validated(experienceId)) { Order = order };
        var service = new ExperienceReuseFeedbackService(ledger, Trusting(records));

        await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
            },
            CancellationToken.None);

        // What the run saw is durable even if every score submission had then failed.
        Assert.Equal(new[] { "ledger", "read", "commit" }, order);
    }

    [Fact]
    public async Task Duplicate_exposed_IDs_are_refused_rather_than_submitted_twice()
    {
        var experienceId = Guid.NewGuid();
        var (service, ledger, _) = Build(Validated(experienceId));

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId, experienceId]),
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Path == "ExposedExperienceIds");
        Assert.Empty(ledger.Submissions);
    }

    [Fact]
    public async Task Null_arguments_are_caller_errors()
    {
        var (service, _, _) = Build(Validated(Guid.NewGuid()));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.RecordAsync(null!, Feedback([Guid.NewGuid()]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.RecordAsync(Authorization, null!, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => new ExperienceReuseFeedbackService(null!, Trusting(new FeedbackRecordStore(null))));
        Assert.Throws<ArgumentNullException>(() => new ExperienceReuseFeedbackService(new FakeReuseFeedbackLedger(), null!));
    }

    [Theory]
    [InlineData(ExperienceStoreOutcome.StaleRevision, ExperienceExposureDisposition.Failed, true)]
    [InlineData(ExperienceStoreOutcome.StatusMismatch, ExperienceExposureDisposition.Failed, true)]
    [InlineData(ExperienceStoreOutcome.Conflict, ExperienceExposureDisposition.Refused, false)]
    [InlineData(ExperienceStoreOutcome.Invalid, ExperienceExposureDisposition.Refused, false)]
    // A record erased after its exposure was written: terminal, so never Failed-and-retryable.
    [InlineData(ExperienceStoreOutcome.Deleted, ExperienceExposureDisposition.Refused, false)]
    public async Task Every_confidence_refusal_maps_onto_a_disposition_that_says_whether_to_retry(
        ExperienceStoreOutcome commitOutcome,
        ExperienceExposureDisposition expected,
        bool retryable)
    {
        // The mapping is the whole contract of a partial failure: a caller decides whether to resubmit
        // from it, so a lost revision race must not read the same as a refused submission.
        var experienceId = Guid.NewGuid();
        var (service, ledger, records) = Build(Validated(experienceId));
        records.CommitOutcome = commitOutcome;

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
            },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(expected, exposure.Disposition);
        Assert.Equal(retryable, exposure.Retryable);
        Assert.Equal(retryable, result.IsRetryable);
        Assert.False(exposure.Counted);

        // The exposure is durable whatever the score did, which is what makes the retry possible.
        Assert.Single(ledger.Submissions);
        Assert.Equal(
            ExperienceReuseFeedbackService.EvidenceIdFor(result.FeedbackId, experienceId),
            exposure.EvidenceId);
    }

    [Fact]
    public async Task Cancellation_after_the_ledger_write_reports_what_landed_instead_of_throwing()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();

        var records = new FeedbackRecordStore(null);
        records.Records[first] = Validated(first);
        records.Records[second] = Validated(second);
        // Cancel once the first record has been committed, so the second is abandoned mid-fan-out.
        records.OnCommit = () => cancellation.Cancel();

        var ledger = new FakeReuseFeedbackLedger();
        var service = new ExperienceReuseFeedbackService(ledger, Trusting(records));

        var ordered = new[] { first, second }.Order().ToArray();
        var result = await service.RecordAsync(
            Authorization,
            Feedback(ordered) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, ordered, "both applied"),
            },
            cancellation.Token);

        // Throwing would leave the caller unable to find out what had already moved, with the ledger
        // durable and no read API to ask.
        Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
        Assert.Equal(2, result.Exposures.Count);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, result.Exposures[0].Disposition);
        Assert.Equal(ExperienceExposureDisposition.Failed, result.Exposures[1].Disposition);
        Assert.True(result.Exposures[1].Retryable);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task The_same_records_in_a_different_order_are_the_same_submission()
    {
        // Otherwise a host that crashed mid-submission and retried with its records in another order
        // would get a permanent Conflict, and no way to discover which records still needed evidence.
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var (service, ledger, records) = Build(null);
        records.Records[first] = Validated(first);
        records.Records[second] = Validated(second);

        var feedback = Feedback([first, second]);
        Assert.Equal(
            ExperienceReuseFeedbackOutcome.Recorded,
            (await service.RecordAsync(Authorization, feedback, CancellationToken.None)).Outcome);

        var retry = await service.RecordAsync(
            Authorization,
            feedback with { ExposedExperienceIds = [second, first] },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.AlreadyRecorded, retry.Outcome);
        Assert.Single(ledger.Submissions);

        // And the stored order is the normalized one, not either caller's.
        Assert.Equal(
            new[] { first, second }.Order(),
            Assert.Single(ledger.Submissions).Exposures.Select(exposure => exposure.ExperienceId));
    }

    [Fact]
    public async Task A_conflict_reports_the_records_the_stored_submission_named()
    {
        var experienceId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var (service, _, records) = Build(null);
        records.Records[experienceId] = Validated(experienceId);
        records.Records[otherId] = Validated(otherId);

        var feedback = Feedback([experienceId]);
        await service.RecordAsync(Authorization, feedback, CancellationToken.None);

        var conflicting = await service.RecordAsync(
            Authorization,
            feedback with { ExposedExperienceIds = [otherId] },
            CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Conflict, conflicting.Outcome);

        // Nothing was written, and the caller can still see what is stored under the ID it collided with.
        var reported = Assert.Single(conflicting.Exposures);
        Assert.Equal(experienceId, reported.ExperienceId);
        Assert.Equal(ExperienceExposureDisposition.Refused, reported.Disposition);
        Assert.False(reported.Retryable);
    }

    [Fact]
    public async Task More_exposed_records_than_the_bound_is_refused()
    {
        var (service, ledger, _) = Build(Validated(Guid.NewGuid()));
        var tooMany = Enumerable.Range(0, ExperienceReuseFeedback.MaxExposedRecords + 1)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        var result = await service.RecordAsync(Authorization, Feedback(tooMany), CancellationToken.None);

        Assert.Equal(ExperienceReuseFeedbackOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Path == "ExposedExperienceIds");
        Assert.Empty(ledger.Submissions);

        // The bound exists because each attributed record costs its own transaction, run sequentially.
        Assert.Equal(64, ExperienceReuseFeedback.MaxExposedRecords);
    }

    [Fact]
    public async Task The_confidence_paths_own_refusal_reason_is_not_overwritten()
    {
        // "Readable only through a sharing grant, which never confers writing to it" is a different fact
        // from "not here at all", and it is the one a host can actually act on.
        var experienceId = Guid.NewGuid();
        var (service, _, records) = Build(Validated(experienceId));
        records.SharedByGrant = true;

        var result = await service.RecordAsync(
            Authorization,
            Feedback([experienceId]) with
            {
                HumanAssessment = Assessment(ExperienceReuseBenefit.Improved, [experienceId], "it applied"),
            },
            CancellationToken.None);

        var exposure = Assert.Single(result.Exposures);
        Assert.Equal(ExperienceExposureDisposition.Unresolved, exposure.Disposition);
        Assert.Contains("grant", exposure.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    private static (ExperienceReuseFeedbackService Service, FakeReuseFeedbackLedger Ledger, FeedbackRecordStore Records) Build(ExperienceRecord? record)
    {
        var ledger = new FakeReuseFeedbackLedger();
        var records = new FeedbackRecordStore(record);
        return (new ExperienceReuseFeedbackService(ledger, Trusting(records)), ledger, records);
    }

    private static ExperienceReuseFeedback Feedback(IReadOnlyList<Guid> exposed) => new(
        FeedbackId: Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        Scope: TestScope,
        ExposedExperienceIds: exposed,
        RunOutcome: TaskVerificationStatus.Verified,
        Measure: new("task-success", 1),
        ObservedAt: Now);

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
            Now);

    private static ComparativeEvaluationResult Comparative(
        Guid runId,
        Guid roundId,
        IReadOnlyList<Guid> attributed,
        ExperienceReuseBenefit benefit) => new(
            EvaluatorId: "baseline-comparator/1.0.0",
            RunId: runId,
            VerificationRoundId: roundId,
            Benefit: benefit,
            AttributedExperienceIds: attributed,
            Evidence: [new Evidence(Guid.NewGuid(), roundId, "rev-7", "task-success", "TestResult", CheckResult.Pass, "ci", null, Now)],
            Summary: "the memory-enabled arm passed and the baseline arm did not",
            EvaluatedAt: Now);

    private static ExperienceRecord Validated(
        Guid experienceId,
        double confidence = 2d / 3d,
        int supporting = 1,
        long revision = 1) => new(
            ExperienceId: experienceId,
            SourceRunId: Guid.NewGuid(),
            Scope: TestScope,
            TaskId: "task-1",
            TaskSummary: null,
            Attempts: [],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], null, Now),
            CompletionScore: 1,
            Reflection: null,
            Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("tests", null, Now, null),
            Status: ExperienceStatus.Validated,
            ReuseConfidence: confidence,
            SupportingValidations: supporting,
            Contradictions: 0,
            Revision: revision,
            CreatedAt: Now,
            UpdatedAt: Now);

    /// <summary>
    /// An in-memory feedback ledger with the real one's idempotency rule: the feedback ID is the key, an
    /// identical resubmission writes nothing, and anything else under that ID is refused.
    /// </summary>
    /// <summary>
    /// These tests are about the arithmetic, the store contract and the reviewer rule, not about verifying
    /// an independence key's inputs (<c>VerifiedIndependenceTests</c> covers that against a store that
    /// knows runs), so they run with the host's identifiers trusted: the opt-out, which keeps only the
    /// own-run rule.
    /// </summary>
    private static ExperienceLifecycleService Trusting(IExperienceRecordStore store, ExperienceIndexingService? indexing = null) =>
        new(store, indexing, new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });

    private sealed class FakeReuseFeedbackLedger : IExperienceReuseFeedbackStore
    {
        private readonly Dictionary<Guid, RecordedExperienceReuseFeedback> _stored = [];

        public List<string> Order { get; set; } = [];

        public IReadOnlyCollection<RecordedExperienceReuseFeedback> Submissions => _stored.Values;

        public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
            AuthorizationContext authorization,
            RecordedExperienceReuseFeedback feedback,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            Order.Add("ledger");

            if (!_stored.TryGetValue(feedback.FeedbackId, out var existing))
            {
                _stored[feedback.FeedbackId] = feedback;
                return Task.FromResult(new ExperienceReuseFeedbackStoreResult(
                    ExperienceReuseFeedbackStoreOutcome.Recorded, feedback, []));
            }

            return SameContent(existing, feedback)
                ? Task.FromResult(new ExperienceReuseFeedbackStoreResult(
                    ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, existing, []))
                // As the real store does: the stored submission comes back on a conflict only when this
                // caller's authorization covers its own scope.
                : Task.FromResult(new ExperienceReuseFeedbackStoreResult(
                    ExperienceReuseFeedbackStoreOutcome.Conflict,
                    authorization.Permits(existing.Scope) ? existing : null,
                    []));
        }

        /// <summary>
        /// Compares the submission's own fields and its exposures in order. Record equality would not
        /// do: the exposures are a list, so two identical submissions would compare unequal by
        /// reference and every retry would look like a conflict.
        /// </summary>
        private static bool SameContent(RecordedExperienceReuseFeedback stored, RecordedExperienceReuseFeedback submitted) =>
            stored with { Exposures = [] } == (submitted with { Exposures = [] })
            && stored.Exposures.SequenceEqual(submitted.Exposures);
    }

    /// <summary>
    /// Answers the reads the evidence path makes and records the commits it produces, with a seam for a
    /// storage failure against one named record so partial failure can be driven.
    /// </summary>
    private sealed class FeedbackRecordStore : IExperienceRecordStore
    {
        public FeedbackRecordStore(ExperienceRecord? record) => Record = record;

        public ExperienceRecord? Record { get; set; }

        public Dictionary<Guid, ExperienceRecord> Records { get; } = [];

        public Guid? ThrowFor { get; init; }

        public bool SharedByGrant { get; set; }

        /// <summary>Runs after each commit is recorded, so a test can cancel mid-fan-out.</summary>
        public Action? OnCommit { get; set; }

        public bool CountEvidence { get; set; } = true;

        /// <summary>
        /// A store outcome to answer every commit with, so the mapping from the confidence path's
        /// refusals onto exposure dispositions can be driven. Without it the fake can only ever say
        /// Committed, and deleting half that mapping would pass every test.
        /// </summary>
        public ExperienceStoreOutcome? CommitOutcome { get; set; }

        public IReadOnlyList<StoreValidationError> CommitErrors { get; set; } = [];

        public bool Reads { get; private set; }

        public List<string> Order { get; set; } = [];

        public List<(Scope Scope, LifecycleEvent Event)> Commits { get; } = [];

        public Task<ExperienceRecordGetResult> GetAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            // Every real port observes the token; without this the fan-out could not be cancelled and
            // the cancellation path would be untestable.
            cancellationToken.ThrowIfCancellationRequested();
            Reads = true;
            Order.Add("read");

            if (experienceId == ThrowFor)
            {
                throw new ExperienceStoreException("the ledger is reachable but this record's store is not.");
            }

            var record = Records.TryGetValue(experienceId, out var stored) ? stored : Record;
            return Task.FromResult(record is not null && record.ExperienceId == experienceId
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [], SharedByGrant)
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));
        }

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            Order.Add("commit");
            Commits.Add((scope, lifecycleEvent));
            OnCommit?.Invoke();

            if (CommitOutcome is { } configured)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(
                    configured,
                    lifecycleEvent.ExpectedRevision,
                    lifecycleEvent.PriorStatus,
                    CommitErrors));
            }

            if (lifecycleEvent.Confidence is { } confidence && !CountEvidence)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(
                    ExperienceStoreOutcome.Committed,
                    lifecycleEvent.ExpectedRevision,
                    lifecycleEvent.PriorStatus,
                    [],
                    confidence.AsRecordedOnly()));
            }

            return Task.FromResult(new ExperienceLifecycleCommitResult(
                ExperienceStoreOutcome.Committed,
                lifecycleEvent.ExpectedRevision + 1,
                null,
                [],
                lifecycleEvent.Confidence));
        }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recording feedback must not create records.");

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recording feedback must not query records.");

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recording feedback must not read history.");

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Recording feedback must not check supersession.");
    }
}
