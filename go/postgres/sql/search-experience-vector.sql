-- contract 1.6: the nearest-neighbour search over stored embeddings, in a transaction that ran declare-bounds.sql for
-- the reader's scope. Four statements, each starting at its "-- statement:" line and running to the next one (or the
-- end): run "search", or "search-excluding-model-authored" when model-written lessons are excluded; only when it
-- returns no row, run the matching compatibility probe ("compatibility" or "compatibility-excluding-model-authored")
-- in the same transaction. Parameters:
--   @tenant_id, @application_id, @project_id   text, the reader's scope
--   @team_id, @agent_id, @user_id              text, NULL when the scope leaves the field unset
--   @statuses                                  text[], {Validated,Reinforced}
--   @min_confidence                            double precision, 0.5
--   @model_id                                  text, the query vector's model ID: non-blank, at most 256 UTF-16
--                                              code units
--   @query_vector                              text, the query vector as a pgvector literal, [x,y,...]
--   @limit                                     integer, 51 (the candidate limit, 50, plus one); search only
-- {dim} is not a parameter: replace every {dim} with the query vector's length (1 to 16000) in decimal digits before
-- running the statement. A pgvector type modifier is part of the type, so it cannot be bound. The probes have no {dim}.
-- Rows come back nearest first; the statement has no tie-break, so order rows of equal distance by experience_id
-- ascending (the lower-case hyphenated text, ordinally). distance is double precision, NaN for a zero-magnitude vector;
-- relevance = clamp(1 - distance / 2, 0, 1), and 0 for NaN. Shared rows are out of contract 1.x scope: the statement
-- matches the reader's own scope only, as the text search does with @with_grants false.
-- An empty search's outcome is the probe's: both false, or the first false, is Found (nothing comparable is stored);
-- the first true and the second false is ModelMismatch; both true is DimensionMismatch.
-- The application role needs the grants in application-role-privileges-1.6.sql.
-- statement: search
SELECT r.experience_id, r.source_run_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, r.task_id, r.status, r.reuse_confidence, r.supporting_validations, r.contradictions, r.revision, r.created_at, r.updated_at, r.payload_version, r.payload, false AS shared_by_grant, NULL::uuid AS permitting_grant_id, NULL::text AS permitting_grant_disclosure, (e.embedding::vector({dim}) <=> CAST(@query_vector AS vector({dim}))) AS distance
FROM agent_experience.experience_embeddings e JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id
WHERE (e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id AND e.user_id IS NOT DISTINCT FROM @user_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id) AND r.deleted_at IS NULL AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence
AND e.model_id = @model_id AND e.dimension = {dim}
ORDER BY (e.embedding::vector({dim}) <=> CAST(@query_vector AS vector({dim}))) LIMIT @limit
-- statement: search-excluding-model-authored
SELECT r.experience_id, r.source_run_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, r.task_id, r.status, r.reuse_confidence, r.supporting_validations, r.contradictions, r.revision, r.created_at, r.updated_at, r.payload_version, r.payload, false AS shared_by_grant, NULL::uuid AS permitting_grant_id, NULL::text AS permitting_grant_disclosure, (e.embedding::vector({dim}) <=> CAST(@query_vector AS vector({dim}))) AS distance
FROM agent_experience.experience_embeddings e JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id
WHERE (e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id AND e.user_id IS NOT DISTINCT FROM @user_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id) AND r.deleted_at IS NULL AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence
AND r.reflection_model_authored IS FALSE AND e.model_id = @model_id AND e.dimension = {dim}
ORDER BY (e.embedding::vector({dim}) <=> CAST(@query_vector AS vector({dim}))) LIMIT @limit
-- statement: compatibility
SELECT EXISTS (SELECT 1 FROM agent_experience.experience_embeddings e JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id WHERE (e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id AND e.user_id IS NOT DISTINCT FROM @user_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id) AND r.deleted_at IS NULL AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence),
EXISTS (SELECT 1 FROM agent_experience.experience_embeddings e JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id WHERE (e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id AND e.user_id IS NOT DISTINCT FROM @user_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id) AND r.deleted_at IS NULL AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence AND e.model_id = @model_id)
-- statement: compatibility-excluding-model-authored
SELECT EXISTS (SELECT 1 FROM agent_experience.experience_embeddings e JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id WHERE (e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id AND e.user_id IS NOT DISTINCT FROM @user_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id) AND r.deleted_at IS NULL AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence AND r.reflection_model_authored IS FALSE),
EXISTS (SELECT 1 FROM agent_experience.experience_embeddings e JOIN agent_experience.experience_records r ON r.experience_id = e.experience_id WHERE (e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id AND e.user_id IS NOT DISTINCT FROM @user_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id) AND r.deleted_at IS NULL AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence AND r.reflection_model_authored IS FALSE AND e.model_id = @model_id)
