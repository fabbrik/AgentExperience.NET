using AgentExperience.Core.Retrieval;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 3.1's explicit sharing grants against a real PostgreSQL 16 container: a grant and its audit
/// event committed together, reads widened by an active grant and by nothing else, expiry and
/// revocation decided by the database, and a grant conferring no write, no history, and no delegation.
/// Each test uses its own random tenant, so tests sharing the container never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresGrantTests
{
    private const string Administrator = "sharing-administrator";

    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;
    private readonly PostgresExperienceGrantStore _grants;
    private readonly PostgresExperienceCandidateSource _source;

    public PostgresGrantTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
        _grants = new PostgresExperienceGrantStore(fixture.DataSource);
        _source = new PostgresExperienceCandidateSource(fixture.DataSource);
    }

    // ---------------------------------------------------------------- matrix: create

    [Fact]
    public async Task A_grant_and_its_issue_event_are_committed_together()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var expiry = Micro(DateTimeOffset.UtcNow.AddHours(1));

        var result = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(Guid.NewGuid(), id, owner, recipient, "sibling team owns the follow-up", expiry),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, result.Outcome);
        Assert.Empty(result.Errors);
        var grant = Assert.IsType<ExperienceGrant>(result.Grant);
        Assert.Equal(id, grant.ExperienceId);
        Assert.Equal(owner, grant.RecordScope);
        Assert.Equal(recipient, grant.RecipientScope);
        Assert.Equal(expiry, grant.ExpiresAt);
        Assert.Equal(Administrator, grant.AdministratorPrincipalId);
        Assert.Null(grant.RevokedAt);
        Assert.Null(grant.RevocationReason);

        // The issue time is the database's, not the caller's: it was never sent.
        Assert.NotEqual(default, grant.IssuedAt);
        Assert.True(grant.IssuedAt < grant.ExpiresAt);

        // Both writes, or neither. The event is the audit trail the grant row alone would not leave.
        Assert.Equal(1L, await CountEventsAsync(grant.GrantId, "Issued"));
        Assert.Equal(1L, await CountGrantsAsync(grant.GrantId));

        var listed = await _grants.ListAsync(Authorize(tenant), owner, id, CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Found, listed.Outcome);
        Assert.Equal(grant, Assert.Single(listed.Grants));
    }

    // ---------------------------------------------------------------- matrix: missing authority

    [Theory]
    [InlineData("null")]
    [InlineData("blank")]
    [InlineData("whitespace")]
    public async Task A_create_without_administrator_authority_is_denied_and_writes_nothing(string authority)
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);
        var grantId = Guid.NewGuid();

        GrantAdministration? administration = authority switch
        {
            "blank" => new GrantAdministration(string.Empty, DateTimeOffset.UtcNow),
            "whitespace" => new GrantAdministration("   ", DateTimeOffset.UtcNow),
            _ => null,
        };

        var result = await _grants.CreateAsync(
            Authorize(tenant),
            administration,
            Request(grantId, id, owner, Scope(tenant, team: "team-b")),
            CancellationToken.None);

        // The caller's own AuthorizationContext permits this scope; administering sharing is a separate
        // authority the host has to construct, and it is never inferred from roles or from the scope.
        Assert.Equal(ExperienceGrantOutcome.Denied, result.Outcome);
        Assert.Null(result.Grant);
        Assert.Equal(0L, await CountGrantsAsync(grantId));
        Assert.Equal(0L, await CountEventsAsync(grantId));
    }

    [Fact]
    public async Task A_revoke_without_administrator_authority_is_denied_and_the_grant_still_stands()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        var result = await _grants.RevokeAsync(
            Authorize(tenant),
            administration: null,
            new ExperienceGrantRevocation(grant.GrantId, owner, "no longer needed"),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Denied, result.Outcome);
        Assert.Equal(1L, await CountEventsAsync(grant.GrantId));
        Assert.Equal(ExperienceStoreOutcome.Found, (await ReadAsync(tenant, recipient, id)).Outcome);
    }

    // ---------------------------------------------------------------- matrix: cross-boundary

    [Theory]
    [InlineData("tenant", "RecipientScope.TenantId")]
    [InlineData("application", "RecipientScope.ApplicationId")]
    [InlineData("project", "RecipientScope.ProjectId")]
    public async Task A_recipient_scope_that_crosses_a_boundary_is_Invalid_with_the_field_path_and_writes_nothing(
        string boundary,
        string path)
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);
        var grantId = Guid.NewGuid();

        var recipient = boundary switch
        {
            "tenant" => owner with { TenantId = NewTenant(), TeamId = "team-b" },
            "application" => owner with { ApplicationId = "app-2", TeamId = "team-b" },
            _ => owner with { ProjectId = "project-2", TeamId = "team-b" },
        };

        var result = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            Request(grantId, id, owner, recipient),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Invalid, result.Outcome);
        Assert.Null(result.Grant);
        Assert.Equal(path, Assert.Single(result.Errors).Path);
        Assert.Equal(0L, await CountGrantsAsync(grantId));
        Assert.Equal(0L, await CountEventsAsync(grantId));

        // And no read was ever widened by the request that failed.
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, Scope(tenant, team: "team-b"), id)).Outcome);
    }

    // ---------------------------------------------------------------- matrix: read via grant

    [Fact]
    public async Task A_recipient_reads_the_granted_record_exactly_as_its_owner_does()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);

        var beforeGrant = await ReadAsync(tenant, recipient, id);
        Assert.Equal(ExperienceStoreOutcome.NotFound, beforeGrant.Outcome);

        await GrantAsync(tenant, id, owner, recipient);

        var theirs = await ReadAsync(tenant, recipient, id);
        var mine = await ReadAsync(tenant, owner, id);

        Assert.Equal(ExperienceStoreOutcome.Found, theirs.Outcome);
        Assert.Equal(Canonical(mine.Record!), Canonical(theirs.Record!));

        // Reading it does not move it: the record still belongs to the team that owns it.
        Assert.Equal(owner, theirs.Record!.Scope);

        // The store says how it was readable, because it is the only layer that knows. The owner's own
        // read of the same record is not marked shared.
        Assert.True(theirs.SharedByGrant);
        Assert.False(mine.SharedByGrant);

        // And only the named record is shared. A sibling record in the same owner scope is not.
        var sibling = await SeedAsync(owner);
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, recipient, sibling)).Outcome);

        // Nor does the grant reach a third scope that was never named.
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, Scope(tenant, team: "team-c"), id)).Outcome);
    }

    [Fact]
    public async Task A_grant_never_widens_a_read_across_a_tenant_application_or_project()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, Scope(tenant, team: "team-b"));

        // The recipient's own optional field matches the grant; everything required does not.
        var elsewhere = new[]
        {
            Scope(NewTenant(), team: "team-b"),
            owner with { ApplicationId = "app-2", TeamId = "team-b" },
            owner with { ProjectId = "project-2", TeamId = "team-b" },
        };

        foreach (var scope in elsewhere)
        {
            var result = await _store.GetAsync(Authorize(scope.TenantId), scope, id, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.NotFound, result.Outcome);
        }
    }

    // ---------------------------------------------------------------- matrix: retrieval via grant

    [Fact]
    public async Task The_text_channel_returns_a_granted_record_under_the_same_eligibility()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var shared = await SeedAsync(owner, taskId: "refund-ticket-triage", summary: "Resolve a customer refund");
        var ineligible = await SeedAsync(owner, taskId: "refund-ticket-draft", summary: "Resolve a customer refund", status: ExperienceStatus.Candidate);
        var ungranted = await SeedAsync(owner, taskId: "refund-ticket-other", summary: "Resolve a customer refund");

        await GrantAsync(tenant, shared, owner, recipient);
        await GrantAsync(tenant, ineligible, owner, recipient);

        var result = await SearchAsync(tenant, recipient, "refund");

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        // Eligibility is unchanged by sharing: the Candidate record stays out, and an ungranted record
        // in the same owner scope is not reachable at all.
        Assert.Equal([shared], result.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.True(Assert.Single(result.Candidates).SharedByGrant);
        Assert.DoesNotContain(ineligible, result.Candidates.Select(candidate => candidate.Record.ExperienceId));
        Assert.DoesNotContain(ungranted, result.Candidates.Select(candidate => candidate.Record.ExperienceId));

        // The confidence floor still applies to a shared record exactly as it does to an owned one.
        var floored = await SearchAsync(tenant, recipient, "refund", minimumConfidence: 0.9d);
        Assert.Empty(floored.Candidates);
    }

    // ---------------------------------------------------------------- matrix: expired

    [Fact]
    public async Task An_expired_grant_denies_the_read_and_the_expiry_is_the_database_clock()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner, taskId: "refund-ticket-triage", summary: "Resolve a customer refund");
        var grant = await GrantAsync(tenant, id, owner, recipient);

        Assert.Equal(ExperienceStoreOutcome.Found, (await ReadAsync(tenant, recipient, id)).Outcome);

        // Aged past its expiry using the server's own clock, so nothing about this assertion depends on
        // the test host's clock agreeing with the database's. Both timestamps move, because a grant
        // that expires before it was issued is one the schema refuses to store at all.
        await ExecuteAsync(
            "UPDATE agent_experience.experience_grants " +
            "SET issued_at = now() - interval '2 seconds', expires_at = now() - interval '1 second' " +
            "WHERE grant_id = @grant_id",
            ("grant_id", grant.GrantId));

        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, recipient, id)).Outcome);
        Assert.Empty((await SearchAsync(tenant, recipient, "refund")).Candidates);

        // Expiring is not revoking: the grant is still on record, with its history intact.
        var listed = await _grants.ListAsync(Authorize(tenant), owner, id, CancellationToken.None);
        Assert.Null(Assert.Single(listed.Grants).RevokedAt);
        Assert.Equal(1L, await CountEventsAsync(grant.GrantId));

        // An expiry that is already past when the grant is issued is rejected by the same clock.
        var stillborn = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(Guid.NewGuid(), id, owner, recipient, "too late", DateTimeOffset.UtcNow.AddDays(-1)),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Invalid, stillborn.Outcome);
        Assert.Equal("ExpiresAt", Assert.Single(stillborn.Errors).Path);
    }

    // ---------------------------------------------------------------- matrix: revoked

    [Fact]
    public async Task A_revoked_grant_denies_the_read_and_the_history_keeps_both_events()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner, taskId: "refund-ticket-triage", summary: "Resolve a customer refund");
        var grant = await GrantAsync(tenant, id, owner, recipient);

        Assert.Equal(ExperienceStoreOutcome.Found, (await ReadAsync(tenant, recipient, id)).Outcome);

        var revoked = await _grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(grant.GrantId, owner, "the collaboration ended"),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Revoked, revoked.Outcome);
        Assert.NotNull(revoked.Grant!.RevokedAt);
        Assert.Equal("the collaboration ended", revoked.Grant.RevocationReason);

        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, recipient, id)).Outcome);
        Assert.Empty((await SearchAsync(tenant, recipient, "refund")).Candidates);

        // Append-only: revoking adds an event and deletes neither the grant nor the issue event. Read
        // through the port, because that is the acceptance criterion -- both events remain in the
        // grant's history, visible without reaching into the table.
        Assert.Equal(1L, await CountGrantsAsync(grant.GrantId));
        var history = await _grants.GetHistoryAsync(Authorize(tenant), owner, grant.GrantId, CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Found, history.Outcome);
        Assert.Equal(
            [ExperienceGrantAction.Issued, ExperienceGrantAction.Revoked],
            history.Events.Select(e => e.Action));

        var listed = await _grants.ListAsync(Authorize(tenant), owner, id, CancellationToken.None);
        Assert.Equal(revoked.Grant, Assert.Single(listed.Grants));

        // Revoking again writes nothing and cannot restate the reason.
        var again = await _grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(grant.GrantId, owner, "a different story"),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.AlreadyRevoked, again.Outcome);
        Assert.Equal("the collaboration ended", again.Grant!.RevocationReason);
        Assert.Equal(2L, await CountEventsAsync(grant.GrantId));
    }

    [Fact]
    public async Task Only_one_active_grant_may_exist_per_recipient_so_revoking_the_known_one_ends_access()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var first = await GrantAsync(tenant, id, owner, recipient);

        // A second, overlapping grant to the same recipient would mean revoking the one an
        // administrator knows about ended nothing. It is refused instead.
        var overlapping = Guid.NewGuid();
        var second = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            Request(overlapping, id, owner, recipient, "and again"),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Conflict, second.Outcome);
        Assert.Equal(0L, await CountGrantsAsync(overlapping));

        // A different recipient is a different grant, and is allowed.
        await GrantAsync(tenant, id, owner, Scope(tenant, team: "team-c"));

        await _grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(first.GrantId, owner, "superseded"),
            CancellationToken.None);

        // Revoking the known grant really did end team-b's access, and left team-c's alone.
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, recipient, id)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, (await ReadAsync(tenant, Scope(tenant, team: "team-c"), id)).Outcome);

        // And once revoked, the recipient can be granted access again.
        await GrantAsync(tenant, id, owner, recipient, reason: "the collaboration resumed");
        Assert.Equal(ExperienceStoreOutcome.Found, (await ReadAsync(tenant, recipient, id)).Outcome);
    }

    // ---------------------------------------------------------------- matrix: mutation and delegation

    [Fact]
    public async Task A_grant_confers_no_write_no_lifecycle_history_and_no_enumeration()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        var auth = Authorize(tenant);

        // A lifecycle commit is a write, and a grant is not authority to write.
        var commit = await _store.CommitLifecycleEventAsync(
            auth,
            recipient,
            Event(id, ExperienceStatus.Validated, ExperienceStatus.Revoked, 0),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, commit.Outcome);

        // The record is untouched by the attempt.
        var stored = (await ReadAsync(tenant, owner, id)).Record!;
        Assert.Equal(ExperienceStatus.Validated, stored.Status);
        Assert.Equal(0, stored.Revision);

        // The audit trail of mutations is not reusable experience, so it stays owner-scope only.
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await _store.GetHistoryAsync(auth, recipient, id, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetHistoryAsync(auth, owner, id, CancellationToken.None)).Outcome);

        // Nor does a grant let a recipient enumerate what the owner scope holds.
        var listed = await _store.QueryAsync(auth, new ExperienceRecordQuery(recipient), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, listed.Outcome);
        Assert.Empty(listed.Records);

        // Nor list, issue, or revoke the grants over the record it can read: from the recipient's
        // scope the record is simply not there, which is the same answer as a record that does not
        // exist.
        var listedGrants = await _grants.ListAsync(auth, recipient, id, CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.NotFound, listedGrants.Outcome);
        Assert.Empty(listedGrants.Grants);
    }

    [Fact]
    public async Task A_recipient_cannot_issue_a_further_grant()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var onward = Scope(tenant, team: "team-c");
        var id = await SeedAsync(owner);
        await GrantAsync(tenant, id, owner, recipient);

        // Without administrator authority: denied outright, whatever the recipient can read.
        var withoutAuthority = await _grants.CreateAsync(
            Authorize(tenant),
            administration: null,
            Request(Guid.NewGuid(), id, recipient, onward),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Denied, withoutAuthority.Outcome);

        // Even with it, a grant is issued from the scope that owns the record, and the recipient does
        // not own it: there is no record at that scope to grant over.
        var grantId = Guid.NewGuid();
        var withAuthority = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            Request(grantId, id, recipient, onward),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.NotFound, withAuthority.Outcome);
        Assert.Equal(0L, await CountGrantsAsync(grantId));
        Assert.Equal(0L, await CountEventsAsync(grantId));
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, onward, id)).Outcome);
    }

    // ---------------------------------------------------------------- matrix: missing scope

    [Theory]
    [InlineData("tenant")]
    [InlineData("application")]
    [InlineData("project")]
    [InlineData("record")]
    public async Task A_grant_request_missing_required_scope_or_identity_is_Invalid_and_never_reaches_the_database(string missing)
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");

        var request = missing switch
        {
            "tenant" => Request(Guid.NewGuid(), Guid.NewGuid(), owner with { TenantId = " " }, recipient with { TenantId = " " }),
            "application" => Request(Guid.NewGuid(), Guid.NewGuid(), owner with { ApplicationId = "" }, recipient with { ApplicationId = "" }),
            "project" => Request(Guid.NewGuid(), Guid.NewGuid(), owner with { ProjectId = "" }, recipient with { ProjectId = "" }),
            _ => Request(Guid.NewGuid(), Guid.Empty, owner, recipient),
        };

        // Against a data source that cannot connect: reaching the database at all would throw.
        await using var unreachable = Unreachable();
        var store = new PostgresExperienceGrantStore(unreachable);

        var result = await store.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            request,
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        // No global or wildcard scope is inferred from an absent field: it is simply a bad request.
        Assert.All(result.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Path)));
    }

    [Fact]
    public async Task A_scope_outside_the_host_authorization_is_denied_before_any_database_access()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");

        await using var unreachable = Unreachable();
        var store = new PostgresExperienceGrantStore(unreachable);
        var administration = new GrantAdministration(Administrator, DateTimeOffset.UtcNow);

        var create = await store.CreateAsync(
            Authorize(NewTenant()),
            administration,
            Request(Guid.NewGuid(), Guid.NewGuid(), owner, Scope(tenant, team: "team-b")),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Denied, create.Outcome);

        var revoke = await store.RevokeAsync(
            Authorize(NewTenant()),
            administration,
            new ExperienceGrantRevocation(Guid.NewGuid(), owner, "not mine to revoke"),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Denied, revoke.Outcome);

        var list = await store.ListAsync(Authorize(NewTenant()), owner, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Denied, list.Outcome);
    }

    // ---------------------------------------------------------------- conflicts and missing records

    [Fact]
    public async Task A_re_issued_grant_id_is_Conflict_and_writes_nothing()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);
        var other = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, Scope(tenant, team: "team-b"));

        var again = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            Request(grant.GrantId, other, owner, Scope(tenant, team: "team-c")),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Conflict, again.Outcome);
        Assert.Null(again.Grant);
        // Nothing of the second request survives: not a row, not an event, not a widened read.
        Assert.Equal(1L, await CountEventsAsync(grant.GrantId));
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, Scope(tenant, team: "team-c"), other)).Outcome);
    }

    [Fact]
    public async Task A_grant_over_a_record_that_is_not_in_the_owner_scope_is_NotFound_and_writes_nothing()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var elsewhere = Scope(tenant, team: "team-z");
        var id = await SeedAsync(elsewhere);
        var grantId = Guid.NewGuid();

        var result = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            Request(grantId, id, owner, Scope(tenant, team: "team-b")),
            CancellationToken.None);

        // Identical to a record that does not exist at all: nothing about the other scope is revealed.
        Assert.Equal(ExperienceGrantOutcome.NotFound, result.Outcome);
        Assert.Equal(0L, await CountGrantsAsync(grantId));
        Assert.Equal(0L, await CountEventsAsync(grantId));
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, Scope(tenant, team: "team-b"), id)).Outcome);
    }

    [Fact]
    public async Task Revoking_a_grant_from_another_owner_scope_is_NotFound_and_leaves_it_active()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        var result = await _grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(grant.GrantId, Scope(tenant, team: "team-z"), "not mine"),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.NotFound, result.Outcome);
        Assert.Equal(1L, await CountEventsAsync(grant.GrantId));
        Assert.Equal(ExperienceStoreOutcome.Found, (await ReadAsync(tenant, recipient, id)).Outcome);
    }

    [Fact]
    public async Task A_grant_changes_nothing_about_the_record_it_names()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);
        var before = (await ReadAsync(tenant, owner, id)).Record!;

        var grant = await GrantAsync(tenant, id, owner, Scope(tenant, team: "team-b"));
        await _grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRevocation(grant.GrantId, owner, "done"),
            CancellationToken.None);

        var after = (await ReadAsync(tenant, owner, id)).Record!;

        // No status, confidence, counter, revision, or timestamp moves on the grant path.
        Assert.Equal(Canonical(before), Canonical(after));
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetHistoryAsync(Authorize(tenant), owner, id, CancellationToken.None)).Outcome);
        Assert.Empty((await _store.GetHistoryAsync(Authorize(tenant), owner, id, CancellationToken.None)).Events);
    }

    // ---------------------------------------------------------------- the owner half of the predicate

    [Fact]
    public async Task A_grant_row_whose_owner_scope_disagrees_with_the_record_never_widens_a_read()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var grant = await GrantAsync(tenant, id, owner, recipient);

        Assert.Equal(ExperienceStoreOutcome.Found, (await ReadAsync(tenant, recipient, id)).Outcome);

        // A writer that bypassed this store and lied about which scope owns the record. The predicate
        // matches the grant's owner columns against the record's own, so the lie admits nothing.
        await ExecuteAsync(
            "UPDATE agent_experience.experience_grants SET team_id = 'team-z' WHERE grant_id = @grant_id",
            ("grant_id", grant.GrantId));

        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, recipient, id)).Outcome);
        Assert.Empty((await SearchAsync(tenant, recipient, "refund")).Candidates);
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await ReadAsync(tenant, Scope(tenant, team: "team-z"), id)).Outcome);
    }

    // ---------------------------------------------------------------- audit trail

    [Fact]
    public async Task A_grant_history_carries_both_events_the_administrator_and_when_their_authority_was_established()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);
        var authorizedAt = Micro(DateTimeOffset.UtcNow.AddMinutes(-5));

        var created = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, authorizedAt),
            Request(Guid.NewGuid(), id, owner, recipient),
            CancellationToken.None);
        var grant = created.Grant!;

        await _grants.RevokeAsync(
            Authorize(tenant),
            new GrantAdministration("second-administrator", authorizedAt),
            new ExperienceGrantRevocation(grant.GrantId, owner, "the collaboration ended"),
            CancellationToken.None);

        var history = await _grants.GetHistoryAsync(Authorize(tenant), owner, grant.GrantId, CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Found, history.Outcome);
        Assert.NotNull(history.Grant!.RevokedAt);
        Assert.Equal(2, history.Events.Count);

        var issued = history.Events[0];
        Assert.Equal(ExperienceGrantAction.Issued, issued.Action);
        Assert.Equal(Administrator, issued.AdministratorPrincipalId);
        // The authority the action was taken under, not only who took it.
        Assert.Equal(authorizedAt, issued.AdministratorAuthorizedAt);
        Assert.Equal(owner, issued.RecordScope);
        Assert.Equal(recipient, issued.RecipientScope);
        Assert.Equal(grant.ExpiresAt, issued.ExpiresAt);

        var revoked = history.Events[1];
        Assert.Equal(ExperienceGrantAction.Revoked, revoked.Action);
        Assert.Equal("second-administrator", revoked.AdministratorPrincipalId);
        Assert.Equal("the collaboration ended", revoked.Reason);

        // Owner-scope only, like the lifecycle history it mirrors.
        Assert.Equal(
            ExperienceGrantOutcome.NotFound,
            (await _grants.GetHistoryAsync(Authorize(tenant), recipient, grant.GrantId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task The_requested_expiry_comes_back_exactly_and_the_administrators_authority_must_be_dated()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var id = await SeedAsync(owner);

        // Sub-microsecond precision: a value PostgreSQL truncates, so a store that echoed the request
        // rather than what it stored would disagree with what the next read sees.
        var expiry = Micro(DateTimeOffset.UtcNow.AddHours(1)).AddTicks(7);
        var created = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(Guid.NewGuid(), id, owner, recipient, "sibling team owns the follow-up", expiry),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, created.Outcome);
        var listed = Assert.Single((await _grants.ListAsync(Authorize(tenant), owner, id, CancellationToken.None)).Grants);
        Assert.Equal(listed.ExpiresAt, created.Grant!.ExpiresAt);

        // An administrator with no established-at instant would put "year zero" in the audit trail.
        var undated = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, default),
            Request(Guid.NewGuid(), id, owner, Scope(tenant, team: "team-c")),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Invalid, undated.Outcome);
        Assert.Equal("Administration.AuthorizedAt", Assert.Single(undated.Errors).Path);
    }

    // ---------------------------------------------------------------- listing and validation

    [Fact]
    public async Task Listing_tells_a_record_with_no_grants_apart_from_a_record_that_is_not_here()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var unshared = await SeedAsync(owner);

        var none = await _grants.ListAsync(Authorize(tenant), owner, unshared, CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Found, none.Outcome);
        Assert.Empty(none.Grants);

        var missing = await _grants.ListAsync(Authorize(tenant), owner, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.NotFound, missing.Outcome);
        Assert.Empty(missing.Grants);
    }

    [Theory]
    [InlineData("revoke-reason")]
    [InlineData("revoke-grant")]
    [InlineData("revoke-scope")]
    [InlineData("list-limit")]
    [InlineData("history-grant")]
    public async Task Malformed_grant_administration_requests_are_Invalid_with_a_field_path_and_never_reach_the_database(string malformed)
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var administration = new GrantAdministration(Administrator, DateTimeOffset.UtcNow);

        // Against a data source that cannot connect: reaching the database at all would throw.
        await using var unreachable = Unreachable();
        var store = new PostgresExperienceGrantStore(unreachable);

        IReadOnlyList<StoreValidationError> errors = malformed switch
        {
            "revoke-reason" => (await store.RevokeAsync(
                Authorize(tenant), administration, new ExperienceGrantRevocation(Guid.NewGuid(), owner, "  "), CancellationToken.None)).Errors,
            "revoke-grant" => (await store.RevokeAsync(
                Authorize(tenant), administration, new ExperienceGrantRevocation(Guid.Empty, owner, "done"), CancellationToken.None)).Errors,
            "revoke-scope" => (await store.RevokeAsync(
                Authorize(tenant), administration, new ExperienceGrantRevocation(Guid.NewGuid(), owner with { ProjectId = " " }, "done"), CancellationToken.None)).Errors,
            "list-limit" => (await store.ListAsync(
                Authorize(tenant), owner, Guid.NewGuid(), CancellationToken.None, limit: 0)).Errors,
            _ => (await store.GetHistoryAsync(
                Authorize(tenant), owner, Guid.Empty, CancellationToken.None)).Errors,
        };

        // A blank revocation reason is a malformed request, not a constraint violation surfacing from
        // the audit insert as an infrastructure failure.
        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Path)));
    }

    [Fact]
    public async Task A_grant_to_the_scope_that_already_owns_the_record_is_Invalid_and_writes_nothing()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var id = await SeedAsync(owner);
        var grantId = Guid.NewGuid();

        var result = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            Request(grantId, id, owner, owner),
            CancellationToken.None);

        // It would permit nothing, and would leave an audit row claiming access was given.
        Assert.Equal(ExperienceGrantOutcome.Invalid, result.Outcome);
        Assert.Equal("RecipientScope", Assert.Single(result.Errors).Path);
        Assert.Equal(0L, await CountGrantsAsync(grantId));
    }

    // ---------------------------------------------------------------- degraded deployments

    [Fact]
    public async Task A_database_without_the_grant_table_reads_the_exact_scope_and_reports_it_once()
    {
        // A deployment that has applied 0001-0003 but not 0005 yet, which the library supports: every
        // get and search must keep working, narrowed to the exact scope.
        await using var dataSource = await _fixture.CreateDatabaseAsync("nogrants");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        await using (var drop = dataSource.CreateCommand("DROP TABLE agent_experience.experience_grants"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        var notices = new List<ExperienceGrantSupportNotice>();
        var store = new PostgresExperienceRecordStore(dataSource, notices.Add);
        var source = new PostgresExperienceCandidateSource(dataSource, notices.Add);

        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var record = Minimal(owner, status: ExperienceStatus.Validated) with
        {
            TaskId = "refund-ticket-triage",
            TaskSummary = "Resolve a customer refund",
            ReuseConfidence = 0.75,
        };
        Assert.Equal(
            ExperienceStoreOutcome.Created,
            (await store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        // The owner still reads and searches normally.
        var mine = await store.GetAsync(Authorize(tenant), owner, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, mine.Outcome);
        Assert.False(mine.SharedByGrant);

        var found = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(owner, "refund", [ExperienceStatus.Validated, ExperienceStatus.Reinforced], 0d),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, found.Outcome);
        Assert.Single(found.Candidates);

        // Nothing is widened while degraded: a sibling scope still sees nothing.
        Assert.Equal(
            ExperienceStoreOutcome.NotFound,
            (await store.GetAsync(Authorize(tenant), Scope(tenant, team: "team-b"), record.ExperienceId, CancellationToken.None)).Outcome);

        // Reported once per reader, with what was wrong, and not again on later reads.
        Assert.Equal(2, notices.Count);
        Assert.All(notices, notice => Assert.Equal(ExperienceGrantSupportReason.TableMissing, notice.Reason));
        Assert.Contains(notices, notice => notice.Operation == "get");
        Assert.Contains(notices, notice => notice.Operation == "candidate search");
    }

    // ---------------------------------------------------------------- helpers

    private static ExperienceGrantRequest Request(Guid grantId, Guid experienceId, Scope owner, Scope recipient, string reason = "sibling team owns the follow-up") =>
        new(grantId, experienceId, owner, recipient, reason, Micro(DateTimeOffset.UtcNow.AddHours(1)));

    private static DateTimeOffset Micro(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private async Task<ExperienceGrant> GrantAsync(string tenant, Guid experienceId, Scope owner, Scope recipient, string reason = "sibling team owns the follow-up")
    {
        var result = await _grants.CreateAsync(
            Authorize(tenant),
            new GrantAdministration(Administrator, DateTimeOffset.UtcNow),
            Request(Guid.NewGuid(), experienceId, owner, recipient, reason),
            CancellationToken.None);

        Assert.Equal(ExperienceGrantOutcome.Created, result.Outcome);
        return result.Grant!;
    }

    private Task<ExperienceRecordGetResult> ReadAsync(string tenant, Scope scope, Guid experienceId) =>
        _store.GetAsync(Authorize(tenant), scope, experienceId, CancellationToken.None);

    private Task<ExperienceCandidateSearchResult> SearchAsync(string tenant, Scope scope, string taskText, double minimumConfidence = 0d) =>
        _source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(scope, taskText, [ExperienceStatus.Validated, ExperienceStatus.Reinforced], minimumConfidence),
            CancellationToken.None);

    /// <summary>Creates one searchable, eligible record and returns its ID.</summary>
    private async Task<Guid> SeedAsync(
        Scope scope,
        string taskId = "refund-ticket-triage",
        string summary = "Resolve a customer refund",
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.75)
    {
        var record = Minimal(scope, status: status) with
        {
            TaskId = taskId,
            TaskSummary = summary,
            ReuseConfidence = confidence,
        };

        var created = await _store.CreateAsync(Authorize(scope.TenantId), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        return record.ExperienceId;
    }

    private Task<long> CountGrantsAsync(Guid grantId) =>
        ScalarAsync("SELECT count(*) FROM agent_experience.experience_grants WHERE grant_id = @grant_id", ("grant_id", grantId));

    private Task<long> CountEventsAsync(Guid grantId, string? action = null) =>
        action is null
            ? ScalarAsync("SELECT count(*) FROM agent_experience.experience_grant_events WHERE grant_id = @grant_id", ("grant_id", grantId))
            : ScalarAsync(
                "SELECT count(*) FROM agent_experience.experience_grant_events WHERE grant_id = @grant_id AND action = @action",
                ("grant_id", grantId),
                ("action", action));

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = _fixture.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { ParameterName = name, Value = value });
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = _fixture.DataSource.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { ParameterName = name, Value = value });
        }

        await command.ExecuteNonQueryAsync();
    }
}
