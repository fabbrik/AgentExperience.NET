using AgentExperience.Core.Lifecycle;
using AgentExperience.Storage.Postgres.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 3.5's grant access trail and grant lifetime bound against a real PostgreSQL 16 container:
/// one access row per record a grant delivered and none for anything else, both auditing modes, the
/// row naming the grant the database actually used, an append-only table that refuses tampering, and
/// an expiry that cannot outrun the configured maximum. Each test uses its own random tenant, so tests
/// sharing the container never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresGrantAccessAuditTests
{
    private const string Administrator = "sharing-administrator";

    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceGrantStore _grants;
    private readonly PostgresExperienceGrantAccessLog _log;
    private readonly PostgresExperienceCandidateSource _source;

    /// <summary>The store with no auditing at all: what every deployment before this story had.</summary>
    private readonly PostgresExperienceRecordStore _unaudited;

    public PostgresGrantAccessAuditTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _grants = new PostgresExperienceGrantStore(fixture.DataSource);
        _log = new PostgresExperienceGrantAccessLog(fixture.DataSource);
        _source = new PostgresExperienceCandidateSource(fixture.DataSource);
        _unaudited = new PostgresExperienceRecordStore(fixture.DataSource);
    }

    // ---------------------------------------------------------------- matrix: granted read

    [Fact]
    public async Task A_read_a_grant_permitted_returns_the_record_and_writes_an_access_row_naming_that_grant()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var store = Audited(failures);

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var read = await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(id, read.Record!.ExperienceId);
        Assert.True(read.SharedByGrant);

        // The read tells the caller WHICH grant permitted it, not merely that one did.
        Assert.Equal(grant.GrantId, read.PermittingGrantId);
        Assert.Empty(failures);

        var row = Assert.Single(await AccessRowsAsync(id));
        Assert.Equal(grant.GrantId, row.GrantId);
        Assert.Equal(id, row.ExperienceId);
        Assert.Equal(owner, row.RecordScope);
        Assert.Equal(recipient, row.RecipientScope);
        Assert.Equal("host-principal", row.PrincipalId);
        Assert.InRange(row.OccurredAt, before, DateTimeOffset.UtcNow.AddSeconds(5));

        // occurred_at is the reader's clock and recorded_at the database's: two facts, not one.
        Assert.NotEqual(default, row.RecordedAt);

        // A second delivery is a second row: the trail counts reads, not recipients.
        await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);
        Assert.Equal(2, (await AccessRowsAsync(id)).Count);
    }

    // ---------------------------------------------------------------- matrix: owner read

    [Fact]
    public async Task An_owner_reading_its_own_record_writes_no_access_row()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);

        // A live grant exists, so the only thing deciding this is that the owner did not need it.
        await GrantAsync(tenant, id, owner, recipient);

        var store = Audited(new List<ExperienceGrantAccessFailure>());
        var read = await store.GetAsync(Authorize(tenant), owner, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.False(read.SharedByGrant);
        Assert.Null(read.PermittingGrantId);
        Assert.Empty(await AccessRowsAsync(id));
    }

    [Fact]
    public async Task A_read_that_finds_nothing_writes_no_access_row()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var stranger = Scope(tenant, team: "team-c");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, Scope(tenant, team: "team-b"));

        var store = Audited(new List<ExperienceGrantAccessFailure>());
        var read = await store.GetAsync(Authorize(tenant), stranger, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.NotFound, read.Outcome);
        Assert.Empty(await AccessRowsAsync(id));
    }

    // ---------------------------------------------------------------- what the row says

    [Fact]
    public async Task An_access_row_names_the_revision_delivered_and_the_work_that_caused_the_read()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var store = Audited(new List<ExperienceGrantAccessFailure>());

        await store.GetAsync(
            Authorize(tenant), recipient, id, new ExperienceReadOptions(CorrelationId: "corr-1"), CancellationToken.None);

        var first = Assert.Single(await AccessRowsAsync(id));
        Assert.Equal(0L, first.RecordRevision);
        Assert.Equal("corr-1", first.CorrelationId);

        // Move the record, then read it again: the trail says which version each read disclosed, which
        // is the one thing a mutable projection can never be asked about after the fact.
        var committed = await new ExperienceLifecycleService(_unaudited).CommitAsync(
            Authorize(tenant),
            new CommitLifecycleTransitionRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: id,
                Scope: owner,
                PriorStatus: ExperienceStatus.Validated,
                CurrentStatus: ExperienceStatus.Reinforced,
                Reason: "still holds",
                Producer: "tests",
                OccurredAt: PayloadTime,
                ExpectedRevision: 0),
            CancellationToken.None);
        Assert.Equal(LifecycleTransitionOutcome.Committed, committed.Outcome);

        await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        var rows = await AccessRowsAsync(id);
        Assert.Equal([0L, 1L], rows.Select(row => row.RecordRevision));

        // No correlation supplied by the second read: recorded as absent, never as a blank string.
        Assert.Null(rows[^1].CorrelationId);
    }

    [Fact]
    public async Task A_read_with_no_principal_is_refused_rather_than_recorded_as_nobody()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var anonymous = new AuthorizationContext(tenant, "   ", ["experience:read"], ColumnTime);
        var failures = new List<ExperienceGrantAccessFailure>();

        // Best effort: the record still comes back, and the host is told the trail has a hole in it.
        var lenient = Audited(failures);
        var read = await lenient.GetAsync(anonymous, recipient, id, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Single(failures);
        Assert.Empty(await AccessRowsAsync(id));

        // Required: a row that cannot say who read the record is not a row, so the read fails closed.
        var strict = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(_log, failures.Add, ExperienceGrantAuditingMode.Required));

        var refused = await strict.GetAsync(anonymous, recipient, id, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, refused.Outcome);
        Assert.Equal(2, failures.Count);
        Assert.Empty(await AccessRowsAsync(id));
    }

    // ---------------------------------------------------------------- a refused read is not a delivery

    [Fact]
    public async Task A_scope_check_read_returns_the_record_and_writes_no_access_row()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var store = Audited(new List<ExperienceGrantAccessFailure>());

        var read = await store.GetAsync(
            Authorize(tenant),
            recipient,
            id,
            new ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck),
            CancellationToken.None);

        // The caller still learns the record is only grant-readable -- which is what it refuses on --
        // and nothing claims it was handed over.
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.True(read.SharedByGrant);
        Assert.Empty(await AccessRowsAsync(id));
    }

    [Fact]
    public async Task A_confidence_submission_refused_for_being_grant_readable_records_no_delivery()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        // Required auditing with a ledger that is down. If the confidence path audited its read, the
        // fail-closed gate would replace its specific refusal with a bare NotFound -- and a rejected
        // write would have been recorded as a disclosure.
        var failures = new List<ExperienceGrantAccessFailure>();
        var strict = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(
                new ThrowingAccessLog(new ExperienceStoreException("down.")),
                failures.Add,
                ExperienceGrantAuditingMode.Required));

        var result = await new ExperienceLifecycleService(strict).ApplyEvidenceAsync(
            Authorize(tenant),
            new ApplyConfidenceEvidenceRequest(
                EventId: Guid.NewGuid(),
                ExperienceId: id,
                Scope: recipient,
                EvidenceId: Guid.NewGuid(),
                Kind: ConfidenceEvidenceKind.Supporting,
                Source: ConfidenceEvidenceSource.Machine,
                RunId: Guid.NewGuid(),
                VerificationRoundId: Guid.NewGuid(),
                Reason: "reuse was observed to be Supporting",
                Producer: "tests",
                OccurredAt: PayloadTime),
            CancellationToken.None);

        Assert.Equal(ConfidenceUpdateOutcome.NotFound, result.Outcome);
        Assert.Contains("sharing grant", result.Reason, StringComparison.Ordinal);
        Assert.Empty(failures);
        Assert.Empty(await AccessRowsAsync(id));
    }

    // ---------------------------------------------------------------- matrix: search match

    [Fact]
    public async Task A_search_that_returns_grant_permitted_records_audits_them_in_one_batch()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");

        var borrowedFirst = await SeedAsync(owner);
        var borrowedSecond = await SeedAsync(owner, taskId: "refund-ticket-escalation");
        var mine = await SeedAsync(recipient, taskId: "refund-ticket-retry");
        var firstGrant = await GrantAsync(tenant, borrowedFirst, owner, recipient);
        var secondGrant = await GrantAsync(tenant, borrowedSecond, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var source = new PostgresExperienceCandidateSource(
            _fixture.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(_log, failures.Add));

        var result = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(
                recipient, "refund", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0d,
                ExperienceCandidateQuery.DefaultLimit, "corr-search"),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Equal(3, result.Candidates.Count);
        Assert.Empty(failures);

        // A candidate carries the record read back IN FULL, so returning one across a scope boundary is
        // a disclosure. Two were disclosed; the reader's own record was not.
        var borrowed = result.Candidates.Where(candidate => candidate.SharedByGrant).ToList();
        Assert.Equal(
            new[] { borrowedFirst, borrowedSecond }.Order(),
            borrowed.Select(candidate => candidate.Record.ExperienceId).Order());
        Assert.All(borrowed, candidate => Assert.NotNull(candidate.PermittingGrantId));

        var rows = (await AccessRowsAsync(borrowedFirst)).Concat(await AccessRowsAsync(borrowedSecond)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new[] { firstGrant.GrantId, secondGrant.GrantId }.Order(),
            rows.Select(row => row.GrantId).Order());
        Assert.All(rows, row =>
        {
            Assert.Equal("corr-search", row.CorrelationId);
            Assert.Equal(recipient, row.RecipientScope);
            Assert.Equal(owner, row.RecordScope);
        });
        Assert.Empty(await AccessRowsAsync(mine));

        // The batch is one statement, so every row lands in the same transaction -- within a few
        // microseconds of the others. (recorded_at is clock_timestamp(), which advances per row, so the
        // stamps are close rather than identical; that is the point of using it.)
        Assert.All(rows, row => Assert.InRange(
            row.RecordedAt, rows[0].RecordedAt.AddSeconds(-1), rows[0].RecordedAt.AddSeconds(1)));
    }

    [Fact]
    public async Task A_search_that_returns_only_the_callers_own_records_writes_nothing()
    {
        var tenant = NewTenant();
        var mine = Scope(tenant, team: "team-b");
        var id = await SeedAsync(mine);

        var failures = new List<ExperienceGrantAccessFailure>();
        var source = new PostgresExperienceCandidateSource(
            _fixture.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(_log, failures.Add));

        var result = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(mine, "refund", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0d),
            CancellationToken.None);

        Assert.Single(result.Candidates);
        Assert.Empty(failures);
        Assert.Empty(await AccessRowsAsync(id));
    }

    [Fact]
    public async Task A_required_search_whose_rows_cannot_be_written_returns_no_candidates()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var borrowed = await SeedAsync(owner);
        await SeedAsync(recipient, taskId: "refund-ticket-retry");
        await GrantAsync(tenant, borrowed, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var source = new PostgresExperienceCandidateSource(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(
                new ThrowingAccessLog(new ExperienceStoreException("the ledger is down.")),
                failures.Add,
                ExperienceGrantAuditingMode.Required));

        var result = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(recipient, "refund", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0d),
            CancellationToken.None);

        // Nothing crosses a scope unrecorded, and a partly-returned page would quietly be a different
        // search than the caller asked for.
        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Empty(result.Candidates);

        var failure = Assert.Single(failures);
        Assert.Equal(ExperienceGrantAuditingMode.Required, failure.Mode);
        Assert.Equal(borrowed, Assert.Single(failure.Accesses).ExperienceId);
    }

    // ---------------------------------------------------------------- matrix: no log wired

    [Fact]
    public async Task With_no_access_log_wired_a_granted_read_behaves_exactly_as_before()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        var read = await _unaudited.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.True(read.SharedByGrant);

        // The grant is still named -- that costs nothing and is the same statement's answer -- but
        // nothing is recorded anywhere.
        Assert.Equal(grant.GrantId, read.PermittingGrantId);
        Assert.Empty(await AccessRowsAsync(id));
    }

    // ---------------------------------------------------------------- matrix: best-effort failure

    [Fact]
    public async Task A_best_effort_audit_failure_still_returns_the_record_and_is_reported()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var boom = new ExperienceStoreException("the ledger is down.");
        var store = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(new ThrowingAccessLog(boom), failures.Add));

        var read = await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(id, read.Record!.ExperienceId);

        var failure = Assert.Single(failures);
        Assert.Same(boom, failure.Failure);
        Assert.Equal(ExperienceGrantAuditingMode.BestEffort, failure.Mode);
        var attempted = Assert.Single(failure.Accesses);
        Assert.Equal(grant.GrantId, attempted.GrantId);
        Assert.Equal(id, attempted.ExperienceId);
        Assert.Equal(recipient, attempted.RecipientScope);
        Assert.Equal("host-principal", attempted.PrincipalId);
    }

    [Fact]
    public async Task A_throwing_failure_callback_does_not_change_what_the_read_returns()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var store = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(
                new ThrowingAccessLog(new ExperienceStoreException("down.")),
                _ => throw new InvalidOperationException("the host's logger is broken too.")));

        var read = await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
    }

    // ---------------------------------------------------------------- matrix: required auditing

    [Fact]
    public async Task Required_auditing_fails_the_read_closed_when_the_access_row_cannot_be_written()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var store = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(
                new ThrowingAccessLog(new ExperienceStoreException("the ledger is down.")),
                failures.Add,
                ExperienceGrantAuditingMode.Required));

        var read = await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        // Nothing comes back, and it is the same answer a record no grant permitted would give, so
        // failing closed tells the caller nothing it would not otherwise have.
        Assert.Equal(ExperienceStoreOutcome.NotFound, read.Outcome);
        Assert.Null(read.Record);
        Assert.False(read.SharedByGrant);
        Assert.Null(read.PermittingGrantId);

        Assert.Equal(ExperienceGrantAuditingMode.Required, Assert.Single(failures).Mode);
    }

    [Fact]
    public async Task Required_auditing_returns_the_record_when_the_access_row_lands()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        var failures = new List<ExperienceGrantAccessFailure>();
        var store = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(_log, failures.Add, ExperienceGrantAuditingMode.Required));

        var read = await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Empty(failures);
        Assert.Equal(grant.GrantId, Assert.Single(await AccessRowsAsync(id)).GrantId);
    }

    [Fact]
    public async Task Required_auditing_leaves_an_owner_read_alone_even_when_the_ledger_is_down()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);

        var failures = new List<ExperienceGrantAccessFailure>();
        var store = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(
                new ThrowingAccessLog(new ExperienceStoreException("down.")),
                failures.Add,
                ExperienceGrantAuditingMode.Required));

        var read = await store.GetAsync(Authorize(tenant), owner, id, CancellationToken.None);

        // A record the caller owns was never a grant's to audit, so requiring auditing cannot take it
        // away: fail-closed applies to deliveries a grant made, and nothing else.
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Empty(failures);
    }

    // ---------------------------------------------------------------- matrix: which grant

    [Fact]
    public async Task Two_competing_grants_over_one_record_are_named_deterministically()
    {
        // Its own database: the second grant needs 0005's active-recipient index out of the way, and
        // that index is a rule every other test in this collection relies on.
        await using var dataSource = await _fixture.CreateDatabaseAsync("grantcompete");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");

        var plain = new PostgresExperienceRecordStore(dataSource);
        var grants = new PostgresExperienceGrantStore(dataSource);
        var record = Minimal(owner, status: ExperienceStatus.Validated) with { ReuseConfidence = 0.75 };
        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await plain.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var created = await grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                Guid.NewGuid(), record.ExperienceId, owner, recipient, "the collaboration",
                Micro(DateTimeOffset.UtcNow.AddHours(1))),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, created.Outcome);

        // Two live grants over the SAME record for the SAME recipient. The store refuses to stack them
        // -- it reports Conflict -- so the second is written the only way 0005's header leaves open: as
        // the tables' owner, with that index dropped.
        var kept = created.Grant!.GrantId;
        var extra = Guid.NewGuid();
        await CopyGrantAsOwnerAsync(dataSource, kept, extra);

        var store = new PostgresExperienceRecordStore(
            dataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(new PostgresExperienceGrantAccessLog(dataSource), _ => { }));

        var expected = kept.CompareTo(extra) < 0 ? kept : extra;

        var first = await store.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None);
        var again = await store.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, first.Outcome);

        // Which one is named is arbitrary; that it is the same one every time is not, and the row in
        // the trail is the one the read told the caller about.
        Assert.Equal(expected, first.PermittingGrantId);
        Assert.Equal(expected, again.PermittingGrantId);

        var rows = await AccessRowsAsync(dataSource, record.ExperienceId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(expected, row.GrantId));
    }

    [Fact]
    public async Task A_revoked_grant_stops_the_read_and_therefore_stops_the_trail()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        var store = Audited(new List<ExperienceGrantAccessFailure>());
        await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        var revoked = await _grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(grant.GrantId, owner, "the collaboration ended"),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Revoked, revoked.Outcome);

        var after = await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, after.Outcome);

        // The one read that happened stays recorded: revocation ends access, it never erases it.
        Assert.Equal(grant.GrantId, Assert.Single(await AccessRowsAsync(id)).GrantId);
    }

    // ---------------------------------------------------------------- matrix: tampered access row

    [Theory]
    [InlineData("UPDATE agent_experience.experience_grant_access SET principal_id = 'someone-else' WHERE experience_id = @experience_id")]
    [InlineData("DELETE FROM agent_experience.experience_grant_access WHERE experience_id = @experience_id")]
    public async Task An_access_row_can_be_neither_rewritten_nor_removed(string sql)
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var store = Audited(new List<ExperienceGrantAccessFailure>());
        await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

        await using var command = _fixture.OwnerDataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", id));
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Single(await AccessRowsAsync(id));
    }

    [Fact]
    public async Task The_access_table_cannot_be_truncated()
    {
        await using var command = _fixture.OwnerDataSource.CreateCommand(
            "TRUNCATE TABLE agent_experience.experience_grant_access");

        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    // ---------------------------------------------------------------- matrix: grant lifetime

    [Fact]
    public async Task An_expiry_beyond_the_configured_maximum_is_refused_with_a_field_path_and_writes_nothing()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);

        var policy = new PostgresExperienceGrantPolicy(TimeSpan.FromDays(7));
        var bounded = new PostgresExperienceGrantStore(_fixture.DataSource, policy);
        var grantId = Guid.NewGuid();

        var result = await bounded.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(grantId, id, owner, recipient, "forever, please", DateTimeOffset.MaxValue),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Invalid, result.Outcome);
        Assert.Equal("ExpiresAt", Assert.Single(result.Errors).Path);
        Assert.Null(result.Grant);
        Assert.Equal(0L, await CountGrantsAsync(grantId));
        Assert.Equal(0L, await CountGrantEventsAsync(grantId));
    }

    [Fact]
    public async Task An_expiry_exactly_at_the_configured_maximum_is_accepted()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);

        // A frozen clock, so "exactly at the maximum" is exact rather than a race with the wall clock --
        // and frozen at the DATABASE's clock, because the statement's own bound is measured from the
        // clock that stamps issued_at, and a container whose clock trails the host's would otherwise
        // refuse an expiry the caller computed as exactly at the maximum.
        var now = await DatabaseNowAsync();
        var policy = new PostgresExperienceGrantPolicy(TimeSpan.FromDays(7));
        var bounded = new PostgresExperienceGrantStore(
            _fixture.DataSource, policy, new FrozenClock(now));

        var result = await bounded.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(Guid.NewGuid(), id, owner, recipient, "one sprint", Micro(now + policy.MaxLifetime)),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task The_default_policy_refuses_a_permanent_grant()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);

        // The default is a policy, not an absence of one: a host that configures nothing still cannot
        // issue the permanent grant DateTimeOffset.MaxValue used to buy.
        var result = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(Guid.NewGuid(), id, owner, recipient, "forever, please", DateTimeOffset.MaxValue),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Invalid, result.Outcome);
        Assert.Equal("ExpiresAt", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public async Task An_existing_grants_expiry_may_still_be_moved_earlier_but_never_later()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        await using (var shrink = _fixture.OwnerDataSource.CreateCommand(
            "UPDATE agent_experience.experience_grants SET expires_at = expires_at - interval '10 minutes' WHERE grant_id = @grant_id"))
        {
            shrink.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grant.GrantId));
            Assert.Equal(1, await shrink.ExecuteNonQueryAsync());
        }

        await using var extend = _fixture.OwnerDataSource.CreateCommand(
            "UPDATE agent_experience.experience_grants SET expires_at = expires_at + interval '1 hour' WHERE grant_id = @grant_id");
        extend.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grant.GrantId));
        await Assert.ThrowsAsync<PostgresException>(() => extend.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task The_database_refuses_an_unbounded_grant_even_from_a_writer_that_bypasses_the_store()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");

        await using var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) " +
            "VALUES (@grant_id, @experience_id, @tenant_id, 'app-1', 'project-1', 'team-a', NULL, NULL, " +
            "@tenant_id, 'app-1', 'project-1', 'team-b', NULL, NULL, " +
            "'hand written', 'someone', now(), 'infinity'::timestamptz, NULL, NULL)");
        command.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", Guid.NewGuid()));
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", Guid.NewGuid()));
        command.Parameters.Add(new NpgsqlParameter<string>("tenant_id", owner.TenantId));

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("experience_grants_lifetime_bounded", failure.ConstraintName);
    }

    [Fact]
    public void A_grant_policy_cannot_be_configured_unbounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresExperienceGrantPolicy(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresExperienceGrantPolicy(Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresExperienceGrantPolicy(TimeSpan.MaxValue));

        // A with-expression re-validates too, so the bound cannot be removed after construction.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PostgresExperienceGrantPolicy.Default with { MaxLifetime = TimeSpan.FromDays(4000) });

        Assert.Equal(TimeSpan.FromDays(90), PostgresExperienceGrantPolicy.Default.MaxLifetime);
    }

    // ---------------------------------------------------------------- reading the trail

    [Fact]
    public async Task The_owner_can_ask_who_saw_its_experience_and_a_recipient_cannot()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var first = await SeedAsync(owner);
        var second = await SeedAsync(owner, taskId: "refund-ticket-escalation");
        var firstGrant = await GrantAsync(tenant, first, owner, recipient);
        await GrantAsync(tenant, second, owner, recipient);

        var store = Audited(new List<ExperienceGrantAccessFailure>());
        await store.GetAsync(Authorize(tenant), recipient, first, CancellationToken.None);
        await store.GetAsync(Authorize(tenant), recipient, second, CancellationToken.None);

        // The whole-scope question -- "who saw our team's experience, and when" -- is the one the
        // ledger exists for, and it is answerable without hand-written SQL.
        var all = await _log.QueryAsync(
            Authorize(tenant), new ExperienceGrantAccessQuery(owner), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, all.Outcome);
        Assert.Equal([first, second], all.Accesses.Select(row => row.ExperienceId));
        Assert.All(all.Accesses, row => Assert.Equal(recipient, row.RecipientScope));
        Assert.NotNull(all.NextCursor);

        // Narrowed to one record.
        var one = await _log.QueryAsync(
            Authorize(tenant), new ExperienceGrantAccessQuery(owner, first), CancellationToken.None);
        Assert.Equal(firstGrant.GrantId, Assert.Single(one.Accesses).GrantId);

        // A recipient cannot enumerate who else read a record it can read, any more than it can list
        // the grants over one: the trail is owner-scope, exactly like the grant history.
        var asRecipient = await _log.QueryAsync(
            Authorize(tenant), new ExperienceGrantAccessQuery(recipient), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, asRecipient.Outcome);
        Assert.Empty(asRecipient.Accesses);

        // And another tenant is denied before any storage is touched.
        var foreign = await _log.QueryAsync(
            Authorize(NewTenant()), new ExperienceGrantAccessQuery(owner), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, foreign.Outcome);
    }

    [Fact]
    public async Task The_trail_pages_from_its_cursor_and_refuses_a_malformed_query()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var store = Audited(new List<ExperienceGrantAccessFailure>());
        for (var i = 0; i < 3; i++)
        {
            await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);
        }

        var page = await _log.QueryAsync(
            Authorize(tenant), new ExperienceGrantAccessQuery(owner, Limit: 2), CancellationToken.None);
        Assert.Equal(2, page.Accesses.Count);

        var next = await _log.QueryAsync(
            Authorize(tenant),
            new ExperienceGrantAccessQuery(owner, Limit: 2, StartAfter: page.NextCursor),
            CancellationToken.None);

        Assert.Single(next.Accesses);
        Assert.Empty(next.Accesses.Select(row => row.AccessId).Intersect(page.Accesses.Select(row => row.AccessId)));

        var invalid = await _log.QueryAsync(
            Authorize(tenant),
            new ExperienceGrantAccessQuery(new Scope(tenant, "app-1", " "), Guid.Empty, 0),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, invalid.Outcome);
        Assert.Equal(["ExperienceId", "Limit", "RecordScope.ProjectId"], invalid.Errors.Select(e => e.Path).Order());
    }

    // ---------------------------------------------------------------- untested corners

    [Fact]
    public async Task A_store_resolved_from_the_container_audits_through_both_registration_overloads()
    {
        foreach (var fromContainer in new[] { true, false })
        {
            var tenant = NewTenant();
            var owner = Scope(tenant, team: "team-a");
            var recipient = Scope(tenant, team: "team-b");
            var id = await SeedAsync(owner);
            var grant = await GrantAsync(tenant, id, owner, recipient);

            var services = new ServiceCollection();
            if (fromContainer)
            {
                services.AddSingleton(_fixture.DataSource);
                services.AddAgentExperiencePostgresGrantAccessLog(_ => { });
                services.AddAgentExperiencePostgresStore();
            }
            else
            {
                services.AddAgentExperiencePostgresGrantAccessLog(_fixture.DataSource, _ => { });
                services.AddAgentExperiencePostgresStore(_fixture.DataSource);
            }

            await using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<IExperienceRecordStore>();

            var read = await store.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);

            // Dropping the auditing argument from either factory must fail here, not only at a host's
            // startup: "registered" and "actually audits" are different claims.
            Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
            Assert.Equal(grant.GrantId, Assert.Single(await AccessRowsAsync(id)).GrantId);
        }
    }

    [Fact]
    public async Task A_candidate_source_resolved_from_the_container_audits_what_it_discloses()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var services = new ServiceCollection();
        services.AddSingleton(_fixture.DataSource);
        services.AddAgentExperiencePostgresGrantAccessLog(_ => { });
        services.AddAgentExperiencePostgresCandidateSource();

        await using var provider = services.BuildServiceProvider();
        var source = provider.GetRequiredService<IExperienceCandidateSource>();

        var result = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(recipient, "refund", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0d),
            CancellationToken.None);

        Assert.True(Assert.Single(result.Candidates).SharedByGrant);
        Assert.Single(await AccessRowsAsync(id));
    }

    [Fact]
    public async Task Cancellation_during_the_append_leaves_the_mode_in_charge_of_the_record()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var failures = new List<ExperienceGrantAccessFailure>();
        var log = new ThrowingAccessLog(new OperationCanceledException(cancelled.Token));

        // Best effort: the read had already finished, so a late cancellation must not discard a record
        // the host's mode says should still be returned. It is reported like any other failure.
        var lenient = new PostgresExperienceRecordStore(
            _fixture.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(log, failures.Add));

        var read = await lenient.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.IsType<OperationCanceledException>(Assert.Single(failures).Failure);

        // Required: still fails closed, because the row still did not land.
        var strict = new PostgresExperienceRecordStore(
            _fixture.DataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(log, failures.Add, ExperienceGrantAuditingMode.Required));

        Assert.Equal(
            ExperienceStoreOutcome.NotFound,
            (await strict.GetAsync(Authorize(tenant), recipient, id, CancellationToken.None)).Outcome);

        Assert.Empty(await AccessRowsAsync(id));
    }

    [Fact]
    public async Task Required_auditing_does_not_break_reads_on_a_database_with_no_grant_table()
    {
        // The degraded path: no grants table, so the read falls back to the exact-scope SQL, nothing is
        // ever shared, and there is nothing to audit -- which must not become a fail-closed refusal.
        await using var dataSource = await _fixture.CreateDatabaseAsync("nograntsreq");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        await using (var drop = dataSource.CreateCommand(
            "DROP TABLE agent_experience.experience_grant_events, agent_experience.experience_grants CASCADE"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var record = Minimal(owner, status: ExperienceStatus.Validated) with { ReuseConfidence = 0.75 };

        var notices = new List<ExperienceGrantSupportNotice>();
        var failures = new List<ExperienceGrantAccessFailure>();
        var store = new PostgresExperienceRecordStore(
            dataSource,
            notices.Add,
            new ExperienceGrantAuditing(
                new PostgresExperienceGrantAccessLog(dataSource), failures.Add, ExperienceGrantAuditingMode.Required));

        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var read = await store.GetAsync(Authorize(tenant), owner, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.False(read.SharedByGrant);
        Assert.Empty(failures);
        Assert.Equal(ExperienceGrantSupportReason.TableMissing, Assert.Single(notices).Reason);
    }

    [Fact]
    public async Task A_grant_stored_before_0009_stays_readable_and_stays_revocable()
    {
        // The upgrade case the migration's runbook is written for: a database that has been issuing
        // grants since 0005 and already holds one with an unbounded expiry when 0009 lands.
        await using var dataSource = await _fixture.CreateDatabaseAsync("legacygrant");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        // 0009 creates the ledger and the ceiling; drop both to stand in for the pre-0009 schema, then
        // write the grant that schema allowed, then put them back exactly as the script does.
        await ExecuteOnAsync(dataSource, "DROP TABLE agent_experience.experience_grant_access");
        await ExecuteOnAsync(
            dataSource, "ALTER TABLE agent_experience.experience_grants DROP CONSTRAINT experience_grants_lifetime_bounded");
        await ExecuteOnAsync(dataSource, "DROP TRIGGER experience_grants_issued_not_future ON agent_experience.experience_grants");

        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = Minimal(owner, status: ExperienceStatus.Validated) with { ReuseConfidence = 0.75 };

        var plain = new PostgresExperienceRecordStore(dataSource);
        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await plain.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var legacy = Guid.NewGuid();
        await using (var insert = dataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) " +
            "VALUES (@grant_id, @experience_id, @tenant_id, 'app-1', 'project-1', 'team-a', NULL, NULL, " +
            "@tenant_id, 'app-1', 'project-1', 'team-b', NULL, NULL, " +
            "'issued before 0009', 'someone', now(), 'infinity'::timestamptz, NULL, NULL)"))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", legacy));
            insert.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", record.ExperienceId));
            insert.Parameters.Add(new NpgsqlParameter<string>("tenant_id", tenant));
            await insert.ExecuteNonQueryAsync();
        }

        await ExecuteOnAsync(dataSource, PostgresExperienceRecordSchema.GetScript(
            PostgresExperienceRecordSchema.GrantAccessLogScriptName));

        // It still permits the read it permitted yesterday: NOT VALID means existing rows are not
        // scanned, so the upgrade does not silently revoke anything.
        var read = await plain.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.True(read.SharedByGrant);

        // And the runbook's remedy works: revoking it is an UPDATE, which re-checks the new CHECK, and
        // without the revoked-row exemption the grant would be permanent and unrevocable forever.
        var revoked = await new PostgresExperienceGrantStore(dataSource).RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(legacy, owner, "issued before the ceiling existed"),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Revoked, revoked.Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.NotFound,
            (await plain.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    // ---------------------------------------------------------------- disclosure level on the trail

    [Theory]
    [InlineData(ExperienceGrantDisclosure.LessonOnly)]
    [InlineData(ExperienceGrantDisclosure.LessonAndApproach)]
    public async Task Every_channel_records_the_permitting_grants_disclosure_level_on_the_access_row(ExperienceGrantDisclosure level)
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient, disclosure: level);
        Assert.Equal(level, grant.Disclosure);

        var failures = new List<ExperienceGrantAccessFailure>();

        // The get path: the read reports the level from the same lateral row that named the grant.
        var read = await Audited(failures).GetAsync(Authorize(tenant), recipient, id, CancellationToken.None);
        Assert.Equal(grant.GrantId, read.PermittingGrantId);
        Assert.Equal(level, read.GrantDisclosure);

        // The text channel: the level reaches the access row, never the candidate.
        var source = new PostgresExperienceCandidateSource(
            _fixture.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(_log, failures.Add));
        var search = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(recipient, "refund", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0d),
            CancellationToken.None);
        Assert.Equal(id, Assert.Single(search.Candidates).Record.ExperienceId);
        Assert.Empty(failures);

        var rows = await _log.QueryAsync(Authorize(tenant), new ExperienceGrantAccessQuery(owner, id), CancellationToken.None);
        Assert.Equal(2, rows.Accesses.Count);
        Assert.All(rows.Accesses, row =>
        {
            Assert.Equal(grant.GrantId, row.GrantId);
            Assert.Equal(level, row.Disclosure);
        });
    }

    [Fact]
    public async Task An_owner_read_reports_no_disclosure_level()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, Scope(tenant, team: "team-b"), disclosure: ExperienceGrantDisclosure.LessonAndApproach);

        var read = await _unaudited.GetAsync(Authorize(tenant), owner, id, CancellationToken.None);

        Assert.False(read.SharedByGrant);
        Assert.Null(read.PermittingGrantId);
        Assert.Null(read.GrantDisclosure);
    }

    [Fact]
    public async Task A_new_access_row_without_a_disclosure_level_is_refused_and_the_mode_decides_the_read()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        // Straight to the log, the way a third-party reader that never learned the level would write.
        var failure = await Assert.ThrowsAsync<ExperienceStoreException>(() => _log.RecordAsync(
            [
                new ExperienceGrantAccess(
                    Guid.NewGuid(), grant.GrantId, id, 0, owner, recipient, "host-principal", null, DateTimeOffset.UtcNow),
            ],
            CancellationToken.None));

        var inner = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal("experience_grant_access_disclosure_recorded", inner.ConstraintName);
        Assert.Empty(await AccessRowsAsync(id));
    }

    [Fact]
    public async Task A_required_search_that_cannot_name_a_disclosure_level_returns_no_candidates()
    {
        // A grant row whose level is NULL is unstorable through this library (NOT NULL, and the level is
        // pinned), so the setup is only reachable as the tables' owner, on a throwaway database.
        await using var dataSource = await _fixture.CreateDatabaseAsync("nolevelreq");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = Minimal(owner, status: ExperienceStatus.Validated) with
        {
            TaskId = "refund-ticket-triage",
            TaskSummary = "Resolve a customer refund",
            ReuseConfidence = 0.75,
        };
        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await new PostgresExperienceRecordStore(dataSource).CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var created = await new PostgresExperienceGrantStore(dataSource).CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                Guid.NewGuid(), record.ExperienceId, owner, recipient, "shared", Micro(DateTimeOffset.UtcNow.AddHours(1))),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, created.Outcome);

        await ExecuteOnAsync(dataSource, "ALTER TABLE agent_experience.experience_grants DISABLE TRIGGER experience_grants_monotonic");
        await ExecuteOnAsync(dataSource, "ALTER TABLE agent_experience.experience_grants ALTER COLUMN disclosure DROP NOT NULL");
        await ExecuteOnAsync(dataSource, "UPDATE agent_experience.experience_grants SET disclosure = NULL");

        var failures = new List<ExperienceGrantAccessFailure>();
        var source = new PostgresExperienceCandidateSource(
            dataSource,
            onGrantsUnavailable: null,
            auditing: new ExperienceGrantAuditing(
                new PostgresExperienceGrantAccessLog(dataSource), failures.Add, ExperienceGrantAuditingMode.Required));

        var result = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(recipient, "refund", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0d),
            CancellationToken.None);

        // The row that could not say what level it delivered under was refused by the database, so under
        // Required the page comes back empty rather than delivering unrecorded.
        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Empty(result.Candidates);

        var failure = Assert.Single(failures);
        Assert.Equal(ExperienceGrantAuditingMode.Required, failure.Mode);
        Assert.Null(Assert.Single(failure.Accesses).Disclosure);
        Assert.Empty(await AccessRowsAsync(dataSource, record.ExperienceId));
    }

    [Fact]
    public async Task Upgrading_to_0011_makes_every_live_grant_LessonOnly_and_leaves_old_trail_rows_unrecorded()
    {
        // A pre-0011 database: every script before 0011, and nothing of it.
        await using var dataSource = await _fixture.CreateDatabaseAsync("upgrade0011");
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames
            .TakeWhile(name => !string.Equals(name, PostgresExperienceRecordSchema.GrantDisclosureScriptName, StringComparison.Ordinal)))
        {
            await ExecuteOnAsync(dataSource, PostgresExperienceRecordSchema.GetScript(scriptName));
        }

        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = Minimal(owner, status: ExperienceStatus.Validated) with { ReuseConfidence = 0.75 };
        var plain = new PostgresExperienceRecordStore(dataSource);
        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await plain.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        // A live grant, its issue event, and one delivery, written in the pre-0011 shape.
        var grantId = Guid.NewGuid();
        await using (var legacy = dataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) " +
            "VALUES (@grant_id, @experience_id, @tenant_id, 'app-1', 'project-1', 'team-a', NULL, NULL, " +
            "@tenant_id, 'app-1', 'project-1', 'team-b', NULL, NULL, " +
            "'issued before 0011', 'someone', now(), now() + interval '1 day', NULL, NULL); " +
            "INSERT INTO agent_experience.experience_grant_events (event_id, grant_id, experience_id, action, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, administrator_authorized_at, expires_at, occurred_at, recorded_at) " +
            "SELECT gen_random_uuid(), grant_id, experience_id, 'Issued', " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, now(), expires_at, now(), now() " +
            "FROM agent_experience.experience_grants WHERE grant_id = @grant_id; " +
            "INSERT INTO agent_experience.experience_grant_access (access_id, grant_id, experience_id, record_revision, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "principal_id, correlation_id, occurred_at, recorded_at) " +
            "VALUES (gen_random_uuid(), @grant_id, @experience_id, 0, @tenant_id, 'app-1', 'project-1', 'team-a', NULL, NULL, " +
            "@tenant_id, 'app-1', 'project-1', 'team-b', NULL, NULL, 'host-principal', NULL, now(), now())"))
        {
            legacy.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
            legacy.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", record.ExperienceId));
            legacy.Parameters.Add(new NpgsqlParameter<string>("tenant_id", tenant));
            await legacy.ExecuteNonQueryAsync();
        }

        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);

        // The grant is LessonOnly -- least disclosure is what an upgrade gives every existing grant.
        var history = await new PostgresExperienceGrantStore(dataSource)
            .GetHistoryAsync(Authorize(tenant), owner, grantId, CancellationToken.None);
        Assert.Equal(ExperienceGrantDisclosure.LessonOnly, history.Grant!.Disclosure);

        // The old event and the old access row carry no level: it was never recorded, and it is not
        // invented after the fact.
        Assert.Null(Assert.Single(history.Events).Disclosure);
        var access = await new PostgresExperienceGrantAccessLog(dataSource)
            .QueryAsync(Authorize(tenant), new ExperienceGrantAccessQuery(owner, record.ExperienceId), CancellationToken.None);
        Assert.Null(Assert.Single(access.Accesses).Disclosure);

        // And the grant still permits the read it permitted before, now reporting its level.
        var read = await plain.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Equal(grantId, read.PermittingGrantId);
        Assert.Equal(ExperienceGrantDisclosure.LessonOnly, read.GrantDisclosure);
    }

    [Fact]
    public async Task The_statements_own_maximum_lifetime_catches_a_client_clock_that_runs_behind()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);

        // A caller whose clock is three days behind the database's would otherwise buy itself three
        // extra days of sharing. The statement measures from the clock that stamps issued_at.
        var policy = new PostgresExperienceGrantPolicy(TimeSpan.FromDays(7));
        var behind = new PostgresExperienceGrantStore(
            _fixture.DataSource, policy, new FrozenClock(DateTimeOffset.UtcNow.AddDays(-3)));

        var grantId = Guid.NewGuid();
        var result = await behind.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                grantId, id, owner, recipient, "just over the line",
                Micro(DateTimeOffset.UtcNow.AddDays(7).AddHours(1))),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Invalid, result.Outcome);
        Assert.Equal("ExpiresAt", Assert.Single(result.Errors).Path);
        Assert.Equal(0L, await CountGrantsAsync(grantId));
        Assert.Equal(0L, await CountGrantEventsAsync(grantId));
    }

    [Fact]
    public async Task A_grant_dated_in_the_future_is_refused_by_the_database()
    {
        var tenant = NewTenant();

        // The other half of the ceiling: it is relative to issued_at, so a bypassing writer could buy an
        // effectively permanent grant simply by dating it a century ahead.
        await using var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) " +
            "VALUES (@grant_id, @experience_id, @tenant_id, 'app-1', 'project-1', 'team-a', NULL, NULL, " +
            "@tenant_id, 'app-1', 'project-1', 'team-b', NULL, NULL, " +
            "'hand written', 'someone', now() + interval '100 years', " +
            "now() + interval '105 years', NULL, NULL)");
        command.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", Guid.NewGuid()));
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", Guid.NewGuid()));
        command.Parameters.Add(new NpgsqlParameter<string>("tenant_id", tenant));

        var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("experience_grants_issued_not_future", failure.ConstraintName);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>The clock that stamps <c>issued_at</c>, which is the one the lifetime bound measures from.</summary>
    private async Task<DateTimeOffset> DatabaseNowAsync()
    {
        await using var command = _fixture.DataSource.CreateCommand("SELECT now()");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task ExecuteOnAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private PostgresExperienceRecordStore Audited(List<ExperienceGrantAccessFailure> failures) =>
        new(_fixture.DataSource, onGrantsUnavailable: null, auditing: new ExperienceGrantAuditing(_log, failures.Add));

    private static DateTimeOffset Micro(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private async Task<ExperienceGrant> GrantAsync(
        string tenant,
        Guid experienceId,
        Scope owner,
        Scope recipient,
        string reason = "sibling team owns the follow-up",
        ExperienceGrantDisclosure disclosure = ExperienceGrantDisclosure.LessonOnly)
    {
        var result = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(
                Guid.NewGuid(), experienceId, owner, recipient, reason, Micro(DateTimeOffset.UtcNow.AddHours(1)), disclosure),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, result.Outcome);
        return result.Grant!;
    }

    private async Task<Guid> SeedAsync(Scope scope, string taskId = "refund-ticket-triage")
    {
        var record = Minimal(scope, status: ExperienceStatus.Validated) with
        {
            TaskId = taskId,
            TaskSummary = "Resolve a customer refund",
            ReuseConfidence = 0.75,
        };

        var created = await _unaudited.CreateAsync(Authorize(scope.TenantId), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        return record.ExperienceId;
    }

    /// <summary>
    /// Writes a second, otherwise identical grant row, with 0005's active-recipient index the only
    /// thing in the way -- so it is written as the tables' owner with that index's guard sidestepped by
    /// a distinct grant ID and the same recipient. Two live grants over one record for one recipient
    /// are exactly what the store refuses to create and exactly what this test needs.
    /// </summary>
    private static async Task CopyGrantAsOwnerAsync(NpgsqlDataSource dataSource, Guid source, Guid copy)
    {
        // The unique index is partial on revoked_at IS NULL, so a second live grant genuinely cannot be
        // inserted while the first stands. This runs against a throwaway database, so the index is
        // dropped and left dropped rather than juggled around the insert.
        await using (var drop = dataSource.CreateCommand(
            "DROP INDEX IF EXISTS agent_experience.ux_experience_grants_active_recipient"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await using var command = dataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) " +
            "SELECT @copy, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, " +
            "reason, administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason " +
            "FROM agent_experience.experience_grants WHERE grant_id = @source");
        command.Parameters.Add(new NpgsqlParameter<Guid>("copy", copy));
        command.Parameters.Add(new NpgsqlParameter<Guid>("source", source));
        await command.ExecuteNonQueryAsync();
    }

    private Task<long> CountGrantsAsync(Guid grantId) =>
        ScalarAsync("SELECT count(*) FROM agent_experience.experience_grants WHERE grant_id = @grant_id", grantId);

    private Task<long> CountGrantEventsAsync(Guid grantId) =>
        ScalarAsync("SELECT count(*) FROM agent_experience.experience_grant_events WHERE grant_id = @grant_id", grantId);

    private async Task<long> ScalarAsync(string sql, Guid grantId)
    {
        await using var command = _fixture.DataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private Task<IReadOnlyList<AccessRow>> AccessRowsAsync(Guid experienceId) =>
        AccessRowsAsync(_fixture.DataSource, experienceId);

    private static async Task<IReadOnlyList<AccessRow>> AccessRowsAsync(NpgsqlDataSource dataSource, Guid experienceId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT access_id, grant_id, experience_id, record_revision, " +
            "tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
            "recipient_tenant_id, recipient_application_id, recipient_project_id, " +
            "recipient_team_id, recipient_agent_id, recipient_user_id, principal_id, correlation_id, " +
            "occurred_at, recorded_at " +
            "FROM agent_experience.experience_grant_access WHERE experience_id = @experience_id " +
            "ORDER BY occurred_at, recorded_at, access_id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));

        var rows = new List<AccessRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AccessRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                new Scope(
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)),
                new Scope(
                    reader.GetString(10),
                    reader.GetString(11),
                    reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15)),
                reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.GetFieldValue<DateTimeOffset>(18),
                reader.GetFieldValue<DateTimeOffset>(19)));
        }

        return rows;
    }

    private sealed record AccessRow(
        Guid AccessId,
        Guid GrantId,
        Guid ExperienceId,
        long RecordRevision,
        Scope RecordScope,
        Scope RecipientScope,
        string PrincipalId,
        string? CorrelationId,
        DateTimeOffset OccurredAt,
        DateTimeOffset RecordedAt);

    /// <summary>A ledger that is down: every append fails the way the port says it must report failure.</summary>
    private sealed class ThrowingAccessLog(Exception failure) : IExperienceGrantAccessLog
    {
        public Task RecordAsync(IReadOnlyList<ExperienceGrantAccess> accesses, CancellationToken cancellationToken) =>
            Task.FromException(failure);

        public Task<ExperienceGrantAccessQueryResult> QueryAsync(
            AuthorizationContext authorization,
            ExperienceGrantAccessQuery query,
            CancellationToken cancellationToken) =>
            Task.FromException<ExperienceGrantAccessQueryResult>(failure);
    }

    /// <summary>A clock that does not move, so "exactly at the maximum" is exact.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
