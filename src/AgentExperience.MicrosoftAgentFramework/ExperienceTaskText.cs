using System.Buffers;
using System.Globalization;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Derives a task text for retrieval, or a task description for capture, from one invocation's messages: the
/// user's own latest words, bounded and cleaned up, by one fixed deterministic rule. Nothing calls it on the host's
/// behalf; a host reads <see cref="Injection.ExperienceInjectionContext.DerivedTaskText"/> or
/// <see cref="ExperienceRunContext.DerivedTaskText"/> (or calls <c>Derive</c>) where it chooses to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The latest request.</b> Only messages in the <see cref="ChatRole.User"/> role count. The latest request is the
/// last of them with text whose MAF request-message source is <see cref="AgentRequestMessageSourceType.External"/>
/// (what MAF reports for a message with no source), and that comes after the last message attributed to
/// <see cref="AgentRequestMessageSourceType.ChatHistory"/>. Any other source -- chat history, a context provider, or
/// a custom one -- never counts as the latest request. So when the new input has no text (an image-only turn), the
/// result is <see langword="null"/>: an older message is never taken for the request. Its text is its
/// <see cref="TextContent"/> parts joined with a space; an image or any other part is ignored. This relies on MAF
/// stamping replayed history as <see cref="AgentRequestMessageSourceType.ChatHistory"/>: messages a custom history
/// component adds without that stamp count as new input.
/// </para>
/// <para>
/// <b>A short follow-up.</b> When the latest request is shorter than <see cref="FollowUpThreshold"/> (64) UTF-16 code
/// units after clean-up, the previous user message with text is put before it, separated by
/// <see cref="FollowUpSeparator"/>: "and retry" after "Deploy service X to prod" gives
/// <c>Deploy service X to prod — and retry</c>. That previous message is <see cref="AgentRequestMessageSourceType.External"/>
/// or replayed <see cref="AgentRequestMessageSourceType.ChatHistory"/>, so the join works across turns when the
/// history is replayed into the request (not when the service keeps it, as with a conversation ID); never a context
/// provider's or any other source's. The rule is length-only and knows no language, so a short request that stands on
/// its own ("Why is the build red?") is joined too. The overload that takes a threshold tunes it; 0 turns it off.
/// </para>
/// <para>
/// <b>Never a Historical Reference block.</b> A message that contains <see cref="HistoricalReferenceWriter.BlockBegin"/>
/// (checked also with every invisible character removed) or carries the
/// <see cref="ExperienceContextProvider.HistoricalReferenceKey"/> stamp is skipped entirely, as the latest request and
/// as the previous message alike: a user who pastes an earlier block into a prompt gets no derived text from that
/// message.
/// </para>
/// <para>
/// <b>Clean-up and bounds.</b> Each message's text has its control and format characters removed (unpaired
/// surrogates too), with two exceptions that carry meaning: a zero-width joiner or non-joiner (U+200D, U+200C)
/// between two kept letters, marks or symbols (Persian, Indic scripts, emoji sequences), and the tag characters of an
/// emoji subdivision flag (U+1F3F4 followed by up to 8 of U+E0020–U+E007E and the cancel tag U+E007F); a tag run in
/// any other place is removed. Whitespace runs become one space, and the ends are trimmed. No Unicode normalization
/// is applied. Lengths are UTF-16 code units, and no cut splits a surrogate pair or leaves a joiner or a partial
/// flag at the end. When the joined text would exceed the maximum length, the previous message is cut so the
/// separator and the whole latest request fit; when the latest request alone exceeds it, it is cut and the previous
/// message is dropped. A maximum too small to hold even the first character (a length of 1 before a surrogate pair)
/// gives <see langword="null"/>.
/// </para>
/// <para>
/// <b>It is the user's own words.</b> Nothing here redacts anything: no model is called and no secret, personal
/// data or instruction is recognised. Used as <c>TaskText</c>, it is matched against stored records as written.
/// Stored as <see cref="ExperienceRunDescriptor.TaskDescription"/>, it is stored unsanitized, as a task
/// description always has been, and it becomes part of the durable record. A host whose users may put sensitive
/// content in a prompt should pass the text through its own redaction before storing it.
/// </para>
/// </remarks>
public static class ExperienceTaskText
{
    /// <summary>The length, in UTF-16 code units, <c>Derive</c> cuts to when no other is given: 512.</summary>
    public const int DefaultMaxLength = 512;

    /// <summary>
    /// The default follow-up threshold: a latest request shorter than this many UTF-16 code units (after clean-up) is
    /// a follow-up, and the previous user message is put before it.
    /// </summary>
    public const int FollowUpThreshold = 64;

    /// <summary>What joins the previous user message to a short follow-up: <c>" — "</c>.</summary>
    public const string FollowUpSeparator = " — ";

    private const int ZeroWidthNonJoiner = 0x200C;

    private const int ZeroWidthJoiner = 0x200D;

    private const int WavingBlackFlag = 0x1F3F4;

    private const int TagFirst = 0xE0020;

    private const int TagLast = 0xE007E;

    private const int CancelTag = 0xE007F;

    private const int MaxFlagTags = 8;

    /// <summary>
    /// Derives the task text from <paramref name="messages"/>, or returns <see langword="null"/> when no user message
    /// in them qualifies or the result is blank. See <see cref="ExperienceTaskText"/> for the rule. The sequence is
    /// enumerated once.
    /// </summary>
    /// <param name="messages">The invocation's messages, in conversation order.</param>
    /// <param name="maxLength">
    /// The longest text returned, in UTF-16 code units, from 1 to
    /// <see cref="ExperienceCandidateQuery.MaxTaskTextLength"/> (4096, what retrieval accepts). Defaults to
    /// <see cref="DefaultMaxLength"/>.
    /// </param>
    /// <returns>The derived text, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxLength"/> is below 1 or above <see cref="ExperienceCandidateQuery.MaxTaskTextLength"/>.
    /// </exception>
    public static string? Derive(IEnumerable<ChatMessage> messages, int maxLength = DefaultMaxLength) =>
        Derive(messages, maxLength, FollowUpThreshold);

    /// <summary>
    /// Derives the task text as <see cref="Derive(IEnumerable{ChatMessage}, int)"/> does, with a follow-up threshold
    /// of the host's choosing.
    /// </summary>
    /// <param name="messages">The invocation's messages, in conversation order.</param>
    /// <param name="maxLength">The longest text returned, in UTF-16 code units, from 1 to 4096.</param>
    /// <param name="followUpThreshold">
    /// A latest request shorter than this many UTF-16 code units is joined to the previous user message. 0 turns the
    /// join off; it may not be negative.
    /// </param>
    /// <returns>The derived text, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxLength"/> is out of range, or <paramref name="followUpThreshold"/> is negative.
    /// </exception>
    public static string? Derive(IEnumerable<ChatMessage> messages, int maxLength, int followUpThreshold)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (maxLength < 1 || maxLength > ExperienceCandidateQuery.MaxTaskTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxLength),
                maxLength,
                $"The maximum length must be between 1 and {ExperienceCandidateQuery.MaxTaskTextLength}.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(followUpThreshold);

        // One pass: the latest request, and the user message with text just before it (history allowed).
        string? latest = null;
        string? beforeLatest = null;
        string? lastSeen = null;
        foreach (var message in messages)
        {
            if (message is null)
            {
                continue;
            }

            var source = message.GetAgentRequestMessageSourceType();
            var external = source == AgentRequestMessageSourceType.External;
            var history = source == AgentRequestMessageSourceType.ChatHistory;
            if (history)
            {
                // The latest request is never older than the last replayed message.
                latest = null;
                beforeLatest = null;
            }

            if (message.Role != ChatRole.User || !(external || history) || UsableText(message) is not { } text)
            {
                continue;
            }

            if (external)
            {
                latest = text;
                beforeLatest = lastSeen;
            }

            lastSeen = text;
        }

        if (latest is null)
        {
            return null;
        }

        if (latest.Length >= maxLength)
        {
            return Clip(latest, maxLength);
        }

        if (latest.Length >= followUpThreshold || beforeLatest is null)
        {
            return latest;
        }

        var room = maxLength - latest.Length - FollowUpSeparator.Length;
        var previous = room > 0 ? Clip(beforeLatest, room) : null;
        return previous is null ? latest : previous + FollowUpSeparator + latest;
    }

    /// <summary>The cleaned text of a user message, or <see langword="null"/> when it has none or carries a block.</summary>
    private static string? UsableText(ChatMessage message)
    {
        if (message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true)
        {
            return null;
        }

        var raw = string.Join(
            ' ',
            message.Contents.OfType<TextContent>().Select(part => part.Text).Where(part => !string.IsNullOrEmpty(part)));
        if (raw.Length == 0 || raw.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal))
        {
            return null;
        }

        var cleaned = Clean(raw);
        if (cleaned.Length == 0)
        {
            return null;
        }

        // A marker split by an invisible character reads as the marker once that character is gone; the joiners and
        // flag tags kept are gone here too.
        var probe = new StringBuilder(cleaned.Length);
        foreach (var rune in cleaned.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                probe.Append(rune.ToString());
            }
        }

        return probe.ToString().Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal) ? null : cleaned;
    }

    /// <summary>
    /// Removes control, format and unpaired surrogate code units (per Unicode scalar), keeping a joiner between two
    /// kept letters, marks or symbols and a well-formed subdivision flag's tags; turns whitespace into single spaces;
    /// and trims.
    /// </summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        Rune? pendingJoiner = null;
        Rune? lastKept = null;
        var index = 0;
        while (index < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
            {
                // An unpaired surrogate: not text, so it goes.
                index += Math.Max(consumed, 1);
                continue;
            }

            index += consumed;
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                pendingJoiner = null;
                lastKept = null;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                // A joiner is decided on the next rune that is kept, so a removed character between does not matter.
                if (rune.Value is ZeroWidthJoiner or ZeroWidthNonJoiner && lastKept is { } before && Joinable(before))
                {
                    pendingJoiner ??= rune;
                }

                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (pendingJoiner is { } joiner && Joinable(rune))
            {
                builder.Append(joiner.ToString());
            }

            pendingJoiner = null;
            builder.Append(rune.ToString());
            lastKept = rune;

            if (rune.Value == WavingBlackFlag)
            {
                index += FlagTags(text, index, builder);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends a subdivision flag's tags after its U+1F3F4 base when they are well formed (1 to
    /// <see cref="MaxFlagTags"/> tag characters and the cancel tag), and returns how many code units they took;
    /// otherwise appends nothing and returns 0, so the tags are removed as format characters.
    /// </summary>
    private static int FlagTags(string text, int start, StringBuilder builder)
    {
        var index = start;
        var tags = 0;
        while (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) == OperationStatus.Done)
        {
            index += consumed;
            if (rune.Value == CancelTag)
            {
                if (tags == 0)
                {
                    return 0;
                }

                builder.Append(text, start, index - start);
                return index - start;
            }

            if (rune.Value is < TagFirst or > TagLast || ++tags > MaxFlagTags)
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>A letter, a mark or a symbol: what a joiner may sit between.</summary>
    private static bool Joinable(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.MathSymbol
            or UnicodeCategory.CurrencySymbol => true,
        _ => false,
    };

    /// <summary>
    /// Cuts <paramref name="text"/> to <paramref name="maxLength"/> UTF-16 code units, never inside a surrogate pair,
    /// and trims the end of spaces, joiners and a flag's unfinished tags; <see langword="null"/> when nothing is left.
    /// </summary>
    private static string? Clip(string text, int maxLength)
    {
        if (text.Length > maxLength)
        {
            var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
            text = TrimCutEnd(text[..cut]);
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>Removes trailing spaces, joiners and tag characters (other than a cancel tag) left by a cut.</summary>
    private static string TrimCutEnd(string text)
    {
        var end = text.Length;
        while (end > 0)
        {
            if (text[end - 1] is ' ' or '‌' or '‍')
            {
                end--;
                continue;
            }

            if (end >= 2
                && Rune.TryCreate(text[end - 2], text[end - 1], out var last)
                && last.Value is >= TagFirst and <= TagLast)
            {
                end -= 2;
                continue;
            }

            break;
        }

        return text[..end];
    }
}

/// <summary>
/// The <see cref="ExperienceTaskText.Derive(IEnumerable{ChatMessage}, int)"/> result a context record computes on
/// first read. A record creates its own on first read (a <c>with</c> copy starts without one), and the first read
/// computes it under a lock, so it is computed at most once per instance.
/// </summary>
internal sealed class DerivedTaskTextCache
{
    private readonly Lock _gate = new();
    private bool _computed;
    private string? _value;

    /// <summary>
    /// The derived text, computing it from <paramref name="source"/> on the first call only. If reading the messages
    /// throws, that first call throws and every later call returns <see langword="null"/> without reading them again.
    /// </summary>
    public string? Get(Func<IEnumerable<ChatMessage>?> source)
    {
        lock (_gate)
        {
            if (_computed)
            {
                return _value;
            }

            _computed = true;
            if (source() is { } messages)
            {
                // Snapshot once, so a single-use sequence is read once and later changes are not seen.
                _value = ExperienceTaskText.Derive(messages.ToArray());
            }

            return _value;
        }
    }

    /// <summary>The cache held in <paramref name="field"/>, created there on first use.</summary>
    public static DerivedTaskTextCache For(ref DerivedTaskTextCache? field) =>
        Volatile.Read(ref field) ?? Interlocked.CompareExchange(ref field, new DerivedTaskTextCache(), null) ?? field!;
}
