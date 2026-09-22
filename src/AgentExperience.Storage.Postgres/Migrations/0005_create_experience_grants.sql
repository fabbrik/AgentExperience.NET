-- AgentExperience.NET: explicit, audited sharing grants over individual Experience Records.
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is IF NOT EXISTS on purpose, matching 0001 and 0002, so a database whose schema was
-- applied by hand can still be journaled. Do not edit this script once it has been journaled anywhere;
-- add the next-numbered script instead.
--
-- A grant names exactly one record and permits exactly one recipient scope to READ it, until it
-- expires or is revoked. It relaxes only the optional scope fields (team, agent, user): the
-- same-boundary CHECK below makes a grant that changes tenant, application, or project unstorable, so
-- the rule survives even a writer that bypasses the store. The grant is written together with its
-- audit event in one transaction, and revocation appends another event rather than deleting anything.
--
-- There is deliberately no foreign key to experience_records, matching 0002: a grant naming a record
-- that does not exist in the request scope is rejected by the store's own INSERT ... SELECT, which
-- reads the record row and therefore writes nothing when there is none. A foreign-key violation would
-- report that as an infrastructure failure instead of a typed NotFound outcome.
--
-- The owner scope columns are copied from the record row inside that INSERT ... SELECT, never taken
-- from caller input, so a grant's stored owner scope can never disagree with the record it names.
--
-- (This script is still unreleased and has only ever been applied to throwaway test databases, so it
-- was corrected in place during review, exactly as 0002 was; once this branch ships, the append-only
-- rule applies to it as it does to 0001.)

CREATE TABLE IF NOT EXISTS agent_experience.experience_grants (
    grant_id                  uuid        NOT NULL,
    experience_id             uuid        NOT NULL,
    tenant_id                 text        NOT NULL,
    application_id            text        NOT NULL,
    project_id                text        NOT NULL,
    team_id                   text        NULL,
    agent_id                  text        NULL,
    user_id                   text        NULL,
    recipient_tenant_id       text        NOT NULL,
    recipient_application_id  text        NOT NULL,
    recipient_project_id      text        NOT NULL,
    recipient_team_id         text        NULL,
    recipient_agent_id        text        NULL,
    recipient_user_id         text        NULL,
    reason                    text        NOT NULL,
    administrator_principal_id text       NOT NULL,
    issued_at                 timestamptz NOT NULL,
    expires_at                timestamptz NOT NULL,
    revoked_at                timestamptz NULL,
    revocation_reason         text        NULL,
    CONSTRAINT experience_grants_pkey PRIMARY KEY (grant_id),
    CONSTRAINT experience_grants_grant_id_not_empty CHECK (grant_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grants_experience_id_not_empty CHECK (experience_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grants_tenant_id_not_blank CHECK (tenant_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_application_id_not_blank CHECK (application_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_project_id_not_blank CHECK (project_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_team_id_not_blank CHECK (team_id IS NULL OR team_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_agent_id_not_blank CHECK (agent_id IS NULL OR agent_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_user_id_not_blank CHECK (user_id IS NULL OR user_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_recipient_team_id_not_blank CHECK (recipient_team_id IS NULL OR recipient_team_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_recipient_agent_id_not_blank CHECK (recipient_agent_id IS NULL OR recipient_agent_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_recipient_user_id_not_blank CHECK (recipient_user_id IS NULL OR recipient_user_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_reason_not_blank CHECK (reason ~ '[^[:space:]]'),
    CONSTRAINT experience_grants_administrator_not_blank CHECK (administrator_principal_id ~ '[^[:space:]]'),
    -- The boundary a grant can never cross. Stated here, not only in application code, so no writer can
    -- store a grant that would let a read leave its tenant, application, or project.
    CONSTRAINT experience_grants_same_boundary CHECK (
        recipient_tenant_id = tenant_id
        AND recipient_application_id = application_id
        AND recipient_project_id = project_id),
    -- issued_at is the database's own clock, so a grant that was already expired when it was issued is
    -- rejected here rather than stored as a grant that never permitted anything.
    CONSTRAINT experience_grants_expires_after_issue CHECK (expires_at > issued_at),
    -- A revoked grant always carries why. Nothing is ever deleted, so this is the only record of it.
    CONSTRAINT experience_grants_revocation_reason_present CHECK ((revoked_at IS NULL) = (revocation_reason IS NULL)),
    CONSTRAINT experience_grants_revocation_reason_not_blank CHECK (revocation_reason IS NULL OR revocation_reason ~ '[^[:space:]]'),
    -- A grant to the scope that already owns the record permits nothing; storing one would leave a
    -- real audit row claiming access was given when none was.
    CONSTRAINT experience_grants_recipient_differs CHECK (
        recipient_team_id IS DISTINCT FROM team_id
        OR recipient_agent_id IS DISTINCT FROM agent_id
        OR recipient_user_id IS DISTINCT FROM user_id)
);

-- At most one ACTIVE grant per (record, recipient scope). Without it, overlapping grants to the same
-- recipient would each keep access alive on their own, so revoking the one an administrator knows
-- about would silently fail to end anything. NULLS NOT DISTINCT because a null optional scope field
-- is an exact value here, not a wildcard: two grants to the same project-level recipient are the same
-- recipient. Re-issuing while one is active is reported as a conflict rather than stacked.
CREATE UNIQUE INDEX IF NOT EXISTS ux_experience_grants_active_recipient
    ON agent_experience.experience_grants
    (experience_id, recipient_tenant_id, recipient_application_id, recipient_project_id,
     recipient_team_id, recipient_agent_id, recipient_user_id)
    NULLS NOT DISTINCT
    WHERE revoked_at IS NULL;

-- The read predicate's own index: partial on the grants that can still permit anything, and carrying
-- every column that predicate filters on -- the record, the recipient scope in full, the owner scope,
-- and the expiry it compares against clock_timestamp().
CREATE INDEX IF NOT EXISTS ix_experience_grants_active
    ON agent_experience.experience_grants
    (experience_id, recipient_tenant_id, recipient_application_id, recipient_project_id,
     recipient_team_id, recipient_agent_id, recipient_user_id, expires_at,
     tenant_id, application_id, project_id, team_id, agent_id, user_id)
    WHERE revoked_at IS NULL;

-- Serves listing the grants over one record from its owner scope.
CREATE INDEX IF NOT EXISTS ix_experience_grants_record
    ON agent_experience.experience_grants
    (tenant_id, application_id, project_id, experience_id);

-- The append-only audit log, following 0002's shape: rows are never updated or deleted, so issuing and
-- then revoking a grant leaves two rows and revocation can never erase the fact that access was given.
CREATE TABLE IF NOT EXISTS agent_experience.experience_grant_events (
    event_id                  uuid        NOT NULL,
    grant_id                  uuid        NOT NULL,
    experience_id             uuid        NOT NULL,
    action                    text        NOT NULL,
    tenant_id                 text        NOT NULL,
    application_id            text        NOT NULL,
    project_id                text        NOT NULL,
    team_id                   text        NULL,
    agent_id                  text        NULL,
    user_id                   text        NULL,
    recipient_tenant_id       text        NOT NULL,
    recipient_application_id  text        NOT NULL,
    recipient_project_id      text        NOT NULL,
    recipient_team_id         text        NULL,
    recipient_agent_id        text        NULL,
    recipient_user_id         text        NULL,
    reason                    text        NOT NULL,
    administrator_principal_id text       NOT NULL,
    administrator_authorized_at timestamptz NOT NULL,
    expires_at                timestamptz NOT NULL,
    occurred_at               timestamptz NOT NULL,
    recorded_at               timestamptz NOT NULL,
    CONSTRAINT experience_grant_events_pkey PRIMARY KEY (event_id),
    CONSTRAINT experience_grant_events_event_id_not_empty CHECK (event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grant_events_grant_id_not_empty CHECK (grant_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grant_events_experience_id_not_empty CHECK (experience_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grant_events_action_known CHECK (action IN ('Issued', 'Revoked')),
    CONSTRAINT experience_grant_events_tenant_id_not_blank CHECK (tenant_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_application_id_not_blank CHECK (application_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_project_id_not_blank CHECK (project_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_team_id_not_blank CHECK (team_id IS NULL OR team_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_agent_id_not_blank CHECK (agent_id IS NULL OR agent_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_user_id_not_blank CHECK (user_id IS NULL OR user_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_recipient_tenant_id_not_blank CHECK (recipient_tenant_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_recipient_application_id_not_blank CHECK (recipient_application_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_recipient_project_id_not_blank CHECK (recipient_project_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_recipient_team_id_not_blank CHECK (recipient_team_id IS NULL OR recipient_team_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_recipient_agent_id_not_blank CHECK (recipient_agent_id IS NULL OR recipient_agent_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_recipient_user_id_not_blank CHECK (recipient_user_id IS NULL OR recipient_user_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_reason_not_blank CHECK (reason ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_events_administrator_not_blank CHECK (administrator_principal_id ~ '[^[:space:]]')
);

-- One grant's trail, oldest first: the grant-history read.
CREATE INDEX IF NOT EXISTS ix_experience_grant_events_grant
    ON agent_experience.experience_grant_events (grant_id, recorded_at);

-- The other audit question: everything ever allowed over one record, and everything one scope ever
-- administered.
CREATE INDEX IF NOT EXISTS ix_experience_grant_events_experience
    ON agent_experience.experience_grant_events (experience_id, recorded_at);

CREATE INDEX IF NOT EXISTS ix_experience_grant_events_scope
    ON agent_experience.experience_grant_events (tenant_id, application_id, project_id, recorded_at);
