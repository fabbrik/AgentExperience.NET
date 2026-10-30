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
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
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
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT payload::text FROM agent_experience.experience_records WHERE experience_id = @id", connection);
        command.Parameters.AddWithValue("id", experienceId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> RowTextAsync(Guid experienceId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT r::text FROM agent_experience.experience_records r WHERE experience_id = @id", connection);
        command.Parameters.AddWithValue("id", experienceId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
