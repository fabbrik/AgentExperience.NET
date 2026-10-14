"""Running an awaitable to completion from a synchronous test."""

from __future__ import annotations

import asyncio
from collections.abc import Awaitable
from typing import TypeVar

T = TypeVar("T")


def run(awaitable: Awaitable[T]) -> T:
    """Runs ``awaitable`` on a new event loop and returns its result (``asyncio.run`` takes only a coroutine)."""

    async def wait() -> T:
        return await awaitable

    return asyncio.run(wait())
