-- AgentExperience.NET: initial Experience Record schema (payload_version 1).
-- Plain SQL with no journal table, so a DbUp-style migrator can run it unchanged.

CREATE SCHEMA IF NOT EXISTS agent_experience;

CREATE TABLE IF NOT EXISTS agent_experience.experience_records (
    experience_id          uuid             NOT NULL,
    source_run_id          uuid             NOT NULL,
    tenant_id              text             NOT NULL,
    application_id         text             NOT NULL,
    project_id             text             NOT NULL,
    team_id                text             NULL,
    agent_id               text             NULL,
    user_id                text             NULL,
    task_id                text             NOT NULL,
    status                 text             NOT NULL,
    reuse_confidence       double precision NOT NULL,
    supporting_validations integer          NOT NULL,
    contradictions         integer          NOT NULL,
    revision               bigint           NOT NULL,
    created_at             timestamptz      NOT NULL,
    updated_at             timestamptz      NOT NULL,
    payload_version        integer          NOT NULL,
    payload                jsonb            NOT NULL,
    CONSTRAINT experience_records_pkey PRIMARY KEY (experience_id),
    CONSTRAINT experience_records_experience_id_not_empty CHECK (experience_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_records_tenant_id_not_blank CHECK (tenant_id ~ '[^[:space:]]'),
    CONSTRAINT experience_records_application_id_not_blank CHECK (application_id ~ '[^[:space:]]'),
    CONSTRAINT experience_records_project_id_not_blank CHECK (project_id ~ '[^[:space:]]'),
    CONSTRAINT experience_records_team_id_not_blank CHECK (team_id IS NULL OR team_id ~ '[^[:space:]]'),
    CONSTRAINT experience_records_agent_id_not_blank CHECK (agent_id IS NULL OR agent_id ~ '[^[:space:]]'),
    CONSTRAINT experience_records_user_id_not_blank CHECK (user_id IS NULL OR user_id ~ '[^[:space:]]'),
    CONSTRAINT experience_records_task_id_not_blank CHECK (task_id ~ '[^[:space:]]'),
    CONSTRAINT experience_records_reuse_confidence_range CHECK (reuse_confidence >= 0 AND reuse_confidence <= 1),
    CONSTRAINT experience_records_supporting_validations_nonnegative CHECK (supporting_validations >= 0),
    CONSTRAINT experience_records_contradictions_nonnegative CHECK (contradictions >= 0),
    CONSTRAINT experience_records_revision_nonnegative CHECK (revision >= 0),
    CONSTRAINT experience_records_payload_version_positive CHECK (payload_version > 0)
);

CREATE INDEX IF NOT EXISTS ix_experience_records_scope
    ON agent_experience.experience_records (tenant_id, application_id, project_id);
