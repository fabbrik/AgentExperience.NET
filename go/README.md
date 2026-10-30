# AgentExperience for Go

A Go core of AgentExperience that implements the [language-neutral contract](../contract/README.md), so a lesson
learned by a Go program is stored in the format the .NET and Python libraries read, and the other way round, with a
Microsoft Agent Framework for Go integration.

**Status: preview, contract 1.17, with a Microsoft Agent Framework integration (the `maf` module).** It passes every
fixture of contract 1.17 (and so every 1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 1.10, 1.11, 1.12, 1.13, 1.14, 1.15 and 1.16 fixture). Every release of this repository tags each module at the
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
  behind your own authorization. `ae.RenderLessonsHTML(page)` renders a page as .NET's lessons page, and
  `ae.LessonsHTMLHandler` serves it ([below](#show-it-the-lessons-page)).
- **The query.** `Statuses` (nil: every status), `Limit` (1 to 500, 50 by default, like .NET) and `IncludeIneligible`
  (nil: true). An invalid limit, or empty or undefined statuses, is an error before anything is read. A scope outside
  the bounds is an empty page with the outcome `Denied`, never an error and never a statement. `HasMore` is set when
  more lessons match, or may (the store answers at most 500 records).
- **Why a lesson is ineligible**, decided in this order: `Status` (not Validated or Reinforced), `BelowConfidenceFloor`
  (below the retrieval policy's `MinimumConfidence`, 0.5 by default, which the reason names; a NaN is below it),
  `ModelAuthored` (the setup's `ExcludeModelAuthored`), `Expired` (contract 1.12: its last activity is more than the
  retrieval policy's `MaxAge` before the page's clock), `OverByteBudget` (its block is larger than
  `BlockOptions.MaxBytes`) and `Unrenderable` (the block could not be rendered; it is listed, and the others still
  are). The view takes the setup's `RetrievalPolicy` (`ae.LessonsOptions.RetrievalPolicy`). .NET's
  `ContentUnconfirmed` does not exist here: this port has no provenance signing, so it never answers it.
- **Injections.** `LastInjectedAt` and `InjectionCount` come from the same read as the page. When the injections table
  cannot be read (not migrated, or the role lacks the 1.4 grants) the page is read without them and says
  `InjectionsTracked` false: a nil `LastInjectedAt` then means "not known", not "never".
- **Stores.** The PostgreSQL store runs the contract's `list-experience-lessons.sql` as written (a bounded read, at
  most 500 records, never a write) and the in-memory store holds the same data; both are an `ae.LessonsReader`.

### Export what helps: the Markdown sink

Contract 1.14. `Lessons.ExportMeasured` hands a sink every lesson in the identity's scope that the
[holdout](#measuring-benefit-the-holdout) measured as helping (.NET's `ExportMeasuredAsync`): eligible, its outcome
`Verified`, its verdict `Helps` in one estimate over the benefit policy's window, sorted by experience ID. It is a sync,
not a dump: the sink gets the whole current set every time, an empty one included, and removes what is no longer in
it. The built-in `ae.MarkdownLessonFileSink` keeps one Markdown file an agent loads (from `AGENTS.md`, `CLAUDE.md` or
a skill folder), byte for byte the file .NET's `MarkdownLessonFileSink` writes for the same lessons:

```go
lessons, _ := maf.LessonsOf(&config)
sink, err := ae.NewMarkdownLessonFileSink("docs/agent-lessons.md", nil) // nil: the system clock
if err != nil {
	return err
}
result, err := lessons.ExportMeasured(ctx, identity, sink, nil) // nil: the benefit policy's minimum runs per arm
// result.Lessons, result.Sync (Added, Updated, Removed), result.HoldoutTracked
```

- **The file** names the scope and the export instant, then each lesson's revision, measured benefit and text (the
  verbose block's lesson lines, `ae.RenderLessonText`). Everything drawn from a record is an indented code block, so no
  lesson can add a heading, a link or an instruction outside it. It is written to a temporary file beside it and
  renamed over it, so a reader sees the old file or the new one; the directory must exist. The file is created as
  .NET creates one, `0666` less the umask (`0644` under the usual one), never owner-only. The previous file is read as
  .NET reads it: a leading byte order mark is dropped, and a heading's GUID may have white space around it.
- **The path is yours.** A blank one is refused and the rest made absolute; as in .NET it is confined to nothing, so a
  path from configuration you do not trust is yours to check.
- **Your own sink** implements `ae.LessonSink` (`Sync(ctx, scope, current)`): another memory store, an instruction
  file, a skill folder.
- **Refused:** a scope with more reusable lessons than one read answers (500), so a sink is never handed part of the set.
  A scope outside the bounds is a result with the outcome `Denied`, and the sink is not called. A `MinimumRunsPerArm` of
  0 is the default (20), as everywhere in this port; a negative one is refused.

### Show it: the lessons page

`ae.RenderLessonsHTML(page)` is a lessons page as one self-contained HTML document (inline CSS, no script, no external
request, light and dark mode), byte for byte .NET's `ExperienceLessonsHtml.Render`, every value HTML-encoded as .NET's
`WebUtility.HtmlEncode` encodes it. `ae.LessonsHTMLHandler` is an `http.Handler` that lists the caller's scope and
serves it with exactly the headers .NET's documented endpoint sets: `Content-Type: text/html; charset=utf-8`,
`Cache-Control: no-store`, `X-Frame-Options: DENY` and a `frame-ancestors 'none'` content security policy. Like that
endpoint (`MapGet`) it answers `GET` only (any other method, `HEAD` included, is 405 with `Allow: GET`); no identity is
403 and a failure 500, neither with a body. **Authorization is yours:** `Identity` resolves the caller from your own authentication (never from the
request's query or body; `nil` answers 403), and the page discloses every lesson in the scope:

```go
mux.Handle("/agent-experience/lessons", requireAuth(ae.LessonsHTMLHandler{Lessons: lessons,
	Identity: func(r *http.Request) (*ae.Identity, error) { return myIdentityFrom(r) }}))
```

### A model writes the lesson: the chat reflector

Contract 1.14. By default the lesson is the deterministic reflector's and never calls a model. Opt in to .NET's
`ChatClientExperienceReflector` with `maf.NewChatClientReflector` and `Options.Reflector`:

```go
reflectorAgent := openaiprovider.NewChatCompletionsAgent(client, openaiprovider.AgentConfig{Model: "gpt-4o-mini"})
reflector, err := maf.NewChatClientReflector(maf.AgentChatClient(reflectorAgent,
	func(o *maf.ChatOptions) []agent.Option { // the Go SDK has no provider-neutral model, temperature or output cap
		params := openai.ChatCompletionNewParams{MaxCompletionTokens: openai.Int(int64(o.MaxOutputTokens))}
		if o.Temperature != nil {
			params.Temperature = openai.Float(float64(*o.Temperature))
		}
		return []agent.Option{openaiprovider.ChatCompletionNewParams(params)}
	}), maf.ChatClientReflectorOptions{ModelName: "gpt-4o-mini"})
if err != nil {
	return err
}
err = maf.UseAgentExperience(&config, maf.Options{Store: store, Identity: identity, Verify: verify,
	Reflector: reflector, FinalizationTimeout: 45 * time.Second})
```

It sends the published `maf.SystemPrompt` and the data message .NET sends (the same bytes: `BuildPrompt` shows it), asks
for the same JSON schema with no tools and a tool mode of none, reads the answer as strictly as .NET does, copies every
bound field from the request, and returns a reflection marked model-authored. Finalization then screens it with the
**content guard** (credential-shaped text, mixed-script words, instruction-override phrasing, `data:`/`javascript:`
links and UNC paths, and any URL, hostname or IP address the run did not show the model), and the block fences it as
model-authored. A failure is a `*maf.ReflectionFailedError` with .NET's kind and fixed message; the record is then
stored `Quarantined` with no lesson, as in .NET (`FinalizedExperience.ReflectionFailure`; a refusal is
`ScreeningRefusal`, an `*ae.ScreeningRefusalError` with .NET's code and reason; a binding check or a screening that
panics is refused `Unreadable`, naming only the panic value's type, as .NET's backstop). The guard bounds its work on
each text to one second, as .NET bounds each match: a run content text not read in time admits nothing, and a field not
checked in time is refused as "text that could not be checked in time". It emits no telemetry of its own.
Privacy is .NET's: sanitized captured run content leaves your process for your model provider.

| .NET | Go |
| --- | --- |
| `ChatClientExperienceReflector` | `maf.ChatClientReflector` (`maf.NewChatClientReflector`) |
| `IChatClient` | `maf.ChatClient`; `maf.AgentChatClient(*agent.Agent, provider)` adapts a MAF agent |
| `ChatOptions` (`ModelId`, `Temperature`, `MaxOutputTokens`, `Tools`, `ToolMode`, `ResponseFormat`) | `maf.ChatOptions` (the same, and `AgentOptions` for provider settings) |
| `ChatClientExperienceReflectorOptions` | `maf.ChatClientReflectorOptions`; zero values are the defaults (30 s, 500, 16,000, 2,048, temperature 0); `NoTemperature` is .NET's `Temperature = null` |
| `ConfigureChatOptions` (an `Action`) | `ConfigureChatOptions func(*ChatOptions) error`: an error or a panic is `ChatOptionsCallbackFailed` |
| `ReflectionFailedException` (`Kind`, `CauseType`) | `*maf.ReflectionFailedError` (`Kind`, `CauseType`: the error's or panic's Go type, never its message) |
| `GetReflectedRunContent`, `IReflectionRunContent` | `ReflectedRunContent`, `ae.ReflectionRunContent` |
| `IExperienceReflector`, `ReflectionRequest`, `EnsureMatches` | `ae.Reflector`, `ae.ReflectionRequest`, `EnsureMatches` |
| `AddAgentExperienceChatClientReflector` | `maf.Options.Reflector` (or `ae.FinalizeContextWith` and `ae.FinalizeOptions.Reflector`) |
| `ReflectionScreeningRefusal` | `ae.ScreeningRefusalCode` (`ae.ScreeningRefusalOf`) |

Where Go differs: a function-invoking pipeline is refused only when the client says so (`maf.FunctionInvokingClient`),
because a MAF agent does not expose its tools; give the reflector an agent with no tools. The response's model ID is
read from the provider's raw response (`Model`), which the OpenAI provider sets. The call runs inside the one call's
`FinalizationTimeout` (5 s by default), so raise it above the reflector's own `Timeout`. The content guard needs Unicode
NFKC, which the core module does not carry: the `maf` module registers it (`ae.SetUnicodeNormalizer`) when it is
imported; a host screening model-authored reflections without `maf` registers `norm.NFKC.String` itself, or every
model-authored reflection is refused as text that cannot be normalized.

### Computing confidence

Contract 1.15. The core computes how a record's reuse confidence moves when evidence about reusing it arrives, exactly
as .NET's confidence engine does. It only computes: nothing here reads or writes a store (the lifecycle service below
computes the same update and commits it).

```go
rule := ae.DefaultConfidenceEngineRule() // (1 + S) / (2 + S + F), recorded as "1.0.0"
update, err := rule.Apply(record, ae.ConfidenceEvidence{
	EvidenceID: evidenceID, Kind: ae.EvidenceSupporting, Source: ae.EvidenceMachine,
	RunID: runID, VerificationRoundID: &roundID,
})
// update.NewReuseConfidence is 0.75 for a freshly validated record (S 1 -> 2).
next := ae.StatusAfter(record.Status, ae.EvidenceContradicting) // Contested for a Validated or Reinforced record
key, err := ae.IndependenceKeyFor(update)                        // machine:<run>:<round>

type bayes struct{} // a host engine, as .NET's IExperienceConfidenceEngine

func (bayes) RuleID() string      { return "bayes" }
func (bayes) RuleVersion() string { return "2.1" }
func (bayes) Score(in ae.ExperienceConfidenceInput) (float64, error) {
	s, f := float64(in.SupportingValidations), float64(in.Contradictions)
	return (2 + s) / (4 + s + f), nil
}

hosted, err := ae.NewConfidenceEngineRule(bayes{}, ae.ConfidenceEngineParameter) // records "bayes/2.1"
```

- **The default rule** (`ConfidenceScore`) is a heuristic for ranking, never a probability, strictly inside (0, 1).
  `ApplyConfidenceEvidence` is .NET's `ReuseConfidenceHeuristic.Apply`: the counters after one piece of evidence,
  refusing a negative stored counter or one at 2,147,483,647 (`MaxEvidenceCounter`) that would overflow .NET's.
  `AcceptsEvidenceIn` is `Validated`, `Reinforced` or `Contested`.
- **A host engine** (`ConfidenceEngine`) is checked once by `NewConfidenceEngineRule`: each identity 1 to 64
  characters from `[A-Za-z0-9._-]`, never the default's `reuse-heuristic`. `rule.Apply` asks it for the score of the
  record as read with the counters after the evidence; a score that is NaN, infinite or outside [0, 1] is a
  `*ConfidenceEngineError` with .NET's message, never clamped, and an error the engine returns is passed on unchanged.
  Refusals are `*ConfidenceArgumentError`, carrying .NET's exception type and parameter name.
- **Assessment tokens.** `NewAssessmentTokenIssuer(ae.IndependenceOptions{AssessmentTokenKey: key})` mints the token a
  human assessment carries (`Issue(reviewer, scope, runID, kind, experienceIDs)`, with the reviewer's `ae.Identity`),
  byte for byte .NET's format: call it from your review flow, never from code an agent drives. The key is at least 32
  bytes; the lifetime a day by default (also when zero), at most 30 days. `IssuedAssessmentToken` and
  `IndependenceOptions` keep the token and the key out of `String` and `GoString` (so `%v`, `%+v`, `%s`, `%#v`), and
  the key out of `encoding/json` (`json:"-"`); other verbs and your own copies are yours to keep private. The
  reviewer's authorization is `Identity.Authorization()`: with nil `Bounds` it is the identity's own scope, so a
  reviewer issuing for another scope needs `Bounds` that permit it. Checking a token is unexported, as in .NET.

### Moving a stored lesson through its lifecycle

Contract 1.16. `ae.LifecycleService` is .NET's `ExperienceLifecycleService` over either store (both are an
`ae.LifecycleStore`): it reinforces, contests, marks stale, supersedes and revokes a stored lesson, applies reuse
evidence to it, and reads its confidence back, writing the same events, evidence rows, counters and revisions .NET
writes, so a .NET, a Python and a Go writer can share one database.

> **By default the Go port records evidence without counting it.** Until it verifies independence (story 27.7),
> everything it admits is host-trusted, so the zero `LifecycleOptions` is `HostTrustedEvidence: ae.HostTrustedRecordedOnly`:
> each piece of evidence is committed on an event that moves the record's revision and nothing else (no counter, score,
> status or `updated_at`). Counting it, as .NET does under `TrustHostSuppliedIdentifiers` by default, is an explicit
> opt-in: `ae.LifecycleOptions{HostTrustedEvidence: ae.HostTrustedCounted}`.

```go
lifecycle, err := ae.NewLifecycleService(store, ae.LifecycleOptions{}) // the default engine, evidence recorded only
authorization := ae.AuthorizationContext{PrincipalID: "governance-review", Bounds: ae.BoundsOf(scope)}

validated := ae.StatusValidated
moved, err := lifecycle.Commit(ctx, authorization, ae.CommitLifecycleTransitionRequest{
	EventID: eventID, ExperienceID: record.ExperienceID, Scope: scope,
	PriorStatus: &validated, CurrentStatus: ae.StatusReinforced,
	Reason: "Confirmed by the weekly review.", Producer: "governance-review/1.0",
	OccurredAt: ae.Now(), ExpectedRevision: record.Revision,
}) // Committed at record.Revision+1, or StaleRevision with the revision to re-decide against, TransitionNotAllowed, ...

applied, err := lifecycle.ApplyEvidence(ctx, authorization, ae.ApplyConfidenceEvidenceRequest{
	EventID: evidenceEventID, ExperienceID: record.ExperienceID, Scope: scope, EvidenceID: evidenceID,
	Kind: ae.EvidenceContradicting, Source: ae.EvidenceMachine, RunID: runID, VerificationRoundID: &roundID,
	Reason: "The lesson was reused and the checks failed.", Producer: "verification-aggregator/1.0",
	OccurredAt: ae.Now(),
}) // Applied (applied.Counted() false when recorded only, or for an observation already counted), Ineligible, ...

report, err := lifecycle.ReadConfidence(ctx, authorization, scope, record.ExperienceID, ae.FilterAll)
```

- **Every write is a revision compare-and-set** in the store: writers deciding against one revision race, exactly one
  commits, and the others are `StaleRevision`. A resubmitted event ID or evidence ID with the same content replays the
  original outcome; with other content it is `Conflict`. Refusals are outcomes, with .NET's errors and reasons; an
  error is an infrastructure failure (or `ae.ErrStoreContract` for a store that broke its contract), and an engine's
  failure is returned as it is in `rule.Apply` (above), with nothing written. `Commit` and `ApplyEvidence` are the
  `lifecycle.commit` and `confidence.apply` operations to the observer hook.
- **Independence is not verified** (story 27.7 brings the verifier): the service runs as .NET's does under
  `IndependenceVerification.TrustHostSuppliedIdentifiers`. Only the own-run rule is checked (`Unverified`, `OwnRun`);
  every other identifier is taken as given, an assessment token is neither checked nor spent, and every update is
  admitted **`HostTrusted`** and stored so. A .NET reader therefore sees Go evidence for what it is: `ReadConfidence`
  with `FilterExcludeHostTrusted` or `FilterVerifiedOnly` leaves it out, and `FilterAll` adds recorded-only evidence
  back, once per independence key.
- **What it does not do yet:** no embedding is removed when a commit takes a record out of eligibility (story 27.6d;
  a vector search already excludes it by status), no provenance signing or verification
  (27.7). A sealed record (`payload_version` 2), which a port cannot open, is `NotFound` to the read, the history and
  the commit.
- **The stores.** The in-memory store (`inmemory.New`) keeps a full event log per record (a finalized record's initial
  event first, at revision 1), the evidence ledger and its key sets, and stamps its `Clock` (`SetClock`) on each
  commit. The PostgreSQL store runs [`contract/sql/lifecycle.sql`](../contract/sql/lifecycle.sql) as written, each
  commit in one `READ COMMITTED` transaction, with the 1.0 privileges only. On a pool or a connection the level is
  pinned; given your own transaction (a `pgx.Tx`), the commit runs in a savepoint inside it and refuses
  (`postgres.ErrHostIsolation`) unless that transaction is `READ COMMITTED`, because under a snapshot isolation a lost
  race would re-read your stale snapshot and never see the revision move. A record sealed while a commit waits for its
  row is still `NotFound`: the seal is checked only once the commit holds the row lock; `SetClock` sets the clock it stamps. `GetHistory` pages a history by
  applied revision, and `CheckSupersession` asks the supersession rules before a commit decides them again.

### Recording what reuse was worth: reuse feedback

Contract 1.17. `ae.ReuseFeedbackService` is .NET's `ExperienceReuseFeedbackService`: after a run that was given stored
lessons, `Record` writes what the run saw, how it came out and what you measured to the feedback ledger, and, only when
the submission carries attribution this library accepts (an `ae.HumanReuseAssessment`, whose reviewer is the
authorization's principal, or an `ae.ComparativeEvaluationResult` with its round and evidence), applies evidence about
each attributed lesson through the lifecycle service: improvement supports, harm contradicts (and contests).

```go
ledger := inmemory.NewReuseFeedbackLedger() // or the PostgreSQL store, which is a ledger too
feedback, err := ae.NewReuseFeedbackService(ledger, lifecycle)
result, err := feedback.Record(ctx, authorization, ae.ExperienceReuseFeedback{
	FeedbackID: feedbackID,                       // the whole submission's idempotency key
	RunID: runID, Scope: scope,                   // the run the lessons were injected into
	ExposedExperienceIDs: injectedIDs,
	RunOutcome: ae.VerificationVerified,
	Measure: &ae.ReuseMeasure{Kind: "tool-calls", Value: 7},
	ObservedAt: observedAt,                        // part of the evidence's identity: keep it for a retry
}) // Recorded, benefit Unknown: exposure alone moves nothing
```

- **Exposure is not attribution.** Without an attribution every exposure is `ExposureOnly`; `ClaimedBenefit` is stored
  and never acted on. An attribution that fails its evidence requirements is dropped (the exposure is still recorded,
  with a reason); a structurally incoherent submission is `Invalid` and writes nothing. Every rule, path and message is
  .NET's, in .NET's order.
- **The ledger is written first; each record is submitted on its own.** A store failure or a confidence engine's
  failure (`ae.IsConfidenceEngineFailure`) fails that record (`Failed`, retryable) and the others are still submitted;
  a cancelled context once the ledger is written marks the rest `Failed`, retryable. Resubmitting the same feedback is
  `AlreadyRecorded` and converges on the derived IDs (`ae.FeedbackEvidenceIDFor`, `ae.FeedbackEventIDFor`). A ledger
  failure is returned and nothing is recorded. `Record` is the `reuse_feedback` operation (`feedback_id`, `run_id`),
  and the `confidence.apply` it runs is nested under it.
- **The ledgers.** `inmemory.NewReuseFeedbackLedger` is .NET's in-memory ledger: independent of the record store,
  checking nothing about the exposed records. The PostgreSQL `Store` is also an `ae.ReuseFeedbackStore`, running
  [`contract/sql/reuse-feedback.sql`](../contract/sql/reuse-feedback.sql) as written in one `READ COMMITTED`
  transaction, with the 1.0 privileges; it refuses an exposure to a record erased in the submission's scope. Its
  `recorded_at` is the clock `SetClock` sets.
- **Not verified, and recorded only by default.** As with `ApplyEvidence`, no attribution's run, round or assessment
  token is checked (story 27.7), so the evidence is admitted `HostTrusted`, and under the lifecycle's default
  (`RecordedOnly`) it moves nothing but the revision: the exposure says so. Count it with `ae.HostTrustedCounted` only
  if you trust the identifiers you pass. The port holds no key store, so the rationale is stored in plaintext; a
  submission a .NET ledger sealed (`(sealed)`) is a `Conflict` when the port resubmits it.

**Finalization's reuse evidence** (.NET's `ExperienceFinalizationOptions.ReuseEvidence` and `ContradictOnFailure`):
`Finalize` is a pure function here, so the step is a call of its own. Once the finalized record is stored, and again
with the stored record when a run turns out to be finalized already:

```go
results, truncated, err := ae.ApplyReuseEvidence(ctx, lifecycle, authorization, finalized.Record,
	ae.ReuseEvidenceOptions{Mode: ae.ReuseEvidenceSameTask, ContradictOnFailure: false})
```

It submits machine evidence, bound to the round finalization closed, about each lesson the run was given on its own
task (supporting for a verified run, contradicting for a failed one under `ContradictOnFailure`), skips the others with
.NET's reasons, and reports every refusal and error rather than returning it; a repeat replays the originals and counts
nothing twice. The zero options are `Off`. Its time budget is your context's deadline (.NET's `ReuseEvidenceTimeout`
has no counterpart), it is not inside the `finalize` operation (so no `reuse_evidence.submitted`), and the `maf`
module does not run it for you yet: call it after the record is stored (in `OnFinalized`, for example).

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
with the same counts and verdicts for the same rows, and every rate, difference and bound equal within 1e-12 ([what it reports](../docs/guide/injection.md#reading-the-estimate)):

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
  `ae.DefaultMinimumRunsPerArm` (20), Go's zero-value idiom (so, unlike .NET and Python, an explicit 0 cannot be refused), and a negative value is an error
  before anything is read.
  `SplitByModel` adds the per-model comparisons.
- **A sealed record** (`payload_version` 2), which a contract 1.x port cannot read, gives its run no outcome: the run
  counts as `Pending`, exactly as .NET counts a run whose record is sealed under a destroyed key. A sealed lesson names
  no model either, so in an encrypted store, where .NET opens sealed records, its model reads as `unknown` here and the
  per-model pairs (`ByModelPair`) can differ from the .NET estimate's.
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

### Acting on the estimate: the benefit policy

From contract 1.10 a Go agent acts on the estimate as .NET's `ExperienceBenefitPolicy` does
([Injection: acting on the estimate](../docs/guide/injection.md#acting-on-the-estimate)):

```go
options := maf.Options{Store: store, Identity: identity,
	Benefit: ae.BenefitPolicy{Enabled: true, PreferMeasuredHelpful: true}}
```

- **Off by default.** The zero value reads, caches, reorders and omits nothing.
- **A lesson measured `Hurts` is omitted** before the record limit, as `ae.OmittedMeasuredHarmful`, with .NET's detail
  (`ae.MeasuredHarmfulDetail`), so the next record takes its slot. **A scope measured `Hurts`** gets no block: every
  record is omitted (`ae.ScopeMeasuredHarmfulDetail`), and the `inject` outcome is `NothingToInject`.
  `InjectionResult.Omitted` lists every omission with its reason and detail, in .NET's order: `MeasuredHarmful`,
  `ModelAuthored`, `OverRecordLimit`, then `OverByteBudget`.
- **`PreferMeasuredHelpful`** moves the lessons measured `Helps` ahead of the others, each group keeping its rank order.
- **`PerModel`** acts on the invoking agent's model's verdicts (the captured run's recorded model, else `Options.Model`),
  falling back to the pooled verdict wherever that model's group has no evidence or the model was never measured.
- **Never on the request path.** The estimate is cached per scope, principal and bounds. An invocation uses what is
  cached (nothing yet: no effect) and, when it is missing or older than `RefreshInterval`, starts one refresh in a
  goroutine for that key; concurrent invocations never start a second. A refresh is bounded by `RefreshTimeout` (on the
  wall clock, through its context, and abandoned if the store ignores it); one that fails or overruns keeps the previous
  view, and a failed one waits out the interval too. Each is reported as `benefit_refresh` (`Refreshed`,
  `NotRefreshed`, `OutcomeUnknown`). `ContextProvider.WaitBenefitRefreshes(ctx)` awaits the refreshes started, and
  `Close` does too. At most 10,000 keys are kept (`maf.MaxBenefitCacheEntries`), dropping the one refreshed longest ago.
- **Only recent evidence counts**: assignments within `Window` of the refresh, on the setup's `Clock`
  (`ae.EstimateBenefitAssignedFrom`). An estimate not tracked or denied is never acted on.
- **The holdout is unchanged**: a lesson omitted as harmful is not in the block, so it is neither delivered nor withheld.
- **The options** and their defaults are .NET's: `Window` 30 days, `MinimumRunsPerArm` 20, `RefreshInterval` 10
  minutes, `RefreshTimeout` 30 seconds (at most a day). Go cannot tell an unset value from an explicit zero, so a zero
  one means its default (where .NET refuses an explicit zero); a negative one is refused by `UseAgentExperience`, even
  with the policy off.
- **The lessons view** shows what was measured: `ae.LessonQuery{IncludeMeasuredBenefit: true}` sets each lesson's
  `MeasuredBenefit` (an `ae.LessonBenefit`, nil when no assigned run in the window named it) from one estimate over the
  policy's `Window` and `MinimumRunsPerArm` (split by model with `PerModel`), whether or not the policy is on. A store
  failure on that read fails the list (a PostgreSQL error, telemetry class `Infrastructure`, as .NET's
  `ExperienceStoreException`).
- **Without the agent**, `ae.NewBenefitViews(estimate).For(model)` is what an estimate means for one invocation, and
  `ae.SelectCandidates` the selection; contract 1.10's `benefit-select` fixtures pin both.

| .NET | Go |
| --- | --- |
| `ExperienceBenefitPolicy.Enabled`, `PreferMeasuredHelpful`, `PerModel` | `ae.BenefitPolicy.Enabled`, `PreferMeasuredHelpful`, `PerModel` |
| `Window`, `MinimumRunsPerArm`, `RefreshInterval`, `RefreshTimeout` | `Window`, `MinimumRunsPerArm`, `RefreshInterval`, `RefreshTimeout` (0: the default) |
| `ExperienceInjectionOptions.Benefit` | `maf.Options.Benefit` |
| `InjectionOmissionReason.MeasuredHarmful` (and the others) | `ae.OmittedMeasuredHarmful`, `OmittedModelAuthored`, `OmittedOverRecordLimit`, `OmittedOverByteBudget` |
| `ExperienceInjectionResult.Omitted` | `maf.InjectionResult.Omitted` (`[]ae.OmittedRecord`) |
| `ExperienceLessonsQuery.IncludeMeasuredBenefit`, `ExperienceLesson.MeasuredBenefit` | `ae.LessonQuery.IncludeMeasuredBenefit`, `ae.Lesson.MeasuredBenefit` |

### Measuring another memory provider

From contract 1.11 ([what it adds](../contract/README-1.11.md)) a Go agent can measure a memory provider that is not
this library (Mem0, Zep, Letta, your own: any `agent.ContextProvider`), as .NET's `UseMeasuredMemory` does: on a
deterministic share of captured runs whose provider added something, what it added is withheld, each run's arm is
recorded under a key you choose, and `ae.EstimateProviderBenefit` compares the arms' verified outcomes.

```go
if err := maf.UseAgentExperience(&config, options); err != nil { ... }
// mem0 is whatever agent.ContextProvider your Mem0 integration gives you; no Mem0 module is needed here.
memory, err := maf.UseMeasuredMemory(&config, mem0, "mem0", maf.MeasuredMemoryOptions{Holdout: &maf.HoldoutOptions{Rate: 0.1}})

// Later, behind your own authorization:
estimate, err := ae.EstimateProviderBenefit(ctx, store, bounds, scope, "mem0", ae.BenefitOptions{})
fmt.Println(estimate.Overall.Verdict, estimate.Overall.Treatment.Rate, estimate.Overall.Holdout.Rate)
```

- **Call `UseAgentExperience` first**: `UseMeasuredMemory` takes that call's store, clock, `InjectionRecordTimeout`
  and, with no `Holdout` of its own, the injection holdout's rate and salt (off by default). It replaces the provider
  where it is in `config.ContextProviders`, or appends itself.
- **The wrapped provider always runs**, `Invoking` and `Invoked`, in both arms; its errors are returned unchanged and
  nothing is recorded for them. It is handed clones of the invocation's slices; a held-out invocation gets exactly the
  ones the wrapper was handed. Only a run this agent captures (with the identity capture resolved; Go has no
  `authorize` callback, since a captured run always has one) whose provider added a message, a tool or a new non-empty
  instruction takes part. At rate 0 it is a pass-through.
- **The arm** is `ae.AssignProviderHoldout(runID, key, salt, rate)`, over the salt `ae.ProviderHoldoutSalt(key, salt)`
  (`"{len}:{key}/{salt}"`). The key is 1 to 100 characters of ASCII letters, digits, `.`, `_` and `-`
  (`ae.ValidProviderKey`); anything else is an error. An empty salt means the default.
- **Recording** is in a goroutine, within `InjectionRecordTimeout`, never delaying or failing the invocation, reported
  as `record_provider_holdout` and through `OnRecorded` (`maf.ProviderHoldoutRecordResult`, always on a goroutine of its own); the
  wrapper's `Close` waits for the records, and so does `maf.ContextProviderOf(config).Close` (with `DisableInjection`
  there is no such provider: close the wrapper itself), `AbandonedRecords` counts writes that overran. A panic while inspecting the provider's answer delivers that
  answer and reports `NotRecorded` (`Unexpected`).
- **Stores:** the in-memory and PostgreSQL stores are `ae.ProviderHoldoutRecorder` and
  `ae.ProviderHoldoutOutcomeReader`; another store reports `NotTracked` and estimates with `Tracked` false. On
  PostgreSQL apply [`sql/application-role-privileges-1.11.sql`](../contract/sql/application-role-privileges-1.11.sql)
  after the other privileges files; without it every assignment is `NotRecorded`.

| .NET | Go |
| --- | --- |
| `ExperienceHoldoutContextProvider`, `UseMeasuredMemory` | `maf.MeasuredMemory`, `maf.UseMeasuredMemory` |
| `AgentExperienceBenefit.EstimateProviderAsync` | `ae.EstimateProviderBenefit` |
| `ExperienceProviderBenefitEstimate.ProviderKey`, `Overall`, `MinimumRunsPerArm`, `Tracked`, `Outcome` | `ae.ProviderBenefitEstimate.ProviderKey`, `Overall`, `MinimumRunsPerArm`, `Tracked`, `Outcome` |
| `ExperienceBenefitOptions.MinimumRunsPerArm` | `ae.BenefitOptions.MinimumRunsPerArm` (0: the default; negative is an error) |
| `ProviderHoldoutAssignment.IsValidProviderKey` | `ae.ValidProviderKey` |

### The retrieval policy

From contract 1.12 ([what it adds](../contract/README-1.12.md)) a Go agent takes .NET's `RetrievalPolicy` and
`ConfidenceDecayPolicy`, with their defaults and validation, so it ranks exactly as a .NET agent configured the same
way:

```go
month := 30 * 24 * time.Hour
err := maf.UseAgentExperience(&config, maf.Options{
	// ...
	RetrievalPolicy: ae.RetrievalPolicy{MaxAge: 180 * 24 * time.Hour, RecencyHalfLife: 14 * 24 * time.Hour},
	ConfidenceDecay: &ae.ConfidenceDecayPolicy{
		HalfLives: map[string]*time.Duration{"framework-api": &month, "math": nil}, // math never decays
	},
})
```

| .NET | Go | Default | Notes |
| --- | --- | --- | --- |
| `RetrievalPolicy.Timeout` | `ae.RetrievalPolicy.Timeout` | 500 ms | at most one day; bounds the text search and the vector channel together |
| `MinimumConfidence` | `MinimumConfidence` (`*float64`) | 0.5 | the search's floor, on stored confidence |
| `MaxAge` | `MaxAge` | none (0) | excludes a record whose `UpdatedAt` is more than this long ago (`Expired`) |
| `RecencyHalfLife` | `RecencyHalfLife` | 30 days | the `Recency` component |
| `CandidateLimit` | `CandidateLimit` | 50 | 1 to 199; the store is asked for one more, to detect truncation |
| `MaxAbandonedSearches` | `MaxAbandonedSearches` | 16 | per `UseAgentExperience` configuration (its context provider) |
| `MinimumMatchedTerms` | `MinimumMatchedTerms` | 3 | the text search's |
| `MinimumVectorRelevance` | `MinimumVectorRelevance` (`*float64`) | none | `Options.MinVectorRelevance`, when set, replaces it (as .NET's `MinimumSemanticSimilarity` does) |
| `ConfidenceDecayPolicy.DomainKey` | `ae.ConfidenceDecayPolicy.DomainKey` | `domain` | |
| `HalfLives` | `HalfLives` (`map[string]*time.Duration`) | empty | a nil half-life never decays; matched ordinally; copied |
| `DefaultHalfLife` | `DefaultHalfLife` (`*time.Duration`) | none | for an unlisted domain or a record with none |
| `ArgumentOutOfRangeException`, `ArgumentException` | `*ae.PolicyError` (its `Option` is the .NET name) | | returned by `UseAgentExperience`, `Validate`, `Resolved` and `RankWith` |
| `RetrievalOutcome` (`Completed`, `TimedOut`, `Denied`, `Failed`) | `ae.RetrievalOutcome` (`ae.RetrievalCompleted`, ...) | | the `retrieve` operation's outcomes |
| `RetrievalExclusionReason` (`IneligibleStatus`, `ModelAuthored`, `Expired`) | `ae.ExclusionReason` (`ae.ExclusionIneligibleStatus`, ...) | | `RankedResult.Excluded`, `InjectionResult.Excluded` |
| `RankingComponent.UndecayedValue` | `ae.RankingComponent.UndecayedValue` (`*float64`) | | the stored confidence when a half-life applied |

- **Go's zero values.** Go cannot tell an unset field from an explicit zero, so a zero `Timeout`, `RecencyHalfLife`,
  `CandidateLimit`, `MaxAbandonedSearches` or `MinimumMatchedTerms` means its default, a zero `MaxAge` means no expiry
  and an empty `DomainKey` means `domain` (.NET refuses each zero). A negative value, and every other value .NET
  refuses (a timeout over a day, a candidate limit over 199, a confidence outside 0 to 1 or NaN, a blank domain key or
  domain, a zero or negative half-life in `HalfLives` or `DefaultHalfLife`), is refused, naming the .NET option. The
  zero `RetrievalPolicy` is .NET's `RetrievalPolicy.Default`, and ranks exactly as before 1.12.
- **Decay never excludes.** It only reorders: the confidence floor is the search's, on stored confidence. In the
  verbose block the `Confidence:` line shows the stored value and the applicability line the decayed one, as .NET's.
- **Timing out.** Past `Timeout` the invocation gets no block, its inject outcome is `RetrievalTimedOut` (retrieve:
  `TimedOut`) and nothing is reported as a failure. The search is abandoned, its context cancelled, and counted until
  it returns; while `MaxAbandonedSearches` are still running, an invocation is `RetrievalTimedOut` at once without
  searching, and `OnInjectionFailure` is told the reason, which names the cap. `Timeout` bounds both channels
  together, as .NET's does: a vector channel still running at `Timeout` times the call out, even when the text search
  had answered. `VectorTimeout` is a fallback (the text answer stands, `ProviderUnavailable` or `VectorSearchFailed`)
  only when it is strictly shorter than `Timeout`. A store that fails at or after the deadline is a timeout too.
- **The caller's cancellation** propagates: the context provider's `Invoking` returns `ctx.Err()`
  (`context.Canceled`), so the agent's run ends with it, as .NET's `OperationCanceledException` does; the `inject` and
  `retrieve` operations are reported `Faulted` with the error class `Cancelled`, and nothing is told
  `OnInjectionFailure`. Every other failure still leaves the invocation running without a block.
- **Denied.** A request scope the identity's bounds do not permit is `RetrievalDenied` (retrieve: `Denied`), and
  nothing is searched or embedded.
- **Without the agent:** `RetrievalPolicy.SearchParameters` gives the store's parameters (its floor, `CandidateLimit +
  1`, its minimum of matched terms), and `ae.RankWith` and `ae.RankHybridWith` rank under `ae.RankOptions{Policy,
  Decay, ExcludeModelAuthored}`; `Rank` and `RankHybrid` are the zero policy.

### The injection options

From contract 1.13 ([what it adds](../contract/README-1.13.md)) a Go agent takes the rest of .NET's
`ExperienceInjectionOptions` that needs no session tracking, with their defaults and validation: the receiving agent's
capability gate, the model-authored policy, and the host's two callbacks. A Go agent configured like a .NET one injects
the same block with the same omissions:

```go
low := ae.RiskLow
err := maf.UseAgentExperience(&config, maf.Options{
	// ...
	ReceivingAgent: &ae.ReceivingAgent{
		AvailableTools:  []string{"read_ledger", "wait_for_lock", "retry_refund"},
		MaxRiskClass:    &low,
		ToolRiskClasses: map[string]ae.ToolRiskClass{"read_ledger": ae.RiskLow, "retry_refund": ae.RiskMedium},
	},
	ModelAuthoredLessons: ae.ModelAuthoredExclude,
	ResolveRequest: func(ctx context.Context, rc maf.RequestContext) (*maf.InjectionRequest, error) {
		if !rc.HasDerivedTaskText {
			return nil, nil // skip this invocation
		}
		return &maf.InjectionRequest{TaskText: rc.DerivedTaskText, CorrelationID: traceID(ctx)}, nil
	},
	DecideInjection: func(d maf.DecisionContext) maf.InjectionDecision {
		if d.ModelAuthored {
			return maf.Deny("risk policy")
		}
		return maf.Permit()
	},
})
```

| .NET | Go | Default | Notes |
| --- | --- | --- | --- |
| `ExperienceInjectionOptions.ReceivingAgent` | `maf.Options.ReceivingAgent` (`*ae.ReceivingAgent`) | none | validated and copied by `UseAgentExperience` |
| `ReceivingAgentCapabilities.AvailableTools` | `AvailableTools` (`[]string`) | none | nil skips the tool check; a non-nil empty slice admits only approaches that call no tool |
| `MaxRiskClass` | `MaxRiskClass` (`*ae.ToolRiskClass`) | none | `ae.RiskCritical` disables the risk check |
| `ToolRiskClasses` | `ToolRiskClasses` (`map[string]ae.ToolRiskClass`) | empty | a tool not listed is `Critical` |
| `UseRunTools` | `UseRunTools` | false | the tools the SDK hands the context providers: the configuration's `Tools` and the run's `agent.WithTool` options |
| `GatedRetrievalWindowMultiplier` | `GatedRetrievalWindowMultiplier` | 4 | |
| `ToolRiskClass` (`Low` ... `Critical`) | `ae.ToolRiskClass` (`ae.RiskLow` ... `ae.RiskCritical`) | | `ae.ParseToolRiskClass` and `String` use .NET's names |
| `ModelAuthoredLessons` (`ModelAuthoredLessonPolicy`) | `ModelAuthoredLessons` (`ae.ModelAuthoredLessonPolicy`) | `Include` | `ExcludeModelAuthored: true` is the same as `Exclude` (either one excludes) |
| `ResolveRequest`, `ResolveRequestAsync` | `ResolveRequest func(ctx, maf.RequestContext) (*maf.InjectionRequest, error)` | none | nil retrieves the derived task text |
| `ExperienceInjectionContext` (`Messages`, `Session`, `Agent`, `DerivedTaskText`) | `maf.RequestContext` (`Messages`, `Session`, `Agent`, `DerivedTaskText` with `HasDerivedTaskText`, `Identity`) | | |
| `RetrieveExperienceRequest` | `maf.InjectionRequest` (`TaskText`, `PreferredEnvironment`, `Limit *int`, `ExcludeModelAuthored`, `CorrelationID`) | | the authorization and scope are the identity's; a nil `Limit` is none |
| `DecideInjection` | `DecideInjection func(maf.DecisionContext) maf.InjectionDecision` | none (permit) | |
| `ExperienceInjectionDecisionContext` (`Candidate`, `Current`, `ModelAuthored`) | `maf.DecisionContext` (`Candidate`, `Current`, `ModelAuthored`) | | `Current` is the ranked record: the port re-reads nothing |
| `InjectionDecision.Permit`, `Deny(reason)` | `maf.Permit()`, `maf.Deny(reason)` | | |
| `InjectionOmissionReason.ToolUnavailable`, `RiskClassExceeded`, `HostDenied` | `ae.OmittedToolUnavailable`, `ae.OmittedRiskClassExceeded`, `ae.OmittedHostDenied` | | in `InjectionResult.Omitted` |
| `ApproachArguments`, `FailureDetail`, `Rendering`, `Limits.MaxRecords`, `Limits.MaxBytes` | `ae.BlockOptions` | | as before (contract 1.0) |
| `MessageRole` | `MessageRole` (`message.RoleUser`, `message.RoleSystem`) | user | as before |

- **The gate** checks the tools of the record's approach (the verified final attempt its `Worked:` line names), the
  tool check first (`ToolUnavailable`), then the risk check (`RiskClassExceeded`), with no detail, so no tool name
  leaves the provider. It runs before the record limit, after the model-authored exclusion: a gated record takes no
  slot. While it can reject and a request names its own `Limit`, retrieval is cut to the wider window
  `min(CandidateLimit, max(Limit, MaxRecords × GatedRetrievalWindowMultiplier))`; selection still uses `Limit`.
- **The resolver.** A nil request with a nil error skips the invocation (inject outcome `Skipped`); an error or a panic
  is `Failed` (and `OnInjectionFailure`); `context.Canceled` or `context.DeadlineExceeded` while the caller's context
  is done propagates from `Invoking`, as .NET's `OperationCanceledException` does; a request retrieval refuses (a
  blank task text, one over 4,096 UTF-16 code units, a `Limit` outside 1..`CandidateLimit`, 0 included) is
  `RetrievalFailed`. A request's `CorrelationID` is the inject span's `agentexperience.correlation_id`. No timeout is
  put around it.
- **Every attempt is reported.** `OnInjected` is told every invocation's outcome, as .NET's `OnContextInjected` is:
  `Injected`, `NothingToInject` and `Withheld` (to `OnWithheld`) as before, and now also `Skipped` (no identity, or a
  nil request), `Failed`, `RetrievalFailed`, `RetrievalTimedOut` and `RetrievalDenied`, with no omission (the inject
  span's `agentexperience.omitted_count` is 0); `OnInjectionFailure` still says why one failed. Unlike .NET's
  `ExperienceInjectionResult`, `InjectionResult` has no `CorrelationId` (it is on the span) and no `Failure` (it goes
  to `OnInjectionFailure`).
- **The byte budget.** `BlockOptions.MaxBytes` must exceed the verbose block's fixed header and footer,
  `ae.BlockOverheadBytes` (754), as .NET's `ExperienceInjectionLimits` requires: `UseAgentExperience` refuses less (0
  is the default, 16 KB).
- **The decision** is asked about each selected record, in order, before the block is rendered. It can only deny, and
  it fails closed: the zero `InjectionDecision` denies (as .NET's `null` does), and so does a panic. A denial is
  `HostDenied` with the reason as its detail (`ae.HostDeniedDetail` for a decision that gave none; on a panic
  `The host injection decision threw <type>, so the record was denied.`). A denied record has taken its slot, and its
  omission comes after the selection's and before the byte budget's.
- **Go's zero values.** A zero `GatedRetrievalWindowMultiplier` means 4 (.NET refuses 0); a negative one, a blank tool
  name in either collection and an undefined risk class are refused by `UseAgentExperience`. An empty
  `ModelAuthoredLessons` is `Include`. `Deny("")` reports the empty reason, as .NET does; a decision literal with an
  empty `Reason`, like the zero value, reports `ae.HostDeniedDetail`.
- **Without the agent:** `ae.NewCapabilityGate`, `CapabilityGate.ForInvocation`, `ae.GatedWindow`,
  `ae.SelectOptions.Gate` and `ae.ApplyHostDecisions` give the selection and the decision the agent runs (a gate with
  `UseRunTools` must be narrowed with `ForInvocation`: checked directly it fails closed, as though no tool were
  offered); contract 1.13's `inject-options` fixtures pin it, with the
  block, the omissions and the outcome.

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

It emits `inject`, `retrieve`, `record_injection`, `record_holdout`, `benefit_refresh`, `capture.start_run`, `capture.append_attempt`, `capture.complete_run`,
`verify`, `reflect`, `finalize`, `index` (the background embedding write), `lifecycle.commit` and `confidence.apply`:
the operations this port performs, and no other. The core and the integration import no OpenTelemetry: they call the observer hook (`ae.SetObserver`, one atomic
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
  `Rank` applies the contract's safety checks, truncation and ranking to the rows a search returns, and `RankWith`
  does so under a retrieval policy and a confidence decay (contract 1.12), reporting each exclusion.
- **Semantic retrieval** (contract 1.6). `Embedder` is your embedding object; `RetrievalSummary`, `RecordSummary` and
  `ContentHash` are what is embedded and how it is hashed; `EmbeddingWrite` and `VectorQuery` validate before any
  statement; `MergeChannels` and `RankHybrid` turn the text and vector candidates into one ranked list: every merged candidate
  is scored, and the ranked list is cut to the candidate limit (50 by default; contract 1.12's cut, as .NET's).
- **The lessons view** (contract 1.7). `NewLessons` over any `LessonsReader`, `Lessons.List` and `ListFor`, `RenderAlone`;
  see [See what it learned](#see-what-it-learned-the-lessons-view). Contract 1.14: `RenderLessonsHTML` and
  `LessonsHTMLHandler`, and `ExportMeasured` with `MarkdownLessonFileSink` (or your own `LessonSink`).
- **The reflector seam** (contract 1.14). `FinalizeContextWith` reflects with an `ae.Reflector` (the deterministic one
  by default), binds what it returns (`ReflectionRequest.EnsureMatches`) and screens it (`ScreenWith`), a
  model-authored reflection with the content guard.
- **PostgreSQL** (the `postgres` package). `Store` writes a finalized record and its initial event as the application
  role, reads records back, runs the contract's text search, records injections (`RecordInjection`, contract
  1.4) and holdout assignments (`RecordHoldoutAssignment`, contract 1.8), writes and searches embeddings (`WriteEmbedding` and `SearchVectors`, contract 1.6) and reads the lessons view's
  page (`ListLessonRecords`, contract 1.7) and the holdout's outcome rows (`QueryHoldoutOutcomes`, contract 1.9),
  commits lifecycle events and reads their history (contract 1.16), and records reuse feedback (contract 1.17), every transaction first declaring its bounds. The contract's statement files are embedded and run as
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

It does nothing outside contract 1.x: no grants, crypto-shredding or provenance signing. It writes no logs at all, so no record content is ever logged.

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
- **The content guard's patterns** (contract 1.14) are .NET regular expressions with lookarounds and atomic groups,
  which RE2 cannot express; hand-written scanners apply them as .NET does, per UTF-16 code unit, so a character outside
  the Basic Multilingual Plane is neither a letter, a digit nor a mark to them, and lower case is .NET's invariant one
  (which leaves U+0130 as it is). Its NFKC is `golang.org/x/text`'s, registered by the `maf` module.
- **Strict JSON.** `encoding/json` matches member names ignoring case and accepts a duplicated member; the chat
  reflector reads the model's answer as .NET's source-generated reader does instead: exact names, a known member at
  most once, at most 64 levels deep, and no lone surrogate escape in a string it reads.

## What the Go port does not do yet

The port covers contract 1.0 to 1.17: capture, finalize, store, text and semantic retrieval under .NET's retrieval
policy (expiry, confidence decay by domain, the timeout and abandoned-search cap), the injected block and the injection
options (the receiving agent, the model-authored policy, the request resolver and the injection decision),
injection recording, the lessons view and its HTML page, exporting measured lessons, the model-backed reflector and its
content guard, computing confidence (the default rule, a host engine and assessment tokens), the lifecycle of a
stored lesson (transitions, applying evidence and reading confidence back), reuse feedback and finalization's reuse
evidence, the injection holdout,
the benefit estimate, the benefit policy, measuring another memory provider and
telemetry. These .NET features have no counterpart here, so a mixed
.NET and Go deployment should keep them on the .NET side:

- **The rest of the lifecycle:** removing an ineligible record's embedding (27.6d), and verifying independence (the
  port admits evidence `HostTrusted`, and checks no reuse-feedback attribution; 27.7). The `maf` module does not run
  finalization's reuse evidence on its own (.NET's `AgentExperienceOptions.ReuseEvidence`): call
  `ae.ApplyReuseEvidence` after the record is stored.
- **Session tracking and withdrawal notices** (.NET's `SessionLimits`, `SessionStateKey`; story 27.6), and the final
  eligibility re-read before injection, with its `Limits.EligibilityCheckTimeout` and `Limits.MaxAbandonedReads`
  (story 27.6): the port injects the records as retrieval ranked them, and `DecisionContext.Current` is the ranked
  record.
- **Sharing:** grants, and so the grant-disclosure choices, and the decision context's `SharedByGrant`,
  `PermittingGrantId`, `GrantDisclosure` and `GrantApproachArguments` (story 27.8).
- **Performing erasure and crypto-shredding** (the port never reads an erased record, contract 1.5) (Go also has no at-rest encryption), and **provenance signing** (so no `ContentUnconfirmed` lesson state, and no `UnconfirmedContent` omission; story 27.7).
- **Migrations:** the port never creates or alters a schema object; the .NET migrator does.
- **Required environment attributes** (.NET's `RequiredEnvironmentAttributes`, and so the `EnvironmentMismatch`
  exclusion): the port only grades preferred attributes, and `maf.InjectionRequest` has no such field.

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
message if the lock names a contract version this package does not support (1.0 to 1.17 are). The verifier and
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
- *The benefit policy, .NET writes, Go acts* (contract 1.10, PostgreSQL leg). The .NET contract tests write a fleet in
  which one lesson measures `Hurts` and check in its rows
  ([`dotnet-benefit-fleet-rows.json`](../tests/AgentExperience.Contract.Tests/cross-language/dotnet-benefit-fleet-rows.json));
  they go into a database, and a Go agent with the benefit policy on omits that lesson as `MeasuredHarmful` once its
  cache is warm, and injects the other.
- *The retrieval policy, three languages* (contract 1.12, PostgreSQL leg, in `postgres/pgtest/`). The .NET contract
  tests check in a seed, a non-default policy (an expiry and per-domain decay) and what .NET retrieved
  ([`dotnet-retrieval-policy.json`](../tests/AgentExperience.Contract.Tests/cross-language/dotnet-retrieval-policy.json));
  the seed goes into a database through the Go store, which searches it with the policy's parameters, and
  `ae.RankWith` must give the same ordered IDs, exclusions and scores, within 1e-12.
- *The injection options, three languages* (contract 1.13, PostgreSQL leg, in `maf/`). The .NET contract tests check
  in a seed, two agent configurations (a receiving agent and a model-authored policy) and what a .NET agent injected
  with each ([`dotnet-inject-options.json`](../tests/AgentExperience.Contract.Tests/cross-language/dotnet-inject-options.json));
  the seed goes into a database through the Go store, and a Go agent with each configuration, run on the same message
  at the same instant, must inject the same block, byte for byte, with the same outcome, role, IDs, byte count,
  omissions and exclusions.
- *Another memory provider, three languages* (contract 1.11). A Go agent wraps a stub provider under `stub-memory` at
  rate 0.5 and runs eight finalized runs; in memory its runs, key and arms must be the rows file's, and in the
  PostgreSQL leg the rows it wrote must be exactly
  [`go-agent-provider-rows.json`](maf/testdata/cross-language/go-agent-provider-rows.json). Then the .NET, Python and
  Go agents' rows files go into one database, every run must be in the arm `ae.AssignProviderHoldout` gives it, and
  `ae.EstimateProviderBenefit` must be exactly
  [`go-provider-fleet-estimate.json`](maf/testdata/cross-language/go-provider-fleet-estimate.json), which the .NET
  contract tests compare with the .NET estimate over the same rows.
- *Exporting measured lessons, .NET writes, Go writes the same file* (contract 1.14). The .NET contract tests export
  each `lesson-export` seed through the .NET lessons view to the .NET Markdown sink; the in-memory module's tests
  export the same seeds through `ExportMeasured` to `ae.MarkdownLessonFileSink`, and the `maf` tests export one through
  a Go agent's one-call setup: the files must be .NET's, byte for byte.
- *The model-backed reflector, .NET finalizes, Go stores the same record* (contract 1.14). Each `reflection-output`
  case is a run the .NET finalization stored with its model-backed reflector over a scripted answer; the `maf` tests
  capture and finalize the same run with `maf.ChatClientReflector` over a scripted client giving the same answer, and
  the stored record and initial event must be .NET's, with the same failure kind or screening refusal.

**PostgreSQL** (the `postgres` build tag, in `postgres/pgtest/` and `maf/`, Go 1.26): in `postgres/pgtest/`, each
finalize fixture is written through the Go store and read back row for row, every search fixture is run and ranked,
every record-injection, holdout-record and provider-holdout-record fixture is run (the late-write guard's race staged as the contract says), every embedding-write and vector-search fixture is run, and sealed rows, tombstones, the exact scope, row-level security, `recorded_at` and concurrent use on a pool are
checked; in `maf/`, the cross-language tests run. Both use the server helper the `pgtest` package is (its roles are
made under an advisory lock, so test binaries may share one server). Each test database (dropped when its test ends) is migrated with the .NET migrator's own
scripts from [`src/AgentExperience.Storage.Postgres/Migrations`](../src/AgentExperience.Storage.Postgres/Migrations),
in order, and given the application role's privileges from the contract's
[`sql/application-role-privileges.sql`](../contract/sql/application-role-privileges.sql) then
[`sql/application-role-privileges-1.4.sql`](../contract/sql/application-role-privileges-1.4.sql),
[`sql/application-role-privileges-1.6.sql`](../contract/sql/application-role-privileges-1.6.sql) and
[`sql/application-role-privileges-1.8.sql`](../contract/sql/application-role-privileges-1.8.sql) and
[`sql/application-role-privileges-1.11.sql`](../contract/sql/application-role-privileges-1.11.sql); from contract 1.6 the
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
