using System.Text;

namespace AgentExperience.Core.ReflectiveExecution;

/// <summary>Runs one attempt for the host. It must not throw for an agent failure: describe it in the outcome.</summary>
/// <param name="input">What this attempt is given.</param>
/// <param name="cancellationToken">The caller's token.</param>
/// <returns>How the attempt ended.</returns>
public delegate ValueTask<ReflectionAttemptOutcome> ReflectionAttemptDelegate(ReflectionAttemptInput input, CancellationToken cancellationToken);

/// <summary>
/// Drives execution reflection: run an attempt, ask the host's trusted check first, evaluate only when it is needed,
/// and retry within hard budgets and only with the host's authorization. It is MAF-free; the host supplies the attempt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Authority.</b> A passing trusted check ends the loop as <see cref="ReflectionStopReason.VerifiedSuccess"/>.
/// An evaluator's <see cref="ReflectionAssessment.Satisfied"/> never does: without a trusted check it ends as
/// <see cref="ReflectionStopReason.EvaluatorSatisfied"/>, which is not verification, and against a failing trusted
/// check it ends as <see cref="ReflectionStopReason.NotRetryable"/>.
/// </para>
/// <para>
/// <b>What it cannot do.</b> It has no tools, identity or scope to change: it calls the host's delegate with the
/// original task text and an untrusted correction note, nothing else. Every semantic retry needs the
/// <see cref="IRetryAuthorizer"/>, which by default denies, and a task that already acted
/// (<see cref="SideEffectPolicy.NeverRetry"/>, the default) is never retried semantically.
/// </para>
/// <para>Experimental: the API may change in a later preview.</para>
/// </remarks>
public sealed class ReflectionExecutionCoordinator
{
    private readonly ReflectionExecutionOptions _options;
    private readonly IReflectionEvaluator? _evaluator;
    private readonly IRetryAuthorizer _authorizer;
    private readonly IReplanningStrategy _replanning;

    /// <summary>Creates a coordinator.</summary>
    /// <param name="options">Limits and policy.</param>
    /// <param name="evaluator">The evaluator, or <see langword="null"/> for none (then only a failing trusted check ends without a retry).</param>
    /// <param name="authorizer">The retry gate; <see langword="null"/> means <see cref="IRetryAuthorizer.DenyAll"/>.</param>
    /// <param name="replanning">Builds the correction from an evaluation; <see langword="null"/> uses the evaluator's own proposal.</param>
    public ReflectionExecutionCoordinator(ReflectionExecutionOptions options, IReflectionEvaluator? evaluator = null, IRetryAuthorizer? authorizer = null, IReplanningStrategy? replanning = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _evaluator = evaluator;
        _authorizer = authorizer ?? IRetryAuthorizer.DenyAll;
        _replanning = replanning ?? IReplanningStrategy.EvaluatorProposal;
    }

    /// <summary>Runs the task until a stop reason.</summary>
    /// <param name="taskText">The original task text, passed unchanged to every evaluation.</param>
    /// <param name="attempt">Runs one attempt.</param>
    /// <param name="onStopped">
    /// Called exactly once with the stop reason, after the last attempt, so the host can close the open run it kept
    /// open between attempts. An exception from it is not swallowed.
    /// </param>
    /// <param name="cancellationToken">Cancels everything; it propagates as <see cref="OperationCanceledException"/>.</param>
    /// <returns>Every attempt, every evaluation and why it stopped.</returns>
    public async ValueTask<ReflectionExecutionResult> ExecuteAsync(
        string taskText,
        ReflectionAttemptDelegate attempt,
        Func<ReflectionStopReason, ValueTask>? onStopped = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskText);
        ArgumentNullException.ThrowIfNull(attempt);

        var time = _options.TimeProvider;
        var started = time.GetTimestamp();
        var attempts = new List<ReflectionAttemptOutcome>();
        var evaluations = new List<ReflectionEvaluation>();
        var tried = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var maxAttempts = _options.Enabled ? Math.Min(_options.MaxAttempts, _options.MaxReflectionCycles + 1) : 1;
        var cyclesBound = _options.MaxReflectionCycles + 1 < _options.MaxAttempts;
        string? note = null;
        long tokens = 0;
        var toolCalls = 0;
        var stop = ReflectionStopReason.AttemptsExhausted;
        Emit(new ReflectionStep(ReflectionStepKind.ExecutionStarted, 0));

        for (var n = 1; ; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await RunAttemptAsync(attempt, new ReflectionAttemptInput(n, note, n >= maxAttempts), cancellationToken).ConfigureAwait(false);
            attempts.Add(outcome);
            toolCalls += outcome.ToolCalls ?? 0;
            tokens += outcome.TokensUsed ?? 0;
            Emit(new ReflectionStep(ReflectionStepKind.AttemptCompleted, n));
            if (_options.Enabled) { Emit(new ReflectionStep(ReflectionStepKind.VerificationReceived, n, TrustedCheckPassed: outcome.TrustedCheckPassed)); }

            if (!_options.Enabled) { stop = ReflectionStopReason.Disabled; break; }
            if (outcome.TrustedCheckPassed == true) { stop = ReflectionStopReason.VerifiedSuccess; break; }
            if (n >= maxAttempts) { stop = cyclesBound ? ReflectionStopReason.CyclesExhausted : ReflectionStopReason.AttemptsExhausted; break; }
            if (time.GetElapsedTime(started) >= _options.MaxDuration) { stop = ReflectionStopReason.DurationExhausted; break; }
            if ((_options.MaxToolCalls is { } maxCalls && toolCalls >= maxCalls) || (_options.MaxTokens is { } maxTokens && tokens >= maxTokens))
            {
                stop = ReflectionStopReason.BudgetExhausted;
                break;
            }

            if (_options.SideEffects == SideEffectPolicy.NeverRetry) { stop = ReflectionStopReason.NotRetryable; break; }

            var context = new ReflectionAttemptContext(taskText, n, outcome, tried.ToArray());
            ReflectionEvaluation evaluation;
            if (outcome.Transient && !outcome.Completed && outcome.TrustedCheckPassed != true)
            {
                // A technical retry: the same input may survive, so no evaluator and no correction note.
                evaluation = new ReflectionEvaluation(ReflectionAssessment.NeedsCorrection, [], null, true, 0);
                note = null;
            }
            else
            {
                if (_evaluator is null) { stop = ReflectionStopReason.NotRetryable; break; }
                Emit(new ReflectionStep(ReflectionStepKind.ReflectionRequested, n));
                var evaluated = await EvaluateAsync(context, cancellationToken).ConfigureAwait(false);
                if (evaluated is null) { stop = ReflectionStopReason.EvaluationFailed; break; }
                evaluation = evaluated;
                evaluations.Add(evaluation);
                Emit(new ReflectionStep(ReflectionStepKind.EvaluationCompleted, n, Assessment: evaluation.Assessment));

                if (evaluation.Assessment == ReflectionAssessment.Unrecoverable) { stop = ReflectionStopReason.Unrecoverable; break; }
                if (evaluation.Assessment == ReflectionAssessment.Satisfied)
                {
                    // The trusted check is authoritative: a model's "fine" never overrides a failing one.
                    stop = outcome.TrustedCheckPassed == false ? ReflectionStopReason.NotRetryable : ReflectionStopReason.EvaluatorSatisfied;
                    break;
                }

                if (!evaluation.RecommendRetry) { stop = ReflectionStopReason.NotRetryable; break; }
                var planned = await ReplanAsync(context, evaluation, cancellationToken).ConfigureAwait(false);
                if (planned.Failed) { stop = ReflectionStopReason.EvaluationFailed; break; }
                if (planned.Note is null) { stop = ReflectionStopReason.NotRetryable; break; }
                if (!seen.Add(Normalize(planned.Note))) { stop = ReflectionStopReason.RepeatedStrategy; break; }
                note = planned.Note;
                tried.Add(note);
                Emit(new ReflectionStep(ReflectionStepKind.ReplanningPerformed, n));
            }

            if (!await AuthorizeAsync(context, evaluation, cancellationToken).ConfigureAwait(false))
            {
                Emit(new ReflectionStep(ReflectionStepKind.RetryDenied, n));
                stop = ReflectionStopReason.RetryDenied;
                break;
            }

            Emit(new ReflectionStep(ReflectionStepKind.RetryAuthorized, n));
        }

        if (stop is ReflectionStopReason.AttemptsExhausted or ReflectionStopReason.CyclesExhausted or ReflectionStopReason.DurationExhausted or ReflectionStopReason.BudgetExhausted)
        {
            Emit(new ReflectionStep(ReflectionStepKind.BudgetExhausted, attempts.Count, StopReason: stop));
        }

        if (onStopped is not null)
        {
            await onStopped(stop).ConfigureAwait(false);
        }

        Emit(new ReflectionStep(ReflectionStepKind.ExecutionStopped, attempts.Count, StopReason: stop));

        return new ReflectionExecutionResult(
            stop,
            attempts,
            evaluations,
            attempts.Count > 1,
            Improved(attempts),
            attempts.Count > 1 && attempts[^1].TrustedCheckPassed == true);
    }

    private void Emit(ReflectionStep step)
    {
        try
        {
            _options.OnStep?.Invoke(step);
        }
        catch (Exception)
        {
            // Observing the loop never changes it.
        }
    }

    private static async ValueTask<ReflectionAttemptOutcome> RunAttemptAsync(ReflectionAttemptDelegate attempt, ReflectionAttemptInput input, CancellationToken ct)
    {
        try
        {
            return await attempt(input, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The delegate should describe a failure rather than throw; if it throws, only the type name is kept.
            return new ReflectionAttemptOutcome(false, ex.GetType().Name, null, []);
        }
    }

    private async ValueTask<ReflectionEvaluation?> EvaluateAsync(ReflectionAttemptContext context, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.EvaluationTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var evaluation = await _evaluator!.EvaluateAsync(context, linked.Token).ConfigureAwait(false);
            return ReflectionEvaluationValidator.Validate(evaluation, context, _options.MaxCorrectionLength) is null ? evaluation : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The correction from the replanning strategy, bounded and screened like the evaluator's own text. A throw or an invalid plan fails the evaluation.</summary>
    private async ValueTask<(string? Note, bool Failed)> ReplanAsync(ReflectionAttemptContext context, ReflectionEvaluation evaluation, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.EvaluationTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var note = await _replanning.ReplanAsync(context, evaluation, linked.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(note)) { return (null, false); }
            return ReflectionEvaluationValidator.ValidateCorrection(note, context, _options.MaxCorrectionLength) is null ? (note, false) : (null, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, true);
        }
    }

    private async ValueTask<bool> AuthorizeAsync(ReflectionAttemptContext context, ReflectionEvaluation evaluation, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.EvaluationTimeout, _options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await _authorizer.AuthorizeAsync(context, evaluation, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Improved(List<ReflectionAttemptOutcome> attempts)
    {
        for (var i = 1; i < attempts.Count; i++)
        {
            var (before, after) = (attempts[i - 1], attempts[i]);
            if ((!before.Completed && after.Completed) || after.FailedCalls.Count < before.FailedCalls.Count || (before.TrustedCheckPassed != true && after.TrustedCheckPassed == true))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string strategy)
    {
        var sb = new StringBuilder(strategy.Length);
        var space = false;
        foreach (var c in strategy.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) { sb.Append(' '); }
            space = false;
            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }
}
