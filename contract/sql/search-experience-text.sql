SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id,
       status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at,
       payload_version, payload, relevance, shared_by_grant, permitting_grant_id, permitting_grant_disclosure
FROM agent_experience.search_experience_text(
    p_tenant_id => @tenant_id,
    p_application_id => @application_id,
    p_project_id => @project_id,
    p_team_id => @team_id,
    p_agent_id => @agent_id,
    p_user_id => @user_id,
    p_task_text => @task_text,
    p_statuses => @statuses,
    p_min_confidence => @min_confidence,
    p_limit => @limit,
    p_exclude_model_authored => @exclude_model_authored,
    p_with_grants => @with_grants,
    p_minimum_matched_terms => @minimum_matched_terms)
