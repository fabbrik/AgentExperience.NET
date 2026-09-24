using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.Core.Tests;

/// <summary>
/// A deterministic <see cref="IExperienceEmbeddingGenerator"/>: the vector is derived from a SHA-256
/// of the text, so the same text always embeds to the same vector and semantically-unrelated texts
/// embed to unrelated directions -- with no model, no network, and no credentials. Every integration
/// test in this story runs on this rather than on a live provider.
/// </summary>
internal sealed class FakeEmbeddingGenerator : IExperienceEmbeddingGenerator
{
    /// <summary>The well-known failure a scripted provider outage throws, so a test can assert on identity rather than on a message.</summary>
    public static readonly InvalidOperationException ThrownException = new("scripted embedding provider failure");

    public string ModelId { get; init; } = "fake-embed-v1";

    public int Dimension { get; init; } = 4;

    /// <summary>When set, every call throws this instead of embedding. Settable, so a test can script an outage part-way through a drive.</summary>
    public Exception? Throws { get; set; }

    /// <summary>When set, the returned vector has this many components instead of <see cref="Dimension"/>.</summary>
    public int? ReturnDimension { get; init; }

    /// <summary>When set, the returned vector is the right width but holds a NaN.</summary>
    public bool ReturnNonFinite { get; init; }

    /// <summary>When set, the call blocks on this before returning, so a test can hold the provider open.</summary>
    public TaskCompletionSource? Gate { get; init; }

    /// <summary>Every text this generator was asked to embed, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>The vector this generator produces for <paramref name="text"/>, without going through the port.</summary>
    public static ReadOnlyMemory<float> VectorFor(string text, int dimension)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[dimension];
        for (var i = 0; i < dimension; i++)
        {
            // Two bytes per component, mapped into [-1, 1]. Deterministic and always finite.
            var raw = (hash[(i * 2) % hash.Length] << 8) | hash[((i * 2) + 1) % hash.Length];
            vector[i] = ((raw / 65535f) * 2f) - 1f;
        }

        return vector;
    }

    /// <summary>Every batch this generator was asked to embed, in order: one entry per provider round trip.</summary>
    public List<IReadOnlyList<string>> Batches { get; } = [];

    /// <summary>The zero-based batch numbers that throw <see cref="ThrownException"/>, so a test can fail one batch in the middle of a pass.</summary>
    public HashSet<int> FailingBatches { get; init; } = [];

    /// <summary>A batch that contains any of these texts throws <see cref="ThrownException"/>: a record the provider always refuses.</summary>
    public HashSet<string> FailingTexts { get; init; } = [];

    /// <summary>When set, every batch comes back this many vectors short of one per input.</summary>
    public int? ShortBy { get; init; }

    /// <summary>One provider round trip for the whole batch, as a batch-capable provider makes.</summary>
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        int number;
        lock (Batches)
        {
            number = Batches.Count;
            Batches.Add(texts.ToArray());
        }

        if (FailingBatches.Contains(number) || texts.Any(FailingTexts.Contains))
        {
            throw ThrownException;
        }

        var produced = new List<ReadOnlyMemory<float>>(texts.Count);
        foreach (var text in texts)
        {
            produced.Add(await EmbedAsync(text, cancellationToken));
        }

        return ShortBy is { } shortBy ? produced.Take(Math.Max(0, produced.Count - shortBy)).ToList() : produced;
    }

    public async Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        lock (Batches)
        {
            Batches.Add([text]);
        }

        return await EmbedAsync(text, cancellationToken);
    }

    private async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(text);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Throws is not null)
        {
            throw Throws;
        }

        var vector = VectorFor(text, ReturnDimension ?? Dimension);
        if (ReturnNonFinite)
        {
            var poisoned = vector.ToArray();
            poisoned[0] = float.NaN;
            return poisoned;
        }

        return vector;
    }
}

/// <summary>
/// An in-memory <see cref="IExperienceEmbeddingIndex"/> that reproduces the adapter's contract rather
/// than merely returning canned values: <see cref="Records"/> is the canonical world it can see, and a
/// write only lands while the named record is in it at exactly the named revision. Deleting a record
/// from <see cref="Records"/> is "the record was deleted"; bumping its revision is "the record moved
/// on". Everything else is scripted through the hooks.
/// </summary>
internal sealed class FakeEmbeddingIndex : IExperienceEmbeddingIndex
{
    /// <summary>The well-known failure a scripted index outage throws.</summary>
    public static readonly ExperienceStoreException ThrownException = new("scripted embedding index failure");

    /// <summary>One canonical record as the index can see it.</summary>
    /// <param name="Revision">The record's current revision. A write only lands while it still matches.</param>
    /// <param name="Summary">The record's normalized retrieval summary.</param>
    /// <param name="Status">The record's lifecycle status, which the scan filters on exactly as the search does.</param>
    /// <param name="ReuseConfidence">The record's reuse confidence, filtered the same way.</param>
    public sealed record Row(
        long Revision,
        string Summary,
        ExperienceStatus Status = ExperienceStatus.Validated,
        double ReuseConfidence = 0.8);

    /// <summary>The records that currently exist, keyed by ID. A missing key is a deleted record.</summary>
    public Dictionary<Guid, Row> Records { get; } = [];

    /// <summary>The vectors currently stored, keyed by record ID.</summary>
    public Dictionary<Guid, (ExperienceEmbeddingDescriptor Descriptor, ReadOnlyMemory<float> Vector)> Stored { get; } = [];

    /// <summary>Every write this index was asked to apply, in order -- including the ones it rejected.</summary>
    public List<ExperienceIndexWrite> Writes { get; } = [];

    /// <summary>Every scan this index was asked for, in order.</summary>
    public List<ExperienceIndexScan> Scans { get; } = [];

    /// <summary>Every vector search this index was asked for, in order.</summary>
    public List<ExperienceVectorQuery> Queries { get; } = [];

    /// <summary>When set, every scan throws this. Settable, so a test can script an outage part-way through a drive.</summary>
    public Exception? ScanThrows { get; set; }

    /// <summary>When set, every write throws this. Settable, so a test can script an outage part-way through a drive.</summary>
    public Exception? WriteThrows { get; set; }

    /// <summary>When set, every search throws this. Settable, so a test can script an outage part-way through a drive.</summary>
    public Exception? SearchThrows { get; set; }

    /// <summary>When set, every scan returns this outcome instead of listing anything.</summary>
    public ExperienceStoreOutcome? ScanOutcome { get; init; }

    /// <summary>When set, runs just before a write is applied -- the seam for "the record moved while the write was in flight".</summary>
    public Action<ExperienceIndexWrite>? BeforeWrite { get; init; }

    /// <summary>When set, answers every vector search instead of the default empty result.</summary>
    public Func<ExperienceVectorQuery, ExperienceVectorSearchResult>? OnSearch { get; init; }

    /// <summary>Every removal this index was asked for, in order -- including the ones that found nothing.</summary>
    public List<(Scope Scope, Guid ExperienceId)> Removals { get; } = [];

    /// <summary>When set, every removal throws this. Settable, so a test can script an outage part-way through a drive.</summary>
    public Exception? RemoveThrows { get; set; }

    /// <summary>When set, runs before a removal is applied -- the seam for a removal that hangs or is cancelled.</summary>
    public Action<CancellationToken>? BeforeRemove { get; init; }

    public Task<ExperienceIndexWriteResult> WriteAsync(
        AuthorizationContext authorization,
        ExperienceIndexWrite write,
        CancellationToken cancellationToken)
    {
        Assert.NotNull(authorization);
        Assert.NotNull(write);
        Writes.Add(write);

        if (WriteThrows is not null)
        {
            throw WriteThrows;
        }

        if (!authorization.Permits(write.Scope))
        {
            return Task.FromResult(new ExperienceIndexWriteResult(ExperienceIndexOutcome.Denied, 0, []));
        }

        BeforeWrite?.Invoke(write);

        if (!Records.TryGetValue(write.ExperienceId, out var row))
        {
            // The conditional INSERT ... SELECT has no source row, so nothing is written and nothing
            // is recreated.
            return Task.FromResult(new ExperienceIndexWriteResult(ExperienceIndexOutcome.Missing, 0, []));
        }

        if (row.Revision != write.Descriptor.SourceRevision)
        {
            return Task.FromResult(new ExperienceIndexWriteResult(ExperienceIndexOutcome.Stale, row.Revision, []));
        }

        Stored[write.ExperienceId] = (write.Descriptor, write.Vector);
        return Task.FromResult(new ExperienceIndexWriteResult(ExperienceIndexOutcome.Written, 0, []));
    }

    public Task<ExperienceIndexRemoveResult> RemoveAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken)
    {
        Assert.NotNull(authorization);
        Assert.NotNull(scope);
        Removals.Add((scope, experienceId));

        if (RemoveThrows is not null)
        {
            throw RemoveThrows;
        }

        if (!authorization.Permits(scope))
        {
            return Task.FromResult(new ExperienceIndexRemoveResult(ExperienceIndexRemoveOutcome.Denied, []));
        }

        BeforeRemove?.Invoke(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Removal is a DELETE: it does not care whether the canonical record still exists, only
        // whether a vector was there to remove.
        return Task.FromResult(new ExperienceIndexRemoveResult(
            Stored.Remove(experienceId) ? ExperienceIndexRemoveOutcome.Removed : ExperienceIndexRemoveOutcome.NotIndexed,
            []));
    }

    public Task<ExperienceIndexScanResult> ScanAsync(
        AuthorizationContext authorization,
        ExperienceIndexScan scan,
        CancellationToken cancellationToken)
    {
        Assert.NotNull(authorization);
        Assert.NotNull(scan);
        Scans.Add(scan);

        if (ScanThrows is not null)
        {
            throw ScanThrows;
        }

        if (ScanOutcome is { } scripted)
        {
            return Task.FromResult(new ExperienceIndexScanResult(scripted, [], []));
        }

        if (!authorization.Permits(scan.Scope))
        {
            return Task.FromResult(new ExperienceIndexScanResult(ExperienceStoreOutcome.Denied, [], []));
        }

        var ids = (scan.ExperienceIds is null ? Records.Keys : Records.Keys.Intersect(scan.ExperienceIds))
            .Where(id => scan.EligibleStatuses.Contains(Records[id].Status) && Records[id].ReuseConfidence >= scan.MinimumConfidence)
            .Where(id => scan.StartAfterId is not { } after || id.CompareTo(after) > 0);

        var targets = ids
            .Order()
            .Take(scan.Limit)
            .Select(id => new ExperienceIndexTarget(
                id,
                Records[id].Revision,
                Records[id].Summary,
                Stored.TryGetValue(id, out var stored) ? stored.Descriptor : null))
            .ToArray();

        return Task.FromResult(new ExperienceIndexScanResult(
            ExperienceStoreOutcome.Found,
            targets,
            [],
            targets.Length > 0 ? targets[^1].ExperienceId : null));
    }

    public Task<ExperienceVectorSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceVectorQuery query,
        CancellationToken cancellationToken)
    {
        Assert.NotNull(authorization);
        Assert.NotNull(query);
        lock (Queries)
        {
            Queries.Add(query);
        }

        if (SearchThrows is not null)
        {
            throw SearchThrows;
        }

        return Task.FromResult(OnSearch?.Invoke(query)
            ?? new ExperienceVectorSearchResult(ExperienceVectorSearchOutcome.Found, [], []));
    }
}

/// <summary>
/// A clock frozen at a known instant. Its timers never fire, so a test that does not mean to exercise
/// the retrieval timeout cannot accidentally hit one, and elapsed time is always exactly zero.
/// </summary>
internal sealed class FrozenClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override long GetTimestamp() => now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new FrozenTimer();

    private sealed class FrozenTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
