using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.DependencyInjection;
using AgentExperience.Core.Sanitization;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// Who an invocation runs for: the authorization the host established for the caller, and the scope its experience
/// is read from and written to. Returned by <see cref="AgentExperienceOptions.ResolveIdentity"/>.
/// </summary>
/// <param name="Authorization">The caller's authorization, from the host's own authentication.</param>
/// <param name="Scope">The exact scope experience is read from and stored under; it must lie within <paramref name="Authorization"/>.</param>
public sealed record ExperienceIdentity(AuthorizationContext Authorization, Scope Scope)
{
    /// <summary>The caller's authorization, from the host's own authentication.</summary>
    public AuthorizationContext Authorization { get; init; } = Authorization ?? throw new ArgumentNullException(nameof(Authorization));

    /// <summary>The exact scope experience is read from and stored under.</summary>
    public Scope Scope { get; init; } = Scope ?? throw new ArgumentNullException(nameof(Scope));

    /// <summary>
    /// Whether <paramref name="other"/> is the same caller in the same scope: the same scope, and the same tenant,
    /// principal and narrowing fields of the authorization. Roles and the issue time may differ between invocations.
    /// </summary>
    internal bool SamePrincipal(ExperienceIdentity other) =>
        Scope == other.Scope
        && string.Equals(Authorization.TenantId, other.Authorization.TenantId, StringComparison.Ordinal)
        && string.Equals(Authorization.PrincipalId, other.Authorization.PrincipalId, StringComparison.Ordinal)
        && string.Equals(Authorization.ApplicationId, other.Authorization.ApplicationId, StringComparison.Ordinal)
        && string.Equals(Authorization.ProjectId, other.Authorization.ProjectId, StringComparison.Ordinal)
        && string.Equals(Authorization.TeamId, other.Authorization.TeamId, StringComparison.Ordinal)
        && string.Equals(Authorization.AgentId, other.Authorization.AgentId, StringComparison.Ordinal)
        && string.Equals(Authorization.UserId, other.Authorization.UserId, StringComparison.Ordinal);
}

/// <summary>
/// What <see cref="AgentExperienceOptions.ResolveIdentity"/> and <see cref="AgentExperienceOptions.ResolveTaskId"/> see
/// of one invocation.
/// </summary>
/// <param name="Messages">The invocation's input messages.</param>
/// <param name="Session">The caller-supplied session, or <see langword="null"/> when the caller passed none.</param>
/// <param name="Agent">The agent being invoked.</param>
public sealed record AgentExperienceIdentityContext(
    IReadOnlyList<ChatMessage> Messages,
    AgentSession? Session,
    AIAgent Agent);

/// <summary>
/// What <see cref="AgentExperienceOptions.Verify"/> is given: the completed run, the identifier of the verification
/// round the library has opened for it, and the identity the invocation ran under.
/// </summary>
/// <remarks>
/// Every piece of <see cref="Evidence"/> returned must name <see cref="RoundId"/> and the returned
/// <see cref="ExperienceVerification.ArtifactRevision"/>, or the run is not stored and the mismatch is reported;
/// <see cref="CreateEvidence"/> names the round, and fills in the evidence identifier and capture time from the
/// configured identifier source and clock. To unit-test a <c>Verify</c> callback, build one with <see cref="Create"/>.
/// </remarks>
public sealed class ExperienceVerificationContext
{
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid> _newId;

    internal ExperienceVerificationContext(ExperienceRun run, Guid roundId, ExperienceIdentity identity, TimeProvider timeProvider, Func<Guid> newId)
    {
        Run = run;
        RoundId = roundId;
        Identity = identity;
        _timeProvider = timeProvider;
        _newId = newId;
    }

    /// <summary>A context for testing a <c>Verify</c> callback outside an agent.</summary>
    /// <param name="run">The completed run.</param>
    /// <param name="roundId">The verification round's identifier.</param>
    /// <param name="identity">The identity the run ran under.</param>
    /// <param name="timeProvider">The clock evidence is stamped with; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    /// <param name="newId">The evidence identifier source; <see cref="Guid.NewGuid"/> when <see langword="null"/>.</param>
    /// <returns>The context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="run"/> or <paramref name="identity"/> is <see langword="null"/>.</exception>
    public static ExperienceVerificationContext Create(
        ExperienceRun run,
        Guid roundId,
        ExperienceIdentity identity,
        TimeProvider? timeProvider = null,
        Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(identity);
        return new(run, roundId, identity, timeProvider ?? TimeProvider.System, newId ?? Guid.NewGuid);
    }

    /// <summary>The completed, sanitized run: its attempts, tool calls, results and errors.</summary>
    public ExperienceRun Run { get; }

    /// <summary>The verification round this run is checked in. Every piece of evidence must name it.</summary>
    public Guid RoundId { get; }

    /// <summary>The identity the invocation ran under, from <see cref="AgentExperienceOptions.ResolveIdentity"/>.</summary>
    public ExperienceIdentity Identity { get; }

    /// <summary>
    /// A piece of evidence for this round: a new identifier, <see cref="RoundId"/>, and the current time.
    /// </summary>
    /// <param name="checkId">The required check it backs.</param>
    /// <param name="kind">The kind of evidence, for example <c>"TestResult"</c>; must match the check's expected kind.</param>
    /// <param name="result">The check's verdict, from your own check, never from the model.</param>
    /// <param name="producer">What produced it: an evaluator, a tool, or a person's identifier.</param>
    /// <param name="artifactRevision">The artifact revision checked; the same one <see cref="ExperienceVerification.ArtifactRevision"/> names.</param>
    /// <param name="detail">Optional sanitized, human-readable detail. Never private reasoning.</param>
    /// <returns>The evidence.</returns>
    public Evidence CreateEvidence(string checkId, string kind, CheckResult result, string producer, string artifactRevision, string? detail = null) =>
        new(
            EvidenceId: _newId(),
            VerificationRoundId: RoundId,
            ArtifactRevision: artifactRevision,
            CheckId: checkId,
            Kind: kind,
            Result: result,
            Producer: producer,
            Detail: detail,
            CapturedAt: _timeProvider.GetUtcNow());
}

/// <summary>
/// Your verdict on one run: the checks it had to pass, the evidence from your own checks, and the artifact revision
/// they were run against. Returned by <see cref="AgentExperienceOptions.Verify"/>.
/// </summary>
/// <param name="RequiredChecks">The checks the run must pass to be verified.</param>
/// <param name="Evidence">The evidence for those checks, each naming <see cref="ExperienceVerificationContext.RoundId"/> and <paramref name="ArtifactRevision"/>.</param>
/// <param name="ArtifactRevision">The artifact revision the checks were run against; it closes the round and is the current revision.</param>
public sealed record ExperienceVerification(
    IReadOnlyList<RequiredCheck> RequiredChecks,
    IReadOnlyList<Evidence> Evidence,
    string ArtifactRevision)
{
    /// <summary>The checks the run must pass to be verified.</summary>
    public IReadOnlyList<RequiredCheck> RequiredChecks { get; init; } = RequiredChecks ?? throw new ArgumentNullException(nameof(RequiredChecks));

    /// <summary>The evidence for those checks.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = Evidence ?? throw new ArgumentNullException(nameof(Evidence));

    /// <summary>The artifact revision the checks were run against.</summary>
    public string ArtifactRevision { get; init; } = ArtifactRevision ?? throw new ArgumentNullException(nameof(ArtifactRevision));
}

/// <summary>
/// The one-call setup's configuration, passed to <c>services.AddAgentExperience(options => …)</c>. Only
/// <see cref="ResolveIdentity"/> is required; everything else has a safe default.
/// </summary>
/// <remarks>
/// <para>
/// With these options the library captures every invocation it has an identity for, injects the lessons that apply
/// to the user's own words (<see cref="ExperienceInjectionContext.DerivedTaskText"/>), and, when <see cref="Verify"/>
/// is set, verifies and stores each completed run. It fills in what the explicit wiring asks the host for: the
/// verification round and its identifier, the current artifact revision, <see cref="Core.Finalization.StorageDecision.Permit"/>,
/// the finalization time, and the authorization.
/// </para>
/// <para>
/// The explicit wiring (<c>UseExperienceCapture</c> and a hand-built <see cref="ExperienceContextProvider"/>) keeps
/// working unchanged; use it when you need what these options do not offer.
/// </para>
/// </remarks>
public sealed class AgentExperienceOptions
{
    /// <summary>The task identifier used when <see cref="ResolveTaskId"/> is not set.</summary>
    public const string DefaultTaskId = "default";

    private string? _taskId;

    /// <summary>
    /// Required. Who the invocation runs for: the caller's authorization and the scope experience is read from and
    /// stored under. Return <see langword="null"/> and the invocation is neither injected nor captured.
    /// </summary>
    /// <remarks>
    /// The identity must come from the host's own authentication (the signed-in user, the service principal, the
    /// tenant the request arrived on), <b>never from model output or from the messages' text</b>: it is the boundary
    /// that keeps one tenant's experience out of another's. It may be called more than once for one invocation (before
    /// capture and, when capture has not resolved one, before injection), so it must give the same answer each time.
    /// If it throws, the invocation runs normally, with no memory, and the failure is reported.
    /// </remarks>
    public Func<AgentExperienceIdentityContext, CancellationToken, ValueTask<ExperienceIdentity?>>? ResolveIdentity { get; set; }

    /// <summary>
    /// The task identifier every run is captured under, when <see cref="ResolveTaskId"/> is not set. Defaults to
    /// <see cref="DefaultTaskId"/>. Set this or <see cref="ResolveTaskId"/>, not both.
    /// </summary>
    public string TaskId
    {
        get => _taskId ?? DefaultTaskId;
        set => _taskId = value;
    }

    /// <summary>
    /// The task identifier for one invocation, when it differs between invocations. Set this or <see cref="TaskId"/>,
    /// not both. If it throws or returns a blank identifier, the invocation runs uncaptured and the failure is reported.
    /// </summary>
    public Func<AgentExperienceIdentityContext, string>? ResolveTaskId { get; set; }

    /// <summary>
    /// Optional. Runs your own checks on a completed run and returns the verdict; return <see langword="null"/> and
    /// the run is not stored. <b>Without it nothing is stored</b>: runs are captured, but a lesson needs verification
    /// to become reusable experience.
    /// </summary>
    /// <remarks>
    /// The evidence must come from your checks (a test run, an exit code, a person's approval), never from the model's
    /// own claim. If it throws, the run is not stored, the invocation is unaffected, and the failure is reported to
    /// <see cref="ExperienceCaptureOptions.OnCaptureFailure"/> (set it through <see cref="Capture"/>). It runs within
    /// <see cref="ExperienceCaptureOptions.FinalizationTimeout"/>, which the one-call setup sets to 2 minutes, since
    /// checks often run tests (change it through <see cref="Capture"/>); the caller waits for it.
    /// </remarks>
    public Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>>? Verify { get; set; }

    /// <summary>
    /// What capture may keep. Defaults to <see cref="AgentExperienceDefaults.Sanitization"/>: no tool argument value is
    /// kept until you allowlist it, results are kept, and secret-named fields are redacted.
    /// </summary>
    public SanitizationOptions Sanitization { get; set; } = AgentExperienceDefaults.Sanitization;

    /// <summary>How much capture may keep. Defaults to <see cref="AgentExperienceDefaults.CaptureLimits"/>.</summary>
    public CaptureLimits CaptureLimits { get; set; } = AgentExperienceDefaults.CaptureLimits;

    /// <summary>
    /// Adjusts the injection options after the library has set its defaults (the request resolver and the clock), for
    /// example the limits, the rendering, or <see cref="ExperienceInjectionOptions.OnContextInjected"/>.
    /// </summary>
    public Action<ExperienceInjectionOptions>? Injection { get; set; }

    /// <summary>
    /// Adjusts the capture options after the library has set its defaults (the run and finalization resolvers and the
    /// clock), for example <see cref="ExperienceCaptureOptions.OnCaptureFailure"/> or the environment fingerprint.
    /// </summary>
    public Action<ExperienceCaptureOptions>? Capture { get; set; }

    /// <summary>
    /// How long <see cref="ResolveIdentity"/> may take. Past it the invocation runs normally, with no memory, and the
    /// timeout is reported. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan IdentityTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The clock for injection, capture and verification. When <see langword="null"/>, the container's registered
    /// <see cref="System.TimeProvider"/> is used, or <see cref="TimeProvider.System"/>. When set, it is also registered as the
    /// container's <see cref="System.TimeProvider"/> unless one is registered already.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>Throws when the options cannot work.</summary>
    internal void Validate()
    {
        if (ResolveIdentity is null)
        {
            throw new ArgumentException(
                $"{nameof(AgentExperienceOptions)}.{nameof(ResolveIdentity)} is required: it tells the library who each invocation runs for, from your own authentication.",
                nameof(ResolveIdentity));
        }

        if (_taskId is not null && ResolveTaskId is not null)
        {
            throw new ArgumentException(
                $"Set {nameof(AgentExperienceOptions)}.{nameof(TaskId)} or {nameof(ResolveTaskId)}, not both.",
                nameof(ResolveTaskId));
        }

        if (string.IsNullOrWhiteSpace(TaskId))
        {
            throw new ArgumentException($"{nameof(AgentExperienceOptions)}.{nameof(TaskId)} may not be blank.", nameof(TaskId));
        }

        if (IdentityTimeout <= TimeSpan.Zero || IdentityTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(IdentityTimeout), IdentityTimeout, $"{nameof(IdentityTimeout)} must be positive and at most {uint.MaxValue - 1} milliseconds.");
        }

        ArgumentNullException.ThrowIfNull(Sanitization, nameof(Sanitization));
        ArgumentNullException.ThrowIfNull(CaptureLimits, nameof(CaptureLimits));
    }
}
