-- AgentExperience.NET SQLite store, schema version 1 (story 28.7).
-- Applied by ExperienceSqliteSchema.MigrateAsync and recorded in agent_experience_schema_versions, inside one
-- transaction with that journal row, so a database is either at version 1 or not at all. Do not edit this script once a
-- database anywhere holds it; add the next-numbered script instead.
--
-- One file, one process family: there are no database roles, no row-level security and no grants. Every scope rule is
-- applied by the store's own statements, exactly as the PostgreSQL store's predicates apply it. Timestamps are UTC ticks
-- (100 ns since 0001-01-01), truncated to whole microseconds as PostgreSQL's timestamptz keeps them. Identifiers are
-- GUIDs in their "D" text form.

-- The records. Scope, status, counters, revision and timestamps live in their own columns, everything else in payload:
-- the PostgreSQL store's version-1 JSON (ExperiencePayload), byte for byte the same shape. Erasure leaves a tombstone:
-- deleted_at set, payload and task_id cleared, and every row naming the record deleted.
CREATE TABLE experience_records (
    experience_id          TEXT    NOT NULL PRIMARY KEY,
    source_run_id          TEXT    NOT NULL,
    tenant_id              TEXT    NOT NULL,
    application_id         TEXT    NOT NULL,
    project_id             TEXT    NOT NULL,
    team_id                TEXT,
    agent_id               TEXT,
    user_id                TEXT,
    task_id                TEXT,
    status                 TEXT    NOT NULL,
    reuse_confidence       REAL    NOT NULL,
    supporting_validations INTEGER NOT NULL,
    contradictions         INTEGER NOT NULL,
    revision               INTEGER NOT NULL,
    created_at             INTEGER NOT NULL,
    updated_at             INTEGER NOT NULL,
    model_authored         INTEGER NOT NULL DEFAULT 0,
    payload_version        INTEGER,
    payload                TEXT,
    deleted_at             INTEGER,
    CHECK ((deleted_at IS NULL) = (payload IS NOT NULL))
);

CREATE INDEX experience_records_scope_created
    ON experience_records (tenant_id, application_id, project_id, created_at DESC, experience_id);

CREATE INDEX experience_records_source_run
    ON experience_records (source_run_id);

-- The lifecycle log: one row per committed event, the event itself as JSON (its identity, compared on replay), with the
-- facts only the store knows beside it. Append-only by the store's own code; erasure deletes a record's rows.
CREATE TABLE experience_lifecycle_events (
    event_id                  TEXT    NOT NULL PRIMARY KEY,
    experience_id             TEXT    NOT NULL REFERENCES experience_records (experience_id),
    tenant_id                 TEXT    NOT NULL,
    application_id            TEXT    NOT NULL,
    project_id                TEXT    NOT NULL,
    team_id                   TEXT,
    agent_id                  TEXT,
    user_id                   TEXT,
    applied_revision          INTEGER NOT NULL,
    recorded_at               INTEGER NOT NULL,
    actor                     TEXT,
    replacement_experience_id TEXT,
    event                     TEXT    NOT NULL,
    UNIQUE (experience_id, applied_revision)
);

-- The confidence ledger: every piece of evidence, counted or recorded only, keyed on its evidence ID.
CREATE TABLE experience_confidence_evidence (
    evidence_id       TEXT    NOT NULL PRIMARY KEY,
    experience_id     TEXT    NOT NULL REFERENCES experience_records (experience_id),
    event_id          TEXT,
    independence_key  TEXT    NOT NULL,
    counted           INTEGER NOT NULL,
    assessment_id     TEXT,
    applied_revision  INTEGER NOT NULL,
    applied_status    TEXT    NOT NULL,
    evidence          TEXT    NOT NULL
);

CREATE INDEX experience_confidence_evidence_key
    ON experience_confidence_evidence (experience_id, independence_key);

CREATE UNIQUE INDEX experience_confidence_evidence_assessment
    ON experience_confidence_evidence (experience_id, assessment_id) WHERE assessment_id IS NOT NULL;

-- Full-text search over each record's task ID, task summary and lesson (at most 100,000 characters across the three,
-- the PostgreSQL search column's bound), with FTS5's Porter stemmer over its Unicode tokenizer. Keyed by the record's
-- rowid; erasure deletes the row.
CREATE VIRTUAL TABLE experience_search USING fts5 (
    task_id,
    summary,
    lesson,
    tokenize = 'porter unicode61 remove_diacritics 0'
);

CREATE TABLE experience_search_rows (
    experience_id TEXT    NOT NULL PRIMARY KEY REFERENCES experience_records (experience_id),
    search_rowid  INTEGER NOT NULL UNIQUE
);

-- Each record's failure signatures (story 28.2), derived once when it is created.
CREATE TABLE experience_failure_signatures (
    experience_id  TEXT    NOT NULL REFERENCES experience_records (experience_id),
    signature_key  TEXT    NOT NULL,
    tool_name      TEXT    NOT NULL,
    error_class    TEXT    NOT NULL,
    fingerprint    TEXT    NOT NULL,
    resolved       INTEGER NOT NULL,
    PRIMARY KEY (experience_id, signature_key)
);

CREATE INDEX experience_failure_signatures_key ON experience_failure_signatures (signature_key);
CREATE INDEX experience_failure_signatures_tool_class ON experience_failure_signatures (tool_name, error_class);

-- Each record's last injection in its own scope, and how many were recorded.
CREATE TABLE experience_injections (
    experience_id    TEXT    NOT NULL PRIMARY KEY REFERENCES experience_records (experience_id),
    last_injected_at INTEGER NOT NULL,
    injection_count  INTEGER NOT NULL
);

-- The injection holdout: which arm a run was assigned to, per record its block named.
CREATE TABLE experience_holdout_assignments (
    run_id         TEXT    NOT NULL,
    experience_id  TEXT    NOT NULL REFERENCES experience_records (experience_id),
    revision       INTEGER NOT NULL,
    arm            TEXT    NOT NULL,
    tenant_id      TEXT    NOT NULL,
    application_id TEXT    NOT NULL,
    project_id     TEXT    NOT NULL,
    team_id        TEXT,
    agent_id       TEXT,
    user_id        TEXT,
    assigned_at    INTEGER NOT NULL,
    PRIMARY KEY (run_id, experience_id)
);

-- Another memory provider's holdout: which arm a run was assigned to, per provider key.
CREATE TABLE experience_provider_holdout_assignments (
    tenant_id      TEXT    NOT NULL,
    run_id         TEXT    NOT NULL,
    provider_key   TEXT    NOT NULL,
    arm            TEXT    NOT NULL,
    application_id TEXT    NOT NULL,
    project_id     TEXT    NOT NULL,
    team_id        TEXT,
    agent_id       TEXT,
    user_id        TEXT,
    assigned_at    INTEGER NOT NULL,
    PRIMARY KEY (tenant_id, run_id, provider_key)
);

-- The reuse-feedback ledger, keyed on its feedback ID; the submission as stored is JSON.
CREATE TABLE experience_reuse_feedback (
    feedback_id    TEXT NOT NULL PRIMARY KEY,
    tenant_id      TEXT NOT NULL,
    application_id TEXT NOT NULL,
    project_id     TEXT NOT NULL,
    team_id        TEXT,
    agent_id       TEXT,
    user_id        TEXT,
    feedback       TEXT NOT NULL
);
