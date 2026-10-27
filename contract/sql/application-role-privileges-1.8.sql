-- contract 1.8: the application role's privileges on agent_experience.experience_holdout_assignments (migration 0027),
-- which sql/record-holdout-assignment.sql writes. Run them after application-role-privileges.sql (whose REVOKE ALL
-- removes them), application-role-privileges-1.4.sql and application-role-privileges-1.6.sql, as the schema's owner, in
-- the same transaction or a later one, with the same :"application_role" substitution and the same modes. They are
-- exactly what ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants on that table with the default
-- options. Every statement is one line ending in ";". There is no UPDATE (the insert's ON CONFLICT DO NOTHING updates
-- nothing) and no DELETE (an erasure removes the rows through 0027's trigger, as the owner). Migration 0029's
-- experience_provider_holdout_assignments is .NET-only and not in the contract: a fleet that uses it runs the .NET
-- privileges call after these files.
-- mode: always
GRANT SELECT, INSERT ON agent_experience.experience_holdout_assignments TO :"application_role";
-- mode: row-level-security
ALTER TABLE agent_experience.experience_holdout_assignments ENABLE ROW LEVEL SECURITY;
