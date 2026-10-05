-- AgentExperience.NET: host-trusted evidence can be recorded on a lifecycle event without moving the record
-- (Story 17.3, KL-11).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0022, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHY. A host that opts out of independence verification (IndependenceVerification.TrustHostSuppliedIdentifiers)
-- can now set ExperienceIndependenceOptions.HostTrustedEvidence = RecordedOnly. Evidence the opt-out admits is then
-- committed exactly as before -- a lifecycle event and a ledger row, with the same IDs, idempotency, assessment
-- spending and admission 'HostTrusted' -- except that the event's new counters, new score and status equal its prior
-- ones, so the record's ranked score, its counters and its status stay where they were. 0007 refused such a ledger
-- row: confidence_evidence_event_only_when_counted ties an event to exactly the counted rows.
--
-- WHAT THIS SCRIPT DOES.
--
-- 1. Replaces confidence_evidence_event_only_when_counted with confidence_evidence_event_when_counted_or_recorded_only:
--    a row has an event exactly when it is counted, OR it is a recorded-only row -- not counted, carrying an event,
--    admission 'HostTrusted', and a new score equal to its prior one. Every other uncounted row still has no event
--    (a duplicate, as before). The counted = "a counter moved" rule (confidence_evidence_counted_matches_counters) and
--    the partial unique index on counted rows are unchanged: a recorded-only row claims no independence key, so
--    verified evidence for the same observation still counts. The store treats a later recorded-only submission for a
--    key that counted evidence or an earlier recorded-only event already holds as a duplicate.
--
-- 2. Adds lifecycle_events_confidence_moves_or_recorded_only: an event carrying evidence moves a counter, or it is
--    a recorded-only event -- admission 'HostTrusted', the same score, and the same status (or no prior status).
--
-- 3. Adds ix_confidence_evidence_key_with_event, a partial index on (experience_id, independence_key) WHERE
--    event_id IS NOT NULL: the store asks it, for each recorded-only submission, whether counted evidence or an
--    earlier recorded-only event already holds the key (every counted row has an event). The partial unique index
--    on counted rows cannot answer that, because it leaves recorded-only rows out.
--
-- The CHECKs compare the admission with IS NOT DISTINCT FROM, so a row with no admission recorded is refused rather
-- than passed by a NULL comparison.
--
-- The stores state the same rules in their shared validator, so a malformed event is a typed Invalid rather than an
-- infrastructure failure. No past evidence is rewritten: what was counted stays counted.
--
-- UPGRADING AN EXISTING DATABASE. Both new CHECKs are ADD CONSTRAINT ... NOT VALID, exactly as in 0006, 0007, 0015
-- and 0018, so neither ledger is scanned at startup. Every existing ledger row satisfied the constraint this replaces,
-- and so satisfies the new one; validate them at a time of your choosing:
--
--     ALTER TABLE agent_experience.confidence_evidence VALIDATE CONSTRAINT confidence_evidence_event_when_counted_or_recorded_only;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_moves_or_recorded_only;
--
-- BUILDING THE INDEX WITHOUT BLOCKING APPENDS. A plain CREATE INDEX takes a SHARE lock on the ledger and blocks
-- every evidence append while it builds: imperceptible on a small ledger, a write outage on a long one. On a large
-- ledger, build it first, outside the migrator (CONCURRENTLY cannot run inside its transaction), and IF NOT EXISTS
-- then makes this script's own statement a no-op:
--
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_confidence_evidence_key_with_event
--         ON agent_experience.confidence_evidence (experience_id, independence_key)
--         WHERE event_id IS NOT NULL;
--
-- CONCURRENTLY can leave an INVALID index behind if it fails; check with
--     SELECT indisvalid FROM pg_index WHERE indexrelid = 'agent_experience.ix_confidence_evidence_key_with_event'::regclass;
-- and DROP INDEX CONCURRENTLY and retry if it comes back false. Do this before migrating, not after.
--
-- LOCKS. Each ADD CONSTRAINT ... NOT VALID takes a SHARE ROW EXCLUSIVE lock, and the DROP CONSTRAINT an ACCESS
-- EXCLUSIVE one, each for an instant. An ACCESS EXCLUSIVE request queues behind any long-running reader of the ledger
-- and then blocks everything queued behind it; on a busy database run the migrator with a lock_timeout (for example
-- ALTER ROLE <owner> SET lock_timeout = '5s') and retry. The new constraint is added before the old one is dropped,
-- in the migrator's one transaction, so the ledger is never without the rule.
--
-- PRIVILEGES. No table, column, function or trigger is added, and an index needs no grant, so the application
-- role's manifest is unchanged. Both ledgers stay append-only (0007's triggers).
--
-- WHAT THIS BINDS, AND WHAT IT DOES NOT. A writer that bypasses AgentExperience.Core can label a row 'HostTrusted'
-- exactly as it could always insert any admission; what it cannot do is write an uncounted event that is not labelled
-- so, or one that moves the score or the status while moving no counter.

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'confidence_evidence_event_when_counted_or_recorded_only'
          AND conrelid = 'agent_experience.confidence_evidence'::regclass)
    THEN
        ALTER TABLE agent_experience.confidence_evidence
            ADD CONSTRAINT confidence_evidence_event_when_counted_or_recorded_only
            CHECK ((event_id IS NOT NULL) = counted
                   OR (event_id IS NOT NULL
                       AND NOT counted
                       AND admission IS NOT DISTINCT FROM 'HostTrusted'
                       AND new_reuse_confidence = prior_reuse_confidence))
            NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_moves_or_recorded_only'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_moves_or_recorded_only
            CHECK (confidence_evidence_id IS NULL
                   OR new_supporting_validations <> prior_supporting_validations
                   OR new_contradictions <> prior_contradictions
                   OR (confidence_admission IS NOT DISTINCT FROM 'HostTrusted'
                       AND new_reuse_confidence = prior_reuse_confidence
                       AND (prior_status IS NULL OR prior_status = current_status)))
            NOT VALID;
    END IF;
END
$body$;

ALTER TABLE agent_experience.confidence_evidence
    DROP CONSTRAINT IF EXISTS confidence_evidence_event_only_when_counted;

CREATE INDEX IF NOT EXISTS ix_confidence_evidence_key_with_event
    ON agent_experience.confidence_evidence (experience_id, independence_key)
    WHERE event_id IS NOT NULL;
