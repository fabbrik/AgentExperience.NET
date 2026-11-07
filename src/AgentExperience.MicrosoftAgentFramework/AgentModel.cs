using AgentExperience.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// The model an agent runs on, as capture records it (<see cref="HoldoutOutcomeRow.ModelMetadataKey"/>) and the benefit
/// policy reads it: the agent's chat client's <see cref="ChatClientMetadata.DefaultModelId"/>, found through the agent's
/// <see cref="AIAgent.GetService(Type, object?)"/>. Never inferred from text or response content.
/// </summary>
internal static class AgentModel
{
    /// <summary>The longest model id recorded; a longer one is cut to this many characters.</summary>
    internal const int MaxLength = 256;

    /// <summary>
    /// The model <paramref name="agent"/>'s chat client reports, trimmed and capped at <see cref="MaxLength"/>, or
    /// <see langword="null"/> when there is no agent, no metadata, no model id, or a blank one. Never throws.
    /// </summary>
    internal static string? Of(AIAgent? agent)
    {
        if (agent is null)
        {
            return null;
        }

        try
        {
            var metadata = agent.GetService<ChatClientMetadata>()
                ?? agent.GetService<IChatClient>()?.GetService<ChatClientMetadata>();
            return Normalize(metadata?.DefaultModelId);
        }
        catch (Exception)
        {
            // An agent whose service lookup throws reports no model.
            return null;
        }
    }

    /// <summary>A model id trimmed and capped at <see cref="MaxLength"/> (never splitting a surrogate pair); <see langword="null"/> when blank.</summary>
    internal static string? Normalize(string? modelId)
    {
        var trimmed = modelId?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length <= MaxLength)
        {
            return trimmed;
        }

        var cut = char.IsHighSurrogate(trimmed[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return trimmed[..cut].TrimEnd();
    }

    /// <summary>
    /// <paramref name="environment"/> with <paramref name="model"/> under <see cref="HoldoutOutcomeRow.ModelMetadataKey"/>;
    /// unchanged when there is no model, or when the host's environment already sets the key (the host's value wins).
    /// </summary>
    internal static EnvironmentFingerprint WithModel(EnvironmentFingerprint environment, string? model)
    {
        if (model is null || environment.Metadata is not { } metadata || metadata.ContainsKey(HoldoutOutcomeRow.ModelMetadataKey))
        {
            return environment;
        }

        var withModel = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            withModel[key] = value;
        }

        withModel[HoldoutOutcomeRow.ModelMetadataKey] = model;
        return environment with { Metadata = withModel };
    }

    /// <summary>
    /// The model an environment records: its <see cref="HoldoutOutcomeRow.ModelMetadataKey"/> entry, normalized as
    /// <see cref="Normalize"/> does; <see langword="null"/> when absent or blank.
    /// </summary>
    internal static string? Recorded(EnvironmentFingerprint? environment) =>
        environment?.Metadata is { } metadata && metadata.TryGetValue(HoldoutOutcomeRow.ModelMetadataKey, out var model)
            ? Normalize(model)
            : null;
}
