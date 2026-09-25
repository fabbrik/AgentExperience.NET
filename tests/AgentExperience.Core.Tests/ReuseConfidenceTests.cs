using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Indexing;
using AgentExperience.Core.Lifecycle;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 3.4: the versioned confidence heuristic, the independence keys that decide which submissions
/// may move a counter, and Core's evidence path -- which reads the record, computes the new counters and
/// score from what it read, and submits them with that revision. These tests pin the documented
/// 2/3 -> 3/4 -> 3/5 sequence, the refusals, and the rule that a score never changes eligibility.
/// </summary>
public class ReuseConfidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Authorization = new("tenant-1", "principal-7", ["experience:write"], Now);
    private static readonly ExperienceStatus[] EveryStatus = Enum.GetValues<ExperienceStatus>();

    [Fact]
    public void The_score_is_one_plus_S_over_two_plus_S_plus_F()
    {
        // The three the documentation quotes, written as the fractions they are rather than as decimals,
        // so a change to the rule cannot hide behind rounding.
        Assert.Equal(2d / 3d, ReuseConfidenceHeuristic.Score(1, 0));
        Assert.Equal(3d / 4d, ReuseConfidenceHeuristic.Score(2, 0));
        Assert.Equal(3d / 5d, ReuseConfidenceHeuristic.Score(2, 1));

        // And the rest of the small table, including the prior at zero evidence.
        Assert.Equal(1d / 2d, ReuseConfidenceHeuristic.Score(0, 0));
        Assert.Equal(1d / 3d, ReuseConfidenceHeuristic.Score(0, 1));
        Assert.Equal(2d / 4d, ReuseConfidenceHeuristic.Score(1, 1));
        Assert.Equal(1d / 4d, ReuseConfidenceHeuristic.Score(0, 2));
    }

    [Fact]
    public void The_value_finalization_stamps_is_the_heuristic_applied_to_the_counters_it_creates()
    {
        // The initial validation is counted once and never again, so these two have to be the same
        // number by construction. Were they independent constants, the first piece of evidence a record
        // received would move its score by whatever gap had opened between them.
        Assert.Equal(
            ExperienceFinalizationService.InitialValidatedReuseConfidence,
            ReuseConfidenceHeuristic.Score(ExperienceFinalizationService.InitialSupportingValidations, 0));
        Assert.Equal(2d / 3d, ExperienceFinalizationService.InitialValidatedReuseConfidence);
        Assert.Equal(1, ExperienceFinalizationService.InitialSupportingValidations);
    }

    [Fact]
    public void The_score_stays_strictly_inside_zero_and_one_for_every_sequence()
    {
        foreach (var supporting in new[] { 0, 1, 2, 7, 1_000, int.MaxValue })
        {
            foreach (var contradictions in new[] { 0, 1, 2, 7, 1_000, int.MaxValue })
            {
                var score = ReuseConfidenceHeuristic.Score(supporting, contradictions);

                Assert.True(score > 0, $"{supporting}/{contradictions} produced {score}");
                Assert.True(score < 1, $"{supporting}/{contradictions} produced {score}");
            }
        }

        // Monotone in both directions: supporting evidence never lowers the score, contradicting
        // evidence never raises it. That is the whole of what the number promises.
        Assert.True(ReuseConfidenceHeuristic.Score(3, 1) > ReuseConfidenceHeuristic.Score(2, 1));
        Assert.True(ReuseConfidenceHeuristic.Score(2, 2) < ReuseConfidenceHeuristic.Score(2, 1));
    }

    [Fact]
    public void A_negative_counter_is_a_caller_error_rather_than_a_number()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReuseConfidenceHeuristic.Score(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReuseConfidenceHeuristic.Score(0, -1));
    }

    [Fact]
    public void Every_accepted_update_records_the_rule_version_that_produced_it()
    {
        var update = ReuseConfidenceHeuristic.Apply(
            Record(ExperienceStatus.Validated, 2d / 3d, 1, 0),
            Guid.NewGuid(),
            ConfidenceEvidenceKind.Supporting,
            ConfidenceEvidenceSource.Machine,
            Guid.NewGuid(),
            Guid.NewGuid(),
            reviewerIdentity: null);

        Assert.Equal(ReuseConfidenceHeuristic.RuleVersion, update.RuleVersion);
        Assert.False(string.IsNullOrWhiteSpace(ReuseConfidenceHeuristic.RuleVersion));
    }

    [Fact]
    public void Machine_evidence_keys_on_the_run_and_the_round_and_human_evidence_on_the_reviewer_and_the_run()
    {
        var run = Guid.NewGuid();
        var otherRun = Guid.NewGuid();
        var round = Guid.NewGuid();
        var otherRound = Guid.NewGuid();

        // Same observation, same key -- which is what makes a resubmission countable exactly once.
        Assert.Equal(ConfidenceIndependenceKey.ForMachine(run, round), ConfidenceIndependenceKey.ForMachine(run, round));
        Assert.Equal(ConfidenceIndependenceKey.ForHuman("reviewer-a", run), ConfidenceIndependenceKey.ForHuman("reviewer-a", run));

        // A different run, round, or reviewer is a different observation.
        Assert.NotEqual(ConfidenceIndependenceKey.ForMachine(run, round), ConfidenceIndependenceKey.ForMachine(otherRun, round));
        Assert.NotEqual(ConfidenceIndependenceKey.ForMachine(run, round), ConfidenceIndependenceKey.ForMachine(run, otherRound));
        Assert.NotEqual(ConfidenceIndependenceKey.ForHuman("reviewer-a", run), ConfidenceIndependenceKey.ForHuman("reviewer-b", run));
        Assert.NotEqual(ConfidenceIndependenceKey.ForHuman("reviewer-a", run), ConfidenceIndependenceKey.ForHuman("reviewer-a", otherRun));

        // The two keyings never collide with each other, whatever the identifiers.
        Assert.NotEqual(
            ConfidenceIndependenceKey.ForMachine(run, round).Value,
            ConfidenceIndependenceKey.ForHuman(round.ToString(), run).Value);

        // The exact strings, because the database derives the same ones from its own columns.
        Assert.Equal($"machine:{run:D}:{round:D}", ConfidenceIndependenceKey.ForMachine(run, round).Value);
        Assert.Equal($"human:reviewer-a:{run:D}", ConfidenceIndependenceKey.ForHuman("reviewer-a", run).Value);
        Assert.Equal(ConfidenceIndependenceKey.ForMachine(run, round).Value.ToLowerInvariant(), ConfidenceIndependenceKey.ForMachine(run, round).Value);
    }

    [Fact]
    public void A_submission_with_no_key_to_count_it_under_is_a_caller_error()
    {
        var update = ReuseConfidenceHeuristic.Apply(
            Record(ExperienceStatus.Validated, 2d / 3d, 1, 0),
            Guid.NewGuid(),
            ConfidenceEvidenceKind.Supporting,
            ConfidenceEvidenceSource.Machine,
            Guid.NewGuid(),
            verificationRoundId: null,
            reviewerIdentity: null);

        Assert.Throws<ArgumentException>(() => ReuseConfidenceHeuristic.IndependenceKeyFor(update));
        Assert.Throws<ArgumentException>(() => ConfidenceIndependenceKey.ForHuman("   ", Guid.NewGuid()));
    }

    [Fact]
    public async Task A_confirmation_then_a_contradiction_walk_a_validated_record_from_two_thirds_to_three_quarters_to_three_fifths()
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1));
        var service = Trusting(store);

        var confirmation = await service.ApplyEvidenceAsync(Authorization, Machine(experienceId), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, confirmation.Outcome);
        Assert.True(confirmation.Counted);
        Assert.Equal(3d / 4d, confirmation.ReuseConfidence);
        Assert.Equal(2, confirmation.SupportingValidations);
        Assert.Equal(0, confirmation.Contradictions);
        // Supporting evidence never moves a status by itself.
        Assert.Equal(ExperienceStatus.Validated, confirmation.Status);
        Assert.Equal(2, confirmation.Revision);

        store.Record = Record(ExperienceStatus.Validated, 3d / 4d, 2, 0, experienceId, revision: 2);
        var contradiction = await service.ApplyEvidenceAsync(
            Authorization,
            Machine(experienceId) with { EvidenceId = Guid.NewGuid(), EventId = Guid.NewGuid(), Kind = ConfidenceEvidenceKind.Contradicting },
            CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, contradiction.Outcome);
        Assert.Equal(3d / 5d, contradiction.ReuseConfidence);
        Assert.Equal(2, contradiction.SupportingValidations);
        Assert.Equal(1, contradiction.Contradictions);
        // The status change is what takes it out of reuse -- never the number.
        Assert.Equal(ExperienceStatus.Contested, contradiction.Status);

        // Both updates are reconstructable from what Core stamped: prior and new score, prior and new
        // counters, the evidence ID, and the rule version.
        foreach (var applied in new[] { confirmation, contradiction })
        {
            var carried = applied.Event!.Confidence!;
            Assert.Equal(applied.Update!.EvidenceId, carried.EvidenceId);
            Assert.Equal(ReuseConfidenceHeuristic.RuleVersion, carried.RuleVersion);
            Assert.NotEqual(carried.PriorReuseConfidence, carried.NewReuseConfidence);
        }

        Assert.Equal(2d / 3d, confirmation.Update!.PriorReuseConfidence);
        Assert.Equal(3d / 4d, contradiction.Update!.PriorReuseConfidence);
    }

    [Fact]
    public async Task A_contradiction_against_an_already_contested_record_keeps_its_status_and_still_counts()
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Contested, 3d / 5d, 2, 1, experienceId, revision: 3));

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization,
            Machine(experienceId) with { Kind = ConfidenceEvidenceKind.Contradicting },
            CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(ExperienceStatus.Contested, result.Status);
        Assert.Equal(2, result.SupportingValidations);
        Assert.Equal(2, result.Contradictions);
        Assert.Equal(3d / 6d, result.ReuseConfidence);

        // Prior and current status are the same, which the ordinary transition table refuses. It is
        // allowed here, and only here, because the event is carrying a counter rather than a move.
        var stamped = Assert.Single(store.Commits).Event;
        Assert.Equal(stamped.PriorStatus, stamped.CurrentStatus);
        Assert.False(ExperienceLifecycleService.IsTransitionAllowed(ExperienceStatus.Contested, ExperienceStatus.Contested));
    }

    [Fact]
    public async Task Repeated_reinforcement_is_expressible_through_the_counters_on_a_reinforced_record()
    {
        // Validated -> Reinforced happens once, and the table still refuses Reinforced -> Reinforced.
        // Evidence is how a record keeps being reinforced after that.
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Reinforced, 3d / 4d, 2, 0, experienceId, revision: 2));

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(experienceId), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(ExperienceStatus.Reinforced, result.Status);
        Assert.Equal(3, result.SupportingValidations);
        Assert.Equal(4d / 5d, result.ReuseConfidence);
    }

    [Theory]
    [InlineData(ExperienceStatus.Revoked)]
    [InlineData(ExperienceStatus.Quarantined)]
    [InlineData(ExperienceStatus.Candidate)]
    [InlineData(ExperienceStatus.Stale)]
    [InlineData(ExperienceStatus.Superseded)]
    public async Task Evidence_against_a_record_that_does_not_accept_it_is_refused_before_anything_is_written(ExperienceStatus status)
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(status, 2d / 3d, 1, 0, experienceId, revision: 4));

        foreach (var kind in Enum.GetValues<ConfidenceEvidenceKind>())
        {
            var result = await Trusting(store).ApplyEvidenceAsync(
                Authorization, Machine(experienceId) with { Kind = kind }, CancellationToken.None);

            Assert.Equal(ConfidenceUpdateOutcome.Ineligible, result.Outcome);
            Assert.Equal(status, result.Status);
            Assert.Null(result.Update);
            Assert.False(result.Counted);
            Assert.Empty(store.Commits);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        }
    }

    [Fact]
    public void The_statuses_that_accept_evidence_are_exactly_the_live_and_the_disputed_ones()
    {
        Assert.Equal(
            [ExperienceStatus.Validated, ExperienceStatus.Reinforced, ExperienceStatus.Contested],
            ReuseConfidenceHeuristic.AcceptsEvidence);

        foreach (var status in EveryStatus)
        {
            Assert.Equal(
                ReuseConfidenceHeuristic.AcceptsEvidence.Contains(status),
                ReuseConfidenceHeuristic.AcceptsEvidenceIn(status));
        }

        // Accepting evidence is not eligibility, and neither implies the other: a Contested record takes
        // evidence and is never reused, and no amount of evidence makes it eligible again.
        Assert.True(ReuseConfidenceHeuristic.AcceptsEvidenceIn(ExperienceStatus.Contested));
        Assert.False(ExperienceLifecycleService.IsEligible(ExperienceStatus.Contested));
    }

    [Fact]
    public async Task The_reviewer_is_the_hosts_principal_and_the_request_has_no_way_to_say_otherwise()
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1));

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization,
            Human(experienceId),
            CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(Authorization.PrincipalId, result.Update!.ReviewerIdentity);
        Assert.Equal(
            ConfidenceIndependenceKey.ForHuman(Authorization.PrincipalId, result.Update.RunId),
            ReuseConfidenceHeuristic.IndependenceKeyFor(result.Update));

        // Machine evidence never carries one, so the human key can never be forged through it.
        var machine = await Trusting(
            new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1)))
            .ApplyEvidenceAsync(Authorization, Machine(experienceId), CancellationToken.None);

        Assert.Null(machine.Update!.ReviewerIdentity);
    }

    [Fact]
    public async Task A_duplicate_the_store_declined_to_count_is_still_accepted_and_still_recorded()
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1))
        {
            CountEvidence = false,
        };

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(experienceId), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, result.Outcome);
        Assert.False(result.Counted);
        Assert.Equal(2d / 3d, result.ReuseConfidence);
        Assert.Equal(1, result.SupportingValidations);
        Assert.Equal(0, result.Contradictions);

        // Core still submitted the increment; only the transaction that saw the key decided otherwise.
        Assert.True(Assert.Single(store.Commits).Event.Confidence!.Counted);
    }

    [Fact]
    public async Task A_contradiction_that_contests_a_record_removes_its_embedding_afterwards()
    {
        var experienceId = Guid.NewGuid();
        var index = new FakeEmbeddingIndex();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1));

        var contested = await Trusting(store, Indexing(index)).ApplyEvidenceAsync(
            Authorization,
            Machine(experienceId) with { Kind = ConfidenceEvidenceKind.Contradicting },
            CancellationToken.None);

        // Contesting takes the record out of reuse, so the same hygiene rule an ordinary transition
        // follows applies here: the stored vector is removed after the fact, and never as a condition.
        Assert.Equal(ConfidenceUpdateOutcome.Applied, contested.Outcome);
        Assert.Equal(ExperienceStatus.Contested, contested.Status);
        Assert.Equal(ExperienceDeindexingOutcome.NotIndexed, contested.Deindexing!.Outcome);
        Assert.Equal((TestScope, experienceId), Assert.Single(index.Removals));
    }

    [Fact]
    public async Task Supporting_evidence_and_an_unapplied_submission_never_de_index()
    {
        var experienceId = Guid.NewGuid();
        var index = new FakeEmbeddingIndex();

        // Supporting evidence leaves the record eligible, so there is nothing to clean up.
        var supporting = await Trusting(
                new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1)),
                Indexing(index))
            .ApplyEvidenceAsync(Authorization, Machine(experienceId), CancellationToken.None);

        // A duplicate contradiction moved nothing: the store reports the status it left the record in,
        // and that is what the hook is asked about -- not the status an uncounted submission would have
        // produced had it counted.
        var duplicate = await Trusting(
                new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1))
                {
                    CountEvidence = false,
                },
                Indexing(index))
            .ApplyEvidenceAsync(
                Authorization,
                Machine(experienceId) with { Kind = ConfidenceEvidenceKind.Contradicting },
                CancellationToken.None);

        Assert.Null(supporting.Deindexing);
        Assert.Equal(ExperienceStatus.Validated, duplicate.Status);
        Assert.Null(duplicate.Deindexing);
        Assert.Empty(index.Removals);
    }

    [Fact]
    public async Task A_store_that_commits_a_payload_without_reporting_it_is_taken_at_its_word_that_nothing_moved()
    {
        // An out-of-tree store that persists the payload but leaves AppliedConfidence null tells us
        // nothing about the independence key. Reporting the submitted increment would be inventing an
        // answer, so the safe reading is that no counter moved.
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1))
        {
            CommitResult = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, 2, null, []),
        };

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(experienceId), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, result.Outcome);
        Assert.False(result.Counted);
        Assert.Equal(2d / 3d, result.ReuseConfidence);
        Assert.Equal(1, result.SupportingValidations);
        Assert.Equal(0, result.Contradictions);
    }

    [Fact]
    public async Task A_retry_after_the_record_stopped_accepting_evidence_is_refused_rather_than_replayed()
    {
        // The gate runs on the record Core read, before the store is asked anything, so it takes
        // precedence over the store's own idempotency check. Pinned because the consequence is worth
        // knowing: the original update is durable and in the history, but a retry after a revocation
        // reports Ineligible rather than replaying Applied.
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1));
        var service = Trusting(store);
        var request = Machine(experienceId);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await service.ApplyEvidenceAsync(Authorization, request, CancellationToken.None)).Outcome);

        store.Record = Record(ExperienceStatus.Revoked, 3d / 4d, 2, 0, experienceId, revision: 3);
        var retry = await service.ApplyEvidenceAsync(Authorization, request, CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Ineligible, retry.Outcome);
        Assert.Equal(ExperienceStatus.Revoked, retry.Status);
        Assert.Single(store.Commits);
    }

    [Fact]
    public async Task Human_evidence_from_a_blank_or_padded_principal_is_refused_before_the_record_is_read()
    {
        var experienceId = Guid.NewGuid();

        foreach (var principal in new[] { "", "   ", " alice", "alice\t" })
        {
            var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1));

            var result = await Trusting(store).ApplyEvidenceAsync(
                Authorization with { PrincipalId = principal },
                Human(experienceId),
                CancellationToken.None);

            Assert.Equal(ConfidenceUpdateOutcome.Invalid, result.Outcome);
            Assert.Contains(result.Errors, error => error.Path == "Authorization.PrincipalId");
            Assert.False(store.Reads);
        }

        // Machine evidence does not rest on the principal, so it is unaffected.
        var machine = await Trusting(
                new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1)))
            .ApplyEvidenceAsync(Authorization with { PrincipalId = " " }, Machine(experienceId), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, machine.Outcome);
    }

    [Fact]
    public async Task A_stored_counter_that_cannot_take_evidence_is_a_typed_refusal_rather_than_an_exception()
    {
        var experienceId = Guid.NewGuid();

        foreach (var (record, path) in new[]
        {
            (Record(ExperienceStatus.Validated, 2d / 3d, -1, 0, experienceId), "SupportingValidations"),
            (Record(ExperienceStatus.Validated, 2d / 3d, 1, -4, experienceId), "Contradictions"),
            (Record(ExperienceStatus.Validated, 2d / 3d, int.MaxValue, 0, experienceId), "SupportingValidations"),
        })
        {
            var store = new EvidenceStore(record);

            var result = await Trusting(store).ApplyEvidenceAsync(
                Authorization, Machine(experienceId), CancellationToken.None);

            Assert.Equal(ConfidenceUpdateOutcome.Invalid, result.Outcome);
            Assert.Contains(result.Errors, error => error.Path == path);
            Assert.Empty(store.Commits);
        }
    }

    [Fact]
    public async Task A_store_reporting_Found_with_no_record_is_NotFound_rather_than_an_exception()
    {
        var store = new EvidenceStore(record: null) { FoundWithNoRecord = true };

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.NotFound, result.Outcome);
        Assert.Empty(store.Commits);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task The_submitted_revision_is_the_one_the_record_was_read_at()
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 12));

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(experienceId), CancellationToken.None);

        var stamped = Assert.Single(store.Commits).Event;
        Assert.Equal(12, stamped.ExpectedRevision);
        Assert.Equal(ExperienceStatus.Validated, stamped.PriorStatus);
        Assert.Equal(13, result.Revision);

        // The arithmetic and the concurrency guard are about the same version of the record.
        Assert.Equal(1, stamped.Confidence!.PriorSupportingValidations);
        Assert.Equal(2d / 3d, stamped.Confidence.PriorReuseConfidence);
    }

    [Theory]
    [InlineData(ExperienceStoreOutcome.StaleRevision, ConfidenceUpdateOutcome.StaleRevision)]
    [InlineData(ExperienceStoreOutcome.StatusMismatch, ConfidenceUpdateOutcome.StatusMismatch)]
    [InlineData(ExperienceStoreOutcome.Conflict, ConfidenceUpdateOutcome.Conflict)]
    [InlineData(ExperienceStoreOutcome.NotFound, ConfidenceUpdateOutcome.NotFound)]
    [InlineData(ExperienceStoreOutcome.Denied, ConfidenceUpdateOutcome.Denied)]
    [InlineData(ExperienceStoreOutcome.Invalid, ConfidenceUpdateOutcome.Invalid)]
    [InlineData(ExperienceStoreOutcome.Deleted, ConfidenceUpdateOutcome.Deleted)]
    public async Task A_store_refusal_is_surfaced_one_to_one_and_reports_no_movement(
        ExperienceStoreOutcome stored,
        ConfidenceUpdateOutcome expected)
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1))
        {
            CommitResult = new ExperienceLifecycleCommitResult(stored, 9, ExperienceStatus.Reinforced, []),
        };

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(experienceId), CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Update);
        Assert.False(result.Counted);
        Assert.Null(result.ReuseConfidence);
        Assert.NotNull(result.Event);
    }

    [Fact]
    public async Task A_record_readable_only_through_a_grant_is_never_written_to()
    {
        var experienceId = Guid.NewGuid();
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, experienceId, revision: 1))
        {
            SharedByGrant = true,
        };

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(experienceId), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.NotFound, result.Outcome);
        Assert.Empty(store.Commits);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task A_record_erased_before_the_read_is_a_terminal_refusal_and_nothing_is_committed()
    {
        // The read is the first port call, and for its own scope the store answers Deleted for a
        // tombstone. That is a typed refusal, not a store failure, and nothing reaches the commit.
        var store = new EvidenceStore(record: null) { ReadOutcome = ExperienceStoreOutcome.Deleted };

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Deleted, result.Outcome);
        Assert.True(store.Reads);
        Assert.Empty(store.Commits);
        Assert.Null(result.Event);
        Assert.Null(result.Update);
        Assert.False(result.Counted);
    }

    [Fact]
    public async Task A_record_that_is_not_in_this_scope_is_reported_before_any_arithmetic()
    {
        var store = new EvidenceStore(record: null);

        var result = await Trusting(store).ApplyEvidenceAsync(
            Authorization, Machine(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.NotFound, result.Outcome);
        Assert.Empty(store.Commits);
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task A_malformed_submission_never_reaches_the_store_and_names_the_field()
    {
        var valid = Machine(Guid.NewGuid());

        foreach (var (request, path) in new[]
        {
            (valid with { EvidenceId = Guid.Empty }, nameof(valid.EvidenceId)),
            (valid with { EventId = Guid.Empty }, nameof(valid.EventId)),
            (valid with { ExperienceId = Guid.Empty }, nameof(valid.ExperienceId)),
            (valid with { RunId = Guid.Empty }, nameof(valid.RunId)),
            (valid with { VerificationRoundId = null }, nameof(valid.VerificationRoundId)),
            (valid with { Source = ConfidenceEvidenceSource.Human }, nameof(valid.VerificationRoundId)),
            (valid with { Kind = (ConfidenceEvidenceKind)99 }, nameof(valid.Kind)),
            (valid with { Source = (ConfidenceEvidenceSource)99 }, nameof(valid.Source)),
            (valid with { Reason = "  " }, nameof(valid.Reason)),
            (valid with { Producer = "" }, nameof(valid.Producer)),
            (valid with { OccurredAt = default }, nameof(valid.OccurredAt)),
        })
        {
            var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, request.ExperienceId, revision: 1));

            var result = await Trusting(store).ApplyEvidenceAsync(
                Authorization, request, CancellationToken.None);

            Assert.Equal(ConfidenceUpdateOutcome.Invalid, result.Outcome);
            Assert.Contains(result.Errors, error => error.Path == path);
            Assert.Empty(store.Commits);
            Assert.False(store.Reads, $"{path}: a malformed request must be refused before the record is read.");
        }
    }

    [Fact]
    public async Task An_ordinary_transition_never_carries_a_confidence_payload()
    {
        // CommitAsync is the other entry point, and it is unchanged by this story: it stamps no
        // confidence, so no counter can move through it.
        var store = new EvidenceStore(Record(ExperienceStatus.Validated, 2d / 3d, 1, 0, Guid.NewGuid(), revision: 1));

        var result = await Trusting(store).CommitAsync(
            Authorization,
            new CommitLifecycleTransitionRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                TestScope,
                ExperienceStatus.Validated,
                ExperienceStatus.Reinforced,
                "reuse succeeded again",
                "tests",
                Now,
                1),
            CancellationToken.None);

        Assert.Equal(LifecycleTransitionOutcome.Committed, result.Outcome);
        Assert.Null(Assert.Single(store.Commits).Event.Confidence);
    }

    /// <summary>
    /// These tests are about the arithmetic, the store contract and the reviewer rule, not about verifying
    /// an independence key's inputs (<c>VerifiedIndependenceTests</c> covers that against a store that
    /// knows runs), so they run with the host's identifiers trusted: the opt-out, which keeps only the
    /// own-run rule.
    /// </summary>
    private static ExperienceLifecycleService Trusting(IExperienceRecordStore store, ExperienceIndexingService? indexing = null) =>
        new(store, indexing, new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });

    private static ApplyConfidenceEvidenceRequest Machine(Guid experienceId) => new(
        EventId: Guid.NewGuid(),
        ExperienceId: experienceId,
        Scope: TestScope,
        EvidenceId: Guid.NewGuid(),
        Kind: ConfidenceEvidenceKind.Supporting,
        Source: ConfidenceEvidenceSource.Machine,
        RunId: Guid.NewGuid(),
        VerificationRoundId: Guid.NewGuid(),
        Reason: "the lesson was reused and the checks passed",
        Producer: "verification-aggregator/1.0.0",
        OccurredAt: Now);

    private static ApplyConfidenceEvidenceRequest Human(Guid experienceId) =>
        Machine(experienceId) with { Source = ConfidenceEvidenceSource.Human, VerificationRoundId = null };

    private static ExperienceIndexingService Indexing(FakeEmbeddingIndex index) =>
        new(index, new FakeEmbeddingGenerator());

    private static ExperienceRecord Record(
        ExperienceStatus status,
        double confidence,
        int supporting,
        int contradictions,
        Guid? experienceId = null,
        long revision = 1) => new(
            ExperienceId: experienceId ?? Guid.NewGuid(),
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
            Status: status,
            ReuseConfidence: confidence,
            SupportingValidations: supporting,
            Contradictions: contradictions,
            Revision: revision,
            CreatedAt: Now,
            UpdatedAt: Now);

    /// <summary>
    /// Answers the one read the evidence path makes and records the commit it produces. Its default
    /// commit behaves like the real adapter's accepted case: the submitted payload is what was stored.
    /// </summary>
    private sealed class EvidenceStore : IExperienceRecordStore
    {
        public EvidenceStore(ExperienceRecord? record) => Record = record;

        public ExperienceRecord? Record { get; set; }

        public bool SharedByGrant { get; init; }

        /// <summary>A store that answers Found and hands back nothing: a contract violation to survive.</summary>
        public bool FoundWithNoRecord { get; init; }

        public bool CountEvidence { get; init; } = true;

        public ExperienceLifecycleCommitResult? CommitResult { get; init; }

        /// <summary>When set, the read answers this outcome with no record, whatever <see cref="Record"/> holds.</summary>
        public ExperienceStoreOutcome? ReadOutcome { get; init; }

        public bool Reads { get; private set; }

        public List<(Scope Scope, LifecycleEvent Event)> Commits { get; } = [];

        public Task<ExperienceRecordGetResult> GetAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            CancellationToken cancellationToken)
        {
            Assert.NotNull(authorization);
            Reads = true;

            if (FoundWithNoRecord)
            {
                return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, null, []));
            }

            if (ReadOutcome is { } outcome)
            {
                return Task.FromResult(new ExperienceRecordGetResult(outcome, null, []));
            }

            return Task.FromResult(Record is { } record
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
            Commits.Add((scope, lifecycleEvent));

            if (CommitResult is { } configured)
            {
                return Task.FromResult(configured);
            }

            if (lifecycleEvent.Confidence is { } confidence && !CountEvidence)
            {
                // What the adapter does with a taken independence key: the ledger row is written and
                // nothing else moves, so the record keeps its revision and its status.
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
            throw new InvalidOperationException("The lifecycle service must not create records.");

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not query records.");

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The lifecycle service must not read history.");

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The evidence path must not check supersession.");
    }
}
