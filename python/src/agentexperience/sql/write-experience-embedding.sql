-- contract 1.6: stores one record's embedding, conditionally, in a transaction that ran declare-bounds.sql for the same
-- scope. Two statements, each starting at its "-- statement:" line and running to the next one (or the end); run
-- "write", and only when it reports 0 rows written, "probe-revision" in the same transaction, then commit. Parameters:
--   @experience_id                             uuid, the record whose summary was embedded
--   @tenant_id, @application_id, @project_id   text, the record's scope (exactly; a write never crosses a scope)
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
--   @model_id                                  text, the embedding model's ID: non-blank, at most 256 UTF-16 code units
--   @dimension                                 integer, the vector's length, 1 to 16000
--   @content_hash                              text, the content hash of the summary that was embedded (README-1.6.md)
--   @source_revision                           bigint, the record revision the summary was read at
--   @embedding                                 text, the vector as a pgvector literal, [x,y,...]; any text pgvector
--                                              parses as a vector of @dimension finite components
--   @now                                       timestamptz, the writer's clock, truncated to whole microseconds in UTC
-- "write" inserts or replaces the row only for a record that is live (not erased) in exactly that scope and still at
-- exactly @source_revision, its scope columns copied from the record, never from the parameters; the record row is
-- locked FOR KEY SHARE, so an erasure racing it either waits and then removes the vector, or runs first and leaves a
-- tombstone this statement does not select. A row already stored from a newer revision is never replaced by an older
-- one. Outcomes: 1 row written is Written. 0 rows: "probe-revision" returns the record's current revision (Stale, with
-- that revision) or no row at all (Missing: an unknown ID, a record in another scope, or an erased record).
-- The application role needs the grants in application-role-privileges-1.6.sql.
-- statement: write
INSERT INTO agent_experience.experience_embeddings AS t (experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, model_id, dimension, content_hash, source_revision, embedding, created_at, updated_at)
SELECT r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, @model_id, @dimension, @content_hash, @source_revision, CAST(@embedding AS vector), @now, @now
FROM agent_experience.experience_records r
WHERE r.experience_id = @experience_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL AND r.revision = @source_revision
FOR KEY SHARE OF r
ON CONFLICT (experience_id) DO UPDATE SET model_id = EXCLUDED.model_id, dimension = EXCLUDED.dimension, content_hash = EXCLUDED.content_hash, source_revision = EXCLUDED.source_revision, embedding = EXCLUDED.embedding, updated_at = EXCLUDED.updated_at
WHERE t.source_revision <= EXCLUDED.source_revision
-- statement: probe-revision
SELECT r.revision FROM agent_experience.experience_records r
WHERE r.experience_id = @experience_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL
