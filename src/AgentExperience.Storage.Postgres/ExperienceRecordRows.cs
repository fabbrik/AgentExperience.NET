using System.Data.Common;
using AgentExperience.Abstractions;
using Npgsql;
using static AgentExperience.Storage.Postgres.ExperienceRecordSql;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// The record store's row decoding, including what other components share: records (opening sealed ones),
/// lifecycle events, the grant columns a read selects, and the stored enum texts. Every decoder reports schema drift or a corrupt row as an <see cref="ExperienceStoreException"/>.
/// </summary>
internal static class ExperienceRecordRows
{
    /// <summary>
    /// Reads the shared-by-grant flag by name. A reader that did not select it is treated as "not
    /// shared", which is the safe direction: a consumer that sees no flag keeps its strict scope check.
    /// </summary>
    internal static bool ReadSharedByGrant(DbDataReader reader)
    {
        try
        {
            var ordinal = reader.GetOrdinal(SharedByGrantAlias);
            return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the permitting grant's ID by name. A reader that did not select it is treated as "not
    /// told which grant", which is the safe direction: a consumer distinguishes that from "no grant"
    /// by the shared flag, and an audited read that cannot name its grant fails rather than inventing
    /// one.
    /// </summary>
    internal static Guid? ReadPermittingGrant(DbDataReader reader)
    {
        try
        {
            var ordinal = reader.GetOrdinal(PermittingGrantAlias);
            return reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the permitting grant's disclosure level by name. A reader that did not select it, a null,
    /// and a value this build does not know are all "not told", which every consumer renders as
    /// <see cref="ExperienceGrantDisclosure.LessonOnly"/> -- the least disclosure -- rather than
    /// guessing wider.
    /// </summary>
    internal static ExperienceGrantDisclosure? ReadPermittingDisclosure(DbDataReader reader)
    {
        try
        {
            var ordinal = reader.GetOrdinal(PermittingDisclosureAlias);
            return reader.IsDBNull(ordinal) ? null : ParseDisclosure(reader.GetString(ordinal));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the permitting grant's argument allowlist by name. A reader that did not select it, a null, and a
    /// stored value that does not parse are all "the owner named no key": no borrowed argument value is shown.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadPermittingApproachArguments(DbDataReader reader)
    {
        try
        {
            var ordinal = reader.GetOrdinal(PermittingApproachArgumentsAlias);
            return reader.IsDBNull(ordinal) ? null : GrantApproachArgumentsCodec.Parse(reader.GetString(ordinal));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a stored disclosure level by its exact name. Anything else is <see langword="null"/>:
    /// a level this build cannot name is never widened into one it can.
    /// </summary>
    internal static ExperienceGrantDisclosure? ParseDisclosure(string? stored) =>
        stored is not null
        && Enum.TryParse<ExperienceGrantDisclosure>(stored, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), stored, StringComparison.Ordinal)
            ? parsed
            : null;

    /// <summary>
    /// Reads the tombstone marker by name. A reader that did not select it is treated as "not erased",
    /// which is the safe direction for a caller that never asked: every statement that could meet a
    /// tombstone either selects this column or filters tombstones out in SQL.
    /// </summary>
    internal static bool ReadDeleted(DbDataReader reader)
    {
        try
        {
            return !reader.IsDBNull(reader.GetOrdinal(DeletedAtAlias));
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the record at ordinals 0-17, opening it when it is sealed. <see langword="null"/> means the row is
    /// sealed and its key was destroyed: the record is erased, and every caller treats it as a tombstone.
    /// </summary>
    /// <param name="reader">A reader positioned on a row that selected <see cref="SelectColumns"/> first.</param>
    /// <param name="encryption">The component's encryption, or <see langword="null"/> in plaintext mode.</param>
    /// <param name="cancellationToken">Cancels the key lookup.</param>
    /// <returns>The record, or <see langword="null"/> when it is crypto-shredded.</returns>
    /// <exception cref="ExperienceStoreException">
    /// The row is sealed and <paramref name="encryption"/> is <see langword="null"/>; the key store has no key
    /// for it (a misconfigured key store, never "erased"); or the sealed value fails authentication.
    /// </exception>
    internal static async ValueTask<ExperienceRecord?> ReadRecordAsync(
        DbDataReader reader,
        ExperienceEncryption? encryption,
        CancellationToken cancellationToken)
    {
        Guid experienceId;
        Scope scope;
        string storedPayload;
        try
        {
            if (reader.GetInt32(16) != SealedText.SealedPayloadVersion)
            {
                return ReadRecord(reader);
            }

            experienceId = reader.GetGuid(0);
            scope = ReadRecordScope(reader);
            storedPayload = reader.GetString(17);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }

        if (encryption is null)
        {
            throw SealedWithoutEncryption();
        }

        using var key = await encryption.ForReadAsync(experienceId, scope, cancellationToken).ConfigureAwait(false);
        return key is null ? null : DecodeSealedRecord(reader, key, storedPayload);
    }

    /// <summary>
    /// <see cref="ReadRecordAsync"/> for many rows already read into memory (<see cref="SnapshotRow"/>), with
    /// every sealed row's key fetched in <em>one</em> key-store call
    /// (<see cref="ExperienceEncryption.ForReadManyAsync"/>). Result <c>i</c> answers <paramref name="rows"/>[<c>i</c>]
    /// exactly as <see cref="ReadRecordAsync"/> would: the record, or <see langword="null"/> when it is sealed and
    /// its key was destroyed; the same exceptions otherwise. Plaintext rows never reach the key store, and every
    /// key is disposed before this returns.
    /// </summary>
    /// <param name="rows">Rows that selected <see cref="SelectColumns"/> first. The reader they came from must already be closed, and the retrieval reads also release its connection first.</param>
    /// <param name="encryption">The component's encryption, or <see langword="null"/> in plaintext mode.</param>
    /// <param name="cancellationToken">Cancels the key lookup.</param>
    /// <returns>One entry per row, in order.</returns>
    internal static async ValueTask<ExperienceRecord?[]> ReadRecordsAsync(
        IReadOnlyList<DbDataReader> rows,
        ExperienceEncryption? encryption,
        CancellationToken cancellationToken)
    {
        var records = new ExperienceRecord?[rows.Count];
        var sealedRows = new List<(int Index, ExperienceKeyReference Reference, string StoredPayload)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            try
            {
                if (row.GetInt32(16) != SealedText.SealedPayloadVersion)
                {
                    records[i] = ReadRecord(row);
                    continue;
                }

                sealedRows.Add((i, new ExperienceKeyReference(row.GetGuid(0), ReadRecordScope(row)), row.GetString(17)));
            }
            catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
            {
                throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
            }
        }

        if (sealedRows.Count == 0)
        {
            return records;
        }

        if (encryption is null)
        {
            throw SealedWithoutEncryption();
        }

        using var keys = await encryption.ForReadManyAsync(sealedRows.Select(row => row.Reference), cancellationToken).ConfigureAwait(false);
        foreach (var (index, reference, storedPayload) in sealedRows)
        {
            records[index] = keys[reference] is { } key ? DecodeSealedRecord(rows[index], key, storedPayload) : null;
        }

        return records;
    }

    internal static ExperienceRecord DecodeSealedRecord(DbDataReader reader, RecordKey key, string storedPayload)
    {
        var (taskId, payloadJson) = SealedText.ReadSealedRecordPlaintext(
            key.Open(SealedText.PayloadColumn, Guid.Empty, SealedText.ReadPayloadEnvelope(storedPayload)));

        try
        {
            return DecodeRecord(reader, taskId, payloadJson);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    private static ExperienceStoreException SealedWithoutEncryption() => new(
        "Stored Experience Record is sealed (payload_version 2), and this component was constructed without an "
        + "ExperienceEncryption. Give every component of an encrypted deployment the same ExperienceEncryption.");

    /// <summary>The owner scope at ordinals 2-7 of <see cref="SelectColumns"/>.</summary>
    internal static Scope ReadRecordScope(DbDataReader reader) => new(
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <summary>Reads a plaintext record at ordinals 0-17. A sealed one goes through <see cref="ReadRecordAsync"/>.</summary>
    internal static ExperienceRecord ReadRecord(DbDataReader reader)
    {
        try
        {
            return DecodeRecord(reader, null, null);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt payload (e.g. InvalidCastException, a null array element).
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    internal static StoredLifecycleEvent ReadEvent(DbDataReader reader, RecordKey? key)
    {
        try
        {
            return DecodeEvent(reader, key);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            // Schema drift or a corrupt row (e.g. InvalidCastException on a retyped column).
            throw new ExperienceStoreException("Stored lifecycle event could not be decoded.", ex);
        }
    }

    private static StoredLifecycleEvent DecodeEvent(DbDataReader reader, RecordKey? key) => new(
        new LifecycleEvent(
            EventId: reader.GetGuid(0),
            ExperienceRecordId: reader.GetGuid(1),
            PriorStatus: reader.IsDBNull(8) ? null : DecodeStatus(reader.GetString(8), "lifecycle event"),
            CurrentStatus: DecodeStatus(reader.GetString(9), "lifecycle event"),
            Reason: SealedText.OpenWith(key, SealedText.EventReasonColumn, reader.GetGuid(0), reader.GetString(10)),
            Producer: reader.GetString(11),
            OccurredAt: reader.GetFieldValue<DateTimeOffset>(12),
            ExpectedRevision: reader.GetInt64(14),
            ReplacementExperienceId: reader.IsDBNull(16) ? null : reader.GetGuid(16),
            Confidence: DecodeConfidence(reader, key)),
        RecordedAt: reader.GetFieldValue<DateTimeOffset>(13),
        AppliedRevision: reader.GetInt64(15),
        Actor: reader.IsDBNull(17) ? null : reader.GetString(17));

    /// <summary>
    /// Rebuilds the confidence payload an event carried, or <see langword="null"/> for the events that
    /// carried none. The evidence ID alone decides which: the table's own CHECK makes the eleven
    /// always-present columns all null or all set together, so a row can never be half an update, and
    /// reading any one of them as the flag is enough.
    /// </summary>
    private static ConfidenceUpdate? DecodeConfidence(DbDataReader reader, RecordKey? key) => reader.IsDBNull(18)
        ? null
        : new ConfidenceUpdate(
            EvidenceId: reader.GetGuid(18),
            Kind: DecodeEnumText<ConfidenceEvidenceKind>(reader.GetString(19), "lifecycle event"),
            Source: DecodeEnumText<ConfidenceEvidenceSource>(reader.GetString(20), "lifecycle event"),
            RunId: reader.GetGuid(21),
            VerificationRoundId: reader.IsDBNull(22) ? null : reader.GetGuid(22),
            ReviewerIdentity: reader.IsDBNull(23) ? null : reader.GetString(23),
            RuleVersion: reader.GetString(24),
            PriorReuseConfidence: reader.GetDouble(26),
            NewReuseConfidence: reader.GetDouble(27),
            PriorSupportingValidations: reader.GetInt32(28),
            NewSupportingValidations: reader.GetInt32(29),
            PriorContradictions: reader.GetInt32(30),
            NewContradictions: reader.GetInt32(31),
            Detail: reader.IsDBNull(25)
                ? null
                : SealedText.OpenWith(key, SealedText.EventConfidenceDetailColumn, reader.GetGuid(0), reader.GetString(25)))
        {
            AssessmentId = reader.IsDBNull(EventAssessmentIdOrdinal) ? null : reader.GetGuid(EventAssessmentIdOrdinal),
            Admission = reader.IsDBNull(EventAdmissionOrdinal)
                ? null
                : DecodeEnumText<ConfidenceEvidenceAdmission>(reader.GetString(EventAdmissionOrdinal), "lifecycle event"),
        };

    /// <summary>Reads a <c>bigint</c> revision, reporting schema drift the way the row decoders do.</summary>
    internal static long ReadRevision(DbDataReader reader, int ordinal)
    {
        try
        {
            return reader.GetInt64(ordinal);
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    internal static ExperienceStatus ReadStoredStatus(DbDataReader reader, int ordinal)
    {
        try
        {
            return DecodeStatus(reader.GetString(ordinal), "Experience Record");
        }
        catch (Exception ex) when (ex is not (ExperienceStoreException or OperationCanceledException or NpgsqlException))
        {
            throw new ExperienceStoreException("Stored Experience Record could not be decoded.", ex);
        }
    }

    internal static Scope ReadEventScope(DbDataReader reader) => new(
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <param name="statusText">The stored status text.</param>
    /// <param name="objectKind">Which stored object the text came from, so a failure names the right row.</param>
    private static ExperienceStatus DecodeStatus(string statusText, string objectKind)
    {
        if (!Enum.TryParse<ExperienceStatus>(statusText, ignoreCase: false, out var status) || !Enum.IsDefined(status)
            || !string.Equals(status.ToString(), statusText, StringComparison.Ordinal))
        {
            throw new ExperienceStoreException($"Stored {objectKind} has an unrecognized status.");
        }

        return status;
    }

    /// <summary>
    /// Reads an enum stored as its own member name, matched case-sensitively and against the defined
    /// members only -- the same strictness <see cref="DecodeStatus"/> applies, for the same reason: a
    /// row whose text is nearly right must fail loudly rather than decode into something else.
    /// </summary>
    /// <typeparam name="T">The enum to decode.</typeparam>
    /// <param name="text">The stored text.</param>
    /// <param name="objectKind">Which stored object the text came from, so a failure names the right row.</param>
    internal static T DecodeEnumText<T>(string text, string objectKind)
        where T : struct, Enum
    {
        if (!Enum.TryParse<T>(text, ignoreCase: false, out var value) || !Enum.IsDefined(value)
            || !string.Equals(value.ToString(), text, StringComparison.Ordinal))
        {
            throw new ExperienceStoreException($"Stored {objectKind} has an unrecognized {typeof(T).Name}.");
        }

        return value;
    }

    /// <param name="reader">The row.</param>
    /// <param name="openedTaskId">For a sealed row, the task ID it opened to; <see langword="null"/> reads the column.</param>
    /// <param name="openedPayloadJson">For a sealed row, the v1 payload it opened to; <see langword="null"/> reads the column.</param>
    private static ExperienceRecord DecodeRecord(DbDataReader reader, string? openedTaskId, string? openedPayloadJson)
    {
        var status = DecodeStatus(reader.GetString(9), "Experience Record");

        var payload = openedPayloadJson is null
            ? ExperiencePayload.Deserialize(reader.GetInt32(16), reader.GetString(17))
            : ExperiencePayload.Deserialize(ExperiencePayload.CurrentVersion, openedPayloadJson);

        return ExperiencePayload.ToRecord(
            payload,
            reader.GetGuid(0),
            reader.GetGuid(1),
            ReadRecordScope(reader),
            openedTaskId ?? reader.GetString(8),
            status,
            reader.GetDouble(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt64(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.GetFieldValue<DateTimeOffset>(15));
    }
}
