using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Confidence;

/// <summary>
/// Opt-in signing of what finalization vouches for: a host-held HMAC key ring under which
/// <c>ExperienceFinalizationService</c> signs every record it creates, and against which confidence verification
/// checks the record of every run it relies on.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is signed.</b> A record's finalization claims, and nothing else: its <see cref="ExperienceRecord.ExperienceId"/>,
/// its <see cref="ExperienceRecord.Scope"/> (all six fields), its <see cref="ExperienceRecord.SourceRunId"/>,
/// <see cref="ExperienceRecord.ClosedRoundId"/> and <see cref="ExperienceRecord.Origin"/>, and the exposures in
/// its <see cref="Provenance.ExposedTo"/>. Content, counters and timestamps are not claims about the run and stay
/// unsigned.
/// </para>
/// <para>
/// <b>What verification does with it.</b> With these options registered, a run is known through its finalized
/// record only when that record carries a signature, under a key in the ring, that verifies (compared in constant
/// time), or when the record carries no signature and its ID is in <see cref="TrustUnsignedRecordIds"/>. Anything
/// else is treated as written outside finalization and refused as <c>IndependenceRefusal.HostWrittenRun</c>.
/// Without these options, nothing is signed or checked, exactly as before.
/// </para>
/// <para>
/// <b>The keys are secrets the host holds.</b> Each is at least <see cref="MinimumKeyBytes"/> random bytes from the
/// host's secret store, never in the database, never where an agent can read it, never the assessment token key,
/// and never shared between environments (staging and production each have their own ring). They are copied when
/// these options are constructed and again when a service is constructed; nothing public returns them, and they
/// appear in no record, log, span or message. A record carries only the key's ID.
/// </para>
/// <para>
/// <b>Rotation.</b> Add the new key, then switch <see cref="CurrentKeyId"/> to it: records signed under the older
/// key keep verifying for as long as it stays in the ring. Removing a key makes every record signed under it vouch
/// for nothing, like an unsigned record.
/// </para>
/// </remarks>
public sealed class ExperienceProvenanceSigningOptions
{
    /// <summary>The shortest key accepted: 32 bytes, HMAC-SHA256's output size.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>The longest key ID accepted.</summary>
    public const int MaximumKeyIdLength = 64;

    private readonly ReadOnlyDictionary<string, byte[]> _keys;
    private readonly ReadOnlySet<Guid> _trustUnsignedRecordIds = new(new HashSet<Guid>());

    /// <summary>
    /// Creates signing options over a key ring.
    /// </summary>
    /// <param name="keys">The key ring: key ID to key. Every ID is 1 to <see cref="MaximumKeyIdLength"/> characters from <c>[A-Za-z0-9._-]</c>; every key is at least <see cref="MinimumKeyBytes"/> bytes. Copied.</param>
    /// <param name="currentKeyId">The key finalization signs new records under. Must be in <paramref name="keys"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys"/> or <paramref name="currentKeyId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The ring is empty, a key ID or key is malformed, or <paramref name="currentKeyId"/> is not in the ring.</exception>
    public ExperienceProvenanceSigningOptions(IReadOnlyDictionary<string, byte[]> keys, string currentKeyId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(currentKeyId);

        if (keys.Count == 0)
        {
            throw new ArgumentException("The provenance signing key ring must hold at least one key.", nameof(keys));
        }

        var copy = new Dictionary<string, byte[]>(keys.Count, StringComparer.Ordinal);
        foreach (var (keyId, key) in keys)
        {
            if (!IsValidKeyId(keyId))
            {
                // The rule only: never the ID, which a host may consider sensitive, and never the key.
                throw new ArgumentException(
                    $"Every provenance signing key ID must be 1 to {MaximumKeyIdLength} characters from [A-Za-z0-9._-].", nameof(keys));
            }

            if (key is null || key.Length < MinimumKeyBytes)
            {
                throw new ArgumentException(
                    $"Every provenance signing key must be at least {MinimumKeyBytes} bytes.", nameof(keys));
            }

            copy[keyId] = key.ToArray();
        }

        if (!copy.ContainsKey(currentKeyId))
        {
            throw new ArgumentException("The current provenance signing key ID must name a key in the ring.", nameof(currentKeyId));
        }

        _keys = new ReadOnlyDictionary<string, byte[]>(copy);
        KeyIds = new ReadOnlyCollection<string>([.. copy.Keys.Order(StringComparer.Ordinal)]);
        CurrentKeyId = currentKeyId;
    }

    /// <summary>The IDs of the keys in the ring, in ordinal order. The keys themselves are never exposed.</summary>
    public IReadOnlyList<string> KeyIds { get; }

    /// <summary>The key finalization signs new records under.</summary>
    public string CurrentKeyId { get; }

    /// <summary>
    /// The explicit cutover for existing data: the IDs of records finalized before signing was switched on, which
    /// verification accepts with <em>no</em> signature. Empty (the default) accepts no unsigned record. Copied when
    /// set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Capture it once, when switching signing on, with
    /// <see cref="ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync"/>, and keep the result in
    /// your configuration. A record created afterwards can never join the set: its ID would have to be one that
    /// already exists, and a store refuses to create over an existing ID or a tombstone. So a host that forges an
    /// unsigned record later is not believed, whatever the record says about itself.
    /// </para>
    /// <para>
    /// It applies only to a record that carries no signature. A signature that is present -- under an unknown key,
    /// or one that does not verify -- is refused whether or not its record's ID is listed.
    /// </para>
    /// </remarks>
    public IReadOnlySet<Guid> TrustUnsignedRecordIds
    {
        get => _trustUnsignedRecordIds;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _trustUnsignedRecordIds = new ReadOnlySet<Guid>(new HashSet<Guid>(value));
        }
    }

    /// <summary>The key ring, for the signer to copy. Never public: nothing outside Core can read a key back.</summary>
    internal IReadOnlyDictionary<string, byte[]> Keys => _keys;

    /// <summary>Names the current key and counts the rest; never prints key material.</summary>
    /// <returns>A description without key bytes.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{nameof(ExperienceProvenanceSigningOptions)} {{ CurrentKeyId = {CurrentKeyId}, Keys = <{_keys.Count} redacted>, TrustUnsignedRecordIds = {_trustUnsignedRecordIds.Count} }}");

    private static bool IsValidKeyId(string? keyId)
    {
        if (string.IsNullOrEmpty(keyId) || keyId.Length > MaximumKeyIdLength)
        {
            return false;
        }

        foreach (var c in keyId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// What <see cref="ExperienceProvenanceSigningCutover.ListUnsignedFinalizedRecordIdsAsync"/> found.
/// </summary>
/// <param name="RecordIds">The IDs of the finalized records with no provenance signature: the set to configure as <see cref="ExperienceProvenanceSigningOptions.TrustUnsignedRecordIds"/>.</param>
/// <param name="IncompleteScopes">
/// The scopes where a status held more records than one query returns (<see cref="ExperienceRecordQuery.MaxLimit"/>),
/// so the list may be missing some of them. Empty when the inventory is complete. A missing record simply stops
/// vouching once signing is on; it is never wrongly trusted.
/// </param>
public sealed record ExperienceUnsignedRecordInventory(IReadOnlySet<Guid> RecordIds, IReadOnlyList<Scope> IncompleteScopes);

/// <summary>
/// Helps a host switch provenance signing on over existing data.
/// </summary>
public static class ExperienceProvenanceSigningCutover
{
    /// <summary>
    /// Lists the records marked <see cref="ExperienceRecordOrigin.Finalized"/> that carry no provenance signature in
    /// each of <paramref name="scopes"/>, read through <see cref="IExperienceRecordStore.QueryAsync"/>.
    /// </summary>
    /// <remarks>
    /// Run it once, when enabling signing, and before the first record is finalized with signing on; store the IDs
    /// in your configuration as <see cref="ExperienceProvenanceSigningOptions.TrustUnsignedRecordIds"/>. Everything
    /// it lists is trusted as finalized from then on, so run it only over data you trust, and review the list: a
    /// record a host wrote by hand marked <c>Finalized</c> before signing is on it too. It reads every status, up to
    /// <see cref="ExperienceRecordQuery.MaxLimit"/> records per status per scope, and reports a scope it could not
    /// read to the end in <see cref="ExperienceUnsignedRecordInventory.IncompleteScopes"/>.
    /// </remarks>
    /// <param name="store">The record store.</param>
    /// <param name="authorization">The host-established authorization the reads run under.</param>
    /// <param name="scopes">The exact scopes to list.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The IDs found, and any scope the inventory could not complete.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The store refused a query (it was not <see cref="ExperienceStoreOutcome.Found"/>).</exception>
    public static async Task<ExperienceUnsignedRecordInventory> ListUnsignedFinalizedRecordIdsAsync(
        IExperienceRecordStore store,
        AuthorizationContext authorization,
        IEnumerable<Scope> scopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scopes);

        var ids = new HashSet<Guid>();
        var incomplete = new List<Scope>();
        foreach (var scope in scopes)
        {
            ArgumentNullException.ThrowIfNull(scope, nameof(scopes));
            var complete = true;
            foreach (var status in Enum.GetValues<ExperienceStatus>())
            {
                var result = await store
                    .QueryAsync(authorization, new ExperienceRecordQuery(scope, [status], ExperienceRecordQuery.MaxLimit), cancellationToken)
                    .ConfigureAwait(false);
                if (result.Outcome != ExperienceStoreOutcome.Found)
                {
                    throw new InvalidOperationException(
                        $"The store answered a query for unsigned finalized records with '{result.Outcome}'; the inventory is incomplete.");
                }

                complete &= result.Records.Count < ExperienceRecordQuery.MaxLimit;
                foreach (var record in result.Records)
                {
                    if (record is { Origin: ExperienceRecordOrigin.Finalized, ProvenanceSignature: null })
                    {
                        ids.Add(record.ExperienceId);
                    }
                }
            }

            if (!complete)
            {
                incomplete.Add(scope);
            }
        }

        return new(new ReadOnlySet<Guid>(ids), incomplete.AsReadOnly());
    }
}

/// <summary>How a record's provenance signature checked out.</summary>
internal enum ProvenanceSignatureCheck
{
    /// <summary>The signature verifies under a key in the ring.</summary>
    Valid,

    /// <summary>No signature, but the record's ID is in the configured cutover set.</summary>
    TrustedUnsigned,

    /// <summary>No signature.</summary>
    Missing,

    /// <summary>The signature names a key that is not in the ring.</summary>
    UnknownKey,

    /// <summary>The signature is present, under a known key, and does not verify, names another algorithm, or covers claims that cannot be encoded.</summary>
    Invalid,
}

/// <summary>
/// Signs and verifies records' finalization claims, over a private copy of the key ring.
/// </summary>
/// <remarks>
/// The canonical encoding (<see cref="Encode"/>): the version tag <c>aexp-prov:v1</c>, then the record ID, the six
/// scope fields in declaration order, the source run, the closed round, the origin, and the exposures. A string is a
/// presence byte (0 for null, 1 for a value), then a 4-byte big-endian length and its strict UTF-8 bytes; a GUID is
/// its 16 bytes in big-endian (RFC 4122) order; the closed round is a presence byte then a GUID; the origin is a
/// 4-byte big-endian integer; the exposures are a 4-byte big-endian count and then each exposure's record ID and
/// revision (8 bytes, big-endian), sorted by the record ID's big-endian bytes, compared as unsigned bytes, then by
/// revision ascending. Claims that have no such encoding -- a null scope or provenance, a null exposure, a lone
/// surrogate -- are refused, never normalized.
/// </remarks>
internal sealed class ProvenanceSigner
{
    internal const string VersionTag = "aexp-prov:v1";

    /// <summary>The one sentence a caller is told for any signature that does not vouch: no oracle for which check failed.</summary>
    internal const string RefusalText = "its provenance signature does not vouch for it";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _currentKeyId;
    private readonly HashSet<Guid> _trustUnsigned;

    private ProvenanceSigner(ExperienceProvenanceSigningOptions options)
    {
        _keys = new Dictionary<string, byte[]>(options.Keys.Count, StringComparer.Ordinal);
        foreach (var (keyId, key) in options.Keys)
        {
            _keys[keyId] = key.ToArray();
        }

        _currentKeyId = options.CurrentKeyId;
        _trustUnsigned = [.. options.TrustUnsignedRecordIds];
    }

    /// <summary>The key new records are signed under.</summary>
    internal string CurrentKeyId => _currentKeyId;

    /// <summary>A signer over <paramref name="options"/>, or <see langword="null"/> when signing is not configured.</summary>
    internal static ProvenanceSigner? Create(ExperienceProvenanceSigningOptions? options) =>
        options is null ? null : new ProvenanceSigner(options);

    /// <summary>Whether any key in this ring is byte-for-byte <paramref name="other"/>.</summary>
    internal bool HoldsKey(ReadOnlySpan<byte> other)
    {
        var found = false;
        foreach (var key in _keys.Values)
        {
            found |= key.Length == other.Length && CryptographicOperations.FixedTimeEquals(key, other);
        }

        return found;
    }

    /// <summary>Whether this ring holds <paramref name="other"/>'s current key, under the same ID.</summary>
    internal bool HoldsCurrentKeyOf(ProvenanceSigner other) =>
        _keys.TryGetValue(other._currentKeyId, out var key)
        && CryptographicOperations.FixedTimeEquals(key, other._keys[other._currentKeyId]);

    /// <summary>Signs <paramref name="record"/>'s finalization claims under the current key.</summary>
    /// <exception cref="ArgumentException">A claim has no canonical encoding.</exception>
    internal ExperienceProvenanceSignature Sign(ExperienceRecord record) =>
        new(_currentKeyId, ExperienceProvenanceSignature.HmacSha256, HMACSHA256.HashData(_keys[_currentKeyId], Encode(record)));

    /// <summary>Checks <paramref name="record"/>'s signature against its claims as they now stand.</summary>
    internal ProvenanceSignatureCheck Verify(ExperienceRecord record)
    {
        if (record.ProvenanceSignature is not { } signature)
        {
            return _trustUnsigned.Contains(record.ExperienceId)
                ? ProvenanceSignatureCheck.TrustedUnsigned
                : ProvenanceSignatureCheck.Missing;
        }

        if (signature.KeyId is null || !_keys.TryGetValue(signature.KeyId, out var key))
        {
            return ProvenanceSignatureCheck.UnknownKey;
        }

        if (!string.Equals(signature.Algorithm, ExperienceProvenanceSignature.HmacSha256, StringComparison.Ordinal))
        {
            return ProvenanceSignatureCheck.Invalid;
        }

        byte[] claims;
        try
        {
            claims = Encode(record);
        }
        catch (ArgumentException)
        {
            // Nothing the signer would sign: whatever this signature covers, it is not these claims.
            return ProvenanceSignatureCheck.Invalid;
        }

        var expected = HMACSHA256.HashData(key, claims);
        return CryptographicOperations.FixedTimeEquals(expected, signature.Value.Span)
            ? ProvenanceSignatureCheck.Valid
            : ProvenanceSignatureCheck.Invalid;
    }

    /// <summary>The canonical encoding of <paramref name="record"/>'s finalization claims.</summary>
    /// <exception cref="ArgumentException">The scope, the provenance or an exposure is null, or a string is not well-formed UTF-16.</exception>
    internal static byte[] Encode(ExperienceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var scope = record.Scope ?? throw new ArgumentException("A record with no scope has no provenance claims to sign.", nameof(record));
        var exposedTo = record.Provenance?.ExposedTo
            ?? throw new ArgumentException("A record with no provenance exposures has no provenance claims to sign.", nameof(record));

        using var buffer = new MemoryStream();
        WriteString(buffer, VersionTag);
        WriteGuid(buffer, record.ExperienceId);
        WriteString(buffer, scope.TenantId);
        WriteString(buffer, scope.ApplicationId);
        WriteString(buffer, scope.ProjectId);
        WriteString(buffer, scope.TeamId);
        WriteString(buffer, scope.AgentId);
        WriteString(buffer, scope.UserId);
        WriteGuid(buffer, record.SourceRunId);

        if (record.ClosedRoundId is { } round)
        {
            buffer.WriteByte(1);
            WriteGuid(buffer, round);
        }
        else
        {
            buffer.WriteByte(0);
        }

        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(number, (int)record.Origin);
        buffer.Write(number[..4]);

        var exposures = new List<(byte[] Id, long Revision)>(exposedTo.Count);
        foreach (var exposure in exposedTo)
        {
            if (exposure is null)
            {
                throw new ArgumentException("A null exposure has no canonical encoding.", nameof(record));
            }

            exposures.Add((exposure.ExperienceId.ToByteArray(bigEndian: true), exposure.Revision));
        }

        exposures.Sort(static (left, right) =>
        {
            var byId = left.Id.AsSpan().SequenceCompareTo(right.Id);
            return byId != 0 ? byId : left.Revision.CompareTo(right.Revision);
        });

        BinaryPrimitives.WriteInt32BigEndian(number, exposures.Count);
        buffer.Write(number[..4]);
        foreach (var (id, revision) in exposures)
        {
            buffer.Write(id);
            BinaryPrimitives.WriteInt64BigEndian(number, revision);
            buffer.Write(number);
        }

        return buffer.ToArray();
    }

    private static void WriteGuid(MemoryStream buffer, Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        buffer.Write(bytes);
    }

    private static void WriteString(MemoryStream buffer, string? value)
    {
        if (value is null)
        {
            buffer.WriteByte(0);
            return;
        }

        buffer.WriteByte(1);

        // Strict: a lone surrogate throws (EncoderFallbackException, an ArgumentException) rather than becoming U+FFFD,
        // so two different strings can never encode to the same claim.
        var bytes = StrictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }
}
