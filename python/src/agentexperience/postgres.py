"""
The PostgreSQL store (the ``postgres`` extra): writes finalized records and their initial event, reads records back,
runs the contract's text search, and records injections, all as the application role inside a transaction that first
declared its bounds.

It runs against a schema the .NET migrator applied (``ExperienceSchemaMigrator.MigrateAsync``, then
``ApplyApplicationRolePrivilegesAsync`` with row-level security on, or the contract's two privileges files) and never
creates or alters a schema object. The bounds, the search and the injection record are the contract's SQL files,
shipped as package data and run as written: only their ``@name`` placeholders are spelled the way the driver spells
them.
"""

from __future__ import annotations

import datetime as _dt
import functools
import json
import re
import uuid
from collections.abc import Callable, Iterator, Sequence
from contextlib import contextmanager
from importlib import resources
from typing import TYPE_CHECKING, Any

from ._text import is_null_or_whitespace
from .confidence import ConfidenceUpdate
from .embedding import (
    EmbeddingWrite,
    EmbeddingWriteResult,
    VectorHit,
    VectorQuery,
    VectorSearchResult,
    order_hits,
    vector_literal,
)
from .encoding import Instant
from .finalize import FinalizedExperience
from .holdout import (
    HoldoutAssignment,
    HoldoutOutcomeCursor,
    HoldoutOutcomeRow,
    ProviderHoldoutAssignment,
    ProviderHoldoutOutcomeRow,
    check_outcome_limit,
    check_provider_assignment,
    check_provider_key,
    holdout_records,
)
from .inmemory import (
    AuthorizationDenied,
    InjectionSummary,
    LessonRow,
    LessonsRead,
    check_lessons_read,
    injection_ids,
    permits,
)
from .lifecycle import (
    AuthorizationContext,
    HistoryQuery,
    HistoryResult,
    LifecycleCommitResult,
    LifecycleEvent,
    RecordReadResult,
    StoredLifecycleEvent,
    StoreValidationError,
    SupersessionCheckResult,
    as_replay_of,
    assessment_spent_error,
    is_same_evidence,
    validate_get,
    validate_history_query,
    validate_lifecycle_event,
    validate_supersession_check,
)
from .model import model_of_metadata
from .records import COLUMN_NAMES, PAYLOAD_VERSION, ExperienceRecord, Scope, from_columns, to_payload
from .reuse_feedback import (
    ExperienceReuseExposure,
    ExperienceReuseFeedbackStoreResult,
    RecordedExperienceReuseFeedback,
    ReuseMeasure,
    normalize_reuse_feedback,
    same_reuse_feedback,
    validate_reuse_feedback,
)
from .search import AuthorizationBounds, SearchParameters, SearchRow, widen_real

if TYPE_CHECKING:
    import psycopg

TABLE = "agent_experience.experience_records"
EVENTS_TABLE = "agent_experience.lifecycle_events"

_PLACEHOLDER = re.compile(r"@([A-Za-z_][A-Za-z0-9_]*)")


def contract_sql(name: str) -> str:
    """One of the contract's SQL files, as the package ships it (``declare-bounds.sql``,
    ``search-experience-text.sql``, ``record-injection.sql``)."""
    return resources.files("agentexperience").joinpath("sql").joinpath(name).read_text(encoding="utf-8")


def to_driver_placeholders(sql: str) -> str:
    """
    The contract's ``@name`` placeholders as psycopg's ``%(name)s``. Only code is rewritten: a ``--`` or ``/* */``
    comment, a single-quoted literal (``E''`` escapes included), a double-quoted identifier and a dollar-quoted string
    are kept as written, so a parameter named only inside one of them is never asked for. Every ``%`` is doubled,
    because psycopg reads the whole text. An ``@`` followed by a letter or ``_`` in code is always a placeholder, so
    PostgreSQL's prefix ``@`` operator must be written with a space after it.
    """
    out: list[str] = []
    index = 0
    length = len(sql)

    def until(end: str, start: int) -> int:
        found = sql.find(end, start)
        return length if found < 0 else found + len(end)

    while index < length:
        ch = sql[index]
        if sql.startswith("--", index):
            stop = until("\n", index)
        elif sql.startswith("/*", index):
            depth, stop = 1, index + 2
            while stop < length and depth:
                if sql.startswith("/*", stop):
                    depth, stop = depth + 1, stop + 2
                elif sql.startswith("*/", stop):
                    depth, stop = depth - 1, stop + 2
                else:
                    stop += 1
        elif ch == "'":
            escaped = (
                index > 0
                and sql[index - 1] in "eE"
                and (index < 2 or not (sql[index - 2].isalnum() or sql[index - 2] == "_"))
            )
            stop = index + 1
            while stop < length:
                if escaped and sql[stop] == "\\":
                    stop += 2
                elif sql[stop] == "'":
                    if sql.startswith("''", stop):
                        stop += 2
                    else:
                        stop += 1
                        break
                else:
                    stop += 1
        elif ch == '"':
            stop = index + 1
            while stop < length:
                if sql.startswith('""', stop):
                    stop += 2
                elif sql[stop] == '"':
                    stop += 1
                    break
                else:
                    stop += 1
        elif ch == "$" and (tag := _DOLLAR_TAG.match(sql, index)) is not None:
            stop = until(tag.group(0), tag.end())
        else:
            placeholder = _PLACEHOLDER.match(sql, index) if ch == "@" else None
            if placeholder is not None:
                out.append(f"%({placeholder.group(1)})s")
                index = placeholder.end()
            else:
                out.append("%%" if ch == "%" else ch)
                index += 1
            continue
        out.append(sql[index:stop].replace("%", "%%"))
        index = stop
    return "".join(out)


_DOLLAR_TAG = re.compile(r"\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$")


def contract_statements(name: str) -> dict[str, str]:
    """
    The statements of a contract SQL file that holds several, each starting at its ``-- statement: <name>`` line and
    running to the next one (or the end), by name; the header before the first is not a statement.
    """
    statements: dict[str, list[str]] = {}
    current: list[str] | None = None
    for line in contract_sql(name).splitlines():
        if line.startswith("-- statement: "):
            current = statements.setdefault(line.removeprefix("-- statement: ").strip(), [])
        elif current is not None:
            current.append(line)
    return {key: "\n".join(lines).strip() for key, lines in statements.items()}


DECLARE_BOUNDS_SQL = "declare-bounds.sql"
SEARCH_SQL = "search-experience-text.sql"
RECORD_INJECTION_SQL = "record-injection.sql"
WRITE_EMBEDDING_SQL = "write-experience-embedding.sql"
SEARCH_VECTOR_SQL = "search-experience-vector.sql"
LIST_LESSONS_SQL = "list-experience-lessons.sql"
RECORD_HOLDOUT_SQL = "record-holdout-assignment.sql"
QUERY_HOLDOUT_OUTCOMES_SQL = "query-holdout-outcomes.sql"
RECORD_PROVIDER_HOLDOUT_SQL = "record-provider-holdout-assignment.sql"
QUERY_PROVIDER_HOLDOUT_OUTCOMES_SQL = "query-provider-holdout-outcomes.sql"
LIFECYCLE_SQL = "lifecycle.sql"
REUSE_FEEDBACK_SQL = "reuse-feedback.sql"

_UNIQUE_VIOLATION = "23505"
_SERIALIZATION_FAILURES = frozenset({"40001", "40P01"})  # serialization_failure, deadlock_detected
_EVENT_PRIMARY_KEY = "lifecycle_events_pkey"
_EVENT_REVISION_INDEX = "ix_lifecycle_events_record_revision"
_EVIDENCE_PRIMARY_KEY = "confidence_evidence_pkey"
_EVIDENCE_INDEPENDENCE_INDEX = "ux_confidence_evidence_independence"
_EVIDENCE_ASSESSMENT_INDEX = "ux_confidence_evidence_assessment"

_INJECTIONS_UNREADABLE = frozenset({"42P01", "42501"})  # undefined_table, insufficient_privilege

_INSERT_RECORD = (
    f"INSERT INTO {TABLE} ({', '.join(COLUMN_NAMES)}) VALUES ({', '.join(f'%({name})s' for name in COLUMN_NAMES)})"
)
_EVENT_COLUMNS = (
    "event_id",
    "experience_id",
    "tenant_id",
    "application_id",
    "project_id",
    "team_id",
    "agent_id",
    "user_id",
    "prior_status",
    "current_status",
    "reason",
    "producer",
    "occurred_at",
    "recorded_at",
    "expected_revision",
    "applied_revision",
    "actor",
)
_INSERT_EVENT = (
    f"INSERT INTO {EVENTS_TABLE} ({', '.join(_EVENT_COLUMNS)}) "
    f"VALUES ({', '.join(f'%({name})s' for name in _EVENT_COLUMNS)})"
)
_SELECT_RECORD = (
    f"SELECT {', '.join(COLUMN_NAMES)} FROM {TABLE} WHERE experience_id = %(experience_id)s "
    "AND tenant_id = %(tenant_id)s AND application_id = %(application_id)s AND project_id = %(project_id)s "
    "AND team_id IS NOT DISTINCT FROM %(team_id)s AND agent_id IS NOT DISTINCT FROM %(agent_id)s "
    "AND user_id IS NOT DISTINCT FROM %(user_id)s AND deleted_at IS NULL"
)


class PostgresExperienceStore:
    """
    Records in PostgreSQL, through psycopg 3. ``connect`` opens a connection as the application role; each
    operation uses one connection and one transaction. ``clock`` is the store's own clock, which a lifecycle commit
    stamps on the event and the record (contract 1.16).

    It moves a record through its lifecycle as .NET's PostgreSQL store does
    (:class:`~agentexperience.lifecycle.LifecycleStore`), running ``sql/lifecycle.sql`` as written: every write is a
    revision compare-and-set in the database, so writers in any language racing at one revision commit exactly once.
    """

    def __init__(
        self, connect: str | Callable[[], psycopg.Connection[Any]], *, clock: Callable[[], Instant] = Instant.now
    ) -> None:
        import psycopg

        if isinstance(connect, str):
            conninfo = connect

            def opener() -> psycopg.Connection[Any]:
                return psycopg.connect(conninfo)

            self._connect: Callable[[], psycopg.Connection[Any]] = opener
        else:
            self._connect = connect
        self._declare = to_driver_placeholders(contract_sql(DECLARE_BOUNDS_SQL))
        self._search = to_driver_placeholders(contract_sql(SEARCH_SQL))
        self._record_injection = to_driver_placeholders(contract_sql(RECORD_INJECTION_SQL))
        self._record_holdout = to_driver_placeholders(contract_sql(RECORD_HOLDOUT_SQL))
        self._write_statements = {
            key: to_driver_placeholders(text) for key, text in contract_statements(WRITE_EMBEDDING_SQL).items()
        }
        # The vector search statements keep their ``{dim}`` until the query vector's length is known.
        self._vector_statements = contract_statements(SEARCH_VECTOR_SQL)
        self._outcome_statements = {
            key: to_driver_placeholders(text) for key, text in contract_statements(QUERY_HOLDOUT_OUTCOMES_SQL).items()
        }
        self._record_provider_holdout = to_driver_placeholders(contract_sql(RECORD_PROVIDER_HOLDOUT_SQL))
        self._provider_outcome_statements = {
            key: to_driver_placeholders(text)
            for key, text in contract_statements(QUERY_PROVIDER_HOLDOUT_OUTCOMES_SQL).items()
        }
        self._lessons_statements = {
            key: to_driver_placeholders(text) for key, text in contract_statements(LIST_LESSONS_SQL).items()
        }
        self._lifecycle = {
            key: to_driver_placeholders(text) for key, text in contract_statements(LIFECYCLE_SQL).items()
        }
        self._clock = clock

    @contextmanager
    def _transaction(self, bounds: AuthorizationBounds) -> Iterator[psycopg.Cursor[Any]]:
        with self._connect() as connection, connection.transaction(), connection.cursor() as cursor:
            cursor.execute(self._declare, bounds.parameters())
            yield cursor

    def insert_finalized(
        self,
        bounds: AuthorizationBounds,
        finalized: FinalizedExperience,
        *,
        actor: str,
        recorded_at: _dt.datetime | None = None,
    ) -> None:
        """
        Writes a finalized record in its final state at revision 1, and the initial lifecycle event that took it
        there, in one transaction. ``actor`` is the authorizing principal; ``recorded_at`` defaults to now.
        """
        if is_null_or_whitespace(actor):
            raise ValueError("The actor (the authorizing principal) must not be blank.")
        if recorded_at is not None and recorded_at.tzinfo is None:
            raise ValueError("recorded_at must be timezone-aware.")
        record, event = finalized.record, finalized.event
        scope = record.scope
        with self._transaction(bounds) as cursor:
            cursor.execute(_INSERT_RECORD, _record_parameters(record))
            cursor.execute(
                _INSERT_EVENT,
                {
                    "event_id": event.event_id,
                    "experience_id": event.experience_id,
                    "tenant_id": scope.tenant_id,
                    "application_id": scope.application_id,
                    "project_id": scope.project_id,
                    "team_id": scope.team_id,
                    "agent_id": scope.agent_id,
                    "user_id": scope.user_id,
                    "prior_status": event.prior_status,
                    "current_status": event.current_status,
                    "reason": event.reason,
                    "producer": event.producer,
                    "occurred_at": event.occurred_at.to_datetime(),
                    "recorded_at": recorded_at or _dt.datetime.now(_dt.timezone.utc),
                    "expected_revision": event.expected_revision,
                    "applied_revision": event.applied_revision,
                    "actor": actor,
                },
            )

    def insert_record(self, bounds: AuthorizationBounds, record: ExperienceRecord) -> None:
        """
        Writes one record's row exactly as given, with no lifecycle event. For seeding and tests: a record a port
        finalizes is written with :meth:`insert_finalized`, or it has no history.
        """
        with self._transaction(bounds) as cursor:
            cursor.execute(_INSERT_RECORD, _record_parameters(record))

    def get(self, bounds: AuthorizationBounds, scope: Scope, experience_id: uuid.UUID) -> ExperienceRecord | None:
        """Reads one record in exactly ``scope``; ``None`` when there is none, or it is sealed (``payload_version``
        2, which a contract 1.x port cannot read)."""
        with self._transaction(bounds) as cursor:
            cursor.execute(
                _SELECT_RECORD,
                {
                    "experience_id": experience_id,
                    "tenant_id": scope.tenant_id,
                    "application_id": scope.application_id,
                    "project_id": scope.project_id,
                    "team_id": scope.team_id,
                    "agent_id": scope.agent_id,
                    "user_id": scope.user_id,
                },
            )
            row = cursor.fetchone()
        if row is None:
            return None
        columns = dict(zip(COLUMN_NAMES, row, strict=True))
        if columns["payload_version"] != PAYLOAD_VERSION:
            return None
        return from_columns(columns, columns["payload"])

    def search(self, bounds: AuthorizationBounds, parameters: SearchParameters) -> list[SearchRow]:
        """
        Runs ``sql/search-experience-text.sql`` and returns its rows in the order returned, each relevance widened
        from ``real``. A sealed row (``payload_version`` 2) is skipped. Rank them with
        :func:`agentexperience.search.rank`.
        """
        with self._transaction(bounds) as cursor:
            cursor.execute(self._search, _search_parameters(parameters))
            names = [column.name for column in cursor.description or ()]
            rows = cursor.fetchall()
        count = len(COLUMN_NAMES)
        if names[:count] != list(COLUMN_NAMES) or names[count : count + 1] != ["relevance"]:
            raise RuntimeError("The text search returned columns in an unexpected order.")
        result: list[SearchRow] = []
        for row in rows:
            columns = dict(zip(names, row, strict=True))
            if columns["payload_version"] != PAYLOAD_VERSION:
                continue
            if columns["relevance"] is None:
                raise RuntimeError("The text search returned a row with no relevance.")
            result.append(
                SearchRow(
                    from_columns(columns, columns["payload"]),
                    widen_real(float(columns["relevance"])),
                    bool(columns.get("shared_by_grant") or False),
                )
            )
        return result

    def list_lessons_page(
        self, bounds: AuthorizationBounds, scope: Scope, statuses: Sequence[str] | None, limit: int
    ) -> LessonsRead:
        """
        The lessons page (contract 1.7): in one transaction that declared the bounds, ``page`` (``page-with-statuses``
        when ``statuses`` is given) from ``sql/list-experience-lessons.sql`` with ``limit`` as ``@limit``: the scope's
        live records newest first, each with its last injection. When that fails with ``undefined_table`` or
        ``insufficient_privilege`` (the injections table is not migrated, or the role lacks the 1.4 grants), the
        transaction is rolled back and a new one runs ``page-without-injections`` (``...-with-statuses``) and the
        result says the injections are not tracked. A sealed row (``payload_version`` 2) is skipped. Any other failure
        raises. Raises :class:`~agentexperience.inmemory.AuthorizationDenied` outside the bounds (no statement runs),
        and ``ValueError`` for an invalid ``limit`` or ``statuses``. Reads only.
        """
        from psycopg import Error
        from psycopg.types.numeric import Int4

        check_lessons_read(statuses, limit)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        parameters: dict[str, Any] = {
            "tenant_id": scope.tenant_id,
            "application_id": scope.application_id,
            "project_id": scope.project_id,
            "team_id": scope.team_id,
            "agent_id": scope.agent_id,
            "user_id": scope.user_id,
            "limit": Int4(limit),
        }
        if statuses is not None:
            parameters["statuses"] = list(dict.fromkeys(statuses))
        suffix = "" if statuses is None else "-with-statuses"
        tracked = True
        try:
            names, rows = self._run_lessons(bounds, "page" + suffix, parameters)
        except Error as failure:
            if failure.sqlstate not in _INJECTIONS_UNREADABLE:
                raise
            tracked = False
            names, rows = self._run_lessons(bounds, "page-without-injections" + suffix, parameters)
        count = len(COLUMN_NAMES)
        expected_tail = ["last_injected_at", "injection_count"] if tracked else []
        if names != [*COLUMN_NAMES, *expected_tail]:
            raise RuntimeError("The lessons page returned columns in an unexpected order.")
        result: list[LessonRow] = []
        for row in rows:
            columns = dict(zip(names, row, strict=True))
            if columns["payload_version"] != PAYLOAD_VERSION:
                continue
            injection = None
            if tracked and columns["last_injected_at"] is not None:
                injection = InjectionSummary(
                    Instant.from_datetime(columns["last_injected_at"]), int(columns["injection_count"])
                )
            result.append(
                LessonRow(from_columns({name: columns[name] for name in names[:count]}, columns["payload"]), injection)
            )
        return LessonsRead(tuple(result), tracked, len(rows))

    def _run_lessons(
        self, bounds: AuthorizationBounds, statement: str, parameters: dict[str, Any]
    ) -> tuple[list[str], list[tuple[Any, ...]]]:
        with self._transaction(bounds) as cursor:
            cursor.execute(self._lessons_statements[statement], parameters)
            names = [column.name for column in cursor.description or ()]
            return names, cursor.fetchall()

    def write_embedding(self, bounds: AuthorizationBounds, write: EmbeddingWrite) -> EmbeddingWriteResult:
        """
        Stores a record's embedding (contract 1.6): in one transaction that declared the bounds for the record's scope,
        ``write`` from ``sql/write-experience-embedding.sql`` and, when it wrote no row, ``probe-revision``.
        ``Written``;
        ``Stale`` with the record's current revision; ``Missing`` for an unknown ID, a record in another scope or an
        erased one. ``write`` is validated first (``ValueError``); nothing runs when it is refused. Needs the grants of
        ``sql/application-role-privileges-1.6.sql`` and the vectors migrations.
        """
        scope = write.scope
        parameters: dict[str, Any] = {
            "experience_id": write.experience_id,
            "tenant_id": scope.tenant_id,
            "application_id": scope.application_id,
            "project_id": scope.project_id,
            "team_id": scope.team_id,
            "agent_id": scope.agent_id,
            "user_id": scope.user_id,
            "model_id": write.model_id,
            "dimension": write.dimension,
            "content_hash": write.content_hash,
            "source_revision": write.source_revision,
            "embedding": vector_literal(write.vector),
            "now": write.now.truncate_to_microseconds().to_datetime(),
        }
        with self._transaction(bounds) as cursor:
            cursor.execute(self._write_statements["write"], parameters)
            if cursor.rowcount == 1:
                return EmbeddingWriteResult("Written")
            cursor.execute(self._write_statements["probe-revision"], parameters)
            row = cursor.fetchone()
        return EmbeddingWriteResult("Missing") if row is None else EmbeddingWriteResult("Stale", int(row[0]))

    def search_vectors(self, bounds: AuthorizationBounds, query: VectorQuery) -> VectorSearchResult:
        """
        The nearest-neighbour search (contract 1.6): in one transaction that declared the bounds, ``search`` (or
        ``search-excluding-model-authored``) from ``sql/search-experience-vector.sql`` with ``{dim}`` the query
        vector's length and, when it returns no row, the matching compatibility probe. Rows come back nearest first,
        ties by experience ID, NaN last. A sealed row (``payload_version`` 2) is skipped. ``query`` is validated first.
        """
        from psycopg.types.numeric import Int4

        scope = query.scope
        excluding = query.exclude_model_authored
        search = self._vector_statements["search-excluding-model-authored" if excluding else "search"]
        probe = self._vector_statements["compatibility-excluding-model-authored" if excluding else "compatibility"]
        search = to_driver_placeholders(search.replace("{dim}", str(len(query.vector))))
        probe = to_driver_placeholders(probe)
        parameters: dict[str, Any] = {
            "tenant_id": scope.tenant_id,
            "application_id": scope.application_id,
            "project_id": scope.project_id,
            "team_id": scope.team_id,
            "agent_id": scope.agent_id,
            "user_id": scope.user_id,
            "statuses": list(query.statuses),
            "min_confidence": float(query.min_confidence),
            "model_id": query.model_id,
            "query_vector": vector_literal(query.vector),
            "limit": Int4(query.limit),
        }
        with self._transaction(bounds) as cursor:
            cursor.execute(search, parameters)
            names = [column.name for column in cursor.description or ()]
            rows = cursor.fetchall()
            compatible: tuple[Any, ...] | None = None
            if not rows:
                cursor.execute(probe, {key: value for key, value in parameters.items() if key != "limit"})
                compatible = cursor.fetchone()
        if not rows:
            if compatible is None or not compatible[0]:
                return VectorSearchResult("Found")
            return VectorSearchResult("DimensionMismatch" if compatible[1] else "ModelMismatch")
        count = len(COLUMN_NAMES)
        if names[:count] != list(COLUMN_NAMES) or names[-1:] != ["distance"]:
            raise RuntimeError("The vector search returned columns in an unexpected order.")
        hits: list[VectorHit] = []
        for row in rows:
            columns = dict(zip(names, row, strict=True))
            if columns["payload_version"] != PAYLOAD_VERSION:
                continue
            if columns["distance"] is None:
                raise RuntimeError("The vector search returned a row with no distance.")
            hits.append(VectorHit(from_columns(columns, columns["payload"]), float(columns["distance"])))
        return VectorSearchResult("Found", tuple(order_hits(hits)))

    def record_injection(
        self,
        bounds: AuthorizationBounds,
        scope: Scope,
        experience_ids: Sequence[uuid.UUID],
        injected_at: Instant,
    ) -> None:
        """
        Records that ``experience_ids`` were injected in ``scope`` at ``injected_at``: runs ``sql/record-injection.sql``
        (contract 1.4) once, in one transaction that declared ``bounds``. Only a record live in exactly ``scope``
        gets a row (one borrowed through a grant, or in another scope, is skipped by the statement); the instant is
        truncated to whole microseconds and clamped to the database clock. Raises
        :class:`~agentexperience.inmemory.AuthorizationDenied` when ``bounds`` do not permit ``scope``, ``ValueError``
        for more than 200 IDs or a nil one, and the driver's
        error otherwise (``insufficient_privilege`` until the role holds the grants of
        ``sql/application-role-privileges-1.4.sql``). The agent integration calls it in the background, fail-open.
        """
        ids = injection_ids(experience_ids)
        if not isinstance(injected_at, Instant):
            raise TypeError("injected_at must be an Instant.")
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        if not ids:
            return
        with self._transaction(bounds) as cursor:
            cursor.execute(
                self._record_injection,
                {
                    "experience_ids": ids,
                    "injected_at": injected_at.to_datetime(),
                    "tenant_id": scope.tenant_id,
                    "application_id": scope.application_id,
                    "project_id": scope.project_id,
                    "team_id": scope.team_id,
                    "agent_id": scope.agent_id,
                    "user_id": scope.user_id,
                },
            )

    def record_holdout_assignment(
        self, bounds: AuthorizationBounds, scope: Scope, assignment: HoldoutAssignment
    ) -> None:
        """
        Records a run's holdout assignment: runs ``sql/record-holdout-assignment.sql`` (contract 1.8) once, in one
        transaction that declared ``bounds``, with each named record once (at the revision first named). Only a record
        live in exactly ``scope`` gets a row, at the revision named; a row that already exists for the run and record
        is kept; the instant is truncated to whole microseconds and clamped to the database clock; nothing is written
        for a run whose own records are all being erased. Raises :class:`~agentexperience.inmemory.AuthorizationDenied`
        when ``bounds`` do not permit ``scope``, ``ValueError`` for an invalid assignment, and the driver's error
        otherwise (``insufficient_privilege`` until the role holds the grants of
        ``sql/application-role-privileges-1.8.sql``). The agent integration calls it in the background, fail-open.
        """
        from psycopg.types.numeric import Int8

        records = holdout_records(assignment)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        if not records:
            return
        with self._transaction(bounds) as cursor:
            cursor.execute(
                self._record_holdout,
                {
                    "run_id": assignment.run_id,
                    "arm": assignment.arm,
                    "experience_ids": [exposure.experience_id for exposure in records],
                    "revisions": [Int8(exposure.revision) for exposure in records],
                    "assigned_at": assignment.assigned_at.to_datetime(),
                    "tenant_id": scope.tenant_id,
                    "application_id": scope.application_id,
                    "project_id": scope.project_id,
                    "team_id": scope.team_id,
                    "agent_id": scope.agent_id,
                    "user_id": scope.user_id,
                },
            )

    def read_holdout_outcomes(
        self, bounds: AuthorizationBounds, scope: Scope, limit: int, after: HoldoutOutcomeCursor | None = None
    ) -> list[HoldoutOutcomeRow]:
        """
        A page of the holdout's outcome rows (contract 1.9): in one transaction that declared ``bounds``,
        ``first-page`` (or ``next-page`` after ``after``) from ``sql/query-holdout-outcomes.sql``. Each assignment row
        in exactly ``scope`` comes back with its run's outcome and model (from its run's own record's payload) and
        the named lesson's model. A sealed record (``payload_version`` 2) cannot be read by a port: its run has no
        outcome (it counts as pending) and it names no model. Raises
        :class:`~agentexperience.inmemory.AuthorizationDenied` outside the bounds (no statement runs), ``ValueError``
        for a limit outside 1 to 1000, and the driver's error otherwise (``insufficient_privilege`` without the 1.8
        grants). Reads only.
        """
        from psycopg.types.numeric import Int4

        check_outcome_limit(limit)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        parameters: dict[str, Any] = {
            "tenant_id": scope.tenant_id,
            "application_id": scope.application_id,
            "project_id": scope.project_id,
            "team_id": scope.team_id,
            "agent_id": scope.agent_id,
            "user_id": scope.user_id,
            "limit": Int4(limit),
        }
        if after is not None:
            parameters["after_run_id"], parameters["after_experience_id"] = after
        with self._transaction(bounds) as cursor:
            cursor.execute(self._outcome_statements["first-page" if after is None else "next-page"], parameters)
            names = [column.name for column in cursor.description or ()]
            rows = cursor.fetchall()
        if len(names) != 23 or names[: len(COLUMN_NAMES)] != list(COLUMN_NAMES) or names[22] != "part":
            raise RuntimeError("The holdout outcome read returned columns in an unexpected order.")
        page: list[tuple[Any, ...]] = []
        lessons: dict[uuid.UUID, str | None] = {}
        for row in rows:
            if row[22] == 0:
                page.append(row)
            else:
                lessons[_uuid(row[0])] = _model(row)
        return [
            HoldoutOutcomeRow(
                _uuid(row[18]),
                row[19],
                _uuid(row[20]),
                Instant.from_datetime(row[21]),
                _outcome(row),
                _model(row),
                lessons.get(_uuid(row[20])),
            )
            for row in page
        ]

    def record_provider_holdout_assignment(
        self, bounds: AuthorizationBounds, scope: Scope, assignment: ProviderHoldoutAssignment
    ) -> None:
        """
        Records a run's arm in a memory provider's holdout: runs ``sql/record-provider-holdout-assignment.sql``
        (contract 1.11) once, in one transaction that declared ``bounds``. One row per tenant, run and key, in exactly
        ``scope``; a row that already exists for them is kept; the instant is truncated to whole microseconds and
        clamped to the database clock; nothing is written for a run whose own records are all erased. Raises
        :class:`~agentexperience.inmemory.AuthorizationDenied` when ``bounds`` do not permit ``scope``, ``ValueError``
        for an invalid assignment, and the driver's error otherwise (``insufficient_privilege`` until the role holds
        the grants of ``sql/application-role-privileges-1.11.sql``). The wrapper calls it in the background, fail-open.
        """
        check_provider_assignment(assignment)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        with self._transaction(bounds) as cursor:
            cursor.execute(
                self._record_provider_holdout,
                {
                    "run_id": assignment.run_id,
                    "provider_key": assignment.provider_key,
                    "arm": assignment.arm,
                    "assigned_at": assignment.assigned_at.truncate_to_microseconds().to_datetime(),
                    "tenant_id": scope.tenant_id,
                    "application_id": scope.application_id,
                    "project_id": scope.project_id,
                    "team_id": scope.team_id,
                    "agent_id": scope.agent_id,
                    "user_id": scope.user_id,
                },
            )

    def read_provider_holdout_outcomes(
        self,
        bounds: AuthorizationBounds,
        scope: Scope,
        provider_key: str,
        limit: int,
        after: uuid.UUID | None = None,
    ) -> list[ProviderHoldoutOutcomeRow]:
        """
        A page of one provider's holdout rows (contract 1.11): in one transaction that declared ``bounds``,
        ``first-page`` (or ``next-page`` after the run ``after``) from ``sql/query-provider-holdout-outcomes.sql``.
        Each row in exactly ``scope`` under ``provider_key`` comes back with its run's outcome (from its run's own
        record's payload); a sealed record (``payload_version`` 2) cannot be read by a port, so its run has no
        outcome. Raises :class:`~agentexperience.inmemory.AuthorizationDenied` outside the bounds (no statement
        runs), ``ValueError`` for a bad key or a limit outside 1 to 1000, and the driver's error otherwise
        (``insufficient_privilege`` without the 1.11 grants). Reads only.
        """
        from psycopg.types.numeric import Int4

        check_provider_key(provider_key)
        check_outcome_limit(limit)
        if not permits(bounds, scope):
            raise AuthorizationDenied("The scope lies outside the authorization bounds.")
        parameters: dict[str, Any] = {
            "tenant_id": scope.tenant_id,
            "application_id": scope.application_id,
            "project_id": scope.project_id,
            "team_id": scope.team_id,
            "agent_id": scope.agent_id,
            "user_id": scope.user_id,
            "provider_key": provider_key,
            "limit": Int4(limit),
        }
        if after is not None:
            parameters["after_run_id"] = after
        with self._transaction(bounds) as cursor:
            cursor.execute(
                self._provider_outcome_statements["first-page" if after is None else "next-page"], parameters
            )
            names = [column.name for column in cursor.description or ()]
            rows = cursor.fetchall()
        if (
            len(names) != 21
            or names[: len(COLUMN_NAMES)] != list(COLUMN_NAMES)
            or names[18:] != ["run_id", "arm", "assigned_at"]
        ):
            raise RuntimeError("The provider holdout outcome read returned columns in an unexpected order.")
        return [
            ProviderHoldoutOutcomeRow(
                _uuid(row[18]), provider_key, row[19], Instant.from_datetime(row[20]), _outcome(row)
            )
            for row in rows
        ]

    # ---- Contract 1.16: the lifecycle (sql/lifecycle.sql) ------------------------------------------------------------

    def read_record(
        self, authorization: AuthorizationContext, scope: Scope, experience_id: uuid.UUID
    ) -> RecordReadResult:
        """
        One record in exactly ``scope``, as the lifecycle reads it (``read-record``): validated, then authorized, then
        in one transaction that declared the bounds. A tombstone is ``Deleted``; a sealed record (``payload_version``
        2) is ``NotFound``, since a port cannot read it.
        """
        errors = validate_get(scope, experience_id)
        if errors:
            return RecordReadResult("Invalid", None, errors)
        if not authorization.permits(scope):
            return RecordReadResult("Denied", None)
        with self._transaction(authorization.bounds) as cursor:
            cursor.execute(self._lifecycle["read-record"], {"experience_id": experience_id, **_scope_parameters(scope)})
            row = cursor.fetchone()
        if row is None:
            return RecordReadResult("NotFound", None)
        if row[22] is not None:
            return RecordReadResult("Deleted", None)
        if row[16] != PAYLOAD_VERSION:
            return RecordReadResult("NotFound", None)
        columns = dict(zip(COLUMN_NAMES, row[: len(COLUMN_NAMES)], strict=True))
        return RecordReadResult("Found", from_columns(columns, columns["payload"]))

    def get_history(self, authorization: AuthorizationContext, query: HistoryQuery) -> HistoryResult:
        """
        One page of a record's history (``read-history``): its events after the cursor, oldest first, with the
        record's revision and the next cursor. A tombstone is ``Deleted``; a sealed record ``NotFound``.
        """
        from psycopg.types.numeric import Int4, Int8

        errors = validate_history_query(query)
        if errors:
            return HistoryResult("Invalid", 0, (), errors)
        if not authorization.permits(query.scope):
            return HistoryResult("Denied", 0, ())
        cursor_value = query.start_after_revision
        with self._transaction(authorization.bounds) as cursor:
            # "No cursor" is a NULL bound as bigint: an untyped NULL leaves "@start_after_revision IS NULL" ambiguous.
            cursor.adapters.register_dumper(_NullBigint, _null_bigint_dumper())
            cursor.execute(
                self._lifecycle["read-history"],
                {
                    "experience_id": query.experience_id,
                    **_scope_parameters(query.scope),
                    "start_after_revision": _NULL_BIGINT if cursor_value is None else Int8(cursor_value),
                    "limit": Int4(query.limit),
                },
            )
            rows = cursor.fetchall()
        if not rows:
            return HistoryResult("NotFound", 0, ())
        first = rows[0]
        if first[35] is not None:
            return HistoryResult("Deleted", 0, ())
        if first[36] != PAYLOAD_VERSION:
            return HistoryResult("NotFound", 0, ())
        events = tuple(_decode_event(row)[0] for row in rows if row[0] is not None)
        return HistoryResult("Found", int(first[34]), events, (), events[-1].applied_revision if events else None)

    def check_supersession(
        self,
        authorization: AuthorizationContext,
        scope: Scope,
        experience_id: uuid.UUID,
        replacement_experience_id: uuid.UUID,
    ) -> SupersessionCheckResult:
        """Whether the replacement may replace the record (``check-supersession``), in one transaction."""
        errors = validate_supersession_check(scope, experience_id, replacement_experience_id)
        if errors:
            return SupersessionCheckResult("Invalid", None, errors)
        if not authorization.permits(scope):
            return SupersessionCheckResult("Denied", None)
        with self._transaction(authorization.bounds) as cursor:
            cursor.execute(
                self._lifecycle["check-supersession"],
                {
                    "experience_id": experience_id,
                    "replacement_id": replacement_experience_id,
                    **_scope_parameters(scope),
                },
            )
            row = cursor.fetchone()
        if row is None or row[0] is None:
            return SupersessionCheckResult("RecordNotFound", None)
        if row[1] is None:
            return SupersessionCheckResult("ReplacementNotFound", None)
        return SupersessionCheckResult("Cycle" if row[2] else "Allowed", row[1])

    def commit_lifecycle_event(
        self, authorization: AuthorizationContext, scope: Scope, event: LifecycleEvent
    ) -> LifecycleCommitResult:
        """
        Commits one lifecycle event as ``sql/lifecycle.sql``'s header orders it: validated, then authorized, then one
        transaction at READ COMMITTED that declares the bounds, inserts the evidence under a savepoint and the event,
        gates a supersession, moves the projection by compare-and-set, and only then, holding the row lock, refuses a
        sealed record (``NotFound``). A unique violation is recognised by its constraint's name; a serialization
        failure or deadlock on the projection is a stale revision. Any other failure raises, with nothing written.
        """
        errors = validate_lifecycle_event(scope, event, authorization)
        if errors:
            return LifecycleCommitResult("Invalid", 0, None, errors)
        if not authorization.permits(scope):
            return LifecycleCommitResult("Denied", 0, None)
        recorded_at = self._clock().truncate_to_microseconds()
        with self._connect() as connection:
            # Explicit transactions: the isolation level is pinned here, never inherited from the connection.
            connection.autocommit = True
            with connection.cursor() as cursor:
                return _Commit(self._lifecycle, self._declare, cursor, authorization, scope, event, recorded_at).run()


def _uuid(value: Any) -> uuid.UUID:
    return value if isinstance(value, uuid.UUID) else uuid.UUID(str(value))


def _payload(row: tuple[Any, ...]) -> Any:
    """A readable record's payload (columns 1-18 of an outcome row), or ``None``: no record, or a sealed one."""
    if row[0] is None or row[16] != PAYLOAD_VERSION:
        return None
    payload = row[17]
    return json.loads(payload) if isinstance(payload, str) else payload


def _outcome(row: tuple[Any, ...]) -> str | None:
    payload = _payload(row)
    outcome = payload.get("outcome") if isinstance(payload, dict) else None
    status = outcome.get("status") if isinstance(outcome, dict) else None
    return status if isinstance(status, str) else None


def _model(row: tuple[Any, ...]) -> str | None:
    payload = _payload(row)
    environment = payload.get("environment") if isinstance(payload, dict) else None
    return model_of_metadata(environment.get("metadata")) if isinstance(environment, dict) else None


def _record_parameters(record: ExperienceRecord) -> dict[str, Any]:
    from psycopg.types.json import Jsonb

    scope = record.scope
    return {
        "experience_id": record.experience_id,
        "source_run_id": record.source_run_id,
        "tenant_id": scope.tenant_id,
        "application_id": scope.application_id,
        "project_id": scope.project_id,
        "team_id": scope.team_id,
        "agent_id": scope.agent_id,
        "user_id": scope.user_id,
        "task_id": record.task_id,
        "status": record.status,
        "reuse_confidence": float(record.reuse_confidence),
        "supporting_validations": record.supporting_validations,
        "contradictions": record.contradictions,
        "revision": record.revision,
        "created_at": record.created_at.to_datetime(),
        "updated_at": record.updated_at.to_datetime(),
        "payload_version": PAYLOAD_VERSION,
        "payload": Jsonb(to_payload(record)),
    }


def _search_parameters(parameters: SearchParameters) -> dict[str, Any]:
    from psycopg.types.numeric import Int4

    values = parameters.parameters()
    values["limit"] = Int4(values["limit"])
    values["minimum_matched_terms"] = Int4(values["minimum_matched_terms"])
    return values


class _NullBigint:
    """A NULL bound with the type ``bigint``."""


_NULL_BIGINT = _NullBigint()


@functools.cache
def _null_bigint_dumper() -> type[Any]:
    """The dumper that binds :data:`_NULL_BIGINT`, built on first use (psycopg is the ``postgres`` extra's)."""
    from psycopg.adapt import Dumper
    from psycopg.postgres import types

    class NullBigintDumper(Dumper):
        oid = types["int8"].oid

        def dump(self, obj: Any) -> None:
            return None

    return NullBigintDumper


def _scope_parameters(scope: Scope) -> dict[str, Any]:
    return {
        "tenant_id": scope.tenant_id,
        "application_id": scope.application_id,
        "project_id": scope.project_id,
        "team_id": scope.team_id,
        "agent_id": scope.agent_id,
        "user_id": scope.user_id,
    }


def _instant(value: Any) -> Instant:
    return Instant.from_datetime(value)


def _decode_update(row: Sequence[Any]) -> ConfidenceUpdate | None:
    """The confidence payload of an event row (columns 18 to 33), or ``None`` when it carried none."""
    if row[18] is None:
        return None
    return ConfidenceUpdate(
        evidence_id=_uuid(row[18]),
        kind=row[19],
        source=row[20],
        run_id=_uuid(row[21]),
        verification_round_id=None if row[22] is None else _uuid(row[22]),
        reviewer_identity=row[23],
        rule_version=row[24],
        prior_reuse_confidence=float(row[26]),
        new_reuse_confidence=float(row[27]),
        prior_supporting_validations=int(row[28]),
        new_supporting_validations=int(row[29]),
        prior_contradictions=int(row[30]),
        new_contradictions=int(row[31]),
        detail=row[25],
        assessment_id=None if row[32] is None else _uuid(row[32]),
        admission=row[33],
    )


def _decode_event(row: Sequence[Any]) -> tuple[StoredLifecycleEvent, Scope]:
    """An event row (the 34 columns of ``select-event``) and the scope it was committed in."""
    event = LifecycleEvent(
        event_id=_uuid(row[0]),
        experience_record_id=_uuid(row[1]),
        prior_status=row[8],
        current_status=row[9],
        reason=row[10],
        producer=row[11],
        occurred_at=_instant(row[12]),
        expected_revision=int(row[14]),
        replacement_experience_id=None if row[16] is None else _uuid(row[16]),
        confidence=_decode_update(row),
    )
    scope = Scope(row[2], row[3], row[4], row[5], row[6], row[7])
    return StoredLifecycleEvent(event, _instant(row[13]), int(row[15]), row[17]), scope


def _constraint(error: Exception) -> str | None:
    """The constraint a unique violation names, or ``None`` for any other error."""
    if getattr(error, "sqlstate", None) != _UNIQUE_VIOLATION:
        return None
    diag = getattr(error, "diag", None)
    return None if diag is None else diag.constraint_name


class _Commit:
    """One lifecycle commit, statement by statement as ``sql/lifecycle.sql``'s header orders it."""

    def __init__(
        self,
        statements: dict[str, str],
        declare: str,
        cursor: psycopg.Cursor[Any],
        authorization: AuthorizationContext,
        scope: Scope,
        event: LifecycleEvent,
        recorded_at: Instant,
    ) -> None:
        self._statements = statements
        self._declare = declare
        self._cursor = cursor
        self._authorization = authorization
        self._scope = scope
        self._event = event
        self._occurred_at = event.occurred_at.truncate_to_microseconds()
        self._recorded_at = recorded_at
        self._applied_revision = event.expected_revision + 1
        self._scope_parameters = _scope_parameters(scope)

    # -- plumbing --

    def _execute(self, name: str, parameters: dict[str, Any] | None = None) -> None:
        self._cursor.execute(self._statements[name], parameters or {})

    def _fetch(self, name: str, parameters: dict[str, Any]) -> tuple[Any, ...] | None:
        self._execute(name, parameters)
        return self._cursor.fetchone()

    def _scalar(self, name: str, parameters: dict[str, Any]) -> bool:
        row = self._fetch(name, parameters)
        return bool(row is not None and row[0] is True)

    def _begin(self, isolation: str | None) -> None:
        self._cursor.execute("BEGIN" if isolation is None else f"BEGIN ISOLATION LEVEL {isolation}")
        self._cursor.execute(self._declare, self._authorization.bounds.parameters())

    def _commit(self) -> None:
        self._cursor.execute("COMMIT")

    def _rollback(self) -> None:
        self._cursor.execute("ROLLBACK")

    def _sealed(self) -> bool:
        return self._scalar("sealed-record", self._record_parameters())

    def _record_parameters(self) -> dict[str, Any]:
        return {"experience_id": self._event.experience_record_id, **self._scope_parameters}

    # -- the commit --

    def run(self) -> LifecycleCommitResult:
        import psycopg
        from psycopg.types.numeric import Int4, Int8

        event = self._event
        self._begin("READ COMMITTED")
        try:
            stored_confidence: ConfidenceUpdate | None = None
            if event.confidence is not None:
                settled = self._insert_evidence(event.confidence)
                if settled is not None:
                    return settled
                stored_confidence = event.confidence

            try:
                self._execute("insert-event", self._event_parameters(stored_confidence))
            except psycopg.Error as failure:
                constraint = _constraint(failure)
                if constraint == _EVENT_PRIMARY_KEY:
                    self._rollback()
                    return self._in_new_transaction(self._compare_stored_event)
                if constraint == _EVENT_REVISION_INDEX:
                    self._rollback()
                    return self._in_new_transaction(self._stale_or_missing)
                raise

            replacement = event.replacement_experience_id
            if replacement is not None:
                self._execute("lock-supersession-rows", {"lock_ids": [event.experience_record_id, replacement]})
                check = self._fetch(
                    "check-supersession",
                    {
                        "experience_id": event.experience_record_id,
                        "replacement_id": replacement,
                        **self._scope_parameters,
                    },
                )
                assert check is not None
                found, status, cycle = check[0], check[1], check[2]
                if found is None or status not in ("Validated", "Reinforced") or cycle:
                    self._rollback()
                    return LifecycleCommitResult("ReplacementNotAllowed", 0, None if found is None else status)

            counted = stored_confidence is not None and stored_confidence.counted
            recorded_only = stored_confidence is not None and not stored_confidence.counted
            statement = (
                "update-projection-with-confidence"
                if counted
                else "update-projection-recorded-only"
                if recorded_only
                else "update-projection"
            )
            parameters: dict[str, Any] = {
                **self._record_parameters(),
                "current_status": event.current_status,
                "prior_status": event.prior_status,
                "expected_revision": Int8(event.expected_revision),
                "applied_revision": Int8(self._applied_revision),
                "recorded_at": self._recorded_at.to_datetime(),
            }
            if counted:
                assert stored_confidence is not None
                parameters["new_reuse_confidence"] = float(stored_confidence.new_reuse_confidence)
                parameters["new_supporting_validations"] = Int4(stored_confidence.new_supporting_validations)
                parameters["new_contradictions"] = Int4(stored_confidence.new_contradictions)
            try:
                self._execute(statement, parameters)
            except psycopg.Error as failure:
                if failure.sqlstate not in _SERIALIZATION_FAILURES:
                    raise
                self._rollback()
                return self._in_new_transaction(self._stale_or_missing)

            if self._cursor.rowcount == 0:
                current = self._fetch("select-revision-and-status", self._record_parameters())
                self._rollback()
                if current is None:
                    return LifecycleCommitResult("NotFound", 0, None)
                revision, status, deleted_at = int(current[0]), current[1], current[2]
                if deleted_at is not None:
                    return LifecycleCommitResult("Deleted", revision, None)
                if revision != event.expected_revision:
                    return LifecycleCommitResult("StaleRevision", revision, None)
                return LifecycleCommitResult("StatusMismatch", revision, status)

            # 5. A port holds no key store, so a sealed record's events are not its to write. Checked only now that this
            # transaction holds the row lock, so a seal cannot land between the check and the write.
            if self._sealed():
                self._rollback()
                return LifecycleCommitResult("NotFound", 0, None)

            self._commit()
            return LifecycleCommitResult("Committed", self._applied_revision, None, (), stored_confidence)
        except BaseException:
            self._abandon()
            raise

    def _abandon(self) -> None:
        """Rolls back whatever is open after an unexpected failure; the connection closes next either way."""
        import contextlib

        with contextlib.suppress(Exception):
            self._cursor.execute("ROLLBACK")

    def _in_new_transaction(self, read: Callable[[], LifecycleCommitResult]) -> LifecycleCommitResult:
        self._begin(None)
        result = read()
        self._rollback()
        return result

    def _event_parameters(self, confidence: ConfidenceUpdate | None) -> dict[str, Any]:
        from psycopg.types.numeric import Int4, Int8

        event = self._event

        def number(value: int | None) -> Int4 | None:
            return None if value is None else Int4(value)

        return {
            "event_id": event.event_id,
            "experience_id": event.experience_record_id,
            **self._scope_parameters,
            "prior_status": event.prior_status,
            "current_status": event.current_status,
            "reason": event.reason,
            "producer": event.producer,
            "occurred_at": self._occurred_at.to_datetime(),
            "recorded_at": self._recorded_at.to_datetime(),
            "expected_revision": Int8(event.expected_revision),
            "applied_revision": Int8(self._applied_revision),
            "replacement_experience_id": event.replacement_experience_id,
            "actor": self._authorization.actor,
            "confidence_evidence_id": None if confidence is None else confidence.evidence_id,
            "confidence_kind": None if confidence is None else confidence.kind,
            "confidence_source": None if confidence is None else confidence.source,
            "confidence_run_id": None if confidence is None else confidence.run_id,
            "confidence_verification_round_id": None if confidence is None else confidence.verification_round_id,
            "confidence_reviewer_identity": None if confidence is None else confidence.reviewer_identity,
            "confidence_rule_version": None if confidence is None else confidence.rule_version,
            "confidence_detail": None if confidence is None else confidence.detail,
            "prior_reuse_confidence": None if confidence is None else float(confidence.prior_reuse_confidence),
            "new_reuse_confidence": None if confidence is None else float(confidence.new_reuse_confidence),
            "prior_supporting_validations": number(
                None if confidence is None else confidence.prior_supporting_validations
            ),
            "new_supporting_validations": number(None if confidence is None else confidence.new_supporting_validations),
            "prior_contradictions": number(None if confidence is None else confidence.prior_contradictions),
            "new_contradictions": number(None if confidence is None else confidence.new_contradictions),
            "confidence_assessment_id": None if confidence is None else confidence.assessment_id,
            "confidence_admission": None if confidence is None else confidence.admission,
        }

    def _insert_one(self, update: ConfidenceUpdate, event_id: uuid.UUID | None, revision: int, status: str) -> None:
        from psycopg.types.numeric import Int4, Int8

        self._execute(
            "insert-evidence",
            {
                "evidence_id": update.evidence_id,
                "experience_id": self._event.experience_record_id,
                "event_id": event_id,
                "confidence_kind": update.kind,
                "confidence_source": update.source,
                "confidence_run_id": update.run_id,
                "confidence_verification_round_id": update.verification_round_id,
                "confidence_reviewer_identity": update.reviewer_identity,
                "counted": update.counted,
                "actor": self._authorization.actor,
                "confidence_rule_version": update.rule_version,
                "confidence_detail": update.detail,
                "recorded_at": self._recorded_at.to_datetime(),
                "applied_revision": Int8(revision),
                "applied_status": status,
                "prior_reuse_confidence": float(update.prior_reuse_confidence),
                "new_reuse_confidence": float(update.new_reuse_confidence),
                "prior_supporting_validations": Int4(update.prior_supporting_validations),
                "new_supporting_validations": Int4(update.new_supporting_validations),
                "prior_contradictions": Int4(update.prior_contradictions),
                "new_contradictions": Int4(update.new_contradictions),
                "confidence_assessment_id": update.assessment_id,
                "confidence_admission": update.admission,
            },
        )

    def _insert_evidence(self, submitted: ConfidenceUpdate) -> LifecycleCommitResult | None:
        """Step 1: the evidence row under a savepoint. ``None`` when the commit goes on; otherwise how it settled."""
        import psycopg

        self._execute("savepoint")
        try:
            self._insert_one(submitted, self._event.event_id, self._applied_revision, self._event.current_status)
        except psycopg.Error as failure:
            constraint = _constraint(failure)
            if constraint == _EVIDENCE_INDEPENDENCE_INDEX:
                self._execute("rollback-to-savepoint")
                return self._record_duplicate(submitted)
            if constraint == _EVIDENCE_PRIMARY_KEY:
                self._execute("rollback-to-savepoint")
                result = self._compare_stored_evidence(submitted)
                self._rollback()
                return result
            if constraint == _EVIDENCE_ASSESSMENT_INDEX:
                result = self._replay_or_spent(submitted)
                self._rollback()
                return result
            raise
        if not submitted.counted and self._scalar("recorded-only-key-taken", {"evidence_id": submitted.evidence_id}):
            self._execute("rollback-to-savepoint")
            return self._record_duplicate(submitted)
        self._execute("release-savepoint")
        return None

    def _record_duplicate(self, submitted: ConfidenceUpdate) -> LifecycleCommitResult:
        """DUPLICATE: the key is taken, so the submission is recorded only, with nothing else written."""
        import psycopg

        event = self._event
        current = self._fetch("lock-revision-and-status", self._record_parameters())
        if current is None:
            self._rollback()
            return LifecycleCommitResult("NotFound", 0, None)
        revision, status, deleted_at = int(current[0]), current[1], current[2]
        if deleted_at is not None:
            self._rollback()
            return LifecycleCommitResult("Deleted", revision, None)
        if revision != event.expected_revision:
            self._rollback()
            return LifecycleCommitResult("StaleRevision", revision, None)
        if event.prior_status is not None and status != event.prior_status:
            self._rollback()
            return LifecycleCommitResult("StatusMismatch", revision, status)
        # A port only: checked under the row lock "lock-revision-and-status" just took.
        if self._sealed():
            self._rollback()
            return LifecycleCommitResult("NotFound", 0, None)
        recorded_only = submitted.as_recorded_only()
        try:
            self._insert_one(recorded_only, None, revision, status)
        except psycopg.Error as failure:
            constraint = _constraint(failure)
            if constraint == _EVIDENCE_PRIMARY_KEY:
                self._execute("rollback-to-savepoint")
                result = self._compare_stored_evidence(submitted)
                self._rollback()
                return result
            if constraint == _EVIDENCE_ASSESSMENT_INDEX:
                result = self._replay_or_spent(submitted)
                self._rollback()
                return result
            raise
        self._execute("release-savepoint")
        self._commit()
        return LifecycleCommitResult("Committed", revision, status, (), recorded_only)

    def _replay_or_spent(self, submitted: ConfidenceUpdate) -> LifecycleCommitResult:
        self._execute("rollback-to-savepoint")
        if self._scalar("evidence-id-taken", {"evidence_id": submitted.evidence_id, **self._scope_parameters}):
            return self._compare_stored_evidence(submitted)
        return LifecycleCommitResult("Conflict", 0, None, (assessment_spent_error(),))

    def _compare_stored_evidence(self, submitted: ConfidenceUpdate) -> LifecycleCommitResult:
        """EVIDENCE REPLAY: the same evidence about the same observation is the original replayed."""
        from dataclasses import replace

        event = self._event
        row = self._fetch("select-evidence", {"evidence_id": submitted.evidence_id, **self._scope_parameters})
        if row is None or _uuid(row[0]) != event.experience_record_id:
            return LifecycleCommitResult("Conflict", 0, None)
        same_content = (
            (row[1] is None or _uuid(row[1]) == event.event_id)
            and row[2] == submitted.kind
            and row[3] == submitted.source
            and _uuid(row[4]) == submitted.run_id
            and (None if row[5] is None else _uuid(row[5])) == submitted.verification_round_id
            and row[6] == submitted.reviewer_identity
            and row[10] == submitted.rule_version
            and row[11] == submitted.detail
            and (None if row[18] is None else _uuid(row[18])) == submitted.assessment_id
        )
        if not same_content:
            return LifecycleCommitResult("Conflict", 0, None)
        stored = replace(
            submitted,
            prior_reuse_confidence=float(row[12]),
            new_reuse_confidence=float(row[13]),
            prior_supporting_validations=int(row[14]),
            new_supporting_validations=int(row[15]),
            prior_contradictions=int(row[16]),
            new_contradictions=int(row[17]),
            admission=row[19],
        )
        return LifecycleCommitResult("Committed", int(row[8]), row[9], (), stored)

    def _compare_stored_event(self) -> LifecycleCommitResult:
        """EVENT REPLAY: the same event, every field and the scope included, is the original replayed."""
        from dataclasses import replace

        event = self._event
        row = self._fetch("select-event", {"event_id": event.event_id})
        if row is None:
            return LifecycleCommitResult("Conflict", 0, None)
        stored, stored_scope = _decode_event(row)
        if stored.event.experience_record_id != event.experience_record_id or stored_scope != self._scope:
            return LifecycleCommitResult("Conflict", 0, None)
        resubmitted = replace(
            event,
            occurred_at=self._occurred_at,
            confidence=None if event.confidence is None else as_replay_of(event.confidence, stored.event.confidence),
        )
        if is_same_evidence(event.confidence, stored.event.confidence):
            resubmitted = replace(resubmitted, current_status=stored.event.current_status)
        if stored.event == resubmitted:
            return LifecycleCommitResult("Committed", stored.applied_revision, None, (), stored.event.confidence)
        return LifecycleCommitResult("Conflict", 0, None)

    def _stale_or_missing(self) -> LifecycleCommitResult:
        """STALE: the record moved on (or went) under this commit."""
        current = self._fetch("select-revision-and-status", self._record_parameters())
        if current is None:
            return LifecycleCommitResult("NotFound", 0, None)
        revision = int(current[0])
        if current[2] is not None:
            return LifecycleCommitResult("Deleted", revision, None)
        return LifecycleCommitResult("StaleRevision", revision, None)


# ---- Contract 1.17: the reuse-feedback ledger (sql/reuse-feedback.sql) ---------------------------------------------

_ERASED_EXPOSURE = "names an Experience Record that has been erased; nothing may be recorded against it again."


class PostgresExperienceReuseFeedbackStore:
    """
    The reuse-feedback ledger in PostgreSQL (.NET's ``PostgresExperienceReuseFeedbackStore``), running
    ``sql/reuse-feedback.sql`` as written, as the application role in a transaction that first declared its bounds.
    ``connect`` is as :class:`PostgresExperienceStore`'s; ``clock`` is the store's own clock, stamped (truncated to
    whole microseconds) as ``recorded_at``.

    A submission is validated, then authorized, then written with its exposures in one transaction at READ COMMITTED:
    the feedback ID's primary key is the arbiter, so the same submission from writers in any language lands once. A
    submission naming a record erased in its own scope is ``Invalid`` and writes nothing. A port holds no key store, so
    the rationale is written in plaintext (``rationale_sealed`` NULL); a submission a .NET store sealed (rationale
    ``(sealed)``) therefore never compares equal to one resubmitted here, and is a ``Conflict``. Any database error
    other than the outcomes the SQL header names raises, with nothing written.
    """

    def __init__(
        self, connect: str | Callable[[], psycopg.Connection[Any]], *, clock: Callable[[], Instant] = Instant.now
    ) -> None:
        import psycopg

        if isinstance(connect, str):
            conninfo = connect

            def opener() -> psycopg.Connection[Any]:
                return psycopg.connect(conninfo)

            self._connect: Callable[[], psycopg.Connection[Any]] = opener
        else:
            self._connect = connect
        self._declare = to_driver_placeholders(contract_sql(DECLARE_BOUNDS_SQL))
        self._statements = {
            key: to_driver_placeholders(text) for key, text in contract_statements(REUSE_FEEDBACK_SQL).items()
        }
        self._clock = clock

    def record(
        self, authorization: AuthorizationContext, feedback: RecordedExperienceReuseFeedback
    ) -> ExperienceReuseFeedbackStoreResult:
        """Writes one submission and all its exposures, or nothing, as ``sql/reuse-feedback.sql``'s header orders it."""
        if authorization is None or feedback is None:
            raise TypeError("authorization and feedback must not be None.")
        errors = validate_reuse_feedback(feedback)
        if errors:
            return ExperienceReuseFeedbackStoreResult("Invalid", None, errors)
        if not authorization.permits(feedback.scope):
            return ExperienceReuseFeedbackStoreResult("Denied", None)
        submitted = feedback
        feedback = normalize_reuse_feedback(feedback)
        recorded_at = self._clock().truncate_to_microseconds()
        with self._connect() as connection:
            # Explicit transactions: the isolation level is pinned here, never inherited from the connection.
            connection.autocommit = True
            with connection.cursor() as cursor:
                cursor.execute("BEGIN ISOLATION LEVEL READ COMMITTED")
                try:
                    cursor.execute(self._declare, authorization.bounds.parameters())
                    cursor.execute(
                        self._statements["insert-submission"], self._submission_parameters(feedback, recorded_at)
                    )
                    if cursor.fetchone() is None:
                        stored = self._read_stored(cursor, feedback.feedback_id)
                        cursor.execute("ROLLBACK")
                        if stored is not None and same_reuse_feedback(stored, feedback):
                            return ExperienceReuseFeedbackStoreResult("AlreadyRecorded", stored)
                        return ExperienceReuseFeedbackStoreResult(
                            "Conflict", stored if stored is not None and authorization.permits(stored.scope) else None
                        )

                    exposures = [exposure for exposure in feedback.exposures or () if exposure is not None]
                    cursor.execute(
                        self._statements["select-exposed-record-state"],
                        {
                            "experience_ids": list(dict.fromkeys(exposure.experience_id for exposure in exposures)),
                            **_scope_parameters(feedback.scope),
                        },
                    )
                    erased = {_uuid(row[0]) for row in cursor.fetchall() if row[1] is not None}
                    if erased:
                        cursor.execute("ROLLBACK")
                        return ExperienceReuseFeedbackStoreResult(
                            "Invalid",
                            None,
                            tuple(
                                StoreValidationError(f"Exposures[{ordinal}].ExperienceId", _ERASED_EXPOSURE)
                                for ordinal, exposure in enumerate(submitted.exposures or ())
                                if exposure is not None and exposure.experience_id in erased
                            ),
                        )

                    from psycopg.types.numeric import Int4

                    for ordinal, exposure in enumerate(exposures):
                        cursor.execute(
                            self._statements["insert-exposure"],
                            {
                                "feedback_id": feedback.feedback_id,
                                "experience_id": exposure.experience_id,
                                "ordinal": Int4(ordinal),
                                "attributed": exposure.attributed,
                                "evidence_id": exposure.evidence_id,
                                "rationale_sealed": None,
                            },
                        )
                    cursor.execute("COMMIT")
                    return ExperienceReuseFeedbackStoreResult("Recorded", feedback)
                except BaseException:
                    import contextlib

                    with contextlib.suppress(Exception):
                        cursor.execute("ROLLBACK")
                    raise

    @staticmethod
    def _submission_parameters(feedback: RecordedExperienceReuseFeedback, recorded_at: Instant) -> dict[str, Any]:
        measure = feedback.measure
        assert measure is not None
        evidence_ids = list(feedback.evidence_ids or ())
        return {
            "feedback_id": feedback.feedback_id,
            "run_id": feedback.run_id,
            **_scope_parameters(feedback.scope),
            "run_outcome": feedback.run_outcome,
            "claimed_benefit": feedback.claimed_benefit,
            "benefit": feedback.benefit,
            "attribution_source": feedback.attribution_source,
            "reviewer_identity": feedback.reviewer_identity,
            "evaluator_id": feedback.evaluator_id,
            "verification_round_id": feedback.verification_round_id,
            "assessment_id": feedback.assessment_id,
            "rationale": feedback.rationale,
            "evidence_ids": evidence_ids or None,
            "attributed_at": None if feedback.attributed_at is None else feedback.attributed_at.to_datetime(),
            "measure_kind": measure.kind,
            "measure_value": float(measure.value),
            "trial_label": feedback.trial_label,
            "observed_at": feedback.observed_at.to_datetime(),
            "recorded_at": recorded_at.to_datetime(),
        }

    def _read_stored(
        self, cursor: psycopg.Cursor[Any], feedback_id: uuid.UUID
    ) -> RecordedExperienceReuseFeedback | None:
        cursor.execute(self._statements["select-submission"], {"feedback_id": feedback_id})
        row = cursor.fetchone()
        if row is None:
            return None
        cursor.execute(self._statements["select-exposures"], {"feedback_id": feedback_id})
        exposures = tuple(
            ExperienceReuseExposure(_uuid(item[0]), bool(item[1]), None if item[2] is None else _uuid(item[2]))
            for item in cursor.fetchall()
        )
        return RecordedExperienceReuseFeedback(
            feedback_id=feedback_id,
            run_id=_uuid(row[0]),
            scope=Scope(row[1], row[2], row[3], row[4], row[5], row[6]),
            run_outcome=row[7],
            claimed_benefit=row[8],
            benefit=row[9],
            attribution_source=row[10],
            reviewer_identity=row[11],
            evaluator_id=row[12],
            verification_round_id=None if row[13] is None else _uuid(row[13]),
            assessment_id=None if row[14] is None else _uuid(row[14]),
            rationale=row[15],
            evidence_ids=tuple(_uuid(item) for item in row[16] or ()),
            attributed_at=None if row[17] is None else _instant(row[17]),
            measure=ReuseMeasure(row[18], float(row[19])),
            trial_label=row[20],
            observed_at=_instant(row[21]),
            exposures=exposures,
        )
