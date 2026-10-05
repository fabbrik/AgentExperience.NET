# AgentExperience.MicrosoftAgentFramework

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

The Microsoft Agent Framework (MAF) adapter for [AgentExperience.NET](https://github.com/fabbrik/AgentExperience.NET).
It does two independent things; use either, or both (plus an opt-in model-backed reflector):

- **Capture.** `UseExperienceCapture` records every MAF invocation — ordinary, streaming, failed, cancelled, or a
  stream the consumer stopped reading — and its tool calls as a sanitized Experience Run. It can finalize each run
  into a durable Experience Record, and record retries as attempts of one run.
- **Injection.** `ExperienceContextProvider` retrieves applicable past experience before each invocation and adds it
  as one delimited, labeled **Historical Reference** message.
- **Model-backed reflection (optional, off by default).** `AddAgentExperienceChatClientReflector` replaces the
  deterministic reflector with `ChatClientExperienceReflector`, which asks your `IChatClient` for the lesson text.
  **It sends sanitized captured run content to your model provider** — task text, tool names, clipped results and
  errors, check and evidence IDs. See [Model-backed reflection](#model-backed-reflection).

Requires `Microsoft.Agents.AI` **1.22.0 or any later 1.x** (declared `[1.22.0, 2.0.0)`). Targets `net10.0`. CI
tests the floor and the newest 1.x on every change and on a weekly schedule. MAF 2.0 and later are outside the range,
and NuGet warns (NU1608) when a host resolves one. Brings in `AgentExperience.Core`.

## Capture

```csharp
using AgentExperience.Core.Capture;
using AgentExperience.Core.Sanitization;
using AgentExperience.MicrosoftAgentFramework;
using Microsoft.Agents.AI;

IExperienceCaptureService capture = new InMemoryExperienceCaptureService(
    new DefaultSanitizer(sanitizationOptions),
    captureLimits,
    TimeProvider.System);   // the clock completed-run retention is measured on (DI uses the registered one)

AIAgent agent = chatClientAgent
    .AsBuilder()
    .UseExperienceCapture(capture, new ExperienceCaptureOptions
    {
        ResolveRun = context => new ExperienceRunDescriptor(
            TaskId: "triage-ticket",
            Scope: hostScope,                  // established by the host, never taken from model output
            TaskDescription: context.DerivedTaskText),   // the user's own words, stored as written: redact first if needed
        OnCaptureFailure = failure => logger.LogWarning("Capture failed at {Stage}: {Reason}", failure.Stage, failure.Reason),
    })
    .Build();

var response = await agent.RunAsync("...", session);
```

Call `UseExperienceCapture` first on the builder so capture is the outermost layer. What to know:

- **Capture never changes what the caller sees.** Responses, streaming updates and exceptions are exactly what the
  agent would produce without it, and capture failures are reported through `OnCaptureFailure`, never thrown.
- **Everything goes through the capture service's sanitizer and limits.** Errors are recorded as the exception's
  type name only, never its message.
- **Tool calls need a `ChatClientAgent`** (`CaptureToolCalls`, on by default). Other `AIAgent` types are captured
  with `CaptureToolCalls = false`.
- **Finalization is opt-in.** Set `FinalizationService` and `ResolveFinalizationAsync` (or the synchronous
  `ResolveFinalization`), and each completed run is turned into a durable record inside `FinalizationTimeout` (5 s by
  default). That adds caller-visible latency; leave it
  unset to finalize out of band.
- **Retries can be one run.** Return `ContinuesRunId` from `ResolveRun` and decide with `ShouldCompleteRun`, so a
  lesson sees the failure and the fix together. An open run is always bounded, by `MaxAttemptsPerOpenRun` (8) and
  `MaxOpenRunDuration` (5 minutes).
- **The run ID is written to the session** under `"AgentExperience.RunId"`, never into `AdditionalProperties` (MAF
  forwards those to the model provider).

Every option, the retry rules and bounds, the supported agent types, and MAF caveats are in
[Capturing runs with MAF](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/capture.md) and
[Finalization](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/finalization.md#finalizing-from-the-maf-adapter).

## Injection

```csharp
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;

var provider = new ExperienceContextProvider(
    retrieval,                    // AgentExperience.Core.Retrieval.ExperienceRetrievalService
    recordStore,                  // IExperienceRecordStore: the final eligibility check re-reads through it
    new ExperienceInjectionOptions
    {
        // The user's latest words, bounded; with none (an image-only turn, say), return null to skip injection.
        // Awaited with the invocation's token, so the caller's authorization and scope can be looked up here.
        ResolveRequestAsync = (context, cancellationToken) => ValueTask.FromResult(context.DerivedTaskText is { } taskText
            ? new RetrieveExperienceRequest(
                Authorization: hostAuthorization,  // host-established; nothing in the invocation may widen it
                Scope: hostScope,
                TaskText: taskText)
            : null),
        DecideInjection = decision => riskPolicy.Allows(decision.Current)
            ? InjectionDecision.Permit
            : InjectionDecision.Deny("risk policy"),
    });

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions { Tools = tools },
    AIContextProviders = [provider],
});
```

What to know:

- **The label is hygiene, not a security control.** The block says it is untrusted reference material; your
  tool-approval boundary is what stops a harmful call. A test proves an obeyed injected instruction is still denied.
- **Raw payloads never appear.** The block carries the lesson, guidance, provenance and, for a verified record, the
  ordered tool *names* the run called — never tool results, errors, evidence detail, or an argument value you did not
  allowlist in `ApproachArguments`.
- **Every record is re-checked immediately before injection**, in one batched read, against every rule retrieval
  applies, and then offered to your `DecideInjection` (fail-closed).
- **A lesson the agent cannot act on is not injected**, when you declare `ReceivingAgent`: its available tools and
  the highest tool risk class it may use. A record whose approach needs a missing tool, or a riskier one, is omitted
  with its own reason. Passing the gate grants no permission; your approval boundary still decides every call.
- **Limits drop whole records**: 8 records and 16 KB per block, re-checked within 500 ms, by default.
- **Reused sessions are tracked by default**: at most 32 records and 64 KB per session, no revision twice, and a
  fixed withdrawal notice when a record the session was given stops being valid. The notice is advisory: the earlier
  block stays in the history (the KL-12 boundary). `SessionLimits = null` turns tracking off; two providers on one
  agent need different `SessionStateKey`s.
- **It never throws into the invocation**, except for the caller's own cancellation.
- **With capture on too**, it records on the captured run which records it delivered, which is what lets confidence
  evidence about that run count.

The payload format, argument allowlists, grant disclosure levels, the session rules, and failure behaviour are in
[Injection into MAF](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/injection.md).

## Model-backed reflection

```csharp
using AgentExperience.MicrosoftAgentFramework.Reflections;

services.AddSingleton<IChatClient>(chatClient);            // a singleton, without UseFunctionInvocation
services.AddAgentExperienceChatClientReflector(options => options.ModelName = "your-model");
```

What to know:

- **Privacy: it sends sanitized captured run content to your model provider.** The task text (or task ID), each
  attempt's sequence number and ordered tool names, each tool call's and attempt's result and error clipped to 500
  characters, and the verification status, check IDs and evidence IDs; never tool arguments, evidence detail, the
  environment or the scope. It is off unless you register it, and the decision is yours. `BuildPrompt` shows the
  exact message, and the system prompt is the public constant `ChatClientExperienceReflector.SystemPrompt`.
- **Your `IChatClient` middleware can record it.** The library's own telemetry never carries the prompt or the
  answer, but `UseOpenTelemetry` with sensitive data enabled, or `UseLogging`, on the client you hand it does.
- **No tools.** Every call offers none, whatever `ConfigureChatOptions` sets, and a client with
  `UseFunctionInvocation` in its pipeline is refused.
- **The model writes only free text.** Every bound field is copied from the request, and finalization's binding
  check and screening apply unchanged. Extra members and reasoning content are ignored and never stored.
- **Failure quarantines.** A throw, a timeout (30 s, enforced even on a client that ignores its token), a tool call,
  an oversized or unparseable answer, or an empty lesson throws `ReflectionFailedException`, with no model text in
  it; finalization keeps the record, quarantined.
- **Registration never evicts silently.** It replaces the default reflector only; another registered reflector needs
  `replaceExisting: true`. A scoped `IChatClient` is refused, and a keyed client has no unkeyed fallback.
- **Not deterministic, and it costs a model call** per verified run finalized. Verification never uses a model.
  Captured tool output can steer the lesson's text; your approval boundary still denies any call it induces.
- **Marked, filtered and labelled.** Its reflections are `ReflectionAuthorship.Model` (a model-backed reflector of
  your own must set that itself). Finalization's content guard refuses one carrying a link the run never showed,
  instruction-override phrasing, credential-shaped text or a mixed-script word, but it is a best-effort filter, not a
  boundary: content echoed from the run, a poisoned tool result included, passes by design. The controls to rely on
  are injection's label around every model-written field and `ModelAuthoredLessons = Exclude`, plus your approval
  boundary. Its records written before story 14.3 read as deterministic; see the upgrade note in the guide.

Details: [Model-backed reflection](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/finalization.md#model-backed-reflection).

## More

- Documentation: [guide and glossary](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/README.md)
- Version policy: [compatibility evidence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/compatibility-evidence.md#the-version-policy-floors-and-one-bounded-range)
- Changes between previews: [changelog](https://github.com/fabbrik/AgentExperience.NET/blob/main/CHANGELOG.md)
- License: Apache-2.0
