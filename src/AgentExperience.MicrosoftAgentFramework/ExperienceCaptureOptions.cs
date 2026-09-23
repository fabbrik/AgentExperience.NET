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
/// <param name="ContinuesRunId">
/// Optional. The run this invocation is a further <see cref="Attempt"/> of, rather than a run of its
/// own. Leave it <see langword="null"/> -- the default -- and the invocation opens a brand-new run
/// under a freshly minted identifier, which is the behaviour every host had before this field
/// existed.
/// </param>
/// <remarks>
/// <para>
/// <b>What <see cref="ContinuesRunId"/> is for.</b> One MAF invocation is one attempt. A host that
/// retries a task -- the whole point of learning from failure -- would otherwise produce two
/// unrelated runs: the failed one finalizes on its own and the successful one is reflected on as if
/// the failure never happened. Naming the first run's identifier here makes the retry a second
/// attempt <em>of that run</em>, with the next <see cref="Attempt.SequenceNumber"/>, so the
/// reflection sees the failure and the success together.
/// </para>
/// <para>
/// <b>It is an explicit identifier, not session identity.</b> Keying continuation on
/// <see cref="AgentSession"/> would silently group two unrelated tasks that happened to share a
/// session -- and injection already documents that blocks accumulate in a reused session, so a
/// session is not a task. The host says what it means instead. The identifier of the run an
/// invocation opened is readable afterwards from
/// <see cref="ExperienceCaptureAgentBuilderExtensions.RunIdStateKey"/> when a session was supplied.
/// </para>
/// <para>
/// <b>It is refused when it is not the same run.</b> An identifier naming a run with a different
/// task or scope, or a run that has already been completed, is a conflict: the invocation runs
/// uncaptured and the refusal is reported through <see cref="ExperienceCaptureOptions.OnCaptureFailure"/>,
/// exactly as a colliding identifier is today. An identifier naming no run at all simply opens a new
/// run under it. An all-zeros identifier -- a default-valued field rather than a run -- is refused
/// outright, so a forgotten assignment cannot quietly accumulate every invocation onto one run. Two
/// invocations racing on the same identifier are serialized by the adapter: one captures and the
/// other is refused, so a run never accumulates two half-recorded attempts at once.
/// </para>
/// <para>
/// <b>What a continuation discards, and why that is the price of reusing an identifier.</b> A
/// continuation never writes to the run it joins: the run keeps the <see cref="TaskDescription"/>,
/// <see cref="EnvironmentFingerprint"/>, <see cref="Provenance"/> -- including its
/// <see cref="Provenance.CorrelationId"/> -- and start time it was <em>opened</em> with, and this
/// invocation's own are silently dropped. That is deliberate: a continuation that overwrote them
/// would rewrite the history of a run already holding attempts. It also means an identifier reused
/// by accident is merged rather than refused, and the two invocations are reflected on as one run.
/// Match on task and scope is all that stands between the two cases, and the in-memory capture
/// service keeps every run it has seen for the process lifetime, so an accidental reuse of an
/// identifier whose run is still open is merged for as long as that run stays open, and one whose
/// run has closed is refused for as long as the process runs. Mint the identifier per retry cycle, and start the next cycle with a new one.
/// </para>
/// </remarks>
public sealed record ExperienceRunDescriptor(
    string TaskId,
    Scope Scope,
    string? TaskDescription = null,
    Guid? ContinuesRunId = null);

/// <summary>
/// What the host sees when it is asked whether this invocation is the last attempt of its run -- so
/// the run should be completed now -- or whether the run should stay open for a further attempt.
/// </summary>
/// <param name="RunId">The run this invocation captured an attempt on.</param>
/// <param name="TaskId">The run's task identifier, as this invocation's descriptor gave it.</param>
/// <param name="Scope">The run's scope, as this invocation's descriptor gave it.</param>
/// <param name="ExecutionStatus">
/// How this invocation finished mechanically. It is not a verdict on the task: a run that completed
/// without throwing may still have failed at what it was asked to do.
/// </param>
/// <param name="AttemptCount">
/// How many attempts the run holds, this invocation's included, as the capture service reports it.
/// </param>
/// <param name="OpenFor">How long the run has been open, measured from when it was first started.</param>
/// <param name="Result">
/// The sanitized text this invocation produced, as it was recorded on the attempt, or
/// <see langword="null"/> when the invocation did not complete. This is what lets a host decide
/// "good enough" in the same step rather than having to declare its intention in advance.
/// </param>
/// <param name="Error">
/// The failure recorded on the attempt -- an exception's type name, never its message -- or
/// <see langword="null"/> when the invocation did not fail.
/// </param>
public sealed record ExperienceRunCompletionContext(
    Guid RunId,
    string TaskId,
    Scope Scope,
    RunExecutionStatus ExecutionStatus,
    int AttemptCount,
    TimeSpan OpenFor,
    string? Result,
    string? Error);

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
/// Host configuration for <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture(Microsoft.Agents.AI.AIAgentBuilder, AgentExperience.Core.Capture.IExperienceCaptureService, ExperienceCaptureOptions)"/>.
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
    /// Decides whether this invocation's run is finished. Called once per captured invocation, after
    /// its attempt was recorded and before the run would be completed. Returning
    /// <see langword="true"/> -- the default, and what every host got before this existed -- completes
    /// the run, which is final: no further attempt can ever be appended to it. Returning
    /// <see langword="false"/> leaves the run open so a later invocation naming it through
    /// <see cref="ExperienceRunDescriptor.ContinuesRunId"/> becomes its next attempt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An open run is bounded, always.</b> It holds this run's captured payload in memory, so
    /// "keep it open" is never open-ended: the run is completed anyway, and the reason reported
    /// through <see cref="OnCaptureFailure"/>, when it reaches <see cref="MaxAttemptsPerOpenRun"/>
    /// attempts, when it has been open for <see cref="MaxOpenRunDuration"/>, when this invocation's
    /// attempt could not be recorded at all, or when this predicate itself throws. A host that
    /// forgets to close a run cannot leave one open.
    /// </para>
    /// <para>
    /// <b>Leaving a run open defers finalization with it.</b> An open run is not handed to
    /// <see cref="FinalizationService"/> -- only a completed run can become a durable Experience
    /// Record -- so nothing is finalized until the invocation that closes it.
    /// </para>
    /// <para>
    /// Exceptions are never thrown into MAF or the caller: a throwing predicate completes the run and
    /// is reported through <see cref="OnCaptureFailure"/>, because failing closed here is the option
    /// that cannot retain payload.
    /// </para>
    /// </remarks>
    public Func<ExperienceRunCompletionContext, bool> ShouldCompleteRun { get; init; } = static _ => true;

    /// <summary>
    /// The upper bound on how long a run may stay open across invocations before the adapter
    /// completes it itself and reports through <see cref="OnCaptureFailure"/>. Default 5 minutes.
    /// Must be positive and at most <see cref="uint.MaxValue"/> - 1 milliseconds.
    /// </summary>
    /// <remarks>
    /// Enforced two ways, so neither a host that keeps invoking nor a host that walks away can defeat
    /// it: it is checked when each invocation finishes, and a timer armed on
    /// <see cref="TimeProvider"/> closes a run that no further invocation ever arrives for. It has no
    /// effect at all on the default behaviour, where every invocation completes its own run and no
    /// run is ever left open.
    /// </remarks>
    public TimeSpan MaxOpenRunDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The most attempts a run may accumulate before the adapter completes it itself and reports
    /// through <see cref="OnCaptureFailure"/>, whatever <see cref="ShouldCompleteRun"/> says.
    /// Default 8. Must be positive.
    /// </summary>
    /// <remarks>
    /// This is the adapter's own bound on a run it is keeping open, and it is separate from the
    /// capture service's <see cref="AgentExperience.Core.Capture.CaptureLimits.MaxAttemptsPerRun"/>,
    /// which bounds what the service will store at all. Whichever is reached first ends the run:
    /// hitting the service's limit means the attempt was not recorded, and an unrecordable attempt
    /// also completes the run rather than leaving it open to collect more of them.
    /// </remarks>
    public int MaxAttemptsPerOpenRun { get; init; } = 8;

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
    /// <remarks>
    /// <b>The thread contract.</b> Most calls arrive on the invocation's own thread, before
    /// <c>RunAsync</c> returns. A run closed by <see cref="MaxOpenRunDuration"/> is the exception:
    /// that close runs from a <see cref="TimeProvider"/> timer callback on a thread-pool thread,
    /// after the invocation that opened the run has returned and after any number of later
    /// invocations. The callback must therefore be thread-safe and must tolerate being called when no
    /// invocation is in flight. Dispose the <c>captureLifetime</c> handle
    /// <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture(Microsoft.Agents.AI.AIAgentBuilder, AgentExperience.Core.Capture.IExperienceCaptureService, ExperienceCaptureOptions, out IDisposable)"/>
    /// hands back to stop those late calls before tearing down whatever the callback writes to.
    /// </remarks>
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
    /// <remarks>
    /// It carries the same thread contract as <see cref="OnCaptureFailure"/>: a run completed at its
    /// <see cref="MaxOpenRunDuration"/> bound is finalized from a <see cref="TimeProvider"/> timer
    /// callback on a thread-pool thread, with no invocation in flight.
    /// </remarks>
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
