using System.Runtime.CompilerServices;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Registers AgentExperience.NET capture on a Microsoft Agent Framework agent pipeline.
/// </summary>
public static class ExperienceCaptureAgentBuilderExtensions
{
    /// <summary>
    /// The single <see cref="AgentSession.StateBag"/> key capture writes to when the caller supplies a
    /// session. Its value is the invocation's run ID as a <c>"D"</c>-formatted <see cref="Guid"/> string.
    /// </summary>
    public const string RunIdStateKey = "AgentExperience.RunId";

    /// <summary>
    /// Captures every invocation of the agent being built as one <see cref="Attempt"/> of an
    /// <see cref="ExperienceRun"/>, through <paramref name="captureService"/> (so its sanitization and
    /// limits apply unchanged). By default that run is this one invocation's and is completed with it,
    /// which is what every host got before continuation existed; a host that returns an
    /// <see cref="ExperienceRunDescriptor.ContinuesRunId"/> and keeps the run open through
    /// <see cref="ExperienceCaptureOptions.ShouldCompleteRun"/> instead accumulates several
    /// invocations as several attempts of one run, so a retry is captured as a retry rather than as an
    /// unrelated second run. Registers agent-run middleware -- which opens or continues the run before
    /// the inner agent executes and finalizes exactly once on success, failure, cancellation, or a
    /// streaming consumer that stops reading early -- and then, when
    /// <see cref="ExperienceCaptureOptions.CaptureToolCalls"/> is <see langword="true"/>, function
    /// middleware that records each tool call into that run. When
    /// <see cref="ExperienceCaptureOptions.FinalizationService"/> and
    /// <see cref="ExperienceCaptureOptions.ResolveFinalization"/> are configured, each successfully
    /// captured run is then handed to Core's finalization service to become a durable Experience
    /// Record, inside the same timeout-bounded step.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="captureService">The capture service runs are recorded through.</param>
    /// <param name="options">Host configuration.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Call this first on the builder so capture is the outermost layer. Capture never alters the
    /// agent's response, streaming updates, or exception, and never wraps or re-executes tools.
    /// Tool capture requires the inner agent to expose a <see cref="FunctionInvokingChatClient"/>
    /// (a <see cref="ChatClientAgent"/>); <see cref="AIAgentBuilder.Build"/> throws otherwise.
    /// </para>
    /// <para>
    /// A host that keeps runs open -- and therefore arms open-run duration bounds on its
    /// <see cref="ExperienceCaptureOptions.TimeProvider"/> -- should use the
    /// <see cref="UseExperienceCapture(AIAgentBuilder, IExperienceCaptureService, ExperienceCaptureOptions, out IDisposable)"/>
    /// overload and dispose what it hands back when the agent is torn down. MAF's
    /// <see cref="AIAgent"/> is not itself disposable, so this overload has nothing to attach a
    /// teardown to: its bounds outlive the agent and can complete a run, and call
    /// <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>/<see cref="ExperienceCaptureOptions.OnRunFinalized"/>,
    /// afterwards. For the default configuration -- every invocation completes its own run -- no
    /// bound is ever armed and there is nothing to dispose: each invocation still takes a transient
    /// claim in the registration's open-run ledger while it is in flight, so a second invocation
    /// naming its run is refused, but the claim is removed when the invocation releases the run and
    /// nothing outlives it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument, or <see cref="ExperienceCaptureOptions.ResolveRun"/>, <see cref="ExperienceCaptureOptions.Environment"/>, <see cref="ExperienceCaptureOptions.TimeProvider"/>, <see cref="ExperienceCaptureOptions.NewId"/>, or <see cref="ExperienceCaptureOptions.ShouldCompleteRun"/>, is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Exactly one of <see cref="ExperienceCaptureOptions.FinalizationService"/> and <see cref="ExperienceCaptureOptions.ResolveFinalization"/> is set.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceCaptureOptions.FinalizationTimeout"/> or <see cref="ExperienceCaptureOptions.MaxOpenRunDuration"/> is not positive or exceeds the timer maximum, or <see cref="ExperienceCaptureOptions.MaxAttemptsPerOpenRun"/> is not positive.</exception>
    public static AIAgentBuilder UseExperienceCapture(
        this AIAgentBuilder builder,
        IExperienceCaptureService captureService,
        ExperienceCaptureOptions options) =>
        builder.UseExperienceCapture(captureService, options, out _);

    /// <summary>
    /// <see cref="UseExperienceCapture(AIAgentBuilder, IExperienceCaptureService, ExperienceCaptureOptions)"/>,
    /// plus the handle that ends this registration's capture.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="captureService">The capture service runs are recorded through.</param>
    /// <param name="options">Host configuration.</param>
    /// <param name="captureLifetime">
    /// Disposing this stops every open-run duration bound this registration armed, so no timer
    /// callback can complete a run, call <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>, or
    /// call <see cref="ExperienceCaptureOptions.OnRunFinalized"/> after the host has torn down the
    /// data source and logger those callbacks reach. A run still open at that moment is
    /// <em>abandoned</em>: nothing is written for it and no timer reports it, because the host that
    /// would receive the report is the one shutting down; an invocation still in flight at that moment
    /// reports the abandonment itself, on its own thread, as it returns. Complete the runs that matter first. After
    /// disposal, further invocations through this agent run uncaptured and say so through
    /// <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <inheritdoc cref="UseExperienceCapture(AIAgentBuilder, IExperienceCaptureService, ExperienceCaptureOptions)" path="/exception"/>
    public static AIAgentBuilder UseExperienceCapture(
        this AIAgentBuilder builder,
        IExperienceCaptureService captureService,
        ExperienceCaptureOptions options,
        out IDisposable captureLifetime)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(captureService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ResolveRun, nameof(options.ResolveRun));
        ArgumentNullException.ThrowIfNull(options.Environment, nameof(options.Environment));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options.TimeProvider));
        ArgumentNullException.ThrowIfNull(options.NewId, nameof(options.NewId));
        ArgumentNullException.ThrowIfNull(options.ShouldCompleteRun, nameof(options.ShouldCompleteRun));
        if (options.FinalizationTimeout <= TimeSpan.Zero || options.FinalizationTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FinalizationTimeout, $"FinalizationTimeout must be positive and at most {uint.MaxValue - 1} milliseconds.");
        }

        // An open run holds captured payload, so its two bounds are validated exactly as strictly as
        // the finalization timeout: a bound that could never be reached is not a bound.
        if (options.MaxOpenRunDuration <= TimeSpan.Zero || options.MaxOpenRunDuration.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxOpenRunDuration, $"MaxOpenRunDuration must be positive and at most {uint.MaxValue - 1} milliseconds.");
        }

        if (options.MaxAttemptsPerOpenRun <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxAttemptsPerOpenRun, "MaxAttemptsPerOpenRun must be positive.");
        }

        // Either half alone could only ever do nothing, silently -- and a resolver without a service is
        // the easier mistake to make. Only the host can supply a run's required checks, evidence,
        // authorization, and storage decision, so the two are configured together or not at all.
        if (options.FinalizationService is null != (options.ResolveFinalization is null))
        {
            throw new ArgumentException(
                $"{nameof(ExperienceCaptureOptions.FinalizationService)} and {nameof(ExperienceCaptureOptions.ResolveFinalization)} must be set together, or neither set.",
                nameof(options));
        }

        var middleware = new ExperienceCaptureMiddleware(captureService, options);
        captureLifetime = middleware;

        // The first Use call is the outermost layer: run middleware wraps function middleware.
        builder.Use(middleware.RunAsync, middleware.RunStreamingAsync);

        if (options.CaptureToolCalls)
        {
            builder.Use(ExperienceCaptureMiddleware.InvokeFunctionAsync);
        }

        return builder;
    }
}

/// <summary>
/// The run and function middleware delegates registered by
/// <c>UseExperienceCapture</c>, and the disposable lifetime of the registry behind them.
/// </summary>
internal sealed class ExperienceCaptureMiddleware(IExperienceCaptureService captureService, ExperienceCaptureOptions options) : IDisposable
{
    /// <summary>
    /// The runs this registration is capturing on, and holding open across invocations. One per
    /// <c>UseExperienceCapture</c> call, never static: two independently built agents do not share
    /// continuations, and a host that wants them to share one shares the registration.
    /// </summary>
    private readonly OpenRunRegistry _openRuns = new(captureService, options);

    /// <summary>This registration's open-run ledger, exposed to the adapter's own tests.</summary>
    internal OpenRunRegistry OpenRuns => _openRuns;

    /// <summary>Ends this registration's capture; see the <c>captureLifetime</c> parameter.</summary>
    public void Dispose() => _openRuns.Dispose();

    /// <summary>Non-streaming run middleware: open, run, finalize once, return or rethrow unchanged.</summary>
    public async Task<AgentResponse> RunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? runOptions,
        AIAgent innerAgent,
        CancellationToken cancellationToken)
    {
        // Materialized once so a host resolver enumerating a one-shot sequence cannot starve the inner agent.
        messages = Materialize(messages);
        var scope = CaptureScope.TryBegin(captureService, options, _openRuns, messages, session, innerAgent);

        // Set inside this async method, so the value flows into the inner agent and is not visible
        // to the caller once this method returns.
        CaptureScope.Current = scope;

        AgentResponse response;
        try
        {
            response = await innerAgent.RunAsync(messages, session, runOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (scope is not null)
            {
                await scope.FinalizeAsync(StatusOf(ex, cancellationToken), result: null, error: CaptureScope.ErrorOf(ex)).ConfigureAwait(false);
            }

            throw;
        }

        if (scope is not null)
        {
            await scope.FinalizeAsync(RunExecutionStatus.Completed, TextOf(response), error: null).ConfigureAwait(false);
        }

        return response;
    }

    /// <summary>
    /// Streaming run middleware: a hand-written async iterator that restores the capture scope before
    /// every inner <c>MoveNextAsync</c> and finalizes in <c>finally</c>, which also runs when the
    /// consumer disposes the enumerator early.
    /// </summary>
    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? runOptions,
        AIAgent innerAgent,
        CancellationToken cancellationToken) =>
        RunStreamingCoreAsync(messages, session, runOptions, innerAgent, cancellationToken);

    /// <summary>Function middleware: records one tool call into the current run; exceptions are recorded, then rethrown unchanged.</summary>
    public static async ValueTask<object?> InvokeFunctionAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var scope = CaptureScope.Current;
        var pending = scope?.BeginToolCall(context);
        if (scope is null || pending is null)
        {
            return await next(context, cancellationToken).ConfigureAwait(false);
        }

        object? result;
        try
        {
            result = await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            scope.FailToolCall(pending, ex);
            throw;
        }

        scope.CompleteToolCall(pending, result);
        return result;
    }

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? runOptions,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        messages = Materialize(messages);
        var scope = CaptureScope.TryBegin(captureService, options, _openRuns, messages, session, innerAgent);
        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        var text = scope is null ? null : new StringBuilder();

        // Until the inner stream ends or throws, finalization treats the run as abandoned by the consumer.
        var status = RunExecutionStatus.Cancelled;
        string? error = null;

        try
        {
            while (true)
            {
                AgentResponseUpdate update;

                // An async iterator's AsyncLocal changes do not survive between MoveNextAsync calls,
                // so the scope is restored before every inner step (where MAF runs the tools).
                CaptureScope.Current = scope;
                try
                {
                    enumerator ??= innerAgent.RunStreamingAsync(messages, session, runOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        status = RunExecutionStatus.Completed;
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception ex)
                {
                    status = StatusOf(ex, cancellationToken);
                    error = CaptureScope.ErrorOf(ex);
                    throw;
                }

                if (text is not null)
                {
                    AppendText(text, update);
                }

                yield return update;
            }
        }
        finally
        {
            try
            {
                if (enumerator is not null)
                {
                    CaptureScope.Current = scope;
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (scope is not null)
                {
                    var result = status == RunExecutionStatus.Completed ? text?.ToString() : null;
                    await scope.FinalizeAsync(status, result, error).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// Caller cancellation (an <see cref="OperationCanceledException"/> while the caller's token is
    /// cancelled) maps to <see cref="RunExecutionStatus.Cancelled"/>; every other exception, including
    /// a provider or HTTP timeout, maps to <see cref="RunExecutionStatus.Failed"/>.
    /// </summary>
    private static RunExecutionStatus StatusOf(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? RunExecutionStatus.Cancelled
            : RunExecutionStatus.Failed;

    private static IEnumerable<ChatMessage> Materialize(IEnumerable<ChatMessage> messages) =>
        messages is IReadOnlyCollection<ChatMessage> or IList<ChatMessage> ? messages : messages.ToList();

    private static string? TextOf(AgentResponse response)
    {
        try
        {
            return response.Text;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendText(StringBuilder text, AgentResponseUpdate update)
    {
        try
        {
            text.Append(update.Text);
        }
        catch
        {
            // Reading update text is capture-only; it must never affect the stream.
        }
    }
}
