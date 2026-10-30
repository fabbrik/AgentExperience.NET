-- AgentExperience.NET SQLite store, schema version 2 (story 28.7): the run store.
-- Applied by ExperienceSqliteSchema.MigrateAsync, in one transaction with its journal row. Do not edit this script once
-- a database anywhere holds it; add the next-numbered script instead.
--
-- One row per completed run, so a run captured in one process can be finalized in a later one. The payload is the run
-- as capture held it once completed -- already sanitized -- in the PostgreSQL run store's version-1 JSON
-- (ExperienceRunPayload). Run IDs are unique across every scope. finalized_at is set once the run became a record;
-- the host deletes a run after finalization or a retention of its choosing. Erasing a record deletes its run's row too
-- when that was the run's last live record in the tenant.
CREATE TABLE experience_runs (
    run_id              TEXT    NOT NULL PRIMARY KEY,
    tenant_id           TEXT    NOT NULL,
    application_id      TEXT    NOT NULL,
    project_id          TEXT    NOT NULL,
    team_id             TEXT,
    agent_id            TEXT,
    user_id             TEXT,
    completion_event_id TEXT    NOT NULL,
    execution_status    TEXT    NOT NULL,
    ended_at            INTEGER NOT NULL,
    saved_at            INTEGER NOT NULL,
    finalized_at        INTEGER,
    payload_version     INTEGER NOT NULL,
    payload             TEXT    NOT NULL
);

CREATE INDEX experience_runs_unfinalized
    ON experience_runs (tenant_id, application_id, project_id, run_id) WHERE finalized_at IS NULL;
