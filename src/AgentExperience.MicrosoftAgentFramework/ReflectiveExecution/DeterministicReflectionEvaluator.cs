using AgentExperience.Core.ReflectiveExecution;

namespace AgentExperience.MicrosoftAgentFramework.ReflectiveExecution;

/// <summary>
/// An evaluator that calls no model: a host's rules map a failed attempt to a correction. The first rule that returns a
/// strategy wins; no rule matching is <see cref="ReflectionAssessment.Unrecoverable"/> for a failed attempt, so an
/// unknown failure is never retried blindly.
/// </summary>
/// <param name="rules">Each rule gets the attempt and returns a correction, or <see langword="null"/> when it does not apply.</param>
public sealed class DeterministicReflectionEvaluator(IEnumerable<Func<ReflectionAttemptContext, string?>> rules) : IReflectionEvaluator
{
    private readonly Func<ReflectionAttemptContext, string?>[] _rules = rules?.ToArray() ?? throw new ArgumentNullException(nameof(rules));

    /// <inheritdoc />
    public ValueTask<ReflectionEvaluation> EvaluateAsync(ReflectionAttemptContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var issues = context.Outcome.FailedCalls
            .Take(ReflectionEvaluationValidator.MaxIssues)
            .Select(call => new ReflectionIssue(ReflectionIssueCategory.ToolFailure, $"{call.Tool} failed", call.ErrorClass))
            .ToArray();

        foreach (var rule in _rules)
        {
            if (rule(context) is { } strategy)
            {
                return ValueTask.FromResult(new ReflectionEvaluation(ReflectionAssessment.NeedsCorrection, issues, strategy, true, 1));
            }
        }

        return ValueTask.FromResult(new ReflectionEvaluation(ReflectionAssessment.Unrecoverable, issues, null, false, 0));
    }
}
