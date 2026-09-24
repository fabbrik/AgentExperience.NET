using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Access to the schema scripts embedded in this package, for reading a script's SQL before it runs.
/// To apply them, call <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>,
/// which runs them in <see cref="ScriptNames"/> order and journals what it applied; hosts do not need
/// their own apply loop.
/// </summary>
public static class PostgresExperienceRecordSchema
{
    /// <summary>
    /// The PostgreSQL schema that holds every AgentExperience.NET table, including the migration
    /// journal <c>schema_versions</c>.
    /// </summary>
    public const string SchemaName = "agent_experience";

    /// <summary>The initial script that creates the <c>experience_records</c> table.</summary>
    public const string InitialScriptName = "0001_create_experience_records.sql";

    /// <summary>The script that creates the append-only <c>lifecycle_events</c> table.</summary>
    public const string LifecycleEventsScriptName = "0002_create_lifecycle_events.sql";

    /// <summary>
    /// The script that adds the generated <c>search_vector</c> column and its GIN index, which
    /// <see cref="PostgresExperienceCandidateSource"/> matches task text against.
    /// </summary>
    public const string SearchScriptName = "0003_add_experience_search.sql";

    /// <summary>
    /// The script that creates <c>experience_grants</c> and its append-only
    /// <c>experience_grant_events</c> log, which <see cref="PostgresExperienceGrantStore"/> administers
    /// and every grant-aware read predicate consults.
    /// </summary>
    /// <remarks>
    /// It is numbered <c>0005</c> because <c>0004</c> belongs to
    /// <c>AgentExperience.Storage.Postgres.Vectors</c>: the two packages apply their own scripts, but
    /// they share one journal and one number sequence, so the whole schema still orders at a glance.
    /// </remarks>
    public const string GrantsScriptName = "0005_create_experience_grants.sql";

    /// <summary>
    /// The script that adds a superseding event's <c>replacement_experience_id</c> and makes both event
    /// logs append-only in the database: <c>BEFORE UPDATE</c>/<c>DELETE</c> triggers that reject
    /// rewriting or removing a stored event, and a trigger that keeps a grant's revocation permanent and
    /// its expiry from being extended.
    /// </summary>
    /// <remarks>
    /// Those triggers bind every writer using the application role, including one that bypasses this
    /// package entirely. They do <em>not</em> bind a superuser, nor the tables' own owner, which can
    /// disable or drop a trigger before writing; see the script's own header and the package README.
    /// </remarks>
    public const string SupersessionAndAppendOnlyScriptName = "0006_lifecycle_supersession_and_append_only.sql";

    /// <summary>
    /// The script that creates <c>confidence_evidence</c> with the unique index that decides evidence
    /// independence, adds the score, counter, evidence, rule-version, and actor columns to
    /// <c>lifecycle_events</c>, and extends <c>enforce_record_projection</c> so reuse confidence and its
    /// counters move only with the revision of the lifecycle event that recorded the evidence for them.
    /// </summary>
    /// <remarks>
    /// The score those columns carry is a heuristic -- <c>(1 + S) / (2 + S + F)</c> -- and never a
    /// calibrated probability; nothing in the database computes it, and the rule version travels with
    /// every update. Its CHECKs on the existing <c>lifecycle_events</c> table are added
    /// <c>NOT VALID</c>; see the script's own header for the confirm-then-<c>VALIDATE</c> step.
    /// </remarks>
    public const string ConfidenceEvidenceScriptName = "0007_confidence_evidence.sql";

    /// <summary>
    /// The script that creates the append-only reuse feedback ledger -- <c>reuse_feedback</c>, one row
    /// per submission, and <c>reuse_feedback_exposures</c>, one row per record a run was exposed to --
    /// which <see cref="PostgresExperienceReuseFeedbackStore"/> writes before any confidence submission.
    /// </summary>
    /// <remarks>
    /// Exposure is not attribution: a submission's <c>benefit</c> is <c>'Unknown'</c> exactly when its
    /// <c>attribution_source</c> is <c>'None'</c>, enforced by a CHECK, and such a row produces no
    /// confidence submission at all. The tables carry no foreign key to <c>experience_records</c>, so a
    /// run that saw an ID resolving to nothing in its scope is still recordable. Its one deferred
    /// constraint -- the exposures-to-submissions foreign key -- is added <c>NOT VALID</c>; see the
    /// script's own header for the confirm-then-<c>VALIDATE</c> step and the <c>CONCURRENTLY</c> note
    /// for its unique indexes.
    /// </remarks>
    public const string ReuseFeedbackScriptName = "0008_reuse_feedback.sql";

    /// <summary>
    /// The script that creates the append-only <c>experience_grant_access</c> ledger, which
    /// <see cref="PostgresExperienceGrantAccessLog"/> writes one row to per record a grant delivered,
    /// and that adds the database's own fixed ceiling on how long a grant may live.
    /// </summary>
    /// <remarks>
    /// It records <em>deliveries</em>: a get that a grant permitted (the pre-injection re-read
    /// included), and every grant-permitted record the text and vector channels return -- a candidate
    /// carries the record read back in full, so returning one across a scope boundary is a disclosure.
    /// A search's rows are written in one statement. The lifetime ceiling is added to the
    /// existing <c>experience_grants</c> table <c>NOT VALID</c>; see the script's own header for the
    /// confirm-then-<c>VALIDATE</c> step, what to do about a grant already issued beyond it, and the
    /// <c>CONCURRENTLY</c> note for the two indexes.
    /// </remarks>
    public const string GrantAccessLogScriptName = "0009_grant_access_log.sql";

    /// <summary>
    /// The script that adds <c>experience_records.deleted_at</c> and the one erasure path:
    /// <c>agent_experience.purge_experience_record</c>, which removes every payload-bearing row that
    /// names one record and leaves a payload-free tombstone behind, plus
    /// <c>agent_experience.purge_expired_grants</c> for grants that have expired or that name a
    /// tombstone.
    /// </summary>
    /// <remarks>
    /// It replaces <c>0006</c>'s and <c>0007</c>'s trigger functions in place, so every
    /// <c>ENABLE ALWAYS</c> binding survives and no table is unguarded for an instant. The guards keep
    /// refusing <c>UPDATE</c> and <c>TRUNCATE</c> unconditionally and admit a <c>DELETE</c> only while
    /// the purge function's transaction-scoped marker is set -- which is an auditability mechanism, not
    /// a privilege boundary: a custom GUC is settable by any session, and the guards still do not bind a
    /// role that can <c>ALTER TABLE</c>.
    /// <para>
    /// It also creates the only two triggers it adds, <c>experience_records_no_delete</c> and
    /// <c>experience_records_no_truncate</c>, which refuse removing a record row from every session with
    /// no marker exception at all -- the erasure never deletes that row, and a freed
    /// <c>experience_id</c> would let a recreated record inherit the old content's sharing grants.
    /// </para>
    /// <para>
    /// The two purge functions are <c>SECURITY DEFINER</c>, so the script revokes <c>EXECUTE</c> on them
    /// from <c>PUBLIC</c> -- PostgreSQL's default would otherwise make erasure reachable by every role
    /// that can connect -- and grants it to the migrating role. An application role that is not the
    /// migrating role needs an explicit grant.
    /// </para>
    /// <para>
    /// Its three indexes are built with plain <c>CREATE INDEX</c> inside the migrator's per-script
    /// transaction; the script's header carries the <c>CONCURRENTLY</c> runbook for building them out of
    /// band first, the confirm-then-<c>VALIDATE</c> step, and the note that the erased text survives in
    /// dead heap tuples until <c>VACUUM</c>. See the script's own header and the package README.
    /// </para>
    /// </remarks>
    public const string DeleteAndExpireScriptName = "0010_delete_and_expire.sql";

    /// <summary>
    /// The script that adds a sharing grant's disclosure level: <c>experience_grants.disclosure</c>
    /// (<c>NOT NULL DEFAULT 'LessonOnly'</c>), and a nullable copy of it on
    /// <c>experience_grant_events</c> and <c>experience_grant_access</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It changes behaviour on upgrade.</b> Every existing grant becomes
    /// <see cref="AgentExperience.Abstractions.ExperienceGrantDisclosure.LessonOnly"/>, so a borrowed
    /// record's <c>Approach:</c> line stops being injected until the owner revokes the grant and issues
    /// a new one with <see cref="AgentExperience.Abstractions.ExperienceGrantDisclosure.LessonAndApproach"/>.
    /// </para>
    /// <para>
    /// The level is immutable: the script restates <c>0006</c>'s <c>enforce_grant_monotonicity()</c>
    /// with <c>disclosure</c> added to its identity pins. The two ledger columns are nullable -- rows
    /// written before this script carry no level -- and require a level on new rows through a
    /// <c>CHECK</c> added <c>NOT VALID</c>, which is meant to stay that way.
    /// </para>
    /// </remarks>
    public const string GrantDisclosureScriptName = "0011_grant_disclosure.sql";

    /// <summary>
    /// The script that gives the grant access log a retention path:
    /// <c>agent_experience.purge_grant_access</c>, which removes access rows older than a host-given
    /// cutoff in bounded batches, within one owner scope or that scope and everything beneath it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The function is <c>SECURITY DEFINER</c>, so the script revokes <c>EXECUTE</c> from <c>PUBLIC</c>
    /// and grants it to the migrating role, exactly as <c>0010</c> does for its two purge functions. It
    /// sets its own marker, <c>agent_experience.access_purge_authorized</c> (transaction-local, and reset
    /// when the function returns), which
    /// <c>reject_event_log_mutation()</c> -- restated in place with <c>0010</c>'s body unchanged --
    /// recognises for a <c>DELETE</c> on <c>experience_grant_access</c> only. <c>0010</c>'s marker still
    /// admits nothing on that table, so erasing a record still keeps its access rows.
    /// </para>
    /// <para>
    /// A row younger than 30 days (by the database's clock, on <c>recorded_at</c>) is never removed:
    /// the function refuses a later cutoff outright, and the guard re-checks every row. See
    /// <see cref="PostgresExperienceGrantAccessLog.MinimumRetentionDays"/> and the script's header,
    /// which carries the <c>CONCURRENTLY</c> runbook for its one index.
    /// </para>
    /// </remarks>
    public const string GrantAccessRetentionScriptName = "0012_grant_access_retention.sql";

    private const string ResourcePrefix = "AgentExperience.Storage.Postgres.Migrations.";

    /// <summary>
    /// Every embedded script name, in the order they must be applied. This package's schema is
    /// deliberately text-only: the derived embedding schema, which needs the <c>vector</c> extension,
    /// is owned and applied by <c>AgentExperience.Storage.Postgres.Vectors</c> instead, so a host that
    /// never enables the vector channel never runs a superuser-only <c>CREATE EXTENSION</c>. That is
    /// why <c>0004</c> is absent from this list while <c>0005</c> is present.
    /// </summary>
    public static IReadOnlyList<string> ScriptNames { get; } =
    [
        InitialScriptName,
        LifecycleEventsScriptName,
        SearchScriptName,
        GrantsScriptName,
        SupersessionAndAppendOnlyScriptName,
        ConfidenceEvidenceScriptName,
        ReuseFeedbackScriptName,
        GrantAccessLogScriptName,
        DeleteAndExpireScriptName,
        GrantDisclosureScriptName,
        GrantAccessRetentionScriptName,
    ];

    /// <summary>Reads an embedded script's SQL text.</summary>
    /// <param name="scriptName">One of <see cref="ScriptNames"/>.</param>
    /// <returns>The script's SQL.</returns>
    /// <exception cref="ArgumentException"><paramref name="scriptName"/> is not an embedded script.</exception>
    public static string GetScript(string scriptName)
    {
        ArgumentNullException.ThrowIfNull(scriptName);
        if (!ScriptNames.Contains(scriptName, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unknown schema script name.", nameof(scriptName));
        }

        using var stream = typeof(PostgresExperienceRecordSchema).Assembly.GetManifestResourceStream(ResourcePrefix + scriptName)
            ?? throw new InvalidOperationException($"Embedded schema script '{scriptName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
