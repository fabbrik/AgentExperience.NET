-- contract 1.18: the supersession lock scoped to the request, exactly as the .NET PostgreSQL store
-- (PostgresExperienceRecordStore) runs it, generated from its source and checked against it on every build. In
-- lifecycle.sql's COMMIT step 3, run "lock-supersession-rows-scoped" in place of 1.16's "lock-supersession-rows",
-- with the same @lock_ids (the record and the replacement, a uuid[]) and the request scope (@tenant_id ...
-- @user_id, NULL for an unset optional field). It locks only the rows of the request scope: a replacement in
-- another scope is not locked, and "check-supersession" then reads it as not found, exactly as before
-- (ReplacementNotAllowed). Every other statement of lifecycle.sql is unchanged.
-- statement: lock-supersession-rows-scoped
SELECT experience_id FROM agent_experience.experience_records WHERE experience_id = ANY(@lock_ids) AND tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id ORDER BY experience_id FOR UPDATE
