-- AgentExperience.NET: which verification mode admitted each piece of confidence evidence (Story 7.3, KL-11).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0016, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT STORY 7.3 CHANGES, AND WHAT OF IT IS HERE. AgentExperience.Core now admits confidence evidence only for
-- a run that was EXPOSED to the record: the run's provenance names the records the library delivered into it,
-- finalization copies that onto the run's record, and Core checks it. Those exposures, and the record's
-- origin (finalized or written by hand), travel in the record's payload as optional version-1 fields, exactly
-- like 0015's closedRoundId: no column, sealed with the rest of the payload in crypto-shredding mode, and out
-- of the application role's reach because it holds no UPDATE on payload. None of that is in this script.
--
-- WHAT THIS SCRIPT DOES. It records which mode admitted each piece of evidence, the column 0015 deferred:
--
-- 1. confidence_evidence.admission: 'Verified' when Core checked the run, its exposure, and the round or the
--    assessment token; 'HostTrusted' when the host had opted out (TrustHostSuppliedIdentifiers) and its
--    identifiers were taken as given. NULL on every row written before this script, which a reader must treat
--    as "not recorded" -- evidence from before 0015 was all admitted without verification.
--
-- 2. lifecycle_events.confidence_admission: the same value on the counted event, so a score read from the
--    history alone (ExperienceLifecycleService.ReadConfidenceAsync) can leave host-trusted evidence out. It sits
--    outside 0007's all-or-nothing CHECK (it is legitimately NULL on older confidence events) and has its own.
--
-- The store writes both exactly as Core gives them and never derives one. They are deliberately not part of a
-- replay's content comparison: a resubmission of stored evidence reports the admission it was stored with.
--
-- UPGRADING AN EXISTING DATABASE. Both new columns are NULL on every existing row, which both constraints
-- admit. The CHECKs are ADD CONSTRAINT ... NOT VALID, exactly as in 0006, 0007 and 0015, so neither ledger is
-- scanned at startup; validate them at a time of your choosing:
--
--     ALTER TABLE agent_experience.confidence_evidence VALIDATE CONSTRAINT confidence_evidence_admission_known;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_admission_known;
--
-- LOCKS. Each ADD COLUMN takes an ACCESS EXCLUSIVE lock on its table for an instant (a nullable column with no
-- default rewrites nothing), and each ADD CONSTRAINT ... NOT VALID a SHARE ROW EXCLUSIVE one. Short as they are,
-- an ACCESS EXCLUSIVE request queues behind any long-running reader of the ledger and then blocks everything
-- queued behind it; on a busy database run the migrator with a lock_timeout (for example
-- ALTER ROLE <owner> SET lock_timeout = '5s') and retry. No index is built.
--
-- PRIVILEGES. No table, function or trigger is added. The application role's manifest grants INSERT and SELECT
-- on both ledgers at table level, which covers the new columns, and no UPDATE on either; 0007's triggers
-- already refuse UPDATE and DELETE on both. So an admission is as append-only as the rest of its row: the
-- application role cannot relabel host-trusted evidence as verified after the fact.
--
-- WHAT THIS BINDS, AND WHAT IT DOES NOT. A writer that bypasses AgentExperience.Core can insert either value,
-- exactly as it could always insert any run_id. The admission is Core's statement about its own check; this is
-- where it is kept.

ALTER TABLE agent_experience.confidence_evidence
    ADD COLUMN IF NOT EXISTS admission text NULL;

ALTER TABLE agent_experience.lifecycle_events
    ADD COLUMN IF NOT EXISTS confidence_admission text NULL;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'confidence_evidence_admission_known'
          AND conrelid = 'agent_experience.confidence_evidence'::regclass)
    THEN
        ALTER TABLE agent_experience.confidence_evidence
            ADD CONSTRAINT confidence_evidence_admission_known
            CHECK (admission IS NULL OR admission IN ('Verified', 'HostTrusted'))
            NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_admission_known'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_admission_known
            CHECK (confidence_admission IS NULL
                   OR (confidence_evidence_id IS NOT NULL AND confidence_admission IN ('Verified', 'HostTrusted')))
            NOT VALID;
    END IF;
END
$body$;
