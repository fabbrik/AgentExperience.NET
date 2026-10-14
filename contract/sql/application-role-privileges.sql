-- contract 1.1: the privileges ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync gives the application role,
-- as the statements it runs on a schema the migrator has just applied, with the default options (no erasure, no
-- access-log purge, no sealing). Run them as the schema's owner, in one transaction, after the migrations.
-- The role: every :"application_role" is the application role's name as a quoted identifier (psql substitutes it with
-- -v application_role=<name>; elsewhere, replace it with the role's quote_ident). The role must already exist.
-- Modes: the statements under "-- mode: always" run in both modes; those under "-- mode: row-level-security" run only
-- with EnableRowLevelSecurity = true, the configuration a contract 1.x port runs against (without it the application
-- role may not execute the text search function). Every statement is one line ending in ";".
-- Not here: with row-level security, the .NET method also re-creates the policies and helper functions migration 0019
-- created (on a freshly migrated schema that changes nothing), and then verifies the role holds exactly these
-- privileges. The optional agent_experience.experience_embeddings table (the vectors package) is out of contract scope.
-- mode: always
REVOKE ALL ON SCHEMA agent_experience FROM :"application_role";
REVOKE ALL ON ALL TABLES IN SCHEMA agent_experience FROM :"application_role";
REVOKE ALL ON ALL SEQUENCES IN SCHEMA agent_experience FROM :"application_role";
REVOKE ALL ON ALL ROUTINES IN SCHEMA agent_experience FROM :"application_role";
GRANT USAGE ON SCHEMA agent_experience TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.experience_records TO :"application_role";
GRANT UPDATE (contradictions, reuse_confidence, revision, status, supporting_validations, updated_at) ON agent_experience.experience_records TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.experience_grants TO :"application_role";
GRANT UPDATE (revocation_reason, revoked_at) ON agent_experience.experience_grants TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.lifecycle_events TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.experience_grant_events TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.confidence_evidence TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.reuse_feedback TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.reuse_feedback_exposures TO :"application_role";
GRANT SELECT, INSERT ON agent_experience.experience_grant_access TO :"application_role";
-- mode: row-level-security
GRANT EXECUTE ON FUNCTION agent_experience.search_experience_text(text, text, text, text, text, text, text, text[], double precision, integer, boolean, boolean, integer) TO :"application_role";
ALTER TABLE agent_experience.experience_records ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_experience.experience_grants ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_experience.lifecycle_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_experience.experience_grant_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_experience.confidence_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_experience.reuse_feedback ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_experience.reuse_feedback_exposures ENABLE ROW LEVEL SECURITY;
ALTER TABLE agent_experience.experience_grant_access ENABLE ROW LEVEL SECURITY;
