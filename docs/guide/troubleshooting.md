# Troubleshooting: why didn't my agent get a lesson?

You ran a task, a relevant lesson exists, and the agent did not get it. This page is the order to check, one
link per step, so you can stop at the first one that explains what you see. The whole model is in
[Concepts in five minutes](concepts.md); here each item is the exact rule that admits or drops a candidate, and
[Telemetry](../telemetry.md) names the signal that tells you which one fired.

Everything below is a normal outcome of a documented rule, not a bug. If you reach the end of the list and
still have nothing, the trace in step 0 is what a maintainer will ask for.

## 0. Read the trace before anything else

Subscribe to the library's own signals and you will see which step dropped the record instead of guessing:

```csharp
.WithTracing(tracing => tracing.AddSource("AgentExperience.*"))
.WithMetrics(metrics => metrics.AddMeter("AgentExperience.*"))
```

Then watch the `retrieve` operation. Its `agentexperience.retrieve.count` instrument and the span
`agentexperience.retrieve` carry the filter and rank outcomes; `agentexperience.omitted` marks what the
injection gate left out, with the reason. The names are in [Telemetry](../telemetry.md#instruments); the
"operation" and "outcome" tags are the two to read first. If `retrieve` never appears at all, the problem is
upstream of retrieval — go to step 4.

## 1. Is the record eligible?

Only a record in **`Validated`** or **`Reinforced`** status is retrieved, injected or indexed. A `Candidate`
that was never reinforced, a `Superseded` record, a `Retracted` one, or one moved to **`Quarantined`** will
never be returned no matter how well it matches. Check the record's lifecycle status first — the transition
table is in [Lifecycle](lifecycle.md), and `Quarantined` in particular is easy to reach by accident and stays
until an operator moves it back.

## 2. Did any check produce evidence?

A reflection only exists for a run whose verification produced evidence, and an experience record is only
created for a run that has one. If your `Verify` callback was never registered, or ran and closed a round with
no evidence — an exit code, a test result, a workflow completion, a human approval — there is nothing to
reflect on and no record to store. This is the most common "no lesson" cause, and it is by design: a
completion with no evidence is not a lesson (see [Finalization](finalization.md)).

## 3. Is the scope right — and is a grant needed?

Matching is **exact**; a null field is not a wildcard. A record stored under one `tenant`/`application`/`project`
is invisible to a caller whose authorization context names a different one, and the optional `team`, `agent` and
`user` fields narrow it further. Two deliberate exceptions exist and are the usual surprises: a null **bound** on
an authorization context leaves that field unrestricted, and a subtree retention sweep treats a null root field as
"any" — neither is retrieval matching. If the record belongs to another scope, it needs an explicit
[sharing grant](sharing.md) and the caller's authorization context must cover the grant. See
[Deployment](deployment.md) for how the host supplies the context.

## 4. Is the task text null, empty, or the wrong text?

Retrieval matches on `RetrieveExperienceRequest.TaskText`, so what your task-text resolver returns decides what
comes back. The usual wiring is to return `context.DerivedTaskText` and `null` to skip injection. A derived text
of `null` means the last request was not usable: only `User` messages whose MAF source is `External` count, and
an image-only turn gives `null` rather than falling back to an older message. If your host stamps its own history
without MAF's `ChatHistory` source, replayed turns can be read as new input. The full derivation is in
[Injection into MAF § The task text](injection.md#the-task-text).

## 5. Do the required attributes and matched terms fit?

A retrieval has required environment attributes that a candidate must carry, **and** a minimum number of
distinct, non-stopword query terms the record must match. A record that is otherwise perfect is excluded if it
lacks a required attribute value, or if your task text has too few meaningful words — a one-word task often
matches nothing. Preferred attributes never exclude; they only grade the ranking. The rules and the ranking
breakdown are in [Retrieval](retrieval.md).

## 6. Is the reuse confidence too low?

Records are ranked by reuse confidence, a score in `[0, 1]` over independent supporting validations `S` and
contradictions `F`. A record a host has contradicted more than supported sits low and can fall below a retrieval
floor the host set. The default formula, and how a host replaces the engine, are in
[Confidence and independence](confidence.md); the score stored with a record names the rule that produced it, so
you can tell a low score from a missing one.

## 7. Did the receiving agent's capabilities gate it out?

Even a retrieved lesson can be omitted at injection time. The block only carries lessons whose approach the
receiving agent can actually act on: its declared available tools and the highest tool risk class it may use. A
lesson that needs a tool the agent does not have, or a risk class above its ceiling, is left out — and
`agentexperience.omitted` records that it was. See [Injection into MAF](injection.md).

## 8. Was the run ever exposed to the record?

Reuse feedback moves a score only for evidence about a record the run was **exposed** to. If a run was never
given the record, a later "that didn't help" cannot lower its confidence, and a "that helped" cannot raise it.
This is deliberate — see [Reuse feedback](reuse-feedback.md) — and shows up as a record whose score never moves
despite feedback. Confirm exposure from the injection step (step 7), not from the feedback call.

## 9. Is the store the one you think it is?

With `AddAgentExperienceInMemoryStorageForDevelopment()`, data is lost when the process ends and none of the
PostgreSQL guarantees apply; the registration also refuses any environment but Development, Test or Testing
unless overridden. If a lesson "disappears" between runs, or a write that logs no error is simply gone, check
which storage the host registered — [Deployment](deployment.md#explicit-wiring). Embedding failures are
separately non-fatal by design: the record is committed and text-searchable, and only vector search is missing
until the embedding is retried ([Indexing](indexing.md)).

## Still stuck

Two things are worth ruling out before filing anything:

- **The library never stores hidden reasoning.** If the "lesson" you expected was the model's own explanation
  rather than an observable fact, it was never a candidate. See the design principles in
  [the guide index](README.md#design-principles).
- **At least one path genuinely does not exist yet.** The per-session cloud listing has no documented endpoint;
  see [Known limits](../known-limits.md) for what is out of scope rather than broken.
