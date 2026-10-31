using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Reflections;

namespace AgentExperience.Core.Lifecycle;

/// <summary>
/// One living lesson per situation: an on-demand, deterministic pass that merges the validated records of
/// one scope that failed the same way and were fixed the same way into one consolidated record, which supersedes them
/// through the ordinary lifecycle and carries their combined, verified, independent support.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never automatic, never a model.</b> Nothing calls it on the request path or in the background; the host calls
/// <see cref="ConsolidateAsync"/>. Every field of a consolidated record is derived from its sources by fixed rules.
/// </para>
/// <para>
/// <b>Which records are sources.</b> Only records the library itself finalized and can still vouch for: in exactly the
/// scope asked for (never another scope's, never one a grant lends), <see cref="ExperienceStatus.Validated"/> or
/// <see cref="ExperienceStatus.Reinforced"/>, verified, <see cref="ExperienceRecordOrigin.Finalized"/>, with a provenance
/// signature that verifies (or listed as trusted unsigned) when signing is configured -- the rule confidence verification
/// applies to a run's record -- and a reflection that is not model-authored, with confirmed content. A hand-written record
/// is never a source. The pass reads at most <see cref="ExperienceRecordQuery.MaxLimit"/> records, newest first.
/// </para>
/// <para>
/// <b>The grouping key.</b> Two sources group together when they hold the same set of <em>resolved</em> failure signature
/// keys (the exact <see cref="ExperienceFailureSignature.Key"/>) and their working attempt -- the final attempt, ended
/// without an error, the one the block's <c>Worked:</c> line names -- called the same tools in the same order, with the
/// same values for the argument keys <see cref="ExperienceConsolidationOptions.ArgumentKeys"/> names. A group is merged
/// when it has at least two records and they came from at least <see cref="ExperienceConsolidationOptions.MinimumSources"/>
/// distinct runs. A consolidated record in the group stands for the sources it lists that are still trusted sources of
/// the same key and that it superseded (a listed source revoked or erased since is left out).
/// </para>
/// <para>
/// <b>The consolidated record.</b> Its ID is <see cref="ExperienceConsolidation.ConsolidatedIdFor"/> over its sources.
/// Its task, summary, attempts, outcome, environment, approaches, preconditions, warnings and reuse guidance are its most
/// recent source's; its lesson is the template <c>Verified in N independent runs (latest yyyy-MM-dd). Failed first with
/// &lt;class&gt; from &lt;tool&gt; in K of them. Worked: &lt;tool&gt; → &lt;tool&gt;.</c>
/// (<see cref="ExperienceConsolidation.TemplateVersion"/>). Its counters are verified evidence only, deduplicated by
/// independence key: each source's own validation (at most
/// <see cref="ExperienceFinalizationService.InitialSupportingValidations"/>, under its run and closed round) and every
/// counted update admitted <see cref="ConfidenceEvidenceAdmission.Verified"/> in each source's history and in each merged
/// consolidated record's own; host-trusted and unrecorded-admission evidence is never carried.
/// </para>
/// <para>
/// <b>Supersession.</b> Right before the record is created, the sources are read again and any that changed is left out
/// (the group is re-evaluated). The record is created as a <see cref="ExperienceStatus.Candidate"/> and validated, then
/// each source is moved to <see cref="ExperienceStatus.Superseded"/> by it, one compare-and-set commit per source. Until
/// those commits land the record and its sources coexist; a source that changed meanwhile is left untouched and
/// reported, and <see cref="ExperienceConsolidationGroupResult.Complete"/> is <see langword="false"/>. Every identifier and
/// timestamp is derived, so a re-run or a concurrent pass converges on the same record. A record already stored under
/// the derived ID is adopted only when it is a trusted consolidated record whose lesson and counters equal those the
/// pass computed.
/// </para>
/// </remarks>
public sealed class ExperienceConsolidationService
{
    private const int MaxRevalidations = 3;

    private const int MaxChainHops = 64;

    private static readonly ExperienceReadOptions ScopeCheckRead = new(ExperienceReadPurpose.ScopeCheck);

    private static readonly IReadOnlyList<ExperienceStatus> Mergeable = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private readonly IExperienceRecordStore _store;
    private readonly ExperienceLifecycleService _lifecycle;
    private readonly TimeProvider _clock;

    /// <summary>Creates a consolidation service.</summary>
    /// <param name="store">The record store the pass reads from and creates consolidated records in.</param>
    /// <param name="lifecycle">
    /// The lifecycle service the supersessions are committed through. Its provenance signing, when configured, signs the
    /// consolidated record and decides which records are vouched for.
    /// </param>
    /// <param name="clock">The clock a consolidated record's creation time is read from; <see langword="null"/> for the system clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="lifecycle"/> is <see langword="null"/>.</exception>
    public ExperienceConsolidationService(IExperienceRecordStore store, ExperienceLifecycleService lifecycle, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _store = store;
        _lifecycle = lifecycle;
        _clock = clock ?? TimeProvider.System;
    }

    private ProvenanceSigner? Signer => _lifecycle.Independence.Signer;

    /// <summary>
    /// Runs one consolidation pass over <paramref name="scope"/>: groups its trusted validated records, creates (or finds)
    /// each group's consolidated record, and supersedes the group's records by it. See the type's remarks for every rule.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do; passed to the store unchanged.</param>
    /// <param name="scope">The exact scope to consolidate. Never treated as authority.</param>
    /// <param name="options">What to merge.</param>
    /// <param name="cancellationToken">Cancels the pass; commits already made stay made, and a re-run finishes the rest.</param>
    /// <returns>
    /// What the pass did, per group and per source. A store refusal of the read (the scope outside the authorization, say)
    /// is a result carrying that outcome, never an exception.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="ExperienceConsolidationOptions.MinimumSources"/> is out of range.</exception>
    /// <exception cref="ArgumentException"><see cref="ExperienceConsolidationOptions.ArgumentKeys"/> names a blank tool or key.</exception>
    /// <exception cref="ExperienceStoreException">Storage infrastructure failed while reading or creating; supersession failures are reported per source instead.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ExperienceConsolidationResult> ConsolidateAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceConsolidationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nameof(options));

        var read = await _store
            .QueryAsync(authorization, new ExperienceRecordQuery(scope, Mergeable, ExperienceRecordQuery.MaxLimit), cancellationToken)
            .ConfigureAwait(false);
        if (read.Outcome != ExperienceStoreOutcome.Found)
        {
            return new(scope, read.Outcome, []) { Errors = read.Errors ?? [] };
        }

        var records = (read.Records ?? []).Where(record => record is not null).ToList();
        var groups = new SortedDictionary<string, List<ExperienceRecord>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Status is not (ExperienceStatus.Validated or ExperienceStatus.Reinforced)
                || !(IsTrustedSource(record, scope) || IsTrustedConsolidated(record, scope))
                || GroupKey(record, options) is not { } key)
            {
                continue;
            }

            if (!groups.TryGetValue(key.Value, out var members))
            {
                groups[key.Value] = members = [];
            }

            members.Add(record);
        }

        var results = new List<ExperienceConsolidationGroupResult>();
        foreach (var (key, members) in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ConsolidateGroupAsync(authorization, scope, key, members, options, cancellationToken).ConfigureAwait(false) is { } result)
            {
                results.Add(result);
            }
        }

        return new(scope, ExperienceStoreOutcome.Found, results)
        {
            RecordsRead = records.Count,
            MayHaveMore = records.Count >= ExperienceRecordQuery.MaxLimit,
        };
    }

    /// <summary>
    /// The consolidated records in <paramref name="scope"/> that list <paramref name="sourceId"/> as a source, so a host
    /// erasing that source can erase them too: erasing a source never reaches a consolidated record that copied its content.
    /// </summary>
    /// <remarks>
    /// Found two ways: along the source's supersession chain (each replacement, and each record that replaced it in turn,
    /// that lists the source), and among the <see cref="ExperienceRecordQuery.MaxLimit"/> most recent records of the scope in
    /// any status. Call it before erasing the source: an erased record has no history left to follow.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the source lies in.</param>
    /// <param name="sourceId">The source record.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The consolidated records found, and whether the scan may have missed older ones.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorization"/> or <paramref name="scope"/> is <see langword="null"/>.</exception>
    /// <exception cref="ExperienceStoreException">Storage infrastructure failed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ExperienceConsolidatedLookupResult> FindConsolidatedContainingAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid sourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);

        var scan = await _store
            .QueryAsync(authorization, new ExperienceRecordQuery(scope, Limit: ExperienceRecordQuery.MaxLimit), cancellationToken)
            .ConfigureAwait(false);
        if (scan.Outcome != ExperienceStoreOutcome.Found)
        {
            return new(scan.Outcome, []);
        }

        var found = new SortedSet<Guid>();
        var scanned = (scan.Records ?? []).Where(record => record is not null).ToList();
        foreach (var record in scanned)
        {
            if (record.Scope == scope && ExperienceConsolidation.SourcesOf(record).Contains(sourceId))
            {
                found.Add(record.ExperienceId);
            }
        }

        var visited = new HashSet<Guid> { sourceId };
        var current = sourceId;
        for (var hop = 0; hop < MaxChainHops; hop++)
        {
            if (await ReplacementOfAsync(authorization, scope, current, cancellationToken).ConfigureAwait(false) is not { } next
                || !visited.Add(next))
            {
                break;
            }

            var read = await _store.GetAsync(authorization, scope, next, ScopeCheckRead, cancellationToken).ConfigureAwait(false);
            if (read is not { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { } replacement })
            {
                break;
            }

            if (replacement.Scope == scope && ExperienceConsolidation.SourcesOf(replacement).Contains(sourceId))
            {
                found.Add(replacement.ExperienceId);
            }

            current = next;
        }

        return new(ExperienceStoreOutcome.Found, [.. found]) { MayHaveMore = scanned.Count >= ExperienceRecordQuery.MaxLimit };
    }

    /// <summary>
    /// Whether <paramref name="record"/> may be a source: finalized by the library, vouched for by its signature when
    /// signing is configured, verified, with a library-written lesson whose content is confirmed. Status is not checked.
    /// </summary>
    private bool IsTrustedSource(ExperienceRecord record, Scope scope) =>
        record.Scope == scope
        && !ExperienceConsolidation.IsConsolidated(record)
        && record.Origin == ExperienceRecordOrigin.Finalized
        && IsVouched(record)
        && record.Outcome?.Status == TaskVerificationStatus.Verified
        && record.Reflection is not null
        && !RecordAuthorship.IsModelAuthored(record, Signer);

    /// <summary>
    /// Whether <paramref name="record"/> is a consolidated record this library wrote: the consolidation producer exactly
    /// (signed content), vouched for by its signature when signing is configured, content confirmed. Status is not checked.
    /// </summary>
    private bool IsTrustedConsolidated(ExperienceRecord record, Scope scope) =>
        record.Scope == scope
        && ExperienceConsolidation.IsConsolidated(record)
        && string.Equals(record.Reflection?.Producer, ExperienceConsolidation.Producer, StringComparison.Ordinal)
        && IsVouched(record)
        && record.Outcome?.Status == TaskVerificationStatus.Verified
        && !RecordAuthorship.IsModelAuthored(record, Signer);

    /// <summary>The independence verifier's rule for a run's record: without signing, anything; with it, a signature that verifies, or a listed unsigned record.</summary>
    private bool IsVouched(ExperienceRecord record) =>
        Signer is not { } signer || signer.Verify(record) is ProvenanceSignatureCheck.Valid or ProvenanceSignatureCheck.TrustedUnsigned;

    /// <summary>
    /// The record's grouping key and what the lesson text says of it, or <see langword="null"/> when it holds no resolved
    /// failure signature or has no working attempt.
    /// </summary>
    internal static GroupingKey? GroupKey(ExperienceRecord record, ExperienceConsolidationOptions options)
    {
        var signatures = ExperienceFailureSignatures.Of(record)
            .Where(entry => entry.Resolution == ExperienceSignatureResolution.Resolved)
            .Select(entry => entry.Signature)
            .DistinctBy(signature => signature.Key, StringComparer.Ordinal)
            .OrderBy(signature => signature.Key, StringComparer.Ordinal)
            .ToList();
        if (signatures.Count == 0 || WorkingCalls(record) is not { Count: > 0 } calls)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var signature in signatures)
        {
            Append(text, "s", signature.Key);
        }

        foreach (var call in calls)
        {
            Append(text, "t", call.ToolName ?? string.Empty);
            if (options.ArgumentKeys is { } allowlist && call.ToolName is { } tool && allowlist.TryGetValue(tool, out var keys))
            {
                foreach (var argument in keys)
                {
                    if (call.Arguments is { } arguments && arguments.TryGetValue(argument, out var value))
                    {
                        byte[] encoded;
                        try
                        {
                            encoded = ProvenanceSigner.EncodeArgumentValue(value);
                        }
                        catch (ArgumentException)
                        {
                            // A value with no canonical encoding cannot be compared, so the record is never merged.
                            return null;
                        }

                        Append(text, "a", argument + "=" + Convert.ToHexStringLower(SHA256.HashData(encoded)));
                    }
                    else
                    {
                        Append(text, "m", argument);
                    }
                }
            }
        }

        return new(text.ToString(), signatures, [.. calls.Select(call => call.ToolName ?? string.Empty)]);

        static void Append(StringBuilder text, string kind, string value) =>
            text.Append(kind).Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    /// <summary>
    /// The working attempt's calls in call order: the final attempt by sequence number, when it ended without an error and
    /// attempt sequence numbers are unique (the block's <c>Worked:</c> rule); otherwise <see langword="null"/>.
    /// </summary>
    private static List<ToolCallRecord>? WorkingCalls(ExperienceRecord record)
    {
        var attempts = (record.Attempts ?? []).Where(attempt => attempt is not null).ToList();
        if (attempts.Count == 0 || attempts.Select(attempt => attempt.SequenceNumber).Distinct().Count() != attempts.Count)
        {
            return null;
        }

        var worked = attempts.MaxBy(attempt => attempt.SequenceNumber)!;
        return worked.Error is null
            ? [.. (worked.ToolCalls ?? []).Where(call => call is not null).OrderBy(call => call.SequenceNumber)]
            : null;
    }

    private async Task<ExperienceConsolidationGroupResult?> ConsolidateGroupAsync(
        AuthorizationContext authorization,
        Scope scope,
        string key,
        List<ExperienceRecord> members,
        ExperienceConsolidationOptions options,
        CancellationToken cancellationToken)
    {
        var plain = members.Where(member => !ExperienceConsolidation.IsConsolidated(member)).ToList();
        var consolidated = members.Where(ExperienceConsolidation.IsConsolidated).ToList();

        for (var attempt = 0; attempt < MaxRevalidations; attempt++)
        {
            // What each consolidated member stands for: the sources it lists that are still trusted sources of this key
            // and that it superseded -- or that are in the group as live records, from an interrupted pass.
            var plainIds = plain.Select(record => record.ExperienceId).ToHashSet();
            var leaves = new Dictionary<Guid, List<ExperienceRecord>>();
            foreach (var member in consolidated)
            {
                leaves[member.ExperienceId] = await LeavesOfAsync(authorization, scope, key, member, plainIds, options, cancellationToken).ConfigureAwait(false);
            }

            // Resuming: a consolidated member that already lists every source the group stands for. The rest are superseded by it.
            var standsFor = plainIds.Concat(leaves.Values.SelectMany(list => list.Select(record => record.ExperienceId))).ToHashSet();
            var covering = consolidated
                .Where(record => ExperienceConsolidation.SourcesOf(record).ToHashSet().IsSupersetOf(standsFor))
                .OrderByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.ExperienceId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
                .FirstOrDefault();
            if (covering is not null)
            {
                var rest = members.Where(member => member.ExperienceId != covering.ExperienceId).ToList();
                if (rest.Count == 0)
                {
                    return null;
                }

                var runs = leaves[covering.ExperienceId].Concat(plain).Select(record => record.SourceRunId).Distinct().Count();
                return Group(
                    covering.ExperienceId,
                    ExperienceConsolidationGroupOutcome.Existing,
                    runs,
                    await SupersedeAsync(authorization, scope, covering, rest, cancellationToken).ConfigureAwait(false),
                    deferred: 0);
            }

            if (members.Count < 2)
            {
                return null;
            }

            // Consolidated members always fit (they cannot be split); plain records fill what is left, newest first.
            var sources = new Dictionary<Guid, ExperienceRecord>();
            foreach (var source in leaves.Values.SelectMany(list => list))
            {
                sources.TryAdd(source.ExperienceId, source);
            }

            if (sources.Count > ExperienceConsolidation.MaxSources)
            {
                return Group(Guid.Empty, ExperienceConsolidationGroupOutcome.Deferred, sources.Select(pair => pair.Value.SourceRunId).Distinct().Count(), [], members.Count) with
                {
                    Reason = $"The group's consolidated records already list more than {ExperienceConsolidation.MaxSources} sources.",
                };
            }

            var included = new List<ExperienceRecord>(consolidated);
            var deferred = 0;
            foreach (var record in plain
                .OrderByDescending(record => record.CreatedAt)
                .ThenByDescending(record => record.ExperienceId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal))
            {
                if (sources.ContainsKey(record.ExperienceId) || sources.Count < ExperienceConsolidation.MaxSources)
                {
                    sources[record.ExperienceId] = record;
                    included.Add(record);
                }
                else
                {
                    deferred++;
                }
            }

            var runIds = sources.Values.Select(source => source.SourceRunId).ToHashSet();
            if (included.Count < 2 || runIds.Count < options.MinimumSources)
            {
                return deferred > 0
                    ? Group(Guid.Empty, ExperienceConsolidationGroupOutcome.Deferred, runIds.Count, [], deferred) with
                    {
                        Reason = $"The consolidated record would list more than {ExperienceConsolidation.MaxSources} sources.",
                    }
                    : null;
            }

            // Right before creating: a member that changed since the pass read it is left out, and the group re-evaluated.
            var changed = await ChangedAsync(authorization, scope, included, cancellationToken).ConfigureAwait(false);
            if (changed.Count > 0)
            {
                plain.RemoveAll(record => changed.Contains(record.ExperienceId));
                consolidated.RemoveAll(record => changed.Contains(record.ExperienceId));
                members.RemoveAll(record => changed.Contains(record.ExperienceId));
                continue;
            }

            var id = ExperienceConsolidation.ConsolidatedIdFor(scope, sources.Keys);
            var target = await BuildAsync(authorization, scope, id, [.. sources.Values], consolidated, options, cancellationToken).ConfigureAwait(false);
            var (record2, outcome, reason) = await CreateOrAdoptAsync(authorization, scope, target, cancellationToken).ConfigureAwait(false);
            if (record2 is null)
            {
                return Group(id, ExperienceConsolidationGroupOutcome.Refused, runIds.Count, [], deferred) with { Reason = reason };
            }

            return Group(
                id,
                outcome,
                runIds.Count,
                await SupersedeAsync(authorization, scope, record2, included, cancellationToken).ConfigureAwait(false),
                deferred);
        }

        return Group(Guid.Empty, ExperienceConsolidationGroupOutcome.Deferred, 0, [], members.Count) with
        {
            Reason = "The group's records kept changing while the pass read them; a later pass will merge them.",
        };

        static ExperienceConsolidationGroupResult Group(
            Guid id,
            ExperienceConsolidationGroupOutcome outcome,
            int runs,
            IReadOnlyList<ExperienceConsolidationSourceResult> sources,
            int deferred) => new(id, outcome, runs, sources) { Deferred = deferred };
    }

    /// <summary>
    /// The sources a consolidated record stands for: those it lists that are still readable in exactly this scope, are
    /// trusted sources of the same grouping key, and were superseded by it (or are live members of the group).
    /// </summary>
    private async Task<List<ExperienceRecord>> LeavesOfAsync(
        AuthorizationContext authorization,
        Scope scope,
        string key,
        ExperienceRecord consolidated,
        HashSet<Guid> liveMembers,
        ExperienceConsolidationOptions options,
        CancellationToken cancellationToken)
    {
        var kept = new List<ExperienceRecord>();
        foreach (var source in await ReadSourcesAsync(authorization, scope, ExperienceConsolidation.SourcesOf(consolidated), cancellationToken).ConfigureAwait(false))
        {
            if (!IsTrustedSource(source, scope) || GroupKey(source, options)?.Value != key)
            {
                continue;
            }

            if (liveMembers.Contains(source.ExperienceId)
                || (source.Status == ExperienceStatus.Superseded
                    && await ReplacementOfAsync(authorization, scope, source.ExperienceId, cancellationToken).ConfigureAwait(false) == consolidated.ExperienceId))
            {
                kept.Add(source);
            }
        }

        return kept;
    }

    /// <summary>The record that superseded <paramref name="experienceId"/>, from its history, or <see langword="null"/>.</summary>
    private async Task<Guid?> ReplacementOfAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken)
    {
        Guid? replacement = null;
        long? after = null;
        while (true)
        {
            var page = await _store
                .GetHistoryAsync(authorization, new ExperienceRecordHistoryQuery(scope, experienceId, ExperienceRecordHistoryQuery.MaxLimit, after), cancellationToken)
                .ConfigureAwait(false);
            if (page.Outcome != ExperienceStoreOutcome.Found)
            {
                return null;
            }

            foreach (var stored in page.Events ?? [])
            {
                if (stored?.Event is { CurrentStatus: ExperienceStatus.Superseded, ReplacementExperienceId: { } id })
                {
                    replacement = id;
                }
            }

            if (page.NextStartAfterRevision is not { } next || (after is { } previous && next <= previous))
            {
                return replacement;
            }

            after = next;
        }
    }

    /// <summary>The IDs of <paramref name="records"/> whose revision or status moved, or that are no longer readable here.</summary>
    private async Task<HashSet<Guid>> ChangedAsync(AuthorizationContext authorization, Scope scope, List<ExperienceRecord> records, CancellationToken cancellationToken)
    {
        var fresh = (await ReadSourcesAsync(authorization, scope, [.. records.Select(record => record.ExperienceId)], cancellationToken).ConfigureAwait(false))
            .ToDictionary(record => record.ExperienceId);
        return records
            .Where(record => !fresh.TryGetValue(record.ExperienceId, out var now) || now.Revision != record.Revision || now.Status != record.Status)
            .Select(record => record.ExperienceId)
            .ToHashSet();
    }

    /// <summary>
    /// The listed records still readable in exactly <paramref name="scope"/> (never through a grant), read as a scope
    /// check. An erased record is left out.
    /// </summary>
    private async Task<List<ExperienceRecord>> ReadSourcesAsync(
        AuthorizationContext authorization,
        Scope scope,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        var found = new List<ExperienceRecord>(ids.Count);
        foreach (var chunk in ids.Chunk(ExperienceRecordGetManyResult.MaxCount))
        {
            var read = await _store.GetManyAsync(authorization, scope, chunk, ScopeCheckRead, cancellationToken).ConfigureAwait(false);
            if (read.Outcome != ExperienceStoreOutcome.Found)
            {
                continue;
            }

            foreach (var result in read.Results ?? [])
            {
                if (result is { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { } record }
                    && record.Scope == scope
                    && chunk.Contains(record.ExperienceId))
                {
                    found.Add(record);
                }
            }
        }

        return found;
    }

    /// <summary>The consolidated record over <paramref name="sources"/>, as it is created: a Candidate at revision 0, signed when signing is configured.</summary>
    private async Task<ExperienceRecord> BuildAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid id,
        List<ExperienceRecord> sources,
        List<ExperienceRecord> consolidated,
        ExperienceConsolidationOptions options,
        CancellationToken cancellationToken)
    {
        var latest = sources
            .OrderByDescending(source => source.CreatedAt)
            .ThenByDescending(source => source.ExperienceId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .First();
        var grouping = GroupKey(latest, options)!;
        var runs = sources.Select(source => source.SourceRunId).Distinct().Count();
        var failedFirst = sources
            .Where(source => ExperienceFailureSignatures.Of(source).Select(entry => entry.Signature.Key).ToHashSet(StringComparer.Ordinal)
                .IsSupersetOf(grouping.Signatures.Select(signature => signature.Key)))
            .Select(source => source.SourceRunId)
            .Distinct()
            .Count();

        var (supporting, contradictions) = await CountAsync(authorization, scope, sources, consolidated, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var reflection = latest.Reflection!;
        var runId = ExperienceConsolidation.RunIdFor(scope, id);

        var record = new ExperienceRecord(
            ExperienceId: id,
            SourceRunId: runId,
            Scope: scope,
            TaskId: latest.TaskId,
            TaskSummary: latest.TaskSummary,
            Attempts: latest.Attempts,
            Outcome: latest.Outcome,
            CompletionScore: latest.CompletionScore,
            Reflection: new Reflection(
                ReflectionId: ExperienceConsolidation.ReflectionIdFor(scope, id),
                ExperienceRunId: runId,
                Lesson: LessonText(runs, latest.CreatedAt, grouping, failedFirst),
                SuccessfulApproaches: reflection.SuccessfulApproaches ?? [],
                FailedApproaches: reflection.FailedApproaches ?? [],
                Preconditions: [.. (reflection.Preconditions ?? []).Where(value => value is not null).Take(ExperienceConsolidation.MaxPreconditions)],
                Warnings: reflection.Warnings ?? [],
                ReuseGuidance: reflection.ReuseGuidance,
                EvidenceIds: reflection.EvidenceIds ?? [],
                VerificationStatus: TaskVerificationStatus.Verified,
                CompletionScore: reflection.CompletionScore,
                VerificationRuleVersion: reflection.VerificationRuleVersion,
                Producer: ExperienceConsolidation.Producer,
                CreatedAt: now),
            Environment: latest.Environment,
            Provenance: new Provenance(ExperienceConsolidation.ProvenanceSource, ExperienceConsolidation.TemplateVersion, now, CorrelationId: null)
            {
                ExposedTo = [.. sources.OrderBy(source => source.ExperienceId).Select(source => new RunExposure(source.ExperienceId, source.Revision))],
            },
            Status: ExperienceStatus.Candidate,
            ReuseConfidence: 0,
            SupportingValidations: supporting,
            Contradictions: contradictions,
            Revision: 0,
            CreatedAt: now,
            UpdatedAt: now);
        // Created at the score its counters explain, as finalization creates every record (stores refuse any other at
        // creation); the confidence engine scores it from the first evidence on, as any record.
        record = record with { ReuseConfidence = ReuseConfidenceHeuristic.Score(supporting, contradictions) };
        return Signer is { } signer ? record with { ProvenanceSignature = signer.Sign(record) } : record;
    }

    /// <summary>The lesson text, template version 1.</summary>
    internal static string LessonText(int runs, DateTimeOffset latest, GroupingKey key, int failedFirst)
    {
        var classes = string.Join(" and ", key.Signatures.Select(signature => $"{signature.ErrorClass} from {signature.ToolName}"));
        var names = key.ToolNames.Take(ExperienceConsolidation.MaxLessonToolNames).ToList();
        var worked = string.Join(" → ", names)
            + (key.ToolNames.Count > names.Count
                ? string.Create(CultureInfo.InvariantCulture, $" (+{key.ToolNames.Count - names.Count} more)")
                : string.Empty);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Verified in {runs} independent runs (latest {latest.UtcDateTime:yyyy-MM-dd}). Failed first with {classes} in {failedFirst} of them. Worked: {worked}.");
    }

    /// <summary>
    /// The merged counters, verified evidence only: every distinct independence key among the sources' own validations
    /// (at most one each) and the counted updates admitted <see cref="ConfidenceEvidenceAdmission.Verified"/> in their
    /// histories and in each merged consolidated record's own. A source's contradictions it started with are counted too,
    /// each on its own (finalization starts every record at none).
    /// </summary>
    private async Task<(int Supporting, int Contradictions)> CountAsync(
        AuthorizationContext authorization,
        Scope scope,
        List<ExperienceRecord> sources,
        List<ExperienceRecord> consolidated,
        CancellationToken cancellationToken)
    {
        var supportingKeys = new HashSet<string>(StringComparer.Ordinal);
        var contradictionKeys = new HashSet<string>(StringComparer.Ordinal);
        long unkeyedContradictions = 0;
        foreach (var source in sources)
        {
            var history = await CountedAsync(authorization, scope, source, cancellationToken).ConfigureAwait(false);
            Add(history);

            // The source's own validation: finalization's, under its run and closed round -- the key the same run's reuse
            // evidence on another record is counted under, so one run is one confirmation. Never more than finalization sets.
            if (source.SupportingValidations - history.Supporting > 0 && ExperienceFinalizationService.InitialSupportingValidations > 0)
            {
                supportingKeys.Add(InitialKey(source));
            }

            unkeyedContradictions += Math.Max(0, source.Contradictions - history.Contradictions);
        }

        foreach (var record in consolidated)
        {
            Add(await CountedAsync(authorization, scope, record, cancellationToken).ConfigureAwait(false));
        }

        return (Saturate(supportingKeys.Count), Saturate(contradictionKeys.Count + unkeyedContradictions));

        void Add(CountedHistory history)
        {
            foreach (var (updateKey, supporting, contradicting) in history.Verified)
            {
                if (supporting > 0)
                {
                    supportingKeys.Add(updateKey);
                }

                if (contradicting > 0)
                {
                    contradictionKeys.Add(updateKey);
                }
            }
        }

        static int Saturate(long value) => (int)Math.Min(int.MaxValue, Math.Max(0, value));
    }

    /// <summary>The key a source's own validation is counted under.</summary>
    private static string InitialKey(ExperienceRecord source) =>
        source.ClosedRoundId is { } round
            ? ConfidenceIndependenceKey.ForMachine(source.SourceRunId, round).Value
            : string.Create(CultureInfo.InvariantCulture, $"initial:{source.SourceRunId:D}");

    /// <summary>
    /// The counted confidence updates in <paramref name="record"/>'s history up to the revision read: their totals (every
    /// admission, to tell what the record started with) and, separately, the verified ones with their keys.
    /// </summary>
    private async Task<CountedHistory> CountedAsync(AuthorizationContext authorization, Scope scope, ExperienceRecord record, CancellationToken cancellationToken)
    {
        var history = new CountedHistory();
        long? after = null;
        while (true)
        {
            var page = await _store
                .GetHistoryAsync(authorization, new ExperienceRecordHistoryQuery(scope, record.ExperienceId, ExperienceRecordHistoryQuery.MaxLimit, after), cancellationToken)
                .ConfigureAwait(false);
            if (page.Outcome != ExperienceStoreOutcome.Found)
            {
                return history;
            }

            foreach (var stored in page.Events ?? [])
            {
                if (stored?.Event?.Confidence is not { Counted: true } update || stored.AppliedRevision > record.Revision)
                {
                    continue;
                }

                var supporting = update.NewSupportingValidations - update.PriorSupportingValidations;
                var contradicting = update.NewContradictions - update.PriorContradictions;
                history.Supporting += supporting;
                history.Contradictions += contradicting;
                if (update.Admission == ConfidenceEvidenceAdmission.Verified && KeyOf(update) is { } updateKey)
                {
                    history.Verified.Add((updateKey, supporting, contradicting));
                }
            }

            if (page.NextStartAfterRevision is not { } next || (after is { } previous && next <= previous))
            {
                return history;
            }

            after = next;
        }
    }

    private static string? KeyOf(ConfidenceUpdate update)
    {
        try
        {
            return ReuseConfidenceHeuristic.IndependenceKeyFor(update).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates <paramref name="target"/> and validates it, or adopts the record an earlier pass stored under the same ID --
    /// only when it is a trusted consolidated record over the same sources, with the same lesson and the counters it
    /// started with equal to those computed now -- and finishes validating it. Returns the record to supersede by, or
    /// <see langword="null"/> and why not.
    /// </summary>
    private async Task<(ExperienceRecord? Record, ExperienceConsolidationGroupOutcome Outcome, string? Reason)> CreateOrAdoptAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceRecord target,
        CancellationToken cancellationToken)
    {
        var outcome = ExperienceConsolidationGroupOutcome.Created;
        var created = await _store.CreateAsync(authorization, target, cancellationToken).ConfigureAwait(false);
        var record = target;
        if (created.Outcome == ExperienceStoreOutcome.Conflict)
        {
            outcome = ExperienceConsolidationGroupOutcome.Existing;
            var existing = await _store.GetAsync(authorization, scope, target.ExperienceId, ScopeCheckRead, cancellationToken).ConfigureAwait(false);
            if (existing is not { Outcome: ExperienceStoreOutcome.Found, SharedByGrant: false, Record: { } stored }
                || !IsTrustedConsolidated(stored, scope)
                || !ExperienceConsolidation.SourcesOf(stored).SequenceEqual(ExperienceConsolidation.SourcesOf(target))
                || !string.Equals(stored.Reflection?.Lesson, target.Reflection?.Lesson, StringComparison.Ordinal))
            {
                return (null, ExperienceConsolidationGroupOutcome.Refused,
                    "A record stored under the consolidated record's derived ID is not the consolidated record this pass computed (its signature, producer, sources or lesson differ).");
            }

            var started = await CountedAsync(authorization, scope, stored, cancellationToken).ConfigureAwait(false);
            if (stored.SupportingValidations - started.Supporting != target.SupportingValidations
                || stored.Contradictions - started.Contradictions != target.Contradictions)
            {
                return (null, ExperienceConsolidationGroupOutcome.Refused,
                    "A record stored under the consolidated record's derived ID started with counters other than those computed from its sources.");
            }

            record = stored;
        }
        else if (created.Outcome != ExperienceStoreOutcome.Created)
        {
            return (null, ExperienceConsolidationGroupOutcome.Refused, $"The store refused the consolidated record ({created.Outcome}).");
        }

        if (record.Status == ExperienceStatus.Candidate)
        {
            var reason = string.Create(
                CultureInfo.InvariantCulture,
                $"Consolidated from {ExperienceConsolidation.SourcesOf(record).Count} verified records of the same failure and fix.");
            var commit = await _lifecycle.CommitAsync(
                authorization,
                new CommitLifecycleTransitionRequest(
                    ExperienceConsolidation.ValidateEventIdFor(scope, record.ExperienceId),
                    record.ExperienceId,
                    scope,
                    ExperienceStatus.Candidate,
                    ExperienceStatus.Validated,
                    reason,
                    ExperienceConsolidation.Producer,
                    record.CreatedAt,
                    ExpectedRevision: record.Revision),
                cancellationToken).ConfigureAwait(false);
            if (commit.Outcome == LifecycleTransitionOutcome.Committed)
            {
                return (record with { Status = ExperienceStatus.Validated, Revision = commit.Revision }, outcome, null);
            }

            var reread = await _store.GetAsync(authorization, scope, record.ExperienceId, ScopeCheckRead, cancellationToken).ConfigureAwait(false);
            record = reread.Record ?? record;
        }

        return ExperienceStatuses.IsEligibleForReuse(record.Status)
            ? (record, outcome, null)
            : (null, ExperienceConsolidationGroupOutcome.Refused, $"The consolidated record is {record.Status}, so nothing is superseded by it.");
    }

    /// <summary>Supersedes each of <paramref name="sources"/> by <paramref name="replacement"/>, at the revision and status read, reporting each.</summary>
    private async Task<IReadOnlyList<ExperienceConsolidationSourceResult>> SupersedeAsync(
        AuthorizationContext authorization,
        Scope scope,
        ExperienceRecord replacement,
        IEnumerable<ExperienceRecord> sources,
        CancellationToken cancellationToken)
    {
        var results = new List<ExperienceConsolidationSourceResult>();
        foreach (var source in sources.OrderBy(source => source.ExperienceId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var commit = await _lifecycle.CommitAsync(
                    authorization,
                    new CommitLifecycleTransitionRequest(
                        ExperienceConsolidation.SupersedeEventIdFor(scope, replacement.ExperienceId, source.ExperienceId),
                        source.ExperienceId,
                        scope,
                        source.Status,
                        ExperienceStatus.Superseded,
                        string.Create(CultureInfo.InvariantCulture, $"Consolidated into {replacement.ExperienceId:D}, one lesson for the same failure and fix."),
                        ExperienceConsolidation.Producer,
                        replacement.CreatedAt,
                        source.Revision,
                        replacement.ExperienceId),
                    cancellationToken).ConfigureAwait(false);
                results.Add(new(source.ExperienceId, commit.Outcome));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031 // One source's failure is reported, and the rest are still superseded.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                results.Add(new(source.ExperienceId, null) { ExceptionType = ex.GetType().Name });
            }
        }

        return results;
    }

    /// <summary>A record's grouping key, with the signatures and tool names its lesson text names.</summary>
    internal sealed record GroupingKey(string Value, IReadOnlyList<ExperienceFailureSignature> Signatures, IReadOnlyList<string> ToolNames);

    private sealed class CountedHistory
    {
        public int Supporting { get; set; }

        public int Contradictions { get; set; }

        public List<(string Key, int Supporting, int Contradicting)> Verified { get; } = [];
    }
}
