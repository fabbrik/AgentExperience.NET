using AgentExperience.Abstractions;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>Erasure: a payload-free tombstone, and every row that named the record gone.</summary>
public sealed partial class SqliteExperienceRecordStore
{
    /// <summary>
    /// Erases one record in exactly <paramref name="scope"/>: its payload, its task ID, its search text, its failure
    /// signatures, its lifecycle events and confidence evidence, its injection summary, every holdout row naming it (or
    /// its run) and the stored run it came from (when no other live record came from it) are removed in one transaction, and a payload-free tombstone stays under its ID, so the ID is never reused
    /// and the run it came from can never be finalized into it again. Erasing a tombstone again changes nothing.
    /// </summary>
    /// <remarks>
    /// Erasure is a capability of this adapter, as of the PostgreSQL one, not of the <see cref="IExperienceRecordStore"/>
    /// port. A read in the record's own scope answers <see cref="ExperienceStoreOutcome.Deleted"/> afterwards; any other
    /// scope still answers <see cref="ExperienceStoreOutcome.NotFound"/>. SQLite may keep erased bytes in free pages of the
    /// file until it is vacuumed (<c>VACUUM</c>), and in the write-ahead log until the next checkpoint.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the record lies in.</param>
    /// <param name="experienceId">The record to erase. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see cref="ExperienceStoreOutcome.Deleted"/> with the tombstone's revision (erasure advances it once) when the record
    /// was erased or already was; <see cref="ExperienceStoreOutcome.NotFound"/> when no record with that ID is in the scope;
    /// <see cref="ExperienceStoreOutcome.Denied"/> or <see cref="ExperienceStoreOutcome.Invalid"/> before any storage access.
    /// </returns>
    public Task<SqliteExperienceDeleteResult> DeleteAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateGet(scope, experienceId);
        if (errors.Count > 0)
        {
            return Task.FromResult(new SqliteExperienceDeleteResult(ExperienceStoreOutcome.Invalid, 0, errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new SqliteExperienceDeleteResult(ExperienceStoreOutcome.Denied, 0, NoErrors));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<SqliteExperienceDeleteResult>(cancellationToken);
        }

        return Database.WriteAsync(
            "The SQLite experience record could not be erased.",
            async (connection, transaction) =>
            {
                var state = await StateInScopeAsync(connection, transaction, scope, experienceId, cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    return (new SqliteExperienceDeleteResult(ExperienceStoreOutcome.NotFound, 0, NoErrors), false);
                }

                if (state.Deleted)
                {
                    return (new SqliteExperienceDeleteResult(ExperienceStoreOutcome.Deleted, state.Revision, NoErrors), false);
                }

                var id = Id(experienceId);
                var runId = (string)(await ScalarAsync(
                    connection,
                    transaction,
                    "SELECT source_run_id FROM experience_records WHERE experience_id = $id",
                    cancellationToken,
                    ("$id", id)).ConfigureAwait(false))!;

                string[] statements =
                [
                    "DELETE FROM experience_search WHERE rowid = (SELECT search_rowid FROM experience_search_rows WHERE experience_id = $id)",
                    "DELETE FROM experience_search_rows WHERE experience_id = $id",
                    "DELETE FROM experience_failure_signatures WHERE experience_id = $id",
                    "DELETE FROM experience_lifecycle_events WHERE experience_id = $id",
                    "DELETE FROM experience_confidence_evidence WHERE experience_id = $id",
                    "DELETE FROM experience_injections WHERE experience_id = $id",
                    "DELETE FROM experience_holdout_assignments WHERE experience_id = $id",
                    $"DELETE FROM experience_holdout_assignments WHERE run_id = $run AND {ScopePredicate}",
                    $"DELETE FROM experience_provider_holdout_assignments WHERE run_id = $run AND {ScopePredicate}",

                    // The stored run it was finalized from holds the same captured content: gone too, when no other live
                    // record in the tenant came from that run.
                    "DELETE FROM experience_runs WHERE run_id = $run AND tenant_id = $tenant_id AND NOT EXISTS ("
                    + "SELECT 1 FROM experience_records r WHERE r.tenant_id = $tenant_id AND r.source_run_id = $run "
                    + "AND r.deleted_at IS NULL AND r.experience_id <> $id)",
                ];
                foreach (var statement in statements)
                {
                    await ExecuteAsync(connection, transaction, statement, cancellationToken, [("$id", id), ("$run", runId), .. ScopeParameters(scope)]).ConfigureAwait(false);
                }

                var revision = state.Revision + 1;
                var now = Ticks(Now);
                await ExecuteAsync(
                    connection,
                    transaction,
                    "UPDATE experience_records SET payload = NULL, payload_version = NULL, task_id = NULL, revision = $revision, "
                    + "updated_at = $now, deleted_at = $now, model_authored = 0 WHERE experience_id = $id",
                    cancellationToken,
                    ("$id", id),
                    ("$revision", revision),
                    ("$now", now)).ConfigureAwait(false);

                return (new SqliteExperienceDeleteResult(ExperienceStoreOutcome.Deleted, revision, NoErrors), true);
            },
            cancellationToken);
    }
}

/// <summary>The result of <see cref="SqliteExperienceRecordStore.DeleteAsync"/>.</summary>
/// <param name="Outcome">
/// <see cref="ExperienceStoreOutcome.Deleted"/> when the record was erased, or already was;
/// <see cref="ExperienceStoreOutcome.NotFound"/> when no record with that ID is in the requested scope (including when it
/// is in another one); <see cref="ExperienceStoreOutcome.Denied"/> or <see cref="ExperienceStoreOutcome.Invalid"/> before
/// any storage was touched.
/// </param>
/// <param name="Revision">The tombstone's revision on <see cref="ExperienceStoreOutcome.Deleted"/>; otherwise 0.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
public sealed record SqliteExperienceDeleteResult(
    ExperienceStoreOutcome Outcome,
    long Revision,
    IReadOnlyList<StoreValidationError> Errors);
