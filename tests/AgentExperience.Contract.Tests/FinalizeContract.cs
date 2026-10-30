using System.Globalization;
using System.Text.Json;
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

    internal static readonly ClosedVerificationRound Round = new(Fixed.Id(0x7e, 1), Revision);

    /// <summary>Every finalize case, by directory name.</summary>
    public static IReadOnlyList<(string Name, FinalizeInput Input)> Cases { get; } =
    [
        ("verified-after-one-failed-attempt", VerifiedAfterOneFailedAttempt()),
        ("failed-verification-quarantined", FailedVerificationQuarantined()),
        ("classified-tool-failure", ClassifiedToolFailure()),
        ("allowlisted-argument-values", AllowlistedArgumentValues()),
        ("sanitization-redacts-secret-field", SanitizationRedactsSecretField()),
        ("non-ascii-and-overlong-text", NonAsciiAndOverlongText()),
        ("unknown-no-closed-round", UnknownNoClosedRound()),
        ("unknown-round-for-another-revision", UnknownRoundForAnotherRevision()),
        ("unknown-check-without-evidence-and-any-kind", UnknownCheckWithoutEvidence()),
        ("three-failed-attempts-team-and-user-scope", ThreeFailedAttempts()),
        ("rejected-attempts", RejectedAttempts()),
        ("capture-caps", CaptureCaps()),
        ("exposures-and-correlation-id", ExposuresAndCorrelationId()),

        // Contract 1.1.
        ("argument-numbers", ArgumentNumbers()),
        ("secret-names-ordinal-ignore-case", SecretNamesOrdinalIgnoreCase()),
        ("separator-task-and-check-ids", SeparatorTaskAndCheckIds()),

        // Contract 1.2.
        ("argument-numbers-beyond-double-range", ArgumentNumbersBeyondDoubleRange()),

        // Contract 1.3.
        ("sub-microsecond-instants", SubMicrosecondInstants()),
        ("sub-microsecond-instants-at-a-second-boundary", SubMicrosecondInstantsAtASecondBoundary()),
    ];

    /// <summary>The files of one finalize case.</summary>
    public static IReadOnlyCollection<string> Files { get; } = ["input.json", "expected-capture.json", "expected-record.json", "expected-event.json"];

    /// <summary>
    /// Runs one case through the library: capture (which sanitizes), then finalization with the deterministic reflector,
    /// committed through the in-memory store. Returns the stored record and its initial lifecycle event, both as the
    /// contract writes them.
    /// </summary>
    public static async Task<(JsonObject Record, JsonObject Event)> RunAsync(FinalizeInput input) =>
        await RunWithCaptureAsync(input) is var (record, lifecycleEvent, _) ? (record, lifecycleEvent) : default;

    /// <summary>
    /// As <see cref="RunAsync"/>, also returning <c>expected-capture.json</c>: what capture answered for each attempt,
    /// in input order, and for the run's exposures. An attempt capture refuses is not stored, and the attempts it keeps
    /// are numbered from 0 in the order they were recorded.
    /// </summary>
    public static async Task<(JsonObject Record, JsonObject Event, JsonObject Capture)> RunWithCaptureAsync(FinalizeInput input) =>
        await RunWithReflectorAsync(input, new DefaultExperienceReflector()) is var (record, lifecycleEvent, capture, _)
            ? (record, lifecycleEvent, capture)
            : default;

    /// <summary>
    /// As <see cref="RunWithCaptureAsync"/>, finalizing with <paramref name="reflector"/> (contract 1.14: the model-backed
    /// reflector over a scripted chat client), also returning the finalization's result.
    /// </summary>
    public static async Task<(JsonObject Record, JsonObject Event, JsonObject Capture, FinalizeExperienceResult Result)> RunWithReflectorAsync(
        FinalizeInput input,
        IExperienceReflector reflector)
    {
        var sanitization = input.Options.AllowedArgumentNames is { } names
            ? AgentExperienceDefaults.SanitizationAllowing([.. names])
            : AgentExperienceDefaults.Sanitization;
        var capture = new InMemoryExperienceCaptureService(new DefaultSanitizer(sanitization), AgentExperienceDefaults.CaptureLimits);

        var run = input.Run;
        // A run starts exposed to nothing; the records it was shown are recorded through capture as they are delivered.
        var started = capture.StartRun(run.RunId, run.TaskId, run.TaskDescription, run.Scope, run.Environment, run.Provenance with { ExposedTo = [] }, run.StartedAt);
        Assert.Equal(StartRunOutcome.Started, started.Outcome);
        var captureOutcomes = new JsonObject();
        if (run.Provenance.ExposedTo.Count > 0)
        {
            captureOutcomes["exposure"] = capture.RecordExposure(run.RunId, run.Provenance.ExposedTo).Outcome.ToString();
        }

        var attemptOutcomes = new JsonArray();

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
            attemptOutcomes.Add(appended.Outcome.ToString());
        }

        captureOutcomes["attempts"] = attemptOutcomes;

        var completed = await capture.CompleteRunAsync(
            run.RunId,
            input.CompletionEventId,
            run.ExecutionStatus ?? throw new InvalidOperationException("A finalize case's run must have finished."),
            run.EndedAt ?? throw new InvalidOperationException("A finalize case's run must have an end."));
        Assert.Equal(CompleteRunOutcome.Recorded, completed.Outcome);

        var store = new InMemoryExperienceRecordStore(new FixedTimeProvider(input.Verification.FinalizedAt));
        var finalization = new ExperienceFinalizationService(capture, reflector, store, new ExperienceLifecycleService(store));
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

        return (ContractJson.ToStored(record), lifecycleEvent, captureOutcomes, result);
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

    private static FinalizeInput UnknownNoClosedRound()
    {
        var run = Fixed.Id(0xf7, 0);
        var t = Fixed.T0.AddHours(6);
        return new FinalizeInput(
            "No verification round was closed: the outcome is Unknown with no evidence examined, so the record is Quarantined. " +
            "With no round, the payload has no closedRoundId but still carries origin Finalized.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "refund-stuck-pending", "Refund ticket 47 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_refund_check", t.AddSeconds(1), new(), result: "refund issued"),
                ],
                result: "Done."),
            ]),
            Fixed.Id(0xf7, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Pass)) with { ClosedRound = null });
    }

    private static FinalizeInput UnknownRoundForAnotherRevision()
    {
        var run = Fixed.Id(0xf8, 0);
        var t = Fixed.T0.AddHours(7);
        return new FinalizeInput(
            "The closed round is for another artifact revision than the current one: stale verification, Unknown, Quarantined. " +
            "The passing evidence is never examined.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "refund-stuck-pending", "Refund ticket 48 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_refund_check", t.AddSeconds(1), new(), result: "refund issued"),
                ],
                result: "Done."),
            ]),
            Fixed.Id(0xf8, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Pass)) with { CurrentArtifactRevision = "rev-2" });
    }

    private static FinalizeInput UnknownCheckWithoutEvidence()
    {
        var run = Fixed.Id(0xf9, 0);
        var t = Fixed.T0.AddHours(8);
        var verification = Verified(run, t.AddSeconds(30), ("refund-issued", "*", CheckResult.Pass));
        return new FinalizeInput(
            "Two required checks: one declared with the any-kind \"*\", met by evidence of kind manual-review; one with no " +
            "evidence at all. The missing check makes the outcome Unknown (completion score 0.5), so the record is Quarantined.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "refund-stuck-pending", "Refund ticket 49 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_refund_check", t.AddSeconds(1), new(), result: "refund issued"),
                ],
                result: "Done."),
            ]),
            Fixed.Id(0xf9, 0xe0),
            verification with
            {
                RequiredChecks = [.. verification.RequiredChecks, new RequiredCheckInput("ledger-balanced", "integration-test")],
                Evidence = [.. verification.Evidence.Select(evidence => evidence with { Kind = "manual-review" })],
            });
    }

    private static FinalizeInput ThreeFailedAttempts()
    {
        var run = Fixed.Id(0xfa, 0);
        var t = Fixed.T0.AddHours(9);
        return new FinalizeInput(
            "Three failed attempts before one that worked: the lesson counts them and names the last one's class. The scope " +
            "sets every optional field (team and user too), each of which takes part in the derived IDs.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "deploy-service", "Deploy service X to prod", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "run_deploy", t.AddSeconds(1), new(), result: null, error: "HTTP/1.1 503 Service Unavailable")], error: "HTTP/1.1 503 Service Unavailable"),
                Attempt(run, 1, t.AddSeconds(5), [Call(run, 1, 0, "run_deploy", t.AddSeconds(5), new(), result: null, error: "process exited with code 2")], error: "process exited with code 2"),
                Attempt(run, 2, t.AddSeconds(9), [Call(run, 2, 0, "run_deploy", t.AddSeconds(9), new(), result: null, error: "ETIMEDOUT")], error: "ETIMEDOUT: the deploy timed out"),
                Attempt(run, 3, t.AddSeconds(13), [Call(run, 3, 0, "run_deploy", t.AddSeconds(13), new(), result: "ok")], result: "Deployed."),
            ]) with { Scope = Fixed.Scope with { TeamId = "payments", UserId = "user-17" } },
            Fixed.Id(0xfa, 0xe0),
            Verified(run, t.AddSeconds(30), ("service-healthy", "smoke-test", CheckResult.Pass)));
    }

    private static FinalizeInput RejectedAttempts()
    {
        var run = Fixed.Id(0xfb, 0);
        var t = Fixed.T0.AddHours(10);
        var wide = new Dictionary<string, object?>();
        for (var index = 0; index < 51; index++)
        {
            wide[$"f{index}"] = index;
        }

        var deep = new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?> { ["a"] = "deep" } } } } };
        return new FinalizeInput(
            "Capture refuses a whole attempt whose arguments are nested deeper than 4 levels, hold more than 50 fields, or use a " +
            "field name containing \".\": nothing of it is stored (see expected-capture.json), and the attempts it keeps are " +
            "numbered from 0 in the order they were recorded.",
            new FinalizeOptions(AllowedArgumentNames: ["a", .. wide.Keys]),
            Run(run, "search-refunds", "Find refunds over 100 EUR this week", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "search_refunds", t.AddSeconds(1), deep, result: "too deep")], result: "rejected"),
                Attempt(run, 1, t.AddSeconds(2), [Call(run, 1, 0, "search_refunds", t.AddSeconds(2), wide, result: "too wide")], result: "rejected"),
                Attempt(run, 2, t.AddSeconds(3), [Call(run, 2, 0, "search_refunds", t.AddSeconds(3), new() { ["a.b"] = "x" }, result: "bad name")], result: "rejected"),
                Attempt(run, 3, t.AddSeconds(4), [Call(run, 3, 0, "search_refunds", t.AddSeconds(4), new() { ["a"] = "amount > 100" }, result: "3 rows")], result: "3 refunds found."),
            ]),
            Fixed.Id(0xfb, 0xe0),
            Verified(run, t.AddSeconds(30), ("results-returned", "assertion", CheckResult.Pass)));
    }

    private static FinalizeInput CaptureCaps()
    {
        var run = Fixed.Id(0xfc, 0);
        var t = Fixed.T0.AddHours(11);
        var attempts = new List<Attempt>();
        for (var index = 0; index < 11; index++)
        {
            var calls = new List<ToolCallRecord>();
            var count = index == 9 ? 52 : 1;
            for (var call = 0; call < count; call++)
            {
                calls.Add(Call(run, index, call, "fetch_page", t.AddSeconds(index), new(), result: "ok"));
            }

            attempts.Add(index < 9
                ? Attempt(run, index, t.AddSeconds(index), calls, error: "System.TimeoutException: page timed out")
                : Attempt(run, index, t.AddSeconds(index), calls, result: "Exported."));
        }

        return new FinalizeInput(
            "A run keeps at most 10 attempts, and an attempt at most 50 tool calls: the 52-call tenth attempt keeps its first 50, " +
            "and an eleventh attempt is refused with CapacityExceeded.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "export-catalog", "Export the whole catalog", t, attempts),
            Fixed.Id(0xfc, 0xe0),
            Verified(run, t.AddSeconds(30), ("export-complete", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput ExposuresAndCorrelationId()
    {
        var run = Fixed.Id(0xfd, 0);
        var t = Fixed.T0.AddHours(12);
        return new FinalizeInput(
            "A run that was shown two stored records and carries a host correlation ID: the provenance keeps both. The " +
            "exposures are recorded through capture (a run starts exposed to nothing), not passed in at the start.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "refund-stuck-pending", "Refund ticket 50 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "run_refund_check", t.AddSeconds(1), new(), result: "refund issued")], result: "Refund issued."),
            ]) with
            {
                Provenance = Fixed.Provenance(t) with
                {
                    CorrelationId = "trace-7f3a",
                    ExposedTo = [new RunExposure(Fixed.Id(0x5a, 1), 1), new RunExposure(Fixed.Id(0x5a, 8), 3)],
                },
            },
            Fixed.Id(0xfd, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput ArgumentNumbers()
    {
        var run = Fixed.Id(0x110, 0);
        var t = Fixed.T0.AddHours(13);
        var arguments = new Dictionary<string, object?>
        {
            ["negativeZero"] = JsonValue("-0"),
            ["negativeZeroFraction"] = JsonValue("-0.0"),
            ["int64Max"] = JsonValue("9223372036854775807"),
            ["int64Min"] = JsonValue("-9223372036854775808"),
            ["beyondInt64"] = JsonValue("15000000000000000000"),
            ["belowInt64"] = JsonValue("-10000000000000000000"),
            ["twentyOneDigits"] = JsonValue("100000000000000000000"),
            ["largestDouble"] = JsonValue("1.7976931348623157e308"),
            ["fraction"] = JsonValue("0.1"),
            ["exponent"] = JsonValue("1E2"),
        };

        return new FinalizeInput(
            "Allowlisted argument values that are JSON numbers at and beyond a 64-bit integer's range (integers a double " +
            "holds exactly), with a fraction or an exponent, and negative zero: capture keeps each as the JSON number it was " +
            "given, and a reader reads an integer that fits in 64 bits as one and any other number as a double. The inject " +
            "case argument-numbers shows how a block renders them.",
            new FinalizeOptions(AllowedArgumentNames: [.. arguments.Keys]),
            Run(run, "set-limits", "Set the export limits", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "set_limits", t.AddSeconds(1), arguments, result: "limits set")], result: "Done."),
            ]),
            Fixed.Id(0x110, 0xe0),
            Verified(run, t.AddSeconds(30), ("limits-applied", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput SecretNamesOrdinalIgnoreCase()
    {
        var run = Fixed.Id(0x111, 0);
        var t = Fixed.T0.AddHours(14);
        var arguments = new Dictionary<string, object?>
        {
            ["PASSWORD"] = "a secret name in another case",
            ["pa\u017Fsword"] = "the long s is not s",
            ["TOKEN"] = "a secret name in another case",
            ["to\u212Aen"] = "the Kelvin sign is not k",
            ["ap\u0130Key"] = "the dotted capital I is not i",
            ["ap\u0131Key"] = "the dotless i is not i",
            ["SECRET"] = "a secret name in another case",
            ["query"] = "amount > 100",
        };

        return new FinalizeInput(
            "Secret names match ignoring case ordinally, one UTF-16 code unit at a time by its simple upper-case mapping: " +
            "PASSWORD, TOKEN and SECRET are redacted, while the long s (U+017F), the Kelvin sign (U+212A), the dotted capital " +
            "I (U+0130) and the dotless i (U+0131) fold to no ASCII letter, so those names are not secret and, allowlisted " +
            "exactly, are kept.",
            new FinalizeOptions(AllowedArgumentNames: ["pa\u017Fsword", "to\u212Aen", "ap\u0130Key", "ap\u0131Key", "query"]),
            Run(run, "search-refunds", "Find refunds over 100 EUR this week", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "search_refunds", t.AddSeconds(1), arguments, result: "3 rows")], result: "3 refunds found."),
            ]),
            Fixed.Id(0x111, 0xe0),
            Verified(run, t.AddSeconds(30), ("results-returned", "assertion", CheckResult.Pass)));
    }

    private static FinalizeInput SeparatorTaskAndCheckIds()
    {
        var run = Fixed.Id(0x112, 0);
        var t = Fixed.T0.AddHours(15);
        return new FinalizeInput(
            "The information separators U+001C to U+001F are not whitespace: a task ID and a required check ID made of one are " +
            "not blank, so the run is captured and verified, and the lesson names the check as it is.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "\u001C", "Separators \u001C\u001D\u001E\u001F between words", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "run_check", t.AddSeconds(1), new(), result: "ok")], result: "Done."),
            ]),
            Fixed.Id(0x112, 0xe0),
            Verified(run, t.AddSeconds(30), ("\u001F", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput ArgumentNumbersBeyondDoubleRange()
    {
        var run = Fixed.Id(0x120, 0);
        var t = Fixed.T0.AddHours(16);

        // double.MaxValue is (2^53 - 1) * 2^971. The integer midway between it and the next power of two, MaxValue + 2^970,
        // rounds (to even) up to an infinity. A number just below that midpoint rounds down to MaxValue and is kept, but a
        // stored record holding it reads back as MaxValue, so no fixture record can hold one: README-1.2 states it.
        const string MaxValueMidpoint = "179769313486231580793728971405303415079934132710037826936173778980444968292764750946649017977587207096330286416692887910946555547851940402630657488671505820681908902000708383676273854845817711531764475730270069855571366959622842914819860834936475292719074168444365510704342711559699508093042880177904174497792";
        var integerBeyondRange = "1" + new string('0', 400);
        return new FinalizeInput(
            "Capture refuses a whole attempt with an allowlisted argument that is a JSON number beyond a double's range: in " +
            "exponent form at the top level or nested, an integer literal of 401 digits, or the integer midway between " +
            "double.MaxValue and the next power of two, which rounds to an infinity. Nothing of such an attempt is stored " +
            "(see expected-capture.json). 1.5e19, beyond a 64-bit integer but finite as a double, is kept as written. A " +
            "number beyond a double's range under a secret name, at the top level or nested, is redacted without being " +
            "read, and under a name not allowlisted it is omitted.",
            new FinalizeOptions(AllowedArgumentNames: ["limit", "limits", "ceiling"]),
            Run(run, "set-limits", "Set the export limits", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "set_limits", t.AddSeconds(1), new() { ["limit"] = JsonValue("1e400") }, result: "too large")], result: "rejected"),
                Attempt(run, 1, t.AddSeconds(2), [Call(run, 1, 0, "set_limits", t.AddSeconds(2), new() { ["limits"] = JsonValue("{\"limit\":-1e400}") }, result: "too small")], result: "rejected"),
                Attempt(run, 2, t.AddSeconds(3), [Call(run, 2, 0, "set_limits", t.AddSeconds(3), new() { ["limit"] = JsonValue(integerBeyondRange) }, result: "too large")], result: "rejected"),
                Attempt(run, 3, t.AddSeconds(4), [Call(run, 3, 0, "set_limits", t.AddSeconds(4), new() { ["ceiling"] = JsonValue(MaxValueMidpoint) }, result: "too large")], result: "rejected"),
                Attempt(run, 4, t.AddSeconds(5), [Call(run, 4, 0, "set_limits", t.AddSeconds(5), new()
                {
                    ["limit"] = JsonValue("1.5e19"),
                    ["token"] = JsonValue("1e400"),
                    ["limits"] = JsonValue("{\"token\":-1e400}"),
                    ["ignored"] = JsonValue("-1e400"),
                }, result: "limits set")], result: "Done."),
            ]),
            Fixed.Id(0x120, 0xe0),
            Verified(run, t.AddSeconds(30), ("limits-applied", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput SubMicrosecondInstants()
    {
        var run = Fixed.Id(0x130, 0);

        // 100-ns ticks below the microsecond on every instant the run carries: .1234567 truncates to .123456, where
        // rounding would give .123457.
        var t = Fixed.T0.AddHours(17).AddTicks(1_234_567);
        return new FinalizeInput(
            "Every instant the host supplied carries 100-ns ticks below the microsecond (.1234567). The finalization instant " +
            "is truncated, never rounded, to whole microseconds (.123456) before anything is derived from it: the outcome's " +
            "evaluatedAt, the reflection's createdAt, the created_at and updated_at columns and the initial event's " +
            "occurred_at all hold the truncated instant. The instants the record copies from the run as given (the " +
            "provenance's recordedAt, each attempt's and tool call's startedAt, each evidence's capturedAt) are payload " +
            "instants and keep all seven fractional digits.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "refund-stuck-pending", "Refund ticket 51 is stuck in pending", t,
            [
                Attempt(run, 0, t.AddSeconds(1),
                [
                    Call(run, 0, 0, "run_refund_check", t.AddSeconds(1), new(), result: null, error: "TimeoutException: the refund lock timed out"),
                ],
                error: "TimeoutException: the refund lock timed out"),
                Attempt(run, 1, t.AddSeconds(5), [Call(run, 1, 0, "run_refund_check", t.AddSeconds(5), new(), result: "refund issued")], result: "Refund issued."),
            ]),
            Fixed.Id(0x130, 0xe0),
            Verified(run, t.AddSeconds(30), ("refund-issued", "integration-test", CheckResult.Pass)));
    }

    private static FinalizeInput SubMicrosecondInstantsAtASecondBoundary()
    {
        var run = Fixed.Id(0x131, 0);

        // .9999996 is 4 ticks short of the next second: rounding to microseconds would carry into it, truncation never does.
        var t = Fixed.T0.AddHours(18).AddTicks(9_999_996);
        return new FinalizeInput(
            "A finalization instant 400 ns before a whole second (:59.9999996): truncated to whole microseconds it is " +
            ":59.999999, never the next second, in every instant derived from it (evaluatedAt, the reflection's createdAt, " +
            "created_at, updated_at and the initial event's occurred_at). The run's own instants (the provenance's " +
            "recordedAt, the attempt's and tool call's startedAt, the evidence's capturedAt) sit at the same fraction of " +
            "other seconds and keep all seven fractional digits.",
            new FinalizeOptions(AllowedArgumentNames: null),
            Run(run, "rotate-signing-keys", "Rotate the signing keys for tenant 7", t,
            [
                Attempt(run, 0, t.AddSeconds(1), [Call(run, 0, 0, "rotate_keys", t.AddSeconds(1), new(), result: "rotated")], result: "Keys rotated."),
            ]),
            Fixed.Id(0x131, 0xe0),
            Verified(run, t.AddSeconds(59), ("keys-rotated", "integration-test", CheckResult.Pass)));
    }

    /// <summary>A JSON value exactly as written, as a host that deserialized a tool call's arguments holds it.</summary>
    private static JsonElement JsonValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // ---- Builders ----------------------------------------------------------------------------------------------------

    internal static ExperienceRun Run(Guid runId, string taskId, string? description, DateTimeOffset startedAt, IReadOnlyList<Attempt> attempts) => new(
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

    internal static Attempt Attempt(Guid runId, int sequence, DateTimeOffset startedAt, IReadOnlyList<ToolCallRecord> calls, string? result = null, string? error = null) => new(
        SubId(runId, 0xa0 + sequence),
        sequence,
        startedAt,
        TimeSpan.FromSeconds(3.5),
        calls,
        result,
        error);

    internal static ToolCallRecord Call(
        Guid runId,
        int attempt,
        int sequence,
        string toolName,
        DateTimeOffset startedAt,
        Dictionary<string, object?> arguments,
        string? result,
        string? error = null) => new(
            SubId(runId, 0xc000 + (attempt * 0x100) + sequence),
            sequence,
            toolName,
            arguments,
            startedAt,
            TimeSpan.FromMilliseconds(1250),
            result,
            error);

    internal static VerificationInput Verified(Guid runId, DateTimeOffset finalizedAt, params (string CheckId, string Kind, CheckResult Result)[] checks) => new(
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
    internal static Guid SubId(Guid runId, int n)
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
