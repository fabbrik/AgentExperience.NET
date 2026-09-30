using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Feedback;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Story 13.1 (KL-11, part 2): with a host key ring configured, finalization signs a record's finalization claims,
/// and confidence verification believes a record's claims about its run only when that signature verifies. A
/// record written or edited outside finalization -- unsigned, under an unknown key, or with any signed claim
/// changed -- is refused as host-written. With no key ring, nothing changes.
/// </summary>
public class SignedProvenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Scope TestScope = new("tenant-1", "app-1", "project-1");
    private static readonly AuthorizationContext Reviewer = new("tenant-1", "reviewer-1", ["experience:write"], Now);
    private static readonly byte[] KeyOne = [.. Enumerable.Range(0, 32).Select(value => (byte)(value * 3 + 1))];
    private static readonly byte[] KeyTwo = [.. Enumerable.Range(0, 48).Select(value => (byte)(200 - value))];
    private static readonly ExperienceProvenanceSigningOptions Signing = Ring(("key-1", KeyOne));

    private static readonly SanitizationOptions Permissive = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
        ["ToolResult"] = new(new HashSet<string>(StringComparer.Ordinal) { "value" }, new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
    });

    // ---- Signing ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Finalization_signs_every_record_it_creates_in_the_same_create_and_signs_nothing_without_a_key_ring()
    {
        var world = new World(Signing);
        var (runId, _) = await world.FinalizeLessonAndReuseAsync();

        var created = Assert.Single(world.Store.Created, record => record.SourceRunId == runId);
        var signature = Assert.IsType<ExperienceProvenanceSignature>(created.ProvenanceSignature);
        Assert.Equal("key-1", signature.KeyId);
        Assert.Equal(ExperienceProvenanceSignature.HmacSha256, signature.Algorithm);
        Assert.Equal(32, signature.Value.Length);
        Assert.Equal(ExperienceRecordOrigin.Finalized, created.Origin);

        var unsigned = new World(signing: null);
        var (plainRun, _) = await unsigned.FinalizeLessonAndReuseAsync();
        Assert.Null(Assert.Single(unsigned.Store.Created, record => record.SourceRunId == plainRun).ProvenanceSignature);
    }

    [Fact]
    public async Task The_signature_is_an_HMAC_SHA256_over_the_documented_canonical_encoding_of_the_claims()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();

        // An independent implementation of the documented encoding: if the format drifts, every stored signature
        // stops verifying, so it is pinned here rather than inferred from the code under test.
        Assert.NotEmpty(world.Store.Created);
        foreach (var record in world.Store.Created)
        {
            Assert.Equal(HMACSHA256.HashData(KeyOne, Canonical(record)), record.ProvenanceSignature!.Value.ToArray());
        }

        // Deterministic: the same claims in another exposure order encode identically.
        var reuse = world.Store.Created.Single(record => record.Provenance.ExposedTo.Count > 0);
        var reordered = reuse with
        {
            Provenance = reuse.Provenance with
            {
                ExposedTo = [new RunExposure(Guid.NewGuid(), 4), .. reuse.Provenance.ExposedTo],
            },
        };
        Assert.Equal(Canonical(reordered), Canonical(reordered with { Provenance = reordered.Provenance with { ExposedTo = [.. reordered.Provenance.ExposedTo.Reverse()] } }));

        // And the encoding starts with its version tag.
        Assert.Equal(Encoding.UTF8.GetBytes("aexp-prov:v1"), Canonical(reuse).AsSpan(5, 12).ToArray());
    }

    [Fact]
    public async Task Exposures_are_signed_in_record_id_order_whatever_order_the_record_lists_them_in()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        var others = Enumerable.Range(0, 6).Select(i => new RunExposure(Guid.NewGuid(), i)).ToList();
        var record = world.RunRecord(run, round);
        record = record with { Provenance = record.Provenance with { ExposedTo = [.. others, .. record.Provenance.ExposedTo] } };
        var signature = Sign(record, "key-1", KeyOne);

        world.Store.Seed(record with
        {
            Provenance = record.Provenance with { ExposedTo = [.. record.Provenance.ExposedTo.Reverse()] },
            ProvenanceSignature = signature,
        });

        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_genuine_signed_run_vouches_for_evidence()
    {
        var world = new World(Signing);
        var (reuseRun, reuseRound) = await world.FinalizeLessonAndReuseAsync();

        var machine = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(reuseRun, reuseRound), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, machine.Outcome);
        Assert.Equal(ConfidenceEvidenceAdmission.Verified, machine.Update!.Admission);
    }

    // ---- Verification refuses what finalization did not sign --------------------------------------------

    [Fact]
    public async Task A_forged_CreateAsync_record_claiming_Finalized_is_refused_when_signing_is_on_and_believed_when_it_is_off()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        var forged = world.RunRecord(run, round);
        Assert.Equal(ExperienceStoreOutcome.Created, (await world.Store.CreateAsync(Reviewer, forged, CancellationToken.None)).Outcome);

        var refused = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None);
        AssertHostWritten(refused);

        // The same store, the same forged record, and no key ring: the pre-13.1 behaviour, unchanged.
        var unsigned = new ExperienceLifecycleService(world.Store, indexingService: null, new ExperienceIndependenceOptions(), world.Capture);
        var believed = await unsigned.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, believed.Outcome);
    }

    [Fact]
    public async Task A_signature_under_a_key_not_in_the_ring_is_refused()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        var record = world.RunRecord(run, round);
        world.Store.Seed(record with { ProvenanceSignature = Sign(record, "someone-else", KeyTwo) });

        AssertHostWritten(await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None));
    }

    public static TheoryData<string> Claims() =>
    [
        "ExperienceId", "TenantId", "ApplicationId", "ProjectId", "TeamId", "TeamId-empty-for-null", "AgentId", "UserId",
        "SourceRunId", "ClosedRoundId", "ClosedRoundId-removed", "Origin", "ExposedTo-id", "ExposedTo-revision",
        "ExposedTo-added", "ExposedTo-removed",
    ];

    [Theory]
    [MemberData(nameof(Claims))]
    public async Task Changing_any_signed_claim_after_signing_makes_the_run_host_written(string claim)
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        var stored = world.RunRecord(run, round);

        // The record as stored is genuine in every claim the lookup reads; the signature is over the claims with
        // exactly one of them different -- which is what a record edited after signing is.
        var signedClaims = claim switch
        {
            "ExperienceId" => stored with { ExperienceId = Guid.NewGuid() },
            "TenantId" => stored with { Scope = stored.Scope with { TenantId = "tenant-2" } },
            "ApplicationId" => stored with { Scope = stored.Scope with { ApplicationId = "app-2" } },
            "ProjectId" => stored with { Scope = stored.Scope with { ProjectId = "project-2" } },
            "TeamId" => stored with { Scope = stored.Scope with { TeamId = "team-1" } },
            "TeamId-empty-for-null" => stored with { Scope = stored.Scope with { TeamId = "" } },
            "AgentId" => stored with { Scope = stored.Scope with { AgentId = "agent-1" } },
            "UserId" => stored with { Scope = stored.Scope with { UserId = "user-1" } },
            "SourceRunId" => stored with { SourceRunId = Guid.NewGuid() },
            "ClosedRoundId" => stored with { ClosedRoundId = Guid.NewGuid() },
            "ClosedRoundId-removed" => stored with { ClosedRoundId = null },
            "Origin" => stored with { Origin = ExperienceRecordOrigin.HostWritten },
            "ExposedTo-id" => stored with { Provenance = stored.Provenance with { ExposedTo = [new RunExposure(Guid.NewGuid(), 0)] } },
            "ExposedTo-revision" => stored with { Provenance = stored.Provenance with { ExposedTo = [stored.Provenance.ExposedTo[0] with { Revision = 7 }] } },
            "ExposedTo-added" => stored with { Provenance = stored.Provenance with { ExposedTo = [.. stored.Provenance.ExposedTo, new RunExposure(Guid.NewGuid(), 0)] } },
            "ExposedTo-removed" => stored with { Provenance = stored.Provenance with { ExposedTo = [] } },
            _ => throw new ArgumentOutOfRangeException(nameof(claim)),
        };
        Assert.NotEqual(Canonical(stored), Canonical(signedClaims));

        world.Store.Seed(stored with { ProvenanceSignature = Sign(signedClaims, "key-1", KeyOne) });
        AssertHostWritten(await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None));

        // The control: the same record signed over its own claims vouches.
        world.Store.Seed(stored with { ProvenanceSignature = Sign(stored, "key-1", KeyOne) });
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_tampered_value_or_algorithm_is_refused_and_content_fields_are_not_claims()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();

        // A run the capture service no longer holds, so its record is the only way it is known.
        var (reuseRun, reuseRound) = (Guid.NewGuid(), Guid.NewGuid());
        var unsigned = world.RunRecord(reuseRun, reuseRound);
        var signature = Sign(unsigned, "key-1", KeyOne);
        var genuine = unsigned with { ProvenanceSignature = signature };

        // Content moves through the lifecycle and is not signed: the record still vouches.
        world.Store.Seed(genuine with { TaskSummary = "rewritten", Status = ExperienceStatus.Validated, ReuseConfidence = 0.9, SupportingValidations = 8, Revision = 9, CreatedAt = Now.AddYears(-1) });
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(reuseRun, reuseRound), CancellationToken.None)).Outcome);

        var flipped = signature.Value.ToArray();
        flipped[0] ^= 1;
        foreach (var tampered in new[]
        {
            signature with { Value = flipped },
            signature with { Value = signature.Value[..16] },
            signature with { Value = ReadOnlyMemory<byte>.Empty },
            signature with { Algorithm = "HMAC-SHA512" },
        })
        {
            world.Store.Seed(genuine with { ProvenanceSignature = tampered });
            AssertHostWritten(await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(reuseRun, reuseRound), CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_run_the_capture_service_still_holds_stays_known_through_capture_when_its_record_is_refused()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var held = world.StartCapturedRun();
        var forged = world.RunRecord(held, Guid.NewGuid());
        world.Store.Seed(forged);

        // Known through the capture service, as for any host-written record -- but not finalized, so no round vouches.
        var result = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(held, forged.ClosedRoundId!.Value), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Unverified, result.Outcome);
        Assert.Equal(IndependenceRefusal.UnknownRound, result.Refusal);
    }

    [Fact]
    public async Task Feedback_attributing_through_a_record_with_a_bad_signature_is_degraded_naming_the_signature()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        world.Store.Seed(world.RunRecord(run, round));

        var feedback = await world.Feedback.RecordAsync(
            Reviewer,
            new ExperienceReuseFeedback(
                FeedbackId: Guid.NewGuid(),
                RunId: run,
                Scope: TestScope,
                ExposedExperienceIds: [world.Lesson.ExperienceId],
                RunOutcome: TaskVerificationStatus.Verified,
                Measure: new ReuseMeasure("tool-calls", 3),
                ObservedAt: Now)
            {
                ComparativeEvaluation = new ComparativeEvaluationResult(
                    "comparator", run, round, ExperienceReuseBenefit.Improved, [world.Lesson.ExperienceId],
                    [new Evidence(Guid.NewGuid(), round, "rev-1", "tests", "TestResult", CheckResult.Pass, "ci", null, Now)],
                    "better with the lesson", Now),
            },
            CancellationToken.None);

        Assert.Equal(ReuseAttributionSource.None, feedback.AttributionSource);
        Assert.Contains(GenericRefusal, feedback.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(KeyOne), feedback.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verified_only_reads_leave_out_the_initial_counters_of_a_record_whose_signature_does_not_vouch()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var forged = world.RunRecord(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Status = ExperienceStatus.Validated,
            SupportingValidations = 500,
            ReuseConfidence = ReuseConfidenceHeuristic.Score(500, 0),
        };
        world.Store.Seed(forged);

        var read = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, forged.ExperienceId, ConfidenceEvidenceFilter.VerifiedOnly, CancellationToken.None);
        Assert.Equal(0, read.Report!.SupportingValidations);

        // The lesson finalization signed keeps its initial validation.
        var lesson = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, world.Lesson.ExperienceId, ConfidenceEvidenceFilter.VerifiedOnly, CancellationToken.None);
        Assert.Equal(1, lesson.Report!.SupportingValidations);
    }

    // ---- Cutover and rotation ----------------------------------------------------------------------------

    [Fact]
    public async Task The_cutover_trusts_only_listed_unsigned_records_whatever_a_record_says_about_its_age()
    {
        // The legacy record existed before signing was switched on; the host listed it then.
        var seed = new World(Signing);
        await seed.FinalizeLessonAndReuseAsync();
        var (legacyRun, legacyRound) = (Guid.NewGuid(), Guid.NewGuid());
        var legacy = seed.RunRecord(legacyRun, legacyRound);

        var world = new World(
            new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne }, "key-1")
            {
                TrustUnsignedRecordIds = new HashSet<Guid> { legacy.ExperienceId },
            },
            seed.Store,
            seed.Capture);
        world.UseLesson(seed.Lesson);
        world.Store.Seed(legacy);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(legacyRun, legacyRound), CancellationToken.None)).Outcome);

        // A forged record created through CreateAsync and backdated by years is not listed, so it is not believed.
        var (forgedRun, forgedRound) = (Guid.NewGuid(), Guid.NewGuid());
        var backdated = world.RunRecord(forgedRun, forgedRound) with { CreatedAt = Now.AddYears(-10), UpdatedAt = Now.AddYears(-10) };
        Assert.Equal(ExperienceStoreOutcome.Created, (await world.Store.CreateAsync(Reviewer, backdated, CancellationToken.None)).Outcome);
        AssertHostWritten(await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(forgedRun, forgedRound), CancellationToken.None));

        // A record finalization signed after the switch, with its signature stripped: its ID was never listed.
        var (signedRun, signedRound) = await world.FinalizeReuseOfAsync(world.Lesson);
        var signedId = ExperienceFinalizationService.ExperienceIdFor(signedRun, TestScope);
        world.Store.Seed(world.Store.Find(signedId)! with { ProvenanceSignature = null });
        var stripped = new ExperienceLifecycleService(
            world.Store, indexingService: null, new ExperienceIndependenceOptions(), captureService: null, deindexingTimeout: null, confidenceEngine: null,
            new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne }, "key-1")
            {
                TrustUnsignedRecordIds = new HashSet<Guid> { legacy.ExperienceId },
            });
        AssertHostWritten(await stripped.ApplyEvidenceAsync(Reviewer, world.Machine(signedRun, signedRound), CancellationToken.None));

        // A listed record whose signature is present and invalid is refused: the set covers unsigned records only.
        world.Store.Seed(legacy with { ProvenanceSignature = Sign(legacy with { ClosedRoundId = Guid.NewGuid() }, "key-1", KeyOne) });
        AssertHostWritten(await world.Lifecycle.ApplyEvidenceAsync(
            Reviewer, world.Machine(legacyRun, legacyRound) with { EvidenceId = Guid.NewGuid(), EventId = Guid.NewGuid() }, CancellationToken.None));
    }

    [Fact]
    public async Task The_cutover_set_is_copied_and_the_helper_lists_exactly_the_unsigned_finalized_records_of_each_scope()
    {
        var world = new World(signing: null);
        await world.FinalizeLessonAndReuseAsync();   // two unsigned finalized records, written before signing
        var handWritten = world.RunRecord(Guid.NewGuid(), Guid.NewGuid()) with { Origin = ExperienceRecordOrigin.HostWritten };
        var signed = world.RunRecord(Guid.NewGuid(), Guid.NewGuid());
        var elsewhere = world.RunRecord(Guid.NewGuid(), Guid.NewGuid()) with { Scope = TestScope with { ProjectId = "project-2" } };
        world.Store.Seed(handWritten);
        world.Store.Seed(signed with { ProvenanceSignature = Sign(signed, "key-1", KeyOne) });
        world.Store.Seed(elsewhere);

        var inventory = await ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync(world.Store, Reviewer, [TestScope]);

        Assert.Equal(world.Store.Created.Select(record => record.ExperienceId).Order(), inventory.RecordIds.Order());
        Assert.Empty(inventory.IncompleteScopes);

        var ids = new HashSet<Guid>(inventory.RecordIds);
        var options = new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne }, "key-1") { TrustUnsignedRecordIds = ids };
        ids.Add(handWritten.ExperienceId);
        Assert.Equal(2, options.TrustUnsignedRecordIds.Count);
        Assert.False(options.TrustUnsignedRecordIds.Contains(handWritten.ExperienceId));
        Assert.Throws<ArgumentNullException>(() => new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne }, "key-1") { TrustUnsignedRecordIds = null! });

        // A store that refuses a query stops the inventory rather than returning a partial set silently.
        world.Store.RefuseQueries = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync(world.Store, Reviewer, [TestScope]));
    }

    [Fact]
    public async Task Rotation_keeps_older_signatures_verifiable_while_their_key_stays_in_the_ring()
    {
        var world = new World(Signing);
        var (oldRun, oldRound) = await world.FinalizeLessonAndReuseAsync();

        // Add key-2 and sign new records under it; key-1 stays for verification.
        var rotated = new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne, ["key-2"] = KeyTwo }, "key-2");
        var next = new World(rotated, world.Store, world.Capture);
        var (newRun, newRound) = await next.FinalizeReuseOfAsync(world.Lesson);
        Assert.Equal("key-2", next.Store.Find(ExperienceFinalizationService.ExperienceIdFor(newRun, TestScope))!.ProvenanceSignature!.KeyId);

        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await next.Lifecycle.ApplyEvidenceAsync(Reviewer, next.Machine(oldRun, oldRound, world.Lesson.ExperienceId), CancellationToken.None)).Outcome);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await next.Lifecycle.ApplyEvidenceAsync(Reviewer, next.Machine(newRun, newRound, world.Lesson.ExperienceId), CancellationToken.None)).Outcome);

        // Remove key-1: what it signed reads as signed by an unknown key.
        var retired = new ExperienceLifecycleService(
            world.Store, indexingService: null, new ExperienceIndependenceOptions(), captureService: null, deindexingTimeout: null, confidenceEngine: null,
            Ring(("key-2", KeyTwo)));
        var another = world.Machine(oldRun, oldRound, world.Lesson.ExperienceId) with { EvidenceId = Guid.NewGuid(), EventId = Guid.NewGuid() };
        AssertHostWritten(await retired.ApplyEvidenceAsync(Reviewer, another, CancellationToken.None));
    }

    // ---- The opt-out ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_opt_out_still_refuses_a_present_but_invalid_signature_and_nothing_else()
    {
        var world = new World(Signing);
        var (reuseRun, _) = await world.FinalizeLessonAndReuseAsync();
        var trusting = new ExperienceLifecycleService(
            world.Store,
            indexingService: null,
            new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers },
            world.Capture,
            deindexingTimeout: null,
            confidenceEngine: null,
            Signing);

        // An invented run, a genuine signed run, an unsigned record and one under an unknown key: all trusted.
        var unsigned = world.RunRecord(Guid.NewGuid(), Guid.NewGuid());
        var unknownKey = world.RunRecord(Guid.NewGuid(), Guid.NewGuid());
        world.Store.Seed(unsigned);
        world.Store.Seed(unknownKey with { ProvenanceSignature = Sign(unknownKey, "retired", KeyTwo) });
        foreach (var run in new[] { Guid.NewGuid(), reuseRun, unsigned.SourceRunId, unknownKey.SourceRunId })
        {
            var trusted = await trusting.ApplyEvidenceAsync(Reviewer, world.Machine(run, Guid.NewGuid()), CancellationToken.None);
            Assert.Equal(ConfidenceUpdateOutcome.Applied, trusted.Outcome);
            Assert.Equal(ConfidenceEvidenceAdmission.HostTrusted, trusted.Update!.Admission);
        }

        // A record the library signed and someone then changed is refused even here.
        var tampered = world.RunRecord(Guid.NewGuid(), Guid.NewGuid());
        world.Store.Seed(tampered with { ProvenanceSignature = Sign(tampered with { ClosedRoundId = Guid.NewGuid() }, "key-1", KeyOne) });
        AssertHostWritten(await trusting.ApplyEvidenceAsync(Reviewer, world.Machine(tampered.SourceRunId, Guid.NewGuid()), CancellationToken.None));
    }

    // ---- Options -------------------------------------------------------------------------------------------

    [Fact]
    public void Signing_options_are_validated_at_construction_and_never_name_key_material()
    {
        var bad = new Func<ExperienceProvenanceSigningOptions>[]
        {
            () => new(new Dictionary<string, byte[]>(), "key-1"),
            () => new(new Dictionary<string, byte[]> { ["key-1"] = new byte[31] }, "key-1"),
            () => new(new Dictionary<string, byte[]> { ["key-1"] = null! }, "key-1"),
            () => new(new Dictionary<string, byte[]> { ["key 1"] = KeyOne }, "key 1"),
            () => new(new Dictionary<string, byte[]> { [""] = KeyOne }, ""),
            () => new(new Dictionary<string, byte[]> { [new string('k', 65)] = KeyOne }, new string('k', 65)),
            () => new(new Dictionary<string, byte[]> { ["key-1"] = KeyOne }, "key-2"),
        };

        foreach (var create in bad)
        {
            var thrown = Assert.ThrowsAny<ArgumentException>(create);
            Assert.DoesNotContain(Convert.ToBase64String(KeyOne), thrown.Message, StringComparison.Ordinal);
        }

        Assert.Throws<ArgumentNullException>(() => new ExperienceProvenanceSigningOptions(null!, "key-1"));
        Assert.Throws<ArgumentNullException>(() => new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne }, null!));

        var valid = new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["A.z_0-9"] = KeyOne, [new string('k', 64)] = KeyTwo }, "A.z_0-9");
        Assert.Equal("A.z_0-9", valid.CurrentKeyId);
        Assert.Equal(["A.z_0-9", new string('k', 64)], valid.KeyIds);
        Assert.Empty(valid.TrustUnsignedRecordIds);
    }

    [Fact]
    public async Task Keys_are_copied_so_changing_the_callers_array_afterwards_changes_nothing()
    {
        var key = KeyOne.ToArray();
        var options = new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = key }, "key-1");
        Array.Clear(key);

        var world = new World(options);
        await world.FinalizeLessonAndReuseAsync();

        Assert.All(world.Store.Created, record => Assert.Equal(HMACSHA256.HashData(KeyOne, Canonical(record)), record.ProvenanceSignature!.Value.ToArray()));
    }

    [Fact]
    public void A_signature_prints_no_bytes_and_compares_by_value()
    {
        var value = HMACSHA256.HashData(KeyOne, new byte[] { 1, 2, 3 });
        var signature = new ExperienceProvenanceSignature("key-1", ExperienceProvenanceSignature.HmacSha256, value);

        var printed = signature.ToString();
        Assert.Contains("key-1", printed, StringComparison.Ordinal);
        Assert.Contains("32 bytes", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(value), printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(value), printed, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(signature, new ExperienceProvenanceSignature("key-1", ExperienceProvenanceSignature.HmacSha256, value.ToArray()));
        Assert.Equal(signature.GetHashCode(), new ExperienceProvenanceSignature("key-1", ExperienceProvenanceSignature.HmacSha256, value.ToArray()).GetHashCode());
        Assert.NotEqual(signature, signature with { Value = new byte[32] });
        Assert.NotEqual(signature, signature with { KeyId = "key-2" });
    }

    [Fact]
    public void The_signature_is_compared_in_constant_time()
    {
        var source = File.ReadAllText(SourceFile("src", "AgentExperience.Core", "Confidence", "ProvenanceSigning.cs"));

        Assert.Contains("CryptographicOperations.FixedTimeEquals(expected, signature.Value.Span)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SequenceEqual", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_signing_options_make_finalization_sign_and_verification_check()
    {
        var services = new ServiceCollection();
        var store = new SigningStore();
        services.AddSingleton<IExperienceRecordStore>(store);
        services.AddAgentExperienceCore(Permissive, new CaptureLimits(8, 8, 1_000, 1_000));
        services.AddSingleton(Signing);
        using var provider = services.BuildServiceProvider();

        var world = new World(
            provider.GetRequiredService<ExperienceLifecycleService>(),
            provider.GetRequiredService<ExperienceFinalizationService>(),
            store,
            (InMemoryExperienceCaptureService)provider.GetRequiredService<IExperienceCaptureService>());
        var (reuseRun, reuseRound) = await world.FinalizeLessonAndReuseAsync();

        Assert.All(store.Created, record => Assert.Equal("key-1", record.ProvenanceSignature!.KeyId));
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(reuseRun, reuseRound), CancellationToken.None)).Outcome);

        var forged = world.RunRecord(Guid.NewGuid(), Guid.NewGuid());
        store.Seed(forged);
        AssertHostWritten(await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(forged.SourceRunId, forged.ClosedRoundId!.Value), CancellationToken.None));
    }

    // ---- Review pass 1 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_record_ids_in_the_exposures_are_signed_by_id_then_revision_ascending()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        var repeated = Guid.NewGuid();
        var record = world.RunRecord(run, round);
        record = record with
        {
            Provenance = record.Provenance with
            {
                ExposedTo = [new RunExposure(repeated, 9), .. record.Provenance.ExposedTo, new RunExposure(repeated, 2), new RunExposure(repeated, 5)],
            },
        };

        // Signed by the independent encoder (id bytes, then revision ascending); stored in yet another order.
        var signature = Sign(record, "key-1", KeyOne);
        world.Store.Seed(record with
        {
            Provenance = record.Provenance with { ExposedTo = [.. record.Provenance.ExposedTo.Reverse()] },
            ProvenanceSignature = signature,
        });

        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_claim_with_no_utf8_encoding_is_never_signed_and_finalization_fails_without_storing()
    {
        var world = new World(Signing);
        var result = await world.FinalizeRunInAsync(TestScope with { TeamId = "team-\uD800" });

        Assert.Equal(FinalizationOutcome.Failed, result.Outcome);
        Assert.Equal(FinalizationStage.CreateRecord, result.Stage);
        Assert.Empty(world.Store.Created);
    }

    [Fact]
    public async Task The_three_ways_a_signature_fails_are_reported_with_one_identical_reason()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var reasons = new List<string>();
        foreach (var variant in new[] { "missing", "unknown-key", "invalid" })
        {
            var (run, round) = (Guid.NewGuid(), Guid.NewGuid());
            var record = world.RunRecord(run, round);
            world.Store.Seed(variant switch
            {
                "missing" => record,
                "unknown-key" => record with { ProvenanceSignature = Sign(record, "key-9", KeyTwo) },
                _ => record with { ProvenanceSignature = Sign(record with { ClosedRoundId = Guid.NewGuid() }, "key-1", KeyOne) },
            });

            var result = await world.Lifecycle.ApplyEvidenceAsync(Reviewer, world.Machine(run, round), CancellationToken.None);
            AssertHostWritten(result);
            reasons.Add(result.Reason!);
        }

        Assert.Single(reasons.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Verified_only_counts_at_most_finalizations_own_initial_validation_of_a_signed_or_listed_record()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();

        // Signed, valid -- but the counters are not claims: 40 stored supporting validations, one of them finalization's.
        var inflated = world.RunRecord(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Status = ExperienceStatus.Validated,
            SupportingValidations = 40,
            Contradictions = 3,
            ReuseConfidence = ReuseConfidenceHeuristic.Score(40, 3),
        };
        world.Store.Seed(inflated with { ProvenanceSignature = Sign(inflated, "key-1", KeyOne) });
        var read = await world.Lifecycle.ReadConfidenceAsync(Reviewer, TestScope, inflated.ExperienceId, ConfidenceEvidenceFilter.VerifiedOnly, CancellationToken.None);
        Assert.Equal(1, read.Report!.SupportingValidations);
        Assert.Equal(0, read.Report.Contradictions);
        Assert.Equal(new ConfidenceAdmissionCounts(40, 3), read.Report.Initial);

        // A listed legacy unsigned record keeps its (capped) initial validation.
        var legacy = world.RunRecord(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Status = ExperienceStatus.Validated,
            SupportingValidations = 1,
            ReuseConfidence = ReuseConfidenceHeuristic.Score(1, 0),
        };
        world.Store.Seed(legacy);
        var listing = new ExperienceLifecycleService(
            world.Store, indexingService: null, new ExperienceIndependenceOptions(), captureService: null, deindexingTimeout: null, confidenceEngine: null,
            new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne }, "key-1")
            {
                TrustUnsignedRecordIds = new HashSet<Guid> { legacy.ExperienceId },
            });
        var legacyRead = await listing.ReadConfidenceAsync(Reviewer, TestScope, legacy.ExperienceId, ConfidenceEvidenceFilter.VerifiedOnly, CancellationToken.None);
        Assert.Equal(1, legacyRead.Report!.SupportingValidations);

        // With no key ring the read is what it always was: a finalized record's initial counters all count.
        var unsigned = new ExperienceLifecycleService(world.Store, indexingService: null, new ExperienceIndependenceOptions(), captureService: null);
        var plain = await unsigned.ReadConfidenceAsync(Reviewer, TestScope, inflated.ExperienceId, ConfidenceEvidenceFilter.VerifiedOnly, CancellationToken.None);
        Assert.Equal(40, plain.Report!.SupportingValidations);
    }

    [Fact]
    public async Task The_opt_out_checks_only_a_record_the_verified_path_would_read_and_so_does_feedback()
    {
        var world = new World(Signing);
        await world.FinalizeLessonAndReuseAsync();
        var trusting = new ExperienceLifecycleService(
            world.Store,
            indexingService: null,
            new ExperienceIndependenceOptions { Verification = IndependenceVerification.TrustHostSuppliedIdentifiers },
            world.Capture,
            deindexingTimeout: null,
            confidenceEngine: null,
            Signing);
        var feedback = new ExperienceReuseFeedbackService(new Ledger(), trusting);

        // An invalid signature on a record not marked finalized: the verified path would never read its claims, so
        // neither does the opt-out -- it is host-trusted like any hand-written record.
        var hostWritten = world.RunRecord(Guid.NewGuid(), Guid.NewGuid()) with { Origin = ExperienceRecordOrigin.HostWritten };
        world.Store.Seed(hostWritten with { ProvenanceSignature = Sign(hostWritten with { ClosedRoundId = Guid.NewGuid() }, "key-1", KeyOne) });
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await trusting.ApplyEvidenceAsync(Reviewer, world.Machine(hostWritten.SourceRunId, Guid.NewGuid()), CancellationToken.None)).Outcome);

        // Feedback attributing through a tampered finalized record is degraded before the ledger, under the opt-out too.
        var tampered = world.RunRecord(Guid.NewGuid(), Guid.NewGuid());
        world.Store.Seed(tampered with { ProvenanceSignature = Sign(tampered with { ClosedRoundId = Guid.NewGuid() }, "key-1", KeyOne) });
        var degraded = await feedback.RecordAsync(Reviewer, ComparativeFeedback(tampered.SourceRunId, tampered.ClosedRoundId!.Value, world.Lesson.ExperienceId), CancellationToken.None);
        Assert.Equal(ReuseAttributionSource.None, degraded.AttributionSource);
        Assert.Contains(GenericRefusal, degraded.Reason!, StringComparison.Ordinal);

        // ...while an unsigned one is host-trusted there, as the opt-out promises.
        var unsigned = world.RunRecord(Guid.NewGuid(), Guid.NewGuid());
        world.Store.Seed(unsigned);
        var trusted = await feedback.RecordAsync(Reviewer, ComparativeFeedback(unsigned.SourceRunId, unsigned.ClosedRoundId!.Value, world.Lesson.ExperienceId), CancellationToken.None);
        Assert.Equal(ReuseAttributionSource.ComparativeEvaluation, trusted.AttributionSource);
    }

    [Fact]
    public void A_provenance_key_that_is_the_assessment_token_key_is_refused()
    {
        var store = new SigningStore();
        var independence = new ExperienceIndependenceOptions { AssessmentTokenKey = KeyTwo.ToArray() };
        var sharing = Ring(("key-1", KeyOne), ("key-2", KeyTwo));

        var thrown = Assert.Throws<ArgumentException>(() => new ExperienceLifecycleService(
            store, indexingService: null, independence, captureService: null, deindexingTimeout: null, confidenceEngine: null, sharing));
        Assert.DoesNotContain(Convert.ToBase64String(KeyTwo), thrown.Message, StringComparison.Ordinal);

        // Also when finalization is given its own ring.
        var lifecycle = new ExperienceLifecycleService(
            store, indexingService: null, independence, captureService: null, deindexingTimeout: null, confidenceEngine: null,
            new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne, ["key-3"] = KeyTwo.Reverse().ToArray() }, "key-1"));
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Permissive), new CaptureLimits(8, 8, 1_000, 1_000));
        Assert.Throws<ArgumentException>(() => new ExperienceFinalizationService(
            capture, new DefaultExperienceReflector(), store, lifecycle, indexingService: null, indexingTimeout: null,
            new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne, ["key-2"] = KeyTwo }, "key-1")));

        // Distinct keys are fine.
        _ = new ExperienceLifecycleService(
            store, indexingService: null, independence, captureService: null, deindexingTimeout: null, confidenceEngine: null, Signing);
    }

    [Fact]
    public async Task Finalizations_own_ring_must_be_one_the_lifecycle_service_checks_and_then_takes_precedence()
    {
        var store = new SigningStore();
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Permissive), new CaptureLimits(8, 8, 1_000, 1_000));
        var own = new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-2"] = KeyTwo }, "key-2");

        ExperienceFinalizationService Build(ExperienceProvenanceSigningOptions? checking) => new(
            capture,
            new DefaultExperienceReflector(),
            store,
            new ExperienceLifecycleService(store, indexingService: null, new ExperienceIndependenceOptions(), capture, deindexingTimeout: null, confidenceEngine: null, checking),
            indexingService: null,
            indexingTimeout: null,
            own);

        // Signed but never checked; checked under a ring without the current key; the same ID under another key.
        Assert.Throws<ArgumentException>(() => Build(checking: null));
        Assert.Throws<ArgumentException>(() => Build(Signing));
        Assert.Throws<ArgumentException>(() => Build(new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-2"] = KeyOne }, "key-2")));

        // A lifecycle ring holding both keys, current key-1; finalization's own ring signs under key-2.
        var world = new World(
            new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne, ["key-2"] = KeyTwo }, "key-1"),
            finalizationSigning: own);
        var (reuseRun, reuseRound) = await world.FinalizeLessonAndReuseAsync();

        Assert.All(world.Store.Created, record => Assert.Equal("key-2", record.ProvenanceSignature!.KeyId));
        var verifier = new ExperienceLifecycleService(
            world.Store, indexingService: null, new ExperienceIndependenceOptions(), captureService: null, deindexingTimeout: null, confidenceEngine: null,
            new ExperienceProvenanceSigningOptions(new Dictionary<string, byte[]> { ["key-1"] = KeyOne, ["key-2"] = KeyTwo }, "key-1"));
        Assert.Equal(ConfidenceUpdateOutcome.Applied, (await verifier.ApplyEvidenceAsync(Reviewer, world.Machine(reuseRun, reuseRound), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_retry_that_collides_with_a_record_whose_signature_does_not_vouch_is_reported_not_replayed()
    {
        var world = new World(Signing);
        var (reuseRun, _) = await world.FinalizeLessonAndReuseAsync();

        // The genuine record replays as before.
        Assert.Equal(FinalizationOutcome.AlreadyFinalized, (await world.RefinalizeAsync(reuseRun)).Outcome);

        // The same record, its signature stripped (or its claims edited): a retry refuses to report it as finalized.
        var id = ExperienceFinalizationService.ExperienceIdFor(reuseRun, TestScope);
        var genuine = world.Store.Find(id)!;
        foreach (var altered in new[] { genuine with { ProvenanceSignature = null }, genuine with { ClosedRoundId = Guid.NewGuid() } })
        {
            world.Store.Seed(altered);
            var retried = await world.RefinalizeAsync(reuseRun);
            Assert.Equal(FinalizationOutcome.Failed, retried.Outcome);
            Assert.Equal(FinalizationStage.CreateRecord, retried.Stage);
            Assert.Contains("valid provenance signature", retried.Reason!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Nothing_public_on_the_options_returns_key_bytes_and_it_prints_none()
    {
        var type = typeof(ExperienceProvenanceSigningOptions);
        foreach (var member in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
        {
            Assert.False(CarriesBytes(member.PropertyType), $"{member.Name} exposes byte data.");
        }

        foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
        {
            Assert.False(CarriesBytes(method.ReturnType), $"{method.Name} returns byte data.");
        }

        var printed = Ring(("key-1", KeyOne), ("key-2", KeyTwo)).ToString();
        Assert.Contains("key-1", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(KeyOne), printed, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(KeyOne), printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Byte", printed, StringComparison.Ordinal);

        static bool CarriesBytes(Type candidate) =>
            candidate == typeof(byte[])
            || candidate.FullName!.Contains("System.Byte", StringComparison.Ordinal)
            || (candidate.IsGenericType && candidate.GetGenericArguments().Any(CarriesBytes));
    }

    [Fact]
    public async Task Options_registered_through_IOptions_are_picked_up_and_AddOptions_alone_changes_nothing()
    {
        foreach (var throughOptions in new[] { true, false })
        {
            var services = new ServiceCollection();
            var store = new SigningStore();
            services.AddSingleton<IExperienceRecordStore>(store);
            services.AddOptions();
            services.AddAgentExperienceCore(Permissive, new CaptureLimits(8, 8, 1_000, 1_000));
            if (throughOptions)
            {
                services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Signing));
            }

            using var provider = services.BuildServiceProvider();
            var world = new World(
                provider.GetRequiredService<ExperienceLifecycleService>(),
                provider.GetRequiredService<ExperienceFinalizationService>(),
                store,
                (InMemoryExperienceCaptureService)provider.GetRequiredService<IExperienceCaptureService>());
            await world.FinalizeLessonAndReuseAsync();

            Assert.All(store.Created, record => Assert.Equal(throughOptions ? "key-1" : null, record.ProvenanceSignature?.KeyId));
        }
    }

    private static ExperienceReuseFeedback ComparativeFeedback(Guid runId, Guid roundId, Guid experienceId) => new(
        FeedbackId: Guid.NewGuid(),
        RunId: runId,
        Scope: TestScope,
        ExposedExperienceIds: [experienceId],
        RunOutcome: TaskVerificationStatus.Verified,
        Measure: new ReuseMeasure("tool-calls", 3),
        ObservedAt: Now)
    {
        ComparativeEvaluation = new ComparativeEvaluationResult(
            "comparator", runId, roundId, ExperienceReuseBenefit.Improved, [experienceId],
            [new Evidence(Guid.NewGuid(), roundId, "rev-1", "tests", "TestResult", CheckResult.Pass, "ci", null, Now)],
            "better with the lesson", Now),
    };

    // ---- Helpers -------------------------------------------------------------------------------------------

    private static ExperienceProvenanceSigningOptions Ring(params (string KeyId, byte[] Key)[] keys) =>
        new(keys.ToDictionary(pair => pair.KeyId, pair => pair.Key, StringComparer.Ordinal), keys[0].KeyId);

    /// <summary>The one sentence every signature refusal carries: which check failed is never told to the caller.</summary>
    private const string GenericRefusal = "its provenance signature does not vouch for it";

    private static void AssertHostWritten(ApplyConfidenceEvidenceResult result)
    {
        Assert.Equal(ConfidenceUpdateOutcome.Unverified, result.Outcome);
        Assert.Equal(IndependenceRefusal.HostWrittenRun, result.Refusal);
        Assert.Null(result.Update);
        Assert.Contains(GenericRefusal, result.Reason!, StringComparison.Ordinal);
        foreach (var hint in new[] { "missing", "unknown", "key ring", "does not verify", "invalid" })
        {
            Assert.DoesNotContain(hint, result.Reason!, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(Convert.ToBase64String(KeyOne), result.Reason!, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(KeyTwo), result.Reason!, StringComparison.Ordinal);
    }

    private static ExperienceProvenanceSignature Sign(ExperienceRecord claims, string keyId, byte[] key) =>
        new(keyId, ExperienceProvenanceSignature.HmacSha256, HMACSHA256.HashData(key, Canonical(claims)));

    /// <summary>The documented canonical encoding, written independently of the library's.</summary>
    private static byte[] Canonical(ExperienceRecord record)
    {
        var bytes = new List<byte>();
        void Str(string? value)
        {
            if (value is null)
            {
                bytes.Add(0);
                return;
            }

            bytes.Add(1);
            var utf8 = Encoding.UTF8.GetBytes(value);
            Int32(utf8.Length);
            bytes.AddRange(utf8);
        }

        void Int32(int value)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            bytes.AddRange(buffer);
        }

        void Id(Guid value) => bytes.AddRange(value.ToByteArray(bigEndian: true));

        Str("aexp-prov:v1");
        Id(record.ExperienceId);
        Str(record.Scope.TenantId);
        Str(record.Scope.ApplicationId);
        Str(record.Scope.ProjectId);
        Str(record.Scope.TeamId);
        Str(record.Scope.AgentId);
        Str(record.Scope.UserId);
        Id(record.SourceRunId);
        if (record.ClosedRoundId is { } round)
        {
            bytes.Add(1);
            Id(round);
        }
        else
        {
            bytes.Add(0);
        }

        Int32((int)record.Origin);
        var exposures = record.Provenance.ExposedTo.OrderBy(exposure => exposure.ExperienceId.ToByteArray(bigEndian: true), ByteOrder.Instance).ThenBy(exposure => exposure.Revision).ToList();
        Int32(exposures.Count);
        foreach (var exposure in exposures)
        {
            Id(exposure.ExperienceId);
            var revision = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(revision, exposure.Revision);
            bytes.AddRange(revision);
        }

        return [.. bytes];
    }

    private static string SourceFile(string first, params string[] rest)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", ".."));
        return Path.Combine([root, first, .. rest]);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                if (x[i] != y[i])
                {
                    return x[i].CompareTo(y[i]);
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>
    /// One scope: a lesson finalized from a real captured run, and a second run, given the lesson, finalized with a
    /// closed round -- both through the shipping capture, finalization and lifecycle services.
    /// </summary>
    private sealed class World
    {
        public World(
            ExperienceProvenanceSigningOptions? signing,
            SigningStore? store = null,
            InMemoryExperienceCaptureService? capture = null,
            ExperienceProvenanceSigningOptions? finalizationSigning = null)
        {
            Store = store ?? new SigningStore();
            Capture = capture ?? new InMemoryExperienceCaptureService(new DefaultSanitizer(Permissive), new CaptureLimits(8, 8, 1_000, 1_000));
            Lifecycle = new ExperienceLifecycleService(
                Store, indexingService: null, new ExperienceIndependenceOptions(), Capture, deindexingTimeout: null, confidenceEngine: null, signing);
            Finalization = new ExperienceFinalizationService(
                Capture, new DefaultExperienceReflector(), Store, Lifecycle, indexingService: null, indexingTimeout: null, finalizationSigning);
            Feedback = new ExperienceReuseFeedbackService(new Ledger(), Lifecycle);
        }

        public World(ExperienceLifecycleService lifecycle, ExperienceFinalizationService finalization, SigningStore store, InMemoryExperienceCaptureService capture)
        {
            Store = store;
            Capture = capture;
            Lifecycle = lifecycle;
            Finalization = finalization;
            Feedback = new ExperienceReuseFeedbackService(new Ledger(), Lifecycle);
        }

        public SigningStore Store { get; }

        public InMemoryExperienceCaptureService Capture { get; }

        public ExperienceLifecycleService Lifecycle { get; }

        public ExperienceFinalizationService Finalization { get; }

        public ExperienceReuseFeedbackService Feedback { get; }

        public ExperienceRecord Lesson { get; private set; } = null!;

        public void UseLesson(ExperienceRecord lesson) => Lesson = lesson;

        /// <summary>Starts, completes and finalizes a run in <paramref name="scope"/> with a closed round.</summary>
        public async Task<FinalizeExperienceResult> FinalizeRunInAsync(Scope scope)
        {
            var runId = Guid.NewGuid();
            Assert.Equal(StartRunOutcome.Started, Capture.StartRun(
                runId, "task-1", "a task", scope,
                new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
                new Provenance("tests", null, Now, null), Now).Outcome);
            await CompleteAsync(runId);
            return await FinalizeAsync(runId, Guid.NewGuid());
        }

        /// <summary>Finalizes an already captured and completed run again, as a retry would.</summary>
        public Task<FinalizeExperienceResult> RefinalizeAsync(Guid runId) => FinalizeAsync(runId, Guid.NewGuid());

        /// <summary>Finalizes a lesson, then a run that was given it; returns the second run and its closed round.</summary>
        public async Task<(Guid RunId, Guid RoundId)> FinalizeLessonAndReuseAsync()
        {
            var lessonRun = StartCapturedRun(exposures: []);
            await CompleteAsync(lessonRun);
            var lesson = await FinalizeAsync(lessonRun, Guid.NewGuid());
            Assert.Equal(FinalizationOutcome.Validated, lesson.Outcome);
            Lesson = Store.Find(lesson.ExperienceId!.Value)!;

            return await FinalizeReuseOfAsync(Lesson);
        }

        public async Task<(Guid RunId, Guid RoundId)> FinalizeReuseOfAsync(ExperienceRecord lesson)
        {
            Lesson = lesson;
            var reuseRun = StartCapturedRun(exposures: [new RunExposure(lesson.ExperienceId, lesson.Revision)]);
            await CompleteAsync(reuseRun);
            var round = Guid.NewGuid();
            Assert.True((await FinalizeAsync(reuseRun, round)).IsDurable);
            return (reuseRun, round);
        }

        /// <summary>A record for <paramref name="runId"/> as finalization would have written it, exposed to the lesson, unsigned.</summary>
        public ExperienceRecord RunRecord(Guid runId, Guid round) => new(
            ExperienceId: ExperienceFinalizationService.ExperienceIdFor(runId, TestScope),
            SourceRunId: runId,
            Scope: TestScope,
            TaskId: "task-1",
            TaskSummary: null,
            Attempts: [],
            Outcome: new Outcome(TaskVerificationStatus.Verified, [], null, Now),
            CompletionScore: 1,
            Reflection: null,
            Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("tests", null, Now, null) { ExposedTo = [new RunExposure(Lesson.ExperienceId, 0)] },
            Status: ExperienceStatus.Quarantined,
            ReuseConfidence: 0,
            SupportingValidations: 0,
            Contradictions: 0,
            Revision: 1,
            CreatedAt: Now,
            UpdatedAt: Now)
        {
            ClosedRoundId = round,
            Origin = ExperienceRecordOrigin.Finalized,
        };

        public ApplyConfidenceEvidenceRequest Machine(Guid runId, Guid roundId, Guid? experienceId = null) => new(
            EventId: Guid.NewGuid(),
            ExperienceId: experienceId ?? Lesson.ExperienceId,
            Scope: TestScope,
            EvidenceId: Guid.NewGuid(),
            Kind: ConfidenceEvidenceKind.Supporting,
            Source: ConfidenceEvidenceSource.Machine,
            RunId: runId,
            VerificationRoundId: roundId,
            Reason: "reused and the checks passed",
            Producer: "tests",
            OccurredAt: Now);

        public Guid StartCapturedRun(IReadOnlyList<RunExposure>? exposures = null)
        {
            var runId = Guid.NewGuid();
            Assert.Equal(StartRunOutcome.Started, Capture.StartRun(
                runId,
                "task-1",
                "a task",
                TestScope,
                new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
                new Provenance("tests", null, Now, null),
                Now).Outcome);

            exposures ??= Lesson is null ? [] : [new RunExposure(Lesson.ExperienceId, Lesson.Revision)];
            if (exposures.Count > 0)
            {
                Assert.Equal(RecordExposureOutcome.Recorded, Capture.RecordExposure(runId, exposures).Outcome);
            }

            return runId;
        }

        private async Task CompleteAsync(Guid runId)
        {
            var appended = await Capture.AppendAttemptAsync(
                runId, new AppendAttemptRequest(Guid.NewGuid(), Now, TimeSpan.FromSeconds(1), [], "done", null));
            Assert.Equal(AppendAttemptOutcome.Recorded, appended.Outcome);
            var completed = await Capture.CompleteRunAsync(runId, Guid.NewGuid(), RunExecutionStatus.Completed, Now.AddMinutes(1));
            Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);
        }

        private Task<FinalizeExperienceResult> FinalizeAsync(Guid runId, Guid round) =>
            Finalization.FinalizeAsync(new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: Reviewer,
                ClosedRound: new ClosedVerificationRound(round, "rev-1"),
                RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                Evidence: [new Evidence(Guid.NewGuid(), round, "rev-1", "tests", "TestResult", CheckResult.Pass, "ci", null, Now)],
                CurrentArtifactRevision: "rev-1",
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: Now.AddMinutes(2)));
    }

    /// <summary>
    /// A scope-exact store double: create-once records, lifecycle commits that apply confidence, and a history.
    /// It persists a provenance signature as given and never checks it, as a store must.
    /// </summary>
    private sealed class SigningStore : IExperienceRecordStore
    {
        private readonly Dictionary<Guid, ExperienceRecord> _records = [];
        private readonly List<StoredLifecycleEvent> _history = [];

        public bool RefuseQueries { get; set; }

        public List<ExperienceRecord> Created { get; } = [];

        public void Seed(ExperienceRecord record) => _records[record.ExperienceId] = record;

        public ExperienceRecord? Find(Guid experienceId) => _records.GetValueOrDefault(experienceId);

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken)
        {
            if (!_records.TryAdd(record.ExperienceId, record))
            {
                return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []));
            }

            Created.Add(record);
            return Task.FromResult(new ExperienceRecordCreateResult(ExperienceStoreOutcome.Created, []));
        }

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.TryGetValue(experienceId, out var record) && record.Scope == scope
                ? new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, record, [])
                : new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []));

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
            AuthorizationContext authorization,
            Scope scope,
            LifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken)
        {
            if (!_records.TryGetValue(lifecycleEvent.ExperienceRecordId, out var record) || record.Scope != scope)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.NotFound, 0, null, []));
            }

            if (record.Revision != lifecycleEvent.ExpectedRevision)
            {
                return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, record.Revision, null, []));
            }

            var revision = record.Revision + 1;
            var update = lifecycleEvent.Confidence;
            _records[record.ExperienceId] = record with
            {
                Status = lifecycleEvent.CurrentStatus,
                Revision = revision,
                ReuseConfidence = update?.NewReuseConfidence ?? record.ReuseConfidence,
                SupportingValidations = update?.NewSupportingValidations ?? record.SupportingValidations,
                Contradictions = update?.NewContradictions ?? record.Contradictions,
            };
            _history.Add(new StoredLifecycleEvent(lifecycleEvent, Now, revision));
            return Task.FromResult(new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, revision, null, [], update));
        }

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken)
        {
            if (!_records.TryGetValue(query.ExperienceId, out var record) || record.Scope != query.Scope)
            {
                return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.NotFound, 0, [], []));
            }

            var events = _history
                .Where(stored => stored.Event.ExperienceRecordId == query.ExperienceId && stored.AppliedRevision > (query.StartAfterRevision ?? -1))
                .OrderBy(stored => stored.AppliedRevision)
                .ToList();
            return Task.FromResult(new ExperienceRecordHistoryResult(ExperienceStoreOutcome.Found, record.Revision, events, []));
        }

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(RefuseQueries
                ? new ExperienceRecordQueryResult(ExperienceStoreOutcome.Denied, [], [])
                : new ExperienceRecordQueryResult(
                    ExperienceStoreOutcome.Found,
                    [.. _records.Values
                        .Where(record => record.Scope == query.Scope && (query.Statuses is null || query.Statuses.Contains(record.Status)))
                        .OrderByDescending(record => record.CreatedAt)
                        .Take(query.Limit)],
                    []));

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            Guid replacementExperienceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Ledger : IExperienceReuseFeedbackStore
    {
        public Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
            AuthorizationContext authorization,
            RecordedExperienceReuseFeedback feedback,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceReuseFeedbackStoreResult(ExperienceReuseFeedbackStoreOutcome.Recorded, feedback, []));
    }
}
