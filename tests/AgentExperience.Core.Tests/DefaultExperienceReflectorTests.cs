using System.Globalization;
using System.Text.Json;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Shared, fully deterministic fixtures (fixed identifiers and timestamps) for the reflector tests:
/// runs are built directly, and evaluations are produced by the real
/// <see cref="VerificationAggregator"/> so reflections are exercised against genuine evaluation shapes.
/// </summary>
internal static class ReflectionFixtures
{
    public static readonly DateTimeOffset BaseTime = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid RunId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid ReflectionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly ClosedVerificationRound Round = new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "rev-1");

    public static EnvironmentFingerprint Environment(string? applicationVersion = "2.4.0", IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            HostName: "worker-01",
            RuntimeVersion: "10.0.0",
            OperatingSystem: "linux-x64",
            ApplicationVersion: applicationVersion,
            Metadata: metadata ?? new Dictionary<string, string> { ["region"] = "us-east" });

    public static Attempt Attempt(int sequenceNumber, string? result, string? error, params ToolCallRecord[] toolCalls) =>
        new(
            AttemptId: new Guid(sequenceNumber + 100, 0, 0, new byte[8]),
            SequenceNumber: sequenceNumber,
            StartedAt: BaseTime.AddSeconds(sequenceNumber),
            Duration: TimeSpan.FromSeconds(1),
            ToolCalls: toolCalls,
            Result: result,
            Error: error);

    public static ToolCallRecord ToolCall(int sequenceNumber, string toolName, string? error = null) =>
        new(
            ToolCallId: new Guid(sequenceNumber + 200, 0, 0, new byte[8]),
            SequenceNumber: sequenceNumber,
            ToolName: toolName,
            Arguments: new Dictionary<string, object?>(),
            StartedAt: BaseTime,
            Duration: TimeSpan.FromMilliseconds(10),
            Result: error is null ? "ok" : null,
            Error: error);

    public static ExperienceRun Run(
        IReadOnlyList<Attempt> attempts,
        EnvironmentFingerprint? environment = null,
        RunExecutionStatus? executionStatus = RunExecutionStatus.Completed,
        Outcome? outcome = null) =>
        new(
            RunId: RunId,
            TaskId: "fix-build",
            TaskDescription: "Fix the failing build",
            Scope: new Scope("tenant-1", "app-1", "project-1"),
            Environment: environment ?? Environment(),
            Provenance: new Provenance("tests", "1.0.0", BaseTime, null),
            Attempts: attempts,
            ExecutionStatus: executionStatus,
            Outcome: outcome,
            StartedAt: BaseTime,
            EndedAt: executionStatus is null ? null : BaseTime.AddMinutes(1));

    /// <summary>A required check satisfied by <c>TestResult</c> evidence (what <see cref="Evidence"/> produces) unless another kind is named.</summary>
    public static RequiredCheck Check(string checkId, string expectedKind = "TestResult") => new(checkId, expectedKind);

    public static Evidence Evidence(int index, string checkId, CheckResult result) =>
        new(
            EvidenceId: Guid.Parse($"aaaaaaaa-0000-0000-0000-{index:D12}"),
            VerificationRoundId: Round.RoundId,
            ArtifactRevision: Round.ArtifactRevision,
            CheckId: checkId,
            Kind: "TestResult",
            Result: result,
            Producer: "evaluator",
            Detail: null,
            CapturedAt: BaseTime);

    public static VerificationResult Verified() =>
        VerificationAggregator.Aggregate(
            RunId, [Evidence(1, "build", CheckResult.Pass), Evidence(2, "tests", CheckResult.Pass)],
            [Check("build"), Check("tests")], Round, Round.ArtifactRevision, BaseTime);

    public static VerificationResult Failed() =>
        VerificationAggregator.Aggregate(
            RunId, [Evidence(1, "build", CheckResult.Fail), Evidence(2, "tests", CheckResult.Pass)],
            [Check("build"), Check("tests")], Round, Round.ArtifactRevision, BaseTime);

    /// <summary>"build" passes, "tests" has no evidence: Unknown with completion score 0.5.</summary>
    public static VerificationResult Unknown() =>
        VerificationAggregator.Aggregate(
            RunId, [Evidence(1, "build", CheckResult.Pass)],
            [Check("build"), Check("tests")], Round, Round.ArtifactRevision, BaseTime);

    /// <summary>Attempt 0 errors, attempt 1 completes.</summary>
    public static IReadOnlyList<Attempt> RepairAttempts() =>
    [
        Attempt(0, null, "CS1002: ; expected", ToolCall(0, "dotnet-build", "exit code 1")),
        Attempt(1, "build and tests green", null, ToolCall(0, "edit-file"), ToolCall(1, "dotnet-build")),
    ];

    public static ReflectionRequest Request(ExperienceRun run, VerificationResult evaluation) =>
        new(run, evaluation, ReflectionId, BaseTime.AddMinutes(5));

    /// <summary>Aggregates <paramref name="evidence"/> for <see cref="RunId"/> in <see cref="Round"/>.</summary>
    public static VerificationResult Evaluate(IReadOnlyList<Evidence> evidence, params RequiredCheck[] checks) =>
        VerificationAggregator.Aggregate(RunId, evidence, checks, Round, Round.ArtifactRevision, BaseTime);

    public static string ToJson(Reflection reflection) => JsonSerializer.Serialize(reflection);
}

/// <summary>
/// Exercises <see cref="DefaultExperienceReflector"/> against every row of Story 1.3's I/O matrix,
/// plus determinism (including under a non-invariant current culture), evidence-ID traceability in
/// the lesson, exact evaluation-basis copying, and cancellation.
/// </summary>
public class DefaultExperienceReflectorTests
{
    private readonly DefaultExperienceReflector _reflector = new();

    [Fact]
    public async Task Verified_with_repair_classifies_the_error_attempt_as_failed_and_the_final_attempt_as_successful()
    {
        var evaluation = ReflectionFixtures.Verified();
        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        var failed = Assert.Single(reflection.FailedApproaches);
        Assert.StartsWith("Attempt 0", failed);
        Assert.Contains("\"CS1002: ; expected\"", failed);
        Assert.Contains("dotnet-build (error: \"exit code 1\")", failed);

        var successful = Assert.Single(reflection.SuccessfulApproaches);
        Assert.StartsWith("Attempt 1 using tools [edit-file, dotnet-build]", successful);

        Assert.Contains("build", reflection.Lesson);
        Assert.Contains("tests", reflection.Lesson);
        foreach (var evidence in evaluation.Outcome.Evidence)
        {
            Assert.Contains(evidence.EvidenceId.ToString("D"), reflection.Lesson);
        }

        // AC1: every structured section is populated.
        Assert.False(string.IsNullOrWhiteSpace(reflection.Lesson));
        Assert.NotEmpty(reflection.Preconditions);
        Assert.NotEmpty(reflection.Warnings);
        Assert.False(string.IsNullOrWhiteSpace(reflection.ReuseGuidance));
        Assert.DoesNotContain(reflection.Warnings, w => w.Contains("not a validated procedure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verified_lesson_matches_the_documented_template()
    {
        var evaluation = ReflectionFixtures.Verified();
        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        Assert.Equal(
            "Task 'fix-build' verified: required checks [build, tests] passed (evidence: aaaaaaaa-0000-0000-0000-000000000001, aaaaaaaa-0000-0000-0000-000000000002).",
            reflection.Lesson);
    }

    [Fact]
    public async Task Failed_puts_error_attempts_and_the_final_attempt_in_failed_approaches_and_names_the_failing_checks()
    {
        var evaluation = ReflectionFixtures.Failed();
        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        Assert.Empty(reflection.SuccessfulApproaches);
        Assert.Equal(2, reflection.FailedApproaches.Count);
        Assert.StartsWith("Attempt 0", reflection.FailedApproaches[0]);
        Assert.StartsWith("Attempt 1", reflection.FailedApproaches[1]);

        Assert.Contains("required checks [build] failed", reflection.Lesson);
        foreach (var evidence in evaluation.Outcome.Evidence)
        {
            Assert.Contains(evidence.EvidenceId.ToString("D"), reflection.Lesson);
        }

        Assert.Contains(reflection.Warnings, w => w.StartsWith("Not a validated procedure", StringComparison.Ordinal));
        Assert.StartsWith("Do not reuse as a validated procedure", reflection.ReuseGuidance);
    }

    [Fact]
    public async Task Unknown_recommends_no_successful_approach_and_warns_that_a_partial_score_is_not_verification()
    {
        var evaluation = ReflectionFixtures.Unknown();
        Assert.Equal(0.5, evaluation.CompletionScore);

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        Assert.Empty(reflection.SuccessfulApproaches);
        Assert.Contains(reflection.Warnings, w => w.StartsWith("Not a validated procedure", StringComparison.Ordinal));
        Assert.Contains(reflection.Warnings, w => w.StartsWith("Unverified", StringComparison.Ordinal));
        Assert.Contains(reflection.Warnings, w => w.Contains("0.5") && w.Contains("is not verification"));
        Assert.Contains(reflection.Warnings, w => w.StartsWith("Attempt 1 (final) completed without an error, but verification is Unknown", StringComparison.Ordinal));
        Assert.StartsWith("Do not reuse as a validated procedure", reflection.ReuseGuidance);

        // The failed attempt is still recorded, and the evaluation reason is quoted, never paraphrased.
        Assert.StartsWith("Attempt 0", Assert.Single(reflection.FailedApproaches));
        Assert.Contains("\"" + evaluation.Outcome.Reason + "\"", reflection.Lesson);
    }

    [Fact]
    public async Task A_superseded_non_error_attempt_is_not_classified_and_its_contribution_is_warned_as_unknown()
    {
        var attempts = new[]
        {
            ReflectionFixtures.Attempt(0, "first try done", null),
            ReflectionFixtures.Attempt(1, "second try done", null),
        };

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(attempts), ReflectionFixtures.Verified()));

        Assert.Empty(reflection.FailedApproaches);
        Assert.StartsWith("Attempt 1 with no tool calls", Assert.Single(reflection.SuccessfulApproaches));
        Assert.DoesNotContain(reflection.SuccessfulApproaches, a => a.Contains("Attempt 0"));
        Assert.Contains(reflection.Warnings, w => w.StartsWith("Attempt 0", StringComparison.Ordinal) && w.Contains("contribution to the outcome is unknown"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_uncaptured_application_version_is_listed_as_unknown_and_repeated_in_warnings(string? applicationVersion)
    {
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), ReflectionFixtures.Environment(applicationVersion));

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(run, ReflectionFixtures.Verified()));

        Assert.Contains("Application version: unknown", reflection.Preconditions);
        Assert.Contains("Precondition 'Application version' was not captured and is unknown.", reflection.Warnings);
        Assert.Contains("unknown preconditions", reflection.ReuseGuidance);
    }

    [Fact]
    public async Task A_blank_environment_metadata_value_is_an_unknown_precondition_and_host_name_is_never_a_precondition()
    {
        var metadata = new Dictionary<string, string> { ["zone"] = "b", ["gpu"] = " " };
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), ReflectionFixtures.Environment(metadata: metadata));

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(run, ReflectionFixtures.Verified()));

        Assert.Equal(
            ["Runtime version: 10.0.0", "Operating system: linux-x64", "Application version: 2.4.0", "Environment metadata [gpu]: unknown", "Environment metadata [zone]: b"],
            reflection.Preconditions);
        Assert.Contains("Precondition 'Environment metadata [gpu]' was not captured and is unknown.", reflection.Warnings);
        Assert.DoesNotContain(reflection.Preconditions, p => p.Contains("worker-01"));
    }

    [Fact]
    public async Task No_attempts_yields_empty_approach_lists_and_a_warning()
    {
        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run([]), ReflectionFixtures.Verified()));

        Assert.Empty(reflection.SuccessfulApproaches);
        Assert.Empty(reflection.FailedApproaches);
        Assert.Contains("No attempts were captured.", reflection.Warnings);
    }

    [Fact]
    public async Task A_verified_run_whose_final_attempt_errored_records_no_successful_approach()
    {
        var attempts = new[]
        {
            ReflectionFixtures.Attempt(0, "done", null),
            ReflectionFixtures.Attempt(1, null, "cleanup step failed"),
        };

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(attempts), ReflectionFixtures.Verified()));

        Assert.Empty(reflection.SuccessfulApproaches);
        Assert.StartsWith("Attempt 1", Assert.Single(reflection.FailedApproaches));
        Assert.Contains(reflection.Warnings, w => w.StartsWith("Attempt 1 (final) ended with an error despite a Verified status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attempts_are_read_in_sequence_number_order_regardless_of_list_order()
    {
        var attempts = ReflectionFixtures.RepairAttempts().Reverse().ToArray();

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(attempts), ReflectionFixtures.Verified()));

        Assert.StartsWith("Attempt 0", Assert.Single(reflection.FailedApproaches));
        Assert.StartsWith("Attempt 1", Assert.Single(reflection.SuccessfulApproaches));
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public async Task The_evaluation_basis_identity_and_producer_exactly_match_the_request(string kind)
    {
        var evaluation = kind switch
        {
            "verified" => ReflectionFixtures.Verified(),
            "failed" => ReflectionFixtures.Failed(),
            _ => ReflectionFixtures.Unknown(),
        };
        var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation);

        var reflection = await _reflector.ReflectAsync(request);

        Assert.Equal(request.ReflectionId, reflection.ReflectionId);
        Assert.Equal(request.CreatedAt, reflection.CreatedAt);
        Assert.Equal(request.Run.RunId, reflection.ExperienceRunId);
        Assert.Equal(evaluation.Outcome.Evidence.Select(e => e.EvidenceId), reflection.EvidenceIds);
        Assert.Equal(evaluation.Outcome.Status, reflection.VerificationStatus);
        Assert.Equal(evaluation.CompletionScore, reflection.CompletionScore);
        Assert.Equal(evaluation.RuleVersion, reflection.VerificationRuleVersion);
        Assert.Equal("AgentExperience.DefaultExperienceReflector/1.0.0", reflection.Producer);
        Assert.Equal(DefaultExperienceReflector.ProducerIdentity, reflection.Producer);
    }

    [Fact]
    public async Task Duplicate_evidence_ids_appear_once_in_produced_order()
    {
        var first = ReflectionFixtures.Evidence(7, "tests", CheckResult.Pass);
        var second = ReflectionFixtures.Evidence(3, "build", CheckResult.Pass);
        var evaluation = ReflectionFixtures.Evaluate([first, second, first], ReflectionFixtures.Check("build"), ReflectionFixtures.Check("tests"));
        Assert.Equal([first, second, first], evaluation.Outcome.Evidence);

        var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation);
        var reflection = await _reflector.ReflectAsync(request);

        Assert.Equal([first.EvidenceId, second.EvidenceId], reflection.EvidenceIds);
        Assert.Equal(VerificationAggregator.RuleVersion, reflection.VerificationRuleVersion);

        // Distinct IDs in produced order is what the binding check expects too.
        request.EnsureMatches(reflection);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public async Task The_same_request_always_produces_equal_content_even_under_a_different_current_culture(string kind)
    {
        var evaluation = kind switch
        {
            "verified" => ReflectionFixtures.Verified(),
            "failed" => ReflectionFixtures.Failed(),
            _ => ReflectionFixtures.Unknown(),
        };
        var request = ReflectionFixtures.Request(
            ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), ReflectionFixtures.Environment(null)),
            evaluation);

        var first = ReflectionFixtures.ToJson(await _reflector.ReflectAsync(request));
        var second = ReflectionFixtures.ToJson(await new DefaultExperienceReflector().ReflectAsync(request));
        Assert.Equal(first, second);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var underOtherCulture = ReflectionFixtures.ToJson(await _reflector.ReflectAsync(request));
            Assert.Equal(first, underOtherCulture);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task Failed_lesson_matches_the_documented_template_including_the_quoted_reason()
    {
        var evaluation = ReflectionFixtures.Failed();

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        Assert.Equal(
            "Task 'fix-build' failed verification: required checks [build] failed (evidence: aaaaaaaa-0000-0000-0000-000000000001, aaaaaaaa-0000-0000-0000-000000000002). Evaluation reason: \"Required check(s) resolved to Fail in the host-closed verification round: build; a Fail always dominates a Pass recorded for the same check.\".",
            reflection.Lesson);
    }

    [Fact]
    public async Task Unknown_lesson_matches_the_documented_template()
    {
        var evaluation = ReflectionFixtures.Unknown();

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        Assert.Equal(
            "Task 'fix-build' is unverified: no conclusive verification was reached (completion score 0.5 under rule 1.0.0; evidence: aaaaaaaa-0000-0000-0000-000000000001). Evaluation reason: \"Required check(s) have no conclusive Pass/Fail evidence (missing, errored, or genuinely inconclusive) in the host-closed verification round: tests.\".",
            reflection.Lesson);
    }

    [Fact]
    public async Task Quoted_text_escapes_backslash_double_quote_and_line_breaks_but_keeps_other_characters()
    {
        var attempts = new[] { ReflectionFixtures.Attempt(0, null, "café said \"no\" at C:\\tmp\r\nline 2") };
        // The evaluation reason is the aggregator's, and it names the failing check, so a check ID
        // with quotes in it puts quotes into the reason.
        var evaluation = ReflectionFixtures.Evaluate(
            [ReflectionFixtures.Evidence(1, "say \"hi\"", CheckResult.Fail)],
            ReflectionFixtures.Check("say \"hi\""));

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(attempts), evaluation));

        Assert.Equal("Attempt 0 with no tool calls ended with error: \"café said \\\"no\\\" at C:\\\\tmp\\r\\nline 2\".", Assert.Single(reflection.FailedApproaches));
        Assert.EndsWith("Evaluation reason: \"Required check(s) resolved to Fail in the host-closed verification round: say \\\"hi\\\"; a Fail always dominates a Pass recorded for the same check.\".", reflection.Lesson);
    }

    [Theory]
    [InlineData(RunExecutionStatus.Failed)]
    [InlineData(RunExecutionStatus.Cancelled)]
    public async Task A_run_that_did_not_complete_carries_an_execution_status_warning(RunExecutionStatus executionStatus)
    {
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), executionStatus: executionStatus);

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(run, ReflectionFixtures.Verified()));

        Assert.Contains($"Run execution status is {executionStatus}, not Completed.", reflection.Warnings);
    }

    [Fact]
    public async Task A_completed_run_carries_no_execution_status_warning()
    {
        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), ReflectionFixtures.Verified()));

        Assert.DoesNotContain(reflection.Warnings, w => w.StartsWith("Run execution status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tool_calls_are_described_in_sequence_number_order_regardless_of_list_order()
    {
        var attempts = new[] { ReflectionFixtures.Attempt(0, "done", null, ReflectionFixtures.ToolCall(1, "second"), ReflectionFixtures.ToolCall(0, "first")) };

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(attempts), ReflectionFixtures.Verified()));

        Assert.StartsWith("Attempt 0 using tools [first, second]", Assert.Single(reflection.SuccessfulApproaches));
    }

    [Fact]
    public async Task Unknown_with_a_zero_completion_score_has_no_partial_score_warning()
    {
        var evaluation = VerificationAggregator.Aggregate(ReflectionFixtures.RunId, [], [ReflectionFixtures.Check("build")], null, "rev-1", ReflectionFixtures.BaseTime);
        Assert.Equal(TaskVerificationStatus.Unknown, evaluation.Outcome.Status);
        Assert.Equal(0.0, evaluation.CompletionScore);

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        Assert.DoesNotContain(reflection.Warnings, w => w.Contains("partial score", StringComparison.Ordinal));
        Assert.Contains(reflection.Warnings, w => w.StartsWith("Not a validated procedure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tiny_completion_score_is_rendered_with_round_trip_precision()
    {
        // One pass among 2,500 required checks: a completion score of 0.0004.
        var checks = Enumerable.Range(0, 2500).Select(i => ReflectionFixtures.Check($"check-{i}")).ToArray();
        var evaluation = ReflectionFixtures.Evaluate([ReflectionFixtures.Evidence(1, "check-0", CheckResult.Pass)], checks);
        Assert.Equal(0.0004, evaluation.CompletionScore);

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation));

        Assert.Contains(reflection.Warnings, w => w.StartsWith("Completion score 0.0004 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_blank_but_non_null_attempt_error_counts_as_a_failed_approach()
    {
        var attempts = new[] { ReflectionFixtures.Attempt(0, null, "") };

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(attempts), ReflectionFixtures.Verified()));

        Assert.Empty(reflection.SuccessfulApproaches);
        Assert.Equal("Attempt 0 with no tool calls ended with error: \"\".", Assert.Single(reflection.FailedApproaches));
    }

    [Theory]
    [InlineData("blank-task-id")]
    [InlineData("null-environment")]
    [InlineData("null-metadata")]
    [InlineData("null-attempts")]
    [InlineData("null-attempt-entry")]
    [InlineData("null-tool-calls")]
    [InlineData("null-tool-call-entry")]
    public async Task A_malformed_request_throws_ArgumentException(string defect)
    {
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts());
        var evaluation = ReflectionFixtures.Verified();
        var attempt = ReflectionFixtures.Attempt(0, "done", null);

        (run, evaluation) = defect switch
        {
            "blank-task-id" => (run with { TaskId = "  " }, evaluation),
            "null-environment" => (run with { Environment = null! }, evaluation),
            "null-metadata" => (run with { Environment = run.Environment with { Metadata = null! } }, evaluation),
            "null-attempts" => (run with { Attempts = null! }, evaluation),
            "null-attempt-entry" => (run with { Attempts = [null!] }, evaluation),
            "null-tool-calls" => (run with { Attempts = [attempt with { ToolCalls = null! }] }, evaluation),
            "null-tool-call-entry" => (run with { Attempts = [attempt with { ToolCalls = [null!] }] }, evaluation),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => _reflector.ReflectAsync(ReflectionFixtures.Request(run, evaluation)));
    }

    [Fact]
    public async Task A_null_request_throws_ArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _reflector.ReflectAsync(null!));
    }

    [Fact]
    public void A_request_with_a_null_run_cannot_be_constructed()
    {
        Assert.Throws<ArgumentNullException>(() => ReflectionFixtures.Request(null!, ReflectionFixtures.Verified()));
    }

    [Fact]
    public void A_request_with_a_null_evaluation_cannot_be_constructed()
    {
        Assert.Throws<ArgumentNullException>(() => ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), null!));
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public void KL6_a_request_pairing_a_run_with_another_runs_evaluation_cannot_be_constructed(string kind)
    {
        // The KL-6 pairing: run A's evaluation handed to a reflector together with run B. The
        // evaluation says which run it was computed for, and the request refuses the mismatch before
        // any reflector, default or host, is called.
        var otherRunId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var evidence = kind switch
        {
            "verified" => new[] { ReflectionFixtures.Evidence(1, "build", CheckResult.Pass) },
            "failed" => [ReflectionFixtures.Evidence(1, "build", CheckResult.Fail)],
            _ => [],
        };
        var otherRunsEvaluation = VerificationAggregator.Aggregate(
            otherRunId, evidence, [ReflectionFixtures.Check("build")], ReflectionFixtures.Round, ReflectionFixtures.Round.ArtifactRevision, ReflectionFixtures.BaseTime);
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts());

        var refused = Assert.Throws<ReflectionBindingException>(() => ReflectionFixtures.Request(run, otherRunsEvaluation));

        Assert.IsAssignableFrom<ArgumentException>(refused);
        Assert.Equal(run.RunId, refused.RunId);
        Assert.Equal(nameof(VerificationBasis.RunId), refused.MismatchedField);
        Assert.Equal("Evaluation", refused.ParamName);
        Assert.Contains(otherRunId.ToString("D"), refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other-run")]
    [InlineData("null-run")]
    [InlineData("null-evaluation")]
    public async Task KL6_the_default_reflector_rechecks_the_binding_of_a_request_that_skipped_its_constructor(string defect)
    {
        // Defence in depth: a request materialized without its constructor (reflection, a serializer
        // that writes fields) is re-checked by the default reflector itself.
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts());
        var evaluation = defect == "other-run"
            ? VerificationAggregator.Aggregate(
                Guid.Parse("99999999-9999-9999-9999-999999999999"), [], [ReflectionFixtures.Check("build")], null, "rev-1", ReflectionFixtures.BaseTime)
            : ReflectionFixtures.Verified();

        var forged = (ReflectionRequest)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ReflectionRequest));
        SetBackingField(forged, nameof(ReflectionRequest.Run), defect == "null-run" ? null : run);
        SetBackingField(forged, nameof(ReflectionRequest.Evaluation), defect == "null-evaluation" ? null : evaluation);
        SetBackingField(forged, nameof(ReflectionRequest.ReflectionId), ReflectionFixtures.ReflectionId);

        if (defect == "other-run")
        {
            await Assert.ThrowsAsync<ReflectionBindingException>(() => _reflector.ReflectAsync(forged));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => _reflector.ReflectAsync(forged));
        }
    }

    private static void SetBackingField(ReflectionRequest request, string property, object? value) =>
        typeof(ReflectionRequest)
            .GetField($"<{property}>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(request, value);

    [Fact]
    public void KL6_a_bound_request_cannot_be_re_paired_with_a_with_expression()
    {
        // Only the reflection identity and timestamp can be changed on a copy; the run and its
        // evaluation stay together.
        Assert.Null(typeof(ReflectionRequest).GetProperty(nameof(ReflectionRequest.Run))!.SetMethod);
        Assert.Null(typeof(ReflectionRequest).GetProperty(nameof(ReflectionRequest.Evaluation))!.SetMethod);

        var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), ReflectionFixtures.Verified());
        var copy = request with { ReflectionId = Guid.Parse("44444444-4444-4444-4444-444444444444") };
        Assert.Same(request.Run, copy.Run);
        Assert.Same(request.Evaluation, copy.Evaluation);
    }

    [Fact]
    public async Task EnsureMatches_accepts_what_the_default_reflector_produced()
    {
        foreach (var evaluation in new[] { ReflectionFixtures.Verified(), ReflectionFixtures.Failed(), ReflectionFixtures.Unknown() })
        {
            var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), evaluation);
            request.EnsureMatches(await _reflector.ReflectAsync(request));
        }
    }

    [Theory]
    [InlineData(nameof(Reflection.ReflectionId))]
    [InlineData(nameof(Reflection.ExperienceRunId))]
    [InlineData(nameof(Reflection.CreatedAt))]
    [InlineData(nameof(Reflection.VerificationStatus))]
    [InlineData(nameof(Reflection.CompletionScore))]
    [InlineData(nameof(Reflection.VerificationRuleVersion))]
    [InlineData(nameof(Reflection.EvidenceIds))]
    public async Task KL6_EnsureMatches_refuses_a_reflection_that_does_not_carry_its_request(string field)
    {
        var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), ReflectionFixtures.Verified());
        var reflection = await _reflector.ReflectAsync(request);

        var tampered = field switch
        {
            nameof(Reflection.ReflectionId) => reflection with { ReflectionId = Guid.NewGuid() },
            nameof(Reflection.ExperienceRunId) => reflection with { ExperienceRunId = Guid.NewGuid() },
            nameof(Reflection.CreatedAt) => reflection with { CreatedAt = reflection.CreatedAt.AddSeconds(1) },
            nameof(Reflection.VerificationStatus) => reflection with { VerificationStatus = TaskVerificationStatus.Failed },
            nameof(Reflection.CompletionScore) => reflection with { CompletionScore = 0.5 },
            nameof(Reflection.VerificationRuleVersion) => reflection with { VerificationRuleVersion = "custom-rule" },
            _ => reflection with { EvidenceIds = [.. reflection.EvidenceIds.Reverse()] },
        };

        var refused = Assert.Throws<ReflectionBindingException>(() => request.EnsureMatches(tampered));
        Assert.Equal(field, refused.MismatchedField);
        Assert.Equal("reflection", refused.ParamName);
    }

    [Fact]
    public async Task EnsureMatches_refuses_missing_or_extra_evidence_ids()
    {
        var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), ReflectionFixtures.Verified());
        var reflection = await _reflector.ReflectAsync(request);

        Assert.Throws<ReflectionBindingException>(() => request.EnsureMatches(reflection with { EvidenceIds = reflection.EvidenceIds.Take(1).ToArray() }));
        Assert.Throws<ReflectionBindingException>(() => request.EnsureMatches(reflection with { EvidenceIds = [.. reflection.EvidenceIds, Guid.NewGuid()] }));
        Assert.Throws<ReflectionBindingException>(() => request.EnsureMatches(reflection with { EvidenceIds = null! }));
        Assert.Throws<ArgumentNullException>(() => request.EnsureMatches(null!));
    }

    [Fact]
    public async Task An_in_progress_run_throws_ArgumentException()
    {
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), executionStatus: null);
        await Assert.ThrowsAsync<ArgumentException>(() => _reflector.ReflectAsync(ReflectionFixtures.Request(run, ReflectionFixtures.Verified())));
    }

    [Fact]
    public void A_run_outcome_status_that_disagrees_with_the_evaluation_cannot_be_paired_with_it()
    {
        var runOutcome = new Outcome(TaskVerificationStatus.Failed, [], "failed earlier", ReflectionFixtures.BaseTime);
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), outcome: runOutcome);

        var refused = Assert.Throws<ReflectionBindingException>(() => ReflectionFixtures.Request(run, ReflectionFixtures.Verified()));
        Assert.Equal("Outcome.Status", refused.MismatchedField);
    }

    [Fact]
    public void KL6_a_failed_run_re_stamped_with_a_verified_runs_id_is_refused_by_its_own_outcome()
    {
        // Re-stamping run B with run A's ID passes the run-ID check; B's own recorded outcome still
        // contradicts A's verdict, and the request refuses it. (A run with no outcome of its own has
        // nothing to contradict: that is the documented limit of a run-ID binding.)
        var verifiedEvaluation = ReflectionFixtures.Verified();
        var otherRun = ReflectionFixtures.Run(
            ReflectionFixtures.RepairAttempts(),
            outcome: new Outcome(TaskVerificationStatus.Failed, [], null, ReflectionFixtures.BaseTime)) with { RunId = Guid.NewGuid() };

        var restamped = otherRun with { RunId = verifiedEvaluation.Basis.RunId };

        var refused = Assert.Throws<ReflectionBindingException>(() => ReflectionFixtures.Request(restamped, verifiedEvaluation));
        Assert.Equal("Outcome.Status", refused.MismatchedField);
        Assert.Equal("Evaluation", refused.ParamName);
    }

    [Fact]
    public async Task A_run_outcome_status_that_matches_the_evaluation_is_accepted()
    {
        var evaluation = ReflectionFixtures.Verified();
        var run = ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts(), outcome: evaluation.Outcome);

        var reflection = await _reflector.ReflectAsync(ReflectionFixtures.Request(run, evaluation));

        Assert.Equal(TaskVerificationStatus.Verified, reflection.VerificationStatus);
    }

    [Fact]
    public async Task Duplicate_attempt_sequence_numbers_throw_ArgumentException()
    {
        var attempts = new[] { ReflectionFixtures.Attempt(0, "a", null), ReflectionFixtures.Attempt(0, "b", null) };
        await Assert.ThrowsAsync<ArgumentException>(() => _reflector.ReflectAsync(ReflectionFixtures.Request(ReflectionFixtures.Run(attempts), ReflectionFixtures.Verified())));
    }

    [Fact]
    public async Task A_cancelled_token_throws_before_anything_is_produced()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = ReflectionFixtures.Request(ReflectionFixtures.Run(ReflectionFixtures.RepairAttempts()), ReflectionFixtures.Verified());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reflector.ReflectAsync(request, cts.Token));
    }
}
