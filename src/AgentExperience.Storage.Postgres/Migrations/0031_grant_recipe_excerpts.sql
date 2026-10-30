-- AgentExperience.NET: a grant's consent to show a borrowed record's recipe excerpts (Story 28.4).
-- Applied by ExperienceSchemaMigrator and recorded in agent_experience.schema_versions.
-- Every statement is idempotent on purpose, matching 0001-0030, so a database whose schema was applied by hand can
-- still be journaled. Do not edit this script once it has been journaled anywhere; add the next-numbered script instead.
--
-- WHAT IT GOVERNS. Since 28.4 a tool call may carry a recipe excerpt: the short part of a working call's result its
-- host vouched for, which injection shows on the record's Worked: attempt when the reader allowlists the tool. A
-- borrowed record shows one only when the OWNER consented on the grant, by tool name: recipe_excerpts holds those
-- names, and the reader's own allowlist can only narrow them. It governs only what the MAF adapter renders; a store
-- returns the record complete.
--
-- UPGRADING CHANGES NOTHING THAT IS SHOWN. Every stored grant keeps recipe_excerpts NULL, which consents to no excerpt.
-- The column is immutable, like the level: revoke the grant and issue a replacement to consent.
--
-- DEPLOYMENT ORDER: RUN THIS SCRIPT BEFORE ISSUING A GRANT THAT NAMES RECIPE EXCERPTS. The build that knows the column
-- names it in an insert only when the request sets it, and reads it through to_jsonb(g), which is NULL on a schema
-- without it, so every other grant write and every read keeps working against a schema this script has not reached.
-- An older build against this schema keeps working too: it never names the column, and a grant it writes has it NULL.
--
-- WHAT recipe_excerpts HOLDS: a non-empty JSON array of tool names. Names only, never an excerpt. It is host
-- configuration rather than captured content, so it is stored in the clear in encrypted mode too, like 0017's
-- approach_arguments; an owner must not put anything secret into a tool name. It is deleted with its grant.
--
-- THE CHECKS ARE NOT VALID, like 0017's: every existing row has the column NULL, so both hold, and they bind every row
-- written from now on. The application role's table-level SELECT and INSERT on experience_grants cover the new column,
-- and its UPDATE is only on revoked_at and revocation_reason, so the privilege manifest needs no new entry.
--
-- ALTER TABLE ... ADD COLUMN of a nullable column without a default never rewrites the table.

ALTER TABLE agent_experience.experience_grants
    ADD COLUMN IF NOT EXISTS recipe_excerpts jsonb NULL;

DO $body$
BEGIN
    -- Consent to show an excerpt belongs to a level that shows the approach the excerpt sits on.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grants_recipe_excerpts_level'
          AND conrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grants
            ADD CONSTRAINT experience_grants_recipe_excerpts_level
            CHECK (recipe_excerpts IS NULL OR disclosure IN ('LessonAndApproach', 'LessonApproachAndArguments'))
            NOT VALID;
    END IF;

    -- Its shape: a non-empty array of strings. The adapter validates the names themselves before it writes; this is
    -- the floor under a writer that bypasses it.
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'experience_grants_recipe_excerpts_shape'
          AND conrelid = 'agent_experience.experience_grants'::regclass)
    THEN
        ALTER TABLE agent_experience.experience_grants
            ADD CONSTRAINT experience_grants_recipe_excerpts_shape
            CHECK (recipe_excerpts IS NULL OR (
                jsonb_typeof(recipe_excerpts) = 'array'
                AND jsonb_array_length(recipe_excerpts) > 0
                AND NOT jsonb_path_exists(recipe_excerpts, 'strict $[*] ? (@.type() != "string")', '{}'::jsonb, true)) IS TRUE)
            NOT VALID;
    END IF;
END
$body$;

-- THE CONSENT IS IMMUTABLE, like the level and 0017's allowlist. It joins the identity pins of the monotonicity trigger.
-- 0017's body is restated here in full with the one added pin; CREATE OR REPLACE keeps every trigger binding, so the
-- table is never unguarded, and resets the function's SET clauses, so 0013's search_path pin is restated after it.
CREATE OR REPLACE FUNCTION agent_experience.enforce_grant_monotonicity() RETURNS trigger AS $body$
BEGIN
    IF NEW.grant_id IS DISTINCT FROM OLD.grant_id
        OR NEW.experience_id IS DISTINCT FROM OLD.experience_id
        OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
        OR NEW.application_id IS DISTINCT FROM OLD.application_id
        OR NEW.project_id IS DISTINCT FROM OLD.project_id
        OR NEW.team_id IS DISTINCT FROM OLD.team_id
        OR NEW.agent_id IS DISTINCT FROM OLD.agent_id
        OR NEW.user_id IS DISTINCT FROM OLD.user_id
        OR NEW.recipient_tenant_id IS DISTINCT FROM OLD.recipient_tenant_id
        OR NEW.recipient_application_id IS DISTINCT FROM OLD.recipient_application_id
        OR NEW.recipient_project_id IS DISTINCT FROM OLD.recipient_project_id
        OR NEW.recipient_team_id IS DISTINCT FROM OLD.recipient_team_id
        OR NEW.recipient_agent_id IS DISTINCT FROM OLD.recipient_agent_id
        OR NEW.recipient_user_id IS DISTINCT FROM OLD.recipient_user_id
        OR NEW.reason IS DISTINCT FROM OLD.reason
        OR NEW.administrator_principal_id IS DISTINCT FROM OLD.administrator_principal_id
        OR NEW.issued_at IS DISTINCT FROM OLD.issued_at
        OR NEW.disclosure IS DISTINCT FROM OLD.disclosure
        OR NEW.approach_arguments IS DISTINCT FROM OLD.approach_arguments
        OR NEW.recipe_excerpts IS DISTINCT FROM OLD.recipe_excerpts
    THEN
        RAISE EXCEPTION
            'A grant names one record, one recipient, one disclosure level and one argument allowlist for the life of the grant; issue a new grant instead.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF OLD.revoked_at IS NOT NULL AND NEW.revoked_at IS DISTINCT FROM OLD.revoked_at THEN
        RAISE EXCEPTION
            'A grant''s revocation is permanent: revoked_at cannot be cleared or changed once set.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF OLD.revocation_reason IS NOT NULL AND NEW.revocation_reason IS DISTINCT FROM OLD.revocation_reason THEN
        RAISE EXCEPTION
            'A grant''s revocation reason is part of the audit trail and cannot be changed once set.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    IF NEW.expires_at > OLD.expires_at THEN
        RAISE EXCEPTION
            'A grant''s expiry cannot be extended; issue a new grant instead.'
            USING ERRCODE = 'insufficient_privilege';
    END IF;

    RETURN NEW;
END;
$body$ LANGUAGE plpgsql;

ALTER FUNCTION agent_experience.enforce_grant_monotonicity()
    SET search_path = pg_catalog, agent_experience, pg_temp;
