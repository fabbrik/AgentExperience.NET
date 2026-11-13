using System.Globalization;
using AgentExperience.Core.Reflections;

namespace AgentExperience.Core.ReflectiveExecution;

/// <summary>
/// Checks an evaluator's output before the coordinator acts on it: bounded counts and lengths, a finite confidence in
/// [0, 1], defined enum values, and every text screened by the same content guard model-authored lessons pass
/// (credential shapes, instruction-override phrasing, links the attempt did not show). It refuses, never truncates.
/// </summary>
internal static class ReflectionEvaluationValidator
{
    internal const int MaxIssues = 8;
    internal const int MaxIssueTextLength = 500;

    /// <summary>The reason <paramref name="evaluation"/> is invalid, as a content-free phrase, or <see langword="null"/> when it is valid.</summary>
    internal static string? Validate(ReflectionEvaluation? evaluation, ReflectionAttemptContext context, int maxStrategyLength)
    {
        if (evaluation is null) { return "no evaluation"; }
        if (!Enum.IsDefined(evaluation.Assessment)) { return "undefined assessment"; }
        if (evaluation.Issues is null) { return "no issue list"; }
        if (evaluation.Issues.Count > MaxIssues) { return "too many issues"; }
        if (double.IsNaN(evaluation.Confidence) || double.IsInfinity(evaluation.Confidence) || evaluation.Confidence is < 0 or > 1)
        {
            return "confidence out of range";
        }

        var links = ModelAuthoredContentGuard.LinksOf(RunContent(context));
        foreach (var issue in evaluation.Issues)
        {
            if (issue is null || !Enum.IsDefined(issue.Category)) { return "undefined issue category"; }
            if (string.IsNullOrWhiteSpace(issue.Description) || issue.Description.Length > MaxIssueTextLength) { return "issue description length"; }
            if (issue.Evidence is { Length: > MaxIssueTextLength }) { return "issue evidence length"; }
            var refusal = ModelAuthoredContentGuard.Check(issue.Description, links)
                ?? (issue.Evidence is null ? null : ModelAuthoredContentGuard.Check(issue.Evidence, links));
            if (refusal is not null) { return refusal; }
        }

        if (evaluation.ProposedStrategy is { } strategy)
        {
            if (string.IsNullOrWhiteSpace(strategy) || strategy.Length > maxStrategyLength) { return "strategy length"; }
            var refusal = ModelAuthoredContentGuard.Check(strategy, links);
            if (refusal is not null) { return refusal; }
        }

        return null;
    }

    /// <summary>The reason a correction note is unacceptable, or <see langword="null"/>: bounded and screened like a strategy.</summary>
    internal static string? ValidateCorrection(string note, ReflectionAttemptContext context, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > maxLength) { return "correction length"; }
        return ModelAuthoredContentGuard.Check(note, ModelAuthoredContentGuard.LinksOf(RunContent(context)));
    }

    /// <summary>What the evaluator was shown, so a link or host it quotes from the run is not refused as invented.</summary>
    internal static IReadOnlyList<string> RunContent(ReflectionAttemptContext context)
    {
        var content = new List<string> { context.TaskText };
        if (context.Outcome.ResultExcerpt is { } excerpt) { content.Add(excerpt); }
        if (context.Outcome.ErrorClass is { } error) { content.Add(error); }
        foreach (var call in context.Outcome.FailedCalls)
        {
            content.Add(call.Tool);
            content.Add(call.ErrorClass);
        }

        content.AddRange(context.PreviousStrategies);
        content.Add(context.AttemptNumber.ToString(CultureInfo.InvariantCulture));
        return content;
    }
}
