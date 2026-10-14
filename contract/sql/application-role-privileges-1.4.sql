-- contract 1.4: the application role's privileges on agent_experience.experience_injections (migration 0026), which
-- sql/record-injection.sql writes. Run them after application-role-privileges.sql (whose REVOKE ALL removes them), as
-- the schema's owner, in the same transaction or a later one, with the same :"application_role" substitution and the
-- same modes. Together the two files are exactly what ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync
-- grants on a schema migrated to 0026 or later, with the default options. Every statement is one line ending in ";".
-- UPDATE is column-level, the two columns the upsert's ON CONFLICT DO UPDATE sets; there is no DELETE (an erasure
-- removes the row through 0026's trigger, as the owner).
-- mode: always
GRANT SELECT, INSERT ON agent_experience.experience_injections TO :"application_role";
GRANT UPDATE (injection_count, last_injected_at) ON agent_experience.experience_injections TO :"application_role";
-- mode: row-level-security
ALTER TABLE agent_experience.experience_injections ENABLE ROW LEVEL SECURITY;
