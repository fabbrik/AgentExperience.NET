namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// What <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> sees for one tool call that returned: the call, the
/// tool, the arguments capture recorded for it, and the raw value it returned. Nothing else -- in particular no model
/// text other than the tool's own result.
/// </summary>
/// <remarks>
/// The dictionary is read-only, but the values in it and <see cref="Result"/> are the live objects the tool and the
/// model work with: the classifier must not change them.
/// </remarks>
public sealed class ExperienceToolResultContext
{
    /// <summary>
    /// The identifier capture gave this tool call, the same one it records as <see cref="AgentExperience.Abstractions.ToolCallRecord.ToolCallId"/>. It
    /// tells concurrent calls of the same tool apart.
    /// </summary>
    public required Guid ToolCallId { get; init; }

    /// <summary>The name the tool is registered under, the same name capture records for the call.</summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// A read-only view of the arguments capture recorded for the call. They are the raw arguments, <em>before</em>
    /// sanitization (which runs when the attempt is stored), so secrets the model passed reach the classifier: never
    /// log or keep them.
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }

    /// <summary>
    /// The raw value the tool returned, before capture converts it to text: a <see cref="string"/>, a
    /// <see cref="System.Text.Json.JsonElement"/>, any other object, or <see langword="null"/>. It is the same object the
    /// model receives, so it must not be changed.
    /// </summary>
    public required object? Result { get; init; }
}
