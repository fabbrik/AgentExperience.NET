using System.Text.Json;
using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 28.4 against a real PostgreSQL container, in whichever suite mode runs (plaintext, or encrypted with
/// <c>AGENTEXPERIENCE_TEST_ENCRYPTION=on</c>): a tool call's recipe excerpt round-trips in the payload, is absent from it
/// when unset, and a grant's recipe-excerpt consent (<c>0031</c>) is stored, validated, read back on the grant and on a
/// borrowed read, and immutable.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRecipeExcerptTests(PostgresFixture fixture)
{
    private const string Administrator = "sharing-administrator";

    private readonly PostgresExperienceRecordStore _store = new(fixture.DataSource);
    private readonly PostgresExperienceGrantStore _grants = new(fixture.DataSource);

    private static ExperienceRecord WithExcerpt(Scope scope, string? excerpt)
    {
        var full = Full(scope);
        var attempt = full.Attempts[0];
        return full with { Attempts = [attempt with { ToolCalls = [attempt.ToolCalls[0] with { RecipeExcerpt = excerpt }, attempt.ToolCalls[1]] }, full.Attempts[1]] };
    }

    [Fact]
    public async Task A_recipe_excerpt_round_trips_and_an_unset_one_is_absent_from_the_payload()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var with = WithExcerpt(scope, "pinned lib to 2.4.1 \U0001F600");
        var without = WithExcerpt(scope, null);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(Authorize(tenant), with, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(Authorize(tenant), without, CancellationToken.None)).Outcome);

        var read = (await _store.GetAsync(Authorize(tenant), scope, with.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal("pinned lib to 2.4.1 \U0001F600", read.Attempts[0].ToolCalls[0].RecipeExcerpt);
        Assert.Null(read.Attempts[0].ToolCalls[1].RecipeExcerpt);
        Assert.Equal(Canonical(with), Canonical(read));

        var plain = (await _store.GetAsync(Authorize(tenant), scope, without.ExperienceId, CancellationToken.None)).Record!;
        Assert.All(plain.Attempts.SelectMany(a => a.ToolCalls), call => Assert.Null(call.RecipeExcerpt));

        if (!EncryptionMode.IsOn)
        {
            // Plaintext: the member is in the stored payload only when set.
            Assert.Contains("recipeExcerpt", await PayloadAsync(with.ExperienceId), StringComparison.Ordinal);
            Assert.DoesNotContain("recipeExcerpt", await PayloadAsync(without.ExperienceId), StringComparison.Ordinal);
        }
        else
        {
            // Encrypted: sealed with the rest of the payload, so its text is nowhere in the row.
            Assert.DoesNotContain("pinned lib", await RowTextAsync(with.ExperienceId), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(ExperienceGrantDisclosure.LessonAndApproach)]
    [InlineData(ExperienceGrantDisclosure.LessonApproachAndArguments)]
    public async Task A_grants_recipe_consent_is_stored_listed_and_read_back_on_a_borrowed_read(ExperienceGrantDisclosure level)
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = WithExcerpt(owner, "pinned lib to 2.4.1");
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var request = Request(record.ExperienceId, owner, recipient, level) with { RecipeExcerpts = ["search_docs", "update_ticket"] };
        var created = await _grants.CreateAsync(Authorize(tenant), Admin(), request, CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, created.Outcome);
        Assert.Equal(["search_docs", "update_ticket"], created.Grant!.RecipeExcerpts);

        var listed = Assert.Single((await _grants.ListAsync(Authorize(tenant), owner, record.ExperienceId, CancellationToken.None)).Grants);
        Assert.Equal(["search_docs", "update_ticket"], listed.RecipeExcerpts);

        var borrowed = await _store.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None);
        Assert.True(borrowed.SharedByGrant);
        Assert.Equal(["search_docs", "update_ticket"], borrowed.GrantRecipeExcerpts);
        Assert.Equal("pinned lib to 2.4.1", borrowed.Record!.Attempts[0].ToolCalls[0].RecipeExcerpt);

        var batch = await _store.GetManyAsync(Authorize(tenant), recipient, [record.ExperienceId], new ExperienceReadOptions(), CancellationToken.None);
        Assert.Equal(["search_docs", "update_ticket"], Assert.Single(batch.Results).GrantRecipeExcerpts);

        // The owner's own read names no grant, so no consent.
        Assert.Null((await _store.GetAsync(Authorize(tenant), owner, record.ExperienceId, CancellationToken.None)).GrantRecipeExcerpts);

        // Immutable: the monotonicity trigger refuses a change in place.
        await using var connection = await fixture.OwnerDataSource.OpenConnectionAsync();
        await using var update = new NpgsqlCommand(
            "UPDATE agent_experience.experience_grants SET recipe_excerpts = '[\"other\"]'::jsonb WHERE grant_id = @grant_id", connection);
        update.Parameters.AddWithValue("grant_id", created.Grant.GrantId);
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task A_grant_without_consent_reads_none_and_a_malformed_or_LessonOnly_consent_is_invalid()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = WithExcerpt(owner, "pinned lib to 2.4.1");
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var plain = await _grants.CreateAsync(Authorize(tenant), Admin(), Request(record.ExperienceId, owner, recipient, ExperienceGrantDisclosure.LessonAndApproach), CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, plain.Outcome);
        Assert.Null(plain.Grant!.RecipeExcerpts);
        Assert.Null((await _store.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None)).GrantRecipeExcerpts);

        foreach (var (level, tools) in new (ExperienceGrantDisclosure, string[])[]
        {
            (ExperienceGrantDisclosure.LessonOnly, ["search_docs"]),
            (ExperienceGrantDisclosure.LessonAndApproach, []),
            (ExperienceGrantDisclosure.LessonAndApproach, ["a", "a"]),
            (ExperienceGrantDisclosure.LessonAndApproach, [" "]),
            (ExperienceGrantDisclosure.LessonAndApproach, ["bad\ntool"]),
        })
        {
            var refused = await _grants.CreateAsync(
                Authorize(tenant),
                Admin(),
                Request(record.ExperienceId, owner, Scope(tenant, team: "team-c"), level) with { RecipeExcerpts = tools },
                CancellationToken.None);
            Assert.Equal(ExperienceGrantOutcome.Invalid, refused.Outcome);
            Assert.Equal("RecipeExcerpts", Assert.Single(refused.Errors).Path);
        }
    }

    [Fact]
    public async Task A_lone_surrogate_excerpt_captured_unsigned_is_stored_as_U_FFFD_and_reads_back()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var capture = new AgentExperience.Core.Capture.InMemoryExperienceCaptureService(
            new AgentExperience.Core.Sanitization.DefaultSanitizer(AgentExperience.Core.DependencyInjection.AgentExperienceDefaults.Sanitization),
            AgentExperience.Core.DependencyInjection.AgentExperienceDefaults.CaptureLimits);
        var runId = Guid.NewGuid();
        capture.StartRun(runId, "task-1", "a task", scope, new EnvironmentFingerprint("h", "r", "o", null, new Dictionary<string, string>()), new Provenance("t", null, PayloadTime, null), PayloadTime);
        var raw = new AgentExperience.Core.Capture.RawToolCall(Guid.NewGuid(), "pin_dependency", new Dictionary<string, object?>(), PayloadTime, TimeSpan.Zero, "ok", null)
        {
            RecipeExcerpt = "pinned \uD83D lib",
        };
        await capture.AppendAttemptAsync(runId, new AgentExperience.Core.Capture.AppendAttemptRequest(Guid.NewGuid(), PayloadTime, TimeSpan.Zero, [raw], "done", null));
        Assert.True(capture.TryGetRun(runId, out var run));
        var call = run!.Attempts[0].ToolCalls[0];
        Assert.Equal("pinned \uFFFD lib", call.RecipeExcerpt);

        var full = Full(scope);
        var record = full with { Attempts = [full.Attempts[0] with { ToolCalls = [call, full.Attempts[0].ToolCalls[1]] }, full.Attempts[1]] };
        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);
        var read = (await _store.GetAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal("pinned \uFFFD lib", read.Attempts[0].ToolCalls[0].RecipeExcerpt);
    }

    [Theory]
    [InlineData("LessonOnly", "[\"run_tests\"]")]
    [InlineData("LessonAndApproach", "[]")]
    [InlineData("LessonAndApproach", "{\"run_tests\": true}")]
    [InlineData("LessonAndApproach", "[\"run_tests\", 3]")]
    [InlineData("LessonAndApproach", "\"run_tests\"")]
    public async Task The_0031_checks_refuse_a_consent_under_LessonOnly_or_of_the_wrong_shape(string disclosure, string json)
    {
        var (grantId, owner) = await RevokedGrantAsync();
        var refused = await Assert.ThrowsAsync<PostgresException>(() => CopyGrantAsync(grantId, disclosure, json));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        Assert.Contains("recipe_excerpts", refused.ConstraintName, StringComparison.Ordinal);

        // The same row with a well-formed consent under a level that shows the approach is accepted.
        await CopyGrantAsync(grantId, "LessonAndApproach", "[\"run_tests\"]");
        _ = owner;
    }

    /// <summary>Issues a LessonAndApproach grant and revokes it, so a copy of its row may be live again.</summary>
    private async Task<(Guid GrantId, Scope Owner)> RevokedGrantAsync()
    {
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var record = WithExcerpt(owner, "x");
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var created = await _grants.CreateAsync(Authorize(tenant), Admin(), Request(record.ExperienceId, owner, Scope(tenant, team: "team-b"), ExperienceGrantDisclosure.LessonAndApproach), CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, created.Outcome);
        await _grants.RevokeAsync(Authorize(tenant), Admin(), new ExperienceGrantRevocation(created.Grant!.GrantId, owner, "ended"), CancellationToken.None);
        return (created.Grant.GrantId, owner);
    }

    /// <summary>Inserts, as the owner, a live copy of a grant row under a new ID with the given level and consent.</summary>
    private async Task CopyGrantAsync(Guid grantId, string disclosure, string recipeExcerpts)
    {
        const string Columns = "experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, recipient_tenant_id, " +
            "recipient_application_id, recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, reason, " +
            "administrator_principal_id, issued_at, expires_at";
        await using var connection = await fixture.OwnerDataSource.OpenConnectionAsync();
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO agent_experience.experience_grants (grant_id, {Columns}, disclosure, recipe_excerpts) " +
            $"SELECT gen_random_uuid(), {Columns}, @disclosure, @recipe::jsonb FROM agent_experience.experience_grants WHERE grant_id = @grant_id",
            connection);
        insert.Parameters.AddWithValue("disclosure", disclosure);
        insert.Parameters.AddWithValue("recipe", recipeExcerpts);
        insert.Parameters.AddWithValue("grant_id", grantId);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task On_a_schema_that_stops_at_0030_reads_and_ordinary_grants_work_and_only_a_recipe_consent_needs_0031()
    {
        await using var dataSource = await fixture.CreateDatabaseAsync("pre_0031");
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames
            .TakeWhile(name => !string.Equals(name, PostgresExperienceRecordSchema.GrantRecipeExcerptsScriptName, StringComparison.Ordinal)))
        {
            await using var command = dataSource.CreateCommand(PostgresExperienceRecordSchema.GetScript(scriptName));
            await command.ExecuteNonQueryAsync();
        }

        var store = new PostgresExperienceRecordStore(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var grants = new PostgresExperienceGrantStore(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var tenant = NewTenant();
        var owner = Scope(tenant, team: "team-a");
        var recipient = Scope(tenant, team: "team-b");
        var record = WithExcerpt(owner, "pinned lib to 2.4.1");
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(Authorize(tenant), record, CancellationToken.None)).Outcome);

        var plain = await grants.CreateAsync(Authorize(tenant), Admin(), Request(record.ExperienceId, owner, recipient, ExperienceGrantDisclosure.LessonAndApproach), CancellationToken.None);
        Assert.Equal(ExperienceGrantOutcome.Created, plain.Outcome);
        Assert.Null(plain.Grant!.RecipeExcerpts);
        Assert.Null(Assert.Single((await grants.ListAsync(Authorize(tenant), owner, record.ExperienceId, CancellationToken.None)).Grants).RecipeExcerpts);

        var borrowed = await store.GetAsync(Authorize(tenant), recipient, record.ExperienceId, CancellationToken.None);
        Assert.True(borrowed.SharedByGrant);
        Assert.Null(borrowed.GrantRecipeExcerpts);
        Assert.Equal("pinned lib to 2.4.1", borrowed.Record!.Attempts[0].ToolCalls[0].RecipeExcerpt);
        Assert.Single((await store.GetManyAsync(Authorize(tenant), recipient, [record.ExperienceId], new ExperienceReadOptions(), CancellationToken.None)).Results);

        // A grant naming recipe excerpts is the one write that needs the column: it fails, and writes nothing.
        await Assert.ThrowsAnyAsync<Exception>(() => grants.CreateAsync(
            Authorize(tenant),
            Admin(),
            Request(record.ExperienceId, owner, Scope(tenant, team: "team-c"), ExperienceGrantDisclosure.LessonAndApproach) with { RecipeExcerpts = ["search_docs"] },
            CancellationToken.None));
        Assert.Single((await grants.ListAsync(Authorize(tenant), owner, record.ExperienceId, CancellationToken.None)).Grants);
    }

    private static GrantAdministration Admin() => new(Administrator, DateTimeOffset.UtcNow);

    private static ExperienceGrantRequest Request(Guid experienceId, Scope owner, Scope recipient, ExperienceGrantDisclosure level) =>
        new(
            Guid.NewGuid(),
            experienceId,
            owner,
            recipient,
            "sibling team owns the follow-up",
            new DateTimeOffset(DateTimeOffset.UtcNow.AddHours(1).UtcTicks / 10 * 10, TimeSpan.Zero),
            level,
            level == ExperienceGrantDisclosure.LessonApproachAndArguments
                ? new Dictionary<string, IReadOnlyList<string>> { ["search_docs"] = ["query"] }
                : null);

    private async Task<string> PayloadAsync(Guid experienceId)
    {
        await using var connection = await fixture.OwnerDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT payload::text FROM agent_experience.experience_records WHERE experience_id = @id", connection);
        command.Parameters.AddWithValue("id", experienceId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> RowTextAsync(Guid experienceId)
    {
        await using var connection = await fixture.OwnerDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT r::text FROM agent_experience.experience_records r WHERE experience_id = @id", connection);
        command.Parameters.AddWithValue("id", experienceId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
