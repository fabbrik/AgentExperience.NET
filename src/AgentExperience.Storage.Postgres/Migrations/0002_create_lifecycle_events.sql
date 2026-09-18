-- AgentExperience.NET: append-only lifecycle event log for Experience Records.
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is IF NOT EXISTS on purpose, matching 0001, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead. (This script is still unreleased and has only ever been applied to
-- throwaway test databases, so the unique index below was corrected in place during review; once this
-- branch ships, the append-only rule applies to it as it does to 0001.)
--
-- Each row is one committed transition. The store writes a row and the matching experience_records
-- projection update in a single transaction, so the log and the projection can never disagree. Rows are
-- never updated or deleted: event_id is the idempotency key a replay is compared against, and
-- applied_revision (always expected_revision + 1) is the record revision this event produced.
--
-- There is deliberately no foreign key to experience_records: a commit for a record that does not exist
-- in the request scope is rolled back by the store's own revision-checked projection update, and a
-- foreign-key violation would report it as an infrastructure failure instead of a NotFound outcome.

CREATE TABLE IF NOT EXISTS agent_experience.lifecycle_events (
    event_id          uuid        NOT NULL,
    experience_id     uuid        NOT NULL,
    tenant_id         text        NOT NULL,
    application_id    text        NOT NULL,
    project_id        text        NOT NULL,
    team_id           text        NULL,
    agent_id          text        NULL,
    user_id           text        NULL,
    prior_status      text        NULL,
    current_status    text        NOT NULL,
    reason            text        NOT NULL,
    producer          text        NOT NULL,
    occurred_at       timestamptz NOT NULL,
    recorded_at       timestamptz NOT NULL,
    expected_revision bigint      NOT NULL,
    applied_revision  bigint      NOT NULL,
    CONSTRAINT lifecycle_events_pkey PRIMARY KEY (event_id),
    CONSTRAINT lifecycle_events_event_id_not_empty CHECK (event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT lifecycle_events_experience_id_not_empty CHECK (experience_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT lifecycle_events_tenant_id_not_blank CHECK (tenant_id ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_application_id_not_blank CHECK (application_id ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_project_id_not_blank CHECK (project_id ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_team_id_not_blank CHECK (team_id IS NULL OR team_id ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_agent_id_not_blank CHECK (agent_id IS NULL OR agent_id ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_user_id_not_blank CHECK (user_id IS NULL OR user_id ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_prior_status_not_blank CHECK (prior_status IS NULL OR prior_status ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_current_status_not_blank CHECK (current_status ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_reason_not_blank CHECK (reason ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_producer_not_blank CHECK (producer ~ '[^[:space:]]'),
    CONSTRAINT lifecycle_events_expected_revision_nonnegative CHECK (expected_revision >= 0),
    CONSTRAINT lifecycle_events_applied_revision_follows_expected CHECK (applied_revision = expected_revision + 1)
);

-- Unique, not merely an index: exactly one event may claim a given revision of a record, so the log can
-- never desynchronize from the projection even if a second writer bypasses the store. Two racing commits
-- from the same revision collide here, and the store reports the loser as a stale revision.
CREATE UNIQUE INDEX IF NOT EXISTS ix_lifecycle_events_record_revision
    ON agent_experience.lifecycle_events (experience_id, applied_revision);
