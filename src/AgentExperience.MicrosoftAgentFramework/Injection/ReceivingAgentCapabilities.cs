using AgentExperience.Abstractions;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// What the host declares about the agent receiving the Historical Reference, so that a record whose
/// verified approach that agent cannot, or must not, carry out is not injected into it. Set on
/// <see cref="ExperienceInjectionOptions.ReceivingAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is checked.</b> Exactly the tool names a record's <c>Approach:</c> line would carry: the
/// verified final attempt's calls, cut to <see cref="HistoricalReferenceWriter.MaxApproachToolNames"/>. A
/// record with no approach (not verified, quarantined, or with no unambiguous error-free final attempt)
/// has nothing to check and passes, and so does a borrowed record whose grant withholds the line, so the
/// gate cannot be used to probe a lender's tool names. Tool names compare ordinally, as recorded -- not as
/// the block renders them.
/// </para>
/// <para>
/// <b>In order.</b> A record fails the tool check when any approach tool is not in
/// <see cref="AvailableTools"/> (<see cref="InjectionOmissionReason.ToolUnavailable"/>), and then the risk
/// check when any approach tool's class in <see cref="ToolRiskClasses"/> -- <see cref="ToolRiskClass.Critical"/>
/// for a tool not listed there -- is above <see cref="MaxRiskClass"/>
/// (<see cref="InjectionOmissionReason.RiskClassExceeded"/>). The first failure decides the reason. Either
/// check is skipped when its property is <see langword="null"/>: <see cref="ToolRiskClasses"/> has no effect
/// without <see cref="MaxRiskClass"/>, and a <see cref="MaxRiskClass"/> of <see cref="ToolRiskClass.Critical"/>
/// disables the risk check. A recorded tool with a <see langword="null"/> or blank name is always unavailable,
/// and <see cref="ToolRiskClass.Critical"/> for risk. Tool names that the lesson text mentions are not checked.
/// </para>
/// <para>
/// <b>Passing grants nothing.</b> The gate only keeps a record out of the block. Every tool call the agent
/// makes is still decided by the host's approval boundary: experience never transfers a capability.
/// </para>
/// </remarks>
public sealed record ReceivingAgentCapabilities
{
    /// <summary>
    /// The tools the receiving agent has, by name, compared ordinally. <see langword="null"/> (the default)
    /// skips the tool check. An empty set admits only records whose approach calls no tool.
    /// </summary>
    public IReadOnlySet<string>? AvailableTools { get; init; }

    /// <summary>
    /// The riskiest class of tool the receiving agent may be taught to use. <see langword="null"/> (the
    /// default) skips the risk check.
    /// </summary>
    public ToolRiskClass? MaxRiskClass { get; init; }

    /// <summary>
    /// The host's risk class for each tool, by name, compared ordinally. A tool missing from it counts as
    /// <see cref="ToolRiskClass.Critical"/>. Empty by default. Only read when <see cref="MaxRiskClass"/> is set.
    /// </summary>
    public IReadOnlyDictionary<string, ToolRiskClass> ToolRiskClasses { get; init; } = new Dictionary<string, ToolRiskClass>(StringComparer.Ordinal);
}

/// <summary>
/// <see cref="ReceivingAgentCapabilities"/> as <see cref="ExperienceContextProvider"/> snapshots it at
/// construction: copied into ordinal collections, so neither a host's own comparer nor a later edit to its
/// collections changes what the gate admits.
/// </summary>
internal sealed class CapabilityGate
{
    private readonly HashSet<string>? _availableTools;
    private readonly ToolRiskClass? _maxRiskClass;
    private readonly Dictionary<string, ToolRiskClass> _toolRiskClasses;

    private CapabilityGate(HashSet<string>? availableTools, ToolRiskClass? maxRiskClass, Dictionary<string, ToolRiskClass> toolRiskClasses)
    {
        _availableTools = availableTools;
        _maxRiskClass = maxRiskClass;
        _toolRiskClasses = toolRiskClasses;
    }

    /// <summary>Validates and snapshots the host's declaration, or returns <see langword="null"/> when there is none.</summary>
    /// <exception cref="ArgumentException">A tool name is <see langword="null"/>, empty or whitespace, or a risk class is not a defined <see cref="ToolRiskClass"/>.</exception>
    internal static CapabilityGate? Create(ReceivingAgentCapabilities? capabilities, string paramName)
    {
        if (capabilities is null)
        {
            return null;
        }

        HashSet<string>? available = null;
        if (capabilities.AvailableTools is { } tools)
        {
            available = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                if (string.IsNullOrWhiteSpace(tool))
                {
                    throw new ArgumentException("ReceivingAgent.AvailableTools may not contain a null, empty or whitespace tool name.", paramName);
                }

                available.Add(tool);
            }
        }

        if (capabilities.MaxRiskClass is { } max && !Enum.IsDefined(max))
        {
            throw new ArgumentException($"ReceivingAgent.MaxRiskClass {(int)max} is not a defined ToolRiskClass.", paramName);
        }

        var classes = new Dictionary<string, ToolRiskClass>(StringComparer.Ordinal);
        foreach (var (tool, riskClass) in capabilities.ToolRiskClasses ?? new Dictionary<string, ToolRiskClass>())
        {
            if (string.IsNullOrWhiteSpace(tool))
            {
                throw new ArgumentException("ReceivingAgent.ToolRiskClasses may not contain a null, empty or whitespace tool name.", paramName);
            }

            if (!Enum.IsDefined(riskClass))
            {
                throw new ArgumentException($"ReceivingAgent.ToolRiskClasses holds {(int)riskClass}, which is not a defined ToolRiskClass.", paramName);
            }

            classes[tool] = riskClass;
        }

        return new CapabilityGate(available, capabilities.MaxRiskClass, classes);
    }

    /// <summary>
    /// The reason a record whose approach makes these <paramref name="calls"/> is kept out of the block, or
    /// <see langword="null"/> when it passes. <see langword="null"/> or empty calls always pass.
    /// </summary>
    internal InjectionOmissionReason? Check(IReadOnlyList<ToolCallRecord>? calls)
    {
        if (calls is null or { Count: 0 })
        {
            return null;
        }

        if (_availableTools is { } available)
        {
            foreach (var call in calls)
            {
                // A null or blank recorded name names no tool the agent could have: always unavailable.
                if (string.IsNullOrWhiteSpace(call.ToolName) || !available.Contains(call.ToolName))
                {
                    return InjectionOmissionReason.ToolUnavailable;
                }
            }
        }

        if (_maxRiskClass is { } max)
        {
            foreach (var call in calls)
            {
                var riskClass = call.ToolName is { } name && _toolRiskClasses.TryGetValue(name, out var declared)
                    ? declared
                    : ToolRiskClass.Critical;
                if (riskClass > max)
                {
                    return InjectionOmissionReason.RiskClassExceeded;
                }
            }
        }

        return null;
    }
}
