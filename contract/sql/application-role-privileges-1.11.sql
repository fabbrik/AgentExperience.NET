-- contract 1.11: the application role's privileges on agent_experience.experience_provider_holdout_assignments
-- (migration 0029), which sql/record-provider-holdout-assignment.sql writes and sql/query-provider-holdout-outcomes.sql
-- reads. Run them after application-role-privileges.sql (whose REVOKE ALL removes them) and the 1.4, 1.6 and 1.8 files,
-- as the schema's owner, in the same transaction or a later one, with the same :"application_role" substitution and the
-- same modes. They are exactly what ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants on that table
-- with the default options. Every statement is one line ending in ";". There is no UPDATE (the insert's ON CONFLICT DO
-- NOTHING updates nothing) and no DELETE (an erasure removes the rows through 0029's trigger, as the owner).
-- mode: always
GRANT SELECT, INSERT ON agent_experience.experience_provider_holdout_assignments TO :"application_role";
-- mode: row-level-security
ALTER TABLE agent_experience.experience_provider_holdout_assignments ENABLE ROW LEVEL SECURITY;
