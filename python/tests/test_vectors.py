"""
Story 23.2: semantic retrieval in Python (contract 1.6). The vector fixture families through the PostgreSQL store (and
the in-memory store where a case needs nothing only a database does), and the agent: every row of the fallback matrix,
the floor, the background embedding write, and a record embedded by the contract's own SQL found by a Python agent.
"""

from __future__ import annotations

import asyncio
import math
import time
import uuid
from collections.abc import Sequence
from dataclasses import replace
from typing import Any

import psycopg
import pytest
from _contract import FIXTURES, cases, json_difference, read_json, scope_of

pytest.importorskip("agent_framework")

from _maf import ScriptedClient, delivered
from _postgres import OWNER, PASSWORD, Server, application_conninfo, create_database
from _sync import run
from agent_framework import Agent, tool
from test_postgres import _erase

from agentexperience import (
    AuthorizationBounds,
    EmbeddingWrite,
    ExperienceRecord,
    Instant,
    Scope,
    SearchParameters,
    VectorQuery,
    content_hash,
    from_stored,
    retrieval_summary,
)
from agentexperience.embedding import record_summary, relevance_of
from agentexperience.inmemory import EmbeddingIndex, InMemoryExperienceStore
from agentexperience.maf import (
    CaptureFailure,
    ExperienceContextProvider,
    ExperienceIdentity,
    ExperienceSettings,
    InjectionFailure,
    InjectionResult,
    VectorFallback,
    tool_succeeded,
    use_agent_experience,
)
from agentexperience.postgres import (
    DECLARE_BOUNDS_SQL,
    PostgresExperienceStore,
    contract_sql,
    contract_statements,
    to_driver_placeholders,
)

TOLERANCE = 1e-9
SCOPE = Scope("contract-tenant", "contract-app", "contract-project", agent_id="refund-agent")
BOUNDS = AuthorizationBounds.of(SCOPE)
MODEL = "contract-embedding"
PARAPHRASE = "Renew the cryptographic credentials immediately"
NOW = Instant.parse("2026-03-02T10:00:00+00:00")


def seed_records(family: str, name: str) -> list[ExperienceRecord]:
    return [from_stored(item) for item in read_json(FIXTURES / family / name / "seed.json")]


def write_of(step: dict[str, Any]) -> EmbeddingWrite:
    return EmbeddingWrite(
        uuid.UUID(step["experienceId"]),
        scope_of(step["scope"]),
        step["modelId"],
        step["dimension"],
        step["contentHash"],
        step["sourceRevision"],
        tuple(float(c) for c in step["vector"]),
        Instant.parse(step["now"]),
    )


def query_of(step: dict[str, Any]) -> VectorQuery:
    return VectorQuery(
        scope_of(step["scope"]),
        step["modelId"],
        tuple(float(c) for c in step["vector"]),
        tuple(step["statuses"]),
        step["minConfidence"],
        step["limit"],
        step["excludeModelAuthored"],
    )


def search_outcome(result: Any) -> dict[str, Any]:
    return {
        "outcome": result.outcome,
        "rows": [
            {
                "experienceId": str(hit.record.experience_id),
                "distance": None if math.isnan(hit.distance) else hit.distance,
                "relevance": hit.relevance,
            }
            for hit in result.hits
        ],
    }


def write_outcome(result: Any) -> dict[str, Any]:
    return {"outcome": result.outcome} | (
        {"currentRevision": result.current_revision} if result.outcome == "Stale" else {}
    )


# ---- The fixtures through the PostgreSQL store ---------------------------------------------


def _set_revision(owner: str, step: dict[str, Any]) -> None:
    with psycopg.connect(owner) as connection:
        connection.execute(
            "UPDATE agent_experience.experience_records SET revision = %s WHERE experience_id = %s::uuid",
            (step["revision"], step["experienceId"]),
        )


def run_steps_on_postgres(pg_server: Server, family: str, name: str) -> tuple[list[Any], str]:
    directory = FIXTURES / family / name
    steps = read_json(directory / "steps.json")["steps"]
    database = create_database(pg_server, f"{family}_{name}")
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    for record in seed_records(family, name):
        store.insert_record(AuthorizationBounds.of(record.scope), record)
    actual: list[Any] = []
    for step in steps:
        if "write" in step:
            write = write_of(step["write"])
            actual.append(write_outcome(store.write_embedding(AuthorizationBounds.of(write.scope), write)))
        elif "search" in step:
            query = query_of(step["search"])
            actual.append(search_outcome(store.search_vectors(AuthorizationBounds.of(query.scope), query)))
        elif "setRevision" in step:
            _set_revision(owner, step["setRevision"])
            actual.append(None)
        else:
            _erase(owner, step["erase"])
            actual.append(None)
    return actual, owner


def stored_embeddings(owner: str, columns: Sequence[str]) -> list[dict[str, Any]]:
    with psycopg.connect(owner) as connection:
        connection.execute("SET TIME ZONE 'UTC'")
        rows = connection.execute(
            "SELECT " + ", ".join(f"{c}::text" for c in columns) + " FROM agent_experience.experience_embeddings "
            "ORDER BY experience_id"
        ).fetchall()
    return [dict(zip(columns, row, strict=True)) for row in rows]


@pytest.mark.postgres
@pytest.mark.parametrize("name", cases("embedding-write"))
def test_embedding_write(pg_server: Server, name: str) -> None:
    expected = read_json(FIXTURES / "embedding-write" / name / "expected.json")
    actual, owner = run_steps_on_postgres(pg_server, "embedding-write", name)
    assert json_difference(expected["steps"], actual, "$.steps", TOLERANCE) is None, actual
    columns = list(expected["experience_embeddings"][0]) if expected["experience_embeddings"] else ["experience_id"]
    rows = stored_embeddings(owner, columns)
    assert json_difference(expected["experience_embeddings"], rows, "$.experience_embeddings") is None, rows


@pytest.mark.postgres
@pytest.mark.parametrize("name", cases("vector-search"))
def test_vector_search(pg_server: Server, name: str) -> None:
    expected = read_json(FIXTURES / "vector-search" / name / "expected.json")
    actual, owner = run_steps_on_postgres(pg_server, "vector-search", name)
    assert json_difference(expected["steps"], actual, "$.steps", TOLERANCE) is None, actual
    columns = list(expected["experience_embeddings"][0]) if expected["experience_embeddings"] else ["experience_id"]
    rows = stored_embeddings(owner, columns)
    assert json_difference(expected["experience_embeddings"], rows, "$.experience_embeddings") is None, rows


@pytest.mark.parametrize(("family", "name"), [(f, n) for f in ("embedding-write", "vector-search") for n in cases(f)])
def test_the_in_memory_store_gives_the_fixtures_outcomes(family: str, name: str) -> None:
    steps = read_json(FIXTURES / family / name / "steps.json")["steps"]
    if any("erase" in step or "setRevision" in step for step in steps):
        pytest.skip("needs a database: an erasure or a lifecycle commit")
    expected = read_json(FIXTURES / family / name / "expected.json")["steps"]
    store = InMemoryExperienceStore(seed_records(family, name))
    for step, outcome in zip(steps, expected, strict=True):
        if "write" in step:
            write = write_of(step["write"])
            assert write_outcome(store.write_embedding(AuthorizationBounds.of(write.scope), write)) == outcome
        else:
            query = query_of(step["search"])
            actual = search_outcome(store.search_vectors(AuthorizationBounds.of(query.scope), query))
            assert json_difference(outcome, actual, "$", 1e-6) is None, actual


def test_the_in_memory_store_never_replaces_a_newer_revision_with_an_older_one() -> None:
    records = [replace(record, revision=2) for record in seed_records("vector-search", "nearest-first")][:1]
    store = InMemoryExperienceStore(records)
    experience_id = records[0].experience_id
    base = EmbeddingWrite(experience_id, SCOPE, MODEL, 2, "h2", 2, (0.0, 1.0), NOW)
    assert store.write_embedding(BOUNDS, base).outcome == "Written"
    older = replace(base, source_revision=1, content_hash="h1", vector=(1.0, 0.0))
    stale = store.write_embedding(BOUNDS, older)
    assert (stale.outcome, stale.current_revision) == ("Stale", 2)
    assert store.embedding(experience_id) == (MODEL, 2, "h2", 2, (0.0, 1.0))
    other = replace(base, scope=replace(SCOPE, agent_id="other"))
    assert store.write_embedding(AuthorizationBounds.of(other.scope), other).outcome == "Missing"


def test_the_in_memory_store_compares_32_bit_floats_and_orders_ties_by_id() -> None:
    records = seed_records("vector-search", "an-exact-tie-is-ordered-by-id")
    store = InMemoryExperienceStore(records)
    for record, vector in zip(
        sorted(records, key=lambda r: str(r.experience_id)), ((1.0, 0, 0, 0), (2.0, 0, 0, 0)), strict=False
    ):
        store.write_embedding(
            BOUNDS, EmbeddingWrite(record.experience_id, SCOPE, MODEL, 4, "h", record.revision, vector, NOW)
        )
    result = store.search_vectors(BOUNDS, VectorQuery(SCOPE, MODEL, (1.0, 0.0, 0.0, 0.0)))
    assert [str(hit.record.experience_id) for hit in result.hits] == sorted(
        str(h.record.experience_id) for h in result.hits
    )
    assert relevance_of(float("nan")) == 0 and relevance_of(-1) == 1 and relevance_of(3) == 0
    # Outside the bounds nothing is found and nothing is written.
    narrow = AuthorizationBounds.of(replace(SCOPE, agent_id="someone-else"))
    assert store.search_vectors(narrow, VectorQuery(SCOPE, MODEL, (1.0, 0.0, 0.0, 0.0))).hits == ()


# ---- Validation: refused before any statement ---------------------------------------------------------------------


@pytest.mark.parametrize(
    "build",
    [
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, " ", 2, "h", 1, (1.0, 0.0), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m" * 257, 2, "h", 1, (1.0, 0.0), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m", 3, "h", 1, (1.0, 0.0), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m", 0, "h", 1, (), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m", 2, "h", 1, (float("nan"), 0.0), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m", 2, "h", 1, (float("inf"), 0.0), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m", 2, " ", 1, (1.0, 0.0), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m", 2, "h", -1, (1.0, 0.0), NOW),
        lambda: EmbeddingWrite(uuid.uuid4(), SCOPE, "m", 16_001, "h", 1, (0.0,) * 16_001, NOW),
        lambda: VectorQuery(SCOPE, "", (1.0,)),
        lambda: VectorQuery(SCOPE, "m", ()),
        lambda: VectorQuery(SCOPE, "m", (float("nan"),)),
        lambda: VectorQuery(SCOPE, "m", (1e300,)),
    ],
)
def test_an_invalid_write_or_search_is_refused(build: Any) -> None:
    with pytest.raises(ValueError):
        build()


def test_a_zero_magnitude_vector_is_valid() -> None:
    assert VectorQuery(SCOPE, "m", (0.0, 0.0)).vector == (0.0, 0.0)


# ---- The agent ------------------------------------------------------------------------------------------------------


class FixedEmbedder:
    """The caller's embedder: one vector for every text, unless it is told to misbehave."""

    def __init__(
        self,
        vector: Sequence[float] = (1.0, 0.0, 0.0, 0.0),
        *,
        model_id: str = MODEL,
        dimension: int | None = None,
        fail: bool = False,
        delay: float = 0.0,
    ) -> None:
        self.vector = list(vector)
        self.model_id = model_id
        self.dimension = len(self.vector) if dimension is None else dimension
        self.fail = fail
        self.delay = delay
        self.texts: list[str] = []

    def embed(self, text: str) -> list[float]:
        self.texts.append(text)
        if self.delay:
            time.sleep(self.delay)
        if self.fail:
            raise RuntimeError("the provider is down: secret-record-content")
        return list(self.vector)


class AsyncEmbedder(FixedEmbedder):
    async def embed(self, text: str) -> list[float]:  # type: ignore[override]
        self.texts.append(text)
        await asyncio.sleep(0)
        return list(self.vector)


class Spy(InMemoryExperienceStore):
    """An in-memory store that counts the vector statements it is asked to run, and can be made to fail them."""

    def __init__(self, *args: Any, search_error: bool = False, search_delay: float = 0.0, **kwargs: Any) -> None:
        super().__init__(*args, **kwargs)
        self.vector_searches = 0
        self.search_error = search_error
        self.search_delay = search_delay

    def search_vectors(self, bounds: AuthorizationBounds, query: VectorQuery) -> Any:
        self.vector_searches += 1
        if self.search_delay:
            time.sleep(self.search_delay)
        if self.search_error:
            raise RuntimeError("the index is down")
        return super().search_vectors(bounds, query)


class TextOnlyStore:
    """A store with the text methods only: no vector methods."""

    def __init__(self, inner: InMemoryExperienceStore) -> None:
        self.inner = inner

    def insert_finalized(self, bounds: AuthorizationBounds, finalized: Any, *, actor: str) -> None:
        self.inner.insert_finalized(bounds, finalized, actor=actor)

    def search(self, bounds: AuthorizationBounds, parameters: SearchParameters) -> Any:
        return self.inner.search(bounds, parameters)


class Seen:
    def __init__(self) -> None:
        self.injections: list[InjectionResult] = []
        self.fallbacks: list[VectorFallback] = []
        self.injection_failures: list[InjectionFailure] = []
        self.capture_failures: list[CaptureFailure] = []
        self.client = ScriptedClient(calls=0)


def make_agent(store: Any, seen: Seen, **options: Any) -> Agent:
    return use_agent_experience(
        Agent(client=seen.client, name="refund-agent"),
        store=store,
        identity=ExperienceIdentity("python-principal", SCOPE),
        on_injected=delivered(seen.injections.append),
        on_vector_fallback=seen.fallbacks.append,
        on_injection_failure=seen.injection_failures.append,
        on_capture_failure=seen.capture_failures.append,
        **options,
    )


def embedded_store(cls: type[InMemoryExperienceStore] = InMemoryExperienceStore, **kwargs: Any) -> Any:
    """Three 'rotate-keys' records, embedded as the nearest-first fixture does: 3 is the query's own vector."""
    records = seed_records("vector-search", "nearest-first")
    store = cls(records, **kwargs)
    vectors = {1: (1.0, 1.0, 0.0, 0.0), 2: (1.0, 0.1, 0.0, 0.0), 3: (1.0, 0.0, 0.0, 0.0)}
    for record in records:
        number = int(str(record.experience_id)[-1])
        store.write_embedding(
            BOUNDS, EmbeddingWrite(record.experience_id, SCOPE, MODEL, 4, "h", record.revision, vectors[number], NOW)
        )
    return store


def ids(result: InjectionResult) -> list[int]:
    return [int(str(experience_id)[-1]) for experience_id in result.experience_ids]


def test_only_the_vector_finds_a_reworded_query_and_without_an_embedder_it_does_not() -> None:
    with_embedder = Seen()
    run(make_agent(embedded_store(), with_embedder, embedder=FixedEmbedder()).run(PARAPHRASE))
    assert with_embedder.fallbacks == [] and with_embedder.injection_failures == []
    assert ids(with_embedder.injections[0]) == [3, 2, 1]
    assert with_embedder.injections[0].vector_fallback is None
    assert "--- RECORD 1: rotate-keys ---" in with_embedder.client.blocks()[0]

    without = Seen()
    run(make_agent(embedded_store(), without).run(PARAPHRASE))
    assert without.injections == [] and without.client.blocks() == []
    assert [fallback.reason for fallback in without.fallbacks] == ["NotConfigured"]


def test_a_text_match_and_a_vector_match_are_one_entry() -> None:
    seen = Seen()
    run(make_agent(embedded_store(), seen, embedder=FixedEmbedder()).run("Rotate the signing keys for the service"))
    assert sorted(ids(seen.injections[0])) == [1, 2, 3]


def test_an_async_embedder_works() -> None:
    seen = Seen()
    run(make_agent(embedded_store(), seen, embedder=AsyncEmbedder()).run(PARAPHRASE))
    assert ids(seen.injections[0]) == [3, 2, 1]


def test_a_store_without_vector_methods_answers_from_text_and_reports_not_configured() -> None:
    inner = embedded_store()
    store = TextOnlyStore(inner)
    assert not isinstance(store, EmbeddingIndex)
    embedder = FixedEmbedder()
    seen = Seen()
    run(make_agent(store, seen, embedder=embedder).run("Rotate the signing keys for the service"))
    assert [fallback.reason for fallback in seen.fallbacks] == ["NotConfigured"]
    assert sorted(ids(seen.injections[0])) == [1, 2, 3]
    assert seen.injections[0].vector_fallback == seen.fallbacks[0]
    assert embedder.texts == []


@pytest.mark.parametrize(
    "embedder",
    [
        FixedEmbedder(fail=True),
        FixedEmbedder(delay=0.4),
        FixedEmbedder(model_id=" "),
        FixedEmbedder(vector=[]),
        FixedEmbedder(dimension=3),
        FixedEmbedder(vector=[1.0, float("nan"), 0.0, 0.0]),
        FixedEmbedder(vector=[1.0, float("inf"), 0.0, 0.0]),
        FixedEmbedder(model_id="m" * 300),
    ],
    ids=["raises", "times-out", "blank-model", "empty", "wrong-length", "nan", "infinite", "long-model"],
)
def test_a_bad_embedder_answers_from_text_and_no_statement_runs(embedder: FixedEmbedder) -> None:
    store = embedded_store(Spy)
    seen = Seen()
    run(make_agent(store, seen, embedder=embedder, vector_timeout=0.1).run("Rotate the signing keys for the service"))
    assert [fallback.reason for fallback in seen.fallbacks] == ["ProviderUnavailable"]
    assert "secret-record-content" not in seen.fallbacks[0].detail
    assert store.vector_searches == 0
    assert seen.injection_failures == []
    assert sorted(ids(seen.injections[0])) == [1, 2, 3]


def test_a_mismatch_answers_from_text_and_says_which() -> None:
    seen = Seen()
    run(make_agent(embedded_store(), seen, embedder=FixedEmbedder(model_id="another-model")).run(PARAPHRASE))
    assert [fallback.reason for fallback in seen.fallbacks] == ["ModelMismatch"]
    assert seen.injections == []

    seen = Seen()
    run(make_agent(embedded_store(), seen, embedder=FixedEmbedder(vector=[1.0, 0.0, 0.0])).run(PARAPHRASE))
    assert [fallback.reason for fallback in seen.fallbacks] == ["DimensionMismatch"]


@pytest.mark.parametrize("kind", ["raises", "times-out"])
def test_a_failing_search_answers_from_text_and_reports_it(kind: str) -> None:
    store = embedded_store(Spy, search_error=kind == "raises", search_delay=0.5 if kind == "times-out" else 0.0)
    seen = Seen()
    run(
        make_agent(store, seen, embedder=FixedEmbedder(), vector_timeout=0.1).run(
            "Rotate the signing keys for the service"
        )
    )
    assert [fallback.reason for fallback in seen.fallbacks] == ["VectorSearchFailed"]
    assert seen.injection_failures == []
    assert sorted(ids(seen.injections[0])) == [1, 2, 3]


def test_a_failing_text_search_fails_the_injection_whatever_the_vector_found() -> None:
    class Broken(InMemoryExperienceStore):
        def search(self, bounds: AuthorizationBounds, parameters: SearchParameters) -> Any:
            raise RuntimeError("the text index is down")

    store = embedded_store(Broken)
    seen = Seen()
    run(make_agent(store, seen, embedder=FixedEmbedder()).run(PARAPHRASE))
    assert [failure.reason for failure in seen.injection_failures] == [
        "Injection raised RuntimeError; no block was added."
    ]
    assert seen.injections == [] and seen.client.blocks() == []


def test_the_floor_drops_weak_vector_candidates_and_never_a_text_one() -> None:
    seen = Seen()
    run(make_agent(embedded_store(), seen, embedder=FixedEmbedder(), min_vector_relevance=0.9).run(PARAPHRASE))
    assert ids(seen.injections[0]) == [3, 2]  # record 1's relevance is about 0.85

    # A text match is kept although its vector is below the floor.
    seen = Seen()
    run(
        make_agent(
            embedded_store(), seen, embedder=FixedEmbedder(vector=[0.0, 0.0, 0.0, 1.0]), min_vector_relevance=1.0
        ).run("Rotate the signing keys for the service")
    )
    assert sorted(ids(seen.injections[0])) == [1, 2, 3]


def test_settings_refuse_a_bad_embedder_floor_or_timeout() -> None:
    base: dict[str, Any] = {"store": InMemoryExperienceStore(), "identity": ExperienceIdentity("p", SCOPE)}
    with pytest.raises(TypeError):
        ExperienceSettings(**base, embedder=object())  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        ExperienceSettings(**base, min_vector_relevance=1.5)
    with pytest.raises(ValueError):
        ExperienceSettings(**base, vector_timeout=0)


# ---- The background write after a record is stored ---------------------------------------------------------------


def stored_agent(store: Any, seen: Seen, embedder: Any, **options: Any) -> Agent:
    @tool
    def run_tests(suite: str) -> str:
        """Runs a test suite."""
        return "ok"

    seen.client = ScriptedClient(calls=1, arguments={"suite": "unit"})
    return use_agent_experience(
        Agent(client=seen.client, name="refund-agent", tools=[run_tests]),
        store=store,
        identity=ExperienceIdentity("python-principal", SCOPE),
        verify=tool_succeeded("run_tests", artifact_revision="build-1"),
        on_capture_failure=seen.capture_failures.append,
        embedder=embedder,
        **options,
    )


def settings_of(agent: Agent) -> ExperienceSettings:
    provider = next(p for p in agent.context_providers if isinstance(p, ExperienceContextProvider))
    return provider.settings


def test_a_stored_record_is_embedded_in_the_background_from_its_summary() -> None:
    store = InMemoryExperienceStore()
    seen = Seen()
    embedder = FixedEmbedder()
    agent = stored_agent(store, seen, embedder)

    async def go() -> None:
        await agent.run("Refund ticket 43 is stuck in pending")
        await settings_of(agent).wait_for_index_writes()

    run(go())
    (record,) = store.records()
    stored = store.embedding(record.experience_id)
    assert stored is not None
    summary = record_summary(record)
    assert summary == retrieval_summary(
        record.task_id, record.task_summary, record.reflection.lesson if record.reflection else None
    )
    assert embedder.texts[-1] == summary  # after the query's own embedding
    assert stored == (MODEL, 4, content_hash(MODEL, summary), record.revision, (1.0, 0.0, 0.0, 0.0))
    assert seen.capture_failures == []


@pytest.mark.parametrize(
    "embedder", [FixedEmbedder(fail=True), FixedEmbedder(dimension=9)], ids=["raises", "wrong-length"]
)
def test_a_failed_embedding_write_is_reported_as_index_and_the_record_stays_stored(embedder: FixedEmbedder) -> None:
    store = InMemoryExperienceStore()
    seen = Seen()
    agent = stored_agent(store, seen, embedder)

    async def go() -> None:
        await agent.run("Refund ticket 43 is stuck in pending")
        await settings_of(agent).wait_for_index_writes()

    run(go())
    assert len(store.records()) == 1
    assert [failure.stage for failure in seen.capture_failures] == ["Index"]
    assert "secret-record-content" not in seen.capture_failures[0].reason
    assert store.embedding(store.records()[0].experience_id) is None


def test_no_embedding_is_written_without_an_embedder_or_a_store_that_takes_it() -> None:
    store = InMemoryExperienceStore()
    seen = Seen()
    run(stored_agent(store, seen, None).run("Refund ticket 43 is stuck in pending"))
    assert store.embedding(store.records()[0].experience_id) is None

    inner = InMemoryExperienceStore()
    seen = Seen()
    embedder = FixedEmbedder()
    run(stored_agent(TextOnlyStore(inner), seen, embedder).run("Refund ticket 43 is stuck in pending"))
    assert len(inner.records()) == 1 and embedder.texts == [] and seen.capture_failures == []


def test_a_lesson_stored_and_embedded_is_found_by_a_reworded_query_later() -> None:
    store = InMemoryExperienceStore()
    seen = Seen()
    agent = stored_agent(store, seen, FixedEmbedder(), min_vector_relevance=0.5)

    async def go() -> None:
        await agent.run("Refund ticket 43 is stuck in pending")
        await settings_of(agent).wait_for_index_writes()

    run(go())
    later = Seen()
    run(make_agent(store, later, embedder=FixedEmbedder()).run("Zebra giraffe antelope"))
    assert len(later.injections) == 1 and later.fallbacks == []


# ---- Against PostgreSQL, with a record embedded by the contract's own SQL -----------------


@pytest.mark.postgres
def test_a_record_embedded_by_the_contracts_sql_is_found_by_a_python_agent(pg_server: Server) -> None:
    case = FIXTURES / "vector-search" / "nearest-first"
    steps = read_json(case / "steps.json")["steps"]
    database = create_database(pg_server, "agent_vector_nearest_first")
    conninfo = application_conninfo(pg_server, database)
    store = PostgresExperienceStore(conninfo)
    for record in seed_records("vector-search", "nearest-first"):
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    # The embeddings are written by the fixture's own statement, not by the Python store.
    write_sql = to_driver_placeholders(contract_statements("write-experience-embedding.sql")["write"])
    declare = to_driver_placeholders(contract_sql(DECLARE_BOUNDS_SQL))
    for step in (s["write"] for s in steps if "write" in s):
        scope = scope_of(step["scope"])
        with psycopg.connect(conninfo) as connection, connection.transaction():
            connection.execute(declare, AuthorizationBounds.of(scope).parameters())
            written = connection.execute(
                write_sql,
                {
                    "experience_id": uuid.UUID(step["experienceId"]),
                    **dict(
                        zip(
                            ("tenant_id", "application_id", "project_id", "team_id", "agent_id", "user_id"),
                            scope.fields(),
                            strict=True,
                        )
                    ),
                    "model_id": step["modelId"],
                    "dimension": step["dimension"],
                    "content_hash": step["contentHash"],
                    "source_revision": step["sourceRevision"],
                    "embedding": "[" + ",".join(str(c) for c in step["vector"]) + "]",
                    "now": Instant.parse(step["now"]).to_datetime(),
                },
            )
            assert written.rowcount == 1

    query = next(s["search"] for s in steps if "search" in s)
    seen = Seen()
    run(make_agent(store, seen, embedder=FixedEmbedder(query["vector"], model_id=query["modelId"])).run(PARAPHRASE))
    assert seen.fallbacks == [] and seen.injection_failures == []
    assert ids(seen.injections[0]) == [3, 2, 1]

    # Without the embedder the same query finds nothing, and says why.
    plain = Seen()
    run(make_agent(store, plain).run(PARAPHRASE))
    assert plain.injections == [] and [f.reason for f in plain.fallbacks] == ["NotConfigured"]


@pytest.mark.postgres
def test_a_python_agent_stores_a_record_and_its_embedding_in_postgres(pg_server: Server) -> None:
    database = create_database(pg_server, "agent_vector_roundtrip")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    seen = Seen()
    agent = stored_agent(store, seen, FixedEmbedder())

    async def go() -> None:
        await agent.run("Refund ticket 43 is stuck in pending")
        await settings_of(agent).wait_for_index_writes()

    run(go())
    assert seen.capture_failures == []
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    rows = stored_embeddings(owner, ["model_id", "dimension", "source_revision", "embedding", "content_hash"])
    assert len(rows) == 1
    assert rows[0]["model_id"] == MODEL and rows[0]["embedding"] == "[1,0,0,0]"

    later = Seen()
    run(make_agent(store, later, embedder=FixedEmbedder()).run("Zebra giraffe antelope"))
    assert len(later.injections) == 1 and later.fallbacks == []


def test_a_lone_surrogate_hashes_as_the_replacement_character() -> None:
    from agentexperience.embedding import content_hash

    assert content_hash("m", "a\ud800b") == content_hash("m", "a�b")


def test_hung_vector_calls_fail_fast_and_never_take_the_default_executor() -> None:
    import asyncio
    import threading

    from agentexperience import embedding as embedding_module
    from agentexperience.embedding import MAX_VECTOR_THREADS, run_on_vector_thread

    release = threading.Event()

    async def scenario() -> None:
        # Threads that earlier tests abandoned (a timed-out embedder) may still hold slots: wait for them to end.
        for _ in range(200):
            free = [embedding_module._vector_threads.acquire(blocking=False) for _ in range(MAX_VECTOR_THREADS)]
            for taken in free:
                if taken:
                    embedding_module._vector_threads.release()
            if all(free):
                break
            await asyncio.sleep(0.05)
        hung = [asyncio.ensure_future(run_on_vector_thread(release.wait)) for _ in range(MAX_VECTOR_THREADS)]
        await asyncio.sleep(0.05)
        with pytest.raises(RuntimeError):
            await run_on_vector_thread(lambda: 1)
        assert await asyncio.to_thread(lambda: 2) == 2  # the default executor is untouched
        release.set()
        await asyncio.gather(*hung)
        assert await run_on_vector_thread(lambda: 3) == 3

    asyncio.run(scenario())
