# Changelog

AgentExperience.NET is a **preview**. It is not production ready, and public APIs may change between previews. The
[Known limits](docs/known-limits.md#known-limits) table lists every limit that is still unresolved, and the
[Documented boundaries](docs/known-limits.md#documented-boundaries) table states what no code change can remove.

## Unreleased

### The benefit estimate in Python and Go: contract 1.9 (story 27.1)

- **Contract 1.9** ([what it adds](contract/README-1.9.md)) brings the benefit estimate into the language-neutral
  contract, add-only: the run's model as bookkeeping metadata (`model-metadata` fixtures), the read of the holdout's
  outcome rows a page at a time (`sql/query-holdout-outcomes.sql`, the .NET store's statements line for line, with
  `holdout-outcomes` fixtures on PostgreSQL) and the estimate's arithmetic (`benefit-estimate` fixtures: Wilson per
  arm, Newcombe's hybrid interval for the difference, the verdict; overall, per lesson and per model pair). Every
  1.0-1.8 file is unchanged.
- **The Python and Go ports read the estimate**: Python `estimate_benefit(store, authorization, scope, options)`, Go
  `EstimateBenefit(ctx, store, authorization, scope, opts)`, over their in-memory and PostgreSQL stores, with the same
  fields as `ExperienceBenefitEstimate`. Their agents record the chat client's model under `agentexperience.model`, so a
  split by model counts their runs. A run whose record is sealed counts as `Pending` in a port, and a sealed lesson's
  model reads as `unknown` there (in an encrypted store its per-model pairs can differ from .NET's). A .NET test
  estimates over rows a Python and a Go agent wrote and gets the counts and verdicts each port computes over the same
  rows, with every other number equal within 1e-12. The benefit
  policy and measuring another memory provider stay .NET-only.

### Acting on the estimate in Python and Go: contract 1.10 (story 27.2)

- **Contract 1.10** ([what it adds](contract/README-1.10.md)) brings the benefit policy into the language-neutral
  contract, add-only: `benefit-select` fixtures (ranked records, the cached estimate and the policy in; the selection,
  the block and every omission with its reason and byte-exact detail out, generated through the .NET provider's own
  selection), and four `lessons-view` cases with the measured benefit, including a holdout read that fails. Every
  1.0-1.9 file is unchanged.
- **The Python and Go agents act on the estimate**, behind the same opt-in policy and defaults (Python `BenefitPolicy`,
  Go `ae.BenefitPolicy`): a lesson measured `Hurts` is omitted as `MeasuredHarmful`, a scope measured `Hurts` gets no
  lessons, `PreferMeasuredHelpful` moves lessons measured `Helps` first, and `PerModel` acts on the invoking model's
  verdicts. The estimate is cached per scope and principal and refreshed in the background, never on the request path,
  reported as `benefit_refresh`. Injection results now carry their omissions with reasons and details. The lessons
  views show each lesson's measured benefit on request. The holdout is unchanged. A .NET-written fleet in which one
  lesson measures `Hurts` is omitted by Python and Go agents over the same PostgreSQL rows. Measuring another memory
  provider stays .NET-only.
- **Behaviour change in both ports: the injection callback now also fires when nothing is injected.** Python
  `on_injected` and Go `OnInjected` are called with outcome `NothingToInject` and the omissions, as .NET's
  `OnContextInjected` is, so a host can tell "the policy omitted everything" from "nothing matched". A host that
  treated every callback as a delivery should check the outcome (`Injected`).

### Measuring another memory provider in Python and Go: contract 1.11 (story 27.3)

- **Contract 1.11** ([what it adds](contract/README-1.11.md)) brings the provider holdout into the language-neutral
  contract, add-only: the assignment (`provider-holdout-assign` fixtures: run ID, key and salt to the length-prefixed
  provider salt, fraction and arm), the write and the paged read of migration `0029`'s rows
  (`sql/record-provider-holdout-assignment.sql` and `sql/query-provider-holdout-outcomes.sql`, the .NET store's
  statements line for line, with `provider-holdout-record` fixtures on PostgreSQL covering the key, a duplicate write,
  the exact scope and the erasure trigger), the grants (`sql/application-role-privileges-1.11.sql`) and the estimate
  (`provider-benefit-estimate` fixtures). Every 1.0-1.10 file is unchanged; no schema object is added.
- **New in both ports (an addition hosts can observe): measuring another memory provider.** Python
  `use_measured_memory(agent, memory, key, holdout=...)` (`ExperienceHoldoutContextProvider` around an agent-framework
  `ContextProvider`) and Go `maf.UseMeasuredMemory(&config, memory, key, maf.MeasuredMemoryOptions{...})` (around an
  `agent.ContextProvider`) withhold what the wrapped provider added on a deterministic share of captured runs, exactly
  as .NET's `UseMeasuredMemory`: the wrapped provider's two hooks still run in both arms, the caller's context is never
  changed, and each run's arm is recorded in the background as the new telemetry operation `record_provider_holdout`
  in both ports. Python `estimate_provider_benefit` and Go `ae.EstimateProviderBenefit` read the estimate with the
  fields of `ExperienceProviderBenefitEstimate`. Off by default (rate 0). Go has no `authorize` callback, and an empty
  salt means the default there. The shutdown path a host already uses awaits the wrapper's records too (Go
  `ContextProviderOf(config).Close`, Python `wait_for_injection_records`), and the wrapper's own callback never runs on
  the invocation's path. A contract test runs a .NET, a Python and a Go agent under one key in one PostgreSQL
  database and checks that the three estimates agree. On PostgreSQL, apply the 1.11 privileges file after the others, or
  run the .NET privileges call last.

### The retrieval policy in Python and Go: contract 1.12 (story 27.4)

- **Contract 1.12** ([what it adds](contract/README-1.12.md)) brings `RetrievalPolicy` and `ConfidenceDecayPolicy`
  into the language-neutral contract, add-only: `retrieval-policy` fixtures generated through
  `ExperienceRetrievalService` (candidates, the instant and the policy in; what the store is asked for, the outcome,
  truncation, the ranked records with every component and the exclusions with their reasons out), covering `MaxAge`
  at, under and over the limit, decay by one domain, several, an unlisted one and a custom domain key, decay against
  the confidence floor, a custom recency half-life, the candidate limit and the default policy, plus
  `policy-validation.json` for every rule .NET refuses, hybrid cases (the policy's vector floor, per-channel limits,
  the floor before the count, the final cut to the candidate limit) and the model-authored exclusion's order; and a
  `lessons-view` case with a confidence floor and `MaxAge`. Every 1.0-1.11 file is unchanged.
- **The Python and Go agents take the same policy**, with .NET's defaults and validation: Python
  `use_agent_experience(..., retrieval_policy=RetrievalPolicy(...), confidence_decay=ConfidenceDecayPolicy(...))`, Go
  `maf.Options.RetrievalPolicy` and `maf.Options.ConfidenceDecay` (zero means the default; negatives are refused).
  With the defaults they rank exactly as before. A PostgreSQL test runs one seed under `MaxAge` plus per-domain decay
  through .NET, Python and Go and gets the same ordered IDs and scores within 1e-12.
- **Behaviour change in both ports: retrieval is bounded.** The text search and the vector channel together now run
  under the policy's `Timeout` (500 ms by default); before, an injection waited on the store indefinitely. Past it
  nothing is injected and the outcome is `RetrievalTimedOut` (`retrieve`: `TimedOut`), also when only the vector
  channel was still running, as in .NET; the ports' own `vector_timeout` / `VectorTimeout` is a text-only fallback only
  when it is shorter than `Timeout`. In Go a store error that arrives once the deadline has passed is a timeout too. The search is abandoned and
  counted until it ends, and while `MaxAbandonedSearches` (16) are still running an injection is `RetrievalTimedOut`
  at once, without searching, with a failure naming the cap. A host with a slow store should raise `Timeout`.
- **Behaviour change in both ports: a scope outside the identity's bounds is `RetrievalDenied`** (`retrieve`:
  `Denied`) before anything is searched or embedded; before, the store refused it and the injection was
  `RetrievalFailed`. Python's `on_injected` now also receives `RetrievalTimedOut` and `RetrievalDenied`.
- **Behaviour change in Go: the caller's cancellation propagates.** `ContextProvider.Invoking` now returns
  `context.Canceled` when the caller cancels during retrieval, so the run ends with it (as .NET's
  `OperationCanceledException`), and the `inject` and `retrieve` operations are reported cancelled; before, it was a
  `RetrievalFailed` with an injection failure. Every other failure still returns no error.
- **The integration's vector floor replaces the policy's**, as .NET's `MinimumSemanticSimilarity` does: Python
  `min_vector_relevance` and Go `Options.MinVectorRelevance`, when set, win over `minimum_vector_relevance` /
  `MinimumVectorRelevance`.
- **The ports' lessons views apply the policy**: the floor is the policy's `MinimumConfidence` (named in the reason), and
  a lesson whose last activity is older than `MaxAge` is `Expired`, as in .NET.
- **Hybrid ranking is cut to the candidate limit**, as .NET cuts it: Go `RankHybrid` and Python `rank_hybrid` return at
  most 50 records with the defaults (up to 100 before), and the vector floor is applied to every vector row before the
  channel's count.
- **Exclusions are reported.** `RankedResult` and the injection result carry `excluded` / `Excluded` with .NET's
  reasons (`IneligibleStatus`, `ModelAuthored`, `Expired`); a record a store returned with an ineligible status, or a
  model-authored one under the exclusion, was dropped silently before. The `Confidence` component carries the stored
  value as `undecayed_value` / `UndecayedValue` when a half-life applied. The `retrieve` and `inject` telemetry
  operations gain the outcomes `TimedOut`, `Denied`, `RetrievalTimedOut` and `RetrievalDenied`.
- **Limits are .NET's**: Go `SearchParameters.Validate` accepts a limit from 1 to 200 (it accepted only 51) and the
  in-memory store's `Search` honours it; Go `VectorQuery.Validate` and Python `SearchParameters` and `VectorQuery` now
  refuse a limit outside 1 to 200, and Python a `minimum_matched_terms` above 2147483647. Go `RankHybrid` refuses a
  vector floor outside 0 to 1. Go's invalid task text is refused by the store and reported `RetrievalFailed`, as
  before.
- **Go: `RankingComponent` has a new field, `UndecayedValue`**, so an unkeyed composite literal
  (`ae.RankingComponent{kind, value, weight}`) no longer compiles; use field names. Python's text search runs on its own daemon
  thread, so a hung search no longer holds the event loop's shutdown.
- Still .NET-only: required environment attributes and a custom environment scorer.
  The injection options (deciding and resolving the request, capabilities and tool risk) are story 27.4b.

### The injection options in Python and Go: contract 1.13 (story 27.4b)

- **Contract 1.13** ([what it adds](contract/README-1.13.md)) brings the deterministic injection options into the
  language-neutral contract, add-only: `inject-options` fixtures generated through the .NET provider's own selection and
  writer (ranked records, the options and the receiving agent in; the request retrieval is asked for, the block, the
  message role, every omission with its reason and detail, and the outcome out), covering each `ToolRiskClass`, the
  tool check, run tools, the gated retrieval window, each `ModelAuthoredLessonPolicy`, approach arguments, each
  `FailureDetail`, the system role and limits with the gate. Every 1.0-1.12 file is unchanged. .NET gains no public
  API; its provider gains two internal entries the fixtures are generated through.
- **New in both ports (additions hosts can observe), all off by default:** the receiving agent's capability gate
  (Python `receiving_agent=ReceivingAgent(...)`, Go `Options.ReceivingAgent`), which omits a record as
  `ToolUnavailable` or `RiskClassExceeded` (no detail) before the record limit and widens retrieval while a gate can
  reject; the model-authored policy (`model_authored_lessons` / `ModelAuthoredLessons`, `Include` or `Exclude`; the
  earlier `exclude_model_authored` / `ExcludeModelAuthored` still works); the host's request resolver (`resolve_request`
  / `ResolveRequest`: `None`/nil is the `inject` outcome `Skipped`, a failure `Failed`, a request retrieval refuses
  `RetrievalFailed`); and the host's injection decision (`decide_injection` / `DecideInjection`), fail-closed, whose
  denials are the new omission reason `HostDenied`. With none of them set, the block, the omissions and the outcomes
  are exactly what they were.
- **Both ports put a request's correlation ID on the `inject` span** as `agentexperience.correlation_id`, as .NET does.
- **The ports' injection results can carry three new omission reasons**, `ToolUnavailable`, `RiskClassExceeded` and
  `HostDenied` (Go `OmittedToolUnavailable`, `OmittedRiskClassExceeded`, `OmittedHostDenied`); a host that switches
  over the reasons should handle them.
- **Port differences:** Python's `use_run_tools` sees the agent's own tools and those an earlier context provider
  added, never tools passed to one `run` call (agent-framework does not hand them to a context provider); Go sees both.
  The ports have no final re-read, so the decision sees the ranked record. Session tracking and withdrawal (27.6),
  unconfirmed content (27.7), grant fields (27.8) and required environment attributes stay .NET-only.
- **Behaviour change in both ports: the injected callback hears every attempt**, as .NET's `OnContextInjected` does.
  Python `on_injected` and Go `OnInjected` are now also called with `Skipped` (no identity, or the resolver returned
  nothing), `Failed` and `RetrievalFailed`, and Go's also with `RetrievalTimedOut` and `RetrievalDenied` (Python's
  already was), each with nothing delivered; the `inject` span carries `agentexperience.omitted_count` 0 on those
  paths. The failure callback is still called. A host that counts every callback as a delivery should check the
  outcome. The ports' result still has no `CorrelationId` or `Failure` field.
- **Behaviour change in both ports: a byte budget of 754 bytes or less is refused** when the agent is built
  (Python `ValueError`, Go an error from `UseAgentExperience`; Go's 0 still means the default), as .NET refuses a
  budget that cannot fit the verbose block's fixed frame. New constants: Python `block.BLOCK_OVERHEAD_BYTES`, Go
  `ae.BlockOverheadBytes`.
- **Python:** an async `decide_injection` is refused with `TypeError` (the decision is synchronous, as in .NET); a
  `CancelledError` the resolver raises while the invocation itself is not being cancelled is `Failed` instead of
  propagating (Python 3.11+); `RequestContext.messages` holds the replayed history and the new input.
- **Go:** `maf.InjectionRequest.Limit` is an `*int` (nil for none, and 0 is refused as `RetrievalFailed`, as .NET
  refuses it); `Deny("")` reports an empty detail, as .NET does, while a zero `InjectionDecision` reports the default;
  `ae.ApplyHostDecisions` and `ae.HostDecision` are public.
- **Both ports: a capability gate built directly with run tools fails closed** when it is checked without the
  invocation's tools: every approach that calls a tool is `ToolUnavailable`.

### Exporting lessons, the lessons page and the model-backed reflector in Python and Go: contract 1.14 (story 27.5)

- **Contract 1.14** ([what it adds](contract/README-1.14.md)) brings three .NET features into the language-neutral
  contract, add-only, every fixture generated by the .NET classes run as a host runs them: `lesson-export` (the
  lessons view and holdout rows in; what `ExportMeasuredAsync` hands a `MarkdownLessonFileSink`, its counts and the
  file's bytes out, including a file edited by hand), `lessons-html` (a lessons page in; `ExperienceLessonsHtml`'s
  document out), `reflection-prompt` (a captured run in; the exact messages, request options and shown content the
  `ChatClientExperienceReflector` sends out) and `reflection-output` (a scripted answer in; the record finalization
  stores, or the failure kind and screening refusal, out). Every 1.0-1.13 file is unchanged; .NET gains no API.
- **New in both ports (additions hosts can observe):**
  - **Exporting measured lessons**: Python `ExperienceLessons.export_measured` / `export_measured_async` with
    `MarkdownLessonFileSink` and a `LessonSink` protocol; Go `Lessons.ExportMeasured` with `ae.MarkdownLessonFileSink`
    and `ae.LessonSink`. Same selection, order, file and counts as .NET; the file is written atomically. As in .NET,
    only a blank path is refused: the path is not confined.
  - **The lessons page**: Python `render_lessons_html` and `lessons_html_response` (status, headers and body for any
    ASGI or WSGI app), Go `ae.RenderLessonsHTML` and `ae.LessonsHTMLHandler` (an `http.Handler`), byte-identical to
    .NET's, sent with `no-store`, `X-Frame-Options: DENY` and a CSP header. Authorization stays the host's.
  - **The model-backed reflector**, opt-in: Python `ChatClientExperienceReflector` (`use_agent_experience(...,
    reflector=...)`), Go `maf.NewChatClientReflector` (`maf.Options.Reflector`), with .NET's options, prompt, request
    options, strict reading, failure kinds and messages, and records marked model-authored. The deterministic
    reflector stays the default.
  - **A reflector seam**: Python `finalize(..., reflector=)` and `finalize_async`, Go `ae.Reflector` and
    `ae.FinalizeContextWith`. A reflector that fails, or returns a reflection that does not match its request, stores
    the record `Quarantined` with no lesson, reported as Python `reflection_failure` and Go `ReflectionFailure`.
  - **The content guard for model-authored reflections**, as .NET's: credential shapes, mixed-script words,
    instruction-override phrasing, always-refused links, and URLs, hostnames and IP addresses the run never showed
    the reflector, refused as `UnsafeContent`. Screening refusals now carry .NET's code besides the reason (Python
    `screening_refusal_code`, Go `*ae.ScreeningRefusalError`), and the `finalize` span carries
    `agentexperience.reflection.screening_refusal`.
- **Behaviour change in both ports: a model-authored reflection is screened, not refused outright.** Before, screening
  refused any reflection that was model-authored, so such a record was always quarantined; now it passes when the
  content guard and the limits allow it, as in .NET.
- **Python: the agent's finalization is asynchronous** (`finalize_async`), so a host reflector may be async.
- **Go: the content guard needs a Unicode normalizer.** The core module requires no other module, so `maf` registers
  one (`golang.org/x/text`) when imported; a host that screens model-written text without `maf` calls
  `ae.SetUnicodeNormalizer`, or every model-written field is refused. Go reads zero option values as the defaults, and
  `AgentChatClient` takes a host function that maps the model, temperature and output cap onto the provider's options.
- **The exported file is created as .NET creates it**, read and write for everyone less the umask (`0644` under the
  usual umask), never owner-only; a previous file with a byte order mark, or white space around a heading's GUID, is
  read as .NET reads it.
- **The lessons page's handlers answer as .NET's endpoint does**: `GET` only (any other method, `HEAD` included, is
  `405` with `Allow: GET`), `403` with no identity and `500` on a failure, all with no body, and exactly .NET's four
  headers on success (Go no longer sends `X-Content-Type-Options` or `Content-Length`). Python's
  `lessons_html_response` takes `method=` and an identity or a resolver function.
- **A binding check or screening that raises or panics** quarantines the record with the refusal `Unreadable` and the
  exception's type only (Python `screening_exception_type`, Go `ScreeningRefusalError.ExceptionType` and
  `ae.RefusalUnreadable`), as .NET's backstop does. In Go nil evidence IDs fail the binding check.
- **The content guard's work is bounded**: linear matchers, and, as .NET's one-second match timeout, a run-content
  text that cannot be read in time admits no link and a field that cannot be checked in time is refused (Go: one
  second per text; Python: texts over 1,048,576 UTF-16 code units). It leaves U+0130 as it is, as .NET's invariant
  lower-casing does. Go `ae.ModelTextLinksIn` now also returns whether the text was read in time (breaking). Go
  screening removes each byte of invalid UTF-8, and the Go block writer writes a space for it, as .NET treats a lone
  surrogate. The reflector's strict reading accepts numbers of any length in members it does not map.
- **Where the ports differ from .NET:** a failure's cause type is the port's own type name; Python cannot refuse a
  function-invoking chat client, and Go only one that says so (give the reflector a client with no tools); a client
  that blocks Python's event loop cannot be timed out; Unicode tables differ by language, which can change the content
  guard's verdict on rare characters.

### The confidence engine in Python and Go: contract 1.15 (story 27.6a)

- **Contract 1.15** ([what it adds](contract/README-1.15.md)) brings the confidence engine into the language-neutral
  contract, add-only: `confidence-engine` fixtures generated through .NET's own `ReuseConfidenceHeuristic`,
  `ReuseConfidenceHeuristicEngine`, the lifecycle service's engine check (`ConfidenceEngineRule`) and the assessment
  token issuer and codec. They cover the default rule and its boundaries; applying evidence against every status
  (both directions and both sources against a `Validated` record, both directions against `Reinforced` and `Contested`
  ones, a machine contradiction against the rest) and against stored scores outside [0, 1]; repeated and conflicting
  evidence; the independence keys, a host engine's
  identity and every score it may or may not return, the assessment token's options, the issuer's refusals in order,
  the token's exact bytes and every path by which a token is refused. Every 1.0-1.14 file is unchanged.
- **The Python and Go ports compute confidence** as .NET does, with .NET's defaults, validation, refusal parameter names
  and, where the library wrote one, messages: Python `agentexperience.confidence` (`score`, `apply`, `status_after`,
  `independence_key_for`, `ConfidenceEngineRule`, the `ExperienceConfidenceEngine` protocol, `AssessmentTokenIssuer`),
  Go `ConfidenceScore`, `ApplyConfidenceEvidence`, `StatusAfter`, `IndependenceKeyFor`, `NewConfidenceEngineRule`, the
  `ConfidenceEngine` interface and `NewAssessmentTokenIssuer`. A host can supply its own engine, checked and recorded
  as `{RuleId}/{RuleVersion}` as in .NET. They only compute: applying an update to a stored record, verifying a key's
  inputs and reuse feedback stay .NET-only. Where a port differs, forced by its language: Go reads a zero token lifetime
  as the default rather than refusing it; Python states a lifetime in whole microseconds, and its `ExperienceIdentity`
  refuses a blank reviewer principal (`ValueError`) before the issuer can; Go's `Identity.Authorization()` with nil
  `Bounds` is the identity's own scope.

### The lifecycle in Python and Go: contract 1.16 (story 27.6b)

- **Contract 1.16** ([what it adds](contract/README-1.16.md)) lifts contract 1.0's "a port never moves a stored
  record's status, counters or revision", add-only. `sql/lifecycle.sql` publishes the .NET PostgreSQL store's own
  lifecycle statements (generated from its source and checked against it on every build) with the order a commit runs
  them in; three new fixture families pin the rest: `lifecycle-commit` and `lifecycle-apply` (generated through .NET's
  `ExperienceLifecycleService` over the in-memory store: every transition the table allows from every status and every
  one it refuses, stale revisions, status mismatches, scope, denial, validation with every error message, event-ID and
  evidence-ID replay and conflict, supersession inside and outside the commit, history paging, the reuse evidence and
  event IDs (tags 4 and 5), duplicate keys, a host engine and its refusals, `RecordedOnly`, and confidence reads) and
  `lifecycle-sql` (through the PostgreSQL store, with the tables each case leaves). Contract 1.15's `apply` cases are
  now generated through `ApplyEvidenceAsync` wherever it can express them, with every 1.0-1.15 file unchanged; the one
  thing 1.15 left unsaid is stated in 1.16: the service refuses evidence when either stored counter is saturated. No
  schema object and no privilege is added.
- **New in both ports (hosts can observe it): the ports now write lifecycle events and change a stored record's status,
  counters, score, revision and `updated_at`.** Python `ExperienceLifecycleService(store)` with `commit`,
  `apply_evidence` and `read_confidence`, and Go `NewLifecycleService(store, LifecycleOptions{})` with `Commit`,
  `ApplyEvidence` and `ReadConfidence`, over their in-memory and PostgreSQL stores, which gain `commit_lifecycle_event`
  / `CommitLifecycleEvent`, `get_history` / `GetHistory` and `check_supersession` / `CheckSupersession` (and a record
  read with .NET's outcomes). A port's evidence is admitted `HostTrusted` (the service runs as .NET's does under
  `IndependenceVerification.TrustHostSuppliedIdentifiers`; verification is story 27.7), so .NET's `ReadConfidenceAsync`
  with `ExcludeHostTrusted` leaves it out.
- **A port's default differs from .NET's: `HostTrustedEvidence = RecordedOnly`.** Until the ports can verify evidence,
  evidence a port applies is by default recorded on an event that moves only the revision: not the counters, the score,
  the status or `updated_at`, so any authorised caller of a port cannot inflate or contest a lesson with invented run IDs.
  `Counted` (.NET's behaviour under `TrustHostSuppliedIdentifiers`) is an explicit opt-in. A port's PostgreSQL commit
  refuses a record .NET seals while the commit runs (it checks once it holds the row lock), and Go refuses a host
  transaction that is not `READ COMMITTED`. A port's commit removes no embedding (27.6d), applies no reuse feedback (27.6c), and answers
  `NotFound` for a record .NET sealed in crypto-shredding mode, which it cannot read or write. Both ports report the
  `lifecycle.commit` and `confidence.apply` telemetry operations.
- **Behaviour change in both in-memory stores:** a finalized record's initial lifecycle event is now part of a history
  that further events extend, read through `get_history` / `GetHistory`, as in the .NET in-memory store.
- **A cross-language race on PostgreSQL** (`CrossLanguageLifecycleRacePostgresTests`, CI job `cross-language-lifecycle`):
  in each of six rounds a .NET, a Python and a Go process commit a transition on one record at revision 1, released
  together behind an advisory lock; the test asserts that exactly one reports `Committed`, the other two
  `StaleRevision` at revision 2, that the record's history holds the winner's event alone, and that the three read that
  history identically.

### Reuse feedback in Python and Go: contract 1.17 (story 27.6c)

- **Contract 1.17** ([what it adds](contract/README-1.17.md)) brings reuse feedback into the language-neutral contract,
  add-only. `sql/reuse-feedback.sql` publishes the .NET PostgreSQL ledger's own statements (generated from its source
  and checked against it on every build) with the order a submission runs them in; three new fixture families pin the
  rest: `reuse-feedback` (generated through .NET's `ExperienceReuseFeedbackService` over the in-memory stores: exposure
  only, human and comparative attribution, harm contesting, `RecordedOnly`, duplicate keys, ineligible, unresolved and
  refused records, every fatal and degrading validation error in order, denial, a host engine that throws or returns no
  score, store failures and their retry, the ledger's own validation and idempotency, and the derived evidence and
  event IDs, tags 1 and 2 under their own namespace), `finalize-reuse-evidence` (finalization with `ReuseEvidence =
  SameTask` and `ContradictOnFailure`, finalized twice to pin the replay) and `reuse-feedback-sql` (through the
  PostgreSQL ledger, with the two tables each case leaves). Every 1.0-1.16 file is unchanged. No schema object and no
  privilege is added.
- **New in both ports (hosts can observe it): the ports now write the reuse-feedback ledger and, for attributed
  records, confidence evidence.** Python `ExperienceReuseFeedbackService(store, lifecycle).record(...)` and Go
  `NewReuseFeedbackService(store, lifecycle)` with `Record`, over in-memory and PostgreSQL ledgers (Python
  `PostgresExperienceReuseFeedbackStore`, Go `postgres`), with .NET's validation, dispositions, reasons and retry
  semantics. Attributed evidence goes through the contract 1.16 lifecycle service, so it keeps that service's port
  default, `HostTrustedEvidence = RecordedOnly`: an attribution is recorded and moves no score until the host opts into
  `Counted`. A port verifies no attribution (story 27.7) and holds no key store, so it writes the rationale in
  plaintext and a resubmission of a submission .NET sealed is a `Conflict`.
- **New in both ports: finalization's reuse evidence**, Python `apply_reuse_evidence(...)` and Go
  `ApplyReuseEvidence(...)`, off unless asked for (`SameTask`, with `contradict_on_failure` / `ContradictOnFailure`
  off by default). A port's finalization is a pure function, so the host calls this step once the finalized record is
  stored, and again with the stored record on a run already finalized; the MAF adapters do not run it yet. The
  `finalize` span's `reuse_evidence.submitted` attribute is not written by a port.
- **Telemetry:** both ports report the `reuse_feedback` operation (`feedback_id`, `run_id`), with a nested
  `confidence.apply` for each attributed record.
- **A cross-language test on PostgreSQL** (`CrossLanguageReuseFeedbackPostgresTests`, run by the CI job
  `cross-language-lifecycle`): feedback a .NET, a Python and a Go agent submit on the same lessons is one row (the
  others `AlreadyRecorded`, a changed resubmission `Conflict` from all three), all three read the same history and
  confidence, and each agent alone leaves the same ledger, evidence, event and record rows in its own database.

### A scoped supersession lock and ranking to the bit: contract 1.18 (story 28.1)

- **A supersession locks only rows in the caller's scope.** The PostgreSQL store locked the record and its proposed
  replacement by ID alone, so with row-level security off a caller naming another tenant's record ID as the
  replacement held a row lock on that tenant's record for its transaction: no read or write, but a way to make another
  tenant's writes wait and to time them. The lock now carries the same exact-scope predicate as every other lifecycle
  statement. The outcome is unchanged (a replacement in another scope is `ReplacementNotAllowed`, as one that does not
  exist), and a test proves a supersession naming a row another transaction holds no longer waits for it.
- **Contract 1.18** ([what it adds](contract/README-1.18.md)), add-only: `sql/lifecycle-1.18.sql`, the scoped lock
  (`lock-supersession-rows-scoped`), which ports run in place of 1.16's `lock-supersession-rows`; and
  `retrieval-policy/ranking-near-tie`, two pairs of records whose scores differ by one ulp, ranked against their
  experience-ID tie-break, so a port whose arithmetic differs from .NET's in the last bit orders them differently
  and fails. Every 1.0-1.17 file is unchanged.
- **The Python and Go ports run the scoped lock**, with PostgreSQL tests. **Go's decay now agrees with .NET's on the
  fixture**: its `math.Pow` was 2 ulps off on the new fixture's input (about 30% of inputs are off by an ulp or more),
  which tied two records .NET orders, so the Go port computes `2^x` with its own correctly rounded double-double
  `exp2`, which agrees with .NET's C-library `pow` except on rare inputs where that `pow` misrounds (about 1 in 1,500
  measured on glibc); and it rounds each ranking component's product before adding it, so the compiler cannot fuse the
  two (an FMA, which Go does on arm64). Python, which calls the platform's `pow` as .NET does, already matched. Only
  the fixture's inputs are pinned: near-tied records whose recency or decay factor falls on a misrounded input may
  still be ordered differently across runtimes.

### The failure signature (story 28.2)

- **A failed tool call now has a failure signature**: `ExperienceFailureSignature` (Abstractions), its tool name, its
  error class (the token its `[failed: …]` marker shows) and a fingerprint, the first 16 bytes of the SHA-256 of the
  error's normalized first line, or its last line after a bare header such as a Python traceback's (GUIDs, hex runs,
  paths, quoted literals, ISO timestamps and numbers replaced by fixed placeholders, with ASCII-only rules so ports
  match; `SignatureVersion = 1`). The same failure in a sibling run, with other paths, numbers and IDs, has the
  same signature. `ExperienceFailureSignatures.Of(record)` lists a record's, de-duplicated, each `Resolved` when a later
  call in the same verified record succeeded. Nothing new is captured: it is derived from `ToolName` and `Error`.
- **A new optional port, `IExperienceSignatureSource`** (`FindBySignatureAsync`), finds reusable records by an exact
  signature or by tool and error class, with text search's eligibility (scope and grants, live records, statuses,
  confidence floor, authorship exclusion) and its refusals. `InMemoryExperienceSignatureSource` and
  `PostgresExperienceSignatureSource` implement it, both storage registrations register it, and the conformance suite
  gains `SignatureSourceConformanceTests`. `IExperienceCandidateSource` is unchanged.
- **Core's `ExperienceSignatureRetrievalService`** applies the retrieval policy's floor and expiry over the port, turns
  refusals and failures into outcomes, and emits a `retrieve.signature` span (`agentexperience.retrieval.channel`,
  `agentexperience.signature.match`). Injection, ranking and the provider are unchanged: stories 28.3 and 28.5 use it.
- **PostgreSQL: migration `0030`** adds `agent_experience.experience_failure_signatures` (tool, class, fingerprint,
  resolved, version and the record's scope; never error text), written in the record's own transaction, removed by
  erasure through a trigger, with row-level security policies the privileges call creates and verifies, and two
  tenant-leading lookup indexes. **Re-run `ApplyApplicationRolePrivilegesAsync` after migrating**: until then records
  are created without their signatures. Then run `BackfillFailureSignaturesAsync`, which writes the signatures of
  plaintext records stored earlier; a sealed record gains them only when it is finalized again. In encrypted mode the
  signatures are stored in the clear, like the other search data (KL-2). The Python and Go ports neither write nor
  read the table; no contract file changes.
- **The retrieval benchmark measures the signature channel**: 18 sibling-run failures against both stores. Exact
  matching finds 13 of the 15 records that hold the same failure with no false positive; coarse matching finds all 15
  with 0.22 unexpected candidates per query.

### Recall at the moment of failure (story 28.3)

- **A failing run is shown what worked**, opt-in: `AgentExperienceOptions.Recall` (or `ExperienceInjectionOptions.Recall`
  under the explicit wiring) set to an `ExperienceRecallOptions`. When a tool call in a captured run throws, or
  `ClassifyToolResult` names an error class for it, and a reusable lesson holds the same failure signature as resolved,
  one short labelled hint (`=== BEGIN HISTORICAL REFERENCE: EARLIER FIX FOR THIS FAILURE … ===`, at most 600 bytes by
  default) naming the calls that worked, rendered as the block's `Tried:` line renders them, is appended to the
  instructions of the model's next call in that run. It is never part of the tool's result: capture records the tool's
  own result or error, and a thrown exception is rethrown unchanged. Off by default; with it off nothing changes.
- **The block's rules apply**: the signature source's eligible records, exact match first (coarse only with
  `AllowCoarseMatch`), then the same final eligibility re-read, grant disclosure (a `LessonOnly` grant gives no hint),
  model-authored policy, capability gate and host decision. A lookup is bounded by `LookupTimeout` (300 ms); a run
  answers at most `MaxHintsPerRun` (3) failures, looks a signature up once (again only after a timeout or failure) and
  starts at most four times that many lookups; a record the run or session already holds is not hinted again, parallel
  failures never hint one record twice; a hint is charged to the session's budget, which is read before the arm is drawn.
  The hint is delivered as system-level instructions for the rest of the run: its labels are hygiene, not a control.
- **Measured by the holdout**: the same arm as the block. A held-out run gets no hint and its would-be hinted records are
  recorded as its assignment; a treatment run's hinted records are recorded as assignment and exposure. Assignment rows
  do not say whether a block or a hint delivered a record, so the estimate covers both; recall rows arise only after a
  matched failure, so the guide says how to read them.
- **Reported** through `ExperienceRecallOptions.OnRecall` (`ExperienceRecallResult`: run, signature, outcome, record
  IDs) and a new `recall` telemetry operation (`agentexperience.record_count`, `agentexperience.signature.match`);
  `docs/telemetry-names.json` gains both. Without a signature source recall is reported once through
  `OnCaptureFailure` at the new stage `Recall`. The quick-start demo shows a hint with `--recall`. .NET only: the
  Python and Go ports and the contract are unchanged.

### The recipe: what the working call produced (story 28.4)

- **A vouched result excerpt on the working attempt.** `ExperienceCaptureOptions.RecipeResultExcerpt` (off by default)
  lets the host vouch, per call that returned, for a short excerpt of its result. Capture sanitizes it with the
  `ToolResult` policy, cuts it after sanitization to `CaptureLimits.MaxRecipeExcerptLength` (200) characters, never
  between a surrogate pair, and stores it as `ToolCallRecord.RecipeExcerpt` (an init-only property, in the payload only
  when set). A failed call never gets one; a callback that throws records nothing and is reported once per run at the
  new stage `ExperienceCaptureFailureStage.RecipeExcerpt`, by type only. Injection shows it only on the `Worked:`
  attempt, for a tool `ExperienceInjectionOptions.RecipeExcerpts` names, as `tool(...) [returned: "…"]`, quoted and
  bounded like an argument string; the recall hint's `worked:` line renders the same way. With none of it set, every
  record, payload, block and fixture is byte for byte what it was.
- **One place to consent.** `AgentExperienceOptions.Recipe(toolName, arguments, resultExcerpt)` sets the capture
  argument allowlist, `ApproachArguments`, the excerpt callback and `RecipeExcerpts` for a tool together. The
  quick-start sample's `--recipe` switch shows one.
- **Signed: claims version 4, fail closed.** `SignClaimsVersion = 4` signs `HMAC-SHA256.aexp-prov.v4`
  (`ExperienceProvenanceSignature.HmacSha256ClaimsV4`), version 3 plus each call's excerpt. The default stays 3, which
  signs every record as before and leaves excerpts unsigned, so with signing on an excerpt is shown only under a version
  4 signature that verifies (`ExperienceRetrievalService.AreRecipeExcerptsConfirmed`); records trusted unsigned or listed
  as reviewed never show one. To show recipe excerpts with signing on, set `SignClaimsVersion = 4` once every verifying
  node runs this release (an earlier build refuses a version 4 signature). `HistoricalReferenceWriteSettings` shows
  excerpts only with `AreRecipeExcerptsConfirmed` or the explicit `ShowRecipeExcerptsWithoutSigning`. A lone surrogate
  in an excerpt becomes U+FFFD at capture, so the record can always be signed and stored; an excerpt that is empty once
  cleaned keeps the call's `[returned]` marker.
- **Grants.** `ExperienceGrantRequest.RecipeExcerpts` / `ExperienceGrant.RecipeExcerpts` name the tools whose excerpts
  an owner consents to show a recipient (only under a level that shows the approach), surfaced on reads as
  `ExperienceRecordGetResult.GrantRecipeExcerpts`, `RankedExperience.GrantRecipeExcerpts` and
  `ExperienceInjectionDecisionContext.GrantRecipeExcerpts`. PostgreSQL migration `0031_grant_recipe_excerpts.sql` adds
  the immutable `experience_grants.recipe_excerpts` column; reads use `to_jsonb`, so they keep working on a schema not
  yet migrated, and only issuing a grant that names recipe excerpts needs it.
- **Contract 1.19** ([what it adds](contract/README-1.19.md)), add-only: a tool call's optional `recipeExcerpt`
  payload member, `schema/experience-record.v1-1.19.json` (the 1.0 schema plus that member, which a 1.19 port validates
  against), the inject option `recipeExcerpts` and three `inject` cases (`recipe-excerpt`, `-verbose`,
  `-not-allowlisted`). Every 1.0-1.18 file is unchanged. **The Python and Go ports** read, keep and write the excerpt
  back unchanged, and render it under `recipe_excerpts` / `RecipeExcerpts` exactly as .NET does; they do not capture or
  sign one, and grants stay .NET-only. **Port limitation:** a port cannot verify signatures, so with its allowlist on it
  renders any stored excerpt, signed or not, where a signing .NET reader would suppress an unconfirmed one (off by
  default; see [contract 1.19](contract/README-1.19.md)).
- **Experiment.** `experiments/AgentExperience.LiveReuse/recipe-amendment.md` proposes (not registered, not run) a
  recipe arm for the real-task experiment.

### A SQLite store and durable capture (story 28.7)

- **New package `AgentExperience.Storage.Sqlite`**: durable storage in one local SQLite file, for a single agent on one
  machine (a coding agent, a CLI, an MCP server). `.UseSqlite(path)` on the one-call builder (or
  `AddAgentExperienceSqliteStorage(path)`) registers `SqliteExperienceRecordStore`, `SqliteExperienceCandidateSource`
  (FTS5 full-text search), `SqliteExperienceSignatureSource` and a reuse-feedback ledger over the file. Each passes the
  same store conformance suite as the PostgreSQL and in-memory stores, with every request validated by the PostgreSQL
  store's own rules, and the record payload is the PostgreSQL store's version-1 JSON.
- **Schema**: `ExperienceSqliteSchema.MigrateAsync(path)` applies journaled, versioned scripts, each in one transaction
  with its journal row (safe to repeat and to run from two processes at once), and puts the file in write-ahead-log
  mode. Nothing migrates on its own unless `SqliteStorageOptions.MigrateSchemaOnFirstUse` is set. Connections run with
  foreign keys on and a busy timeout, and every write is one `BEGIN IMMEDIATE` transaction, so two connections or
  processes committing from one revision have exactly one winner.
- **Search** reads a query's terms exactly as the in-memory source does, with the same any-term `MinimumMatchedTerms`
  rule and coverage ranking, through an FTS5 index stemmed with Porter, so "invoices" matches "invoice" as it does in
  PostgreSQL. The retrieval benchmark gains a SQLite column (`GoldenSqliteReport.txt`).
- **Erasure**: `SqliteExperienceRecordStore.DeleteAsync` leaves a payload-free tombstone and removes the record's search
  text, signatures, events, evidence, injection summary and holdout rows in one transaction, with `secure_delete` on, the
  FTS5 index rewritten and the write-ahead log checkpointed afterwards. A new database file is created owner-only, and a
  store refuses a missing, older or newer schema on its first operation.
- **Durable capture**: a new optional port, `IExperienceRunStore` (save a completed run by run ID and completion event,
  read it within an authorization, list a scope's unfinalized runs, mark one finalized, delete one), implemented by
  `SqliteExperienceRunStore` (registered by `UseSqlite`) and `PostgresExperienceRunStore` (opt-in,
  `AddAgentExperiencePostgresRunStore`, migration `0032_experience_runs.sql` with its grants and row-level security
  policies; refused in crypto-shredding mode). With one registered, the capture service saves each run as it completes
  it (new `InMemoryExperienceCaptureService` constructor; `CompleteRunResult.RunStoreFailure` reports a failed save, which
  never fails the run, and the MAF adapter reports it at the new `ExperienceCaptureFailureStage.RunStore`), and
  finalization reads a run the capture service no longer holds from the store (new `ExperienceFinalizationService`
  constructor), with the same verification, closed-round and idempotency rules, then marks it finalized. Erasing a
  record turns the stored run it came from into a tombstone, so a capture retry can never save it again. A save runs
  off the caller's thread, bounded by `ExperienceRunStoreOptions.SaveTimeout` (5 s; SQLite waits at most
  `RunSaveBusyTimeout`, 2 s, for a busy file); `ExperienceRunStoreOptions.ShouldPersistRun` keeps runs out of the store;
  a run the host's storage decision refuses (`StorageDenied`) is deleted from it, a failed delete reported on
  `FinalizeExperienceResult.RunStoreFailure`. The run store conformance suite runs on both stores; a run completed in
  one service provider is finalized by another on SQLite and on PostgreSQL. Without a run store nothing changes.
- **Try it**: the quick-start sample takes `--sqlite <path>` (one run per process: run it twice, and the second process
  is handed the lesson the first stored), and the CI triage experiment takes `--storage sqlite:<path>`, with which a
  failure still waiting for its fix is triaged and its run finalized by a later pass once the fix lands
  (`--scripted --scripted-later` shows it offline).
- **Not provided**: sharing grants and the access log, encryption and crypto-shredding, row-level security and database
  roles, vectors, retention sweeps. Internally, the PostgreSQL payload's attempt, outcome, environment and provenance
  mappings are each one method now, shared with the SQLite store; nothing stored changes.

### Precision before recall: an injection-time match floor (story 28.5)

- **`ExperienceInjectionOptions.MinimumMatch`** (type `ExperienceMatchFloor`, off by default): a record is injected only
  when it has an exact failure-signature match on the current run (a recall hint), text coverage at least
  `MinimumTextCoverage` (the share of the request's distinct terms it contains, relative to the store's terms; required,
  no default), or semantic similarity at least `MinimumSemanticSimilarity` (by default the retrieval policy's
  `MinimumVectorRelevance`; with neither set the semantic leg is off). A weaker record is omitted as the new
  `InjectionOmissionReason.WeakMatch`, with a content-free detail, before the capability gate and the record limit, so
  it takes no slot and a stronger one backfills; a host-limited request is widened like the capability gate's. A block
  that injects nothing with a `WeakMatch` omission ends `NothingToInject`, and its `inject` span carries
  `agentexperience.nothing_reason = weak_match`. The holdout splits after the floor. Under a floor, recall never asks
  for a coarse signature match. Which candidates retrieval returns is unchanged, and with the floor off everything is
  byte for byte what it was.
- **Each channel's strength travels with the record.** `ExperienceCandidate.TermCoverage` (the in-memory, PostgreSQL and
  SQLite text sources set it), `RankedExperience.TextCoverage` and `RankedExperience.VectorRelevance` (set by
  retrieval). Ranking is unchanged.
- **Chosen by a rule fixed before measuring, and the rule said off.** The retrieval benchmark's goldens gain an
  `INJECTION FLOOR` sweep (0 to 0.6). No floor kept unexpected records at or below 0.15 in the first 8 on both stores, and
  none removed the distractor-overlap noise (distractors containing every word of a short query), while every floor
  cost long, realistic requests their lesson; so the floor stays off and has no recommended value
  ([the table](docs/benchmarks.md#the-injection-floor-story-285)).
- The Python and Go ports and the contract are unchanged; the ports do not apply a floor yet.

## 0.1.0-preview.17

This preview measures whether injected lessons help. An opt-in holdout withholds the block on a deterministic share of
runs and records both arms; `AgentExperienceBenefit.EstimateAsync` compares the arms' verified outcomes, overall, per
lesson and optionally per model; the injection provider can act on the measured benefit; the same holdout measures any
other MAF memory provider; and lessons measured as helping can be exported to other memory stores. PostgreSQL gains
migrations `0027` to `0029` (re-run `ApplyApplicationRolePrivilegesAsync` after migrating). The Python and Go ports
take part in the holdout through contract 1.8 (add-only). Every new option is off by default, and the public API
changes are additions. The same tag releases the six NuGet packages, the Python distribution `agenticexperience` and
the six Go modules at one version.

### The holdout in Python and Go: contract 1.8 (story 25.6)

- **Contract 1.8** ([what it adds](contract/README-1.8.md)) brings the injection holdout into the language-neutral
  contract: the assignment function (`holdout-assign` fixtures: run ID and salt to fraction and arm), the withholding
  on the injection path, the statement that records an assignment (`sql/record-holdout-assignment.sql`, the .NET
  store's line for line, with `holdout-record` fixtures on PostgreSQL) and the application role's grants on migration
  `0027`'s table (`sql/application-role-privileges-1.8.sql`, run after the 1.0, 1.4 and 1.6 files). Every 1.0-1.7 file
  is unchanged.
- **The Python and Go ports take part**, behind the same opt-in option, off by default: Python
  `use_agent_experience(..., holdout=HoldoutOptions(rate=..., salt=...))`, Go `HoldoutOptions{Rate, Salt}` on the Agent
  Framework options. A held-out run's block is withheld (outcome `Withheld`), both arms are recorded in the background
  (telemetry operation `record_holdout`), and an invalid rate or an empty salt is refused at construction. In a mixed
  fleet sharing one PostgreSQL store, `AgentExperienceBenefit.EstimateAsync` counts the Python and Go agents' runs with
  the .NET ones; a .NET test estimates over rows a Python and a Go agent wrote. The estimate, the benefit policy,
  measuring another memory provider, exporting lessons and recording the run's model stay .NET-only.
- **Upgrading a mixed fleet:** apply `sql/application-role-privileges-1.8.sql` after the contract's other privileges
  files (or run the .NET privileges call last); without it a port's assignments are reported `NotRecorded` and
  injection carries on.

### The injection holdout: withhold the block on a share of runs and record both arms (story 25.1)

- **`ExperienceInjectionOptions.Holdout`** (`ExperienceHoldoutOptions { Rate, Salt }`, off by default with `Rate = 0`).
  On a deterministic, run-ID-derived share of captured runs that have a block to inject, the provider builds the block
  and then withholds it: no message, nothing staged in the session, no exposure, no "last injected", and the result is
  the new `InjectionOutcome.Withheld` with `ExperienceInjectionResult.WithheldExperienceIds`. The other runs are
  injected as before. The arm is SHA-256 over the run ID (RFC 4122 byte order) and the salt, so every invocation of a
  run lands in the same arm. Only invocations captured by the agent's own `UseExperienceCapture` take part. A rate
  outside 0..1 or not a number, or an empty salt, throws when the provider is constructed. See
  [Measuring benefit: the holdout](docs/guide/injection.md#measuring-benefit-the-holdout).
- **Both arms are recorded**, in the background, through the new optional store capability
  `IExperienceRecordStore.TracksHoldout` / `RecordHoldoutAssignmentAsync(authorization, scope, HoldoutAssignment, ct)`
  (`HoldoutArm`, `HoldoutAssignment`, `ExperienceHoldoutRecordResult`); the default records nothing, so existing stores
  keep compiling. The outcome is the new `record_holdout` telemetry operation (`Recorded`, `NotTracked`, `NotRecorded`,
  `OutcomeUnknown`). Rows hold IDs, revisions, the arm, the scope and an instant, never content, and only for records in
  the invocation's own scope.
- **PostgreSQL: migration `0027_experience_holdout_assignments.sql`** adds `agent_experience.experience_holdout_assignments`
  (keyed on run and record, `ON CONFLICT DO NOTHING`), an erasure trigger that removes a record's rows and its own run's
  rows, and its row-level security policies. Re-run `ApplyApplicationRolePrivilegesAsync` after migrating: the role gets
  `SELECT` and `INSERT`. The table is .NET-only, so the contract is unchanged; in a mixed fleet run the .NET privileges
  call after the contract's files. The in-memory store records assignments too.

### Estimate benefit from verified outcomes (story 25.2)

- **`AgentExperienceBenefit.EstimateAsync(services, authorization, scope, options?, ct)`** compares how often the
  holdout's two arms verified, overall and per lesson: per arm the runs, successes and rate, the difference (treatment
  minus holdout) with a 95% Newcombe hybrid-score interval, and a verdict (`ExperienceBenefitVerdict.NoEvidence`,
  `Helps`, `Hurts`) that stays `NoEvidence` until each arm has `MinimumRunsPerArm` runs (20 by default) and the interval
  excludes zero. A run's outcome is its own finalized record's; `Unknown` outcomes, runs not finalized yet and runs
  recorded in both arms are left out of the rates and counted. The arithmetic is the pure
  `ExperienceBenefitEstimator.Estimate` in Core. Nothing about ranking, confidence or injection changes. See
  [Reading the estimate](docs/guide/injection.md#reading-the-estimate).
- **New optional store read `IExperienceRecordStore.QueryHoldoutOutcomesAsync`** (`HoldoutOutcomeQuery`,
  `HoldoutOutcomeCursor`, `HoldoutOutcomeRow`, `HoldoutOutcomeQueryResult`): a keyset-paged read of one scope's
  assignment rows, each with its run's outcome status, under the caller's authorization. The default reads nothing and
  reports not tracked; a store overrides `TracksHoldout`, `RecordHoldoutAssignmentAsync` and this read together, which
  the conformance suite's `AssertHoldoutTrackingIsConsistent` now checks. The in-memory and PostgreSQL stores implement
  it (PostgreSQL in one statement per page, opening sealed records; no migration).

### Act on measured benefit (story 25.3)

- **`ExperienceInjectionOptions.Benefit`** (`ExperienceBenefitPolicy { Enabled, RefreshInterval, RefreshTimeout, Window,
  MinimumRunsPerArm, PreferMeasuredHelpful }`, off by default). With it on, a lesson the holdout measured as hurting is
  omitted before selection with the new `InjectionOmissionReason.MeasuredHarmful`; a scope whose overall verdict is
  `Hurts` gets no lessons (withdrawal notices still go out); and with `PreferMeasuredHelpful` lessons measured as
  helping move ahead of the rest before the record limit. The estimate is cached per scope, tenant and principal and
  refreshed in the background (every 10 minutes by default, each refresh bounded by 30 seconds), counting only
  assignments from the last 30 days, so verdicts can reverse; it never delays or fails an invocation, and an estimate
  the store could not give is never acted on. The holdout keeps running unchanged. Invalid bounds throw
  `ArgumentOutOfRangeException` when the provider is constructed. See
  [Acting on the estimate](docs/guide/injection.md#acting-on-the-estimate).
- **New telemetry operation `benefit_refresh`** (measurements only: `Refreshed`, `NotRefreshed`, `OutcomeUnknown`), in
  `docs/telemetry-names.json` too.

### Know which model learned and which model used it (story 25.4)

- **Capture records the model.** At run start `UseExperienceCapture` adds `Environment.Metadata["agentexperience.model"]`
  (`HoldoutOutcomeRow.ModelMetadataKey`): the agent's chat client's `ChatClientMetadata.DefaultModelId`, found through the
  agent's `GetService`, trimmed and capped at 256 characters. A host environment that already sets the key keeps its
  value; with no model id the key is absent. A record keeps its source run's environment, so a lesson names the model
  that learned it. Nothing is inferred from text. No contract or schema change. The key is the holdout's bookkeeping
  only: the default reflector does not list it as a precondition, the block's environment line does not show it, and
  retrieval ignores it in required and preferred environment attributes and hides it from the environment scorer, so a
  lesson learned on one model reads and ranks the same for an agent on any other.
- **The holdout read carries both models.** `HoldoutOutcomeRow.RunModel` (the run's own record) and `LessonModel` (the
  named lesson's record), new optional init-only members, `null` when absent. The in-memory and PostgreSQL stores fill
  them from the records the read already opens; PostgreSQL reads the lessons in the same statement and decodes sealed
  ones in the same key-store batch.
- **`ExperienceBenefitOptions.SplitByModel`** (default `false`, when the estimate is unchanged). On, the estimate adds
  `ByConsumingModel` (`ExperienceModelBenefit` per run model, unrecorded ones as `ExperienceBenefitEstimate.UnknownModel`)
  and each lesson `ByModelPair` (`ExperienceModelPairBenefit` per producing and consuming model), every group decided
  by the same rules and minimum. `ExperienceBenefitAccumulator` and `AgentExperienceBenefit.EstimateAsync` pass it through.
- **`ExperienceBenefitPolicy.PerModel`** (default `false`). On, the benefit policy acts on the verdicts for the invoking
  agent's own model, falling back to the pooled verdict wherever that model's group has `NoEvidence`. See
  [Per model](docs/guide/injection.md#per-model).

### Holdout hardening (story 25.10)

- **The lessons view can show what the holdout measured.** `ExperienceLessonsQuery.IncludeMeasuredBenefit` (default
  `false`) fills each lesson's new `ExperienceLesson.MeasuredBenefit` (an `ExperienceLessonBenefit`, or `null` when no
  assigned run named it or the store does not record the holdout) from one estimate for the page's scope, over the
  context provider's benefit window and threshold, so a host can see why a lesson stopped being injected. Off, nothing
  more is read and the page is unchanged.
- **The estimate holds one page at a time.** The new `ExperienceBenefitAccumulator` (Core) folds holdout rows run by run
  and gives exactly what `ExperienceBenefitEstimator.Estimate` gives over the same rows, in memory that grows with runs
  and lessons rather than rows. `AgentExperienceBenefit.EstimateAsync` and the benefit policy's refresh use it; a store
  that pages one run's rows apart is refused with `InvalidOperationException` instead of counting the run twice.
- **PostgreSQL: a holdout write that overlaps an erasure of its run's own record is refused.** The insert now also locks
  the run's own records `FOR KEY SHARE` and writes nothing when all of them are by then tombstones, so an erasure under
  way when the write starts makes it record nothing, and one that starts during the write removes its rows. Only a write
  that overlaps the erasure is handled. **Known limit:** a write that starts after the erasure has committed still
  records its rows, because an erased record keeps no run ID; those rows hold IDs only, never join an outcome, and are
  counted as `Pending`.
- **PostgreSQL: migration `0028_experience_records_source_run_id.sql`** adds `ix_experience_records_source_run` on
  `experience_records (tenant_id, source_run_id)`, which the holdout's outcome read and the write's run lookup use
  instead of scanning the tenant's records. It is a plain `CREATE INDEX` (the migrator runs in a transaction), which
  blocks writes to `experience_records` while it builds; on a large table build it `CONCURRENTLY` first, as
  [Applying the schema](docs/guide/deployment.md#applying-the-schema) shows. No privilege changes.

### Measure any memory provider (story 25.8)

- **`ExperienceHoldoutContextProvider`** wraps any MAF `AIContextProvider` (Mem0, Zep, Letta, your own) under the
  holdout, recorded against a key you choose (1-100 characters of `[A-Za-z0-9._-]`; anything else throws
  `ArgumentException`). The wrapped provider is always invoked, and its `InvokedAsync` and state keys forwarded, so it
  keeps learning in both arms; its exceptions propagate unchanged. On a deterministic share of captured runs whose
  wrapped context is not empty (salt `"{providerKey.Length}:{providerKey}/{Salt}"`, independent of the lessons' holdout and of other
  providers) the context it added is withheld. Off by default (rate 0). With the one-call setup,
  `services.UseMeasuredMemory(memory, "mem0", holdout?)` builds it over the container's store, for
  `ChatClientAgentOptions.AIContextProviders`. No vendor SDK dependency. See
  [Measuring another memory provider](docs/guide/injection.md#measuring-another-memory-provider).
- **`AgentExperienceBenefit.EstimateProviderAsync(services, authorization, scope, providerKey, options?, ct)`** returns
  an `ExperienceProviderBenefitEstimate`: the overall comparison of the provider's arms, by the same rules and verdict as
  the lessons' estimate (no per-item comparison; the provider's items are opaque).
- **New optional store capability** `IExperienceRecordStore.TracksProviderHoldout`,
  `RecordProviderHoldoutAssignmentAsync(authorization, scope, ProviderHoldoutAssignment, ct)` and
  `QueryProviderHoldoutOutcomesAsync(authorization, ProviderHoldoutOutcomeQuery, ct)` (`ProviderHoldoutOutcomeRow`,
  `ProviderHoldoutOutcomeQueryResult`); the defaults record and read nothing, so existing stores keep compiling.
  `RecordStoreConformanceTests.AssertProviderHoldoutTrackingIsConsistent` checks all-or-none. The in-memory and
  PostgreSQL stores implement it. New telemetry operation `record_provider_holdout` (`Recorded`, `NotTracked`,
  `NotRecorded`, `OutcomeUnknown`).
- **PostgreSQL: migration `0029_experience_provider_holdout_assignments.sql`** adds
  `agent_experience.experience_provider_holdout_assignments` (keyed on tenant, run and provider key, `ON CONFLICT DO
  NOTHING`, behind 25.10's late-write guard), an erasure trigger that removes a run's rows when the run's last live
  record in the tenant is erased (`0027`'s trigger now does the same for its run rows), and its row-level security
  policies. Re-run `ApplyApplicationRolePrivilegesAsync` after migrating:
  the role gets `SELECT` and `INSERT`. .NET-only; the contract is unchanged.

### Export measured lessons to other memory stores (story 25.9)

- **`AgentExperienceLessons.ExportMeasuredAsync(identity, sink, options?, ct)`** hands an `IExperienceLessonSink` the
  complete current set of lessons in exactly the identity's scope that are eligible, verified and measured `Helps` (over
  the benefit policy's window, and `MinimumRunsPerArm` unless `ExperienceBenefitOptions` is passed), each an
  `ExportedLesson`: ID, revision, task ID, the lesson text without the block's wrapper, the measured comparison and the
  export instant. The sink upserts what is new or changed and removes what is absent, so a lesson revoked, erased or no
  longer measured as helping leaves the copy too. A store that does not track the holdout gives an empty set (the sink is
  still called); a denied read returns the store's outcome without calling the sink; sink exceptions propagate. It
  returns `ExperienceLessonExportResult` with the sink's `ExperienceLessonSyncResult(Added, Updated, Removed)`. Explicit
  only: nothing exports automatically. No vendor SDK dependency.
- **`MarkdownLessonFileSink(path, timeProvider?)`** keeps one deterministic Markdown file (sorted by experience ID; the
  scope, the export instant, and each lesson's revision, measured benefit and text, the untrusted parts as indented code
  blocks), written atomically (temporary file, then move), for `AGENTS.md`, `CLAUDE.md` or a skill folder. See
  [Sharing measured lessons with other tools](docs/guide/injection.md#sharing-measured-lessons-with-other-tools).

## 0.1.0-preview.16

The Python and Go ports can now list what an agent learned and report the same OpenTelemetry spans and metrics as .NET.
Both pass contract 1.7, which this preview adds (add-only; for .NET users no library behaviour or public API changed).
The same tag releases the six NuGet packages, the Python distribution `agenticexperience` and, for the first time, six Go
modules (the new one is the optional `go/telemetry` adapter) at one version.

### Port telemetry: Python and Go emit what .NET emits (story 23.5)

- **Python and Go report the same spans and metrics as .NET** for the operations they perform: `inject`, `retrieve`,
  `record_injection`, `capture.start_run`, `capture.append_attempt`, `capture.complete_run`, `verify`, `reflect`,
  `finalize` and `index`, under the same two sources and meters, span names, three instruments, four dimensions and span
  attributes as [the telemetry contract](docs/telemetry.md#the-python-and-go-ports), so one dashboard and one alert serve
  a mixed fleet. Neither core imports OpenTelemetry: they call a small observer hook that costs a `None`/`nil` check
  when nothing is registered. Python: the optional `otel` extra (`pip install "agenticexperience[otel]"`, the
  OpenTelemetry API only), `agentexperience.telemetry.enable()`. Go: the new module `go/telemetry` (the OpenTelemetry API
  only), `telemetry.Enable()`; it is released with the other Go modules, now six, and nothing requires it. No
  OpenTelemetry SDK or exporter is a dependency, and an observer, exporter or provider that raises is swallowed.
- **A pin for the names.** A .NET test generates [`docs/telemetry-names.json`](docs/telemetry-names.json) from the .NET
  definitions (`AGENTEXPERIENCE_TELEMETRY_NAMES_UPDATE=1` rewrites it) and fails on drift; the Python and Go tests load it
  and fail when a name or value a port can emit is not in it. No .NET library behaviour or public API changed.
- **Go: `...Context` forms.** `StartRunContext`, `(*RunCapture).AppendAttemptContext`, `CompleteContext` and
  `FinalizeContext` take the context the operation's span parents under; the existing forms are unchanged.

### Contract 1.7: the lessons view in Python and Go (story 23.4)

- **Contract 1.7** (add-only: every earlier file is unchanged; [what 1.7 adds](contract/README-1.7.md)) publishes what
  the .NET lessons view reads and answers. `contract/sql/list-experience-lessons.sql` holds the .NET store's page-with-
  injections statement line for line (the lessons page and each record's last injection in one bounded statement), its
  status-filtered form, and the two forms that leave the injections out for a schema that cannot read the injections
  table. The new `lessons-view` fixture family is generated by the .NET lessons view over PostgreSQL with a fixed clock:
  each lesson's status, confidence, checks, rendered block text (byte-equal to `RenderAlone`), eligibility and reason,
  last injection and count, and the page's `hasMore`, `outOfScopeDropped` and `injectionsTracked`. The .NET view and the
  statements run as written must give the same rows. No .NET library behaviour or public API changed.
- **Python and Go can list what an agent learned.** Both ports add a read-only lessons API (Python `agentexperience.lessons`,
  Go `Lessons` in the root module with the stores' page reads) reachable from the one-call setup and constructible from a
  store, under the caller's authorization and own scope only, never writing, and now claim contract 1.0 to 1.7. A record
  that cannot be rendered is listed as `Unrenderable`. `Expired` and `ContentUnconfirmed` do not exist in the ports (no
  maximum-age policy, no provenance signing); HTML rendering is not provided.

## 0.1.0-preview.15

The Python and Go ports now retrieve by meaning as well as by text. Register an embedder and a reworded query finds a
lesson it shares no words with; without one, nothing changes. Both ports pass contract 1.6, which this preview adds
(add-only; for .NET users no library behaviour or public API changed). The same tag releases the six NuGet packages, the
Python distribution `agenticexperience` and the five Go modules at one version.

### Contract 1.6: vector search calls (story 23.1)

- **Contract 1.6** (add-only: every earlier file is unchanged; [what 1.6 adds](contract/README-1.6.md)) brings
  retrieval by meaning into the contract, so the Python and Go ports can match .NET's recall on reworded queries. It
  publishes the .NET embedding index's statements line for line: the conditional embedding write and its revision probe
  (`contract/sql/write-experience-embedding.sql`), and the nearest-neighbour search, with and without the model-authored
  exclusion, and its two compatibility probes (`contract/sql/search-experience-vector.sql`, the dimension written in as
  `vector({dim})`). It also publishes the application role's grants on `agent_experience.experience_embeddings`
  (`contract/sql/application-role-privileges-1.6.sql`, applied after the 1.0 and 1.4 files), and states the text that
  is embedded and its content hash, the validation rules, the relevance formula, the text-only fallbacks, and how the two
  channels merge before the existing ranking. Four fixture families, generated from .NET with fixed vectors and no
  embedding model: `embedding-summary` and `hybrid-merge` (no database), and `embedding-write` and `vector-search`
  (PostgreSQL with pgvector). The .NET index and the contract statements run as written must give the same results. A
  contract test keeps the .NET privileges call equal to the 1.0, 1.4 and 1.6 files together. No library behaviour or
  public API changed.
- **The Python and Go ports pass contract 1.6** (23.2, Python, and 23.3, Go, below).

### Python semantic retrieval (story 23.2)

- **The Python port passes contract 1.6.** `use_agent_experience(..., embedder=...)` takes your embedding callable (an
  object with `model_id`, `dimension` and a sync or async `embed(text)`; no model is a dependency). With one, injection
  runs the text and the vector channel together and merges them as contract 1.6 says, then ranks with the unchanged
  ranking, so a reworded query finds a lesson it shares no words with. Without one, or when the vector channel fails,
  it answers from text alone and reports why: `NotConfigured`, `ProviderUnavailable`, `ModelMismatch`,
  `DimensionMismatch` or `VectorSearchFailed`, through `on_vector_fallback` and `InjectionResult.vector_fallback`. The
  vector channel never fails the call and is bounded by `vector_timeout` (0.5 seconds by default); the text channel's
  failure still fails it. `min_vector_relevance` drops weak vector candidates, never text ones.
- **Stores.** `InMemoryExperienceStore` and `PostgresExperienceStore` (PostgreSQL with pgvector, running the contract's
  two new SQL files as written) both serve the channel through `write_embedding` and `search_vectors`. After a record is
  stored, its summary's embedding is written in the background, outside the record's own commit; a failure is reported
  to `on_capture_failure` as the new stage `Index` and never fails capture.
- Without an embedder nothing changes. The Python test suite now needs a pgvector image (`pgvector/pgvector`) for its
  PostgreSQL leg.

### Go semantic retrieval (story 23.3)

- **The Go port passes contract 1.6.** `maf.Options.Embedder` takes your embedding object (an `ae.Embedder`:
  `ModelID`, `Dimension` and `Embed(ctx, text)`; no model is a dependency). With one, injection runs the text and the
  vector channel together and merges them as contract 1.6 says, then ranks with the unchanged `Rank`, so a reworded
  query finds a lesson it shares no words with. Without one, or when the vector channel fails, it answers from text
  alone and reports why: `NotConfigured`, `ProviderUnavailable`, `ModelMismatch`, `DimensionMismatch` or
  `VectorSearchFailed`, through `OnVectorFallback` and `InjectionResult.VectorFallback`. The vector channel never fails
  the call and is bounded by `VectorTimeout` (500 ms by default); the text channel's failure still fails it.
  Embedder and vector-store calls are bounded too: at most 16 in flight, abandoned ones included, so a hung provider
  cannot starve the text search. `MinVectorRelevance` drops weak vector candidates, never text ones.
- **Stores.** `inmemory.Store` and `postgres.Store` (PostgreSQL with pgvector, running the contract's two new SQL files
  as written) both serve the channel through `WriteEmbedding` and `SearchVectors` (the new optional `ae.EmbeddingIndex`).
  After a record is stored, its summary's embedding is written in the background, outside the record's own commit; a
  failure is reported to `OnCaptureFailure` as the new stage `Index` and never fails capture
  (`maf.WaitForIndexWrites` awaits the writes still running).
- Without an embedder nothing changes. The Go PostgreSQL tests now need a pgvector image (`pgvector/pgvector`). All
  three ports (.NET, Python, Go) now pass contract 1.6.

## 0.1.0-preview.14

This is the first preview that publishes the Python and Go ports. The same tag now releases the six NuGet packages, the
Python distribution `agenticexperience` (PyPI) and the five Go modules, all at one version. For .NET users it adds the
last-injected instant to the lessons view (database migration `0026`; apply it with the usual migrator), makes the
one-call setup see the chat history and a run's own tools exactly as the explicit wiring does, and adds contract 1.3
to 1.5 fixtures that the ports pass. Everything else below is port hardening and release plumbing.

### Contract 1.5: erased records are never read (story 22.10)

- **Contract 1.5** (add-only: every earlier file is unchanged; [what 1.5 adds](contract/README-1.5.md)) adds a
  PostgreSQL fixture family, `erased`, generated from .NET: a record erased through `purge_experience_record` is never
  returned by the search (the next match takes its place), is not found by a read by ID, and gets no row when an
  injection names it. The .NET store and the contract statements run as written must both give it; the Python and Go
  ports pass it on their PostgreSQL legs and list 1.0 to 1.5 as supported. No library behaviour changed.

### Contract 1.4: the Python and Go ports record injections (story 22.9)

- **Contract 1.4** (add-only: every earlier file is unchanged; [what 1.4 adds](contract/README-1.4.md)) publishes the
  statement the .NET store runs to record an injected block, `contract/sql/record-injection.sql` (live records in
  exactly the injecting scope only, `FOR KEY SHARE` in ID order, the instant clamped to `clock_timestamp()` and only
  moved forward, the count saturating), and the application role's grants on `agent_experience.experience_injections`,
  `contract/sql/application-role-privileges-1.4.sql`, applied after the 1.0 file. The .NET store's statement is that
  file's, line for line, and a contract test keeps the .NET privileges call equal to the 1.0 and 1.4 files together.
  A new PostgreSQL fixture family, `record-injection`, generated from .NET, pins repeats, earlier instants, the scope,
  the clamp, saturation and erasure; the .NET store and the contract statement run as written must both leave it.
- **The Python and Go ports record their injections**, so a lesson only a Python or Go agent uses no longer reads
  "never" in the .NET lessons view. Each PostgreSQL store gains the record call (Python `record_injection`, Go
  `RecordInjection`), running the embedded contract statement; each in-memory store tracks injections too. Each MAF
  integration records after the block is handed over, in the background (Python: a task on the event loop,
  `wait_for_injection_records()`; Go: a goroutine, `ContextProviderOf(&config).Close(ctx)`), chunked at 200 IDs,
  bounded by `injection_record_timeout` / `InjectionRecordTimeout` (5 seconds), fail-open, and reported as `Recorded`,
  `NotTracked`, `NotRecorded` or `OutcomeUnknown` through `on_injection_recorded` / `OnInjectionRecorded`. Both ports
  list 1.0 to 1.4 as supported.
- **The cross-language proof covers injections.** The Python and Go agents' rows files now include the injection
  their second run recorded; the .NET contract tests insert it, read it through the .NET lessons view, and compare
  every column with what the .NET store records for the same injection.
- **The lessons page drops its ".NET agents only" note**: `ExperienceLessonsHtml.DotNetOnlyNote`, unreleased since
  21.8, is removed.
- **Privileges order.** In a mixed fleet, apply the 1.0 then the 1.4 contract privileges files, or the .NET call
  ([deployment](docs/guide/deployment.md#seeing-what-it-learned)). With the 1.0 file alone every agent still injects,
  and each injection is reported `NotRecorded`.

### Changed: the single-call wiring sees the chat history and a run's own tools

- **`UseAgentExperience` alone now injects over a `ChatClientAgent` exactly as the provider on the agent's options
  does.** At the agent level the provider now reads the chat history the agent replays, the way the agent reads it
  (its `ChatHistoryProvider`, or one a run passes; none under a conversation ID or per-service-call persistence), so a
  short follow-up such as "and close it" is joined with the earlier turn's request in the derived task text. It also
  sees the tools a run adds through `ChatClientAgentRunOptions`, followed by the agent's default tools, so with
  `ReceivingAgentCapabilities.UseRunTools` a lesson whose approach calls a run-only tool is no longer withheld. The
  derived text and the block equal the explicit wiring's, byte for byte, on MAF 1.22.0 and 1.24.0; a new parity
  suite runs both wirings over the same scenarios (follow-ups with and without a session, conversation IDs,
  per-service-call persistence, passed history providers, a reducer, a continued background response, run-only tools,
  run middleware, streaming). A run that continues a background response now gets no injection at the agent level,
  as on the options. Behaviour change: an agent wired this way may now get a lesson for a follow-up, or a
  run-only-tool lesson, it did not get before. The explicit wiring is unchanged.
- How the history is read: the default `InMemoryChatHistoryProvider` without side effects, from its stored messages,
  so its reducer is not run twice; with a reducer that runs before retrieval and drops the earlier turn, or an output
  filter, the join reads the history as stored rather than the model's view. Any other history provider is asked as
  the agent asks it, so it is read once more per run, bounded at 500 milliseconds and by the caller's cancellation. A
  read that fails at the agent level only joins nothing; it adds no new failure to the run, and is reported on the
  `inject` span as `agentexperience.history_read`. Messages or tools another context provider on the agent's options
  adds are still not seen at the agent level, and an agent that is not a `ChatClientAgent` keeps today's behaviour.
  Asking a custom history provider uses MAF's `[Experimental]` `ChatHistoryProvider.InvokingContext` constructor,
  under the one `MAAI001` suppression in the library ([compatibility evidence](docs/compatibility-evidence.md)). No
  public API changed.

### Added: Python and Go packages are published with each release

- **Python and Go packages are published with each release**, from the same tag and the same verified commit as the
  six NuGet packages, at the same version. The Python distribution `agenticexperience` goes to PyPI, through PyPI
  Trusted Publishing, as the PEP 440 alpha of the NuGet version (`0.1.0-preview.N` is `0.1.0aN`):
  `pip install "agenticexperience[maf]"` or `"agenticexperience[postgres]"`. The five Go modules
  (`github.com/fabbrik/AgentExperience.NET/go`, `.../go/inmemory`, `.../go/postgres`, `.../go/postgres/pgtest` and
  `.../go/maf`) are tagged `go/v0.1.0-preview.N`, `go/maf/v0.1.0-preview.N`, and so on:
  `go get github.com/fabbrik/AgentExperience.NET/go/maf@latest`. The name is final: the PyPI distribution is
  `agenticexperience` (PyPI refuses `agentexperience` as too similar to the unrelated `agent-experience`), the Python import
  name stays `agentexperience`, and the Go module paths stay under this repository.
- The release workflow's verify job also builds and checks the Python sdist and wheel and runs the Python and Go
  tests on the tagged commit, and fails when `python/pyproject.toml` or a Go module's requirement on its siblings
  is not the version `Directory.Build.props` sets; a release test holds the same. Nothing in the six NuGet packages
  changed.

### Added: the lessons view shows when each lesson was last injected (story 21.8)

- **`ExperienceLesson.LastInjectedAt` and `InjectionCount`**, a field on each lesson, and
  `ExperienceLessonsPage.InjectionsTracked`. The field has three states: when an agent in the scope was last shown the
  lesson (and how many injected blocks delivered it); never; or not tracked, when the store cannot say.
  `ExperienceLessonsHtml` shows it on every lesson ("last injected 2026-10-14 09:30:00 UTC (3 times)", "never", "not
  tracked"). A lesson never injected is the first thing to look at: a dead lesson, or a retrieval miss. The Python
  and Go ports record their injections too, since contract 1.4 (above).
- **Where it comes from.** No stored fact answered it reliably: a run's record names what it was exposed to only when
  the run was captured and finalized, inside a payload that is sealed in encrypted mode, and reuse feedback names
  exposures only when a host submits it. So the context provider now records it, **after the block is handed over, in
  the background**: one store write per injected block, bounded by its own timeout of a few seconds, never delaying or
  failing the invocation. The outcome is the new `record_injection` operation in telemetry: `Recorded`, `NotTracked`,
  `NotRecorded`, or `OutcomeUnknown` (a timeout or cancellation, which may have landed).
- **A capability and two port methods, all with default implementations** so existing stores keep compiling:
  `IExperienceRecordStore.TracksInjections`, `RecordInjectionAsync` (counts only records live in exactly the injecting
  scope; clamps the instant to the store's clock; saturates the count) and `QueryWithInjectionsAsync` (the query plus
  each record's `ExperienceInjectionSummary` in one bounded read, on the new `ExperienceRecordQueryResult.Injections`).
  Override all three or none, and forward all three from a decorator: the defaults read as not tracked, and the store
  conformance suite fails a store that overrides only some. A lesson another scope borrows through a grant and injects
  there is counted in neither scope; the owner sees those deliveries in the grant access log. The in-memory and
  PostgreSQL stores implement them.
- **Migration `0026_experience_injections.sql`** creates `agent_experience.experience_injections` (one row per record:
  scope, `last_injected_at`, `injection_count`; no content; a foreign key to the record), a trigger that deletes a
  record's row when it is erased, and the table's row-level security policies, which admit a written row only for a live
  record in exactly its scope. The application role gets `SELECT`, `INSERT` and `UPDATE` on the two moving columns,
  once the table exists. **Re-run `ApplyApplicationRolePrivilegesAsync` after migrating**: until then injections report
  `NotRecorded`, the view reads as not tracked (reported once through the new
  `PostgresExperienceRecordStore.OnInjectionTrackingUnavailable`), and nothing else changes. Contract 1.4 (above)
  publishes the table's statement and grants for the ports.
- **The 20.8 capability gate, end to end on PostgreSQL**: a new test runs the provider over the real candidate source
  and record store with grant auditing on. A borrowed record whose grant shows the approach and whose tool the agent
  lacks takes no slot, the reader's own record backfills it, and the only access row is the search's.

### Contract 1.3, and port hardening (story 22.8)

- **Contract 1.3** (add-only: every 1.0, 1.1 and 1.2 file is unchanged; [what 1.3 adds](contract/README-1.3.md)).
  Two finalize cases, `sub-microsecond-instants` and `sub-microsecond-instants-at-a-second-boundary`, hold instants
  with 100-ns ticks below the microsecond. The finalization instant is truncated, never rounded, to whole
  microseconds before the outcome's, the reflection's, the columns' and the initial event's instants are derived
  from it (`…:59.9999996` becomes `…:59.999999`, never the next second), while the instants the record copies from
  the run keep all seven fractional digits. Nothing pinned this before: every earlier case used whole seconds. A
  search case with an erased (tombstoned) row was not added, because the search fixtures' seed format cannot hold
  one; README-1.3 says why.
- **Python and Go** both pass contract 1.3 (unit and PostgreSQL legs) with no change to either port, and report
  1.0 to 1.3 as supported.
- **The Python "Python writes, .NET reads" proof now reads the raw rows**, as Go's does: the Python PostgreSQL test
  checks the rows its store wrote, every column in PostgreSQL's text form, against
  `python/tests/cross-language/python-agent-rows.json`, and the .NET contract tests insert those rows as they are,
  read them back through the .NET store, check each initial lifecycle event, and compare every column with what the
  .NET store writes for the same record, over every column of both tables. A Python writer that encodes a column
  differently from .NET, or leaves to the database a column .NET writes, now fails there, naming the column, even
  when the Python reader undoes it. The Go proof runs through the same .NET harness and gains the same comparison.
  Both agents' clocks now carry 100-ns ticks below the microsecond, so every instant in those rows goes through
  truncation in each language. Python and Go each gain a test of their store's default `recorded_at` (now, in UTC,
  in whole microseconds).
- **The Python tests are type-checked**: `mypy --strict` covers `python/tests` as well as `src`, locally and in CI.
- **A timing-sensitive .NET test is deterministic.**
  `ExperienceCaptureTests.Background_work_settles_only_after_a_timed_out_close_abandoned_completion_has_finished`
  failed once in CI: under load, the first invocation's own finalization could overrun the test's real 100 ms
  timeout and complete the run a second time. Its timeouts are now manual and fire only when the test fires them.
- The only library change: two private helpers of Core's `ExperienceFinalizationService` (the initial event's target
  status and reason) became internal, so the contract tests derive the initial event as finalization does. No public
  API or behaviour changed.

## 0.1.0-preview.13

For .NET users, this preview carries one behaviour change: capture now refuses a tool-call argument that holds NaN,
an infinity, or a JSON number beyond a double's range (the attempt is `SanitizationRejected`), because such a record
could be stored but never written again. The rest of this release is the repository growing past .NET. A versioned,
language-neutral contract (`contract/`, now 1.2) specifies how a run becomes a stored lesson and how lessons become
the injected block, with fixtures generated from this library, and Python and Go ports implement it with Microsoft
Agent Framework integration, so a lesson learned by an agent in one language is injected into an agent in another.
The Python and Go packages are not published yet; nothing in the six NuGet packages' public API changed.

### Added: Go Agent Framework capture and injection (story 22.6)

- **[`go/maf`](go/README.md#quick-start-with-microsoft-agent-framework)**, a third Go module
  (`.../go/maf`, Go 1.26, the floor of Microsoft Agent Framework for Go, `github.com/microsoft/agent-framework-go`
  v0.1.1): one call, `maf.UseAgentExperience(&config, maf.Options{Store, Identity, Verify, ClassifyToolResult, ...})`,
  wires an `agent.Config` before the agent is built from it (the Go SDK builds an agent once, so the call changes its
  configuration). Capture is agent middleware plus function invocation middleware, both outermost: each invocation,
  streaming or not, is one run of one attempt, with every tool call's arguments, result and error (a returned error by
  its type name only, a panic by its value's type and re-raised, or a returned call the classifier named a class for,
  an invalid class becoming `ToolReportedError`), parallel calls in the order they started. When it ends, `Verify`
  decides and the run is finalized and stored through the core. Injection is a context provider: the contract's task
  text, text search and ranking, and the rendered block as one user-role message just before the new input, stamped
  `AgentExperience.HistoricalReference`; a block the session's history replays is dropped, so each request carries
  one. Nothing returns an error or panics into the agent: failures go to `OnCaptureFailure` and
  `OnInjectionFailure`. As in .NET and Python, a failed or cancelled invocation (a canceled context) ends its run
  `Failed` or `Cancelled` and is still verified, and finalization is bounded by `FinalizationTimeout` (5 seconds) on
  its own clock, never the caller's context. The identity is resolved once per invocation and shared with injection;
  a second call on one configuration is `ErrAlreadyWired`.
- **In the core (still requiring no module):** the verifiers and classifiers with the semantics of .NET's
  `ExperienceVerifiers` and `ExperienceToolResultClassifiers` (`ToolSucceeded`, `Check`, `Approval`, `AllOf`, `AnyOf`,
  `ExitCode`, `HTTPStatus`, their patterns run by the error class's .NET-semantics matcher), the `Identity` an
  invocation runs under, `DevelopmentIdentity` (refused outside Development, Test or Testing:
  `AGENTEXPERIENCE_ENVIRONMENT` first, then .NET's variables), and the `Store` interface both stores satisfy.
- **[`go/inmemory`](go/README.md#modules)**, an in-memory store for development (its own module, Go 1.25, because
  Unicode normalization is `golang.org/x/text`): insert, get and the .NET in-memory text search, honouring scope and
  bounds.
- **Cross-language proof.** The contract's .NET-finalized fixture records, written into PostgreSQL, are injected into
  a Go agent. The other way round, the rows a Go agent writes through the Go PostgreSQL store, every column in
  PostgreSQL's own text form, are checked in (`go/maf/testdata/cross-language/go-agent-rows.json`); the new test-only
  `GoAgentRecordsPostgresRoundTripTests` inserts them as they are, reads every record back unchanged through the .NET
  PostgreSQL store (`go-agent-records.json`), checks each initial event against .NET's derivation, and finds the Go
  lessons through the .NET retrieval. The .NET library itself is unchanged.
- **`go/postgres/pgtest`**, a test-only module (Go 1.26): the PostgreSQL server helper the Go PostgreSQL tests share,
  and the store's PostgreSQL tests, moved there so the store's own module no longer requires testcontainers-go. On Go
  1.26 its dependencies (testcontainers-go v0.44) have no known vulnerability, so govulncheck scans every module with
  no exclusion.
- **`go/maf/examples/quickstart`**, the README quick start with a scripted model: the second run is handed the lesson
  naming `run_tests [failed: exit 3], run_tests [returned]`. A test runs it and compares its output.
- **CI:** the `go` job adds the `inmemory` module (Go 1.25 and later), and the `maf` and `postgres/pgtest` modules
  (Go 1.26 and later; the PostgreSQL legs run these two):
  `go mod tidy`, gofmt, go vet, staticcheck and govulncheck, and the tests with the race detector, and the PostgreSQL
  16 and 18 legs run the cross-language tests. No module is tagged.

### Added: a Go core that passes the contract (story 22.5)

- **[`go/`](go/README.md)**, a Go core (`github.com/fabbrik/AgentExperience.NET/go`, package `agentexperience`, both
  provisional until the project is named) that passes every fixture of [contract](contract/README.md) 1.2, and a
  PostgreSQL store in its own module (`.../go/postgres`). Both are pre-release and untagged. The Agent Framework
  integration comes next.
  - **The core requires no module at all** and needs Go 1.24 or later. It captures and sanitizes a run (refusing a
    number a double cannot hold finitely, as contract 1.2 requires), finalizes it with the verification rule, the
    deterministic reflector and the initial lifecycle event, derives the IDs, and writes and reads the stored record.
    It also renders the injected block, derives the task text, classifies errors, and builds the search parameters
    and the ranking. Tool-call argument numbers are kept exactly as written (`json.Number`), never decoded into a
    `float64`, and every instant is range-checked as .NET's `DateTimeOffset` is.
  - **.NET parity by construction:** lengths and cuts in UTF-16 code units, .NET's whitespace, word characters and
    ordinal ignore-case (Go's regexp `\s` and `\b`, `strings.EqualFold` and `(?i)` differ), .NET's number formats,
    and invalid UTF-8 (a lone surrogate, to .NET) dropped from the task text. The error class is .NET's regular
    expression run by a small backtracking matcher that reuses its memory, because Go's RE2 has no Unicode `\b`.
  - **The store** (pgx v5, Go 1.25 or later) runs on a connection source the host controls (a `*pgxpool.Pool`, a
    connection or a transaction; `Open` builds a pool from a connection string). It writes a finalized record and its
    own initial event as the application role, reads records back in exactly their scope (`ErrSealed` for a sealed
    one), and runs the contract's SQL files as written, embedded from a copy a test keeps identical to
    `contract/sql/`.
  - **Tests:** `go test ./...` in the core runs every fixture family; the store's module validates every record against
    the schema (its ECMA-262 `\s` translated for Go's RE2). With `-tags postgres`, the PostgreSQL leg migrates each
    database with the .NET scripts, writes every finalize fixture through the Go store and reads it back row for row,
    runs every search fixture, and uses the store from many goroutines at once.
- **CI:** a `go` job checks that `go mod tidy` changes nothing, then runs gofmt, go vet, staticcheck, govulncheck and
  the tests with the race detector. The core runs on Go 1.24, both modules on 1.25 and the latest stable, plus
  PostgreSQL 16 and 18 legs that fail rather than skip. It is in CONTRIBUTING's merge-gate list.

### Fixed: capture refuses non-finite numbers (contract 1.2)

- **Behaviour change.** `DefaultSanitizer` now rejects, fail-closed, an allowlisted tool-call argument that is a
  `double`, `float`, `Half`, `NFloat` or `Complex` holding NaN or an infinity, or a `JsonElement` number beyond a
  double's range (such as `1e400`, or an integer literal that rounds past `double.MaxValue`). Capture answers
  `SanitizationRejected` for the attempt, as for a depth or width violation, and stores nothing of it. Such attempts
  were accepted before, and a record holding one could be stored but not written again: a number beyond a double's
  range read back as an infinity that System.Text.Json refuses to write. The reason names the argument's path, never
  its value. A secret-named argument is still redacted without being read, and a number beyond 64 bits that a double
  holds finitely (`1.5e19`) is still kept as written.
- **Limit:** any other value type is still kept as an opaque leaf without being looked into, so a custom struct that
  holds a non-finite number is not detected ([Capture](docs/guide/capture.md#sanitization-happens-at-capture)). A
  host-supplied `ISanitizer` must refuse non-finite numbers itself.
- **[Contract](contract/README.md) 1.2** only adds files: the finalize case `argument-numbers-beyond-double-range`
  and [`contract/README-1.2.md`](contract/README-1.2.md), which moves these numbers out of 1.1's "not pinned" list.
  The case refuses exponent-form and integer-form literals beyond a double's range, including the integer midway
  between `double.MaxValue` and the next power of two, and redacts such numbers under secret names at any depth. NaN
  and the infinities have no fixture, because JSON cannot hold them; the README states the rule. Every 1.0 and 1.1
  file is byte-identical.
- **Python** passes contract 1.2. It already refused NaN and the infinities, and now also refuses an integer that a
  double cannot hold finitely, as .NET does (it kept one before). Its refusal now names the field's path.

### Added: contract 1.1, the edges ports get wrong (story 22.4)

- **[Contract](contract/README.md) 1.1** only adds files. Every 1.0 file, the README included, is byte-identical,
  and what 1.1 adds to the prose is in a new [`contract/README-1.1.md`](contract/README-1.1.md). The lock rule is now
  a unit-tested function: a minor version may only add files, and changing or removing any locked file is a major
  version.
  - **New fixtures, all generated by the .NET library:**
    - an `encoding/` family: instants and durations (out-of-range values, fraction digits, case, padding, leap days,
      the `TimeSpan` range), non-blank check IDs and task IDs (U+0085 is blank), and record IDs derived from scope
      fields that hold lone, reversed or split surrogates or empty strings;
    - `error-class/cases-1.1.json`: whitespace, word characters, joiners and case folding in the regular expression;
    - finalize and inject cases for argument numbers beyond 64 bits and negative zero, for secret names and block
      labels matched ordinally ignoring case, and for the information separators U+001C to U+001F, which are not
      whitespace;
    - two task-text cases for those separators.
  - **Not pinned:** numbers beyond a double's range and non-finite numbers are outside the contract. .NET still
    accepts them at capture; that is a known bug.
- **`contract/sql/application-role-privileges.sql`**: the statements `ApplyApplicationRolePrivilegesAsync` runs on a
  freshly migrated schema, in both modes, kept identical to the method by a test. It is for a port's own test setup
  only.
- **Python** passes contract 1.1:
  - Its PostgreSQL tests apply the privileges file instead of replaying the grants by hand.
  - A stored tool-call argument beyond 64 bits now reads back as a double, as .NET reads it, and one beyond a double's
    range is refused.
  - Instants with more than seven fractional digits are cut to seven, unpadded durations are accepted, and durations
    beyond .NET's `TimeSpan` are refused, all as .NET does.

### Added: Python Agent Framework capture and injection (story 22.3)

- **`agentexperience.maf`** (the new `maf` extra, `agent-framework-core>=1.0.0`, tested on the floor (1.0.0) and the locked version): one call,
  `use_agent_experience(agent, store=..., identity=..., verify=..., classify_tool_result=...)`, wires a Python MAF
  agent the way `UseAgentExperience` wires a .NET one. Capture is agent-run middleware plus function middleware: each
  non-streaming invocation is one run of one attempt, with every tool call's arguments, result and error (a raised
  exception by its type name only, or a returned call the classifier named a class for, an invalid class becoming
  `ToolReportedError`). When it ends, `verify` decides and the run is finalized and stored through the core. Injection
  is a context provider: the contract's task text, text search and ranking, and the rendered block as one user-role
  message stamped `AgentExperience.HistoricalReference`. An agent that already holds the provider gets no second one.
  Nothing raises into the agent: failures go to `on_capture_failure` and `on_injection_failure`. A streaming
  invocation is injected but not captured. As in .NET, a run whose invocation raised or was cancelled ends `Failed` or
  `Cancelled` and is still verified, and finalization is bounded by `finalization_timeout` (5 seconds).
- **Verifiers and classifiers** with the semantics of .NET's `ExperienceVerifiers` and
  `ExperienceToolResultClassifiers`: `tool_succeeded`, `check`, `approval`, `all_of`, `any_of`, `exit_code` and
  `http_status` (dependency-free, in `agentexperience.verifiers` and `agentexperience.classifiers`).
- **`development_identity()`**, which refuses outside Development, Test or Testing by the .NET rule
  (`DOTNET_ENVIRONMENT`, else `ASPNETCORE_ENVIRONMENT`), and **`InMemoryExperienceStore`**, for development with no
  PostgreSQL (insert, get and the in-memory text search, honouring scope and bounds). `AGENTEXPERIENCE_ENVIRONMENT`
  is read before the .NET variables.
- **Cross-language proof.** The contract's .NET-finalized fixture records, written into PostgreSQL, are injected into
  a Python agent; and the records a Python agent writes (`python/tests/cross-language/python-agent-records.json`, which
  the Python tests check the agent writes exactly) read back unchanged through the .NET PostgreSQL store, in the new
  test-only `PythonAgentRecordsPostgresRoundTripTests`: a new .NET test was added, and the .NET library itself is
  unchanged.
- **`python/samples/quickstart.py`**, the README quick start with a scripted model: the second run is handed the lesson
  naming `run_tests [failed: exit 3], run_tests [returned]`. A test runs it and compares its output.
- **CI:** the `python` job installs the `maf` extra, runs the MAF tests again on agent-framework-core 1.0.0, and checks
  that the built wheel's `agentexperience.maf` needs the extra. Not published to PyPI.

### Added: a Python core that passes the contract (story 22.2)

- **[`python/`](python/README.md)**, a pure-Python package (provisional name `agentexperience`, version
  `0.1.0.dev0`, Python 3.10 or later, typed, no runtime dependency) that implements contract 1.0: capture with
  sanitization and the default limits, verification, the deterministic reflector and its screening, the derived IDs,
  finalization into a stored record and its initial lifecycle event, the injected block in both layouts, the task text,
  the error class, and the text search's parameters, safety checks and ranking. A `postgres` extra (psycopg 3) adds a
  store that writes a finalized record with its initial event as the application role, reads records back, and runs
  the contract's SQL files as written.
- **A conformance runner** (`python/tests/test_contract.py`) runs every contract fixture and compares the output
  exactly, validates every record against the schema, and checks `contract/lock.json`. The PostgreSQL tests migrate a
  database with the .NET migrator's own scripts, write each finalize fixture through the store and read it back row for
  row, and run every search fixture.
- **CI:** a `python` job runs ruff, `mypy --strict` and the tests on CPython 3.10 to 3.14, imports the built wheel
  from a clean environment, and runs the PostgreSQL tests on PostgreSQL 16 and 18. Not published to PyPI; no Agent Framework integration yet. No change to the .NET library.

### Added: a language-neutral contract for ports (story 22.1)

- **[`contract/`](contract/README.md), version `1.0`**, states what a Python or Go port must match to share lessons
  with this library, independently of .NET: the stored record (a JSON Schema,
  `contract/schema/experience-record.v1.json`, for the record columns and the `payload_version` 1 payload, with every
  encoding, the default capture limits, the status rules, and how the record, initial event and reflection IDs are
  derived), how a captured run plus a verification becomes a record, how records become the injected block, how the
  task text is derived, the error class, and the text search (`contract/sql/`: the bounds declaration and the
  `agent_experience.search_experience_text` call with named parameters, the column order, and how the rows are checked,
  truncated and ranked).
- **Conformance fixtures** under `contract/fixtures/` (finalize, inject, task-text, search, error-class), generated by
  the new `tests/AgentExperience.Contract.Tests` from the library's own code paths and checked on every build: each
  fixture must be reproduced byte for byte; every stored record in them must validate against the schema and, inserted
  into PostgreSQL the way a port inserts it, read back through the PostgreSQL record store unchanged; and the search
  fixtures run against a schema the migrator applied, with row-level security on.
  `AGENTEXPERIENCE_CONTRACT_FIXTURES_UPDATE=1` rewrites them.
- **Versioning:** `major.minor`. Added cases bump the minor version, a change to anything published bumps the major
  version, and a port claims "contract 1.N". `contract/lock.json` hashes every contract file, and the test source holds
  each published version's lock digest, so a published version cannot change without a reviewed bump.
- Out of scope for contract 1.x: grants, crypto-shredding, provenance signing, vectors, and the lifecycle after
  finalization (reuse evidence, reinforcement, contest, revocation). A port finalizes, injects and searches plaintext
  records in its own scope.
- No change to the library's behaviour or public API. `AgentExperience.Core` and `AgentExperience.Storage.Postgres`
  grant the new test project internal access (`InternalsVisibleTo`), for the error classifier and the payload mapping.

## 0.1.0-preview.12

This preview finishes the work on the first run. If the app already registers an embedding generator, retrieval
becomes semantic on its own (`Embeddings = Auto`), with a minimum similarity so a task with no related lesson is not
shown unrelated ones; the in-memory store gains a development vector index and PostgreSQL gains `.WithPostgresVectors()`.
`AgentExperienceLessons` and `ExperienceLessonsHtml` show what was learned, why each lesson is or is not eligible, and
the block an agent would be shown. The README now opens with a tested five-line setup, and a new page compares the
library with MAF's own memory providers, including when it is the wrong choice. Four types that were public by
accident are now internal (declared below, with migration notes). Behaviour changes are labelled: an app that already
registers an embedding generator now sends task text and lesson summaries to it unless `Embeddings = Off`.

### Breaking: a smaller public surface before 1.0 (story 21.6)

Every public type is a 1.0 compatibility promise. An audit of all 309 public types found four that were public only
by accident; they are now `internal`, so the public surface goes from **309 types to 305**. The library behaves as before;
what changes is how a host reaches the in-memory feedback ledger (below). The
package validation baseline (`0.1.0-preview.11`) reports each one as a removed type (`CP0001`), declared in the
project's `CompatibilitySuppressions.xml` and scoped to exactly these types.

- **Breaking: `AgentExperience.Core.Sanitization.SanitizationFailureException` and `SanitizationFailureReason` are
  internal.** `DefaultSanitizer` threw and caught them inside `SanitizeAsync`; neither ever reached a host. Migration:
  check the returned `SanitizedPayload` for `Decision == SanitizationDecision.Rejected`, as before. Its `Reason` is
  human-readable free text for logs: do not parse it, because no typed failure categories are offered.
- **Breaking: `AgentExperience.Core.Diagnostics.ExperienceOperationErrorClass` is internal.** No public method took or
  returned it; it only named the `error.class` metric and span tag values. Migration: match on the tag strings
  `Cancelled`, `Timeout`, `Infrastructure` and `Unexpected`, which are unchanged and still listed in
  [the telemetry contract](docs/telemetry.md#errorclass).
- **Breaking: `AgentExperience.Storage.InMemory.InMemoryExperienceReuseFeedbackStore` is internal.** Migration:
  register it with `AddAgentExperienceInMemoryStorageForDevelopment()` (or the one-call setup's
  `UseInMemoryStorageForDevelopment()`) and resolve `IExperienceReuseFeedbackStore`. Resolving it goes through that
  registration's environment guard, which `new` did not: a test process that may run under another environment name
  passes `options => options.AllowProductionEnvironment = true`, as the package README's test example now does.
  Pairing the in-memory ledger with a different record store, or wrapping it, by constructing it is no longer
  possible; implement your own `IExperienceReuseFeedbackStore` and register it first, which the registration keeps.
  Its guarantees are now stated on `AddAgentExperienceInMemoryStorageForDevelopment` and in the package README.
- **Kept public on purpose:** `ReuseConfidenceHeuristicEngine`, `ReuseConfidenceHeuristic`, `ConfidenceIndependenceKey`
  and `AttributeMatchEnvironmentScorer`, because a host that decorates the default confidence engine or environment
  scorer needs them; `ExperienceRecordStoreExtensions`, because store implementers use it; and
  `ExperienceVectorSchema`, so a DBA can review the embedding schema's SQL (`GetScript`) before applying it, as with
  `PostgresExperienceRecordSchema.GetScript`.

### Added: see what it learned (story 21.4)

- **`AgentExperienceLessons`**, a read-only view of the lessons stored in one scope, registered by the one-call setup.
  Get it with `provider.GetAgentExperienceLessons()` or from the container. `ListAsync(identity, query?)` returns an
  `ExperienceLessonsPage` of `ExperienceLesson`s, newest first: experience and task IDs, status, confidence, created and
  updated times, the verified checks (evidence kind and result, never the detail text), the block an agent in that scope
  would be shown for the record, and whether retrieval would currently consider it eligible, with a code
  (`ExperienceLessonIneligibility`: `Status`, `BelowConfidenceFloor`, `ModelAuthored`, `ContentUnconfirmed`, `Expired`,
  `OverByteBudget`, `Unrenderable`) and a reason when not. The block is rendered by the container's context provider
  with its injection options, for every status; it is the injected text but for its ranking line (none: ranking depends
  on the task) and its record number (always 1).
- `ListForDevelopmentIdentityAsync(query?)` lists what `UseDevelopmentIdentity()` stored, for the quick start; it throws
  when the setup does not use that identity.
- `ExperienceLessonsQuery` filters by status, sets the limit (1 to 500) and can leave ineligible lessons out; the page's
  `HasMore` says whether more match, and `OutOfScopeDropped` counts records a store answered from another scope, which
  are left out. It reads through `IExperienceRecordStore.QueryAsync` under the caller's own authorization and scope, so
  no grant-borrowed record is listed, and it never writes. A store answer other than `Found` is an empty page carrying
  that outcome; a record that cannot be rendered is listed as `Unrenderable` and the rest still are.
- **`ExperienceLessonsHtml.Render(page)`** returns one self-contained HTML document (inline CSS, no script, no external
  request, light and dark mode, a development-view banner and the scope). Every record value is HTML-encoded and the
  page's content security policy allows no script.
- **No endpoint is mapped by the library**, and the view is development-first by documentation only: nothing enforces
  it. The host maps the view behind its own authorization (the guide's example also maps it only in development and sets
  `Cache-Control: no-store` and anti-framing headers), with the identity from its own authentication; exposing it
  publicly discloses the lessons. See [Seeing what it learned](docs/guide/deployment.md#seeing-what-it-learned).
- Not in this version: when a lesson was last injected (deferred).
- Additive public API only; no new package and no ASP.NET dependency.

### Docs: say why, plainly (story 21.5)

- **README first screen.** The first 60 lines now hold what the library is (memory of what *worked*, verified by your
  own checks, for MAF agents), the quick-start setup (now with the version it needs, `0.1.0-preview.11`, and the
  `Microsoft.Extensions.DependencyInjection` package named) running two tickets so the second gets the first's lesson,
  a shortened copy of the block that second run gets, the measured result table, and a link to the new comparison page.
- **Anchors.** `#what-an-agent-sees` is now that shortened block, from the quick start; the full block, from the
  end-to-end sample, moved to a new `#the-whole-block` section below the first screen. "In production" is a new section
  holding the identity and PostgreSQL setup; `#semantic-retrieval` is now a top-level section.
- **Caveats moved to the guides**, with a pointer left in the README:
  [Starting from the quick start](docs/guide/deployment.md#starting-from-the-quick-start) (previews, packages,
  environments, task ID, one block, failures, argument values),
  [Which lessons a run gets](docs/guide/injection.md#which-lessons-a-run-gets) (matching and the task text) and
  [Finalizing from the MAF adapter](docs/guide/finalization.md#finalizing-from-the-maf-adapter) (what a run is stored
  as). One substantive addition there: a run counts as failed through a non-zero exit code only under the `ExitCode`
  classifier, which the finalization guide now says.
- **[Compared with MAF memory](docs/compared-with-maf-memory.md)** (new): what MAF's session chat history,
  `ChatHistoryMemoryProvider`, `TextSearchProvider` and `FileMemoryProvider`, the Mem0 provider (Python; .NET has only
  an old preview package) and this library each store, what decides it, what reaches the model and what each trusts;
  when to use which, including when this library is the wrong choice (no tools, no checkable outcome, conversational
  personalisation, one-off tasks, needing a non-preview package); and how they compose on one agent.
- Every package README opens with the same sentence and a link to the README quick start; the storage ones say they
  work through the MAF adapter or with Core directly, and the in-memory one starts with "For development and tests only".
- Tests: the README's setup block is compiled and run (`ReadmeQuickStart.cs` in the MAF adapter tests): the second run's
  block must name `run_tests [failed: exit 3], run_tests [returned]`, and the README's shortened block must be cut from
  it, in order, each gap one `…` line. The setup, the shortened block, the result table and the comparison link must sit
  within the first 60 lines (one shared constant), and the end-to-end sample's whole block below them.

### Retrieval: semantic retrieval when the app already has embeddings (story 21.3)

- **Behaviour change: the one-call setup now uses a registered embedding generator.** When the container has a
  `Microsoft.Extensions.AI` `IEmbeddingGenerator<string, Embedding<float>>` (registered before or after
  `AddAgentExperience`; resolved when the setup is first used, per container), the setup adapts it, embeds each committed
  record's sanitized summary at finalization, and makes retrieval hybrid. An app that already registers a generator
  therefore starts sending task texts and lesson summaries to it on upgrade; **`options.Embeddings = Off` is the
  opt-out.** **With no generator, nothing changes**: retrieval is text-only and every golden is unchanged. The generator
  must report its model ID and dimension (`EmbeddingGeneratorMetadata`; OpenAI's needs `AsIEmbeddingGenerator(1536)`). A
  generator that throws or times out makes only that retrieval text-only (`VectorFallback`, `ProviderUnavailable`) and
  that record unindexed (`Indexing`, `ProviderFailed`); no run fails. Records stored earlier, or under another model
  (`ModelMismatch`), match by meaning only after a re-index pass (snippet in the indexing guide).
  - A host's own `IExperienceEmbeddingGenerator` (unkeyed, registered before or after) is used instead of the adapter,
    after the same model ID and dimension check; a host that registered one and an index beside `AddAgentExperience`,
    without `AddAgentExperienceIndexing`, now also gets indexing at finalization. A host's `ExperienceIndexingService`
    or `IExperienceEmbeddingIndex` registered first is kept. The setup registers no service that resolves to null.
- **`AgentExperienceOptions.Embeddings`** (new) and the new enum **`AgentExperienceEmbeddings`**: `Auto` (the default),
  `Off` (text-only whatever is registered, a host's own generator included) or `Required` (building an agent with
  `UseAgentExperience`, or resolving the context provider, throws `InvalidOperationException`, with the cause as the
  inner exception, without a usable generator and an embedding index).
- **`AgentExperienceOptions.MinimumSemanticSimilarity`** (new, default `0.5`): the cosine similarity a vector match
  needs, so an unrelated lesson is not injected just for being the nearest; `-1` turns it off. A text match is kept
  whatever its similarity. It maps to the new **`RetrievalPolicy.MinimumVectorRelevance`** (Core; relevance
  `(1 + cosine) / 2`), which defaults to no floor, so explicit wiring is unchanged.
- **`AgentExperienceOptions.EmbeddingGeneratorKey`** (new): use a keyed generator. Keyed registrations are otherwise
  ignored.
- **`ExperienceCaptureFailureStage.Embeddings`** (new value): under `Auto`, a generator the setup cannot use (the storage
  has no embedding index, the generator reports no model ID or dimension, or resolving the generator or the index threw)
  leaves retrieval text-only and is reported once per container through `OnCaptureFailure`, with no run ID, when an
  agent is built. A resolution that threw is tried again for the next agent built. (No `ILogger` is used: the MAF
  adapter's dependency boundary forbids `Microsoft.Extensions.Logging`.)
- **In-memory embedding index.** `UseInMemoryStorageForDevelopment()` now also registers an internal in-memory
  `IExperienceEmbeddingIndex` (exact cosine search, the PostgreSQL index's scope, eligibility and conditional-write
  rules, the same development-only guard). It takes no new package dependency, and on its own changes nothing: it is
  used only with a generator. `AddAgentExperienceInMemoryStorageForDevelopment` alone still registers none.
- **`.WithPostgresVectors()`** (new, `AgentExperience.Storage.Postgres.Vectors`): registers the pgvector index on the data
  source `UsePostgres` registered. `UsePostgres` alone stays text-only, because `AgentExperience.Storage.Postgres` takes
  no vector dependency; with a generator and no `.WithPostgresVectors()`, the setup reports it once (stage
  `Embeddings`). The vector schema is still applied explicitly; without it, retrieval falls back to text, flagged
  `VectorSearchFailed`, as before.
- The vector index's request validation now lives in the shared validator both stores compile, so the in-memory and
  PostgreSQL indexes refuse exactly the same requests; `AiExperienceEmbeddingGenerator` is shared source too, compiled
  internal into the MAF adapter. Neither changes a public signature.
- Tests: a new embedding-index conformance suite, passed by both indexes (PostgreSQL in plaintext, encrypted and
  row-level security modes); matrix tests of the one-call setup over both storages, with a deterministic test generator
  whose numbers are never reported as quality.
- Benchmark: the retrieval benchmark has an opt-in hybrid column (text vs text plus vectors, in memory, merged by Core's
  own merge rule, with the setup's default floor), run only when
  `AGENTEXPERIENCE_RETRIEVALQUALITY_EMBEDDINGS` names a provider (`openai`, any OpenAI-compatible endpoint, or `azure`);
  otherwise it says it was skipped. CI needs no key, and the checked-in goldens are text-only and unchanged. No
  improvement figure is claimed until a run against a real embedding model is committed.
- Docs: a "Semantic retrieval" section in the README, the one-call path first in the
  [indexing guide](docs/guide/indexing.md#with-the-one-call-setup), and the deployment guide.

## 0.1.0-preview.11

This preview is about the first run. A development setup is now about five lines: `UseDevelopmentIdentity()` replaces
a hand-built identity (and refuses outside Development, Test and Testing), the task ID defaults to the agent's name, and
one `UseAgentExperience()` call wires both capture and injection. Built-in verifiers (`ExperienceVerifiers`) and
classifiers (`ExperienceToolResultClassifiers`) let a run be verified from a tool's own outcome, such as an exit code or an
HTTP status, without writing evidence by hand, and a host can now say that a tool call which returned actually failed
(`ClassifyToolResult`), so lessons stop calling a failed approach `[returned]`. Borrowed lessons are gated before the
record limit too. Two behaviour changes are labelled below: runs with no task ID set now file under the agent's name,
and `UseAgentExperience` now injects as well as captures.

### Verification: built-in verifiers (story 21.2)

- **`ExperienceVerifiers`** (new static class): ready-made `AgentExperienceOptions.Verify` callbacks, each returning
  `Verify`'s exact delegate type, that decide only from facts the host's code produced, never from model text:
  - `ToolSucceeded(toolName, checkId?, artifactRevision?)`: passes when the run's last call to `toolName` (ordinal,
    across all attempts, by sequence) returned, fails when it failed (thrown, or classified by `ClassifyToolResult`),
    and stores nothing when there was no call. Calls are ordered by attempt, then by call sequence within the attempt.
    It never reads a result, so it relies on capture (`ClassifyToolResult`) to mark return-value failures. Evidence kind
    `ToolOutcome`, producer `tool:<toolName>`, check ID `toolName` by default.
  - `Check(checkId, check, artifactRevision?)`: the host's own `bool?` verdict (`null` stores nothing). Kind
    `HostCheck`, producer `host`.
  - `Approval(checkId, approve, artifactRevision?)`: a person's decision, as the new public record
    **`ExperienceApproval(string ApproverId, bool Approved)`** (`null` stores nothing). Kind `HumanApproval`; the
    approver ID is stored verbatim as the producer, so pass an opaque identifier, not an email address. A blank
    approver is reported through the `Verify` failure path and nothing is stored.
  - `AllOf(...)` runs them in order, stopping at the first `null` (then nothing is stored), and returns one
    verification with every check and all the evidence under the one explicit revision (else `unversioned`).
    Duplicate check IDs, a `null` check or two different explicit revisions throw (reported, not stored).
    `AnyOf(...)` returns the first verification that passed (every required check has `Pass` evidence of an accepted
    kind and none other), else the first one returned, else `null`.
  - `artifactRevision` defaults to **`ExperienceVerifiers.UnversionedRevision`** (`"unversioned"`); pass a real build ID
    or commit when the artifact changes between runs.
- **`ExperienceToolResultClassifiers`** (new static class): ready-made `ClassifyToolResult` functions returning
  single error-class tokens, for the named tools only (`ExitCode("run_tests")`) or, with no names, every tool.
  `ExitCode(...)` returns `exit N` for a non-zero exit code (`exit=N`, `exit N`, `exit code N`, `exit status N`,
  `exited with code N`, `returncode=N`, and the JSON `exitCode`, `exit_code`, `returnCode`, `return_code`); a non-zero
  code no token can carry (more than six digits, such as a Windows status code, or not an integer) is
  `ToolReportedError`. `HttpStatus(...)` returns `HTTP N` for a status from 400 to 599 (a leading `HTTP N` or
  `status N`, or the JSON `status`, `statusCode`, `status_code`). Both read strings, `TextContent`, `JsonElement`s and
  other objects serialized as capture serializes them. A JSON object is read by its top-level properties only, whatever
  its size; a JSON array names no code; in other text the last match counts.
- Docs: the README quick start now verifies with `ExperienceVerifiers.ToolSucceeded("run_tests")` and the
  `ExitCode("run_tests")` classifier, while the runnable quick-start sample keeps its own hand-written verifier (its
  output is unchanged). The deployment guide has a [Built-in verifiers](docs/guide/deployment.md#built-in-verifiers)
  section, and the capture guide describes the classifiers.

### Setup: a five-line first run (story 21.1)

- **Behaviour change: the task ID defaults to the agent's name.** Under the one-call setup, when neither
  `AgentExperienceOptions.TaskId` nor `ResolveTaskId` is set, a run is now captured under the invoked agent's `Name`,
  reduced to an identifier (lower case; ASCII letters, digits, `.` and `_` kept; every run of other characters, dashes
  included, one `-`; at most 100 characters, never fusing two words at the cut: `"Triage - Bot"` becomes `triage-bot`),
  instead of `"default"`. An unnamed agent, or one whose name keeps nothing, still files under
  `AgentExperienceOptions.DefaultTaskId` (`"default"`). A host that set neither and named its agent now files new runs
  under the agent's name; records stored under `"default"` are unchanged and still found by retrieval, which matches on
  text, but `ReuseEvidence = SameTask` no longer pairs them with new runs. Set
  `options.TaskId = AgentExperienceOptions.DefaultTaskId` to keep the old behaviour. Reading `TaskId` while it is unset
  still returns `"default"`, although runs use the agent's name; and distinct names can reduce to the same identifier
  (`"Triage Bot"` and `"triage-bot"`), so their runs share it. A host with several agents should set `TaskId` or
  `ResolveTaskId` explicitly.
- **`AgentExperienceOptions.UseDevelopmentIdentity(string? tenant = null)`** (new): sets `ResolveIdentity` to one fixed
  identity for development and tests: tenant `local` (or the one given), an application and a project named after the
  host environment's `ApplicationName`, else the entry assembly (reduced the same way, `app` when there is none), and
  the principal `local-developer` with no roles, issued at each invocation by the configured clock.
  It refuses outside `Development`, `Test` or `Testing` by exactly the in-memory store's rule (one shared source file):
  building an agent with `UseAgentExperience` or resolving the context provider throws `InvalidOperationException`
  naming the rule, and under a Generic Host a hosted service refuses to start. There is no override. Setting `ResolveIdentity` afterwards replaces it, check included.
- **Behaviour change: `UseAgentExperience(services)` now wires injection too.** When the agent was not given an
  `ExperienceContextProvider` on `ChatClientAgentOptions.AIContextProviders`, the container's provider is added at the
  agent level, through MAF's `AIAgentBuilder.UseAIContextProviders`, inside capture: the same block, the same session
  account. An agent that already holds one (the two-line wiring) is left exactly as before and gets exactly one block.
  At the agent level the provider does not see the chat history a `ChatClientAgent` replays (a short follow-up is not
  joined with an earlier turn's request) or the tools a run adds through `ChatClientAgentRunOptions`: with
  `ReceivingAgentCapabilities.UseRunTools` the gate reads the agent's default tools, and withholds a record whose
  approach calls a run-only tool. Give the agent the provider on its options as well where either matters. A host
  that used `UseAgentExperience` for capture only, with no provider on the agent, now gets lessons injected; set the
  new **`AgentExperienceOptions.InjectWithUseAgentExperience`** (default `true`) to `false` to keep it capture-only.
  A second `UseAgentExperience` on the same pipeline adds no second provider. With an inner agent that is not a
  `ChatClientAgent`, the gate reads the tools the agent reports through `GetService<ChatOptions>()`, if any.
- The MAF adapter now depends on `Microsoft.Extensions.Hosting.Abstractions` (floor 10.0.3, the in-memory store's),
  for the `IHostEnvironment` the development identity's guard reads.
- Docs: the README quick start and [the one-call setup](docs/guide/deployment.md#the-one-call-setup) use
  `UseDevelopmentIdentity()` and the single `UseAgentExperience` line, with the production identity next to them. The
  quick-start sample uses the same wiring; its output is unchanged.

### Capture: let a host say a returned tool call failed (story 20.9)

- **`ExperienceCaptureOptions.ClassifyToolResult`** (new, default `null`): a
  `Func<ExperienceToolResultContext, string?>` that runs in the function middleware after a tool *returned*. `null`
  records the call as returned, as before; an error class records it as failed, with `ToolCallRecord.Error` set to the
  class and `Result` still the text the model saw. The class then flows through the paths a thrown exception's type
  name already takes: the claims-v3 provenance signature, the call's `[failed: <class>]` marker on `Tried:` lines, and
  the default and model-backed reflectors' input. A tool that reports failure in its result (an exit code, `{"ok":false}`)
  no longer has to throw to be marked failed.
- **`ExperienceToolResultContext`** (new public sealed class with `required init` properties): the call's
  `ToolCallId` (which tells concurrent calls apart), the tool's registered `ToolName`, a read-only view of the captured
  `Arguments`, and the raw `Result` before text conversion. Nothing else reaches the classifier. The arguments are raw,
  before sanitization, so a classifier must not log them; it must not change `Result` or argument values either.
- **`ExperienceCaptureFailureStage.ToolResultClassification`** (new enum member, appended): classifier problems are
  reported there, so they never hide a later `ToolCall` report of a call that could not be recorded.
- **The class must be one token a `Tried:` line can show:** a type-like name ending in `Error` or `Exception`
  (`LockHeldError`, `ExitCodeError`) or `exit N` (`exit 3`); `HTTP N`, `status N`, errno names and `Timeout` also
  qualify. It must be exactly the first token the `Tried:` line's classifier finds in it, at most 40 characters.
  Anything else is recorded as **`ExperienceCaptureOptions.ToolReportedErrorClass`** (new constant,
  `"ToolReportedError"`, rendered `[failed: ToolReportedError]`) and reported once per run through `OnCaptureFailure`
  at stage `ToolResultClassification`, with a fixed reason. Surrounding white space is trimmed; case and form are not
  forgiven (`exit 3`, not `Exit 3`). A classifier that throws leaves the call recorded as returned and is reported at
  stage `ToolResultClassification`: `Reason` names only the exception type, `Exception` carries the object. It runs
  inline before the result goes back to the model (keep it fast; its time is not counted in the call's `Duration`),
  never replaces the result, and is not called for a tool that throws or a call finishing after its run was finalized.
- With no classifier, captured runs, records, blocks and signatures are unchanged. No change to `ToolCallRecord`,
  storage, the signing encoding or the `Tried:` line's tokenizer. The quick-start sample's tool still throws.
- Docs: [Capture](docs/guide/capture.md#tools-that-report-failure-in-their-result) shows a classifier for a non-zero
  exit code; [Injection](docs/guide/injection.md#what-each-attempt-tried-and-what-worked) points to it where the
  `[returned]` marker is described.

### Injection: gate borrowed lessons before the record limit (story 20.8)

- **`ExperienceCandidate.GrantDisclosure`** (new init-only property, default `null`): the level of the grant
  the candidate source's own predicate matched a borrowed record under, set only alongside `SharedByGrant`. The
  PostgreSQL text search (both its own statement and the row-level-security function) and the vector index set it
  from the permitting-grant column they already read for access rows; no migration, and the SQL function is unchanged.
  The in-memory sources never share and leave it `null`. A consumer treats `null` on a shared candidate, or a value it
  does not define, as `LessonOnly`.
- **`RankedExperience.MatchedGrantDisclosure`** (new init-only property, default `null`): retrieval copies the
  candidate's level onto it for shared records only; a record both channels found keeps the level that came with the
  `SharedByGrant` it keeps (the text channel's candidate, when both found it). The injection re-read clears it when it
  reports the record no longer shared. `RankedExperience.GrantDisclosure` keeps its meaning (`null` from retrieval,
  filled by the pre-injection re-read), and rendering, the host decision and the audit trail still read only that.
- Both new properties are declared in the record body, not as constructor or `Deconstruct` parameters, so the
  constructors and `Deconstruct` methods 0.1.0-preview.10 shipped are unchanged and code compiled against it still
  runs. Like every record property, they take part in record equality: two candidates or ranked records that differ
  only in the level are no longer equal.
- **The capability gate checks borrowed records before the limit.** With `ReceivingAgent` set, a borrowed record
  matched under `LessonAndApproach` or `LessonApproachAndArguments` is now tool- and risk-checked on its ranked
  candidate, like an owned one. Under `LessonOnly`, or with no or an unknown matched level, it still passes unprobed and
  is decided on its re-read. A record gated before the limit is never re-read, so that omission is final: if its grant
  was revoked and reissued as `LessonOnly` between the search and the re-read (a level cannot change in place), it is
  still dropped, although the re-read would have injected it with the approach withheld.
- **Behaviour change for hosts that share records and set a gate:** a borrowed record matched under an
  approach-showing grant whose working tools the agent lacks no longer takes a slot; the next eligible record fills
  it. It is no longer re-read, so there is no delivery (re-read) grant access row (the search-time row a PostgreSQL
  search wrote stays), and it is omitted as
  `ToolUnavailable` or `RiskClassExceeded` before the limit. Without a gate, or with only owned records, blocks,
  omissions and outcomes are unchanged.
- Tests close two seams 20.7 left untested: a gate that throws before the limit fails the invocation exactly as one
  that throws on the re-read (`Failed`, the same failure reason), and, with a real `ChatClientAgent` and `UseRunTools`,
  a tool added by an `AIContextProvider` registered before the experience provider is seen while one registered after
  it is not.

## 0.1.0-preview.10

This preview makes lessons say more precisely what worked, makes sure the lessons a model is shown are ones it can act
on, and adds an experiment that asks whether a lesson helps on a *different* system. Each call on a `Tried:` line now
carries its own outcome (`[returned]`, `[failed: <class>]`), so a model can tell the failed approach from the working
one inside a single attempt. Provenance signing moves to claims version 3, which also covers the error text those
markers come from. The receiving-agent capability gate now runs before the record limit, and can use the run's own
tools (`UseRunTools`), so lessons whose working approach needs a tool the agent lacks no longer take its slots. The
transfer experiment (pre-registered, offline-tested, no live results yet) found that problem before any user did.

### Injection: only lessons the agent can act on (story 20.7)

- **The capability gate runs before the record limit.** With `ExperienceInjectionOptions.ReceivingAgent` set, the
  provider now applies the tool and risk checks (unchanged semantics) to the ranked candidates before taking
  `Limits.MaxRecords`, so a gated record takes no slot and a lower-ranked record the agent can act on fills it. The
  re-read record is still checked again before `DecideInjection`. While a gate is configured, a request that names its
  own `Limit` asks retrieval for `MaxRecords × ReceivingAgentCapabilities.GatedRetrievalWindowMultiplier` (new init
  property, default 4, at least 1) records, capped by the policy's candidate limit: up to that many times the retrieval
  cost and latency. A gate that can reject nothing widens nothing. The request's own `Limit` still caps what is
  injected, and backfill fetched past it and not used is not reported. Gated records are still omitted as
  `ToolUnavailable` or `RiskClassExceeded`, with no detail. A borrowed record is still decided only on its re-read,
  because a ranked candidate does not carry its grant's disclosure level. The decision on the ranked revision is final
  (a newer revision that would pass stays out), and a record that fails only the re-read gate leaves its slot empty.
- **Behaviour change for hosts that already set `AvailableTools` (or `MaxRiskClass`):** gated records no longer consume
  record slots, so a block can now hold records that previously fell past the limit as `OverRecordLimit`, and a
  gated owned record is no longer re-read (so no longer appears in that re-read's batch), and a gated record past the
  session's record budget now reports `ToolUnavailable`/`RiskClassExceeded` rather than `OverSessionBudget`. Without
  `ReceivingAgent`, blocks, omissions and outcomes are byte-identical.
- **`ReceivingAgentCapabilities.UseRunTools`** (default `false`): check approach tools against the invocation's own
  tools as MAF hands them to the provider (`AIContext.Tools`: a `ChatClientAgent`'s default tools plus the run's
  `ChatClientAgentRunOptions` tools, proven by a test with a real `ChatClientAgent`), intersected with `AvailableTools`
  when that is set too. Taken once per invocation, ordinal, blank names ignored; a run with no tools has an empty set.
  Trade-off: a tool exposed under another name (an MCP prefix, a rename) does not match, so memory is withheld
  silently except for the `ToolUnavailable` omission. Tools added by context providers that run after this one, by
  function-invocation middleware (`AdditionalTools`), or hosted server-side are not seen, and their records are
  withheld: register the provider after any provider that adds tools.
- **Transfer experiment, amendment 1** (before any live run): the transfer harness turns on `UseRunTools`, as a real
  host would. The offline scripted run moves from the same-cluster lesson injected in 0 of 12 blocks
  (`TransferNoDemonstratedBenefit`) to 12 of 12, first in 11 (`TransferBenefitAttributableToContent`); no distractor
  takes a slot -- because the gate removes all 24 distractors, not because retrieval ranks better. Made after, and
  because of, the registered offline outcome; no live result existed. A property of the scripted operator, not a model
  result. The 9.1 experiment is unchanged.

### Experiments: a transfer experiment, pre-registered (story 20.4)

- **`experiments/AgentExperience.LiveReuse` gains a second experiment, `--experiment transfer`**: a lesson learned on
  one service has to be found by the library's own retrieval (`InMemoryExperienceRecordStore`,
  `InMemoryExperienceCandidateSource`, `RetrievalPolicy.Default with { Timeout = 15 s }`, the shipped
  `ExperienceContextProvider`) in one shared store that also holds other clusters' lessons and 24 distractors (cache
  flushes and config rollouts), and has to help on an unseen service that shares only its cluster. Four conditions
  (memory disabled, enabled, placebo, and a mismatched-trait control without the same-cluster lesson), the first
  experiment's gate, `Transfer…` verdicts, its own pre-registration (`preregistration.transfer.json`, with trait and
  task-text digests), ledger (`results/transfer-ledger.tsv`) and report prefix (`transfer-`). No live run has been made.
- **Offline result, golden-tested:** with the texts as written, the config-rollout tickets, worded like the migration
  tickets, fill all 8 slots of every memory-enabled block, so the same-cluster lesson is never injected and the scripted
  run concludes `TransferNoDemonstratedBenefit` (2.50 mean failed attempts in every condition). Reported, not tuned.
- The first experiment's behaviour, pre-registration, ledger and reports are unchanged. No library change.

### Provenance signing covers what the `Tried:` line shows (story 20.6)

- **Claims version 3, signed by default.** With provenance signing configured, finalization now signs claims version 3
  (`aexp-prov:v3`, algorithm `HMAC-SHA256.aexp-prov.v3`, new constant
  `ExperienceProvenanceSignature.HmacSha256ClaimsV3`): the version 2 claims, with a content digest that also covers
  every attempt's and every tool call's `Error`. So the error classes on a `Tried:` line and each call's
  `[returned]`/`[failed: …]` marker are signed: flipping which call failed, or rewriting an error's class or text,
  leaves the record's content unconfirmed, and it is fenced or excluded as model-authored (`UnconfirmedContent`). The
  version 3 content encoding is the version 2 one with the errors added where the attempts are encoded; every other
  byte is unchanged.
- **Version 1 and 2 signatures verify exactly as before.** A version 2 signature still confirms its record's content,
  so upgrading fences nothing, and nothing is re-signed. **The error text of a version 2 record is unsigned:** a party
  that can write the store can change its error classes and per-call markers without breaking the signature.
- **Rolling deploys.** `SignClaimsVersion` now accepts 1, 2 or 3 (default 3; anything else throws
  `ArgumentOutOfRangeException`). Nodes on `0.1.0-preview.7` to `0.1.0-preview.9` refuse a version 3 signature as one
  they cannot check, so sign 2 until every node runs this build, then 3.
- **Rolling back** to `0.1.0-preview.7`–`0.1.0-preview.9` after signing version 3: those builds refuse a version 3
  signature, so on them every record signed version 3 has unconfirmed content (fenced or excluded as model-authored)
  and its run vouches for nothing in independence verification. Records signed version 2 are unaffected. To keep a
  rollback open, sign 2 until you no longer need it.
- **Error text must have a canonical encoding.** Version 3 signs every attempt's and tool call's error text, so a lone
  surrogate there (which a custom `ISanitizer` can pass through) now ends finalization `Failed` at the create stage,
  with nothing stored, as one in any other signed text already did.
- **No migration.** The stores hold the algorithm as free text; nothing in either store changes.

### Each call on a `Tried:` line says how it ended (story 20.3)

- **Behaviour change: per-call outcome markers.** On a record in the reader's own scope, every call on a `Tried:` line
  is now followed by its own outcome, taken from the call's captured error: `[returned]`, or `[failed: <class>]` (only
  the first token of the error's class by the same rule as the attempt's, so a marker is bounded and holds no comma;
  never an excerpt, even under `FailureDetail = Excerpt`; `[failed]` under `None`). `[returned]` means the tool
  returned rather than threw, not that the call achieved anything. An agent that failed and then succeeded inside one attempt now reads
  `run_refund_check(strategy="retry-immediately") [failed: InvalidOperationException], run_refund_check(strategy="wait-for-lock") [returned] → completed`
  instead of having to guess which call worked. The attempt's own ` → completed`/` → failed (…)` suffix is unchanged.
  A call counts as failed only when the tool throws; a tool that reports failure in its result is marked `[returned]`.
  Blocks grow by the markers' bytes (the three-record fixture: 1,886 to 1,968 bytes compact, 4,368 to 4,450 verbose),
  under the same budgets and clamps.
- **Hosts that parse `Tried:` lines:** each call now ends with ` [marker]` before the next `, ` or the ` → ` outcome;
  match it with `HistoricalReferenceWriter.CallReturned`, `CallFailed` and `CallFailedClassPrefix` (closed by
  `CallMarkerEnd`) rather than literal text.
- **Borrowed records keep their layout.** A record shown through a grant has no per-call markers: the grant consents
  to the working attempt's calls, not to failure classes. It renders as before except for the bracket mapping below.
- **A tool name cannot spell a marker.** Square brackets in a tool name, and the look-alikes `［` `］`, `⟦` `⟧` and
  `【` `】`, now render as `(` and `)`, as `→` and `,` were already neutralized, on every record's line. A written name
  can still carry the library's own bracketed marks: `[delimiter removed]` and the `[...]` clamp mark.
- **Signed under claims version 3.** Provenance signing's claims version 2 encodes only whether each attempt failed,
  so on a version 2 record the error classes and per-call markers are unsigned. Claims version 3 (story 20.6, below)
  covers them, and finalization signs it by default.
- **New constants** on `HistoricalReferenceWriter`: `CallReturned`, `CallFailed`, `CallFailedClassPrefix` and
  `CallMarkerEnd`.
- **Quick start.** The demo's tool now throws on a non-zero exit, so its failed call is recorded as failed, and its
  stand-in model picks the call marked `[returned]` rather than the last call on the line.
- **Decided, not built:** allowlisted argument values still never go into reflector input or stored lesson text. Text
  stored at reflection time under the owner's allowlist would bypass a `LessonOnly` grant and a reader's narrower
  `ApproachArguments`; values reach readers only through the grant-aware `Tried:` line.

## 0.1.0-preview.9

This preview makes text retrieval find lessons for requests written the way people write them. Until now a stored
lesson was returned only when it contained every word of the request, so a paraphrased or longer request found nothing.
A lesson is now a candidate when it shares at least three of the request's terms (`MinimumMatchedTerms`, capped at the
request's term count), ranked by how much of the request it covers. On the new model-free retrieval benchmark, recall@8
rises from 0.378 to 0.700 in memory and from 0.511 to 0.844 on PostgreSQL, with under one unexpected lesson per
injection. The preview also adds a one-minute quick-start demo and the benchmark itself.

Upgrading from `0.1.0-preview.8` applies migration `0025`, which replaces `search_experience_text`. Run the migrator
before deploying the new build and, **with row-level security on, re-run the privileges call after migrating**: until
then text search runs without the function and its GIN index, as it does when the function is not granted. This is a
**behaviour change**, labelled below: requests now match on some of their terms, and on PostgreSQL every full match
reports relevance 1. To keep the old all-terms matching, set `RetrievalPolicy.MinimumMatchedTerms` to
`ExperienceCandidateQuery.AllTerms`. There are no breaking API changes; the additions are listed below.

### Text search matches on any term and ranks by coverage (story 20.2)

- **Behaviour change: a record no longer needs every query term.** Both lexical candidate sources (in-memory and
  PostgreSQL, including `search_experience_text` under row-level security) return a record when it contains at least
  `ExperienceCandidateQuery.MinimumMatchedTerms` distinct query terms, capped at the query's term count so a one-word
  query still matches. The default is **3**, chosen by a rule fixed before the benchmark ran: the value with the highest
  overall recall@8 on PostgreSQL whose false positives in the first 8 stay at most 1.0 on both adapters (1 and 2 gave
  5.7 to 6.2 and 1.4 to 2.3 false positives). On the story 20.1 benchmark, recall@8 rises from 0.378 to 0.700 in
  memory and from 0.511 to 0.844 on PostgreSQL, false positives in the first 8 are 0.417 and 0.521, and the realistic
  requests that found nothing now find their record (8 of 10 in memory, 10 of 10 on PostgreSQL). See
  [Retrieval quality](docs/benchmarks.md#retrieval-quality-story-201).
- **Ranking.** Candidates are ranked by how much of the query they cover. In memory, relevance is the existing
  field-weighted fraction (summary 3, task ID 1, lesson 1, out of 5 per query term), so a record containing every term
  keeps exactly the relevance and source order it had. On PostgreSQL, relevance is now the share of the query's
  lexemes the record contains, in (0, 1], with `ts_rank_cd` and then the experience ID breaking ties; it was
  `ts_rank_cd` alone, in [0, 1). The `ts_rank_cd` tie-break ranks a full match against the AND of the terms, so for a
  plain-word request (no quotes, `-word`, `or`, hyphenated compounds or repeated words, which `websearch_to_tsquery`
  read as phrases, negation, OR, or kept) the source returns full matches in their old order. Fields are not weighted
  on PostgreSQL yet (that needs weighted vectors and a backfill).
- **What Core sees.** Core's retrieval score uses this relevance. On PostgreSQL every full match now reports exactly 1,
  so Core no longer sees text strength between full matches: confidence, recency and status decide between them,
  whatever order the source returned them in.
- **The text and vector balance moved.** With the vector channel on, Core takes the higher of a record's text and
  vector relevance. A PostgreSQL full text match is now 1.0, above almost any vector hit (`1 - cosine_distance / 2`),
  while a partial match on a long request (3 of 11 terms is about 0.27) now loses to most vector hits; before, every
  text relevance was a `ts_rank_cd` value well below 1.
- **The query.** PostgreSQL builds an OR of the distinct lexemes of `ts_debug('english', task_text)`, each a quoted
  `tsquery` literal, so the GIN indexes still answer it. A hyphenated compound counts as one term, its whole-compound
  lexeme ("multi-stage" is one term, not three). Quotes, `or` and `-` are plain words and negate nothing,
  where `websearch_to_tsquery` read them as operators. Text with no terms left after stopwords still matches nothing.
- **The old behaviour stays reachable.** `MinimumMatchedTerms = ExperienceCandidateQuery.AllTerms` (or any value at
  least the query's term count) requires every term again. Core sets it from the new
  `RetrievalPolicy.MinimumMatchedTerms` (default 3, at least 1); a value below 1 is `Invalid` at the port and throws
  in the policy.
- **Migration `0025_text_search_any_term.sql`** drops `0024`'s `search_experience_text`, whose signature gains
  `p_minimum_matched_terms`, and creates its replacement from the same SQL constants as the store's statement. It
  stays `SECURITY DEFINER` with its pinned `search_path`, `EXECUTE` is revoked from `PUBLIC`, and the privileges call
  still verifies it byte for byte. `0024` is unchanged. Its security review is in its header.
- **Upgrade note.** Run `MigrateAsync`, then, **with row-level security on, re-run the privileges call**
  (`ApplyApplicationRolePrivilegesAsync`): dropping the old function drops the application role's `EXECUTE` grant,
  and until the call grants the new one, text searches run the store's own statement under the policies (correct,
  but without the GIN index), as they do whenever the function is not granted. With row-level security off, migrating
  is enough.
- **Public API (additive).** `ExperienceCandidateQuery.MinimumMatchedTerms`, `DefaultMinimumMatchedTerms` and
  `AllTerms`; `RetrievalPolicy.MinimumMatchedTerms`; `PostgresExperienceRecordSchema.TextSearchAnyTermScriptName`.

### A retrieval benchmark without a model (story 20.1)

- `tests/AgentExperience.RetrievalQuality` measures how often lexical retrieval finds the right lesson: a checked-in
  corpus of 53 records and 48 labelled queries, run through each adapter's candidate source and reported as recall,
  precision, false positives and zero-candidate share in a golden report per adapter. Today, overall recall@8 is 0.378
  in memory and 0.511 on PostgreSQL, and every one of the 10 realistic requests (long, paraphrased, worded the way a
  developer types them to an agent) finds no candidate at all on either adapter, because every query term must match.
  See [Retrieval quality](docs/benchmarks.md#retrieval-quality-story-201). Test-only: no behaviour or public API
  change. (Story 20.2 above changes these numbers.)

### A one-minute quick-start demo

- `dotnet run --project samples/AgentExperience.Sample.QuickStart` runs the README quick start's one-call wiring twice
  on the same ticket, against a tool with one working strategy, and prints both runs and the block the second run's
  model was sent. It uses a scripted stand-in model by default (no key, no network, deterministic output guarded by a
  golden test) and a real model on any OpenAI-compatible endpoint when `OPENAI_API_KEY` is set. The README now opens
  with it, and with the live experiment's results ("Does it help?"). No library change.

## 0.1.0-preview.8

A maintenance preview. It fixes how a reflection timeout is reported, and makes the codebase easier to keep correct.
Packing checks binary compatibility with the last published preview, the PostgreSQL record store is split into
smaller files, race tests wait for signals instead of sleeping, and one version per package is enforced across the
repository. There are no new migrations, no public API changes and no breaking changes. Upgrading from
`0.1.0-preview.7` means updating the package versions.

### A reflection timeout is always reported as one

- `ChatClientExperienceReflector` enforces its timeout twice (the call's token and a wait), and both fire at the
  timeout. When the token won, the reflection failed as `ModelCallFailed` instead of `TimedOut`. Either order is now
  `TimedOut`; a `TimeoutException` the chat client itself fails with is still `ModelCallFailed`.

### Packing checks compatibility with the last published preview (story 19.1)

- `dotnet pack` now runs the SDK's package validation for all six packages against the last published preview
  (`AgentExperiencePackageValidationBaseline` in `Directory.Build.props`, `0.1.0-preview.7`), so a binary break —
  a removed or re-shaped member, `init` changed to `set` — fails CI and the release unless the project's
  `CompatibilitySuppressions.xml` declares it and this changelog labels it as breaking. A release test holds the
  baseline to the last release. Restore now downloads the baseline packages from nuget.org. No behaviour or API
  change.

### Race tests wait for signals, not for time (story 19.3)

- The PostgreSQL race tests wait until the competing statement is really parked on the lock (`pg_blocking_pids`)
  instead of sleeping, and the open-run tests assert a missing close the moment it is decided. For that, the MAF
  adapter's `OpenRunRegistry` now counts the closes it starts and holds each background close, and each abandoned
  finalization, until it finishes: an internal test seam, no behaviour or public API change. CONTRIBUTING's "Without
  Docker" commands are per project, and a release test keeps them complete.

### PostgreSQL record store split (story 19.2)

- **Internal refactor, no behaviour change.** `PostgresExperienceRecordStore` was one file of almost 3,900 lines. Its
  stateful concerns now sit in partial files of the same class (the lifecycle commit, erasure and retention, sealing and
  the authorship backfill), and its stateless code moved to internal static classes, which also hold what other
  components share: the store's SQL fragments (`ExperienceRecordSql`), its row and payload decoders
  (`ExperienceRecordRows`), its parameter binding (`ExperienceRecordParameters`) and failure translation
  (`ExperienceStoreFailures`). The SQL text, the public API, the telemetry and the exceptions are unchanged. About ten
  helpers that were private to the store (`DecodeSealedRecord`, `ReadEvent`, `ReadRevision`, `ReadStoredStatus`,
  `ReadEventScope`, `DecodeEnumText` and the four `Nullable*` binders) are now `internal`, so the assemblies this
  package grants `InternalsVisibleTo` can reach them.
- **`AgentExperience.Storage.Postgres.Vectors` keeps its `InternalsVisibleTo` access, on purpose.** The vector channel
  shares the store's schema and SQL fragments, its row decoder, the per-record keys of crypto-shredding, the
  row-level-security session and grant auditing. Making the package independent would mean publishing those
  security-sensitive internals as public API or duplicating them, so the access stays, and the project file says what
  the vectors package depends on and why.

### One version per package across the repository (story 19.4)

- A release test now fails when two projects anywhere in the repository (tests, sample, experiments, proof)
  reference different versions of the same package; the upgrade seeders, which pin older published versions on
  purpose, are exempt. This replaces adopting central package management, which the floating-dependency probe and the
  per-project pin checks rely on not having. Tests only.

## 0.1.0-preview.7

This preview makes the library easier to adopt and more useful once adopted. One call sets it up
(`services.AddAgentExperience(...)`), host callbacks can be async, an injected lesson says what failed and what worked
in a compact block, the task text can be derived from the conversation, and verified reuse can move a lesson's
confidence (opt-in). It also bounds the in-memory capture service and the pre-model path, fails closed on unknown
authorship, signs the reflection with the rest of the record (provenance v2), and keeps text search on its GIN index
under row-level security.

Upgrading from `0.1.0-preview.6` applies migrations `0022`, `0023` and `0024`. Run the migrator before deploying the new
build. `0022` reads every live plaintext payload inside the migrator's transaction and command timeout, so on a large
plaintext table migrate in a maintenance window or with a raised command timeout. `0023` builds an index on the
confidence-evidence ledger, which blocks appends to it while it builds; on a large ledger build it `CONCURRENTLY` first,
as its header shows, and run the migrator with a `lock_timeout`. Migrate before setting
`HostTrustedEvidence = RecordedOnly` (`0023`), and with row-level security on, run the privileges call after migrating
so searches use `search_experience_text` (`0024`). Breaking changes, all labelled below: `Tried:` and `Worked:` replace
`Approach:` in the rendered block, the compact rendering is the default, `InitialEventIdFor` takes the scope,
`ResolveRequest` is no longer `required`, and some option setters changed from `init` to `set`. During a rolling
deploy, a node on `0.1.0-preview.6` refuses the version 2 provenance signatures new nodes make: set
`SignClaimsVersion = 1` until every node runs this build (see [Confidence](docs/guide/confidence.md)).

### The in-memory capture service bounds the finished runs it keeps (story 16.1)

- **The problem it fixes.** `InMemoryExperienceCaptureService`, the default `IExperienceCaptureService`, kept every
  run it had ever captured, so a long-lived host grew without bound until it ran out of memory.
- **Retention bounds.** Completed runs are dropped after `CaptureLimits.CompletedRunRetention` (new, default 24 hours)
  or once more than `CaptureLimits.MaxRetainedCompletedRuns` (new, default 10,000) are held, earliest completed first.
  Both are optional `init` properties; the positional constructor is unchanged. Open runs are never dropped. The
  bounds are applied on the service's own calls (no timer). Age is measured on the `TimeProvider`'s monotonic
  timestamp, so wall-clock changes do not move it; the service gains a constructor overload taking a `TimeProvider`,
  and `AddAgentExperienceCore` passes the registered one.
- **Behaviour change.** A dropped run answers exactly as an unknown run: finalizing it (or retrying finalization)
  returns `RunNotFound`, a continuation naming it opens a new run, and evidence naming a run that was never finalized
  must arrive while the run is held. While a run is held, nothing changes: a finalization retry still returns
  `AlreadyFinalized`. A reused ID of a dropped run finalizes as `AlreadyFinalized` for the earlier record, so use a
  fresh run ID per run. See [How long runs are kept](docs/guide/capture.md#how-long-runs-are-kept).

### The in-memory capture service bounds the runs it holds open (story 16.4)

- **The problem it fixes.** Story 16.1 bounded completed runs only, so a caller that used
  `InMemoryExperienceCaptureService` directly (without the MAF adapter's `MaxOpenRunDuration`) and never completed
  its runs still grew without bound.
- **Open-run count bound, on by default.** `CaptureLimits.MaxOpenRuns` (new, default 10,000). Past it, a `StartRun`
  call that would open a new run returns the new `StartRunOutcome.CapacityExceeded` (appended last, so existing
  values are unchanged) and stores nothing; continuing an open run is never refused. The MAF adapter reports the
  refusal through `OnCaptureFailure` and runs the invocation uncaptured.
- **Open-run age bound, off by default.** `CaptureLimits.MaxOpenRunAge` (new, `null` by default). When set, the
  service completes an open run older than it as `Cancelled`, on its next call, and the run is then held and dropped
  like any completed run. A later host completion under its own event ID returns `Conflict`.
- **Behaviour change.** A host with more than 10,000 runs open at once in one service now has new runs refused; raise
  `MaxOpenRuns` if that is intended. The service can hold up to `MaxOpenRuns + MaxRetainedCompletedRuns` runs (20,000
  at the defaults, so about twice the completed-only worst case), so size the two together. A run the age bound
  cancels is never finalized or reported by the service. See [How long runs are kept](docs/guide/capture.md#how-long-runs-are-kept).

### The whole pre-model path is bounded, not only retrieval (story 16.2)

- **The problem it fixes.** The injection provider's final eligibility re-read was bounded only cooperatively: a
  store that ignored its cancellation token, or was slow to cancel, held the model call well past
  `EligibilityCheckTimeout`, and the per-record fallback after a failed batch could make it worse.
- **Hard bound.** The batch re-read, each per-record fallback read, and the session's withdrawal re-check read are
  each awaited for what is left of one `EligibilityCheckTimeout` budget, as retrieval bounds its search. A read still
  running at expiry is abandoned (cancelled in the background) and the check reports its usual timeout; nothing is
  injected.
- **Behaviour change.** `ExperienceInjectionLimits.DefaultEligibilityCheckTimeout` drops from 2 s to **500 ms**, so
  the worst case before the model call is about 1 s at the defaults (retrieval's 500 ms plus the check's 500 ms,
  which includes `DecideInjection` time, plus the host's `ResolveRequest`). A host whose store needs longer should set
  `Limits.EligibilityCheckTimeout`. Hosts most at risk use a store that keeps the port's default `GetManyAsync`, which
  reads one record at a time; the symptom is `Failed` injection results whose reason says the final eligibility check
  exceeded its bound, with nothing injected.
- **Abandoned reads keep running.** A read abandoned at the bound keeps running against the store in the background
  until it observes its cancellation, so the store must tolerate concurrent use (not a scoped, non-thread-safe
  context), it may hold a pooled connection until the cancel lands, and a grant's access rows may be written after the
  timeout is reported. The bound is released by a `TimeProvider` timer, so a starved thread pool can still release it
  late. See [Pre-model latency budget](docs/guide/injection.md#pre-model-latency-budget).

### Abandoned reads against a hung store are capped (story 16.5)

- **The problem it fixes.** Retrieval and the injection provider's eligibility re-read abandon a timed-out store read
  and let it keep running. Against a store that hangs on every call, each invocation added another running read, so
  they piled up without limit and could exhaust the connection or thread pool. An abandoned read also kept a
  registration on the caller's cancellation token until it ended, which for a hung read was never.
- **Running-count cap.** `RetrievalPolicy.MaxAbandonedSearches` and `ExperienceInjectionLimits.MaxAbandonedReads`
  (new `init` properties, default 16 each, must be positive; `DefaultMaxAbandonedSearches` and
  `DefaultMaxAbandonedReads` are new). Each retrieval service and each provider counts its own abandoned reads still
  running, whether they were abandoned on a timeout or because the caller cancelled. At the cap no new read is
  started: retrieval returns `TimedOut` at once, the same empty result as a timeout, but with a `Failure` whose reason
  names the cap (a real timeout still carries no `Failure`; injection reports it as `RetrievalTimedOut` with that
  reason), and the eligibility check is not started and reports a `Failed` injection result whose reason names
  `MaxAbandonedReads`. Neither touches the store. The count drops as the abandoned reads end. There is no
  circuit-breaker state, probe or timer.
- **Shared instances only.** The counts are per `ExperienceRetrievalService` and per `ExperienceContextProvider`
  instance, so the caps engage only when those are shared and long-lived (for example DI singletons); one built per
  request never reaches them.
- **Detached at once.** An abandoned read is now unregistered from the caller's token the moment it is abandoned; its
  own token source is still cancelled off the caller's thread and disposed only once the read ends. Reads that finish
  in time, and caller cancellation, behave as before. See
  [Pre-model latency budget](docs/guide/injection.md#pre-model-latency-budget).

### The privileges call retries a deadlock (story 16.6)

- **The problem it mitigates.** `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` can fail with
  `40P01 deadlock detected` when PostgreSQL chooses its transaction as a deadlock victim (seen once in CI, with
  row-level security and crypto-shredding on). Privileges calls were already serialized against each other by the
  migrator's advisory lock, so the other side is most likely another session, such as store traffic, taking locks in
  a different order from the `ACCESS EXCLUSIVE` locks the call takes to re-create policies and switch row-level
  security. That lock-order risk remains: run the call when store traffic is low.
- **The mitigation.** On `40P01` the whole privileges transaction, which PostgreSQL has already rolled back, is
  retried: up to three attempts in all, after a short jittered wait (50–150 ms, then 100–300 ms). Cancelling during
  that wait ends the call with the same `OperationCanceledException` as any other cancellation. Only a deadlock is
  retried. A deadlock on the last attempt throws `ExperienceStoreException` (the `PostgresException` inside) whose
  message says the call was a deadlock victim three times in a row, most likely against live store traffic, and to run
  it when traffic is low; it carries no SQL or role name. Every other failure surfaces exactly as before, and the
  checks, refusals, idempotency and the migrator's lock are unchanged. The call still logs nothing. See
  [Applying the privileges, on every deploy](docs/guide/deployment.md#applying-the-privileges-on-every-deploy).
- **Tests.** The PostgreSQL test owner data sources set `Include Error Detail=true`, so a future deadlock in the suite
  names the other session and its statement.

### Retrieval unwraps record keys in one batch (story 16.3)

- **The problem it fixes.** With crypto-shredding on, every sealed row a read returned cost one key-store call, made
  in turn while the data reader held its connection. At 10 ms per KMS unwrap, a 50-candidate text search took about
  half a second, the whole default retrieval budget, and injection silently delivered nothing.
- **Batch key lookup.** `IExperienceKeyStore.GetKeysAsync` (new) looks up several references in one call. It is a
  default interface method that calls `GetKeyAsync` for each reference in order, so existing key stores keep working
  unchanged; on a failure it disposes every key it already obtained. `EnvelopeExperienceKeyStore` overrides it to
  look up and unwrap concurrently, at most 16 at a time by default; a new constructor overload takes
  `maxConcurrentKeyLookups`, and `MaxConcurrentKeyLookups` and `DefaultMaxConcurrentKeyLookups` are new. Nothing is
  cached: a destroyed key is never served by a later call.
- **Retrieval reads.** The text search, the vector search and `GetManyAsync` (injection's eligibility re-read) now
  read their rows into memory, close the reader, commit their read-only transaction and return the connection to the
  pool, then make one `GetKeysAsync` call for every sealed row and decode. A slow key store no longer holds a reader,
  a transaction or a pooled connection on these paths. The buffered rows are bounded by the candidate limit (the
  searches) or the ID list given (`GetManyAsync`). Results, ordering, error types and access-audit rows are
  unchanged; plaintext rows never reach the key store. Other reads still make one call per sealed record with their
  reader open.
- **Concurrency to plan for.** `maxConcurrentKeyLookups` bounds one `GetKeysAsync` call, not the process: one
  injection can run the text search, the vector search and the re-read together, and hosts run retrievals in
  parallel, so the unwraps in flight can reach the bound times the concurrent calls. With the envelope store, your
  `IExperienceWrappedKeyRepository` and `IExperienceKeyEncryptionKey` implementations are now called concurrently and
  must be thread-safe. One injection can unwrap the same record's key more than once (text, vector, re-read).
- **Action for a custom key store over a remote KMS:** override `GetKeysAsync` to unwrap concurrently or through the
  KMS's batch API; the default is still one call after another. A wrapper or decorator around a key store must
  forward `GetKeysAsync`, or it falls back to sequential lookups. The new `EncryptedRetrievalBenchmarks` measures it at
  10 ms per unwrap: 62.7 ms batched against 610.4 ms with concurrency limited to one, which exceeds the 500 ms
  default retrieval timeout. With no unwrap latency, batching adds a small overhead (5.1 ms against 4.3 ms, within
  the run's error). See [Key custody](docs/guide/crypto-shredding.md#key-custody-the-property-is-only-as-true-as-this).

### Unknown authorship fails closed, and the library's own reflector is recognised (story 17.1)

- **The problem it fixes.** Two clauses of the KL-18 boundary were removable by code. Under
  `ModelAuthoredLessons = Exclude`, a PostgreSQL row sealed without its authorship flag (before migration `0021`, by
  an instance on an earlier build during a rolling deploy, or by any writer that left the flag out) was still returned
  by both channels and took a place in the candidate window before retrieval dropped it. And records the library's own
  `ChatClientExperienceReflector` wrote before it declared authorship read as `Deterministic`.
- **Behaviour change: fail closed in SQL.** Under exclusion, the text candidate source, the vector search and its
  compatibility probe keep only rows whose `reflection_model_authored` is `false` (`IS FALSE`, was `IS NOT TRUE`).
  A sealed row with an unknown flag is left out of an excluding search, so a deterministic record among them is not
  injected under `Exclude` until its flag is written. Without the exclusion nothing changes.
- **One authorship rule.** Core (finalization's content guard, retrieval's exclusion check), the MAF adapter
  (injection labelling, fencing and exclusion) and both stores now decide authorship by one rule: model-authored when
  `Authorship` is anything but `Deterministic`, **or** when `Producer` starts with
  `AgentExperience.ChatClientExperienceReflector/` (ordinal), now public as
  `ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix` in Abstractions. No other producer is read.
  The reflector shipped in `0.1.0-preview.5`, the release that also made it declare `Model`, so no published release
  wrote one of its records as `Deterministic`; the rule covers unreleased builds between stories 14.2 and 14.3.
- **Action for a custom `IExperienceCandidateSource` or `IExperienceEmbeddingIndex`:** `ExcludeModelAuthored` now also
  covers a reflection whose producer starts with `ReflectionAuthorshipConventions.LibraryModelReflectorProducerPrefix`,
  whatever authorship it declares, so an implementation must apply that prefix check too. The store conformance suite
  checks it.
- **Migration `0022_library_reflector_authorship`** (`PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName`)
  replaces `payload_reflection_model_authored` with the same rule and recomputes the flag on every live plaintext row
  that differs; a reflection whose producer is missing or not a string, which the reader refuses, is `true` (fail
  closed). It adds no table, column, index or function signature, takes row locks only on the rows it rewrites, and is
  idempotent. Its `UPDATE` reads every live plaintext payload inside the migrator's transaction and command timeout:
  on a large plaintext table, migrate in a maintenance window or with a raised command timeout. See [0022](docs/guide/postgres-schema.md#0022-library-reflector-authorship).
- **Owner-run backfill.** `PostgresExperienceRecordStore.BackfillSealedAuthorshipAsync` (new, with an optional
  `startAfter` cursor, returning the new `ExperienceAuthorshipBackfillResult` with `SetCount`, `SkippedCount`,
  `MoreRemain` and `ResumeAfter`) opens each live sealed row whose flag is unknown with its record key and writes the
  flag, in bounded, authorized batches that resume past the last examined ID. It fetches a batch's keys in one
  key-store call before locking any row. It must run over the owner's data source with the record keys (the
  application role holds no `UPDATE` on the flag), skips and counts a row whose key was destroyed or whose payload
  cannot be opened, and never changes a flag already written; a call that throws may already have set some flags, and
  rerunning it is safe. It is the new `record.authorship.backfill` telemetry operation. **Action with crypto-shredding
  on:** run it after upgrading, passing each `ResumeAfter` on until `MoreRemain` is `false` for every project; see
  [Backfilling authorship flags](docs/guide/crypto-shredding.md#backfilling-authorship-flags-after-upgrading).
- **KL-18** drops both clauses; see [Limits history](docs/limits-history.md#narrowed-in-010-preview7).

### The reflection is signed too (story 17.2)

- **The problem it fixes.** Provenance signing (story 13.1) covered only a record's finalization claims, so a party
  that could write the store could change a signed record's lesson, its approach, or flip its `Reflection.Authorship`
  to `Deterministic`, and the result was then injected unfenced (KL-18).
- **Claims version 2.** With `ExperienceProvenanceSigningOptions` configured, finalization signs `aexp-prov:v2`: the
  version 1 claims, then a SHA-256 digest of everything injection renders from the record: `TaskId`, `TaskSummary`,
  the outcome's status and evidence count, the `Environment`, the `Attempts` (tool names and argument values, through
  their JSON form as the PostgreSQL store writes it, numbers by exact decimal value, so both stores read back the same
  bytes), and the reflection's free text, evidence count,
  `Authorship` and `Producer`. The signature's `Algorithm` is the new `ExperienceProvenanceSignature.HmacSha256ClaimsV2`
  (`HMAC-SHA256.aexp-prov.v2`, within the characters every store accepts), so a verifier never guesses the version. A
  version 1 signature (`HmacSha256`) still verifies for the finalization claims, and independence verification treats
  it exactly as before. A version 2 record whose content changed after signing no longer verifies, so its run stops
  vouching too. A run whose content has no canonical encoding (a lone surrogate, or a tool argument value with no JSON
  form) now ends `Failed` at the create stage with nothing stored, and the failure says the claims or content could
  not be encoded. No stored schema changes, and nothing re-signs existing records.
- **Behaviour change: unconfirmed content is model-authored.** With signing configured, a record's content is
  confirmed only by a version 2 signature that verifies under a key in the ring, or by `TrustUnsignedRecordIds` for a
  listed unsigned record. Any other record counts as model-authored: `ExperienceRetrievalService` excludes it under
  `ExcludeModelAuthored`, and injection omits it under `ModelAuthoredLessons = Exclude`, both with the new reason
  `UnconfirmedContent` (`RetrievalExclusionReason.UnconfirmedContent`, `InjectionOmissionReason.UnconfirmedContent`)
  when that is the only reason. Otherwise injection fences and labels it, and every line drawn from it moves inside
  the fence: its task ID (a `Task:` line, the `Source:` line ending with `HistoricalReferenceWriter.UnconfirmedTaskNotice`),
  its `Recorded:`, `Environment:`, `Verification:` and `Evidence:` lines and its `Approach:` line. A stored record whose
  content cannot be encoded is unconfirmed and never fails a retrieval. **Records signed before this release are fenced as model-authored at injection and excluded under
  `Exclude`**, as are records a host writes through `CreateAsync` without a signature. A store's candidate window is
  not aware of signatures, so under `Exclude` such records still take candidate slots before retrieval drops them.
  Without signing, nothing changes.
- **Upgrade settings.** `ExperienceProvenanceSigningOptions.SignClaimsVersion` (new, default 2, only 1 or 2): set it
  to 1 during a rolling deploy in which nodes on an earlier build still verify, since they refuse a version 2
  signature; switch it back afterwards. `ExperienceProvenanceSigningOptions.ConfirmV1Content` (new, default `false`):
  a transition setting that lets a valid version 1 signature confirm its record's content, accepting for those records
  the exposure version 1 left open, while existing lessons are reviewed or replaced.
  `ExperienceProvenanceSigningOptions.ConfirmContentRecordIds` (new, empty by default) is the per-record alternative:
  listed records (signed version 1, including during a `SignClaimsVersion = 1` rollout, or unsigned) count as
  confirmed once reviewed, unless their signature is present and does not verify.
- **New API.** `ExperienceRetrievalService` gains a constructor that takes `provenanceSigning`, and public
  `IsModelAuthored(ExperienceRecord)` and `IsContentConfirmed(ExperienceRecord)`, which the MAF provider uses on the
  record it re-read. `AddAgentExperienceRetrieval` passes registered signing options (directly or as `IOptions<T>`) to
  it. `ExperienceInjectionDecisionContext.ModelAuthored` (new, `true` unless set, which the provider always does) carries
  the provider's verdict to `DecideInjection`; a
  host should decide on it rather than on the declared authorship. `HistoricalReferenceWriter.Write` gains an overload
  taking a content-confirmation function; the existing overloads decide on the reflection alone.
  **Action for a host that constructs `ExperienceRetrievalService` by hand with signing on:** pass the same options
  to the new constructor, or records are judged without the content check.
- **KL-18** drops the clause that signing covers the finalization claims only; KL-11 now says a content edit is
  caught only for version 2 records. See [Limits history](docs/limits-history.md#narrowed-in-010-preview7) and
  [Signing provenance](docs/guide/confidence.md#signing-provenance).

### Host-trusted evidence can be kept out of the ranked score (story 17.3)

- **The problem it fixes.** Under the verification opt-out (`IndependenceVerification.TrustHostSuppliedIdentifiers`),
  evidence stored as `HostTrusted` still moved the record's counters, its stored `ReuseConfidence` (which retrieval
  ranks and filters on) and, for a contradiction, its status. Only `ReadConfidenceAsync` could leave it out (KL-11).
- **New option.** `ExperienceIndependenceOptions.HostTrustedEvidence` of the new enum `HostTrustedEvidenceEffect`
  (`Counted = 0`, the default and the earlier behaviour; `RecordedOnly = 1`). An undefined value is refused when a
  service is constructed. It only matters under the opt-out; verified evidence always counts.
- **`RecordedOnly`.** Host-trusted evidence is committed on a lifecycle event and an evidence row exactly as before
  (same IDs, idempotency, assessment spending, access audit, `Admission = HostTrusted`), but the event's new counters,
  new score and status equal the prior ones, and the record's `UpdatedAt` is not refreshed: only its revision moves.
  `ApplyEvidenceAsync` reports `Applied` with `Counted: false`, a contradiction does not contest the record, and the
  confidence engine is not asked to score it. A later recorded-only submission for a key already held by counted
  evidence or by an earlier recorded-only event is a duplicate (a ledger row, no event). The revision still advances
  and the event is appended to history exactly as a counted one is, so reused-session re-delivery, `StaleRevision`
  contention and history growth are as under `Counted`.
- **Reading it.** `ReadConfidenceAsync` with `ConfidenceEvidenceFilter.All` now adds recorded-only host-trusted
  evidence to the stored counters (once per independence key no counted update holds) and scores that, which gives
  the counts and score `Counted` would have stored (not the `Contested` status or de-indexing it would have applied);
  `ExcludeHostTrusted` and `VerifiedOnly` report without it. `ConfidenceReport` gains
  `HostTrustedRecordedOnly`. With no such evidence, `All` still reports the stored score unchanged.
- **Stores.** Both shipped stores, through the shared validator, accept a confidence event whose evidence moves no
  counter only when its admission is `HostTrusted` and its score and status do not move; any other such event is
  `Invalid` (it was refused before too: as an infrastructure failure in PostgreSQL). **Migration
  `0023_recorded_only_evidence`** (`PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName`) replaces `0007`'s
  `confidence_evidence_event_only_when_counted` CHECK with one that also admits such a row, and adds the matching
  CHECK on `lifecycle_events` (both `NOT VALID`), plus a partial index for the duplicate check; no new table, column
  or grant. **Migrate before setting `RecordedOnly`:** against a database without `0023` every host-trusted
  submission fails as an infrastructure error. **Action for an out-of-tree
  store:** accept such an event, leave counters, score and `UpdatedAt` alone, and treat a later recorded-only
  submission for a key already counted or recorded as a duplicate.
- **No backfill.** Evidence counted before `RecordedOnly` was set stays counted; read it out with `ExcludeHostTrusted`.
- **KL-11** drops the clause that the opt-out's evidence is excluded only on read; see
  [Limits history](docs/limits-history.md#narrowed-in-010-preview7) and
  [Keeping host-trusted evidence out of the ranked score](docs/guide/confidence.md#keeping-host-trusted-evidence-out-of-the-ranked-score).

### Session tracking is serialized within a process (story 17.4)

- **The problem it fixes.** Session tracking (story 6.5) loaded a session's account, awaited retrieval, then staged one
  pending delivery and saved it. Two invocations running concurrently on one session both read the same account, so
  they could deliver the same record revision twice, and the second save overwrote the first one's stage: that
  delivery was never charged, never tracked, and never withdrawn later (KL-12).
- **A lock per account.** `ExperienceContextProvider` now holds a `SemaphoreSlim` per `AgentSession` instance and
  state key (held in a `ConditionalWeakTable`, so it goes with the session, and shared by every provider in the
  process) from loading the account to saving it, through retrieval and the final eligibility check, and around
  settling in `InvokedCoreAsync`. It is never held across the model call. Waiting to inject honours the invocation's
  cancellation token, and a cancelled wait propagates like any cancellation of the invocation; settling waits
  regardless, so a cancelled invocation's stage is still discarded. Concurrent invocations on one session now queue
  their pre-model latency (retrieval and the eligibility re-check) behind one another.
- **Action for a host:** the lock is held while `DecideInjection` runs, so a `DecideInjection` callback must not run
  any invocation that injects with the same session and the same state key. That invocation would wait for the lock
  until its token is cancelled, and with a token that cannot be cancelled it waits indefinitely.
- **One pending stage per in-flight invocation.** Each provider settles the stages its own account holds among the
  blocks a context provider injected into the invocation; a block the request carries from the chat history, or one
  the host sends again as input, settles nothing. Until settled, every decision counts a stage as delivered: it is
  charged to the budget, its records are not delivered again and are re-checked for withdrawal, and its withdrawal
  notices stay owed. At most eight stages stay pending; staging a ninth first commits the oldest as unsure, as a
  single leftover stage was before. With one invocation at a time, the block, the charges and the notices are
  unchanged.
- **New option: `ExperienceInjectionSessionLimits.InFlightStageWindow`** (default `DefaultInFlightStageWindow`,
  5 minutes, the capture adapter's default `MaxOpenRunDuration`; must be strictly positive). Each stage records when
  it was staged, on the provider's `TimeProvider`. A pending stage is held as in flight only within the window; an
  older one (or one dated further ahead than the window) is taken as abandoned and committed as unsure at the
  session's next invocation, exactly as a leftover stage was before: charged, its records eligible again, its
  notices still owed. MAF gives no signal for an abandoned stream, so its age is the only discriminator. Set the
  window above your longest invocation: a stage that outlives it is committed as unsure while still running, so a
  concurrent invocation can deliver its records again (charged and tracked, never lost). A successful invocation
  whose settling is skipped (settling threw, say) is likewise committed as unsure after the window.
- **Behaviour change: an abandoned stream's records are held for at most the window before becoming eligible
  again.** A stream abandoned before MAF settled it was committed as unsure at the session's next invocation, so its
  records were delivered again at once. Nothing tells it from an invocation still running, so an invocation within
  `InFlightStageWindow` of the abandoned one now counts its records as delivered; one after the window commits it as
  unsure and delivers them again, as before.
- **State format version 2.** The account's `pending` member is now a list of stages (empty when none is pending),
  each carrying `staged`, when it was staged. Version 1 still loads: its single stage recorded no staging time, so it
  is committed as unsure on load, as before, and the state is saved back as version 2 at the session's next
  invocation. **Rollback:** a build before this change reads version 2 as an unreadable state, so a session saved by
  this build injects nothing there until the host removes its state key, which resets the session's account.
- **KL-12** drops the clause that concurrent invocations on one session race on the tracking; what remains is two
  processes resuming the same serialized session, the host deleting or replacing the state key, and a model that
  cannot unread a block. See [Limits history](docs/limits-history.md#narrowed-in-010-preview7) and
  [Reused sessions](docs/guide/injection.md#reused-sessions-a-budget-no-repeats-and-withdrawal-notices).

### The embedding's content hash stops confirming a guessed summary after erasure (story 17.5)

- **The problem it fixes.** Each `experience_embeddings` row stored `content_hash` as a plain SHA-256 over the model
  ID and the normalized summary. With crypto-shredding on, erasing a record destroyed its key, but the hash survived
  in every backup, replica and WAL segment and still let anyone confirm a guessed summary against it (KL-2).
- **Keyed in encrypted mode.** With an `ExperienceEncryption`, `PostgresExperienceEmbeddingIndex` now writes
  `keyed:` followed by base64 of HMAC-SHA256 of the plain hash, under a subkey derived from the record's data key
  with HKDF-SHA256 and the fixed label `aexp:embedding-content-hash:v1`. It looks the key up first; it creates one only
  for a live plaintext record in exactly the write's scope (written before the upgrade), so an unknown ID, another
  scope or a tombstone leaves no key behind. A destroyed key is `Missing`, as before. Once the key is destroyed, the
  stored value no longer confirms a guessed summary; it is deterministic per record, so two copies still show whether
  the summary changed between them.
- **Action for a host:** the key store identity the vectors index runs under needs permission to create keys, for
  plaintext records written before the upgrade.
- **The scan translates it back.** `ScanAsync` reads its rows (at most the scan's limit) into memory, releases its
  connection, and then looks up in one `GetKeysAsync` call the key of every sealed row and of every row with a
  stored embedding; it no longer makes one key-store call per sealed row with its reader open. A row whose key is
  destroyed, sealed or not, yields no target, so its text never reaches a provider again. The scan recomputes the
  plain hash from the opened summary and the stored model ID, keys it and compares in constant time: a match is
  reported as the plain hash, so Core skips the record exactly as before; anything else is reported as a value no
  computed hash equals, so Core re-embeds it. A plaintext row whose key the store never held reports a clear hash as
  stored and does not fail the scan. Core, `ExperienceEmbeddingDescriptor.ComputeContentHash`, the schema and
  plaintext mode are unchanged; no migration.
- **Behaviour change: one re-embed per keyed record after upgrading.** In encrypted mode a content hash stored in the
  clear for a record that has a key (by an earlier release, by a process in plaintext mode, or before the deployment
  switched modes, on a record since sealed) is reported as unconfirmed, so the next re-index pass re-embeds it once,
  one provider call each (batched as usual), and stores its hash keyed; the following pass skips it. Run a full
  `ReindexAsync` after sealing, then `VACUUM agent_experience.experience_embeddings`;
  [Crypto-shredding](docs/guide/crypto-shredding.md#upgrading-a-plaintext-deployment) has the query that confirms no
  clear hash remains. Do not run a plaintext-mode and an encrypted-mode indexer against the same database: each
  re-embeds the other's rows, and plaintext-mode writes put clear hashes back.
- **KL-2** drops the content hash from what confirms a guessed summary in every copy after erasure; the embedding
  vector and the full-text vector remain. See [Limits history](docs/limits-history.md#narrowed-in-010-preview7) and
  [Crypto-shredding](docs/guide/crypto-shredding.md#search-and-why-the-residual-is-what-it-is).

### The initial lifecycle event ID is derived from the scope too (story 17.6)

- **The problem it fixes.** `ExperienceFinalizationService.InitialEventIdFor(runId)` derived a record's initial
  lifecycle event ID from the run alone, and event IDs are unique across every scope. A writer in another scope that
  knew the run ID could commit an event of its own under that ID first, and this run's initial commit then failed as
  a `Conflict` on every retry. Story 4.5 closed the same squat for the record ID.
- **Breaking (preview): `InitialEventIdFor(Guid runId, Scope scope)`.** It replaces the one-argument overload, with
  no compatible overload, as `ExperienceIdFor(Guid)` was replaced in story 4.5. An `[Obsolete]` overload would have to
  keep deriving the ID any scope can take. Callers pass the same `Scope` they finalize the run under. The derivation
  is the record ID's own: the fixed namespace, the run, the purpose tag and every scope field, length-prefixed. The
  record ID and the reflection ID do not change, and there is no schema change. Like the record ID since story 4.5,
  the derivation is unkeyed and is not a secret: it raises the bar from knowing the run ID to knowing the run ID and
  the victim's scope fields, and a writer that knows both can still take the ID first. Run IDs are random identifiers
  the host holds, and that is the protection.
- **Records from earlier releases keep working.** An event's ID is stored and never re-derived. Finalizing a run whose
  record an earlier release already confirmed under the run-only event ID returns `AlreadyFinalized`, with no second
  initial event, including when that release's commit lands between this call's read and its commit. A record an
  earlier release created but never confirmed is confirmed under the new ID, even when another scope took its
  run-only ID. See [Why retrying is safe](docs/guide/finalization.md#why-retrying-is-safe).
- **Migration note.** To find a record's initial event, read the first entry of its history (applied revision 1,
  `ExpectedRevision` 0) instead of computing `InitialEventIdFor`, which matches nothing for a record finalized before
  this release.

### Text search keeps its GIN index under row-level security (story 17.7)

- **The problem it fixes.** With `EnableRowLevelSecurity` on (story 15.1), the text channel's `@@` match is not
  leakproof, so PostgreSQL applied it only after the read policy and could not use the GIN indexes: every text search
  scanned every live record of the declared tenant, which made row-level security and large tenants incompatible
  (KL-17).
- **`search_experience_text` (migration `0024`).** A new `SECURITY DEFINER` function, owned by the schema owner, runs
  the text channel's search as the owner, whom the policies do not bind, so the planner uses
  `ix_experience_records_search` and `ix_experience_records_search_sealed` again. It returns nothing unless bounds are
  declared, admits a row only through the read policy's own admission over `0019`'s helpers, and applies every
  predicate of the store's own search, built from the same SQL constants; `pg_temp` is last on its pinned
  `search_path` and `EXECUTE` is revoked from `PUBLIC`. Its security review is in its header and in
  [Enabling row-level security](docs/guide/deployment.md#enabling-row-level-security).
- **When it is used.** `PostgresExperienceCandidateSource` calls it only while row-level security is enabled on
  `experience_records` and the connecting role may execute it, detected once per data source and again after each
  privileges call in the process; a call refused because the function was revoked or dropped since falls back to the
  store's own statement. The cached route is also re-detected after five minutes, so another process's privileges
  call takes effect within that time. The function returns nothing for a requested scope outside the declared bounds.
  With row-level security off, the SQL the store sends is unchanged. Results are identical
  either way: rows, order, relevance, shared flags, permitting grants and the access rows recorded.
- **The privileges call.** `ApplyApplicationRolePrivilegesAsync` grants `EXECUTE` on the function exactly while
  `EnableRowLevelSecurity` is set, refuses to enable row-level security when the function is missing (run
  `MigrateAsync` first) or is not byte for byte the canonical definition (it is never re-created by the call), and,
  like any other `SECURITY DEFINER` function, refuses it when the role can execute it without that opt-in. It also
  requires `SELECT` on `experience_grants` while row-level security is on. There is no opt-out: enabling row-level
  security requires the function.
- **Action for a host with row-level security on:** run `MigrateAsync` (which applies `0024`), then the privileges
  call, as usual on every deploy. Until the privileges call has granted the function, searches keep the previous
  behaviour.
- **KL-17** drops its GIN-index clause, and states accurate reasons for two others: a colliding caller-chosen record
  ID reports `Conflict` because record IDs are globally unique by design, and an exposure of an unknown record is kept
  as `Unresolved` by design. It gains one stated boundary: the search uses the table-wide GIN index, so its cost
  reflects matches in every tenant (a weak timing side channel). See
  [Limits history](docs/limits-history.md#narrowed-in-010-preview7).

### An injected lesson says what failed and what worked (story 18.1)

- **The problem it fixes.** A lesson from the default pipeline told the receiving agent almost nothing it could act
  on: the default reflector wrote `Task 'X' verified: required checks [...] passed (evidence: <guid>)`, and the block's
  `Approach:` line named only the final attempt's tools. What failed, how, and what finally worked never reached the
  model.
- **Breaking (rendering): `Tried:` and `Worked:` replace `Approach:`.** Each record now lists its attempts, oldest
  first, at most the last `HistoricalReferenceWriter.MaxTriedAttempts` (4), saying how many earlier ones were
  omitted: `attempt N: tool_a, tool_b(key="value") → failed (TimeoutException, exit 2)` or `→ completed`. A verified
  record whose final attempt ended without an error also gets `Worked: attempt N (the final attempt)`, which names
  the attempt rather than repeating its calls, so each tool name and value appears once. Tool names and allowlisted
  argument values are rendered with the same allowlist, grant rules, bounds and neutralization as before, and a
  tool name or a value can no longer spell a separator: `->` and `→` become `- >`, and a comma in a tool name
  becomes `;`. A record in the reader's own scope that is unverified or failed now says what it tried too; a
  quarantined record, or one whose attempt numbers are not distinct, still says nothing. The lines sit where
  `Approach:` sat, inside the model-authored fence whenever the approach was. `Tried:` and `Worked:` join the
  neutralized field labels, and `Approach:` stays one. The capability gate still checks only the tools of the
  attempt the `Worked:` line names. Records are bigger, so more of a record's byte budget goes to its attempt lines
  and a block can drop whole records sooner. See [Injection](docs/guide/injection.md#the-payload).
- **A sharing grant shows no more than it did.** A record read through a `LessonAndApproach` or
  `LessonApproachAndArguments` grant shows exactly what its `Approach:` line used to: only a verified record's final
  attempt, as one `Tried:` line (with argument values only under `LessonApproachAndArguments`) and the `Worked:`
  line; never the owner's failed attempts, their error classes, or anything of a record that did not verify. A
  `LessonOnly` grant withholds the lines, and the `Shared:` line says so, now as "The grant withholds this lesson's
  attempts.", for a verified, unquarantined record with distinct attempt numbers whose final attempt ended without an
  error, as before.
- **Error class, never error text, by default.** A failed attempt shows a class built only from recognised tokens
  (an exception type name, copied from the error when it is a capitalised word of at most 40 letters and digits
  ending in `Exception` or `Error`; an exit code; an HTTP status, including `HTTP/1.1 503` and .NET's
  `Response status code does not indicate success: 503`; a POSIX errno name; a timeout, left out when a timeout
  exception is already named; at most three) or `unclassified error`. Classification is deterministic: it reads at
  most the first 8,192 characters with a non-backtracking matcher and no timeout. New `ExperienceInjectionOptions.FailureDetail` (`AttemptFailureDetail.ErrorClass`, the
  default; `Excerpt`, which adds the error's first line, cut to 120 characters, neutralized and quoted; or `None`).
  An undefined value is refused when the provider is constructed.
- **Public API.** `HistoricalReferenceWriter` loses `ApproachPrefix`, `ApproachSuffix`, `ApproachSeparator`,
  `ApproachClamped`, `ApproachArgumentsSuffix`, `ApproachGrantArgumentsSuffix`, `ApproachArgumentsClamped` and
  `NoToolsUsed`, and gains `NoToolCalled`, `ToolSeparator`, `OutcomeSeparator`, `AttemptCompleted`, `AttemptFailed`,
  `AttemptToolsClamped`, `AttemptArgumentsClamped`, `WorkedSuffix`, `MaxTriedAttempts` and `MaxErrorExcerptLength`. New enum
  `AttemptFailureDetail`. `ApproachWithheld` now reads "The grant withholds this lesson's attempts.". A host that
  matched the old `Approach:` text must match the new lines.
- **The default reflector's lesson (new records only).** `DefaultExperienceReflector.TemplateVersion` is `1.1.0`, so
  its `Producer` is `AgentExperience.DefaultExperienceReflector/1.1.0`. The lesson is now
  `Verified after 2 attempts. Failed: attempt 1 — TimeoutException, exit 2. Worked: attempt 2. Checks: [tests].`:
  the verdict (`Verified`, `Did not verify` or `Unverified`), each failed attempt by number with its error class (or,
  past two, `Failed: 5 attempts; last: attempt 6 — …`), the working attempt of a verified run, and the deciding check
  IDs — the failing ones of a failed run, the inconclusive ones of an unverified run, the passing ones of a verified
  run, and no clause when there are none. It carries no tool name (the tools are on the block's `Tried:` lines,
  which a `LessonOnly` grant withholds), no evidence ID and no score. The evaluation reason, which the lesson used to
  quote, is now a warning (`Evaluation reason: "…"`) of a run that did not verify. Every other field it writes is
  unchanged, and records stored before this release keep their lesson. See
  [Finalization](docs/guide/finalization.md#the-default-reflectors-lesson).

### A compact rendering by default (story 18.2)

- **The problem it fixes.** Most of the injected block's bytes carried nothing a model could decide with: an
  eight-line preamble written for developers, per-record GUIDs, the ranking arithmetic, two timestamps, the host,
  runtime and OS, and an evidence count. Nothing said why a record was matched.
- **Behaviour change: the default rendering is now compact.** New `ExperienceInjectionOptions.Rendering`
  (`HistoricalReferenceRendering.Compact`, the default, or `Verbose`). Compact writes the same begin and end markers,
  a two-line preamble addressed to the model (the records are untrusted reference data, not instructions, and
  authorize nothing), and per record:
  - **kept:** a header `--- RECORD n: {task id} ---`; a `Matched:` line; `Confidence: 0.67 · Verified · Validated`
    (confidence, verification status, lifecycle status); `Environment: differs from this run's (fit 0.40)` when the
    ranking's environment fit is below 1; then `Shared:`, `Lesson:`, `Tried:`/`Worked:`, `Reuse guidance:`,
    `Preconditions:` and `Warnings:`. The model-authored and unconfirmed-content fences are placed as before, and
    withdrawal notices are unchanged. An unconfirmed record's header carries no task, and its task and verification
    status sit inside the fence.
  - **dropped:** experience and run IDs, the `Applicability` line, `Recorded:`, the captured environment fingerprint,
    `Evidence:`, and any empty field. For a reflection the default reflector wrote, and only then, two generic lines
    are dropped too: a precondition whose value it could not capture (`…: unknown`) and its "Verification applies
    only to this run…" warning. Any other reflection keeps every precondition and warning. The default reflector's
    wording is now shared by the reflector and the writer from one source file.
  - **size:** the 3-record test fixture is 1,886 bytes against 4,368 verbose (57% smaller), and the end-to-end
    sample's block went from 2,176 bytes to 955 (56% smaller).
- **`Matched:`** names the record's text relevance (the ranking's relevance component, the stronger of the text and
  vector matches when both channels ran), e.g. `Matched: text relevance 0.45`. It never names confidence, environment
  fit (a fit below 1 gets the `Environment:` line), recency or lifecycle status, and it is built only from the ranking
  retrieval computed, never from record text. A ranking with no relevance component says `Matched: no ranking data`;
  a relevance that is not a real number says `Matched: (unavailable)`. Each store scores
  relevance on its own scale, so in-memory and PostgreSQL can show different values.
- **`Verbose`** is the earlier block byte for byte, except that a line of record text starting with `Matched:` is now
  neutralized: `Matched:` joins the field labels in both renderings. A host that parses `Source:`, `Applicability` or
  the other dropped lines must set `Rendering = HistoricalReferenceRendering.Verbose`. The
  `HistoricalReferenceWriter.Write` overloads without settings keep rendering `Verbose`.
- **Budgets apply to the block as rendered**, so more compact records fit in `Limits.MaxBytes` and in the session
  budget. `BlockOverheadBytes` and `RetractionBlockBytes`, which validate the budgets, stay the larger verbose sizes.
- **The message role is configurable.** New `ExperienceInjectionOptions.MessageRole`
  (`HistoricalReferenceMessageRole.User`, the default and the previous behaviour, or `System`). The text and the
  `AgentExperience.HistoricalReference` marker are the same either way. Some chat APIs reject, move or merge a system
  message that is not first, or allow only one, so test `System` with your provider. Undefined `Rendering` or
  `MessageRole` values are refused when the provider is constructed.
- **Public API.** New enums `HistoricalReferenceRendering` and `HistoricalReferenceMessageRole`, the two options, the
  record `HistoricalReferenceWriteSettings` (rendering, failure detail, content confirmation) and a `Write` overload
  that takes it and renders exactly as the provider does, and `HistoricalReferenceWriter.MatchedWithoutRanking`. A call
  that passes a literal `null` as the fourth argument of `Write` is now ambiguous; cast it to the intended type. See
  [Injection](docs/guide/injection.md#the-payload).

### The task text can be derived from the conversation (story 18.3)

- **The problem it fixes.** Retrieval matches on `RetrieveExperienceRequest.TaskText`, which every host had to build
  itself in `ResolveRequest`, and `ExperienceRunDescriptor.TaskDescription` was usually left null, so stored records
  were findable only by their task ID and lesson.
- **New `ExperienceTaskText.Derive(messages, maxLength = 512)`** and `Derive(messages, maxLength,
  followUpThreshold)`, with the public constants `DefaultMaxLength` (512), `FollowUpThreshold` (64) and
  `FollowUpSeparator` (" — "). One deterministic rule, no model call, no language-specific processing:
  - the latest request is the last `User` message with text whose MAF source is `External` (or unset) and that comes
    after the last `ChatHistory` message; a custom, provider or history source never counts. A new input with no text
    (image-only) gives `null`. Its text parts are joined with a space. It relies on MAF stamping replayed history as
    `ChatHistory`; an unstamped custom history component's messages count as new input;
  - when it is shorter than the threshold (64 UTF-16 code units; 0 turns this off), the previous user message with
    text, `External` or `ChatHistory` only, is put before it, separated by " — " (`Deploy service X to prod — and
    retry`). The rule is length-only, so a short standalone request is joined too. Across turns this needs the history
    replayed into the request; with service-managed history (a conversation ID) there is nothing to join;
  - a message containing the Historical Reference begin marker, or carrying its stamp, is skipped entirely: a user who
    pastes an earlier block into a prompt gets no derived text from that message, their own words in it included;
  - control and format characters (and unpaired surrogates) are removed, except a zero-width joiner or non-joiner
    between kept letters, marks or symbols and a well-formed emoji subdivision flag's tags; tag characters anywhere
    else are removed. Whitespace is collapsed and the ends trimmed; no Unicode normalization;
  - lengths are UTF-16 code units; no cut splits a surrogate pair or leaves a joiner or unfinished flag tags at the
    end. An over-long join cuts the previous message so the separator and the whole latest request fit; a latest
    request over the maximum on its own is cut and the previous message dropped. `null` when nothing qualifies, the
    result is blank, or the maximum cannot hold even the first character. `maxLength` must be 1 to
    `ExperienceCandidateQuery.MaxTaskTextLength` (4096) and the threshold non-negative, or it throws
    `ArgumentOutOfRangeException`.
- **New `DerivedTaskText` on `ExperienceInjectionContext` and `ExperienceRunContext`.** Read-only, computed on first
  read from a snapshot of the messages, at most once per instance (a `with` copy computes its own). If reading the
  messages throws, the first read throws and later reads return `null`. In the injection provider it reads the
  request before MAF's input filter, history included, so a follow-up joins the previous turn; `context.Messages` is
  unchanged. In capture it reads only the invocation's own input, so the two differ for a short follow-up whose
  previous request is only in history; the capture guide shows deriving from the host's own history instead. The
  positional parameters are unchanged; the property is left out of both records' equality and `ToString` (their
  `Equals` and `GetHashCode` are now written out, with the same meaning), so equal contexts can derive different text.
- **What changes for every invocation.** Nothing derives or stores the text unless the host reads it. But
  `ExperienceContextProvider` now overrides `InvokingCoreAsync` and keeps a reference to the full unfiltered request
  (history included) on every invocation until the resolver's context is built, whether or not `DerivedTaskText` is
  read. A request that is not a list or array is materialized into an array once, and MAF reads that same array.
- **It is the user's own words.** It is used for retrieval as written, and stored as `TaskDescription` it is stored
  unsanitized, as a task description always was. Hosts whose prompts can hold sensitive data should redact it first.
- **Docs.** The README quick start and the injection and capture guides use it, returning `null` from `ResolveRequest`
  to skip injection when there is no text. See [Injection: the task text](docs/guide/injection.md#the-task-text).

### Async resolvers (story 18.4)

- **The problem it fixes.** `ExperienceInjectionOptions.ResolveRequest` and `ExperienceCaptureOptions.ResolveFinalization`
  were synchronous and saw no cancellation token, though building a retrieval request usually means looking up the
  caller's authorization and scope, and finalizing means running checks or reading CI for the evidence. Hosts were
  pushed into sync-over-async or precomputation.
- **New `ExperienceInjectionOptions.ResolveRequestAsync`**
  (`Func<ExperienceInjectionContext, CancellationToken, ValueTask<RetrieveExperienceRequest?>>?`). Awaited at the
  point `ResolveRequest` is called, with the invocation's token. Its result is treated exactly as the sync one's:
  `null` skips (`Skipped`), a throw or a faulted task injects nothing and is reported (`Failed`, the reason naming
  `ResolveRequestAsync`), and the request is validated, traced and counted the same way. An
  `OperationCanceledException` while the invocation's own token is cancelled propagates to the caller, as the
  provider's cancellation does elsewhere, and no result is reported; any other cancellation is `Failed`. Its time is
  host time on top of the pre-model budget; no timeout is put around it.
- **New `ExperienceCaptureOptions.ResolveFinalizationAsync`**
  (`Func<ExperienceFinalizationContext, CancellationToken, ValueTask<FinalizeExperienceRequest?>>?`). Awaited where
  `ResolveFinalization` is called, with the `FinalizationTimeout`-bounded token finalization already runs under. A
  throw, a faulted task, or a request for another run is reported through `OnCaptureFailure` exactly as with the sync
  form, the reason naming `ResolveFinalizationAsync`. When `FinalizationTimeout` fires during the await, only the
  timeout is reported (stage `Finalize`), and a request returned after the timeout, or after the capture lifetime was
  disposed, finalizes nothing.
- **Exactly one of each pair.** The provider's constructor throws `ArgumentException` naming both when neither or
  both of `ResolveRequest` and `ResolveRequestAsync` are set. `UseExperienceCapture` keeps "`FinalizationService` and
  a resolver together, or neither", counting either resolver, and refuses both resolvers with `ArgumentException`.
- **Breaking.** `ExperienceInjectionOptions.ResolveRequest` is no longer `required` and its type is now nullable
  (`Func<ExperienceInjectionContext, RetrieveExperienceRequest?>?`), so an object initializer that omits it compiles
  and is refused at construction instead, and code reading the property must handle `null`. A provider built with no
  resolver now throws `ArgumentException`, not `ArgumentNullException` (its subclass): a `catch` or test that expects
  `ArgumentNullException` exactly must change. The injection failure reason for a throwing resolver now names the form
  that ran: `The injection request resolver (ResolveRequest) threw.` (was `The injection request resolver threw.`).
- **Unchanged.** The sync forms behave exactly as before. `ResolveRun` and `DecideInjection` stay synchronous;
  `DecideInjection` runs while the session's lock is held and has no async hook: prefetch what it needs, keyed by
  scope, in `ResolveRequestAsync`, or decide offline.
- **Docs.** The README quick start and the injection, capture and finalization guides use the async forms. See
  [Injection: options](docs/guide/injection.md#options) and
  [Finalization](docs/guide/finalization.md#finalizing-from-the-maf-adapter).

### One-call setup (story 18.5)

- **The problem it fixes.** Getting one MAF agent to learn took about 80 lines and 14 `using`s: a hand-built
  `SanitizationPolicy` under magic-string kinds, `CaptureLimits`, each store and service registered by hand, a
  hand-built `ExperienceContextProvider`, and a `FinalizeExperienceRequest` whose round, timestamp and storage decision
  the library could fill itself.
- **New `services.AddAgentExperience(options => ...)`** (MAF package, in the `Microsoft.Extensions.DependencyInjection`
  namespace). Registers Core with safe defaults, retrieval and the injection provider, each with `TryAdd`, and returns
  an `IAgentExperienceBuilder`. Calling it twice throws. `AgentExperienceOptions`: `ResolveIdentity` (required, async,
  returns an `ExperienceIdentity(Authorization, Scope)` from the host's authentication; `null` means the invocation is
  neither injected nor captured), `TaskId` (default `"default"`) or `ResolveTaskId`, `Verify` (optional, async,
  returns an `ExperienceVerification(RequiredChecks, Evidence, ArtifactRevision)`; `null` stores nothing; without it
  runs are captured but never stored), `Sanitization`, `CaptureLimits`, `Injection` and `Capture` hooks applied after
  the defaults, and `TimeProvider`. The library fills in the rest of the finalization request: a new
  `ClosedVerificationRound` (identifier from `NewId`), the verdict's revision as the current one,
  `StorageDecision.Permit`, the finalization time and the identity's authorization.
  `ExperienceVerificationContext.CreateEvidence(...)` builds evidence bound to the round.
- **Storage on the builder.** `.UseInMemoryStorageForDevelopment()` (InMemory package, the same development-only
  guard) and `.UsePostgres(connectionString)` or `.UsePostgres(dataSource)` (Postgres package: the data source, the
  record store and the candidate source; a connection string beside an already-registered data source throws rather
  than being ignored). Without a choice, `UseAgentExperience` and `GetAgentExperienceContextProvider` throw
  `InvalidOperationException` naming the choices. Migrations stay an explicit owner step. The builder methods extend
  the new BCL-only `AgentExperience.Abstractions.IAgentExperienceBuilder<TServices>`, so the storage packages still
  depend on Abstractions only.
- **Agent side.** `AIAgentBuilder.UseAgentExperience(provider)` wires capture and, through `Verify`, finalization.
  MAF's agent builder takes only message-level context providers, which do not see the session and history injection
  works with, so the provider goes on `ChatClientAgentOptions.AIContextProviders` through
  `provider.GetAgentExperienceContextProvider()`; `UseAgentExperience` alone captures but injects nothing, and every agent
  shares the container's one injection configuration. Capture resolves the identity once per invocation, bounded by
  `IdentityTimeout` (default 5 seconds), and injection reuses the outcome; injection matches on `DerivedTaskText` and
  skips when there is none. A run keeps the identity it was opened under: continuing it under another is refused, and a
  run the open-run bound closes is finalized under its own. `FinalizationTimeout` defaults to 2 minutes under the
  one-call setup, since `Verify` often runs tests; evidence naming another round or revision is reported and nothing is
  stored; `ExperienceVerificationContext.Create(...)` lets a host unit-test `Verify`. The capture of each agent is
  disposed with the container. A second storage choice, a different data source beside a registered one, or an
  `options.TimeProvider` other than a registered `TimeProvider` throws.
- **Defaults.** New `AgentExperienceDefaults` (Core): `Sanitization` keeps **no** tool argument until the host
  allowlists it, keeps the result text, and redacts `password`, `secret`, `token`, `apiKey`, `api_key`,
  `authorization` and `connectionString` in any case (depth 4, 50 fields, values up to 1,000,000 characters, so long
  output is cut by capture rather than refused by the sanitizer); `SanitizationAllowing(...)` keeps named arguments;
  `CaptureLimits` is 10 attempts, 50 tool calls, 4,000 characters for results and errors. New `SanitizationKinds.ToolArguments`,
  `ToolResult` and `ValueField` constants replace the magic strings in the docs and samples.
- **Changed.** The properties of `ExperienceCaptureOptions` and `ExperienceInjectionOptions` are now `set` rather than
  `init`, so the hooks can adjust them. `UseExperienceCapture` and the `ExperienceContextProvider` constructor take a
  copy, so changing the options after the agent or provider is built has no effect, exactly as before.
- **Breaking (binary).** Changing `init` to `set` changes the setters' signatures: source compatible, but an assembly
  compiled against an earlier preview that sets these properties must be recompiled.
- **Unchanged.** The explicit wiring behaves exactly as before.
- **Docs.** The README quick start uses the one-call setup, in-memory first, with a short PostgreSQL variant; the
  previous long form is now [Explicit wiring](docs/guide/deployment.md#explicit-wiring), beside
  [The one-call setup](docs/guide/deployment.md#the-one-call-setup). The public API snapshots now include types in the
  `Microsoft.Extensions.DependencyInjection` namespace, which the generator left out by default.

### Verified reuse can move confidence (story 18.6)

- **The problem it fixes.** Confidence moved only when a host submitted evidence itself, so for almost every host
  every lesson stayed at 2/3 forever, although the library already knows when a run that was given a lesson then
  verified on the same task.
- **New `ExperienceFinalizationOptions.ReuseEvidence`** (`ReuseEvidenceMode.Off`, the default, or `SameTask`) and
  **`ContradictOnFailure`** (default `false`). Under `SameTask`, once a run's record is durable (including an
  `AlreadyFinalized` replay), finalization submits machine evidence through `ApplyEvidenceAsync` for each record the
  run was exposed to that is readable in its scope and has the same `TaskId`: supporting when the run verified,
  contradicting when it failed and `ContradictOnFailure` is set. It is bound to the run and the round its finalization
  closed, so independence verification applies unchanged. Evidence and event IDs are derived from the run, its scope,
  the record and the kind (`ExperienceFinalizationService.ReuseEvidenceIdFor`, `ReuseEventIdFor`), so a replay counts
  nothing twice and a writer in another scope cannot take them first; the producer is `ExperienceFinalizationService.ReuseEvidenceProducer`. An undefined mode is refused.
- **Reported, never acted on.** New `FinalizeExperienceResult.ReuseEvidence`, one `ReuseEvidenceResult` per distinct
  record the run was given (`ExperienceId`, `Kind`, `Outcome`, `Reason`, plus `Counted`, `Replay`, `Skipped`,
  `Refusal` and `ExceptionType`), and `ReuseEvidenceTruncated`. A record on another task, shared by a grant, from this
  run, or not readable in the run's scope is reported as `Skipped`; a refusal is reported and not retried; an exception
  is caught and reported; a replay reports `Replay = true` and `Counted = false`. The step is bounded by the new
  `ExperienceFinalizationOptions.ReuseEvidenceTimeout` (10 seconds); a cancellation or the timeout stops it and sets
  `ReuseEvidenceTruncated`. The finalization outcome never changes, and finalizing the run again recovers lost evidence
  (and backfills a run finalized with the option off). Each submission is a `confidence.apply` operation nested in
  `finalize`, which carries the new span attribute `agentexperience.reuse_evidence.submitted`.
- **One-call setup.** `AgentExperienceOptions.ReuseEvidence` and `ContradictOnFailure` set the same options;
  `ContradictOnFailure` without `ReuseEvidence` is refused. They replace an `ExperienceFinalizationOptions` registered as
  an instance before `AddAgentExperience` with a copy carrying them, refuse one registered through a factory or a
  type, and building an agent throws when one registered afterwards disagrees with them.
- **Unchanged by default.** With the option off nothing new happens. See
  [Letting reuse move confidence](docs/guide/confidence.md#letting-reuse-move-confidence).

### Documentation restructured (story 18.7)

- The README now leads with a real injected lesson, the quick start and which reflector to choose; a new
  [Concepts in five minutes](docs/guide/concepts.md) page explains the model; user-facing text names releases instead of internal story numbers. No behaviour change.

## 0.1.0-preview.6

Excluding model-authored lessons now happens inside retrieval (story 14.4), so
`ModelAuthoredLessons = Exclude` returns the top deterministic lessons instead of
fewer. The release also fixes a flaky test.

Upgrading from `0.1.0-preview.5` applies migration `0021`. Run the migrator before deploying the new build. **It holds
an `ACCESS EXCLUSIVE` lock on `experience_records`, blocking every read and write of the table, until its backfill
commits, and it rewrites every row it backfills (recomputing its stored `search_vector`)**: every live record in a
crypto-shredding deployment, usually few in a plaintext one. It runs under the command timeout (30 seconds by
default). Above about 20,000 rows to rewrite, take the two-step route in
[0021: reflection authorship](docs/guide/postgres-schema.md#0021-reflection-authorship): the script by hand without its
backfill, then the backfill in batches. During a rolling deploy, instances still on `0.1.0-preview.5` seal new records
without the flag; see the residual below.

### Exclusion of model-authored lessons moves into retrieval (story 14.4)

- **The problem it fixes.** With `ModelAuthoredLessons = Exclude`, story 14.3 dropped model-authored records only after
  retrieval had applied its limit, so when model-authored records filled the candidate window
  (`RetrieveExperienceRequest.Limit`, or the policy's candidate limit), deterministic records below it were never
  found and fewer lessons were injected than existed. No model-written text leaked.
- **Port.** `ExperienceCandidateQuery` and `ExperienceVectorQuery` gain an init property `ExcludeModelAuthored`
  (default `false`). When set, a source leaves out every record whose reflection authorship is anything but
  `Deterministic` (fail closed on undefined values; a record with no reflection is kept), **before** its limit, like
  the status and confidence filters. A source that cannot honour it must answer `Invalid` rather than ignore it. The
  store conformance suite has a new rule: with model-authored records ranked above deterministic ones and
  `Limit = N`, an excluding search returns the top N deterministic records; a non-excluding search is unchanged. The
  in-memory and PostgreSQL stores pass it, and the end-to-end sample's in-memory double honours it too.
- **Retrieval.** `RetrieveExperienceRequest` gains an init property `ExcludeModelAuthored` (default `false`), which
  `ExperienceRetrievalService` passes to the text channel and, when hybrid, to the vector channel. It then excludes
  any model-authored record a source still returned (fail closed; a record with no reflection is kept), listing it in
  `Excluded` under the new `RetrievalExclusionReason.ModelAuthored`. With the property unset, every query and every
  result is exactly what it was.
- **Injection.** `ExperienceContextProvider` sets it on the resolved request when `ModelAuthoredLessons = Exclude`
  (and never clears a host's own `true`). It keeps its own check before the record limit and after the re-read, and
  `InjectionOmissionReason.ModelAuthored` for anything it still drops; since retrieval now excludes such records
  first, they appear in the injection result's `Excluded` instead of `Omitted`.
- **Migration `0021_reflection_authorship`** (`PostgresExperienceRecordSchema.ReflectionAuthorshipScriptName`): the
  nullable `experience_records.reflection_model_authored` flag, added with a catalog-only `false` default that is then
  dropped; `agent_experience.payload_reflection_model_authored(payload, payload_version)`, the one rule (`NULL` for a
  sealed payload; otherwise `false` when the reflection is null, has no `authorship` member, or one that is null or
  `Deterministic` in any ASCII case, and `true` for anything else, an unreadable payload of an unknown version
  included); a backfill of only the live rows that differ from `false` (sealed rows to `NULL`, model-authored
  plaintext rows to `true`); a `BEFORE INSERT OR UPDATE` trigger that derives it for every live unsealed row on every
  write and gives every tombstone the fixed `false`; and a `NOT VALID` check that a tombstone carries nothing else. The
  store writes it from the reflection for a sealed record, and the application role cannot update it. The PostgreSQL
  text search and the vectors package's search and compatibility probes filter `reflection_model_authored IS NOT TRUE`
  only when asked. No index, no privilege change; the row-level security policies cover it as they cover every
  column. Under the out-of-band HNSW index the vector channel applies it after the index walk, as it does its other
  filters, so it can return fewer than its limit; `hnsw.iterative_scan` (pgvector 0.8+) is the host's lever.
- **Residual: a sealed row stored without its flag.** A row sealed before `0021` (a migration cannot open it), one an
  instance still on `0.1.0-preview.5` seals during a rolling deploy, or one any writer inserts without the flag keeps
  `NULL`, and an excluding search still returns it, so it takes a place in its source's candidate window; the
  retrieval service opens it and excludes it. Find them with the query in
  [0021: reflection authorship](docs/guide/postgres-schema.md#0021-reflection-authorship), read each through the
  store, and revoke, supersede or erase the model-authored ones. In crypto-shredding mode the flag is one bit of
  plaintext metadata about a sealed lesson, like the status.
- **Tests.** The conformance rule; Core tests that both channels receive the flag and that Core excludes what a
  source returns anyway; injection tests over a fake source, the in-memory store, the sample's double and a real
  PostgreSQL (five model-authored records ranked above three deterministic ones, retrieval limit three); PostgreSQL
  tests of the write, the trigger (unknown payload versions included), the application role's refusal, sealing,
  erasure, grant-shared records, the exact-scope fallback, the backfill on a pre-`0021` database (null, case, number,
  object and unknown values) and the sealed residual; vector-channel and hybrid tests, grant-shared included; and an
  upgrade-suite check that a test-written plaintext payload saying `Model` in a `0.1.0-preview.2` database is flagged
  and left out, while every sealed row stays unknown and every tombstone `false`.

## 0.1.0-preview.5

The hardening release. It adds:
- tests that upgrade databases the published previews created, plus benchmarks (Epic 12);
- opt-in HMAC signing of finalization provenance (Epic 13);
- screening of every reflection, an optional model-backed reflector, and labelling and an exclude option for the
  lessons it writes (Epic 14);
- opt-in PostgreSQL row-level security behind the application role (Epic 15).

Upgrading from `0.1.0-preview.4` applies migration `0019` and, with the vectors package, `0020`. Everything new is off
by default apart from reflection screening. Read the story sections below before you upgrade.

### Model-authored lessons are marked and guarded (story 14.3)

- **Authorship, declared by the reflector.** `Reflection` gains `Authorship` (`ReflectionAuthorship.Deterministic =
  0`, the default, or `Model = 1`), an init property. It is self-declared: `ChatClientExperienceReflector` always sets
  `Model`, and **a host's own model-backed `IExperienceReflector` must set it too** (the contract now says so). Nothing
  infers it from `Producer`. A deterministic reflection serializes as before (System.Text.Json omits the default). The
  PostgreSQL store writes an optional `authorship` member in the version-1 reflection payload only when it is not
  `Deterministic` (no migration; absent reads back as `Deterministic`; sealed with the rest in crypto-shredding mode);
  the in-memory store keeps it. An undefined value is `Invalid` in both stores and refused by screening as the new
  `ReflectionScreeningRefusal.UndefinedAuthorship`. Neither authorship nor the reflection's text is covered by
  provenance signing (new documented boundary KL-18).
- **A best-effort content guard for model-authored text.** After hygiene and the host sanitizer, screening refuses a
  reflection whose authorship is anything but `Deterministic` with the new `ReflectionScreeningRefusal.UnsafeContent`
  when a free-text field holds credential-shaped text (private keys, AWS, OpenAI/Anthropic `sk-`, GitHub, GitLab,
  Google, Slack, JWT, Azure `AccountKey=`/`SharedAccessSignature=`, `password=`/`pwd=`); a word mixing Latin with
  Cyrillic or Greek letters; listed instruction-override phrasing (normalized, with look-alikes folded, letters run
  together, and across all fields joined); a `data:`, `javascript:`, `vbscript:` or `file:` link or a UNC path; or a
  whole URL, hostname (last label a listed TLD, an `xn--` or a non-ASCII label) or IP address that does not equal a
  whole token in the run content the reflector was given, after undoing `[.]`, `(dot)`, ` dot ` and full-width dots.
  The new `IReflectionRunContent` lets a reflector declare exactly what it sent; `ChatClientExperienceReflector`
  implements it with the same clipping as its prompt. With no run content, every link is refused. The reason names
  the field and the rule, never the matched text.
- **Injection labels every model-written field and can exclude them.** A model-authored entry carries its `Approach:`
  line first, then `HistoricalReferenceWriter.ModelAuthoredLine`, the lesson, reuse guidance, preconditions and
  warnings, then `HistoricalReferenceWriter.ModelAuthoredEndLine`. Any authorship other than `Deterministic` counts.
  The new `ExperienceInjectionOptions.ModelAuthoredLessons` (`ModelAuthoredLessonPolicy.Include`, the default, or
  `Exclude`) omits such records as the new `InjectionOmissionReason.ModelAuthored`, with no detail, **before** the
  record limit (so they cannot crowd deterministic records out) and again after the re-read.
- **Behaviour change for deterministic records:** none to their rendering, except that a line of a record's own text
  that starts with `Authored:` or `End authored:` is now neutralized like any other field label. A test pins a
  deterministic entry byte for byte against the pre-14.3 rendering.
- **Behaviour change in `ChatClientExperienceReflector`'s `Producer`:** a model ID longer than 128 characters
  (`MaxProducerModelLength`), or one containing `:` or `/`, or one shaped like a URL, hostname or IP address, is now
  dropped (falling back to `ModelName`, then `unknown`) instead of being cut and named. The reflection is not refused.
- **Upgrade note.** Records the story 14.2 `ChatClientExperienceReflector` wrote before this release read back as
  `Deterministic`, are not relabelled, and `Exclude` does not cover them. Find them by `Producer` (it starts with
  `AgentExperience.ChatClientExperienceReflector/`) and revoke or erase them; see
  [Limits of model-authored lessons](docs/guide/finalization.md#limits-of-model-authored-lessons).
- **The guard is a filter, not a boundary.** Content echoed from the run (a poisoned tool result included) passes by
  design; paraphrased instructions, whitespace-split dots, encoded secrets and mid-line contradictions pass too; and a
  legitimate lesson can be refused (`ASP.NET`, `1.0.0.0`). The label, `Exclude` and the approval boundary are the
  controls to rely on. This closes the deferred-work item story 14.2 opened.

### Row-level security (story 15.1)

- **An opt-in second isolation layer.** `ExperienceApplicationRoleOptions.EnableRowLevelSecurity` (default `false`)
  makes `ApplyApplicationRolePrivilegesAsync` re-create the canonical helper functions and policies, enable PostgreSQL row-level security on
  every table the application role reads or writes by scope, and verify it -- policy names, commands, roles and
  expressions included -- in the same transaction as the privileges. It is never forced; `false` disables it again,
  so the setting is declarative. The call refuses missing or extra policies, an unreadable `experience_grants`, a role
  or database default for the declared settings, and an application role that can reach `BYPASSRLS`. See
  [Enabling row-level security](docs/guide/deployment.md#enabling-row-level-security).
- **Migration `0019_row_level_security`**, and the vectors package's **`0020_embeddings_row_level_security`**: the
  `rls_*` policies and five helper functions (none `SECURITY DEFINER`). They switch nothing on. A row is admitted only
  inside the authorization bounds the current operation declared, plus, for reading, the records and embeddings a live
  grant shares with those bounds; a recipient reads only live grants; an access row must describe a live grant that
  names it exactly; nothing is admitted when nothing was declared. `0019` also redefines the four `SECURITY DEFINER`
  functions (erasure, grant purge, access purge, sealing), bodies unchanged, with a guard that refuses a scope outside
  declared bounds and, while row-level security is enabled, an undeclared caller that is neither a member of the
  tables' owner nor `BYPASSRLS`. Run both migrators before enabling. `PostgresExperienceRecordSchema.RowLevelSecurityScriptName` and
  `ExperienceVectorSchema.EmbeddingsRowLevelSecurityScriptName` name them.
- **Every store operation now runs in a transaction and declares its bounds first**, whether or not row-level
  security is on: the host `AuthorizationContext`'s tenant, application, project, team, agent and user bounds, all
  seven settings every transaction, transaction-locally, as parameters. Operations that were one autocommitted
  statement are wrapped in a transaction; the SQL they send is otherwise unchanged. An audited read's access rows are
  appended under the reader's authorization; `IExperienceGrantAccessLog.RecordAsync` called directly declares the
  recipient scope its rows name.
- **Behaviour change, with row-level security on only.** Every role but the owner sees nothing until it declares
  bounds, so a reporting role or a hand-run script as the application role must declare them too. Presetting the
  settings (connection-string `-c` options, session-level `SET` on pooled connections) is not supported.
- **Cost.** About 0.7 ms per operation for the transaction and the declaration, and about 1 ms more per text search
  with row-level security on, on the benchmark machine. **Text search does not use its GIN index beneath the
  policies** (`@@` is not leakproof): it scans the declared tenant's live records through the scope index. The vector
  channel keeps its HNSW index. See [Row-level security](docs/benchmarks.md#row-level-security-story-151).
- **What it does not do**: bind a compromised application role, which can declare any bounds itself; enforce grant
  disclosure levels; hide that a colliding ID exists in another tenant; check the record a feedback exposure names.
  That is [KL-17](docs/known-limits.md#documented-boundaries), a new documented boundary.
- **Tests.** CI runs the store, vector (with the conformance suites), sample and upgrade suites again on every
  PostgreSQL major with `AGENTEXPERIENCE_TEST_RLS=on`, and the store and vector suites with both
  `AGENTEXPERIENCE_TEST_RLS=on` and `AGENTEXPERIENCE_TEST_ENCRYPTION=on`; `RELEASING.md` step 3 does the same. In RLS
  mode the suites' own hand-written SQL runs through a fixture role that inherits the application role's privileges
  with `BYPASSRLS`, while every store runs as the application role behind the policies. `PostgresRowLevelSecurityTests`
  proves the layer in every mode, table by table, including the grant rules, the owner's functions, policy tampering,
  preset defaults, access-log forgery and transaction locality. The upgrade suite's schema comparison now covers each
  table's row-level security and every policy.

### Model-backed reflector (story 14.2)

- **An optional `ChatClientExperienceReflector` in `AgentExperience.MicrosoftAgentFramework`.** It implements
  `IExperienceReflector` over a host-supplied `IChatClient` and asks the model, through structured output, only for the
  free text: `lesson`, `successfulApproaches`, `failedApproaches`, `preconditions`, `warnings` and `reuseGuidance`.
  Every bound field is copied from the `ReflectionRequest`, so story 5.5's binding check and story 14.1's screening
  apply unchanged; extra members the model returns, and any reasoning content, are ignored and never stored. The
  answer is parsed strictly (no duplicate properties, comments or trailing commas; a code fence only as the whole
  answer). `Producer` is `AgentExperience.ChatClientExperienceReflector/1.0.0 (<model>)`, the model restricted to
  `[A-Za-z0-9._:/@+-]` and cut to fit 200 characters.
- **Off by default, and never evicts silently.** `AddAgentExperienceChatClientReflector(configure,
  chatClientServiceKey, replaceExisting)` replaces the default reflector; any other registered reflector needs
  `replaceExisting: true`. It resolves a registered, optionally keyed (no fallback) `IChatClient`, and refuses a scoped
  one. `DefaultExperienceReflector` stays the default and is unchanged.
- **Privacy: it sends sanitized captured run content to the host's model provider**: the task text (or task ID),
  each attempt's sequence number and ordered tool names, each tool call's and attempt's result and error clipped to
  `MaxQuotedLength` (500), and the verification status, check IDs and evidence IDs. Every captured string, tool
  names included, is escaped before it is clipped, so none can create structure. The message is capped at
  `MaxPromptLength` (16,000) by bounding the header's lists and dropping the oldest attempts first; when not even the
  smallest header fits, nothing is sent. The system prompt is the public constant
  `ChatClientExperienceReflector.SystemPrompt`, and `BuildPrompt` returns the data message.
- **No tools, bounded output.** Every call offers no tools, re-asserted after `ConfigureChatOptions`; a client whose
  pipeline contains a `FunctionInvokingChatClient` is refused at construction; a response carrying a function call
  fails. `MaxOutputTokens` (2,048) is set on every call, and a text answer over `MaxAnswerBytes` (64 KB) is refused
  unread.
- **Failure quarantines.** The new `ReflectionFailedException` (`ReflectionFailureKind`: `ModelCallFailed`,
  `TimedOut`, `Unparseable`, `EmptyLesson`, `ToolCallAttempted`, `OutputTooLarge`, `PromptTooLarge`,
  `ChatOptionsCallbackFailed`) carries no model text and no inner exception, only the cause's type name. The timeout
  is enforced even on a client that ignores its token; the caller's cancellation still propagates.
- **Options:** `ChatClientExperienceReflectorOptions` with `ModelName`, `Timeout`, `MaxQuotedLength`,
  `MaxPromptLength`, `MaxOutputTokens`, `Temperature` (0 by default) and `ConfigureChatOptions`, all validated and
  copied when the reflector is built.
- Documented in the finalization guide's new "Model-backed reflection" section (with its limits: captured tool
  output can steer a model-authored lesson, which the new open deferred-work item records) and the MAF README.

### Reflection screening (story 14.1)

- **Finalization screens what a reflector wrote before the record is created.** After the binding check, the six
  free-text fields (`Lesson`, `SuccessfulApproaches`, `FailedApproaches`, `Preconditions`, `Warnings`,
  `ReuseGuidance`) and `Producer` pass two layers, for every reflector, the default one included:
  - **built-in hygiene:** the new `ReflectionLimits` (4,000 characters of lesson and of reuse guidance, 1,000 per list
    item, 32 items per list, 200 characters of `Producer`, and a 5-second `SanitizerTimeout`); invisible characters
    removed; a field empty afterwards counts as absent, and a missing lesson or producer refuses the reflection;
  - **the host's `ISanitizer`,** as a payload of the new kind `ExperienceReflection`
    (`ReflectionScreening.PayloadKind`), bounded by `SanitizerTimeout`.
- **Over-limit text is refused, never truncated.** A refused reflection quarantines the record with a
  `FinalizationFailure` at stage `Reflect` and a content-free reason, exactly as a binding mismatch does. The new
  `FinalizationFailure.ScreeningRefusal` (`ReflectionScreeningRefusal`: `OverLimit`, `MissingLesson`,
  `MissingProducer`, `MissingField`, `Unreadable`, `SanitizerRejected`, `SanitizerFailed`, `SanitizerTimedOut`,
  `FieldOmitted`) says why, and the `finalize` span carries it as `agentexperience.reflection.screening_refusal`.
  A sanitizer that times out, throws, cancels on its own, omits a field or item, or returns something other than an
  allowed payload of text or a rejection quarantines too; the caller's cancellation still propagates. An exception
  behind a refusal is withheld; only its type is kept, in the new `FinalizationFailure.ExceptionType`.
- **Redactions are kept and reported by path.** The new `FinalizeExperienceResult.ReflectionRedactedFieldPaths` lists
  the redacted fields' paths as indexes into the stored lists, never their values. No reflection text reaches
  telemetry, logs or failure reasons.
- **One invisible-character rule, shared with the Historical Reference writer.** Story 8.2's rule now also removes
  variation selectors, the combining grapheme joiner, Hangul fillers and the blank braille pattern, turns the line
  and paragraph separators into spaces, and cuts a run of combining marks to four, in the writer and in screening
  alike; a cross-check test holds the two to it code point by code point.
- **`DefaultSanitizer` has a built-in policy for the one new kind.** `ReflectionScreening.DefaultSanitizationPolicy`
  (`SanitizationPolicyFor(ReflectionLimits.Default)`) applies when the options configure none for
  `ExperienceReflection`; it allows exactly the six fields, bounded by the default limits, and changes nothing. A
  configured policy replaces it. Every other unconfigured kind is still rejected.
- **The default reflector bounds its own output** to `ReflectionLimits.Default`: at most 500 characters of any quoted
  captured text, each field and item cut to its limit with an ellipsis, and a list kept to 32 items, the last saying
  how many more are not listed. Its output for the sample is unchanged, pinned byte for byte, and the golden
  transcript is unchanged. A captured invisible character it quotes is now removed from the stored reflection.
- **Wiring.** `AddAgentExperienceCore` passes the registered `ISanitizer` and an optional registered
  `ExperienceFinalizationOptions` (new, holding `ReflectionLimits`). Hosts that build services by hand use the new
  `ExperienceFinalizationService` constructor overload with `reflectionSanitizer` and `options`; the existing
  overloads screen through a `DefaultSanitizer` bounded by the service's limits. **A host `ISanitizer` must allow the
  `ExperienceReflection` kind**, or every record is quarantined as `SanitizerRejected`.
- Documented in [Screening reflections](docs/guide/finalization.md#screening-reflections), in the telemetry reference,
  and in the `IExperienceReflector` remarks.

### Signed provenance (story 13.1)

- **Finalization can sign what it vouches for** (KL-11, narrowed). Register an `ExperienceProvenanceSigningOptions`
  and `ExperienceFinalizationService` signs every record it creates. The options hold a key ring of keys of at least
  32 bytes (only `KeyIds` is public; keys are copied and never returned), a `CurrentKeyId`, and an optional
  `TrustUnsignedRecordIds` cutover set. The signature is HMAC-SHA256 over a canonical encoding (`aexp-prov:v1`) of the
  record's finalization claims:
  - the record ID;
  - all six scope fields, as strict UTF-8;
  - the source run, the closed round and the origin;
  - the exposures, sorted by big-endian record ID then revision.

  It is stored with the record, in the same create, as the new `ExperienceRecord.ProvenanceSignature`
  (`ExperienceProvenanceSignature`: key ID, algorithm, value). Content fields and counters are not signed.
- **Verification refuses a record finalization did not sign.** With the options registered, a run known through a
  record whose signature is missing, names a key not in the ring, or does not verify (constant-time comparison) is
  refused as `IndependenceRefusal.HostWrittenRun`. The reason is one generic sentence for all three cases and names
  no key material. So a record written through `CreateAsync` claiming `Finalized`, or a payload edited outside the
  library, can no longer pass as finalized.
- **Initial counters are capped.** `ConfidenceEvidenceFilter.VerifiedOnly` leaves out a refused record's initial
  counters. For a record that vouches, it counts at most finalization's own initial validation as verified.
- **The cutover is a list of record IDs, not a date.** `ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync`
  lists the unsigned finalized records in given scopes. Run it once when switching signing on and configure the
  result as `TrustUnsignedRecordIds`. A record created later can never join the set, so backdating a forged record
  gains nothing.
- **Under the `TrustHostSuppliedIdentifiers` opt-out**, a record with no signature or under an unknown key stays
  host-trusted. Only a finalized record whose signature is present, under a key in the ring, and invalid is refused,
  in the confidence path and in feedback attribution alike.
- **Misconfiguration fails at construction.** A ring holding the assessment token key is refused. Finalization's own
  options are refused unless the lifecycle service it is built over checks their current key.
- **A retry does not replay a record that does not vouch.** With signing on, finalizing a run whose stored record
  lacks a valid signature ends `Failed` at the create stage, saying so, instead of `AlreadyFinalized`. A run whose
  scope cannot be encoded (a lone surrogate) ends `Failed` with nothing stored.
- **Rotation:** add a key, switch `CurrentKeyId`. Older signatures verify while their key stays in the ring.
- **No key ring, no change.** Without the options nothing is signed or checked, and every existing test passes
  unmodified. `AddAgentExperienceCore` picks the options up in either registration order, directly or as an explicitly
  registered `IOptions<ExperienceProvenanceSigningOptions>`. Hosts that build services by hand use the new
  `ExperienceLifecycleService` and `ExperienceFinalizationService` constructor overloads that take
  `provenanceSigning`; a finalization service built over a lifecycle service with signing options signs with them by
  default.
- **Storage.** Both stores persist the signature. The PostgreSQL store writes it as an optional version-1 payload
  field, `provenanceSignature`, only when set, so it needs no migration and is sealed with the rest of the payload in
  crypto-shredding mode. Both stores refuse, as `Invalid`:
  - a malformed signature: a key ID or algorithm outside `[A-Za-z0-9._-]{1,64}`, or a value that is empty or longer
    than 512 bytes;
  - a scope field that is not well-formed UTF-16.

  The store conformance suite checks the round trip and each malformation.
- **Store implementers:** an `IExperienceRecordStore` must persist `ExperienceRecord.ProvenanceSignature` as given.
  One that drops it makes every signed record read back unsigned, and, with signing on, their runs vouch for nothing.
- Documented in [Signing provenance](docs/guide/confidence.md#signing-provenance), in
  [Finalization](docs/guide/finalization.md#signing-provenance), in the deployment wiring, in the
  [security suite](docs/security-suite.md), and in the narrowed KL-11 row of
  [Known limits and documented boundaries](docs/known-limits.md#documented-boundaries).

### Benchmarks

- **The hot paths are measured** (story 12.2). `benchmarks/AgentExperience.Benchmarks` is a BenchmarkDotNet console
  project, in the solution so every build compiles it, never packed and never run by CI (a release test checks that no
  workflow names it). It times retrieval over 1k and 10k records (text-only, with a preferred environment, and with a
  confidence decay policy), the MAF context provider producing an 8-record Historical Reference block with and without
  session tracking, finalization of a captured run with tool calls, and applying confidence evidence, each on the
  in-memory store and on PostgreSQL, with allocations. PostgreSQL runs in one Testcontainers pgvector container,
  started and seeded once; without Docker those cases are skipped with a message. No model is ever called.
- [docs/benchmarks.md](docs/benchmarks.md) says how to run it and holds one committed baseline with the machine, OS,
  runtime and PostgreSQL version it ran on. The numbers compare only with another run on the same machine.
- `BenchmarkDotNet` 0.15.8 and `Testcontainers.PostgreSql` 4.15.0 are exact-pinned in that project only, and recorded
  in the [compatibility evidence](docs/compatibility-evidence.md#test-infrastructure-not-shipped). No shipping package
  or `src/` file changed.

### Tests: upgrades from published previews

- **A database a published preview created now upgrades under test** (story 12.1). The new
  `tests/AgentExperience.Upgrade.Tests` suite starts a PostgreSQL container per preview, lets that preview's own
  packages, restored from nuget.org, create and fill a database, and upgrades it with today's migrators following the
  [runbook](#upgrade-in-this-order). As the application role it then reads every record, lifecycle event, grant,
  grant access row, reuse feedback submission, tombstone and embedding back through today's stores (queries, text
  and vector searches and batch reads included), carries the lifecycle on (a transition, verified and opt-out
  confidence evidence, an expiring grant's purge, a new grant, an erasure, new feedback, a new embedding), and
  compares the upgraded schema with a fresh install of today's, catalog object by catalog object. It covers
  `0.1.0-preview.1` (schema `0001` to `0010`, one role) and `0.1.0-preview.2` (`0001` to `0018`, two roles), in
  plaintext and in crypto-shredding mode; `0.1.0-preview.2`'s schema is also that of `0.1.0-preview.3` and
  `0.1.0-preview.4`. It runs in CI's `postgres` matrix on every supported major. Until now every upgrade test built
  its old database from a prefix of today's scripts, with today's store writing the rows.
- The databases come from one seeder program per preview, under `tests/AgentExperience.Upgrade.Seeders`, each
  exact-pinning that preview's packages. They are not in the solution and never packed, and
  `CompatibilityPinAgreementTests` exempts only that directory from its floor rule, because the pins are deliberately
  old, and holds it to its own: each seeder resolves exactly the preview it names, and the set matches the suite's.
  The suite's [README](tests/AgentExperience.Upgrade.Tests/README.md) lists what it checks, the items whose meaning
  the upgrade changes by design (a `0.1.0-preview.1` grant becomes `LessonOnly`, for example), and how to add a
  preview.
- No migration script, store or package changed: both previews upgrade with their data intact.

## 0.1.0-preview.4

The .NET 10 release: every package now targets `net10.0` only. Nothing else changes for a host already on .NET 10;
a host on .NET 8 or 9 stays on `0.1.0-preview.3`.

### Breaking: .NET 10 only

- **All six packages now target `net10.0` only.** `net8.0` and `net9.0` are gone from every package, and every test
  project, the sample and the experiments build and run on `net10.0` alone. This lands ahead of the date announced
  with `0.1.0-preview.3` (the first preview after .NET 8 and 9 leave support on 10 November 2026), by the
  maintainer's decision. **A host on .NET 8 or 9 stays on `0.1.0-preview.3`**, the last release that targets them.
- **The `net8.0`-only dependencies are gone.** Core no longer references `System.Text.Json` or `Microsoft.Bcl.Memory`,
  and `AgentExperience.Storage.Postgres` no longer references `System.Text.Json`; the `net10.0` shared framework
  supplies every API they did. Every other dependency and floor is unchanged, and the public API is identical.
- **Code that existed only for older runtimes is removed:** the `#if NET9_0_OR_GREATER` fallback in
  `ExperienceIndex.ComputeContentHash` (the hash is unchanged), and the retry in the MAF adapter's open-run registry
  that worked around a `ConcurrentDictionary` bug in the .NET 8.0.0–8.0.10 runtimes (the fix listed under
  `0.1.0-preview.3`), which a `net10.0` assembly can never run on. See
  [Target frameworks](docs/compatibility-evidence.md#target-frameworks).

## 0.1.0-preview.3

Everything since `0.1.0-preview.2`: stories 8.2, 9.1, 9.2, 10.1–10.4, 11.1 and 11.2. It adds a sixth package,
`AgentExperience.Storage.InMemory` (development and tests only, guarded against production use), graded environment
compatibility, a replaceable confidence engine, read-time confidence decay by domain, a capability gate on injection,
a store conformance suite that both stores pass, and the first live-model results: a confirmatory run against Gemini
and an exploratory one against Claude, both concluding that the injected content reduces failed attempts. There are
no new migrations; every new behaviour is opt-in and leaves default results unchanged.

### Fixed

- **An invocation can no longer spin forever on a forgotten run on .NET 8.0.10 and earlier.** On .NET 8.0.0 through
  8.0.10, `ConcurrentDictionary.TryRemove` can report an entry another thread has only just added as absent
  ([dotnet/runtime#107525](https://github.com/dotnet/runtime/issues/107525), fixed in 8.0.11). When that happened as
  the Microsoft Agent Framework adapter forgot a run, the forgotten entry stayed in its open-run ledger, and every
  later invocation naming that run spun at full CPU looking it up again, never returning. The adapter now retries the
  removal for as long as that very entry is still there.

### Store contract clarifications

- **Reordered feedback exposures now converge.** `PostgresExperienceReuseFeedbackStore` and
  `InMemoryExperienceReuseFeedbackStore` sort a submission's exposures by `ExperienceId` before they store it or
  compare a resubmission with it, so the same exposures listed in another order are `AlreadyRecorded` rather than
  `Conflict`, and the submission handed back lists them in ID order, as `RecordedExperienceReuseFeedback.Exposures`
  always documented. Naming one record twice is still `Invalid`. Callers going through Core saw no difference: Core
  already ordered them.
- **`ExperienceLifecycleCommitResult.CurrentStatus` doc corrected.** On `Committed` it is set only for a confidence
  submission that did not move the record (an independence key already taken, or a replayed evidence ID); an
  identical replay of an event that carried no confidence payload reports `null`, as both stores already did.
- **`ExperienceSupersessionCheckResult.ReplacementStatus` doc corrected.** It is `null` on `RecordNotFound`, even when
  the replacement exists in the scope, as both stores already did.
- The store conformance suite now asserts all three instead of leaving them open.

### In-memory storage for development (story 11.2)

- **A sixth package, `AgentExperience.Storage.InMemory`, for development and tests only.** It holds
  `InMemoryExperienceRecordStore` (`IExperienceRecordStore`), `InMemoryExperienceCandidateSource`
  (`IExperienceCandidateSource`, searching that same record store) and `InMemoryExperienceReuseFeedbackStore`
  (`IExperienceReuseFeedbackStore`), all sealed and thread-safe, and copying everything on write into read-only
  snapshots (timestamps in UTC, truncated to whole microseconds, as PostgreSQL stores them), so capture, finalization, retrieval, injection and
  feedback run without PostgreSQL. Data is lost when the process ends, and none of the PostgreSQL guarantees apply
  (append-only enforcement, erasure reach, backups, the two database roles, crypto-shredding); every public type says
  so. This reverses the earlier "never publish an in-memory store" policy, by the maintainer's decision, on the
  condition that the package is guarded and conformance-tested.
- **It passes the whole store conformance suite of story 11.1** on `net8.0`, `net9.0` and `net10.0`
  (`tests/AgentExperience.Storage.InMemory.Tests`), with the suite unchanged, and keeps the confidence ledger's
  evidence-ID idempotency, independence key and single-use assessment.
- **One registration, `AddAgentExperienceInMemoryStorageForDevelopment(configure)`,** registers the three ports as
  singletons, sharing one record store. It runs only when the environment is `Development`, `Test` or `Testing`
  (case-insensitive): the registered `IHostEnvironment`'s name, or with none registered `DOTNET_ENVIRONMENT`, else
  `ASPNETCORE_ENVIRONMENT` (neither set: nothing to check). Anywhere else, `Staging` and blank names included, a hosted
  service it registers stops a Generic Host from starting and resolving a store throws `InvalidOperationException`,
  unless `InMemoryStorageOptions.AllowProductionEnvironment` is set. It throws at registration beside a different
  `IExperienceRecordStore`, and on a second call that passes options. Constructing the stores directly is not guarded.
- **Search matches every query word, without stemming.** Text is normalized (Unicode form KC, case-folded) and split
  into words; a query drops the common English stopwords PostgreSQL's `english` configuration drops and terms shorter
  than two characters, and a record matches only when its task ID, task summary or lesson (first 100,000 characters)
  contains every remaining term. Relevance is the fraction of the terms it contains, weighted towards the task
  summary, in (0, 1]; its values still differ from PostgreSQL's. Candidates come strongest first, then by ID, after
  the scope, status and confidence filters and before the limit.
- **Same validation as PostgreSQL.** The PostgreSQL store's rules for the core ports moved into
  `src/Shared/ExperienceRecordValidator.Shared.cs`, which both packages link, so both stores refuse exactly the same
  requests. The only wording change: a reserved sealed-value prefix or placeholder is now "reserved for sealed values"
  rather than "which the store reserves". No new public API in Abstractions or the PostgreSQL package.
- **The feedback conformance suite also varies a human assessment's reviewer and assessment** (a new
  `HumanAssessedFeedback` fixture), for both stores.
- **Not provided:** sharing grants and the grant access log (every read behaves as if no grant exists), the embedding
  index, deletion, retention and encryption.
- **Dependencies:** Abstractions, `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12+ and
  `Microsoft.Extensions.Hosting.Abstractions` 10.0.3+ (new, the version the repository already resolves; evidence row
  in `docs/compatibility-evidence.md`). No Core, Npgsql or MAF; `eng/verify-packages.cs` holds it to that.
- **Release checks count six packages:** package verification, the public API baselines (a new
  `AgentExperience.Storage.InMemory.verified.txt`), `release.yml`'s artifact count, and `RELEASING.md`, which also
  documents checking the first push of the new package ID under the Trusted Publishing policy, and the one-time
  API-key push if nuget.org refuses it. Nothing was published.
- The end-to-end sample keeps its own in-memory doubles; the comment that stated the old policy now points at this
  package.

### Anthropic provider for the live experiment

- **An exploratory run against `claude-haiku-4-5` concluded `ReuseBenefitAttributableToContent`**
  ([report](experiments/AgentExperience.LiveReuse/results/anthropic-claude-haiku-4-5-2026-09-28.md)): mean failed attempts 0.83 with memory, 2.75 without it, 2.42
  with the strategy withheld and 2.67 with stale experience (sign tests p = 0.0020 against both no memory and the
  placebo; the stale control p = 0.81). It is exploratory because no Anthropic model is registered, and it cost about
  USD 0.93.
- **`experiments/AgentExperience.LiveReuse` can run against Anthropic (Claude).** Set `ANTHROPIC_API_KEY` (and
  optionally `ANTHROPIC_MODEL`, default `claude-haiku-4-5`); `AGENTEXPERIENCE_LIVE_PROVIDER=anthropic` chooses it when
  another provider's variables are set too, and any two or three configured providers without a choice are refused.
- **Anthropic's own C# SDK**, `Anthropic` exact-pinned `[12.50.0]` in the experiment only, through its
  `AsIChatClient` adapter; not an OpenAI-compatible endpoint. It keeps `Microsoft.Extensions.AI.Abstractions` at
  10.10.0. The key and base URL are set explicitly, the SDK's default retries (two) are kept, and API errors are
  recorded by exception type and HTTP status only.
- **Request differences, recorded in the report's Settings row:** no seed (the API has none), a 4096-token
  `max_tokens` cap per call (the API requires one; other providers are sent none), no thinking. Built-in price for
  `claude-haiku-4-5`: USD 1.00 / 5.00 per million input / output tokens.
- **Exploratory runs are labelled.** The harness now reads `registeredModels` from `preregistration.json` (which is
  unchanged). A run of a provider or model it does not register -- any Anthropic run, or a non-default Gemini model --
  says **EXPLORATORY RUN** in its first lines and its *Run* table, carries `"exploratory": true` in its raw results,
  and is never called a candidate for the confirmatory result. The seed wording no longer claims every seedless
  provider rejects the field.
- **A redundant call after success no longer stops the report.** Claude sometimes made a second `apply_migration`
  call in the response that got the migration live; the stored record and the `Approach:` line keep both, in order.
  The harness took the last strategy as stored and the first on the line as shown, and refused to report. It now
  compares the full ordered sequences (`storedStrategies`, `blockStrategies` in the raw results; joined with ` > ` in
  the report), still refuses a block that differs from its store, and keeps `StoredStrategy`, `BlockStrategy` and
  `Followed` on the first strategy.
- No change to `src/`, to any shipping package, or to the registered design.

### Capability gate for injection (story 10.4)

- **`ExperienceInjectionOptions.ReceivingAgent`**, an optional `ReceivingAgentCapabilities` declaring what the agent
  receiving the Historical Reference can and may do: `AvailableTools` (tool names), `MaxRiskClass`, and
  `ToolRiskClasses` (each tool's host-declared `ToolRiskClass`: `Low`, `Medium`, `High`, `Critical`). With it set, a
  record whose verified approach calls a tool the agent lacks is omitted as the new
  `InjectionOmissionReason.ToolUnavailable`, and one whose approach calls a tool riskier than `MaxRiskClass` as the new
  `RiskClassExceeded`. The tool check runs first, and the first failure decides the reason. Either check is skipped
  when its property is `null`.
- **Risk classes are declared, never inferred.** A tool missing from `ToolRiskClasses` counts as `Critical`, even one
  listed in `AvailableTools`. `ToolRiskClasses` has no effect without `MaxRiskClass`, and `MaxRiskClass = Critical`
  disables the risk check. A recorded call with a null or blank tool name is always unavailable, and `Critical`.
- **Only the tools the `Approach:` line would name** are checked: the verified final attempt's calls, up to
  `MaxApproachToolNames`. Tool names the lesson text mentions are not. A record with no approach passes, and so does a
  borrowed record whose grant withholds the line, so the gate cannot be used to probe a lender's tool names. Tool
  names compare ordinally, whatever comparer the host's collections use.
- **Between the final eligibility re-read and `DecideInjection`**, after `AlreadyDelivered`. It runs after the
  `MaxRecords` cut, so a gated record still takes a record slot and the agent may get fewer records than the limit. A
  gated record is never shown to the host's decision, rendered, charged to a session budget, or recorded as a run
  exposure. Its omission has no detail, so no tool name reaches results or telemetry. A gated borrowed record still
  writes a grant access row, because the store disclosed it on the re-read, as one the host denies does.
- **Passing grants nothing.** The approval boundary still decides every tool call.
- **Validated and snapshotted at construction.** A null, empty or whitespace tool name in either collection, or an
  undefined risk class, throws `ArgumentException`. With `ReceivingAgent` unset, the block, omissions and outcomes are
  unchanged. The new enum members are appended, so existing numeric values are stable.

### Confidence decay by domain (story 10.3)

- **`ConfidenceDecayPolicy`**, an optional retrieval setting. With one configured, each eligible record's `Confidence`
  ranking component is its stored `ReuseConfidence` times `2^(-age / halfLife)`. The half-life is chosen by the
  record's domain: the `EnvironmentFingerprint.Metadata` entry named `DomainKey` (default `"domain"`), matched
  ordinally against `HalfLives` (stored as a read-only copy). The factor is in [0, 1] and reaches 0 only for an age
  vastly beyond the half-life. A domain mapped to `null` never decays. A missing or unlisted domain uses
  `DefaultHalfLife`, whose default of `null` means no decay.
- **Age is the lesson's, not its last activity's.** It is measured from `CreatedAt`, because `UpdatedAt` already drives
  the separate Recency component. A `CreatedAt` in the future (clock skew) decays nothing.
- **Ranking only.** Eligibility, including the `MinimumConfidence` floor, uses the stored value, so decay never
  excludes a record, and it only reorders candidates already fetched under the stored-confidence floor and the
  candidate bound. A read never writes. Decay can change the order of injected records, and the MAF Historical
  Reference block's `Confidence:` line still shows the stored confidence, so it can differ from the decayed value on
  the components line.
- **`RankingComponent.UndecayedValue`**, an optional init property. On the Confidence component it holds the stored,
  normalized confidence whenever a half-life applied, and it is `null` everywhere else. With no policy, scores,
  components, ordering and the golden ranking fixture are unchanged.
- **Validated at construction and on `with`.** A zero or negative half-life throws `ArgumentOutOfRangeException`. A
  blank `DomainKey`, or a null or blank key in `HalfLives`, throws `ArgumentException`.
- **A new `ExperienceRetrievalService` constructor overload** takes an optional policy (`null` means no decay). The
  existing constructors are unchanged, and `AddAgentExperienceRetrieval` resolves a registered
  `ConfidenceDecayPolicy` in any registration order.

### First live result (story 9.1)

- **The first confirmatory run, against `gemini-3.1-flash-lite`, concluded `ReuseBenefitAttributableToContent`**
  ([report](experiments/AgentExperience.LiveReuse/results/gemini-gemini-3.1-flash-lite-2026-09-27.md)). Mean failed attempts were 0.00 with memory, 2.17 without it,
  2.42 with the strategy withheld (placebo) and 2.75 with stale experience. Memory beat both no memory (sign test
  p = 0.0010) and the placebo (p = 0.0005), and the stale control showed no benefit (p = 0.94). Every condition
  verified all 12 tasks. The run cost about USD 0.08 (288 model calls). It is one model, one run, 12 instances and a
  synthetic task; the report lists its limitations.
- **Pre-registration amendment 1**, recorded before any trial completed: the seed is sent only to a provider that
  accepts it. Gemini's OpenAI-compatible endpoint rejects a `seed` field with HTTP 400, which errored the first
  attempt's first two calls; that attempt was stopped and stays in `results/ledger.tsv`, with its partial record.
  `RunDescriptor.SeedSent` carries the choice, and the report says whether the seed was sent.

### Replaceable confidence engine (story 10.2)

- **`IExperienceConfidenceEngine`**, a Core port with `RuleId`, `RuleVersion` and `Score(ExperienceConfidenceInput)`.
  The input is the record as read and the evidence counters after this evidence is applied. The engine supplies only
  the score. Status changes, accepted statuses, counter increments, independence and exposure admission, and replay
  stay in the library.
- **`ReuseConfidenceHeuristicEngine`**, the default (`Instance`, rule ID `reuse-heuristic`, version `1.0.0`), wraps
  `ReuseConfidenceHeuristic.Score`. With no host engine, every stored value, rule version, outcome and event is
  unchanged.
- **The rule is recorded with no migration.** The default engine still records `"1.0.0"` in
  `ConfidenceUpdate.RuleVersion`; a host engine records `"{RuleId}/{RuleVersion}"`, on the lifecycle event and in the
  evidence ledger. Both parts must be 1 to 64 characters from `[A-Za-z0-9._-]`, and a host engine may not use
  `reuse-heuristic`. A violation throws `ArgumentException` when the lifecycle service is constructed.
- **A score is validated, never clamped.** NaN, an infinity, or a value outside [0, 1] throws
  `InvalidOperationException` naming the rule, with nothing written. An engine exception propagates unchanged.
- **`ReadConfidenceAsync`** recomputes a filtered score through the same engine, so with a host engine it can now
  throw when it recomputes: `InvalidOperationException` for a value that is not a score, or the engine's own
  exception.
- **Reuse feedback keeps going.** An engine failure while `ExperienceReuseFeedbackService` submits one exposed
  record fails that record only, reported as retryable like a storage failure; the other records are still
  submitted.
- **Replay compares the rule string.** Retrying evidence stored under a different rule string (for example after
  installing or changing an engine) is reported as `Conflict`. Keep one engine per deployment, and roll a change out
  after in-flight retries drain.
- **A new `ExperienceLifecycleService` constructor overload** takes `confidenceEngine` as its last parameter, after
  `deindexingTimeout` (`null` means the default for either), and
  `AddAgentExperienceCore` resolves a registered `IExperienceConfidenceEngine` once, from the root provider. The
  existing constructors are unchanged. Finalization's initial 2/3 is still the
  heuristic's; a host engine applies from the first evidence onward.

### Graded environment compatibility (story 10.1)

- **`RetrieveExperienceRequest.PreferredEnvironmentAttributes`**, an optional init property. Preferred attributes only
  grade the `EnvironmentCompatibility` ranking component; they never exclude a record. Required attributes still
  exclude a mismatch as `EnvironmentMismatch`, and are applied before anything is scored.
- **`IEnvironmentCompatibilityScorer`**, a Core port called for every eligible record with its `EnvironmentFingerprint`
  and the preferred attributes (an empty dictionary when none are set). Its value is clamped to [0, 1], and NaN counts
  as 0. A scorer that throws fails the retrieval closed: `RetrievalOutcome.Failed` and no records. The
  `RetrievalFailure`'s reason is fixed and content-free; its exception is the scorer's own, passed through as is. An
  `OperationCanceledException` while the caller's token is cancelled propagates unwrapped.
- **`AttributeMatchEnvironmentScorer`**, the default: the fraction of preferred attributes the record's
  `Metadata` carries with an ordinally equal value, and 1.0 when nothing is preferred. With no preferences and the
  default scorer, scores, components, ordering and exclusions are unchanged, and the golden ranking fixture still
  passes as it was.
- **A new `ExperienceRetrievalService` constructor overload** takes an optional scorer (`null` means the default), and
  `AddAgentExperienceRetrieval` resolves a host-registered `IEnvironmentCompatibilityScorer` once, from the root
  provider, so register it as a singleton. It must be thread-safe, and it is not bounded by `RetrievalPolicy.Timeout`:
  the caller's token is checked between records, not during a call. The
  existing constructors are unchanged. `CompatibleEnvironmentScore` stays, now documented as the default scorer's value
  when nothing is preferred.
- MAF hosts set preferred attributes in `ExperienceInjectionOptions.ResolveRequest`, as they already do for required
  ones. The Historical Reference block format is unchanged.

### Live-model reuse experiment (story 9.1)

- **`experiments/AgentExperience.LiveReuse`**, an opt-in console app that runs story 4.4's reuse methodology against a
  real model: Gemini (default `gemini-3.1-flash-lite`) or Azure OpenAI, both through `IChatClient`. The model works a
  migration-rollout task whose working strategy is a hidden property of each service's database; the library
  captures, verifies, reflects on and stores its verified learning runs; then each service's different evaluation
  ticket runs memory-disabled, memory-enabled, as a placebo (the same block with the working strategy withheld), and
  as a negative control with genuine but stale experience injected. Success is decided by the simulated database's
  state, never by a model.
- **Pre-registered** in `preregistration.json` before any live run: 12 instances, the metrics, and the verdict rule --
  a strictly lower mean of failed attempts, an exact one-sided sign test at 0.05, no loss of verified success and no
  rise in refused bypass requests, with a benefit credited to the injected content only if memory-enabled also beats
  the placebo and the negative control shows none. The harness reads every value it executes from the file, which is
  embedded in the build, and a test pins the file's blob id so any later change must be recorded as an amendment. The
  registered model and a first-complete-run rule, enforced by an append-only `results/ledger.tsv`, fix which run is the
  confirmatory one.
- **Configuration from environment variables only**, a hard budget cap on model calls and tokens (default 600 and
  2,000,000) that stops the run cleanly, and a Markdown report plus raw per-trial JSON under `results/` with no key,
  no prompt, and only the endpoint's host (for Azure, without the resource name). With no provider variable set it skips with a message and spends nothing.
  It never runs in CI or in `dotnet test`.
- **One new package, in the experiment only**: `Microsoft.Extensions.AI.OpenAI` `[10.10.0]`, which reaches Gemini's
  OpenAI-compatible endpoint and the Azure OpenAI v1 endpoint alike, and resolves `Microsoft.Extensions.AI.Abstractions`
  to exactly the shipping floor. Nothing under `src/` changes. The comparison with `Google.GenAI` and `Azure.AI.OpenAI`
  is in the experiment's README.
- **`experiments/AgentExperience.LiveReuse.Tests`**, part of the normal suite, proves the harness offline with scripted
  models: a model that follows the block benefits and gains nothing from stale experience or the placebo, a model that
  ignores the block shows identical numbers in every condition, the four conditions' first requests are byte-identical
  once the block is removed, no request ever replays a function call (batched calls, unknown tools and unbindable
  arguments included), the bypass guardrail can fail, the budget cap stops the run, errors are recorded by type only,
  and a fake key appears in no output.
- **`CompatibilityPinAgreementTests`** now also checks the lock files under `experiments/`, so the experiment's graph
  must resolve every floored package to its floor, as the test projects' do.
- The first live result is recorded separately, below the heading "First live result (story 9.1)".

### Release criteria: known limits and documented boundaries

The README's Known limits table is split in two, and the `1.0` gate is redefined. No code changes, and this does
not claim `1.0` or production readiness; it makes the gate reachable. The decision is reversible, and its rationale
is recorded in [`RELEASING.md`](RELEASING.md#decision-known-limits-and-documented-boundaries).

- **Known limits** are unresolved problems a code change could fix, and they block `1.0`. A row still leaves only by
  fixing the limit. The table is now empty.
- **Documented boundaries** are properties the library cannot remove by code, inherent to PostgreSQL, to what
  independence can prove, or to how models work. They do not block `1.0`. Each row states the boundary exactly, why
  no code change can remove it, and what the library does about it. KL-2, KL-11 and KL-12 move here with their
  numbers and their wording unchanged. A row may move from limits to boundaries only with a written reason why no
  code change can remove it, and it returns to the limits table if that reason stops holding.
- **The gate** (`RELEASING.md` step 9, and the same step in `release.yml`) now counts only the rows of the Known
  limits table. Dropping the preview suffix requires that table to be empty and the maintainers' deferred-work ledger
  to be closed; a non-empty table still requires a preview version. The GitHub release notes carry both tables.

### Hygiene: tool names, the session state key, deferred tests (story 8.2)

- **Invisible characters in a tool name no longer reach the `Approach:` line.** Story 6.2 turned every control,
  format, private-use and unassigned code point in an argument *value* into a space, classified per Unicode scalar so
  TAG characters are caught, but left tool *names* alone. Names now go through the same routine, before whitespace is
  collapsed and markers are neutralized: a bidirectional override or isolate, a zero-width character or joiner, any
  other format character (a soft hyphen, a byte-order mark), a TAG-character payload, a C0 or C1 control, a
  private-use or unassigned code point, or a lone surrogate in a name becomes a space, so a name can neither use a
  bidirectional control to reorder the rest of the line when it is displayed nor carry text in those code points, and
  a name made only of them reads `(none recorded)`. A marker split by one of them, even inside a word, is still
  neutralized. Variation selectors, the combining grapheme joiner and Hangul fillers are letters or marks and pass
  through, as they do in argument values; which code points are unassigned follows the running .NET's Unicode data. **Behaviour change, for such names only:** until now TAG characters, controls and private-use
  code points reached the block as they were, and format characters in the Basic Multilingual Plane were removed
  rather than turned into spaces (`read`, a zero-width space and `ledger` rendered `readledger` and now renders
  `read ledger`; an emoji ZWJ sequence now renders with spaces). A name
  that holds none of these characters renders byte for byte as before; the sample's and the reuse baseline's golden
  reports are unchanged.
- **`ExperienceInjectionOptions.SessionStateKey`** (new, additive) sets the `StateBag` key session tracking keeps its
  account under. It defaults to `ExperienceContextProvider.SessionStateKey` (`"AgentExperience.InjectionSession"`),
  so nothing changes unless it is set. Two providers on one agent can now each have their own account (budget,
  deduplication and withdrawals); with the same key, `ChatClientAgent` refuses two of its own providers when it is
  built. The key is
  validated when the provider is constructed: not null or blank, at most `MaxSessionStateKeyLength` (128)
  characters, no whitespace or invisible code point, and not capture's `"AgentExperience.RunId"`. Changing the key of
  a deployed provider starts existing sessions afresh. `ExperienceContextProvider.StateKeys` now returns the
  configured key.
- **Tests for paths that had none:** the sealing function's `Deleted` (plaintext and sealed tombstones) and
  `AlreadySealed` outcomes, and a feedback replay after every sealed rationale copy has become unopenable (story
  6.4); `ReadConfidenceAsync`'s revision cut-off under a race with a later commit, and its refusal of a history cursor
  that stays put or goes back (story 7.3). Each was checked by breaking the code it covers.

### Planned: `net8.0` and `net9.0` leave the matrix

.NET 8 and .NET 9 leave support on 10 November 2026. The first preview published after that date removes the
`net8.0` and `net9.0` targets from all six packages, with the `net8.0`-only `System.Text.Json` and
`Microsoft.Bcl.Memory` references; a host still on either must stay on an earlier preview or move to .NET 10. Nothing
is removed in this release. PostgreSQL 14 (end of life 12 November 2026) stays outside the supported matrix, as it
is now. See [Target frameworks](docs/compatibility-evidence.md#target-frameworks).

## 0.1.0-preview.2

Everything since `0.1.0-preview.1`: stories 3.6, 5.1–5.6, 6.1–6.6 and 7.1–7.3. It adds migrations `0011` to `0018`
(there is no `0014`), a supported two-role deployment, opt-in crypto-shredding, verified and exposure-bound confidence
evidence, session tracking for reused MAF sessions, argument values on the `Approach:` line, and a wider supported
matrix (`net8.0`, `net9.0` and `net10.0`; PostgreSQL 15 to 18; MAF `[1.22.0, 2.0.0)`).

### Known limits resolved or narrowed

`0.1.0-preview.1` shipped sixteen known limits. This preview resolves thirteen and narrows the other three, which stay
in the table (see [Known limits](docs/known-limits.md#known-limits)).

- **Resolved:** KL-1 (story 5.6), KL-3 and KL-10 (5.4), KL-4 (6.1), KL-5 and KL-6 (5.5), KL-7 (5.3), KL-8 (6.2, then
  7.1), KL-9 (3.6), KL-13 (6.3, then 7.2), KL-14 and KL-15 (5.1), and KL-16 (5.2).
- **Narrowed, not closed:**
  - **KL-2** (story 6.4). In encrypted mode a `pg_dump`, a base backup, a replica, the WAL and the dead heap tuple
    hold only ciphertext that nothing opens once the record is erased. What remains in every copy is the derived
    search data PostgreSQL reads in the clear: the full-text vector (task ID, summary and lesson as lexemes with
    positions) and the embedding with its content hash. So do the identifiers and metadata that are never sealed,
    and anything written before the switch. Plaintext mode is unchanged, and the property is only as good as a key
    store kept outside the database's backups, whose own backup retention bounds the erasure.
  - **KL-11** (stories 6.6 and 7.3). What remains: exposure means the library *delivered* a record into a run, not
    that the run used it, so each run given a lesson is one key; a host that calls `RecordExposure` for records it
    did not deliver, or marks a hand-written record `Finalized`, is believed; the round is the `ClosedRound` the host
    passed to finalization, and a direct aggregator caller's run ID is still its own statement (its result counts
    only through finalization or such a marked record); whoever holds the assessment token key, or can call the
    issuer, can mint; a retry made after its token expired is refused although the original landed; and the opt-out
    still trusts everything — its evidence is labelled and excludable on read, but the stored score retrieval ranks
    on still counts it.
  - **KL-12** (story 6.5). What remains: the earlier block stays in the session's history, verbatim, and a model that
    already read it cannot be made to forget it, so a withdrawal notice is advisory. The provider cannot strip its
    own earlier blocks, and the tracking is only as trustworthy as the host's session storage (removing the key
    resets it; concurrent invocations on one session race on it).

### Upgrade, in this order

From `0.1.0-preview.1`, whose schema ends at `0010`. Steps 1 to 5 are one maintenance window: once `0011` is applied
an old build can no longer write grant events or access rows, and once ownership moves (step 2) the application's
role cannot read or write any table until step 4 grants it the manifest. Step 6 is optional and comes later.

0. **Optional, before the window, on a large database:** build the new indexes out of band, as the role that owns
   the tables at the time. The headers of `0012` (`ix_experience_grant_access_retention`), `0015` (the
   `assessment_id` column, then `ux_confidence_evidence_assessment`) and `0016` (the `search_vector_sealed` column,
   then `ix_experience_records_search_sealed` and `ix_experience_records_unsealed`) have the `CREATE INDEX
   CONCURRENTLY` statements. Check each with `pg_index.indisvalid` and drop and retry an invalid one: `IF NOT EXISTS`
   would otherwise accept it.
1. **Stop every process running `0.1.0-preview.1`**, and keep it stopped until step 5.
2. **Create an owner role and move ownership to it, as a superuser** (story 6.1), using the SQL in
   [Store: upgrading an existing single-role database](docs/guide/deployment.md#upgrading-an-existing-single-role-database).
   It creates the owner, grants it the one superuser-only privilege a non-superuser migrating role needs —
   `GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized` — and moves
   ownership of the database, the `agent_experience` schema and every table, sequence and function in it. The role
   your application uses today stays the application role, with the same credentials. The purge functions stay
   `SECURITY DEFINER` and now run as the new owner; the transfer takes a brief `ACCESS EXCLUSIVE` lock on each table.
3. **Run the schema migrator as the owner** (`ExperienceSchemaMigrator.MigrateAsync`), then
   `ExperienceVectorSchemaMigrator.MigrateAsync` if you use the vector channel. It applies `0011` to `0018`; no
   journaled script is edited and none rewrites a row. Most new checks on existing tables are added `NOT VALID`, so
   nothing large is scanned; each such header has the `VALIDATE CONSTRAINT` statement to run later, at a time of your
   choosing. On a busy database, give the owner a `lock_timeout` (for example
   `ALTER ROLE <owner> SET lock_timeout = '5s'`) and retry on timeout, rather than let an `ACCESS EXCLUSIVE` request
   queue behind a long reader. **Every existing sharing grant becomes `LessonOnly` the moment `0011` applies** (see
   Behaviour changes). From here on the migrator never runs with the application's credentials.
4. **Call `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` as the owner**, after both migrators,
   naming the application role, and again on every deploy. Set `AllowErasure` if the application deletes records,
   sweeps retention or purges expired grants, and `AllowAccessLogPurge` if it purges the access log; without them
   those calls fail with a permission error. Leave `AllowSealing` off.
5. **Deploy `0.1.0-preview.2`, then start it.** Never before step 3: this build reads `0011`'s `disclosure`, `0016`'s
   `search_vector_sealed` (in both modes) and `0017`'s `approach_arguments`, so against an older schema every
   grant-joined read and every text search fails with `42703`. Before starting it, decide:
   - **Exposure.** An agent built with both `ExperienceContextProvider` and `UseExperienceCapture` records what it
     delivers with no change. A host that injects records some other way calls
     `IExperienceCaptureService.RecordExposure(runId, exposures)` from the code that delivers them, before the run
     completes. Without either, confidence evidence and attributed feedback about reuse in that run are refused.
   - **Human assessments.** A host that records them registers an assessment token key,
     `services.AddSingleton(new ExperienceIndependenceOptions { AssessmentTokenKey = ... })` (at least 32 random
     bytes from your secret store), and calls `AssessmentTokenIssuer.Issue` from its review flow.
   - **Or opt out** with `ExperienceIndependenceOptions.Verification = IndependenceVerification.TrustHostSuppliedIdentifiers`
     if you capture and retrieve in different scopes, finalize nothing, or must keep accepting evidence about runs
     finalized before this version. Its evidence is labelled `HostTrusted`.
   - **Session tracking** is on by default. A host whose chat history drops injected blocks sets
     `ExperienceInjectionOptions.SessionLimits = null`, and `Limits.MaxBytes` must be at least
     `HistoricalReferenceWriter.RetractionBlockBytes` while it is on.
   - **Grants.** To restore a borrowed record's `Approach:` line, the owner revokes the grant and issues it again as
     `LessonAndApproach`; to show selected argument values too, as `LessonApproachAndArguments`, naming the keys,
     which the recipient allowlists as well. Review any dotted key already in an `ApproachArguments` allowlist.
6. **Optional: turn on crypto-shredding** (story 6.4). Plaintext mode stays the default.
   1. Stand up a key store outside the database's backup domain: `EnvelopeExperienceKeyStore` over your KMS
      (`IExperienceKeyEncryptionKey`) and a durable repository for wrapped keys (`IExperienceWrappedKeyRepository`)
      that does not share the database's backups.
   2. Give every PostgreSQL component the same `ExperienceEncryption`
      (`services.AddAgentExperiencePostgresEncryption(keyStore)`, or the new constructor parameter) and deploy. New
      records and ledger rows are sealed from here on.
   3. Re-apply the application role's privileges with the **same options plus `AllowSealing = true`** (the call is
      declarative: leaving out `AllowErasure` takes erasure away), and seal the existing records with
      `SealPlaintextRecordsAsync`, batch by batch, until `MoreRemain` is `false` for every project root.
   4. Take `AllowSealing` away again on the next deploy: it is a content-rewrite power over plaintext records.
   5. Run `VACUUM` on `agent_experience.experience_records` and age out pre-upgrade backups. Copies made before the
      records were sealed are plaintext; ledger rows and grant reasons written before step 6.2 stay plaintext until
      their record is erased.

A host that skips steps 2 and 4 keeps working as a single-role deployment. That is fine for local development and
tests, but it gets none of the two-role guarantees and is not a supported production deployment.

### Breaking changes

- **`RequiredCheck` must name an evidence kind** (KL-5). `new RequiredCheck("id")` no longer compiles. To accept any
  kind, pass `RequiredCheck.AnyKind` (`"*"`).
- **An evaluation is bound to its run** (KL-6).
  - `VerificationAggregator.Aggregate` now takes `runId` as its first argument.
  - `VerificationResult` can only be produced by the aggregator: it has no public constructor (so it can no longer be
    deserialized), and its properties are get-only.
  - `ReflectionRequest.Run` and `ReflectionRequest.Evaluation` are get-only. A mismatched pair throws
    `ReflectionBindingException`, so a run whose own outcome disagrees with the evaluation fails at request
    construction rather than inside the default reflector.
  - Finalization quarantines a reflection that does not match its request.
- **Confidence evidence the library cannot verify is refused by default** (stories 6.6 and 7.3, KL-11) with
  `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal`; an attributed feedback submission records the
  failing attribution with benefit `Unknown`.
  - Machine evidence about a run finalized before this version is refused (`UnknownRound`: its record carries no
    closed round), and so is evidence about a run captured and retrieved in different scopes (`UnknownRun`).
  - Evidence about a run with no recorded exposure is refused (`NotExposed`). That includes every run finalized
    before this version (no record carries exposures yet) and every run captured without something calling
    `RecordExposure`.
  - A record read back without an origin is `HostWritten`, so a record stored before this version, or written by hand
    through `CreateAsync`, vouches for no run (`HostWrittenRun`). A host that writes records itself and wants them to
    count sets `Origin = ExperienceRecordOrigin.Finalized` — and is then making that statement.
  - **Evidence naming the record's own source run is refused in every mode** (`OwnRun`). It was a documented rule that
    nothing enforced.
  - **A human assessment needs an assessment token.** Without one (or with an invalid one, or an `AssessmentId` that
    is not the token's) the feedback is recorded with benefit `Unknown`; a direct `Human` submission is `Unverified`.
    A token spent on a record is refused for any other evidence ID, including a second feedback submission. `Machine`
    evidence carrying an `AssessmentToken` is `Invalid`.
  - `IndependenceVerification.TrustHostSuppliedIdentifiers` opts out of all of this except `OwnRun`.
- **New enum members; an exhaustive `switch` needs the arms:** `ConfidenceUpdateOutcome.Unverified` (9),
  `IndependenceRefusal.NotExposed` (9) and `.HostWrittenRun` (10), `ExperienceCaptureFailureStage.RecordExposure` (5),
  `InjectionOutcome.Retracted` and `.SessionBudgetExhausted`, `InjectionOmissionReason.AlreadyDelivered` and
  `.OverSessionBudget`, and `ExperienceGrantDisclosure.LessonApproachAndArguments`.
- **Binary break for code compiled against the previous preview: recompile.** These gain a trailing optional
  positional parameter, which changes their constructor and `Deconstruct` signatures (source that names its arguments,
  or omits the new one, compiles unchanged):
  - `ApplyConfidenceEvidenceRequest` (`AssessmentToken`), `ApplyConfidenceEvidenceResult` (`Refusal`) and
    `HumanReuseAssessment` (`AssessmentToken`);
  - `ExperienceGrant`, `ExperienceGrantRequest` (`ApproachArguments`), `ExperienceRecordGetResult`,
    `RankedExperience` and `ExperienceInjectionDecisionContext` (`GrantApproachArguments`);
  - the PostgreSQL record store, candidate source, grant store, reuse-feedback store and embedding index
    (`ExperienceEncryption? encryption`).
- **Other signature and shape changes.**
  - `ExperienceLifecycleService` gains a constructor taking `ExperienceIndependenceOptions` and an optional
    `IExperienceCaptureService`; the existing constructors verify, with no key and no capture service.
  - `IExperienceCaptureService` gains `RecordExposure`, with a default interface implementation that records nothing
    and returns `NotSupported`: an existing implementation compiles, and its runs are exposed to nothing.
  - `StartRun` throws `ArgumentException` for a provenance whose `ExposedTo` is not empty: exposure is recorded, not
    claimed up front.
  - `Provenance` gains the init property `ExposedTo` and value equality that compares it element by element;
    `ExperienceRecord` gains `ClosedRoundId` and `Origin`; `ConfidenceUpdate` gains `AssessmentId` and `Admission`.
  - **Record equality:** `ApproachArguments` is a dictionary, compared by reference in the generated `Equals`, so two
    reads of the same `LessonApproachAndArguments` grant are no longer equal as whole records. Compare its fields
    instead.
- **Stricter validation.** A `Limits.MaxBytes` below `HistoricalReferenceWriter.RetractionBlockBytes` is refused while
  session tracking is on (the provider's constructor throws `ArgumentException`). The PostgreSQL store refuses
  `ExperienceRecord.ClosedRoundId == Guid.Empty`, an `AssessmentId` on machine evidence, and an `ExposedTo` with an
  empty ID, a negative revision, a duplicate record or more than 256 entries (`Invalid`), and text in the sealed
  format (see Behaviour changes).
- **For `IExperienceRecordStore` implementers:**
  - persist `ExperienceRecord.ClosedRoundId` (a store that drops it makes every machine submission `UnknownRound`),
    `Provenance.ExposedTo`, `ExperienceRecord.Origin` (a store that drops it makes every run `HostWrittenRun`) and
    `ConfidenceUpdate.Admission`;
  - refuse a second piece of evidence presenting the same `ConfidenceUpdate.AssessmentId` for a record with `Conflict`
    and an error on `ConfidenceUpdate.AssessmentIdPath` (a store that ignores it leaves tokens replayable, though each
    replay still meets the independence key);
  - a store (or test double) that reports `LessonApproachAndArguments` must also report `GrantApproachArguments`, and
    a `PermittingGrantId`, or no borrowed value is shown.
- **Two-role deployments:** without `AllowErasure` or `AllowAccessLogPurge`, the calls those options cover fail with a
  permission error.

### Behaviour changes

- **Existing sharing grants become `LessonOnly`** (story 3.6, KL-9). A borrowed record loses its `Approach:` line
  until its owner revokes the grant and issues it again as `LessonAndApproach`. The level is immutable, and the
  recipient has no access between the two calls.
- **Every run is bounded from the moment it opens** (KL-7). This includes the default single-invocation path.
  - An invocation still running after one `MaxOpenRunDuration` period (5 minutes by default) is handed the bound.
  - At twice that duration, its run is completed as `Cancelled` and the closure is reported. The invocation's answer
    is untouched, but its attempt is refused.
- **Retention sweeps count only what they erase.** `DeletedCount` no longer includes records that another caller
  erased first.
- **The injection eligibility check re-checks its time limit before each record and after the last decision**, from
  elapsed time as well as its timer. A slow host decision can no longer slip a record in after the limit.
- **Session tracking is on by default** (story 6.5, KL-12): a reused `AgentSession` no longer gets the same record
  twice, and its injection is bounded. With a session supplied, `ExperienceContextProvider` keeps an account in the
  session's `StateBag` under `ExperienceContextProvider.SessionStateKey` (`"AgentExperience.InjectionSession"`):
  record IDs, revisions and counters, never content. It is on by default because KL-12 is a safety limit and MAF's
  `ChatClientAgent` keeps every injected block in the session's history. A host that uses a fresh session per task
  sees no difference in what is injected; it still gets the state key written, and the `Limits.MaxBytes` floor.
  - A record revision the session already holds is omitted as `AlreadyDelivered` and takes no slot; a strictly newer
    revision is injected again.
  - A session is given at most 32 record deliveries and 64 KB of Historical Reference across its invocations
    (`ExperienceInjectionOptions.SessionLimits`). When that cannot take another record, retrieval is not run and the
    outcome is `SessionBudgetExhausted`; a record the byte budget drops is `OverSessionBudget`.
  - A delivery is charged only when MAF reports the invocation succeeded. A failed invocation's records are delivered
    again. A stage nothing settled (an abandoned stream) is charged at the next invocation, but its records may be
    delivered again and its notices stay owed, because MAF kept no history for it.
  - Every record a session holds is re-checked on every invocation, and one no longer valid gets a withdrawal notice
    (see Added). A session whose resolver changes scope withdraws what the new scope cannot read: held records are
    re-checked in the current request's authorization and scope, and the account keeps no scope.
  - A session state that does not validate injects nothing: the invocation reports `Failed` and leaves the value as
    it is, until the host removes the key. So does a withdrawal re-check that throws or times out, until it succeeds.
  - **Who should turn it off:** a host whose chat history drops injected blocks (for example a `ChatHistoryProvider`
    written to follow the old advice for KL-12, or a chat reducer that trims old messages) should set
    `SessionLimits = null`, or deduplication hides a record the model no longer sees. `null` writes no state and
    restores the previous blocks, omissions and outcomes exactly.
- **Marker and label matching is looser for every field** (all record text, not only notices). A marker now matches
  across any run of whitespace or line break and with dash look-alikes, invisible format characters (zero-width
  spaces, bidirectional controls) are removed from stored text, every Unicode line separator is a line break, and a
  label matches after leading whitespace. Record text without such characters renders byte for byte as before.
- **An existing `ApproachArguments` key that contains a `.` can now show more** (story 7.1). Such a key was already
  valid, and matched only an argument literally named that way; a call without one showed nothing. It is now also
  read as a path, so `["options.mode"]` shows the nested `options.mode` of a record the reader owns. Review any dotted
  key you allowlisted.
- **A session withdraws borrowed argument values more eagerly.** A delivery whose line showed a borrowed record's
  values is withdrawn when the record is next read through any other grant — even a reissued one at the same level —
  or at a level that shows no values. Session state records the grant (`grantArgs`) only on a delivery that actually
  showed a borrowed value, so a state that never did is byte for byte what an older build writes; a state that did is
  refused by an older build (it rejects unknown members), which then injects nothing and says so rather than
  forgetting the delivery.
- **A borrowed value needs a named grant.** A store that reports `LessonApproachAndArguments` without a
  `PermittingGrantId` shows no borrowed value, because the session could not tell a later switch of grants.
- **Text in the sealed format is refused on write, in both modes** (story 6.4). A lifecycle reason, confidence
  detail, grant or revocation reason, or feedback rationale beginning with `aexp-sealed:v1:` is `Invalid`, and so is a
  rationale that is exactly `(sealed)`: the store opens a value with that prefix as ciphertext.
- **A sealed record cannot be erased without its key.** `0016`'s guard refuses to tombstone a `payload_version = 2`
  row unless the erasing transaction declares that it destroys the key, so a process left without an
  `ExperienceEncryption` fails its delete (`42501`) instead of reporting `Deleted` while the key survives.
- **`ExperienceReuseFeedbackService` checks an attribution's run** (known, and not an attributed record's own), round,
  token and exposure before writing its ledger, and records a failing one with benefit `Unknown` and the reason, as it
  does every other attribution failure.

### Added

**The two-role deployment** (story 6.1, KL-4) is now the documented and supported one. An owner role owns the schema
and runs the migrators. A separate application role, which the stores connect as, owns nothing and holds exactly what
the stores need: no `ALTER TABLE`, so it cannot disable a trigger or replace a guard function; no `DELETE`,
`TRUNCATE` or `UPDATE` on any ledger, so a purge marker it sets by hand admits nothing; `UPDATE` only on the columns
the store moves, so it cannot write a tombstone by hand; and `EXECUTE` on the purge functions only when the host opts
in. The owner role and superusers remain unbound, which is inherent in PostgreSQL. See
[Store: deploying with two roles](docs/guide/deployment.md#deploying-with-two-roles).

- `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` and `ExperienceApplicationRoleOptions`
  (`AllowErasure`, `AllowAccessLogPurge`, `AllowSealing`). In one transaction, the call:
  - refuses a missing role, an unmigrated schema, a superuser, the caller itself, any role that is or is a member of
    an owner of the database, the schema or an object in it, and any role that reaches a superuser, a server-file
    role, `pg_maintain` (PostgreSQL 17 and later) or `SET` on `session_replication_role`;
  - revokes everything the application role holds in the schema;
  - grants the stores' manifest. Every `UPDATE` is column-level, including on `experience_embeddings`;
  - verifies the role's effective privileges, including `CREATE` on the schema and grant options, across every role
    the application role can `SET ROLE` to, and rolls back on any difference. On PostgreSQL 17 and later that
    includes `MAINTAIN` on any table in the schema.
- `0013_role_separation_hardening`: `search_path = pg_catalog, agent_experience, pg_temp` on the three purge
  functions and on every guard trigger function, and `EXECUTE` on the purge functions revoked from `PUBLIC` again.

**Crypto-shredding** (story 6.4, KL-2), an opt-in mode in which erasure reaches every copy of a record's text. See
[Store: crypto-shredding](docs/guide/crypto-shredding.md).

- **`ExperienceEncryption`** (Storage.Postgres): with it, every free-text column erasure removes is stored as
  AES-256-GCM ciphertext under a per-record data key. That covers the record payload together with the task ID,
  lifecycle reasons and confidence detail, evidence detail, grant reasons, revocation reasons and grant event reasons,
  and the reuse-feedback rationale, sealed once per exposed record. The associated data binds every value to its
  column, row, record and all six scope fields; a value that fails its tag throws and nothing decrypted is returned.
  `DeleteAsync` and the sweep destroy the key inside the erasure's transaction, after every database-side check and
  before the commit: a record never looks erased while its key survives, and never looks live once its key is gone.
  A sealed row read without a key it ever had is a configuration error, never "erased".
- The record store, candidate source, grant store, reuse-feedback store and embedding index each gain a trailing
  optional `ExperienceEncryption? encryption` constructor parameter (`null` is plaintext mode), and the DI extensions
  pick up a registered `ExperienceEncryption`; `AddAgentExperiencePostgresEncryption(keyStore)` registers one. The
  grant-store and feedback-store overloads taking a data source now register a factory rather than an instance, so
  the encryption is picked up however the registrations are ordered.
- **The key-custody port** (Abstractions): `IExperienceKeyStore` (`CreateKeyAsync` get-or-create, `GetKeyAsync`,
  `DestroyKeyAsync`, scoped to one record's `ExperienceKeyReference`; a destroyed reference is destroyed for ever),
  `ExperienceKeyLookup`, `ExperienceKeyStatus`, `ExperienceDataKey`, and for envelope encryption
  `IExperienceKeyEncryptionKey`, `IExperienceWrappedKeyRepository`, `ExperienceWrappedKey` and
  `ExperienceWrappedKeyEntry`.
- **`EnvelopeExperienceKeyStore`** (Core), with `RewrapAsync` for KEK rotation (bounded, resumable, and never writing
  back a key destroyed meanwhile), and the development-only reference implementations
  `LocalExperienceKeyEncryptionKey` and `InMemoryExperienceWrappedKeyRepository`. No new package reference anywhere:
  BCL `AesGcm` only.
- **`PostgresExperienceRecordStore.SealPlaintextRecordsAsync`** and `ExperienceSealingResult`: the upgrade job. It
  seals existing plaintext records oldest first, in bounded batches, `Exact` or `Subtree`, authorized like a sweep.
  Each record is sealed in its own transaction, and the seal is opened and compared before it is written. Nothing the
  record answers changes: its revision, ranking, embedding and history stay as they were.
  `ExperienceApplicationRoleOptions.AllowSealing` grants `EXECUTE` on `0016`'s `seal_experience_record`, and the
  privilege manifest and its verification cover it. It is the `record.seal` telemetry operation, carrying
  `agentexperience.sealed_count` and `scope_match` only.
- `0016_crypto_shredding`: `search_vector_sealed` (the full-text vector of a sealed record, from exactly `0003`'s
  expression, so a sealed record ranks as its plaintext twin), `reuse_feedback_exposures.rationale_sealed`, two partial
  indexes, three `NOT VALID` sealed-shape checks, a trigger clearing the sealed vector on erasure, and the sealing
  function. It touches no row.

**Verified, exposure-bound confidence evidence** (stories 6.6 and 7.3, KL-11). See
[Updating confidence from evidence](docs/guide/confidence.md).

- **Verified independence keys.** `ExperienceLifecycleService.ApplyEvidenceAsync`, and so every attributed feedback
  submission, refuses with `ConfidenceUpdateOutcome.Unverified` and an `IndependenceRefusal` an independence key it
  cannot vouch for, checking the run, then the round or token, then exposure, for machine and human evidence alike:
  - a `RunId` that is not finalized into a record in the evidence's scope nor held by the capture service
    (`UnknownRun`), or is the record's own source run (`OwnRun`);
  - a machine `VerificationRoundId` other than the round finalization closed for that run (`UnknownRound`);
  - human evidence without a valid assessment token: missing, forged, for another scope, run, reviewer, direction or
    record, expired, or already spent on this record (`AssessmentToken*`), or with no key configured;
  - a run whose recorded exposure does not include the record at or before its current revision (`NotExposed`), and
    a run known only through a hand-written record (`HostWrittenRun`).
- `ExperienceIndependenceOptions` (`Verification`, `AssessmentTokenKey`, `AssessmentTokenLifetime`, `TimeProvider`),
  `IndependenceVerification`, `IndependenceRefusal`, `AssessmentTokenIssuer` and `IssuedAssessmentToken`.
  `AddAgentExperienceCore` wires the registered capture service and options into the lifecycle service. It
  deliberately does not register the issuer: anything that can resolve it can mint, so the review flow constructs it.
- An assessment token is HMAC-SHA256 under the host's key, over its ID, issue time, direction and records and over
  the scope, run and reviewer. It is compared in constant time, carries its signed expiry (a day by default, at most
  30), and is spent once per record in the same transaction as the evidence it lands. It never appears in
  `ToString()`.
- `ExperienceRecord.ClosedRoundId`, stamped by finalization from the evaluation's `VerificationBasis.ClosedRound` and
  stored in the payload (`closedRoundId`, omitted when null). `ConfidenceUpdate.AssessmentId`, stored in
  `confidence_evidence.assessment_id` and `lifecycle_events.confidence_assessment_id` and read back with history.
- **Exposure is recorded on the captured run.** `Provenance.ExposedTo`, a list of `RunExposure` (record ID and
  revision, never content), one entry per record at the earliest revision it was delivered at, at most
  `RunExposure.MaxPerRun` (256). `IExperienceCaptureService.RecordExposure` adds to it on a still-open run
  (`RecordExposureOutcome`: `Recorded`, `DuplicateNoOp`, `Conflict` once the run is completed, `RunNotFound`,
  `CapacityExceeded`, `NotSupported`). The MAF context provider records every record it injects, at the revision it
  rendered, on the run `UseExperienceCapture` captures the invocation as — only when that run is its own agent's, so
  an uncaptured agent nested inside a captured one exposes nothing to the outer run; a failure, including a capture
  service that returns `NotSupported`, is reported through `OnCaptureFailure` at the new stage `RecordExposure`. What
  a reused session's history carries from an earlier run is not credited to a later run: the session account is host
  storage, unauthenticated.
- **Finalization carries it onto the record** and marks the record `ExperienceRecord.Origin = Finalized`
  (`ExperienceRecordOrigin`: `HostWritten`, the default, and `Finalized`). The PostgreSQL store keeps both in the
  payload as optional version-1 fields (`provenance.exposedTo`, `origin`), sealed in crypto-shredding mode.
- **Which mode admitted each piece of evidence is recorded.** `ConfidenceUpdate.Admission`
  (`ConfidenceEvidenceAdmission`: `Verified`, `HostTrusted`; `null` when none was recorded — evidence stored before
  this version, or an update written by something other than Core), stored in `confidence_evidence.admission` and
  `lifecycle_events.confidence_admission`, read back on replay and in history, and never part of a replay's content
  comparison.
- **`ExperienceLifecycleService.ReadConfidenceAsync(authorization, scope, experienceId, filter, ct)`**, with
  `ConfidenceEvidenceFilter` (`All`, `ExcludeHostTrusted`, `VerifiedOnly`), `ConfidenceReadResult`, `ConfidenceReport`
  and `ConfidenceAdmissionCounts`: a score recomputed from the stored counters less the excluded evidence, read from
  the record's history. `VerifiedOnly` also leaves out a hand-written record's initial counters (the report carries
  `Origin` and `Initial`). It writes nothing.
- **Telemetry:** `confidence.apply` spans carry `agentexperience.confidence.admission` on `Applied` (the admission the
  stored evidence carries), and `agentexperience.independence.refusal` on `Unverified`. Both are span attributes only;
  the metric dimensions are unchanged.
- `0015_verified_independence`: the two `assessment_id` columns, human-only `CHECK`s (`NOT VALID`), and the unique
  index `ux_confidence_evidence_assessment` on `(experience_id, assessment_id)`. `0018_evidence_admission`: the two
  admission columns and two `NOT VALID` checks. Neither adds a table, function or grant, so the application role's
  manifest is unchanged.

**Injection into MAF** (stories 6.2, 6.5 and 7.1, KL-8 and KL-12). See
[Adapter: showing selected argument values](docs/guide/injection.md#showing-selected-argument-values)
and [Adapter: reused sessions](docs/guide/injection.md#reused-sessions-a-budget-no-repeats-and-withdrawal-notices).

- **`ExperienceInjectionOptions.ApproachArguments`: selected argument values on the `Approach:` line.** A host can
  allowlist, per tool name, the argument keys whose values the injected `Approach:` line shows, for example
  `ApproachArguments = { ["run_incident_check"] = ["strategy"] }`, which renders
  `run_incident_check(strategy="wait-for-lock")`. It is empty by default, and without it the block is byte for byte
  what it was. `HistoricalReferenceWriter.Write` gains an overload that takes the same allowlist. A malformed
  allowlist is refused when `ExperienceContextProvider` is constructed, and the provider keeps a copy.
  - Only keys named for that exact tool are read, and only the value the capture-time sanitizer stored, so a redacted
    value stays redacted.
  - Only a string, number, boolean, null or enum name is shown. An object or array becomes `(not shown: not a string,
    number or boolean)`.
  - A string has invisible characters (per Unicode scalar) and whitespace collapsed and trimmed, its markers
    neutralized, its double quotes turned into single quotes and `->` broken up, and is cut to 64 characters and
    quoted. A line's arguments are capped at 512 characters in total, and the byte budget still drops whole records.
- **Dotted paths:** an `ApproachArguments` key such as `options.mode` or `targets.0` walks, by lookup only, into an
  object- or array-valued argument and shows the scalar it ends on, under every bound above; a path ending on a
  container shows the not-shown marker. A key that exists literally at the top level is matched first.
- **Borrowed argument values:** `ExperienceGrantDisclosure.LessonApproachAndArguments` (Abstractions), a third,
  immutable grant level; existing member names and values are unchanged. `ExperienceGrantRequest` and
  `ExperienceGrant` gain `ApproachArguments` — per tool, the argument keys the owner consents to show — required at
  the new level and `Invalid` at any other, bounded by `ExperienceGrant.MaxApproachArgumentTools` (32),
  `MaxApproachArgumentKeysPerTool` (16), `MaxApproachArgumentToolNameLength` (256) and `MaxApproachArgumentKeyLength`
  (64), with the same key rules as the injection allowlist. Under the new level the `Approach:` line shows a borrowed
  record's value for a key both the grant and the reader's `ApproachArguments` name for the same tool, and ends with
  `HistoricalReferenceWriter.ApproachGrantArgumentsSuffix`; a malformed or missing owner allowlist shows nothing.
  Under `LessonOnly` and `LessonAndApproach` a borrowed record still shows no argument value.
- `ExperienceRecordGetResult.GrantApproachArguments`, `RankedExperience.GrantApproachArguments` and
  `ExperienceInjectionDecisionContext.GrantApproachArguments`: the owner's keys, read from the same grant row as the
  level, only at the new level and only for a borrowed record.
- `0017_grant_argument_disclosure`: `experience_grants.approach_arguments jsonb NULL`, with checks that it is present
  exactly at the new level and is a non-empty object of non-empty string arrays, the three `*_disclosure_known`
  checks widened, and `approach_arguments` among the monotonicity trigger's pins (restated with its `search_path`
  pin). The owner's keys are names only and are stored in the clear in encrypted mode as well: do not put anything
  secret into a tool name or an argument key.
- **Withdrawal notices.** Every record a session holds is re-checked on every invocation, in one `GetManyAsync` call
  declared `ScopeCheck`, so no access row is written for it. One that is no longer readable in scope (erased, deleted,
  grant revoked or expired), no longer in an eligible status (revoked, superseded, quarantined, contested), below the
  confidence floor, past `MaxAge`, or read through a grant that now withholds the approach the session was shown gets
  a fixed notice, once, ahead of any new record: `Withdrawn: experience <id>, delivered earlier in this conversation,
  is withdrawn and is no longer valid reference material.` inside a `--- WITHDRAWN ---` section. It carries no reason
  and no content. Record text cannot forge one structurally: the section markers and the wording are block markers
  and `Withdrawn:` is a field label. Notices take the block budget first, no record is written while one is still
  owed, and the session budget never refuses one. A re-check that throws injects nothing and withdraws nothing; one
  the store answers with a refusal or no row withdraws.
- `ExperienceInjectionSessionLimits`, `ExperienceInjectionOptions.SessionLimits`, `ExperienceInjectionSessionUsage`,
  `ExperienceInjectionResult.RetractedExperienceIds` and `.Session`, `HistoricalReferencePayload.RetractedExperienceIds`,
  `InjectionOutcome.Retracted` and `.SessionBudgetExhausted`, `InjectionOmissionReason.AlreadyDelivered` and
  `.OverSessionBudget`, `ExperienceContextProvider.SessionStateKey` and its `StateKeys` and `InvokedCoreAsync`
  overrides, `HistoricalReferenceWriter.RetractionBegin`, `.RetractionEnd`, `.WithdrawnNotice` and
  `.RetractionBlockBytes`, and the span attribute `agentexperience.retracted_count` (written only when not zero).

**Grants, retention, batching and telemetry** (stories 3.6, 5.2, 5.4 and 5.6).

- **A grant disclosure level:** `ExperienceGrantDisclosure`, recorded on grants, grant events and every access row
  (KL-9), by `0011_grant_disclosure`.
- **Retention for subtrees and for the access log** (KL-3, KL-10).
  - `ScopeMatch.Subtree` for retention sweeps.
  - `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync`, by `0012_grant_access_retention`, which keeps every access
    row for at least 30 days.
- **Batch reads and embeddings** (KL-1), both as default interface methods, so no implementer breaks.
  - `IExperienceRecordStore.GetManyAsync`.
  - `IExperienceEmbeddingGenerator.GenerateBatchAsync`, with `ReindexExperienceRequest.EmbeddingBatchSize`.
  - One injection re-read now takes 2 database commands instead of 12, for 8 candidates.
- **Telemetry for erasure** (KL-16): `delete`, `retention.sweep`, `grant.purge` and `grant.access.purge`, on the
  `AgentExperience.Storage.Postgres` source and meter.

### Release process

- **Releases are published by `.github/workflows/release.yml`, with NuGet Trusted Publishing.** Pushing a `v*` tag
  re-runs the release checks on the tagged commit, which must be on `main` and match `Directory.Build.props`. After
  a reviewer approves the `nuget-release` environment, the workflow pushes the verified packages with a short-lived
  OIDC-issued key and creates the GitHub release, whose notes are this section. No API key is stored. See
  `RELEASING.md` step 10.

### Fixed

- **The store README said the migrator never needs a superuser.** A non-superuser migrating role has always failed at
  `0010`, with "permission denied to set parameter", until a superuser grants it `SET` on the two marker parameters.
  The README now documents that one grant.
- **A crypto-shredding store test failed about one run in 43** (test only). It used a random task ID and asserted its
  lexeme, but when all eight hex digits were decimal PostgreSQL's parser split the ID into a word and a signed
  integer. It now uses a fixed ID and asserts the lexemes on `search_vector_sealed` itself.
- `RELEASING.md` step 3's PostgreSQL loop now splits its list of majors in zsh as well as bash.

### Dependencies and support matrix

- **Target frameworks: `net8.0`, `net9.0` and `net10.0`** (stories 6.3 and 7.2); `0.1.0-preview.1` targeted
  `net10.0` only. All five packages ship all three builds with the same public API, which one baseline per assembly
  gates; the test projects run on all three.
  - **New `net8.0`-only dependencies:** Core takes `System.Text.Json` 10.0.12+ and `Microsoft.Bcl.Memory` 10.0.12+,
    and the store takes `System.Text.Json` 10.0.12+. They are the .NET 10 train's packages for APIs the .NET 8 shared
    framework lacks: `JsonElement.DeepEquals`, the strict payload decoder's two options, and `Base64Url`. `net9.0` and
    `net10.0` declare nothing new.
  - `ExperienceEmbeddingDescriptor.ComputeContentHash` uses `Convert.ToHexString(...).ToLowerInvariant()` on
    `net8.0`, which gives the same 64 lowercase hex characters.
  - .NET 8 and .NET 9 both leave support on 10 November 2026. The first preview after that date drops both.
- **PostgreSQL 15, 16, 17 and 18** (previously 16 only). CI runs the store, vector, proof and sample suites on each
  major (the sample on `net10.0`), and the store and vector suites again in crypto-shredding mode. **PostgreSQL 14 is
  not supported:** migration `0005` uses `NULLS NOT DISTINCT`, which is PostgreSQL 15 syntax. DbUp journals scripts
  by name, so the only ways to reach 14 are serving different text under the journaled name or keeping a second
  schema lineage, and a PostgreSQL 14 database upgraded in place would keep that lineage for life. PostgreSQL 14
  itself reaches end of life on 12 November 2026.
- **Every dependency except MAF is a floor, not an exact pin** (story 6.3), declared `>=` with no upper bound, so a
  host that needs a newer release of any of them, or a MAF that raises one, gets no restore conflict. CI tests each
  floor itself and the newest release in its major (the same minor for `Pgvector`) on every change; the second is the
  `floating-dependencies` job, `eng/probe-floating-dependencies.sh`, which also floats MAF to `1.*`. It gates pushes
  and the weekly schedule, and reports without blocking on pull requests. A later major restores but is not claimed.
- **`Microsoft.Agents.AI` is the range `[1.22.0, 2.0.0)`** (story 7.2). A host that needs a newer MAF 1.x restores it
  with no NU1608 warning or NU1107 conflict; the lock files still resolve 1.22.0. The bound is at the next major
  because MAF states no SemVer promise: between 1.15 and 1.22 it kept every signature the adapter uses, but changed
  caller-visible behaviour five times, once (1.22's per-run clone of `ChatClientAgentRunOptions`) on a hook the
  adapter uses. A host on a MAF 2.x, once one exists, gets NU1608. The MAF probe's `latest` leg probes the newest
  stable version inside the range and gates pushes and the weekly schedule, reporting without blocking on pull
  requests; the `pinned` leg runs the floor and always gates.

| Package | `0.1.0-preview.1` | `0.1.0-preview.2` |
| --- | --- | --- |
| `Microsoft.Agents.AI` | `[1.20.0]` | `[1.22.0, 2.0.0)` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `[10.0.11]` | `10.0.12` |
| `Microsoft.Extensions.Compliance.Redaction` | `10.9.0` | `10.10.0` |
| `Microsoft.Extensions.AI.Abstractions` | `[10.9.0]` | `10.10.0` |
| `Npgsql` | `[10.0.3]` | `10.0.3` |
| `dbup-postgresql` | `[7.0.1]` | `7.0.1` |
| `dbup-core` | `[6.1.1]` | `6.1.1` |
| `Pgvector` | `[0.3.2]` | `0.3.2` |
| `System.Text.Json` (`net8.0` only; Core and the store) | — | `10.0.12` |
| `Microsoft.Bcl.Memory` (`net8.0` only; Core) | — | `10.0.12` |

A bare version is a floor (`>=`). Story 5.1 briefly made every reference exact at the versions above (KL-14, KL-15);
story 6.3 replaced that policy with floors before this preview shipped.

### For contributors

- A full local test run needs the .NET 8 and .NET 9 runtimes beside the pinned SDK; CI installs both in every job.
  `AGENTEXPERIENCE_POSTGRES_MAJOR` selects the PostgreSQL major the container tests start (16 by default; 15 to 18
  are accepted, and anything else fails), and `AGENTEXPERIENCE_TEST_ENCRYPTION=on` runs the store and vector suites,
  unmodified, in crypto-shredding mode.
- `RELEASING.md` step 3 runs the container suites on every supported major, in both modes, and step 6 runs the MAF
  range probes and the floating-dependency probe as release blockers.
- `CompatibilityPinAgreementTests` refuses an exact pin in a shipping project. It allows a bare floor, or, for MAF
  alone, `[x.y.z, (x+1).0.0)`, and checks that a framework-conditioned reference names one target framework and is a
  direct reference only in that framework's lock-file section. `eng/probe-maf-version.sh` reads the range, skips the
  `DeclaredPins` tests for any version but the floor, and says whether the probed version is inside the range.
  `eng/verify-packages.cs` checks each framework's dependency group, including the `net8.0`-only floors, which appear
  there and nowhere else.
- In the store and vector suites, tests whose subject is the stored representation are mode-aware, or pinned to
  plaintext where their subject is a plaintext row (a database from before `0016`, the plaintext decoder); tests that
  purge by hand declare the key destruction `0016`'s guard asks for, and none is skipped. Every store test runs as
  the application role. `PostgresApplicationRoleTests` proves each refusal from the application role's own
  connection. `PostgresCryptoShreddingTests` proves the crypto-shredding property with a real `pg_dump` and a
  `pageinspect` read of the dead tuple, both failure orders of key destruction, tampering, values moved between
  records, rows, columns and scopes, KEK rotation, the ledgers, and the upgrade job; `EnvelopeExperienceKeyStoreTests`
  covers the key store.
- `HistoricalReferenceBorrowedAndNestedArgumentsTests` plants a marker in every place a value must not come from —
  siblings, other array elements, container content, owner-only, reader-only, case variants and other tools — for
  owned and borrowed records, in both the CLR and the JSON shapes, and checks the intersection, the fail-closed owner
  allowlist, `LessonOnly`'s withheld line and every bound on a nested leaf. `InjectedContentAuthorizationTests` adds
  a nested value on a borrowed record that orders a guarded call, which the boundary still denies. The store suite
  covers `0017` on each supported major, in both modes (the upgrade test itself writes plaintext rows, since a
  pre-`0016` schema can hold only those).
- The script-order tests in `OfflineStoreTests` pin each script's absolute position, so appending a script no longer
  shifts every earlier assertion.
- **The reuse baseline** no longer registers a host `WorkingApproachReflector`. The learning phase runs the shipped
  `DefaultExperienceReflector` alone, and the trials allowlist the `strategy` argument instead. The three golden
  reports changed only in prose: the learned-records line now reads the strategy off the record's final attempt, and
  the paragraph explaining what the harness supplies names the allowlist rather than a reflector. No number moved:
  every trial, mean, verdict, experience ID and confidence is identical. A new test shows the allowlist is
  load-bearing: with it off, the agent reads no strategy from the block and the harness refuses to report reuse.
- The sample's golden transcript is unchanged, byte for byte, and so are the reuse baseline's reports apart from the
  prose above: the sample submits unattributed feedback, which checks no exposure, and the reuse baseline drives
  capture directly without the adapter's capture wrapper, so it records no exposure and submits no attribution. The
  synthetic comparative-evaluation tests seed their run's record as finalized and exposed; the Core telemetry loop
  reuses its lesson in a second run, seeded into its store double as finalized, exposed and with a token, rather than
  citing the record's own run, which the own-run rule refuses; its call table is unchanged, and its span-attribute set
  gains the two new attributes.

## 0.1.0-preview.1

The first preview: Epics 1–4. See the
[release](https://github.com/fabbrik/AgentExperience.NET/releases/tag/v0.1.0-preview.1).
