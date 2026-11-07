using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Feedback;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.17's PostgreSQL reuse-feedback ledger: <c>sql/reuse-feedback.sql</c>, the .NET
/// <see cref="PostgresExperienceReuseFeedbackStore"/>'s own statements (generated from its source, never written by hand),
/// and the <c>reuse-feedback-sql</c> family: submissions recorded through the .NET store against a migrated schema, and
/// the two ledger tables they leave.
/// </summary>
internal static class ReuseFeedbackSqlContract
{
    public const string File = "sql/reuse-feedback.sql";
    public const string Family = "reuse-feedback-sql";

    private const int F = 0x1172;

    private static readonly Scope S = Fixed.Scope;
    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };
    private static readonly DateTimeOffset Clock = Fixed.T0.AddHours(4);
    private static readonly DateTimeOffset Observed = Fixed.T0.AddMinutes(30);
    private static readonly Guid Run = Fixed.Id(F, 0x5100);
    private static readonly Guid Round = Fixed.Id(F, 0x5200);

    /// <summary>Every column of <c>agent_experience.reuse_feedback</c> the store writes, <c>recorded_at</c> last.</summary>
    public static IReadOnlyList<string> SubmissionColumns { get; } =
        ["feedback_id", .. Store<string>("SubmissionColumns").Split(',').Select(column => column.Trim()), "recorded_at"];

    /// <summary>Every column of <c>agent_experience.reuse_feedback_exposures</c>.</summary>
    public static IReadOnlyList<string> ExposureColumns { get; } =
        ["feedback_id", "experience_id", "ordinal", "attributed", "evidence_id", "rationale_sealed"];

    // ---- sql/reuse-feedback.sql ------------------------------------------------------------------------------------

    /// <summary>The statements, by name, in file order: the .NET store's text, every one.</summary>
    public static IReadOnlyList<(string Name, string Sql)> Statements { get; } =
    [
        ("insert-submission", Store<string>("InsertSubmissionSql")),
        ("select-submission", Store<string>("SelectSubmissionSql")),
        ("select-exposures", Store<string>("SelectExposuresSql")),
        ("select-exposed-record-state", Store<string>("SelectExposedRecordStateSql")),
        ("insert-exposure", Store<string>("InsertExposureSql")),
    ];

    private const string Header = """
        -- contract 1.17: the reuse-feedback ledger, exactly as the .NET PostgreSQL store (PostgresExperienceReuseFeedbackStore)
        -- records one submission (RecordAsync). Every statement is generated from the .NET store's source and checked against
        -- it on every build. Each starts at its "-- statement:" line and runs to the next one (or the end).
        --
        -- Parameters are named as in the statements: @tenant_id ... @user_id are the submission's scope (NULL for an unset
        -- optional field); outcomes, benefits and attribution sources are bound as their member names (text); instants are
        -- truncated to whole microseconds in UTC; @recorded_at is the store's own clock; @evidence_ids is a uuid[] (NULL when
        -- there are none); @measure_value a double precision; @experience_ids a uuid[]; @ordinal an integer;
        -- @rationale_sealed is NULL (a port holds no key store: it writes plaintext, as the .NET store does without
        -- encryption). Every transaction first runs declare-bounds.sql for the authorization's bounds. A submission is
        -- validated, then authorized, before any statement runs (README-1.17.md): Invalid and Denied touch nothing.
        --
        -- RECORD. The exposures are first put in experience-ID order (the order a lowercase GUID's text sorts in); the caller's
        -- own order is kept only to name an erased exposure. One transaction at READ COMMITTED (pinned, never inherited):
        --  1. "insert-submission". When it returns no row the feedback ID is taken: "select-submission" (no scope predicate:
        --     the key is global, and row-level security hides another scope's row) and, when it returns a row,
        --     "select-exposures" (in ordinal order), in this transaction, which is then rolled back. No row is Conflict with no
        --     submission. Otherwise the stored submission (its scope columns as the scope, evidence_ids NULL as none, its
        --     exposures in ordinal order) is compared with the one in hand: the run, the scope, the outcome, the claimed and
        --     established benefit, the attribution source, reviewer, evaluator, round, assessment and rationale (exactly; NULL
        --     equals NULL), the evidence IDs in order, attributed_at and observed_at against the submitted ones truncated, the
        --     measure's kind and value (exactly) and the trial label, and the exposures (each ID, flag and evidence ID) in
        --     order. Equal is AlreadyRecorded, with the stored submission; anything else Conflict, with the stored submission
        --     when the authorization permits its scope and with none otherwise.
        --  2. "select-exposed-record-state" with @experience_ids the exposed IDs, in the submission's scope. A row whose
        --     deleted_at is set is an erased record: when there is any, roll back, and the outcome is Invalid with one error
        --     per exposure naming one, in the caller's order: path "Exposures[i].ExperienceId", i the exposure's position as
        --     the caller listed it, message "names an Experience Record that has been erased; nothing may be recorded against
        --     it again.". A record that does not exist, or lies in another scope, is not refused.
        --  3. "insert-exposure" for each exposure, in experience-ID order, @ordinal its position from 0.
        --  4. Commit: Recorded, with the submission as given, its exposures in experience-ID order (its instants as given).
        -- Any other database error is a store failure, never an outcome.
        --
        -- The application role needs only the 1.0 privileges (application-role-privileges.sql), which already grant SELECT and
        -- INSERT on both ledger tables; the FOR KEY SHARE lock on the exposed records is covered by its UPDATE grant on the
        -- record's lifecycle columns.
        """;

    /// <summary>The whole of <c>sql/reuse-feedback.sql</c>.</summary>
    public static string Render()
    {
        var text = new StringBuilder(Header.Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
        foreach (var (name, sql) in Statements)
        {
            text.Append("-- statement: ").Append(name).Append('\n').Append(sql.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()).Append('\n');
        }

        return text.ToString();
    }

    private static T Store<T>(string name)
    {
        var field = typeof(PostgresExperienceReuseFeedbackStore).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"PostgresExperienceReuseFeedbackStore has no {name}.");
        return (T)(field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null))!;
    }

    // ---- the reuse-feedback-sql family -----------------------------------------------------------------------------

    /// <summary>Every <c>reuse-feedback-sql</c> case, by directory name: its seed records and its steps.</summary>
    public static IReadOnlyList<(string Name, JsonObject Seed, JsonObject Steps)> Cases { get; } =
    [
        Case("record-replay-and-conflict", RecordReplayAndConflict()),
        Case("comparative-and-unattributed", ComparativeAndUnattributed()),
        Case("erased-exposure", ErasedExposure()),
    ];

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) RecordReplayAndConflict()
    {
        var (a, b) = (Fixed.Id(F, 0x10), Fixed.Id(F, 0x11));
        var feedbackId = Fixed.Id(F, 0xf001);
        var human = Submission(feedbackId, [new(b, true, ExperienceReuseFeedbackService.EvidenceIdFor(feedbackId, b)), new(a, false, null)]) with
        {
            Benefit = ExperienceReuseBenefit.Improved,
            ClaimedBenefit = ExperienceReuseBenefit.Improved,
            AttributionSource = ReuseAttributionSource.HumanAssessment,
            ReviewerIdentity = "reviewer-ü",
            AssessmentId = Fixed.Id(F, 0xa001),
            VerificationRoundId = Round,
            Rationale = "The reviewer saw the lesson applied.",
            AttributedAt = Observed.AddTicks(1_234_567),
            ObservedAt = Observed.AddTicks(7_654_321),
            TrialLabel = "memory-enabled",
        };
        return (
            "A human-attributed submission is written as one row and one row per exposure, in experience-ID order with dense " +
            "ordinals, its instants truncated to whole microseconds and recorded_at the store's clock. The same submission " +
            "with its exposures in another order, or an instant differing below a microsecond, is AlreadyRecorded with the " +
            "stored one; a field changed is a Conflict with the stored one; the same ID from another scope's caller, which " +
            "cannot see the stored row, a Conflict with none. A malformed submission is Invalid and a scope the authorization " +
            "does not permit Denied, before any statement runs.",
            [],
            [
                Record(S, human),
                Record(S, human with { Exposures = [.. human.Exposures.Reverse()] }),
                Record(S, human with { ObservedAt = human.ObservedAt.AddTicks(2), AttributedAt = human.AttributedAt!.Value.AddTicks(-3) }),
                Record(S, human with { Measure = new ReuseMeasure("tool-calls", 7.5) }),
                Record(OtherAgent, human with { Scope = OtherAgent }),
                Record(S, human with { FeedbackId = Guid.Empty, Rationale = null }),
                Record(OtherAgent, human with { FeedbackId = Fixed.Id(F, 0xf002) }),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) ComparativeAndUnattributed()
    {
        var (a, b, c) = (Fixed.Id(F, 0x20), Fixed.Id(F, 0x21), Fixed.Id(F, 0x22));
        var comparativeId = Fixed.Id(F, 0xf011);
        var comparative = Submission(comparativeId, [new(c, true, ExperienceReuseFeedbackService.EvidenceIdFor(comparativeId, c)), new(a, true, ExperienceReuseFeedbackService.EvidenceIdFor(comparativeId, a)), new(b, false, null)]) with
        {
            Benefit = ExperienceReuseBenefit.Harmed,
            AttributionSource = ReuseAttributionSource.ComparativeEvaluation,
            EvaluatorId = "comparative-evaluator/2.0",
            VerificationRoundId = Round,
            Rationale = "Runs with the lesson needed more tool calls.",
            EvidenceIds = [Fixed.Id(F, 0xe002), Fixed.Id(F, 0xe001)],
            AttributedAt = Observed.AddMinutes(10),
            RunOutcome = TaskVerificationStatus.Failed,
            Measure = new ReuseMeasure("tool-calls", 12),
        };
        var unattributed = Submission(Fixed.Id(F, 0xf012), [new(a, false, null)]) with
        {
            ClaimedBenefit = ExperienceReuseBenefit.Harmed,
            RunOutcome = TaskVerificationStatus.Unknown,
            Measure = new ReuseMeasure("wall-clock-ms", -0.125),
            TrialLabel = "memory-disabled",
        };
        return (
            "A comparative submission stores its evaluator, round and evidence IDs (in the order given); an unattributed one " +
            "stores benefit Unknown, source None and every attribution column NULL, the caller's claim beside it. Each is " +
            "replayed by resubmitting it.",
            [],
            [
                Record(S, comparative),
                Record(S, unattributed),
                Record(S, comparative with { Exposures = [.. comparative.Exposures.Reverse()] }),
                Record(S, unattributed with { EvidenceIds = [] }),
                Record(S, comparative with { EvidenceIds = [Fixed.Id(F, 0xe001), Fixed.Id(F, 0xe002)] }),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) ErasedExposure()
    {
        var a = Seed(0x30, S);
        var b = Seed(0x31, S);
        var foreign = Seed(0x32, OtherAgent);
        var missing = Fixed.Id(F, 0x3f);
        return (
            "The ledger reads an Experience Record for one reason: a submission naming a record erased in its own scope is " +
            "Invalid, naming the exposure by the position the caller gave it, and writes nothing, not even the submission " +
            "row. A record erased in another scope is invisible to that read, and one that never existed is recorded like any " +
            "other: no foreign key.",
            [a, b, foreign],
            [
                Erase(a.ExperienceId, S, Clock.AddMinutes(-30)),
                Erase(foreign.ExperienceId, OtherAgent, Clock.AddMinutes(-30)),
                Record(S, Submission(Fixed.Id(F, 0xf021), [new(b.ExperienceId, false, null), new(missing, false, null), new(a.ExperienceId, false, null)])),
                Record(S, Submission(Fixed.Id(F, 0xf022), [new(foreign.ExperienceId, false, null), new(b.ExperienceId, false, null), new(missing, false, null)])),
            ]);
    }

    // ---- step building ---------------------------------------------------------------------------------------------

    private static (string Name, JsonObject Seed, JsonObject Steps) Case(
        string name,
        (string Description, IReadOnlyList<ExperienceRecord> Records, IReadOnlyList<JsonObject> Steps) definition)
    {
        var steps = new JsonArray();
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            step["clock"] = ContractJson.Timestamp(Clock.AddMinutes(i).AddTicks(i * 3));
            steps.Add(step);
        }

        return (
            name,
            new JsonObject { ["records"] = SearchContract.ToSeedJson(definition.Records) },
            new JsonObject { ["description"] = definition.Description, ["steps"] = steps });
    }

    private static ExperienceRecord Seed(int n, Scope scope) =>
        SearchContract.Seed(F, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", scope: scope);

    private static RecordedExperienceReuseFeedback Submission(Guid feedbackId, IReadOnlyList<ExperienceReuseExposure> exposures) => new(
        feedbackId,
        Run,
        S,
        TaskVerificationStatus.Verified,
        ExperienceReuseBenefit.Unknown,
        ExperienceReuseBenefit.Unknown,
        ReuseAttributionSource.None,
        ReviewerIdentity: null,
        EvaluatorId: null,
        VerificationRoundId: null,
        AssessmentId: null,
        Rationale: null,
        EvidenceIds: [],
        AttributedAt: null,
        new ReuseMeasure("tool-calls", 7),
        TrialLabel: null,
        Observed,
        exposures);

    private static JsonObject Record(Scope authorization, RecordedExperienceReuseFeedback feedback) => new()
    {
        ["record"] = new JsonObject
        {
            ["authorization"] = LifecycleContract.Auth(authorization),
            ["feedback"] = ReuseFeedbackContract.RecordedJson(feedback),
        },
    };

    private static JsonObject Erase(Guid experienceId, Scope scope, DateTimeOffset deletedAt) => new()
    {
        ["erase"] = new JsonObject
        {
            ["experienceId"] = LifecycleContract.D(experienceId),
            ["scope"] = LifecycleContract.ScopeJson(scope),
            ["deletedAt"] = ContractJson.Timestamp(deletedAt),
        },
    };
}
