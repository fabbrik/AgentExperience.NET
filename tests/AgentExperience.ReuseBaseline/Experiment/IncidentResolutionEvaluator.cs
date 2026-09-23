using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentExperience.ReuseBaseline.Experiment;

/// <summary>The exit code of a trial's final incident check, as evaluation context.</summary>
/// <param name="exitCode">The exit code, or <see langword="null"/> when the trial never produced one.</param>
internal sealed class IncidentCheckContext(int? exitCode)
    : EvaluationContext("IncidentCheckExitCode", exitCode?.ToString(CultureInfo.InvariantCulture) ?? "(none)")
{
    /// <summary>The exit code the trial's final incident check reported.</summary>
    public int? ExitCode { get; } = exitCode;
}

/// <summary>
/// The per-trial task check, as a <see cref="IEvaluator"/> returning a <see cref="BooleanMetric"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what the harness reuses from
/// <c>Microsoft.Extensions.AI.Evaluation</c>: the evaluator interface and its result type, for a
/// deterministic check, with no model and no <see cref="ChatConfiguration"/> -- exactly the shape
/// <c>tests/AgentExperience.CompatibilityProof/EvaluationRedactionProof.cs:39</c> proves works.
/// There is no <c>ReportingConfiguration</c> and no <c>ScenarioRun</c> anywhere in this project:
/// <c>reuse-boundaries.md</c> forbids building an evaluation reporting platform by name, and the
/// reporting this story needs is a golden-filed text report.
/// </para>
/// <para>
/// It is a second, independent reading of the same fact the verification aggregator reaches from
/// evidence. The harness asserts the two agree and says so in the report rather than quietly
/// preferring one.
/// </para>
/// </remarks>
internal sealed class IncidentResolutionEvaluator : IEvaluator
{
    /// <summary>The name of the metric this evaluator produces.</summary>
    public const string MetricName = "IncidentResolved";

    /// <inheritdoc />
    public IReadOnlyCollection<string> EvaluationMetricNames { get; } = [MetricName];

    /// <inheritdoc />
    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var context = additionalContext?.OfType<IncidentCheckContext>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{nameof(IncidentResolutionEvaluator)} requires an {nameof(IncidentCheckContext)} in additionalContext.");

        var resolved = context.ExitCode == 0;
        var reason = context.ExitCode is { } code
            ? string.Format(CultureInfo.InvariantCulture, "the final incident check exited {0}", code)
            : "the trial produced no incident check exit code";

        return new ValueTask<EvaluationResult>(new EvaluationResult(new BooleanMetric(MetricName, resolved, reason)));
    }
}
