using System.Globalization;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.ReflectiveExecution;

namespace AgentExperience.MicrosoftAgentFramework.ReflectiveExecution;

/// <summary>
/// The per-container state of reflective execution: the host's options, the capture the one-call setup built (bound
/// when <c>UseAgentExperience</c> runs), and the composition that lets one run hold several attempts.
/// </summary>
internal sealed class ReflectiveExecutionRegistration(ReflectiveExecutionOptions options)
{
    private ExperienceCaptureMiddleware? _capture;
    private IExperienceCaptureService? _service;

    internal ReflectiveExecutionOptions Options { get; } = options;

    /// <summary>Whether <c>UseAgentExperience</c> has bound its capture.</summary>
    internal bool IsBound => _capture is not null;

    internal IExperienceCaptureService? Service => _service;

    internal void Bind(ExperienceCaptureMiddleware capture, IExperienceCaptureService service)
    {
        _capture = capture;
        _service = service;
    }

    /// <summary>Closes the open run with the last attempt's status; a no-op when capture closed it already.</summary>
    internal Task CloseRunAsync(Guid runId, RunExecutionStatus status) =>
        _capture?.OpenRuns.CloseRunAsync(runId, status) ?? Task.CompletedTask;

    /// <summary>
    /// Wraps the capture options' <c>ResolveRun</c> and <c>ShouldCompleteRun</c> so that, inside a reflective execution,
    /// each attempt continues the run the first one opened and the run stays open until the coordinator closes it.
    /// Outside one, both behave exactly as the host configured them.
    /// </summary>
    internal void Compose(ExperienceCaptureOptions capture)
    {
        if (!Options.Limits.Enabled)
        {
            return;
        }

        Options.Limits.Validate();
        if (Options.Limits.MaxAttempts > capture.MaxAttemptsPerOpenRun)
        {
            throw new InvalidOperationException(
                $"Reflective execution allows {Options.Limits.MaxAttempts} attempts, but capture holds a run open for at most "
                + $"{capture.MaxAttemptsPerOpenRun.ToString(CultureInfo.InvariantCulture)} ({nameof(ExperienceCaptureOptions.MaxAttemptsPerOpenRun)}). Lower MaxAttempts or raise that bound through AgentExperienceOptions.Capture.");
        }

        if (Options.Limits.MaxDuration > capture.MaxOpenRunDuration)
        {
            throw new InvalidOperationException(
                $"Reflective execution may run for {Options.Limits.MaxDuration}, but capture holds a run open for at most {capture.MaxOpenRunDuration} "
                + $"({nameof(ExperienceCaptureOptions.MaxOpenRunDuration)}). Lower MaxDuration or raise that bound through AgentExperienceOptions.Capture.");
        }

        var resolve = capture.ResolveRun;
        var complete = capture.ShouldCompleteRun;
        capture.ResolveRun = context =>
        {
            var descriptor = resolve(context);
            return ReflectiveRunState.Current is { RunId: { } runId } ? descriptor with { ContinuesRunId = runId } : descriptor;
        };
        capture.ShouldCompleteRun = context =>
            ReflectiveRunState.Current is { } state ? state.CloseAfterThisAttempt : complete(context);
    }
}

/// <summary>The reflective execution an invocation is running inside, flowed to the capture callbacks.</summary>
internal sealed class ReflectiveRunState(Guid? runId, bool closeAfterThisAttempt)
{
    private static readonly AsyncLocal<ReflectiveRunState?> Slot = new();

    internal static ReflectiveRunState? Current => Slot.Value;

    internal Guid? RunId { get; } = runId;

    internal bool CloseAfterThisAttempt { get; } = closeAfterThisAttempt;

    internal static void Enter(ReflectiveRunState state) => Slot.Value = state;

    internal static void Leave() => Slot.Value = null;
}
