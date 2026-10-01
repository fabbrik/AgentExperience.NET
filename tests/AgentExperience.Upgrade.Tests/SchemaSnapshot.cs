using Npgsql;

namespace AgentExperience.Upgrade.Tests;

/// <summary>
/// The <c>agent_experience</c> schema as the catalog describes it, one line per object, so an upgraded database can be
/// compared with a fresh install of today's schema: relations with their owners, privileges and row-level security;
/// row-level security policies, whole; columns with type,
/// nullability, default, generation and column privileges; constraints (with whether they are validated); indexes;
/// triggers with their enabled state; function definitions (<c>pg_get_functiondef</c>) with owner, security, settings
/// and privileges; sequences; the schema's owner and privileges; and the installed extensions. Nothing here reads a
/// row of data. Access-control lists are sorted, so two identical sets granted in a different order compare equal.
/// </summary>
internal static class SchemaSnapshot
{
    private const string Acl = "coalesce((SELECT string_agg(x::text, ',' ORDER BY x::text) FROM unnest({0}) x), '')";

    private static readonly string[] Queries =
    [
        "SELECT 'schema agent_experience owner=' || pg_get_userbyid(nspowner) || ' acl=' || " + string.Format(Acl, "nspacl") +
        " FROM pg_namespace WHERE nspname = 'agent_experience'",

        "SELECT 'relation ' || c.relname || ' kind=' || c.relkind::text || ' owner=' || pg_get_userbyid(c.relowner)" +
        " || ' rowsecurity=' || c.relrowsecurity || ' forcerowsecurity=' || c.relforcerowsecurity || ' acl=' || " + string.Format(Acl, "c.relacl") +
        " FROM pg_class c WHERE c.relnamespace = 'agent_experience'::regnamespace",

        // Story 15.1: every row-level security policy, whole -- command, permissiveness, roles and both expressions.
        "SELECT 'policy ' || p.polrelid::regclass::text || ' ' || p.polname || ' cmd=' || p.polcmd::text || ' permissive=' || p.polpermissive" +
        " || ' roles=' || coalesce((SELECT string_agg(CASE WHEN r = 0 THEN 'public' ELSE pg_get_userbyid(r)::text END, ',' ORDER BY 1) FROM unnest(p.polroles) r), '')" +
        " || ' using=' || coalesce(pg_get_expr(p.polqual, p.polrelid), '') || ' check=' || coalesce(pg_get_expr(p.polwithcheck, p.polrelid), '')" +
        " FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid WHERE c.relnamespace = 'agent_experience'::regnamespace",

        "SELECT 'column ' || c.relname || '.' || a.attname || ' type=' || format_type(a.atttypid, a.atttypmod) || ' notnull=' || a.attnotnull" +
        " || ' default=' || coalesce(pg_get_expr(d.adbin, d.adrelid), '') || ' generated=' || a.attgenerated::text || ' identity=' || a.attidentity::text" +
        " || ' acl=' || " + string.Format(Acl, "a.attacl") +
        " FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum" +
        " WHERE c.relnamespace = 'agent_experience'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm', 'f') AND a.attnum > 0 AND NOT a.attisdropped",

        "SELECT 'constraint ' || conrelid::regclass::text || ' ' || conname || ' type=' || contype::text || ' validated=' || convalidated" +
        " || ' ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE connamespace = 'agent_experience'::regnamespace",

        "SELECT 'index ' || i.indexrelid::regclass::text || ' valid=' || i.indisvalid || ' ' || pg_get_indexdef(i.indexrelid)" +
        " FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid WHERE c.relnamespace = 'agent_experience'::regnamespace",

        "SELECT 'trigger ' || t.tgrelid::regclass::text || ' ' || t.tgname || ' enabled=' || t.tgenabled::text || ' ' || pg_get_triggerdef(t.oid)" +
        " FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid WHERE NOT t.tgisinternal AND c.relnamespace = 'agent_experience'::regnamespace",

        "SELECT 'function ' || p.oid::regprocedure::text || ' owner=' || pg_get_userbyid(p.proowner) || ' security_definer=' || p.prosecdef" +
        " || ' config=' || coalesce(array_to_string(p.proconfig, ','), '') || ' acl=' || " + string.Format(Acl, "p.proacl") +
        " || E'\\n' || pg_get_functiondef(p.oid) FROM pg_proc p WHERE p.pronamespace = 'agent_experience'::regnamespace",

        "SELECT 'sequence ' || sequencename || ' owner=' || sequenceowner || ' type=' || data_type || ' start=' || start_value || ' min=' || min_value" +
        " || ' max=' || max_value || ' increment=' || increment_by || ' cycle=' || cycle || ' cache=' || cache_size" +
        " FROM pg_sequences WHERE schemaname = 'agent_experience'",

        "SELECT 'extension ' || extname || ' version=' || extversion || ' schema=' || extnamespace::regnamespace::text FROM pg_extension",
    ];

    /// <summary>Every line, sorted.</summary>
    public static async Task<IReadOnlyList<string>> TakeAsync(NpgsqlDataSource dataSource)
    {
        var lines = new List<string>();
        foreach (var query in Queries)
        {
            await using var command = dataSource.CreateCommand(query);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    /// <summary>
    /// The first line (in sorted order) that only one of the two snapshots has, described, or <see langword="null"/> when
    /// they are identical. The count of every differing line comes with it.
    /// </summary>
    public static string? FirstDifference(IReadOnlyList<string> upgraded, IReadOnlyList<string> fresh)
    {
        var onlyUpgraded = upgraded.Except(fresh, StringComparer.Ordinal).ToList();
        var onlyFresh = fresh.Except(upgraded, StringComparer.Ordinal).ToList();
        if (onlyUpgraded.Count == 0 && onlyFresh.Count == 0)
        {
            return upgraded.Count == fresh.Count ? null : $"the same objects, but {upgraded.Count} lines after the upgrade and {fresh.Count} in a fresh install";
        }

        var first = onlyUpgraded.Select(line => (Line: line, Side: "only after the upgrade"))
            .Concat(onlyFresh.Select(line => (Line: line, Side: "only in a fresh install")))
            .OrderBy(entry => entry.Line, StringComparer.Ordinal)
            .First();
        var described = $"{onlyUpgraded.Count + onlyFresh.Count} catalog lines differ; the first is {first.Side}: {UpgradeReport.Truncate(first.Line.ReplaceLineEndings(" "))}";

        // A function whose header matches but whose body does not: show where the definitions part.
        var header = Header(first.Line);
        var counterpart = (first.Side == "only after the upgrade" ? onlyFresh : onlyUpgraded).FirstOrDefault(line => Header(line) == header);
        if (counterpart is not null)
        {
            var at = 0;
            while (at < first.Line.Length && at < counterpart.Length && first.Line[at] == counterpart[at])
            {
                at++;
            }

            var from = Math.Max(0, at - 60);
            described += $" (it parts from the other side's '{Snippet(counterpart, from)}' at '{Snippet(first.Line, from)}')";
        }

        return described;
    }

    private static string Header(string line) => line.Split('\n')[0].Split(" acl=")[0].Split(" validated=")[0];

    private static string Snippet(string line, int from) =>
        line.Substring(from, Math.Min(140, line.Length - from)).ReplaceLineEndings(" ");
}
