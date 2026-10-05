using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The record store's SQL fragments, including the ones other components share: the table, the record and
/// lifecycle-event columns, the tombstone and exact-scope predicates, the grant columns, joins and aliases, and the
/// store's read statements. One home, so every channel composes byte-for-byte the same text.
/// </summary>
internal static class ExperienceRecordSql
{
    /// <summary>The canonical record table. Shared with <see cref="PostgresExperienceCandidateSource"/>, which reads from it.</summary>
    internal const string Table = "agent_experience.experience_records";

    /// <summary>
    /// The record columns every read selects, in the order <see cref="ExperienceRecordRows.DecodeRecord"/> expects (ordinals 0-17).
    /// A reader that selects more must append its extra columns <em>after</em> these, never before.
    /// </summary>
    internal const string SelectColumns =
        "experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, " +
        "status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, " +
        "payload_version, payload";

    internal const string EventsTable = "agent_experience.lifecycle_events";

    /// <summary>
    /// The event columns every read selects, in the order <see cref="ExperienceRecordRows.DecodeEvent"/> expects (ordinals
    /// 0-31). A reader that selects more must append its extra columns <em>after</em> these.
    /// <para>
    /// Everything from <c>actor</c> onwards arrived with <c>0007</c>. <c>actor</c> is written for every
    /// commit; the <c>confidence_*</c> and score columns are written together or not at all, which the
    /// table states as a CHECK, so a half-written update cannot reach the log.
    /// </para>
    /// </summary>
    internal const string EventColumns =
        "event_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision, " +
        "replacement_experience_id, actor, confidence_evidence_id, confidence_kind, confidence_source, " +
        "confidence_run_id, confidence_verification_round_id, confidence_reviewer_identity, confidence_rule_version, " +
        "confidence_detail, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, " +
        "new_supporting_validations, prior_contradictions, new_contradictions, confidence_assessment_id, " +
        "confidence_admission";

    /// <summary>The ordinal <c>confidence_assessment_id</c> sits at in <see cref="EventColumns"/> (added by <c>0015</c>).</summary>
    internal const int EventAssessmentIdOrdinal = 32;

    /// <summary>The ordinal <c>confidence_admission</c> sits at, the last of <see cref="EventColumns"/> (added by <c>0018</c>).</summary>
    internal const int EventAdmissionOrdinal = 33;

    internal const string JoinedEventColumns =
        "e.event_id, e.experience_id, e.tenant_id, e.application_id, e.project_id, e.team_id, e.agent_id, e.user_id, " +
        "e.prior_status, e.current_status, e.reason, e.producer, e.occurred_at, e.recorded_at, e.expected_revision, " +
        "e.applied_revision, e.replacement_experience_id, e.actor, e.confidence_evidence_id, e.confidence_kind, " +
        "e.confidence_source, e.confidence_run_id, e.confidence_verification_round_id, e.confidence_reviewer_identity, " +
        "e.confidence_rule_version, e.confidence_detail, e.prior_reuse_confidence, e.new_reuse_confidence, " +
        "e.prior_supporting_validations, e.new_supporting_validations, e.prior_contradictions, e.new_contradictions, " +
        "e.confidence_assessment_id, e.confidence_admission";

    /// <summary>
    /// "this row is not a tombstone", unqualified, for a statement over the record table alone.
    /// <para>
    /// A tombstone carries no payload at all (see <c>0010</c>), so it is not a record a read can
    /// return: <see cref="ExperiencePayload.Deserialize"/> would fail on it, and a list that included
    /// one would be handing back a row with nothing in it. Every read filters it out, and the two
    /// operations that name one record -- <see cref="PostgresExperienceRecordStore.GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/> and
    /// <see cref="PostgresExperienceRecordStore.GetHistoryAsync"/> -- read <c>deleted_at</c> instead of filtering on it, so they can
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
    /// <see cref="PostgresExperienceRecordStore.CommitLifecycleEventAsync"/> needs no locking clause of its own: its projection
    /// <c>UPDATE</c> is itself the lock, and <c>0010</c>'s projection guard refuses any <c>UPDATE</c> of
    /// a tombstone from the database's side as well.
    /// </para>
    /// </summary>
    internal const string RecordKeyShareLock = "FOR KEY SHARE OF r";

    /// <summary>The alias a read selects <c>deleted_at</c> under, read back by name, never by ordinal.</summary>
    internal const string DeletedAtAlias = "deleted_at";

    /// <summary>
    /// The tombstone marker, appended <em>after</em> the record columns so <see cref="ExperienceRecordRows.ReadRecord"/>'s
    /// ordinals 0-17 are untouched.
    /// </summary>
    internal const string DeletedAtColumn = "r." + DeletedAtAlias + " AS " + DeletedAtAlias;

    /// <summary>The exact-scope predicate every statement applies, shared with <see cref="PostgresExperienceCandidateSource"/>.</summary>
    internal const string ScopePredicate =
        "tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id " +
        "AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id " +
        "AND user_id IS NOT DISTINCT FROM @user_id";

    /// <summary>
    /// The column <c>0021</c> adds: whether the record's reflection was written by a model, the flag an excluding
    /// search filters on in SQL. For a plaintext record <c>0021</c>'s trigger derives it from the payload as the row is
    /// written, so <see cref="PostgresExperienceRecordStore.InsertSql"/> does not name it; for a sealed record, whose payload the database cannot
    /// read, <see cref="PostgresExperienceRecordStore.InsertSealedSql"/> writes it from the record's reflection. It is in the clear in both modes.
    /// </summary>
    internal const string ModelAuthoredColumn = "reflection_model_authored";

    /// <summary>
    /// The derived full-text vector of a sealed record, computed from exactly the expression <c>0003</c>'s
    /// generated <c>search_vector</c> uses -- task ID, task summary, lesson, bounded to 100000 characters -- so a
    /// sealed record ranks exactly as its plaintext twin would. The text is sent as parameters and never stored;
    /// what is stored is the tsvector (stemmed words and positions), which is the residual docs/guide/crypto-shredding.md names.
    /// </summary>
    internal const string SealedSearchVectorExpression =
        "to_tsvector('english', left(coalesce(@search_task_id, '') || ' ' || coalesce(@search_summary, '') || ' ' || " +
        "coalesce(@search_lesson, ''), 100000))";

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
        $"SELECT {SelectColumns}, {SharedByGrantColumn}, {PermittingGrantColumn}, {PermittingDisclosureColumn}, " +
        $"{PermittingApproachArgumentsColumn}, {DeletedAtColumn} FROM {Table} r " +
        $"{PermittingGrantJoin} ";

    /// <summary>The readability rule <see cref="GetSql"/> and <see cref="GetManySql"/> share, byte for byte.</summary>
    internal const string GetReadablePredicate = ReadableWithNamedGrantPredicate;

    /// <summary>
    /// <see cref="GetSql"/> for several records in one statement (KL-1): the identical select,
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
        $"NULL::text AS {PermittingDisclosureAlias}, NULL::jsonb AS {PermittingApproachArgumentsAlias}, {DeletedAtColumn} " +
        $"FROM {Table} r ";

    /// <summary><see cref="GetExactSql"/> for several records in one statement: the fallback <see cref="GetManySql"/> takes when grants are unavailable.</summary>
    internal const string GetManyExactSql = GetExactSelectFrom + $"WHERE r.experience_id = ANY(@experience_ids) AND {RecordScopePredicate}";

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
    /// statement that already calls <see cref="ExperienceRecordParameters.AddScopeParameters"/> can compose it as it stands.
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
    /// It is used by <see cref="PostgresExperienceRecordStore.GetAsync(AuthorizationContext, Scope, Guid, CancellationToken)"/>, by the text channel, and by the vector channel -- the
    /// three paths a grant covers. Writes, lifecycle commits, lifecycle history, and
    /// <see cref="PostgresExperienceRecordStore.QueryAsync"/>'s enumeration keep the exact-scope predicate: a grant confers reading
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
    /// record columns, so <see cref="ExperienceRecordRows.ReadRecord"/>'s ordinals 0-17 are untouched.
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
    /// <para>
    /// <b>And the owner's argument allowlist</b> (<c>0017</c>), from the same row again, so the keys injection may
    /// show are the keys of the grant that admitted the read and carries the level they belong to.
    /// </para>
    /// </summary>
    internal const string PermittingGrantJoin =
        $"LEFT JOIN LATERAL (SELECT g.grant_id, g.disclosure, g.approach_arguments FROM {GrantsTable} g WHERE {ActiveGrantConditions} " +
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

    /// <summary>The alias the permitting grant's argument allowlist is selected under, read back by name.</summary>
    internal const string PermittingApproachArgumentsAlias = "permitting_grant_approach_arguments";

    /// <summary>
    /// The permitting grant's argument allowlist, from the same lateral row as <see cref="PermittingGrantColumn"/>.
    /// Null for a record the requester owns and for every level but <c>LessonApproachAndArguments</c>.
    /// </summary>
    internal const string PermittingApproachArgumentsColumn =
        PermittingGrantSource + ".approach_arguments AS " + PermittingApproachArgumentsAlias;

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
}
