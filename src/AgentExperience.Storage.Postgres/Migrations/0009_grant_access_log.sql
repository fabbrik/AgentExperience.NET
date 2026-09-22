-- AgentExperience.NET: the append-only log of reads a sharing grant delivered, and the database's own
-- ceiling on how long a grant may live.
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0008, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT THIS TABLE ANSWERS. agent_experience.experience_grant_events records ADMINISTRATION: who allowed
-- what, until when, and when they stopped allowing it. It cannot answer "who actually read our team's
-- experience, and when". agent_experience.experience_grant_access answers exactly that, and nothing else:
-- one row per record DELIVERED to a caller who could only see it because a grant permitted it.
--
-- THIS TABLE HOLDS DELIVERIES, AND A SEARCH RESULT IS A DELIVERY. A row is written whenever a read hands
-- the caller a record it does not own: IExperienceRecordStore.GetAsync (which the MAF provider's
-- pre-injection re-read also goes through), the text candidate source, and the vector index. The two
-- search channels are included because an ExperienceCandidate carries the record read back IN FULL -- a
-- host consuming those ports directly receives complete foreign records -- so a match is a disclosure,
-- not a notice that something matched. A search's rows are written in ONE statement, so auditing costs
-- one round trip per search rather than one per row.
--
-- TWO READS ARE NOT DELIVERIES. A record the caller owns: the grant permitted nothing there, so there is
-- nothing to attribute to one, which is also why every row here can carry a non-null grant_id. And a read
-- the caller refuses after fetching it precisely BECAUSE a grant is what made it readable -- a confidence
-- submission, which a grant never confers -- because nothing was handed over. The port says which of the
-- two a read is, through ExperienceReadPurpose.
--
-- THE ROW IS NEVER WRITTEN INSIDE THE READ'S OWN STATEMENT. A read that wrote its own audit row would
-- take a write lock on every read and could never run on a replica. The reader appends this row in a
-- separate statement once the record has been read; whether a failed append also fails the read is the
-- host's ExperienceGrantAuditing.Mode, not this schema's business.
--
-- NO FOREIGN KEY TO experience_grants OR experience_records, matching 0002, 0007, and 0008. The trail
-- must outlive whatever it describes: a read that happened is a fact even if the record is later removed
-- by a retention pass, and a foreign key would turn "this happened" into a write failure. grant_id is
-- joinable to agent_experience.experience_grants and to agent_experience.experience_grant_events by hand,
-- which is how an auditor moves from "who read it" to "who permitted it":
--
--     SELECT a.occurred_at, a.principal_id, a.recipient_team_id, e.administrator_principal_id, e.reason
--     FROM agent_experience.experience_grant_access a
--     JOIN agent_experience.experience_grant_events e ON e.grant_id = a.grant_id AND e.action = 'Issued'
--     WHERE a.experience_id = $1
--     ORDER BY a.occurred_at;
--
-- occurred_at IS THE READER'S CLOCK, recorded_at IS THE DATABASE'S. When the read happened and when the
-- row landed are two facts, and a reader that was slow, retried, or clock-skewed must not be able to
-- disguise either. recorded_at is clock_timestamp(), not now(): now() is fixed at the start of the
-- surrounding transaction, and this column is meant to be the instant the row landed. (0002 splits the
-- same two names, but both of its values are written from the client, so it is not the precedent for
-- this one.) occurred_at comes from the reader's injected TimeProvider, so a test can freeze it.
--
-- UPGRADING AN EXISTING DATABASE. The table is created here, so on any database the migrator has
-- journaled it starts empty and every constraint on it is plain. The one deferred constraint is on the
-- EXISTING experience_grants table -- the lifetime ceiling below -- which is added NOT VALID exactly as
-- 0007's CHECKs are, because a database that has been issuing grants since 0005 may already hold a row
-- with an unbounded expiry (DateTimeOffset.MaxValue was storable before this story). New and updated rows
-- are checked from this moment on; existing rows are not scanned. After upgrading, find the offenders:
--
--     SELECT grant_id, issued_at, expires_at FROM agent_experience.experience_grants
--     WHERE revoked_at IS NULL AND expires_at > issued_at + interval '10 years';
--
-- Those grants cannot be shortened in place -- 0006's trigger refuses any UPDATE that moves expires_at,
-- deliberately -- so end them the way everything else here ends: revoke them through
-- IExperienceGrantStore.RevokeAsync and issue replacements with a bounded expiry. Once the query returns
-- nothing:
--
--     ALTER TABLE agent_experience.experience_grants
--         VALIDATE CONSTRAINT experience_grants_lifetime_bounded;
--
-- VALIDATE takes only a SHARE UPDATE EXCLUSIVE lock, so it does not block reads or writes.
--
-- THE INDEXES ARE NOT FREE ON A LARGE, HAND-APPLIED TABLE. They are built with plain CREATE INDEX, which
-- takes a SHARE lock and therefore blocks appends for the duration of the build. On the empty table this
-- script creates that is imperceptible; on a hand-applied table with a long history it is a write outage.
-- A deployment that cannot take one should create them out of band *before* running this script --
-- CREATE INDEX ... CONCURRENTLY cannot run inside the migrator's per-script transaction, and
-- IF NOT EXISTS then makes this script's own statements no-ops:
--
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_grant_access_grant
--         ON agent_experience.experience_grant_access (grant_id, occurred_at);
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_grant_access_record
--         ON agent_experience.experience_grant_access
--         (tenant_id, application_id, project_id, experience_id, occurred_at);
--
-- CONCURRENTLY can leave an INVALID index behind if it fails; check with
-- "SELECT indisvalid FROM pg_index WHERE indexrelid = 'agent_experience.ix_experience_grant_access_grant'::regclass",
-- and DROP INDEX CONCURRENTLY and retry if it comes back false. Do this before migrating, not after.
--
-- RETENTION. Deferred to roadmap story 4.5 along with the confidence and feedback ledgers', and for the
-- same reason: the append-only triggers below mean a retention pass is a deliberate, documented operation
-- by the table's owner, not something an application role does by accident. See 0006's header for the
-- runbook and for exactly what the triggers do and do not bind. This is the ledger most likely to grow
-- without bound in a deployment that shares heavily, so plan it before enabling auditing at scale.

CREATE TABLE IF NOT EXISTS agent_experience.experience_grant_access (
    access_id                uuid        NOT NULL,

    -- The grant the read predicate actually used, not merely one that could have permitted it. Where two
    -- active grants would both admit the same read, the reader's lateral join names one deterministically
    -- and this is that one.
    grant_id                 uuid        NOT NULL,

    experience_id            uuid        NOT NULL,

    -- The record's revision as it was delivered. A record is a mutable projection that every lifecycle
    -- commit moves, so without this the trail could say a record was disclosed but never which version.
    record_revision          bigint      NOT NULL,

    -- The record's owner scope, as the read found it: whose experience was handed over.
    tenant_id                text        NOT NULL,
    application_id           text        NOT NULL,
    project_id               text        NOT NULL,
    team_id                  text        NULL,
    agent_id                 text        NULL,
    user_id                  text        NULL,

    -- The scope the read was made in: who it was handed to. Same shape as the grant's recipient columns.
    recipient_tenant_id      text        NOT NULL,
    recipient_application_id text        NOT NULL,
    recipient_project_id     text        NOT NULL,
    recipient_team_id        text        NULL,
    recipient_agent_id       text        NULL,
    recipient_user_id        text        NULL,

    -- The host's AuthorizationContext.PrincipalId, never anything the caller passed as data. NOT NULL,
    -- unlike lifecycle_events.actor: a row that cannot say WHO read the record does not answer the
    -- question this ledger exists for, so the reader refuses a blank principal as an audit failure --
    -- reported, and fail-closed under Required -- rather than storing a row with a null in it.
    principal_id             text        NOT NULL,

    -- The host's identifier for the work that caused the read, when it supplied one, so a delivery can
    -- be tied back to the invocation behind it. Nothing here interprets it.
    correlation_id           text        NULL,

    -- When the read happened, from the reader's clock, and when the row landed, from the database's.
    occurred_at              timestamptz NOT NULL,
    recorded_at              timestamptz NOT NULL,

    CONSTRAINT experience_grant_access_pkey PRIMARY KEY (access_id),
    CONSTRAINT experience_grant_access_access_id_not_empty CHECK (
        access_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grant_access_grant_id_not_empty CHECK (
        grant_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grant_access_experience_id_not_empty CHECK (
        experience_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT experience_grant_access_scope_not_blank CHECK (
        tenant_id ~ '[^[:space:]]' AND application_id ~ '[^[:space:]]' AND project_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_team_id_not_blank CHECK (team_id IS NULL OR team_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_agent_id_not_blank CHECK (agent_id IS NULL OR agent_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_user_id_not_blank CHECK (user_id IS NULL OR user_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_recipient_scope_not_blank CHECK (
        recipient_tenant_id ~ '[^[:space:]]'
        AND recipient_application_id ~ '[^[:space:]]'
        AND recipient_project_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_recipient_team_id_not_blank CHECK (
        recipient_team_id IS NULL OR recipient_team_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_recipient_agent_id_not_blank CHECK (
        recipient_agent_id IS NULL OR recipient_agent_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_recipient_user_id_not_blank CHECK (
        recipient_user_id IS NULL OR recipient_user_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_principal_id_not_blank CHECK (principal_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_correlation_id_not_blank CHECK (
        correlation_id IS NULL OR correlation_id ~ '[^[:space:]]'),
    CONSTRAINT experience_grant_access_record_revision_non_negative CHECK (record_revision >= 0),

    -- The boundary a grant can never cross, restated on the row that claims a grant carried a record
    -- across one. A row saying a record left its tenant, application, or project is unstorable here even
    -- if some other writer managed to store the grant that would have allowed it.
    CONSTRAINT experience_grant_access_same_boundary CHECK (
        recipient_tenant_id = tenant_id
        AND recipient_application_id = application_id
        AND recipient_project_id = project_id),

    -- A row attributing a delivery to a grant must describe a delivery a grant was needed for. An
    -- identical recipient scope is the owner reading its own record, which produces no row at all.
    CONSTRAINT experience_grant_access_recipient_differs CHECK (
        recipient_team_id IS DISTINCT FROM team_id
        OR recipient_agent_id IS DISTINCT FROM agent_id
        OR recipient_user_id IS DISTINCT FROM user_id)
);

-- "Who read anything through this grant, and when." The grant's own trail says who permitted it; this
-- index is how that question reaches the reads.
CREATE INDEX IF NOT EXISTS ix_experience_grant_access_grant
    ON agent_experience.experience_grant_access (grant_id, occurred_at);

-- "Who saw our team's experience, and when." Owner scope first, so the question can be asked of a whole
-- project as easily as of one record.
CREATE INDEX IF NOT EXISTS ix_experience_grant_access_record
    ON agent_experience.experience_grant_access
    (tenant_id, application_id, project_id, experience_id, occurred_at);

-- No other index is created here on purpose, following 0007 and 0008: these two serve the questions this
-- story exists to answer, and an index for a query nobody has written yet is a write cost with no reader.

-- Append-only for the same reason every other ledger here is: a row that could be edited or removed could
-- erase that a record was handed to someone after the fact, which is the single thing this table exists
-- to make impossible. 0006's function serves it unchanged -- its message names the table it fired on.
DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grant_access_append_only'
          AND tgrelid = 'agent_experience.experience_grant_access'::regclass)
    THEN
        CREATE TRIGGER experience_grant_access_append_only
            BEFORE UPDATE OR DELETE ON agent_experience.experience_grant_access
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grant_access_no_truncate'
          AND tgrelid = 'agent_experience.experience_grant_access'::regclass)
    THEN
        CREATE TRIGGER experience_grant_access_no_truncate
            BEFORE TRUNCATE ON agent_experience.experience_grant_access
            FOR EACH STATEMENT EXECUTE FUNCTION agent_experience.reject_event_log_mutation();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_grant_access ENABLE ALWAYS TRIGGER experience_grant_access_append_only;
ALTER TABLE agent_experience.experience_grant_access ENABLE ALWAYS TRIGGER experience_grant_access_no_truncate;

-- THE DATABASE'S OWN CEILING ON A GRANT'S LIFETIME. The host-configured maximum lives in
-- PostgresExperienceGrantPolicy and is applied where grants are created, because a CHECK cannot express
-- "whatever interval this deployment configured". This is defence in depth underneath it: a fixed,
-- deliberately generous bound that makes a literally permanent grant -- DateTimeOffset.MaxValue, which
-- this column accepted before today -- unstorable by ANY writer, including one that bypasses this
-- library. It is not the policy; it is the floor the policy can never be configured below.
--
-- A REVOKED ROW IS EXEMPT, AND THAT IS THE WHOLE POINT OF THE EXEMPTION. PostgreSQL re-checks a CHECK on
-- every UPDATE of the row, not only on INSERT. Without "revoked_at IS NOT NULL OR", revoking a
-- pre-existing unbounded grant -- the one remedy this script's header prescribes for it -- would be
-- refused by the very constraint that made it a problem, leaving it unrevocable and permanent forever.
-- The exemption cannot widen anything: a revoked grant permits no read at all, and 0006's monotonicity
-- trigger still refuses any UPDATE that moves expires_at outward or un-revokes a revoked row.
DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grants_lifetime_bounded'
          AND conrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grants
            ADD CONSTRAINT experience_grants_lifetime_bounded
            CHECK (revoked_at IS NOT NULL OR expires_at <= issued_at + interval '10 years')
            NOT VALID;
    END IF;
END
$body$;

-- ...AND THE OTHER HALF OF THAT CEILING: issued_at MUST NOT BE IN THE FUTURE. The bound above is relative
-- to issued_at, so a writer bypassing this library could store an effectively permanent grant simply by
-- dating it a century ahead -- "ten years" from 2126 outlives everyone the trail is for. A CHECK cannot
-- say this, because now() is not immutable and a CHECK may only call immutable functions, so it is a
-- trigger. One minute of tolerance absorbs ordinary clock skew between an application host and the
-- database without admitting anything meaningful.
CREATE OR REPLACE FUNCTION agent_experience.reject_future_grant_issue()
RETURNS trigger
LANGUAGE plpgsql
AS $body$
BEGIN
    IF NEW.issued_at > clock_timestamp() + interval '1 minute' THEN
        RAISE EXCEPTION
            'agent_experience.% rejected: issued_at must not be in the future.', TG_TABLE_NAME
            USING ERRCODE = 'check_violation', CONSTRAINT = 'experience_grants_issued_not_future';
    END IF;

    RETURN NEW;
END
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_grants_issued_not_future'
          AND tgrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        CREATE TRIGGER experience_grants_issued_not_future
            BEFORE INSERT ON agent_experience.experience_grants
            FOR EACH ROW EXECUTE FUNCTION agent_experience.reject_future_grant_issue();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_grants ENABLE ALWAYS TRIGGER experience_grants_issued_not_future;
