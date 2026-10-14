from __future__ import annotations

from collections.abc import Iterator

import pytest


@pytest.fixture(scope="session")
def pg_server() -> Iterator[object]:
    """A PostgreSQL server with the contract's roles; the PostgreSQL tests skip when there is none."""
    import os

    from _postgres import prepare_roles, server, server_major

    servers = server()
    try:
        admin = next(servers)
    except RuntimeError as unavailable:
        if os.environ.get("AGENTEXPERIENCE_REQUIRE_POSTGRES") == "1":
            # A CI leg that exists to run these tests must fail rather than skip them.
            pytest.fail(f"PostgreSQL is required but not available: {unavailable}")
        pytest.skip(f"PostgreSQL is not available: {unavailable}")
    try:
        requested = os.environ.get("AGENTEXPERIENCE_POSTGRES_MAJOR")
        if requested:
            # A leg must not quietly run another major than the one it names.
            assert server_major(admin) == int(requested), "The server is not the requested PostgreSQL major."
        prepare_roles(admin)
        yield admin
    finally:
        servers.close()
