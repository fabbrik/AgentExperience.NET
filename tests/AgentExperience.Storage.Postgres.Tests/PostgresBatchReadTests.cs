using System.Diagnostics;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 5.6 (KL-1): <see cref="PostgresExperienceRecordStore.GetManyAsync"/> against a real PostgreSQL 16
/// container. The one question every test here asks is whether the batched read can see anything, or
/// audit anything, differently from a loop of <see cref="PostgresExperienceRecordStore.GetAsync(AuthorizationContext, Scope, Guid, ExperienceReadOptions, CancellationToken)"/>
/// calls: scope, grants (and which grant), disclosure, tombstones, status, and the access rows. The
/// per-record path is run through the port's own sequential default,
/// <see cref="ExperienceRecordStoreExtensions.GetManySequentiallyAsync"/>, which is exactly the loop the
/// injection provider ran before this story.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresBatchReadTests(PostgresFixture fixture)
{
    private const string Administrator = "sharing-administrator";

    private readonly PostgresExperienceGrantStore _grants = new(fixture.DataSource);

    private readonly PostgresExperienceGrantAccessLog _log = new(fixture.DataSource);

    // ---------------------------------------------------------------- equivalence

    [Fact]
    public async Task A_mixed_batch_answers_every_position_exactly_as_its_own_single_read_and_writes_the_same_access_rows()
    {
        var world = await MixedWorldAsync();
        var store = Audited(new List<ExperienceGrantAccessFailure>());

        var sequential = await store.GetManySequentiallyAsync(
            Authorize(world.Tenant), world.Reader, world.Ids, new ExperienceReadOptions(CorrelationId: world.Tenant + "-seq"), CancellationToken.None);
        var batched = await store.GetManyAsync(
            Authorize(world.Tenant), world.Reader, world.Ids, new ExperienceReadOptions(CorrelationId: world.Tenant + "-batch"), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, sequential.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, batched.Outcome);
        Assert.Equal(world.Ids.Count, batched.Results.Count);
        AssertSameAnswers(sequential.Results, batched.Results);

        // And the answers are the ones the matrix expects, so the two paths are not merely equally wrong.
        Assert.Equal(world.Expected, batched.Results.Select(Describe));

        // The same access rows, apart from each row's own ID and instants: one per grant-delivered
        // position -- the two shared records, each named twice, and the shared revoked one -- and none for
        // anything else: no row for the expired grant or for the tombstone a grant still names.
        var sequentialRows = await AccessRowsAsync(world.Tenant + "-seq");
        var batchedRows = await AccessRowsAsync(world.Tenant + "-batch");
        Assert.Equal(5, batchedRows.Count);
        Assert.Equal(sequentialRows, batchedRows);
    }

    [Fact]
    public async Task The_owner_of_the_mixed_set_sees_its_own_tombstone_as_Deleted_in_both_paths()
    {
        var world = await MixedWorldAsync();
        var store = Audited(new List<ExperienceGrantAccessFailure>());

        var sequential = await store.GetManySequentiallyAsync(
            Authorize(world.Tenant), world.Owner, world.Ids, new ExperienceReadOptions(), CancellationToken.None);
        var batched = await store.GetManyAsync(
            Authorize(world.Tenant), world.Owner, world.Ids, new ExperienceReadOptions(), CancellationToken.None);

        AssertSameAnswers(sequential.Results, batched.Results);
        Assert.Contains(batched.Results, result => result.Outcome == ExperienceStoreOutcome.Deleted);
    }

    [Fact]
    public async Task A_scope_check_batch_returns_the_same_records_and_writes_no_access_row()
    {
        var world = await MixedWorldAsync();
        var store = Audited(new List<ExperienceGrantAccessFailure>());
        var options = new ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck, world.Tenant + "-check");

        var sequential = await store.GetManySequentiallyAsync(Authorize(world.Tenant), world.Reader, world.Ids, options, CancellationToken.None);
        var batched = await store.GetManyAsync(Authorize(world.Tenant), world.Reader, world.Ids, options, CancellationToken.None);

        AssertSameAnswers(sequential.Results, batched.Results);
        Assert.Empty(await AccessRowsAsync(world.Tenant + "-check"));
    }

    [Fact]
    public async Task Required_auditing_that_cannot_write_drops_the_same_positions_in_both_paths()
    {
        var world = await MixedWorldAsync();

        // No principal: a row that cannot say who read the record is refused, and Required fails closed.
        var anonymous = new AuthorizationContext(world.Tenant, "  ", ["experience:read"], ColumnTime);
        var failures = new List<ExperienceGrantAccessFailure>();
        var strict = new PostgresExperienceRecordStore(
            fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(_log, failures.Add, ExperienceGrantAuditingMode.Required));

        var sequential = await strict.GetManySequentiallyAsync(anonymous, world.Reader, world.Ids, new ExperienceReadOptions(), CancellationToken.None);
        var sequentialFailures = failures.Count;
        var batched = await strict.GetManyAsync(anonymous, world.Reader, world.Ids, new ExperienceReadOptions(), CancellationToken.None);

        AssertSameAnswers(sequential.Results, batched.Results);
        Assert.DoesNotContain(batched.Results, result => result.SharedByGrant);
        Assert.Contains(batched.Results, result => result.Outcome == ExperienceStoreOutcome.Found);

        // The sequential path reports one failure per grant-delivered read; the batch reports its one
        // append, naming every row it could not write.
        Assert.Equal(5, sequentialFailures);
        var batchFailure = Assert.Single(failures.Skip(sequentialFailures));
        Assert.Equal(5, batchFailure.Accesses.Count);
    }

    [Fact]
    public async Task Best_effort_auditing_that_cannot_write_still_delivers_the_same_records()
    {
        var world = await MixedWorldAsync();
        var failures = new List<ExperienceGrantAccessFailure>();
        var lenient = new PostgresExperienceRecordStore(
            fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(new ThrowingAccessLog(), failures.Add));

        var sequential = await lenient.GetManySequentiallyAsync(Authorize(world.Tenant), world.Reader, world.Ids, new ExperienceReadOptions(), CancellationToken.None);
        var batched = await lenient.GetManyAsync(Authorize(world.Tenant), world.Reader, world.Ids, new ExperienceReadOptions(), CancellationToken.None);

        AssertSameAnswers(sequential.Results, batched.Results);
        Assert.Contains(batched.Results, result => result.SharedByGrant);
    }

    [Fact]
    public async Task A_database_without_the_grant_table_narrows_the_batch_exactly_as_it_narrows_a_single_read()
    {
        await using var dataSource = await fixture.CreateDatabaseAsync("batchnogrants");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var reader = Scope(tenant, team: "team-b");
        var seeding = new PostgresExperienceRecordStore(dataSource);
        var mine = await SeedAsync(seeding, reader);
        var theirs = await SeedAsync(seeding, owner);
        var grants = new PostgresExperienceGrantStore(dataSource);
        await GrantAsync(grants, tenant, theirs, owner, reader);

        await using (var drop = dataSource.CreateCommand("DROP TABLE agent_experience.experience_grants CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        var notices = new List<ExperienceGrantSupportNotice>();
        var store = new PostgresExperienceRecordStore(dataSource, notices.Add);
        IReadOnlyList<Guid> ids = [mine, theirs];

        var batched = await store.GetManyAsync(Authorize(tenant), reader, ids, new ExperienceReadOptions(), CancellationToken.None);
        var sequential = await store.GetManySequentiallyAsync(Authorize(tenant), reader, ids, new ExperienceReadOptions(), CancellationToken.None);

        AssertSameAnswers(sequential.Results, batched.Results);
        Assert.Equal([ExperienceStoreOutcome.Found, ExperienceStoreOutcome.NotFound], batched.Results.Select(result => result.Outcome));
        Assert.Single(notices);
    }

    // ---------------------------------------------------------------- request-wide refusals

    [Fact]
    public async Task A_scope_outside_the_authorization_is_denied_and_nothing_is_read()
    {
        var tenant = NewTenant();
        var id = await SeedAsync(new PostgresExperienceRecordStore(fixture.DataSource), Scope(tenant));
        var store = new PostgresExperienceRecordStore(fixture.DataSource);

        var (result, commands) = await CountCommandsAsync(() => store.GetManyAsync(
            Authorize(NewTenant()), Scope(tenant), [id], new ExperienceReadOptions(), CancellationToken.None));

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Results);
        Assert.Equal(0, commands);
    }

    [Fact]
    public async Task A_request_that_is_malformed_as_a_whole_is_invalid_and_an_empty_guid_is_invalid_only_where_it_stands()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(new PostgresExperienceRecordStore(fixture.DataSource), scope);
        var store = new PostgresExperienceRecordStore(fixture.DataSource);

        var tooMany = await store.GetManyAsync(
            Authorize(tenant), scope, Enumerable.Range(0, ExperienceRecordGetManyResult.MaxCount + 1).Select(_ => Guid.NewGuid()).ToArray(),
            new ExperienceReadOptions(), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, tooMany.Outcome);
        Assert.Equal("ExperienceIds", Assert.Single(tooMany.Errors).Path);

        var badScope = await store.GetManyAsync(
            Authorize(tenant), scope with { ApplicationId = " " }, [id], new ExperienceReadOptions(), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, badScope.Outcome);

        // An empty GUID is one malformed position, answered exactly as its own single read is.
        IReadOnlyList<Guid> ids = [id, Guid.Empty];
        var mixed = await store.GetManyAsync(Authorize(tenant), scope, ids, new ExperienceReadOptions(), CancellationToken.None);
        var single = await store.GetAsync(Authorize(tenant), scope, Guid.Empty, new ExperienceReadOptions(), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, mixed.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, mixed.Results[0].Outcome);
        Assert.Equal(ExperienceStoreOutcome.Invalid, mixed.Results[1].Outcome);
        Assert.Equal(single.Errors, mixed.Results[1].Errors);

        var (empty, commands) = await CountCommandsAsync(() => store.GetManyAsync(
            Authorize(tenant), scope, [], new ExperienceReadOptions(), CancellationToken.None));
        Assert.Equal(ExperienceStoreOutcome.Found, empty.Outcome);
        Assert.Empty(empty.Results);
        Assert.Equal(0, commands);
    }

    // ---------------------------------------------------------------- round trips

    [Fact]
    public async Task Eight_records_cost_one_read_and_one_audit_append_where_the_per_record_path_cost_twelve_commands()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var reader = Scope(tenant, team: "team-b");
        var seeding = new PostgresExperienceRecordStore(fixture.DataSource);

        // The default injection limit: 8 records, four the reader's own and four shared with it.
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await SeedAsync(seeding, reader));
            var shared = await SeedAsync(seeding, owner);
            await GrantAsync(_grants, tenant, shared, owner, reader);
            ids.Add(shared);
        }

        var store = Audited(new List<ExperienceGrantAccessFailure>());
        var options = new ExperienceReadOptions(CorrelationId: tenant);

        var (sequential, sequentialCommands) = await CountCommandsAsync(
            () => store.GetManySequentiallyAsync(Authorize(tenant), reader, ids, options, CancellationToken.None));
        var (batched, batchedCommands) = await CountCommandsAsync(
            () => store.GetManyAsync(Authorize(tenant), reader, ids, options, CancellationToken.None));

        AssertSameAnswers(sequential.Results, batched.Results);

        // Before: one SELECT per record and one INSERT per grant-delivered record. After: one of each.
        Assert.Equal(12, sequentialCommands);
        Assert.Equal(2, batchedCommands);

        // With no auditing wired: eight reads become one.
        var unaudited = new PostgresExperienceRecordStore(fixture.DataSource);
        var (_, unauditedSequential) = await CountCommandsAsync(
            () => unaudited.GetManySequentiallyAsync(Authorize(tenant), reader, ids, options, CancellationToken.None));
        var (_, unauditedBatched) = await CountCommandsAsync(
            () => unaudited.GetManyAsync(Authorize(tenant), reader, ids, options, CancellationToken.None));
        Assert.Equal(8, unauditedSequential);
        Assert.Equal(1, unauditedBatched);
    }

    [Fact]
    public void The_batched_statement_is_the_single_read_with_only_the_id_match_widened()
    {
        // Structural, so a later edit to one statement cannot silently leave the other behind: the single
        // read and the batch are the same text apart from the ID match, in both the grant-aware form and the
        // exact-scope fallback.
        const string One = "WHERE r.experience_id = @experience_id AND ";
        const string Many = "WHERE r.experience_id = ANY(@experience_ids) AND ";
        Assert.Equal(1, CountOf(PostgresExperienceRecordStore.GetSql, One));
        Assert.Equal(PostgresExperienceRecordStore.GetManySql, PostgresExperienceRecordStore.GetSql.Replace(One, Many, StringComparison.Ordinal));
        Assert.Equal(1, CountOf(PostgresExperienceRecordStore.GetExactSql, One));
        Assert.Equal(PostgresExperienceRecordStore.GetManyExactSql, PostgresExperienceRecordStore.GetExactSql.Replace(One, Many, StringComparison.Ordinal));
        Assert.Equal(
            PostgresExperienceRecordStore.GetManySql,
            PostgresExperienceRecordStore.GetSelectFrom + "WHERE r.experience_id = ANY(@experience_ids) AND " + PostgresExperienceRecordStore.GetReadablePredicate);
        Assert.Equal(
            PostgresExperienceRecordStore.GetManyExactSql,
            PostgresExperienceRecordStore.GetExactSelectFrom + "WHERE r.experience_id = ANY(@experience_ids) AND " + PostgresExperienceRecordStore.RecordScopePredicate);
        Assert.Contains(PostgresExperienceRecordStore.PermittingGrantJoin, PostgresExperienceRecordStore.GetSelectFrom, StringComparison.Ordinal);
        Assert.Contains(PostgresExperienceRecordStore.DeletedAtColumn, PostgresExperienceRecordStore.GetSelectFrom, StringComparison.Ordinal);
    }

    private static int CountOf(string text, string fragment) =>
        (text.Length - text.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    // ---------------------------------------------------------------- the mixed world

    /// <summary>
    /// A reader scope and every kind of record the final eligibility check can meet: its own live record,
    /// its own revoked and superseded records, its own erased record, records shared with it at each
    /// disclosure level, one shared under a grant since revoked, one shared and then erased, a sibling's
    /// record with no grant, another tenant's record, an ID that never existed, and a grant-delivered
    /// record named twice. Added in review: its own quarantined record, a shared record that is itself
    /// revoked, a record whose only grant has expired, and a tombstone that a grant written outside this
    /// library still names -- the one case where the tombstone rule, not a missing row, decides.
    /// </summary>
    private async Task<MixedWorld> MixedWorldAsync()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var reader = Scope(tenant, team: "team-b");
        var seeding = new PostgresExperienceRecordStore(fixture.DataSource);

        var own = await SeedAsync(seeding, reader);
        var ownRevoked = await SeedAsync(seeding, reader, ExperienceStatus.Revoked);
        var ownSuperseded = await SeedAsync(seeding, reader, ExperienceStatus.Superseded);
        var ownErased = await SeedAsync(seeding, reader);
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await seeding.DeleteAsync(Authorize(tenant), reader, ownErased, CancellationToken.None)).Outcome);

        var lessonOnly = await SeedAsync(seeding, owner);
        var lessonOnlyGrant = await GrantAsync(_grants, tenant, lessonOnly, owner, reader, ExperienceGrantDisclosure.LessonOnly);

        var withApproach = await SeedAsync(seeding, owner);
        var withApproachGrant = await GrantAsync(_grants, tenant, withApproach, owner, reader, ExperienceGrantDisclosure.LessonAndApproach);

        var grantRevoked = await SeedAsync(seeding, owner);
        var revokedGrant = await GrantAsync(_grants, tenant, grantRevoked, owner, reader);
        Assert.Equal(
            ExperienceGrantOutcome.Revoked,
            (await _grants.RevokeAsync(
                Authorize(tenant),
                new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
                new ExperienceGrantRevocation(revokedGrant.GrantId, owner, "no longer needed"),
                CancellationToken.None)).Outcome);

        var sharedThenErased = await SeedAsync(seeding, owner);
        await GrantAsync(_grants, tenant, sharedThenErased, owner, reader);
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await seeding.DeleteAsync(Authorize(tenant), owner, sharedThenErased, CancellationToken.None)).Outcome);

        var sibling = await SeedAsync(seeding, Scope(tenant, team: "team-c"));

        var ownQuarantined = await SeedAsync(seeding, reader, ExperienceStatus.Quarantined);

        var sharedRevokedStatus = await SeedAsync(seeding, owner, ExperienceStatus.Revoked);
        var sharedRevokedGrant = await GrantAsync(_grants, tenant, sharedRevokedStatus, owner, reader, ExperienceGrantDisclosure.LessonAndApproach);

        var grantExpired = await SeedAsync(seeding, owner);
        await RawGrantAsync(grantExpired, owner, reader, issuedAgo: "2 days", expiresIn: "-1 day");

        var tombstoneStillGranted = await SeedAsync(seeding, owner);
        Assert.Equal(
            ExperienceStoreOutcome.Deleted,
            (await seeding.DeleteAsync(Authorize(tenant), owner, tombstoneStillGranted, CancellationToken.None)).Outcome);
        await RawGrantAsync(tombstoneStillGranted, owner, reader, issuedAgo: "0 seconds", expiresIn: "1 hour");

        var foreignTenant = NewTenant();
        var foreign = await SeedAsync(seeding, Scope(foreignTenant, team: "team-b"));
        var neverExisted = Guid.NewGuid();

        IReadOnlyList<Guid> ids =
        [
            own, ownRevoked, ownSuperseded, ownErased, lessonOnly, withApproach, grantRevoked, sharedThenErased,
            sibling, foreign, neverExisted, lessonOnly, withApproach,
            ownQuarantined, sharedRevokedStatus, grantExpired, tombstoneStillGranted,
        ];

        IReadOnlyList<string> expected =
        [
            "Found own Validated",
            "Found own Revoked",
            "Found own Superseded",
            "Deleted",
            $"Found shared Validated {lessonOnlyGrant.GrantId} LessonOnly",
            $"Found shared Validated {withApproachGrant.GrantId} LessonAndApproach",
            "NotFound",
            "NotFound",
            "NotFound",
            "NotFound",
            "NotFound",
            $"Found shared Validated {lessonOnlyGrant.GrantId} LessonOnly",
            $"Found shared Validated {withApproachGrant.GrantId} LessonAndApproach",
            "Found own Quarantined",
            $"Found shared Revoked {sharedRevokedGrant.GrantId} LessonAndApproach",
            "NotFound",
            "NotFound", // a tombstone reached through a grant says nothing about the erasure
        ];

        return new MixedWorld(tenant, owner, reader, ids, expected);
    }

    /// <summary>
    /// A grant row written as the tables' owner, the way a grant outside this library would be: live or
    /// already expired, and able to name a tombstone, which the library's own erasure never leaves behind.
    /// </summary>
    private async Task RawGrantAsync(Guid experienceId, Scope owner, Scope recipient, string issuedAgo, string expiresIn)
    {
        await using var grant = fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, tenant_id, application_id, " +
            "project_id, team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, " +
            "recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, reason, " +
            "administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) VALUES " +
            "(gen_random_uuid(), @experience_id, @tenant, 'app-1', 'project-1', @team, NULL, NULL, @tenant, 'app-1', " +
            "'project-1', @recipient_team, NULL, NULL, 'written outside this library', @administrator, " +
            "now() - @issued_ago::interval, now() + @expires_in::interval, NULL, NULL)");
        grant.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        grant.Parameters.Add(new NpgsqlParameter<string>("tenant", owner.TenantId));
        grant.Parameters.Add(new NpgsqlParameter<string>("team", owner.TeamId!));
        grant.Parameters.Add(new NpgsqlParameter<string>("recipient_team", recipient.TeamId!));
        grant.Parameters.Add(new NpgsqlParameter<string>("administrator", Administrator));
        grant.Parameters.Add(new NpgsqlParameter<string>("issued_ago", issuedAgo));
        grant.Parameters.Add(new NpgsqlParameter<string>("expires_in", expiresIn));
        Assert.Equal(1, await grant.ExecuteNonQueryAsync());
    }

    private sealed record MixedWorld(string Tenant, Scope Owner, Scope Reader, IReadOnlyList<Guid> Ids, IReadOnlyList<string> Expected);

    // ---------------------------------------------------------------- helpers

    private static string Describe(ExperienceRecordGetResult result) => result.Record is { } record
        ? result.SharedByGrant
            ? $"{result.Outcome} shared {record.Status} {result.PermittingGrantId} {result.GrantDisclosure}"
            : $"{result.Outcome} own {record.Status}"
        : result.Outcome.ToString();

    private static void AssertSameAnswers(IReadOnlyList<ExperienceRecordGetResult> expected, IReadOnlyList<ExperienceRecordGetResult> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Outcome, actual[i].Outcome);
            Assert.Equal(expected[i].SharedByGrant, actual[i].SharedByGrant);
            Assert.Equal(expected[i].PermittingGrantId, actual[i].PermittingGrantId);
            Assert.Equal(expected[i].GrantDisclosure, actual[i].GrantDisclosure);
            Assert.Equal(expected[i].Errors, actual[i].Errors);
            Assert.Equal(
                expected[i].Record is null ? null : Canonical(expected[i].Record!),
                actual[i].Record is null ? null : Canonical(actual[i].Record!));
        }
    }

    private PostgresExperienceRecordStore Audited(List<ExperienceGrantAccessFailure> failures) =>
        new(fixture.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(_log, failures.Add));

    private static DateTimeOffset Micro(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private static async Task<ExperienceGrant> GrantAsync(
        PostgresExperienceGrantStore grants,
        string tenant,
        Guid experienceId,
        Scope owner,
        Scope recipient,
        ExperienceGrantDisclosure disclosure = ExperienceGrantDisclosure.LessonOnly)
    {
        var result = await grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                Guid.NewGuid(), experienceId, owner, recipient, "sibling team owns the follow-up", Micro(DateTimeOffset.UtcNow.AddHours(1)), disclosure),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, result.Outcome);
        return result.Grant!;
    }

    private static async Task<Guid> SeedAsync(
        PostgresExperienceRecordStore store,
        Scope scope,
        ExperienceStatus status = ExperienceStatus.Validated)
    {
        var record = Minimal(scope, status: status) with
        {
            TaskId = "refund-ticket-triage",
            TaskSummary = "Resolve a customer refund",
            ReuseConfidence = 0.75,
        };

        var created = await store.CreateAsync(Authorize(scope.TenantId), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        return record.ExperienceId;
    }

    /// <summary>
    /// Counts the database commands <paramref name="action"/> issues, by the spans Npgsql emits for them,
    /// keeping only the spans under a parent this call starts -- so commands other tests run on the shared
    /// container at the same moment are never counted.
    /// </summary>
    private static async Task<(T Result, int Commands)> CountCommandsAsync<T>(Func<Task<T>> action)
    {
        using var source = new ActivitySource("AgentExperience.Tests.RoundTrips");
        var commands = 0;
        var trace = default(ActivityTraceId);

        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name is "Npgsql" || ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == "Npgsql" && activity.Kind == ActivityKind.Client && activity.TraceId == trace)
                {
                    Interlocked.Increment(ref commands);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        T result;
        using (var parent = source.StartActivity("round-trips"))
        {
            Assert.NotNull(parent);
            trace = parent.TraceId;
            result = await action();
        }

        return (result, Volatile.Read(ref commands));
    }

    private async Task<IReadOnlyList<AccessRow>> AccessRowsAsync(string correlationId)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT grant_id, experience_id, record_revision, tenant_id, team_id, recipient_tenant_id, recipient_team_id, " +
            "principal_id, disclosure " +
            "FROM agent_experience.experience_grant_access WHERE correlation_id = @correlation_id " +
            "ORDER BY experience_id, grant_id");
        command.Parameters.Add(new NpgsqlParameter<string>("correlation_id", correlationId));

        var rows = new List<AccessRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AccessRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return rows;
    }

    /// <summary>Everything an access row says apart from its own ID and instants, which differ between any two reads.</summary>
    private sealed record AccessRow(
        Guid GrantId,
        Guid ExperienceId,
        long RecordRevision,
        string TenantId,
        string? TeamId,
        string RecipientTenantId,
        string? RecipientTeamId,
        string PrincipalId,
        string? Disclosure);

    /// <summary>A ledger that is down.</summary>
    private sealed class ThrowingAccessLog : IExperienceGrantAccessLog
    {
        public Task RecordAsync(IReadOnlyList<ExperienceGrantAccess> accesses, CancellationToken cancellationToken) =>
            Task.FromException(new ExperienceStoreException("the ledger is down."));

        public Task<ExperienceGrantAccessQueryResult> QueryAsync(
            AuthorizationContext authorization,
            ExperienceGrantAccessQuery query,
            CancellationToken cancellationToken) =>
            Task.FromException<ExperienceGrantAccessQueryResult>(new ExperienceStoreException("the ledger is down."));
    }
}
