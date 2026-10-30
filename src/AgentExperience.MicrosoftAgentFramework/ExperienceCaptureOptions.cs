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
    AIAgent Agent)
{
    private DerivedTaskTextCache? _derivedTaskText;

    /// <summary>A copy for <c>with</c>: the same members and a fresh, unread derived text.</summary>
    /// <param name="original">The context being copied.</param>
    private ExperienceRunContext(ExperienceRunContext original)
    {
        Messages = original.Messages;
        Session = original.Session;
        Agent = original.Agent;
        Identity = original.Identity;
    }

    /// <summary>
    /// The identity the one-call setup resolved for this invocation, before <see cref="ExperienceCaptureOptions.ResolveRun"/>
    /// is called; <see langword="null"/> under the explicit wiring. Internal: only the one-call setup's own resolver reads it.
    /// </summary>
    internal ExperienceIdentity? Identity { get; set; }

    /// <summary>
    /// The task text <see cref="ExperienceTaskText.Derive(IEnumerable{ChatMessage}, int)"/> derives from <see cref="Messages"/> (at most
    /// <see cref="ExperienceTaskText.DefaultMaxLength"/> UTF-16 code units), or <see langword="null"/> when no user
    /// message qualifies. Nothing uses it unless the host does: write
    /// <c>TaskDescription: context.DerivedTaskText</c> in <see cref="ExperienceCaptureOptions.ResolveRun"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only this invocation's input.</b> Capture sees the messages the caller passed, not the session's chat history,
    /// so a short follow-up is joined only to a previous user message in the same input; "and retry" sent on its own
    /// is derived as "and retry", where the injection provider, which sees the replayed history, derives
    /// "Deploy service X to prod — and retry". To describe the run the same way, pass the history the host keeps to
    /// <see cref="ExperienceTaskText.Derive(IEnumerable{ChatMessage}, int)"/> together with <see cref="Messages"/>.
    /// </para>
    /// <para>
    /// <b>Computed on first read.</b> <see cref="Messages"/> is snapshotted then, enumerated once, so a single-use
    /// sequence is not consumed twice by this property and a later change to the same list is not seen. It is computed
    /// at most once per instance (a <c>with</c> copy computes its own) and is part of neither equality nor
    /// <see cref="object.ToString"/>. If reading <see cref="Messages"/> throws, the first read throws that exception
    /// and every later read returns <see langword="null"/>.
    /// </para>
    /// <para>
    /// It is the user's own words: stored as <see cref="ExperienceRunDescriptor.TaskDescription"/> it is stored
    /// unredacted, as a task description always was, so a host with sensitive prompts should pass it through its own
    /// redaction first. See <see cref="ExperienceTaskText"/>.
    /// </para>
    /// </remarks>
    public string? DerivedTaskText => DerivedTaskTextCache.For(ref _derivedTaskText).Get(() => Messages);

    /// <summary>Equal when <see cref="Messages"/>, <see cref="Session"/> and <see cref="Agent"/> are.</summary>
    /// <param name="other">The context to compare with.</param>
    /// <returns><see langword="true"/> when the two contexts are equal.</returns>
    public bool Equals(ExperienceRunContext? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (EqualityComparer<IEnumerable<ChatMessage>>.Default.Equals(Messages, other.Messages)
                && EqualityComparer<AgentSession?>.Default.Equals(Session, other.Session)
                && EqualityComparer<AIAgent>.Default.Equals(Agent, other.Agent)));

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Messages, Session, Agent);

    // The derived text is the user's own words, so it stays out of ToString, which hosts log.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Messages = ").Append(Messages)
            .Append(", Session = ").Append(Session)
            .Append(", Agent = ").Append(Agent);
        return true;
    }
}

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
/// session -- and injection already documents that a reused session keeps every block injected into
/// it, so a session is not a task. The host says what it means instead. The identifier of the run an
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
public sealed record ExperienceFinalizationContext(ExperienceRun Run)
{
    /// <summary>
    /// The identity the one-call setup resolved for the invocation that ended the run, or <see langword="null"/> under
    /// the explicit wiring. Internal: the one-call setup's finalization takes its authorization from here.
    /// </summary>
    internal ExperienceIdentity? Identity { get; init; }
}

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

    /// <summary>
    /// Recording which stored records the <c>ExperienceContextProvider</c> delivered into the run threw or
    /// returned a non-success outcome (the run was already completed, was past
    /// <see cref="RunExposure.MaxPerRun"/> records, or the capture service does not record exposure). The
    /// injection itself is unaffected; confidence evidence about reusing those records in this run will be
    /// refused as not exposed.
    /// </summary>
    RecordExposure,

    /// <summary>
    /// The host's <see cref="ExperienceCaptureOptions.ClassifyToolResult"/> threw (the call is recorded as returned) or
    /// named a class that is not a single error-class token (the call is recorded as failed with
    /// <see cref="ExperienceCaptureOptions.ToolReportedErrorClass"/>). A stage of its own, so a classifier problem never
    /// hides a later <see cref="ToolCall"/> report of a tool call that was not recorded.
    /// </summary>
    ToolResultClassification,

    /// <summary>
    /// The one-call setup found an embedding generator it cannot use for semantic retrieval -- the storage has no
    /// embedding index (PostgreSQL without <c>.WithPostgresVectors()</c>), or the generator reports no model ID or
    /// dimension -- so retrieval stays text-only. Reported once per container, with no run ID, when the first agent is
    /// built with <c>UseAgentExperience</c>. See <c>AgentExperienceOptions.Embeddings</c>.
    /// </summary>
    Embeddings,

    /// <summary>
    /// Recall at the moment of failure is configured (<c>ExperienceInjectionOptions.Recall</c>) but has no failure
    /// signature source to look failures up in -- neither <c>ExperienceRecallOptions.SignatureSource</c> nor a record store
    /// that implements <see cref="IExperienceSignatureSource"/> -- so it does nothing. Reported once per context
    /// provider, on the first captured run it would have served.
    /// </summary>
    Recall,

    /// <summary>
    /// The host's <see cref="ExperienceCaptureOptions.RecipeResultExcerpt"/> threw: the call is recorded without an
    /// excerpt, exactly as it would be with no callback. Reported once per run, naming only the exception's type.
    /// </summary>
    RecipeExcerpt,
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
/// <remarks>
/// The properties are settable so a configuration hook (<c>AgentExperienceOptions.Capture</c>) can adjust them. Capture
/// takes a copy when <c>UseExperienceCapture</c> is called, so changing the options afterwards has no effect on an agent
/// already built.
/// </remarks>
public sealed class ExperienceCaptureOptions
{
    /// <summary>
    /// Describes each invocation as an Experience Run (task ID, scope, task description). Called once
    /// per invocation, before the inner agent executes. If it throws, the invocation runs uncaptured
    /// and the failure is reported through <see cref="OnCaptureFailure"/>.
    /// </summary>
    public required Func<ExperienceRunContext, ExperienceRunDescriptor> ResolveRun { get; set; }

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
    public Func<ExperienceRunCompletionContext, bool> ShouldCompleteRun { get; set; } = static _ => true;

    /// <summary>
    /// The upper bound on how long a run may stay open across invocations before the adapter
    /// completes it itself and reports through <see cref="OnCaptureFailure"/>. Default 5 minutes.
    /// Must be positive and at most <see cref="uint.MaxValue"/> - 1 milliseconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enforced two ways, so neither a host that keeps invoking nor a host that walks away can defeat
    /// it: it is checked when each invocation finishes, and a timer armed on
    /// <see cref="TimeProvider"/> the moment the run is opened closes a run that no further invocation
    /// ever arrives for -- or whose invocation never comes back.
    /// </para>
    /// <para>
    /// If the bound is reached while an invocation is still in flight on the run, the close is handed
    /// to that invocation and the timer re-arms for one further period of this length; if the
    /// invocation has still not returned when that period ends, the run is closed underneath it. So a
    /// run is never held for more than twice this duration (plus the close's own
    /// <see cref="FinalizationTimeout"/>) by an invocation that does not return -- unless
    /// <see cref="TimeProvider"/> cannot create the timer at all, which is reported when the run opens.
    /// This applies on the default behaviour too, where every invocation completes its own run: there
    /// the only effect is that an invocation still running at twice this duration has its run
    /// completed as <see cref="RunExecutionStatus.Cancelled"/> underneath it, reported, and its own
    /// attempt is then refused when it returns. Set it above the longest invocation you expect.
    /// </para>
    /// </remarks>
    public TimeSpan MaxOpenRunDuration { get; set; } = TimeSpan.FromMinutes(5);

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
    public int MaxAttemptsPerOpenRun { get; set; } = 8;

    /// <summary>
    /// The environment fingerprint recorded on every run. Defaults to the current machine name plus
    /// <see cref="RuntimeInformation"/>'s framework, OS, and process-architecture descriptions.
    /// </summary>
    public EnvironmentFingerprint Environment { get; set; } = CreateDefaultEnvironment();

    /// <summary>
    /// Whether tool calls are recorded (default <see langword="true"/>). Tool capture registers MAF
    /// function middleware, which requires the wrapped agent to expose a
    /// <see cref="FunctionInvokingChatClient"/> (a <see cref="ChatClientAgent"/>). Set this to
    /// <see langword="false"/> to capture run lifecycle only for any other <see cref="AIAgent"/>.
    /// </summary>
    public bool CaptureToolCalls { get; set; } = true;

    /// <summary>
    /// Optionally says that a tool call which <em>returned</em> failed: it gets the tool's name, its captured
    /// arguments and the raw result, and returns <see langword="null"/> when the call is recorded as returned, or an
    /// error class that is recorded as the call's <see cref="ToolCallRecord.Error"/>. The class must be one token a
    /// <c>Tried:</c> line can show: a type-like name ending in <c>Error</c> or <c>Exception</c> (<c>LockHeldError</c>,
    /// <c>ExitCodeError</c>) or the <c>exit N</c> form (<c>exit 3</c>); anything else is recorded as
    /// <see cref="ToolReportedErrorClass"/> (<c>ToolReportedError</c>). Default <see langword="null"/>: only a tool that throws is recorded as
    /// failed. Used only when <see cref="CaptureToolCalls"/> is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Many tools report failure in their return value -- an exit code, <c>{"ok":false}</c>, an HTTP status in the
    /// body. Without a classifier such a call is recorded as returned, and a later agent's <c>Tried:</c> line marks it
    /// <c>[returned]</c>. With one, the call is recorded exactly like a call that failed: its error is the class, its
    /// result is still the text the model saw, and the class reaches the provenance signature, the <c>Tried:</c>
    /// marker (<c>[failed: &lt;class&gt;]</c>) and both reflectors' input the way a thrown exception's type name does.
    /// </para>
    /// <para>
    /// The classifier runs only after the tool returned, and only for a call capture can still record; a tool that
    /// throws is recorded as before and the classifier is not called. It runs inline, before the result is returned to
    /// MAF and the model, so it adds its own time to every tool call: keep it fast. It never replaces the result, and
    /// nothing it does makes the tool call throw. Its context's dictionary is read-only, but <c>Result</c> and the
    /// argument values are the live objects the model works with, so it must not change them. The arguments are the
    /// raw, unsanitized ones -- secrets the model passed reach the classifier -- so it must not log or keep them.
    /// </para>
    /// <para>
    /// A classifier that throws leaves the call recorded as returned and is reported once per run through
    /// <see cref="OnCaptureFailure"/> at <see cref="ExperienceCaptureFailureStage.ToolResultClassification"/>: the
    /// failure's <c>Reason</c> names only the exception type, while its <c>Exception</c> carries the exception object,
    /// as for the other host callbacks.
    /// </para>
    /// <para>
    /// <b>The class must be one error-class token</b>, because it is stored, signed and rendered on <c>Tried:</c>
    /// lines: exactly the first token the <c>Tried:</c> line's error classifier would find in it, at most 40
    /// characters. Use a type-like name ending in <c>Error</c> or <c>Exception</c> (<c>LockHeldError</c>,
    /// <c>ExitCodeError</c>) or <c>exit N</c> (<c>exit 3</c>); <c>HTTP N</c>, <c>status N</c>, a POSIX errno name
    /// (<c>ENOENT</c>) and <c>Timeout</c> are tokens too. Surrounding white space is trimmed, but the class is
    /// case-sensitive and must be the exact normalized form: <c>exit 3</c>, not <c>Exit 3</c> or <c>exit=3</c>.
    /// Anything else -- blank, <c>exit code 3</c>, <c>ExitCode3</c>, a sentence, a bracket -- is recorded as
    /// <see cref="ToolReportedErrorClass"/> (rendered <c>[failed: ToolReportedError]</c>) and reported once per run
    /// through <see cref="OnCaptureFailure"/> at <see cref="ExperienceCaptureFailureStage.ToolResultClassification"/>,
    /// with a fixed reason that never quotes the class.
    /// </para>
    /// <para>
    /// <b>The thread contract.</b> It runs on the tool-invocation thread, inside MAF's function invocation, and can
    /// run concurrently with itself when MAF invokes tools concurrently. Keep it fast and free of shared mutable state.
    /// </para>
    /// </remarks>
    public Func<ExperienceToolResultContext, string?>? ClassifyToolResult { get; set; }

    /// <summary>
    /// The error class recorded for a returned tool call when <see cref="ClassifyToolResult"/> names a class that is not
    /// a single error-class token. It is a token itself, so a <c>Tried:</c> line shows it: <c>[failed: ToolReportedError]</c>.
    /// </summary>
    public const string ToolReportedErrorClass = "ToolReportedError";

    /// <summary>
    /// Optional, off by default: the <em>recipe</em>. For a tool call that returned and that capture records as returned
    /// (not failed, <see cref="ClassifyToolResult"/> included), it gets the same context the classifier gets -- the
    /// call, the tool, the raw arguments and the raw result -- and returns a short excerpt of the result the host
    /// vouches for as what the working call produced (the version that was pinned, the key that was set), or
    /// <see langword="null"/> for none. Used only when <see cref="CaptureToolCalls"/> is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What happens to the excerpt.</b> It is stored on the call as <see cref="ToolCallRecord.RecipeExcerpt"/> after
    /// the capture sanitizer's text rules (the <c>ToolResult</c> policy that results go through; a refused excerpt is
    /// dropped, never the call), cut to <c>CaptureLimits.MaxRecipeExcerptLength</c> characters (200 by default) after
    /// sanitization and never between a surrogate pair. It is signed with the record (claims version 4). A later
    /// agent sees it only on the <c>Worked:</c> attempt's line, as <c>tool(...) [returned: "excerpt"]</c>, and only for a
    /// tool its <c>ExperienceInjectionOptions.RecipeExcerpts</c> names; a failed call never has one. Nothing else of the
    /// result is ever shown. <c>AgentExperienceOptions.Recipe</c> sets this, the argument allowlists and the injection
    /// allowlist together.
    /// </para>
    /// <para>
    /// <b>Vouch only for what a later model may read.</b> The excerpt is text a later model is shown: return a value
    /// from a small, known shape (a version, a flag, a file name), never free text, personal data, or anything a secret
    /// could be written into. The sanitizer classifies by field name, not content.
    /// </para>
    /// <para>
    /// A callback that throws leaves the call recorded without an excerpt and is reported once per run through
    /// <see cref="OnCaptureFailure"/> at <see cref="ExperienceCaptureFailureStage.RecipeExcerpt"/>, the reason naming only the
    /// exception's type. It runs inline, on the tool-invocation thread, possibly concurrently with itself, and its
    /// context's values are the live objects the model works with: keep it fast, and change nothing.
    /// </para>
    /// </remarks>
    public Func<ExperienceToolResultContext, string?>? RecipeResultExcerpt { get; set; }

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
    public TimeSpan FinalizationTimeout { get; set; } = TimeSpan.FromSeconds(5);

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
    /// invocations -- or while an invocation that never returned is still hung on the run. The
    /// callback must therefore be thread-safe and must tolerate being called when no invocation is in
    /// flight. Dispose the <c>captureLifetime</c> handle
    /// <see cref="ExperienceCaptureAgentBuilderExtensions.UseExperienceCapture(Microsoft.Agents.AI.AIAgentBuilder, AgentExperience.Core.Capture.IExperienceCaptureService, ExperienceCaptureOptions, out IDisposable)"/>
    /// hands back to stop those late calls before tearing down whatever the callback writes to.
    /// </remarks>
    public Action<ExperienceCaptureFailure>? OnCaptureFailure { get; set; }

    /// <summary>
    /// Optional. The Core service that turns each completed, captured run into a durable Experience
    /// Record. Leave it <see langword="null"/> to capture only -- the host can still finalize runs
    /// itself, whenever it likes, from the capture service's snapshot.
    /// </summary>
    /// <remarks>
    /// Setting this requires exactly one finalization resolver too -- <see cref="ResolveFinalizationAsync"/>
    /// (preferred) or <see cref="ResolveFinalization"/> -- and a resolver requires this, because only the
    /// host knows a run's required checks, its verification evidence, its authorization context, and
    /// its storage policy. Finalization runs inside the same once-only, <see cref="FinalizationTimeout"/>-bounded
    /// step as capture finalization, after the run's attempt and completion were recorded, and only
    /// when both of those succeeded. It never uses the caller's cancellation token and never changes
    /// what the caller of the agent observes.
    /// </remarks>
    public ExperienceFinalizationService? FinalizationService { get; set; }

    /// <summary>
    /// The synchronous form of <see cref="ResolveFinalizationAsync"/>. When <see cref="FinalizationService"/> is
    /// set, exactly one of the two resolvers is required (and neither is valid without it): builds the
    /// finalize request for one completed run. Returning <see langword="null"/> skips finalizing that
    /// run. The request's <c>RunId</c> must be this invocation's run. If it throws, or returns a
    /// request for another run, the run is not finalized and the failure is reported through
    /// <see cref="OnCaptureFailure"/>. Prefer <see cref="ResolveFinalizationAsync"/> whenever producing the
    /// evidence needs I/O.
    /// </summary>
    public Func<ExperienceFinalizationContext, FinalizeExperienceRequest?>? ResolveFinalization { get; set; }

    /// <summary>
    /// The preferred finalization resolver: builds the finalize request for one completed run, asynchronously --
    /// running the run's checks or reading CI for its evidence, say. When <see cref="FinalizationService"/> is set,
    /// exactly one of this and <see cref="ResolveFinalization"/> is required, and neither is valid without it.
    /// </summary>
    /// <remarks>
    /// Awaited at the point <see cref="ResolveFinalization"/> would be called, with the token finalization already
    /// runs under: the <see cref="FinalizationTimeout"/>-bounded token, never the caller's. Its result is treated
    /// exactly as the synchronous resolver's: <see langword="null"/> skips finalizing that run; a request for another
    /// run, a throw, or a faulted task leaves the run not finalized and is reported through
    /// <see cref="OnCaptureFailure"/>. When <see cref="FinalizationTimeout"/> fires while it is awaited, the timeout
    /// is reported once, at stage <see cref="ExperienceCaptureFailureStage.Finalize"/>, and the resolver's own
    /// cancellation is not reported again; a request it returns after the timeout, or after the capture lifetime was
    /// disposed, finalizes nothing.
    /// </remarks>
    public Func<ExperienceFinalizationContext, CancellationToken, ValueTask<FinalizeExperienceRequest?>>? ResolveFinalizationAsync { get; set; }

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
    public Action<FinalizeExperienceResult>? OnRunFinalized { get; set; }

    /// <summary>The clock used for run, attempt, and tool-call timestamps, durations, and the finalization timeout.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Creates run, attempt, tool-call, and completion-event identifiers. Defaults to <see cref="Guid.NewGuid"/>. Must be thread-safe.</summary>
    public Func<Guid> NewId { get; set; } = Guid.NewGuid;

    /// <summary>
    /// The one-call setup's identity step: awaited before <see cref="ResolveRun"/>, which then reads the identity from
    /// <see cref="ExperienceRunContext.Identity"/>. A <see langword="null"/> identity runs the invocation uncaptured,
    /// and is not a failure. Internal, so the explicit wiring is unchanged.
    /// </summary>
    internal Func<ExperienceRunContext, CancellationToken, ValueTask<ExperienceIdentity?>>? ResolveIdentityAsync { get; set; }

    /// <summary>A shallow copy, taken once validation starts, so a later change to these options changes nothing.</summary>
    internal ExperienceCaptureOptions Snapshot() => (ExperienceCaptureOptions)MemberwiseClone();

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
