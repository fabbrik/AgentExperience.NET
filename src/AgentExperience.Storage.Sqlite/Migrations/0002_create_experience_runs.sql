-- AgentExperience.NET SQLite store, schema version 2 (story 28.7): the run store.
-- Applied by ExperienceSqliteSchema.MigrateAsync, in one transaction with its journal row. Do not edit this script once
-- a database anywhere holds it; add the next-numbered script instead.
--
-- One row per completed run, so a run captured in one process can be finalized in a later one. The payload is the run
-- as capture held it once completed -- already sanitized -- in the PostgreSQL run store's version-1 JSON
-- (ExperienceRunPayload). Run IDs are unique across every scope. finalized_at is set once the run became a record;
-- the host deletes a run after finalization or a retention of its choosing.
--
-- Erasing a record turns its run's row into a tombstone (erased_at set, everything but the ID and scope cleared), or
-- writes one when there was no row, when that was the run's last live record in the tenant: a capture retry can never
-- save the erased run again, and the store reads a tombstone as no run at all.
CREATE TABLE experience_runs (
    run_id              TEXT    NOT NULL PRIMARY KEY,
    tenant_id           TEXT    NOT NULL,
    application_id      TEXT    NOT NULL,
    project_id          TEXT    NOT NULL,
    team_id             TEXT,
    agent_id            TEXT,
    user_id             TEXT,
    completion_event_id TEXT,
    execution_status    TEXT,
    ended_at            INTEGER,
    saved_at            INTEGER,
    finalized_at        INTEGER,
    payload_version     INTEGER,
    payload             TEXT,
    erased_at           INTEGER,
    CHECK ((erased_at IS NULL) = (payload IS NOT NULL)),
    CHECK (erased_at IS NOT NULL OR (completion_event_id IS NOT NULL AND execution_status IS NOT NULL
        AND ended_at IS NOT NULL AND saved_at IS NOT NULL AND payload_version IS NOT NULL))
);

CREATE INDEX experience_runs_unfinalized
    ON experience_runs (tenant_id, application_id, project_id, run_id) WHERE finalized_at IS NULL AND erased_at IS NULL;
