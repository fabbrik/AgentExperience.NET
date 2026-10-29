-- contract 1.11: reads one memory provider's holdout rows in exactly one scope, a page at a time, for the provider's
-- benefit estimate. Two statements, each starting at its "-- statement:" line and running to the next one (or the end):
-- run "first-page" for the first page, and "next-page" for every later one, in a transaction that ran
-- declare-bounds.sql for the scope read (the caller's own bounds). Parameters:
--   @tenant_id, @application_id, @project_id   text, the scope read: the scope the runs were captured in
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
--   @provider_key                              text, the provider's key
--   @limit                                     integer, 1 to 1000: the page size (.NET reads 1000 rows a page)
--   @after_run_id                              uuid, "next-page" only: the run_id of the previous page's last row
--                                              (column 19)
-- The page is the scope's rows for the key in run_id order, at most @limit of them, one row each: columns 1-18 are the
-- run's own record (the live record in exactly the scope whose source_run_id is the run, the earliest created, then the
-- lowest experience_id), all NULL when the run has none, and columns 19-21 are the row's run_id, arm and assigned_at.
-- A port reads the run's outcome (outcome.status) from the record's payload, as README-1.9.md reads a run's outcome; a
-- sealed record (payload_version 2) cannot be read by a port, so its run has no outcome. The page is full when it has
-- @limit rows: read the next one after its last row; a page with fewer is the last.
-- The application role needs the grants in application-role-privileges.sql and application-role-privileges-1.11.sql:
-- without the latter it fails with insufficient_privilege (42501).
-- statement: first-page
WITH page AS (SELECT run_id, arm, assigned_at
FROM agent_experience.experience_provider_holdout_assignments
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND provider_key = @provider_key
ORDER BY run_id LIMIT @limit),
run_record AS (SELECT DISTINCT ON (source_run_id) experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload
FROM agent_experience.experience_records
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND source_run_id IN (SELECT run_id FROM page)
ORDER BY source_run_id, created_at, experience_id)
SELECT run_record.*, page.run_id, page.arm, page.assigned_at
FROM page LEFT JOIN run_record ON run_record.source_run_id = page.run_id
ORDER BY page.run_id
-- statement: next-page
WITH page AS (SELECT run_id, arm, assigned_at
FROM agent_experience.experience_provider_holdout_assignments
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND provider_key = @provider_key AND run_id > @after_run_id
ORDER BY run_id LIMIT @limit),
run_record AS (SELECT DISTINCT ON (source_run_id) experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload
FROM agent_experience.experience_records
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND source_run_id IN (SELECT run_id FROM page)
ORDER BY source_run_id, created_at, experience_id)
SELECT run_record.*, page.run_id, page.arm, page.assigned_at
FROM page LEFT JOIN run_record ON run_record.source_run_id = page.run_id
ORDER BY page.run_id
