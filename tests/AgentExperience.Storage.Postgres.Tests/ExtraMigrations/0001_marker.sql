-- Test-only script: the already-journaled script a later run must not reapply.
CREATE TABLE IF NOT EXISTS agent_experience.extra_marker (
    id integer NOT NULL
);
