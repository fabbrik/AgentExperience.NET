-- contract 1.4: records that a block was injected, for the lessons view's "last injected". Run it once per injected
-- block (per chunk of at most 200 IDs), after the block is handed over, in a transaction that ran
-- declare-bounds.sql for the same scope. Parameters:
--   @experience_ids                            uuid[], the records the block delivered, at most 200; duplicates
--                                              and IDs of records in another scope are harmless (no row)
--   @injected_at                               timestamptz, the instant the block was injected, truncated to whole
--                                              microseconds in UTC
--   @tenant_id, @application_id, @project_id   text, the injecting agent's scope
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
-- It writes one row per named record that is live (not erased) in exactly that scope, its scope copied from the
-- record; a record borrowed through a grant, or in another scope, is skipped. The records are locked FOR KEY SHARE in
-- experience_id order, so an erasure racing it either waits and then removes the row, or runs first and leaves a
-- tombstone this statement does not select. The instant is clamped to the database clock (clock_timestamp()) and then
-- only moves forward; the count rises by one per statement and saturates at the bigint maximum.
-- Encrypted mode: before it, the .NET store drops from @experience_ids every record whose key is destroyed (one batched
-- key lookup in its key store). A contract 1.x port has no key store and reads only plaintext rows, so it never injects
-- a sealed record: for a port that step is a no-op, and the statement is the whole of it.
-- The application role needs the grants in application-role-privileges-1.4.sql; without them it fails with
-- insufficient_privilege (42501), which a port reports as not recorded without affecting the injection.
INSERT INTO agent_experience.experience_injections AS i (experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, last_injected_at, injection_count)
SELECT r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, LEAST(@injected_at, pg_catalog.clock_timestamp()), 1
FROM agent_experience.experience_records r
WHERE r.experience_id = ANY(@experience_ids) AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL
ORDER BY r.experience_id FOR KEY SHARE OF r
ON CONFLICT (experience_id) DO UPDATE SET last_injected_at = GREATEST(i.last_injected_at, EXCLUDED.last_injected_at), injection_count = CASE WHEN i.injection_count = 9223372036854775807 THEN i.injection_count ELSE i.injection_count + 1 END
