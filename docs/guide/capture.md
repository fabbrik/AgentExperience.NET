# Capturing runs with MAF

**In short.** One call, `UseExperienceCapture`, wraps a Microsoft Agent Framework (MAF) agent so that every
invocation is recorded as an Experience Run: the attempt, each tool call with its arguments and result, and how the
invocation ended (success, failure, cancellation, or a stream the caller stopped reading). Everything goes through
Core's sanitizer before it is kept, so unsafe content is refused rather than stored. Capture never changes what the
caller sees, and it never throws into the agent. By default one invocation is one run; you can opt in to recording
retries as attempts of one run, so a later lesson sees the failure and the fix together.

Package: `AgentExperience.MicrosoftAgentFramework`, over Core's `IExperienceCaptureService`. It requires
`Microsoft.Agents.AI` 1.22.0 or any later 1.x (declared `[1.22.0, 2.0.0)`).

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
  success, failure, cancellation, or when a streaming consumer stops early — unless you asked to keep the run open
  for a further attempt (see [Retries as attempts of one run](#retries-as-attempts-of-one-run)).
- **Function middleware** (when `CaptureToolCalls` is `true`, the default). It records each tool call, including
  its result or exception, into the run for that invocation.

Every recorded value goes through the `IExperienceCaptureService` you pass in, so its sanitizer and limits apply
unchanged. This adapter never evaluates, reflects, or persists anything itself; when you configure finalization it
hands the completed run to Core's `ExperienceFinalizationService`, which owns all of that (see
[Finalization](finalization.md#finalizing-from-the-maf-adapter)).

When the caller passes a session, the run ID is written to it under
`ExperienceCaptureAgentBuilderExtensions.RunIdStateKey` (`"AgentExperience.RunId"`) as a `"D"`-formatted GUID
string. Nothing else is written to the session. The run ID is never placed in `AgentRunOptions.AdditionalProperties`,
because MAF forwards those to the model provider.

## Sanitization happens at capture

Sanitization is the first gate, and it is fail-closed at capture time rather than at storage time. The default
sanitizer applies a per-`Kind` policy (tool arguments are `"ToolArguments"`, results and errors `"ToolResult"`): a
field you allowlisted is kept, a field you marked secret is redacted as a whole value, and anything else is dropped.
A `Kind` with no policy at all is rejected, never passed through.

When content cannot be sanitized, `AppendAttemptAsync` returns `AppendAttemptOutcome.SanitizationRejected` and the
sanitizer's own `Reason`, the attempt is not recorded, the run stays open, and **nothing is stored anywhere** —
there is no database involved, so there is no partial write and no persisted denial record to reconcile later. The
host is told the decision and why, and can correct and resubmit the same attempt ID; the rejected ID is not tracked,
so a corrected resubmission succeeds. Unsafe content therefore never reaches an Experience Record, and never becomes
something a grant could later share.

Sanitization is name-based: it cannot guarantee that every arbitrary secret hiding in free text is detected. See
`DefaultSanitizer`.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `ResolveRun` | required | Maps messages, session, and agent to a task ID, scope, task description, and optionally the ID of a run this invocation continues. If it throws or returns a null descriptor, task ID, or scope, the invocation runs uncaptured and the failure is reported. |
| `ShouldCompleteRun` | always complete | Decides, after the attempt is recorded, whether this invocation's run is finished. `false` keeps it open for a further attempt. The default is exactly the behaviour every host had before continuation existed. |
| `MaxOpenRunDuration` | 5 min | How long a run may stay open across invocations before the adapter completes it itself and reports. Must be positive and at most `uint.MaxValue - 1` milliseconds. |
| `MaxAttemptsPerOpenRun` | 8 | The most attempts a run the adapter is holding open may accumulate before it is completed anyway and reported. Must be positive. Separate from the capture service's own `CaptureLimits.MaxAttemptsPerRun`; whichever is reached first ends the run. |
| `Environment` | machine name + `RuntimeInformation` | Environment fingerprint recorded on every run. |
| `CaptureToolCalls` | `true` | Registers function middleware. Requires a `ChatClientAgent`. |
| `FinalizationTimeout` | 5 s | Upper bound on the whole post-invocation step: appending the attempt, completing the run, and — when `FinalizationService` is set — finalizing it into a durable Experience Record. With finalization configured this bounds database round trips, not just in-memory capture, so 5 s may be too tight. Must be positive and at most `uint.MaxValue - 1` milliseconds. It uses its own token, never the caller's. |
| `OnCaptureFailure` | none | Called when capture fails, at most once per failure stage per run. The stages are `ResolveRun`, `StartRun`, `Finalize` (recording the attempt and completing the run in memory), `ToolCall`, `Finalization` (turning the completed run into a durable Experience Record), and `RecordExposure` (recording which records the context provider delivered into the run). Exceptions it throws are swallowed. |
| `FinalizationService` | none | Core's `ExperienceFinalizationService`. When set, each successfully captured run is finalized into a durable Experience Record. Requires `ResolveFinalization`. |
| `ResolveFinalization` | none | Builds the `FinalizeExperienceRequest` for one completed run. Return `null` to skip finalizing that run. Required when `FinalizationService` is set. |
| `OnRunFinalized` | none | Receives every `FinalizeExperienceResult`, durable or not — including a host decision such as `StorageDenied`, which is not a capture failure. Not called once finalization has overrun `FinalizationTimeout`. Exceptions it throws are swallowed. |
| `TimeProvider` | `TimeProvider.System` | Timestamps, durations, and the finalization timeout. |
| `NewId` | `Guid.NewGuid` | Run, attempt, tool-call, and completion-event IDs. Must be thread-safe. |

## Retries as attempts of one run

By default one invocation is one run: it is opened, its single attempt is recorded, and it is completed. A retry is
therefore a *second run* — the failed one finalizes on its own (quarantined, never reusable) and the successful one
is reflected on as if the failure never happened. That makes the library's own premise, learning from failure,
unreachable through this adapter.

Opt out by naming the run the invocation continues and saying when the run is finished. **One retry cycle is one
run**: mint its identifier when the cycle starts, and a fresh one for the next cycle. A completed run is final, so an
identifier reused after its run closed is refused and every later invocation naming it runs uncaptured.

```csharp
Guid? currentRun = null;   // the run of the retry cycle in progress

var agent = innerAgent.AsBuilder()
    .UseExperienceCapture(capture, new ExperienceCaptureOptions
    {
        ResolveRun = _ => new ExperienceRunDescriptor("triage-ticket", hostScope, ContinuesRunId: currentRun),
        // Decided on the attempt's own recorded result, inside the invocation, so the attempt that
        // succeeds is the one that closes the run.
        ShouldCompleteRun = ctx => (ctx.Result is { } text && Check(text)) || ctx.AttemptCount >= 3,
        MaxAttemptsPerOpenRun = 3,
        MaxOpenRunDuration = TimeSpan.FromMinutes(2),
        FinalizationService = finalization,
        ResolveFinalization = ctx => BuildRequest(ctx.Run),
    }, out var captureLifetime)
    .Build();

async Task RunCycleAsync(string prompt)
{
    currentRun = Guid.NewGuid();   // a new cycle, so a new run

    for (var attempt = 1; attempt <= 3; attempt++)
    {
        var response = await agent.RunAsync(prompt);
        if (Check(response.Text))
        {
            break;
        }
    }
}

// At teardown, so no open-run bound fires into a host that is shutting down.
captureLifetime.Dispose();
```

The second invocation of a cycle appends attempt `1` to the same run, so the reflection eventually produced sees the
failure and the success together; the next call to `RunCycleAsync` starts a new run. Finalization is deferred with the
run: an open run is never handed to `FinalizationService`, because only a completed run can become a durable
Experience Record. The example keeps the cycle's identifier in one field, so it runs one cycle at a time; a host
running several at once keeps each cycle's identifier with that task's own state and returns it from `ResolveRun`.

**The rules this comes with.**

- A continuation ID that names a run with a **different task ID or scope**, or a run that has **already been
  completed**, is a conflict: that invocation runs uncaptured and the refusal is reported through `OnCaptureFailure`
  at the `StartRun` stage, exactly as a colliding identifier always has been. A run finalizes once and is never
  reopened.
- A continuation ID that names **no run at all** simply opens a new run under it. An all-zeros ID (`Guid.Empty`) is
  refused at the `ResolveRun` stage: it is a default-valued field, not a run, and accepting it would pile every
  invocation onto one run.
- **A continuation joins a run; it never rewrites it.** The run keeps the task description, environment, provenance
  (its `CorrelationId` included) and start time it was *opened* with, and the continuing invocation's own are dropped.
  So an ID reused by accident, with the same task and scope, is merged rather than refused, and the two invocations
  are reflected on as one run. Matching task and scope is the only check, and the capture service keeps its runs for
  the process lifetime, so the collision window is that long. Minting the ID per cycle is what prevents it.
- **Two invocations cannot capture on one run at once** — including while the invocation that *opened* the run is
  still in flight, since its ID is readable from the session the moment the run exists. The second is refused and
  runs uncaptured, rather than interleaving a second half-recorded attempt.
- **What the default path costs.** To make the rule above hold for the invocation that opens a run, every captured
  invocation takes a transient claim (one entry in the registration's open-run ledger) for as long as it is in
  flight, the default single-invocation path included. It also arms its run's duration bound, one timer on your
  `TimeProvider`, as soon as the run is opened (see the next point). When the invocation releases the run, the
  claim is removed and the timer is disposed. A default invocation, which completes its own run, therefore leaves no
  entry and no timer behind. In a microbenchmark on one machine (not part of this repository), creating and
  disposing the timer with `TimeProvider.System` took about 45 ns and 120 bytes. Together with keeping the timer
  from capturing the invocation's `ExecutionContext`, it added about 200 bytes to an in-memory invocation through the
  adapter (about 5.7 KB allocated, 7 to 10 µs), with no time difference above the noise. That is far below the cost
  of one model call. A run that is left open keeps its entry and its timer until the run is completed, and so does
  a run whose default finalization failed or overran `FinalizationTimeout`, because whether that run was completed
  is unknown.
- **An open run is bounded from the moment it opens, and never silent.** It holds captured payload in memory, so
  the adapter completes it, reporting through `OnCaptureFailure`, in any of these cases:
  - it reaches `MaxAttemptsPerOpenRun`;
  - it has been open for `MaxOpenRunDuration`. A timer on your `TimeProvider` enforces this, armed when the run is
    opened, so neither a host that never invokes again nor an invocation that never returns can leave a run open;
  - an attempt could not be recorded at all;
  - the run could not be read back to ask `ShouldCompleteRun`;
  - `ShouldCompleteRun` throws.

  There is no path where a run stays open because a host forgot to close it. If the duration bound fires while an
  invocation is holding the run, the close is handed to that invocation once, and the bound re-arms for one further
  period. If the invocation has still not come back by then, the run is closed underneath it: a hung inner agent, a
  streaming consumer that abandoned its enumerator without disposing it, or the invocation that *opened* the run and
  never returned. A run that was completed normally is never reported as closed at its bound. The one exception is
  a `TimeProvider` whose `CreateTimer` throws. That is reported at once, the run has no bound while its invocation is
  in flight, and the run is completed when the invocation returns instead of being left open.
- **So an invocation can hold a run for at most twice `MaxOpenRunDuration`, on the default path too.** The only
  addition to that is the time the close itself takes, which is bounded by `FinalizationTimeout`. A default
  invocation still running when one bound period ends is handed the bound and completes its run normally when it
  returns. One still running at twice the duration has its run completed as `Cancelled` underneath it, and that is
  reported. When it does return, its answer is untouched, but its attempt is refused by the completed run and that
  is reported too. Set `MaxOpenRunDuration` (default 5 minutes) above the longest invocation you expect.
- **Bound callbacks arrive off the invocation.** A run closed by `MaxOpenRunDuration` is completed, reported through
  `OnCaptureFailure`, and finalized (with `OnRunFinalized`) from a timer callback on a thread-pool thread. That is
  never the thread of an invocation, including a hung one still holding the run. This holds with
  `TimeProvider.System` and with any provider that runs timer callbacks asynchronously. A fake clock that fires a
  due timer synchronously inside `CreateTimer` or `Change` runs the callback on whichever thread called it. Both
  callbacks must be thread-safe.

  Use the `UseExperienceCapture(..., out var captureLifetime)` overload and dispose the handle at teardown.
  Disposal cancels every armed bound, including one armed at open for an invocation still in flight. After
  disposal, the adapter reports nothing more through its bounds and starts no finalization. Disposal does not
  wait: a close that was already inside your capture service when you disposed still finishes writing its
  completion, and a callback already running is not interrupted. A run still open at that moment is abandoned:
  nothing is written and no timer reports it, and an invocation still in flight says so as it returns. Later
  invocations through that agent run uncaptured and say so. The default configuration can have bounds to dispose
  too: a hung invocation's, and one whose finalization failed.
- The run this registration is holding open belongs to that registration. Build the agent once and reuse it; two
  independently built agents do not share continuations.

## Supported agent types

| Agent | Run lifecycle | Tool calls |
| --- | --- | --- |
| `ChatClientAgent` | yes | yes |
| Other `AIAgent` subclasses, `A2AAgent` | yes, with `CaptureToolCalls = false` | no. `Build()` throws if `CaptureToolCalls` is `true`, because MAF function middleware needs a `FunctionInvokingChatClient`. A2A tools run server-side. |
| `ChatClientAgent` with `UseProvidedChatClientAsIs = true` and a raw client without `FunctionInvokingChatClient` | yes, with `CaptureToolCalls = false` | not supported |

## Semantics

- **One invocation is one attempt.** That attempt holds every tool call from the invocation, ordered by
  start time, including calls MAF runs concurrently (`AllowConcurrentInvocation`). MAF's own function-calling loop
  iterations are not split into separate attempts. By default that attempt's run is completed with the invocation, so
  one invocation is one run; a host that opts into continuation gets several attempts on one run instead.
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
  complete the run), while `Finalization` is turning that completed run into a durable Experience Record. Each
  failure stage is reported at most once per run, so an earlier tool-call or session-write failure never hides a
  later finalization failure.
- **Finalization.** Completion is attempted even when appending the attempt fails. If finalization times out, the run
  may be left without an attempt or completion.
- **Latency.** Finalization is awaited before the response is returned, the exception rethrown, or the stream ends, so
  it can add up to `FinalizationTimeout` of caller-visible latency. That includes invocations that failed or were
  cancelled.
- **Undisposed streams.** A streaming run is finalized when its enumerator finishes or is disposed. An enumerator that
  is never disposed (for example, abandoned without `await foreach` or `DisposeAsync`) holds its run open until the
  open-run bound closes it: at most twice `MaxOpenRunDuration`, reported through `OnCaptureFailure` (see
  [Retries as attempts of one run](#retries-as-attempts-of-one-run)).
- **Late tool calls.** A tool call that finishes after its run was finalized is not recorded. This can happen after an
  early stream break.

## MAF caveats (1.22.0 and later 1.x)

- **Reusing a `ChatClientAgentRunOptions` instance for one invocation after another is safe from 1.22.0.** At 1.20.0,
  MAF's function middleware wrote its `ChatClientFactory` onto the options instance it received, so a reused instance
  stacked a middleware layer per invocation. 1.22.0 works on a per-run clone and leaves the caller's instance, and any
  factory the host set on it, unchanged. The three `ExperienceCaptureTests.A_reused_ChatClientAgentRunOptions_instance_…`
  tests pin this for sequential reuse, streaming and not, with and without a host factory. Sharing one instance
  across *concurrent* invocations is not tested; MAF's source clones it per run, but this library makes no claim.
- MAF function middleware accepts only a `null` `AgentRunOptions`, a plain `AgentRunOptions`, or a
  `ChatClientAgentRunOptions`. Other options subclasses throw `NotSupportedException` when `CaptureToolCalls` is
  `true`.
- Capture materializes the input messages once (reusing them when they are already a collection) and passes that
  same collection to both `ResolveRun` and the inner agent, so a one-shot sequence is not consumed by the resolver.
- CI tests the range's floor and the newest 1.x on every change and on a weekly schedule, and a 1.x that breaks the
  adapter fails `main` on the next push or scheduled run. MAF 2.0 and later are outside the range, and NuGet warns
  (NU1608) about this package when a host resolves one. The reasons are in
  [the version policy](../compatibility-evidence.md#the-version-policy-floors-and-one-bounded-range).
