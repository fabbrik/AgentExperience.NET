using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.Core.Lifecycle;

/// <summary>
/// What one consolidation pass (<see cref="ExperienceConsolidationService.ConsolidateAsync"/>) merges, and how.
/// </summary>
public sealed record ExperienceConsolidationOptions
{
    /// <summary>The default <see cref="MinimumSources"/>.</summary>
    public const int DefaultMinimumSources = 3;

    /// <summary>
    /// How many distinct source runs a group needs before it is merged into one consolidated record: from 2 to
    /// <see cref="ExperienceConsolidation.MaxSources"/>; <see cref="DefaultMinimumSources"/> (3) by default. Counted over
    /// the runs the group's records came from, so a consolidated record in the group counts every run it was merged from.
    /// </summary>
    public int MinimumSources { get; init; } = DefaultMinimumSources;

    /// <summary>
    /// Per tool name, the argument keys whose values on the working attempt's calls are part of the grouping key, so
    /// <c>retry(delay=0)</c> and <c>retry(delay=30)</c> stay two lessons. <see langword="null"/> (the default) groups on
    /// tool names alone. Name the keys the Historical Reference block shows (the injection's approach-argument allowlist),
    /// so a merged lesson never hides a difference its sources' blocks showed. Tool names and keys are matched ordinally;
    /// values are compared by their canonical encoding (the one provenance signing uses), never by their text.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ArgumentKeys { get; init; }

    /// <summary>Throws when the options cannot be used.</summary>
    internal void Validate(string parameterName)
    {
        if (MinimumSources is < 2 or > ExperienceConsolidation.MaxSources)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                MinimumSources,
                $"{nameof(MinimumSources)} must be from 2 to {ExperienceConsolidation.MaxSources}.");
        }

        if (ArgumentKeys is null)
        {
            return;
        }

        foreach (var (tool, keys) in ArgumentKeys)
        {
            if (string.IsNullOrWhiteSpace(tool) || keys is null || keys.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"{nameof(ArgumentKeys)} must name non-blank tools, each with a non-null list of non-blank argument keys.",
                    parameterName);
            }
        }
    }
}

/// <summary>
/// The fixed names and rules of a consolidated record: how one is recognised, what it lists, and the ID a set of
/// sources derives.
/// </summary>
/// <remarks>
/// <para>
/// A consolidated record is an ordinary <see cref="ExperienceRecord"/> written by
/// <see cref="ExperienceConsolidationService"/>. It is marked in its provenance rather than by a new
/// <see cref="ExperienceRecordOrigin"/> value, so no store, schema or port changes: its
/// <see cref="Provenance.Source"/> is <see cref="ProvenanceSource"/>, its <see cref="Provenance.SourceVersion"/> is
/// <see cref="TemplateVersion"/>, its reflection's <see cref="Reflection.Producer"/> is <see cref="Producer"/>, and its
/// <see cref="Provenance.ExposedTo"/> lists its source records, each at the revision consolidation read it (at most
/// <see cref="MaxSources"/>). Its <see cref="ExperienceRecord.Origin"/> stays <see cref="ExperienceRecordOrigin.HostWritten"/>:
/// it was not derived from one captured run, so it vouches for none. Its <see cref="ExperienceRecord.SourceRunId"/> is a
/// derived identifier no run has.
/// </para>
/// <para>
/// With provenance signing configured it is signed like a finalized record, and the signed claims include
/// <see cref="Provenance.ExposedTo"/>: the source list cannot be changed without the signature failing.
/// </para>
/// </remarks>
public static class ExperienceConsolidation
{
    /// <summary>The <see cref="Provenance.Source"/> of every consolidated record.</summary>
    public const string ProvenanceSource = "AgentExperience.Consolidation";

    /// <summary>The version of the lesson template (and of the record's shape), as its <see cref="Provenance.SourceVersion"/>.</summary>
    public const string TemplateVersion = "1";

    /// <summary>The <see cref="Reflection.Producer"/> of every consolidated record's reflection, and the producer of the lifecycle events consolidation commits.</summary>
    public const string Producer = ProvenanceSource + "/" + TemplateVersion;

    /// <summary>The most source records one consolidated record lists: <see cref="RunExposure.MaxPerRun"/>, the bound on <see cref="Provenance.ExposedTo"/>.</summary>
    public const int MaxSources = RunExposure.MaxPerRun;

    /// <summary>The most preconditions a consolidated record's reflection carries: its most recent source's, cut to this many.</summary>
    public const int MaxPreconditions = 16;

    /// <summary>The most tool names the lesson text's <c>Worked:</c> sentence names; the rest are counted.</summary>
    public const int MaxLessonToolNames = 16;

    /// <summary>The namespace every consolidation identifier is derived under.</summary>
    private static readonly Guid DerivationNamespace = Guid.Parse("6f1d2c0e-28a6-4c57-9b1e-0c0a5d1e2860");

    private const byte RecordIdTag = 1;
    private const byte RunIdTag = 2;
    private const byte ReflectionIdTag = 3;
    private const byte ValidateEventTag = 4;
    private const byte SupersedeEventTag = 5;

    /// <summary>
    /// Whether <paramref name="record"/> is a consolidated record: its provenance source is <see cref="ProvenanceSource"/>
    /// and its reflection's producer starts with <c>AgentExperience.Consolidation/</c>.
    /// </summary>
    /// <param name="record">A record.</param>
    /// <returns>Whether consolidation wrote it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static bool IsConsolidated(ExperienceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return string.Equals(record.Provenance?.Source, ProvenanceSource, StringComparison.Ordinal)
            && record.Reflection?.Producer is { } producer
            && producer.StartsWith(ProvenanceSource + "/", StringComparison.Ordinal);
    }

    /// <summary>The source records a consolidated record lists, in ID order; empty for any other record.</summary>
    /// <param name="record">A record.</param>
    /// <returns>The IDs of the records it was merged from.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<Guid> SourcesOf(ExperienceRecord record) =>
        IsConsolidated(record)
            ? [.. (record.Provenance.ExposedTo ?? []).Where(exposure => exposure is not null).Select(exposure => exposure.ExperienceId).Distinct().Order()]
            : [];

    /// <summary>
    /// The ID of the consolidated record merging exactly <paramref name="sourceIds"/> in <paramref name="scope"/>: a
    /// SHA-256 of a fixed namespace, every scope field (length-prefixed) and the sorted, distinct source IDs, as a
    /// version-8 GUID. The same sources always derive the same ID, so a pass re-run over them finds its own record; a
    /// new source folded in derives a new one.
    /// </summary>
    /// <param name="scope">The scope the sources lie in.</param>
    /// <param name="sourceIds">The source records.</param>
    /// <returns>The derived ID.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static Guid ConsolidatedIdFor(Scope scope, IEnumerable<Guid> sourceIds)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sourceIds);
        var sorted = sourceIds.Distinct().Order().ToList();
        var suffix = new byte[sorted.Count * 16];
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].TryWriteBytes(suffix.AsSpan(i * 16, 16), bigEndian: true, out _);
        }

        return Derive(RecordIdTag, scope, Guid.Empty, suffix);
    }

    /// <summary>The <see cref="ExperienceRecord.SourceRunId"/> of the consolidated record <paramref name="consolidatedId"/>: derived, and no run's.</summary>
    internal static Guid RunIdFor(Scope scope, Guid consolidatedId) => Derive(RunIdTag, scope, consolidatedId, []);

    /// <summary>The <see cref="Reflection.ReflectionId"/> of the consolidated record <paramref name="consolidatedId"/>.</summary>
    internal static Guid ReflectionIdFor(Scope scope, Guid consolidatedId) => Derive(ReflectionIdTag, scope, consolidatedId, []);

    /// <summary>The event ID of the commit that validates the consolidated record <paramref name="consolidatedId"/>.</summary>
    internal static Guid ValidateEventIdFor(Scope scope, Guid consolidatedId) => Derive(ValidateEventTag, scope, consolidatedId, []);

    /// <summary>The event ID of the commit that supersedes <paramref name="sourceId"/> by <paramref name="consolidatedId"/>.</summary>
    internal static Guid SupersedeEventIdFor(Scope scope, Guid consolidatedId, Guid sourceId)
    {
        var suffix = new byte[16];
        sourceId.TryWriteBytes(suffix, bigEndian: true, out _);
        return Derive(SupersedeEventTag, scope, consolidatedId, suffix);
    }

    private static Guid Derive(byte tag, Scope scope, Guid subject, byte[] suffix)
    {
        using var input = new MemoryStream();
        Span<byte> guid = stackalloc byte[16];
        Span<byte> length = stackalloc byte[4];
        DerivationNamespace.TryWriteBytes(guid, bigEndian: true, out _);
        input.Write(guid);
        input.WriteByte(tag);
        subject.TryWriteBytes(guid, bigEndian: true, out _);
        input.Write(guid);
        foreach (var field in new[] { scope.TenantId, scope.ApplicationId, scope.ProjectId, scope.TeamId, scope.AgentId, scope.UserId })
        {
            if (field is null)
            {
                input.WriteByte(0);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            input.WriteByte(1);
            input.Write(length);
            input.Write(bytes);
        }

        input.Write(suffix);
        var hash = SHA256.HashData(input.ToArray());
        var id = hash.AsSpan(0, 16);
        id[6] = (byte)((id[6] & 0x0F) | 0x80);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true);
    }
}

/// <summary>What one consolidation pass did in one scope.</summary>
/// <param name="Scope">The scope consolidated: exactly the one asked for.</param>
/// <param name="Outcome">
/// The store's outcome for the pass's read: <see cref="ExperienceStoreOutcome.Found"/>, or why nothing was read (the scope
/// lies outside the authorization, say). Per-group and per-source outcomes are in <see cref="Groups"/>.
/// </param>
/// <param name="Groups">Every group the pass acted on, in grouping-key order. Groups with nothing to do are not listed.</param>
public sealed record ExperienceConsolidationResult(
    Scope Scope,
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExperienceConsolidationGroupResult> Groups)
{
    /// <summary>How many validated or reinforced records the pass read.</summary>
    public int RecordsRead { get; init; }

    /// <summary>
    /// Whether the scope may hold more validated or reinforced records than one store read answers
    /// (<see cref="ExperienceRecordQuery.MaxLimit"/>, newest first): the older ones were not considered this pass.
    /// </summary>
    public bool MayHaveMore { get; init; }

    /// <summary>The store's validation errors when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>.</summary>
    public IReadOnlyList<StoreValidationError> Errors { get; init; } = [];

    /// <summary>
    /// How many groups are incomplete: not <see cref="ExperienceConsolidationGroupResult.Complete"/> (a consolidated record
    /// coexists with sources it did not supersede, or the group was refused or deferred). Zero when the pass finished
    /// everything it started; otherwise run it again.
    /// </summary>
    public int IncompleteGroups => Groups.Count(group => group is not null && !group.Complete);
}

/// <summary>What happened to one group: records in one scope with the same resolved failure signature and the same working sequence.</summary>
/// <param name="ConsolidatedExperienceId">The consolidated record the group's records are superseded by.</param>
/// <param name="Outcome">Whether that record was created now, already existed, or could not be used.</param>
/// <param name="IndependentRuns">How many distinct source runs it was merged from.</param>
/// <param name="Sources">Each record this pass tried to supersede by it, and the outcome; empty when <see cref="Outcome"/> is <see cref="ExperienceConsolidationGroupOutcome.Refused"/>.</param>
public sealed record ExperienceConsolidationGroupResult(
    Guid ConsolidatedExperienceId,
    ExperienceConsolidationGroupOutcome Outcome,
    int IndependentRuns,
    IReadOnlyList<ExperienceConsolidationSourceResult> Sources)
{
    /// <summary>Why, in words, when <see cref="Outcome"/> is <see cref="ExperienceConsolidationGroupOutcome.Refused"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Records of the group left for a later pass: because the consolidated record would have listed more than
    /// <see cref="ExperienceConsolidation.MaxSources"/> sources, or, with <see cref="ExperienceConsolidationGroupOutcome.Deferred"/>,
    /// because the group could not be merged this pass.
    /// </summary>
    public int Deferred { get; init; }

    /// <summary>
    /// Whether the group is finished: its consolidated record exists and every record this pass tried to supersede by it
    /// is superseded, and nothing was deferred. <see langword="false"/> means the consolidated record and some of its
    /// sources coexist (both can be retrieved) until a later pass finishes, or the group was refused or deferred.
    /// </summary>
    public bool Complete =>
        Outcome is ExperienceConsolidationGroupOutcome.Created or ExperienceConsolidationGroupOutcome.Existing
        && Deferred == 0
        && Sources.All(source => source is { Superseded: true });
}

/// <summary>How a group's consolidated record came to be used.</summary>
public enum ExperienceConsolidationGroupOutcome
{
    /// <summary>This pass created and validated the consolidated record.</summary>
    Created,

    /// <summary>
    /// The consolidated record already existed (an earlier pass, or a concurrent one, created it from the same sources),
    /// and this pass superseded what that pass had not.
    /// </summary>
    Existing,

    /// <summary>
    /// The consolidated record could not be created or used (the store refused it, its ID names another record, or it is
    /// no longer eligible), so nothing was superseded. See <see cref="ExperienceConsolidationGroupResult.Reason"/>.
    /// </summary>
    Refused,

    /// <summary>
    /// The group was not merged this pass: its sources would exceed <see cref="ExperienceConsolidation.MaxSources"/>, or
    /// its records kept changing while the pass read them. <see cref="ExperienceConsolidationGroupResult.Deferred"/> counts
    /// the records left; <see cref="ExperienceConsolidationGroupResult.ConsolidatedExperienceId"/> is <see cref="Guid.Empty"/>.
    /// </summary>
    Deferred,
}

/// <summary>What <see cref="ExperienceConsolidationService.FindConsolidatedContainingAsync"/> found.</summary>
/// <param name="Outcome">The store's outcome for the read: <see cref="ExperienceStoreOutcome.Found"/>, or why nothing was read.</param>
/// <param name="ExperienceIds">The consolidated records that list the source, in ID order.</param>
public sealed record ExperienceConsolidatedLookupResult(ExperienceStoreOutcome Outcome, IReadOnlyList<Guid> ExperienceIds)
{
    /// <summary>
    /// Whether the scope holds more records than one store read answers, so a consolidated record off the source's
    /// supersession chain and older than the most recent <see cref="ExperienceRecordQuery.MaxLimit"/> may have been missed.
    /// </summary>
    public bool MayHaveMore { get; init; }
}

/// <summary>One record a pass tried to supersede by a consolidated record.</summary>
/// <param name="ExperienceId">The source record.</param>
/// <param name="Outcome">
/// The lifecycle commit's outcome: <see cref="LifecycleTransitionOutcome.Committed"/> when it is now superseded; any other
/// value (a <see cref="LifecycleTransitionOutcome.StaleRevision"/> or <see cref="LifecycleTransitionOutcome.StatusMismatch"/>
/// when it changed after the pass read it) when it was left untouched. <see langword="null"/> when the commit threw.
/// </param>
public sealed record ExperienceConsolidationSourceResult(Guid ExperienceId, LifecycleTransitionOutcome? Outcome)
{
    /// <summary>Whether the record is now superseded by the consolidated record.</summary>
    public bool Superseded => Outcome == LifecycleTransitionOutcome.Committed;

    /// <summary>The exception's type name when the commit threw; the record is untouched or superseded as the store left it, and a later pass finishes it.</summary>
    public string? ExceptionType { get; init; }
}
