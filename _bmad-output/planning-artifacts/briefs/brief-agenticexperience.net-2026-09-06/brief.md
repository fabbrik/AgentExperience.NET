---
title: "AgentExperience.NET Product Brief"
status: draft
created: 2026-09-06
updated: 2026-09-06
---

# AgentExperience.NET

## Product concept

AgentExperience.NET is an open-source, framework-agnostic .NET library that gives Microsoft Agent Framework (MAF) agents portable, evidence-backed experience memory. It captures observable attempts and outcomes, evaluates whether work succeeded, produces structured lessons, and safely retrieves relevant prior experience for future runs.

**Tagline:** Portable, evidence-backed experience memory for .NET agents.

## Problem

MAF provides agent execution, tools, workflows, sessions, checkpoints, approvals, and observability, but it does not provide a first-class abstraction for durable experience:

- what task was attempted;
- which observable approaches failed or succeeded;
- how success was verified;
- where the experience applies;
- how trustworthy or current it is; and
- whether another authorized agent should reuse it.

Conversation memory and semantic fact memory are insufficient for reliable learning from failed work. Teams need a governed layer that turns execution evidence into reusable, auditable experience without storing hidden chain-of-thought or modifying model weights.

## Target users

### Primary user — [ASSUMPTION]

.NET developers and platform engineers building production agents with Microsoft Agent Framework who need agents to learn from previous work across sessions and agents.

### Secondary users — [ASSUMPTION]

- Enterprise AI platform teams operating multiple agents across projects and tenants.
- Maintainers of .NET agent tooling seeking a composable experience layer rather than another agent runtime.
- Open-source contributors interested in evaluation, memory, reliability, and MAF integrations.

## Product promise

An agent can reuse a prior lesson as historical reference, understand the conditions under which it was validated, avoid stale or contradicted guidance, and leave an auditable trail showing whether reuse helped.

Retrieved experience is context, never an authority. Policies, approvals, and normal tool controls remain in force.

## MVP experience

The first release proves one complete loop:

```text
MAF run
→ capture observable attempts and tool events
→ evaluate outcome deterministically
→ produce structured reflection
→ persist experience in PostgreSQL
→ retrieve with hybrid search
→ rank by confidence and environment compatibility
→ inject as historical context
→ record whether reuse helped
```

### Example demonstration

An agent diagnoses a failing .NET integration test. Its first approach fails; a later change fixes the issue and the test suite verifies the result. AgentExperience.NET stores the task, observable attempts, failure, successful change, test evidence, environment, and reuse guidance. In a later compatible run, the agent receives the prior experience as reference and records whether it shortened or improved the resolution.

## Goals

1. Provide framework-independent experience abstractions with a first-class MAF adapter.
2. Capture successful and failed runs using observable, sanitized data only.
3. Make evidence, provenance, confidence, environment, and lifecycle status explicit.
4. Support safe cross-session and authorized cross-agent reuse.
5. Provide deterministic evaluation first, with optional model-based evaluation later.
6. Offer PostgreSQL/pgvector as the production MVP store and a replaceable store interface.
7. Expose OpenTelemetry signals for capture, retrieval, evaluation, reuse, and lifecycle changes.
8. Remain self-hostable and compatible with local models and enterprise deployments.

## Non-goals

- Online model-weight training or self-modification.
- Replacing MAF agents, workflows, checkpoints, approvals, or harness features.
- Replacing general-purpose memory engines such as AgentMemory.NET or Mem0Sharp.
- Persisting private chain-of-thought.
- Automatically turning one successful run into a global rule.
- Building a second workflow engine or autonomous operating system.

## Differentiation

The project is differentiated by reliability rather than feature volume:

- failure-aware learning;
- evidence-backed confidence;
- environment-aware retrieval;
- explicit contradiction, revocation, supersession, and forgetting;
- safe cross-agent validation and transfer;
- auditable structured lessons without hidden reasoning dependencies;
- native C# and MAF integration.

## MVP boundaries

### Include

- `AgentExperience.Abstractions` and `AgentExperience.Core`;
- `AgentExperience.MicrosoftAgentFramework`;
- observable run/tool capture with sanitization hooks;
- deterministic outcome evaluators;
- structured reflection contracts and a basic reflector;
- confidence and environment compatibility scoring;
- PostgreSQL + pgvector storage;
- hybrid retrieval and historical-reference context injection;
- reuse feedback events;
- tenant/project/agent scope enforcement;
- OpenTelemetry instrumentation;
- integration tests and one end-to-end sample.

### Defer

- procedural skill generation and autonomous promotion;
- Neo4j graph projection;
- AgentMemory.NET and Mem0Sharp adapters;
- MCP and CLI surfaces;
- distributed reinforcement learning;
- personality memory and broad semantic memory;
- elaborate LLM judging and background consolidation.

## Success measures — [ASSUMPTION]

The MVP is successful when:

1. A developer can add the MAF adapter to an existing agent without forking MAF.
2. Both failed and successful runs are captured with no hidden chain-of-thought dependency.
3. A later compatible run retrieves a relevant experience and receives it as clearly labeled historical context.
4. Retrieval rejects or downgrades experiences outside tenant, permission, or environment boundaries.
5. The end-to-end sample demonstrates measurable reuse benefit, such as fewer failed attempts, lower tool calls, lower elapsed time, or improved evaluator score.
6. The project has reproducible tests for capture, retrieval ranking, policy gates, lifecycle transitions, and tenant isolation.

## Initial release shape — [ASSUMPTION]

- License: Apache-2.0.
- Primary language: C#.
- Initial target: .NET 10, with core abstractions designed for .NET 8+ compatibility.
- Canonical production store: PostgreSQL + pgvector.
- Initial distribution: NuGet packages plus a runnable sample repository/project.
- Project status: independent open-source project, not affiliated with or endorsed by Microsoft.

## Risks and open questions

- MAF APIs may evolve; the adapter needs compatibility isolation and integration tests.
- Deterministic evaluation may not cover every domain; evaluator composition must remain extensible.
- Reflection quality may be poor without strong evidence boundaries and clear prompts/contracts.
- Storing tool arguments, outputs, and artifacts can expose secrets; sanitization and retention must be mandatory design concerns.
- PostgreSQL may be too heavy for local experimentation; SQLite or an in-memory test store should be a later adoption aid, not a blocker for the production MVP.
- The primary launch audience, first MAF version, and exact demo scenario remain to be confirmed.

## Recommended next artifact

Create an MVP specification that freezes the domain model, package boundaries, first end-to-end scenario, acceptance criteria, and compatibility matrix. Then scaffold the solution and implement the capture-to-retrieval vertical slice before expanding the architecture.
