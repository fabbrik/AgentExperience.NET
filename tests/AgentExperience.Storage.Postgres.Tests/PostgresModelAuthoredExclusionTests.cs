using AgentExperience.Core.Retrieval;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 14.4 against a real PostgreSQL: <c>0021</c>'s <c>reflection_model_authored</c> flag is derived from every
/// plaintext write, written by the store for a sealed one, cleared by erasure, and backfilled from plaintext payloads
/// only; an excluding search leaves out
/// <c>true</c> before its limit, so retrieval fills its window with deterministic records, and keeps <c>NULL</c>, the
/// record sealed before <c>0021</c> that SQL cannot classify. The sealed cases construct their own encrypted components,
/// so they run in both suite modes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresModelAuthoredExclusionTests(PostgresFixture fixture)
{
    private const string Text = "refund ticket stuck on a lock";

    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    [Fact]
    public async Task Every_create_writes_the_flag_from_the_reflection_and_never_from_the_producer()
    {
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, producer: "AgentExperience.ChatClientExperienceReflector/1.0.0 (some-model)");
        var unreflected = Minimal(scope, status: ExperienceStatus.Validated);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(scope.TenantId), unreflected, CancellationToken.None)).Outcome);

        Assert.True(await FlagAsync(model));
        Assert.False(await FlagAsync(deterministic));
        Assert.False(await FlagAsync(unreflected.ExperienceId));
    }

    [Fact]
    public async Task A_sealed_record_carries_the_flag_in_the_clear_and_erasure_resets_it_to_the_fixed_false()
    {
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: EncryptionMode.Shared);
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        Assert.Equal(2, await ScalarAsync<int>($"SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = '{model}'"));
        Assert.True(await FlagAsync(model));

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(Authorize(scope.TenantId), scope, model, CancellationToken.None)).Outcome);

        Assert.False(await FlagAsync(model));
    }

    [Fact]
    public async Task A_plaintext_erasure_resets_the_flag_and_a_tombstone_cannot_be_flagged()
    {
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        Assert.Equal(1, await ScalarAsync<int>($"SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = '{model}'"));

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(Authorize(scope.TenantId), scope, model, CancellationToken.None)).Outcome);
        Assert.False(await FlagAsync(model));

        // The check binds even the owner: 0010's guard refuses any change to a tombstone first, so nothing can put one back.
        await using var command = fixture.OwnerDataSource.CreateCommand(
            $"UPDATE agent_experience.experience_records SET reflection_model_authored = true WHERE experience_id = '{model}'");
        await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Through_retrieval_five_model_authored_records_ranked_first_leave_the_limit_to_the_three_deterministic_ones()
    {
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var source = new PostgresExperienceCandidateSource(fixture.DataSource);
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(store, scope, ReflectionAuthorship.Model, strong: true);
        }

        var deterministic = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            deterministic.Add(await SeedAsync(store, scope, ReflectionAuthorship.Deterministic));
        }

        var retrieval = new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, new FrozenClock(ColumnTime));
        var request = new RetrieveExperienceRequest(Authorize(scope.TenantId), scope, Text, Limit: 3);

        var excluding = await retrieval.RetrieveAsync(request with { ExcludeModelAuthored = true });
        var including = await retrieval.RetrieveAsync(request);

        Assert.Equal(RetrievalOutcome.Completed, excluding.Outcome);
        Assert.Equal(deterministic.Order(), excluding.Records.Select(r => r.Record.ExperienceId).Order());
        Assert.Equal(3, including.Records.Count);
        Assert.All(including.Records, r => Assert.Equal(ReflectionAuthorship.Model, r.Record.Reflection!.Authorship));
    }

    [Fact]
    public async Task A_plaintext_row_s_flag_follows_its_payload_whatever_its_writer_supplies()
    {
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var record = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);

        // Even the owner cannot label a plaintext row against its own payload: the trigger derives it on every write.
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET reflection_model_authored = true WHERE experience_id = '{record}'");
        Assert.False(await FlagAsync(record));
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{{reflection,authorship}}', '\"Model\"') WHERE experience_id = '{record}'");
        Assert.True(await FlagAsync(record));
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE experience_id = '{record}'");
        Assert.True(await FlagAsync(record));

        // A payload version this schema does not know: read with the version-1 rule while it has that shape, and
        // model-authored (fail closed) once it does not.
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET payload_version = 3, payload = payload #- '{{reflection,authorship}}' WHERE experience_id = '{record}'");
        Assert.False(await FlagAsync(record));
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET payload = '{{\"opaque\": \"v3\"}}' WHERE experience_id = '{record}'");
        Assert.True(await FlagAsync(record));
    }

    [Fact]
    public async Task The_application_role_cannot_write_the_flag()
    {
        var scope = Scope(NewTenant());
        var record = await SeedAsync(new PostgresExperienceRecordStore(fixture.DataSource), scope, ReflectionAuthorship.Model);

        // Its column-level UPDATE does not name the flag -- in the two-role deployment, and behind row-level security.
        foreach (var dataSource in new[] { fixture.DataSource, fixture.RawDataSource })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                dataSource, $"UPDATE agent_experience.experience_records SET reflection_model_authored = false WHERE experience_id = '{record}'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }

        Assert.True(await FlagAsync(record));
    }

    [Fact]
    public async Task Sealing_a_flagged_plaintext_record_after_0021_keeps_its_flag()
    {
        var scope = Scope(NewTenant());
        var model = await SeedAsync(
            new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext), scope, ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(
            new PostgresExperienceRecordStore(fixture.DataSource, encryption: ExperienceEncryption.ForcePlaintext), scope, ReflectionAuthorship.Deterministic);
        var store = new PostgresExperienceRecordStore(fixture.DataSource, encryption: EncryptionMode.Shared);

        Assert.Equal(2, (await store.SealPlaintextRecordsAsync(Authorize(scope.TenantId), scope, 10, ScopeMatch.Exact, CancellationToken.None)).SealedCount);

        Assert.Equal(2, await ScalarAsync<int>($"SELECT payload_version FROM agent_experience.experience_records WHERE experience_id = '{model}'"));
        Assert.True(await FlagAsync(model));
        Assert.False(await FlagAsync(deterministic));
        var excluding = await SearchAsync(new PostgresExperienceCandidateSource(fixture.DataSource, encryption: EncryptionMode.Shared), scope, exclude: true);
        Assert.Equal([deterministic], Ids(excluding));
    }

    [Fact]
    public async Task A_model_authored_record_shared_by_a_grant_is_excluded_like_an_owned_one()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var reader = Scope(tenant, team: "team-b");
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var model = await SeedAsync(store, owner, ReflectionAuthorship.Model, strong: true);
        var deterministic = await SeedAsync(store, owner, ReflectionAuthorship.Deterministic);
        var grants = new PostgresExperienceGrantStore(fixture.DataSource);
        foreach (var id in new[] { model, deterministic })
        {
            var granted = await grants.CreateAsync(
                Authorize(tenant),
                new GrantAdministration("sharing-administrator", DateTimeOffset.UtcNow),
                new ExperienceGrantRequest(Guid.NewGuid(), id, owner, reader, "sibling team owns the follow-up", Micro(DateTimeOffset.UtcNow.AddHours(1))),
                CancellationToken.None);
            Assert.Equal(ExperienceGrantOutcome.Created, granted.Outcome);
        }

        var source = new PostgresExperienceCandidateSource(fixture.DataSource);
        var excluding = await SearchAsync(source, reader, exclude: true);
        var including = await SearchAsync(source, reader, exclude: false);

        Assert.Equal([deterministic], Ids(excluding));
        Assert.True(Assert.Single(excluding.Candidates).SharedByGrant);
        Assert.Equal(new[] { model, deterministic }.Order(), Ids(including));
    }

    [Fact]
    public async Task The_exact_scope_fallback_without_the_grant_table_is_still_filtered()
    {
        await using var dataSource = await fixture.CreateDatabaseAsync("authorship_nogrants");
        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        await ExecuteAsync(dataSource, "DROP TABLE agent_experience.experience_grants CASCADE");
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(dataSource);
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model, strong: true);
        var deterministic = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);

        var notices = new List<ExperienceGrantSupportNotice>();
        var source = new PostgresExperienceCandidateSource(dataSource, notices.Add);
        var excluding = await SearchAsync(source, scope, exclude: true);
        var including = await SearchAsync(source, scope, exclude: false);

        Assert.NotEmpty(notices);
        Assert.Equal([deterministic], Ids(excluding));
        Assert.Equal(new[] { model, deterministic }.Order(), Ids(including));
    }

    [Fact]
    public async Task The_backfill_classifies_every_plaintext_payload_and_fails_closed_on_an_unknown_value()
    {
        // A database before 0021, holding the payloads a story 14.3 store wrote -- the member only when it is not
        // Deterministic -- plus a hand-written "Deterministic", a value no published version writes, and a tombstone.
        await using var dataSource = await BeforeAuthorshipAsync("authorship_fill");
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var absent = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var unknown = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var jsonNull = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var lowercase = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var numeric = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var structured = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var unreflected = Minimal(scope, status: ExperienceStatus.Validated);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(scope.TenantId), unreflected, CancellationToken.None)).Outcome);
        await ExecuteAsync(dataSource, $"UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{{reflection,authorship}}', '\"Deterministic\"') WHERE experience_id = '{deterministic}'");
        await ExecuteAsync(dataSource, $"UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{{reflection,authorship}}', '\"Heuristic\"') WHERE experience_id = '{unknown}'");
        await ExecuteAsync(dataSource, $"UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{{reflection,authorship}}', 'null') WHERE experience_id = '{jsonNull}'");
        await ExecuteAsync(dataSource, $"UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{{reflection,authorship}}', '\"dETERMINISTIC\"') WHERE experience_id = '{lowercase}'");
        await ExecuteAsync(dataSource, $"UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{{reflection,authorship}}', '0') WHERE experience_id = '{numeric}'");
        await ExecuteAsync(dataSource, $"UPDATE agent_experience.experience_records SET payload = jsonb_set(payload, '{{reflection,authorship}}', '{{}}') WHERE experience_id = '{structured}'");
        var tombstone = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(Authorize(scope.TenantId), scope, tombstone, CancellationToken.None)).Outcome);

        var applied = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        Assert.Equal(PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName, applied.AppliedScripts[^1]);

        Assert.False(await FlagAsync(absent, dataSource));
        Assert.True(await FlagAsync(model, dataSource));
        Assert.False(await FlagAsync(deterministic, dataSource));
        Assert.True(await FlagAsync(unknown, dataSource));
        Assert.False(await FlagAsync(jsonNull, dataSource));
        Assert.False(await FlagAsync(lowercase, dataSource));
        Assert.True(await FlagAsync(numeric, dataSource));
        Assert.True(await FlagAsync(structured, dataSource));
        Assert.False(await FlagAsync(unreflected.ExperienceId, dataSource));
        Assert.False(await FlagAsync(tombstone, dataSource));

        // The reader agrees where the rule is lenient: both read back Deterministic.
        foreach (var lenient in new[] { jsonNull, lowercase })
        {
            var read = await store.GetAsync(Authorize(scope.TenantId), scope, lenient, CancellationToken.None);
            Assert.Equal(ReflectionAuthorship.Deterministic, read.Record!.Reflection!.Authorship);
        }

        // An excluding search leaves the backfilled model-authored records out, like freshly written ones. (A search
        // that returned the unknown value would fail to read it; that is the store's existing, loud, behaviour.)
        var source = new PostgresExperienceCandidateSource(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        Assert.Equal(new[] { absent, deterministic, jsonNull, lowercase }.Order(), Ids(await SearchAsync(source, scope, exclude: true)));
    }

    [Fact]
    public async Task A_record_sealed_before_0021_stays_unknown_and_an_excluding_search_still_returns_it()
    {
        // Before 0021, a plaintext model-authored record sealed by the upgrade job: the backfill cannot open it.
        await using var dataSource = await BeforeAuthorshipAsync("authorship_sealed");
        var scope = Scope(NewTenant());
        var sealedBefore = await SeedAsync(
            new PostgresExperienceRecordStore(dataSource, encryption: ExperienceEncryption.ForcePlaintext), scope, ReflectionAuthorship.Model);
        var store = new PostgresExperienceRecordStore(dataSource, encryption: EncryptionMode.Shared);
        Assert.Equal(1, (await store.SealPlaintextRecordsAsync(Authorize(scope.TenantId), scope, 10, ScopeMatch.Exact, CancellationToken.None)).SealedCount);

        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        Assert.Null(await FlagAsync(sealedBefore, dataSource));

        // Written after 0021 and sealed: the store says what the database cannot read.
        var sealedAfter = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        Assert.True(await FlagAsync(sealedAfter, dataSource));
        Assert.False(await FlagAsync(deterministic, dataSource));

        var source = new PostgresExperienceCandidateSource(dataSource, encryption: EncryptionMode.Shared);
        var excluding = await SearchAsync(source, scope, exclude: true);

        // The documented residual: the unknown record is still a candidate -- its consumer drops it once it is opened,
        // and here it opens as model-authored -- while the one the store flagged is left out.
        Assert.Equal(new[] { sealedBefore, deterministic }.Order(), Ids(excluding));
        Assert.Equal(ReflectionAuthorship.Model, excluding.Candidates.Single(c => c.Record.ExperienceId == sealedBefore).Record.Reflection!.Authorship);
        Assert.Equal(new[] { sealedBefore, sealedAfter, deterministic }.Order(), Ids(await SearchAsync(source, scope, exclude: false)));

        // Through the retrieval service the unknown record is opened and excluded, so it never reaches the result.
        var retrieved = await new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, new FrozenClock(ColumnTime))
            .RetrieveAsync(new RetrieveExperienceRequest(Authorize(scope.TenantId), scope, Text) { ExcludeModelAuthored = true });
        Assert.Equal([deterministic], retrieved.Records.Select(r => r.Record.ExperienceId));
        Assert.Equal(new ExcludedExperience(sealedBefore, RetrievalExclusionReason.ModelAuthored), Assert.Single(retrieved.Excluded));
    }

    [Fact]
    public void The_script_adds_the_column_grants_nothing_builds_no_index_and_is_applied_last()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName);
        var statements = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.Contains("ADD COLUMN IF NOT EXISTS reflection_model_authored boolean NULL", statements, StringComparison.Ordinal);
        Assert.Contains("NOT VALID;", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE INDEX", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY DEFINER", statements, StringComparison.Ordinal);
        Assert.Contains("BEFORE INSERT OR UPDATE ON agent_experience.experience_records", statements, StringComparison.Ordinal);
        Assert.Equal("reflection_model_authored IS NOT TRUE", PostgresExperienceCandidateSource.ModelAuthoredPredicate);
        Assert.Equal(PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName, PostgresExperienceRecordSchema.ScriptNames[^1]);
    }

    private static async Task<Guid> SeedAsync(
        PostgresExperienceRecordStore store,
        Scope scope,
        ReflectionAuthorship authorship,
        bool strong = false,
        string producer = "tests")
    {
        var runId = Guid.NewGuid();
        var record = Minimal(scope, status: ExperienceStatus.Validated) with
        {
            SourceRunId = runId,
            // Every term in the task ID too makes a record a stronger text match than one with them in its summary only.
            TaskId = strong ? "refund-ticket-stuck-lock" : "triage",
            TaskSummary = "A refund ticket stuck on a lock",
            ReuseConfidence = 0.75,
            Reflection = new Reflection(
                Guid.NewGuid(), runId, "Check the lock table before retrying.", [], [], [], [], null, [],
                TaskVerificationStatus.Verified, 1, "v1", producer, ColumnTime) { Authorship = authorship },
        };

        var created = await store.CreateAsync(Authorize(scope.TenantId), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        return record.ExperienceId;
    }

    private static Task<ExperienceCandidateSearchResult> SearchAsync(PostgresExperienceCandidateSource source, Scope scope, bool exclude) =>
        source.SearchAsync(
            Authorize(scope.TenantId),
            new ExperienceCandidateQuery(scope, Text, Eligible, 0d) { ExcludeModelAuthored = exclude },
            CancellationToken.None);

    private static Guid[] Ids(ExperienceCandidateSearchResult result) =>
        [.. result.Candidates.Select(candidate => candidate.Record.ExperienceId).Order()];

    /// <summary>
    /// A fresh database with every script before <c>0021</c> applied by hand, as the superuser, and nothing journaled:
    /// the schema a <c>0.1.0-preview.5</c> host runs. Today's plaintext insert names no <c>0021</c> column, so today's
    /// store writes into it; the migrator then re-applies the idempotent scripts and applies <c>0021</c>.
    /// </summary>
    private async Task<NpgsqlDataSource> BeforeAuthorshipAsync(string purpose)
    {
        var dataSource = await fixture.CreateDatabaseAsync(purpose);
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames
            .TakeWhile(name => !string.Equals(name, PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName, StringComparison.Ordinal)))
        {
            await ExecuteAsync(dataSource, PostgresExperienceRecordSchema.GetScript(scriptName));
        }

        return dataSource;
    }

    /// <summary>The flag as stored, read through the tests' own SQL role, or through <paramref name="dataSource"/>.</summary>
    private async Task<bool?> FlagAsync(Guid experienceId, NpgsqlDataSource? dataSource = null)
    {
        await using var command = (dataSource ?? fixture.RawDataSource).CreateCommand(
            $"SELECT reflection_model_authored FROM agent_experience.experience_records WHERE experience_id = '{experienceId}'");
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (bool)value;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = fixture.RawDataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static DateTimeOffset Micro(DateTimeOffset value) => new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
