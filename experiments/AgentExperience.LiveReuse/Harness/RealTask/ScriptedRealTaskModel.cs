using System.Globalization;
using System.Text.RegularExpressions;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Extensions.AI;

namespace AgentExperience.LiveReuse.Harness;

/// <summary>How the scripted stand-in for a model behaves in the real-task experiment. Each one proves one property of the harness.</summary>
public enum ScriptedRealTaskBehavior
{
    /// <summary>Goes to the file the first record of an injected block names, and explores when that does not fix the service.</summary>
    FollowsBlock,

    /// <summary>Never reads the block: the harness must then show no benefit in any comparison.</summary>
    IgnoresBlock,

    /// <summary>Never calls a tool: every trial fails, in every condition.</summary>
    NeverActs,
}

/// <summary>
/// A deterministic, offline <see cref="IChatClient"/> that plays the engineer. Used by the harness's tests and by
/// <c>--scripted</c>, never by a live run, and never presented as a model: every report it produces says so.
/// </summary>
/// <remarks>
/// It reads only what a real model would be sent -- the ticket, the harness's work log of what its calls returned, and an
/// injected block -- never the task set. It does know how to write the fix once it has found the file (the reference
/// helper for that feature): the scripted run measures how many steps the harness's conditions cost it, not whether a
/// model could write the patch. Without a block it explores in a fixed order; with one it goes to the first path the
/// block names.
/// </remarks>
public sealed partial class ScriptedRealTaskModel(ScriptedRealTaskBehavior behavior = ScriptedRealTaskBehavior.FollowsBlock) : IChatClient
{
    public const string ModelId = "scripted-engineer";

    private static readonly Dictionary<string, string> FeatureOfTest = new(StringComparer.Ordinal)
    {
        ["test_checkout_total"] = "pricing",
        ["test_product_url"] = "catalog",
        ["test_prorated_charge"] = "proration",
        ["test_batch_count"] = "batching",
        ["test_unique_accounts"] = "accounts",
        ["test_completion_label"] = "reports",
    };

    private int _sequence;

    /// <summary>The messages of every call, in order, for tests that compare what the conditions were shown.</summary>
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages.ToList();
        Calls.Add(list);

        var usage = new UsageDetails { InputTokenCount = list.Sum(message => (long)message.Text.Length) / 4 + 100, OutputTokenCount = 20 };
        ChatResponse Respond(ChatMessage message) => new(message) { ModelId = ModelId, Usage = usage };

        if (behavior == ScriptedRealTaskBehavior.NeverActs)
        {
            return Task.FromResult(Respond(new ChatMessage(ChatRole.Assistant, "Where should I look?")));
        }

        var log = string.Join("\n", list.Select(message => message.Text).Where(text => text.Contains(RealWorkLog.Heading, StringComparison.Ordinal)));
        var block = list.Select(message => message.Text).FirstOrDefault(text => text.Contains(HistoricalReferenceWriter.BlockBegin, StringComparison.Ordinal));
        var calls = CallLine().Matches(log).Select(match => (Tool: match.Groups[1].Value, Path: PathArgument().Match(match.Groups[2].Value) is { Success: true } path ? path.Groups[1].Value : null)).ToList();

        var next = Next(calls, log, behavior == ScriptedRealTaskBehavior.IgnoresBlock ? null : block);
        return Task.FromResult(Respond(next is null
            ? new ChatMessage(ChatRole.Assistant, "I could not find the cause.")
            : Call(next.Value.Tool, next.Value.Arguments)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static (string Tool, Dictionary<string, object?> Arguments)? Next(List<(string Tool, string? Path)> calls, string log, string? block)
    {
        (string, Dictionary<string, object?>) Read(string path) => (RealTaskEnvironment.ReadToolName, new() { ["path"] = path });
        (string, Dictionary<string, object?>) Write(string path, string feature) => (RealTaskEnvironment.WriteToolName, new() { ["path"] = path, ["content"] = RealTaskSet.FixedHelpers(feature) });
        (string, Dictionary<string, object?>) Test() => (RealTaskEnvironment.TestToolName, new());
        bool Done(string tool, string? path = null) => calls.Any(call => call.Tool == tool && (path is null || call.Path == path));

        var ranTests = Done(RealTaskEnvironment.TestToolName);
        var blockPath = block is null ? null : HelperPath().Match(block) is { Success: true } hit ? hit.Value : null;

        if (!ranTests)
        {
            if (blockPath is not null)
            {
                var feature = blockPath.Split('/')[1];
                return !Done(RealTaskEnvironment.ReadToolName, blockPath) ? Read(blockPath)
                    : !Done(RealTaskEnvironment.WriteToolName, blockPath) ? Write(blockPath, feature)
                    : Test();
            }

            return !Done(RealTaskEnvironment.ListToolName) ? (RealTaskEnvironment.ListToolName, new())
                : !Done(RealTaskEnvironment.ReadToolName, "tests/test_service.py") ? Read("tests/test_service.py")
                : Test();
        }

        // The tests ran and the service is not fixed: find the failing feature in the first output that names one.
        var failing = FailingTest().Match(log) is { Success: true } failure && FeatureOfTest.TryGetValue(failure.Groups[1].Value, out var named) ? named : null;
        if (failing is null)
        {
            return null;
        }

        var helpers = "app/" + failing + "/helpers.py";
        string[] reads = ["app/service.py", "app/" + failing + "/api.py", helpers, "vendor/CHANGELOG.md"];
        foreach (var path in reads)
        {
            if (!Done(RealTaskEnvironment.ReadToolName, path))
            {
                return Read(path);
            }
        }

        // A write after the last test run that is already the fix would be a repeat; one more run is all that is left.
        var lastTest = calls.FindLastIndex(call => call.Tool == RealTaskEnvironment.TestToolName);
        var wroteAfterLastTest = calls.Skip(lastTest + 1).Any(call => call.Tool == RealTaskEnvironment.WriteToolName && call.Path == helpers);
        return wroteAfterLastTest ? Test() : Write(helpers, failing);
    }

    private ChatMessage Call(string tool, Dictionary<string, object?> arguments) =>
        new(ChatRole.Assistant, [new FunctionCallContent("scripted-" + (++_sequence).ToString(CultureInfo.InvariantCulture), tool, arguments)]);

    [GeneratedRegex(@"^\d+\. You called (\w+)\((.*)\) ->", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex CallLine();

    [GeneratedRegex(@"path=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex PathArgument();

    [GeneratedRegex(@"app/[a-z]+/helpers\.py", RegexOptions.CultureInvariant)]
    private static partial Regex HelperPath();

    [GeneratedRegex(@"(?:FAIL|ERROR): (test_\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex FailingTest();
}
