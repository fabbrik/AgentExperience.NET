using AgentExperience.Abstractions;
using static AgentExperience.Storage.Sqlite.SqliteDatabase;

namespace AgentExperience.Storage.Sqlite;

/// <summary>
/// An <see cref="IExperienceSignatureSource"/> over one <see cref="SqliteExperienceRecordStore"/>'s records. The store
/// derives each record's failure signatures (<see cref="ExperienceFailureSignatures.Of"/>) once, when it is created, and
/// indexes them by key and by tool and error class; erasure deletes them.
/// </summary>
/// <remarks>
/// Only records in exactly the given scope, live, in one of the query's eligible statuses, at or above its minimum
/// confidence and, when <see cref="ExperienceSignatureQuery.ExcludeModelAuthored"/> is set, not model-authored; then exact
/// matches first, resolved before unresolved, higher confidence, more recently created, and by ID, and only then cut to
/// the limit -- the in-memory and PostgreSQL sources' order. There are no grants here.
/// </remarks>
public sealed class SqliteExperienceSignatureSource : IExperienceSignatureSource
{
    private readonly SqliteExperienceRecordStore _store;

    /// <summary>Creates a signature source over <paramref name="store"/>'s records.</summary>
    /// <param name="store">The record store whose records are searched.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    public SqliteExperienceSignatureSource(SqliteExperienceRecordStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<ExperienceSignatureSearchResult> FindBySignatureAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceSignatureQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateSignatureQuery(scope, query);
        if (errors.Count > 0)
        {
            return Task.FromResult(new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Invalid, [], errors));
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Denied, [], []));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<ExperienceSignatureSearchResult>(cancellationToken);
        }

        var signature = query.Signature;
        var exactOnly = query.Match == ExperienceSignatureMatch.Exact;
        var parameters = new List<(string, object?)>(ScopeParameters(scope))
        {
            ("$key", signature.Key),
            ("$tool", signature.ToolName),
            ("$class", signature.ErrorClass),
            ("$minimum", query.MinimumConfidence),
            ("$exclude", query.ExcludeModelAuthored ? 1 : 0),
        };
        var statusNames = new List<string>();
        foreach (var (status, i) in query.EligibleStatuses.Distinct().Select((status, i) => (status, i)))
        {
            statusNames.Add($"$status{i}");
            parameters.Add(($"$status{i}", status.ToString()));
        }

        // A record matches exactly when it holds the whole key; coarsely when it holds the tool and class. Its resolution
        // is that of the exact signature, or, for a coarse match, whether any signature with the tool and class resolved.
        var matching = exactOnly
            ? "s.signature_key = $key"
            : "s.tool_name = $tool AND s.error_class = $class";
        var sql =
            "SELECT r.experience_id, max(s.signature_key = $key) AS exact, "
            + "max(CASE WHEN s.signature_key = $key THEN s.resolved END) AS exact_resolved, max(s.resolved) AS any_resolved, "
            + "r.reuse_confidence, r.created_at "
            + "FROM experience_failure_signatures s JOIN experience_records r ON r.experience_id = s.experience_id "
            + $"WHERE {matching} "
            + "AND r.tenant_id = $tenant_id AND r.application_id = $application_id AND r.project_id = $project_id "
            + "AND r.team_id IS $team_id AND r.agent_id IS $agent_id AND r.user_id IS $user_id "
            + $"AND r.deleted_at IS NULL AND r.status IN ({string.Join(", ", statusNames)}) "
            + "AND r.reuse_confidence >= $minimum AND ($exclude = 0 OR r.model_authored = 0) "
            + "GROUP BY r.experience_id";

        return _store.Database.RunAsync(
            "The SQLite signature search failed.",
            async connection =>
            {
                using var transaction = connection.BeginTransaction(deferred: true);
                var found = new List<(Guid Id, ExperienceSignatureMatch Match, ExperienceSignatureResolution Resolution, double Confidence, long CreatedAt)>();
                using (var command = Command(connection, transaction, sql, [.. parameters]))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var exact = reader.GetInt64(1) == 1;
                        var resolved = exact ? reader.GetInt64(2) == 1 : reader.GetInt64(3) == 1;
                        found.Add((
                            ReadId(reader, 0),
                            exact ? ExperienceSignatureMatch.Exact : ExperienceSignatureMatch.Coarse,
                            resolved ? ExperienceSignatureResolution.Resolved : ExperienceSignatureResolution.Unresolved,
                            reader.GetDouble(4),
                            reader.GetInt64(5)));
                    }
                }

                var ranked = found
                    .OrderBy(hit => hit.Match == ExperienceSignatureMatch.Exact ? 0 : 1)
                    .ThenBy(hit => hit.Resolution == ExperienceSignatureResolution.Resolved ? 0 : 1)
                    .ThenByDescending(hit => hit.Confidence)
                    .ThenByDescending(hit => hit.CreatedAt)
                    .ThenBy(hit => hit.Id)
                    .Take(query.Limit)
                    .ToList();

                var candidates = new List<ExperienceSignatureCandidate>(ranked.Count);
                foreach (var hit in ranked)
                {
                    using var command = Command(
                        connection,
                        transaction,
                        $"SELECT {SqliteExperienceRecordStore.RecordColumns} FROM experience_records WHERE experience_id = $id",
                        ("$id", Id(hit.Id)));
                    using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && SqliteExperienceRecordStore.ReadRecord(reader) is { } record)
                    {
                        candidates.Add(new ExperienceSignatureCandidate(record, hit.Match, hit.Resolution));
                    }
                }

                transaction.Commit();
                return new ExperienceSignatureSearchResult(ExperienceStoreOutcome.Found, candidates, []);
            },
            cancellationToken);
    }
}
