namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The canonical row-level security policies: every <c>rls_*</c> policy <c>0019</c> creates on this
/// package's tables and the vectors package's <c>0020</c> creates on <c>experience_embeddings</c>, as the exact DDL
/// those scripts contain. The privileges call re-runs this DDL, under the migrator's lock, every time it enables row-level
/// security, so a policy altered or dropped by hand since the migration is put back before anything is enabled; and
/// <c>OfflineStoreTests</c> holds the two scripts equal to it, so the migrations and this list cannot drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a bound is compared.</b> Every comparison with a declared bound goes through an uncorrelated subquery,
/// <c>(SELECT agent_experience.rls_bound('tenant'))</c>, which PostgreSQL evaluates once per statement as an InitPlan:
/// no helper function runs per row, and <c>tenant_id = (SELECT ...)</c> is an ordinary index condition.
/// </para>
/// <para>
/// <b>How a grant is admitted.</b> <c>agent_experience.rls_granted_keys()</c> returns, once per statement, the
/// identity (the record and its six owner columns, as a row literal) of every grant that is live and whose recipient
/// lies inside the declared bounds; a record or embedding row is admitted for reading when its own identity is in that
/// set. PostgreSQL hashes an uncorrelated <c>IN (SELECT ...)</c>, so this is a hash probe per row, and only for a row the
/// scope branch has not already admitted. The set is taken at the start of the statement, from its snapshot, and a
/// grant only ever stops being live after that, so it never admits less than the stores' own per-row grant predicate.
/// </para>
/// <para>
/// Tenant, application and project are stated first and outside the grant branch: no grant crosses them
/// (<c>experience_grants_same_boundary</c>), so a row a grant admits already lies inside those three bounds.
/// </para>
/// </remarks>
internal static class RowLevelSecurityPolicies
{
    /// <summary>One policy: its table, name, command and the two expressions, exactly as the scripts write them.</summary>
    internal sealed record Policy(string Table, string Name, string Command, string? Using, string? Check)
    {
        /// <summary>The DDL the scripts contain for this policy, and that the privileges call re-runs.</summary>
        public string Ddl =>
            $"DROP POLICY IF EXISTS {Name} ON agent_experience.{Table};\n" +
            $"CREATE POLICY {Name} ON agent_experience.{Table}\n" +
            $"    FOR {Command}\n" +
            (Using is null ? string.Empty : $"    USING (\n        {Using})") +
            (Using is not null && Check is not null ? "\n" : string.Empty) +
            (Check is null ? string.Empty : $"    WITH CHECK (\n        {Check})") +
            ";\n";
    }

    /// <summary>
    /// The helper functions the policies call, exactly as <c>0019</c> creates them (a test holds the script equal), each
    /// with what the catalog must then say about it. The privileges call re-runs this DDL whenever it enables row-level
    /// security -- <c>CREATE OR REPLACE</c> keeps each function's owner and ACL -- so a helper replaced by hand since the
    /// migration is put back, and then verifies it.
    /// </summary>
    internal static IReadOnlyList<HelperFunction> Functions { get; } =
    [
        new(
            "agent_experience.rls_bound(text)",
            's',
            null,
            """
CREATE OR REPLACE FUNCTION agent_experience.rls_bound(p_field text)
RETURNS text
LANGUAGE sql
STABLE
PARALLEL SAFE
RETURN CASE
    WHEN pg_catalog.current_setting('agent_experience.auth_set', true) = 'on'
        AND pg_catalog.left(pg_catalog.current_setting('agent_experience.auth_' || p_field, true), 1) = '='
    THEN pg_catalog.substr(pg_catalog.current_setting('agent_experience.auth_' || p_field, true), 2)
END;
""".Replace("\r\n", "\n", StringComparison.Ordinal)),
        new(
            "agent_experience.rls_unbounded(text)",
            's',
            null,
            """
CREATE OR REPLACE FUNCTION agent_experience.rls_unbounded(p_field text)
RETURNS boolean
LANGUAGE sql
STABLE
PARALLEL SAFE
RETURN COALESCE(
    pg_catalog.current_setting('agent_experience.auth_set', true) = 'on'
    AND pg_catalog.current_setting('agent_experience.auth_' || p_field, true) = '',
    false);
""".Replace("\r\n", "\n", StringComparison.Ordinal)),
        new(
            "agent_experience.rls_scope_admits(text, text, text, text, text, text)",
            's',
            null,
            """
CREATE OR REPLACE FUNCTION agent_experience.rls_scope_admits(
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text)
RETURNS boolean
LANGUAGE sql
STABLE
PARALLEL SAFE
RETURN COALESCE(
    p_tenant_id = agent_experience.rls_bound('tenant')
    AND (p_application_id = agent_experience.rls_bound('application') OR agent_experience.rls_unbounded('application'))
    AND (p_project_id = agent_experience.rls_bound('project') OR agent_experience.rls_unbounded('project'))
    AND (p_team_id = agent_experience.rls_bound('team') OR agent_experience.rls_unbounded('team'))
    AND (p_agent_id = agent_experience.rls_bound('agent') OR agent_experience.rls_unbounded('agent'))
    AND (p_user_id = agent_experience.rls_bound('user') OR agent_experience.rls_unbounded('user')),
    false);
""".Replace("\r\n", "\n", StringComparison.Ordinal)),
        new(
            "agent_experience.rls_granted_keys()",
            's',
            "search_path=pg_catalog, pg_temp",
            """
CREATE OR REPLACE FUNCTION agent_experience.rls_granted_keys()
RETURNS SETOF text
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
SET search_path = pg_catalog, pg_temp
AS $body$
DECLARE
    v_tenant text := agent_experience.rls_bound('tenant');
BEGIN
    IF v_tenant IS NULL
        OR pg_catalog.to_regclass('agent_experience.experience_grants') IS NULL
        OR NOT pg_catalog.has_table_privilege('agent_experience.experience_grants', 'SELECT')
    THEN
        RETURN;
    END IF;

    RETURN QUERY
        SELECT ROW(g.experience_id, g.tenant_id, g.application_id, g.project_id, g.team_id, g.agent_id, g.user_id)::text
        FROM agent_experience.experience_grants g
        WHERE g.recipient_tenant_id = v_tenant
          AND g.revoked_at IS NULL
          AND g.expires_at > pg_catalog.clock_timestamp()
          AND agent_experience.rls_scope_admits(
              g.recipient_tenant_id, g.recipient_application_id, g.recipient_project_id,
              g.recipient_team_id, g.recipient_agent_id, g.recipient_user_id);
END
$body$;
""".Replace("\r\n", "\n", StringComparison.Ordinal)),
        new(
            "agent_experience.rls_access_grant_live(uuid, uuid, text, text, text, text, text, text, text, text, text, text, text, text)",
            's',
            "search_path=pg_catalog, pg_temp",
            """
CREATE OR REPLACE FUNCTION agent_experience.rls_access_grant_live(
    p_grant_id uuid,
    p_experience_id uuid,
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_recipient_tenant_id text,
    p_recipient_application_id text,
    p_recipient_project_id text,
    p_recipient_team_id text,
    p_recipient_agent_id text,
    p_recipient_user_id text)
RETURNS boolean
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
SET search_path = pg_catalog, pg_temp
AS $body$
BEGIN
    IF pg_catalog.to_regclass('agent_experience.experience_grants') IS NULL
        OR NOT pg_catalog.has_table_privilege('agent_experience.experience_grants', 'SELECT')
    THEN
        RETURN false;
    END IF;

    RETURN EXISTS (
        SELECT 1
        FROM agent_experience.experience_grants g
        WHERE g.grant_id = p_grant_id
          AND g.experience_id = p_experience_id
          AND g.revoked_at IS NULL
          AND g.expires_at > pg_catalog.clock_timestamp()
          AND g.tenant_id = p_tenant_id
          AND g.application_id = p_application_id
          AND g.project_id = p_project_id
          AND g.team_id IS NOT DISTINCT FROM p_team_id
          AND g.agent_id IS NOT DISTINCT FROM p_agent_id
          AND g.user_id IS NOT DISTINCT FROM p_user_id
          AND g.recipient_tenant_id = p_recipient_tenant_id
          AND g.recipient_application_id = p_recipient_application_id
          AND g.recipient_project_id = p_recipient_project_id
          AND g.recipient_team_id IS NOT DISTINCT FROM p_recipient_team_id
          AND g.recipient_agent_id IS NOT DISTINCT FROM p_recipient_agent_id
          AND g.recipient_user_id IS NOT DISTINCT FROM p_recipient_user_id);
END
$body$;
""".Replace("\r\n", "\n", StringComparison.Ordinal)),
    ];

    /// <summary>
    /// One helper: its signature, the volatility (<c>provolatile</c>) and the single <c>proconfig</c> entry it must carry,
    /// and the DDL. None is <c>SECURITY DEFINER</c>.
    /// </summary>
    internal sealed record HelperFunction(string Signature, char Volatility, string? Config, string Ddl);

    /// <summary>Every policy, in the order the scripts create them.</summary>
    internal static IReadOnlyList<Policy> All { get; } = Build();

    /// <summary>The policies on one table, by name.</summary>
    internal static IReadOnlyList<string> NamesFor(string table) =>
        [.. All.Where(p => p.Table == table).Select(p => p.Name).Order(StringComparer.Ordinal)];

    private static string Bound(string field) => $"(SELECT agent_experience.rls_bound('{field}'))";

    private static string Open(string field) => $"(SELECT agent_experience.rls_unbounded('{field}'))";

    private static string Required(string column) => $"{column} = {Bound("tenant")}";

    private static string Optional(string column, string field) => $"({column} = {Bound(field)} OR {Open(field)})";

    private static string[] Boundary(string prefix) =>
    [
        Required(prefix + "tenant_id"),
        Optional(prefix + "application_id", "application"),
        Optional(prefix + "project_id", "project"),
    ];

    private static string[] Below(string prefix) =>
    [
        Optional(prefix + "team_id", "team"),
        Optional(prefix + "agent_id", "agent"),
        Optional(prefix + "user_id", "user"),
    ];

    private static string Join(IEnumerable<string> terms, string indent) => string.Join($"\n{indent}AND ", terms);

    /// <summary>The row's own scope inside the declared bounds.</summary>
    private static string Scope(string prefix = "", string indent = "        ") => Join([.. Boundary(prefix), .. Below(prefix)], indent);

    /// <summary>The row's recipient scope inside the declared bounds.</summary>
    private static string Recipient(string indent = "        ") => Join(
        [
            Required("recipient_tenant_id"),
            Optional("recipient_application_id", "application"),
            Optional("recipient_project_id", "project"),
            Optional("recipient_team_id", "team"),
            Optional("recipient_agent_id", "agent"),
            Optional("recipient_user_id", "user"),
        ],
        indent);

    /// <summary>The identity a live grant over this row would carry, as <c>rls_granted_keys()</c> writes it.</summary>
    private static string GrantKey(string prefix) =>
        $"ROW({prefix}experience_id, {prefix}tenant_id, {prefix}application_id, {prefix}project_id, " +
        $"{prefix}team_id, {prefix}agent_id, {prefix}user_id)::text IN (SELECT agent_experience.rls_granted_keys())";

    /// <summary>A record or embedding a read may see: its scope inside the bounds, or a live grant shares it.</summary>
    private static string Readable() => Readable(string.Empty);

    /// <summary>
    /// <see cref="Readable()"/> over a <paramref name="prefix"/>-qualified row: the read policy's own admission, through
    /// the same helpers, for <see cref="TextSearchFunction"/>, which runs as the owner and so must apply it
    /// itself. With an empty prefix it is the policy text exactly.
    /// </summary>
    internal static string Readable(string prefix) =>
        $"{Join(Boundary(prefix), "        ")}\n" +
        "        AND (\n" +
        $"            ({Join(Below(prefix), "            ")})\n" +
        $"            OR {GrantKey(prefix)})";

    private static List<Policy> Build()
    {
        var scope = Scope();
        var policies = new List<Policy>
        {
            new("experience_records", "rls_records_select", "SELECT", Readable(), null),
            new("experience_records", "rls_records_insert", "INSERT", null, scope),
            new("experience_records", "rls_records_update", "UPDATE", scope, scope),

            new("lifecycle_events", "rls_lifecycle_events_select", "SELECT", scope, null),
            new("lifecycle_events", "rls_lifecycle_events_insert", "INSERT", null, scope),
        };

        var evidence =
            "EXISTS (\n" +
            "            SELECT 1 FROM agent_experience.experience_records r\n" +
            "            WHERE r.experience_id = confidence_evidence.experience_id\n" +
            $"              AND {Scope("r.", "              ")})";
        policies.Add(new("confidence_evidence", "rls_confidence_evidence_select", "SELECT", evidence, null));
        policies.Add(new("confidence_evidence", "rls_confidence_evidence_insert", "INSERT", null, evidence));

        // A grant is read by its owner, and by its recipient only while it is live.
        var grantRead =
            $"({scope})\n" +
            $"        OR ({Recipient()}\n" +
            "        AND revoked_at IS NULL\n" +
            "        AND expires_at > pg_catalog.clock_timestamp())";
        policies.Add(new("experience_grants", "rls_grants_select", "SELECT", grantRead, null));
        policies.Add(new("experience_grants", "rls_grants_insert", "INSERT", null, scope));
        policies.Add(new("experience_grants", "rls_grants_update", "UPDATE", scope, scope));

        policies.Add(new("experience_grant_events", "rls_grant_events_select", "SELECT", scope, null));
        policies.Add(new("experience_grant_events", "rls_grant_events_insert", "INSERT", null, scope));

        // An access row is appended by the reader, about a live grant that names exactly its owner and recipient.
        policies.Add(new("experience_grant_access", "rls_grant_access_select", "SELECT", $"({scope})\n        OR ({Recipient()})", null));
        policies.Add(new(
            "experience_grant_access",
            "rls_grant_access_insert",
            "INSERT",
            null,
            $"{Recipient()}\n" +
            "        AND agent_experience.rls_access_grant_live(\n" +
            "            grant_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id,\n" +
            "            recipient_tenant_id, recipient_application_id, recipient_project_id,\n" +
            "            recipient_team_id, recipient_agent_id, recipient_user_id)"));

        policies.Add(new("reuse_feedback", "rls_reuse_feedback_select", "SELECT", scope, null));
        policies.Add(new("reuse_feedback", "rls_reuse_feedback_insert", "INSERT", null, scope));

        var submission =
            "EXISTS (\n" +
            "            SELECT 1 FROM agent_experience.reuse_feedback f\n" +
            "            WHERE f.feedback_id = reuse_feedback_exposures.feedback_id\n" +
            $"              AND {Scope("f.", "              ")})";
        policies.Add(new("reuse_feedback_exposures", "rls_reuse_feedback_exposures_select", "SELECT", submission, null));

        // Its submission's scope only. The record an exposure names is not checked here: the port lets a submission
        // name a record the store does not hold, and a record outside the bounds is, to these policies, exactly as
        // invisible as one that does not exist, so no policy can tell the two apart (see docs/guide/deployment.md).
        policies.Add(new("reuse_feedback_exposures", "rls_reuse_feedback_exposures_insert", "INSERT", null, submission));

        policies.Add(new(ApplicationRolePrivileges.EmbeddingsTable, "rls_embeddings_select", "SELECT", Readable(), null));
        policies.Add(new(ApplicationRolePrivileges.EmbeddingsTable, "rls_embeddings_insert", "INSERT", null, scope));
        policies.Add(new(ApplicationRolePrivileges.EmbeddingsTable, "rls_embeddings_update", "UPDATE", scope, scope));
        policies.Add(new(ApplicationRolePrivileges.EmbeddingsTable, "rls_embeddings_delete", "DELETE", scope, null));
        return policies;
    }
}
