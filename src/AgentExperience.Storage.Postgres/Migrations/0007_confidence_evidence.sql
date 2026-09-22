-- AgentExperience.NET: evidence-based reuse confidence.
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0006, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- Three things happen here.
--
-- 1. agent_experience.confidence_evidence: one row per submitted piece of evidence that a stored lesson
--    was reused and the reuse held up, or did not. Its unique index -- not application code -- is what
--    decides independence: the first submission for a key is the only one that moves a counter.
--
-- 2. lifecycle_events gains the columns that make a confidence update reconstructable from the audit
--    trail alone: the prior and new score, the prior and new counters, the evidence ID, the rule version,
--    and the actor the commit ran under. They are columns rather than payload JSON for the same reason
--    replacement_experience_id is: they are facts about one transition, and an auditor has to be able to
--    filter and aggregate them in SQL.
--
-- 3. enforce_record_projection (created in 0006) is replaced with a version that guards reuse_confidence,
--    supporting_validations, and contradictions exactly as it already guards status: they move only with
--    the revision a lifecycle event produced. An immutable event log beside freely rewritable counters
--    would prove nothing -- a direct UPDATE could set any score, and the log would keep describing the
--    counters the record no longer has.
--
-- THE SCORE IS A HEURISTIC. The number these columns carry is (1 + S) / (2 + S + F), Laplace's rule of
-- succession over independent observations. It is a monotone, bounded summary of how often reuse held up.
-- It is not calibrated against anything and it is not the probability that the next reuse will succeed.
-- Nothing in the database computes it: AgentExperience.Core does, from the record it read, and writes it
-- through the same revision-guarded UPDATE that moves the status. The rule version travels with every
-- update so a later rule change stays auditable against scores computed under an earlier one.
--
-- INDEPENDENCE, AND EXACTLY WHAT THE KEY GUARANTEES. independence_key is a stored generated column, derived
-- from source, run_id, verification_round_id, and reviewer_identity. Deriving it here means no writer picks
-- the key *string*: two submissions describing the same observation collide however they are phrased, and
-- the same string is computed independently by AgentExperience.Core.Confidence.ConfidenceIndependenceKey,
-- with a test pinning the two so neither side can drift into counting what the other deduplicates.
--
-- It does NOT stop a caller that invents the key's *inputs*. There is no foreign key from run_id or
-- verification_round_id to anything, and nothing in this schema can check that a run happened or that a
-- round was closed. A caller passing a fresh Guid for both on every submission gets a fresh key every time
-- and drives S -- and therefore the score -- as high as it likes. The run and the verification round are a
-- HOST TRUST BOUNDARY, exactly like reviewer_identity: a host must establish them the way it establishes
-- AuthorizationContext (from its own run bookkeeping and its own closed verification rounds) and must never
-- pass through an identifier an agent supplied. What this schema guarantees is that a host which does that
-- cannot then have its own observations counted twice.
--
-- Machine evidence keys on the run and the verification round; human evidence keys on the reviewer and the
-- run. The reviewer is the host's AuthorizationContext.PrincipalId, taken from the authorization context
-- and never from the submission, and it is compared ordinally and case-sensitively like every other
-- identity in this library -- so a host that issues the same principal under two spellings has two
-- reviewers, and the store rejects one with leading or trailing whitespace rather than guessing.
--
-- DUPLICATES ARE RECORDED, AND CHANGE NOTHING ELSE. The unique index is partial (WHERE counted), so a later
-- submission for a taken key is still inserted, with counted = false. That is why the index is partial
-- rather than plain: a plain unique index would have to reject the row, and the submission would vanish
-- from the audit trail.
--
-- Such a submission writes its ledger row and nothing else -- no counters, no status, no revision, no
-- updated_at, and no lifecycle event. "Nothing else" is meant literally, because the two obvious
-- exceptions are the harmful ones: refreshing updated_at would let one observation, replayed under fresh
-- evidence IDs, keep a record permanently recent for ranking and permanently un-expired; and writing status
-- would contest a record on the strength of an observation the independence rule had just declared already
-- counted. It writes no event for a structural reason too: an event must claim applied_revision =
-- expected_revision + 1, so an event that moved nothing would consume a revision the record never reaches
-- and wedge every later commit against the unique index on (experience_id, applied_revision).
--
-- That is why this table carries the prior and new numbers, the applied revision, and the applied status
-- itself rather than joining lifecycle_events for them: a row with no event has nothing to join to, and a
-- resubmission has to be answered with one coherent picture of one moment.
--
-- UPGRADING AN EXISTING DATABASE. Every CHECK added to the existing lifecycle_events table is
-- ADD CONSTRAINT ... NOT VALID, exactly as in 0006: new and updated rows are checked from this moment on,
-- existing rows are not scanned. The new columns are all NULL on existing rows, which every constraint
-- below admits, so validation would in fact succeed -- but a scan of a large, append-only log at startup
-- is a cost no deployment asked for, and the log cannot be repaired in place if it did fail. After
-- upgrading, confirm and then validate at a time of your choosing:
--
--     SELECT event_id FROM agent_experience.lifecycle_events
--     WHERE num_nonnulls(confidence_evidence_id, confidence_kind, confidence_source, confidence_run_id,
--                        confidence_rule_version, prior_reuse_confidence, new_reuse_confidence,
--                        prior_supporting_validations, new_supporting_validations,
--                        prior_contradictions, new_contradictions) NOT IN (0, 11)
--        OR (confidence_evidence_id IS NOT NULL AND replacement_experience_id IS NOT NULL)
--        OR (confidence_kind IS NOT NULL AND confidence_kind NOT IN ('Supporting', 'Contradicting'))
--        OR (confidence_source IS NOT NULL AND confidence_source NOT IN ('Machine', 'Human'))
--        OR prior_reuse_confidence < 0 OR prior_reuse_confidence > 1
--        OR new_reuse_confidence < 0 OR new_reuse_confidence > 1
--        OR prior_supporting_validations < 0 OR new_supporting_validations < 0
--        OR prior_contradictions < 0 OR new_contradictions < 0
--        OR actor !~ '[^[:space:]]';
--
-- Once it returns nothing:
--
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_actor_not_blank;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_all_or_nothing;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_kind_known;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_source_known;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_scores_in_range;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_counters_nonnegative;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_not_on_supersession;
--
-- VALIDATE takes only a SHARE UPDATE EXCLUSIVE lock, so it does not block reads or writes. The new table's
-- own constraints are plain: it starts empty, so there is nothing to scan and nothing to reconcile.
--
-- ONE INDEX HERE IS NOT FREE ON A LARGE LOG. ux_lifecycle_events_confidence_evidence is built with a plain
-- CREATE UNIQUE INDEX, which takes a SHARE lock on lifecycle_events and therefore blocks appends for the
-- duration of the build. On an empty or small log that is imperceptible; on a log with a long history it is
-- a write outage. A deployment that cannot take one should create the index out of band *before* running
-- this script -- CREATE UNIQUE INDEX ... CONCURRENTLY cannot run inside the migrator's per-script
-- transaction, and IF NOT EXISTS then makes the script's own statement a no-op:
--
--     CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_lifecycle_events_confidence_evidence
--         ON agent_experience.lifecycle_events (confidence_evidence_id)
--         WHERE confidence_evidence_id IS NOT NULL;
--
-- CONCURRENTLY can leave an INVALID index behind if it fails; check with
-- "SELECT indisvalid FROM pg_index WHERE indexrelid = 'agent_experience.ux_lifecycle_events_confidence_evidence'::regclass",
-- and DROP INDEX CONCURRENTLY and retry if it comes back false. Do this before migrating, not after, so the
-- script never has to choose between blocking and running unguarded.
--
-- WHAT THIS BINDS. Exactly what 0006's header says, and no more: ordinary writes from any role while the
-- triggers are enabled, including under session_replication_role = 'replica'. It does not bind a superuser
-- or the tables' owner, which can disable a trigger or drop a constraint first. See 0006 for the full
-- statement, the deletion and retention runbook, and why this is a guard against a bug or a careless
-- script rather than tamper-proofing.

CREATE TABLE IF NOT EXISTS agent_experience.confidence_evidence (
    evidence_id                  uuid             NOT NULL,
    experience_id                uuid             NOT NULL,

    -- The lifecycle event this submission produced, when it produced one. A submission whose key was
    -- already taken moves nothing, so it writes no event and this is NULL: an event has to claim a
    -- revision, and claiming one without moving the record would consume it forever.
    event_id                     uuid             NULL,

    kind                         text             NOT NULL,
    source                       text             NOT NULL,
    run_id                       uuid             NOT NULL,
    verification_round_id        uuid             NULL,
    reviewer_identity            text             NULL,
    counted                      boolean          NOT NULL,
    actor                        text             NULL,
    rule_version                 text             NOT NULL,
    detail                       text             NULL,
    recorded_at                  timestamptz      NOT NULL,

    -- The record as this submission left it. Deliberately duplicated from lifecycle_events rather than
    -- joined to it: an uncounted submission has no event to join to, and a replay has to report one
    -- coherent picture of one moment rather than a revision from here and a status from a later read.
    applied_revision             bigint           NOT NULL,
    applied_status               text             NOT NULL,
    prior_reuse_confidence       double precision NOT NULL,
    new_reuse_confidence         double precision NOT NULL,
    prior_supporting_validations integer          NOT NULL,
    new_supporting_validations   integer          NOT NULL,
    prior_contradictions         integer          NOT NULL,
    new_contradictions           integer          NOT NULL,

    -- Derived here, never accepted from a writer. STORED, because the unique index below is on it and a
    -- VIRTUAL column could not be indexed.
    independence_key             text             GENERATED ALWAYS AS (
        CASE source
            WHEN 'Machine' THEN 'machine:' || run_id::text || ':' || verification_round_id::text
            WHEN 'Human'   THEN 'human:' || reviewer_identity || ':' || run_id::text
        END) STORED,

    CONSTRAINT confidence_evidence_pkey PRIMARY KEY (evidence_id),
    CONSTRAINT confidence_evidence_evidence_id_not_empty CHECK (evidence_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT confidence_evidence_experience_id_not_empty CHECK (experience_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT confidence_evidence_event_id_not_empty CHECK (event_id IS NULL OR event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT confidence_evidence_run_id_not_empty CHECK (run_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT confidence_evidence_kind_known CHECK (kind IN ('Supporting', 'Contradicting')),
    CONSTRAINT confidence_evidence_source_known CHECK (source IN ('Machine', 'Human')),
    CONSTRAINT confidence_evidence_rule_version_not_blank CHECK (rule_version ~ '[^[:space:]]'),
    CONSTRAINT confidence_evidence_actor_not_blank CHECK (actor IS NULL OR actor ~ '[^[:space:]]'),
    CONSTRAINT confidence_evidence_applied_revision_positive CHECK (applied_revision > 0),
    CONSTRAINT confidence_evidence_applied_status_known CHECK (applied_status IN (
        'Candidate', 'Validated', 'Quarantined', 'Contested',
        'Stale', 'Superseded', 'Revoked', 'Reinforced')),
    CONSTRAINT confidence_evidence_scores_in_range CHECK (
        prior_reuse_confidence >= 0 AND prior_reuse_confidence <= 1
        AND new_reuse_confidence >= 0 AND new_reuse_confidence <= 1),

    -- Evidence only ever moves a counter up, and only ever by one: a row claiming otherwise is a rewrite
    -- of history rather than an observation.
    CONSTRAINT confidence_evidence_counters_move_up_by_at_most_one CHECK (
        prior_supporting_validations >= 0 AND prior_contradictions >= 0
        AND new_supporting_validations - prior_supporting_validations BETWEEN 0 AND 1
        AND new_contradictions - prior_contradictions BETWEEN 0 AND 1),

    -- counted is not an independent flag: it is exactly "a counter moved", read off the numbers, so a row
    -- can never claim to have counted while its own prior and new values say nothing happened.
    CONSTRAINT confidence_evidence_counted_matches_counters CHECK (
        counted = (new_supporting_validations <> prior_supporting_validations
                   OR new_contradictions <> prior_contradictions)),

    -- Exactly the counted submissions produce a lifecycle event, because exactly they move the record.
    CONSTRAINT confidence_evidence_event_only_when_counted CHECK ((event_id IS NOT NULL) = counted),

    -- Each source carries exactly the identifiers its key is made of, and not the other's. Without this,
    -- a machine row with no round (or a human row with no reviewer) would generate a NULL key, which a
    -- unique index cannot deduplicate -- every such submission would count.
    CONSTRAINT confidence_evidence_machine_names_its_round CHECK (
        source <> 'Machine'
        OR (verification_round_id IS NOT NULL
            AND verification_round_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND reviewer_identity IS NULL)),
    CONSTRAINT confidence_evidence_human_names_its_reviewer CHECK (
        source <> 'Human'
        OR (reviewer_identity ~ '[^[:space:]]' AND verification_round_id IS NULL)),
    CONSTRAINT confidence_evidence_independence_key_present CHECK (independence_key IS NOT NULL)
);

-- The whole independence rule, in one index. Partial, so a later submission for a taken key is still
-- stored (counted = false) rather than rejected: the counters must not move, but the submission is part of
-- the audit trail either way.
CREATE UNIQUE INDEX IF NOT EXISTS ux_confidence_evidence_independence
    ON agent_experience.confidence_evidence (experience_id, independence_key)
    WHERE counted;

-- No other index is created here on purpose. The only reads this story performs are by evidence_id (the
-- primary key) and the arbiter lookup the unique index above serves. Listing a record's ledger, a foreign
-- key to experience_records, and retention over this table all belong to roadmap story 4.5; the index each
-- of those needs belongs with the query that justifies it, not ahead of it.

ALTER TABLE agent_experience.lifecycle_events
    ADD COLUMN IF NOT EXISTS actor                           text             NULL,
    ADD COLUMN IF NOT EXISTS confidence_evidence_id          uuid             NULL,
    ADD COLUMN IF NOT EXISTS confidence_kind                 text             NULL,
    ADD COLUMN IF NOT EXISTS confidence_source               text             NULL,
    ADD COLUMN IF NOT EXISTS confidence_run_id               uuid             NULL,
    ADD COLUMN IF NOT EXISTS confidence_verification_round_id uuid            NULL,
    ADD COLUMN IF NOT EXISTS confidence_reviewer_identity    text             NULL,
    ADD COLUMN IF NOT EXISTS confidence_rule_version         text             NULL,
    ADD COLUMN IF NOT EXISTS confidence_detail               text             NULL,
    ADD COLUMN IF NOT EXISTS prior_reuse_confidence          double precision NULL,
    ADD COLUMN IF NOT EXISTS new_reuse_confidence            double precision NULL,
    ADD COLUMN IF NOT EXISTS prior_supporting_validations    integer          NULL,
    ADD COLUMN IF NOT EXISTS new_supporting_validations      integer          NULL,
    ADD COLUMN IF NOT EXISTS prior_contradictions            integer          NULL,
    ADD COLUMN IF NOT EXISTS new_contradictions              integer          NULL;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_actor_not_blank'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_actor_not_blank
            CHECK (actor IS NULL OR actor ~ '[^[:space:]]')
            NOT VALID;
    END IF;

    -- Either the event carries a whole confidence update or it carries none of one. A half-written update
    -- is the one shape that would make history unreconstructable: a new score with no prior one to compare
    -- it against says nothing at all.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_all_or_nothing'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_all_or_nothing
            CHECK (num_nonnulls(
                confidence_evidence_id, confidence_kind, confidence_source, confidence_run_id,
                confidence_rule_version, prior_reuse_confidence, new_reuse_confidence,
                prior_supporting_validations, new_supporting_validations,
                prior_contradictions, new_contradictions) IN (0, 11))
            NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_kind_known'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_kind_known
            CHECK (confidence_kind IS NULL OR confidence_kind IN ('Supporting', 'Contradicting'))
            NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_source_known'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_source_known
            CHECK (confidence_source IS NULL OR confidence_source IN ('Machine', 'Human'))
            NOT VALID;
    END IF;

    -- The same bounds experience_records states for the column these numbers are written to, so an event
    -- can never claim a score the projection could not hold.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_scores_in_range'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_scores_in_range
            CHECK ((prior_reuse_confidence IS NULL OR (prior_reuse_confidence >= 0 AND prior_reuse_confidence <= 1))
               AND (new_reuse_confidence IS NULL OR (new_reuse_confidence >= 0 AND new_reuse_confidence <= 1)))
            NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_counters_nonnegative'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_counters_nonnegative
            CHECK (COALESCE(prior_supporting_validations, 0) >= 0
               AND COALESCE(new_supporting_validations, 0) >= 0
               AND COALESCE(prior_contradictions, 0) >= 0
               AND COALESCE(new_contradictions, 0) >= 0)
            NOT VALID;
    END IF;

    -- Supersession and a confidence update are different facts about different things, and an event that
    -- claimed both would make the replacement chain and the evidence trail depend on each other.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_confidence_not_on_supersession'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_confidence_not_on_supersession
            CHECK (confidence_evidence_id IS NULL OR replacement_experience_id IS NULL)
            NOT VALID;
    END IF;
END
$body$;

-- One event applies at most one piece of evidence, and one piece of evidence rides at most one event.
CREATE UNIQUE INDEX IF NOT EXISTS ux_lifecycle_events_confidence_evidence
    ON agent_experience.lifecycle_events (confidence_evidence_id)
    WHERE confidence_evidence_id IS NOT NULL;

-- 0006's projection guard, extended to the three columns this story starts moving. The added rule is the
-- same shape as the status rule directly above it: these columns change only together with the revision
-- the lifecycle event produced, which is exactly what the store's revision-guarded UPDATE does and what no
-- direct UPDATE can imitate without first winning that guard.
CREATE OR REPLACE FUNCTION agent_experience.enforce_record_projection() RETURNS trigger AS $body$
BEGIN
    IF NEW.experience_id IS DISTINCT FROM OLD.experience_id THEN
        RAISE EXCEPTION
            'An Experience Record''s identity is fixed; its lifecycle events name it.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF NEW.revision < OLD.revision THEN
        RAISE EXCEPTION
            'An Experience Record''s revision only moves forward: % cannot follow %.', NEW.revision, OLD.revision
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF NEW.status IS DISTINCT FROM OLD.status AND NEW.revision <= OLD.revision THEN
        RAISE EXCEPTION
            'An Experience Record''s status changes only with the revision its lifecycle event produced.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF NEW.reuse_confidence IS DISTINCT FROM OLD.reuse_confidence
        OR NEW.supporting_validations IS DISTINCT FROM OLD.supporting_validations
        OR NEW.contradictions IS DISTINCT FROM OLD.contradictions
    THEN
        IF NEW.revision <= OLD.revision THEN
            RAISE EXCEPTION
                'An Experience Record''s reuse confidence and evidence counters change only with the revision '
                'of the lifecycle event that recorded the evidence for them.'
                USING ERRCODE = 'insufficient_privilege';
        END IF;

        -- Advancing the revision is not enough on its own: UPDATE ... SET reuse_confidence = 1,
        -- revision = revision + 1 would satisfy the rule above while no evidence said anything. The
        -- numbers have to be the ones an event already appended for exactly this revision, which is why
        -- the store writes the event first and the projection second, in one transaction.
        IF NOT EXISTS (
            SELECT 1 FROM agent_experience.lifecycle_events e
            WHERE e.experience_id = NEW.experience_id
              AND e.applied_revision = NEW.revision
              AND e.confidence_evidence_id IS NOT NULL
              AND e.new_reuse_confidence = NEW.reuse_confidence
              AND e.new_supporting_validations = NEW.supporting_validations
              AND e.new_contradictions = NEW.contradictions)
        THEN
            RAISE EXCEPTION
                'An Experience Record''s reuse confidence and evidence counters may only be set to the values '
                'a lifecycle event recorded for revision %.', NEW.revision
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;

    RETURN NEW;
END;
$body$ LANGUAGE plpgsql;

-- The evidence ledger is append-only for the same reason the event logs are: a row that could be edited
-- or removed could un-count an observation the counters already reflect, or free an independence key so
-- the same observation could be counted twice. 0006's function serves it unchanged -- its message names
-- the table it fired on.
DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'confidence_evidence_append_only'
          AND tgrelid = 'agent_experience.confidence_evidence'::regclass)
    THEN
        CREATE TRIGGER confidence_evidence_append_only
            BEFORE UPDATE OR DELETE ON agent_experience.confidence_evidence
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'confidence_evidence_no_truncate'
          AND tgrelid = 'agent_experience.confidence_evidence'::regclass)
    THEN
        CREATE TRIGGER confidence_evidence_no_truncate
            BEFORE TRUNCATE ON agent_experience.confidence_evidence
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;
END
$body$;

ALTER TABLE agent_experience.confidence_evidence ENABLE ALWAYS TRIGGER confidence_evidence_append_only;
ALTER TABLE agent_experience.confidence_evidence ENABLE ALWAYS TRIGGER confidence_evidence_no_truncate;
