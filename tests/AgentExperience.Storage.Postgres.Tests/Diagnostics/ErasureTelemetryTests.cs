using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests.Diagnostics;

/// <summary>
/// Story 5.2 (KL-16): what a host subscribed to <c>AgentExperience.*</c> receives when it erases a
/// record, sweeps a scope for retention, or purges expired grants. The contract is the one the 4.1
/// operations already keep: one span and one count/duration pair per call under the result's own
/// outcome name, a failure counter only a throw moves, four metric dimensions and no more, and --
/// the point for an erasure in particular -- nothing that was erased anywhere in any of them.
/// </summary>
/// <remarks>
/// Listeners are process-wide and <c>PostgresDeletionTests</c> erases concurrently in the shared
/// collection, so this runs in its own non-parallel collection with its own container. Every assertion
/// is made against what the listener collected, which is what an exporter would have been handed.
/// </remarks>
[Collection(PostgresTelemetryCollection.Name)]
public sealed class ErasureTelemetryTests(PostgresFixture fixture)
{
    private const string Source = "AgentExperience.Storage.Postgres";

    private const string CountInstrument = "agentexperience.operation.count";
    private const string DurationInstrument = "agentexperience.operation.duration";
    private const string FailuresInstrument = "agentexperience.operation.failures";

    private const string DeleteOperation = "delete";
    private const string SweepOperation = "retention.sweep";
    private const string PurgeOperation = "grant.purge";

    private const string DeleteSpan = "agentexperience.delete";
    private const string SweepSpan = "agentexperience.retention.sweep";
    private const string PurgeSpan = "agentexperience.grant.purge";

    private const string OperationAttribute = "agentexperience.operation";
    private const string OutcomeAttribute = "agentexperience.outcome";
    private const string ErrorClassAttribute = "agentexperience.error.class";
    private const string ErrorTypeAttribute = "error.type";
    private const string ExperienceIdAttribute = "agentexperience.experience_id";
    private const string ErasedCountAttribute = "agentexperience.erased_count";
    private const string InterruptedAttribute = "agentexperience.interrupted";

    private const string Administrator = "sharing-administrator";

    /// <summary>Planted in everything an erasure touches or removes; it must surface nowhere in telemetry.</summary>
    private const string Marker = "ERASED-7f3a9c-MARKER";

    /// <summary>Every span attribute the three erasure operations may write, and nothing else.</summary>
    private static readonly string[] AllowedSpanAttributes =
    [
        OperationAttribute,
        OutcomeAttribute,
        ErrorClassAttribute,
        ErrorTypeAttribute,
        ExperienceIdAttribute,
        ErasedCountAttribute,
        InterruptedAttribute,
    ];

    private static readonly string[] AllowedDimensions = ["operation", "outcome", "error.class", "nested"];

    private readonly PostgresFixture _fixture = fixture;

    // ------------------------------------------------------------------ what a returned erasure emits

    [Fact]
    public async Task An_erasure_is_one_span_and_one_count_and_duration_under_its_outcome()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource);
        var id = await SeedAsync(store, auth, scope, ColumnTime);

        using var probe = TelemetryProbe.All();

        var deleted = await store.DeleteAsync(auth, scope, id, CancellationToken.None);
        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);

        var span = Assert.Single(probe.Spans(DeleteSpan));
        Assert.Equal(Source, span.Source.Name);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(DeleteOperation, span.GetTagItem(OperationAttribute));
        Assert.Equal(nameof(ExperienceStoreOutcome.Deleted), span.GetTagItem(OutcomeAttribute));
        Assert.Equal(id.ToString("D"), span.GetTagItem(ExperienceIdAttribute));

        var counted = Assert.Single(probe.For(CountInstrument, DeleteOperation));
        Assert.Equal(1d, counted.Value);
        Assert.Equal(Source, counted.Meter);
        Assert.Equal(nameof(ExperienceStoreOutcome.Deleted), counted.Tags["outcome"]);
        Assert.False(Assert.IsType<bool>(counted.Tags["nested"]));

        var timed = Assert.Single(probe.For(DurationInstrument, DeleteOperation));
        Assert.True(timed.Value >= 0d);
        Assert.Equal(nameof(ExperienceStoreOutcome.Deleted), timed.Tags["outcome"]);

        Assert.Empty(probe.For(FailuresInstrument, DeleteOperation));

        // Deleting twice is Deleted again, and is a second, separate count.
        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, scope, id, CancellationToken.None)).Outcome);
        Assert.Equal(2, probe.For(CountInstrument, DeleteOperation).Count);
        Assert.Equal(2, probe.Spans(DeleteSpan).Count);
    }

    [Fact]
    public async Task Every_refusal_is_reported_under_its_own_outcome_name_and_never_as_a_failure()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var foreign = Scope(tenant, team: "team-elsewhere");
        var store = new PostgresExperienceRecordStore(_fixture.DataSource);
        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var administration = new GrantAdministration(Administrator, DateTimeOffset.UtcNow);
        var denied = Authorize(NewTenant());
        var id = await SeedAsync(store, auth, scope, ColumnTime);

        using var probe = TelemetryProbe.All();

        var expected = new List<(string Operation, ExperienceStoreOutcome Outcome)>
        {
            (DeleteOperation, (await store.DeleteAsync(auth, foreign, id, CancellationToken.None)).Outcome),
            (DeleteOperation, (await store.DeleteAsync(auth, scope, id, expectedRevision: 7, CancellationToken.None)).Outcome),
            (DeleteOperation, (await store.DeleteAsync(denied, scope, id, CancellationToken.None)).Outcome),
            (DeleteOperation, (await store.DeleteAsync(auth, scope, Guid.Empty, CancellationToken.None)).Outcome),
            (SweepOperation, (await store.SweepExpiredAsync(denied, scope, TimeSpan.FromDays(1), 10, CancellationToken.None)).Outcome),
            (SweepOperation, (await store.SweepExpiredAsync(auth, scope, TimeSpan.Zero, 10, CancellationToken.None)).Outcome),
            (PurgeOperation, (await grants.PurgeExpiredAsync(auth, administration: null, scope, 10, CancellationToken.None)).Outcome),
            (PurgeOperation, (await grants.PurgeExpiredAsync(auth, administration, scope, 0, CancellationToken.None)).Outcome),
        };

        // The calls really did reach each refusal; otherwise the assertions below would be about Deleted.
        Assert.Equal(
            [
                ExperienceStoreOutcome.NotFound, ExperienceStoreOutcome.StaleRevision, ExperienceStoreOutcome.Denied,
                ExperienceStoreOutcome.Invalid, ExperienceStoreOutcome.Denied, ExperienceStoreOutcome.Invalid,
                ExperienceStoreOutcome.Denied, ExperienceStoreOutcome.Invalid,
            ],
            expected.Select(entry => entry.Outcome));

        var spans = probe.Activities.Where(activity => activity.Source.Name == Source).ToList();
        Assert.Equal(expected.Count, spans.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(ActivityStatusCode.Ok, spans[i].Status);
            Assert.Equal(expected[i].Operation, spans[i].GetTagItem(OperationAttribute));
            Assert.Equal(expected[i].Outcome.ToString(), spans[i].GetTagItem(OutcomeAttribute));
        }

        foreach (var operation in new[] { DeleteOperation, SweepOperation, PurgeOperation })
        {
            Assert.Equal(
                expected.Where(entry => entry.Operation == operation).Select(entry => entry.Outcome.ToString()).Order(),
                probe.For(CountInstrument, operation).Select(measurement => (string)measurement.Tags["outcome"]!).Order());
            Assert.Empty(probe.For(FailuresInstrument, operation));
        }

        // A refusal erased nothing, and a sweep or purge that was refused says so with a zero count.
        Assert.All(spans.Where(span => (string?)span.GetTagItem(OperationAttribute) != DeleteOperation), span =>
            Assert.Equal(0, span.GetTagItem(ErasedCountAttribute)));
    }

    [Fact]
    public async Task A_sweep_is_one_span_that_reports_how_many_it_erased_and_never_which()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: new FrozenClock(ColumnTime));
        var seeded = new[]
        {
            await SeedAsync(store, auth, scope, ColumnTime.AddDays(-300)),
            await SeedAsync(store, auth, scope, ColumnTime.AddDays(-200)),
            await SeedAsync(store, auth, scope, ColumnTime.AddDays(-100)),
        };

        using var probe = TelemetryProbe.All();

        var first = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 2, CancellationToken.None);
        var second = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 2, CancellationToken.None);
        Assert.Equal(2, first.DeletedCount);
        Assert.Equal(1, second.DeletedCount);

        // One span per call, not one per record: the sweep erases through the store's private step,
        // never through DeleteAsync, so no delete span was opened underneath it.
        var spans = probe.Spans(SweepSpan);
        Assert.Equal(2, spans.Count);
        Assert.Empty(probe.Spans(DeleteSpan));
        Assert.Empty(probe.For(CountInstrument, DeleteOperation));

        Assert.Equal(2, spans[0].GetTagItem(ErasedCountAttribute));
        Assert.False(Assert.IsType<bool>(spans[0].GetTagItem(InterruptedAttribute)));
        Assert.Equal(1, spans[1].GetTagItem(ErasedCountAttribute));
        Assert.All(spans, span =>
        {
            Assert.Equal(ActivityStatusCode.Ok, span.Status);
            Assert.Equal(nameof(ExperienceStoreOutcome.Deleted), span.GetTagItem(OutcomeAttribute));
            Assert.Null(span.GetTagItem(ExperienceIdAttribute));
        });

        // Which records went is the database's to say, never the trace's.
        var values = probe.EverySpanValue.Concat(probe.EveryMeasurementValue).ToList();
        Assert.All(seeded, id => Assert.DoesNotContain(values, value => value.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(2, probe.For(CountInstrument, SweepOperation).Count);
        Assert.Empty(probe.For(FailuresInstrument, SweepOperation));
    }

    [Fact]
    public async Task A_purge_reports_how_many_grants_it_removed()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var owner = Scope(tenant, team: "team-a");
        var store = new PostgresExperienceRecordStore(_fixture.DataSource);
        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        var id = await SeedAsync(store, auth, owner, ColumnTime);
        var grantId = await SeedExpiredGrantAsync(id, owner, Scope(tenant, team: "team-b"), "a window that closed", Administrator);

        using var probe = TelemetryProbe.All();

        var purged = await grants.PurgeExpiredAsync(auth, new GrantAdministration(Administrator, DateTimeOffset.UtcNow), owner, 10, CancellationToken.None);
        Assert.Equal(1, purged.PurgedCount);

        var span = Assert.Single(probe.Spans(PurgeSpan));
        Assert.Equal(Source, span.Source.Name);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(PurgeOperation, span.GetTagItem(OperationAttribute));
        Assert.Equal(nameof(ExperienceStoreOutcome.Deleted), span.GetTagItem(OutcomeAttribute));
        Assert.Equal(1, span.GetTagItem(ErasedCountAttribute));

        // The purged grant's ID, and the record it named, are not on the span.
        Assert.DoesNotContain(probe.EverySpanValue, value => value.Contains(grantId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(probe.EverySpanValue, value => value.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase));

        var counted = Assert.Single(probe.For(CountInstrument, PurgeOperation));
        Assert.Equal(nameof(ExperienceStoreOutcome.Deleted), counted.Tags["outcome"]);
        Assert.Single(probe.For(DurationInstrument, PurgeOperation));
        Assert.Empty(probe.For(FailuresInstrument, PurgeOperation));
    }

    // ------------------------------------------------------------------ a sweep that stops early

    [Fact]
    public async Task A_sweep_the_caller_stopped_is_a_returned_outcome_that_still_reports_its_partial_count()
    {
        var (store, auth, scope, blocked) = await SweepBlockedOnItsSecondRecordAsync();
        await using var holding = await HoldAsync(blocked);

        using var probe = TelemetryProbe.All();
        using var cancellation = new CancellationTokenSource();

        var sweeping = store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 50, cancellation.Token);
        await WaitForABlockedErasureAsync();
        await cancellation.CancelAsync();

        var partial = await sweeping;
        Assert.True(partial.Interrupted);
        Assert.Equal(1, partial.DeletedCount);

        // The host asked it to stop, so the span is Ok and the failure counter does not move.
        var span = Assert.Single(probe.Spans(SweepSpan));
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal(nameof(ExperienceStoreOutcome.Deleted), span.GetTagItem(OutcomeAttribute));
        Assert.Equal(1, span.GetTagItem(ErasedCountAttribute));
        Assert.True(Assert.IsType<bool>(span.GetTagItem(InterruptedAttribute)));
        Assert.Empty(probe.For(FailuresInstrument, SweepOperation));
    }

    [Fact]
    public async Task A_sweep_broken_by_storage_mid_batch_is_an_Infrastructure_failure_that_still_reports_what_it_erased()
    {
        var (store, auth, scope, blocked) = await SweepBlockedOnItsSecondRecordAsync();
        await using var holding = await HoldAsync(blocked);

        using var probe = TelemetryProbe.All();

        var sweeping = store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 50, CancellationToken.None);
        await WaitForABlockedErasureAsync();

        // Kill the sweep's own backend while it waits on the held row: a real storage failure part-way
        // through the batch, after one record was irreversibly erased.
        await using (var terminate = _fixture.DataSource.CreateCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE wait_event_type = 'Lock' AND query LIKE '%purge_experience_record%'"))
        {
            Assert.True(Assert.IsType<bool>(await terminate.ExecuteScalarAsync()));
        }

        var thrown = await Assert.ThrowsAsync<ExperienceRetentionSweepInterruptedException>(() => sweeping);
        Assert.Equal(1, thrown.Partial.DeletedCount);

        var span = Assert.Single(probe.Spans(SweepSpan));
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("Faulted", span.GetTagItem(OutcomeAttribute));
        Assert.Equal("Infrastructure", span.GetTagItem(ErrorClassAttribute));
        Assert.Equal(typeof(ExperienceRetentionSweepInterruptedException).FullName, span.GetTagItem(ErrorTypeAttribute));
        Assert.Equal(1, span.GetTagItem(ErasedCountAttribute));
        Assert.True(Assert.IsType<bool>(span.GetTagItem(InterruptedAttribute)));

        var failure = Assert.Single(probe.For(FailuresInstrument, SweepOperation));
        Assert.Equal("Infrastructure", failure.Tags["error.class"]);
        Assert.Equal("Faulted", Assert.Single(probe.For(CountInstrument, SweepOperation)).Tags["outcome"]);
    }

    // ------------------------------------------------------------------ what a thrown erasure emits

    [Theory]
    [InlineData(DeleteOperation, "unreachable", "Infrastructure")]
    [InlineData(SweepOperation, "unreachable", "Infrastructure")]
    [InlineData(PurgeOperation, "unreachable", "Infrastructure")]
    [InlineData(DeleteOperation, "cancelled", "Cancelled")]
    [InlineData(SweepOperation, "cancelled", "Cancelled")]
    [InlineData(PurgeOperation, "cancelled", "Cancelled")]
    [InlineData(DeleteOperation, "null-argument", "Unexpected")]
    [InlineData(SweepOperation, "null-argument", "Unexpected")]
    [InlineData(PurgeOperation, "null-argument", "Unexpected")]
    public async Task A_thrown_erasure_is_classified_and_counted_as_a_failure(string operation, string kind, string expectedClass)
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await using var unreachable = Unreachable();
        var dataSource = kind == "unreachable" ? unreachable : _fixture.DataSource;
        var store = new PostgresExperienceRecordStore(dataSource);
        var grants = new PostgresExperienceGrantStore(dataSource);
        var auth = kind == "null-argument" ? null! : Authorize(tenant);
        using var cancellation = new CancellationTokenSource();
        if (kind == "cancelled")
        {
            await cancellation.CancelAsync();
        }

        using var probe = TelemetryProbe.All();

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => operation switch
        {
            DeleteOperation => store.DeleteAsync(auth, scope, Guid.NewGuid(), cancellation.Token),
            SweepOperation => store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(1), 10, cancellation.Token),
            _ => grants.PurgeExpiredAsync(auth, new GrantAdministration(Administrator, DateTimeOffset.UtcNow), scope, 10, cancellation.Token),
        });

        var span = Assert.Single(probe.Activities, activity => activity.Source.Name == Source);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(operation, span.GetTagItem(OperationAttribute));
        Assert.Equal("Faulted", span.GetTagItem(OutcomeAttribute));
        Assert.Equal(expectedClass, span.GetTagItem(ErrorClassAttribute));
        Assert.Equal(thrown.GetType().FullName, span.GetTagItem(ErrorTypeAttribute));

        // The exception's own words never reach the span.
        Assert.DoesNotContain(probe.EverySpanValue, value => value.Contains(thrown.Message, StringComparison.Ordinal));

        var failure = Assert.Single(probe.For(FailuresInstrument, operation));
        Assert.Equal(1d, failure.Value);
        Assert.Equal(expectedClass, failure.Tags["error.class"]);
        Assert.False(Assert.IsType<bool>(failure.Tags["nested"]));
        Assert.Equal("Faulted", Assert.Single(probe.For(CountInstrument, operation)).Tags["outcome"]);
        Assert.Equal("Faulted", Assert.Single(probe.For(DurationInstrument, operation)).Tags["outcome"]);
    }

    // ------------------------------------------------------------------ what never reaches telemetry

    [Fact]
    public async Task Telemetry_never_carries_what_was_erased()
    {
        // The marker is in the scope, the task ID, the payload, the lesson, the grant's reason, its
        // recipient scope and its administrator: everything an erasure touches or removes.
        var tenant = $"tenant-{Marker}-{Guid.NewGuid():N}";
        var auth = Authorize(tenant);
        var scope = Scope(tenant, team: $"team-{Marker}");
        var recipient = Scope(tenant, team: $"recipient-{Marker}");
        var administrator = $"admin-{Marker}";
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: new FrozenClock(ColumnTime));
        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        await using var unreachable = Unreachable();
        var offline = new PostgresExperienceRecordStore(unreachable);
        var offlineGrants = new PostgresExperienceGrantStore(unreachable);

        var erased = await SeedMarkedAsync(store, auth, scope, ColumnTime);
        var swept = await SeedMarkedAsync(store, auth, scope, ColumnTime.AddDays(-400));
        var granted = await SeedMarkedAsync(store, auth, scope, ColumnTime);
        await SeedExpiredGrantAsync(granted, scope, recipient, $"reason-{Marker}", administrator);

        // The marker really is in the stored record, or the sweep below proves nothing.
        var stored = await store.GetAsync(auth, scope, erased, CancellationToken.None);
        Assert.Contains(Marker, stored.Record!.TaskSummary!, StringComparison.Ordinal);
        Assert.Contains(Marker, stored.Record.TaskId, StringComparison.Ordinal);
        Assert.Contains(Marker, stored.Record.Reflection!.Lesson, StringComparison.Ordinal);

        using var probe = TelemetryProbe.All();

        Assert.Equal(ExperienceStoreOutcome.Deleted, (await store.DeleteAsync(auth, scope, erased, CancellationToken.None)).Outcome);
        Assert.Equal(1, (await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 10, CancellationToken.None)).DeletedCount);
        Assert.Equal(1, (await grants.PurgeExpiredAsync(auth, new GrantAdministration(administrator, DateTimeOffset.UtcNow), scope, 10, CancellationToken.None)).PurgedCount);
        Assert.Equal(ExperienceStoreOutcome.NotFound, (await store.DeleteAsync(auth, recipient, granted, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, (await store.DeleteAsync(Authorize(NewTenant()), scope, granted, CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<ExperienceStoreException>(() => offline.DeleteAsync(auth, scope, granted, CancellationToken.None));
        await Assert.ThrowsAsync<ExperienceStoreException>(() => offline.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 10, CancellationToken.None));
        await Assert.ThrowsAsync<ExperienceStoreException>(() => offlineGrants.PurgeExpiredAsync(auth, new GrantAdministration(administrator, DateTimeOffset.UtcNow), scope, 10, CancellationToken.None));
        Assert.Equal(ExperienceStoreOutcome.Denied, (await store.SweepExpiredAsync(Authorize(NewTenant()), scope, TimeSpan.FromDays(90), 10, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Invalid, (await store.SweepExpiredAsync(auth, scope, TimeSpan.Zero, 10, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, (await grants.PurgeExpiredAsync(Authorize(NewTenant()), new GrantAdministration(administrator, DateTimeOffset.UtcNow), scope, 10, CancellationToken.None)).Outcome);
        Assert.Equal(ExperienceStoreOutcome.Invalid, (await grants.PurgeExpiredAsync(auth, new GrantAdministration(administrator, DateTimeOffset.UtcNow), scope, 0, CancellationToken.None)).Outcome);

        // Every path of all three operations -- success, refusal and fault -- saw the marked inputs.
        var spans = probe.Activities.Where(activity => activity.Source.Name == Source).ToList();
        Assert.Equal(12, spans.Count);

        Assert.All(probe.EverySpanValue, value => Assert.DoesNotContain(Marker, value, StringComparison.Ordinal));

        // No span events, links or baggage at all: an AddException would put the message and the stack
        // into an event's tags, where the tag sweep above would never look.
        Assert.All(spans, span =>
        {
            Assert.Empty(span.Events);
            Assert.Empty(span.Links);
            Assert.Empty(span.Baggage);
        });
        Assert.All(probe.EveryMeasurementValue, value => Assert.DoesNotContain(Marker, value, StringComparison.Ordinal));

        // The key sets are pinned too: the sweep above can only prove this drive's content stayed off,
        // whereas an exact key set makes a new attribute a deliberate, reviewed act.
        Assert.All(spans.SelectMany(span => span.TagObjects).Select(tag => tag.Key), key => Assert.Contains(key, AllowedSpanAttributes));
        Assert.All(probe.Measurements.Where(m => m.Meter == Source).SelectMany(m => m.Tags.Keys), key => Assert.Contains(key, AllowedDimensions));
    }

    /// <summary>
    /// Rule 6 against a hostile host: every listener callback throws, on span start, span stop and every
    /// measurement. An irreversible erasure that succeeded must still be reported as the result it
    /// reached, and one that failed must still throw its own exception, never the listener's.
    /// </summary>
    [Fact]
    public async Task A_listener_that_throws_never_changes_what_an_erasure_returns_or_throws()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant, team: "team-a");
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: new FrozenClock(ColumnTime));
        var grants = new PostgresExperienceGrantStore(_fixture.DataSource);
        await using var unreachable = Unreachable();
        var offline = new PostgresExperienceRecordStore(unreachable);
        var offlineGrants = new PostgresExperienceGrantStore(unreachable);
        var administration = new GrantAdministration(Administrator, DateTimeOffset.UtcNow);

        var deletable = await SeedAsync(store, auth, scope, ColumnTime);
        await SeedAsync(store, auth, scope, ColumnTime.AddDays(-400));
        var granted = await SeedAsync(store, auth, scope, ColumnTime);
        await SeedExpiredGrantAsync(granted, scope, Scope(tenant, team: "team-b"), "a window that closed", Administrator);

        using var probe = TelemetryProbe.Throwing();

        var deleted = await store.DeleteAsync(auth, scope, deletable, CancellationToken.None);
        var swept = await store.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 10, CancellationToken.None);
        var purged = await grants.PurgeExpiredAsync(auth, administration, scope, 10, CancellationToken.None);

        Assert.Equal(ExperienceStoreOutcome.Deleted, deleted.Outcome);
        Assert.Equal((ExperienceStoreOutcome.Deleted, 1), (swept.Outcome, swept.DeletedCount));
        Assert.Equal((ExperienceStoreOutcome.Deleted, 1), (purged.Outcome, purged.PurgedCount));

        // The fault path, where it matters most: telemetry runs inside a catch that is about to rethrow.
        await Assert.ThrowsAsync<ExperienceStoreException>(() => offline.DeleteAsync(auth, scope, deletable, CancellationToken.None));
        await Assert.ThrowsAsync<ExperienceStoreException>(() => offline.SweepExpiredAsync(auth, scope, TimeSpan.FromDays(90), 10, CancellationToken.None));
        await Assert.ThrowsAsync<ExperienceStoreException>(() => offlineGrants.PurgeExpiredAsync(auth, administration, scope, 10, CancellationToken.None));

        // The listener really was called, on every path, and threw every time: a span start that threw
        // still has its span stopped (so no orphan is left as Activity.Current), and a measurement
        // callback that threw on the count cost neither the duration nor the alertable failure count.
        Assert.Null(Activity.Current);
        Assert.Equal(6, probe.Activities.Count(activity => activity.Source.Name == Source));
        foreach (var operation in new[] { DeleteOperation, SweepOperation, PurgeOperation })
        {
            Assert.Equal(["Deleted", "Faulted"], probe.For(CountInstrument, operation).Select(m => (string)m.Tags["outcome"]!).Order());
            Assert.Equal(2, probe.For(DurationInstrument, operation).Count);
            Assert.Equal("Infrastructure", Assert.Single(probe.For(FailuresInstrument, operation)).Tags["error.class"]);
        }
    }

    [Fact]
    public async Task A_meter_listener_alone_records_every_erasure_and_opens_no_span()
    {
        var tenant = NewTenant();
        await using var unreachable = Unreachable();
        var store = new PostgresExperienceRecordStore(unreachable);

        using var probe = TelemetryProbe.MetricsOnly();

        var denied = await store.DeleteAsync(Authorize(NewTenant()), Scope(tenant), Guid.NewGuid(), CancellationToken.None);
        await Assert.ThrowsAsync<ExperienceStoreException>(() => store.SweepExpiredAsync(Authorize(tenant), Scope(tenant), TimeSpan.FromDays(1), 10, CancellationToken.None));

        Assert.Equal(ExperienceStoreOutcome.Denied, denied.Outcome);
        Assert.Null(Activity.Current);
        Assert.Equal("Denied", Assert.Single(probe.For(CountInstrument, DeleteOperation)).Tags["outcome"]);
        Assert.Equal("Faulted", Assert.Single(probe.For(CountInstrument, SweepOperation)).Tags["outcome"]);
        Assert.Equal("Infrastructure", Assert.Single(probe.For(FailuresInstrument, SweepOperation)).Tags["error.class"]);
    }

    [Fact]
    public async Task A_sampler_that_declines_still_records_measurements_and_changes_no_result()
    {
        var tenant = NewTenant();
        var scope = Scope(tenant);
        await using var unreachable = Unreachable();
        var store = new PostgresExperienceRecordStore(unreachable);

        var unobserved = await store.DeleteAsync(Authorize(NewTenant()), scope, Guid.NewGuid(), CancellationToken.None);

        using var probe = TelemetryProbe.Declining();

        var observed = await store.DeleteAsync(Authorize(NewTenant()), scope, Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(unobserved.Outcome, observed.Outcome);
        Assert.Equal(ExperienceStoreOutcome.Denied, observed.Outcome);
        Assert.Empty(probe.Activities);
        Assert.Equal(nameof(ExperienceStoreOutcome.Denied), Assert.Single(probe.For(CountInstrument, DeleteOperation)).Tags["outcome"]);

        // A declined span is a null Activity on the faulted path too, and the alertable counter must
        // still move: Faulted records through the instruments, not through the span.
        await Assert.ThrowsAsync<ExperienceStoreException>(() => store.DeleteAsync(Authorize(tenant), scope, Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(probe.Activities);
        Assert.Equal("Infrastructure", Assert.Single(probe.For(FailuresInstrument, DeleteOperation)).Tags["error.class"]);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Guid> SeedAsync(PostgresExperienceRecordStore store, AuthorizationContext auth, Scope scope, DateTimeOffset createdAt)
    {
        var record = Minimal(scope, createdAt: createdAt);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    private static async Task<Guid> SeedMarkedAsync(PostgresExperienceRecordStore store, AuthorizationContext auth, Scope scope, DateTimeOffset createdAt)
    {
        var full = Full(scope);
        var record = full with
        {
            TaskId = $"task-{Marker}",
            TaskSummary = $"summary {Marker}",
            Reflection = full.Reflection! with { Lesson = $"lesson {Marker}" },
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);
        return record.ExperienceId;
    }

    /// <summary>
    /// Two records past the cutoff, and the second (younger) one is the row the test will hold, so the
    /// sweep erases the first and then blocks.
    /// </summary>
    private async Task<(PostgresExperienceRecordStore Store, AuthorizationContext Auth, Scope Scope, Guid Blocked)> SweepBlockedOnItsSecondRecordAsync()
    {
        var tenant = NewTenant();
        var auth = Authorize(tenant);
        var scope = Scope(tenant);
        var store = new PostgresExperienceRecordStore(_fixture.DataSource, onGrantsUnavailable: null, auditing: null, timeProvider: new FrozenClock(ColumnTime));
        await SeedAsync(store, auth, scope, ColumnTime.AddDays(-400));
        var blocked = await SeedAsync(store, auth, scope, ColumnTime.AddDays(-300));
        return (store, auth, scope, blocked);
    }

    /// <summary>Takes the row lock the sweep's erasure of <paramref name="experienceId"/> will wait on, until disposed.</summary>
    private async Task<HeldRow> HoldAsync(Guid experienceId)
    {
        var connection = await _fixture.DataSource.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var pin = new NpgsqlCommand(
            "SELECT revision FROM agent_experience.experience_records WHERE experience_id = @id FOR UPDATE", connection, transaction))
        {
            pin.Parameters.Add(new NpgsqlParameter<Guid>("id", experienceId));
            Assert.NotNull(await pin.ExecuteScalarAsync());
        }

        return new HeldRow(connection, transaction);
    }

    /// <summary>
    /// Waits until an erasure is parked on a row lock. This collection runs alone against its own
    /// container, so the only such backend is the sweep under test.
    /// </summary>
    private async Task WaitForABlockedErasureAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = _fixture.DataSource.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%purge_experience_record%'");
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The sweep never blocked on the held row.");
    }

    private async Task<Guid> SeedExpiredGrantAsync(Guid experienceId, Scope owner, Scope recipient, string reason, string administrator)
    {
        var grantId = Guid.NewGuid();

        await using (var grant = _fixture.DataSource.CreateCommand(
            "INSERT INTO agent_experience.experience_grants (grant_id, experience_id, tenant_id, application_id, " +
            "project_id, team_id, agent_id, user_id, recipient_tenant_id, recipient_application_id, " +
            "recipient_project_id, recipient_team_id, recipient_agent_id, recipient_user_id, reason, " +
            "administrator_principal_id, issued_at, expires_at, revoked_at, revocation_reason) VALUES " +
            "(@grant_id, @experience_id, @tenant, @app, @project, @team, NULL, NULL, @tenant, @app, " +
            "@project, @recipient_team, NULL, NULL, @reason, @administrator, " +
            "now() - interval '2 days', now() - interval '1 day', NULL, NULL)"))
        {
            grant.Parameters.Add(new NpgsqlParameter<Guid>("grant_id", grantId));
            grant.Parameters.Add(new NpgsqlParameter<Guid>("experience_id", experienceId));
            grant.Parameters.Add(new NpgsqlParameter<string>("tenant", NpgsqlDbType.Text) { TypedValue = owner.TenantId });
            grant.Parameters.Add(new NpgsqlParameter<string>("app", NpgsqlDbType.Text) { TypedValue = owner.ApplicationId });
            grant.Parameters.Add(new NpgsqlParameter<string>("project", NpgsqlDbType.Text) { TypedValue = owner.ProjectId });
            grant.Parameters.Add(new NpgsqlParameter<string>("team", NpgsqlDbType.Text) { TypedValue = owner.TeamId! });
            grant.Parameters.Add(new NpgsqlParameter<string>("recipient_team", NpgsqlDbType.Text) { TypedValue = recipient.TeamId! });
            grant.Parameters.Add(new NpgsqlParameter<string>("reason", NpgsqlDbType.Text) { TypedValue = reason });
            grant.Parameters.Add(new NpgsqlParameter<string>("administrator", NpgsqlDbType.Text) { TypedValue = administrator });
            Assert.Equal(1, await grant.ExecuteNonQueryAsync());
        }

        return grantId;
    }

    /// <summary>A row lock held open on its own connection, released by rolling back.</summary>
    private sealed class HeldRow(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>A clock the test sets, so "older than ninety days" is a fact rather than a wait.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
