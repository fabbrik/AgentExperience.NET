using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Capture;
using AgentExperience.Core.Holdout;
using AgentExperience.Core.Sanitization;
using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.Postgres;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 27.3's cross-language proof (contract 1.11), the .NET half: a mixed fleet measures one memory provider. A .NET,
/// a Python and a Go agent, in one scope, each wrap a stub memory provider under the key <see cref="Key"/> with the
/// holdout at rate 0.5 and the default salt, and each runs eight captured runs whose own records are then finalized,
/// some verified and some failed.
/// <list type="number">
/// <item>Here the .NET agent runs through the .NET PostgreSQL store, its <see cref="ExperienceHoldoutContextProvider"/>
/// recording each run's arm, and the runs' own records are written as .NET finalization writes them. The rows it wrote,
/// every column in PostgreSQL's text form, must be exactly the checked-in <see cref="RowsFile"/> (rewritten with the
/// update switch).</item>
/// <item>Each port's own PostgreSQL test does the same with its agent and checks in its rows file; it then loads all
/// three rows files into one database, estimates the provider with its own <c>estimate_provider_benefit</c> /
/// <c>EstimateProviderBenefit</c>, and checks in what it computed.</item>
/// <item>Here the three rows files go into one database: every run must be in the arm the assignment function gives it,
/// and the .NET <see cref="AgentExperienceBenefit.EstimateProviderAsync"/> over the scope must count every finalized run
/// once, in its arm, and equal both ports' estimates: counts exactly, numbers within 1e-12.</item>
/// </list>
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class CrossLanguageProviderHoldoutPostgresTests(ContractPostgresDatabase database)
{
    /// <summary>The key every language's agent wraps its stub provider under.</summary>
    internal const string Key = "stub-memory";

    private const double Rate = 0.5;

    private const int F = 0xd7;

    /// <summary>The fleet's scope, the same in every language.</summary>
    internal static readonly Scope FleetScope = new("cross-language-provider", "provider-app", "provider-project", AgentId: "provider-agent");

    private static readonly string Repository = Path.GetDirectoryName(ContractFiles.Root)!;

    private static readonly string RowsFile = Path.Combine(
        Repository, "tests", "AgentExperience.Contract.Tests", "cross-language", "dotnet-provider-fleet-rows.json");

    private static readonly (string Port, string Path)[] RowsFiles =
    [
        ("dotnet", RowsFile),
        ("python", Path.Combine(Repository, "python", "tests", "cross-language", "python-agent-provider-rows.json")),
        ("go", Path.Combine(Repository, "go", "maf", "testdata", "cross-language", "go-agent-provider-rows.json")),
    ];

    private static readonly (string Port, string Path)[] EstimateFiles =
    [
        ("python", Path.Combine(Repository, "python", "tests", "cross-language", "python-provider-fleet-estimate.json")),
        ("go", Path.Combine(Repository, "go", "maf", "testdata", "cross-language", "go-provider-fleet-estimate.json")),
    ];

    private static readonly string[] Tables = ["experience_records", "lifecycle_events", ProviderHoldoutStatements.Table];

    /// <summary>Whether each measured run verified: a mix of verified and failed outcomes in each arm.</summary>
    private static readonly bool[] Passes = [true, false, true, true, false, true, false, true];

    private static readonly HashSet<string> Derived = new(StringComparer.Ordinal)
    {
        "search_vector", "deleted_at", "search_vector_sealed", "reflection_model_authored",
        "replacement_experience_id", "confidence_evidence_id", "confidence_kind", "confidence_source", "confidence_run_id",
        "confidence_verification_round_id", "confidence_reviewer_identity", "confidence_rule_version", "confidence_detail",
        "prior_reuse_confidence", "new_reuse_confidence", "prior_supporting_validations", "new_supporting_validations",
        "prior_contradictions", "new_contradictions", "confidence_assessment_id", "confidence_admission",
    };

    [Fact]
    public async Task The_dotnet_agent_wraps_its_provider_and_writes_the_checked_in_rows()
    {
        var application = await database.CreateAsync("xl_provider_dotnet");
        var owner = database.OwnerOf("xl_provider_dotnet");
        var authorization = SearchContractPostgresTests.Authorization(FleetScope);
        var store = new PostgresExperienceRecordStore(application);
        var clock = new FixedTimeProvider(Fixed.T0.AddTicks(1_234_560));
        var memory = new StubMemoryProvider();
        var wrapper = new ExperienceHoldoutContextProvider(memory, Key, new ExperienceHoldoutOptions { Rate = Rate }, store, _ => authorization, clock);
        var client = new StubChatClient();
        Guid nextRun = default;
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [wrapper] })
            .AsBuilder()
            .UseExperienceCapture(
                new InMemoryExperienceCaptureService(new DefaultSanitizer(new SanitizationOptions(new Dictionary<string, SanitizationPolicy>(StringComparer.Ordinal))), new CaptureLimits(10, 50, 10_000, 10_000)),
                new ExperienceCaptureOptions
                {
                    ResolveRun = _ => new ExperienceRunDescriptor("refund-ticket", FleetScope, "Triage a refund ticket", ContinuesRunId: nextRun),
                    CaptureToolCalls = false,
                })
            .Build();

        var runs = Enumerable.Range(1, Passes.Length).Select(n => Fixed.Id(F, 0x100 + n)).ToList();
        foreach (var run in runs)
        {
            nextRun = run;
            await agent.RunAsync($"Ticket {run}: a refund is stuck on a lock. Triage it.");
            var arm = HoldoutAssignmentFunction.Assign(run, ExperienceHoldoutContextProvider.ProviderSalt(Key, ExperienceHoldoutOptions.DefaultSalt), Rate);
            Assert.Equal(arm == HoldoutArm.Treatment, client.SawMemory);
        }

        await wrapper.RecordsSettledAsync();
        Assert.Equal((runs.Count, runs.Count), (memory.Invoking, memory.Invoked));

        // Each run's own record, as .NET finalization writes it: the outcome the estimate compares.
        var problems = new List<string>();
        for (var i = 0; i < runs.Count; i++)
        {
            var record = SearchContract.Seed(F, i + 1, "refund-ticket", "Triage a refund ticket", "Release the lock before retrying the refund.", scope: FleetScope);
            Assert.Equal(runs[i], record.SourceRunId);
            record = record with { Outcome = record.Outcome with { Status = Passes[i] ? TaskVerificationStatus.Verified : TaskVerificationStatus.Failed } };
            await PortAgentRowsPostgres.WriteAsDotNetAsync(application, record, actor: null, problems);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var rows = await DumpAsync(owner);
        Assert.Equal(runs.Count, rows[ProviderHoldoutStatements.Table]!.AsArray().Count);
        var text = ContractJson.Write(rows);
        if (ContractFiles.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RowsFile)!);
            await File.WriteAllTextAsync(RowsFile, text);
        }

        await application.DisposeAsync();
        await owner.DisposeAsync();
        Assert.True(File.Exists(RowsFile), $"{RowsFile} is missing; run with {ContractFiles.UpdateVariable}=1.");
        Assert.True(
            text == await File.ReadAllTextAsync(RowsFile),
            $"{RowsFile} is not what the .NET agent writes now; run with {ContractFiles.UpdateVariable}=1 and read the diff.");
    }

    [Fact]
    public async Task The_fleet_estimate_counts_every_run_in_its_arm_and_equals_every_port_s()
    {
        var application = await database.CreateAsync("xl_provider_fleet");
        var expected = new Dictionary<string, Counts>(StringComparer.Ordinal) { ["Treatment"] = new(), ["Holdout"] = new() };
        var salt = ExperienceHoldoutContextProvider.ProviderSalt(Key, ExperienceHoldoutOptions.DefaultSalt);
        var problems = new List<string>();
        var runs = 0;

        foreach (var (port, path) in RowsFiles)
        {
            Assert.True(File.Exists(path), $"{path} is missing.");
            var file = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            foreach (var table in Tables)
            {
                foreach (var row in Rows(file, table))
                {
                    await PortAgentRowsPostgres.InsertRawAsync(application, table, row);
                }
            }

            var records = Rows(file, "experience_records");
            var assignments = Rows(file, ProviderHoldoutStatements.Table);
            Assert.NotEmpty(assignments);
            var arms = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in assignments)
            {
                var runId = Guid.Parse(row["run_id"]!.GetValue<string>());
                var arm = row["arm"]!.GetValue<string>();
                var assigned = HoldoutAssignmentFunction.Assign(runId, salt, Rate).ToString();
                if (arm != assigned)
                {
                    problems.Add($"{port} run {runId}: recorded in {arm}, but the assignment function puts it in {assigned}.");
                }

                if (row["provider_key"]!.GetValue<string>() != Key)
                {
                    problems.Add($"{port} run {runId}: recorded under '{row["provider_key"]}', not '{Key}'.");
                }

                Assert.Equal(FleetScope, PortAgentRowsPostgres.ScopeOf(row));
                arms.Add(arm);
                runs++;
                var own = records.Where(record => record["source_run_id"]!.GetValue<string>() == row["run_id"]!.GetValue<string>()).ToList();
                if (own.Count != 1)
                {
                    problems.Add($"{port} run {runId}: {own.Count} records name it as their run, not 1: every measured run must be finalized.");
                    continue;
                }

                expected[arm].Add(JsonNode.Parse(own[0]["payload"]!.GetValue<string>())!["outcome"]!["status"]!.GetValue<string>());
            }

            if (!arms.SetEquals(["Treatment", "Holdout"]))
            {
                problems.Add($"{port}: its runs were assigned to [{string.Join(", ", arms)}], not to both arms.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));

        var services = new ServiceCollection();
        services.AddAgentExperience(options => options.ResolveIdentity = (_, _) => ValueTask.FromResult<ExperienceIdentity?>(null))
            .UsePostgres(application);
        await using var provider = services.BuildServiceProvider();
        var estimate = await AgentExperienceBenefit.EstimateProviderAsync(provider, SearchContractPostgresTests.Authorization(FleetScope), FleetScope, Key);

        Assert.Equal((ExperienceStoreOutcome.Found, true), (estimate.Outcome, estimate.Tracked));
        var overall = estimate.Overall;
        Assert.Equal((0, 0), (overall.Pending, overall.MixedArm));
        Assert.Equal(expected["Treatment"].Unverified + expected["Holdout"].Unverified, overall.Unverified);
        Assert.Equal(new ExperienceBenefitArm(expected["Treatment"].Runs, expected["Treatment"].Successes), overall.Treatment);
        Assert.Equal(new ExperienceBenefitArm(expected["Holdout"].Runs, expected["Holdout"].Successes), overall.Holdout);
        Assert.Equal(runs, overall.Treatment.Runs + overall.Holdout.Runs + overall.Unverified);

        await application.DisposeAsync();
        var dotnet = ProviderBenefitEstimateContract.EstimateJson(estimate);
        dotnet["tracked"] = estimate.Tracked;
        dotnet["outcome"] = estimate.Outcome.ToString();
        foreach (var (port, path) in EstimateFiles)
        {
            Assert.True(File.Exists(path), $"{path} is missing.");
            var computed = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var difference = Difference(dotnet, computed, "$");
            Assert.True(difference is null, $"{port}'s estimate of the fleet differs from .NET's at {difference}: .NET {dotnet.ToJsonString()}.");
        }
    }

    /// <summary>Every row of the fleet's tables, each column in PostgreSQL's text form in UTC, read as the owner, in key order.</summary>
    private static async Task<JsonObject> DumpAsync(NpgsqlDataSource owner)
    {
        var dump = new JsonObject();
        foreach (var table in Tables)
        {
            var columns = (await PortAgentRowsPostgres.ColumnsAsync(owner, table)).Where(column => !Derived.Contains(column)).ToList();
            var order = table switch
            {
                "experience_records" => "experience_id",
                "lifecycle_events" => "event_id",
                _ => "run_id, provider_key",
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

    private static double Number(JsonValue value) =>
        double.Parse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The first place two estimates differ, or <see langword="null"/>: numbers within 1e-12, everything else exactly.</summary>
    private static string? Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (null, null):
                return null;
            case (JsonObject e, JsonObject a):
                if (!e.Select(p => p.Key).Order(StringComparer.Ordinal).SequenceEqual(a.Select(p => p.Key).Order(StringComparer.Ordinal)))
                {
                    return $"{path} (members)";
                }

                return e.Select(p => Difference(p.Value, a[p.Key], $"{path}.{p.Key}")).FirstOrDefault(d => d is not null);
            case (JsonArray e, JsonArray a):
                return e.Count != a.Count ? $"{path} (length)" : e.Select((item, i) => Difference(item, a[i], $"{path}[{i}]")).FirstOrDefault(d => d is not null);
            case (JsonValue e, JsonValue a) when e.GetValueKind() == System.Text.Json.JsonValueKind.Number && a.GetValueKind() == System.Text.Json.JsonValueKind.Number:
                return Math.Abs(Number(e) - Number(a)) <= 1e-12 ? null : path;
            default:
                return JsonNode.DeepEquals(expected, actual) ? null : path;
        }
    }

    private static List<JsonObject> Rows(JsonObject rows, string table) =>
        rows[table] is JsonArray array ? [.. array.Select(row => row!.AsObject())] : throw new Xunit.Sdk.XunitException($"rows file: {table} is not an array.");

    /// <summary>One arm's expected counts: runs with a verified or failed outcome, the verified ones, and the unknown ones.</summary>
    private sealed class Counts
    {
        public int Runs { get; private set; }

        public int Successes { get; private set; }

        public int Unverified { get; private set; }

        public void Add(string status)
        {
            switch (status)
            {
                case nameof(TaskVerificationStatus.Verified):
                    Runs++;
                    Successes++;
                    break;
                case nameof(TaskVerificationStatus.Failed):
                    Runs++;
                    break;
                default:
                    Unverified++;
                    break;
            }
        }
    }

    /// <summary>A memory provider that remembers one thing about every customer, and counts its two hooks.</summary>
    private sealed class StubMemoryProvider : AIContextProvider
    {
        private int _invoking;
        private int _invoked;

        public int Invoking => Volatile.Read(ref _invoking);

        public int Invoked => Volatile.Read(ref _invoked);

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoking);
            return ValueTask.FromResult(new AIContext { Messages = [new ChatMessage(ChatRole.User, "Memory: the customer was refunded twice before.")] });
        }

        protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invoked);
            return default;
        }
    }

    /// <summary>A model that answers at once, and remembers whether it was shown the memory.</summary>
    private sealed class StubChatClient : IChatClient
    {
        public bool SawMemory { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            SawMemory = messages.Any(message => message.Text.StartsWith("Memory:", StringComparison.Ordinal));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Released the lock; the refund went through.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
