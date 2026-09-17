using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Tests that need no database: the store runs against a data source pointing at a closed port, so
/// any attempt to open a connection would throw. Invalid and Denied results prove no connection was
/// opened; the unavailable case proves infrastructure failures are wrapped.
/// </summary>
public sealed class OfflineStoreTests : IAsyncLifetime
{
    private readonly Npgsql.NpgsqlDataSource _dataSource = Unreachable();
    private PostgresExperienceRecordStore Store => new(_dataSource);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task Malformed_record_returns_Invalid_with_every_field_path_error_and_no_database_call()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant)) with
        {
            ExperienceId = Guid.Empty,
            TaskId = "   ",
            Scope = new Scope(tenant, "", "project-1", TeamId: " "),
            ReuseConfidence = 1.5,
            CompletionScore = double.NaN,
            SupportingValidations = -1,
            Contradictions = -2,
            Revision = -3,
            Status = (ExperienceStatus)999,
            Attempts = [new Attempt(Guid.NewGuid(), 0, ColumnTime, TimeSpan.Zero, [null!], null, null)],
        };

        var result = await Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(
            new[]
            {
                "Attempts[0].ToolCalls[0]", "CompletionScore", "Contradictions", "ExperienceId", "ReuseConfidence",
                "Revision", "Scope.ApplicationId", "Scope.TeamId", "Status", "SupportingValidations", "TaskId",
            },
            result.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }

    [Fact]
    public async Task Null_nested_members_return_Invalid_with_every_field_path()
    {
        var tenant = NewTenant();
        var valid = Full(Scope(tenant));
        var evidence = valid.Outcome.Evidence[0] with { ArtifactRevision = null!, CheckId = null!, Kind = null!, Producer = null! };
        var record = valid with
        {
            Outcome = valid.Outcome with { Evidence = [null!, evidence] },
            Reflection = valid.Reflection! with
            {
                Lesson = null!,
                SuccessfulApproaches = ["ok", null!],
                Producer = null!,
                VerificationRuleVersion = null!,
            },
            Environment = valid.Environment with
            {
                HostName = null!,
                Metadata = new Dictionary<string, string> { ["region"] = null! },
            },
            Provenance = valid.Provenance with { Source = null! },
        };

        var result = await Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(
            new[]
            {
                "Environment.HostName", "Environment.Metadata",
                "Outcome.Evidence[0]", "Outcome.Evidence[1].ArtifactRevision", "Outcome.Evidence[1].CheckId",
                "Outcome.Evidence[1].Kind", "Outcome.Evidence[1].Producer",
                "Provenance.Source",
                "Reflection.Lesson", "Reflection.Producer", "Reflection.SuccessfulApproaches[1]", "Reflection.VerificationRuleVersion",
            }.Order(StringComparer.Ordinal),
            result.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Undefined_query_status_returns_Invalid()
    {
        var tenant = NewTenant();

        var result = await Store.QueryAsync(
            Authorize(tenant),
            new ExperienceRecordQuery(Scope(tenant), [(ExperienceStatus)999]),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["Statuses[0]"], result.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task NaN_tool_argument_returns_Invalid()
    {
        var tenant = NewTenant();
        var result = await Store.CreateAsync(Authorize(tenant), WithArgument(Minimal(Scope(tenant)), double.NaN), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["Attempts"], result.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task Unserializable_self_referencing_tool_argument_returns_Invalid()
    {
        var tenant = NewTenant();
        var cycle = new List<object?>();
        cycle.Add(cycle);

        var result = await Store.CreateAsync(Authorize(tenant), WithArgument(Minimal(Scope(tenant)), cycle), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["Attempts"], result.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task NUL_in_TaskSummary_returns_Invalid_without_echoing_content()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant)) with { TaskSummary = "secret\0summary" };

        var result = await Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Payload", error.Path);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Escaped_backslash_followed_by_u0000_text_is_not_mistaken_for_NUL()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant)) with { TaskSummary = "literal \\u0000 text" };

        // Passes validation and the NUL check, so it reaches the unreachable database.
        await Assert.ThrowsAsync<ExperienceStoreException>(() => Store.CreateAsync(Authorize(tenant), record, CancellationToken.None));
    }

    [Fact]
    public async Task NUL_in_scope_ProjectId_returns_Invalid()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant, project: "project\0one");

        var create = await Store.CreateAsync(Authorize(tenant), Minimal(scope), CancellationToken.None);
        var query = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(scope), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, create.Outcome);
        Assert.Equal(["Scope.ProjectId"], create.Errors.Select(e => e.Path));
        Assert.Equal(ExperienceStoreOutcome.Invalid, query.Outcome);
        Assert.Equal(["Scope.ProjectId"], query.Errors.Select(e => e.Path));
    }

    private static ExperienceRecord WithArgument(ExperienceRecord record, object? value) => record with
    {
        Attempts =
        [
            new Attempt(
                Guid.NewGuid(),
                0,
                ColumnTime,
                TimeSpan.Zero,
                [new ToolCallRecord(Guid.NewGuid(), 0, "tool", new Dictionary<string, object?> { ["value"] = value }, ColumnTime, TimeSpan.Zero, null, null)],
                null,
                null),
        ],
    };

    [Fact]
    public async Task Negative_confidence_and_blank_tenant_are_Invalid()
    {
        var record = Minimal(new Scope("\t", "app", "project")) with { ReuseConfidence = -0.01 };

        var result = await Store.CreateAsync(Authorize("\t"), record, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["ReuseConfidence", "Scope.TenantId"], result.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Malformed_get_and_query_return_Invalid()
    {
        var tenant = NewTenant();

        var get = await Store.GetAsync(Authorize(tenant), new Scope(tenant, "app", " "), Guid.Empty, CancellationToken.None);
        var query = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(Scope(tenant), [], Limit: 501), CancellationToken.None);
        var zeroLimit = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(Scope(tenant), Limit: 0), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, get.Outcome);
        Assert.Equal(["ExperienceId", "Scope.ProjectId"], get.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(ExperienceStoreOutcome.Invalid, query.Outcome);
        Assert.Equal(["Limit", "Statuses"], query.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(["Limit"], zeroLimit.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task Different_tenant_is_Denied_before_any_connection_opens()
    {
        var record = Minimal(Scope("tenant-b"));
        var auth = Authorize("tenant-a");

        var create = await Store.CreateAsync(auth, record, CancellationToken.None);
        var get = await Store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None);
        var query = await Store.QueryAsync(auth, new ExperienceRecordQuery(record.Scope), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, create.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, get.Outcome);
        Assert.Null(get.Record);
        Assert.Equal(ExperienceStoreOutcome.Denied, query.Outcome);
        Assert.Empty(query.Records);
    }

    [Fact]
    public async Task Non_null_bound_that_differs_from_request_scope_is_Denied()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant) with { ProjectId = "project-1" };
        var scope = Scope(tenant, project: "project-2");

        Assert.Equal(ExperienceStoreOutcome.Denied, (await Store.CreateAsync(auth, Minimal(scope), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, (await Store.GetAsync(auth, scope, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, (await Store.QueryAsync(auth, new ExperienceRecordQuery(scope), CancellationToken.None)).Outcome);

        var caseOnly = Authorize(tenant) with { ProjectId = "PROJECT-1" };
        Assert.Equal(ExperienceStoreOutcome.Denied, (await Store.QueryAsync(caseOnly, new ExperienceRecordQuery(Scope(tenant)), CancellationToken.None)).Outcome);

        var teamBound = Authorize(tenant) with { TeamId = "t1" };
        Assert.Equal(ExperienceStoreOutcome.Denied, (await Store.QueryAsync(teamBound, new ExperienceRecordQuery(Scope(tenant, team: null)), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Unavailable_database_throws_ExperienceStoreException_with_the_original_failure()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant));

        var create = await Assert.ThrowsAsync<ExperienceStoreException>(() => Store.CreateAsync(auth, record, CancellationToken.None));
        var get = await Assert.ThrowsAsync<ExperienceStoreException>(() => Store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None));
        var query = await Assert.ThrowsAsync<ExperienceStoreException>(() => Store.QueryAsync(auth, new ExperienceRecordQuery(record.Scope), CancellationToken.None));

        Assert.All([create, get, query], ex => Assert.IsAssignableFrom<Npgsql.NpgsqlException>(ex.InnerException));
    }

    [Fact]
    public async Task Pre_cancelled_token_throws_OperationCanceledException_not_a_store_exception()
    {
        var tenant = NewTenant();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Store.GetAsync(Authorize(tenant), Scope(tenant), Guid.NewGuid(), cts.Token));
        Assert.IsNotType<ExperienceStoreException>(ex);
    }

    [Fact]
    public async Task Null_arguments_throw_ArgumentNullException()
    {
        var tenant = NewTenant();
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.CreateAsync(null!, Minimal(Scope(tenant)), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.CreateAsync(Authorize(tenant), null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.GetAsync(Authorize(tenant), null!, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.QueryAsync(Authorize(tenant), null!, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => new PostgresExperienceRecordStore(null!));
    }

    [Fact]
    public void Embedded_schema_script_is_available_and_creates_the_versioned_table()
    {
        var sql = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.InitialScriptName);

        Assert.Equal([PostgresExperienceRecordSchema.InitialScriptName], PostgresExperienceRecordSchema.ScriptNames);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS agent_experience", sql, StringComparison.Ordinal);
        Assert.Contains("payload_version", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vector", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => PostgresExperienceRecordSchema.GetScript("9999_missing.sql"));
    }
}
