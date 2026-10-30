-- contract 1.16: a stored record's lifecycle, exactly as the .NET PostgreSQL store (PostgresExperienceRecordStore)
-- runs it: commit one lifecycle event (CommitLifecycleEventAsync), read one page of a history (GetHistoryAsync), check
-- a supersession (CheckSupersessionAsync), and read one record in exactly its scope (the read the lifecycle service
-- makes first). Every statement but "sealed-record" is generated from the .NET store's source and checked against it
-- on every build. Each starts at its "-- statement:" line and runs to the next one (or the end).
--
-- Parameters are named as in the statements: @tenant_id ... @user_id are the request scope (NULL for an unset
-- optional field); statuses, kinds, sources and admissions are bound as their member names (text); instants are
-- truncated to whole microseconds in UTC; @recorded_at is the store's own clock, @applied_revision is
-- @expected_revision + 1, @lock_ids is a uuid[], @limit an integer and @start_after_revision a bigint (NULL for none).
-- @actor is the authorization's principal, NULL when it is blank. An event without a confidence update binds every
-- confidence parameter NULL. Every transaction first runs declare-bounds.sql for the authorization's bounds. A request
-- is validated, then authorized, before any statement runs (README-1.16.md): Invalid and Denied touch nothing.
--
-- COMMIT. One transaction at READ COMMITTED (pinned, never inherited):
--  1. With a confidence update: "savepoint", then "insert-evidence" with @counted the update's own Counted (a new
--     counter differs from its prior), @event_id the event's, @applied_status the event's current status. On a unique
--     violation (SQLSTATE 23505) of
--       ux_confidence_evidence_independence: "rollback-to-savepoint", then DUPLICATE;
--       confidence_evidence_pkey: "rollback-to-savepoint", then EVIDENCE REPLAY, and roll back;
--       ux_confidence_evidence_assessment: "rollback-to-savepoint", then "evidence-id-taken": true is EVIDENCE REPLAY,
--       false is Conflict with the error (Confidence.AssessmentId, "this assessment has already landed evidence for
--       this record under another evidence ID."); roll back either way.
--     Otherwise, for an update that is not counted (host-trusted evidence recorded only): "recorded-only-key-taken"
--     true is "rollback-to-savepoint", then DUPLICATE. Otherwise "release-savepoint".
--  2. "insert-event". On a unique violation of lifecycle_events_pkey: roll back, then in a new transaction
--     "select-event": no row, or a row of another record, or a row whose scope is not the request scope, is Conflict;
--     otherwise EVENT REPLAY. On a unique violation of ix_lifecycle_events_record_revision: roll back, then STALE in a
--     new transaction.
--  3. With a replacement: "lock-supersession-rows" (@lock_ids the record and the replacement), then
--     "check-supersession". It passes only when its first value is not NULL, its second (the replacement's status)
--     is Validated or Reinforced, and its third (the cycle flag) is false. Otherwise roll back: ReplacementNotAllowed,
--     with the second value as the current status when the first is not NULL (else none).
--  4. "update-projection-with-confidence" for a counted update, "update-projection-recorded-only" for one not
--     counted, "update-projection" for an event without one. On a serialization failure (40001) or a deadlock
--     (40P01): roll back, then STALE in a new transaction. When it updates no row: "select-revision-and-status" in
--     this transaction, then roll back: no row is NotFound; a tombstone (deleted_at set) is Deleted, with its
--     revision; a revision other than @expected_revision is StaleRevision, with it; otherwise StatusMismatch, with
--     the stored revision and the stored status as the current status.
--  5. (A port only.) "sealed-record", now that this transaction holds the row lock: true, roll back; NotFound.
--  6. Commit: Committed, at @applied_revision, no current status, the update as submitted.
--
-- DUPLICATE, in the same transaction: "lock-revision-and-status": no row is NotFound; a tombstone Deleted (with its
--  revision); a revision other than @expected_revision StaleRevision (with it); an event prior status that is set
--  and is not the stored status StatusMismatch (with the stored revision and status); (a port only) "sealed-record"
--  true NotFound; each rolled back. Otherwise
--  "insert-evidence" with the update recorded only (its new score and counters set to its prior ones, @counted
--  false), @event_id NULL, @applied_revision the stored revision and @applied_status the stored status; a violation
--  of confidence_evidence_pkey or ux_confidence_evidence_assessment is handled as in step 1. Then
--  "release-savepoint" and commit: Committed, with the stored revision, the stored status as the current status,
--  and the update recorded only.
-- EVIDENCE REPLAY: "select-evidence". No row (none, or one whose record is not in the request scope), or a row about
--  another record, is Conflict. A row whose event_id is NULL or the event's, and whose kind, source, run, round,
--  reviewer, rule version, detail and assessment equal the update's (exactly; NULL equals NULL), is Committed with
--  the row's applied_revision, its applied_status as the current status, and the update with the row's prior and new
--  score and counters and its admission. Anything else is Conflict.
-- EVENT REPLAY: the stored event against the submitted one with its occurred_at truncated. A submitted update takes
--  the stored one's admission and, when it is the same evidence (the same evidence ID), the stored prior and new
--  score and counters, and the event then takes the stored current status. Every field then equal (the event and
--  experience IDs, the statuses, reason, producer, occurred_at, expected revision, replacement, and every field of
--  the update, or both none) is Committed with the stored applied_revision, no current status, and the stored
--  update; anything else Conflict.
-- STALE, in a new transaction: "select-revision-and-status": no row NotFound; a tombstone Deleted (with its revision);
--  otherwise StaleRevision, with its revision.
--
-- HISTORY, in one transaction: "read-history". No row is NotFound; a row whose deleted_at is set is Deleted; (a port
-- only) a payload_version other than 1 is NotFound; otherwise Found, with the revision column, the rows whose
-- event_id is not NULL as the events (oldest first), and the last event's applied_revision as the next cursor (none
-- for no events).
-- SUPERSESSION CHECK, in one transaction: "check-supersession". A NULL first value is RecordNotFound; a NULL second
-- value ReplacementNotFound; the cycle flag Cycle, with the replacement's status; otherwise Allowed, with it.
-- READ, in one transaction: "read-record". No row is NotFound; deleted_at set is Deleted; (a port only) a
-- payload_version other than 1 is NotFound; otherwise Found: the first eighteen columns are the stored record.
--
-- The application role needs only the 1.0 privileges (application-role-privileges.sql), which already grant INSERT
-- on both logs and UPDATE of the record's lifecycle columns.
-- statement: read-record
SELECT experience_id, source_run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, task_id, status, reuse_confidence, supporting_validations, contradictions, revision, created_at, updated_at, payload_version, payload, false AS shared_by_grant, NULL::uuid AS permitting_grant_id, NULL::text AS permitting_grant_disclosure, NULL::jsonb AS permitting_grant_approach_arguments, r.deleted_at AS deleted_at FROM agent_experience.experience_records r WHERE r.experience_id = @experience_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id
-- statement: sealed-record
SELECT EXISTS (SELECT 1 FROM agent_experience.experience_records WHERE experience_id = @experience_id AND tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL AND payload_version <> 1)
-- statement: savepoint
SAVEPOINT confidence_evidence_attempt
-- statement: rollback-to-savepoint
ROLLBACK TO SAVEPOINT confidence_evidence_attempt
-- statement: release-savepoint
RELEASE SAVEPOINT confidence_evidence_attempt
-- statement: insert-evidence
INSERT INTO agent_experience.confidence_evidence (evidence_id, experience_id, event_id, kind, source, run_id, verification_round_id, reviewer_identity, counted, actor, rule_version, detail, recorded_at, applied_revision, applied_status, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, new_supporting_validations, prior_contradictions, new_contradictions, assessment_id, admission) VALUES (@evidence_id, @experience_id, @event_id, @confidence_kind, @confidence_source, @confidence_run_id, @confidence_verification_round_id, @confidence_reviewer_identity, @counted, @actor, @confidence_rule_version, @confidence_detail, @recorded_at, @applied_revision, @applied_status, @prior_reuse_confidence, @new_reuse_confidence, @prior_supporting_validations, @new_supporting_validations, @prior_contradictions, @new_contradictions, @confidence_assessment_id, @confidence_admission)
-- statement: recorded-only-key-taken
SELECT EXISTS (SELECT 1 FROM agent_experience.confidence_evidence mine JOIN agent_experience.confidence_evidence other ON other.experience_id = mine.experience_id AND other.independence_key = mine.independence_key AND other.evidence_id <> mine.evidence_id WHERE mine.evidence_id = @evidence_id AND other.event_id IS NOT NULL)
-- statement: evidence-id-taken
SELECT EXISTS (SELECT 1 FROM agent_experience.confidence_evidence ev JOIN agent_experience.experience_records r ON r.experience_id = ev.experience_id WHERE ev.evidence_id = @evidence_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL)
-- statement: select-evidence
SELECT ev.experience_id, ev.event_id, ev.kind, ev.source, ev.run_id, ev.verification_round_id, ev.reviewer_identity, ev.counted, ev.applied_revision, ev.applied_status, ev.rule_version, ev.detail, ev.prior_reuse_confidence, ev.new_reuse_confidence, ev.prior_supporting_validations, ev.new_supporting_validations, ev.prior_contradictions, ev.new_contradictions, ev.assessment_id, ev.admission FROM agent_experience.confidence_evidence ev JOIN agent_experience.experience_records r ON r.experience_id = ev.experience_id WHERE ev.evidence_id = @evidence_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL
-- statement: insert-event
INSERT INTO agent_experience.lifecycle_events (event_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision, replacement_experience_id, actor, confidence_evidence_id, confidence_kind, confidence_source, confidence_run_id, confidence_verification_round_id, confidence_reviewer_identity, confidence_rule_version, confidence_detail, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, new_supporting_validations, prior_contradictions, new_contradictions, confidence_assessment_id, confidence_admission) VALUES (@event_id, @experience_id, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, @prior_status, @current_status, @reason, @producer, @occurred_at, @recorded_at, @expected_revision, @applied_revision, @replacement_experience_id, @actor, @confidence_evidence_id, @confidence_kind, @confidence_source, @confidence_run_id, @confidence_verification_round_id, @confidence_reviewer_identity, @confidence_rule_version, @confidence_detail, @prior_reuse_confidence, @new_reuse_confidence, @prior_supporting_validations, @new_supporting_validations, @prior_contradictions, @new_contradictions, @confidence_assessment_id, @confidence_admission)
-- statement: select-event
SELECT event_id, experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, prior_status, current_status, reason, producer, occurred_at, recorded_at, expected_revision, applied_revision, replacement_experience_id, actor, confidence_evidence_id, confidence_kind, confidence_source, confidence_run_id, confidence_verification_round_id, confidence_reviewer_identity, confidence_rule_version, confidence_detail, prior_reuse_confidence, new_reuse_confidence, prior_supporting_validations, new_supporting_validations, prior_contradictions, new_contradictions, confidence_assessment_id, confidence_admission FROM agent_experience.lifecycle_events WHERE event_id = @event_id
-- statement: lock-supersession-rows
SELECT experience_id FROM agent_experience.experience_records WHERE experience_id = ANY(@lock_ids) ORDER BY experience_id FOR UPDATE
-- statement: check-supersession
WITH RECURSIVE replaced_by(experience_id) AS (
    SELECT @replacement_id::uuid
    UNION
    SELECT e.replacement_experience_id
    FROM agent_experience.lifecycle_events e
    JOIN replaced_by c ON e.experience_id = c.experience_id
    WHERE e.replacement_experience_id IS NOT NULL AND e.tenant_id = @tenant_id AND e.application_id = @application_id AND e.project_id = @project_id AND e.team_id IS NOT DISTINCT FROM @team_id AND e.agent_id IS NOT DISTINCT FROM @agent_id AND e.user_id IS NOT DISTINCT FROM @user_id
)
SELECT
    (SELECT r.status FROM agent_experience.experience_records r
     WHERE r.experience_id = @experience_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL),
    (SELECT r.status FROM agent_experience.experience_records r
     WHERE r.experience_id = @replacement_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id AND r.deleted_at IS NULL),
    EXISTS (SELECT 1 FROM replaced_by WHERE experience_id = @experience_id)
-- statement: update-projection
UPDATE agent_experience.experience_records SET status = @current_status, revision = @applied_revision, updated_at = @recorded_at WHERE experience_id = @experience_id AND revision = @expected_revision AND status = COALESCE(@prior_status, @current_status) AND tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL
-- statement: update-projection-with-confidence
UPDATE agent_experience.experience_records SET status = @current_status, revision = @applied_revision, updated_at = @recorded_at, reuse_confidence = @new_reuse_confidence, supporting_validations = @new_supporting_validations, contradictions = @new_contradictions WHERE experience_id = @experience_id AND revision = @expected_revision AND status = COALESCE(@prior_status, @current_status) AND tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL
-- statement: update-projection-recorded-only
UPDATE agent_experience.experience_records SET status = @current_status, revision = @applied_revision WHERE experience_id = @experience_id AND revision = @expected_revision AND status = COALESCE(@prior_status, @current_status) AND tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id AND deleted_at IS NULL
-- statement: select-revision-and-status
SELECT revision, status, deleted_at FROM agent_experience.experience_records WHERE experience_id = @experience_id AND tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id
-- statement: lock-revision-and-status
SELECT revision, status, deleted_at FROM agent_experience.experience_records WHERE experience_id = @experience_id AND tenant_id = @tenant_id AND application_id = @application_id AND project_id = @project_id AND team_id IS NOT DISTINCT FROM @team_id AND agent_id IS NOT DISTINCT FROM @agent_id AND user_id IS NOT DISTINCT FROM @user_id FOR UPDATE
-- statement: read-history
SELECT e.event_id, e.experience_id, e.tenant_id, e.application_id, e.project_id, e.team_id, e.agent_id, e.user_id, e.prior_status, e.current_status, e.reason, e.producer, e.occurred_at, e.recorded_at, e.expected_revision, e.applied_revision, e.replacement_experience_id, e.actor, e.confidence_evidence_id, e.confidence_kind, e.confidence_source, e.confidence_run_id, e.confidence_verification_round_id, e.confidence_reviewer_identity, e.confidence_rule_version, e.confidence_detail, e.prior_reuse_confidence, e.new_reuse_confidence, e.prior_supporting_validations, e.new_supporting_validations, e.prior_contradictions, e.new_contradictions, e.confidence_assessment_id, e.confidence_admission, r.revision, r.deleted_at, r.payload_version FROM agent_experience.experience_records r LEFT JOIN agent_experience.lifecycle_events e ON e.experience_id = r.experience_id AND (@start_after_revision IS NULL OR e.applied_revision > @start_after_revision) WHERE r.experience_id = @experience_id AND r.tenant_id = @tenant_id AND r.application_id = @application_id AND r.project_id = @project_id AND r.team_id IS NOT DISTINCT FROM @team_id AND r.agent_id IS NOT DISTINCT FROM @agent_id AND r.user_id IS NOT DISTINCT FROM @user_id ORDER BY e.applied_revision LIMIT @limit
