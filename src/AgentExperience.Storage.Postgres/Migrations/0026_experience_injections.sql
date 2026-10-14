-- AgentExperience.NET: when each record was last injected (Story 21.8).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0025, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. The lessons view shows when each lesson was last put in front of a model. No stored fact answered
-- that: a captured run's record names what it was exposed to, but only for runs that were captured and finalized,
-- and inside a payload that is sealed in encrypted mode; reuse feedback names exposures only when a host submits it.
-- So the injection adapter now records it, once per injected block: one row per record, upserted with the latest
-- instant and a count, in one statement keyed on the record.
--
-- agent_experience.experience_injections: one row per record that has been injected in its own scope.
--   * Keyed on experience_id alone. The row carries the record's own scope columns, copied from the record by the
--     upsert (INSERT ... SELECT FROM experience_records in exactly the caller's scope), so a row exists only for a
--     live record in the scope that injected it, and a caller cannot create one for a record in any other scope.
--     A record another scope borrows through a grant and injects there is not counted: its owner sees that delivery
--     in experience_grant_access.
--   * last_injected_at only moves forward (GREATEST); injection_count rises by one per injected block.
--   * It holds no content: an ID, a scope, an instant and a count. Nothing here is sealed in encrypted mode.
--
-- ERASURE. The tombstone transition of a record (0010's purge_experience_record, which every erasure path runs)
-- deletes the record's row here, through the trigger below, so 0010's function does not have to be restated -- the
-- pattern 0016 uses for the sealed search vector. The trigger runs as whoever tombstones the record: the owner,
-- inside the SECURITY DEFINER purge, which row-level security never binds.
--
-- PRIVILEGES. ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants the application role SELECT and
-- INSERT on the table and UPDATE on last_injected_at and injection_count only (the upsert's ON CONFLICT DO UPDATE),
-- and no DELETE. Until the privileges call is re-run after this migration the role cannot reach the table: the
-- injection adapter then reports the write as not recorded and injects anyway, and the lessons view reads every
-- lesson as not tracked. Re-run the privileges call after migrating.
--
-- ROW-LEVEL SECURITY. The policies below admit a row to the application role only while its scope lies inside the
-- operation's declared bounds, for reading, inserting and updating alike; a grant never admits one. Like 0019's,
-- they switch nothing on: the privileges call enables row-level security on this table with the others.
--
-- LOCKS. CREATE TABLE and the trigger take brief locks on an empty or existing table; CREATE TRIGGER takes a SHARE ROW
-- EXCLUSIVE lock on experience_records for an instant. No table is rewritten.

CREATE TABLE IF NOT EXISTS agent_experience.experience_injections (
    experience_id    uuid        NOT NULL,
    tenant_id        text        NOT NULL,
    application_id   text        NOT NULL,
    project_id       text        NOT NULL,
    team_id          text        NULL,
    agent_id         text        NULL,
    user_id          text        NULL,
    last_injected_at timestamptz NOT NULL,
    injection_count  bigint      NOT NULL,
    CONSTRAINT experience_injections_pkey PRIMARY KEY (experience_id),
    CONSTRAINT experience_injections_count_positive CHECK (injection_count > 0)
);

-- The tombstone transition removes the record's injection row.
CREATE OR REPLACE FUNCTION agent_experience.clear_erased_record_injections() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
BEGIN
    IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL THEN
        DELETE FROM agent_experience.experience_injections WHERE experience_id = NEW.experience_id;
    END IF;

    RETURN NULL;
END;
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_injections_cleared'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_injections_cleared
            AFTER UPDATE OF deleted_at ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.clear_erased_record_injections();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_injections_cleared;

DROP POLICY IF EXISTS rls_injections_select ON agent_experience.experience_injections;
CREATE POLICY rls_injections_select ON agent_experience.experience_injections
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_injections_insert ON agent_experience.experience_injections;
CREATE POLICY rls_injections_insert ON agent_experience.experience_injections
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_injections_update ON agent_experience.experience_injections;
CREATE POLICY rls_injections_update ON agent_experience.experience_injections
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
