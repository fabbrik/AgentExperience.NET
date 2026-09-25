using AgentExperience.Abstractions;
using Npgsql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The privilege manifest for the application role, and the one transaction that applies and verifies
/// it. See
/// <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(NpgsqlDataSource, ExperienceApplicationRoleOptions, CancellationToken)"/>.
/// </summary>
internal static class ApplicationRolePrivileges
{
    internal const string SchemaName = PostgresExperienceRecordSchema.SchemaName;

    /// <summary>The optional table the vectors package creates; granted only when it exists.</summary>
    internal const string EmbeddingsTable = "experience_embeddings";

    /// <summary>
    /// Every table the application role gets anything on, and exactly what. A table in the schema that is
    /// not listed here -- the migration journal, or anything a later migration adds before this list names
    /// it -- gets nothing, and the verification fails if the role can reach it anyway.
    /// </summary>
    internal static IReadOnlyList<TablePrivileges> Tables { get; } =
    [
        // The projection. UPDATE is column-level: the lifecycle commit moves only these six, row locks need
        // UPDATE on at least one column, and without deleted_at, payload, task_id, created_at and
        // source_run_id the role cannot write 0010's tombstone shape by hand under a hand-set marker.
        new("experience_records", Delete: false,
            ["contradictions", "reuse_confidence", "revision", "status", "supporting_validations", "updated_at"]),

        // A grant's revocation is its only UPDATE.
        new("experience_grants", Delete: false, ["revocation_reason", "revoked_at"]),

        // The ledgers: append and read, nothing else.
        new("lifecycle_events", Delete: false, []),
        new("experience_grant_events", Delete: false, []),
        new("confidence_evidence", Delete: false, []),
        new("reuse_feedback", Delete: false, []),
        new("reuse_feedback_exposures", Delete: false, []),
        new("experience_grant_access", Delete: false, []),

        // Derived, rebuildable data owned by the vectors package: the index upserts and removes vectors.
        // UPDATE is column-level, the columns the upsert's ON CONFLICT DO UPDATE sets: never experience_id,
        // created_at or the scope columns that the removal matches on.
        new(EmbeddingsTable, Delete: true,
            ["content_hash", "dimension", "embedding", "model_id", "source_revision", "updated_at"], Optional: true),
    ];

    /// <summary>
    /// The <c>SECURITY DEFINER</c> functions -- the three purges and <c>0016</c>'s sealing transition -- by
    /// signature, and the opt-in that grants each. Every one is revoked from <c>PUBLIC</c> by its script, and
    /// the verification refuses any <c>SECURITY DEFINER</c> function the role can reach without its opt-in.
    /// </summary>
    internal static IReadOnlyList<(string Signature, Func<ExperienceApplicationRoleOptions, bool> Granted)> PurgeFunctions { get; } =
    [
        ("agent_experience.purge_experience_record(uuid, text, text, text, text, text, text, bigint, timestamptz)", o => o.AllowErasure),
        ("agent_experience.purge_expired_grants(text, text, text, text, text, text, timestamptz, integer)", o => o.AllowErasure),
        ("agent_experience.purge_grant_access(text, text, text, text, text, text, boolean, timestamptz, integer)", o => o.AllowAccessLogPurge),
        ("agent_experience.seal_experience_record(uuid, text, text, text, text, text, text, bigint, jsonb)", o => o.AllowSealing),
    ];

    internal static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        ExperienceApplicationRoleOptions options,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var schemaOid = await ScalarAsync<uint?>(
            connection, transaction, "SELECT to_regnamespace(@schema)::oid", cancellationToken, ("schema", SchemaName)).ConfigureAwait(false)
            ?? throw Refused("the agent_experience schema does not exist; run ExperienceSchemaMigrator.MigrateAsync first");

        var role = await ReadRoleAsync(connection, transaction, options.RoleName, cancellationToken).ConfigureAwait(false);
        await RefuseOwnershipAsync(connection, transaction, role, schemaOid, cancellationToken).ConfigureAwait(false);
        await RefuseDangerousMembershipsAsync(connection, transaction, role, cancellationToken).ConfigureAwait(false);

        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in Tables)
        {
            var present = await ScalarAsync<bool>(
                connection, transaction, "SELECT to_regclass(@name) IS NOT NULL", cancellationToken,
                ("name", $"{SchemaName}.{table.Name}")).ConfigureAwait(false);
            if (present)
            {
                existing.Add(table.Name);
            }
            else if (!table.Optional)
            {
                throw Refused($"the table {SchemaName}.{table.Name} does not exist; run ExperienceSchemaMigrator.MigrateAsync first");
            }
        }

        var purgeOids = new Dictionary<uint, bool>();
        foreach (var (signature, granted) in PurgeFunctions)
        {
            var oid = await ScalarAsync<uint?>(
                connection, transaction, "SELECT to_regprocedure(@signature)::oid", cancellationToken, ("signature", signature)).ConfigureAwait(false)
                ?? throw Refused($"the function {signature} does not exist; run ExperienceSchemaMigrator.MigrateAsync first");
            purgeOids[oid] = granted(options);
        }

        foreach (var statement in Statements(role.QuotedName, existing, options))
        {
            await ExecuteAsync(connection, transaction, statement, cancellationToken).ConfigureAwait(false);
        }

        var violations = new List<string>();
        await VerifySchemaAsync(connection, transaction, role, schemaOid, violations, cancellationToken).ConfigureAwait(false);
        await VerifyRelationsAsync(connection, transaction, role, schemaOid, existing, violations, cancellationToken).ConfigureAwait(false);
        await VerifyFunctionsAsync(connection, transaction, role, schemaOid, purgeOids, violations, cancellationToken).ConfigureAwait(false);

        if (violations.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new ExperienceStoreException(
                $"The application role {role.QuotedName} does not hold exactly the privileges the stores need, so nothing was " +
                "changed. Run this as the role that owns the agent_experience schema, and remove any grant that reaches the " +
                $"role another way (PUBLIC, another grantor, a role it is a member of): {string.Join("; ", violations)}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The statements, in order: take everything away, then give back the manifest. One transaction, so
    /// the role is never observed half-granted. <paramref name="role"/> is the server's own
    /// <c>quote_ident</c> of a role that exists, never caller text.
    /// </summary>
    internal static IEnumerable<string> Statements(string role, IReadOnlySet<string> existingTables, ExperienceApplicationRoleOptions options)
    {
        yield return $"REVOKE ALL ON SCHEMA {SchemaName} FROM {role}";
        yield return $"REVOKE ALL ON ALL TABLES IN SCHEMA {SchemaName} FROM {role}";
        yield return $"REVOKE ALL ON ALL SEQUENCES IN SCHEMA {SchemaName} FROM {role}";
        yield return $"REVOKE ALL ON ALL ROUTINES IN SCHEMA {SchemaName} FROM {role}";

        yield return $"GRANT USAGE ON SCHEMA {SchemaName} TO {role}";

        foreach (var table in Tables.Where(t => existingTables.Contains(t.Name)))
        {
            var privileges = new List<string> { "SELECT", "INSERT" };
            if (table.Delete)
            {
                privileges.Add("DELETE");
            }

            yield return $"GRANT {string.Join(", ", privileges)} ON {SchemaName}.{table.Name} TO {role}";

            if (table.UpdateColumns.Count > 0)
            {
                yield return $"GRANT UPDATE ({string.Join(", ", table.UpdateColumns)}) ON {SchemaName}.{table.Name} TO {role}";
            }
        }

        foreach (var (signature, granted) in PurgeFunctions)
        {
            if (granted(options))
            {
                yield return $"GRANT EXECUTE ON FUNCTION {signature} TO {role}";
            }
        }
    }

    private static async Task<RoleFacts> ReadRoleAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string roleName, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT r.oid, r.rolsuper, quote_ident(r.rolname), r.rolname = current_user FROM pg_catalog.pg_roles r WHERE r.rolname = @role",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("role", roleName));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Refused("no role by that name exists; create it first (see the store README's two-role deployment)");
        }

        var facts = new RoleFacts(reader.GetFieldValue<uint>(0), reader.GetString(2));
        var superuser = reader.GetBoolean(1);
        var self = reader.GetBoolean(3);

        if (superuser)
        {
            throw Refused($"{facts.QuotedName} is a superuser, and no privilege or trigger binds a superuser");
        }

        if (self)
        {
            throw Refused(
                $"{facts.QuotedName} is the role running this call. The application role must be a separate role from the owner " +
                "that runs the migrator, or it can ALTER TABLE its way past every guard");
        }

        return facts;
    }

    /// <summary>
    /// Membership in an owning role is ownership: a member can <c>SET ROLE</c> to the owner (or inherits
    /// its rights) and then <c>ALTER TABLE</c>, disable a trigger, or replace a guard function.
    /// </summary>
    private static async Task RefuseOwnershipAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RoleFacts role, uint schemaOid, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT quote_ident(pg_catalog.pg_get_userbyid(o.owner)) FROM (" +
            "SELECT n.nspowner AS owner FROM pg_catalog.pg_namespace n WHERE n.oid = @schema " +
            "UNION SELECT c.relowner FROM pg_catalog.pg_class c WHERE c.relnamespace = @schema " +
            "UNION SELECT p.proowner FROM pg_catalog.pg_proc p WHERE p.pronamespace = @schema " +
            "UNION SELECT t.typowner FROM pg_catalog.pg_type t WHERE t.typnamespace = @schema " +
            // The database's owner can DROP DATABASE -- every ledger at once -- so it is refused too.
            "UNION SELECT d.datdba FROM pg_catalog.pg_database d WHERE d.datname = pg_catalog.current_database()) o " +
            "WHERE pg_catalog.pg_has_role(@role, o.owner, 'MEMBER') ORDER BY 1",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<uint>("schema", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = schemaOid });
        command.Parameters.Add(new NpgsqlParameter<uint>("role", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = role.Oid });

        var owners = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                owners.Add(reader.GetString(0));
            }
        }

        if (owners.Count > 0)
        {
            throw Refused(
                $"{role.QuotedName} is, or is a member of, a role that owns the database, the schema or an object in it ({string.Join(", ", owners)}). " +
                "An owner is not bound by the append-only guards; the application role must own nothing");
        }
    }

    /// <summary>
    /// Every role the application role is a member of, directly or not, with or without <c>INHERIT</c>
    /// or <c>SET</c>, including itself. has_*_privilege counts only what a role inherits, so a
    /// <c>NOINHERIT</c> role, or a PostgreSQL 16 membership granted <c>WITH INHERIT FALSE</c>, would hide
    /// what the role reaches by <c>SET ROLE</c>. The checks that forbid something run over this whole set.
    /// </summary>
    private const string ReachCte =
        "WITH reach AS (SELECT r.oid FROM pg_catalog.pg_roles r WHERE pg_catalog.pg_has_role(@role, r.oid, 'MEMBER')) ";

    /// <summary>
    /// A role reachable from the application role that bypasses every privilege and trigger (a superuser),
    /// reaches the server's files or programs (and from there the data directory), or may switch triggers
    /// off with <c>session_replication_role = replica</c>, is refused: nothing in the schema binds it. So is
    /// PostgreSQL 17's <c>pg_maintain</c>, which holds <c>MAINTAIN</c> on every table: no trigger fires on
    /// <c>LOCK TABLE</c>, <c>CLUSTER</c>, <c>REINDEX</c> or <c>VACUUM</c>, so an application role that can
    /// run them can hold every ledger's lock indefinitely or rewrite a table under the owner's feet.
    /// </summary>
    private static async Task RefuseDangerousMembershipsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RoleFacts role, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            ReachCte +
            "SELECT quote_ident(r.rolname), CASE " +
            "WHEN r.rolsuper THEN 'a superuser' " +
            "WHEN r.rolname IN ('pg_execute_server_program', 'pg_write_server_files', 'pg_read_server_files') THEN 'server file or program access' " +
            "WHEN r.rolname = 'pg_maintain' THEN 'MAINTAIN on every table, which includes LOCK TABLE, CLUSTER and REINDEX' " +
            "ELSE 'SET on session_replication_role, which switches the guard triggers off' END " +
            "FROM reach JOIN pg_catalog.pg_roles r ON r.oid = reach.oid " +
            "WHERE r.rolsuper " +
            "OR r.rolname IN ('pg_execute_server_program', 'pg_write_server_files', 'pg_read_server_files') " +
            // pg_maintain exists from PostgreSQL 17; on 15 and 16 no role has that name, so this matches nothing.
            "OR r.rolname = 'pg_maintain' " +
            "OR pg_catalog.has_parameter_privilege(r.oid, 'session_replication_role', 'SET') ORDER BY 1",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<uint>("role", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = role.Oid });

        var reasons = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                reasons.Add($"{reader.GetString(0)} ({reader.GetString(1)})");
            }
        }

        if (reasons.Count > 0)
        {
            throw Refused(
                $"{role.QuotedName} is, or is a member of, a role that no guard binds: {string.Join(", ", reasons)}. " +
                "The application role must hold no such membership or privilege");
        }
    }

    private static async Task VerifySchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RoleFacts role,
        uint schemaOid,
        List<string> violations,
        CancellationToken cancellationToken)
    {
        // CREATE in the schema would let the role plant a better-matching function or operator on the
        // pinned search_path of the SECURITY DEFINER purges and the guards; an owner's REVOKE cannot remove
        // CREATE that reaches the role through PUBLIC, another grantor or a membership.
        await using var command = new NpgsqlCommand(
            ReachCte +
            "SELECT pg_catalog.has_schema_privilege(@role, @schema, 'USAGE'), " +
            "EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_schema_privilege(m.oid, @schema, 'CREATE')), " +
            "EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_schema_privilege(m.oid, @schema, 'USAGE WITH GRANT OPTION'))",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<uint>("schema", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = schemaOid });
        command.Parameters.Add(new NpgsqlParameter<uint>("role", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = role.Oid });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!reader.GetBoolean(0))
        {
            violations.Add($"{SchemaName}: USAGE on the schema is missing");
        }

        if (reader.GetBoolean(1))
        {
            violations.Add($"{SchemaName}: the role can CREATE in the schema");
        }

        if (reader.GetBoolean(2))
        {
            violations.Add($"{SchemaName}: the role holds USAGE on the schema with GRANT OPTION");
        }
    }

    private static async Task VerifyRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RoleFacts role,
        uint schemaOid,
        IReadOnlySet<string> existingTables,
        List<string> violations,
        CancellationToken cancellationToken)
    {
        // Effective privileges: has_*_privilege sees PUBLIC, every grantor, inherited memberships and the
        // predefined roles (pg_read_all_data, pg_write_all_data), which an owner's REVOKE cannot remove.
        // What must be present is checked on the role itself; what must be absent is checked on every
        // role in its reach, so a privilege one SET ROLE away is caught too.
        //
        // MAINTAIN (PostgreSQL 17+) is a table privilege of its own. The privilege name is not known to 15
        // or 16, where has_table_privilege would raise on it, so the column is only asked for on 17+ and is
        // a constant false below that; there is nothing to hold on a server that has no such privilege.
        var maintain = connection.PostgreSqlVersion.Major >= 17
            ? "c.relkind <> 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_table_privilege(m.oid, c.oid, 'MAINTAIN')) "
            : "false ";
        await using var command = new NpgsqlCommand(
            ReachCte +
            "SELECT c.relname::text, c.relkind = 'S', " +
            "c.relkind = 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_sequence_privilege(m.oid, c.oid, 'USAGE,SELECT,UPDATE')), " +
            "c.relkind <> 'S' AND pg_catalog.has_table_privilege(@role, c.oid, 'SELECT'), " +
            "c.relkind <> 'S' AND pg_catalog.has_table_privilege(@role, c.oid, 'INSERT'), " +
            "c.relkind <> 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_table_privilege(m.oid, c.oid, 'DELETE')), " +
            "c.relkind <> 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_table_privilege(m.oid, c.oid, 'TRUNCATE')), " +
            "c.relkind <> 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_table_privilege(m.oid, c.oid, 'TRIGGER')), " +
            "c.relkind <> 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_any_column_privilege(m.oid, c.oid, 'REFERENCES')), " +
            "c.relkind <> 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_any_column_privilege(m.oid, c.oid, 'SELECT,INSERT,UPDATE,REFERENCES')), " +
            "c.relkind <> 'S' AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_any_column_privilege(m.oid, c.oid, " +
            "'SELECT WITH GRANT OPTION,INSERT WITH GRANT OPTION,UPDATE WITH GRANT OPTION,REFERENCES WITH GRANT OPTION') " +
            "OR pg_catalog.has_table_privilege(m.oid, c.oid, 'DELETE WITH GRANT OPTION,TRUNCATE WITH GRANT OPTION,TRIGGER WITH GRANT OPTION')), " +
            "ARRAY(SELECT a.attname::text FROM pg_catalog.pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0 " +
            "AND NOT a.attisdropped AND c.relkind <> 'S' " +
            "AND EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_column_privilege(m.oid, c.oid, a.attnum, 'UPDATE')) ORDER BY a.attname), " +
            "ARRAY(SELECT a.attname::text FROM pg_catalog.pg_attribute a WHERE a.attrelid = c.oid AND a.attnum > 0 " +
            "AND NOT a.attisdropped AND c.relkind <> 'S' " +
            "AND pg_catalog.has_column_privilege(@role, c.oid, a.attnum, 'UPDATE') ORDER BY a.attname), " +
            maintain +
            "FROM pg_catalog.pg_class c WHERE c.relnamespace = @schema AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S') ORDER BY 1",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<uint>("schema", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = schemaOid });
        command.Parameters.Add(new NpgsqlParameter<uint>("role", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = role.Oid });

        var byName = Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var isSequence = reader.GetBoolean(1);
            var sequenceAny = reader.GetBoolean(2);
            var select = reader.GetBoolean(3);
            var insert = reader.GetBoolean(4);
            var delete = reader.GetBoolean(5);
            var truncate = reader.GetBoolean(6);
            var trigger = reader.GetBoolean(7);
            var references = reader.GetBoolean(8);
            var anyColumn = reader.GetBoolean(9);
            var grantOption = reader.GetBoolean(10);
            var updatable = reader.GetFieldValue<string[]>(11);
            var ownUpdatable = reader.GetFieldValue<string[]>(12);
            var maintainable = reader.GetBoolean(13);

            if (isSequence)
            {
                if (sequenceAny)
                {
                    violations.Add($"{name}: the role can use a sequence it does not need");
                }

                continue;
            }

            if (!byName.TryGetValue(name, out var expected) || !existingTables.Contains(name))
            {
                if (anyColumn || delete || truncate || trigger || grantOption || maintainable)
                {
                    violations.Add($"{name}: the role holds privileges on a table the stores do not use");
                }

                continue;
            }

            if (!select || !insert)
            {
                violations.Add($"{name}: SELECT and INSERT are missing");
            }

            if (delete != expected.Delete)
            {
                violations.Add(expected.Delete ? $"{name}: DELETE is missing" : $"{name}: the role can DELETE");
            }

            if (truncate)
            {
                violations.Add($"{name}: the role can TRUNCATE");
            }

            if (trigger)
            {
                violations.Add($"{name}: the role can create triggers");
            }

            if (maintainable)
            {
                violations.Add($"{name}: the role holds MAINTAIN (LOCK TABLE, CLUSTER, REINDEX, VACUUM)");
            }

            if (references)
            {
                violations.Add($"{name}: the role holds REFERENCES");
            }

            if (grantOption)
            {
                violations.Add($"{name}: the role holds a privilege WITH GRANT OPTION");
            }

            var expectedUpdatable = expected.UpdateColumns.Order(StringComparer.Ordinal).ToArray();
            var extra = updatable.Except(expectedUpdatable, StringComparer.Ordinal).ToArray();
            var missing = expectedUpdatable.Except(ownUpdatable, StringComparer.Ordinal).ToArray();
            if (extra.Length > 0 || missing.Length > 0)
            {
                violations.Add(
                    $"{name}: UPDATE is not exactly the needed columns" +
                    (extra.Length > 0 ? $" (updatable but should not be: {string.Join(", ", extra)})" : string.Empty) +
                    (missing.Length > 0 ? $" (missing: {string.Join(", ", missing)})" : string.Empty));
            }
        }
    }

    private static async Task VerifyFunctionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RoleFacts role,
        uint schemaOid,
        IReadOnlyDictionary<uint, bool> purgeFunctions,
        List<string> violations,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            ReachCte +
            "SELECT p.oid, p.proname::text, p.prosecdef, pg_catalog.has_function_privilege(@role, p.oid, 'EXECUTE'), " +
            "EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_function_privilege(m.oid, p.oid, 'EXECUTE')), " +
            "EXISTS (SELECT 1 FROM reach m WHERE pg_catalog.has_function_privilege(m.oid, p.oid, 'EXECUTE WITH GRANT OPTION')) " +
            "FROM pg_catalog.pg_proc p WHERE p.pronamespace = @schema ORDER BY 2, 1",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<uint>("schema", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = schemaOid });
        command.Parameters.Add(new NpgsqlParameter<uint>("role", NpgsqlTypes.NpgsqlDbType.Oid) { TypedValue = role.Oid });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var oid = reader.GetFieldValue<uint>(0);
            var name = reader.GetString(1);
            var securityDefiner = reader.GetBoolean(2);
            var execute = reader.GetBoolean(3);
            var reachable = reader.GetBoolean(4);
            var grantOption = reader.GetBoolean(5);

            if (grantOption && (securityDefiner || purgeFunctions.ContainsKey(oid)))
            {
                violations.Add($"{name}: the role holds EXECUTE WITH GRANT OPTION");
            }

            if (purgeFunctions.TryGetValue(oid, out var expected))
            {
                if (expected && !execute)
                {
                    violations.Add($"{name}: EXECUTE is missing although the host opted in");
                }
                else if (!expected && reachable)
                {
                    violations.Add($"{name}: the role can EXECUTE a purge function the host did not opt into");
                }
            }
            else if (securityDefiner && reachable)
            {
                // A SECURITY DEFINER function runs as its owner. One the manifest does not know about --
                // typically a later migration's, still carrying PostgreSQL's default EXECUTE to PUBLIC -- is
                // an unreviewed path from the application role to the owner's rights.
                violations.Add($"{name}: the role can EXECUTE a SECURITY DEFINER function the stores do not use");
            }
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        (string Name, string Value) parameter)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string>(parameter.Name, parameter.Value));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? default! : (T)value;
    }

    private static ExperienceStoreException Refused(string reason) =>
        new($"The application role's privileges were not applied: {reason}. Nothing was changed.");

    internal sealed record TablePrivileges(
        string Name,
        bool Delete,
        IReadOnlyList<string> UpdateColumns,
        bool Optional = false);

    private sealed record RoleFacts(uint Oid, string QuotedName);
}
