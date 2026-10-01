using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 15.1's second layer, proved from the application role's side with row-level security on, in every suite
/// mode: each test builds its own two-role database with <see cref="ExperienceApplicationRoleOptions.EnableRowLevelSecurity"/>
/// set, so these run whether or not <c>AGENTEXPERIENCE_TEST_RLS</c> turned the whole suite's layer on.
/// </summary>
/// <remarks>
/// The broken predicates below are a test-only SQL path: a store statement's own text with its scope predicate
/// replaced, run by hand inside a transaction that declared its bounds exactly as a store does. No store is changed.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRowLevelSecurityTests(PostgresFixture fixture)
{
    private const string Administrator = "sharing-administrator";

    /// <summary>Every table the policies cover in this package's schema, with no vectors table in these databases.</summary>
    private static readonly string[] CoveredTables =
    [
        "experience_records", "lifecycle_events", "confidence_evidence", "experience_grants", "experience_grant_events",
        "experience_grant_access", "reuse_feedback", "reuse_feedback_exposures",
    ];

    // ---------------------------------------------------------------- nothing declared

    [Fact]
    public async Task With_nothing_declared_the_application_role_sees_no_row_and_can_write_none()
    {
        await using var world = await RlsWorldAsync("rls_nothing");
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = await world.CreateAsync(tenant, scope);
        await world.CommitAsync(tenant, scope, record);

        // The rows are there: the owner, whom row-level security does not bind, sees them.
        Assert.Equal(1L, await ScalarAsync<long>(world.Owner, "SELECT count(*) FROM agent_experience.experience_records"));
        Assert.Equal(1L, await ScalarAsync<long>(world.Owner, "SELECT count(*) FROM agent_experience.lifecycle_events"));

        // The application role, with no bounds declared, sees none of them in any covered table.
        foreach (var table in CoveredTables)
        {
            Assert.Equal(0L, await ScalarAsync<long>(world.App, $"SELECT count(*) FROM agent_experience.{table}"));
        }

        // Nor can it write one, even a row in a scope nobody has used.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertRawRecordAsync(world.App, Scope(NewTenant()), declare: null));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Contains("row-level security", refused.MessageText, StringComparison.Ordinal);

        // And a statement that reads another table's rows to decide what to write finds nothing to write from.
        Assert.Equal(0, await ExecuteAsync(
            world.App,
            declare: null,
            "UPDATE agent_experience.experience_records SET updated_at = updated_at WHERE experience_id = @id",
            ("id", record)));
    }

    // ---------------------------------------------------------------- one tenant's bounds

    [Fact]
    public async Task A_declaration_for_one_tenant_cannot_read_or_update_another_tenants_rows()
    {
        await using var world = await RlsWorldAsync("rls_tenants");
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var a = await world.CreateAsync(tenantA, Scope(tenantA));
        var b = await world.CreateAsync(tenantB, Scope(tenantB));
        await world.CommitAsync(tenantB, Scope(tenantB), b);

        var declareA = Authorize(tenantA);

        // Tenant A's bounds see A's record and nothing of B's, by ID, in the record table or the event log.
        Assert.Equal(1L, await ScalarAsync<long>(world.App, declareA, "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id", ("id", a)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, declareA, "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id", ("id", b)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, declareA, "SELECT count(*) FROM agent_experience.lifecycle_events WHERE experience_id = @id", ("id", b)));

        // An UPDATE of B's record under A's bounds matches nothing, so nothing moves.
        Assert.Equal(0, await ExecuteAsync(
            world.App, declareA, "UPDATE agent_experience.experience_records SET updated_at = now() WHERE experience_id = @id", ("id", b)));
        Assert.Equal(1L, await ScalarAsync<long>(world.Owner, $"SELECT revision FROM agent_experience.experience_records WHERE experience_id = '{b}'"));

        // And a row in B's scope cannot be written under A's bounds.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertRawRecordAsync(world.App, Scope(tenantB), declareA));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);

        // The bounds are the host's, bound for bound: a context restricted to team-a does not see the project's
        // own team-less record, while one with no team bound does.
        var project = Scope(tenantA);
        var teamRecord = await world.CreateAsync(tenantA, project with { TeamId = "team-a" });
        var teamOnly = Authorize(tenantA) with { TeamId = "team-a" };
        Assert.Equal(0L, await ScalarAsync<long>(world.App, teamOnly, "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id", ("id", a)));
        Assert.Equal(1L, await ScalarAsync<long>(world.App, teamOnly, "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id", ("id", teamRecord)));
        Assert.Equal(1L, await ScalarAsync<long>(world.App, declareA, "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id", ("id", teamRecord)));
    }

    // ---------------------------------------------------------------- a broken store predicate

    [Fact]
    public async Task A_broken_store_predicate_is_cut_back_to_the_declared_bounds()
    {
        await using var world = await RlsWorldAsync("rls_broken");
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var a = await world.CreateAsync(tenantA, Scope(tenantA));
        var b = await world.CreateAsync(tenantB, Scope(tenantB));

        // The single read's own statement, with its exact-scope predicate replaced by "true" -- the mistake the
        // second layer exists for. Run as the owner, whom row-level security does not bind, it really does hand
        // over tenant B's record to a caller asking in tenant A's scope.
        var broken = BreakScopePredicate(PostgresExperienceRecordStore.GetManySql);
        Assert.Equal(
            new[] { a, b }.Order(),
            (await ReadIdsAsync(world.Owner, declare: null, broken, Scope(tenantA), a, b)).Order());

        // Run as the application role, inside a transaction that declared tenant A's bounds exactly as the store
        // does, the same broken statement returns tenant A's record and nothing of tenant B's.
        Assert.Equal(new[] { a }, (await ReadIdsAsync(world.App, Authorize(tenantA), broken, Scope(tenantA), a, b)).ToArray());

        // The same holds for a broken write: the projection update with its scope predicate removed.
        var brokenUpdate = BreakScopePredicate(
            "UPDATE agent_experience.experience_records SET updated_at = now() WHERE experience_id = ANY(@experience_ids) AND " +
            PostgresExperienceRecordStore.ScopePredicate);
        Assert.Equal(0, await ExecuteAsync(
            world.App, Authorize(tenantA), brokenUpdate, ("experience_ids", new[] { b }), Scope(tenantA)));
    }

    // ---------------------------------------------------------------- grants

    [Fact]
    public async Task A_live_grant_admits_its_recipients_bounds_to_read_and_never_to_write_and_revoking_it_ends_that()
    {
        await using var world = await RlsWorldAsync("rls_grants");
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await world.CreateAsync(tenant, owner);

        var grants = new PostgresExperienceGrantStore(world.App);
        var issued = await grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(Guid.NewGuid(), id, owner, recipient, "team-b owns the follow-up", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, issued.Outcome);

        var asRecipient = Authorize(tenant) with { TeamId = "team-b" };
        var asStranger = Authorize(tenant) with { TeamId = "team-c" };
        const string ById = "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id";

        // The recipient's bounds admit the owner's record for reading while the grant is live; a sibling's do not.
        Assert.Equal(1L, await ScalarAsync<long>(world.App, asRecipient, ById, ("id", id)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, asStranger, ById, ("id", id)));

        // The store reads it through the grant exactly as before.
        var store = new PostgresExperienceRecordStore(world.App);
        var shared = await store.GetAsync(asRecipient, recipient, id, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, shared.Outcome);
        Assert.True(shared.SharedByGrant);

        // A grant never admits a write.
        Assert.Equal(0, await ExecuteAsync(
            world.App, asRecipient, "UPDATE agent_experience.experience_records SET updated_at = now() WHERE experience_id = @id", ("id", id)));

        // Revoked, it admits nothing.
        var revoked = await grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(issued.Grant!.GrantId, owner, "done"),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Revoked, revoked.Outcome);
        Assert.Equal(0L, await ScalarAsync<long>(world.App, asRecipient, ById, ("id", id)));
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await store.GetAsync(asRecipient, recipient, id, CancellationToken.None)).Outcome);
    }

    // ---------------------------------------------------------------- the owner's own paths

    [Fact]
    public async Task Erasure_and_the_retention_sweep_still_work_behind_row_level_security()
    {
        await using var world = await RlsWorldAsync("rls_erasure");
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(world.App);

        var erased = await world.CreateAsync(tenant, scope);
        await world.CommitAsync(tenant, scope, erased);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(Authorize(tenant), scope, erased, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.GetAsync(Authorize(tenant), scope, erased, CancellationToken.None)).Outcome);
        Assert.Equal(0L, await ScalarAsync<long>(world.Owner, $"SELECT count(*) FROM agent_experience.lifecycle_events WHERE experience_id = '{erased}'"));

        var old = await world.CreateAsync(tenant, scope, createdAt: DateTimeOffset.UtcNow.AddDays(-400));
        var swept = await store.SweepExpiredAsync(Authorize(tenant), scope, TimeSpan.FromDays(365), 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, swept.Outcome);
        Assert.Equal(1, swept.DeletedCount);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.GetAsync(Authorize(tenant), scope, old, CancellationToken.None)).Outcome);
    }

    // ---------------------------------------------------------------- the privileges step

    [Fact]
    public async Task Enabling_is_declarative_never_forced_and_verified()
    {
        await using var world = await RlsWorldAsync("rls_declarative");
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.Equal((true, false), (row.Enabled, row.Forced)));

        // A table forced by hand is put back: forcing would bind the owner's own erasure and sealing functions.
        await ExecuteAsync(world.Owner, declare: null, "ALTER TABLE agent_experience.lifecycle_events FORCE ROW LEVEL SECURITY");
        await world.ApplyAsync(enable: true);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.Equal((true, false), (row.Enabled, row.Forced)));

        // Switched off by the same call, every covered table, and on again.
        await world.ApplyAsync(enable: false);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.False(row.Enabled));
        await world.ApplyAsync(enable: true);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.True(row.Enabled));
    }

    [Fact]
    public async Task Enabling_refuses_a_policy_the_migrations_did_not_create_and_a_missing_one_and_changes_nothing()
    {
        await using var world = await RlsWorldAsync("rls_policies", enable: false);

        await ExecuteAsync(world.Owner, declare: null, "CREATE POLICY wide_open ON agent_experience.lifecycle_events USING (true)");
        var extra = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true));
        Assert.Contains("wide_open", extra.Message, StringComparison.Ordinal);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.False(row.Enabled));

        await ExecuteAsync(world.Owner, declare: null, "DROP POLICY wide_open ON agent_experience.lifecycle_events");
        await ExecuteAsync(world.Owner, declare: null, "DROP POLICY rls_records_update ON agent_experience.experience_records");
        var missing = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true));
        Assert.Contains("rls_records_update", missing.Message, StringComparison.Ordinal);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.False(row.Enabled));
    }

    [Fact]
    public async Task Enabling_refuses_an_application_role_that_can_bypass_it()
    {
        await using var world = await RlsWorldAsync("rls_bypass", enable: false);
        await fixture.ExecuteAsSuperuserAsync($"ALTER ROLE \"{world.AppRole}\" BYPASSRLS");

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true));
        Assert.Contains("BYPASSRLS", refused.Message, StringComparison.Ordinal);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.False(row.Enabled));

        // Disabling needs no such check: nothing is left for the attribute to bypass.
        await world.ApplyAsync(enable: false);
    }

    [Fact]
    public async Task The_suite_runs_behind_row_level_security_exactly_when_its_mode_says_so()
    {
        // The guard against a suite that believes it proved the policies while a test quietly switched them off.
        Assert.All(
            await RowSecurityAsync(fixture.OwnerDataSource),
            row => Assert.Equal((RowLevelSecurityMode.IsOn, false), (row.Enabled, row.Forced)));
    }

    // ---------------------------------------------------------------- every covered table

    /// <summary>What <see cref="Every_covered_table_hides_another_tenants_rows_and_refuses_writing_them"/> seeds, per table.</summary>
    public static TheoryData<string, string> CoveredTablesWithKeys() => new()
    {
        { "experience_records", "experience_id" },
        { "lifecycle_events", "event_id" },
        { "confidence_evidence", "evidence_id" },
        { "experience_grants", "grant_id" },
        { "experience_grant_events", "event_id" },
        { "experience_grant_access", "access_id" },
        { "reuse_feedback", "feedback_id" },
        { "reuse_feedback_exposures", "experience_id" },
    };

    [Theory]
    [MemberData(nameof(CoveredTablesWithKeys))]
    public async Task Every_covered_table_hides_another_tenants_rows_and_refuses_writing_them(string table, string key)
    {
        var stem = table.Replace("experience_", string.Empty, StringComparison.Ordinal);
        await using var world = await RlsWorldAsync("rls_t_" + stem[..Math.Min(12, stem.Length)]);
        var tenantB = NewTenant();
        await world.SeedEveryTableAsync(tenantB);

        // The owner, whom the policies do not bind, sees tenant B's row in this table.
        Assert.True(await ScalarAsync<long>(world.Owner, $"SELECT count(*) FROM agent_experience.{table}") > 0);

        // Tenant A's bounds see none of it, and cannot write a copy of it under a fresh key.
        var tenantA = Authorize(NewTenant());
        Assert.Equal(0L, await ScalarAsync<long>(world.App, tenantA, $"SELECT count(*) FROM agent_experience.{table}"));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => CopyRowAsync(world, table, key, tenantA));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Contains("row-level security", refused.MessageText, StringComparison.Ordinal);

        // The grant's recipient (tenant B, team-b) sees exactly what a grant confers: the live grant, its own access
        // row and the shared record -- never the owner's history, evidence, feedback, or the grant's audit trail.
        var recipient = Authorize(tenantB) with { TeamId = "team-b" };
        var recipientSees = table is "experience_records" or "experience_grants" or "experience_grant_access";
        Assert.Equal(recipientSees ? 1L : 0L, await ScalarAsync<long>(world.App, recipient, $"SELECT count(*) FROM agent_experience.{table}"));
        if (table != "experience_grant_access")
        {
            // The one table a recipient writes is the access log, about its own reads; everything else is the owner's.
            var alsoRefused = await Assert.ThrowsAsync<PostgresException>(() => CopyRowAsync(world, table, key, recipient));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, alsoRefused.SqlState);
        }
    }

    [Fact]
    public async Task A_grant_admits_a_record_only_while_live_and_only_the_record_owner_and_recipient_it_names()
    {
        await using var world = await RlsWorldAsync("rls_grant_rules");
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        const string ById = "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id";

        var expired = await world.CreateAsync(tenant, owner);
        var granted = await world.CreateAsync(tenant, owner);
        var other = await world.CreateAsync(tenant, owner);
        var mismatched = await world.CreateAsync(tenant, owner);
        var agentShared = await world.CreateAsync(tenant, owner);
        var userShared = await world.CreateAsync(tenant, owner);

        // An expired grant, written by hand (the library never issues one), admits nothing.
        await world.SeedGrantAsync(expired, owner, Scope(tenant, team: "team-b"), issuedAgo: TimeSpan.FromHours(2), expiresIn: TimeSpan.FromHours(-1));

        // A live grant over one record admits that record, not a sibling.
        await world.SeedGrantAsync(granted, owner, Scope(tenant, team: "team-b"), issuedAgo: TimeSpan.Zero, expiresIn: TimeSpan.FromHours(1));

        // A grant whose owner columns do not describe the record it names admits nothing.
        await world.SeedGrantAsync(mismatched, owner with { TeamId = "team-z" }, Scope(tenant, team: "team-b"), issuedAgo: TimeSpan.Zero, expiresIn: TimeSpan.FromHours(1));

        // Recipients bounded below the team: an agent, and a user.
        await world.SeedGrantAsync(agentShared, owner, Scope(tenant) with { AgentId = "agent-1" }, issuedAgo: TimeSpan.Zero, expiresIn: TimeSpan.FromHours(1));
        await world.SeedGrantAsync(userShared, owner, Scope(tenant) with { UserId = "user-1" }, issuedAgo: TimeSpan.Zero, expiresIn: TimeSpan.FromHours(1));

        var teamB = Authorize(tenant) with { TeamId = "team-b" };
        Assert.Equal(0L, await ScalarAsync<long>(world.App, teamB, ById, ("id", expired)));
        Assert.Equal(1L, await ScalarAsync<long>(world.App, teamB, ById, ("id", granted)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, teamB, ById, ("id", other)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, teamB, ById, ("id", mismatched)));

        // The recipient's bounds decide, field by field: a recipient with no team whose agent (or user) is the
        // grant's is admitted; another agent (or user), or a team bound the grant's recipient does not have, is not.
        Assert.Equal(1L, await ScalarAsync<long>(world.App, Authorize(tenant) with { AgentId = "agent-1" }, ById, ("id", agentShared)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, Authorize(tenant) with { AgentId = "agent-2" }, ById, ("id", agentShared)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, Authorize(tenant) with { AgentId = "agent-1", TeamId = "team-b" }, ById, ("id", agentShared)));
        Assert.Equal(1L, await ScalarAsync<long>(world.App, Authorize(tenant) with { UserId = "user-1" }, ById, ("id", userShared)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, Authorize(tenant) with { UserId = "user-2" }, ById, ("id", userShared)));

        // A recipient sees a grant only while it is live: the expired one is invisible to team-b, the live one is not.
        Assert.Equal(1L, await ScalarAsync<long>(world.App, teamB, "SELECT count(*) FROM agent_experience.experience_grants WHERE experience_id = @id", ("id", granted)));
        Assert.Equal(0L, await ScalarAsync<long>(world.App, teamB, "SELECT count(*) FROM agent_experience.experience_grants WHERE experience_id = @id", ("id", expired)));
    }

    // ---------------------------------------------------------------- the owner's functions apply the bounds too

    [Fact]
    public async Task A_declaration_for_one_tenant_cannot_purge_sweep_or_seal_another_tenants_rows_through_the_owners_functions()
    {
        await using var world = await RlsWorldAsync("rls_definer");
        var tenantB = NewTenant();
        var scopeB = Scope(tenantB);
        var record = await world.CreateAsync(tenantB, scopeB);
        var declareA = Authorize(NewTenant());
        const string State = "SELECT payload::text || '|' || revision || '|' || (deleted_at IS NULL) FROM agent_experience.experience_records WHERE experience_id = ";
        var before = await ScalarAsync<string>(world.Owner, $"{State}'{record}'");

        var calls = new[]
        {
            "SELECT * FROM agent_experience.purge_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
            "SELECT agent_experience.purge_expired_grants(@tenant, 'app-1', 'project-1', NULL, NULL, NULL, now(), NULL)",
            "SELECT * FROM agent_experience.purge_grant_access(@tenant, 'app-1', 'project-1', NULL, NULL, NULL, true, now() - interval '400 days', NULL)",
            "SELECT agent_experience.seal_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, 0, '{\"sealed\": \"aexp-sealed:v1:x\"}'::jsonb)",
        };

        foreach (var call in calls)
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync(world.App, declareA, "SET LOCAL agent_experience.erasure_destroys_key = 'on'; " + call, ("id", record), ("tenant", tenantB)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            Assert.Contains("outside the declared authorization bounds", refused.MessageText, StringComparison.Ordinal);
        }

        // Nothing moved: the record is live, with the payload and revision it had.
        Assert.Equal(before, await ScalarAsync<string>(world.Owner, $"{State}'{record}'"));
        Assert.EndsWith("|True", before.Replace("|true", "|True", StringComparison.Ordinal), StringComparison.Ordinal);

        // Under tenant B's own bounds the same erasure is the ordinary one.
        await ExecuteAsync(
            world.App, Authorize(tenantB),
            "SET LOCAL agent_experience.erasure_destroys_key = 'on'; SELECT * FROM agent_experience.purge_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())",
            ("id", record), ("tenant", tenantB));
        Assert.Equal(1L, await ScalarAsync<long>(world.Owner,
            $"SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = '{record}' AND deleted_at IS NOT NULL"));
    }

    [Fact]
    public async Task With_row_level_security_on_an_undeclared_caller_cannot_purge_or_seal_but_the_owner_still_can()
    {
        await using var world = await RlsWorldAsync("rls_undeclared");
        var tenant = NewTenant();
        var record = await world.CreateAsync(tenant, Scope(tenant));
        const string Purge =
            "SET LOCAL agent_experience.erasure_destroys_key = 'on'; " +
            "SELECT * FROM agent_experience.purge_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, NULL, now())";
        const string Seal =
            "SELECT agent_experience.seal_experience_record(@id, @tenant, 'app-1', 'project-1', NULL, NULL, NULL, 0, '{\"sealed\": \"aexp-sealed:v1:x\"}'::jsonb)";

        // The application role with nothing declared, and with the marker set to anything but 'on', is refused.
        foreach (var prefix in new[] { string.Empty, "SET LOCAL agent_experience.auth_set = 'off'; " })
        {
            foreach (var call in new[] { Purge, Seal })
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(
                    () => ExecuteAsync(world.App, declare: null, prefix + call, ("id", record), ("tenant", tenant)));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
                Assert.Contains("no authorization bounds were declared", refused.MessageText, StringComparison.Ordinal);
            }
        }

        Assert.Equal(1L, await ScalarAsync<long>(world.Owner,
            $"SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = '{record}' AND deleted_at IS NULL"));

        // The owner, whom row-level security does not bind either, still erases without declaring anything.
        await ExecuteAsync(world.Owner, declare: null, Purge, ("id", record), ("tenant", tenant));
        Assert.Equal(1L, await ScalarAsync<long>(world.Owner,
            $"SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = '{record}' AND deleted_at IS NOT NULL"));

        // With row-level security off, an undeclared caller behaves exactly as it always did.
        var other = await world.CreateAsync(tenant, Scope(tenant));
        await world.ApplyAsync(enable: false);
        await ExecuteAsync(world.App, declare: null, Purge, ("id", other), ("tenant", tenant));
        Assert.Equal(1L, await ScalarAsync<long>(world.Owner,
            $"SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = '{other}' AND deleted_at IS NOT NULL"));
    }

    [Fact]
    public async Task Enabling_puts_back_a_helper_function_replaced_by_hand()
    {
        await using var world = await RlsWorldAsync("rls_helper");
        var tenant = NewTenant();
        var teamB = await world.CreateAsync(tenant, Scope(tenant, team: "team-b"));
        var teamA = Authorize(tenant) with { TeamId = "team-a" };
        const string ById = "SELECT count(*) FROM agent_experience.experience_records WHERE experience_id = @id";
        Assert.Equal(0L, await ScalarAsync<long>(world.App, teamA, ById, ("id", teamB)));

        // An owner replaces a helper so that every field reads as unrestricted: team-a's bounds now see team-b's record.
        await ExecuteAsync(world.Owner, declare: null,
            "CREATE OR REPLACE FUNCTION agent_experience.rls_unbounded(p_field text) RETURNS boolean LANGUAGE sql STABLE RETURN true");
        Assert.Equal(1L, await ScalarAsync<long>(world.App, teamA, ById, ("id", teamB)));

        // The next deploy re-creates the canonical helper before it enables anything.
        await world.ApplyAsync(enable: true);
        Assert.Equal(0L, await ScalarAsync<long>(world.App, teamA, ById, ("id", teamB)));
        Assert.Contains(
            "current_setting('agent_experience.auth_set'",
            await ScalarAsync<string>(world.Owner, "SELECT pg_get_functiondef('agent_experience.rls_unbounded(text)'::regprocedure)"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabling_refuses_a_default_for_the_settings_whatever_case_it_is_written_in()
    {
        await using var world = await RlsWorldAsync("rls_defaults_up", enable: false);
        var database = await ScalarAsync<string>(world.Owner, "SELECT current_database()::text");
        await fixture.ExecuteAsSuperuserAsync($"ALTER ROLE \"{world.AppRole}\" IN DATABASE \"{database}\" SET \"AGENT_EXPERIENCE.AUTH_SET\" = 'on'");

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true));
        Assert.Contains("auth_set", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.False(row.Enabled));
    }

    // ---------------------------------------------------------------- the privileges call: more

    [Fact]
    public async Task Enabling_puts_back_a_policy_altered_by_hand()
    {
        await using var world = await RlsWorldAsync("rls_altered");
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        await world.CreateAsync(tenantA, Scope(tenantA));
        await world.CreateAsync(tenantB, Scope(tenantB));
        const string All = "SELECT count(*) FROM agent_experience.experience_records";

        // An owner widens the read policy by hand: tenant A's bounds now see tenant B's record too.
        await ExecuteAsync(world.Owner, declare: null, "ALTER POLICY rls_records_select ON agent_experience.experience_records USING (true)");
        Assert.Equal(2L, await ScalarAsync<long>(world.App, Authorize(tenantA), All));

        // The next deploy re-creates the canonical policy before it enables anything.
        await world.ApplyAsync(enable: true);
        Assert.Equal(1L, await ScalarAsync<long>(world.App, Authorize(tenantA), All));
        Assert.Equal(
            RowLevelSecurityPolicies.All.Count(p => p.Table != ApplicationRolePrivileges.EmbeddingsTable),
            await ScalarAsync<long>(world.Owner, "SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid WHERE c.relnamespace = 'agent_experience'::regnamespace"));
    }

    [Fact]
    public async Task Enabling_refuses_a_default_for_the_settings_the_policies_read()
    {
        await using var world = await RlsWorldAsync("rls_defaults", enable: false);
        var database = await ScalarAsync<string>(world.Owner, "SELECT current_database()::text");
        await fixture.ExecuteAsSuperuserAsync($"ALTER ROLE \"{world.AppRole}\" IN DATABASE \"{database}\" SET agent_experience.auth_set = 'on'");

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true));
        // Case-insensitive: PostgreSQL stores a placeholder setting under the spelling its session first saw.
        Assert.Contains("agent_experience.auth_set", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(await RowSecurityAsync(world.Owner), row => Assert.False(row.Enabled));

        await fixture.ExecuteAsSuperuserAsync($"ALTER ROLE \"{world.AppRole}\" IN DATABASE \"{database}\" RESET agent_experience.auth_set");
        await world.ApplyAsync(enable: true);
    }

    [Fact]
    public async Task A_role_that_may_not_read_the_grant_table_still_reads_its_own_scope_through_the_exact_fallback()
    {
        await using var world = await RlsWorldAsync("rls_fallback");
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var mine = await world.CreateAsync(tenant, owner);

        var reader = await fixture.CreateLoginRoleAsync("rls_reader");
        var database = await ScalarAsync<string>(world.Owner, "SELECT current_database()::text");
        await ExecuteAsync(world.Owner, declare: null, $"GRANT USAGE ON SCHEMA agent_experience TO \"{reader}\"");
        await ExecuteAsync(world.Owner, declare: null, $"GRANT SELECT ON agent_experience.experience_records TO \"{reader}\"");
        await using var source = NpgsqlDataSource.Create(fixture.ConnectionString(database, reader));

        var notices = new List<ExperienceGrantSupportNotice>();
        var store = new PostgresExperienceRecordStore(source, notices.Add);
        var read = await store.GetAsync(Authorize(tenant), owner, mine, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(ExperienceGrantSupportReason.NotPermitted, Assert.Single(notices).Reason);

        // And a sibling team's bounds, with no grant it could read, see nothing -- narrower, never wider.
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await store.GetAsync(Authorize(tenant), Scope(tenant, team: "team-b"), mine, CancellationToken.None)).Outcome);
    }

    // ---------------------------------------------------------------- the suite's own database, in both modes

    [Fact]
    public async Task An_access_log_batch_naming_several_readers_lands_row_by_row_with_each_readers_own_scope()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var readerB = Scope(tenant, team: "team-b");
        var readerC = Scope(tenant, team: "team-c");
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var grants = new PostgresExperienceGrantStore(fixture.DataSource);

        async Task<(Guid Record, Guid Grant)> SharedAsync(Scope recipient)
        {
            var record = Minimal(owner);
            Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);
            var grant = await grants.CreateAsync(
                Authorize(tenant),
                new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
                new ExperienceGrantRequest(Guid.NewGuid(), record.ExperienceId, owner, recipient, "shared", DateTimeOffset.UtcNow.AddHours(1)),
                CancellationToken.None);
            return (record.ExperienceId, grant.Grant!.GrantId);
        }

        var first = await SharedAsync(readerB);
        var second = await SharedAsync(readerC);
        var third = await SharedAsync(readerB);

        ExperienceGrantAccess Access((Guid Record, Guid Grant) shared, Scope reader) => new(
            Guid.NewGuid(), shared.Grant, shared.Record, 0, owner, reader, "reader-principal", "batch", DateTimeOffset.UtcNow,
            ExperienceGrantDisclosure.LessonOnly);

        // Interleaved: B, C, B -- the port member declares each row's own recipient, group by group, in one transaction.
        var batch = new[] { Access(first, readerB), Access(second, readerC), Access(third, readerB) };
        await new PostgresExperienceGrantAccessLog(fixture.DataSource).RecordAsync(batch, CancellationToken.None);

        foreach (var access in batch)
        {
            await using var command = fixture.RawDataSource.CreateCommand(
                "SELECT grant_id, experience_id, recipient_team_id, team_id FROM agent_experience.experience_grant_access WHERE access_id = @id");
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", access.AccessId));
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(access.GrantId, reader.GetGuid(0));
            Assert.Equal(access.ExperienceId, reader.GetGuid(1));
            Assert.Equal(access.RecipientScope.TeamId, reader.GetString(2));
            Assert.Equal("team-a", reader.GetString(3));
            Assert.False(await reader.ReadAsync());
        }

        // A row that names a grant it was not delivered through is refused with row-level security on, and the
        // whole batch with it; with it off, only the store predicates stand, and the ledger takes it as written.
        var forged = new[] { Access(first, readerB), Access(second, readerB) };
        if (RowLevelSecurityMode.IsOn)
        {
            await Assert.ThrowsAsync<ExperienceStoreException>(
                () => new PostgresExperienceGrantAccessLog(fixture.DataSource).RecordAsync(forged, CancellationToken.None));
            Assert.Equal(0L, await RawCountAsync("SELECT count(*) FROM agent_experience.experience_grant_access WHERE access_id = ANY(@ids)", forged.Select(a => a.AccessId).ToArray()));
        }
        else
        {
            await new PostgresExperienceGrantAccessLog(fixture.DataSource).RecordAsync(forged, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Declared_bounds_end_with_their_transaction_on_the_same_physical_connection()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString(PostgresFixture.StoreDatabase, PostgresFixture.ApplicationRoleName))
        {
            MaxPoolSize = 1,
        };
        await using var single = NpgsqlDataSource.Create(builder.ConnectionString);
        var tenant = NewTenant();
        const string Probe =
            "SELECT pg_backend_pid(), coalesce(current_setting('agent_experience.auth_set', true), '') = 'on', agent_experience.rls_bound('tenant')";

        async Task<(int Pid, bool Set, string? Tenant)> ProbeAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction)
        {
            await using var command = new NpgsqlCommand(Probe, connection, transaction);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            return (reader.GetInt32(0), reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        int pid;
        await using (var connection = await single.OpenConnectionAsync())
        {
            foreach (var commit in new[] { true, false })
            {
                await using (var transaction = await ExperienceSessionContext.BeginAsync(connection, Authorize(tenant), CancellationToken.None))
                {
                    var inside = await ProbeAsync(connection, transaction);
                    Assert.True(inside.Set);
                    Assert.Equal(tenant, inside.Tenant);
                    if (commit)
                    {
                        await transaction.CommitAsync();
                    }
                    else
                    {
                        await transaction.RollbackAsync();
                    }
                }

                await using var next = await connection.BeginTransactionAsync();
                var after = await ProbeAsync(connection, next);
                Assert.False(after.Set);
                Assert.Null(after.Tenant);
            }

            pid = (await ProbeAsync(connection, null)).Pid;
        }

        // A store operation on the one pooled connection, and then a statement on it that declared nothing.
        var record = Minimal(Scope(tenant));
        Assert.Equal(ExperienceStoreOutcome.Created, (await new PostgresExperienceRecordStore(single).CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);
        await using (var connection = await single.OpenConnectionAsync())
        {
            var afterStore = await ProbeAsync(connection, null);
            Assert.Equal(pid, afterStore.Pid);
            Assert.False(afterStore.Set);
            Assert.Null(afterStore.Tenant);
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task<long> RawCountAsync(string sql, Guid[] ids)
    {
        await using var command = fixture.RawDataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", ids));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// One of <paramref name="table"/>'s rows, read as the owner, written back as the application role under
    /// <paramref name="declare"/> with a fresh <paramref name="key"/>: every other column exactly as stored, generated
    /// columns left to the database.
    /// </summary>
    private static async Task CopyRowAsync(RlsWorld world, string table, string key, AuthorizationContext declare)
    {
        var row = await ScalarAsync<string>(world.Owner, $"SELECT to_jsonb(t)::text FROM agent_experience.{table} t LIMIT 1");
        var columns = await ScalarAsync<string>(
            world.Owner,
            "SELECT string_agg(quote_ident(attname), ', ' ORDER BY attnum) FROM pg_attribute " +
            $"WHERE attrelid = 'agent_experience.{table}'::regclass AND attnum > 0 AND NOT attisdropped AND attgenerated = ''");
        await ExecuteAsync(
            world.App,
            declare,
            $"INSERT INTO agent_experience.{table} ({columns}) SELECT {columns} FROM jsonb_populate_record(NULL::agent_experience.{table}, " +
            $"jsonb_set(@row::jsonb, '{{{key}}}', to_jsonb(gen_random_uuid())))",
            ("row", row));
    }

    private static string BreakScopePredicate(string sql)
    {
        var broken = sql
            .Replace(PostgresExperienceRecordStore.RecordScopePredicate, "true", StringComparison.Ordinal)
            .Replace(PostgresExperienceRecordStore.ScopePredicate, "true", StringComparison.Ordinal);
        Assert.NotEqual(sql, broken);
        return broken;
    }

    private static async Task<List<Guid>> ReadIdsAsync(
        NpgsqlDataSource source, AuthorizationContext? declare, string sql, Scope scope, params Guid[] ids)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (declare is not null)
        {
            await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("experience_ids", ids));
        PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, scope);

        var found = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            found.Add(reader.GetGuid(0));
        }

        return found;
    }

    private static Task<int> ExecuteAsync(NpgsqlDataSource source, AuthorizationContext? declare, string sql, params (string Name, object Value)[] parameters) =>
        ExecuteAsync(source, declare, sql, parameters, scope: null);

    private static Task<int> ExecuteAsync(NpgsqlDataSource source, AuthorizationContext? declare, string sql, (string Name, object Value) parameter, Scope scope) =>
        ExecuteAsync(source, declare, sql, [parameter], scope);

    private static async Task<int> ExecuteAsync(
        NpgsqlDataSource source, AuthorizationContext? declare, string sql, (string Name, object Value)[] parameters, Scope? scope)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (declare is not null)
        {
            await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        if (scope is not null)
        {
            PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, scope);
        }

        var affected = await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return affected;
    }

    private static Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql) => ScalarAsync<T>(source, null, sql);

    private static async Task<T> ScalarAsync<T>(
        NpgsqlDataSource source, AuthorizationContext? declare, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (declare is not null)
        {
            await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>A hand-written record row, the way a store's INSERT would write one, under the given bounds or none.</summary>
    private static Task InsertRawRecordAsync(NpgsqlDataSource source, Scope scope, AuthorizationContext? declare)
    {
        var sql =
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, project_id, " +
            "team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, " +
            "created_at, updated_at, payload_version, payload) VALUES (gen_random_uuid(), gen_random_uuid(), @tenant_id, " +
            "@application_id, @project_id, @team_id, @agent_id, @user_id, 'task-1', 'Candidate', 0, 0, 0, 0, now(), now(), 1, '{}'::jsonb)";
        return ExecuteAsync(source, declare, sql, [], scope);
    }

    private static async Task<List<(string Table, bool Enabled, bool Forced)>> RowSecurityAsync(NpgsqlDataSource owner)
    {
        await using var command = owner.CreateCommand(
            "SELECT c.relname::text, c.relrowsecurity, c.relforcerowsecurity FROM pg_class c " +
            "WHERE c.relnamespace = 'agent_experience'::regnamespace AND c.relname = ANY(@tables) ORDER BY 1");
        command.Parameters.Add(new NpgsqlParameter<string[]>("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = CoveredTables });

        var rows = new List<(string, bool, bool)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        Assert.Equal(CoveredTables.Length, rows.Count);
        return rows;
    }

    /// <summary>
    /// A fresh database owned by a fresh owner, migrated by it, and a fresh application role given the manifest with
    /// row-level security switched on (unless <paramref name="enable"/> says otherwise).
    /// </summary>
    private async Task<RlsWorld> RlsWorldAsync(string purpose, bool enable = true)
    {
        var owner = await fixture.CreateLoginRoleAsync(purpose + "_o");
        var app = await fixture.CreateLoginRoleAsync(purpose + "_a");
        var database = await fixture.CreateDatabaseNameAsync(purpose, owner);
        await fixture.ExecuteAsSuperuserAsync(PostgresFixture.OwnerParameterGrant(owner));

        var ownerSource = NpgsqlDataSource.Create(fixture.ConnectionString(database, owner));
        await ExperienceSchemaMigrator.MigrateAsync(ownerSource, CancellationToken.None);

        var world = new RlsWorld(ownerSource, NpgsqlDataSource.Create(fixture.ConnectionString(database, app)), app);
        await world.ApplyAsync(enable);
        return world;
    }

    private sealed record RlsWorld(NpgsqlDataSource Owner, NpgsqlDataSource App, string AppRole) : IAsyncDisposable
    {
        public Task ApplyAsync(bool enable) =>
            ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
                Owner,
                new ExperienceApplicationRoleOptions(AppRole)
                {
                    AllowErasure = true,
                    AllowAccessLogPurge = true,
                    AllowSealing = true,
                    EnableRowLevelSecurity = enable,
                },
                CancellationToken.None);

        /// <summary>Creates a record through the store, as the application role, and returns its ID.</summary>
        public async Task<Guid> CreateAsync(string tenant, Scope scope, DateTimeOffset? createdAt = null)
        {
            var record = Minimal(scope, createdAt: createdAt is { } at ? new DateTimeOffset(at.UtcTicks - (at.UtcTicks % 10), TimeSpan.Zero) : null);
            var created = await new PostgresExperienceRecordStore(App).CreateAsync(Authorize(tenant), record, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
            return record.ExperienceId;
        }

        /// <summary>Commits the record's first lifecycle event through the store, as the application role.</summary>
        public async Task CommitAsync(string tenant, Scope scope, Guid id)
        {
            var committed = await new PostgresExperienceRecordStore(App).CommitLifecycleEventAsync(
                Authorize(tenant), scope, Event(id, ExperienceStatus.Candidate, ExperienceStatus.Validated, expectedRevision: 0), CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Committed, committed.Outcome);
        }

        /// <summary>A grant written by hand as the owner, with the issue time and expiry the caller chooses.</summary>
        public async Task SeedGrantAsync(Guid experienceId, Scope owner, Scope recipient, TimeSpan issuedAgo, TimeSpan expiresIn)
        {
            await using var command = Owner.CreateCommand(
                "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, tenant_id, application_id, project_id, " +
                "team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, recipient_project_id, recipient_team_id, " +
                "recipient_agent_id, recipient_user_id, reason, administrator_principal_id, issued_at, expires_at) VALUES " +
                "(gen_random_uuid(), @id, @tenant, @app, @project, @team, @agent, @user, @tenant, @app, @project, @r_team, @r_agent, " +
                "@r_user, 'written by hand', 'administrator', now() - @ago, now() + @expires)");
            command.Parameters.AddWithValue("id", experienceId);
            command.Parameters.AddWithValue("tenant", owner.TenantId);
            command.Parameters.AddWithValue("app", owner.ApplicationId);
            command.Parameters.AddWithValue("project", owner.ProjectId);
            command.Parameters.Add(new NpgsqlParameter("team", NpgsqlDbType.Text) { Value = (object?)owner.TeamId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = (object?)owner.AgentId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Text) { Value = (object?)owner.UserId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("r_team", NpgsqlDbType.Text) { Value = (object?)recipient.TeamId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("r_agent", NpgsqlDbType.Text) { Value = (object?)recipient.AgentId ?? DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter("r_user", NpgsqlDbType.Text) { Value = (object?)recipient.UserId ?? DBNull.Value });
            command.Parameters.AddWithValue("ago", issuedAgo);
            command.Parameters.AddWithValue("expires", expiresIn);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        /// <summary>
        /// One row in every covered table for <paramref name="tenant"/>, through the stores as the application role
        /// where they write it and as the owner where only a hand-written row will do: a team-a record with a lifecycle
        /// event and a piece of evidence, a grant to team-b with its issue event, team-b's audited read of it, and a
        /// feedback submission with its exposure.
        /// </summary>
        public async Task SeedEveryTableAsync(string tenant)
        {
            var owner = Scope(tenant, team: "team-a");
            var recipient = Scope(tenant, team: "team-b");
            var record = await CreateAsync(tenant, owner);
            await CommitAsync(tenant, owner, record);

            await using (var evidence = Owner.CreateCommand(
                "INSERT INTO agent_experience.confidence_evidence (evidence_id, experience_id, event_id, kind, source, run_id, " +
                "verification_round_id, reviewer_identity, counted, actor, rule_version, detail, recorded_at, applied_revision, " +
                "applied_status, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, " +
                "new_supporting_validations, prior_contradictions, new_contradictions) VALUES (gen_random_uuid(), @id, NULL, " +
                "'Supporting', 'Machine', gen_random_uuid(), gen_random_uuid(), NULL, false, 'tests', 'v1', NULL, now(), 1, " +
                "'Validated', 0, 0, 0, 0, 0, 0)"))
            {
                evidence.Parameters.AddWithValue("id", record);
                Assert.Equal(1, await evidence.ExecuteNonQueryAsync());
            }

            var grant = await new PostgresExperienceGrantStore(App).CreateAsync(
                Authorize(tenant),
                new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
                new ExperienceGrantRequest(Guid.NewGuid(), record, owner, recipient, "team-b owns the follow-up", DateTimeOffset.UtcNow.AddHours(1)),
                CancellationToken.None);
            Assert.Equal(ExperienceGrantOutcome.Created, grant.Outcome);

            var audited = new PostgresExperienceRecordStore(
                App, onGrantsUnavailable: null,
                auditing: new ExperienceGrantAuditing(new PostgresExperienceGrantAccessLog(App), _ => { }, ExperienceGrantAuditingMode.Required));
            var read = await audited.GetAsync(Authorize(tenant) with { TeamId = "team-b" }, recipient, record, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);

            var feedback = await new PostgresExperienceReuseFeedbackStore(App).RecordAsync(
                Authorize(tenant),
                new RecordedExperienceReuseFeedback(
                    Guid.NewGuid(), Guid.NewGuid(), owner, TaskVerificationStatus.Verified, ExperienceReuseBenefit.Unknown,
                    ExperienceReuseBenefit.Unknown, ReuseAttributionSource.None, ReviewerIdentity: null, EvaluatorId: null,
                    VerificationRoundId: null, AssessmentId: null, Rationale: null, EvidenceIds: [], AttributedAt: null,
                    new ReuseMeasure("task-success", 1), TrialLabel: null, ColumnTime,
                    [new ExperienceReuseExposure(record, Attributed: false, EvidenceId: null)]),
                CancellationToken.None);
            Assert.Equal(ExperienceReuseFeedbackStoreOutcome.Recorded, feedback.Outcome);
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Owner.DisposeAsync();
        }
    }
}
