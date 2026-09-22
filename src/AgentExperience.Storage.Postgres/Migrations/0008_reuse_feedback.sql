-- AgentExperience.NET: the reuse feedback ledger.
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0007, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- Two tables, mirroring the evidence ledger 0007 created.
--
-- 1. agent_experience.reuse_feedback: one row per submission. What run it is about, which scope it
--    happened in, how the run came out, what the host measured, which experimental condition it belongs
--    to, and -- the whole point -- whether anything ATTRIBUTED the run's outcome to the records it saw.
--
-- 2. agent_experience.reuse_feedback_exposures: one row per record the run was exposed to, and whether
--    attribution named it. An attributed row also carries the confidence evidence ID that was derived
--    for it, so this ledger and agent_experience.confidence_evidence can be joined by an auditor.
--
-- EXPOSURE IS NOT ATTRIBUTION, AND THE DEFAULT MOVES NOTHING. Records being injected into a run that
-- then succeeded says only that both things happened. benefit is therefore 'Unknown' unless
-- attribution_source names one of the two shapes this library accepts, and a row with benefit 'Unknown'
-- has no evidence_id on any of its exposures and produced no confidence submission at all. That is not a
-- gap to be filled in later by a smarter query: it is the honest answer, and the CHECK below makes
-- 'Unknown' and 'no attribution' the same fact rather than two columns that could drift apart.
--
-- claimed_benefit IS RECORDED AND NEVER ACTED ON. It is what the caller believed. Keeping it visible is
-- better than discarding it -- an operator can compare what hosts claim against what evidence
-- established -- but nothing in this schema or in AgentExperience.Core ever promotes it into benefit.
-- A caller asserting that memory helped is data about the caller, not evidence about the record.
--
-- THE RUN AND THE VERIFICATION ROUND ARE A HOST TRUST BOUNDARY. Exactly as 0007 states for
-- confidence_evidence, and for the same reason: run_id and verification_round_id are what the derived
-- confidence evidence is keyed on, there is no foreign key behind either, and nothing in this schema can
-- check that a run happened or that a round was closed. A caller inventing them gets a fresh independence
-- key every time. reviewer_identity is the same boundary and is the one the library enforces: it is the
-- host's AuthorizationContext.PrincipalId, taken from the authorization context and never from the
-- submission, because the count of distinct human reviewers is what the independence rule protects.
--
-- A HUMAN ASSESSMENT IS THE WEAKEST BOUNDARY HERE, AND IT IS STILL A HOST TRUST BOUNDARY. Nothing in
-- this schema or in AgentExperience.Core can check that a human made an assessment, that they saw the
-- run, or that they meant it. What is enforced is narrow: the reviewer is the host's
-- AuthorizationContext.PrincipalId rather than anything on the submission, assessment_id names a review
-- the host established, and one reviewer's opinion about one run counts once. Because the caller
-- supplies run_id, a host that lets agent output populate run_id or assessment_id has handed the agent a
-- fresh independence key on every call -- and therefore the ability to contest its own stored lessons
-- repeatedly. Establish both from your own review bookkeeping, exactly as you establish
-- AuthorizationContext, and never from anything an agent produced.
--
-- IDEMPOTENCY IS THE FEEDBACK ID. feedback_id is the primary key, and the exposures are keyed on it, so
-- one submission can be written exactly once. AgentExperience.Core compares a colliding submission's
-- stored content against the new one: identical is the original replayed and writes nothing; anything
-- else is refused with nothing written. The derived evidence IDs are a function of the feedback ID and
-- the experience ID, so a retry after a partial failure re-derives the same IDs and converges on the
-- confidence ledger's own idempotency rather than counting an observation twice.
--
-- evidence_id SAYS WHICH ID, NOT THAT IT LANDED. An exposure's evidence_id is DERIVED from
-- (feedback_id, experience_id) and is written with the exposure, before any confidence submission is
-- attempted -- because the exposure must be durable first. So an attributed exposure whose record turned
-- out ineligible or unresolved, or whose commit failed, carries an evidence_id with no row in
-- agent_experience.confidence_evidence. That is not a dangling reference to be cleaned up: it is the
-- outstanding work, and it is exactly what a retry of the same feedback converges on. Read it with a
-- LEFT JOIN, never an inner one, which would silently drop precisely the rows worth looking at:
--
--     SELECT x.feedback_id, x.experience_id, x.evidence_id
--     FROM agent_experience.reuse_feedback_exposures x
--     LEFT JOIN agent_experience.confidence_evidence ce ON ce.evidence_id = x.evidence_id
--     WHERE x.attributed AND ce.evidence_id IS NULL;   -- attributed, not yet counted
--
-- NOTHING HERE MOVES A SCORE. This ledger records exposure and attribution. Confidence moves only
-- through the lifecycle event path 0007 guards, in its own transaction, after these rows are committed --
-- which is why the ordering matters: what the run saw is durable even if every score submission then
-- fails, and a failed one is retried by resubmitting the same feedback.
--
-- NO FOREIGN KEY TO experience_records, ON PURPOSE, and none to lifecycle_events or confidence_evidence.
-- An exposed record that has since been revoked, or that never existed in this scope, must still be
-- recordable: "the run saw an ID that resolves to nothing here" is a fact worth keeping, and a foreign
-- key would turn it into a write failure. The same reasoning 0007 gives for its own missing key.
--
-- UPGRADING AN EXISTING DATABASE. Both tables are created by this script, so on any database the migrator
-- has journaled they start empty and every constraint on them is plain: there is nothing to scan and
-- nothing to reconcile. The one exception is the exposures-to-submissions foreign key, which is added
-- with ALTER TABLE ... NOT VALID exactly as 0007's CHECKs are, because CREATE TABLE IF NOT EXISTS is a
-- no-op against a database whose schema was applied by hand -- and such a database may already hold rows
-- this script never saw. New and updated rows are checked from this moment on; existing rows are not
-- scanned. After upgrading, confirm and then validate at a time of your choosing:
--
--     SELECT x.feedback_id FROM agent_experience.reuse_feedback_exposures x
--     WHERE NOT EXISTS (SELECT 1 FROM agent_experience.reuse_feedback f WHERE f.feedback_id = x.feedback_id);
--
-- Once it returns nothing:
--
--     ALTER TABLE agent_experience.reuse_feedback_exposures
--         VALIDATE CONSTRAINT reuse_feedback_exposures_submission_fkey;
--
-- VALIDATE takes only a SHARE UPDATE EXCLUSIVE lock, so it does not block reads or writes.
--
-- THE UNIQUE INDEXES ARE NOT FREE ON A LARGE, HAND-APPLIED TABLE. ux_reuse_feedback_exposures_evidence and
-- ux_reuse_feedback_exposures_ordinal are built with plain CREATE UNIQUE INDEX, which takes a SHARE lock
-- on the table and therefore blocks appends for the duration of the build. On the empty table this script
-- creates that is imperceptible; on a hand-applied table with a long history it is a write outage. A
-- deployment that cannot take one should create them out of band *before* running this script --
-- CREATE UNIQUE INDEX ... CONCURRENTLY cannot run inside the migrator's per-script transaction, and
-- IF NOT EXISTS then makes this script's own statements no-ops:
--
--     CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_reuse_feedback_exposures_evidence
--         ON agent_experience.reuse_feedback_exposures (evidence_id)
--         WHERE evidence_id IS NOT NULL;
--     CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ux_reuse_feedback_exposures_ordinal
--         ON agent_experience.reuse_feedback_exposures (feedback_id, ordinal);
--
-- CONCURRENTLY can leave an INVALID index behind if it fails; check with
-- "SELECT indisvalid FROM pg_index WHERE indexrelid = 'agent_experience.ux_reuse_feedback_exposures_evidence'::regclass",
-- and DROP INDEX CONCURRENTLY and retry if it comes back false. Do this before migrating, not after, so the
-- script never has to choose between blocking and running unguarded.
--
-- RETENTION. Deferred to roadmap story 4.5 along with the confidence ledger's, and for the same reason:
-- the append-only triggers below mean a retention pass is a deliberate, documented operation by the
-- tables' owner, not something an application role does by accident. See 0006's header for the runbook
-- and for exactly what the triggers do and do not bind.

CREATE TABLE IF NOT EXISTS agent_experience.reuse_feedback (
    feedback_id           uuid             NOT NULL,

    -- The run the records were injected into. NOT the record's own source run, and not checked by
    -- anything here: see the host trust boundary above.
    run_id                uuid             NOT NULL,

    tenant_id             text             NOT NULL,
    application_id        text             NOT NULL,
    project_id            text             NOT NULL,
    team_id               text             NULL,
    agent_id              text             NULL,
    user_id               text             NULL,

    run_outcome           text             NOT NULL,

    -- What the caller believed, kept for audit and analysis; never promoted into benefit.
    claimed_benefit       text             NOT NULL,

    -- What attribution actually established. 'Unknown' exactly when attribution_source is 'None'.
    benefit               text             NOT NULL,
    attribution_source    text             NOT NULL,

    -- The host's AuthorizationContext.PrincipalId, for a human assessment only.
    reviewer_identity     text             NULL,

    -- The host-established review a human judgement came out of. Required for that shape: without it a
    -- human attribution is a benefit, a list of record IDs and a free-text string, which is exactly the
    -- bare claim claimed_benefit is refused for. It is not a proof that a human judged anything -- see
    -- the trust boundary above -- but it makes a moved score traceable back to a review that exists.
    assessment_id         uuid             NULL,

    -- The comparative evaluator's identity, for that shape only.
    evaluator_id          text             NULL,

    -- The verification round: the machine independence key's second half for a comparative result, and
    -- audit-only for a human assessment (whose evidence is keyed on the reviewer and the run instead, so
    -- a round the reviewer chose must never reach the key).
    verification_round_id uuid             NULL,

    -- The assessment's rationale or the evaluator's summary, recorded as the derived evidence's detail.
    rationale             text             NULL,

    -- The evidence a comparative result reached its conclusion from, by ID, so an auditor sees what a
    -- moved score rested on rather than only the evaluator's own summary of it.
    evidence_ids          uuid[]           NULL,

    -- When the assessment or the comparison was made, as distinct from when the feedback was observed.
    attributed_at         timestamptz      NULL,

    -- A named kind plus a number, so a host records what it actually measured rather than a fixed metric
    -- this library invents. Nothing here interprets it and nothing ranks on it.
    measure_kind          text             NOT NULL,
    measure_value         double precision NOT NULL,

    -- The experimental condition this run was declared to belong to, so a later measurement aggregates
    -- conditions that were named up front instead of selecting subsets after the fact.
    trial_label           text             NULL,

    observed_at           timestamptz      NOT NULL,
    recorded_at           timestamptz      NOT NULL,

    CONSTRAINT reuse_feedback_pkey PRIMARY KEY (feedback_id),
    CONSTRAINT reuse_feedback_feedback_id_not_empty CHECK (feedback_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT reuse_feedback_run_id_not_empty CHECK (run_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT reuse_feedback_scope_not_blank CHECK (
        tenant_id ~ '[^[:space:]]' AND application_id ~ '[^[:space:]]' AND project_id ~ '[^[:space:]]'),
    CONSTRAINT reuse_feedback_run_outcome_known CHECK (run_outcome IN ('Unknown', 'Verified', 'Failed')),
    CONSTRAINT reuse_feedback_claimed_benefit_known CHECK (claimed_benefit IN ('Unknown', 'Improved', 'Harmed')),
    CONSTRAINT reuse_feedback_benefit_known CHECK (benefit IN ('Unknown', 'Improved', 'Harmed')),
    CONSTRAINT reuse_feedback_attribution_source_known CHECK (
        attribution_source IN ('None', 'HumanAssessment', 'ComparativeEvaluation')),

    -- "No attribution" and "benefit Unknown" are one fact, written in two columns. Without this they
    -- could drift, and a row could claim an improvement nothing attributed.
    CONSTRAINT reuse_feedback_benefit_needs_attribution CHECK (
        (attribution_source = 'None') = (benefit = 'Unknown')),

    -- Each attribution shape carries exactly the identifiers its derived evidence is keyed on, and not
    -- the other's. A human row with no reviewer, or a comparative row with no round, would produce
    -- evidence with no independence key -- and every such submission would then count.
    CONSTRAINT reuse_feedback_human_names_its_reviewer CHECK (
        attribution_source <> 'HumanAssessment'
        OR (reviewer_identity ~ '[^[:space:]]'
            AND assessment_id IS NOT NULL
            AND assessment_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND evaluator_id IS NULL
            AND evidence_ids IS NULL
            AND (verification_round_id IS NULL
                 OR verification_round_id <> '00000000-0000-0000-0000-000000000000'::uuid))),
    CONSTRAINT reuse_feedback_comparative_names_its_round CHECK (
        attribution_source <> 'ComparativeEvaluation'
        OR (evaluator_id ~ '[^[:space:]]'
            AND verification_round_id IS NOT NULL
            AND verification_round_id <> '00000000-0000-0000-0000-000000000000'::uuid
            AND reviewer_identity IS NULL
            AND assessment_id IS NULL
            AND evidence_ids IS NOT NULL
            AND array_length(evidence_ids, 1) >= 1
            AND array_position(evidence_ids, NULL) IS NULL)),
    CONSTRAINT reuse_feedback_unattributed_names_nothing CHECK (
        attribution_source <> 'None'
        OR (reviewer_identity IS NULL
            AND evaluator_id IS NULL
            AND verification_round_id IS NULL
            AND assessment_id IS NULL
            AND rationale IS NULL
            AND evidence_ids IS NULL
            AND attributed_at IS NULL)),

    -- An attribution says why, and when. A row that moved scores with no auditable reason, or none an
    -- auditor can place in time, is the shape a later reader cannot reconstruct.
    CONSTRAINT reuse_feedback_attribution_states_its_reason CHECK (
        attribution_source = 'None' OR (rationale ~ '[^[:space:]]' AND attributed_at IS NOT NULL)),

    CONSTRAINT reuse_feedback_measure_kind_not_blank CHECK (measure_kind ~ '[^[:space:]]'),

    -- PostgreSQL orders NaN above every number and treats it as equal to itself, so a stray NaN would
    -- sort to the top of any future aggregation rather than being obviously wrong. Refuse it here.
    CONSTRAINT reuse_feedback_measure_value_finite CHECK (
        measure_value <> 'NaN'::double precision
        AND measure_value <> 'Infinity'::double precision
        AND measure_value <> '-Infinity'::double precision),

    CONSTRAINT reuse_feedback_trial_label_not_blank CHECK (trial_label IS NULL OR trial_label ~ '[^[:space:]]'),
    CONSTRAINT reuse_feedback_reviewer_identity_not_blank CHECK (reviewer_identity IS NULL OR reviewer_identity ~ '[^[:space:]]'),
    CONSTRAINT reuse_feedback_evaluator_id_not_blank CHECK (evaluator_id IS NULL OR evaluator_id ~ '[^[:space:]]')
);

CREATE TABLE IF NOT EXISTS agent_experience.reuse_feedback_exposures (
    feedback_id   uuid    NOT NULL,
    experience_id uuid    NOT NULL,

    -- The exposure's position in the stored submission. AgentExperience.Core orders the records by
    -- experience_id before deriving ordinals, deliberately *not* by the order the caller listed them:
    -- the set of records a run saw is the fact, the order they were typed in is not, and letting that
    -- order into the stored submission would make two hosts submitting the same feedback with the
    -- records listed differently collide as a conflict no retry could ever resolve.
    ordinal       integer NOT NULL,

    attributed    boolean NOT NULL,

    -- The confidence evidence ID derived from (feedback_id, experience_id). Present exactly when this
    -- exposure was attributed, and the join between this ledger and confidence_evidence.
    evidence_id   uuid    NULL,

    CONSTRAINT reuse_feedback_exposures_pkey PRIMARY KEY (feedback_id, experience_id),
    CONSTRAINT reuse_feedback_exposures_experience_id_not_empty CHECK (
        experience_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    -- Mirrors AgentExperience.Abstractions' ExperienceReuseFeedback.MaxExposedRecords. Ordinals are
    -- unique per submission and dense from zero, so bounding the ordinal bounds the fan-out a single
    -- submission can ask for -- which is the only bound on it, since each attributed record costs its
    -- own transaction.
    CONSTRAINT reuse_feedback_exposures_ordinal_in_range CHECK (ordinal >= 0 AND ordinal < 64),
    CONSTRAINT reuse_feedback_exposures_evidence_id_not_empty CHECK (
        evidence_id IS NULL OR evidence_id <> '00000000-0000-0000-0000-000000000000'::uuid),

    -- Exactly the attributed exposures carry an evidence ID, because exactly they produced a confidence
    -- submission. An unattributed row with an evidence ID would claim a score moved for a record nothing
    -- attributed anything to.
    CONSTRAINT reuse_feedback_exposures_evidence_only_when_attributed CHECK ((evidence_id IS NOT NULL) = attributed)
);

-- One derived evidence ID belongs to one exposure. The derivation is a pure function of the feedback and
-- the experience, so a collision here means the derivation was bypassed rather than that two observations
-- coincided.
CREATE UNIQUE INDEX IF NOT EXISTS ux_reuse_feedback_exposures_evidence
    ON agent_experience.reuse_feedback_exposures (evidence_id)
    WHERE evidence_id IS NOT NULL;

-- One rank per submission, so the caller's ordering reads back unambiguously.
CREATE UNIQUE INDEX IF NOT EXISTS ux_reuse_feedback_exposures_ordinal
    ON agent_experience.reuse_feedback_exposures (feedback_id, ordinal);

-- No other index is created here on purpose, following 0007: the only reads this story performs are by
-- feedback_id, which the primary keys already serve. The aggregations roadmap story 4.4 needs -- by run,
-- by trial label, by scope -- belong with the queries that justify them, not ahead of them.

DO $body$
BEGIN
    -- Deferred, exactly as 0007's CHECKs are: this script creates both tables, so on a journaled database
    -- there is nothing to scan -- but CREATE TABLE IF NOT EXISTS is a no-op against a hand-applied schema
    -- that may already hold rows, and a validating ADD CONSTRAINT would scan them at startup. See the
    -- header for the confirm-then-VALIDATE step.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'reuse_feedback_exposures_submission_fkey'
          AND conrelid = 'agent_experience.reuse_feedback_exposures'::regclass)
    THEN
        ALTER TABLE agent_experience.reuse_feedback_exposures
            ADD CONSTRAINT reuse_feedback_exposures_submission_fkey
            FOREIGN KEY (feedback_id) REFERENCES agent_experience.reuse_feedback (feedback_id)
            NOT VALID;
    END IF;
END
$body$;

-- Both tables are append-only for the same reason the event logs and the evidence ledger are: a row that
-- could be edited or removed could rewrite what a run was exposed to after the fact, or free a derived
-- evidence ID so one observation could be submitted again under a fresh claim. 0006's function serves
-- them unchanged -- its message names the table it fired on.
DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'reuse_feedback_append_only'
          AND tgrelid = 'agent_experience.reuse_feedback'::regclass)
    THEN
        CREATE TRIGGER reuse_feedback_append_only
            BEFORE UPDATE OR DELETE ON agent_experience.reuse_feedback
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'reuse_feedback_no_truncate'
          AND tgrelid = 'agent_experience.reuse_feedback'::regclass)
    THEN
        CREATE TRIGGER reuse_feedback_no_truncate
            BEFORE TRUNCATE ON agent_experience.reuse_feedback
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'reuse_feedback_exposures_append_only'
          AND tgrelid = 'agent_experience.reuse_feedback_exposures'::regclass)
    THEN
        CREATE TRIGGER reuse_feedback_exposures_append_only
            BEFORE UPDATE OR DELETE ON agent_experience.reuse_feedback_exposures
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'reuse_feedback_exposures_no_truncate'
          AND tgrelid = 'agent_experience.reuse_feedback_exposures'::regclass)
    THEN
        CREATE TRIGGER reuse_feedback_exposures_no_truncate
            BEFORE TRUNCATE ON agent_experience.reuse_feedback_exposures
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;
END
$body$;

ALTER TABLE agent_experience.reuse_feedback ENABLE ALWAYS TRIGGER reuse_feedback_append_only;
ALTER TABLE agent_experience.reuse_feedback ENABLE ALWAYS TRIGGER reuse_feedback_no_truncate;
ALTER TABLE agent_experience.reuse_feedback_exposures ENABLE ALWAYS TRIGGER reuse_feedback_exposures_append_only;
ALTER TABLE agent_experience.reuse_feedback_exposures ENABLE ALWAYS TRIGGER reuse_feedback_exposures_no_truncate;
