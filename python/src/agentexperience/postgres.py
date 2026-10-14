"""
The PostgreSQL store (the ``postgres`` extra): writes finalized records and their initial event, reads records back,
and runs the contract's text search, all as the application role inside a transaction that first declared its bounds.

It runs against a schema the .NET migrator applied (``ExperienceSchemaMigrator.MigrateAsync``, then
``ApplyApplicationRolePrivilegesAsync`` with row-level security on) and never creates or alters a schema object. The
two statements are the contract's SQL files, shipped as package data and run as written: only their ``@name``
placeholders are spelled the way the driver spells them.
"""

from __future__ import annotations

import datetime as _dt
import re
import uuid
from collections.abc import Callable, Iterator
from contextlib import contextmanager
from importlib import resources
from typing import TYPE_CHECKING, Any

from ._text import is_null_or_whitespace
from .finalize import FinalizedExperience
from .records import COLUMN_NAMES, PAYLOAD_VERSION, ExperienceRecord, Scope, from_columns, to_payload
from .search import AuthorizationBounds, SearchParameters, SearchRow, widen_real

if TYPE_CHECKING:
    import psycopg

TABLE = "agent_experience.experience_records"
EVENTS_TABLE = "agent_experience.lifecycle_events"

_PLACEHOLDER = re.compile(r"@([A-Za-z_][A-Za-z0-9_]*)")


def contract_sql(name: str) -> str:
    """One of the contract's SQL files, as the package ships it (``declare-bounds.sql``,
    ``search-experience-text.sql``)."""
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


DECLARE_BOUNDS_SQL = "declare-bounds.sql"
SEARCH_SQL = "search-experience-text.sql"

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
