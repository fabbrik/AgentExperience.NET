-- contract 1.6: the application role's privileges on agent_experience.experience_embeddings (the vectors migrations,
-- 0004 and 0020), which sql/write-experience-embedding.sql writes and sql/search-experience-vector.sql reads. Run them
-- after application-role-privileges.sql (whose REVOKE ALL removes them) and application-role-privileges-1.4.sql, as the
-- schema's owner, in the same transaction or a later one, with the same :"application_role" substitution and the same
-- modes. Together the three files are exactly what ExperienceSchemaMigrator.ApplyApplicationRolePrivilegesAsync grants on
-- a schema migrated to 0026 or later with the vectors migrations applied, with the default options. Every statement is
-- one line ending in ";". UPDATE is column-level, the six columns the write's ON CONFLICT DO UPDATE sets: never
-- experience_id, created_at or the scope columns. DELETE is the .NET index's removal of a vector; a contract 1.6 port
-- never deletes one (an erasure removes it, as the owner).
-- mode: always
GRANT SELECT, INSERT, DELETE ON agent_experience.experience_embeddings TO :"application_role";
GRANT UPDATE (content_hash, dimension, embedding, model_id, source_revision, updated_at) ON agent_experience.experience_embeddings TO :"application_role";
-- mode: row-level-security
ALTER TABLE agent_experience.experience_embeddings ENABLE ROW LEVEL SECURITY;
