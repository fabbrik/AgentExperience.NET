using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErrorClassRule = AgentExperience.Abstractions.ErrorClassification.ErrorClass;

namespace AgentExperience.Abstractions;

/// <summary>
/// The failure signature of one failed tool call (story 28.2): which tool failed, how, and a fingerprint of what it
/// said, as a key a lesson can be looked up by while a run is failing. It is derived only from what a record already
/// stores (<see cref="ToolCallRecord.ToolName"/> and <see cref="ToolCallRecord.Error"/>), deterministically, so the
/// same failure in two runs has the same signature whatever IDs, paths, line numbers or timestamps its message named.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fields.</b> <see cref="ToolName"/> is the call's tool name, as recorded. <see cref="ErrorClass"/> is the first
/// token of the error's class -- exactly the token the injected block's <c>[failed: …]</c> marker shows for that call
/// (<c>TimeoutException</c>, <c>exit 2</c>, <c>HTTP 503</c>, <c>ENOENT</c>, <c>Timeout</c>, or
/// <c>unclassified error</c>). <see cref="Fingerprint"/> is the first 16 bytes of the SHA-256 of the normalized excerpt
/// (<see cref="NormalizeExcerpt"/>) in UTF-8, as 32 lowercase hexadecimal characters. <see cref="Key"/> joins the three
/// with <see cref="KeySeparator"/> (U+001F) into the one string both stores index.
/// </para>
/// <para>
/// <b>What is not a signature.</b> A call with no error (<see cref="ToolCallRecord.Error"/> is <see langword="null"/>)
/// has none. Nor does a call whose tool name is blank, longer than <see cref="MaxToolNameLength"/> characters, holds a
/// control character (the separator among them) or is not well-formed UTF-16: such a name cannot be keyed and indexed
/// faithfully, and a real tool's name is a short identifier. An attempt's own error, outside any tool call, is not a
/// signature either.
/// </para>
/// <para>
/// <b>Versioned.</b> The normalization and the hash are fixed for <see cref="SignatureVersion"/>; a store records the
/// version with every signature and matches only signatures of the version it is asked for, so a later change to the
/// rule is a new version, never a silent change in what matches.
/// </para>
/// <para>
/// A signature holds no error text: only the class and a one-way fingerprint of a normalized first line. Whoever knows
/// a candidate message can still test whether a record holds it, as with any unsalted hash.
/// </para>
/// </remarks>
/// <param name="ToolName">The failed call's tool name.</param>
/// <param name="ErrorClass">The first token of the error's class, as the call's <c>[failed: …]</c> marker shows it.</param>
/// <param name="Fingerprint">32 lowercase hexadecimal characters: the first 16 bytes of the SHA-256 of the normalized excerpt.</param>
public sealed record ExperienceFailureSignature(string ToolName, string ErrorClass, string Fingerprint)
{
    /// <summary>The version of the normalization and hash this type derives. A store matches only signatures of one version.</summary>
    public const int SignatureVersion = 1;

    /// <summary>The longest tool name a signature is derived for.</summary>
    public const int MaxToolNameLength = 256;

    /// <summary>The longest error class a signature carries: an error class token is at most 40 characters.</summary>
    public const int MaxErrorClassLength = 64;

    /// <summary>How many characters of the error's trimmed first line are normalized and hashed.</summary>
    public const int MaxExcerptLength = 500;

    /// <summary>The length of <see cref="Fingerprint"/>: 16 bytes, as lowercase hexadecimal.</summary>
    public const int FingerprintLength = 32;

    /// <summary>The character <see cref="Key"/> joins its three parts with: U+001F, the unit separator.</summary>
    public const char KeySeparator = '\u001F';

    /// <summary>The placeholder a GUID becomes in a normalized excerpt.</summary>
    public const string GuidPlaceholder = "<guid>";

    /// <summary>The placeholder a run of 8 or more hexadecimal characters becomes in a normalized excerpt.</summary>
    public const string HexPlaceholder = "<hex>";

    /// <summary>The placeholder a file path becomes in a normalized excerpt, followed by its file name's extension when it has one.</summary>
    public const string PathPlaceholder = "<path>";

    /// <summary>The placeholder a quoted literal becomes in a normalized excerpt.</summary>
    public const string StringPlaceholder = "<str>";

    /// <summary>The placeholder an ISO 8601 date or timestamp becomes in a normalized excerpt.</summary>
    public const string TimePlaceholder = "<time>";

    /// <summary>The placeholder a number becomes in a normalized excerpt.</summary>
    public const string NumberPlaceholder = "<n>";

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    // NonBacktracking: every rule is linear in the (capped) excerpt, so normalization needs no timeout and always
    // finishes with the same answer. Every rule runs over invariant-lowercase text.
    private static readonly Regex Guids = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", Options);

    private static readonly Regex HexRuns = new(@"\b(0x)?[0-9a-f]{8,}\b", Options);

    private static readonly Regex Paths = new(@"([a-z]:)?[\w.~<>-]*([/\\][\w.~<>-]*)+", Options);

    private static readonly Regex Quoted = new("'[^']*'|\"[^\"]*\"|‘[^’]*’|“[^”]*”", Options);

    private static readonly Regex Timestamps = new(
        @"\b[0-9]{4}-[0-9]{2}-[0-9]{2}([t ][0-9]{2}:[0-9]{2}(:[0-9]{2}([.,][0-9]+)?)?(z|[+-][0-9]{2}(:?[0-9]{2})?)?)?",
        Options);

    private static readonly Regex Numbers = new(@"[0-9]+(\.[0-9]+)?", Options);

    private static readonly Regex Whitespace = new(@"\s+", Options);

    private static readonly Regex Extension = new(@"^[a-z0-9]{1,10}$", Options);

    /// <summary>
    /// The single canonical string both stores index: <see cref="ToolName"/>, <see cref="ErrorClass"/> and
    /// <see cref="Fingerprint"/>, joined by <see cref="KeySeparator"/>.
    /// </summary>
    public string Key => ToolName + KeySeparator + ErrorClass + KeySeparator + Fingerprint;

    /// <summary>
    /// The signature of <paramref name="toolCall"/>, or <see langword="null"/> when it recorded no error or its tool
    /// name cannot be keyed (see the remarks on <see cref="ExperienceFailureSignature"/>).
    /// </summary>
    /// <param name="toolCall">A recorded tool call.</param>
    /// <returns>Its failure signature, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="toolCall"/> is <see langword="null"/>.</exception>
    public static ExperienceFailureSignature? From(ToolCallRecord toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return From(toolCall.ToolName, toolCall.Error);
    }

    /// <summary>
    /// The signature of a call to <paramref name="toolName"/> that failed with <paramref name="error"/>, or
    /// <see langword="null"/> when <paramref name="error"/> is <see langword="null"/> or the tool name cannot be keyed.
    /// The same rule as <see cref="From(ToolCallRecord)"/>, for a failure seen before any record holds it.
    /// </summary>
    /// <param name="toolName">The failed call's tool name.</param>
    /// <param name="error">The call's (sanitized) error text; <see langword="null"/> when it did not fail.</param>
    /// <returns>Its failure signature, or <see langword="null"/>.</returns>
    public static ExperienceFailureSignature? From(string? toolName, string? error)
    {
        if (error is null || !IsKeyableToolName(toolName))
        {
            return null;
        }

        return new ExperienceFailureSignature(toolName!, FirstToken(ErrorClassRule.Of(error)), FingerprintOf(error));
    }

    /// <summary>
    /// Whether <paramref name="toolName"/> can be part of a signature: non-blank, at most
    /// <see cref="MaxToolNameLength"/> characters, no control character, and well-formed UTF-16.
    /// </summary>
    /// <param name="toolName">A tool name.</param>
    /// <returns>Whether a failed call to it has a signature.</returns>
    public static bool IsKeyableToolName(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Length > MaxToolNameLength)
        {
            return false;
        }

        for (var i = 0; i < toolName.Length; i++)
        {
            var c = toolName[i];
            if (char.IsControl(c))
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= toolName.Length || !char.IsLowSurrogate(toolName[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The fingerprint of <paramref name="error"/>: the first 16 bytes of the SHA-256 of its normalized excerpt, in UTF-8, as lowercase hexadecimal.</summary>
    /// <param name="error">An error text.</param>
    /// <returns>32 lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static string FingerprintOf(string error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeExcerpt(error)));
        return Convert.ToHexStringLower(hash.AsSpan(0, FingerprintLength / 2));
    }

    /// <summary>
    /// The normalized excerpt of <paramref name="error"/> that <see cref="Fingerprint"/> hashes, by the rule of
    /// <see cref="SignatureVersion"/> 1, applied in this order:
    /// <list type="number">
    /// <item><description>The error is trimmed and its first line taken (up to the first line feed or carriage return), trimmed again, and cut to <see cref="MaxExcerptLength"/> characters (never inside a surrogate pair).</description></item>
    /// <item><description>It is lowercased with the invariant culture.</description></item>
    /// <item><description>GUIDs (<c>8-4-4-4-12</c> hexadecimal) become <see cref="GuidPlaceholder"/>.</description></item>
    /// <item><description>Runs of 8 or more hexadecimal characters between word boundaries become <see cref="HexPlaceholder"/> when they hold both a digit and a letter, or follow <c>0x</c>; a run of digits alone is left for the number rule, and a word of letters alone is left as it is.</description></item>
    /// <item><description>File paths -- absolute or relative, POSIX or Windows, any token of word characters, dots, tildes, minus signs and placeholders with at least one <c>/</c> or <c>\</c> and at least one letter or digit -- become <see cref="PathPlaceholder"/>, followed by the final file name's extension (a dot and 1 to 10 letters or digits) when it has one: <c>&lt;path&gt;.py</c>.</description></item>
    /// <item><description>Quoted literals (<c>'…'</c>, <c>"…"</c>, and their typographic forms) become <see cref="StringPlaceholder"/>.</description></item>
    /// <item><description>ISO 8601 dates and timestamps (<c>2026-10-30</c>, <c>2026-10-30T12:00:01.5Z</c>, with an offset or without) become <see cref="TimePlaceholder"/>.</description></item>
    /// <item><description>Numbers (integers and decimals, digits <c>0-9</c> anywhere, so <c>line 42</c> and <c>python3</c> alike) become <see cref="NumberPlaceholder"/>.</description></item>
    /// <item><description>Every run of whitespace becomes one space, and the result is trimmed.</description></item>
    /// </list>
    /// </summary>
    /// <param name="error">An error text.</param>
    /// <returns>The normalized excerpt; empty for a blank error.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is <see langword="null"/>.</exception>
    public static string NormalizeExcerpt(string error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var text = error.Trim();
        var end = text.AsSpan().IndexOfAny('\n', '\r');
        if (end >= 0)
        {
            text = text[..end];
        }

        text = text.Trim();
        if (text.Length > MaxExcerptLength)
        {
            var cut = char.IsHighSurrogate(text[MaxExcerptLength - 1]) ? MaxExcerptLength - 1 : MaxExcerptLength;
            text = text[..cut];
        }

        text = text.ToLowerInvariant();
        text = Guids.Replace(text, GuidPlaceholder);
        text = HexRuns.Replace(text, HexRun);
        text = Paths.Replace(text, PathOf);
        text = Quoted.Replace(text, StringPlaceholder);
        text = Timestamps.Replace(text, TimePlaceholder);
        text = Numbers.Replace(text, NumberPlaceholder);
        return Whitespace.Replace(text, " ").Trim();
    }

    /// <summary>The first token of an error class, whose tokens are joined with <c>", "</c>.</summary>
    private static string FirstToken(string errorClass)
    {
        var end = errorClass.IndexOf(',', StringComparison.Ordinal);
        return end < 0 ? errorClass : errorClass[..end];
    }

    private static string HexRun(Match match)
    {
        var value = match.Value;
        if (value.StartsWith("0x", StringComparison.Ordinal))
        {
            return HexPlaceholder;
        }

        return value.Any(char.IsAsciiDigit) && value.Any(char.IsAsciiLetter) ? HexPlaceholder : value;
    }

    private static string PathOf(Match match)
    {
        var value = match.Value;
        if (!value.Any(char.IsLetterOrDigit))
        {
            // Separators alone ("/", " / ", "\\"): punctuation, not a path.
            return value;
        }

        var name = value[(value.LastIndexOfAny(['/', '\\']) + 1)..].TrimEnd('.');
        var dot = name.LastIndexOf('.');
        if (dot > 0 && Extension.IsMatch(name[(dot + 1)..]))
        {
            return PathPlaceholder + "." + name[(dot + 1)..];
        }

        return PathPlaceholder;
    }
}

/// <summary>Whether a signature a record holds was followed by a call that worked.</summary>
public enum ExperienceSignatureResolution
{
    /// <summary>No later call in the record succeeded after the failure's last occurrence, or the record's run was not verified.</summary>
    Unresolved = 0,

    /// <summary>
    /// The record's run was verified (<see cref="TaskVerificationStatus.Verified"/>) and a later call in it succeeded
    /// after the failure's last occurrence: the lesson knows what worked after that failure.
    /// </summary>
    Resolved = 1,
}

/// <summary>One failure signature a record holds, with whether something worked after it.</summary>
/// <param name="Signature">The failure signature.</param>
/// <param name="Resolution">Whether a later call in the same verified record succeeded after the failure's last occurrence.</param>
public sealed record ExperienceRecordFailureSignature(ExperienceFailureSignature Signature, ExperienceSignatureResolution Resolution);

/// <summary>The failure signatures of a whole record (story 28.2).</summary>
public static class ExperienceFailureSignatures
{
    /// <summary>
    /// Every failed tool call's signature in <paramref name="record"/>, in attempt order and then call order (by
    /// sequence number), de-duplicated by <see cref="ExperienceFailureSignature.Key"/> (the first occurrence keeps its
    /// place). Each is <see cref="ExperienceSignatureResolution.Resolved"/> when the record's outcome is
    /// <see cref="TaskVerificationStatus.Verified"/> and some call after the signature's <em>last</em> occurrence
    /// recorded no error; otherwise <see cref="ExperienceSignatureResolution.Unresolved"/>. A failure that came back
    /// after a success was not resolved by it.
    /// </summary>
    /// <param name="record">A record.</param>
    /// <returns>Its signatures; empty when no tool call failed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ExperienceRecordFailureSignature> Of(ExperienceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var calls = (record.Attempts ?? [])
            .Where(attempt => attempt is not null)
            .OrderBy(attempt => attempt.SequenceNumber)
            .SelectMany(attempt => (attempt.ToolCalls ?? []).Where(call => call is not null).OrderBy(call => call.SequenceNumber))
            .ToList();

        var order = new List<ExperienceFailureSignature>();
        var lastOccurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastSuccess = -1;
        for (var i = 0; i < calls.Count; i++)
        {
            if (calls[i].Error is null)
            {
                lastSuccess = i;
                continue;
            }

            if (ExperienceFailureSignature.From(calls[i]) is not { } signature)
            {
                continue;
            }

            if (!lastOccurrence.ContainsKey(signature.Key))
            {
                order.Add(signature);
            }

            lastOccurrence[signature.Key] = i;
        }

        var verified = record.Outcome?.Status == TaskVerificationStatus.Verified;
        return [.. order.Select(signature => new ExperienceRecordFailureSignature(
            signature,
            verified && lastSuccess > lastOccurrence[signature.Key]
                ? ExperienceSignatureResolution.Resolved
                : ExperienceSignatureResolution.Unresolved))];
    }
}
