using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// How <see cref="HistoricalReferenceWriter.Write(IReadOnlyList{RankedExperience}, ExperienceInjectionLimits, IEnumerable{KeyValuePair{string, IReadOnlyList{string}}}, HistoricalReferenceWriteSettings)"/>
/// renders a block. Its defaults are <see cref="ExperienceInjectionOptions"/>' defaults, so with
/// <see cref="IsContentConfirmed"/> set to the retrieval service's <see cref="ExperienceRetrievalService.IsContentConfirmed"/>
/// and the provider's own <see cref="ExperienceInjectionOptions.Rendering"/> and
/// <see cref="ExperienceInjectionOptions.FailureDetail"/>, the block is exactly what <see cref="ExperienceContextProvider"/> injects
/// for the same records (without session tracking's withdrawal notices).
/// </summary>
public sealed record HistoricalReferenceWriteSettings
{
    /// <summary>The layout; see <see cref="ExperienceInjectionOptions.Rendering"/>. Defaults to <see cref="HistoricalReferenceRendering.Compact"/>.</summary>
    public HistoricalReferenceRendering Rendering { get; init; } = HistoricalReferenceRendering.Compact;

    /// <summary>How much a <c>Tried:</c> line says about a failed attempt; see <see cref="ExperienceInjectionOptions.FailureDetail"/>. Defaults to <see cref="AttemptFailureDetail.ErrorClass"/>.</summary>
    public AttemptFailureDetail FailureDetail { get; init; } = AttemptFailureDetail.ErrorClass;

    /// <summary>
    /// Whether a record's content is confirmed (story 17.2); pass <see cref="ExperienceRetrievalService.IsContentConfirmed"/>
    /// to fence as the provider does. <see langword="null"/> (the default) confirms every record, deciding authorship on
    /// the reflection alone.
    /// </summary>
    public Func<ExperienceRecord, bool>? IsContentConfirmed { get; init; }
}
