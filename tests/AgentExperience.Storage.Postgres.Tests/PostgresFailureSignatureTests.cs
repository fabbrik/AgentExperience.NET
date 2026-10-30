using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 28.2 against a real PostgreSQL container: <c>0030</c>'s failure signatures. Written with the record, in its own
/// scope, in the clear and without error text; gone with the record's erasure; left out (not failing the create) while
/// the role is not granted the table; restored by the backfill for plaintext records only; and found through a sharing
/// grant exactly as text search finds the record. Each test uses its own random tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresFailureSignatureTests(PostgresFixture fixture)
{
    private const string Error = "FileNotFoundException: Could not find file '/home/runner/work/app/fixtures/orders-1182.json'.";

    private readonly PostgresExperienceRecordStore _store = new(fixture.DataSource);

    [Fact]
    public async Task A_records_signatures_are_written_with_it_in_its_own_scope_with_the_class_and_fingerprint_and_no_error_text()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant, team: "team-a");
        var record = WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated));
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var expected = ExperienceFailureSignature.From("run_tests", Error)!;
        await using var command = fixture.RawDataSource.CreateCommand(
            "SELECT to_jsonb(s)::text, signature_key, tool_name, error_class, fingerprint, resolved, signature_version, tenant_id, team_id " +
            "FROM agent_experience.experience_failure_signatures s WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected.Key, reader.GetString(1));
        Assert.Equal("run_tests", reader.GetString(2));
        Assert.Equal("FileNotFoundException", reader.GetString(3));
        Assert.Equal(expected.Fingerprint, reader.GetString(4));
        Assert.True(reader.GetBoolean(5));
        Assert.Equal(ExperienceFailureSignature.SignatureVersion, reader.GetInt32(6));
        Assert.Equal(tenant, reader.GetString(7));
        Assert.Equal("team-a", reader.GetString(8));

        // No part of the error's text is stored, in either mode.
        var row = reader.GetString(0);
        Assert.DoesNotContain("orders", row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Could not find", row, StringComparison.OrdinalIgnoreCase);
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Erasing_a_record_removes_its_signatures_and_no_other_records()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var erased = WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated));
        var kept = WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated));
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, erased, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, kept, CancellationToken.None)).Outcome);
        Assert.Equal(1L, await CountAsync(erased.ExperienceId));

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await _store.DeleteAsync(auth, scope, erased.ExperienceId, CancellationToken.None)).Outcome);

        Assert.Equal(0L, await CountAsync(erased.ExperienceId));
        Assert.Equal(1L, await CountAsync(kept.ExperienceId));
        var found = await new PostgresExperienceSignatureSource(fixture.DataSource).FindBySignatureAsync(
            auth, scope, Query(ExperienceFailureSignature.From("run_tests", Error)!), CancellationToken.None);
        Assert.Equal([kept.ExperienceId], found.Candidates.Select(c => c.Record.ExperienceId));
    }

    [Fact]
    public async Task A_record_is_created_without_its_signatures_while_the_role_is_not_granted_the_table_and_the_backfill_adds_them_to_plaintext_records()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated));
        var silent = WithFailedCall(Minimal(scope, status: ExperienceStatus.Validated), tool: "build", error: "exit 1");
        var unfailing = Minimal(scope, status: ExperienceStatus.Validated);

        // The privileges call not yet re-run after 0030: the role holds nothing on the table.
        await OwnerExecuteAsync($"REVOKE INSERT ON agent_experience.experience_failure_signatures FROM {PostgresFixture.ApplicationRoleName}");
        try
        {
            foreach (var created in new[] { record, silent, unfailing })
            {
                Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, created, CancellationToken.None)).Outcome);
            }
        }
        finally
        {
            await OwnerExecuteAsync($"GRANT INSERT ON agent_experience.experience_failure_signatures TO {PostgresFixture.ApplicationRoleName}");
        }

        Assert.Equal(0L, await CountAsync(record.ExperienceId));
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, scope, record.ExperienceId, CancellationToken.None)).Outcome);

        // The upgrade job, one record per call, resumed from each result's cursor.
        var owner = new PostgresExperienceRecordStore(fixture.OwnerDataSource);
        var records = 0;
        var signatures = 0;
        var calls = 0;
        Guid? cursor = null;
        ExperienceFailureSignatureBackfillResult batch;
        do
        {
            batch = await owner.BackfillFailureSignaturesAsync(auth, scope, batchSize: 1, ScopeMatch.Exact, cursor, CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Committed, batch.Outcome);
            Assert.Equal(0, batch.SkippedCount);
            records += batch.RecordCount;
            signatures += batch.SignatureCount;
            cursor = batch.ResumeAfter;
            calls++;
        }
        while (batch.MoreRemain);

        if (EncryptionMode.IsOn)
        {
            // Sealed records are skipped: the job cannot read a sealed payload, and examines none.
            Assert.Equal(0, records);
            Assert.Equal(0L, await CountAsync(record.ExperienceId));
            return;
        }

        Assert.Equal(3, calls);
        Assert.Equal(2, records);
        Assert.Equal(2, signatures);
        Assert.Equal(1L, await CountAsync(record.ExperienceId));
        Assert.Equal(1L, await CountAsync(silent.ExperienceId));
        Assert.Equal(0L, await CountAsync(unfailing.ExperienceId));

        // Idempotent: a second pass writes nothing.
        var again = await owner.BackfillFailureSignaturesAsync(auth, scope, batchSize: 10, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal((ExperienceStoreOutcome.Committed, 0, 0, false), (again.Outcome, again.RecordCount, again.SignatureCount, again.MoreRemain));

        var found = await new PostgresExperienceSignatureSource(fixture.DataSource).FindBySignatureAsync(
            auth, scope, Query(ExperienceFailureSignature.From("run_tests", Error)!), CancellationToken.None);
        Assert.Equal([record.ExperienceId], found.Candidates.Select(c => c.Record.ExperienceId));
    }

    [Fact]
    public async Task The_backfill_refuses_what_it_must()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);

        var denied = await _store.BackfillFailureSignaturesAsync(Authorize(NewTenant()), scope, 10, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);

        var invalid = await _store.BackfillFailureSignaturesAsync(Authorize(tenant), scope, 0, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Invalid, invalid.Outcome);
        Assert.NotEmpty(invalid.Errors);
    }

    [Fact]
    public async Task A_record_shared_by_a_grant_is_found_by_its_signature_from_the_recipient_scope_and_says_so()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = WithFailedCall(Minimal(owner, status: ExperienceStatus.Validated));
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        var notShared = WithFailedCall(Minimal(owner, status: ExperienceStatus.Validated));
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, notShared, CancellationToken.None)).Outcome);

        var grantId = Guid.NewGuid();
        var granted = await new PostgresExperienceGrantStore(fixture.DataSource).CreateAsync(
            auth,
            new GrantAdministration("sharing-administrator", DateTimeOffset.UtcNow),
            new ExperienceGrantRequest(grantId, record.ExperienceId, owner, recipient, "team-b owns the follow-up", DateTimeOffset.UtcNow.AddHours(1)),
            CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, granted.Outcome);

        var found = await new PostgresExperienceSignatureSource(fixture.DataSource).FindBySignatureAsync(
            auth, recipient, Query(ExperienceFailureSignature.From("run_tests", Error)!), CancellationToken.None);

        var candidate = Assert.Single(found.Candidates);
        Assert.Equal(record.ExperienceId, candidate.Record.ExperienceId);
        Assert.True(candidate.SharedByGrant);
        Assert.Equal(grantId, candidate.PermittingGrantId);
        Assert.NotNull(candidate.GrantDisclosure);
        Assert.Equal(ExperienceSignatureMatch.Exact, candidate.Match);
    }

    private static ExperienceSignatureQuery Query(ExperienceFailureSignature signature) =>
        new(signature, ExperienceStatuses.EligibleForReuse, 0d);

    private async Task<long> CountAsync(Guid experienceId)
    {
        await using var command = fixture.RawDataSource.CreateCommand(
            "SELECT count(*) FROM agent_experience.experience_failure_signatures WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task OwnerExecuteAsync(string sql)
    {
        await using var command = fixture.OwnerDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
