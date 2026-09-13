# PRD Addendum

## Technical decisions intentionally deferred

- The canonical domain model remains independent of MAF, EF Core, PostgreSQL, Neo4j, and model providers.
- PostgreSQL/pgvector is the first production adapter; SQLite and in-memory stores are adoption and test options for later work.
- MAF integration should combine an `AIContextProvider` for retrieval/injection with middleware or an overridden invocation lifecycle for failed-run capture.
- AgentMemory.NET and Mem0Sharp are integration candidates, not dependencies of the MVP.
- Reflection may later support model-backed implementations, but the MVP contract must work with deterministic evidence and auditable structured output.

## Source reconciliation notes

The PRD absorbs the product brief and MVP specification. The production architecture remains a companion because it contains detailed domain models, storage schema, security design, and future integration decisions needed by architecture and implementation workflows.
