using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentExperience.Abstractions;

namespace AgentExperience.Storage.Postgres;

/// <summary>
/// Turns on <b>crypto-shredding</b> for the PostgreSQL adapter: every free-text column that erasure removes
/// is stored as AES-256-GCM ciphertext under a per-record data key from <see cref="KeyStore"/>, and erasing a
/// record destroys that key -- so every copy of the ciphertext, in backups, replicas, WAL and dead tuples,
/// becomes unreadable.
/// </summary>
/// <remarks>
/// <para>
/// Pass the same instance to every component of one deployment: <see cref="PostgresExperienceRecordStore"/>,
/// <see cref="PostgresExperienceCandidateSource"/>, <see cref="PostgresExperienceGrantStore"/>,
/// <see cref="PostgresExperienceReuseFeedbackStore"/>, and the vectors package's embedding index. A component
/// constructed without it runs in plaintext mode and writes plaintext; the DI extensions pick up a registered
/// instance for every component they create.
/// </para>
/// <para>
/// <b>What is sealed</b>: the record payload (task summary, attempts, tool calls, outcome and evidence
/// detail, reflection and lesson, environment, provenance) together with the task ID; lifecycle event
/// reasons and confidence detail; confidence-evidence detail; grant reasons, revocation reasons and grant
/// event reasons; and a reuse-feedback rationale, sealed once per exposed record. <b>What is not</b>: IDs,
/// scope, statuses, scores, counters, timestamps, principal and reviewer identities, and the derived search
/// data PostgreSQL must read in the clear to search -- the full-text vector (stemmed words with positions)
/// and the embedding. The store README states each mode's guarantee exactly.
/// </para>
/// <para>
/// <b>Key custody decides whether any of this is true.</b> The key store must keep its keys outside the
/// database's backup domain; see <see cref="IExperienceKeyStore"/>.
/// </para>
/// </remarks>
public sealed class ExperienceEncryption
{
    /// <summary>Creates the configuration over a key store.</summary>
    /// <param name="keyStore">Custody of the per-record data keys. Must live outside the database's backup domain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keyStore"/> is <see langword="null"/>.</exception>
    public ExperienceEncryption(IExperienceKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        KeyStore = keyStore;
    }

    /// <summary>The key store every sealed value's key comes from.</summary>
    public IExperienceKeyStore KeyStore { get; }

    /// <summary>
    /// <b>Test-only.</b> The encryption a component uses when it was constructed without one. Always
    /// <see langword="null"/> in a host: it is internal, and only this repository's test assemblies set it,
    /// from <c>AGENTEXPERIENCE_TEST_ENCRYPTION</c>, so the unmodified store and vectors suites can run a second
    /// time in encrypted mode.
    /// </summary>
    internal static ExperienceEncryption? TestSuiteDefault { get; set; }

    /// <summary>
    /// <b>Test-only.</b> Forces plaintext mode for one component even while <see cref="TestSuiteDefault"/> is
    /// set -- for the tests whose subject is a plaintext row (the upgrade job's input).
    /// </summary>
    internal static ExperienceEncryption ForcePlaintext { get; } = new(new RefusingKeyStore());

    /// <summary>What a component constructed with <paramref name="configured"/> actually uses.</summary>
    internal static ExperienceEncryption? Resolve(ExperienceEncryption? configured) =>
        ReferenceEquals(configured, ForcePlaintext) ? null : configured ?? TestSuiteDefault;

    /// <summary>
    /// The record's key for a write: created when it has none. <see langword="null"/> when it was destroyed
    /// -- the record is erased (or mid-erasure), and a write must treat it exactly as a tombstone.
    /// </summary>
    internal async ValueTask<RecordKey?> ForWriteAsync(Guid experienceId, Scope scope, CancellationToken cancellationToken)
    {
        var reference = new ExperienceKeyReference(experienceId, scope);
        ExperienceKeyLookup lookup;
        try
        {
            lookup = await KeyStore.CreateKeyAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw KeyStoreFailure("create", ex);
        }

        if (lookup is not { Status: ExperienceKeyStatus.Active })
        {
            lookup.Key?.Dispose();
        }

        return lookup switch
        {
            { Status: ExperienceKeyStatus.Active, Key: { } key } => new RecordKey(reference, key),
            { Status: ExperienceKeyStatus.Destroyed } => null,
            _ => throw new ExperienceStoreException("The key store answered a key creation with neither a key nor a destruction."),
        };
    }

    /// <summary>
    /// The record's key for a read of sealed data: <see langword="null"/> when it was destroyed (the record
    /// is erased). A key the store never held is a configuration failure and throws: a sealed row without a
    /// key is never silently reported as erased.
    /// </summary>
    internal async ValueTask<RecordKey?> ForReadAsync(Guid experienceId, Scope scope, CancellationToken cancellationToken)
    {
        var reference = new ExperienceKeyReference(experienceId, scope);
        ExperienceKeyLookup lookup;
        try
        {
            lookup = await KeyStore.GetKeyAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw KeyStoreFailure("read", ex);
        }

        if (lookup is not { Status: ExperienceKeyStatus.Active })
        {
            lookup.Key?.Dispose();
        }

        return lookup switch
        {
            { Status: ExperienceKeyStatus.Active, Key: { } key } => new RecordKey(reference, key),
            { Status: ExperienceKeyStatus.Destroyed } => null,
            _ => throw new ExperienceStoreException(
                "A sealed Experience Record has no key in the configured key store. The key store is misconfigured "
                + "(the wrong or an empty store); the record is not reported as erased."),
        };
    }

    /// <summary>
    /// The record's key without creating one, as a tri-state: destroyed, a key, or neither (a record written
    /// before the upgrade, whose values are plaintext). For readers that may meet either kind of row.
    /// </summary>
    internal async ValueTask<(bool Destroyed, RecordKey? Key)> LookupAsync(Guid experienceId, Scope scope, CancellationToken cancellationToken)
    {
        var reference = new ExperienceKeyReference(experienceId, scope);
        ExperienceKeyLookup lookup;
        try
        {
            lookup = await KeyStore.GetKeyAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw KeyStoreFailure("read", ex);
        }

        if (lookup is not { Status: ExperienceKeyStatus.Active })
        {
            lookup.Key?.Dispose();
        }

        return lookup switch
        {
            { Status: ExperienceKeyStatus.Active, Key: { } key } => (false, new RecordKey(reference, key)),
            { Status: ExperienceKeyStatus.Destroyed } => (true, null),
            _ => (false, null),
        };
    }

    /// <summary>Destroys the record's key. Throws <see cref="ExperienceStoreException"/> when the key store cannot.</summary>
    internal async ValueTask DestroyAsync(Guid experienceId, Scope scope, CancellationToken cancellationToken)
    {
        try
        {
            await KeyStore.DestroyKeyAsync(new ExperienceKeyReference(experienceId, scope), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw KeyStoreFailure("destroy", ex);
        }
    }

    private static ExperienceStoreException KeyStoreFailure(string operation, Exception inner) =>
        new($"The Experience Record key store failed to {operation} a data key.", inner);

    /// <summary>The key store behind <see cref="ForcePlaintext"/>. Never called: that instance resolves to plaintext.</summary>
    private sealed class RefusingKeyStore : IExperienceKeyStore
    {
        public ValueTask<ExperienceKeyLookup> CreateKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Plaintext mode has no key store.");

        public ValueTask<ExperienceKeyLookup> GetKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Plaintext mode has no key store.");

        public ValueTask DestroyKeyAsync(ExperienceKeyReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Plaintext mode has no key store.");
    }
}

/// <summary>
/// One record's data key, with the reference it belongs to, for sealing and opening that record's values.
/// Disposing it zeroes the key.
/// </summary>
internal sealed class RecordKey : IDisposable
{
    private readonly ExperienceDataKey _key;

    internal RecordKey(ExperienceKeyReference reference, ExperienceDataKey key)
    {
        Reference = reference;
        _key = key;
    }

    internal ExperienceKeyReference Reference { get; }

    /// <summary>Seals <paramref name="plaintext"/> for exactly one column of one row of this record.</summary>
    internal string Seal(string column, Guid rowId, string plaintext) =>
        SealedText.Seal(_key.Span, SealedText.AssociatedData(column, Reference, rowId), plaintext);

    /// <summary>
    /// Opens a stored value of one column of one row. A value that is not sealed (a row written in plaintext
    /// mode, before the upgrade) is returned as stored.
    /// </summary>
    internal string Open(string column, Guid rowId, string stored) =>
        SealedText.IsSealed(stored)
            ? SealedText.Open(_key.Span, SealedText.AssociatedData(column, Reference, rowId), stored)
            : stored;

    /// <summary><see cref="Open"/> for a nullable column.</summary>
    internal string? OpenNullable(string column, Guid rowId, string? stored) =>
        stored is null ? null : Open(column, rowId, stored);

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// The sealed-value format: <c>aexp-sealed:v1:</c> followed by base64 of nonce (12) ‖ ciphertext ‖ tag (16),
/// AES-256-GCM, with a fresh random 96-bit nonce for every value.
/// </summary>
/// <remarks>
/// A data key belongs to one record and seals only that record's handful of values (its payload, its events'
/// reasons and details, its grants' reasons), so random nonces are nowhere near the 2^32-per-key bound
/// under which NIST SP 800-38D allows them.
/// </remarks>
internal static class SealedText
{
    internal const string Prefix = "aexp-sealed:v1:";

    /// <summary>The stored <c>task_id</c> of a sealed record: the real one is inside the sealed payload.</summary>
    internal const string SealedTaskId = "(sealed)";

    /// <summary>What a nullable sealed-per-exposure column's parent row holds instead of the text.</summary>
    internal const string SealedPlaceholder = "(sealed)";

    /// <summary>The <c>payload_version</c> of a sealed record: a v1 payload inside a sealed envelope.</summary>
    internal const int SealedPayloadVersion = 2;

    /// <summary>The one property of a sealed record's stored payload.</summary>
    internal const string PayloadProperty = "sealed";

    // Column names bound into the associated data. A value moved to another column fails its tag.
    internal const string PayloadColumn = "experience_records.payload";
    internal const string EventReasonColumn = "lifecycle_events.reason";
    internal const string EventConfidenceDetailColumn = "lifecycle_events.confidence_detail";
    internal const string EvidenceDetailColumn = "confidence_evidence.detail";
    internal const string GrantReasonColumn = "experience_grants.reason";
    internal const string GrantRevocationReasonColumn = "experience_grants.revocation_reason";
    internal const string GrantEventReasonColumn = "experience_grant_events.reason";
    internal const string ExposureRationaleColumn = "reuse_feedback_exposures.rationale";

    private const string Label = "AgentExperience.NET/sealed-field/v1";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    internal static bool IsSealed(string? stored) =>
        stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Opens a stored value with <paramref name="key"/>: a plaintext value (plaintext mode, or written before
    /// the upgrade) is returned as stored; a sealed one needs the key, and without one this fails loudly rather
    /// than hand back ciphertext as if it were the text.
    /// </summary>
    internal static string OpenWith(RecordKey? key, string column, Guid rowId, string stored)
    {
        if (!IsSealed(stored))
        {
            return stored;
        }

        return key is null
            ? throw new ExperienceStoreException(
                "A stored value is sealed and no key is available to open it: this component has no ExperienceEncryption, "
                + "or the key store holds no key for the record.")
            : key.Open(column, rowId, stored);
    }

    internal static string Seal(ReadOnlySpan<byte> key, byte[] associatedData, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var blob = new byte[NonceSize + plain.Length + TagSize];
            var nonce = blob.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, blob.AsSpan(NonceSize, plain.Length), blob.AsSpan(NonceSize + plain.Length, TagSize), associatedData);
            return Prefix + Convert.ToBase64String(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static string Open(ReadOnlySpan<byte> key, byte[] associatedData, string stored)
    {
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw Tampered();
        }

        if (blob.Length < NonceSize + TagSize)
        {
            throw Tampered();
        }

        var cipherLength = blob.Length - NonceSize - TagSize;
        var plain = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                blob.AsSpan(0, NonceSize),
                blob.AsSpan(NonceSize, cipherLength),
                blob.AsSpan(NonceSize + cipherLength, TagSize),
                plain,
                associatedData);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // AuthenticationTagMismatchException derives from it. Nothing decrypted is kept or returned, and
            // the inner exception is deliberately dropped: it carries nothing the caller can use.
            throw Tampered();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// The associated data for one value: a fixed label and version, the column, the record ID, the row ID
    /// (<see cref="Guid.Empty"/> for the payload), and all six scope fields, each length-prefixed with a null
    /// field distinct from an empty one.
    /// </summary>
    internal static byte[] AssociatedData(string column, ExperienceKeyReference reference, Guid rowId)
    {
        using var stream = new MemoryStream();
        Write(stream, Label);
        Write(stream, column);
        Write(stream, reference.ExperienceId.ToString("D"));
        Write(stream, rowId.ToString("D"));
        Write(stream, reference.Scope.TenantId);
        Write(stream, reference.Scope.ApplicationId);
        Write(stream, reference.Scope.ProjectId);
        Write(stream, reference.Scope.TeamId);
        Write(stream, reference.Scope.AgentId);
        Write(stream, reference.Scope.UserId);
        return stream.ToArray();
    }

    /// <summary>What a sealed record stores in <c>payload</c>: <c>{"sealed": "&lt;sealed value&gt;"}</c>.</summary>
    internal static string PayloadEnvelope(string sealedValue)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(PayloadProperty, sealedValue);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The sealed value inside a stored payload envelope.</summary>
    internal static string ReadPayloadEnvelope(string storedPayload)
    {
        try
        {
            using var document = JsonDocument.Parse(storedPayload);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PayloadProperty, out var sealedValue)
                && sealedValue.ValueKind == JsonValueKind.String
                && sealedValue.GetString() is { } value
                && IsSealed(value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        throw new ExperienceStoreException("Stored Experience Record has a sealed payload_version but no sealed payload.");
    }

    /// <summary>The plaintext that is sealed for a record: its task ID and its v1 payload JSON, together.</summary>
    internal static string SealedRecordPlaintext(string taskId, string payloadJson)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("taskId", taskId);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(payloadJson, skipInputValidation: true);
            writer.WriteEndObject();
        }

        try
        {
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.GetBuffer());
        }
    }

    /// <summary>Splits an opened record plaintext back into its task ID and v1 payload JSON.</summary>
    internal static (string TaskId, string PayloadJson) ReadSealedRecordPlaintext(string plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("taskId", out var taskId) && taskId.ValueKind == JsonValueKind.String
                && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                return (taskId.GetString()!, payload.GetRawText());
            }
        }
        catch (JsonException)
        {
        }

        throw new ExperienceStoreException("A sealed Experience Record opened, but not to the shape this adapter seals.");
    }

    private static ExperienceStoreException Tampered() =>
        new("A sealed value failed authentication: it was altered, or moved from another record, row, column or scope. "
            + "Nothing was decrypted.");

    private static void Write(Stream stream, string? value)
    {
        Span<byte> length = stackalloc byte[4];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, -1);
            stream.Write(length);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
