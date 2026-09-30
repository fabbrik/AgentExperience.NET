using System.Text.Json.Serialization;

namespace AgentExperience.MicrosoftAgentFramework.Reflections;

/// <summary>
/// The fixed shape <see cref="ChatClientExperienceReflector"/> asks the model for: the free-text fields
/// of a reflection and nothing else. No identity, verdict, score, rule version, evidence, producer or
/// timestamp is part of it, so the model has no field through which to set one; a member the model adds
/// anyway is ignored when the response is read.
/// </summary>
internal sealed class ReflectionModelOutput
{
    /// <summary>The lesson for a future agent. Required and non-empty.</summary>
    [JsonPropertyName("lesson")]
    public string? Lesson { get; set; }

    /// <summary>Approaches that worked. Kept only when the run was verified.</summary>
    [JsonPropertyName("successfulApproaches")]
    public List<string?>? SuccessfulApproaches { get; set; }

    /// <summary>Approaches that did not work.</summary>
    [JsonPropertyName("failedApproaches")]
    public List<string?>? FailedApproaches { get; set; }

    /// <summary>What must hold before the lesson applies.</summary>
    [JsonPropertyName("preconditions")]
    public List<string?>? Preconditions { get; set; }

    /// <summary>Cautions for a future agent.</summary>
    [JsonPropertyName("warnings")]
    public List<string?>? Warnings { get; set; }

    /// <summary>How to reuse the lesson, or <see langword="null"/>.</summary>
    [JsonPropertyName("reuseGuidance")]
    public string? ReuseGuidance { get; set; }
}

/// <summary>
/// Source-generated metadata for <see cref="ReflectionModelOutput"/>: the schema the model is given and
/// the reader of its answer use the same contract, with no reflection-based serialization. Property
/// names are matched exactly, members the type does not declare are skipped, and the JSON is read strictly:
/// a duplicated property, a comment or a trailing comma makes the answer unparseable.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
    AllowTrailingCommas = false,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ReflectionModelOutput))]
internal sealed partial class ReflectionModelOutputContext : JsonSerializerContext;
