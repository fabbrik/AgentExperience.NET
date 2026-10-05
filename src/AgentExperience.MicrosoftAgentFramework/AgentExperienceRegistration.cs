using System.Collections.Concurrent;
using AgentExperience.Abstractions;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Retrieval;
using AgentExperience.Core.Verification;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentExperience.MicrosoftAgentFramework;

/// <summary>
/// The one-call setup's state in the container: the host's options, and the wiring that turns them into injection
/// and capture options. Registered by <c>AddAgentExperience</c>, so a second call can tell it is one.
/// </summary>
internal sealed class AgentExperienceRegistration(AgentExperienceOptions options)
{
    internal AgentExperienceOptions Options { get; } = options;

    /// <summary>
    /// Throws a clear <see cref="InvalidOperationException"/> when no storage was chosen: without a record store and a
    /// candidate source nothing can be stored or found, and the container's own message would not say what to do.
    /// </summary>
    internal static void EnsureStorage(IServiceProvider services)
    {
        var isService = services.GetService<IServiceProviderIsService>();
        bool Registered(Type type) => isService?.IsService(type) ?? services.GetService(type) is not null;

        if (!Registered(typeof(IExperienceRecordStore)) || !Registered(typeof(IExperienceCandidateSource)))
        {
            throw new InvalidOperationException(
                "AddAgentExperience has no storage. Choose one on the builder it returns: "
                + ".UseInMemoryStorageForDevelopment() (package AgentExperience.Storage.InMemory, for development and tests only) "
                + "or .UsePostgres(connectionString) (package AgentExperience.Storage.Postgres). A host with its own storage "
                + $"registers an {nameof(IExperienceRecordStore)} and an {nameof(IExperienceCandidateSource)} instead.");
        }
    }

    /// <summary>The registration, or a clear exception when <c>AddAgentExperience</c> was never called.</summary>
    internal static AgentExperienceRegistration From(IServiceProvider services) =>
        services.GetService<AgentExperienceRegistration>()
        ?? throw new InvalidOperationException(
            "UseAgentExperience and GetAgentExperienceContextProvider need services.AddAgentExperience(...) to have been called on the container's service collection.");

    /// <summary>The clock: the options' own, else the container's, else the system clock.</summary>
    internal TimeProvider ClockFrom(IServiceProvider services) =>
        Options.TimeProvider ?? services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>The context provider the container hands out: the library's defaults, then the host's hook.</summary>
    internal ExperienceContextProvider CreateContextProvider(IServiceProvider services)
    {
        EnsureStorage(services);

        var clock = ClockFrom(services);
        var injection = new ExperienceInjectionOptions
        {
            ResolveRequestAsync = (context, cancellationToken) => ResolveRequestAsync(context, clock, cancellationToken),
            TimeProvider = clock,
        };
        Options.Injection?.Invoke(injection);

        return new ExperienceContextProvider(
            services.GetRequiredService<ExperienceRetrievalService>(),
            services.GetRequiredService<IExperienceRecordStore>(),
            injection);
    }

    /// <summary>The default finalization timeout under the one-call setup: checks often run tests.</summary>
    internal static readonly TimeSpan DefaultFinalizationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The capture options one agent is built with: the library's defaults, then the host's hook.</summary>
    internal ExperienceCaptureOptions CreateCaptureOptions(IServiceProvider services)
    {
        EnsureStorage(services);

        var clock = ClockFrom(services);
        var binding = new FinalizationBinding();
        var capture = new ExperienceCaptureOptions
        {
            ResolveRun = ResolveRun,
            TimeProvider = clock,
            FinalizationTimeout = DefaultFinalizationTimeout,
            ResolveIdentityAsync = (context, cancellationToken) => ResolveIdentityAsync(IdentityContextOf(context), clock, cancellationToken),
        };

        if (Options.Verify is { } verify)
        {
            capture.FinalizationService = services.GetRequiredService<ExperienceFinalizationService>();
            capture.ResolveFinalizationAsync = (context, cancellationToken) => ResolveFinalizationAsync(verify, binding, context, cancellationToken);
        }

        Options.Capture?.Invoke(capture);

        if (Options.Verify is not null && capture.ResolveFinalization is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(AgentExperienceOptions)}.{nameof(AgentExperienceOptions.Verify)} is set, so the {nameof(AgentExperienceOptions.Capture)} hook may not set "
                + $"{nameof(ExperienceCaptureOptions.ResolveFinalization)}: the one-call setup finalizes through Verify. Remove one of the two.");
        }

        // Bound once, after the hook, so a later change to the options object changes nothing.
        binding.NewId = capture.NewId;
        binding.Clock = capture.TimeProvider;
        return capture;
    }

    /// <summary>
    /// The host's identity resolver, bounded by <see cref="AgentExperienceOptions.IdentityTimeout"/>. A timeout throws
    /// <see cref="TimeoutException"/>; the caller's own cancellation propagates.
    /// </summary>
    internal async ValueTask<ExperienceIdentity?> ResolveIdentityAsync(
        AgentExperienceIdentityContext context,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var limit = Options.IdentityTimeout;
        using var timeout = new CancellationTokenSource(limit, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // WaitAsync also bounds a resolver that ignores its token.
            return await Options.ResolveIdentity!(context, linked.Token).AsTask().WaitAsync(limit, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && (ex is TimeoutException || (ex is OperationCanceledException && timeout.IsCancellationRequested)))
        {
            throw new TimeoutException($"ResolveIdentity did not finish within {limit}.", ex);
        }
    }

    /// <summary>
    /// The injection request: the identity capture already resolved for this invocation when it did (an identity, none,
    /// or a failure), else the host's resolver; and the user's own words. No identity, or no words, skips injection.
    /// </summary>
    private async ValueTask<RetrieveExperienceRequest?> ResolveRequestAsync(
        ExperienceInjectionContext context,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ExperienceIdentity? identity;
        if (IdentityResolution.Current is { } resolved && resolved.For(context.Agent))
        {
            if (resolved.Failure is { } failure)
            {
                throw new InvalidOperationException("The identity could not be resolved for this invocation; capture reported why.", failure);
            }

            identity = resolved.Identity;
        }
        else
        {
            identity = await ResolveIdentityAsync(
                new AgentExperienceIdentityContext(context.Messages, context.Session, context.Agent), clock, cancellationToken).ConfigureAwait(false);
        }

        if (identity is null || context.DerivedTaskText is not { } taskText)
        {
            return null;
        }

        return new RetrieveExperienceRequest(identity.Authorization, identity.Scope, taskText);
    }

    /// <summary>The run descriptor: the task identifier, the resolved identity's scope, and the user's own words.</summary>
    private ExperienceRunDescriptor ResolveRun(ExperienceRunContext context)
    {
        var identity = context.Identity
            ?? throw new InvalidOperationException("No identity was resolved for this invocation, so it has no scope to be captured under.");

        var taskId = Options.ResolveTaskId is { } resolveTaskId ? resolveTaskId(IdentityContextOf(context)) : Options.TaskId;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new InvalidOperationException($"{nameof(AgentExperienceOptions.ResolveTaskId)} returned a blank task identifier.");
        }

        return new ExperienceRunDescriptor(taskId, identity.Scope, TaskDescription: context.DerivedTaskText);
    }

    /// <summary>
    /// The finalization request: the host's verdict, and everything else filled in -- a new closed round under the
    /// verdict's revision, that revision as the current one, storage permitted, the time now, and the authorization
    /// the invocation ran under. A <see langword="null"/> verdict finalizes nothing.
    /// </summary>
    private static async ValueTask<FinalizeExperienceRequest?> ResolveFinalizationAsync(
        Func<ExperienceVerificationContext, CancellationToken, ValueTask<ExperienceVerification?>> verify,
        FinalizationBinding capture,
        ExperienceFinalizationContext context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity
            ?? throw new InvalidOperationException("No identity was resolved for the invocation that ended this run, so it has no authorization to be stored under.");

        var roundId = capture.NewId();
        var verification = await verify(
            new ExperienceVerificationContext(context.Run, roundId, identity, capture.Clock, capture.NewId),
            cancellationToken).ConfigureAwait(false);

        if (verification is null)
        {
            return null;
        }

        // Evidence for another round or revision would be checked against nothing this run was verified in.
        foreach (var evidence in verification.Evidence)
        {
            if (evidence is null
                || evidence.VerificationRoundId != roundId
                || !string.Equals(evidence.ArtifactRevision, verification.ArtifactRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Verify returned evidence that does not name this run's verification round and the returned artifact revision; "
                    + "build it with context.CreateEvidence(...) and the same revision. The run is not stored.");
            }
        }

        return new FinalizeExperienceRequest(
            RunId: context.Run.RunId,
            Authorization: identity.Authorization,
            ClosedRound: new ClosedVerificationRound(roundId, verification.ArtifactRevision),
            RequiredChecks: verification.RequiredChecks,
            Evidence: verification.Evidence,
            CurrentArtifactRevision: verification.ArtifactRevision,
            StorageDecision: StorageDecision.Permit,
            FinalizedAt: capture.Clock.GetUtcNow());
    }

    /// <summary>The identifier source and clock finalization uses, bound once the capture hook has run.</summary>
    private sealed class FinalizationBinding
    {
        public Func<Guid> NewId { get; set; } = Guid.NewGuid;

        public TimeProvider Clock { get; set; } = TimeProvider.System;
    }

    private static AgentExperienceIdentityContext IdentityContextOf(ExperienceRunContext context) =>
        new(context.Messages as IReadOnlyList<ChatMessage> ?? context.Messages.ToList(), context.Session, context.Agent);
}

/// <summary>
/// The capture lifetimes of every agent built with <c>UseAgentExperience</c> on one container, disposed with it, so
/// disposing the container stops their open-run timers and any late finalization.
/// </summary>
internal sealed class AgentExperienceLifetimes : IDisposable
{
    private readonly ConcurrentQueue<IDisposable> _lifetimes = new();
    private int _disposed;

    /// <summary>Holds <paramref name="lifetime"/> until disposal; disposes it at once when that already happened.</summary>
    public void Add(IDisposable lifetime)
    {
        _lifetimes.Enqueue(lifetime);
        if (Volatile.Read(ref _disposed) != 0)
        {
            Drain();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Drain();
    }

    private void Drain()
    {
        while (_lifetimes.TryDequeue(out var lifetime))
        {
            lifetime.Dispose();
        }
    }
}
