using AgentExperience.Abstractions;
using AgentExperience.MicrosoftAgentFramework.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// Puts another memory provider (Mem0, Zep, Letta, the host's own: any MAF <see cref="AIContextProvider"/>) under the
/// same holdout the library's own lessons run under, so its benefit can be measured: on a deterministic share of captured
/// runs whose wrapped context is not empty, that context is withheld, and each such run's arm is recorded against a
/// host-chosen provider key. <see cref="AgentExperienceBenefit.EstimateProviderAsync"/> then compares the arms' verified
/// outcomes. Off by default (<see cref="ExperienceHoldoutOptions.Rate"/> 0).
/// </summary>
/// <remarks>
/// <para>
/// <b>The wrapped provider runs exactly as without the wrapper.</b> Its <see cref="AIContextProvider.InvokingAsync"/> is
/// always called, in both arms, and so is its <see cref="AIContextProvider.InvokedAsync"/>; its
/// <see cref="AIContextProvider.StateKeys"/> are this provider's, so it keeps learning from every run. Its exceptions
/// propagate unchanged, and nothing is recorded for an invocation that threw. Only the context it returns is withheld.
/// </para>
/// <para>
/// <b>Who takes part.</b> Only an invocation captured by this agent's own <c>UseExperienceCapture</c> (or
/// <c>UseAgentExperience</c>), with a known authorization (see below), whose wrapped provider added at least one message,
/// tool or instruction. Every other invocation is passed through exactly as the wrapped provider answered it, and writes
/// nothing. A provider whose items are opaque is measured as a whole: no item is attributed.
/// </para>
/// <para>
/// <b>How a run is assigned.</b> As the library's own holdout (<see cref="ExperienceHoldoutOptions"/>), with the salt
/// <c>"{providerKey.Length}:{providerKey}/{Salt}"</c>: each provider is randomised independently of the lessons and of every other provider,
/// and every invocation of the same run lands in the same arm.
/// </para>
/// <para>
/// <b>What a held-out invocation does.</b> The context the wrapped provider was handed is returned, the same object,
/// unchanged, so the run sees exactly what it would see without the provider. The context passed in is never changed. A treatment invocation returns what the
/// wrapped provider returned.
/// </para>
/// <para>
/// <b>What is recorded.</b> In the background, through
/// <see cref="IExperienceRecordStore.RecordProviderHoldoutAssignmentAsync"/>, the run, the key, the arm and the instant,
/// in the scope the run is captured in, under the authorization of the identity the one-call setup resolved for the
/// invocation, or the host's <c>authorize</c> callback. The write never delays or fails the invocation; its outcome is the
/// <c>record_provider_holdout</c> operation in telemetry.
/// </para>
/// </remarks>
public sealed class ExperienceHoldoutContextProvider : AIContextProvider
{
    private readonly AIContextProvider _inner;
    private readonly string _providerKey;
    private readonly ExperienceHoldoutOptions _holdout;
    private readonly string _salt;
    private readonly IExperienceRecordStore _store;
    private readonly Func<Scope, AuthorizationContext?>? _authorize;
    private readonly TimeProvider _clock;
    private readonly BackgroundRecorder _recorder;

    /// <summary>Wraps <paramref name="inner"/> under a holdout recorded against <paramref name="providerKey"/>.</summary>
    /// <param name="inner">The memory provider to measure. It is always invoked.</param>
    /// <param name="providerKey">
    /// The host's name for the provider (<c>"mem0"</c>, say), which the estimate reads by: 1 to
    /// <see cref="ProviderHoldoutAssignment.MaxProviderKeyLength"/> characters of ASCII letters, digits, <c>.</c>, <c>_</c>
    /// and <c>-</c>.
    /// </param>
    /// <param name="holdout">The rate and salt. At rate 0 (the default) nothing is withheld and nothing recorded.</param>
    /// <param name="store">The store the assignments are recorded in.</param>
    /// <param name="authorize">
    /// The authorization to record a run captured in the given scope under, when the invocation has no identity the
    /// one-call setup resolved (the explicit wiring); <see langword="null"/> or a <see langword="null"/> answer, and such a
    /// run does not take part. Called once per invocation that would take part; a callback that throws counts as no answer.
    /// </param>
    /// <param name="timeProvider">The clock for the assignment instant and the write's bound; <see cref="TimeProvider.System"/> by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/>, <paramref name="providerKey"/>, <paramref name="holdout"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="providerKey"/> is malformed, or the holdout's salt is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The holdout's rate is outside [0, 1].</exception>
    public ExperienceHoldoutContextProvider(
        AIContextProvider inner,
        string providerKey,
        ExperienceHoldoutOptions holdout,
        IExperienceRecordStore store,
        Func<Scope, AuthorizationContext?>? authorize = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(providerKey);
        ArgumentNullException.ThrowIfNull(holdout);
        ArgumentNullException.ThrowIfNull(store);
        if (!ProviderHoldoutAssignment.IsValidProviderKey(providerKey))
        {
            throw new ArgumentException(
                $"The provider key must be 1 to {ProviderHoldoutAssignment.MaxProviderKeyLength} characters of A-Z, a-z, 0-9, '.', '_' and '-'.",
                nameof(providerKey));
        }

        holdout.Validate(nameof(holdout));
        _inner = inner;
        _providerKey = providerKey;
        _holdout = holdout;
        _salt = ProviderSalt(providerKey, holdout.Salt);
        _store = store;
        _authorize = authorize;
        _clock = timeProvider ?? TimeProvider.System;
        _recorder = new BackgroundRecorder(_clock, ExperienceInjectionLimits.DefaultMaxAbandonedReads);
    }

    /// <summary>The key the assignments are recorded against.</summary>
    public string ProviderKey => _providerKey;

    /// <summary>The wrapped provider.</summary>
    public AIContextProvider InnerProvider => _inner;

    /// <summary>The wrapped provider's state keys: it keeps its session state exactly as without the wrapper.</summary>
    public override IReadOnlyList<string> StateKeys => _inner.StateKeys;

    /// <summary>Completes once every background record started so far has ended. For tests.</summary>
    internal Task RecordsSettledAsync() => _recorder.SettledAsync();

    /// <summary>The wrapper itself, or what the wrapped provider answers.</summary>
    /// <param name="serviceType">The service asked for.</param>
    /// <param name="serviceKey">Its key, if any.</param>
    /// <returns>The service, or <see langword="null"/>.</returns>
    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        base.GetService(serviceType, serviceKey) ?? _inner.GetService(serviceType, serviceKey);

    /// <summary>
    /// Invokes the wrapped provider and, when this run takes part, decides its arm: the wrapped provider's context is
    /// returned in the treatment arm and withheld in the holdout arm.
    /// </summary>
    /// <param name="context">The invocation MAF is about to run.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the wrapped provider returned, or, held out, the context it was handed.</returns>
    protected override async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        if (!_holdout.Active || context?.AIContext is not { } given)
        {
            return await _inner.InvokingAsync(context!, cancellationToken).ConfigureAwait(false);
        }

        // References only, taken before the wrapped provider runs: the context passed in is never changed, and nothing is
        // enumerated unless the run takes part.
        var instructions = given.Instructions;
        var messages = given.Messages;
        var tools = given.Tools;

        var answered = await _inner.InvokingAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            if (answered is null || CaptureScope.Current is not { } capture || !capture.Captures(context.Agent)
                || !Added(instructions, messages, tools, answered)
                || Authorization(capture) is not { } authorization)
            {
                return answered!;
            }

            var arm = HoldoutAssignmentFunction.Assign(capture.RunId, _salt, _holdout.Rate);
            StartRecording(authorization, capture.Scope, new ProviderHoldoutAssignment(capture.RunId, _providerKey, arm, _clock.GetUtcNow()));

            // Held out: exactly the context the wrapped provider was handed, the very object, as if it were not there.
            return arm == HoldoutArm.Holdout ? given : answered;
        }
        catch (Exception)
        {
            // A sequence or check that throws after the wrapped provider answered: its answer, unchanged, and no row.
            InjectionDiagnostics.BackgroundRecorded(
                InjectionDiagnostics.RecordProviderHoldout,
                InjectionDiagnostics.NotRecordedOutcome,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                Core.Diagnostics.ExperienceOperationErrorClass.Unexpected);
            return answered!;
        }
    }

    /// <summary>Forwards to the wrapped provider, in both arms, so it keeps learning from every run.</summary>
    /// <param name="context">The invocation MAF has run.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The wrapped provider's own completion.</returns>
    protected override ValueTask InvokedCoreAsync(InvokedContext context, CancellationToken cancellationToken = default) =>
        _inner.InvokedAsync(context, cancellationToken);

    /// <summary>The salt a provider's arms are drawn with: the key, length-prefixed, then the holdout's salt, so no two (key, salt) pairs share one.</summary>
    internal static string ProviderSalt(string providerKey, string salt) => $"{providerKey.Length}:{providerKey}/{salt}";

    /// <summary>Whether the wrapped provider added anything: an instruction, or a message or tool it was not handed.</summary>
    private static bool Added(string? instructions, IEnumerable<ChatMessage>? messages, IEnumerable<AITool>? tools, AIContext answered) =>
        (!string.IsNullOrEmpty(answered.Instructions) && !string.Equals(instructions, answered.Instructions, StringComparison.Ordinal))
        || AnyNew(messages, answered.Messages)
        || AnyNew(tools, answered.Tools);

    private static bool AnyNew<T>(IEnumerable<T>? given, IEnumerable<T>? answered)
        where T : class
    {
        if (answered is null || ReferenceEquals(given, answered))
        {
            return false;
        }

        HashSet<T>? handed = null;
        foreach (var item in answered)
        {
            if (item is null)
            {
                continue;
            }

            handed ??= new HashSet<T>(given ?? [], ReferenceEqualityComparer.Instance);
            if (!handed.Contains(item))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The identity the one-call setup resolved for this invocation, else the host's callback's answer.</summary>
    private AuthorizationContext? Authorization(CaptureScope capture)
    {
        if (capture.Identity is { } identity)
        {
            return identity.Authorization;
        }

        try
        {
            return _authorize?.Invoke(capture.Scope);
        }
        catch (Exception)
        {
            // No answer: the run does not take part.
            return null;
        }
    }

    private void StartRecording(AuthorizationContext authorization, Scope scope, ProviderHoldoutAssignment assignment) =>
        _recorder.Start(
            InjectionDiagnostics.RecordProviderHoldout,
            () => _store.TracksProviderHoldout,
            1,
            () => async (_, _, token) =>
            {
                var recorded = await _store.RecordProviderHoldoutAssignmentAsync(authorization, scope, assignment, token).ConfigureAwait(false);
                return new RecordAnswer(recorded is { Outcome: ExperienceStoreOutcome.Committed }, recorded?.Tracked ?? true);
            });
}
