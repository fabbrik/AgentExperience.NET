# AgentExperience.MicrosoftAgentFramework

> **Preview — not production ready.** This is a `0.1.0-preview` package, and it claims no production readiness.
> Public APIs may change between previews. Read
> [Known limits and documented boundaries](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/known-limits.md)
> before you rely on it.

The Microsoft Agent Framework (MAF) adapter for [AgentExperience.NET](https://github.com/fabbrik/AgentExperience.NET).
It does two independent things; use either, or both:

- **Capture.** `UseExperienceCapture` records every MAF invocation — ordinary, streaming, failed, cancelled, or a
  stream the consumer stopped reading — and its tool calls as a sanitized Experience Run. It can finalize each run
  into a durable Experience Record, and record retries as attempts of one run.
- **Injection.** `ExperienceContextProvider` retrieves applicable past experience before each invocation and adds it
  as one delimited, labeled **Historical Reference** message.

Requires `Microsoft.Agents.AI` **1.22.0 or any later 1.x** (declared `[1.22.0, 2.0.0)`), for `net8.0`, `net9.0` and
`net10.0`. CI tests the floor and the newest 1.x on every change and on a weekly schedule. MAF 2.0 and later are
outside the range, and NuGet warns (NU1608) when a host resolves one. Brings in `AgentExperience.Core`.

## Capture

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

Call `UseExperienceCapture` first on the builder so capture is the outermost layer. What to know:

- **Capture never changes what the caller sees.** Responses, streaming updates and exceptions are exactly what the
  agent would produce without it, and capture failures are reported through `OnCaptureFailure`, never thrown.
- **Everything goes through the capture service's sanitizer and limits.** Errors are recorded as the exception's
  type name only, never its message.
- **Tool calls need a `ChatClientAgent`** (`CaptureToolCalls`, on by default). Other `AIAgent` types are captured
  with `CaptureToolCalls = false`.
- **Finalization is opt-in.** Set `FinalizationService` and `ResolveFinalization`, and each completed run is turned
  into a durable record inside `FinalizationTimeout` (5 s by default). That adds caller-visible latency; leave it
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
        ResolveRequest = context => new RetrieveExperienceRequest(
            Authorization: hostAuthorization,      // host-established; nothing in the invocation may widen it
            Scope: hostScope,
            TaskText: context.Messages
                .LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))?.Text
                ?? fallbackTaskDescription),        // never Last(): the list can be empty
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
- **Limits drop whole records**: 8 records and 16 KB per block, re-checked within 2 s, by default.
- **Reused sessions are tracked by default**: at most 32 records and 64 KB per session, no revision twice, and a
  fixed withdrawal notice when a record the session was given stops being valid. The notice is advisory: the earlier
  block stays in the history (the KL-12 boundary). `SessionLimits = null` turns tracking off; two providers on one
  agent need different `SessionStateKey`s.
- **It never throws into the invocation**, except for the caller's own cancellation.
- **With capture on too**, it records on the captured run which records it delivered, which is what lets confidence
  evidence about that run count.

The payload format, argument allowlists, grant disclosure levels, the session rules, and failure behaviour are in
[Injection into MAF](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/injection.md).

## More

- Documentation: [guide and glossary](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/guide/README.md)
- Version policy: [compatibility evidence](https://github.com/fabbrik/AgentExperience.NET/blob/main/docs/compatibility-evidence.md#the-version-policy-floors-and-one-bounded-range)
- Changes between previews: [changelog](https://github.com/fabbrik/AgentExperience.NET/blob/main/CHANGELOG.md)
- License: Apache-2.0
