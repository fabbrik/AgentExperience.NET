using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 6.1 (KL-4) against a real PostgreSQL container, on every supported major (15 to 18, see
/// <see cref="AgentExperience.Tests.Shared.PostgresTestImage"/>): the two-role deployment, proved from the
/// application role's own connection. Every other store test in this project already runs as that role
/// (see <see cref="PostgresFixture"/>); this class proves what it can <em>not</em> do -- own, alter,
/// disable, rewrite, remove, truncate, or reach a purge it was not given -- and that
/// <see cref="ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync"/> refuses, re-applies and
/// verifies exactly as documented. Tests that change a schema's objects or ACLs use a database of their
/// own, so the shared fixture's privilege set never moves under another test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresApplicationRoleTests
{
    /// <summary>Every table the application role may append to and read, and must never rewrite or remove.</summary>
    public static readonly TheoryData<string> Ledgers = new()
    {
        "lifecycle_events",
        "experience_grant_events",
        "confidence_evidence",
        "reuse_feedback",
        "reuse_feedback_exposures",
        "experience_grant_access",
    };

    /// <summary>The purge markers, alone and together: none of them makes a refused statement allowed.</summary>
    public static readonly TheoryData<string[]> MarkerSets = new()
    {
        Array.Empty<string>(),
        new[] { "agent_experience.purge_authorized" },
        new[] { "agent_experience.access_purge_authorized" },
        new[] { "agent_experience.purge_authorized", "agent_experience.access_purge_authorized" },
    };

    private static readonly string[] GuardedTables =
    [
        "experience_records",
        "experience_grants",
        "lifecycle_events",
        "experience_grant_events",
        "confidence_evidence",
        "reuse_feedback",
        "reuse_feedback_exposures",
        "experience_grant_access",
    ];

    private static readonly string[] PurgeSignatures =
    [
        "agent_experience.purge_experience_record(uuid, text, text, text, text, text, text, bigint, timestamptz)",
        "agent_experience.purge_expired_grants(text, text, text, text, text, text, timestamptz, integer)",
        "agent_experience.purge_grant_access(text, text, text, text, text, text, boolean, timestamptz, integer)",
    ];

    /// <summary>0016's sealing transition, the crypto-shredding upgrade job's one write.</summary>
    private const string SealSignature =
        "agent_experience.seal_experience_record(uuid, text, text, text, text, text, text, bigint, jsonb)";

    private const string Tombstone =
        "UPDATE agent_experience.experience_records SET payload = '{}'::jsonb, task_id = '(deleted)', " +
        "status = 'Deleted', source_run_id = '00000000-0000-0000-0000-000000000000'::uuid, " +
        "reuse_confidence = 0, supporting_validations = 0, contradictions = 0, " +
        "created_at = now(), updated_at = now(), deleted_at = now(), revision = revision + 1 WHERE experience_id = @id";

    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;

    public PostgresApplicationRoleTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
    }

    // ------------------------------------------------------------------ what the role is

    [Fact]
    public async Task The_application_role_owns_nothing_and_holds_exactly_the_manifest()
    {
        const string App = PostgresFixture.ApplicationRoleName;

        // Ownership, directly or through membership, of the database, the schema, or anything in it.
        Assert.Equal(0L, await OwnerScalarAsync<long>(
            "SELECT count(*) FROM (" +
            "SELECT nspowner AS owner FROM pg_namespace WHERE nspname = 'agent_experience' " +
            "UNION ALL SELECT relowner FROM pg_class WHERE relnamespace = 'agent_experience'::regnamespace " +
            "UNION ALL SELECT proowner FROM pg_proc WHERE pronamespace = 'agent_experience'::regnamespace " +
            "UNION ALL SELECT datdba FROM pg_database WHERE datname = current_database()) o " +
            $"WHERE pg_has_role('{App}', o.owner, 'MEMBER')"));

        // Table-level grants, read independently of the code that made them.
        var tableGrants = await OwnerRowsAsync(
            "SELECT table_name || ':' || privilege_type FROM information_schema.role_table_grants " +
            $"WHERE grantee = '{App}' AND table_schema = 'agent_experience' ORDER BY 1");
        var expectedTableGrants = GuardedTables
            .SelectMany(t => new[] { $"{t}:INSERT", $"{t}:SELECT" })
            .Order(StringComparer.Ordinal);
        Assert.Equal(expectedTableGrants, tableGrants.Order(StringComparer.Ordinal));

        // UPDATE exists only as column grants, on the projection columns and the revocation columns.
        var updatable = await OwnerRowsAsync(
            "SELECT table_name || '.' || column_name FROM information_schema.role_column_grants " +
            $"WHERE grantee = '{App}' AND table_schema = 'agent_experience' AND privilege_type = 'UPDATE' ORDER BY 1");
        Assert.Equal(
            [
                "experience_grants.revocation_reason",
                "experience_grants.revoked_at",
                "experience_records.contradictions",
                "experience_records.reuse_confidence",
                "experience_records.revision",
                "experience_records.status",
                "experience_records.supporting_validations",
                "experience_records.updated_at",
            ],
            updatable.Order(StringComparer.Ordinal));

        // No CREATE on the schema, nothing on the migration journal, and EXECUTE on the purges and the
        // sealing function only because this fixture opted into all three.
        Assert.False(await OwnerScalarAsync<bool>($"SELECT has_schema_privilege('{App}', 'agent_experience', 'CREATE')"));
        Assert.False(await OwnerScalarAsync<bool>(
            $"SELECT has_table_privilege('{App}', 'agent_experience.schema_versions', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')"));
        foreach (var signature in PurgeSignatures.Append(SealSignature))
        {
            Assert.True(await OwnerScalarAsync<bool>($"SELECT has_function_privilege('{App}', '{signature}', 'EXECUTE')"));
        }
    }

    [Fact]
    public async Task The_purges_and_every_guard_resolve_names_only_through_a_pinned_search_path_with_pg_temp_last()
    {
        foreach (var function in PurgeSignatures.Concat(
        [
            SealSignature,
            "agent_experience.guard_sealed_record_erasure()",
            "agent_experience.reject_event_log_mutation()",
            "agent_experience.enforce_grant_monotonicity()",
            "agent_experience.reject_audited_grant_delete()",
            "agent_experience.enforce_record_projection()",
            "agent_experience.reject_record_removal()",
            "agent_experience.reject_future_grant_issue()",
        ]))
        {
            var config = await OwnerScalarAsync<string[]>($"SELECT proconfig FROM pg_proc WHERE oid = '{function}'::regprocedure");
            Assert.Contains("search_path=pg_catalog, agent_experience, pg_temp", config);
        }

        // The purges keep their own marker resets alongside the new pin.
        Assert.Contains(
            "agent_experience.purge_authorized=off",
            await OwnerScalarAsync<string[]>($"SELECT proconfig FROM pg_proc WHERE oid = '{PurgeSignatures[0]}'::regprocedure"));
        Assert.Contains(
            "agent_experience.access_purge_authorized=off",
            await OwnerScalarAsync<string[]>($"SELECT proconfig FROM pg_proc WHERE oid = '{PurgeSignatures[2]}'::regprocedure"));
    }

    // ------------------------------------------------------------------ what the role cannot do

    [Fact]
    public async Task The_application_role_cannot_alter_disable_or_drop_a_guard_or_replace_its_function()
    {
        var statements = new List<string>();
        foreach (var table in GuardedTables)
        {
            statements.Add($"ALTER TABLE agent_experience.{table} DISABLE TRIGGER ALL");
            statements.Add($"ALTER TABLE agent_experience.{table} OWNER TO {PostgresFixture.ApplicationRoleName}");
        }

        statements.AddRange(
        [
            "ALTER TABLE agent_experience.lifecycle_events DISABLE TRIGGER lifecycle_events_append_only",
            "ALTER TABLE agent_experience.lifecycle_events ENABLE REPLICA TRIGGER lifecycle_events_append_only",
            "DROP TRIGGER lifecycle_events_append_only ON agent_experience.lifecycle_events",
            "DROP TRIGGER experience_records_no_delete ON agent_experience.experience_records",
            "ALTER TABLE agent_experience.lifecycle_events DROP CONSTRAINT lifecycle_events_current_status_known",
            "DROP TABLE agent_experience.lifecycle_events",
            "CREATE OR REPLACE FUNCTION agent_experience.reject_event_log_mutation() RETURNS trigger " +
            "LANGUAGE plpgsql AS $$ BEGIN RETURN OLD; END $$",
            "ALTER FUNCTION agent_experience.purge_experience_record(uuid, text, text, text, text, text, text, bigint, timestamptz) " +
            "SECURITY INVOKER",
            "ALTER FUNCTION agent_experience.reject_event_log_mutation() RESET search_path",
            "CREATE TABLE agent_experience.shadow_ledger (id int)",
            "CREATE FUNCTION agent_experience.shadow() RETURNS int LANGUAGE sql AS 'SELECT 1'",
            // Replica mode skips an ordinary trigger; these are ENABLE ALWAYS anyway, and only a superuser
            // may enter it.
            "SET session_replication_role = 'replica'",
        ]);

        foreach (var sql in statements)
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => AppExecuteAsync(sql, markers: []));
            Assert.True(
                refused.SqlState == PostgresErrorCodes.InsufficientPrivilege,
                $"Expected 42501 for: {sql}; got {refused.SqlState} {refused.MessageText}");
        }

        // Every guard is still armed, ALWAYS.
        Assert.Equal(0L, await OwnerScalarAsync<long>(
            "SELECT count(*) FROM pg_trigger t WHERE t.tgrelid IN (SELECT oid FROM pg_class " +
            "WHERE relnamespace = 'agent_experience'::regnamespace) AND NOT t.tgisinternal AND t.tgenabled <> 'A'"));
    }

    [Theory]
    [MemberData(nameof(MarkerSets))]
    public async Task No_ledger_record_or_grant_row_can_be_rewritten_removed_or_truncated_by_the_application_role_whatever_marker_it_sets(string[] markers)
    {
        var (auth, scope, record) = await ValidatedRecordAsync();

        foreach (var table in GuardedTables)
        {
            var first = await OwnerScalarAsync<string>(
                $"SELECT attname::text FROM pg_attribute WHERE attrelid = 'agent_experience.{table}'::regclass AND attnum = 1");

            var statements = new List<string>
            {
                $"DELETE FROM agent_experience.{table}",
                $"TRUNCATE agent_experience.{table}",
            };

            // experience_records and experience_grants keep a few updatable columns (the projection and
            // the revocation); their first column -- the identity -- is not one of them.
            statements.Add($"UPDATE agent_experience.{table} SET {first} = {first}");

            foreach (var sql in statements)
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => AppExecuteAsync(sql, markers));

                // The privilege system refused it, before any trigger could have been asked: the marker was
                // never consulted, so setting it by hand gains nothing.
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
                Assert.StartsWith("permission denied for table", refused.MessageText, StringComparison.Ordinal);
            }
        }

        // Everything is still there.
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, scope, record, CancellationToken.None)).Outcome);
        Assert.Single((await _store.GetFirstHistoryPageAsync(auth, scope, record, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task A_hand_set_marker_would_admit_a_delete_from_a_role_holding_DELETE_which_is_why_the_application_role_holds_none()
    {
        // The contrast that makes the test above mean something. The owner holds DELETE, and for it the
        // marker is exactly what KL-4 said it was -- a switch any session can flip. The application role
        // is refused the same statement by the privilege system.
        var (auth, scope, record) = await ValidatedRecordAsync();
        var sql = "DELETE FROM agent_experience.lifecycle_events WHERE experience_id = @id";

        var appRefused = await Assert.ThrowsAsync<PostgresException>(
            () => AppExecuteAsync(sql, ["agent_experience.purge_authorized"], record));
        Assert.StartsWith("permission denied for table", appRefused.MessageText, StringComparison.Ordinal);

        Assert.Equal(1, await ExecuteAsync(_fixture.OwnerDataSource, sql, ["agent_experience.purge_authorized"], record));
        Assert.Empty((await _store.GetFirstHistoryPageAsync(auth, scope, record, CancellationToken.None)).Events);
    }

    [Fact]
    public async Task A_hand_marked_tombstone_is_out_of_reach_because_the_role_cannot_write_those_columns()
    {
        // 0010's projection guard admits exactly one marked UPDATE: a live record into its tombstone shape.
        // A role that could run it by hand could erase a record's payload while leaving every ledger row
        // that names it -- a second erasure path that skips the sweep. The application role cannot write
        // deleted_at, payload, task_id, created_at or source_run_id at all.
        var (auth, scope, record) = await ValidatedRecordAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(
            () => AppExecuteAsync(Tombstone, ["agent_experience.purge_authorized"], record));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.StartsWith("permission denied for table experience_records", refused.MessageText, StringComparison.Ordinal);
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, scope, record, CancellationToken.None)).Outcome);

        // The owner, which can, gets exactly the hole this closes: a tombstone whose history survives.
        // (Since 0016 a sealed row also needs the key-destruction marker, which only the owner's statement gets here.)
        Assert.Equal(1, await ExecuteAsync(
            _fixture.OwnerDataSource, Tombstone, ["agent_experience.purge_authorized", "agent_experience.erasure_destroys_key"], record));
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await _store.GetAsync(auth, scope, record, CancellationToken.None)).Outcome);
        Assert.Equal(1L, await OwnerScalarAsync<long>(
            $"SELECT count(*) FROM agent_experience.lifecycle_events WHERE experience_id = '{record}'"));
    }

    [Fact]
    public async Task Without_the_opt_ins_no_purge_is_reachable_and_every_other_store_path_still_works()
    {
        var role = await _fixture.CreateLoginRoleAsync("noopt");
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _fixture.OwnerDataSource, new ExperienceApplicationRoleOptions(role), CancellationToken.None);

        await using var source = NpgsqlDataSource.Create(_fixture.ConnectionString(PostgresFixture.StoreDatabase, role));
        var store = new PostgresExperienceRecordStore(source);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = Minimal(scope);

        // Create, commit, read: the ordinary paths need no purge.
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await store.CommitLifecycleEventAsync(auth, scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // The erasure fails loudly, as a permission error, and erases nothing.
        var delete = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(delete.InnerException).SqlState);
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // Each purge function, called directly.
        await using var connection = await source.OpenConnectionAsync();
        foreach (var call in new[]
        {
            "SELECT * FROM agent_experience.purge_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
            "SELECT agent_experience.purge_expired_grants(@tenant, 'app-1', 'project-1', NULL, NULL, NULL, now(), 10)",
            "SELECT * FROM agent_experience.purge_grant_access(@tenant, 'app-1', 'project-1', NULL, NULL, NULL, true, now() - interval '100 days', 10)",
            "SELECT agent_experience.seal_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, 0, "
                + "'{\"sealed\": \"aexp-sealed:v1:AAAA\"}'::jsonb)",
        })
        {
            await using var command = new NpgsqlCommand(call, connection);
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            command.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
            var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            Assert.StartsWith("permission denied for function", refused.MessageText, StringComparison.Ordinal);
        }

        // Opting into erasure opens exactly the two erasure functions, and still not the access purge.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _fixture.OwnerDataSource, new ExperienceApplicationRoleOptions(role) { AllowErasure = true }, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.False(await OwnerScalarAsync<bool>($"SELECT has_function_privilege('{role}', '{PurgeSignatures[2]}', 'EXECUTE')"));
        Assert.False(await OwnerScalarAsync<bool>($"SELECT has_function_privilege('{role}', '{SealSignature}', 'EXECUTE')"));

        // Opting into sealing opens exactly the sealing function.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _fixture.OwnerDataSource, new ExperienceApplicationRoleOptions(role) { AllowSealing = true }, CancellationToken.None);
        Assert.True(await OwnerScalarAsync<bool>($"SELECT has_function_privilege('{role}', '{SealSignature}', 'EXECUTE')"));
        Assert.False(await OwnerScalarAsync<bool>($"SELECT has_function_privilege('{role}', '{PurgeSignatures[0]}', 'EXECUTE')"));

        // And re-applying without it takes it away again.
        await ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            _fixture.OwnerDataSource, new ExperienceApplicationRoleOptions(role), CancellationToken.None);
        Assert.False(await OwnerScalarAsync<bool>($"SELECT has_function_privilege('{role}', '{PurgeSignatures[0]}', 'EXECUTE')"));
        Assert.False(await OwnerScalarAsync<bool>($"SELECT has_function_privilege('{role}', '{SealSignature}', 'EXECUTE')"));
    }

    // ------------------------------------------------------------------ what the API refuses

    [Fact]
    public async Task The_privilege_call_refuses_a_superuser_itself_a_missing_role_and_an_unmigrated_database()
    {
        var superuser = await ScalarAsync<string>(_fixture.SuperuserDataSource, "SELECT current_user::text");

        var super = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(_fixture.OwnerDataSource, superuser));
        Assert.Contains("superuser", super.Message, StringComparison.Ordinal);

        var self = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(_fixture.OwnerDataSource, PostgresFixture.OwnerRoleName));
        Assert.Contains("the role running this call", self.Message, StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(_fixture.OwnerDataSource, "aen_no_such_role"));
        Assert.Contains("no role by that name", missing.Message, StringComparison.Ordinal);

        var role = await _fixture.CreateLoginRoleAsync("unmigrated");
        await using var empty = await _fixture.CreateDatabaseAsync("unmigrated");
        var unmigrated = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(empty, role));
        Assert.Contains("run ExperienceSchemaMigrator.MigrateAsync first", unmigrated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_privilege_call_refuses_a_member_of_the_owner_and_changes_nothing()
    {
        // Membership in the owning role is ownership: SET ROLE, then ALTER TABLE.
        var member = await _fixture.CreateLoginRoleAsync("ownermember");
        await _fixture.ExecuteAsSuperuserAsync($"GRANT {PostgresFixture.OwnerRoleName} TO \"{member}\"");

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(_fixture.OwnerDataSource, member));
        Assert.Contains(PostgresFixture.OwnerRoleName, refused.Message, StringComparison.Ordinal);
        Assert.Contains("must own nothing", refused.Message, StringComparison.Ordinal);

        // A role that owns the database is refused the same way: it could drop the whole thing.
        var databaseOwner = await _fixture.CreateLoginRoleAsync("dbowner");
        var database = await _fixture.CreateDatabaseNameAsync("dbowner", databaseOwner);
        var owner = await _fixture.CreateLoginRoleAsync("dbowner_o");
        await _fixture.ExecuteAsSuperuserAsync(PostgresFixture.OwnerParameterGrant(owner));
        await _fixture.ExecuteAsSuperuserAsync($"GRANT CREATE ON DATABASE \"{database}\" TO \"{owner}\"");
        await using (var ownerSource = NpgsqlDataSource.Create(_fixture.ConnectionString(database, owner)))
        {
            await ExperienceSchemaMigrator.MigrateAsync(ownerSource, CancellationToken.None);
            var dbRefused = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(ownerSource, databaseOwner));
            Assert.Contains("must own nothing", dbRefused.Message, StringComparison.Ordinal);
        }

        // Nothing was granted: the member's effective USAGE comes from the owner, so the ACL is what is checked.
        Assert.Equal(0L, await OwnerScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace n, aclexplode(n.nspacl) a " +
            $"WHERE n.nspname = 'agent_experience' AND a.grantee = '{member}'::regrole"));
    }

    [Fact]
    public async Task The_privilege_call_refuses_a_role_reaching_a_superuser_server_files_or_session_replication_role()
    {
        await using var world = await TwoRoleDatabaseAsync("danger");
        var superuser = await ScalarAsync<string>(_fixture.SuperuserDataSource, "SELECT current_user::text");

        // Each is one step from switching every guard off: a superuser bypasses privileges and triggers,
        // server file or program access reaches the data directory, and session_replication_role = replica
        // stops ordinary triggers firing.
        var grants = new (string Purpose, string? Grant, string Expected)[]
        {
            ("dsuper", null, "a superuser"),
            ("dfiles", "GRANT pg_write_server_files TO \"{0}\"", "server file or program access"),
            ("dsrr", "GRANT SET ON PARAMETER session_replication_role TO \"{0}\"", "session_replication_role"),
        };

        foreach (var (purpose, grant, expected) in grants)
        {
            var role = await _fixture.CreateLoginRoleAsync(purpose);
            if (grant is null)
            {
                await GrantWithoutInheritAsync(superuser, role);
            }
            else
            {
                await _fixture.ExecuteAsSuperuserAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, grant, role));
            }

            var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(world.Owner, role));
            Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed", refused.Message, StringComparison.Ordinal);
            Assert.Equal(0L, await ScalarAsync<long>(
                world.Owner,
                "SELECT count(*) FROM pg_namespace n, aclexplode(n.nspacl) a " +
                $"WHERE n.nspname = 'agent_experience' AND a.grantee = '{role}'::regrole"));
        }
    }

    [Fact]
    public async Task The_privilege_call_rolls_back_on_CREATE_through_PUBLIC_or_DELETE_one_SET_ROLE_away()
    {
        await using var world = await TwoRoleDatabaseAsync("reach");

        // CREATE in the schema, from PUBLIC, which the owner's REVOKE from the role cannot remove: it would
        // let the role plant an object on the purges' pinned search_path.
        await ExecuteAsync(world.Owner, "GRANT CREATE ON SCHEMA agent_experience TO PUBLIC", markers: []);
        var create = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(world.Owner, world.App));
        Assert.Contains("the role can CREATE in the schema", create.Message, StringComparison.Ordinal);
        await ExecuteAsync(world.Owner, "REVOKE CREATE ON SCHEMA agent_experience FROM PUBLIC", markers: []);

        // DELETE held by a role the application role may SET ROLE to but does not inherit from. The
        // inheriting has_table_privilege alone would not see it.
        var deleter = await _fixture.CreateLoginRoleAsync("reach_del");
        await ExecuteAsync(world.Owner, $"GRANT DELETE ON agent_experience.lifecycle_events TO \"{deleter}\"", markers: []);
        await GrantWithoutInheritAsync(deleter, world.App);
        Assert.False(await ScalarAsync<bool>(world.Owner, $"SELECT has_table_privilege('{world.App}', 'agent_experience.lifecycle_events', 'DELETE')"));

        var delete = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(world.Owner, world.App));
        Assert.Contains("lifecycle_events: the role can DELETE", delete.Message, StringComparison.Ordinal);

        // Rolled back both times: the application role was granted nothing.
        Assert.Equal(0L, await ScalarAsync<long>(
            world.Owner,
            "SELECT count(*) FROM pg_namespace n, aclexplode(n.nspacl) a " +
            $"WHERE n.nspname = 'agent_experience' AND a.grantee = '{world.App}'::regrole"));

        // Take the membership away and the same call succeeds.
        await _fixture.ExecuteAsSuperuserAsync($"REVOKE \"{deleter}\" FROM \"{world.App}\"");
        await _fixture.ExecuteAsSuperuserAsync($"ALTER ROLE \"{world.App}\" INHERIT");
        await ApplyAsync(world.Owner, world.App);
    }

    /// <summary>
    /// Story 6.3: PostgreSQL 17 added the <c>MAINTAIN</c> table privilege and the <c>pg_maintain</c>
    /// predefined role. No trigger fires on <c>LOCK TABLE</c>, <c>CLUSTER</c>, <c>REINDEX</c> or
    /// <c>VACUUM</c>, so the call refuses either one, whether held directly, through PUBLIC, or one
    /// <c>SET ROLE</c> away. On 15 and 16 neither exists, and the same call succeeds: the check is guarded
    /// by server version rather than failing on a privilege name those servers do not know.
    /// </summary>
    [Fact]
    public async Task The_privilege_call_refuses_MAINTAIN_and_pg_maintain_on_PostgreSQL_17_and_later()
    {
        await using var world = await TwoRoleDatabaseAsync("maintain");
        var serverMajor = await ScalarAsync<int>(world.Owner, "SELECT current_setting('server_version_num')::int / 10000");
        Assert.Equal(AgentExperience.Tests.Shared.PostgresTestImage.Major, serverMajor);

        if (serverMajor < 17)
        {
            Assert.False(await ScalarAsync<bool>(world.Owner, "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pg_maintain')"));
            await ApplyAsync(world.Owner, world.App);
            return;
        }

        // MAINTAIN on one guarded ledger, through PUBLIC: the owner's REVOKE from the role cannot remove it.
        await ExecuteAsync(world.Owner, "GRANT MAINTAIN ON agent_experience.lifecycle_events TO PUBLIC", markers: []);
        var viaPublic = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(world.Owner, world.App));
        Assert.Contains("lifecycle_events: the role holds MAINTAIN", viaPublic.Message, StringComparison.Ordinal);
        await ExecuteAsync(world.Owner, "REVOKE MAINTAIN ON agent_experience.lifecycle_events FROM PUBLIC", markers: []);

        // MAINTAIN held by a role the application role can SET ROLE to without inheriting it.
        var maintainer = await _fixture.CreateLoginRoleAsync("maint_one");
        await ExecuteAsync(world.Owner, $"GRANT MAINTAIN ON agent_experience.experience_records TO \"{maintainer}\"", markers: []);
        await GrantWithoutInheritAsync(maintainer, world.App);
        var viaMembership = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(world.Owner, world.App));
        Assert.Contains("experience_records: the role holds MAINTAIN", viaMembership.Message, StringComparison.Ordinal);
        await _fixture.ExecuteAsSuperuserAsync($"REVOKE \"{maintainer}\" FROM \"{world.App}\"");

        // pg_maintain: MAINTAIN on every table in the cluster, refused before anything is granted.
        await _fixture.ExecuteAsSuperuserAsync($"GRANT pg_maintain TO \"{world.App}\"");
        var predefined = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(world.Owner, world.App));
        Assert.Contains("pg_maintain", predefined.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", predefined.Message, StringComparison.Ordinal);
        await _fixture.ExecuteAsSuperuserAsync($"REVOKE pg_maintain FROM \"{world.App}\"");

        // Rolled back every time, and with each path removed the same call succeeds.
        Assert.Equal(0L, await ScalarAsync<long>(
            world.Owner,
            "SELECT count(*) FROM pg_namespace n, aclexplode(n.nspacl) a " +
            $"WHERE n.nspname = 'agent_experience' AND a.grantee = '{world.App}'::regrole"));
        await ApplyAsync(world.Owner, world.App);
    }

    [Fact]
    public async Task The_privilege_call_verifies_effective_privileges_and_rolls_back_when_a_predefined_role_grants_DELETE()
    {
        // pg_write_all_data gives INSERT, UPDATE and DELETE on every table, and an owner's REVOKE cannot
        // touch it. Only the effective-privilege check sees it.
        var writer = await _fixture.CreateLoginRoleAsync("writealldata");
        await _fixture.ExecuteAsSuperuserAsync($"GRANT pg_write_all_data TO \"{writer}\"");

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(_fixture.OwnerDataSource, writer));
        Assert.Contains("the role can DELETE", refused.Message, StringComparison.Ordinal);
        Assert.Contains("lifecycle_events", refused.Message, StringComparison.Ordinal);

        // Rolled back: not even the schema USAGE the call granted first was recorded. (pg_write_all_data
        // itself implies USAGE on every schema, so the ACL is what is checked, not the effective privilege.)
        Assert.Equal(0L, await OwnerScalarAsync<long>(
            "SELECT count(*) FROM pg_namespace n, aclexplode(n.nspacl) a " +
            $"WHERE n.nspname = 'agent_experience' AND a.grantee = '{writer}'::regrole"));
    }

    [Fact]
    public async Task The_privilege_call_run_by_a_role_that_is_not_the_owner_fails_and_changes_nothing()
    {
        var outsider = await _fixture.CreateLoginRoleAsync("outsider");
        var target = await _fixture.CreateLoginRoleAsync("outsidertarget");
        await using var outsiderSource = NpgsqlDataSource.Create(_fixture.ConnectionString(PostgresFixture.StoreDatabase, outsider));

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(outsiderSource, target));
        Assert.Contains("Nothing was changed", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("infrastructure", refused.Message, StringComparison.Ordinal);
        Assert.False(await OwnerScalarAsync<bool>($"SELECT has_schema_privilege('{target}', 'agent_experience', 'USAGE')"));
    }

    // ------------------------------------------------------------------ re-applying it

    [Fact]
    public async Task Re_applying_is_idempotent_and_removes_every_stray_grant_the_owner_made()
    {
        await using var world = await TwoRoleDatabaseAsync("reapply");
        await ApplyAsync(world.Owner, world.App, erasure: true);
        var first = await AclSnapshotAsync(world.Owner);

        await ApplyAsync(world.Owner, world.App, erasure: true);
        Assert.Equal(first, await AclSnapshotAsync(world.Owner));

        // Over-grant it every way an owner can -- the single-role habit of GRANT ALL -- and re-apply.
        foreach (var sql in new[]
        {
            $"GRANT ALL ON ALL TABLES IN SCHEMA agent_experience TO \"{world.App}\"",
            $"GRANT ALL ON ALL SEQUENCES IN SCHEMA agent_experience TO \"{world.App}\"",
            $"GRANT ALL ON ALL FUNCTIONS IN SCHEMA agent_experience TO \"{world.App}\"",
            $"GRANT ALL ON SCHEMA agent_experience TO \"{world.App}\"",
        })
        {
            await ExecuteAsync(world.Owner, sql, markers: []);
        }

        Assert.True(await ScalarAsync<bool>(world.Owner, $"SELECT has_table_privilege('{world.App}', 'agent_experience.lifecycle_events', 'DELETE')"));

        await ApplyAsync(world.Owner, world.App, erasure: true);
        Assert.Equal(first, await AclSnapshotAsync(world.Owner));

        await using var app = world.AppSource();
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, "DELETE FROM agent_experience.lifecycle_events", markers: []));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    [Fact]
    public async Task An_object_a_later_migration_adds_gets_nothing_until_the_manifest_names_it()
    {
        await using var world = await TwoRoleDatabaseAsync("later");
        await ApplyAsync(world.Owner, world.App);

        // A later migration's table, with its own sequence -- and a default privilege that would hand the
        // application role DELETE on it the moment it is created.
        await ExecuteAsync(world.Owner, $"ALTER DEFAULT PRIVILEGES IN SCHEMA agent_experience GRANT DELETE ON TABLES TO \"{world.App}\"", markers: []);
        await ExecuteAsync(world.Owner, "CREATE TABLE agent_experience.future_ledger (id serial PRIMARY KEY, note text)", markers: []);
        Assert.True(await ScalarAsync<bool>(world.Owner, $"SELECT has_table_privilege('{world.App}', 'agent_experience.future_ledger', 'DELETE')"));

        // Re-applying takes it away: the table and its sequence get nothing at all.
        await ApplyAsync(world.Owner, world.App);
        Assert.False(await ScalarAsync<bool>(world.Owner,
            $"SELECT has_table_privilege('{world.App}', 'agent_experience.future_ledger', 'SELECT,INSERT,UPDATE,DELETE,TRUNCATE,REFERENCES,TRIGGER')"));
        Assert.False(await ScalarAsync<bool>(world.Owner,
            $"SELECT has_sequence_privilege('{world.App}', 'agent_experience.future_ledger_id_seq', 'USAGE,SELECT,UPDATE')"));

        // A later SECURITY DEFINER function still carrying PostgreSQL's default EXECUTE to PUBLIC is an
        // unreviewed path to the owner's rights: the call refuses to certify the role until it is revoked.
        await ExecuteAsync(world.Owner,
            "CREATE FUNCTION agent_experience.future_purge() RETURNS int LANGUAGE sql SECURITY DEFINER " +
            "SET search_path = pg_catalog AS 'SELECT 1'",
            markers: []);
        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => ApplyAsync(world.Owner, world.App));
        Assert.Contains("future_purge", refused.Message, StringComparison.Ordinal);
        Assert.Contains("SECURITY DEFINER", refused.Message, StringComparison.Ordinal);

        await ExecuteAsync(world.Owner, "REVOKE ALL ON FUNCTION agent_experience.future_purge() FROM PUBLIC", markers: []);
        await ApplyAsync(world.Owner, world.App);
    }

    // ------------------------------------------------------------------ upgrading a single-role database

    /// <summary>
    /// The store README's upgrade runbook, verbatim apart from the two role names: moves the database, the
    /// schema and everything in it from the role the application used to migrate as, to a new owner.
    /// </summary>
    internal static string OwnershipTransferSql(string newOwner) => $$"""
        DO $transfer$
        DECLARE
            obj record;
        BEGIN
            EXECUTE format('ALTER DATABASE %I OWNER TO %I', current_database(), '{{newOwner}}');
            EXECUTE format('ALTER SCHEMA agent_experience OWNER TO %I', '{{newOwner}}');
            FOR obj IN
                SELECT c.oid::regclass AS name, c.relkind
                FROM pg_class c
                WHERE c.relnamespace = 'agent_experience'::regnamespace
                  AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_depend d
                      WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('a', 'i'))
            LOOP
                EXECUTE format(
                    CASE WHEN obj.relkind = 'S' THEN 'ALTER SEQUENCE %s OWNER TO %I' ELSE 'ALTER TABLE %s OWNER TO %I' END,
                    obj.name, '{{newOwner}}');
            END LOOP;
            FOR obj IN SELECT p.oid::regprocedure AS name FROM pg_proc p WHERE p.pronamespace = 'agent_experience'::regnamespace
            LOOP
                EXECUTE format('ALTER FUNCTION %s OWNER TO %I', obj.name, '{{newOwner}}');
            END LOOP;
        END
        $transfer$;
        """;

    [Fact]
    public async Task A_single_role_database_upgrades_to_two_roles_with_the_documented_sql()
    {
        // Before: the application role ran the migrator, so it owns the database and every table.
        var legacy = await _fixture.CreateLoginRoleAsync("legacy");
        var database = await _fixture.CreateDatabaseNameAsync("legacy", legacy);
        await _fixture.ExecuteAsSuperuserAsync(PostgresFixture.OwnerParameterGrant(legacy));
        await using var legacySource = NpgsqlDataSource.Create(_fixture.ConnectionString(database, legacy));
        await ExperienceSchemaMigrator.MigrateAsync(legacySource, CancellationToken.None);

        var store = new PostgresExperienceRecordStore(legacySource);
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = Minimal(scope);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await store.CommitLifecycleEventAsync(auth, scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None)).Outcome);

        // KL-4 as it was: the owner switches a guard off.
        await ExecuteAsync(legacySource, "ALTER TABLE agent_experience.lifecycle_events DISABLE TRIGGER lifecycle_events_append_only", markers: []);
        await ExecuteAsync(legacySource, "ALTER TABLE agent_experience.lifecycle_events ENABLE ALWAYS TRIGGER lifecycle_events_append_only", markers: []);

        // The runbook, as a superuser.
        var owner = await _fixture.CreateLoginRoleAsync("newowner");
        await _fixture.ExecuteAsSuperuserAsync(PostgresFixture.OwnerParameterGrant(owner));
        await _fixture.ExecuteAsSuperuserAsync(OwnershipTransferSql(owner), database);

        // The EXECUTE 0010 and 0012 granted the old owner moved with the ownership: the old role has none
        // until the privilege call grants it.
        await using var ownerSource = NpgsqlDataSource.Create(_fixture.ConnectionString(database, owner));
        foreach (var signature in PurgeSignatures)
        {
            Assert.False(await ScalarAsync<bool>(ownerSource, $"SELECT has_function_privilege('{legacy}', '{signature}', 'EXECUTE')"));
        }

        // Then, as the new owner, on every deploy from now on.
        Assert.Empty((await ExperienceSchemaMigrator.MigrateAsync(ownerSource, CancellationToken.None)).AppliedScripts);
        await ApplyAsync(ownerSource, legacy, erasure: true);

        // After: the old role owns nothing...
        Assert.Equal(0L, await ScalarAsync<long>(ownerSource,
            "SELECT count(*) FROM (" +
            "SELECT nspowner AS owner FROM pg_namespace WHERE nspname = 'agent_experience' " +
            "UNION ALL SELECT relowner FROM pg_class WHERE relnamespace = 'agent_experience'::regnamespace " +
            "UNION ALL SELECT proowner FROM pg_proc WHERE pronamespace = 'agent_experience'::regnamespace " +
            "UNION ALL SELECT datdba FROM pg_database WHERE datname = current_database()) o " +
            $"WHERE pg_has_role('{legacy}', o.owner, 'MEMBER')"));

        // ...cannot switch a guard off or delete history any more...
        var altered = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            legacySource, "ALTER TABLE agent_experience.lifecycle_events DISABLE TRIGGER lifecycle_events_append_only", markers: []));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, altered.SqlState);
        var deleted = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            legacySource, "DELETE FROM agent_experience.lifecycle_events", ["agent_experience.purge_authorized"]));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleted.SqlState);

        // ...and every store path it used still works, erasure included because this host opted in.
        Assert.Equal(ExperienceStoreOutcome.Found, (await store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(AuthorizationContext Auth, Scope Scope, Guid Record)> ValidatedRecordAsync()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = Minimal(scope);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await _store.CommitLifecycleEventAsync(auth, scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None)).Outcome);
        return (auth, scope, record.ExperienceId);
    }

    private static Task ApplyAsync(NpgsqlDataSource ownerSource, string role, bool erasure = false) =>
        ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
            ownerSource,
            new ExperienceApplicationRoleOptions(role) { AllowErasure = erasure },
            CancellationToken.None);

    private Task<int> AppExecuteAsync(string sql, string[] markers, Guid? id = null) =>
        ExecuteAsync(_fixture.DataSource, sql, markers, id);

    /// <summary>
    /// Runs one statement in its own transaction with the given markers hand-set -- which any session may
    /// do -- and commits only when it succeeds, so a statement that should have been refused and was not
    /// still fails the test rather than silently changing the shared database.
    /// </summary>
    private static async Task<int> ExecuteAsync(NpgsqlDataSource source, string sql, string[] markers, Guid? id = null)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var marker in markers)
        {
            // Each marker is one of three literals in this file, never input.
            await using var set = new NpgsqlCommand($"SET LOCAL {marker} = 'on'", connection, transaction);
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (id is { } value)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", value));
        }

        var affected = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return affected;
    }

    /// <summary>
    /// Makes <paramref name="member"/> a member of <paramref name="granted"/> that can <c>SET ROLE</c> to it
    /// but does not inherit its privileges, on every supported major. PostgreSQL 16 added per-membership
    /// <c>WITH INHERIT FALSE, SET TRUE</c>; on 15 the same effect is the member's <c>NOINHERIT</c> attribute.
    /// </summary>
    private async Task GrantWithoutInheritAsync(string granted, string member)
    {
        var serverMajor = await ScalarAsync<int>(_fixture.SuperuserDataSource, "SELECT current_setting('server_version_num')::int / 10000");
        if (serverMajor >= 16)
        {
            await _fixture.ExecuteAsSuperuserAsync($"GRANT \"{granted}\" TO \"{member}\" WITH INHERIT FALSE, SET TRUE");
        }
        else
        {
            await _fixture.ExecuteAsSuperuserAsync($"ALTER ROLE \"{member}\" NOINHERIT");
            await _fixture.ExecuteAsSuperuserAsync($"GRANT \"{granted}\" TO \"{member}\"");
        }
    }

    private Task<T> OwnerScalarAsync<T>(string sql) => ScalarAsync<T>(_fixture.OwnerDataSource, sql);

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<string>> OwnerRowsAsync(string sql)
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    /// <summary>Every ACL in the schema -- schema, relations, columns, functions -- as one comparable string.</summary>
    private static Task<string> AclSnapshotAsync(NpgsqlDataSource source) => ScalarAsync<string>(
        source,
        "SELECT string_agg(entry, E'\\n' ORDER BY entry) FROM (" +
        "SELECT 'schema ' || coalesce(nspacl::text, '') AS entry FROM pg_namespace WHERE nspname = 'agent_experience' " +
        "UNION ALL SELECT 'rel ' || c.relname || ' ' || coalesce(c.relacl::text, '') FROM pg_class c " +
        "WHERE c.relnamespace = 'agent_experience'::regnamespace " +
        "UNION ALL SELECT 'col ' || c.relname || '.' || a.attname || ' ' || a.attacl::text FROM pg_attribute a " +
        "JOIN pg_class c ON c.oid = a.attrelid WHERE c.relnamespace = 'agent_experience'::regnamespace AND a.attacl IS NOT NULL " +
        "UNION ALL SELECT 'fn ' || p.oid::regprocedure::text || ' ' || coalesce(p.proacl::text, '') FROM pg_proc p " +
        "WHERE p.pronamespace = 'agent_experience'::regnamespace) acl");

    /// <summary>A fresh database owned by a fresh owner role, migrated by it, and a fresh application role.</summary>
    private async Task<TwoRoleWorld> TwoRoleDatabaseAsync(string purpose)
    {
        var owner = await _fixture.CreateLoginRoleAsync(purpose + "_o");
        var app = await _fixture.CreateLoginRoleAsync(purpose + "_a");
        var database = await _fixture.CreateDatabaseNameAsync(purpose, owner);
        await _fixture.ExecuteAsSuperuserAsync(PostgresFixture.OwnerParameterGrant(owner));

        var ownerSource = NpgsqlDataSource.Create(_fixture.ConnectionString(database, owner));
        await ExperienceSchemaMigrator.MigrateAsync(ownerSource, CancellationToken.None);
        return new TwoRoleWorld(ownerSource, app, _fixture.ConnectionString(database, app));
    }

    private sealed record TwoRoleWorld(NpgsqlDataSource Owner, string App, string AppConnectionString) : IAsyncDisposable
    {
        public NpgsqlDataSource AppSource() => NpgsqlDataSource.Create(AppConnectionString);

        public ValueTask DisposeAsync() => Owner.DisposeAsync();
    }
}
