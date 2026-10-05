using Npgsql;
using static AgentExperience.Storage.Postgres.Tests.TestRecords;

namespace AgentExperience.Storage.Postgres.Tests;

/// <summary>
/// Story 17.3: migration <c>0023</c> over a ledger written before it, and the script's shape.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresRecordedOnlyEvidenceMigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_0023_over_an_existing_ledger_validates_both_constraints_and_drops_0007_s()
    {
        await using var dataSource = await fixture.CreateDatabaseAsync("recorded_only_0023");
        foreach (var scriptName in PostgresExperienceRecordSchema.ScriptNames
            .TakeWhile(name => !string.Equals(name, PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName, StringComparison.Ordinal)))
        {
            await ExecuteAsync(dataSource, PostgresExperienceRecordSchema.GetScript(scriptName));
        }

        // A ledger as an earlier build wrote it: counted evidence, a duplicate of it, and counted evidence with no admission.
        var tenant = NewTenant();
        var (auth, scope) = (Authorize(tenant), Scope(tenant));
        var store = new PostgresExperienceRecordStore(dataSource, encryption: ExperienceEncryption.ForcePlaintext);
        var record = Minimal(scope, status: ExperienceStatus.Validated);
        Assert.Equal(ExperienceStoreOutcome.Created, (await store.CreateAsync(auth, record, CancellationToken.None)).Outcome);

        var run = Guid.NewGuid();
        var round = Guid.NewGuid();
        var counted = Machine(run, round, 0, 0d) with { Admission = ConfidenceEvidenceAdmission.Verified };
        Assert.True((await CommitAsync(store, auth, scope, record.ExperienceId, 0, counted)).AppliedConfidence!.Counted);
        var duplicate = Machine(run, round, 0, 0d) with { Admission = ConfidenceEvidenceAdmission.HostTrusted };
        Assert.False((await CommitAsync(store, auth, scope, record.ExperienceId, 1, duplicate)).AppliedConfidence!.Counted);
        var unrecorded = Machine(Guid.NewGuid(), Guid.NewGuid(), 1, counted.NewReuseConfidence);
        Assert.True((await CommitAsync(store, auth, scope, record.ExperienceId, 1, unrecorded)).AppliedConfidence!.Counted);

        var applied = await ExperienceSchemaMigrator.MigrateAsync(dataSource, CancellationToken.None);
        Assert.Contains(PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName, applied.AppliedScripts);

        // The header's VALIDATE statements succeed over every row written before it.
        await ExecuteAsync(dataSource, "ALTER TABLE agent_experience.confidence_evidence VALIDATE CONSTRAINT confidence_evidence_event_when_counted_or_recorded_only");
        await ExecuteAsync(dataSource, "ALTER TABLE agent_experience.lifecycle_events VALIDATE CONSTRAINT lifecycle_events_confidence_moves_or_recorded_only");

        Assert.Equal(0L, await ScalarAsync(dataSource, "SELECT count(*) FROM pg_constraint WHERE conname = 'confidence_evidence_event_only_when_counted'"));
        Assert.Equal(1L, await ScalarAsync(dataSource, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'agent_experience' AND indexname = 'ix_confidence_evidence_key_with_event'"));
        Assert.Equal(3L, await ScalarAsync(dataSource, $"SELECT count(*) FROM agent_experience.confidence_evidence WHERE experience_id = '{record.ExperienceId}'"));

        // Running the script again changes nothing.
        await ExecuteAsync(dataSource, PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName));
    }

    [Fact]
    public void Script_0023_grants_nothing_adds_no_table_or_column_and_is_applied_after_0022()
    {
        var script = PostgresExperienceRecordSchema.GetScript(PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName);
        var statements = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.Contains("ADD CONSTRAINT confidence_evidence_event_when_counted_or_recorded_only", statements, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT lifecycle_events_confidence_moves_or_recorded_only", statements, StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT IF EXISTS confidence_evidence_event_only_when_counted", statements, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS ix_confidence_evidence_key_with_event", statements, StringComparison.Ordinal);
        Assert.Contains("IS NOT DISTINCT FROM 'HostTrusted'", statements, StringComparison.Ordinal);
        Assert.Contains("NOT VALID", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("SECURITY DEFINER", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD COLUMN", statements, StringComparison.Ordinal);
        Assert.DoesNotContain("TRIGGER", statements, StringComparison.Ordinal);

        var names = PostgresExperienceRecordSchema.ScriptNames.ToList();
        Assert.True(
            names.IndexOf(PostgresExperienceRecordSchema.LibraryReflectorAuthorshipScriptName)
                < names.IndexOf(PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName));
        Assert.True(names.IndexOf(PostgresExperienceRecordSchema.ConfidenceEvidenceScriptName) < names.IndexOf(PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName));
        Assert.True(names.IndexOf(PostgresExperienceRecordSchema.EvidenceAdmissionScriptName) < names.IndexOf(PostgresExperienceRecordSchema.RecordedOnlyEvidenceScriptName));
    }

    private static ConfidenceUpdate Machine(Guid run, Guid round, int priorSupporting, double priorScore) => new(
        EvidenceId: Guid.NewGuid(),
        Kind: ConfidenceEvidenceKind.Supporting,
        Source: ConfidenceEvidenceSource.Machine,
        RunId: run,
        VerificationRoundId: round,
        ReviewerIdentity: null,
        RuleVersion: "1.0.0",
        PriorReuseConfidence: priorScore,
        NewReuseConfidence: (2d + priorSupporting) / (3d + priorSupporting),
        PriorSupportingValidations: priorSupporting,
        NewSupportingValidations: priorSupporting + 1,
        PriorContradictions: 0,
        NewContradictions: 0);

    private static Task<ExperienceLifecycleCommitResult> CommitAsync(
        PostgresExperienceRecordStore store, AuthorizationContext auth, Scope scope, Guid recordId, long expectedRevision, ConfidenceUpdate update) =>
        store.CommitLifecycleEventAsync(
            auth,
            scope,
            new LifecycleEvent(Guid.NewGuid(), recordId, ExperienceStatus.Validated, ExperienceStatus.Validated, "seeded", "tests", ColumnTime, expectedRevision, null, update),
            CancellationToken.None);

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
