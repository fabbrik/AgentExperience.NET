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
            Kind: "TestResult",
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

        // Distinct shapes: Scope's required project identity has no counterpart on
        // AuthorizationContext, and AuthorizationContext's host-established grant has no
        // counterpart on Scope.
        Assert.Contains("ApplicationId", scopeProperties);
        Assert.Contains("ProjectId", scopeProperties);
        Assert.DoesNotContain("ApplicationId", authorizationProperties);
        Assert.DoesNotContain("ProjectId", authorizationProperties);

        Assert.Contains("PrincipalId", authorizationProperties);
        Assert.Contains("Roles", authorizationProperties);
        Assert.DoesNotContain("PrincipalId", scopeProperties);
        Assert.DoesNotContain("Roles", scopeProperties);

        // Both types can be used side by side without one substituting for the other.
        var authorization = new AuthorizationContext("tenant-1", "svc-principal", ["capture:write"], DateTimeOffset.UtcNow);
        var scope = new Scope("tenant-1", "app-1", "project-1");
        Assert.NotEqual<object>(authorization, scope);
    }
}
