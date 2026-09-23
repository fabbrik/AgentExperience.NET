-- AgentExperience.NET: deletion and retention -- payload erasure with a payload-free tombstone (Story 4.5).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0009, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- 0006's header pointed here: "until the library ships a purge path (roadmap story 4.5 ... which will need
-- a SECURITY DEFINER purge function or time-partitioned logs -- this script cannot be edited once
-- journaled)". This is that script. 0006 is untouched; its trigger functions are replaced in place with
-- CREATE OR REPLACE, so every ENABLE ALWAYS binding it created survives unchanged and no table is ever
-- left unguarded for an instant.
--
-- WHAT DELETION IS HERE. Not a row vanishing: payload erasure plus a tombstone. The experience_records row
-- survives, carrying only the opaque experience_id, the six scope columns, revision, deleted_at, status,
-- and a fixed non-blank task_id placeholder. Everything else that named the record -- its evidence, its
-- exposure rows, its grants and their audit events, its lifecycle history, its embedding -- is REMOVED.
-- The tombstone is what makes the ID unusable afterwards: a create collides with it, and every other write
-- path refuses it.
--
-- WHICH REFUSALS THE SCHEMA ENFORCES, AND WHICH THE ADAPTER DOES. "Every other write path refuses a
-- tombstone" is true of this library's write paths, and it is worth saying exactly where the rule lives,
-- because the two are not the same strength:
--   * SCHEMA-ENFORCED, so raw SQL cannot get round them: recreating the record (the primary key collides
--     with the surviving tombstone row); any UPDATE of a tombstone (enforce_record_projection below
--     refuses it, marker or not); setting, clearing or moving deleted_at outside the purge; a tombstone
--     row whose payload or task_id is not the erased shape (the tombstone-shape CHECK); and deleting or
--     truncating experience_records at all (reject_record_removal below).
--   * ADAPTER-ENFORCED, by a predicate the store puts in its own statements, and therefore only binding
--     for callers who go through this library: a lifecycle event or confidence-evidence row naming a
--     tombstone, an embedding write, a reuse-feedback exposure, and a sharing grant. Raw SQL can still
--     INSERT any of those rows against a tombstoned ID; there is no foreign key to experience_records on
--     any of those tables, deliberately (0002, 0005, 0007, 0008), and adding one now would rewrite four
--     journaled tables' shapes for this one rule.
--   * The adapter-enforced predicates are locked, not merely read: every one of them takes
--     FOR KEY SHARE on the record row, so a writer that starts before a purge commits is parked against
--     the purge's FOR UPDATE and re-checks the tombstone when it is released, instead of deciding
--     against a snapshot the purge has already invalidated.
--
-- WHAT IS RETAINED AFTER A DELETE, EXHAUSTIVELY:
--     experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id,
--     revision, deleted_at, status (the fixed literal 'Deleted'), task_id (the fixed literal '(deleted)'),
--     payload_version.
-- Nothing else. payload_version is on the list rather than described as an exception to it: it describes
-- the (now empty) payload envelope's shape and says nothing about the record, but it does survive, and a
-- list that called itself exhaustive while omitting it would be wrong. source_run_id is zeroed, payload
-- becomes '{}'::jsonb, reuse_confidence and both counters become 0, and created_at and updated_at are set
-- to deleted_at -- a tombstone's only timestamp is the moment it was erased. search_vector is
-- GENERATED ALWAYS from task_id and two payload fields (0003), so erasing the payload and replacing
-- task_id regenerates it to hold only the placeholder -- the erasure of searchable text is automatic and
-- needs no separate index maintenance.
--
-- experience_grant_access rows are DELIBERATELY NOT DELETED. They name a grant and a principal and carry
-- no record payload; they are the answer to "who read this before it was deleted", which is exactly the
-- question a deletion makes urgent. The purge marker below does not admit a delete on that table at all.
--
-- HOW THE APPEND-ONLY GUARDS STAY ARMED. Erasure needs DELETE on five append-only tables. The runbook
-- 0006 documented -- ALTER TABLE ... DISABLE TRIGGER, DELETE, ENABLE ALWAYS TRIGGER -- is replaced rather
-- than automated, because disabling a trigger is table-wide and session-independent: for the length of
-- that window *every other connection in the pool* can rewrite the audit log, and a failure between
-- disable and re-enable leaves the guard off afterwards. Instead the guards themselves learn one
-- transaction-scoped marker:
--
--     SET LOCAL agent_experience.purge_authorized = 'on'
--
-- set only inside agent_experience.purge_experience_record below, and read by
-- agent_experience.purge_authorized(). The marker is invisible to every other session, it dies with the
-- transaction, and -- because the function declares a SET clause for the same variable -- it dies at
-- function exit even if the caller's transaction runs on. The guards keep refusing UPDATE and TRUNCATE
-- unconditionally, on every table, in every session, including the purging one.
--
-- WHO MAY CALL THE PURGE FUNCTIONS. Both are SECURITY DEFINER, so they run with the owner's rights, and
-- PostgreSQL grants EXECUTE on a new function to PUBLIC by default. Left at the default that would make
-- them a universally callable erasure primitive: any role that can connect -- including a SELECT-only
-- reporting role explicitly denied DELETE and UPDATE on every table -- could read an experience_id and a
-- scope out of experience_records and erase that record, in any tenant. That is a privilege escalation in
-- the opposite direction from the one this script is otherwise careful about, so EXECUTE is revoked from
-- PUBLIC and granted explicitly, at the bottom of this script, to the role applying it -- which is the
-- role that owns these tables and runs the application. A deployment whose application role is not the
-- migrating role must grant it EXECUTE itself, once, and should grant it to nothing else:
--
--     GRANT EXECUTE ON FUNCTION agent_experience.purge_experience_record(
--         uuid, text, text, text, text, text, text, bigint, timestamptz) TO <application_role>;
--     GRANT EXECUTE ON FUNCTION agent_experience.purge_expired_grants(
--         text, text, text, text, text, text, timestamptz, integer) TO <application_role>;
--
-- THIS IS AN AUDITABILITY MECHANISM, NOT A PRIVILEGE BOUNDARY. Be precise, because a reader who assumed
-- otherwise would trust it for something it does not do:
--   * A custom GUC is settable by any session. Nothing stops a connection that already has DELETE on
--     these tables from issuing the same SET LOCAL itself and then deleting from them directly. The
--     marker decides whether a *permitted* delete is refused; it is not what decides permission.
--   * The guards still do not bind a role that can ALTER TABLE -- which is the application role, because
--     it created the tables (0006:46-62). An owner can disable or drop a trigger and write what it likes.
--   * Conversely, the EXECUTE grant above is a real privilege boundary, and the only one here: a role
--     without it cannot reach the purge at all, whatever it does with the GUC.
-- What this buys is narrower and real: erasure has exactly ONE code path, inside ONE transaction, with the
-- guard never switched off, never left off across a failure, and never widened for any other session. It
-- is a guard against a bug, a careless script, or a compromised application path -- not against an
-- administrator who has decided to tamper. A deployment that needs more must own these tables with a role
-- the application does not have.
--
-- WHAT DELETION DOES NOT REACH. Backups, replicas, WAL and logical-replication streams, exported
-- telemetry, and any external artifact a record merely named are host-owned and out of reach of this
-- schema.
--
-- AND ONE THING INSIDE THIS DATABASE: THE DEAD TUPLE. Step 9 is an UPDATE, and an UPDATE in PostgreSQL
-- writes a new row version and leaves the old one in the heap. Until VACUUM reclaims it, the previous
-- version of the record row is still on disk and STILL CARRIES THE ERASED TEXT -- the task summary, the
-- lesson, the attempt results, the task id -- readable by anyone who can inspect the page (pageinspect,
-- a file-level copy, a base backup taken in that window). The same is true of every DELETE above. State
-- this plainly rather than reassuringly: the *index* entries that go dead alongside them point at rows
-- that no longer carry the erased text and so cannot return it, but the *heap* tuple they point at is
-- the erased text, for as long as it survives. An erasure obligation with a deadline has to reach it:
--
--     VACUUM (VERBOSE) agent_experience.experience_records;   -- and the tables swept above
--
-- Ordinary VACUUM reclaims a dead tuple once no snapshot can still see it; autovacuum will get there on
-- its own schedule, which is not a schedule anybody promised. VACUUM does not overwrite the freed bytes,
-- so a deployment that must also defeat forensic recovery of freed pages needs VACUUM FULL (which
-- rewrites the table under an ACCESS EXCLUSIVE lock) or a storage-level guarantee, neither of which this
-- script can give it.
--
-- THE THREE INDEXES ARE NOT FREE ON A LARGE, HAND-APPLIED DATABASE, AND ONE OF THEM IS OVER THE BIGGEST
-- TABLE HERE. ix_experience_records_live_by_age, ix_confidence_evidence_experience and
-- ix_reuse_feedback_exposures_experience are built with plain CREATE INDEX inside the migrator's
-- per-script transaction, which takes a SHARE lock and therefore blocks every write to those tables for
-- the duration of the build. On a fresh database that is imperceptible; on an established one with a long
-- record history it is a write outage, and it is a larger one than 0007's, 0008's or 0009's, because
-- experience_records is the table this library writes most. A deployment that cannot take one should
-- create all three out of band *before* running this script -- CREATE INDEX ... CONCURRENTLY cannot run
-- inside a transaction block at all, and IF NOT EXISTS then makes this script's own statements no-ops:
--
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_records_live_by_age
--         ON agent_experience.experience_records
--         (tenant_id, application_id, project_id, created_at, experience_id)
--         WHERE deleted_at IS NULL;
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_confidence_evidence_experience
--         ON agent_experience.confidence_evidence (experience_id);
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_reuse_feedback_exposures_experience
--         ON agent_experience.reuse_feedback_exposures (experience_id);
--
-- The first of those needs the deleted_at column, so add it first and separately -- ADD COLUMN ... NULL
-- rewrites nothing and takes only a brief ACCESS EXCLUSIVE lock:
--
--     ALTER TABLE agent_experience.experience_records ADD COLUMN IF NOT EXISTS deleted_at timestamptz NULL;
--
-- CONCURRENTLY can leave an INVALID index behind if it fails; check with
-- "SELECT indisvalid FROM pg_index WHERE indexrelid = 'agent_experience.ix_experience_records_live_by_age'::regclass",
-- and DROP INDEX CONCURRENTLY and retry if it comes back false. Do this before migrating, not after.
--
-- UPGRADING AN EXISTING DATABASE. The new column is nullable, so no row is rewritten and no default is
-- backfilled. The tombstone-shape CHECK is ADD CONSTRAINT ... NOT VALID exactly as 0006's and 0007's are:
-- every existing row has deleted_at IS NULL and therefore satisfies it, so validation would in fact
-- succeed -- but a scan of a large record table at startup is a cost no deployment asked for. After
-- upgrading, confirm and then validate at a time of your choosing:
--
--     SELECT experience_id FROM agent_experience.experience_records
--     WHERE (deleted_at IS NULL) <> (status <> 'Deleted')
--        OR (deleted_at IS NOT NULL AND (payload <> '{}'::jsonb OR task_id <> '(deleted)'));
--
-- Once it returns nothing:
--
--     ALTER TABLE agent_experience.experience_records VALIDATE CONSTRAINT experience_records_tombstone_shape;
--
-- VALIDATE takes only a SHARE UPDATE EXCLUSIVE lock, so it does not block reads or writes.

ALTER TABLE agent_experience.experience_records
    ADD COLUMN IF NOT EXISTS deleted_at timestamptz NULL;

DO $body$
BEGIN
    -- A tombstone is one shape or it is not a tombstone. Without this, a writer could set deleted_at on a
    -- row that still holds its payload -- a record that reads as erased everywhere while the text it was
    -- deleted for is still sitting in the table.
    --
    -- WHAT THIS CHECK DOES NOT PIN, SAID HERE SO NOBODY READS MORE INTO IT. It constrains an existing
    -- row's shape, not its history. A row can be INSERTed as a tombstone directly, with any created_at,
    -- updated_at or deleted_at the writer likes -- there is no UPDATE for enforce_record_projection to
    -- refuse, and a tombstone's timestamps are checked against each other only in that trigger, on the
    -- one transition that creates one. So "a tombstone's only timestamp is the moment it was erased" is
    -- a property of agent_experience.purge_experience_record, and of every tombstone this library made;
    -- it is not a property the schema can prove about a row somebody else inserted. The same goes for
    -- the revision on such a row: only the purge's UPDATE is made to advance it by exactly one.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_records_tombstone_shape'
          AND conrelid = 'agent_experience.experience_records'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_records
            ADD CONSTRAINT experience_records_tombstone_shape
            CHECK (
                (deleted_at IS NULL AND status <> 'Deleted')
                OR (deleted_at IS NOT NULL
                    AND status = 'Deleted'
                    AND task_id = '(deleted)'
                    AND payload = '{}'::jsonb
                    AND source_run_id = '00000000-0000-0000-0000-000000000000'::uuid
                    AND reuse_confidence = 0
                    AND supporting_validations = 0
                    AND contradictions = 0))
            NOT VALID;
    END IF;
END
$body$;

-- The retention sweep's whole predicate: a scope's live records, oldest first. Partial on
-- deleted_at IS NULL, so the index holds only rows a sweep could still act on and tombstones cost nothing
-- to keep. It also serves every read's "and not a tombstone" filter for a scope that has accumulated them.
CREATE INDEX IF NOT EXISTS ix_experience_records_live_by_age
    ON agent_experience.experience_records (tenant_id, application_id, project_id, created_at, experience_id)
    WHERE deleted_at IS NULL;

-- 0007 said this index "belongs with the query that justifies it, not ahead of it", and named this story.
-- Here is the query: the erasure sweeps the evidence ledger by the record it is about, and that record has
-- no other handle on this table -- confidence_evidence carries no scope columns and no foreign key.
CREATE INDEX IF NOT EXISTS ix_confidence_evidence_experience
    ON agent_experience.confidence_evidence (experience_id);

-- The same, for the exposure ledger: its primary key is (feedback_id, experience_id), so deleting by the
-- record alone would scan it. 0008 deferred this index to this story for the same reason 0007 did.
CREATE INDEX IF NOT EXISTS ix_reuse_feedback_exposures_experience
    ON agent_experience.reuse_feedback_exposures (experience_id);

-- The marker, read in one place so no guard retypes the parameter name or the default. A custom GUC that
-- was never set reads back NULL under the missing_ok form, which is exactly "not authorized".
CREATE OR REPLACE FUNCTION agent_experience.purge_authorized() RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $body$
    SELECT coalesce(pg_catalog.current_setting('agent_experience.purge_authorized', true), 'off') = 'on';
$body$;

-- 0006's append-only guard, extended with exactly one exception and no others. UPDATE and TRUNCATE are
-- still refused unconditionally, in every session including the purging one: erasure removes rows, it
-- never rewrites them, and a TRUNCATE is never scoped to one record. DELETE is admitted only while the
-- marker is set and only on the five tables an erasure sweeps -- experience_grant_access is deliberately
-- absent, because who read a record before it was deleted outlives the record.
--
-- The refusal message and SQLSTATE are unchanged, so an operator who hits the guard is told exactly what
-- 0006 told them.
CREATE OR REPLACE FUNCTION agent_experience.reject_event_log_mutation() RETURNS trigger AS $body$
BEGIN
    IF TG_OP = 'DELETE'
        AND agent_experience.purge_authorized()
        AND TG_TABLE_NAME IN (
            'lifecycle_events',
            'experience_grant_events',
            'confidence_evidence',
            'reuse_feedback',
            'reuse_feedback_exposures')
    THEN
        RETURN OLD;
    END IF;

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

-- THE RECORD ROW ITSELF CANNOT BE REMOVED, BY ANYBODY, MARKER OR NOT. Until this script there was no
-- guard here at all: a bare
--
--     DELETE FROM agent_experience.experience_records WHERE experience_id = ...;
--
-- succeeded from any session with DELETE on the table, and it is the one statement that undoes everything
-- the erasure above is for. It orphans the whole audit trail -- lifecycle_events, confidence_evidence and
-- the exposure ledger have no foreign key to experience_records, deliberately (0002, 0007, 0008), so
-- their rows simply outlive the record and name an ID that no longer resolves -- and, worse, it FREES THE
-- ID: experience_grants has no foreign key either (0005), so re-inserting a record under the same
-- experience_id makes every grant that was issued over the old content apply to the new content. That is
-- precisely what step 6 of the erasure exists to prevent, and it was reachable by exactly the actor this
-- script's honesty statement names as in scope -- a bug, a careless script, or a compromised application
-- path.
--
-- There is no exception and no marker clause, because the erasure never deletes this row: it UPDATEs it
-- into a tombstone (step 9), and the tombstone is the point. So "no supported path removes a record" is
-- now enforced by the schema rather than promised by the documentation. The escape hatch is the same one
-- every other guard here has and no smaller: the table's owner can ALTER TABLE ... DISABLE TRIGGER, which
-- is a deliberate, visible act by a role that could drop the table anyway.
--
-- TRUNCATE is refused for the same reason and with the same finality.
CREATE OR REPLACE FUNCTION agent_experience.reject_record_removal() RETURNS trigger AS $body$
BEGIN
    RAISE EXCEPTION
        'An Experience Record row is never removed: erasure leaves a payload-free tombstone under the same '
        'experience_id, through agent_experience.purge_experience_record, so the ID can never be reused.'
        USING ERRCODE = 'insufficient_privilege';
END;
$body$ LANGUAGE plpgsql;

-- CREATE TRIGGER has no IF NOT EXISTS, and dropping one to recreate it would leave a window in which the
-- table is unguarded, so each is created only when it is absent -- exactly as 0006 does. ENABLE ALWAYS is
-- applied unconditionally afterwards: a no-op on a trigger that already has it, and what makes the guard
-- survive session_replication_role = 'replica'.
DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_no_delete'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_no_delete
            BEFORE DELETE ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_record_removal();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_no_truncate'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_no_truncate
            BEFORE TRUNCATE ON agent_experience.experience_records
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_record_removal();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_no_delete;
ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_no_truncate;

-- 0006's grant-delete guard, with the same one exception. The audit-trail rule is unchanged for every
-- ordinary writer: a grant that has events cannot be deleted, because the row would go and the trail would
-- stay. Inside a purge the trail has already gone -- the erasure order deletes experience_grant_events
-- first, in the same transaction -- so the EXISTS below would pass anyway; the marker is tested explicitly
-- so that the rule reads as one deliberate exception rather than as a coincidence of ordering. TRUNCATE
-- stays refused unconditionally.
CREATE OR REPLACE FUNCTION agent_experience.reject_audited_grant_delete() RETURNS trigger AS $body$
BEGIN
    IF TG_OP = 'TRUNCATE' THEN
        RAISE EXCEPTION
            'agent_experience.experience_grants cannot be truncated: its audit trail would outlive it.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF agent_experience.purge_authorized() THEN
        RETURN OLD;
    END IF;

    IF EXISTS (SELECT 1 FROM agent_experience.experience_grant_events e WHERE e.grant_id = OLD.grant_id) THEN
        RAISE EXCEPTION
            'A grant with an audit trail cannot be deleted: revoke it instead, so the trail and the row agree.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    RETURN OLD;
END;
$body$ LANGUAGE plpgsql;

-- The projection guard, extended twice: once to make a tombstone terminal, and once to admit the one
-- UPDATE that creates it.
--
-- WHY THE GUARD HAD TO CHANGE AT ALL. 0007's rule says reuse_confidence and its counters may only move to
-- values a lifecycle event recorded for the new revision. A tombstone zeroes all three, and the erasure
-- has just removed every lifecycle event the record had, so there is no event to point at and the guard
-- would reject the very statement the purge exists to perform. Rather than leave the numbers behind --
-- they are a summary of how often this record's lesson held up, which is exactly what a deletion is asked
-- to remove -- the guard recognises the tombstone shape under the same marker the append-only guards read.
--
-- THE EXCEPTION IS SHAPE-CHECKED, NOT MERELY MARKER-CHECKED, AND THE SHAPE INCLUDES THE SCOPE. A marked
-- transaction may make this one transition and no other: a live row, to a row whose deleted_at is set,
-- whose payload is empty, whose task_id is the placeholder, whose status is the tombstone literal, whose
-- created_at and updated_at are the deletion instant, whose payload_version and six scope columns are
-- unchanged, one revision forward. Checking only the payload columns would have left a marked UPDATE free
-- to move the row's scope while erasing it -- tombstoning a record INTO ANOTHER TENANT'S SCOPE, so that
-- the scope that owned it sees NotFound for its own erased record and a scope that never held it sees
-- Deleted. The scope equality below is what makes the tombstone answerable to, and only to, the scope
-- that owned the record.
--
-- A marked UPDATE that sets deleted_at and does NOT match the shape is refused outright rather than
-- falling through to the ordinary rules: the ordinary rules are about live projections, and on a row
-- whose counters already read 0/0/0 they would have let a malformed tombstone through.
--
-- A TOMBSTONE IS TERMINAL. No UPDATE of a row whose deleted_at is already set is admitted, marker or not.
-- That is what makes "deleting twice touches nothing" and "a late lifecycle commit cannot move a
-- tombstone" true of the schema rather than only of the adapter. And outside a purge, deleted_at cannot be
-- set, cleared, or changed at all: erasure has one code path.
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

    IF OLD.deleted_at IS NOT NULL THEN
        RAISE EXCEPTION
            'An erased Experience Record is a tombstone: it carries no payload and cannot be changed again.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF NEW.deleted_at IS NOT NULL THEN
        IF NOT agent_experience.purge_authorized() THEN
            RAISE EXCEPTION
                'An Experience Record is erased only through agent_experience.purge_experience_record.'
                USING ERRCODE = 'insufficient_privilege';
        END IF;

        -- The authorized exception: exactly the tombstone, and only while the marker is set. Anything
        -- else that sets deleted_at stops here rather than continuing into the live-projection rules.
        IF NEW.revision = OLD.revision + 1
            AND NEW.status = 'Deleted'
            AND NEW.task_id = '(deleted)'
            AND NEW.payload = '{}'::jsonb
            AND NEW.payload_version = OLD.payload_version
            AND NEW.created_at = NEW.deleted_at
            AND NEW.updated_at = NEW.deleted_at
            AND NEW.tenant_id = OLD.tenant_id
            AND NEW.application_id = OLD.application_id
            AND NEW.project_id = OLD.project_id
            AND NEW.team_id IS NOT DISTINCT FROM OLD.team_id
            AND NEW.agent_id IS NOT DISTINCT FROM OLD.agent_id
            AND NEW.user_id IS NOT DISTINCT FROM OLD.user_id
        THEN
            RETURN NEW;
        END IF;

        RAISE EXCEPTION
            'A purge may make exactly one transition and no other: a live Experience Record into its own '
            'tombstone, in its own scope, one revision forward.'
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

-- THE ONE ERASURE PATH. One transaction, one order, every step inside this function.
--
-- The order is not incidental and must not be rearranged:
--   1. experience_records   SELECT ... FOR UPDATE with the scope and revision guards. Establishes
--                           authorization and pins the row. A row that does not match is disambiguated by
--                           a second, scope-only read inside this same transaction, exactly as the store's
--                           lifecycle commit disambiguates its own "no row updated".
--   2. confidence_evidence  MUST precede step 9. It has no scope columns and no foreign key (0007), so the
--                           record row's scope is the only thing that makes it reachable by scope at all.
--   3. reuse_feedback_exposures  The exposures naming this record. Children before parents: the foreign key
--                           to reuse_feedback is NO ACTION. The submissions those exposures belong to are
--                           locked FOR UPDATE *first*, see below.
--   4. reuse_feedback       Only the submissions step 3 emptied. A submission that also named other records
--                           keeps its row -- it still describes them -- and only its exposure of this
--                           record is gone.
--   5. experience_grant_events   Before the grants themselves: reject_audited_grant_delete refuses to delete
--                           a grant that still has events.
--   6. experience_grants    Legal only once step 5 emptied the trail. Grants are purged with the record
--                           because experience_grants has no foreign key to it (0005) and a re-appearing ID
--                           would otherwise re-apply them.
--   7. lifecycle_events     The record's own history.
--   8. experience_embeddings  Guarded by to_regclass and a column check, and run through EXECUTE, because
--                           the table belongs to the vectors package (0004) and this package must not
--                           depend on it. A base-only deployment simply skips the step. (0004's foreign key
--                           is ON DELETE CASCADE, but nothing here deletes the record row -- and nothing
--                           can, see reject_record_removal -- so the row must be removed explicitly.)
--
-- NOTE WHAT STEPS 2-8 DO NOT CARRY: A SCOPE PREDICATE. Every one of them matches on the record's ID alone.
-- For steps 2-4 that is forced -- confidence_evidence and the feedback ledger have no scope columns at all
-- (0007, 0008) -- and for steps 5-8 it is a choice, stated here rather than left to be discovered, because
-- disclosing it for some of the steps and not the others would read as though the others were scoped:
--   * experience_grants and experience_grant_events DO have the six owner-scope columns, and every grant
--     this library issues copies them from the record row (IssueGrantSql), so in practice the scope
--     predicate would match the same rows. A grant written outside this library with a different owner
--     scope over this record's ID is deleted anyway, and deliberately: "every row that named this record"
--     is what erasure means here, and a grant over an ID whose content is gone is exactly the row nothing
--     else would collect. The same reasoning covers a feedback exposure another scope recorded against
--     this ID, which 0008 deliberately allows.
--   * experience_embeddings likewise carries copied scope columns and is matched by ID for the same reason.
-- Authorization is decided ONCE, at step 1, over the record itself: a caller who cannot pass the scope and
-- revision guards there never reaches step 2. What follows is not a second authorization check and must
-- not be read as one.
--   9. experience_records   The tombstone, last, so every scope-dependent sweep above still had its scope.
--
-- SECURITY DEFINER is what makes the marker meaningful as a single code path rather than as a privilege:
-- see the honesty statement in this script's header. search_path is pinned so nothing here resolves
-- through a caller's.
CREATE OR REPLACE FUNCTION agent_experience.purge_experience_record(
    p_experience_id uuid,
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_expected_revision bigint,
    p_deleted_at timestamptz)
RETURNS TABLE (purge_outcome text, purge_revision bigint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, agent_experience
SET agent_experience.purge_authorized = 'off'
AS $body$
DECLARE
    v_revision bigint;
    v_deleted_at timestamptz;
    v_feedback_ids uuid[];
BEGIN
    -- Transaction-scoped, and narrower still: the function declares a SET for the same variable, so the
    -- marker is restored at function exit even when the caller's transaction continues afterwards.
    SET LOCAL agent_experience.purge_authorized = 'on';

    -- Step 1. The scope predicate, the revision guard, and the existence check in one statement, so a
    -- foreign scope, a stale revision, and a missing record are all "no row" and none of them can be told
    -- apart from the outcome, from a timing branch, or from the error text.
    SELECT r.revision, r.deleted_at INTO v_revision, v_deleted_at
    FROM agent_experience.experience_records r
    WHERE r.experience_id = p_experience_id
      AND r.tenant_id = p_tenant_id
      AND r.application_id = p_application_id
      AND r.project_id = p_project_id
      AND r.team_id IS NOT DISTINCT FROM p_team_id
      AND r.agent_id IS NOT DISTINCT FROM p_agent_id
      AND r.user_id IS NOT DISTINCT FROM p_user_id
      AND r.deleted_at IS NULL
      AND (p_expected_revision IS NULL OR r.revision = p_expected_revision)
    FOR UPDATE;

    IF NOT FOUND THEN
        -- Re-read inside the same transaction, with the same scope predicate and without the revision and
        -- tombstone guards, so "not in this scope" stays indistinguishable from "does not exist" while a
        -- stale revision and an already-erased record can still be reported to the scope that owns them.
        SELECT r.revision, r.deleted_at INTO v_revision, v_deleted_at
        FROM agent_experience.experience_records r
        WHERE r.experience_id = p_experience_id
          AND r.tenant_id = p_tenant_id
          AND r.application_id = p_application_id
          AND r.project_id = p_project_id
          AND r.team_id IS NOT DISTINCT FROM p_team_id
          AND r.agent_id IS NOT DISTINCT FROM p_agent_id
          AND r.user_id IS NOT DISTINCT FROM p_user_id
        FOR UPDATE;

        IF NOT FOUND THEN
            RETURN QUERY SELECT 'NotFound'::text, 0::bigint;
            RETURN;
        END IF;

        IF v_deleted_at IS NOT NULL THEN
            -- Already a tombstone. Deleting again is a success that touches nothing.
            RETURN QUERY SELECT 'AlreadyDeleted'::text, v_revision;
            RETURN;
        END IF;

        RETURN QUERY SELECT 'StaleRevision'::text, v_revision;
        RETURN;
    END IF;

    -- Step 2.
    DELETE FROM agent_experience.confidence_evidence WHERE experience_id = p_experience_id;

    -- Step 3, in three parts, and the order of them is the whole defence against two purges sharing one
    -- submission.
    --
    -- THE RACE THIS AVOIDS. A submission may name several records; two purges erasing two of them run
    -- concurrently. If each simply deleted its own exposure and then asked "does this submission have any
    -- exposures left?", each would still see the other's not-yet-committed exposure row -- READ COMMITTED
    -- hides an uncommitted delete -- so each would decide the submission is still describing something and
    -- leave it. Both commit; the submission survives with ZERO exposures, describing nothing, and nothing
    -- else ever collects it. It carries a run ID, a scope, an outcome, a measure and -- for a human
    -- assessment -- a reviewer identity and a free-text rationale about records that no longer exist.
    --
    -- So: find the submissions this record's exposures belong to, take a row lock on each of them in a
    -- deterministic order, and only then delete. The second purge blocks on that lock until the first has
    -- committed, and its "any exposures left?" then runs against a snapshot that can see the first's
    -- delete. Locking the parents (rather than re-checking afterwards) also means the two purges cannot
    -- interleave into a deadlock: the order is by feedback_id for both.
    SELECT array_agg(DISTINCT x.feedback_id) INTO v_feedback_ids
    FROM agent_experience.reuse_feedback_exposures x
    WHERE x.experience_id = p_experience_id;

    IF v_feedback_ids IS NOT NULL THEN
        PERFORM 1 FROM agent_experience.reuse_feedback f
        WHERE f.feedback_id = ANY(v_feedback_ids)
        ORDER BY f.feedback_id
        FOR UPDATE;
    END IF;

    DELETE FROM agent_experience.reuse_feedback_exposures x WHERE x.experience_id = p_experience_id;

    -- Step 4. Only the submissions step 3 emptied -- "every submission with no exposures" would be a
    -- different, and much larger, statement.
    IF v_feedback_ids IS NOT NULL THEN
        DELETE FROM agent_experience.reuse_feedback f
        WHERE f.feedback_id = ANY(v_feedback_ids)
          AND NOT EXISTS (
              SELECT 1 FROM agent_experience.reuse_feedback_exposures x WHERE x.feedback_id = f.feedback_id);
    END IF;

    -- Step 5.
    DELETE FROM agent_experience.experience_grant_events
    WHERE grant_id IN (
        SELECT g.grant_id FROM agent_experience.experience_grants g WHERE g.experience_id = p_experience_id);

    -- Step 6.
    DELETE FROM agent_experience.experience_grants WHERE experience_id = p_experience_id;

    -- Step 7.
    DELETE FROM agent_experience.lifecycle_events WHERE experience_id = p_experience_id;

    -- Step 8. to_regclass answers "is there a relation by that name", which is not the same question as
    -- "is it the vectors package's embedding table". A deployment that has something else under that name
    -- -- an old shape, a view, another project's table -- would otherwise fail the EXECUTE with a bare
    -- undefined_column and abort the whole erasure, with the record still carrying its payload and no
    -- indication of why. So the shape is checked too, and a mismatch is reported as itself.
    IF to_regclass('agent_experience.experience_embeddings') IS NOT NULL THEN
        IF NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = to_regclass('agent_experience.experience_embeddings')
              AND a.attname = 'experience_id'
              AND a.atttypid = 'pg_catalog.uuid'::pg_catalog.regtype
              AND a.attnum > 0
              AND NOT a.attisdropped)
        THEN
            RAISE EXCEPTION
                'The relation % has no uuid experience_id column, so this record''s stored vector cannot '
                'be removed. Nothing has been erased; reconcile it with 0004 and retry.',
                to_regclass('agent_experience.experience_embeddings')
                USING ERRCODE = 'undefined_column';
        END IF;

        EXECUTE 'DELETE FROM agent_experience.experience_embeddings WHERE experience_id = $1'
            USING p_experience_id;
    END IF;

    -- Step 9. Everything not on the retained list is set to a fixed, content-free value rather than left
    -- as it stands: a tombstone must not say when the work happened, which run produced it, or how often
    -- its lesson held up.
    UPDATE agent_experience.experience_records r
    SET payload = '{}'::jsonb,
        task_id = '(deleted)',
        status = 'Deleted',
        source_run_id = '00000000-0000-0000-0000-000000000000'::uuid,
        reuse_confidence = 0,
        supporting_validations = 0,
        contradictions = 0,
        created_at = p_deleted_at,
        updated_at = p_deleted_at,
        deleted_at = p_deleted_at,
        revision = r.revision + 1
    WHERE r.experience_id = p_experience_id;

    RETURN QUERY SELECT 'Deleted'::text, v_revision + 1;
END
$body$;

-- EXPIRED GRANTS. A grant that has expired permits nothing and its row and trail are the only place its
-- recipient scope, its reason, and the administrator who issued it are still written down. This purges
-- them in bounded batches, oldest expiry first, through the same marker and the same erasure ordering
-- rule: the events before the grant they belong to.
--
-- It also reaches a grant naming a record that is already a tombstone. The record purge removes such
-- grants in its own transaction, and every write path in this library refuses to issue one over a
-- tombstone, so one should not exist -- but "should not" is adapter-enforced, not schema-enforced (there
-- is no foreign key from experience_grants to experience_records, by design in 0005), and a grant naming
-- an erased record is exactly the row nothing else would ever collect.
--
-- THE BATCH BOUND IS APPLIED HERE, NOT ONLY BY THE CALLER. LIMIT NULL means "no limit" in PostgreSQL, so
-- a hand-caller passing p_limit => NULL would have got an unbounded destructive sweep from a function
-- whose whole contract is that it is bounded. p_limit is clamped below to the same 1..500 the adapter's
-- validator enforces, so the bound is a property of the function rather than of the one caller that
-- happens to go through C#.
--
-- WHICH CLOCK DECIDES. p_now is the host's, and everywhere a grant is *read* this schema deliberately
-- uses clock_timestamp() instead, "so a caller whose clock is wrong cannot widen anything". Issuing with
-- a wrong clock only narrows a window; deleting with one destroys rows the database still considers live,
-- which a host skewed a day forward would do silently. The cutoff below is therefore
-- LEAST(p_now, clock_timestamp()): the host can make a purge collect less than the database would, never
-- more.
--
-- A REVOKED-BUT-UNEXPIRED GRANT IS LEFT ALONE. Its revocation is a fact about a window that has not closed
-- yet, and 0006 makes that revocation permanent on purpose; it is collected once it expires like any other.
CREATE OR REPLACE FUNCTION agent_experience.purge_expired_grants(
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_now timestamptz,
    p_limit integer)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, agent_experience
SET agent_experience.purge_authorized = 'off'
AS $body$
DECLARE
    v_grant_ids uuid[];
    v_purged bigint;
    -- 1 and 500 are the same bounds ExperienceRecordValidator enforces on the adapter's batchSize. A NULL
    -- becomes the maximum rather than an error, because a bound is what this function owes its caller and
    -- refusing would tell a hand-caller nothing it could not work out from the signature.
    v_limit integer := least(greatest(coalesce(p_limit, 500), 1), 500);
    v_cutoff timestamptz := least(p_now, pg_catalog.clock_timestamp());
BEGIN
    SET LOCAL agent_experience.purge_authorized = 'on';

    SELECT array_agg(expired.grant_id) INTO v_grant_ids
    FROM (
        SELECT g.grant_id
        FROM agent_experience.experience_grants g
        WHERE g.tenant_id = p_tenant_id
          AND g.application_id = p_application_id
          AND g.project_id = p_project_id
          AND g.team_id IS NOT DISTINCT FROM p_team_id
          AND g.agent_id IS NOT DISTINCT FROM p_agent_id
          AND g.user_id IS NOT DISTINCT FROM p_user_id
          AND (g.expires_at <= v_cutoff
               OR EXISTS (
                   SELECT 1 FROM agent_experience.experience_records r
                   WHERE r.experience_id = g.experience_id AND r.deleted_at IS NOT NULL))
        ORDER BY g.expires_at, g.grant_id
        LIMIT v_limit
        FOR UPDATE) expired;

    IF v_grant_ids IS NULL THEN
        RETURN 0::bigint;
    END IF;

    DELETE FROM agent_experience.experience_grant_events WHERE grant_id = ANY(v_grant_ids);

    WITH removed AS (
        DELETE FROM agent_experience.experience_grants WHERE grant_id = ANY(v_grant_ids) RETURNING 1)
    SELECT count(*) INTO v_purged FROM removed;

    RETURN v_purged;
END
$body$;

-- WHO MAY ERASE. Both functions above are SECURITY DEFINER, and PostgreSQL grants EXECUTE on a function to
-- PUBLIC by default -- which would make them callable by every role that can connect, including one with
-- no DELETE or UPDATE privilege anywhere in this schema, over any tenant whose experience_id and scope it
-- can SELECT. That is the one genuine privilege escalation this script could introduce, so it is revoked
-- here and granted back only to the role applying the migration, which owns these tables and is the role
-- the application runs as. CREATE OR REPLACE FUNCTION preserves a function's ACL, so re-running this
-- script neither loses the revoke nor re-opens the grant; both statements are idempotent.
--
-- A deployment whose application role is NOT the migrating role must grant EXECUTE to it explicitly -- see
-- this script's header for the two statements -- and should grant it to nothing else. This is the only
-- privilege boundary in this script; the marker is not one, and never claimed to be.
REVOKE ALL ON FUNCTION agent_experience.purge_experience_record(
    uuid, text, text, text, text, text, text, bigint, timestamptz) FROM PUBLIC;

REVOKE ALL ON FUNCTION agent_experience.purge_expired_grants(
    text, text, text, text, text, text, timestamptz, integer) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION agent_experience.purge_experience_record(
    uuid, text, text, text, text, text, text, bigint, timestamptz) TO CURRENT_USER;

GRANT EXECUTE ON FUNCTION agent_experience.purge_expired_grants(
    text, text, text, text, text, text, timestamptz, integer) TO CURRENT_USER;

-- agent_experience.purge_authorized() is deliberately left callable by PUBLIC: it is a STABLE reader of a
-- custom GUC, it reveals only whether the *calling* session set the marker, and the guards above call it
-- from within every session's own triggers.
