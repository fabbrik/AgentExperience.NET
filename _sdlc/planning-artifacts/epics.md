---
stepsCompleted: [1, 2, 3, 4]
status: ready-for-dev
validationResult: pass
validationReport: epics-validation.md
updated: 2026-09-07
inputDocuments:
  - _sdlc/planning-artifacts/prds/prd-agenticexperience.net-2026-09-06/prd.md
  - _sdlc/planning-artifacts/architecture/architecture-agenticexperience.net-2026-09-06/ARCHITECTURE-SPINE.md
  - _sdlc/planning-artifacts/architecture/architecture-agenticexperience.net-2026-09-06/solution-design.md
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
- Epic 3: 3.1 → 3.2 → 3.4 → 3.3 → 3.5 → 3.6.
- Epic 4: 4.1 → 4.2 → 4.4 → 4.5 → 4.6 → 4.3.
- Epic 5: 5.1 → 5.2 → 5.3 → 5.4 → 5.5 → 5.6.
- Epic 6: 6.2 → 6.1 → 6.3 → 6.5 → 6.6 → 6.4.
- Epic 7: 7.2 → 7.1 → 7.3.
- Epic 8: 8.1 → 8.2.
- Epic 9: 9.2 → 9.1.
- Epic 10: 10.1 → 10.2 → 10.3 → 10.4.
- Epic 11: 11.1 → 11.2.
- Epic 12: 12.1 → 12.2.
- Epic 13: 13.1.
- Epic 14: 14.1 → 14.2 → 14.3 → 14.4.
- Epic 15: 15.1.
- Epic 16: 16.1 → 16.4 → 16.2 → 16.3.
- Epic 17: 17.1 → 17.2 → 17.3 → 17.4 → 17.5 → 17.6 → 17.7.
- Epic 18: 18.1 → 18.2 → 18.3 → 18.4 → 18.5 → 18.6 → 18.7.
- Epic 19: 19.1 → 19.2 → 19.3.

Epics 16–19 were added on 2026-10-05 from a review of the documented boundaries, the developer experience, and the architecture.

Epics 5–9 and Story 3.6 were added on 2026-09-26, reconstructed from the merged pull requests (each story names its PR); they record what was delivered rather than a plan made in advance.

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

### Epic 5: Close the Directly Fixable Known Limits
Maintainers can resolve the known limits of `0.1.0-preview.1` that needed no new trust model: dependency pins, erasure telemetry, open-run bounds, retention reach, verification binding, and round trips.

### Epic 6: Resolve or Narrow the Remaining Known Limits
Operators get a bound application role, argument values on the approach line, a wider support matrix, bounded reused sessions, verified confidence independence, and opt-in crypto-shredding, each limit resolved or narrowed to an exact residual.

### Epic 7: Close the Residuals of the Narrowed Limits
Maintainers can close what Epic 6 left of KL-8 and KL-13 and narrow KL-11 to what no code change can remove.

### Epic 8: Make the Release Gate Reachable and Clear Deferred Work
Maintainers can reach a production-readiness decision by separating fixable limits from inherent boundaries, and close the items earlier stories deferred.

### Epic 9: Make the Project Readable and Its Benefit Testable
Newcomers can understand the project from a short README and focused guides, and maintainers can test the reuse benefit against a real model under a pre-registered design.

### Epic 10: Make Retrieval Environment- and Confidence-Aware
Agents get the experience that fits their environment and capabilities best, ranked by a confidence model the host can replace and age by domain.

### Epic 11: Try It Without PostgreSQL
Developers can run the whole learning loop in memory for tests, demos and quick starts, with a store proven to behave like PostgreSQL and guarded against production use.

### Epic 12: Upgrade Safely
Operators can upgrade a database created by any published preview and keep every record, with performance measured.

### Epic 13: Sign What Finalization Vouches For
Operators can detect records whose finalization claims were forged outside the library.

### Epic 14: Reflect With a Model, Safely
Developers can opt into a model-written lesson that is screened and bound to its evidence exactly like the deterministic one.

#### Story 14.3: Mark and Guard Model-Authored Lessons

**Traces:** FR5, FR8, NFR2 · **Depends on:** 14.1, 14.2

As a platform engineer,
I want model-written lessons marked, screened for content the run never contained, and labelled when injected,
So that captured tool output cannot quietly steer a model into teaching other agents to follow injected instructions.

**Acceptance Criteria:**

**Given** a reflection
**When** it is stored
**Then** it records its authorship (deterministic or model), the model reflector always marks its output as model-authored, and older records read as deterministic.

**Given** a model-authored reflection
**When** it is screened
**Then** it is quarantined if it names a URL or host absent from the captured run, carries instruction-override phrasing, or contains credential-shaped text; the deterministic reflector is unaffected.

**Given** injection
**When** a model-authored record is rendered
**Then** the Historical Reference labels it as written by a model from captured output, and a host option can exclude model-authored records from injection.

#### Story 14.4: Exclude Model-Authored Lessons at Retrieval

**Traces:** FR5, FR8 · **Depends on:** 14.3

As a platform engineer,
I want retrieval itself to leave out model-authored records when I ask it to,
So that excluding model-written lessons from injection never costs me deterministic lessons that model records outranked.

**Acceptance Criteria:**

**Given** a retrieval or candidate search that asks to exclude model-authored records
**When** model-authored records outrank deterministic ones
**Then** the store leaves them out before applying its limit, so the deterministic records fill the result.

**Given** injection with `ModelAuthoredLessons = Exclude`
**When** it retrieves
**Then** it asks retrieval for the exclusion, and still drops any model-authored record it re-reads.

## Epic 15: Isolate Tenants in the Database Too
Operators can add PostgreSQL row-level security as a second isolation layer behind the application role.

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

### Story 3.5: Audit Grant Access and Bound Grant Lifetime

**Traces:** FR9, NFR1, NFR7 · **AD:** AD-4, AD-5 · **Depends on:** 3.1, 3.3

As an enterprise platform engineer,
I want every read a sharing grant delivers to be recorded, and every grant to expire within a host-set bound,
So that "who read our team's experience, and when" is answerable and no grant is permanent.

**Acceptance Criteria:**

**Given** a recipient reads a record through a grant, by ID or through a search channel
**When** the read returns the record
**Then** an append-only access row names the permitting grant, the record, the recipient scope, the reading principal and the time, and the read tells the caller which grant permitted it; an owner reading its own record, a candidate the caller never received, and a read refused after the fetch produce no row.

**Given** a host that requires auditing
**When** an access row cannot be written
**Then** the read fails closed and returns nothing; under the default best-effort mode the read returns and the failure is reported to the host. A deployment that wires no access log behaves exactly as before.

**Given** access rows are written
**When** a read or a search delivers records
**Then** the row is written outside the read's own statement, and a search writes its rows in one batched statement.

**Given** a host-configured maximum grant lifetime
**When** a grant is requested with a longer expiry
**Then** it is refused with a field path and nothing is written; an expiry may still only shrink, and the maximum cannot be raised for a grant already issued.

### Story 3.6: Let a Grant Withhold the Approach Line

**Traces:** KL-9, FR9, NFR1 · **Depends on:** 3.5, 4.6 · **Delivered:** PR #23

As an enterprise platform engineer,
I want a sharing grant to state whether the borrowing scope's model may see the lending scope's `Approach:` line,
So that lending a lesson does not also disclose the tool names our agents used, without denying the whole record.

**Acceptance Criteria:**

**Given** a grant is issued without a disclosure level, or existed before migration `0011`
**When** it is stored or migrated
**Then** its `ExperienceGrantDisclosure` is `LessonOnly`; the level is immutable under the grant monotonicity trigger, so changing it means revoking the grant and issuing a new one, and an undefined enum value is refused as `Invalid` with nothing written.

**Given** a record is borrowed through a `LessonOnly` grant
**When** its Historical Reference block is written
**Then** the `Approach:` line is omitted and the `Shared:` line says the grant withholds the approach only when there was an approach to withhold; under `LessonAndApproach` the line is rendered, and a record in the reader's own scope is unaffected.

**Given** a store reports a shared record with no level or an unknown one
**When** it is injected
**Then** it is treated as `LessonOnly`; the host decision can deny the record but never widen its level.

**Given** a borrowed record is delivered by get, text search or vector search
**When** the access row and the grant's issue event are written
**Then** they record the level taken from the same row that names the permitting grant.

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

### Story 4.6: Make Learn-from-Failure Work Through the Adapter

**Traces:** FR1, FR2, FR5, FR7, NFR8 · **AD:** AD-1, AD-3, AD-9 · **Depends on:** 4.5

As a platform engineer,
I want a retry through the MAF adapter to be recorded as a further attempt of the same run, and the verified approach to reach a later agent,
So that an agent that fails and tries again produces a lesson a later run can use.

**Acceptance Criteria:**

**Given** a host that does not opt in
**When** an invocation is captured
**Then** behaviour is unchanged: one invocation, one attempt, run closed.

**Given** an invocation whose run descriptor names an open run to continue
**When** it is captured
**Then** its attempt is appended to that run, a host predicate decides whether this invocation closes the run, a continued run must match on task and scope or it is a conflict, and a run that has completed can never be continued.

**Given** a run is left open
**When** the host's maximum open duration or maximum attempt count is reached
**Then** the adapter completes the run and reports it through the existing capture-failure channel; no run stays open indefinitely.

**Given** a record is injected as Historical Reference
**When** the block is written
**Then** it carries the ordered tool names of the verified approach, derived from the record's attempts rather than from reflection prose, and nothing else from the captured run: no tool arguments, tool results, attempt results, attempt errors or evidence detail, whatever a host reflector wrote. The block's framing is unchanged, and the injected-content authorization boundary still denies a guarded call induced by an injected approach line.

### Story 4.3: Harden Compatibility, Security, and Release Verification

**Traces:** NFR1, NFR6, NFR8 · **AD:** AD-1, AD-12 · **Depends on:** 4.5, 4.6

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

## Epic 5: Close the Directly Fixable Known Limits

Maintainers resolve the `0.1.0-preview.1` known limits that a bounded change to existing code could fix, without breaking what did not have to break.

### Story 5.1: Unblock Newer MAF by Moving the Shared Pins

**Traces:** KL-14, KL-15, NFR6 · **Depends on:** 4.3 · **Delivered:** PR #24

As a .NET developer on a newer Microsoft Agent Framework,
I want the adapter and its shared dependencies pinned to versions that work with MAF 1.22.0,
So that the exact pins no longer block me from adopting it.

**Acceptance Criteria:**

**Given** the shipping packages
**When** their references are inspected
**Then** `Microsoft.Agents.AI` is `[1.22.0]`, the DI abstractions `[10.0.12]`, `Microsoft.Extensions.AI.Abstractions` `[10.10.0]`, and Core's redaction dependency an exact `[10.10.0]` instead of a floor, and the pin agreement test has no documented-floor exception.

**Given** MAF 1.22.0 copies `ChatClientAgentRunOptions` per run
**When** a run's options are reused sequentially, plainly, streaming, or with a host-set factory
**Then** the adapter needs no code change and tests pin that behaviour.

**Given** release verification
**When** the pin evidence and the pinned and latest MAF probes run
**Then** the evidence has hashed rows for the new pins and both probes pass at 1.22.0.

### Story 5.2: Instrument Erasure

**Traces:** KL-16, FR11, NFR7 · **Depends on:** 4.1, 4.5 · **Delivered:** PR #25

As an operator,
I want deletion, the retention sweep and the grant purge to emit the same telemetry as every other operation,
So that the library's one irreversible operation is visible and alertable.

**Acceptance Criteria:**

**Given** `DeleteAsync`, `SweepExpiredAsync` or `PurgeExpiredAsync` runs
**When** it succeeds, is refused or faults
**Then** it emits the `delete`, `retention.sweep` or `grant.purge` operation on an `AgentExperience.Storage.Postgres` source and meter, with Core's span naming, instruments, four dimensions and failure classes, pinned to Core by an agreement test.

**Given** the storage adapter references only Abstractions
**When** the instrumentation is added
**Then** it emits through the BCL, and a boundary test forbids a Core or OpenTelemetry reference from the adapter.

**Given** a planted marker in erased content
**When** any of the three operations succeeds, is refused or faults
**Then** no erased content reaches telemetry.

**Given** a sweep is interrupted by a storage failure, or a telemetry listener throws
**When** the operation reports
**Then** the count of records already erased is kept, and a throwing listener neither orphans `Activity.Current` nor drops an instrument.

### Story 5.3: Bound Every Run from the Moment It Is Opened

**Traces:** KL-7, FR2 · **Depends on:** 4.6 · **Delivered:** PR #26

As a platform engineer,
I want the open-run duration bound armed when a run is opened, not only when an invocation releases it,
So that an invocation that never returns cannot hold its run and captured payload indefinitely.

**Acceptance Criteria:**

**Given** a run is started or continued through the adapter
**When** the run is opened
**Then** its per-run timer is armed for what remains of `MaxOpenRunDuration`, reused when the invocation releases the run, and disposed when the run's entry is removed.

**Given** an invocation still running after one `MaxOpenRunDuration`
**When** twice that duration has passed
**Then** its run is completed as Cancelled underneath it and the close is reported; its answer is untouched but its attempt is refused.

**Given** the guarantees of Story 4.6
**When** the bound is armed early
**Then** there is still one live scope per run, no false report for a run that completed normally, and disposal cancels every bound.

### Story 5.4: Retention That Reaches a Subtree, and a Grant Access-Log Purge

**Traces:** KL-3, KL-10, FR10, NFR1 · **Depends on:** 4.5, 5.2 · **Delivered:** PR #27

As an operator,
I want a retention sweep that can reach every scope beneath a root, and a bounded purge of old grant access rows,
So that a project-level or per-user sweep is complete and the access log does not grow without limit.

**Acceptance Criteria:**

**Given** `SweepExpiredAsync` with `ScopeMatch.Subtree`
**When** it runs for a root scope
**Then** it reaches records with the same tenant, application and project whose team, agent and user fields are either null on the root or equal to it, never an ancestor, sibling or other project; the existing overload stays exact, and `MoreRemain` is truthful across the subtree.

**Given** a caller authorized for the root
**When** the subtree sweep runs
**Then** each candidate is re-checked against authorization and containment, erased one per transaction through the unchanged purge function, and `DeletedCount` counts only records this call erased.

**Given** an administrator calls `PostgresExperienceGrantAccessLog.PurgeOlderThanAsync` with a scope and a cutoff
**When** the purge runs
**Then** it removes access rows older than the cutoff in bounded batches through migration `0012`'s `SECURITY DEFINER` function with `EXECUTE` revoked from `PUBLIC`, refuses a cutoff inside the 30-day floor on the database's clock, and emits `grant.access.purge`.

### Story 5.5: Default-Deny Evidence Kinds, and Bind an Evaluation to Its Run

**Traces:** KL-5, KL-6, FR4, FR5 · **Depends on:** 1.3, 1.5, 2.5 · **Delivered:** PR #28

As a platform engineer,
I want a required check to name the evidence kind it accepts, and an evaluation that cannot be paired with another run's reflection,
So that verification does not silently accept any evidence and a reflection always describes the run it was computed from.

**Acceptance Criteria:**

**Given** a `RequiredCheck`
**When** it is constructed or aggregated
**Then** its expected kind is required, accepting any kind is spelled `RequiredCheck.AnyKind`, and a null or blank kind is refused.

**Given** `VerificationAggregator.Aggregate` is called with a run ID
**When** it returns
**Then** the `VerificationResult` records the run, round, revision and checks it was computed from, and only the aggregator can construct one.

**Given** a `ReflectionRequest` built from an evaluation for another run, or one whose status contradicts the run's outcome
**When** it is constructed
**Then** it throws `ReflectionBindingException`; finalization checks the reflector's output against its request and quarantines a mismatched reflection instead of storing it as Validated.

### Story 5.6: Batch the Embedding and Injection Re-Read Round Trips

**Traces:** KL-1, FR7, FR8, NFR5 · **Depends on:** 2.6, 2.3, 3.5, 3.6 · **Delivered:** PR #29

As an operator,
I want the injection eligibility re-read and the re-index path to batch their round trips,
So that retrieval and indexing latency does not grow with one call per record.

**Acceptance Criteria:**

**Given** `IExperienceRecordStore.GetManyAsync` with up to 200 IDs
**When** the PostgreSQL store answers it
**Then** one statement applies exactly `GetAsync`'s scope, grant, disclosure, tombstone and readability rules, grant deliveries are written in one access-row append, and an equivalence test compares both paths field by field.

**Given** a re-index pass
**When** records need a vector
**Then** they are embedded through `GenerateBatchAsync` in batches of `EmbeddingBatchSize` (default 16, 1-128), and a failed batch is retried one record at a time.

**Given** the MAF provider's final eligibility check
**When** it runs
**Then** it is one batch read, falling back to the per-record loop if that read throws, with omission reasons and the injected block identical to before.

**Given** an out-of-tree store or generator
**When** it does not override the new methods
**Then** the default interface implementations keep its previous per-item behaviour.

## Epic 6: Resolve or Narrow the Remaining Known Limits

Operators get each remaining known limit resolved, or narrowed to a residual stated exactly in the known-limits table.

### Story 6.2: Show Allowlisted Argument Values on the Approach Line

**Traces:** KL-8 (narrowed), FR8, NFR2 · **Depends on:** 4.6, 3.6 · **Delivered:** PR #31

As a platform engineer,
I want to allowlist, per tool, argument keys whose values the injected `Approach:` line may show,
So that a later agent learns the verified argument that mattered, not only the tool name.

**Acceptance Criteria:**

**Given** no `ExperienceInjectionOptions.ApproachArguments`, or one matching no call
**When** a block is written
**Then** it is byte for byte what it was before.

**Given** an allowlisted key for a tool
**When** the `Approach:` line is written
**Then** only that key's sanitized stored scalar value is shown, neutralized, quoted and clamped to 64 characters, under a 512-character per-line cap; other keys, objects, arrays and unreadable values render a marker or nothing.

**Given** a borrowed record under any grant level
**When** it is injected
**Then** no argument values are shown.

**Given** an allowlisted value that orders a guarded tool call
**When** the model obeys it
**Then** the approval boundary still denies the call.

### Story 6.1: Bind the Application Role in a Two-Role Deployment

**Traces:** KL-4, NFR1 · **Depends on:** 4.5, 5.4 · **Delivered:** PR #33

As an operator,
I want the stores to run as an application role that owns nothing and holds only the privileges they need,
So that the purge path is a privilege boundary, not only an audit trail.

**Acceptance Criteria:**

**Given** `ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync` run by the owner
**When** it targets a role
**Then** it refuses a missing role, an unmigrated schema, a superuser, the caller, and any owning role or member of one, revokes everything the role holds, grants the manifest, verifies effective privileges including those reachable through `SET ROLE`, and rolls back on any difference.

**Given** the application role's own connection
**When** it tries to alter a table, replace or disable a guard, modify a ledger, write a tombstone by hand, or call a purge without the opt-in
**Then** each attempt is refused.

**Given** migration `0013`
**When** it is applied
**Then** every purge and guard function pins `search_path` and the purges' `EXECUTE` stays revoked from `PUBLIC`; the entire store and vectors suites run as the application role.

### Story 6.3: Widen the Supported Matrix

**Traces:** KL-13 (narrowed), NFR6 · **Depends on:** 5.1, 6.1 · **Delivered:** PR #32

As a .NET developer,
I want the packages supported on more frameworks, PostgreSQL versions and dependency versions,
So that I can adopt the library without matching one exact environment.

**Acceptance Criteria:**

**Given** the packages
**When** they are built and tested
**Then** they target `net9.0` and `net10.0` with one public API baseline, and the storage suites run on PostgreSQL 15, 16, 17 and 18.

**Given** every dependency except MAF
**When** it is referenced
**Then** it is a floor with no upper bound; the lock files resolve the floor, and a floating-dependencies job tests the top of each range, gating on push and the weekly schedule and reporting on pull requests.

**Given** PostgreSQL 17
**When** the application-role verification runs
**Then** it refuses the `MAINTAIN` privilege and `pg_maintain` role.

### Story 6.5: Bound and Track Injection Across a Reused Session

**Traces:** KL-12 (narrowed), FR8, FR10 · **Depends on:** 5.6, 3.6, 6.2 · **Delivered:** PR #34

As a platform engineer,
I want the context provider to track what it gave each session,
So that a reused session gets a bounded budget, no repeated records, and a notice when a record it holds is no longer valid.

**Acceptance Criteria:**

**Given** a session and the default `SessionLimits` (32 records, 64 KB)
**When** invocations run
**Then** the budget caps records and bytes across the session, retrieval is not run once it is spent, and a revision the session already holds is omitted as `AlreadyDelivered` while a strictly newer revision is injected.

**Given** a held record that is erased, revoked, superseded, quarantined, un-granted, below the confidence floor, past `MaxAge`, or now read through a grant that withholds what the session saw
**When** the next invocation re-checks held records in one scope-check read with no access row
**Then** the block carries one fixed withdrawal notice, with no reason and no content, ahead of any new record, and record text cannot forge it.

**Given** an invocation fails
**When** its staged delivery is settled
**Then** it is not charged and its notices stay owed; an invalid session state injects nothing and reports `Failed`.

**Given** `SessionLimits = null` or no session
**When** a block is written
**Then** output is byte-identical to before.

### Story 6.6: Verify Confidence Independence Keys

**Traces:** KL-11 (narrowed), FR10 · **Depends on:** 3.3, 3.4, 5.5 · **Delivered:** PR #35

As an operator,
I want the run, round and assessment identifiers behind confidence evidence checked against what the library knows,
So that a caller cannot inflate confidence with invented independence keys.

**Acceptance Criteria:**

**Given** confidence evidence or attributed feedback naming a run
**When** it is submitted in the default mode
**Then** it is refused as `Unverified` with an `IndependenceRefusal` unless the run is finalized into a record in the evidence's scope or held by the capture service, and the record's own run is refused in every mode.

**Given** machine evidence naming a verification round
**When** it is checked
**Then** it must be the round finalization closed for that run, stamped as `ExperienceRecord.ClosedRoundId`.

**Given** a human assessment
**When** it is submitted
**Then** it must present an `AssessmentTokenIssuer` HMAC token bound to scope, run, reviewer, direction and records, compared in constant time, unexpired, and spent once per record by migration `0015`'s unique index in the evidence's transaction; forged, expired, replayed or mismatched tokens are refused with nothing written.

**Given** `IndependenceVerification.TrustHostSuppliedIdentifiers`
**When** a host opts out
**Then** the previous behaviour returns, keeping only the own-run rule.

### Story 6.4: Crypto-Shred Erased Records

**Traces:** KL-2 (narrowed), FR10, NFR2 · **Depends on:** 4.5, 5.2, 5.4, 6.1, 6.3 · **Delivered:** PR #36

As an operator,
I want erasure to make a record's text unreadable in every backup, replica, WAL segment and dead tuple,
So that erasure reaches copies the library cannot delete.

**Acceptance Criteria:**

**Given** `ExperienceEncryption` over an `IExperienceKeyStore` is configured
**When** records, lifecycle and evidence detail, grant reasons and reuse-feedback rationale are written
**Then** every free-text column erasure removes is stored as AES-256-GCM ciphertext under a per-record key, with associated data binding it to its record, row, column and scope; plaintext mode stays the default and behaves as before.

**Given** a sealed record is erased
**When** the erasure commits
**Then** the purge and key destruction run in one transaction: a key-store failure rolls back and leaves the record live, a destroyed key with a lost commit reads as a tombstone, and migration `0016` refuses to tombstone a sealed row unless the transaction declares it destroys the key.

**Given** a real `pg_dump` and a read of the dead tuple after erasure
**When** they are inspected
**Then** they hold only ciphertext, except the full-text vector and embedding, which stay plaintext for search and are stated as the residual.

**Given** existing records or a rotated KEK
**When** `SealPlaintextRecordsAsync` or `EnvelopeExperienceKeyStore.RewrapAsync` runs
**Then** existing records are sealed in bounded, resumable, authorized batches gated by `AllowSealing`, and keys are re-wrapped under the current KEK.

## Epic 7: Close the Residuals of the Narrowed Limits

Maintainers close what could still be fixed in KL-8, KL-11 and KL-13 after Epic 6, leaving only inherent residuals.

### Story 7.2: Close KL-13: MAF Range, net8.0, and the PostgreSQL 14 Decision

**Traces:** KL-13, NFR6 · **Depends on:** 6.3 · **Delivered:** PR #37

As a .NET developer,
I want to take a newer MAF 1.x and to run on `net8.0`,
So that the adapter's pin and the framework list no longer block my host.

**Acceptance Criteria:**

**Given** the adapter package
**When** its MAF reference is inspected
**Then** it is `[1.22.0, 2.0.0)`, the lock files resolve 1.22.0, a latest-in-range compatibility leg gates on push and the weekly schedule, and the pin agreement test refuses any other exact pin.

**Given** `net8.0`
**When** all five packages are built and tested
**Then** they target it as well, taking the .NET 10 train's `System.Text.Json` and `Microsoft.Bcl.Memory` on that framework only, and package verification checks each framework's dependency group.

**Given** PostgreSQL 14
**When** support is assessed
**Then** it stays out, with the reason recorded in the compatibility evidence: migration `0005` does not parse on 14 and every path to it edits a journaled script or adds a second schema lineage.

### Story 7.1: Show Borrowed and Nested Argument Values

**Traces:** KL-8, FR8, FR9 · **Depends on:** 3.6, 6.1, 6.2, 6.4 · **Delivered:** PR #39

As an enterprise platform engineer,
I want an owner to consent, on the grant, to specific argument values, and allowlisted keys that can reach nested values,
So that borrowed records and structured arguments can carry the verified argument without widening disclosure.

**Acceptance Criteria:**

**Given** a grant issued at `ExperienceGrantDisclosure.LessonApproachAndArguments` with `ApproachArguments`
**When** a borrowed record is injected
**Then** a value is shown only for a key both the grant and the reader's `ApproachArguments` name for the same tool, so the reader can narrow but never widen the owner's consent; `LessonOnly` and `LessonAndApproach` keep their behaviour.

**Given** an allowlisted dotted path such as `options.mode` or `targets.0`
**When** the line is written
**Then** only the scalar it ends on is shown under every Story 6.2 bound, a path ending on an object or array shows the marker, and a literal top-level key matches first.

**Given** migration `0017`
**When** it is applied
**Then** the grant's argument allowlist is stored immutably under the monotonicity trigger with no privilege-manifest change, and access rows record the new level.

**Given** a session holds a delivery that showed borrowed values
**When** the record is later read through another grant or a lower level
**Then** session tracking withdraws it.

### Story 7.3: Bind Confidence Evidence to Recorded Exposure

**Traces:** KL-11 (narrowed), FR10, NFR7 · **Depends on:** 6.5, 6.6 · **Delivered:** PR #40

As an operator,
I want evidence counted only from runs the library itself delivered the record to,
So that choosing among real runs no longer yields one independence key per run.

**Acceptance Criteria:**

**Given** an agent wrapped with capture and the context provider
**When** records are injected
**Then** each record and rendered revision is recorded on the captured run as `Provenance.ExposedTo`, only in its own agent's capture scope and not for what a reused session carried from earlier turns, and finalization copies it onto the record marked `Finalized`.

**Given** confidence evidence or a feedback attribution
**When** the run was not exposed to the record at or before the evidence's revision
**Then** it is refused as `NotExposed`, and a run known only through a hand-written record is refused as `HostWrittenRun`.

**Given** evidence admitted under the opt-out
**When** it is stored and read
**Then** it is labelled `HostTrusted` on both ledgers by migration `0018`, tagged in telemetry, and excluded on request by `ReadConfidenceAsync`.

## Epic 8: Make the Release Gate Reachable and Clear Deferred Work

Maintainers can reach a production-readiness decision and close the items earlier stories deferred. Alongside this epic, PR #41 added tag-triggered publishing through NuGet Trusted Publishing and PR #42 prepared `0.1.0-preview.2`.

### Story 8.1: Split Known Limits from Documented Boundaries

**Traces:** NFR6 · **Depends on:** 4.3, 6.4, 6.5, 7.3 · **Delivered:** PR #43

As an open-source maintainer,
I want fixable known limits separated from boundaries no code change can remove,
So that the release gate blocks on real limits and 1.0 is reachable, without claiming it.

**Acceptance Criteria:**

**Given** the README's limits
**When** they are split
**Then** a Known limits table (now empty) and a Documented boundaries table exist, KL-2, KL-11 and KL-12 move with their numbers and a written reason, and a boundary returns to Known limits if its reason stops holding.

**Given** RELEASING step 9 and the release workflow's gate
**When** they run
**Then** both count only the Known limits rows, fail if that section is missing, require a preview suffix while it is non-empty, and dropping the suffix also requires a closed deferred-work ledger.

**Given** the release tests
**When** they run
**Then** they check that the gate counts only the Known limits section, that RELEASING and the workflow run the same count, and the shape of both tables; the release notes carry both tables.

### Story 8.2: Tool-Name Hygiene, a Configurable Session State Key, and Deferred Tests

**Traces:** FR8, NFR2 · **Depends on:** 6.2, 6.4, 6.5, 7.3 · **Delivered:** PR #44

As a maintainer,
I want the items earlier stories deferred closed,
So that nothing known is left open behind the empty known-limits table.

**Acceptance Criteria:**

**Given** a tool name containing control, format, private-use or unassigned code points or lone surrogates
**When** the `Approach:` line is written
**Then** they become spaces, a name made only of them renders as `(none recorded)`, a split block marker is still neutralized, and a clean name renders byte for byte as before.

**Given** `ExperienceInjectionOptions.SessionStateKey`
**When** a provider is constructed
**Then** the key defaults to `AgentExperience.InjectionSession`, is validated (not blank, at most 128 characters, no whitespace or invisible code points, not capture's run key), and two providers with different keys keep independent session tracking.

**Given** the tests deferred from Stories 6.4 and 7.3
**When** they are added
**Then** they cover the seal function's `Deleted` and `AlreadySealed` outcomes, a feedback replay with no openable sealed copy, and `ReadConfidenceAsync`'s revision cut-off and cursor guard, each checked by temporarily breaking the code it covers.

## Epic 9: Make the Project Readable and Its Benefit Testable

Newcomers can read the project quickly, and maintainers can test the reuse benefit against a real model.

### Story 9.2: Make the Documentation Readable

**Traces:** NFR6 · **Depends on:** 8.1 · **Delivered:** PR #45

As a .NET developer new to the project,
I want a short README and one guide page per topic,
So that I can understand and start using the library in minutes without losing any documented guarantee.

**Acceptance Criteria:**

**Given** the root README
**When** a newcomer reads it
**Then** it covers what the project is, the learning loop, the five packages, a quick start that compiles against the current API, preview status, and a documentation index; depth lives in `docs/guide/`, and no fact, guarantee or caveat is dropped.

**Given** the limits tables
**When** they move to `docs/known-limits.md`, with history in `docs/limits-history.md`
**Then** RELEASING step 9, the release workflow's gate and release notes, and the workflow tests read the new file.

**Given** tracked Markdown outside `_sdlc/` and released CHANGELOG sections
**When** `MarkdownLinkTests` runs
**Then** it fails on a broken relative link or a missing anchor, and stale facts found against the code are corrected.

### Story 9.1: Run a Pre-Registered Live-Model Reuse Experiment

**Traces:** FR11 (measurement) · **Depends on:** 4.4 · **Delivered:** PR #46

As a maintainer,
I want Story 4.4's methodology runnable against a real model under a pre-registered design,
So that a benefit claim, or its absence, can be checked independently.

**Acceptance Criteria:**

**Given** `preregistration.json`
**When** the experiment runs
**Then** learning runs are finalized through the library, and evaluation tickets run under memory-disabled, memory-enabled, placebo and negative-control conditions, with success read from simulated state and no LLM judge.

**Given** the gate
**When** it is assessed
**Then** a benefit is credited only if memory-enabled beats both memory-disabled and the placebo on mean failed attempts with a one-sided sign test at p <= 0.05, with no loss of verified success and no rise in refused bypass requests, and the negative control shows no benefit; the first complete run per registered model in the append-only ledger is the confirmatory one.

**Given** provider configuration
**When** it is absent or present
**Then** the experiment never runs in CI or `dotnet test` and skips when unconfigured, takes configuration from environment variables only, stops at a hard call and token budget, and reports carry no key or prompt.

**Given** no provider key was available
**When** the story merged
**Then** no live result is recorded or claimed.

## Epic 10: Make Retrieval Environment- and Confidence-Aware

Retrieval ranks by how well a record's environment fits, by a confidence model the host can replace and age, and never injects a lesson the current agent cannot act on. Planned on 2026-09-27 from architecture sections 9.6, 9.9, 15 and 18.1.

### Story 10.1: Grade Environment Compatibility

**Traces:** FR7, Architecture 5.1#9, 9.9 · **Depends on:** 2.2, 2.6

As a platform engineer,
I want retrieval to score how closely a record's environment matches the current one, not only whether required attributes are equal,
So that among eligible records the one captured in the closest environment ranks first.

**Acceptance Criteria:**

**Given** a retrieval request with no preferred environment attributes and the default scorer
**When** records are ranked
**Then** every score, component and order is identical to before, and required attributes still exclude a mismatch as `EnvironmentMismatch`.

**Given** a request with preferred environment attributes
**When** an eligible record is ranked
**Then** its environment component is the scorer's value in [0, 1], reported with its weight, so a closer environment ranks higher all else equal; a preferred attribute never excludes a record.

**Given** a host-supplied `IEnvironmentCompatibilityScorer`
**When** it returns a value outside [0, 1], NaN, or throws
**Then** the value is clamped, NaN is treated as 0, and a throw fails the retrieval the way other port failures do, without leaking record content to telemetry.

**Given** the MAF context provider
**When** the host configures preferred attributes
**Then** they flow into retrieval the same way required attributes do, and the injected block is unchanged in format.

### Story 10.2: Make the Confidence Engine Replaceable

**Traces:** FR10, Architecture 9.6, 15 · **Depends on:** 3.4, 6.6, 7.3

As a platform engineer,
I want reuse confidence computed through a port whose default is today's heuristic,
So that a host can adopt a different evidence model without forking the library, and every stored score says which rule produced it.

**Acceptance Criteria:**

**Given** no host engine
**When** confidence is updated
**Then** the default engine reproduces `ReuseConfidenceHeuristic` exactly, and stored results are unchanged.

**Given** a host engine
**When** it returns a score
**Then** the score is validated to [0, 1], recorded with the engine's rule identifier and version on the lifecycle event, and evidence admission rules (independence, exposure) are unchanged by the engine.

### Story 10.3: Decay Confidence by Domain at Read Time

**Traces:** FR7, Architecture 15 · **Depends on:** 10.2

As a platform engineer,
I want an optional per-domain half-life applied to confidence when records are ranked,
So that lessons about fast-moving APIs fade faster than lessons about stable business rules.

**Acceptance Criteria:**

**Given** no decay policy
**When** records are ranked
**Then** results are identical to before.

**Given** a decay policy mapping a record's domain to a half-life (or no decay)
**When** a record is ranked
**Then** the confidence component is the stored confidence times the decay factor, the ranking result reports both, and stored confidence is never rewritten by a read.

### Story 10.4: Gate Injection on the Receiving Agent's Capabilities

**Traces:** FR8, FR9, Architecture 18.1, 18.2 · **Depends on:** 10.1, 6.2

As a platform engineer,
I want a record excluded from injection when its approach relies on tools the receiving agent does not have or on a risk class above the agent's,
So that an agent is not taught an approach it cannot, or must not, carry out.

**Acceptance Criteria:**

**Given** the host declares the receiving agent's available tools and maximum risk class
**When** a candidate record's approach uses an unavailable tool or exceeds the risk class
**Then** it is omitted with a distinct reason and no content leaves the store for it.

**Given** a record passes the gate
**When** it is injected
**Then** nothing about tool permission changes: the approval boundary still decides every tool call, as Architecture 18.2 requires.

**Given** no capability declaration
**When** records are injected
**Then** behaviour is identical to before.

## Epic 11: Try It Without PostgreSQL

Developers can run the learning loop without a database, for tests, demos and quick starts, using a store that passes the same behaviour contract as PostgreSQL and refuses to run in production by accident. Planned on 2026-09-28. It reverses the earlier policy of not publishing an in-memory store (stated in the sample's `InMemoryRecordStore`), by the maintainer's decision, on the condition that the package is guarded and conformance-tested.

### Story 11.1: Define a Store Conformance Suite

**Traces:** NFR6, NFR8 · **Depends on:** 2.1, 2.2, 2.4, 3.3

As a maintainer,
I want one reusable set of behaviour tests that every implementation of the core storage ports must pass,
So that a second store cannot quietly differ from PostgreSQL on the rules the library relies on.

**Acceptance Criteria:**

**Given** the record store, candidate source and reuse-feedback store ports
**When** the conformance suite is defined
**Then** it is a set of abstract xUnit test classes, parameterized by a factory for the store under test, covering create-only records and cross-scope id conflicts, idempotent lifecycle-event replay and conflicting replays, optimistic revision checks, status guards, scope and tombstone rules on reads, history and supersession, candidate filtering by scope, status and confidence with a limit and strongest-first order, and feedback idempotency.

**Given** the PostgreSQL adapter
**When** its tests run
**Then** it passes the full suite through a subclass, proving the suite describes real behaviour, and no existing PostgreSQL test changes.

**Given** a behaviour that is PostgreSQL-specific (full-text relevance values, the application role, append-only triggers)
**When** the suite is written
**Then** it is left out of the contract and named as such in the suite's documentation.

### Story 11.2: Ship a Guarded In-Memory Storage Package

**Traces:** NFR6 · **Depends on:** 11.1

As a .NET developer evaluating the library,
I want an in-memory store I can register in one line,
So that I can run capture, finalization, retrieval, injection and feedback without provisioning PostgreSQL.

**Acceptance Criteria:**

**Given** the new package `AgentExperience.Storage.InMemory`
**When** it is built and tested
**Then** its record store, candidate source and reuse-feedback store pass the full conformance suite from Story 11.1, are thread-safe, and depend only on Abstractions and the DI abstractions.

**Given** a host
**When** it registers the package
**Then** it does so only through an explicitly named method such as `AddAgentExperienceInMemoryStorageForDevelopment`, which refuses to run when the host environment is Production unless the host passes an explicit override, and every public type states that data is lost on restart and none of the PostgreSQL guarantees (append-only enforcement, erasure reach, roles) apply.

**Given** the release checks
**When** the package is added
**Then** the solution, package verification, public API baselines, pin agreement, README install table, guide package table, CONTRIBUTING layout and RELEASING are updated, and the first publish of the new package id through Trusted Publishing is verified or documented.

## Epic 12: Upgrade Safely

Planned on 2026-09-30 from the architecture's release-readiness items. Existing upgrade tests simulate old schemas by running a prefix of the scripts; none starts from a database a published preview created.

### Story 12.1: Upgrade From Databases the Published Previews Created

**Traces:** NFR6, NFR8 · **Depends on:** 2.1, 4.5

As an operator,
I want proof that a database created and filled by each published preview upgrades to the current schema and reads back intact,
So that upgrading never loses or corrupts experience.

**Acceptance Criteria:**

**Given** seeder programs that reference the published `AgentExperience.Storage.Postgres` `0.1.0-preview.1` and `0.1.0-preview.2` packages from nuget.org
**When** each creates a database with its own migrator and writes records, lifecycle events, grants and feedback through its own stores
**Then** the current migrator upgrades it under the journal, and the current stores read every record, event, grant and feedback row back with identical content, and continue the lifecycle.

**Given** CI
**When** the upgrade suite runs
**Then** it runs on every supported PostgreSQL major, and a failure names the preview and the object that did not survive.

### Story 12.2: Measure the Hot Paths

**Traces:** NFR5 · **Depends on:** 11.2

As a maintainer,
I want a BenchmarkDotNet suite for retrieval, injection and finalization on both stores,
So that performance regressions are visible and published numbers are reproducible.

**Acceptance Criteria:**

**Given** the benchmark project
**When** it runs locally
**Then** it reports the hot paths with recorded environment details, a baseline result is committed, and the project never runs in CI's test step.

## Epic 13: Sign What Finalization Vouches For

### Story 13.1: Sign Finalized Provenance and Verify It Before Counting Evidence

**Traces:** FR10, KL-11 · **Depends on:** 6.6, 7.3

As an operator,
I want the claims finalization makes about a record (origin, exposures, source run, closed round) signed with a host key and verified before confidence evidence counts,
So that a record written or edited outside the library cannot pass as a finalized one.

**Acceptance Criteria:**

**Given** a host-configured signing key ring (key id plus key, at least 32 bytes)
**When** finalization stores a record
**Then** the payload carries a signature over those claims and the key id; with no key configured, behaviour is unchanged.

**Given** confidence evidence or attributed feedback about a record
**When** a key ring is configured
**Then** a record whose signature is missing, unknown-key or invalid is treated as host-written and refused as such; rotation keeps old key ids verifiable; KL-11's documented boundary is narrowed and restated.

## Epic 14: Reflect With a Model, Safely

### Story 14.1: Screen Reflector Output Before It Is Stored

**Traces:** FR5, FR9, NFR2 · **Depends on:** 1.4, 5.5

As a platform engineer,
I want every reflector's free text sanitized and bounded before finalization stores it,
So that a host or model reflector cannot put secrets or unbounded text into a record.

**Acceptance Criteria:**

**Given** any `IExperienceReflector`
**When** finalization receives its reflection
**Then** its free-text fields pass through the configured sanitizer and length bounds, a rejection quarantines the record with a `Reflect` failure, and the default reflector's output is unchanged.

### Story 14.2: Offer an Optional Model-Backed Reflector

**Traces:** FR5 · **Depends on:** 14.1

As a platform engineer,
I want an optional reflector that asks an `IChatClient` for the lesson and guidance,
So that lessons can be richer than templates without weakening evidence binding.

**Acceptance Criteria:**

**Given** `ChatClientExperienceReflector` in the MAF package
**When** it reflects a verified run
**Then** it requests structured output, fills only the free-text fields, copies every bound field from the request, names the model in `Producer`, never asks for or stores reasoning, and any malformed or failed answer quarantines the record; the deterministic reflector stays the default.

## Epic 15: Isolate Tenants in the Database Too

### Story 15.1: Opt-In Row-Level Security Behind the Application Role

**Traces:** NFR1 · **Depends on:** 6.1, 3.1

As an operator,
I want PostgreSQL row-level security policies that confine the application role to the authorized scope,
So that a SQL-level mistake cannot read or write another tenant's rows.

**Acceptance Criteria:**

**Given** RLS enabled through the application-role privilege step
**When** the stores run as the application role
**Then** every store sets the authorized scope for its transaction, policies confine reads and writes to it (grant-shared reads included), a query without the scope sees nothing, SECURITY DEFINER purges keep working, and the whole store suite passes with RLS on; with RLS off, nothing changes.

## Epic 16: Hold Up Under Sustained Load

Found by the 2026-10-05 architecture review: risks in long-lived hosts that the existing suites do not exercise.

### Story 16.1: Release Finished Runs From the Capture Service

**Traces:** NFR3 · **Depends on:** 2.2

As an operator of a long-lived agent host,
I want the in-memory capture service to drop a run once it is finalized, abandoned or past a retention bound,
So that memory does not grow with every invocation the host has ever served.

**Acceptance Criteria:**

**Given** `InMemoryExperienceCaptureService` under a stream of runs
**When** runs finalize, are abandoned, or exceed a configurable retention bound (time and count)
**Then** their state is removed, a late call for a removed run gets the same answer as an unknown run, and a soak test shows a stable run count after many thousands of runs.

### Story 16.2: Bound the Whole Pre-Model Path, Not Only Retrieval

**Traces:** NFR3 · **Depends on:** 5.6

As a host developer,
I want the eligibility re-read after retrieval bounded the same way retrieval is,
So that a slow store cannot delay the model call by seconds.

**Acceptance Criteria:**

**Given** a store that is slow or ignores cancellation during the eligibility re-read
**When** the context provider injects
**Then** the re-read is hard-bounded and abandoned like retrieval, the per-candidate fallback is bounded by the same budget, the default timeout is reduced, and the docs state one end-to-end pre-model budget.

### Story 16.3: Unwrap Record Keys in Batches

**Traces:** NFR3, NFR5 · **Depends on:** 6.4

As an operator using crypto-shredding with a remote KMS,
I want retrieval to unwrap the keys of a result set in one batched call, and only for rows that can be returned,
So that encrypted retrieval does not time out once each unwrap takes milliseconds.

**Acceptance Criteria:**

**Given** encryption on and a key store with simulated per-call latency
**When** retrieval reads up to its candidate window
**Then** keys are fetched through a batch call (with a default that loops for existing implementations), the reader is not held open across unwraps, a destroyed key is never served afterwards, and a benchmark covers encrypted retrieval.

### Story 16.4: Bound Runs That Are Never Completed

**Traces:** NFR3 · **Depends on:** 16.1

As an operator of a host that calls the capture service directly,
I want runs that are started but never completed to be bounded too,
So that memory stays bounded without the MAF adapter's open-run timer.

**Acceptance Criteria:**

**Given** `InMemoryExperienceCaptureService` used without the MAF adapter
**When** runs are started and never completed
**Then** an optional open-run age bound (off by default, measured on the monotonic clock) completes them as abandoned so the completed-run bounds then apply, a count bound refuses new runs with a typed outcome once too many are open, and the MAF adapter's behaviour is unchanged.

## Epic 17: Close the Boundary Residuals a Code Change Can Remove

Found by the 2026-10-05 review of the documented boundaries: clauses that a code change can remove, which by the rule in `docs/known-limits.md` must not stay boundaries.

### Story 17.1: Fail Closed on Unknown Authorship and Backfill It

**Traces:** FR5 · **Depends on:** 14.4

**Acceptance Criteria:**

**Given** `ModelAuthoredLessons = Exclude`
**When** a PostgreSQL row's authorship flag is unknown (sealed before `0021`, or written by an older instance)
**Then** the candidate query treats it as model-authored, an owner-run backfill restores the flag for sealed rows, records written by the library's own model-backed reflector before 14.3 are recognized as model-authored by their producer, and KL-18's text drops these clauses.

### Story 17.2: Sign the Reflection Too

**Traces:** FR5, NFR1 · **Depends on:** 13.1

**Acceptance Criteria:**

**Given** provenance signing configured
**When** finalization signs a record
**Then** the claims (version 2) include a digest of the task text, the reflection's free text, authorship and producer; verification accepts version 1; a record whose content or authorship changed after signing is treated as unverified and model-authored; and KL-18's store-tampering clause is removed.

### Story 17.3: Keep Host-Trusted Evidence Out of the Ranked Score

**Traces:** FR7 · **Depends on:** 6.6, 13.1

**Acceptance Criteria:**

**Given** a host that opted out with `TrustHostSuppliedIdentifiers`
**When** host-trusted evidence is recorded
**Then** both stores keep it in separate counters, an option ranks and filters on the verified-only score, existing evidence is backfilled from the lifecycle history, the conformance suite asserts it, and KL-11's clause (3) is narrowed accordingly.

### Story 17.4: Serialize Session Tracking Within a Process

**Traces:** FR6 · **Depends on:** 6.5

**Acceptance Criteria:**

**Given** concurrent invocations on one session in one process
**When** they inject
**Then** session state is updated under a per-session lock with per-invocation pending stages, a race can only over-notify and never repeat or lose a delivery, version 1 state still loads, and KL-12 keeps only the cross-process and host-storage clauses.

### Story 17.5: Make the Content Hash Unlinkable After Erasure

**Traces:** NFR5 · **Depends on:** 16.3

**Acceptance Criteria:**

**Given** encryption on
**When** an embedding's content hash is written
**Then** it is keyed under the record's key so it cannot confirm a guessed summary once the key is destroyed, existing hashes migrate with at most one re-embed, and KL-2's text says so.

### Story 17.6: Put the Scope Into the Initial Lifecycle Event ID

**Traces:** NFR1 · **Depends on:** 4.5

**Acceptance Criteria:**

**Given** two scopes finalizing
**When** finalization derives the first lifecycle event ID
**Then** the scope is part of the derivation, a writer in another scope cannot make a run's first commit conflict, and a replay of an event under the old ID is still recognized.

### Story 17.7: Keep the Text Index Usable Under Row-Level Security

**Traces:** NFR1, NFR3 · **Depends on:** 15.1

**Acceptance Criteria:**

**Given** row-level security on
**When** the text channel searches
**Then** it goes through an owner-run function that applies the declared scope bounds itself and can use the GIN index, the RLS suite still passes, a security review of the function is recorded, and KL-17's index clause is removed; the clauses that stay (caller-chosen global IDs, unchecked exposures) get an accurate written reason.

## Epic 18: Inject Lessons an Agent Can Act On

Found by the 2026-10-05 consumer review: the default pipeline stores and injects lessons with almost no task knowledge, and setup takes about 80 lines.

### Story 18.1: Say What Failed and What Worked

**Traces:** FR5, FR6 · **Depends on:** 14.1

As an agent receiving a lesson,
I want the record to say which approaches failed (and how) and which worked,
So that I can avoid a known dead end instead of rediscovering it.

**Acceptance Criteria:**

**Given** a run with failed and successful attempts
**When** the default reflector reflects it and the writer renders it
**Then** the lesson names the failing step and its error class and the step that worked, failed and successful approaches are rendered bounded and sanitized, model-authored text keeps its fence, and nothing the guard would refuse is rendered.

### Story 18.2: A Compact Rendering by Default

**Traces:** FR6 · **Depends on:** 18.1

**Acceptance Criteria:**

**Given** the default injection options
**When** a block is rendered
**Then** it drops IDs, ranking arithmetic, timestamps and host detail, carries a short preamble and a one-line reason each record matched, keeps the trust labels, the current verbose rendering stays available as an option, the message role is configurable, and a test measures the token reduction.

### Story 18.3: Derive the Task Text From the Conversation

**Traces:** FR6 · **Depends on:** 5.1

**Acceptance Criteria:**

**Given** no host-supplied request resolver
**When** the context provider injects or capture starts a run
**Then** the task text is derived from the invocation's latest user messages (bounded and sanitized) and stored as the task description, so retrieval matches on what the task was about.

### Story 18.4: Async Resolvers

**Traces:** FR4, FR6

**Acceptance Criteria:**

**Given** `ResolveFinalization` and `ResolveRequest`
**When** a host needs async work (running checks, reading CI) to answer
**Then** async overloads taking a `CancellationToken` exist and are preferred, the sync forms still work, and the docs use the async form.

### Story 18.5: One-Call Setup

**Traces:** FR10 · **Depends on:** 18.3, 18.4

**Acceptance Criteria:**

**Given** a new host
**When** it calls `services.AddAgentExperience(...)` with an in-memory or PostgreSQL store and `UseAgentExperience` on the agent builder
**Then** capture, injection and finalization are wired from DI with safe defaults (sanitization, limits, clock, IDs), the README quick start fits in about 20 lines, and the existing explicit wiring still works.

### Story 18.6: Let Verified Reuse Move Confidence

**Traces:** FR7 · **Depends on:** 17.3

**Acceptance Criteria:**

**Given** opt-in automatic reuse evidence
**When** a run that was given a lesson finalizes Verified or Failed on the same task
**Then** supporting or contradicting machine evidence bound to that run's closed round is submitted for each exposed record, independence rules still apply, and a test shows confidence moving across a sequence of runs.

### Story 18.7: Concepts in Five Minutes and a Leaner README

**Traces:** FR10 · **Depends on:** 18.1, 18.2, 18.5

**Acceptance Criteria:**

**Given** a reader new to the library
**When** they open the README
**Then** it shows the short quick start, a real rendered lesson, which reflector to choose and why, links to a concepts page, keeps status and limits short with a link to known-limits, and no user-facing text refers to internal story numbers.

## Epic 19: Keep the Codebase Easy to Change

Found by the 2026-10-05 architecture review.

### Story 19.1: Track the Public API

**Acceptance Criteria:** public API analyzer baselines exist for every package, CI fails on an undeclared change, and package validation runs against the last published preview.

### Story 19.2: Split the PostgreSQL Record Store

**Acceptance Criteria:** `PostgresExperienceRecordStore` is split into cohesive collaborators (decoding, lifecycle commit, erasure and retention, sealing), the Vectors package no longer needs `InternalsVisibleTo` from the shipped store, and every suite passes unchanged.

### Story 19.3: Replace Fixed Waits in Tests With Signals

**Acceptance Criteria:** tests that assert absence after a fixed delay or hope a statement reached a lock wait on a deterministic signal instead (`pg_locks`, completion hooks, a fake clock), and central package management removes version drift.
