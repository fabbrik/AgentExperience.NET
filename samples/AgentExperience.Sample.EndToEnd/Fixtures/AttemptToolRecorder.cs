using System.Text.Json;
using AgentExperience.Core.Capture;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.Sample.EndToEnd.Fixtures;

/// <summary>
/// MAF function middleware that buffers one attempt's tool calls as
/// <see cref="RawToolCall"/>s, so the host can hand them to
/// <c>IExperienceCaptureService.AppendAttemptAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not something a host normally writes.</b>
/// <c>AIAgentBuilder.UseExperienceCapture(...)</c> does exactly this, and more, for you -- when one
/// invocation is one attempt. This sample's first run is deliberately <em>one</em> Experience Run
/// with <em>two</em> attempts, which the capture contract models as two
/// <c>AppendAttemptAsync</c> calls on one run, so the host here drives capture itself and records
/// the tool calls for each attempt as it goes. The sample's second run is wrapped with
/// <c>UseExperienceCapture</c> in the ordinary way.
/// </para>
/// <para>
/// It mirrors the adapter's own rules: a tool failure is recorded as the exception's type name and
/// never its message, and the raw result is converted to text the same way the adapter converts it.
/// </para>
/// </remarks>
internal sealed class AttemptToolRecorder(TimeProvider clock, Func<Guid> newId)
{
    private readonly List<RawToolCall> _calls = [];
    private readonly List<object?> _results = [];

    /// <summary>Every tool call observed on this attempt, in the order they started.</summary>
    public IReadOnlyList<RawToolCall> Calls => _calls;

    /// <summary>The raw object the last tool returned, so the host can read the check's own exit code off it.</summary>
    public object? LastResult => _results.Count == 0 ? null : _results[^1];

    /// <summary>Records one tool call, then returns (or rethrows) exactly what the tool produced.</summary>
    /// <param name="agent">The agent MAF is invoking the tool for. Unused; part of the middleware signature.</param>
    /// <param name="context">The invocation MAF is about to make.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="cancellationToken">Cancels the tool call.</param>
    public async ValueTask<object?> InvokeAsync(
        AIAgent agent,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var toolCallId = newId();
        var startedAt = clock.GetUtcNow();
        var startTimestamp = clock.GetTimestamp();
        var arguments = context.Arguments is { } raw
            ? new Dictionary<string, object?>(raw, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        object? result = null;
        string? error = null;
        try
        {
            result = await next(context, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception ex)
        {
            // The type name only: a tool's exception message is caller data and never belongs in a record.
            error = ex.GetType().FullName ?? ex.GetType().Name;
            throw;
        }
        finally
        {
            _calls.Add(new RawToolCall(
                ToolCallId: toolCallId,
                ToolName: context.Function?.Name ?? context.CallContent?.Name ?? string.Empty,
                Arguments: arguments,
                StartedAt: startedAt,
                Duration: clock.GetElapsedTime(startTimestamp),
                Result: error is null ? AsText(result) : null,
                Error: error));
            _results.Add(result);
        }
    }

    private static string? AsText(object? result) => result switch
    {
        null => null,
        string value => value,
        JsonElement element => element.ToString(),
        _ => JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions),
    };
}
