using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IExperienceEmbeddingIndex"/> must show, observed through the ports alone: a write is
/// conditional on the record existing in exactly the write's scope at exactly the descriptor's revision (otherwise
/// <see cref="ExperienceIndexOutcome.Stale"/> or <see cref="ExperienceIndexOutcome.Missing"/>, and nothing changes); a
/// scan lists only what a search could return, with the stored descriptor; a search applies the exact scope, the
/// statuses, the confidence floor and the authorship exclusion before the limit, compares only vectors of the query's
/// model and width, ranks the nearest first with a relevance in [0, 1], and tells an incomparable scope from an empty
/// match; removal is idempotent and scoped. Records are seeded through the record store the index reads, so a subclass
/// supplies both: <see cref="CreateRecordStore"/> and <see cref="CreateEmbeddingIndex"/> must see the same records.
/// </summary>
/// <remarks>Sharing grants, erasure and encryption are adapter features the port does not require, so none is tested here.</remarks>
public abstract class EmbeddingIndexConformanceTests
{
    private const string Model = "conformance-embed-v1";

    private static readonly ExperienceStatus[] Eligible = [ExperienceStatus.Validated, ExperienceStatus.Reinforced];

    private readonly Lazy<IExperienceRecordStore> _store;
    private readonly Lazy<IExperienceEmbeddingIndex> _index;

    protected EmbeddingIndexConformanceTests()
    {
        _store = new Lazy<IExperienceRecordStore>(CreateRecordStore);
        _index = new Lazy<IExperienceEmbeddingIndex>(CreateEmbeddingIndex);
    }

    /// <summary>The record store the test seeds records through.</summary>
    protected IExperienceRecordStore RecordStore => _store.Value;

    /// <summary>The embedding index under test.</summary>
    protected IExperienceEmbeddingIndex Index => _index.Value;

    /// <summary>Creates the record store whose records <see cref="CreateEmbeddingIndex"/> indexes. Called at most once per test.</summary>
    protected abstract IExperienceRecordStore CreateRecordStore();

    /// <summary>Creates the embedding index under test. Called at most once per test.</summary>
    protected abstract IExperienceEmbeddingIndex CreateEmbeddingIndex();

    [Fact]
    public async Task A_write_for_the_record_s_current_revision_is_stored_and_listed_by_a_scan()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        var hash = Hash();

        var written = await WriteAsync(tenant, scope, id, Axis(0), revision: 0, hash: hash);
        Assert.Equal(ExperienceIndexOutcome.Written, written.Outcome);
        Assert.Empty(written.Errors);

        var scan = await ScanAsync(tenant, scope);
        var target = Assert.Single(scan.Targets);
        Assert.Equal(id, target.ExperienceId);
        Assert.Equal(0, target.SourceRevision);
        Assert.Equal(ExperienceRetrievalSummary.For("conformance-task", "Resolve a refund ticket", "Release the lock first"), target.Summary);
        Assert.Equal(new ExperienceEmbeddingDescriptor(Model, 4, hash, 0), target.Stored);
        Assert.Equal(id, scan.LastExaminedId);
    }

    [Fact]
    public async Task A_write_whose_revision_has_moved_is_Stale_with_the_current_revision_and_changes_nothing()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        Assert.Equal(ExperienceIndexOutcome.Written, (await WriteAsync(tenant, scope, id, Axis(0), revision: 0, hash: Hash())).Outcome);
        await MoveRevisionAsync(tenant, scope, id);

        var stale = await WriteAsync(tenant, scope, id, Axis(1), revision: 0, hash: "a-different-hash");

        Assert.Equal(ExperienceIndexOutcome.Stale, stale.Outcome);
        Assert.Equal(1, stale.CurrentRevision);
        Assert.Equal(0, Assert.Single((await ScanAsync(tenant, scope)).Targets).Stored!.SourceRevision);
        Assert.Equal(id, Assert.Single((await SearchAsync(tenant, scope, Axis(0))).Candidates).Record.ExperienceId);
        Assert.Empty((await SearchAsync(tenant, scope, Axis(1))).Candidates.Where(c => c.Relevance > 0.9));
    }

    [Fact]
    public async Task A_write_for_a_record_that_is_not_there_or_is_in_another_scope_is_Missing_and_stores_nothing()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var foreign = Scope(tenant, project: "project-2");
        var elsewhere = await SeedAsync(tenant, foreign);

        Assert.Equal(ExperienceIndexOutcome.Missing, (await WriteAsync(tenant, scope, Guid.NewGuid(), Axis(0))).Outcome);
        Assert.Equal(ExperienceIndexOutcome.Missing, (await WriteAsync(tenant, scope, elsewhere, Axis(0))).Outcome);

        Assert.Null(Assert.Single((await ScanAsync(tenant, foreign)).Targets).Stored);
        Assert.Empty((await SearchAsync(tenant, foreign, Axis(0))).Candidates);
    }

    [Fact]
    public async Task A_scan_lists_only_what_a_search_could_return_in_ascending_ID_order_and_pages_by_its_cursor()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var eligible = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            eligible.Add(await SeedAsync(tenant, scope));
        }

        await SeedAsync(tenant, scope, status: ExperienceStatus.Quarantined);
        await SeedAsync(tenant, scope, confidence: 0.2);
        await SeedAsync(tenant, Scope(tenant, project: "project-2"));

        var first = await ScanAsync(tenant, scope, limit: 2);
        var second = await ScanAsync(tenant, scope, limit: 2, startAfter: first.LastExaminedId);
        var third = await ScanAsync(tenant, scope, limit: 2, startAfter: second.LastExaminedId);

        var listed = first.Targets.Concat(second.Targets).Select(target => target.ExperienceId).ToList();
        Assert.Equal(2, first.Targets.Count);
        Assert.Equal(eligible.Order().ToList(), listed.Order().ToList());
        Assert.Empty(third.Targets);
        Assert.Null(third.LastExaminedId);

        var only = await ScanAsync(tenant, scope, ids: [eligible[1]]);
        Assert.Equal([eligible[1]], only.Targets.Select(target => target.ExperienceId));
    }

    [Fact]
    public async Task A_search_returns_the_nearest_first_with_relevance_in_zero_to_one_and_applies_the_limit_after_ranking()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var far = await SeedAsync(tenant, scope);
        var near = await SeedAsync(tenant, scope);
        var nearest = await SeedAsync(tenant, scope);
        await WriteAsync(tenant, scope, far, Axis(3));
        await WriteAsync(tenant, scope, near, [1f, 1f, 0f, 0f]);
        await WriteAsync(tenant, scope, nearest, [1f, 0.1f, 0f, 0f]);

        var all = await SearchAsync(tenant, scope, Axis(0));
        var limited = await SearchAsync(tenant, scope, Axis(0), limit: 1);

        Assert.Equal(ExperienceVectorSearchOutcome.Found, all.Outcome);
        Assert.Equal([nearest, near, far], all.Candidates.Select(c => c.Record.ExperienceId));
        Assert.All(all.Candidates, candidate => Assert.InRange(candidate.Relevance, 0d, 1d));
        Assert.True(all.Candidates[0].Relevance > all.Candidates[1].Relevance);
        Assert.True(all.Candidates[1].Relevance > all.Candidates[2].Relevance);
        Assert.Equal([nearest], limited.Candidates.Select(c => c.Record.ExperienceId));
        Assert.All(all.Candidates, candidate => Assert.False(candidate.SharedByGrant));
    }

    [Fact]
    public async Task A_search_never_returns_another_scope_s_record_however_close_its_vector()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var mine = await SeedAsync(tenant, scope);
        await WriteAsync(tenant, scope, mine, [1f, 1f, 1f, 0f]);
        foreach (var other in new[] { Scope(tenant, project: "project-2"), Scope(tenant, team: "team-1"), Scope(tenant, project: "Project-1") })
        {
            var id = await SeedAsync(tenant, other);
            Assert.Equal(ExperienceIndexOutcome.Written, (await WriteAsync(tenant, other, id, Axis(0))).Outcome);
        }

        Assert.Equal([mine], (await SearchAsync(tenant, scope, Axis(0))).Candidates.Select(c => c.Record.ExperienceId));
    }

    [Fact]
    public async Task A_search_applies_the_statuses_the_confidence_floor_and_the_authorship_exclusion()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var kept = await SeedAsync(tenant, scope, confidence: 0.5);
        var revoked = await SeedAsync(tenant, scope);
        var low = await SeedAsync(tenant, scope, confidence: 0.49);
        var modelAuthored = await SeedAsync(tenant, scope, modelAuthored: true);
        foreach (var id in new[] { kept, revoked, low, modelAuthored })
        {
            Assert.Equal(ExperienceIndexOutcome.Written, (await WriteAsync(tenant, scope, id, Axis(0))).Outcome);
        }

        var commit = await RecordStore.CommitLifecycleEventAsync(
            Authorize(tenant), scope, Event(revoked, ExperienceStatus.Validated, ExperienceStatus.Revoked, 0), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, commit.Outcome);

        var included = await SearchAsync(tenant, scope, Axis(0), minimumConfidence: 0.5);
        var excluding = await SearchAsync(tenant, scope, Axis(0), minimumConfidence: 0.5, excludeModelAuthored: true);

        Assert.Equal(new[] { kept, modelAuthored }.Order(), included.Candidates.Select(c => c.Record.ExperienceId).Order());
        Assert.Equal([kept], excluding.Candidates.Select(c => c.Record.ExperienceId));
    }

    [Fact]
    public async Task An_incomparable_scope_is_a_model_or_dimension_mismatch_and_an_empty_one_is_an_empty_match()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        Assert.Equal(ExperienceVectorSearchOutcome.Found, (await SearchAsync(tenant, scope, Axis(0))).Outcome);

        var id = await SeedAsync(tenant, scope);
        await WriteAsync(tenant, scope, id, Axis(0));

        var otherModel = await Index.SearchAsync(
            Authorize(tenant), new ExperienceVectorQuery(scope, "another-model", Axis(0), Eligible, 0d), CancellationToken.None);
        var otherWidth = await Index.SearchAsync(
            Authorize(tenant), new ExperienceVectorQuery(scope, Model, new float[] { 1f, 0f, 0f }, Eligible, 0d), CancellationToken.None);

        Assert.Equal(ExperienceVectorSearchOutcome.ModelMismatch, otherModel.Outcome);
        Assert.Empty(otherModel.Candidates);
        Assert.Equal(ExperienceVectorSearchOutcome.DimensionMismatch, otherWidth.Outcome);
        Assert.Empty(otherWidth.Candidates);
    }

    [Fact]
    public async Task An_excluding_search_over_a_scope_whose_only_comparable_vectors_are_model_authored_is_an_empty_Found()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope, modelAuthored: true);
        await WriteAsync(tenant, scope, id, Axis(0));

        var result = await SearchAsync(tenant, scope, Axis(0), excludeModelAuthored: true);

        Assert.Equal(ExperienceVectorSearchOutcome.Found, result.Outcome);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task A_rewrite_at_the_same_revision_under_a_new_model_makes_the_old_model_a_ModelMismatch()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        await WriteAsync(tenant, scope, id, Axis(0));

        var rewritten = await Index.WriteAsync(
            Authorize(tenant),
            new ExperienceIndexWrite(
                scope,
                id,
                new ExperienceEmbeddingDescriptor("conformance-embed-v2", 4, ExperienceEmbeddingDescriptor.ComputeContentHash("conformance-embed-v2", Summary), 0),
                Axis(0)),
            CancellationToken.None);
        Assert.Equal(ExperienceIndexOutcome.Written, rewritten.Outcome);

        Assert.Equal(ExperienceVectorSearchOutcome.ModelMismatch, (await SearchAsync(tenant, scope, Axis(0))).Outcome);
        var newModel = await Index.SearchAsync(
            Authorize(tenant), new ExperienceVectorQuery(scope, "conformance-embed-v2", Axis(0), Eligible, 0d), CancellationToken.None);
        Assert.Equal([id], newModel.Candidates.Select(c => c.Record.ExperienceId));
    }

    [Fact]
    public async Task A_scope_holding_comparable_and_incomparable_vectors_is_Found_with_only_the_comparable_ones()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var comparable = await SeedAsync(tenant, scope);
        var otherModel = await SeedAsync(tenant, scope);
        var otherWidth = await SeedAsync(tenant, scope);
        await WriteAsync(tenant, scope, comparable, Axis(0));
        await Index.WriteAsync(
            Authorize(tenant),
            new ExperienceIndexWrite(scope, otherModel, new ExperienceEmbeddingDescriptor("another-model", 4, "hash", 0), Axis(0)),
            CancellationToken.None);
        await WriteAsync(tenant, scope, otherWidth, [1f, 0f, 0f]);

        var result = await SearchAsync(tenant, scope, Axis(0));

        Assert.Equal(ExperienceVectorSearchOutcome.Found, result.Outcome);
        Assert.Equal([comparable], result.Candidates.Select(c => c.Record.ExperienceId));
    }

    [Fact]
    public async Task Removal_takes_the_vector_out_of_the_search_and_repeating_it_or_naming_another_scope_is_NotIndexed()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        await WriteAsync(tenant, scope, id, Axis(0));

        var foreign = await Index.RemoveAsync(Authorize(tenant), Scope(tenant, project: "project-2"), id, CancellationToken.None);
        Assert.Equal(ExperienceIndexRemoveOutcome.NotIndexed, foreign.Outcome);
        Assert.Single((await SearchAsync(tenant, scope, Axis(0))).Candidates);

        Assert.Equal(ExperienceIndexRemoveOutcome.Removed, (await Index.RemoveAsync(Authorize(tenant), scope, id, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceIndexRemoveOutcome.NotIndexed, (await Index.RemoveAsync(Authorize(tenant), scope, id, CancellationToken.None)).Outcome);
        Assert.Empty((await SearchAsync(tenant, scope, Axis(0))).Candidates);
        Assert.Null(Assert.Single((await ScanAsync(tenant, scope)).Targets).Stored);
    }

    [Fact]
    public async Task A_scope_outside_the_authorization_is_Denied_everywhere_and_writes_nothing()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        var stranger = Authorize(NewTenant());

        var write = await Index.WriteAsync(
            stranger, new ExperienceIndexWrite(scope, id, new ExperienceEmbeddingDescriptor(Model, 4, "hash", 0), Axis(0)), CancellationToken.None);
        var scan = await Index.ScanAsync(stranger, new ExperienceIndexScan(scope, Model, Eligible, 0d), CancellationToken.None);
        var search = await Index.SearchAsync(stranger, new ExperienceVectorQuery(scope, Model, Axis(0), Eligible, 0d), CancellationToken.None);
        var remove = await Index.RemoveAsync(stranger, scope, id, CancellationToken.None);

        Assert.Equal(ExperienceIndexOutcome.Denied, write.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, scan.Outcome);
        Assert.Equal(ExperienceVectorSearchOutcome.Denied, search.Outcome);
        Assert.Empty(search.Candidates);
        Assert.Equal(ExperienceIndexRemoveOutcome.Denied, remove.Outcome);
        Assert.Null(Assert.Single((await ScanAsync(tenant, scope)).Targets).Stored);
    }

    [Theory]
    [InlineData("write-width-disagrees")]
    [InlineData("write-non-finite")]
    [InlineData("write-empty-id")]
    [InlineData("search-blank-model")]
    [InlineData("search-empty-vector")]
    [InlineData("search-no-statuses")]
    [InlineData("search-limit")]
    [InlineData("scan-limit")]
    public async Task A_malformed_request_is_Invalid_with_errors(string malformation)
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        var authorization = Authorize(tenant);
        var descriptor = new ExperienceEmbeddingDescriptor(Model, 4, "hash", 0);

        IReadOnlyList<StoreValidationError> errors;
        switch (malformation)
        {
            case "write-width-disagrees":
            case "write-non-finite":
            case "write-empty-id":
                var write = malformation switch
                {
                    "write-width-disagrees" => new ExperienceIndexWrite(scope, id, descriptor, new float[] { 1f, 0f }),
                    "write-non-finite" => new ExperienceIndexWrite(scope, id, descriptor, new[] { float.NaN, 0f, 0f, 0f }),
                    _ => new ExperienceIndexWrite(scope, Guid.Empty, descriptor, Axis(0)),
                };
                var written = await Index.WriteAsync(authorization, write, CancellationToken.None);
                Assert.Equal(ExperienceIndexOutcome.Invalid, written.Outcome);
                errors = written.Errors;
                break;
            case "scan-limit":
                var scan = await Index.ScanAsync(authorization, new ExperienceIndexScan(scope, Model, Eligible, 0d, Limit: 0), CancellationToken.None);
                Assert.Equal(ExperienceStoreOutcome.Invalid, scan.Outcome);
                errors = scan.Errors;
                break;
            default:
                var valid = new ExperienceVectorQuery(scope, Model, Axis(0), Eligible, 0d);
                var query = malformation switch
                {
                    "search-blank-model" => valid with { ModelId = " " },
                    "search-empty-vector" => valid with { Vector = ReadOnlyMemory<float>.Empty },
                    "search-no-statuses" => valid with { EligibleStatuses = [] },
                    _ => valid with { Limit = ExperienceCandidateQuery.MaxLimit + 1 },
                };
                var searched = await Index.SearchAsync(authorization, query, CancellationToken.None);
                Assert.Equal(ExperienceVectorSearchOutcome.Invalid, searched.Outcome);
                Assert.Empty(searched.Candidates);
                errors = searched.Errors;
                break;
        }

        Assert.NotEmpty(errors);
        Assert.Null(Assert.Single((await ScanAsync(tenant, scope)).Targets).Stored);
    }

    [Fact]
    public async Task An_already_cancelled_token_surfaces_as_OperationCanceledException_on_every_call_and_changes_nothing()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var id = await SeedAsync(tenant, scope);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.WriteAsync(
            Authorize(tenant), new ExperienceIndexWrite(scope, id, new ExperienceEmbeddingDescriptor(Model, 4, "hash", 0), Axis(0)), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.SearchAsync(
            Authorize(tenant), new ExperienceVectorQuery(scope, Model, Axis(0), Eligible, 0d), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.ScanAsync(
            Authorize(tenant), new ExperienceIndexScan(scope, Model, Eligible, 0d), cancelled.Token));
        Assert.Null(Assert.Single((await ScanAsync(tenant, scope)).Targets).Stored);

        await WriteAsync(tenant, scope, id, Axis(0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Index.RemoveAsync(Authorize(tenant), scope, id, cancelled.Token));
        Assert.Single((await SearchAsync(tenant, scope, Axis(0))).Candidates);
    }

    /// <summary>A unit vector along one of four axes.</summary>
    protected static float[] Axis(int axis)
    {
        var vector = new float[4];
        vector[axis] = 1f;
        return vector;
    }

    private async Task<Guid> SeedAsync(
        string tenant,
        Scope scope,
        ExperienceStatus status = ExperienceStatus.Validated,
        double confidence = 0.8,
        bool modelAuthored = false)
    {
        var record = Record(scope, status, summary: "Resolve a refund ticket", lesson: "Release the lock first", confidence: confidence);
        if (modelAuthored)
        {
            record = record with { Reflection = record.Reflection! with { Authorship = ReflectionAuthorship.Model } };
        }

        var created = await RecordStore.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        return record.ExperienceId;
    }

    /// <summary>Moves a Validated record to Reinforced, which takes its revision from 0 to 1.</summary>
    private async Task MoveRevisionAsync(string tenant, Scope scope, Guid id)
    {
        var commit = await RecordStore.CommitLifecycleEventAsync(
            Authorize(tenant), scope, Event(id, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, commit.Outcome);
    }

    /// <summary>The content hash Core would compute for a record seeded here: the scan reports a stored one back as written.</summary>
    private static readonly string Summary = ExperienceRetrievalSummary.For("conformance-task", "Resolve a refund ticket", "Release the lock first");

    private static string Hash() => ExperienceEmbeddingDescriptor.ComputeContentHash(Model, Summary);

    private Task<ExperienceIndexWriteResult> WriteAsync(string tenant, Scope scope, Guid id, float[] vector, long revision = 0, string? hash = null) =>
        Index.WriteAsync(
            Authorize(tenant),
            new ExperienceIndexWrite(scope, id, new ExperienceEmbeddingDescriptor(Model, vector.Length, hash ?? Hash(), revision), vector),
            CancellationToken.None);

    private async Task<ExperienceIndexScanResult> ScanAsync(
        string tenant,
        Scope scope,
        int limit = ExperienceIndexScan.DefaultLimit,
        Guid? startAfter = null,
        IReadOnlyList<Guid>? ids = null)
    {
        var scan = await Index.ScanAsync(
            Authorize(tenant), new ExperienceIndexScan(scope, Model, Eligible, 0.5, ids, limit, startAfter), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, scan.Outcome);
        return scan;
    }

    private async Task<ExperienceVectorSearchResult> SearchAsync(
        string tenant,
        Scope scope,
        float[] vector,
        int limit = ExperienceCandidateQuery.DefaultLimit,
        double minimumConfidence = 0d,
        bool excludeModelAuthored = false)
    {
        var result = await Index.SearchAsync(
            Authorize(tenant),
            new ExperienceVectorQuery(scope, Model, vector, Eligible, minimumConfidence, limit) { ExcludeModelAuthored = excludeModelAuthored },
            CancellationToken.None);
        Assert.Contains(result.Outcome, new[] { ExperienceVectorSearchOutcome.Found, ExperienceVectorSearchOutcome.ModelMismatch, ExperienceVectorSearchOutcome.DimensionMismatch });
        return result;
    }
}
