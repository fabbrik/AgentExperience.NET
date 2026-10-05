-- AgentExperience.NET: keep the text index usable under row-level security (Story 17.7, KL-17).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0023, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- THE PROBLEM. With row-level security on (0019, story 15.1), rls_records_select is a security barrier: PostgreSQL
-- evaluates a qual that is not LEAKPROOF only after the policy, so it cannot push it into an index scan. The text
-- match is "search_vector @@ websearch_to_tsquery(...)", and @@ is not leakproof, so the GIN indexes
-- (ix_experience_records_search, ix_experience_records_search_sealed) go unused and a text search reads every live
-- record the declared bounds admit. Marking @@ leakproof is a superuser's call about a built-in, and is not made here.
--
-- WHAT THIS SCRIPT DOES. It creates agent_experience.search_experience_text, the text channel's search run as the
-- owner. The policies are never forced, so the owner is not bound by them, its query has no barrier, and the match
-- drives the GIN indexes exactly as it does with row-level security off. PostgresExperienceCandidateSource calls it
-- only while row-level security is enabled on experience_records and the application role may execute it; with
-- row-level security off the store's own statement runs, unchanged. It revokes EXECUTE from PUBLIC; the privileges call
-- (ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync) grants it to the application role exactly while it
-- enables row-level security, and revokes it otherwise.
--
-- SECURITY REVIEW.
--
-- Why SECURITY DEFINER. The barrier is the policy's, and the only roles it does not bind are the owner, a superuser
-- and a BYPASSRLS role. Running the query as the owner, inside a function the application role can only call, is the
-- one way to drop the barrier for this query without granting BYPASSRLS or marking a built-in leakproof -- both of
-- which would drop it for every query the role sends.
--
-- What it trusts. The transaction-local settings the policies read (agent_experience.auth_*), and nothing else:
-- exactly what the policies trust. A session that can run arbitrary SQL as the application role can declare any
-- bounds, and then reads through this function exactly what it could read through the policies (KL-17).
--
-- Why it cannot read more than the policies.
--   1. With nothing declared (agent_experience.auth_set not 'on') it returns no row, before anything is read. Nor does
--      it when the requested scope (the p_ arguments) lies outside the declared bounds: rls_scope_admits, 0019's helper,
--      with AuthorizationContext.Permits' reading of a null bound. That scope is what selects the permitting grant, so a
--      grant whose ID and disclosure level it returns always has a recipient inside the bounds -- one rls_grants_select
--      shows the caller anyway.
--   2. Every row it returns satisfies the read policy's own admission, rls_records_select's USING expression over the
--      r-qualified row: the record's scope inside the declared bounds, or a live grant whose recipient lies inside them
--      shares it. It is the same text, built from the same source (RowLevelSecurityPolicies in the adapter), calling the
--      same 0019 helpers -- rls_bound, rls_unbounded and rls_granted_keys -- so it does not restate their logic.
--      rls_granted_keys reads experience_grants as the owner here, under the same WHERE clause (recipient inside the
--      bounds, live on the database's clock), which is a subset of what rls_grants_select admits the application role.
--   3. Every row then also satisfies every predicate of the store's own search -- the exact scope or a live grant
--      naming the requesting scope, not a tombstone, the eligible statuses, the confidence floor, the authorship
--      exclusion when asked (reflection_model_authored IS FALSE, story 17.1), the text match -- ranked and limited as
--      the store ranks and limits. These are PostgresExperienceCandidateSource's own SQL constants with each @parameter
--      renamed to its p_ argument, so the two cannot drift; a test holds this script equal to them.
--   4. It is STABLE and only reads: experience_records, and experience_grants through the store's permitting-grant
--      join and through rls_granted_keys. It returns exactly the columns the store's statement selects -- the record
--      columns, the relevance, the shared flag and the permitting grant's ID and level, the last two only for a grant
--      whose recipient is the requested scope, which point 1 keeps inside the bounds.
--   6. It reads experience_grants as the owner, so the privileges call requires the application role to hold SELECT on
--      that table while row-level security is on: a grant the function shares is one the role could read itself.
--      Revoking that SELECT afterwards is unsupported drift, refused by the next privileges call.
--   5. Hardened as 0013 prescribes: search_path pinned to pg_catalog, pg_temp (pg_temp last); every relation and every
--      function of this schema schema-qualified; EXECUTE revoked from PUBLIC; owned by the role that runs the migrator,
--      which owns the tables. The privileges call refuses to enable row-level security unless the catalog's function is
--      exactly this one -- SECURITY DEFINER, PL/pgSQL, STABLE, not leakproof, this search_path alone, owned by the tables'
--      owner, these arguments and result, this body byte for byte -- and refuses any SECURITY DEFINER function the
--      application role can execute without its opt-in. It never re-creates this function: one altered by hand is refused.
--
-- The definition check runs when the privileges call enables row-level security. A CREATE OR REPLACE the owner runs
-- afterwards is used until the next privileges call refuses it: the owner is trusted, as for every function here.
--
-- WHAT IT DOES NOT HIDE. The function searches the table-wide GIN indexes, so a search's cost reflects how many rows
-- match across every tenant, not only the declared one: a weak timing and statistics side channel. A tenant-leading
-- composite GIN index would need the btree_gin extension, which the library does not require (KL-17).
--
-- So with the policies on, a search through this function returns exactly what the store's own statement returns
-- with them off, for the same data and the same authorization, which the store has already checked contains the
-- requested scope (AuthorizationContext.Permits).
--
-- LOCKS. CREATE OR REPLACE FUNCTION and REVOKE take no table lock.

CREATE OR REPLACE FUNCTION agent_experience.search_experience_text(
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_task_text text,
    p_statuses text[],
    p_min_confidence double precision,
    p_limit integer,
    p_exclude_model_authored boolean,
    p_with_grants boolean)
RETURNS TABLE (
    experience_id uuid,
    source_run_id uuid,
    tenant_id text,
    application_id text,
    project_id text,
    team_id text,
    agent_id text,
    user_id text,
    task_id text,
    status text,
    reuse_confidence double precision,
    supporting_validations integer,
    contradictions integer,
    revision bigint,
    created_at timestamp with time zone,
    updated_at timestamp with time zone,
    payload_version integer,
    payload jsonb,
    relevance real,
    shared_by_grant boolean,
    permitting_grant_id uuid,
    permitting_grant_disclosure text)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $body$
#variable_conflict use_column
BEGIN
    IF pg_catalog.current_setting('agent_experience.auth_set', true) IS DISTINCT FROM 'on' THEN
        RETURN;
    END IF;

    IF NOT agent_experience.rls_scope_admits(p_tenant_id, p_application_id, p_project_id, p_team_id, p_agent_id, p_user_id) THEN
        RETURN;
    END IF;

    IF p_with_grants AND p_exclude_model_authored THEN
        RETURN QUERY
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ts_rank_cd(coalesce(search_vector_sealed, search_vector), websearch_to_tsquery('english', p_task_text), 32) AS relevance, NOT (r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) AS shared_by_grant, permitting_grant.grant_id AS permitting_grant_id, permitting_grant.disclosure AS permitting_grant_disclosure FROM agent_experience.experience_records r LEFT JOIN LATERAL (SELECT g.grant_id, g.disclosure, g.approach_arguments FROM agent_experience.experience_grants g WHERE g.experience_id = r.experience_id AND g.revoked_at IS NULL AND g.expires_at > clock_timestamp() AND g.tenant_id = r.tenant_id AND g.application_id = r.application_id AND g.project_id = r.project_id AND g.team_id IS NOT DISTINCT FROM r.team_id AND g.agent_id IS NOT DISTINCT FROM r.agent_id AND g.user_id IS NOT DISTINCT FROM r.user_id AND g.recipient_tenant_id = p_tenant_id AND g.recipient_application_id = p_application_id AND g.recipient_project_id = p_project_id AND g.recipient_team_id IS NOT DISTINCT FROM p_team_id AND g.recipient_agent_id IS NOT DISTINCT FROM p_agent_id AND g.recipient_user_id IS NOT DISTINCT FROM p_user_id ORDER BY g.grant_id LIMIT 1) permitting_grant ON true WHERE ((r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) OR permitting_grant.grant_id IS NOT NULL)
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND reflection_model_authored IS FALSE AND ((search_vector_sealed IS NULL AND search_vector @@ websearch_to_tsquery('english', p_task_text)) OR search_vector_sealed @@ websearch_to_tsquery('english', p_task_text)) ORDER BY relevance DESC, experience_id LIMIT p_limit;
    ELSIF p_with_grants THEN
        RETURN QUERY
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ts_rank_cd(coalesce(search_vector_sealed, search_vector), websearch_to_tsquery('english', p_task_text), 32) AS relevance, NOT (r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) AS shared_by_grant, permitting_grant.grant_id AS permitting_grant_id, permitting_grant.disclosure AS permitting_grant_disclosure FROM agent_experience.experience_records r LEFT JOIN LATERAL (SELECT g.grant_id, g.disclosure, g.approach_arguments FROM agent_experience.experience_grants g WHERE g.experience_id = r.experience_id AND g.revoked_at IS NULL AND g.expires_at > clock_timestamp() AND g.tenant_id = r.tenant_id AND g.application_id = r.application_id AND g.project_id = r.project_id AND g.team_id IS NOT DISTINCT FROM r.team_id AND g.agent_id IS NOT DISTINCT FROM r.agent_id AND g.user_id IS NOT DISTINCT FROM r.user_id AND g.recipient_tenant_id = p_tenant_id AND g.recipient_application_id = p_application_id AND g.recipient_project_id = p_project_id AND g.recipient_team_id IS NOT DISTINCT FROM p_team_id AND g.recipient_agent_id IS NOT DISTINCT FROM p_agent_id AND g.recipient_user_id IS NOT DISTINCT FROM p_user_id ORDER BY g.grant_id LIMIT 1) permitting_grant ON true WHERE ((r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) OR permitting_grant.grant_id IS NOT NULL)
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND ((search_vector_sealed IS NULL AND search_vector @@ websearch_to_tsquery('english', p_task_text)) OR search_vector_sealed @@ websearch_to_tsquery('english', p_task_text)) ORDER BY relevance DESC, experience_id LIMIT p_limit;
    ELSIF p_exclude_model_authored THEN
        RETURN QUERY
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ts_rank_cd(coalesce(search_vector_sealed, search_vector), websearch_to_tsquery('english', p_task_text), 32) AS relevance, false AS shared_by_grant, NULL::uuid AS permitting_grant_id, NULL::text AS permitting_grant_disclosure FROM agent_experience.experience_records r WHERE r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND reflection_model_authored IS FALSE AND ((search_vector_sealed IS NULL AND search_vector @@ websearch_to_tsquery('english', p_task_text)) OR search_vector_sealed @@ websearch_to_tsquery('english', p_task_text)) ORDER BY relevance DESC, experience_id LIMIT p_limit;
    ELSE
        RETURN QUERY
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ts_rank_cd(coalesce(search_vector_sealed, search_vector), websearch_to_tsquery('english', p_task_text), 32) AS relevance, false AS shared_by_grant, NULL::uuid AS permitting_grant_id, NULL::text AS permitting_grant_disclosure FROM agent_experience.experience_records r WHERE r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND ((search_vector_sealed IS NULL AND search_vector @@ websearch_to_tsquery('english', p_task_text)) OR search_vector_sealed @@ websearch_to_tsquery('english', p_task_text)) ORDER BY relevance DESC, experience_id LIMIT p_limit;
    END IF;
END
$body$;

REVOKE ALL ON FUNCTION agent_experience.search_experience_text(
    text, text, text, text, text, text, text, text[], double precision, integer, boolean, boolean) FROM PUBLIC;
