using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.16's PostgreSQL lifecycle: <c>sql/lifecycle.sql</c>, the .NET PostgreSQL store's own lifecycle statements
/// (generated from <see cref="PostgresExperienceRecordStore"/>'s source, never written by hand), and the
/// <c>lifecycle-sql</c> family: store-level steps (commit an event, read a history page, check a supersession, read a
/// record, erase one) run through the .NET store against a migrated schema, and the tables they leave.
/// </summary>
internal static class LifecycleSqlContract
{
    public const string File = "sql/lifecycle.sql";
    public const string Family = "lifecycle-sql";

    /// <summary>The value an expected event's or evidence row's column holds where it is the database's own clock.</summary>
    public const string DatabaseClock = "database-clock";

    private const int F = 0x1162;

    private static readonly Scope S = Fixed.Scope;
    private static readonly Scope OtherAgent = Fixed.Scope with { AgentId = "other-agent" };
    private static readonly DateTimeOffset Clock = Fixed.T0.AddHours(2);

    /// <summary>The record columns the expected tables hold: everything a lifecycle write can move, and the tombstone marker.</summary>
    public static IReadOnlyList<string> RecordColumns { get; } =
        ["experience_id", "status", "reuse_confidence", "supporting_validations", "contradictions", "revision", "updated_at", "deleted_at"];

    /// <summary>Every column of <c>agent_experience.lifecycle_events</c> the store writes, in its <c>EventColumns</c> order.</summary>
    public static IReadOnlyList<string> EventColumns { get; } = Columns(ExperienceRecordSql.EventColumns);

    /// <summary>Every column of <c>agent_experience.confidence_evidence</c> the store writes, then the generated key.</summary>
    public static IReadOnlyList<string> EvidenceColumns { get; } = [.. Columns(Store<string>("EvidenceColumns")), "independence_key"];

    // ---- sql/lifecycle.sql ----------------------------------------------------------------------------------------

    /// <summary>The statements, by name, in file order: the .NET store's text, except the one a port alone runs.</summary>
    public static IReadOnlyList<(string Name, string Sql)> Statements { get; } =
    [
        ("read-record", ExperienceRecordSql.GetExactSql),
        ("sealed-record", SealedRecordSql),
        ("savepoint", $"SAVEPOINT {Store<string>("EvidenceSavepoint")}"),
        ("rollback-to-savepoint", $"ROLLBACK TO SAVEPOINT {Store<string>("EvidenceSavepoint")}"),
        ("release-savepoint", $"RELEASE SAVEPOINT {Store<string>("EvidenceSavepoint")}"),
        ("insert-evidence", Store<string>("InsertEvidenceSql")),
        ("recorded-only-key-taken", Store<string>("RecordedOnlyKeyTakenSql")),
        ("evidence-id-taken", Store<string>("EvidenceIdTakenSql")),
        ("select-evidence", Store<string>("SelectEvidenceSql")),
        ("insert-event", Store<string>("InsertEventSql")),
        ("select-event", Store<string>("SelectEventSql")),
        ("lock-supersession-rows", Store<string>("LockSupersessionRowsSql")),
        ("check-supersession", Store<string>("SupersessionCheckSql").Trim()),
        ("update-projection", Store<string>("UpdateProjectionSql")),
        ("update-projection-with-confidence", Store<string>("UpdateProjectionWithConfidenceSql")),
        ("update-projection-recorded-only", Store<string>("UpdateProjectionRecordedOnlySql")),
        ("select-revision-and-status", Store<string>("SelectRevisionAndStatusSql")),
        ("lock-revision-and-status", Store<string>("LockRevisionAndStatusSql")),
        ("read-history", Store<string>("HistorySql")),
    ];

    /// <summary>
    /// The one statement that is not the .NET store's: a port holds no key store, so it cannot write a sealed record's
    /// events the way the .NET store in crypto-shredding mode does, and refuses to commit against one (contract 1.0: a
    /// sealed row is skipped).
    /// </summary>
    private const string SealedRecordSql =
        $"SELECT EXISTS (SELECT 1 FROM {ExperienceRecordSql.Table} WHERE experience_id = @experience_id AND " +
        $"{ExperienceRecordSql.ScopePredicate} AND {ExperienceRecordSql.LivePredicate} AND payload_version <> 1)";

    private const string Header = """
        -- contract 1.16: a stored record's lifecycle, exactly as the .NET PostgreSQL store (PostgresExperienceRecordStore)
        -- runs it: commit one lifecycle event (CommitLifecycleEventAsync), read one page of a history (GetHistoryAsync), check
        -- a supersession (CheckSupersessionAsync), and read one record in exactly its scope (the read the lifecycle service
        -- makes first). Every statement but "sealed-record" is generated from the .NET store's source and checked against it
        -- on every build. Each starts at its "-- statement:" line and runs to the next one (or the end).
        --
        -- Parameters are named as in the statements: @tenant_id ... @user_id are the request scope (NULL for an unset
        -- optional field); statuses, kinds, sources and admissions are bound as their member names (text); instants are
        -- truncated to whole microseconds in UTC; @recorded_at is the store's own clock, @applied_revision is
        -- @expected_revision + 1, @lock_ids is a uuid[], @limit an integer and @start_after_revision a bigint (NULL for none).
        -- @actor is the authorization's principal, NULL when it is blank. An event without a confidence update binds every
        -- confidence parameter NULL. Every transaction first runs declare-bounds.sql for the authorization's bounds. A request
        -- is validated, then authorized, before any statement runs (README-1.16.md): Invalid and Denied touch nothing.
        --
        -- COMMIT. One transaction at READ COMMITTED (pinned, never inherited):
        --  0. (A port only.) "sealed-record" true: roll back; NotFound.
        --  1. With a confidence update: "savepoint", then "insert-evidence" with @counted the update's own Counted (a new
        --     counter differs from its prior), @event_id the event's, @applied_status the event's current status. On a unique
        --     violation (SQLSTATE 23505) of
        --       ux_confidence_evidence_independence: "rollback-to-savepoint", then DUPLICATE;
        --       confidence_evidence_pkey: "rollback-to-savepoint", then EVIDENCE REPLAY, and roll back;
        --       ux_confidence_evidence_assessment: "rollback-to-savepoint", then "evidence-id-taken": true is EVIDENCE REPLAY,
        --       false is Conflict with the error (Confidence.AssessmentId, "this assessment has already landed evidence for
        --       this record under another evidence ID."); roll back either way.
        --     Otherwise, for an update that is not counted (host-trusted evidence recorded only): "recorded-only-key-taken"
        --     true is "rollback-to-savepoint", then DUPLICATE. Otherwise "release-savepoint".
        --  2. "insert-event". On a unique violation of lifecycle_events_pkey: roll back, then in a new transaction
        --     "select-event": no row, or a row of another record, or a row whose scope is not the request scope, is Conflict;
        --     otherwise EVENT REPLAY. On a unique violation of ix_lifecycle_events_record_revision: roll back, then STALE in a
        --     new transaction.
        --  3. With a replacement: "lock-supersession-rows" (@lock_ids the record and the replacement), then
        --     "check-supersession". It passes only when its first value is not NULL, its second (the replacement's status)
        --     is Validated or Reinforced, and its third (the cycle flag) is false. Otherwise roll back: ReplacementNotAllowed,
        --     with the second value as the current status when the first is not NULL (else none).
        --  4. "update-projection-with-confidence" for a counted update, "update-projection-recorded-only" for one not
        --     counted, "update-projection" for an event without one. On a serialization failure (40001) or a deadlock
        --     (40P01): roll back, then STALE in a new transaction. When it updates no row: "select-revision-and-status" in
        --     this transaction, then roll back: no row is NotFound; a tombstone (deleted_at set) is Deleted, with its
        --     revision; a revision other than @expected_revision is StaleRevision, with it; otherwise StatusMismatch, with
        --     the stored revision and the stored status as the current status.
        --  5. Commit: Committed, at @applied_revision, no current status, the update as submitted.
        --
        -- DUPLICATE, in the same transaction: "lock-revision-and-status": no row is NotFound; a tombstone Deleted (with its
        --  revision); a revision other than @expected_revision StaleRevision (with it); an event prior status that is set
        --  and is not the stored status StatusMismatch (with the stored revision and status); each rolled back. Otherwise
        --  "insert-evidence" with the update recorded only (its new score and counters set to its prior ones, @counted
        --  false), @event_id NULL, @applied_revision the stored revision and @applied_status the stored status; a violation
        --  of confidence_evidence_pkey or ux_confidence_evidence_assessment is handled as in step 1. Then
        --  "release-savepoint" and commit: Committed, with the stored revision, the stored status as the current status,
        --  and the update recorded only.
        -- EVIDENCE REPLAY: "select-evidence". No row (none, or one whose record is not in the request scope), or a row about
        --  another record, is Conflict. A row whose event_id is NULL or the event's, and whose kind, source, run, round,
        --  reviewer, rule version, detail and assessment equal the update's (exactly; NULL equals NULL), is Committed with
        --  the row's applied_revision, its applied_status as the current status, and the update with the row's prior and new
        --  score and counters and its admission. Anything else is Conflict.
        -- EVENT REPLAY: the stored event against the submitted one with its occurred_at truncated. A submitted update takes
        --  the stored one's admission and, when it is the same evidence (the same evidence ID), the stored prior and new
        --  score and counters, and the event then takes the stored current status. Every field then equal (the event and
        --  experience IDs, the statuses, reason, producer, occurred_at, expected revision, replacement, and every field of
        --  the update, or both none) is Committed with the stored applied_revision, no current status, and the stored
        --  update; anything else Conflict.
        -- STALE, in a new transaction: "select-revision-and-status": no row NotFound; a tombstone Deleted (with its revision);
        --  otherwise StaleRevision, with its revision.
        --
        -- HISTORY, in one transaction: "read-history". No row is NotFound; a row whose deleted_at is set is Deleted; (a port
        -- only) a payload_version other than 1 is NotFound; otherwise Found, with the revision column, the rows whose
        -- event_id is not NULL as the events (oldest first), and the last event's applied_revision as the next cursor (none
        -- for no events).
        -- SUPERSESSION CHECK, in one transaction: "check-supersession". A NULL first value is RecordNotFound; a NULL second
        -- value ReplacementNotFound; the cycle flag Cycle, with the replacement's status; otherwise Allowed, with it.
        -- READ, in one transaction: "read-record". No row is NotFound; deleted_at set is Deleted; (a port only) a
        -- payload_version other than 1 is NotFound; otherwise Found: the first eighteen columns are the stored record.
        --
        -- The application role needs only the 1.0 privileges (application-role-privileges.sql), which already grant INSERT
        -- on both logs and UPDATE of the record's lifecycle columns.
        """;

    /// <summary>The whole of <c>sql/lifecycle.sql</c>.</summary>
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
        var field = typeof(PostgresExperienceRecordStore).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"PostgresExperienceRecordStore has no {name}.");
        return (T)(field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null))!;
    }

    private static List<string> Columns(string list) => [.. list.Split(',').Select(column => column.Trim())];

    // ---- the lifecycle-sql family ---------------------------------------------------------------------------------

    /// <summary>Every <c>lifecycle-sql</c> case, by directory name: its seed records and its steps.</summary>
    public static IReadOnlyList<(string Name, JsonObject Seed, JsonObject Steps)> Cases { get; } =
    [
        Case("commit-replay-and-conflict", CommitReplayAndConflict()),
        Case("compare-and-set", CompareAndSet()),
        Case("scope-denied-invalid", ScopeDeniedInvalid()),
        Case("supersession-gate", SupersessionGate()),
        Case("confidence-counted-duplicate-and-replay", ConfidenceCountedDuplicateAndReplay()),
        Case("assessment-spent", AssessmentSpent()),
        Case("recorded-only", RecordedOnly()),
        Case("erased-record", ErasedRecord()),
        Case("history-paging", HistoryPaging()),
    ];

    private static (string Description, IReadOnlyList<ExperienceRecord> Records, IReadOnlyList<JsonObject> Steps) CommitReplayAndConflict()
    {
        var (a, b, foreign) = (Rec(1, ExperienceStatus.Validated), Rec(2, ExperienceStatus.Validated), Rec(3, ExperienceStatus.Validated, OtherAgent));
        var e1 = Fixed.Id(F, 0x1001);
        var at = Fixed.T0.AddTicks(1_234_567);
        return (
            "A committed event, then the same event ID again: identical (occurred_at equal once truncated) is the original " +
            "replayed, with the revision it produced; any field changed, another record, or another scope's request is a " +
            "Conflict. Nothing but the first writes anything.",
            [a, b, foreign],
            [
                Commit(S, Event(e1, a.ExperienceId, "Validated", "Reinforced", 1, at: at)),
                Commit(S, Event(e1, a.ExperienceId, "Validated", "Reinforced", 1, at: at.AddTicks(2))),
                Commit(S, Event(e1, a.ExperienceId, "Validated", "Reinforced", 1, reason: "another reason", at: at)),
                Commit(S, Event(e1, b.ExperienceId, "Validated", "Reinforced", 1, at: at)),
                Commit(OtherAgent, Event(e1, a.ExperienceId, "Validated", "Reinforced", 1, at: at)),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) CompareAndSet()
    {
        var a = Rec(0x10, ExperienceStatus.Validated);
        var candidate = Rec(0x11, ExperienceStatus.Candidate) with { Revision = 0, SupportingValidations = 0, ReuseConfidence = 0.5 };
        return (
            "The revision is compared and set: of two events decided against revision 1 the first commits, and the second " +
            "meets the event revision index (another event took revision 2) and is StaleRevision with the current revision; " +
            "one against a revision the record never had matches no row and is StaleRevision too; one against the right " +
            "revision from a status the record is not in is StatusMismatch with the stored status. A first event (no prior " +
            "status) may only record the status the record is in.",
            [a, candidate],
            [
                Commit(S, Event(Fixed.Id(F, 0x1101), a.ExperienceId, "Validated", "Reinforced", 1)),
                Commit(S, Event(Fixed.Id(F, 0x1102), a.ExperienceId, "Validated", "Stale", 1)),
                Commit(S, Event(Fixed.Id(F, 0x1103), a.ExperienceId, "Validated", "Stale", 7)),
                Commit(S, Event(Fixed.Id(F, 0x1104), a.ExperienceId, "Validated", "Stale", 2)),
                Commit(S, Event(Fixed.Id(F, 0x1105), candidate.ExperienceId, null, "Validated", 0)),
                Commit(S, Event(Fixed.Id(F, 0x1106), candidate.ExperienceId, null, "Candidate", 0)),
                Commit(S, Event(Fixed.Id(F, 0x1107), candidate.ExperienceId, "Candidate", "Validated", 1)),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) ScopeDeniedInvalid()
    {
        var a = Rec(0x20, ExperienceStatus.Validated);
        return (
            "A record in another scope is NotFound and a missing one too; a scope the authorization does not permit is Denied; " +
            "a malformed event is Invalid with every error, its replacement and confidence rules included. None runs a statement.",
            [a],
            [
                Commit(OtherAgent, Event(Fixed.Id(F, 0x2001), a.ExperienceId, "Validated", "Revoked", 1)),
                Commit(S, Event(Fixed.Id(F, 0x2002), Fixed.Id(F, 0x2f), "Validated", "Revoked", 1)),
                Commit(S, Event(Fixed.Id(F, 0x2003), a.ExperienceId, "Validated", "Revoked", 1), authorization: OtherAgent),
                Commit(S, Event(Guid.Empty, a.ExperienceId, "Validated", "Superseded", -1, reason: " ")),
                Commit(S, Event(Fixed.Id(F, 0x2004), a.ExperienceId, "Validated", "Revoked", 1, replacement: a.ExperienceId,
                    confidence: Update(Fixed.Id(F, 0x2101), "Supporting", "Machine", Fixed.Id(F, 0x5000), null, 2d / 3d, 0.75, 1, 2, 0, 0))),
                Commit(S, Event(Fixed.Id(F, 0x2005), a.ExperienceId, "Validated", "Validated", 1,
                    confidence: Update(Fixed.Id(F, 0x2102), "Supporting", "Human", Fixed.Id(F, 0x5000), Fixed.Id(F, 0x5001), 2d / 3d, 0.6, 1, 1, 0, 0, admission: "Verified"))),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) SupersessionGate()
    {
        var a = Rec(0x30, ExperienceStatus.Validated);
        var b = Rec(0x31, ExperienceStatus.Validated);
        var contested = Rec(0x32, ExperienceStatus.Contested);
        var foreign = Rec(0x33, ExperienceStatus.Validated, OtherAgent);
        var c = Rec(0x34, ExperienceStatus.Reinforced);
        Guid E(int n) => Fixed.Id(F, 0x3000 + n);
        return (
            "The supersession gate runs inside the commit, after the event insert and with both rows locked: a replacement " +
            "that does not exist or lies in another scope is ReplacementNotAllowed with no status, an ineligible one with its " +
            "status, and so is superseding a record that is not there. The store applies no transition table, so a record " +
            "can be moved back to Validated at the store; superseding its replacement by it then closes a cycle, refused with " +
            "the replacement's (eligible) status. Retrying the committed supersession replays it after its replacement moved on.",
            [a, b, contested, foreign, c],
            [
                Commit(S, Event(E(1), a.ExperienceId, "Validated", "Superseded", 1, replacement: Fixed.Id(F, 0x3f))),
                Commit(S, Event(E(2), a.ExperienceId, "Validated", "Superseded", 1, replacement: foreign.ExperienceId)),
                Commit(S, Event(E(3), a.ExperienceId, "Validated", "Superseded", 1, replacement: contested.ExperienceId)),
                Commit(S, Event(E(4), Fixed.Id(F, 0x3e), "Validated", "Superseded", 1, replacement: b.ExperienceId)),
                Commit(S, Event(E(5), a.ExperienceId, "Validated", "Superseded", 1, replacement: b.ExperienceId)),
                Commit(S, Event(E(6), a.ExperienceId, "Superseded", "Validated", 2)),
                Check(S, b.ExperienceId, a.ExperienceId),
                Commit(S, Event(E(7), b.ExperienceId, "Validated", "Superseded", 1, replacement: a.ExperienceId)),
                Commit(S, Event(E(8), b.ExperienceId, "Validated", "Superseded", 1, replacement: c.ExperienceId)),
                Commit(S, Event(E(5), a.ExperienceId, "Validated", "Superseded", 1, replacement: b.ExperienceId)),
                Check(S, a.ExperienceId, b.ExperienceId),
                Check(S, Fixed.Id(F, 0x3e), b.ExperienceId),
                Check(S, a.ExperienceId, foreign.ExperienceId),
                Check(S, a.ExperienceId, contested.ExperienceId),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) ConfidenceCountedDuplicateAndReplay()
    {
        var a = Rec(0x40, ExperienceStatus.Validated);
        var (run1, run2, round) = (Fixed.Id(F, 0x4a01), Fixed.Id(F, 0x4a02), Fixed.Id(F, 0x4b00));
        var (ev1, ev2, ev3) = (Fixed.Id(F, 0x4101), Fixed.Id(F, 0x4102), Fixed.Id(F, 0x4103));
        JsonObject Counted(Guid evidence, Guid run, string? detail = null) => Update(evidence, "Supporting", "Machine", run, round, 2d / 3d, 0.75, 1, 2, 0, 0, detail: detail);
        return (
            "Confidence rides the event: the evidence row goes in first under a savepoint and claims its independence key, then " +
            "the event and the counters. The same observation under a new evidence ID loses the key: one ledger row, not " +
            "counted, and nothing else -- no event, no counters, no revision, no updated_at -- reported with the record's " +
            "revision and status. A duplicate decided against a stale revision writes nothing. The same evidence ID with the " +
            "same content and event is the original replayed (the numbers it was stored with); with a fresh event ID, or any " +
            "content changed, it is a Conflict.",
            [a],
            [
                Commit(S, Event(Fixed.Id(F, 0x4001), a.ExperienceId, "Validated", "Validated", 1, confidence: Counted(ev1, run1))),
                Commit(S, Event(Fixed.Id(F, 0x4002), a.ExperienceId, "Validated", "Validated", 2, confidence: Update(ev2, "Supporting", "Machine", run1, round, 0.75, 0.8, 2, 3, 0, 0))),
                Commit(S, Event(Fixed.Id(F, 0x4003), a.ExperienceId, "Validated", "Validated", 1, confidence: Update(ev3, "Supporting", "Machine", run1, round, 0.75, 0.8, 2, 3, 0, 0))),
                Commit(S, Event(Fixed.Id(F, 0x4001), a.ExperienceId, "Validated", "Validated", 1, confidence: Counted(ev1, run1))),
                Commit(S, Event(Fixed.Id(F, 0x4004), a.ExperienceId, "Validated", "Validated", 2, confidence: Counted(ev1, run1))),
                Commit(S, Event(Fixed.Id(F, 0x4001), a.ExperienceId, "Validated", "Validated", 1, confidence: Counted(ev1, run1, detail: "changed"))),
                Commit(S, Event(Fixed.Id(F, 0x4005), a.ExperienceId, "Validated", "Contested", 2, confidence: Update(Fixed.Id(F, 0x4104), "Contradicting", "Machine", run2, round, 0.75, 0.6, 2, 2, 0, 1))),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) AssessmentSpent()
    {
        var a = Rec(0x50, ExperienceStatus.Validated);
        var run = Fixed.Id(F, 0x5a01);
        var assessment = Fixed.Id(F, 0x5c01);
        JsonObject Human(Guid evidence, string reviewer, double prior, double next, int s) =>
            Update(evidence, "Supporting", "Human", run, null, prior, next, s, s + 1, 0, 0, reviewer: reviewer, assessment: assessment, admission: "Verified");
        return (
            "An assessment is spent once per record: human evidence that presents it is counted, its assessment stored on the " +
            "ledger and the event; another evidence ID presenting it -- even another reviewer's, a fresh independence key -- " +
            "is a Conflict with an error on Confidence.AssessmentId, and nothing is written. Resubmitting the original is its " +
            "replay. (Verified admission here is only what the event carries: the store never derives one.)",
            [a],
            [
                Commit(S, Event(Fixed.Id(F, 0x5001), a.ExperienceId, "Validated", "Validated", 1, confidence: Human(Fixed.Id(F, 0x5101), "reviewer-1", 2d / 3d, 0.75, 1)), principal: "reviewer-1"),
                Commit(S, Event(Fixed.Id(F, 0x5002), a.ExperienceId, "Validated", "Validated", 2, confidence: Human(Fixed.Id(F, 0x5102), "reviewer-2", 0.75, 0.8, 2)), principal: "reviewer-2"),
                Commit(S, Event(Fixed.Id(F, 0x5001), a.ExperienceId, "Validated", "Validated", 1, confidence: Human(Fixed.Id(F, 0x5101), "reviewer-1", 2d / 3d, 0.75, 1)), principal: "reviewer-1"),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) RecordedOnly()
    {
        var a = Rec(0x60, ExperienceStatus.Validated);
        var (run1, run2, round) = (Fixed.Id(F, 0x6a01), Fixed.Id(F, 0x6a02), Fixed.Id(F, 0x6b00));
        JsonObject Recorded(Guid evidence, Guid run, string kind) =>
            Update(evidence, kind, "Machine", run, round, 2d / 3d, 2d / 3d, 1, 1, 0, 0, admission: "HostTrusted");
        return (
            "Host-trusted evidence recorded only rides an event that moves nothing but the revision: not the counters, not " +
            "the score, not the status, not updated_at. Another recorded-only submission for a key an event already recorded " +
            "is a duplicate (a ledger row only); counted evidence for that key still counts, since a recorded-only row claims " +
            "no key. Evidence that moves no counter is accepted only so: admitted HostTrusted, its score and status unmoved.",
            [a],
            [
                Commit(S, Event(Fixed.Id(F, 0x6001), a.ExperienceId, "Validated", "Validated", 1, confidence: Recorded(Fixed.Id(F, 0x6101), run1, "Contradicting"))),
                Commit(S, Event(Fixed.Id(F, 0x6002), a.ExperienceId, "Validated", "Validated", 2, confidence: Recorded(Fixed.Id(F, 0x6102), run1, "Supporting"))),
                Commit(S, Event(Fixed.Id(F, 0x6003), a.ExperienceId, "Validated", "Validated", 2, confidence: Update(Fixed.Id(F, 0x6103), "Supporting", "Machine", run1, round, 2d / 3d, 0.75, 1, 2, 0, 0, admission: "HostTrusted"))),
                Commit(S, Event(Fixed.Id(F, 0x6004), a.ExperienceId, "Validated", "Validated", 3, confidence: Update(Fixed.Id(F, 0x6104), "Supporting", "Machine", run2, round, 0.75, 0.75, 2, 2, 0, 0, admission: "Verified"))),
                Commit(S, Event(Fixed.Id(F, 0x6005), a.ExperienceId, "Validated", "Contested", 3, confidence: Update(Fixed.Id(F, 0x6105), "Contradicting", "Machine", run2, round, 0.75, 0.75, 2, 2, 0, 0, admission: "HostTrusted"))),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) ErasedRecord()
    {
        var (a, b) = (Rec(0x70, ExperienceStatus.Validated), Rec(0x71, ExperienceStatus.Validated));
        return (
            "Erasing a record removes its events and evidence and leaves a tombstone: a later commit against it is Deleted " +
            "(with the tombstone's revision), and so are its history and a read of it; the supersession check no longer finds it.",
            [a, b],
            [
                Commit(S, Event(Fixed.Id(F, 0x7001), a.ExperienceId, "Validated", "Validated", 1, confidence: Update(Fixed.Id(F, 0x7101), "Supporting", "Machine", Fixed.Id(F, 0x7a01), Fixed.Id(F, 0x7b00), 2d / 3d, 0.75, 1, 2, 0, 0))),
                Erase(a.ExperienceId, S, Clock.AddMinutes(30)),
                Commit(S, Event(Fixed.Id(F, 0x7002), a.ExperienceId, "Validated", "Revoked", 2)),
                Commit(S, Event(Fixed.Id(F, 0x7003), a.ExperienceId, "Validated", "Validated", 2, confidence: Update(Fixed.Id(F, 0x7102), "Supporting", "Machine", Fixed.Id(F, 0x7a01), Fixed.Id(F, 0x7b00), 0.75, 0.8, 2, 3, 0, 0))),
                History(S, a.ExperienceId, 100, null),
                Read(S, a.ExperienceId),
                Read(S, b.ExperienceId),
                Read(OtherAgent, b.ExperienceId),
                Read(S, Fixed.Id(F, 0x7f)),
                Check(S, b.ExperienceId, a.ExperienceId),
            ]);
    }

    private static (string, IReadOnlyList<ExperienceRecord>, IReadOnlyList<JsonObject>) HistoryPaging()
    {
        var a = Rec(0x80, ExperienceStatus.Candidate) with { Revision = 0, SupportingValidations = 0, ReuseConfidence = 0.5 };
        var quiet = Rec(0x81, ExperienceStatus.Validated);
        Guid E(int n) => Fixed.Id(F, 0x8000 + n);
        return (
            "A history page is one statement: the record's revision and its events after the cursor, oldest first, at most the " +
            "limit. Pages of two, then an exhausted cursor, which is still Found with no events and no next cursor; a record " +
            "with no events is Found and empty; a missing or foreign record NotFound; an unpermitted scope Denied; a limit or " +
            "cursor out of range Invalid.",
            [a, quiet],
            [
                Commit(S, Event(E(1), a.ExperienceId, null, "Candidate", 0)),
                Commit(S, Event(E(2), a.ExperienceId, "Candidate", "Validated", 1)),
                Commit(S, Event(E(3), a.ExperienceId, "Validated", "Reinforced", 2, confidence: null)),
                Commit(S, Event(E(4), a.ExperienceId, "Reinforced", "Reinforced", 3, confidence: Update(Fixed.Id(F, 0x8101), "Supporting", "Human", Fixed.Id(F, 0x8a01), null, 0.5, 2d / 3d, 0, 1, 0, 0, reviewer: "reviewer-ü", detail: "held up"))),
                Commit(S, Event(E(5), a.ExperienceId, "Reinforced", "Revoked", 4)),
                History(S, a.ExperienceId, 2, null),
                History(S, a.ExperienceId, 2, 2),
                History(S, a.ExperienceId, 2, 4),
                History(S, a.ExperienceId, 2, 5),
                History(S, quiet.ExperienceId, 100, null),
                History(OtherAgent, a.ExperienceId, 100, null),
                History(S, a.ExperienceId, 100, null, authorization: OtherAgent),
                History(S, a.ExperienceId, 501, -1),
            ]);
    }

    // ---- step building ------------------------------------------------------------------------------------------

    private static (string Name, JsonObject Seed, JsonObject Steps) Case(
        string name,
        (string Description, IReadOnlyList<ExperienceRecord> Records, IReadOnlyList<JsonObject> Steps) definition)
    {
        var steps = new JsonArray();
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            step["clock"] = ContractJson.Timestamp(Clock.AddMinutes(i));
            steps.Add(step);
        }

        return (
            name,
            new JsonObject { ["records"] = SearchContract.ToSeedJson(definition.Records) },
            new JsonObject { ["description"] = definition.Description, ["steps"] = steps });
    }

    private static ExperienceRecord Rec(int n, ExperienceStatus status, Scope? scope = null) =>
        SearchContract.Seed(F, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", status, scope: scope) is var record
        && status == ExperienceStatus.Quarantined
            ? record with { ReuseConfidence = 0 }
            : record;

    private static JsonObject Event(
        Guid eventId,
        Guid experienceId,
        string? prior,
        string current,
        long expectedRevision,
        Guid? replacement = null,
        JsonObject? confidence = null,
        string? reason = null,
        DateTimeOffset? at = null) => new()
    {
        ["eventId"] = LifecycleContract.D(eventId),
        ["experienceRecordId"] = LifecycleContract.D(experienceId),
        ["priorStatus"] = prior,
        ["currentStatus"] = current,
        ["reason"] = reason ?? $"{prior ?? "nothing"} to {current}.",
        ["producer"] = "governance-review/1.0",
        ["occurredAt"] = ContractJson.Timestamp(at ?? Fixed.T0),
        ["expectedRevision"] = expectedRevision,
        ["replacementExperienceId"] = replacement is { } r ? LifecycleContract.D(r) : null,
        ["confidence"] = confidence,
    };

    private static JsonObject Update(
        Guid evidenceId,
        string kind,
        string source,
        Guid runId,
        Guid? round,
        double priorScore,
        double newScore,
        int priorS,
        int newS,
        int priorF,
        int newF,
        string? reviewer = null,
        string? detail = null,
        Guid? assessment = null,
        string? admission = "HostTrusted",
        string ruleVersion = "1.0.0") => new()
    {
        ["evidenceId"] = LifecycleContract.D(evidenceId),
        ["kind"] = kind,
        ["source"] = source,
        ["runId"] = LifecycleContract.D(runId),
        ["verificationRoundId"] = round is { } r ? LifecycleContract.D(r) : null,
        ["reviewerIdentity"] = reviewer,
        ["ruleVersion"] = ruleVersion,
        ["priorReuseConfidence"] = priorScore,
        ["newReuseConfidence"] = newScore,
        ["priorSupportingValidations"] = priorS,
        ["newSupportingValidations"] = newS,
        ["priorContradictions"] = priorF,
        ["newContradictions"] = newF,
        ["detail"] = detail,
        ["assessmentId"] = assessment is { } a ? LifecycleContract.D(a) : null,
        ["admission"] = admission,
    };

    private static JsonObject Commit(Scope scope, JsonObject lifecycleEvent, Scope? authorization = null, string principal = "contract-principal") => new()
    {
        ["commit"] = new JsonObject
        {
            ["authorization"] = LifecycleContract.Auth(authorization ?? scope, principal),
            ["scope"] = LifecycleContract.ScopeJson(scope),
            ["event"] = lifecycleEvent,
        },
    };

    private static JsonObject Check(Scope scope, Guid experienceId, Guid replacementId) => new()
    {
        ["checkSupersession"] = new JsonObject
        {
            ["authorization"] = LifecycleContract.Auth(scope),
            ["scope"] = LifecycleContract.ScopeJson(scope),
            ["experienceId"] = LifecycleContract.D(experienceId),
            ["replacementExperienceId"] = LifecycleContract.D(replacementId),
        },
    };

    private static JsonObject History(Scope scope, Guid experienceId, int limit, long? startAfter, Scope? authorization = null) => new()
    {
        ["history"] = new JsonObject
        {
            ["authorization"] = LifecycleContract.Auth(authorization ?? scope),
            ["query"] = new JsonObject
            {
                ["scope"] = LifecycleContract.ScopeJson(scope),
                ["experienceId"] = LifecycleContract.D(experienceId),
                ["limit"] = limit,
                ["startAfterRevision"] = startAfter,
            },
        },
    };

    private static JsonObject Read(Scope scope, Guid experienceId) => new()
    {
        ["read"] = new JsonObject
        {
            ["authorization"] = LifecycleContract.Auth(scope),
            ["scope"] = LifecycleContract.ScopeJson(scope),
            ["experienceId"] = LifecycleContract.D(experienceId),
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

    // ---- reading steps back -------------------------------------------------------------------------------------

    /// <summary>A <see cref="LifecycleEvent"/> as a step names it.</summary>
    public static LifecycleEvent ReadEvent(JsonObject e) => new(
        Guid.Parse(e["eventId"]!.GetValue<string>()),
        Guid.Parse(e["experienceRecordId"]!.GetValue<string>()),
        e["priorStatus"] is { } prior ? LifecycleContract.ReadStatus(prior.GetValue<string>()) : null,
        LifecycleContract.ReadStatus(e["currentStatus"]!.GetValue<string>()),
        e["reason"]!.GetValue<string>(),
        e["producer"]!.GetValue<string>(),
        LifecycleContract.Instant(e["occurredAt"])!.Value,
        e["expectedRevision"]!.GetValue<long>(),
        e["replacementExperienceId"] is { } replacement ? Guid.Parse(replacement.GetValue<string>()) : null,
        e["confidence"] is JsonObject c ? ReadUpdate(c) : null);

    private static ConfidenceUpdate ReadUpdate(JsonObject c) => new(
        Guid.Parse(c["evidenceId"]!.GetValue<string>()),
        Enum.Parse<ConfidenceEvidenceKind>(c["kind"]!.GetValue<string>()),
        Enum.Parse<ConfidenceEvidenceSource>(c["source"]!.GetValue<string>()),
        Guid.Parse(c["runId"]!.GetValue<string>()),
        c["verificationRoundId"] is { } round ? Guid.Parse(round.GetValue<string>()) : null,
        c["reviewerIdentity"]?.GetValue<string>(),
        c["ruleVersion"]!.GetValue<string>(),
        c["priorReuseConfidence"]!.GetValue<double>(),
        c["newReuseConfidence"]!.GetValue<double>(),
        c["priorSupportingValidations"]!.GetValue<int>(),
        c["newSupportingValidations"]!.GetValue<int>(),
        c["priorContradictions"]!.GetValue<int>(),
        c["newContradictions"]!.GetValue<int>(),
        c["detail"]?.GetValue<string>())
    {
        AssessmentId = c["assessmentId"] is { } assessment ? Guid.Parse(assessment.GetValue<string>()) : null,
        Admission = c["admission"] is { } admission ? Enum.Parse<ConfidenceEvidenceAdmission>(admission.GetValue<string>()) : null,
    };

    internal static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
