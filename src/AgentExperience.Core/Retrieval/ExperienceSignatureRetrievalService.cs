using AgentExperience.Abstractions;
using AgentExperience.Core.Diagnostics;

namespace AgentExperience.Core.Retrieval;

/// <summary>
/// Looks reusable experience up by a failure signature (story 28.2): the records holding a failed tool call with the
/// same signature -- or, asked for a coarse match, the same tool and error class -- through an
/// <see cref="IExperienceSignatureSource"/>, with the eligibility Core's text retrieval applies. It ranks nothing and
/// renders nothing: it is the lookup a caller that is watching a run fail (story 28.3) builds on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Eligibility.</b> The source is asked for <see cref="ExperienceStatuses.EligibleForReuse"/> at or above the
/// policy's <see cref="RetrievalPolicy.MinimumConfidence"/>, in exactly the request's scope (and whatever sharing grants
/// the source honours), leaving model-authored records out when the request asks; a record older than the policy's
/// <see cref="RetrievalPolicy.MaxAge"/> (by <see cref="ExperienceRecord.UpdatedAt"/>, on this service's clock) is then
/// dropped, as text retrieval drops it. The source's order is kept.
/// </para>
/// <para>
/// <b>Failure is an answer.</b> An authorization that does not permit the scope is
/// <see cref="SignatureRetrievalOutcome.Denied"/> before the source is called; a request the source refuses is
/// <see cref="SignatureRetrievalOutcome.Invalid"/>; a source that throws (for example a database not migrated to the
/// signature table) is <see cref="SignatureRetrievalOutcome.Failed"/>, with the exception on the result. Only the caller's
/// own cancellation propagates. There is no timeout of its own: the caller's token bounds it.
/// </para>
/// <para>
/// <b>Telemetry.</b> One <c>retrieve.signature</c> operation on the Core source, its span tagged with the retrieval
/// channel (<c>signature</c>), how it matched, the correlation ID when there is one, and the outcome. The signature, its
/// tool name, its error class and every record stay off it.
/// </para>
/// </remarks>
public sealed class ExperienceSignatureRetrievalService
{
    /// <summary>The value of the span's retrieval channel attribute.</summary>
    internal const string SignatureChannel = "signature";

    private readonly IExperienceSignatureSource _source;
    private readonly RetrievalPolicy _policy;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the lookup over a signature source.</summary>
    /// <param name="source">The store's signature port.</param>
    /// <param name="policy">The confidence floor and expiry to apply. <see langword="null"/> uses <see cref="RetrievalPolicy.Default"/>.</param>
    /// <param name="timeProvider">The clock expiry is measured on. <see langword="null"/> uses <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public ExperienceSignatureRetrievalService(
        IExperienceSignatureSource source,
        RetrievalPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _policy = policy ?? RetrievalPolicy.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Finds the reusable records holding <see cref="SignatureRetrievalRequest.Signature"/>.</summary>
    /// <param name="request">The scope, the signature, how to match it, and the limit.</param>
    /// <param name="cancellationToken">Cancels the operation; it propagates unwrapped.</param>
    /// <returns>A result that always says what happened; it is empty unless <see cref="SignatureRetrievalOutcome.Completed"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/>, or its authorization, scope or signature, is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<SignatureRetrievalResult> FindAsync(
        SignatureRetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = ExperienceDiagnostics.Start(ExperienceOperationNames.RetrieveSignature, cancellationToken);

        SignatureRetrievalResult result;
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.RetrievalChannelAttribute, SignatureChannel);
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.SignatureMatchAttribute, request.Match.ToString());
            ExperienceDiagnostics.Tag(operation, ExperienceDiagnostics.CorrelationIdAttribute, request.CorrelationId);

            result = await FindCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExperienceDiagnostics.Faulted(operation, ExperienceOperationNames.RetrieveSignature, ex);
            throw;
        }

        ExperienceDiagnostics.Succeeded(operation, ExperienceOperationNames.RetrieveSignature, result.Outcome.ToString());
        return result;
    }

    private async Task<SignatureRetrievalResult> FindCoreAsync(SignatureRetrievalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.Authorization, $"{nameof(request)}.{nameof(request.Authorization)}");
        ArgumentNullException.ThrowIfNull(request.Scope, $"{nameof(request)}.{nameof(request.Scope)}");
        ArgumentNullException.ThrowIfNull(request.Signature, $"{nameof(request)}.{nameof(request.Signature)}");
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.Authorization.Permits(request.Scope))
        {
            return SignatureRetrievalResult.Empty(SignatureRetrievalOutcome.Denied);
        }

        var query = new ExperienceSignatureQuery(
            request.Signature,
            ExperienceStatuses.EligibleForReuse,
            _policy.MinimumConfidence,
            request.Match,
            request.Limit,
            request.CorrelationId)
        {
            ExcludeModelAuthored = request.ExcludeModelAuthored,
        };

        ExperienceSignatureSearchResult found;
        try
        {
            found = await _source.FindBySignatureAsync(request.Authorization, request.Scope, query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SignatureRetrievalResult.Empty(SignatureRetrievalOutcome.Failed) with { Failure = ex };
        }

        switch (found?.Outcome)
        {
            case ExperienceStoreOutcome.Found when found.Candidates is not null:
                break;
            case ExperienceStoreOutcome.Denied:
                return SignatureRetrievalResult.Empty(SignatureRetrievalOutcome.Denied);
            case ExperienceStoreOutcome.Invalid:
                return SignatureRetrievalResult.Empty(SignatureRetrievalOutcome.Invalid) with { Errors = found.Errors ?? [] };
            default:
                return SignatureRetrievalResult.Empty(SignatureRetrievalOutcome.Failed) with
                {
                    Failure = new InvalidOperationException($"The signature source returned '{found?.Outcome}' rather than '{ExperienceStoreOutcome.Found}'."),
                };
        }

        var now = _timeProvider.GetUtcNow();
        // The strict scope check text retrieval keeps: a record outside the scope is believed only when the source that
        // applied the predicate says a grant shared it.
        var candidates = found.Candidates
            .Where(candidate => candidate?.Record is not null
                && (candidate.Record.Scope == request.Scope || candidate.SharedByGrant)
                && !(_policy.MaxAge is { } maxAge && now - candidate.Record.UpdatedAt > maxAge))
            .ToList();
        return new SignatureRetrievalResult(SignatureRetrievalOutcome.Completed, candidates, [], null);
    }
}

/// <summary>A failure-signature lookup.</summary>
/// <param name="Authorization">What the host has established the caller may do.</param>
/// <param name="Scope">The exact scope to look in. Never treated as authority.</param>
/// <param name="Signature">The failure signature to look up, usually <see cref="ExperienceFailureSignature.From(string, string)"/> of the failure just seen.</param>
public sealed record SignatureRetrievalRequest(AuthorizationContext Authorization, Scope Scope, ExperienceFailureSignature Signature)
{
    /// <summary>Whether to match the whole signature (the default) or only its tool and error class.</summary>
    public ExperienceSignatureMatch Match { get; init; } = ExperienceSignatureMatch.Exact;

    /// <summary>The most records to return, from <see cref="ExperienceSignatureQuery.MinLimit"/> to <see cref="ExperienceSignatureQuery.MaxLimit"/>.</summary>
    public int Limit { get; init; } = ExperienceSignatureQuery.DefaultLimit;

    /// <summary>Whether to leave model-authored records out, by the rule <see cref="ExperienceSignatureQuery.ExcludeModelAuthored"/> states.</summary>
    public bool ExcludeModelAuthored { get; init; }

    /// <summary>The host's identifier for the work causing this lookup, echoed on the span and on any access rows. <see langword="null"/> when there is none.</summary>
    public string? CorrelationId { get; init; }
}

/// <summary>What a failure-signature lookup did.</summary>
public enum SignatureRetrievalOutcome
{
    /// <summary>The source answered; the candidates (possibly none) are the eligible matches.</summary>
    Completed,

    /// <summary>The authorization does not permit the scope; no source was called, or the source refused it.</summary>
    Denied,

    /// <summary>The source refused the request as malformed; see the errors.</summary>
    Invalid,

    /// <summary>The source failed; see the failure. Nothing was returned.</summary>
    Failed,
}

/// <summary>The result of <see cref="ExperienceSignatureRetrievalService.FindAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Candidates">The eligible matches, in the source's order, when <see cref="Outcome"/> is <see cref="SignatureRetrievalOutcome.Completed"/>; otherwise empty.</param>
/// <param name="Errors">The source's validation errors when <see cref="Outcome"/> is <see cref="SignatureRetrievalOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="Failure">What the source threw when <see cref="Outcome"/> is <see cref="SignatureRetrievalOutcome.Failed"/>; otherwise <see langword="null"/>.</param>
public sealed record SignatureRetrievalResult(
    SignatureRetrievalOutcome Outcome,
    IReadOnlyList<ExperienceSignatureCandidate> Candidates,
    IReadOnlyList<StoreValidationError> Errors,
    Exception? Failure)
{
    internal static SignatureRetrievalResult Empty(SignatureRetrievalOutcome outcome) => new(outcome, [], [], null);
}
