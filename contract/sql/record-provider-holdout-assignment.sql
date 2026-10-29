-- contract 1.11: records a captured run's arm in another memory provider's holdout (migration 0029's
-- agent_experience.experience_provider_holdout_assignments). Run it once per invocation that takes part, in the
-- background after the arm is decided, in a transaction that ran declare-bounds.sql for the same scope. Parameters:
--   @run_id                                    uuid, the captured run
--   @provider_key                              text, the host's key for the provider: 1 to 100 characters of
--                                              A-Z, a-z, 0-9, '.', '_' and '-'
--   @arm                                       text, 'Treatment' (the provider's context was passed through) or
--                                              'Holdout' (it was withheld)
--   @assigned_at                               timestamptz, when the arm was decided, truncated to whole microseconds
--                                              in UTC
--   @tenant_id, @application_id, @project_id   text, the scope the run is captured in
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
-- It writes one row for the run and key, in exactly that scope; no record is read for it, and the provider's items are
-- opaque (no item, no content: a run, a key, an arm, a scope and an instant). A row that already exists for the run and
-- key in the tenant is left as it is, whatever its scope: a run invoked again keeps its first row. The instant is
-- clamped to the database clock (clock_timestamp()). The late-write guard is record-holdout-assignment.sql's: the run's
-- own records (source_run_id is the run, in the caller's tenant) are locked FOR KEY SHARE first, and nothing is written
-- when every one of them is by then erased; a run with no record yet, or with one still live, is written.
-- The application role needs the grants in application-role-privileges-1.11.sql; without them it fails with
-- insufficient_privilege (42501), which a port reports as not recorded without affecting the invocation.
WITH run_record AS MATERIALIZED (SELECT d.deleted_at
FROM agent_experience.experience_records d
WHERE d.experience_id = ANY (ARRAY(SELECT s.experience_id FROM agent_experience.experience_records s WHERE s.tenant_id = @tenant_id AND s.source_run_id = @run_id))
ORDER BY d.experience_id FOR KEY SHARE OF d)
INSERT INTO agent_experience.experience_provider_holdout_assignments (run_id, provider_key, arm, tenant_id, application_id, project_id, team_id, agent_id, user_id, assigned_at)
SELECT @run_id, @provider_key, @arm, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, LEAST(@assigned_at, pg_catalog.clock_timestamp())
WHERE (SELECT pg_catalog.bool_and(run_record.deleted_at IS NOT NULL) FROM run_record) IS NOT TRUE
ON CONFLICT (tenant_id, run_id, provider_key) DO NOTHING
