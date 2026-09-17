# Reuse boundaries and compatibility evidence

Status: executable compatibility proof completed by Story 1.7. All 4 runnable tracks (MAF invocation hooks;
context-provider fit; PostgreSQL/pgvector; evaluation-and-redaction mapping) are proven by
`tests/AgentExperience.CompatibilityProof/` -- run with `dotnet test --filter FullyQualifiedName~CompatibilityProof`
(14/14 passing at authoring time on 2026-09-07; Docker required for Track 3, ephemeral via Testcontainers, torn
down after the run). Track 5 (AgentMemory.NET/MagiCore) is an API-fit comparison only, no runnable proof required
by design -- both remain deferred. Epic 2 stories (2.1, 2.3, 2.6) may cite the decisions below directly.

**Local run note:** Testcontainers' Ryuk resource-reaper container can fail to start under some local Docker
setups (observed on Rancher Desktop, which rejected Ryuk's docker.sock bind-mount) -- if `dotnet test` hangs or
errors starting the Track 3 container locally, set `TESTCONTAINERS_RYUK_DISABLED=true` as a workaround; the
container is still explicitly torn down by the test fixture regardless of Ryuk. GitHub Actions' standard Docker
Engine (`ubuntu-latest`) is not known to need this.

## Ownership contract

| Area | Reuse | AgentExperience.NET owns |
| --- | --- | --- |
| Invocation and tools | MAF lifecycle, function middleware, sessions, streaming, approvals. Caveat: `FunctionInvocationContext` (the function-middleware type) has no exception/failure property, and it is unverified whether a thrown tool exception reaches middleware's `next()` call cleanly or is caught earlier by `FunctionInvokingChatClient` and converted to an error `FunctionResultContent` -- wrap `next()` in a try/catch rather than assuming exception visibility, see Track 1 below | Sanitized event-to-experience mapping and run aggregation |
| Context injection | Custom `AIContextProvider` (simple tier) -- `TextSearchProvider` evaluated and rejected, see Track 2 below | Eligibility, evidence labels, scope checks, bounded experience payload |
| Embeddings/search | Microsoft.Extensions.AI `IEmbeddingGenerator`; `CommunityToolkit.VectorData.PgVector` for retrieval; plain Npgsql/Pgvector for canonical writes, see Track 3 below | Revision-aware indexing, experience filters and applicability scoring |
| Evaluation | Microsoft.Extensions.AI.Evaluation adapter for applicable metrics; existing reporting tooling | Verification rounds, artifact binding, deterministic task checks, evidence acceptance |
| Redaction | Microsoft.Extensions.Compliance.Redaction primitives (flat-string transform only, see Track 4 below) | Payload traversal, field classification, allowlists, limits, rejection policy |
| Telemetry | MAF instrumentation, BCL ActivitySource/Meter, host OpenTelemetry exporters | Experience-specific spans, metrics, and links to existing traces |
| Authorization | Trusted host principal/authorization services | Experience resource access checks and read-only sharing grants |
| Canonical persistence | Existing database client, transactions, migrations mechanism | Experience schema, event/projection atomicity, revisions, deletion semantics |

Portable domain contracts remain free of adapter types. Existing neutral ecosystem interfaces are used at adapter boundaries rather than cloned as generic provider abstractions. A domain-specific port remains justified when it carries experience semantics, such as atomic lifecycle commits or task verification rounds.

No new agent runtime, tool runner, session store, workflow engine, general vector database, generic memory consolidation engine, identity service, evaluation reporting platform, or telemetry exporter is in scope.

## Story 1.7 compatibility proof evidence

Full narrative findings: `_sdlc/implementation-artifacts/story-1-7-research-digest.md`. Runnable harness:
`tests/AgentExperience.CompatibilityProof/` (`IsPackable=false`; proof-only, never referenced by any shipping
package). Every citation below is an exact package version plus either a NuGet package-version URL or a
commit/tag-pinned source URL -- no bare `main`-branch reference.

### Track 1 -- MAF invocation hooks (AC1, `MafHooksProof.cs`)

- Package: `Microsoft.Agents.AI` **1.20.0** (GA). https://www.nuget.org/packages/Microsoft.Agents.AI/1.20.0
- `AIContextProvider` source, pinned to the `dotnet-1.20.0` tag:
  https://github.com/microsoft/agent-framework/blob/dotnet-1.20.0/dotnet/src/Microsoft.Agents.AI.Abstractions/AIContextProvider.cs
- `ChatClientAgent` source, same pin:
  https://github.com/microsoft/agent-framework/blob/dotnet-1.20.0/dotnet/src/Microsoft.Agents.AI/ChatClient/ChatClientAgent.cs
- **Decision confirmed**: observe both successful and failed invocations by overriding `InvokedCoreAsync`
  directly, never a "simple tier" `StoreAIContextAsync` override alone. Proven by two contrasting, actually-run
  tests: `Simple_tier_StoreAIContextAsync_override_is_never_called_for_a_failed_invocation` (0 calls against a
  deterministic thrown exception) and `..._is_called_for_a_successful_invocation` (1 call). Confirmed at the
  source level: `ChatClientAgent.RunCoreAsync`/`RunStreamingAsync` call every registered
  `AIContextProvider.InvokedAsync` on both the success path and the failure path (via
  `NotifyProvidersOfFailureAtEndOfRunAsync`), then rethrow -- so this hook fires for every `ChatClientAgent` run,
  win or fail.
- Which agent types the hook fires for: `ChatClientAgent` -- proven above, both hooks. Hand-rolled `AIAgent`
  subclasses only receive `AIContextProvider` hooks if their own `RunCoreAsync` override calls them explicitly
  (not automatic -- documented, not independently re-verified here, since only the `Microsoft.Agents.AI` package
  is referenced by this proof project). `A2AAgent` (separate `Microsoft.Agents.AI.A2A` package, not referenced
  here): function-invocation middleware does not apply, since tools execute server-side for that agent type.
  https://learn.microsoft.com/en-us/agent-framework/agents/middleware/
- **Not fully API-locked**: even at 1.20.0 GA, parts of `AIContextProvider`'s surface remain tagged
  `[Experimental(...AgentsAIExperiments)]` in source -- e.g. `InvokingContext`'s constructor, per the pinned
  `AIContextProvider.cs` above -- and this proof project's own use of `InvokedCoreAsync`/`StoreAIContextAsync`
  overrides required `#pragma warning disable MAAI001` (see `MafHooksProof.cs`). Do not present this hook as
  fully API-locked to Epic 2 stories.
- Function-call middleware is a separate, lower-level surface not covered by this proof project's dependencies:
  `Microsoft.Extensions.AI` `FunctionInvocationContext` (registered via `agent.AsBuilder().Use(middleware).Build()`)
  has no exception/failure property (`Arguments`/`CallContent`/`Function`/`FunctionCallIndex`/`FunctionCount`/
  `IsStreaming`/`Iteration`/`Messages`/`Options`/`Terminate` only), and it is unverified whether a thrown tool
  exception reaches a middleware's `next()` call cleanly or is caught earlier by `FunctionInvokingChatClient` and
  converted to an error `FunctionResultContent` -- wrap `next()` in a try/catch rather than assuming exception
  visibility. https://learn.microsoft.com/en-us/agent-framework/agents/middleware/

### Track 2 -- Context-provider fit (AC2, `ContextProviderFitProof.cs`)

- **Decision confirmed: reject `TextSearchProvider`, build a custom `AIContextProvider`.** Proven end to end by
  `Custom_provider_injects_labeled_messages_within_scope_and_under_the_payload_cap`: a custom simple-tier
  provider (`ProvideAIContextAsync` override) delivers `Source`/`Confidence`-labeled messages (carried in
  `ChatMessage.AdditionalProperties`), enforces a payload cap, and gates on scope -- through a real
  `ChatClientAgent` invocation, verified by inspecting exactly what a recording fake `IChatClient` received.
  `Custom_provider_injects_nothing_when_no_candidate_is_in_scope` proves the gate excludes out-of-scope
  candidates entirely.
- `TextSearchProvider` source, pinned to `dotnet-1.20.0`:
  https://github.com/microsoft/agent-framework/blob/dotnet-1.20.0/dotnet/src/Microsoft.Agents.AI/TextSearchProvider.cs
  -- confirms `TextSearchResult` (`SourceName`/`SourceLink`/`Text`/`RawRepresentation` only) has no
  confidence/applicability field and that `RawRepresentation` is debug-only, not sent to the model; and that MAF,
  not the caller, builds the search query from chat history.
- Context-provider concepts (package `Microsoft.Agents.AI` 1.20.0):
  https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/context-providers
- No built-in timeout exists on either path -- confirmed directly against the pinned source above for both
  `TextSearchProvider` and the `AIContextProvider` base type. A caller-side timeout wrapper is required
  regardless of which path is chosen; this is a shared gap, not a reason to prefer one path over the other.
- Multiple providers compose natively: `ChatClientAgentOptions.AIContextProviders` is a list, each called in
  sequence with the next receiving the previous one's merged output (sequential, not parallel) -- relevant for
  Epic 2 stories that may register their own provider alongside this one on the same agent.
  https://learn.microsoft.com/en-us/agent-framework/concepts/agents/agent-pipeline

### Track 3 -- PostgreSQL / pgvector (AC3, `PostgresVectorProof.cs`)

- Packages: `Npgsql` **10.0.3** (https://www.nuget.org/packages/npgsql/10.0.3), `Pgvector` **0.3.2**
  (https://www.nuget.org/packages/Pgvector/0.3.2), `Microsoft.Extensions.VectorData.Abstractions` **10.9.0**
  (https://www.nuget.org/packages/Microsoft.Extensions.VectorData.Abstractions/10.9.0),
  `CommunityToolkit.VectorData.PgVector` **1.0.1**
  (https://www.nuget.org/packages/CommunityToolkit.VectorData.PgVector/1.0.1), `Testcontainers.PostgreSql`
  **4.15.0** (https://www.nuget.org/packages/Testcontainers.PostgreSql/4.15.0). Container image:
  `pgvector/pgvector:pg16`.
- Connector source, pinned to the exact commit the 1.0.1 package was published from (per its own nuspec
  `<repository>` element):
  https://github.com/CommunityToolkit/AI/tree/126f5ae78aef91192d161088737e3c00acc3a23c/MEVD/src/PgVector
- **Decision confirmed**: `CommunityToolkit.VectorData.PgVector` for retrieval; plain `Npgsql` for canonical
  transactional writes -- the current, actively-maintained connector, successor to the now-legacy
  `Microsoft.SemanticKernel.Connectors.PgVector` (last version 1.74.0-preview, 2026-03-20; do not use for new
  code).
- Neither `PostgresVectorStore` nor `PostgresCollection<TKey,TRecord>` can share an ambient transaction with a
  canonical write: proven two ways, not just inferred from reading the source. (1)
  `Vector_connector_constructors_accept_only_a_NpgsqlDataSource_or_connection_string_never_an_ambient_transaction`
  reflects over every public constructor of both types and confirms none accepts
  `NpgsqlConnection`/`NpgsqlTransaction`. (2)
  `Vector_upsert_on_a_pooled_data_source_does_not_participate_in_a_concurrent_plain_Npgsql_transaction` runs a
  real, still-open plain-`Npgsql` transaction alongside a `PostgresCollection<,>.UpsertAsync` call on the *same*
  pooled `NpgsqlDataSource`, and shows the vector write becomes visible immediately (its own connection commits
  independently) while the canonical write stays invisible to a third connection until it commits.
- `Scoped_vector_search_returns_only_records_within_the_requested_scope_closest_first` confirms
  `VectorSearchOptions<TRecord>.Filter` scoping works against a property marked
  `[VectorStoreData(IsIndexed = true)]`, against a real `pgvector/pgvector:pg16` container spun up by
  Testcontainers and torn down afterward (confirmed via `docker ps` showing no leftover container post-run). The
  proof tested filtering *on* an indexed property; it did not test that filtering on a *non-indexed* property is
  rejected, so "only on `IsIndexed` properties" (per the digest) is carried forward as a documented claim, not
  independently re-verified here. Source: https://learn.microsoft.com/en-us/dotnet/ai/vector-stores/how-to/use-vector-stores
- No reindex/VACUUM API exists on `VectorStoreCollection` -- confirmed by its member list in
  `Microsoft.Extensions.VectorData.Abstractions` **10.9.0** itself (where `VectorStoreCollection<TKey,TRecord>` is
  defined; the `CommunityToolkit.VectorData.PgVector` connector only implements this same abstract surface, it
  doesn't add to it): `CollectionExistsAsync`/`EnsureCollectionExistsAsync`/`EnsureCollectionDeletedAsync`/
  `GetAsync`/`DeleteAsync`/`UpsertAsync`/`SearchAsync`/`GetService` only. Source, pinned to the `v10.9.0` tag:
  https://github.com/dotnet/extensions/blob/v10.9.0/src/Libraries/Microsoft.Extensions.VectorData.Abstractions/VectorStoreCollection.cs
  -- index maintenance stays raw-SQL/out-of-band, deferred to a later story.

### Track 4 -- Evaluation & redaction (AC4, `EvaluationRedactionProof.cs`)

- Packages: `Microsoft.Extensions.Compliance.Redaction` **10.9.0**
  (https://www.nuget.org/packages/Microsoft.Extensions.Compliance.Redaction/10.9.0),
  `Microsoft.Extensions.AI.Evaluation` **10.9.0**
  (https://www.nuget.org/packages/Microsoft.Extensions.AI.Evaluation/10.9.0). Source for both, pinned to the
  `v10.9.0` tag:
  https://github.com/dotnet/extensions/blob/v10.9.0/src/Libraries/Microsoft.Extensions.Compliance.Abstractions/Redaction/Redactor.cs
  and
  https://github.com/dotnet/extensions/blob/v10.9.0/src/Libraries/Microsoft.Extensions.AI.Evaluation/IEvaluator.cs
- **Decision confirmed**: `Redactor` (the library's abstract base, subclassed by `FixedMaskRedactor` in the
  proof) is a flat string transform only -- `Redact(ReadOnlySpan<char>, Span<char>)` and
  `GetRedactedLength(ReadOnlySpan<char>)`, no object-graph awareness, and no "reject" outcome (only "redact in
  place"). AgentExperience.NET's own recursive traversal (`SanitizePayload` in the proof) owns walking a nested
  payload, classifying which fields are secret vs. allowlisted vs. unknown, and rejecting (omitting) unknown
  fields -- proven by `Custom_traversal_redacts_classified_leaf_values_at_any_depth_via_the_library_Redactor` and
  `Custom_traversal_rejects_unknown_fields_at_any_depth_a_policy_the_library_has_no_equivalent_for`, both run
  against a payload nested two levels deep. Confirms the plan already recorded in `epics.md`/Story 1.4's own AC.
  Background discussion (traversal exists only via the logging source generator's `[LogProperties(Transitive =
  true)]`, not as a general callable API): https://github.com/dotnet/extensions/discussions/4735
- **Decision confirmed**: `IEvaluator.EvaluateAsync(...)` is genuinely general-purpose, not LLM-bound --
  `DeterministicExitCodeEvaluator` in the proof wraps a plain exit-code-style bool with no `ChatConfiguration`
  and no model call, and both a passing and a failing case are proven via `BooleanMetric.Value`.
  https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.evaluation.ievaluator
- **Judgment call, not a hard constraint** (carried over from the digest, not independently re-tested here, and
  already recorded in `epics.md` Story 1.5 AC7 -- "domain verification remains executable without an LLM or
  ecosystem-specific core types"): `Microsoft.Extensions.AI.Evaluation` is fairly heavyweight for
  purely-deterministic needs -- it drags in `Microsoft.Extensions.AI.Abstractions` and an LLM-response-shaped
  caching/reporting model. Story 1.5's core deterministic evaluator pipeline stays independent of this package;
  only its *results* are optionally adapted later as evidence.
  https://learn.microsoft.com/en-us/dotnet/ai/evaluation/libraries -- **corrected rationale**: this is not about
  reducing the deployed application's dependency footprint, since `packages.lock.json` shows `Microsoft.Agents.AI`
  1.20.0 already transitively depends on `Microsoft.Extensions.AI.Evaluation` 10.9.0 -- Story 1.6's MAF adapter
  pulls this package in regardless of whether `Core` references it directly. The actual reason to keep `Core`
  independent is the AD-1 architectural boundary (`Core` depends only on the BCL plus explicitly approved neutral
  abstractions, never a specific ecosystem package), not application footprint.

### Track 5 -- AgentMemory.NET / MagiCore comparison (evidence-only, no runnable proof)

Both checked live against their GitHub repositories on 2026-09-07 (not against a pinned commit, since this is a
maturity/fit comparison of actively-changing projects, not an API-shape claim to be regression-tested):
https://github.com/joslat/agent-memory-dotnet (v1.5.0, ~weekly releases, last commit 2026-09-02; dedicated
`AgentMemory.AgentFramework` NuGet package with a `Neo4jMemoryContextProvider` implementing `AIContextProvider`;
Neo4j-only storage; explicit owner/store isolation; trust/lifecycle states not implemented, open issue #92) and
https://github.com/jihadkhawaja/magicore (v1.0.0 tagged 2026-09-05, 45 NuGet downloads; sample-only MAF
integration, no packaged adapter; Postgres/pgvector supported via `Microsoft.Extensions.VectorData`;
metadata/run-filter partitioning only, no documented isolation model; no trust/lifecycle states found).

**Decision confirmed: defer both** (consistent with `ARCHITECTURE-SPINE.md`'s existing Deferred list). Reference
only, not a dependency -- installing either is not required, and neither is added as a compile-time dependency of
this or any other AgentExperience.NET project. Correction preserved from the digest: the claim that
"Mem0Sharp/MagiCore is an official Microsoft Agent Framework sample" is false -- Microsoft's own docs state "Mem0
integration isn't currently available for Agent Framework .NET," and a code search of `microsoft/agent-framework`
for "Mem0Sharp" returns zero results.

## Compatibility proof exit criteria (satisfied by Story 1.7)

Exact versions, official source links, runnable commands, observed results, and supported limitations are now
recorded above for MAF hooks, context injection, one PostgreSQL retrieval route, evaluation mapping, and
redaction. AgentMemory.NET and MagiCore received an API-fit comparison only, per plan; neither was installed as a
dependency.

The selected retrieval split -- `CommunityToolkit.VectorData.PgVector` for candidate retrieval, plain Npgsql for
canonical transactional writes -- is proven, not merely asserted: scoped filtering, index-update-on-create, and
the transaction boundary between the two are each covered by a runnable test in
`tests/AgentExperience.CompatibilityProof/PostgresVectorProof.cs`. No optional engine became a mandatory
dependency without demonstrated fit; every unsupported capability found along the way (no reindex/VACUUM API, no
built-in context-provider timeout, no `Redactor` reject outcome, no ambient-transaction support in the vector
connector) is recorded above as an explicit limitation, not built as new framework infrastructure. This evidence
gates the adapter-facing stories listed in `epic-1-context.md` (1.4 on the evaluation-and-redaction track; 1.6 on
the MAF-hooks track; Epic 2's 2.1, 2.3, 2.6 on this story's integration paths generally) -- it does not gate the
portable contract scaffold from Story 1.1, which predates and does not depend on this story.
