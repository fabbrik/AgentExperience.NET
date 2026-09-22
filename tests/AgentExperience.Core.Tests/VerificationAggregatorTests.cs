namespace AgentExperience.Core.Tests;

/// <summary>
/// Exercises <see cref="VerificationAggregator.Aggregate"/> against Story 1.5's AC2-AC6: all-pass
/// yields Verified; a single required Fail dominates every other check's Pass; a missing or Unknown
/// required check yields Unknown; an empty required set yields Unknown; a later host-closed round
/// can verify success even after an earlier round failed, without combining them; evidence outside
/// the selected round/revision never contributes; no closed round (or a stale, revision-mismatched
/// one) yields Unknown; mixed Pass+Fail evidence for one check resolves to Fail; the completion
/// score is the passing/required fraction reported alongside a rule version; an evaluator's own
/// internal failure (caught upstream, per <see cref="TaskCheckEvaluators"/>'s Boundary) aggregates
/// like any other Unknown evidence; and cancellation propagates as an exception rather than
/// returning any <see cref="VerificationResult"/>.
/// </summary>
public class VerificationAggregatorTests
{
    /// <summary>A required check with no expected kind, i.e. one any evidence kind may satisfy.</summary>
    private static RequiredCheck Check(string checkId, string? expectedKind = null) => new(checkId, expectedKind);

    private static Evidence MakeEvidence(Guid roundId, string artifactRevision, string checkId, CheckResult result, string producer = "evaluator") =>
        new(
            EvidenceId: Guid.NewGuid(),
            VerificationRoundId: roundId,
            ArtifactRevision: artifactRevision,
            CheckId: checkId,
            Kind: "TestResult",
            Result: result,
            Producer: producer,
            Detail: null,
            CapturedAt: DateTimeOffset.UtcNow);

    [Fact]
    public void AC2_all_required_checks_passing_in_the_closed_round_yields_Verified()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[]
        {
            MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass),
            MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass),
        };

        var result = VerificationAggregator.Aggregate(evidence, [Check("build"), Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Verified, result.Outcome.Status);
        Assert.Equal(1.0, result.CompletionScore);
        Assert.Equal(2, result.Outcome.Evidence.Count);
    }

    [Fact]
    public void AC2_one_required_check_failing_yields_Failed_regardless_of_other_passing_checks()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[]
        {
            MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Fail),
            MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass),
            MakeEvidence(round.RoundId, round.ArtifactRevision, "lint", CheckResult.Pass),
        };

        var result = VerificationAggregator.Aggregate(evidence, [Check("build"), Check("tests"), Check("lint")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Failed, result.Outcome.Status);
        Assert.Contains("build", result.Outcome.Reason);
        Assert.DoesNotContain("tests", result.Outcome.Reason);
    }

    [Fact]
    public void AC2_a_required_check_with_no_evidence_at_all_yields_Unknown()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[] { MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass) };

        var result = VerificationAggregator.Aggregate(evidence, [Check("build"), Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Equal(0.5, result.CompletionScore);
        Assert.Contains("tests", result.Outcome.Reason);
        Assert.DoesNotContain("build", result.Outcome.Reason);
    }

    [Fact]
    public void AC1_evidence_for_a_CheckId_outside_the_required_set_never_contributes_to_the_outcome_or_verdict()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var required = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass);
        var unrelated = MakeEvidence(round.RoundId, round.ArtifactRevision, "lint", CheckResult.Fail);

        var result = VerificationAggregator.Aggregate([required, unrelated], [Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Verified, result.Outcome.Status);
        Assert.Same(required, Assert.Single(result.Outcome.Evidence));
    }

    [Fact]
    public void Outcome_evidence_keeps_production_order_even_when_required_checks_are_declared_in_a_different_order()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var producedFirst = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass);
        var producedSecond = MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass);

        var result = VerificationAggregator.Aggregate([producedFirst, producedSecond], [Check("build"), Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal([producedFirst, producedSecond], result.Outcome.Evidence);
    }

    [Fact]
    public void An_evidence_Result_outside_the_defined_CheckResult_values_resolves_to_Unknown_never_Pass()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[] { MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", (CheckResult)999) };

        var result = VerificationAggregator.Aggregate(evidence, [Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Equal(0.0, result.CompletionScore);
    }

    [Fact]
    public void Duplicate_required_check_ids_throw_rather_than_skewing_the_completion_score()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[] { MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass) };

        Assert.Throws<ArgumentException>(() =>
            VerificationAggregator.Aggregate(evidence, [Check("build"), Check("build"), Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_null_evidence_entry_throws_rather_than_being_silently_dropped()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[] { MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass), null! };

        Assert.Throws<ArgumentException>(() =>
            VerificationAggregator.Aggregate(evidence, [Check("build")], round, round.ArtifactRevision, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AC2_a_required_check_whose_only_evidence_is_Unknown_yields_overall_Unknown()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[]
        {
            MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass),
            MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Unknown),
        };

        var result = VerificationAggregator.Aggregate(evidence, [Check("build"), Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
    }

    [Fact]
    public void AC2_an_empty_required_check_set_yields_Unknown_even_with_passing_evidence_present()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[] { MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass) };

        var result = VerificationAggregator.Aggregate(evidence, [], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Equal(0.0, result.CompletionScore);
        Assert.Empty(result.Outcome.Evidence);
    }

    [Fact]
    public void AC3_a_later_closed_round_can_verify_success_after_an_earlier_round_failed_without_combining_them()
    {
        const string artifactRevision = "rev-1";
        var earlierRoundId = Guid.NewGuid();
        var laterRoundId = Guid.NewGuid();
        var closedRound = new ClosedVerificationRound(laterRoundId, artifactRevision);

        var earlierFailure = MakeEvidence(earlierRoundId, artifactRevision, "tests", CheckResult.Fail);
        var laterPass = MakeEvidence(laterRoundId, artifactRevision, "tests", CheckResult.Pass);
        var allEvidence = new[] { earlierFailure, laterPass };

        var result = VerificationAggregator.Aggregate(allEvidence, [Check("tests")], closedRound, artifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Verified, result.Outcome.Status);
        // The earlier round's failing evidence never contributes to the outcome...
        Assert.DoesNotContain(earlierFailure, result.Outcome.Evidence);
        Assert.Contains(laterPass, result.Outcome.Evidence);
        // ...yet remains untouched in the caller's own evidence collection -- attempt history is
        // never dropped or mutated by aggregation, only filtered from what backs this outcome.
        Assert.Contains(earlierFailure, allEvidence);
    }

    [Fact]
    public void AC3_AC4_evidence_from_a_different_artifact_revision_never_contributes_even_under_the_matching_round_id()
    {
        var roundId = Guid.NewGuid();
        var closedRound = new ClosedVerificationRound(roundId, "rev-2");

        // Same RoundId, but recorded against a stale artifact revision ("rev-1") -- must not
        // contribute even though VerificationRoundId matches.
        var staleRevisionEvidence = MakeEvidence(roundId, "rev-1", "tests", CheckResult.Pass);

        var result = VerificationAggregator.Aggregate([staleRevisionEvidence], [Check("tests")], closedRound, "rev-2", DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Empty(result.Outcome.Evidence);
    }

    [Fact]
    public void AC3_evidence_from_a_non_selected_round_id_never_contributes_even_under_the_matching_revision()
    {
        const string artifactRevision = "rev-1";
        var closedRound = new ClosedVerificationRound(Guid.NewGuid(), artifactRevision);

        var otherRoundEvidence = MakeEvidence(Guid.NewGuid(), artifactRevision, "tests", CheckResult.Pass);

        var result = VerificationAggregator.Aggregate([otherRoundEvidence], [Check("tests")], closedRound, artifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Empty(result.Outcome.Evidence);
    }

    [Fact]
    public void AC4_no_closed_round_yields_Unknown()
    {
        var result = VerificationAggregator.Aggregate([], [Check("tests")], null, "rev-1", DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Equal(0.0, result.CompletionScore);
        Assert.Empty(result.Outcome.Evidence);
    }

    [Fact]
    public void AC4_a_closed_round_for_a_different_artifact_revision_than_current_is_stale_and_yields_Unknown()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[] { MakeEvidence(round.RoundId, "rev-2", "tests", CheckResult.Pass) };

        var result = VerificationAggregator.Aggregate(evidence, [Check("tests")], round, "rev-2", DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
    }

    [Fact]
    public void AC4_mixed_Pass_and_Fail_evidence_for_the_same_required_check_resolves_to_Fail()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[]
        {
            MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass),
            MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Fail),
        };

        var result = VerificationAggregator.Aggregate(evidence, [Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Failed, result.Outcome.Status);
        // Both pieces of conflicting evidence are retained for audit, not discarded.
        Assert.Equal(2, result.Outcome.Evidence.Count);
    }

    [Fact]
    public void AC5_completion_score_is_the_fraction_of_required_checks_that_conclusively_passed_and_carries_the_rule_version()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[]
        {
            MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass),
            MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass),
            // "lint" has no evidence at all -> Unknown, so overall stays Unknown even though 2/3 passed.
        };

        var result = VerificationAggregator.Aggregate(evidence, [Check("build"), Check("tests"), Check("lint")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status); // completion score alone never grants verification
        Assert.Equal(2.0 / 3.0, result.CompletionScore, precision: 10);
        Assert.False(string.IsNullOrWhiteSpace(result.RuleVersion));
        Assert.Equal(VerificationAggregator.RuleVersion, result.RuleVersion);
    }

    [Fact]
    public void AC5_completion_score_for_an_empty_required_set_is_zero()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");

        var result = VerificationAggregator.Aggregate([], [], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(0.0, result.CompletionScore);
    }

    [Fact]
    public void AC6_an_evaluators_own_internal_failure_is_caught_upstream_and_aggregates_like_any_other_Unknown_evidence()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidenceId = Guid.NewGuid();
        var capturedAt = DateTimeOffset.UtcNow;

        // Simulate an evaluator observing an out-of-range/unrecognized status. Per
        // TaskCheckEvaluators's own Boundary, this is caught internally and reported as Unknown
        // evidence with a safe diagnostic -- it must never propagate as an exception.
        var invalidStatus = (WorkflowCompletionStatus)(-1);
        var produced = TaskCheckEvaluators.WorkflowCompletion(
            evidenceId, "workflow-completes", round.RoundId, round.ArtifactRevision, "workflow-engine", capturedAt, invalidStatus);

        Assert.Equal(CheckResult.Unknown, produced.Result);
        Assert.False(string.IsNullOrWhiteSpace(produced.Detail));

        var result = VerificationAggregator.Aggregate([produced], [Check("workflow-completes")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Same(produced, Assert.Single(result.Outcome.Evidence));
    }

    [Fact]
    public void A_required_check_naming_an_ExpectedKind_ignores_evidence_of_any_other_kind()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");

        // A human approval claiming the check the task declared must be answered by a test run. Under
        // CheckId-only matching this would have verified the task; the expected kind stops it.
        var wrongKind = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass) with { Kind = "HumanApproval" };

        var result = VerificationAggregator.Aggregate(
            [wrongKind], [Check("tests", "TestResult")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
        Assert.Equal(0.0, result.CompletionScore);
        Assert.Empty(result.Outcome.Evidence);
    }

    [Fact]
    public void A_required_check_naming_an_ExpectedKind_is_satisfied_by_matching_evidence()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var matching = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass); // Kind "TestResult"

        var result = VerificationAggregator.Aggregate(
            [matching], [Check("tests", "TestResult")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Verified, result.Outcome.Status);
        Assert.Same(matching, Assert.Single(result.Outcome.Evidence));
    }

    [Fact]
    public void An_ExpectedKind_match_is_ordinal_and_case_sensitive()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var wrongCase = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass) with { Kind = "testresult" };

        var result = VerificationAggregator.Aggregate(
            [wrongCase], [Check("tests", "TestResult")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, result.Outcome.Status);
    }

    [Fact]
    public void A_null_ExpectedKind_accepts_evidence_of_any_kind()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var approval = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass) with { Kind = "HumanApproval" };

        var result = VerificationAggregator.Aggregate(
            [approval], [Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Verified, result.Outcome.Status);
    }

    [Fact]
    public void Evidence_of_the_wrong_kind_cannot_hide_a_Fail_recorded_by_the_expected_kind()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var failingTest = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Fail);
        var approvalPass = MakeEvidence(round.RoundId, round.ArtifactRevision, "tests", CheckResult.Pass) with { Kind = "HumanApproval" };

        var result = VerificationAggregator.Aggregate(
            [failingTest, approvalPass], [Check("tests", "TestResult")], round, round.ArtifactRevision, DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Failed, result.Outcome.Status);
        Assert.Same(failingTest, Assert.Single(result.Outcome.Evidence));
    }

    [Fact]
    public void A_malformed_required_check_throws_rather_than_being_tolerated()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");

        Assert.Throws<ArgumentException>(() =>
            VerificationAggregator.Aggregate([], [null!], round, round.ArtifactRevision, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            VerificationAggregator.Aggregate([], [Check("  ")], round, round.ArtifactRevision, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            VerificationAggregator.Aggregate([], [Check("tests", "  ")], round, round.ArtifactRevision, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Duplicate_check_ids_throw_even_when_their_expected_kinds_differ()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");

        Assert.Throws<ArgumentException>(() => VerificationAggregator.Aggregate(
            [], [Check("tests", "TestResult"), Check("tests", "HumanApproval")], round, round.ArtifactRevision, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void AC6_cancellation_propagates_as_an_exception_rather_than_returning_any_VerificationResult()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            VerificationAggregator.Aggregate([], [Check("tests")], round, round.ArtifactRevision, DateTimeOffset.UtcNow, cts.Token));
    }

    [Fact]
    public void AC6_cancellation_requested_mid_aggregation_still_propagates_rather_than_returning_a_partial_result()
    {
        var round = new ClosedVerificationRound(Guid.NewGuid(), "rev-1");
        var evidence = new[] { MakeEvidence(round.RoundId, round.ArtifactRevision, "build", CheckResult.Pass) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            VerificationAggregator.Aggregate(evidence, [Check("build"), Check("tests"), Check("lint")], round, round.ArtifactRevision, DateTimeOffset.UtcNow, cts.Token));
    }
}
