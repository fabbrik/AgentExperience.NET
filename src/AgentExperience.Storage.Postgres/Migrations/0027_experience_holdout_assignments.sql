-- AgentExperience.NET: which arm of the injection holdout each captured run was assigned to (Story 25.1).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0026, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. The library cannot tell whether its lessons help unless it can compare runs that were given them with
-- runs that would have been and were not. With ExperienceInjectionOptions.Holdout on, the .NET injection adapter
-- withholds the block from a deterministic, run-id-derived share of captured runs and injects it into the rest, and
-- records both arms here, in the background: one row per run and record the block named. Benefit estimation joins
-- these rows to the runs' own records (experience_records.source_run_id = run_id), whose verified outcome is the
-- comparison. The Python and Go ports do not write it yet.
--
-- agent_experience.experience_holdout_assignments: one row per captured run and record its block named.
--   * Keyed on (run_id, experience_id), with a foreign key from experience_id to experience_records ON DELETE CASCADE.
--     The row carries the record's own scope columns, copied from the record by the insert (INSERT ... SELECT FROM
--     experience_records in exactly the caller's scope, live only), and the policies below require a live record with
--     exactly that ID and those scope columns, so a caller cannot create a row for a record in any other scope. A record
--     another scope borrows through a grant is withheld or delivered with the rest of the block but is not recorded.
--   * run_id has no foreign key: the run's own record is written at finalization, after its invocations.
--   * arm is 'Treatment' (the block was injected) or 'Holdout' (it was withheld); revision is the record's revision as
--     the block rendered it. A row is never updated: the insert is ON CONFLICT DO NOTHING, so a run invoked again keeps
--     its first rows. The instant is clamped to the database's clock_timestamp().
--   * It holds no content: IDs, a revision, an arm, a scope and an instant. Nothing here is sealed in encrypted mode.
--
-- ERASURE. Every erasure path tombstones the record through 0010's purge_experience_record (one record per
-- transaction, the sweeps included), and the tombstone transition deletes, through the trigger below, every row that
-- names the record as a lesson and every row of the run the record was captured from (the record's source_run_id
-- before the purge zeroes it, in the record's own tenant only: a run id is a caller-supplied value, so another tenant's
-- record naming the same run must not erase this tenant's rows), so neither the lesson nor the run outlives its erasure here. The trigger runs as whoever
-- tombstones the record: the owner, inside the SECURITY DEFINER purge, which row-level security never binds. A record
-- row is never deleted (0010's experience_records_no_delete refuses it); the foreign key's cascade covers one removed
-- anyway, by an owner who disabled that guard. In encrypted mode the store also skips a record whose key is already
-- destroyed.
--
-- CONCURRENCY. The insert locks the records it names FOR KEY SHARE, in experience_id order, before it writes each
-- row: the established pattern of every adapter write that checks a record (0010's header, 0026's). The purge locks its
-- one record FOR UPDATE before anything else, which conflicts with FOR KEY SHARE, so an insert that started first holds
-- the purge until it commits (and the trigger then deletes its rows), and one that starts second waits and re-reads
-- the record as a tombstone, writing nothing. The insert takes no row lock here (DO NOTHING updates nothing), so no
-- deadlock is possible.
--
-- PRIVILEGES. ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants the application role SELECT and
-- INSERT on the table, and no UPDATE or DELETE; it grants them only when the table exists, so running it before this
-- migration still succeeds. Until it is re-run after this migration the role cannot reach the table: assignments are
-- reported as not recorded and injection goes on. The table is .NET-only and is not in the contract's privileges files,
-- whose REVOKE ALL removes the grants: in a mixed fleet, run the .NET privileges call after those scripts.
--
-- ROW-LEVEL SECURITY. The policies below admit a row to the application role only while its scope lies inside the
-- operation's declared bounds, for reading and inserting alike, and admit an inserted row only for a live record with
-- that ID in exactly that scope; a grant never admits one. Like 0019's, they switch nothing on: the privileges call
-- enables row-level security on this table with the others.
--
-- LOCKS. CREATE TABLE and the trigger take brief locks on an empty or existing table; CREATE TRIGGER and the foreign key
-- take a SHARE ROW EXCLUSIVE lock on experience_records for an instant. No table is rewritten.

CREATE TABLE IF NOT EXISTS agent_experience.experience_holdout_assignments (
    run_id           uuid        NOT NULL,
    experience_id    uuid        NOT NULL,
    revision         bigint      NOT NULL,
    arm              text        NOT NULL,
    tenant_id        text        NOT NULL,
    application_id   text        NOT NULL,
    project_id       text        NOT NULL,
    team_id          text        NULL,
    agent_id         text        NULL,
    user_id          text        NULL,
    assigned_at      timestamptz NOT NULL,
    CONSTRAINT experience_holdout_assignments_pkey PRIMARY KEY (run_id, experience_id),
    CONSTRAINT experience_holdout_assignments_record_fkey FOREIGN KEY (experience_id)
        REFERENCES agent_experience.experience_records (experience_id) ON DELETE CASCADE,
    CONSTRAINT experience_holdout_assignments_arm_known CHECK (arm IN ('Treatment', 'Holdout')),
    CONSTRAINT experience_holdout_assignments_revision_nonnegative CHECK (revision >= 0)
);

-- The lesson's rows, for its erasure and the foreign key's cascade; the primary key serves the run's.
CREATE INDEX IF NOT EXISTS experience_holdout_assignments_experience_idx
    ON agent_experience.experience_holdout_assignments (experience_id);

-- The tombstone transition removes the record's rows as a lesson, and its run's rows.
CREATE OR REPLACE FUNCTION agent_experience.clear_erased_record_holdout_assignments() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
BEGIN
    IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL THEN
        DELETE FROM agent_experience.experience_holdout_assignments
        WHERE experience_id = NEW.experience_id
           OR (run_id = OLD.source_run_id AND OLD.source_run_id <> '00000000-0000-0000-0000-000000000000'::uuid
               AND tenant_id = OLD.tenant_id);
    END IF;

    RETURN NULL;
END;
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_holdout_assignments_cleared'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_holdout_assignments_cleared
            AFTER UPDATE OF deleted_at ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.clear_erased_record_holdout_assignments();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_holdout_assignments_cleared;

DROP POLICY IF EXISTS rls_holdout_assignments_select ON agent_experience.experience_holdout_assignments;
CREATE POLICY rls_holdout_assignments_select ON agent_experience.experience_holdout_assignments
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_holdout_assignments_insert ON agent_experience.experience_holdout_assignments;
CREATE POLICY rls_holdout_assignments_insert ON agent_experience.experience_holdout_assignments
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))
        AND EXISTS (
            SELECT 1 FROM agent_experience.experience_records r
            WHERE r.experience_id = experience_holdout_assignments.experience_id
              AND r.tenant_id = experience_holdout_assignments.tenant_id
              AND r.application_id = experience_holdout_assignments.application_id
              AND r.project_id = experience_holdout_assignments.project_id
              AND r.team_id IS NOT DISTINCT FROM experience_holdout_assignments.team_id
              AND r.agent_id IS NOT DISTINCT FROM experience_holdout_assignments.agent_id
              AND r.user_id IS NOT DISTINCT FROM experience_holdout_assignments.user_id
              AND r.deleted_at IS NULL));
