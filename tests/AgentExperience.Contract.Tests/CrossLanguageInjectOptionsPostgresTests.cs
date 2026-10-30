using System.Globalization;
using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Retrieval;
using AgentExperience.MicrosoftAgentFramework.Injection;
using AgentExperience.Storage.Postgres;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Story 27.4b's cross-language proof, the .NET half: agents configured with the same receiving agent and the same
/// model-authored policy inject the same block, with the same omissions, from the same rows in every language. The seed
/// is inserted as a search seed is (the application role), and a real <see cref="ExperienceContextProvider"/> over the
/// .NET PostgreSQL candidate source and record store injects into a <see cref="ChatClientAgent"/> run on the task text,
/// once per configuration in <see cref="Agents"/>. The seed, the configurations and what .NET injected must be exactly
/// the checked-in <see cref="File"/> (rewritten with the update switch). The Python and Go ports' PostgreSQL tests
/// insert the same seed into their own database and run their own agent with each configuration: each must inject the
/// same block, byte for byte, with the same outcome and omissions.
/// </summary>
[Collection(ContractPostgresCollection.Name)]
public sealed class CrossLanguageInjectOptionsPostgresTests(ContractPostgresDatabase database)
{
    /// <summary>The seed's scope.</summary>
    internal static readonly Scope SeedScope = new("cross-language-inject", "inject-app", "inject-project", AgentId: "inject-agent");

    private const int F = 0xe2;

    private const string TaskText = "Refund ticket handling for a refund stuck on the ledger lock";

    private static readonly DateTimeOffset Now = Fixed.T0;

    private static readonly string File = Path.Combine(
        Path.GetDirectoryName(ContractFiles.Root)!, "tests", "AgentExperience.Contract.Tests", "cross-language", "dotnet-inject-options.json");

    /// <summary>The configurations every language's agent runs with, by name.</summary>
    private static IReadOnlyList<(string Name, InjectOptionsInput Options)> Agents { get; } =
    [
        ("risk-and-include", InjectOptionsInput.Default with
        {
            Description = "MaxRiskClass Medium over the host's risk classes; model-authored lessons included (fenced).",
            ReceivingAgent = new ReceivingAgentInput(null, ToolRiskClass.Medium, InjectOptionsContract.RiskClasses, UseRunTools: false, GatedRetrievalWindowMultiplier: 4),
        }),
        ("tools-and-exclude", InjectOptionsInput.Default with
        {
            Description = "AvailableTools read_ledger and wait_for_lock, three records at most, and model-authored lessons excluded.",
            MaxRecords = 3,
            ModelAuthoredLessons = ModelAuthoredLessonPolicy.Exclude,
            ReceivingAgent = new ReceivingAgentInput(["read_ledger", "wait_for_lock"], null, InjectOptionsContract.RiskClasses, UseRunTools: false, GatedRetrievalWindowMultiplier: 4),
        }),
    ];

    [Fact]
    public async Task The_same_options_inject_the_same_block_in_every_language()
    {
        ExperienceRecord Lesson(int n, string taskId, string lesson, string[][] attempts, bool modelAuthored = false, bool failFirst = false) =>
            InjectOptionsContract.Lesson(n, taskId, lesson, attempts, modelAuthored, failFirst, F, SeedScope);

        ExperienceRecord[] seed =
        [
            Lesson(1, "read-ledger", "Read the ledger before touching a refund ticket.", [["read_ledger"]]),
            InjectOptionsContract.Retried(2, F, SeedScope),
            Lesson(3, "issue-refund", "Issue the refund once the ledger agrees with the ticket.", [["read_ledger", "issue_refund"]]),
            Lesson(4, "drop-ledger", "Drop the stale ledger lock and rebuild it.", [["drop_ledger"]]),
            Lesson(5, "explain-refund", "Explain the refund handling policy; no tool is needed.", [[]]),
            Lesson(6, "rebuild-ledger", "Rebuilding the ledger failed; reading the ledger was enough.", [["drop_ledger"], ["read_ledger"]], failFirst: true),
            Lesson(7, "model-refund", "A model wrote this lesson about the refund ledger.", [["read_ledger"]], modelAuthored: true),
            Lesson(8, "model-drop", "A model wrote this lesson about dropping the ledger lock.", [["drop_ledger"]], modelAuthored: true),
        ];

        var application = await database.CreateAsync("xl_inject_options");
        foreach (var record in seed)
        {
            await SearchContractPostgresTests.InsertAsync(application, ContractJson.ToStored(record));
        }

        var clock = new FixedTimeProvider(Now);
        var authorization = SearchContractPostgresTests.Authorization(SeedScope);
        var agents = new JsonArray();
        var outcomes = new List<(ExperienceInjectionResult Result, string? Block)>();
        foreach (var (name, options) in Agents)
        {
            ExperienceInjectionResult? result = null;
            var injection = InjectOptionsContract.Options(
                options,
                context => context.DerivedTaskText is { } text ? new RetrieveExperienceRequest(authorization, SeedScope, text) : null,
                clock);
            injection.OnContextInjected = injected => result = injected;
            var provider = new ExperienceContextProvider(
                new ExperienceRetrievalService(new PostgresExperienceCandidateSource(application), RetrievalPolicy.Default, RankingWeights.Default, clock),
                new PostgresExperienceRecordStore(application),
                injection);
            var client = new RecordingChatClient();
            var agent = new ChatClientAgent(client, new ChatClientAgentOptions { AIContextProviders = [provider] });
            await agent.RunAsync(TaskText);

            Assert.NotNull(result);
            var block = client.Seen
                .SingleOrDefault(message => message.AdditionalProperties?.ContainsKey(ExperienceContextProvider.HistoricalReferenceKey) == true);
            outcomes.Add((result, block?.Text));
            agents.Add(new JsonObject
            {
                ["name"] = name,
                ["options"] = JsonNode.Parse(ContractJson.Write(options)),
                ["expected"] = new JsonObject
                {
                    ["outcome"] = result.Outcome.ToString(),
                    ["messageRole"] = block is null ? null : (block.Role == ChatRole.System ? "System" : "User"),
                    ["injected"] = new JsonArray([.. result.InjectedExperienceIds.Select(id => (JsonNode)Id(id))]),
                    ["byteCount"] = result.PayloadBytes,
                    ["omitted"] = new JsonArray([.. result.Omitted.Select(entry => (JsonNode)new JsonObject
                    {
                        ["experienceId"] = Id(entry.ExperienceId),
                        ["reason"] = entry.Reason.ToString(),
                        ["detail"] = entry.Detail,
                    })]),
                    ["excluded"] = new JsonArray([.. result.Excluded.Select(entry => (JsonNode)new JsonObject
                    {
                        ["experienceId"] = Id(entry.ExperienceId),
                        ["reason"] = entry.Reason.ToString(),
                    })]),
                    ["block"] = block?.Text,
                },
            });
        }

        await application.DisposeAsync();

        // The options did something: the risk gate and the tool gate both omitted, a model-authored lesson was fenced
        // under Include, and none reached the block under Exclude.
        Assert.All(outcomes, entry => Assert.Equal(InjectionOutcome.Injected, entry.Result.Outcome));
        Assert.Contains(outcomes[0].Result.Omitted, entry => entry.Reason == InjectionOmissionReason.RiskClassExceeded);
        Assert.Contains(HistoricalReferenceWriter.ModelAuthoredLine, outcomes[0].Block, StringComparison.Ordinal);
        Assert.Contains(outcomes[1].Result.Omitted, entry => entry.Reason == InjectionOmissionReason.ToolUnavailable);
        Assert.DoesNotContain(HistoricalReferenceWriter.ModelAuthoredLine, outcomes[1].Block, StringComparison.Ordinal);

        var file = new JsonObject
        {
            ["description"] =
                "Story 27.4b: insert seed as a search seed is inserted, and for each agent run the port's own agent, over the " +
                "port's own PostgreSQL store, at now, with identity scope and the agent's options, on one user message, " +
                "taskText. Its injection must be expected's: the outcome, the message role, the injected IDs in order, the " +
                "byte count, the omissions in order with their reasons and details, retrieval's exclusions and the block, " +
                "byte for byte.",
            ["scope"] = JsonNode.Parse(ContractJson.Write(SeedScope)),
            ["taskText"] = TaskText,
            ["now"] = ContractJson.Timestamp(Now),
            ["seed"] = SearchContract.ToSeedJson(seed),
            ["agents"] = agents,
        };

        var text = ContractJson.Write(file);
        if (ContractFiles.Updating)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            await System.IO.File.WriteAllTextAsync(File, text);
        }

        Assert.True(System.IO.File.Exists(File), $"{File} is missing; run with {ContractFiles.UpdateVariable}=1.");
        Assert.True(
            text == await System.IO.File.ReadAllTextAsync(File),
            $"{File} is not what .NET injects now; run with {ContractFiles.UpdateVariable}=1 and read the diff.");
    }

    private static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>A model that answers at once, and keeps every message it was sent.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatMessage> Seen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Read the ledger; the refund went through.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
