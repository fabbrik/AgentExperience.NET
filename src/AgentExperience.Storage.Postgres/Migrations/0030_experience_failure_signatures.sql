-- AgentExperience.NET: the failure signatures each record holds (Story 28.2).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0029, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. A failure signature is (tool name, error class, excerpt fingerprint) for one failed tool call, derived
-- deterministically in .NET (ExperienceFailureSignature) from what the record's payload already holds. The store writes
-- a record's signatures here, in the same transaction as the record, so PostgresExperienceSignatureSource can find the
-- records that hold a failure a running agent just met, by the whole key or by its tool and class.
--
-- agent_experience.experience_failure_signatures: one row per record, signature version and signature key.
--   * signature_key is tool_name, error_class and fingerprint joined by U+001F, as ExperienceFailureSignature.Key
--     writes it (a CHECK holds the two equal). fingerprint is 32 lowercase hexadecimal characters: the first 16 bytes
--     of the SHA-256 of the error's normalized excerpt (its first line, or its last after a bare header). error_class is the class's first token. resolved says
--     whether a later call in the same verified record succeeded after the failure's last occurrence.
--   * signature_version is the derivation's version; a search matches only the version it is asked for, so a future
--     rule is written beside this one, never over it.
--   * It holds no error text: the class and a one-way fingerprint only. In encrypted mode it is stored in the clear,
--     like the other search data (the derived search vector, the authorship flag): see docs/guide/crypto-shredding.md
--     and KL-2. A record sealed by the upgrade job keeps the rows it had; a record that was already sealed before this
--     migration gains rows only when it is finalized again, because the backfill cannot read a sealed payload.
--   * The scope columns are the record's own, copied from the record by the insert (INSERT ... SELECT FROM
--     experience_records in exactly the caller's scope, live only), and the policies below require a live record with
--     exactly that ID and those scope columns, so a caller cannot create or move a row for a record in another scope.
--   * Keyed on (experience_id, signature_version, signature_key), with a foreign key to experience_records ON DELETE
--     CASCADE. The lookups lead with the tenant: (tenant_id, signature_key) for an exact match and
--     (tenant_id, tool_name, error_class) for a coarse one.
--
-- ERASURE. Every erasure path tombstones the record through 0010's purge_experience_record (one record per
-- transaction, the sweeps included), and the tombstone transition deletes the record's rows here through the trigger
-- below, so 0010's function does not have to be restated -- the pattern 0016 and 0026 use. The trigger runs as whoever
-- tombstones the record: the owner, inside the SECURITY DEFINER purge, which row-level security never binds. A record
-- row is never deleted (0010's experience_records_no_delete refuses it); the foreign key's cascade covers one removed
-- anyway, by an owner who disabled that guard.
--
-- CONCURRENCY. The insert reads its record FOR KEY SHARE, live and in exactly the caller's scope, before it writes, the
-- established pattern of every adapter write that checks a record (0010's header). The purge locks its record FOR
-- UPDATE first, which conflicts, so a backfill insert that started first holds the purge until it commits (and the
-- trigger then deletes its rows), and one that starts second waits and re-reads the record as a tombstone, writing
-- nothing. The record's own create writes its rows in its own transaction, before any purge can see the record.
--
-- PRIVILEGES. ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants the application role SELECT and
-- INSERT on the table, and no UPDATE or DELETE; it grants them only when the table exists, so running it before this
-- migration still succeeds. Until it is re-run after this migration the role cannot reach the table: a record is still
-- created, without its signatures (the store writes them under a savepoint and leaves them out on a missing table or
-- privilege), and the owner-run backfill adds them later. The table is .NET-only and is not in the contract's
-- privileges files.
--
-- ROW-LEVEL SECURITY. The policies below admit a row for reading exactly as 0019's read policy admits its record --
-- inside the operation's declared bounds, or shared by a live grant -- so a signature search sees the signatures of
-- every record it may read; and admit a written row only inside the bounds and only for a live record with that ID in
-- exactly that scope. Like 0019's, they switch nothing on: the privileges call enables row-level security on this
-- table with the others.
--
-- LOCKS. CREATE TABLE, its indexes (on an empty table) and the trigger take brief locks; CREATE TRIGGER and the foreign
-- key take a SHARE ROW EXCLUSIVE lock on experience_records for an instant. No table is rewritten.

CREATE TABLE IF NOT EXISTS agent_experience.experience_failure_signatures (
    experience_id     uuid    NOT NULL,
    signature_version integer NOT NULL,
    signature_key     text    NOT NULL,
    tool_name         text    NOT NULL,
    error_class       text    NOT NULL,
    fingerprint       text    NOT NULL,
    resolved          boolean NOT NULL,
    tenant_id         text    NOT NULL,
    application_id    text    NOT NULL,
    project_id        text    NOT NULL,
    team_id           text    NULL,
    agent_id          text    NULL,
    user_id           text    NULL,
    CONSTRAINT experience_failure_signatures_pkey PRIMARY KEY (experience_id, signature_version, signature_key),
    CONSTRAINT experience_failure_signatures_record_fkey FOREIGN KEY (experience_id)
        REFERENCES agent_experience.experience_records (experience_id) ON DELETE CASCADE,
    CONSTRAINT experience_failure_signatures_version_positive CHECK (signature_version > 0),
    CONSTRAINT experience_failure_signatures_tool_name_shape CHECK (char_length(tool_name) BETWEEN 1 AND 256),
    CONSTRAINT experience_failure_signatures_error_class_shape CHECK (char_length(error_class) BETWEEN 1 AND 64),
    CONSTRAINT experience_failure_signatures_fingerprint_shape CHECK (fingerprint ~ '^[0-9a-f]{32}$'),
    CONSTRAINT experience_failure_signatures_key_parts CHECK (
        signature_key = tool_name || chr(31) || error_class || chr(31) || fingerprint)
);

-- An exact match: one key in one tenant.
CREATE INDEX IF NOT EXISTS experience_failure_signatures_key_idx
    ON agent_experience.experience_failure_signatures (tenant_id, signature_key);

-- A coarse match: one tool and error class in one tenant.
CREATE INDEX IF NOT EXISTS experience_failure_signatures_tool_class_idx
    ON agent_experience.experience_failure_signatures (tenant_id, tool_name, error_class);

-- The tombstone transition removes the record's signature rows.
CREATE OR REPLACE FUNCTION agent_experience.clear_erased_record_failure_signatures() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
BEGIN
    IF NEW.deleted_at IS NOT NULL AND OLD.deleted_at IS NULL THEN
        DELETE FROM agent_experience.experience_failure_signatures WHERE experience_id = NEW.experience_id;
    END IF;

    RETURN NULL;
END;
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_failure_signatures_cleared'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_failure_signatures_cleared
            AFTER UPDATE OF deleted_at ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.clear_erased_record_failure_signatures();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_failure_signatures_cleared;

DROP POLICY IF EXISTS rls_failure_signatures_select ON agent_experience.experience_failure_signatures;
CREATE POLICY rls_failure_signatures_select ON agent_experience.experience_failure_signatures
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (
            ((team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
            AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
            AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
            OR ROW(experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id)::text IN (SELECT agent_experience.rls_granted_keys())));

DROP POLICY IF EXISTS rls_failure_signatures_insert ON agent_experience.experience_failure_signatures;
CREATE POLICY rls_failure_signatures_insert ON agent_experience.experience_failure_signatures
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))
        AND EXISTS (
            SELECT 1 FROM agent_experience.experience_records r
            WHERE r.experience_id = experience_failure_signatures.experience_id
              AND r.tenant_id = experience_failure_signatures.tenant_id
              AND r.application_id = experience_failure_signatures.application_id
              AND r.project_id = experience_failure_signatures.project_id
              AND r.team_id IS NOT DISTINCT FROM experience_failure_signatures.team_id
              AND r.agent_id IS NOT DISTINCT FROM experience_failure_signatures.agent_id
              AND r.user_id IS NOT DISTINCT FROM experience_failure_signatures.user_id
              AND r.deleted_at IS NULL));
