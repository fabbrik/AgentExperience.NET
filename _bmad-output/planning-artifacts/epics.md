---
stepsCompleted: [1, 2, 3, 4]
status: ready-for-dev
validationResult: pass
validationReport: epics-validation.md
updated: 2026-09-07
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-agenticexperience.net-2026-09-06/prd.md
  - _bmad-output/planning-artifacts/architecture/architecture-agenticexperience.net-2026-09-06/ARCHITECTURE-SPINE.md
  - _bmad-output/planning-artifacts/architecture/architecture-agenticexperience.net-2026-09-06/solution-design.md
  - docs/AgentExperience_NET_MAF_Production_Architecture.md
---

# agenticexperience.net - Epic Breakdown

## Overview

This document provides the epic and story breakdown for AgentExperience.NET, based on the PRD and architecture decisions.

Advanced Elicitation revisions accepted on 2026-09-07: missing capture/sanitization work, early policy enforcement, evaluator conflict rules, concurrency controls, and comparative measurement are included below. Existing story IDs are preserved; execution order is explicit rather than numeric where new prerequisites were inserted.

Findings-resolution pass accepted on 2026-09-07: closed all six findings from the prior CONCERNS report — explicit MVP denial of promotion plus host risk-policy gating for FR9 (Stories 2.5, 2.3), narrowed Story 2.1 to record persistence only, made Story 1.7's proof tracks explicitly independent, added per-story traceability and dependency tags, defined the evidence-independence key for Story 3.4, and bound the previously open numeric defaults. Final re-validation returned **pass**; see epics-validation.md. Ready for Sprint Planning.

### Delivery order and shared acceptance requirements

- Epic 1: 1.1 → 1.7 → 1.4 → 1.2 → 1.5 → 1.3 → 1.6.
- Epic 2: 2.1 → 2.4 → 2.5 → 2.2 → 2.6 → 2.3. Minimum storage and retrieval policy is implemented here, without depending on Epic 3.
- Epic 3: 3.1 → 3.2 → 3.4 → 3.3.
- Epic 4: 4.1 → 4.2 → 4.4 → 4.5 → 4.3.

Every implementation story includes automated happy-path and failure tests, safe correlated diagnostics, and documented public behavior. Tests and diagnostics are delivered with their owning behavior, not postponed to Epic 4. Schemas and entities are introduced only when their story needs them.

All stories follow [reuse-boundaries.md](../specs/spec-agentexperience-net/reuse-boundaries.md). Story 1.7 verifies upstream integration before adapter implementation. The backlog specifies experience behavior, not permission to recreate MAF runtime, generic memory infrastructure, model providers, or observability platforms.

Accepted planning defaults: required TenantId/ApplicationId/ProjectId match exactly and case-sensitively; optional TeamId/AgentId/UserId also match exactly, including null-to-null. Null never means wildcard. Explicit sharing grants introduced in Story 3.1 may relax optional-field equality within the same tenant/application/project; they never permit cross-tenant access. Missing required scope is denied.

Default operational values (fixtures for Stories 2.2 and 2.3, adjustable via configuration): ranking weights relevance 0.35, confidence 0.25, recency 0.15, status 0.15, environment compatibility 0.10 (sum 1.0); default eligibility confidence threshold 0.5 — set below the initial validated confidence of 2/3 (≈0.667) so first reuse is not blocked; default retrieval timeout 500 ms; default injection limits 8 records / 16 KB payload.

Candidate records are not eligible for injection. Core can initially validate a candidate only when task verification passes and sanitization/storage policy permits it. Only Validated or Reinforced records passing confidence, scope, expiry, and environment checks are eligible. Epic 3 adds management transitions without being a prerequisite for these checks.

Critique reconciliation: verification selects a host-closed final round for the current artifact revision; earlier failures remain historical evidence. Completion score and reuse confidence are distinct. Host authorization establishes the caller's permitted scope; request scope only selects within it. Story 2.5 owns finalization orchestration; Story 2.6 owns embedding ingestion. Story 4.5 defines bounded deletion of library-owned data. These accepted rules supersede conflicting recommendations in the original architecture proposal.

## Requirements Inventory

### Functional Requirements

- FR1: Correlate each MAF invocation as an Experience Run before execution.
- FR2: Capture observable attempts, tool calls, results, errors, duration, and final state for successful and failed invocations.
- FR3: Sanitize task context, tool arguments, tool results, and evidence before storage or injection.
- FR4: Compose deterministic evaluators for tool exit codes, test results, workflow completion, human approval, and human correction.
- FR5: Create auditable structured Reflections from run data and evaluation.
- FR6: Save, retrieve, query, append lifecycle events, and update Experience Records in PostgreSQL/pgvector.
- FR7: Rank candidates using hybrid relevance, confidence, lifecycle state, recency, scope, and environment compatibility.
- FR8: Inject ranked candidates into MAF as labeled Historical Reference content.
- FR9: Enforce storage, retrieval, and promotion policy against scope, risk, sanitization, and lifecycle state.
- FR10: Track reinforcement, contradiction, supersession, staleness, quarantine, revocation, and reuse feedback.
- FR11: Emit correlated OpenTelemetry traces and metrics for the learning loop.

### NonFunctional Requirements

- NFR1 Security: enforce tenant/project boundaries at policy and query layers.
- NFR2 Privacy: redact or reject secrets and sensitive fields before persistence or context injection.
- NFR3 Reliability: failed-run capture must not depend solely on success callbacks.
- NFR4 Reliability: persistence and retrieval failures must be observable and must not make retrieved context authoritative.
- NFR5 Performance: retrieval uses a bounded timeout and safe empty-result behavior when unavailable.
- NFR6 Compatibility: isolate MAF compatibility in its adapter package; target .NET 10 while keeping core abstractions compatible with .NET 8+ where feasible.
- NFR7 Observability: correlate runs, experiences, retrieval, policy decisions, and reuse without logging private chain-of-thought.
- NFR8 Maintainability: core packages cannot depend on MAF, EF Core, PostgreSQL, model providers, or telemetry implementations.

### Additional Requirements

- Use hexagonal architecture with event-oriented lifecycle state.
- Keep `AgentExperience.Abstractions` and `AgentExperience.Core` portable and adapter-independent.
- Core owns evaluation-to-lifecycle transitions; adapters persist commands/events.
- Require explicit Scope for all store and retrieval operations; strict mode never infers global scope.
- Treat retrieved experience as untrusted historical reference; it cannot bypass MAF approvals or policies.
- Initial packages: Abstractions, Core, MicrosoftAgentFramework, Storage.Postgres, OpenTelemetry.
- Initial production store: PostgreSQL 16+ with pgvector 0.7+.
- MAF package version must be pinned by the first compatibility-tested adapter release.
- Provide a runnable MAF sample and automated unit/integration tests.
- License and publish as an independent Apache-2.0 open-source project.

### UX Design Requirements

Not applicable for the MVP: AgentExperience.NET is a developer library/API with a runnable sample, not a user-facing application.

### FR Coverage Map

- FR1: Epic 1 — correlate Experience Runs.
- FR2: Epic 1 — capture successful and failed observable attempts.
- FR3: Epic 1 — sanitize captured data.
- FR4: Epic 1 — evaluate deterministic evidence.
- FR5: Epic 1 — create structured reflections.
- FR6: Epic 2 — persist and query Experience Records.
- FR7: Epic 2 — retrieve and rank applicable candidates.
- FR8: Epic 2 — inject Historical Reference into MAF.
- FR9: Stories 1.4, 2.1–2.3 — enforce minimum policy at capture, storage, and injection; Story 2.5 — host risk-policy decision gates storage; Story 2.3 — host risk-policy decision gates injection; Story 3.1 — administer explicit sharing grants. The MVP has no procedural promotion engine: the only routes to Validated are Story 2.5's verified-evidence commit and Story 3.2's administrator-authorized transitions (see ARCHITECTURE-SPINE.md Deferred: procedural promotion).
- FR10: Epic 3 — manage lifecycle and reuse feedback.
- FR11: Each implementation story — safe diagnostics; Story 4.1 — integrated OpenTelemetry export and metrics.

## Epic List

### Epic 1: Capture and Explain Agent Experience
Developers can capture successful and failed runs, evaluate evidence, and produce structured reflections.

### Epic 2: Reuse Relevant Experience
Agents can persist experience and retrieve applicable Historical Reference during later MAF runs.

### Epic 3: Govern Experience Safely
Operators can control scope, lifecycle, trust, contradiction, revocation, and reuse feedback.

### Epic 4: Operate and Measure the Learning Loop
Developers and operators can observe, test, and validate the complete production learning loop.

## Epic 1: Capture and Explain Agent Experience

Agents and developers can capture successful and failed runs, evaluate observable evidence, and produce structured reflections.
**FRs covered:** FR1, FR2, FR3, FR4, FR5

### Story 1.1: Define the Experience Domain and Run Capture Contract

**Traces:** FR1, FR2, FR5, NFR8 · **AD:** AD-1, AD-2, AD-5 · **Depends on:** none (first story)

As a .NET agent developer,
I want portable contracts for Experience Runs, Attempts, Outcomes, Evidence, Reflections, Scope, and Environment,
So that agent integrations can capture experience without depending on MAF or a specific storage provider.

**Acceptance Criteria:**

**Given** the `AgentExperience.Abstractions` project is referenced
**When** a developer creates an Experience Run and appends observable Attempts
**Then** the contracts support task identity, ordered actions, tool metadata, results, errors, duration, scope, environment, and provenance.

**Given** an invocation succeeds or fails
**When** the developer completes the run snapshot
**Then** the model can represent both outcomes without requiring private chain-of-thought.

**Given** captured data includes sensitive fields
**When** the run is passed through the sanitization contract
**Then** the contract supports redaction or rejection before persistence or context injection.

**Given** lifecycle state changes
**When** a lifecycle event is appended
**Then** the event includes the Experience Record identifier, prior/current state, reason, producer, and timestamp.

**Given** the package is built
**When** it is inspected for dependencies
**Then** `AgentExperience.Abstractions` has no dependency on MAF, EF Core, PostgreSQL, model providers, or OpenTelemetry implementations.

**Given** a fresh checkout has no .NET solution
**When** the documented build/test commands run after this story
**Then** a minimal solution, Abstractions project, contract tests, and CI build exist with exact SDK/target-framework choices recorded; only projects needed for this story are scaffolded.

**Given** a caller submits scope, verification evidence, or a lifecycle command
**When** public contracts are inspected
**Then** they distinguish a host-established authorization context from request/record scope and carry verification round IDs, artifact revisions, event IDs, and expected record revisions without depending on an identity provider.

### Story 1.7: Prove Existing Library Integration Before Building Adapters

**Traces:** NFR6 · **AD:** AD-3, AD-7, AD-9, AD-12 · **Depends on:** 1.1

As a maintainer,
I want runnable compatibility evidence for the upstream capabilities we plan to reuse,
So that adapter development addresses real gaps instead of duplicating existing libraries.

**Acceptance Criteria:**

**Given** the initial contract scaffold and reuse-boundaries.md
**When** a small compatibility harness is run
**Then** it records exact package versions, immutable source references where available, commands and observed results for MAF successful/failed invocation hooks and tool capture using a deterministic chat-client double; the supported agent types are listed.

**Given** TextSearchProvider and AIContextProvider integration options
**When** the harness exercises labeled output, bounded payloads, scope enforcement, and final eligibility checks
**Then** it selects the existing provider if sufficient or records the precise gap requiring a custom experience provider; generic context merging is delegated to MAF.

**Given** an ephemeral PostgreSQL test database and deterministic embeddings
**When** the chosen VectorData connector or Npgsql/Pgvector route is exercised
**Then** scoped text/vector retrieval and model/dimension behavior are demonstrated; connector limitations and the boundary from canonical event/projection transactions are recorded.

**Given** ecosystem evaluation and redaction APIs
**When** representative results and nested classified payloads are adapted
**Then** the proof records which types can be reused, which domain mappings remain custom, and any unsupported behavior without creating replacement frameworks.

**Given** current AgentMemory.NET and MagiCore APIs/documentation
**When** their fit is compared with canonical storage and candidate retrieval needs
**Then** an evidence-linked reuse/defer/gap decision is recorded for each; neither becomes mandatory from README claims alone, and no full production adapter is required in this proof.

**Given** the five proof tracks (MAF hooks; context-provider fit; database/vector; evaluation-and-redaction mapping; AgentMemory.NET/MagiCore comparison)
**When** each is dispatched as an independently bounded task
**Then** each records and saves its evidence as soon as it completes rather than waiting on the others, and a blocked or failing track — for example the database/vector track — blocks only stories that depend on that specific track, never stories whose dependency is limited to a different, already-completed track (for example Story 1.4's redaction work, which depends only on the evaluation-and-redaction mapping track).

**Given** all checks finish
**When** the evidence is handed off
**Then** dependent stories cite its selected integration paths; failed checks remain visible and block only their dependent implementation, not unrelated domain work.

### Story 1.4: Sanitize Captured Experience

**Traces:** FR3, FR9, NFR2 · **AD:** AD-4, AD-12 · **Depends on:** 1.7

As a developer,
I want an executable sanitization pipeline,
So that sensitive payloads cannot enter retained experience or diagnostics.

**Acceptance Criteria:**

**Given** configured allowlisted fields and redaction rules
**When** task, tool, evidence, or reflection data is processed
**Then** unknown payload fields are omitted by default and configured secrets are redacted in nested values before downstream consumption.

**Given** sanitization throws, rejects input, or exceeds configured input limits
**When** capture handles the failure
**Then** it retains only safe identifiers and an error classification, never the original payload in storage, context, logs, or exception messages.

**Given** a custom sanitizer is registered
**When** conformance fixtures exercise redaction and rejection
**Then** all downstream sinks receive only accepted sanitized output; the documentation states that configured rules cannot guarantee detection of every arbitrary secret.

**Given** the compatibility proof selects supported redaction primitives
**When** the sanitization adapter is implemented
**Then** it composes Microsoft.Extensions.Compliance.Redaction for classified values, keeps traversal/allowlists/rejection experience-specific, and implements no competing general redactor framework.

### Story 1.2: Capture and Complete a Run In Memory

**Traces:** FR1, FR2, NFR3 · **AD:** AD-1, AD-3 · **Depends on:** 1.4

As a .NET agent developer,
I want a Core service that records sanitized observable attempts and completes run snapshots,
So that successful and failed runs become usable Experience Record candidates before persistence is introduced.

**Acceptance Criteria:**

**Given** a new Experience Run is started
**When** observable attempts and tool results are appended
**Then** Core preserves their order, summaries, results, failures, and durations.

**Given** a run completes successfully, throws, or is cancelled
**When** Core finalizes capture
**Then** the snapshot distinguishes Completed, Failed, and Cancelled execution from task verification, which remains Unknown until evaluated.

**Given** repeated completion or event callbacks carry the same identifiers
**When** Core processes them
**Then** identical duplicates are no-ops, conflicting duplicates return a conflict, and the run is finalized once.

**Given** capture receives more events or bytes than its configured positive limits
**When** the limit is reached
**Then** excess content is omitted with explicit truncation metadata and a safe diagnostic; no unbounded payload is retained.

**Given** the Core project is built
**When** dependencies are inspected
**Then** it remains independent of MAF, PostgreSQL, and model-provider implementations.

**Given** the accumulator receives mapped execution events
**When** capture is implemented
**Then** it aggregates sanitized experience only; MAF retains execution, tool invocation, sessions, streaming control, and agent retry ownership.

### Story 1.5: Evaluate Task Verification Deterministically

**Traces:** FR4 · **AD:** AD-6, AD-8 · **Depends on:** 1.2

As a developer,
I want task-specific verification criteria and composable evaluators,
So that a passing action is not mistaken for a successfully completed task.

**Acceptance Criteria:**

**Given** a task declares required checks with unique IDs
**When** exit-code, test-result, workflow-completion, human-approval, and human-correction evaluators run
**Then** each emits Pass, Fail, or Unknown with evidence IDs and producer identity; an unrelated passing test cannot satisfy a required check.

**Given** a host declares required checks and closes a verification round for the current artifact revision
**When** the pipeline aggregates that round
**Then** any Fail for a required check makes verification Failed; otherwise any missing, Unknown, or errored required check makes it Unknown; only all required checks passing yields Verified success, and an empty required set yields Unknown.

**Given** a test failed in an earlier round and passes after a fix in a later host-closed round for the current artifact revision
**When** final verification is evaluated
**Then** the later round may verify success, the earlier failure remains in attempt history, and evidence from different rounds or artifact revisions is never combined to manufacture a pass.

**Given** the artifact changed after verification, no current round is closed, or conflicting evidence exists in the selected round
**When** final verification is evaluated
**Then** stale or unclosed verification yields Unknown and a Fail in the selected round dominates Pass; agent-supplied timestamps or round selections cannot override the host-selected round.

**Given** evaluation completes
**When** the score is calculated
**Then** the completion score is the fraction of required checks conclusively passing in the selected round, or zero for an empty set, with the rule version recorded; completion score is not reuse confidence and never alone grants verification.

**Given** an evaluator fails or cancellation is requested
**When** the pipeline exits
**Then** captured evidence is retained, evaluator failure has a safe diagnostic, and cancellation remains distinguishable from task failure and does not yield verified success.

**Given** an applicable Microsoft.Extensions.AI.Evaluation result is supplied through an adapter
**When** it is mapped into evidence
**Then** metric identity, producer, diagnostics, and verification-round applicability are preserved; response-quality metrics do not automatically satisfy task checks, and domain verification remains executable without an LLM or ecosystem-specific core types.

### Story 1.3: Generate an Auditable Reflection

**Traces:** FR5, NFR7 · **AD:** AD-2, AD-6 · **Depends on:** 1.5

As a .NET agent developer,
I want evaluated run data transformed into a structured Reflection,
So that future agents can understand what failed, what worked, and when the experience may be reusable.

**Acceptance Criteria:**

**Given** an evaluated Experience Run contains failed and successful Attempts
**When** the reflector processes the run
**Then** it produces a Reflection with a lesson, failed approaches, successful approaches, preconditions, warnings, and reuse guidance.

**Given** the run has no verified success
**When** reflection is generated
**Then** the Reflection clearly identifies uncertainty and does not recommend the result as a validated procedure.

**Given** evidence and provenance are available
**When** the Reflection is created
**Then** its confidence is traceable to the evaluation and evidence rather than invented independently.

**Given** a reflection implementation is replaced
**When** the same run and evaluation are supplied
**Then** the `IExperienceReflector` contract remains stable and the resulting content remains structured.

**Given** reflection output is serialized
**When** it is inspected
**Then** it contains no private chain-of-thought requirement and remains readable as user-visible audit content.

**Given** the default reflector receives sanitized evidence and an evaluation
**When** it generates a lesson
**Then** it uses deterministic templates with source evidence IDs, carries the completion score separately from reuse confidence, marks unknown preconditions explicitly, and invents no causal explanation or verified success.

### Story 1.6: Capture Real MAF Invocations and Tool Calls

**Traces:** FR1, FR2, NFR3, NFR6 · **AD:** AD-1, AD-3, AD-12 · **Depends on:** 1.3; evidence from 1.7

As a MAF developer,
I want the adapter to collect actual invocation and tool outcomes,
So that experience capture works without manual event submission.

**Acceptance Criteria:**

**Given** an exact MAF package version is verified against official sources
**When** the adapter integration tests execute against that pinned version
**Then** ordinary, streaming, failed, and cancelled runs use demonstrated lifecycle hooks and produce correlated sanitized snapshots without relying on a success-only callback.

**Given** tools succeed or throw within concurrent invocations
**When** middleware captures their events
**Then** events belong to the correct run and preserve safe error classification; duplicate completion cannot create duplicate candidates.

**Given** capture fails while the agent succeeds or throws
**When** the outer invocation completes
**Then** capture reports its own failure without replacing the original agent result or exception, and bounded cleanup does not hang cancellation.

**Given** no database or model credentials are available
**When** the adapter test harness runs with a deterministic chat-client double and real MAF execution
**Then** successful and failed tool paths are captured and can be inspected in memory; session state retains only small coordination metadata.

**Given** the supported MAF agent types and hooks proven in Story 1.7
**When** capture is wired
**Then** it reuses InvokedCoreAsync/InvokeException and MAF middleware as appropriate, preserves original results, and neither wraps tools in a second execution engine nor creates an independent session store; unsupported agent types are documented explicitly.

## Epic 2: Reuse Relevant Experience

Agents can persist experience and retrieve applicable Historical Reference during later MAF runs.
**FRs covered:** FR6, FR7, FR8

### Story 2.1: Persist Experience Records in PostgreSQL

**Traces:** FR6, FR9, NFR1 · **AD:** AD-2, AD-5, AD-12 · **Depends on:** 1.7 (integration proof); first story of Epic 2

As a platform engineer,
I want Experience Records stored durably in PostgreSQL,
So that experience survives sessions and can be queried by authorized agents.

**Acceptance Criteria:**

**Given** a policy-approved Experience Record is supplied
**When** the PostgreSQL store saves it
**Then** task data, attempts, outcomes, evidence, reflection, environment, provenance, scope, confidence, status, and timestamps can be read back without loss.

**Given** a record is malformed or violates required scope fields
**When** persistence is attempted
**Then** the store rejects it with an actionable validation result and does not create a partial record.

**Given** the database is unavailable
**When** a persistence operation fails
**Then** the adapter exposes an observable infrastructure failure without changing Core lifecycle state.

**Given** any read, query, or save operation supplies scope
**When** the adapter executes it
**Then** required fields and exact scope matching are enforced inside the database operation; foreign-scope IDs disclose no record and cannot be overwritten.

**Given** the request names another tenant or scope beyond the host-established authorization context
**When** any store operation is attempted
**Then** it is denied before database access; no public operation accepts request-supplied identity as authorization, and the trusted host boundary is documented for the in-process library.

**Given** a new database and then an existing supported schema
**When** documented migrations run
**Then** both reach the schema needed for this story and existing data round-trips intact; precise database dependency pins are verified and recorded before implementation.

**Given** the compatibility proof's database choice
**When** persistence is implemented
**Then** it reuses an established PostgreSQL client and migration mechanism; custom work is limited to experience schema, scope predicates, revisions, and lifecycle operations rather than drivers or a general memory database.

*Note: atomic lifecycle-event append and projection-update ownership belongs exclusively to Story 2.4; this story persists and reads the canonical record and schema only.*

### Story 2.4: Commit Audited Lifecycle Changes Atomically

**Traces:** FR6, NFR4 · **AD:** AD-2, AD-6 · **Depends on:** 2.1

As a platform engineer,
I want lifecycle events and their current-state projection committed together,
So that retries and concurrent writers cannot corrupt the experience history.

**Acceptance Criteria:**

**Given** a Core-issued lifecycle command includes host authorization, event ID, and expected revision
**When** it commits
**Then** the event and projection update atomically; adapters persist Core's decision without inventing transitions or scores.

**Given** a transaction fails, a request repeats, or two writers race
**When** persistence completes
**Then** both event and projection commit or neither does; identical event IDs return the prior result, differing payloads conflict, and stale revisions cannot overwrite newer state.

**Given** initial validation, quarantine, or revocation is requested
**When** Core handles it
**Then** the minimal transitions described in the shared rules work without Epic 3, and a scoped query exposes the revision and event history.

### Story 2.5: Finalize Captured Runs into Durable Experience

**Traces:** FR4, FR5, FR6, FR9, NFR4 · **AD:** AD-4, AD-6, AD-8 · **Depends on:** 2.4; upstream captures from 1.3, 1.5, 1.6

As a MAF developer,
I want one Core finalization service connected to completed captures,
So that capture, evaluation, reflection, policy, and persistence form a usable integration.

**Acceptance Criteria:**

**Given** Story 1.6 delivers a sanitized completed snapshot
**When** Core finalization runs
**Then** it evaluates the final verification round, generates a reflection, checks host authorization and storage policy, constructs the Experience Record, and commits its initial event/projection through Story 2.4; dependency registration connects the adapter to this service.

**Given** final verification passes and reflection/policy succeed
**When** the initial record is committed
**Then** it is Validated with initial reuse confidence 2/3; otherwise permitted records with insufficient evidence or reflection failure are Quarantined with safe failure metadata and no eligible lesson; a storage-policy denial persists no payload.

**Given** a host supplies a risk-policy decision alongside verification for the current run
**When** finalization checks storage eligibility
**Then** a host-denied risk decision blocks record creation with a structured denial result and persists no payload, independent of verification outcome; the MVP performs no procedural promotion beyond this evidence-gated commit.

**Given** evaluation, reflection, or persistence fails
**When** finalization returns
**Then** the host receives a structured stage result, the agent's original result remains intact, and database failure is never reported as durable success; permitted sanitized snapshots remain available to the host for explicit retry.

**Given** a host retries finalization of the same run
**When** the command is replayed
**Then** stable run/event identifiers prevent duplicate records or initial confirmations; process-crash recovery requires host-managed durable capture and is outside the in-memory capture guarantee.

### Story 2.2: Retrieve Applicable Experience by Text

**Traces:** FR7, FR9, NFR5 · **AD:** AD-2, AD-5, AD-7 · **Depends on:** 2.5

As a .NET agent developer,
I want relevant Experience Records ranked by applicability and trust,
So that an agent receives useful historical context instead of arbitrary memories.

**Acceptance Criteria:**

**Given** a retrieval request includes task text, Scope, and an Environment Fingerprint
**When** the store searches
**Then** candidates are filtered by scope and lifecycle policy before ranking.

**Given** candidates have different relevance, confidence, recency, status, and environment compatibility
**When** ranking runs
**Then** each normalized score component and effective nonnegative weight is exposed, weights sum to one, ties sort by Experience ID, and golden fixtures prove the documented default ordering; invalid weights fail configuration validation.

**Given** a candidate is revoked, unauthorized, or outside a strict scope
**When** retrieval runs
**Then** it is not returned.

**Given** retrieval exceeds its configured timeout
**When** the timeout expires
**Then** the caller receives an empty Historical Reference result and a correlated timeout signal.

**Given** a record has a noneligible status, expired validity, insufficient configured confidence, or mismatched required environment dependency
**When** eligibility is evaluated before ranking
**Then** it is excluded; missing required environment attributes also exclude it, while a task with no required environment attributes is explicitly marked as unrestricted by that check.

**Given** the caller cancels the request
**When** retrieval observes cancellation
**Then** cancellation propagates distinctly from the internal retrieval timeout fallback.

### Story 2.6: Add Embedding Ingestion and Hybrid Retrieval

**Traces:** FR7 · **AD:** AD-9, AD-12 · **Depends on:** 2.2

As a developer,
I want stored experiences indexed for vector and text retrieval,
So that semantically related tasks can find applicable experience.

**Acceptance Criteria:**

**Given** a canonical record commits successfully
**When** the indexing service processes it
**Then** it embeds only the sanitized retrieval summary through a replaceable provider and stores model ID, dimension, content hash, and source revision separately from lifecycle state.

**Given** indexing fails or old records lack embeddings
**When** text retrieval or an explicit scoped reindex command runs
**Then** records remain text-searchable, failed indexing is observable and retryable, and reindexing is idempotent; canonical persistence does not depend on provider availability.

**Given** an index write races with a record revision or deletion
**When** it commits
**Then** a revision/hash check rejects stale embeddings and cannot recreate deleted records.

**Given** query and stored vectors share model ID and dimension
**When** hybrid retrieval combines bounded text/vector candidates
**Then** eligibility checks apply to both channels, the configured scoring policy deterministically orders results, and a model mismatch or unavailable provider yields an explicit text-only fallback with no incompatible vector comparisons.

**Given** deterministic embedding fixtures and provider-failure fixtures
**When** integration tests run
**Then** they verify ingestion, semantic candidate retrieval, reindexing, and fallback without live model credentials.

**Given** the integration path selected in Story 1.7
**When** indexing and retrieval are implemented
**Then** the adapter consumes IEmbeddingGenerator and the proven VectorData or Npgsql/Pgvector route; no new generic embedding provider or vector-store framework is introduced, and experience-specific eligibility/revision checks remain explicit.

### Story 2.3: Inject Historical Reference into MAF

**Traces:** FR8, FR9, NFR4 · **AD:** AD-2, AD-4, AD-7 · **Depends on:** 2.6

As a MAF agent developer,
I want ranked experience injected before invocation,
So that the agent can use prior verified work as context while normal controls remain authoritative.

**Acceptance Criteria:**

**Given** retrieval returns eligible candidates
**When** the MAF context provider prepares an invocation
**Then** it injects a clearly labeled Historical Reference containing source, confidence, applicability, and evidence summary.

**Given** retrieval returns no candidates or times out
**When** the invocation is prepared
**Then** the agent runs normally without fabricated or stale context.

**Given** injected experience contains imperative or unsafe text
**When** the agent receives the context
**Then** the content remains delimited as untrusted reference; an integration test forcing an unauthorized tool call proves the existing authorization boundary still denies execution, without claiming labels guarantee model obedience.

**Given** a candidate was revoked or access changed after retrieval
**When** the provider performs its final eligibility check before injection
**Then** the candidate is omitted; changes after that check affect subsequent invocations and cannot retract context already sent to a model.

**Given** a host risk-policy decision denies a candidate at injection time
**When** the context provider performs its final eligibility check
**Then** the candidate is excluded regardless of stored confidence or lifecycle status, and the denial is recorded without altering the underlying record.

**Given** eligible context exceeds the configured byte and record limits
**When** the provider constructs the payload
**Then** it includes complete references in rank order within those limits and records omissions without cutting evidence labels or serializing raw payloads.

**Given** the context-provider fit result from Story 1.7
**When** the experience integration is implemented
**Then** it uses TextSearchProvider where sufficient or a narrowly scoped AIContextProvider for documented gaps, retaining MAF's merging/source attribution and host approvals.

## Epic 3: Govern Experience Safely

Operators can control scope, lifecycle, trust, contradiction, revocation, and reuse feedback.
**FRs covered:** FR9, FR10

### Story 3.1: Administer Explicit Experience Sharing Grants

**Traces:** FR9, NFR1 · **AD:** AD-4, AD-5 · **Depends on:** 2.3 (Epic 2 complete); first story of Epic 3

As an enterprise platform engineer,
I want authorized, audited sharing grants layered on existing strict scope enforcement,
So that experience cannot cross tenant or authorization boundaries.

**Acceptance Criteria:**

**Given** a store or retrieval request lacks required Scope
**When** policy evaluation runs in strict mode
**Then** the operation is denied and no global scope is inferred.

**Given** an Experience Record belongs to another tenant or unauthorized project
**When** a retrieval query executes
**Then** the record is excluded at the persistence boundary.

**Given** captured data fails sanitization
**When** storage or injection policy evaluates it
**Then** the operation is denied or redacted according to configured policy, and the decision is recorded.

**Given** an authenticated administrator supplies an experience ID, recipient scope, reason, and expiry
**When** a sharing grant is created
**Then** its actor and audit event are persisted atomically, it can relax only optional scope fields within the same tenant/application/project, and it never changes evidence confidence or promotes a procedure.

**Given** access is permitted by a sharing grant
**When** the recipient reads or requests injection
**Then** only read/retrieval/injection is permitted; mutation, deletion, feedback submission, and grant delegation require separate host authority and cannot be inferred from the grant.

**Given** a grant expires or is revoked
**When** a subsequent retrieval or pre-injection authorization check runs
**Then** access through that grant is denied; callers cannot issue their own grants without administrator authority supplied by the host.

### Story 3.2: Manage Audited Experience Lifecycle Transitions

**Traces:** FR10 · **AD:** AD-2, AD-6, AD-8 · **Depends on:** 3.1

As an operator,
I want explicit lifecycle transitions with revision checks,
So that contradicted, stale, or unsafe experience stops influencing agents.

**Acceptance Criteria:**

**Given** a Candidate Experience Record receives verified supporting evidence
**When** Core processes the validation event
**Then** it becomes Validated only if the required task checks and policy pass; Candidate with insufficient evidence becomes Quarantined and remains ineligible.

**Given** later evidence contradicts an experience
**When** the contradiction event is processed
**Then** a Validated or Reinforced record becomes Contested and is excluded from injection.

**Given** an operator revokes an experience
**When** the revocation is persisted
**Then** future retrieval excludes the record while preserving the reason and provenance.

**Given** an experience is superseded or stale
**When** lifecycle maintenance runs
**Then** the status and retrieval eligibility change without deleting the audit history.

**Given** a management transition is requested
**When** Core validates it
**Then** only Candidate→Validated/Quarantined, Validated→Reinforced, Validated/Reinforced→Contested/Stale/Superseded, and any non-Revoked state→Revoked are accepted in the MVP; same-state identical events are no-ops and other transitions are rejected.

**Given** supersession is requested
**When** Core validates the replacement
**Then** it requires a different eligible record in the same exact scope, records the replacement ID, rejects cycles, and uses atomic expected-revision persistence; advanced revalidation of terminal/excluded states is deferred.

### Story 3.4: Apply Evidence-Based Confidence Updates

**Traces:** FR10 · **AD:** AD-8 · **Depends on:** 3.2

As an operator,
I want repeatable confidence updates from accepted evidence,
So that retries or concurrent feedback cannot inflate trust.

**Acceptance Criteria:**

**Given** a validated record and unique accepted confirmation or contradiction
**When** Core calculates confidence
**Then** the versioned heuristic computes (1 + S) / (2 + S + F), where S counts independent accepted supporting validations including the initial validation once, and F counts accepted contradictions; it is independent of completion score and status eligibility.

**Given** one initial validation and no reuse evidence
**When** confidence is calculated, then independently confirmed, then contradicted
**Then** scores are respectively 2/3, 3/4, and 3/5; contradiction also makes the experience Contested, so its numeric score cannot restore eligibility.

**Given** the same evidence is submitted again or two writers share an expected revision
**When** feedback is committed
**Then** duplicate evidence cannot increment counters twice, conflicting evidence IDs are rejected, and a revision conflict is returned without silently losing an update.

**Given** two accepted evidence submissions reference the same originating Run ID and Verification Round, even under different Evidence IDs
**When** confidence recalculates S or F
**Then** only the first accepted submission for that (Experience ID, Run ID, Verification Round ID) pair counts toward S or F; for human-sourced evidence the independence key is (Experience ID, Reviewer Identity, Run ID) instead; later duplicates under new Evidence IDs are recorded for audit but excluded from the count.

**Given** accepted evidence changes confidence
**When** an operator inspects history
**Then** prior/new scores, counters, evidence IDs, rule version, and actor are reconstructable; the score is documented as a heuristic, not a calibrated probability.

### Story 3.3: Record Experience Reuse Feedback

**Traces:** FR10 · **AD:** AD-2, AD-6 · **Depends on:** 3.4

As a platform engineer,
I want to record whether retrieved experience helped a later run,
So that confidence reflects real reuse outcomes.

**Acceptance Criteria:**

**Given** an invocation used one or more Experience Records
**When** the run completes
**Then** reuse feedback links the run, retrieved record IDs, outcome, evaluator result, and reuse measure.

**Given** an experience was injected but no attribution evidence exists
**When** feedback is recorded
**Then** it records exposure with benefit Unknown and changes neither confidence nor confirmation/contradiction counters.

**Given** a caller attributes improvement or harm to an experience
**When** feedback is accepted
**Then** it must include an authorized human assessment or a configured comparative evaluator result with evidence, run and experience IDs, and a unique feedback ID; otherwise attribution remains Unknown.

**Given** reuse produces verified improvement
**When** feedback is accepted
**Then** the originating experience receives an explicit supporting confirmation event.

**Given** reuse produces a failure attributable to the historical reference
**When** feedback is accepted
**Then** the originating experience receives a contradiction or warning event without silently deleting it.

## Epic 4: Operate and Measure the Learning Loop

Developers and operators can observe, test, and validate the complete production learning loop.
**FRs covered:** FR11 and NFR1–NFR8

### Story 4.1: Instrument the Experience Learning Loop

**Traces:** FR11, NFR2, NFR7 · **AD:** AD-1, AD-11 · **Depends on:** 3.3 (Epic 3 complete); instruments operations delivered in 1.2–3.4; first story of Epic 4

As an operator,
I want correlated OpenTelemetry signals for experience operations,
So that I can diagnose retrieval, policy, evaluation, persistence, and reuse behavior.

**Acceptance Criteria:**

**Given** an Experience Run executes
**When** capture, evaluation, retrieval, policy, persistence, and reuse operations occur
**Then** spans include run and Experience Record correlation IDs, while metrics use only bounded dimensions such as operation and outcome; IDs, task text, and raw error strings are not metric labels.

**Given** telemetry is emitted
**When** attributes are serialized
**Then** secrets, raw sensitive values, and private chain-of-thought are excluded.

**Given** a dependency times out or fails
**When** telemetry is collected
**Then** the failure is visible with operation, duration, and safe error classification.

**Given** OpenTelemetry export is disabled or unavailable
**When** previously instrumented operations run
**Then** host-visible safe diagnostics remain available and execution does not depend on an exporter; run/record IDs belong in traces, not unbounded metric labels.

**Given** MAF agent/chat instrumentation is already enabled
**When** experience instrumentation is added
**Then** it emits only experience operations and links them to existing Activity context; a test verifies it does not add duplicate agent/model/tool execution spans, enable sensitive MAF logging, or create a second exporter pipeline.

### Story 4.2: Deliver the End-to-End MAF Demonstration

**Traces:** FR1–FR8 (integration), NFR6 · **AD:** AD-3, AD-12 · **Depends on:** 4.1; exercises the full Epic 1–3 loop

As a .NET developer evaluating AgentExperience.NET,
I want a runnable sample showing failed-run capture through later reuse,
So that I can verify the project's value without building an application first.

**Acceptance Criteria:**

**Given** the sample is checked out with documented prerequisites
**When** the developer runs its setup and test commands
**Then** the sample builds and executes reproducibly.

**Given** the sample agent encounters an intentionally failing integration-test task
**When** it tries an unsuccessful approach and then a verified successful approach
**Then** the sample captures both, evaluates evidence, creates a Reflection, and persists the Experience Record.

**Given** the same compatible task is run again
**When** the MAF adapter retrieves context
**Then** the prior experience is injected as Historical Reference and reuse feedback is recorded.

**Given** the demo uses deterministic fixtures
**When** its report is produced
**Then** it demonstrates functional capture and reuse only and does not claim measured real-model improvement.

### Story 4.4: Measure Reuse Against a Controlled Baseline

**Traces:** FR11 (measurement), NFR5 · **AD:** AD-7 · **Depends on:** 4.2

As a maintainer,
I want a comparative evaluation of experience reuse,
So that published benefit claims can be checked independently.

**Acceptance Criteria:**

**Given** a versioned task set and predeclared trial count
**When** memory-enabled and memory-disabled trials run
**Then** they share task inputs, model version/settings, tools, limits, and verification criteria; learning data and evaluation tasks are separated and condition order is balanced.

**Given** trials include errors, timeouts, and regressions
**When** results are summarized
**Then** all trials remain in the report with task success, failed attempts, tool calls, elapsed time, sample size, and dispersion; no favorable metric or trial subset is selected after observing results.

**Given** the reference test-remediation experiment finishes
**When** its predeclared benefit gate is assessed
**Then** it reports whether mean failed attempts decreased without reducing verified task success or increasing unauthorized tool executions; a failed gate is reported as no demonstrated benefit, not silently converted into a pass.

**Given** existing evaluation reporting/test infrastructure meets a reporting need
**When** the experiment harness is implemented
**Then** it reuses that infrastructure and owns only controlled trial orchestration, experience attribution, and domain measures; it does not build a generic evaluation dashboard or reporting platform.

### Story 4.5: Delete and Expire Library-Owned Experience Data

**Traces:** FR10, NFR1 · **AD:** AD-10 · **Depends on:** 4.4

As an operator,
I want scoped deletion and explicit retention behavior,
So that revocation is not mistaken for removal of stored payloads.

**Acceptance Criteria:**

**Given** a host-authorized delete command names an experience and expected revision
**When** it commits
**Then** canonical payload, event payloads, embeddings, grants, and dependent feedback payloads are removed atomically; shared referenced experiences are not deleted, and foreign-scope requests cannot delete or reveal data.

**Given** a delete is retried or late capture/index/feedback writes arrive
**When** the store handles them
**Then** deletion is idempotent and a minimal payload-free tombstone prevents recreation under the deleted ID; documentation lists retained opaque ID, scope identifiers, and deletion timestamp explicitly.

**Given** the default configuration or an explicit host retention duration
**When** an operator invokes a scoped retention sweep
**Then** default retention is indefinite, a positive configured age is measured from CreatedAt, and the sweep deletes eligible records through the same operation in bounded batches; scheduling belongs to the host.

**Given** records contain external artifact links or data also exists in backups/exported telemetry
**When** deletion is documented and tested
**Then** the guarantee is limited to the library's live store, tombstone exceptions are stated, and host-owned backup/external-artifact cleanup is explicit; audit history is append-only during normal operation but payload erasure is an authorized exception.

### Story 4.3: Harden Compatibility, Security, and Release Verification

**Traces:** NFR1, NFR6, NFR8 · **AD:** AD-1, AD-12 · **Depends on:** 4.5

As an open-source maintainer,
I want automated quality gates for compatibility, isolation, reliability, and packaging,
So that each release is safe for downstream .NET users.

**Acceptance Criteria:**

**Given** the solution is built in CI
**When** the test and packaging workflows run
**Then** core, PostgreSQL, MAF integration, and end-to-end tests execute against the supported target frameworks.

**Given** security tests exercise tenant isolation, sanitization, revoked records, and untrusted context
**When** the suite completes
**Then** unauthorized retrieval and unsafe injection paths fail.

**Given** packages are produced
**When** package metadata and dependencies are inspected
**Then** core packages contain no forbidden adapter dependencies, symbols are versioned, and license/readme metadata is present.

**Given** an MAF package version changes
**When** the compatibility matrix runs
**Then** the adapter either passes the explicit supported version or fails with a visible compatibility result.

**Given** release verification begins
**When** the maintainer runs the documented checks
**Then** exact SDK, MAF, storage, and telemetry package pins have source-backed compatibility evidence; Story 4.5 deletion/retention tests pass and support limits are documented before claiming production readiness, and any unresolved item blocks that claim.
