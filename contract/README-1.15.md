# Contract 1.15: additions

This page lists what contract 1.15 adds to [contract 1.0](README.md) and the pages of 1.1 to 1.14
([1.1](README-1.1.md), [1.2](README-1.2.md), [1.3](README-1.3.md), [1.4](README-1.4.md), [1.5](README-1.5.md),
[1.6](README-1.6.md), [1.7](README-1.7.md), [1.8](README-1.8.md), [1.9](README-1.9.md), [1.10](README-1.10.md),
[1.11](README-1.11.md), [1.12](README-1.12.md), [1.13](README-1.13.md) and [1.14](README-1.14.md)). Those pages are
still normative and unchanged: a minor version only adds files, this page among them. The contract's version is the
one `lock.json` records (`contractVersion`), which the .NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.15 passes every 1.0 to 1.14 fixture and the fixtures below.

## The confidence engine, now in the ports

Up to 1.14 a port only read a record's stored confidence. .NET computes how it moves: when evidence about reusing a
lesson arrives, [`ReuseConfidenceHeuristic`](../src/AgentExperience.Core/Confidence/ReuseConfidenceHeuristic.cs)
settles the evidence counters, an [`IExperienceConfidenceEngine`](../src/AgentExperience.Core/Confidence/IExperienceConfidenceEngine.cs)
(by default [`ReuseConfidenceHeuristicEngine`](../src/AgentExperience.Core/Confidence/ReuseConfidenceHeuristicEngine.cs))
scores them, and the lifecycle service checks the engine's identity and every score it returns. Human evidence carries
an assessment token ([`AssessmentTokens.cs`](../src/AgentExperience.Core/Confidence/AssessmentTokens.cs)).

From 1.15 a port computes all of it, with .NET's defaults and validation, and gives the same numbers, statuses, rule
strings, keys and tokens as .NET for the same input ([Confidence and independence](../docs/guide/confidence.md)). It
only computes: applying an update to a stored record (the lifecycle event, the evidence ledger, the revision check,
the independence index) is not part of 1.15.

## What 1.15 adds

- **The default rule**, `(1 + S) / (2 + S + F)` in double precision, where `S` counts independent supporting
  validations (the one a record is finalized with included) and `F` independent contradictions: strictly inside (0, 1),
  a heuristic for ranking, never a probability. A negative count is refused, the supporting count checked first.
- **Applying evidence.** Which statuses accept evidence (`Validated`, `Reinforced`, `Contested`); the status evidence
  moves a record to (a contradiction contests a `Validated` or `Reinforced` record; nothing else moves a status); the
  update one piece of evidence makes (prior and new counters and score, the rule string, the evidence's identifiers,
  `counted`, and the same update recorded only); and the independence key it is counted under.
- **The engine seam.** A host engine has a `RuleId` and a `RuleVersion`, each 1 to 64 characters from `[A-Za-z0-9._-]`,
  and may not use the default's `RuleId`, `reuse-heuristic`. Every update records `1.0.0` under the default engine and
  `{RuleId}/{RuleVersion}` under a host engine. The engine is given the record as read and the counters after the
  evidence, and a score that is NaN, infinite or outside [0, 1] is refused, never clamped.
- **Assessment tokens.** The options a token is minted and checked under, the issuer's refusals, the token's exact
  bytes, and every way the codec refuses one.

## The rules

**Counters.** A record's counters are .NET's 32-bit integers: a stored counter that is negative is refused, naming
`record.SupportingValidations` or `record.Contradictions`, and one at 2147483647 that this evidence would increment is
refused, naming `record`, before the engine is asked anything. The other counter may be anything.

**Engine identity**, checked when the engine is wired in, the ID first: a `RuleId` or `RuleVersion` that is missing,
empty, longer than 64 characters or holds a character outside `[A-Za-z0-9._-]` (so any non-ASCII letter), or a host
engine whose `RuleId` is ordinally `reuse-heuristic`, is refused with an `ArgumentException` naming the engine's
parameter (`confidenceEngine` in .NET's lifecycle service). No engine, or the default engine itself, records `1.0.0`.

**Engine score.** A score in [0, 1], 0 and 1 included, is the update's new score. Anything else is refused with an
`InvalidOperationException` whose message is, byte for byte,
`The confidence engine '<rule>' returned <value>, which is not a score in [0, 1]. Nothing was written.`, where `<rule>`
is the recorded rule string and `<value>` is .NET's `double.ToString("R")`: `NaN`, `Infinity`, `-Infinity`, or the
shortest round-trip digits, with no trailing `.0`, in scientific notation (`1E+17`, `-5E-324`, an exponent of at least
two digits) below 1e-4 and from 1e17, so `1E-05` but `10000000000000000`. An exception the engine itself throws
propagates unchanged.

**Independence keys.** Machine evidence with a verification round is keyed `machine:<run>:<round>`; human evidence with
a reviewer that is not blank is keyed `human:<reviewer>:<run>`, the reviewer verbatim and compared ordinally (GUIDs in
lower-case `D` form). Machine evidence without a round, or human evidence without a reviewer, is refused naming
`update`; a reviewer with leading or trailing whitespace (as .NET's `char.IsWhiteSpace` defines it) is refused naming
`reviewerIdentity`, never trimmed. A machine submission's reviewer, and a human submission's round, are not part of its
key.

**Assessment token options**, checked in this order, each refusal an `ArgumentException` naming `options`: the
verification mode must be `Verified` or `TrustHostSuppliedIdentifiers`; the lifetime must be strictly positive and at
most 30 days (`ExperienceIndependenceOptions.AssessmentTokenLifetime must be strictly positive and at most
30.00:00:00.`); the key, when there is one, at least 32 bytes. An issuer needs a key (`An assessment token issuer needs
ExperienceIndependenceOptions.AssessmentTokenKey.`). The default lifetime is one day.

**Issuing**, checked in this order, each an `ArgumentException`: the reviewer's principal must be non-blank with no
leading or trailing whitespace (`reviewer`); the reviewer's authorization must permit the scope, and bounds whose tenant
is blank permit nothing (`scope`); the run must not be the empty GUID (`runId`); the kind must be `Supporting` or
`Contradicting` (`kind`); and 1 to 64 records, distinct and none the empty GUID (`experienceIds`). The issuer draws a
fresh random assessment ID for each token.

**The token.** `aexat1.` followed by the unpadded base64url encoding of: a version byte (1); the assessment ID (16
bytes, the GUID's big-endian, RFC 4122 byte order); the issue time and the expiry (Unix milliseconds, 8 bytes each,
big-endian, the expiry the issue time plus the lifetime in whole milliseconds, truncated); the kind (1 byte, 0 for
`Supporting`, 1 for `Contradicting`); the number of records (1 byte); each record ID (16 bytes, big-endian); and the
32-byte HMAC-SHA256, under the key, of the ASCII domain label `AgentExperience.AssessmentToken/v1` and a zero byte,
every byte before the MAC, then the six scope fields in order (tenant, application, project, team, agent, user; each a
zero byte when absent, otherwise a one byte, its UTF-8 length as a 4-byte big-endian integer and its UTF-8), the run ID's
16 bytes, and the reviewer (its UTF-8 length as 4 bytes, big-endian, and its UTF-8). UTF-8 is .NET's: a lone surrogate
encodes as U+FFFD.

**Checking a token**, against the scope, run, reviewer and kind a submission names, in this order:

1. no token, or an empty one: `AssessmentTokenMissing`;
2. longer than 4096 UTF-16 code units, or not starting with `aexat1.` (ordinally): `AssessmentTokenInvalid`;
3. a character after the prefix outside `A-Z a-z 0-9 - _`, or an encoding that does not decode: invalid;
4. fewer bytes than a header, one record ID and a MAC, or a version other than 1: invalid;
5. a record count of 0 or over 64, or one the length disagrees with: invalid;
6. an encoding that is not the canonical one of its bytes (unused low bits set): invalid;
7. a MAC that is not the one computed for this binding, compared in constant time: invalid;
8. now the token is genuine and bound to this scope, run and reviewer: a kind byte that is not this kind: invalid;
9. an issue time before 1970, an expiry not after the issue time, a lifetime over 30 days, or an issue time more than
   five minutes after now: invalid;
10. now at or after the expiry: `AssessmentTokenExpired`;
11. otherwise the token's assessment ID and its records, in order.

A refusal carries the empty GUID and no records. Whether the token covers the record, and whether it was already spent,
are decided when evidence is applied (`AssessmentTokenNotForRecord`, `AssessmentTokenReplayed`), not by these
fixtures. .NET's token codec is internal and so is each port's; the issuer is public.

**A language whose zero value cannot mean "unset"** (Go) may read a zero lifetime as the default, a zero verification
mode as `Verified` and a missing clock as the system clock, as 1.12 allows for the retrieval policy. It still refuses
every other value .NET refuses. A language whose duration type is coarser than .NET's 100-nanosecond tick (Python's
`timedelta`, whole microseconds) states a lifetime in its own unit; the whole milliseconds a token carries are the same.

## The fixtures

```
fixtures/confidence-engine/<case>/   input.json -> expected.json
```

Each case's `input.json` has a `description` and an `operation`; `expected.json` is what .NET's classes gave for it.
Numbers compare within 1e-12; everything else, rule strings and tokens included, exactly. A refusal is `exception`
(.NET's type name), `parameter` (`null` for none) and `message`: the library's own message, byte for byte, where it
wrote one, and `null` where the runtime's guard clause or a host engine did, in which case a port's wording is its own.

- **`score`**: `supportingValidations` and `contradictions` -> `score`, or `refusal`.
- **`rule`**: `engine`, `null` for none, `"default"` for the default engine, or a host engine's `ruleId` and
  `ruleVersion` (either may be `null`) -> `recorded`, or `refusal`.
- **`apply`**: `record`, a stored record in 1.0's form (its `status`, `reuse_confidence`, `supporting_validations` and
  `contradictions` columns are what matter, negative counters included); `evidence`, with `evidenceId`, `kind`,
  `source`, `runId`, `verificationRoundId`, `reviewerIdentity` and `detail` (each `null` for none); and `engine`, as
  above, where a host engine's `score` says what it returns: a number, `"NaN"`, `"Infinity"`, `"-Infinity"`, `"bayes"`
  for `(2 + S) / (4 + S + F)` over the counters it is given, `"stored"` for the record's stored score, or `"throws"`.
  Expected: `acceptsEvidence` and `statusAfter` (what the record's status and the evidence's kind give, whatever else
  happens); `update` (`evidenceId`, `kind`, `source`, `runId`, `verificationRoundId`, `reviewerIdentity`,
  `ruleVersion`, the prior and new `ReuseConfidence`, `SupportingValidations` and `Contradictions`, `detail`,
  `counted`) as the lifecycle service computes it, the counters by the heuristic and the score by the engine;
  `recordedOnly`, that update counted zero times; and `independenceKey` (`source` and `value`, or its `refusal`). When
  the update itself is refused, `update`, `recordedOnly` and `independenceKey` are `null` and `refusal` says why; a
  `HostEngineException` is the host engine's own exception, which a port's test engine raises as its own.
- **`options`**: `options` -> `accepted`, or `refusal`, building an issuer.
- **`issue`**: `options`, `assessmentId`, `reviewer` (the `principalId` and the authorization's `tenantId` and other
  bounds, `null` for unbounded), `scope`, `runId`, `kind` (`Sideways` is a kind that is not defined) and `experienceIds`
  -> `assessmentId`, `token`, `issuedAtUnixMs` and `expiresAtUnixMs`, or `refusal`. The issuer draws a random
  assessment ID; the fixture's token is the same codec's encoding under the case's `assessmentId`, so a port checks
  the issuer's refusals, then encodes under that ID with its codec.
- **`verify`**: `options`, `token` (`null` for none), and the binding: `scope`, `runId`, `reviewer` and `kind` ->
  `refusal` (`null` for a genuine token), `assessmentId` and `covered`.

Every `options` object holds `verification`, `assessmentTokenKey` (hex, `null` for none),
`assessmentTokenLifetimeTicks` (100-nanosecond ticks) and `nowUnixMs`, what the clock reads. The tokens a `verify`
case presents were minted by .NET's codec (those with a lifetime the options refuse, through its private constructor:
only a minter sharing the key could make one) and some then altered byte by byte, so every path above is reached.
