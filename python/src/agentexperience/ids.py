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


def derive(run_id: uuid.UUID, tag: int, scope: Scope | None = None) -> uuid.UUID:
    """
    SHA-256 over the namespace's 16 bytes, the run ID's 16 bytes, the tag byte and, with a scope, each scope field
    (``0x00`` when null; else ``0x01``, its UTF-8 length as a 4-byte big-endian integer, and its UTF-8 bytes); the
    first 16 bytes, stamped with version 8 and the RFC 4122 variant. UTF-8 is as .NET encodes it: a lone surrogate
    is U+FFFD.
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
