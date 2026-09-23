using AgentExperience.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// <see cref="IExperienceReuseFeedbackStore"/> over PostgreSQL with plain Npgsql. It follows the same
/// order <see cref="PostgresExperienceRecordStore"/> and <see cref="PostgresExperienceGrantStore"/>
/// use -- validate the submission, check it against the host-established
/// <see cref="AuthorizationContext"/>, and only then open a connection -- and translates failures the
/// same way. The schema must already exist: <c>0008_reuse_feedback.sql</c> creates
/// <c>reuse_feedback</c> and its <c>reuse_feedback_exposures</c> rows, and the host applies it by
/// calling <see cref="ExperienceSchemaMigrator.MigrateAsync(NpgsqlDataSource, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The submission and its exposures commit together or not at all.</b> One transaction on one
/// connection inserts the submission row and every exposure row, so a run's feedback can never be half
/// recorded -- which matters because Core writes this ledger <em>before</em> submitting any confidence
/// evidence, and reads it back on a retry.
/// </para>
/// <para>
/// <b>This store decides nothing about benefit.</b> It writes the attribution decision Core made. It
/// never promotes <see cref="ExperienceReuseBenefit.Unknown"/>, never derives an evidence ID, and never
/// treats <see cref="RecordedExperienceReuseFeedback.ClaimedBenefit"/> as attribution. The database
/// enforces the same rule from its own side: a CHECK ties <c>benefit = 'Unknown'</c> to
/// <c>attribution_source = 'None'</c>, so the two can never drift.
/// </para>
/// <para>
/// <b>Idempotency is the feedback ID, and it is compared field by field.</b> A colliding
/// <see cref="RecordedExperienceReuseFeedback.FeedbackId"/> is read back inside the same transaction and
/// compared against the submission in hand -- every stored column, and the exposures in order.
/// Identical is <see cref="ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded"/> with nothing written;
/// anything else is <see cref="ExperienceReuseFeedbackStoreOutcome.Conflict"/>, again with nothing
/// written. A conflict is reported without the stored submission, because the colliding ID may name a
/// row in another scope and handing it back would be a cross-scope read.
/// </para>
/// <para>
/// <b>This store reads an Experience Record for exactly one reason.</b> There is still no foreign key
/// to <c>experience_records</c> and no join in the write: an exposed ID that resolves to nothing in the
/// scope is recorded exactly like one that resolves, and whether it resolves is decided later, by the
/// confidence path, against the record itself. The one exception is an <em>erased</em> record. Recording
/// an exposure to a tombstone would write the ID back into a ledger the erasure emptied, so a submission
/// naming one is <see cref="ExperienceReuseFeedbackStoreOutcome.Invalid"/>, naming the exposure by
/// position, with nothing written. Only tombstones in the submission's own scope are visible to that
/// check, so a refusal can never reveal another scope's. That read takes <c>FOR KEY SHARE</c> on the
/// records it names, so a record erased while the submission is being written is refused rather than
/// recorded against.
/// </para>
/// <para>
/// <b>The database's own CHECKs are defence in depth, not a second validation path.</b> Every rule
/// <c>0008</c> states is stated again in <see cref="ExperienceRecordValidator.ValidateReuseFeedback"/>
/// and refused there as <see cref="ExperienceReuseFeedbackStoreOutcome.Invalid"/>, before a connection
/// opens. A constraint violation reaching the server therefore means a writer bypassed this class or the
/// two definitions drifted, which is a fault rather than an expected condition -- so it surfaces as
/// <see cref="ExperienceStoreException"/>, loudly, instead of being translated into a typed refusal that
/// would make the drift look routine.
/// </para>
/// </remarks>
public sealed class PostgresExperienceReuseFeedbackStore : IExperienceReuseFeedbackStore
{
    /// <summary>The submission ledger. Created by <c>0008_reuse_feedback.sql</c>.</summary>
    internal const string Table = "agent_experience.reuse_feedback";

    /// <summary>The per-record exposure ledger. Created by <c>0008_reuse_feedback.sql</c>.</summary>
    internal const string ExposuresTable = "agent_experience.reuse_feedback_exposures";

    /// <summary>
    /// The submission columns every read selects, in the order <see cref="DecodeSubmission"/> expects
    /// (ordinals 0-21). <c>recorded_at</c> is deliberately absent: it is this store's own clock reading,
    /// so comparing it would make every replay a conflict.
    /// </summary>
    private const string SubmissionColumns =
        "run_id, tenant_id, application_id, project_id, team_id, agent_id, user_id, " +
        "run_outcome, claimed_benefit, benefit, attribution_source, reviewer_identity, evaluator_id, " +
        "verification_round_id, assessment_id, rationale, evidence_ids, attributed_at, " +
        "measure_kind, measure_value, trial_label, observed_at";

    /// <summary>
    /// The conditional insert. <c>ON CONFLICT DO NOTHING</c> rather than a pre-read: the primary key is
    /// the arbiter, so two hosts submitting the same feedback at once cannot both decide they are first.
    /// No row returned means the ID is taken, and the comparison below then decides what by.
    /// </summary>
    private static readonly string InsertSubmissionSql =
        $"INSERT INTO {Table} (feedback_id, {SubmissionColumns}, recorded_at) VALUES " +
        "(@feedback_id, @run_id, @tenant_id, @application_id, @project_id, @team_id, @agent_id, @user_id, " +
        "@run_outcome, @claimed_benefit, @benefit, @attribution_source, @reviewer_identity, @evaluator_id, " +
        "@verification_round_id, @assessment_id, @rationale, @evidence_ids, @attributed_at, " +
        "@measure_kind, @measure_value, @trial_label, @observed_at, @recorded_at) " +
        "ON CONFLICT (feedback_id) DO NOTHING RETURNING feedback_id";

    private static readonly string InsertExposureSql =
        $"INSERT INTO {ExposuresTable} (feedback_id, experience_id, ordinal, attributed, evidence_id) " +
        "VALUES (@feedback_id, @experience_id, @ordinal, @attributed, @evidence_id)";

    /// <summary>
    /// The stored submission, read by ID alone. There is deliberately no scope predicate: the primary
    /// key is global, so a submission stored in another scope has to be reported as a conflict rather
    /// than as "not here" -- otherwise the same ID could be recorded twice, once per scope, and a retry
    /// would have no way to tell which one it was replaying. Nothing read here is returned to a caller
    /// on the conflict path; it is only ever compared.
    /// </summary>
    private static readonly string SelectSubmissionSql =
        $"SELECT {SubmissionColumns} FROM {Table} WHERE feedback_id = @feedback_id";

    private static readonly string SelectExposuresSql =
        $"SELECT experience_id, attributed, evidence_id FROM {ExposuresTable} " +
        "WHERE feedback_id = @feedback_id ORDER BY ordinal";

    /// <summary>
    /// Every exposed record this scope actually holds, with its tombstone marker, locked for the rest of
    /// the transaction.
    /// <para>
    /// A run's exposure to a record that never existed here, or that was revoked, is still recordable --
    /// <c>0008</c> has no foreign key precisely so that "the run saw an ID that resolves to nothing" stays
    /// a fact worth keeping. An <em>erased</em> record is the one exception: writing its ID into this
    /// ledger would put back an association the erasure just removed, and a human assessment carries a
    /// reviewer identity and a free-text rationale about the record into an append-only table. A
    /// tombstone in another scope is invisible to this statement, so a refusal can never reveal one.
    /// </para>
    /// <para>
    /// <b>It selects live rows too, and locks them, on purpose.</b> Asking only for tombstones would
    /// lock nothing when every named record is still live -- which is the case a concurrent erasure
    /// turns into a lie between this read and the exposure inserts below it. Taking
    /// <c>FOR KEY SHARE</c> over every named record in scope, live or not, is what makes this a check
    /// that holds until the transaction commits rather than a check-then-write; see
    /// <see cref="PostgresExperienceRecordStore.RecordKeyShareLock"/>. The rows are locked in
    /// <c>experience_id</c> order so two submissions naming overlapping records cannot deadlock.
    /// </para>
    /// </summary>
    private static readonly string SelectExposedRecordStateSql =
        $"SELECT r.experience_id, r.{PostgresExperienceRecordStore.DeletedAtAlias} " +
        $"FROM {PostgresExperienceRecordStore.Table} r " +
        "WHERE r.experience_id = ANY(@experience_ids) " +
        $"AND {PostgresExperienceRecordStore.RecordScopePredicate} " +
        "ORDER BY r.experience_id " +
        PostgresExperienceRecordStore.RecordKeyShareLock;

    private static readonly IReadOnlyList<StoreValidationError> NoErrors = [];

    private readonly NpgsqlDataSource _dataSource;

    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a feedback store over a host-owned data source. The store never disposes it.</summary>
    /// <param name="dataSource">The Npgsql data source to open connections from.</param>
    /// <param name="timeProvider">
    /// The clock this store stamps <c>recorded_at</c> from -- its own reading of when the row landed,
    /// deliberately separate from the caller's <see cref="RecordedExperienceReuseFeedback.ObservedAt"/>.
    /// Defaults to <see cref="TimeProvider.System"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="dataSource"/> is <see langword="null"/>.</exception>
    public PostgresExperienceReuseFeedbackStore(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ExperienceReuseFeedbackStoreResult> RecordAsync(
        AuthorizationContext authorization,
        RecordedExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(feedback);

        var errors = ExperienceRecordValidator.ValidateReuseFeedback(feedback);
        if (errors.Count > 0)
        {
            return new(ExperienceReuseFeedbackStoreOutcome.Invalid, null, errors);
        }

        if (!authorization.Permits(feedback.Scope))
        {
            return new(ExperienceReuseFeedbackStoreOutcome.Denied, null, NoErrors);
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // Pinned, not inherited, for the same reason the lifecycle commit pins it: the expected
            // conditions here are decided by the primary key, never by a serialization failure a
            // stricter level would raise instead.
            await using var transaction = await connection
                .BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            var inserted = await InsertSubmissionAsync(connection, transaction, feedback, cancellationToken).ConfigureAwait(false);
            if (!inserted)
            {
                // The ID is taken. Read inside this transaction, which is then rolled back, so the
                // comparison can never be the thing that writes something.
                var stored = await ReadStoredAsync(connection, transaction, feedback.FeedbackId, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                if (stored is not null && SameContent(stored, feedback))
                {
                    return new(ExperienceReuseFeedbackStoreOutcome.AlreadyRecorded, stored, NoErrors);
                }

                // Different content under the same ID. The stored submission comes back only when this
                // caller's authorization covers its own scope -- a colliding ID must never disclose a
                // scope the caller has no authority over -- so a host whose retry was refused can still
                // see which records the stored submission named.
                return new(
                    ExperienceReuseFeedbackStoreOutcome.Conflict,
                    stored is not null && authorization.Permits(stored.Scope) ? stored : null,
                    NoErrors);
            }

            var erased = await ReadErasedExposuresAsync(connection, transaction, feedback, cancellationToken).ConfigureAwait(false);
            if (erased.Count > 0)
            {
                // Read and refused inside the transaction that is about to be rolled back, so a
                // submission naming an erased record writes nothing at all -- not the submission row the
                // insert above provisionally took, and not one exposure.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(ExperienceReuseFeedbackStoreOutcome.Invalid, null, erased);
            }

            for (var ordinal = 0; ordinal < feedback.Exposures.Count; ordinal++)
            {
                await InsertExposureAsync(connection, transaction, feedback.FeedbackId, feedback.Exposures[ordinal], ordinal, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ExperienceReuseFeedbackStoreOutcome.Recorded, feedback, NoErrors);
        }
        catch (Exception ex) when (PostgresExperienceRecordStore.IsInfrastructureFailure(ex, cancellationToken))
        {
            throw PostgresExperienceRecordStore.Translate(ex, "reuse feedback record", cancellationToken);
        }
    }

    private async Task<bool> InsertSubmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecordedExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(InsertSubmissionSql, connection, transaction);
        var parameters = command.Parameters;

        parameters.Add(new NpgsqlParameter<Guid>("feedback_id", feedback.FeedbackId));
        parameters.Add(new NpgsqlParameter<Guid>("run_id", feedback.RunId));
        PostgresExperienceRecordStore.AddScopeParameters(parameters, feedback.Scope);
        parameters.Add(Text("run_outcome", feedback.RunOutcome.ToString()));
        parameters.Add(Text("claimed_benefit", feedback.ClaimedBenefit.ToString()));
        parameters.Add(Text("benefit", feedback.Benefit.ToString()));
        parameters.Add(Text("attribution_source", feedback.AttributionSource.ToString()));
        parameters.Add(NullableText("reviewer_identity", feedback.ReviewerIdentity));
        parameters.Add(NullableText("evaluator_id", feedback.EvaluatorId));
        parameters.Add(NullableUuid("verification_round_id", feedback.VerificationRoundId));
        parameters.Add(NullableUuid("assessment_id", feedback.AssessmentId));
        parameters.Add(NullableText("rationale", feedback.Rationale));
        parameters.Add(new NpgsqlParameter("evidence_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = feedback.EvidenceIds is { Count: > 0 } ids ? ids.ToArray() : (object)DBNull.Value,
        });
        parameters.Add(new NpgsqlParameter("attributed_at", NpgsqlDbType.TimestampTz)
        {
            Value = feedback.AttributedAt is { } attributedAt
                ? PostgresExperienceRecordStore.ToStoredTimestamp(attributedAt)
                : (object)DBNull.Value,
        });
        parameters.Add(Text("measure_kind", feedback.Measure.Kind));
        parameters.Add(new NpgsqlParameter<double>("measure_value", feedback.Measure.Value));
        parameters.Add(NullableText("trial_label", feedback.TrialLabel));
        // Truncated the way every other stored timestamp is, so a replay's comparison is between the
        // value that was stored and the same value, not between it and a higher-precision original.
        parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "observed_at",
            PostgresExperienceRecordStore.ToStoredTimestamp(feedback.ObservedAt)));
        parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "recorded_at",
            PostgresExperienceRecordStore.ToStoredTimestamp(_timeProvider.GetUtcNow())));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task InsertExposureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid feedbackId,
        ExperienceReuseExposure exposure,
        int ordinal,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(InsertExposureSql, connection, transaction);
        var parameters = command.Parameters;

        parameters.Add(new NpgsqlParameter<Guid>("feedback_id", feedbackId));
        parameters.Add(new NpgsqlParameter<Guid>("experience_id", exposure.ExperienceId));
        parameters.Add(new NpgsqlParameter<int>("ordinal", ordinal));
        parameters.Add(new NpgsqlParameter<bool>("attributed", exposure.Attributed));
        parameters.Add(NullableUuid("evidence_id", exposure.EvidenceId));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Names every exposure whose record has been erased, as a validation error per exposure. The
    /// message is content-free and the path is an index, so a refusal says which position of the
    /// caller's own submission is unrecordable without echoing anything back.
    /// </summary>
    private static async Task<IReadOnlyList<StoreValidationError>> ReadErasedExposuresAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RecordedExperienceReuseFeedback feedback,
        CancellationToken cancellationToken)
    {
        var exposedIds = feedback.Exposures.Select(exposure => exposure.ExperienceId).Distinct().ToArray();
        if (exposedIds.Length == 0)
        {
            return NoErrors;
        }

        var erased = new HashSet<Guid>();
        await using (var command = new NpgsqlCommand(SelectExposedRecordStateSql, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("experience_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = exposedIds,
            });
            PostgresExperienceRecordStore.AddScopeParameters(command.Parameters, feedback.Scope);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Every named record in scope comes back and is now locked; only the tombstones among
                // them are refusals.
                if (!reader.IsDBNull(1))
                {
                    erased.Add(reader.GetGuid(0));
                }
            }
        }

        if (erased.Count == 0)
        {
            return NoErrors;
        }

        var errors = new List<StoreValidationError>();
        for (var ordinal = 0; ordinal < feedback.Exposures.Count; ordinal++)
        {
            if (erased.Contains(feedback.Exposures[ordinal].ExperienceId))
            {
                errors.Add(new(
                    $"Exposures[{ordinal}].ExperienceId",
                    "names an Experience Record that has been erased; nothing may be recorded against it again."));
            }
        }

        return errors;
    }

    /// <summary>
    /// Reads the submission stored under this feedback ID, with its exposures in stored order. Read
    /// inside the caller's transaction, which is then rolled back, so a comparison can never be the
    /// thing that writes something.
    /// </summary>
    private static async Task<RecordedExperienceReuseFeedback?> ReadStoredAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid feedbackId,
        CancellationToken cancellationToken)
    {
        RecordedExperienceReuseFeedback stored;
        await using (var command = new NpgsqlCommand(SelectSubmissionSql, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("feedback_id", feedbackId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // The insert said the key was taken and the read says it is not. Nothing deletes from
                // this ledger, so this cannot happen; reporting it as no stored submission writes
                // nothing, which is the safe answer to a database that just contradicted itself.
                return null;
            }

            stored = DecodeSubmission(reader, feedbackId);
        }

        var exposures = new List<ExperienceReuseExposure>();
        await using (var command = new NpgsqlCommand(SelectExposuresSql, connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("feedback_id", feedbackId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                exposures.Add(new(
                    reader.GetGuid(0),
                    reader.GetBoolean(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2)));
            }
        }

        return stored with { Exposures = exposures };
    }

    private static RecordedExperienceReuseFeedback DecodeSubmission(NpgsqlDataReader reader, Guid feedbackId) => new(
        feedbackId,
        reader.GetGuid(0),
        new Scope(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            NullableString(reader, 4),
            NullableString(reader, 5),
            NullableString(reader, 6)),
        Enum.Parse<TaskVerificationStatus>(reader.GetString(7)),
        Enum.Parse<ExperienceReuseBenefit>(reader.GetString(8)),
        Enum.Parse<ExperienceReuseBenefit>(reader.GetString(9)),
        Enum.Parse<ReuseAttributionSource>(reader.GetString(10)),
        NullableString(reader, 11),
        NullableString(reader, 12),
        reader.IsDBNull(13) ? null : reader.GetGuid(13),
        reader.IsDBNull(14) ? null : reader.GetGuid(14),
        NullableString(reader, 15),
        reader.IsDBNull(16) ? [] : reader.GetFieldValue<Guid[]>(16),
        reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        new ReuseMeasure(reader.GetString(18), reader.GetDouble(19)),
        NullableString(reader, 20),
        reader.GetFieldValue<DateTimeOffset>(21),
        []);

    /// <summary>
    /// Whether the stored submission is the one in hand. Every stored field is compared explicitly,
    /// rather than through record equality, because two of them need their own rule: the timestamps are
    /// compared against the truncated value that was actually written, and the two lists are compared as
    /// sequences (record equality would compare them by reference and report every replay as a
    /// conflict). The exposures are part of the comparison because they are part of the submission --
    /// a resubmission naming a different set of records is a different submission, whatever its header
    /// columns say. Core normalizes the exposure order before deriving ordinals, so comparing them
    /// positionally here compares record <em>sets</em>, not the order a caller happened to list them in.
    /// </summary>
    private static bool SameContent(RecordedExperienceReuseFeedback stored, RecordedExperienceReuseFeedback submitted) =>
        stored.RunId == submitted.RunId
        && stored.Scope == submitted.Scope
        && stored.RunOutcome == submitted.RunOutcome
        && stored.ClaimedBenefit == submitted.ClaimedBenefit
        && stored.Benefit == submitted.Benefit
        && stored.AttributionSource == submitted.AttributionSource
        && string.Equals(stored.ReviewerIdentity, submitted.ReviewerIdentity, StringComparison.Ordinal)
        && string.Equals(stored.EvaluatorId, submitted.EvaluatorId, StringComparison.Ordinal)
        && stored.VerificationRoundId == submitted.VerificationRoundId
        && stored.AssessmentId == submitted.AssessmentId
        && string.Equals(stored.Rationale, submitted.Rationale, StringComparison.Ordinal)
        && stored.EvidenceIds.SequenceEqual(submitted.EvidenceIds)
        && stored.AttributedAt == Stored(submitted.AttributedAt)
        && string.Equals(stored.Measure.Kind, submitted.Measure.Kind, StringComparison.Ordinal)
        // Bit-for-bit, not within a tolerance: a measure that differs at all is different data, and this
        // is an identity comparison rather than a numeric one.
        && stored.Measure.Value.Equals(submitted.Measure.Value)
        && string.Equals(stored.TrialLabel, submitted.TrialLabel, StringComparison.Ordinal)
        && stored.ObservedAt == PostgresExperienceRecordStore.ToStoredTimestamp(submitted.ObservedAt)
        && stored.Exposures.SequenceEqual(submitted.Exposures);

    private static DateTimeOffset? Stored(DateTimeOffset? value) =>
        value is { } set ? PostgresExperienceRecordStore.ToStoredTimestamp(set) : null;

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static NpgsqlParameter Text(string name, string value) =>
        new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value };

    private static NpgsqlParameter NullableText(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = value is null ? DBNull.Value : value };

    private static NpgsqlParameter NullableUuid(string name, Guid? value) =>
        new(name, NpgsqlDbType.Uuid) { Value = value is { } id ? id : DBNull.Value };
}
