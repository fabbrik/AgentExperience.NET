# AgentExperience for Go

A Go core of AgentExperience that implements the [language-neutral contract](../contract/README.md), so a lesson
learned by a Go program is stored in the format the .NET and Python libraries read, and the other way round, with a
Microsoft Agent Framework for Go integration.

**Status: preview, contract 1.9, with a Microsoft Agent Framework integration (the `maf` module).** It passes every
fixture of contract 1.9 (and so every 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7 and 1.8 fixture). Every release of this repository tags each module at the
same version as the .NET packages (`v0.1.0-preview.N`), and the Go module proxy serves it from there. Like the .NET
packages, it claims no production readiness, and its API may change between previews. It is Apache-2.0, as the
[repository](../LICENSE) is.

```bash
go get github.com/fabbrik/AgentExperience.NET/go/maf@latest
```

That brings the core (`.../go`) with it; add `.../go/postgres` or `.../go/inmemory` for a store the same way.

## Modules

| Module (in) | Go | Requires | What it is |
| --- | --- | --- | --- |
| `.../go` (`.`) | 1.24 | nothing | The core: capture, finalize, the block, the task text, search and ranking, the verifiers and classifiers, the identity |
| `.../go/postgres` (`postgres/`) | 1.25 | [pgx](https://github.com/jackc/pgx) v5 | The PostgreSQL store |
| `.../go/inmemory` (`inmemory/`) | 1.25 | `golang.org/x/text` | An in-memory store, for development |
| `.../go/maf` (`maf/`) | 1.26 | [Agent Framework for Go](https://github.com/microsoft/agent-framework-go) v0.1.1 | The one-call Agent Framework integration |
| `.../go/postgres/pgtest` (`postgres/pgtest/`) | 1.26 | testcontainers-go, the PostgreSQL store | Test support only: the PostgreSQL server the tests share, and the store's PostgreSQL tests |
| `.../go/telemetry` (`telemetry/`) | 1.26 | the OpenTelemetry API | Optional: reports spans and metrics to OpenTelemetry, as [`docs/telemetry.md`](../docs/telemetry.md#the-python-and-go-ports) documents. Nothing else requires it |

The core's `go.mod` has no `require`, and a test checks that no file of it, tests included, imports anything but the
standard library. Each other module is its own, so a program pays only for what it imports: the stores' Go 1.25 is
what the pgx and `golang.org/x/text` releases without known vulnerabilities need (CI runs govulncheck), and 1.26 is the
Go SDK's own minimum. The PostgreSQL test support is a module of its own, so the store's module requires no container
library: it is on Go 1.26, where the Docker client testcontainers-go brings in has no known vulnerability, and every
module, it included, passes govulncheck with nothing left out. The store's and the integration's tests also use a JSON
Schema validator. Each module requires the others at the release's own version, and also builds them from their
directories in this repository (a `replace`, which a program that imports the module ignores).

## Quick start with Microsoft Agent Framework

Bring `client` (an OpenAI client, or any provider's) and `runTests` (your test run as a function tool, returning text
such as `exit=0`). Development and tests only: see [In production](#in-production). The Go SDK builds an agent from its
configuration once, so the one call wires the configuration before the provider's constructor builds the agent:

```go
store, err := inmemory.New()
if err != nil {
	return err
}
identity, err := ae.DevelopmentIdentity(ae.DevelopmentOptions{}) // one local identity: development and tests only
if err != nil {
	return err
}
config := agent.Config{Name: "triage-ticket", Tools: []tool.Tool{runTests}}
if err := maf.UseAgentExperience(&config, maf.Options{Store: store, Identity: identity,
	Verify: ae.ToolSucceeded("run_tests"), ClassifyToolResult: ae.ExitCode("run_tests")}); err != nil { // your check, never the model
	return err
}
triage := openaiprovider.NewChatCompletionsAgent(client, openaiprovider.AgentConfig{Config: config, Model: model})
if _, err := triage.RunText(ctx, "Ticket #4812: a refund is stuck on a lock. Triage it.").Collect(); err != nil {
	return err
}
_, err = triage.RunText(ctx, "Ticket #4977: a refund is stuck on a lock again. Triage it.").Collect() // gets the lesson
return err
```

[`maf/example_test.go`](maf/example_test.go) holds this snippet as a function, so `go vet` and `go test` keep it
compiling. The second run's model is handed the Historical Reference block, as one user-role message just before the ticket,
which names both of the first run's test runs:

```text
Lesson: Verified after 1 attempt. Worked: attempt 0. Checks: [run_tests].
Tried:
  - attempt 0: run_tests [failed: exit 3], run_tests [returned] → completed
```

[`maf/examples/quickstart`](maf/examples/quickstart/main.go) is based on this snippet and runs it with no key and no
network: `cd maf && go run ./examples/quickstart`. It differs in what a runnable, repeatable demo needs: the model is a
scripted stand-in (a provider `Run` function, wired with the SDK's automatic tool invocation as the OpenAI provider
wires it, which calls `run_tests` until a call returns `exit=0`), the first test run of each ticket returns `exit=3`,
the identity names its application, the recorded environment is fixed so the output is the same on every machine,
and it prints each run and the block. Its output is checked in as
[`quickstart.expected.txt`](maf/examples/quickstart/quickstart.expected.txt), and a test compares them.

### Semantic retrieval: registering an embedder

Without an embedder retrieval is text only, and a reworded query finds nothing it shares no words with. Implement
`ae.Embedder` over any model (none is a dependency of this repository) and set `maf.Options.Embedder`, and retrieval
becomes hybrid:

```go
type myEmbedder struct{ client *myprovider.Client }

func (myEmbedder) ModelID() string { return "my-embedding-model" } // stored with every vector; another model's vectors are never compared
func (myEmbedder) Dimension() int  { return 1536 }                // the vector's length, known without calling the provider

func (e myEmbedder) Embed(ctx context.Context, text string) ([]float32, error) { // exactly Dimension() finite numbers
	return e.client.Embed(ctx, text)
}

err := maf.UseAgentExperience(&config, maf.Options{Store: store, Identity: identity, Verify: verify,
	Embedder: myEmbedder{client}})
```

- **In-memory and PostgreSQL stores.** `inmemory.Store` compares 32-bit floats by cosine distance. `postgres.Store`
  needs PostgreSQL with pgvector, the .NET vectors package's migrations (`0004`, `0020`) and the grants of
  `sql/application-role-privileges-1.6.sql` after the 1.0 and 1.4 files (or the .NET privileges call); it runs the
  contract's two new SQL files as written (`WriteEmbedding`, `SearchVectors`). Both are an `ae.EmbeddingIndex`.
- **Fail-open.** The vector channel never fails or delays past `VectorTimeout` (default 500 ms, covering the embedding
  and the search) the text answer, and at most `maf.MaxVectorCalls` (16) embedder and vector-store calls are in flight at
  once, abandoned ones included, so a hung provider cannot starve the text search; past that a call fails at once. When
  the channel does not contribute the injection answers from text and reports the reason through `OnVectorFallback`
  and `InjectionResult.VectorFallback`: `NotConfigured` (no embedder, or a store that is not an `ae.EmbeddingIndex`),
  `ProviderUnavailable` (the embedder failed, panicked, ran out of time, or returned a blank model ID or an empty,
  wrong-length or non-finite vector; no statement runs), `ModelMismatch` or `DimensionMismatch` (every stored vector is
  of another model or width) and `VectorSearchFailed`. A failure of the text search still fails the injection, as
  without an embedder. `MinVectorRelevance` (0 to 1) drops vector candidates below it; text candidates are never
  dropped.
- **Writing embeddings.** After a record is stored, a goroutine embeds its summary (task ID, task summary and lesson)
  and writes it, only while the record is still at that revision. It never runs inside the record's own commit and
  never fails capture: a failure is reported to `OnCaptureFailure` with the stage `maf.StageIndex` (`"Index"`).
  `maf.WaitForIndexWrites(ctx, &config)` (and `ContextProviderOf(&config).Close(ctx)`) awaits the ones still running.
  Records stored before an embedder was registered have no vector until one is written; the .NET library's re-index
  does it for a shared database.

### See what it learned: the lessons view

A .NET developer can list what the library learned (`AgentExperienceLessons`); so can a Go one (contract 1.7). The view
is read-only and lists the caller's own scope, newest first, under the caller's authorization bounds:

```go
lessons, err := maf.LessonsOf(&config) // after UseAgentExperience; or maf.NewLessons(options), or ae.NewLessons(store, ae.LessonsOptions{...})
if err != nil {
	return err
}
page, err := lessons.ListFor(ctx, identity, ae.LessonQuery{Limit: 20}) // identity from your own authentication
for _, lesson := range page.Lessons {
	fmt.Println(lesson.Status, lesson.Confidence, lesson.IsEligible, lesson.IneligibleReason, lesson.LastInjectedAt)
}
```

- **What a lesson shows.** Its status, confidence, creation and update instants, its checks (check ID, evidence kind
  and result, never the evidence's detail), `BlockText` (the Historical Reference block an agent would be shown for it
  alone, numbered 1, with no ranking and no limit: `ae.RenderAlone`, byte-equal to .NET's), whether retrieval would
  use it and why not, and when it was last injected and how often. Listing lessons discloses them: serve the view only
  behind your own authorization. It renders no HTML and maps no endpoint.
- **The query.** `Statuses` (nil: every status), `Limit` (1 to 500, 50 by default, like .NET) and `IncludeIneligible`
  (nil: true). An invalid limit, or empty or undefined statuses, is an error before anything is read. A scope outside
  the bounds is an empty page with the outcome `Denied`, never an error and never a statement. `HasMore` is set when
  more lessons match, or may (the store answers at most 500 records).
- **Why a lesson is ineligible**, decided in this order: `Status` (not Validated or Reinforced), `BelowConfidenceFloor`
  (below the retrieval minimum, 0.5; a NaN is below it), `ModelAuthored` (the setup's `ExcludeModelAuthored`),
  `OverByteBudget` (its block is larger than `BlockOptions.MaxBytes`) and `Unrenderable` (the block could not be
  rendered; it is listed, and the others still are). .NET's `Expired` and `ContentUnconfirmed` do not exist here: this
  port has no maximum-age policy and no provenance signing, so it never answers them.
- **Injections.** `LastInjectedAt` and `InjectionCount` come from the same read as the page. When the injections table
  cannot be read (not migrated, or the role lacks the 1.4 grants) the page is read without them and says
  `InjectionsTracked` false: a nil `LastInjectedAt` then means "not known", not "never".
- **Stores.** The PostgreSQL store runs the contract's `list-experience-lessons.sql` as written (a bounded read, at
  most 500 records, never a write) and the in-memory store holds the same data; both are an `ae.LessonsReader`.

### What the one call wires

- **Capture**: agent middleware and function invocation middleware, each put first so it wraps everything else on
  the agent (the history and context providers included). Each invocation, streaming or not, is one run of one
  attempt: every tool call with its arguments (sanitized when stored: only allowlisted values are kept, secrets
  redacted; arguments that are not a JSON object are recorded as none and reported), its result (text as it is, a
  `*string` unwrapped, framework text content as its text, anything else, other content included, as its JSON), and
  its error.
  An error a tool returns is recorded by its type's name only (`io/fs.PathError`; `fmt.Errorf` and `errors.Join`
  wrappers are looked through), never its message; a panic by its value's type (`panic(string)`), and then it goes
  on, unchanged. A tool that reports failure in its result is recorded as failed when `ClassifyToolResult` names a
  class for it; a class that is not a single error-class token is recorded as `ToolReportedError`. Calls the SDK runs
  in parallel are kept in the order they started. When the invocation ends, `Verify` decides from your own facts; a
  verdict is finalized through the core (the deterministic lesson, the derived IDs) and stored, its initial event
  recorded at the finalization's time on the integration's `Clock`. Without `Verify` nothing is stored. The run's
  provenance names `agentexperience.maf` and `maf.Version()`, this module's version as the build recorded it
  (`0.0.0-dev` for an untagged build).
- **Injection**: a context provider (`*maf.ContextProvider`). It derives the task text from the invocation's messages
  (the contract's rule), searches the store in the identity's scope, ranks with the contract's weights, renders the
  block with `BlockOptions` (the contract's options and limits), and adds it as one user-role message
  (`MessageRole: message.RoleSystem` sends it as a system message) just before the new input (right after the replayed
  history; never after the input, even when no message of it came from outside the pipeline), stamped
  `AdditionalProperties["AgentExperience.HistoricalReference"] = true` and sourced as the context provider
  `agentexperience`. A configuration that already holds a `*maf.ContextProvider` gets no second one;
  `DisableInjection` captures without injecting.
- **Injection tracking** (contract 1.4). Once a block is handed over, a store that is an `ae.InjectionRecorder` (the
  PostgreSQL and in-memory stores) is told which records it delivered, so the .NET lessons view shows when each lesson
  was last injected, by a Go agent too. It runs in a goroutine on the invocation's values but its own context, bounded
  by `InjectionRecordTimeout` (5 seconds, .NET's), and never delays or fails the invocation; a store call that
  overruns it is abandoned. `OnInjectionRecorded` reports how it ended (`Recorded`, `NotTracked`, `NotRecorded` or
  `OutcomeUnknown`, as .NET's `record_injection` operation), and `maf.ContextProviderOf(&config).Close(ctx)` awaits the
  records still running, for a host shutting down.
- **Semantic retrieval** (contract 1.6), with an embedder you register (below). Injection then runs the text and the
  vector channel together, merges them and ranks as before; each stored record's summary is embedded in the
  background.
- **The injection holdout** (contract 1.8), off by default; see [Measuring benefit](#measuring-benefit-the-holdout).
  The run's model (`Model`, contract 1.9) is recorded for the estimate's split by model; see
  [Reading the estimate](#reading-the-estimate).
- **The task text, and where history sits.** Capture records the task text of the invocation's own input. In the Go
  SDK a session's history provider always runs before the context providers, so injection sees the replayed history,
  then the new input, and derives the task text from both, as .NET's `ChatClientAgent`-level provider does: a request
  shorter than the follow-up threshold (64 characters, "and retry") is searched joined after the previous request.
  The SDK keeps the injected block in the session's history with the request; a replayed block is dropped from the
  request and never counts as one, so each request carries exactly one block.
- **The run's end, as in .NET and Python.** An invocation whose provider returns an error ends its run `Failed` (the
  error is its type's name), one whose context is canceled or past its deadline ends it `Cancelled`
  (`context.Canceled` or `context.DeadlineExceeded`), and one whose caller stops reading its updates early ends it
  `Cancelled` (`agent.RunStopped`); `Verify` still decides whether that run is stored, and the error then reaches the
  caller unchanged. Finalization is bounded by `FinalizationTimeout` (5 seconds, .NET's `FinalizationTimeout`), on its
  own context (the caller's values, never its cancellation): past it the work is abandoned and reported ("the record
  may still be stored": a store write already under way may complete), its context ends, and it stores nothing more
  and calls no `OnFinalized`, so a cancellation is never held up longer than that. A panic through the invocation
  (the provider's, a later middleware's, or one raised in the caller's own loop over the updates, which runs inside
  the iterator) ends its run `Failed`, recorded as `panic(<type>)`, and goes on unchanged.
- **Nothing returns an error or panics into the agent.** A store, identity, classifier or verifier failure (a
  returned error or a panic) means no block or no stored record, reported through `OnCaptureFailure` or
  `OnInjectionFailure` (the reason never quotes record content). Only the invocation's own errors and the tool's own
  errors and panics reach the caller, unchanged. `UseAgentExperience` itself refuses what would fail every run: no
  store, no identity or both `Identity` and `ResolveIdentity`, a sanitization capture would refuse, an environment
  with a blank host, runtime or operating system (or a blank metadata key, or invalid UTF-8), block options the block
  cannot honour; and a second call on one configuration is `maf.ErrAlreadyWired`.

**Verifiers** (in the core) have the semantics of .NET's `ExperienceVerifiers`: `ToolSucceeded(tool)` (the last call
to the tool decides), `Check(id, fn)` (your own check: `true`, `false`, or `nil` for "do not store"),
`Approval(id, fn)` (a person's decision, by an opaque approver ID), `AllOf(...)` (every check; stops at the first
`nil`) and `AnyOf(...)` (the first that passes); `WithArtifactRevision` names the revision. **Classifiers** (in the
core) have the semantics of `ExperienceToolResultClassifiers`: `ExitCode(tools...)` and `HTTPStatus(tools...)` read a
JSON object's top-level members, else the last match in the text, with .NET's `\b`, `\s` and case rules (they run on
the error class's matcher, not Go's RE2). A blank tool name or check ID panics, as an invalid pattern does in
`regexp.MustCompile`.

### Measuring benefit: the holdout

The .NET library can estimate whether its lessons help, by comparing the verified outcomes of runs that were given
them with runs they were withheld from ([Injection: measuring benefit](../docs/guide/injection.md#measuring-benefit-the-holdout)).
A Go agent takes part with `Holdout`:

```go
options := maf.Options{Store: store, Identity: identity, Verify: verify,
	Holdout: maf.HoldoutOptions{Rate: 0.1}} // Salt: ae.DefaultHoldoutSalt when empty
```

- **Off by default.** The zero value (rate 0) changes nothing. A rate that is NaN, below 0 or above 1 is refused by
  `UseAgentExperience`. An empty `Salt` is `ae.DefaultHoldoutSalt` (`agentexperience-holdout-v1`), Go's zero-value
  idiom: where .NET and Python refuse an explicitly empty salt, Go cannot tell one from an unset one.
- **Who takes part.** Only an invocation captured by the same `UseAgentExperience` call whose block names at least
  one record. Its arm is `ae.AssignHoldout(runID, salt, rate)`: SHA-256 over the run ID's bytes and the salt, the
  first 8 bytes over 2^64, held out when below the rate. It is the arm .NET and Python assign the same run
  (the contract's `holdout-assign` fixtures pin it).
- **A held-out invocation** gets no block (it is built first, so what is withheld is exactly what would have been
  delivered): no message, no exposure on the run, no injection record. The `inject` outcome is `Withheld`, and
  `OnWithheld` is told the withheld records (`InjectionResult.WithheldExperienceIDs`). This port has no withdrawal
  notices, so a held-out invocation never owes any.
- **A treatment invocation** is injected exactly as without the holdout.
- **Both arms are recorded** in the background, like injections: through a store that is an `ae.HoldoutRecorder` (the
  PostgreSQL store runs the contract's `sql/record-holdout-assignment.sql`; the in-memory store keeps the rows), bounded
  by `InjectionRecordTimeout`, fail-open, and never delaying the invocation. `OnHoldoutRecorded` reports how it ended
  (`Recorded`, `NotTracked`, `NotRecorded` or `OutcomeUnknown`, the `record_holdout` operation), and `Close` awaits it.
  On PostgreSQL the application role needs the grants of `sql/application-role-privileges-1.8.sql`.

Rows a Go agent writes are read by the .NET estimate unchanged (`AgentExperienceBenefit.EstimateAsync`), together with
the .NET and Python agents' in the same scope, and the Go port reads them too (next section).

### Reading the estimate

`ae.EstimateBenefit` (contract 1.9) is the .NET `AgentExperienceBenefit.EstimateAsync`: it reads the holdout's rows in
exactly one scope (the scope the blocks were built in), each with its run's verified outcome, and compares the arms,
with the same numbers for the same rows ([what it reports](../docs/guide/injection.md#reading-the-estimate)):

```go
estimate, err := ae.EstimateBenefit(ctx, store, identity.Authorization(), identity.Scope,
	ae.BenefitOptions{SplitByModel: true})
// estimate.Overall.Verdict is ae.Helps, ae.Hurts or ae.NoEvidence
```

- **The store** is any `ae.Store`. One that is an `ae.HoldoutOutcomeReader` (the PostgreSQL store, which runs the
  contract's `sql/query-holdout-outcomes.sql`, and the in-memory store) is read 1,000 rows a page, each page folded
  into running counts before the next (`ae.BenefitAccumulator`), so one page is held at a time; any other gives an
  empty estimate with `Tracked` false. A store that pages a run's rows apart is an error (`ae.ErrRunPagedApart`), not a
  run counted twice. The bounds and scope must come from your own authentication; the estimate never writes.
- **A scope outside the bounds** reads nothing and gives an empty estimate whose `Outcome` is `ae.BenefitDenied`,
  never an error.
- **The options.** `MinimumRunsPerArm` is the fewest verified runs each arm needs before a verdict; 0 is the default,
  `ae.DefaultMinimumRunsPerArm` (20), Go's zero-value idiom, and a negative value is an error before anything is read.
  `SplitByModel` adds the per-model comparisons.
- **A sealed record** (`payload_version` 2), which a contract 1.x port cannot read, gives its run no outcome: the run
  counts as `Pending`, exactly as .NET counts a run whose record is sealed under a destroyed key.
- **Rows read any other way** are estimated with `ae.EstimateBenefitFromRows`, pure and order-independent.

The fields map one to one to .NET's `ExperienceBenefitEstimate`:

| .NET | Go |
| --- | --- |
| `ExperienceBenefitEstimate.Overall`, `Lessons`, `ByConsumingModel` | `BenefitEstimate.Overall`, `Lessons`, `ByConsumingModel` (nil unless split) |
| `Tracked`, `Outcome` (`Found`, `Denied`), `MinimumRunsPerArm` | `Tracked`, `Outcome` (`BenefitFound`, `BenefitDenied`), `MinimumRunsPerArm` |
| `ExperienceBenefitComparison.Treatment`, `Holdout` | `BenefitComparison.Treatment`, `Holdout` |
| `Difference`, `Lower`, `Upper` (`double?`) | `Difference`, `Lower`, `Upper` (`*float64`, nil when either arm has no runs) |
| `Verdict` (`NoEvidence`, `Helps`, `Hurts`) | `Verdict` (`ae.NoEvidence`, `ae.Helps`, `ae.Hurts`) |
| `Unverified`, `Pending`, `MixedArm` | `Unverified`, `Pending`, `MixedArm` |
| `ExperienceBenefitArm.Runs`, `Successes`, `Rate` (`double?`) | `BenefitArm.Runs`, `Successes`, `Rate` (`*float64`) |
| `ExperienceLessonBenefit.ExperienceId`, `Comparison`, `ByModelPair` | `LessonBenefit.ExperienceID`, `Comparison`, `ByModelPair` |
| `ExperienceModelBenefit.Model`, `Comparison` | `ModelBenefit.Model`, `Comparison` |
| `ExperienceModelPairBenefit.LessonModel`, `RunModel`, `Comparison` | `ModelPairBenefit.LessonModel`, `RunModel`, `Comparison` |
| `ExperienceBenefitEstimate.UnknownModel` | `ae.UnknownModel` |
| `ExperienceBenefitOptions.MinimumRunsPerArm`, `SplitByModel` | `BenefitOptions.MinimumRunsPerArm`, `SplitByModel` |
| `HoldoutOutcomeRow` (`RunId`, `Arm`, `ExperienceId`, `AssignedAt`, `RunOutcome`, `RunModel`, `LessonModel`) | `HoldoutOutcomeRow` (`RunID`, `Arm`, `ExperienceID`, `AssignedAt`, `RunOutcome`, `RunModel`, `LessonModel`) |
| `ExperienceBenefitAccumulator` | `BenefitAccumulator` |

**The run's model.** Microsoft Agent Framework for Go does not expose the chat client's model on the built agent, so
the host names it: `maf.Options{Model: "gpt-4o"}` (for example the `Model` of `openaiprovider.AgentConfig`). Every
captured run records it, trimmed and capped at 256 UTF-16 code units (`ae.NormalizeModel`), under
`agentexperience.model` (`ae.ModelMetadataKey`) in its environment metadata; nothing when it is empty or blank, and
an `Environment` that already sets the key keeps its own value. It is bookkeeping for the split by model only: the
reflector never lists it as a precondition, the block never shows it, and ranking ignores it.

Acting on the estimate (the benefit policy) and measuring another memory provider stay .NET-only.

### Telemetry: the same spans and metrics as .NET

The optional module `.../go/telemetry` (the OpenTelemetry API only; no SDK, no exporter) reports the operations the port
performs to OpenTelemetry, under the names, dimensions and attributes
[`docs/telemetry.md`](../docs/telemetry.md#the-python-and-go-ports) documents for .NET, so one dashboard serves a
mixed fleet:

```go
import "github.com/fabbrik/AgentExperience.NET/go/telemetry"

disable := telemetry.Enable() // the global providers your application configured; WithTracerProvider and WithMeterProvider override
defer disable()
```

It emits `inject`, `retrieve`, `record_injection`, `record_holdout`, `capture.start_run`, `capture.append_attempt`, `capture.complete_run`,
`verify`, `reflect`, `finalize` and `index` (the background embedding write): the operations this port performs, and no
other. The core and the integration import no OpenTelemetry: they call the observer hook (`ae.SetObserver`, one atomic
load when nothing is registered), and `go/telemetry` is the adapter. A provider, exporter or observer that fails or
panics is recovered and never reaches the agent. Spans parent under the context you pass: `ae.StartRunContext`,
`(*RunCapture).AppendAttemptContext`, `CompleteContext` and `FinalizeContext` take the context (the integration passes
the invocation's); the forms without one report root spans. Attributes carry identifiers and counts only, never record
content, task text, scopes or error messages.

### In production

The identity must come from your own authentication, never from model output: set `ResolveIdentity`, a function of
the invocation's context and an `IdentityContext` (the agent and the session) that returns an `*ae.Identity`
(principal and scope), or `nil` to leave an invocation neither captured nor injected. `ae.DevelopmentIdentity` runs
everything as one fixed caller in one fixed scope, so it refuses (`ae.ErrDevelopmentOnly`) outside Development, Test
or Testing: the `AGENTEXPERIENCE_ENVIRONMENT` variable is read first, then, by the .NET library's rule,
`DOTNET_ENVIRONMENT`, else `ASPNETCORE_ENVIRONMENT`; with none set it is allowed (`EnvironmentName` names your host's
own environment, and it is kept with the identity). `UseAgentExperience` checks a development identity, and a resolver
that returns one is checked on every invocation: outside development the invocation is reported (`ResolveIdentity`)
and runs with no memory. The store is the PostgreSQL store (`postgres.NewStore`), which shares lessons with .NET and
Python agents on the same database.

## What it does

- **Capture and finalize.** `StartRun` returns a `RunCapture` that sanitizes a run as it is recorded: secret-named
  fields are redacted, argument values are kept only when allowlisted, a number a double cannot hold finitely is
  refused, and the default limits apply. `Finalize` aggregates the host's verification, writes the deterministic
  lesson for a verified run, and returns the stored record (`Validated`, or `Quarantined` without a lesson) and its
  initial lifecycle event. The IDs are derived, so a retry derives the same ones.
- **The stored form.** `ExperienceRecord.ToStored` and `FromStored` write and read the record exactly as the
  contract's schema describes it. Tool-call arguments are an `*Object` (members in order) whose numbers are
  `json.Number`, kept exactly as written: `DecodeJSON` never decodes a number into a `float64`. `ReadNumber` reads one
  the way .NET does (an integer that fits in 64 bits as an `int64`, anything else as a `float64`).
- **The injected block.** `RenderBlock` turns ranked records into the Historical Reference block, byte for byte as
  the .NET adapter writes it, in the compact or verbose layout, within the record and byte limits, with record text
  neutralized.
- **The task text.** `DeriveTaskText` derives the text retrieval searches with from an invocation's messages.
- **The error class.** `ErrorClass` reduces a captured error to its short class.
- **Search and ranking.** `SearchParameters` and `AuthorizationBounds` are the contract statements' parameters;
  `Rank` applies the contract's safety checks, truncation and ranking to the rows a search returns.
- **Semantic retrieval** (contract 1.6). `Embedder` is your embedding object; `RetrievalSummary`, `RecordSummary` and
  `ContentHash` are what is embedded and how it is hashed; `EmbeddingWrite` and `VectorQuery` validate before any
  statement; `MergeChannels` and `RankHybrid` turn the text and vector candidates into one ranked list (`Rank` itself is
  unchanged, and ranks every merged candidate, up to 100).
- **The lessons view** (contract 1.7). `NewLessons` over any `LessonsReader`, `Lessons.List` and `ListFor`, `RenderAlone`;
  see [See what it learned](#see-what-it-learned-the-lessons-view).
- **PostgreSQL** (the `postgres` package). `Store` writes a finalized record and its initial event as the application
  role, reads records back, runs the contract's text search, records injections (`RecordInjection`, contract
  1.4) and holdout assignments (`RecordHoldoutAssignment`, contract 1.8), writes and searches embeddings (`WriteEmbedding` and `SearchVectors`, contract 1.6) and reads the lessons view's
  page (`ListLessonRecords`, contract 1.7) and the holdout's outcome rows (`QueryHoldoutOutcomes`, contract 1.9), every transaction first declaring its bounds. The contract's statement files are embedded and run as
  written (a test keeps the copies in `postgres/sql/` identical to `contract/sql/`); only their `@name` placeholders are
  numbered for the driver. The store needs a schema the .NET migrator applied, with row-level security on; it never
  creates or alters a schema object. The application role's privileges are the .NET privileges call's, or the
  contract's two files, in this order: `sql/application-role-privileges.sql`, then
  `sql/application-role-privileges-1.4.sql` (without the second, every injection is still made and its record is
  reported `NotRecorded`), then `sql/application-role-privileges-1.6.sql` and `sql/application-role-privileges-1.8.sql`
  (without the last, the holdout still withholds and each assignment is reported `NotRecorded`). `Search` skips a sealed
  row (`payload_version` 2), and `Get` answers `ErrSealed` for one (and `nil, nil` for none).
  - **Connections are the host's.** `NewStore` takes anything that begins a transaction: a `*pgxpool.Pool`, a
    `*pgx.Conn` or a `pgx.Tx`, each connecting as the application role. `Open` is the convenience that builds a pool
    from a connection string (close it with `Close`). Each operation is one transaction. The store is safe for
    concurrent use exactly when its source is: a pool is, a single connection or transaction is not.

- **The in-memory store** (the `inmemory` package): the same writes, reads and text search in memory, for
  development and tests, with the .NET in-memory store's search (words normalized to Unicode form KC and lower-cased
  as .NET's invariant culture lowers them, terms of at least two characters, the English stopwords PostgreSQL drops,
  the 3/1/1 field weights; a test pins the cases where Go's own lowering differs, and 20,000 random strings gave the
  .NET store's terms), honouring scope and bounds. It copies every record it is given and returns, so a caller never
  shares one with it, and honours a done context. Both stores are an `ae.Store`.
- **Verification and identity for an agent integration.** The verifiers, the tool-result classifiers, the `Identity`
  an invocation runs under and `DevelopmentIdentity` (see [the quick start](#quick-start-with-microsoft-agent-framework)),
  in the core, so they need no Agent Framework type.

It does nothing outside contract 1.x: no grants, crypto-shredding or provenance signing, and no lifecycle
after finalization. It writes no logs at all, so no record content is ever logged.

[`example_test.go`](example_test.go) captures a run, finalizes it and renders the block; `go test` runs it and checks
its output. In short:

```go
capture, _ := ae.StartRun(ae.CapturedRun{RunID: runID, TaskID: "refund-stuck-pending", Scope: scope,
	Environment: environment, Provenance: provenance, StartedAt: start},
	ae.SanitizationAllowing("strategy"), ae.DefaultCaptureLimits())
outcome, _ := capture.AppendAttempt(attempt) // Recorded, SanitizationRejected, CapacityExceeded, ...
_, _ = capture.Complete(ae.RunCompleted, end)
run := capture.Run()

finalized, _ := ae.Finalize(run, ae.Verification{ClosedRound: &round, RequiredChecks: checks, Evidence: evidence,
	CurrentArtifactRevision: "rev-1", FinalizedAt: now})

pool, _ := pgxpool.New(ctx, os.Getenv("AGENTEXPERIENCE_CONNSTRING")) // as the application role
store, _ := postgres.NewStore(pool)
bounds := ae.BoundsOf(scope)
_ = store.InsertFinalized(ctx, bounds, &finalized, "host-principal", nil)
rows, _ := store.Search(ctx, bounds, ae.NewSearchParameters(scope, "refund ticket stuck in pending"))
ranked, _ := ae.Rank(rows, scope, ae.Now(), nil, false)
block, _ := ae.RenderBlock(ranked.Ranked, ae.BlockOptions{})
```

## Where Go differs from .NET, and what the port does

Every rule is .NET's, and Go's standard library differs from .NET in places
([contract 1.1](../contract/README-1.1.md#where-a-ports-own-library-differs-from-net) pins most of them):

- **UTF-16.** A Go string is UTF-8; every length, cut and limit is counted in UTF-16 code units (`UTF16Len`,
  `UTF16Prefix`), and a cut never splits a surrogate pair. Ordinal order is UTF-16 order, not byte order.
- **Lone surrogates.** A Go string cannot hold one. JSON decoding turns one into U+FFFD, which is exactly what .NET's
  UTF-8 encoding writes for it in a derived ID, so the encoding fixture's UTF-16 arrays decode with `unicode/utf16`.
- **Whitespace, words and case.** Go's regexp `\s` is ASCII only (no `\v`, U+0085 or Unicode spaces), its `\b`
  knows only ASCII words, and `strings.EqualFold`, `unicode.ToUpper` and regexp's `(?i)` fold the long s, the
  Kelvin sign or the dotless i to ASCII letters. The port uses .NET's `char.IsWhiteSpace`, `\b` word characters and
  ordinal-ignore-case rules instead (U+001C to U+001F are not whitespace, U+0085 is).
- **The error class** is a .NET regular expression with a Unicode `\b`, which Go's regexp (RE2) cannot express. A
  small backtracking matcher with .NET's leftmost-first priority runs the same pattern, linear in the input.
- **Numbers.** .NET's round-trip (`1E+20`, `-0`) and fixed (`0.00`, 15 significant digits, half away from zero)
  formats are reproduced; a `real` relevance is widened from float32 exactly.
- **Instants and durations** are 100-nanosecond ticks with .NET's range checks, not `time.Time`: every way of making
  an instant (parsing, `InstantFromTicks`, `InstantFromTime`, `Add`) refuses one outside 0001-01-01 to
  9999-12-31T23:59:59.9999999 UTC.
- **Text Go cannot hold.** .NET text is UTF-16, and its only text a Go string cannot hold is a lone surrogate, which
  reaches Go as invalid UTF-8 (WTF-8 writes one as three bytes). The task text drops each invalid byte, as .NET
  drops a lone surrogate; a real U+FFFD is kept.
- **The Unicode version.** Character classes (whitespace, word characters, control and format characters, marks)
  come from the `unicode` package of the Go toolchain that builds the program: Unicode 15.0.0 for Go 1.24 and 1.25,
  17.0.0 for Go 1.27 (`unicode.Version`). .NET's tables follow its own runtime's Unicode version, so a character
  assigned or given another category between the two versions can be classified differently: kept by one and
  neutralized by the other in a block or a task text, or a word character to one error class and not the other. Every
  character the contract's fixtures use is classified the same in all of them, and CI runs the fixtures on Go 1.24 and
  the latest stable.

## What the Go port does not do yet

The port covers contract 1.0 to 1.9: capture, finalize, store, text and semantic retrieval, the injected block,
injection recording, the lessons view, the injection holdout, the benefit estimate and telemetry. These .NET features have no counterpart here, so a mixed
.NET and Go deployment should keep them on the .NET side:

- **Lifecycle after finalization:** reuse feedback, confidence reinforcement, contest and revocation.
- **Sharing:** grants, and so the grant-disclosure choices.
- **Performing erasure and crypto-shredding** (the port never reads an erased record, contract 1.5) (Go also has no at-rest encryption), and **provenance signing** (so no `Expired` or `ContentUnconfirmed` lesson state).
- **Migrations:** the port never creates or alters a schema object; the .NET migrator does.
- **Lessons view HTML and endpoint mapping.**
- **Acting on the benefit estimate** (the benefit policy), measuring another memory provider, and exporting lessons:
  the port reads the estimate, but nothing it injects depends on it.

What the port does write or read follows the same contract the .NET library is tested against.

## Running the tests

From this directory (the core module), and from `postgres/`, `inmemory/` and `maf/` (the other modules):

```bash
gofmt -l . && go vet ./... && go test -race ./...
```

In the core, `go test ./...` runs the unit tests and the conformance runner ([`contract_test.go`](contract_test.go)), which loads
every fixture family from [`../contract/fixtures`](../contract/fixtures) and compares this package's output exactly:
blocks and task text byte for byte, JSON by value, with a number written as an integer never equal to one written
with a fraction or an exponent. It checks every contract file against `contract/lock.json`, and fails with a clear
message if the lock names a contract version this package does not support (1.0 to 1.9 are). The verifier and
classifier tests port the cases of .NET's and Python's. In `postgres/`,
`go test ./...` validates every record of the fixtures, and every record the core writes for a finalize fixture,
against the contract's JSON Schema (there, because it needs a validator and the core module requires none). The
schema's patterns are ECMA-262 regular expressions and the validator
([jsonschema v6](https://github.com/santhosh-tekuri/jsonschema)) evaluates them with Go's RE2: the two agree on every
construct the schema uses except `\s`, so the schema's one `[^\s]` is translated into ECMA-262's whitespace class, and
any other `\s`, `\S` or multiline flag fails the test until it is translated too.

In `maf/`, `go test ./...` drives a real `agent.Agent` built by `agent.New` with a scripted provider and the SDK's own
automatic tool invocation (capture, classification, verification, storage, injection, failures, sessions, streaming,
cancellation, parallel calls), runs the quick start and compares its output
(`AGENTEXPERIENCE_QUICKSTART_GOLDEN_UPDATE=1` rewrites it on purpose), and runs the cross-language proof:

- *Go writes, .NET reads.* A Go agent on a fixed clock and fixed identifiers stores two runs, and the records must be
  exactly [`maf/testdata/cross-language/go-agent-records.json`](maf/testdata/cross-language/go-agent-records.json)
  (checked in memory and against the schema, and read back from PostgreSQL in the PostgreSQL leg). In the PostgreSQL
  leg the rows the Go store wrote, every column it writes in PostgreSQL's own text form, must be exactly
  [`go-agent-rows.json`](maf/testdata/cross-language/go-agent-rows.json). The .NET contract tests'
  `GoAgentRecordsPostgresRoundTripTests` insert those raw rows into a schema the .NET migrator applied, each value
  cast from its text by PostgreSQL, read every record back through the .NET PostgreSQL store (it must be
  `go-agent-records.json`), check each initial event is the one .NET finalization derives, compare every column with
  what the .NET store writes for the same record, read the injection the second run recorded through the .NET
  lessons view and compare its row with what the .NET store records for it, and find the Go agent's lessons through
  the .NET retrieval. `AGENTEXPERIENCE_CROSS_LANGUAGE_UPDATE=1` rewrites both files on purpose (the
  rows in the PostgreSQL leg).
- *.NET writes, Go reads.* The records the .NET library finalized for the contract's finalize fixtures (generated from
  .NET and checked against it on every build) are written into PostgreSQL, and a Go agent on their scope is injected
  with the one its task matches; a quarantined one never, and another agent's scope none. No .NET process runs at test
  time: the fixtures are the .NET-written records.
- *The holdout, Go writes, .NET estimates* (contract 1.8, PostgreSQL leg). A Go agent with the holdout at rate 0.5
  learns a lesson, then runs eight measured runs (three held out, five treated, some verified and some failed); the
  rows it wrote (records, initial events and holdout assignments) must be exactly
  [`go-agent-holdout-rows.json`](maf/testdata/cross-language/go-agent-holdout-rows.json), each measured run's rows in
  the arm `ae.AssignHoldout` gives it. The .NET contract tests insert them, with the Python agent's, into one database
  and run the .NET benefit estimate over the scope.
- *The fleet's estimate, three languages* (contract 1.9, PostgreSQL leg). The Go and Python agents' holdout rows go
  into one database, and `ae.EstimateBenefit` over the scope, split by model, must be exactly
  [`go-fleet-holdout-estimate.json`](maf/testdata/cross-language/go-fleet-holdout-estimate.json); the .NET contract
  tests compare the .NET estimate over the same rows with it, and with the Python port's.

**PostgreSQL** (the `postgres` build tag, in `postgres/pgtest/` and `maf/`, Go 1.26): in `postgres/pgtest/`, each
finalize fixture is written through the Go store and read back row for row, every search fixture is run and ranked,
every record-injection and holdout-record fixture is run (the late-write guard's race staged as the contract says), every embedding-write and vector-search fixture is run, and sealed rows, tombstones, the exact scope, row-level security, `recorded_at` and concurrent use on a pool are
checked; in `maf/`, the cross-language tests run. Both use the server helper the `pgtest` package is (its roles are
made under an advisory lock, so test binaries may share one server). Each test database (dropped when its test ends) is migrated with the .NET migrator's own
scripts from [`src/AgentExperience.Storage.Postgres/Migrations`](../src/AgentExperience.Storage.Postgres/Migrations),
in order, and given the application role's privileges from the contract's
[`sql/application-role-privileges.sql`](../contract/sql/application-role-privileges.sql) then
[`sql/application-role-privileges-1.4.sql`](../contract/sql/application-role-privileges-1.4.sql),
[`sql/application-role-privileges-1.6.sql`](../contract/sql/application-role-privileges-1.6.sql) and
[`sql/application-role-privileges-1.8.sql`](../contract/sql/application-role-privileges-1.8.sql); from contract 1.6 the
vectors package's migrations (`src/AgentExperience.Storage.Postgres.Vectors/Migrations`) follow, after a superuser ran
`CREATE EXTENSION vector`. The server is a testcontainers-go `pgvector/pgvector` container
(`AGENTEXPERIENCE_POSTGRES_MAJOR`, 16 by default), or the server `PGHOST`, `PGPORT`, `PGUSER` and `PGPASSWORD` name (a
superuser, with the pgvector extension available). With neither the tests skip, unless
`AGENTEXPERIENCE_REQUIRE_POSTGRES=1`, when they fail. On a Mac with Rancher Desktop or Colima, testcontainers-go may
need:

```bash
cd maf && TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock TESTCONTAINERS_RYUK_DISABLED=true go test -race -tags postgres ./...
```

CI runs the `go` job for the core on Go 1.24, the core and both stores on Go 1.25 (the store's unit tests), and every
module on Go 1.26 and the latest stable Go (`go mod tidy` leaves each module unchanged, gofmt, go vet, staticcheck and govulncheck on Go 1.26,
and the tests with the race detector), and the PostgreSQL and cross-language tests, also with the race detector, on
PostgreSQL 16 and 18. To run a PostgreSQL leg as CI does, set `AGENTEXPERIENCE_POSTGRES_MAJOR` (16 or 18) and
`AGENTEXPERIENCE_REQUIRE_POSTGRES=1`, and run `go test -race -tags postgres ./...` in `postgres/pgtest/` and in
`maf/`.
