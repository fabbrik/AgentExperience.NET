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
import re
import uuid
from collections.abc import Callable, Iterator, Sequence
from contextlib import contextmanager
from importlib import resources
from typing import TYPE_CHECKING, Any

from ._text import is_null_or_whitespace
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
from .inmemory import (
    AuthorizationDenied,
    InjectionSummary,
    LessonRow,
    LessonsRead,
    check_lessons_read,
    injection_ids,
    permits,
)
from .records import COLUMN_NAMES, PAYLOAD_VERSION, ExperienceRecord, Scope, from_columns, to_payload
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
    operation uses one connection and one transaction.
    """

    def __init__(self, connect: str | Callable[[], psycopg.Connection[Any]]) -> None:
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
        self._write_statements = {
            key: to_driver_placeholders(text) for key, text in contract_statements(WRITE_EMBEDDING_SQL).items()
        }
        # The vector search statements keep their ``{dim}`` until the query vector's length is known.
        self._vector_statements = contract_statements(SEARCH_VECTOR_SQL)
        self._lessons_statements = {
            key: to_driver_placeholders(text) for key, text in contract_statements(LIST_LESSONS_SQL).items()
        }

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
