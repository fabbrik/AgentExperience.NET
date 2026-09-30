namespace AgentExperience.Core.Reflections;

/// <summary>
/// The size limits finalization holds every <see cref="AgentExperience.Abstractions.Reflection"/> to
/// before the record that carries it is created, whichever <see cref="IExperienceReflector"/> wrote it.
/// A reflection over any limit is refused, never truncated: cutting a lesson short silently changes what
/// it says. Every limit must be strictly positive, checked at construction and on a <c>with</c>
/// expression alike.
/// </summary>
/// <remarks>
/// Lengths are counted in UTF-16 code units, after invisible characters were removed (see
/// <see cref="ReflectionScreening"/>). List counts are counted as the reflector returned them, before any
/// empty item is dropped, so an unbounded list is refused without being walked to its end.
/// </remarks>
/// <param name="MaxLessonLength">The longest <see cref="AgentExperience.Abstractions.Reflection.Lesson"/>, and the longest <see cref="AgentExperience.Abstractions.Reflection.ReuseGuidance"/>: the two single-text fields. Defaults to 4,000.</param>
/// <param name="MaxListItemLength">The longest item of any of the four lists (successful and failed approaches, preconditions, warnings). Defaults to 1,000.</param>
/// <param name="MaxListItems">The most items any one of those lists may hold. Defaults to 32.</param>
/// <param name="MaxProducerLength">The longest <see cref="AgentExperience.Abstractions.Reflection.Producer"/>. Screened like the free text: invisible characters are removed, and a producer left empty refuses the reflection. Defaults to 200.</param>
public sealed record ReflectionLimits(
    int MaxLessonLength = ReflectionLimits.DefaultMaxLessonLength,
    int MaxListItemLength = ReflectionLimits.DefaultMaxListItemLength,
    int MaxListItems = ReflectionLimits.DefaultMaxListItems,
    int MaxProducerLength = ReflectionLimits.DefaultMaxProducerLength)
{
    /// <summary>The default <see cref="MaxLessonLength"/>.</summary>
    public const int DefaultMaxLessonLength = 4_000;

    /// <summary>The default <see cref="MaxListItemLength"/>.</summary>
    public const int DefaultMaxListItemLength = 1_000;

    /// <summary>The default <see cref="MaxListItems"/>.</summary>
    public const int DefaultMaxListItems = 32;

    /// <summary>The default <see cref="MaxProducerLength"/>.</summary>
    public const int DefaultMaxProducerLength = 200;

    /// <summary>The documented defaults: 4,000 characters of lesson, 1,000 per list item, 32 items per list, 200 characters of producer, and five seconds for the sanitizer.</summary>
    public static ReflectionLimits Default { get; } = new();

    /// <summary>The longest lesson, and the longest reuse guidance (see the primary constructor's parameter doc).</summary>
    public int MaxLessonLength
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxLessonLength));
    } = EnsurePositive(MaxLessonLength, nameof(MaxLessonLength));

    /// <summary>The longest list item (see the primary constructor's parameter doc).</summary>
    public int MaxListItemLength
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxListItemLength));
    } = EnsurePositive(MaxListItemLength, nameof(MaxListItemLength));

    /// <summary>The most items per list (see the primary constructor's parameter doc).</summary>
    public int MaxListItems
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxListItems));
    } = EnsurePositive(MaxListItems, nameof(MaxListItems));

    /// <summary>The longest producer identity (see the primary constructor's parameter doc).</summary>
    public int MaxProducerLength
    {
        get;
        init => field = EnsurePositive(value, nameof(MaxProducerLength));
    } = EnsurePositive(MaxProducerLength, nameof(MaxProducerLength));

    /// <summary>The default <see cref="SanitizerTimeout"/>: five seconds.</summary>
    public static readonly TimeSpan DefaultSanitizerTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the host sanitizer may take to screen one reflection. A sanitizer that has not answered by
    /// then -- or that cancels on its own while the caller has not -- refuses the reflection, so the record
    /// is quarantined rather than finalization hanging on it. The caller's own cancellation still
    /// propagates. Must be strictly positive and finite. Defaults to <see cref="DefaultSanitizerTimeout"/>.
    /// </summary>
    public TimeSpan SanitizerTimeout
    {
        get;
        init => field = value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SanitizerTimeout), value, "The sanitizer timeout must be strictly positive and at most int.MaxValue milliseconds.");
    } = TimeSpan.FromSeconds(5); // DefaultSanitizerTimeout, spelled out: static initializers run in textual order.

    private static int EnsurePositive(int value, string name) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(name, value, "A reflection limit must be strictly positive.");
}
