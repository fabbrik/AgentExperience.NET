-- AgentExperience.NET: match on any term and rank by coverage, behind row-level security too (Story 20.2).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0024, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead. 0024 stays as it was journaled; this script replaces what it created.
--
-- WHAT CHANGES. Until now a text search returned a record only when it contained every query term:
-- websearch_to_tsquery ANDs its terms, in the store's own statement and in 0024's search_experience_text alike. A
-- request written the way users write -- paraphrased, longer, with extra words -- then found nothing. The store now
-- takes the query's terms as the distinct lexemes of ts_debug('english', task_text), less the parts of a hyphenated
-- compound (hword_part, hword_asciipart, hword_numpart: "multi-stage" is one term), finds rows through an OR of
-- them (each lexeme a quoted tsquery literal, so the GIN indexes still answer it, and quotes, "or" and "-" in the text
-- are plain words, never operators), and keeps a row only when its vector contains at least p_minimum_matched_terms of
-- them, capped at their number, so a one-word query still matches. Relevance is the share of the query's lexemes the
-- row contains; rows are ordered by it, then by ts_rank_cd (against the AND of the lexemes for a row that has them
-- all, so for a plain-word request full matches keep the order they had, and against their OR for a partial one),
-- then by experience_id. A minimum at least the query's lexeme count asks for every term, as before.
--
-- WHY A DROP. The function gains an argument, p_minimum_matched_terms, so its signature changes and CREATE OR REPLACE
-- would add a second function beside 0024's instead of replacing it. This script drops 0024's signature (IF EXISTS, so
-- a re-run is a no-op), creates the new one, and revokes EXECUTE from PUBLIC as 0024 did. Dropping the function also
-- drops the application role's EXECUTE grant on it: until the privileges call
-- (ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync) is re-run, the candidate source finds no function it
-- may execute and runs its own statement under the policies, exactly as it does with row-level security on and the
-- function ungranted. Re-run the privileges call after migrating, as every upgrade does.
--
-- SECURITY REVIEW. 0024's review applies unchanged, point by point; only the text match and the ranking differ.
--   1. Nothing is returned without declared bounds (agent_experience.auth_set = 'on'), nor for a requested scope
--      outside them (rls_scope_admits).
--   2. Every row satisfies the read policy's own admission, the same text over the same 0019 helpers.
--   3. Every row then satisfies every predicate of the store's own search -- scope or live grant, not a tombstone,
--      eligible status, confidence floor, the authorship exclusion when asked, the text match -- built from
--      PostgresExperienceCandidateSource's own SQL constants with each @parameter renamed to its p_ argument, and
--      ranked and limited as the store ranks and limits. A test holds this script equal to them.
--   4. It is STABLE and only reads experience_records and experience_grants. The new expressions read only the
--      p_task_text argument and the row's own search vectors: ts_debug, array_agg, coalesce, unnest, string_agg, replace,
--      chr, the text-to-tsquery cast, ts_delete, length, cardinality, least, greatest and ts_rank_cd, all in
--      pg_catalog. The query's lexemes are quoted literals, so text in the request cannot become a tsquery operator.
--   5. Hardened as 0013 prescribes: SECURITY DEFINER, search_path pinned to pg_catalog, pg_temp (pg_temp last); every
--      relation and every function of this schema schema-qualified; EXECUTE revoked from PUBLIC; owned by the role
--      that runs the migrator. The privileges call refuses to enable row-level security unless the catalog's function
--      is exactly this one, byte for byte, and refuses any SECURITY DEFINER function the application role can execute
--      without its opt-in -- so 0024's definition, were it ever re-created beside this one, would be refused too.
--   6. It reads experience_grants as the owner, so the privileges call still requires the application role to hold
--      SELECT on that table while row-level security is on.
--
-- WHAT IT DOES NOT HIDE. As 0024: the table-wide GIN indexes make a search's cost reflect matches across every
-- tenant, a weak timing and statistics side channel (KL-17). An OR query matches more rows across all tenants than an
-- AND did before the minimum is applied, so that cost now grows with how common the request's words are, which
-- strengthens the channel.
--
-- LOCKS. DROP FUNCTION, CREATE OR REPLACE FUNCTION and REVOKE take no table lock.

DROP FUNCTION IF EXISTS agent_experience.search_experience_text(
    text, text, text, text, text, text, text, text[], double precision, integer, boolean, boolean);

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
    p_with_grants boolean,
    p_minimum_matched_terms integer)
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
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ((length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))))::real / greatest(cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))), 1)::real) AS relevance, NOT (r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) AS shared_by_grant, permitting_grant.grant_id AS permitting_grant_id, permitting_grant.disclosure AS permitting_grant_disclosure FROM agent_experience.experience_records r LEFT JOIN LATERAL (SELECT g.grant_id, g.disclosure, g.approach_arguments FROM agent_experience.experience_grants g WHERE g.experience_id = r.experience_id AND g.revoked_at IS NULL AND g.expires_at > clock_timestamp() AND g.tenant_id = r.tenant_id AND g.application_id = r.application_id AND g.project_id = r.project_id AND g.team_id IS NOT DISTINCT FROM r.team_id AND g.agent_id IS NOT DISTINCT FROM r.agent_id AND g.user_id IS NOT DISTINCT FROM r.user_id AND g.recipient_tenant_id = p_tenant_id AND g.recipient_application_id = p_application_id AND g.recipient_project_id = p_project_id AND g.recipient_team_id IS NOT DISTINCT FROM p_team_id AND g.recipient_agent_id IS NOT DISTINCT FROM p_agent_id AND g.recipient_user_id IS NOT DISTINCT FROM p_user_id ORDER BY g.grant_id LIMIT 1) permitting_grant ON true WHERE ((r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) OR permitting_grant.grant_id IS NOT NULL)
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND reflection_model_authored IS FALSE AND ((search_vector_sealed IS NULL AND search_vector @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) OR search_vector_sealed @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) AND (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) >= least(p_minimum_matched_terms, cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))) ORDER BY relevance DESC, CASE WHEN (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) = cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) THEN ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' & ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) ELSE ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) END DESC, experience_id LIMIT p_limit;
    ELSIF p_with_grants THEN
        RETURN QUERY
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ((length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))))::real / greatest(cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))), 1)::real) AS relevance, NOT (r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) AS shared_by_grant, permitting_grant.grant_id AS permitting_grant_id, permitting_grant.disclosure AS permitting_grant_disclosure FROM agent_experience.experience_records r LEFT JOIN LATERAL (SELECT g.grant_id, g.disclosure, g.approach_arguments FROM agent_experience.experience_grants g WHERE g.experience_id = r.experience_id AND g.revoked_at IS NULL AND g.expires_at > clock_timestamp() AND g.tenant_id = r.tenant_id AND g.application_id = r.application_id AND g.project_id = r.project_id AND g.team_id IS NOT DISTINCT FROM r.team_id AND g.agent_id IS NOT DISTINCT FROM r.agent_id AND g.user_id IS NOT DISTINCT FROM r.user_id AND g.recipient_tenant_id = p_tenant_id AND g.recipient_application_id = p_application_id AND g.recipient_project_id = p_project_id AND g.recipient_team_id IS NOT DISTINCT FROM p_team_id AND g.recipient_agent_id IS NOT DISTINCT FROM p_agent_id AND g.recipient_user_id IS NOT DISTINCT FROM p_user_id ORDER BY g.grant_id LIMIT 1) permitting_grant ON true WHERE ((r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id) OR permitting_grant.grant_id IS NOT NULL)
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND ((search_vector_sealed IS NULL AND search_vector @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) OR search_vector_sealed @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) AND (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) >= least(p_minimum_matched_terms, cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))) ORDER BY relevance DESC, CASE WHEN (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) = cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) THEN ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' & ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) ELSE ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) END DESC, experience_id LIMIT p_limit;
    ELSIF p_exclude_model_authored THEN
        RETURN QUERY
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ((length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))))::real / greatest(cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))), 1)::real) AS relevance, false AS shared_by_grant, NULL::uuid AS permitting_grant_id, NULL::text AS permitting_grant_disclosure FROM agent_experience.experience_records r WHERE r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND reflection_model_authored IS FALSE AND ((search_vector_sealed IS NULL AND search_vector @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) OR search_vector_sealed @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) AND (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) >= least(p_minimum_matched_terms, cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))) ORDER BY relevance DESC, CASE WHEN (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) = cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) THEN ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' & ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) ELSE ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) END DESC, experience_id LIMIT p_limit;
    ELSE
        RETURN QUERY
            SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, ((length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))))::real / greatest(cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))), 1)::real) AS relevance, false AS shared_by_grant, NULL::uuid AS permitting_grant_id, NULL::text AS permitting_grant_disclosure FROM agent_experience.experience_records r WHERE r.tenant_id = p_tenant_id AND r.application_id = p_application_id AND r.project_id = p_project_id AND r.team_id IS NOT DISTINCT FROM p_team_id AND r.agent_id IS NOT DISTINCT FROM p_agent_id AND r.user_id IS NOT DISTINCT FROM p_user_id
            AND (r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id)::text IN (SELECT agent_experience.rls_granted_keys())))
            AND r.deleted_at IS NULL AND status = ANY(p_statuses) AND reuse_confidence >= p_min_confidence AND ((search_vector_sealed IS NULL AND search_vector @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) OR search_vector_sealed @@ (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme))) AND (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) >= least(p_minimum_matched_terms, cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart')))) ORDER BY relevance DESC, CASE WHEN (length(coalesce(search_vector_sealed, search_vector)) - length(ts_delete(coalesce(search_vector_sealed, search_vector), (SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))))) = cardinality((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) THEN ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' & ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) ELSE ts_rank_cd(coalesce(search_vector_sealed, search_vector), (SELECT string_agg('''' || replace(replace(query_lexeme, chr(92), chr(92) || chr(92)), '''', '''''') || '''', ' | ')::tsquery FROM unnest((SELECT coalesce(array_agg(DISTINCT query_lexeme), '{}'::text[]) FROM ts_debug('english', p_task_text) AS query_token, unnest(query_token.lexemes) AS query_lexeme WHERE query_token.alias NOT IN ('hword_part', 'hword_asciipart', 'hword_numpart'))) AS query_lexemes(query_lexeme)), 32) END DESC, experience_id LIMIT p_limit;
    END IF;
END
$body$;

REVOKE ALL ON FUNCTION agent_experience.search_experience_text(
    text, text, text, text, text, text, text, text[], double precision, integer, boolean, boolean, integer) FROM PUBLIC;
