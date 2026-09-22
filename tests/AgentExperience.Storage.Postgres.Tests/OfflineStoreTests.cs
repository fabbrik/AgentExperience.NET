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

        // 0006 is applied last, which the migrator relies on for ordinal name ordering.
        Assert.Equal(
            PostgresExperienceRecordSchema.SupersessionAndAppendOnlyScriptName,
            PostgresExperienceRecordSchema.ScriptNames[^1]);
        Assert.Equal(
            PostgresExperienceRecordSchema.ScriptNames.Order(StringComparer.Ordinal),
            PostgresExperienceRecordSchema.ScriptNames);
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
