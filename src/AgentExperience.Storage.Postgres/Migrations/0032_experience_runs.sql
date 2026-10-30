-- AgentExperience.NET: the run store, so a run captured in one process can be finalized in a later one (Story 28.7).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0031, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. PostgresExperienceRunStore (IExperienceRunStore), registered only by
-- AddAgentExperiencePostgresRunStore, keeps each completed run the capture service completes, as capture held it --
-- already sanitized -- so finalization can read it when the verdict arrives after the capturing process has ended (a CI
-- result, a merged pull request). It is a staging area: a run becomes learning only through finalization, which marks it
-- finalized; the host deletes a stored run after finalization or after a retention of its choosing.
--
-- agent_experience.experience_runs: one row per completed run.
--   * Keyed on run_id alone: run IDs are unique across every scope, as record IDs are, and a save under an ID another
--     scope holds is a conflict that reveals nothing (the insert is ON CONFLICT DO NOTHING).
--   * The scope columns are the run's own scope. payload is the run without its ID and scope (ExperienceRunPayload,
--     version 1): its task ID and description, environment, provenance (with the records it was exposed to), attempts,
--     execution status and instants, in the same JSON shapes as a record's payload. completion_event_id is the event
--     that completed it: saving the same run again under it is a replay.
--   * finalized_at is set once, by MarkFinalizedAsync, after the run became (or already was) a durable record;
--     saved_at is the database's clock_timestamp() at the save.
--   * It is plaintext: the run store is not available in crypto-shredding mode (its registration refuses), because a
--     sealed record's run would otherwise be kept unsealed beside it.
--
-- ERASURE. Every erasure path tombstones the record through 0010's purge_experience_record, and the tombstone
-- transition deletes, through the trigger below, the stored run the record was finalized from (the record's
-- source_run_id before the purge zeroes it, in the record's own tenant only), and only when that was the run's last live
-- record in the tenant -- the 0029 pattern. The trigger runs as whoever tombstones the record: the owner, inside the
-- SECURITY DEFINER purge, which row-level security never binds.
--
-- PRIVILEGES. ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants the application role SELECT, INSERT,
-- DELETE and UPDATE of finalized_at on the table, only when the table exists, so running it before this migration still
-- succeeds. Until it is re-run after this migration, a save fails and is reported (the run still completes in memory).
-- The table is .NET-only and is not in the contract's privileges files.
--
-- ROW-LEVEL SECURITY. The policies below admit a row to the application role only while its scope lies inside the
-- operation's declared bounds, for every command. Like 0019's, they switch nothing on: the privileges call enables
-- row-level security on this table with the others.
--
-- LOCKS. CREATE TABLE and its index take brief locks on a new table; CREATE TRIGGER takes a SHARE ROW EXCLUSIVE lock on
-- experience_records for an instant. No table is rewritten.

CREATE TABLE IF NOT EXISTS agent_experience.experience_runs (
    run_id              uuid        NOT NULL,
    tenant_id           text        NOT NULL,
    application_id      text        NOT NULL,
    project_id          text        NOT NULL,
    team_id             text        NULL,
    agent_id            text        NULL,
    user_id             text        NULL,
    completion_event_id uuid        NOT NULL,
    execution_status    text        NOT NULL,
    ended_at            timestamptz NOT NULL,
    saved_at            timestamptz NOT NULL,
    finalized_at        timestamptz NULL,
    payload_version     integer     NOT NULL,
    payload             jsonb       NOT NULL,
    CONSTRAINT experience_runs_pkey PRIMARY KEY (run_id),
    CONSTRAINT experience_runs_status_known CHECK (execution_status IN ('Completed', 'Failed', 'Cancelled')),
    CONSTRAINT experience_runs_payload_version_known CHECK (payload_version = 1)
);

-- A scope's runs still waiting for a verdict, in run order, for the listing.
CREATE INDEX IF NOT EXISTS experience_runs_unfinalized_idx
    ON agent_experience.experience_runs (tenant_id, application_id, project_id, run_id)
    WHERE finalized_at IS NULL;

-- The tombstone transition removes the stored run its record was finalized from.
CREATE OR REPLACE FUNCTION agent_experience.clear_erased_record_runs() RETURNS trigger
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
        DELETE FROM agent_experience.experience_runs
        WHERE run_id = OLD.source_run_id AND tenant_id = OLD.tenant_id;
    END IF;

    RETURN NULL;
END;
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_runs_cleared'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_runs_cleared
            AFTER UPDATE OF deleted_at ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.clear_erased_record_runs();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_runs_cleared;

DROP POLICY IF EXISTS rls_runs_select ON agent_experience.experience_runs;
CREATE POLICY rls_runs_select ON agent_experience.experience_runs
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_runs_insert ON agent_experience.experience_runs;
CREATE POLICY rls_runs_insert ON agent_experience.experience_runs
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_runs_update ON agent_experience.experience_runs;
CREATE POLICY rls_runs_update ON agent_experience.experience_runs
    FOR UPDATE
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_runs_delete ON agent_experience.experience_runs;
CREATE POLICY rls_runs_delete ON agent_experience.experience_runs
    FOR DELETE
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));
