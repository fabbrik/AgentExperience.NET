using System.Text.Json.Nodes;
using AgentExperience.Storage.Postgres;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// The conformance runner for contract 1.17's <c>reuse-feedback-sql</c> family, against a schema the .NET migrator applied
/// with row-level security on: each case's steps run through the .NET <see cref="PostgresExperienceReuseFeedbackStore"/>
/// (whose statements <c>sql/reuse-feedback.sql</c> is generated from), and <c>expected.json</c> is each step's result and
/// the two ledger tables afterwards, every column in PostgreSQL's text form.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class ReuseFeedbackSqlContractPostgresTests(ContractPostgresDatabase database)
{
    [Fact]
    public async Task Every_reuse_feedback_sql_fixture_is_what_the_dotnet_postgres_ledger_leaves()
    {
        var drift = new List<string>();
        foreach (var (name, seed, steps) in ReuseFeedbackSqlContract.Cases)
        {
            var directory = $"fixtures/{ReuseFeedbackSqlContract.Family}/{name}";
            var seedJson = ContractJson.Write(seed);
            var stepsJson = ContractJson.Write(steps);
            ContractFiles.Verify($"{directory}/seed.json", seedJson, drift);
            ContractFiles.Verify($"{directory}/steps.json", stepsJson, drift);

            // As checked in, read as a port reads them.
            var readSeed = JsonNode.Parse(ContractFiles.Read($"{directory}/seed.json") ?? seedJson)!.AsObject();
            var readSteps = JsonNode.Parse(ContractFiles.Read($"{directory}/steps.json") ?? stepsJson)!.AsObject();
            var expected = await RunAsync("rf_" + name.Replace('-', '_'), readSeed, readSteps);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(expected), drift);
        }

        ContractFiles.VerifyNoStray(
            ReuseFeedbackSqlContract.Family,
            ReuseFeedbackSqlContract.Cases.Select(entry => entry.Name),
            ["seed.json", "steps.json", "expected.json"],
            drift);
        if (ContractFiles.Updating)
        {
            ContractLock.Relock(drift);
        }

        ContractFiles.AssertNoDrift(drift);
    }

    private async Task<JsonObject> RunAsync(string caseName, JsonObject seed, JsonObject steps)
    {
        var application = await database.CreateAsync(caseName);
        var owner = database.OwnerOf(caseName);
        foreach (var record in seed["records"]!.AsArray())
        {
            await SearchContractPostgresTests.InsertAsync(application, record!);
        }

        var clock = new SettableTimeProvider(Fixed.T0);
        var store = new PostgresExperienceReuseFeedbackStore(application, clock);
        var results = new JsonArray();
        foreach (var step in steps["steps"]!.AsArray())
        {
            clock.Now = LifecycleContract.Instant(step!["clock"])!.Value;
            var (operation, body) = step.AsObject().Where(member => member.Key != "clock").Select(member => (member.Key, member.Value!.AsObject())).Single();
            switch (operation)
            {
                case "record":
                    var recorded = await store.RecordAsync(
                        LifecycleContract.ReadAuthorization(body["authorization"]!),
                        ReuseFeedbackContract.ReadRecorded(body["feedback"]!.AsObject()),
                        CancellationToken.None);
                    results.Add(new JsonObject
                    {
                        ["outcome"] = recorded.Outcome.ToString(),
                        ["feedback"] = ReuseFeedbackContract.RecordedJson(recorded.Feedback),
                        ["errors"] = LifecycleContract.Errors(recorded.Errors),
                    });
                    break;

                case "erase":
                    await InjectionRecordContractPostgresTests.EraseAsync(owner, new EraseStep(
                        Guid.Parse(body["experienceId"]!.GetValue<string>()),
                        LifecycleContract.ReadScope(body["scope"]!),
                        LifecycleContract.Instant(body["deletedAt"])!.Value));
                    results.Add(null);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown reuse-feedback-sql step '{operation}'.");
            }
        }

        var expected = new JsonObject
        {
            ["results"] = results,
            ["reuse_feedback"] = await LifecycleSqlContractPostgresTests.TableAsync(owner, "reuse_feedback", ReuseFeedbackSqlContract.SubmissionColumns, "feedback_id"),
            ["reuse_feedback_exposures"] = await LifecycleSqlContractPostgresTests.TableAsync(owner, "reuse_feedback_exposures", ReuseFeedbackSqlContract.ExposureColumns, "feedback_id, ordinal"),
        };

        // Every case gets a database: idle pooled connections left behind would exhaust the server's slots.
        application.Clear();
        owner.Clear();
        return expected;
    }
}
