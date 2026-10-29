using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.Postgres;
using Npgsql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 27.2's cross-language proof, the .NET half: a fleet .NET wrote acts the same in every language. Through the
/// .NET PostgreSQL store it writes two lessons and the measured runs of each (the holdout's rows and each run's own
/// finalized record): treatment runs given lesson X failed and holdout runs denied it verified, so X measures
/// <c>Hurts</c>; lesson Y measures the reverse, <c>Helps</c>; together the runs span zero. The rows it wrote, every
/// column in PostgreSQL's text form, must be exactly the checked-in <see cref="RowsFile"/> (rewritten with the update
/// switch). The Python and Go ports' PostgreSQL tests load that file into their own database and invoke an agent with
/// the benefit policy on: each must omit X as <c>MeasuredHarmful</c> and inject Y. Here .NET's own cache and selection
/// do the same over the rows it wrote.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class CrossLanguageBenefitPolicyPostgresTests(ContractPostgresDatabase database)
{
    /// <summary>The fleet's scope.</summary>
    internal static readonly Scope FleetScope = new("cross-language-benefit", "benefit-app", "benefit-project", AgentId: "benefit-agent");

    /// <summary>The instant the ports' agents and .NET's cache read the fleet at: an hour after the runs, inside the window.</summary>
    internal static readonly DateTimeOffset FleetNow = Fixed.T0.AddHours(1);

    /// <summary>The fewest runs per arm the policy needs, in every language: each lesson has three runs per arm.</summary>
    internal const int MinimumRunsPerArm = 3;

    private const int F = 0xb0;

    private static readonly string RowsFile = Path.Combine(
        Path.GetDirectoryName(ContractFiles.Root)!, "tests", "AgentExperience.Contract.Tests", "cross-language", "dotnet-benefit-fleet-rows.json");

    private static readonly string[] Tables = ["experience_records", "lifecycle_events", "experience_holdout_assignments"];

    private static readonly HashSet<string> Derived = new(StringComparer.Ordinal)
    {
        "search_vector", "deleted_at", "search_vector_sealed", "reflection_model_authored",
        "replacement_experience_id", "confidence_evidence_id", "confidence_kind", "confidence_source", "confidence_run_id",
        "confidence_verification_round_id", "confidence_reviewer_identity", "confidence_rule_version", "confidence_detail",
        "prior_reuse_confidence", "new_reuse_confidence", "prior_supporting_validations", "new_supporting_validations",
        "prior_contradictions", "new_contradictions", "confidence_assessment_id", "confidence_admission",
    };

    private static readonly ExperienceRecord X = SearchContract.Seed(F, 1, "rotate-keys", "Rotate the signing keys", "Restart the signer after rotating the signing keys.", scope: FleetScope);

    private static readonly ExperienceRecord Y = SearchContract.Seed(F, 2, "rotate-keys", "Rotate the signing keys", "Rotate the signing keys in the documented order.", scope: FleetScope);

    [Fact]
    public async Task A_dotnet_fleet_measures_one_lesson_hurting_and_the_policy_omits_it()
    {
        var application = await database.CreateAsync("xl_benefit");
        var owner = database.OwnerOf("xl_benefit");
        var authorization = SearchContractPostgresTests.Authorization(FleetScope);
        var problems = new List<string>();

        // The lessons and the runs that measured them, written as .NET finalization writes them.
        var runs = new List<(ExperienceRecord Run, ExperienceRecord Lesson, HoldoutArm Arm)>();
        var next = 0x10;
        void Runs(ExperienceRecord lesson, HoldoutArm arm, bool verified)
        {
            for (var i = 0; i < MinimumRunsPerArm; i++)
            {
                var run = SearchContract.Seed(F, next++, "close-books", "Close the quarterly books", "Reconcile the ledger before closing.", scope: FleetScope);
                run = run with { Outcome = run.Outcome with { Status = verified ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed } };
                runs.Add((run, lesson, arm));
            }
        }

        Runs(X, HoldoutArm.Treatment, verified: false);
        Runs(X, HoldoutArm.Holdout, verified: true);
        Runs(Y, HoldoutArm.Treatment, verified: true);
        Runs(Y, HoldoutArm.Holdout, verified: false);

        foreach (var record in new[] { X, Y }.Concat(runs.Select(entry => entry.Run)))
        {
            await PortAgentRowsPostgres.WriteAsDotNetAsync(application, record, actor: null, problems);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var store = new PostgresExperienceRecordStore(application);
        for (var i = 0; i < runs.Count; i++)
        {
            var (run, lesson, arm) = runs[i];
            var recorded = await store.RecordHoldoutAssignmentAsync(
                authorization,
                FleetScope,
                new HoldoutAssignment(run.SourceRunId, arm, [new RunExposure(lesson.ExperienceId, lesson.Revision)], Fixed.T0.AddMinutes(i).AddTicks(1_234_560)),
                CancellationToken.None);
            Assert.Equal(ExperienceStoreOutcome.Committed, recorded.Outcome);
        }

        // The rows, as the ports load them.
        var rows = await DumpAsync(owner);
        var text = ContractJson.Write(rows);
        if (ContractFiles.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RowsFile)!);
            await File.WriteAllTextAsync(RowsFile, text);
        }

        Assert.True(File.Exists(RowsFile), $"{RowsFile} is missing; run with {ContractFiles.UpdateVariable}=1.");
        Assert.True(
            text == await File.ReadAllTextAsync(RowsFile),
            $"{RowsFile} is not what the .NET store writes now; run with {ContractFiles.UpdateVariable}=1 and read the diff.");

        // The estimate the policy acts on: X hurts, Y helps, the scope has no evidence.
        var policy = new ExperienceBenefitPolicy { Enabled = true, PreferMeasuredHelpful = true, MinimumRunsPerArm = MinimumRunsPerArm };
        var clock = new FixedTimeProvider(FleetNow);
        var cache = new ExperienceBenefitCache(store, policy, clock);
        Assert.Null(cache.Current(authorization, FleetScope));
        await cache.SettledAsync();
        var view = cache.Current(authorization, FleetScope);
        Assert.NotNull(view);
        Assert.False(view.ScopeHurts);
        Assert.Equal([X.ExperienceId], view.Hurts);
        Assert.Equal([Y.ExperienceId], view.Helps);

        // The file loads as a port loads it (each row as the application role, every value cast from its text), and reads
        // back as the same estimate.
        var loaded = await database.CreateAsync("xl_benefit_loaded");
        var file = JsonNode.Parse(await File.ReadAllTextAsync(RowsFile))!.AsObject();
        foreach (var table in Tables)
        {
            foreach (var row in file[table]!.AsArray())
            {
                await PortAgentRowsPostgres.InsertRawAsync(loaded, table, row!.AsObject());
            }
        }

        var reread = new ExperienceBenefitCache(new PostgresExperienceRecordStore(loaded), policy, clock);
        Assert.Null(reread.Current(authorization, FleetScope));
        await reread.SettledAsync();
        var loadedView = reread.Current(authorization, FleetScope);
        Assert.NotNull(loadedView);
        Assert.Equal([X.ExperienceId], loadedView.Hurts);
        Assert.Equal([Y.ExperienceId], loadedView.Helps);

        // And what .NET's own provider selects for the task the ports' agents are given.
        var retrieval = new ExperienceRetrievalService(new PostgresExperienceCandidateSource(application), RetrievalPolicy.Default, RankingWeights.Default, clock);
        var retrieved = await retrieval.RetrieveAsync(new RetrieveExperienceRequest(authorization, FleetScope, "Rotate the signing keys"), CancellationToken.None);
        Assert.Equal(RetrievalOutcome.Completed, retrieved.Outcome);
        Assert.Equal(
            new[] { X.ExperienceId, Y.ExperienceId }.Order(),
            retrieved.Records.Select(entry => entry.Record.ExperienceId).Order());
        var provider = new ExperienceContextProvider(retrieval, store, new ExperienceInjectionOptions
        {
            ResolveRequest = _ => null,
            SessionLimits = null,
            Benefit = policy,
            TimeProvider = clock,
        });
        var omitted = new List<OmittedExperience>();
        var selected = provider.SelectCandidates(retrieved.Records, omitted, requestLimit: null, view);
        Assert.Equal([Y.ExperienceId], selected.Select(entry => entry.Record.ExperienceId));
        var harmful = Assert.Single(omitted);
        Assert.Equal(X.ExperienceId, harmful.ExperienceId);
        Assert.Equal(InjectionOmissionReason.MeasuredHarmful, harmful.Reason);
        Assert.Equal(ExperienceContextProvider.MeasuredHarmfulDetail, harmful.Detail);
    }

    /// <summary>Every row of the fleet's tables, each column in PostgreSQL's text form in UTC, read as the owner, in key order.</summary>
    private static async Task<JsonObject> DumpAsync(NpgsqlDataSource owner)
    {
        var dump = new JsonObject();
        foreach (var table in Tables)
        {
            // The columns a writer supplies, as the ports' rows files hold them: not the ones the database derives (the
            // search vectors, the model-authored flag) or leaves at their defaults for a finalized record.
            var columns = (await PortAgentRowsPostgres.ColumnsAsync(owner, table)).Where(column => !Derived.Contains(column)).ToList();
            var order = table switch
            {
                "experience_records" => "experience_id",
                "lifecycle_events" => "event_id",
                _ => "run_id, experience_id",
            };

            await using var connection = await owner.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var zone = new NpgsqlCommand("SET LOCAL TIME ZONE 'UTC'", connection, transaction))
            {
                await zone.ExecuteNonQueryAsync();
            }

            await using var select = new NpgsqlCommand(
                $"SELECT {string.Join(", ", columns.Select(name => $"\"{name}\"::text"))} FROM agent_experience.{table} " +
                $"WHERE tenant_id = @tenant ORDER BY {order}",
                connection,
                transaction);
            select.Parameters.AddWithValue("tenant", FleetScope.TenantId);
            var rows = new JsonArray();
            await using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new JsonObject();
                for (var i = 0; i < columns.Count; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
                }

                rows.Add(row);
            }

            dump[table] = rows;
        }

        return dump;
    }
}
