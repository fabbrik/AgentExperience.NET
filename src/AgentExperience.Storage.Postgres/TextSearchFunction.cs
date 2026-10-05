using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <c>agent_experience.search_experience_text</c> (story 17.7, <c>0024</c>): the text channel's search behind row-level
/// security, run as the owner so the planner can use the GIN indexes. A test holds the script equal to <see cref="Ddl"/>,
/// and the privileges call refuses to enable row-level security unless the catalog's function is exactly this one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> With row-level security on, the read policy on <c>experience_records</c> is a security barrier:
/// PostgreSQL evaluates a qual that is not <c>LEAKPROOF</c> only after the policy, and <c>@@</c> is not leakproof, so the
/// match cannot drive an index scan and a search reads every live record the declared bounds admit. The owner is not
/// bound by the policies (they are never forced), so a query the owner runs has no barrier and the match uses
/// <c>ix_experience_records_search</c> and <c>ix_experience_records_search_sealed</c> as it does with the policies off.
/// </para>
/// <para>
/// <b>Why it cannot read more than the policies.</b> It returns nothing unless the calling transaction declared bounds
/// (<c>agent_experience.auth_set = 'on'</c>), and nothing unless the requested scope lies inside them
/// (<c>rls_scope_admits</c>, <see cref="AgentExperience.Abstractions.AuthorizationContext.Permits"/>'s reading of a null
/// bound), so the scope that selects the permitting grant -- whose ID and level it returns -- is always one the bounds
/// admit, and <c>rls_grants_select</c> would show that grant to the caller. Every row it returns must satisfy the read policy's own admission --
/// <see cref="RowLevelSecurityPolicies.Readable(string)"/>, the same text over the same <c>0019</c> helpers
/// (<c>rls_bound</c>, <c>rls_unbounded</c>, <c>rls_granted_keys</c>) -- and then every predicate the store's own search
/// applies, built from the same constants (<see cref="PostgresExperienceCandidateSource.ReadableSearchHead"/>,
/// <see cref="PostgresExperienceCandidateSource.ExactSearchHead"/> and the two filter tails) with each
/// <c>@parameter</c> renamed to the function's argument. It reads only <c>experience_records</c> and
/// <c>experience_grants</c>, both schema-qualified, writes nothing, and its <c>search_path</c> is pinned with
/// <c>pg_temp</c> last.
/// </para>
/// <para>
/// <b>What it trusts.</b> The declared settings, exactly as the policies do: a session that can run arbitrary SQL as the
/// application role can declare any bounds, which KL-17 states.
/// </para>
/// </remarks>
internal static class TextSearchFunction
{
    /// <summary>The function's identity, as <c>to_regprocedure</c> reads it.</summary>
    internal const string Signature =
        "agent_experience.search_experience_text(text, text, text, text, text, text, text, text[], double precision, integer, boolean, boolean)";

    /// <summary>The single <c>proconfig</c> entry it must carry.</summary>
    internal const string Config = "search_path=pg_catalog, pg_temp";

    /// <summary>Its arguments, as <c>pg_get_function_arguments</c> renders them.</summary>
    internal const string Arguments =
        "p_tenant_id text, p_application_id text, p_project_id text, p_team_id text, p_agent_id text, p_user_id text, " +
        "p_task_text text, p_statuses text[], p_min_confidence double precision, p_limit integer, " +
        "p_exclude_model_authored boolean, p_with_grants boolean";

    /// <summary>
    /// The columns it returns, as <c>pg_get_function_result</c> renders them inside <c>TABLE(...)</c>: the record columns
    /// in <see cref="PostgresExperienceRecordStore.SelectColumns"/> order, then the relevance, the shared flag, and the
    /// permitting grant's ID and level -- exactly the columns the store's own statement selects.
    /// </summary>
    internal const string Columns =
        "experience_id uuid, source_run_id uuid, tenant_id text, application_id text, project_id text, team_id text, " +
        "agent_id text, user_id text, task_id text, status text, reuse_confidence double precision, " +
        "supporting_validations integer, contradictions integer, revision bigint, created_at timestamp with time zone, " +
        "updated_at timestamp with time zone, payload_version integer, payload jsonb, " +
        PostgresExperienceCandidateSource.RelevanceColumn + " real, " +
        PostgresExperienceRecordStore.SharedByGrantAlias + " boolean, " +
        PostgresExperienceRecordStore.PermittingGrantAlias + " uuid, " +
        PostgresExperienceRecordStore.PermittingDisclosureAlias + " text";

    /// <summary>What <c>pg_get_function_result</c> must say.</summary>
    internal const string Result = "TABLE(" + Columns + ")";

    /// <summary>The statement the candidate source runs in place of its own search while row-level security is on.</summary>
    internal const string CallSql =
        "SELECT * FROM agent_experience.search_experience_text(@tenant_id, @application_id, @project_id, @team_id, @agent_id, " +
        "@user_id, @task_text, @statuses, @min_confidence, @limit, @exclude_model_authored, @with_grants)";

    /// <summary>The store parameters the function's arguments stand in for, in the order <see cref="Parameterize"/> renames them.</summary>
    private static readonly string[] Parameters =
    [
        "tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id",
        "task_text", "statuses", "min_confidence", "limit",
    ];

    /// <summary>The function's body: <c>prosrc</c>, exactly.</summary>
    internal static string Body { get; } = BuildBody();

    /// <summary>The DDL <c>0024</c> contains for the function.</summary>
    internal static string Ddl { get; } =
        "CREATE OR REPLACE FUNCTION agent_experience.search_experience_text(\n" +
        "    " + Arguments.Replace(", p_", ",\n    p_", StringComparison.Ordinal) + ")\n" +
        "RETURNS TABLE (\n" +
        "    " + Columns.Replace(", ", ",\n    ", StringComparison.Ordinal) + ")\n" +
        "LANGUAGE plpgsql\n" +
        "STABLE\n" +
        "SECURITY DEFINER\n" +
        "SET search_path = pg_catalog, pg_temp\n" +
        "AS $body$" + Body + "$body$;\n";

    /// <summary>One of the store's statements with each <c>@parameter</c> renamed to the function's <c>p_</c> argument.</summary>
    internal static string Parameterize(string sql)
    {
        foreach (var name in Parameters)
        {
            sql = sql.Replace("@" + name, "p_" + name, StringComparison.Ordinal);
        }

        return sql;
    }

    private static string Query(string head, string filters) =>
        "            " + Parameterize(head) + "\n" +
        "            AND (" + RowLevelSecurityPolicies.Readable("r.") + ")\n" +
        "           " + Parameterize(filters);

    private static string BuildBody() =>
        "\n" +
        "#variable_conflict use_column\n" +
        "BEGIN\n" +
        "    IF pg_catalog.current_setting('agent_experience.auth_set', true) IS DISTINCT FROM 'on' THEN\n" +
        "        RETURN;\n" +
        "    END IF;\n" +
        "\n" +
        "    IF NOT agent_experience.rls_scope_admits(p_tenant_id, p_application_id, p_project_id, p_team_id, p_agent_id, p_user_id) THEN\n" +
        "        RETURN;\n" +
        "    END IF;\n" +
        "\n" +
        "    IF p_with_grants AND p_exclude_model_authored THEN\n" +
        "        RETURN QUERY\n" +
        Query(PostgresExperienceCandidateSource.ReadableSearchHead, PostgresExperienceCandidateSource.ExcludingSearchFilters) + ";\n" +
        "    ELSIF p_with_grants THEN\n" +
        "        RETURN QUERY\n" +
        Query(PostgresExperienceCandidateSource.ReadableSearchHead, PostgresExperienceCandidateSource.SearchFilters) + ";\n" +
        "    ELSIF p_exclude_model_authored THEN\n" +
        "        RETURN QUERY\n" +
        Query(PostgresExperienceCandidateSource.ExactSearchHead, PostgresExperienceCandidateSource.ExcludingSearchFilters) + ";\n" +
        "    ELSE\n" +
        "        RETURN QUERY\n" +
        Query(PostgresExperienceCandidateSource.ExactSearchHead, PostgresExperienceCandidateSource.SearchFilters) + ";\n" +
        "    END IF;\n" +
        "END\n";
}

/// <summary>
/// Whether a data source's text searches go through <see cref="TextSearchFunction"/>: exactly when row-level security is
/// enabled on <c>experience_records</c> and the connecting role may execute the function. Detected once per data source,
/// in a search's own transaction, and cached until the next privileges call in this process (which is what switches
/// row-level security and the function's grant), until <see cref="Lifetime"/> has passed (so a privileges call made by
/// another process takes effect within it), or until a call fails in a way that may mean the function is gone.
/// </summary>
internal static class TextSearchRoute
{
    /// <summary>How long a detected route is trusted before it is detected again.</summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const string UndefinedFunction = "42883";

    private const string InsufficientPrivilege = "42501";

    private const string DetectSql =
        "SELECT COALESCE((SELECT c.relrowsecurity FROM pg_catalog.pg_class c " +
        "WHERE c.oid = pg_catalog.to_regclass('agent_experience.experience_records')), false) " +
        "AND COALESCE(pg_catalog.has_function_privilege(pg_catalog.to_regprocedure(@signature)::oid, 'EXECUTE'), false)";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NpgsqlDataSource, State> States = new();

    private static int _generation;

    /// <summary><b>Test seam.</b> The clock <see cref="Lifetime"/> is measured on.</summary>
    internal static TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Called after every privileges call commits: every cached route is detected again on its next search.</summary>
    internal static void PrivilegesChanged() => Interlocked.Increment(ref _generation);

    /// <summary>Whether this data source's search should call the function, detecting it inside <paramref name="session"/> when not cached.</summary>
    internal static async Task<bool> UsesFunctionAsync(NpgsqlDataSource dataSource, AuthorizedTransaction session, CancellationToken cancellationToken)
    {
        var state = States.GetValue(dataSource, _ => new State());
        var generation = Volatile.Read(ref _generation);
        var now = Time.GetUtcNow();
        if (state.Current is { } cached && cached.Generation == generation && now - cached.DetectedAt < Lifetime && now >= cached.DetectedAt)
        {
            return cached.UseFunction;
        }

        await using var command = session.CreateCommand(DetectSql);
        command.Parameters.Add(new NpgsqlParameter<string>("signature", TextSearchFunction.Signature));
        var useFunction = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        state.Current = new Route(generation, useFunction, now);
        return useFunction;
    }

    /// <summary><b>Test seam.</b> The cached route, or <see langword="null"/> when none is cached.</summary>
    internal static bool? Cached(NpgsqlDataSource dataSource) =>
        States.TryGetValue(dataSource, out var state) ? state.Current?.UseFunction : null;

    /// <summary>Forgets the cached route, so the next search detects it again.</summary>
    internal static void Invalidate(NpgsqlDataSource dataSource)
    {
        if (States.TryGetValue(dataSource, out var state))
        {
            state.Current = null;
        }
    }

    /// <summary>
    /// Whether a failed call may mean the function is gone or no longer granted to this role. The caller then detects the
    /// route again and falls back only when the function is in fact unusable; otherwise the error is rethrown.
    /// </summary>
    internal static bool MayBeStale(PostgresException exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && exception.SqlState is UndefinedFunction or InsufficientPrivilege;

    private sealed record Route(int Generation, bool UseFunction, DateTimeOffset DetectedAt);

    private sealed class State
    {
        private Route? _current;

        public Route? Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }
    }
}
