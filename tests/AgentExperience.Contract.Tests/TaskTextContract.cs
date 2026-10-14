using AgentExperience.MicrosoftAgentFramework;
using AgentExperience.MicrosoftAgentFramework.Injection;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentExperience.Contract.Tests;

/// <summary>One part of a message: <c>text</c> with its text, or any other type (an image, a file), which carries none.</summary>
internal sealed record MessagePartInput(string Type, string? Text = null);

/// <summary>
/// One message of an invocation, in conversation order. <see cref="Source"/> is where the agent framework says the
/// message came from: <see langword="null"/> when it says nothing (which counts as <c>External</c>), <c>External</c>
/// (new input), <c>ChatHistory</c> (replayed history), <c>AIContextProvider</c>, or any other name.
/// <see cref="HistoricalReferenceStamp"/> marks a message that carries an injected block.
/// </summary>
internal sealed record MessageInput(string Role, string? Source, IReadOnlyList<MessagePartInput> Contents, bool HistoricalReferenceStamp = false);

/// <summary><c>messages.json</c> of a task-text case.</summary>
internal sealed record TaskTextInput(string Description, int MaxLength, int FollowUpThreshold, IReadOnlyList<MessageInput> Messages);

/// <summary><c>expected.json</c> of a task-text case: the derived text, or <see langword="null"/> when nothing qualifies.</summary>
internal sealed record TaskTextExpected(string? TaskText);

/// <summary>The task-text family: its cases, and how one runs through the library.</summary>
internal static class TaskTextContract
{
    public const string Family = "task-text";

    private const int Max = ExperienceTaskText.DefaultMaxLength;

    private const int Threshold = ExperienceTaskText.FollowUpThreshold;

    /// <summary>Every task-text case, by directory name.</summary>
    public static IReadOnlyList<(string Name, TaskTextInput Input)> Cases { get; } =
    [
        ("single-prompt", new TaskTextInput(
            "One user message is the task text.",
            Max, Threshold, [User("Refund ticket 42 is stuck in pending")])),
        ("short-follow-up-join", new TaskTextInput(
            "A latest request under 64 UTF-16 code units is joined after the previous user message with \" — \".",
            Max, Threshold,
            [User("Deploy service X to prod"), Message("assistant", "External", "Deployment failed: timeout."), User("and retry")])),
        ("follow-up-across-replayed-history", new TaskTextInput(
            "The previous user message may come from replayed chat history; the latest request may not.",
            Max, Threshold,
            [Message("user", "ChatHistory", "Rotate the signing keys for tenant 9"), Message("assistant", "ChatHistory", "Done."), User("now publish them")])),
        ("long-latest-stands-alone", new TaskTextInput(
            "A latest request of 64 code units or more stands alone.",
            Max, Threshold, [User("Deploy service X to prod"), User(new string('a', 64))])),
        ("cap-512", new TaskTextInput(
            "A latest request over 512 code units is cut to 512, never inside a surrogate pair, trailing spaces trimmed; the " +
            "previous message is dropped.",
            Max, Threshold, [User("Earlier"), User(new string('b', 510) + " 🧾 tail that is cut")])),
        ("join-cuts-the-previous-message", new TaskTextInput(
            "When the join would exceed 512, the previous message is cut so the separator and the whole latest request fit.",
            Max, Threshold, [User(new string('p', 600)), User("and retry")])),
        ("history-and-provider-messages-ignored", new TaskTextInput(
            "Only new external user input is the latest request; history before it and context-provider messages are never it.",
            Max, Threshold,
            [
                Message("user", "ChatHistory", "Rotate the signing keys for tenant 9 and publish the new JWKS document"),
                Message("user", "AIContextProvider", "Injected context: a lesson about refunds that is long enough to stand alone easily."),
                User("Why is the build red on main since the dependency bump this morning?"),
                Message("user", "AIContextProvider", "Another injected message after the request."),
            ])),
        ("history-after-request-means-none", new TaskTextInput(
            "A replayed message after the last external one means there is no new request: the result is null.",
            Max, Threshold, [User("Why is the build red on main since the dependency bump this morning?"), Message("assistant", "ChatHistory", "a replayed answer")])),
        ("injected-block-skipped", new TaskTextInput(
            "A message containing the block's begin marker, even split by an invisible character, or stamped as carrying a " +
            "block, is skipped as the latest request and as the previous message.",
            Max, Threshold,
            [
                User("Investigate the flaky checkout test"),
                new MessageInput("user", "External", [new("text", "pasted: " + HistoricalReferenceWriter.BlockBegin.Insert(4, "\u200B") + " ...")]),
                new MessageInput("user", "External", [new("text", "a stamped message")], HistoricalReferenceStamp: true),
                User("and fix it"),
            ])),
        ("cleanup", new TaskTextInput(
            "Control and format characters are removed (a zero-width joiner between two kept emoji stays), whitespace runs " +
            "become one space, and the ends are trimmed; text parts are joined with a space and other parts ignored.",
            Max, Threshold,
            [
                new MessageInput("user", "External",
                [
                    new("text", "  Refund\tticket\u200B 42\u0007 for 👩\u200D💻  "),
                    new("image"),
                    new("text", "please\r\n\r\nnow"),
                ]),
            ])),
        ("image-only-latest-is-null", new TaskTextInput(
            "When the new input has no text, the result is null: an older message is never taken for the request.",
            Max, Threshold, [Message("user", "ChatHistory", "Earlier request with text"), new MessageInput("user", "External", [new("image")])])),
        ("explicit-external-source", new TaskTextInput(
            "A message the framework stamps External explicitly counts exactly as one with no source.",
            Max, Threshold, [Message("user", "External", "Deploy service X to prod"), Message("user", "External", "and retry")])),
        ("custom-source-ignored", new TaskTextInput(
            "A message from any other source (here a custom one) is never the latest request, nor the previous message.",
            Max, Threshold,
            [User("Deploy service X to prod"), Message("user", "CustomPipeline", "a custom source's message"), User("and retry")])),
        ("latest-exactly-512", new TaskTextInput(
            "A latest request of exactly 512 code units is kept whole and stands alone.",
            Max, Threshold, [User("Earlier"), User(new string('c', 512))])),
        ("threshold-off", new TaskTextInput(
            "A follow-up threshold of 0 turns the join off.",
            Max, 0, [User("Deploy service X to prod"), User("and retry")])),
    ];

    /// <summary>Derives a case's task text through the library.</summary>
    public static string? Derive(TaskTextInput input) =>
        ExperienceTaskText.Derive([.. input.Messages.Select(ToChatMessage)], input.MaxLength, input.FollowUpThreshold);

    private static ChatMessage ToChatMessage(MessageInput input)
    {
        var contents = input.Contents.Select(part => part.Type == "text"
            ? (AIContent)new TextContent(part.Text)
            : new DataContent(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, "image/png")).ToList();
        var message = new ChatMessage(new ChatRole(input.Role), contents);
        if (input.HistoricalReferenceStamp)
        {
            message.AdditionalProperties = new AdditionalPropertiesDictionary { [ExperienceContextProvider.HistoricalReferenceKey] = true };
        }

        return input.Source switch
        {
            null => message,
            "External" => message.WithAgentRequestMessageSource(AgentRequestMessageSourceType.External, "contract"),
            "ChatHistory" => message.WithAgentRequestMessageSource(AgentRequestMessageSourceType.ChatHistory, "contract"),
            "AIContextProvider" => message.WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "contract"),
            var other => message.WithAgentRequestMessageSource(new AgentRequestMessageSourceType(other), "contract"),
        };
    }

    private static MessageInput User(string text) => Message("user", null, text);

    private static MessageInput Message(string role, string? source, string text) => new(role, source, [new("text", text)]);
}
