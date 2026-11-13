namespace AgentExperience.Core.ReflectiveExecution;

/// <summary>
/// Turns an evaluation into the correction an attempt is given. The task, the host's constraints and the permissions are
/// not inputs it can change: it returns text only, which the coordinator bounds and screens like any model-authored
/// text, and the host presents as untrusted reference. The default (<see cref="EvaluatorProposal"/>) uses the evaluator's
/// own proposal; a host replaces it to keep a plan inside its own rules (for example, only corrections from a catalogue).
/// </summary>
public interface IReplanningStrategy
{
    /// <summary>The correction for the next attempt, or <see langword="null"/> when no acceptable plan exists (the loop stops as not retryable).</summary>
    /// <param name="context">The attempt that just ended.</param>
    /// <param name="evaluation">The validated evaluation.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    ValueTask<string?> ReplanAsync(ReflectionAttemptContext context, ReflectionEvaluation evaluation, CancellationToken cancellationToken);

    /// <summary>Uses the evaluator's own <see cref="ReflectionEvaluation.ProposedStrategy"/>.</summary>
    public static IReplanningStrategy EvaluatorProposal { get; } = new ProposalStrategy();

    private sealed class ProposalStrategy : IReplanningStrategy
    {
        public ValueTask<string?> ReplanAsync(ReflectionAttemptContext context, ReflectionEvaluation evaluation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(evaluation.ProposedStrategy);
    }
}

/// <summary>
/// One explicit requirement of a task, checked without a model. Requirement-driven reflection is triggered when a
/// finished attempt's output does not meet it.
/// </summary>
/// <param name="Name">A short, stable name, used as the issue's description.</param>
/// <param name="IsMet">Whether the attempt meets it.</param>
/// <param name="Correction">What to do differently when it is not met.</param>
public sealed record ReflectionRequirement(string Name, Func<ReflectionAttemptContext, bool> IsMet, string Correction);

/// <summary>
/// Requirement-driven reflection with no model: each unmet <see cref="ReflectionRequirement"/> is an
/// <see cref="ReflectionIssueCategory.UnmetRequirement"/>, and the first unmet one's correction is the proposal. All met
/// is <see cref="ReflectionAssessment.Satisfied"/>, which, like any evaluator's, is not verification.
/// </summary>
/// <param name="requirements">The requirements, checked in order.</param>
public sealed class RequirementReflectionEvaluator(IEnumerable<ReflectionRequirement> requirements) : IReflectionEvaluator
{
    private readonly ReflectionRequirement[] _requirements = requirements?.ToArray() ?? throw new ArgumentNullException(nameof(requirements));

    /// <inheritdoc />
    public ValueTask<ReflectionEvaluation> EvaluateAsync(ReflectionAttemptContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var unmet = _requirements.Where(r => !r.IsMet(context)).Take(ReflectionEvaluationValidator.MaxIssues).ToArray();
        if (unmet.Length == 0)
        {
            return ValueTask.FromResult(new ReflectionEvaluation(ReflectionAssessment.Satisfied, [], null, false, 1));
        }

        return ValueTask.FromResult(new ReflectionEvaluation(
            ReflectionAssessment.NeedsCorrection,
            [.. unmet.Select(r => new ReflectionIssue(ReflectionIssueCategory.UnmetRequirement, r.Name))],
            unmet[0].Correction,
            true,
            1));
    }
}

/// <summary>
/// Combines several authorized evaluators. <see cref="ReflectionAssessment.Unrecoverable"/> from any wins; otherwise the
/// first <see cref="ReflectionAssessment.NeedsCorrection"/> supplies the strategy and every NeedsCorrection's issues are
/// merged (bounded); all <see cref="ReflectionAssessment.Satisfied"/> is Satisfied. The confidence is the lowest. An
/// evaluator that throws fails the whole evaluation, so a broken source is never skipped silently.
/// </summary>
/// <param name="evaluators">The evaluators, asked in order.</param>
public sealed class CompositeReflectionEvaluator(IEnumerable<IReflectionEvaluator> evaluators) : IReflectionEvaluator
{
    private readonly IReflectionEvaluator[] _evaluators = evaluators?.ToArray() ?? throw new ArgumentNullException(nameof(evaluators));

    /// <inheritdoc />
    public async ValueTask<ReflectionEvaluation> EvaluateAsync(ReflectionAttemptContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var issues = new List<ReflectionIssue>();
        ReflectionEvaluation? first = null;
        var confidence = 1.0;
        foreach (var evaluator in _evaluators)
        {
            var evaluation = await evaluator.EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
            confidence = Math.Min(confidence, evaluation.Confidence);
            if (evaluation.Assessment == ReflectionAssessment.Unrecoverable)
            {
                return evaluation with { Confidence = confidence };
            }

            if (evaluation.Assessment == ReflectionAssessment.NeedsCorrection)
            {
                first ??= evaluation;
                issues.AddRange(evaluation.Issues);
            }
        }

        return first is null
            ? new ReflectionEvaluation(ReflectionAssessment.Satisfied, [], null, false, _evaluators.Length == 0 ? 0 : confidence)
            : first with { Issues = [.. issues.Take(ReflectionEvaluationValidator.MaxIssues)], Confidence = confidence };
    }
}
