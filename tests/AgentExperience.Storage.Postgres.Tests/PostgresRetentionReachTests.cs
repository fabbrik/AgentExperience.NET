using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 5.4 against a real PostgreSQL 16 container: a retention sweep that can reach a scope and
/// everything beneath it (KL-3), and a bounded, authorized retention path for the grant access log
/// (KL-10). Every "beneath" claim is proved against a corpus of stored scopes at every field level,
/// and every cross-scope claim against a neighbour that shares every other field. Each test uses its
/// own random tenant, so tests sharing the container never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRetentionReachTests
{
    private const string Administrator = "retention-administrator";

    /// <summary>A recipient user no owner scope in this file ever uses, so an access row's recipient always differs.</summary>
    private const string ReaderUser = "reader-user";

    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceGrantAccessLog _access;

    public PostgresRetentionReachTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _access = new PostgresExperienceGrantAccessLog(fixture.DataSource);
    }

    // ------------------------------------------------------------------ KL-3: what "beneath" reaches

    /// <summary>
    /// The corpus every level test sweeps: one stored scope per shape the six fields can take under one
    /// project, named by (team, agent, user) with "-" for null.
    /// </summary>
    private static readonly (string Name, string? Team, string? Agent, string? User)[] Corpus =
    [
        ("-/-/-", null, null, null),
        ("t1/-/-", "t1", null, null),
        ("t2/-/-", "t2", null, null),
        ("t1/a1/-", "t1", "a1", null),
        ("t1/a2/-", "t1", "a2", null),
        ("t2/a1/-", "t2", "a1", null),
        ("t1/a1/u1", "t1", "a1", "u1"),
        ("t1/a1/u2", "t1", "a1", "u2"),
        ("t1/-/u1", "t1", null, "u1"),
        ("-/a1/-", null, "a1", null),
        ("-/a1/u1", null, "a1", "u1"),
        ("-/-/u1", null, null, "u1"),
        ("-/-/u2", null, null, "u2"),
        ("T1/-/-", "T1", null, null),
    ];

    /// <summary>
    /// Every field level, and the two gap shapes, against the whole corpus. The expected set is written
    /// out by hand from the definition on <see cref="ScopeMatch"/> -- a null root field matches any
    /// value including null, a set one matches only itself, ordinally -- so the test is the spec, not a
    /// re-derivation of the SQL.
    /// </summary>
    [Theory]
    [InlineData("project", null, null, null,
        "-/-/-,t1/-/-,t2/-/-,t1/a1/-,t1/a2/-,t2/a1/-,t1/a1/u1,t1/a1/u2,t1/-/u1,-/a1/-,-/a1/u1,-/-/u1,-/-/u2,T1/-/-")]
    [InlineData("team", "t1", null, null, "t1/-/-,t1/a1/-,t1/a2/-,t1/a1/u1,t1/a1/u2,t1/-/u1")]
    [InlineData("agent", "t1", "a1", null, "t1/a1/-,t1/a1/u1,t1/a1/u2")]
    [InlineData("user", "t1", "a1", "u1", "t1/a1/u1")]
    [InlineData("agent-without-team", null, "a1", null, "t1/a1/-,t2/a1/-,t1/a1/u1,t1/a1/u2,-/a1/-,-/a1/u1")]
    [InlineData("user-without-team-or-agent", null, null, "u1", "t1/a1/u1,t1/-/u1,-/a1/u1,-/-/u1")]
    [InlineData("team-and-user-without-agent", "t1", null, "u1", "t1/a1/u1,t1/-/u1")]
    [InlineData("case-sensitive-team", "T1", null, null, "T1/-/-")]
    public async Task A_subtree_sweep_reaches_exactly_the_scopes_at_or_beneath_its_root(
        string level,
        string? team,
        string? agent,
        string? user,
        string expected)
    {
        Assert.False(string.IsNullOrEmpty(level));
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var seeded = await SeedCorpusAsync(store, auth, tenant, clock.GetUtcNow().AddDays(-400));

        // A neighbouring tenant holding the identical corpus: it must survive every one of these sweeps.
        var neighbour = NewTenant();
        var foreign = await SeedCorpusAsync(store, Authorize(neighbour), neighbour, clock.GetUtcNow().AddDays(-400));

        var root = new Scope(tenant, "app-1", "project-1", team, agent, user);
        var swept = await store.SweepExpiredAsync(auth, root, Retention, PostgresExperienceRecordStore.MaxSweepBatchSize, ScopeMatch.Subtree, CancellationToken.None);

        var expectedNames = expected.Split(',').ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ExperienceStoreOutcome.Deleted, swept.Outcome);
        Assert.Equal(expectedNames.Count, swept.DeletedCount);
        Assert.False(swept.MoreRemain);

        foreach (var (name, id) in seeded)
        {
            Assert.True(
                expectedNames.Contains(name) == await IsTombstoneAsync(id),
                $"Root {level}: stored scope {name} was {(expectedNames.Contains(name) ? "not " : string.Empty)}erased.");
        }

        foreach (var id in foreign.Values)
        {
            Assert.False(await IsTombstoneAsync(id));
        }

        // The in-process containment check agrees with the database, field for field.
        foreach (var (name, candidateTeam, candidateAgent, candidateUser) in Corpus)
        {
            var candidate = new Scope(tenant, "app-1", "project-1", candidateTeam, candidateAgent, candidateUser);
            Assert.Equal(expectedNames.Contains(name), PostgresExperienceRecordStore.IsAtOrBeneath(candidate, root, ScopeMatch.Subtree));
        }
    }

    [Fact]
    public async Task The_candidate_page_itself_excludes_older_rows_outside_the_subtree()
    {
        // The in-process containment check stops a sweep loudly if the database ever hands it a candidate
        // outside the root. That check must not be what makes the other tests pass: here many rows OLDER
        // than the in-subtree ones sit in an ancestor, a sibling, another project and another tenant (whose
        // name the root's tenant prefixes), far more than one page. If the SQL predicate let any of them
        // through, they would head every page and the sweep would stop instead of erasing.
        var tenant = NewTenant();
        var prefixed = tenant + "x";
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var ancient = clock.GetUtcNow().AddDays(-900);

        var outside = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            outside.Add(await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1"), ancient.AddMinutes(i)));
            outside.Add(await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", "t2", "a1", "u1"), ancient.AddMinutes(i)));
            outside.Add(await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", "T1", "a1"), ancient.AddMinutes(i)));
            outside.Add(await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-2", "t1", "a1"), ancient.AddMinutes(i)));
            outside.Add(await SeedAtAsync(store, Authorize(prefixed), new Scope(prefixed, "app-1", "project-1", "t1", "a1"), ancient.AddMinutes(i)));
        }

        var inside = new[]
        {
            await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", "t1"), ancient.AddDays(400)),
            await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", "t1", "a1", "u9"), ancient.AddDays(401)),
            await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", "t1", null, "u1"), ancient.AddDays(402)),
        };

        var root = new Scope(tenant, "app-1", "project-1", "t1");
        var first = await store.SweepExpiredAsync(auth, root, Retention, 2, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(2, first.DeletedCount);
        Assert.True(first.MoreRemain);

        var second = await store.SweepExpiredAsync(auth, root, Retention, 2, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(1, second.DeletedCount);
        Assert.False(second.MoreRemain);

        foreach (var id in inside)
        {
            Assert.True(await IsTombstoneAsync(id));
        }

        foreach (var id in outside)
        {
            Assert.False(await IsTombstoneAsync(id));
        }
    }

    [Fact]
    public async Task An_exact_sweep_is_unchanged_and_the_subtree_sweep_of_the_same_root_is_a_superset_of_it()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var seeded = await SeedCorpusAsync(store, auth, tenant, clock.GetUtcNow().AddDays(-400));
        var root = new Scope(tenant, "app-1", "project-1");

        // The default -- the five-argument overload -- still reaches only the root's own records, and
        // still says nothing about the scopes beneath it. That is KL-3's failure mode, kept on purpose
        // for Exact, and the reason Subtree exists.
        var exact = await store.SweepExpiredAsync(auth, root, Retention, 50, CancellationToken.None);
        Assert.Equal(1, exact.DeletedCount);
        Assert.False(exact.MoreRemain);
        Assert.True(await IsTombstoneAsync(seeded["-/-/-"]));
        Assert.False(await IsTombstoneAsync(seeded["t1/-/-"]));

        var explicitExact = await store.SweepExpiredAsync(auth, root, Retention, 50, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal(0, explicitExact.DeletedCount);

        var subtree = await store.SweepExpiredAsync(auth, root, Retention, 50, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(Corpus.Length - 1, subtree.DeletedCount);
        foreach (var id in seeded.Values)
        {
            Assert.True(await IsTombstoneAsync(id));
        }
    }

    [Fact]
    public async Task A_subtree_sweep_never_reaches_another_tenant_application_or_project()
    {
        // The neighbours share every other field, and one tenant's name is a prefix of the other's, so
        // a LIKE or a prefix compare anywhere would be caught here.
        var tenant = NewTenant();
        var prefixed = tenant + "x";
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var old = clock.GetUtcNow().AddDays(-400);

        var mine = await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", "t1", "a1", "u1"), old);
        var otherTenant = await SeedAtAsync(store, Authorize(prefixed), new Scope(prefixed, "app-1", "project-1", "t1", "a1", "u1"), old);
        var otherTenantRoot = await SeedAtAsync(store, Authorize(prefixed), new Scope(prefixed, "app-1", "project-1"), old);
        var otherApplication = await SeedAtAsync(store, auth, new Scope(tenant, "app-2", "project-1", "t1"), old);
        var otherProject = await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-2", "t1"), old);

        var swept = await store.SweepExpiredAsync(auth, new Scope(tenant, "app-1", "project-1"), Retention, 50, ScopeMatch.Subtree, CancellationToken.None);

        Assert.Equal(1, swept.DeletedCount);
        Assert.False(swept.MoreRemain);
        Assert.True(await IsTombstoneAsync(mine));
        Assert.False(await IsTombstoneAsync(otherTenant));
        Assert.False(await IsTombstoneAsync(otherTenantRoot));
        Assert.False(await IsTombstoneAsync(otherApplication));
        Assert.False(await IsTombstoneAsync(otherProject));

        // And an authorization for another tenant is refused before anything is read, even though the
        // root it names is a subtree that would hold records.
        var denied = await store.SweepExpiredAsync(Authorize(prefixed), new Scope(tenant, "app-1", "project-1"), Retention, 50, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
    }

    [Fact]
    public async Task Authorizing_the_root_covers_the_subtree_and_a_narrower_authorization_cannot_widen_to_it()
    {
        var tenant = NewTenant();
        var (store, clock) = Store();
        var old = clock.GetUtcNow().AddDays(-400);
        var full = Authorize(tenant);
        var mine = await SeedAtAsync(store, full, new Scope(tenant, "app-1", "project-1", "t1", "a1"), old);
        var sibling = await SeedAtAsync(store, full, new Scope(tenant, "app-1", "project-1", "t2", "a1"), old);

        // Bounded to team t1: it may not sweep the project root, Exact or Subtree -- Permits refuses a
        // root that leaves the bounded field null -- so a subtree can never be used to widen it.
        var teamOnly = new AuthorizationContext(tenant, "host-principal", ["experience:write"], ColumnTime, TeamId: "t1");
        var projectRoot = new Scope(tenant, "app-1", "project-1");
        Assert.Equal(ExperienceStoreOutcome.Denied, (await store.SweepExpiredAsync(teamOnly, projectRoot, Retention, 50, ScopeMatch.Subtree, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, (await store.SweepExpiredAsync(teamOnly, new Scope(tenant, "app-1", "project-1", "t2"), Retention, 50, ScopeMatch.Subtree, CancellationToken.None)).Outcome);
        Assert.False(await IsTombstoneAsync(mine));
        Assert.False(await IsTombstoneAsync(sibling));

        // Its own team root is permitted, and reaches only that team.
        var swept = await store.SweepExpiredAsync(teamOnly, new Scope(tenant, "app-1", "project-1", "t1"), Retention, 50, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(1, swept.DeletedCount);
        Assert.True(await IsTombstoneAsync(mine));
        Assert.False(await IsTombstoneAsync(sibling));

        // The lemma, stated over the corpus: whatever permits the root permits everything beneath it.
        foreach (var bound in new AuthorizationContext[] { full, teamOnly, teamOnly with { AgentId = "a1" }, full with { UserId = "u1" } })
        {
            foreach (var (_, rootTeam, rootAgent, rootUser) in Corpus)
            {
                var root = new Scope(tenant, "app-1", "project-1", rootTeam, rootAgent, rootUser);
                if (!bound.Permits(root))
                {
                    continue;
                }

                foreach (var (_, team, agent, user) in Corpus)
                {
                    var candidate = new Scope(tenant, "app-1", "project-1", team, agent, user);
                    if (PostgresExperienceRecordStore.IsAtOrBeneath(candidate, root, ScopeMatch.Subtree))
                    {
                        Assert.True(bound.Permits(candidate), $"{bound} permits {root} but not {candidate} beneath it.");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("", null, null, "Scope.TeamId")]
    [InlineData("  ", null, null, "Scope.TeamId")]
    [InlineData(null, "", null, "Scope.AgentId")]
    [InlineData(null, null, "\t", "Scope.UserId")]
    public async Task A_blank_lower_field_is_Invalid_rather_than_read_as_any(string? team, string? agent, string? user, string path)
    {
        // Only null is "any". A blank field is the shape a mis-mapped host value takes, and reading it
        // as a wildcard would silently widen a destructive operation, so it is refused -- offline.
        var tenant = NewTenant();
        var offline = new PostgresExperienceRecordStore(Unreachable());
        var root = new Scope(tenant, "app-1", "project-1", team, agent, user);

        var swept = await offline.SweepExpiredAsync(Authorize(tenant), root, Retention, 10, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, swept.Outcome);
        Assert.Equal(path, Assert.Single(swept.Errors).Path);

        var purged = await new PostgresExperienceGrantAccessLog(Unreachable()).PurgeOlderThanAsync(
            Authorize(tenant), Admin(), root, DateTimeOffset.UtcNow.AddDays(-400), ScopeMatch.Subtree, 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, purged.Outcome);
        Assert.Equal(path.Replace("Scope.", "RecordScope.", StringComparison.Ordinal), Assert.Single(purged.Errors).Path);
    }

    [Fact]
    public async Task An_undefined_scope_match_is_Invalid_before_any_connection_opens()
    {
        var tenant = NewTenant();
        var root = new Scope(tenant, "app-1", "project-1");

        var swept = await new PostgresExperienceRecordStore(Unreachable())
            .SweepExpiredAsync(Authorize(tenant), root, Retention, 10, (ScopeMatch)7, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, swept.Outcome);
        Assert.Equal("Match", Assert.Single(swept.Errors).Path);

        var purged = await new PostgresExperienceGrantAccessLog(Unreachable())
            .PurgeOlderThanAsync(Authorize(tenant), Admin(), root, DateTimeOffset.UtcNow.AddDays(-400), (ScopeMatch)(-1), 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, purged.Outcome);
        Assert.Equal("Match", Assert.Single(purged.Errors).Path);

        // And a subtree sweep outside the authorization is Denied without a connection, like every other.
        var denied = await new PostgresExperienceRecordStore(Unreachable())
            .SweepExpiredAsync(Authorize(NewTenant()), root, Retention, 10, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
    }

    [Fact]
    public async Task A_subtree_sweep_is_bounded_oldest_first_across_leaves_and_says_truthfully_whether_more_remain()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var now = clock.GetUtcNow();

        // Five records in five different leaves, interleaved in age, plus one too young to go.
        var ages = new (Scope Scope, int DaysOld)[]
        {
            (new Scope(tenant, "app-1", "project-1", "t1"), 500),
            (new Scope(tenant, "app-1", "project-1", null, null, "u1"), 400),
            (new Scope(tenant, "app-1", "project-1"), 300),
            (new Scope(tenant, "app-1", "project-1", "t2", "a1", "u2"), 200),
            (new Scope(tenant, "app-1", "project-1", null, "a9"), 100),
            (new Scope(tenant, "app-1", "project-1", "t1", "a1"), 1),
        };

        var ids = new List<Guid>();
        foreach (var (scope, daysOld) in ages)
        {
            ids.Add(await SeedAtAsync(store, auth, scope, now.AddDays(-daysOld)));
        }

        var root = new Scope(tenant, "app-1", "project-1");

        var first = await store.SweepExpiredAsync(auth, root, Retention, 2, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(2, first.DeletedCount);
        Assert.True(first.MoreRemain);
        Assert.True(await IsTombstoneAsync(ids[0]));
        Assert.True(await IsTombstoneAsync(ids[1]));
        Assert.False(await IsTombstoneAsync(ids[2]));

        var second = await store.SweepExpiredAsync(auth, root, Retention, 2, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(2, second.DeletedCount);
        Assert.True(second.MoreRemain);

        // Exactly one left past the cutoff: this batch takes it, and says nothing remains.
        var third = await store.SweepExpiredAsync(auth, root, Retention, 2, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(1, third.DeletedCount);
        Assert.False(third.MoreRemain);
        Assert.False(await IsTombstoneAsync(ids[5]));

        var fourth = await store.SweepExpiredAsync(auth, root, Retention, 2, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(0, fourth.DeletedCount);
        Assert.False(fourth.MoreRemain);
    }

    // ------------------------------------------------------------------ KL-3: concurrency

    [Fact]
    public async Task Two_racing_subtree_sweeps_erase_every_record_once_and_their_counts_sum_to_exactly_that()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var old = clock.GetUtcNow().AddDays(-400);

        var ids = new List<Guid>();
        for (var i = 0; i < 24; i++)
        {
            var scope = (i % 4) switch
            {
                0 => new Scope(tenant, "app-1", "project-1"),
                1 => new Scope(tenant, "app-1", "project-1", $"t{i % 3}"),
                2 => new Scope(tenant, "app-1", "project-1", $"t{i % 3}", $"a{i % 2}"),
                _ => new Scope(tenant, "app-1", "project-1", null, null, $"u{i % 5}"),
            };
            ids.Add(await SeedAtAsync(store, auth, scope, old.AddMinutes(i)));
        }

        var root = new Scope(tenant, "app-1", "project-1");

        // Both read the same page, then race record by record. The loser of each race sees an already-
        // erased tombstone, which it must not count -- before 5.4 both counted it.
        var results = await Task.WhenAll(
            store.SweepExpiredAsync(auth, root, Retention, 50, ScopeMatch.Subtree, CancellationToken.None),
            store.SweepExpiredAsync(auth, root, Retention, 50, ScopeMatch.Subtree, CancellationToken.None),
            store.SweepExpiredAsync(auth, new Scope(tenant, "app-1", "project-1", "t1"), Retention, 50, ScopeMatch.Subtree, CancellationToken.None));

        Assert.All(results, result => Assert.Equal(ExperienceStoreOutcome.Deleted, result.Outcome));
        Assert.Equal(ids.Count, results.Sum(result => result.DeletedCount));
        foreach (var id in ids)
        {
            // Every tombstone was made exactly once: one revision forward from 0.
            Assert.True(await IsTombstoneAsync(id));
            Assert.Equal(1L, await RevisionAsync(id));
        }
    }

    [Fact]
    public async Task Two_sweeps_parked_on_the_same_record_count_it_once()
    {
        // The interleaving is forced rather than hoped for: a third connection holds the oldest record's
        // row lock, both sweeps read the same page and park on it, and only then is it released. One of
        // them therefore meets that record as an already-erased tombstone, which before 5.4 it counted.
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var old = clock.GetUtcNow().AddDays(-400);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", i % 2 == 0 ? "t1" : null), old.AddMinutes(i)));
        }

        var root = new Scope(tenant, "app-1", "project-1");
        Task<ExperienceRetentionSweepResult>[] sweeps;
        await using (var holder = await _fixture.DataSource.OpenConnectionAsync())
        {
            await using var hold = await holder.BeginTransactionAsync();
            await using (var pin = new NpgsqlCommand(
                "SELECT revision FROM agent_experience.experience_records WHERE experience_id = @id FOR UPDATE", holder, hold))
            {
                pin.Parameters.Add(new NpgsqlParameter<Guid>("id", ids[0]));
                Assert.NotNull(await pin.ExecuteScalarAsync());
            }

            sweeps =
            [
                store.SweepExpiredAsync(auth, root, Retention, 50, ScopeMatch.Subtree, CancellationToken.None),
                store.SweepExpiredAsync(auth, root, Retention, 50, ScopeMatch.Subtree, CancellationToken.None),
            ];

            await WaitForParkedErasuresAsync(2);
            await hold.RollbackAsync();
        }

        var results = await Task.WhenAll(sweeps);
        Assert.Equal(ids.Count, results.Sum(result => result.DeletedCount));
        foreach (var id in ids)
        {
            Assert.True(await IsTombstoneAsync(id));
            Assert.Equal(1L, await RevisionAsync(id));
        }
    }

    [Fact]
    public async Task A_subtree_sweep_racing_the_access_purge_completes_both_and_neither_reaches_the_others_rows()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var old = clock.GetUtcNow().AddDays(-400);
        var root = new Scope(tenant, "app-1", "project-1");

        var records = new List<Guid>();
        var oldAccess = new List<Guid>();
        var freshAccess = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            var scope = new Scope(tenant, "app-1", "project-1", i % 2 == 0 ? null : $"t{i % 3}", null, i % 3 == 0 ? "u1" : null);
            var id = await SeedAtAsync(store, auth, scope, old.AddMinutes(i));
            records.Add(id);
            oldAccess.Add(await SeedAccessAsync(scope, id, DateTimeOffset.UtcNow.AddDays(-200).AddMinutes(i)));
            freshAccess.Add(await SeedAccessAsync(scope, id, DateTimeOffset.UtcNow.AddDays(-2)));
        }

        var sweeping = store.SweepExpiredAsync(auth, root, Retention, 50, ScopeMatch.Subtree, CancellationToken.None);
        var purging = _access.PurgeOlderThanAsync(auth, Admin(), root, DateTimeOffset.UtcNow.AddDays(-90), ScopeMatch.Subtree, 50, CancellationToken.None);
        await Task.WhenAll(sweeping, purging);

        Assert.Equal(records.Count, (await sweeping).DeletedCount);
        Assert.Equal(oldAccess.Count, (await purging).PurgedCount);
        Assert.False((await purging).MoreRemain);

        // The sweep erased the records and left their access rows alone -- 0010's marker admits nothing
        // on that ledger -- and the purge removed only the old ones.
        foreach (var id in records)
        {
            Assert.True(await IsTombstoneAsync(id));
        }

        foreach (var id in oldAccess)
        {
            Assert.False(await AccessExistsAsync(id));
        }

        foreach (var id in freshAccess)
        {
            Assert.True(await AccessExistsAsync(id));
        }
    }

    [Fact]
    public async Task Two_racing_access_purges_remove_every_row_once_and_their_counts_sum_to_exactly_that()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var ids = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            var scope = new Scope(tenant, "app-1", "project-1", $"t{i % 3}", i % 2 == 0 ? "a1" : null);
            ids.Add(await SeedAccessAsync(scope, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-100).AddMinutes(i)));
        }

        var root = new Scope(tenant, "app-1", "project-1");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-60);
        var results = await Task.WhenAll(
            _access.PurgeOlderThanAsync(auth, Admin(), root, cutoff, ScopeMatch.Subtree, 20, CancellationToken.None),
            _access.PurgeOlderThanAsync(auth, Admin(), root, cutoff, ScopeMatch.Subtree, 20, CancellationToken.None));

        // Locked in (recorded_at, access_id) order, so the two neither deadlock nor both count a row.
        var total = results.Sum(result => result.PurgedCount);
        while (total < ids.Count)
        {
            var more = await _access.PurgeOlderThanAsync(auth, Admin(), root, cutoff, ScopeMatch.Subtree, 20, CancellationToken.None);
            Assert.True(more.PurgedCount > 0, "A purge found nothing while rows past the cutoff remained.");
            total += more.PurgedCount;
        }

        Assert.Equal(ids.Count, total);
        foreach (var id in ids)
        {
            Assert.False(await AccessExistsAsync(id));
        }
    }

    // ------------------------------------------------------------------ KL-10: the access-log purge

    [Fact]
    public async Task An_access_purge_removes_rows_older_than_the_cutoff_oldest_first_in_bounded_batches()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = new Scope(tenant, "app-1", "project-1", "t1");
        var now = DateTimeOffset.UtcNow;

        var oldest = await SeedAccessAsync(scope, Guid.NewGuid(), now.AddDays(-300));
        var older = await SeedAccessAsync(scope, Guid.NewGuid(), now.AddDays(-200));
        var old = await SeedAccessAsync(scope, Guid.NewGuid(), now.AddDays(-100));
        var inside = await SeedAccessAsync(scope, Guid.NewGuid(), now.AddDays(-50));
        var fresh = await SeedAccessAsync(scope, Guid.NewGuid(), now.AddDays(-1));
        var cutoff = now.AddDays(-90);

        var first = await _access.PurgeOlderThanAsync(auth, Admin(), scope, cutoff, ScopeMatch.Exact, 2, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, first.Outcome);
        Assert.Equal(2, first.PurgedCount);
        Assert.True(first.MoreRemain);
        Assert.False(await AccessExistsAsync(oldest));
        Assert.False(await AccessExistsAsync(older));
        Assert.True(await AccessExistsAsync(old));

        var second = await _access.PurgeOlderThanAsync(auth, Admin(), scope, cutoff, ScopeMatch.Exact, 2, CancellationToken.None);
        Assert.Equal(1, second.PurgedCount);
        Assert.False(second.MoreRemain);
        Assert.False(await AccessExistsAsync(old));

        Assert.True(await AccessExistsAsync(inside));
        Assert.True(await AccessExistsAsync(fresh));

        // Age is recorded_at, the database's clock, never the reader's occurred_at: a row whose reader
        // claimed a read four hundred days ago but which landed yesterday stays.
        var backdated = await SeedAccessAsync(scope, Guid.NewGuid(), now.AddDays(-1), occurredAt: now.AddDays(-400));
        var third = await _access.PurgeOlderThanAsync(auth, Admin(), scope, cutoff, ScopeMatch.Exact, 10, CancellationToken.None);
        Assert.Equal(0, third.PurgedCount);
        Assert.True(await AccessExistsAsync(backdated));
    }

    /// <summary>
    /// The access purge's "beneath" is a second copy of the rule, in SQL inside <c>0012</c>, so it is held
    /// to the same hand-written expectations as the sweep, root shape for root shape.
    /// </summary>
    [Theory]
    [InlineData("project", null, null, null,
        "-/-/-,t1/-/-,t2/-/-,t1/a1/-,t1/a2/-,t2/a1/-,t1/a1/u1,t1/a1/u2,t1/-/u1,-/a1/-,-/a1/u1,-/-/u1,-/-/u2,T1/-/-")]
    [InlineData("team", "t1", null, null, "t1/-/-,t1/a1/-,t1/a2/-,t1/a1/u1,t1/a1/u2,t1/-/u1")]
    [InlineData("agent", "t1", "a1", null, "t1/a1/-,t1/a1/u1,t1/a1/u2")]
    [InlineData("user", "t1", "a1", "u1", "t1/a1/u1")]
    [InlineData("agent-without-team", null, "a1", null, "t1/a1/-,t2/a1/-,t1/a1/u1,t1/a1/u2,-/a1/-,-/a1/u1")]
    [InlineData("user-without-team-or-agent", null, null, "u1", "t1/a1/u1,t1/-/u1,-/a1/u1,-/-/u1")]
    [InlineData("team-and-user-without-agent", "t1", null, "u1", "t1/a1/u1,t1/-/u1")]
    [InlineData("case-sensitive-team", "T1", null, null, "T1/-/-")]
    public async Task An_access_purge_reaches_exactly_the_owner_scopes_at_or_beneath_its_root(
        string level,
        string? team,
        string? agent,
        string? user,
        string expected)
    {
        var tenant = NewTenant();
        var neighbour = NewTenant();
        var aged = DateTimeOffset.UtcNow.AddDays(-400);
        var rows = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var foreign = new List<Guid>();
        foreach (var (name, rowTeam, rowAgent, rowUser) in Corpus)
        {
            rows[name] = await SeedAccessAsync(new Scope(tenant, "app-1", "project-1", rowTeam, rowAgent, rowUser), Guid.NewGuid(), aged);
            foreign.Add(await SeedAccessAsync(new Scope(neighbour, "app-1", "project-1", rowTeam, rowAgent, rowUser), Guid.NewGuid(), aged));
        }

        var root = new Scope(tenant, "app-1", "project-1", team, agent, user);
        var purged = await _access.PurgeOlderThanAsync(Authorize(tenant), Admin(), root, DateTimeOffset.UtcNow.AddDays(-90), ScopeMatch.Subtree, 500, CancellationToken.None);

        var expectedNames = expected.Split(',').ToHashSet(StringComparer.Ordinal);
        Assert.Equal(ExperienceStoreOutcome.Deleted, purged.Outcome);
        Assert.Equal(expectedNames.Count, purged.PurgedCount);
        Assert.False(purged.MoreRemain);

        foreach (var (name, id) in rows)
        {
            Assert.True(
                expectedNames.Contains(name) != await AccessExistsAsync(id),
                $"Root {level}: access row under {name} was {(expectedNames.Contains(name) ? "not " : string.Empty)}purged.");
        }

        foreach (var id in foreign)
        {
            Assert.True(await AccessExistsAsync(id));
        }
    }

    [Fact]
    public async Task An_access_purge_never_reaches_another_tenant_or_a_scope_outside_its_subtree()
    {
        var tenant = NewTenant();
        var prefixed = tenant + "x";
        var auth = Authorize(tenant);
        var aged = DateTimeOffset.UtcNow.AddDays(-400);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);

        var exactRoot = await SeedAccessAsync(new Scope(tenant, "app-1", "project-1", "t1"), Guid.NewGuid(), aged);
        var beneath = await SeedAccessAsync(new Scope(tenant, "app-1", "project-1", "t1", "a1", "u1"), Guid.NewGuid(), aged);
        var ancestor = await SeedAccessAsync(new Scope(tenant, "app-1", "project-1"), Guid.NewGuid(), aged);
        var sibling = await SeedAccessAsync(new Scope(tenant, "app-1", "project-1", "t2", "a1"), Guid.NewGuid(), aged);
        var otherTenant = await SeedAccessAsync(new Scope(prefixed, "app-1", "project-1", "t1", "a1", "u1"), Guid.NewGuid(), aged);
        var otherProject = await SeedAccessAsync(new Scope(tenant, "app-1", "project-2", "t1"), Guid.NewGuid(), aged);
        var otherApplication = await SeedAccessAsync(new Scope(tenant, "app-2", "project-1", "t1"), Guid.NewGuid(), aged);

        var root = new Scope(tenant, "app-1", "project-1", "t1");

        // Exact first: only the root's own row.
        var exact = await _access.PurgeOlderThanAsync(auth, Admin(), root, cutoff, ScopeMatch.Exact, 50, CancellationToken.None);
        Assert.Equal(1, exact.PurgedCount);
        Assert.False(await AccessExistsAsync(exactRoot));
        Assert.True(await AccessExistsAsync(beneath));

        var subtree = await _access.PurgeOlderThanAsync(auth, Admin(), root, cutoff, ScopeMatch.Subtree, 50, CancellationToken.None);
        Assert.Equal(1, subtree.PurgedCount);
        Assert.False(subtree.MoreRemain);
        Assert.False(await AccessExistsAsync(beneath));

        foreach (var survivor in new[] { ancestor, sibling, otherTenant, otherProject, otherApplication })
        {
            Assert.True(await AccessExistsAsync(survivor));
        }

        // An authorization for the other tenant is refused before anything is read.
        Assert.Equal(
            ExperienceStoreOutcome.Denied,
            (await _access.PurgeOlderThanAsync(Authorize(prefixed), Admin(), root, cutoff, ScopeMatch.Subtree, 50, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task An_access_purge_needs_administrator_authority_and_an_authorization_that_permits_the_root()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var root = new Scope(tenant, "app-1", "project-1");
        var row = await SeedAccessAsync(new Scope(tenant, "app-1", "project-1", "t1"), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-400));
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);

        Assert.Equal(ExperienceStoreOutcome.Denied, (await _access.PurgeOlderThanAsync(auth, null, root, cutoff, ScopeMatch.Subtree, 10, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, (await _access.PurgeOlderThanAsync(auth, new GrantAdministration(" ", DateTimeOffset.UtcNow), root, cutoff, ScopeMatch.Subtree, 10, CancellationToken.None)).Outcome);

        var teamOnly = new AuthorizationContext(tenant, "host-principal", ["experience:write"], ColumnTime, TeamId: "t1");
        Assert.Equal(ExperienceStoreOutcome.Denied, (await _access.PurgeOlderThanAsync(teamOnly, Admin(), root, cutoff, ScopeMatch.Subtree, 10, CancellationToken.None)).Outcome);

        Assert.Equal("BatchSize", Assert.Single((await _access.PurgeOlderThanAsync(auth, Admin(), root, cutoff, ScopeMatch.Subtree, 0, CancellationToken.None)).Errors).Path);
        Assert.Equal("BatchSize", Assert.Single((await _access.PurgeOlderThanAsync(auth, Admin(), root, cutoff, ScopeMatch.Subtree, 501, CancellationToken.None)).Errors).Path);
        Assert.Equal("Cutoff", Assert.Single((await _access.PurgeOlderThanAsync(auth, Admin(), root, default, ScopeMatch.Subtree, 10, CancellationToken.None)).Errors).Path);
        Assert.Equal(
            "Administration.AuthorizedAt",
            Assert.Single((await _access.PurgeOlderThanAsync(auth, new GrantAdministration(Administrator, default), root, cutoff, ScopeMatch.Subtree, 10, CancellationToken.None)).Errors).Path);

        Assert.True(await AccessExistsAsync(row));

        // The team-bounded caller may purge its own team's subtree.
        var own = await _access.PurgeOlderThanAsync(teamOnly, Admin(), new Scope(tenant, "app-1", "project-1", "t1"), cutoff, ScopeMatch.Subtree, 10, CancellationToken.None);
        Assert.Equal(1, own.PurgedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(29)]
    [InlineData(-5)]
    public async Task A_cutoff_inside_the_minimum_retention_is_refused_and_removes_nothing(int daysAgo)
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = new Scope(tenant, "app-1", "project-1");
        var ancient = await SeedAccessAsync(scope, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-400));

        // Refused, not clamped: a clamp would report a clean purge while the rows the host asked about
        // survived. Even the ancient row -- which a clamp would have taken -- is left, because the whole
        // call is refused.
        var refused = await _access.PurgeOlderThanAsync(auth, Admin(), scope, DateTimeOffset.UtcNow.AddDays(-daysAgo), ScopeMatch.Exact, 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, refused.Outcome);
        Assert.Equal("Cutoff", Assert.Single(refused.Errors).Path);
        Assert.Equal(0, refused.PurgedCount);
        Assert.True(await AccessExistsAsync(ancient));

        // Just past the floor is accepted.
        var accepted = await _access.PurgeOlderThanAsync(
            auth, Admin(), scope, DateTimeOffset.UtcNow.AddDays(-PostgresExperienceGrantAccessLog.MinimumRetentionDays).AddMinutes(-1), ScopeMatch.Exact, 10, CancellationToken.None);
        Assert.Equal(1, accepted.PurgedCount);
    }

    // ------------------------------------------------------------------ KL-10: the schema's own guards

    [Fact]
    public async Task The_access_purge_is_out_of_reach_of_a_role_that_was_never_granted_it()
    {
        var tenant = NewTenant();
        var row = await SeedAccessAsync(new Scope(tenant, "app-1", "project-1"), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-400));

        await using var reporterSource = await _fixture.CreateRoleAsync(
            "accessreporter",
            "GRANT USAGE ON SCHEMA agent_experience TO {role}",
            "GRANT SELECT ON ALL TABLES IN SCHEMA agent_experience TO {role}");
        await using var reporter = await reporterSource.OpenConnectionAsync();

        await using var purge = new NpgsqlCommand(
            "SELECT purged FROM agent_experience.purge_grant_access(@tenant, 'app-1', 'project-1', NULL, NULL, NULL, true, now() - interval '100 days', 10)",
            reporter);
        purge.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });

        var denied = await Assert.ThrowsAsync<PostgresException>(() => purge.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.True(await AccessExistsAsync(row));

        // The function's own ACL says the same thing, so a later script cannot quietly re-open it.
        await using var acl = _fixture.DataSource.CreateCommand(
            "SELECT has_function_privilege('public', 'agent_experience.purge_grant_access(text, text, text, text, text, text, boolean, timestamptz, integer)', 'EXECUTE'), " +
            "p.prosecdef, array_to_string(p.proconfig, ',') " +
            "FROM pg_proc p WHERE p.oid = 'agent_experience.purge_grant_access(text, text, text, text, text, text, boolean, timestamptz, integer)'::regprocedure");
        await using var reader = await acl.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Contains("search_path=pg_catalog, agent_experience", reader.GetString(2), StringComparison.Ordinal);
        await reader.CloseAsync();

        // The guard that re-checks the floor compares with <= and -, so it pins its own search_path too:
        // a session cannot shadow those operators to walk a hand-marked delete past the floor.
        await using var guard = _fixture.DataSource.CreateCommand(
            "SELECT array_to_string(proconfig, ',') FROM pg_proc WHERE oid = 'agent_experience.reject_event_log_mutation()'::regprocedure");
        Assert.Contains("search_path=pg_catalog, agent_experience", (string)(await guard.ExecuteScalarAsync())!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_guard_admits_a_hand_marked_delete_only_of_an_old_row_and_only_under_the_access_marker()
    {
        var tenant = NewTenant();
        var scope = new Scope(tenant, "app-1", "project-1");
        var old = await SeedAccessAsync(scope, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-400));
        var fresh = await SeedAccessAsync(scope, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-29));

        // Unmarked: refused, as 0009 promised.
        await AssertRefusedAsync(null, "DELETE FROM agent_experience.experience_grant_access WHERE access_id = @id", old);

        // 0010's record-erasure marker still admits nothing on this ledger.
        await AssertRefusedAsync("agent_experience.purge_authorized", "DELETE FROM agent_experience.experience_grant_access WHERE access_id = @id", old);

        // The access marker never admits an UPDATE, and never a row younger than the floor -- even set
        // by hand, which any session may do (the marker is auditability, not a privilege boundary).
        await AssertRefusedAsync("agent_experience.access_purge_authorized", "UPDATE agent_experience.experience_grant_access SET principal_id = 'someone-else' WHERE access_id = @id", old);
        await AssertRefusedAsync("agent_experience.access_purge_authorized", "DELETE FROM agent_experience.experience_grant_access WHERE access_id = @id", fresh);

        // ...and it admits nothing on the other ledgers: a lifecycle event of this test's own record.
        var auth = Authorize(tenant);
        var (store, _) = Store();
        var record = Minimal(scope);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await store.CommitLifecycleEventAsync(auth, scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None)).Outcome);
        await AssertRefusedAsync(
            "agent_experience.access_purge_authorized",
            "DELETE FROM agent_experience.lifecycle_events WHERE experience_id = @id",
            record.ExperienceId);

        Assert.True(await AccessExistsAsync(old));
        Assert.True(await AccessExistsAsync(fresh));

        // An old row under the access marker is the one exception.
        Assert.Equal(1, await MarkedAsync("agent_experience.access_purge_authorized", "DELETE FROM agent_experience.experience_grant_access WHERE access_id = @id", old));
        Assert.False(await AccessExistsAsync(old));
    }

    [Fact]
    public async Task The_access_marker_does_not_outlive_the_purge_function_and_hand_calls_are_still_bounded()
    {
        var tenant = NewTenant();
        var scope = new Scope(tenant, "app-1", "project-1", "t1");
        var survivor = await SeedAccessAsync(scope, Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-400));

        await using (var many = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grant_access (access_id, grant_id, experience_id, record_revision, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, " +
            "recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, principal_id, correlation_id, " +
            "occurred_at, recorded_at, disclosure) " +
            "SELECT gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 0, @tenant, 'app-1', 'project-1', 't2', NULL, NULL, " +
            "@tenant, 'app-1', 'project-1', 't2', NULL, 'reader-user', 'reader', NULL, now() - interval '400 days', " +
            "now() - interval '400 days' + (g * interval '1 second'), 'LessonOnly' FROM generate_series(1, 510) g"))
        {
            many.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
            Assert.Equal(510, await many.ExecuteNonQueryAsync());
        }

        await using var connection = await _fixture.OwnerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // A NULL limit is 500, never "no limit"; a NULL subtree flag is Exact, so t1's row is untouched.
        await using (var purge = new NpgsqlCommand(
            "SELECT purge_outcome, purged, more_remain FROM agent_experience.purge_grant_access(" +
            "@tenant, 'app-1', 'project-1', 't2', NULL, NULL, NULL, now() - interval '100 days', NULL)",
            connection,
            transaction))
        {
            purge.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
            await using var reader = await purge.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Purged", reader.GetString(0));
            Assert.Equal(500L, reader.GetInt64(1));
            Assert.True(reader.GetBoolean(2));
        }

        // Still inside the purge's own transaction: the marker is gone, and the guard refuses again.
        await using (var afterwards = new NpgsqlCommand(
            "DELETE FROM agent_experience.experience_grant_access WHERE access_id = @id", connection, transaction))
        {
            afterwards.Parameters.Add(new NpgsqlParameter<Guid>("id", survivor));
            var refused = await Assert.ThrowsAsync<PostgresException>(() => afterwards.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        await transaction.RollbackAsync();

        // A NULL cutoff is refused, never read as "no bound".
        await using var nullCutoff = _fixture.DataSource.CreateCommand(
            "SELECT purge_outcome FROM agent_experience.purge_grant_access(@tenant, 'app-1', 'project-1', NULL, NULL, NULL, true, NULL, 10)");
        nullCutoff.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = tenant });
        Assert.Equal("CutoffTooRecent", await nullCutoff.ExecuteScalarAsync());

        // A NULL required field matches nothing, even as a subtree root.
        await using var nullTenant = _fixture.DataSource.CreateCommand(
            "SELECT purged FROM agent_experience.purge_grant_access(NULL, 'app-1', 'project-1', NULL, NULL, NULL, true, now() - interval '100 days', 500)");
        Assert.Equal(0L, await nullTenant.ExecuteScalarAsync());
        Assert.True(await AccessExistsAsync(survivor));
    }

    [Fact]
    public async Task Erasing_a_record_still_keeps_its_access_rows_after_0012()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var (store, clock) = Store();
        var scope = new Scope(tenant, "app-1", "project-1", "t1");
        var id = await SeedAtAsync(store, auth, scope, clock.GetUtcNow().AddDays(-400));
        var access = await SeedAccessAsync(scope, id, DateTimeOffset.UtcNow.AddDays(-400));

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, scope, id, CancellationToken.None)).Outcome);
        Assert.True(await AccessExistsAsync(access));
    }

    // ------------------------------------------------------------------ helpers

    private (PostgresExperienceRecordStore Store, TimeProvider Clock) Store()
    {
        var clock = new FixedClock(ColumnTime);
        return (new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: clock), clock);
    }

    private static GrantAdministration Admin() => new(Administrator, DateTimeOffset.UtcNow);

    private static async Task<Dictionary<string, Guid>> SeedCorpusAsync(
        PostgresExperienceRecordStore store,
        AuthorizationContext auth,
        string tenant,
        DateTimeOffset createdAt)
    {
        var seeded = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var offset = 0;
        foreach (var (name, team, agent, user) in Corpus)
        {
            seeded[name] = await SeedAtAsync(store, auth, new Scope(tenant, "app-1", "project-1", team, agent, user), createdAt.AddMinutes(offset++));
        }

        return seeded;
    }

    private static async Task<Guid> SeedAtAsync(PostgresExperienceRecordStore store, AuthorizationContext auth, Scope scope, DateTimeOffset createdAt)
    {
        var record = Minimal(scope, createdAt: createdAt);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    /// <summary>
    /// An access row written straight into the ledger with a <c>recorded_at</c> the test chooses. The
    /// adapter always stamps the database's clock, so this is the only way to stage a row that is
    /// already past a retention cutoff; INSERT is not guarded, only UPDATE, DELETE and TRUNCATE are.
    /// </summary>
    private async Task<Guid> SeedAccessAsync(Scope owner, Guid experienceId, DateTimeOffset recordedAt, DateTimeOffset? occurredAt = null)
    {
        var accessId = Guid.NewGuid();
        await using var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grant_access (access_id, grant_id, experience_id, record_revision, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, " +
            "recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, principal_id, correlation_id, " +
            "occurred_at, recorded_at, disclosure) VALUES (@access_id, @grant_id, @experience_id, 0, " +
            "@tenant, @application, @project, @team, @agent, @user, @tenant, @application, @project, @team, @agent, @reader, " +
            "'reading-principal', NULL, @occurred_at, @recorded_at, 'LessonOnly')");
        var parameters = command.Parameters;
        parameters.Add(new NpgsqlParameter<Guid>("access_id", accessId));
        parameters.Add(new NpgsqlParameter<Guid>("grant_id", Guid.NewGuid()));
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = owner.TenantId });
        parameters.Add(new NpgsqlParameter<string>("application", NpgsqlDbType.Text) { TypedValue = owner.ApplicationId });
        parameters.Add(new NpgsqlParameter<string>("project", NpgsqlDbType.Text) { TypedValue = owner.ProjectId });
        parameters.Add(new NpgsqlParameter("team", NpgsqlDbType.Text) { Value = (object?)owner.TeamId ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = (object?)owner.AgentId ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter("user", NpgsqlDbType.Text) { Value = (object?)owner.UserId ?? DBNull.Value });
        parameters.Add(new NpgsqlParameter<string>("reader", NpgsqlDbType.Text) { TypedValue = ReaderUser });
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", (occurredAt ?? recordedAt).ToUniversalTime()));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>("recorded_at", recordedAt.ToUniversalTime()));

        Assert.NotEqual(ReaderUser, owner.UserId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        return accessId;
    }

    /// <summary>
    /// Waits until <paramref name="count"/> erasures are parked on a row lock. Every test in this
    /// collection runs serially against its own container, so the only such backends are the test's own.
    /// </summary>
    private async Task WaitForParkedErasuresAsync(int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = _fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%purge_experience_record%'");
            if ((long)(await command.ExecuteScalarAsync())! >= count)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"{count} sweeps never parked on the held row.");
    }

    private async Task<bool> AccessExistsAsync(Guid accessId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.experience_grant_access WHERE access_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", accessId));
        return (long)(await command.ExecuteScalarAsync())! == 1;
    }

    private async Task<bool> IsTombstoneAsync(Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT deleted_at IS NOT NULL FROM agent_experience.experience_records WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> RevisionAsync(Guid experienceId)
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT revision FROM agent_experience.experience_records WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Runs one statement with a marker hand-set (or none), which any session may do, and returns the rows
    /// it touched. As the tables' owner, which holds DELETE: the guard under test is what a writer allowed
    /// to delete still meets. The application role is refused before the guard, by the privilege system
    /// (PostgresApplicationRoleTests).
    /// </summary>
    private async Task<int> MarkedAsync(string? marker, string sql, Guid id)
    {
        await using var connection = await _fixture.OwnerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        if (marker is not null)
        {
            // The GUC name is one of two literals in this file, never input.
            await using var set = new NpgsqlCommand($"SET LOCAL {marker} = 'on'", connection, transaction);
            await set.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));

        try
        {
            var affected = await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return affected;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Asserts the statement, which names an existing row, is refused by an append-only guard.</summary>
    private async Task AssertRefusedAsync(string? marker, string sql, Guid id)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => MarkedAsync(marker, sql, id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
