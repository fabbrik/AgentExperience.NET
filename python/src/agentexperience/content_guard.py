"""
The content guard finalization's screening applies to a model-authored reflection (contract 1.14): .NET's
``ModelAuthoredContentGuard`` and the shapes of ``ModelTextPatterns`` it looks for, ported exactly.

It is a fixed, deterministic, best-effort filter, never a model call, and never applied to a deterministic reflection.
Every field is folded first (invisible characters removed, Unicode NFKC, removed again). It then refuses
credential-shaped text, a word mixing Latin with Cyrillic or Greek letters, instruction-override phrasing (in each field
and across all of them joined), a ``data:``, ``javascript:``, ``vbscript:`` or ``file:`` link or a UNC path, and any
whole URL, hostname or IP address that is not, as a whole token, among those in the run content the reflector was
given.

.NET applies its patterns to UTF-16 code units, so here every pattern runs on the text with each character outside the
Basic Multilingual Plane spelled as its two surrogate code points: such a character is neither a letter, a mark nor a
digit to them, as in .NET. Lower-casing is .NET's invariant simple mapping, one code unit at a time.

A filter, not a boundary: content echoed from the run passes by design, and paraphrase passes too (see the finalization
guide's "Limits of model-authored lessons").
"""

from __future__ import annotations

import re
import unicodedata
from collections.abc import Callable, Iterable, Sequence
from dataclasses import dataclass, field

from ._text import is_whitespace, visible

__all__ = [
    "Links",
    "check",
    "check_combined",
    "fold",
    "has_credential",
    "has_instruction_override",
    "has_link",
    "has_mixed_script_word",
    "links_in",
    "links_of",
]


# ---- UTF-16 code units -------------------------------------------------------------------------------------------


def to_units(text: str) -> str:
    """``text`` with each character outside the Basic Multilingual Plane as its surrogate pair: one code point per
    UTF-16 code unit, as .NET's ``char`` sees the string."""
    if all(ord(ch) <= 0xFFFF for ch in text):
        return text
    out: list[str] = []
    for ch in text:
        code = ord(ch)
        if code > 0xFFFF:
            code -= 0x10000
            out.append(chr(0xD800 + (code >> 10)))
            out.append(chr(0xDC00 + (code & 0x3FF)))
        else:
            out.append(ch)
    return "".join(out)


def _category(unit: str) -> str:
    code = ord(unit)
    return "Cs" if 0xD800 <= code <= 0xDFFF else unicodedata.category(unit)


def _is_letter(unit: str) -> bool:
    """.NET's ``char.IsLetter``: a surrogate code unit is not one."""
    return _category(unit) in ("Lu", "Ll", "Lt", "Lm", "Lo")


def lower_invariant(units: str) -> str:
    """.NET's ``ToLowerInvariant``: each code unit's simple lower-case mapping (the dotted capital I is ``i``)."""
    out: list[str] = []
    for unit in units:
        code = ord(unit)
        if 0xD800 <= code <= 0xDFFF or code < 0x41:
            out.append(unit)
        elif code == 0x130:
            out.append("i")
        else:
            lower = unit.lower()
            out.append(lower if len(lower) == 1 else unit)
    return "".join(out)


def _class(predicate: Callable[[int], bool]) -> str:
    """The body of a regular-expression class of every BMP code point (no surrogate) ``predicate`` admits."""
    ranges: list[str] = []
    start = -1
    for code in range(0x10001):
        inside = code <= 0xFFFF and not 0xD800 <= code <= 0xDFFF and predicate(code)
        if inside and start < 0:
            start = code
        elif not inside and start >= 0:
            end = code - 1
            ranges.append(f"\\u{start:04x}" if start == end else f"\\u{start:04x}-\\u{end:04x}")
            start = -1
    return "".join(ranges)


_L = _class(lambda code: unicodedata.category(chr(code)) in ("Lu", "Ll", "Lt", "Lm", "Lo"))
_N = _class(lambda code: unicodedata.category(chr(code)) in ("Nd", "Nl", "No"))
_M = _class(lambda code: unicodedata.category(chr(code)) in ("Mn", "Mc", "Me"))
_D = _class(lambda code: unicodedata.category(chr(code)) == "Nd")
_S = _class(lambda code: is_whitespace(chr(code)))


# ---- The patterns (ModelTextPatterns.Shared.cs) ------------------------------------------------------------------

_GENERIC_TLDS = (
    "com net org edu gov mil arpa info biz pro mobi aero asia coop jobs museum tel travel xxx xyz online site website "
    "top club tech cloud app dev icu vip win bid loan example localhost onion"
).split()

_COUNTRY_TLDS = (
    "ad ae af ag ai al ao aq ar at au aw ax az ba bb bd be bf bg bh bi bj bl bm bn bo bq br bs bt bv bw by bz ca cd cf "
    "cg ch ci ck cl cm cn co cr cu cv cw cx cy cz de dj dk dm do dz ec ee eg eh er es et fi fj fk fm fo fr ga gb ge gf "
    "gg gh gi gl gm gn gp gq gr gs gt gu gw gy hk hm hn hr ht hu ie il im io iq ir it je jm jo jp ke kg kh ki km kn kp "
    "kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc me mf mg mh mm mn mp mq mr ms mt mu mv mw mx my mz na nc ne nf "
    "ng ni nl no np nr nu nz om pa pe pf pg ph pk pn pr pt pw qa re ro ru rw sa sb sc sd se sg si sj sk sl sm sn sr ss "
    "st sv sx sy sz tc td tg th tj tk tl tm tn to tr tt tv tw tz ua ug um us uy uz va vc ve vg vi vn vu wf ws ye yt za "
    "zm zw uk eu su ac"
).split()

TLDS: frozenset[str] = frozenset(_GENERIC_TLDS + [tld for tld in _COUNTRY_TLDS if tld != "mm"])
"""The top-level domains a hostname may end in, besides an ``xn--`` label and a non-ASCII one."""

_URL = re.compile(rf"(?<![{_L}{_N}])[a-z][a-z0-9+.\-]*://[^{_S}<>\"'`]+|(?<![{_L}{_N}{_M}_\-.])www\.[^{_S}<>\"'`]+")
# .NET's labels are atomic groups; a label is always followed by a dot or by a character outside its class, so a
# greedy label matches exactly what the atomic one does.
_DOTTED_TOKEN = re.compile(rf"(?<![{_L}{_N}{_M}\-])[{_L}{_N}{_M}\-]+(?:\.[{_L}{_N}{_M}\-]+)+(?![{_L}{_N}{_M}])")
_IP = re.compile(
    rf"(?<![{_L}{_N}.])(?:[{_D}]{{1,3}}\.){{3}}[{_D}]{{1,3}}(?![{_L}{_N}]|\.[{_D}])"
    rf"|(?<![{_L}{_N}])0x[0-9a-f]{{8}}(?![{_L}{_N}])"
    rf"|(?<![{_L}{_N}])0x[0-9a-f]{{1,2}}(?:\.0x[0-9a-f]{{1,2}}){{3}}(?![{_L}{_N}])"
)
_ALWAYS_REFUSED = re.compile(
    rf"(?<![{_L}{_N}])(?:javascript|vbscript):[^{_S}]|(?<![{_L}{_N}])data:(?:,|[a-z]+/)|(?<![{_L}{_N}])file:/"
    rf"|\\\\[{_L}{_N}][{_L}{_N}.\-]*\\[{_L}{_N}$]"
)
_BRACKETED_DOT = re.compile(rf"[{_S}]*[\[({{][{_S}]*(?:\.|dot)[{_S}]*[\])}}][{_S}]*")
_SPELLED_DOT = re.compile(rf"(?<=[{_L}{_N}])[{_S}]+dot[{_S}]+(?=[{_L}{_N}])")


def _ascii_ignore_case(word: str) -> str:
    """``word`` as .NET's ``IgnoreCase | CultureInvariant`` matches it on folded text: each ASCII letter, both cases."""
    return "".join(f"[{ch.lower()}{ch.upper()}]" for ch in word)


_SECRET_NAMES = "|".join(map(_ascii_ignore_case, ("AccountKey", "SharedAccessSignature", "password", "pwd")))
_CREDENTIAL = re.compile(
    r"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----"
    r"|(?:AKIA|ASIA)[A-Z0-9]{16}"
    r"|sk-[A-Za-z0-9_\-]{20,}"
    r"|gh[opusr]_[A-Za-z0-9]{36}"
    r"|github_pat_[A-Za-z0-9_]{22,}"
    r"|glpat-[A-Za-z0-9_\-]{20,}"
    r"|AIza[0-9A-Za-z_\-]{35}"
    r"|xox[abposr]-"
    r"|eyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+"
    rf"|(?<![a-zA-Z])(?:{_SECRET_NAMES})"
    rf"[{_S}]*=[^{_S};\"',]"
)

_ROLES = (
    "assistant|admin|administrator|operator|root|superuser|system|developer|agent|ai|bot|model|hacker|jailbroken|"
    "unrestricted|unfiltered|dan"
)
_PHRASE = re.compile(
    r"(?<![a-z])(?:"
    r"(?:ignore|disregard|forget|override|bypass) (?:(?:all|any|the|your|my|these|those|every) )?"
    r"(?:(?:the )?(?:previous|prior|earlier|above|preceding|former|original|system|existing) )?"
    r"(?:instructions?|directions|directives|prompts?)"
    r"|(?:ignore|disregard|forget) (?:all (?:the )?(?:previous|prior|earlier|above|preceding)|(?:the|everything) above"
    r"|everything (?:before|previous|prior))"
    rf"|you are now (?:a|an|the) (?:[a-z]+ )?(?:{_ROLES})"
    r"|you are now in (?:[a-z]+ ){0,2}mode"
    r"|you are now (?:unrestricted|jailbroken|unfiltered|root|admin)"
    r"|(?:reveal|print|ignore|output|show|repeat|leak|display|dump|disclose) (?:the|your|my) "
    r"(?:(?:full|entire|original|hidden|initial|secret) )?system prompts?"
    r"|(?:do not|don t|dont|never) (?:tell|inform|notify|alert) the user"
    r")(?![a-z])"
)
_NEW_INSTRUCTIONS = re.compile(rf"(?<![{_L}{_N}])new instructions[{_S}]*:")
_COLLAPSED_PHRASES = (
    "ignorepreviousinstructions",
    "ignoreallpreviousinstructions",
    "ignorepriorinstructions",
    "ignoreallpriorinstructions",
    "ignoreyourinstructions",
    "ignoretheaboveinstructions",
    "disregardpreviousinstructions",
    "disregardallpreviousinstructions",
    "disregardpriorinstructions",
    "disregardyourinstructions",
    "disregardallinstructions",
    "forgetpreviousinstructions",
    "forgetallpreviousinstructions",
    "overrideyourinstructions",
    "overridetheinstructions",
    "donottelltheuser",
    "donttelltheuser",
    "revealthesystemprompt",
    "revealyoursystemprompt",
    "printthesystemprompt",
    "printyoursystemprompt",
)
_CONFUSABLES = {
    "\u0430": "a",
    "\u0435": "e",
    "\u0451": "e",
    "\u0456": "i",
    "\u0457": "i",
    "\u0458": "j",
    "\u043a": "k",
    "\u043e": "o",
    "\u0440": "p",
    "\u0441": "c",
    "\u0455": "s",
    "\u0443": "y",
    "\u0445": "x",
    "\u04bb": "h",
    "\u0501": "d",
    "\u051b": "q",
    "\u051d": "w",
    "ı": "i",
    "ɡ": "g",
    "\u03b1": "a",
    "\u03b5": "e",
    "\u03b9": "i",
    "\u03ba": "k",
    "\u03bd": "v",
    "\u03bf": "o",
    "\u03c1": "p",
    "\u03c4": "t",
    "\u03c5": "u",
    "\u03c7": "x",
}


@dataclass(frozen=True)
class Links:
    """The links found in one text: whole URLs, whole hostnames and IP addresses, lower-cased (as UTF-16 code units),
    and whether it holds a link that is always refused."""

    urls: frozenset[str] = field(default_factory=frozenset)
    hosts: frozenset[str] = field(default_factory=frozenset)
    addresses: frozenset[str] = field(default_factory=frozenset)
    always_refused: bool = False


def deobfuscate(lower: str) -> str:
    """The common ways of hiding a dot undone: the ideographic and full-width full stops, ``[.]``, ``(.)``, ``{.}``,
    ``[dot]`` and a spelled-out ``dot`` between two words."""
    text = lower.replace("。", ".").replace("｡", ".").replace("．", ".")
    text = _BRACKETED_DOT.sub(".", text)
    return _SPELLED_DOT.sub(".", text)


def _trim_url(url: str) -> str:
    return url.rstrip(".,;:!?)]}>")


def _as_host(token: str) -> str | None:
    host = token.strip(".-")
    dot = host.rfind(".")
    if dot <= 0:
        return None
    tld = host[dot + 1 :].strip("-")
    if not tld:
        return None
    if tld in TLDS or tld.startswith("xn--") or (any(ord(c) > 0x7F for c in tld) and any(_is_letter(c) for c in tld)):
        return host
    return None


def _is_address(match: str) -> bool:
    if match.startswith("0x"):
        return True
    for part in match.split("."):
        # .NET's int.TryParse with NumberStyles.None: ASCII digits only.
        if not part or not all("0" <= ch <= "9" for ch in part) or int(part) > 255:
            return False
    return True


def _links_in_units(units: str) -> Links:
    text = deobfuscate(lower_invariant(units))
    urls = frozenset(_trim_url(match.group()) for match in _URL.finditer(text))
    hosts = frozenset(host for match in _DOTTED_TOKEN.finditer(text) if (host := _as_host(match.group())) is not None)
    addresses = frozenset(match.group() for match in _IP.finditer(text) if _is_address(match.group()))
    return Links(urls, hosts, addresses, _ALWAYS_REFUSED.search(text) is not None)


def links_in(text: str) -> Links:
    """The links in ``text``, which the caller has folded; empty when there are none."""
    return _links_in_units(to_units(text))


def has_link(text: str) -> bool:
    """Whether ``text`` holds any URL, hostname, IP address, colon-only scheme or UNC path."""
    links = links_in(text)
    return links.always_refused or bool(links.urls) or bool(links.hosts) or bool(links.addresses)


def has_credential(text: str) -> bool:
    """Whether ``text`` holds credential-shaped text."""
    return _CREDENTIAL.search(to_units(text)) is not None


def _phrase_form(units: str) -> str:
    result: list[str] = []
    space = False
    for unit in lower_invariant(units):
        mapped = _CONFUSABLES.get(unit, unit)
        if _is_letter(mapped):
            if space and result:
                result.append(" ")
            space = False
            result.append(mapped)
        else:
            space = True
    return "".join(result)


def _collapse_whitespace(units: str) -> str:
    result: list[str] = []
    space = False
    for unit in units:
        if is_whitespace(unit):
            space = True
            continue
        if space and result:
            result.append(" ")
        space = False
        result.append(unit)
    return "".join(result)


def has_instruction_override(text: str) -> bool:
    """Whether ``text`` holds instruction-override phrasing, in its phrase form or with its letters run together."""
    units = to_units(text)
    phrase = _phrase_form(units)
    if _PHRASE.search(phrase):
        return True
    collapsed = phrase.replace(" ", "")
    if any(core in collapsed for core in _COLLAPSED_PHRASES):
        return True
    return _NEW_INSTRUCTIONS.search(_collapse_whitespace(lower_invariant(units))) is not None


def _is_latin(code: int) -> bool:
    return (
        0x61 <= code <= 0x7A
        or 0x41 <= code <= 0x5A
        or 0xC0 <= code <= 0x24F
        or 0x250 <= code <= 0x2AF
        or 0x1E00 <= code <= 0x1EFF
        or 0x2C60 <= code <= 0x2C7F
        or 0xA720 <= code <= 0xA7FF
        or 0xAB30 <= code <= 0xAB6F
    )


def _is_cyrillic_or_greek(code: int) -> bool:
    return code != 0x3BC and (
        0x370 <= code <= 0x3FF
        or 0x1F00 <= code <= 0x1FFF
        or 0x400 <= code <= 0x52F
        or 0x1C80 <= code <= 0x1C8F
        or 0x2DE0 <= code <= 0x2DFF
        or 0xA640 <= code <= 0xA69F
    )


def has_mixed_script_word(text: str) -> bool:
    """Whether a word of ``text`` mixes Latin letters with Cyrillic or Greek ones. The micro sign's Greek mu is
    neutral."""
    latin = other = False
    for unit in to_units(text):
        if not _is_letter(unit) and _category(unit) not in ("Mn", "Mc"):
            latin = other = False
            continue
        code = ord(unit)
        if _is_latin(code):
            latin = True
        elif _is_cyrillic_or_greek(code):
            other = True
        if latin and other:
            return True
    return False


# ---- The guard (ModelAuthoredContentGuard.cs) --------------------------------------------------------------------


def fold(value: str) -> str:
    """Invisible characters removed, NFKC-normalized, invisible characters removed again."""
    return visible(unicodedata.normalize("NFKC", visible(value, remove=True)), remove=True)


def links_of(content: Iterable[str | None] | None) -> Links:
    """The whole URLs, hostnames and IP addresses in ``content`` (what the reflector showed the model); none when it
    is ``None``."""
    urls: set[str] = set()
    hosts: set[str] = set()
    addresses: set[str] = set()
    for text in content or ():
        if not text:
            continue
        try:
            links = links_in(fold(text))
        except Exception:  # Content that cannot be read admits nothing: the guard only gets stricter.
            continue
        urls |= links.urls
        hosts |= links.hosts
        addresses |= links.addresses
    return Links(frozenset(urls), frozenset(hosts), frozenset(addresses), False)


def check(value: str, run: Links) -> str | None:
    """The rule ``value`` breaks, as a content-free phrase, or ``None``. The matched text is never returned."""
    folded = fold(value)
    if has_credential(folded):
        return "credential-shaped text"
    if has_mixed_script_word(folded):
        return "a word mixing Latin with Cyrillic or Greek letters"
    if has_instruction_override(folded):
        return "instruction-override phrasing"
    links = links_in(folded)
    if links.always_refused:
        return "a data:, javascript:, vbscript: or file: link or a UNC path"
    if not links.urls <= run.urls:
        return "a URL that is not in the captured run"
    if not links.hosts <= run.hosts:
        return "a hostname that is not in the captured run"
    if not links.addresses <= run.addresses:
        return "an IP address that is not in the captured run"
    return None


def check_combined(fields: Sequence[str | None]) -> str | None:
    """Instruction-override phrasing in all of ``fields`` joined by spaces, so a phrase split across items is
    caught."""
    joined = fold(" ".join(item for item in fields if item is not None))
    return "instruction-override phrasing" if has_instruction_override(joined) else None
