"""
A PostgreSQL schema exactly as the .NET migrator leaves it, for the PostgreSQL tests.

The schema is never reimplemented here. Each test database is migrated by running the .NET migrator's own scripts
(``src/AgentExperience.Storage.Postgres/Migrations/*.sql``) in name order, one transaction per script, as the owner
role, which is what ``ExperienceSchemaMigrator.MigrateAsync`` does through DbUp (with variable substitution off).

What is replayed by hand is the application role's privileges. ``ExperienceSchemaMigrator.
ApplyApplicationRolePrivilegesAsync`` is .NET code, not a script, so :data:`APPLICATION_ROLE_PRIVILEGES` repeats
the statements it issues with ``EnableRowLevelSecurity = true`` on a fresh role (``ApplicationRolePrivileges.
Statements`` and ``RowLevelSecurityStatements``): USAGE on the schema, SELECT and INSERT on each table, UPDATE on the
columns the stores move, EXECUTE on the text search function, and row-level security enabled on every table. The
policies and helper functions it re-creates are the ones migration 0019 already created. If that .NET code changes,
this list must follow it; the search fixtures, which need row-level security and the EXECUTE grant, catch a drift.

The server comes from ``PGHOST`` (with ``PGPORT``, ``PGUSER``, ``PGPASSWORD``: a superuser connection, libpq's own
variables) when set, else from a Testcontainers ``postgres`` container (``AGENTEXPERIENCE_POSTGRES_MAJOR``, default 16).
With neither available the PostgreSQL tests are skipped.
"""

from __future__ import annotations

import hashlib
import os
import re
from collections.abc import Iterator
from dataclasses import dataclass
from typing import Any

from _contract import MIGRATIONS

OWNER = "contract_owner"
APPLICATION = "contract_app"
PASSWORD = "contract-role-password"
SCHEMA = "agent_experience"

_TABLES: tuple[tuple[str, tuple[str, ...]], ...] = (
    (
        "experience_records",
        ("contradictions", "reuse_confidence", "revision", "status", "supporting_validations", "updated_at"),
    ),
    ("experience_grants", ("revocation_reason", "revoked_at")),
    ("lifecycle_events", ()),
    ("experience_grant_events", ()),
    ("confidence_evidence", ()),
    ("reuse_feedback", ()),
    ("reuse_feedback_exposures", ()),
    ("experience_grant_access", ()),
)

TEXT_SEARCH_SIGNATURE = (
    "agent_experience.search_experience_text(text, text, text, text, text, text, text, text[], double precision, "
    "integer, boolean, boolean, integer)"
)


def _privileges(role: str) -> list[str]:
    statements = [f"GRANT USAGE ON SCHEMA {SCHEMA} TO {role}"]
    for table, update_columns in _TABLES:
        statements.append(f"GRANT SELECT, INSERT ON {SCHEMA}.{table} TO {role}")
        if update_columns:
            statements.append(f"GRANT UPDATE ({', '.join(update_columns)}) ON {SCHEMA}.{table} TO {role}")
    statements.append(f"GRANT EXECUTE ON FUNCTION {TEXT_SEARCH_SIGNATURE} TO {role}")
    statements.extend(f"ALTER TABLE {SCHEMA}.{table} ENABLE ROW LEVEL SECURITY" for table, _ in _TABLES)
    return statements


APPLICATION_ROLE_PRIVILEGES = _privileges(APPLICATION)


@dataclass(frozen=True)
class Server:
    host: str
    port: int
    user: str
    password: str

    def conninfo(self, database: str = "postgres", user: str | None = None, password: str | None = None) -> str:
        from psycopg.conninfo import make_conninfo

        return make_conninfo(
            host=self.host,
            port=self.port,
            dbname=database,
            user=user or self.user,
            password=password if user else self.password,
        )


def server() -> Iterator[Server]:
    """The server the tests run against; raises ``RuntimeError`` (which the fixture turns into a skip) when none."""
    if os.environ.get("PGHOST"):
        yield Server(
            os.environ["PGHOST"],
            int(os.environ.get("PGPORT", "5432")),
            os.environ.get("PGUSER", "postgres"),
            os.environ.get("PGPASSWORD", ""),
        )
        return

    try:
        import docker

        docker.from_env().ping()
    except Exception as unavailable:
        raise RuntimeError(f"Docker is not available ({type(unavailable).__name__}).") from unavailable

    try:
        from testcontainers.community.postgres import PostgresContainer
    except ImportError:
        from testcontainers.postgres import PostgresContainer

    major = os.environ.get("AGENTEXPERIENCE_POSTGRES_MAJOR") or "16"
    image = _apply_hub_image_prefix(f"postgres:{major}")
    with PostgresContainer(image, username="postgres", password="postgres", dbname="postgres") as pg:
        yield Server(pg.get_container_host_ip(), int(pg.get_exposed_port(5432)), "postgres", "postgres")


def _apply_hub_image_prefix(image: str) -> str:
    """
    ``image`` with ``TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX`` applied here rather than by testcontainers-python.

    testcontainers-python (4.x) prepends the prefix to every image it starts, the Reaper's (Ryuk) included, but
    recognises the Reaper by comparing that prefixed name with the unprefixed ``ryuk_image`` setting. With a prefix
    they never match, so starting the Reaper starts another Reaper, without end, until a ``RecursionError``. CI sets
    the prefix (a Docker Hub mirror), so this moves it into the two image names the tests use, the Reaper's and
    PostgreSQL's, and clears it: the Reaper then starts once and removes every container when the session ends.
    """
    from testcontainers.core.config import testcontainers_config

    prefix = testcontainers_config.hub_image_name_prefix
    if not prefix:
        return image
    testcontainers_config.hub_image_name_prefix = ""
    testcontainers_config.ryuk_image = prefix + testcontainers_config.ryuk_image
    return prefix + image


def prepare_roles(admin: Server) -> None:
    """The owner role that runs the migrations and the application role the stores use, as the .NET tests make them."""
    import psycopg

    with psycopg.connect(admin.conninfo(), autocommit=True) as connection:
        for role in (OWNER, APPLICATION):
            exists = connection.execute("SELECT 1 FROM pg_roles WHERE rolname = %s", (role,)).fetchone()
            if exists is None:
                connection.execute(f"CREATE ROLE {role} LOGIN PASSWORD '{PASSWORD}'")
        # What a non-superuser owner needs to run the migrations (the erasure scripts set these parameters).
        connection.execute(
            f"GRANT SET ON PARAMETER agent_experience.purge_authorized, agent_experience.access_purge_authorized "
            f"TO {OWNER}"
        )


def database_name(label: str) -> str:
    """A unique, valid database name for a label: a readable prefix and a hash of the whole label, within 63 bytes."""
    digest = hashlib.sha256(label.encode("utf-8")).hexdigest()[:12]
    prefix = re.sub(r"[^a-z0-9_]", "_", label.lower())[:40]
    return f"py_{prefix}_{digest}"


def create_database(admin: Server, label: str) -> str:
    """A fresh database, migrated by the .NET scripts and with the application role's privileges; its name."""
    import psycopg
    from psycopg import sql

    name = database_name(label)
    with psycopg.connect(admin.conninfo(), autocommit=True) as connection:
        connection.execute(sql.SQL("DROP DATABASE IF EXISTS {} WITH (FORCE)").format(sql.Identifier(name)))
        connection.execute(sql.SQL("CREATE DATABASE {} OWNER {}").format(sql.Identifier(name), sql.Identifier(OWNER)))

    scripts = sorted(MIGRATIONS.glob("*.sql"), key=lambda path: path.name)
    assert scripts, f"No migration scripts under {MIGRATIONS}."
    with psycopg.connect(admin.conninfo(name, OWNER, PASSWORD)) as owner:
        for script in scripts:
            with owner.transaction():
                owner.execute(script.read_text(encoding="utf-8").encode("utf-8"))
        with owner.transaction():
            for statement in APPLICATION_ROLE_PRIVILEGES:
                owner.execute(statement.encode("utf-8"))
    return name


def application_conninfo(admin: Server, database: str) -> str:
    return admin.conninfo(database, APPLICATION, PASSWORD)


def server_major(admin: Server) -> Any:
    import psycopg

    with psycopg.connect(admin.conninfo()) as connection:
        row = connection.execute("SHOW server_version_num").fetchone()
        assert row is not None
        return int(row[0]) // 10000
