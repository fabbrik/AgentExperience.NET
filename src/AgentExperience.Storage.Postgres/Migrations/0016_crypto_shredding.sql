-- AgentExperience.NET: crypto-shredding -- sealed payloads, sealed search, and the sealing function (Story 6.4, KL-2).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0015, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT KL-2 WAS. Erasure (0010) reaches this database's live rows only. Backups, replicas, WAL, and the dead
-- heap tuple an UPDATE or DELETE leaves behind all keep the erased text, and nothing this schema does can
-- reach them.
--
-- WHAT CLOSES IT IS MOSTLY NOT IN THIS SCRIPT. It is a deployment mode: with an ExperienceEncryption
-- configured, the adapter stores every free-text column erasure removes as AES-256-GCM ciphertext under a
-- per-record data key held by a key store OUTSIDE this database's backup domain, and erasure destroys the key
-- before it writes the tombstone. Every copy of the ciphertext -- live, dead, archived, replicated -- is then
-- unreadable. The encryption happens in the application; this database only ever sees ciphertext, and holds
-- no key. This script adds exactly what the database side of that needs, and changes nothing for a
-- deployment that stays in plaintext mode.
--
-- THE SEALED RECORD SHAPE. A sealed record is payload_version 2: its payload is the one-property object
-- {"sealed": "aexp-sealed:v1:<base64>"}, and task_id is the placeholder '(sealed)' -- the real task ID is
-- sealed inside the payload with everything else. A plaintext-mode reader meeting one fails loudly on the
-- unknown payload_version rather than misreading it. 0010's tombstone is unchanged: the purge still writes
-- payload '{}' and task_id '(deleted)', and keeps payload_version.
--
-- SEARCH, AND THE ONE RESIDUAL. PostgreSQL has to read a text-search vector in the clear to search it. So a
-- sealed record keeps a derived full-text vector, search_vector_sealed, computed from exactly the expression
-- 0003 uses for search_vector (task ID, task summary, lesson, bounded to 100000 characters), so ranking is
-- identical in both modes. It holds stemmed words and their positions, not the text -- but that IS derived
-- from the text, it is not encrypted, and it survives in backups, WAL and dead tuples exactly as plaintext
-- does. Erasure removes it from the live row (the trigger below). The generated search_vector of a sealed row
-- holds only the placeholder. The vectors package's embeddings are the same kind of residual.
--
-- WHY A FUNCTION FOR THE UPGRADE, AND NOT A DATA MIGRATION. Existing plaintext rows are sealed by
-- PostgresExperienceRecordStore.SealPlaintextRecordsAsync: bounded, resumable, authorized, one record per
-- transaction, run by the host when it chooses. Sealing needs the key store, which this script cannot reach,
-- and a journaled script that rewrote every record would be an unbounded write outage besides.
-- seal_experience_record is the only way that job changes a row, and it admits exactly one transition: a
-- live plaintext row, at the revision the caller read, into its sealed shape. The application role holds no
-- UPDATE on payload or task_id (6.1), and reaches this function only when the host sets
-- ExperienceApplicationRoleOptions.AllowSealing.
--
-- WHAT THIS SCRIPT DOES NOT SEAL. The append-only ledgers -- lifecycle_events, confidence_evidence,
-- reuse_feedback, experience_grant_events -- and experience_grants. Rows written to them in encrypted mode
-- are sealed by the adapter as they are written. Rows written before a deployment switched modes stay
-- plaintext: this library will not open an UPDATE path on its audit trail. Erasure still deletes them from
-- the live tables, exactly as before.
--
-- THE TWO INDEXES ON A LARGE, HAND-APPLIED DATABASE. Both are partial and small once built, but CREATE INDEX
-- scans the whole table under a SHARE lock inside the migrator's per-script transaction, which blocks writes
-- to experience_records for the duration. A deployment that cannot take that should build both out of band
-- first -- CONCURRENTLY cannot run inside a transaction, and IF NOT EXISTS makes this script's own statements
-- no-ops afterwards. Add the column first (ADD COLUMN ... NULL rewrites nothing):
--
--     ALTER TABLE agent_experience.experience_records ADD COLUMN IF NOT EXISTS search_vector_sealed tsvector NULL;
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_records_search_sealed
--         ON agent_experience.experience_records USING GIN (search_vector_sealed)
--         WHERE search_vector_sealed IS NOT NULL;
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_records_unsealed
--         ON agent_experience.experience_records (tenant_id, application_id, project_id, created_at, experience_id)
--         WHERE deleted_at IS NULL AND payload_version = 1;
--
-- THE CHECKS ARE NOT VALID, like 0006's, 0007's and 0010's: every existing row satisfies them (the new
-- columns are NULL and every existing payload is version 1), but validating would scan the table at startup.
-- They bind every row written or updated from now on. Validate at a time of your choosing; VALIDATE takes only
-- a SHARE UPDATE EXCLUSIVE lock:
--
--     ALTER TABLE agent_experience.experience_records VALIDATE CONSTRAINT experience_records_sealed_shape;
--     ALTER TABLE agent_experience.experience_records VALIDATE CONSTRAINT experience_records_sealed_search_only_when_sealed;
--     ALTER TABLE agent_experience.reuse_feedback_exposures VALIDATE CONSTRAINT reuse_feedback_exposures_rationale_sealed_format;

ALTER TABLE agent_experience.experience_records
    ADD COLUMN IF NOT EXISTS search_vector_sealed tsvector NULL;

ALTER TABLE agent_experience.reuse_feedback_exposures
    ADD COLUMN IF NOT EXISTS rationale_sealed text NULL;

-- The sealed rows' text search. Partial, so a plaintext deployment's index stays empty.
CREATE INDEX IF NOT EXISTS ix_experience_records_search_sealed
    ON agent_experience.experience_records USING GIN (search_vector_sealed)
    WHERE search_vector_sealed IS NOT NULL;

-- The upgrade job's worklist: live plaintext records, oldest first, per project. Partial, so it empties as the
-- job runs and a re-run starts where the last one stopped without walking the rows already sealed.
CREATE INDEX IF NOT EXISTS ix_experience_records_unsealed
    ON agent_experience.experience_records (tenant_id, application_id, project_id, created_at, experience_id)
    WHERE deleted_at IS NULL AND payload_version = 1;

DO $body$
BEGIN
    -- A live sealed row carries nothing in the clear but its sealed search vector: the payload is exactly the
    -- one sealed property, and task_id is the placeholder. Without this a writer could label a plaintext row
    -- version 2, or leave the task ID in the clear next to a sealed payload.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_records_sealed_shape'
          AND conrelid = 'agent_experience.experience_records'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_records
            ADD CONSTRAINT experience_records_sealed_shape
            -- IS TRUE, so a NULL in any term (a missing property, say) fails the check rather than passing it.
            CHECK (
                deleted_at IS NOT NULL
                OR payload_version <> 2
                OR (task_id = '(sealed)'
                    AND search_vector_sealed IS NOT NULL
                    AND jsonb_typeof(payload -> 'sealed') = 'string'
                    AND (payload - 'sealed') = '{}'::jsonb
                    AND left(payload ->> 'sealed', 15) = 'aexp-sealed:v1:') IS TRUE)
            NOT VALID;
    END IF;

    -- The sealed vector belongs to a live sealed row and nothing else: a plaintext row has its generated one,
    -- and a tombstone has none (the trigger below clears it on erasure).
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_records_sealed_search_only_when_sealed'
          AND conrelid = 'agent_experience.experience_records'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_records
            ADD CONSTRAINT experience_records_sealed_search_only_when_sealed
            CHECK (search_vector_sealed IS NULL OR (payload_version = 2 AND deleted_at IS NULL))
            NOT VALID;
    END IF;

    -- A reuse-feedback rationale sealed to one exposed record's key. Only ever the sealed format: the column
    -- exists so the rationale is never written here in the clear.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'reuse_feedback_exposures_rationale_sealed_format'
          AND conrelid = 'agent_experience.reuse_feedback_exposures'::regclass)
    THEN
        ALTER TABLE agent_experience.reuse_feedback_exposures
            ADD CONSTRAINT reuse_feedback_exposures_rationale_sealed_format
            CHECK (rationale_sealed IS NULL OR left(rationale_sealed, 15) = 'aexp-sealed:v1:')
            NOT VALID;
    END IF;
END
$body$;

-- The tombstone transition of a sealed record, guarded and cleaned, so 0010's purge_experience_record does
-- not have to be restated. It fires on exactly one transition, the tombstone's: 0010's projection guard refuses
-- every UPDATE of a tombstone afterwards.
--   * A sealed row (payload_version 2) is tombstoned only by a transaction that has declared, with
--     SET LOCAL agent_experience.erasure_destroys_key = 'on', that it destroys the record's key before it commits.
--     The encrypted-mode adapter does; a process that forgot its ExperienceEncryption does not, and is refused
--     loudly rather than reporting Deleted while the key -- and so every copy of the ciphertext -- survives.
--     Like 0010's marker this is a guard against a mistake, not a privilege boundary: any session can set it.
--   * The sealed search vector is cleared. That only ever sets a column to NULL, so it can widen nothing.
CREATE OR REPLACE FUNCTION agent_experience.guard_sealed_record_erasure() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
BEGIN
    IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL THEN
        IF OLD.payload_version = 2
            AND coalesce(pg_catalog.current_setting('agent_experience.erasure_destroys_key', true), 'off') <> 'on'
        THEN
            RAISE EXCEPTION
                'A sealed Experience Record is erased only by a transaction that destroys its key: configure '
                'ExperienceEncryption on the store that deletes it.'
                USING ERRCODE = 'insufficient_privilege';
        END IF;

        NEW.search_vector_sealed := NULL;
    END IF;

    RETURN NEW;
END;
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_sealed_search_cleared'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_sealed_search_cleared
            BEFORE UPDATE ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.guard_sealed_record_erasure();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_sealed_search_cleared;

-- The upgrade job's one write. SECURITY DEFINER, because the application role holds no UPDATE on payload or
-- task_id and must not: it admits exactly one transition and nothing else.
--   * the row is live, in exactly this scope, still plaintext (payload_version 1), and at p_expected_revision;
--   * p_sealed_payload is exactly the sealed envelope;
--   * the sealed search vector is the row's OWN generated search_vector, read in the same statement before it
--     regenerates from the placeholder -- so the job never sends the text back, and the vector is exactly the
--     one plaintext search ranked on.
-- The revision does not move: the record's content is unchanged, an embedding computed at this revision stays
-- valid, and a lifecycle commit racing the job is serialized by the row lock and still wins or loses on its
-- own revision check. The function cannot verify that the ciphertext opens to the row's plaintext -- it holds
-- no key -- so the job opens its own seal and compares before it calls this.
CREATE OR REPLACE FUNCTION agent_experience.seal_experience_record(
    p_experience_id uuid,
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_expected_revision bigint,
    p_sealed_payload jsonb)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
DECLARE
    v_revision bigint;
    v_deleted_at timestamptz;
    v_payload_version integer;
BEGIN
    IF p_sealed_payload IS NULL
        OR jsonb_typeof(p_sealed_payload -> 'sealed') IS DISTINCT FROM 'string'
        OR (p_sealed_payload - 'sealed') <> '{}'::jsonb
        OR left(p_sealed_payload ->> 'sealed', 15) IS DISTINCT FROM 'aexp-sealed:v1:'
        OR p_expected_revision IS NULL
    THEN
        RETURN 'Invalid';
    END IF;

    SELECT r.revision, r.deleted_at, r.payload_version INTO v_revision, v_deleted_at, v_payload_version
    FROM agent_experience.experience_records r
    WHERE r.experience_id = p_experience_id
      AND r.tenant_id = p_tenant_id
      AND r.application_id = p_application_id
      AND r.project_id = p_project_id
      AND r.team_id IS NOT DISTINCT FROM p_team_id
      AND r.agent_id IS NOT DISTINCT FROM p_agent_id
      AND r.user_id IS NOT DISTINCT FROM p_user_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RETURN 'NotFound';
    END IF;

    IF v_deleted_at IS NOT NULL THEN
        RETURN 'Deleted';
    END IF;

    IF v_payload_version = 2 THEN
        RETURN 'AlreadySealed';
    END IF;

    IF v_payload_version <> 1 THEN
        RETURN 'Invalid';
    END IF;

    IF v_revision <> p_expected_revision THEN
        RETURN 'StaleRevision';
    END IF;

    UPDATE agent_experience.experience_records r
    SET payload = p_sealed_payload,
        payload_version = 2,
        task_id = '(sealed)',
        search_vector_sealed = r.search_vector
    WHERE r.experience_id = p_experience_id;

    RETURN 'Sealed';
END
$body$;

REVOKE ALL ON FUNCTION agent_experience.seal_experience_record(
    uuid, text, text, text, text, text, text, bigint, jsonb) FROM PUBLIC;
