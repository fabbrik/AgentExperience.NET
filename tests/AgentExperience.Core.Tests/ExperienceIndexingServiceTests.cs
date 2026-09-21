using AgentExperience.Core.Indexing;
using AgentExperience.Core.Retrieval;

namespace AgentExperience.Core.Tests;

/// <summary>
/// Covers <see cref="ExperienceIndexingService"/> against a deterministic fake generator and an
/// in-memory index that enforces the real conditional-write contract: one test per row of the story's
/// I/O and edge-case matrix that this service owns -- indexing after a commit, a provider that is
/// down, a stale write, a deleted record, and re-indexing both unchanged and changed records.
/// </summary>
public class ExperienceIndexingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly Scope RequestScope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host-principal", ["experience:write"], Now);

    private const string Summary = "refund-ticket Resolve a refund ticket Retry the refund after releasing the lock";

    // ---------------------------------------------------------------- matrix: index after commit

    [Fact]
    public async Task Indexing_a_committed_record_stores_the_vector_with_its_model_dimension_hash_and_revision()
    {
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var generator = new FakeEmbeddingGenerator();
        var service = new ExperienceIndexingService(index, generator);

        var result = await service.IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.Indexed, result.Outcome);
        Assert.True(result.IsIndexed);
        Assert.Null(result.Failure);

        var (descriptor, vector) = index.Stored[id];
        Assert.Equal("fake-embed-v1", descriptor.ModelId);
        Assert.Equal(4, descriptor.Dimension);
        Assert.Equal(ExperienceEmbeddingDescriptor.ComputeContentHash("fake-embed-v1", Summary), descriptor.ContentHash);
        Assert.Equal(1, descriptor.SourceRevision);
        Assert.Equal(FakeEmbeddingGenerator.VectorFor(Summary, 4).ToArray(), vector.ToArray());
        Assert.Equal(descriptor, result.Descriptor);
    }

    [Fact]
    public async Task Only_the_sanitized_retrieval_summary_is_ever_handed_to_the_provider()
    {
        // The service embeds what the index listed, not a record the caller happened to be holding:
        // attempts, tool calls, evidence, and environment never reach a provider because they are
        // never in the summary to begin with.
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var generator = new FakeEmbeddingGenerator();

        await new ExperienceIndexingService(index, generator).IndexAsync(Authorization, RequestScope, id);

        Assert.Equal([Summary], generator.Requests);
    }

    // ---------------------------------------------------------------- matrix: provider down

    [Fact]
    public async Task A_provider_that_throws_leaves_the_record_untouched_and_is_reported_as_retryable()
    {
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var service = new ExperienceIndexingService(
            index,
            new FakeEmbeddingGenerator { Throws = FakeEmbeddingGenerator.ThrownException });

        var result = await service.IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.ProviderFailed, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.False(result.IsIndexed);
        Assert.Same(FakeEmbeddingGenerator.ThrownException, result.Failure!.Exception);
        Assert.Empty(index.Stored);
        Assert.Empty(index.Writes);
    }

    [Fact]
    public async Task A_provider_that_returns_the_wrong_width_is_a_provider_failure_and_nothing_is_written()
    {
        // A descriptor that disagreed with its own vector would make every later comparison unsound,
        // so it never reaches the index at all.
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var service = new ExperienceIndexingService(index, new FakeEmbeddingGenerator { ReturnDimension = 3 });

        var result = await service.IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.ProviderFailed, result.Outcome);
        Assert.Empty(index.Writes);
        Assert.Empty(index.Stored);
    }

    [Fact]
    public async Task An_index_that_throws_is_reported_as_retryable_rather_than_propagating()
    {
        var id = Id(1);
        var index = new FakeEmbeddingIndex
        {
            Records = { [id] = new(1, Summary) },
            WriteThrows = FakeEmbeddingIndex.ThrownException,
        };

        var result = await new ExperienceIndexingService(index, new FakeEmbeddingGenerator())
            .IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.IndexFailed, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.Same(FakeEmbeddingIndex.ThrownException, result.Failure!.Exception);
    }

    // ---------------------------------------------------------------- matrix: stale write

    [Fact]
    public async Task A_record_that_moves_to_a_new_revision_before_the_write_lands_rejects_it_and_keeps_the_stored_vector()
    {
        var id = Id(1);
        FakeEmbeddingIndex? index = null;
        index = new FakeEmbeddingIndex
        {
            Records = { [id] = new(1, Summary) },
            // The record moves to revision 2 while this very write is in flight.
            BeforeWrite = _ => index!.Records[id] = new(2, Summary),
        };

        // Seed a vector from revision 1, so the test can prove the in-flight write did not replace it.
        var first = new ExperienceEmbeddingDescriptor("fake-embed-v1", 4, "seeded", 1);
        index.Stored[id] = (first, FakeEmbeddingGenerator.VectorFor("seeded", 4));

        var result = await new ExperienceIndexingService(index, new FakeEmbeddingGenerator())
            .IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.Stale, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.Contains("revision 2", result.Failure!.Reason, StringComparison.Ordinal);
        Assert.Equal(first, index.Stored[id].Descriptor);
        Assert.Equal(1, index.Stored[id].Descriptor.SourceRevision);
    }

    // ---------------------------------------------------------------- matrix: deleted record

    [Fact]
    public async Task A_record_deleted_before_the_write_lands_is_never_recreated()
    {
        var id = Id(1);
        FakeEmbeddingIndex? index = null;
        index = new FakeEmbeddingIndex
        {
            Records = { [id] = new(1, Summary) },
            BeforeWrite = _ => index!.Records.Remove(id),
        };

        var result = await new ExperienceIndexingService(index, new FakeEmbeddingGenerator())
            .IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.Missing, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Empty(index.Stored);
    }

    [Fact]
    public async Task A_record_that_never_existed_is_Missing_and_no_provider_is_called()
    {
        var index = new FakeEmbeddingIndex();
        var generator = new FakeEmbeddingGenerator();

        var result = await new ExperienceIndexingService(index, generator).IndexAsync(Authorization, RequestScope, Id(9));

        Assert.Equal(ExperienceIndexingOutcome.Missing, result.Outcome);
        Assert.Empty(generator.Requests);
        Assert.Empty(index.Writes);
    }

    // ---------------------------------------------------------------- matrix: reindex unchanged

    [Fact]
    public async Task Reindexing_an_unchanged_record_calls_no_provider_writes_nothing_and_reports_it_skipped()
    {
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var generator = new FakeEmbeddingGenerator();
        var service = new ExperienceIndexingService(index, generator);

        var first = await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));
        Assert.Equal(1, first.Indexed);

        var writesAfterFirst = index.Writes.Count;
        var second = await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));

        Assert.Equal(ExperienceReindexOutcome.Completed, second.Outcome);
        Assert.Equal(1, second.Examined);
        Assert.Equal(0, second.Indexed);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(ExperienceIndexingOutcome.Skipped, Assert.Single(second.Records).Outcome);

        // The whole point: an unchanged content hash under the same model costs exactly one read.
        Assert.Single(generator.Requests);
        Assert.Equal(writesAfterFirst, index.Writes.Count);
    }

    [Fact]
    public async Task A_vector_stored_under_a_different_model_is_re_embedded_rather_than_skipped()
    {
        // The model ID is inside the content hash on purpose: the same text under a different model
        // is a different, incomparable vector and must never look "unchanged".
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        index.Stored[id] = (
            new ExperienceEmbeddingDescriptor("other-model", 4, ExperienceEmbeddingDescriptor.ComputeContentHash("other-model", Summary), 1),
            FakeEmbeddingGenerator.VectorFor(Summary, 4));

        var generator = new FakeEmbeddingGenerator();
        var result = await new ExperienceIndexingService(index, generator).IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.Indexed, result.Outcome);
        Assert.Single(generator.Requests);
        Assert.Equal("fake-embed-v1", index.Stored[id].Descriptor.ModelId);
    }

    // ---------------------------------------------------------------- matrix: reindex changed

    [Fact]
    public async Task A_changed_summary_is_re_embedded_and_rewritten_once_and_the_repeat_is_idempotent()
    {
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var generator = new FakeEmbeddingGenerator();
        var service = new ExperienceIndexingService(index, generator);

        await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));
        var firstHash = index.Stored[id].Descriptor.ContentHash;

        const string Changed = Summary + " and confirm the ledger entry";
        index.Records[id] = new(1, Changed);

        var rewritten = await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));
        Assert.Equal(1, rewritten.Indexed);
        Assert.Equal(0, rewritten.Skipped);
        Assert.NotEqual(firstHash, index.Stored[id].Descriptor.ContentHash);
        Assert.Equal(FakeEmbeddingGenerator.VectorFor(Changed, 4).ToArray(), index.Stored[id].Vector.ToArray());
        Assert.Equal(2, generator.Requests.Count);

        var repeat = await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));
        Assert.Equal(0, repeat.Indexed);
        Assert.Equal(1, repeat.Skipped);
        Assert.Equal(2, generator.Requests.Count);
    }

    [Fact]
    public async Task Whitespace_only_differences_in_the_summary_never_cause_a_re_embedding()
    {
        // The summary is normalized before it is hashed, so re-indentation is not a content change.
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, "refund ticket lesson") } };
        var generator = new FakeEmbeddingGenerator();
        var service = new ExperienceIndexingService(index, generator);

        await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));

        Assert.Equal(
            ExperienceEmbeddingDescriptor.ComputeContentHash("fake-embed-v1", ExperienceRetrievalSummary.For("refund", "ticket", "lesson")),
            index.Stored[id].Descriptor.ContentHash);
        Assert.Equal("refund ticket lesson", ExperienceRetrievalSummary.For("  refund\n\t", " ticket  ", "lesson"));
    }

    // ---------------------------------------------------------------- scope, authorization, bounds

    [Fact]
    public async Task A_scope_outside_the_authorization_is_denied_before_anything_is_read_or_embedded()
    {
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var generator = new FakeEmbeddingGenerator();
        var elsewhere = new AuthorizationContext("tenant-1", "p", [], Now, ProjectId: "elsewhere");

        var result = await new ExperienceIndexingService(index, generator).IndexAsync(elsewhere, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.Denied, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Empty(generator.Requests);
        Assert.Empty(index.Writes);
    }

    [Fact]
    public async Task A_reindex_pass_is_scoped_and_bounded_and_says_so_in_the_scan_it_issues()
    {
        var index = new FakeEmbeddingIndex();
        for (var n = 1; n <= 5; n++)
        {
            index.Records[Id(n)] = new(1, $"summary {n}");
        }

        var service = new ExperienceIndexingService(index, new FakeEmbeddingGenerator());
        var result = await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope, Limit: 3));

        var scan = Assert.Single(index.Scans);
        Assert.Equal(RequestScope, scan.Scope);
        Assert.Equal("fake-embed-v1", scan.ModelId);
        Assert.Equal(3, scan.Limit);
        Assert.Null(scan.ExperienceIds);
        Assert.Equal(3, result.Examined);
        Assert.Equal(3, result.Indexed);
    }

    [Fact]
    public async Task A_reindex_narrowed_to_specific_records_passes_exactly_those_ids_through()
    {
        var index = new FakeEmbeddingIndex
        {
            Records = { [Id(1)] = new(1, "one"), [Id(2)] = new(1, "two"), [Id(3)] = new(1, "three") },
        };

        var service = new ExperienceIndexingService(index, new FakeEmbeddingGenerator());
        var result = await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope, [Id(1), Id(3)]));

        Assert.Equal([Id(1), Id(3)], Assert.Single(index.Scans).ExperienceIds);
        Assert.Equal(2, result.Examined);
        Assert.Equal([Id(1), Id(3)], result.Records.Select(record => record.ExperienceId));
    }

    [Fact]
    public async Task A_pass_whose_scan_fails_examines_nothing_and_reports_the_failure()
    {
        var index = new FakeEmbeddingIndex { ScanThrows = FakeEmbeddingIndex.ThrownException };

        var result = await new ExperienceIndexingService(index, new FakeEmbeddingGenerator())
            .ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));

        Assert.Equal(ExperienceReindexOutcome.Failed, result.Outcome);
        Assert.Equal(0, result.Examined);
        Assert.Empty(result.Records);
        Assert.Same(FakeEmbeddingIndex.ThrownException, result.Failure!.Exception);
    }

    [Fact]
    public async Task A_pass_tallies_every_per_record_outcome_it_saw()
    {
        var index = new FakeEmbeddingIndex
        {
            Records = { [Id(1)] = new(1, "one"), [Id(2)] = new(1, "two") },
        };

        // Id(2) is already indexed under this model with exactly this text, so it is skipped.
        index.Stored[Id(2)] = (
            new ExperienceEmbeddingDescriptor("fake-embed-v1", 4, ExperienceEmbeddingDescriptor.ComputeContentHash("fake-embed-v1", "two"), 1),
            FakeEmbeddingGenerator.VectorFor("two", 4));

        var result = await new ExperienceIndexingService(index, new FakeEmbeddingGenerator())
            .ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));

        Assert.Equal(ExperienceReindexOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.Examined);
        Assert.Equal(1, result.Indexed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(0, result.Failed);
    }

    // ---------------------------------------------------------------- eligibility and paging

    [Fact]
    public async Task A_record_a_search_could_never_return_is_never_listed_and_never_embedded()
    {
        var index = new FakeEmbeddingIndex
        {
            Records =
            {
                [Id(1)] = new(1, "eligible"),
                [Id(2)] = new(1, "quarantined", ExperienceStatus.Quarantined),
                [Id(3)] = new(1, "revoked", ExperienceStatus.Revoked),
                [Id(4)] = new(1, "low confidence", ExperienceStatus.Validated, ReuseConfidence: 0.1),
            },
        };

        var generator = new FakeEmbeddingGenerator();
        var pass = await new ExperienceIndexingService(index, generator)
            .ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));

        Assert.Equal(1, pass.Examined);
        Assert.Equal([Id(1)], pass.Records.Select(record => record.ExperienceId));
        Assert.Equal(["eligible"], generator.Requests);

        var scan = Assert.Single(index.Scans);
        Assert.Equal([ExperienceStatus.Validated, ExperienceStatus.Reinforced], scan.EligibleStatuses);
        Assert.Equal(RetrievalPolicy.DefaultMinimumConfidence, scan.MinimumConfidence);
    }

    [Fact]
    public void The_indexable_rule_is_exactly_the_one_a_vector_search_applies()
    {
        var service = new ExperienceIndexingService(new FakeEmbeddingIndex(), new FakeEmbeddingGenerator());

        Assert.Equal(ExperienceRetrievalService.EligibleStatuses, ExperienceIndexingService.IndexableStatuses);
        Assert.Equal(RetrievalPolicy.DefaultMinimumConfidence, service.MinimumConfidence);
        Assert.True(service.IsIndexable(ExperienceStatus.Validated, 0.5));
        Assert.True(service.IsIndexable(ExperienceStatus.Reinforced, 1d));
        Assert.False(service.IsIndexable(ExperienceStatus.Validated, 0.49));
        Assert.False(service.IsIndexable(ExperienceStatus.Quarantined, 1d));
        Assert.False(service.IsIndexable(ExperienceStatus.Revoked, 1d));

        // And it tracks whatever floor retrieval was configured with, not a second copy of the default.
        var strict = new ExperienceIndexingService(
            new FakeEmbeddingIndex(),
            new FakeEmbeddingGenerator(),
            RetrievalPolicy.Default with { MinimumConfidence = 0.9 });
        Assert.False(strict.IsIndexable(ExperienceStatus.Validated, 0.8));
    }

    [Fact]
    public async Task A_scope_larger_than_one_page_is_walked_to_the_end_by_the_cursor()
    {
        // Without a cursor every pass re-reads the same first page, so a scope larger than the limit is
        // never fully indexed however often the pass runs.
        var index = new FakeEmbeddingIndex();
        for (var n = 1; n <= 5; n++)
        {
            index.Records[Id(n)] = new(1, $"summary {n}");
        }

        var service = new ExperienceIndexingService(index, new FakeEmbeddingGenerator());

        var first = await service.ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope, Limit: 2));
        Assert.Equal([Id(1), Id(2)], first.Records.Select(record => record.ExperienceId));
        Assert.Equal(Id(2), first.LastExaminedId);

        var second = await service.ReindexAsync(
            Authorization,
            new ReindexExperienceRequest(RequestScope, Limit: 2, StartAfterId: first.LastExaminedId));
        Assert.Equal([Id(3), Id(4)], second.Records.Select(record => record.ExperienceId));

        var third = await service.ReindexAsync(
            Authorization,
            new ReindexExperienceRequest(RequestScope, Limit: 2, StartAfterId: second.LastExaminedId));
        Assert.Equal([Id(5)], third.Records.Select(record => record.ExperienceId));

        var exhausted = await service.ReindexAsync(
            Authorization,
            new ReindexExperienceRequest(RequestScope, Limit: 2, StartAfterId: third.LastExaminedId));
        Assert.Equal(0, exhausted.Examined);
        Assert.Null(exhausted.LastExaminedId);

        Assert.Equal(5, index.Stored.Count);
    }

    // ---------------------------------------------------------------- malformed provider answers

    [Fact]
    public async Task A_vector_with_a_non_finite_component_is_a_provider_failure_and_never_reaches_the_index()
    {
        // Right width, so every later check would pass -- and then the database would reject it, as a
        // failure reported retryable, which is a retry loop that never terminates.
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        var service = new ExperienceIndexingService(index, new FakeEmbeddingGenerator { ReturnNonFinite = true });

        var result = await service.IndexAsync(Authorization, RequestScope, id);

        Assert.Equal(ExperienceIndexingOutcome.ProviderFailed, result.Outcome);
        Assert.Contains("non-finite", result.Failure!.Reason, StringComparison.Ordinal);
        Assert.Empty(index.Writes);
        Assert.Empty(index.Stored);
    }

    [Fact]
    public async Task A_provider_that_cancels_for_its_own_reasons_fails_one_record_not_the_whole_pass()
    {
        // A client-side request timeout is an OperationCanceledException the caller never asked for.
        // Letting it escape would abandon a whole pass with no per-record results at all.
        var index = new FakeEmbeddingIndex
        {
            Records = { [Id(1)] = new(1, "one"), [Id(2)] = new(1, "two") },
        };

        var pass = await new ExperienceIndexingService(
                index,
                new FakeEmbeddingGenerator { Throws = new TaskCanceledException("provider request timeout") })
            .ReindexAsync(Authorization, new ReindexExperienceRequest(RequestScope));

        Assert.Equal(ExperienceReindexOutcome.Completed, pass.Outcome);
        Assert.Equal(2, pass.Examined);
        Assert.Equal(2, pass.Failed);
        Assert.All(pass.Records, record => Assert.Equal(ExperienceIndexingOutcome.ProviderFailed, record.Outcome));
        Assert.All(pass.Records, record => Assert.True(record.IsRetryable));
    }

    // ---------------------------------------------------------------- construction and arguments

    [Fact]
    public void A_generator_that_cannot_say_what_it_is_fails_at_construction_rather_than_mid_query()
    {
        var index = new FakeEmbeddingIndex();

        Assert.Throws<ArgumentNullException>(() => new ExperienceIndexingService(null!, new FakeEmbeddingGenerator()));
        Assert.Throws<ArgumentNullException>(() => new ExperienceIndexingService(index, null!));
        Assert.Throws<ArgumentException>(() => new ExperienceIndexingService(index, new FakeEmbeddingGenerator { ModelId = "  " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceIndexingService(index, new FakeEmbeddingGenerator { Dimension = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExperienceIndexingService(
            index,
            new FakeEmbeddingGenerator { Dimension = ExperienceEmbeddingDescriptor.MaxDimension + 1 }));
    }

    [Fact]
    public async Task Null_and_empty_arguments_throw()
    {
        var service = new ExperienceIndexingService(new FakeEmbeddingIndex(), new FakeEmbeddingGenerator());

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.IndexAsync(null!, RequestScope, Id(1)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.IndexAsync(Authorization, null!, Id(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.IndexAsync(Authorization, RequestScope, Guid.Empty));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ReindexAsync(null!, new ReindexExperienceRequest(RequestScope)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ReindexAsync(Authorization, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ReindexAsync(Authorization, new ReindexExperienceRequest(null!)));
    }

    [Fact]
    public async Task Caller_cancellation_propagates_unwrapped_rather_than_becoming_a_reported_failure()
    {
        var id = Id(1);
        var index = new FakeEmbeddingIndex { Records = { [id] = new(1, Summary) } };
        using var cancellation = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var service = new ExperienceIndexingService(index, new FakeEmbeddingGenerator { Gate = gate });

        var indexing = service.IndexAsync(Authorization, RequestScope, id, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexing);
        Assert.Empty(index.Stored);
    }

    // ---------------------------------------------------------------- the content hash itself

    [Fact]
    public void The_content_hash_covers_the_model_and_the_text_and_nothing_can_collide_across_the_two()
    {
        var a = ExperienceEmbeddingDescriptor.ComputeContentHash("model-a", "text");
        var b = ExperienceEmbeddingDescriptor.ComputeContentHash("model-b", "text");
        var c = ExperienceEmbeddingDescriptor.ComputeContentHash("model-a", "other");

        Assert.Equal(64, a.Length);
        Assert.Equal(a, ExperienceEmbeddingDescriptor.ComputeContentHash("model-a", "text"));
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);

        // The separator cannot occur in either part, so "model-a" + "b|text" cannot hash like
        // "model-ab" + "text".
        Assert.NotEqual(
            ExperienceEmbeddingDescriptor.ComputeContentHash("model", "atext"),
            ExperienceEmbeddingDescriptor.ComputeContentHash("modela", "text"));
        Assert.All(a, character => Assert.Contains(character, "0123456789abcdef"));
    }

    [Fact]
    public void Truncation_never_splits_a_character_or_leaves_a_dangling_separator()
    {
        // A raw cut at a UTF-16 index can land inside a surrogate pair, and UTF-8 encoding silently
        // replaces a lone surrogate with U+FFFD -- so the text that was hashed and the text the
        // provider saw would differ, which is the one thing the content hash exists to rule out.
        // One filler short of the ceiling, so the surrogate pair straddles the cut: it is dropped
        // whole rather than halved.
        var straddling = ExperienceRetrievalSummary.For(
            new string('a', ExperienceRetrievalSummary.MaxLength - 1) + string.Concat(Enumerable.Repeat("\U0001F600", 8)),
            null,
            null);

        Assert.Equal(ExperienceRetrievalSummary.MaxLength - 1, straddling.Length);
        Assert.EndsWith("a", straddling, StringComparison.Ordinal);
        Assert.DoesNotContain(straddling, char.IsSurrogate);

        // Round-tripping through UTF-8 is exactly what the provider and the hash both do; a lone
        // surrogate would come back as U+FFFD and the two would no longer be hashing the same text.
        Assert.Equal(
            straddling,
            System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(straddling)));

        // An even cut needs no backing off, and a whole pair is kept.
        var aligned = ExperienceRetrievalSummary.For(
            new string('a', ExperienceRetrievalSummary.MaxLength - 2) + string.Concat(Enumerable.Repeat("\U0001F600", 8)),
            null,
            null);
        Assert.Equal(ExperienceRetrievalSummary.MaxLength, aligned.Length);
        Assert.Equal(aligned, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(aligned)));

        // And a cut that would have ended on the separator space trims it instead.
        var trailing = ExperienceRetrievalSummary.For(new string('a', ExperienceRetrievalSummary.MaxLength - 1), "b", null);
        Assert.Equal(ExperienceRetrievalSummary.MaxLength - 1, trailing.Length);
        Assert.EndsWith("a", trailing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_control_character_in_the_summary_can_never_collide_with_the_hash_separator()
    {
        // The hash joins the model ID and the summary with U+001F. That is only unambiguous while
        // neither part can contain it, so the summary strips every control character.
        Assert.Equal("model text", ExperienceRetrievalSummary.For("model\u001ftext", null, null));
        Assert.Equal("a b", ExperienceRetrievalSummary.For("a\u0000\u0007\u001fb", null, null));

        Assert.NotEqual(
            ExperienceEmbeddingDescriptor.ComputeContentHash("m", ExperienceRetrievalSummary.For("odel\u001ftext", null, null)),
            ExperienceEmbeddingDescriptor.ComputeContentHash("model", ExperienceRetrievalSummary.For("text", null, null)));
    }

    [Fact]
    public void The_retrieval_summary_is_exactly_the_three_fields_the_text_index_analyzes()
    {
        var record = TestRecord("refund-ticket", "Resolve a refund ticket", "Release the lock first");

        Assert.Equal("refund-ticket Resolve a refund ticket Release the lock first", ExperienceRetrievalSummary.For(record));

        // A record with no reflection contributes no lesson, and a blank field contributes nothing at
        // all rather than a stray separator.
        Assert.Equal("refund-ticket", ExperienceRetrievalSummary.For(TestRecord("refund-ticket", null, null)));
        Assert.Equal("refund-ticket lesson", ExperienceRetrievalSummary.For(TestRecord("refund-ticket", "   ", "lesson")));
        Assert.Equal(ExperienceRetrievalSummary.MaxLength, ExperienceRetrievalSummary.For(new string('a', 20_000), null, null).Length);
    }

    // ---------------------------------------------------------------- helpers

    private static Guid Id(int n) => Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{n:000000000000}"));

    private static ExperienceRecord TestRecord(string taskId, string? taskSummary, string? lesson) => new(
        ExperienceId: Id(1),
        SourceRunId: Guid.NewGuid(),
        Scope: RequestScope,
        TaskId: taskId,
        TaskSummary: taskSummary,
        Attempts: [],
        Outcome: new Outcome(TaskVerificationStatus.Verified, [], "checks passed", Now),
        CompletionScore: 1,
        Reflection: lesson is null
            ? null
            : new Reflection(Guid.NewGuid(), Guid.NewGuid(), lesson, [], [], [], [], null, [], TaskVerificationStatus.Verified, 1, "v1", "tests", Now),
        Environment: new EnvironmentFingerprint("worker-01", "10.0.0", "linux-x64", null, new Dictionary<string, string>()),
        Provenance: new Provenance("tests", null, Now, null),
        Status: ExperienceStatus.Validated,
        ReuseConfidence: 0.5,
        SupportingValidations: 1,
        Contradictions: 0,
        Revision: 1,
        CreatedAt: Now,
        UpdatedAt: Now);
}
