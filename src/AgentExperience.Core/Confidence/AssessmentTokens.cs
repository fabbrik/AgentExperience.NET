using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Confidence;

/// <summary>
/// Why the confidence path refused to treat a submission's identifiers as an independence key. Carried on
/// <c>ApplyConfidenceEvidenceResult.Refusal</c> with <c>ConfidenceUpdateOutcome.Unverified</c>.
/// </summary>
public enum IndependenceRefusal
{
    /// <summary>
    /// The evidence names the record's own source run. A lesson being "reused" in the run it came from is
    /// not reuse, and would let a run confirm itself. Refused in every mode.
    /// </summary>
    OwnRun,

    /// <summary>
    /// The run is not one the library knows in the record's scope: no record was finalized from it there,
    /// and the capture service does not hold it there.
    /// </summary>
    UnknownRun,

    /// <summary>
    /// Machine evidence named a verification round that finalization did not close for the run -- another
    /// round, a run that was never finalized, or one finalized with no closed round.
    /// </summary>
    UnknownRound,

    /// <summary>Human evidence carried no assessment token.</summary>
    AssessmentTokenMissing,

    /// <summary>
    /// The assessment token is malformed, was not minted under the configured key, or was minted for a
    /// different scope, run, reviewer or direction. These are deliberately indistinguishable: the token's
    /// signature covers all of them, and telling them apart would help a forger.
    /// </summary>
    AssessmentTokenInvalid,

    /// <summary>The assessment token is genuine but its lifetime has passed.</summary>
    AssessmentTokenExpired,

    /// <summary>The assessment token is genuine but does not cover this record.</summary>
    AssessmentTokenNotForRecord,

    /// <summary>
    /// The assessment token has already landed evidence for this record under another evidence ID. A
    /// token is spent once per record; resubmitting the identical evidence is a replay, not a second use.
    /// </summary>
    AssessmentTokenReplayed,

    /// <summary>Human evidence was submitted under verification, and no assessment token key is configured to check it with.</summary>
    AssessmentKeyNotConfigured,
}

/// <summary>
/// One assessment token, as <see cref="AssessmentTokenIssuer.Issue"/> minted it.
/// </summary>
/// <remarks>
/// <see cref="Token"/> is a bearer credential for one review. <see cref="ToString"/> never prints it.
/// </remarks>
/// <param name="AssessmentId">The assessment's identity, carried inside the token. Pass it as <c>HumanReuseAssessment.AssessmentId</c>; it is what the ledgers record.</param>
/// <param name="Token">The token to present as <c>AssessmentToken</c>.</param>
/// <param name="IssuedAt">When it was minted, to the millisecond.</param>
/// <param name="ExpiresAt">When it stops being accepted: signed into the token, so a verifier configured with another lifetime honours this one.</param>
public sealed record IssuedAssessmentToken(Guid AssessmentId, string Token, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AssessmentId = ").Append(AssessmentId)
            .Append(", Token = <redacted>")
            .Append(", IssuedAt = ").Append(IssuedAt)
            .Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

/// <summary>
/// Mints assessment tokens: the only way a human assessment can move a confidence score under
/// <see cref="IndependenceVerification.Verified"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Call it from the host's own review flow, where a person's decision is recorded -- never from code an
/// agent drives.</b> A token is an HMAC-SHA256 signature, under the host's
/// <see cref="ExperienceIndependenceOptions.AssessmentTokenKey"/>, over the assessment's own ID, the time it
/// was minted, the direction (supporting or contradicting), the records it covers, the scope, the run the
/// reuse happened in, and the reviewing principal. The confidence path recomputes it from the submission
/// and refuses anything that does not match, so a token cannot be invented, and one minted for a review
/// cannot be moved to another scope, run, reviewer, direction or record.
/// </para>
/// <para>
/// It expires after <see cref="ExperienceIndependenceOptions.AssessmentTokenLifetime"/>, and the store
/// spends it once per record it covers: another piece of evidence presenting it for the same record is
/// refused, while resubmitting the same evidence stays an idempotent replay.
/// </para>
/// </remarks>
public sealed class AssessmentTokenIssuer
{
    private readonly AssessmentTokenCodec _codec;

    /// <summary>Creates an issuer over the host's independence options.</summary>
    /// <param name="options">Must carry an <see cref="ExperienceIndependenceOptions.AssessmentTokenKey"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No key is configured, the key is shorter than <see cref="ExperienceIndependenceOptions.MinimumAssessmentTokenKeyBytes"/>, or the lifetime or clock is invalid.</exception>
    public AssessmentTokenIssuer(ExperienceIndependenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _codec = AssessmentTokenCodec.Create(options)
            ?? throw new ArgumentException(
                "An assessment token issuer needs ExperienceIndependenceOptions.AssessmentTokenKey.", nameof(options));
    }

    /// <summary>
    /// Mints a token for one human assessment of one run.
    /// </summary>
    /// <param name="reviewer">The reviewing principal's authorization. Its <see cref="AuthorizationContext.PrincipalId"/> is the reviewer the evidence will be counted under, and it must permit <paramref name="scope"/>.</param>
    /// <param name="scope">The exact scope of the records the assessment is about.</param>
    /// <param name="runId">The run the records were reused in, which the assessment judged.</param>
    /// <param name="kind">The direction: reuse helped (<see cref="ConfidenceEvidenceKind.Supporting"/>) or hurt (<see cref="ConfidenceEvidenceKind.Contradicting"/>).</param>
    /// <param name="experienceIds">The records the assessment is about: 1 to <see cref="ExperienceReuseFeedback.MaxExposedRecords"/>, distinct and non-empty.</param>
    /// <returns>The token, its assessment ID, and its validity window.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument is invalid, or <paramref name="scope"/> lies outside the reviewer's authorization.</exception>
    public IssuedAssessmentToken Issue(
        AuthorizationContext reviewer,
        Scope scope,
        Guid runId,
        ConfidenceEvidenceKind kind,
        IReadOnlyList<Guid> experienceIds)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(experienceIds);

        if (string.IsNullOrWhiteSpace(reviewer.PrincipalId)
            || !string.Equals(reviewer.PrincipalId, reviewer.PrincipalId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The reviewer's PrincipalId must be non-blank and carry no leading or trailing whitespace: it is the reviewer the evidence is counted under.",
                nameof(reviewer));
        }

        if (!reviewer.Permits(scope))
        {
            throw new ArgumentException("The scope lies outside the reviewer's authorization.", nameof(scope));
        }

        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The run must not be an empty GUID.", nameof(runId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentException("The evidence kind must be defined.", nameof(kind));
        }

        var ids = experienceIds.ToArray();
        if (ids.Length is 0 or > ExperienceReuseFeedback.MaxExposedRecords)
        {
            throw new ArgumentException(
                $"An assessment covers 1 to {ExperienceReuseFeedback.MaxExposedRecords} records.", nameof(experienceIds));
        }

        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
        {
            throw new ArgumentException("The records must be distinct and non-empty.", nameof(experienceIds));
        }

        return _codec.Encode(Guid.NewGuid(), scope, runId, reviewer.PrincipalId, kind, ids);
    }
}

/// <summary>
/// The token format, and the one place a token is signed or checked.
/// </summary>
/// <remarks>
/// <para>
/// <c>aexat1.</c> followed by the base64url (unpadded) encoding of: a version byte (1); the assessment ID
/// (16 bytes, big-endian); the issue time and the expiry (Unix milliseconds, 8 bytes each, big-endian); the
/// kind (1 byte); the number of records (1 byte); each record ID (16 bytes, big-endian); and the 32-byte
/// HMAC-SHA256. The expiry is signed in, so a token lives exactly as long as the lifetime it was minted
/// under, whatever lifetime the verifier is configured with.
/// </para>
/// <para>
/// The MAC is over a fixed domain label, every byte before it, and then -- not carried in the token,
/// supplied by whoever verifies -- the six scope fields (each a presence byte, then a 4-byte length and
/// its UTF-8), the run ID, and the reviewer principal (a 4-byte length and its UTF-8). Every variable
/// field is length-prefixed, so no two different bindings share an input.
/// </para>
/// </remarks>
internal sealed class AssessmentTokenCodec
{
    internal const string Prefix = "aexat1.";

    /// <summary>A generous upper bound on a token's length, checked before any decoding work.</summary>
    internal const int MaxTokenLength = 4096;

    /// <summary>How far in the future an issue time may lie before a token is refused: clock skew between hosts.</summary>
    internal static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);

    private const byte Version = 1;
    private const int HeaderLength = 1 + 16 + 8 + 8 + 1 + 1;
    private const int IssuedAtOffset = 17;
    private const int ExpiresAtOffset = 25;
    private const int KindOffset = 33;
    private const int CountOffset = 34;
    private const int IdLength = 16;
    private const int MacLength = 32;

    private static readonly byte[] DomainLabel = Encoding.ASCII.GetBytes("AgentExperience.AssessmentToken/v1\0");

    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _timeProvider;

    private AssessmentTokenCodec(byte[] key, TimeSpan lifetime, TimeProvider timeProvider)
    {
        _key = key;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Validates the options and returns a codec over a private copy of the key, or <see langword="null"/>
    /// when no key is configured.
    /// </summary>
    internal static AssessmentTokenCodec? Create(ExperienceIndependenceOptions options)
    {
        if (!Enum.IsDefined(options.Verification))
        {
            throw new ArgumentException("ExperienceIndependenceOptions.Verification must be a defined mode.", nameof(options));
        }

        if (options.AssessmentTokenLifetime <= TimeSpan.Zero
            || options.AssessmentTokenLifetime > ExperienceIndependenceOptions.MaximumAssessmentTokenLifetime)
        {
            throw new ArgumentException(
                $"ExperienceIndependenceOptions.AssessmentTokenLifetime must be strictly positive and at most {ExperienceIndependenceOptions.MaximumAssessmentTokenLifetime}.",
                nameof(options));
        }

        if (options.TimeProvider is null)
        {
            throw new ArgumentException("ExperienceIndependenceOptions.TimeProvider must be set.", nameof(options));
        }

        if (options.AssessmentTokenKey is not { } key)
        {
            return null;
        }

        if (key.Length < ExperienceIndependenceOptions.MinimumAssessmentTokenKeyBytes)
        {
            // The length only: never the key, or anything derived from it.
            throw new ArgumentException(
                $"ExperienceIndependenceOptions.AssessmentTokenKey must be at least {ExperienceIndependenceOptions.MinimumAssessmentTokenKeyBytes} bytes.",
                nameof(options));
        }

        return new AssessmentTokenCodec(key.ToArray(), options.AssessmentTokenLifetime, options.TimeProvider);
    }

    internal IssuedAssessmentToken Encode(
        Guid assessmentId,
        Scope scope,
        Guid runId,
        string reviewer,
        ConfidenceEvidenceKind kind,
        IReadOnlyList<Guid> experienceIds)
    {
        var issuedAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAtMs = issuedAtMs + (long)_lifetime.TotalMilliseconds;
        var body = new byte[HeaderLength + (experienceIds.Count * IdLength) + MacLength];

        body[0] = Version;
        assessmentId.TryWriteBytes(body.AsSpan(1, IdLength), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(IssuedAtOffset, 8), issuedAtMs);
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(ExpiresAtOffset, 8), expiresAtMs);
        body[KindOffset] = (byte)kind;
        body[CountOffset] = (byte)experienceIds.Count;
        for (var index = 0; index < experienceIds.Count; index++)
        {
            experienceIds[index].TryWriteBytes(body.AsSpan(HeaderLength + (index * IdLength), IdLength), bigEndian: true, out _);
        }

        var signed = body.Length - MacLength;
        ComputeMac(body.AsSpan(0, signed), scope, runId, reviewer, body.AsSpan(signed));

        return new IssuedAssessmentToken(
            assessmentId,
            Prefix + Base64Url.EncodeToString(body),
            DateTimeOffset.FromUnixTimeMilliseconds(issuedAtMs),
            DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs));
    }

    /// <summary>
    /// Checks a token against the binding the submission names. The signature is checked before anything
    /// the token claims is looked at, so a forged token learns nothing about expiry or coverage; the
    /// comparison is constant-time.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> and the assessment ID and covered records when the token is genuine, bound to
    /// exactly this scope, run, reviewer and kind, and inside its lifetime; otherwise the refusal.
    /// </returns>
    internal (IndependenceRefusal? Refusal, Guid AssessmentId, IReadOnlyList<Guid> Covered) Verify(
        string? token,
        Scope scope,
        Guid runId,
        string reviewer,
        ConfidenceEvidenceKind kind)
    {
        if (string.IsNullOrEmpty(token))
        {
            return (IndependenceRefusal.AssessmentTokenMissing, Guid.Empty, []);
        }

        if (token.Length > MaxTokenLength || !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Invalid();
        }

        var encoded = token.AsSpan(Prefix.Length);

        // The unpadded base64url alphabet and nothing else, checked before decoding: the decoder throws
        // on a character outside it rather than reporting failure, and a refusal must never be an exception.
        foreach (var character in encoded)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return Invalid();
            }
        }

        var body = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(encoded, body, out written))
            {
                return Invalid();
            }
        }
        catch (FormatException)
        {
            return Invalid();
        }

        var bytes = body.AsSpan(0, written);
        if (bytes.Length < HeaderLength + IdLength + MacLength || bytes[0] != Version)
        {
            return Invalid();
        }

        var count = bytes[CountOffset];
        if (count is 0 or > ExperienceReuseFeedback.MaxExposedRecords
            || bytes.Length != HeaderLength + (count * IdLength) + MacLength)
        {
            return Invalid();
        }

        // A non-canonical encoding of the same bytes must not be a different token: re-encoding what was
        // decoded has to give back exactly what was presented.
        if (!string.Equals(Base64Url.EncodeToString(bytes), encoded.ToString(), StringComparison.Ordinal))
        {
            return Invalid();
        }

        var signed = bytes.Length - MacLength;
        Span<byte> expected = stackalloc byte[MacLength];
        ComputeMac(bytes[..signed], scope, runId, reviewer, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes[signed..]))
        {
            return Invalid();
        }

        // Genuine, and bound to this scope, run and reviewer. What it claims can be read now.
        if (bytes[KindOffset] != (byte)kind)
        {
            return Invalid();
        }

        var issuedAtMs = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(IssuedAtOffset, 8));
        var expiresAtMs = BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(ExpiresAtOffset, 8));
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // Only this library signs, so these hold for every genuine token; they are checked anyway, so a
        // key shared with a careless minter still cannot produce a token that never expires.
        if (issuedAtMs < 0
            || expiresAtMs <= issuedAtMs
            || expiresAtMs - issuedAtMs > (long)ExperienceIndependenceOptions.MaximumAssessmentTokenLifetime.TotalMilliseconds
            || issuedAtMs > now + (long)AllowedClockSkew.TotalMilliseconds)
        {
            return Invalid();
        }

        if (now >= expiresAtMs)
        {
            return (IndependenceRefusal.AssessmentTokenExpired, Guid.Empty, []);
        }

        var covered = new Guid[count];
        for (var index = 0; index < count; index++)
        {
            covered[index] = new Guid(bytes.Slice(HeaderLength + (index * IdLength), IdLength), bigEndian: true);
        }

        return (null, new Guid(bytes.Slice(1, IdLength), bigEndian: true), covered);

        static (IndependenceRefusal?, Guid, IReadOnlyList<Guid>) Invalid() =>
            (IndependenceRefusal.AssessmentTokenInvalid, Guid.Empty, []);
    }

    private void ComputeMac(ReadOnlySpan<byte> signed, Scope scope, Guid runId, string reviewer, Span<byte> destination)
    {
        using var buffer = new MemoryStream();
        buffer.Write(DomainLabel);
        buffer.Write(signed);
        WriteOptional(buffer, scope.TenantId);
        WriteOptional(buffer, scope.ApplicationId);
        WriteOptional(buffer, scope.ProjectId);
        WriteOptional(buffer, scope.TeamId);
        WriteOptional(buffer, scope.AgentId);
        WriteOptional(buffer, scope.UserId);

        Span<byte> run = stackalloc byte[IdLength];
        runId.TryWriteBytes(run, bigEndian: true, out _);
        buffer.Write(run);
        WriteString(buffer, reviewer);

        HMACSHA256.HashData(_key, buffer.GetBuffer().AsSpan(0, (int)buffer.Length), destination);
    }

    private static void WriteOptional(MemoryStream buffer, string? value)
    {
        if (value is null)
        {
            buffer.WriteByte(0);
            return;
        }

        buffer.WriteByte(1);
        WriteString(buffer, value);
    }

    private static void WriteString(MemoryStream buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        buffer.Write(length);
        buffer.Write(bytes);
    }
}
