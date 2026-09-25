-- AgentExperience.NET: verified confidence independence (Story 6.6, KL-11).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0013, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT KL-11 WAS. 0007's header said it plainly: there is no foreign key behind run_id or
-- verification_round_id, nothing in the schema can check that a run happened or that a round was closed, and a
-- caller passing a fresh Guid for either on every submission gets a fresh independence key every time. The
-- human shape was weaker still: an assessment was a GUID the caller named.
--
-- WHAT CLOSES MOST OF IT IS NOT IN THIS SCRIPT. AgentExperience.Core now verifies every submission before it
-- reaches the store: the run must be one finalized into a record in the evidence's scope (or held by the
-- capture service), machine evidence must name the round that finalization closed (recorded in the record's
-- payload as closedRoundId, which needs no column), and human evidence must present an HMAC assessment token
-- the library minted under a host-held key. None of that needs a schema change.
--
-- WHAT THIS SCRIPT DOES. It makes an assessment single-use, in the database, atomically with the evidence
-- it lands:
--
-- 1. confidence_evidence.assessment_id: the ID of the assessment token a human submission presented, NULL
--    for machine evidence and for evidence submitted without verification. A CHECK keeps it on human rows
--    only, and never the empty UUID.
--
-- 2. ux_confidence_evidence_assessment: UNIQUE (experience_id, assessment_id) WHERE assessment_id IS NOT
--    NULL. One assessment lands at most one piece of evidence per record it covers. Unlike 0007's
--    independence index it is not partial on counted: a replayed token must be refused whether or not the
--    first use counted, or the replay would still be recorded. A resubmission of the SAME evidence ID is a
--    replay of the original and is decided by the primary key, exactly as before; the store tells the two
--    apart by whether the evidence ID is already in the ledger.
--
-- 3. lifecycle_events.confidence_assessment_id: the same ID on the counted event, so a moved score traces back
--    to the assessment that moved it from the audit trail alone. It sits outside 0007's all-or-nothing
--    CHECK (it is legitimately NULL on a whole machine update) and has its own.
--
-- UPGRADING AN EXISTING DATABASE. Both new columns are NULL on every existing row, which every constraint
-- below admits. The CHECK on the existing lifecycle_events table is ADD CONSTRAINT ... NOT VALID, exactly as in
-- 0006 and 0007, so the log is not scanned at startup; validate it at a time of your choosing:
--
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_assessment_human_only;
--
-- The CHECK on confidence_evidence is NOT VALID for the same reason, and validates the same way:
--
--     ALTER TABLE agent_experience.confidence_evidence VALIDATE CONSTRAINT confidence_evidence_assessment_human_only;
--
-- LOCKS. Each ADD COLUMN takes an ACCESS EXCLUSIVE lock on its table for an instant (a nullable column with no
-- default rewrites nothing), and each ADD CONSTRAINT ... NOT VALID a SHARE ROW EXCLUSIVE one. Short as they are,
-- an ACCESS EXCLUSIVE request queues behind any long-running reader of the ledger and then blocks everything
-- queued behind it; on a busy database run the migrator with a lock_timeout (for example
-- ALTER ROLE <owner> SET lock_timeout = '5s') and retry, rather than let it stall the application.
--
-- THE INDEX IS BUILT WITH A PLAIN CREATE UNIQUE INDEX, which takes a SHARE lock on confidence_evidence and
-- blocks evidence appends for the duration. Every existing row has assessment_id NULL, so the partial index is
-- empty and the build is a scan of the table and nothing more; on a very large ledger that scan is still a
-- pause in evidence writes. A deployment that cannot take one should build it out of band first -- CONCURRENTLY
-- cannot run inside the migrator's per-script transaction, and IF NOT EXISTS then makes the statement below a
-- no-op:
--
--     ALTER TABLE agent_experience.confidence_evidence ADD COLUMN IF NOT EXISTS assessment_id uuid NULL;
--     CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_confidence_evidence_assessment
--         ON agent_experience.confidence_evidence (experience_id, assessment_id)
--         WHERE assessment_id IS NOT NULL;
--
-- CONCURRENTLY can leave an INVALID index behind if it fails, and IF NOT EXISTS below would then accept it and
-- journal this script with no working replay guard. Check before migrating, and drop and retry if it is false:
--
--     SELECT indisvalid FROM pg_index
--     WHERE indexrelid = 'agent_experience.ux_confidence_evidence_assessment'::regclass;
--
-- PRIVILEGES. No table, function or trigger is added. The application role's manifest grants INSERT and
-- SELECT on both ledgers at table level, which covers the new columns, and no UPDATE on either, so the new
-- columns are as append-only as the rest of each row (0007's triggers already refuse UPDATE and DELETE).
--
-- WHAT THIS BINDS, AND WHAT IT DOES NOT. The index binds every writer while it exists, including the owner. It
-- does not make an assessment ID meaningful by itself: a writer that bypasses AgentExperience.Core can insert
-- any UUID, exactly as it could always insert any run_id. The token check is Core's; this is the replay guard
-- underneath it.

ALTER TABLE agent_experience.confidence_evidence
    ADD COLUMN IF NOT EXISTS assessment_id uuid NULL;

ALTER TABLE agent_experience.lifecycle_events
    ADD COLUMN IF NOT EXISTS confidence_assessment_id uuid NULL;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'confidence_evidence_assessment_human_only'
          AND conrelid = 'agent_experience.confidence_evidence'::regclass)
    THEN
        ALTER TABLE agent_experience.confidence_evidence
            ADD CONSTRAINT confidence_evidence_assessment_human_only
            CHECK (assessment_id IS NULL
                   OR (source = 'Human' AND assessment_id <> '00000000-0000-0000-0000-000000000000'::uuid))
            NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_assessment_human_only'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_assessment_human_only
            CHECK (confidence_assessment_id IS NULL
                   OR (confidence_source = 'Human'
                       AND confidence_assessment_id <> '00000000-0000-0000-0000-000000000000'::uuid))
            NOT VALID;
    END IF;
END
$body$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_confidence_evidence_assessment
    ON agent_experience.confidence_evidence (experience_id, assessment_id)
    WHERE assessment_id IS NOT NULL;
