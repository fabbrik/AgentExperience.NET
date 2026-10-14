"""
The error class: a captured error reduced to at most three distinct tokens it recognises, in the order they first
appear, joined with ``", "``; ``unclassified error`` when there is none.
"""

from __future__ import annotations

import re

from ._text import SPACE_CLASS, ignore_case, is_null_or_whitespace, utf16_len, utf16_prefix, word_class

UNCLASSIFIED = "unclassified error"
MAX_TOKENS = 3
MAX_TOKEN_LENGTH = 40
MAX_SCANNED_LENGTH = 8192

_TIMEOUT = "Timeout"


def _ci(word: str) -> str:
    return "".join(ignore_case(ch) for ch in word)


def _compile() -> re.Pattern[str]:
    # The .NET pattern, spelled so Python matches what .NET matches: \s is .NET's whitespace, \b is a lookaround on
    # .NET's boundary word characters (letters, non-spacing marks, decimal digits, connector punctuation, ZWJ and
    # ZWNJ), and (?i:...) is per-letter ASCII case (never the dotless i, the dotted capital I or the long s). Every \b
    # in the pattern sits next to a word character, so it is a lookbehind before a token and a lookahead after one.
    w = word_class()
    start, end, space = f"(?<!{w})", f"(?!{w})", SPACE_CLASS
    return re.compile(
        rf"(?P<type>{start}[A-Z][A-Za-z0-9]{{0,38}}(?:Exception|Error){end})"
        rf"|(?P<exit>{start}{_ci('exit')}(?:{_ci('ed')})?(?:{space}+{_ci('with')})?(?:{space}+{_ci('code')})?"
        rf"{space}*[:=]?{space}*(?P<exitcode>-?[0-9]{{1,6}}){end})"
        rf"|(?P<http>{start}{_ci('http')}(?:/[0-9](?:\.[0-9])?)?{space}*(?P<httpcode>[1-5][0-9]{{2}}){end})"
        rf"|(?P<dotnet>{_ci('status')}{space}+{_ci('code')}{space}+{_ci('does')}{space}+{_ci('not')}{space}+"
        rf"{_ci('indicate')}{space}+{_ci('success')}{space}*:{space}*(?P<dotnetcode>[1-5][0-9]{{2}}){end})"
        rf"|(?P<status>{start}{_ci('status')}(?:{space}+{_ci('code')})?{space}*[:=]?{space}*"
        rf"(?P<statuscode>[1-5][0-9]{{2}}){end})"
        rf"|(?P<errno>{start}E[A-Z0-9]{{2,15}}{end})"
        rf"|(?P<timeout>{start}(?:{_ci('time')}[ -]?{_ci('out')}|{_ci('timed')}{space}+{_ci('out')}){end})"
    )


_tokens: re.Pattern[str] | None = None


def _pattern() -> re.Pattern[str]:
    global _tokens
    if _tokens is None:
        _tokens = _compile()
    return _tokens


_ERRNO_NAMES = frozenset(
    """
    E2BIG EACCES EADDRINUSE EADDRNOTAVAIL EAFNOSUPPORT EAGAIN EALREADY EBADF EBADMSG EBUSY ECANCELED ECHILD
    ECONNABORTED ECONNREFUSED ECONNRESET EDEADLK EDESTADDRREQ EDOM EDQUOT EEXIST EFAULT EFBIG EHOSTUNREACH EIDRM
    EILSEQ EINPROGRESS EINTR EINVAL EIO EISCONN EISDIR ELOOP EMFILE EMLINK EMSGSIZE EMULTIHOP ENAMETOOLONG ENETDOWN
    ENETRESET ENETUNREACH ENFILE ENOBUFS ENODATA ENODEV ENOENT ENOEXEC ENOLCK ENOLINK ENOMEM ENOMSG ENOPROTOOPT ENOSPC
    ENOSR ENOSTR ENOSYS ENOTCONN ENOTDIR ENOTEMPTY ENOTRECOVERABLE ENOTSOCK ENOTSUP ENOTTY ENXIO EOPNOTSUPP EOVERFLOW
    EOWNERDEAD EPERM EPIPE EPROTO EPROTONOSUPPORT EPROTOTYPE ERANGE EROFS ESPIPE ESRCH ESTALE ETIME ETIMEDOUT ETXTBSY
    EWOULDBLOCK EXDEV
    """.split()
)


def _token(match: re.Match[str]) -> str | None:
    if match["type"] is not None:
        return match["type"]
    if match["exit"] is not None:
        return "exit " + match["exitcode"]
    if match["http"] is not None:
        return "HTTP " + match["httpcode"]
    if match["dotnet"] is not None:
        return "HTTP " + match["dotnetcode"]
    if match["status"] is not None:
        return "status " + match["statuscode"]
    if match["errno"] is not None:
        name = match["errno"]
        return name if name in _ERRNO_NAMES else None
    return _TIMEOUT if match["timeout"] is not None else None


def error_class(error: str | None) -> str:
    """The class of ``error``; ``unclassified error`` for a blank one or one with no recognised token."""
    if error is None or is_null_or_whitespace(error):
        return UNCLASSIFIED

    scanned = utf16_prefix(error, MAX_SCANNED_LENGTH)
    found: list[str] = []
    for match in _pattern().finditer(scanned):
        token = _token(match)
        if token is not None and len(token) <= MAX_TOKEN_LENGTH and token not in found:
            found.append(token)

    # A timeout exception already says it timed out: "TimeoutException, Timeout" would say it twice.
    if (
        any(
            token != _TIMEOUT
            and "timeout" in token.lower()
            and (token.endswith("Exception") or token.endswith("Error"))
            for token in found
        )
        and _TIMEOUT in found
    ):
        found.remove(_TIMEOUT)

    return ", ".join(found[:MAX_TOKENS]) if found else UNCLASSIFIED


def first_token(errorclass: str) -> str:
    """The first token of a class: what a call's ``[failed: ...]`` marker shows."""
    end = errorclass.find(",")
    return errorclass if end < 0 else errorclass[:end]


def is_single_token(value: str | None) -> bool:
    """
    Whether ``value`` is exactly one token: the first token :func:`error_class` finds in it is all of it, in its
    normalized form (``exit 3``, not ``Exit 3`` or ``exit code 3``), at most 40 characters. Such a value is its own
    class's first token, so a ``Tried:`` line's call marker shows it unchanged.
    """
    if not value or utf16_len(value) > MAX_TOKEN_LENGTH:
        return False
    found = error_class(value)
    if found == UNCLASSIFIED:
        return False
    return first_token(found) == value
