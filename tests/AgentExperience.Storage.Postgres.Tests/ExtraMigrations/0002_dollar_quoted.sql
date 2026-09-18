-- Test-only script: a $body$-quoted DO block. It only runs unchanged while DbUp variable substitution
-- is disabled; with variables enabled, $body$ is read as a variable and the script fails.
DO $body$
BEGIN
    EXECUTE 'CREATE TABLE IF NOT EXISTS agent_experience.extra_dollar_quoted (id integer NOT NULL)';
END
$body$;
