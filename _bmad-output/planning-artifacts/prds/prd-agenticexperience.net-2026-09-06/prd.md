---
title: "AgentExperience.NET Product Requirements Document"
status: draft
created: 2026-09-06
updated: 2026-09-07
---

# PRD: AgentExperience.NET

## 0. Document Purpose

This PRD defines the first open-source release of AgentExperience.NET for downstream architecture, epics, stories, and implementation work. It builds on the [product brief](/Users/fabriz/dev/agenticexperience.net/_bmad-output/planning-artifacts/briefs/brief-agenticexperience.net-2026-09-06/brief.md), [MVP specification](/Users/fabriz/dev/agenticexperience.net/_bmad-output/specs/spec-agentexperience-net/SPEC.md), and [production architecture](/Users/fabriz/dev/agenticexperience.net/docs/AgentExperience_NET_MAF_Production_Architecture.md). Technical alternatives and deferred integrations remain in `addendum.md`.

## 1. Vision

AgentExperience.NET lets .NET agents accumulate verified experience instead of merely retaining conversation history. It records observable attempts, failures, outcomes, evidence, environmental context, and structured reuse guidance; then retrieves relevant experience for later work as clearly labeled historical reference.

The first release is a focused reliability layer for Microsoft Agent Framework. It does not become another agent runtime or memory database. Its value is disciplined learning: experience must be scoped, sanitized, evaluated, confidence-ranked, and capable of being contradicted or revoked.

## 2. Target User

### 2.1 Jobs To Be Done

- As a .NET agent developer, I want failed and successful MAF runs captured so future agents do not repeat verified mistakes.
- As a platform engineer, I want reusable experience scoped by tenant, project, agent, and environment so lessons transfer safely.
- As an open-source maintainer, I want framework-independent abstractions and replaceable storage so the project can evolve with MAF and storage ecosystems.
- As an operator, I want provenance, evidence, lifecycle state, and telemetry so I can audit why an experience was retrieved and whether it helped.

### 2.2 Non-Users (v1)

- Teams seeking online model-weight training or autonomous self-modification.
- Users wanting a general chat-memory product without execution evidence.
- Teams seeking a replacement for MAF workflows, approvals, checkpoints, or agent orchestration.

### 2.3 Key User Journeys

- **UJ-1. Alex captures a failed and successful agent run.** Alex integrates the MAF adapter into a .NET agent. The agent attempts to resolve a failing integration test; one approach fails and a later approach passes verification. AgentExperience.NET captures both paths, sanitized tool events, evidence, environment, and provenance. Alex can inspect the resulting experience record without hidden chain-of-thought.

- **UJ-2. Alex reuses a compatible experience.** On a later run with a matching task and environment, retrieval returns the prior experience. The MAF context provider injects it as historical reference with confidence and applicability metadata. The agent uses it alongside normal instructions and approvals, then records whether reuse reduced failed attempts, tool calls, or elapsed time.

- **UJ-3. Alex rejects unsafe or stale reuse.** A retrieved candidate is outside the active tenant, revoked, stale, low confidence, or environment-incompatible. Policy and ranking exclude or downgrade it. The agent proceeds without treating the candidate as authority, and the decision is observable.

## 3. Glossary

- **Experience Record** — Durable, scoped representation of a task, observable attempts, outcome, evidence, reflection, environment, provenance, confidence, and lifecycle state.
- **Experience Run** — One correlated MAF execution whose observable events may produce an Experience Record.
- **Attempt** — An observable action or strategy step, including sanitized tool information and result; never hidden chain-of-thought.
- **Evidence** — A verifiable artifact or evaluator result supporting an outcome, such as a test result, exit code, human approval, or workflow completion.
- **Reflection** — Structured lesson containing failed approaches, successful approaches, preconditions, warnings, and reuse guidance.
- **Historical Reference** — Retrieved Experience Record content labeled as contextual evidence, not trusted instruction.
- **Environment Fingerprint** — Runtime, framework, repository, service, dependency, and deployment attributes used to assess applicability.
- **Scope** — Tenant, application, project, team, agent, and user boundaries governing storage and retrieval.
- **Lifecycle Status** — Candidate, validated, reinforced, contested, stale, superseded, revoked, or quarantined state.

## 4. Features

### 4.1 Observable Experience Capture

The system captures both successful and failed MAF executions through a correlation-aware adapter and middleware. Data is sanitized before persistence and is limited to observable execution artifacts.

#### FR-1: Start and correlate an Experience Run

The MAF integration can create a correlation identifier before agent execution and associate provider, middleware, workflow, and tool events with that identifier.

**Consequences:**

- A run identifier is available to capture and telemetry components before the first observable action.
- Session coordination stores identifiers and small metadata, not the durable experience corpus.

#### FR-2: Capture failed and successful invocations

The system records observable attempts, tool calls, results, errors, duration, and final invocation state for both successful and failed MAF invocations.

**Consequences:**

- A failed invocation can produce a Candidate Experience Record.
- The adapter does not rely solely on a post-success storage callback.

#### FR-3: Sanitize captured data

The system applies a replaceable sanitization policy to task context, tool arguments, tool results, and evidence before storage.

**Consequences:**

- The default path does not persist secrets or unsanitized sensitive fields by design.
- Callers can configure redaction and rejection behavior.

### 4.2 Outcome Evaluation and Reflection

The system turns observable run data into evidence-backed outcomes and structured lessons.

#### FR-4: Compose deterministic evaluators

The system can evaluate tool exit codes, test results, workflow completion, human approval, and human correction as independent evaluator inputs.

**Consequences:**

- Evaluator output includes outcome kind, verification status, score, evidence, and producer.
- Evaluation is deterministic and testable without an LLM.
- Verification selects the host-closed final round for the current artifact revision; prior failed attempts remain auditable and cannot prevent a later repaired result from passing. Completion score and reuse confidence are separate measures.

#### FR-5: Create structured Reflection

The system can produce a Reflection from an Experience Run and its evaluation, including failed approaches, successful approaches, preconditions, warnings, and reuse guidance.

**Consequences:**

- Reflection content is auditable and independent of private model reasoning.
- Reflection can be generated by a deterministic implementation or optional model-backed implementation behind an interface.

### 4.3 Experience Storage and Retrieval

The system persists canonical records and retrieves candidates using task relevance, metadata, confidence, status, scope, and environment compatibility.

#### FR-6: Persist Experience Records

The system can save, retrieve, query, append lifecycle events, and update status for Experience Records in PostgreSQL with pgvector.

**Consequences:**

- Records retain evidence, provenance, scope, and lifecycle fields.
- The storage adapter is replaceable through core interfaces.

#### FR-7: Rank applicable candidates

The system can retrieve and rank candidates using hybrid relevance, confidence, recency/status, and Environment Fingerprint compatibility.

**Consequences:**

- Revoked and unauthorized records are excluded.
- Stale, low-confidence, contested, or incompatible records are excluded or downgraded according to policy.

#### FR-8: Inject Historical Reference

The MAF context provider can inject ranked candidates before invocation as explicitly labeled Historical Reference content.

**Consequences:**

- Retrieved content cannot bypass normal agent instructions, tool approvals, or policy decisions.
- The prompt/context payload identifies source, confidence, applicability, and evidence summary.

### 4.4 Governance, Reuse, and Observability

The system makes experience lifecycle, scope, and reuse outcomes explicit.

#### FR-9: Enforce Scope and policy

The system evaluates storage, retrieval, and promotion decisions against Scope, risk, sanitization, and lifecycle policy.

**Consequences:**

- Strict tenant isolation is tested.
- Host-established authority bounds every requested scope. Sharing grants permit read/retrieval/injection only; writes and administrative actions require separate authority.
- Agents cannot expand permissions through retrieved experience.

#### FR-10: Track lifecycle and reuse feedback

The system can reinforce, contradict, supersede, stale, quarantine, revoke, and record reuse outcomes for Experience Records.

**Consequences:**

- A later success or contradiction updates confidence and validation counters through explicit events.
- Reuse feedback links back to the originating Experience Record.
- Mere injection records exposure, not proven benefit. Confidence changes require independent accepted attribution evidence.
- Operators can explicitly delete live-store experience payloads and run optional age-based retention sweeps. Default retention is indefinite; a minimal opaque-ID/scope/timestamp tombstone prevents recreation. Backups and external artifacts remain host-managed.

#### FR-11: Emit correlated telemetry

The system emits OpenTelemetry traces and metrics for capture, evaluation, reflection, retrieval, policy decisions, persistence, and reuse feedback.

**Consequences:**

- Operators can connect an Experience Run to retrieved records and outcomes.
- Telemetry does not require storing private chain-of-thought.

## 5. Non-Goals (Explicit)

- Reimplementing MAF execution, context infrastructure, or existing generic embedding, redaction, evaluation-reporting, identity, and telemetry frameworks. Adapters reuse those capabilities; the product owns evidence-linked experience semantics. See the accepted [reuse boundaries](../../../specs/spec-agentexperience-net/reuse-boundaries.md).

- Online model-weight training or autonomous self-modification.
- Replacing MAF agents, workflows, checkpoints, harness features, approvals, or general observability.
- Replacing AgentMemory.NET, Mem0Sharp, or general-purpose semantic memory.
- Procedural skill generation, autonomous promotion, graph reasoning, distributed reinforcement learning, MCP, CLI, and personality memory in v1.
- Treating retrieved experience as trusted instruction or a universal policy.

## 6. MVP Scope

### 6.1 In Scope

- `AgentExperience.Abstractions`, `AgentExperience.Core`, and `AgentExperience.MicrosoftAgentFramework` packages.
- PostgreSQL/pgvector storage adapter.
- Observable capture, sanitization hooks, deterministic evaluation, structured reflection contracts, confidence, lifecycle, and environment scoring.
- Hybrid retrieval, policy filtering, Historical Reference injection, reuse feedback, and OpenTelemetry.
- Runnable MAF sample and automated unit/integration tests.
- Apache-2.0 licensing and NuGet-oriented package boundaries.

### 6.2 Out of Scope for MVP

- Additional storage and memory-engine adapters; defer until the canonical model and store contract stabilize.
- Background consolidation and procedural promotion; defer until repeated validation data exists.
- Model-based judging as a required path; defer because deterministic evidence must establish the initial reliability baseline.
- MCP and CLI; defer until core APIs have adoption feedback.

## 7. Cross-Cutting Non-Functional Requirements

- **Security:** Tenant and project boundaries must be enforced at query and policy layers; secrets must be redacted or rejected before persistence.
- **Reliability:** Failed-run capture must not fail silently because the agent invocation failed; persistence errors must be observable and must not convert retrieved context into trusted fallback behavior.
- **Performance:** Retrieval should support a bounded timeout and return an empty historical-reference set safely when the store is unavailable.
- **Compatibility:** Core abstractions target .NET 8+ where feasible; the initial release targets .NET 10 and isolates MAF compatibility in its adapter package.
- **Observability:** Every retrieval and policy decision must be correlatable to an Experience Run without logging private chain-of-thought.
- **Maintainability:** Core packages cannot depend on provider, storage, or MAF implementation types.

## 8. Success Metrics

**Primary**

- **SM-1:** In the end-to-end sample, 100% of intentionally failed and successful test runs produce observable Experience Run records. Validates FR-1, FR-2.
- **SM-2:** A compatible follow-up run retrieves the relevant prior experience and receives it as labeled Historical Reference. Validates FR-6, FR-7, FR-8.
- **SM-3:** Automated tests demonstrate zero cross-tenant retrievals across the MVP test matrix. Validates FR-9.

**Secondary**

- **SM-4:** The sample records a reuse outcome and demonstrates an improvement in at least one selected measure: failed attempts, tool calls, elapsed time, or evaluator score. Validates FR-10.
- **SM-5:** A trace connects capture, evaluation, retrieval, policy, persistence, and reuse feedback for one run. Validates FR-11.

**Counter-metrics**

- **SM-C1:** No increase in unauthorized tool actions or policy bypasses attributable to retrieved experience. Prevents optimizing reuse at the expense of safety.
- **SM-C2:** No requirement to maximize the number of stored records. Prevents indiscriminate memory growth and low-quality experience accumulation.

## 9. Open Questions

1. Which exact MAF package/version should v1 guarantee?
2. Should the reference sample use integration-test remediation as the canonical scenario?
3. Which reuse metric becomes the release demo gate: elapsed time, tool calls, failed attempts, evaluator score, or a composite?
4. Which .NET 8 APIs are mandatory for `AgentExperience.Abstractions`?
5. Retention/deletion defaults are resolved by Story 4.5: indefinite retention by default, optional host-invoked age sweeps, and explicit live-store deletion with minimal tombstones. Deployment-specific backup/external-artifact policies remain the host's responsibility.

## 10. Assumptions Index

- **A-1:** Primary users are .NET developers and enterprise platform teams building MAF agents.
- **A-2:** PostgreSQL with pgvector is the first production storage adapter.
- **A-3:** The first public demonstration is a repeatable test-failure remediation scenario.
- **A-4:** The project launches as an independent Apache-2.0 open-source project.
