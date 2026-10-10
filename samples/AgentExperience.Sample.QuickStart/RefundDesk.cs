using System.ComponentModel;
using AgentExperience.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentExperience.Sample.QuickStart;

/// <summary>One <c>run_refund_check</c> call, and whether it released the refund.</summary>
public sealed record RefundAttempt(string Strategy, bool Succeeded);

/// <summary>
/// The system the agent works on: a refund stuck behind a ledger lock, and the one tool that can release it. Exactly
/// one strategy works, and a wrong one says only "not that one", so nothing in the tool's answers tells the model which.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RefundReleased"/> is the verdict <c>Verify</c> reads: the desk's own state, set only by a call with the
/// working strategy. Nothing the model says can make it true.
/// </para>
/// <para>
/// A call that exits non-zero throws, as a real tool reports failure, so capture records it as a failed call and
/// the injected <c>Tried:</c> line marks it <c>[failed: …]</c>. The agent's function-calling loop hands the model an
/// error result for it and the run goes on.
/// </para>
/// </remarks>
public sealed class RefundDesk
{
    public const string ToolName = "run_refund_check";

    /// <summary>Every strategy the tool accepts, in the order its description lists them.</summary>
    public static readonly IReadOnlyList<string> Strategies = ["retry-immediately", "wait-for-lock", "skip-ledger-check"];

    /// <summary>The fixed environment every run is recorded under, so the output is the same on every machine.</summary>
    public static readonly EnvironmentFingerprint Environment = new(
        HostName: "quick-start",
        RuntimeVersion: "net10.0",
        OperatingSystem: "any",
        ApplicationVersion: "1.0.0-quick-start",
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    private const string WorkingStrategy = "wait-for-lock";

    private readonly List<RefundAttempt> _attempts = [];

    public RefundDesk() =>
        Tool = AIFunctionFactory.Create(
            RunRefundCheck,
            ToolName,
            "Runs the refund check for a ticket with the named strategy. It returns exit=0 when the refund is released; "
                + "any other strategy fails with an error naming its exit code, and changes nothing. Valid strategies: "
                + string.Join(", ", Strategies) + ". Exactly one of them works for a locked refund.");

    public AIFunction Tool { get; }

    /// <summary>Every call this run made, in order.</summary>
    public IReadOnlyList<RefundAttempt> Attempts => _attempts;

    /// <summary>Whether the refund is released: the desk's state, never the model's claim.</summary>
    public bool RefundReleased => _attempts.Any(attempt => attempt.Succeeded);

    /// <summary>Starts a new run on a fresh ticket.</summary>
    public void Reset() => _attempts.Clear();

    private string RunRefundCheck(
        [Description("The ticket number, for example 4812.")] string ticketId,
        [Description("The strategy to use.")] string strategy)
    {
        if (!Strategies.Contains(strategy, StringComparer.Ordinal))
        {
            // Not an attempt at the refund, so not in Attempts; still a failed call.
            throw new InvalidOperationException("exit=64 unknown strategy; nothing was changed");
        }

        var succeeded = string.Equals(strategy, WorkingStrategy, StringComparison.Ordinal);
        _attempts.Add(new RefundAttempt(strategy, succeeded));
        return succeeded
            ? "exit=0 the refund is released"
            : throw new InvalidOperationException("exit=3 the refund is still stuck; nothing was changed");
    }
}
