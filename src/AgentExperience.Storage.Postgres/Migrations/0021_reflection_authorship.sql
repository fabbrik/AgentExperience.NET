-- AgentExperience.NET: a record's reflection authorship as a column, so retrieval can leave model-authored records out (Story 14.4).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0019, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHY A COLUMN. Story 14.3 stored a reflection's authorship in the payload only (reflection.authorship, written
-- when it is not 'Deterministic'), and the injection provider dropped model-authored records after retrieval had
-- already applied its limit. Model-authored records could therefore fill the candidate window, and deterministic
-- records below it were never found. The exclusion now runs in the candidate sources' SQL, before their LIMIT, and
-- SQL cannot read a sealed payload. So the authorship is kept beside the payload, as a plaintext flag.
--
-- WHAT THIS SCRIPT DOES.
--
-- 1. experience_records.reflection_model_authored boolean NULL. On a live row, true: the record has a reflection
--    whose authorship is anything but 'Deterministic' (an undefined or future value counts); false: it has a
--    deterministic reflection, or none; NULL: not known. On a tombstone it is the fixed value false, like the
--    tombstone's zeroed counters: content-free, and saying nothing about the erased reflection.
--    It is added with DEFAULT false and the default is dropped straight away: PostgreSQL stores that constant once,
--    in the catalog, so every existing row reads false without being rewritten, and only the rows whose value is
--    not false are written by the backfill (3). A row inserted later without the column -- by a build that predates
--    this script -- gets NULL, not false, because the default is gone.
--
-- 2. agent_experience.payload_reflection_model_authored(payload, payload_version), the one rule:
--    * payload_version 2 (sealed): NULL. This database holds no key, so it cannot say.
--    * any other version: the version-1 reading, which is never laxer than the C# reader. false when the payload's
--      reflection is JSON null, or carries no authorship member, or one that is JSON null, or a string equal to
--      "Deterministic" ignoring ASCII case -- every value the reader takes as Deterministic. true for anything else:
--      an unknown string, a number, an object, a reflection that is not an object, and a payload that is not an
--      object or has no reflection member at all, which the reader cannot read. A version this script does not know
--      (neither 1 nor 2) is read the same way: a payload shaped like version 1 is classified like one, and anything
--      else is true, because a payload no reader can open cannot be vouched for as deterministic.
--
-- 3. The backfill: every live row whose value under the rule is not the false it already reads -- model-authored
--    plaintext rows (true), sealed rows (NULL), rows of an unknown shape (true). Tombstones keep false; nothing here
--    or anywhere may UPDATE a tombstone (0010's guard). In a plaintext deployment that is only the model-authored
--    rows, usually none or few. In a crypto-shredding deployment it is every live record.
--
-- 4. A BEFORE INSERT OR UPDATE trigger that keeps the flag true to the row from now on:
--    * a live row that is not sealed, whenever it is inserted or updated, gets the flag the rule derives from its
--      payload, whatever the writer supplied -- so it cannot be labelled against its own payload, and the store's
--      plaintext insert needs no new column;
--    * a sealed row keeps what its writer supplied: the store sets it from the record's reflection when it seals
--      the record, because the database cannot open it, and 0016's sealing function, which turns a plaintext row
--      into a sealed one, leaves the plaintext row's flag as it was;
--    * a tombstone gets false whoever writes it: the purge's tombstone transition, as 0016's trigger clears
--      search_vector_sealed, so 0010's purge_experience_record does not have to be restated, and any other writer
--      too, whose row 0010's guard and tombstone-shape check then judge as before; a CHECK keeps any other value off a
--      tombstone.
--
-- WHAT THE FILTER DOES WITH NULL. An excluding search leaves out true only. A NULL row -- A SEALED ROW STORED
-- WITHOUT ITS FLAG -- stays a candidate and takes a place in the source's candidate window; the retrieval service
-- opens it, sees the authorship, and excludes it (and the injection provider checks again on its re-read). Such rows
-- are those sealed before this script, those an instance still running the previous build seals during a rolling
-- deploy (until every instance runs this version, newly sealed rows may be unflagged), and any a writer inserts
-- without the flag. A host can find them,
--
--     SELECT experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id
--     FROM agent_experience.experience_records
--     WHERE deleted_at IS NULL AND payload_version = 2 AND reflection_model_authored IS NULL;
--
-- read each through the store (which opens it), and revoke, supersede or erase the model-authored ones.
--
-- WHAT IS STORED IN THE CLEAR. In crypto-shredding mode the flag is plaintext metadata, like the status and the
-- confidence: one bit saying whether a model wrote the sealed lesson. Erasure resets it on the live row; backups,
-- WAL and dead tuples keep it, as they keep the status.
--
-- LOCKS, AND WHAT THE BACKFILL REWRITES. ADD COLUMN takes an ACCESS EXCLUSIVE lock on experience_records, and the
-- migrator runs this whole script in one transaction, so that lock -- which blocks every read and write of the
-- table, and queues behind any running reader -- is held until the backfill commits. Every row the backfill
-- updates is written as a new tuple, and PostgreSQL recomputes the stored generated search_vector (0003) for it,
-- which costs about what re-inserting the record's text costs; the old tuples are dead until VACUUM. The script also
-- runs under the data source's command timeout (30 seconds by default): a backfill that does not finish inside it
-- fails the script, which rolls back and changes nothing.
--
-- So first count what the backfill would rewrite:
--
--     SELECT count(*) FROM agent_experience.experience_records
--     WHERE deleted_at IS NULL AND (payload_version <> 1 OR payload -> 'reflection' ? 'authorship');
--
-- (an upper bound: it counts every sealed row and every plaintext row with an authorship member). Up to about
-- 20,000 rows, let the migrator run the script. Above that, or whenever the table cannot be locked for the time it
-- takes -- as a rough guide, a few thousand rows a second, slower with long task text -- use the two-step route:
--
--   Step 1, by hand, as the owner: run this script with the backfill UPDATE removed. It holds ACCESS EXCLUSIVE for
--   an instant only (nothing is rewritten). From then on every row is written with its flag.
--   Step 2, by hand, in batches of 1,000 to 5,000 rows, each its own transaction (row locks only, each batch well
--   inside any timeout), walking experience_id in order: take the batch's upper bound
--
--     SELECT max(experience_id) FROM (SELECT experience_id FROM agent_experience.experience_records
--         WHERE experience_id > :last ORDER BY experience_id LIMIT 1000) b;
--
--   then rewrite only what differs inside it, and repeat from that bound until it returns NULL:
--
--     UPDATE agent_experience.experience_records r
--     SET reflection_model_authored = agent_experience.payload_reflection_model_authored(r.payload, r.payload_version)
--     WHERE r.experience_id > :last AND r.experience_id <= :bound AND r.deleted_at IS NULL
--       AND r.reflection_model_authored IS DISTINCT FROM
--           agent_experience.payload_reflection_model_authored(r.payload, r.payload_version);
--
--   Then run the migrator: it re-applies the script, whose UPDATE now finds nothing, and journals it. Between the
--   steps, rows not yet backfilled read false: a model-authored plaintext row is then not left out by SQL (the
--   retrieval service still excludes it), and an unflagged sealed row is not yet found by the query above.
--
-- No index is built: the flag is a filter on rows the search, scope and HNSW indexes already select, and a boolean
-- splits a table in two at best, so the planner would not use one.
--
-- THE CHECK IS NOT VALID, like 0016's: every existing row satisfies it, but validating would scan the table. Validate
-- at a time of your choosing (SHARE UPDATE EXCLUSIVE only):
--
--     ALTER TABLE agent_experience.experience_records VALIDATE CONSTRAINT experience_records_authorship_only_when_live;
--
-- PRIVILEGES AND ROW-LEVEL SECURITY. No table is added, so the application role's manifest is unchanged: its
-- table-level INSERT and SELECT cover the column, and its column-level UPDATE does not name it. 0019's policies are
-- per row and cover the column as they cover every other. Both functions are SECURITY INVOKER with PUBLIC's default
-- EXECUTE, like 0016's trigger function and 0019's helpers.

ALTER TABLE agent_experience.experience_records
    ADD COLUMN IF NOT EXISTS reflection_model_authored boolean NULL DEFAULT false;

ALTER TABLE agent_experience.experience_records
    ALTER COLUMN reflection_model_authored DROP DEFAULT;

CREATE OR REPLACE FUNCTION agent_experience.payload_reflection_model_authored(p_payload jsonb, p_payload_version integer)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
    SELECT CASE
        WHEN p_payload_version = 2 THEN NULL
        WHEN jsonb_typeof(p_payload) IS DISTINCT FROM 'object' OR NOT (p_payload ? 'reflection') THEN true
        WHEN jsonb_typeof(p_payload -> 'reflection') = 'null' THEN false
        WHEN jsonb_typeof(p_payload -> 'reflection') <> 'object' THEN true
        WHEN coalesce(jsonb_typeof(p_payload -> 'reflection' -> 'authorship'), 'null') = 'null' THEN false
        WHEN jsonb_typeof(p_payload -> 'reflection' -> 'authorship') = 'string'
            AND translate(p_payload -> 'reflection' ->> 'authorship', 'DETRMINSC', 'detrminsc') = 'deterministic' THEN false
        ELSE true
    END;
$body$;

UPDATE agent_experience.experience_records r
SET reflection_model_authored = agent_experience.payload_reflection_model_authored(r.payload, r.payload_version)
WHERE r.deleted_at IS NULL
  AND r.reflection_model_authored IS DISTINCT FROM
      agent_experience.payload_reflection_model_authored(r.payload, r.payload_version);

DO $body$
BEGIN
    -- A tombstone carries no reflection, so it carries only the fixed false (the trigger below sets it).
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_records_authorship_only_when_live'
          AND conrelid = 'agent_experience.experience_records'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_records
            ADD CONSTRAINT experience_records_authorship_only_when_live
            CHECK (deleted_at IS NULL OR reflection_model_authored IS FALSE)
            NOT VALID;
    END IF;
END
$body$;

-- Keeps the flag true to the row (item 4 above). It only ever writes this one column.
CREATE OR REPLACE FUNCTION agent_experience.maintain_record_authorship() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, agent_experience, pg_temp
AS $body$
BEGIN
    IF NEW.deleted_at IS NOT NULL THEN
        NEW.reflection_model_authored := false;
    ELSIF NEW.payload_version IS DISTINCT FROM 2 THEN
        NEW.reflection_model_authored := agent_experience.payload_reflection_model_authored(NEW.payload, NEW.payload_version);
    END IF;

    RETURN NEW;
END;
$body$;

DO $body$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgname = 'experience_records_authorship'
          AND tgrelid = 'agent_experience.experience_records'::regclass)
    THEN
        CREATE TRIGGER experience_records_authorship
            BEFORE INSERT OR UPDATE ON agent_experience.experience_records
            FOR EACH ROW EXECUTE FUNCTION agent_experience.maintain_record_authorship();
    END IF;
END
$body$;

ALTER TABLE agent_experience.experience_records ENABLE ALWAYS TRIGGER experience_records_authorship;
