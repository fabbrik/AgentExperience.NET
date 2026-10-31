using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentExperience.Abstractions;
using AgentExperience.Core.Confidence;
using AgentExperience.Core.Reflections;

namespace AgentExperience.Core.Lifecycle;

/// <summary>
/// One living lesson per situation: an on-demand, deterministic pass that merges the validated records of
/// one scope that failed the same way and were fixed the same way into one consolidated record, which supersedes them
/// through the ordinary lifecycle and carries their combined independent support.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never automatic, never a model.</b> Nothing calls it on the request path or in the background; the host calls
/// <see cref="ConsolidateAsync"/>. Every field of a consolidated record is derived from its sources by fixed rules.
/// </para>
/// <para>
/// <b>The grouping key.</b> Records are considered only in exactly the scope asked for (never another scope's, never one
/// a grant lends), only <see cref="ExperienceStatus.Validated"/> or <see cref="ExperienceStatus.Reinforced"/>, verified,
/// with a reflection that is not model-authored and whose content is confirmed when provenance signing is configured (the
/// injection's own rule), as one store read returns them (<see cref="ExperienceRecordQuery.MaxLimit"/>, newest first;
/// erased and superseded records are never returned). Two records group together when they hold the same set of
/// <em>resolved</em> failure signature keys (the exact <see cref="ExperienceFailureSignature.Key"/>) and their
/// working attempt -- the final attempt, ended without an error, the one the block's <c>Worked:</c> line names -- called
/// the same tools in the same order, with the same values for the argument keys
/// <see cref="ExperienceConsolidationOptions.ArgumentKeys"/> names. A record with no resolved signature or no working
/// attempt is never merged. A group is merged when it has at least two records and they came from at least
/// <see cref="ExperienceConsolidationOptions.MinimumSources"/> distinct runs.
/// </para>
/// <para>
/// <b>The consolidated record.</b> Its ID is <see cref="ExperienceConsolidation.ConsolidatedIdFor"/> over its source
/// records (a consolidated record in the group contributes the sources it lists, so folding in a new source derives a
/// new ID, and re-running over the same sources finds the same record). Its task, summary, attempts, outcome, environment,
/// approaches, warnings and reuse guidance are its most recent source's, so its <c>Tried:</c> and <c>Worked:</c> lines and
/// recipe render as that source's did; its reflection's preconditions are the distinct ones its sources carried (at most
/// <see cref="ExperienceConsolidation.MaxPreconditions"/>); and its lesson is the template
/// <c>Verified in N independent runs (latest yyyy-MM-dd). Failed first with &lt;class&gt; from &lt;tool&gt; in K of them.
/// Worked: &lt;tool&gt; → &lt;tool&gt;.</c> (<see cref="ExperienceConsolidation.TemplateVersion"/>). Its counters are the
/// sources' independent evidence, deduplicated by independence key rather than by record: each source's own validation
/// under its run and closed round, plus every counted confidence update in each source's history (and in a consolidated
/// source's own), so a run that both produced one source and was counted as reuse of another counts once. Its score is
/// the lifecycle service's confidence engine's over those counters. With provenance signing it is signed like a finalized
/// record; see <see cref="ExperienceConsolidation"/> for how it is marked and what it lists.
/// </para>
/// <para>
/// <b>Supersession.</b> The record is created as a <see cref="ExperienceStatus.Candidate"/> and validated, then each source
/// is moved to <see cref="ExperienceStatus.Superseded"/> by it through
/// <see cref="ExperienceLifecycleService.CommitAsync"/>, one commit per source, each at the revision and status the pass
/// read (compare-and-set). A source that changed meanwhile is left untouched and reported
/// (<see cref="ExperienceConsolidationSourceResult"/>); each commit is the store's own transaction, so a source is either
/// superseded or untouched. Every identifier and timestamp of those commits is derived, so a re-run, or a concurrent pass,
/// converges on the same record and finishes what an interrupted pass left. A source left untouched because it changed is
/// folded in by the next pass if it still groups.
/// </para>
/// <para>
/// <b>Reversal.</b> A consolidated record is an ordinary record: contesting it (a contradiction) or revoking it works as for
/// any other, and its sources stay superseded (the transition table never moves a superseded record back). A later
/// verified run in the same situation is finalized into an ordinary record, and the next pass folds it in by superseding
/// the consolidated record with a new one over the larger source set.
/// </para>
/// </remarks>
public sealed class ExperienceConsolidationService
{
    private static readonly ExperienceReadOptions ScopeCheckRead = new(ExperienceReadPurpose.ScopeCheck);

    private static readonly IReadOnlyList<ExperienceStatus> Mergeable = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private readonly IExperienceRecordStore _store;
    private readonly ExperienceLifecycleService _lifecycle;
    private readonly TimeProvider _clock;

    /// <summary>Creates a consolidation service.</summary>
    /// <param name="store">The record store the pass reads from and creates consolidated records in.</param>
    /// <param name="lifecycle">
    /// The lifecycle service the supersessions are committed through. Its confidence engine scores the consolidated record,
    /// and its provenance signing, when configured, signs it and decides whose content is confirmed.
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
    /// Runs one consolidation pass over <paramref name="scope"/>: groups its validated records, creates (or finds) each
    /// group's consolidated record, and supersedes the group's records by it. See the type's remarks for every rule.
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
    /// <exception cref="InvalidOperationException">The confidence engine returned a value that is not a score.</exception>
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
            if (!IsMergeable(record, scope) || GroupKey(record, options) is not { } key)
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
        foreach (var members in groups.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ConsolidateGroupAsync(authorization, scope, members, options, cancellationToken).ConfigureAwait(false) is { } result)
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

    /// <summary>Whether <paramref name="record"/> may be merged at all, whatever it groups with.</summary>
    private bool IsMergeable(ExperienceRecord record, Scope scope) =>
        record.Scope == scope
        && record.Status is ExperienceStatus.Validated or ExperienceStatus.Reinforced
        && record.Outcome?.Status == TaskVerificationStatus.Verified
        && record.Reflection is not null
        && !RecordAuthorship.IsModelAuthored(record, Signer);

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
        List<ExperienceRecord> members,
        ExperienceConsolidationOptions options,
        CancellationToken cancellationToken)
    {
        // Resuming: a consolidated record in the group that already lists every source the group holds was made from
        // them by an earlier (or concurrent) pass, which did not get to supersede the rest.
        var consolidated = members.Where(ExperienceConsolidation.IsConsolidated).ToList();
        var listed = new HashSet<Guid>();
        foreach (var member in members)
        {
            listed.UnionWith(SourcesOrSelf(member));
        }

        var covering = consolidated
            .Where(record => ExperienceConsolidation.SourcesOf(record).ToHashSet().IsSupersetOf(listed))
            .OrderByDescending(record => record.CreatedAt)
            .ThenByDescending(record => record.ExperienceId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .FirstOrDefault();
        if (covering is not null)
        {
            if (members.Count == 1)
            {
                return null;
            }

            var runs = await IndependentRunsOfAsync(authorization, scope, covering, cancellationToken).ConfigureAwait(false);
            return new(
                covering.ExperienceId,
                ExperienceConsolidationGroupOutcome.Existing,
                runs,
                await SupersedeAsync(authorization, scope, covering, members.Where(member => member.ExperienceId != covering.ExperienceId), cancellationToken).ConfigureAwait(false));
        }

        if (members.Count < 2)
        {
            return null;
        }

        // Every source the merged record would list: the plain records themselves, and the sources each consolidated
        // record lists. Consolidated records always fit (they cannot be split); plain ones fill what is left, newest first.
        var plain = members.Where(member => !ExperienceConsolidation.IsConsolidated(member))
            .OrderByDescending(member => member.CreatedAt)
            .ThenByDescending(member => member.ExperienceId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .ToList();
        var sources = new Dictionary<Guid, ExperienceRecord>();
        var nested = new List<(ExperienceRecord Record, List<ExperienceRecord> Sources)>();
        foreach (var member in consolidated)
        {
            var read = await ReadSourcesAsync(authorization, scope, ExperienceConsolidation.SourcesOf(member), cancellationToken).ConfigureAwait(false);
            nested.Add((member, read));
            foreach (var source in read)
            {
                sources.TryAdd(source.ExperienceId, source);
            }
        }

        if (sources.Count > ExperienceConsolidation.MaxSources)
        {
            return null;
        }

        var included = new List<ExperienceRecord>(consolidated);
        var deferred = 0;
        foreach (var member in plain)
        {
            if (sources.ContainsKey(member.ExperienceId))
            {
                included.Add(member);
            }
            else if (sources.Count < ExperienceConsolidation.MaxSources)
            {
                sources.Add(member.ExperienceId, member);
                included.Add(member);
            }
            else
            {
                deferred++;
            }
        }

        var runIds = sources.Values.Select(source => source.SourceRunId).ToHashSet();
        if (included.Count < 2 || runIds.Count < options.MinimumSources)
        {
            return null;
        }

        var id = ExperienceConsolidation.ConsolidatedIdFor(scope, sources.Keys);
        var target = await BuildAsync(authorization, scope, id, members[0], [.. sources.Values], nested, options, cancellationToken).ConfigureAwait(false);
        var (record, outcome, reason) = await CreateAndValidateAsync(authorization, scope, target, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new(id, ExperienceConsolidationGroupOutcome.Refused, runIds.Count, []) { Reason = reason, Deferred = deferred };
        }

        return new(
            id,
            outcome,
            runIds.Count,
            await SupersedeAsync(authorization, scope, record, included, cancellationToken).ConfigureAwait(false))
        {
            Deferred = deferred,
        };
    }

    /// <summary>The sources a record stands for: those it lists when consolidated, else itself.</summary>
    private static IEnumerable<Guid> SourcesOrSelf(ExperienceRecord record) =>
        ExperienceConsolidation.IsConsolidated(record) ? ExperienceConsolidation.SourcesOf(record) : [record.ExperienceId];

    /// <summary>How many distinct runs a consolidated record's readable sources came from.</summary>
    private async Task<int> IndependentRunsOfAsync(AuthorizationContext authorization, Scope scope, ExperienceRecord record, CancellationToken cancellationToken) =>
        (await ReadSourcesAsync(authorization, scope, ExperienceConsolidation.SourcesOf(record), cancellationToken).ConfigureAwait(false))
            .Select(source => source.SourceRunId)
            .Distinct()
            .Count();

    /// <summary>
    /// The listed sources still readable in exactly <paramref name="scope"/> (never through a grant), read as a scope
    /// check. An erased source is left out: its content is gone, and so is its part in a later merge.
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
        ExperienceRecord any,
        List<ExperienceRecord> sources,
        List<(ExperienceRecord Record, List<ExperienceRecord> Sources)> nested,
        ExperienceConsolidationOptions options,
        CancellationToken cancellationToken)
    {
        var latest = sources
            .OrderByDescending(source => source.CreatedAt)
            .ThenByDescending(source => source.ExperienceId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .First();
        var key = GroupKey(latest, options) ?? GroupKey(any, options)!;
        var runs = sources.Select(source => source.SourceRunId).Distinct().Count();
        var failedFirst = sources
            .Where(source => ExperienceFailureSignatures.Of(source).Select(entry => entry.Signature.Key).ToHashSet(StringComparer.Ordinal)
                .IsSupersetOf(key.Signatures.Select(signature => signature.Key)))
            .Select(source => source.SourceRunId)
            .Distinct()
            .Count();

        var (supporting, contradictions) = await CountAsync(authorization, scope, sources, nested, cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var reflection = latest.Reflection!;
        var preconditions = sources
            .OrderByDescending(source => source.CreatedAt)
            .ThenByDescending(source => source.ExperienceId.ToString("D", CultureInfo.InvariantCulture), StringComparer.Ordinal)
            .SelectMany(source => source.Reflection?.Preconditions ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(ExperienceConsolidation.MaxPreconditions)
            .ToList();
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
                Lesson: LessonText(runs, latest.CreatedAt, key, failedFirst),
                SuccessfulApproaches: reflection.SuccessfulApproaches ?? [],
                FailedApproaches: reflection.FailedApproaches ?? [],
                Preconditions: preconditions,
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
        record = record with { ReuseConfidence = _lifecycle.Confidence.Score(record, supporting, contradictions) };
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
    /// The merged counters: every distinct independence key among the sources' own validations and the counted updates
    /// in their histories and in each consolidated member's own, plus what carries no key, counted on its own.
    /// </summary>
    private async Task<(int Supporting, int Contradictions)> CountAsync(
        AuthorizationContext authorization,
        Scope scope,
        List<ExperienceRecord> sources,
        List<(ExperienceRecord Record, List<ExperienceRecord> Sources)> nested,
        CancellationToken cancellationToken)
    {
        var tally = new Tally();
        var perSource = new Dictionary<Guid, Tally>();
        foreach (var source in sources)
        {
            var own = new Tally();
            var counted = await CountedAsync(authorization, scope, source, cancellationToken).ConfigureAwait(false);
            own.AddHistory(counted);

            // The source's own validation: finalization's, under its run and closed round -- the key the same run's reuse
            // evidence on another record is counted under, so one run is one confirmation.
            var initialSupporting = Math.Max(0, source.SupportingValidations - counted.Supporting);
            if (initialSupporting > 0)
            {
                own.SupportingKeys.Add(source.ClosedRoundId is { } round
                    ? ConfidenceIndependenceKey.ForMachine(source.SourceRunId, round).Value
                    : string.Create(CultureInfo.InvariantCulture, $"initial:{source.SourceRunId:D}"));
                own.UnkeyedSupporting += initialSupporting - 1;
            }

            own.UnkeyedContradictions += Math.Max(0, source.Contradictions - counted.Contradictions);
            perSource[source.ExperienceId] = own;
            tally.Add(own);
        }

        foreach (var (record, listed) in nested)
        {
            var counted = await CountedAsync(authorization, scope, record, cancellationToken).ConfigureAwait(false);
            tally.AddHistory(counted);

            // What the consolidated record started with that its readable sources no longer account for (a source erased
            // since, say) is kept, counted on its own, rather than lost.
            var fromSources = new Tally();
            foreach (var source in listed)
            {
                if (perSource.TryGetValue(source.ExperienceId, out var own))
                {
                    fromSources.Add(own);
                }
            }

            tally.UnkeyedSupporting += Math.Max(0, record.SupportingValidations - counted.Supporting - fromSources.SupportingCount);
            tally.UnkeyedContradictions += Math.Max(0, record.Contradictions - counted.Contradictions - fromSources.ContradictionCount);
        }

        return (tally.SupportingCount, tally.ContradictionCount);
    }

    /// <summary>The counted confidence updates in <paramref name="record"/>'s history up to the revision read.</summary>
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
                history.Updates.Add((KeyOf(update), supporting, contradicting));
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
    /// Creates <paramref name="target"/> and validates it, or finds the one an earlier pass created under the same ID and
    /// finishes validating it. Returns the record to supersede by, or <see langword="null"/> and why not.
    /// </summary>
    private async Task<(ExperienceRecord? Record, ExperienceConsolidationGroupOutcome Outcome, string? Reason)> CreateAndValidateAsync(
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
                || stored.Scope != scope
                || !ExperienceConsolidation.IsConsolidated(stored)
                || !ExperienceConsolidation.SourcesOf(stored).SequenceEqual(ExperienceConsolidation.SourcesOf(target)))
            {
                return (null, ExperienceConsolidationGroupOutcome.Refused, "The consolidated record's derived ID names another record.");
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

        public List<(string? Key, int Supporting, int Contradicting)> Updates { get; } = [];
    }

    private sealed class Tally
    {
        public HashSet<string> SupportingKeys { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ContradictionKeys { get; } = new(StringComparer.Ordinal);

        public int UnkeyedSupporting { get; set; }

        public int UnkeyedContradictions { get; set; }

        public int SupportingCount => Saturate((long)SupportingKeys.Count + UnkeyedSupporting);

        public int ContradictionCount => Saturate((long)ContradictionKeys.Count + UnkeyedContradictions);

        public void AddHistory(CountedHistory history)
        {
            foreach (var (key, supporting, contradicting) in history.Updates)
            {
                if (supporting > 0)
                {
                    if (key is null)
                    {
                        UnkeyedSupporting += supporting;
                    }
                    else
                    {
                        SupportingKeys.Add(key);
                    }
                }

                if (contradicting > 0)
                {
                    if (key is null)
                    {
                        UnkeyedContradictions += contradicting;
                    }
                    else
                    {
                        ContradictionKeys.Add(key);
                    }
                }
            }
        }

        public void Add(Tally other)
        {
            SupportingKeys.UnionWith(other.SupportingKeys);
            ContradictionKeys.UnionWith(other.ContradictionKeys);
            UnkeyedSupporting += other.UnkeyedSupporting;
            UnkeyedContradictions += other.UnkeyedContradictions;
        }

        private static int Saturate(long value) => (int)Math.Min(int.MaxValue, Math.Max(0, value));
    }
}
