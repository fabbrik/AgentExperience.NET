"""
Who a run is for: the identity an invocation runs under, and the development-only identity.

In production the identity comes from the host's own authentication, never from model output. The development identity
runs every invocation as one fixed caller in one fixed scope, so it refuses outside a development or test environment,
by the same rule as the .NET library: the environment is allowed when its name is ``Development``, ``Test`` or
``Testing`` (case-insensitive). The name is the one the host passes, else the Python-native
``AGENTEXPERIENCE_ENVIRONMENT`` variable, else .NET's ``DOTNET_ENVIRONMENT``, else ``ASPNETCORE_ENVIRONMENT``; with
none of them there is nothing to check, so it is allowed.
"""

from __future__ import annotations

import os
import sys
from collections.abc import Mapping
from dataclasses import dataclass, field
from pathlib import Path

from ._text import is_null_or_whitespace
from .records import Scope
from .search import AuthorizationBounds

ALLOWED_ENVIRONMENTS: tuple[str, ...] = ("Development", "Test", "Testing")
"""The environment names the development identity runs in, compared case-insensitively."""

ENVIRONMENT_VARIABLES: tuple[str, ...] = ("AGENTEXPERIENCE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT")
"""The variables the environment name is read from, in order: the Python-native one first, then .NET's."""

DEVELOPMENT_TENANT = "local"
DEVELOPMENT_PRINCIPAL = "local-developer"
DEVELOPMENT_APPLICATION = "app"
MAX_NAME_LENGTH = 100


class DevelopmentOnlyError(RuntimeError):
    """A development-only convenience was used outside a development or test environment."""


@dataclass(frozen=True)
class ExperienceIdentity:
    """
    The identity an invocation runs under: the principal it is authorized as, the scope experience is read from and
    stored under, and the authorization bounds every store transaction declares (by default, bounds equal to the
    scope). It must come from the host's own authentication, never from model output.
    """

    principal: str
    scope: Scope
    bounds: AuthorizationBounds | None = None
    development: bool = field(default=False, compare=False)
    environment_name: str | None = field(default=None, compare=False)
    """For a development identity, the host environment name it was created under (``None``: read the variables)."""

    def __post_init__(self) -> None:
        if not isinstance(self.principal, str) or is_null_or_whitespace(self.principal):
            raise ValueError("The principal must not be blank.")
        if not isinstance(self.scope, Scope):
            raise TypeError("The scope must be a Scope.")

    @property
    def authorization(self) -> AuthorizationBounds:
        """The bounds every transaction declares: the given ones, else bounds equal to the scope."""
        return self.bounds if self.bounds is not None else AuthorizationBounds.of(self.scope)


def read_environment_name(environment: Mapping[str, str] | None = None) -> tuple[str | None, str]:
    """The environment's name and where it was read from: ``AGENTEXPERIENCE_ENVIRONMENT``, else the .NET rule's
    variables; ``(None, "nothing")`` with none set."""
    variables = os.environ if environment is None else environment
    for variable in ENVIRONMENT_VARIABLES:
        value = variables.get(variable)
        if value is not None:
            return value, variable
    return None, "nothing"


def is_development_environment(name: str | None = None, *, environment: Mapping[str, str] | None = None) -> bool:
    """
    Whether the process runs in a development or test environment. ``name``, when given, is the host's own environment
    name (like .NET's host environment) and is checked alone; otherwise :data:`ENVIRONMENT_VARIABLES` are read in
    order, and with none set it is allowed.
    """
    if name is None:
        name, source = read_environment_name(environment)
        if source == "nothing":
            return True
    return name is not None and name.casefold() in {allowed.casefold() for allowed in ALLOWED_ENVIRONMENTS}


def ensure_development_environment(name: str | None = None, *, environment: Mapping[str, str] | None = None) -> None:
    """Raises :class:`DevelopmentOnlyError` outside a development or test environment."""
    if is_development_environment(name, environment=environment):
        return
    if name is None:
        name, source = read_environment_name(environment)
    else:
        source = "the host environment"
    raise DevelopmentOnlyError(
        "development_identity() is for development and tests only: every invocation runs as one fixed caller in one "
        "fixed scope, so everything stored is shared by everyone using the process. It runs only when the environment "
        f"is {', '.join(ALLOWED_ENVIRONMENTS)}, and {source} is '{name}'. Resolve the identity from your own "
        "authentication instead."
    )


def reduce_name(name: str | None) -> str | None:
    """
    Reduces a name the host did not choose as an identifier (an agent's name, the program's name) to one: lower case,
    ASCII letters, digits, ``.`` and ``_`` kept, every run of other characters (dashes included) one ``-``, no
    separator at either end, at most 100 characters, never fusing two words at the cut. ``None`` when nothing is kept.
    """
    if name is None or is_null_or_whitespace(name):
        return None
    text: list[str] = []
    pending_separator = False
    for ch in name:
        if (ch.isascii() and ch.isalnum()) or ch in "._":
            needed = 2 if pending_separator and text else 1
            if len(text) + needed > MAX_NAME_LENGTH:
                # Stop rather than fuse two words or cut the separator off its word.
                break
            if needed == 2:
                text.append("-")
            pending_separator = False
            text.append(ch.lower())
        else:
            pending_separator = True
    reduced = "".join(text).strip("._-")
    return reduced or None


def _program_name() -> str | None:
    main = sys.modules.get("__main__")
    path = getattr(main, "__file__", None) or (sys.argv[0] if sys.argv and sys.argv[0] else None)
    return Path(path).stem if path else None


def development_identity(
    tenant: str | None = None,
    *,
    application: str | None = None,
    environment_name: str | None = None,
) -> ExperienceIdentity:
    """
    One fixed local identity, for development and tests only: tenant ``local`` (or ``tenant``), principal
    ``local-developer``, and the application and project named after ``application``, else the program's file name,
    else ``app`` (each reduced to an identifier). Raises :class:`DevelopmentOnlyError` outside a development or test
    environment; ``environment_name`` is the host's own environment name, when it has one.
    """
    if tenant is not None and is_null_or_whitespace(tenant):
        raise ValueError("The development tenant may not be blank; pass None for the default, 'local'.")
    ensure_development_environment(environment_name)
    segment = reduce_name(application) or reduce_name(_program_name()) or DEVELOPMENT_APPLICATION
    chosen = tenant if tenant is not None else DEVELOPMENT_TENANT
    return ExperienceIdentity(
        DEVELOPMENT_PRINCIPAL, Scope(chosen, segment, segment), development=True, environment_name=environment_name
    )
