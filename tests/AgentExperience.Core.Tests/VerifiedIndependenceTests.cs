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
        var runB = world.StartCapturedRun(TestScope, complete: true);
        var finalizedB = await world.FinalizeAsync(runB, roundB);

        Assert.Equal(FinalizationOutcome.Validated, finalizedA.Outcome);
        Assert.Equal(roundA, finalizedA.Record!.ClosedRoundId);
        Assert.Equal(roundB, world.Store.Find(finalizedB.Record!.ExperienceId)!.ClosedRoundId);

        var lesson = finalizedA.Record.ExperienceId;

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
    private static ExperienceRecord Finalized(Guid runId, Scope scope, Guid? closedRound) => new(
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
        Provenance: new Provenance("tests", null, Now, null),
        Status: ExperienceStatus.Quarantined,
        ReuseConfidence: 0,
        SupportingValidations: 0,
        Contradictions: 0,
        Revision: 1,
        CreatedAt: Now,
        UpdatedAt: Now)
    {
        ClosedRoundId = closedRound,
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
            ReuseRecord = Finalized(ReuseRun, TestScope, ReuseRound);

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

        public ExperienceRecord ReuseRecord { get; }

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

        public Guid StartCapturedRun(Scope scope, bool complete = false)
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
            return Done(new(ExperienceStoreOutcome.Committed, revision, null, [], update));
        }

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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
