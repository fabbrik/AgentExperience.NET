using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Lifecycle;
using AgentExperience.Core.Reflections;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;
using AgentExperience.Storage.InMemory;

namespace AgentExperience.Contract.Tests;

/// <summary>A required check, as a finalize case declares it.</summary>
internal sealed record RequiredCheckInput(string CheckId, string ExpectedKind);

/// <summary>What the host's verification supplied for the run.</summary>
internal sealed record VerificationInput(
    ClosedVerificationRound? ClosedRound,
    IReadOnlyList<RequiredCheckInput> RequiredChecks,
    IReadOnlyList<Evidence> Evidence,
    string CurrentArtifactRevision,
    DateTimeOffset FinalizedAt);

/// <summary>
/// The options that change what a finalize case stores. <see cref="AllowedArgumentNames"/> <see langword="null"/> is the
/// default sanitization (no argument kept but by allowlist: none; the default secret names redacted); a list is the
/// default policy with exactly those argument names allowlisted.
/// </summary>
internal sealed record FinalizeOptions(IReadOnlyList<string>? AllowedArgumentNames);

/// <summary>
/// <c>input.json</c> of a finalize case: the run as its host observed it (attempts and tool calls before sanitization),
/// the event ID its completion was recorded under, the verification, and the options that matter.
/// </summary>
internal sealed record FinalizeInput(
    string Description,
    FinalizeOptions Options,
    ExperienceRun Run,
    Guid CompletionEventId,
    VerificationInput Verification);

/// <summary>The finalize family: its cases, and how one runs through the library.</summary>
internal static class FinalizeContract
{
    public const string Family = "finalize";

    private const string Revision = "rev-1";

    private static readonly ClosedVerificationRound Round = new(Fixed.Id(0x7e, 1), Revision);

    /// <summary>Every finalize case, by directory name.</summary>
    public static IReadOnlyList<(string Name, FinalizeInput Input)> Cases { get; } =
    [
        ("verified-after-one-failed-attempt", VerifiedAfterOneFailedAttempt()),
        ("failed-verification-quarantined", FailedVerificationQuarantined()),
        ("classified-tool-failure", ClassifiedToolFailure()),
        ("allowlisted-argument-values", AllowlistedArgumentValues()),
        ("sanitization-redacts-secret-field", SanitizationRedactsSecretField()),
        ("non-ascii-and-overlong-text", NonAsciiAndOverlongText()),
    ];

    /// <summary>
    /// Runs one case through the library: capture (which sanitizes), then finalization with the deterministic reflector,
    /// committed through the in-memory store. Returns the stored record and its initial lifecycle event, both as the
    /// contract writes them.
    /// </summary>
    public static async Task<(JsonObject Record, JsonObject Event)> RunAsync(FinalizeInput input)
    {
        var sanitization = input.Options.AllowedArgumentNames is { } names
            ? AgentExperienceDefaults.SanitizationAllowing([.. names])
            : AgentExperienceDefaults.Sanitization;
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(sanitization), AgentExperienceDefaults.CaptureLimits);

        var run = input.Run;
        var started = capture.StartRun(run.RunId, run.TaskId, run.TaskDescription, run.Scope, run.Environment, run.Provenance, run.StartedAt);
        Assert.Equal(StartRunOutcome.Started, started.Outcome);

        foreach (var attempt in run.Attempts.OrderBy(attempt => attempt.SequenceNumber))
        {
            var appended = await capture.AppendAttemptAsync(
                run.RunId,
                new AppendAttemptRequest(
                    attempt.AttemptId,
                    attempt.StartedAt,
                    attempt.Duration,
                    [.. attempt.ToolCalls.OrderBy(call => call.SequenceNumber).Select(call => new RawToolCall(
                        call.ToolCallId, call.ToolName, call.Arguments, call.StartedAt, call.Duration, call.Result, call.Error))],
                    attempt.Result,
                    attempt.Error));
            Assert.True(appended.Outcome == AppendAttemptOutcome.Recorded, $"Attempt {attempt.SequenceNumber} was not appended: {appended.Outcome}.");
        }

        var completed = await capture.CompleteRunAsync(
            run.RunId,
            input.CompletionEventId,
            run.ExecutionStatus ?? throw new InvalidOperationException("A finalize case's run must have finished."),
            run.EndedAt ?? throw new InvalidOperationException("A finalize case's run must have an end."));
        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);

        var store = new InMemoryExperienceRecordStore(new FixedTimeProvider(input.Verification.FinalizedAt));
        var finalization = new ExperienceFinalizationService(capture, new DefaultExperienceReflector(), store, new ExperienceLifecycleService(store));
        var verification = input.Verification;
        var result = await finalization.FinalizeAsync(new FinalizeExperienceRequest(
            run.RunId,
            Fixed.Authorization,
            verification.ClosedRound,
            [.. verification.RequiredChecks.Select(check => new RequiredCheck(check.CheckId, check.ExpectedKind))],
            verification.Evidence,
            verification.CurrentArtifactRevision,
            StorageDecision.Permit,
            verification.FinalizedAt));
        Assert.True(result.IsDurable, $"The case did not finalize durably: {result.Outcome} at {result.Stage}: {result.Reason}");

        var read = await store.GetAsync(Fixed.Authorization, run.Scope, result.ExperienceId!.Value, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        var record = read.Record!;
        var initial = result.Event ?? throw new InvalidOperationException("A durable finalization committed no initial event.");

        var lifecycleEvent = new JsonObject
        {
            ["event_id"] = initial.EventId.ToString("D", CultureInfo.InvariantCulture),
            ["experience_id"] = initial.ExperienceRecordId.ToString("D", CultureInfo.InvariantCulture),
            ["prior_status"] = initial.PriorStatus?.ToString(),
            ["current_status"] = initial.CurrentStatus.ToString(),
            ["reason"] = initial.Reason,
            ["producer"] = initial.Producer,
            ["occurred_at"] = ContractJson.Timestamp(initial.OccurredAt),
            ["expected_revision"] = initial.ExpectedRevision,
            ["applied_revision"] = result.Revision,
        };

        return (ContractJson.ToStored(record), lifecycleEvent);
    }

    // ---- Cases -------------------------------------------------------------------------------------------------------

    private static FinalizeInput VerifiedAfterOneFailedAttempt()
    {
        var run = Fixed.Id(0xf1, 0);
        var t = Fixed.T0;
        return new FinalizeInput(
            "A refund that failed once with a thrown error and then worked, verified by one passing check. Default sanitization: " +
            "no argument is allowlisted, so the stored calls keep no argument values.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "refund-stuck-pending", "Refund ticket 42 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_refund_check", t.AddSeconds(1), new() { ["strategy"] = "retry-immediately" },
                        result: null, error: "System.InvalidOperationException: the refund lock is held by another worker"),
                ],
                error: "System.InvalidOperationException: the refund lock is held by another worker"),
                Attempt(run, 1, t.AddSeconds(5),
                [
                    Call(run, 1, 0, "run_refund_check", t.AddSeconds(5), new() { ["strategy"] = "wait-for-lock" }, result: "refund issued"),
                ],
                result: "Refund issued."),
            ]),
            Fixed.Id(0xf1, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput FailedVerificationQuarantined()
    {
        var run = Fixed.Id(0xf2, 0);
        var t = Fixed.T0.AddHours(1);
        return new FinalizeInput(
            "A run whose one attempt completed but whose required check failed: stored Quarantined, with no reflection, no " +
            "confidence and no supporting validation.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "refund-stuck-pending", "Refund ticket 43 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_refund_check", t.AddSeconds(1), new() { ["strategy"] = "skip-lock" }, result: "refund queued"),
                ],
                result: "Done."),
            ]),
            Fixed.Id(0xf2, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Fail)));
    }

    private static FinalizeInput ClassifiedToolFailure()
    {
        var run = Fixed.Id(0xf3, 0);
        var t = Fixed.T0.AddHours(2);
        return new FinalizeInput(
            "A tool that reported failure in its result (exit code 3) and that the host classified as failed: the call keeps the " +
            "result the model saw and carries the class as its error. The attempt itself ended without an error.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "deploy-service", "Deploy service X to prod", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_deploy", t.AddSeconds(1), new() { ["mode"] = "rolling" }, result: "exit=3", error: "exit 3"),
                    Call(run, 0, 1, "run_deploy", t.AddSeconds(3), new() { ["mode"] = "blue-green" }, result: "exit=0"),
                ],
                result: "Deployed on the second try."),
            ]),
            Fixed.Id(0xf3, 0xe0),
            Verified(run, t.AddSeconds(30), ("service-healthy", "smoke-test", CheckResult.Pass)));
    }

    private static FinalizeInput AllowlistedArgumentValues()
    {
        var run = Fixed.Id(0xf4, 0);
        var t = Fixed.T0.AddHours(3);
        return new FinalizeInput(
            "Argument values the host allowlisted for capture (strategy, ticket) are stored as given; an argument it did not " +
            "allowlist (note) is omitted. Stored values never reach the lesson text: only a block whose reader allowlisted the " +
            "key for the tool shows them.",
            new FinalizeOptions(AllowedArgumentNames: ["strategy", "ticket"]),
            Run(run, "refund-stuck-pending", "Refund ticket 44 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_refund_check", t.AddSeconds(1),
                        new() { ["strategy"] = "wait-for-lock", ["ticket"] = 44, ["note"] = "customer called twice" },
                        result: "refund issued"),
                ],
                result: "Refund issued."),
            ]),
            Fixed.Id(0xf4, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput SanitizationRedactsSecretField()
    {
        var run = Fixed.Id(0xf5, 0);
        var t = Fixed.T0.AddHours(4);
        return new FinalizeInput(
            "A secret-named argument (apiKey, and authorization inside an allowlisted object) is redacted to the empty string " +
            "even though it is not allowlisted; secret names match ignoring case. Unlisted fields are omitted.",
            new FinalizeOptions(AllowedArgumentNames: ["query", "headers", "accept"]),
            Run(run, "search-refunds", "Find refunds over 100 EUR this week", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "search_refunds", t.AddSeconds(1),
                        new()
                        {
                            ["query"] = "amount > 100",
                            ["ApiKey"] = "sk-live-0123456789",
                            ["headers"] = new Dictionary<string, object?>
                            {
                                ["authorization"] = "Bearer abc.def",
                                ["accept"] = "application/json",
                                ["x-trace"] = "t-1",
                            },
                            ["page"] = 2,
                        },
                        result: "3 rows"),
                ],
                result: "3 refunds found."),
            ]),
            Fixed.Id(0xf5, 0xe0),
            Verified(run, t.AddSeconds(30), ("results-returned", "assertion", CheckResult.Pass)));
    }

    private static FinalizeInput NonAsciiAndOverlongText()
    {
        var run = Fixed.Id(0xf6, 0);
        var t = Fixed.T0.AddHours(5);

        // 4,100 characters: over capture's 4,000-character result limit, so it is stored as the placeholder.
        var overlongResult = string.Concat(Enumerable.Repeat("Zeile mit Umlauten äöü. ", 171))[..4_100];

        // Over the reflector's 500-character quote: the cut falls exactly on a surrogate pair, which it never splits.
        const string head = "TimeoutException: Zeitüberschreitung beim Warten auf die Sperre — 等待锁超时 ";
        var error = head + new string('x', 498 - head.Length) + "🧾 und danach weiterer Text, der nicht mehr zitiert wird.";

        return new FinalizeInput(
            "Non-ASCII text (umlauts, CJK, an emoji outside the BMP) is stored as given. A tool result over 4,000 UTF-16 code " +
            "units is replaced by capture's placeholder; an error over 500 code units is quoted cut to 500 with an ellipsis, " +
            "never splitting a surrogate pair. Every length in the contract is counted in UTF-16 code units.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "rückerstattung", "Rückerstattung für Ticket 45 hängt — 退款卡住 🧾", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "export_ledger", t.AddSeconds(1), new(), result: overlongResult),
                    Call(run, 0, 1, "run_refund_check", t.AddSeconds(2), new(), result: null, error: error),
                ],
                error: error),
                Attempt(run, 1, t.AddSeconds(9),
                [
                    Call(run, 1, 0, "run_refund_check", t.AddSeconds(9), new(), result: "ok"),
                ],
                result: "Erstattet. 已退款。"),
            ]),
            Fixed.Id(0xf6, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Pass)));
    }

    // ---- Builders ----------------------------------------------------------------------------------------------------

    private static ExperienceRun Run(Guid runId, string taskId, string? description, DateTimeOffset startedAt, IReadOnlyList<Attempt> attempts) => new(
        runId,
        taskId,
        description,
        Fixed.Scope,
        Fixed.Environment,
        Fixed.Provenance(startedAt),
        attempts,
        RunExecutionStatus.Completed,
        Outcome: null,
        startedAt,
        startedAt.AddSeconds(20));

    private static Attempt Attempt(Guid runId, int sequence, DateTimeOffset startedAt, IReadOnlyList<ToolCallRecord> calls, string? result = null, string? error = null) => new(
        SubId(runId, 0xa0 + sequence),
        sequence,
        startedAt,
        TimeSpan.FromSeconds(3.5),
        calls,
        result,
        error);

    private static ToolCallRecord Call(
        Guid runId,
        int attempt,
        int sequence,
        string toolName,
        DateTimeOffset startedAt,
        Dictionary<string, object?> arguments,
        string? result,
        string? error = null) => new(
            SubId(runId, 0xc00 + (attempt * 0x10) + sequence),
            sequence,
            toolName,
            arguments,
            startedAt,
            TimeSpan.FromMilliseconds(1250),
            result,
            error);

    private static VerificationInput Verified(Guid runId, DateTimeOffset finalizedAt, params (string CheckId, string Kind, CheckResult Result)[] checks) => new(
        Round,
        [.. checks.Select(check => new RequiredCheckInput(check.CheckId, check.Kind))],
        [.. checks.Select((check, index) => new Evidence(
            SubId(runId, 0xe100 + index),
            Round.RoundId,
            Revision,
            check.CheckId,
            check.Kind,
            check.Result,
            "contract-ci",
            check.Result == CheckResult.Pass ? "all assertions passed" : "expected status refunded, was pending",
            finalizedAt.AddSeconds(-5)))],
        Revision,
        finalizedAt);

    /// <summary>An ID derived from a case's run ID, so every ID in a case reads as belonging to it.</summary>
    private static Guid SubId(Guid runId, int n)
    {
        var text = runId.ToString("D", CultureInfo.InvariantCulture);
        return Guid.Parse(text[..24] + n.ToString("x12", CultureInfo.InvariantCulture));
    }
}

/// <summary>A clock that always reads one instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
