-- AgentExperience.NET: opt-in row-level security behind the application role (Story 15.1).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0018, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT THIS IS. Tenant isolation has so far rested on one layer: the scope predicates every store statement
-- carries. This script adds the policies for a second, independent layer -- PostgreSQL row-level security --
-- that confines the application role to the authorization bounds of the operation it is running, so that a
-- mistake in one store statement's predicate still cannot read or write another tenant's rows.
--
-- IT DOES NOT SWITCH ANYTHING ON. A policy on a table whose row-level security is disabled has no effect.
-- ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync enables it, table by table, only when the host
-- sets ExperienceApplicationRoleOptions.EnableRowLevelSecurity, and disables it again when the host does not.
-- It never uses FORCE ROW LEVEL SECURITY: the owner -- and so the SECURITY DEFINER purge and sealing functions,
-- which run as the owner -- keeps bypassing the policies, exactly as it bypasses every privilege.
--
-- WHAT THE POLICIES READ. Each store operation declares, inside its own transaction, the host
-- AuthorizationContext's bounds with set_config(..., true):
--
--     agent_experience.auth_tenant, auth_application, auth_project, auth_team, auth_agent, auth_user
--     agent_experience.auth_set = 'on'
--
-- A bound is '=' followed by the value, or the empty string for "unrestricted" (a null AuthorizationContext
-- bound); the tenant is always a bound. The prefix keeps an empty-string bound -- which restricts to a value no
-- row can hold -- from reading as "no bound". A policy admits a row only while auth_set is 'on' and the row's
-- scope lies inside the bounds, so a statement run with nothing declared sees nothing and can write nothing.
--
-- WHAT EACH TABLE ADMITS. "Inside the bounds" is AuthorizationContext.Permits over the row's scope columns:
-- the tenant exactly, and each other field exactly where it is bounded.
--
--   experience_records, experience_embeddings (the latter from the vectors package's own script)
--       read:  the row's scope inside the bounds, OR a live sharing grant over the row whose recipient scope is
--              inside the bounds -- the read predicate's own grant rule: not revoked, expires_at later than
--              clock_timestamp(), and the grant's owner columns equal to the row's.
--       write: the row's scope inside the bounds, before and after. A grant never admits a write.
--   lifecycle_events, reuse_feedback
--       read and write: the row's scope inside the bounds.
--   confidence_evidence (no scope columns of its own)
--       read and write: its record's scope inside the bounds.
--   reuse_feedback_exposures (no scope columns of its own)
--       read and write: its submission's scope inside the bounds. The record an exposure names is not checked:
--       the port lets a submission name a record the store does not hold, and a record outside the bounds is, to
--       a policy, exactly as invisible as one that does not exist.
--   experience_grants
--       read: the owner scope inside the bounds, OR the recipient scope inside them while the grant is live
--             (not revoked, not expired) -- a recipient's read consults the grant, and only a live one.
--       write (issue, revoke): the owner scope inside the bounds.
--   experience_grant_events
--       read and write: the owner scope inside the bounds.
--   experience_grant_access
--       read: the owner scope OR the recipient scope inside the bounds.
--       write: the recipient scope inside the bounds AND a live grant whose ID, record, owner columns and
--              recipient columns are exactly the row's -- the reader appends the row about its own read, and
--              cannot append one about a grant that does not exist or does not name it.
--
-- No table has a DELETE policy here, so with row-level security on the application role can delete from none
-- of them (it holds DELETE only on experience_embeddings, whose policy the vectors script adds). Erasure,
-- sweeps, grant and access-log purges and sealing all run inside the SECURITY DEFINER functions, as the owner.
--
-- THE SECURITY DEFINER FUNCTIONS APPLY THE BOUNDS THEMSELVES. purge_experience_record, purge_expired_grants,
-- purge_grant_access and seal_experience_record run as the owner, so no policy binds what they touch. This script
-- redefines each -- the body exactly as 0010, 0012 and 0016 wrote it, search_path as 0013 pinned it, ACL and owner
-- kept by CREATE OR REPLACE -- with one guard first: while the calling transaction has declared bounds
-- (auth_set = 'on'), a scope argument outside them raises insufficient_privilege before anything is read. While
-- row-level security is enabled on experience_records, a caller that declared nothing is refused as well, unless its
-- login role is a member of the tables' owner or holds BYPASSRLS. With row-level security disabled, an undeclared
-- caller is unaffected, exactly as before; a caller that declares its own bounds is the documented boundary below.
--
-- A SUPERSET OF THE STORE PREDICATES, NEVER A REPLACEMENT. The bounds are what the host authorized, which
-- contains every scope a store statement may touch: the exact-scope predicates, the subtree predicates of the
-- sweeps, and the grant reads. So with the policies on, every statement a store sends returns exactly what it
-- returned before; only a statement whose own predicate is wrong is cut back to the bounds. The store
-- predicates all stay in the SQL.
--
-- WHAT THIS DOES NOT GUARD AGAINST. set_config is open to every session: a host that can run arbitrary SQL as
-- the application role can declare whatever bounds it likes. Row-level security here guards against a SQL
-- mistake in a store, not against a compromised application role.
-- Nor does it enforce a grant's disclosure level: a grant admits the whole row, and what of it a borrowed record
-- shows (LessonOnly and the rest) is decided in the library's code. And unique indexes span tenants, so an insert
-- that collides with another tenant's ID still fails as a collision -- a store reports it as a Conflict -- which
-- tells the writer that ID exists somewhere, exactly as it did before.
--
-- THE HELPERS. None is SECURITY DEFINER, so none is a path to the owner's rights; they keep PostgreSQL's default
-- EXECUTE for PUBLIC, which the policies need, and run as the application role under the other tables' own policies.
--
--   rls_bound(field)        the declared bound for one field, or NULL when there is none or nothing was declared;
--   rls_unbounded(field)    true exactly when that field was declared unrestricted (the empty string);
--   rls_scope_admits(...)   a scope inside the bounds, for the SECURITY DEFINER guards and the two below;
--   rls_granted_keys()      once per statement: the identity of every live grant whose recipient lies inside the
--                           bounds -- the record and its six owner columns, as a row literal;
--   rls_access_grant_live() whether a live grant names exactly an access row's grant, record, owner and recipient.
--
-- The first three have SQL-standard bodies, bound when this script runs, so the caller's search_path cannot change
-- what an operator means inside one, and the planner can inline them. The last two read experience_grants: they are
-- PL/pgSQL under a pinned search_path, so they record no dependency on that table (a database without 0005 is
-- simulated by dropping it), and they answer "nothing is granted" rather than fail when the table is missing or the
-- caller may not read it -- which is what lets a store's exact-scope fallback keep working with row-level security
-- on. They read clock_timestamp(), exactly as the store's grant predicate does.
--
-- WHY THE POLICIES LOOK THE WAY THEY DO. Every bound is compared through an uncorrelated subquery,
-- (SELECT agent_experience.rls_bound(...)), which the planner evaluates once per statement as an InitPlan: no helper
-- runs per row, and a policy's "tenant_id = <that>" is an ordinary index condition on the existing scope indexes.
-- Tenant, application and project come first and outside the grant branch, because no grant crosses them
-- (experience_grants_same_boundary). The grant branch is "ROW(...)::text IN (SELECT agent_experience.rls_granted_keys())",
-- an uncorrelated subquery PostgreSQL hashes: one call per statement, then a hash probe for a row the scope branch has
-- not already admitted. The set is taken at the start of the statement and grants only stop being live after that,
-- so it never admits less than the store's own per-row predicate.
--
-- The policies are exactly RowLevelSecurityPolicies.All in the adapter, which a test holds this script equal to, and
-- which ApplyApplicationRolePrivilegesAsync re-runs whenever it enables row-level security.
--
-- LOCKS. CREATE POLICY and DROP POLICY take an ACCESS EXCLUSIVE lock on their table for an instant. On a busy
-- database run the migrator with a lock_timeout and retry. No table is rewritten and no index is built.

CREATE OR REPLACE FUNCTION agent_experience.rls_bound(p_field text)
RETURNS text
LANGUAGE sql
STABLE
PARALLEL SAFE
RETURN CASE
    WHEN pg_catalog.current_setting('agent_experience.auth_set', true) = 'on'
        AND pg_catalog.left(pg_catalog.current_setting('agent_experience.auth_' || p_field, true), 1) = '='
    THEN pg_catalog.substr(pg_catalog.current_setting('agent_experience.auth_' || p_field, true), 2)
END;

CREATE OR REPLACE FUNCTION agent_experience.rls_unbounded(p_field text)
RETURNS boolean
LANGUAGE sql
STABLE
PARALLEL SAFE
RETURN COALESCE(
    pg_catalog.current_setting('agent_experience.auth_set', true) = 'on'
    AND pg_catalog.current_setting('agent_experience.auth_' || p_field, true) = '',
    false);

CREATE OR REPLACE FUNCTION agent_experience.rls_scope_admits(
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text)
RETURNS boolean
LANGUAGE sql
STABLE
PARALLEL SAFE
RETURN COALESCE(
    p_tenant_id = agent_experience.rls_bound('tenant')
    AND (p_application_id = agent_experience.rls_bound('application') OR agent_experience.rls_unbounded('application'))
    AND (p_project_id = agent_experience.rls_bound('project') OR agent_experience.rls_unbounded('project'))
    AND (p_team_id = agent_experience.rls_bound('team') OR agent_experience.rls_unbounded('team'))
    AND (p_agent_id = agent_experience.rls_bound('agent') OR agent_experience.rls_unbounded('agent'))
    AND (p_user_id = agent_experience.rls_bound('user') OR agent_experience.rls_unbounded('user')),
    false);

CREATE OR REPLACE FUNCTION agent_experience.rls_granted_keys()
RETURNS SETOF text
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
SET search_path = pg_catalog, pg_temp
AS $body$
DECLARE
    v_tenant text := agent_experience.rls_bound('tenant');
BEGIN
    IF v_tenant IS NULL
        OR pg_catalog.to_regclass('agent_experience.experience_grants') IS NULL
        OR NOT pg_catalog.has_table_privilege('agent_experience.experience_grants', 'SELECT')
    THEN
        RETURN;
    END IF;

    RETURN QUERY
        SELECT ROW(g.experience_id, g.tenant_id, g.application_id, g.project_id, g.team_id, g.agent_id, g.user_id)::text
        FROM agent_experience.experience_grants g
        WHERE g.recipient_tenant_id = v_tenant
          AND g.revoked_at IS NULL
          AND g.expires_at > pg_catalog.clock_timestamp()
          AND agent_experience.rls_scope_admits(
              g.recipient_tenant_id, g.recipient_application_id, g.recipient_project_id,
              g.recipient_team_id, g.recipient_agent_id, g.recipient_user_id);
END
$body$;

CREATE OR REPLACE FUNCTION agent_experience.rls_access_grant_live(
    p_grant_id uuid,
    p_experience_id uuid,
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_recipient_tenant_id text,
    p_recipient_application_id text,
    p_recipient_project_id text,
    p_recipient_team_id text,
    p_recipient_agent_id text,
    p_recipient_user_id text)
RETURNS boolean
LANGUAGE plpgsql
STABLE
PARALLEL SAFE
SET search_path = pg_catalog, pg_temp
AS $body$
BEGIN
    IF pg_catalog.to_regclass('agent_experience.experience_grants') IS NULL
        OR NOT pg_catalog.has_table_privilege('agent_experience.experience_grants', 'SELECT')
    THEN
        RETURN false;
    END IF;

    RETURN EXISTS (
        SELECT 1
        FROM agent_experience.experience_grants g
        WHERE g.grant_id = p_grant_id
          AND g.experience_id = p_experience_id
          AND g.revoked_at IS NULL
          AND g.expires_at > pg_catalog.clock_timestamp()
          AND g.tenant_id = p_tenant_id
          AND g.application_id = p_application_id
          AND g.project_id = p_project_id
          AND g.team_id IS NOT DISTINCT FROM p_team_id
          AND g.agent_id IS NOT DISTINCT FROM p_agent_id
          AND g.user_id IS NOT DISTINCT FROM p_user_id
          AND g.recipient_tenant_id = p_recipient_tenant_id
          AND g.recipient_application_id = p_recipient_application_id
          AND g.recipient_project_id = p_recipient_project_id
          AND g.recipient_team_id IS NOT DISTINCT FROM p_recipient_team_id
          AND g.recipient_agent_id IS NOT DISTINCT FROM p_recipient_agent_id
          AND g.recipient_user_id IS NOT DISTINCT FROM p_recipient_user_id);
END
$body$;

-- The SECURITY DEFINER functions, each with its bounds guard first. Bodies otherwise exactly as their scripts wrote them.

CREATE OR REPLACE FUNCTION agent_experience.purge_experience_record(
    p_experience_id uuid,
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_expected_revision bigint,
    p_deleted_at timestamptz)
RETURNS TABLE (purge_outcome text, purge_revision bigint)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, agent_experience, pg_temp
SET agent_experience.purge_authorized = 'off'
AS $body$
DECLARE
    v_revision bigint;
    v_deleted_at timestamptz;
    v_feedback_ids uuid[];
BEGIN
    -- Story 15.1. This function runs as its owner, which row-level security does not bind, so it applies the
    -- declared bounds itself: while a transaction has declared them, a scope outside them is refused before
    -- anything is read. With row-level security enabled, a caller that declared nothing is refused too -- unless the
    -- login role is a member of the tables' owner, or holds BYPASSRLS, neither of which the policies bind either.
    -- With row-level security disabled, an undeclared caller behaves exactly as before.
    IF pg_catalog.current_setting('agent_experience.auth_set', true) = 'on' THEN
        IF NOT agent_experience.rls_scope_admits(p_tenant_id, p_application_id, p_project_id, p_team_id, p_agent_id, p_user_id) THEN
            RAISE EXCEPTION 'agent_experience: the scope is outside the declared authorization bounds'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    ELSIF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        WHERE c.oid = 'agent_experience.experience_records'::pg_catalog.regclass
          AND c.relrowsecurity
          AND NOT pg_catalog.pg_has_role(session_user, c.relowner, 'MEMBER')
          AND NOT (SELECT r.rolbypassrls FROM pg_catalog.pg_roles r WHERE r.rolname = session_user))
    THEN
        RAISE EXCEPTION 'agent_experience: no authorization bounds were declared, and row-level security is enabled'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    -- Transaction-scoped, and narrower still: the function declares a SET for the same variable, so the
    -- marker is restored at function exit even when the caller's transaction continues afterwards.
    SET LOCAL agent_experience.purge_authorized = 'on';

    -- Step 1. The scope predicate, the revision guard, and the existence check in one statement, so a
    -- foreign scope, a stale revision, and a missing record are all "no row" and none of them can be told
    -- apart from the outcome, from a timing branch, or from the error text.
    SELECT r.revision, r.deleted_at INTO v_revision, v_deleted_at
    FROM agent_experience.experience_records r
    WHERE r.experience_id = p_experience_id
      AND r.tenant_id = p_tenant_id
      AND r.application_id = p_application_id
      AND r.project_id = p_project_id
      AND r.team_id IS NOT DISTINCT FROM p_team_id
      AND r.agent_id IS NOT DISTINCT FROM p_agent_id
      AND r.user_id IS NOT DISTINCT FROM p_user_id
      AND r.deleted_at IS NULL
      AND (p_expected_revision IS NULL OR r.revision = p_expected_revision)
    FOR UPDATE;

    IF NOT FOUND THEN
        -- Re-read inside the same transaction, with the same scope predicate and without the revision and
        -- tombstone guards, so "not in this scope" stays indistinguishable from "does not exist" while a
        -- stale revision and an already-erased record can still be reported to the scope that owns them.
        SELECT r.revision, r.deleted_at INTO v_revision, v_deleted_at
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
            RETURN QUERY SELECT 'NotFound'::text, 0::bigint;
            RETURN;
        END IF;

        IF v_deleted_at IS NOT NULL THEN
            -- Already a tombstone. Deleting again is a success that touches nothing.
            RETURN QUERY SELECT 'AlreadyDeleted'::text, v_revision;
            RETURN;
        END IF;

        RETURN QUERY SELECT 'StaleRevision'::text, v_revision;
        RETURN;
    END IF;

    -- Step 2.
    DELETE FROM agent_experience.confidence_evidence WHERE experience_id = p_experience_id;

    -- Step 3, in three parts, and the order of them is the whole defence against two purges sharing one
    -- submission.
    --
    -- THE RACE THIS AVOIDS. A submission may name several records; two purges erasing two of them run
    -- concurrently. If each simply deleted its own exposure and then asked "does this submission have any
    -- exposures left?", each would still see the other's not-yet-committed exposure row -- READ COMMITTED
    -- hides an uncommitted delete -- so each would decide the submission is still describing something and
    -- leave it. Both commit; the submission survives with ZERO exposures, describing nothing, and nothing
    -- else ever collects it. It carries a run ID, a scope, an outcome, a measure and -- for a human
    -- assessment -- a reviewer identity and a free-text rationale about records that no longer exist.
    --
    -- So: find the submissions this record's exposures belong to, take a row lock on each of them in a
    -- deterministic order, and only then delete. The second purge blocks on that lock until the first has
    -- committed, and its "any exposures left?" then runs against a snapshot that can see the first's
    -- delete. Locking the parents (rather than re-checking afterwards) also means the two purges cannot
    -- interleave into a deadlock: the order is by feedback_id for both.
    SELECT array_agg(DISTINCT x.feedback_id) INTO v_feedback_ids
    FROM agent_experience.reuse_feedback_exposures x
    WHERE x.experience_id = p_experience_id;

    IF v_feedback_ids IS NOT NULL THEN
        PERFORM 1 FROM agent_experience.reuse_feedback f
        WHERE f.feedback_id = ANY(v_feedback_ids)
        ORDER BY f.feedback_id
        FOR UPDATE;
    END IF;

    DELETE FROM agent_experience.reuse_feedback_exposures x WHERE x.experience_id = p_experience_id;

    -- Step 4. Only the submissions step 3 emptied -- "every submission with no exposures" would be a
    -- different, and much larger, statement.
    IF v_feedback_ids IS NOT NULL THEN
        DELETE FROM agent_experience.reuse_feedback f
        WHERE f.feedback_id = ANY(v_feedback_ids)
          AND NOT EXISTS (
              SELECT 1 FROM agent_experience.reuse_feedback_exposures x WHERE x.feedback_id = f.feedback_id);
    END IF;

    -- Step 5.
    DELETE FROM agent_experience.experience_grant_events
    WHERE grant_id IN (
        SELECT g.grant_id FROM agent_experience.experience_grants g WHERE g.experience_id = p_experience_id);

    -- Step 6.
    DELETE FROM agent_experience.experience_grants WHERE experience_id = p_experience_id;

    -- Step 7.
    DELETE FROM agent_experience.lifecycle_events WHERE experience_id = p_experience_id;

    -- Step 8. to_regclass answers "is there a relation by that name", which is not the same question as
    -- "is it the vectors package's embedding table". A deployment that has something else under that name
    -- -- an old shape, a view, another project's table -- would otherwise fail the EXECUTE with a bare
    -- undefined_column and abort the whole erasure, with the record still carrying its payload and no
    -- indication of why. So the shape is checked too, and a mismatch is reported as itself.
    IF to_regclass('agent_experience.experience_embeddings') IS NOT NULL THEN
        IF NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_attribute a
            WHERE a.attrelid = to_regclass('agent_experience.experience_embeddings')
              AND a.attname = 'experience_id'
              AND a.atttypid = 'pg_catalog.uuid'::pg_catalog.regtype
              AND a.attnum > 0
              AND NOT a.attisdropped)
        THEN
            RAISE EXCEPTION
                'The relation % has no uuid experience_id column, so this record''s stored vector cannot '
                'be removed. Nothing has been erased; reconcile it with 0004 and retry.',
                to_regclass('agent_experience.experience_embeddings')
                USING ERRCODE = 'undefined_column';
        END IF;

        EXECUTE 'DELETE FROM agent_experience.experience_embeddings WHERE experience_id = $1'
            USING p_experience_id;
    END IF;

    -- Step 9. Everything not on the retained list is set to a fixed, content-free value rather than left
    -- as it stands: a tombstone must not say when the work happened, which run produced it, or how often
    -- its lesson held up.
    UPDATE agent_experience.experience_records r
    SET payload = '{}'::jsonb,
        task_id = '(deleted)',
        status = 'Deleted',
        source_run_id = '00000000-0000-0000-0000-000000000000'::uuid,
        reuse_confidence = 0,
        supporting_validations = 0,
        contradictions = 0,
        created_at = p_deleted_at,
        updated_at = p_deleted_at,
        deleted_at = p_deleted_at,
        revision = r.revision + 1
    WHERE r.experience_id = p_experience_id;

    RETURN QUERY SELECT 'Deleted'::text, v_revision + 1;
END
$body$;

CREATE OR REPLACE FUNCTION agent_experience.purge_expired_grants(
    p_tenant_id text,
    p_application_id text,
    p_project_id text,
    p_team_id text,
    p_agent_id text,
    p_user_id text,
    p_now timestamptz,
    p_limit integer)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, agent_experience, pg_temp
SET agent_experience.purge_authorized = 'off'
AS $body$
DECLARE
    v_grant_ids uuid[];
    v_purged bigint;
    -- 1 and 500 are the same bounds ExperienceRecordValidator enforces on the adapter's batchSize. A NULL
    -- becomes the maximum rather than an error, because a bound is what this function owes its caller and
    -- refusing would tell a hand-caller nothing it could not work out from the signature.
    v_limit integer := least(greatest(coalesce(p_limit, 500), 1), 500);
    v_cutoff timestamptz := least(p_now, pg_catalog.clock_timestamp());
BEGIN
    -- Story 15.1. This function runs as its owner, which row-level security does not bind, so it applies the
    -- declared bounds itself: while a transaction has declared them, a scope outside them is refused before
    -- anything is read. With row-level security enabled, a caller that declared nothing is refused too -- unless the
    -- login role is a member of the tables' owner, or holds BYPASSRLS, neither of which the policies bind either.
    -- With row-level security disabled, an undeclared caller behaves exactly as before.
    IF pg_catalog.current_setting('agent_experience.auth_set', true) = 'on' THEN
        IF NOT agent_experience.rls_scope_admits(p_tenant_id, p_application_id, p_project_id, p_team_id, p_agent_id, p_user_id) THEN
            RAISE EXCEPTION 'agent_experience: the scope is outside the declared authorization bounds'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    ELSIF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        WHERE c.oid = 'agent_experience.experience_records'::pg_catalog.regclass
          AND c.relrowsecurity
          AND NOT pg_catalog.pg_has_role(session_user, c.relowner, 'MEMBER')
          AND NOT (SELECT r.rolbypassrls FROM pg_catalog.pg_roles r WHERE r.rolname = session_user))
    THEN
        RAISE EXCEPTION 'agent_experience: no authorization bounds were declared, and row-level security is enabled'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    SET LOCAL agent_experience.purge_authorized = 'on';

    SELECT array_agg(expired.grant_id) INTO v_grant_ids
    FROM (
        SELECT g.grant_id
        FROM agent_experience.experience_grants g
        WHERE g.tenant_id = p_tenant_id
          AND g.application_id = p_application_id
          AND g.project_id = p_project_id
          AND g.team_id IS NOT DISTINCT FROM p_team_id
          AND g.agent_id IS NOT DISTINCT FROM p_agent_id
          AND g.user_id IS NOT DISTINCT FROM p_user_id
          AND (g.expires_at <= v_cutoff
               OR EXISTS (
                   SELECT 1 FROM agent_experience.experience_records r
                   WHERE r.experience_id = g.experience_id AND r.deleted_at IS NOT NULL))
        ORDER BY g.expires_at, g.grant_id
        LIMIT v_limit
        FOR UPDATE) expired;

    IF v_grant_ids IS NULL THEN
        RETURN 0::bigint;
    END IF;

    DELETE FROM agent_experience.experience_grant_events WHERE grant_id = ANY(v_grant_ids);

    WITH removed AS (
        DELETE FROM agent_experience.experience_grants WHERE grant_id = ANY(v_grant_ids) RETURNING 1)
    SELECT count(*) INTO v_purged FROM removed;

    RETURN v_purged;
END
$body$;

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
SET search_path = pg_catalog, agent_experience, pg_temp
SET agent_experience.access_purge_authorized = 'off'
AS $body$
DECLARE
    v_limit integer := least(greatest(coalesce(p_limit, 500), 1), 500);
    v_subtree boolean := coalesce(p_subtree, false);
    v_access_ids uuid[];
    v_purged bigint;
    v_more boolean;
BEGIN
    -- Story 15.1. This function runs as its owner, which row-level security does not bind, so it applies the
    -- declared bounds itself: while a transaction has declared them, a scope outside them is refused before
    -- anything is read. With row-level security enabled, a caller that declared nothing is refused too -- unless the
    -- login role is a member of the tables' owner, or holds BYPASSRLS, neither of which the policies bind either.
    -- With row-level security disabled, an undeclared caller behaves exactly as before.
    IF pg_catalog.current_setting('agent_experience.auth_set', true) = 'on' THEN
        IF NOT agent_experience.rls_scope_admits(p_tenant_id, p_application_id, p_project_id, p_team_id, p_agent_id, p_user_id) THEN
            RAISE EXCEPTION 'agent_experience: the scope is outside the declared authorization bounds'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    ELSIF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        WHERE c.oid = 'agent_experience.experience_records'::pg_catalog.regclass
          AND c.relrowsecurity
          AND NOT pg_catalog.pg_has_role(session_user, c.relowner, 'MEMBER')
          AND NOT (SELECT r.rolbypassrls FROM pg_catalog.pg_roles r WHERE r.rolname = session_user))
    THEN
        RAISE EXCEPTION 'agent_experience: no authorization bounds were declared, and row-level security is enabled'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

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
    -- Story 15.1. This function runs as its owner, which row-level security does not bind, so it applies the
    -- declared bounds itself: while a transaction has declared them, a scope outside them is refused before
    -- anything is read. With row-level security enabled, a caller that declared nothing is refused too -- unless the
    -- login role is a member of the tables' owner, or holds BYPASSRLS, neither of which the policies bind either.
    -- With row-level security disabled, an undeclared caller behaves exactly as before.
    IF pg_catalog.current_setting('agent_experience.auth_set', true) = 'on' THEN
        IF NOT agent_experience.rls_scope_admits(p_tenant_id, p_application_id, p_project_id, p_team_id, p_agent_id, p_user_id) THEN
            RAISE EXCEPTION 'agent_experience: the scope is outside the declared authorization bounds'
                USING ERRCODE = 'insufficient_privilege';
        END IF;
    ELSIF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        WHERE c.oid = 'agent_experience.experience_records'::pg_catalog.regclass
          AND c.relrowsecurity
          AND NOT pg_catalog.pg_has_role(session_user, c.relowner, 'MEMBER')
          AND NOT (SELECT r.rolbypassrls FROM pg_catalog.pg_roles r WHERE r.rolname = session_user))
    THEN
        RAISE EXCEPTION 'agent_experience: no authorization bounds were declared, and row-level security is enabled'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

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

DROP POLICY IF EXISTS rls_records_select ON agent_experience.experience_records;
CREATE POLICY rls_records_select ON agent_experience.experience_records
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

DROP POLICY IF EXISTS rls_records_insert ON agent_experience.experience_records;
CREATE POLICY rls_records_insert ON agent_experience.experience_records
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_records_update ON agent_experience.experience_records;
CREATE POLICY rls_records_update ON agent_experience.experience_records
    FOR UPDATE
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_lifecycle_events_select ON agent_experience.lifecycle_events;
CREATE POLICY rls_lifecycle_events_select ON agent_experience.lifecycle_events
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_lifecycle_events_insert ON agent_experience.lifecycle_events;
CREATE POLICY rls_lifecycle_events_insert ON agent_experience.lifecycle_events
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_confidence_evidence_select ON agent_experience.confidence_evidence;
CREATE POLICY rls_confidence_evidence_select ON agent_experience.confidence_evidence
    FOR SELECT
    USING (
        EXISTS (
            SELECT 1 FROM agent_experience.experience_records r
            WHERE r.experience_id = confidence_evidence.experience_id
              AND r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
              AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
              AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
              AND (r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
              AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
              AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))));

DROP POLICY IF EXISTS rls_confidence_evidence_insert ON agent_experience.confidence_evidence;
CREATE POLICY rls_confidence_evidence_insert ON agent_experience.confidence_evidence
    FOR INSERT
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM agent_experience.experience_records r
            WHERE r.experience_id = confidence_evidence.experience_id
              AND r.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
              AND (r.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
              AND (r.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
              AND (r.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
              AND (r.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
              AND (r.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))));

DROP POLICY IF EXISTS rls_grants_select ON agent_experience.experience_grants;
CREATE POLICY rls_grants_select ON agent_experience.experience_grants
    FOR SELECT
    USING (
        (tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
        OR (recipient_tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (recipient_application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (recipient_project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (recipient_team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (recipient_agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (recipient_user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))
        AND revoked_at IS NULL
        AND expires_at > pg_catalog.clock_timestamp()));

DROP POLICY IF EXISTS rls_grants_insert ON agent_experience.experience_grants;
CREATE POLICY rls_grants_insert ON agent_experience.experience_grants
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_grants_update ON agent_experience.experience_grants;
CREATE POLICY rls_grants_update ON agent_experience.experience_grants
    FOR UPDATE
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_grant_events_select ON agent_experience.experience_grant_events;
CREATE POLICY rls_grant_events_select ON agent_experience.experience_grant_events
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_grant_events_insert ON agent_experience.experience_grant_events;
CREATE POLICY rls_grant_events_insert ON agent_experience.experience_grant_events
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_grant_access_select ON agent_experience.experience_grant_access;
CREATE POLICY rls_grant_access_select ON agent_experience.experience_grant_access
    FOR SELECT
    USING (
        (tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))))
        OR (recipient_tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (recipient_application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (recipient_project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (recipient_team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (recipient_agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (recipient_user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))));

DROP POLICY IF EXISTS rls_grant_access_insert ON agent_experience.experience_grant_access;
CREATE POLICY rls_grant_access_insert ON agent_experience.experience_grant_access
    FOR INSERT
    WITH CHECK (
        recipient_tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (recipient_application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (recipient_project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (recipient_team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (recipient_agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (recipient_user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))
        AND agent_experience.rls_access_grant_live(
            grant_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id,
            recipient_tenant_id, recipient_application_id, recipient_project_id,
            recipient_team_id, recipient_agent_id, recipient_user_id));

DROP POLICY IF EXISTS rls_reuse_feedback_select ON agent_experience.reuse_feedback;
CREATE POLICY rls_reuse_feedback_select ON agent_experience.reuse_feedback
    FOR SELECT
    USING (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_reuse_feedback_insert ON agent_experience.reuse_feedback;
CREATE POLICY rls_reuse_feedback_insert ON agent_experience.reuse_feedback
    FOR INSERT
    WITH CHECK (
        tenant_id = (SELECT agent_experience.rls_bound('tenant'))
        AND (application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
        AND (project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
        AND (team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
        AND (agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
        AND (user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user'))));

DROP POLICY IF EXISTS rls_reuse_feedback_exposures_select ON agent_experience.reuse_feedback_exposures;
CREATE POLICY rls_reuse_feedback_exposures_select ON agent_experience.reuse_feedback_exposures
    FOR SELECT
    USING (
        EXISTS (
            SELECT 1 FROM agent_experience.reuse_feedback f
            WHERE f.feedback_id = reuse_feedback_exposures.feedback_id
              AND f.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
              AND (f.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
              AND (f.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
              AND (f.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
              AND (f.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
              AND (f.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))));

DROP POLICY IF EXISTS rls_reuse_feedback_exposures_insert ON agent_experience.reuse_feedback_exposures;
CREATE POLICY rls_reuse_feedback_exposures_insert ON agent_experience.reuse_feedback_exposures
    FOR INSERT
    WITH CHECK (
        EXISTS (
            SELECT 1 FROM agent_experience.reuse_feedback f
            WHERE f.feedback_id = reuse_feedback_exposures.feedback_id
              AND f.tenant_id = (SELECT agent_experience.rls_bound('tenant'))
              AND (f.application_id = (SELECT agent_experience.rls_bound('application')) OR (SELECT agent_experience.rls_unbounded('application')))
              AND (f.project_id = (SELECT agent_experience.rls_bound('project')) OR (SELECT agent_experience.rls_unbounded('project')))
              AND (f.team_id = (SELECT agent_experience.rls_bound('team')) OR (SELECT agent_experience.rls_unbounded('team')))
              AND (f.agent_id = (SELECT agent_experience.rls_bound('agent')) OR (SELECT agent_experience.rls_unbounded('agent')))
              AND (f.user_id = (SELECT agent_experience.rls_bound('user')) OR (SELECT agent_experience.rls_unbounded('user')))));
