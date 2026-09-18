-- Test-only script: proves scripts applied before a failure stay applied and journaled.
CREATE TABLE IF NOT EXISTS agent_experience.migration_marker (
    id integer NOT NULL
);
