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
    /// Whether a record's content is confirmed; pass <see cref="ExperienceRetrievalService.IsContentConfirmed"/>
    /// to fence as the provider does. <see langword="null"/> (the default) confirms every record, deciding authorship on
    /// the reflection alone.
    /// </summary>
    public Func<ExperienceRecord, bool>? IsContentConfirmed { get; init; }

    /// <summary>
    /// The tool names whose recipe excerpts the <c>Worked:</c> attempt's line may show; see
    /// <see cref="ExperienceInjectionOptions.RecipeExcerpts"/>. <see langword="null"/> (the default) or empty shows none.
    /// </summary>
    public IEnumerable<string>? RecipeExcerpts { get; init; }

    /// <summary>
    /// Whether a record's recipe excerpts are confirmed; pass <see cref="ExperienceRetrievalService.AreRecipeExcerptsConfirmed"/>
    /// to show them as the provider does. <see langword="null"/> (the default) confirms every record's.
    /// </summary>
    public Func<ExperienceRecord, bool>? AreRecipeExcerptsConfirmed { get; init; }
}

/// <summary>
/// The recipe a block renders with: the reader's validated <see cref="ExperienceInjectionOptions.RecipeExcerpts"/>, and
/// whether a record's excerpts are confirmed (<see langword="null"/> confirms every record's).
/// </summary>
internal sealed record RecipeRendering(RecipeExcerptAllowlist Allowlist, Func<ExperienceRecord, bool>? AreConfirmed)
{
    /// <summary>No excerpt is shown.</summary>
    internal static RecipeRendering None { get; } = new(RecipeExcerptAllowlist.Empty, null);

    /// <summary>
    /// The tools whose excerpts <paramref name="ranked"/>'s <c>Worked:</c> line may show: none when its excerpts are
    /// unconfirmed; for a borrowed record, only those its grant also names, under a level that shows the approach.
    /// </summary>
    internal RecipeExcerptAllowlist For(RankedExperience ranked)
    {
        if (Allowlist.IsEmpty || (AreConfirmed is { } confirmed && !confirmed(ranked.Record)))
        {
            return RecipeExcerptAllowlist.Empty;
        }

        return !ranked.SharedByGrant
            ? Allowlist
            : HistoricalReferenceWriter.ShowsApproach(ranked.GrantDisclosure)
                ? Allowlist.IntersectWithGrant(ranked.GrantRecipeExcerpts)
                : RecipeExcerptAllowlist.Empty;
    }
}
