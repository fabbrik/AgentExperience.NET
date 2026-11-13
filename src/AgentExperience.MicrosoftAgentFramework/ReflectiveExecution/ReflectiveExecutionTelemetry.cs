using System.Diagnostics.Metrics;
using AgentExperience.Core.ReflectiveExecution;

namespace AgentExperience.MicrosoftAgentFramework.ReflectiveExecution;

/// <summary>
/// The counters reflective execution emits, on their own meter (<see cref="MeterName"/>). Experimental, and deliberately
/// <b>not</b> part of the cross-language names file (<c>docs/telemetry-names.json</c>): the Python and Go ports have no
/// reflective execution, so no port has to match these names. Tags are enums only: no prompt, tool argument, error text,
/// model text or identity ever reaches a measurement.
/// </summary>
internal static class ReflectiveExecutionTelemetry
{
    /// <summary>The meter name; subscribe with <c>AddMeter</c>.</summary>
    internal const string MeterName = "AgentExperience.MicrosoftAgentFramework.ReflectiveExecution";

    private static readonly Meter Meter = new(MeterName, "1");

    private static readonly Counter<long> Executions = Meter.CreateCounter<long>(
        "agentexperience.reflection.executions", "{execution}", "Reflective executions, tagged with why they stopped.");

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "agentexperience.reflection.retries", "{execution}", "Executions in which a retry occurred.");

    private static readonly Counter<long> Improved = Meter.CreateCounter<long>(
        "agentexperience.reflection.retries.improved", "{execution}", "Executions in which a retry did better than the attempt before it. Not evidence of success.");

    private static readonly Counter<long> Verified = Meter.CreateCounter<long>(
        "agentexperience.reflection.retries.verified", "{execution}", "Executions in which a retry ended with the host's own check passing. The only one of the three that is evidence.");

    internal static void Record(ReflectionExecutionResult result)
    {
        Executions.Add(1, new KeyValuePair<string, object?>("agentexperience.reflection.stop_reason", result.StopReason.ToString()));
        if (result.RetryHappened) { Retries.Add(1); }
        if (result.RetryImprovedResult) { Improved.Add(1); }
        if (result.RetryReachedVerifiedSuccess) { Verified.Add(1); }
    }
}
