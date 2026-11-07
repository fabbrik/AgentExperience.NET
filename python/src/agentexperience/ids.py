"""The derived IDs: UUIDv8 (RFC 9562's custom version) over SHA-256, so a retried finalization re-derives them."""

from __future__ import annotations

import hashlib
import uuid

from ._text import utf8_bytes
from .records import Scope

NAMESPACE = uuid.UUID("0b6a8a3f-1c2d-4f5e-9a70-3d1c9f2b8e41")

EXPERIENCE_ID_TAG = 1
INITIAL_EVENT_ID_TAG = 2
REFLECTION_ID_TAG = 3
REUSE_EVIDENCE_ID_TAG = 4
REUSE_EVENT_ID_TAG = 5

_EVIDENCE_KINDS = ("Supporting", "Contradicting")


def derive(run_id: uuid.UUID, tag: int, scope: Scope | None = None, suffix: bytes = b"") -> uuid.UUID:
    """
    SHA-256 over the namespace's 16 bytes, the run ID's 16 bytes, the tag byte and, with a scope, each scope field
    (``0x00`` when null; else ``0x01``, its UTF-8 length as a 4-byte big-endian integer, and its UTF-8 bytes), then
    ``suffix``; the first 16 bytes, stamped with version 8 and the RFC 4122 variant. UTF-8 is as .NET encodes it: a
    lone surrogate is U+FFFD.
    """
    data = bytearray(NAMESPACE.bytes)
    data += run_id.bytes
    data.append(tag)
    if scope is not None:
        for value in scope.fields():
            if value is None:
                data.append(0)
            else:
                encoded = utf8_bytes(value)
                data.append(1)
                data += len(encoded).to_bytes(4, "big")
                data += encoded
    data += suffix
    digest = bytearray(hashlib.sha256(bytes(data)).digest()[:16])
    digest[6] = (digest[6] & 0x0F) | 0x80
    digest[8] = (digest[8] & 0x3F) | 0x80
    return uuid.UUID(bytes=bytes(digest))


def experience_id_for(run_id: uuid.UUID, scope: Scope) -> uuid.UUID:
    return derive(run_id, EXPERIENCE_ID_TAG, scope)


def initial_event_id_for(run_id: uuid.UUID, scope: Scope) -> uuid.UUID:
    return derive(run_id, INITIAL_EVENT_ID_TAG, scope)


def reflection_id_for(run_id: uuid.UUID) -> uuid.UUID:
    return derive(run_id, REFLECTION_ID_TAG)


def _reuse_suffix(experience_id: uuid.UUID, kind: str) -> bytes:
    if kind not in _EVIDENCE_KINDS:
        # .NET's ArgumentOutOfRangeException for an undefined ConfidenceEvidenceKind.
        raise ValueError("Not a defined ConfidenceEvidenceKind.")
    return experience_id.bytes + bytes((_EVIDENCE_KINDS.index(kind),))


def reuse_evidence_id_for(run_id: uuid.UUID, scope: Scope, experience_id: uuid.UUID, kind: str) -> uuid.UUID:
    """
    The evidence ID finalization derives for its run's reuse evidence about one record (.NET's
    ``ExperienceFinalizationService.ReuseEvidenceIdFor``, tag 4): the run, the scope, then the record's 16 big-endian
    bytes and the kind's byte (``Supporting`` 0, ``Contradicting`` 1). ``ValueError`` for another kind.
    """
    return derive(run_id, REUSE_EVIDENCE_ID_TAG, scope, _reuse_suffix(experience_id, kind))


def reuse_event_id_for(run_id: uuid.UUID, scope: Scope, experience_id: uuid.UUID, kind: str) -> uuid.UUID:
    """The lifecycle event ID that reuse evidence rides on (.NET's ``ReuseEventIdFor``, tag 5), derived as
    :func:`reuse_evidence_id_for` derives the evidence ID."""
    return derive(run_id, REUSE_EVENT_ID_TAG, scope, _reuse_suffix(experience_id, kind))
