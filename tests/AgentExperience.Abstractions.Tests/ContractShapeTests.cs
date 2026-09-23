using System.Reflection;

namespace AgentExperience.Abstractions.Tests;

/// <summary>
/// Proves the domain contracts defined in Story 1.1 support the shapes required by its
/// acceptance criteria: task identity, ordered attempts, tool metadata, results, errors,
/// duration, scope, environment, and provenance (AC1); representing both a successful and a
/// failed invocation, and a verified vs. an unverified outcome, without private chain-of-thought
/// (AC2); lifecycle events carrying the Experience Record id, prior/current state, reason,
/// producer, and timestamp (AC4); and a host-established <see cref="AuthorizationContext"/>
/// staying distinct from request/record <see cref="Scope"/> (AC7).
/// </summary>
public class ContractShapeTests
{
    [Fact]
    public void ExperienceRun_supports_task_identity_ordered_attempts_tool_metadata_results_errors_duration_scope_environment_and_provenance()
    {
        var scope = new Scope(TenantId: "tenant-1", ApplicationId: "app-1", ProjectId: "project-1", TeamId: "team-1", AgentId: "agent-1", UserId: "user-1");
        var environment = new EnvironmentFingerprint(
            HostName: "worker-01",
            RuntimeVersion: "10.0.0",
            OperatingSystem: "linux-x64",
            ApplicationVersion: "1.0.0",
            Metadata: new Dictionary<string, string> { ["region"] = "us-east" });
        var provenance = new Provenance(
            Source: "AgentExperience.MicrosoftAgentFramework",
            SourceVersion: "1.0.0",
            RecordedAt: DateTimeOffset.UtcNow,
            CorrelationId: "trace-123");

        var firstToolCall = new ToolCallRecord(
            ToolCallId: Guid.NewGuid(),
            SequenceNumber: 0,
            ToolName: "search_docs",
            Arguments: new Dictionary<string, object?> { ["query"] = "refund policy" },
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromMilliseconds(120),
            Result: "3 matching documents",
            Error: null);

        var secondToolCall = new ToolCallRecord(
            ToolCallId: Guid.NewGuid(),
            SequenceNumber: 1,
            ToolName: "update_ticket",
            Arguments: new Dictionary<string, object?> { ["ticketId"] = "T-42" },
            StartedAt: firstToolCall.StartedAt.Add(firstToolCall.Duration),
            Duration: TimeSpan.FromMilliseconds(80),
            Result: null,
            Error: "ticket locked by another agent");

        var firstAttempt = new Attempt(
            AttemptId: Guid.NewGuid(),
            SequenceNumber: 0,
            StartedAt: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(1),
            ToolCalls: [firstToolCall, secondToolCall],
            Result: null,
            Error: "could not update ticket");

        var secondAttempt = new Attempt(
            AttemptId: Guid.NewGuid(),
            SequenceNumber: 1,
            StartedAt: firstAttempt.StartedAt.Add(firstAttempt.Duration),
            Duration: TimeSpan.FromSeconds(2),
            ToolCalls: [],
            Result: "ticket updated after retry",
            Error: null);

        var run = new ExperienceRun(
            RunId: Guid.NewGuid(),
            TaskId: "support-ticket-resolution",
            TaskDescription: "Resolve customer refund ticket T-42",
            Scope: scope,
            Environment: environment,
            Provenance: provenance,
            Attempts: [firstAttempt, secondAttempt],
            ExecutionStatus: RunExecutionStatus.Completed,
            Outcome: null,
            StartedAt: firstAttempt.StartedAt,
            EndedAt: secondAttempt.StartedAt.Add(secondAttempt.Duration));

        // Task identity.
        Assert.Equal("support-ticket-resolution", run.TaskId);

        // Ordered attempts (actions).
        Assert.Equal([0, 1], run.Attempts.Select(a => a.SequenceNumber));
        Assert.Same(firstAttempt, run.Attempts[0]);
        Assert.Same(secondAttempt, run.Attempts[1]);

        // Ordered tool calls (metadata) within an attempt.
        Assert.Equal([0, 1], run.Attempts[0].ToolCalls.Select(t => t.SequenceNumber));
        Assert.Equal("search_docs", run.Attempts[0].ToolCalls[0].ToolName);
        Assert.Equal("update_ticket", run.Attempts[0].ToolCalls[1].ToolName);

        // Results and errors representable at both tool-call and attempt level.
        Assert.Equal("3 matching documents", run.Attempts[0].ToolCalls[0].Result);
        Assert.Null(run.Attempts[0].ToolCalls[0].Error);
        Assert.Null(run.Attempts[0].ToolCalls[1].Result);
        Assert.Equal("ticket locked by another agent", run.Attempts[0].ToolCalls[1].Error);
        Assert.Equal("could not update ticket", run.Attempts[0].Error);
        Assert.Equal("ticket updated after retry", run.Attempts[1].Result);

        // Duration.
        Assert.Equal(TimeSpan.FromMilliseconds(120), run.Attempts[0].ToolCalls[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(1), run.Attempts[0].Duration);

        // Scope, environment, provenance.
        Assert.Equal(scope, run.Scope);
        Assert.Equal(environment, run.Environment);
        Assert.Equal(provenance, run.Provenance);
    }

    [Fact]
    public void ExperienceRun_can_represent_both_a_successful_and_a_failed_invocation_without_a_chain_of_thought_field()
    {
        static ExperienceRun MakeRun(RunExecutionStatus status) => new(
            RunId: Guid.NewGuid(),
            TaskId: "task",
            TaskDescription: null,
            Scope: new Scope("tenant", "app", "project"),
            Environment: new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            Provenance: new Provenance("test", null, DateTimeOffset.UtcNow, null),
            Attempts: [],
            ExecutionStatus: status,
            Outcome: null,
            StartedAt: DateTimeOffset.UtcNow,
            EndedAt: DateTimeOffset.UtcNow);

        var succeeded = MakeRun(RunExecutionStatus.Completed);
        var failed = MakeRun(RunExecutionStatus.Failed);

        Assert.Equal(RunExecutionStatus.Completed, succeeded.ExecutionStatus);
        Assert.Equal(RunExecutionStatus.Failed, failed.ExecutionStatus);

        // Neither representation depends on a private chain-of-thought field existing at all.
        Assert.DoesNotContain(
            typeof(ExperienceRun).GetProperties(),
            p => p.Name.Contains("Thought", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Reasoning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Outcome_represents_a_verified_success_case()
    {
        var evidence = new Evidence(
            EvidenceId: Guid.NewGuid(),
            VerificationRoundId: Guid.NewGuid(),
            ArtifactRevision: "rev-7",
            CheckId: "unit-tests-pass",
            Kind: "TestResult",
            Result: CheckResult.Pass,
            Producer: "ci-test-runner",
            Detail: "42 of 42 tests passed",
            CapturedAt: DateTimeOffset.UtcNow);

        var outcome = new Outcome(
            Status: TaskVerificationStatus.Verified,
            Evidence: [evidence],
            Reason: "all required checks passed in the final round",
            EvaluatedAt: DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Verified, outcome.Status);
        Assert.Single(outcome.Evidence);
        Assert.Equal("rev-7", outcome.Evidence[0].ArtifactRevision);
    }

    [Fact]
    public void Outcome_represents_an_unverified_failure_case_by_surfacing_uncertainty_rather_than_a_false_pass()
    {
        // The run's execution failed, but no evidence exists to verify the task outcome either
        // way -- it must surface as Unknown, never silently reported as a validated Verified
        // procedure and never asserted as a definitive Failed absent evidence.
        var outcome = new Outcome(
            Status: TaskVerificationStatus.Unknown,
            Evidence: [],
            Reason: "run terminated with an unhandled error before any required check produced evidence",
            EvaluatedAt: DateTimeOffset.UtcNow);

        Assert.Equal(TaskVerificationStatus.Unknown, outcome.Status);
        Assert.Empty(outcome.Evidence);
    }

    [Fact]
    public void LifecycleEvent_carries_experience_record_id_prior_and_current_status_reason_producer_and_timestamp()
    {
        var recordId = Guid.NewGuid();
        var evt = new LifecycleEvent(
            EventId: Guid.NewGuid(),
            ExperienceRecordId: recordId,
            PriorStatus: ExperienceStatus.Candidate,
            CurrentStatus: ExperienceStatus.Validated,
            Reason: "verified-evidence commit",
            Producer: "capture-service",
            OccurredAt: DateTimeOffset.UtcNow,
            ExpectedRevision: 1);

        Assert.NotEqual(Guid.Empty, evt.EventId);
        Assert.Equal(recordId, evt.ExperienceRecordId);
        Assert.Equal(ExperienceStatus.Candidate, evt.PriorStatus);
        Assert.Equal(ExperienceStatus.Validated, evt.CurrentStatus);
        Assert.Equal("verified-evidence commit", evt.Reason);
        Assert.Equal("capture-service", evt.Producer);
        Assert.NotEqual(default, evt.OccurredAt);
        Assert.Equal(1, evt.ExpectedRevision);
    }

    [Fact]
    public void LifecycleEvent_allows_a_null_prior_status_for_the_first_event_on_a_record()
    {
        var evt = new LifecycleEvent(
            EventId: Guid.NewGuid(),
            ExperienceRecordId: Guid.NewGuid(),
            PriorStatus: null,
            CurrentStatus: ExperienceStatus.Candidate,
            Reason: "initial capture",
            Producer: "capture-service",
            OccurredAt: DateTimeOffset.UtcNow,
            ExpectedRevision: 0);

        Assert.Null(evt.PriorStatus);
    }

    [Fact]
    public void AuthorizationContext_and_Scope_are_distinct_types_with_no_field_level_conflation()
    {
        Assert.NotEqual(typeof(AuthorizationContext), typeof(Scope));

        var authorizationProperties = typeof(AuthorizationContext).GetProperties().Select(p => p.Name).ToHashSet();
        var scopeProperties = typeof(Scope).GetProperties().Select(p => p.Name).ToHashSet();

        // Distinct shapes: AuthorizationContext's host-established grant has no counterpart on
        // Scope. AuthorizationContext may carry optional bounds with Scope's field names (Story 2.1),
        // but they are nullable restrictions defaulting to null, never a required request identity.
        Assert.Contains("ApplicationId", scopeProperties);
        Assert.Contains("ProjectId", scopeProperties);
        foreach (var bound in new[] { "ApplicationId", "ProjectId", "TeamId", "AgentId", "UserId" })
        {
            Assert.Contains(bound, authorizationProperties);
            Assert.Equal(typeof(string), typeof(AuthorizationContext).GetProperty(bound)!.PropertyType);
        }

        Assert.Contains("PrincipalId", authorizationProperties);
        Assert.Contains("Roles", authorizationProperties);
        Assert.DoesNotContain("PrincipalId", scopeProperties);
        Assert.DoesNotContain("Roles", scopeProperties);

        // Both types can be used side by side without one substituting for the other.
        var authorization = new AuthorizationContext("tenant-1", "svc-principal", ["capture:write"], DateTimeOffset.UtcNow);
        var scope = new Scope("tenant-1", "app-1", "project-1");
        Assert.NotEqual<object>(authorization, scope);
    }

    // Story 2.1: ExperienceRecord, the IExperienceRecordStore port, and AuthorizationContext.Permits.
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExperienceRecord_carries_every_canonical_part_and_no_version_field()
    {
        var parameters = typeof(ExperienceRecord).GetConstructors().Single().GetParameters()
            .Select(p => (p.Name, p.ParameterType))
            .ToList();

        Assert.Equal(
            new (string?, Type)[]
            {
                ("ExperienceId", typeof(Guid)),
                ("SourceRunId", typeof(Guid)),
                ("Scope", typeof(Scope)),
                ("TaskId", typeof(string)),
                ("TaskSummary", typeof(string)),
                ("Attempts", typeof(IReadOnlyList<Attempt>)),
                ("Outcome", typeof(Outcome)),
                ("CompletionScore", typeof(double)),
                ("Reflection", typeof(Reflection)),
                ("Environment", typeof(EnvironmentFingerprint)),
                ("Provenance", typeof(Provenance)),
                ("Status", typeof(ExperienceStatus)),
                ("ReuseConfidence", typeof(double)),
                ("SupportingValidations", typeof(int)),
                ("Contradictions", typeof(int)),
                ("Revision", typeof(long)),
                ("CreatedAt", typeof(DateTimeOffset)),
                ("UpdatedAt", typeof(DateTimeOffset)),
            },
            parameters);

        Assert.DoesNotContain(typeof(ExperienceRecord).GetProperties(), p => p.Name.Contains("Version", StringComparison.OrdinalIgnoreCase));
        Assert.All(typeof(ExperienceRecord).GetProperties(), p => Assert.True(p.SetMethod is null || p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit")));

        var nullability = new NullabilityInfoContext();
        var ctorParameters = typeof(ExperienceRecord).GetConstructors().Single().GetParameters().ToDictionary(p => p.Name!);
        Assert.Equal(NullabilityState.Nullable, nullability.Create(ctorParameters["TaskSummary"]).WriteState);
        Assert.Equal(NullabilityState.Nullable, nullability.Create(ctorParameters["Reflection"]).WriteState);
        Assert.Equal(NullabilityState.NotNull, nullability.Create(ctorParameters["Outcome"]).WriteState);
    }

    [Fact]
    public void Store_port_operations_take_authorization_and_a_required_cancellation_token()
    {
        var methods = typeof(IExperienceRecordStore).GetMethods().OrderBy(m => m.Name, StringComparer.Ordinal).ToList();

        // Two GetAsync overloads: the plain read, and the one that also says what the caller will do
        // with the record. Only auditing looks at the difference, so the second has a default
        // implementation and an existing store keeps compiling.
        Assert.Equal(
            ["CheckSupersessionAsync", "CommitLifecycleEventAsync", "CreateAsync", "GetAsync", "GetAsync", "GetHistoryAsync", "QueryAsync"],
            methods.Select(m => m.Name));
        Assert.All(methods, method =>
        {
            var parameters = method.GetParameters();
            Assert.Equal(typeof(AuthorizationContext), parameters[0].ParameterType);
            Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
            Assert.False(parameters[^1].HasDefaultValue);
        });

        Assert.Equal(typeof(Task<ExperienceSupersessionCheckResult>), methods[0].ReturnType);
        Assert.Equal([typeof(AuthorizationContext), typeof(Scope), typeof(Guid), typeof(Guid), typeof(CancellationToken)], methods[0].GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<ExperienceLifecycleCommitResult>), methods[1].ReturnType);
        Assert.Equal([typeof(AuthorizationContext), typeof(Scope), typeof(LifecycleEvent), typeof(CancellationToken)], methods[1].GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<ExperienceRecordCreateResult>), methods[2].ReturnType);
        Assert.Equal([typeof(AuthorizationContext), typeof(ExperienceRecord), typeof(CancellationToken)], methods[2].GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<ExperienceRecordGetResult>), methods[3].ReturnType);
        Assert.Equal([typeof(AuthorizationContext), typeof(Scope), typeof(Guid), typeof(CancellationToken)], methods[3].GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<ExperienceRecordGetResult>), methods[4].ReturnType);
        Assert.Equal(
            [typeof(AuthorizationContext), typeof(Scope), typeof(Guid), typeof(ExperienceReadOptions), typeof(CancellationToken)],
            methods[4].GetParameters().Select(p => p.ParameterType));

        // The plain read is the contract; the purpose-carrying one has a default implementation that
        // forwards to it, so an existing store keeps compiling and only a store that writes access rows
        // has any reason to override it.
        Assert.True(methods[3].IsAbstract);
        Assert.False(methods[4].IsAbstract);

        Assert.Equal(typeof(Task<ExperienceRecordHistoryResult>), methods[5].ReturnType);
        Assert.Equal([typeof(AuthorizationContext), typeof(ExperienceRecordHistoryQuery), typeof(CancellationToken)], methods[5].GetParameters().Select(p => p.ParameterType));
        Assert.Equal(typeof(Task<ExperienceRecordQueryResult>), methods[6].ReturnType);
        Assert.Equal([typeof(AuthorizationContext), typeof(ExperienceRecordQuery), typeof(CancellationToken)], methods[6].GetParameters().Select(p => p.ParameterType));
    }

    // Story 2.2: the retrieval candidate-source port.
    [Fact]
    public void Candidate_source_port_mirrors_the_store_port_and_stays_separate_from_it()
    {
        var method = Assert.Single(typeof(IExperienceCandidateSource).GetMethods());

        Assert.Equal("SearchAsync", method.Name);
        Assert.Equal(typeof(Task<ExperienceCandidateSearchResult>), method.ReturnType);
        Assert.Equal(
            [typeof(AuthorizationContext), typeof(ExperienceCandidateQuery), typeof(CancellationToken)],
            method.GetParameters().Select(p => p.ParameterType));
        Assert.False(method.GetParameters()[^1].HasDefaultValue);

        // A new port, not an extension of the store: retrieval must not change what a writer implements.
        Assert.DoesNotContain(
            typeof(IExperienceRecordStore).GetMethods(),
            m => m.Name.Contains("Search", StringComparison.Ordinal));
        Assert.False(typeof(IExperienceCandidateSource).IsAssignableFrom(typeof(IExperienceRecordStore)));
    }

    [Fact]
    public void Candidate_query_defaults_to_a_limit_of_50_within_1_to_200_and_a_candidate_carries_a_normalized_relevance()
    {
        var query = new ExperienceCandidateQuery(new Scope("t", "a", "p"), "refund", [ExperienceStatus.Validated], 0.5);

        Assert.Equal(50, query.Limit);
        Assert.Equal(50, ExperienceCandidateQuery.DefaultLimit);
        Assert.Equal(1, ExperienceCandidateQuery.MinLimit);
        Assert.Equal(200, ExperienceCandidateQuery.MaxLimit);

        var record = new ExperienceRecord(
            Guid.NewGuid(), Guid.NewGuid(), query.Scope, "task", null, [],
            new Outcome(TaskVerificationStatus.Verified, [], null, Now), 1, null,
            new EnvironmentFingerprint("host", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
            new Provenance("tests", null, Now, null),
            ExperienceStatus.Validated, 0.8, 1, 0, 1, Now, Now);

        var found = new ExperienceCandidateSearchResult(
            ExperienceStoreOutcome.Found, [new ExperienceCandidate(record, 0.42)], []);

        Assert.Equal(0.42, Assert.Single(found.Candidates).Relevance);
        Assert.Same(record, found.Candidates[0].Record);

        // The result reuses the store's outcome enum, which has no timeout member: a retrieval timeout
        // is Core's own result type, never a storage outcome.
        Assert.DoesNotContain("Timeout", Enum.GetNames<ExperienceStoreOutcome>());
        Assert.Empty(new ExperienceCandidateSearchResult(ExperienceStoreOutcome.Denied, [], []).Candidates);
    }

    // Story 2.4: the lifecycle commit and history contracts.
    [Fact]
    public void Lifecycle_commit_and_history_results_carry_a_revision_and_ordered_events()
    {
        var commit = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.Committed, 4, null, []);
        Assert.Equal(4, commit.Revision);
        Assert.Null(commit.CurrentStatus);
        Assert.Empty(commit.Errors);

        var stale = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StaleRevision, 9, null, []);
        Assert.Equal(ExperienceStoreOutcome.StaleRevision, stale.Outcome);

        // A prior-status mismatch reports the status the record is actually in, to re-decide against.
        var mismatch = new ExperienceLifecycleCommitResult(ExperienceStoreOutcome.StatusMismatch, 9, ExperienceStatus.Quarantined, []);
        Assert.Equal(ExperienceStatus.Quarantined, mismatch.CurrentStatus);
        Assert.Equal(9, mismatch.Revision);

        var invalid = new ExperienceLifecycleCommitResult(
            ExperienceStoreOutcome.Invalid, 0, null, [new StoreValidationError("Reason", "must not be empty or whitespace.")]);
        Assert.Equal("Reason", Assert.Single(invalid.Errors).Path);

        // A commit result never carries record or event payload back to the caller.
        Assert.DoesNotContain(
            typeof(ExperienceLifecycleCommitResult).GetProperties(),
            p => p.PropertyType == typeof(ExperienceRecord) || p.PropertyType == typeof(LifecycleEvent));

        var first = new LifecycleEvent(Guid.NewGuid(), Guid.NewGuid(), null, ExperienceStatus.Candidate, "captured", "capture", Now, 0);
        var second = first with { EventId = Guid.NewGuid(), PriorStatus = ExperienceStatus.Candidate, CurrentStatus = ExperienceStatus.Validated, ExpectedRevision = 1 };
        var history = new ExperienceRecordHistoryResult(
            ExperienceStoreOutcome.Found,
            2,
            [new StoredLifecycleEvent(first, Now, 1), new StoredLifecycleEvent(second, Now, 2)],
            [],
            NextStartAfterRevision: 2);

        Assert.Equal(2, history.Revision);
        Assert.Equal([0L, 1L], history.Events.Select(e => e.Event.ExpectedRevision));
        Assert.Equal([1L, 2L], history.Events.Select(e => e.AppliedRevision));
        Assert.Equal(ExperienceStatus.Validated, history.Events[^1].Event.CurrentStatus);

        // The store's own facts live on the projection, never on the event Core stamped.
        Assert.DoesNotContain(typeof(LifecycleEvent).GetProperties(), p => p.Name is "RecordedAt" or "AppliedRevision");
        Assert.Equal(2, history.NextStartAfterRevision);

        // A page that returned nothing carries no cursor, which is how paging ends.
        var notFound = new ExperienceRecordHistoryResult(ExperienceStoreOutcome.NotFound, 0, [], []);
        Assert.Empty(notFound.Events);
        Assert.Null(notFound.NextStartAfterRevision);
    }

    [Fact]
    public void LifecycleEvent_equality_is_field_by_field_so_a_replay_can_be_told_from_a_conflict()
    {
        // The store's replay check compares the resubmitted event against the stored one by value.
        var original = new LifecycleEvent(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ExperienceStatus.Candidate,
            ExperienceStatus.Validated,
            "verified evidence",
            "finalization",
            Now,
            3);

        Assert.Equal(original, original with { });
        Assert.NotEqual(original, original with { Reason = "verified evidence " });
        Assert.NotEqual(original, original with { Producer = "Finalization" });
        Assert.NotEqual(original, original with { PriorStatus = null });
        Assert.NotEqual(original, original with { CurrentStatus = ExperienceStatus.Quarantined });
        Assert.NotEqual(original, original with { OccurredAt = Now.AddTicks(10) });
        Assert.NotEqual(original, original with { ExpectedRevision = 4 });
    }

    [Fact]
    public void Query_defaults_to_all_statuses_and_a_limit_of_50_within_1_to_500()
    {
        var query = new ExperienceRecordQuery(new Scope("t", "a", "p"));

        Assert.Null(query.Statuses);
        Assert.Equal(50, query.Limit);
        Assert.Equal(50, ExperienceRecordQuery.DefaultLimit);
        Assert.Equal(1, ExperienceRecordQuery.MinLimit);
        Assert.Equal(500, ExperienceRecordQuery.MaxLimit);
    }

    [Fact]
    public void Eligibility_for_reuse_is_stated_once_and_matches_the_status_doc()
    {
        // Retrieval, indexing, the lifecycle service's de-index decision and the storage adapter's
        // in-transaction supersession gate all read this one list. Duplicating it is how the four drift.
        Assert.Equal(
            [ExperienceStatus.Validated, ExperienceStatus.Reinforced],
            ExperienceStatuses.EligibleForReuse);

        foreach (var status in Enum.GetValues<ExperienceStatus>())
        {
            Assert.Equal(ExperienceStatuses.EligibleForReuse.Contains(status), ExperienceStatuses.IsEligibleForReuse(status));
        }
    }

    [Fact]
    public void Store_outcomes_results_and_exception_have_the_expected_shape()
    {
        Assert.Equal(
            [
                "Created", "Found", "NotFound", "Denied", "Invalid", "Conflict", "Committed", "StaleRevision",
                "StatusMismatch", "ReplacementNotAllowed", "Deleted",
            ],
            Enum.GetNames<ExperienceStoreOutcome>());

        var error = new StoreValidationError("Scope.TenantId", "must not be empty or whitespace.");
        Assert.Equal("Scope.TenantId", error.Path);

        var get = new ExperienceRecordGetResult(ExperienceStoreOutcome.NotFound, null, []);
        Assert.Null(get.Record);
        var query = new ExperienceRecordQueryResult(ExperienceStoreOutcome.Invalid, [], [error]);
        Assert.Single(query.Errors);
        var create = new ExperienceRecordCreateResult(ExperienceStoreOutcome.Conflict, []);
        Assert.DoesNotContain(create.GetType().GetProperties(), p => p.PropertyType == typeof(ExperienceRecord));

        var inner = new InvalidOperationException("driver");
        var exception = new ExperienceStoreException("storage failed", inner);
        Assert.Same(inner, exception.InnerException);
        Assert.IsAssignableFrom<Exception>(new ExperienceStoreException("unsupported payload version"));
    }

    [Fact]
    public void AuthorizationContext_bounds_default_to_null_so_existing_call_sites_compile()
    {
        var authorization = new AuthorizationContext("tenant-1", "principal", [], Now);

        Assert.Null(authorization.ApplicationId);
        Assert.Null(authorization.ProjectId);
        Assert.Null(authorization.TeamId);
        Assert.Null(authorization.AgentId);
        Assert.Null(authorization.UserId);
    }

    [Fact]
    public void Unbounded_context_permits_any_scope_in_its_tenant()
    {
        var authorization = new AuthorizationContext("tenant-1", "principal", [], Now);

        Assert.True(authorization.Permits(new Scope("tenant-1", "app", "project")));
        Assert.True(authorization.Permits(new Scope("tenant-1", "other-app", "other-project", "team", "agent", "user")));
    }

    [Theory]
    [InlineData("tenant-2")]
    [InlineData("Tenant-1")]
    [InlineData("tenant-1 ")]
    public void Tenant_must_match_exactly(string requestTenant)
    {
        var authorization = new AuthorizationContext("tenant-1", "principal", [], Now);

        Assert.False(authorization.Permits(new Scope(requestTenant, "app", "project")));
    }

    [Fact]
    public void Blank_authorized_tenant_permits_nothing()
    {
        Assert.False(new AuthorizationContext("", "principal", [], Now).Permits(new Scope("", "app", "project")));
        Assert.False(new AuthorizationContext(" ", "principal", [], Now).Permits(new Scope(" ", "app", "project")));
    }

    [Fact]
    public void Each_non_null_bound_must_equal_the_scope_field_exactly()
    {
        var scope = new Scope("tenant-1", "app", "project", "team", "agent", "user");
        var baseline = new AuthorizationContext("tenant-1", "principal", [], Now);

        Assert.True((baseline with { ApplicationId = "app", ProjectId = "project", TeamId = "team", AgentId = "agent", UserId = "user" }).Permits(scope));

        Assert.False((baseline with { ApplicationId = "other" }).Permits(scope));
        Assert.False((baseline with { ProjectId = "Project" }).Permits(scope));
        Assert.False((baseline with { TeamId = "other" }).Permits(scope));
        Assert.False((baseline with { AgentId = "other" }).Permits(scope));
        Assert.False((baseline with { UserId = "other" }).Permits(scope));
    }

    [Fact]
    public void Non_null_bound_does_not_permit_a_null_scope_field()
    {
        var authorization = new AuthorizationContext("tenant-1", "principal", [], Now, TeamId: "team");

        Assert.False(authorization.Permits(new Scope("tenant-1", "app", "project", TeamId: null)));
    }

    [Fact]
    public void Permits_rejects_a_null_scope()
    {
        var authorization = new AuthorizationContext("tenant-1", "principal", [], Now);

        Assert.Throws<ArgumentNullException>(() => authorization.Permits(null!));
    }

    // ---- Story 3.1: sharing grants ---------------------------------------------------------------

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("team-b", "agent-b", "user-b")]
    [InlineData("team-b", null, null)]
    public void The_grant_boundary_ignores_exactly_the_fields_a_grant_may_relax(string? team, string? agent, string? user)
    {
        var owner = new Scope("tenant-1", "app-1", "project-1", "team-a", "agent-a", "user-a");

        Assert.True(owner.SharesGrantBoundary(new Scope("tenant-1", "app-1", "project-1", team, agent, user)));
    }

    [Theory]
    [InlineData("tenant-2", "app-1", "project-1")]
    [InlineData("tenant-1", "app-2", "project-1")]
    [InlineData("tenant-1", "app-1", "project-2")]
    [InlineData("Tenant-1", "app-1", "project-1")]
    public void The_grant_boundary_is_never_crossed_by_a_differing_required_field(string tenant, string application, string project)
    {
        var owner = new Scope("tenant-1", "app-1", "project-1", "team-a");

        Assert.False(owner.SharesGrantBoundary(new Scope(tenant, application, project, "team-a")));
        Assert.Throws<ArgumentNullException>(() => owner.SharesGrantBoundary(null!));
    }

    [Fact]
    public void Administrator_authority_is_a_separate_required_input_on_every_grant_mutating_call()
    {
        // The shape is the guarantee: authority to administer sharing cannot be inferred from an
        // AuthorizationContext, from its roles, or from a scope, because every mutating call demands a
        // GrantAdministration of its own alongside the authorization it already takes.
        foreach (var name in new[] { nameof(IExperienceGrantStore.CreateAsync), nameof(IExperienceGrantStore.RevokeAsync) })
        {
            var parameters = typeof(IExperienceGrantStore).GetMethod(name)!.GetParameters();
            Assert.Equal(typeof(AuthorizationContext), parameters[0].ParameterType);
            Assert.Equal(typeof(GrantAdministration), parameters[1].ParameterType);
        }

        // Listing is a read of the owner's own administration, so it takes no administrator authority.
        var list = typeof(IExperienceGrantStore).GetMethod(nameof(IExperienceGrantStore.ListAsync))!.GetParameters();
        Assert.DoesNotContain(list, parameter => parameter.ParameterType == typeof(GrantAdministration));

        // And a grant carries who issued it, so an audit can answer "who allowed this, and until when".
        var grant = new ExperienceGrant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new Scope("tenant-1", "app-1", "project-1", "team-a"),
            new Scope("tenant-1", "app-1", "project-1", "team-b"),
            "the sibling team owns the follow-up",
            "administrator-1",
            Now,
            Now.AddDays(7),
            RevokedAt: null,
            RevocationReason: null);

        Assert.True(grant.RecordScope.SharesGrantBoundary(grant.RecipientScope));
        Assert.Equal("administrator-1", grant.AdministratorPrincipalId);
    }

    [Fact]
    public void Grant_auditing_cannot_be_constructed_or_copied_into_a_state_that_audits_nothing()
    {
        var log = new NullAccessLog();
        Action<ExperienceGrantAccessFailure> report = _ => { };

        // The whole point of the policy object is that a host cannot end up with a mode it did not
        // choose, a missing ledger, or -- under best effort, where the read still returns -- nowhere for
        // a missing row to be reported.
        Assert.Throws<ArgumentNullException>(() => new ExperienceGrantAuditing(null!, report));
        Assert.Throws<ArgumentNullException>(() => new ExperienceGrantAuditing(log, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExperienceGrantAuditing(log, report, (ExperienceGrantAuditingMode)99));

        // And a `with` expression re-validates, so nothing can be copied over the guard afterwards.
        var auditing = new ExperienceGrantAuditing(log, report, ExperienceGrantAuditingMode.Required);
        Assert.Throws<ArgumentNullException>(() => auditing with { Log = null! });
        Assert.Throws<ArgumentNullException>(() => auditing with { OnNotRecorded = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => auditing with { Mode = (ExperienceGrantAuditingMode)99 });

        Assert.Equal(ExperienceGrantAuditingMode.Required, auditing.Mode);
        Assert.Same(TimeProvider.System, auditing.Clock);
        Assert.Same(TimeProvider.System, (auditing with { Clock = null! }).Clock);

        // Auditing is off by default, and that is expressible only by wiring nothing at all.
        Assert.Equal(ExperienceGrantAuditingMode.BestEffort, new ExperienceGrantAuditing(log, report).Mode);
    }

    private sealed class NullAccessLog : IExperienceGrantAccessLog
    {
        public Task RecordAsync(IReadOnlyList<ExperienceGrantAccess> accesses, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ExperienceGrantAccessQueryResult> QueryAsync(
            AuthorizationContext authorization,
            ExperienceGrantAccessQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExperienceGrantAccessQueryResult(ExperienceStoreOutcome.Found, [], []));
    }
}
