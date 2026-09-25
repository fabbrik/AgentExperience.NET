using AgentExperience.Core.Confidence;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 6.6 (KL-11): an independence key's inputs are verified against what the library itself knows. A
/// run must be one finalized into a record in the evidence's scope or held by the capture service, and
/// never the record's own; a machine round must be the one finalization closed for that run; a human
/// assessment must present a library-minted token that verifies, has not expired, covers the record, and
/// is spent once per record. Each refusal writes nothing.
/// </summary>
public class VerifiedIndependenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly Scope OtherScope = new("tenant-1", "app-1", "project-2");
    private static readonly AuthorizationContext Reviewer = new("tenant-1", "reviewer-1", ["experience:write"], Now);
    private static readonly AuthorizationContext OtherReviewer = Reviewer with { PrincipalId = "reviewer-2" };
    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(value => (byte)(value * 7))];

    private static readonly SanitizationOptions Permissive = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
        ["ToolResult"] = new(new HashSet<string>(StringComparer.Ordinal) { "value" }, new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
    });

    // ---- RunId ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_forged_run_is_refused_as_unknown_and_nothing_is_written()
    {
        var world = new World();

        var machine = await world.Lifecycle.ApplyEvidenceAsync(
            Reviewer, world.Machine(runId: Guid.NewGuid(), roundId: Guid.NewGuid()), CancellationToken.None);

        AssertRefused(machine, IndependenceRefusal.UnknownRun);

        // A genuine token cannot vouch for a run: minted for an invented run, it still names an unknown one.
        var invented = Guid.NewGuid();
        var token = world.Issuer.Issue(Reviewer, TestScope, invented, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var human = await world.Lifecycle.ApplyEvidenceAsync(
            Reviewer, world.Human(invented, token.Token), CancellationToken.None);

        AssertRefused(human, IndependenceRefusal.UnknownRun);
        Assert.Empty(world.Store.Commits);
        Assert.Equal(0, world.Store.EvidenceRows);
    }

    [Fact]
    public async Task Evidence_naming_the_records_own_source_run_is_refused_in_both_modes()
    {
        var world = new World();
        var ownRun = world.Target.SourceRunId;

        var verified = await world.Lifecycle.ApplyEvidenceAsync(
            Reviewer, world.Machine(ownRun, world.ReuseRound), CancellationToken.None);
        AssertRefused(verified, IndependenceRefusal.OwnRun);

        var trusting = new ExperienceLifecycleService(
            world.Store,
            indexingService: null,
            new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });
        Assert.Equal(IndependenceVerification.TrustHostSuppliedIdentifiers, trusting.IndependenceVerification);

        var trusted = await trusting.ApplyEvidenceAsync(Reviewer, world.Machine(ownRun, Guid.NewGuid()), CancellationToken.None);
        AssertRefused(trusted, IndependenceRefusal.OwnRun);

        // And the opt-out is otherwise the previous behaviour: an invented run and round count.
        var invented = await trusting.ApplyEvidenceAsync(Reviewer, world.Machine(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, invented.Outcome);
        Assert.True(invented.Counted);
        Assert.Null(invented.Update!.AssessmentId);
    }

    [Fact]
    public async Task A_run_finalized_only_in_another_scope_or_readable_only_through_a_grant_is_unknown()
    {
        var world = new World();

        // Finalized, with a closed round -- but in another scope. The record the evidence's scope would
        // derive for it does not exist.
        var elsewhere = Guid.NewGuid();
        var elsewhereRound = Guid.NewGuid();
        world.Store.Seed(Finalized(elsewhere, OtherScope, elsewhereRound));

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(elsewhere, elsewhereRound), CancellationToken.None),
            IndependenceRefusal.UnknownRun);

        // Readable here only through a sharing grant: a grant never makes another scope's run this one's.
        world.Store.SharedByGrant.Add(world.ReuseRecord.ExperienceId);

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(world.ReuseRun, world.ReuseRound), CancellationToken.None),
            IndependenceRefusal.UnknownRun);

        // Erased: a tombstone vouches for nothing.
        world.Store.SharedByGrant.Clear();
        world.Store.Erased.Add(world.ReuseRecord.ExperienceId);

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(world.ReuseRun, world.ReuseRound), CancellationToken.None),
            IndependenceRefusal.UnknownRun);
        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task A_run_the_capture_service_holds_in_the_scope_is_known_and_one_it_holds_elsewhere_is_not()
    {
        var world = new World();
        var held = world.StartCapturedRun(TestScope);
        var heldElsewhere = world.StartCapturedRun(OtherScope);

        var token = world.Issuer.Issue(Reviewer, TestScope, held, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var applied = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(held, token.Token), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);
        Assert.True(applied.Counted);
        Assert.Equal(token.AssessmentId, applied.Update!.AssessmentId);

        var elsewhereToken = world.Issuer.Issue(Reviewer, TestScope, heldElsewhere, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(heldElsewhere, elsewhereToken.Token), CancellationToken.None),
            IndependenceRefusal.UnknownRun);

        // A service with no capture service wired in knows only finalized runs.
        var finalizedOnly = new ExperienceLifecycleService(world.Store, indexingService: null, new ExperienceIndependenceOptions { AssessmentTokenKey = Key });
        var other = world.Issuer.Issue(OtherReviewer, TestScope, held, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        AssertRefused(
            await finalizedOnly.ApplyEvidenceAsync(OtherReviewer, world.Human(held, other.Token), CancellationToken.None),
            IndependenceRefusal.UnknownRun);
    }

    // ---- VerificationRoundId -------------------------------------------------------------------------

    [Fact]
    public async Task A_forged_round_is_refused_for_a_real_run_and_so_is_every_round_of_a_run_that_closed_none()
    {
        var world = new World();

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(world.ReuseRun, Guid.NewGuid()), CancellationToken.None),
            IndependenceRefusal.UnknownRound);

        // Finalized with no closed round: there is no round it can vouch for.
        var noRound = Guid.NewGuid();
        world.Store.Seed(Finalized(noRound, TestScope, closedRound: null));
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(noRound, Guid.NewGuid()), CancellationToken.None),
            IndependenceRefusal.UnknownRound);

        // Held by the capture service but never finalized: nothing closed a round for it.
        var held = world.StartCapturedRun(TestScope);
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(held, Guid.NewGuid()), CancellationToken.None),
            IndependenceRefusal.UnknownRound);

        // Another run's genuine round is not this run's.
        var second = Guid.NewGuid();
        world.Store.Seed(Finalized(second, TestScope, Guid.NewGuid()));
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(second, world.ReuseRound), CancellationToken.None),
            IndependenceRefusal.UnknownRound);

        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task Machine_evidence_carrying_an_assessment_token_is_malformed()
    {
        var world = new World();

        var result = await world.Lifecycle.ApplyEvidenceAsync(
            Reviewer,
            world.Machine(world.ReuseRun, world.ReuseRound) with { AssessmentToken = "aexat1.anything" },
            CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Errors, error => error.Path == nameof(ApplyConfidenceEvidenceRequest.AssessmentToken));
    }

    // ---- AssessmentId --------------------------------------------------------------------------------

    [Fact]
    public async Task Human_evidence_without_a_token_or_without_a_key_to_check_it_is_refused()
    {
        var world = new World();

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token: null), CancellationToken.None),
            IndependenceRefusal.AssessmentTokenMissing);

        var keyless = new ExperienceLifecycleService(world.Store, indexingService: null, new ExperienceIndependenceOptions());
        var token = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        AssertRefused(
            await keyless.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token.Token), CancellationToken.None),
            IndependenceRefusal.AssessmentKeyNotConfigured);
        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task A_random_GUID_a_malformed_string_or_a_tampered_token_is_refused_as_invalid()
    {
        var world = new World();
        var genuine = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]).Token;

        var forgeries = new List<string>
        {
            Guid.NewGuid().ToString(),
            Guid.NewGuid().ToString("N"),
            "aexat1.",
            "aexat1.!!!!",
            "aexat2." + genuine["aexat1.".Length..],
            genuine + "A",
            genuine[..^1],
            " " + genuine,
            new string('A', 10_000),
            "aexat1." + new string('A', 4096),
        };

        // Every single-character change to the body, including the MAC and the record list.
        for (var index = "aexat1.".Length; index < genuine.Length; index++)
        {
            var flipped = genuine[index] == 'A' ? 'B' : 'A';
            forgeries.Add(string.Concat(genuine.AsSpan(0, index), flipped.ToString(), genuine.AsSpan(index + 1)));
        }

        // Minted under a different key: well formed, and still not the library's.
        var otherKey = Key.Select(value => (byte)(value ^ 0x5A)).ToArray();
        forgeries.Add(new AssessmentTokenIssuer(new ExperienceIndependenceOptions { AssessmentTokenKey = otherKey })
            .Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]).Token);

        foreach (var forged in forgeries)
        {
            var result = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, forged), CancellationToken.None);
            AssertRefused(result, IndependenceRefusal.AssessmentTokenInvalid);
        }

        Assert.Empty(world.Store.Commits);

        // The genuine one still lands: the refusals above consumed nothing.
        var landed = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, genuine), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, landed.Outcome);
        Assert.True(landed.Counted);
    }

    [Fact]
    public async Task A_token_for_another_scope_run_reviewer_or_direction_is_refused_and_one_for_another_record_is_not_for_this_one()
    {
        var world = new World();
        var target = world.Target.ExperienceId;
        var secondRun = Guid.NewGuid();
        world.Store.Seed(Finalized(secondRun, TestScope, Guid.NewGuid()));

        var wrongScope = world.Issuer.Issue(Reviewer with { ProjectId = null }, OtherScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [target]);
        var wrongRun = world.Issuer.Issue(Reviewer, TestScope, secondRun, ConfidenceEvidenceKind.Supporting, [target]);
        var wrongReviewer = world.Issuer.Issue(OtherReviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [target]);
        var wrongDirection = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Contradicting, [target]);
        var wrongRecord = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [Guid.NewGuid()]);

        foreach (var token in new[] { wrongScope, wrongRun, wrongReviewer, wrongDirection })
        {
            AssertRefused(
                await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token.Token), CancellationToken.None),
                IndependenceRefusal.AssessmentTokenInvalid);
        }

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, wrongRecord.Token), CancellationToken.None),
            IndependenceRefusal.AssessmentTokenNotForRecord);
        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task A_tokens_expiry_is_the_one_it_was_minted_with_whatever_the_verifier_is_configured_with()
    {
        var clock = new MutableClock(Now);
        var world = new World(clock);
        var shortLived = new AssessmentTokenIssuer(new ExperienceIndependenceOptions
        {
            AssessmentTokenKey = Key,
            AssessmentTokenLifetime = TimeSpan.FromMinutes(10),
            TimeProvider = clock,
        });

        // The verifier's own lifetime is a day; this token was minted for ten minutes, and that is what holds.
        var token = shortLived.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        Assert.Equal(Now + TimeSpan.FromMinutes(10), token.ExpiresAt);

        clock.Now = Now + TimeSpan.FromMinutes(10);
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token.Token), CancellationToken.None),
            IndependenceRefusal.AssessmentTokenExpired);

        // And the reverse: a day-long token outlives a verifier configured for ten minutes.
        clock.Now = Now;
        var longLived = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var strict = new ExperienceLifecycleService(
            world.Store,
            indexingService: null,
            new ExperienceIndependenceOptions { AssessmentTokenKey = Key, AssessmentTokenLifetime = TimeSpan.FromMinutes(10), TimeProvider = clock },
            world.Capture);
        clock.Now = Now + TimeSpan.FromHours(1);
        var landed = await strict.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, longLived.Token), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, landed.Outcome);
    }

    [Fact]
    public async Task The_token_binds_every_field_of_a_scope()
    {
        // The MAC encodes the scope field by field. A field added to Scope that the encoding did not learn
        // about would silently stop binding, so this pins the count the encoding knows.
        Assert.Equal(
            ["TenantId", "ApplicationId", "ProjectId", "TeamId", "AgentId", "UserId"],
            typeof(Scope).GetProperties().Where(property => property.GetMethod?.IsPublic == true && property.Name != "EqualityContract").Select(property => property.Name));

        var world = new World();
        foreach (var variant in new[]
        {
            TestScope with { TeamId = "team-1" },
            TestScope with { AgentId = "agent-1" },
            TestScope with { UserId = "user-1" },
        })
        {
            var token = world.Issuer.Issue(Reviewer, variant, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
            AssertRefused(
                await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token.Token), CancellationToken.None),
                IndependenceRefusal.AssessmentTokenInvalid);
        }
    }

    [Fact]
    public async Task An_expired_token_is_refused_and_one_issued_in_the_future_is_invalid()
    {
        var clock = new MutableClock(Now);
        var world = new World(clock);
        var token = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);

        Assert.Equal(Now, token.IssuedAt);
        Assert.Equal(Now + ExperienceIndependenceOptions.DefaultAssessmentTokenLifetime, token.ExpiresAt);

        clock.Now = token.ExpiresAt;
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token.Token), CancellationToken.None),
            IndependenceRefusal.AssessmentTokenExpired);

        // Minted by a clock running ahead of the verifier by more than the allowed skew.
        clock.Now = Now + TimeSpan.FromHours(1);
        var early = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        clock.Now = Now;
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, early.Token), CancellationToken.None),
            IndependenceRefusal.AssessmentTokenInvalid);

        // One millisecond before expiry it is still good.
        clock.Now = token.ExpiresAt - TimeSpan.FromMilliseconds(1);
        var landed = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token.Token), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, landed.Outcome);
    }

    [Fact]
    public async Task A_contradicting_token_lets_a_reviewer_contest_a_lesson_directly_and_through_feedback()
    {
        var world = new World();
        var target = world.Target.ExperienceId;

        // Directly: a Contradicting token with a Contradicting submission counts, and contests the record.
        var token = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Contradicting, [target]);
        var contested = await world.Lifecycle.ApplyEvidenceAsync(
            Reviewer, world.Human(world.ReuseRun, token.Token) with { Kind = ConfidenceEvidenceKind.Contradicting }, CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, contested.Outcome);
        Assert.True(contested.Counted);
        Assert.Equal(ExperienceStatus.Contested, contested.Status);
        Assert.Equal(1, world.Store.Find(target)!.Contradictions);

        // Through feedback: Harmed maps to Contradicting, and a Contradicting token for another reviewer lands.
        var harmed = world.Issuer.Issue(OtherReviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Contradicting, [target]);
        var recorded = await world.Feedback.RecordAsync(
            OtherReviewer,
            Feedback(world.ReuseRun, [target]) with
            {
                HumanAssessment = new HumanReuseAssessment(
                    harmed.AssessmentId, ExperienceReuseBenefit.Harmed, [target], "it misled the run", Now, AssessmentToken: harmed.Token),
            },
            CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.HumanAssessment, recorded.AttributionSource);
        Assert.Equal(ExperienceReuseBenefit.Harmed, recorded.Benefit);
        var exposure = Assert.Single(recorded.Exposures);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition);
        Assert.True(exposure.Counted);
        Assert.Equal(2, world.Store.Find(target)!.Contradictions);
    }

    [Fact]
    public async Task A_replayed_token_is_refused_while_an_identical_retry_of_the_same_evidence_still_replays()
    {
        var world = new World();
        var token = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var request = world.Human(world.ReuseRun, token.Token);

        var first = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, request, CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, first.Outcome);
        Assert.True(first.Counted);

        // The same evidence again -- a lost acknowledgement retried -- is the original, reported again.
        var retry = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, request, CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, retry.Outcome);
        Assert.Equal(first.Update!.EvidenceId, retry.Update!.EvidenceId);

        // The same token under fresh evidence: refused, not recorded-and-uncounted.
        var replay = await world.Lifecycle.ApplyEvidenceAsync(
            Reviewer, request with { EvidenceId = Guid.NewGuid(), EventId = Guid.NewGuid() }, CancellationToken.None);

        AssertRefused(replay, IndependenceRefusal.AssessmentTokenReplayed);
        Assert.Equal(1, world.Store.EvidenceRows);
        Assert.Equal(1, world.Store.Commits.Count(commit => commit.Confidence is not null));
    }

    // ---- The legitimate flow -------------------------------------------------------------------------

    [Fact]
    public async Task A_legitimate_flow_through_real_finalization_counts_once_per_independence_key()
    {
        var world = new World(seedTarget: false);
        var roundA = Guid.NewGuid();
        var roundB = Guid.NewGuid();

        // Run A produces the lesson; run B reuses it. Both are captured and finalized for real.
        var runA = world.StartCapturedRun(TestScope, complete: true);
        var finalizedA = await world.FinalizeAsync(runA, roundA);
        Assert.Equal(FinalizationOutcome.Validated, finalizedA.Outcome);
        var lesson = finalizedA.Record!.ExperienceId;

        // Run B is given the lesson (as the context provider would record it), and finalization carries that
        // exposure onto run B's record, marked as finalized.
        var runB = world.StartCapturedRun(
            TestScope, complete: true, exposures: [new RunExposure(lesson, world.Store.Find(lesson)!.Revision)]);
        var finalizedB = await world.FinalizeAsync(runB, roundB);

        Assert.Equal(roundA, finalizedA.Record.ClosedRoundId);
        var storedB = world.Store.Find(finalizedB.Record!.ExperienceId)!;
        Assert.Equal(roundB, storedB.ClosedRoundId);
        Assert.Equal(ExperienceRecordOrigin.Finalized, storedB.Origin);
        Assert.Contains(storedB.Provenance.ExposedTo, exposure => exposure.ExperienceId == lesson);
        Assert.DoesNotContain(world.Store.Find(lesson)!.Provenance.ExposedTo, exposure => exposure.ExperienceId == lesson);

        // Machine: counted once for (run B, round B), recorded-not-counted for the same key again.
        var machine = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(runB, roundB, lesson), CancellationToken.None);
        var machineAgain = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(runB, roundB, lesson), CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, machine.Outcome);
        Assert.True(machine.Counted);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, machineAgain.Outcome);
        Assert.False(machineAgain.Counted);

        // Human: one reviewer's token counts once; a second token from the same reviewer about the same
        // run is the same key, recorded and not counted; another reviewer's is a new key.
        var first = world.Issuer.Issue(Reviewer, TestScope, runB, ConfidenceEvidenceKind.Supporting, [lesson]);
        var second = world.Issuer.Issue(Reviewer, TestScope, runB, ConfidenceEvidenceKind.Supporting, [lesson]);
        var otherReviewers = world.Issuer.Issue(OtherReviewer, TestScope, runB, ConfidenceEvidenceKind.Supporting, [lesson]);

        var human = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(runB, first.Token, lesson), CancellationToken.None);
        var sameKey = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(runB, second.Token, lesson), CancellationToken.None);
        var otherKey = await world.Lifecycle.ApplyEvidenceAsync(OtherReviewer, world.Human(runB, otherReviewers.Token, lesson), CancellationToken.None);

        Assert.True(human.Counted);
        Assert.Equal(first.AssessmentId, human.Update!.AssessmentId);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, sameKey.Outcome);
        Assert.False(sameKey.Counted);
        Assert.True(otherKey.Counted);

        // Initial 1, plus run B's machine round, plus two reviewers: S = 4.
        var record = world.Store.Find(lesson)!;
        Assert.Equal(4, record.SupportingValidations);
        Assert.Equal(ReuseConfidenceHeuristic.Score(4, 0), record.ReuseConfidence);
    }

    [Fact]
    public async Task Finalization_records_no_round_when_none_was_closed()
    {
        var world = new World(seedTarget: false);
        var run = world.StartCapturedRun(TestScope, complete: true);

        var finalized = await world.FinalizeAsync(run, closedRound: null);

        Assert.Equal(FinalizationOutcome.Quarantined, finalized.Outcome);
        Assert.Null(finalized.Record!.ClosedRoundId);
        Assert.Null(world.Store.Find(finalized.Record.ExperienceId)!.ClosedRoundId);
    }

    // ---- Feedback ------------------------------------------------------------------------------------

    [Fact]
    public async Task Feedback_with_a_forged_or_mismatched_assessment_records_the_exposure_and_moves_nothing()
    {
        var world = new World();
        var target = world.Target.ExperienceId;
        var genuine = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [target]);

        var cases = new (HumanReuseAssessment Assessment, Guid RunId, string Path)[]
        {
            (Assessment(Guid.NewGuid(), target, token: null), world.ReuseRun, "AssessmentToken"),
            (Assessment(Guid.NewGuid(), target, Guid.NewGuid().ToString()), world.ReuseRun, "AssessmentToken"),
            (Assessment(Guid.NewGuid(), target, genuine.Token), world.ReuseRun, "AssessmentId"),
            (Assessment(genuine.AssessmentId, target, genuine.Token) with { Benefit = ExperienceReuseBenefit.Harmed }, world.ReuseRun, "AssessmentToken"),
            (Assessment(genuine.AssessmentId, target, genuine.Token), Guid.NewGuid(), "RunId"),
        };

        foreach (var (assessment, runId, path) in cases)
        {
            var result = await world.Feedback.RecordAsync(
                Reviewer, Feedback(runId, [target]) with { HumanAssessment = assessment }, CancellationToken.None);

            Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
            Assert.Equal(ReuseAttributionSource.None, result.AttributionSource);
            Assert.Equal(ExperienceReuseBenefit.Unknown, result.Benefit);
            Assert.Contains(path, result.Reason!, StringComparison.Ordinal);
            Assert.DoesNotContain(genuine.Token, result.Reason!, StringComparison.Ordinal);
        }

        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task Feedback_attributing_a_record_to_its_own_source_run_is_degraded_before_the_ledger()
    {
        var world = new World();
        var ownRun = world.Target.SourceRunId;

        // The target is itself the record finalization made from its own run, so the run is known and its
        // round genuine: only the own-run rule stands in the way.

        var comparative = await world.Feedback.RecordAsync(
            Reviewer,
            Feedback(ownRun, [world.Target.ExperienceId]) with
            {
                ComparativeEvaluation = Comparative(ownRun, world.Target.ClosedRoundId!.Value, world.Target.ExperienceId),
            },
            CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.None, comparative.AttributionSource);
        Assert.Equal(ExperienceReuseBenefit.Unknown, comparative.Benefit);
        Assert.Contains("own source run", comparative.Reason!, StringComparison.Ordinal);
        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task Feedback_with_a_token_that_does_not_cover_every_attributed_record_is_degraded()
    {
        var world = new World();
        var second = Finalized(Guid.NewGuid(), TestScope, Guid.NewGuid());
        world.Store.Seed(second);
        world.ExposeReuseRunTo(second);
        var partial = world.Issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);

        var result = await world.Feedback.RecordAsync(
            Reviewer,
            Feedback(world.ReuseRun, [world.Target.ExperienceId, second.ExperienceId]) with
            {
                HumanAssessment = new HumanReuseAssessment(
                    partial.AssessmentId,
                    ExperienceReuseBenefit.Improved,
                    [world.Target.ExperienceId, second.ExperienceId],
                    "helped",
                    Now,
                    AssessmentToken: partial.Token),
            },
            CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.None, result.AttributionSource);
        Assert.Contains("does not cover", result.Reason!, StringComparison.Ordinal);
        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task Feedback_with_a_valid_token_counts_each_attributed_record_once_and_a_second_feedback_cannot_reuse_it()
    {
        var world = new World();
        var second = Finalized(Guid.NewGuid(), TestScope, Guid.NewGuid()) with { Status = ExperienceStatus.Validated, SupportingValidations = 1, ReuseConfidence = 2d / 3d, Revision = 1 };
        world.Store.Seed(second);
        world.ExposeReuseRunTo(second);
        var token = world.Issuer.Issue(
            Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId, second.ExperienceId]);
        var assessment = new HumanReuseAssessment(
            token.AssessmentId, ExperienceReuseBenefit.Improved, [world.Target.ExperienceId, second.ExperienceId], "helped", Now, AssessmentToken: token.Token);

        var submission = Feedback(world.ReuseRun, [world.Target.ExperienceId, second.ExperienceId]) with { HumanAssessment = assessment };
        var recorded = await world.Feedback.RecordAsync(Reviewer, submission, CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.HumanAssessment, recorded.AttributionSource);
        Assert.All(recorded.Exposures, exposure =>
        {
            Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition);
            Assert.True(exposure.Counted);
        });

        // Retrying the identical feedback converges: nothing is refused, nothing counts twice.
        var retried = await world.Feedback.RecordAsync(Reviewer, submission, CancellationToken.None);
        Assert.Equal(ExperienceReuseFeedbackOutcome.AlreadyRecorded, retried.Outcome);
        Assert.All(retried.Exposures, exposure => Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, exposure.Disposition));

        // A second feedback submission presenting the same token lands nothing: the token is spent.
        var reused = await world.Feedback.RecordAsync(
            Reviewer, submission with { FeedbackId = Guid.NewGuid() }, CancellationToken.None);

        Assert.All(reused.Exposures, exposure =>
        {
            Assert.Equal(ExperienceExposureDisposition.Refused, exposure.Disposition);
            Assert.False(exposure.Retryable);
            Assert.Contains("already landed", exposure.Reason!, StringComparison.Ordinal);
        });

        Assert.Equal(2, world.Store.Find(world.Target.ExperienceId)!.SupportingValidations);
        Assert.Equal(2, world.Store.Find(second.ExperienceId)!.SupportingValidations);
    }

    [Fact]
    public async Task A_comparative_result_naming_an_unknown_run_or_a_round_its_run_did_not_close_is_degraded()
    {
        var world = new World();

        foreach (var (runId, roundId, path) in new[]
        {
            (Guid.NewGuid(), world.ReuseRound, "RunId"),
            (world.ReuseRun, Guid.NewGuid(), "VerificationRoundId"),
        })
        {
            var result = await world.Feedback.RecordAsync(
                Reviewer,
                Feedback(runId, [world.Target.ExperienceId]) with { ComparativeEvaluation = Comparative(runId, roundId, world.Target.ExperienceId) },
                CancellationToken.None);

            Assert.Equal(ExperienceReuseFeedbackOutcome.Recorded, result.Outcome);
            Assert.Equal(ReuseAttributionSource.None, result.AttributionSource);
            Assert.Contains(path, result.Reason!, StringComparison.Ordinal);
        }

        var accepted = await world.Feedback.RecordAsync(
            Reviewer,
            Feedback(world.ReuseRun, [world.Target.ExperienceId]) with
            {
                ComparativeEvaluation = Comparative(world.ReuseRun, world.ReuseRound, world.Target.ExperienceId),
            },
            CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.ComparativeEvaluation, accepted.AttributionSource);
        Assert.True(Assert.Single(accepted.Exposures).Counted);
    }

    // ---- Exposure (story 7.3) ------------------------------------------------------------------------

    [Fact]
    public async Task A_real_run_that_was_never_exposed_to_the_record_is_refused_for_machine_and_human_evidence()
    {
        var world = new World();

        // Run C is real, finalized here with a closed round -- and was never given the target lesson.
        var unexposed = Guid.NewGuid();
        var unexposedRound = Guid.NewGuid();
        world.Store.Seed(Finalized(unexposed, TestScope, unexposedRound));

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(unexposed, unexposedRound), CancellationToken.None),
            IndependenceRefusal.NotExposed);

        var token = world.Issuer.Issue(Reviewer, TestScope, unexposed, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(unexposed, token.Token), CancellationToken.None),
            IndependenceRefusal.NotExposed);

        // A captured run the capture service holds, exposed to another record only.
        var held = world.StartCapturedRun(TestScope, exposeTarget: false, exposures: [new RunExposure(Guid.NewGuid(), 0)]);
        var heldToken = world.Issuer.Issue(Reviewer, TestScope, held, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(held, heldToken.Token), CancellationToken.None),
            IndependenceRefusal.NotExposed);

        Assert.Empty(world.Store.Commits);
        Assert.Equal(0, world.Store.EvidenceRows);

        // The refused token was not spent: once that run is exposed it lands.
        Assert.Equal(RecordExposureOutcome.Recorded, world.Capture.RecordExposure(held, [new RunExposure(world.Target.ExperienceId, world.Target.Revision)]).Outcome);
        var landed = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(held, heldToken.Token), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, landed.Outcome);
        Assert.True(landed.Counted);
    }

    [Fact]
    public async Task An_exposed_run_is_admitted_once_per_independence_key_and_a_caller_choosing_among_runs_gets_one_key_per_exposed_run()
    {
        var world = new World();

        // Five real runs in the scope; only run B (the world's reuse run) was given the lesson.
        var others = Enumerable.Range(0, 5).Select(_ => (Run: Guid.NewGuid(), Round: Guid.NewGuid())).ToList();
        foreach (var (run, round) in others)
        {
            world.Store.Seed(Finalized(run, TestScope, round));
        }

        var counted = 0;
        foreach (var (run, round) in others.Append((world.ReuseRun, world.ReuseRound)))
        {
            var result = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None);
            counted += result.Counted ? 1 : 0;
        }

        // And the exposed run a second time, under a fresh evidence ID: recorded, not counted.
        var again = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(world.ReuseRun, world.ReuseRound), CancellationToken.None);

        Assert.Equal(1, counted);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, again.Outcome);
        Assert.False(again.Counted);
        Assert.Equal(ConfidenceEvidenceAdmission.Verified, again.Update!.Admission);
        Assert.Equal(2, world.Store.Find(world.Target.ExperienceId)!.SupportingValidations);
    }

    [Fact]
    public async Task Exposure_at_a_revision_after_the_one_the_evidence_is_computed_against_is_refused_and_an_earlier_one_admits()
    {
        var world = new World();

        // A run whose exposure claims a revision five past the target's current one was given a version of the
        // record that does not exist yet: refused, fail-closed.
        var late = world.StartCapturedRun(TestScope, exposeTarget: false, exposures: [new RunExposure(world.Target.ExperienceId, world.Target.Revision + 5)]);
        var lateToken = world.Issuer.Issue(Reviewer, TestScope, late, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var refused = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(late, lateToken.Token), CancellationToken.None);
        AssertRefused(refused, IndependenceRefusal.NotExposed);
        Assert.Contains("later", refused.Reason!, StringComparison.Ordinal);

        // A run exposed at revision 0, before the current one, is still admitted after the record has moved on: the lesson is the
        // same record, and a later revision of it is the same lesson.
        var moved = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(world.ReuseRun, world.ReuseRound), CancellationToken.None);
        Assert.True(moved.Counted);
        Assert.True(world.Store.Find(world.Target.ExperienceId)!.Revision > 0);

        var early = world.StartCapturedRun(TestScope, exposeTarget: false, exposures: [new RunExposure(world.Target.ExperienceId, 0)]);
        var earlyToken = world.Issuer.Issue(Reviewer, TestScope, early, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var admitted = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(early, earlyToken.Token), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, admitted.Outcome);
        Assert.True(admitted.Counted);

        // Once the record reaches the late run's claimed revision, the late exposure is no longer ahead of it.
        world.Store.Seed(world.Store.Find(world.Target.ExperienceId)! with { Revision = world.Target.Revision + 5 });
        var caughtUp = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(late, lateToken.Token), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, caughtUp.Outcome);
    }

    [Fact]
    public async Task A_hand_written_record_under_a_runs_derived_id_vouches_for_nothing_until_marked_finalized()
    {
        var world = new World();

        // Written through CreateAsync by a host, not by finalization: same derived ID, same round, even the
        // exposure -- and the default origin, HostWritten.
        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        var handWritten = Finalized(run, TestScope, round, new RunExposure(world.Target.ExperienceId, 0)) with { Origin = ExperienceRecordOrigin.HostWritten };
        Assert.Equal(ExperienceStoreOutcome.Created, (await world.Store.CreateAsync(Reviewer, handWritten, CancellationToken.None)).Outcome);

        AssertRefused(
            await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None),
            IndependenceRefusal.HostWrittenRun);

        var token = world.Issuer.Issue(Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var feedback = await world.Feedback.RecordAsync(
            Reviewer,
            Feedback(run, [world.Target.ExperienceId]) with { HumanAssessment = Assessment(token.AssessmentId, world.Target.ExperienceId, token.Token) },
            CancellationToken.None);
        Assert.Equal(ReuseAttributionSource.None, feedback.AttributionSource);
        Assert.Contains("without finalization", feedback.Reason!, StringComparison.Ordinal);
        Assert.Empty(world.Store.Commits);

        // A record constructed with no origin at all is HostWritten: the default is the unverified one.
        Assert.Equal(ExperienceRecordOrigin.HostWritten, (handWritten with { Origin = default }).Origin);

        // The host that marks it finalized is making that statement itself -- and is then believed.
        world.Store.Seed(handWritten with { Origin = ExperienceRecordOrigin.Finalized });
        var believed = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, believed.Outcome);
    }

    [Fact]
    public async Task Feedback_attributing_a_record_the_run_was_never_exposed_to_is_degraded_before_the_ledger()
    {
        var world = new World();
        var second = Finalized(Guid.NewGuid(), TestScope, Guid.NewGuid()) with { Status = ExperienceStatus.Validated, SupportingValidations = 1, ReuseConfidence = 2d / 3d, Revision = 1 };
        world.Store.Seed(second);

        // Run B saw the target but not the second record.
        var token = world.Issuer.Issue(
            Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId, second.ExperienceId]);
        var human = await world.Feedback.RecordAsync(
            Reviewer,
            Feedback(world.ReuseRun, [world.Target.ExperienceId, second.ExperienceId]) with
            {
                HumanAssessment = new HumanReuseAssessment(
                    token.AssessmentId, ExperienceReuseBenefit.Improved, [world.Target.ExperienceId, second.ExperienceId], "helped", Now, AssessmentToken: token.Token),
            },
            CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.None, human.AttributionSource);
        Assert.Contains("exposed to", human.Reason!, StringComparison.Ordinal);

        var comparative = await world.Feedback.RecordAsync(
            Reviewer,
            Feedback(world.ReuseRun, [second.ExperienceId]) with { ComparativeEvaluation = Comparative(world.ReuseRun, world.ReuseRound, second.ExperienceId) },
            CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.None, comparative.AttributionSource);
        Assert.Contains("exposed to", comparative.Reason!, StringComparison.Ordinal);
        Assert.Empty(world.Store.Commits);
    }

    [Fact]
    public async Task Opt_out_evidence_is_flagged_host_trusted_and_a_confidence_read_can_leave_it_out()
    {
        var world = new World();
        var trusting = new ExperienceLifecycleService(
            world.Store,
            indexingService: null,
            new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });

        // One verified machine key, and three host-trusted ones about runs nobody can vouch for.
        var verified = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(world.ReuseRun, world.ReuseRound), CancellationToken.None);
        Assert.Equal(ConfidenceEvidenceAdmission.Verified, verified.Update!.Admission);
        Assert.Equal(ConfidenceEvidenceAdmission.Verified, verified.Event!.Confidence!.Admission);

        for (var i = 0; i < 3; i++)
        {
            var trusted = await trusting.ApplyEvidenceAsync(Reviewer, world.Machine(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            Assert.True(trusted.Counted);
            Assert.Equal(ConfidenceEvidenceAdmission.HostTrusted, trusted.Update!.Admission);
        }

        // And one contradiction the opt-out admitted, so both counters carry excludable evidence.
        var contradicted = await trusting.ApplyEvidenceAsync(
            Reviewer, world.Machine(Guid.NewGuid(), Guid.NewGuid()) with { Kind = ConfidenceEvidenceKind.Contradicting }, CancellationToken.None);
        Assert.Equal(ConfidenceEvidenceAdmission.HostTrusted, contradicted.Update!.Admission);

        var stored = world.Store.Find(world.Target.ExperienceId)!;
        Assert.Equal(5, stored.SupportingValidations);
        Assert.Equal(1, stored.Contradictions);

        var all = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, world.Target.ExperienceId, ConfidenceEvidenceFilter.All, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, all.Outcome);
        Assert.Equal(stored.ReuseConfidence, all.Report!.ReuseConfidence);
        Assert.Equal(new ConfidenceAdmissionCounts(1, 0), all.Report.Verified);
        Assert.Equal(new ConfidenceAdmissionCounts(3, 1), all.Report.HostTrusted);
        Assert.Equal(new ConfidenceAdmissionCounts(0, 0), all.Report.Unrecorded);

        var excluded = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, world.Target.ExperienceId, ConfidenceEvidenceFilter.ExcludeHostTrusted, CancellationToken.None);
        Assert.Equal(2, excluded.Report!.SupportingValidations);
        Assert.Equal(0, excluded.Report.Contradictions);
        Assert.Equal(ReuseConfidenceHeuristic.Score(2, 0), excluded.Report.ReuseConfidence);
        Assert.Equal(stored.ReuseConfidence, excluded.Report.StoredReuseConfidence);
        Assert.Equal(stored.Revision, excluded.Report.Revision);

        // Nothing moved: the read is a read.
        Assert.Equal(stored, world.Store.Find(world.Target.ExperienceId));
    }

    [Fact]
    public async Task Verified_only_also_leaves_out_evidence_stored_before_admission_was_recorded()
    {
        var world = new World();

        // A counted update from before admission was recorded, as a store hands it back: no admission.
        var legacy = await world.Store.CommitLifecycleEventAsync(
            Reviewer,
            TestScope,
            new LifecycleEvent(
                Guid.NewGuid(), world.Target.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Validated, "legacy", "tests", Now, world.Target.Revision, null,
                ReuseConfidenceHeuristic.Apply(world.Store.Find(world.Target.ExperienceId)!, Guid.NewGuid(), ConfidenceEvidenceKind.Supporting,
                    ConfidenceEvidenceSource.Machine, Guid.NewGuid(), Guid.NewGuid(), null, null)),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, legacy.Outcome);

        var verified = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(world.ReuseRun, world.ReuseRound), CancellationToken.None);
        Assert.True(verified.Counted);

        var keep = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, world.Target.ExperienceId, ConfidenceEvidenceFilter.ExcludeHostTrusted, CancellationToken.None);
        var strict = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, world.Target.ExperienceId, ConfidenceEvidenceFilter.VerifiedOnly, CancellationToken.None);

        Assert.Equal(new ConfidenceAdmissionCounts(1, 0), strict.Report!.Unrecorded);
        Assert.Equal(3, keep.Report!.SupportingValidations);
        Assert.Equal(2, strict.Report.SupportingValidations);
        Assert.Equal(ReuseConfidenceHeuristic.Score(2, 0), strict.Report.ReuseConfidence);

        // A record readable only through a grant has no history here to report.
        world.Store.SharedByGrant.Add(world.Target.ExperienceId);
        var shared = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, world.Target.ExperienceId, ConfidenceEvidenceFilter.All, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, shared.Outcome);
        Assert.Null(shared.Report);

        // A record its writer seeded by hand: its initial counters are its writer's statement, not verified evidence.
        var seeded = Finalized(Guid.NewGuid(), TestScope, Guid.NewGuid()) with
        {
            Origin = ExperienceRecordOrigin.HostWritten,
            Status = ExperienceStatus.Validated,
            SupportingValidations = 500,
            ReuseConfidence = ReuseConfidenceHeuristic.Score(500, 0),
        };
        world.Store.Seed(seeded);
        var seededRead = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, seeded.ExperienceId, ConfidenceEvidenceFilter.VerifiedOnly, CancellationToken.None);
        Assert.Equal(ExperienceRecordOrigin.HostWritten, seededRead.Report!.Origin);
        Assert.Equal(new ConfidenceAdmissionCounts(500, 0), seededRead.Report.Initial);
        Assert.Equal(0, seededRead.Report.SupportingValidations);
        Assert.Equal(ReuseConfidenceHeuristic.Score(0, 0), seededRead.Report.ReuseConfidence);
        Assert.Equal(500, (await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, seeded.ExperienceId, ConfidenceEvidenceFilter.ExcludeHostTrusted, CancellationToken.None)).Report!.SupportingValidations);

        var invalid = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, Guid.Empty, (ConfidenceEvidenceFilter)9, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, invalid.Outcome);
        Assert.Equal(2, invalid.Errors.Count);
    }

    [Fact]
    public async Task A_replay_of_evidence_stored_before_admission_was_recorded_reports_none_and_tags_none()
    {
        using var probe = Diagnostics.TelemetryProbe.SpansOnly();
        var world = new World();
        var trusting = new ExperienceLifecycleService(
            world.Store, indexingService: null, new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers });

        // Stored as a store holding pre-0018 evidence would hand it back: no admission.
        var request = world.Machine(Guid.NewGuid(), Guid.NewGuid());
        var legacy = ReuseConfidenceHeuristic.Apply(
            world.Store.Find(world.Target.ExperienceId)!, request.EvidenceId, request.Kind, request.Source, request.RunId, request.VerificationRoundId, null, null);
        Assert.Equal(ExperienceStoreOutcome.Committed, (await world.Store.CommitLifecycleEventAsync(
            Reviewer,
            TestScope,
            new LifecycleEvent(Guid.NewGuid(), world.Target.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Validated, "legacy", "tests", Now, world.Target.Revision, null, legacy),
            CancellationToken.None)).Outcome);

        // Resubmitted today, under the opt-out: the replay reports what was stored, and the span borrows nothing.
        var replayed = await trusting.ApplyEvidenceAsync(Reviewer, request, CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, replayed.Outcome);
        Assert.Null(replayed.Update!.Admission);
        var span = Assert.Single(
            probe.LibraryActivities,
            activity => activity.GetTagItem("agentexperience.event_id") as string == request.EventId.ToString("D"));
        Assert.Null(span.GetTagItem("agentexperience.confidence.admission"));
    }

    [Fact]
    public void Provenance_compares_its_exposures_by_value_not_by_reference()
    {
        var id = Guid.NewGuid();
        var written = new Provenance("tests", "1", Now, "c") { ExposedTo = [new RunExposure(id, 2)] };
        var readBack = new Provenance("tests", "1", Now, "c") { ExposedTo = new List<RunExposure> { new(id, 2) } };

        Assert.Equal(written, readBack);
        Assert.Equal(written.GetHashCode(), readBack.GetHashCode());
        Assert.NotEqual(written, readBack with { ExposedTo = [new RunExposure(id, 3)] });
        Assert.NotEqual(written, readBack with { ExposedTo = [] });
        Assert.NotEqual(written, readBack with { CorrelationId = "d" });
    }

    [Fact]
    public void The_capture_service_keeps_the_earliest_revision_once_per_record_and_closes_exposure_with_the_run()
    {
        var world = new World();
        var run = world.StartCapturedRun(TestScope, exposeTarget: false);
        var a = new Guid("00000000-0000-0000-0000-00000000000a");
        var b = new Guid("00000000-0000-0000-0000-00000000000b");

        Assert.Equal(RecordExposureOutcome.Recorded, world.Capture.RecordExposure(run, [new RunExposure(b, 4), new RunExposure(a, 2)]).Outcome);
        Assert.Equal(RecordExposureOutcome.DuplicateNoOp, world.Capture.RecordExposure(run, [new RunExposure(a, 3)]).Outcome);
        Assert.Equal(RecordExposureOutcome.Recorded, world.Capture.RecordExposure(run, [new RunExposure(b, 1)]).Outcome);

        Assert.True(world.Capture.TryGetRun(run, out var captured));
        Assert.Equal([new RunExposure(a, 2), new RunExposure(b, 1)], captured.Provenance.ExposedTo);

        // Invalid input throws; an unknown run is a typed outcome; so is capacity, all-or-nothing.
        Assert.Throws<ArgumentException>(() => world.Capture.RecordExposure(run, [new RunExposure(Guid.Empty, 0)]));
        Assert.Throws<ArgumentException>(() => world.Capture.RecordExposure(run, [new RunExposure(a, -1)]));
        Assert.Throws<ArgumentNullException>(() => world.Capture.RecordExposure(run, null!));
        Assert.Equal(RecordExposureOutcome.RunNotFound, world.Capture.RecordExposure(Guid.NewGuid(), [new RunExposure(a, 0)]).Outcome);

        var tooMany = Enumerable.Range(0, RunExposure.MaxPerRun).Select(_ => new RunExposure(Guid.NewGuid(), 0)).ToArray();
        Assert.Equal(RecordExposureOutcome.CapacityExceeded, world.Capture.RecordExposure(run, tooMany).Outcome);
        Assert.True(world.Capture.TryGetRun(run, out captured));
        Assert.Equal(2, captured.Provenance.ExposedTo.Count);

        // A run starts exposed to nothing: a host cannot pre-seed exposure through StartRun -- not even by handing
        // it an empty list it fills in afterwards.
        var later = new List<RunExposure>();
        var normalized = Guid.NewGuid();
        Assert.Equal(StartRunOutcome.Started, world.Capture.StartRun(
            normalized, "task-1", null, TestScope,
            new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            new Provenance("tests", null, Now, null) { ExposedTo = later },
            Now).Outcome);
        later.Add(new RunExposure(a, 0));
        Assert.True(world.Capture.TryGetRun(normalized, out var normalizedRun));
        Assert.Empty(normalizedRun.Provenance.ExposedTo);

        Assert.Throws<ArgumentException>(() => world.Capture.StartRun(
            Guid.NewGuid(), "task-1", null, TestScope,
            new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            new Provenance("tests", null, Now, null) { ExposedTo = [new RunExposure(a, 0)] },
            Now));

        // Completion closes it: finalization may already have copied the provenance.
        var completed = world.StartCapturedRun(TestScope, complete: true, exposeTarget: false);
        Assert.Equal(RecordExposureOutcome.Conflict, world.Capture.RecordExposure(completed, [new RunExposure(a, 0)]).Outcome);
    }

    [Fact]
    public void A_capture_service_written_before_exposure_existed_records_none()
    {
        IExperienceCaptureService legacy = new LegacyCaptureService();

        Assert.Equal(RecordExposureOutcome.NotSupported, legacy.RecordExposure(Guid.NewGuid(), [new RunExposure(Guid.NewGuid(), 0)]).Outcome);
    }

    // ---- The issuer and the options ------------------------------------------------------------------

    [Fact]
    public void The_issuer_refuses_what_it_should_never_sign_and_never_prints_a_token()
    {
        var issuer = new AssessmentTokenIssuer(new ExperienceIndependenceOptions { AssessmentTokenKey = Key });
        var run = Guid.NewGuid();
        var record = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer, OtherScope with { TenantId = "tenant-2" }, run, ConfidenceEvidenceKind.Supporting, [record]));
        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer with { PrincipalId = " " }, TestScope, run, ConfidenceEvidenceKind.Supporting, [record]));
        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer with { PrincipalId = "reviewer-1 " }, TestScope, run, ConfidenceEvidenceKind.Supporting, [record]));
        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer, TestScope, Guid.Empty, ConfidenceEvidenceKind.Supporting, [record]));
        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer, TestScope, run, (ConfidenceEvidenceKind)7, [record]));
        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, []));
        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, [Guid.Empty]));
        Assert.Throws<ArgumentException>(() => issuer.Issue(Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, [record, record]));
        Assert.Throws<ArgumentException>(() => issuer.Issue(
            Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, [.. Enumerable.Range(0, ExperienceReuseFeedback.MaxExposedRecords + 1).Select(_ => Guid.NewGuid())]));

        var token = issuer.Issue(Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, [record]);
        Assert.StartsWith("aexat1.", token.Token, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Token, token.ToString(), StringComparison.Ordinal);
        Assert.Contains("<redacted>", token.ToString(), StringComparison.Ordinal);

        var assessment = new HumanReuseAssessment(token.AssessmentId, ExperienceReuseBenefit.Improved, [record], "r", Now, AssessmentToken: token.Token);
        Assert.DoesNotContain(token.Token, assessment.ToString(), StringComparison.Ordinal);

        var request = new ApplyConfidenceEvidenceRequest(
            Guid.NewGuid(), record, TestScope, Guid.NewGuid(), ConfidenceEvidenceKind.Supporting, ConfidenceEvidenceSource.Human,
            run, null, "r", "p", Now, AssessmentToken: token.Token);
        Assert.DoesNotContain(token.Token, request.ToString(), StringComparison.Ordinal);

        // Two tokens for the same review are two assessments.
        Assert.NotEqual(token.AssessmentId, issuer.Issue(Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, [record]).AssessmentId);
    }

    [Fact]
    public async Task Options_that_cannot_be_verified_under_are_refused_at_construction_and_the_key_is_copied()
    {
        var store = new IndependenceStore();

        Assert.Throws<ArgumentException>(() => new AssessmentTokenIssuer(new ExperienceIndependenceOptions()));
        Assert.Throws<ArgumentException>(() => new AssessmentTokenIssuer(new ExperienceIndependenceOptions { AssessmentTokenKey = new byte[31] }));
        Assert.Throws<ArgumentException>(() => new ExperienceLifecycleService(store, null, new ExperienceIndependenceOptions { AssessmentTokenKey = new byte[16] }));
        Assert.Throws<ArgumentException>(() => new ExperienceLifecycleService(store, null, new ExperienceIndependenceOptions { AssessmentTokenLifetime = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new ExperienceLifecycleService(store, null, new ExperienceIndependenceOptions { AssessmentTokenLifetime = TimeSpan.FromDays(31) }));
        Assert.Throws<ArgumentException>(() => new ExperienceLifecycleService(store, null, new ExperienceIndependenceOptions { Verification = (IndependenceVerification)9 }));
        Assert.Throws<ArgumentException>(() => new ExperienceLifecycleService(store, null, new ExperienceIndependenceOptions { TimeProvider = null! }));
        Assert.Throws<ArgumentNullException>(() => new ExperienceLifecycleService(store, null, (ExperienceIndependenceOptions)null!));

        // Verification is the default.
        Assert.Equal(IndependenceVerification.Verified, new ExperienceLifecycleService(store).IndependenceVerification);

        // The key is copied: clearing the array afterwards does not change what the issuer signs under,
        // so its tokens still verify against a service holding the original key.
        var world = new World();
        var key = Key.ToArray();
        var issuer = new AssessmentTokenIssuer(new ExperienceIndependenceOptions { AssessmentTokenKey = key, TimeProvider = new MutableClock(Now) });
        Array.Clear(key);

        var token = issuer.Issue(Reviewer, TestScope, world.ReuseRun, ConfidenceEvidenceKind.Supporting, [world.Target.ExperienceId]);
        var landed = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Human(world.ReuseRun, token.Token), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, landed.Outcome);
    }

    [Fact]
    public async Task Core_registration_verifies_by_default_wires_the_capture_service_and_never_registers_an_issuer()
    {
        var store = new IndependenceStore();

        var services = new ServiceCollection();
        services.AddSingleton<IExperienceRecordStore>(store);
        services.AddAgentExperienceCore(Permissive, new CaptureLimits(8, 8, 1_000, 1_000));

        await using (var provider = services.BuildServiceProvider())
        {
            Assert.Equal(IndependenceVerification.Verified, provider.GetRequiredService<ExperienceLifecycleService>().IndependenceVerification);

            // Anything that can resolve an issuer can mint, so the container that agent-driven components
            // resolve from never offers one: the review flow constructs its own.
            Assert.Null(provider.GetService<AssessmentTokenIssuer>());
        }

        var keyed = new ServiceCollection();
        keyed.AddSingleton<IExperienceRecordStore>(store);
        keyed.AddAgentExperienceCore(Permissive, new CaptureLimits(8, 8, 1_000, 1_000));
        keyed.AddSingleton(new ExperienceIndependenceOptions { AssessmentTokenKey = Key });

        await using var withKey = keyed.BuildServiceProvider();
        var lifecycle = withKey.GetRequiredService<ExperienceLifecycleService>();
        Assert.Null(withKey.GetService<AssessmentTokenIssuer>());
        var issuer = new AssessmentTokenIssuer(withKey.GetRequiredService<ExperienceIndependenceOptions>());
        var capture = withKey.GetRequiredService<IExperienceCaptureService>();

        // The registered capture service's runs are known to the registered lifecycle service.
        var target = Finalized(Guid.NewGuid(), TestScope, Guid.NewGuid()) with { Status = ExperienceStatus.Validated, SupportingValidations = 1, ReuseConfidence = 2d / 3d, Revision = 1 };
        store.Seed(target);
        var run = Guid.NewGuid();
        Assert.Equal(StartRunOutcome.Started, capture.StartRun(
            run, "task-1", null, TestScope, new EnvironmentFingerprint("h", "r", "o", null, new Dictionary<string, string>()), new Provenance("t", null, Now, null), Now).Outcome);
        Assert.Equal(RecordExposureOutcome.Recorded, capture.RecordExposure(run, [new RunExposure(target.ExperienceId, target.Revision)]).Outcome);

        var token = issuer.Issue(Reviewer, TestScope, run, ConfidenceEvidenceKind.Supporting, [target.ExperienceId]);
        var applied = await lifecycle.ApplyEvidenceAsync(
            Reviewer,
            new ApplyConfidenceEvidenceRequest(
                Guid.NewGuid(), target.ExperienceId, TestScope, Guid.NewGuid(), ConfidenceEvidenceKind.Supporting,
                ConfidenceEvidenceSource.Human, run, null, "reviewed", "tests", Now, AssessmentToken: token.Token),
            CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);
        Assert.True(applied.Counted);
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    private static void AssertRefused(ApplyConfidenceEvidenceResult result, IndependenceRefusal refusal)
    {
        Assert.Equal(ConfidenceUpdateOutcome.Unverified, result.Outcome);
        Assert.Equal(refusal, result.Refusal);
        Assert.Null(result.Update);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    private static HumanReuseAssessment Assessment(Guid assessmentId, Guid experienceId, string? token) =>
        new(assessmentId, ExperienceReuseBenefit.Improved, [experienceId], "it helped", Now, AssessmentToken: token);

    private static ComparativeEvaluationResult Comparative(Guid runId, Guid roundId, Guid experienceId) => new(
        "comparator",
        runId,
        roundId,
        ExperienceReuseBenefit.Improved,
        [experienceId],
        [new Evidence(Guid.NewGuid(), roundId, "rev-1", "tests", "TestResult", CheckResult.Pass, "ci", null, Now)],
        "better with the lesson",
        Now);

    private static ExperienceReuseFeedback Feedback(Guid runId, IReadOnlyList<Guid> exposed) => new(
        FeedbackId: Guid.NewGuid(),
        RunId: runId,
        Scope: TestScope,
        ExposedExperienceIds: exposed,
        RunOutcome: TaskVerificationStatus.Verified,
        Measure: new ReuseMeasure("tool-calls", 3),
        ObservedAt: Now);

    /// <summary>A record as finalization would have left it for <paramref name="runId"/> in <paramref name="scope"/>.</summary>
    private static ExperienceRecord Finalized(Guid runId, Scope scope, Guid? closedRound, params RunExposure[] exposedTo) => new(
        ExperienceId: ExperienceFinalizationService.ExperienceIdFor(runId, scope),
        SourceRunId: runId,
        Scope: scope,
        TaskId: "task-1",
        TaskSummary: null,
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Verified, [], null, Now),
        CompletionScore: 1,
        Reflection: null,
        Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        Provenance: new Provenance("tests", null, Now, null) { ExposedTo = exposedTo },
        Status: ExperienceStatus.Quarantined,
        ReuseConfidence: 0,
        SupportingValidations: 0,
        Contradictions: 0,
        Revision: 1,
        CreatedAt: Now,
        UpdatedAt: Now)
    {
        ClosedRoundId = closedRound,
        Origin = ExperienceRecordOrigin.Finalized,
    };

    /// <summary>
    /// One scope: a validated lesson from run A (the evidence target), and run B, finalized with a closed
    /// round, that reused it. The lifecycle service verifies, with a key and the capture service.
    /// </summary>
    private sealed class World
    {
        public World(TimeProvider? clock = null, bool seedTarget = true)
        {
            var options = new ExperienceIndependenceOptions { AssessmentTokenKey = Key, TimeProvider = clock ?? new MutableClock(Now) };
            Capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Permissive), new CaptureLimits(8, 8, 1_000, 1_000));
            Lifecycle = new ExperienceLifecycleService(Store, indexingService: null, options, Capture);
            Issuer = new AssessmentTokenIssuer(options);
            Feedback = new ExperienceReuseFeedbackService(Ledger, Lifecycle);
            Finalization = new ExperienceFinalizationService(Capture, new DefaultExperienceReflector(), Store, Lifecycle);

            Target = Finalized(Guid.NewGuid(), TestScope, Guid.NewGuid()) with
            {
                Status = ExperienceStatus.Validated,
                ReuseConfidence = 2d / 3d,
                SupportingValidations = 1,
            };
            ReuseRecord = Finalized(ReuseRun, TestScope, ReuseRound, new RunExposure(Target.ExperienceId, 0));

            if (seedTarget)
            {
                Store.Seed(Target);
                Store.Seed(ReuseRecord);
            }
        }

        public IndependenceStore Store { get; } = new();

        public FeedbackLedger Ledger { get; } = new();

        public InMemoryExperienceCaptureService Capture { get; }

        public ExperienceLifecycleService Lifecycle { get; }

        public AssessmentTokenIssuer Issuer { get; }

        public ExperienceReuseFeedbackService Feedback { get; }

        public ExperienceFinalizationService Finalization { get; }

        public ExperienceRecord Target { get; }

        public Guid ReuseRun { get; } = Guid.NewGuid();

        public Guid ReuseRound { get; } = Guid.NewGuid();

        public ExperienceRecord ReuseRecord { get; private set; }

        /// <summary>Re-seeds run B's record as also having been exposed to <paramref name="others"/>, at revision 0.</summary>
        public void ExposeReuseRunTo(params ExperienceRecord[] others)
        {
            ReuseRecord = ReuseRecord with
            {
                Provenance = ReuseRecord.Provenance with
                {
                    ExposedTo = [.. ReuseRecord.Provenance.ExposedTo, .. others.Select(other => new RunExposure(other.ExperienceId, 0))],
                },
            };
            Store.Seed(ReuseRecord);
        }

        public ApplyConfidenceEvidenceRequest Machine(Guid runId, Guid roundId, Guid? experienceId = null) => new(
            EventId: Guid.NewGuid(),
            ExperienceId: experienceId ?? Target.ExperienceId,
            Scope: TestScope,
            EvidenceId: Guid.NewGuid(),
            Kind: ConfidenceEvidenceKind.Supporting,
            Source: ConfidenceEvidenceSource.Machine,
            RunId: runId,
            VerificationRoundId: roundId,
            Reason: "reused and the checks passed",
            Producer: "tests",
            OccurredAt: Now);

        public ApplyConfidenceEvidenceRequest Human(Guid runId, string? token, Guid? experienceId = null) =>
            Machine(runId, Guid.Empty, experienceId) with
            {
                Source = ConfidenceEvidenceSource.Human,
                VerificationRoundId = null,
                AssessmentToken = token,
            };

        /// <summary>
        /// Starts a captured run and, unless <paramref name="exposeTarget"/> is <see langword="false"/>, records
        /// that the library delivered the target lesson into it at its current revision -- what the MAF
        /// adapter's context provider does when it injects it.
        /// </summary>
        public Guid StartCapturedRun(Scope scope, bool complete = false, bool exposeTarget = true, IReadOnlyList<RunExposure>? exposures = null)
        {
            var runId = Guid.NewGuid();
            Assert.Equal(StartRunOutcome.Started, Capture.StartRun(
                runId,
                "task-1",
                "a task",
                scope,
                new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
                new Provenance("tests", null, Now, null),
                Now).Outcome);

            if (exposeTarget)
            {
                Assert.Equal(
                    RecordExposureOutcome.Recorded,
                    Capture.RecordExposure(runId, [new RunExposure(Target.ExperienceId, Target.Revision)]).Outcome);
            }

            if (exposures is { Count: > 0 })
            {
                Assert.Equal(RecordExposureOutcome.Recorded, Capture.RecordExposure(runId, exposures).Outcome);
            }

            if (complete)
            {
                var appended = Capture.AppendAttemptAsync(
                    runId, new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null)).GetAwaiter().GetResult();
                Assert.Equal(AppendAttemptOutcome.Recorded, appended.Outcome);
                var completed = Capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddMinutes(1)).GetAwaiter().GetResult();
                Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
            }

            return runId;
        }

        public Task<FinalizeExperienceResult> FinalizeAsync(Guid runId, Guid? closedRound) =>
            Finalization.FinalizeAsync(new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: Reviewer,
                ClosedRound: closedRound is { } round ? new ClosedVerificationRound(round, "rev-1") : null,
                RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                Evidence: closedRound is { } evidenceRound
                    ? [new Evidence(Guid.NewGuid(), evidenceRound, "rev-1", "tests", "TestResult", CheckResult.Pass, "ci", null, Now)]
                    : [],
                CurrentArtifactRevision: "rev-1",
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: Now.AddMinutes(2)));
    }

    /// <summary>A capture service that implements only the port as it stood before exposure existed.</summary>
    private sealed class LegacyCaptureService : IExperienceCaptureService
    {
        public StartRunResult StartRun(Guid runId, string taskId, string? taskDescription, Scope scope, EnvironmentFingerprint environment, Provenance provenance, DateTimeOffset startedAt) =>
            throw new NotSupportedException();

        public Task<AppendAttemptResult> AppendAttemptAsync(Guid runId, AppendAttemptRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CompleteRunResult> CompleteRunAsync(Guid runId, Guid completionEventId, RunExecutionStatus executionStatus, DateTimeOffset endedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool TryGetRun(Guid runId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExperienceRun? run)
        {
            run = null;
            return false;
        }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// A store double with the parts of the adapter's contract this story rests on: scope-exact reads (a
    /// grant read flagged as such, an erased record answering <see cref="ExperienceStoreOutcome.Deleted"/>),
    /// create-once records, evidence replayed by its ID, the independence key counted once, and an
    /// assessment spent once per record -- the last refused with the port's error path.
    /// </summary>
    private sealed class IndependenceStore : IExperienceRecordStore
    {
        private readonly Dictionary<Guid, ExperienceRecord> _records = [];
        private readonly Dictionary<Guid, (Guid ExperienceId, ConfidenceUpdate Update, long Revision, ExperienceStatus Status)> _evidence = [];
        private readonly HashSet<(Guid, string)> _counted = [];
        private readonly HashSet<(Guid, Guid)> _spent = [];

        public HashSet<Guid> SharedByGrant { get; } = [];

        public HashSet<Guid> Erased { get; } = [];

        public List<LifecycleEvent> Commits { get; } = [];

        public int EvidenceRows => _evidence.Count;

        public void Seed(ExperienceRecord record) => _records[record.ExperienceId] = record;

        public ExperienceRecord? Find(Guid experienceId) => _records.GetValueOrDefault(experienceId);

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken)
        {
            if (!authorization.Permits(record.Scope))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Denied, []));
            }

            return Task.FromResult(_records.TryAdd(record.ExperienceId, record)
                ? new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, [])
                : new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []));
        }

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken)
        {
            if (!_records.TryGetValue(experienceId, out var record))
            {
                return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));
            }

            if (Erased.Contains(experienceId))
            {
                return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Deleted, null, []));
            }

            if (SharedByGrant.Contains(experienceId))
            {
                return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [], SharedByGrant: true));
            }

            return Task.FromResult(record.Scope == scope
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [])
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));
        }

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            var id = lifecycleEvent.ExperienceRecordId;
            if (!_records.TryGetValue(id, out var record) || record.Scope != scope)
            {
                return Done(new(ExperienceStoreOutcome.NotFound, 0, null, []));
            }

            if (lifecycleEvent.Confidence is not { } update)
            {
                if (record.Revision != lifecycleEvent.ExpectedRevision)
                {
                    return Done(new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, []));
                }

                _records[id] = record with { Status = lifecycleEvent.CurrentStatus, Revision = record.Revision + 1 };
                Commits.Add(lifecycleEvent);
                return Done(new(ExperienceStoreOutcome.Committed, record.Revision + 1, null, []));
            }

            if (_evidence.TryGetValue(update.EvidenceId, out var stored))
            {
                var same = stored.ExperienceId == id
                    && stored.Update.Kind == update.Kind
                    && stored.Update.Source == update.Source
                    && stored.Update.RunId == update.RunId
                    && stored.Update.VerificationRoundId == update.VerificationRoundId
                    && stored.Update.ReviewerIdentity == update.ReviewerIdentity
                    && stored.Update.AssessmentId == update.AssessmentId;
                return Done(same
                    ? new(ExperienceStoreOutcome.Committed, stored.Revision, stored.Status, [], stored.Update)
                    : new(ExperienceStoreOutcome.Conflict, 0, null, []));
            }

            if (update.AssessmentId is { } assessment && _spent.Contains((id, assessment)))
            {
                return Done(new(
                    ExperienceStoreOutcome.Conflict,
                    0,
                    null,
                    [new StoreValidationError(ConfidenceUpdate.AssessmentIdPath, "this assessment has already landed evidence for this record under another evidence ID.")]));
            }

            if (record.Revision != lifecycleEvent.ExpectedRevision)
            {
                return Done(new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, []));
            }

            if (update.AssessmentId is { } spent)
            {
                _spent.Add((id, spent));
            }

            if (!_counted.Add((id, ReuseConfidenceHeuristic.IndependenceKeyFor(update).Value)))
            {
                var recordedOnly = update.AsRecordedOnly();
                _evidence[update.EvidenceId] = (id, recordedOnly, record.Revision, record.Status);
                return Done(new(ExperienceStoreOutcome.Committed, record.Revision, record.Status, [], recordedOnly));
            }

            var revision = record.Revision + 1;
            _records[id] = record with
            {
                Status = lifecycleEvent.CurrentStatus,
                Revision = revision,
                ReuseConfidence = update.NewReuseConfidence,
                SupportingValidations = update.NewSupportingValidations,
                Contradictions = update.NewContradictions,
            };
            _evidence[update.EvidenceId] = (id, update, revision, lifecycleEvent.CurrentStatus);
            Commits.Add(lifecycleEvent);
            _history.Add(new StoredLifecycleEvent(lifecycleEvent, Now, revision));
            return Done(new(ExperienceStoreOutcome.Committed, revision, null, [], update));
        }

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        /// <summary>The events that moved a record, each with the revision it produced, as a store's history holds them.</summary>
        private readonly List<StoredLifecycleEvent> _history = [];

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken)
        {
            if (!_records.TryGetValue(query.ExperienceId, out var record) || record.Scope != query.Scope)
            {
                return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.NotFound, 0, [], []));
            }

            // A page of two, so a history longer than that is walked through its cursor.
            var events = _history
                .Where(stored => stored.Event.ExperienceRecordId == query.ExperienceId && stored.AppliedRevision > (query.StartAfterRevision ?? -1))
                .OrderBy(stored => stored.AppliedRevision)
                .ToList();
            var page = events.Take(2).ToList();
            return Task.FromResult(new ExperienceRecordHistoryResult(
                ExperienceStoreOutcome.Found,
                record.Revision,
                page,
                [],
                events.Count > page.Count ? page[^1].AppliedRevision : null));
        }

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static Task<ExperienceLifecycleCommitResult> Done(ExperienceLifecycleCommitResult result) => Task.FromResult(result);
    }

    private sealed class FeedbackLedger : IExperienceReuseFeedbackStore
    {
        private readonly Dictionary<Guid, RecordedExperienceReuseFeedback> _rows = [];

        public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
            AuthorizationContext authorization,
            RecordedExperienceReuseFeedback feedback,
            CancellationToken cancellationToken)
        {
            if (_rows.TryGetValue(feedback.FeedbackId, out var stored))
            {
                var same = stored with { Exposures = feedback.Exposures, EvidenceIds = feedback.EvidenceIds } == feedback
                    && stored.Exposures.SequenceEqual(feedback.Exposures)
                    && stored.EvidenceIds.SequenceEqual(feedback.EvidenceIds);
                return Task.FromResult(new ExperienceReuseFeedbackStoreResult(
                    same ? ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded : ExperienceReuseFeedbackStoreOutcome.Conflict,
                    same ? stored : null,
                    []));
            }

            _rows[feedback.FeedbackId] = feedback;
            return Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Recorded, feedback, []));
        }
    }
}
