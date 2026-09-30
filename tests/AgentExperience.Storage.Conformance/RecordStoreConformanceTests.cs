using static AgentExperience.Storage.Conformance.ConformanceData;

namespace AgentExperience.Storage.Conformance;

/// <summary>
/// The behaviour every <see cref="IExperienceRecordStore"/> must show, observed through the port alone: create-only
/// records with global ID conflicts, lossless and scope-exact reads that reveal nothing across scopes, the lifecycle
/// commit's idempotency, optimistic revision and prior-status guards (under concurrency too), its supersession
/// guard, ordered and paged history, supersession checks, a batched read that answers exactly as single reads do,
/// the documented refusals (<see cref="ExperienceStoreOutcome.Denied"/>, <see cref="ExperienceStoreOutcome.Invalid"/>)
/// and unwrapped cancellation. A subclass supplies the store through <see cref="CreateStore"/>; see this project's
/// README for the contract and what it leaves out.
/// </summary>
public abstract class RecordStoreConformanceTests
{
    private const int Racers = 16;

    private readonly Lazy<IExperienceRecordStore> _store;

    protected RecordStoreConformanceTests()
    {
        _store = new Lazy<IExperienceRecordStore>(CreateStore);
    }

    /// <summary>The store under test.</summary>
    protected IExperienceRecordStore Store => _store.Value;

    /// <summary>
    /// Creates the store under test. Called at most once per test. Records of other tests may be visible to it;
    /// every test works in a tenant of its own.
    /// </summary>
    protected abstract IExperienceRecordStore CreateStore();

    // ---------------------------------------------------------------- create and read

    [Fact]
    public async Task A_fully_populated_record_reads_back_exactly_as_it_was_created()
    {
        var tenant = NewTenant();
        var record = FullRecord(new Scope(tenant, "app-1", "project-1", "team-1", "agent-1", "user-1"));

        var created = await Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        var read = await Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Created, created.Outcome);
        Assert.Empty(created.Errors);
        Assert.Equal(ExperienceStoreOutcome.Found, read.Outcome);
        Assert.Empty(read.Errors);
        Assert.False(read.SharedByGrant);
        Assert.Null(read.PermittingGrantId);
        Assert.Equal(Json(record), Json(read.Record));
    }

    [Fact]
    public async Task A_provenance_signature_reads_back_byte_for_byte_and_a_record_without_one_reads_back_without_one()
    {
        var tenant = NewTenant();
        var signed = FullRecord(Scope(tenant));
        var unsigned = Record(Scope(tenant));

        await CreateAsync(tenant, signed);
        await CreateAsync(tenant, unsigned);
        var readSigned = (await Store.GetAsync(Authorize(tenant), signed.Scope, signed.ExperienceId, CancellationToken.None)).Record!;
        var readUnsigned = (await Store.GetAsync(Authorize(tenant), unsigned.Scope, unsigned.ExperienceId, CancellationToken.None)).Record!;

        Assert.NotNull(readSigned.ProvenanceSignature);
        Assert.Equal(signed.ProvenanceSignature!.KeyId, readSigned.ProvenanceSignature!.KeyId);
        Assert.Equal(signed.ProvenanceSignature.Algorithm, readSigned.ProvenanceSignature.Algorithm);
        Assert.Equal(signed.ProvenanceSignature.Value.ToArray(), readSigned.ProvenanceSignature.Value.ToArray());
        Assert.Equal(signed.ProvenanceSignature, readSigned.ProvenanceSignature);
        Assert.Equal(ExperienceRecordOrigin.Finalized, readSigned.Origin);
        Assert.Null(readUnsigned.ProvenanceSignature);
    }

    [Fact]
    public async Task Create_is_create_only_and_a_duplicate_id_in_the_same_scope_is_Conflict_with_the_stored_record_unchanged()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant), taskId: "original");
        await CreateAsync(tenant, record);

        var duplicate = await Store.CreateAsync(
            Authorize(tenant), record with { TaskId = "overwritten", Status = ExperienceStatus.Validated }, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, duplicate.Outcome);
        var stored = await Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(Json(record), Json(stored.Record));
    }

    [Fact]
    public async Task A_duplicate_id_in_another_scope_is_Conflict_identically_and_creates_nothing_there()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var record = Record(Scope(tenant), taskId: "original");
        await CreateAsync(tenant, record);

        var sameTenantOtherProject = await Store.CreateAsync(
            Authorize(tenant), record with { Scope = Scope(tenant, project: "project-2") }, CancellationToken.None);
        var otherTenant = await Store.CreateAsync(
            Authorize(foreignTenant), record with { Scope = Scope(foreignTenant) }, CancellationToken.None);
        var sameScope = await Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        // Conflict in any scope, and reported identically wherever the existing record lives.
        Assert.Equal(ExperienceStoreOutcome.Conflict, sameTenantOtherProject.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Conflict, otherTenant.Outcome);
        Assert.Equal(Json(sameScope), Json(sameTenantOtherProject));
        Assert.Equal(Json(sameScope), Json(otherTenant));

        Assert.Equal(
            ExperienceStoreOutcome.NotFound,
            (await Store.GetAsync(Authorize(foreignTenant), Scope(foreignTenant), record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.NotFound,
            (await Store.GetAsync(Authorize(tenant), Scope(tenant, project: "project-2"), record.ExperienceId, CancellationToken.None)).Outcome);
        Assert.Equal(
            "original",
            (await Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!.TaskId);
    }

    [Fact]
    public async Task Concurrent_creates_of_one_id_have_exactly_one_winner()
    {
        var tenant = NewTenant();
        var id = Guid.NewGuid();
        var attempts = Enumerable.Range(0, Racers)
            .Select(i => Record(Scope(tenant), id: id, taskId: $"racer-{i}"))
            .ToArray();

        await WarmUpAsync(tenant, attempts[0]);
        using var start = new ManualResetEventSlim();
        var racing = attempts
            .Select(record => Task.Run(() =>
            {
                start.Wait();
                return Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
            }))
            .ToArray();
        start.Set();
        var results = await Task.WhenAll(racing);

        Assert.Equal(1, results.Count(r => r.Outcome == ExperienceStoreOutcome.Created));
        Assert.Equal(Racers - 1, results.Count(r => r.Outcome == ExperienceStoreOutcome.Conflict));
        var winner = attempts[Array.FindIndex(results, r => r.Outcome == ExperienceStoreOutcome.Created)];
        var stored = await Store.GetAsync(Authorize(tenant), winner.Scope, id, CancellationToken.None);
        Assert.Equal(Json(winner), Json(stored.Record));
    }

    [Fact]
    public async Task A_record_in_another_scope_reads_exactly_like_a_missing_one()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var record = Record(Scope(tenant, team: "team-1"));
        await CreateAsync(tenant, record);
        var foreign = Record(Scope(foreignTenant, team: "team-1"));
        await CreateAsync(foreignTenant, foreign);

        var missing = await Store.GetAsync(Authorize(tenant), record.Scope, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.NotFound, missing.Outcome);
        Assert.Null(missing.Record);

        // Every neighbouring scope, including one that differs only by an optional field or by case.
        foreach (var neighbour in new[]
        {
            Scope(tenant, team: "team-2"),
            Scope(tenant),
            Scope(tenant, project: "project-2", team: "team-1"),
            Scope(tenant, project: "Project-1", team: "team-1"),
        })
        {
            var read = await Store.GetAsync(Authorize(tenant), neighbour, record.ExperienceId, CancellationToken.None);
            Assert.Equal(Json(missing), Json(read));
        }

        // Another tenant's record, asked for from this tenant's own scope.
        var otherTenant = await Store.GetAsync(Authorize(tenant), record.Scope, foreign.ExperienceId, CancellationToken.None);
        Assert.Equal(Json(missing), Json(otherTenant));
    }

    [Fact]
    public async Task A_read_outside_the_authorization_is_Denied()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);

        var read = await Store.GetAsync(Authorize(NewTenant()), record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, read.Outcome);
        Assert.Null(read.Record);
    }

    [Fact]
    public async Task A_create_outside_the_authorization_is_Denied_and_writes_nothing()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));

        var denied = await Store.CreateAsync(Authorize(NewTenant()), record, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
        Assert.Equal(
            ExperienceStoreOutcome.NotFound,
            (await Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
    }

    public static TheoryData<string> MalformedRecords => ["empty-id", "blank-task", "blank-application", "confidence-above-one", "negative-supporting", "negative-contradictions", "negative-revision",
        "signature-empty-value", "signature-oversized-value", "signature-bad-key-id", "signature-bad-algorithm",
        "scope-lone-surrogate"];

    [Theory]
    [MemberData(nameof(MalformedRecords))]
    public async Task A_malformed_record_is_Invalid_with_errors_and_writes_nothing(string malformation)
    {
        var tenant = NewTenant();
        var valid = Record(Scope(tenant));
        var record = malformation switch
        {
            "empty-id" => valid with { ExperienceId = Guid.Empty },
            "blank-task" => valid with { TaskId = " " },
            "blank-application" => valid with { Scope = valid.Scope with { ApplicationId = " " } },
            "confidence-above-one" => valid with { ReuseConfidence = 1.5 },
            "negative-supporting" => valid with { SupportingValidations = -1 },
            "negative-contradictions" => valid with { Contradictions = -1 },
            "signature-empty-value" => valid with { ProvenanceSignature = Signature() with { Value = ReadOnlyMemory<byte>.Empty } },
            "signature-oversized-value" => valid with { ProvenanceSignature = Signature() with { Value = new byte[513] } },
            "signature-bad-key-id" => valid with { ProvenanceSignature = Signature() with { KeyId = "key id/1" } },
            "signature-bad-algorithm" => valid with { ProvenanceSignature = Signature() with { Algorithm = " " } },
            "scope-lone-surrogate" => valid with { Scope = valid.Scope with { TeamId = "team-\uD800" } },
            _ => valid with { Revision = -1 },
        };

        var result = await Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        if (record.ExperienceId != Guid.Empty)
        {
            Assert.Equal(
                ExperienceStoreOutcome.NotFound,
                (await Store.GetAsync(Authorize(tenant), valid.Scope, record.ExperienceId, CancellationToken.None)).Outcome);
        }
    }

    /// <summary>A well-formed provenance signature, for the malformations above to break one part of.</summary>
    private static ExperienceProvenanceSignature Signature() =>
        new("conformance-key-1", ExperienceProvenanceSignature.HmacSha256, new byte[32]);

    [Fact]
    public async Task A_read_of_an_empty_id_is_Invalid()
    {
        var tenant = NewTenant();

        var read = await Store.GetAsync(Authorize(tenant), Scope(tenant), Guid.Empty, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, read.Outcome);
        Assert.NotEmpty(read.Errors);
        Assert.Null(read.Record);
    }

    // ---------------------------------------------------------------- query

    [Fact]
    public async Task Query_returns_only_the_exact_scope_and_the_requested_statuses()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var scope = Scope(tenant);
        var candidate = Record(scope);
        var validated = Record(scope, ExperienceStatus.Validated);
        await CreateAsync(tenant, candidate);
        await CreateAsync(tenant, validated);
        await CreateAsync(tenant, Record(Scope(tenant, team: "team-1"), ExperienceStatus.Validated));
        await CreateAsync(tenant, Record(Scope(tenant, project: "project-2"), ExperienceStatus.Validated));
        await CreateAsync(tenant, Record(Scope(tenant, project: "Project-1"), ExperienceStatus.Validated));
        await CreateAsync(foreignTenant, Record(Scope(foreignTenant), ExperienceStatus.Validated));

        var all = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(scope), CancellationToken.None);
        var onlyValidated = await Store.QueryAsync(
            Authorize(tenant), new ExperienceRecordQuery(scope, [ExperienceStatus.Validated]), CancellationToken.None);
        var caseVariant = await Store.QueryAsync(
            Authorize(tenant), new ExperienceRecordQuery(Scope(tenant, project: "PROJECT-1")), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, all.Outcome);
        Assert.Equal(
            new[] { candidate.ExperienceId, validated.ExperienceId }.Order(),
            all.Records.Select(r => r.ExperienceId).Order());
        Assert.Equal([validated.ExperienceId], onlyValidated.Records.Select(r => r.ExperienceId));
        Assert.Equal(ExperienceStoreOutcome.Found, caseVariant.Outcome);
        Assert.Empty(caseVariant.Records);
    }

    [Fact]
    public async Task Query_returns_the_newest_first_within_its_limit_and_orders_ties_stably()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var oldest = Record(scope, createdAt: Time);
        var middle = Record(scope, createdAt: Time.AddMinutes(1));
        var newest = Record(scope, createdAt: Time.AddMinutes(2));
        var tiedA = Record(Scope(tenant, project: "ties"), createdAt: Time);
        var tiedB = Record(Scope(tenant, project: "ties"), createdAt: Time);
        foreach (var record in new[] { middle, oldest, newest, tiedA, tiedB })
        {
            await CreateAsync(tenant, record);
        }

        var all = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(scope), CancellationToken.None);
        var limited = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(scope, Limit: 2), CancellationToken.None);
        var ties = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(tiedA.Scope), CancellationToken.None);
        var tiesAgain = await Store.QueryAsync(Authorize(tenant), new ExperienceRecordQuery(tiedA.Scope), CancellationToken.None);

        Assert.Equal([newest.ExperienceId, middle.ExperienceId, oldest.ExperienceId], all.Records.Select(r => r.ExperienceId));
        Assert.Equal([newest.ExperienceId, middle.ExperienceId], limited.Records.Select(r => r.ExperienceId));
        Assert.Equal(2, ties.Records.Count);
        Assert.Equal(ties.Records.Select(r => r.ExperienceId), tiesAgain.Records.Select(r => r.ExperienceId));
    }

    [Fact]
    public async Task Query_outside_the_authorization_is_Denied()
    {
        var tenant = NewTenant();
        await CreateAsync(tenant, Record(Scope(tenant)));

        var result = await Store.QueryAsync(Authorize(NewTenant()), new ExperienceRecordQuery(Scope(tenant)), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Records);
    }

    [Theory]
    [InlineData("limit-zero")]
    [InlineData("limit-above-max")]
    [InlineData("empty-statuses")]
    [InlineData("undefined-status")]
    public async Task A_malformed_query_is_Invalid(string malformation)
    {
        var tenant = NewTenant();
        var query = malformation switch
        {
            "limit-zero" => new ExperienceRecordQuery(Scope(tenant), Limit: ExperienceRecordQuery.MinLimit - 1),
            "limit-above-max" => new ExperienceRecordQuery(Scope(tenant), Limit: ExperienceRecordQuery.MaxLimit + 1),
            "empty-statuses" => new ExperienceRecordQuery(Scope(tenant), Statuses: []),
            _ => new ExperienceRecordQuery(Scope(tenant), Statuses: [(ExperienceStatus)999]),
        };

        var result = await Store.QueryAsync(Authorize(tenant), query, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
    }

    // ---------------------------------------------------------------- lifecycle commit

    [Fact]
    public async Task A_commit_sets_the_status_raises_the_revision_to_expected_plus_one_and_moves_UpdatedAt()
    {
        var tenant = NewTenant();
        var record = FullRecord(Scope(tenant)) with { Status = ExperienceStatus.Candidate };
        await CreateAsync(tenant, record);

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
        Assert.Equal(1, result.Revision);
        Assert.Null(result.CurrentStatus);
        Assert.Empty(result.Errors);

        var stored = (await Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(ExperienceStatus.Validated, stored.Status);
        Assert.Equal(1, stored.Revision);
        Assert.True(stored.UpdatedAt > record.UpdatedAt, "A commit changes the record, so its UpdatedAt must move forward.");

        // Nothing else about the record moves: the store persists the decision it was given.
        Assert.Equal(
            Json(record with { Status = ExperienceStatus.Validated, Revision = 1, UpdatedAt = stored.UpdatedAt }),
            Json(stored));
    }

    [Fact]
    public async Task Replaying_an_identical_event_returns_the_original_revision_and_writes_nothing()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);
        var first = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        var second = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 1);

        var committed = await Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, first, CancellationToken.None);
        var replay = await Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, first, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, committed.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);
        Assert.Equal(committed.Revision, replay.Revision);

        // An event with no confidence payload replays with no CurrentStatus: the status it moved the record to is the
        // one it carries, which the caller already holds.
        Assert.Null(replay.CurrentStatus);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Validated, revision: 1, events: 1);

        // After a later commit, the replay still reports its own revision, not the record's current one, and still no
        // CurrentStatus: in particular not the status the record has moved on to.
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, second, CancellationToken.None)).Outcome);
        var lateReplay = await Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, first, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, lateReplay.Outcome);
        Assert.Equal(1, lateReplay.Revision);
        Assert.Null(lateReplay.CurrentStatus);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Reinforced, revision: 2, events: 2);
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("producer")]
    [InlineData("current")]
    [InlineData("prior")]
    [InlineData("revision")]
    [InlineData("occurred")]
    [InlineData("record")]
    [InlineData("scope")]
    [InlineData("confidence")]
    public async Task The_same_event_id_with_different_content_is_Conflict_and_writes_nothing(string difference)
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        var other = Record(Scope(tenant, project: "project-2"));
        await CreateAsync(tenant, record);
        await CreateAsync(tenant, other);

        var original = Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, original, CancellationToken.None)).Outcome);

        var scope = difference == "scope" ? other.Scope : record.Scope;
        var diverged = difference switch
        {
            "reason" => original with { Reason = original.Reason + "!" },
            "producer" => original with { Producer = "someone-else" },
            "current" => original with { CurrentStatus = ExperienceStatus.Revoked },
            "prior" => original with { PriorStatus = null },
            "revision" => original with { ExpectedRevision = 1 },
            "occurred" => original with { OccurredAt = original.OccurredAt.AddSeconds(1) },
            "record" => original with { ExperienceRecordId = other.ExperienceId },
            "confidence" => original with { Confidence = SupportingConfidence() },
            _ => original,
        };

        var result = await Store.CommitLifecycleEventAsync(Authorize(tenant), scope, diverged, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, result.Outcome);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Validated, revision: 1, events: 1);
        await AssertStoredAsync(tenant, other, ExperienceStatus.Candidate, revision: 0, events: 0);
        var stored = (await Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None)).Record!;
        Assert.Equal(0, stored.SupportingValidations);
        Assert.Equal(record.ReuseConfidence, stored.ReuseConfidence);
    }

    [Fact]
    public async Task Replaying_a_supersession_with_a_different_replacement_is_Conflict_and_writes_nothing()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var first = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var second = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var original = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 0, replacement: first.ExperienceId);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await Store.CommitLifecycleEventAsync(Authorize(tenant), scope, original, CancellationToken.None)).Outcome);

        var diverged = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), scope, original with { ReplacementExperienceId = second.ExperienceId }, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, diverged.Outcome);
        var history = await Store.GetFirstHistoryPageAsync(Authorize(tenant), scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(first.ExperienceId, Assert.Single(history.Events).Event.ReplacementExperienceId);
    }

    [Fact]
    public async Task An_event_id_already_stored_in_another_tenant_is_Conflict_and_writes_nothing()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var record = Record(Scope(tenant));
        var foreign = Record(Scope(foreignTenant));
        await CreateAsync(tenant, record);
        await CreateAsync(foreignTenant, foreign);
        var eventId = Guid.NewGuid();
        await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0, eventId), CancellationToken.None);

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(foreignTenant), foreign.Scope, Event(foreign.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0, eventId), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Conflict, result.Outcome);
        await AssertStoredAsync(foreignTenant, foreign, ExperienceStatus.Candidate, revision: 0, events: 0);
    }

    [Fact]
    public async Task An_expected_revision_behind_or_ahead_is_StaleRevision_reports_the_current_revision_and_writes_nothing()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);
        await CommitAsync(tenant, record, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0);

        // Only the revision is wrong: the prior status is the one the record is really in.
        var behind = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 0), CancellationToken.None);
        var ahead = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 5), CancellationToken.None);

        Assert.All([behind, ahead], result =>
        {
            Assert.Equal(ExperienceStoreOutcome.StaleRevision, result.Outcome);
            Assert.Equal(1, result.Revision);
        });
        await AssertStoredAsync(tenant, record, ExperienceStatus.Validated, revision: 1, events: 1);
    }

    [Fact]
    public async Task Concurrent_commits_from_the_same_revision_have_exactly_one_winner()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);

        // Every racer is right about the status and none moves it, so a loser can only be wrong about the revision.
        var racers = Enumerable.Range(0, Racers)
            .Select(i => Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Candidate, 0, reason: $"racer {i}"))
            .ToArray();

        await WarmUpAsync(tenant, record);
        using var start = new ManualResetEventSlim();
        var racing = racers
            .Select(lifecycleEvent => Task.Run(() =>
            {
                start.Wait();
                return Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, lifecycleEvent, CancellationToken.None);
            }))
            .ToArray();
        start.Set();
        var results = await Task.WhenAll(racing);

        Assert.Equal(1, results.Count(r => r.Outcome == ExperienceStoreOutcome.Committed));
        Assert.All(
            results.Where(r => r.Outcome != ExperienceStoreOutcome.Committed),
            loser =>
            {
                Assert.Equal(ExperienceStoreOutcome.StaleRevision, loser.Outcome);
                Assert.Equal(1, loser.Revision);
            });
        await AssertStoredAsync(tenant, record, ExperienceStatus.Candidate, revision: 1, events: 1);
    }

    [Fact]
    public async Task A_prior_status_the_record_is_not_in_is_StatusMismatch_reports_the_stored_status_and_writes_nothing()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);
        await CommitAsync(tenant, record, ExperienceStatus.Candidate, ExperienceStatus.Quarantined, 0);

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 1), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.StatusMismatch, result.Outcome);
        Assert.Equal(ExperienceStatus.Quarantined, result.CurrentStatus);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Quarantined, revision: 1, events: 1);
    }

    [Fact]
    public async Task A_null_prior_status_falls_back_to_the_current_status_and_is_no_way_past_the_guard()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);

        var elsewhere = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, null, ExperienceStatus.Validated, 0), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.StatusMismatch, elsewhere.Outcome);
        Assert.Equal(ExperienceStatus.Candidate, elsewhere.CurrentStatus);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Candidate, revision: 0, events: 0);

        var inPlace = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, null, ExperienceStatus.Candidate, 0), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, inPlace.Outcome);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Candidate, revision: 1, events: 1);
    }

    [Fact]
    public async Task A_commit_against_a_missing_record_or_one_in_another_scope_is_NotFound_and_writes_nothing()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant, team: "team-1"));
        await CreateAsync(tenant, record);

        var missing = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(Guid.NewGuid(), ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);
        var otherTeam = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), Scope(tenant, team: "team-2"), Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);
        var noTeam = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), Scope(tenant), Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Revoked, 0), CancellationToken.None);

        Assert.All([missing, otherTeam, noTeam], result => Assert.Equal(ExperienceStoreOutcome.NotFound, result.Outcome));
        await AssertStoredAsync(tenant, record, ExperienceStatus.Candidate, revision: 0, events: 0);
    }

    [Fact]
    public async Task A_commit_outside_the_authorization_is_Denied_and_writes_nothing()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(NewTenant()), record.Scope, Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Candidate, revision: 0, events: 0);
    }

    [Theory]
    [InlineData("superseded-without-replacement")]
    [InlineData("replacement-on-another-status")]
    public async Task A_malformed_event_is_Invalid_and_writes_nothing(string malformation)
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));
        var replacement = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));
        var lifecycleEvent = malformation == "superseded-without-replacement"
            ? Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 0)
            : Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Revoked, 0, replacement: replacement.ExperienceId);

        var result = await Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, lifecycleEvent, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Validated, revision: 0, events: 0);
    }

    // ---------------------------------------------------------------- supersession guard on commit

    public static TheoryData<string> RefusedReplacements => ["other-scope", "missing", "candidate", "revoked", "cycle"];

    [Theory]
    [MemberData(nameof(RefusedReplacements))]
    public async Task A_superseding_commit_naming_an_unacceptable_replacement_is_ReplacementNotAllowed_and_writes_nothing(string replacementKind)
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var expectedRevision = 0L;
        var expectedStatus = ExperienceStatus.Validated;
        Guid replacement;
        ExperienceStatus? reportedStatus;
        switch (replacementKind)
        {
            case "other-scope":
                replacement = (await CreateAsync(tenant, Record(Scope(tenant, project: "project-2"), ExperienceStatus.Validated))).ExperienceId;
                reportedStatus = null;
                break;
            case "missing":
                replacement = Guid.NewGuid();
                reportedStatus = null;
                break;
            case "candidate":
                replacement = (await CreateAsync(tenant, Record(scope, ExperienceStatus.Candidate))).ExperienceId;
                reportedStatus = ExperienceStatus.Candidate;
                break;
            case "revoked":
                replacement = (await CreateAsync(tenant, Record(scope, ExperienceStatus.Revoked))).ExperienceId;
                reportedStatus = ExperienceStatus.Revoked;
                break;
            default:
                // The record already replaces this one (other -> record), so record -> other would close a loop.
                var other = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
                await SupersedeAsync(tenant, other, record.ExperienceId);
                replacement = other.ExperienceId;
                reportedStatus = ExperienceStatus.Superseded;
                break;
        }

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant),
            scope,
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, expectedRevision, replacement: replacement),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.ReplacementNotAllowed, result.Outcome);
        Assert.Equal(reportedStatus, result.CurrentStatus);
        await AssertStoredAsync(tenant, record, expectedStatus, revision: 0, events: 0);
    }

    [Fact]
    public async Task A_record_naming_itself_as_its_replacement_is_refused_and_writes_nothing()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));

        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant),
            record.Scope,
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 0, replacement: record.ExperienceId),
            CancellationToken.None);

        // Refused either as malformed or by the supersession guard; the port does not fix which.
        Assert.Contains(result.Outcome, new[] { ExperienceStoreOutcome.Invalid, ExperienceStoreOutcome.ReplacementNotAllowed });
        await AssertStoredAsync(tenant, record, ExperienceStatus.Validated, revision: 0, events: 0);
    }

    [Fact]
    public async Task Retrying_a_committed_supersession_reports_the_original_commit_even_after_the_replacement_moves_on()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var replacement = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var supersede = Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 0, replacement: replacement.ExperienceId);
        Assert.Equal(
            ExperienceStoreOutcome.Committed,
            (await Store.CommitLifecycleEventAsync(Authorize(tenant), scope, supersede, CancellationToken.None)).Outcome);

        // The replacement leaves eligibility; a retry of the identical event is still a replay, not a new supersession.
        await CommitAsync(tenant, replacement, ExperienceStatus.Validated, ExperienceStatus.Revoked, 0);
        var replay = await Store.CommitLifecycleEventAsync(Authorize(tenant), scope, supersede, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Committed, replay.Outcome);
        Assert.Equal(1, replay.Revision);
        await AssertStoredAsync(tenant, record, ExperienceStatus.Superseded, revision: 1, events: 1);
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public async Task History_lists_every_committed_event_exactly_oldest_first_with_the_record_s_current_revision()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);
        var steps = new[]
        {
            Event(record.ExperienceId, ExperienceStatus.Candidate, ExperienceStatus.Validated, 0),
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Quarantined, 1, reason: "suspected"),
            Event(record.ExperienceId, ExperienceStatus.Quarantined, ExperienceStatus.Revoked, 2, reason: "withdrawn"),
        };
        foreach (var step in steps)
        {
            Assert.Equal(
                ExperienceStoreOutcome.Committed,
                (await Store.CommitLifecycleEventAsync(Authorize(tenant), record.Scope, step, CancellationToken.None)).Outcome);
        }

        var history = await Store.GetFirstHistoryPageAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(3, history.Revision);
        Assert.Equal(steps.Select(Json), history.Events.Select(e => Json(e.Event)));
        Assert.Equal([1L, 2L, 3L], history.Events.Select(e => e.AppliedRevision));
        Assert.Equal(3, history.NextStartAfterRevision);
    }

    [Fact]
    public async Task History_respects_its_limit_and_cursor_and_a_page_past_the_end_is_Found_and_empty()
    {
        var tenant = NewTenant();
        var record = Record(Scope(tenant));
        await CreateAsync(tenant, record);
        await CommitAsync(tenant, record, null, ExperienceStatus.Candidate, 0);
        await CommitAsync(tenant, record, ExperienceStatus.Candidate, ExperienceStatus.Validated, 1);
        await CommitAsync(tenant, record, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 2);
        await CommitAsync(tenant, record, ExperienceStatus.Reinforced, ExperienceStatus.Stale, 3);
        await CommitAsync(tenant, record, ExperienceStatus.Stale, ExperienceStatus.Revoked, 4);

        var walked = new List<StoredLifecycleEvent>();
        long? cursor = null;
        var pages = 0;
        while (true)
        {
            Assert.True(++pages <= 10, "The history cursor never reached the end.");
            var page = await Store.GetHistoryAsync(
                Authorize(tenant),
                new ExperienceRecordHistoryQuery(record.Scope, record.ExperienceId, Limit: 2, StartAfterRevision: cursor),
                CancellationToken.None);

            Assert.Equal(ExperienceStoreOutcome.Found, page.Outcome);
            Assert.Equal(5, page.Revision);
            Assert.InRange(page.Events.Count, 0, 2);
            if (page.Events.Count == 0)
            {
                Assert.Null(page.NextStartAfterRevision);
                break;
            }

            walked.AddRange(page.Events);
            Assert.Equal(page.Events[^1].AppliedRevision, page.NextStartAfterRevision);
            cursor = page.NextStartAfterRevision;
        }

        Assert.Equal([1L, 2L, 3L, 4L, 5L], walked.Select(e => e.AppliedRevision));
        Assert.Equal(5, walked.Select(e => e.Event.EventId).Distinct().Count());

        var pastTheEnd = await Store.GetHistoryAsync(
            Authorize(tenant), new ExperienceRecordHistoryQuery(record.Scope, record.ExperienceId, StartAfterRevision: 999), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, pastTheEnd.Outcome);
        Assert.Equal(5, pastTheEnd.Revision);
        Assert.Empty(pastTheEnd.Events);
        Assert.Null(pastTheEnd.NextStartAfterRevision);
    }

    [Fact]
    public async Task History_of_a_record_with_no_events_is_Found_and_one_in_another_scope_is_NotFound_like_a_missing_one()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var record = Record(Scope(tenant, team: "team-1"));
        await CreateAsync(tenant, record);
        var foreign = Record(Scope(foreignTenant, team: "team-1"));
        await CreateAsync(foreignTenant, foreign);

        var empty = await Store.GetFirstHistoryPageAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        var missing = await Store.GetFirstHistoryPageAsync(Authorize(tenant), record.Scope, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, empty.Outcome);
        Assert.Equal(0, empty.Revision);
        Assert.Empty(empty.Events);
        Assert.Null(empty.NextStartAfterRevision);

        Assert.Equal(ExperienceStoreOutcome.NotFound, missing.Outcome);
        Assert.Equal(0, missing.Revision);
        Assert.Empty(missing.Events);
        foreach (var (scope, id) in new[]
        {
            (Scope(tenant, team: "team-2"), record.ExperienceId),
            (Scope(tenant, project: "Project-1", team: "team-1"), record.ExperienceId),
            (record.Scope, foreign.ExperienceId),
        })
        {
            Assert.Equal(Json(missing), Json(await Store.GetFirstHistoryPageAsync(Authorize(tenant), scope, id, CancellationToken.None)));
        }
    }

    [Fact]
    public async Task History_outside_the_authorization_is_Denied()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant)));

        var result = await Store.GetFirstHistoryPageAsync(Authorize(NewTenant()), record.Scope, record.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, result.Outcome);
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData("limit-zero")]
    [InlineData("limit-above-max")]
    [InlineData("empty-id")]
    public async Task A_malformed_history_request_is_Invalid(string malformation)
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant)));
        var query = malformation switch
        {
            "limit-zero" => new ExperienceRecordHistoryQuery(record.Scope, record.ExperienceId, Limit: ExperienceRecordHistoryQuery.MinLimit - 1),
            "limit-above-max" => new ExperienceRecordHistoryQuery(record.Scope, record.ExperienceId, Limit: ExperienceRecordHistoryQuery.MaxLimit + 1),
            _ => new ExperienceRecordHistoryQuery(record.Scope, Guid.Empty),
        };

        var result = await Store.GetHistoryAsync(Authorize(tenant), query, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Invalid, result.Outcome);
        Assert.NotEmpty(result.Errors);
    }

    // ---------------------------------------------------------------- supersession check

    [Fact]
    public async Task A_supersession_check_allows_an_unrelated_replacement_and_reports_its_status_without_deciding_eligibility()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var reinforced = await CreateAsync(tenant, Record(scope, ExperienceStatus.Reinforced));
        var revoked = await CreateAsync(tenant, Record(scope, ExperienceStatus.Revoked));

        var eligible = await Store.CheckSupersessionAsync(Authorize(tenant), scope, record.ExperienceId, reinforced.ExperienceId, CancellationToken.None);
        var ineligible = await Store.CheckSupersessionAsync(Authorize(tenant), scope, record.ExperienceId, revoked.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.Allowed, eligible.Outcome);
        Assert.Equal(ExperienceStatus.Reinforced, eligible.ReplacementStatus);

        // Eligibility is Core's rule: the check reports the status and decides nothing about it.
        Assert.Equal(ExperienceSupersessionOutcome.Allowed, ineligible.Outcome);
        Assert.Equal(ExperienceStatus.Revoked, ineligible.ReplacementStatus);
    }

    [Fact]
    public async Task A_supersession_check_walks_the_replacement_chain_directly_and_transitively()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var a = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var b = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var c = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var unrelated = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));

        // a is replaced by b, then b by c: the chain runs a -> b -> c.
        await SupersedeAsync(tenant, a, b.ExperienceId);
        await SupersedeAsync(tenant, b, c.ExperienceId);

        var direct = await Store.CheckSupersessionAsync(Authorize(tenant), scope, b.ExperienceId, a.ExperienceId, CancellationToken.None);
        var transitive = await Store.CheckSupersessionAsync(Authorize(tenant), scope, c.ExperienceId, a.ExperienceId, CancellationToken.None);
        var allowed = await Store.CheckSupersessionAsync(Authorize(tenant), scope, unrelated.ExperienceId, c.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.Cycle, direct.Outcome);
        Assert.Equal(ExperienceSupersessionOutcome.Cycle, transitive.Outcome);

        // The replacement was found in scope, so its status is reported even on a cycle.
        Assert.Equal(ExperienceStatus.Superseded, direct.ReplacementStatus);
        Assert.Equal(ExperienceStatus.Superseded, transitive.ReplacementStatus);
        Assert.Equal(ExperienceSupersessionOutcome.Allowed, allowed.Outcome);
    }

    [Fact]
    public async Task A_supersession_check_never_allows_a_record_to_replace_itself()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant), ExperienceStatus.Validated));

        var check = await Store.CheckSupersessionAsync(Authorize(tenant), record.Scope, record.ExperienceId, record.ExperienceId, CancellationToken.None);

        // A record replacing itself is the shortest cycle there is. The port names no outcome for it beyond that, so
        // only the one answer that would be wrong is ruled out.
        Assert.NotEqual(ExperienceSupersessionOutcome.Allowed, check.Outcome);
    }

    [Fact]
    public async Task A_record_or_replacement_in_another_scope_is_not_found_exactly_like_a_missing_one()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var scope = Scope(tenant);
        var record = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var otherProject = await CreateAsync(tenant, Record(Scope(tenant, project: "project-2"), ExperienceStatus.Validated));
        var otherTenant = await CreateAsync(foreignTenant, Record(Scope(foreignTenant), ExperienceStatus.Validated));

        var missing = await Store.CheckSupersessionAsync(Authorize(tenant), scope, record.ExperienceId, Guid.NewGuid(), CancellationToken.None);
        var crossProject = await Store.CheckSupersessionAsync(Authorize(tenant), scope, record.ExperienceId, otherProject.ExperienceId, CancellationToken.None);
        var crossTenant = await Store.CheckSupersessionAsync(Authorize(tenant), scope, record.ExperienceId, otherTenant.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.ReplacementNotFound, missing.Outcome);
        Assert.Null(missing.ReplacementStatus);
        Assert.Equal(Json(missing), Json(crossProject));
        Assert.Equal(Json(missing), Json(crossTenant));

        var missingRecord = await Store.CheckSupersessionAsync(Authorize(tenant), scope, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var recordInOtherProject = await Store.CheckSupersessionAsync(Authorize(tenant), scope, otherProject.ExperienceId, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.RecordNotFound, missingRecord.Outcome);
        Assert.Null(missingRecord.ReplacementStatus);
        Assert.Equal(Json(missingRecord), Json(recordInOtherProject));

        // With no record there is nothing to supersede, so no replacement status is reported, even for a replacement
        // that is in scope -- whether the record is missing or in another scope.
        var replacementInScope = await CreateAsync(tenant, Record(scope, ExperienceStatus.Reinforced));
        var missingRecordRealReplacement = await Store.CheckSupersessionAsync(
            Authorize(tenant), scope, Guid.NewGuid(), replacementInScope.ExperienceId, CancellationToken.None);
        var foreignRecordRealReplacement = await Store.CheckSupersessionAsync(
            Authorize(tenant), scope, otherProject.ExperienceId, replacementInScope.ExperienceId, CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.RecordNotFound, missingRecordRealReplacement.Outcome);
        Assert.Null(missingRecordRealReplacement.ReplacementStatus);
        Assert.Equal(Json(missingRecordRealReplacement), Json(foreignRecordRealReplacement));
    }

    [Fact]
    public async Task A_supersession_check_outside_the_authorization_is_Denied_and_an_empty_id_is_Invalid()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        var record = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var replacement = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));

        var denied = await Store.CheckSupersessionAsync(Authorize(NewTenant()), scope, record.ExperienceId, replacement.ExperienceId, CancellationToken.None);
        var emptyRecord = await Store.CheckSupersessionAsync(Authorize(tenant), scope, Guid.Empty, replacement.ExperienceId, CancellationToken.None);
        var emptyReplacement = await Store.CheckSupersessionAsync(Authorize(tenant), scope, record.ExperienceId, Guid.Empty, CancellationToken.None);

        Assert.Equal(ExperienceSupersessionOutcome.Denied, denied.Outcome);
        Assert.Null(denied.ReplacementStatus);
        Assert.All([emptyRecord, emptyReplacement], result =>
        {
            Assert.Equal(ExperienceSupersessionOutcome.Invalid, result.Outcome);
            Assert.NotEmpty(result.Errors);
        });
    }

    // ---------------------------------------------------------------- batched read

    [Fact]
    public async Task GetMany_answers_every_position_exactly_as_its_own_GetAsync_does()
    {
        var tenant = NewTenant();
        var foreignTenant = NewTenant();
        var scope = Scope(tenant);
        var mine = await CreateAsync(tenant, FullRecord(scope));
        var revoked = await CreateAsync(tenant, Record(scope, ExperienceStatus.Revoked));
        var otherTeam = await CreateAsync(tenant, Record(Scope(tenant, team: "team-1"), ExperienceStatus.Validated));
        var otherTenant = await CreateAsync(foreignTenant, Record(Scope(foreignTenant), ExperienceStatus.Validated));

        IReadOnlyList<Guid> ids =
        [
            mine.ExperienceId,
            Guid.NewGuid(),
            otherTeam.ExperienceId,
            revoked.ExperienceId,
            otherTenant.ExperienceId,
            mine.ExperienceId,
            Guid.Empty,
        ];
        var options = new ExperienceReadOptions();

        var batch = await Store.GetManyAsync(Authorize(tenant), scope, ids, options, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Found, batch.Outcome);
        Assert.Equal(ids.Count, batch.Results.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var single = await Store.GetAsync(Authorize(tenant), scope, ids[i], options, CancellationToken.None);
            Assert.Equal(Json(single), Json(batch.Results[i]));
        }

        // And the answers are the right ones, so the two paths are not merely equally wrong.
        Assert.Equal(
            [
                ExperienceStoreOutcome.Found,
                ExperienceStoreOutcome.NotFound,
                ExperienceStoreOutcome.NotFound,
                ExperienceStoreOutcome.Found,
                ExperienceStoreOutcome.NotFound,
                ExperienceStoreOutcome.Found,
                ExperienceStoreOutcome.Invalid,
            ],
            batch.Results.Select(r => r.Outcome));
        Assert.Equal(Json(mine), Json(batch.Results[0].Record));

        var empty = await Store.GetManyAsync(Authorize(tenant), scope, [], options, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, empty.Outcome);
        Assert.Empty(empty.Results);
    }

    [Fact]
    public async Task GetMany_outside_the_authorization_is_Denied_and_over_its_maximum_is_Invalid()
    {
        var tenant = NewTenant();
        var record = await CreateAsync(tenant, Record(Scope(tenant)));

        var denied = await Store.GetManyAsync(Authorize(NewTenant()), record.Scope, [record.ExperienceId], new ExperienceReadOptions(), CancellationToken.None);
        var tooMany = await Store.GetManyAsync(
            Authorize(tenant),
            record.Scope,
            [.. Enumerable.Range(0, ExperienceRecordGetManyResult.MaxCount + 1).Select(_ => Guid.NewGuid())],
            new ExperienceReadOptions(),
            CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
        Assert.Empty(denied.Results);
        Assert.Equal(ExperienceStoreOutcome.Invalid, tooMany.Outcome);
        Assert.Empty(tooMany.Results);
        Assert.NotEmpty(tooMany.Errors);
    }

    // ---------------------------------------------------------------- cancellation

    [Fact]
    public async Task Every_operation_called_with_a_cancelled_token_throws_an_unwrapped_OperationCanceledException()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var record = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        var replacement = await CreateAsync(tenant, Record(scope, ExperienceStatus.Validated));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var token = cancelled.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.CreateAsync(auth, Record(scope), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.GetAsync(auth, scope, record.ExperienceId, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.GetManyAsync(auth, scope, [record.ExperienceId], new ExperienceReadOptions(), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.QueryAsync(auth, new ExperienceRecordQuery(scope), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.CommitLifecycleEventAsync(
            auth, scope, Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Reinforced, 0), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.GetHistoryAsync(auth, new ExperienceRecordHistoryQuery(scope, record.ExperienceId), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.CheckSupersessionAsync(auth, scope, record.ExperienceId, replacement.ExperienceId, token));

        await AssertStoredAsync(tenant, record, ExperienceStatus.Validated, revision: 0, events: 0);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Runs as many concurrent reads as there will be racers, so a store that pools resources (connections, say) has
    /// them ready and the racers really overlap instead of queueing behind resource creation.
    /// </summary>
    private async Task WarmUpAsync(string tenant, ExperienceRecord record) =>
        await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ =>
            Task.Run(() => Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None))));

    private async Task<ExperienceRecord> CreateAsync(string tenant, ExperienceRecord record)
    {
        var created = await Store.CreateAsync(Authorize(tenant), record, CancellationToken.None);
        Assert.True(
            created.Outcome == ExperienceStoreOutcome.Created,
            $"Seeding a record returned {created.Outcome}: {string.Join("; ", created.Errors.Select(e => $"{e.Path}: {e.Message}"))}");
        return record;
    }

    private async Task CommitAsync(string tenant, ExperienceRecord record, ExperienceStatus? prior, ExperienceStatus current, long expectedRevision)
    {
        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant), record.Scope, Event(record.ExperienceId, prior, current, expectedRevision), CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
    }

    private async Task SupersedeAsync(string tenant, ExperienceRecord record, Guid replacement)
    {
        var result = await Store.CommitLifecycleEventAsync(
            Authorize(tenant),
            record.Scope,
            Event(record.ExperienceId, ExperienceStatus.Validated, ExperienceStatus.Superseded, 0, replacement: replacement),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Committed, result.Outcome);
    }

    /// <summary>What the port reports about <paramref name="record"/>: its status, revision, and event count.</summary>
    private async Task AssertStoredAsync(string tenant, ExperienceRecord record, ExperienceStatus status, long revision, int events)
    {
        var stored = await Store.GetAsync(Authorize(tenant), record.Scope, record.ExperienceId, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, stored.Outcome);
        Assert.Equal(status, stored.Record!.Status);
        Assert.Equal(revision, stored.Record.Revision);

        var history = await Store.GetHistoryAsync(
            Authorize(tenant),
            new ExperienceRecordHistoryQuery(record.Scope, record.ExperienceId, ExperienceRecordHistoryQuery.MaxLimit),
            CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Found, history.Outcome);
        Assert.Equal(revision, history.Revision);
        Assert.Equal(events, history.Events.Count);
    }
}
