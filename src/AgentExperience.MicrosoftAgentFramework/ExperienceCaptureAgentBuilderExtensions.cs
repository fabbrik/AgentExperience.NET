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
    /// Captures every invocation of the agent being built as one <see cref="ExperienceRun"/> with one
    /// <see cref="Attempt"/>, through <paramref name="captureService"/> (so its sanitization and limits
    /// apply unchanged). Registers agent-run middleware -- which opens the run before the inner agent
    /// executes and completes it exactly once on success, failure, cancellation, or a streaming
    /// consumer that stops reading early -- and then, when
    /// <see cref="ExperienceCaptureOptions.CaptureToolCalls"/> is <see langword="true"/>, function
    /// middleware that records each tool call into that run.
    /// </summary>
    /// <param name="builder">The agent builder.</param>
    /// <param name="captureService">The capture service runs are recorded through.</param>
    /// <param name="options">Host configuration.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <remarks>
    /// Call this first on the builder so capture is the outermost layer. Capture never alters the
    /// agent's response, streaming updates, or exception, and never wraps or re-executes tools.
    /// Tool capture requires the inner agent to expose a <see cref="FunctionInvokingChatClient"/>
    /// (a <see cref="ChatClientAgent"/>); <see cref="AIAgentBuilder.Build"/> throws otherwise.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument, or <see cref="ExperienceCaptureOptions.ResolveRun"/>, <see cref="ExperienceCaptureOptions.Environment"/>, <see cref="ExperienceCaptureOptions.TimeProvider"/>, or <see cref="ExperienceCaptureOptions.NewId"/>, is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceCaptureOptions.FinalizationTimeout"/> is not positive or exceeds the timer maximum.</exception>
    public static AIAgentBuilder UseExperienceCapture(
        this AIAgentBuilder builder,
        IExperienceCaptureService captureService,
        ExperienceCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(captureService);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ResolveRun, nameof(options.ResolveRun));
        ArgumentNullException.ThrowIfNull(options.Environment, nameof(options.Environment));
        ArgumentNullException.ThrowIfNull(options.TimeProvider, nameof(options.TimeProvider));
        ArgumentNullException.ThrowIfNull(options.NewId, nameof(options.NewId));
        if (options.FinalizationTimeout <= TimeSpan.Zero || options.FinalizationTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FinalizationTimeout, $"FinalizationTimeout must be positive and at most {uint.MaxValue - 1} milliseconds.");
        }

        var middleware = new ExperienceCaptureMiddleware(captureService, options);

        // The first Use call is the outermost layer: run middleware wraps function middleware.
        builder.Use(middleware.RunAsync, middleware.RunStreamingAsync);

        if (options.CaptureToolCalls)
        {
            builder.Use(ExperienceCaptureMiddleware.InvokeFunctionAsync);
        }

        return builder;
    }
}

/// <summary>The run and function middleware delegates registered by <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture"/>.</summary>
internal sealed class ExperienceCaptureMiddleware(IExperienceCaptureService captureService, ExperienceCaptureOptions options)
{
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
        var scope = CaptureScope.TryBegin(captureService, options, messages, session, innerAgent);

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
        var scope = CaptureScope.TryBegin(captureService, options, messages, session, innerAgent);
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
