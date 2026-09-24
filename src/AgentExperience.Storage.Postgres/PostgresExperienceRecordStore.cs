using System.Data.Common;
using System.Net.Sockets;
using AgentExperience.Abstractions;
using AgentExperience.Storage.Postgres.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceRecordStore"/> over PostgreSQL with plain Npgsql. Each operation validates
/// the request, checks it against the host-established <see cref="AuthorizationContext"/>, and only
/// then opens a connection and runs parameterized SQL whose predicates apply the exact scope -- or,
/// for <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> alone, the exact scope or an active sharing grant. The
/// schema must already exist: the host applies it once by calling
/// <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>. The store
/// never migrates, on construction or otherwise.
/// </summary>
/// <remarks>
/// <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> is also the one read that can be <em>audited</em>: when the host wires an
/// <see cref="ExperienceGrantAuditing"/>, a record delivered through a grant appends one access row naming the
/// grant the statement actually used. The append is a separate statement after the read, never part of it, so a
/// read still takes no write lock and can still run on a replica. An owner's own record writes nothing, and nor
/// does a read the caller declared an <see cref="ExperienceReadPurpose.ScopeCheck"/> -- it is about to refuse the
/// record for being grant-readable, so nothing is handed over. The two search channels audit what they return
/// through their own batched append.
/// <see cref="CommitLifecycleEventAsync"/> is the only operation that changes a <em>live</em> record: it appends
/// the event and updates the record's projection in one transaction on one connection, keyed by
/// <see cref="LifecycleEvent.EventId"/> for idempotency and by
/// <see cref="LifecycleEvent.ExpectedRevision"/> for concurrency. The store persists the transition Core
/// decided and never derives a status, score, or counter of its own.
/// <see cref="DeleteAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> is the one destructive
/// operation: it erases a record's payload and every stored row that named it, in one transaction, and leaves a
/// payload-free tombstone behind that every other path then refuses.
/// PostgreSQL <c>timestamptz</c> stores microseconds, so <see cref="ExperienceRecord.CreatedAt"/> and
/// <see cref="ExperienceRecord.UpdatedAt"/> are truncated to whole microseconds (in UTC) on write.
/// Nested timestamps live in the JSONB payload at full precision and are also returned in UTC.
/// Tool-call argument values read back JSON-normalized: <see cref="string"/>, <see cref="bool"/>,
/// <see cref="long"/>, <see cref="double"/>, <see langword="null"/>,
/// <see cref="Dictionary{TKey,TValue}"/> of <see cref="string"/> to <see cref="object"/>, and
/// <see cref="List{T}"/> of <see cref="object"/>. Dictionary key order is not preserved, and whole-number
/// doubles read back as <see cref="long"/>. Query ties on <c>CreatedAt</c> are broken by PostgreSQL <c>uuid</c>
/// byte order, which differs from .NET <see cref="Guid"/> comparison.
/// </remarks>
public sealed class PostgresExperienceRecordStore : IExperienceRecordStore
{
    /// <summary>The canonical record table. Shared with <see cref="PostgresExperienceCandidateSource"/>, which reads from it.</summary>
    internal const string Table = "agent_experience.experience_records";

    /// <summary>The smallest batch <see cref="SweepExpiredAsync(AuthorizationContext, Scope, TimeSpan, int, ScopeMatch, CancellationToken)"/> accepts. There is no "sweep everything".</summary>
    public const int MinSweepBatchSize = 1;

    /// <summary>
    /// The largest batch <see cref="SweepExpiredAsync(AuthorizationContext, Scope, TimeSpan, int, ScopeMatch, CancellationToken)"/> accepts. Each record in a batch is erased in
    /// its own transaction across seven tables, so the bound is what keeps one sweep call from becoming
    /// an unbounded amount of destructive work the host cannot interrupt.
    /// </summary>
    public const int MaxSweepBatchSize = 500;

    /// <summary>
    /// The record columns every read selects, in the order <see cref="DecodeRecord"/> expects (ordinals 0-17).
    /// A reader that selects more must append its extra columns <em>after</em> these, never before.
    /// </summary>
    internal const string SelectColumns =
        "experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, " +
        "status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, " +
        "payload_version, payload";

    /// <summary>
    /// "this row is not a tombstone", unqualified, for a statement over the record table alone.
    /// <para>
    /// A tombstone carries no payload at all (see <c>0010</c>), so it is not a record a read can
    /// return: <see cref="ExperiencePayload.Deserialize"/> would fail on it, and a list that included
    /// one would be handing back a row with nothing in it. Every read filters it out, and the two
    /// operations that name one record -- <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> and
    /// <see cref="GetHistoryAsync"/> -- read <c>deleted_at</c> instead of filtering on it, so they can
    /// answer <see cref="ExperienceStoreOutcome.Deleted"/> inside the scope that owns the tombstone.
    /// </para>
    /// </summary>
    internal const string LivePredicate = "deleted_at IS NULL";

    /// <summary>The same, qualified with the <c>r</c> alias, for a statement that joins the record table to another.</summary>
    internal const string RecordLivePredicate = "r.deleted_at IS NULL";

    /// <summary>
    /// The row lock every <em>write</em> that gates on <see cref="RecordLivePredicate"/> has to take on
    /// the record row it gated against.
    /// <para>
    /// Without it the predicate is evaluated against a READ COMMITTED snapshot taken before the erasure
    /// committed, and the write lands on a record the caller has already been told is gone: a stored
    /// vector derived from the erased summary and lesson, a live sharing grant over a spent ID, or a
    /// reviewer identity and free-text rationale about an erased record in an append-only ledger. With
    /// it, the writer is parked against the purge's own <c>FOR UPDATE</c> (step 1 of
    /// <c>purge_experience_record</c>) and, when the purge commits, PostgreSQL re-checks the write's
    /// predicate against the row version the purge left behind -- which is the tombstone, so the write
    /// matches nothing and the caller is told it lost. The same lock taken first makes the purge wait
    /// instead, and the erasure then sweeps the row the writer committed.
    /// </para>
    /// <para>
    /// <c>FOR KEY SHARE</c> rather than <c>FOR SHARE</c> on purpose: it is the weakest mode that still
    /// conflicts with the purge's <c>FOR UPDATE</c>, and it does <em>not</em> conflict with the
    /// <c>FOR NO KEY UPDATE</c> an ordinary lifecycle commit's projection <c>UPDATE</c> takes, so
    /// serializing against erasure costs nothing against the writes that happen all the time.
    /// </para>
    /// <para>
    /// <see cref="CommitLifecycleEventAsync"/> needs no locking clause of its own: its projection
    /// <c>UPDATE</c> is itself the lock, and <c>0010</c>'s projection guard refuses any <c>UPDATE</c> of
    /// a tombstone from the database's side as well.
    /// </para>
    /// </summary>
    internal const string RecordKeyShareLock = "FOR KEY SHARE OF r";

    /// <summary>The alias a read selects <c>deleted_at</c> under, read back by name, never by ordinal.</summary>
    internal const string DeletedAtAlias = "deleted_at";

    /// <summary>
    /// The tombstone marker, appended <em>after</em> the record columns so <see cref="ReadRecord"/>'s
    /// ordinals 0-17 are untouched.
    /// </summary>
    internal const string DeletedAtColumn = "r." + DeletedAtAlias + " AS " + DeletedAtAlias;

    /// <summary>The exact-scope predicate every statement applies, shared with <see cref="PostgresExperienceCandidateSource"/>.</summary>
    internal const string ScopePredicate =
        "tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id " +
        "AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND user_id IS NOT DISTINCT FROM @user_id";

    private const string InsertSql =
        $"INSERT INTO {Table} ({SelectColumns}) VALUES (@experience_id, @source_run_id, @tenant_id, @application_id, " +
        "@project_id, @team_id, @agent_id, @user_id, @task_id, @status, @reuse_confidence, @supporting_validations, " +
        "@contradictions, @revision, @created_at, @updated_at, @payload_version, @payload)";

    /// <summary>
    /// The one read that a grant may widen: exactly this scope, or an active grant naming this record
    /// and permitting this scope. The table is aliased so the grant subquery's correlation is
    /// unambiguous -- an unqualified <c>experience_id</c> inside it would silently resolve to the
    /// grants table's own column and match every record.
    /// <para>
    /// This is the one grant-aware read that also has to <em>name</em> the grant, because it is the
    /// one that delivers a record to a caller and therefore the one an access row is written for. The
    /// lateral join both decides readability and produces the ID, so the row can never name a grant
    /// other than the one the database used. See <see cref="PermittingGrantJoin"/>.
    /// </para>
    /// </summary>
    internal const string GetSql = GetSelectFrom + $"WHERE r.experience_id = @experience_id AND {GetReadablePredicate}";

    /// <summary>
    /// Everything <see cref="GetSql"/> says before its <c>WHERE</c>: the columns, the shared flag, the
    /// permitting grant and its disclosure, the tombstone marker, and the lateral join that names the
    /// grant. <see cref="GetManySql"/> is built from the same text, so the single and the batched read
    /// cannot drift apart on any of it.
    /// </summary>
    internal const string GetSelectFrom =
        $"SELECT {SelectColumns}, {SharedByGrantColumn}, {PermittingGrantColumn}, {PermittingDisclosureColumn}, {DeletedAtColumn} FROM {Table} r " +
        $"{PermittingGrantJoin} ";

    /// <summary>The readability rule <see cref="GetSql"/> and <see cref="GetManySql"/> share, byte for byte.</summary>
    internal const string GetReadablePredicate = ReadableWithNamedGrantPredicate;

    /// <summary>
    /// <see cref="GetSql"/> for several records in one statement (story 5.6, KL-1): the identical select,
    /// join and readability predicate, with only the ID match widened from one parameter to an array.
    /// The lateral join is evaluated per row, so each row names its own permitting grant exactly as a
    /// single read of it would; and the whole batch is read from one snapshot.
    /// </summary>
    internal const string GetManySql = GetSelectFrom + $"WHERE r.experience_id = ANY(@experience_ids) AND {GetReadablePredicate}";

    /// <summary>
    /// The same read with the grant branch removed, for a database that has no
    /// <c>experience_grants</c> table or a role that may not read it. See <see cref="PostgresGrantSupport"/>.
    /// Nothing is shared on this path, so nothing is audited either: the read returns only records the
    /// requesting scope already owns.
    /// </summary>
    internal const string GetExactSql = GetExactSelectFrom + $"WHERE r.experience_id = @experience_id AND {RecordScopePredicate}";

    /// <summary>Everything <see cref="GetExactSql"/> says before its <c>WHERE</c>, shared with <see cref="GetManyExactSql"/>.</summary>
    internal const string GetExactSelectFrom =
        $"SELECT {SelectColumns}, false AS {SharedByGrantAlias}, NULL::uuid AS {PermittingGrantAlias}, " +
        $"NULL::text AS {PermittingDisclosureAlias}, {DeletedAtColumn} " +
        $"FROM {Table} r ";

    /// <summary><see cref="GetExactSql"/> for several records in one statement: the fallback <see cref="GetManySql"/> takes when grants are unavailable.</summary>
    internal const string GetManyExactSql = GetExactSelectFrom + $"WHERE r.experience_id = ANY(@experience_ids) AND {RecordScopePredicate}";

    private const string QuerySql = $"SELECT {SelectColumns} FROM {Table} WHERE {ScopePredicate} AND {LivePredicate}";

    private const string QueryStatusPredicate = " AND status = ANY(@statuses)";

    private const string QueryOrderAndLimit = " ORDER BY created_at DESC, experience_id LIMIT @limit";

    private const string EventsTable = "agent_experience.lifecycle_events";

    /// <summary>
    /// The event columns every read selects, in the order <see cref="DecodeEvent"/> expects (ordinals
    /// 0-31). A reader that selects more must append its extra columns <em>after</em> these.
    /// <para>
    /// Everything from <c>actor</c> onwards arrived with <c>0007</c>. <c>actor</c> is written for every
    /// commit; the <c>confidence_*</c> and score columns are written together or not at all, which the
    /// table states as a CHECK, so a half-written update cannot reach the log.
    /// </para>
    /// </summary>
    private const string EventColumns =
        "event_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision, " +
        "replacement_experience_id, actor, confidence_evidence_id, confidence_kind, confidence_source, " +
        "confidence_run_id, confidence_verification_round_id, confidence_reviewer_identity, confidence_rule_version, " +
        "confidence_detail, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, " +
        "new_supporting_validations, prior_contradictions, new_contradictions";

    /// <summary>The ordinal <c>r.revision</c> sits at in <see cref="HistorySql"/>, straight after <see cref="EventColumns"/>.</summary>
    private const int HistoryRevisionOrdinal = 32;

    /// <summary>The ordinal <c>r.deleted_at</c> sits at in <see cref="HistorySql"/>, straight after the revision.</summary>
    private const int HistoryDeletedAtOrdinal = 33;

    private const string InsertEventSql =
        $"INSERT INTO {EventsTable} ({EventColumns}) VALUES (@event_id, @experience_id, @tenant_id, @application_id, " +
        "@project_id, @team_id, @agent_id, @user_id, @prior_status, @current_status, @reason, @producer, " +
        "@occurred_at, @recorded_at, @expected_revision, @applied_revision, @replacement_experience_id, @actor, " +
        "@confidence_evidence_id, @confidence_kind, @confidence_source, @confidence_run_id, " +
        "@confidence_verification_round_id, @confidence_reviewer_identity, @confidence_rule_version, " +
        "@confidence_detail, @prior_reuse_confidence, @new_reuse_confidence, @prior_supporting_validations, " +
        "@new_supporting_validations, @prior_contradictions, @new_contradictions)";

    /// <summary>The primary key a resubmitted <see cref="LifecycleEvent.EventId"/> violates.</summary>
    private const string EventPrimaryKey = "lifecycle_events_pkey";

    /// <summary>The unique index a second event claiming an already-taken record revision violates.</summary>
    private const string EventRevisionIndex = "ix_lifecycle_events_record_revision";

    /// <summary>The evidence ledger. Created by <c>0007_confidence_evidence.sql</c>.</summary>
    private const string EvidenceTable = "agent_experience.confidence_evidence";

    /// <summary>
    /// The evidence row's own columns. <c>independence_key</c> is deliberately absent: it is a generated
    /// column the database derives from <c>source</c>, <c>run_id</c>, <c>verification_round_id</c>, and
    /// <c>reviewer_identity</c>, precisely so no writer -- this one included -- can choose it.
    /// </summary>
    private const string EvidenceColumns =
        "evidence_id, experience_id, event_id, kind, source, run_id, verification_round_id, " +
        "reviewer_identity, counted, actor, rule_version, detail, recorded_at, applied_revision, applied_status, " +
        "prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, new_supporting_validations, " +
        "prior_contradictions, new_contradictions";

    private const string InsertEvidenceSql =
        $"INSERT INTO {EvidenceTable} ({EvidenceColumns}) VALUES (@evidence_id, @experience_id, @event_id, " +
        "@confidence_kind, @confidence_source, @confidence_run_id, @confidence_verification_round_id, " +
        "@confidence_reviewer_identity, @counted, @actor, @confidence_rule_version, @confidence_detail, " +
        "@recorded_at, @applied_revision, @applied_status, @prior_reuse_confidence, @new_reuse_confidence, " +
        "@prior_supporting_validations, @new_supporting_validations, @prior_contradictions, @new_contradictions)";

    /// <summary>The primary key a resubmitted <see cref="ConfidenceUpdate.EvidenceId"/> violates.</summary>
    private const string EvidencePrimaryKey = "confidence_evidence_pkey";

    /// <summary>
    /// The partial unique index that decides independence. Violating it means this observation has
    /// already been counted for this record, which is not a failure: the submission is stored anyway,
    /// with <c>counted = false</c>, and the counters stay where they are.
    /// </summary>
    private const string EvidenceIndependenceIndex = "ux_confidence_evidence_independence";

    /// <summary>
    /// The savepoint the first evidence insert runs under, so a taken independence key costs only that
    /// statement rather than the whole transaction. Without it the unique violation would abort the
    /// commit that is supposed to record the duplicate.
    /// </summary>
    private const string EvidenceSavepoint = "confidence_evidence_attempt";

    /// <summary>
    /// One resubmitted evidence ID, read back whole from the ledger row itself, which carries every number
    /// a replay has to report so the answer describes one moment rather than one value from here and
    /// another from a later read.
    /// <para>
    /// The join to the record is not for data -- nothing is selected from it. It is there to carry
    /// <see cref="RecordScopePredicate"/>, so an evidence ID that belongs to another scope reads back as
    /// no row at all. Without it a guessed ID would hand a caller another tenant's scores, counters, and
    /// revision: the primary key is global, and this is the one statement that looks a row up by it alone.
    /// </para>
    /// </summary>
    private const string SelectEvidenceSql =
        "SELECT ev.experience_id, ev.event_id, ev.kind, ev.source, ev.run_id, ev.verification_round_id, " +
        "ev.reviewer_identity, ev.counted, ev.applied_revision, ev.applied_status, ev.rule_version, ev.detail, " +
        "ev.prior_reuse_confidence, ev.new_reuse_confidence, ev.prior_supporting_validations, " +
        "ev.new_supporting_validations, ev.prior_contradictions, ev.new_contradictions " +
        $"FROM {EvidenceTable} ev JOIN {Table} r ON r.experience_id = ev.experience_id " +
        $"WHERE ev.evidence_id = @evidence_id AND {RecordScopePredicate} AND {RecordLivePredicate}";

    /// <summary>
    /// The record's revision and status, locked for the rest of the transaction. Used only on the
    /// duplicate path, which writes no projection update and therefore has no revision-guarded statement
    /// of its own to hold the row still while it records what the record currently looks like.
    /// </summary>
    private const string LockRevisionAndStatusSql = SelectRevisionAndStatusSql + " FOR UPDATE";

    /// <summary>
    /// The revision guard, the prior-status guard, and the scope predicate live in the same statement,
    /// so a stale revision, a prior status the record is not in, and a foreign scope are all "no row
    /// updated" and none of them can overwrite state it does not own.
    /// <para>
    /// A <see langword="null"/> <c>@prior_status</c> does <em>not</em> skip the status match: it falls
    /// back to <c>@current_status</c>, so a record's first event may only record the status the record
    /// is already in. Skipping the match -- which this statement used to do -- let a caller move a
    /// record from any status to any other simply by omitting the prior status, which is precisely what
    /// Core's transition table exists to prevent.
    /// </para>
    /// </summary>
    private const string UpdateProjectionSetSql =
        $"UPDATE {Table} SET status = @current_status, revision = @applied_revision, updated_at = @recorded_at";

    /// <summary>
    /// The three columns a counted confidence update moves, written in the same statement as the status
    /// and the revision -- which is what satisfies the database's own projection guard, and what makes
    /// "the counters moved" and "the event that says so was appended" one fact rather than two.
    /// Every value is one the event carried: this statement reads nothing and derives nothing.
    /// </summary>
    private const string UpdateProjectionConfidenceSetSql =
        ", reuse_confidence = @new_reuse_confidence, supporting_validations = @new_supporting_validations, " +
        "contradictions = @new_contradictions";

    /// <summary>
    /// The guards above plus "and this record has not been erased". A tombstone is terminal: a late
    /// commit against one matches no row here and is reported as
    /// <see cref="ExperienceStoreOutcome.Deleted"/> after the same re-read that tells a stale revision
    /// from a missing record. The database refuses it a second time from its own side -- <c>0010</c>'s
    /// projection guard rejects every UPDATE of a tombstone -- so neither this adapter nor a writer
    /// bypassing it can move one.
    /// <para>
    /// The tombstone term here is redundant and kept on purpose: <c>status = COALESCE(...)</c> compares
    /// against an <see cref="ExperienceStatus"/> member's name, and a tombstone's status is a literal no
    /// member has, so this statement could never match one anyway. It is defence in depth against a
    /// future status whose name collides, and it is named as redundant rather than counted as the thing
    /// that makes late commits safe -- the re-read below, and <c>0010</c>'s projection guard, are.
    /// </para>
    /// </summary>
    private const string UpdateProjectionWhereSql =
        " WHERE experience_id = @experience_id AND revision = @expected_revision " +
        $"AND status = COALESCE(@prior_status, @current_status) AND {ScopePredicate} AND {LivePredicate}";

    private const string UpdateProjectionSql = UpdateProjectionSetSql + UpdateProjectionWhereSql;

    private const string UpdateProjectionWithConfidenceSql =
        UpdateProjectionSetSql + UpdateProjectionConfidenceSetSql + UpdateProjectionWhereSql;

    /// <summary>
    /// The record's revision, status, and tombstone marker within exactly this scope. <c>deleted_at</c>
    /// is selected rather than filtered on, because this read is what turns "the guarded UPDATE matched
    /// no row" into a reason, and "erased" is one of the reasons. The status of a tombstone is a literal
    /// this library's enum has no member for, so it is only ever decoded when <c>deleted_at</c> is null.
    /// </summary>
    private const string SelectRevisionAndStatusSql =
        $"SELECT revision, status, {DeletedAtAlias} FROM {Table} WHERE experience_id = @experience_id AND {ScopePredicate}";

    private const string SelectEventSql = $"SELECT {EventColumns} FROM {EventsTable} WHERE event_id = @event_id";

    private const string JoinedEventColumns =
        "e.event_id, e.experience_id, e.tenant_id, e.application_id, e.project_id, e.team_id, e.agent_id, e.user_id, " +
        "e.prior_status, e.current_status, e.reason, e.producer, e.occurred_at, e.recorded_at, e.expected_revision, " +
        "e.applied_revision, e.replacement_experience_id, e.actor, e.confidence_evidence_id, e.confidence_kind, " +
        "e.confidence_source, e.confidence_run_id, e.confidence_verification_round_id, e.confidence_reviewer_identity, " +
        "e.confidence_rule_version, e.confidence_detail, e.prior_reuse_confidence, e.new_reuse_confidence, " +
        "e.prior_supporting_validations, e.new_supporting_validations, e.prior_contradictions, e.new_contradictions";

    /// <summary>
    /// The same exact-scope predicate as <see cref="ScopePredicate"/>, qualified with the <c>r</c>
    /// alias for a statement that joins the record table to another one. Shared with the vectors
    /// adapter, so both retrieval channels apply a byte-for-byte identical scope match.
    /// </summary>
    internal const string RecordScopePredicate =
        "r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id " +
        "AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND r.user_id IS NOT DISTINCT FROM @user_id";

    /// <summary>The sharing-grant table. Created by <c>0005_create_experience_grants.sql</c>.</summary>
    internal const string GrantsTable = "agent_experience.experience_grants";

    /// <summary>
    /// An active grant naming the <c>r</c>-aliased record and permitting the requesting scope. Active
    /// is decided here and nowhere else: issued, not revoked, and not yet expired as of
    /// <c>clock_timestamp()</c> -- the database's own wall clock, so a caller whose clock is wrong (or
    /// convenient) cannot widen anything. It is <c>clock_timestamp()</c> rather than <c>now()</c>
    /// because <c>now()</c> is fixed at the start of the surrounding transaction: inside a long
    /// caller-held transaction it would keep admitting a grant that expired minutes ago.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves are matched. The grant's owner-scope columns must equal the record's, which is what
    /// keeps a hand-written grant row from attaching itself to a record it does not describe; the
    /// grant's recipient columns must equal the request scope, which is what it actually permits.
    /// Since the recipient's tenant, application, and project are constrained equal to the owner's by
    /// <c>experience_grants_same_boundary</c>, no grant can move a record across those three however
    /// this predicate is composed.
    /// </para>
    /// <para>
    /// It uses the same <c>@tenant_id</c>..<c>@user_id</c> parameters the scope predicate does, so any
    /// statement that already calls <see cref="AddScopeParameters"/> can compose it as it stands.
    /// </para>
    /// </remarks>
    internal const string ActiveGrantPredicate =
        $"EXISTS (SELECT 1 FROM {GrantsTable} g WHERE {ActiveGrantConditions})";

    /// <summary>
    /// The conditions that make a <c>g</c>-aliased grant row active for the <c>r</c>-aliased record and
    /// the requesting scope, without the <c>EXISTS</c> wrapper around them. Factored out so
    /// <see cref="ActiveGrantPredicate"/> and <see cref="PermittingGrantJoin"/> are the same rule
    /// written once: the join that <em>names</em> the grant must not be able to drift from the
    /// predicate that decides whether one exists, or an access row could name a grant that did not
    /// permit the read.
    /// </summary>
    internal const string ActiveGrantConditions =
        "g.experience_id = r.experience_id " +
        "AND g.revoked_at IS NULL AND g.expires_at > clock_timestamp() " +
        "AND g.tenant_id = r.tenant_id AND g.application_id = r.application_id AND g.project_id = r.project_id " +
        "AND g.team_id IS NOT DISTINCT FROM r.team_id AND g.agent_id IS NOT DISTINCT FROM r.agent_id " +
        "AND g.user_id IS NOT DISTINCT FROM r.user_id " +
        "AND g.recipient_tenant_id = @tenant_id AND g.recipient_application_id = @application_id " +
        "AND g.recipient_project_id = @project_id " +
        "AND g.recipient_team_id IS NOT DISTINCT FROM @team_id " +
        "AND g.recipient_agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND g.recipient_user_id IS NOT DISTINCT FROM @user_id";

    /// <summary>
    /// What a <em>read</em> may return: the record's own exact scope, or an active grant that names it
    /// and permits the requesting scope. This is the whole of grant enforcement, and it lives in SQL,
    /// so the database can never hand back a row the predicate did not permit and no application code
    /// is in a position to widen one.
    /// <para>
    /// It is used by <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/>, by the text channel, and by the vector channel -- the
    /// three paths a grant covers. Writes, lifecycle commits, lifecycle history, and
    /// <see cref="QueryAsync"/>'s enumeration keep the exact-scope predicate: a grant confers reading
    /// one named record, never writing, never the audit trail of mutations, and never the right to
    /// list what a scope holds.
    /// </para>
    /// </summary>
    internal const string ReadableRecordScopePredicate =
        "((" + RecordScopePredicate + ") OR " + ActiveGrantPredicate + ")";

    /// <summary>The alias the shared-by-grant flag is selected under, read back by name, never by ordinal.</summary>
    internal const string SharedByGrantAlias = "shared_by_grant";

    /// <summary>
    /// Whether the row that came back is the requester's own or someone else's, shared. It is the
    /// negation of the exact-scope match, computed by the same statement that decided readability, so
    /// the answer cannot be re-derived (or mis-derived) anywhere else. Appended <em>after</em> the
    /// record columns, so <see cref="ReadRecord"/>'s ordinals 0-17 are untouched.
    /// </summary>
    internal const string SharedByGrantColumn = "NOT (" + RecordScopePredicate + ") AS " + SharedByGrantAlias;

    /// <summary>The alias the permitting grant's ID is selected under, read back by name, never by ordinal.</summary>
    internal const string PermittingGrantAlias = "permitting_grant_id";

    /// <summary>The name the lateral join is given, kept distinct from the <c>g</c> alias inside it.</summary>
    private const string PermittingGrantSource = "permitting_grant";

    /// <summary>
    /// The one active grant the read actually used, as a lateral join rather than a second subquery.
    /// <para>
    /// <b>Why a join and not another <c>EXISTS</c>.</b> An access row has to name the grant that
    /// permitted the read, not merely assert that one did. Deciding readability with
    /// <see cref="ActiveGrantPredicate"/> and then looking the ID up separately would ask the same
    /// question twice, and the two answers could differ -- a grant revoked in between, or simply a
    /// different one picked -- leaving a row naming a grant that did not permit anything. Here the row
    /// comes back from the same statement that admitted the record, so the two cannot disagree.
    /// </para>
    /// <para>
    /// <b>Why it is ordered.</b> Two active grants may legitimately permit the same read -- different
    /// administrators, different reasons, overlapping windows. <c>ORDER BY g.grant_id LIMIT 1</c>
    /// makes which one the trail names a stable fact rather than whatever the planner happened to hand
    /// back first; the choice is arbitrary but it is not arbitrary <em>per read</em>.
    /// </para>
    /// <para>
    /// It is a <c>LEFT JOIN LATERAL ... ON true</c>, so a record the requester owns still comes back
    /// with a null grant ID rather than being filtered away.
    /// </para>
    /// <para>
    /// <b>It also yields the grant's disclosure level</b>, from the same row. The level injection honours
    /// and the level an access row records must be the level of the grant that admitted the read, so it
    /// is never looked up a second time.
    /// </para>
    /// </summary>
    internal const string PermittingGrantJoin =
        $"LEFT JOIN LATERAL (SELECT g.grant_id, g.disclosure FROM {GrantsTable} g WHERE {ActiveGrantConditions} " +
        $"ORDER BY g.grant_id LIMIT 1) {PermittingGrantSource} ON true";

    /// <summary>The permitting grant's ID, appended <em>after</em> the record columns and the shared flag.</summary>
    internal const string PermittingGrantColumn = PermittingGrantSource + ".grant_id AS " + PermittingGrantAlias;

    /// <summary>The alias the permitting grant's disclosure level is selected under, read back by name.</summary>
    internal const string PermittingDisclosureAlias = "permitting_grant_disclosure";

    /// <summary>
    /// The permitting grant's disclosure level, from the same lateral row as
    /// <see cref="PermittingGrantColumn"/>. Null for a record the requester owns.
    /// </summary>
    internal const string PermittingDisclosureColumn =
        PermittingGrantSource + ".disclosure AS " + PermittingDisclosureAlias;

    /// <summary>
    /// <see cref="ReadableRecordScopePredicate"/> expressed against <see cref="PermittingGrantJoin"/>:
    /// the requester's own record, or one the join found a live grant for. The two are the same rule --
    /// the join's conditions are <see cref="ActiveGrantConditions"/> byte for byte -- but this form
    /// reuses the row the join already produced instead of re-running the subquery as an
    /// <c>EXISTS</c>.
    /// </summary>
    internal const string ReadableWithNamedGrantPredicate =
        "((" + RecordScopePredicate + ") OR " + PermittingGrantFoundPredicate + ")";

    /// <summary>
    /// "the lateral join found a live grant", for a statement composing its own readable predicate --
    /// the vectors channel, whose exact-scope branch spans two aliases. Shared so no channel retypes
    /// the alias the join was given.
    /// </summary>
    internal const string PermittingGrantFoundPredicate = PermittingGrantSource + ".grant_id IS NOT NULL";

    /// <summary>
    /// One statement, so the revision and the events come from one snapshot however the server is
    /// configured: a commit landing mid-read can never make the returned revision contradict the
    /// returned events. The outer join keeps a record with no events a <see cref="ExperienceStoreOutcome.Found"/>
    /// with an empty history -- that row has a null <c>event_id</c>.
    /// <para>
    /// Both the cursor and the bound are deliberately awkward here, and both are placed the only way
    /// that works. The cursor lives in the join's <c>ON</c> clause rather than in the <c>WHERE</c>:
    /// moved to the <c>WHERE</c>, a cursor past the last event would filter the single joined row away
    /// and turn an exhausted history into <see cref="ExperienceStoreOutcome.NotFound"/> -- losing the
    /// distinction between "this record has nothing more to show" and "no such record in this scope".
    /// The <c>LIMIT</c> is safe where it is for the mirror reason: the no-events row appears only
    /// when the join matched nothing at all, so any limit of at least one still keeps the row that
    /// carries <c>r.revision</c>.
    /// </para>
    /// </summary>
    private const string HistorySql =
        $"SELECT {JoinedEventColumns}, r.revision, r.{DeletedAtAlias} FROM {Table} r " +
        $"LEFT JOIN {EventsTable} e ON e.experience_id = r.experience_id " +
        "AND (@start_after_revision IS NULL OR e.applied_revision > @start_after_revision) " +
        $"WHERE r.experience_id = @experience_id AND {RecordScopePredicate} " +
        "ORDER BY e.applied_revision LIMIT @limit";

    /// <summary>
    /// The same exact-scope predicate, written out against the <c>e</c> alias rather than derived from
    /// <see cref="RecordScopePredicate"/> by string replacement. A blind <c>"r." -&gt; "e."</c> rewrite
    /// would also rewrite any future parameter or column name containing those two characters, and the
    /// failure would be a silently wrong scope filter inside the recursive chain walk rather than a
    /// syntax error. The two are kept honest by <c>Scope_predicates_stay_in_step_across_aliases</c>.
    /// </summary>
    internal const string EventScopePredicate =
        "e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id " +
        "AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND e.user_id IS NOT DISTINCT FROM @user_id";

    /// <summary>
    /// Locks both the record being superseded and its proposed replacement, in a deterministic order so
    /// two supersessions naming each other cannot deadlock. Held for the rest of the commit transaction,
    /// which is what makes the replacement checks below atomic with the write: a concurrent transition
    /// of the replacement either lands before this lock (and is therefore seen by the check) or blocks
    /// behind it (and is therefore decided against a record this commit has already moved).
    /// </summary>
    private const string LockSupersessionRowsSql =
        $"SELECT experience_id FROM {Table} WHERE experience_id = ANY(@lock_ids) ORDER BY experience_id FOR UPDATE";

    /// <summary>
    /// The whole supersession check, in one statement and one round trip: is the record in this exact
    /// scope, is the replacement, and does the replacement already sit on a chain that leads back to
    /// the record.
    /// <para>
    /// The recursive term walks <c>replacement_experience_id</c> forward from the proposed replacement:
    /// each step asks "and what replaced <em>that</em>". Reaching the record being superseded means the
    /// record already replaces the replacement, directly or transitively, so accepting this one would
    /// close a cycle. It is <c>UNION</c>, not <c>UNION ALL</c>, so the walk terminates even over a chain
    /// some earlier writer managed to close. The recursion is scope-qualified like everything else, so
    /// a foreign-scope event can neither extend the chain nor reveal that it exists.
    /// </para>
    /// </summary>
    private static readonly string SupersessionCheckSql = $"""
        WITH RECURSIVE replaced_by(experience_id) AS (
            SELECT @replacement_id::uuid
            UNION
            SELECT e.replacement_experience_id
            FROM {EventsTable} e
            JOIN replaced_by c ON e.experience_id = c.experience_id
            WHERE e.replacement_experience_id IS NOT NULL AND {EventScopePredicate}
        )
        SELECT
            (SELECT r.status FROM {Table} r
             WHERE r.experience_id = @experience_id AND {RecordScopePredicate} AND {RecordLivePredicate}),
            (SELECT r.status FROM {Table} r
             WHERE r.experience_id = @replacement_id AND {RecordScopePredicate} AND {RecordLivePredicate}),
            EXISTS (SELECT 1 FROM replaced_by WHERE experience_id = @experience_id)
        """;

    /// <summary>
    /// The one erasure path, created by <c>0010</c>. Every step of it -- the scope and revision guards,
    /// the seven tables it sweeps, and the tombstone it leaves -- runs inside this one function, in one
    /// transaction, under a marker the append-only guards recognise and no other session can see. This
    /// adapter composes no DELETE of its own: there is nothing here to get out of step with the order the
    /// script pins.
    /// </summary>
    private const string PurgeSql =
        "SELECT purge_outcome, purge_revision FROM agent_experience.purge_experience_record(" +
        "@experience_id, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, " +
        "@expected_revision, @deleted_at)";

    /// <summary>
    /// "at or beneath this scope": the three required fields exactly, and each optional field either
    /// unconstrained (the root's is null) or exactly the root's. This is the definition
    /// <see cref="ScopeMatch"/> documents, and the reading <see cref="AuthorizationContext.Permits"/>
    /// already gives a null bound. The required fields are never a wildcard: a null parameter there
    /// matches nothing.
    /// </summary>
    internal const string SubtreeScopePredicate =
        "tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id " +
        "AND (@team_id IS NULL OR team_id = @team_id) AND (@agent_id IS NULL OR agent_id = @agent_id) " +
        "AND (@user_id IS NULL OR user_id = @user_id)";

    /// <summary>The columns a sweep candidate is read with: its ID, and the exact scope it is erased in.</summary>
    private const string SweepCandidateColumns =
        "experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id";

    /// <summary>
    /// One bounded page of a scope's records that are older than the retention cutoff, oldest first.
    /// Deliberately only the IDs and their scope: the sweep erases what it finds and never reads a
    /// payload it is about to destroy. One row beyond the batch is selected so the result can say
    /// whether more remain without a second count.
    /// </summary>
    private const string SweepCandidatesSql =
        $"SELECT {SweepCandidateColumns} FROM {Table} " +
        $"WHERE {ScopePredicate} AND {LivePredicate} AND created_at < @cutoff " +
        "ORDER BY created_at, experience_id LIMIT @limit";

    /// <summary>
    /// The same page across the scope and every scope beneath it (<see cref="ScopeMatch.Subtree"/>).
    /// Served by <c>0010</c>'s <c>ix_experience_records_live_by_age</c>, whose leading columns are the
    /// three required fields and then <c>created_at</c>.
    /// </summary>
    private const string SweepSubtreeCandidatesSql =
        $"SELECT {SweepCandidateColumns} FROM {Table} " +
        $"WHERE {SubtreeScopePredicate} AND {LivePredicate} AND created_at < @cutoff " +
        "ORDER BY created_at, experience_id LIMIT @limit";

    /// <summary>The purge function's outcome for a record it erased.</summary>
    private const string PurgedOutcome = "Deleted";

    /// <summary>The purge function's outcome for a record that was already a tombstone.</summary>
    private const string AlreadyPurgedOutcome = "AlreadyDeleted";

    /// <summary>The purge function's outcome for a record that is not in the requesting scope, erased or not.</summary>
    private const string PurgeNotFoundOutcome = "NotFound";

    /// <summary>The purge function's outcome for a record whose revision has moved past the expected one.</summary>
    private const string PurgeStaleOutcome = "StaleRevision";

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    /// <summary>What the four-argument <see cref="GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> means: a caller that keeps what it reads.</summary>
    private static readonly ExperienceReadOptions DeliveryRead = new();

    private readonly NpgsqlDataSource _dataSource;

    private readonly PostgresGrantSupport _grants;

    private readonly ExperienceGrantAuditing? _auditing;

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a store over a host-owned data source. The store never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <param name="onGrantsUnavailable">
    /// Called at most once, when a read first finds <c>agent_experience.experience_grants</c> missing
    /// or unreadable and falls back to the exact-scope predicate. Optional: the fallback happens either
    /// way, and it only ever narrows what a read returns.
    /// </param>
    /// <param name="auditing">
    /// Where to record reads that a grant delivered, and what a failed recording does to the read.
    /// <see langword="null"/> -- the default -- switches auditing off entirely: no extra write, no
    /// extra failure mode, and a deployment behaves exactly as it did before this was added.
    /// </param>
    /// <param name="timeProvider">
    /// The clock this store stamps its own readings from: a lifecycle event's <c>recorded_at</c>, a
    /// tombstone's <c>deleted_at</c>, and the cutoff a retention sweep measures against
    /// <see cref="ExperienceRecord.CreatedAt"/>. Defaults to <see cref="TimeProvider.System"/>. It is
    /// this store's own clock and never a caller's: whether a sharing grant is still live is always the
    /// database's <c>clock_timestamp()</c>, which no host can wind.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceRecordStore(
        NpgsqlDataSource dataSource,
        Action<ExperienceGrantSupportNotice>? onGrantsUnavailable = null,
        ExperienceGrantAuditing? auditing = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _grants = new PostgresGrantSupport(onGrantsUnavailable);
        _auditing = auditing;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordCreateResult> CreateAsync(
        AuthorizationContext authorization,
        ExperienceRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(record);

        var errors = ExperienceRecordValidator.ValidateRecord(record);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, errors);
        }

        if (!authorization.Permits(record.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, NoErrors);
        }

        string payload;
        try
        {
            payload = ExperiencePayload.Serialize(record);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any serialization failure (non-finite doubles, invalid UTF-16, cycles, throwing getters)
            // is a malformed request, never an infrastructure failure.
            return new(
                ExperienceStoreOutcome.Invalid,
                [new StoreValidationError("Attempts", "tool-call arguments could not be serialized to JSON.")]);
        }

        if (ContainsEscapedNul(payload))
        {
            return new(
                ExperienceStoreOutcome.Invalid,
                [new StoreValidationError("Payload", "must not contain the NUL character (U+0000), which PostgreSQL jsonb cannot store.")]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(InsertSql);
            var parameters = command.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", record.ExperienceId));
            parameters.Add(new NpgsqlParameter<Guid>("source_run_id", record.SourceRunId));
            AddScopeParameters(parameters, record.Scope);
            parameters.Add(new NpgsqlParameter<string>("task_id", record.TaskId));
            parameters.Add(new NpgsqlParameter<string>("status", record.Status.ToString()));
            parameters.Add(new NpgsqlParameter<double>("reuse_confidence", record.ReuseConfidence));
            parameters.Add(new NpgsqlParameter<int>("supporting_validations", record.SupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("contradictions", record.Contradictions));
            parameters.Add(new NpgsqlParameter<long>("revision", record.Revision));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", ToStoredTimestamp(record.CreatedAt)));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("updated_at", ToStoredTimestamp(record.UpdatedAt)));
            parameters.Add(new NpgsqlParameter<int>("payload_version", ExperiencePayload.CurrentVersion));
            parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlDbType.Jsonb) { TypedValue = payload });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Created, NoErrors);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && !cancellationToken.IsCancellationRequested)
        {
            // Identical regardless of which scope owns the existing ID: no record data is revealed.
            return new(ExperienceStoreOutcome.Conflict, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "create", cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken) =>
        GetAsync(authorization, scope, experienceId, DeliveryRead, cancellationToken);

    /// <inheritdoc />
    public async Task<ExperienceRecordGetResult> GetAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        ExperienceReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);

        var errors = ExperienceRecordValidator.ValidateGet(scope, experienceId);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        ExperienceRecordGetResult result;
        try
        {
            try
            {
                result = await ReadOneAsync(_grants.Available ? GetSql : GetExactSql, scope, experienceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (_grants.ShouldFallBack(ex, "get", cancellationToken))
            {
                // No grant table, or no permission to read it. Falling back narrows the read to the
                // exact scope; it can never return a record this scope did not already own.
                result = await ReadOneAsync(GetExactSql, scope, experienceId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "get", cancellationToken);
        }

        // Deliberately outside the read's own translation: an audit failure is the host's policy to
        // decide, not a storage failure to raise, and it must never be reported as a failed read.
        return _auditing is null || options.Purpose == ExperienceReadPurpose.ScopeCheck
            ? result
            : await RecordGrantAccessAsync(authorization, scope, options, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExperienceRecordGetResult> ReadOneAsync(
        string sql,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        AddScopeParameters(command.Parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(ExperienceStoreOutcome.NotFound, null, NoErrors);
        }

        return ResultFromRow(reader);
    }

    /// <summary>
    /// What one row of <see cref="GetSql"/>, <see cref="GetExactSql"/>, <see cref="GetManySql"/> or
    /// <see cref="GetManyExactSql"/> means. The single and the batched read both decide through here, so
    /// the tombstone rule and the grant columns are read the same way for both.
    /// </summary>
    private static ExperienceRecordGetResult ResultFromRow(DbDataReader reader)
    {
        var sharedByGrant = ReadSharedByGrant(reader);

        if (ReadDeleted(reader))
        {
            // An erased record. The owner is told so -- the ID is spent and no retry will make it
            // resolve -- but a reader that only reached the row through a grant is told nothing it did
            // not already have: the erasure purges every grant over the record, so a grant that still
            // names a tombstone was written outside this library, and answering it with anything but
            // NotFound would leak the tombstone's existence across a scope boundary.
            return new(
                sharedByGrant ? ExperienceStoreOutcome.NotFound : ExperienceStoreOutcome.Deleted,
                null,
                NoErrors);
        }

        return new(
            ExperienceStoreOutcome.Found,
            ReadRecord(reader),
            NoErrors,
            sharedByGrant,
            ReadPermittingGrant(reader),
            ReadPermittingDisclosure(reader));
    }

    /// <summary>
    /// Appends the access row for a record a grant just delivered, and decides what a failed append
    /// does to the read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a delivery is recorded.</b> A read that found nothing, and an owner reading its own
    /// record, both write nothing: no grant permitted either, so there is no access to attribute to
    /// one. This is also the only reason every stored row can carry a non-null grant.
    /// </para>
    /// <para>
    /// <b>The failure path is the host's policy, not a storage error.</b> Under
    /// <see cref="ExperienceGrantAuditingMode.BestEffort"/> the record is still returned and the
    /// failure is reported; under <see cref="ExperienceGrantAuditingMode.Required"/> the read returns
    /// <see cref="ExperienceStoreOutcome.NotFound"/> -- the same answer as a record no grant permitted,
    /// so failing closed tells a caller nothing it would not otherwise have -- and the failure is
    /// reported just the same. Caller cancellation is never an audit failure and propagates unwrapped.
    /// </para>
    /// <para>
    /// A read that came back shared but unnamed cannot happen through this store's own SQL, because the
    /// same lateral join decides both. If it ever did, the row is attempted anyway with an empty grant
    /// ID, the database's own <c>experience_grant_access_grant_id_not_empty</c> refuses it, and the
    /// configured mode decides the read -- which is the safe direction, rather than quietly delivering
    /// a shared record with no trail.
    /// </para>
    /// </remarks>
    private async Task<ExperienceRecordGetResult> RecordGrantAccessAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceReadOptions options,
        ExperienceRecordGetResult result,
        CancellationToken cancellationToken)
    {
        var auditing = _auditing!;

        if (result is not { Outcome: ExperienceStoreOutcome.Found, Record: { } record, SharedByGrant: true })
        {
            return result;
        }

        var access = GrantAuditing.Access(
            auditing, authorization, scope, options.CorrelationId, record, result.PermittingGrantId, result.GrantDisclosure);

        return await GrantAuditing.RecordAsync(auditing, [access], cancellationToken).ConfigureAwait(false)
            ? result
            : new(ExperienceStoreOutcome.NotFound, null, NoErrors);
    }

    /// <summary>
    /// Reads several records by ID in <em>one</em> statement, each answered exactly as
    /// <see cref="GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/>
    /// answers it: the same exact-scope-or-active-grant predicate (the SQL shares its select, join and
    /// predicate text with the single read), the same tombstone rule, the same permitting grant and
    /// disclosure, and the same grant fallback. Grant-delivered positions are audited in one append of
    /// one row each, under the same mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Round trips.</b> One statement for the reads, and -- only when auditing is wired and at least
    /// one position was delivered through a grant -- one statement for the access rows. The per-record
    /// path it replaces was one statement per record, plus one per grant-delivered record.
    /// </para>
    /// <para>
    /// <b>Required auditing fails closed for the whole batch's grant deliveries.</b> The rows go in one
    /// statement, so they land together or not at all; when they cannot be written under
    /// <see cref="ExperienceGrantAuditingMode.Required"/>, every grant-delivered position becomes
    /// <see cref="ExperienceStoreOutcome.NotFound"/> and every owner-scope position is returned as
    /// read, which is what a failing ledger does to each single read.
    /// </para>
    /// </remarks>
    /// <inheritdoc />
    public async Task<ExperienceRecordGetManyResult> GetManyAsync(
        AuthorizationContext authorization,
        Scope scope,
        IReadOnlyList<Guid> experienceIds,
        ExperienceReadOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(experienceIds);
        ArgumentNullException.ThrowIfNull(options);

        var errors = ExperienceRecordValidator.ValidateGetMany(scope, experienceIds.Count);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // An empty GUID is answered per position, with exactly the errors a single read of it gets, and
        // is never sent to the database -- the single read never sends it either.
        var wanted = experienceIds.Where(id => id != Guid.Empty).Distinct().ToArray();

        Dictionary<Guid, ExperienceRecordGetResult> found;
        if (wanted.Length == 0)
        {
            found = [];
        }
        else
        {
            try
            {
                try
                {
                    found = await ReadManyAsync(_grants.Available ? GetManySql : GetManyExactSql, scope, wanted, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (_grants.ShouldFallBack(ex, "get", cancellationToken))
                {
                    // Exactly the single read's fallback: narrower, never wider.
                    found = await ReadManyAsync(GetManyExactSql, scope, wanted, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
            {
                throw Translate(ex, "get", cancellationToken);
            }
        }

        var notFound = new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, NoErrors);
        var results = new ExperienceRecordGetResult[experienceIds.Count];
        for (var i = 0; i < results.Length; i++)
        {
            var id = experienceIds[i];
            results[i] = id == Guid.Empty
                ? new(ExperienceStoreOutcome.Invalid, null, ExperienceRecordValidator.ValidateGet(scope, id))
                : found.TryGetValue(id, out var result) ? result : notFound;
        }

        if (_auditing is not null && options.Purpose != ExperienceReadPurpose.ScopeCheck)
        {
            await RecordGrantAccessAsync(authorization, scope, options, results, cancellationToken).ConfigureAwait(false);
        }

        return new(ExperienceStoreOutcome.Found, results, NoErrors);
    }

    private async Task<Dictionary<Guid, ExperienceRecordGetResult>> ReadManyAsync(
        string sql,
        Scope scope,
        Guid[] experienceIds,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("experience_ids", experienceIds));
        AddScopeParameters(command.Parameters, scope);

        var found = new Dictionary<Guid, ExperienceRecordGetResult>(experienceIds.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // experience_id is the primary key, so a row per ID at most; the lateral join is LIMIT 1.
            found[reader.GetGuid(0)] = ResultFromRow(reader);
        }

        return found;
    }

    /// <summary>
    /// The batched form of the single read's access-row append: one row per position a grant delivered,
    /// in request order, written in one statement, with the single read's failure policy applied to
    /// every one of those positions. Positions are rewritten in place.
    /// </summary>
    private async Task RecordGrantAccessAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceReadOptions options,
        ExperienceRecordGetResult[] results,
        CancellationToken cancellationToken)
    {
        var auditing = _auditing!;
        var accesses = new List<ExperienceGrantAccess>();
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is { Outcome: ExperienceStoreOutcome.Found, Record: { } record, SharedByGrant: true } delivered)
            {
                accesses.Add(GrantAuditing.Access(
                    auditing, authorization, scope, options.CorrelationId, record, delivered.PermittingGrantId, delivered.GrantDisclosure));
            }
        }

        if (await GrantAuditing.RecordAsync(auditing, accesses, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var refused = new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, NoErrors);
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is { Outcome: ExperienceStoreOutcome.Found, Record: not null, SharedByGrant: true })
            {
                results[i] = refused;
            }
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordQueryResult> QueryAsync(
        AuthorizationContext authorization,
        ExperienceRecordQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var sql = query.Statuses is null
                ? QuerySql + QueryOrderAndLimit
                : QuerySql + QueryStatusPredicate + QueryOrderAndLimit;

            await using var command = _dataSource.CreateCommand(sql);
            AddScopeParameters(command.Parameters, query.Scope);
            if (query.Statuses is not null)
            {
                var statuses = query.Statuses.Distinct().Select(s => s.ToString()).ToArray();
                command.Parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
            }

            command.Parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            var records = new List<ExperienceRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(ReadRecord(reader));
            }

            return new(ExperienceStoreOutcome.Found, records, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "query", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        var errors = ExperienceRecordValidator.ValidateLifecycleEvent(scope, lifecycleEvent, authorization);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Both timestamps are truncated the same way the record's columns are, so a replay's stored
        // OccurredAt compares equal to the value the caller resubmits.
        var occurredAt = ToStoredTimestamp(lifecycleEvent.OccurredAt);
        var recordedAt = ToStoredTimestamp(_timeProvider.GetUtcNow());
        var appliedRevision = lifecycleEvent.ExpectedRevision + 1;

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Pinned, not inherited: under REPEATABLE READ or SERIALIZABLE the same-revision race would
            // abort with a serialization failure instead of matching no row, turning an expected stale
            // revision into an infrastructure failure.
            await using var transaction = await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            // The evidence goes in first, because whether its independence key was free decides whether
            // there is anything else to write at all. An event is append-only once written, so it cannot
            // be corrected afterwards to say the counters did not move after all.
            ConfidenceUpdate? storedConfidence = null;
            if (lifecycleEvent.Confidence is { } submitted)
            {
                var applied = await InsertEvidenceAsync(
                    connection, transaction, scope, Actor(authorization), lifecycleEvent, submitted, recordedAt,
                    appliedRevision, cancellationToken)
                    .ConfigureAwait(false);

                if (applied.Settled is { } settled)
                {
                    // Either a resubmitted evidence ID, which writes nothing and reports the original
                    // outcome, or a duplicate independence key, whose ledger row is the whole of what this
                    // call writes. A duplicate that also moved the status, the revision, or updated_at
                    // would let one observation, replayed under fresh evidence IDs, keep a record
                    // permanently recent -- and would contest a record on evidence already counted.
                    if (settled.Commit)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    return settled.Result;
                }

                storedConfidence = applied.Stored;
            }

            var eventToStore = storedConfidence is null
                ? lifecycleEvent
                : lifecycleEvent with { Confidence = storedConfidence };

            try
            {
                await using var insert = new NpgsqlCommand(InsertEventSql, connection, transaction);
                AddEventParameters(insert.Parameters, authorization, scope, eventToStore, occurredAt, recordedAt, appliedRevision);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, EventPrimaryKey, cancellationToken))
            {
                // A resubmitted event ID. PostgreSQL has aborted the transaction, so nothing this call
                // attempted survives; the stored row then decides replay from conflict.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await CompareStoredEventAsync(connection, scope, lifecycleEvent, occurredAt, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (IsViolationOf(ex, EventRevisionIndex, cancellationToken))
            {
                // A different event already claimed this record revision. The unique index makes the
                // loser of a same-revision race block here and fail once the winner commits, which is a
                // stale revision by another name.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await StaleOrMissingAsync(connection, null, scope, lifecycleEvent.ExperienceRecordId, cancellationToken).ConfigureAwait(false);
            }

            // Deliberately after the insert, so a replay never reaches it: retrying a committed
            // supersession must report the original outcome even once the replacement has itself moved
            // on, which is exactly the retry a lost acknowledgement calls for.
            if (lifecycleEvent.ReplacementExperienceId is { } replacementId
                && await CheckReplacementInTransactionAsync(
                    connection, transaction, scope, lifecycleEvent.ExperienceRecordId, replacementId, cancellationToken)
                    .ConfigureAwait(false) is { } refusal)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return refusal;
            }

            // Only a counted update writes the three confidence columns. A duplicate submission takes the
            // statement that leaves them alone, so "the counters did not move" is a fact about the SQL
            // that ran, not a value that happened to be equal.
            var counted = storedConfidence is { Counted: true };

            int updated;
            try
            {
                await using var update = new NpgsqlCommand(
                    counted ? UpdateProjectionWithConfidenceSql : UpdateProjectionSql, connection, transaction);
                var parameters = update.Parameters;
                parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
                parameters.Add(new NpgsqlParameter<string>("current_status", lifecycleEvent.CurrentStatus.ToString()));
                parameters.Add(NullableText("prior_status", lifecycleEvent.PriorStatus?.ToString()));
                parameters.Add(new NpgsqlParameter<long>("expected_revision", lifecycleEvent.ExpectedRevision));
                parameters.Add(new NpgsqlParameter<long>("applied_revision", appliedRevision));
                parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
                if (counted)
                {
                    parameters.Add(new NpgsqlParameter<double>("new_reuse_confidence", storedConfidence!.NewReuseConfidence));
                    parameters.Add(new NpgsqlParameter<int>("new_supporting_validations", storedConfidence.NewSupportingValidations));
                    parameters.Add(new NpgsqlParameter<int>("new_contradictions", storedConfidence.NewContradictions));
                }

                AddScopeParameters(parameters, scope);
                updated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (!cancellationToken.IsCancellationRequested
                && ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                // Another writer got there first. However the server is configured, losing that race is an
                // expected condition, not an infrastructure failure.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await StaleOrMissingAsync(connection, null, scope, lifecycleEvent.ExperienceRecordId, cancellationToken).ConfigureAwait(false);
            }

            if (updated == 0)
            {
                // The record is not in this scope, its revision has moved on, or it is not in the status
                // the event was decided against. The row is re-read inside the same transaction that is
                // about to be rolled back, so the event insert above never reaches the log.
                var current = await ReadRevisionAndStatusAsync(connection, transaction, scope, lifecycleEvent.ExperienceRecordId, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                if (current is not { } record)
                {
                    return new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
                }

                if (record.Deleted)
                {
                    // The record was erased. A tombstone is terminal, so this is not a race to retry:
                    // the event this call appended is rolled back with everything else.
                    return new(ExperienceStoreOutcome.Deleted, record.Revision, null, NoErrors);
                }

                return record.Revision != lifecycleEvent.ExpectedRevision
                    ? new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors)
                    // Scope and revision both matched, so the prior-status guard is what rejected it.
                    : new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors, storedConfidence);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            // Nothing this call wrote is visible unless the commit itself succeeded and only its
            // acknowledgement was lost; retrying the identical event then replays instead of reapplying.
            throw Translate(ex, "lifecycle commit", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceRecordHistoryResult> GetHistoryAsync(
        AuthorizationContext authorization,
        ExperienceRecordHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Scope, $"{nameof(query)}.{nameof(query.Scope)}");

        var errors = ExperienceRecordValidator.ValidateHistoryQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, [], errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, [], NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(HistorySql);
            var parameters = command.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", query.ExperienceId));
            AddScopeParameters(parameters, query.Scope);
            parameters.Add(new NpgsqlParameter("start_after_revision", NpgsqlDbType.Bigint)
            {
                Value = query.StartAfterRevision is { } cursor ? cursor : DBNull.Value,
            });
            parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // No record in this scope: indistinguishable from one that exists elsewhere.
                return new(ExperienceStoreOutcome.NotFound, 0, [], NoErrors);
            }

            if (!reader.IsDBNull(HistoryDeletedAtOrdinal))
            {
                // An erased record has no history left to page: its events were removed with its
                // payload. Reported as Deleted rather than as an empty page, which would say the record
                // is alive and has nothing to show.
                return new(ExperienceStoreOutcome.Deleted, 0, [], NoErrors);
            }

            var revision = ReadRevision(reader, HistoryRevisionOrdinal);

            var events = new List<StoredLifecycleEvent>();
            if (!reader.IsDBNull(0))
            {
                // A null event_id is the outer join's single "record with no events" row -- which is
                // also what an exhausted cursor produces, and deliberately so: it keeps the record
                // Found with nothing left to show rather than making it look missing.
                do
                {
                    events.Add(ReadEvent(reader));
                }
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            return new(
                ExperienceStoreOutcome.Found,
                revision,
                events,
                NoErrors,
                events.Count > 0 ? events[^1].AppliedRevision : null);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "history", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        Guid replacementExperienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateSupersessionCheck(scope, experienceId, replacementExperienceId);
        if (errors.Count > 0)
        {
            return new(ExperienceSupersessionOutcome.Invalid, null, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceSupersessionOutcome.Denied, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await ReadSupersessionAsync(connection, null, scope, experienceId, replacementExperienceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "supersession check", cancellationToken);
        }
    }

    /// <summary>
    /// Erases one record: its payload and every stored row that named it, leaving a payload-free
    /// tombstone under the same ID. This is the only destructive operation this library has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is erased, and what is left.</b> The evidence ledger, the exposure rows, the grants and
    /// their audit events, the lifecycle history, and the embedding are removed. The record row survives
    /// carrying only <see cref="ExperienceRecord.ExperienceId"/>, the six scope columns,
    /// <see cref="ExperienceRecord.Revision"/>, the deletion timestamp, a tombstone status, and a fixed
    /// <c>task_id</c> placeholder. <c>experience_grant_access</c> rows are deliberately kept: they name
    /// a grant and a principal, carry no payload, and are the answer to "who read this before it was
    /// deleted". See the package README for the retained list, stated exhaustively.
    /// </para>
    /// <para>
    /// <b>One transaction, one code path.</b> Every step runs inside <c>0010</c>'s
    /// <c>purge_experience_record</c> function, in the order that script pins, under a
    /// transaction-scoped marker the append-only guards recognise. The guards are never disabled and
    /// never widened for another session. That buys atomicity and a single path -- not a privilege
    /// boundary; the README says exactly what it does not bind.
    /// </para>
    /// <para>
    /// <b>Foreign scope is indistinguishable from absent</b>, exactly as it is everywhere else: both are
    /// <see cref="ExperienceStoreOutcome.NotFound"/>, decided by one statement's predicate rather than
    /// by a branch here. <b>Deleting twice</b> is <see cref="ExperienceStoreOutcome.Deleted"/> again,
    /// with nothing written.
    /// </para>
    /// <para>
    /// <b>This is not on <see cref="IExperienceRecordStore"/>.</b> Erasure is a capability of this
    /// adapter, not of the port: Core never deletes, and a port method would oblige every
    /// implementation -- including the in-memory doubles hosts write for tests -- to promise an erasure
    /// it cannot actually perform.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the record must lie in. Never treated as authority.</param>
    /// <param name="experienceId">The record to erase. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Deleted"/>, <see cref="ExperienceStoreOutcome.NotFound"/>, <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    public Task<ExperienceRecordDeleteResult> DeleteAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken) =>
        DeleteAsync(authorization, scope, experienceId, expectedRevision: null, cancellationToken);

    /// <summary>
    /// The same erasure, refused unless the record is still at <paramref name="expectedRevision"/>.
    /// </summary>
    /// <remarks>
    /// The revision guard, the scope predicate, and the existence check are one statement inside the
    /// purge function, so a stale revision, a foreign scope, and a missing record are all "no row" --
    /// and only the scope that owns the record is told which. Pass <see langword="null"/> to erase
    /// whatever revision the record is at, which is what the retention sweep does: an age-based deletion
    /// is not racing a writer for a particular version.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the record must lie in. Never treated as authority.</param>
    /// <param name="experienceId">The record to erase. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="expectedRevision">The revision the record must still be at, or <see langword="null"/> for none. Must not be negative.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Deleted"/>, <see cref="ExperienceStoreOutcome.StaleRevision"/>
    /// (carrying the record's current revision), <see cref="ExperienceStoreOutcome.NotFound"/>,
    /// <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    public async Task<ExperienceRecordDeleteResult> DeleteAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        // Opened before the arguments are checked, exactly as Core's operations are, so even a refused
        // or malformed erasure is counted. The record ID is the only identifier written: the tombstone
        // keeps it, and nothing the erasure removed ever reaches telemetry.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.Delete);
        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.ExperienceIdAttribute, experienceId.ToString("D"));

        ExperienceRecordDeleteResult result;
        try
        {
            result = await DeleteCoreAsync(authorization, scope, experienceId, expectedRevision, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    private async Task<ExperienceRecordDeleteResult> DeleteCoreAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateDelete(scope, experienceId, expectedRevision);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, errors);
        }

        if (!authorization.Permits(scope))
        {
            // Fail-closed, and before any connection opens: nothing is erased and nothing is read.
            return new(ExperienceStoreOutcome.Denied, 0, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var (result, _) = await PurgeAsync(connection, scope, experienceId, expectedRevision, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            throw Translate(ex, "delete", cancellationToken);
        }
    }

    /// <summary>
    /// Erases the records in exactly one scope that are older than <paramref name="retentionAge"/>, in
    /// one bounded batch. The same as
    /// <see cref="SweepExpiredAsync(AuthorizationContext, Scope, TimeSpan, int, ScopeMatch, CancellationToken)"/>
    /// with <see cref="ScopeMatch.Exact"/>.
    /// </summary>
    /// <remarks>
    /// <b>This sweeps the exact scope and no scope under it.</b> A sweep of <c>(tenant, app, project)</c>
    /// with no team, agent or user reaches only the records stored with all three of those null, and
    /// reports <see cref="ExperienceRetentionSweepResult.MoreRemain"/> <see langword="false"/> while
    /// team-, agent- and user-scoped records under it survive. A host whose policy covers everything
    /// under a scope passes <see cref="ScopeMatch.Subtree"/> to the other overload.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope to sweep, matched field for field. Never treated as authority.</param>
    /// <param name="retentionAge">How long a record may be kept, measured from <see cref="ExperienceRecord.CreatedAt"/>. Must be strictly positive.</param>
    /// <param name="batchSize">The most records this call may erase, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="cancellationToken">Cancels the operation between records; records already erased stay erased, and the count comes back on the result rather than being lost.</param>
    /// <returns>See the other overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceRetentionSweepInterruptedException">A storage failure stopped the batch part-way; the count of what was erased is on the exception.</exception>
    public Task<ExperienceRetentionSweepResult> SweepExpiredAsync(
        AuthorizationContext authorization,
        Scope scope,
        TimeSpan retentionAge,
        int batchSize,
        CancellationToken cancellationToken) =>
        SweepExpiredAsync(authorization, scope, retentionAge, batchSize, ScopeMatch.Exact, cancellationToken);

    /// <summary>
    /// Erases the records in a scope -- or, with <see cref="ScopeMatch.Subtree"/>, in that scope and
    /// every scope beneath it -- that are older than <paramref name="retentionAge"/>, in one bounded
    /// batch, through exactly the same erasure as <see cref="DeleteAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no default retention and no timer.</b> Nothing expires unless a host calls this with
    /// a positive age, and this library ships no scheduler, no background service, and no hosted
    /// service: when a sweep runs is the host's decision, made with the host's own scheduling, because
    /// only the host knows what its data-retention obligations are.
    /// </para>
    /// <para>
    /// <b>Bounded, and resumable.</b> At most <paramref name="batchSize"/> records are erased per call,
    /// oldest <see cref="ExperienceRecord.CreatedAt"/> first across everything the match reaches, and
    /// <see cref="ExperienceRetentionSweepResult.MoreRemain"/> says whether another call would find
    /// more -- across the whole subtree under <see cref="ScopeMatch.Subtree"/>. Each record is erased in
    /// its own transaction, in its own exact stored scope, so an interrupted sweep leaves every record
    /// it reached wholly erased and every record it did not reach wholly untouched.
    /// <see cref="ExperienceRetentionSweepResult.DeletedCount"/> counts only the records this call
    /// erased: one another caller erased first is not counted again.
    /// </para>
    /// <para>
    /// The cutoff is measured on this store's <see cref="TimeProvider"/> against the record's stored
    /// <see cref="ExperienceRecord.CreatedAt"/>, never against <see cref="ExperienceRecord.UpdatedAt"/>:
    /// age is how long the library has held the data, and a record that is read, ranked, or re-scored
    /// does not thereby become younger.
    /// </para>
    /// <para>
    /// <b><see cref="ScopeMatch.Exact"/> sweeps the exact scope and no scope under it, and that is the
    /// one failure mode here that looks like success.</b> A sweep of <c>(tenant, app, project)</c> with
    /// no team, agent or user reaches only the records stored with all three of those null; records the
    /// same project holds under a team, an agent or a user are a <em>different</em> scope and are not
    /// swept, not counted, and not reflected in <see cref="ExperienceRetentionSweepResult.MoreRemain"/>.
    /// <see cref="ScopeMatch.Subtree"/> is how a host whose policy covers everything under a scope says
    /// so. What it reaches is defined exactly on <see cref="ScopeMatch"/>: never another tenant,
    /// application or project, never an ancestor or a sibling of <paramref name="scope"/>.
    /// </para>
    /// <para>
    /// <b>Authorization is decided on <paramref name="scope"/>, and that covers the subtree.</b> An
    /// <see cref="AuthorizationContext"/> that permits the root has no bound on any field the root
    /// leaves null, so it permits every scope beneath it; one bounded to a team does not permit a
    /// project root at all, and is <see cref="ExperienceStoreOutcome.Denied"/> before any connection
    /// opens. Every candidate is checked again, against the root and the authorization, before it is
    /// erased.
    /// </para>
    /// <para>
    /// <b>Stopping early.</b> Cancelling between records returns what the call had already erased, with
    /// <see cref="ExperienceRetentionSweepResult.Interrupted"/> set, rather than throwing away the
    /// count. A storage failure part-way through throws
    /// <see cref="ExperienceRetentionSweepInterruptedException"/>, which carries the same partial
    /// result and is an <see cref="ExperienceStoreException"/> like any other storage failure here.
    /// </para>
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The scope to sweep, or the root of the subtree to sweep. Never treated as authority.</param>
    /// <param name="retentionAge">How long a record may be kept, measured from <see cref="ExperienceRecord.CreatedAt"/>. Must be strictly positive.</param>
    /// <param name="batchSize">The most records this call may erase, from <see cref="MinSweepBatchSize"/> to <see cref="MaxSweepBatchSize"/>.</param>
    /// <param name="match">Whether to sweep <paramref name="scope"/> alone or everything beneath it too. A value the enum does not define is <see cref="ExperienceStoreOutcome.Invalid"/>.</param>
    /// <param name="cancellationToken">Cancels the operation between records; records already erased stay erased, and the count comes back on the result rather than being lost.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Deleted"/> when the sweep ran (possibly erasing nothing, and
    /// possibly stopping early -- see <see cref="ExperienceRetentionSweepResult.Interrupted"/>),
    /// <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceRetentionSweepInterruptedException">A storage failure stopped the batch part-way; the count of what was erased is on the exception.</exception>
    public async Task<ExperienceRetentionSweepResult> SweepExpiredAsync(
        AuthorizationContext authorization,
        Scope scope,
        TimeSpan retentionAge,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken)
    {
        // One span for the whole batch, never one per record: the records are erased through PurgeAsync,
        // not through DeleteAsync, and a span attribute is not a place for a list that grows with the
        // batch. What reaches the trace is how many were erased, whether the batch stopped early, and
        // how wide it was asked to reach -- never which scope.
        using var operation = ErasureDiagnostics.Start(ErasureDiagnostics.RetentionSweep);
        ErasureDiagnostics.TagScopeMatch(operation, match);

        ExperienceRetentionSweepResult result;
        try
        {
            result = await SweepExpiredCoreAsync(authorization, scope, retentionAge, batchSize, match, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExperienceRetentionSweepInterruptedException ex)
        {
            // How much a failed sweep irreversibly erased is the one fact a compliance log needs, so it
            // reaches the trace as well as the exception.
            TagSweep(operation, ex.Partial);
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }
        catch (Exception ex)
        {
            ErasureDiagnostics.Faulted(operation, ex, cancellationToken);
            throw;
        }

        TagSweep(operation, result);
        ErasureDiagnostics.Succeeded(operation, result.Outcome);
        return result;
    }

    /// <summary>Writes what a sweep did -- a count and a flag, never which records -- onto its span.</summary>
    private static void TagSweep(in ErasureTrace operation, ExperienceRetentionSweepResult result)
    {
        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.ErasedCountAttribute, result.DeletedCount);
        ErasureDiagnostics.Tag(operation, ErasureDiagnostics.InterruptedAttribute, result.Interrupted);
    }

    private async Task<ExperienceRetentionSweepResult> SweepExpiredCoreAsync(
        AuthorizationContext authorization,
        Scope scope,
        TimeSpan retentionAge,
        int batchSize,
        ScopeMatch match,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateRetentionSweep(scope, retentionAge, batchSize, match);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, 0, false, errors);
        }

        if (!authorization.Permits(scope))
        {
            return new(ExperienceStoreOutcome.Denied, 0, false, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var cutoff = ToStoredTimestamp(_timeProvider.GetUtcNow() - retentionAge);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            var candidates = new List<(Guid ExperienceId, Scope Scope)>(batchSize + 1);
            var candidatesSql = match == ScopeMatch.Subtree ? SweepSubtreeCandidatesSql : SweepCandidatesSql;
            await using (var command = new NpgsqlCommand(candidatesSql, connection))
            {
                AddScopeParameters(command.Parameters, scope);
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));

                // One row beyond the batch, so "more remain" is read off the same statement rather than
                // from a second count that could disagree with it.
                command.Parameters.Add(new NpgsqlParameter<int>("limit", batchSize + 1));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidates.Add((
                        reader.GetGuid(0),
                        new Scope(
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6))));
                }
            }

            var moreRemain = candidates.Count > batchSize;

            // Declared outside the loop, and read again by both handlers below, because the number of
            // records this call irreversibly erased is the one fact a compliance log needs and it must
            // not be lost just because the batch stopped early.
            var deleted = 0;
            try
            {
                foreach (var (experienceId, candidateScope) in candidates.Take(batchSize))
                {
                    // Defence in depth over the page the database returned: the candidate must lie at or
                    // beneath the root this call was authorized for, and the authorization must permit
                    // it on its own. Both hold by construction (see ScopeMatch), so neither can fail
                    // unless the predicate above is wrong. If it is, the sweep stops loudly without
                    // erasing that record: silently skipping it would leave it at the head of every later
                    // page, so every call would erase nothing and report MoreRemain forever.
                    if (!IsAtOrBeneath(candidateScope, scope, match) || !authorization.Permits(candidateScope))
                    {
                        throw new ExperienceRetentionSweepInterruptedException(
                            new(ExperienceStoreOutcome.Deleted, deleted, true, NoErrors, Interrupted: true),
                            new ExperienceStoreException(
                                "A retention sweep candidate lay outside the requested scope or authorization; "
                                + "the sweep stopped without erasing it."));
                    }

                    // No expected revision: a sweep deletes a record for its age, not for the version it
                    // happened to be at when the page was read. The candidate's own exact scope, so the
                    // purge function's scope guard is the same exact-match guard DeleteAsync relies on.
                    var (_, erasedNow) = await PurgeAsync(connection, candidateScope, experienceId, expectedRevision: null, cancellationToken)
                        .ConfigureAwait(false);

                    // Only what this call erased. A record another sweep or delete erased first comes back
                    // as already a tombstone, and counting it here too would let two racing sweeps report
                    // more erasures than there were records.
                    if (erasedNow)
                    {
                        deleted++;
                    }
                }
            }
            catch (Exception ex) when (ex is not ExperienceStoreException && cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation, however the driver reported it -- an OperationCanceledException,
                // or the server's own query_canceled for a statement that was already running. Decided
                // by the token exactly as Translate decides it, so the two never disagree.
                //
                // The host asked the sweep to stop, which is a normal way to run one: each record was
                // erased in its own transaction, so what is erased is erased and what is left is whole.
                // Returned rather than thrown, because a cancelled sweep that threw away its count would
                // leave a host unable to say how much of its data it had just destroyed. MoreRemain is
                // true regardless of what the page said: at least the record it stopped on is still there.
                return new(ExperienceStoreOutcome.Deleted, deleted, true, NoErrors, Interrupted: true);
            }
            catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
            {
                // A failure, not a request. Thrown -- a host must not read this as a sweep that ran --
                // but thrown carrying the count, as an ExperienceStoreException like every other storage
                // failure here, so nothing that already catches those has to change.
                throw new ExperienceRetentionSweepInterruptedException(
                    new(ExperienceStoreOutcome.Deleted, deleted, true, NoErrors, Interrupted: true),
                    Translate(ex, "retention sweep", cancellationToken));
            }

            return new(ExperienceStoreOutcome.Deleted, deleted, moreRemain, NoErrors);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex, cancellationToken))
        {
            // Only the candidate read can reach this now, and it erases nothing.
            throw Translate(ex, "retention sweep", cancellationToken);
        }
    }

    /// <summary>
    /// Runs the purge function and maps its outcome. One statement, so the whole erasure is one
    /// transaction whether or not the caller opened one.
    /// </summary>
    private async Task<(ExperienceRecordDeleteResult Result, bool ErasedNow)> PurgeAsync(
        NpgsqlConnection connection,
        Scope scope,
        Guid experienceId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PurgeSql, connection);
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        AddScopeParameters(parameters, scope);
        parameters.Add(new NpgsqlParameter("expected_revision", NpgsqlDbType.Bigint)
        {
            Value = expectedRevision is { } revision ? revision : DBNull.Value,
        });
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("deleted_at", ToStoredTimestamp(_timeProvider.GetUtcNow())));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The function always returns exactly one row; treat the impossible case the way a missing
            // record is treated, which writes nothing and claims nothing.
            return (new(ExperienceStoreOutcome.NotFound, 0, NoErrors), false);
        }

        var outcome = reader.GetString(0);
        var currentRevision = reader.GetInt64(1);

        return outcome switch
        {
            // Erased now, or erased earlier: deleting twice is a success that touches nothing. Only the
            // first is this call's erasure, which is what a sweep counts.
            PurgedOutcome => (new(ExperienceStoreOutcome.Deleted, currentRevision, NoErrors), true),
            AlreadyPurgedOutcome => (new(ExperienceStoreOutcome.Deleted, currentRevision, NoErrors), false),
            PurgeStaleOutcome => (new(ExperienceStoreOutcome.StaleRevision, currentRevision, NoErrors), false),
            PurgeNotFoundOutcome => (new(ExperienceStoreOutcome.NotFound, 0, NoErrors), false),
            _ => throw new ExperienceStoreException("The erasure function reported an unrecognized outcome."),
        };
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> itself (<see cref="ScopeMatch.Exact"/>),
    /// or at or beneath it (<see cref="ScopeMatch.Subtree"/>), exactly as <see cref="ScopeMatch"/> defines
    /// it: the three required fields equal, and each optional field of the root either null or equal.
    /// Ordinal throughout, like <see cref="AuthorizationContext.Permits"/>.
    /// </summary>
    internal static bool IsAtOrBeneath(Scope candidate, Scope root, ScopeMatch match)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(root);

        var required = string.Equals(candidate.TenantId, root.TenantId, StringComparison.Ordinal)
            && string.Equals(candidate.ApplicationId, root.ApplicationId, StringComparison.Ordinal)
            && string.Equals(candidate.ProjectId, root.ProjectId, StringComparison.Ordinal);

        return match switch
        {
            ScopeMatch.Exact => required
                && string.Equals(candidate.TeamId, root.TeamId, StringComparison.Ordinal)
                && string.Equals(candidate.AgentId, root.AgentId, StringComparison.Ordinal)
                && string.Equals(candidate.UserId, root.UserId, StringComparison.Ordinal),
            ScopeMatch.Subtree => required
                && (root.TeamId is null || string.Equals(candidate.TeamId, root.TeamId, StringComparison.Ordinal))
                && (root.AgentId is null || string.Equals(candidate.AgentId, root.AgentId, StringComparison.Ordinal))
                && (root.UserId is null || string.Equals(candidate.UserId, root.UserId, StringComparison.Ordinal)),
            _ => false,
        };
    }

    /// <summary>
    /// Re-decides the replacement rules inside the commit transaction, with both record rows locked, and
    /// returns the refusal when they no longer hold. <see langword="null"/> means the supersession may
    /// proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the authoritative check, not a second opinion.
    /// <see cref="CheckSupersessionAsync"/> answers the same question on its own connection, which makes
    /// it useful for telling a caller <em>why</em> before it tries -- but an answer read outside this
    /// transaction is only a prediction. Two supersessions naming each other ("A by B" and "B by A")
    /// each pass such a prediction and would both commit the cycle the contract refuses. Running the
    /// check here, after locking both rows in a deterministic order, is what makes "a cycle is refused"
    /// and "an ineligible replacement is refused" true under concurrency: the loser either sees the
    /// winner's event or waits for it.
    /// </para>
    /// <para>
    /// Eligibility is read from <see cref="ExperienceStatuses.EligibleForReuse"/> rather than decided
    /// here, so the rule the transaction enforces is the same list retrieval and indexing apply.
    /// </para>
    /// </remarks>
    private static async Task<ExperienceLifecycleCommitResult?> CheckReplacementInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Scope scope,
        Guid experienceId,
        Guid replacementId,
        CancellationToken cancellationToken)
    {
        await using (var locks = new NpgsqlCommand(LockSupersessionRowsSql, connection, transaction))
        {
            locks.Parameters.Add(new NpgsqlParameter<Guid[]>("lock_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = [experienceId, replacementId],
            });
            await locks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var check = await ReadSupersessionAsync(connection, transaction, scope, experienceId, replacementId, cancellationToken)
            .ConfigureAwait(false);

        // The record itself is left to the projection update, which reports NotFound in the one way every
        // other operation does.
        if (check.Outcome is ExperienceSupersessionOutcome.RecordNotFound or ExperienceSupersessionOutcome.Allowed
            && check.ReplacementStatus is { } status
            && ExperienceStatuses.IsEligibleForReuse(status))
        {
            return null;
        }

        return new(ExperienceStoreOutcome.ReplacementNotAllowed, 0, check.ReplacementStatus, NoErrors);
    }

    /// <summary>
    /// Runs the supersession statement, optionally inside a transaction, and turns its three values into
    /// an outcome. Shared by the read-only port operation and the in-transaction gate, so the two can
    /// never disagree about what a cycle is.
    /// </summary>
    private static async Task<ExperienceSupersessionCheckResult> ReadSupersessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        Guid replacementId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SupersessionCheckSql, connection, transaction);
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        parameters.Add(new NpgsqlParameter<Guid>("replacement_id", replacementId));
        AddScopeParameters(parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // The statement always produces exactly one row; treat the impossible case as "no record".
            return new(ExperienceSupersessionOutcome.RecordNotFound, null, NoErrors);
        }

        if (reader.IsDBNull(0))
        {
            return new(ExperienceSupersessionOutcome.RecordNotFound, null, NoErrors);
        }

        if (reader.IsDBNull(1))
        {
            // Missing, or in another scope: identical either way, so nothing about it is revealed.
            return new(ExperienceSupersessionOutcome.ReplacementNotFound, null, NoErrors);
        }

        var replacementStatus = ReadStoredStatus(reader, 1);

        // The cycle is reported before the status, so a replacement that is both eligible and on a
        // closing chain is still refused for the reason that actually matters.
        return reader.GetBoolean(2)
            ? new(ExperienceSupersessionOutcome.Cycle, replacementStatus, NoErrors)
            : new(ExperienceSupersessionOutcome.Allowed, replacementStatus, NoErrors);
    }

    /// <summary>
    /// Decides a resubmitted <see cref="LifecycleEvent.EventId"/>: byte-for-byte the same event (scope
    /// included) is the original commit replayed, so its original outcome is returned and nothing is
    /// written; any difference is a <see cref="ExperienceStoreOutcome.Conflict"/>. The comparison is
    /// identical whichever scope owns the stored event, so it reveals no event data.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> CompareStoredEventAsync(
        NpgsqlConnection connection,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectEventSql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", lifecycleEvent.EventId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Events are never deleted, so the row that just collided cannot vanish. Treat the
            // impossible case as a conflict rather than writing anything.
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        var stored = ReadEvent(reader);
        var storedScope = ReadEventScope(reader);
        var appliedRevision = stored.AppliedRevision;

        // Record equality compares every field of the event -- the replacement ID included, so a replay
        // that names a different replacement is a conflict rather than a silent no-op. The scope is
        // compared alongside it. The revision reported is the one the original commit produced, not the
        // record's current one.
        var resubmitted = lifecycleEvent with { OccurredAt = occurredAt };
        return stored.Event == resubmitted && storedScope == scope
            ? new(ExperienceStoreOutcome.Committed, appliedRevision, null, NoErrors, stored.Event.Confidence)
            : new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
    }

    /// <summary>
    /// Writes the evidence row, and decides -- from the database, inside the commit transaction -- which
    /// of the three things this submission is: the first for its independence key, a later one for a key
    /// already counted, or a resubmission of an evidence ID that is already stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first insert claims the key by writing <c>counted = true</c>, which the partial unique index
    /// admits exactly once per record and key. It runs under a savepoint because losing that race is an
    /// <em>expected</em> outcome that the commit has to survive: a unique violation aborts the whole
    /// transaction otherwise, and the transaction is what is supposed to record the duplicate.
    /// </para>
    /// <para>
    /// On the violation the statement is undone and the same submission is written again with
    /// <c>counted = false</c> -- and that row is <em>all</em> this call writes. No event, no counters, no
    /// status, no revision, no <c>updated_at</c>. Each of those would be a way for one observation,
    /// replayed under fresh evidence IDs, to keep changing a record the independence rule has already
    /// declared it finished with: refreshing <c>updated_at</c> would keep it permanently recent for
    /// ranking and permanently un-expired, and writing the status would contest it on evidence that was
    /// not counted. An event is impossible as well as unwanted -- it must claim
    /// <c>expected_revision + 1</c>, and claiming a revision the record never reaches would wedge every
    /// later commit against the unique index on <c>(experience_id, applied_revision)</c>.
    /// </para>
    /// <para>
    /// Because that path writes no revision-guarded statement of its own, it re-reads the record
    /// <c>FOR UPDATE</c> first: the row it records has to say what the record actually looks like, and the
    /// usual refusals (gone, moved on, not in this status) still have to be reported rather than silently
    /// recorded against stale values.
    /// </para>
    /// <para>
    /// Whether this store's own reading of the key agrees with the database's is never asked: the key is
    /// a generated column, so the only writer who decides it is the database.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <c>Stored</c> is the payload the event must record, and is <see langword="null"/> when
    /// <c>Settled</c> is set. <c>Settled</c> is the outcome to return instead of writing an event and a
    /// projection: <c>Commit</c> says whether the transaction holds a ledger row worth keeping (a
    /// duplicate) or nothing at all (a resubmitted evidence ID, or a refusal).
    /// </returns>
    private static async Task<(ConfidenceUpdate? Stored, (ExperienceLifecycleCommitResult Result, bool Commit)? Settled)> InsertEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Scope scope,
        string? actor,
        LifecycleEvent lifecycleEvent,
        ConfidenceUpdate submitted,
        DateTimeOffset recordedAt,
        long appliedRevision,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync($"SAVEPOINT {EvidenceSavepoint}", cancellationToken).ConfigureAwait(false);

        try
        {
            await InsertOneAsync(submitted, lifecycleEvent.EventId, appliedRevision, lifecycleEvent.CurrentStatus)
                .ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsViolationOf(ex, EvidenceIndependenceIndex, cancellationToken))
        {
            await ExecuteAsync($"ROLLBACK TO SAVEPOINT {EvidenceSavepoint}", CancellationToken.None).ConfigureAwait(false);
            return (null, await RecordDuplicateAsync().ConfigureAwait(false));
        }
        catch (PostgresException ex) when (IsViolationOf(ex, EvidencePrimaryKey, cancellationToken))
        {
            return (null, (await ReplayEvidenceAsync().ConfigureAwait(false), Commit: false));
        }

        await ExecuteAsync($"RELEASE SAVEPOINT {EvidenceSavepoint}", cancellationToken).ConfigureAwait(false);
        return (submitted, null);

        async Task ExecuteAsync(string sql, CancellationToken token)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        async Task InsertOneAsync(ConfidenceUpdate update, Guid? eventId, long revision, ExperienceStatus status)
        {
            await using var insert = new NpgsqlCommand(InsertEvidenceSql, connection, transaction);
            var parameters = insert.Parameters;
            parameters.Add(new NpgsqlParameter<Guid>("evidence_id", update.EvidenceId));
            parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
            parameters.Add(NullableUuid("event_id", eventId));
            parameters.Add(new NpgsqlParameter<string>("confidence_kind", update.Kind.ToString()));
            parameters.Add(new NpgsqlParameter<string>("confidence_source", update.Source.ToString()));
            parameters.Add(new NpgsqlParameter<Guid>("confidence_run_id", update.RunId));
            parameters.Add(NullableUuid("confidence_verification_round_id", update.VerificationRoundId));
            parameters.Add(NullableText("confidence_reviewer_identity", update.ReviewerIdentity));
            parameters.Add(new NpgsqlParameter<bool>("counted", update.Counted));
            parameters.Add(NullableText("actor", actor));
            parameters.Add(new NpgsqlParameter<string>("confidence_rule_version", update.RuleVersion));
            parameters.Add(NullableText("confidence_detail", update.Detail));
            parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
            parameters.Add(new NpgsqlParameter<long>("applied_revision", revision));
            parameters.Add(new NpgsqlParameter<string>("applied_status", status.ToString()));
            parameters.Add(new NpgsqlParameter<double>("prior_reuse_confidence", update.PriorReuseConfidence));
            parameters.Add(new NpgsqlParameter<double>("new_reuse_confidence", update.NewReuseConfidence));
            parameters.Add(new NpgsqlParameter<int>("prior_supporting_validations", update.PriorSupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("new_supporting_validations", update.NewSupportingValidations));
            parameters.Add(new NpgsqlParameter<int>("prior_contradictions", update.PriorContradictions));
            parameters.Add(new NpgsqlParameter<int>("new_contradictions", update.NewContradictions));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        async Task<(ExperienceLifecycleCommitResult Result, bool Commit)> RecordDuplicateAsync()
        {
            var current = await ReadRevisionAndStatusAsync(
                connection, transaction, scope, lifecycleEvent.ExperienceRecordId, cancellationToken, forUpdate: true)
                .ConfigureAwait(false);

            if (current is not { } record)
            {
                return (new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors), Commit: false);
            }

            if (record.Deleted)
            {
                // Nothing is recorded against a tombstone -- not even a duplicate submission's ledger
                // row, which would put the erased record's ID back into a table the erasure emptied.
                return (new(ExperienceStoreOutcome.Deleted, record.Revision, null, NoErrors), Commit: false);
            }

            if (record.Revision != lifecycleEvent.ExpectedRevision)
            {
                return (new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors), Commit: false);
            }

            if (lifecycleEvent.PriorStatus is { } prior && record.Status != prior)
            {
                return (new(ExperienceStoreOutcome.StatusMismatch, record.Revision, record.Status, NoErrors), Commit: false);
            }

            var recordedOnly = submitted.AsRecordedOnly();
            try
            {
                // Not a tombstone, so the status decoded: the branch above returned for the one case
                // where it could not.
                await InsertOneAsync(recordedOnly, eventId: null, record.Revision, record.Status!.Value).ConfigureAwait(false);
            }
            catch (PostgresException pk) when (IsViolationOf(pk, EvidencePrimaryKey, cancellationToken))
            {
                return (await ReplayEvidenceAsync().ConfigureAwait(false), Commit: false);
            }

            await ExecuteAsync($"RELEASE SAVEPOINT {EvidenceSavepoint}", cancellationToken).ConfigureAwait(false);

            // The record is untouched, so its revision and status are reported exactly as they were read.
            return (
                new(ExperienceStoreOutcome.Committed, record.Revision, record.Status, NoErrors, recordedOnly),
                Commit: true);
        }

        async Task<ExperienceLifecycleCommitResult> ReplayEvidenceAsync()
        {
            // The failed statement has left the transaction unusable; undoing it to the savepoint makes
            // the connection readable again so the stored row can be compared. The caller rolls the
            // whole transaction back afterwards, so nothing this call attempted survives either way.
            await ExecuteAsync($"ROLLBACK TO SAVEPOINT {EvidenceSavepoint}", CancellationToken.None).ConfigureAwait(false);
            return await CompareStoredEvidenceAsync(connection, transaction, scope, lifecycleEvent, submitted, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decides a resubmitted <see cref="ConfidenceUpdate.EvidenceId"/>: the same evidence about the same
    /// observation is the original submission replayed, so its original outcome is returned and nothing
    /// is written; anything else is a <see cref="ExperienceStoreOutcome.Conflict"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is compared is the evidence's <em>identity and claim</em>: the record it is about, the
    /// lifecycle event it rode in on, which way it points, who observed it, the run and round or reviewer
    /// it came from, the rule version, and the detail. The counters and the score are deliberately not
    /// compared -- they are derived from whatever the record held when the submission was first made, so a
    /// genuine replay that arrived after other evidence landed would otherwise be reported as a conflict
    /// for agreeing with itself.
    /// </para>
    /// <para>
    /// The event ID <em>is</em> compared, for a stored row that produced one. Without that, a retry under
    /// a fresh event ID would be reported as committed while carrying a lifecycle event that was never
    /// written -- the same trap the plain lifecycle replay avoids by comparing every field. A stored row
    /// that produced no event (a duplicate) has no event ID to contradict, so there is nothing to compare.
    /// </para>
    /// <para>
    /// Every number reported comes from the ledger row, so a replay describes the one moment the original
    /// submission settled rather than mixing a stored revision with a freshly read status.
    /// </para>
    /// </remarks>
    private static async Task<ExperienceLifecycleCommitResult> CompareStoredEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        ConfidenceUpdate submitted,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SelectEvidenceSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("evidence_id", submitted.EvidenceId));
        AddScopeParameters(command.Parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Either no such row, or one whose record is in another scope -- reported identically, so a
            // guessed evidence ID reveals nothing about another scope's scores or counters. Evidence rows
            // are never deleted, so the row that just collided cannot otherwise vanish.
            return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
        }

        try
        {
            var storedEventId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);

            var sameContent =
                reader.GetGuid(0) == lifecycleEvent.ExperienceRecordId
                && (storedEventId is null || storedEventId == lifecycleEvent.EventId)
                && DecodeEnumText<ConfidenceEvidenceKind>(reader.GetString(2), "confidence evidence") == submitted.Kind
                && DecodeEnumText<ConfidenceEvidenceSource>(reader.GetString(3), "confidence evidence") == submitted.Source
                && reader.GetGuid(4) == submitted.RunId
                && (reader.IsDBNull(5) ? (Guid?)null : reader.GetGuid(5)) == submitted.VerificationRoundId
                && string.Equals(reader.IsDBNull(6) ? null : reader.GetString(6), submitted.ReviewerIdentity, StringComparison.Ordinal)
                && string.Equals(reader.GetString(10), submitted.RuleVersion, StringComparison.Ordinal)
                && string.Equals(reader.IsDBNull(11) ? null : reader.GetString(11), submitted.Detail, StringComparison.Ordinal);

            if (!sameContent)
            {
                return new(ExperienceStoreOutcome.Conflict, 0, null, NoErrors);
            }

            var stored = submitted with
            {
                PriorReuseConfidence = reader.GetDouble(12),
                NewReuseConfidence = reader.GetDouble(13),
                PriorSupportingValidations = reader.GetInt32(14),
                NewSupportingValidations = reader.GetInt32(15),
                PriorContradictions = reader.GetInt32(16),
                NewContradictions = reader.GetInt32(17),
            };

            return new(
                ExperienceStoreOutcome.Committed,
                reader.GetInt64(8),
                ReadStoredStatus(reader, 9),
                NoErrors,
                stored);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // A retyped or hand-written row, reported the way every other decode failure is.
            throw new ExperienceStoreException("Stored confidence evidence could not be decoded.", ex);
        }
    }

    /// <summary>
    /// Reports a lost race: the record's current revision, or <see cref="ExperienceStoreOutcome.NotFound"/>
    /// when it is not in this scope at all. Used where the server aborted the transaction itself, so the
    /// re-read runs outside it.
    /// </summary>
    private static async Task<ExperienceLifecycleCommitResult> StaleOrMissingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        var current = await ReadRevisionAndStatusAsync(connection, transaction, scope, experienceId, cancellationToken).ConfigureAwait(false);
        if (current is not { } record)
        {
            return new(ExperienceStoreOutcome.NotFound, 0, null, NoErrors);
        }

        return record.Deleted
            ? new(ExperienceStoreOutcome.Deleted, record.Revision, null, NoErrors)
            : new(ExperienceStoreOutcome.StaleRevision, record.Revision, null, NoErrors);
    }

    /// <summary>
    /// Reads the record's revision and status within exactly <paramref name="scope"/>, optionally locking
    /// the row for the rest of the transaction. Only the duplicate path needs the lock: every other caller
    /// either holds the row through its own revision-guarded UPDATE or is reporting a race it already lost.
    /// </summary>
    private static async Task<StoredRecordState?> ReadRevisionAndStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken,
        bool forUpdate = false)
    {
        await using var command = new NpgsqlCommand(
            forUpdate ? LockRevisionAndStatusSql : SelectRevisionAndStatusSql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        AddScopeParameters(command.Parameters, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // A tombstone's status is a literal no ExperienceStatus member names, so it is never decoded:
        // the marker is read first and the status left alone.
        return reader.IsDBNull(2)
            ? new StoredRecordState(ReadRevision(reader, 0), ReadStoredStatus(reader, 1), Deleted: false)
            : new StoredRecordState(ReadRevision(reader, 0), null, Deleted: true);
    }

    /// <summary>
    /// What a scoped read of one record row found: its revision, its status when it has one this
    /// library's enum names, and whether it is a tombstone.
    /// </summary>
    private readonly record struct StoredRecordState(long Revision, ExperienceStatus? Status, bool Deleted);

    /// <summary>
    /// Matches a unique violation of one named constraint. Naming it keeps the event primary key (a
    /// resubmitted event ID) apart from the record-revision index (a lost race), so neither is ever
    /// mistaken for the other or for a constraint added later.
    /// </summary>
    private static bool IsViolationOf(PostgresException ex, string constraintName, CancellationToken cancellationToken) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(ex.ConstraintName, constraintName, StringComparison.Ordinal)
        && !cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Binds the event row, including the confidence payload when the event carries one and the actor
    /// the commit ran under.
    /// </summary>
    /// <remarks>
    /// The actor is <see cref="AuthorizationContext.PrincipalId"/> and is taken from the
    /// host-established context rather than from anything on the event -- which is the same rule the
    /// reviewer identity follows, for the same reason. It is written for every commit, not only a
    /// confidence one, because "who did this" is the question an auditor asks of every transition.
    /// </remarks>
    private static void AddEventParameters(
        NpgsqlParameterCollection parameters,
        AuthorizationContext authorization,
        Scope scope,
        LifecycleEvent lifecycleEvent,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt,
        long appliedRevision)
    {
        parameters.Add(new NpgsqlParameter<Guid>("event_id", lifecycleEvent.EventId));
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", lifecycleEvent.ExperienceRecordId));
        AddScopeParameters(parameters, scope);
        parameters.Add(NullableText("prior_status", lifecycleEvent.PriorStatus?.ToString()));
        parameters.Add(new NpgsqlParameter<string>("current_status", lifecycleEvent.CurrentStatus.ToString()));
        parameters.Add(new NpgsqlParameter<string>("reason", lifecycleEvent.Reason));
        parameters.Add(new NpgsqlParameter<string>("producer", lifecycleEvent.Producer));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", occurredAt));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt));
        parameters.Add(new NpgsqlParameter<long>("expected_revision", lifecycleEvent.ExpectedRevision));
        parameters.Add(new NpgsqlParameter<long>("applied_revision", appliedRevision));
        parameters.Add(NullableUuid("replacement_experience_id", lifecycleEvent.ReplacementExperienceId));
        parameters.Add(NullableText("actor", Actor(authorization)));

        var confidence = lifecycleEvent.Confidence;
        parameters.Add(NullableUuid("confidence_evidence_id", confidence?.EvidenceId));
        parameters.Add(NullableText("confidence_kind", confidence?.Kind.ToString()));
        parameters.Add(NullableText("confidence_source", confidence?.Source.ToString()));
        parameters.Add(NullableUuid("confidence_run_id", confidence?.RunId));
        parameters.Add(NullableUuid("confidence_verification_round_id", confidence?.VerificationRoundId));
        parameters.Add(NullableText("confidence_reviewer_identity", confidence?.ReviewerIdentity));
        parameters.Add(NullableText("confidence_rule_version", confidence?.RuleVersion));
        parameters.Add(NullableText("confidence_detail", confidence?.Detail));
        parameters.Add(NullableDouble("prior_reuse_confidence", confidence?.PriorReuseConfidence));
        parameters.Add(NullableDouble("new_reuse_confidence", confidence?.NewReuseConfidence));
        parameters.Add(NullableInt("prior_supporting_validations", confidence?.PriorSupportingValidations));
        parameters.Add(NullableInt("new_supporting_validations", confidence?.NewSupportingValidations));
        parameters.Add(NullableInt("prior_contradictions", confidence?.PriorContradictions));
        parameters.Add(NullableInt("new_contradictions", confidence?.NewContradictions));
    }

    internal static void AddScopeParameters(NpgsqlParameterCollection parameters, Scope scope)
    {
        parameters.Add(new NpgsqlParameter<string>("tenant_id", NpgsqlDbType.Text) { TypedValue = scope.TenantId });
        parameters.Add(new NpgsqlParameter<string>("application_id", NpgsqlDbType.Text) { TypedValue = scope.ApplicationId });
        parameters.Add(new NpgsqlParameter<string>("project_id", NpgsqlDbType.Text) { TypedValue = scope.ProjectId });
        parameters.Add(NullableText("team_id", scope.TeamId));
        parameters.Add(NullableText("agent_id", scope.AgentId));
        parameters.Add(NullableText("user_id", scope.UserId));
    }

    /// <summary>
    /// The principal to record on a row, or <see langword="null"/> when the host established none worth
    /// recording. It is bound as null rather than as the blank string on purpose: the column's non-blank
    /// CHECK would otherwise turn a host with an empty <see cref="AuthorizationContext.PrincipalId"/> into
    /// an infrastructure failure on *every* lifecycle commit, confidence or not. A blank principal is
    /// still refused where it actually matters -- human evidence, whose whole independence rule rests on
    /// it -- and there it is a typed validation error naming the field.
    /// </summary>
    private static string? Actor(AuthorizationContext authorization) =>
        string.IsNullOrWhiteSpace(authorization.PrincipalId) ? null : authorization.PrincipalId;

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    private static NpgsqlParameter NullableUuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value is { } id ? id : DBNull.Value };

    private static NpgsqlParameter NullableDouble(string name, double? value) =>
        new(name, NpgsqlDbType.Double) { Value = value is { } number ? number : DBNull.Value };

    private static NpgsqlParameter NullableInt(string name, int? value) =>
        new(name, NpgsqlDbType.Integer) { Value = value is { } number ? number : DBNull.Value };

    internal static DateTimeOffset ToStoredTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }

    /// <summary>
    /// Finds a JSON <c>\u0000</c> escape whose backslash is not itself escaped (an odd run of backslashes).
    /// </summary>
    private static bool ContainsEscapedNul(string json)
    {
        const string Escape = "\\u0000";
        for (var index = json.IndexOf(Escape, StringComparison.Ordinal); index >= 0; index = json.IndexOf(Escape, index + 1, StringComparison.Ordinal))
        {
            var backslashes = 0;
            for (var i = index; i >= 0 && json[i] == '\\'; i--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the shared-by-grant flag by name. A reader that did not select it is treated as "not
    /// shared", which is the safe direction: a consumer that sees no flag keeps its strict scope check.
    /// </summary>
    internal static bool ReadSharedByGrant(DbDataReader reader)
    {
        try
        {
            var ordinal = reader.GetOrdinal(SharedByGrantAlias);
            return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the permitting grant's ID by name. A reader that did not select it is treated as "not
    /// told which grant", which is the safe direction: a consumer distinguishes that from "no grant"
    /// by the shared flag, and an audited read that cannot name its grant fails rather than inventing
    /// one.
    /// </summary>
    internal static Guid? ReadPermittingGrant(DbDataReader reader)
    {
        try
        {
            var ordinal = reader.GetOrdinal(PermittingGrantAlias);
            return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the permitting grant's disclosure level by name. A reader that did not select it, a null,
    /// and a value this build does not know are all "not told", which every consumer renders as
    /// <see cref="ExperienceGrantDisclosure.LessonOnly"/> -- the least disclosure -- rather than
    /// guessing wider.
    /// </summary>
    internal static ExperienceGrantDisclosure? ReadPermittingDisclosure(DbDataReader reader)
    {
        try
        {
            var ordinal = reader.GetOrdinal(PermittingDisclosureAlias);
            return reader.IsDBNull(ordinal) ? null : ParseDisclosure(reader.GetString(ordinal));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a stored disclosure level by its exact name. Anything else is <see langword="null"/>:
    /// a level this build cannot name is never widened into one it can.
    /// </summary>
    internal static ExperienceGrantDisclosure? ParseDisclosure(string? stored) =>
        stored is not null
        && Enum.TryParse<ExperienceGrantDisclosure>(stored, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), stored, StringComparison.Ordinal)
            ? parsed
            : null;

    /// <summary>
    /// Reads the tombstone marker by name. A reader that did not select it is treated as "not erased",
    /// which is the safe direction for a caller that never asked: every statement that could meet a
    /// tombstone either selects this column or filters tombstones out in SQL.
    /// </summary>
    internal static bool ReadDeleted(DbDataReader reader)
    {
        try
        {
            return !reader.IsDBNull(reader.GetOrdinal(DeletedAtAlias));
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    internal static ExperienceRecord ReadRecord(DbDataReader reader)
    {
        try
        {
            return DecodeRecord(reader);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt payload (e.g. InvalidCastException, a null array element).
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static StoredLifecycleEvent ReadEvent(DbDataReader reader)
    {
        try
        {
            return DecodeEvent(reader);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt row (e.g. InvalidCastException on a retyped column).
            throw new ExperienceStoreException("Stored lifecycle event could not be decoded.", ex);
        }
    }

    private static StoredLifecycleEvent DecodeEvent(DbDataReader reader) => new(
        new LifecycleEvent(
            EventId: reader.GetGuid(0),
            ExperienceRecordId: reader.GetGuid(1),
            PriorStatus: reader.IsDBNull(8) ? null : DecodeStatus(reader.GetString(8), "lifecycle event"),
            CurrentStatus: DecodeStatus(reader.GetString(9), "lifecycle event"),
            Reason: reader.GetString(10),
            Producer: reader.GetString(11),
            OccurredAt: reader.GetFieldValue<DateTimeOffset>(12),
            ExpectedRevision: reader.GetInt64(14),
            ReplacementExperienceId: reader.IsDBNull(16) ? null : reader.GetGuid(16),
            Confidence: DecodeConfidence(reader)),
        RecordedAt: reader.GetFieldValue<DateTimeOffset>(13),
        AppliedRevision: reader.GetInt64(15),
        Actor: reader.IsDBNull(17) ? null : reader.GetString(17));

    /// <summary>
    /// Rebuilds the confidence payload an event carried, or <see langword="null"/> for the events that
    /// carried none. The evidence ID alone decides which: the table's own CHECK makes the eleven
    /// always-present columns all null or all set together, so a row can never be half an update, and
    /// reading any one of them as the flag is enough.
    /// </summary>
    private static ConfidenceUpdate? DecodeConfidence(DbDataReader reader) => reader.IsDBNull(18)
        ? null
        : new ConfidenceUpdate(
            EvidenceId: reader.GetGuid(18),
            Kind: DecodeEnumText<ConfidenceEvidenceKind>(reader.GetString(19), "lifecycle event"),
            Source: DecodeEnumText<ConfidenceEvidenceSource>(reader.GetString(20), "lifecycle event"),
            RunId: reader.GetGuid(21),
            VerificationRoundId: reader.IsDBNull(22) ? null : reader.GetGuid(22),
            ReviewerIdentity: reader.IsDBNull(23) ? null : reader.GetString(23),
            RuleVersion: reader.GetString(24),
            PriorReuseConfidence: reader.GetDouble(26),
            NewReuseConfidence: reader.GetDouble(27),
            PriorSupportingValidations: reader.GetInt32(28),
            NewSupportingValidations: reader.GetInt32(29),
            PriorContradictions: reader.GetInt32(30),
            NewContradictions: reader.GetInt32(31),
            Detail: reader.IsDBNull(25) ? null : reader.GetString(25));

    /// <summary>Reads a <c>bigint</c> revision, reporting schema drift the way the row decoders do.</summary>
    private static long ReadRevision(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static ExperienceStatus ReadStoredStatus(DbDataReader reader, int ordinal)
    {
        try
        {
            return DecodeStatus(reader.GetString(ordinal), "Experience Record");
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static Scope ReadEventScope(DbDataReader reader) => new(
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <param name="statusText">The stored status text.</param>
    /// <param name="objectKind">Which stored object the text came from, so a failure names the right row.</param>
    private static ExperienceStatus DecodeStatus(string statusText, string objectKind)
    {
        if (!Enum.TryParse<ExperienceStatus>(statusText, ignoreCase: false, out var status) || !Enum.IsDefined(status)
            || !string.Equals(status.ToString(), statusText, StringComparison.Ordinal))
        {
            throw new ExperienceStoreException($"Stored {objectKind} has an unrecognized status.");
        }

        return status;
    }

    /// <summary>
    /// Reads an enum stored as its own member name, matched case-sensitively and against the defined
    /// members only -- the same strictness <see cref="DecodeStatus"/> applies, for the same reason: a
    /// row whose text is nearly right must fail loudly rather than decode into something else.
    /// </summary>
    /// <typeparam name="T">The enum to decode.</typeparam>
    /// <param name="text">The stored text.</param>
    /// <param name="objectKind">Which stored object the text came from, so a failure names the right row.</param>
    private static T DecodeEnumText<T>(string text, string objectKind)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(text, ignoreCase: false, out var value) || !Enum.IsDefined(value)
            || !string.Equals(value.ToString(), text, StringComparison.Ordinal))
        {
            throw new ExperienceStoreException($"Stored {objectKind} has an unrecognized {typeof(T).Name}.");
        }

        return value;
    }

    private static ExperienceRecord DecodeRecord(DbDataReader reader)
    {
        var status = DecodeStatus(reader.GetString(9), "Experience Record");

        var payload = ExperiencePayload.Deserialize(reader.GetInt32(16), reader.GetString(17));

        var scope = new Scope(
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

        return ExperiencePayload.ToRecord(
            payload,
            reader.GetGuid(0),
            reader.GetGuid(1),
            scope,
            reader.GetString(8),
            status,
            reader.GetDouble(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetFieldValue<DateTimeOffset>(15));
    }

    /// <summary>
    /// Driver, socket, and timeout failures are translated. An <see cref="OperationCanceledException"/>
    /// caused by the caller's own token is not matched, so it propagates unwrapped with its stack.
    /// </summary>
    internal static bool IsInfrastructureFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        NpgsqlException or SocketException or TimeoutException => true,
        _ => false,
    };

    internal static Exception Translate(Exception ex, string operation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled while the driver reported a failure: surface cancellation, unwrapped.
            return new OperationCanceledException("The Experience Record store operation was cancelled.", ex, cancellationToken);
        }

        return new ExperienceStoreException($"Experience Record {operation} failed due to a storage infrastructure error.", ex);
    }
}
