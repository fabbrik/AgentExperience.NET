-- AgentExperience.NET: the row-level security policies for derived embeddings (Story 15.1).
-- Applied by ExperienceVectorSchemaMigrator -- this package's own migrator -- and recorded in
-- agent_experience.schema_versions alongside the base adapter's scripts. Every statement is idempotent on
-- purpose, matching 0004, so a database whose schema was applied by hand can still be journaled. Do not edit
-- this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. The base adapter's 0019 adds the policies, and the helper functions they read, for every table
-- that package owns. experience_embeddings belongs to this package, so its policies are here, on the same
-- helpers: the embedding row's own scope inside the operation's declared bounds, or -- for a read only -- a live
-- sharing grant over the embedded record whose recipient scope is inside them, exactly as 0019 admits the record
-- itself. A write, an upsert's update and a removal must all lie inside the bounds; a grant never admits one.
--
-- IT DOES NOT SWITCH ANYTHING ON. ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync enables row-level
-- security on this table, as on the others, only when the host sets
-- ExperienceApplicationRoleOptions.EnableRowLevelSecurity -- and refuses to while these policies are missing, so
-- the table can never be left with row-level security on and nothing admitting a row. Never FORCE: the owner's
-- erasure function deletes an erased record's vector as the owner, and the foreign key's cascade is not subject
-- to row-level security either.
--
-- RUN THE BASE MIGRATION FIRST. The helpers are the base adapter's 0019; without them this script stops with a
-- message saying so, and nothing is applied.
--
-- LOCKS. CREATE POLICY and DROP POLICY take an ACCESS EXCLUSIVE lock on the table for an instant.

DO $body$
BEGIN
    IF pg_catalog.to_regprocedure('agent_experience.rls_bound(text)') IS NULL
        OR pg_catalog.to_regprocedure('agent_experience.rls_unbounded(text)') IS NULL
        OR pg_catalog.to_regprocedure('agent_experience.rls_granted_keys()') IS NULL THEN
        RAISE EXCEPTION 'agent_experience row-level security helpers are missing: run ExperienceSchemaMigrator.MigrateAsync (0019_row_level_security.sql) before the vectors migrator';
    END IF;
END
$body$;

DROP POLICY IF EXISTS rls_embeddings_select ON agent_experience.experience_embeddings;
CREATE POLICY rls_embeddings_select ON agent_experience.experience_embeddings
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id)::text IN (SELECT agent_experience.rls_granted_keys())));

DROP POLICY IF EXISTS rls_embeddings_insert ON agent_experience.experience_embeddings;
CREATE POLICY rls_embeddings_insert ON agent_experience.experience_embeddings
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_embeddings_update ON agent_experience.experience_embeddings;
CREATE POLICY rls_embeddings_update ON agent_experience.experience_embeddings
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

DROP POLICY IF EXISTS rls_embeddings_delete ON agent_experience.experience_embeddings;
CREATE POLICY rls_embeddings_delete ON agent_experience.experience_embeddings
    FOR DELETE
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));
