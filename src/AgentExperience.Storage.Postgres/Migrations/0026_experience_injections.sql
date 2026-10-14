-- AgentExperience.NET: when each record was last injected (Story 21.8).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0025, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. The lessons view shows when each lesson was last put in front of a model. No stored fact answered
-- that: a captured run's record names what it was exposed to, but only for runs that were captured and finalized,
-- and inside a payload that is sealed in encrypted mode; reuse feedback names exposures only when a host submits it.
-- So the .NET injection adapter now records it, after each injected block is handed over: one row per record,
-- upserted with the latest instant and a count, in one statement keyed on the record. The Python and Go ports do not
-- write it yet, so a lesson only they injected reads "never".
--
-- agent_experience.experience_injections: one row per record that has been injected in its own scope.
--   * Keyed on experience_id, with a foreign key to experience_records ON DELETE CASCADE. The row carries the record's
--     own scope columns, copied from the record by the upsert (INSERT ... SELECT FROM experience_records in exactly the
--     caller's scope, live only), and the policies below require a live record with exactly that ID and those scope
--     columns, so a caller cannot create or move a row for a record in any other scope. A record another scope borrows
--     through a grant and injects there is not counted: its owner sees that delivery in experience_grant_access.
--   * The instant is clamped to the database's clock_timestamp() before it is compared, so a writer's clock running
--     ahead cannot pin it in the future; last_injected_at only moves forward (GREATEST); injection_count rises by one
--     per injected block and saturates at the bigint maximum.
--   * It holds no content: an ID, a scope, an instant and a count. Nothing here is sealed in encrypted mode.
--
-- ERASURE. Every erasure path tombstones the record through 0010's purge_experience_record (one record per
-- transaction, the sweeps included), and the tombstone transition deletes the record's row here through the trigger
-- below, so 0010's function does not have to be restated -- the pattern 0016 uses for the sealed search vector. The
-- trigger runs as whoever tombstones the record: the owner, inside the SECURITY DEFINER purge, which row-level security
-- never binds. A record row is never deleted (0010's experience_records_no_delete refuses it); the foreign key's cascade
-- covers one removed anyway, by an owner who disabled that guard. In encrypted mode the store also skips a record whose
-- key is already destroyed, and the lessons view never lists one, so its row (if a key was destroyed outside an erasure)
-- discloses nothing and goes with the record's tombstone.
--
-- CONCURRENCY. The upsert locks the records it names FOR KEY SHARE, in experience_id order, before it writes each row:
-- the established pattern of every adapter write that checks a record (0010's header). The purge locks its one record
-- FOR UPDATE before anything else, which conflicts with FOR KEY SHARE, so an upsert that started first holds the purge
-- until it commits (and the trigger then deletes its row), and one that starts second waits and re-reads the record as
-- a tombstone, writing nothing. No deadlock is possible: the upsert takes an injection row's lock only after the lock on
-- that row's record, the purge takes the injection row's lock only after its own record's, and the purge locks one
-- record per transaction, so no transaction ever waits for a record lock while holding another transaction's
-- injection row.
--
-- PRIVILEGES. ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants the application role SELECT and
-- INSERT on the table and UPDATE on last_injected_at and injection_count only (the upsert's ON CONFLICT DO UPDATE),
-- and no DELETE; it grants them only when the table exists, so running it before this migration still succeeds. Until
-- it is re-run after this migration the role cannot reach the table: injections are reported as not recorded and the
-- lessons view reads every lesson as not tracked. The table is .NET-only and is not in the contract's privileges file,
-- whose REVOKE ALL removes the grants: in a mixed fleet, run the .NET privileges call after that script.
--
-- ROW-LEVEL SECURITY. The policies below admit a row to the application role only while its scope lies inside the
-- operation's declared bounds, for reading, inserting and updating alike, and admit a written row only for a live
-- record with that ID in exactly that scope; a grant never admits one. Like 0019's, they switch nothing on: the
-- privileges call enables row-level security on this table with the others.
--
-- LOCKS. CREATE TABLE and the trigger take brief locks on an empty or existing table; CREATE TRIGGER and the foreign key
-- take a SHARE ROW EXCLUSIVE lock on experience_records for an instant. No table is rewritten.

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
    CONSTRAINT experience_injections_record_fkey FOREIGN KEY (experience_id)
        REFERENCES agent_experience.experience_records (experience_id) ON DELETE CASCADE,
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
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))
        AND EXISTS (
            SELECT 1 FROM agent_experience.experience_records r
            WHERE r.experience_id = experience_injections.experience_id
              AND r.tenant_id = experience_injections.tenant_id
              AND r.application_id = experience_injections.application_id
              AND r.project_id = experience_injections.project_id
              AND r.team_id IS NOT DISTINCT FROM experience_injections.team_id
              AND r.agent_id IS NOT DISTINCT FROM experience_injections.agent_id
              AND r.user_id IS NOT DISTINCT FROM experience_injections.user_id
              AND r.deleted_at IS NULL));

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
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))
        AND EXISTS (
            SELECT 1 FROM agent_experience.experience_records r
            WHERE r.experience_id = experience_injections.experience_id
              AND r.tenant_id = experience_injections.tenant_id
              AND r.application_id = experience_injections.application_id
              AND r.project_id = experience_injections.project_id
              AND r.team_id IS NOT DISTINCT FROM experience_injections.team_id
              AND r.agent_id IS NOT DISTINCT FROM experience_injections.agent_id
              AND r.user_id IS NOT DISTINCT FROM experience_injections.user_id
              AND r.deleted_at IS NULL));
