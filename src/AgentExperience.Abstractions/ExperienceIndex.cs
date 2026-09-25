using System.Security.Cryptography;
using System.Text;

namespace AgentExperience.Abstractions;

/// <summary>
/// The sanitized retrieval summary: the only text of an <see cref="ExperienceRecord"/> that is ever
/// handed to an embedding provider, and exactly the three fields the text index already analyzes --
/// <see cref="ExperienceRecord.TaskId"/>, <see cref="ExperienceRecord.TaskSummary"/>, and the
/// <see cref="Reflection.Lesson"/>.
/// </summary>
/// <remarks>
/// <para>
/// Attempts, tool calls, evidence, provenance, and environment metadata are deliberately excluded.
/// They are operational detail, they would flood a vector with identifiers and stack-trace-like
/// fragments, and -- unlike the three fields above -- they are not what a record is <em>about</em>.
/// Reading the same fields as the text index means the two retrieval channels answer over the same
/// claim about the record rather than over two different documents. They do not necessarily read the
/// same <em>length</em>: this is capped at <see cref="MaxLength"/>, while the text index analyzes far
/// more, so a very long summary is matched on more of its text by words than by meaning.
/// </para>
/// <para>
/// The text is normalized before it is hashed or embedded: every run of whitespace collapses to one
/// space and the result is trimmed, so a record whose summary differs only in line endings or
/// indentation hashes the same and is never re-embedded for nothing.
/// </para>
/// </remarks>
public static class ExperienceRetrievalSummary
{
    /// <summary>
    /// The longest summary that is ever embedded. Providers bound their input, and a very long
    /// summary would be truncated by the provider anyway -- truncating here instead keeps the hashed
    /// text and the embedded text identical, so the skip-on-unchanged-hash rule stays sound.
    /// </summary>
    public const int MaxLength = 8192;

    /// <summary>Builds the normalized retrieval summary for <paramref name="record"/>.</summary>
    /// <param name="record">The record to summarize. Never mutated.</param>
    /// <returns>The normalized summary, at most <see cref="MaxLength"/> characters. Never <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public static string For(ExperienceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return For(record.TaskId, record.TaskSummary, record.Reflection?.Lesson);
    }

    /// <summary>
    /// Builds the normalized retrieval summary from the three fields directly, for a caller that
    /// holds them without a materialized <see cref="ExperienceRecord"/> (a storage adapter reading
    /// them out of a row, for example).
    /// </summary>
    /// <param name="taskId">The record's task identifier.</param>
    /// <param name="taskSummary">The record's sanitized task summary, if any.</param>
    /// <param name="lesson">The reflection's lesson, if the record carries a reflection.</param>
    /// <returns>The normalized summary, at most <see cref="MaxLength"/> characters. Never <see langword="null"/>.</returns>
    public static string For(string? taskId, string? taskSummary, string? lesson)
    {
        var builder = new StringBuilder(capacity: 256);
        Append(builder, taskId);
        Append(builder, taskSummary);
        Append(builder, lesson);

        Truncate(builder);
        return builder.ToString();
    }

    /// <summary>
    /// Cuts the summary to <see cref="MaxLength"/> without ever splitting a character or leaving a
    /// dangling separator.
    /// </summary>
    /// <remarks>
    /// A raw cut at a UTF-16 index can land between a surrogate pair, and a lone surrogate is not a
    /// valid character: UTF-8 encoding silently replaces it with U+FFFD. The hash would then be taken
    /// over one string and the provider handed another, which is exactly the disagreement the content
    /// hash exists to rule out. Backing off one unit when the last kept unit is a high surrogate makes
    /// the cut land on a character boundary; dropping a trailing space afterwards keeps the result
    /// identical in shape to an untruncated summary.
    /// </remarks>
    private static void Truncate(StringBuilder builder)
    {
        if (builder.Length <= MaxLength)
        {
            return;
        }

        var length = MaxLength;
        if (char.IsHighSurrogate(builder[length - 1]))
        {
            length--;
        }

        while (length > 0 && builder[length - 1] == ' ')
        {
            length--;
        }

        builder.Length = length;
    }

    /// <summary>
    /// Appends one field, collapsing every run of whitespace <em>or control characters</em> inside it
    /// to a single space and separating it from what is already there by exactly one space. A
    /// separator is only ever written immediately before a kept character, so the result never ends in
    /// a space and a blank field contributes nothing at all.
    /// </summary>
    /// <remarks>
    /// Control characters are dropped rather than kept for a reason that is not cosmetic:
    /// <see cref="ExperienceEmbeddingDescriptor.ComputeContentHash"/> joins the model ID and the
    /// summary with <c>U+001F</c>, and that separator is only unambiguous while neither part can
    /// contain it. Stripping the whole C0/C1 range here makes that true of every summary, whatever a
    /// task description happened to carry.
    /// </remarks>
    private static void Append(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var pendingSpace = builder.Length > 0;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }
    }
}

/// <summary>
/// What a stored embedding <em>is</em>, kept entirely separate from lifecycle state: which model
/// produced it, how wide the vector is, a hash of the exact text that was embedded, and the record
/// revision the text was read at.
/// </summary>
/// <remarks>
/// <para>
/// None of these four fields ever influences eligibility, status, or reuse confidence. They exist so
/// an index write can be made conditional (<see cref="SourceRevision"/>), so a re-index can tell
/// "nothing changed" from "the text changed" without calling a provider
/// (<see cref="ContentHash"/>), and so a query vector is never compared against a vector it is not
/// comparable with (<see cref="ModelId"/> and <see cref="Dimension"/>).
/// </para>
/// </remarks>
/// <param name="ModelId">The provider's identifier for the model that produced the vector. Compared ordinally; a different value makes two vectors incomparable.</param>
/// <param name="Dimension">How many components the vector has. Must be strictly positive, and must equal the stored vector's length.</param>
/// <param name="ContentHash">A hash of the model ID and the normalized summary that was embedded. See <see cref="ComputeContentHash"/>.</param>
/// <param name="SourceRevision">The <see cref="ExperienceRecord.Revision"/> the summary was read at. An index write applies only while the record is still at exactly this revision.</param>
public sealed record ExperienceEmbeddingDescriptor(
    string ModelId,
    int Dimension,
    string ContentHash,
    long SourceRevision)
{
    /// <summary>The longest permitted <see cref="ModelId"/>.</summary>
    public const int MaxModelIdLength = 256;

    /// <summary>
    /// The largest permitted <see cref="Dimension"/>. pgvector's own ceiling for an indexable vector
    /// is well below this; bounding it here keeps an absurd dimension a typed validation error rather
    /// than a multi-megabyte parameter sent to a database.
    /// </summary>
    public const int MaxDimension = 16000;

    /// <summary>
    /// The content hash: lowercase hexadecimal SHA-256 over the model ID, a separator that cannot
    /// occur in either part, and the normalized summary text.
    /// </summary>
    /// <remarks>
    /// The model ID is inside the hash on purpose. Two models embedding identical text produce
    /// different, incomparable vectors, so "same text" alone must never be enough to skip a
    /// re-embedding; including the model makes a model change look exactly like a text change. The
    /// separator is <c>U+001F</c> (unit separator), which neither a model ID nor a normalized summary
    /// can contain, so no concatenation of one pair can collide with another.
    /// </remarks>
    /// <param name="modelId">The model that would produce the vector.</param>
    /// <param name="summary">The normalized summary, as <see cref="ExperienceRetrievalSummary"/> produces it.</param>
    /// <returns>64 lowercase hexadecimal characters.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static string ComputeContentHash(string modelId, string summary)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(summary);

        // Written as an escape, never as the literal byte: every stored content hash depends on this
        // one character surviving every editor, diff, and patch tool that ever touches this file, and
        // an unprintable byte in source does not.
        var bytes = Encoding.UTF8.GetBytes(string.Concat(modelId, "\u001F", summary));
#if NET9_0_OR_GREATER
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
#else
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#endif
    }
}

/// <summary>
/// Port for turning a record's sanitized retrieval summary into a vector. It is deliberately stated
/// in domain terms only -- a string in, floats out -- so neither
/// <c>AgentExperience.Abstractions</c> nor <c>AgentExperience.Core</c> ever takes a dependency on a
/// model-provider package. An adapter bridges this to whatever generator a host actually runs.
/// </summary>
/// <remarks>
/// <see cref="ModelId"/> and <see cref="Dimension"/> must be known <em>without</em> calling the
/// provider: the content hash covers the model ID, so an unchanged summary under the same model has
/// to be recognizable before any provider call is made.
/// </remarks>
public interface IExperienceEmbeddingGenerator
{
    /// <summary>
    /// The identifier of the model this generator produces vectors with, stored on every embedding it
    /// produces and compared ordinally at query time. Must be stable for the life of the generator.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// How many components <see cref="GenerateAsync"/> returns. Must be strictly positive and stable
    /// for the life of the generator; a vector of any other length is rejected by the caller.
    /// </summary>
    int Dimension { get; }

    /// <summary>Embeds one already-sanitized, already-normalized summary.</summary>
    /// <param name="text">The text to embed. Never a raw payload: only <see cref="ExperienceRetrievalSummary"/> output reaches here.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A vector of exactly <see cref="Dimension"/> finite components.</returns>
    Task<ReadOnlyMemory<float>> GenerateAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// Embeds several already-sanitized, already-normalized summaries in one provider call, shaped
    /// like <c>Microsoft.Extensions.AI</c>'s <c>IEmbeddingGenerator</c>: a list in, one vector per
    /// input out, in input order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All or nothing.</b> A provider batch either returns every vector or fails, so this throws for
    /// the whole batch rather than reporting per input. The caller -- Core's indexing pass -- retries a
    /// failed batch one input at a time, so a failure still lands on the record that caused it, and writes
    /// each record that did get a vector with its own conditional, revision-checked write.
    /// </para>
    /// <para>
    /// <b>The default implementation calls <see cref="GenerateAsync"/> once per input, in order.</b>
    /// An existing generator therefore keeps working, with the same per-record outcomes, but saves no
    /// round trips (and a failed batch is re-asked one text at a time, so a failure costs up to one extra
    /// call per text already embedded in that batch). A generator
    /// over a batch-capable provider should override it. The caller bounds the batch size itself
    /// (see <c>ReindexExperienceRequest.EmbeddingBatchSize</c> in <c>AgentExperience.Core</c>); an
    /// implementation whose provider accepts fewer inputs per request must split the batch itself.
    /// </para>
    /// </remarks>
    /// <param name="texts">The texts to embed. Never raw payloads: only <see cref="ExperienceRetrievalSummary"/> output reaches here. Non-empty.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Exactly one vector per input, in input order, each of exactly <see cref="Dimension"/> finite components. A result of any other count is rejected by the caller.</returns>
    async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var vectors = new ReadOnlyMemory<float>[texts.Count];
        for (var i = 0; i < vectors.Length; i++)
        {
            vectors[i] = await GenerateAsync(texts[i], cancellationToken).ConfigureAwait(false);
        }

        return vectors;
    }
}

/// <summary>
/// Port for the derived embedding index: it stores one vector per Experience Record, lists what a
/// scoped re-index would have to look at, and answers a scoped vector search. It is deliberately
/// separate from <see cref="IExperienceRecordStore"/>, because an embedding is derived data -- the
/// canonical record is written, committed, and text-searchable whether or not any of this ever runs.
/// </summary>
/// <remarks>
/// <para>
/// The same trust boundary applies as to <see cref="IExperienceRecordStore"/>: every call takes a
/// host-established <see cref="AuthorizationContext"/>, a request scope outside it is
/// <see cref="ExperienceStoreOutcome.Denied"/> before any storage access, and scope matching is
/// exact (ordinal, case-sensitive, <see langword="null"/> matches only <see langword="null"/>).
/// Expected conditions return typed results; infrastructure failures throw
/// <see cref="ExperienceStoreException"/>; caller cancellation surfaces as an unwrapped
/// <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// An implementation decides <em>nothing</em> about eligibility or lifecycle. It never creates,
/// updates, or deletes an Experience Record, and it never writes a vector for a record that does not
/// currently exist at the revision the write names.
/// </para>
/// </remarks>
public interface IExperienceEmbeddingIndex
{
    /// <summary>
    /// Stores one record's vector, but only while that record still exists, in exactly
    /// <see cref="ExperienceIndexWrite.Scope"/>, at exactly
    /// <see cref="ExperienceEmbeddingDescriptor.SourceRevision"/>. A record whose revision has moved
    /// on is <see cref="ExperienceIndexOutcome.Stale"/> and the stored vector is left untouched; a
    /// record that no longer exists is <see cref="ExperienceIndexOutcome.Missing"/> and no row is
    /// created, so an in-flight write can never resurrect a deleted record.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="write">The vector and its descriptor, for one record in one scope.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A result naming what happened; nothing is written unless it is <see cref="ExperienceIndexOutcome.Written"/>.</returns>
    Task<ExperienceIndexWriteResult> WriteAsync(
        AuthorizationContext authorization,
        ExperienceIndexWrite write,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists what an index pass would have to consider within exactly
    /// <see cref="ExperienceIndexScan.Scope"/>: each record's current revision, its normalized
    /// retrieval summary, and the descriptor of the vector already stored for it, if any. This is the
    /// read that lets a caller skip an unchanged record without ever calling a provider.
    /// </summary>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scan">Which records to list, and under which model to report stored descriptors.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see cref="ExperienceStoreOutcome.Found"/> (possibly with no targets), <see cref="ExperienceStoreOutcome.Invalid"/>, or <see cref="ExperienceStoreOutcome.Denied"/>.</returns>
    Task<ExperienceIndexScanResult> ScanAsync(
        AuthorizationContext authorization,
        ExperienceIndexScan scan,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds records within exactly <see cref="ExperienceVectorQuery.Scope"/> whose stored vector was
    /// produced by <see cref="ExperienceVectorQuery.ModelId"/> at
    /// <see cref="ExperienceVectorQuery.Vector"/>'s dimension, nearest first, applying the same
    /// status filter and confidence floor the text channel applies.
    /// </summary>
    /// <remarks>
    /// A vector from another model, or of another dimension, is never compared: it is excluded by the
    /// query itself. When the scope holds embeddings but none of them are comparable, the result says
    /// so (<see cref="ExperienceVectorSearchOutcome.ModelMismatch"/> or
    /// <see cref="ExperienceVectorSearchOutcome.DimensionMismatch"/>) rather than looking like an
    /// empty match, so the caller can fall back to text alone and say why.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="query">The scoped vector search. Never treated as authority.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A result naming what happened; candidates are present only on <see cref="ExperienceVectorSearchOutcome.Found"/>.</returns>
    Task<ExperienceVectorSearchResult> SearchAsync(
        AuthorizationContext authorization,
        ExperienceVectorQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes one record's stored vector within exactly <paramref name="scope"/>. A record that was
    /// never indexed, or whose vector is in another scope, is
    /// <see cref="ExperienceIndexRemoveOutcome.NotIndexed"/> rather than an error, so removal is
    /// idempotent and repeating it is free.
    /// </summary>
    /// <remarks>
    /// This exists because leaving eligibility has to take the vector with it: a record that a search
    /// may no longer return must not keep a row a search could match. It removes <em>derived</em> data
    /// only -- the canonical record, its status, and its lifecycle history are untouched, and a later
    /// index pass can re-embed the record if it becomes eligible again.
    /// </remarks>
    /// <param name="authorization">What the host has established the caller may do.</param>
    /// <param name="scope">The exact scope the vector must lie in. Never treated as authority.</param>
    /// <param name="experienceId">The record whose vector to remove. Must not be <see cref="Guid.Empty"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A result naming what happened.</returns>
    Task<ExperienceIndexRemoveResult> RemoveAsync(
        AuthorizationContext authorization,
        Scope scope,
        Guid experienceId,
        CancellationToken cancellationToken);
}

/// <summary>What removing one record's vector ended as.</summary>
public enum ExperienceIndexRemoveOutcome
{
    /// <summary>The stored vector was deleted. A search can no longer return this record through the vector channel.</summary>
    Removed,

    /// <summary>
    /// There was no vector to remove within the requested scope -- the record was never indexed, its
    /// vector was already removed, or it lies in another scope. Nothing was written, and the outcome is
    /// deliberately the same in all three cases.
    /// </summary>
    NotIndexed,

    /// <summary>The request scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The request was malformed. See the result's validation errors. No storage was accessed.</summary>
    Invalid,
}

/// <summary>
/// The result of <see cref="IExperienceEmbeddingIndex.RemoveAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceIndexRemoveOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceIndexRemoveResult(
    ExperienceIndexRemoveOutcome Outcome,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// One conditional index write: a record's vector, what it is, and the scope and revision it is only
/// valid for.
/// </summary>
/// <param name="Scope">The exact scope the record must lie in. Never treated as authority.</param>
/// <param name="ExperienceId">The record the vector describes. Must not be <see cref="Guid.Empty"/>.</param>
/// <param name="Descriptor">The model, dimension, content hash, and source revision the write is conditional on.</param>
/// <param name="Vector">The vector itself. Must hold exactly <see cref="ExperienceEmbeddingDescriptor.Dimension"/> finite components.</param>
public sealed record ExperienceIndexWrite(
    Scope Scope,
    Guid ExperienceId,
    ExperienceEmbeddingDescriptor Descriptor,
    ReadOnlyMemory<float> Vector);

/// <summary>What a conditional index write ended as.</summary>
public enum ExperienceIndexOutcome
{
    /// <summary>The vector and its descriptor were stored, replacing any previous vector for that record.</summary>
    Written,

    /// <summary>
    /// The record exists in the requested scope but is no longer at the revision the write named, so
    /// the write was rejected and the stored vector is unchanged. The result carries the record's
    /// current revision.
    /// </summary>
    Stale,

    /// <summary>
    /// No record with that ID exists within the requested scope -- it was deleted, or it is in another
    /// scope. Nothing was written, and no row was created.
    /// </summary>
    Missing,

    /// <summary>The request scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The request was malformed. See the result's validation errors. No storage was accessed.</summary>
    Invalid,
}

/// <summary>
/// The result of <see cref="IExperienceEmbeddingIndex.WriteAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="CurrentRevision">The record's revision as the index observed it, when <see cref="Outcome"/> is <see cref="ExperienceIndexOutcome.Stale"/>; otherwise 0.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceIndexOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceIndexWriteResult(
    ExperienceIndexOutcome Outcome,
    long CurrentRevision,
    IReadOnlyList<StoreValidationError> Errors);

/// <summary>
/// Which records an index pass should consider, within exactly one scope.
/// </summary>
/// <param name="Scope">The exact scope to scan. Never treated as authority.</param>
/// <param name="ModelId">The model whose stored descriptor to report for each record. A vector stored under another model is reported as it stands, so the caller can see the model changed.</param>
/// <param name="EligibleStatuses">
/// The statuses a record must be in to be listed. Non-empty, and only defined values. This is
/// deliberately the <em>same</em> filter a vector search applies: embedding a record whose vector could
/// never be returned would send its summary and lesson to an external provider for nothing.
/// </param>
/// <param name="MinimumConfidence">The smallest <see cref="ExperienceRecord.ReuseConfidence"/> a record may have and still be listed, in [0, 1]. Again, the search's own floor.</param>
/// <param name="ExperienceIds">Optional. Exactly which records to list; <see langword="null"/> lists every record in the scope, bounded by <paramref name="Limit"/>. When supplied it must be non-empty and hold no empty GUID.</param>
/// <param name="Limit">Maximum number of targets to return, from <see cref="MinLimit"/> to <see cref="MaxLimit"/>. Defaults to <see cref="DefaultLimit"/>.</param>
/// <param name="StartAfterId">
/// Optional keyset cursor: list only records whose <see cref="ExperienceRecord.ExperienceId"/> sorts
/// strictly after this one. Targets always come back in ascending ID order, so passing the previous
/// page's <see cref="ExperienceIndexScanResult.LastExaminedId"/> walks a scope larger than
/// <paramref name="Limit"/> to the end. <see langword="null"/> starts from the beginning.
/// </param>
public sealed record ExperienceIndexScan(
    Scope Scope,
    string ModelId,
    IReadOnlyList<ExperienceStatus> EligibleStatuses,
    double MinimumConfidence,
    IReadOnlyList<Guid>? ExperienceIds = null,
    int Limit = ExperienceIndexScan.DefaultLimit,
    Guid? StartAfterId = null)
{
    /// <summary>The smallest permitted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>The largest permitted <see cref="Limit"/>. A re-index is a batch job: it pages, rather than loading a whole scope at once.</summary>
    public const int MaxLimit = 500;

    /// <summary>The <see cref="Limit"/> used when none is specified.</summary>
    public const int DefaultLimit = 100;
}

/// <summary>
/// One record an index pass may have to (re-)embed, read at a single instant so its revision, its
/// summary, and its stored descriptor cannot disagree with each other.
/// </summary>
/// <param name="ExperienceId">The record.</param>
/// <param name="SourceRevision">The record's revision at the moment it was read. An index write for this target is conditional on it.</param>
/// <param name="Summary">The record's normalized retrieval summary, as <see cref="ExperienceRetrievalSummary"/> defines it.</param>
/// <param name="Stored">The descriptor of the vector already stored for this record, or <see langword="null"/> when it has never been indexed.</param>
public sealed record ExperienceIndexTarget(
    Guid ExperienceId,
    long SourceRevision,
    string Summary,
    ExperienceEmbeddingDescriptor? Stored);

/// <summary>
/// The result of <see cref="IExperienceEmbeddingIndex.ScanAsync"/>.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Targets">The records to consider, in ascending <see cref="ExperienceRecord.ExperienceId"/> order, when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceStoreOutcome.Invalid"/>; otherwise empty.</param>
/// <param name="LastExaminedId">
/// The last listed record's ID, to pass as the next scan's <see cref="ExperienceIndexScan.StartAfterId"/>.
/// <see langword="null"/> when nothing was listed, which is how a caller knows the scope is exhausted.
/// </param>
public sealed record ExperienceIndexScanResult(
    ExperienceStoreOutcome Outcome,
    IReadOnlyList<ExperienceIndexTarget> Targets,
    IReadOnlyList<StoreValidationError> Errors,
    Guid? LastExaminedId = null);

/// <summary>
/// A scoped nearest-neighbour search for reusable Experience Records. It mirrors
/// <see cref="ExperienceCandidateQuery"/> field for field, apart from matching on a vector instead of
/// on text, so both retrieval channels are filtered identically.
/// </summary>
/// <param name="Scope">The exact scope to search within. Never treated as authority.</param>
/// <param name="ModelId">The model the query vector was produced by. Only vectors stored under exactly this model are ever compared against it.</param>
/// <param name="Vector">The query vector. Must be non-empty, hold only finite components, and be no wider than <see cref="ExperienceEmbeddingDescriptor.MaxDimension"/>.</param>
/// <param name="EligibleStatuses">The statuses a record must be in to be returned. Must be non-empty and contain only defined values.</param>
/// <param name="MinimumConfidence">The smallest <see cref="ExperienceRecord.ReuseConfidence"/> a record may have and still be returned, in [0, 1].</param>
/// <param name="Limit">Maximum number of candidates to return, from <see cref="ExperienceCandidateQuery.MinLimit"/> to <see cref="ExperienceCandidateQuery.MaxLimit"/>. Defaults to <see cref="ExperienceCandidateQuery.DefaultLimit"/>.</param>
/// <param name="CorrelationId">
/// The host's identifier for the work causing this search, recorded on the access rows written for
/// any grant-permitted records it returns. Nothing else reads it.
/// </param>
public sealed record ExperienceVectorQuery(
    Scope Scope,
    string ModelId,
    ReadOnlyMemory<float> Vector,
    IReadOnlyList<ExperienceStatus> EligibleStatuses,
    double MinimumConfidence,
    int Limit = ExperienceCandidateQuery.DefaultLimit,
    string? CorrelationId = null);

/// <summary>What a scoped vector search ended as.</summary>
public enum ExperienceVectorSearchOutcome
{
    /// <summary>The search ran. It may still have matched nothing, which is an answer, not a failure.</summary>
    Found,

    /// <summary>
    /// The scope holds embeddings, but every one of them was produced by a different model, so none
    /// is comparable with the query vector. No comparison was attempted and no candidate is returned.
    /// </summary>
    ModelMismatch,

    /// <summary>
    /// The scope holds embeddings from this model, but at a different dimension, so none is comparable
    /// with the query vector. No comparison was attempted and no candidate is returned.
    /// </summary>
    DimensionMismatch,

    /// <summary>The request scope lies outside the host-established authorization. No storage was accessed.</summary>
    Denied,

    /// <summary>The request was malformed. See the result's validation errors. No storage was accessed.</summary>
    Invalid,
}

/// <summary>
/// The result of <see cref="IExperienceEmbeddingIndex.SearchAsync"/>. Candidates carry the same
/// normalized [0, 1] relevance <see cref="ExperienceCandidate"/> always does, so the two retrieval
/// channels can be merged on one comparable measure.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Candidates">The matching candidates, nearest first, when <see cref="Outcome"/> is <see cref="ExperienceVectorSearchOutcome.Found"/>; otherwise empty.</param>
/// <param name="Errors">Every validation error when <see cref="Outcome"/> is <see cref="ExperienceVectorSearchOutcome.Invalid"/>; otherwise empty.</param>
public sealed record ExperienceVectorSearchResult(
    ExperienceVectorSearchOutcome Outcome,
    IReadOnlyList<ExperienceCandidate> Candidates,
    IReadOnlyList<StoreValidationError> Errors);
