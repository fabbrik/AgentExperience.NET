namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// How risky the host judges one of its tools to be, for the capability gate on injection
/// (<see cref="ReceivingAgentCapabilities"/>). Ordered: a higher value is riskier. The library never
/// infers a tool's class -- not from its name, not from a record -- the host declares it in
/// <see cref="ReceivingAgentCapabilities.ToolRiskClasses"/>, and a tool it does not declare counts as
/// <see cref="Critical"/>.
/// </summary>
public enum ToolRiskClass
{
    /// <summary>Read-only or otherwise harmless.</summary>
    Low = 0,

    /// <summary>Changes state in a limited, recoverable way.</summary>
    Medium = 1,

    /// <summary>Changes state in a way that is hard to undo or reaches other parties.</summary>
    High = 2,

    /// <summary>Irreversible or high-impact. Also the class of every tool the host did not declare.</summary>
    Critical = 3,
}
