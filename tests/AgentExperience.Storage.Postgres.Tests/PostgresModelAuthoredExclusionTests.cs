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

    /// <summary>What the library's ChatClientExperienceReflector wrote before it declared authorship (story 17.1).</summary>
    private const string LegacyProducer = "AgentExperience.ChatClientExperienceReflector/1.0.0 (some-model)";

    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    [Fact]
    public async Task Every_create_writes_the_flag_by_the_shared_rule_reading_only_the_library_s_own_producer()
    {
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(fixture.DataSource);
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        var legacy = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, producer: LegacyProducer);
        var thirdParty = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, producer: "Contoso.ModelReflector/1.0 (some-model)");
        var unreflected = Minimal(scope, status: ExperienceStatus.Validated);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(scope.TenantId), unreflected, CancellationToken.None)).Outcome);

        // Story 17.1: in both suite modes -- 0022's function for a plaintext row, the store's rule for a sealed one.
        Assert.True(await FlagAsync(model));
        Assert.True(await FlagAsync(legacy));
        Assert.False(await FlagAsync(thirdParty));
        Assert.False(await FlagAsync(unreflected.ExperienceId));
        var excluding = Ids(await SearchAsync(new PostgresExperienceCandidateSource(fixture.DataSource), scope, exclude: true));
        Assert.Contains(thirdParty, excluding);
        Assert.DoesNotContain(legacy, excluding);
        Assert.DoesNotContain(model, excluding);
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
        Assert.Contains(PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName, applied.AppliedScripts);

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
    public async Task A_record_sealed_before_0021_is_left_out_by_an_excluding_search_until_the_owner_s_backfill_flags_it()
    {
        // Before 0021, a plaintext model-authored record and a deterministic one, sealed by the upgrade job: the
        // migration cannot open them.
        await using var dataSource = await BeforeAuthorshipAsync("authorship_sealed");
        var scope = Scope(NewTenant());
        var plain = new PostgresExperienceRecordStore(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var sealedModel = await SeedAsync(plain, scope, ReflectionAuthorship.Model);
        var sealedDeterministic = await SeedAsync(plain, scope, ReflectionAuthorship.Deterministic);
        var store = new PostgresExperienceRecordStore(dataSource, encryption: EncryptionMode.Shared);
        Assert.Equal(2, (await store.SealPlaintextRecordsAsync(Authorize(scope.TenantId), scope, 10, ScopeMatch.Exact, CancellationToken.None)).SealedCount);

        await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        Assert.Null(await FlagAsync(sealedModel, dataSource));
        Assert.Null(await FlagAsync(sealedDeterministic, dataSource));

        // Written after 0021 and sealed: the store says what the database cannot read.
        var sealedAfter = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        Assert.True(await FlagAsync(sealedAfter, dataSource));
        Assert.False(await FlagAsync(deterministic, dataSource));

        // Story 17.1: unknown fails closed -- both unflagged rows are left out before the limit, and take no place in
        // the window; without the exclusion nothing changes.
        var source = new PostgresExperienceCandidateSource(dataSource, encryption: EncryptionMode.Shared);
        Assert.Equal([deterministic], Ids(await SearchAsync(source, scope, exclude: true)));
        Assert.Equal(new[] { sealedModel, sealedDeterministic, sealedAfter, deterministic }.Order(), Ids(await SearchAsync(source, scope, exclude: false)));
        var retrieval = new ExperienceRetrievalService(source, RetrievalPolicy.Default, RankingWeights.Default, new FrozenClock(ColumnTime));
        var retrieved = await retrieval.RetrieveAsync(new RetrieveExperienceRequest(Authorize(scope.TenantId), scope, Text) { ExcludeModelAuthored = true });
        Assert.Equal([deterministic], retrieved.Records.Select(r => r.Record.ExperienceId));
        Assert.Empty(retrieved.Excluded);

        // The owner's backfill opens each and writes its flag; the deterministic one is found again.
        var backfill = await store.BackfillSealedAuthorshipAsync(Authorize(scope.TenantId), scope, 10, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal((ExperienceStoreOutcome.Committed, 2, false), (backfill.Outcome, backfill.SetCount, backfill.MoreRemain));
        Assert.Empty(backfill.Errors);
        Assert.True(await FlagAsync(sealedModel, dataSource));
        Assert.False(await FlagAsync(sealedDeterministic, dataSource));
        Assert.Equal(new[] { sealedDeterministic, deterministic }.Order(), Ids(await SearchAsync(source, scope, exclude: true)));
    }

    [Fact]
    public async Task The_owner_s_backfill_flags_unknown_sealed_rows_in_batches_leaves_a_shredded_one_alone_and_then_finds_nothing()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant, team: "team-a");
        var shreddedScope = Scope(tenant, team: "team-b");
        var root = Scope(tenant);
        var auth = Authorize(tenant);
        var writer = new PostgresExperienceRecordStore(fixture.DataSource, encryption: EncryptionMode.Shared);
        var model = await SeedAsync(writer, scope, ReflectionAuthorship.Model);
        var deterministic = await SeedAsync(writer, scope, ReflectionAuthorship.Deterministic);
        var legacy = await SeedAsync(writer, scope, ReflectionAuthorship.Deterministic, producer: LegacyProducer);
        var unreflected = Minimal(scope, status: ExperienceStatus.Validated);
        Assert.Equal(ExperienceStoreOutcome.Created, (await writer.CreateAsync(auth, unreflected, CancellationToken.None)).Outcome);
        var shredded = await SeedAsync(writer, shreddedScope, ReflectionAuthorship.Deterministic);
        var flaggedFalse = await SeedAsync(writer, scope, ReflectionAuthorship.Deterministic);

        // Unknown, as rows sealed before 0021 or by an instance on an earlier build. A flag already written is never
        // revisited, so the last record keeps its false.
        var unknown = new[] { model, deterministic, legacy, unreflected.ExperienceId, shredded };
        await ExecuteAsync(
            fixture.OwnerDataSource,
            $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE experience_id IN ({string.Join(", ", unknown.Select(id => $"'{id}'"))})");
        await EncryptionMode.Shared.KeyStore.DestroyKeyAsync(new ExperienceKeyReference(shredded, shreddedScope), CancellationToken.None);

        var source = new PostgresExperienceCandidateSource(fixture.DataSource, encryption: EncryptionMode.Shared);
        Assert.Equal([flaggedFalse], Ids(await SearchAsync(source, scope, exclude: true)));

        // Not the application role: it holds no UPDATE on the flag, so the write is refused and nothing is set.
        var refused = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => writer.BackfillSealedAuthorshipAsync(auth, scope, 10, ScopeMatch.Exact, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, Assert.IsType<PostgresException>(refused.InnerException).SqlState);
        Assert.Null(await FlagAsync(model));

        var owner = new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: EncryptionMode.Shared);
        var first = await owner.BackfillSealedAuthorshipAsync(auth, scope, 3, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal((ExperienceStoreOutcome.Committed, 3, 0, true), (first.Outcome, first.SetCount, first.SkippedCount, first.MoreRemain));
        var second = await owner.BackfillSealedAuthorshipAsync(auth, scope, 3, ScopeMatch.Exact, first.ResumeAfter, CancellationToken.None);
        Assert.Equal((ExperienceStoreOutcome.Committed, 1, 0, false), (second.Outcome, second.SetCount, second.SkippedCount, second.MoreRemain));

        Assert.True(await FlagAsync(model));
        Assert.False(await FlagAsync(deterministic));
        Assert.True(await FlagAsync(legacy));
        Assert.False(await FlagAsync(unreflected.ExperienceId));
        Assert.False(await FlagAsync(flaggedFalse));

        // Across the subtree: the shredded record is erased as far as anyone can read, so it is left alone.
        var subtree = await owner.BackfillSealedAuthorshipAsync(auth, root, 10, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal((ExperienceStoreOutcome.Committed, 0, 1, false, shredded), (subtree.Outcome, subtree.SetCount, subtree.SkippedCount, subtree.MoreRemain, subtree.ResumeAfter));
        Assert.Null(await FlagAsync(shredded));
        Assert.Equal(0, (await owner.BackfillSealedAuthorshipAsync(auth, root, 10, ScopeMatch.Subtree, CancellationToken.None)).SetCount);

        Assert.Equal(new[] { deterministic, flaggedFalse }.Order(), Ids(await SearchAsync(source, scope, exclude: true)));
    }

    [Fact]
    public async Task The_backfill_writes_the_flag_only_and_leaves_revision_payload_and_timestamps_alone()
    {
        var scope = Scope(NewTenant());
        var writer = new PostgresExperienceRecordStore(fixture.DataSource, encryption: EncryptionMode.Shared);
        var id = await SeedAsync(writer, scope, ReflectionAuthorship.Model);
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE experience_id = '{id}'");
        var before = await RowTextAsync(id);

        var result = await new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: EncryptionMode.Shared)
            .BackfillSealedAuthorshipAsync(Authorize(scope.TenantId), scope, 10, ScopeMatch.Exact, CancellationToken.None);

        Assert.Equal(1, result.SetCount);
        Assert.True(await FlagAsync(id));
        Assert.Equal(before, await RowTextAsync(id));
    }

    [Fact]
    public async Task Destroyed_key_rows_sorting_first_do_not_stall_the_backfill_which_resumes_past_them()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var auth = Authorize(tenant);
        var writer = new PostgresExperienceRecordStore(fixture.DataSource, encryption: EncryptionMode.Shared);
        var shredded = new[] { await SeedAsync(writer, scope, ReflectionAuthorship.Deterministic, id: Sorted("00")), await SeedAsync(writer, scope, ReflectionAuthorship.Deterministic, id: Sorted("01")) };
        var later = new[] { await SeedAsync(writer, scope, ReflectionAuthorship.Model, id: Sorted("fe")), await SeedAsync(writer, scope, ReflectionAuthorship.Deterministic, id: Sorted("ff")) };
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE tenant_id = '{tenant}'");
        foreach (var id in shredded)
        {
            await EncryptionMode.Shared.KeyStore.DestroyKeyAsync(new ExperienceKeyReference(id, scope), CancellationToken.None);
        }

        var owner = new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: EncryptionMode.Shared);
        var first = await owner.BackfillSealedAuthorshipAsync(auth, scope, 2, ScopeMatch.Exact, CancellationToken.None);
        Assert.Equal((0, 2, true, shredded[1]), (first.SetCount, first.SkippedCount, first.MoreRemain, first.ResumeAfter));

        var second = await owner.BackfillSealedAuthorshipAsync(auth, scope, 2, ScopeMatch.Exact, first.ResumeAfter, CancellationToken.None);
        Assert.Equal((2, 0, false, later[1]), (second.SetCount, second.SkippedCount, second.MoreRemain, second.ResumeAfter));
        Assert.True(await FlagAsync(later[0]));
        Assert.False(await FlagAsync(later[1]));
        Assert.Null(await FlagAsync(shredded[0]));

        // Past the end of the worklist: nothing examined, the cursor kept.
        var past = await owner.BackfillSealedAuthorshipAsync(auth, scope, 2, ScopeMatch.Exact, second.ResumeAfter, CancellationToken.None);
        Assert.Equal((0, 0, false, later[1]), (past.SetCount, past.SkippedCount, past.MoreRemain, past.ResumeAfter));
    }

    [Fact]
    public async Task A_row_whose_payload_cannot_be_opened_is_skipped_and_counted_and_the_batch_goes_on()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var writer = new PostgresExperienceRecordStore(fixture.DataSource, encryption: EncryptionMode.Shared);
        var broken = await SeedAsync(writer, scope, ReflectionAuthorship.Deterministic, id: Sorted("00"));
        var donor = await SeedAsync(writer, scope, ReflectionAuthorship.Model, id: Sorted("ff"));

        // Well-formed, but sealed under another record's key and associated data: it cannot be opened as this record.
        await ExecuteAsync(
            fixture.OwnerDataSource,
            $"UPDATE agent_experience.experience_records SET payload = (SELECT payload FROM agent_experience.experience_records WHERE experience_id = '{donor}') WHERE experience_id = '{broken}'");
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET reflection_model_authored = NULL WHERE tenant_id = '{tenant}'");

        var result = await new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: EncryptionMode.Shared)
            .BackfillSealedAuthorshipAsync(Authorize(tenant), scope, 10, ScopeMatch.Exact, CancellationToken.None);

        Assert.Equal((ExperienceStoreOutcome.Committed, 1, 1, false), (result.Outcome, result.SetCount, result.SkippedCount, result.MoreRemain));
        Assert.Null(await FlagAsync(broken));
        Assert.True(await FlagAsync(donor));
    }

    [Theory]
    [InlineData("bare prefix", "'\"AgentExperience.ChatClientExperienceReflector/\"'", null)]
    [InlineData("prefix and suffix", "'\"AgentExperience.ChatClientExperienceReflector/1.0.0 (some-model)\"'", null)]
    [InlineData("lookalike case", "'\"agentexperience.chatclientexperiencereflector/1.0.0\"'", null)]
    [InlineData("prefix without its slash", "'\"AgentExperience.ChatClientExperienceReflector\"'", null)]
    [InlineData("third party", "'\"Contoso.ModelReflector/1.0\"'", null)]
    [InlineData("prefix and Model", "'\"AgentExperience.ChatClientExperienceReflector/1.0.0\"'", "'\"Model\"'")]
    [InlineData("non-string producer", "'5'", null)]
    [InlineData("object producer", "'{}'", null)]
    [InlineData("missing producer, malformed authorship", null, "'7'")]
    [InlineData("missing producer, unknown authorship", null, "'\"Heuristic\"'")]
    [InlineData("null producer", "'null'", null)]
    [InlineData("missing producer", null, null)]
    public async Task The_SQL_rule_and_the_shared_C_sharp_rule_agree_on_every_payload_shape(string name, string? producer, string? authorship)
    {
        _ = name;
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var id = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic);
        var payload = "payload";
        payload = producer is null ? $"({payload} #- '{{reflection,producer}}')" : $"jsonb_set({payload}, '{{reflection,producer}}', {producer})";
        if (authorship is not null)
        {
            payload = $"jsonb_set({payload}, '{{reflection,authorship}}', {authorship})";
        }

        // The trigger classifies the rewritten payload with 0022's function; the store reads it back with today's reader.
        await ExecuteAsync(fixture.OwnerDataSource, $"UPDATE agent_experience.experience_records SET payload = {payload} WHERE experience_id = '{id}'");
        var sql = await FlagAsync(id);

        ExperienceRecord? read = null;
        try
        {
            read = (await store.GetAsync(Authorize(scope.TenantId), scope, id, CancellationToken.None)).Record;
        }
        catch (ExperienceStoreException)
        {
            // Unreadable by the reader: SQL must fail closed.
        }

        Assert.Equal(read is null || ReflectionAuthorshipRule.IsModelAuthored(read.Reflection), sql);
    }

    [Fact]
    public async Task The_backfill_refuses_bad_arguments_and_foreign_scopes_before_touching_storage()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var owner = new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: EncryptionMode.Shared);

        var plaintext = await new PostgresExperienceRecordStore(fixture.OwnerDataSource, encryption: ExperienceEncryption.ForcePlaintext)
            .BackfillSealedAuthorshipAsync(Authorize(tenant), scope, 10, ScopeMatch.Exact, CancellationToken.None);
        var tooBig = await owner.BackfillSealedAuthorshipAsync(Authorize(tenant), scope, PostgresExperienceRecordStore.MaxSweepBatchSize + 1, ScopeMatch.Exact, CancellationToken.None);
        var denied = await owner.BackfillSealedAuthorshipAsync(Authorize(NewTenant()), scope, 10, ScopeMatch.Exact, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, plaintext.Outcome);
        Assert.Contains(plaintext.Errors, error => error.Path == "Encryption");
        Assert.Equal(ExperienceStoreOutcome.Invalid, tooBig.Outcome);
        Assert.Contains(tooBig.Errors, error => error.Path == "BatchSize");
        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
        await Assert.ThrowsAsync<ArgumentNullException>(() => owner.BackfillSealedAuthorshipAsync(null!, scope, 10, ScopeMatch.Exact, CancellationToken.None));
    }

    [Fact]
    public async Task Migration_0022_flags_the_library_reflector_s_plaintext_records_and_changes_nothing_when_re_run()
    {
        // A database at 0021, holding plaintext records 0021's function reads as deterministic.
        await using var dataSource = await BeforeScriptAsync("authorship_0022", PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName);
        var scope = Scope(NewTenant());
        var store = new PostgresExperienceRecordStore(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var legacy = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, producer: LegacyProducer);
        var thirdParty = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, producer: "Contoso.ModelReflector/1.0 (some-model)");
        var lookalike = await SeedAsync(store, scope, ReflectionAuthorship.Deterministic, producer: "agentexperience.chatclientexperiencereflector/1.0.0 (some-model)");
        var model = await SeedAsync(store, scope, ReflectionAuthorship.Model);
        Assert.False(await FlagAsync(legacy, dataSource));

        var applied = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        Assert.Equal(PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName, applied.AppliedScripts[^1]);

        Assert.True(await FlagAsync(legacy, dataSource));
        Assert.False(await FlagAsync(thirdParty, dataSource));
        Assert.False(await FlagAsync(lookalike, dataSource));
        Assert.True(await FlagAsync(model, dataSource));

        // Idempotent: the script again rewrites no row.
        await using var rerun = dataSource.CreateCommand(PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName));
        Assert.Equal(0, await rerun.ExecuteNonQueryAsync());
        Assert.True(await FlagAsync(legacy, dataSource));
        Assert.False(await FlagAsync(thirdParty, dataSource));

        var source = new PostgresExperienceCandidateSource(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        Assert.Equal(new[] { thirdParty, lookalike }.Order(), Ids(await SearchAsync(source, scope, exclude: true)));
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
        Assert.Equal("reflection_model_authored IS FALSE", PostgresExperienceCandidateSource.ModelAuthoredPredicate);
        Assert.Equal(PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName, PostgresExperienceRecordSchema.ScriptNames[^2]);
    }

    [Fact]
    public void Script_0022_adds_nothing_grants_nothing_and_is_applied_last()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName);
        var statements = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.Contains("CREATE OR REPLACE FUNCTION agent_experience.payload_reflection_model_authored(p_payload jsonb, p_payload_version integer)", statements, StringComparison.Ordinal);
        Assert.Contains("'AgentExperience.ChatClientExperienceReflector/'", statements, StringComparison.Ordinal);
        Assert.Contains($"left(p_payload -> 'reflection' ->> 'producer', {"AgentExperience.ChatClientExperienceReflector/".Length})", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY DEFINER", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD COLUMN", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("TRIGGER", statements, StringComparison.Ordinal);
        Assert.Equal(PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName, PostgresExperienceRecordSchema.ScriptNames[^1]);
    }

    private static async Task<Guid> SeedAsync(
        PostgresExperienceRecordStore store,
        Scope scope,
        ReflectionAuthorship authorship,
        bool strong = false,
        string producer = "tests",
        Guid? id = null)
    {
        var runId = Guid.NewGuid();
        var record = Minimal(scope, id: id, status: ExperienceStatus.Validated) with
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
    private Task<NpgsqlDataSource> BeforeAuthorshipAsync(string purpose) =>
        BeforeScriptAsync(purpose, PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName);

    /// <summary>A fresh database with every script before <paramref name="firstMissing"/> applied by hand, and nothing journaled.</summary>
    private async Task<NpgsqlDataSource> BeforeScriptAsync(string purpose, string firstMissing)
    {
        var dataSource = await fixture.CreateDatabaseAsync(purpose);
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames
            .TakeWhile(name => !string.Equals(name, firstMissing, StringComparison.Ordinal)))
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

    /// <summary>A fresh ID whose first byte is <paramref name="firstByte"/>, so PostgreSQL's uuid order puts it where the test needs it.</summary>
    private static Guid Sorted(string firstByte) => Guid.Parse(firstByte + Guid.NewGuid().ToString("N")[2..]);

    /// <summary>Everything about a row but its authorship flag, as text.</summary>
    private Task<string> RowTextAsync(Guid id) => ScalarAsync<string>(
        $"SELECT (to_jsonb(r) - 'reflection_model_authored')::text FROM agent_experience.experience_records r WHERE experience_id = '{id}'");

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
