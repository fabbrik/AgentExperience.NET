using AgentExperience.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// What the host declares about the agent receiving the Historical Reference, so that a record whose
/// verified approach that agent cannot, or must not, carry out is not injected into it. Set on
/// <see cref="ExperienceInjectionOptions.ReceivingAgent"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is checked.</b> Exactly the tool names of the attempt a record's <c>Worked:</c> line names: the
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
/// <b>Before the record limit.</b> The gate runs on the ranked candidates before
/// <see cref="ExperienceInjectionLimits.MaxRecords"/> is taken, so a gated record takes no slot and a lower-ranked
/// record the agent can act on fills it. While a gate is configured, a request that names its own
/// <see cref="AgentExperience.Core.Retrieval.RetrieveExperienceRequest.Limit"/> asks retrieval for at least
/// <see cref="ExperienceInjectionLimits.MaxRecords"/> times <see cref="GatedRetrievalWindowMultiplier"/> records,
/// capped by the retrieval policy's candidate limit, so there is something to backfill from. The record as the final
/// eligibility re-read returns it is checked again. A borrowed record is decided only there, because a ranked
/// candidate does not carry its grant's disclosure level: before the limit it counts as withholding its approach.
/// The decision on the ranked revision is final: a record gated there stays out even if a newer revision would pass.
/// A record that passes there but fails on its re-read leaves its slot empty; nothing backfills it. And a gated record
/// is reported as <see cref="InjectionOmissionReason.ToolUnavailable"/> or
/// <see cref="InjectionOmissionReason.RiskClassExceeded"/> even where it also lies past the session's record budget,
/// rather than <see cref="InjectionOmissionReason.OverSessionBudget"/>.
/// </para>
/// <para>
/// <b>Run tools.</b> With <see cref="UseRunTools"/>, the tool check reads the tools MAF hands this invocation
/// instead of (or, with <see cref="AvailableTools"/> also set, intersected with) a static list.
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
    /// How many times <see cref="ExperienceInjectionLimits.MaxRecords"/> records a gated provider asks retrieval for
    /// when the resolved request names its own <see cref="AgentExperience.Core.Retrieval.RetrieveExperienceRequest.Limit"/>,
    /// capped by the retrieval policy's candidate limit. 4 by default; at least 1, or the provider refuses it at
    /// construction. A request with no limit already gets the whole candidate window, a limit already larger is kept,
    /// and a gate that can reject nothing widens nothing. Retrieval then ranks up to this many times more records, so its
    /// cost and latency can grow by up to this factor while a gate is active. The host's own limit still caps what is
    /// injected, and a record fetched only as backfill and not injected is not reported.
    /// </summary>
    public int GatedRetrievalWindowMultiplier { get; init; } = 4;

    /// <summary>
    /// <see langword="true"/> to check approach tools against the tools of the invocation itself: the names of
    /// <see cref="AIContext.Tools"/> as MAF hands them to the provider (a <see cref="ChatClientAgent"/>'s default
    /// tools and the run's own <see cref="ChatClientAgentRunOptions"/> tools), intersected with
    /// <see cref="AvailableTools"/> when that is set too. <see langword="false"/> (the default) uses
    /// <see cref="AvailableTools"/> alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The set is taken once per invocation, compared ordinally, and a tool with a <see langword="null"/> or blank name
    /// is ignored. An invocation with no tools has an empty set: only records whose approach calls no tool, or that have
    /// no approach, pass.
    /// </para>
    /// <para>
    /// <b>Which tools are seen.</b> Exactly <see cref="AIContext.Tools"/> as MAF hands it to this provider. Tools that a
    /// context provider running after this one adds, that function-invocation middleware adds
    /// (<see cref="FunctionInvokingChatClient.AdditionalTools"/>), or that run server-side (hosted tools) are not seen,
    /// and records whose approach calls them are withheld. Register this provider after any provider that adds tools.
    /// </para>
    /// <para>
    /// <b>The trade-off.</b> The names compared are the ones the recorded run called and the ones this run offers. A
    /// tool exposed under another name here -- an MCP server's prefix, a rename, a wrapper -- does not match, and the
    /// record is withheld without anything in the block saying so. The result's
    /// <see cref="InjectionOmissionReason.ToolUnavailable"/> omissions are where that shows.
    /// </para>
    /// </remarks>
    public bool UseRunTools { get; init; }

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
    private readonly bool _useRunTools;

    private CapabilityGate(HashSet<string>? availableTools, ToolRiskClass? maxRiskClass, Dictionary<string, ToolRiskClass> toolRiskClasses, bool useRunTools, int windowMultiplier)
    {
        _availableTools = availableTools;
        _maxRiskClass = maxRiskClass;
        _toolRiskClasses = toolRiskClasses;
        _useRunTools = useRunTools;
        WindowMultiplier = windowMultiplier;
    }

    /// <summary>The snapshotted <see cref="ReceivingAgentCapabilities.GatedRetrievalWindowMultiplier"/>.</summary>
    internal int WindowMultiplier { get; }

    /// <summary>
    /// Whether this gate can keep any record out: it has a tool check (declared or from the run's tools), or a risk
    /// check below <see cref="ToolRiskClass.Critical"/>. One that cannot is no gate at all.
    /// </summary>
    internal bool CanReject => _availableTools is not null || _useRunTools || _maxRiskClass is < ToolRiskClass.Critical;

    /// <summary>
    /// The gate for one invocation: this one, unless <see cref="ReceivingAgentCapabilities.UseRunTools"/> is set, in
    /// which case the available set is the names of <paramref name="runTools"/> (blank names ignored), intersected with
    /// the declared <see cref="ReceivingAgentCapabilities.AvailableTools"/> when there is one. Taken once, so a later
    /// change to the invocation's tools changes nothing. A sequence that cannot be read counts as no tools.
    /// </summary>
    internal CapabilityGate ForInvocation(IEnumerable<AITool>? runTools)
    {
        if (!_useRunTools)
        {
            return this;
        }

        var available = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var tool in runTools ?? [])
            {
                if (tool?.Name is { } name && !string.IsNullOrWhiteSpace(name)
                    && (_availableTools is null || _availableTools.Contains(name)))
                {
                    available.Add(name);
                }
            }
        }
#pragma warning disable CA1031 // A tool list that throws while read offers no tool; fail closed, never into the invocation.
        catch (Exception)
#pragma warning restore CA1031
        {
            available.Clear();
        }

        return new CapabilityGate(available, _maxRiskClass, _toolRiskClasses, useRunTools: false, WindowMultiplier);
    }

    /// <summary>Validates and snapshots the host's declaration, or returns <see langword="null"/> when there is none.</summary>
    /// <exception cref="ArgumentException">A tool name is <see langword="null"/>, empty or whitespace, or a risk class is not a defined <see cref="ToolRiskClass"/>, or <see cref="ReceivingAgentCapabilities.GatedRetrievalWindowMultiplier"/> is below 1.</exception>
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

        if (capabilities.GatedRetrievalWindowMultiplier < 1)
        {
            throw new ArgumentException(
                $"ReceivingAgent.GatedRetrievalWindowMultiplier must be at least 1; {capabilities.GatedRetrievalWindowMultiplier} was set.",
                paramName);
        }

        return new CapabilityGate(available, capabilities.MaxRiskClass, classes, capabilities.UseRunTools, capabilities.GatedRetrievalWindowMultiplier);
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
