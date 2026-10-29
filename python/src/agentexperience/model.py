"""
The run's model (contract 1.9): capture records the model id the agent's chat client reports in the run's environment
metadata under :data:`MODEL_METADATA_KEY`, so a record names the model that learned it and a run's own record names
the model that used its lessons. It is bookkeeping for the benefit estimate only: the reflector never lists it as a
precondition, the block never shows it, and ranking ignores it.
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Any

from ._text import is_null_or_whitespace, is_whitespace, utf16_len, utf16_prefix

MODEL_METADATA_KEY = "agentexperience.model"
"""The environment metadata key: .NET's ``HoldoutOutcomeRow.ModelMetadataKey``."""

MAX_MODEL_LENGTH = 256
"""The longest model id recorded, in UTF-16 code units; a longer one is cut."""


def _trim_end(text: str) -> str:
    end = len(text)
    while end > 0 and is_whitespace(text[end - 1]):
        end -= 1
    return text[:end]


def _trim(text: str) -> str:
    start = 0
    while start < len(text) and is_whitespace(text[start]):
        start += 1
    return _trim_end(text[start:])


def normalize_model_id(model_id: str | None) -> str | None:
    """
    A reported model id as capture records it: trimmed of white space as .NET's ``String.Trim`` trims it, cut to
    :data:`MAX_MODEL_LENGTH` UTF-16 code units without splitting a surrogate pair, and trimmed at the end again;
    ``None`` when absent or blank.
    """
    if not isinstance(model_id, str):
        return None
    trimmed = _trim(model_id)
    if not trimmed:
        return None
    if utf16_len(trimmed) <= MAX_MODEL_LENGTH:
        return trimmed
    return _trim_end(utf16_prefix(trimmed, MAX_MODEL_LENGTH))


def with_model(metadata: Mapping[str, str], model: str | None) -> dict[str, str]:
    """``metadata`` with ``model`` under :data:`MODEL_METADATA_KEY`; unchanged when there is no model, or when the
    metadata already has the key (the host's own value wins, even a blank one)."""
    result = dict(metadata)
    if model is not None and MODEL_METADATA_KEY not in result:
        result[MODEL_METADATA_KEY] = model
    return result


def without_model(metadata: Mapping[str, str]) -> Mapping[str, str]:
    """``metadata`` without the model key; the same mapping when it has none."""
    if MODEL_METADATA_KEY not in metadata:
        return metadata
    return {key: value for key, value in metadata.items() if key != MODEL_METADATA_KEY}


def model_of_metadata(metadata: Any) -> str | None:
    """The model a stored environment metadata object names, exactly as stored; ``None`` when absent or blank (.NET
    ``string.IsNullOrWhiteSpace``)."""
    if not isinstance(metadata, Mapping):
        return None
    model = metadata.get(MODEL_METADATA_KEY)
    return model if isinstance(model, str) and not is_null_or_whitespace(model) else None


def reported_model(agent: Any) -> str | None:
    """
    The model ``agent``'s chat client reports, normalized: the agent's default ``model`` (or ``model_id``) option,
    else its chat client's ``model`` (or ``model_id``) attribute. ``None`` when none is a non-blank string. Never
    raises.
    """
    try:
        options = getattr(agent, "default_options", None)
        candidates: list[Any] = []
        if isinstance(options, Mapping):
            candidates += [options.get("model"), options.get("model_id")]
        for owner in (getattr(agent, "client", None), getattr(agent, "chat_client", None)):
            if owner is not None:
                candidates += [getattr(owner, "model", None), getattr(owner, "model_id", None)]
        for candidate in candidates:
            model = normalize_model_id(candidate) if isinstance(candidate, str) else None
            if model is not None:
                return model
    except Exception:
        return None
    return None


__all__ = [
    "MAX_MODEL_LENGTH",
    "MODEL_METADATA_KEY",
    "model_of_metadata",
    "normalize_model_id",
    "reported_model",
    "with_model",
    "without_model",
]
