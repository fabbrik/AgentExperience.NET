namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// The chat role the injected Historical Reference message is sent in. See
/// <see cref="ExperienceInjectionOptions.MessageRole"/>.
/// </summary>
public enum HistoricalReferenceMessageRole
{
    /// <summary>A user-role message: reference material the model may read, not an instruction from the host. The default.</summary>
    User = 0,

    /// <summary>
    /// A system-role message. Some models weigh system text more heavily, so the block's untrusted framing then rests
    /// on the model honouring a system message that says its own content is untrusted. Choose it only when your model
    /// handles context better that way; the approval boundary around tools is the control either way. Some chat APIs
    /// reject a system message that is not first, hoist or merge system messages, or allow only one, so test it with
    /// your provider.
    /// </summary>
    System = 1,
}
