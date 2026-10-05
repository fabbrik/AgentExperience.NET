using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 17.7: with row-level security on, the text channel searches through <c>agent_experience.search_experience_text</c>,
/// which runs as the owner so the GIN indexes stay usable, and which admits exactly what the read policy admits. Each test
/// builds its own two-role database, so these run in every suite mode.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresTextSearchRowLevelSecurityTests(PostgresFixture fixture)
{
    private const string Administrator = "sharing-administrator";

    private const string Text = "refund lock";

    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    // ---------------------------------------------------------------- same answers

    [Fact]
    public async Task Every_search_answers_exactly_the_same_with_row_level_security_on_as_off()
    {
        await using var world = await WorldAsync("tsrls_same", enable: false);
        var tenant = NewTenant();
        var foreign = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var project = Scope(tenant);
        var store = new PostgresExperienceRecordStore(world.App);

        // Own rows, a deterministic and a model-authored one; a shared row and an unshared sibling in another team;
        // a row shared by a grant that has expired; a project-level row; an erased row; and another tenant's rows.
        var own = await world.SeedAsync(recipient, "refund-ticket-stuck-lock", ReflectionAuthorship.Deterministic);
        var ownWeaker = await world.SeedAsync(recipient, "triage", ReflectionAuthorship.Deterministic);
        var ownModel = await world.SeedAsync(recipient, "refund-lock", ReflectionAuthorship.Model);
        var shared = await world.SeedAsync(owner, "refund-lock-retry", ReflectionAuthorship.Deterministic);
        var sharedModel = await world.SeedAsync(owner, "refund-ticket-lock", ReflectionAuthorship.Model);
        var unshared = await world.SeedAsync(owner, "refund-ticket", ReflectionAuthorship.Deterministic);
        var expired = await world.SeedAsync(owner, "refund-lock-expired", ReflectionAuthorship.Deterministic);
        var projectLevel = await world.SeedAsync(project, "refund-lock-project", ReflectionAuthorship.Deterministic);
        var erased = await world.SeedAsync(recipient, "refund-lock-erased", ReflectionAuthorship.Deterministic);
        await world.SeedAsync(Scope(foreign, team: "team-b"), "refund-ticket-stuck-lock", ReflectionAuthorship.Deterministic);
        await world.SeedAsync(Scope(foreign), "refund-lock", ReflectionAuthorship.Deterministic);

        var grant = await world.GrantAsync(tenant, shared, owner, recipient, ExperienceGrantDisclosure.LessonAndApproach);
        await world.GrantAsync(tenant, sharedModel, owner, recipient, ExperienceGrantDisclosure.LessonOnly);
        await world.SeedExpiredGrantAsync(expired, owner, recipient);
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await store.DeleteAsync(Authorize(tenant), recipient, erased, CancellationToken.None)).Outcome);

        (AuthorizationContext Authorization, ExperienceCandidateQuery Query)[] requests =
        [
            (Authorize(tenant), Query(recipient)),
            (Authorize(tenant), Query(recipient) with { ExcludeModelAuthored = true }),
            (Authorize(tenant) with { TeamId = "team-b" }, Query(recipient)),
            (Authorize(tenant), Query(owner)),
            (Authorize(tenant), Query(project)),
            (Authorize(tenant), Query(recipient) with { Limit = 2 }),
            (Authorize(tenant), Query(recipient) with { MinimumConfidence = 0.8 }),
            (Authorize(tenant), Query(recipient) with { TaskText = "nothing matches this" }),
        ];

        // Row-level security off: the store's own statement, as it always ran.
        Assert.False(await world.RoutesThroughFunctionAsync(tenant));
        var off = new List<string>();
        foreach (var (authorization, query) in requests)
        {
            off.Add(await world.AnswerAsync(authorization, query));
        }

        // On: the function, and the same answers -- rows, order, relevance, shared flags, permitting grants, the
        // disclosure each access row records -- for every request.
        await world.ApplyAsync(enable: true);
        Assert.True(await world.RoutesThroughFunctionAsync(tenant));
        for (var i = 0; i < requests.Length; i++)
        {
            Assert.Equal(off[i], await world.AnswerAsync(requests[i].Authorization, requests[i].Query));
        }

        // The answers are the fixture's, not an empty match: the recipient sees its own rows and the live grant's, ranked.
        var source = new PostgresExperienceCandidateSource(world.App);
        var found = await source.SearchAsync(Authorize(tenant), Query(recipient), CancellationToken.None);
        Assert.Equal(
            new[] { own, ownWeaker, ownModel, shared, sharedModel }.Order(),
            found.Candidates.Select(c => c.Record.ExperienceId).Order());
        var borrowed = Assert.Single(found.Candidates, c => c.Record.ExperienceId == shared);
        Assert.True(borrowed.SharedByGrant);
        Assert.Equal(grant, borrowed.PermittingGrantId);
        Assert.DoesNotContain(found.Candidates, c => c.Record.ExperienceId == unshared || c.Record.ExperienceId == expired
            || c.Record.ExperienceId == projectLevel || c.Record.ExperienceId == erased);

        // And off again: the route follows the privileges call, and the answers stay the same.
        await world.ApplyAsync(enable: false);
        Assert.False(await world.RoutesThroughFunctionAsync(tenant));
        for (var i = 0; i < requests.Length; i++)
        {
            Assert.Equal(off[i], await world.AnswerAsync(requests[i].Authorization, requests[i].Query));
        }
    }

    // ---------------------------------------------------------------- the function's own bounds

    [Fact]
    public async Task Called_directly_the_function_returns_nothing_without_declared_bounds_and_nothing_of_another_tenant()
    {
        await using var world = await WorldAsync("tsrls_bounds");
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        await world.SeedAsync(Scope(tenantA), "refund-lock", ReflectionAuthorship.Deterministic);
        await world.SeedAsync(Scope(tenantB), "refund-lock", ReflectionAuthorship.Deterministic);

        // Declared bounds that contain the scope: the row comes back, so the calls below are not empty by accident.
        Assert.Equal(1L, await world.CallAsync(world.App, Authorize(tenantA), Scope(tenantA)));

        // Nothing declared: nothing, for the application role and for the owner alike.
        Assert.Equal(0L, await world.CallAsync(world.App, declare: null, Scope(tenantA)));
        Assert.Equal(0L, await world.CallAsync(world.Owner, declare: null, Scope(tenantA)));

        // Tenant A's bounds, tenant B's scope: nothing, though B's row matches and the function runs as the owner.
        Assert.Equal(0L, await world.CallAsync(world.App, Authorize(tenantA), Scope(tenantB)));
        Assert.Equal(0L, await world.CallAsync(world.Owner, Authorize(tenantA), Scope(tenantB)));

        // A team bound narrower than the scope asked for: the project's own team-less row is outside it.
        Assert.Equal(0L, await world.CallAsync(world.App, Authorize(tenantA) with { TeamId = "team-a" }, Scope(tenantA)));
    }

    // ---------------------------------------------------------------- the index

    [Fact]
    public async Task With_row_level_security_on_the_search_uses_the_GIN_index_and_the_policies_alone_cannot()
    {
        await using var world = await WorldAsync("tsrls_index");
        var tenant = NewTenant();
        var scope = Scope(tenant);

        // A large tenant: twenty thousand rows that do not match, and three that do.
        await using (var bulk = world.Owner.CreateCommand(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, project_id, " +
            "task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, " +
            "payload_version, payload) SELECT gen_random_uuid(), gen_random_uuid(), @tenant, 'app-1', 'project-1', " +
            "'filler-task-' || i || CASE WHEN i % 6000 = 0 THEN ' zebra' ELSE '' END, 'Validated', 0.5, 0, 0, 0, now(), now(), 1, '{}'::jsonb " +
            "FROM generate_series(1, 20000) i"))
        {
            bulk.Parameters.AddWithValue("tenant", tenant);
            bulk.CommandTimeout = 120;
            Assert.Equal(20000, await bulk.ExecuteNonQueryAsync());
        }

        // And two sealed rows that match only through their sealed vector, so both GIN indexes have work to do.
        await world.SeedSealedWithoutFlagAsync(scope, "zebra sealed one");
        await world.SeedSealedWithoutFlagAsync(scope, "zebra sealed two");

        await using (var analyze = world.Owner.CreateCommand("ANALYZE agent_experience.experience_records"))
        {
            await analyze.ExecuteNonQueryAsync();
        }

        // The function's own statement, as the application role runs it (SET ROLE from a session that loaded
        // auto_explain), with the nested plan reported back as a notice.
        var (rows, plans) = await world.ExplainAsAppAsync(
            fixture, Authorize(tenant), TextSearchFunction.CallSql.Replace("SELECT *", "SELECT count(*)", StringComparison.Ordinal), scope, "zebra");
        Assert.Equal(5L, rows);
        var nested = Assert.Single(plans, plan => plan.Contains("Bitmap Index Scan on ix_experience_records_search", StringComparison.Ordinal));
        Assert.Matches(@"Bitmap Index Scan on ix_experience_records_search\s", nested);
        Assert.Matches(@"Bitmap Index Scan on ix_experience_records_search_sealed\s", nested);
        Assert.Matches(@"Index Cond: \(search_vector @@ ", nested);
        Assert.Matches(@"Index Cond: \(search_vector_sealed @@ ", nested);

        // The store's own statement, behind the policies: @@ is not leakproof, so the barrier keeps it off the index.
        var (direct, directPlans) = await world.ExplainAsAppAsync(
            fixture, Authorize(tenant), "SELECT count(*) FROM (" + StoreStatement + ") q", scope, "zebra");
        Assert.Equal(5L, direct);
        Assert.Contains(directPlans, plan => plan.Contains("experience_records", StringComparison.Ordinal));
        Assert.DoesNotContain(directPlans, plan => plan.Contains("Index Scan on ix_experience_records_search", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the privileges call

    [Fact]
    public async Task Enabling_refuses_a_text_search_function_altered_by_hand_and_changes_nothing()
    {
        await using var world = await WorldAsync("tsrls_tamper", enable: false);
        var canonical = await ScalarAsync<string>(world.Owner, "SELECT pg_get_functiondef(to_regprocedure(@signature))", ("signature", TextSearchFunction.Signature));

        // An owner drops the bounds check from the body.
        await ExecuteAsync(world.Owner, canonical.Replace(
            "IF pg_catalog.current_setting('agent_experience.auth_set', true) IS DISTINCT FROM 'on' THEN",
            "IF false THEN",
            StringComparison.Ordinal));
        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true));
        Assert.Contains("search_experience_text", refused.Message, StringComparison.Ordinal);
        Assert.False(await RowSecurityOnAsync(world.Owner));
        Assert.False(await ScalarAsync<bool>(world.Owner, $"SELECT has_function_privilege('{world.AppRole}', to_regprocedure(@signature), 'EXECUTE')", ("signature", TextSearchFunction.Signature)));

        // The canonical body again, but run as its caller: refused too.
        await ExecuteAsync(world.Owner, canonical);
        await ExecuteAsync(world.Owner, $"ALTER FUNCTION {TextSearchFunction.Signature} SECURITY INVOKER");
        Assert.Contains("search_experience_text", (await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true))).Message, StringComparison.Ordinal);

        // A widened search_path: refused.
        await ExecuteAsync(world.Owner, canonical);
        await ExecuteAsync(world.Owner, $"ALTER FUNCTION {TextSearchFunction.Signature} SET search_path = public, pg_catalog, pg_temp");
        Assert.Contains("search_experience_text", (await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true))).Message, StringComparison.Ordinal);
        Assert.False(await RowSecurityOnAsync(world.Owner));

        // Put back exactly as 0024 wrote it, it is accepted and granted.
        await ExecuteAsync(world.Owner, TextSearchFunction.Ddl);
        await world.ApplyAsync(enable: true);
        Assert.True(await RowSecurityOnAsync(world.Owner));
        Assert.True(await ScalarAsync<bool>(world.Owner, $"SELECT has_function_privilege('{world.AppRole}', to_regprocedure(@signature), 'EXECUTE')", ("signature", TextSearchFunction.Signature)));
    }

    [Fact]
    public async Task The_function_is_granted_only_while_row_level_security_is_on_and_a_wider_grant_is_refused()
    {
        await using var world = await WorldAsync("tsrls_grant", enable: false);
        const string Executable = "SELECT has_function_privilege(@role, to_regprocedure(@signature), 'EXECUTE')";
        Assert.False(await ScalarAsync<bool>(world.Owner, Executable, ("role", world.AppRole), ("signature", TextSearchFunction.Signature)));

        await world.ApplyAsync(enable: true);
        Assert.True(await ScalarAsync<bool>(world.Owner, Executable, ("role", world.AppRole), ("signature", TextSearchFunction.Signature)));

        await world.ApplyAsync(enable: false);
        Assert.False(await ScalarAsync<bool>(world.Owner, Executable, ("role", world.AppRole), ("signature", TextSearchFunction.Signature)));

        // EXECUTE reaching the role through PUBLIC, with row-level security off, is refused like any unreviewed definer.
        await ExecuteAsync(world.Owner, $"GRANT EXECUTE ON FUNCTION {TextSearchFunction.Signature} TO PUBLIC");
        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: false));
        Assert.Contains("search_experience_text", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_cached_before_the_function_was_revoked_falls_back_to_the_store_statement()
    {
        await using var world = await WorldAsync("tsrls_stale");
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await world.SeedAsync(scope, "refund-lock", ReflectionAuthorship.Deterministic);
        var source = new PostgresExperienceCandidateSource(world.App);
        TextSearchRoute.Invalidate(world.App);
        Assert.Equal(id, Assert.Single((await source.SearchAsync(Authorize(tenant), Query(scope), CancellationToken.None)).Candidates).Record.ExperienceId);
        Assert.True(TextSearchRoute.Cached(world.App));

        // Revoked behind the library's back, with no privileges call: the cached route is stale, the call is refused,
        // and the search runs the store's own statement under the policies instead.
        await ExecuteAsync(world.Owner, $"REVOKE EXECUTE ON FUNCTION {TextSearchFunction.Signature} FROM \"{world.AppRole}\"");
        Assert.Equal(id, Assert.Single((await source.SearchAsync(Authorize(tenant), Query(scope), CancellationToken.None)).Candidates).Record.ExperienceId);
        Assert.False(await world.RoutesThroughFunctionAsync(tenant));
    }

    // ---------------------------------------------------------------- the scope the caller asks for

    [Fact]
    public async Task A_scope_outside_the_declared_bounds_returns_nothing_even_where_a_grant_to_it_exists()
    {
        await using var world = await WorldAsync("tsrls_scope");
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var teamB = Scope(tenant, team: "team-b");
        var teamC = Scope(tenant, team: "team-c");
        var record = await world.SeedAsync(owner, "refund-lock", ReflectionAuthorship.Deterministic);
        var toB = await world.GrantAsync(tenant, record, owner, teamB, ExperienceGrantDisclosure.LessonOnly);
        var toC = await world.GrantAsync(tenant, record, owner, teamC, ExperienceGrantDisclosure.LessonAndApproach);
        var boundToB = Authorize(tenant) with { TeamId = "team-b" };
        var call = TextSearchFunction.CallSql;

        // Inside the bounds: team-b's own grant names the record.
        var inside = await world.RawAsync(world.App, boundToB, call, teamB, exclude: false, withGrants: true);
        Assert.Contains($"{record} ", inside, StringComparison.Ordinal);
        Assert.Contains(toB.ToString(), inside, StringComparison.Ordinal);

        // team-b's bounds asking as team-c: the record is admitted through team-b's grant, but the scope asked for is
        // outside the bounds, so nothing comes back -- in particular not team-c's grant, which team-b may not read.
        Assert.Equal(string.Empty, await world.RawAsync(world.App, boundToB, call, teamC, exclude: false, withGrants: true));
        Assert.Equal(string.Empty, await world.RawAsync(world.Owner, boundToB, call, teamC, exclude: false, withGrants: true));
        Assert.Equal(string.Empty, await world.RawAsync(world.App, boundToB, call, teamC, exclude: false, withGrants: false));
        Assert.DoesNotContain(toC.ToString(), inside, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the exact-scope branches and unknown authorship

    [Fact]
    public async Task The_exact_scope_branches_and_an_unknown_authorship_flag_answer_the_same_with_row_level_security_on_as_off()
    {
        await using var world = await WorldAsync("tsrls_exact", enable: false);
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var scope = Scope(tenant, team: "team-c");
        await world.SeedAsync(scope, "refund-lock", ReflectionAuthorship.Deterministic);
        await world.SeedAsync(scope, "refund-ticket-lock", ReflectionAuthorship.Model);
        var unknown = await world.SeedSealedWithoutFlagAsync(scope, "refund lock sealed");
        var shared = await world.SeedAsync(owner, "refund-lock-shared", ReflectionAuthorship.Deterministic);
        await world.GrantAsync(tenant, shared, owner, scope, ExperienceGrantDisclosure.LessonOnly);
        await world.SeedSealedWithoutFlagAsync(Scope(NewTenant(), team: "team-c"), "refund lock foreign");

        var declare = Authorize(tenant);
        var cases = new (bool Exclude, bool WithGrants)[] { (false, true), (true, true), (false, false), (true, false) };
        static string Store(bool exclude, bool withGrants) =>
            (withGrants ? PostgresExperienceCandidateSource.ReadableSearchHead : PostgresExperienceCandidateSource.ExactSearchHead)
            + (exclude ? PostgresExperienceCandidateSource.ExcludingSearchFilters : PostgresExperienceCandidateSource.SearchFilters);

        var off = new List<string>();
        foreach (var (exclude, withGrants) in cases)
        {
            off.Add(await world.RawAsync(world.App, declare, Store(exclude, withGrants), scope, exclude, withGrants));
        }

        // The unknown flag is a candidate when nothing is excluded and left out when model authorship is.
        Assert.Contains(unknown.ToString(), off[0], StringComparison.Ordinal);
        Assert.DoesNotContain(unknown.ToString(), off[1], StringComparison.Ordinal);
        Assert.Contains(shared.ToString(), off[0], StringComparison.Ordinal);
        Assert.DoesNotContain(shared.ToString(), off[2], StringComparison.Ordinal);

        await world.ApplyAsync(enable: true);
        for (var i = 0; i < cases.Length; i++)
        {
            Assert.Equal(off[i], await world.RawAsync(world.App, declare, TextSearchFunction.CallSql, scope, cases[i].Exclude, cases[i].WithGrants));
        }
    }

    // ---------------------------------------------------------------- the candidate source really calls it

    [Fact]
    public async Task The_candidate_source_calls_the_function_once_a_privileges_call_enables_row_level_security()
    {
        await using var world = await WorldAsync("tsrls_route", enable: false);
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await world.SeedAsync(scope, "refund-lock", ReflectionAuthorship.Deterministic);
        var (traced, log) = world.Traced(fixture);
        await using (traced)
        {
            var source = new PostgresExperienceCandidateSource(traced);

            // Off: the route is primed as the store's own statement, and the function is never called.
            Assert.Equal(id, Assert.Single((await source.SearchAsync(Authorize(tenant), Query(scope), CancellationToken.None)).Candidates).Record.ExperienceId);
            Assert.False(TextSearchRoute.Cached(traced));
            Assert.DoesNotContain(log.Entries, e => e.Message.Contains("search_experience_text(", StringComparison.Ordinal));
            var before = log.Entries.Count;

            // The privileges call alone -- no Invalidate -- moves the route, and the next search runs the function.
            await world.ApplyAsync(enable: true);
            Assert.Equal(id, Assert.Single((await source.SearchAsync(Authorize(tenant), Query(scope), CancellationToken.None)).Candidates).Record.ExperienceId);
            Assert.True(TextSearchRoute.Cached(traced));
            Assert.Contains(log.Entries.Skip(before), e => e.Message.Contains("Command execution completed", StringComparison.Ordinal)
                && e.Message.Contains("FROM agent_experience.search_experience_text(", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_cached_route_is_detected_again_after_its_lifetime()
    {
        await using var world = await WorldAsync("tsrls_ttl");
        var tenant = NewTenant();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var previous = TextSearchRoute.Time;
        TextSearchRoute.Time = clock;
        try
        {
            TextSearchRoute.Invalidate(world.App);
            Assert.True(await DetectAsync(world.App, tenant));

            // Row-level security switched off behind this process's back (another process's privileges call, say).
            await ExecuteAsync(world.Owner, "ALTER TABLE agent_experience.experience_records DISABLE ROW LEVEL SECURITY");
            clock.Advance(TextSearchRoute.Lifetime - TimeSpan.FromSeconds(1));
            Assert.True(await DetectAsync(world.App, tenant));

            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.False(await DetectAsync(world.App, tenant));
        }
        finally
        {
            TextSearchRoute.Time = previous;
        }

        static async Task<bool> DetectAsync(NpgsqlDataSource app, string tenant)
        {
            await using var session = await AuthorizedTransaction.OpenAsync(app, Authorize(tenant), CancellationToken.None);
            return await TextSearchRoute.UsesFunctionAsync(app, session, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_route_cached_before_the_function_was_dropped_falls_back_to_the_store_statement()
    {
        await using var world = await WorldAsync("tsrls_drop");
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await world.SeedAsync(scope, "refund-lock", ReflectionAuthorship.Deterministic);
        var source = new PostgresExperienceCandidateSource(world.App);
        TextSearchRoute.Invalidate(world.App);
        Assert.Equal(id, Assert.Single((await source.SearchAsync(Authorize(tenant), Query(scope), CancellationToken.None)).Candidates).Record.ExperienceId);
        Assert.True(TextSearchRoute.Cached(world.App));

        await ExecuteAsync(world.Owner, $"DROP FUNCTION {TextSearchFunction.Signature}");
        Assert.Equal(id, Assert.Single((await source.SearchAsync(Authorize(tenant), Query(scope), CancellationToken.None)).Candidates).Record.ExperienceId);
        Assert.False(TextSearchRoute.Cached(world.App));
    }

    [Fact]
    public async Task An_error_from_inside_a_usable_function_is_reported_not_taken_for_a_missing_function()
    {
        await using var world = await WorldAsync("tsrls_inner");
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await world.SeedAsync(scope, "refund-lock", ReflectionAuthorship.Deterministic);
        var source = new PostgresExperienceCandidateSource(world.App);

        // A helper the function calls is gone: 42883 from inside the body, while the function itself is still usable.
        await ExecuteAsync(world.Owner, "ALTER FUNCTION agent_experience.rls_scope_admits(text, text, text, text, text, text) RENAME TO rls_scope_admits_gone");
        await Assert.ThrowsAsync<ExperienceStoreException>(() => source.SearchAsync(Authorize(tenant), Query(scope), CancellationToken.None));
        Assert.True(TextSearchRoute.Cached(world.App));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("volatile")]
    [InlineData("leakproof")]
    public async Task Enabling_refuses_a_text_search_function_whose_owner_volatility_or_leakproofness_was_changed(string change)
    {
        await using var world = await WorldAsync("tsrls_attrs", enable: false);
        var other = await fixture.CreateLoginRoleAsync("tsrls_other");
        var sql = change switch
        {
            "owner" => $"ALTER FUNCTION {TextSearchFunction.Signature} OWNER TO \"{other}\"",
            "volatile" => $"ALTER FUNCTION {TextSearchFunction.Signature} VOLATILE",
            _ => $"ALTER FUNCTION {TextSearchFunction.Signature} LEAKPROOF",
        };
        await fixture.ExecuteAsSuperuserAsync(sql, world.Database);

        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(() => world.ApplyAsync(enable: true));
        Assert.Contains("search_experience_text", refused.Message, StringComparison.Ordinal);
        Assert.False(await RowSecurityOnAsync(world.Owner));
    }

    // ---------------------------------------------------------------- helpers

    private static ExperienceCandidateQuery Query(Scope scope) => new(scope, Text, Eligible, 0d);

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>The store's own readable search statement, with its parameters left for <see cref="RlsWorld.ExplainAsAppAsync"/> to bind.</summary>
    private const string StoreStatement =
        PostgresExperienceCandidateSource.ReadableSearchHead + PostgresExperienceCandidateSource.SearchFilters;

    private static async Task<bool> RowSecurityOnAsync(NpgsqlDataSource owner) =>
        await ScalarAsync<bool>(owner, "SELECT relrowsecurity FROM pg_class WHERE oid = 'agent_experience.experience_records'::regclass");

    private static async Task ExecuteAsync(NpgsqlDataSource source, string sql)
    {
        await using var command = source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource source, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static void AddSearchParameters(NpgsqlParameterCollection parameters, Scope scope, string text, bool exclude = false, bool withGrants = true)
    {
        PostgresExperienceRecordStore.AddScopeParameters(parameters, scope);
        parameters.Add(new NpgsqlParameter<string>("task_text", NpgsqlDbType.Text) { TypedValue = text });
        parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = [.. Eligible.Select(s => s.ToString())] });
        parameters.Add(new NpgsqlParameter<double>("min_confidence", 0d));
        parameters.Add(new NpgsqlParameter<int>("limit", ExperienceCandidateQuery.MaxLimit));
        parameters.Add(new NpgsqlParameter<bool>("exclude_model_authored", exclude));
        parameters.Add(new NpgsqlParameter<bool>("with_grants", withGrants));
    }

    private async Task<RlsWorld> WorldAsync(string purpose, bool enable = true)
    {
        var owner = await fixture.CreateLoginRoleAsync(purpose + "_o");
        var app = await fixture.CreateLoginRoleAsync(purpose + "_a");
        var database = await fixture.CreateDatabaseNameAsync(purpose, owner);
        await fixture.ExecuteAsSuperuserAsync(PostgresFixture.OwnerParameterGrant(owner));

        var ownerSource = NpgsqlDataSource.Create(fixture.ConnectionString(database, owner));
        await ExperienceSchemaMigrator.MigrateAsync(ownerSource, CancellationToken.None);

        var world = new RlsWorld(ownerSource, NpgsqlDataSource.Create(fixture.ConnectionString(database, app)), app, database, owner);
        await world.ApplyAsync(enable);
        return world;
    }

    private sealed record RlsWorld(NpgsqlDataSource Owner, NpgsqlDataSource App, string AppRole, string Database, string OwnerRole) : IAsyncDisposable
    {
        public Task ApplyAsync(bool enable) =>
            ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync(
                Owner,
                new ExperienceApplicationRoleOptions(AppRole) { AllowErasure = true, EnableRowLevelSecurity = enable },
                CancellationToken.None);

        /// <summary>A validated record with a reflection, created through the store as the application role.</summary>
        public async Task<Guid> SeedAsync(Scope scope, string taskId, ReflectionAuthorship authorship)
        {
            var runId = Guid.NewGuid();
            var record = Minimal(scope, status: ExperienceStatus.Validated) with
            {
                SourceRunId = runId,
                TaskId = taskId,
                TaskSummary = "A refund ticket stuck on a lock",
                ReuseConfidence = taskId.Length % 2 == 0 ? 0.75 : 0.9,
                Reflection = new Reflection(
                    Guid.NewGuid(), runId, "Check the lock table before retrying.", [], [], [], [], null, [],
                    TaskVerificationStatus.Verified, 1, "v1", "tests", ColumnTime) { Authorship = authorship },
            };

            var created = await new PostgresExperienceRecordStore(App).CreateAsync(Authorize(scope.TenantId), record, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
            return record.ExperienceId;
        }

        /// <summary>A live grant through the grant store, as the application role; returns its ID.</summary>
        public async Task<Guid> GrantAsync(string tenant, Guid record, Scope owner, Scope recipient, ExperienceGrantDisclosure disclosure)
        {
            var id = Guid.NewGuid();
            var created = await new PostgresExperienceGrantStore(App).CreateAsync(
                Authorize(tenant),
                new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
                new ExperienceGrantRequest(id, record, owner, recipient, "the recipient owns the follow-up", DateTimeOffset.UtcNow.AddHours(1), disclosure),
                CancellationToken.None);
            Assert.Equal(ExperienceGrantOutcome.Created, created.Outcome);
            return id;
        }

        /// <summary>A grant that expired an hour ago, written by hand as the owner.</summary>
        public async Task SeedExpiredGrantAsync(Guid record, Scope owner, Scope recipient)
        {
            await using var command = Owner.CreateCommand(
                "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, tenant_id, application_id, project_id, " +
                "team_id, recipient_tenant_id, recipient_application_id, recipient_project_id, recipient_team_id, reason, " +
                "administrator_principal_id, issued_at, expires_at) VALUES (gen_random_uuid(), @id, @tenant, @app, @project, @team, " +
                "@tenant, @app, @project, @r_team, 'written by hand', 'administrator', now() - interval '2 hours', now() - interval '1 hour')");
            command.Parameters.AddWithValue("id", record);
            command.Parameters.AddWithValue("tenant", owner.TenantId);
            command.Parameters.AddWithValue("app", owner.ApplicationId);
            command.Parameters.AddWithValue("project", owner.ProjectId);
            command.Parameters.AddWithValue("team", owner.TeamId!);
            command.Parameters.AddWithValue("r_team", recipient.TeamId!);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        /// <summary>Whether a candidate source on <see cref="App"/> would call the function now, detected afresh.</summary>
        public async Task<bool> RoutesThroughFunctionAsync(string tenant)
        {
            TextSearchRoute.Invalidate(App);
            await using var session = await AuthorizedTransaction.OpenAsync(App, Authorize(tenant), CancellationToken.None);
            return await TextSearchRoute.UsesFunctionAsync(App, session, CancellationToken.None);
        }

        /// <summary>
        /// One audited search, as text: each candidate's ID, relevance, shared flag and permitting grant, in order, then the
        /// access rows it appended (record, grant, disclosure), read back as the owner.
        /// </summary>
        public async Task<string> AnswerAsync(AuthorizationContext authorization, ExperienceCandidateQuery query)
        {
            var correlation = Guid.NewGuid().ToString("N");
            var source = new PostgresExperienceCandidateSource(
                App,
                onGrantsUnavailable: null,
                auditing: new ExperienceGrantAuditing(new PostgresExperienceGrantAccessLog(App), _ => { }, ExperienceGrantAuditingMode.Required));
            var result = await source.SearchAsync(authorization, query with { CorrelationId = correlation }, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);

            var lines = result.Candidates
                .Select(c => $"{c.Record.ExperienceId} {c.Relevance:R} {c.SharedByGrant} {c.PermittingGrantId}")
                .ToList();

            await using var command = Owner.CreateCommand(
                "SELECT experience_id, grant_id, disclosure FROM agent_experience.experience_grant_access " +
                "WHERE correlation_id = @correlation ORDER BY experience_id, grant_id");
            command.Parameters.AddWithValue("correlation", correlation);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add($"access {reader.GetGuid(0)} {reader.GetGuid(1)} {(reader.IsDBNull(2) ? "-" : reader.GetString(2))}");
            }

            return string.Join('\n', lines);
        }

        /// <summary>
        /// One search's rows as text -- ID, relevance, shared flag, permitting grant and its level -- read straight from
        /// <paramref name="sql"/> (the store's own statement, or the function's call) without decoding a record.
        /// </summary>
        public async Task<string> RawAsync(NpgsqlDataSource source, AuthorizationContext declare, string sql, Scope scope, bool exclude, bool withGrants)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            AddSearchParameters(command.Parameters, scope, Text, exclude, withGrants);
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(string.Join(' ',
                    reader.GetGuid(reader.GetOrdinal("experience_id")),
                    reader.GetFloat(reader.GetOrdinal("relevance")).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetBoolean(reader.GetOrdinal("shared_by_grant")),
                    reader.IsDBNull(reader.GetOrdinal("permitting_grant_id")) ? "-" : reader.GetGuid(reader.GetOrdinal("permitting_grant_id")).ToString(),
                    reader.IsDBNull(reader.GetOrdinal("permitting_grant_disclosure")) ? "-" : reader.GetString(reader.GetOrdinal("permitting_grant_disclosure"))));
            }

            return string.Join('\n', lines);
        }

        /// <summary>A sealed-shaped row written by hand as the owner, its authorship flag unknown (<c>NULL</c>).</summary>
        public async Task<Guid> SeedSealedWithoutFlagAsync(Scope scope, string text)
        {
            var id = Guid.NewGuid();
            await using var command = Owner.CreateCommand(
                "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, project_id, " +
                "team_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, " +
                "payload_version, payload, search_vector_sealed, reflection_model_authored) VALUES (@id, gen_random_uuid(), @tenant, " +
                "@app, @project, @team, '(sealed)', 'Validated', 0.8, 0, 0, 0, now(), now(), 2, " +
                "jsonb_build_object('sealed', 'aexp-sealed:v1:opaque'), to_tsvector('english', @text), NULL)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("tenant", scope.TenantId);
            command.Parameters.AddWithValue("app", scope.ApplicationId);
            command.Parameters.AddWithValue("project", scope.ProjectId);
            command.Parameters.Add(new NpgsqlParameter("team", NpgsqlDbType.Text) { Value = (object?)scope.TeamId ?? DBNull.Value });
            command.Parameters.AddWithValue("text", text);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            return id;
        }

        /// <summary>Calls the function on <paramref name="source"/>, under <paramref name="declare"/> or nothing, and counts its rows.</summary>
        public async Task<long> CallAsync(NpgsqlDataSource source, AuthorizationContext? declare, Scope scope)
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (declare is not null)
            {
                await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
            }

            await using var command = new NpgsqlCommand(
                TextSearchFunction.CallSql.Replace("SELECT *", "SELECT count(*)", StringComparison.Ordinal), connection, transaction);
            AddSearchParameters(command.Parameters, scope, Text);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>
        /// Runs <paramref name="sql"/> as the application role -- a superuser session that loads <c>auto_explain</c>, then
        /// <c>SET ROLE</c>s -- under <paramref name="declare"/>, and returns its scalar and every plan reported, nested ones included.
        /// </summary>
        public async Task<(long Rows, List<string> Plans)> ExplainAsAppAsync(
            PostgresFixture fixture, AuthorizationContext declare, string sql, Scope scope, string text)
        {
            await using var superuser = NpgsqlDataSource.Create(fixture.ConnectionString(Database, username: null));
            await using var connection = await superuser.OpenConnectionAsync();
            var plans = new List<string>();
            connection.Notice += (_, e) => plans.Add(e.Notice.MessageText);
            foreach (var setup in new[]
            {
                "LOAD 'auto_explain'",
                "SET auto_explain.log_min_duration = 0",
                "SET auto_explain.log_nested_statements = on",
                "SET auto_explain.log_level = notice",
                "SET client_min_messages = notice",
                $"SET ROLE \"{AppRole}\"",
            })
            {
                await using var command = new NpgsqlCommand(setup, connection);
                await command.ExecuteNonQueryAsync();
            }

            await using var transaction = await connection.BeginTransactionAsync();
            await ExperienceSessionContext.DeclareAsync(connection, transaction, declare, CancellationToken.None);
            plans.Clear();
            await using var search = new NpgsqlCommand(sql, connection, transaction);
            AddSearchParameters(search.Parameters, scope, text);
            var rows = (long)(await search.ExecuteScalarAsync())!;
            return (rows, plans);
        }

        /// <summary>A data source on the application role whose every executed command Npgsql logs to the returned factory.</summary>
        public (NpgsqlDataSource Source, MigratorLogSilenceTests.CapturingLoggerFactory Log) Traced(PostgresFixture fixture)
        {
            var log = new MigratorLogSilenceTests.CapturingLoggerFactory();
            var builder = new NpgsqlDataSourceBuilder(fixture.ConnectionString(Database, AppRole));
            builder.UseLoggerFactory(log);
            return (builder.Build(), log);
        }

        public async ValueTask DisposeAsync()
        {
            TextSearchRoute.Invalidate(App);
            await App.DisposeAsync();
            await Owner.DisposeAsync();
        }
    }
}
