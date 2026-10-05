using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;

namespace AgentExperience.Core.Reflections;

/// <summary>
/// The one Core entry point that decides whether a stored record's lesson counts as model-authored, given the
/// provenance signing configuration (story 17.2). Retrieval's exclusion re-check calls it, and injection reaches it
/// through <c>ExperienceRetrievalService.IsModelAuthored</c> and <c>ExperienceRetrievalService.IsContentConfirmed</c>.
/// </summary>
internal static class RecordAuthorship
{
    /// <summary>
    /// Whether <paramref name="record"/>'s content is confirmed: always without signing (<paramref name="signer"/>
    /// <see langword="null"/>); with it, only as <see cref="ProvenanceSigner.ConfirmsContent"/> says.
    /// </summary>
    internal static bool IsContentConfirmed(ExperienceRecord record, ProvenanceSigner? signer)
    {
        ArgumentNullException.ThrowIfNull(record);
        return signer is null || signer.ConfirmsContent(record);
    }

    /// <summary>
    /// Whether <paramref name="record"/> counts as model-authored: its reflection does by the shared rule
    /// (<see cref="ReflectionAuthorshipRule"/>), or its content is not confirmed (<see cref="IsContentConfirmed"/>).
    /// Without signing, a record with no reflection never counts.
    /// </summary>
    internal static bool IsModelAuthored(ExperienceRecord record, ProvenanceSigner? signer)
    {
        ArgumentNullException.ThrowIfNull(record);
        return ReflectionAuthorshipRule.IsModelAuthored(record.Reflection) || !IsContentConfirmed(record, signer);
    }

    /// <summary>
    /// Why <paramref name="record"/> is left out under <c>ExcludeModelAuthored</c>, or <see langword="null"/> when it
    /// is not: <see cref="Retrieval.RetrievalExclusionReason.ModelAuthored"/> when its reflection is model-authored by
    /// the shared rule, <see cref="Retrieval.RetrievalExclusionReason.UnconfirmedContent"/> when that is so only because
    /// its content is unconfirmed.
    /// </summary>
    internal static Retrieval.RetrievalExclusionReason? ExclusionReason(ExperienceRecord record, ProvenanceSigner? signer) =>
        ReflectionAuthorshipRule.IsModelAuthored(record.Reflection) ? Retrieval.RetrievalExclusionReason.ModelAuthored
        : !IsContentConfirmed(record, signer) ? Retrieval.RetrievalExclusionReason.UnconfirmedContent
        : null;
}
