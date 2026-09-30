using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;

namespace AgentExperience.Core.Reflections;

/// <summary>
/// The deterministic, template-based <see cref="IExperienceReflector"/>. It fills fixed,
/// invariant-culture templates using only the request run's attempts and environment fingerprint
/// plus the supplied evaluation, so the same request always produces equal content. It never calls
/// a model, never reads the clock or generates identifiers, never invents a causal explanation
/// (captured, sanitized error and evaluation-reason text is quoted verbatim instead), and never
/// produces a reuse-confidence value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Attempt classification</b> (attempts are read in <see cref="Attempt.SequenceNumber"/> order):
/// every attempt with a captured <see cref="Attempt.Error"/> is a failed approach. The final attempt,
/// when it has no error, is a successful approach only when the evaluation is
/// <see cref="TaskVerificationStatus.Verified"/>; a failed approach when it is
/// <see cref="TaskVerificationStatus.Failed"/>; and neither when it is
/// <see cref="TaskVerificationStatus.Unknown"/> (with a warning). A non-final attempt without an
/// error is never classified -- attempts are not linked to verification rounds, so claiming it
/// contributed would be causal invention -- and a warning records that its contribution is unknown.
/// </para>
/// <para>
/// <b>Preconditions</b> come only from <see cref="EnvironmentFingerprint.RuntimeVersion"/>,
/// <see cref="EnvironmentFingerprint.OperatingSystem"/>, <see cref="EnvironmentFingerprint.ApplicationVersion"/>,
/// and <see cref="EnvironmentFingerprint.Metadata"/> (ordered by key, ordinal); a missing or blank
/// value is listed as <c>unknown</c> and repeated as a warning. <see cref="EnvironmentFingerprint.HostName"/>
/// is not a precondition.
/// </para>
/// <para>
/// <b>Bounded output.</b> Finalization screens every reflection against <see cref="ReflectionLimits"/>
/// and quarantines one over a limit, so this reflector never writes one: a quoted piece of captured text
/// is cut to 500 characters, the lesson and the reuse guidance to
/// <see cref="ReflectionLimits.MaxLessonLength"/>, and each list item to
/// <see cref="ReflectionLimits.MaxListItemLength"/>, each cut ending in an ellipsis (U+2026) and never
/// falling between a surrogate pair; a list longer than <see cref="ReflectionLimits.MaxListItems"/> keeps
/// its first items and ends with one saying how many more are not listed. All against
/// <see cref="ReflectionLimits.Default"/>: a host that lowers the limits below the defaults can see this
/// reflector's output refused.
/// </para>
/// </remarks>
public sealed class DefaultExperienceReflector : IExperienceReflector
{
    /// <summary>The version of this reflector's text templates. Bumped whenever any template's wording or structure changes.</summary>
    public const string TemplateVersion = "1.0.0";

    /// <summary>The <see cref="Reflection.Producer"/> value every reflection produced by this implementation carries.</summary>
    public const string ProducerIdentity = "AgentExperience.DefaultExperienceReflector/" + TemplateVersion;

    private const string UnknownValue = "unknown";

    /// <summary>The most characters of one piece of captured text (an error, a result, an evaluation reason) this reflector quotes.</summary>
    private const int MaxQuotedLength = 500;

    /// <summary>What ends a text this reflector had to cut short: a single ellipsis character.</summary>
    private const string ClipMarker = "\u2026";

    /// <inheritdoc />
    public Task<Reflection> ReflectAsync(ReflectionRequest request, CancellationToken cancellationToken = default)
    {
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.Reflect, cancellationToken);

        Reflection reflection;
        try
        {
            Validate(request);

            // Both identifiers are request-derived, so both are on the span before the work runs: a
            // reflection that threw should still say which run it was reflecting on and which
            // reflection ID the caller asked it to stamp. After Validate, so instrumentation is never
            // the thing that rejects a malformed request.
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.RunIdAttribute, request.Run.RunId.ToString("D"));
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.ReflectionIdAttribute, request.ReflectionId.ToString("D"));

            cancellationToken.ThrowIfCancellationRequested();

            reflection = Build(request, cancellationToken);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.Reflect, ex);
            throw;
        }

        // Identifiers only. The lesson, the approaches, the warnings, and the reuse guidance are all
        // built from captured text and none of them is ever a telemetry value.
        //
        // A reflector has no outcome enum of its own: the verification status it reflected on is
        // the bounded decision this call reached, and it is what an operator slices reflections by.
        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.Reflect, reflection.VerificationStatus.ToString());
        return Task.FromResult(reflection);
    }

    private static Reflection Build(ReflectionRequest request, CancellationToken cancellationToken)
    {
        var run = request.Run;
        var evaluation = request.Evaluation;
        var outcome = evaluation.Outcome;
        var status = outcome.Status;

        var evidenceIds = DistinctInOrder(outcome.Evidence.Select(e => e.EvidenceId));
        var evidenceText = evidenceIds.Count == 0
            ? "none"
            : string.Join(", ", evidenceIds.Select(id => id.ToString("D", CultureInfo.InvariantCulture)));
        var scoreText = evaluation.CompletionScore.ToString("R", CultureInfo.InvariantCulture);
        var passingChecks = DistinctInOrder(outcome.Evidence.Where(e => e.Result == CheckResult.Pass).Select(e => e.CheckId));
        var failingChecks = DistinctInOrder(outcome.Evidence.Where(e => e.Result == CheckResult.Fail).Select(e => e.CheckId));

        var warnings = new List<string>();
        var successfulApproaches = new List<string>();
        var failedApproaches = new List<string>();

        // Lesson and verdict warnings -- built only from the supplied evaluation.
        string lesson;
        switch (status)
        {
            case TaskVerificationStatus.Verified:
                lesson = Invariant($"Task '{run.TaskId}' verified: required checks [{JoinList(passingChecks)}] passed (evidence: {evidenceText}).");
                warnings.Add("Verification applies only to this run and its captured environment; reuse in another context is not itself verified.");
                if (evidenceIds.Count == 0)
                {
                    warnings.Add("Verification status is Verified, but the evaluation supplied no evidence.");
                }

                break;

            case TaskVerificationStatus.Failed:
                lesson = Invariant($"Task '{run.TaskId}' failed verification: required checks [{JoinList(failingChecks)}] failed (evidence: {evidenceText}).") + ReasonSuffix(outcome.Reason);
                warnings.Add("Not a validated procedure: task verification status is Failed.");
                warnings.Add(Invariant($"Verification failed: required checks [{JoinList(failingChecks)}] failed."));
                break;

            default:
                lesson = Invariant($"Task '{run.TaskId}' is unverified: no conclusive verification was reached (completion score {scoreText} under rule {evaluation.RuleVersion}; evidence: {evidenceText}).") + ReasonSuffix(outcome.Reason);
                warnings.Add("Not a validated procedure: task verification status is Unknown.");
                warnings.Add("Unverified: not every required check reached a conclusive result in the evaluation.");
                break;
        }

        if (status != TaskVerificationStatus.Verified && evaluation.CompletionScore > 0)
        {
            warnings.Add(Invariant($"Completion score {scoreText} (rule {evaluation.RuleVersion}) is a partial score and is not verification."));
        }

        if (run.ExecutionStatus != RunExecutionStatus.Completed)
        {
            warnings.Add(Invariant($"Run execution status is {run.ExecutionStatus}, not Completed."));
        }

        // Attempt classification -- see the type's remarks.
        var attempts = run.Attempts.OrderBy(a => a.SequenceNumber).ToList();
        if (attempts.Count == 0)
        {
            warnings.Add("No attempts were captured.");
        }

        for (var index = 0; index < attempts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attempt = attempts[index];
            var isFinal = index == attempts.Count - 1;
            var description = Describe(attempt);

            if (attempt.Error is not null)
            {
                failedApproaches.Add(Invariant($"{description} ended with error: {Quote(attempt.Error)}."));
                if (isFinal && status == TaskVerificationStatus.Verified)
                {
                    warnings.Add(Invariant($"Attempt {attempt.SequenceNumber} (final) ended with an error despite a Verified status; no captured attempt is recorded as a successful approach."));
                }

                continue;
            }

            if (!isFinal)
            {
                warnings.Add(Invariant($"Attempt {attempt.SequenceNumber} completed without an error but was followed by a later attempt; its contribution to the outcome is unknown."));
                continue;
            }

            switch (status)
            {
                case TaskVerificationStatus.Verified:
                    successfulApproaches.Add(Invariant($"{description} completed without an error (result: {QuoteOrMissing(attempt.Result)}) as the final attempt of a verified run."));
                    break;

                case TaskVerificationStatus.Failed:
                    failedApproaches.Add(Invariant($"{description} completed without an error (result: {QuoteOrMissing(attempt.Result)}) as the final attempt, but verification failed."));
                    break;

                default:
                    warnings.Add(Invariant($"Attempt {attempt.SequenceNumber} (final) completed without an error, but verification is Unknown; it is not recorded as a successful approach."));
                    break;
            }
        }

        // Preconditions -- environment fingerprint only; HostName is not a precondition.
        var preconditions = new List<string>();
        var hasUnknownPrecondition = false;
        void AddPrecondition(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                preconditions.Add(Invariant($"{label}: {UnknownValue}"));
                warnings.Add(Invariant($"Precondition '{label}' was not captured and is {UnknownValue}."));
                hasUnknownPrecondition = true;
            }
            else
            {
                preconditions.Add(Invariant($"{label}: {value}"));
            }
        }

        var environment = run.Environment;
        AddPrecondition("Runtime version", environment.RuntimeVersion);
        AddPrecondition("Operating system", environment.OperatingSystem);
        AddPrecondition("Application version", environment.ApplicationVersion);
        foreach (var entry in environment.Metadata.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            AddPrecondition(Invariant($"Environment metadata [{entry.Key}]"), entry.Value);
        }

        var reuseGuidance = status == TaskVerificationStatus.Verified
            ? Invariant($"Reuse only where the listed preconditions match, and re-run required checks [{JoinList(passingChecks)}] to confirm the outcome in the new context.")
                + (hasUnknownPrecondition ? " Confirm the unknown preconditions before reuse." : string.Empty)
            : Invariant($"Do not reuse as a validated procedure: task verification status is {status}. Treat the listed approaches as observed history only, and verify any approach independently before relying on it.");

        // Bounded last, so the output always fits the screening finalization applies to every reflection.
        var limits = ReflectionLimits.Default;
        return new Reflection(
            ReflectionId: request.ReflectionId,
            ExperienceRunId: run.RunId,
            Lesson: Clip(lesson, limits.MaxLessonLength),
            SuccessfulApproaches: Bound(successfulApproaches, "successful approaches", limits),
            FailedApproaches: Bound(failedApproaches, "failed approaches", limits),
            Preconditions: Bound(preconditions, "preconditions", limits),
            Warnings: Bound(warnings, "warnings", limits),
            ReuseGuidance: Clip(reuseGuidance, limits.MaxLessonLength),
            EvidenceIds: evidenceIds.ToArray(),
            VerificationStatus: status,
            CompletionScore: evaluation.CompletionScore,
            VerificationRuleVersion: evaluation.RuleVersion,
            Producer: ProducerIdentity,
            CreatedAt: request.CreatedAt);
    }

    private static void Validate(ReflectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The request's constructor already refused an evaluation computed for another run; this is
        // the same check again, so the default reflector never depends on how its request was made.
        var run = request.Run;
        var evaluation = ReflectionRequest.EnsureBound(run, request.Evaluation);

        if (string.IsNullOrWhiteSpace(run.TaskId))
        {
            throw new ArgumentException("The run's TaskId must not be null or blank.", nameof(request));
        }

        if (run.ExecutionStatus is null)
        {
            throw new ArgumentException("The run is still in progress (ExecutionStatus is null); only a finished run can be reflected on.", nameof(request));
        }

        if (run.Environment?.Metadata is null)
        {
            throw new ArgumentException("The run's Environment and its Metadata must not be null.", nameof(request));
        }

        if (run.Attempts is null || run.Attempts.Any(a => a is null || a.ToolCalls is null || a.ToolCalls.Any(t => t is null)))
        {
            throw new ArgumentException("The run's Attempts (and each attempt's ToolCalls) must not be null or contain null entries.", nameof(request));
        }

        if (run.Attempts.Select(a => a.SequenceNumber).Distinct().Count() != run.Attempts.Count)
        {
            throw new ArgumentException("The run's attempt SequenceNumbers must be unique.", nameof(request));
        }

        var outcome = evaluation.Outcome;
        if (outcome is null || outcome.Evidence is null || outcome.Evidence.Any(e => e is null))
        {
            throw new ArgumentException("The evaluation's Outcome and its Evidence must not be null or contain null entries.", nameof(request));
        }

        if (!Enum.IsDefined(outcome.Status))
        {
            throw new ArgumentException("The evaluation's Outcome.Status is not a defined TaskVerificationStatus value.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(evaluation.RuleVersion))
        {
            throw new ArgumentException("The evaluation's RuleVersion must not be null or blank.", nameof(request));
        }

        if (double.IsNaN(evaluation.CompletionScore) || evaluation.CompletionScore < 0 || evaluation.CompletionScore > 1)
        {
            throw new ArgumentException("The evaluation's CompletionScore must be between 0 and 1.", nameof(request));
        }
    }

    private static string Describe(Attempt attempt)
    {
        var toolCalls = attempt.ToolCalls
            .OrderBy(t => t.SequenceNumber)
            .Select(t => t.Error is null ? t.ToolName : Invariant($"{t.ToolName} (error: {Quote(t.Error)})"))
            .ToList();

        return toolCalls.Count == 0
            ? Invariant($"Attempt {attempt.SequenceNumber} with no tool calls")
            : Invariant($"Attempt {attempt.SequenceNumber} using tools [{string.Join(", ", toolCalls)}]");
    }

    private static string ReasonSuffix(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : Invariant($" Evaluation reason: {Quote(reason)}.");

    /// <summary>
    /// Wraps captured text in double quotes, escaping backslash, double quote, CR and LF so the quoted
    /// span is unambiguous. Every other character (including non-ASCII) is kept as-is for readability.
    /// </summary>
    private static string Quote(string text) =>
        "\"" + Clip(text, MaxQuotedLength).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// <paramref name="text"/> cut to at most <paramref name="length"/> characters, the last of them
    /// <see cref="ClipMarker"/>, never between a surrogate pair. A text that fits is returned as it is.
    /// </summary>
    private static string Clip(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var cut = length - ClipMarker.Length;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return string.Concat(text.AsSpan(0, cut), ClipMarker);
    }

    /// <summary>
    /// <paramref name="items"/> with each item clipped to <see cref="ReflectionLimits.MaxListItemLength"/> and
    /// the list cut to <see cref="ReflectionLimits.MaxListItems"/>, its last item then saying how many more
    /// were left out. A list that fits is copied as it is.
    /// </summary>
    private static string[] Bound(List<string> items, string noun, ReflectionLimits limits)
    {
        if (items.Count > limits.MaxListItems)
        {
            var shown = limits.MaxListItems - 1;
            var omitted = items.Count - shown;
            items = [.. items.Take(shown), Invariant($"{omitted} further {noun} are not listed.")];
        }

        return [.. items.Select(item => Clip(item, limits.MaxListItemLength))];
    }

    private static string QuoteOrMissing(string? text) => text is null ? "none captured" : Quote(text);

    private static string JoinList(IReadOnlyList<string> items) => string.Join(", ", items);

    private static List<T> DistinctInOrder<T>(IEnumerable<T> source)
    {
        var seen = new HashSet<T>();
        var result = new List<T>();
        foreach (var item in source)
        {
            if (seen.Add(item))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
