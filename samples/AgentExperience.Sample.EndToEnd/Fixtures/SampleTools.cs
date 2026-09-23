using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentExperience.Sample.EndToEnd.Fixtures;

/// <summary>What the sample's refund check reported: the strategy it ran under and its exit code.</summary>
/// <param name="Strategy">The strategy the agent chose.</param>
/// <param name="ExitCode">The process exit code the check ended with. Zero is a pass.</param>
/// <param name="Output">A one-line, content-free summary of what the check saw.</param>
internal sealed record RefundCheckOutcome(string Strategy, int ExitCode, string Output);

/// <summary>
/// A demonstration fixture, not a tool for real use: one deterministic tool whose exit code depends
/// only on the strategy the agent picked, so the wrong approach and the correct one differ by one
/// argument and nothing else.
/// </summary>
/// <remarks>
/// The exit code is what makes the two attempts legible: <c>TaskCheckEvaluators.ExitCode</c> turns
/// each one into <c>Evidence</c> with no bespoke evaluator.
/// </remarks>
internal static class SampleTools
{
    /// <summary>The tool's name, as the model asks for it and as capture records it.</summary>
    public const string RefundCheckToolName = "run_refund_check";

    /// <summary>Attempt 1's approach: retry the refund straight away, which the lock refuses.</summary>
    public const string WrongStrategy = "retry-immediately";

    /// <summary>Attempt 2's approach: wait for the ledger lock, then refund.</summary>
    public const string CorrectStrategy = "wait-for-lock";

    /// <summary>The ticket both attempts work on.</summary>
    public const string TicketId = "RF-4821";

    /// <summary>
    /// The argument name the sanitization policy classifies as a secret, so the captured Experience
    /// Run shows it redacted rather than stored.
    /// </summary>
    public const string SecretArgumentName = "apiToken";

    /// <summary>The value the scripted model passes for <see cref="SecretArgumentName"/>. It must never reach a record.</summary>
    public const string SecretArgumentValue = "sk-live-must-never-be-captured";

    /// <summary>The refund check, as MAF invokes it.</summary>
    public static AIFunction RefundCheck { get; } = AIFunctionFactory.Create(
        (string ticketId, string strategy, string apiToken) => Run(ticketId, strategy),
        RefundCheckToolName,
        "Runs the refund pipeline's check for one ticket under the named strategy and reports its exit code.");

    /// <summary>The tool call the scripted model makes for <paramref name="strategy"/>.</summary>
    /// <param name="strategy">The strategy the scripted model asks for.</param>
    public static ScriptedToolCall CallFor(string strategy) => new(
        RefundCheckToolName,
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ticketId"] = TicketId,
            ["strategy"] = strategy,
            [SecretArgumentName] = SecretArgumentValue,
        });

    /// <summary>
    /// Reads the check's own exit code out of what the tool returned. MAF marshals a factory-created
    /// tool's result to JSON before any middleware sees it, so the host reads the structured output
    /// the model was given rather than the CLR object the tool body built.
    /// </summary>
    /// <param name="toolResult">What the tool call produced.</param>
    /// <returns>The exit code, or <see langword="null"/> when the result is not one of this tool's.</returns>
    public static int? ExitCodeOf(object? toolResult) => toolResult switch
    {
        RefundCheckOutcome outcome => outcome.ExitCode,
        JsonElement element when element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("exitCode", out var value)
            && value.TryGetInt32(out var code) => code,
        _ => null,
    };

    private static RefundCheckOutcome Run(string ticketId, string strategy) =>
        string.Equals(strategy, CorrectStrategy, StringComparison.Ordinal)
            ? new RefundCheckOutcome(
                strategy,
                0,
                string.Format(CultureInfo.InvariantCulture, "refund-check: lock on ticket {0} released, refund posted", ticketId))
            : new RefundCheckOutcome(
                strategy,
                2,
                string.Format(CultureInfo.InvariantCulture, "refund-check: ticket {0} is still held by lock 'refund_ledger'", ticketId));
}
