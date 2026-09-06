# MVP Design Companion

## Package boundaries

```text
AgentExperience.Abstractions
  Domain records, scopes, events, queries, policies, and public interfaces.

AgentExperience.Core
  Capture orchestration, evaluation pipeline, reflection contracts, confidence,
  environment scoring, lifecycle rules, and policy coordination.

AgentExperience.MicrosoftAgentFramework
  AIContextProvider and middleware integration, failed-invocation capture,
  session correlation, and context injection.

AgentExperience.Storage.Postgres
  PostgreSQL/pgvector schema, migrations, persistence, metadata filters,
  hybrid candidate retrieval, and lifecycle updates.

AgentExperience.OpenTelemetry
  Tracing, metrics, and correlation helpers.

samples/AgentExperience.MafDemo
  One runnable end-to-end scenario with deterministic evidence.
```

## Canonical MVP record

The minimum persisted record contains:

- scoped task identity and task summary;
- ordered observable attempts with sanitized tool data;
- outcome, verification status, score, and evidence;
- structured reflection;
- environment fingerprint;
- provenance and correlation IDs;
- confidence, lifecycle status, risk, timestamps, and validation counters.

## Vertical-slice acceptance criteria

1. A MAF invocation creates an experience correlation ID before execution.
2. Tool calls and outcomes are captured with configurable sanitization.
3. A failed invocation produces a candidate record.
4. Deterministic test evidence can validate a successful outcome.
5. A reflection can be created from the run snapshot without private reasoning.
6. A record can be written to and read from PostgreSQL/pgvector.
7. Retrieval applies scope, status, confidence, and environment filters.
8. Retrieved records are injected with explicit historical-reference labeling.
9. Reuse feedback is persisted and linked to the original experience.
10. OpenTelemetry connects capture, retrieval, evaluation, and reuse.

## Delivery sequence

1. Create solution, package projects, repository metadata, Apache-2.0 license, and CI.
2. Implement abstractions and in-memory core tests.
3. Implement capture/evaluation/reflection vertical slice.
4. Implement PostgreSQL schema, migrations, and store adapter.
5. Implement hybrid retrieval and environment-aware ranking.
6. Implement MAF provider/middleware integration, including failed runs.
7. Add end-to-end sample, OpenTelemetry, security tests, and documentation.

## Deliberately deferred architecture

Neo4j projection, AgentMemory.NET adapter, Mem0Sharp adapter, MCP, CLI, background consolidation, procedural promotion, and model-based judging remain extension points. They must not leak into the MVP core dependency graph.
