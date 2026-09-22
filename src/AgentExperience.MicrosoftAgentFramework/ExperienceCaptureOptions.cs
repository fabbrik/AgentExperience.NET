using System.Runtime.InteropServices;
using AgentExperience.Abstractions;
using AgentExperience.Core.Finalization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// What the host sees when it is asked to describe a MAF invocation as an Experience Run: the
/// invocation's input messages, the caller-supplied session (if any), and the agent the capture
/// middleware wraps.
/// </summary>
/// <param name="Messages">
/// The invocation's input messages, materialized once by capture; the inner agent receives this
/// same collection, so enumerating it does not consume the caller's sequence.
/// </param>
/// <param name="Session">The caller-supplied session, or <see langword="null"/> when the caller passed none.</param>
/// <param name="Agent">The inner agent the capture middleware wraps.</param>
public sealed record ExperienceRunContext(
    IEnumerable<ChatMessage> Messages,
    AgentSession? Session,
    AIAgent Agent);

/// <summary>
/// The host's description of one MAF invocation, used to open its <see cref="ExperienceRun"/>.
/// </summary>
/// <param name="TaskId">Identifies which task the invocation is attempting.</param>
/// <param name="Scope">The tenancy/ownership scope the run belongs to. Host-established, never taken from model output.</param>
/// <param name="TaskDescription">Optional human-readable description of the task.</param>
public sealed record ExperienceRunDescriptor(
    string TaskId,
    Scope Scope,
    string? TaskDescription = null);

/// <summary>
/// What the host sees when it is asked how a completed, captured run should be finalized into a
/// durable Experience Record.
/// </summary>
/// <param name="Run">
/// The completed run's sanitized snapshot, read back from the capture service after the invocation's
/// attempt and completion were recorded. Its <see cref="ExperienceRun.ExecutionStatus"/> is set.
/// </param>
public sealed record ExperienceFinalizationContext(ExperienceRun Run);

/// <summary>Where in the capture pipeline an <see cref="ExperienceCaptureFailure"/> happened.</summary>
public enum ExperienceCaptureFailureStage
{
    /// <summary><see cref="ExperienceCaptureOptions.ResolveRun"/> threw or returned an invalid descriptor.</summary>
    ResolveRun,

    /// <summary><see cref="AgentExperience.Core.Capture.IExperienceCaptureService.StartRun"/> threw or did not start the run.</summary>
    StartRun,

    /// <summary>Appending the attempt or completing the run threw, returned a non-success outcome, or timed out.</summary>
    Finalize,

    /// <summary>Capturing a tool call's start, result, or error threw; that tool call is not recorded.</summary>
    ToolCall,

    /// <summary>
    /// Finalizing the completed run into a durable Experience Record threw, was declined for a
    /// foreign run ID, or failed a stage. A host decision (storage denied, or a scope outside the
    /// authorization) is not reported here -- it is an expected outcome on
    /// <see cref="ExperienceCaptureOptions.OnRunFinalized"/>. The captured run is unchanged and still
    /// available for the host to retry.
    /// </summary>
    Finalization,
}

/// <summary>
/// A capture problem reported to <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>. Capture
/// failures never change what the caller of the agent observes.
/// </summary>
/// <param name="Stage">Where the failure happened.</param>
/// <param name="RunId">The run the failure belongs to, when one had been assigned.</param>
/// <param name="Reason">A content-free explanation of what went wrong.</param>
/// <param name="Exception">The exception behind the failure, if any. Handed to the host only; never captured into a run.</param>
public sealed record ExperienceCaptureFailure(
    ExperienceCaptureFailureStage Stage,
    Guid? RunId,
    string Reason,
    Exception? Exception);

/// <summary>
/// Host configuration for <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture"/>.
/// </summary>
public sealed class ExperienceCaptureOptions
{
    /// <summary>
    /// Describes each invocation as an Experience Run (task ID, scope, task description). Called once
    /// per invocation, before the inner agent executes. If it throws, the invocation runs uncaptured
    /// and the failure is reported through <see cref="OnCaptureFailure"/>.
    /// </summary>
    public required Func<ExperienceRunContext, ExperienceRunDescriptor> ResolveRun { get; init; }

    /// <summary>
    /// The environment fingerprint recorded on every run. Defaults to the current machine name plus
    /// <see cref="RuntimeInformation"/>'s framework, OS, and process-architecture descriptions.
    /// </summary>
    public EnvironmentFingerprint Environment { get; init; } = CreateDefaultEnvironment();

    /// <summary>
    /// Whether tool calls are recorded (default <see langword="true"/>). Tool capture registers MAF
    /// function middleware, which requires the wrapped agent to expose a
    /// <see cref="FunctionInvokingChatClient"/> (a <see cref="ChatClientAgent"/>). Set this to
    /// <see langword="false"/> to capture run lifecycle only for any other <see cref="AIAgent"/>.
    /// </summary>
    public bool CaptureToolCalls { get; init; } = true;

    /// <summary>
    /// The upper bound on the whole post-invocation step per run: append the attempt, complete the
    /// run, and -- when <see cref="FinalizationService"/> is configured -- finalize it into a durable
    /// Experience Record. Default 5 seconds. Must be positive and at most
    /// <see cref="uint.MaxValue"/> - 1 milliseconds. It never uses the caller's cancellation token.
    /// </summary>
    /// <remarks>
    /// With finalization configured this bound covers database round trips, not just in-memory
    /// capture, so 5 seconds may be too tight for a slow or distant database. A timeout is reported
    /// through <see cref="OnCaptureFailure"/> and can leave the Experience Record created but not yet
    /// confirmed -- a <c>Candidate</c>, which is never reusable. Finalizing that run again completes
    /// the same commit.
    /// </remarks>
    public TimeSpan FinalizationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Called for capture failures (resolver exception, non-success capture outcome, capture
    /// exception, or finalization timeout), at most once per <see cref="ExperienceCaptureFailureStage"/>
    /// per run. Exceptions thrown by the callback are swallowed.
    /// </summary>
    public Action<ExperienceCaptureFailure>? OnCaptureFailure { get; init; }

    /// <summary>
    /// Optional. The Core service that turns each completed, captured run into a durable Experience
    /// Record. Leave it <see langword="null"/> to capture only -- the host can still finalize runs
    /// itself, whenever it likes, from the capture service's snapshot.
    /// </summary>
    /// <remarks>
    /// Setting this requires <see cref="ResolveFinalization"/> too (and vice versa), because only the
    /// host knows a run's required checks, its verification evidence, its authorization context, and
    /// its storage policy. Finalization runs inside the same once-only, <see cref="FinalizationTimeout"/>-bounded
    /// step as capture finalization, after the run's attempt and completion were recorded, and only
    /// when both of those succeeded. It never uses the caller's cancellation token and never changes
    /// what the caller of the agent observes.
    /// </remarks>
    public ExperienceFinalizationService? FinalizationService { get; init; }

    /// <summary>
    /// Required when <see cref="FinalizationService"/> is set (and only valid then): builds the
    /// finalize request for one completed run. Returning <see langword="null"/> skips finalizing that
    /// run. The request's <c>RunId</c> must be this invocation's run. If it throws, or returns a
    /// request for another run, the run is not finalized and the failure is reported through
    /// <see cref="OnCaptureFailure"/>.
    /// </summary>
    public Func<ExperienceFinalizationContext, FinalizeExperienceRequest?>? ResolveFinalization { get; init; }

    /// <summary>
    /// Optional. Receives every finalization result, durable or not -- including an expected
    /// <see cref="FinalizationOutcome.StorageDenied"/> or
    /// <see cref="FinalizationOutcome.NotAuthorized"/>, which are host decisions rather than capture
    /// failures and are therefore not reported through <see cref="OnCaptureFailure"/>. Not called when
    /// finalization already overran <see cref="FinalizationTimeout"/>. Exceptions thrown by the
    /// callback are swallowed.
    /// </summary>
    public Action<FinalizeExperienceResult>? OnRunFinalized { get; init; }

    /// <summary>The clock used for run, attempt, and tool-call timestamps, durations, and the finalization timeout.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Creates run, attempt, tool-call, and completion-event identifiers. Defaults to <see cref="Guid.NewGuid"/>. Must be thread-safe.</summary>
    public Func<Guid> NewId { get; init; } = Guid.NewGuid;

    private static EnvironmentFingerprint CreateDefaultEnvironment() => new(
        HostName: System.Environment.MachineName,
        RuntimeVersion: RuntimeInformation.FrameworkDescription,
        OperatingSystem: RuntimeInformation.OSDescription,
        ApplicationVersion: null,
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProcessArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        });
}
