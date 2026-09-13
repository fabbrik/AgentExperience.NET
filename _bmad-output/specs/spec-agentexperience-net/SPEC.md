---
title: "AgentExperience.NET MVP Specification"
status: draft
updated: 2026-09-07
slug: agentexperience-net
companions:
  - reuse-boundaries.md
  - mvp-design.md
  - ../../planning-artifacts/architecture/architecture-agenticexperience.net-2026-09-06/ARCHITECTURE-SPINE.md
  - ../../planning-artifacts/epics.md
sources:
  - ../../planning-artifacts/briefs/brief-agenticexperience.net-2026-09-06/brief.md
---

# Why

Microsoft Agent Framework supplies agent execution, tools, workflows, sessions, and observability, but not a portable, evidence-backed representation of reusable experience. AgentExperience.NET fills that gap for .NET developers by capturing observable attempts, evaluating outcomes, producing structured lessons, and safely reusing them in later MAF runs.

# Capabilities

## CAP-1 — Capture observable experience

**Intent:** Record sanitized task context, observable actions, tool events, failures, outcomes, evidence, environment, and provenance for both successful and failed MAF runs.

**Success:** An integration test persists a complete run snapshot without requiring hidden chain-of-thought, and sensitive fields pass through a replaceable sanitization policy.

## CAP-2 — Evaluate outcomes deterministically

**Intent:** Convert observable run results into an outcome and evidence assessment using composable deterministic evaluators.

**Success:** The MVP can evaluate at least tool exit codes, test results, workflow completion, and explicit human approval/correction, producing a stable score and verification status.

Verification uses the host-closed final round for the current artifact revision; earlier failures remain history. Completion score and confidence in reuse are distinct.

## CAP-3 — Produce structured reflections

**Intent:** Generate an auditable lesson containing failed approaches, successful approaches, preconditions, warnings, and reuse guidance from a run and its evidence.

**Success:** A stored reflection contains only user-visible or deliberately generated structured content and can be rendered independently of the underlying model provider.

## CAP-4 — Persist and retrieve experience

**Intent:** Store experience records durably and retrieve relevant candidates using hybrid similarity, task metadata, confidence, and environment compatibility.

**Success:** A later compatible task retrieves a relevant validated experience from PostgreSQL/pgvector, while low-confidence, stale, unauthorized, or incompatible records are excluded or downgraded.

## CAP-5 — Inject historical reference into MAF

**Intent:** Integrate with MAF through a context-provider adapter that supplies retrieved experience as clearly labeled historical reference and correlates the run across pre- and post-invocation stages.

**Success:** A sample MAF agent receives retrieved experience before execution, and the adapter still captures failed invocations rather than losing them through the default successful-invocation path.

## CAP-6 — Govern lifecycle and reuse

**Intent:** Maintain status, confidence, provenance, scope, contradiction, revocation, and reuse-feedback state so experience is not treated as permanent truth.

**Success:** Tests demonstrate tenant/project/agent scope enforcement, confidence updates from confirmation or contradiction, revocation from retrieval, and recording whether reuse helped.

Host-authorized deletion and explicit retention remove live-store payloads with a documented minimal tombstone exception; external artifacts and backup cleanup belong to the host.

## CAP-7 — Observe the learning loop

**Intent:** Emit OpenTelemetry traces, metrics, and correlated identifiers for capture, evaluation, reflection, retrieval, policy decisions, persistence, and reuse feedback.

**Success:** The end-to-end sample exposes a trace that connects one MAF run to its retrieved experience, evaluator result, stored record, and reuse outcome.

# Constraints

- The core abstractions must not depend on MAF, EF Core, PostgreSQL, Neo4j, or a model provider.
- The first production storage adapter is PostgreSQL with pgvector; storage remains replaceable through interfaces.
- The first integration targets MAF and must not fork or modify MAF.
- Captured data must be observable and sanitized; private chain-of-thought is never a storage requirement.
- Retrieved experience is historical reference, not trusted instruction, and cannot bypass tool approvals or policy gates.
- Strict scope isolation is required for tenant, application, project, team, agent, and user boundaries.
- The initial release targets .NET 10 while keeping core abstractions compatible with .NET 8+ where feasible.
- Apache-2.0 is the working license assumption.
- Host-established authorization bounds all request scopes; agent-supplied scope cannot grant access. Sharing grants permit read/retrieval/injection only.
- Core owns finalization through policy-approved atomic persistence; separate revision-checked indexing generates embeddings and preserves text retrieval during provider failure.
- Reuse confidence is the versioned heuristic (1+S)/(2+S+F), with independent accepted supporting validations S and contradictions F; initial validation counts once and status gates remain separate.
- Correlation IDs belong in traces; metric dimensions are bounded by operation/outcome.
- Reuse existing MAF lifecycle/context infrastructure and ecosystem embedding, evaluation, redaction, database, and telemetry capabilities through adapters; reuse-boundaries.md defines ownership and Story 1.7's executable compatibility gate.

# Non-goals

- Online model-weight training or autonomous self-modification.
- Replacing MAF runtime, workflows, checkpoints, harness features, approvals, or general observability.
- Replacing AgentMemory.NET, Mem0Sharp, or general-purpose semantic memory.
- Procedural skill generation, autonomous promotion, graph reasoning, distributed reinforcement learning, MCP, CLI, or personality memory in the MVP.
- Treating one successful run as a universal policy.

# Success signal

The MVP is demonstrated by a runnable MAF sample in which an agent’s failed and successful attempts are captured, deterministically evaluated, reflected into a structured experience, stored in PostgreSQL, retrieved during a later compatible run, injected as historical context, and measured for reuse benefit. Automated tests prove sanitization, tenant isolation, lifecycle transitions, retrieval ranking, and failed-run capture.

# Open questions

- Which exact MAF package/version should the first compatibility matrix guarantee?
- The reference demo uses test-failure remediation; comparative benefit means fewer mean failed attempts without reducing verified success or increasing unauthorized executions. Actual improvement remains unproven until measured.
- Which .NET 8+ APIs are mandatory for the abstractions package?
