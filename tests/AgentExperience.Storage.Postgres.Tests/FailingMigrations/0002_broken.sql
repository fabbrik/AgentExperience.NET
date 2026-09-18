-- Test-only script: fails at execution time (undefined_table, 42P01). Never shipped in the package.
SELECT * FROM agent_experience.table_that_does_not_exist;
