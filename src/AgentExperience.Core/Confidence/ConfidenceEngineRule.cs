using System.Globalization;

namespace AgentExperience.Core.Confidence;

/// <summary>
/// Validates an <see cref="IExperienceConfidenceEngine"/>'s identity once, at wiring time, and checks every
/// score it produces. Holds the rule string every update records.
/// </summary>
internal sealed class ConfidenceEngineRule
{
    private const int MaxIdentityLength = 64;

    /// <summary>
    /// The <see cref="Exception.Data"/> key that marks an exception as the confidence engine's failure -- one it
    /// threw, or the refusal of a value it returned -- so a caller that must keep going (the reuse-feedback
    /// service) can tell it apart from everything else without the exception being wrapped or replaced.
    /// </summary>
    private const string EngineFailureKey = "AgentExperience.Core.ConfidenceEngineFailure";

    private readonly IExperienceConfidenceEngine _engine;

    private ConfidenceEngineRule(IExperienceConfidenceEngine engine, string recorded)
    {
        _engine = engine;
        Recorded = recorded;
    }

    /// <summary>The heuristic, recording the plain <c>"1.0.0"</c>.</summary>
    public static ConfidenceEngineRule Default { get; } =
        new(ReuseConfidenceHeuristicEngine.Instance, ReuseConfidenceHeuristic.RuleVersion);

    /// <summary>
    /// What <see cref="AgentExperience.Abstractions.ConfidenceUpdate.RuleVersion"/> records: <c>"1.0.0"</c>
    /// for the default engine, <c>"{RuleId}/{RuleVersion}"</c> for any other.
    /// </summary>
    public string Recorded { get; }

    /// <summary>Validates <paramref name="engine"/>'s identity; <see langword="null"/> is the default.</summary>
    /// <exception cref="ArgumentException">The identity is malformed, or claims the default's rule ID.</exception>
    public static ConfidenceEngineRule For(IExperienceConfidenceEngine? engine, string parameterName)
    {
        if (engine is null or ReuseConfidenceHeuristicEngine)
        {
            return Default;
        }

        var ruleId = engine.RuleId;
        var ruleVersion = engine.RuleVersion;

        if (!IsWellFormed(ruleId))
        {
            throw new ArgumentException(
                $"The confidence engine's RuleId must be 1 to {MaxIdentityLength} characters from [A-Za-z0-9._-].",
                parameterName);
        }

        if (!IsWellFormed(ruleVersion))
        {
            throw new ArgumentException(
                $"The confidence engine's RuleVersion must be 1 to {MaxIdentityLength} characters from [A-Za-z0-9._-].",
                parameterName);
        }

        if (string.Equals(ruleId, ReuseConfidenceHeuristicEngine.HeuristicRuleId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A host confidence engine may not use the default engine's RuleId '{ReuseConfidenceHeuristicEngine.HeuristicRuleId}': " +
                "its scores would be recorded as if the heuristic had produced them.",
                parameterName);
        }

        return new(engine, string.Concat(ruleId, "/", ruleVersion));
    }

    /// <summary>
    /// Scores through the engine and refuses a value that is not a score. An engine exception propagates
    /// unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">The engine returned NaN, an infinity, or a value outside [0, 1].</exception>
    public double Score(AgentExperience.Abstractions.ExperienceRecord record, int supportingValidations, int contradictions)
    {
        double score;
        try
        {
            score = _engine.Score(new ExperienceConfidenceInput(record, supportingValidations, contradictions));
        }
        catch (Exception ex) when (MarkEngineFailure(ex))
        {
            // Unreachable: the filter marks the exception and declines it, so it propagates unchanged.
            throw;
        }

        if (!double.IsFinite(score) || score < 0d || score > 1d)
        {
            // Names the rule and the value, never anything from the record.
            var refused = new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "The confidence engine '{0}' returned {1}, which is not a score in [0, 1]. Nothing was written.",
                Recorded,
                score.ToString("R", CultureInfo.InvariantCulture)));
            MarkEngineFailure(refused);
            throw refused;
        }

        return score;
    }

    /// <summary>Whether <paramref name="exception"/> is a confidence engine failure this type marked.</summary>
    public static bool IsEngineFailure(Exception exception) =>
        exception.Data.Contains(EngineFailureKey) && exception.Data[EngineFailureKey] is true;

    /// <summary>
    /// Marks an engine failure, and always answers <see langword="false"/> so it can run as an exception filter.
    /// A cancellation is left unmarked: it is the caller's, and is handled as one.
    /// </summary>
    private static bool MarkEngineFailure(Exception exception)
    {
        if (exception is not OperationCanceledException)
        {
            try
            {
                exception.Data[EngineFailureKey] = true;
            }
            catch (Exception)
            {
                // A read-only Data dictionary: the exception still propagates, just unmarked.
            }
        }

        return false;
    }

    private static bool IsWellFormed(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdentityLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
