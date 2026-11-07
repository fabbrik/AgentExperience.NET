-- AgentExperience.NET: which arm of another memory provider's holdout each captured run was assigned to (Story 25.8).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0028, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. A host that uses another memory provider in Microsoft Agent Framework (Mem0, Zep, Letta, its own) can
-- wrap it in ExperienceHoldoutContextProvider under a key of its choosing. On a deterministic, run-id-derived share of
-- captured runs whose wrapped context is not empty the wrapper withholds that context, and it records both arms here,
-- in the background: one row per run and provider key. A provider's benefit estimate joins these rows to the runs' own
-- records (experience_records.source_run_id = run_id), whose verified outcome is the comparison, exactly as 0027's
-- rows are joined. The Python and Go ports do not write it.
--
-- agent_experience.experience_provider_holdout_assignments: one row per captured run and provider key.
--   * Keyed on (tenant_id, run_id, provider_key), so a run ID another tenant reuses never suppresses this tenant's row. run_id has no foreign key: the run's own record is written at finalization,
--     after its invocations. provider_key is the host's name for the provider, 1-100 characters of [A-Za-z0-9._-].
--   * The scope columns are the caller's own scope, the scope the run was captured in; the policies below require them
--     to lie inside the operation's declared bounds.
--   * arm is 'Treatment' (the provider's context was passed through) or 'Holdout' (it was withheld). A row is never
--     updated: the insert is ON CONFLICT DO NOTHING, so a run invoked again keeps its first row. The instant is clamped
--     to the database's clock_timestamp().
--   * It holds no content, and nothing about the provider's memory items, which are opaque: a run ID, a key, an arm, a
--     scope and an instant. Nothing here is sealed in encrypted mode.
--
-- ERASURE. Every erasure path tombstones the record through 0010's purge_experience_record, and the tombstone
-- transition deletes, through the trigger below, every row of the run the record was captured from (the record's
-- source_run_id before the purge zeroes it, in the record's own tenant only: a run id is a caller-supplied value, so
-- another tenant's record naming the same run must not erase this tenant's rows), and only when that was the run's last
-- live record in the tenant: erasing one of several records of a run that still has a live one keeps its rows (two
-- erased concurrently can each see the other as live; the rows then count as pending). The trigger runs as whoever tombstones
-- the record: the owner, inside the SECURITY DEFINER purge, which row-level security never binds.
--
-- CONCURRENCY. The insert first locks the run's own records (source_run_id is the run, in the caller's tenant, found
-- through 0028's index) FOR KEY SHARE in ID order, and writes nothing when every one of them is by then a tombstone, as
-- 0027's insert does since story 25.10. An erasure of the run's record that overlaps the write therefore serialises with
-- it in either order: one that started first holds the write until it commits, and the write then sees the tombstone;
-- one that starts second waits, and its trigger then deletes the row. Known limit, as 0027's: an erasure committed
-- before the write began cannot be seen (the tombstone keeps no source_run_id), so such a write still records its row
-- (a run ID and an arm, never joined to an outcome, counted as pending).
--
-- PRIVILEGES. ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants the application role SELECT and
-- INSERT on the table, and no UPDATE or DELETE; it grants them only when the table exists, so running it before this
-- migration still succeeds. Until it is re-run after this migration the role cannot reach the table: assignments are
-- reported as not recorded and the agent runs on. The table is .NET-only and is not in the contract's privileges files.
--
-- ROW-LEVEL SECURITY. The policies below admit a row to the application role only while its scope lies inside the
-- operation's declared bounds, for reading and inserting alike. Like 0019's, they switch nothing on: the privileges
-- call enables row-level security on this table with the others.
--
-- LOCKS. CREATE TABLE and the trigger take brief locks on an empty or existing table; CREATE TRIGGER takes a SHARE ROW
-- EXCLUSIVE lock on experience_records for an instant. No table is rewritten.

CREATE TABLE IF NOT EXISTS agent_experience.experience_provider_holdout_assignments (
    run_id           uuid        NOT NULL,
    provider_key     text        NOT NULL,
    arm              text        NOT NULL,
    tenant_id        text        NOT NULL,
    application_id   text        NOT NULL,
    project_id       text        NOT NULL,
    team_id          text        NULL,
    agent_id         text        NULL,
    user_id          text        NULL,
    assigned_at      timestamptz NOT NULL,
    CONSTRAINT experience_provider_holdout_assignments_pkey PRIMARY KEY (tenant_id, run_id, provider_key),
    CONSTRAINT experience_provider_holdout_assignments_arm_known CHECK (arm IN ('Treatment', 'Holdout')),
    CONSTRAINT experience_provider_holdout_assignments_key_shape CHECK (provider_key ~ '^[A-Za-z0-9._-]{1,100}$')
);

-- A provider's rows in one tenant, in run order, for the estimate's read; the primary key serves the erasure's.
CREATE INDEX IF NOT EXISTS experience_provider_holdout_assignments_key_idx
    ON agent_experience.experience_provider_holdout_assignments (tenant_id, provider_key, run_id);

-- The tombstone transition removes its run's rows.
CREATE OR REPLACE FUNCTION agent_experience.clear_erased_record_provider_holdout_assignments() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
BEGIN
    IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL
       AND OLD.source_run_id <> '00000000-0000-0000-0000-000000000000'::uuid
       AND NOT EXISTS (
           SELECT 1 FROM agent_experience.experience_records r
           WHERE r.tenant_id = OLD.tenant_id AND r.source_run_id = OLD.source_run_id
             AND r.deleted_at IS NULL AND r.experience_id <> NEW.experience_id)
    THEN
        DELETE FROM agent_experience.experience_provider_holdout_assignments
        WHERE run_id = OLD.source_run_id AND tenant_id = OLD.tenant_id;
    END IF;

    RETURN NULL;
END;
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_provider_holdout_assignments_cleared'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_provider_holdout_assignments_cleared
            AFTER UPDATE OF deleted_at ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.clear_erased_record_provider_holdout_assignments();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_provider_holdout_assignments_cleared;

DROP POLICY IF EXISTS rls_provider_holdout_assignments_select ON agent_experience.experience_provider_holdout_assignments;
CREATE POLICY rls_provider_holdout_assignments_select ON agent_experience.experience_provider_holdout_assignments
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_provider_holdout_assignments_insert ON agent_experience.experience_provider_holdout_assignments;
CREATE POLICY rls_provider_holdout_assignments_insert ON agent_experience.experience_provider_holdout_assignments
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));
