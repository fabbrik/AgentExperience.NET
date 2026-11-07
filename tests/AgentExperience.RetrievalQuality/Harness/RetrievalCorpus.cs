using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentExperience.RetrievalQuality.Harness;

/// <summary>
/// One experience record of the corpus, as <c>Corpus/records.json</c> states it.
/// </summary>
/// <param name="Id">The corpus's own name for the record, which the queries and the report use.</param>
/// <param name="Group">Its task family, <see cref="RetrievalCorpus.Distractor"/> or <see cref="RetrievalCorpus.Ineligible"/>.</param>
/// <param name="TaskId">The record's task ID, which the candidate sources index.</param>
/// <param name="Summary">The record's task summary, which the candidate sources index.</param>
/// <param name="Lesson">The record's reflection lesson, which the candidate sources index.</param>
/// <param name="Status">The record's lifecycle status.</param>
/// <param name="Confidence">The record's reuse confidence.</param>
/// <param name="Scope"><see cref="RetrievalCorpus.QueryScope"/> for the scope every query searches, <see cref="RetrievalCorpus.OtherScope"/> for another project of the same tenant.</param>
internal sealed record CorpusRecord(
    string Id,
    string Group,
    string TaskId,
    string Summary,
    string Lesson,
    ExperienceStatus Status,
    double Confidence,
    string Scope)
{
    /// <summary>
    /// Whether a query may return this record at all: in the searched scope, in a reusable status and at or above the
    /// harness's confidence floor. Decided here from the corpus alone, not from what a candidate source returned.
    /// </summary>
    public bool IsEligible =>
        Scope == RetrievalCorpus.QueryScope
        && ExperienceStatuses.IsEligibleForReuse(Status)
        && Confidence >= RetrievalQualityRun.MinimumConfidence;

    /// <summary>
    /// The record's experience ID: a hash of <see cref="Id"/>, so it is the same on every run. Both candidate sources
    /// break relevance ties by ID, so a random ID would make the order of tied records, and the report, vary.
    /// </summary>
    public Guid ExperienceId { get; } = new(SHA256.HashData(Encoding.UTF8.GetBytes("retrieval-quality/" + Id)).AsSpan(0, 16));
}

/// <summary>
/// One labelled query of the corpus, as <c>Corpus/queries.json</c> states it.
/// </summary>
/// <param name="Id">The query's name in the report.</param>
/// <param name="Category">One of <see cref="RetrievalCorpus.Categories"/>.</param>
/// <param name="Text">The task text, passed to the candidate source unchanged.</param>
/// <param name="Expected">The records a good retriever returns for it. Empty when no record answers it: then any hit is a false positive.</param>
internal sealed record CorpusQuery(string Id, string Category, string Text, IReadOnlyList<string> Expected);

/// <summary>
/// Story 28.2: one failed tool call a corpus record holds, as <c>Corpus/signatures.json</c> states it. The record's run
/// was verified; when <paramref name="Resolved"/> a second attempt's call to the same tool succeeded after it.
/// </summary>
/// <param name="Record">The corpus record that holds the failure.</param>
/// <param name="Tool">The failed call's tool name.</param>
/// <param name="Error">The failed call's error text, as a sanitized capture would hold it.</param>
/// <param name="Resolved">Whether a later call succeeded.</param>
internal sealed record CorpusFailure(string Record, string Tool, string Error, bool Resolved);

/// <summary>
/// Story 28.2: one labelled signature query: a failure seen in a sibling run, with its own paths, numbers and IDs, and
/// the records that hold the same failure.
/// </summary>
/// <param name="Id">The query's name in the report.</param>
/// <param name="Tool">The failed call's tool name.</param>
/// <param name="Error">The failed call's error text.</param>
/// <param name="Expected">The records holding the same failure. Empty when no record does: then any hit is a false positive.</param>
internal sealed record CorpusSignatureQuery(string Id, string Tool, string Error, IReadOnlyList<string> Expected)
{
    /// <summary>The signature the query looks up.</summary>
    public ExperienceFailureSignature Signature => ExperienceFailureSignature.From(Tool, Error)
        ?? throw new InvalidDataException($"signature query {Id} has no signature");

    /// <summary>The query as a text query, for the shared metrics: its tool and error's first line, and its labels.</summary>
    public CorpusQuery AsQuery() => new(Id, "signature", Tool + ": " + Error.Split('\n')[0], Expected);
}

/// <summary>
/// The checked-in corpus: experience records in five task families, distractors that share some of their words, and
/// ineligible records no search may return, with queries labelled by category and by the records they should find.
/// </summary>
/// <remarks>
/// The corpus was written before the harness first ran against it, and is not tuned to its results: a query that
/// finds nothing today is the measurement. Loading checks its shape only -- unique IDs, known groups and categories,
/// expected records that exist and are eligible -- and fails loudly on anything else.
/// </remarks>
internal sealed class RetrievalCorpus
{
    /// <summary>The scope every query searches.</summary>
    public const string QueryScope = "query";

    /// <summary>Another project of the same tenant: the host may read it, but no query asks for it.</summary>
    public const string OtherScope = "other";

    /// <summary>The group of a record that shares words with a family but answers none of its tasks.</summary>
    public const string Distractor = "distractor";

    /// <summary>The group of a record no search may return: out of scope, not reusable, or below the confidence floor.</summary>
    public const string Ineligible = "ineligible";

    /// <summary>The query categories, in the order the report prints them.</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["exact", "paraphrase", "realistic", "morphology", "distractor-overlap", "stopword-heavy"];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private RetrievalCorpus(
        IReadOnlyList<CorpusRecord> records,
        IReadOnlyList<CorpusQuery> queries,
        IReadOnlyList<CorpusFailure> failures,
        IReadOnlyList<CorpusSignatureQuery> signatureQueries)
    {
        Records = records;
        Queries = queries;
        Failures = failures;
        SignatureQueries = signatureQueries;
    }

    /// <summary>Story 28.2: the failed calls corpus records hold, in file order.</summary>
    public IReadOnlyList<CorpusFailure> Failures { get; }

    /// <summary>Story 28.2: the signature queries, in file order.</summary>
    public IReadOnlyList<CorpusSignatureQuery> SignatureQueries { get; }

    /// <summary>The records, in file order.</summary>
    public IReadOnlyList<CorpusRecord> Records { get; }

    /// <summary>The queries, in file order.</summary>
    public IReadOnlyList<CorpusQuery> Queries { get; }

    /// <summary>The task families, in order of first appearance.</summary>
    public IReadOnlyList<string> Families =>
        [.. Records.Select(r => r.Group).Where(g => g is not Distractor and not Ineligible).Distinct(StringComparer.Ordinal)];

    /// <summary>The record named <paramref name="id"/>.</summary>
    public CorpusRecord Record(string id) => Records.Single(r => r.Id == id);

    /// <summary>The corpus embedded in this assembly, checked.</summary>
    public static RetrievalCorpus Load()
    {
        var records = Read<RecordsFile>("AgentExperience.RetrievalQuality.Corpus.records.json").Records
            ?? throw new InvalidDataException("records.json has no \"records\" array.");
        var queries = Read<QueriesFile>("AgentExperience.RetrievalQuality.Corpus.queries.json").Queries
            ?? throw new InvalidDataException("queries.json has no \"queries\" array.");

        var signatures = Read<SignaturesFile>("AgentExperience.RetrievalQuality.Corpus.signatures.json");
        var failures = signatures.Failures ?? throw new InvalidDataException("signatures.json has no \"failures\" array.");
        var signatureQueries = signatures.Queries ?? throw new InvalidDataException("signatures.json has no \"queries\" array.");

        var corpus = new RetrievalCorpus(records, queries, failures, signatureQueries);
        corpus.Check();
        return corpus;
    }

    private void Check()
    {
        var problems = new List<string>();

        problems.AddRange(Records.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => $"record {g.Key} appears {g.Count()} times"));
        foreach (var record in Records)
        {
            if (new[] { record.Id, record.Group, record.TaskId, record.Summary, record.Lesson, record.Scope }.Any(string.IsNullOrWhiteSpace))
            {
                problems.Add($"record {record.Id} has a blank field");
                continue;
            }

            if (record.Scope is not QueryScope and not OtherScope)
            {
                problems.Add($"record {record.Id} has scope '{record.Scope}'");
            }

            if (record.Group == Ineligible && record.IsEligible)
            {
                problems.Add($"record {record.Id} is in the ineligible group but every search may return it");
            }

            if (record.Group != Ineligible && !record.IsEligible)
            {
                problems.Add($"record {record.Id} is ineligible but in group '{record.Group}'");
            }
        }

        problems.AddRange(Queries.GroupBy(q => q.Id).Where(g => g.Count() > 1).Select(g => $"query {g.Key} appears {g.Count()} times"));
        var eligible = Records.Where(r => r.IsEligible).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var query in Queries)
        {
            if (string.IsNullOrWhiteSpace(query.Id) || string.IsNullOrWhiteSpace(query.Text) || query.Expected is null)
            {
                problems.Add($"query {query.Id} has a blank field");
                continue;
            }

            if (!Categories.Contains(query.Category))
            {
                problems.Add($"query {query.Id} has category '{query.Category}'");
            }

            if (query.Text.Length > ExperienceCandidateQuery.MaxTaskTextLength)
            {
                problems.Add($"query {query.Id} is longer than {ExperienceCandidateQuery.MaxTaskTextLength} characters");
            }

            problems.AddRange(query.Expected.Where(id => !eligible.Contains(id)).Select(id => $"query {query.Id} expects {id}, which is not an eligible record"));
            problems.AddRange(query.Expected.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => $"query {query.Id} expects {g.Key} twice"));
        }

        var ids = Records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(Failures.Where(f => !ids.Contains(f.Record)).Select(f => $"failure of {f.Record} names no record"));
        problems.AddRange(Failures.GroupBy(f => f.Record).Where(g => g.Count() > 1).Select(g => $"record {g.Key} holds {g.Count()} failures"));
        problems.AddRange(Failures.Where(f => ExperienceFailureSignature.From(f.Tool, f.Error) is null).Select(f => $"failure of {f.Record} has no signature"));
        problems.AddRange(SignatureQueries.GroupBy(q => q.Id).Where(g => g.Count() > 1).Select(g => $"signature query {g.Key} appears {g.Count()} times"));
        foreach (var query in SignatureQueries)
        {
            if (ExperienceFailureSignature.From(query.Tool, query.Error) is null || query.Expected is null)
            {
                problems.Add($"signature query {query.Id} has no signature or no labels");
                continue;
            }

            problems.AddRange(query.Expected
                .Where(id => !eligible.Contains(id) || !Failures.Any(f => f.Record == id && f.Tool == query.Tool))
                .Select(id => $"signature query {query.Id} expects {id}, which is not an eligible record holding a {query.Tool} failure"));
        }

        if (problems.Count > 0)
        {
            throw new InvalidDataException("The retrieval-quality corpus is malformed:\n" + string.Join("\n", problems));
        }
    }

    private static T Read<T>(string name)
    {
        using var stream = typeof(RetrievalCorpus).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"'{name}' is not embedded in the harness assembly.");
        return JsonSerializer.Deserialize<T>(stream, Options)
            ?? throw new InvalidDataException($"'{name}' is empty.");
    }

    private sealed record RecordsFile(IReadOnlyList<CorpusRecord>? Records);

    private sealed record QueriesFile(IReadOnlyList<CorpusQuery>? Queries);

    private sealed record SignaturesFile(IReadOnlyList<CorpusFailure>? Failures, IReadOnlyList<CorpusSignatureQuery>? Queries);
}
