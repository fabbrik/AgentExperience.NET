# AgentExperience.NET — the end-to-end sample

One command, no credentials, no Docker, no network:

```bash
dotnet run --project samples/AgentExperience.Sample.EndToEnd
```

It prints seven stages and exits 0. Run it twice and the two transcripts are byte-identical —
that is what makes the sample assertable in CI, and it is why the clock and the identifier source
are fixtures.

## Prerequisites

- The .NET 10 SDK (the repository pins it in `global.json`). Nothing else.
- No model credentials. No test and no sample in this repository needs one.
- No Docker, no PostgreSQL, unless you opt into the PostgreSQL mode below.

## What the seven stages show

| # | Stage | What it shows |
|---|---|---|
| 1 | `capture` | Run A opens; attempt 1 takes the wrong approach and the check exits non-zero |
| 2 | `capture` | Attempt 2 takes the correct approach and the check exits zero; the run completes |
| 3 | `verify` | Attempt 1's evidence against attempt 2's, and what each verification round the host could have closed would have concluded |
| 4 | `finalize` | The verified run is reflected on and persisted as one Experience Record |
| 5 | `retrieve` | Run B asks the same task; the stored record is found |
| 6 | `inject` | The record is injected into run B as a labelled Historical Reference |
| 7 | `feedback` | The exposure is recorded against run B |

## What is real and what is a fixture

Real, and exercised exactly as a host would exercise it: capture, sanitization (including a secret
tool argument that never reaches the record), the verification aggregator, the default reflector,
the lifecycle service, finalization, retrieval and ranking, the final pre-injection eligibility
re-check, the Historical Reference payload writer, reuse feedback, and — for run B — MAF's
`AIAgentBuilder.UseExperienceCapture(...)` on a real `ChatClientAgent`.

Fixtures, each named as one in its own first documentation line:

- **The model.** `ScriptedChatClient` asks for one tool call and then answers with a fixed
  sentence. It exists so the sample needs no credentials.
- **The clock and the identifiers.** `SteppingTimeProvider` and `DeterministicIds` are wired into
  `ExperienceCaptureOptions.TimeProvider`, `.NewId`, `.Environment`, and
  `ExperienceInjectionOptions.TimeProvider`. A real host leaves all of these at their defaults.
- **The three storage ports**, in the default mode only: `InMemoryRecordStore`,
  `InMemoryCandidateSource`, and `InMemoryReuseFeedbackStore`, under `Doubles/`. They are
  `internal` and they live here rather than in `src/` on purpose — an in-memory record store
  shipped from a published package is an invitation to run one in production. They keep nothing
  across processes.

## Why run A does not use `UseExperienceCapture`

`UseExperienceCapture(...)` records one MAF invocation as one Experience Run with **one** attempt.
Run A is deliberately one run with **two** attempts, which is what the capture contract models for
a second try at the same task: two `AppendAttemptAsync` calls on the same run, before it is
completed. So the sample's host opens run A itself and drives capture directly, and
`AttemptToolRecorder` buffers each attempt's tool calls the way the adapter buffers an
invocation's. Run B is wrapped with `UseExperienceCapture(...)` in the ordinary way, and that is
also where run B's run ID comes from: the session state key `AgentExperience.RunId`, never agent
output.

## Why stage 3 has two verification rounds, and what that costs

Within one closed round, a `Fail` for a required check dominates a `Pass` for the same check — a
later pass never erases an earlier failure inside the round that recorded it. So "failing evidence
and passing evidence, aggregated against one closed round" can never be `Verified`, and the sample
uses two rounds: attempt 1's evidence belongs to the round the host opened for the wrong approach,
attempt 2's to the round the host closed after the fix.

Stage 3 prints three aggregations of the same two pieces of evidence, because they answer three
different questions:

| Closed round the host supplies | Result |
|---|---|
| none | `Unknown` — an unclosed round is **unresolved**, never `Failed` |
| round 1, constructed for the sake of the question | `Failed` — inside one round a `Fail` dominates a later `Pass` |
| round 2, the one the host actually closed | `Verified`, completion score 1.000 |

The middle row is a hypothetical. It is obtained by *constructing* a closed round for round 1's ID,
which is not what the host did. An earlier version of this sample printed "round 1, which the host
never closed" on one line and "that round alone would have been `Failed`" on the next; those two
lines contradict each other, and only the first one described what happened.

**The trust boundary this exposes.** Which round is closed is the host's claim, not the library's
finding. `VerificationAggregator` reads only the round it is handed: it cannot tell a genuine
fix-and-rerun — what this sample is staging — from a host that parks a failure in a round it simply
never closes and then closes a clean one. Nothing in this library can tell those apart, because the
difference is in the host's intent and not in the evidence. Closing a round is an assertion you are
accountable for. What the library does give you is an audit trail: the lifecycle log records every
transition with its actor, its evidence IDs, and its revision, so the claim is reviewable after the
fact even though it cannot be checked at the time.

## Why the reuse feedback claims nothing

Stage 7 prints `Outcome: Recorded | Benefit: Unknown | nothing moved`, and that is the honest
answer. Exposure is not attribution: the run saw the record and the run ended, and nothing in those
two facts attributes one to the other. Only two shapes move a confidence score — an authorized
`HumanReuseAssessment`, or a `ComparativeEvaluationResult` carrying its evidence and its
verification round. The sample performed neither, so it submits neither, and it never populates
`ClaimedBenefit` with anything but `Unknown`.

Resubmitting the same `FeedbackId` with the same content is `AlreadyRecorded`, not a second row:
the feedback ID is the ledger's idempotency key. With different content it is `Conflict` and
nothing is written. The sample does not exercise either; it records once.

## What this sample does not show

- **Anything about model quality.** The model is a scripted fixture, the clock is a fixture, and
  the identifiers are a counter. Nothing here measures what injected experience does to a real
  model's behaviour. That measurement is **story 4.4's** job.
- **Quarantine.** A run whose verification does not resolve to `Verified` is finalized as
  `FinalizationOutcome.Quarantined`: the record is durable, it carries no Reflection, and it is
  never eligible for reuse. Staging one here would add a dead end to the narrative, so the sample
  explains it instead of printing it.
- **Hybrid (vector) retrieval.** Stage 5 runs text-only, which is a first-class supported
  deployment. Adding a vector channel would need either a model credential or a fake embedding
  generator that teaches nothing. See `AgentExperience.Storage.Postgres.Vectors` for the real
  thing.
- **Sharing grants, supersession, and contradiction.** All covered by the library's own test
  projects.
- **Telemetry.** Story 4.1 instruments the loop with an `ActivitySource` and a `Meter`. The sample
  registers no listener and no exporter — it is a library consumer, not a telemetry host — and it
  behaves identically whether or not something is listening. That last part is a test
  (`Sample_renders_the_same_transcript_with_a_telemetry_listener_attached`), not an assurance: it
  holds only because 4.1 measures duration with `Stopwatch.GetTimestamp()` rather than through the
  injected `TimeProvider`, and a one-line change there would break it.

## How the sample is held to all of this

`tests/AgentExperience.Sample.EndToEnd.Tests` runs the sample and then reads its **stores**, not its
printed lines: the Experience Run capture holds, the Experience Record the store returns, the
ledger row reuse feedback wrote, and the run ID the capture middleware put in run B's session state
bag. A transcript line is evidence that the sample said something; only the stores are evidence
that it did it.

The whole rendered transcript is also compared, byte for byte, against
`tests/AgentExperience.Sample.EndToEnd.Tests/GoldenTranscript.txt`, under the invariant, `de-DE`,
and `tr-TR` cultures. Nothing updates that file on its own. When a change to the sample is meant to
change its output, regenerate it on purpose and read the diff:

```bash
AGENTEXPERIENCE_SAMPLE_GOLDEN_UPDATE=1 dotnet test tests/AgentExperience.Sample.EndToEnd.Tests
git diff tests/AgentExperience.Sample.EndToEnd.Tests/GoldenTranscript.txt
```

CI runs the sample twice and diffs the two runs' standard output, so the byte-identical claim on
this page is checked on every push rather than asserted on it.

## The optional PostgreSQL mode

```bash
export AGENTEXPERIENCE_SAMPLE_POSTGRES='Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=agentexperience'
dotnet run --project samples/AgentExperience.Sample.EndToEnd
```

The environment variable is the only branch in the sample. When it holds a connection string, the
sample runs `ExperienceSchemaMigrator.MigrateAsync` first and then registers
`AddAgentExperiencePostgresStore`, `AddAgentExperiencePostgresCandidateSource`, and
`AddAgentExperiencePostgresReuseFeedbackStore` in place of the three doubles. **Every other line of
the sample is shared between the two modes.** The base schema needs no `pgvector` and no superuser.

Whitespace is treated as unset. An unreachable or unusable connection string prints one line naming
the mode and the failure class and exits **1** — the sample never falls back to its in-memory
doubles, because a mode that quietly becomes another mode is a mode that lies.

**Point it at a database this sample has not written to yet.** The identifiers are fixtures, so
every execution asks to finalize the same run ID, and an Experience Record's ID is derived from its
run's. Against a store that actually kept the first execution's record, the second one is an
`AlreadyFinalized` replay — the store behaving exactly as it should. The sample says so and exits 1
rather than narrating a stage 4 that did not happen. Drop the `agentexperience` schema, or use a
throwaway database, between executions. The default in-memory mode has no such constraint: it
starts empty every time, which is why that is the mode the determinism guarantee is about.

The two modes rank candidates differently: the in-memory candidate source scores by word overlap,
and the PostgreSQL one uses full-text search, so the relevance number each computes for the same
record is different. Nothing downstream of the ranking is: with one candidate, both modes rank it
first, and the Historical Reference block is written from the record, so both modes print the same
`byte budget used: 2059`. `SamplePostgresModeTests` asserts exactly that — the PostgreSQL mode's
transcript is compared against the checked-in one line for line, with the header that names the
ports as the only difference.
