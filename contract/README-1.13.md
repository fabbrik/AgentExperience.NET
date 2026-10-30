# Contract 1.13: additions

This page lists what contract 1.13 adds to [contract 1.0](README.md) and the pages of 1.1 to 1.12
([1.1](README-1.1.md), [1.2](README-1.2.md), [1.3](README-1.3.md), [1.4](README-1.4.md), [1.5](README-1.5.md),
[1.6](README-1.6.md), [1.7](README-1.7.md), [1.8](README-1.8.md), [1.9](README-1.9.md), [1.10](README-1.10.md),
[1.11](README-1.11.md) and [1.12](README-1.12.md)). Those pages are still normative and unchanged: a minor version only
adds files, this page among them. The contract's version is the one `lock.json` records (`contractVersion`), which the
.NET suite holds in `ContractFiles.ContractVersion`.

A port that passes contract 1.13 passes every 1.0 to 1.12 fixture and the fixtures below.

## The injection options

.NET's [`ExperienceInjectionOptions`](../src/AgentExperience.MicrosoftAgentFramework/Injection/ExperienceInjectionOptions.cs)
let a host decide per invocation whether to inject and what to retrieve, keep out a lesson the receiving agent cannot
or must not act on, choose how model-authored lessons are treated, and tune the block. Up to 1.12 a port offered the
block's layout, failure detail, argument allowlist and limits (1.0's `inject` fixtures), the message role and a
model-authored exclusion. From 1.13 it offers the rest that does not need session tracking, with .NET's defaults and
validation, and a Python or Go agent configured like a .NET one injects the same block with the same omissions
([Injection](../docs/guide/injection.md)).

**With the defaults nothing changes:** no receiving agent, `Include`, no host decision and no request resolver give the
block and the omissions of 1.0 to 1.12, and every 1.0 to 1.12 fixture is byte-identical. A port that does not
implement 1.13 keeps passing 1.12.

## What 1.13 adds

- **Fixtures:** `inject-options` (no database): twenty cases.
- **Rules:** the receiving agent's capability gate, the model-authored policy, the order selection applies them in, the
  gated retrieval window, and the host's two callbacks (request resolver and injection decision), which are not
  fixtured.

Nothing else changes: no SQL, no schema object, no privilege.

## The options

Each port names them in its own idiom and maps them one to one in its README.

| Option (.NET) | Default | Valid | Meaning |
| --- | --- | --- | --- |
| `ReceivingAgent` | none | below | The capability gate (below). None: nothing is gated. |
| `ModelAuthoredLessons` | `Include` | `Include`, `Exclude` | `Include`: a model-authored record is injected inside the fixed `Authored:`/`End authored:` fence (1.0). `Exclude`: retrieval is asked to leave model-authored records out (1.0's `exclude_model_authored`, set to true, never cleared), and any that still arrives is omitted (below). |
| `ApproachArguments`, `FailureDetail`, `Rendering`, `Limits.MaxRecords`, `Limits.MaxBytes` | 1.0's | 1.0's, and `MaxBytes` above the verbose block's fixed overhead (754 bytes) | As 1.0's `inject` options. |
| `MessageRole` | `User` | `User`, `System` | The chat role the block is sent in; the text is the same. |
| `ResolveRequest` / `ResolveRequestAsync` | none | | The host's request resolver (below). |
| `DecideInjection` | none (permit) | | The host's per-record decision (below). |

**`ReceivingAgentCapabilities`**

| Option | Default | Valid | Meaning |
| --- | --- | --- | --- |
| `AvailableTools` | none | no blank name | The tools the agent has, compared ordinally. None skips the tool check; an empty set admits only approaches that call no tool. |
| `MaxRiskClass` | none | a `ToolRiskClass` | The riskiest class the agent may be taught. None skips the risk check; `Critical` disables it. |
| `ToolRiskClasses` | empty | no blank name; each a `ToolRiskClass` | The host's class per tool. A tool not listed is `Critical`. Read only with `MaxRiskClass`. |
| `UseRunTools` | false | | The tool check uses the names of the tools the invocation offers (a blank name ignored), intersected with `AvailableTools` when that is set. Taken once per invocation. |
| `GatedRetrievalWindowMultiplier` | 4 | at least 1 | Widens retrieval while a gate can reject (below). |

**`ToolRiskClass`**, ordered: `Low` (0), `Medium` (1), `High` (2), `Critical` (3).

An invalid value is refused when the agent is built: a blank tool name in either collection, an undefined risk class, a
multiplier below 1, an undefined `ModelAuthoredLessons`. .NET throws `ArgumentException`; a port raises or returns its
own error. A port copies the declaration when the agent is built, so a later change to the host's collections has no
effect. **A language whose zero value cannot mean "unset"** (Go) may read a zero multiplier as 4, and must say so in its
README; it refuses a negative one.

## The capability gate

**What is checked:** the tool names of the record's approach, the attempt its `Worked:` line names (1.0): its verified
final attempt's calls, in call order, cut to 1.0's `MaxApproachToolNames` (20), exactly the calls its `Tried:` line
shows. A record with no approach (not `Verified`, `Quarantined`, no attempts, duplicated attempt sequence numbers, or a
final attempt with an error) has nothing to check and passes. A tool an earlier attempt called, or one the lesson text
names, is never checked.

**In order, the first failure deciding:**

1. **The tool check**, when there is an available set: any approach tool not in it, or with a blank name, omits the
   record as `ToolUnavailable`.
2. **The risk check**, when `MaxRiskClass` is set: any approach tool whose class (`ToolRiskClasses`, else `Critical`)
   is above `MaxRiskClass` omits it as `RiskClassExceeded`.

Both omissions carry **no detail** (`null`): no tool name leaves the provider. A gate **can reject** when it has an
available set (declared, or `UseRunTools`) or a `MaxRiskClass` below `Critical`; one that cannot is no gate at all: it
neither checks a record nor widens retrieval.

## Selection, in order

For each ranked record, best first, the first rule that applies (1.10's selection, with two rules added):

1. `MeasuredHarmful` (1.10);
2. `ModelAuthored`, under `Exclude`, when the record counts as model-authored (1.0's rule), with no detail;
3. the capability gate: `ToolUnavailable` or `RiskClassExceeded`, with no detail;
4. the record limit: selected while fewer than `min(MaxRecords, request limit)` are, otherwise `OverRecordLimit` (1.10's
   detail).

A record omitted by 1 to 3 takes **no slot**: the next record fills it. A record ranked past the request's own limit is
backfill (1.10): selected while a slot is left, never reported when omitted. Then the host's decision (below), then the
writer and its byte budget (`OverByteBudget`, 1.10's detail). The **outcome** is `Injected` when the block holds a
record and `NothingToInject` otherwise.

**The gated retrieval window.** While a gate can reject and the resolved request names its own limit, retrieval is asked
for `min(CandidateLimit, max(limit, MaxRecords × GatedRetrievalWindowMultiplier))` records instead, so a gated record
can be backfilled; a limit that is not positive or above `CandidateLimit` is left for retrieval to refuse. Selection
still uses the request's own limit. Under `Exclude`, the request retrieval is asked for excludes model-authored
records.

## The host's callbacks (not fixtured)

A callback has no fixture: each port pins it with its own tests, against the rules here, which are .NET's.

**The request resolver** (`ResolveRequest`, `ResolveRequestAsync`): called once per invocation, before retrieval, with
the invocation (its messages, session and agent, and 1.5's derived task text). It returns the request (the task text,
and optionally the preferred environment, a limit and the model-authored exclusion; in .NET also the authorization and
scope, which a port takes from its identity), or nothing:

| The resolver | Outcome (`inject` operation) | |
| --- | --- | --- |
| returns nothing | `Skipped` | nothing is retrieved or reported as omitted |
| raises or fails | `Failed` | nothing is injected; the invocation runs normally |
| is cancelled with the invocation | the cancellation propagates | as the language's cancellation |
| returns a request retrieval refuses (blank or overlong task text, a limit not in 1 to `CandidateLimit`) | `RetrievalFailed` | |

**The injection decision** (`DecideInjection`): asked once per selected record, in order, after selection and before the
writer, with the ranked record, the record as it stands and whether it counts as model-authored. It can only deny:

- a denial omits the record as **`HostDenied`**, with the decision's reason as the detail, or, when it gives none,
  `The host injection decision denied the record.`;
- **it fails closed**: a callback that raises, or returns nothing, denies, the detail
  `The host injection decision threw <exception type>, so the record was denied.` when it raised;
- a denied record has taken its slot: nothing backfills it;
- the `HostDenied` omissions come after the selection's and before the byte budget's.

## The fixtures

```
fixtures/inject-options/<case>/   records.json + options.json -> expected.json + expected-block.txt
```

`records.json` is 1.0's `inject` form: each ranked record, best first, as stored, with its ranking. `options.json`
holds the options above (camelCase, enums by name): `rendering`, `failureDetail`, `messageRole`, `maxRecords`,
`maxBytes`, `approachArguments`, `modelAuthoredLessons`, `requestLimit` (`null`: none), `receivingAgent` (`null`: none;
`availableTools` `null` for none, `maxRiskClass` `null` for none, `toolRiskClasses`, `useRunTools`,
`gatedRetrievalWindowMultiplier`) and `runTools`, the names of the tools the invocation offers (`null` for none;
read only with `useRunTools`). Retrieval's policy is the default (`CandidateLimit` 50). Select and render the records
under these options, with no benefit view and no host decision, and compare with `expected.json`:

- `outcome`, `Injected` or `NothingToInject`;
- `messageRole`, the role the block is sent in (`null` when nothing is injected);
- `retrievalRequest`: the `limit` retrieval is asked for (the gated window, or the request's own; `null` for none) and
  `excludeModelAuthored`;
- `selected` (before the byte budget) and `injected`, in order;
- `byteCount`, the block's UTF-8 size (0 when nothing is injected);
- `omitted`, in order: each `experienceId`, `reason` and `detail` (`null` for none);

and the block with `expected-block.txt`, byte for byte (empty when nothing is injected).
