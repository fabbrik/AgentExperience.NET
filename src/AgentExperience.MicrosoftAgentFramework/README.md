# AgentExperience.MicrosoftAgentFramework

Records Microsoft Agent Framework (MAF) invocations and their tool calls as AgentExperience.NET Experience Runs.
It covers ordinary, streaming, failed, and cancelled invocations, plus streams the consumer stops reading early.

Pinned to `Microsoft.Agents.AI` **1.20.0** (exact). No other MAF version is verified.

## Usage

```csharp
using AgentExperience.Core.Capture;
using AgentExperience.Core.Sanitization;
using AgentExperience.MicrosoftAgentFramework;
using Microsoft.Agents.AI;

IExperienceCaptureService capture = new InMemoryExperienceCaptureService(
    new DefaultSanitizer(sanitizationOptions),
    captureLimits);

AIAgent agent = chatClientAgent
    .AsBuilder()
    .UseExperienceCapture(capture, new ExperienceCaptureOptions
    {
        ResolveRun = context => new ExperienceRunDescriptor(
            TaskId: "triage-ticket",
            Scope: hostScope,                  // established by the host, never taken from model output
            TaskDescription: "Triage an incoming support ticket"),
        OnCaptureFailure = failure => logger.LogWarning("Capture failed at {Stage}: {Reason}", failure.Stage, failure.Reason),
    })
    .Build();

var response = await agent.RunAsync("...", session);
```

Call `UseExperienceCapture` first on the builder so capture is the outermost layer. It registers:

- **Agent-run middleware.** It opens the run before the inner agent executes and completes it exactly once, on
  success, failure, cancellation, or when a streaming consumer stops early.
- **Function middleware** (when `CaptureToolCalls` is `true`, the default). It records each tool call, including
  its result or exception, into the run for that invocation.

Every recorded value goes through the `IExperienceCaptureService` you pass in, so its sanitizer and limits apply
unchanged. This adapter never evaluates, reflects, or persists anything itself; when you configure finalization
(below) it hands the completed run to Core's `ExperienceFinalizationService`, which owns all of that.

When the caller passes a session, the run ID is written to it under
`ExperienceCaptureAgentBuilderExtensions.RunIdStateKey` (`"AgentExperience.RunId"`) as a `"D"`-formatted GUID
string. Nothing else is written to the session. The run ID is never placed in `AgentRunOptions.AdditionalProperties`,
because MAF forwards those to the model provider.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `ResolveRun` | required | Maps messages, session, and agent to a task ID, scope, and task description. If it throws or returns a null descriptor, task ID, or scope, the invocation runs uncaptured and the failure is reported. |
| `Environment` | machine name + `RuntimeInformation` | Environment fingerprint recorded on every run. |
| `CaptureToolCalls` | `true` | Registers function middleware. Requires a `ChatClientAgent`. |
| `FinalizationTimeout` | 5 s | Upper bound on the whole post-invocation step: appending the attempt, completing the run, and — when `FinalizationService` is set — finalizing it into a durable Experience Record. With finalization configured this bounds database round trips, not just in-memory capture, so 5 s may be too tight. Must be positive and at most `uint.MaxValue - 1` milliseconds. It uses its own token, never the caller's. |
| `OnCaptureFailure` | none | Called when capture fails, at most once per failure stage per run. The stages are `ResolveRun`, `StartRun`, `Finalize` (recording the attempt and completing the run in memory), `ToolCall`, and `Finalization` (turning the completed run into a durable Experience Record). Exceptions it throws are swallowed. |
| `FinalizationService` | none | Core's `ExperienceFinalizationService`. When set, each successfully captured run is finalized into a durable Experience Record. Requires `ResolveFinalization`. |
| `ResolveFinalization` | none | Builds the `FinalizeExperienceRequest` for one completed run. Return `null` to skip finalizing that run. Required when `FinalizationService` is set. |
| `OnRunFinalized` | none | Receives every `FinalizeExperienceResult`, durable or not — including a host decision such as `StorageDenied`, which is not a capture failure. Not called once finalization has overrun `FinalizationTimeout`. Exceptions it throws are swallowed. |
| `TimeProvider` | `TimeProvider.System` | Timestamps, durations, and the finalization timeout. |
| `NewId` | `Guid.NewGuid` | Run, attempt, tool-call, and completion-event IDs. Must be thread-safe. |

## Finalizing captured runs

Capture alone keeps the run in memory. To turn each invocation into a durable Experience Record, give the adapter
Core's finalization service and a resolver that supplies what only the host knows — the required checks, the
verification evidence and the round it was closed in, the authorization context, and the storage decision:

```csharp
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Verification;

var options = new ExperienceCaptureOptions
{
    ResolveRun = context => new ExperienceRunDescriptor("triage-ticket", hostScope),

    FinalizationService = finalization,          // AgentExperience.Core.Finalization.ExperienceFinalizationService
    ResolveFinalization = context => new FinalizeExperienceRequest(
        RunId: context.Run.RunId,
        Authorization: hostAuthorization,
        ClosedRound: new ClosedVerificationRound(roundId, artifactRevision),
        RequiredChecks: [new RequiredCheck("unit-tests-pass", ExpectedKind: "TestResult")],
        Evidence: evidenceFor(context.Run),
        CurrentArtifactRevision: artifactRevision,
        StorageDecision: StorageDecision.Permit,
        FinalizedAt: DateTimeOffset.UtcNow),

    OnRunFinalized = result => logger.LogInformation(
        "Experience {Id} is {Status}", result.ExperienceId, result.Status),
};
```

- **When it runs.** Immediately after the run's attempt and completion were both recorded, inside the same once-only
  step and the same `FinalizationTimeout`. A run whose capture reported a problem is never finalized, so a
  half-captured run is never persisted as if it were whole.
- **Opting out per run.** `ResolveFinalization` returning `null` skips that run, and is not a failure.
- **Failures.** A throwing resolver, a throwing `FinalizeAsync`, or a non-durable outcome (`StorageDenied`,
  `NotAuthorized`, `Failed`, …) is reported through `OnCaptureFailure` with stage `Finalization` and never thrown.
  The captured run is left untouched, so the host can retry finalization itself from the capture service.
- **Latency.** Finalization is a database round trip and is awaited inside `FinalizationTimeout`, so it adds
  caller-visible latency. Leave `FinalizationService` unset and finalize out of band if that is not acceptable.
- **Setting `FinalizationService` without `ResolveFinalization` throws** at `UseExperienceCapture`, rather than
  silently doing nothing.

## Supported agent types

| Agent | Run lifecycle | Tool calls |
| --- | --- | --- |
| `ChatClientAgent` | yes | yes |
| Other `AIAgent` subclasses, `A2AAgent` | yes, with `CaptureToolCalls = false` | no. `Build()` throws if `CaptureToolCalls` is `true`, because MAF function middleware needs a `FunctionInvokingChatClient`. A2A tools run server-side. |
| `ChatClientAgent` with `UseProvidedChatClientAsIs = true` and a raw client without `FunctionInvokingChatClient` | yes, with `CaptureToolCalls = false` | not supported |

## Semantics

- **One invocation is one run with one attempt.** That attempt holds every tool call from the invocation, ordered by
  start time, including calls MAF runs concurrently (`AllowConcurrentInvocation`). MAF's own function-calling loop
  iterations are not split into separate attempts.
- **Attempt result.** On success the attempt `Result` is the response `Text`. For streaming, it is the concatenated
  `Text` of the yielded updates. Tool-call `Result` is the tool's return value converted to a string, and `Arguments`
  are the `FunctionInvocationContext.Arguments`. All of it is raw input to the capture service's sanitizer.
- **Error classification.**
  - Errors are recorded as the exception's full type name only, never its message, because messages often embed
    arguments or secrets.
  - An `OperationCanceledException` marks the run `Cancelled` only when the caller's cancellation token is cancelled.
    Any other exception marks it `Failed`, including an `OperationCanceledException` from a provider or `HttpClient`
    timeout.
  - A streaming consumer that stops reading early marks the run `Cancelled` with no error. It is finalized when the
    enumerator is disposed.
- **Tool exceptions** are recorded, then rethrown unchanged. MAF then turns them into an error result for the model,
  and the run continues.
- **Capture never changes what the caller sees.** The response, streaming updates, and exceptions are exactly what
  the agent would produce without capture. Capture does not wrap or re-execute tools, retry, or keep its own session
  store.
- **Capture failures** are reported through `OnCaptureFailure` and never thrown. They include a resolver exception, a
  non-success capture outcome, a capture exception, a finalization timeout, and a failed Experience Record
  finalization. Note the two similarly named stages: `Finalize` is the in-memory capture step (append the attempt,
  complete the run), while `Finalization` is turning that completed run into a durable Experience Record. Each failure stage is reported at most
  once per run, so an earlier tool-call or session-write failure never hides a later finalization failure.
- **Finalization.** Completion is attempted even when appending the attempt fails. If finalization times out, the run
  may be left without an attempt or completion.
- **Latency.** Finalization is awaited before the response is returned, the exception rethrown, or the stream ends, so
  it can add up to `FinalizationTimeout` of caller-visible latency. That includes invocations that failed or were
  cancelled.
- **Undisposed streams.** A streaming run is finalized when its enumerator finishes or is disposed. An enumerator that
  is never disposed (for example, abandoned without `await foreach` or `DisposeAsync`) leaves the run open.
- **Late tool calls.** A tool call that finishes after its run was finalized is not recorded. This can happen after an
  early stream break.

## MAF caveats (1.20.0)

- **Don't reuse `ChatClientAgentRunOptions` instances across invocations.** MAF's function middleware changes the
  options instance it receives (`ChatClientFactory`), so a reused instance accumulates middleware layers.
- MAF function middleware accepts only a `null` `AgentRunOptions`, a plain `AgentRunOptions`, or a
  `ChatClientAgentRunOptions`. Other options subclasses throw `NotSupportedException` when `CaptureToolCalls` is
  `true`.
- Capture materializes the input messages once (reusing them when they are already a collection) and passes that
  same collection to both `ResolveRun` and the inner agent, so a one-shot sequence is not consumed by the resolver.
- Sanitization is name-based and cannot guarantee that every arbitrary secret is detected. See `DefaultSanitizer`.
