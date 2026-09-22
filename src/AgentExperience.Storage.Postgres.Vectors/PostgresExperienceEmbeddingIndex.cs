using System.Data.Common;
using System.Globalization;
using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace AgentExperience.Storage.Postgres.Vectors;

/// <summary>
/// <see cref="IExperienceEmbeddingIndex"/> over PostgreSQL and pgvector. It follows exactly the order
/// <see cref="PostgresExperienceRecordStore"/> uses -- validate the request, check it against the
/// host-established <see cref="AuthorizationContext"/>, and only then open a connection and run
/// parameterized SQL whose predicates apply the exact scope -- and translates failures the same way.
/// The schema must already exist: <c>0004_add_experience_embeddings.sql</c> creates the extension and
/// the <c>experience_embeddings</c> table, and the host applies it by calling
/// <see cref="ExperienceVectorSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>
/// after the base adapter's own migration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writes are conditional, in SQL.</b> A write is an <c>INSERT ... SELECT</c> whose source is the
/// canonical record row itself, matched on the exact scope <em>and</em> on the revision the write
/// names. So a record that has moved on writes nothing (<see cref="ExperienceIndexOutcome.Stale"/>)
/// and a record that no longer exists writes nothing and creates no row
/// (<see cref="ExperienceIndexOutcome.Missing"/>) -- an in-flight write can never resurrect a deleted
/// record, because there is no row for its <c>SELECT</c> to read. The scope columns stored alongside
/// the vector are copied from that same record row, never from caller input, so they cannot disagree
/// with the record they describe.
/// </para>
/// <para>
/// <b>Never inside the canonical transaction.</b> Every statement here runs on its own connection
/// from the host's pooled data source. This adapter is only ever called <em>after</em> a record's
/// lifecycle commit has landed; it never participates in one. That is a property of the design, not
/// of the driver: embeddings are derived data, and the canonical write must not depend on a provider.
/// </para>
/// <para>
/// <b>Comparability is a predicate, not a check afterwards.</b> A search filters on the query's model
/// ID and on the query vector's width before any distance is computed, so a vector from another model
/// or of another dimension is never compared. When the scope holds embeddings but none of them are
/// comparable, the result says which (<see cref="ExperienceVectorSearchOutcome.ModelMismatch"/> or
/// <see cref="ExperienceVectorSearchOutcome.DimensionMismatch"/>) rather than looking like an empty
/// match.
/// </para>
/// <para>
/// <b>Relevance.</b> Distance is pgvector's cosine distance (<c>&lt;=&gt;</c>), which lies in [0, 2];
/// the reported relevance is <c>1 - distance / 2</c>, so it is already in [0, 1] with 1 for an exact
/// direction match. Like the text channel's relevance it is a within-search measure.
/// </para>
/// <para>
/// This adapter reads and writes only <c>agent_experience.experience_embeddings</c>. It needs
/// <c>SELECT</c> on <c>agent_experience.experience_records</c> and never writes to it.
/// </para>
/// </remarks>
public sealed class PostgresExperienceEmbeddingIndex : IExperienceEmbeddingIndex
{
    /// <summary>The derived embedding table. Created by <c>0004_add_experience_embeddings.sql</c>.</summary>
    internal const string Table = "agent_experience.experience_embeddings";

    /// <summary>
    /// <see cref="PostgresExperienceRecordStore.SelectColumns"/> qualified with the <c>r</c> alias.
    /// Derived from the shared constant rather than retyped, so a column added there cannot silently
    /// shift this reader's ordinals: <see cref="PostgresExperienceRecordStore.ReadRecord"/> still sees
    /// ordinals 0-17 in exactly the documented order, and the distance is appended after them.
    /// </summary>
    private static readonly string RecordColumns =
        "r." + PostgresExperienceRecordStore.SelectColumns.Replace(", ", ", r.", StringComparison.Ordinal);

    /// <summary>The alias the distance is selected under, read back by name rather than by ordinal.</summary>
    private const string DistanceColumn = "distance";

    /// <summary>
    /// The same exact-scope predicate, qualified with the <c>e</c> alias. It is redundant with the
    /// <c>r</c>-aliased one -- the embedding's scope columns are copied from the record row inside the
    /// write, so they cannot disagree -- and it is applied anyway, because it is what lets
    /// <c>ix_experience_embeddings_scope_model</c> serve the search: a btree on
    /// <c>(tenant_id, application_id, project_id, model_id, dimension)</c> cannot be used when the only
    /// predicates on the embeddings table are the trailing two columns.
    /// </summary>
    private static readonly string EmbeddingScopePredicate =
        PostgresExperienceRecordStore.RecordScopePredicate.Replace("r.", "e.", StringComparison.Ordinal);

    /// <summary>
    /// What the vector channel may return: the exact scope on both sides of the join, or an active
    /// sharing grant naming the record and permitting the requesting scope. It is the base adapter's
    /// predicate, composed rather than retyped, so the two retrieval channels honour byte-for-byte the
    /// same rule about what a grant does.
    /// <para>
    /// The exact-scope branch keeps both aliases, so the common case can still be served by
    /// <c>ix_experience_embeddings_scope_model</c>. The grant branch is stated on the record side
    /// only: an embedding's scope columns are copied from its record, so a granted record's embedding
    /// carries the <em>owner's</em> scope and an <c>e</c>-side exact match would exclude exactly the
    /// rows the grant exists to admit.
    /// </para>
    /// </summary>
    private static readonly string ReadableJoinScopePredicate =
        $"(({EmbeddingScopePredicate} AND {PostgresExperienceRecordStore.RecordScopePredicate}) " +
        $"OR {PostgresExperienceRecordStore.ActiveGrantPredicate})";

    /// <summary>
    /// The same join predicate with the grant branch removed, for a database that has no
    /// <c>experience_grants</c> table or a role that may not read it.
    /// </summary>
    private static readonly string ExactJoinScopePredicate =
        $"({EmbeddingScopePredicate} AND {PostgresExperienceRecordStore.RecordScopePredicate})";

    /// <summary>
    /// The conditional write. The target table is aliased <c>t</c> so the conflict action can name it
    /// unambiguously, and the source row is the canonical record itself: nothing is inserted unless
    /// that record exists, in exactly this scope, at exactly this revision.
    /// <para>
    /// The <c>ON CONFLICT</c> guard is a second line of defence for two writes racing each other: an
    /// older in-flight write can never overwrite a vector already stored from a newer revision.
    /// </para>
    /// </summary>
    private const string WriteSql =
        $"INSERT INTO {Table} AS t (experience_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "model_id, dimension, content_hash, source_revision, embedding, created_at, updated_at) " +
        "SELECT r.experience_id, r.tenant_id, r.application_id, r.project_id, r.team_id, r.agent_id, r.user_id, " +
        "@model_id, @dimension, @content_hash, @source_revision, CAST(@embedding AS vector), @now, @now " +
        $"FROM {PostgresExperienceRecordStore.Table} r " +
        $"WHERE r.experience_id = @experience_id AND {PostgresExperienceRecordStore.RecordScopePredicate} " +
        "AND r.revision = @source_revision " +
        "ON CONFLICT (experience_id) DO UPDATE SET " +
        "model_id = EXCLUDED.model_id, dimension = EXCLUDED.dimension, content_hash = EXCLUDED.content_hash, " +
        "source_revision = EXCLUDED.source_revision, embedding = EXCLUDED.embedding, updated_at = EXCLUDED.updated_at " +
        "WHERE t.source_revision <= EXCLUDED.source_revision";

    /// <summary>
    /// Removal, matched on the embedding row's own scope columns rather than through a join to the
    /// record. That matters: a record can leave eligibility and later be removed entirely, and the
    /// vector must still be removable either way. Those columns were copied from the record row when
    /// the vector was written, so they cannot disagree with the record they describe.
    /// </summary>
    private static readonly string RemoveSql =
        $"DELETE FROM {Table} e WHERE e.experience_id = @experience_id AND {EmbeddingScopePredicate}";

    /// <summary>
    /// Reports a rejected write: the record's current revision, or nothing at all when it is not in
    /// this scope. Identical whichever scope actually owns the record, so it reveals nothing.
    /// </summary>
    private const string ProbeRevisionSql =
        $"SELECT r.revision FROM {PostgresExperienceRecordStore.Table} r " +
        $"WHERE r.experience_id = @experience_id AND {PostgresExperienceRecordStore.RecordScopePredicate}";

    /// <summary>
    /// One statement, so a record's revision, the summary read at that revision, and the descriptor of
    /// whatever vector is stored for it all come from a single snapshot. The summary is assembled from
    /// exactly the three fields <c>0003</c> indexes for text, so both channels describe the same claim
    /// about the record. A left join keeps a never-indexed record in the list with a null descriptor.
    /// </summary>
    private const string ScanSql =
        "SELECT r.experience_id, r.revision, r.task_id, r.payload ->> 'taskSummary', " +
        "r.payload -> 'reflection' ->> 'lesson', e.model_id, e.dimension, e.content_hash, e.source_revision " +
        $"FROM {PostgresExperienceRecordStore.Table} r " +
        $"LEFT JOIN {Table} e ON e.experience_id = r.experience_id " +
        $"WHERE {PostgresExperienceRecordStore.RecordScopePredicate} " +
        // The search's own predicates, applied here too: a record whose vector could never be returned
        // is never embedded, so its summary and lesson never leave the database for a third party.
        "AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence";

    private const string ScanIdPredicate = " AND r.experience_id = ANY(@experience_ids)";

    /// <summary>The keyset cursor. The sort key is the primary key, so this is a stable, gap-free walk.</summary>
    private const string ScanCursorPredicate = " AND r.experience_id > @start_after_id";

    private const string ScanOrderAndLimit = " ORDER BY r.experience_id LIMIT @limit";

    /// <summary>
    /// What the scope actually holds, used only when a search matched nothing, to tell "nothing is
    /// similar" apart from "nothing here is comparable". Two <c>EXISTS</c> probes in one statement,
    /// not an aggregate: each stops at the first matching row, so the healthy "nothing similar" case
    /// costs two index probes rather than a scan of every in-scope embedding, and the answer cannot
    /// depend on how many distinct groups happened to fit under a limit.
    /// <para>
    /// The two questions are asked separately and in the right order: is there <em>any</em> comparable
    /// population at all, and is there one for this exact model? A scope with 500 models would have
    /// made a <c>GROUP BY ... LIMIT</c> report <c>ModelMismatch</c> whenever this model's group fell
    /// outside the limit, even though the real cause was the width.
    /// </para>
    /// </summary>
    private static readonly string CompatibilityProbeExactSql =
        $"SELECT EXISTS (SELECT 1 FROM {Table} e JOIN {PostgresExperienceRecordStore.Table} r ON r.experience_id = e.experience_id " +
        $"WHERE {ExactJoinScopePredicate} " +
        "AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence), " +
        $"EXISTS (SELECT 1 FROM {Table} e JOIN {PostgresExperienceRecordStore.Table} r ON r.experience_id = e.experience_id " +
        $"WHERE {ExactJoinScopePredicate} " +
        "AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence AND e.model_id = @model_id)";

    private static readonly string CompatibilityProbeSql =
        $"SELECT EXISTS (SELECT 1 FROM {Table} e JOIN {PostgresExperienceRecordStore.Table} r ON r.experience_id = e.experience_id " +
        $"WHERE {ReadableJoinScopePredicate} " +
        "AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence), " +
        $"EXISTS (SELECT 1 FROM {Table} e JOIN {PostgresExperienceRecordStore.Table} r ON r.experience_id = e.experience_id " +
        $"WHERE {ReadableJoinScopePredicate} " +
        "AND r.status = ANY(@statuses) AND r.reuse_confidence >= @min_confidence AND e.model_id = @model_id)";

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private static readonly IReadOnlyList<ExperienceCandidate> NoCandidates = [];

    private static readonly IReadOnlyList<ExperienceIndexTarget> NoTargets = [];

    private readonly NpgsqlDataSource _dataSource;

    private readonly PostgresGrantSupport _grants;

    /// <summary>Creates an embedding index over a host-owned data source. The index never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <param name="onGrantsUnavailable">
    /// Called at most once, when a search first finds <c>agent_experience.experience_grants</c> missing
    /// or unreadable and falls back to the exact-scope predicate. Optional.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceEmbeddingIndex(
        NpgsqlDataSource dataSource,
        Action<ExperienceGrantSupportNotice>? onGrantsUnavailable = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _grants = new PostgresGrantSupport(onGrantsUnavailable);
    }

    /// <inheritdoc />
    public async Task<ExperienceIndexWriteResult> WriteAsync(
        AuthorizationContext authorization,
        ExperienceIndexWrite write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(write);

        var errors = ExperienceRecordValidator.ValidateIndexWrite(write);
        if (errors.Count > 0)
        {
            return new(ExperienceIndexOutcome.Invalid, 0, errors);
        }

        if (!authorization.Permits(write.Scope))
        {
            // Fail-closed, and before any connection opens: no statement is issued at all.
            return new(ExperienceIndexOutcome.Denied, 0, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // One connection for the write and, if it wrote nothing, for the probe that explains why.
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            int written;
            await using (var command = new NpgsqlCommand(WriteSql, connection))
            {
                var parameters = command.Parameters;
                parameters.Add(new NpgsqlParameter<Guid>("experience_id", write.ExperienceId));
                PostgresExperienceRecordStore.AddScopeParameters(parameters, write.Scope);
                parameters.Add(new NpgsqlParameter<string>("model_id", NpgsqlDbType.Text) { TypedValue = write.Descriptor.ModelId });
                parameters.Add(new NpgsqlParameter<int>("dimension", write.Descriptor.Dimension));
                parameters.Add(new NpgsqlParameter<string>("content_hash", NpgsqlDbType.Text) { TypedValue = write.Descriptor.ContentHash });
                parameters.Add(new NpgsqlParameter<long>("source_revision", write.Descriptor.SourceRevision));
                parameters.Add(new NpgsqlParameter<string>("embedding", NpgsqlDbType.Text) { TypedValue = ToVectorLiteral(write.Vector) });
                parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", ToStoredTimestamp(DateTimeOffset.UtcNow)));

                written = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (written > 0)
            {
                return new(ExperienceIndexOutcome.Written, 0, NoErrors);
            }

            // Nothing was written. Either the record is not in this scope at all, or its revision has
            // moved past the one this write was computed from.
            var current = await ProbeRevisionAsync(connection, write.Scope, write.ExperienceId, cancellationToken).ConfigureAwait(false);
            return current is { } revision
                ? new(ExperienceIndexOutcome.Stale, revision, NoErrors)
                : new(ExperienceIndexOutcome.Missing, 0, NoErrors);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "embedding write", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceIndexScanResult> ScanAsync(
        AuthorizationContext authorization,
        ExperienceIndexScan scan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scan);

        var errors = ExperienceRecordValidator.ValidateIndexScan(scan);
        if (errors.Count > 0)
        {
            return new(ExperienceStoreOutcome.Invalid, NoTargets, errors);
        }

        if (!authorization.Permits(scan.Scope))
        {
            return new(ExperienceStoreOutcome.Denied, NoTargets, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var sql = ScanSql
                + (scan.ExperienceIds is null ? string.Empty : ScanIdPredicate)
                + (scan.StartAfterId is null ? string.Empty : ScanCursorPredicate)
                + ScanOrderAndLimit;

            await using var command = _dataSource.CreateCommand(sql);
            var parameters = command.Parameters;
            PostgresExperienceRecordStore.AddScopeParameters(parameters, scan.Scope);
            parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
            {
                TypedValue = [.. scan.EligibleStatuses.Distinct().Select(status => status.ToString())],
            });
            parameters.Add(new NpgsqlParameter<double>("min_confidence", scan.MinimumConfidence));
            if (scan.ExperienceIds is not null)
            {
                parameters.Add(new NpgsqlParameter<Guid[]>("experience_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    TypedValue = [.. scan.ExperienceIds.Distinct()],
                });
            }

            if (scan.StartAfterId is { } startAfterId)
            {
                parameters.Add(new NpgsqlParameter<Guid>("start_after_id", startAfterId));
            }

            parameters.Add(new NpgsqlParameter<int>("limit", scan.Limit));

            var targets = new List<ExperienceIndexTarget>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                targets.Add(ReadTarget(reader));
            }

            return new(
                ExperienceStoreOutcome.Found,
                targets,
                NoErrors,
                targets.Count > 0 ? targets[^1].ExperienceId : null);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "embedding scan", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceVectorSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceVectorQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(query);

        var errors = ExperienceRecordValidator.ValidateVectorQuery(query);
        if (errors.Count > 0)
        {
            return new(ExperienceVectorSearchOutcome.Invalid, NoCandidates, errors);
        }

        if (!authorization.Permits(query.Scope))
        {
            return new(ExperienceVectorSearchOutcome.Denied, NoCandidates, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var dimension = query.Vector.Length;
        var statuses = query.EligibleStatuses.Distinct().Select(status => status.ToString()).ToArray();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return await RunSearchAsync(connection, query, dimension, statuses, _grants.Available, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (_grants.ShouldFallBack(ex, "vector search", cancellationToken))
            {
                // No grant table, or no permission to read it: search the exact scope only.
                return await RunSearchAsync(connection, query, dimension, statuses, readable: false, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "vector search", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ExperienceIndexRemoveResult> RemoveAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var errors = ExperienceRecordValidator.ValidateIndexRemove(scope, experienceId);
        if (errors.Count > 0)
        {
            return new(ExperienceIndexRemoveOutcome.Invalid, errors);
        }

        if (!authorization.Permits(scope))
        {
            // Fail-closed, and before any connection opens.
            return new(ExperienceIndexRemoveOutcome.Denied, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var command = _dataSource.CreateCommand(RemoveSql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, scope);

            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Never indexed, already removed, or in another scope: one outcome for all three, so a
            // repeated removal is free and a foreign-scope attempt reveals nothing.
            return new(
                removed > 0 ? ExperienceIndexRemoveOutcome.Removed : ExperienceIndexRemoveOutcome.NotIndexed,
                NoErrors);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "embedding removal", cancellationToken);
        }
    }

    private static async Task<ExperienceVectorSearchResult> RunSearchAsync(
        NpgsqlConnection connection,
        ExperienceVectorQuery query,
        int dimension,
        string[] statuses,
        bool readable,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ExperienceCandidate>();
        await using (var command = new NpgsqlCommand(SearchSql(dimension, readable), connection))
        {
            var parameters = command.Parameters;
            PostgresExperienceRecordStore.AddScopeParameters(parameters, query.Scope);
            parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
            parameters.Add(new NpgsqlParameter<double>("min_confidence", query.MinimumConfidence));
            parameters.Add(new NpgsqlParameter<string>("model_id", NpgsqlDbType.Text) { TypedValue = query.ModelId });
            parameters.Add(new NpgsqlParameter<string>("query_vector", NpgsqlDbType.Text) { TypedValue = ToVectorLiteral(query.Vector) });
            parameters.Add(new NpgsqlParameter<int>("limit", query.Limit));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidates.Add(new ExperienceCandidate(
                    PostgresExperienceRecordStore.ReadRecord(reader),
                    ReadRelevance(reader),
                    PostgresExperienceRecordStore.ReadSharedByGrant(reader)));
            }
        }

        if (candidates.Count > 0)
        {
            return new(ExperienceVectorSearchOutcome.Found, candidates, NoErrors);
        }

        // Only now -- an empty answer is the one case where "nothing similar" and "nothing
        // comparable" look the same from outside, and a host must be able to tell them apart. The
        // probe sees exactly what the search saw, grants included, so a recipient whose only
        // comparable population arrives through a grant is told which mismatch it hit.
        var mismatch = await ProbeCompatibilityAsync(connection, query, statuses, readable, cancellationToken).ConfigureAwait(false);
        return new(mismatch ?? ExperienceVectorSearchOutcome.Found, NoCandidates, NoErrors);
    }

    /// <summary>
    /// The nearest-neighbour statement for one dimension. The dimension is written into the SQL rather
    /// than parameterized because a pgvector type modifier is part of the type, not a value -- it can
    /// never be a parameter. It is an <see cref="int"/> the validator has already bounded, so nothing
    /// caller-controlled reaches the statement text.
    /// <para>
    /// Both the stored vector and the query vector are cast to <c>vector(n)</c> so the expression
    /// matches the partial HNSW index <see cref="ExperienceVectorIndexMaintenance"/> creates. The
    /// <c>e.dimension = n</c> predicate is what makes the cast safe: a row of another width is filtered
    /// out before the distance expression is ever evaluated on it.
    /// </para>
    /// </summary>
    /// <summary>
    /// The exact statement the search issues, for a test to hand to <c>EXPLAIN</c>. Asserting the
    /// planner's choice is only meaningful against the real statement: an approximation would prove the
    /// index matches something this adapter never runs.
    /// </summary>
    internal static string SearchSqlForTesting(int dimension) => SearchSql(dimension, readable: true);

    private static string SearchSql(int dimension, bool readable)
    {
        var width = dimension.ToString(CultureInfo.InvariantCulture);
        var scope = readable ? ReadableJoinScopePredicate : ExactJoinScopePredicate;
        var shared = readable
            ? PostgresExperienceRecordStore.SharedByGrantColumn
            : "false AS " + PostgresExperienceRecordStore.SharedByGrantAlias;

        return $"SELECT {RecordColumns}, {shared}, " +
            $"(e.embedding::vector({width}) <=> CAST(@query_vector AS vector({width}))) AS {DistanceColumn} " +
            $"FROM {Table} e " +
            $"JOIN {PostgresExperienceRecordStore.Table} r ON r.experience_id = e.experience_id " +
            // Both sides of the join carry the scope. The r-side is the authoritative one; the e-side is
            // what makes ix_experience_embeddings_scope_model usable (see EmbeddingScopePredicate).
            // An active grant is the alternative to that exact match, decided in SQL like the rest.
            $"WHERE {scope} " +
            "AND r.status = ANY(@statuses) " +
            "AND r.reuse_confidence >= @min_confidence " +
            "AND e.model_id = @model_id " +
            $"AND e.dimension = {width} " +
            // The distance expression is repeated rather than referenced by its alias, and it is the
            // only sort key: an index scan can supply this ordering directly, while a tie-break on
            // r.experience_id would force the whole join to be sorted and the HNSW index never to be
            // used. Exact distance ties are broken arbitrarily here as a result, which costs nothing --
            // Core re-sorts every candidate by score and breaks its own ties on ExperienceId, so the
            // order a caller sees is still total and stable.
            $"ORDER BY (e.embedding::vector({width}) <=> CAST(@query_vector AS vector({width}))) LIMIT @limit";
    }

    private static async Task<long?> ProbeRevisionAsync(
        NpgsqlConnection connection,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ProbeRevisionSql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
        PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, scope);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long revision ? revision : null;
    }

    /// <summary>
    /// Decides why an empty search was empty. <see langword="null"/> means the scope simply held no
    /// comparable-or-otherwise embedding to match, which is an answer rather than a fallback.
    /// </summary>
    private static async Task<ExperienceVectorSearchOutcome?> ProbeCompatibilityAsync(
        NpgsqlConnection connection,
        ExperienceVectorQuery query,
        string[] statuses,
        bool readable,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(readable ? CompatibilityProbeSql : CompatibilityProbeExactSql, connection);
        var parameters = command.Parameters;
        PostgresExperienceRecordStore.AddScopeParameters(parameters, query.Scope);
        parameters.Add(new NpgsqlParameter<string[]>("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = statuses });
        parameters.Add(new NpgsqlParameter<double>("min_confidence", query.MinimumConfidence));

        parameters.Add(new NpgsqlParameter<string>("model_id", NpgsqlDbType.Text) { TypedValue = query.ModelId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sawAnything = reader.GetBoolean(0);
        var sawThisModel = reader.GetBoolean(1);

        if (!sawAnything)
        {
            return null;
        }

        // The search already filtered on both model and dimension and found nothing, so whichever of
        // the two the stored rows disagree on is the one to report. The dimension is named only when
        // the model itself matched, so "wrong model" is never reported as "wrong width".
        return sawThisModel ? ExperienceVectorSearchOutcome.DimensionMismatch : ExperienceVectorSearchOutcome.ModelMismatch;
    }

    private static ExperienceIndexTarget ReadTarget(DbDataReader reader)
    {
        try
        {
            var stored = reader.IsDBNull(5)
                ? null
                : new ExperienceEmbeddingDescriptor(
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetString(7),
                    reader.GetInt64(8));

            return new ExperienceIndexTarget(
                reader.GetGuid(0),
                reader.GetInt64(1),
                ExperienceRetrievalSummary.For(
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)),
                stored);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    /// <summary>
    /// Turns cosine distance into the normalized [0, 1] relevance every
    /// <see cref="ExperienceCandidate"/> carries: <c>1 - distance / 2</c>, since <c>&lt;=&gt;</c>
    /// lies in [0, 2]. A NaN -- which pgvector returns for a zero-magnitude vector -- scores 0 rather
    /// than travelling into Core's ranking arithmetic and poisoning every comparison against it.
    /// </summary>
    private static double ReadRelevance(DbDataReader reader)
    {
        double distance;
        try
        {
            distance = reader.GetDouble(reader.GetOrdinal(DistanceColumn));
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }

        if (double.IsNaN(distance))
        {
            return 0d;
        }

        return Math.Clamp(1d - (distance / 2d), 0d, 1d);
    }

    /// <summary>
    /// Formats a vector as pgvector's own text literal, sent as text and cast in SQL. Going through
    /// the literal rather than a mapped CLR type is deliberate: it means this adapter works on any
    /// <see cref="NpgsqlDataSource"/> the host built, whether or not <c>UseVector()</c> was called on
    /// its builder, so a host cannot misconfigure the two halves of the schema against each other.
    /// <see cref="Pgvector.Vector"/> owns the formatting, which is invariant-culture by construction.
    /// </summary>
    private static string ToVectorLiteral(ReadOnlyMemory<float> vector) => new Vector(vector).ToString();

    /// <summary>
    /// Truncates to whole microseconds in UTC, which is the precision PostgreSQL's <c>timestamptz</c>
    /// keeps -- matching <see cref="PostgresExperienceRecordStore"/>, so a timestamp read back from
    /// either table compares equal to the value that was sent.
    /// </summary>
    private static DateTimeOffset ToStoredTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - (utcTicks % 10), TimeSpan.Zero);
    }
}
