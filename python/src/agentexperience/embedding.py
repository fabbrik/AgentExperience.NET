"""
Semantic retrieval (contract 1.6): the embedder the caller brings, what is embedded and how it is hashed, the
validation that runs before any statement, the write and search types a store serves, and the pure functions that turn
two channels' candidates into one list.

No embedding model is a dependency of this package: an :class:`Embedder` is the caller's own object.
"""

from __future__ import annotations

import asyncio
import hashlib
import inspect
import math
import struct
import threading
import unicodedata
import uuid
from collections.abc import Awaitable, Callable, Mapping, Sequence
from dataclasses import dataclass
from typing import Generic, Literal, Protocol, TypeVar, runtime_checkable

from ._text import format_round_trip, is_null_or_whitespace, utf16_len
from .encoding import Instant
from .records import ExperienceRecord, ExperienceStatus, Scope
from .retrieval import DEFAULT_POLICY, ConfidenceDecayPolicy, RetrievalPolicy
from .search import (
    CANDIDATE_LIMIT,
    DEFAULT_MIN_CONFIDENCE,
    ELIGIBLE_STATUSES,
    STATEMENT_LIMIT,
    RankedResult,
    SearchRow,
    check_rows,
    rank_checked,
)

MAX_SUMMARY_LENGTH = 8192
"""The longest summary that is embedded, in UTF-16 code units."""

MAX_MODEL_ID_LENGTH = 256
MAX_DIMENSION = 16_000
"""The most components a vector has (pgvector's ``vector`` type accepts up to 16,000)."""

_SEPARATORS = frozenset({"Cc", "Zs", "Zl", "Zp"})


@runtime_checkable
class Embedder(Protocol):
    """
    Turns text into a vector: the caller's callable, registered with ``use_agent_experience(embedder=...)``.

    ``model_id`` and ``dimension`` are known without calling the provider; the vector ``embed`` returns has exactly
    ``dimension`` finite components. ``embed`` may be a plain function (it then runs on a worker thread) or a coroutine
    function. Embeddings of different ``model_id`` or ``dimension`` are never compared.
    """

    @property
    def model_id(self) -> str: ...

    @property
    def dimension(self) -> int: ...

    def embed(self, text: str) -> Sequence[float] | Awaitable[Sequence[float]]: ...


FallbackReason = Literal[
    "NotConfigured", "ProviderUnavailable", "ModelMismatch", "DimensionMismatch", "VectorSearchFailed"
]
"""Why an injection answered from text alone (contract 1.6)."""


@dataclass(frozen=True)
class VectorFallback:
    """The vector channel did not contribute: the contract's reason, a sentence that never quotes record or vector
    content, and the exception, when there was one."""

    reason: FallbackReason
    detail: str
    exception: BaseException | None = None


# ---- What is embedded ---------------------------------------------------------------------------------------------


def _append(parts: list[str], value: str | None) -> None:
    if value is None:
        return
    words: list[str] = []
    word: list[str] = []
    for ch in value:
        if unicodedata.category(ch) in _SEPARATORS:
            if word:
                words.append("".join(word))
                word = []
        else:
            word.append(ch)
    if word:
        words.append("".join(word))
    if words:
        parts.append(" ".join(words))


def retrieval_summary(task_id: str | None, task_summary: str | None, lesson: str | None) -> str:
    """
    The only text of a record that is embedded: task ID, task summary and lesson, each with every run of separators
    (Unicode categories Cc, Zs, Zl, Zp) as one space, joined by one space, at most 8,192 UTF-16 code units.
    """
    parts: list[str] = []
    for value in (task_id, task_summary, lesson):
        _append(parts, value)
    text = " ".join(parts)
    if utf16_len(text) <= MAX_SUMMARY_LENGTH:
        return text
    # Cut at 8,192 code units; a cut that would end on a high surrogate drops it too; then trailing spaces go.
    kept: list[str] = []
    used = 0
    for ch in text:
        size = 2 if ord(ch) > 0xFFFF else 1
        if used + size > MAX_SUMMARY_LENGTH:
            break
        kept.append(ch)
        used += size
    return "".join(kept).rstrip(" ")


def record_summary(record: ExperienceRecord) -> str:
    """:func:`retrieval_summary` of a record."""
    return retrieval_summary(
        record.task_id, record.task_summary, record.reflection.lesson if record.reflection else None
    )


def content_hash(model_id: str, summary: str) -> str:
    """Lowercase hex SHA-256 of the UTF-8 bytes of the model ID, U+001F and the summary."""
    text = model_id + "\u001f" + summary
    # A lone surrogate is U+FFFD in UTF-8, as .NET encodes it.
    data = text.encode("utf-16-le", "surrogatepass").decode("utf-16-le", "replace").encode("utf-8")
    return hashlib.sha256(data).hexdigest()


MAX_VECTOR_THREADS = 16
"""How many embedder or vector-store calls may run at once, abandoned (timed-out) ones included. They run on daemon
threads of their own, never the event loop's default executor that the text search uses, so a hung provider can never
starve the text channel; past this a call fails fast."""

_vector_threads = threading.BoundedSemaphore(MAX_VECTOR_THREADS)


async def run_on_vector_thread(function: Callable[..., object], *args: object) -> object:
    """Runs ``function`` on a daemon thread of its own and awaits it. Raises ``RuntimeError`` at once when
    :data:`MAX_VECTOR_THREADS` calls are already running. A call the caller stops waiting for keeps its thread
    (a thread cannot be stopped) until it ends."""
    if not _vector_threads.acquire(blocking=False):
        raise RuntimeError("Too many earlier embedding or vector-search calls are still running.")
    loop = asyncio.get_running_loop()
    future: asyncio.Future[object] = loop.create_future()

    def settle(value: object, error: BaseException | None) -> None:
        if not future.done():
            if error is None:
                future.set_result(value)
            else:
                future.set_exception(error)

    def run() -> None:
        try:
            try:
                value, error = function(*args), None
            except BaseException as ex:
                value, error = None, ex
            try:
                loop.call_soon_threadsafe(settle, value, error)
            except RuntimeError:  # the loop closed first
                pass
        finally:
            _vector_threads.release()

    threading.Thread(target=run, name="agentexperience-vector", daemon=True).start()
    return await future


async def embed_text(embedder: Embedder, text: str) -> Sequence[float]:
    """The embedder's vector for ``text``: a coroutine function is awaited, a plain function runs on a worker thread
    (so a blocking provider never stalls the event loop)."""
    if inspect.iscoroutinefunction(embedder.embed):
        result = await embedder.embed(text)
    else:
        result = await run_on_vector_thread(embedder.embed, text)
        if inspect.isawaitable(result):
            result = await result
    return result  # type: ignore[no-any-return]


# ---- Validation (before any statement) -----------------------------------------------------------------------------


def validate_model_id(model_id: str) -> None:
    if not isinstance(model_id, str) or is_null_or_whitespace(model_id):
        raise ValueError("The model ID must not be blank.")
    if utf16_len(model_id) > MAX_MODEL_ID_LENGTH:
        raise ValueError(f"The model ID is longer than {MAX_MODEL_ID_LENGTH} UTF-16 code units.")


def float32(value: float) -> float:
    """``value`` rounded to the nearest 32-bit float; ``ValueError`` when that is not finite."""
    try:
        rounded = float(struct.unpack("<f", struct.pack("<f", value))[0])
    except OverflowError as overflow:
        raise ValueError("A vector component is outside the 32-bit float range.") from overflow
    if not math.isfinite(rounded):
        raise ValueError("A vector component must be finite.")
    return rounded


def validate_vector(vector: Sequence[float], dimension: int | None = None) -> tuple[float, ...]:
    """The vector as floats; ``ValueError`` unless it has 1 to 16,000 finite components (and ``dimension`` of them,
    when given)."""
    try:
        components = tuple(float(component) for component in vector)
    except (TypeError, ValueError, OverflowError) as bad:
        raise ValueError("A vector must be a sequence of numbers.") from bad
    if not 1 <= len(components) <= MAX_DIMENSION:
        raise ValueError(f"A vector has 1 to {MAX_DIMENSION} components.")
    if dimension is not None and len(components) != dimension:
        raise ValueError("The vector's length is not its dimension.")
    for component in components:
        float32(component)
        if not math.isfinite(component):
            raise ValueError("A vector component must be finite.")
    return components


def vector_literal(vector: Sequence[float]) -> str:
    """A pgvector literal, ``[x,y,...]``, each component its shortest round-trip text."""
    return "[" + ",".join(format_round_trip(float(component)) for component in vector) + "]"


@dataclass(frozen=True)
class EmbeddingWrite:
    """One record's embedding to store: the record (its ID, exact scope and the revision the summary was read at) and
    what the embedding is."""

    experience_id: uuid.UUID
    scope: Scope
    model_id: str
    dimension: int
    content_hash: str
    source_revision: int
    vector: tuple[float, ...]
    now: Instant

    def __post_init__(self) -> None:
        validate_model_id(self.model_id)
        if (
            not isinstance(self.dimension, int)
            or isinstance(self.dimension, bool)
            or not 1 <= self.dimension <= MAX_DIMENSION
        ):
            raise ValueError(f"The dimension must be 1 to {MAX_DIMENSION}.")
        validate_vector(self.vector, self.dimension)
        if is_null_or_whitespace(self.content_hash):
            raise ValueError("The content hash must not be blank.")
        if not isinstance(self.source_revision, int) or self.source_revision < 0:
            raise ValueError("The source revision must not be negative.")


@dataclass(frozen=True)
class VectorQuery:
    """A nearest-neighbour search in exactly ``scope``, over embeddings of ``model_id`` and the vector's length."""

    scope: Scope
    model_id: str
    vector: tuple[float, ...]
    statuses: tuple[ExperienceStatus, ...] = ELIGIBLE_STATUSES
    min_confidence: float = DEFAULT_MIN_CONFIDENCE
    limit: int = STATEMENT_LIMIT
    exclude_model_authored: bool = False

    def __post_init__(self) -> None:
        validate_model_id(self.model_id)
        validate_vector(self.vector)
        if self.limit < 1:
            raise ValueError("The limit must be strictly positive.")


EmbeddingWriteOutcome = Literal["Written", "Stale", "Missing"]


@dataclass(frozen=True)
class EmbeddingWriteResult:
    """``Written``; ``Stale`` with the record's current revision (it moved on, nothing changed); or ``Missing`` (an
    unknown, other-scope or erased record, nothing created)."""

    outcome: EmbeddingWriteOutcome
    current_revision: int | None = None


VectorSearchOutcome = Literal["Found", "ModelMismatch", "DimensionMismatch"]


def relevance_of(distance: float) -> float:
    """``clamp(1 - distance / 2, 0, 1)``, and 0 for NaN."""
    if math.isnan(distance):
        return 0.0
    return min(max(1.0 - distance / 2.0, 0.0), 1.0)


@dataclass(frozen=True)
class VectorHit:
    """One record the vector search returned, and its cosine distance (NaN for a zero-magnitude vector)."""

    record: ExperienceRecord
    distance: float

    @property
    def relevance(self) -> float:
        return relevance_of(self.distance)


@dataclass(frozen=True)
class VectorSearchResult:
    """The outcome, and the hits nearest first (ties by experience ID ordinal, NaN last); no hits unless ``Found``."""

    outcome: VectorSearchOutcome
    hits: tuple[VectorHit, ...] = ()


def order_hits(hits: Sequence[VectorHit]) -> list[VectorHit]:
    """Nearest first; equal distances by experience ID as text, ordinally; NaN after every number, equal to each
    other."""
    return sorted(
        hits,
        key=lambda hit: (
            math.isnan(hit.distance),
            0.0 if math.isnan(hit.distance) else hit.distance,
            str(hit.record.experience_id),
        ),
    )


# ---- Merging the two channels --------------------------------------------------------------------------------------

T = TypeVar("T")

Channel = Literal["text", "vector"]


@dataclass(frozen=True)
class Merged(Generic[T]):
    """One entry of the merged list: the candidate kept (the first channel's), the relevance kept, and its channel."""

    item: T
    relevance: float
    source: Channel


def _clamped(relevance: float) -> float:
    return 0.0 if math.isnan(relevance) else min(max(relevance, 0.0), 1.0)


def merge_channels(
    text: Sequence[T],
    vector: Sequence[T],
    key: Callable[[T], uuid.UUID],
    relevance: Callable[[T], float],
    minimum_vector_relevance: float | None = None,
) -> list[Merged[T]]:
    """
    Contract 1.6 steps 1 and 3. The floor drops vector candidates below ``minimum_vector_relevance`` (one exactly at it
    stays; a NaN relevance is below any floor; text is never floored). The merge lists the text candidates in order,
    then the vector candidates in theirs; a record already listed keeps its position and the text channel's candidate,
    and takes the vector relevance, as given, only when its clamped value (NaN as 0) is higher.
    """
    merged: list[Merged[T]] = []
    positions: dict[uuid.UUID, int] = {}
    for item in text:
        positions.setdefault(key(item), len(merged))
        merged.append(Merged(item, relevance(item), "text"))
    for item in vector:
        value = relevance(item)
        if minimum_vector_relevance is not None and not value >= minimum_vector_relevance:
            continue
        position = positions.get(key(item))
        if position is None:
            positions[key(item)] = len(merged)
            merged.append(Merged(item, value, "vector"))
        elif _clamped(value) > _clamped(merged[position].relevance):
            merged[position] = Merged(merged[position].item, value, merged[position].source)
    return merged


def check_channel(rows: Sequence[SearchRow], scope: Scope, candidate_limit: int = CANDIDATE_LIMIT) -> list[SearchRow]:
    """Contract 1.6 step 2 for one channel: over its first ``candidate_limit`` candidates (50 by default), one outside
    ``scope`` or a repeated ``experience_id`` raises :class:`SearchSafetyError`; returns those candidates."""
    return check_rows(rows, scope, candidate_limit)


def rank_hybrid(
    text_rows: Sequence[SearchRow],
    vector_rows: Sequence[SearchRow],
    scope: Scope,
    now: Instant,
    preferred_environment: Mapping[str, str] | None = None,
    *,
    minimum_vector_relevance: float | None = None,
    exclude_model_authored: bool = False,
    policy: RetrievalPolicy | None = None,
    confidence_decay: ConfidenceDecayPolicy | None = None,
) -> RankedResult:
    """
    The whole of steps 1 to 4: floor, per-channel checks and truncation (at the policy's candidate limit, 50 by
    default), merge, then eligibility and ranking over the merged candidates (each with the relevance kept), as
    :func:`~agentexperience.search.rank` ranks. ``minimum_vector_relevance`` is the floor (``None``: the policy's,
    which is none by default); ``policy`` and ``confidence_decay`` are contract 1.12's.
    """
    limit = (policy or DEFAULT_POLICY).candidate_limit
    if minimum_vector_relevance is None and policy is not None:
        minimum_vector_relevance = policy.minimum_vector_relevance
    floored = [
        row
        for row in vector_rows[: limit + 1]
        if minimum_vector_relevance is None or row.relevance >= minimum_vector_relevance
    ]
    # Checks are over each channel's first candidates as returned (the floor only drops vector candidates).
    text = check_channel(text_rows, scope, limit)
    vector = check_channel(floored, scope, limit)
    merged = merge_channels(text, vector, lambda row: row.record.experience_id, lambda row: row.relevance)
    rows = [SearchRow(entry.item.record, entry.relevance) for entry in merged]
    ranked, excluded = rank_checked(
        rows,
        now,
        preferred_environment,
        exclude_model_authored=exclude_model_authored,
        policy=policy,
        confidence_decay=confidence_decay,
    )
    truncated = len(text_rows) > limit or len(floored) > limit
    return RankedResult(ranked, truncated, excluded)
