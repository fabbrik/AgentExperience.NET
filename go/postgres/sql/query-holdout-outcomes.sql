-- contract 1.9: reads the injection holdout's outcome rows in exactly one scope, a page at a time, for the benefit
-- estimate. Two statements, each starting at its "-- statement:" line and running to the next one (or the end): run
-- "first-page" for the first page, and "next-page" for every later one, in a transaction that ran declare-bounds.sql
-- for the scope read (the caller's own bounds). Parameters:
--   @tenant_id, @application_id, @project_id   text, the scope read: the scope the blocks were built in
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
--   @limit                                     integer, 1 to 1000: the page size (.NET reads 1000 rows a page)
--   @after_run_id, @after_experience_id        uuid, "next-page" only: the run_id and experience_id of the previous
--                                              page's last row (columns 19 and 21)
-- The page is the scope's assignment rows in (run_id, experience_id) order, at most @limit of them. Each comes back as
-- one row with part = 0: columns 1-18 are the run's own record (the live record in exactly the scope whose
-- source_run_id is the run, the earliest created, then the lowest experience_id), all NULL when the run has none, and
-- columns 19-22 are the assignment's run_id, arm, experience_id and assigned_at; column 23 (part) is 0. After the page's
-- rows come the live records in exactly the scope that the page's rows name as lessons, one row each with part = 1:
-- columns 1-18 are the record and 19-22 are NULL. Rows arrive ordered by part, run_id, the assignment's experience_id,
-- then the record's experience_id. A port reads a run's outcome (outcome.status) and model from the part 0 record's
-- payload, and a lesson's model from its part 1 record's payload: README-1.9.md says how. A sealed record
-- (payload_version 2) cannot be read by a port: its run has no outcome, and it names no model. The page is full when it
-- has @limit part 0 rows: read the next one after its last row; a page with fewer is the last.
-- The application role needs the grants in application-role-privileges.sql and application-role-privileges-1.8.sql:
-- without the latter it fails with insufficient_privilege (42501).
-- statement: first-page
WITH page AS (SELECT run_id, arm, experience_id, assigned_at
FROM agent_experience.experience_holdout_assignments
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id
ORDER BY run_id, experience_id LIMIT @limit),
run_record AS (SELECT DISTINCT ON (source_run_id) experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload
FROM agent_experience.experience_records
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND source_run_id IN (SELECT run_id FROM page)
ORDER BY source_run_id, created_at, experience_id),
lesson_record AS (SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload
FROM agent_experience.experience_records
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND experience_id IN (SELECT experience_id FROM page))
SELECT run_record.*, page.run_id, page.arm, page.experience_id, page.assigned_at, 0 AS part
FROM page LEFT JOIN run_record ON run_record.source_run_id = page.run_id
UNION ALL
SELECT lesson_record.*, NULL, NULL, NULL, NULL, 1 AS part
FROM lesson_record
ORDER BY 23, 19, 21, 1
-- statement: next-page
WITH page AS (SELECT run_id, arm, experience_id, assigned_at
FROM agent_experience.experience_holdout_assignments
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND (run_id, experience_id) > (@after_run_id, @after_experience_id)
ORDER BY run_id, experience_id LIMIT @limit),
run_record AS (SELECT DISTINCT ON (source_run_id) experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload
FROM agent_experience.experience_records
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND source_run_id IN (SELECT run_id FROM page)
ORDER BY source_run_id, created_at, experience_id),
lesson_record AS (SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload
FROM agent_experience.experience_records
WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND experience_id IN (SELECT experience_id FROM page))
SELECT run_record.*, page.run_id, page.arm, page.experience_id, page.assigned_at, 0 AS part
FROM page LEFT JOIN run_record ON run_record.source_run_id = page.run_id
UNION ALL
SELECT lesson_record.*, NULL, NULL, NULL, NULL, 1 AS part
FROM lesson_record
ORDER BY 23, 19, 21, 1
