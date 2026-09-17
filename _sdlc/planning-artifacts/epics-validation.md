# Epic and story final validation

Verdict: **PASS** — re-validation after the 2026-09-07 findings-resolution pass. Ready for Sprint Planning.

Scope: current 4 epics / 22 stories, PRD, architecture spine, spec reuse contract. This re-validation checks that the six findings from the prior CONCERNS report are closed and re-runs the full mechanical and dependency checks; it still does not constitute application implementation or executable compatibility evidence — Story 1.7 remains the gate for that.

## Resolution of prior findings

| # | Severity | Finding | Resolution | Where |
| --- | --- | --- | --- | --- |
| 1 | High | FR9 lacked a testable risk-policy contract and an explicit promotion stance | Added host risk-policy denial as an explicit AC at both the storage gate and the injection gate; FR9 coverage map now states the MVP has no procedural promotion engine, citing ARCHITECTURE-SPINE.md's own Deferred entry for procedural promotion | Story 2.5 (new AC), Story 2.3 (new AC), FR Coverage Map |
| 2 | Medium | Story 2.1 still claimed lifecycle-event responsibilities owned by 2.4 | Narrowed user story and two ACs to record persistence only; removed "event mutation"/"event" wording; added an explicit ownership note pointing to 2.4 | Story 2.1 |
| 3 | Medium | Story 1.7 bundled several proof tracks with no stated independence | Added an AC stating each of the five tracks is dispatched and its evidence saved independently, and that a blocked track (e.g. database/vector) blocks only stories depending on that specific track | Story 1.7 (new AC) |
| 4 | Medium | Most stories lacked explicit FR/NFR/AD traceability and depends-on tags; order must follow the declared sequence, not numeric/file order | Added a `Traces / AD / Depends on` line to all 22 stories, using the declared delivery-order chains (not file order) for dependencies | All 22 stories |
| 5 | Medium | No dedup/independence key defined for Story 3.4 confidence evidence | Added an AC defining the independence key as (Experience ID, Run ID, Verification Round ID) for automated evidence and (Experience ID, Reviewer Identity, Run ID) for human evidence; later submissions under a new Evidence ID for the same key are recorded but excluded from the S/F count | Story 3.4 (new AC) |
| 6 | Low | Retrieval weights, eligibility threshold, and limits were unbound; a threshold above 2/3 would block first reuse | Added a "Default operational values" block: ranking weights (0.35/0.25/0.15/0.15/0.10, sum 1.0), eligibility confidence threshold 0.5 (below the 2/3 initial-validation confidence, so first reuse is not blocked), retrieval timeout 500 ms, injection limits 8 records / 16 KB | Overview, after "Accepted planning defaults" |

## Requirement coverage (re-checked against the new Traces tags)

| Requirement | Implementing stories | Assessment |
| --- | --- | --- |
| FR1 correlation | 1.1, 1.2, 1.6 | Covered |
| FR2 capture | 1.1, 1.2, 1.6 | Covered; supported MAF types gated by 1.7 |
| FR3 sanitization | 1.4 | Covered |
| FR4 evaluation | 1.5, 2.5 | Covered, round/artifact semantics explicit |
| FR5 reflection | 1.1, 1.3, 2.5 | Covered |
| FR6 persistence/events | 2.1, 2.4, 2.5 | Covered; split boundary now explicit (2.1 record-only, 2.4 atomic event/projection) |
| FR7 retrieval | 2.2, 2.6 | Covered |
| FR8 context injection | 2.3, 4.2 | Covered; provider choice gated by 1.7 |
| FR9 scope/risk/promotion policy | 1.4, 2.1, 2.2, 2.3, 2.5, 3.1 | Covered, including host risk-policy gating and the explicit no-promotion-engine statement |
| FR10 lifecycle/feedback | 3.2, 3.3, 3.4, 4.5 | Covered |
| FR11 telemetry | 4.1, 4.4 (measurement) | Covered |

NFR1–8 each trace to at least one story via its `Traces`/`AD` tag (NFR1: 2.1, 3.1, 4.3, 4.5; NFR2: 1.4, 4.1; NFR3: 1.2, 1.6; NFR4: 2.4, 2.3, 2.5; NFR5: 2.2, 2.3, 4.4; NFR6: 1.6, 1.7, 4.2, 4.3; NFR7: 1.3, 4.1; NFR8: 1.1, 4.3). No UI or starter template is specified, so neither adds a prerequisite; Story 1.1 includes the minimal scaffold.

## Dependency validation

Every story's `Depends on` tag references only stories earlier in its epic's declared delivery-order chain (never file/numeric order, never a later story). Cross-epic dependencies point only to fully preceding epics (Epic 2 depends on Epic 1's 1.7 output; Epic 3 depends on Epic 2 completing; Epic 4 depends on Epic 3 completing). No forward dependency exists.

## Epic and story quality

Unchanged from the prior review's structural assessment: Epic 1 provides inspectable in-memory MAF experience; Epic 2 adds durable reuse with minimum policy and lifecycle behavior already included; Epic 3 adds management and feedback; Epic 4 supplies integrated operational/release evidence. Core-file overlap across epics is justified by incremental outcomes, provided the shared interfaces (now explicit via `Depends on`) are retained. Story 2.6 can still test rejection of an index write to a missing record without implementing Story 4.5's public deletion API; Story 3.4 can still test direct Core commands without Story 3.3's feedback ingestion.

Reuse boundaries prohibit generic replacements for MAF execution/context, embedding providers, vector infrastructure, evaluation reports, redaction engines, and exporters. This remains a planning constraint, not proof of runtime compatibility; Story 1.7 still supplies that evidence, now with explicit per-track independence.

## Handoff

All prior findings are closed. The epic and story breakdown is ready for Sprint Planning. Story 1.7's compatibility evidence remains the gate before adapter implementation stories proceed; no sprint tracking or application code was created by this validation.
