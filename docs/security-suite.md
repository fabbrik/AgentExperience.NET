# The security suite, in one place

Release verification requires that security tests exercise **tenant isolation, sanitization, revoked records, and
untrusted context**, and that unauthorized retrieval and unsafe injection paths fail. Those tests live next to the
code they test, across seven projects, because that is where their fixtures are. This page is the single place to
see that all four properties are covered and by what.

It is not documentation that can quietly go stale: `AgentExperience.Release.Tests/Security/SecuritySuiteMapTests`
parses every table below and fails if a listed test no longer exists in the named project, or if any property's
table falls below its minimum. Every listed test also runs in the ordinary `dotnet test`, so "the security suite
passed" means "the full suite passed" — there is no separate, weaker security build. `RELEASING.md` step 3 runs it.

Tests marked **(DB)** start a PostgreSQL container and need Docker.

## 1. Tenant isolation — a caller in scope A cannot retrieve, inject, or write against scope B

### Retrieve

| Project | Test | What it proves |
| --- | --- | --- |
| Storage.Postgres.Tests | `PostgresExperienceCandidateSourceTests.A_record_in_another_tenant_project_or_team_is_never_returned` | Text channel: the exact-scope predicate is in SQL **(DB)** |
| Storage.Postgres.Vectors.Tests | `PostgresEmbeddingIndexTests.A_vector_search_never_returns_a_record_from_another_scope_however_close_its_vector` | Vector channel: a closer foreign vector is still never returned **(DB)** |
| Storage.Postgres.Vectors.Tests | `HybridRetrievalIntegrationTests.Hybrid_retrieval_never_crosses_a_scope_boundary` | Both channels through Core retrieval **(DB)** |
| Storage.Postgres.Tests | `PostgresExperienceRecordStoreTests.Query_never_crosses_tenants_or_optional_scope_fields` | Listing a scope **(DB)** |
| Storage.Postgres.Tests | `PostgresLifecycleCommitTests.History_of_a_record_in_another_scope_is_NotFound_like_a_missing_one` | History reveals nothing across scopes **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantTests.A_grant_never_widens_a_read_across_a_tenant_application_or_project` | A sharing grant cannot cross the hard boundary **(DB)** |
| Core.Tests | `ExperienceRetrievalServiceTests.A_scope_outside_the_authorization_is_an_empty_fail_closed_result_and_no_search_is_issued` | Denied before any channel is queried |
| Core.Tests | `ExperienceRetrievalServiceTests.A_candidate_from_another_tenant_application_or_project_empties_the_result_whatever_the_optional_fields_say` | A misbehaving adapter returning a foreign row fails closed |
| Core.Tests | `ExperienceRetrievalServiceTests.A_declared_grant_can_still_not_carry_a_candidate_across_a_tenant_application_or_project` | Core re-checks what the adapter says a grant permitted |
| Core.Tests | `HybridRetrievalTests.A_denied_scope_never_reaches_either_channel` | Nothing is embedded or searched for a denied scope |

### Inject

| Project | Test | What it proves |
| --- | --- | --- |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_request_scope_outside_the_authorization_injects_nothing_and_is_reported_as_denied` | No retrieval, no block |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_store_that_answers_with_a_record_from_another_tenant_is_omitted_even_though_it_said_Found` | The pre-injection re-read checks scope itself |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_candidate_no_longer_readable_in_scope_is_omitted_indistinguishably_from_a_missing_one` | A re-scoped record drops out, revealing nothing |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_LessonOnly_grant_withholds_the_approach_says_so_and_the_access_row_records_the_level` | A borrowed record's `Approach:` line — the lending scope's tool names — is withheld unless its grant permits it |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_store_that_says_shared_but_reports_no_level_is_rendered_LessonOnly` | A store that reports no disclosure level fails closed to the least disclosure |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_borrowed_record_never_shows_an_argument_value_whatever_its_grant` | The reader's `ApproachArguments` allowlist never reaches a borrowed record: the lending scope's argument values are shown under no disclosure level |

### Write

| Project | Test | What it proves |
| --- | --- | --- |
| Abstractions.Tests | `ContractShapeTests.Tenant_must_match_exactly` | The authorization predicate every adapter uses |
| Abstractions.Tests | `ContractShapeTests.Blank_authorized_tenant_permits_nothing` | A blank authorization is not a wildcard |
| Storage.Postgres.Tests | `OfflineStoreTests.Different_tenant_is_Denied_before_any_connection_opens` | Refused before the database is touched |
| Storage.Postgres.Tests | `OfflineStoreTests.A_scope_beyond_the_authorization_is_Denied_before_any_connection_opens` | Same, for a narrower authorization |
| Storage.Postgres.Tests | `PostgresLifecycleCommitTests.An_unknown_record_and_one_in_another_scope_are_both_NotFound` | A foreign lifecycle commit is indistinguishable from a missing record **(DB)** |
| Storage.Postgres.Tests | `PostgresSupersessionAndAppendOnlyTests.A_replacement_in_another_scope_is_refused_and_reveals_nothing` | Supersession cannot point across scopes **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.A_delete_naming_another_scope_is_the_same_answer_as_one_naming_nothing` | Erasure cannot reach, or probe, another scope **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.A_sweep_never_reaches_another_scope` | Retention cannot either **(DB)** |
| Storage.Postgres.Tests | `PostgresRetentionReachTests.A_subtree_sweep_never_reaches_another_tenant_application_or_project` | A `ScopeMatch.Subtree` sweep never leaves its tenant, application or project — including a tenant whose name its own prefixes **(DB)** |
| Storage.Postgres.Tests | `PostgresRetentionReachTests.A_subtree_sweep_reaches_exactly_the_scopes_at_or_beneath_its_root` | "Beneath" at every field level, including gap roots: never an ancestor, a sibling, or a case-variant **(DB)** |
| Storage.Postgres.Tests | `PostgresRetentionReachTests.Authorizing_the_root_covers_the_subtree_and_a_narrower_authorization_cannot_widen_to_it` | A team-bounded authorization cannot take a project root, so a subtree cannot widen it **(DB)** |
| Storage.Postgres.Tests | `PostgresRetentionReachTests.A_blank_lower_field_is_Invalid_rather_than_read_as_any` | Only `null` is a wildcard; a blank field is refused, never read as "any" |
| Storage.Postgres.Tests | `OfflineStoreTests.A_subtree_sweep_or_access_purge_outside_the_authorization_is_Denied_before_any_connection_opens` | Refused before the database is touched |
| Storage.Postgres.Tests | `PostgresRetentionReachTests.An_access_purge_never_reaches_another_tenant_or_a_scope_outside_its_subtree` | The access-log purge cannot reach another tenant, an ancestor or a sibling **(DB)** |
| Storage.Postgres.Tests | `PostgresRetentionReachTests.The_access_purge_is_out_of_reach_of_a_role_that_was_never_granted_it` | `EXECUTE` on the `SECURITY DEFINER` access purge is revoked from `PUBLIC`, and its `search_path` is pinned **(DB)** |
| Storage.Postgres.Tests | `PostgresRetentionReachTests.The_guard_admits_a_hand_marked_delete_only_of_an_old_row_and_only_under_the_access_marker` | Even a hand-set marker cannot delete an access row inside the 30-day floor, and 0010's marker admits nothing on that ledger **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_application_role_owns_nothing_and_holds_exactly_the_manifest` | Two-role deployment (KL-4): the application role owns nothing, and its grants are exactly the stores' manifest — no `DELETE`, `TRUNCATE` or table-level `UPDATE` anywhere, no `CREATE` on the schema **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_application_role_cannot_alter_disable_or_drop_a_guard_or_replace_its_function` | From the application role's own connection: `ALTER TABLE`, `DISABLE TRIGGER`, `DROP TRIGGER`, replacing or re-pinning a guard function, or altering a purge function are all refused **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.No_ledger_record_or_grant_row_can_be_rewritten_removed_or_truncated_by_the_application_role_whatever_marker_it_sets` | `UPDATE`/`DELETE`/`TRUNCATE` on every ledger, record and grant table is refused by the privilege system, with each purge marker set by hand and with both **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.A_hand_marked_tombstone_is_out_of_reach_because_the_role_cannot_write_those_columns` | Column-level `UPDATE` closes the hand-written tombstone that would erase a payload and skip the ledger sweep **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.Without_the_opt_ins_no_purge_is_reachable_and_every_other_store_path_still_works` | `EXECUTE` on each purge function exists only when the host opts in **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_refuses_a_member_of_the_owner_and_changes_nothing` | A role that could `SET ROLE` to the owner, or that owns the database, is never certified as the application role **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_verifies_effective_privileges_and_rolls_back_when_a_predefined_role_grants_DELETE` | Effective privileges are verified, so `DELETE` arriving through `pg_write_all_data` fails the deploy instead of passing silently **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_refuses_a_role_reaching_a_superuser_server_files_or_session_replication_role` | A role that reaches a superuser (even without inheriting), a server-file role, or `SET` on `session_replication_role` is refused before anything is granted **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_rolls_back_on_CREATE_through_PUBLIC_or_DELETE_one_SET_ROLE_away` | `CREATE` on the schema from `PUBLIC`, and `DELETE` held by a role the application role can `SET ROLE` to but does not inherit from, both fail verification and roll back **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_refuses_MAINTAIN_and_pg_maintain_on_PostgreSQL_17_and_later` | On PostgreSQL 17 and later, `MAINTAIN` on a guarded table (through `PUBLIC` or one `SET ROLE` away) and membership in `pg_maintain` both fail the call and roll back; on 15 and 16, which have neither, the same call succeeds **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.An_object_a_later_migration_adds_gets_nothing_until_the_manifest_names_it` | Re-applying on every deploy covers later objects: a new table gets nothing, and a new `SECURITY DEFINER` function executable by the role fails verification **(DB)** |
| Storage.Postgres.Tests | `PostgresReuseFeedbackTests.A_run_scope_outside_the_authorization_is_denied_before_anything_is_written` | Feedback **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantTests.A_revoke_without_administrator_authority_is_denied_and_the_grant_still_stands` | Grant administration needs its own authority **(DB)** |
| Storage.Postgres.Tests | `PostgresSupersessionAndAppendOnlyTests.A_live_grants_disclosure_level_cannot_be_changed_in_place` | A grant's disclosure level is immutable: widening it needs a new, audited grant **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantTests.An_undefined_disclosure_level_is_Invalid_on_Disclosure_and_writes_nothing` | A disclosure level nobody chose is refused before anything is written **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantAccessAuditTests.A_new_access_row_without_a_disclosure_level_is_refused_and_the_mode_decides_the_read` | A delivery that cannot say what level it was made under is refused by the database, not stored blank **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantAccessAuditTests.Upgrading_to_0011_makes_every_live_grant_LessonOnly_and_leaves_old_trail_rows_unrecorded` | An upgrade narrows every existing grant to the least disclosure and invents no level for old trail rows **(DB)** |
| Storage.Postgres.Vectors.Tests | `HybridRetrievalIntegrationTests.A_vector_delivery_records_the_grants_disclosure_level_and_retrieval_carries_none` | The vector channel records the permitting grant's level on its access row **(DB)** |
| Core.Tests | `ExperienceIndexingServiceTests.A_scope_outside_the_authorization_is_denied_before_anything_is_read_or_embedded` | Indexing |
| Core.Tests | `ExperienceFinalizationServiceTests.Another_scope_cannot_block_a_run_by_taking_the_id_it_will_finalize_under` | The cross-scope ID squat (deferred item 1, fixed by story 4.5) cannot block a run from finalizing |

## 2. Sanitization — unsafe content never reaches storage, a model, or telemetry

| Project | Test | What it proves |
| --- | --- | --- |
| Core.Tests | `DefaultSanitizerTests.AC1_unknown_fields_are_omitted_secret_fields_are_redacted_whole_and_allowlisted_fields_pass_through_or_recurse` | Allowlist by default, secrets redacted whole |
| Core.Tests | `SanitizerConformanceTests.Secret_classified_dict_value_is_never_traversed_or_leaked` | A secret subtree is never walked |
| Core.Tests | `SanitizerConformanceTests.List_of_nested_dicts_is_recursed_and_its_secrets_never_leak` | Nested secrets in lists |
| Core.Tests | `DefaultSanitizerTests.Rejection_reason_is_the_internally_thrown_SanitizationFailureException_message_and_never_contains_the_input_value` | A rejection reason never quotes the value |
| Core.Tests | `InMemoryExperienceCaptureServiceTests.An_unsanitizable_capture_stores_nothing_and_hands_the_host_back_the_decision_and_its_reason` | Fail-closed at capture: nothing stored |
| Core.Tests | `InMemoryExperienceCaptureServiceTests.AC6_secret_classified_argument_field_never_reaches_the_stored_tool_call_only_the_sanitizer_allowed_result_does` | Tool-call arguments |
| Core.Tests | `ExperienceIndexingServiceTests.Only_the_sanitized_retrieval_summary_is_ever_handed_to_the_provider` | Nothing else is sent to an embedding provider |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachTests.A_host_reflectors_secret_in_SuccessfulApproaches_never_reaches_the_block` | A host reflector cannot widen what injection emits |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_value_of_an_argument_not_on_the_allowlist_never_reaches_the_block` | Only the argument keys a host allowlisted for that exact tool can reach the `Approach:` line — planted markers in every other key, a case variant and another tool stay out |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_value_the_capture_sanitizer_redacted_stays_redacted_and_one_it_omitted_stays_absent` | Through real capture and `DefaultSanitizer`: an allowlisted key shows the stored value, so a redacted secret stays redacted and an omitted field stays absent |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_non_scalar_value_is_omitted_with_a_marker_and_its_content_never_read` | An allowlisted object or array is never rendered, so nothing nested inside it can reach a model |
| MicrosoftAgentFramework.Tests | `InjectionTelemetryTests.Injection_telemetry_never_contains_the_block_it_injected` | Telemetry carries no injected content |
| Sample.EndToEnd.Tests | `SampleRunTests.Secret_tool_argument_reaches_neither_the_transcript_the_capture_nor_the_record` | End to end, through the MAF adapter |
| Storage.Postgres.Tests | `MigratorLogSilenceTests.A_failing_script_reaches_no_console_trace_logger_or_activity_sink` | The migrator leaks no error text to the host's sinks **(DB)** |

## 3. Revoked and superseded records — unreachable through every read and injection channel

| Project | Test | What it proves |
| --- | --- | --- |
| Core.Tests | `ExperienceRetrievalServiceTests.Only_Validated_and_Reinforced_are_ever_eligible` | Every other status, `Revoked` and `Superseded` included, is ineligible |
| Storage.Postgres.Tests | `PostgresExperienceCandidateSourceTests.A_status_change_committed_through_the_store_takes_a_record_out_of_the_eligible_set` | Text channel, in SQL **(DB)** |
| Storage.Postgres.Vectors.Tests | `PostgresEmbeddingIndexTests.The_status_filter_and_the_confidence_floor_are_applied_in_SQL_on_the_vector_channel_too` | Vector channel, in SQL **(DB)** |
| Storage.Postgres.Vectors.Tests | `HybridRetrievalIntegrationTests.A_revoked_and_a_superseded_record_are_unreachable_through_both_channels_even_with_their_vectors_left_in_place` | Both channels at once, with the vectors deliberately *not* removed — the status predicate is the boundary, not de-indexing **(DB)** |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_candidate_revoked_between_retrieval_and_injection_is_omitted_and_the_rest_still_injected` | Injection re-checks immediately before building the block |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_grant_revoked_between_retrieval_and_injection_delivers_nothing_and_records_nothing` | A revoked *grant* is honoured at injection too |
| Storage.Postgres.Tests | `PostgresGrantTests.A_revoked_grant_denies_the_read_and_the_history_keeps_both_events` | **(DB)** |
| Storage.Postgres.Vectors.Tests | `HybridRetrievalIntegrationTests.A_record_shared_by_a_grant_is_retrieved_through_both_channels_until_the_grant_is_revoked` | **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.Every_read_path_reports_the_tombstone_as_erased_or_not_at_all` | An erased record, the strongest form of revocation **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.The_erased_text_itself_is_no_longer_findable_by_search` | **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.Every_write_path_refuses_a_tombstone_rather_than_resurrecting_it` | **(DB)** |

## 4. Untrusted context — injected content never becomes authority

| Project | Test | What it proves |
| --- | --- | --- |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.Injected_text_that_orders_an_unauthorized_tool_call_is_still_denied_by_the_existing_boundary` | A model that *obeys* an injected instruction is still denied by the approval boundary |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.An_injected_approach_line_naming_a_guarded_tool_is_still_denied_by_the_existing_boundary` | The same, for the `Approach:` line |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.An_injection_shaped_guarded_tool_name_is_escaped_in_the_block_and_its_call_is_still_denied` | A tool name crafted to break out of the block |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.An_allowlisted_argument_value_that_orders_a_guarded_call_is_still_denied_by_the_existing_boundary` | The same, for an allowlisted argument value: the first model-chosen text the `Approach:` line can carry |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_value_cannot_add_a_line_forge_a_marker_or_a_label_or_close_its_own_quotes` | An argument value crafted to break out of its quotes, its line or the block forges no structure |
| ReuseBaseline | `ApprovalBoundaryTests.A_poisoned_lesson_is_obeyed_denied_counted_and_fails_the_guardrail` | A poisoned lesson, measured: obeyed, denied, and counted against the experiment |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.The_injected_message_is_reference_material_in_the_user_role_not_a_host_instruction` | Never injected as a system instruction |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_host_denial_omits_the_record_whatever_its_confidence_or_status_and_leaves_it_unchanged` | The host's risk policy overrides confidence |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.More_eligible_records_than_the_record_limit_injects_the_top_two_and_records_the_rest` | Bounded: whole records dropped, never cut |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_retrieval_failure_injects_nothing_and_is_reported_never_rethrown` | A failure never fabricates context |
| MicrosoftAgentFramework.Tests | `ExperienceCaptureTests.Row3_a_continuation_id_naming_a_different_task_or_scope_is_refused_and_runs_uncaptured` | A run cannot be continued into another task or scope |
| Core.Tests | `VerificationAggregatorTests.KL5_a_null_ExpectedKind_is_refused_rather_than_read_as_any_kind` | Evidence from a producer the task did not name (an approval, an agent's own report) cannot verify a check by default: "any kind" must be declared |
| Core.Tests | `VerificationAggregatorTests.A_required_check_naming_an_ExpectedKind_ignores_evidence_of_any_other_kind` | A mismatched evaluator leaves the check `Unknown`, never `Pass` |
| Core.Tests | `DefaultExperienceReflectorTests.KL6_a_request_pairing_a_run_with_another_runs_evaluation_cannot_be_constructed` | An evaluation made under one run ID cannot be paired with another run: no reflector, default or host, receives the pair |
| Core.Tests | `ExperienceFinalizationServiceTests.KL6_a_host_reflection_that_does_not_match_its_request_quarantines_the_record_without_a_lesson` | A host reflector that re-judges the verdict, swaps the run, or adds evidence gets no validated lesson stored |

## What this suite does not prove

The label on an injected block is hygiene, not a control: nothing here claims a model will *treat* retrieved text
as data. The control is the approval boundary around tools, which lives outside the block, and section 4 is what
proves that boundary holds when the model does obey. The residual risk — injected blocks accumulating in a reused
session — is a row in the root README's Known limits table.

Nor does it prove that an argument value a host allowlists through `ExperienceInjectionOptions.ApproachArguments` is
safe to show. Such a value was chosen by the captured run's model and is only as clean as the capture-time sanitizer,
which classifies by field name, not content, left it. The suite proves the structural bounds — only allowlisted keys,
only stored scalars, quoted with no double quote or step separator inside, clamped and capped, never for a borrowed record — and that the approval boundary
still holds when a shown value orders a guarded call; which keys are harmless to show is the host's decision.
