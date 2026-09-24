-- AgentExperience.NET: a sharing grant's disclosure level -- how much of a borrowed record injection may
-- show the recipient's model.
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0010, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT THE LEVEL GOVERNS. Since 4.6 a verified record's injected block carries an "Approach:" line naming
-- the tools its successful attempts used. A record borrowed through a grant therefore showed the LENDING
-- scope's tool names to the borrower's model. The level is the owner's control over that:
--     LessonOnly         the block omits the Approach: line and says the grant withholds it (the default)
--     LessonAndApproach  the block renders the Approach: line exactly as the owner scope would see it
-- It governs only what the MAF adapter renders. The record a store hands back to host code is complete
-- either way, exactly as before.
--
-- UPGRADING AN EXISTING DATABASE -- THIS CHANGES BEHAVIOUR. Every grant already stored becomes LessonOnly
-- through the column default: least disclosure is the default. A borrowed record's Approach: line
-- therefore DISAPPEARS from injected blocks the moment this script applies, until the owner issues a new
-- grant with LessonAndApproach. The level is immutable (see the trigger below), so to restore the line,
-- revoke the old grant through IExperienceGrantStore.RevokeAsync and issue a replacement; 0005's one-
-- active-grant index permits the replacement once the old one is revoked, so the recipient has no access
-- between the two calls.
--
-- DEPLOYMENT ORDER: RUN THIS SCRIPT, THEN DEPLOY THE BUILD THAT READS IT, AND STOP OLDER WRITERS FIRST.
-- The new build selects g.disclosure in every grant-joined read, so against a pre-0011 schema those reads fail
-- with 42703 (undefined_column). An older build against this schema cannot write grant events or access rows,
-- because the *_disclosure_recorded CHECKs below refuse a row without a level. Both failures are loud on purpose;
-- nothing falls back silently.
--
-- WHAT THE LEVEL DOES NOT COVER. Only the Approach: line. The lesson, reuse guidance, preconditions and warnings
-- are the reflector's prose and are rendered unfiltered under either level.
--
-- WHERE THE LEVEL IS READ. From the same LEFT JOIN LATERAL row that names the permitting grant, never from
-- a second lookup, so the level injection honours and the level the access row records are the level of
-- the grant that actually admitted the read.
--
-- THE TWO LEDGERS. experience_grant_events and experience_grant_access each gain a NULLABLE disclosure
-- column, because their existing rows were written before the level existed and a level cannot be
-- backfilled into an append-only trail (0006's triggers refuse the UPDATE anyway). NULL means "not
-- recorded". New rows must carry one: that rule is a CHECK added NOT VALID, exactly as 0009 added its
-- lifetime ceiling, so existing rows are not scanned and are never re-checked. There is nothing to
-- VALIDATE afterwards -- the pre-0011 rows are NULL by definition and would fail it -- so leave the two
-- *_disclosure_recorded constraints NOT VALID. The access row keeps its level after the grant row itself
-- is purged: it is a copy made at delivery, not a join.
--
-- ALTER TABLE ... ADD COLUMN with a constant default does not rewrite the table on PostgreSQL 11 and
-- later, and adding a nullable column without a default never does, so this script is cheap on a large
-- ledger. It does take a brief ACCESS EXCLUSIVE lock on each of the three tables.

-- The grant's level. NOT NULL with a default, so a pre-0011 row reads LessonOnly and a writer bypassing
-- this library that names no level stores the least disclosure too.
ALTER TABLE agent_experience.experience_grants
    ADD COLUMN IF NOT EXISTS disclosure text NOT NULL DEFAULT 'LessonOnly';

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grants_disclosure_known'
          AND conrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grants
            ADD CONSTRAINT experience_grants_disclosure_known
            CHECK (disclosure IN ('LessonOnly', 'LessonAndApproach'));
    END IF;
END
$body$;

-- The issue and revoke events copy the grant's level, so the administration trail says what was shared,
-- not only with whom.
ALTER TABLE agent_experience.experience_grant_events
    ADD COLUMN IF NOT EXISTS disclosure text NULL;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grant_events_disclosure_known'
          AND conrelid = 'agent_experience.experience_grant_events'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grant_events
            ADD CONSTRAINT experience_grant_events_disclosure_known
            CHECK (disclosure IS NULL OR disclosure IN ('LessonOnly', 'LessonAndApproach'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grant_events_disclosure_recorded'
          AND conrelid = 'agent_experience.experience_grant_events'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grant_events
            ADD CONSTRAINT experience_grant_events_disclosure_recorded
            CHECK (disclosure IS NOT NULL)
            NOT VALID;
    END IF;
END
$body$;

-- Every delivery records the level it was delivered under: get, text, and vector alike.
ALTER TABLE agent_experience.experience_grant_access
    ADD COLUMN IF NOT EXISTS disclosure text NULL;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grant_access_disclosure_known'
          AND conrelid = 'agent_experience.experience_grant_access'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grant_access
            ADD CONSTRAINT experience_grant_access_disclosure_known
            CHECK (disclosure IS NULL OR disclosure IN ('LessonOnly', 'LessonAndApproach'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grant_access_disclosure_recorded'
          AND conrelid = 'agent_experience.experience_grant_access'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grant_access
            ADD CONSTRAINT experience_grant_access_disclosure_recorded
            CHECK (disclosure IS NOT NULL)
            NOT VALID;
    END IF;
END
$body$;

-- THE LEVEL IS IMMUTABLE. It joins the identity pins of 0006's monotonicity trigger: widening a live
-- grant from LessonOnly to LessonAndApproach in place would disclose tool names nobody issued a grant
-- for, and narrowing it in place would rewrite what the issue event says was shared. To change it, revoke
-- the grant and issue a new one. 0006's body is restated here in full with the one added pin; CREATE OR
-- REPLACE keeps every trigger binding 0006 created, so the table is never unguarded.
CREATE OR REPLACE FUNCTION agent_experience.enforce_grant_monotonicity() RETURNS trigger AS $body$
BEGIN
    IF NEW.grant_id IS DISTINCT FROM OLD.grant_id
        OR NEW.experience_id IS DISTINCT FROM OLD.experience_id
        OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
        OR NEW.application_id IS DISTINCT FROM OLD.application_id
        OR NEW.project_id IS DISTINCT FROM OLD.project_id
        OR NEW.team_id IS DISTINCT FROM OLD.team_id
        OR NEW.agent_id IS DISTINCT FROM OLD.agent_id
        OR NEW.user_id IS DISTINCT FROM OLD.user_id
        OR NEW.recipient_tenant_id IS DISTINCT FROM OLD.recipient_tenant_id
        OR NEW.recipient_application_id IS DISTINCT FROM OLD.recipient_application_id
        OR NEW.recipient_project_id IS DISTINCT FROM OLD.recipient_project_id
        OR NEW.recipient_team_id IS DISTINCT FROM OLD.recipient_team_id
        OR NEW.recipient_agent_id IS DISTINCT FROM OLD.recipient_agent_id
        OR NEW.recipient_user_id IS DISTINCT FROM OLD.recipient_user_id
        OR NEW.reason IS DISTINCT FROM OLD.reason
        OR NEW.administrator_principal_id IS DISTINCT FROM OLD.administrator_principal_id
        OR NEW.issued_at IS DISTINCT FROM OLD.issued_at
        OR NEW.disclosure IS DISTINCT FROM OLD.disclosure
    THEN
        RAISE EXCEPTION
            'A grant names one record, one recipient, and one disclosure level for the life of the grant; issue a new grant instead.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF OLD.revoked_at IS NOT NULL AND NEW.revoked_at IS DISTINCT FROM OLD.revoked_at THEN
        RAISE EXCEPTION
            'A grant''s revocation is permanent: revoked_at cannot be cleared or changed once set.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF OLD.revocation_reason IS NOT NULL AND NEW.revocation_reason IS DISTINCT FROM OLD.revocation_reason THEN
        RAISE EXCEPTION
            'A grant''s revocation reason is part of the audit trail and cannot be changed once set.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF NEW.expires_at > OLD.expires_at THEN
        RAISE EXCEPTION
            'A grant''s expiry cannot be extended; issue a new grant instead.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    RETURN NEW;
END;
$body$ LANGUAGE plpgsql;
