using System.Text.Json.Nodes;
using AgentExperience.Abstractions;
using AgentExperience.Core.Finalization;
using AgentExperience.Core.Reflections;
using AgentExperience.MicrosoftAgentFramework.Reflections;

namespace AgentExperience.Contract.Tests;

/// <summary>
/// Contract 1.14's reflection checks: every reflection-prompt fixture is what the .NET model-backed reflector sends when
/// finalization calls it, and every reflection-output fixture is what finalization stores from the scripted answer.
/// </summary>
public sealed partial class ContractFixtureTests
{
    [Fact]
    public async Task Every_reflection_prompt_fixture_is_what_the_model_backed_reflector_sends()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in ReflectionContract.PromptCases)
        {
            var directory = $"fixtures/{ReflectionContract.PromptFamily}/{name}";
            var read = WriteAndReadInputs(directory, definition, drift);
            var run = await ReflectionContract.RunAsync(read, captureRequest: true);
            Assert.NotNull(run.Messages);
            ContractFiles.Verify($"{directory}/expected-messages.json", ContractJson.Write(ReflectionContract.MessagesJson(run.Messages)), drift);
            ContractFiles.Verify($"{directory}/expected-options.json", ContractJson.Write(ReflectionContract.OptionsJson(run.Options!)), drift);
            ContractFiles.Verify($"{directory}/expected-shown.json", ContractJson.Write(new JsonArray([.. run.Shown!.Select(text => (JsonNode)text)])), drift);
        }

        ContractFiles.VerifyNoStray(ReflectionContract.PromptFamily, ReflectionContract.PromptCases.Select(entry => entry.Name), ReflectionContract.PromptFiles, drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public async Task Every_reflection_output_fixture_is_what_finalization_stores_from_the_answer()
    {
        var drift = new List<string>();
        foreach (var (name, definition) in ReflectionContract.OutputCases)
        {
            var directory = $"fixtures/{ReflectionContract.OutputFamily}/{name}";
            var read = WriteAndReadInputs(directory, definition, drift);
            var run = await ReflectionContract.RunAsync(read, captureRequest: false);
            ContractFiles.Verify($"{directory}/expected.json", ContractJson.Write(ReflectionContract.OutputJson(run.Result)), drift);
            ContractFiles.Verify($"{directory}/expected-record.json", ContractJson.Write(run.Record), drift);
            ContractFiles.Verify($"{directory}/expected-event.json", ContractJson.Write(run.Event), drift);
            if (ContractFiles.Read($"{directory}/expected-record.json") is { } stored)
            {
                RecordSchema.Check($"{directory}/expected-record.json", JsonNode.Parse(stored)!, drift);
            }
        }

        ContractFiles.VerifyNoStray(ReflectionContract.OutputFamily, ReflectionContract.OutputCases.Select(entry => entry.Name), ReflectionContract.OutputFiles, drift);
        Relock(drift);
        ContractFiles.AssertNoDrift(drift);
    }

    [Fact]
    public async Task The_reflection_output_cases_cover_every_failure_and_refusal()
    {
        var results = new Dictionary<string, FinalizeExperienceResult>();
        foreach (var (name, definition) in ReflectionContract.OutputCases)
        {
            results[name] = (await ReflectionContract.RunAsync(definition, captureRequest: false)).Result;
        }

        ReflectionFailureKind? Kind(string name) => (results[name].Failure?.Exception as ReflectionFailedException)?.Kind;
        ReflectionScreeningRefusal? Refusal(string name) => results[name].Failure?.ScreeningRefusal;

        foreach (var name in new[] { "valid-reply", "fenced-reply-crlf", "null-and-blank-items", "unknown-preconditions-full-list", "producer-model-sanitized", "link-from-the-run-passes" })
        {
            Assert.Equal(FinalizationOutcome.Validated, results[name].Outcome);
        }

        // Every failure kind finalization can reach (ChatOptionsCallbackFailed needs a host callback, which a port tests itself).
        Assert.Equal(
            Enum.GetValues<ReflectionFailureKind>().Where(kind => kind != ReflectionFailureKind.ChatOptionsCallbackFailed).Order(),
            results.Keys.Select(Kind).OfType<ReflectionFailureKind>().Distinct().Order());
        Assert.Equal(ReflectionFailureKind.TimedOut, Kind("timed-out"));
        Assert.Equal("System.InvalidOperationException", ((ReflectionFailedException)results["model-call-throws"].Failure!.Exception!).CauseType);

        Assert.Equal(ReflectionScreeningRefusal.UnsafeContent, Refusal("link-not-in-the-run"));
        Assert.Equal(ReflectionScreeningRefusal.UnsafeContent, Refusal("credential-in-lesson"));
        Assert.Equal(ReflectionScreeningRefusal.UnsafeContent, Refusal("instruction-override-across-items"));
        Assert.Equal(ReflectionScreeningRefusal.UnsafeContent, Refusal("mixed-script-word"));
        Assert.Equal(ReflectionScreeningRefusal.OverLimit, Refusal("lesson-over-limit"));
        Assert.All(results.Values.Where(result => result.Outcome == FinalizationOutcome.Quarantined), result => Assert.Equal(FinalizationStage.Reflect, result.Failure!.Stage));
    }

    private static ReflectionCase WriteAndReadInputs(string directory, ReflectionCase definition, List<string> drift)
    {
        ContractFiles.Verify($"{directory}/input.json", ContractJson.Write(definition.Input), drift);
        ContractFiles.Verify($"{directory}/reflector.json", ContractJson.Write(definition.Reflector), drift);
        ContractFiles.Verify($"{directory}/reply.json", ContractJson.Write(definition.Reply), drift);
        return new ReflectionCase(
            ContractJson.Read<FinalizeInput>(ContractFiles.Read($"{directory}/input.json") ?? ContractJson.Write(definition.Input)),
            ContractJson.Read<ReflectorOptionsInput>(ContractFiles.Read($"{directory}/reflector.json") ?? ContractJson.Write(definition.Reflector)),
            ContractJson.Read<ScriptedReply>(ContractFiles.Read($"{directory}/reply.json") ?? ContractJson.Write(definition.Reply)));
    }
}
