namespace AgentExperience.Abstractions.Tests;

/// <summary>
/// Story 5.6: the port's default <see cref="IExperienceRecordStore.GetManyAsync"/>, which every store that
/// does not override it -- the 4.2 sample's in-memory store, any out-of-tree store -- answers through.
/// </summary>
public class GetManyDefaultTests
{
    private static readonly Scope Scope = new("tenant-1", "app-1", "project-1");

    private static readonly AuthorizationContext Authorization = new("tenant-1", "host", ["experience:read"], DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task The_default_reads_each_position_in_order_through_the_single_read_with_the_same_options()
    {
        var store = new CountingStore();
        IExperienceRecordStore port = store;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var options = new ExperienceReadOptions(ExperienceReadPurpose.ScopeCheck, "corr-1");

        var result = await port.GetManyAsync(Authorization, Scope, [first, second, first], options, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, result.Outcome);
        Assert.Equal([first, second, first], store.Reads.Select(read => read.Id));
        Assert.All(store.Reads, read => Assert.Same(options, read.Options));
        Assert.Equal([first, second, first], result.Results.Select(r => r.Record!.ExperienceId));
    }

    [Fact]
    public async Task Too_many_ids_are_refused_as_a_whole_before_any_read()
    {
        var store = new CountingStore();
        IExperienceRecordStore port = store;

        var result = await port.GetManyAsync(
            Authorization,
            Scope,
            Enumerable.Range(0, ExperienceRecordGetManyResult.MaxCount + 1).Select(_ => Guid.NewGuid()).ToArray(),
            new ExperienceReadOptions(),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.Equal("ExperienceIds", Assert.Single(result.Errors).Path);
        Assert.Empty(result.Results);
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task A_scope_outside_the_authorization_is_denied_as_a_whole_before_any_read()
    {
        var store = new CountingStore();
        IExperienceRecordStore port = store;

        var result = await port.GetManyAsync(
            Authorization, Scope with { TenantId = "tenant-2" }, [Guid.NewGuid()], new ExperienceReadOptions(), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Results);
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task A_read_that_throws_ends_the_whole_call_with_that_exception()
    {
        var store = new CountingStore { ThrowOn = 1 };
        IExperienceRecordStore port = store;

        await Assert.ThrowsAsync<ExperienceStoreException>(() => port.GetManyAsync(
            Authorization, Scope, [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], new ExperienceReadOptions(), CancellationToken.None));
        Assert.Equal(2, store.Reads.Count);
    }

    /// <summary>A store that implements only the single read, as every store written before story 5.6 does.</summary>
    private sealed class CountingStore : IExperienceRecordStore
    {
        public List<(Guid Id, ExperienceReadOptions Options)> Reads { get; } = [];

        public int? ThrowOn { get; init; }

        public Task<ExperienceRecordGetResult> GetAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, CancellationToken cancellationToken) =>
            GetAsync(authorization, scope, experienceId, new ExperienceReadOptions(), cancellationToken);

        public Task<ExperienceRecordGetResult> GetAsync(
            AuthorizationContext authorization,
            Scope scope,
            Guid experienceId,
            ExperienceReadOptions options,
            CancellationToken cancellationToken)
        {
            Reads.Add((experienceId, options));
            if (Reads.Count - 1 == ThrowOn)
            {
                throw new ExperienceStoreException("down");
            }

            return Task.FromResult(new ExperienceRecordGetResult(ExperienceStoreOutcome.Found, Record(experienceId), []));
        }

        public Task<ExperienceRecordCreateResult> CreateAsync(AuthorizationContext authorization, ExperienceRecord record, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRecordQueryResult> QueryAsync(AuthorizationContext authorization, ExperienceRecordQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceLifecycleCommitResult> CommitLifecycleEventAsync(AuthorizationContext authorization, Scope scope, LifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceRecordHistoryResult> GetHistoryAsync(AuthorizationContext authorization, ExperienceRecordHistoryQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExperienceSupersessionCheckResult> CheckSupersessionAsync(AuthorizationContext authorization, Scope scope, Guid experienceId, Guid replacementExperienceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static ExperienceRecord Record(Guid id) => new(
            id,
            Guid.NewGuid(),
            Scope,
            "task",
            null,
            [],
            new Outcome(TaskVerificationStatus.Unknown, [], null, DateTimeOffset.UnixEpoch),
            0,
            null,
            new EnvironmentFingerprint("host", "10", "os", null, new Dictionary<string, string>()),
            new Provenance("tests", null, DateTimeOffset.UnixEpoch, null),
            ExperienceStatus.Validated,
            0,
            0,
            0,
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }
}
