using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentExperience.Storage.Conformance;

/// <summary>
/// Valid inputs for the conformance suite, built from the ports' own types only. Every test takes a fresh tenant
/// from <see cref="NewTenant"/>, so tests sharing one store (one database, one container) never see each other's
/// records and stay independent.
/// </summary>
public static class ConformanceData
{
    /// <summary>
    /// A whole-second UTC instant, safely in the past: storable without truncation by any store, and earlier than
    /// any clock a store stamps a change with.
    /// </summary>
    public static readonly DateTimeOffset Time = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A tenant no other test uses.</summary>
    public static string NewTenant() => "conformance-" + Guid.NewGuid().ToString("N");

    /// <summary>A scope in <paramref name="tenant"/>; the optional fields vary it without leaving the tenant.</summary>
    public static Scope Scope(string tenant, string project = "project-1", string? team = null) =>
        new(tenant, "app-1", project, team, AgentId: null, UserId: null);

    /// <summary>A host-established authorization for the whole of <paramref name="tenant"/>.</summary>
    public static AuthorizationContext Authorize(string tenant) =>
        new(tenant, "conformance-principal", ["experience:write"], Time);

    /// <summary>A valid record with no attempts and no confidence evidence; a reflection only when a lesson is given.</summary>
    public static ExperienceRecord Record(
        Scope scope,
        ExperienceStatus status = ExperienceStatus.Candidate,
        Guid? id = null,
        string taskId = "conformance-task",
        string? summary = null,
        string? lesson = null,
        double confidence = 0d,
        DateTimeOffset? createdAt = null)
    {
        var runId = Guid.NewGuid();
        return new ExperienceRecord(
            ExperienceId: id ?? Guid.NewGuid(),
            SourceRunId: runId,
            Scope: scope,
            TaskId: taskId,
            TaskSummary: summary,
            Attempts: [],
            Outcome: new Outcome(TaskVerificationStatus.Unknown, [], null, Time),
            CompletionScore: 0,
            Reflection: lesson is null
                ? null
                : new Reflection(Guid.NewGuid(), runId, lesson, [], [], [], [], null, [], TaskVerificationStatus.Verified, 1, "v1", "conformance", Time),
            Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("conformance", null, Time, null),
            Status: status,
            ReuseConfidence: confidence,
            SupportingValidations: 0,
            Contradictions: 0,
            Revision: 0,
            CreatedAt: createdAt ?? Time,
            UpdatedAt: createdAt ?? Time);
    }

    /// <summary>
    /// A record with every part populated: attempts with tool calls whose arguments nest objects and lists,
    /// verification evidence, a reflection with every list filled, environment metadata, provenance with an
    /// exposure, a closed round, and confidence counters its score agrees with. Its reflection is model-authored,
    /// so the non-default authorship round-trips too (a record from <see cref="Record"/> keeps the default). What a
    /// store returns for it must be the same record.
    /// </summary>
    public static ExperienceRecord FullRecord(Scope scope, Guid? id = null)
    {
        var evidenceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        return new ExperienceRecord(
            ExperienceId: id ?? Guid.NewGuid(),
            SourceRunId: runId,
            Scope: scope,
            TaskId: "support-ticket-resolution",
            TaskSummary: "Resolve refund ticket",
            Attempts:
            [
                new Attempt(
                    Guid.NewGuid(),
                    0,
                    Time,
                    TimeSpan.FromMilliseconds(1500),
                    [
                        new ToolCallRecord(
                            Guid.NewGuid(),
                            0,
                            "search_docs",
                            new Dictionary<string, object?>
                            {
                                ["query"] = "refund policy",
                                ["limit"] = 3L,
                                ["threshold"] = 0.75,
                                ["exact"] = true,
                                ["cursor"] = null,
                                ["filters"] = new Dictionary<string, object?> { ["lang"] = "en", ["tags"] = new List<object?> { "a", 2L, false } },
                            },
                            Time.AddSeconds(1),
                            TimeSpan.FromMilliseconds(250),
                            "3 documents",
                            null),
                        new ToolCallRecord(Guid.NewGuid(), 1, "update_ticket", new Dictionary<string, object?>(), Time.AddSeconds(2), TimeSpan.Zero, null, "System.TimeoutException"),
                    ],
                    null,
                    "could not update ticket"),
                new Attempt(Guid.NewGuid(), 1, Time.AddSeconds(3), TimeSpan.FromSeconds(2), [], "ticket updated", null),
            ],
            Outcome: new Outcome(
                TaskVerificationStatus.Verified,
                [new Evidence(evidenceId, Guid.NewGuid(), "rev-7", "unit-tests-pass", "TestResult", CheckResult.Pass, "ci", "42 of 42 passed", Time)],
                "all required checks passed",
                Time.AddMinutes(1)),
            CompletionScore: 1,
            Reflection: new Reflection(
                Guid.NewGuid(),
                runId,
                "Retry the ticket update after the lock clears.",
                ["retry after lock"],
                ["immediate update"],
                ["ticket system reachable"],
                ["lock duration unknown"],
                "Use for ticket-lock failures only.",
                [evidenceId],
                TaskVerificationStatus.Verified,
                1,
                "v1",
                "template-reflector/1.0",
                Time.AddMinutes(2))
            {
                Authorship = ReflectionAuthorship.Model,
            },
            Environment: new EnvironmentFingerprint("worker-01", "10.0.0", "linux-x64", "1.2.3", new Dictionary<string, string> { ["region"] = "us-east", ["az"] = "1b", ["a"] = "x" }),
            Provenance: new Provenance("conformance-adapter", "1.0.0", Time, "trace-123") { ExposedTo = [new RunExposure(Guid.NewGuid(), 2)] },
            Status: ExperienceStatus.Validated,
            // The confidence its own counters explain: (1 + 4) / (2 + 4 + 1).
            ReuseConfidence: 5d / 7d,
            SupportingValidations: 4,
            Contradictions: 1,
            Revision: 0,
            CreatedAt: Time,
            UpdatedAt: Time)
        {
            ClosedRoundId = Guid.NewGuid(),
            Origin = ExperienceRecordOrigin.Finalized,
            // A store persists a provenance signature as given and never checks it: it holds no key.
            ProvenanceSignature = new ExperienceProvenanceSignature(
                "conformance-key-1", ExperienceProvenanceSignature.HmacSha256, Enumerable.Range(0, 32).Select(i => (byte)(i * 11)).ToArray()),
        };
    }

    /// <summary>A lifecycle event for <paramref name="recordId"/>, with a fresh event ID unless one is given.</summary>
    public static LifecycleEvent Event(
        Guid recordId,
        ExperienceStatus? prior,
        ExperienceStatus current,
        long expectedRevision,
        Guid? eventId = null,
        string reason = "conformance transition",
        Guid? replacement = null) => new(
            EventId: eventId ?? Guid.NewGuid(),
            ExperienceRecordId: recordId,
            PriorStatus: prior,
            CurrentStatus: current,
            Reason: reason,
            Producer: "conformance",
            OccurredAt: Time,
            ExpectedRevision: expectedRevision,
            ReplacementExperienceId: replacement);

    /// <summary>
    /// A human-sourced supporting confidence update for a record with no evidence yet: 0 -> 1 supporting, 0 -> 2/3.
    /// </summary>
    public static ConfidenceUpdate SupportingConfidence() => new(
        EvidenceId: Guid.NewGuid(),
        Kind: ConfidenceEvidenceKind.Supporting,
        Source: ConfidenceEvidenceSource.Human,
        RunId: Guid.NewGuid(),
        VerificationRoundId: null,
        ReviewerIdentity: "conformance-principal",
        RuleVersion: "v1",
        PriorReuseConfidence: 0d,
        NewReuseConfidence: 2d / 3d,
        PriorSupportingValidations: 0,
        NewSupportingValidations: 1,
        PriorContradictions: 0,
        NewContradictions: 0);

    /// <summary>An unattributed reuse-feedback submission, in the shape the ledger stores it (exposures ordered by ID).</summary>
    public static RecordedExperienceReuseFeedback Feedback(Scope scope, IReadOnlyList<Guid> exposed, Guid? feedbackId = null, string? trialLabel = "memory-enabled") => new(
        FeedbackId: feedbackId ?? Guid.NewGuid(),
        RunId: Guid.NewGuid(),
        Scope: scope,
        RunOutcome: TaskVerificationStatus.Verified,
        ClaimedBenefit: ExperienceReuseBenefit.Unknown,
        Benefit: ExperienceReuseBenefit.Unknown,
        AttributionSource: ReuseAttributionSource.None,
        ReviewerIdentity: null,
        EvaluatorId: null,
        VerificationRoundId: null,
        AssessmentId: null,
        Rationale: null,
        EvidenceIds: [],
        AttributedAt: null,
        Measure: new ReuseMeasure("task-success", 1),
        TrialLabel: trialLabel,
        ObservedAt: Time,
        Exposures: [.. exposed.Order().Select(id => new ExperienceReuseExposure(id, Attributed: false, EvidenceId: null))]);

    /// <summary>
    /// A comparative-evaluation submission: every attribution field set, one attributed exposure (with its derived
    /// evidence ID) and one unattributed one.
    /// </summary>
    public static RecordedExperienceReuseFeedback AttributedFeedback(Scope scope)
    {
        var exposed = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        return Feedback(scope, exposed) with
        {
            ClaimedBenefit = ExperienceReuseBenefit.Improved,
            Benefit = ExperienceReuseBenefit.Improved,
            AttributionSource = ReuseAttributionSource.ComparativeEvaluation,
            EvaluatorId = "conformance-evaluator",
            VerificationRoundId = Guid.NewGuid(),
            Rationale = "the memory-enabled run passed where the baseline did not",
            EvidenceIds = [Guid.NewGuid()],
            AttributedAt = Time.AddMinutes(5),
            Exposures =
            [
                new ExperienceReuseExposure(exposed[0], Attributed: true, EvidenceId: Guid.NewGuid()),
                new ExperienceReuseExposure(exposed[1], Attributed: false, EvidenceId: null),
            ],
        };
    }

    /// <summary>
    /// A human-assessment submission: a reviewer, the assessment it came out of, a rationale and an attribution time,
    /// with one attributed exposure (with its derived evidence ID) and one unattributed one.
    /// </summary>
    public static RecordedExperienceReuseFeedback HumanAssessedFeedback(Scope scope)
    {
        var exposed = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        return Feedback(scope, exposed) with
        {
            ClaimedBenefit = ExperienceReuseBenefit.Improved,
            Benefit = ExperienceReuseBenefit.Harmed,
            AttributionSource = ReuseAttributionSource.HumanAssessment,
            ReviewerIdentity = "conformance-reviewer",
            AssessmentId = Guid.NewGuid(),
            Rationale = "the reviewer judged that the injected lesson misled the run",
            AttributedAt = Time.AddMinutes(10),
            Exposures =
            [
                new ExperienceReuseExposure(exposed[0], Attributed: true, EvidenceId: Guid.NewGuid()),
                new ExperienceReuseExposure(exposed[1], Attributed: false, EvidenceId: null),
            ],
        };
    }

    /// <summary>
    /// A value's canonical JSON: object keys sorted recursively, so two values compare equal however their
    /// dictionaries are ordered, and a record's collections compare by content rather than by reference.
    /// </summary>
    public static string Json<T>(T value) => Sort(JsonSerializer.SerializeToNode(value))?.ToJsonString() ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sort)]),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
}
