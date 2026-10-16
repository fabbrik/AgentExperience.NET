using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.Storage.Postgres.Vectors;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.6's SQL: the statements of <c>sql/write-experience-embedding.sql</c> and
/// <c>sql/search-experience-vector.sql</c>, and the .NET statements they must equal.
/// </summary>
/// <remarks>
/// A contract file holds several statements, each starting at a <c>-- statement: name</c> line and running to the next one
/// (or the end), its other <c>--</c> lines skipped. A statement may span lines; its lines joined by one space are the .NET
/// statement exactly, so the file breaks lines only where the .NET text has a space.
/// </remarks>
internal static class VectorStatements
{
    public const string WriteFile = "sql/write-experience-embedding.sql";

    public const string SearchFile = "sql/search-experience-vector.sql";

    public const string PrivilegesFile = "sql/application-role-privileges-1.6.sql";

    /// <summary>The placeholder the search statements carry for the query vector's length.</summary>
    public const string DimensionPlaceholder = "{dim}";

    private const string Marker = "-- statement: ";

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>A contract file's statements by name, in file order, each as written (its lines joined by <c>\n</c>).</summary>
    public static IReadOnlyDictionary<string, string> Read(string file)
    {
        var statements = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in ContractFiles.Read(file)!.Split('\n').Where(line => line.Length > 0))
        {
            if (line.StartsWith(Marker, StringComparison.Ordinal))
            {
                current = [];
                statements.Add(line[Marker.Length..], current);
                continue;
            }

            if (line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(current is not null, $"{file}: a statement line before any '{Marker}' line: {line}");
            current.Add(line);
        }

        return statements.ToDictionary(entry => entry.Key, entry => string.Join('\n', entry.Value), StringComparer.Ordinal);
    }

    /// <summary>A search statement as a port runs it: every <c>{dim}</c> replaced with the query vector's length.</summary>
    public static string WithDimension(string statement, int dimension) =>
        statement.Replace(DimensionPlaceholder, dimension.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The .NET index's conditional write.</summary>
    public static string DotnetWrite => Constant("WriteSql");

    /// <summary>The .NET index's revision probe, run when the write wrote nothing.</summary>
    public static string DotnetProbeRevision => Constant("ProbeRevisionSql");

    /// <summary>
    /// The .NET index's nearest-neighbour statement for <paramref name="dimension"/>, exact scope only (the one it runs
    /// without grants, as the text search runs in contract 1.x).
    /// </summary>
    public static string DotnetSearch(int dimension, bool excludeModelAuthored) =>
        (string)typeof(PostgresExperienceEmbeddingIndex).GetMethod("SearchSql", Private)!.Invoke(null, [dimension, false, excludeModelAuthored])!;

    /// <summary>The .NET index's exact-scope compatibility probe.</summary>
    public static string DotnetCompatibility(bool excludeModelAuthored) =>
        (string)typeof(PostgresExperienceEmbeddingIndex)
            .GetField(excludeModelAuthored ? "ExcludingCompatibilityProbeExactSql" : "CompatibilityProbeExactSql", Private)!
            .GetValue(null)!;

    private static string Constant(string name) =>
        (string)typeof(PostgresExperienceEmbeddingIndex).GetField(name, Private)!.GetRawConstantValue()!;
}

/// <summary>One case of the <c>embedding-summary</c> family: the three fields, the model, and what they give.</summary>
internal sealed record EmbeddingSummaryCase(
    string Description,
    string? TaskId,
    string? TaskSummary,
    string? Lesson,
    string ModelId,
    string Summary,
    string ContentHash);

/// <summary>
/// Contract 1.6's <c>embedding-summary</c> family (no database): the text a record contributes to an embedding, and its
/// content hash. Inputs and answers in one file, as the encoding family has them.
/// </summary>
internal static class EmbeddingSummaryContract
{
    public const string Family = "embedding-summary";

    public const string FileName = "cases.json";

    private const string Model = "contract-embedding";

    /// <summary>Every case, with its summary and hash as the library computes them now.</summary>
    public static IReadOnlyList<EmbeddingSummaryCase> Run() =>
    [
        .. Inputs().Select(input => Rerun(new EmbeddingSummaryCase(input.Description, input.TaskId, input.TaskSummary, input.Lesson, input.ModelId, string.Empty, string.Empty))),
    ];

    /// <summary>A case's summary and hash computed again from its inputs.</summary>
    public static EmbeddingSummaryCase Rerun(EmbeddingSummaryCase input)
    {
        var summary = ExperienceRetrievalSummary.For(input.TaskId, input.TaskSummary, input.Lesson);
        return input with { Summary = summary, ContentHash = ExperienceEmbeddingDescriptor.ComputeContentHash(input.ModelId, summary) };
    }

    private static IEnumerable<(string Description, string? TaskId, string? TaskSummary, string? Lesson, string ModelId)> Inputs()
    {
        yield return ("The task ID, the task summary and the lesson, joined by single spaces.",
            "refund-stuck-pending", "Refund stuck in pending", "Release the lock before retrying.", Model);
        yield return ("Every run of whitespace or control characters inside a field (tab, CR, LF, NBSP, U+2028, NUL, U+0085, " +
            "U+001F, DEL, U+009F) is one space; the result is trimmed.",
            "deploy\tservice", "  Line one\r\n\n  line two\u00A0end\u2028x  ", "a\u0000b\u0085c\u001Fd\u007Fe\u009Ff\n", Model);
        yield return ("A missing or blank field contributes nothing, and no separator.",
            "rotate-keys", null, " \t\r\n ", Model);
        yield return ("A missing summary and lesson and an empty task ID: the summary is empty, and is still hashed.",
            string.Empty, null, null, Model);
        yield return ("Format characters (U+200B, U+FEFF) are not whitespace and are kept; a character outside the BMP is kept " +
            "whole.", "zero\u200Bwidth", "bom\uFEFFinside", "emoji \U0001F600 kept", Model);
        yield return ("The model ID is inside the hash, as UTF-8: the same summary under another model hashes differently.",
            "refund-stuck-pending", "Refund stuck in pending", "Release the lock before retrying.", "mod\u00E8le-\u00FC/2");
        yield return ("Longer than 8,192 UTF-16 code units: cut at 8,192.",
            "t", new string('a', 9000), "never reached", Model);
        yield return ("Cut where the last kept unit is a high surrogate: that unit goes too, so no character is split (8,191 " +
            "units).", "t", new string('a', 8189) + "\U0001F600" + "tail", null, Model);
        yield return ("Cut right after a space: the trailing space is dropped (8,191 units).",
            "t", new string('a', 8189) + " " + new string('b', 20), null, Model);
        yield return ("Cut inside a surrogate pair that follows a space: the high surrogate, then the space, are dropped (8,190 " +
            "units).", "t", new string('a', 8188) + " \U0001F600", null, Model);
    }
}

/// <summary>One candidate of a <c>hybrid-merge</c> case: the record and its normalized relevance from one channel.</summary>
/// A NaN relevance is written as the string <c>"NaN"</c>.
internal sealed record MergeCandidate(
    Guid ExperienceId,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals)] double Relevance);

/// <summary>One entry of a merged list: the record, the relevance kept, and the channel whose snapshot was kept.</summary>
/// A NaN relevance is written as the string <c>"NaN"</c>.
internal sealed record MergedCandidate(
    Guid ExperienceId,
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals)] double Relevance,
    string From);

/// <summary>One case of the <c>hybrid-merge</c> family.</summary>
internal sealed record HybridMergeCase(
    string Description,
    double? MinimumVectorRelevance,
    IReadOnlyList<MergeCandidate> Text,
    IReadOnlyList<MergeCandidate> Vector,
    IReadOnlyList<MergedCandidate> Merged);

/// <summary>
/// Contract 1.6's <c>hybrid-merge</c> family (no database): how the two channels' candidates become one list before the
/// existing ranking, from the retrieval service's own floor and merge. Inputs and answers in one file.
/// </summary>
internal static class HybridMergeContract
{
    public const string Family = "hybrid-merge";

    public const string FileName = "cases.json";

    private const int F = 0x6a;

    /// <summary>Every case, with its merged list as the library computes it now.</summary>
    public static IReadOnlyList<HybridMergeCase> Run() => [.. Inputs().Select(Rerun)];

    /// <summary>A case's merged list computed again from its inputs, through the retrieval service's floor and merge.</summary>
    public static HybridMergeCase Rerun(HybridMergeCase input)
    {
        var records = input.Text.Concat(input.Vector).Select(candidate => candidate.ExperienceId).Distinct()
            .ToDictionary(id => id, id => SearchContract.Seed(F, Convert.ToInt32(id.ToString("N")[20..], 16), "task", "Summary", "Lesson."));

        // Each channel reads its own snapshot of the record: distinct instances, so the one kept tells which channel it came from.
        List<ExperienceCandidate> text = [.. input.Text.Select(candidate => new ExperienceCandidate(records[candidate.ExperienceId] with { }, candidate.Relevance))];
        List<ExperienceCandidate> vector = [.. input.Vector.Select(candidate => new ExperienceCandidate(records[candidate.ExperienceId] with { }, candidate.Relevance))];
        var textRecords = text.Select(candidate => candidate.Record).ToHashSet(ReferenceEqualityComparer.Instance);

        var merged = ExperienceRetrievalService.MergeChannels(text, AboveFloor(input.MinimumVectorRelevance, vector));
        return input with
        {
            Merged = [.. merged.Select(candidate => new MergedCandidate(
                candidate.Record.ExperienceId,
                candidate.Relevance,
                textRecords.Contains(candidate.Record) ? "text" : "vector"))],
        };
    }

    /// <summary>The retrieval service's vector floor, under a policy with <paramref name="floor"/>.</summary>
    private static IReadOnlyList<ExperienceCandidate> AboveFloor(double? floor, IReadOnlyList<ExperienceCandidate> candidates)
    {
        var service = new ExperienceRetrievalService(
            new NoCandidates(),
            RetrievalPolicy.Default with { MinimumVectorRelevance = floor },
            RankingWeights.Default,
            new FixedTimeProvider(Fixed.T0));
        return (IReadOnlyList<ExperienceCandidate>)typeof(ExperienceRetrievalService)
            .GetMethod("AboveFloor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [candidates])!;
    }

    private static IEnumerable<HybridMergeCase> Inputs()
    {
        Guid Id(int n) => Fixed.Id(F, n);
        MergeCandidate C(int n, double relevance) => new(Id(n), relevance);
        HybridMergeCase Case(string description, double? floor, MergeCandidate[] text, MergeCandidate[] vector) =>
            new(description, floor, text, vector, []);

        yield return Case("Disjoint channels: the text candidates in their order, then the vector candidates in theirs.",
            null, [C(2, 0.6), C(1, 0.4)], [C(4, 0.9), C(3, 0.7)]);
        yield return Case("A record in both channels is one entry, at its text position, with the higher relevance (here the " +
            "vector's) and the text channel's snapshot.", null, [C(1, 0.5), C(2, 0.3)], [C(2, 0.8), C(3, 0.6)]);
        yield return Case("The text relevance is the higher: it is kept, with the text channel's snapshot.",
            null, [C(1, 0.9)], [C(1, 0.5)]);
        yield return Case("Equal relevances: the text channel's entry is kept unchanged.",
            null, [C(1, 0.5)], [C(1, 0.5)]);
        yield return Case("A floor drops vector candidates below it before the merge (a candidate exactly at it is kept); text " +
            "candidates are never floored, so a record whose vector relevance is below the floor keeps its text relevance.",
            0.75, [C(1, 0.2), C(2, 0.6)], [C(2, 0.9), C(3, 0.8), C(4, 0.7), C(1, 0.74), C(5, 0.75)]);
        yield return Case("No text candidates: the vector candidates, in their order.",
            null, [], [C(2, 0.9), C(1, 0.4)]);
        yield return Case("No vector candidates (the vector channel fell back, or matched nothing): the text candidates.",
            null, [C(1, 0.4), C(2, 0.3)], []);
        yield return Case("Relevances are compared clamped to [0, 1], NaN as 0, but the raw value is kept: a NaN text relevance " +
            "takes the vector's 0.5; a text 1.2 beats a vector 0.9 and stays 1.2; a vector 1.5 beats a text 0.9 and is kept as " +
            "1.5; with no floor a NaN vector candidate is kept.",
            null, [C(1, double.NaN), C(2, 1.2), C(3, 0.4), C(5, 0.9)], [C(1, 0.5), C(2, 0.9), C(4, double.NaN), C(5, 1.5)]);
        yield return Case("A floor of 0 keeps every vector candidate with a number for a relevance, 0 included, and drops a NaN one.",
            0, [], [C(1, double.NaN), C(2, 0), C(3, 0.3)]);
    }

    /// <summary>A candidate source the floor's service never calls.</summary>
    private sealed class NoCandidates : IExperienceCandidateSource
    {
        public Task<ExperienceCandidateSearchResult> SearchAsync(AuthorizationContext authorization, ExperienceCandidateQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not called.");
    }
}

/// <summary>One step of an <c>embedding-write</c> or <c>vector-search</c> case.</summary>
internal abstract record VectorStep;

/// <summary>Stores a record's embedding through the conditional write (the port's write call).</summary>
internal sealed record WriteEmbeddingStep(
    Guid ExperienceId,
    Scope Scope,
    string ModelId,
    int Dimension,
    string ContentHash,
    long SourceRevision,
    float[] Vector,
    DateTimeOffset Now) : VectorStep;

/// <summary>Runs the nearest-neighbour search (the port's search call).</summary>
internal sealed record VectorSearchStep(
    Scope Scope,
    string ModelId,
    float[] Vector,
    IReadOnlyList<ExperienceStatus> Statuses,
    double MinConfidence,
    int Limit,
    bool ExcludeModelAuthored) : VectorStep;

/// <summary>
/// Moves a record to a later revision, as the schema's owner: what a lifecycle commit does to it, so a write computed at
/// the earlier revision is stale.
/// </summary>
internal sealed record SetRevisionStep(Guid ExperienceId, long Revision) : VectorStep;

/// <summary>Erases a record through <c>agent_experience.purge_experience_record</c>, as the schema's owner.</summary>
internal sealed record EraseEmbeddingStep(EraseStep Erase) : VectorStep;

/// <summary>An <c>embedding-write</c> or <c>vector-search</c> case: the records seeded (as stored), the description and the steps.</summary>
internal sealed record VectorCase(string Description, IReadOnlyList<ExperienceRecord> Records, IReadOnlyList<VectorStep> Steps);

/// <summary>
/// Contract 1.6's two PostgreSQL families, <c>embedding-write</c> and <c>vector-search</c>, with fixed vectors (no model).
/// Running them needs PostgreSQL with pgvector, so the vector PostgreSQL contract test runs them. Each case writes
/// <c>seed.json</c> (stored records, exactly as a search seed holds them), <c>steps.json</c> (the description and the
/// steps) and <c>expected.json</c> (one entry per step, and every row of <c>agent_experience.experience_embeddings</c>
/// afterwards).
/// </summary>
internal static class VectorContract
{
    public const string WriteFamily = "embedding-write";

    public const string SearchFamily = "vector-search";

    /// <summary>The model every case's embeddings come from, unless a case names another.</summary>
    public const string Model = "contract-embedding";

    /// <summary>Every <c>embedding-write</c> case, by directory name.</summary>
    public static IReadOnlyList<(string Name, VectorCase Case)> WriteCases { get; } =
    [
        ("written-with-the-record-scope", WrittenWithTheRecordScope()),
        ("revisions-newer-replaces-older-is-stale", Revisions()),
        ("missing-unknown-other-scope-or-erased", Missing()),
    ];

    /// <summary>Every <c>vector-search</c> case, by directory name.</summary>
    public static IReadOnlyList<(string Name, VectorCase Case)> SearchCases { get; } =
    [
        ("nearest-first", NearestFirst()),
        ("an-exact-tie-is-ordered-by-id", ExactTie()),
        ("scope-status-confidence-and-erasure", ScopeStatusConfidenceAndErasure()),
        ("model-authored-excluded", ModelAuthoredExcluded()),
        ("nothing-comparable-is-found-empty", NothingComparable()),
        ("another-model-or-dimension", AnotherModelOrDimension()),
        ("zero-magnitude-query", ZeroMagnitudeQuery()),
        ("zero-magnitude-stored-vector", ZeroMagnitudeStoredVector()),
    ];

    private static DateTimeOffset T1 => Fixed.T0.AddHours(1);

    private static VectorCase WrittenWithTheRecordScope()
    {
        const int f = 0x70;
        var a = Record(f, 1);
        var b = SearchContract.Seed(f, 2, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.",
            scope: new Scope("contract-tenant", "contract-app", "contract-project", TeamId: "payments", AgentId: null, UserId: "user-7"));
        return new(
            "A write for a live record at its revision stores one row: the scope columns are the record's (a scope with a team " +
            "and a user, and one with an agent only), the descriptor and the vector are the write's, and created_at and " +
            "updated_at are the write's clock.",
            [a, b],
            [Write(a, [1f, 0f, 0f, 0f], T1), Write(b, [0f, 1f, 0f, 0f], T1.AddMinutes(1))]);
    }

    private static VectorCase Revisions()
    {
        const int f = 0x71;
        var a = Record(f, 1);
        return new(
            "A record moves from revision 1 to 2. A write computed at revision 1 is then Stale, with the current revision, and " +
            "leaves the stored row as it was; a write at revision 2 replaces it (created_at kept, updated_at the new clock), " +
            "including with another model and dimension.",
            [a],
            [
                Write(a, [1f, 0f, 0f, 0f], T1),
                new SetRevisionStep(a.ExperienceId, 2),
                Write(a, [0f, 1f, 0f, 0f], T1.AddMinutes(1)),
                Write(a, [0f, 0f, 1f, 0f], T1.AddMinutes(2), revision: 2),
                Write(a, [0f, 0f, 0f, 1f], T1.AddMinutes(3)),
                Write(a, [1f, 2f, 3f, 4f, 5f], T1.AddMinutes(4), revision: 2, model: "contract-embedding-v2"),
            ]);
    }

    private static VectorCase Missing()
    {
        const int f = 0x72;
        var a = Record(f, 1);
        var b = Record(f, 2);
        return new(
            "Missing, and no row: a write for an unknown ID, for a record named in another scope (its own scope, one agent " +
            "away), and for an erased record (whose row the erasure removed).",
            [a, b],
            [
                Write(a, [1f, 0f, 0f, 0f], T1) with { ExperienceId = Fixed.Id(f, 9) },
                Write(a, [1f, 0f, 0f, 0f], T1) with { Scope = Fixed.Scope with { AgentId = "other-agent" } },
                Write(b, [0f, 1f, 0f, 0f], T1),
                new EraseEmbeddingStep(new EraseStep(b.ExperienceId, b.Scope, T1.AddMinutes(1))),
                Write(b, [0f, 1f, 0f, 0f], T1.AddMinutes(2)),
            ]);
    }

    private static VectorCase NearestFirst()
    {
        const int f = 0x73;
        var (far, near, nearest) = (Record(f, 1), Record(f, 2), Record(f, 3));
        return new(
            "Query [1,0,0,0] over [1,1,0,0], [1,0.1,0,0] and [1,0,0,0]: nearest first, whatever the ID order, each with its " +
            "cosine distance and relevance = 1 - distance / 2. A limit of 2 keeps the two nearest.",
            [far, near, nearest],
            [
                Write(far, [1f, 1f, 0f, 0f], T1),
                Write(near, [1f, 0.1f, 0f, 0f], T1),
                Write(nearest, [1f, 0f, 0f, 0f], T1),
                Search([1f, 0f, 0f, 0f]),
                Search([1f, 0f, 0f, 0f]) with { Limit = 2 },
            ]);
    }

    private static VectorCase ExactTie()
    {
        const int f = 0x74;
        var (a, b, c) = (Record(f, 1), Record(f, 2), Record(f, 3));
        return new(
            "Two records at exactly the same distance ([2,0,0,0] and [1,0,0,0] point the same way), written in descending ID " +
            "order: they are ordered by experience ID ascending.",
            [a, b, c],
            [
                Write(c, [0f, 1f, 0f, 0f], T1),
                Write(b, [2f, 0f, 0f, 0f], T1),
                Write(a, [1f, 0f, 0f, 0f], T1),
                Search([1f, 0f, 0f, 0f]),
            ]);
    }

    private static VectorCase ScopeStatusConfidenceAndErasure()
    {
        const int f = 0x75;
        var mine = Record(f, 1);
        var otherAgent = SearchContract.Seed(f, 2, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", scope: Fixed.Scope with { AgentId = "other-agent" });
        var otherTenant = SearchContract.Seed(f, 3, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", scope: Fixed.Scope with { TenantId = "other-tenant" });
        var quarantined = SearchContract.Seed(f, 4, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", status: ExperienceStatus.Quarantined, confidence: 0);
        var candidate = SearchContract.Seed(f, 5, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", status: ExperienceStatus.Candidate);
        var unsure = SearchContract.Seed(f, 6, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.", confidence: 0.4);
        var erased = Record(f, 7);
        var reinforced = SearchContract.Seed(f, 8, "rotate-keys", "Rotate the signing keys", "Verified after 2 attempts.", status: ExperienceStatus.Reinforced, confidence: 0.9);
        float[] same = [1f, 0f, 0f, 0f];
        return new(
            "Every record has the query's own vector. Only the reader's live, eligible records at or above the confidence floor " +
            "come back: not another agent's or tenant's (each written in its own scope), not a Quarantined or Candidate record, " +
            "not one under 0.5 confidence, and not an erased one.",
            [mine, otherAgent, otherTenant, quarantined, candidate, unsure, erased, reinforced],
            [
                .. new[] { mine, otherAgent, otherTenant, quarantined, candidate, unsure, erased, reinforced }.Select(record => (VectorStep)Write(record, same, T1)),
                new EraseEmbeddingStep(new EraseStep(erased.ExperienceId, erased.Scope, T1.AddMinutes(1))),
                Search(same),
            ]);
    }

    private static VectorCase ModelAuthoredExcluded()
    {
        const int f = 0x76;
        var deterministic = Record(f, 1);
        var modelAuthored = SearchContract.Seed(f, 2, "rotate-keys", "Rotate the signing keys", "Use a staged rollout.", modelAuthored: true);
        return new(
            "A model-authored record is returned by the search, and left out by the search that excludes model-written lessons " +
            "(before the limit), as the text search leaves it out.",
            [deterministic, modelAuthored],
            [
                Write(deterministic, [1f, 0.5f, 0f, 0f], T1),
                Write(modelAuthored, [1f, 0f, 0f, 0f], T1),
                Search([1f, 0f, 0f, 0f]),
                Search([1f, 0f, 0f, 0f]) with { ExcludeModelAuthored = true, Limit = 1 },
            ]);
    }

    private static VectorCase NothingComparable()
    {
        const int f = 0x77;
        var (a, quarantined) = (Record(f, 1), SearchContract.Seed(f, 2, "rotate-keys", "Rotate the signing keys", "Lock timeout.", status: ExperienceStatus.Quarantined, confidence: 0));
        return new(
            "Found and empty: no embedding is stored in the scope; and later the only one stored belongs to a record the search " +
            "leaves out (Quarantined), which the compatibility probe leaves out too.",
            [a, quarantined],
            [
                Search([1f, 0f, 0f, 0f]),
                Write(quarantined, [1f, 0f, 0f, 0f], T1),
                Search([1f, 0f, 0f, 0f]),
            ]);
    }

    private static VectorCase AnotherModelOrDimension()
    {
        const int f = 0x78;
        var (a, b) = (Record(f, 1), Record(f, 2));
        return new(
            "Embeddings stored under the model at dimension 5 only (the one dimension-5 case). A dimension-4 query under another model is ModelMismatch; " +
            "under this model it is DimensionMismatch; a dimension-5 query under this model finds them. No rows come back with " +
            "a mismatch.",
            [a, b],
            [
                Write(a, [1f, 0f, 0f, 0f, 0f], T1),
                Write(b, [0f, 1f, 0f, 0f, 1f], T1),
                Search([1f, 0f, 0f, 0f]) with { ModelId = "another-model" },
                Search([1f, 0f, 0f, 0f]),
                Search([1f, 0f, 0f, 0f, 1f]),
            ]);
    }

    private static VectorCase ZeroMagnitudeQuery()
    {
        const int f = 0x79;
        var (a, b) = (Record(f, 1), Record(f, 2));
        return new(
            "A zero-magnitude query vector is valid, but has no direction: every distance is NaN (written null) and every " +
            "relevance 0, and the rows are in experience ID order.",
            [a, b],
            [
                Write(b, [1f, 0f, 0f, 0f], T1),
                Write(a, [0f, 1f, 0f, 0f], T1),
                Search([0f, 0f, 0f, 0f]),
            ]);
    }

    private static VectorCase ZeroMagnitudeStoredVector()
    {
        const int f = 0x7b;
        var (zero, same, orthogonal) = (Record(f, 1), Record(f, 2), Record(f, 3));
        return new(
            "A stored zero-magnitude vector is valid, but has no direction: against a normal query its distance is NaN (written " +
            "null) and its relevance 0, and it comes after every row with a number for a distance (PostgreSQL orders NaN after " +
            "every number), whatever its ID.",
            [zero, same, orthogonal],
            [
                Write(zero, [0f, 0f, 0f, 0f], T1),
                Write(same, [1f, 0f, 0f, 0f], T1),
                Write(orthogonal, [0f, 1f, 0f, 0f], T1),
                Search([1f, 0f, 0f, 0f]),
            ]);
    }

    private static ExperienceRecord Record(int family, int n) =>
        SearchContract.Seed(family, n, "rotate-keys", "Rotate the signing keys", "Verified after 1 attempt.");

    /// <summary>A write of <paramref name="vector"/> for <paramref name="record"/>, its content hash the record's summary's.</summary>
    private static WriteEmbeddingStep Write(ExperienceRecord record, float[] vector, DateTimeOffset now, long? revision = null, string? model = null)
    {
        var modelId = model ?? Model;
        return new(
            record.ExperienceId,
            record.Scope,
            modelId,
            vector.Length,
            ExperienceEmbeddingDescriptor.ComputeContentHash(modelId, ExperienceRetrievalSummary.For(record)),
            revision ?? record.Revision,
            vector,
            now);
    }

    private static VectorSearchStep Search(float[] vector) => new(
        Fixed.Scope,
        Model,
        vector,
        ExperienceRetrievalService.EligibleStatuses,
        RetrievalPolicy.DefaultMinimumConfidence,
        RetrievalPolicy.DefaultCandidateLimit + 1,
        ExcludeModelAuthored: false);

    /// <summary><c>seed.json</c>: the records as stored.</summary>
    public static JsonArray ToSeedJson(VectorCase definition) => SearchContract.ToSeedJson(definition.Records);

    /// <summary><c>steps.json</c>: the description and the steps, in order.</summary>
    public static JsonObject ToStepsJson(VectorCase definition) => new()
    {
        ["description"] = definition.Description,
        ["steps"] = new JsonArray([.. definition.Steps.Select(step => (JsonNode)(step switch
        {
            WriteEmbeddingStep write => new JsonObject { ["write"] = JsonSerializer.SerializeToNode(write, ContractJson.Options) },
            VectorSearchStep search => new JsonObject { ["search"] = JsonSerializer.SerializeToNode(search, ContractJson.Options) },
            SetRevisionStep set => new JsonObject { ["setRevision"] = JsonSerializer.SerializeToNode(set, ContractJson.Options) },
            EraseEmbeddingStep erase => InjectionRecordContract.StepJson(erase.Erase),
            _ => throw new InvalidOperationException($"Unknown step {step}."),
        }))]),
    };

    /// <summary>Reads <c>steps.json</c> back: the steps a runner runs.</summary>
    public static IReadOnlyList<VectorStep> ReadSteps(JsonNode steps) =>
    [
        .. steps["steps"]!.AsArray().Select(step => step!["write"] is { } write
            ? ContractJson.Read<WriteEmbeddingStep>(write.ToJsonString())
            : step["search"] is { } search
                ? ContractJson.Read<VectorSearchStep>(search.ToJsonString())
                : step["setRevision"] is { } set
                    ? ContractJson.Read<SetRevisionStep>(set.ToJsonString())
                    : (VectorStep)new EraseEmbeddingStep((EraseStep)InjectionRecordContract.ReadStep(step))),
    ];

    /// <summary>A vector as a pgvector literal, <c>[x,y,...]</c>, each component's shortest round-trip text.</summary>
    public static string Literal(float[] vector) =>
        "[" + string.Join(",", vector.Select(component => component.ToString("R", CultureInfo.InvariantCulture))) + "]";

    /// <summary>The contract's relevance for a cosine distance: <c>clamp(1 - d / 2, 0, 1)</c>, and 0 for NaN.</summary>
    public static double Relevance(double distance) => double.IsNaN(distance) ? 0d : Math.Clamp(1d - (distance / 2d), 0d, 1d);
}
