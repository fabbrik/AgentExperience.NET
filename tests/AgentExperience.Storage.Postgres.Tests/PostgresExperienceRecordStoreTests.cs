using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL 16 container. Each test uses its own random tenant, so
/// tests sharing the container never see each other's rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresExperienceRecordStoreTests
{
    private readonly PostgresFixture _fixture;
    private readonly PostgresExperienceRecordStore _store;

    public PostgresExperienceRecordStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _store = new PostgresExperienceRecordStore(fixture.DataSource);
    }

    [Fact]
    public async Task Fully_populated_record_round_trips_deep_equal_after_JSON_normalization()
    {
        var tenant = NewTenant();
        var scope = new Scope(tenant, "app-1", "project-1", "team-1", "agent-1", "user-1");
        var record = Full(scope);

        var created = await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        Assert.Empty(created.Errors);

        var read = await _store.GetAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Empty(read.Errors);
        Assert.NotNull(read.Record);
        Assert.Equal(Canonical(record), Canonical(read.Record));
        Assert.Equal(record.CreatedAt, read.Record.CreatedAt);
        Assert.Equal(record.UpdatedAt, read.Record.UpdatedAt);
        Assert.Equal(record.Attempts[0].StartedAt.UtcTicks, read.Record.Attempts[0].StartedAt.UtcTicks);
    }

    [Fact]
    public async Task Tool_call_arguments_read_back_as_normalized_CLR_values()
    {
        var tenant = NewTenant();
        var record = Full(Scope(tenant));
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var read = await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        var arguments = read.Record!.Attempts[0].ToolCalls[0].Arguments;

        Assert.IsType<string>(arguments["query"]);
        Assert.Equal(3L, Assert.IsType<long>(arguments["limit"]));
        Assert.Equal(0.75, Assert.IsType<double>(arguments["threshold"]));
        Assert.True(Assert.IsType<bool>(arguments["exact"]));
        Assert.Null(arguments["cursor"]);
        var filters = Assert.IsType<Dictionary<string, object?>>(arguments["filters"]);
        var tags = Assert.IsType<List<object?>>(filters["tags"]);
        Assert.Equal(["a", 2L, false], tags);
    }

    [Fact]
    public async Task Whole_number_double_tool_argument_reads_back_as_long()
    {
        var tenant = NewTenant();
        var minimal = Minimal(Scope(tenant));
        var record = minimal with
        {
            Attempts =
            [
                new Attempt(Guid.NewGuid(), 0, ColumnTime, TimeSpan.Zero,
                    [new ToolCallRecord(Guid.NewGuid(), 0, "tool", new Dictionary<string, object?> { ["whole"] = 1.0, ["fraction"] = 1.5 }, ColumnTime, TimeSpan.Zero, null, null)],
                    null, null),
            ],
        };
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        var arguments = (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!.Attempts[0].ToolCalls[0].Arguments;

        Assert.Equal(1L, Assert.IsType<long>(arguments["whole"]));
        Assert.Equal(1.5, Assert.IsType<double>(arguments["fraction"]));
    }

    [Fact]
    public async Task Timestamps_are_returned_in_UTC_as_the_same_instant()
    {
        var tenant = NewTenant();
        var offsetTime = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(10);
        var record = Minimal(Scope(tenant), createdAt: offsetTime) with
        {
            Provenance = new Provenance("tests", null, offsetTime.AddTicks(3), null),
        };

        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var read = (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;

        Assert.Equal(TimeSpan.Zero, read.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, read.UpdatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, read.Provenance.RecordedAt.Offset);
        Assert.Equal(TimeSpan.Zero, read.Outcome.EvaluatedAt.Offset);
        Assert.Equal(offsetTime, read.CreatedAt); // DateTimeOffset equality compares instants
        Assert.Equal(offsetTime.AddTicks(3), read.Provenance.RecordedAt);
    }

    [Fact]
    public async Task Column_timestamps_are_truncated_to_PostgreSQL_microsecond_precision()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant), createdAt: ColumnTime.AddTicks(7));

        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var read = (await _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;

        Assert.Equal(ColumnTime, read.CreatedAt);
    }

    [Fact]
    public async Task Get_of_an_ID_in_another_project_or_team_is_NotFound_like_a_missing_ID()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var record = Minimal(Scope(tenant, project: "project-1", team: "team-1"));
        await _store.CreateAsync(auth, record, CancellationToken.None);

        var otherProject = await _store.GetAsync(auth, Scope(tenant, project: "project-2", team: "team-1"), record.ExperienceId, CancellationToken.None);
        var otherTeam = await _store.GetAsync(auth, Scope(tenant, project: "project-1", team: "team-2"), record.ExperienceId, CancellationToken.None);
        var noTeam = await _store.GetAsync(auth, Scope(tenant, project: "project-1"), record.ExperienceId, CancellationToken.None);
        var missing = await _store.GetAsync(auth, record.Scope, Guid.NewGuid(), CancellationToken.None);

        Assert.All([otherProject, otherTeam, noTeam, missing], result =>
        {
            Assert.Equal(ExperienceStoreOutcome.NotFound, result.Outcome);
            Assert.Null(result.Record);
            Assert.Empty(result.Errors);
        });
    }

    [Fact]
    public async Task Duplicate_ID_conflicts_identically_in_any_scope_and_leaves_the_stored_row_unchanged()
    {
        var tenant = NewTenant();
        var otherTenant = NewTenant();
        var original = Full(Scope(tenant));
        await _store.CreateAsync(Authorize(tenant), original, CancellationToken.None);

        var sameScope = await _store.CreateAsync(
            Authorize(tenant),
            Minimal(original.Scope, id: original.ExperienceId, status: ExperienceStatus.Revoked),
            CancellationToken.None);
        var foreignScope = await _store.CreateAsync(
            Authorize(otherTenant),
            Minimal(Scope(otherTenant), id: original.ExperienceId),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, sameScope.Outcome);
        Assert.Empty(sameScope.Errors);
        Assert.Equal(ExperienceStoreOutcome.Conflict, foreignScope.Outcome);
        Assert.Empty(foreignScope.Errors);

        var read = await _store.GetAsync(Authorize(tenant), original.Scope, original.ExperienceId, CancellationToken.None);
        Assert.Equal(Canonical(original), Canonical(read.Record!));
        var foreignRead = await _store.GetAsync(Authorize(otherTenant), Scope(otherTenant), original.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, foreignRead.Outcome);
    }

    [Fact]
    public async Task Null_TeamId_matches_only_null_and_a_set_TeamId_matches_only_itself()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var noTeam = Minimal(Scope(tenant, team: null));
        var team = Minimal(Scope(tenant, team: "t1"));
        await _store.CreateAsync(auth, noTeam, CancellationToken.None);
        await _store.CreateAsync(auth, team, CancellationToken.None);

        var nullQuery = await _store.QueryAsync(auth, new ExperienceRecordQuery(Scope(tenant, team: null)), CancellationToken.None);
        var teamQuery = await _store.QueryAsync(auth, new ExperienceRecordQuery(Scope(tenant, team: "t1")), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, nullQuery.Outcome);
        Assert.Equal([noTeam.ExperienceId], nullQuery.Records.Select(r => r.ExperienceId));
        Assert.Equal([team.ExperienceId], teamQuery.Records.Select(r => r.ExperienceId));
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await _store.GetAsync(auth, Scope(tenant, team: null), team.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task ProjectId_matching_is_case_sensitive()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var lower = Minimal(Scope(tenant, project: "proj"));
        var upper = Minimal(Scope(tenant, project: "Proj"));
        await _store.CreateAsync(auth, lower, CancellationToken.None);
        await _store.CreateAsync(auth, upper, CancellationToken.None);

        var lowerQuery = await _store.QueryAsync(auth, new ExperienceRecordQuery(Scope(tenant, project: "proj")), CancellationToken.None);
        var upperQuery = await _store.QueryAsync(auth, new ExperienceRecordQuery(Scope(tenant, project: "Proj")), CancellationToken.None);

        Assert.Equal([lower.ExperienceId], lowerQuery.Records.Select(r => r.ExperienceId));
        Assert.Equal([upper.ExperienceId], upperQuery.Records.Select(r => r.ExperienceId));
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await _store.GetAsync(auth, Scope(tenant, project: "PROJ"), lower.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Query_never_crosses_tenants_or_optional_scope_fields()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        await _store.CreateAsync(Authorize(tenantA), Minimal(Scope(tenantA)), CancellationToken.None);
        await _store.CreateAsync(Authorize(tenantB), Minimal(Scope(tenantB)), CancellationToken.None);
        var agentScoped = Minimal(new Scope(tenantA, "app-1", "project-1", AgentId: "agent-1"));
        await _store.CreateAsync(Authorize(tenantA), agentScoped, CancellationToken.None);

        var result = await _store.QueryAsync(Authorize(tenantA), new ExperienceRecordQuery(Scope(tenantA)), CancellationToken.None);

        var only = Assert.Single(result.Records);
        Assert.Equal(tenantA, only.Scope.TenantId);
        Assert.Null(only.Scope.AgentId);
    }

    [Fact]
    public async Task Query_filters_by_status()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var candidate = Minimal(Scope(tenant), status: ExperienceStatus.Candidate);
        var validated = Minimal(Scope(tenant), status: ExperienceStatus.Validated);
        var reinforced = Minimal(Scope(tenant), status: ExperienceStatus.Reinforced);
        foreach (var record in new[] { candidate, validated, reinforced })
        {
            await _store.CreateAsync(auth, record, CancellationToken.None);
        }

        var result = await _store.QueryAsync(
            auth,
            new ExperienceRecordQuery(Scope(tenant), [ExperienceStatus.Validated, ExperienceStatus.Reinforced, ExperienceStatus.Validated]),
            CancellationToken.None);
        var all = await _store.QueryAsync(auth, new ExperienceRecordQuery(Scope(tenant)), CancellationToken.None);

        Assert.Equal(
            new[] { validated.ExperienceId, reinforced.ExperienceId }.Order(),
            result.Records.Select(r => r.ExperienceId).Order());
        Assert.Equal(3, all.Records.Count);
    }

    [Fact]
    public async Task Query_applies_the_limit_newest_first()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var records = Enumerable.Range(0, 5)
            .Select(i => Minimal(Scope(tenant), createdAt: ColumnTime.AddMinutes(i)))
            .ToList();
        foreach (var record in records)
        {
            await _store.CreateAsync(auth, record, CancellationToken.None);
        }

        var result = await _store.QueryAsync(auth, new ExperienceRecordQuery(Scope(tenant), Limit: 2), CancellationToken.None);

        Assert.Equal([records[4].ExperienceId, records[3].ExperienceId], result.Records.Select(r => r.ExperienceId));
    }

    [Fact]
    public async Task Bounded_authorization_permits_its_exact_project()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant) with { ProjectId = "project-1", TeamId = "t1" };
        var record = Minimal(Scope(tenant, project: "project-1", team: "t1"));

        Assert.Equal(ExperienceStoreOutcome.Created, (await _store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Found, (await _store.GetAsync(auth, record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Unsupported_payload_version_throws_ExperienceStoreException_on_get_and_query()
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant));
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        await using (var command = _fixture.DataSource.CreateCommand(
            "UPDATE agent_experience.experience_records SET payload_version = 99 WHERE experience_id = @id"))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<ExperienceStoreException>(
            () => _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None));
        await Assert.ThrowsAsync<ExperienceStoreException>(
            () => _store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(record.Scope), CancellationToken.None));
    }

    [Theory]
    [InlineData("payload = '{}'::jsonb")]
    [InlineData("payload = jsonb_set(payload, '{attempts}', '[null]'::jsonb)")]
    // 0006 guards the projection, so a status change carries the revision it belongs to -- which is
    // what the store's own commit does. The corruption is the status text, not the shape of the write.
    [InlineData("status = '1', revision = revision + 1")]
    [InlineData("status = 'validated', revision = revision + 1")]
    public async Task Corrupt_stored_row_throws_ExperienceStoreException_on_get_and_query(string corruption)
    {
        var tenant = NewTenant();
        var record = Minimal(Scope(tenant), status: ExperienceStatus.Validated);
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        await using (var command = _fixture.DataSource.CreateCommand(
            $"UPDATE agent_experience.experience_records SET {corruption} WHERE experience_id = @id"))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var get = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => _store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None));
        var query = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => _store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(record.Scope), CancellationToken.None));
        Assert.Equal(get.GetType(), query.GetType());
    }

    [Fact]
    public async Task Hand_written_payload_version_1_row_maps_field_by_field()
    {
        // Golden fixture: pins the stored v1 JSON keys, enum member names, and column values. If this
        // breaks, existing rows break too -- add a new payload version instead of renaming.
        var tenant = NewTenant();
        var experienceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        const string payload = """
            {
              "taskSummary": "Resolve refund ticket",
              "attempts": [
                {
                  "attemptId": "22222222-2222-2222-2222-222222222222",
                  "sequenceNumber": 0,
                  "startedAt": "2026-09-17T09:00:00.1234567+00:00",
                  "duration": "00:00:01.5000000",
                  "toolCalls": [
                    {
                      "toolCallId": "33333333-3333-3333-3333-333333333333",
                      "sequenceNumber": 0,
                      "toolName": "search_docs",
                      "arguments": { "query": "refund", "limit": 3, "threshold": 0.5, "exact": true, "cursor": null, "tags": ["a", 1.0], "filter": { "lang": "en" } },
                      "startedAt": "2026-09-17T09:00:00.2000000+00:00",
                      "duration": "00:00:00.1000000",
                      "result": "3 documents",
                      "error": null
                    }
                  ],
                  "result": null,
                  "error": "System.TimeoutException"
                }
              ],
              "outcome": {
                "status": "Verified",
                "evidence": [
                  {
                    "evidenceId": "44444444-4444-4444-4444-444444444444",
                    "verificationRoundId": "55555555-5555-5555-5555-555555555555",
                    "artifactRevision": "rev-7",
                    "checkId": "unit-tests-pass",
                    "kind": "TestResult",
                    "result": "Pass",
                    "producer": "ci",
                    "detail": "42 of 42 passed",
                    "capturedAt": "2026-09-17T09:01:00+00:00"
                  }
                ],
                "reason": "all checks passed",
                "evaluatedAt": "2026-09-17T09:02:00+00:00"
              },
              "completionScore": 1,
              "reflection": {
                "reflectionId": "66666666-6666-6666-6666-666666666666",
                "experienceRunId": "77777777-7777-7777-7777-777777777777",
                "lesson": "Retry after the lock clears.",
                "successfulApproaches": ["retry"],
                "failedApproaches": ["immediate update"],
                "preconditions": ["ticket system reachable"],
                "warnings": ["lock duration unknown"],
                "reuseGuidance": "Ticket-lock failures only.",
                "evidenceIds": ["44444444-4444-4444-4444-444444444444"],
                "verificationStatus": "Verified",
                "completionScore": 0.75,
                "verificationRuleVersion": "v1",
                "producer": "template-reflector/1.0",
                "createdAt": "2026-09-17T09:03:00+00:00"
              },
              "environment": {
                "hostName": "worker-01",
                "runtimeVersion": "10.0.0",
                "operatingSystem": "linux-x64",
                "applicationVersion": "1.2.3",
                "metadata": { "region": "us-east" }
              },
              "provenance": {
                "source": "AgentExperience.MicrosoftAgentFramework",
                "sourceVersion": "1.0.0",
                "recordedAt": "2026-09-17T09:04:00+00:00",
                "correlationId": "trace-123"
              }
            }
            """;

        await using (var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, project_id, " +
            "team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, " +
            "created_at, updated_at, payload_version, payload) VALUES (@id, '77777777-7777-7777-7777-777777777777', @tenant, " +
            "'app-1', 'project-1', 'team-1', NULL, 'user-1', 'support-ticket', 'Reinforced', 0.8, 4, 1, 3, " +
            "'2026-09-17T10:00:00.123456Z', '2026-09-17T11:00:00Z', 1, @payload::jsonb) " +
            "ON CONFLICT (experience_id) DO UPDATE SET tenant_id = EXCLUDED.tenant_id"))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
            command.Parameters.Add(new NpgsqlParameter<string>("tenant", tenant));
            command.Parameters.Add(new NpgsqlParameter<string>("payload", payload));
            await command.ExecuteNonQueryAsync();
        }

        var scope = new Scope(tenant, "app-1", "project-1", "team-1", null, "user-1");
        var result = await _store.GetAsync(Authorize(tenant), scope, experienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        var r = result.Record!;
        Assert.Equal(experienceId, r.ExperienceId);
        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), r.SourceRunId);
        Assert.Equal(scope, r.Scope);
        Assert.Equal("support-ticket", r.TaskId);
        Assert.Equal("Resolve refund ticket", r.TaskSummary);
        Assert.Equal(ExperienceStatus.Reinforced, r.Status);
        Assert.Equal(0.8, r.ReuseConfidence);
        Assert.Equal(4, r.SupportingValidations);
        Assert.Equal(1, r.Contradictions);
        Assert.Equal(3L, r.Revision);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero).AddTicks(1_234_560), r.CreatedAt);
        Assert.Equal(TimeSpan.Zero, r.CreatedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 11, 0, 0, TimeSpan.Zero), r.UpdatedAt);
        Assert.Equal(1d, r.CompletionScore);

        var attempt = Assert.Single(r.Attempts);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), attempt.AttemptId);
        Assert.Equal(0, attempt.SequenceNumber);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 9, 0, 0, TimeSpan.Zero).AddTicks(1_234_567), attempt.StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), attempt.Duration);
        Assert.Null(attempt.Result);
        Assert.Equal("System.TimeoutException", attempt.Error);

        var toolCall = Assert.Single(attempt.ToolCalls);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), toolCall.ToolCallId);
        Assert.Equal(0, toolCall.SequenceNumber);
        Assert.Equal("search_docs", toolCall.ToolName);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 9, 0, 0, 200, TimeSpan.Zero), toolCall.StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(100), toolCall.Duration);
        Assert.Equal("3 documents", toolCall.Result);
        Assert.Null(toolCall.Error);
        Assert.Equal("refund", toolCall.Arguments["query"]);
        Assert.Equal(3L, Assert.IsType<long>(toolCall.Arguments["limit"]));
        Assert.Equal(0.5, Assert.IsType<double>(toolCall.Arguments["threshold"]));
        Assert.Equal(true, toolCall.Arguments["exact"]);
        Assert.Null(toolCall.Arguments["cursor"]);
        var tags = Assert.IsType<List<object?>>(toolCall.Arguments["tags"]);
        Assert.Equal("a", tags[0]);
        Assert.Equal(1.0, Assert.IsType<double>(tags[1])); // a stored "1.0" literal stays double
        Assert.Equal("en", Assert.IsType<Dictionary<string, object?>>(toolCall.Arguments["filter"])["lang"]);

        Assert.Equal(TaskVerificationStatus.Verified, r.Outcome.Status);
        Assert.Equal("all checks passed", r.Outcome.Reason);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 9, 2, 0, TimeSpan.Zero), r.Outcome.EvaluatedAt);
        var evidence = Assert.Single(r.Outcome.Evidence);
        Assert.Equal(Guid.Parse("44444444-4444-4444-4444-444444444444"), evidence.EvidenceId);
        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), evidence.VerificationRoundId);
        Assert.Equal("rev-7", evidence.ArtifactRevision);
        Assert.Equal("unit-tests-pass", evidence.CheckId);
        Assert.Equal("TestResult", evidence.Kind);
        Assert.Equal(CheckResult.Pass, evidence.Result);
        Assert.Equal("ci", evidence.Producer);
        Assert.Equal("42 of 42 passed", evidence.Detail);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 9, 1, 0, TimeSpan.Zero), evidence.CapturedAt);

        var reflection = r.Reflection!;
        Assert.Equal(Guid.Parse("66666666-6666-6666-6666-666666666666"), reflection.ReflectionId);
        Assert.Equal(Guid.Parse("77777777-7777-7777-7777-777777777777"), reflection.ExperienceRunId);
        Assert.Equal("Retry after the lock clears.", reflection.Lesson);
        Assert.Equal(["retry"], reflection.SuccessfulApproaches);
        Assert.Equal(["immediate update"], reflection.FailedApproaches);
        Assert.Equal(["ticket system reachable"], reflection.Preconditions);
        Assert.Equal(["lock duration unknown"], reflection.Warnings);
        Assert.Equal("Ticket-lock failures only.", reflection.ReuseGuidance);
        Assert.Equal([Guid.Parse("44444444-4444-4444-4444-444444444444")], reflection.EvidenceIds);
        Assert.Equal(TaskVerificationStatus.Verified, reflection.VerificationStatus);
        Assert.Equal(0.75, reflection.CompletionScore);
        Assert.Equal("v1", reflection.VerificationRuleVersion);
        Assert.Equal("template-reflector/1.0", reflection.Producer);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 9, 3, 0, TimeSpan.Zero), reflection.CreatedAt);

        Assert.Equal("worker-01", r.Environment.HostName);
        Assert.Equal("10.0.0", r.Environment.RuntimeVersion);
        Assert.Equal("linux-x64", r.Environment.OperatingSystem);
        Assert.Equal("1.2.3", r.Environment.ApplicationVersion);
        Assert.Equal("us-east", Assert.Single(r.Environment.Metadata).Value);
        Assert.Equal("region", Assert.Single(r.Environment.Metadata).Key);

        Assert.Equal("AgentExperience.MicrosoftAgentFramework", r.Provenance.Source);
        Assert.Equal("1.0.0", r.Provenance.SourceVersion);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 9, 4, 0, TimeSpan.Zero), r.Provenance.RecordedAt);
        Assert.Equal("trace-123", r.Provenance.CorrelationId);
    }

    [Fact]
    public async Task Stored_rows_use_scope_status_and_version_columns()
    {
        var tenant = NewTenant();
        var record = Full(new Scope(tenant, "app-1", "project-1", "team-1", null, "user-1"));
        await _store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        await using var command = _fixture.DataSource.CreateCommand(
            "SELECT tenant_id, team_id, agent_id, status, reuse_confidence, revision, payload_version, payload ? 'attempts' " +
            "FROM agent_experience.experience_records WHERE experience_id = @id");
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", record.ExperienceId));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(tenant, reader.GetString(0));
        Assert.Equal("team-1", reader.GetString(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal("Validated", reader.GetString(3));
        // The confidence Full()'s own counters explain: (1 + 4) / (2 + 4 + 1).
        Assert.Equal(5d / 7d, reader.GetDouble(4));
        Assert.Equal(3L, reader.GetInt64(5));
        Assert.Equal(1, reader.GetInt32(6));
        Assert.True(reader.GetBoolean(7));
    }

    [Fact]
    public async Task Schema_rejects_blank_required_scope_even_when_bypassing_the_store()
    {
        await using var command = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, project_id, " +
            "task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload) " +
            "VALUES (gen_random_uuid(), gen_random_uuid(), '  ', 'app', 'proj', 'task', 'Candidate', 0, 0, 0, 0, now(), now(), 1, '{}')");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_an_unwrapped_OperationCanceledException()
    {
        var tenant = NewTenant();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.CreateAsync(Authorize(tenant), Minimal(Scope(tenant)), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(Scope(tenant)), cts.Token));
    }
}
