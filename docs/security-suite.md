# The security suite, in one place

Release verification requires that security tests exercise **tenant isolation, sanitization, revoked records, and
untrusted context**, and that unauthorized retrieval and unsafe injection paths fail. Those tests live next to the
code they test, across seven projects, because that is where their fixtures are. This page is the single place to
see that all four properties are covered and by what.

It is not documentation that can quietly go stale: `AgentExperience.Release.Tests/Security/SecuritySuiteMapTests`
parses every table below and fails if a listed test no longer exists in the named project, or if any property's
table falls below its minimum. Every listed test also runs in the ordinary `dotnet test`, so "the security suite
passed" means "the full suite passed" — there is no separate, weaker security build. `RELEASING.md` step 3 runs it.

Tests marked **(DB)** start a PostgreSQL container and need Docker. The store and vector suites run three times in CI:
in plaintext mode; again, unmodified, in crypto-shredding mode (`AGENTEXPERIENCE_TEST_ENCRYPTION=on`); and again with
PostgreSQL row-level security on (`AGENTEXPERIENCE_TEST_RLS=on`, story 15.1), every store running as the application
role behind the policies. So every **(DB)** row below that belongs to those two projects holds in all three modes. The
`PostgresRowLevelSecurityTests` rows build their own database with row-level security on, so they prove the second
layer in every mode.

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
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.A_broken_store_predicate_is_cut_back_to_the_declared_bounds` | Second layer (story 15.1): a store read with its scope predicate removed, which as the owner really returns another tenant's record, returns only the declared tenant's as the application role **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.With_nothing_declared_the_application_role_sees_no_row_and_can_write_none` | A statement that declared no bounds sees no row in any covered table and can insert none **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.A_live_grant_admits_its_recipients_bounds_to_read_and_never_to_write_and_revoking_it_ends_that` | The policies follow the grant rule: a live grant admits its recipient's bounds for reading only, and revoking it admits nothing **(DB)** |
| Storage.Postgres.Vectors.Tests | `ApplicationRoleVectorsTests.The_embeddings_table_is_behind_row_level_security_exactly_when_the_mode_says_so_and_then_admits_only_the_declared_bounds` | The embedding table's policies admit only the declared tenant **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.Every_covered_table_hides_another_tenants_rows_and_refuses_writing_them` | Every covered table, one by one: another tenant's rows are invisible and a copy of one cannot be written, and a grant's recipient sees only the record, the live grant and its own access row **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.A_grant_admits_a_record_only_while_live_and_only_the_record_owner_and_recipient_it_names` | Expired grants, grants over another record, grants whose owner columns do not match, and agent- or user-bounded recipients are admitted exactly as the grant rule says **(DB)** |
| Storage.Postgres.Vectors.Tests | `ApplicationRoleVectorsTests.Another_tenants_embedding_is_invisible_and_cannot_be_written_under_a_declaration` | Another tenant's vector is invisible, cannot be deleted, and cannot be written back under a foreign declaration **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.Every_search_answers_exactly_the_same_with_row_level_security_on_as_off` | Story 17.7: with row-level security on, text search goes through the owner-run `search_experience_text` and returns exactly what it returns with it off — rows, order, relevance, shared flags, permitting grants and access-row disclosure — over own, shared, expired-grant, foreign, erased and model-authored rows **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.Called_directly_the_function_returns_nothing_without_declared_bounds_and_nothing_of_another_tenant` | The `SECURITY DEFINER` search returns no row with nothing declared, nothing of a tenant outside the declared bounds, and nothing outside a narrower team bound — for the application role and the owner alike **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.With_row_level_security_on_the_search_uses_the_GIN_index_and_the_policies_alone_cannot` | Over a 20,000-record tenant the function's plan uses the GIN index, while the store's own statement behind the policies cannot **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.Enabling_refuses_a_text_search_function_altered_by_hand_and_changes_nothing` | A search function whose body, security or `search_path` was altered by hand is refused, and nothing is enabled or granted **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.The_function_is_granted_only_while_row_level_security_is_on_and_a_wider_grant_is_refused` | `EXECUTE` on the search function follows `EnableRowLevelSecurity`, and a `PUBLIC` grant of it is refused **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.A_route_cached_before_the_function_was_revoked_falls_back_to_the_store_statement` | A function revoked behind the library's back falls back to the store's own statement under the policies **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.A_scope_outside_the_declared_bounds_returns_nothing_even_where_a_grant_to_it_exists` | A direct call naming a scope outside the declared bounds returns nothing, so the owner-run grant join never names a grant the caller could not read **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.The_exact_scope_branches_and_an_unknown_authorship_flag_answer_the_same_with_row_level_security_on_as_off` | The function's four branches match the store's own statements, including a sealed row whose authorship flag is unknown (`NULL`) **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.The_candidate_source_calls_the_function_once_a_privileges_call_enables_row_level_security` | The candidate source really executes the function with row-level security on, and a privileges call alone moves its cached route **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.A_cached_route_is_detected_again_after_its_lifetime` | A cached route is re-detected after five minutes, so another process's change takes effect **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.A_route_cached_before_the_function_was_dropped_falls_back_to_the_store_statement` | A dropped function (`42883`) falls back to the store's own statement **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.An_error_from_inside_a_usable_function_is_reported_not_taken_for_a_missing_function` | An error raised inside a still-usable function fails the search rather than silently changing path **(DB)** |
| Storage.Postgres.Tests | `PostgresTextSearchRowLevelSecurityTests.Enabling_refuses_a_text_search_function_whose_owner_volatility_or_leakproofness_was_changed` | A search function given to another owner, made `VOLATILE` or marked `LEAKPROOF` is refused **(DB)** |
| Storage.Postgres.Tests | `OfflineStoreTests.Text_search_function_script_is_the_canonical_hardened_definition` | `0024` is the canonical function verbatim: `SECURITY DEFINER` with `pg_temp` last, revoked from `PUBLIC`, nothing granted or marked leakproof, the read policy's admission and the store's own predicates in every query |
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
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_LessonOnly_grant_withholds_the_approach_says_so_and_the_access_row_records_the_level` | A borrowed record's `Tried:` and `Worked:` lines — the lending scope's tool names — are withheld unless its grant permits it, and the `Shared:` line says so |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.A_store_that_says_shared_but_reports_no_level_is_rendered_LessonOnly` | A store that reports no disclosure level fails closed to the least disclosure |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_borrowed_record_shows_no_argument_value_under_a_level_that_is_not_consent_to_it` | Under `LessonOnly` or `LessonAndApproach` the reader's `ApproachArguments` allowlist never reaches a borrowed record: neither level is the owner's consent to show argument values |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.A_borrowed_record_under_LessonApproachAndArguments_shows_only_the_keys_both_sides_named` | Under the third level a borrowed record shows only the keys the owner's grant **and** the reader both named — planted markers in owner-only, reader-only, sibling, container and other-tool values stay out |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.The_readers_allowlist_cannot_widen_what_the_owner_consented_to` | A reader that names a key, a sibling path, a container or a tool the owner did not is shown none of them |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.An_owner_allowlist_that_is_absent_or_malformed_shows_no_borrowed_value` | A store that reports the level without usable owner keys fails closed to names only |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.A_LessonOnly_grant_still_withholds_the_whole_approach_line_whatever_either_allowlist_says` | `LessonOnly`, an unreported and an undefined level still withhold the `Tried:` and `Worked:` lines entirely, whatever keys the store reports |
| MicrosoftAgentFramework.Tests | `ExperienceInjectionTests.An_owner_allowlist_a_store_reports_under_another_level_never_reaches_the_host_or_the_block` | Owner keys a store reports under another level reach neither the block nor the host |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_regrant_that_could_show_fewer_argument_values_withdraws_the_delivery_that_showed_them` | A session shown borrowed values through one grant has them withdrawn when the record is read through another grant or a lower level |
| Storage.Postgres.Tests | `PostgresGrantTests.The_owners_keys_are_immutable_to_the_application_role_and_to_the_owner` | The owner's argument consent cannot be widened in place: no `UPDATE` privilege, and the monotonicity trigger refuses the owner too **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantTests.A_writer_that_bypasses_the_store_cannot_store_a_level_and_allowlist_that_disagree` | The schema refuses keys under another level, the new level without keys, a malformed shape and an unknown level **(DB)** |

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
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.Without_the_opt_ins_no_purge_is_reachable_and_every_other_store_path_still_works` | `EXECUTE` on each purge function, and on `0016`'s sealing function, exists only when the host opts in **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.The_upgrade_job_is_authorized_needs_encryption_and_a_role_without_the_opt_in_cannot_run_it` | The crypto-shredding upgrade is refused outside the authorization, without encryption, and to a role without `AllowSealing`; the sealing function admits only a well-formed seal at the read revision **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.Sealed_text_moved_to_another_record_row_or_scope_is_refused_rather_than_read_there` | A sealed value copied onto another record, moved to another scope, or swapped between two events fails its authentication tag instead of being read there **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.The_associated_data_binds_every_value_to_its_column_its_row_its_record_and_each_scope_field` | The associated data covers the column, the row, the record and each of the six scope fields, and every seal uses a fresh nonce |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_refuses_a_member_of_the_owner_and_changes_nothing` | A role that could `SET ROLE` to the owner, or that owns the database, is never certified as the application role **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_verifies_effective_privileges_and_rolls_back_when_a_predefined_role_grants_DELETE` | Effective privileges are verified, so `DELETE` arriving through `pg_write_all_data` fails the deploy instead of passing silently **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_refuses_a_role_reaching_a_superuser_server_files_or_session_replication_role` | A role that reaches a superuser (even without inheriting), a server-file role, or `SET` on `session_replication_role` is refused before anything is granted **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_rolls_back_on_CREATE_through_PUBLIC_or_DELETE_one_SET_ROLE_away` | `CREATE` on the schema from `PUBLIC`, and `DELETE` held by a role the application role can `SET ROLE` to but does not inherit from, both fail verification and roll back **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.The_privilege_call_refuses_MAINTAIN_and_pg_maintain_on_PostgreSQL_17_and_later` | On PostgreSQL 17 and later, `MAINTAIN` on a guarded table (through `PUBLIC` or one `SET ROLE` away) and membership in `pg_maintain` both fail the call and roll back; on 15 and 16, which have neither, the same call succeeds **(DB)** |
| Storage.Postgres.Tests | `PostgresApplicationRoleTests.An_object_a_later_migration_adds_gets_nothing_until_the_manifest_names_it` | Re-applying on every deploy covers later objects: a new table gets nothing, and a new `SECURITY DEFINER` function executable by the role fails verification **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.A_declaration_for_one_tenant_cannot_read_or_update_another_tenants_rows` | Second layer (story 15.1): one tenant's declared bounds cannot read, update or insert another tenant's rows, and a narrower bound narrows **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.Enabling_is_declarative_never_forced_and_verified` | Row-level security is enabled and disabled by the privileges call, never forced, and a hand-forced table is put back **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.Enabling_refuses_a_policy_the_migrations_did_not_create_and_a_missing_one_and_changes_nothing` | An extra permissive policy or a missing one fails the call and rolls it back **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.Enabling_refuses_an_application_role_that_can_bypass_it` | An application role holding `BYPASSRLS` is refused **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.The_suite_runs_behind_row_level_security_exactly_when_its_mode_says_so` | The suite's third mode really runs behind the policies **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.A_declaration_for_one_tenant_cannot_purge_sweep_or_seal_another_tenants_rows_through_the_owners_functions` | The `SECURITY DEFINER` erasure, grant purge, access purge and sealing functions refuse a scope outside the declared bounds **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.Enabling_puts_back_a_policy_altered_by_hand` | A policy widened with `ALTER POLICY … USING (true)` is re-created from its canonical definition on the next deploy **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.Enabling_refuses_a_default_for_the_settings_the_policies_read` | A role or database default for the declared settings fails the call **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.An_access_log_batch_naming_several_readers_lands_row_by_row_with_each_readers_own_scope` | An access row about a grant it was not delivered through is refused with row-level security on **(DB)** |
| Storage.Postgres.Tests | `PostgresRowLevelSecurityTests.Declared_bounds_end_with_their_transaction_on_the_same_physical_connection` | The declaration never outlives its transaction, committed or rolled back, on one pooled connection **(DB)** |
| Storage.Postgres.Vectors.Tests | `ApplicationRoleVectorsTests.Enabling_row_level_security_over_an_embedding_table_without_its_policies_is_refused_and_enables_nothing` | The embedding table is never left with row-level security on and no policy **(DB)** |
| Storage.Postgres.Tests | `PostgresReuseFeedbackTests.A_run_scope_outside_the_authorization_is_denied_before_anything_is_written` | Feedback **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantTests.A_revoke_without_administrator_authority_is_denied_and_the_grant_still_stands` | Grant administration needs its own authority **(DB)** |
| Storage.Postgres.Tests | `PostgresSupersessionAndAppendOnlyTests.A_live_grants_disclosure_level_cannot_be_changed_in_place` | A grant's disclosure level is immutable: widening it needs a new, audited grant **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantTests.An_undefined_disclosure_level_is_Invalid_on_Disclosure_and_writes_nothing` | A disclosure level nobody chose is refused before anything is written **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantAccessAuditTests.A_new_access_row_without_a_disclosure_level_is_refused_and_the_mode_decides_the_read` | A delivery that cannot say what level it was made under is refused by the database, not stored blank **(DB)** |
| Storage.Postgres.Tests | `PostgresGrantAccessAuditTests.Upgrading_to_0011_makes_every_live_grant_LessonOnly_and_leaves_old_trail_rows_unrecorded` | An upgrade narrows every existing grant to the least disclosure and invents no level for old trail rows **(DB)** |
| Storage.Postgres.Vectors.Tests | `HybridRetrievalIntegrationTests.A_vector_delivery_records_the_grants_disclosure_level_and_retrieval_carries_none` | The vector channel records the permitting grant's level on its access row **(DB)** |
| Core.Tests | `ExperienceIndexingServiceTests.A_scope_outside_the_authorization_is_denied_before_anything_is_read_or_embedded` | Indexing |
| Core.Tests | `ExperienceFinalizationServiceTests.Another_scope_cannot_block_a_run_by_taking_the_id_it_will_finalize_under` | The cross-scope ID squat (deferred item 1, fixed by story 4.5) cannot block a run from finalizing |
| Core.Tests | `ExperienceFinalizationServiceTests.Another_scope_cannot_block_a_run_by_committing_an_event_under_its_initial_event_id` | The cross-scope initial event ID squat (fixed by story 17.6) cannot block a run's initial commit, under the run-only ID earlier releases derived or the ID another scope derives |
| Storage.Postgres.Tests | `PostgresFinalizationTests.Another_scope_committing_events_under_this_runs_initial_event_ids_cannot_block_it` | The same against the globally unique `lifecycle_events` key **(DB)** |
| Core.Tests | `ExperienceFinalizationServiceTests.A_derived_initial_event_id_is_distinct_per_scope_and_from_the_run_only_id_of_earlier_releases` | The initial event ID differs for every scope field and from the run-only ID 0.1.0-preview.6 derived (pinned by a golden vector) |
| Storage.Postgres.Tests | `PostgresFinalizationTests.The_same_run_id_finalized_in_two_scopes_commits_under_two_different_initial_event_ids` | Two scopes finalizing the same run ID each commit under their own initial event ID **(DB)** |

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
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_value_of_an_argument_not_on_the_allowlist_never_reaches_the_block` | Only the argument keys a host allowlisted for that exact tool can reach a `Tried:` line — planted markers in every other key, a case variant and another tool stay out |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_value_the_capture_sanitizer_redacted_stays_redacted_and_one_it_omitted_stays_absent` | Through real capture and `DefaultSanitizer`: an allowlisted key shows the stored value, so a redacted secret stays redacted and an omitted field stays absent |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_non_scalar_value_is_omitted_with_a_marker_and_its_content_never_read` | An allowlisted object or array is never rendered, so nothing nested inside it can reach a model |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.A_nested_path_shows_only_the_scalar_it_ends_on_and_no_other_key_or_path_ever_appears` | A dotted path shows only the scalar it names — planted siblings, other array elements and container content stay out, in both the CLR and the JSON shape |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.A_path_that_ends_on_an_object_or_an_array_is_the_marker_and_its_content_is_never_read` | A path never shows a container whole |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.A_nested_dictionary_with_a_looser_comparer_cannot_widen_a_path_by_case` | Every step of a path is matched ordinally |
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
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_record_withdrawn_after_delivery_is_retracted_once_on_the_next_invocation` | A record revoked, superseded, quarantined, erased, un-granted, aged out or re-scored below the floor *after* it was delivered is withdrawn on the session's next invocation, by a fixed notice carrying its ID and no content |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_withdrawal_check_that_throws_injects_nothing_and_changes_nothing` | A withdrawal check that cannot be made injects no new record and withdraws nothing (fail closed) |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.Withdrawal_notices_take_the_block_budget_first_and_the_rest_stay_owed` | A notice cannot be crowded out: it takes the budget first, and no new record is shown while one is owed |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_failed_invocation_leaves_its_withdrawal_notice_owed` | A notice whose invocation failed is delivered again |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_stream_abandoned_while_carrying_a_withdrawal_notice_leaves_the_notice_owed` | A notice whose stream was abandoned, so MAF kept no history of it, is delivered again rather than counted as delivered |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_held_record_selected_again_and_withdrawn_at_the_re_read_is_retracted` | A held record revoked or made unreadable between retrieval and injection is withdrawn, not only omitted |
| Storage.Postgres.Tests | `PostgresGrantTests.A_revoked_grant_denies_the_read_and_the_history_keeps_both_events` | **(DB)** |
| Storage.Postgres.Vectors.Tests | `HybridRetrievalIntegrationTests.A_record_shared_by_a_grant_is_retrieved_through_both_channels_until_the_grant_is_revoked` | **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.Every_read_path_reports_the_tombstone_as_erased_or_not_at_all` | An erased record, the strongest form of revocation **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.The_erased_text_itself_is_no_longer_findable_by_search` | **(DB)** |
| Storage.Postgres.Tests | `PostgresDeletionTests.Every_write_path_refuses_a_tombstone_rather_than_resurrecting_it` | **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.After_erasure_neither_a_pre_erasure_dump_nor_the_dead_heap_tuple_can_be_opened_with_anything_still_reachable` | Crypto-shredding (KL-2): a real `pg_dump` taken before the erasure and a `pageinspect` read of the dead tuple after it hold only ciphertext, and no key the key store still holds opens it **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.A_destroyed_key_whose_tombstone_never_committed_reads_as_erased_everywhere_until_a_retry_completes_it` | A record whose key is gone never looks live: every read answers as for a tombstone and every write is refused, until a retry writes the tombstone **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.A_key_store_that_cannot_destroy_leaves_the_record_live_readable_and_keyed` | A record never looks erased while its key survives: a failed key destruction rolls the erasure back **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.A_process_without_encryption_cannot_erase_a_sealed_record_and_leave_its_key_behind` | A store left without its encryption cannot report a sealed record erased while its key survives: `0016`'s guard refuses the tombstone **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.The_sealed_shape_checks_refuse_a_malformed_row_even_from_the_owner` | A sealed row cannot carry its task ID or anything but its seal in the clear, and a sealed-rationale column holds only the sealed format **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.Tampered_ciphertext_is_rejected_by_its_authentication_tag_on_every_read` | One flipped bit in a sealed payload or event reason fails every read, and nothing decrypted is returned **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.The_seal_function_answers_Deleted_for_a_tombstone_and_AlreadySealed_for_a_sealed_row_and_changes_neither` | The upgrade job's one write cannot touch an erased record (plaintext or sealed) or re-seal a sealed one: `0016`'s function answers `Deleted` and `AlreadySealed` and leaves the row byte for byte as it was **(DB)** |
| Storage.Postgres.Tests | `PostgresCryptoShreddingTests.A_feedback_replay_with_no_sealed_copy_left_to_open_compares_everything_but_the_rationale` | A feedback submission whose sealed rationale copies can no longer be opened is still recognized on replay, hands back only the placeholder, and conflicts on any field it can still read **(DB)** |
| Storage.Postgres.Vectors.Tests | `CryptoShreddingVectorsTests.A_sealed_record_is_embedded_from_the_same_summary_as_its_plaintext_twin_and_a_shredded_one_never_again` | A record whose key is destroyed is never scanned, re-embedded or returned by the vector channel **(DB)** |
| Core.Tests | `EnvelopeExperienceKeyStoreTests.A_destroyed_reference_is_destroyed_for_ever_and_is_never_given_a_fresh_key` | The reference key store never re-creates a destroyed key |
| Core.Tests | `EnvelopeExperienceKeyStoreTests.A_wrapped_key_moved_to_another_records_entry_cannot_be_unwrapped` | A wrapped data key is bound to its record and scope |

## 4. Untrusted context — injected content never becomes authority

| Project | Test | What it proves |
| --- | --- | --- |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.Injected_text_that_orders_an_unauthorized_tool_call_is_still_denied_by_the_existing_boundary` | A model that *obeys* an injected instruction is still denied by the approval boundary |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.An_injected_approach_line_naming_a_guarded_tool_is_still_denied_by_the_existing_boundary` | The same, for a tool name on the `Tried:` line of the attempt `Worked:` names |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.An_injection_shaped_guarded_tool_name_is_escaped_in_the_block_and_its_call_is_still_denied` | A tool name crafted to break out of the block |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachTests.A_tool_name_laden_with_bidi_zero_width_and_tag_characters_renders_clean` | A tool name carrying bidirectional overrides and isolates, zero-width characters, TAG-character text and a terminal escape reaches the block with each of them as a space, and no invisible code point anywhere in the block |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachTests.Each_invisible_code_point_in_a_tool_name_becomes_a_space` | Every class the 6.2 value routine strips (bidi controls, zero-width and other format characters, a supplementary-plane format character, TAG characters, C0 and C1 controls, private-use) is stripped from a tool name the same way |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachTests.A_tool_name_cannot_reorder_or_hide_the_rest_of_the_line_or_the_block` | An unterminated override or isolate in a tool name cannot reorder the separator, the next name or the standing suffix, nor the lines after it |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachTests.A_marker_split_by_invisible_characters_in_a_tool_name_is_still_neutralized` | Invisible characters in a tool name cannot hide one of the block's markers from neutralization, whether they split it between words or inside one |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.An_allowlisted_argument_value_that_orders_a_guarded_call_is_still_denied_by_the_existing_boundary` | The same, for an allowlisted argument value: the first model-chosen text a `Tried:` line can carry |
| MicrosoftAgentFramework.Tests | `InjectedContentAuthorizationTests.A_nested_value_on_a_borrowed_record_that_orders_a_guarded_call_is_still_denied_by_the_existing_boundary` | The same, for a nested value shown on a borrowed record through a `LessonApproachAndArguments` grant |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachArgumentsTests.A_value_cannot_add_a_line_forge_a_marker_or_a_label_or_close_its_own_quotes` | An argument value crafted to break out of its quotes, its line or the block forges no structure |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceTriedWorkedTests.A_hostile_error_yields_only_its_recognised_tokens_by_default_and_a_neutralized_quoted_excerpt_on_request` | A captured error that reads as an instruction reaches the block only as its recognised tokens (`HTTP 200`) by default, and as a neutralized, quoted, bounded excerpt only when the host opts in |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceTriedWorkedTests.An_excerpt_is_its_first_line_only_neutralized_quoted_and_cut_to_its_limit` | An opted-in error excerpt is one line, cut to 120 characters, with its markers neutralized, its quotes and invisible characters taken out, and none of the error's later lines |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceTriedWorkedTests.Record_text_cannot_start_a_line_with_the_new_labels` | A stored lesson or warning cannot forge a `Tried:` or `Worked:` line |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceTriedWorkedTests.A_tool_name_cannot_spell_a_separator_to_fake_a_call_or_an_outcome` | A tool name holding `, `, `->` or `→` can fake neither another call nor an attempt's outcome |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceTriedWorkedTests.An_argument_value_cannot_spell_the_outcome_separator` | An allowlisted value holding `→` and a forged `Worked:` cannot fake an outcome or a line |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachTests.A_showing_grant_shows_only_the_borrowed_records_verified_final_attempt_and_no_failure` | A grant that shows a borrowed record's approach shows its verified final attempt only: never the owner's failed attempts or their error classes |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceApproachTests.A_showing_grant_shows_nothing_of_a_borrowed_record_that_did_not_verify` | A borrowed record that did not verify shows no attempt under any level |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_borrowed_delivery_tracks_only_the_value_its_final_attempt_showed_and_narrowing_retracts_it` | A value shown on a borrowed record's final attempt is withdrawn when the grant narrows; a failed attempt's value never crosses |
| Core.Tests | `ErrorClassTests.A_class_never_carries_other_text_from_the_error` | The error class is built only from recognised tokens, never other text from the error |
| MicrosoftAgentFramework.Tests | `HistoricalReferenceBorrowedAndNestedArgumentsTests.A_nested_leaf_keeps_every_bound_a_top_level_value_has` | A nested value gets every 6.2 bound: quotes and look-alikes, `->`, line breaks, markers, invisible characters and the clamp |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.Record_content_cannot_forge_a_withdrawal_notice` | A lesson, reuse guidance or tool name that spells a withdrawal notice for another record forges no notice |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.Loose_spellings_of_a_withdrawal_notice_in_record_text_are_neutralized` | The same with doubled whitespace, dash look-alikes, zero-width characters, Unicode line separators and indented labels |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_value_set_in_process_as_another_type_is_neither_trusted_nor_overwritten` | Session state that is present but not readable is never mistaken for absent, so the budget and what is owed cannot be reset that way |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.Two_providers_with_the_same_key_on_one_agent_are_refused_before_either_shares_an_account` | Two providers cannot silently share one session account, which would let one reset or spend the other's budget and swallow its withdrawal notices: `ChatClientAgent` refuses the duplicate key when it is built |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.Two_providers_with_different_keys_on_one_agent_keep_independent_budgets_dedupe_and_withdrawals` | Two providers with their own keys on one agent keep independent budgets, deduplication and withdrawals, and neither account names the other's records |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_malformed_session_state_key_is_refused_where_it_is_configured` | A session state key that is blank, oversized, carries whitespace or an invisible code point, or is capture's run ID key is refused at construction |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_state_that_does_not_validate_is_neither_trusted_nor_overwritten` | Session state edited into a malformed, oversized or out-of-range shape injects nothing rather than resetting the budget or forgetting a notice owed |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.A_state_naming_records_the_reader_cannot_read_widens_nothing_and_discloses_only_their_ids` | Session state naming another tenant's record reads it only in the request's own scope and discloses nothing of it |
| MicrosoftAgentFramework.Tests | `SessionInjectionTests.The_withdrawal_check_is_a_scope_check_that_writes_no_access_row` | The withdrawal re-check hands nothing over and is not audited as a delivery |
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
| Core.Tests | `VerifiedIndependenceTests.A_forged_run_is_refused_as_unknown_and_nothing_is_written` | KL-11: a `RunId` agent output invented is not a fresh independence key — not even behind a genuine token minted for it |
| Core.Tests | `VerifiedIndependenceTests.Evidence_naming_the_records_own_source_run_is_refused_in_both_modes` | A run cannot confirm the lesson it produced, even under the trust-the-host opt-out |
| Core.Tests | `VerifiedIndependenceTests.A_run_finalized_only_in_another_scope_or_readable_only_through_a_grant_is_unknown` | A run is known only in the evidence's own scope: not through another scope's record, a grant, or a tombstone |
| Core.Tests | `VerifiedIndependenceTests.A_forged_round_is_refused_for_a_real_run_and_so_is_every_round_of_a_run_that_closed_none` | A machine round must be the one finalization closed for that run; another run's genuine round is refused too |
| Core.Tests | `VerifiedIndependenceTests.A_random_GUID_a_malformed_string_or_a_tampered_token_is_refused_as_invalid` | An assessment cannot be forged: a random GUID, any single-character tamper, an over-long string or another key's token fails the HMAC, and is a refusal, never an exception |
| Core.Tests | `VerifiedIndependenceTests.A_token_for_another_scope_run_reviewer_or_direction_is_refused_and_one_for_another_record_is_not_for_this_one` | A genuine token cannot be moved to another scope, run, reviewer, direction or record |
| Core.Tests | `VerifiedIndependenceTests.An_expired_token_is_refused_and_one_issued_in_the_future_is_invalid` | A token expires, and one stamped ahead of the verifier's clock is refused |
| Core.Tests | `VerifiedIndependenceTests.A_replayed_token_is_refused_while_an_identical_retry_of_the_same_evidence_still_replays` | A token is spent once per record; a lost acknowledgement's retry still converges |
| Core.Tests | `VerifiedIndependenceTests.Feedback_with_a_forged_or_mismatched_assessment_records_the_exposure_and_moves_nothing` | Reuse feedback drops a forged, mismatched or unknown-run attribution before its ledger records a benefit, and never echoes the token |
| Core.Tests | `VerifiedIndependenceTests.The_token_binds_every_field_of_a_scope` | A token minted for a narrower or wider scope fails, and a field added to `Scope` fails this test until the MAC binds it |
| Core.Tests | `VerifiedIndependenceTests.A_tokens_expiry_is_the_one_it_was_minted_with_whatever_the_verifier_is_configured_with` | Expiry is signed into the token, so reconfiguring a verifier cannot resurrect or extend one |
| Core.Tests | `VerifiedIndependenceTests.Feedback_attributing_a_record_to_its_own_source_run_is_degraded_before_the_ledger` | Feedback cannot record an attributed benefit for a lesson "reused" in the run it came from |
| Core.Tests | `VerifiedIndependenceTests.The_issuer_refuses_what_it_should_never_sign_and_never_prints_a_token` | The issuer never signs for a scope outside the reviewer's authorization, and no `ToString()` prints a token |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.Forged_runs_rounds_and_assessment_tokens_are_refused_and_nothing_reaches_the_ledger` | The same refusals end to end over real finalization: nothing reaches `confidence_evidence` **(DB)** |
| Core.Tests | `VerifiedIndependenceTests.Core_registration_verifies_by_default_wires_the_capture_service_and_never_registers_an_issuer` | Verification is the default through DI, and no container offers an issuer an agent-driven component could resolve |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.A_replayed_token_is_refused_by_the_database_and_an_identical_retry_still_replays` | Single use is the database's unique index, atomic with the evidence; a replay writes not even an uncounted row **(DB)** |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.The_assessment_index_refuses_a_second_use_even_where_the_independence_key_is_free` | A writer that bypasses Core's token check still cannot spend one assessment twice on a record **(DB)** |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.A_second_feedback_presenting_a_spent_token_lands_nothing_while_retrying_the_first_converges` | A token cannot be reused through a second feedback submission **(DB)** |
| Core.Tests | `VerifiedIndependenceTests.A_real_run_that_was_never_exposed_to_the_record_is_refused_for_machine_and_human_evidence` | KL-11 (7.3): a real, finalized or captured run that was never given the record is not a key, for machine or human evidence, and a token it presented is not spent |
| Core.Tests | `VerifiedIndependenceTests.An_exposed_run_is_admitted_once_per_independence_key_and_a_caller_choosing_among_runs_gets_one_key_per_exposed_run` | A caller that can name every real run in the scope gets one key per run that saw the lesson, not one per real run |
| Core.Tests | `VerifiedIndependenceTests.Exposure_at_a_revision_after_the_one_the_evidence_is_computed_against_is_refused_and_an_earlier_one_admits` | An exposure claiming a revision the record has not reached yet is refused, fail-closed |
| Core.Tests | `VerifiedIndependenceTests.A_hand_written_record_under_a_runs_derived_id_vouches_for_nothing_until_marked_finalized` | A record written through `CreateAsync` without finalization, under the derived ID and with a forged round and exposure, vouches for no run by default |
| Core.Tests | `VerifiedIndependenceTests.Feedback_attributing_a_record_the_run_was_never_exposed_to_is_degraded_before_the_ledger` | Reuse feedback cannot record an attributed benefit for a record the run was never given |
| Core.Tests | `VerifiedIndependenceTests.The_capture_service_keeps_the_earliest_revision_once_per_record_and_closes_exposure_with_the_run` | A host cannot pre-seed exposure through `StartRun` (not even with an empty list it fills afterwards), and nothing can add exposure to a completed run whose provenance finalization may already have copied |
| Core.Tests | `VerifiedIndependenceTests.Opt_out_evidence_is_flagged_host_trusted_and_a_confidence_read_can_leave_it_out` | Evidence the trust-the-host opt-out admitted is labelled as such and can be left out of a read score |
| Core.Tests | `VerifiedIndependenceTests.Verified_only_also_leaves_out_evidence_stored_before_admission_was_recorded` | A verified-only read also drops evidence with no recorded admission and a hand-written record's self-chosen initial counters |
| Core.Tests | `Diagnostics.ExperienceTelemetryTests.Span_attributes_are_only_the_documented_keys` | The admission (`Verified` and `HostTrusted`) and the refusal reach telemetry as closed-set member names, and nothing else was added |
| MicrosoftAgentFramework.Tests | `ExposureBoundEvidenceTests.Inject_capture_finalize_then_feedback_is_admitted_for_the_exposed_run_and_refused_for_one_that_never_saw_the_lesson` | End to end through the adapter: exposure is recorded where the library injects, survives finalization, and is what feedback is checked against |
| MicrosoftAgentFramework.Tests | `ExposureBoundEvidenceTests.A_capture_service_that_fails_to_record_exposure_is_reported_and_costs_the_invocation_nothing` | A failure to record exposure fails closed for evidence and never costs the invocation its context |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.Exposure_and_origin_survive_finalization_and_the_store_and_an_unexposed_run_is_refused` | Exposure and origin round-trip through the payload (sealed in crypto-shredding mode), and an unexposed run writes nothing **(DB)** |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.A_record_written_by_hand_under_a_runs_derived_id_reads_back_host_written_and_vouches_for_nothing` | A hand-written record reads back unverified and vouches for no run **(DB)** |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.The_database_refuses_an_unknown_admission_and_the_application_role_cannot_relabel_one` | The application role cannot relabel host-trusted evidence as verified on either ledger (`42501`), and each ledger's CHECK refuses an unknown admission **(DB)** |
| Storage.Postgres.Tests | `PostgresVerifiedIndependenceTests.Admission_is_stored_on_the_ledger_and_the_event_and_a_confidence_read_can_leave_host_trusted_evidence_out` | The admission is kept on both ledgers, a replay cannot relabel it, and a read over the real history leaves host-trusted evidence out **(DB)** |
| Storage.Postgres.Tests | `OfflineStoreTests.A_malformed_exposure_list_or_origin_is_Invalid_with_no_database_call` | A hand-written record cannot store an empty, negative, duplicated or oversized exposure list, or an undefined origin |
| MicrosoftAgentFramework.Tests | `ExposureBoundEvidenceTests.An_uncaptured_agent_running_inside_a_captured_invocation_does_not_expose_the_outer_run` | A nested agent's injected block is never recorded as delivered to the outer captured run |
| MicrosoftAgentFramework.Tests | `ExposureBoundEvidenceTests.A_later_run_in_the_same_session_is_not_credited_with_what_earlier_turns_delivered` | Unauthenticated session state is never turned into exposure: a later run in a reused session is credited only with what it is given itself |
| MicrosoftAgentFramework.Tests | `ExposureBoundEvidenceTests.A_capture_service_that_does_not_record_exposure_is_reported_once_and_the_injection_still_happens` | A capture service that cannot record exposure is reported, and its runs are exposed to nothing |
| Core.Tests | `VerifiedIndependenceTests.A_replay_of_evidence_stored_before_admission_was_recorded_reports_none_and_tags_none` | A replay never borrows the resubmitting call's admission, in its result or on its span |
| Core.Tests | `SignedProvenanceTests.A_forged_CreateAsync_record_claiming_Finalized_is_refused_when_signing_is_on_and_believed_when_it_is_off` | KL-11 (13.1): with provenance signing on, a record written through `CreateAsync` claiming `Finalized` vouches for no run |
| Core.Tests | `SignedProvenanceTests.Changing_any_signed_claim_after_signing_makes_the_run_host_written` | Changing any signed claim (record ID, each scope field, null versus empty, source run, round, origin, each exposure) after signing makes the run host-written |
| Core.Tests | `SignedProvenanceTests.A_signature_under_a_key_not_in_the_ring_is_refused` | A signature under a key the host does not hold vouches for nothing |
| Core.Tests | `SignedProvenanceTests.A_tampered_value_or_algorithm_is_refused_and_content_fields_are_not_claims` | A flipped, truncated or empty signature, or another algorithm, is refused |
| Core.Tests | `SignedProvenanceTests.The_cutover_trusts_only_listed_unsigned_records_whatever_a_record_says_about_its_age` | A forged record backdated by years is not trusted by the cutover, nor is a signed record with its signature stripped; only the listed legacy IDs are |
| Core.Tests | `SignedProvenanceTests.The_three_ways_a_signature_fails_are_reported_with_one_identical_reason` | The refusal reason is no oracle for which signature check failed |
| Core.Tests | `SignedProvenanceTests.Verified_only_counts_at_most_finalizations_own_initial_validation_of_a_signed_or_listed_record` | Counters are not signed claims: inflated initial counters on a validly signed record do not count as verified |
| Core.Tests | `SignedProvenanceTests.The_opt_out_still_refuses_a_present_but_invalid_signature_and_nothing_else` | The trust-the-host opt-out still refuses a record the library signed that someone then changed |
| Core.Tests | `SignedProvenanceTests.The_opt_out_checks_only_a_record_the_verified_path_would_read_and_so_does_feedback` | Feedback attribution under the opt-out applies the same tamper check before the ledger |
| Core.Tests | `SignedProvenanceTests.A_provenance_key_that_is_the_assessment_token_key_is_refused` | One secret never serves both purposes: a ring holding the assessment token key is refused at construction |
| Core.Tests | `SignedProvenanceTests.Finalizations_own_ring_must_be_one_the_lifecycle_service_checks_and_then_takes_precedence` | Signing under a key the checker does not hold is refused at construction, never discovered as silently refused evidence |
| Core.Tests | `SignedProvenanceTests.A_retry_that_collides_with_a_record_whose_signature_does_not_vouch_is_reported_not_replayed` | A retry never reports as finalized a stored record whose signature does not vouch for it |
| Core.Tests | `SignedProvenanceTests.Nothing_public_on_the_options_returns_key_bytes_and_it_prints_none` | No public member returns key bytes, and `ToString` redacts them |
| Core.Tests | `SignedProvenanceTests.The_signature_is_compared_in_constant_time` | The signature comparison is `CryptographicOperations.FixedTimeEquals` |
| Core.Tests | `SignedProvenanceTests.The_content_encoding_matches_the_pinned_golden_vector_and_the_documented_encoding` | KL-18 (17.2): the claims version 2 content encoding is pinned to a golden byte vector, length-prefixed and strict UTF-8 |
| Core.Tests | `SignedProvenanceTests.A_v2_record_whose_content_changed_in_the_store_is_unconfirmed_model_authored_and_fails_its_claims_check` | KL-18 (17.2): changing any signed content field (task text, lesson, approach lists, guidance, preconditions, warnings, producer) after signing makes the record model-authored and excluded under `Exclude` |
| Core.Tests | `SignedProvenanceTests.Authorship_flipped_from_Model_to_Deterministic_in_the_store_is_treated_as_model_authored` | KL-18 (17.2): flipping a signed record's authorship to `Deterministic` in the store does not unfence it |
| Core.Tests | `SignedProvenanceTests.A_v1_record_still_vouches_for_its_run_but_its_content_is_unconfirmed_so_it_is_fenced_and_excluded` | KL-18 (17.2): a record signed before claims version 2 keeps vouching for its run, but its lesson counts as model-authored |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.With_signing_a_v2_record_tampered_before_the_re_read_is_fenced_or_omitted_by_the_provider` | KL-18 (17.2): the provider decides on the record it re-read, so content changed after retrieval ranked it is fenced, or omitted under `Exclude` |
| Core.Tests | `SignedProvenanceTests.Every_field_the_writer_renders_is_covered_so_changing_one_leaves_the_content_unconfirmed` | KL-18 (17.2): a changed tool name, argument value, tool call order, attempt error, environment field, outcome status or evidence count leaves a v2 record's content unconfirmed |
| Core.Tests | `SignedProvenanceTests.Argument_values_encode_through_their_JSON_form_so_either_store_reads_back_the_same_bytes` | KL-18 (17.2): argument values encode by their JSON form as the store writes it (enums by name, camel-case objects, numbers by exact decimal value, last repeated member wins), and a value with no JSON form is refused |
| Core.Tests | `SignedProvenanceTests.A_tampered_v2_record_no_longer_vouches_for_its_run` | KL-11, KL-18 (17.2): a content edit to a v2 record makes its run vouch for nothing |
| Core.Tests | `SignedProvenanceTests.SignClaimsVersion_1_signs_new_records_as_an_earlier_build_verifies_them_and_both_versions_verify` | KL-18 (17.2): the rolling-deploy setting signs exactly the 13.1 encoding, and its records' content stays unconfirmed |
| Core.Tests | `SignedProvenanceTests.ConfirmV1Content_lets_a_valid_v1_record_render_by_its_own_authorship_and_nothing_else` | KL-18 (17.2): the transition setting confirms only a v1 signature that verifies, and never overrides a model-authored reflection |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.An_unconfirmed_record_has_its_task_ID_and_Approach_line_inside_the_fence_and_none_of_it_above` | KL-18 (17.2): nothing an unconfirmed record supplies (task ID, recorded times, environment, verification, evidence, approach, lesson) is rendered outside the fence |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.A_v2_record_whose_approach_was_tampered_is_fenced_with_its_approach_inside_or_omitted_as_unconfirmed` | KL-18 (17.2): a tool name changed in the store is fenced, or omitted as `UnconfirmedContent` |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.With_signing_an_unsigned_record_is_unfenced_only_when_the_cutover_lists_it_and_without_signing_nothing_changes` | KL-18 (17.2): an unsigned record the cutover does not list, and a v1 record, are fenced or excluded |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.The_host_decision_is_told_the_provider_s_verdict_not_the_declared_authorship` | KL-18 (17.2): `DecideInjection` sees the provider's verdict, so a declared `Deterministic` cannot mislead it |
| Core.Tests | `SignedProvenanceTests.An_untouched_v2_record_verifies_and_its_content_is_confirmed_so_it_renders_by_its_own_authorship` | KL-18 (17.2): a genuine v2 record's content is confirmed and it is retrieved under `Exclude` |
| Core.Tests | `SignedProvenanceTests.A_signature_under_an_unknown_key_or_an_unknown_algorithm_leaves_the_content_unconfirmed` | KL-18 (17.2): an unknown key or algorithm, or a v2 value relabelled as v1, confirms nothing |
| Core.Tests | `SignedProvenanceTests.Content_is_confirmed_for_a_listed_unsigned_record_only` | KL-18 (17.2): the cutover set confirms only listed unsigned records, never a listed record with a signature or a model-authored reflection |
| Core.Tests | `SignedProvenanceTests.Without_signing_nothing_changes_whatever_the_record_carries` | KL-18 (17.2): with no key ring, unsigned, v1 and tampered records are judged by their reflection as before |
| Core.Tests | `SignedProvenanceTests.AddAgentExperienceRetrieval_decides_authorship_against_the_registered_signing_options_in_either_order` | KL-18 (17.2): registered signing options reach the retrieval service whatever the registration order or form |
| Core.Tests | `SignedProvenanceTests.A_record_whose_content_cannot_be_encoded_is_unconfirmed_and_does_not_fail_the_retrieval_of_others` | KL-18 (17.2): a stored record whose content cannot be encoded fails closed as `UnconfirmedContent`, and retrieval of other records completes |
| Core.Tests | `SignedProvenanceTests.ConfirmContentRecordIds_confirms_listed_v1_or_unsigned_records_only_as_they_verify` | KL-18 (17.2): per-record confirmation covers only listed records whose signature is absent or verifies |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.With_signing_a_v2_confirmed_deterministic_record_renders_unfenced_and_a_v1_one_is_fenced_or_excluded` | KL-18 (17.2): a confirmed v2 record renders unfenced, a v1 record is fenced or excluded as `UnconfirmedContent` |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.An_unconfirmed_record_with_no_reflection_is_fenced_whole_or_excluded_as_unconfirmed` | KL-18 (17.2): an unconfirmed record with no reflection renders nothing it supplies outside the fence |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.The_public_writer_decides_on_the_reflection_alone_unless_given_the_content_confirmation` | KL-18 (17.2): the public writer fences unconfirmed content when given the confirmation function, and says it decides on the reflection alone otherwise |
| Storage.Postgres.Tests | `PostgresSignedProvenanceTests.A_v2_signature_over_every_rendered_field_still_confirms_after_the_store_round_trip` | KL-18 (17.2): the content digest survives the store's round trip, plaintext and sealed **(DB)** |
| Storage.Postgres.Tests | `PostgresSignedProvenanceTests.A_model_authored_v2_record_whose_authorship_was_removed_in_the_database_is_still_model_authored` | KL-18 (17.2): an authorship flip written straight into the payload leaves the record model-authored **(DB)** |
| Storage.Postgres.Tests | `PostgresSignedProvenanceTests.A_v2_record_read_back_has_confirmed_content_and_a_lesson_edited_in_the_database_does_not` | KL-18 (17.2): a lesson rewritten in the payload is caught **(DB)** |
| Storage.Postgres.Tests | `PostgresSignedProvenanceTests.A_payload_whose_signed_claim_was_changed_in_the_database_is_refused_as_host_written` | A closed round rewritten in place in the payload, by a role that can, makes the run vouch for nothing **(DB)** |
| Storage.Postgres.Tests | `PostgresSignedProvenanceTests.A_record_forged_through_CreateAsync_with_a_copied_signature_or_none_is_refused_in_either_mode` | A genuine signature copied onto a forged record, or none, vouches for nothing, in plaintext and crypto-shredding mode **(DB)** |
| Core.Tests | `ModelAuthoredContentGuardTests.A_model_lesson_adding_a_URL_absent_from_the_run_is_quarantined_as_UnsafeContent` | Story 14.3: a model-authored lesson carrying a URL the run never showed is quarantined, and the reason does not repeat it |
| Core.Tests | `ModelAuthoredContentGuardTests.Each_rule_refuses_model_text_in_every_field_naming_the_rule_and_never_the_matched_text` | Every guard rule (links, obfuscated dots, IPs, colon-only schemes, UNC paths, homoglyphs, instruction phrasing, credentials) refuses model text in the lesson, a list item and the reuse guidance |
| Core.Tests | `ModelAuthoredContentGuardTests.A_link_passes_only_as_a_whole_token_the_run_showed` | A host passes only as a whole token: `evil.com` is not admitted by `notevil.com` or `evil.com.au`, nor a URL by a longer one |
| Core.Tests | `ModelAuthoredContentGuardTests.With_no_run_content_every_URL_hostname_and_IP_address_is_refused` | A reflector that declares no run content, or whose declaration throws, has every link refused |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.A_model_authored_record_wraps_every_model_written_field_and_keeps_the_approach_outside` | Every model-written field sits between the fixed `Authored:` and `End authored:` lines |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.An_undefined_authorship_read_back_from_a_store_is_labelled_and_excluded` | Any authorship other than `Deterministic` is labelled and excluded: the label fails closed |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.Exclude_drops_model_records_before_the_record_limit_so_they_cannot_crowd_out_deterministic_ones` | Model-authored records excluded by the host take no record slot |
| MicrosoftAgentFramework.Tests | `ModelAuthoredInjectionTests.A_lesson_cannot_forge_or_contradict_the_authored_line` | A lesson cannot start a line with `Authored:` or `End authored:` |

## What this suite does not prove

It does not prove that a model-authored lesson is safe. The content guard is a best-effort filter: content echoed
from the run, a poisoned tool result included, passes it by design, and so do paraphrased instructions,
whitespace-split dots and encoded secrets. Authorship is self-declared and unsigned. The label, `Exclude` and the
approval boundary are what the suite pins down; see KL-18 and
[Limits of model-authored lessons](guide/finalization.md#limits-of-model-authored-lessons).

It does not prove that row-level security binds a compromised application role. The bounds the policies read are
settings any session may set, so a host that runs arbitrary SQL as the application role can declare wide ones; the
second layer catches a mistake in a store's own SQL, not an attacker holding the role's credentials. Nor does the
second layer enforce a grant's disclosure level, hide that a colliding ID exists in another tenant, or check the
record a feedback exposure names; those are KL-17 too. See
[Enabling row-level security](guide/deployment.md#enabling-row-level-security).

It does not prove that erasure reaches a copy of the **derived search data**. In crypto-shredding mode a record's
full-text vector (its task ID, summary and lesson as lexemes) and its embedding stay readable in every backup,
replica and dead tuple, because PostgreSQL searches them in the clear; in plaintext mode every copy of everything
stays readable. Nor does it prove a production key store's custody: the property holds only if the keys live
outside the database's backup domain and the key store's own backups are kept no longer than the erasure deadline.
Both are KL-2 in the [Documented boundaries](known-limits.md#documented-boundaries) table.

The label on an injected block is hygiene, not a control: nothing here claims a model will *treat* retrieved text
as data. The control is the approval boundary around tools, which lives outside the block, and section 4 is what
proves that boundary holds when the model does obey. The same goes for a withdrawal notice: the suite proves one is
delivered, once, carrying nothing but an ID, and that record text cannot forge one — not that a model that already
read the withdrawn text stops using it. That residual, and that the earlier block stays in a reused session's
history, is KL-12 in the [Documented boundaries](known-limits.md#documented-boundaries) table.

Nor does it prove that an argument value a host allowlists through `ExperienceInjectionOptions.ApproachArguments` is
safe to show. Such a value was chosen by the captured run's model and is only as clean as the capture-time sanitizer,
which classifies by field name, not content, left it. The suite proves the structural bounds — only allowlisted keys,
only stored scalars (a dotted path reaching only the scalar it names, never a container), quoted with no double quote or step separator inside, clamped and capped, and for a borrowed record only what both its
`LessonApproachAndArguments` grant and the reader named — and that the approval boundary still holds when a shown
value orders a guarded call, nested and borrowed included; which keys are harmless to show is the host's decision
(the owner's, for a borrowed record, as well as the reader's).

Nor does it prove that a run cited as confidence evidence *used* the record. The suite proves that a run, a round and
an assessment cannot be invented — each must be one the library finalized, captured or minted — that none can be
moved to another scope or record or used twice, and that the run must be one the library recorded
delivering the record into; a caller can still cite each run that was given the lesson, once per run, whatever the
lesson did there. It does not prove the host's own statements: a host that calls `RecordExposure` itself, marks a
hand-written record `Finalized`, or passes a dishonest `ClosedRound` to finalization is believed; so is an
application role granted `AllowSealing`, which can replace a plaintext payload — exposures and origin included — while
the grant stands. And the
trust-the-host opt-out still trusts everything; the suite proves only that what it admits is labelled and can be left
out of a read. Those residuals are KL-11 in the same table.
