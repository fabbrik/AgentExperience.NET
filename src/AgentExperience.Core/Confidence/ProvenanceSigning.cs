using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Confidence;

/// <summary>
/// Opt-in signing of what finalization vouches for: a host-held HMAC key ring under which
/// <c>ExperienceFinalizationService</c> signs every record it creates, and against which confidence verification
/// checks the record of every run it relies on.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is signed.</b> Claims version 2 (story 17.2, <see cref="ExperienceProvenanceSignature.HmacSha256ClaimsV2"/>):
/// a record's finalization claims -- its <see cref="ExperienceRecord.ExperienceId"/>, its
/// <see cref="ExperienceRecord.Scope"/> (all six fields), its <see cref="ExperienceRecord.SourceRunId"/>,
/// <see cref="ExperienceRecord.ClosedRoundId"/> and <see cref="ExperienceRecord.Origin"/>, and the exposures in its
/// <see cref="Provenance.ExposedTo"/> -- then a SHA-256 digest of its content, everything injection renders from it:
/// its <see cref="ExperienceRecord.TaskId"/> and <see cref="ExperienceRecord.TaskSummary"/>, its outcome's status and
/// evidence count, its environment (all fields and attributes), its attempts (each tool call's name and argument
/// values, through their JSON form, numbers canonicalized by exact decimal value), and its reflection's free text,
/// evidence count, <see cref="Reflection.Authorship"/> and <see cref="Reflection.Producer"/>. Lifecycle status,
/// counters and timestamps change through the lifecycle and stay unsigned. A version 1 signature (<see cref="ExperienceProvenanceSignature.HmacSha256"/>),
/// made before story 17.2, still verifies for the finalization claims it covers.
/// </para>
/// <para>
/// <b>What it does to authorship.</b> With these options registered, a record's content is <em>confirmed</em> only
/// when it carries a version 2 signature under a key in the ring that verifies, carries no signature and its ID is in
/// <see cref="TrustUnsignedRecordIds"/>, or is confirmed by <see cref="ConfirmV1Content"/> or
/// <see cref="ConfirmContentRecordIds"/>. A record whose content is not confirmed -- a version 1 signature, none, an
/// unknown key, one that does not verify, or content that cannot be encoded -- counts as model-authored: <c>ExperienceRetrievalService</c> excludes
/// it under <c>ExcludeModelAuthored</c>, and injection fences and labels it as model-authored.
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
    private readonly int _signClaimsVersion = 2;
    private readonly ReadOnlySet<Guid> _confirmContentRecordIds = new(new HashSet<Guid>());

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
    /// <para>
    /// A listed record's content (everything a version 2 signature would cover: its task ID and summary, outcome status
    /// and evidence count, environment, attempts with their tool names and argument values, and its reflection's free
    /// text, evidence count, authorship and producer) counts as confirmed too (story 17.2), so its authorship is what it
    /// declares. Being unsigned, that content stays editable by a party that can write the store.
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

    /// <summary>
    /// The claims version finalization signs new records with: 2 (the default) signs the finalization claims and a
    /// digest of the record's content; 1 signs the finalization claims only, as releases before story 17.2 did.
    /// Verification accepts both whatever this says.
    /// </summary>
    /// <remarks>
    /// Set it to 1 only for the duration of a rolling deploy in which nodes on an earlier build still verify: they
    /// refuse a version 2 signature as one they cannot check, so a record signed version 2 would vouch for nothing on
    /// them. Records signed version 1 have content nothing confirms, so with signing on they are fenced as
    /// model-authored at injection (unless <see cref="ConfirmV1Content"/> is set); switch back to 2 once every node
    /// runs this build.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Set to anything but 1 or 2.</exception>
    public int SignClaimsVersion
    {
        get => _signClaimsVersion;
        init
        {
            if (value is not (1 or 2))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The provenance claims version must be 1 or 2.");
            }

            _signClaimsVersion = value;
        }
    }

    /// <summary>
    /// A transition setting: when <see langword="true"/>, a version 1 signature that verifies also confirms its
    /// record's content, so records signed before story 17.2 are judged by the authorship they declare instead of
    /// being fenced as model-authored. <see langword="false"/> (the default) confirms content only through a
    /// version 2 signature or <see cref="TrustUnsignedRecordIds"/>.
    /// </summary>
    /// <remarks>
    /// Setting it accepts, for those records, the exposure version 1 left open: a party that could write the store
    /// before or since could have changed their text or flipped their authorship, and nothing would notice. Use it
    /// while existing lessons are reviewed or replaced, then turn it off.
    /// </remarks>
    public bool ConfirmV1Content { get; init; }

    /// <summary>
    /// The per-record alternative to <see cref="ConfirmV1Content"/>: the IDs of records whose content a host has
    /// reviewed and accepts as it stands, which count as confirmed whatever their signature version -- a version 1
    /// signature (made before story 17.2, or during a <see cref="SignClaimsVersion"/> = 1 rollout) or none. Empty (the
    /// default) lists none. Copied when set.
    /// </summary>
    /// <remarks>
    /// A listed record whose signature is present but does not verify (a changed claim, an unknown key, a version 2
    /// signature over different content) is still unconfirmed: the list vouches for reviewed content, not for an edit
    /// made since. Its content can still be changed unnoticed while it carries a version 1 signature or none, so list
    /// only what was reviewed, and prefer replacing such lessons with records finalized under version 2.
    /// </remarks>
    public IReadOnlySet<Guid> ConfirmContentRecordIds
    {
        get => _confirmContentRecordIds;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _confirmContentRecordIds = new ReadOnlySet<Guid>(new HashSet<Guid>(value));
        }
    }

    /// <summary>The key ring, for the signer to copy. Never public: nothing outside Core can read a key back.</summary>
    internal IReadOnlyDictionary<string, byte[]> Keys => _keys;

    /// <summary>Names the current key and counts the rest; never prints key material.</summary>
    /// <returns>A description without key bytes.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{nameof(ExperienceProvenanceSigningOptions)} {{ CurrentKeyId = {CurrentKeyId}, Keys = <{_keys.Count} redacted>, TrustUnsignedRecordIds = {_trustUnsignedRecordIds.Count}, SignClaimsVersion = {_signClaimsVersion}, ConfirmV1Content = {ConfirmV1Content}, ConfirmContentRecordIds = {_confirmContentRecordIds.Count} }}");

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
/// <para>
/// The canonical encoding of claims version 1 (<see cref="Encode"/>): the version tag <c>aexp-prov:v1</c>, then the
/// record ID, the six scope fields in declaration order, the source run, the closed round, the origin, and the
/// exposures. A string is a presence byte (0 for null, 1 for a value), then a 4-byte big-endian length and its strict
/// UTF-8 bytes; a GUID is its 16 bytes in big-endian (RFC 4122) order; the closed round is a presence byte then a
/// GUID; the origin is a 4-byte big-endian integer; the exposures are a 4-byte big-endian count and then each
/// exposure's record ID and revision (8 bytes, big-endian), sorted by the record ID's big-endian bytes, compared as
/// unsigned bytes, then by revision ascending. Claims that have no such encoding -- a null scope or provenance, a
/// null exposure, a lone surrogate -- are refused, never normalized.
/// </para>
/// <para>
/// Claims version 2 (<see cref="EncodeV2"/>) is the same encoding under the tag <c>aexp-prov:v2</c>, followed by the
/// 32-byte SHA-256 digest of the record's content encoding (<see cref="EncodeContent"/>): everything the injection
/// writer can render from the record, in this pinned order. <see cref="ExperienceRecord.TaskId"/>;
/// <see cref="ExperienceRecord.TaskSummary"/>; the outcome (presence byte, then its
/// <see cref="Outcome.Status"/> and its evidence count, each a 4-byte big-endian integer, -1 for a null list); the
/// environment (presence byte, then <see cref="EnvironmentFingerprint.HostName"/>,
/// <see cref="EnvironmentFingerprint.RuntimeVersion"/>, <see cref="EnvironmentFingerprint.OperatingSystem"/>,
/// <see cref="EnvironmentFingerprint.ApplicationVersion"/>, and the metadata as a presence byte, a count and each key
/// and value in ordinal key order); the attempts (presence byte, count, and per attempt a presence byte, its sequence
/// number, a byte saying whether it has an error, and its tool calls as a presence byte, a count and per call a
/// presence byte, its sequence number, its tool name and its arguments: a presence byte, a count and each key in
/// ordinal order with its canonical JSON value); then a presence byte for the reflection (0 for none, and nothing
/// follows) and, when present, its <see cref="Reflection.Lesson"/>, <see cref="Reflection.SuccessfulApproaches"/>,
/// <see cref="Reflection.FailedApproaches"/>, <see cref="Reflection.ReuseGuidance"/>,
/// <see cref="Reflection.Preconditions"/>, <see cref="Reflection.Warnings"/>, its evidence ID count,
/// <see cref="Reflection.Authorship"/> (a 4-byte big-endian integer) and <see cref="Reflection.Producer"/>. Strings
/// encode as above; a list is a presence byte, then a 4-byte big-endian count, then each element as a string, in
/// stored order. An argument value is encoded through its JSON form (see <c>WriteValue</c>), so a value reads back to
/// the same bytes from either store. A test pins the bytes against a golden vector.
/// </para>
/// </remarks>
internal sealed class ProvenanceSigner
{
    internal const string VersionTag = "aexp-prov:v1";

    internal const string VersionTagV2 = "aexp-prov:v2";

    /// <summary>The one sentence a caller is told for any signature that does not vouch: no oracle for which check failed.</summary>
    internal const string RefusalText = "its provenance signature does not vouch for it";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _currentKeyId;
    private readonly HashSet<Guid> _trustUnsigned;
    private readonly int _signClaimsVersion;
    private readonly bool _confirmV1Content;
    private readonly HashSet<Guid> _confirmContent;

    private ProvenanceSigner(ExperienceProvenanceSigningOptions options)
    {
        _signClaimsVersion = options.SignClaimsVersion;
        _confirmV1Content = options.ConfirmV1Content;
        _confirmContent = [.. options.ConfirmContentRecordIds];
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

    /// <summary>
    /// Signs <paramref name="record"/> under the current key, with the claims version the options name
    /// (<see cref="ExperienceProvenanceSigningOptions.SignClaimsVersion"/>): version 2, the finalization claims and the
    /// content digest, by default; version 1, the finalization claims only, during a rolling deploy.
    /// </summary>
    /// <exception cref="ArgumentException">A claim or a content field has no canonical encoding.</exception>
    internal ExperienceProvenanceSignature Sign(ExperienceRecord record) =>
        _signClaimsVersion == 1
            ? new(_currentKeyId, ExperienceProvenanceSignature.HmacSha256, HMACSHA256.HashData(_keys[_currentKeyId], Encode(record)))
            : new(_currentKeyId, ExperienceProvenanceSignature.HmacSha256ClaimsV2, HMACSHA256.HashData(_keys[_currentKeyId], EncodeV2(record)));

    /// <summary>
    /// Whether <paramref name="record"/>'s content -- everything the injection writer renders from it (see
    /// <see cref="EncodeContent"/>) -- is confirmed: it carries a claims version 2 signature, under a key in the ring,
    /// that verifies; or a version 1 signature that verifies while
    /// <see cref="ExperienceProvenanceSigningOptions.ConfirmV1Content"/> is set or its ID is in
    /// <see cref="ExperienceProvenanceSigningOptions.ConfirmContentRecordIds"/>; or no signature, and its ID is in the
    /// cutover set or that list.
    /// </summary>
    internal bool ConfirmsContent(ExperienceRecord record)
    {
        var listed = _confirmContent.Contains(record.ExperienceId);
        if (record.ProvenanceSignature is not { } signature)
        {
            return listed || _trustUnsigned.Contains(record.ExperienceId);
        }

        var v2 = string.Equals(signature.Algorithm, ExperienceProvenanceSignature.HmacSha256ClaimsV2, StringComparison.Ordinal);
        var v1 = string.Equals(signature.Algorithm, ExperienceProvenanceSignature.HmacSha256, StringComparison.Ordinal);
        return (v2 || (v1 && (_confirmV1Content || listed))) && Verify(record) == ProvenanceSignatureCheck.Valid;
    }

    /// <summary>
    /// Checks <paramref name="record"/>'s signature against its claims as they now stand: the version the signature's
    /// algorithm names (version 1, the finalization claims; version 2, those claims and the content digest).
    /// </summary>
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

        Func<ExperienceRecord, byte[]> encode;
        if (string.Equals(signature.Algorithm, ExperienceProvenanceSignature.HmacSha256ClaimsV2, StringComparison.Ordinal))
        {
            encode = EncodeV2;
        }
        else if (string.Equals(signature.Algorithm, ExperienceProvenanceSignature.HmacSha256, StringComparison.Ordinal))
        {
            encode = Encode;
        }
        else
        {
            return ProvenanceSignatureCheck.Invalid;
        }

        byte[] claims;
        try
        {
            claims = encode(record);
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

    /// <summary>The canonical encoding of <paramref name="record"/>'s finalization claims (claims version 1).</summary>
    /// <exception cref="ArgumentException">The scope, the provenance or an exposure is null, or a string is not well-formed UTF-16.</exception>
    internal static byte[] Encode(ExperienceRecord record) => Guarded(() => EncodeClaims(record));

    /// <summary>
    /// Runs one encoding and turns any failure into an <see cref="ArgumentException"/>: whatever a stored record holds
    /// (an argument value whose getter throws, a disposed JSON document, a number no type can hold), encoding it either
    /// succeeds or says it has no canonical encoding. So verification answers <see cref="ProvenanceSignatureCheck.Invalid"/>
    /// and its content is unconfirmed (fail closed), and signing fails finalization, never anything else.
    /// </summary>
    private static byte[] Guarded(Func<byte[]> encode)
    {
        try
        {
            return encode();
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArgumentException("The record has no canonical encoding: reading one of its values failed.", ex);
        }
    }

    private static byte[] EncodeClaims(ExperienceRecord record)
    {
        using var buffer = new MemoryStream();
        WriteClaims(buffer, record, VersionTag);
        return buffer.ToArray();
    }

    /// <summary>
    /// The canonical encoding of <paramref name="record"/>'s claims version 2: its finalization claims under the tag
    /// <c>aexp-prov:v2</c>, then the SHA-256 digest of <see cref="EncodeContent"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A claim or a content field has no canonical encoding.</exception>
    internal static byte[] EncodeV2(ExperienceRecord record) => Guarded(() =>
    {
        using var buffer = new MemoryStream();
        WriteClaims(buffer, record, VersionTagV2);
        buffer.Write(SHA256.HashData(EncodeContentCore(record)));
        return buffer.ToArray();
    });

    /// <summary>
    /// The canonical encoding of <paramref name="record"/>'s content -- everything the injection writer can render
    /// from it -- in the order the type's remarks pin.
    /// </summary>
    /// <exception cref="ArgumentException">A string is not well-formed UTF-16, or an argument value has no JSON form.</exception>
    internal static byte[] EncodeContent(ExperienceRecord record) => Guarded(() => EncodeContentCore(record));

    private static byte[] EncodeContentCore(ExperienceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var buffer = new MemoryStream();
        WriteString(buffer, record.TaskId);
        WriteString(buffer, record.TaskSummary);

        // The outcome's verification status and evidence count, as the Verification: and Evidence: lines show them.
        if (record.Outcome is { } outcome)
        {
            buffer.WriteByte(1);
            WriteInt32(buffer, (int)outcome.Status);
            WriteInt32(buffer, outcome.Evidence?.Count ?? -1);
        }
        else
        {
            buffer.WriteByte(0);
        }

        // The environment, as the Environment: line shows it: four fields, then the attributes sorted by key.
        if (record.Environment is { } environment)
        {
            buffer.WriteByte(1);
            WriteString(buffer, environment.HostName);
            WriteString(buffer, environment.RuntimeVersion);
            WriteString(buffer, environment.OperatingSystem);
            WriteString(buffer, environment.ApplicationVersion);
            if (environment.Metadata is { } metadata)
            {
                buffer.WriteByte(1);
                WriteInt32(buffer, metadata.Count);
                foreach (var (key, value) in metadata.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    WriteString(buffer, key);
                    WriteString(buffer, value);
                }
            }
            else
            {
                buffer.WriteByte(0);
            }
        }
        else
        {
            buffer.WriteByte(0);
        }

        // The attempts, as the Tried: and Worked: lines derive from them: each attempt's sequence number and whether it
        // failed, and each tool call's sequence number, name and arguments (keys sorted, values canonical).
        if (record.Attempts is { } attempts)
        {
            buffer.WriteByte(1);
            WriteInt32(buffer, attempts.Count);
            foreach (var attempt in attempts)
            {
                if (attempt is null)
                {
                    buffer.WriteByte(0);
                    continue;
                }

                buffer.WriteByte(1);
                WriteInt32(buffer, attempt.SequenceNumber);
                buffer.WriteByte(attempt.Error is null ? (byte)0 : (byte)1);
                if (attempt.ToolCalls is not { } calls)
                {
                    buffer.WriteByte(0);
                    continue;
                }

                buffer.WriteByte(1);
                WriteInt32(buffer, calls.Count);
                foreach (var call in calls)
                {
                    if (call is null)
                    {
                        buffer.WriteByte(0);
                        continue;
                    }

                    buffer.WriteByte(1);
                    WriteInt32(buffer, call.SequenceNumber);
                    WriteString(buffer, call.ToolName);
                    WriteArguments(buffer, call.Arguments);
                }
            }
        }
        else
        {
            buffer.WriteByte(0);
        }

        if (record.Reflection is not { } reflection)
        {
            buffer.WriteByte(0);
            return buffer.ToArray();
        }

        buffer.WriteByte(1);
        WriteString(buffer, reflection.Lesson);
        WriteList(buffer, reflection.SuccessfulApproaches);
        WriteList(buffer, reflection.FailedApproaches);
        WriteString(buffer, reflection.ReuseGuidance);
        WriteList(buffer, reflection.Preconditions);
        WriteList(buffer, reflection.Warnings);
        WriteInt32(buffer, reflection.EvidenceIds?.Count ?? -1);
        WriteInt32(buffer, (int)reflection.Authorship);
        WriteString(buffer, reflection.Producer);
        return buffer.ToArray();
    }

    private static void WriteInt32(MemoryStream buffer, int value)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, value);
        buffer.Write(number);
    }

    /// <summary>A tool call's arguments: a presence byte, a count, then each key (ordinal order) and its canonical value.</summary>
    private static void WriteArguments(MemoryStream buffer, IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            buffer.WriteByte(0);
            return;
        }

        buffer.WriteByte(1);
        var entries = arguments.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();
        WriteInt32(buffer, entries.Count);
        foreach (var (key, value) in entries)
        {
            WriteString(buffer, key);
            WriteValue(buffer, ToJson(value), depth: 0);
        }
    }

    /// <summary>
    /// An argument value as JSON: what a store persists, so a value reads back to the same canonical encoding from
    /// either store, whatever CLR type captured it.
    /// </summary>
    private static JsonElement ToJson(object? value)
    {
        if (value is JsonElement element)
        {
            return element;
        }

        try
        {
            return JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), StoreJson);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ArgumentException("A tool call argument value has no JSON form, so it has no canonical encoding.", nameof(value), ex);
        }
    }

    /// <summary>
    /// Serializer options equivalent to how the PostgreSQL store writes a payload: camel-case property names and enums
    /// as their names, so an enum or object argument encodes as the JSON the store keeps, and reads back the same.
    /// </summary>
    private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>
    /// A JSON value, canonically: a kind byte (0 null, 1 string, 2 true, 3 false, 4 number, 5 array, 6 object), then
    /// a string's text, a number's text (an integer that fits a 64-bit signed value as that integer, otherwise the
    /// round-trippable double -- how the PostgreSQL store normalizes a number), an array's count and elements in order,
    /// or an object's count and its members in ordinal key order.
    /// </summary>
    private static void WriteValue(MemoryStream buffer, JsonElement value, int depth)
    {
        if (depth > MaxValueDepth)
        {
            throw new ArgumentException("A tool call argument value is nested too deeply to have a canonical encoding.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                buffer.WriteByte(0);
                break;
            case JsonValueKind.String:
                buffer.WriteByte(1);
                WriteString(buffer, value.GetString());
                break;
            case JsonValueKind.True:
                buffer.WriteByte(2);
                break;
            case JsonValueKind.False:
                buffer.WriteByte(3);
                break;
            case JsonValueKind.Number:
                buffer.WriteByte(4);
                WriteString(buffer, ExactDecimal(value.GetRawText()));
                break;
            case JsonValueKind.Array:
                buffer.WriteByte(5);
                WriteInt32(buffer, value.GetArrayLength());
                foreach (var item in value.EnumerateArray())
                {
                    WriteValue(buffer, item, depth + 1);
                }

                break;
            default:
                buffer.WriteByte(6);

                // A repeated member name keeps its last value, as PostgreSQL's jsonb does.
                var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var member in value.EnumerateObject())
                {
                    members[member.Name] = member.Value;
                }

                WriteInt32(buffer, members.Count);
                foreach (var (name, member) in members.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    WriteString(buffer, name);
                    WriteValue(buffer, member, depth + 1);
                }

                break;
        }
    }

    /// <summary>
    /// A JSON number's exact decimal value, canonically: an optional <c>-</c>, its significant digits with leading and
    /// trailing zeros stripped, <c>E</c>, and the exponent that places them (<c>0E0</c> for any zero). So <c>1e17</c>,
    /// <c>100000000000000000</c> and <c>1.0E+17</c> encode alike, as do <c>1.5</c> and <c>1.50</c>, however a store
    /// rewrote the text; nothing passes through a double or a 64-bit integer, so no value is rounded or refused for
    /// its size.
    /// </summary>
    internal static string ExactDecimal(string number)
    {
        var at = 0;
        var negative = number.Length > 0 && number[0] == '-';
        if (negative)
        {
            at = 1;
        }

        var digits = new StringBuilder();
        long exponent = 0;
        var fraction = false;
        for (; at < number.Length && number[at] is not ('e' or 'E'); at++)
        {
            var c = number[at];
            if (c == '.')
            {
                fraction = true;
                continue;
            }

            if (!char.IsAsciiDigit(c))
            {
                throw new ArgumentException("A JSON number holds a character that is not part of a number.", nameof(number));
            }

            digits.Append(c);
            if (fraction)
            {
                exponent--;
            }
        }

        if (at < number.Length)
        {
            if (!long.TryParse(number.AsSpan(at + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var stated)
                || stated is > int.MaxValue or < int.MinValue)
            {
                throw new ArgumentException("A JSON number's exponent is out of range.", nameof(number));
            }

            exponent += stated;
        }

        var text = digits.ToString().TrimStart('0');
        if (text.Length == 0)
        {
            return "0E0";
        }

        var trimmed = text.TrimEnd('0');
        exponent += text.Length - trimmed.Length;
        return (negative ? "-" : string.Empty) + trimmed + "E" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The deepest argument value the content encoding walks: System.Text.Json's own default depth.</summary>
    private const int MaxValueDepth = 64;

    private static void WriteClaims(MemoryStream buffer, ExperienceRecord record, string versionTag)
    {
        ArgumentNullException.ThrowIfNull(record);
        var scope = record.Scope ?? throw new ArgumentException("A record with no scope has no provenance claims to sign.", nameof(record));
        var exposedTo = record.Provenance?.ExposedTo
            ?? throw new ArgumentException("A record with no provenance exposures has no provenance claims to sign.", nameof(record));

        WriteString(buffer, versionTag);
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
    }

    private static void WriteList(MemoryStream buffer, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            buffer.WriteByte(0);
            return;
        }

        buffer.WriteByte(1);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, values.Count);
        buffer.Write(count);
        foreach (var value in values)
        {
            WriteString(buffer, value);
        }
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
