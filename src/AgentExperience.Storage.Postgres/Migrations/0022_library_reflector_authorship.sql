-- AgentExperience.NET: the library's own model-backed reflector counts as model-authored, whatever authorship its
-- record declares (Story 17.1).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0021, so a database whose schema was applied by
-- hand can still be journaled. Do not edit this script once it has been journaled anywhere; add the
-- next-numbered script instead.
--
-- WHY. The MAF adapter's ChatClientExperienceReflector (story 14.2) has a model write every reflection it returns,
-- but before story 14.3 it left the reflection's authorship at its default, Deterministic. Its records say who wrote
-- them all the same: their reflection's producer starts with 'AgentExperience.ChatClientExperienceReflector/'. The
-- library now decides authorship by one rule everywhere (ReflectionAuthorshipRule in the C# source): model-authored
-- when the authorship is anything but Deterministic, or when the producer starts with that prefix, compared
-- ordinally. Only the library's own prefix is recognised; a third-party reflector's authorship is what it declared.
--
-- WHAT THIS SCRIPT DOES.
--
-- 1. Replaces 0021's agent_experience.payload_reflection_model_authored(payload, payload_version) with the same rule
--    plus two arms: a plaintext payload whose reflection is an object with a string producer starting with the prefix
--    is true, and so is one whose producer is missing, JSON null or not a string, which the reader refuses (fail
--    closed, like 0021's other unreadable shapes). Every other answer is 0021's. A sealed payload
--    (version 2) is still NULL: this database holds no key. 0021's trigger calls the function by name, so every plaintext write from now on is classified by the new rule.
--
-- 2. Recomputes the flag on every live plaintext row whose stored value differs from the new rule: in practice the
--    library reflector's records written by a build between stories 14.2 and 14.3, which read false until now. No
--    published release wrote any: the reflector shipped in 0.1.0-preview.5, the release that also made it declare
--    Model authorship, so on a database only published releases wrote this usually rewrites nothing. Sealed rows
--    (version 2) and tombstones are not touched. Running it again finds nothing to change.
--
-- WHAT IT DOES NOT DO: SEALED ROWS. A sealed row's payload cannot be read here, so neither a sealed library-reflector
-- record nor a row sealed without its flag (before 0021, or by an instance on an earlier build during a rolling
-- deploy) can be classified by SQL. Excluding searches now leave a NULL flag out (fail closed: unknown counts as
-- model-authored), and the owner-run PostgresExperienceRecordStore.BackfillSealedAuthorshipAsync opens each such row
-- with its record key and writes its flag by the same rule. Run it after upgrading; see
-- docs/guide/crypto-shredding.md. A sealed row that already carries false although its producer is the library
-- reflector's (a plaintext record 0021 flagged false and the upgrade job then sealed, before this script ran) is not
-- revisited by that job either. No published release can contain one -- every record a published release's reflector
-- wrote declares Model -- and the retrieval service and the injection provider would still exclude it once opened.
--
-- PRIVILEGES AND LOCKS. No table, column, index or function signature is added, so the application role's manifest
-- is unchanged; the function stays SECURITY INVOKER with PUBLIC's default EXECUTE, like 0021's. CREATE OR REPLACE
-- keeps its owner and ACL. The UPDATE takes row locks on the rows it rewrites only (and reads every live plaintext
-- payload to find them), under the migrator's one transaction and its command timeout (30 seconds by default): on a
-- large plaintext table, run the migrator in a maintenance window, or with a data source whose command timeout is
-- raised for the migration; a script that times out rolls back and changes nothing.

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
        WHEN jsonb_typeof(p_payload -> 'reflection' -> 'producer') IS DISTINCT FROM 'string' THEN true
        WHEN left(p_payload -> 'reflection' ->> 'producer', 46) = 'AgentExperience.ChatClientExperienceReflector/' THEN true
        WHEN coalesce(jsonb_typeof(p_payload -> 'reflection' -> 'authorship'), 'null') = 'null' THEN false
        WHEN jsonb_typeof(p_payload -> 'reflection' -> 'authorship') = 'string'
            AND translate(p_payload -> 'reflection' ->> 'authorship', 'DETRMINSC', 'detrminsc') = 'deterministic' THEN false
        ELSE true
    END;
$body$;

UPDATE agent_experience.experience_records r
SET reflection_model_authored = agent_experience.payload_reflection_model_authored(r.payload, r.payload_version)
WHERE r.deleted_at IS NULL
  AND r.payload_version IS DISTINCT FROM 2
  AND r.reflection_model_authored IS DISTINCT FROM
      agent_experience.payload_reflection_model_authored(r.payload, r.payload_version);
