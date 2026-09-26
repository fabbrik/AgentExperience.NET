using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;

namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// One record a session has been given: which record, at which revision, whether the block showed it an
/// <c>Approach:</c> line only a grant permitted, and whether a withdrawal notice for it has since been
/// delivered.
/// </summary>
/// <param name="ExperienceId">The record.</param>
/// <param name="Revision">The revision that was rendered. A strictly newer one may be injected again.</param>
/// <param name="ApproachByGrant">
/// <see langword="true"/> when the record was borrowed through a grant whose level showed its approach
/// (<see cref="AgentExperience.Abstractions.ExperienceGrantDisclosure.LessonAndApproach"/>), so a later
/// grant that withholds the approach withdraws what the session was shown.
/// </param>
/// <param name="Withdrawn"><see langword="true"/> once a withdrawal notice for this delivery has been delivered.</param>
/// <param name="Confirmed">
/// <see langword="true"/> when MAF reported the invocation that delivered it succeeded, so the session's
/// history holds the block. An unconfirmed delivery (charged from a stage nothing settled) is still
/// re-checked and withdrawn like any other, but it is not deduplicated against: the model may never have
/// seen it, so it may be delivered again.
/// </param>
internal sealed record DeliveredRecord(Guid ExperienceId, long Revision, bool ApproachByGrant, bool Withdrawn, bool Confirmed = true)
{
    /// <summary>
    /// The grant whose owner allowlist the block applied when it showed this borrowed record's argument values
    /// (<see cref="AgentExperience.Abstractions.ExperienceGrantDisclosure.LessonApproachAndArguments"/>), or
    /// <see langword="null"/> when it showed none through a grant. <see cref="Guid.Empty"/> when the store named no
    /// grant. A later read through any other grant, or at a lower level, withdraws the delivery.
    /// </summary>
    public Guid? ArgumentsGrantId { get; init; }
}

/// <summary>
/// What one invocation handed to MAF and is waiting to be charged for: settled by
/// <see cref="ExperienceContextProvider"/> when MAF reports how the invocation ended.
/// </summary>
/// <param name="Stage">
/// Identifies the invocation that staged it. The injected message carries the same value under
/// <see cref="ExperienceContextProvider.StageKey"/>, so only the invocation whose request held that block
/// can settle it.
/// </param>
/// <param name="Bytes">The injected block's UTF-8 size.</param>
/// <param name="Delivered">The records the block carried, as they will be tracked.</param>
/// <param name="Withdrawn">The records whose withdrawal notice the block carried.</param>
internal sealed record PendingDelivery(Guid Stage, int Bytes, IReadOnlyList<DeliveredRecord> Delivered, IReadOnlyList<Guid> Withdrawn);

/// <summary>
/// The injection state one <see cref="AgentSession"/> carries in its <see cref="AgentSession.StateBag"/>
/// under the provider's <see cref="ExperienceInjectionOptions.SessionStateKey"/>: what the session has been charged, what
/// it has been given, and what one invocation staged. Immutable: every change is a new instance, saved
/// whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>The state is host-held data, so it is parsed strictly.</b> It travels wherever the host keeps the
/// session. A value that does not parse, names a version this build does not know, carries a negative
/// counter, more than <see cref="ExperienceInjectionSessionLimits.MaxTrackedRecords"/> entries, a
/// duplicate or an empty ID is not trusted: the provider injects nothing and says so rather than resetting
/// the budget or forgetting what it owes. It is also never overwritten, so a host can inspect it.
/// </para>
/// <para>
/// A host that can edit its session storage can reset the budget by removing the key; this state is only
/// as trustworthy as that storage. What it cannot do by editing it is widen a read: every ID it names is
/// re-read in the request's own authorization and scope, and a notice for one only ever says it is withdrawn.
/// </para>
/// </remarks>
internal sealed record InjectionSessionState(
    long BytesUsed,
    int RecordsUsed,
    IReadOnlyList<DeliveredRecord> Delivered,
    PendingDelivery? Pending)
{
    /// <summary>The only state format this build reads and writes.</summary>
    internal const int FormatVersion = 1;

    /// <summary>A session nothing has been injected into.</summary>
    public static InjectionSessionState Empty { get; } = new(0, 0, [], null);

    /// <summary>The record deliveries in this session that have not been withdrawn, in delivery order.</summary>
    public IEnumerable<DeliveredRecord> Active => Delivered.Where(entry => !entry.Withdrawn);

    /// <summary>The tracked delivery of <paramref name="experienceId"/> that still stands, or <see langword="null"/>.</summary>
    public DeliveredRecord? ActiveFor(Guid experienceId)
    {
        foreach (var entry in Delivered)
        {
            if (entry.ExperienceId == experienceId)
            {
                return entry.Withdrawn ? null : entry;
            }
        }

        return null;
    }

    /// <summary>
    /// The grant to track for a delivery the session is unsure about: either rendering may be the one the model has.
    /// When both showed values through different grants, a fresh ID no read will ever name, so the next re-check
    /// withdraws the delivery rather than trusting either grant's keys.
    /// </summary>
    private static Guid? MergeArgumentsGrant(Guid? staged, Guid? earlier) =>
        staged is { } now && earlier is { } before && now != before ? Guid.NewGuid() : staged ?? earlier;

    /// <summary>
    /// Charges the staged delivery and folds it into what the session holds: a record delivered again
    /// replaces its entry, a withdrawn one is marked, and the counters grow (saturating, never wrapping).
    /// Returns this instance when nothing is staged.
    /// </summary>
    /// <param name="settled">
    /// <see langword="true"/> only when MAF reported the invocation that staged it succeeded. A stage
    /// nothing settled (an abandoned stream, a concurrent invocation's) is charged, and its records are
    /// tracked so they can still be withdrawn -- but as unconfirmed, so they are not deduplicated against,
    /// and its withdrawal notices are not marked delivered, because its block may never have reached the
    /// history: an owed notice sent twice costs a line, one lost costs the withdrawal.
    /// </param>
    public InjectionSessionState Commit(bool settled = true)
    {
        if (Pending is not { } pending)
        {
            return this;
        }

        var entries = Delivered.ToList();
        foreach (var staged in pending.Delivered)
        {
            var at = entries.FindIndex(entry => entry.ExperienceId == staged.ExperienceId);
            var delivered = settled
                ? staged with { Confirmed = true }
                : staged with
                {
                    Confirmed = false,

                    // Unsure which rendering the model has: keep the wider one, so a narrowed grant still
                    // withdraws it.
                    ApproachByGrant = staged.ApproachByGrant || (at >= 0 && entries[at] is { Withdrawn: false, ApproachByGrant: true }),
                    ArgumentsGrantId = MergeArgumentsGrant(staged.ArgumentsGrantId, at >= 0 && !entries[at].Withdrawn ? entries[at].ArgumentsGrantId : null),
                };
            if (at >= 0)
            {
                entries[at] = delivered;
            }
            else
            {
                entries.Add(delivered);
            }
        }

        foreach (var withdrawn in settled ? pending.Withdrawn : [])
        {
            var at = entries.FindIndex(entry => entry.ExperienceId == withdrawn);
            if (at >= 0)
            {
                entries[at] = entries[at] with { Withdrawn = true };
            }
        }

        return new InjectionSessionState(
            Saturate(BytesUsed + pending.Bytes),
            (int)Math.Min((long)RecordsUsed + pending.Delivered.Count, int.MaxValue),
            entries,
            Pending: null);
    }

    /// <summary>Drops the staged delivery without charging it: the invocation it belonged to failed.</summary>
    public InjectionSessionState Discard() => Pending is null ? this : this with { Pending = null };

    /// <summary>
    /// Reads the state from <paramref name="bag"/> under <paramref name="key"/>. <see langword="true"/> with
    /// <see cref="Empty"/> when the key is absent; <see langword="false"/> when a value is present and does not
    /// parse or validate.
    /// </summary>
    public static bool TryLoad(AgentSessionStateBag bag, string key, out InjectionSessionState state) =>
        TryLoad(bag, key, out state, out _);

    /// <summary>As <see cref="TryLoad(AgentSessionStateBag, string, out InjectionSessionState)"/>, also saying whether the key was absent.</summary>
    public static bool TryLoad(AgentSessionStateBag bag, string key, out InjectionSessionState state, out bool absent)
    {
        state = Empty;
        absent = false;

        // Read as a raw JSON node and parsed here, never through TryGetValue<T> of the typed model: that
        // answers false both for "absent" and for "present but another shape", and treating a value that
        // does not parse as absent would reset the budget and forget every retraction owed. A serialized
        // value always reads as a node (a JSON null as a null node), but a value some in-process code set
        // as another type does not, so a false here is confirmed against the bag's own serialized form
        // before it is believed. The provider writes the key on first use, so that is paid once per session.
        if (bag.TryGetValue<JsonNode>(key, out var node, InjectionSessionJsonContext.Default.Options))
        {
            return TryParse(node, out state);
        }

        var serialized = bag.Serialize();
        if (serialized.ValueKind == JsonValueKind.Object
            && serialized.TryGetProperty(key, out _))
        {
            return false;
        }

        absent = true;
        return true;
    }

    /// <summary>Writes this state to <paramref name="bag"/> under <paramref name="key"/>, replacing whatever was there.</summary>
    public void Save(AgentSessionStateBag bag, string key) =>
        bag.SetValue(key, ToNode(), InjectionSessionJsonContext.Default.Options);

    /// <summary>This state as the JSON the bag stores.</summary>
    internal JsonNode ToNode() => JsonSerializer.SerializeToNode(
        new SessionStateDocument
        {
            Version = FormatVersion,
            BytesUsed = BytesUsed,
            RecordsUsed = RecordsUsed,
            Delivered = Delivered.Select(Document).ToList(),
            Pending = Pending is { } pending
                ? new PendingDocument
                {
                    Stage = pending.Stage,
                    Bytes = pending.Bytes,
                    Delivered = pending.Delivered.Select(Document).ToList(),
                    Withdrawn = pending.Withdrawn.ToList(),
                }
                : null,
        },
        InjectionSessionJsonContext.Default.SessionStateDocument)!;

    /// <summary>Parses and validates one stored state. See the type's remarks for what is refused.</summary>
    internal static bool TryParse(JsonNode? node, out InjectionSessionState state)
    {
        state = Empty;
        SessionStateDocument? document;
        try
        {
            if (node is not JsonObject)
            {
                return false;
            }

            document = node.Deserialize(InjectionSessionJsonContext.Default.SessionStateDocument);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }

        if (document is not { Version: FormatVersion, BytesUsed: >= 0, RecordsUsed: >= 0, Delivered: { } delivered }
            || !TryEntries(delivered, out var entries))
        {
            return false;
        }

        PendingDelivery? pending = null;
        if (document.Pending is { } staged)
        {
            if (staged is not { Bytes: >= 0, Delivered: { } stagedDelivered, Withdrawn: { } withdrawn }
                || staged.Stage == Guid.Empty
                || !TryEntries(stagedDelivered, out var stagedEntries)
                || stagedEntries.Any(entry => entry.Withdrawn)
                || withdrawn.Count > ExperienceInjectionSessionLimits.MaxTrackedRecords
                || withdrawn.Contains(Guid.Empty)
                || withdrawn.Distinct().Count() != withdrawn.Count
                || entries.Select(entry => entry.ExperienceId).Union(stagedEntries.Select(entry => entry.ExperienceId)).Count()
                    > ExperienceInjectionSessionLimits.MaxTrackedRecords)
            {
                return false;
            }

            pending = new PendingDelivery(staged.Stage, staged.Bytes, stagedEntries, withdrawn);
        }

        state = new InjectionSessionState(document.BytesUsed, document.RecordsUsed, entries, pending);
        return true;
    }

    private static bool TryEntries(List<DeliveredDocument?> documents, out List<DeliveredRecord> entries)
    {
        entries = [];
        if (documents.Count > ExperienceInjectionSessionLimits.MaxTrackedRecords)
        {
            return false;
        }

        var seen = new HashSet<Guid>();
        foreach (var document in documents)
        {
            if (document is not { Revision: >= 0 } || document.ExperienceId == Guid.Empty || !seen.Add(document.ExperienceId))
            {
                return false;
            }

            entries.Add(new DeliveredRecord(document.ExperienceId, document.Revision, document.ApproachByGrant, document.Withdrawn, document.Confirmed)
            {
                ArgumentsGrantId = document.ArgumentsGrantId,
            });
        }

        return true;
    }

    private static DeliveredDocument? Document(DeliveredRecord entry) => new()
    {
        ExperienceId = entry.ExperienceId,
        Revision = entry.Revision,
        ApproachByGrant = entry.ApproachByGrant,
        Withdrawn = entry.Withdrawn,
        Confirmed = entry.Confirmed,
        ArgumentsGrantId = entry.ArgumentsGrantId,
    };

    private static long Saturate(long value) => value < 0 ? long.MaxValue : value;
}

/// <summary>The stored shape of <see cref="InjectionSessionState"/>. Every member is required.</summary>
internal sealed class SessionStateDocument
{
    [JsonPropertyName("v")]
    [JsonRequired]
    public int Version { get; set; }

    [JsonPropertyName("bytes")]
    [JsonRequired]
    public long BytesUsed { get; set; }

    [JsonPropertyName("records")]
    [JsonRequired]
    public int RecordsUsed { get; set; }

    [JsonPropertyName("delivered")]
    [JsonRequired]
    public List<DeliveredDocument?>? Delivered { get; set; }

    [JsonPropertyName("pending")]
    public PendingDocument? Pending { get; set; }
}

/// <summary>The stored shape of <see cref="DeliveredRecord"/>.</summary>
internal sealed class DeliveredDocument
{
    [JsonPropertyName("id")]
    [JsonRequired]
    public Guid ExperienceId { get; set; }

    [JsonPropertyName("rev")]
    [JsonRequired]
    public long Revision { get; set; }

    [JsonPropertyName("grantApproach")]
    [JsonRequired]
    public bool ApproachByGrant { get; set; }

    [JsonPropertyName("withdrawn")]
    [JsonRequired]
    public bool Withdrawn { get; set; }

    [JsonPropertyName("confirmed")]
    [JsonRequired]
    public bool Confirmed { get; set; }

    /// <summary>
    /// Optional, and omitted when null, so a state that never showed a borrowed argument value is exactly what
    /// a build without this member writes and reads.
    /// </summary>
    [JsonPropertyName("grantArgs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ArgumentsGrantId { get; set; }
}

/// <summary>The stored shape of <see cref="PendingDelivery"/>.</summary>
internal sealed class PendingDocument
{
    [JsonPropertyName("stage")]
    [JsonRequired]
    public Guid Stage { get; set; }

    [JsonPropertyName("bytes")]
    [JsonRequired]
    public int Bytes { get; set; }

    [JsonPropertyName("delivered")]
    [JsonRequired]
    public List<DeliveredDocument?>? Delivered { get; set; }

    [JsonPropertyName("withdrawn")]
    [JsonRequired]
    public List<Guid>? Withdrawn { get; set; }
}

/// <summary>
/// Source-generated serialization for the session state, so it neither depends on reflection nor on
/// whatever options MAF's state bag defaults to. Unknown members are refused.
/// </summary>
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SessionStateDocument))]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class InjectionSessionJsonContext : JsonSerializerContext;
