-- contract 1.7: the lessons view's page of records, each with when it was last injected, in one bounded statement, in a
-- transaction that ran declare-bounds.sql for the reader's scope. Four statements, each starting at its "-- statement:"
-- line and running to the next one (or the end): run "page", or "page-with-statuses" when the query names statuses. When
-- it fails with undefined_table (42P01) or insufficient_privilege (42501) -- the injections table is not migrated, or the
-- application role has not been granted it (application-role-privileges-1.4.sql) -- roll that transaction back, open a
-- new one that declares the same bounds, and run "page-without-injections" or "page-without-injections-with-statuses":
-- the same page with no injection summary, and the page's injections are then not tracked. Parameters:
--   @tenant_id, @application_id, @project_id   text, the reader's scope
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
--   @statuses                                  text[], the statuses asked for (the "-with-statuses" statements only)
--   @limit                                     integer, 1 to 500: the number of rows to read (README-1.7.md says how a
--                                              port derives it from the query's limit)
-- Rows come back newest first: created_at descending, then experience_id ascending (the lower-case hyphenated text,
-- ordinally). The page is the reader's own scope only, live (not erased) records only. The first 18 columns are the
-- record columns, as in the other reads; the "page" statements add last_injected_at (timestamptz) and injection_count
-- (bigint) as columns 19 and 20, both NULL for a record never injected.
-- The application role needs the grants in application-role-privileges.sql and application-role-privileges-1.4.sql.
-- statement: page
WITH page AS (SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload FROM agent_experience.experience_records WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL
ORDER BY created_at DESC, experience_id LIMIT @limit)
SELECT page.*, i.last_injected_at, i.injection_count FROM page LEFT JOIN agent_experience.experience_injections i ON i.experience_id = page.experience_id AND i.tenant_id = page.tenant_id AND i.application_id = page.application_id AND i.project_id = page.project_id AND i.team_id IS NOT DISTINCT FROM page.team_id AND i.agent_id IS NOT DISTINCT FROM page.agent_id AND i.user_id IS NOT DISTINCT FROM page.user_id
ORDER BY page.created_at DESC, page.experience_id
-- statement: page-with-statuses
WITH page AS (SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload FROM agent_experience.experience_records WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND status = ANY(@statuses)
ORDER BY created_at DESC, experience_id LIMIT @limit)
SELECT page.*, i.last_injected_at, i.injection_count FROM page LEFT JOIN agent_experience.experience_injections i ON i.experience_id = page.experience_id AND i.tenant_id = page.tenant_id AND i.application_id = page.application_id AND i.project_id = page.project_id AND i.team_id IS NOT DISTINCT FROM page.team_id AND i.agent_id IS NOT DISTINCT FROM page.agent_id AND i.user_id IS NOT DISTINCT FROM page.user_id
ORDER BY page.created_at DESC, page.experience_id
-- statement: page-without-injections
SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload FROM agent_experience.experience_records WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL
ORDER BY created_at DESC, experience_id LIMIT @limit
-- statement: page-without-injections-with-statuses
SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload FROM agent_experience.experience_records WHERE tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND status = ANY(@statuses)
ORDER BY created_at DESC, experience_id LIMIT @limit
