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
    public async Task A_malformed_confidence_payload_is_Invalid_before_any_connection_opens()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var recordId = Guid.NewGuid();
        var valid = new ConfidenceUpdate(
            EvidenceId: Guid.NewGuid(),
            Kind: ConfidenceEvidenceKind.Supporting,
            Source: ConfidenceEvidenceSource.Machine,
            RunId: Guid.NewGuid(),
            VerificationRoundId: Guid.NewGuid(),
            ReviewerIdentity: null,
            RuleVersion: "1.0.0",
            PriorReuseConfidence: 2d / 3d,
            NewReuseConfidence: 3d / 4d,
            PriorSupportingValidations: 1,
            NewSupportingValidations: 2,
            PriorContradictions: 0,
            NewContradictions: 0);

        foreach (var (confidence, path) in new[]
        {
            (valid with { EvidenceId = Guid.Empty }, "Confidence.EvidenceId"),
            (valid with { RunId = Guid.Empty }, "Confidence.RunId"),
            (valid with { RuleVersion = " " }, "Confidence.RuleVersion"),
            (valid with { NewReuseConfidence = 1.5 }, "Confidence.NewReuseConfidence"),
            (valid with { PriorContradictions = -1 }, "Confidence.PriorContradictions"),
            // Evidence only ever moves a counter up; a smaller new value is a rewrite of history.
            (valid with { NewSupportingValidations = 0 }, "Confidence.NewSupportingValidations"),
            // Each source carries exactly the identifier its independence key is made of.
            (valid with { VerificationRoundId = null }, "Confidence.VerificationRoundId"),
            (valid with { ReviewerIdentity = "someone" }, "Confidence.ReviewerIdentity"),
            (valid with { Source = ConfidenceEvidenceSource.Human, ReviewerIdentity = null }, "Confidence.ReviewerIdentity"),
            (valid with { Source = (ConfidenceEvidenceSource)99 }, "Confidence.Source"),
        })
        {
            var result = await Store.CommitLifecycleEventAsync(
                Authorize(tenant),
                scope,
                Event(recordId, ExperienceStatus.Validated, ExperienceStatus.Validated, 1) with { Confidence = confidence },
                CancellationToken.None);

            Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
            Assert.Contains(result.Errors, error => error.Path == path);
        }

        // The well-formed payload passes validation and only then reaches the (unreachable) database.
        await Assert.ThrowsAsync<ExperienceStoreException>(() => Store.CommitLifecycleEventAsync(
            Authorize(tenant),
            scope,
            Event(recordId, ExperienceStatus.Validated, ExperienceStatus.Validated, 1) with { Confidence = valid },
            CancellationToken.None));
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
    public async Task Unavailable_database_makes_the_migrator_throw_ExperienceStoreException_with_the_original_failure()
    {
        var ex = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => ExperienceSchemaMigrator.MigrateAsync(_dataSource, CancellationToken.None));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void Advisory_lock_key_is_pinned()
    {
        // Changing this silently stops serializing against hosts still running the previous package
        // version, so two of them could apply the same script at once. Treat a change as breaking.
        Assert.Equal(0x4147455850455201L, ExperienceSchemaMigrator.AdvisoryLockKey);
    }

    [Fact]
    public void Embedded_migration_resources_match_the_declared_script_names()
    {
        // The migrator scans by resource prefix while callers read ScriptNames, so the two must agree:
        // an embedded script missing from ScriptNames (or the reverse) would go unnoticed otherwise.
        const string ResourcePrefix = "AgentExperience.Storage.Postgres.Migrations.";

        var embedded = typeof(PostgresExperienceRecordSchema).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => name[ResourcePrefix.Length..])
            .Order(StringComparer.Ordinal);

        Assert.Equal(PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal), embedded);
    }

    [Fact]
    public void Embedded_schema_script_is_available_and_creates_the_versioned_table()
    {
        var sql = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.InitialScriptName);

        Assert.Equal(
            [
                PostgresExperienceRecordSchema.InitialScriptName,
                PostgresExperienceRecordSchema.LifecycleEventsScriptName,
                PostgresExperienceRecordSchema.SearchScriptName,
                PostgresExperienceRecordSchema.GrantsScriptName,
                PostgresExperienceRecordSchema.SupersessionAndAppendOnlyScriptName,
                PostgresExperienceRecordSchema.ConfidenceEvidenceScriptName,
                PostgresExperienceRecordSchema.ReuseFeedbackScriptName,
                PostgresExperienceRecordSchema.GrantAccessLogScriptName,
                PostgresExperienceRecordSchema.DeleteAndExpireScriptName,
                PostgresExperienceRecordSchema.GrantDisclosureScriptName,
                PostgresExperienceRecordSchema.GrantAccessRetentionScriptName,
            ],
            PostgresExperienceRecordSchema.ScriptNames);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS agent_experience", sql, StringComparison.Ordinal);
        Assert.Contains("payload_version", sql, StringComparison.Ordinal);
        Assert.Contains("jsonb", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("vector", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => PostgresExperienceRecordSchema.GetScript("9999_missing.sql"));
    }

    [Fact]
    public void Lifecycle_script_is_embedded_separately_and_never_edits_the_initial_one()
    {
        var lifecycle = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.LifecycleEventsScriptName);

        // Scripts are append-only: 0002 adds its own table and touches nothing 0001 created.
        Assert.Contains("CREATE TABLE IF NOT EXISTS agent_experience.lifecycle_events", lifecycle, StringComparison.Ordinal);
        Assert.Contains("applied_revision = expected_revision + 1", lifecycle, StringComparison.Ordinal);
        // Unique, so two events can never claim one revision of a record and desynchronize the log.
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ix_lifecycle_events_record_revision", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE", lifecycle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", lifecycle, StringComparison.OrdinalIgnoreCase);

        // 0002 is applied after 0001, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
    }

    [Fact]
    public void Search_script_is_embedded_separately_and_only_adds_derived_read_artifacts()
    {
        var search = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.SearchScriptName);

        // A generated column, so no write path has to maintain it and it can never disagree with the
        // record it indexes; the store's INSERT and its lifecycle UPDATE are untouched by this script.
        Assert.Contains("ADD COLUMN IF NOT EXISTS search_vector tsvector", search, StringComparison.Ordinal);
        Assert.Contains("GENERATED ALWAYS AS", search, StringComparison.Ordinal);
        Assert.Contains("STORED", search, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_experience_records_search", search, StringComparison.Ordinal);
        Assert.Contains("USING GIN", search, StringComparison.Ordinal);

        // The query and the generated column must be analyzed with the same configuration: querying with
        // a different one silently changes which rows match, so the constant is pinned to the script.
        Assert.Contains($"'{PostgresExperienceCandidateSource.SearchConfiguration}'", search, StringComparison.Ordinal);
        Assert.Equal("english", PostgresExperienceCandidateSource.SearchConfiguration);

        // A tsvector may not exceed 1 MB, and a generated column that raises fails the INSERT, not the
        // search -- so the concatenated text is bounded before it is analyzed.
        Assert.Contains("left(", search, StringComparison.Ordinal);

        // The indexed text is exactly the three fields that say what a record is about.
        Assert.Contains("task_id", search, StringComparison.Ordinal);
        Assert.Contains("payload ->> 'taskSummary'", search, StringComparison.Ordinal);
        Assert.Contains("payload -> 'reflection' ->> 'lesson'", search, StringComparison.Ordinal);

        // The "never" assertions below are about what the script *executes*, so the leading comment block
        // -- which explains, in prose, why 0001's now-redundant index is not dropped -- is stripped first.
        var statements = string.Join(
            '\n',
            search.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // Append-only: 0003 adds to the table and rewrites nothing 0001 or 0002 created.
        Assert.DoesNotContain("DROP", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER COLUMN", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lifecycle_events", statements, StringComparison.Ordinal);

        // Story 2.6 owns embeddings; this script must not anticipate them.
        Assert.DoesNotContain("embedding", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE EXTENSION", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hnsw", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ivfflat", statements, StringComparison.OrdinalIgnoreCase);

        // 0003 is applied after 0002, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
    }

    [Fact]
    public void Grant_script_is_embedded_separately_and_states_the_boundary_a_grant_cannot_cross()
    {
        var grants = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.GrantsScriptName);

        Assert.Contains("CREATE TABLE IF NOT EXISTS agent_experience.experience_grants", grants, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS agent_experience.experience_grant_events", grants, StringComparison.Ordinal);

        // The rule that makes a grant a grant lives in the database, not only in the adapter: a row
        // that would move a record across a tenant, application, or project cannot be stored at all.
        Assert.Contains("CONSTRAINT experience_grants_same_boundary CHECK (", grants, StringComparison.Ordinal);
        Assert.Contains("recipient_tenant_id = tenant_id", grants, StringComparison.Ordinal);
        Assert.Contains("recipient_application_id = application_id", grants, StringComparison.Ordinal);
        Assert.Contains("recipient_project_id = project_id", grants, StringComparison.Ordinal);

        // A grant that was expired the moment it was issued is not a grant.
        Assert.Contains("CHECK (expires_at > issued_at)", grants, StringComparison.Ordinal);
        // At most one ACTIVE grant per (record, recipient), so revoking the grant an administrator
        // knows about actually ends that recipient's access rather than leaving an overlapping one.
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_experience_grants_active_recipient", grants, StringComparison.Ordinal);
        Assert.Contains("NULLS NOT DISTINCT", grants, StringComparison.Ordinal);
        // The read predicate's own partial index, over the grants that can still permit anything.
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_experience_grants_active", grants, StringComparison.Ordinal);
        Assert.Contains("WHERE revoked_at IS NULL", grants, StringComparison.Ordinal);
        // A grant to the scope that already owns the record permits nothing and is refused.
        Assert.Contains("CONSTRAINT experience_grants_recipient_differs CHECK (", grants, StringComparison.Ordinal);
        // The authority an action was taken under is part of the trail, not only who took it.
        Assert.Contains("administrator_authorized_at timestamptz NOT NULL", grants, StringComparison.Ordinal);

        var statements = string.Join(
            '\n',
            grants.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // Append-only, like 0002: 0005 adds its own tables and rewrites nothing earlier scripts created.
        Assert.DoesNotContain("ALTER TABLE", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP", statements, StringComparison.OrdinalIgnoreCase);
        // No foreign key, so a grant naming a record that is not in the owner scope is a typed
        // NotFound rather than an infrastructure failure.
        Assert.DoesNotContain("REFERENCES", statements, StringComparison.OrdinalIgnoreCase);
        // The vector extension belongs to the vectors package's 0004 and must not leak into this one.
        Assert.DoesNotContain("CREATE EXTENSION", statements, StringComparison.OrdinalIgnoreCase);

        // 0005 is applied before 0006, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
    }

    [Fact]
    public void Append_only_script_adds_the_replacement_column_and_the_triggers_that_enforce_the_logs()
    {
        var script = PostgresExperienceRecordSchema.GetScript(
            PostgresExperienceRecordSchema.SupersessionAndAppendOnlyScriptName);

        // The replacement is a column on the event, not a payload field: the cycle check walks it in SQL.
        Assert.Contains("ADD COLUMN IF NOT EXISTS replacement_experience_id uuid", script, StringComparison.Ordinal);
        Assert.Contains("lifecycle_events_replacement_only_when_superseded", script, StringComparison.Ordinal);
        Assert.Contains("(replacement_experience_id IS NOT NULL) = (current_status = 'Superseded')", script, StringComparison.Ordinal);
        // The one cycle a single row can state on its own.
        Assert.Contains("lifecycle_events_replacement_is_another_record", script, StringComparison.Ordinal);
        // The index the recursive chain walk follows.
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_lifecycle_events_replacement", script, StringComparison.Ordinal);

        // Append-only stops being a convention: both event logs refuse UPDATE and DELETE outright.
        Assert.Contains("CREATE TRIGGER lifecycle_events_append_only", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TRIGGER experience_grant_events_append_only", script, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON agent_experience.lifecycle_events", script, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON agent_experience.experience_grant_events", script, StringComparison.Ordinal);

        // A grant's revocation is permanent and its expiry only ever moves closer.
        Assert.Contains("CREATE TRIGGER experience_grants_monotonic", script, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE ON agent_experience.experience_grants", script, StringComparison.Ordinal);
        Assert.Contains("NEW.expires_at > OLD.expires_at", script, StringComparison.Ordinal);

        // A tamperer is told it is a permission failure, not an incidental constraint.
        Assert.Contains("ERRCODE = 'insufficient_privilege'", script, StringComparison.Ordinal);

        // TRUNCATE does not fire FOR EACH ROW triggers, so a row-level guard alone leaves the whole log
        // erasable with no error. Statement-level triggers are the only thing that catches it.
        Assert.Contains("BEFORE TRUNCATE ON agent_experience.lifecycle_events", script, StringComparison.Ordinal);
        Assert.Contains("BEFORE TRUNCATE ON agent_experience.experience_grant_events", script, StringComparison.Ordinal);
        Assert.Contains("BEFORE TRUNCATE ON agent_experience.experience_grants", script, StringComparison.Ordinal);
        Assert.Contains("FOR EACH STATEMENT", script, StringComparison.Ordinal);

        // Deleting a revoked grant and inserting it again would restore access the trail says ended.
        Assert.Contains("BEFORE DELETE ON agent_experience.experience_grants", script, StringComparison.Ordinal);

        // An immutable log beside a freely rewritable projection proves nothing.
        Assert.Contains("BEFORE UPDATE ON agent_experience.experience_records", script, StringComparison.Ordinal);
        Assert.Contains("NEW.revision < OLD.revision", script, StringComparison.Ordinal);

        // A grant's identity is pinned, so a live grant cannot be re-pointed at another record.
        Assert.Contains("NEW.experience_id IS DISTINCT FROM OLD.experience_id", script, StringComparison.Ordinal);
        Assert.Contains("NEW.recipient_team_id IS DISTINCT FROM OLD.recipient_team_id", script, StringComparison.Ordinal);

        // ENABLE ALWAYS, or session_replication_role = 'replica' skips every one of them silently.
        foreach (var trigger in new[]
        {
            "lifecycle_events_append_only", "lifecycle_events_no_truncate",
            "experience_grant_events_append_only", "experience_grant_events_no_truncate",
            "experience_grants_monotonic", "experience_grants_audited_delete", "experience_grants_no_truncate",
            "experience_records_projection_guard",
        })
        {
            Assert.Contains($"ENABLE ALWAYS TRIGGER {trigger}", script, StringComparison.Ordinal);
        }

        // The status CHECK the replacement rule compares against; without it 'Superseded' is one string
        // among infinitely many a non-blank column would accept.
        Assert.Contains("lifecycle_events_current_status_known", script, StringComparison.Ordinal);
        Assert.Contains("lifecycle_events_prior_status_known", script, StringComparison.Ordinal);

        // Every CHECK is deferred, so a database holding a pre-0006 Superseded event still upgrades.
        Assert.Equal(4, CountOccurrences(script, "NOT VALID;"));
        Assert.Contains("VALIDATE CONSTRAINT lifecycle_events_replacement_only_when_superseded", script, StringComparison.Ordinal);

        // The header has to say what replaces DELETE now that nothing can delete, and point at 4.5.
        Assert.Contains("DELETION AND RETENTION", script, StringComparison.Ordinal);
        Assert.Contains("DISABLE TRIGGER lifecycle_events_append_only", script, StringComparison.Ordinal);
        Assert.Contains("session_replication_role", script, StringComparison.Ordinal);

        // The header must say plainly what the triggers do not bind, because a reader who assumes
        // otherwise would treat this as tamper-proofing it is not.
        Assert.Contains("superuser", script, StringComparison.Ordinal);
        Assert.Contains("owner", script, StringComparison.Ordinal);

        var statements = string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // What the script *executes* adds a nullable column, deferred CHECKs, an index and triggers.
        // Nothing is dropped, nothing is retyped, and no trigger is recreated through a window in which
        // the log would be unguarded.
        Assert.DoesNotContain("DROP", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER COLUMN", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISABLE TRIGGER", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE EXTENSION", statements, StringComparison.OrdinalIgnoreCase);

        // 0006 is applied after 0005 and before 0007, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.SupersessionAndAppendOnlyScriptName,
            PostgresExperienceRecordSchema.ScriptNames[^7]);
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
    }

    [Fact]
    public void Confidence_script_adds_the_evidence_ledger_and_guards_the_columns_it_starts_moving()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.ConfidenceEvidenceScriptName);

        // The independence rule is the index, and the key it is on is generated -- a writer that could
        // choose its own key could submit one observation under a fresh key every time.
        Assert.Contains("CREATE TABLE IF NOT EXISTS agent_experience.confidence_evidence", script, StringComparison.Ordinal);
        Assert.Contains("GENERATED ALWAYS AS", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS ux_confidence_evidence_independence", script, StringComparison.Ordinal);

        // Partial, so a later submission for a taken key is recorded rather than rejected: the counters
        // must not move, but the submission belongs in the audit trail either way.
        Assert.Contains("WHERE counted;", script, StringComparison.Ordinal);

        // The projection guard now covers reuse confidence and its counters, and ties them to the event
        // that recorded them: advancing the revision alone is not a way to set any number you like.
        Assert.Contains("NEW.reuse_confidence IS DISTINCT FROM OLD.reuse_confidence", script, StringComparison.Ordinal);
        Assert.Contains("e.new_reuse_confidence = NEW.reuse_confidence", script, StringComparison.Ordinal);

        // The run and the round are a host trust boundary, and the header has to say so rather than
        // leaving a reader to believe the generated key makes inflation impossible.
        Assert.Contains("HOST TRUST BOUNDARY", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX CONCURRENTLY", script, StringComparison.Ordinal);
        Assert.Contains("NEW.supporting_validations IS DISTINCT FROM OLD.supporting_validations", script, StringComparison.Ordinal);
        Assert.Contains("NEW.contradictions IS DISTINCT FROM OLD.contradictions", script, StringComparison.Ordinal);

        // The ledger is append-only for the same reason the event logs are.
        foreach (var trigger in new[] { "confidence_evidence_append_only", "confidence_evidence_no_truncate" })
        {
            Assert.Contains($"ENABLE ALWAYS TRIGGER {trigger}", script, StringComparison.Ordinal);
        }

        // Every CHECK added to the already-populated event log is deferred, with the documented step
        // that validates it afterwards.
        Assert.Equal(7, CountOccurrences(script, "NOT VALID;"));
        Assert.Contains("VALIDATE CONSTRAINT lifecycle_events_confidence_all_or_nothing", script, StringComparison.Ordinal);

        // The header has to say what the number is, and what it is not.
        Assert.Contains("THE SCORE IS A HEURISTIC", script, StringComparison.Ordinal);
        Assert.Contains("not the probability", script, StringComparison.Ordinal);

        var statements = string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // Additive only: a new table, new nullable columns, new indexes, a replaced function, and
        // triggers. Nothing is dropped, retyped, or left unguarded through a recreate.
        Assert.DoesNotContain("DROP", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER COLUMN", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISABLE TRIGGER", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE EXTENSION", statements, StringComparison.OrdinalIgnoreCase);

        // 0007 is applied after 0006 and before 0008, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.ConfidenceEvidenceScriptName,
            PostgresExperienceRecordSchema.ScriptNames[^6]);
    }

    [Fact]
    public void Reuse_feedback_script_creates_an_append_only_ledger_that_cannot_claim_unattributed_benefit()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.ReuseFeedbackScriptName);

        Assert.Contains("CREATE TABLE IF NOT EXISTS agent_experience.reuse_feedback", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS agent_experience.reuse_feedback_exposures", script, StringComparison.Ordinal);

        // The whole story, in one CHECK: "no attribution" and "benefit Unknown" are one fact, so a row
        // can never claim an improvement nothing attributed.
        Assert.Contains("(attribution_source = 'None') = (benefit = 'Unknown')", script, StringComparison.Ordinal);

        // Exactly the attributed exposures carry the derived evidence ID that produced a confidence
        // submission, so the two ledgers can be joined and neither can invent a row in the other.
        Assert.Contains("(evidence_id IS NOT NULL) = attributed", script, StringComparison.Ordinal);

        // Each attribution shape carries exactly the identifiers its evidence is keyed on. Without this a
        // machine row with no round -- or a human row with no reviewer -- would key under nothing.
        Assert.Contains("reuse_feedback_human_names_its_reviewer", script, StringComparison.Ordinal);
        Assert.Contains("reuse_feedback_comparative_names_its_round", script, StringComparison.Ordinal);

        // The header has to say what a caller's own claim is worth, and what the run and round are.
        Assert.Contains("EXPOSURE IS NOT ATTRIBUTION", script, StringComparison.Ordinal);
        Assert.Contains("HOST TRUST BOUNDARY", script, StringComparison.Ordinal);
        Assert.Contains("claimed_benefit IS RECORDED AND NEVER ACTED ON", script, StringComparison.Ordinal);

        // The human shape is the weakest boundary here, so the header has to say so as loudly as it says
        // it for the run and the round -- a reader must not come away believing a human assessment is
        // checked by anything.
        Assert.Contains("A HUMAN ASSESSMENT IS THE WEAKEST BOUNDARY HERE", script, StringComparison.Ordinal);
        Assert.Contains("assessment_id IS NOT NULL", script, StringComparison.Ordinal);

        // An attributed exposure's evidence ID says which ID, not that it landed, and an auditor who
        // inner-joins on it silently drops exactly the rows worth looking at.
        Assert.Contains("evidence_id SAYS WHICH ID, NOT THAT IT LANDED", script, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN agent_experience.confidence_evidence", script, StringComparison.Ordinal);

        // The comparative shape has to carry the evidence it concluded from, and the ordinal bound is
        // the schema's mirror of Core's cap on a submission's fan-out.
        Assert.Contains("array_length(evidence_ids, 1) >= 1", script, StringComparison.Ordinal);
        Assert.Contains($"ordinal < {ExperienceReuseFeedback.MaxExposedRecords}", script, StringComparison.Ordinal);

        // The deferred constraint and the out-of-band index build both have to be documented, exactly as
        // 0007 documents its own.
        Assert.Equal(1, CountOccurrences(script, "NOT VALID;"));
        Assert.Contains("VALIDATE CONSTRAINT reuse_feedback_exposures_submission_fkey", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX CONCURRENTLY", script, StringComparison.Ordinal);

        // Append-only for the same reason the event logs are: an editable row could rewrite what a run
        // was exposed to, or free a derived evidence ID for a second submission.
        foreach (var trigger in new[]
        {
            "reuse_feedback_append_only",
            "reuse_feedback_no_truncate",
            "reuse_feedback_exposures_append_only",
            "reuse_feedback_exposures_no_truncate",
        })
        {
            Assert.Contains($"ENABLE ALWAYS TRIGGER {trigger}", script, StringComparison.Ordinal);
        }

        var statements = string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // Additive only, like every script before it, and it touches no existing table.
        Assert.DoesNotContain("DROP", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER COLUMN", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISABLE TRIGGER", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE EXTENSION", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("experience_records", statements, StringComparison.OrdinalIgnoreCase);

        // 0008 is applied after 0007 and before 0009, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.ReuseFeedbackScriptName,
            PostgresExperienceRecordSchema.ScriptNames[^5]);
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
    }

    [Fact]
    public void Delete_script_adds_one_erasure_path_and_leaves_every_guard_armed()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.DeleteAndExpireScriptName);

        // The tombstone column, and the CHECK that makes "erased" one shape rather than a flag a writer
        // could set over a payload that is still there. Deferred like every CHECK on an existing table,
        // with the documented confirm-then-VALIDATE step.
        Assert.Contains("ADD COLUMN IF NOT EXISTS deleted_at timestamptz NULL", script, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(script, "NOT VALID;"));
        Assert.Contains("VALIDATE CONSTRAINT experience_records_tombstone_shape", script, StringComparison.Ordinal);

        // The retained list is stated in the script itself, not only in the README.
        Assert.Contains("WHAT IS RETAINED AFTER A DELETE, EXHAUSTIVELY", script, StringComparison.Ordinal);

        // The honesty statement 0006's header demands of anything that touches these guards: this is a
        // single code path, not a privilege boundary, and the script has to say so in as many words.
        Assert.Contains("NOT A PRIVILEGE BOUNDARY", script, StringComparison.Ordinal);
        Assert.Contains("settable by any session", script, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE", script, StringComparison.Ordinal);

        // And what erasure does not reach, stated rather than left to be assumed.
        Assert.Contains("WHAT DELETION DOES NOT REACH", script, StringComparison.Ordinal);
        Assert.Contains("VACUUM", script, StringComparison.Ordinal);

        var statements = string.Join(
            '\n',
            script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // 0006 is journaled and must never be edited, so its guards are replaced in place: every
        // ENABLE ALWAYS binding survives, and no trigger is ever dropped, disabled, or recreated through
        // a window in which a log would be unguarded. Nothing this script ships is dropped either.
        foreach (var destructive in new[] { "DROP TRIGGER", "DROP FUNCTION", "DROP TABLE", "DROP INDEX", "DROP CONSTRAINT" })
        {
            Assert.DoesNotContain(destructive, statements, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("DISABLE TRIGGER", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER COLUMN", statements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE EXTENSION", statements, StringComparison.OrdinalIgnoreCase);

        // The only triggers this script creates are the two new ones over experience_records -- the
        // guard that makes "no path removes a record row" a property of the schema. Every trigger 0006
        // created is left exactly where it is, guarded by the function bodies replaced above.
        var createdTriggers = statements
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("CREATE TRIGGER ", StringComparison.Ordinal))
            .Select(line => line["CREATE TRIGGER ".Length..])
            .ToArray();

        Assert.Equal(["experience_records_no_delete", "experience_records_no_truncate"], createdTriggers);

        // ...and both are ENABLE ALWAYS, so they survive session_replication_role = 'replica' exactly as
        // 0006's do. A guard a replica connection could step around would not be one.
        foreach (var trigger in createdTriggers)
        {
            Assert.Contains(
                $"ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER {trigger};",
                statements,
                StringComparison.Ordinal);
        }

        // The bare DELETE the guard closes: the one statement that frees an experience_id, so that a
        // record recreated under it inherits every grant issued over the old content.
        Assert.Contains("CREATE OR REPLACE FUNCTION agent_experience.reject_record_removal()", statements, StringComparison.Ordinal);
        Assert.Contains("BEFORE DELETE ON agent_experience.experience_records", statements, StringComparison.Ordinal);
        Assert.Contains("BEFORE TRUNCATE ON agent_experience.experience_records", statements, StringComparison.Ordinal);

        // It takes no marker and has no exception, because the erasure never deletes that row: it
        // updates it into a tombstone. A marker clause here would be a bypass with nothing to justify it.
        Assert.DoesNotContain("purge_authorized", RejectRecordRemovalBody(statements), StringComparison.Ordinal);

        foreach (var guard in new[]
        {
            "agent_experience.reject_event_log_mutation",
            "agent_experience.reject_audited_grant_delete",
            "agent_experience.enforce_record_projection",
        })
        {
            Assert.Contains($"CREATE OR REPLACE FUNCTION {guard}()", statements, StringComparison.Ordinal);
        }

        // The marker is transaction-scoped and set only inside the two purge functions. Anywhere else it
        // would be a switch a caller could leave on.
        Assert.Equal(2, CountOccurrences(statements, "SET LOCAL agent_experience.purge_authorized = 'on';"));
        Assert.Equal(2, CountOccurrences(statements, "SET agent_experience.purge_authorized = 'off'"));

        // The exception is a DELETE on the five tables an erasure sweeps, and nothing else: UPDATE and
        // TRUNCATE stay refused in every session, marked or not.
        Assert.Contains("IF TG_OP = 'DELETE'", statements, StringComparison.Ordinal);
        foreach (var table in new[]
        {
            "'lifecycle_events'",
            "'experience_grant_events'",
            "'confidence_evidence'",
            "'reuse_feedback'",
            "'reuse_feedback_exposures'",
        })
        {
            Assert.Contains(table, statements, StringComparison.Ordinal);
        }

        // Who read a record before it was deleted outlives the record: the access log is not a table the
        // marker admits a delete on, and nothing in this script removes a row from it.
        Assert.DoesNotContain("'experience_grant_access'", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM agent_experience.experience_grant_access", statements, StringComparison.Ordinal);

        // The erasure order is the frozen one. Evidence before the tombstone, because it has no scope
        // columns of its own; children before parents; grant events before grants; the record last.
        var order = new[]
        {
            "DELETE FROM agent_experience.confidence_evidence",
            "DELETE FROM agent_experience.reuse_feedback_exposures",
            "DELETE FROM agent_experience.reuse_feedback f",
            "DELETE FROM agent_experience.experience_grant_events",
            "DELETE FROM agent_experience.experience_grants WHERE experience_id",
            "DELETE FROM agent_experience.lifecycle_events",
            "DELETE FROM agent_experience.experience_embeddings",
            "UPDATE agent_experience.experience_records r",
        };

        var previous = -1;
        foreach (var step in order)
        {
            var at = statements.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at > previous, $"Erasure step out of order: {step}");
            previous = at;
        }

        // The embeddings table belongs to the vectors package, so the base purge tolerates its absence:
        // the step is guarded by to_regclass and issued through EXECUTE, which is what keeps a base-only
        // database from ever parsing a reference to a table it does not have.
        Assert.Contains("to_regclass('agent_experience.experience_embeddings') IS NOT NULL", statements, StringComparison.Ordinal);
        Assert.Contains("EXECUTE 'DELETE FROM agent_experience.experience_embeddings", statements, StringComparison.Ordinal);

        // The tombstone writes a fixed value into every column that is not on the retained list.
        Assert.Contains("payload = '{}'::jsonb", statements, StringComparison.Ordinal);
        Assert.Contains("task_id = '(deleted)'", statements, StringComparison.Ordinal);
        Assert.Contains("created_at = p_deleted_at", statements, StringComparison.Ordinal);
        Assert.Contains("revision = r.revision + 1", statements, StringComparison.Ordinal);

        // Two submissions sharing one record cannot be left orphaned by two purges racing: the
        // submissions are locked before the exposures are deleted, so the second purge's "are there any
        // exposures left?" runs after the first has committed rather than against its own stale snapshot.
        Assert.True(
            statements.IndexOf("FROM agent_experience.reuse_feedback f", StringComparison.Ordinal)
                < statements.IndexOf("DELETE FROM agent_experience.reuse_feedback_exposures", StringComparison.Ordinal),
            "The shared submissions must be locked FOR UPDATE before their exposures are deleted.");
        Assert.Contains("ORDER BY f.feedback_id\n        FOR UPDATE;", statements, StringComparison.Ordinal);

        // The marked exception is shape-checked for the scope, the timestamps and the envelope version,
        // not only for the payload columns -- otherwise one marked UPDATE could tombstone a record into
        // another tenant's scope, where the owner would see NotFound for its own erased record.
        foreach (var pinned in new[]
        {
            "NEW.payload_version = OLD.payload_version",
            "NEW.created_at = NEW.deleted_at",
            "NEW.updated_at = NEW.deleted_at",
            "NEW.tenant_id = OLD.tenant_id",
            "NEW.application_id = OLD.application_id",
            "NEW.project_id = OLD.project_id",
            "NEW.team_id IS NOT DISTINCT FROM OLD.team_id",
            "NEW.agent_id IS NOT DISTINCT FROM OLD.agent_id",
            "NEW.user_id IS NOT DISTINCT FROM OLD.user_id",
        })
        {
            Assert.Contains(pinned, statements, StringComparison.Ordinal);
        }

        // The grant purge bounds its own batch: LIMIT NULL means "no limit" in PostgreSQL, so a bound
        // that lived only in the C# validator was no bound at all for a hand-caller.
        Assert.Contains(
            $"least(greatest(coalesce(p_limit, {PostgresExperienceRecordStore.MaxSweepBatchSize}), "
                + $"{PostgresExperienceRecordStore.MinSweepBatchSize}), {PostgresExperienceRecordStore.MaxSweepBatchSize})",
            statements,
            StringComparison.Ordinal);
        Assert.Contains("LIMIT v_limit", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT p_limit", statements, StringComparison.Ordinal);

        // ...and it never destroys more than the database itself considers expired, however the host's
        // clock is set. Every read of a grant already uses clock_timestamp() for the same reason.
        Assert.Contains("least(p_now, pg_catalog.clock_timestamp())", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("g.expires_at <= p_now", statements, StringComparison.Ordinal);

        // Step 8's guard checks the embedding table's shape, not only its existence: a divergent table
        // would otherwise abort the whole erasure with a bare undefined_column.
        Assert.Contains("a.attname = 'experience_id'", statements, StringComparison.Ordinal);
        Assert.Contains("ERRCODE = 'undefined_column'", statements, StringComparison.Ordinal);

        // The most severe thing this script could have shipped: two SECURITY DEFINER functions with
        // PostgreSQL's default EXECUTE grant to PUBLIC, which would let any role that can connect erase
        // any tenant's record. Revoked, and granted back only to the role applying the migration.
        Assert.Equal(2, CountOccurrences(statements, "SECURITY DEFINER"));
        Assert.Equal(2, CountOccurrences(statements, "FROM PUBLIC;"));
        Assert.Equal(2, CountOccurrences(statements, "TO CURRENT_USER;"));
        foreach (var purge in new[]
        {
            "agent_experience.purge_experience_record(\n    uuid, text, text, text, text, text, text, bigint, timestamptz)",
            "agent_experience.purge_expired_grants(\n    text, text, text, text, text, text, timestamptz, integer)",
        })
        {
            Assert.Contains($"REVOKE ALL ON FUNCTION {purge} FROM PUBLIC;", statements, StringComparison.Ordinal);
            Assert.Contains($"GRANT EXECUTE ON FUNCTION {purge} TO CURRENT_USER;", statements, StringComparison.Ordinal);
        }

        // The documentation fixes this story's reviewers asked for, pinned so they cannot quietly go
        // back to reassuring: the dead heap tuple still holds the erased text, the index builds are not
        // free, and payload_version is on the retained list rather than an exception to it.
        Assert.Contains("STILL CARRIES THE ERASED TEXT", script, StringComparison.Ordinal);
        Assert.Contains("VACUUM (VERBOSE) agent_experience.experience_records;", script, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_records_live_by_age", script, StringComparison.Ordinal);
        Assert.Contains("     payload_version.", script, StringComparison.Ordinal);
        Assert.Contains("ADAPTER-ENFORCED", script, StringComparison.Ordinal);
        Assert.Contains("SCHEMA-ENFORCED", script, StringComparison.Ordinal);

        // 0010 is applied immediately before 0011 and 0012, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.DeleteAndExpireScriptName,
            PostgresExperienceRecordSchema.ScriptNames[^3]);
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
    }

    [Fact]
    public void Grant_disclosure_script_is_applied_last_and_restates_0006s_monotonicity_with_the_level_pinned()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.GrantDisclosureScriptName);
        var original = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.SupersessionAndAppendOnlyScriptName);

        // Least disclosure is the default, for existing grants and for writers that bypass the store.
        Assert.Contains("ADD COLUMN IF NOT EXISTS disclosure text NOT NULL DEFAULT 'LessonOnly'", script, StringComparison.Ordinal);
        Assert.Contains("CHECK (disclosure IN ('LessonOnly', 'LessonAndApproach'))", script, StringComparison.Ordinal);

        // The two ledgers: nullable, with the not-null rule deferred so pre-0011 rows are not scanned.
        Assert.Equal(2, CountOccurrences(script, "ADD COLUMN IF NOT EXISTS disclosure text NULL;"));
        Assert.Equal(2, CountOccurrences(script, "NOT VALID;"));
        Assert.Contains("experience_grant_events_disclosure_recorded", script, StringComparison.Ordinal);
        Assert.Contains("experience_grant_access_disclosure_recorded", script, StringComparison.Ordinal);

        // The monotonicity function is replaced in place with every one of 0006's pins, plus the level.
        Assert.Contains("CREATE OR REPLACE FUNCTION agent_experience.enforce_grant_monotonicity()", script, StringComparison.Ordinal);
        foreach (var pin in original.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains("IS DISTINCT FROM OLD.", StringComparison.Ordinal) && line.StartsWith("OR NEW.", StringComparison.Ordinal)))
        {
            Assert.Contains(pin, script, StringComparison.Ordinal);
        }

        Assert.Contains("OR NEW.disclosure IS DISTINCT FROM OLD.disclosure", script, StringComparison.Ordinal);

        // The upgrade note says what changes for a running deployment.
        Assert.Contains("THIS CHANGES BEHAVIOUR", script, StringComparison.Ordinal);

        Assert.Equal(
            PostgresExperienceRecordSchema.GrantDisclosureScriptName,
            PostgresExperienceRecordSchema.ScriptNames[^2]);
    }

    [Fact]
    public void Grant_access_retention_script_is_applied_last_and_restates_0010s_guard_with_one_narrow_exception()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.GrantAccessRetentionScriptName);
        var deleteAndExpire = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.DeleteAndExpireScriptName);
        var statements = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        // One SECURITY DEFINER function, with PostgreSQL's default EXECUTE to PUBLIC revoked and granted
        // back only to the migrating role, and its search_path pinned so nothing resolves through a
        // caller's.
        const string Signature = "agent_experience.purge_grant_access(\n    text, text, text, text, text, text, boolean, timestamptz, integer)";
        Assert.Equal(1, CountOccurrences(statements, "SECURITY DEFINER"));
        Assert.Contains($"REVOKE ALL ON FUNCTION {Signature} FROM PUBLIC;", statements, StringComparison.Ordinal);
        Assert.Contains($"GRANT EXECUTE ON FUNCTION {Signature} TO CURRENT_USER;", statements, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(statements, "FROM PUBLIC;"));
        Assert.Equal(1, CountOccurrences(statements, "TO CURRENT_USER;"));
        Assert.Contains("SET search_path = pg_catalog, agent_experience", statements, StringComparison.Ordinal);

        // Its own marker, reset at function exit, and never 0010's.
        Assert.Contains("SET agent_experience.access_purge_authorized = 'off'", statements, StringComparison.Ordinal);
        Assert.Contains("SET LOCAL agent_experience.access_purge_authorized = 'on';", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("SET LOCAL agent_experience.purge_authorized", statements, StringComparison.Ordinal);

        // The guard is replaced in place, with 0010's exception list carried over verbatim and the
        // access ledger still absent from it.
        Assert.Contains("CREATE OR REPLACE FUNCTION agent_experience.reject_event_log_mutation()", statements, StringComparison.Ordinal);
        const string ZeroTenList =
            "        AND TG_TABLE_NAME IN (\n" +
            "            'lifecycle_events',\n" +
            "            'experience_grant_events',\n" +
            "            'confidence_evidence',\n" +
            "            'reuse_feedback',\n" +
            "            'reuse_feedback_exposures')";
        Assert.Contains(ZeroTenList, deleteAndExpire, StringComparison.Ordinal);
        Assert.Contains(ZeroTenList, statements, StringComparison.Ordinal);
        Assert.DoesNotContain("DISABLE TRIGGER", statements, StringComparison.Ordinal);

        // The floor, twice, on the database's clock and on recorded_at; bounded; refused rather than clamped.
        Assert.Equal(
            2,
            CountOccurrences(statements, $"pg_catalog.clock_timestamp() - interval '{PostgresExperienceGrantAccessLog.MinimumRetentionDays} days'"));
        Assert.DoesNotContain("interval '", statements.Replace($"interval '{PostgresExperienceGrantAccessLog.MinimumRetentionDays} days'", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("SET search_path = pg_catalog, agent_experience;", statements, StringComparison.Ordinal);
        Assert.Contains("TG_TABLE_SCHEMA = 'agent_experience' AND TG_TABLE_NAME = 'experience_grant_access'", statements, StringComparison.Ordinal);
        Assert.Contains("OLD.recorded_at <=", statements, StringComparison.Ordinal);
        Assert.Contains("a.recorded_at < p_cutoff", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("occurred_at <", statements, StringComparison.Ordinal);
        Assert.Contains("least(greatest(coalesce(p_limit, 500), 1), 500)", statements, StringComparison.Ordinal);
        Assert.Contains("'CutoffTooRecent'", statements, StringComparison.Ordinal);

        // The out-of-band index runbook is in the header.
        Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_grant_access_retention", script, StringComparison.Ordinal);

        Assert.Equal(
            PostgresExperienceRecordSchema.GrantAccessRetentionScriptName,
            PostgresExperienceRecordSchema.ScriptNames[^1]);
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
    }

    [Fact]
    public async Task A_subtree_sweep_or_access_purge_outside_the_authorization_is_Denied_before_any_connection_opens()
    {
        var auth = Authorize("tenant-a");
        var foreignRoot = new Scope("tenant-b", "app-1", "project-1");
        var access = new PostgresExperienceGrantAccessLog(_dataSource);

        var swept = await Store.SweepExpiredAsync(auth, foreignRoot, TimeSpan.FromDays(1), 10, ScopeMatch.Subtree, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, swept.Outcome);

        var purged = await access.PurgeOlderThanAsync(
            auth, new GrantAdministration("admin", DateTimeOffset.UtcNow), foreignRoot, DateTimeOffset.UtcNow.AddDays(-400), ScopeMatch.Subtree, 10, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Denied, purged.Outcome);

        // A team-bounded authorization cannot take a project root, so a subtree cannot widen it.
        var teamOnly = new AuthorizationContext("tenant-a", "host-principal", ["experience:write"], DateTimeOffset.UtcNow, TeamId: "t1");
        var projectRoot = new Scope("tenant-a", "app-1", "project-1");
        Assert.Equal(ExperienceStoreOutcome.Denied, (await Store.SweepExpiredAsync(teamOnly, projectRoot, TimeSpan.FromDays(1), 10, ScopeMatch.Subtree, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.Denied,
            (await access.PurgeOlderThanAsync(teamOnly, new GrantAdministration("admin", DateTimeOffset.UtcNow), projectRoot, DateTimeOffset.UtcNow.AddDays(-400), ScopeMatch.Subtree, 10, CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// The body of <c>reject_record_removal</c> alone, so "it takes no marker" is asserted about that
    /// function rather than about a script that mentions the marker several times elsewhere.
    /// </summary>
    private static string RejectRecordRemovalBody(string statements)
    {
        const string Start = "CREATE OR REPLACE FUNCTION agent_experience.reject_record_removal()";
        var from = statements.IndexOf(Start, StringComparison.Ordinal);
        Assert.True(from >= 0, "0010 no longer defines agent_experience.reject_record_removal().");

        var to = statements.IndexOf("$body$ LANGUAGE plpgsql;", from, StringComparison.Ordinal);
        Assert.True(to > from, "agent_experience.reject_record_removal() has no terminated body.");
        return statements[from..to];
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void Scope_predicates_stay_in_step_across_aliases()
    {
        // The e-aliased predicate used to be derived from the r-aliased one by replacing "r." with "e.",
        // which would also rewrite any future parameter or column name containing those two characters --
        // and the failure would be a silently wrong scope filter inside the recursive chain walk rather
        // than a syntax error. They are written out separately now, so this keeps them equivalent.
        Assert.Equal(
            PostgresExperienceRecordStore.RecordScopePredicate,
            PostgresExperienceRecordStore.EventScopePredicate.Replace("e.", "r.", StringComparison.Ordinal));

        // Both bind exactly the six scope parameters every statement already adds, and no others.
        foreach (var parameter in new[] { "@tenant_id", "@application_id", "@project_id", "@team_id", "@agent_id", "@user_id" })
        {
            Assert.Contains(parameter, PostgresExperienceRecordStore.EventScopePredicate, StringComparison.Ordinal);
        }

        Assert.Equal(6, CountOccurrences(PostgresExperienceRecordStore.EventScopePredicate, "e."));
    }

    [Fact]
    public void The_grant_predicate_is_correlated_expiry_checked_and_composed_from_the_exact_one()
    {
        // This inspects the predicate constants only. It cannot say which statements compose them --
        // that is proved behaviourally against the container in PostgresGrantTests, which is where the
        // "writes are never widened" and "history stays owner-scope" claims are actually tested.
        Assert.Contains("g.revoked_at IS NULL", PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);

        // clock_timestamp(), not now(): now() is fixed at transaction start, so inside a caller-held
        // transaction an expired grant would keep permitting reads.
        Assert.Contains("g.expires_at > clock_timestamp()", PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("expires_at > now()", PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);
        Assert.Contains("g.recipient_tenant_id = @tenant_id", PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);

        // The record side of the correlation is aliased, never bare: a bare experience_id inside the
        // subquery would bind to the grants table's own column and match every record ever granted.
        Assert.Contains("g.experience_id = r.experience_id", PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("g.experience_id = experience_id", PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);

        Assert.Contains(PostgresExperienceRecordStore.RecordScopePredicate, PostgresExperienceRecordStore.ReadableRecordScopePredicate, StringComparison.Ordinal);
        Assert.Contains(PostgresExperienceRecordStore.ActiveGrantPredicate, PostgresExperienceRecordStore.ReadableRecordScopePredicate, StringComparison.Ordinal);

        // Expiry is the database's clock, never a value this adapter computed and sent.
        Assert.DoesNotContain("@now", PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);

        // The shared-by-grant flag is the negation of the exact match, computed by the same statement
        // that decided readability, so no consumer has to re-derive it by comparing scopes.
        Assert.Contains(PostgresExperienceRecordStore.RecordScopePredicate, PostgresExperienceRecordStore.SharedByGrantColumn, StringComparison.Ordinal);
        Assert.StartsWith("NOT (", PostgresExperienceRecordStore.SharedByGrantColumn, StringComparison.Ordinal);
        Assert.EndsWith(PostgresExperienceRecordStore.SharedByGrantAlias, PostgresExperienceRecordStore.SharedByGrantColumn, StringComparison.Ordinal);

        // The join that NAMES the permitting grant and the predicate that decides whether one exists are
        // the same rule written once. If they could drift, an access row could name a grant that did not
        // permit the read -- which is the one thing the trail must never say.
        Assert.Contains(PostgresExperienceRecordStore.ActiveGrantConditions, PostgresExperienceRecordStore.ActiveGrantPredicate, StringComparison.Ordinal);
        Assert.Contains(PostgresExperienceRecordStore.ActiveGrantConditions, PostgresExperienceRecordStore.PermittingGrantJoin, StringComparison.Ordinal);

        // One grant, chosen the same way every time, so the trail's answer is stable rather than
        // whatever the planner returned first.
        Assert.Contains("ORDER BY g.grant_id LIMIT 1", PostgresExperienceRecordStore.PermittingGrantJoin, StringComparison.Ordinal);

        // LEFT, so a record the requester owns still comes back -- with no grant named.
        Assert.StartsWith("LEFT JOIN LATERAL", PostgresExperienceRecordStore.PermittingGrantJoin, StringComparison.Ordinal);

        // The readable predicate expressed against the join is the exact match or a named grant, and it
        // still carries the exact-scope predicate byte for byte.
        Assert.Contains(PostgresExperienceRecordStore.RecordScopePredicate, PostgresExperienceRecordStore.ReadableWithNamedGrantPredicate, StringComparison.Ordinal);
        Assert.EndsWith(PostgresExperienceRecordStore.PermittingGrantAlias, PostgresExperienceRecordStore.PermittingGrantColumn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_candidate_search_returns_Invalid_with_every_field_path_and_no_database_call()
    {
        var tenant = NewTenant();
        var source = new PostgresExperienceCandidateSource(_dataSource); // unreachable: reaching it would hang or throw

        var result = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(new Scope(tenant, "app-1", " "), "  ", [], 1.5, 0),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Equal(
            ["Scope.ProjectId", "TaskText", "EligibleStatuses", "MinimumConfidence", "Limit"],
            result.Errors.Select(error => error.Path));
    }

    [Fact]
    public async Task Task_text_past_the_maximum_length_is_Invalid_rather_than_reaching_the_parser()
    {
        var tenant = NewTenant();
        var source = new PostgresExperienceCandidateSource(_dataSource);

        var result = await source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(
                Scope(tenant),
                new string('a', ExperienceCandidateQuery.MaxTaskTextLength + 1),
                [ExperienceStatus.Validated],
                0.5),
            CancellationToken.None);

        // A typed Invalid, decided before any connection opens -- not a multi-megabyte round trip that
        // comes back as an opaque infrastructure failure.
        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        var error = Assert.Single(result.Errors);
        Assert.Equal("TaskText", error.Path);
        Assert.DoesNotContain("aaaa", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_candidate_search_outside_the_authorization_is_Denied_before_any_connection_opens()
    {
        var tenant = NewTenant();
        var source = new PostgresExperienceCandidateSource(_dataSource);

        var result = await source.SearchAsync(
            new AuthorizationContext(tenant, "host-principal", [], ColumnTime, ProjectId: "other-project"),
            new ExperienceCandidateQuery(Scope(tenant), "refund", [ExperienceStatus.Validated], 0.5),
            CancellationToken.None);

        // The data source points at a closed port: any connection attempt would have failed instead.
        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task An_unreachable_database_makes_a_candidate_search_throw_ExperienceStoreException()
    {
        var tenant = NewTenant();
        var source = new PostgresExperienceCandidateSource(_dataSource);

        var ex = await Assert.ThrowsAsync<ExperienceStoreException>(() => source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(Scope(tenant), "refund", [ExperienceStatus.Validated], 0.5),
            CancellationToken.None));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task A_cancelled_candidate_search_surfaces_cancellation_unwrapped()
    {
        var tenant = NewTenant();
        var source = new PostgresExperienceCandidateSource(_dataSource);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync(
            Authorize(tenant),
            new ExperienceCandidateQuery(Scope(tenant), "refund", [ExperienceStatus.Validated], 0.5),
            cancellation.Token));
    }

    [Fact]
    public void A_candidate_source_needs_a_data_source()
    {
        Assert.Throws<ArgumentNullException>(() => new PostgresExperienceCandidateSource(null!));
    }

    [Fact]
    public async Task Malformed_lifecycle_commit_returns_Invalid_with_every_field_path_and_no_database_call()
    {
        var tenant = NewTenant();
        var malformed = Event(Guid.Empty, (ExperienceStatus)999, (ExperienceStatus)998, -1, eventId: Guid.Empty, reason: "  ", producer: "")
            with { OccurredAt = default };

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant),
            new Scope(tenant, "app-1", " "),
            malformed,
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(0, result.Revision);
        Assert.Equal(
            new[] { "CurrentStatus", "EventId", "ExperienceRecordId", "ExpectedRevision", "OccurredAt", "PriorStatus", "Producer", "Reason", "Scope.ProjectId" }
                .Order(StringComparer.Ordinal),
            result.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }

    [Theory]
    [InlineData(long.MaxValue)]       // ExpectedRevision + 1 would wrap
    [InlineData(long.MaxValue - 1)]   // commits, but then no later commit could ever be expressed
    public async Task A_revision_that_leaves_no_room_for_the_next_one_is_Invalid(long expectedRevision)
    {
        var tenant = NewTenant();

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant),
            Scope(tenant),
            Event(Guid.NewGuid(), ExperienceStatus.Candidate, ExperienceStatus.Revoked, expectedRevision),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["ExpectedRevision"], result.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task An_unset_OccurredAt_is_Invalid_because_it_is_part_of_the_replay_identity()
    {
        var tenant = NewTenant();
        var unset = Event(Guid.NewGuid(), ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0) with { OccurredAt = default };

        var result = await Store.CommitLifecycleEventAsync(Authorize(tenant), Scope(tenant), unset, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["OccurredAt"], result.Errors.Select(e => e.Path));
    }

    [Fact]
    public async Task Malformed_history_request_returns_Invalid()
    {
        var tenant = NewTenant();

        var result = await Store.GetFirstHistoryPageAsync(Authorize(tenant), new Scope(tenant, "", "project-1"), Guid.Empty, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal(["ExperienceId", "Scope.ApplicationId"], result.Errors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Empty(result.Events);
        Assert.Equal(0, result.Revision);
    }

    [Fact]
    public async Task A_scope_beyond_the_authorization_is_Denied_before_any_connection_opens()
    {
        var scope = Scope("tenant-b");
        var auth = Authorize("tenant-a");

        var commit = await Store.CommitLifecycleEventAsync(
            auth, scope, Event(Guid.NewGuid(), ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);
        var history = await Store.GetFirstHistoryPageAsync(auth, scope, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, commit.Outcome);
        Assert.Empty(commit.Errors);
        Assert.Equal(ExperienceStoreOutcome.Denied, history.Outcome);
        Assert.Empty(history.Events);
    }

    [Fact]
    public async Task An_unavailable_database_throws_for_commit_and_history_and_a_pre_cancelled_token_does_not()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var lifecycleEvent = Event(Guid.NewGuid(), ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);

        var commit = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => Store.CommitLifecycleEventAsync(auth, scope, lifecycleEvent, CancellationToken.None));
        var history = await Assert.ThrowsAsync<ExperienceStoreException>(
            () => Store.GetFirstHistoryPageAsync(auth, scope, lifecycleEvent.ExperienceRecordId, CancellationToken.None));
        Assert.All([commit, history], ex => Assert.IsAssignableFrom<Npgsql.NpgsqlException>(ex.InnerException));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Store.CommitLifecycleEventAsync(auth, scope, lifecycleEvent, cts.Token));
        Assert.IsNotType<ExperienceStoreException>(cancelled);
    }

    [Fact]
    public async Task Null_lifecycle_arguments_throw_ArgumentNullException()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var lifecycleEvent = Event(Guid.NewGuid(), ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);

        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.CommitLifecycleEventAsync(null!, scope, lifecycleEvent, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.CommitLifecycleEventAsync(Authorize(tenant), null!, lifecycleEvent, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.CommitLifecycleEventAsync(Authorize(tenant), scope, null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.GetFirstHistoryPageAsync(null!, scope, Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Store.GetFirstHistoryPageAsync(Authorize(tenant), null!, Guid.NewGuid(), CancellationToken.None));
    }
}
