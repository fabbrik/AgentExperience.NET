"""
A PostgreSQL schema exactly as the .NET migrator leaves it, for the PostgreSQL tests.

The schema is never reimplemented here. Each test database is migrated by running the .NET migrator's own scripts
(``src/AgentExperience.Storage.Postgres/Migrations/*.sql``) in name order, one transaction per script, as the owner
role, which is what ``ExperienceSchemaMigrator.MigrateAsync`` does through DbUp (with variable substitution off).

The application role's privileges are the contract's ``sql/application-role-privileges.sql`` then, from contract 1.4,
``sql/application-role-privileges-1.4.sql`` (the injection table's): together the statements
``ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync`` runs on a freshly migrated schema, which a .NET test
keeps identical to that method's. Both of their modes are applied (``EnableRowLevelSecurity = true``, what a contract
1.x port runs against), with the role's name in place of the ``:"application_role"`` placeholder.

The server comes from ``PGHOST`` (with ``PGPORT``, ``PGUSER``, ``PGPASSWORD``: a superuser connection, libpq's own
variables) when set, else from a Testcontainers ``postgres`` container (``AGENTEXPERIENCE_POSTGRES_MAJOR``, default 16).
With neither available the PostgreSQL tests are skipped.
"""

from __future__ import annotations

import hashlib
import os
import re
from collections.abc import Generator
from dataclasses import dataclass
from datetime import timedelta
from pathlib import Path
from typing import Any

from _contract import CONTRACT, MIGRATIONS

from agentexperience import RetrievalPolicy

PG_RETRIEVAL_POLICY = RetrievalPolicy(timeout=timedelta(seconds=30))
"""The retrieval policy every PostgreSQL agent test runs injection under: a timeout generous enough that a cold
Testcontainers server cannot time a retrieval out (the default is 500 ms, contract 1.12)."""

OWNER = "contract_owner"
APPLICATION = "contract_app"
PASSWORD = "contract-role-password"
SCHEMA = "agent_experience"

PRIVILEGES_SQL = CONTRACT / "sql" / "application-role-privileges.sql"
PRIVILEGES_1_4_SQL = CONTRACT / "sql" / "application-role-privileges-1.4.sql"
PRIVILEGES_1_6_SQL = CONTRACT / "sql" / "application-role-privileges-1.6.sql"
PRIVILEGES_1_8_SQL = CONTRACT / "sql" / "application-role-privileges-1.8.sql"
PRIVILEGES_1_11_SQL = CONTRACT / "sql" / "application-role-privileges-1.11.sql"
VECTOR_MIGRATIONS = MIGRATIONS.parent.parent / "AgentExperience.Storage.Postgres.Vectors" / "Migrations"
_ROLE_PLACEHOLDER = ':"application_role"'


def application_role_privileges(
    role: str, *, row_level_security: bool = True, path: Path = PRIVILEGES_SQL
) -> list[str]:
    """
    The statements of a contract privileges file (``sql/application-role-privileges.sql`` by default) for ``role`` (a
    plain identifier): those under ``-- mode: always``, then, with ``row_level_security``, those under
    ``-- mode: row-level-security``.
    """
    assert re.fullmatch(r"[a-z_][a-z0-9_]*", role), role
    modes: dict[str, list[str]] = {"always": [], "row-level-security": []}
    current: list[str] | None = None
    for line in path.read_text(encoding="utf-8").splitlines():
        if re.match(r"--\s*mode\b", line, re.IGNORECASE):
            mode = line.removeprefix("-- mode: ")
            if not line.startswith("-- mode: ") or mode not in modes:
                raise AssertionError(
                    f"Unknown mode marker {line!r}: the markers are '-- mode: always' and "
                    "'-- mode: row-level-security'."
                )
            current = modes[mode]
        elif line and not line.startswith("--"):
            if current is None:
                raise AssertionError(f"A statement before any mode marker: {line!r}")
            if not line.endswith(";") or line.index(";") != len(line) - 1:
                raise AssertionError(f"Not one statement on one line ending in ';': {line!r}")
            current.append(line[:-1].replace(_ROLE_PLACEHOLDER, role))
    return modes["always"] + (modes["row-level-security"] if row_level_security else [])


APPLICATION_ROLE_PRIVILEGES_1_0 = application_role_privileges(APPLICATION)
"""Contract 1.0's grants alone: the injection table is not granted."""

APPLICATION_ROLE_PRIVILEGES = APPLICATION_ROLE_PRIVILEGES_1_0 + application_role_privileges(
    APPLICATION, path=PRIVILEGES_1_4_SQL
)
"""The 1.0 file, then the 1.4 file: what the .NET privileges call grants on the base schema."""

APPLICATION_ROLE_PRIVILEGES_1_6 = APPLICATION_ROLE_PRIVILEGES + application_role_privileges(
    APPLICATION, path=PRIVILEGES_1_6_SQL
)
"""The 1.0 file, then the 1.4 file, then the 1.6 file (the embeddings table's): what the .NET privileges call grants
on a schema with the vectors migrations applied."""

APPLICATION_ROLE_PRIVILEGES_1_8 = APPLICATION_ROLE_PRIVILEGES_1_6 + application_role_privileges(
    APPLICATION, path=PRIVILEGES_1_8_SQL
)
"""The 1.0, 1.4 and 1.6 files, then the 1.8 file (the holdout assignment table's, contract 1.8): what the .NET
privileges call grants on the tables a contract 1.8 port reads and writes."""

APPLICATION_ROLE_PRIVILEGES_1_11 = APPLICATION_ROLE_PRIVILEGES_1_8 + application_role_privileges(
    APPLICATION, path=PRIVILEGES_1_11_SQL
)
"""The 1.0, 1.4, 1.6 and 1.8 files, then the 1.11 file (the provider holdout table's, contract 1.11): what the .NET
privileges call grants on every table a contract 1.11 port reads and writes."""


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


def server() -> Generator[Server, None, None]:
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
    # pgvector's image: the vector channel (contract 1.6) needs the extension.
    image = _apply_hub_image_prefix(f"pgvector/pgvector:pg{major}")
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


def create_database(admin: Server, label: str, *, privileges: list[str] | None = None) -> str:
    """A fresh database, migrated by the .NET scripts (the vectors package's too, after ``CREATE EXTENSION vector`` as a
    superuser) and with the application role's privileges (the five contract files unless ``privileges`` says
    otherwise); its name."""
    import psycopg
    from psycopg import sql

    name = database_name(label)
    with psycopg.connect(admin.conninfo(), autocommit=True) as connection:
        connection.execute(sql.SQL("DROP DATABASE IF EXISTS {} WITH (FORCE)").format(sql.Identifier(name)))
        connection.execute(sql.SQL("CREATE DATABASE {} OWNER {}").format(sql.Identifier(name), sql.Identifier(OWNER)))

    scripts = sorted(MIGRATIONS.glob("*.sql"), key=lambda path: path.name)
    assert scripts, f"No migration scripts under {MIGRATIONS}."
    vector_scripts = sorted(VECTOR_MIGRATIONS.glob("*.sql"), key=lambda path: path.name)
    assert vector_scripts, f"No migration scripts under {VECTOR_MIGRATIONS}."
    with psycopg.connect(admin.conninfo(name), autocommit=True) as superuser:
        # pgvector is not a trusted extension: a superuser installs it, as an operator does (contract 1.6).
        superuser.execute("CREATE EXTENSION IF NOT EXISTS vector")
    with psycopg.connect(admin.conninfo(name, OWNER, PASSWORD)) as owner:
        for script in [*scripts, *vector_scripts]:
            with owner.transaction():
                owner.execute(script.read_text(encoding="utf-8").encode("utf-8"))
        with owner.transaction():
            for statement in APPLICATION_ROLE_PRIVILEGES_1_11 if privileges is None else privileges:
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
