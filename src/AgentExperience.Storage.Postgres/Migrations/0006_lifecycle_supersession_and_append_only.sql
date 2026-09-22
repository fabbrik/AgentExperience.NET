-- AgentExperience.NET: supersession's recorded replacement, and append-only enforced by the database.
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0005, so a database whose schema was applied by
-- hand can still be journaled. (This script is still unreleased and has only ever been applied to
-- throwaway test databases, so it was corrected in place during review, exactly as 0002 and 0005 were;
-- once this branch ships, the append-only rule applies to it as it does to 0001.)
--
-- Two things happen here.
--
-- 1. A superseding event names its replacement. replacement_experience_id is a column on the event, not a
--    field of the record's JSON payload: supersession is a fact about one transition, and the replacement
--    chain has to be walked in SQL to reject a cycle, which a payload field could not serve. It is
--    present exactly when the event moves a record to 'Superseded', stated as a CHECK so the rule holds
--    for a writer that bypasses the store. That CHECK reads a status *name*, so the status columns get an
--    enumeration CHECK in the same script -- otherwise the rule would be comparing against a column
--    constrained only to be non-blank, and a row storing 'superseded' or 'Superceded' would dodge it.
--
-- 2. The event logs stop being append-only by convention, the grant row stops being freely rewritable, and
--    the record projection stops being freely movable. See "WHAT THIS BINDS" below for the limits.
--
-- UPGRADING AN EXISTING DATABASE. Every CHECK added here is ADD CONSTRAINT ... NOT VALID: new and updated
-- rows are checked from this moment on, existing rows are not scanned. That is deliberate and not
-- laziness. A database written through 0001-0005 could hold a 'Superseded' lifecycle event with no
-- replacement -- the public port has always accepted one, because Core's transition table was never
-- applied by the store -- and a plain ADD CONSTRAINT validates immediately, so this script would abort at
-- startup on exactly the deployments that most need it. After upgrading, reconcile and then validate:
--
--     SELECT event_id, experience_id, current_status, prior_status
--     FROM agent_experience.lifecycle_events
--     WHERE (replacement_experience_id IS NOT NULL) <> (current_status = 'Superseded')
--        OR replacement_experience_id = experience_id
--        OR current_status NOT IN ('Candidate','Validated','Quarantined','Contested','Stale','Superseded','Revoked','Reinforced')
--        OR (prior_status IS NOT NULL AND prior_status NOT IN ('Candidate','Validated','Quarantined','Contested','Stale','Superseded','Revoked','Reinforced'));
--
-- Those rows cannot be repaired in place (the log is now append-only), so a deployment that finds any must
-- decide explicitly: leave them and keep the constraints NOT VALID, or purge them through the runbook
-- below. Once the query returns nothing:
--
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_prior_status_known;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_current_status_known;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_replacement_only_when_superseded;
--     ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_replacement_is_another_record;
--
-- VALIDATE takes only a SHARE UPDATE EXCLUSIVE lock, so it does not block reads or writes.
--
-- WHAT THIS BINDS, AND WHAT IT DOES NOT. Be precise, because a reader who assumes more would treat this as
-- tamper-proofing it is not:
--   * It binds ordinary INSERT/UPDATE/DELETE/TRUNCATE from any role, superusers included, *as long as the
--     triggers are enabled and in session_replication_role = 'origin' or 'local'*. The triggers below are
--     created ENABLE ALWAYS, so they also fire under session_replication_role = 'replica' -- the mode
--     logical-replication appliers and several restore and ETL tools run in, and the mode in which an
--     ordinary ENABLE trigger is skipped silently.
--   * It does NOT bind anyone who can ALTER TABLE these tables -- a superuser, or the tables' owner, which
--     the application role is because it created them. An owner can DISABLE TRIGGER, DROP TRIGGER, or drop
--     a constraint and then write whatever it likes. Row-level security and column-privilege REVOKE are no
--     stronger: neither binds an owner either.
--   * It does NOT survive a restore that recreates the tables without this script, and it says nothing
--     about backups or about anyone with filesystem access to the data directory.
-- So: a guard against a bug, a careless script, a compromised application path, or a replication apply
-- that would otherwise rewrite history -- not a guard against an administrator who has decided to tamper.
-- A deployment that needs tamper-evidence beyond this should ship the log off-box, or own these tables
-- with a role the application does not have.
--
-- DELETION AND RETENTION. There is now no supported way to delete an event row, and the logs carry
-- free-text `reason` and `producer` that a host may have filled with personal data. Until the library
-- ships a purge path (roadmap story 4.5, "delete and expire library-owned data", which will need a
-- SECURITY DEFINER purge function or time-partitioned logs -- this script cannot be edited once journaled),
-- purging is an explicit, audited operator action by the tables' owner:
--
--     BEGIN;
--     ALTER TABLE agent_experience.lifecycle_events DISABLE TRIGGER lifecycle_events_append_only;
--     DELETE FROM agent_experience.lifecycle_events WHERE ...;   -- always narrow, never unqualified
--     ALTER TABLE agent_experience.lifecycle_events ENABLE ALWAYS TRIGGER lifecycle_events_append_only;
--     COMMIT;
--
-- Do it in one transaction so the guard is never off across a failure, and record why outside the database.
-- Deleting an event row does not move the record's projection: reconcile experience_records afterwards.
--
-- The triggers raise SQLSTATE 42501 (insufficient_privilege), so a tamperer sees a permission failure
-- rather than a constraint that might look incidental. The store never updates or deletes either log, so no
-- supported code path can reach them.

ALTER TABLE agent_experience.lifecycle_events
    ADD COLUMN IF NOT EXISTS replacement_experience_id uuid NULL;

DO $body$
BEGIN
    -- The status names the rest of this script compares against. Without these, 'Superseded' is just one
    -- string among infinitely many a non-blank CHECK would accept.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_current_status_known'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_current_status_known
            CHECK (current_status IN (
                'Candidate', 'Validated', 'Quarantined', 'Contested',
                'Stale', 'Superseded', 'Revoked', 'Reinforced'))
            NOT VALID;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_prior_status_known'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_prior_status_known
            CHECK (prior_status IS NULL OR prior_status IN (
                'Candidate', 'Validated', 'Quarantined', 'Contested',
                'Stale', 'Superseded', 'Revoked', 'Reinforced'))
            NOT VALID;
    END IF;

    -- Present exactly for a supersession. A superseding event with no replacement would record that a
    -- record was replaced by nothing; a replacement on any other transition would record a relationship
    -- that transition did not create.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_replacement_only_when_superseded'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_replacement_only_when_superseded
            CHECK ((replacement_experience_id IS NOT NULL) = (current_status = 'Superseded'))
            NOT VALID;
    END IF;

    -- The one cycle a single row can state on its own. The longer chains are rejected by the store's
    -- recursive check inside the commit transaction; this catches the degenerate case whatever the writer.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'lifecycle_events_replacement_is_another_record'
          AND conrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        ALTER TABLE agent_experience.lifecycle_events
            ADD CONSTRAINT lifecycle_events_replacement_is_another_record
            CHECK (replacement_experience_id IS NULL OR replacement_experience_id <> experience_id)
            NOT VALID;
    END IF;
END
$body$;

-- The cycle check walks the chain by following an experience_id to whatever replaced it, one link per
-- recursion step. Partial, because only a superseding event has a replacement at all.
CREATE INDEX IF NOT EXISTS ix_lifecycle_events_replacement
    ON agent_experience.lifecycle_events (experience_id, replacement_experience_id)
    WHERE replacement_experience_id IS NOT NULL;

-- Append-only, enforced. One function serves both event logs and both trigger levels: the message names
-- the table it fired on, so an operator who hits it is told which log refused and why. TRUNCATE is handled
-- here because it does not fire FOR EACH ROW triggers at all -- without a statement-level trigger,
-- TRUNCATE would erase a whole audit log with no error.
CREATE OR REPLACE FUNCTION agent_experience.reject_event_log_mutation() RETURNS trigger AS $body$
BEGIN
    RAISE EXCEPTION
        'agent_experience.% is append-only: a stored event row cannot be %.',
        TG_TABLE_NAME,
        CASE TG_OP
            WHEN 'UPDATE' THEN 'updated'
            WHEN 'DELETE' THEN 'deleted'
            ELSE 'truncated away'
        END
        USING ERRCODE = 'insufficient_privilege';
END;
$body$ LANGUAGE plpgsql;

-- A grant's revocation is permanent, its expiry only ever moves closer, and the thing it names never
-- changes. The grant row itself stays updatable, because revoking one is an UPDATE -- it is the direction
-- of travel, and the identity, that are constrained. Without the identity pins, an UPDATE could re-point a
-- live grant at another record or another recipient and hand out access nobody ever issued.
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
    THEN
        RAISE EXCEPTION
            'A grant names one record and one recipient for the life of the grant; issue a new grant instead.'
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

-- Deleting a grant row that has audit events would undo a revocation the trail says happened: the row goes,
-- the events stay, and re-inserting the same grant_id restores access as if it had never been revoked.
-- A grant that has no events at all was never issued through the store and is left deletable, so a
-- half-written row can still be cleaned up.
CREATE OR REPLACE FUNCTION agent_experience.reject_audited_grant_delete() RETURNS trigger AS $body$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION
            'agent_experience.experience_grants cannot be truncated: its audit trail would outlive it.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF EXISTS (SELECT 1 FROM agent_experience.experience_grant_events e WHERE e.grant_id = OLD.grant_id) THEN
        RAISE EXCEPTION
            'A grant with an audit trail cannot be deleted: revoke it instead, so the trail and the row agree.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    RETURN OLD;
END;
$body$ LANGUAGE plpgsql;

-- The projection is the other half of every lifecycle commit, and an immutable log beside a freely
-- rewritable projection proves nothing: a direct UPDATE could set any status, or wind the revision back so
-- a replayed event applies twice. A revision only ever moves forward, and a status only ever changes
-- together with it -- which is exactly what the store's own revision-guarded UPDATE does.
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

    RETURN NEW;
END;
$body$ LANGUAGE plpgsql;

-- CREATE TRIGGER has no IF NOT EXISTS, and dropping one to recreate it would leave a window in which the
-- log is unguarded, so each is created only when it is absent. ENABLE ALWAYS is applied unconditionally
-- afterwards: it is a no-op on a trigger that already has it, and it is what makes the guards survive
-- session_replication_role = 'replica'.
DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'lifecycle_events_append_only'
          AND tgrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        CREATE TRIGGER lifecycle_events_append_only
            BEFORE UPDATE OR DELETE ON agent_experience.lifecycle_events
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'lifecycle_events_no_truncate'
          AND tgrelid = 'agent_experience.lifecycle_events'::regclass)
    THEN
        CREATE TRIGGER lifecycle_events_no_truncate
            BEFORE TRUNCATE ON agent_experience.lifecycle_events
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grant_events_append_only'
          AND tgrelid = 'agent_experience.experience_grant_events'::regclass)
    THEN
        CREATE TRIGGER experience_grant_events_append_only
            BEFORE UPDATE OR DELETE ON agent_experience.experience_grant_events
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grant_events_no_truncate'
          AND tgrelid = 'agent_experience.experience_grant_events'::regclass)
    THEN
        CREATE TRIGGER experience_grant_events_no_truncate
            BEFORE TRUNCATE ON agent_experience.experience_grant_events
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grants_monotonic'
          AND tgrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        CREATE TRIGGER experience_grants_monotonic
            BEFORE UPDATE ON agent_experience.experience_grants
            FOR EACH ROW EXECUTE FUNCTION agent_experience.enforce_grant_monotonicity();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grants_audited_delete'
          AND tgrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        CREATE TRIGGER experience_grants_audited_delete
            BEFORE DELETE ON agent_experience.experience_grants
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_audited_grant_delete();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grants_no_truncate'
          AND tgrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        CREATE TRIGGER experience_grants_no_truncate
            BEFORE TRUNCATE ON agent_experience.experience_grants
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_audited_grant_delete();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_projection_guard'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_projection_guard
            BEFORE UPDATE ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.enforce_record_projection();
    END IF;
END
$body$;

ALTER TABLE agent_experience.lifecycle_events ENABLE ALWAYS TRIGGER lifecycle_events_append_only;
ALTER TABLE agent_experience.lifecycle_events ENABLE ALWAYS TRIGGER lifecycle_events_no_truncate;
ALTER TABLE agent_experience.experience_grant_events ENABLE ALWAYS TRIGGER experience_grant_events_append_only;
ALTER TABLE agent_experience.experience_grant_events ENABLE ALWAYS TRIGGER experience_grant_events_no_truncate;
ALTER TABLE agent_experience.experience_grants ENABLE ALWAYS TRIGGER experience_grants_monotonic;
ALTER TABLE agent_experience.experience_grants ENABLE ALWAYS TRIGGER experience_grants_audited_delete;
ALTER TABLE agent_experience.experience_grants ENABLE ALWAYS TRIGGER experience_grants_no_truncate;
ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_projection_guard;
