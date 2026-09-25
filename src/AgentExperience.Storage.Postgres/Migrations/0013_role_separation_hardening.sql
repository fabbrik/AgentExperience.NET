-- AgentExperience.NET: hardening for the two-role deployment (Story 6.1, KL-4).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0012, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHAT KL-4 WAS. 0006, 0010 and 0012 each said it plainly: the append-only guards do not bind a role that can
-- ALTER TABLE, and the purge markers are custom GUCs any session can set. In a deployment where the
-- application runs the migrator, the application role OWNS every table here, so it could disable a trigger,
-- or -- holding DELETE -- set the marker by hand and delete from a ledger directly.
--
-- WHAT CLOSES IT IS NOT IN THIS SCRIPT. It is a deployment shape: an owner role owns this schema and runs
-- the migrators, and a separate application role holds only what the stores need, applied by
-- ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync. That role has no ownership (so no ALTER
-- TABLE), no DELETE or TRUNCATE on any ledger or on experience_records (so a hand-set marker admits
-- nothing: the privilege check refuses the statement before any trigger runs), and UPDATE on
-- experience_records only on the projection columns (so it cannot write 0010's tombstone shape by hand
-- either). Only the SECURITY DEFINER purge functions, running as the owner, can delete -- and only a host
-- that opts in grants the application role EXECUTE on them.
--
-- The privilege set is NOT granted here, deliberately. A migration runs once and is journaled, so it could
-- not re-apply grants to objects a later migration adds; the role name is host configuration, and this
-- migrator runs with variable substitution off. The API revokes everything from the role and re-grants
-- the manifest on every call, then verifies the role's effective privileges, so it is safe to run on every
-- deploy and fails closed on any object it does not know.
--
-- WHAT THIS SCRIPT DOES. Two things, both about functions, neither restating a body or recreating a
-- trigger binding.
--
-- 1. search_path, with pg_temp explicitly last. PostgreSQL's own guidance for SECURITY DEFINER functions
--    is to list pg_temp last: when it is not listed, the session's temporary schema is searched FIRST for
--    relations. Every relation the three purge functions name is schema-qualified, so this was not
--    exploitable -- but the two-role deployment makes these functions the only path from the application
--    role to a DELETE, so they get the documented form rather than an argument about why the other form
--    happens to be safe. The invoker trigger functions that pinned nothing (enforce_grant_monotonicity,
--    reject_audited_grant_delete, enforce_record_projection, reject_record_removal,
--    reject_future_grant_issue) are pinned the same way: they run as the writing role, and comparison
--    operators resolve through the writer's search_path, so a writer with CREATE on some schema could put
--    its own operator ahead of pg_catalog and walk a guard's comparison. The application role has CREATE
--    on no schema here, but a database created before PostgreSQL 15 may still give PUBLIC CREATE on
--    "public"; pinning removes the question. reject_event_log_mutation was pinned by 0012 without pg_temp
--    and is re-pinned with it.
--
--    ALTER FUNCTION ... SET replaces only that one setting: the purge functions' own
--    SET agent_experience.purge_authorized / access_purge_authorized = 'off' clauses are untouched.
--
-- 2. EXECUTE on the three purge functions revoked from PUBLIC again. 0010 and 0012 did this and
--    CREATE OR REPLACE preserves an ACL, so on a journaled database this is a no-op; it is restated so that
--    a database whose ACL was widened by hand since is narrowed back by the next migration, and so the
--    privilege API's verification (which refuses a SECURITY DEFINER function the application role can
--    execute without an opt-in) never trips over a PUBLIC grant this schema itself could have removed.
--    The grant to the migrating role that 0010 and 0012 made is left alone: the owner can always execute
--    its own function anyway.
--
-- WHAT STILL DOES NOT BIND, AND CANNOT. A superuser bypasses every privilege check, and the owner role can
-- ALTER TABLE, disable or drop a trigger, and replace a function. That is inherent in PostgreSQL. A
-- deployment where the application role IS the owner -- the single-role shape every earlier script
-- assumed -- gets none of this; it is for local development and tests only.
--
-- ALTER FUNCTION needs ownership of each function, which the migrating role has when it created them. It
-- takes no table lock.

ALTER FUNCTION agent_experience.purge_experience_record(
    uuid, text, text, text, text, text, text, bigint, timestamptz)
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.purge_expired_grants(
    text, text, text, text, text, text, timestamptz, integer)
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.purge_grant_access(
    text, text, text, text, text, text, boolean, timestamptz, integer)
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.reject_event_log_mutation()
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.enforce_grant_monotonicity()
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.reject_audited_grant_delete()
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.enforce_record_projection()
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.reject_record_removal()
    SET search_path = pg_catalog, agent_experience, pg_temp;

ALTER FUNCTION agent_experience.reject_future_grant_issue()
    SET search_path = pg_catalog, agent_experience, pg_temp;

REVOKE ALL ON FUNCTION agent_experience.purge_experience_record(
    uuid, text, text, text, text, text, text, bigint, timestamptz) FROM PUBLIC;

REVOKE ALL ON FUNCTION agent_experience.purge_expired_grants(
    text, text, text, text, text, text, timestamptz, integer) FROM PUBLIC;

REVOKE ALL ON FUNCTION agent_experience.purge_grant_access(
    text, text, text, text, text, text, boolean, timestamptz, integer) FROM PUBLIC;
