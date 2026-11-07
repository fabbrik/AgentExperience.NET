-- AgentExperience.NET: an index on each record's source run, per tenant (Story 25.10).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0027, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT THIS IS. Two reads look a run's own record up by experience_records.source_run_id: each page of the holdout's
-- outcome read (0027's rows joined to their runs' records), and the holdout write's check for the run's record, which it
-- locks so that an erasure of that record serialises with the write. Neither had an index to use, so each scanned the
-- tenant's records. This adds one on (tenant_id, source_run_id): both reads name the tenant and the run. Nothing else
-- changes: no column, constraint, trigger, policy or grant. A tombstone's source_run_id is zeroed (0010), so erased
-- records gather under one key per tenant and are never what either read looks for.
--
-- LOCKS. CREATE INDEX without CONCURRENTLY, because the migrator runs each script in a transaction and CONCURRENTLY
-- cannot run inside one. It scans experience_records under a SHARE lock, which blocks every write to the table (reads
-- go on) for the length of the build: imperceptible on a fresh database, a write outage on one with a long record
-- history. A deployment that cannot take that should build the index out of band before running this script, and IF
-- NOT EXISTS then makes this script's statement a no-op:
--
--     CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_experience_records_source_run
--         ON agent_experience.experience_records (tenant_id, source_run_id);
--
-- A CONCURRENTLY build that fails leaves an INVALID index under that name, which IF NOT EXISTS would then accept: drop
-- it (DROP INDEX CONCURRENTLY agent_experience.ix_experience_records_source_run) and build it again.

CREATE INDEX IF NOT EXISTS ix_experience_records_source_run
    ON agent_experience.experience_records (tenant_id, source_run_id);
