-- contract 1.8: records a captured run's injection holdout assignment. Run it once per assigned block (per chunk of at
-- most 200 records), in the background after the arm is decided, in a transaction that ran declare-bounds.sql for the
-- same scope. Parameters:
--   @run_id                                    uuid, the captured run
--   @arm                                       text, 'Treatment' (the block was delivered) or 'Holdout' (it was withheld)
--   @experience_ids                            uuid[], the records the block named, at most 200, each once (a record
--                                              named twice counts once, at the revision first named: drop the repeats
--                                              before the statement); IDs of records in another scope are harmless
--   @revisions                                 bigint[], the revision each record was rendered from, in the same order
--   @assigned_at                               timestamptz, when the arm was decided, truncated to whole microseconds
--                                              in UTC
--   @tenant_id, @application_id, @project_id   text, the injecting agent's scope
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
-- It writes one row per named record that is live (not erased) in exactly that scope, its scope copied from the record,
-- at the revision named; a record borrowed through a grant, or in another scope, is skipped. A row that already exists
-- for the run and record is left as it is (a run invoked again keeps its first rows). The instant is clamped to the
-- database clock (clock_timestamp()). The late-write guard: the run's own records (source_run_id is the run, in the
-- caller's tenant) are locked FOR KEY SHARE first, and nothing is written when every one of them is by then erased; a
-- run with no record yet, or with one still live, is written. The named records are then locked FOR KEY SHARE in
-- experience_id order, so an erasure racing it either waits and then removes the rows, or runs first and leaves a
-- tombstone this statement does not select.
-- Encrypted mode: before it, the .NET store drops from @experience_ids every record whose key is destroyed. A contract
-- 1.x port has no key store and reads only plaintext rows, so for a port that step is a no-op.
-- The application role needs the grants in application-role-privileges-1.8.sql; without them it fails with
-- insufficient_privilege (42501), which a port reports as not recorded without affecting the injection.
WITH run_record AS MATERIALIZED (SELECT d.deleted_at
FROM agent_experience.experience_records d
WHERE d.experience_id = ANY (ARRAY(SELECT s.experience_id FROM agent_experience.experience_records s WHERE s.tenant_id = @tenant_id AND s.source_run_id = @run_id))
ORDER BY d.experience_id FOR KEY SHARE OF d)
INSERT INTO agent_experience.experience_holdout_assignments (run_id, experience_id, revision, arm, tenant_id, application_id, project_id, team_id, agent_id, user_id, assigned_at)
SELECT @run_id, r.experience_id, n.revision, @arm, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, LEAST(@assigned_at, pg_catalog.clock_timestamp())
FROM agent_experience.experience_records r
JOIN ROWS FROM (pg_catalog.unnest(@experience_ids), pg_catalog.unnest(@revisions)) AS n(experience_id, revision) ON n.experience_id = r.experience_id
WHERE r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL
AND (SELECT pg_catalog.bool_and(run_record.deleted_at IS NOT NULL) FROM run_record) IS NOT TRUE
ORDER BY r.experience_id FOR KEY SHARE OF r
ON CONFLICT (run_id, experience_id) DO NOTHING
