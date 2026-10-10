using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>
/// The transfer experiment's distractor task family: a service's read cache, and the one tool that flushes it.
/// </summary>
/// <remarks>
/// Its runs are driven by a fixed script (<see cref="TransferExperiment"/>), never by a model, so a live run spends no
/// model call on them. They exist so the shared store holds verified experience that names the same clusters as the
/// migration records and is useless for a migration: retrieval has to rank past it. As in
/// <see cref="MigrationEnvironment"/>, the verifier is the simulated state, never text.
/// </remarks>
internal sealed class CacheEnvironment
{
    public const string FlushToolName = "flush_read_cache";
    public const int ExitFlushed = 0;
    public const int ExitUnknownService = 5;

    private readonly string _service;

    public CacheEnvironment(string service)
    {
        _service = service;
        Flush = AIFunctionFactory.Create(FlushReadCache, FlushToolName, "Flushes a service's read cache in production and reports an exit code (exit=0 means flushed).");
    }

    public AIFunction Flush { get; }

    /// <summary>The verifier: whether the cache was flushed. Reads state, never text.</summary>
    public bool IsFlushed { get; private set; }

    /// <summary>The tool's implementation, which the script calls directly.</summary>
    public string FlushReadCache([Description("The service whose read cache to flush.")] string service)
    {
        if (!string.Equals(service, _service, StringComparison.Ordinal))
        {
            return "exit=" + ExitUnknownService.ToString(CultureInfo.InvariantCulture) + " unknown service '" + service + "'; nothing was flushed";
        }

        IsFlushed = true;
        return "exit=" + ExitFlushed.ToString(CultureInfo.InvariantCulture) + " read cache flushed";
    }
}

/// <summary>
/// The second distractor family: a service's configuration, and the one tool that pushes a change to it. It takes no
/// strategy, so no record of it ever names one. Driven by the same fixed script as <see cref="CacheEnvironment"/>.
/// </summary>
internal sealed class ConfigEnvironment(string service, string change)
{
    public const string PushToolName = "push_config";
    public const int ExitApplied = 0;
    public const int ExitRejected = 5;

    /// <summary>The verifier: whether the change is live. Reads state, never text.</summary>
    public bool IsLive { get; private set; }

    /// <summary>The tool's implementation, which the script calls directly.</summary>
    public string PushConfig(string target, string pushed)
    {
        if (!string.Equals(target, service, StringComparison.Ordinal) || !string.Equals(pushed, change, StringComparison.Ordinal))
        {
            return "exit=" + ExitRejected.ToString(CultureInfo.InvariantCulture) + " unknown service or change; nothing was pushed";
        }

        IsLive = true;
        return "exit=" + ExitApplied.ToString(CultureInfo.InvariantCulture) + " config change is live";
    }
}
