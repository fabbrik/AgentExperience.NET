using AgentExperience.Core.Confidence;
using AgentExperience.Core.Feedback;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 6.6 (KL-11) against a real PostgreSQL container, with the shipping capture service, reflector,
/// finalization and lifecycle services in their default, verifying mode. Runs A and B are captured and
/// finalized for real; the lesson from A is reused in B. The closed round travels in the payload, the
/// database spends an assessment once per record, and forged identifiers write nothing. Each test uses its
/// own random tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresVerifiedIndependenceTests
{
    private const string ArtifactRevision = "rev-1";

    private static readonly byte[] Key = [.. Enumerable.Range(0, 32).Select(value => (byte)(255 - value))];

    private static readonly SanitizationOptions Sanitization = new(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal)
    {
        ["ToolArguments"] = new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
        ["ToolResult"] = new(new HashSet<string>(StringComparer.Ordinal) { "value" }, new HashSet<string>(StringComparer.Ordinal), 2, 5, 1_000, 100),
    });

    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;
    private readonly InMemoryExperienceCaptureService _capture;
    private readonly ExperienceLifecycleService _lifecycle;
    private readonly ExperienceFinalizationService _finalization;
    private readonly ExperienceReuseFeedbackService _feedback;
    private readonly AssessmentTokenIssuer _issuer;

    public PostgresVerifiedIndependenceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(Sanitization), new CaptureLimits(8, 8, 1_000, 1_000));

        var independence = new ExperienceIndependenceOptions { AssessmentTokenKey = Key };
        _lifecycle = new ExperienceLifecycleService(_store, indexingService: null, independence, _capture);
        _issuer = new AssessmentTokenIssuer(independence);
        _finalization = new ExperienceFinalizationService(_capture, new DefaultExperienceReflector(), _store, _lifecycle);
        _feedback = new ExperienceReuseFeedbackService(new PostgresExperienceReuseFeedbackStore(fixture.DataSource), _lifecycle);
    }

    [Fact]
    public async Task Finalization_stores_the_closed_round_in_the_payload_and_stores_none_when_none_was_closed()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var round = Guid.NewGuid();

        var closed = await FinalizeAsync(auth, scope, round);
        var open = await FinalizeAsync(auth, scope, closedRound: null);

        var readClosed = (await _store.GetAsync(auth, scope, closed.ExperienceId, CancellationToken.None)).Record!;
        var readOpen = (await _store.GetAsync(auth, scope, open.ExperienceId, CancellationToken.None)).Record!;

        Assert.Equal(round, readClosed.ClosedRoundId);
        Assert.Null(readOpen.ClosedRoundId);

        // Omitted rather than written as null, so a payload with no round is byte for byte what it was.
        Assert.True(await PayloadHasClosedRoundAsync(closed.ExperienceId));
        Assert.False(await PayloadHasClosedRoundAsync(open.ExperienceId));

        // And the store refuses a hand-written empty one.
        var invalid = await _store.CreateAsync(auth, Minimal(scope) with { ClosedRoundId = Guid.Empty }, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, invalid.Outcome);
        Assert.Contains(invalid.Errors, error => error.Path == "ClosedRoundId");
    }

    [Fact]
    public async Task Forged_runs_rounds_and_assessment_tokens_are_refused_and_nothing_reaches_the_ledger()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var roundB = Guid.NewGuid();
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, roundB);

        var refusals = new (ApplyConfidenceEvidenceRequest Request, IndependenceRefusal Refusal)[]
        {
            (Machine(scope, lesson.ExperienceId, Guid.NewGuid(), Guid.NewGuid()), IndependenceRefusal.UnknownRun),
            (Machine(scope, lesson.ExperienceId, reuse.RunId, Guid.NewGuid()), IndependenceRefusal.UnknownRound),
            (Machine(scope, lesson.ExperienceId, lesson.RunId, lesson.RoundId!.Value), IndependenceRefusal.OwnRun),
            (Human(scope, lesson.ExperienceId, reuse.RunId, Guid.NewGuid().ToString()), IndependenceRefusal.AssessmentTokenInvalid),
            (Human(scope, lesson.ExperienceId, reuse.RunId, token: null), IndependenceRefusal.AssessmentTokenMissing),
            (Human(scope, lesson.ExperienceId, reuse.RunId,
                _issuer.Issue(auth, Scope(tenant, project: "project-2"), reuse.RunId, ConfidenceEvidenceKind.Supporting, [lesson.ExperienceId]).Token),
                IndependenceRefusal.AssessmentTokenInvalid),
            (Human(scope, lesson.ExperienceId, reuse.RunId,
                _issuer.Issue(auth, scope, reuse.RunId, ConfidenceEvidenceKind.Supporting, [reuse.ExperienceId]).Token),
                IndependenceRefusal.AssessmentTokenNotForRecord),
        };

        foreach (var (request, refusal) in refusals)
        {
            var result = await _lifecycle.ApplyEvidenceAsync(auth, request, CancellationToken.None);

            Assert.Equal(ConfidenceUpdateOutcome.Unverified, result.Outcome);
            Assert.Equal(refusal, result.Refusal);
        }

        Assert.Equal(0, await CountEvidenceAsync(lesson.ExperienceId));
        var stored = (await _store.GetAsync(auth, scope, lesson.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal((1, 1L), (stored.SupportingValidations, stored.Revision));
    }

    [Fact]
    public async Task A_legitimate_flow_counts_once_per_key_and_the_history_names_the_assessment()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var reviewer = auth with { PrincipalId = "reviewer-2" };
        var roundB = Guid.NewGuid();
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, roundB);

        var machine = await Apply(auth, Machine(scope, lesson.ExperienceId, reuse.RunId, roundB));
        var machineAgain = await Apply(auth, Machine(scope, lesson.ExperienceId, reuse.RunId, roundB));

        var token = _issuer.Issue(reviewer, scope, reuse.RunId, ConfidenceEvidenceKind.Supporting, [lesson.ExperienceId]);
        var human = await Apply(reviewer, Human(scope, lesson.ExperienceId, reuse.RunId, token.Token));

        Assert.True(machine.Counted);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, machineAgain.Outcome);
        Assert.False(machineAgain.Counted);
        Assert.True(human.Counted);

        var stored = (await _store.GetAsync(auth, scope, lesson.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(3, stored.SupportingValidations);

        // The audit trail carries the assessment on the counted human event, and on its ledger row.
        var history = await _store.GetFirstHistoryPageAsync(auth, scope, lesson.ExperienceId, CancellationToken.None);
        var humanEvent = Assert.Single(history.Events, stored => stored.Event.Confidence?.Source == ConfidenceEvidenceSource.Human);
        Assert.Equal(token.AssessmentId, humanEvent.Event.Confidence!.AssessmentId);
        Assert.All(
            history.Events.Where(stored => stored.Event.Confidence?.Source == ConfidenceEvidenceSource.Machine),
            stored => Assert.Null(stored.Event.Confidence!.AssessmentId));
        Assert.Equal(token.AssessmentId, await ReadAssessmentAsync(human.Update!.EvidenceId));
    }

    [Fact]
    public async Task A_replayed_token_is_refused_by_the_database_and_an_identical_retry_still_replays()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, Guid.NewGuid());

        var token = _issuer.Issue(auth, scope, reuse.RunId, ConfidenceEvidenceKind.Supporting, [lesson.ExperienceId]);
        var request = Human(scope, lesson.ExperienceId, reuse.RunId, token.Token);

        var first = await Apply(auth, request);
        var retry = await Apply(auth, request);
        var replay = await Apply(auth, request with { EvidenceId = Guid.NewGuid(), EventId = Guid.NewGuid() });

        Assert.True(first.Counted);
        Assert.Equal(ConfidenceUpdateOutcome.Applied, retry.Outcome);
        Assert.Equal(first.Update!.EvidenceId, retry.Update!.EvidenceId);
        Assert.Equal(ConfidenceUpdateOutcome.Unverified, replay.Outcome);
        Assert.Equal(IndependenceRefusal.AssessmentTokenReplayed, replay.Refusal);

        // The replay wrote nothing: not a counted row, and not a recorded-and-uncounted one either.
        Assert.Equal(1, await CountEvidenceAsync(lesson.ExperienceId));
    }

    [Fact]
    public async Task Resubmitting_an_evidence_id_with_a_different_assessment_is_a_conflict_not_a_replay()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, Guid.NewGuid());

        var first = _issuer.Issue(auth, scope, reuse.RunId, ConfidenceEvidenceKind.Supporting, [lesson.ExperienceId]);
        var second = _issuer.Issue(auth, scope, reuse.RunId, ConfidenceEvidenceKind.Supporting, [lesson.ExperienceId]);
        var request = Human(scope, lesson.ExperienceId, reuse.RunId, first.Token);

        Assert.True((await Apply(auth, request)).Counted);

        // Same evidence and event IDs, a second genuine token: the assessment is part of what is replayed.
        var resent = await Apply(auth, request with { AssessmentToken = second.Token });

        Assert.Equal(ConfidenceUpdateOutcome.Conflict, resent.Outcome);
        Assert.Equal(1, await CountEvidenceAsync(lesson.ExperienceId));
        Assert.Equal(first.AssessmentId, await ReadAssessmentAsync(request.EvidenceId));
    }

    [Fact]
    public async Task The_store_refuses_an_assessment_on_machine_evidence_or_an_empty_one_as_Invalid()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());

        var empty = await CommitHumanAsync(auth, scope, lesson.ExperienceId, Guid.NewGuid(), Guid.Empty, expectedRevision: 1);
        Assert.Equal(ExperienceStoreOutcome.Invalid, empty.Outcome);
        Assert.Contains(empty.Errors, error => error.Path == ConfidenceUpdate.AssessmentIdPath);

        var machine = await CommitHumanAsync(
            auth, scope, lesson.ExperienceId, Guid.NewGuid(), Guid.NewGuid(), expectedRevision: 1, machineRound: Guid.NewGuid());
        Assert.Equal(ExperienceStoreOutcome.Invalid, machine.Outcome);
        Assert.Contains(machine.Errors, error => error.Path == ConfidenceUpdate.AssessmentIdPath);

        Assert.Equal(0, await CountEvidenceAsync(lesson.ExperienceId));
    }

    [Fact]
    public async Task The_assessment_index_refuses_a_second_use_even_where_the_independence_key_is_free()
    {
        // Only a writer that bypasses Core's token check can present one assessment under two different
        // runs, so the store is driven directly: the database, not Core, is what spends an assessment.
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var assessment = Guid.NewGuid();

        var first = await CommitHumanAsync(auth, scope, lesson.ExperienceId, Guid.NewGuid(), assessment, expectedRevision: 1);
        var second = await CommitHumanAsync(auth, scope, lesson.ExperienceId, Guid.NewGuid(), assessment, expectedRevision: 2);

        Assert.Equal(ExperienceStoreOutcome.Committed, first.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Conflict, second.Outcome);
        Assert.Contains(second.Errors, error => error.Path == ConfidenceUpdate.AssessmentIdPath);
        Assert.Equal(1, await CountEvidenceAsync(lesson.ExperienceId));

        // The same assessment for another record is another use, and lands.
        var other = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var elsewhere = await CommitHumanAsync(auth, scope, other.ExperienceId, Guid.NewGuid(), assessment, expectedRevision: 1);
        Assert.Equal(ExperienceStoreOutcome.Committed, elsewhere.Outcome);
    }

    [Fact]
    public async Task The_database_refuses_an_assessment_on_machine_evidence_or_the_empty_uuid()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());

        foreach (var (source, round, reviewer, assessment) in new (string, Guid?, string?, Guid)[]
        {
            ("Machine", Guid.NewGuid(), null, Guid.NewGuid()),
            ("Human", null, "someone", Guid.Empty),
        })
        {
            await using var command = _fixture.DataSource.CreateCommand(
                "INSERT INTO agent_experience.confidence_evidence (evidence_id, experience_id, event_id, kind, source, " +
                "run_id, verification_round_id, reviewer_identity, counted, rule_version, recorded_at, applied_revision, " +
                "applied_status, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, " +
                "new_supporting_validations, prior_contradictions, new_contradictions, assessment_id) " +
                "VALUES (gen_random_uuid(), @experience_id, NULL, 'Supporting', @source, gen_random_uuid(), " +
                "@round, @reviewer, false, '1.0.0', now(), 1, 'Validated', 2.0/3.0, 2.0/3.0, 1, 1, 0, 0, @assessment)");
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", lesson.ExperienceId));
            command.Parameters.Add(new NpgsqlParameter<string>("source", source));
            command.Parameters.Add(new NpgsqlParameter("round", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = round is { } id ? id : DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("reviewer", NpgsqlTypes.NpgsqlDbType.Text) { Value = reviewer ?? (object)DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter<Guid>("assessment", assessment));

            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        }

        Assert.Equal(0, await CountEvidenceAsync(lesson.ExperienceId));
    }

    [Fact]
    public async Task A_second_feedback_presenting_a_spent_token_lands_nothing_while_retrying_the_first_converges()
    {
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var lesson = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var reuse = await FinalizeAsync(auth, scope, Guid.NewGuid());
        var token = _issuer.Issue(auth, scope, reuse.RunId, ConfidenceEvidenceKind.Supporting, [lesson.ExperienceId]);

        var submission = new ExperienceReuseFeedback(
            FeedbackId: Guid.NewGuid(),
            RunId: reuse.RunId,
            Scope: scope,
            ExposedExperienceIds: [lesson.ExperienceId],
            RunOutcome: TaskVerificationStatus.Verified,
            Measure: new ReuseMeasure("tool-calls", 2),
            ObservedAt: ColumnTime,
            HumanAssessment: new HumanReuseAssessment(
                token.AssessmentId, ExperienceReuseBenefit.Improved, [lesson.ExperienceId], "it helped", ColumnTime, AssessmentToken: token.Token));

        var first = await _feedback.RecordAsync(auth, submission, CancellationToken.None);
        var retry = await _feedback.RecordAsync(auth, submission, CancellationToken.None);
        var reused = await _feedback.RecordAsync(auth, submission with { FeedbackId = Guid.NewGuid() }, CancellationToken.None);

        Assert.True(Assert.Single(first.Exposures).Counted);
        Assert.Equal(ExperienceReuseFeedbackOutcome.AlreadyRecorded, retry.Outcome);
        Assert.Equal(ExperienceExposureDisposition.EvidenceApplied, Assert.Single(retry.Exposures).Disposition);
        Assert.Equal(ExperienceExposureDisposition.Refused, Assert.Single(reused.Exposures).Disposition);

        Assert.Equal(1, await CountEvidenceAsync(lesson.ExperienceId));
        Assert.Equal(2, (await _store.GetAsync(auth, scope, lesson.ExperienceId, CancellationToken.None)).Record!.SupportingValidations);
    }

    private Task<ApplyConfidenceEvidenceResult> Apply(AuthorizationContext auth, ApplyConfidenceEvidenceRequest request) =>
        _lifecycle.ApplyEvidenceAsync(auth, request, CancellationToken.None);

    private Task<ExperienceLifecycleCommitResult> CommitHumanAsync(
        AuthorizationContext auth,
        Scope scope,
        Guid experienceId,
        Guid runId,
        Guid assessment,
        long expectedRevision,
        Guid? machineRound = null)
    {
        var prior = (int)expectedRevision;
        var update = new ConfidenceUpdate(
            EvidenceId: Guid.NewGuid(),
            Kind: ConfidenceEvidenceKind.Supporting,
            Source: machineRound is null ? ConfidenceEvidenceSource.Human : ConfidenceEvidenceSource.Machine,
            RunId: runId,
            VerificationRoundId: machineRound,
            ReviewerIdentity: machineRound is null ? auth.PrincipalId : null,
            RuleVersion: ReuseConfidenceHeuristic.RuleVersion,
            PriorReuseConfidence: ReuseConfidenceHeuristic.Score(prior, 0),
            NewReuseConfidence: ReuseConfidenceHeuristic.Score(prior + 1, 0),
            PriorSupportingValidations: prior,
            NewSupportingValidations: prior + 1,
            PriorContradictions: 0,
            NewContradictions: 0)
        {
            AssessmentId = assessment,
        };

        return _store.CommitLifecycleEventAsync(
            auth,
            scope,
            new LifecycleEvent(
                EventId: Guid.NewGuid(),
                ExperienceRecordId: experienceId,
                PriorStatus: ExperienceStatus.Validated,
                CurrentStatus: ExperienceStatus.Validated,
                Reason: "reviewed",
                Producer: "tests",
                OccurredAt: ColumnTime,
                ExpectedRevision: expectedRevision,
                ReplacementExperienceId: null,
                Confidence: update),
            CancellationToken.None);
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

    private static ApplyConfidenceEvidenceRequest Human(Scope scope, Guid experienceId, Guid runId, string? token) =>
        Machine(scope, experienceId, runId, Guid.Empty) with
        {
            Source = ConfidenceEvidenceSource.Human,
            VerificationRoundId = null,
            AssessmentToken = token,
        };

    /// <summary>Captures a run in <paramref name="scope"/> and finalizes it for real, with or without a closed round.</summary>
    private async Task<(Guid RunId, Guid ExperienceId, Guid? RoundId)> FinalizeAsync(AuthorizationContext auth, Scope scope, Guid? closedRound)
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
        Assert.Equal(AppendAttemptOutcome.Recorded, (await _capture.AppendAttemptAsync(
            runId, new AppendAttemptRequest(Guid.NewGuid(), PayloadTime, TimeSpan.FromSeconds(1), [], "done", null))).Outcome);
        Assert.Equal(CompleteRunOutcome.Recorded, (await _capture.CompleteRunAsync(
            runId, Guid.NewGuid(), RunExecutionStatus.Completed, PayloadTime.AddMinutes(1))).Outcome);

        var result = await _finalization.FinalizeAsync(
            new FinalizeExperienceRequest(
                RunId: runId,
                Authorization: auth,
                ClosedRound: closedRound is { } round ? new ClosedVerificationRound(round, ArtifactRevision) : null,
                RequiredChecks: [new RequiredCheck("tests", "TestResult")],
                Evidence: closedRound is { } evidenceRound
                    ? [new Evidence(Guid.NewGuid(), evidenceRound, ArtifactRevision, "tests", "TestResult", CheckResult.Pass, "ci", null, PayloadTime)]
                    : [],
                CurrentArtifactRevision: ArtifactRevision,
                StorageDecision: StorageDecision.Permit,
                FinalizedAt: ColumnTime),
            CancellationToken.None);

        Assert.True(result.IsDurable);
        return (runId, result.ExperienceId!.Value, closedRound);
    }

    private async Task<bool> PayloadHasClosedRoundAsync(Guid experienceId)
    {
        if (!EncryptionMode.IsOn)
        {
            await using var command = _fixture.DataSource.CreateCommand(
                "SELECT payload ? 'closedRoundId' FROM agent_experience.experience_records WHERE experience_id = @id");
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
            return (bool)(await command.ExecuteScalarAsync())!;
        }

        // Encrypted mode: the payload is sealed, so the stored JSON is the one inside the seal. Opened here with
        // the suite's key store to make the same byte-level assertion about what was written.
        await using var sealedRead = _fixture.DataSource.CreateCommand(
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
        using var payload = System.Text.Json.JsonDocument.Parse(payloadJson);
        return payload.RootElement.TryGetProperty("closedRoundId", out _);
    }

    private async Task<Guid?> ReadAssessmentAsync(Guid evidenceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT assessment_id FROM agent_experience.confidence_evidence WHERE evidence_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", evidenceId));
        return await command.ExecuteScalarAsync() is Guid id ? id : null;
    }

    private async Task<long> CountEvidenceAsync(Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.confidence_evidence WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
