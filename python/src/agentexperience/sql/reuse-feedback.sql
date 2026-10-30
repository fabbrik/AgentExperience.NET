-- contract 1.17: the reuse-feedback ledger, exactly as the .NET PostgreSQL store (PostgresExperienceReuseFeedbackStore)
-- records one submission (RecordAsync). Every statement is generated from the .NET store's source and checked against
-- it on every build. Each starts at its "-- statement:" line and runs to the next one (or the end).
--
-- Parameters are named as in the statements: @tenant_id ... @user_id are the submission's scope (NULL for an unset
-- optional field); outcomes, benefits and attribution sources are bound as their member names (text); instants are
-- truncated to whole microseconds in UTC; @recorded_at is the store's own clock; @evidence_ids is a uuid[] (NULL when
-- there are none); @measure_value a double precision; @experience_ids a uuid[]; @ordinal an integer;
-- @rationale_sealed is NULL (a port holds no key store: it writes plaintext, as the .NET store does without
-- encryption). Every transaction first runs declare-bounds.sql for the authorization's bounds. A submission is
-- validated, then authorized, before any statement runs (README-1.17.md): Invalid and Denied touch nothing.
--
-- RECORD. The exposures are first put in experience-ID order (the order a lowercase GUID's text sorts in); the caller's
-- own order is kept only to name an erased exposure. One transaction at READ COMMITTED (pinned, never inherited):
--  1. "insert-submission". When it returns no row the feedback ID is taken: "select-submission" (no scope predicate:
--     the key is global, and row-level security hides another scope's row) and, when it returns a row,
--     "select-exposures" (in ordinal order), in this transaction, which is then rolled back. No row is Conflict with no
--     submission. Otherwise the stored submission (its scope columns as the scope, evidence_ids NULL as none, its
--     exposures in ordinal order) is compared with the one in hand: the run, the scope, the outcome, the claimed and
--     established benefit, the attribution source, reviewer, evaluator, round, assessment and rationale (exactly; NULL
--     equals NULL), the evidence IDs in order, attributed_at and observed_at against the submitted ones truncated, the
--     measure's kind and value (exactly) and the trial label, and the exposures (each ID, flag and evidence ID) in
--     order. Equal is AlreadyRecorded, with the stored submission; anything else Conflict, with the stored submission
--     when the authorization permits its scope and with none otherwise.
--  2. "select-exposed-record-state" with @experience_ids the exposed IDs, in the submission's scope. A row whose
--     deleted_at is set is an erased record: when there is any, roll back, and the outcome is Invalid with one error
--     per exposure naming one, in the caller's order: path "Exposures[i].ExperienceId", i the exposure's position as
--     the caller listed it, message "names an Experience Record that has been erased; nothing may be recorded against
--     it again.". A record that does not exist, or lies in another scope, is not refused.
--  3. "insert-exposure" for each exposure, in experience-ID order, @ordinal its position from 0.
--  4. Commit: Recorded, with the submission as given, its exposures in experience-ID order (its instants as given).
-- Any other database error is a store failure, never an outcome.
--
-- The application role needs only the 1.0 privileges (application-role-privileges.sql), which already grant SELECT and
-- INSERT on both ledger tables; the FOR KEY SHARE lock on the exposed records is covered by its UPDATE grant on the
-- record's lifecycle columns.
-- statement: insert-submission
INSERT INTO agent_experience.reuse_feedback (feedback_id, run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, run_outcome, claimed_benefit, benefit, attribution_source, reviewer_identity, evaluator_id, verification_round_id, assessment_id, rationale, evidence_ids, attributed_at, measure_kind, measure_value, trial_label, observed_at, recorded_at) VALUES (@feedback_id, @run_id, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, @run_outcome, @claimed_benefit, @benefit, @attribution_source, @reviewer_identity, @evaluator_id, @verification_round_id, @assessment_id, @rationale, @evidence_ids, @attributed_at, @measure_kind, @measure_value, @trial_label, @observed_at, @recorded_at) ON CONFLICT (feedback_id) DO NOTHING RETURNING feedback_id
-- statement: select-submission
SELECT run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, run_outcome, claimed_benefit, benefit, attribution_source, reviewer_identity, evaluator_id, verification_round_id, assessment_id, rationale, evidence_ids, attributed_at, measure_kind, measure_value, trial_label, observed_at FROM agent_experience.reuse_feedback WHERE feedback_id = @feedback_id
-- statement: select-exposures
SELECT experience_id, attributed, evidence_id, rationale_sealed FROM agent_experience.reuse_feedback_exposures WHERE feedback_id = @feedback_id ORDER BY ordinal
-- statement: select-exposed-record-state
SELECT r.experience_id, r.deleted_at FROM agent_experience.experience_records r WHERE r.experience_id = ANY(@experience_ids) AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id ORDER BY r.experience_id FOR KEY SHARE OF r
-- statement: insert-exposure
INSERT INTO agent_experience.reuse_feedback_exposures (feedback_id, experience_id, ordinal, attributed, evidence_id, rationale_sealed) VALUES (@feedback_id, @experience_id, @ordinal, @attributed, @evidence_id, @rationale_sealed)
