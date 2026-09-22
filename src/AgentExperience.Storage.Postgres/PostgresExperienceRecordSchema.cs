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
