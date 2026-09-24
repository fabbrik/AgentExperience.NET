-- AgentExperience.NET: a retention path for the grant access log (Story 5.4, KL-10).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0011, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT THIS ADDS. 0009 created agent_experience.experience_grant_access, append-only, and deferred its
-- retention; 0010 decided that erasing a record KEEPS the access rows that name it, because they answer
-- "who read this before it was deleted". Nothing collected them afterwards, so the ledger grew until the
-- tables' owner pruned it by hand. This script is that collection path: one SECURITY DEFINER function,
-- agent_experience.purge_grant_access, that removes access rows older than a host-given cutoff, in bounded
-- batches, within one owner scope (or that scope and everything beneath it), under a marker the append-only
-- guard recognises: SET LOCAL inside the function, and reset when the function returns by its own SET clause,
-- so it never outlives the call even inside a longer transaction. No trigger is ever disabled.
--
-- WHY A SEPARATE MARKER. 0010's agent_experience.purge_authorized admits DELETE on five ledgers and
-- deliberately NOT on this one. Reusing it here would have made "record erasure never deletes an access
-- row" a property of purge_experience_record's body rather than of the schema. So this function sets its
-- own marker, agent_experience.access_purge_authorized, and the guard admits a DELETE on
-- experience_grant_access only under that marker. The two markers never admit each other's tables.
--
-- THE MINIMUM RETENTION, AND WHY IT IS REFUSED RATHER THAN CLAMPED. An access row is the answer to "who
-- read our experience". The purge is the one operation that removes that answer, so it must not be usable
-- to erase a read the moment after it happened through this library -- a bug, a careless script, or a
-- misused API call covering a read it just made. It does NOT stop the tables' owner: that role can disable the
-- trigger or insert rows with any recorded_at (KL-4, and 0010's honesty statement). The floor is 30 days by
-- the DATABASE's clock, enforced twice:
--   * purge_grant_access refuses a cutoff later than clock_timestamp() - 30 days (and a NULL cutoff),
--     returning 'CutoffTooRecent' and deleting nothing. It does not clamp: a clamp would report a
--     successful purge while rows the host asked about survived -- a retention obligation quietly unmet,
--     reported as success, which is exactly the failure mode KL-3 was.
--   * the guard itself re-checks every row: a DELETE under the marker is admitted only when
--     OLD.recorded_at is at least 30 days old. A session that sets the marker by hand still cannot delete
--     a fresh row.
-- 30 days is a floor, not a policy. The host decides the real retention and passes it as the cutoff.
--
-- WHICH TIMESTAMP DECIDES AGE. recorded_at, which the adapter writes as clock_timestamp() when the row
-- lands -- never occurred_at, which is the reader's clock and could be backdated into the purge window by
-- a skewed or hostile reader. (A row INSERTed by raw SQL carries whatever recorded_at its writer chose;
-- the floor protects the rows this library appended.)
--
-- WHAT "BENEATH" MEANS (p_subtree). With p_subtree false or NULL the owner scope is matched exactly, field
-- for field, as every other operation here matches it. With p_subtree true a row is in reach when its
-- tenant_id, application_id and project_id equal the root's exactly and, for each of team_id, agent_id and
-- user_id, the root's value is NULL or equals the row's. A NULL root field matches any value including
-- NULL; a non-NULL one matches only itself. The three required fields are never a wildcard: a NULL
-- p_tenant_id, p_application_id or p_project_id matches nothing. So a subtree never reaches another
-- tenant, application or project, an ancestor of the root, or a sibling.
--
-- THIS IS AN AUDITABILITY MECHANISM, NOT A PRIVILEGE BOUNDARY -- the same honesty statement as 0010's,
-- unchanged. A custom GUC is settable by any session, and the guards do not bind a role that can
-- ALTER TABLE. The one real boundary is EXECUTE on the function, revoked from PUBLIC below. A deployment
-- whose application role is not the migrating role must grant it once, and to nothing else:
--
--     GRANT EXECUTE ON FUNCTION agent_experience.purge_grant_access(
--         text, text, text, text, text, text, boolean, timestamptz, integer) TO <application_role>;
--
-- THE INDEX IS NOT FREE ON A LARGE LEDGER. ix_experience_grant_access_retention is built with plain
-- CREATE INDEX inside the migrator's per-script transaction, which blocks appends for the duration of the
-- build -- and this is the ledger most likely to be large. A deployment that cannot take that should
-- create it out of band first; IF NOT EXISTS then makes this script's statement a no-op:
--
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_grant_access_retention
--         ON agent_experience.experience_grant_access
--         (tenant_id, application_id, project_id, recorded_at, access_id);
--
-- CONCURRENTLY can leave an INVALID index behind if it fails; check with
-- "SELECT indisvalid FROM pg_index WHERE indexrelid = 'agent_experience.ix_experience_grant_access_retention'::regclass",
-- and DROP INDEX CONCURRENTLY and retry if it comes back false. Do this before migrating, not after.
--
-- AND THE DEAD TUPLE. As 0010 says of every erasure here: a deleted access row stays in the heap until
-- VACUUM reclaims it. An obligation with a deadline has to run
-- "VACUUM agent_experience.experience_grant_access" itself.

CREATE INDEX IF NOT EXISTS ix_experience_grant_access_retention
    ON agent_experience.experience_grant_access
    (tenant_id, application_id, project_id, recorded_at, access_id);

-- The access marker, read in one place. An unset custom GUC reads back NULL under the missing_ok form,
-- which is exactly "not authorized".
CREATE OR REPLACE FUNCTION agent_experience.access_purge_authorized() RETURNS boolean
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $body$
    SELECT coalesce(pg_catalog.current_setting('agent_experience.access_purge_authorized', true), 'off') = 'on';
$body$;

-- 0010's append-only guard, restated with its one exception unchanged and exactly one more. UPDATE and
-- TRUNCATE are still refused unconditionally, on every table, in every session, including the purging one.
-- The new exception: a DELETE on experience_grant_access, while the ACCESS marker is set, of a row at
-- least 30 days old by the database's clock. The 0010 marker still admits nothing on that table.
--
-- The refusal message and SQLSTATE are unchanged, so an operator who hits the guard is told exactly what
-- 0006 told them. Unlike 0010's version, this one pins its search_path: the floor comparison below uses the
-- <= and - operators, and a session that put a schema of its own ahead of pg_catalog could otherwise shadow
-- them and walk a hand-marked DELETE past the floor. The new branch also checks the table's schema, not
-- only its name.
CREATE OR REPLACE FUNCTION agent_experience.reject_event_log_mutation() RETURNS trigger AS $body$
BEGIN
    IF TG_OP = 'DELETE'
        AND agent_experience.purge_authorized()
        AND TG_TABLE_NAME IN (
            'lifecycle_events',
            'experience_grant_events',
            'confidence_evidence',
            'reuse_feedback',
            'reuse_feedback_exposures')
    THEN
        RETURN OLD;
    END IF;

    -- Nested rather than one AND chain on purpose: OLD.recorded_at exists only on this table, and an
    -- expression naming it is resolved against whichever table fired the trigger. The outer test keeps
    -- every other table (and every statement-level TRUNCATE, where OLD is unassigned) away from it.
    IF TG_OP = 'DELETE' AND TG_TABLE_SCHEMA = 'agent_experience' AND TG_TABLE_NAME = 'experience_grant_access' THEN
        IF agent_experience.access_purge_authorized()
            AND OLD.recorded_at <= pg_catalog.clock_timestamp() - interval '30 days'
        THEN
            RETURN OLD;
        END IF;
    END IF;

    RAISE EXCEPTION
        'agent_experience.% is append-only: a stored event row cannot be %.',
        TG_TABLE_NAME,
        CASE TG_OP
            WHEN 'UPDATE' THEN 'updated'
            WHEN 'DELETE' THEN 'deleted'
            ELSE 'truncated away'
        END
        USING ERRCODE = 'insufficient_privilege';
END;
$body$ LANGUAGE plpgsql
SET search_path = pg_catalog, agent_experience;

-- THE ONE ACCESS-LOG RETENTION PATH. One statement from the caller, one transaction, one batch.
--
-- THE BATCH BOUND IS APPLIED HERE, NOT ONLY BY THE CALLER, exactly as purge_expired_grants does it:
-- LIMIT NULL means "no limit" in PostgreSQL, so p_limit is clamped to the adapter's 1..500 and NULL
-- becomes 500.
--
-- ROWS ARE LOCKED IN A DETERMINISTIC ORDER (recorded_at, access_id) before any is deleted, so two
-- concurrent purges over overlapping rows cannot deadlock: the second waits on the first's lock, then
-- skips the rows the first deleted (READ COMMITTED re-checks a locked row and drops it once it is gone),
-- so the two counts never overlap.
--
-- more_remain is asked after the delete, inside the same transaction, with the same predicate, so it is
-- true whenever another call with the same arguments could find more (a row another purge is
-- deleting right now still counts, which errs towards "call again", never towards a false "clean").
CREATE OR REPLACE FUNCTION agent_experience.purge_grant_access(
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_subtree boolean,
    p_cutoff timestamptz,
    p_limit integer)
RETURNS TABLE (purge_outcome text, purged bigint, more_remain boolean)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, agent_experience
SET agent_experience.access_purge_authorized = 'off'
AS $body$
DECLARE
    v_limit integer := least(greatest(coalesce(p_limit, 500), 1), 500);
    v_subtree boolean := coalesce(p_subtree, false);
    v_access_ids uuid[];
    v_purged bigint;
    v_more boolean;
BEGIN
    -- The floor, by the database's clock. A NULL cutoff is refused too: LEAST and comparisons would
    -- otherwise read it as "no bound".
    IF p_cutoff IS NULL OR p_cutoff > pg_catalog.clock_timestamp() - interval '30 days' THEN
        RETURN QUERY SELECT 'CutoffTooRecent'::text, 0::bigint, false;
        RETURN;
    END IF;

    SET LOCAL agent_experience.access_purge_authorized = 'on';

    SELECT array_agg(aged.access_id) INTO v_access_ids
    FROM (
        SELECT a.access_id
        FROM agent_experience.experience_grant_access a
        WHERE a.tenant_id = p_tenant_id
          AND a.application_id = p_application_id
          AND a.project_id = p_project_id
          AND (a.team_id IS NOT DISTINCT FROM p_team_id OR (v_subtree AND p_team_id IS NULL))
          AND (a.agent_id IS NOT DISTINCT FROM p_agent_id OR (v_subtree AND p_agent_id IS NULL))
          AND (a.user_id IS NOT DISTINCT FROM p_user_id OR (v_subtree AND p_user_id IS NULL))
          AND a.recorded_at < p_cutoff
        ORDER BY a.recorded_at, a.access_id
        LIMIT v_limit
        FOR UPDATE) aged;

    IF v_access_ids IS NULL THEN
        v_purged := 0;
    ELSE
        WITH removed AS (
            DELETE FROM agent_experience.experience_grant_access WHERE access_id = ANY(v_access_ids) RETURNING 1)
        SELECT count(*) INTO v_purged FROM removed;
    END IF;

    SELECT EXISTS (
        SELECT 1
        FROM agent_experience.experience_grant_access a
        WHERE a.tenant_id = p_tenant_id
          AND a.application_id = p_application_id
          AND a.project_id = p_project_id
          AND (a.team_id IS NOT DISTINCT FROM p_team_id OR (v_subtree AND p_team_id IS NULL))
          AND (a.agent_id IS NOT DISTINCT FROM p_agent_id OR (v_subtree AND p_agent_id IS NULL))
          AND (a.user_id IS NOT DISTINCT FROM p_user_id OR (v_subtree AND p_user_id IS NULL))
          AND a.recorded_at < p_cutoff) INTO v_more;

    RETURN QUERY SELECT 'Purged'::text, v_purged, v_more;
END
$body$;

-- WHO MAY PURGE THE TRAIL. SECURITY DEFINER, and PostgreSQL grants EXECUTE on a new function to PUBLIC by
-- default -- which would let any role that can connect, including a SELECT-only reporting role, erase any
-- tenant's access history older than the floor. Revoked, and granted back only to the role applying the
-- migration. CREATE OR REPLACE preserves a function's ACL, so re-running this script neither loses the
-- revoke nor re-opens the grant.
REVOKE ALL ON FUNCTION agent_experience.purge_grant_access(
    text, text, text, text, text, text, boolean, timestamptz, integer) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION agent_experience.purge_grant_access(
    text, text, text, text, text, text, boolean, timestamptz, integer) TO CURRENT_USER;

-- agent_experience.access_purge_authorized() is deliberately left callable by PUBLIC, like 0010's
-- purge_authorized(): it reveals only whether the calling session set the marker, and the guard calls it
-- from within every session's own triggers.
