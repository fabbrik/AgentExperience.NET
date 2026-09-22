-- AgentExperience.NET: full-text search over Experience Records, for text retrieval (Story 2.2).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is IF NOT EXISTS on purpose, matching 0001 and 0002, so a database whose schema was
-- applied by hand can still be journaled. Do not edit this script once it has been journaled anywhere;
-- add the next-numbered script instead. (This script is still unreleased and has only ever been applied
-- to throwaway test databases, so the length bound below was added in place during review; once this
-- branch ships, the append-only rule applies to it as it does to 0001.)
--
-- search_vector is a GENERATED ... STORED column, not a trigger and not a column the store writes: it is
-- derived from columns and payload fields that already exist, so it can never disagree with the record it
-- indexes, and no write path has to remember to maintain it. That also means the store's INSERT and its
-- lifecycle UPDATE are unchanged -- PostgreSQL recomputes the vector itself.
--
-- The indexed text is deliberately narrow: the task identifier, the sanitized task summary, and the
-- reflection's lesson. Those are the fields that say what a record is *about*. Attempts, tool calls,
-- evidence, and environment metadata are not indexed: they are operational detail, they would flood the
-- vector with identifiers and stack-trace-like fragments, and matching on them would make retrieval
-- recall incidental strings rather than applicable experience.
--
-- to_tsvector's two-argument form is used with a literal configuration ('english'), which is IMMUTABLE and
-- therefore legal in a generated column; the one-argument form depends on default_text_search_config and is
-- only STABLE. Changing the configuration later means a new script that rebuilds the column, because every
-- already-indexed row would otherwise keep the old analysis.
--
-- The concatenated text is bounded with left(): a tsvector may not exceed 1 MB, and a record with a very
-- long task summary or lesson would otherwise make to_tsvector raise -- which, in a generated column, is
-- not a search failure but a failed INSERT (and a failed migration on a table that already holds such a
-- row). 100k characters is far more than any realistic summary and analyzes to well under the ceiling, so
-- the bound only ever truncates text that would have broken the write.

ALTER TABLE agent_experience.experience_records
    ADD COLUMN IF NOT EXISTS search_vector tsvector
    GENERATED ALWAYS AS (
        to_tsvector(
            'english',
            left(
                coalesce(task_id, '') || ' ' ||
                coalesce(payload ->> 'taskSummary', '') || ' ' ||
                coalesce(payload -> 'reflection' ->> 'lesson', ''),
                100000))
    ) STORED;

-- GIN, not GiST: the vector is read far more often than it is written (a record's text never changes after
-- it is created -- only status, revision, and updated_at do), and GIN answers @@ lookups faster.
CREATE INDEX IF NOT EXISTS ix_experience_records_search
    ON agent_experience.experience_records USING GIN (search_vector);

-- The scope columns are the first predicate every search applies, and a tenant's records are a small
-- fraction of the table, so this composite index keeps the text match from scanning foreign scopes. It
-- carries status and reuse_confidence so the status and confidence filters are decided from the index too.
--
-- Only the three required scope columns are indexed. team_id, agent_id, and user_id are matched with
-- IS NOT DISTINCT FROM, which is not an indexable btree operator, so adding them would not help: a
-- deployment that scopes records by team, agent, or user still scans every row of its project and filters
-- those three in memory. That is acceptable while a project's record count is modest; a deployment that
-- leans heavily on the optional scope fields should add its own partial or expression index.
--
-- 0001's index on (tenant_id, application_id, project_id) is now a prefix of this one and therefore
-- redundant. It is deliberately left in place: scripts are append-only, and dropping an index 0001 created
-- would rewrite history for every database that already applied it. The cost is one extra index to
-- maintain on write, which is small next to the rewrite risk.
CREATE INDEX IF NOT EXISTS ix_experience_records_scope_status_confidence
    ON agent_experience.experience_records
       (tenant_id, application_id, project_id, status, reuse_confidence);
