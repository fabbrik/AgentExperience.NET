"""
Contract 1.11: measuring another memory provider, without the agent integration. The ``provider-holdout-assign``
fixture (no database), the ``provider-holdout-record`` fixtures through the in-memory store and, in the PostgreSQL leg,
through the PostgreSQL store (the packaged ``record-provider-holdout-assignment.sql``), the
``provider-benefit-estimate`` fixtures (pure, paged and through a store), and ``estimate_provider_benefit``'s edges: a
store that does not record provider holdout assignments, a foreign scope, a malformed key, invalid options, a sealed
run record, paging, and a schema without the 1.11 grants.
"""

from __future__ import annotations

import dataclasses
import datetime as dt
import functools
import json
import uuid
from typing import Any

import pytest
from _benefit import TOLERANCE
from _contract import FIXTURES, cases, json_difference, read_json, scope_of
from _postgres import OWNER, PASSWORD, Server, application_conninfo, create_database
from test_holdout import DATABASE_CLOCK, _clock, _erase, _erase_during, postgres_text

from agentexperience import (
    AuthorizationBounds,
    BenefitAccumulator,
    BenefitOptions,
    HoldoutOptions,
    Instant,
    ProviderHoldoutAssignment,
    ProviderHoldoutOutcomeRow,
    Scope,
    assign_holdout,
    assign_provider_holdout,
    estimate_provider,
    estimate_provider_benefit,
    from_stored,
    holdout_fraction,
    is_valid_provider_key,
    provider_salt,
)
from agentexperience.benefit import add_provider_rows, provider_estimate_to_json
from agentexperience.holdout import MAX_PROVIDER_KEY_LENGTH
from agentexperience.inmemory import (
    AuthorizationDenied,
    InMemoryExperienceStore,
    ProviderHoldoutRecorder,
    ProviderHoldoutRow,
)

PROVIDER_COLUMNS = (
    "run_id",
    "provider_key",
    "arm",
    "tenant_id",
    "application_id",
    "project_id",
    "team_id",
    "agent_id",
    "user_id",
    "assigned_at",
)
SCOPE = Scope("provider-tenant", "provider-app", "provider-project", agent_id="provider-agent")
AT = Instant.parse("2026-03-02T09:00:00+00:00")


# ---- provider-holdout-assign ---------------------------------------------------------------------------------------

ASSIGN = read_json(FIXTURES / "provider-holdout-assign" / "cases.json")["cases"]


@pytest.mark.parametrize("index", range(len(ASSIGN)))
def test_provider_holdout_assign(index: int) -> None:
    case = ASSIGN[index]
    run_id = uuid.UUID(case["runId"])
    salt = provider_salt(case["providerKey"], case["salt"])
    assert salt == case["providerSalt"]
    fraction = holdout_fraction(run_id, salt)
    assert f"{fraction:.15g}" == f"{case['fraction']:.15g}"
    for arm in case["arms"]:
        assert assign_holdout(run_id, salt, arm["rate"]) == arm["arm"], arm
        if case["validKey"]:
            options = HoldoutOptions(rate=arm["rate"], salt=case["salt"])
            assert assign_provider_holdout(run_id, case["providerKey"], options) == arm["arm"], arm
    assert is_valid_provider_key(case["providerKey"]) is case["validKey"]


@pytest.mark.parametrize(
    ("key", "valid"),
    [
        ("mem0", True),
        ("Letta.v2_prod-1", True),
        ("k" * MAX_PROVIDER_KEY_LENGTH, True),
        ("k" * (MAX_PROVIDER_KEY_LENGTH + 1), False),
        ("", False),
        ("a b", False),
        ("a/b", False),
        ("a:b", False),
        ("mém0", False),  # a letter, but not an ASCII one
        ("mem٠", False),  # Arabic-Indic digit zero: a digit, but not an ASCII one
        ("mem0\n", False),
        (None, False),
        (7, False),
    ],
)
def test_the_key_check(key: Any, valid: bool) -> None:
    assert is_valid_provider_key(key) is valid


def test_the_provider_salt_counts_utf16_code_units() -> None:
    # A key is ASCII, so this only shows the derivation is the .NET one for any string.
    assert provider_salt("🧪", "s") == "2:🧪/s"


# ---- provider-holdout-record ---------------------------------------------------------------------------------------


def _assignment(record: dict[str, Any]) -> ProviderHoldoutAssignment:
    return ProviderHoldoutAssignment(
        uuid.UUID(record["runId"]), record["providerKey"], record["arm"], Instant.parse(record["assignedAt"])
    )


def _record_in(store: ProviderHoldoutRecorder, record: dict[str, Any]) -> None:
    scope = scope_of(record["scope"])
    store.record_provider_holdout_assignment(AuthorizationBounds.of(scope), scope, _assignment(record))


def _memory_rows(rows: list[ProviderHoldoutRow], clock: Instant) -> list[dict[str, Any]]:
    return [
        {
            "run_id": str(row.run_id),
            "provider_key": row.provider_key,
            "arm": row.arm,
            "tenant_id": row.scope.tenant_id,
            "application_id": row.scope.application_id,
            "project_id": row.scope.project_id,
            "team_id": row.scope.team_id,
            "agent_id": row.scope.agent_id,
            "user_id": row.scope.user_id,
            "assigned_at": DATABASE_CLOCK if row.assigned_at == clock else postgres_text(row.assigned_at),
        }
        for row in rows
    ]


@pytest.mark.parametrize("name", cases("provider-holdout-record"))
def test_provider_holdout_record_in_memory(name: str) -> None:
    """A provider-holdout-record fixture through the in-memory store: an ``eraseDuringRecord`` step is its erasure,
    then its record call, which the guard then refuses."""
    directory = FIXTURES / "provider-holdout-record" / name
    clock = Instant.parse("2027-01-01T00:00:00+00:00")
    store = InMemoryExperienceStore(
        (from_stored(stored) for stored in read_json(directory / "seed.json")["records"]), clock=lambda: clock
    )
    for step in read_json(directory / "steps.json")["steps"]:
        if "record" in step:
            _record_in(store, step["record"])
        elif "erase" in step:
            assert store.erase(uuid.UUID(step["erase"]["experienceId"]))
        else:
            race = step["eraseDuringRecord"]
            assert store.erase(uuid.UUID(race["erase"]["experienceId"]))
            _record_in(store, race["record"])
    actual = _memory_rows(store.provider_holdout_assignments(), clock)
    expected = read_json(directory / "expected.json")["experience_provider_holdout_assignments"]
    difference = json_difference(expected, actual, "$.experience_provider_holdout_assignments")
    assert difference is None, difference


def test_an_assignment_is_refused_outside_the_bounds_or_when_malformed() -> None:
    store = InMemoryExperienceStore()
    good = ProviderHoldoutAssignment(uuid.uuid4(), "mem0", "Holdout", AT)
    other = Scope("provider-tenant", "provider-app", "provider-project", agent_id="other")
    with pytest.raises(AuthorizationDenied):
        store.record_provider_holdout_assignment(AuthorizationBounds.of(other), SCOPE, good)
    bounds = AuthorizationBounds.of(SCOPE)
    for bad in (
        dataclasses.replace(good, run_id=uuid.UUID(int=0)),
        dataclasses.replace(good, provider_key="a/b"),
        dataclasses.replace(good, provider_key=""),
        dataclasses.replace(good, arm="Control"),  # type: ignore[arg-type]
    ):
        with pytest.raises(ValueError):
            store.record_provider_holdout_assignment(bounds, SCOPE, bad)
    assert store.provider_holdout_assignments() == []


def _pg_rows(owner: str, expected: Any, before: dt.datetime, after: dt.datetime) -> list[dict[str, Any]]:
    import psycopg

    with psycopg.connect(owner) as connection:
        connection.execute("SET TIME ZONE 'UTC'")
        rows = connection.execute(
            "SELECT "
            + ", ".join(f"{column}::text" for column in PROVIDER_COLUMNS)
            + ", assigned_at FROM agent_experience.experience_provider_holdout_assignments "
            "ORDER BY tenant_id, run_id, provider_key"
        ).fetchall()
    clamped = {
        (item["tenant_id"], item["run_id"], item["provider_key"])
        for item in expected
        if item["assigned_at"] == DATABASE_CLOCK
    }
    actual: list[dict[str, Any]] = []
    for row in rows:
        values = dict(zip(PROVIDER_COLUMNS, row[:-1], strict=True))
        if (values["tenant_id"], values["run_id"], values["provider_key"]) in clamped:
            assert before <= row[-1] <= after, (values, before, after)
            values["assigned_at"] = DATABASE_CLOCK
        actual.append(values)
    return actual


@pytest.mark.postgres
@pytest.mark.parametrize("name", cases("provider-holdout-record"))
def test_provider_holdout_record_in_postgresql(pg_server: Server, name: str) -> None:
    """A provider-holdout-record fixture, each record step through the store's record call (the packaged statement)."""
    from agentexperience.postgres import PostgresExperienceStore

    directory = FIXTURES / "provider-holdout-record" / name
    database = create_database(pg_server, "provider_holdout_" + name)
    owner = pg_server.conninfo(database, OWNER, PASSWORD)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    for stored in read_json(directory / "seed.json")["records"]:
        record = from_stored(stored)
        store.insert_record(AuthorizationBounds.of(record.scope), record)

    before = _clock(owner)
    for step in read_json(directory / "steps.json")["steps"]:
        if "record" in step:
            _record_in(store, step["record"])
        elif "erase" in step:
            _erase(owner, step["erase"])
        else:
            race = step["eraseDuringRecord"]
            _erase_during(owner, race["erase"], functools.partial(_record_in, store, race["record"]))
    after = _clock(owner)
    expected = read_json(directory / "expected.json")["experience_provider_holdout_assignments"]
    difference = json_difference(
        expected, _pg_rows(owner, expected, before, after), "$.experience_provider_holdout_assignments"
    )
    assert difference is None, difference


# ---- provider-benefit-estimate -------------------------------------------------------------------------------------


def _provider_row(value: dict[str, Any]) -> ProviderHoldoutOutcomeRow:
    return ProviderHoldoutOutcomeRow(
        uuid.UUID(value["runId"]),
        value["providerKey"],
        value["arm"],
        Instant.parse(value["assignedAt"]),
        value["runOutcome"],
    )


class PagedRows:
    """A store answering the estimate's pages from fixed rows, as a store pages them (by run)."""

    def __init__(self, rows: list[ProviderHoldoutOutcomeRow]) -> None:
        self.rows = sorted(rows, key=lambda row: row.run_id)
        self.reads: list[tuple[int, uuid.UUID | None]] = []

    def read_provider_holdout_outcomes(
        self, bounds: AuthorizationBounds, scope: Scope, provider_key: str, limit: int, after: uuid.UUID | None = None
    ) -> list[ProviderHoldoutOutcomeRow]:
        self.reads.append((limit, after))
        return [row for row in self.rows if row.provider_key == provider_key and (after is None or row.run_id > after)][
            :limit
        ]


@pytest.mark.parametrize("name", cases("provider-benefit-estimate"))
def test_provider_benefit_estimate(name: str) -> None:
    directory = FIXTURES / "provider-benefit-estimate" / name
    source = read_json(directory / "rows.json")
    rows = [_provider_row(row) for row in source["rows"]]
    options = BenefitOptions(read_json(directory / "options.json")["minimumRunsPerArm"])
    expected = read_json(directory / "expected.json")

    def check(actual: Any, how: str) -> None:
        value = provider_estimate_to_json(actual)
        assert value.pop("tracked") is True and value.pop("outcome") == "Found", how
        difference = json_difference(expected, json.loads(json.dumps(value)), tolerance=TOLERANCE)
        assert difference is None, f"{how}: {difference}"

    check(estimate_provider(rows, source["providerKey"], options), "pure")
    for size in (1, 3, 1000):
        accumulator = BenefitAccumulator(options)
        for start in range(0, len(rows), size):
            add_provider_rows(accumulator, rows[start : start + size])
        estimate_ = estimate_provider([], source["providerKey"], options)
        check(dataclasses.replace(estimate_, overall=accumulator.to_estimate().overall), f"paged by {size}")
    store = PagedRows(rows)
    read = estimate_provider_benefit(store, AuthorizationBounds.of(SCOPE), SCOPE, source["providerKey"], options)
    check(read, "read")
    assert store.reads == [(1000, None)]


def test_the_estimate_pages_through_every_run_a_thousand_at_a_time() -> None:
    rows = [
        ProviderHoldoutOutcomeRow(
            uuid.UUID(int=(n + 1) << 64), "mem0", "Treatment" if n % 2 else "Holdout", AT, "Verified" if n % 3 else None
        )
        for n in range(2500)
    ]
    store = PagedRows(rows)
    estimate_ = estimate_provider_benefit(store, AuthorizationBounds.of(SCOPE), SCOPE, "mem0")
    assert [after for _, after in store.reads] == [None, rows[999].run_id, rows[1999].run_id]
    overall = estimate_.overall
    assert overall.treatment.runs + overall.holdout.runs + overall.pending == 2500
    assert overall.pending == len([row for row in rows if row.run_outcome is None])


def test_a_store_that_does_not_record_provider_holdout_is_not_tracked() -> None:
    estimate_ = estimate_provider_benefit(object(), AuthorizationBounds.of(SCOPE), SCOPE, "mem0")
    assert not estimate_.tracked and estimate_.outcome == "Found"
    assert estimate_.provider_key == "mem0" and estimate_.overall.verdict == "NoEvidence"
    assert estimate_.minimum_runs_per_arm == 20


def test_a_scope_outside_the_authorization_is_denied_and_reads_nothing() -> None:
    store = PagedRows([])
    other = Scope("provider-tenant", "provider-app", "provider-project", agent_id="other-agent")
    estimate_ = estimate_provider_benefit(store, AuthorizationBounds.of(SCOPE), other, "mem0")
    assert estimate_.outcome == "Denied" and estimate_.tracked
    assert estimate_.overall.treatment.runs == 0
    assert store.reads == []


def test_the_key_is_checked_first_then_the_options() -> None:
    with pytest.raises(ValueError, match="provider key"):
        estimate_provider_benefit(object(), AuthorizationBounds.of(SCOPE), SCOPE, "a/b", "bad")  # type: ignore[arg-type]
    with pytest.raises(TypeError):
        estimate_provider_benefit(object(), AuthorizationBounds.of(SCOPE), SCOPE, "mem0", "bad")  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        estimate_provider_benefit(PagedRows([]), AuthorizationBounds.of(SCOPE), SCOPE, "")


def test_the_in_memory_read_joins_each_run_to_its_own_record_in_exactly_the_scope() -> None:
    stored = read_json(FIXTURES / "provider-holdout-record" / "erased-run" / "seed.json")["records"]
    records = [from_stored(item) for item in stored]
    store = InMemoryExperienceStore(records, clock=lambda: Instant.parse("2027-01-01T00:00:00+00:00"))
    record = records[0]
    own = min(
        (item for item in records if item.source_run_id == record.source_run_id and item.scope == record.scope),
        key=lambda item: (item.created_at, item.experience_id),
    )
    bounds = AuthorizationBounds.of(record.scope)
    other_run = uuid.UUID(int=1)
    for run_id, arm in ((record.source_run_id, "Holdout"), (other_run, "Treatment")):
        store.record_provider_holdout_assignment(
            bounds,
            record.scope,
            ProviderHoldoutAssignment(run_id, "mem0", arm, AT),  # type: ignore[arg-type]
        )
    store.record_provider_holdout_assignment(
        bounds, record.scope, ProviderHoldoutAssignment(other_run, "zep", "Holdout", AT)
    )
    rows = store.read_provider_holdout_outcomes(bounds, record.scope, "mem0", 1000)
    assert [(row.run_id, row.arm, row.run_outcome) for row in sorted(rows, key=lambda row: row.run_id)] == sorted(
        [(record.source_run_id, "Holdout", own.outcome.status), (other_run, "Treatment", None)]
    )
    first = store.read_provider_holdout_outcomes(bounds, record.scope, "mem0", 1)
    rest = store.read_provider_holdout_outcomes(bounds, record.scope, "mem0", 1000, first[-1].run_id)
    assert [row.run_id for row in first + rest] == sorted([record.source_run_id, other_run])
    with pytest.raises(AuthorizationDenied):
        store.read_provider_holdout_outcomes(AuthorizationBounds.of(SCOPE), record.scope, "mem0", 10)
    with pytest.raises(ValueError):
        store.read_provider_holdout_outcomes(bounds, record.scope, "mem0", 1001)


# ---- PostgreSQL: the read ------------------------------------------------------------------------------------------


@pytest.mark.postgres
def test_the_postgresql_read_pages_by_run_and_a_sealed_run_record_has_no_outcome(pg_server: Server) -> None:
    import psycopg

    from agentexperience.postgres import PostgresExperienceStore

    stored = read_json(FIXTURES / "provider-holdout-record" / "erased-run" / "seed.json")["records"]
    database = create_database(pg_server, "provider_outcomes_read")
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    record = from_stored(stored[0])
    bounds = AuthorizationBounds.of(record.scope)
    store.insert_record(bounds, record)
    sealed_run = uuid.UUID("000000ff-0000-4000-8000-000000000901")
    bare_run = uuid.UUID("000000ff-0000-4000-8000-000000000902")
    with psycopg.connect(pg_server.conninfo(database, OWNER, PASSWORD)) as owner, owner.transaction():
        # A run's own record, sealed as the .NET sealing writes one (payload_version 2, opaque payload).
        owner.execute(
            "INSERT INTO agent_experience.experience_records (experience_id, source_run_id, tenant_id, application_id, "
            "project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, "
            "contradictions, revision, created_at, updated_at, payload_version, payload, search_vector_sealed) "
            "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, '(sealed)', 'Validated', 0.9, 1, 0, 1, now(), now(), 2, "
            "%s::jsonb, to_tsvector('english', 'sealed'))",
            (uuid.uuid4(), sealed_run, *record.scope.fields(), '{"sealed": "aexp-sealed:v1:AAAA"}'),
        )
    for run_id, arm in ((record.source_run_id, "Treatment"), (sealed_run, "Holdout"), (bare_run, "Holdout")):
        store.record_provider_holdout_assignment(
            bounds,
            record.scope,
            ProviderHoldoutAssignment(run_id, "mem0", arm, AT),  # type: ignore[arg-type]
        )
    store.record_provider_holdout_assignment(
        bounds, record.scope, ProviderHoldoutAssignment(bare_run, "zep", "Treatment", AT)
    )
    everything = store.read_provider_holdout_outcomes(bounds, record.scope, "mem0", 1000)
    assert [(row.run_id, row.provider_key, row.arm, row.run_outcome) for row in everything] == sorted(
        [
            (record.source_run_id, "mem0", "Treatment", record.outcome.status),
            (sealed_run, "mem0", "Holdout", None),
            (bare_run, "mem0", "Holdout", None),
        ]
    )
    assert all(row.assigned_at == AT for row in everything)
    paged = store.read_provider_holdout_outcomes(bounds, record.scope, "mem0", 2)
    paged += store.read_provider_holdout_outcomes(bounds, record.scope, "mem0", 2, paged[-1].run_id)
    assert paged == everything
    estimate_ = estimate_provider_benefit(store, bounds, record.scope, "mem0")
    assert estimate_.outcome == "Found" and estimate_.tracked
    assert estimate_.overall.pending == 2 and estimate_.overall.treatment.runs == 1
    with pytest.raises(AuthorizationDenied):
        store.read_provider_holdout_outcomes(AuthorizationBounds.of(SCOPE), record.scope, "mem0", 10)


@pytest.mark.postgres
def test_without_the_1_11_grants_recording_and_reading_fail_and_write_nothing(pg_server: Server) -> None:
    import psycopg
    from _postgres import APPLICATION_ROLE_PRIVILEGES_1_8

    from agentexperience.postgres import PostgresExperienceStore

    database = create_database(pg_server, "provider_holdout_no_grant", privileges=APPLICATION_ROLE_PRIVILEGES_1_8)
    store = PostgresExperienceStore(application_conninfo(pg_server, database))
    bounds = AuthorizationBounds.of(SCOPE)
    with pytest.raises(psycopg.errors.InsufficientPrivilege):
        store.record_provider_holdout_assignment(
            bounds, SCOPE, ProviderHoldoutAssignment(uuid.uuid4(), "mem0", "Holdout", AT)
        )
    with pytest.raises(psycopg.errors.InsufficientPrivilege):
        estimate_provider_benefit(store, bounds, SCOPE, "mem0")
    with psycopg.connect(pg_server.conninfo(database)) as connection:
        count = connection.execute(
            "SELECT count(*) FROM agent_experience.experience_provider_holdout_assignments"
        ).fetchone()
    assert count == (0,)
