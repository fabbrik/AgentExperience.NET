using AgentExperience.Core.Confidence;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 13.1 (KL-11, part 2) against a real PostgreSQL container, in plaintext and (with
/// <c>AGENTEXPERIENCE_TEST_ENCRYPTION=on</c>) crypto-shredding mode: finalization's provenance signature travels in
/// the payload and reads back byte for byte, and a record whose signed claims were changed in the database, or
/// forged through <c>CreateAsync</c>, stops vouching for its run. Each test uses its own random tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresSignedProvenanceTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(value => (byte)(value * 5 + 3))];

    private static readonly ExperienceProvenanceSigningOptions Signing =
        new(new Dictionary<string, byte[]> { ["integration-key-1"] = Key }, "integration-key-1");

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
        ["ToolResult"] = new(new HashSet<string>(StringComparer.Ordinal) { "value" }, new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
    });

    /// <summary>The one sentence a caller is told for any signature that does not vouch.</summary>
    private const string GenericRefusal = "its provenance signature does not vouch for it";

    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;
    private readonly InMemoryExperienceCaptureService _capture;
    private readonly ExperienceFinalizationService _finalization;

    /// <summary>
    /// Verifies with no capture service, as a process that restarted after finalization would: a run is then known
    /// only through its stored record, which is exactly what the signature protects.
    /// </summary>
    private readonly ExperienceLifecycleService _verifier;

    public PostgresSignedProvenanceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(8, 8, 1_000, 1_000));

        var lifecycle = new ExperienceLifecycleService(
            _store, indexingService: null, new ExperienceIndependenceOptions(), _capture, deindexingTimeout: null, confidenceEngine: null, Signing);
        _finalization = new ExperienceFinalizationService(_capture, new DefaultExperienceReflector(), _store, lifecycle);
        _verifier = new ExperienceLifecycleService(
            _store, indexingService: null, new ExperienceIndependenceOptions(), captureService: null, deindexingTimeout: null, confidenceEngine: null, Signing);
    }

    [Fact]
    public async Task A_signed_record_survives_the_round_trip_and_its_signature_travels_in_the_payload()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, Guid.NewGuid(), exposedTo: [lesson.ExperienceId]);

        var read = (await _store.GetAsync(auth, scope, reuse.ExperienceId, CancellationToken.None)).Record!;
        var signature = Assert.IsType<ExperienceProvenanceSignature>(read.ProvenanceSignature);
        Assert.Equal(reuse.Signature, signature);
        Assert.Equal("integration-key-1", signature.KeyId);
        Assert.Equal(32, signature.Value.Length);

        // In the payload (sealed with the rest in crypto-shredding mode), never in a column.
        using (var payload = System.Text.Json.JsonDocument.Parse(await PayloadJsonAsync(reuse.ExperienceId)))
        {
            var stored = payload.RootElement.GetProperty("provenanceSignature");
            Assert.Equal("integration-key-1", stored.GetProperty("keyId").GetString());
            Assert.Equal(Convert.ToBase64String(signature.Value.Span), stored.GetProperty("value").GetString());
        }

        Assert.Equal(!EncryptionMode.IsOn, await PayloadTextContainsAsync(reuse.ExperienceId, "provenanceSignature"));

        // And, read back from the database, it still vouches for the run.
        var applied = await _verifier.ApplyEvidenceAsync(auth, Machine(scope, lesson.ExperienceId, reuse.RunId, reuse.RoundId), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);

        // A record written with no signature reads back with none, and its payload has no such field.
        var plain = Minimal(scope);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, plain, CancellationToken.None)).Outcome);
        Assert.Null((await _store.GetAsync(auth, scope, plain.ExperienceId, CancellationToken.None)).Record!.ProvenanceSignature);
        using var plainPayload = System.Text.Json.JsonDocument.Parse(await PayloadJsonAsync(plain.ExperienceId));
        Assert.False(plainPayload.RootElement.TryGetProperty("provenanceSignature", out _));
    }

    [Fact]
    public async Task A_payload_whose_signed_claim_was_changed_in_the_database_is_refused_as_host_written()
    {
        if (EncryptionMode.IsOn)
        {
            // A sealed payload cannot be edited in place without the record's key; the forged-create test below
            // covers that mode through the store port.
            return;
        }

        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, Guid.NewGuid(), exposedTo: [lesson.ExperienceId]);
        var inventedRound = Guid.NewGuid();

        // What an operator with the owner's or a superuser's rights can do that the application role cannot: rewrite
        // the payload's closed round in place, leaving the signature as finalization wrote it.
        await using (var tamper = _fixture.SuperuserDataSource.CreateCommand(
            "UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{closedRoundId}', to_jsonb(@round::text)) WHERE experience_id = @id"))
        {
            tamper.Parameters.Add(new NpgsqlParameter<Guid>("round", inventedRound));
            tamper.Parameters.Add(new NpgsqlParameter<Guid>("id", reuse.ExperienceId));
            Assert.Equal(1, await tamper.ExecuteNonQueryAsync());
        }

        var read = (await _store.GetAsync(auth, scope, reuse.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(inventedRound, read.ClosedRoundId);
        Assert.Equal(reuse.Signature, read.ProvenanceSignature);

        foreach (var round in new[] { inventedRound, reuse.RoundId })
        {
            var refused = await _verifier.ApplyEvidenceAsync(auth, Machine(scope, lesson.ExperienceId, reuse.RunId, round), CancellationToken.None);
            Assert.Equal(ConfidenceUpdateOutcome.Unverified, refused.Outcome);
            Assert.Equal(IndependenceRefusal.HostWrittenRun, refused.Refusal);
            Assert.Contains(GenericRefusal, refused.Reason!, StringComparison.Ordinal);
        }

        Assert.Equal(0, await CountEvidenceAsync(lesson.ExperienceId));
    }

    [Fact]
    public async Task A_record_forged_through_CreateAsync_with_a_copied_signature_or_none_is_refused_in_either_mode()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, Guid.NewGuid(), exposedTo: [lesson.ExperienceId]);
        var genuine = (await _store.GetAsync(auth, scope, reuse.ExperienceId, CancellationToken.None)).Record!;

        foreach (var signature in new[] { genuine.ProvenanceSignature, null })
        {
            // A run that never happened, claiming to be finalized, exposed to the lesson, with a round of its own.
            var run = Guid.NewGuid();
            var round = Guid.NewGuid();
            var forged = genuine with
            {
                ExperienceId = ExperienceFinalizationService.ExperienceIdFor(run, scope),
                SourceRunId = run,
                ClosedRoundId = round,
                Reflection = null,
                Status = ExperienceStatus.Quarantined,
                ReuseConfidence = 0,
                SupportingValidations = 0,
                Revision = 0,
                ProvenanceSignature = signature,
            };
            Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, forged, CancellationToken.None)).Outcome);

            var refused = await _verifier.ApplyEvidenceAsync(auth, Machine(scope, lesson.ExperienceId, run, round), CancellationToken.None);
            Assert.Equal(ConfidenceUpdateOutcome.Unverified, refused.Outcome);
            Assert.Equal(IndependenceRefusal.HostWrittenRun, refused.Refusal);
            Assert.Contains(GenericRefusal, refused.Reason!, StringComparison.Ordinal);
        }

        Assert.Equal(0, await CountEvidenceAsync(lesson.ExperienceId));
    }

    [Fact]
    public async Task The_signature_survives_lifecycle_commits_and_still_vouches_afterwards()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, Guid.NewGuid(), exposedTo: [lesson.ExperienceId]);
        var before = (await _store.GetAsync(auth, scope, reuse.ExperienceId, CancellationToken.None)).Record!;

        // A transition on the run's own record (Validated -> Revoked), and confidence evidence moving the lesson.
        var revoked = await _store.CommitLifecycleEventAsync(
            auth,
            scope,
            new LifecycleEvent(
                EventId: Guid.NewGuid(),
                ExperienceRecordId: reuse.ExperienceId,
                PriorStatus: before.Status,
                CurrentStatus: ExperienceStatus.Revoked,
                Reason: "withdrawn",
                Producer: "tests",
                OccurredAt: ColumnTime.AddMinutes(5),
                ExpectedRevision: before.Revision,
                ReplacementExperienceId: null),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, revoked.Outcome);

        var after = (await _store.GetAsync(auth, scope, reuse.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Revoked, after.Status);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(reuse.Signature, after.ProvenanceSignature);

        var applied = await _verifier.ApplyEvidenceAsync(auth, Machine(scope, lesson.ExperienceId, reuse.RunId, reuse.RoundId), CancellationToken.None);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, applied.Outcome);
        var lessonAfter = (await _store.GetAsync(auth, scope, lesson.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(lesson.Signature, lessonAfter.ProvenanceSignature);
        Assert.Equal(2, lessonAfter.SupportingValidations);
    }

    private static ApplyConfidenceEvidenceRequest Machine(Scope scope, Guid experienceId, Guid runId, Guid roundId) => new(
        EventId: Guid.NewGuid(),
        ExperienceId: experienceId,
        Scope: scope,
        EvidenceId: Guid.NewGuid(),
        Kind: ConfidenceEvidenceKind.Supporting,
        Source: ConfidenceEvidenceSource.Machine,
        RunId: runId,
        VerificationRoundId: roundId,
        Reason: "reused, and the checks passed",
        Producer: "tests",
        OccurredAt: ColumnTime);

    /// <summary>Captures a run in <paramref name="scope"/> and finalizes it for real, with a closed round.</summary>
    private async Task<(Guid RunId, Guid ExperienceId, Guid RoundId, ExperienceProvenanceSignature? Signature)> FinalizeAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid round,
        IReadOnlyList<Guid>? exposedTo = null)
    {
        var runId = Guid.NewGuid();
        Assert.Equal(StartRunOutcome.Started, _capture.StartRun(
            runId,
            "task-1",
            "resolve the ticket",
            scope,
            new EnvironmentFingerprint("worker-01", "net10.0", "linux-x64", null, new Dictionary<string, string>()),
            new Provenance("integration-tests", null, PayloadTime, null),
            PayloadTime).Outcome);

        if (exposedTo is { Count: > 0 })
        {
            var exposures = new List<RunExposure>();
            foreach (var experienceId in exposedTo)
            {
                var read = await _store.GetAsync(auth, scope, experienceId, CancellationToken.None);
                exposures.Add(new RunExposure(experienceId, read.Record!.Revision));
            }

            Assert.Equal(RecordExposureOutcome.Recorded, _capture.RecordExposure(runId, exposures).Outcome);
        }

        Assert.Equal(AppendAttemptOutcome.Recorded, (await _capture.AppendAttemptAsync(
            runId, new AppendAttemptRequest(Guid.NewGuid(), PayloadTime, TimeSpan.FromSeconds(1), [], "done", null))).Outcome);
        Assert.Equal(CompleteRunOutcome.Recorded, (await _capture.CompleteRunAsync(
            runId, Guid.NewGuid(), RunExecutionStatus.Completed, PayloadTime.AddMinutes(1))).Outcome);

        var result = await _finalization.FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: auth,
                ClosedRound: new ClosedVerificationRound(round, ArtifactRevision),
                RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                Evidence: [new Evidence(Guid.NewGuid(), round, ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, PayloadTime)],
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: ColumnTime),
            CancellationToken.None);

        Assert.True(result.IsDurable);
        return (runId, result.ExperienceId!.Value, round, result.Record?.ProvenanceSignature);
    }

    private async Task<bool> PayloadTextContainsAsync(Guid experienceId, string text)
    {
        await using var command = _fixture.RawDataSource.CreateCommand(
            "SELECT strpos(payload::text, @text) > 0 FROM agent_experience.experience_records WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        command.Parameters.Add(new NpgsqlParameter<string>("text", text));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The stored payload JSON: as it is in plaintext mode, or opened with the suite's key store when sealed.</summary>
    private async Task<string> PayloadJsonAsync(Guid experienceId)
    {
        if (!EncryptionMode.IsOn)
        {
            await using var command = _fixture.RawDataSource.CreateCommand(
                "SELECT payload::text FROM agent_experience.experience_records WHERE experience_id = @id");
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
            return (string)(await command.ExecuteScalarAsync())!;
        }

        await using var sealedRead = _fixture.RawDataSource.CreateCommand(
            "SELECT payload ->> 'sealed', tenant_id, application_id, project_id, team_id, agent_id, user_id " +
            "FROM agent_experience.experience_records WHERE experience_id = @id");
        sealedRead.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        await using var reader = await sealedRead.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var scope = new Scope(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
        using var key = await EncryptionMode.Shared.ForReadAsync(experienceId, scope, CancellationToken.None);
        var (_, payloadJson) = SealedText.ReadSealedRecordPlaintext(key!.Open(SealedText.PayloadColumn, Guid.Empty, reader.GetString(0)));
        return payloadJson;
    }

    private async Task<long> CountEvidenceAsync(Guid experienceId)
    {
        await using var command = _fixture.RawDataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.confidence_evidence WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
