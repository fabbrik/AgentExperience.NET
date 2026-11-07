using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;

namespace AgentExperience.MicrosoftAgentFramework.Export;

/// <summary>
/// One lesson the holdout measured as helping, as <see cref="AgentExperienceLessons.ExportMeasuredAsync"/> hands it to an
/// <see cref="IExperienceLessonSink"/>: what it says, where it came from, and what was measured. Nothing else of the
/// record (no raw tool arguments, results, errors or evidence detail) is carried.
/// </summary>
/// <param name="ExperienceId">The record's ID: the key a sink upserts and removes its copy by.</param>
/// <param name="Revision">The record's revision when it was exported; a sink can tell a changed lesson by it.</param>
/// <param name="TaskId">The task it was captured under. Untrusted text.</param>
/// <param name="LessonText">
/// The lesson as the Historical Reference block renders it, without the block's begin and end markers, the record's
/// delimiters or its ranking line: what was tried, what worked, and the reflection (lesson, reuse guidance,
/// preconditions, warnings), inside the model-authored fence when a model wrote it. Untrusted reference material: it was
/// drawn from captured runs, and whatever loads it must treat it as reference, never as instructions.
/// </param>
/// <param name="MeasuredBenefit">
/// The holdout's comparison for this lesson: treatment runs given it against holdout runs denied it, the difference and
/// its 95% interval. Its verdict is always <see cref="ExperienceBenefitVerdict.Helps"/>.
/// </param>
/// <param name="ExportedAt">When the set was read, by the setup's clock: the same instant for every lesson in one export.</param>
public sealed record ExportedLesson(
    Guid ExperienceId,
    long Revision,
    string TaskId,
    string LessonText,
    ExperienceBenefitComparison MeasuredBenefit,
    DateTimeOffset ExportedAt)
{
    /// <summary>The task it was captured under. Untrusted text.</summary>
    public string TaskId { get; init; } = TaskId ?? throw new ArgumentNullException(nameof(TaskId));

    /// <summary>The lesson's text (see the primary constructor's parameter doc). Untrusted reference material.</summary>
    public string LessonText { get; init; } = LessonText ?? throw new ArgumentNullException(nameof(LessonText));

    /// <summary>The holdout's comparison for this lesson.</summary>
    public ExperienceBenefitComparison MeasuredBenefit { get; init; } = MeasuredBenefit ?? throw new ArgumentNullException(nameof(MeasuredBenefit));
}

/// <summary>
/// Where <see cref="AgentExperienceLessons.ExportMeasuredAsync"/> sends the measured lessons: another memory store (Mem0,
/// Zep, Letta), an instruction file (<c>AGENTS.md</c>, <c>CLAUDE.md</c>), a skill folder. The host implements it;
/// <see cref="MarkdownLessonFileSink"/> is the built-in one.
/// </summary>
public interface IExperienceLessonSink
{
    /// <summary>
    /// Brings the sink's copy of <paramref name="scope"/>'s lessons in step with <paramref name="current"/>: adds the lessons
    /// it does not hold, updates those whose revision or text changed, and removes every copy whose lesson is not in
    /// <paramref name="current"/> (withdrawn, erased, no longer eligible, or no longer measured as helping). Called with the
    /// whole current set every time, an empty one included.
    /// </summary>
    /// <param name="scope">The scope the lessons were read in: the identity's own.</param>
    /// <param name="current">Every lesson that should now be in the sink, sorted by experience ID.</param>
    /// <param name="cancellationToken">Cancels the sync.</param>
    /// <returns>What changed. An exception propagates to the caller of the export.</returns>
    Task<ExperienceLessonSyncResult> SyncAsync(Scope scope, IReadOnlyList<ExportedLesson> current, CancellationToken cancellationToken);
}

/// <summary>What one <see cref="IExperienceLessonSink.SyncAsync"/> changed, as the sink reports it.</summary>
/// <param name="Added">Lessons the sink did not hold before.</param>
/// <param name="Updated">Lessons it held whose copy changed.</param>
/// <param name="Removed">Copies it removed because their lesson is no longer in the set.</param>
public sealed record ExperienceLessonSyncResult(int Added, int Updated, int Removed);

/// <summary>What <see cref="AgentExperienceLessons.ExportMeasuredAsync"/> did.</summary>
/// <param name="Scope">The scope exported: the identity's own.</param>
/// <param name="Outcome">
/// The store's outcome: <see cref="ExperienceStoreOutcome.Found"/>, or why nothing was read (the scope lies outside the
/// authorization, say), in which case the sink was not called.
/// </param>
/// <param name="Lessons">The set handed to the sink; empty when it was not called.</param>
/// <param name="Sync">What the sink reported; <see langword="null"/> when it was not called.</param>
/// <param name="ExportedAt">When the set was read, by the setup's clock.</param>
public sealed record ExperienceLessonExportResult(
    Scope Scope,
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExportedLesson> Lessons,
    ExperienceLessonSyncResult? Sync,
    DateTimeOffset ExportedAt)
{
    /// <summary>The scope exported.</summary>
    public Scope Scope { get; init; } = Scope ?? throw new ArgumentNullException(nameof(Scope));

    /// <summary>The set handed to the sink.</summary>
    public IReadOnlyList<ExportedLesson> Lessons { get; init; } = Lessons ?? throw new ArgumentNullException(nameof(Lessons));

    /// <summary>
    /// Whether the store records holdout assignments. When <see langword="false"/> nothing can be measured, so the set was
    /// empty and the sink removed every copy it held.
    /// </summary>
    public bool HoldoutTracked { get; init; }
}
