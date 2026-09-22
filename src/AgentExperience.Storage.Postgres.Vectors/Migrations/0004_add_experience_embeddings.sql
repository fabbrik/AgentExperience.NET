-- AgentExperience.NET: derived embeddings over Experience Records, for hybrid retrieval (Story 2.6).
-- Applied by ExperienceVectorSchemaMigrator -- this package's own migrator -- and recorded in
-- agent_experience.schema_versions alongside the base adapter's scripts. It is deliberately NOT in the
-- base adapter's script list: CREATE EXTENSION vector needs a superuser, because pgvector is not a
-- trusted extension, and a text-only deployment must never be made to run it for a feature it has not
-- enabled. Run the base migration first: the foreign key below needs experience_records to exist.
-- Every statement is IF NOT EXISTS on purpose, matching 0001-0003, so a database whose schema was
-- applied by hand can still be journaled. Do not edit this script once it has been journaled anywhere;
-- add the next-numbered script instead.
--
-- This script is append-only with respect to 0001-0003: it creates the pgvector extension and one new
-- table, and alters nothing that already exists. The canonical record table is untouched, so every
-- write path -- the store's INSERT and its lifecycle UPDATE -- is unaffected, and a deployment that
-- never indexes anything simply has an empty table.

CREATE EXTENSION IF NOT EXISTS vector;

-- One row per indexed record. The embedding is derived data: the canonical record is created,
-- committed, and text-searchable whether or not a row ever appears here, and nothing in this table
-- takes part in a lifecycle decision.
--
-- The scope columns are denormalized from the record on purpose, so the scope predicate a search
-- applies can be decided from this table's own index rather than only after the join. They are
-- written from the record row inside the conditional INSERT ... SELECT, never from caller input, so
-- they cannot disagree with the record they copy.
--
-- model_id, dimension, content_hash, and source_revision are what the embedding *is*, kept separate
-- from lifecycle state (status, revision, reuse confidence all stay on the record):
--   * model_id + dimension decide comparability -- a query vector is only ever compared with vectors
--     from the same model at the same width;
--   * content_hash is SHA-256 over the model ID and the normalized summary that was embedded, so a
--     re-index can skip an unchanged record without calling a provider at all;
--   * source_revision is the record revision the summary was read at, which makes every write
--     conditional: a write only lands while the record is still at exactly that revision.
--
-- The embedding column is an UNCONSTRAINED `vector`, not `vector(n)`. The dimension is a property of
-- whichever model a host configured, which this schema cannot know, and a typmod would have to be
-- chosen here and then be wrong for everyone else. The dimension is carried in its own column and
-- checked in the search predicate instead; the dimension-specific HNSW index is created out of band
-- (see ExperienceVectorIndexMaintenance) because it, too, needs a dimension this script does not have.
--
-- ON DELETE CASCADE: an embedding may never outlive the record it describes. Together with the
-- conditional INSERT ... SELECT, which can only insert a row whose record currently exists, this is
-- what makes "a deleted record can never be recreated by an in-flight write" true of the schema and
-- not only of the adapter.
CREATE TABLE IF NOT EXISTS agent_experience.experience_embeddings (
    experience_id uuid PRIMARY KEY
        REFERENCES agent_experience.experience_records (experience_id) ON DELETE CASCADE,
    tenant_id text NOT NULL,
    application_id text NOT NULL,
    project_id text NOT NULL,
    team_id text NULL,
    agent_id text NULL,
    user_id text NULL,
    model_id text NOT NULL,
    dimension integer NOT NULL,
    content_hash text NOT NULL,
    source_revision bigint NOT NULL,
    embedding vector NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ck_experience_embeddings_dimension CHECK (dimension > 0),
    CONSTRAINT ck_experience_embeddings_source_revision CHECK (source_revision >= 0),
    -- The stored vector and the dimension column can never disagree. The search filters on `dimension`
    -- and then casts `embedding::vector(n)`; without this constraint a row written outside this adapter
    -- could claim a width it does not have, and the cast would raise mid-query on a row the predicate
    -- had already admitted. It also keeps the partial HNSW index's own expression buildable.
    CONSTRAINT ck_experience_embeddings_vector_dims CHECK (vector_dims(embedding) = dimension)
);

-- The three required scope columns plus the two comparability columns: exactly the predicate a
-- scoped vector search applies before it ever computes a distance, so an incomparable or foreign-scope
-- row is never read. The optional scope columns are deliberately absent for the same reason as in
-- 0003: they are matched with IS NOT DISTINCT FROM, which is not an indexable btree operator.
CREATE INDEX IF NOT EXISTS ix_experience_embeddings_scope_model
    ON agent_experience.experience_embeddings
       (tenant_id, application_id, project_id, model_id, dimension);

-- A re-index pass walks a scope's records in a stable order and pages through them; the record table's
-- own scope index serves that walk, so nothing else is needed here.
