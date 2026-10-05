using System.Buffers;
using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Core.Sanitization;

namespace AgentExperience.Core.Reflections;

/// <summary>
/// How finalization screens what an <see cref="IExperienceReflector"/> wrote before the record that
/// carries it is created: the payload kind and field names a host's <see cref="ISanitizer"/> sees, and
/// the default policy <see cref="DefaultSanitizer"/> applies to that kind when the host configured none.
/// </summary>
/// <remarks>
/// <para>
/// A reflector is a host seam, and a model-backed one can write any free text. So every reflection that
/// passes <see cref="ReflectionRequest.EnsureMatches"/> is screened in two layers, whichever reflector
/// wrote it, the default one included:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Built-in hygiene.</b> The six free-text fields (<see cref="ScreenedFieldNames"/>) and
/// <see cref="Reflection.Producer"/> are held to <see cref="ReflectionLimits"/>. Invisible characters are
/// removed (see <see cref="Neutralize"/>) with exactly the rule the Historical Reference writer applies to a
/// tool name. A text that is empty or blank afterwards counts as absent: an absent list item is dropped,
/// absent reuse guidance becomes <see langword="null"/>, and an absent lesson or producer refuses the
/// reflection.
/// </description></item>
/// <item><description>
/// <b>The host's sanitizer.</b> The six fields then go through the registered <see cref="ISanitizer"/> as
/// one <see cref="RawPayload"/> of kind <see cref="PayloadKind"/>, each list as a list of strings and absent
/// reuse guidance as <see langword="null"/>, within <see cref="ReflectionLimits.SanitizerTimeout"/>. A
/// rejection, a timeout, a throw, or an omitted field or item refuses the reflection. Redactions are kept,
/// and the redacted fields' paths (<c>Lesson</c>, <c>Warnings[2]</c>, indexed into the stored lists) are
/// reported on the finalization result, never their values. What the sanitizer returns is put through the
/// hygiene layer again, so a redaction can neither reintroduce an invisible character nor push a field
/// over its limit. The sanitizer sees the text as the reflector wrote it, less the invisible characters:
/// it is not Unicode-normalized, so a pattern rule should fold (for example NFKC plus confusable folding)
/// inside the sanitizer.
/// </description></item>
/// <item><description>
/// <b>The content guard, for model-authored text only.</b> A reflection whose
/// <see cref="Reflection.Authorship"/> is anything but <see cref="ReflectionAuthorship.Deterministic"/>, or whose
/// <see cref="Reflection.Producer"/> names the library's own model-backed reflector, is then
/// refused, as <see cref="ReflectionScreeningRefusal.UnsafeContent"/>, when a screened field holds a whole URL,
/// hostname or IP address that is not in the run content its reflector was given (see
/// <see cref="IReflectionRunContent"/>; with none, every one is refused), a <c>data:</c>, <c>javascript:</c>,
/// <c>vbscript:</c> or <c>file:</c> link or a UNC path, instruction-override phrasing (also across all fields
/// joined), credential-shaped text, or a word mixing Latin with Cyrillic or Greek letters. It is a fixed,
/// best-effort filter, never a model call, and never applied to a deterministic reflection.
/// </description></item>
/// </list>
/// <para>
/// A refused reflection quarantines the record, exactly as a binding mismatch does, with a
/// content-free reason that names the field and the limit and never the text, and a
/// <see cref="ReflectionScreeningRefusal"/> code. The bound fields -- the identifiers, the verdict, the
/// score, the rule version, the evidence IDs and <c>CreatedAt</c> -- are never rewritten.
/// </para>
/// <para>
/// No layer detects prompt injection or an arbitrary secret in free text. The hygiene layer
/// removes what a reader cannot see and bounds what it can; the content guard catches a few fixed
/// shapes in model-authored text; anything beyond that is the host sanitizer's policy, and the approval
/// boundary around tools stays the control.
/// </para>
/// </remarks>
public static class ReflectionScreening
{
    /// <summary>The <see cref="RawPayload.Kind"/> a reflection's free-text fields are sanitized as.</summary>
    public const string PayloadKind = "ExperienceReflection";

    /// <summary>The most combining marks in a row a screened text keeps; the rest of the run is removed.</summary>
    public const int MaxConsecutiveCombiningMarks = 4;

    /// <summary>The field names of that payload, which are exactly the reflection's free-text fields.</summary>
    public static IReadOnlyList<string> ScreenedFieldNames { get; } =
    [
        nameof(Reflection.Lesson),
        nameof(Reflection.SuccessfulApproaches),
        nameof(Reflection.FailedApproaches),
        nameof(Reflection.Preconditions),
        nameof(Reflection.Warnings),
        nameof(Reflection.ReuseGuidance),
    ];

    /// <summary>
    /// The policy <see cref="DefaultSanitizer"/> applies to a <see cref="PayloadKind"/> payload when its
    /// <see cref="SanitizationOptions"/> configure none: <see cref="SanitizationPolicyFor"/> the
    /// <see cref="ReflectionLimits.Default"/> limits. It changes nothing a screened reflection can hold.
    /// </summary>
    public static SanitizationPolicy DefaultSanitizationPolicy { get; } = SanitizationPolicyFor(ReflectionLimits.Default);

    private static readonly string[] ListFieldNames =
    [
        nameof(Reflection.SuccessfulApproaches),
        nameof(Reflection.FailedApproaches),
        nameof(Reflection.Preconditions),
        nameof(Reflection.Warnings),
    ];

    private static readonly FrozenSet<string> ListFieldSet = ListFieldNames.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>What <see cref="Neutralize"/> uses internally to mean "append nothing".</summary>
    private const char Removed = '\0';

    /// <summary>
    /// A policy for the <see cref="PayloadKind"/> payload that allows exactly the screened fields, classifies
    /// none as secret, and is bounded by <paramref name="limits"/>: depth 2 (the payload and its lists), as
    /// many entries as the larger of the six fields and <see cref="ReflectionLimits.MaxListItems"/>, and values
    /// as long as the longer of <see cref="ReflectionLimits.MaxLessonLength"/> and
    /// <see cref="ReflectionLimits.MaxListItemLength"/>. A host that raises its limits above the defaults and
    /// screens through a <see cref="DefaultSanitizer"/> configures this policy (or one derived from it with
    /// <c>with</c>) for <see cref="PayloadKind"/>, or the built-in policy rejects what the raised limits allow.
    /// </summary>
    /// <param name="limits">The limits finalization screens against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="limits"/> is <see langword="null"/>.</exception>
    public static SanitizationPolicy SanitizationPolicyFor(ReflectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        return new SanitizationPolicy(
            AllowedFieldNames: ScreenedFieldNames.ToFrozenSet(StringComparer.Ordinal),
            SecretFieldNames: FrozenSet<string>.Empty,
            MaxDepth: 2,
            MaxFieldCount: Math.Max(ScreenedFieldNames.Count, limits.MaxListItems),
            MaxValueLength: Math.Max(limits.MaxLessonLength, limits.MaxListItemLength),
            MaxFieldNameLength: 64);
    }

    /// <summary>
    /// Screens <paramref name="reflection"/>'s free-text fields and producer, returning the screened reflection
    /// and the redacted paths, or a content-free refusal.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    internal static async Task<ReflectionScreeningResult> ScreenAsync(
        Reflection reflection,
        ISanitizer sanitizer,
        ReflectionLimits limits,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? runContent = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Layer 1: the reflector's own text.
        string producer;
        Hygienic first;
        try
        {
            if (!Enum.IsDefined(reflection.Authorship))
            {
                throw new Refusal(ReflectionScreeningRefusal.UndefinedAuthorship, "its Authorship is not a defined ReflectionAuthorship value");
            }

            producer = Clean(reflection.Producer)
                ?? throw new Refusal(ReflectionScreeningRefusal.MissingProducer, "its Producer is missing, or empty once invisible characters are removed");
            EnsureLength(producer, limits.MaxProducerLength, nameof(Reflection.Producer));

            var lists = new List<string?>[ListFieldNames.Length];
            for (var list = 0; list < lists.Length; list++)
            {
                lists[list] = Snapshot(ListOf(reflection, list), ListFieldNames[list], limits);
            }

            first = Hygiene(reflection.Lesson, lists, reflection.ReuseGuidance, sourceIndexes: null, limits, afterSanitizer: false);
        }
        catch (Refusal refusal)
        {
            return refusal.ToResult();
        }

        // Layer 2: the host's sanitizer, bounded in time.
        SanitizedPayload sanitized;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(limits.SanitizerTimeout);
            try
            {
                var pending = sanitizer.SanitizeAsync(new RawPayload(PayloadKind, first.ToPayloadFields()), budget.Token);
                try
                {
                    sanitized = await pending.WaitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!pending.IsCompleted)
                {
                    // Abandoned, not awaited: observe whatever it ends with so it is never unobserved.
                    _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                return ReflectionScreeningResult.Refused(
                    ReflectionScreeningRefusal.SanitizerTimedOut,
                    Invariant($"the sanitizer did not answer within {limits.SanitizerTimeout.TotalSeconds.ToString("R", CultureInfo.InvariantCulture)}s"),
                    exceptionType: null);
            }
            catch (Exception ex)
            {
                // Only the type, and never in the reason: a host sanitizer's message may quote what it was
                // looking at, and a host type is not the library's text to repeat.
                return ReflectionScreeningResult.Refused(ReflectionScreeningRefusal.SanitizerFailed, "the sanitizer threw", ex.GetType().FullName);
            }
        }

        // Layer 1 again, over what the sanitizer returned.
        try
        {
            if (sanitized is null)
            {
                throw new Refusal(ReflectionScreeningRefusal.SanitizerFailed, "the sanitizer returned no result");
            }

            if (sanitized.Decision == SanitizationDecision.Rejected)
            {
                // The sanitizer's own reason is not repeated: a host's may quote the text it rejected.
                throw new Refusal(ReflectionScreeningRefusal.SanitizerRejected, Invariant($"the sanitizer rejected it as a {PayloadKind} payload"));
            }

            if (sanitized.Decision != SanitizationDecision.Allowed || sanitized.Fields is null)
            {
                throw new Refusal(ReflectionScreeningRefusal.SanitizerFailed, "the sanitizer returned a result that is neither an allowed payload nor a rejection");
            }

            if (sanitized.OmittedFieldPaths is { Count: > 0 })
            {
                // A field or an item the sanitizer dropped is never stored silently empty.
                throw new Refusal(ReflectionScreeningRefusal.FieldOmitted, "the sanitizer omitted a field or an item");
            }

            var fields = sanitized.Fields;
            var lesson = TextFrom(fields, nameof(Reflection.Lesson), sentNull: false);
            var guidance = TextFrom(fields, nameof(Reflection.ReuseGuidance), sentNull: first.ReuseGuidance is null);

            var lists = new List<string?>[ListFieldNames.Length];
            var collapsed = new bool[ListFieldNames.Length];
            for (var list = 0; list < lists.Length; list++)
            {
                (lists[list], collapsed[list]) = ListFrom(fields, ListFieldNames[list], limits);
            }

            var second = Hygiene(lesson, lists, guidance, first.SourceIndexes, limits, afterSanitizer: true);

            // Layer 3, for model-authored text only: the content guard, over exactly what would be stored. Fail
            // closed: anything that is not Deterministic counts as model-authored, as does a reflection the library's
            // own model-backed reflector produced (ReflectionAuthorshipRule), judged on the cleaned producer that will
            // be stored, so what the guard sees is what every later reader decides on. With no run content, every URL,
            // hostname and IP address is refused.
            if (ReflectionAuthorshipRule.IsModelAuthored(reflection with { Producer = producer }))
            {
                GuardModelAuthored(second, ModelAuthoredContentGuard.LinksOf(runContent));
            }

            var screened = reflection with
            {
                Lesson = second.Lesson,
                SuccessfulApproaches = second.Lists[0],
                FailedApproaches = second.Lists[1],
                Preconditions = second.Lists[2],
                Warnings = second.Lists[3],
                ReuseGuidance = second.ReuseGuidance,
                Producer = producer,
            };

            return new ReflectionScreeningResult(
                screened,
                Refusal: null,
                RefusalReason: null,
                ExceptionType: null,
                RedactedPaths(sanitized.RedactedFieldPaths, second, collapsed));
        }
        catch (Refusal refusal)
        {
            return refusal.ToResult();
        }
    }

    /// <summary>
    /// <paramref name="value"/> with every invisible character removed, except that a whitespace one becomes
    /// a space. A string that holds none of them is returned as it is, the same instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Invisible, classified per Unicode scalar so a TAG character or any other supplementary-plane one is
    /// seen for what it is: a control, format, private-use or unassigned code point; a lone surrogate; a
    /// variation selector (U+FE00-FE0F, U+E0100-E01EF), the combining grapheme joiner (U+034F), a Hangul
    /// filler (U+115F, U+1160, U+3164, U+FFA0) or the blank braille pattern (U+2800), which render as nothing
    /// or as blank space. The line and paragraph separators (U+2028, U+2029) become spaces. A run of more than
    /// <see cref="MaxConsecutiveCombiningMarks"/> combining marks is cut to that many, so stacked marks
    /// cannot bury the text around them.
    /// </para>
    /// <para>
    /// The same rule the Historical Reference writer applies to a tool name (story 8.2), and a cross-check
    /// test holds the two to it code point by code point. It is reimplemented here rather than shared
    /// because Core grants its internals to no other assembly.
    /// </para>
    /// </remarks>
    internal static string Neutralize(string value)
    {
        StringBuilder? mapped = null;
        var marks = 0;
        var index = 0;
        while (index < value.Length)
        {
            var start = index;
            char? replacement;
            var isMark = false;
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
            {
                // A lone surrogate: not a character.
                replacement = Removed;
                index += Math.Max(consumed, 1);
            }
            else
            {
                index += consumed;
                var category = Rune.GetUnicodeCategory(rune);
                if (category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    replacement = ' ';
                }
                else if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate || IsBlankIgnorable(rune.Value))
                {
                    replacement = Rune.IsWhiteSpace(rune) ? ' ' : Removed;
                }
                else if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                {
                    isMark = marks < MaxConsecutiveCombiningMarks;
                    replacement = isMark ? null : Removed;
                }
                else
                {
                    replacement = null;
                }
            }

            // A removed character is invisible, so it neither ends nor extends a run of marks.
            if (replacement != Removed)
            {
                marks = isMark ? marks + 1 : 0;
            }

            if (replacement is { } character)
            {
                mapped ??= new StringBuilder(value.Length).Append(value, 0, start);
                if (character != Removed)
                {
                    mapped.Append(character);
                }
            }
            else
            {
                mapped?.Append(value, start, index - start);
            }
        }

        return mapped?.ToString() ?? value;
    }

    /// <summary>
    /// Refuses, as <see cref="ReflectionScreeningRefusal.UnsafeContent"/>, the first screened field that breaks
    /// a <see cref="ModelAuthoredContentGuard"/> rule, naming the field (a list item by the reflector's own
    /// index) and the rule, never the text.
    /// </summary>
    private static void GuardModelAuthored(Hygienic screened, Links runContent)
    {
        void Check(string? text, string field)
        {
            if (text is not null && ModelAuthoredContentGuard.Check(text, runContent) is { } rule)
            {
                throw new Refusal(ReflectionScreeningRefusal.UnsafeContent, Invariant($"its {field} contains {rule}"));
            }
        }

        Check(screened.Lesson, nameof(Reflection.Lesson));
        for (var list = 0; list < screened.Lists.Length; list++)
        {
            for (var item = 0; item < screened.Lists[list].Length; item++)
            {
                Check(screened.Lists[list][item], Invariant($"{ListFieldNames[list]}[{screened.SourceIndexes[list][item]}]"));
            }
        }

        Check(screened.ReuseGuidance, nameof(Reflection.ReuseGuidance));

        // A phrase split across fields or items is caught on all of them joined.
        if (ModelAuthoredContentGuard.CheckCombined([screened.Lesson, .. screened.Lists.SelectMany(list => list), screened.ReuseGuidance]) is { } combined)
        {
            throw new Refusal(ReflectionScreeningRefusal.UnsafeContent, Invariant($"the text across its fields contains {combined}"));
        }
    }

    /// <summary>Code points that are letters, marks or symbols by category but render as nothing or as blank space.</summary>
    private static bool IsBlankIgnorable(int value) =>
        value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) or 0x034F or 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800;

    private static IReadOnlyList<string>? ListOf(Reflection reflection, int list) => list switch
    {
        0 => reflection.SuccessfulApproaches,
        1 => reflection.FailedApproaches,
        2 => reflection.Preconditions,
        _ => reflection.Warnings,
    };

    /// <summary>
    /// Neutralizes every field and holds it to its limit. An absent lesson refuses; an absent list item is
    /// dropped; absent reuse guidance becomes <see langword="null"/>. An over-limit item is named by its index
    /// in the reflector's own list, through <paramref name="sourceIndexes"/> when this is the second pass.
    /// </summary>
    private static Hygienic Hygiene(
        string? lesson,
        List<string?>[] lists,
        string? reuseGuidance,
        int[][]? sourceIndexes,
        ReflectionLimits limits,
        bool afterSanitizer)
    {
        var cleanLesson = Clean(lesson)
            ?? throw new Refusal(
                afterSanitizer ? ReflectionScreeningRefusal.FieldOmitted : ReflectionScreeningRefusal.MissingLesson,
                afterSanitizer
                    ? "the sanitizer left it without a lesson"
                    : "its Lesson is missing, or empty once invisible characters are removed");

        EnsureLength(cleanLesson, limits.MaxLessonLength, nameof(Reflection.Lesson));

        var cleanGuidance = Clean(reuseGuidance);
        if (cleanGuidance is not null)
        {
            EnsureLength(cleanGuidance, limits.MaxLessonLength, nameof(Reflection.ReuseGuidance));
        }

        var cleanLists = new string[lists.Length][];
        var kept = new int[lists.Length][];
        var passIndexes = new int[lists.Length][];
        for (var list = 0; list < lists.Length; list++)
        {
            var texts = new List<string>(lists[list].Count);
            var from = new List<int>(lists[list].Count);
            for (var item = 0; item < lists[list].Count; item++)
            {
                if (Clean(lists[list][item]) is { } text)
                {
                    var original = sourceIndexes is null
                        ? item
                        : item < sourceIndexes[list].Length ? sourceIndexes[list][item] : item;
                    EnsureLength(text, limits.MaxListItemLength, Invariant($"{ListFieldNames[list]}[{original}]"));
                    texts.Add(text);
                    from.Add(item);
                }
            }

            cleanLists[list] = [.. texts];
            passIndexes[list] = [.. from];

            // Where each kept item came from, as an index into the reflector's own list.
            kept[list] = sourceIndexes is null
                ? [.. from]
                : [.. from.Select(item => item < sourceIndexes[list].Length ? sourceIndexes[list][item] : item)];
        }

        return new Hygienic(cleanLesson, cleanLists, cleanGuidance, kept, passIndexes);
    }

    /// <summary>The neutralized text, or <see langword="null"/> when it is absent: null, empty, or blank.</summary>
    private static string? Clean(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var neutralized = Neutralize(text);
        return string.IsNullOrWhiteSpace(neutralized) ? null : neutralized;
    }

    private static void EnsureLength(string text, int limit, string field)
    {
        if (text.Length > limit)
        {
            throw new Refusal(ReflectionScreeningRefusal.OverLimit, Invariant($"its {field} is longer than the {limit}-character limit"));
        }
    }

    /// <summary>
    /// A reflector's list, copied once and never read again, so a host list that changes after it was
    /// checked cannot put unchecked text on the record. Counted as it is copied, so an unbounded list is
    /// refused at the first item past the limit.
    /// </summary>
    private static List<string?> Snapshot(IReadOnlyList<string>? list, string field, ReflectionLimits limits)
    {
        if (list is null)
        {
            throw new Refusal(ReflectionScreeningRefusal.MissingField, Invariant($"its {field} is missing"));
        }

        var copy = new List<string?>();
        try
        {
            foreach (var item in list)
            {
                if (copy.Count == limits.MaxListItems)
                {
                    throw new Refusal(ReflectionScreeningRefusal.OverLimit, Invariant($"its {field} holds more items than the {limits.MaxListItems}-item limit"));
                }

                copy.Add(item);
            }
        }
        catch (Exception ex) when (ex is not Refusal and not OperationCanceledException)
        {
            throw new Refusal(ReflectionScreeningRefusal.Unreadable, Invariant($"reading its {field} threw"), ex.GetType().FullName);
        }

        return copy;
    }

    /// <summary>
    /// A single-text field as the sanitizer returned it. Missing, or <see langword="null"/> where the screening
    /// sent text, is an omission.
    /// </summary>
    private static string? TextFrom(IReadOnlyDictionary<string, object?> fields, string field, bool sentNull)
    {
        if (!fields.TryGetValue(field, out var value))
        {
            throw new Refusal(ReflectionScreeningRefusal.FieldOmitted, Invariant($"the sanitizer omitted its {field}"));
        }

        return value switch
        {
            null when sentNull => null,
            null => throw new Refusal(ReflectionScreeningRefusal.FieldOmitted, Invariant($"the sanitizer omitted its {field}")),
            string text => text,
            _ => throw new Refusal(ReflectionScreeningRefusal.SanitizerFailed, Invariant($"the sanitizer returned its {field} as something other than text")),
        };
    }

    /// <summary>
    /// A list field as the sanitizer returned it: a list of strings, or a single string (which is what a
    /// whole-value redaction leaves, <c>collapsed</c>). Missing, <see langword="null"/>, or holding a
    /// <see langword="null"/> item is an omission.
    /// </summary>
    private static (List<string?> Items, bool Collapsed) ListFrom(IReadOnlyDictionary<string, object?> fields, string field, ReflectionLimits limits)
    {
        if (!fields.TryGetValue(field, out var value) || value is null)
        {
            throw new Refusal(ReflectionScreeningRefusal.FieldOmitted, Invariant($"the sanitizer omitted its {field}"));
        }

        if (value is string text)
        {
            return ([text], true);
        }

        if (value is not IEnumerable items)
        {
            throw new Refusal(ReflectionScreeningRefusal.SanitizerFailed, Invariant($"the sanitizer returned its {field} as something other than a list of text"));
        }

        var copy = new List<string?>();
        foreach (var item in items)
        {
            if (copy.Count == limits.MaxListItems)
            {
                throw new Refusal(ReflectionScreeningRefusal.OverLimit, Invariant($"the sanitizer returned more items for its {field} than the {limits.MaxListItems}-item limit"));
            }

            copy.Add(item switch
            {
                null => throw new Refusal(ReflectionScreeningRefusal.FieldOmitted, Invariant($"the sanitizer omitted an item of its {field}")),
                string itemText => itemText,
                _ => throw new Refusal(ReflectionScreeningRefusal.SanitizerFailed, Invariant($"the sanitizer returned an item of its {field} as something other than text")),
            });
        }

        return (copy, false);
    }

    /// <summary>
    /// The sanitizer's redacted paths, as paths into what is stored, distinct and in order: a screened field
    /// (<c>Lesson</c>), or an item of a list (<c>Warnings[2]</c>) re-indexed into the stored list. An item that
    /// screening then dropped as blank, a field left absent, and anything that is not one of these shapes are
    /// dropped rather than reported: a path is the host sanitizer's text, and one that is not a screened
    /// field could carry what it redacted.
    /// </summary>
    private static string[] RedactedPaths(IReadOnlyList<string>? paths, Hygienic stored, bool[] collapsed)
    {
        if (paths is null || paths.Count == 0)
        {
            return [];
        }

        var kept = new List<string>();
        foreach (var path in paths)
        {
            if (path is not null && Remap(path, stored, collapsed) is { } remapped && !kept.Contains(remapped, StringComparer.Ordinal))
            {
                kept.Add(remapped);
            }
        }

        return [.. kept];
    }

    private static string? Remap(string path, Hygienic stored, bool[] collapsed)
    {
        var bracket = path.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
        {
            return path switch
            {
                nameof(Reflection.ReuseGuidance) => stored.ReuseGuidance is null ? null : path,
                _ => ScreenedFieldNames.Contains(path, StringComparer.Ordinal) ? path : null,
            };
        }

        var field = path[..bracket];
        if (!ListFieldSet.Contains(field) || path[^1] != ']')
        {
            return null;
        }

        var digits = path.AsSpan(bracket + 1, path.Length - bracket - 2);
        if (digits.IsEmpty || digits.Length > 9 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var returnedIndex))
        {
            return null;
        }

        var list = Array.IndexOf(ListFieldNames, field);
        if (collapsed[list])
        {
            // The list came back as one whole-value redaction; its item paths name nothing stored.
            return null;
        }

        var storedIndex = Array.IndexOf(stored.PassIndexes[list], returnedIndex);
        return storedIndex < 0 ? null : Invariant($"{field}[{storedIndex}]");
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>The six screened fields, neutralized and bounded, with where each kept item came from.</summary>
    /// <param name="Lesson">The lesson.</param>
    /// <param name="Lists">The four lists, in <see cref="ListFieldNames"/> order.</param>
    /// <param name="ReuseGuidance">The reuse guidance, or <see langword="null"/>.</param>
    /// <param name="SourceIndexes">For each list, each kept item's index in the reflector's own list.</param>
    /// <param name="PassIndexes">For each list, each kept item's index in the list this pass was handed.</param>
    private sealed record Hygienic(string Lesson, string[][] Lists, string? ReuseGuidance, int[][] SourceIndexes, int[][] PassIndexes)
    {
        internal Dictionary<string, object?> ToPayloadFields() => new(StringComparer.Ordinal)
        {
            [nameof(Reflection.Lesson)] = Lesson,
            [nameof(Reflection.SuccessfulApproaches)] = Lists[0],
            [nameof(Reflection.FailedApproaches)] = Lists[1],
            [nameof(Reflection.Preconditions)] = Lists[2],
            [nameof(Reflection.Warnings)] = Lists[3],
            [nameof(Reflection.ReuseGuidance)] = ReuseGuidance,
        };
    }

    /// <summary>A refusal on its way out of the screening; its message is the content-free detail.</summary>
    private sealed class Refusal(ReflectionScreeningRefusal code, string detail, string? exceptionType = null) : Exception(detail)
    {
        internal ReflectionScreeningResult ToResult() => ReflectionScreeningResult.Refused(code, Message, exceptionType);
    }
}

/// <summary>What screening one reflection reached: the screened reflection and its redacted paths, or a content-free refusal.</summary>
/// <param name="Reflection">The screened reflection, or <see langword="null"/> when it was refused.</param>
/// <param name="Refusal">Why it was refused, as a code; <see langword="null"/> when it was not.</param>
/// <param name="RefusalReason">Why it was refused, naming fields and limits only; <see langword="null"/> when it was not.</param>
/// <param name="ExceptionType">The type of the exception behind the refusal, when there was one.</param>
/// <param name="RedactedFieldPaths">The paths of the stored fields the host sanitizer redacted.</param>
internal sealed record ReflectionScreeningResult(
    Reflection? Reflection,
    ReflectionScreeningRefusal? Refusal,
    string? RefusalReason,
    string? ExceptionType,
    IReadOnlyList<string> RedactedFieldPaths)
{
    internal static ReflectionScreeningResult Refused(ReflectionScreeningRefusal refusal, string reason, string? exceptionType) =>
        new(Reflection: null, refusal, reason, exceptionType, RedactedFieldPaths: []);
}
