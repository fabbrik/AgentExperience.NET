-- AgentExperience.NET: a third disclosure level -- a grant whose owner consents to showing selected argument
-- values of a borrowed record's approach (Story 7.1, KL-8).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0016, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT THE NEW LEVEL GOVERNS. 0011 gave a grant two levels: LessonOnly (the Approach: line is withheld) and
-- LessonAndApproach (the line is shown, tool names only). Since 6.2 a reader may allowlist argument keys whose
-- sanitized values the Approach: line shows -- but never on a borrowed record, because no grant was issued as
-- the owner's consent to that. This script adds the level that is:
--     LessonApproachAndArguments  the Approach: line, plus the values of the argument keys the OWNER named on
--                                 the grant AND the reader allowlisted for the same tool -- the intersection.
-- The owner's keys are stored on the grant, in approach_arguments, because the reader's configuration must
-- never widen what the owner consented to, and the owner's own injection options live in another host that
-- no read can consult. It governs only what the MAF adapter renders; a store returns the record complete.
--
-- UPGRADING AN EXISTING DATABASE CHANGES NOTHING THAT IS SHOWN. Every stored grant keeps its level, and its
-- approach_arguments is NULL. No borrowed argument value appears until an owner issues a new grant at the new
-- level: the level is immutable, so revoke the old grant and issue the replacement (0005's one-active-grant
-- index permits it once the old one is revoked).
--
-- DEPLOYMENT ORDER: RUN THIS SCRIPT, THEN DEPLOY THE BUILD THAT READS IT, AND STOP OLDER WRITERS FIRST. The
-- new build selects g.approach_arguments in every grant-joined record read, so against a pre-0017 schema those
-- reads fail with 42703 (undefined_column). An older build against this schema keeps working -- it never
-- names the column, and every level it can write is still accepted -- but it cannot decode a grant stored at
-- the new level (its grant store reports an unrecognized level, loudly), and it renders a record read through
-- one as LessonOnly, the least disclosure.
--
-- WHAT approach_arguments HOLDS: one JSON object of tool name -> array of argument keys (a key may be a dotted
-- path such as "options.mode"). Names only, never an argument value. It is host configuration rather than
-- captured content, so it is stored in the clear in encrypted mode too (0016 seals free text, and this is
-- not); an owner must not put anything secret into a tool name or an argument key. It is deleted with its
-- grant, so erasure (0010) and the expired-grant purge remove it from the live table like the grant itself.
--
-- THE THREE *_disclosure_known CHECKS ARE WIDENED, not added. Each is dropped and re-added with the new name in
-- its list, in one transaction, so no writer ever sees a table without one. 0011 added them validated. The two on
-- experience_grants and experience_grant_events -- small, administration-sized tables -- are re-validated here,
-- so they stay validated. The one on experience_grant_access, the per-read ledger that can be very large, is
-- re-added NOT VALID: every existing row satisfied the narrower CHECK that was just dropped, so it satisfies the
-- wider one, and a scan here would prove nothing while holding this transaction's ACCESS EXCLUSIVE lock on the
-- ledger for its duration. It binds every row written from now on; pg_constraint.convalidated reads false for it
-- until you validate it, at a time of your choosing (VALIDATE takes only SHARE UPDATE EXCLUSIVE):
--
--     ALTER TABLE agent_experience.experience_grant_access VALIDATE CONSTRAINT experience_grant_access_disclosure_known;
--
-- The two new CHECKs on approach_arguments are NOT VALID, like 0006's, 0007's and 0016's: every existing row has
-- it NULL and a level other than the new one, so both hold, and they bind every row written from now on.
--
-- THE PRIVILEGE MANIFEST (6.1) NEEDS NO NEW ENTRY. The application role already has table-level SELECT and
-- INSERT on experience_grants, which cover the new column, and UPDATE only on revoked_at and
-- revocation_reason, which do not: the role cannot change an allowlist, and the trigger below refuses anyone
-- else who tries.
--
-- ALTER TABLE ... ADD COLUMN of a nullable column without a default never rewrites the table. Each ALTER
-- takes a brief ACCESS EXCLUSIVE lock on its table.

ALTER TABLE agent_experience.experience_grants
    ADD COLUMN IF NOT EXISTS approach_arguments jsonb NULL;

DO $body$
BEGIN
    -- The widened level lists. Idempotent by content: a constraint that already names the new level is left
    -- alone, so a re-run (or a hand-applied schema) changes nothing.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grants_disclosure_known'
          AND conrelid = 'agent_experience.experience_grants'::regclass
          AND pg_get_constraintdef(oid) LIKE '%LessonApproachAndArguments%')
    THEN
        ALTER TABLE agent_experience.experience_grants
            DROP CONSTRAINT IF EXISTS experience_grants_disclosure_known;
        ALTER TABLE agent_experience.experience_grants
            ADD CONSTRAINT experience_grants_disclosure_known
            CHECK (disclosure IN ('LessonOnly', 'LessonAndApproach', 'LessonApproachAndArguments'))
            NOT VALID;
        ALTER TABLE agent_experience.experience_grants
            VALIDATE CONSTRAINT experience_grants_disclosure_known;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grant_events_disclosure_known'
          AND conrelid = 'agent_experience.experience_grant_events'::regclass
          AND pg_get_constraintdef(oid) LIKE '%LessonApproachAndArguments%')
    THEN
        ALTER TABLE agent_experience.experience_grant_events
            DROP CONSTRAINT IF EXISTS experience_grant_events_disclosure_known;
        ALTER TABLE agent_experience.experience_grant_events
            ADD CONSTRAINT experience_grant_events_disclosure_known
            CHECK (disclosure IS NULL OR disclosure IN ('LessonOnly', 'LessonAndApproach', 'LessonApproachAndArguments'))
            NOT VALID;
        ALTER TABLE agent_experience.experience_grant_events
            VALIDATE CONSTRAINT experience_grant_events_disclosure_known;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grant_access_disclosure_known'
          AND conrelid = 'agent_experience.experience_grant_access'::regclass
          AND pg_get_constraintdef(oid) LIKE '%LessonApproachAndArguments%')
    THEN
        ALTER TABLE agent_experience.experience_grant_access
            DROP CONSTRAINT IF EXISTS experience_grant_access_disclosure_known;
        ALTER TABLE agent_experience.experience_grant_access
            ADD CONSTRAINT experience_grant_access_disclosure_known
            CHECK (disclosure IS NULL OR disclosure IN ('LessonOnly', 'LessonAndApproach', 'LessonApproachAndArguments'))
            NOT VALID;
    END IF;

    -- The allowlist belongs to the new level and to nothing else: present exactly when the level is
    -- LessonApproachAndArguments. Keys under another level would read back as consent that level never gives,
    -- and the new level without keys would be consent to nothing.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grants_approach_arguments_level'
          AND conrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grants
            ADD CONSTRAINT experience_grants_approach_arguments_level
            CHECK ((disclosure = 'LessonApproachAndArguments') = (approach_arguments IS NOT NULL))
            NOT VALID;
    END IF;

    -- Its shape: a non-empty object whose every member is a non-empty array of strings. The adapter validates
    -- the keys themselves (length, characters, counts) before it writes; this is the floor under a writer that
    -- bypasses it. The paths are strict, so a filter never unwraps the array it is testing, and silent, so a
    -- structural error is NULL rather than an exception; IS TRUE then fails the check on any NULL rather than
    -- passing it.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grants_approach_arguments_shape'
          AND conrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grants
            ADD CONSTRAINT experience_grants_approach_arguments_shape
            CHECK (approach_arguments IS NULL OR (
                jsonb_typeof(approach_arguments) = 'object'
                AND approach_arguments <> '{}'::jsonb
                AND NOT jsonb_path_exists(approach_arguments, 'strict $.* ? (@.type() != "array" || @.size() == 0)', '{}'::jsonb, true)
                AND NOT jsonb_path_exists(approach_arguments, 'strict $.*[*] ? (@.type() != "string")', '{}'::jsonb, true)) IS TRUE)
            NOT VALID;
    END IF;
END
$body$;

-- THE ALLOWLIST IS IMMUTABLE, like the level. It joins the identity pins of the monotonicity trigger: widening
-- a live grant's keys in place would disclose argument values nobody issued a grant for. 0011's body is
-- restated here in full with the one added pin; CREATE OR REPLACE keeps every trigger binding 0006 created, so
-- the table is never unguarded. CREATE OR REPLACE also resets the function's SET clauses, so 0013's
-- search_path pin is restated right after it, in the same transaction.
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
        OR NEW.approach_arguments IS DISTINCT FROM OLD.approach_arguments
    THEN
        RAISE EXCEPTION
            'A grant names one record, one recipient, one disclosure level and one argument allowlist for the life of the grant; issue a new grant instead.'
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

ALTER FUNCTION agent_experience.enforce_grant_monotonicity()
    SET search_path = pg_catalog, agent_experience, pg_temp;
