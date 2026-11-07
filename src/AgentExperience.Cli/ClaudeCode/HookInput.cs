using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentExperience.Cli.ClaudeCode;

/// <summary>
/// What a Claude Code hook receives on standard input (the hooks reference's common fields and the event's own), read
/// as data. Nothing here decides identity, scope, storage or what runs: the session ID only picks which open run the
/// event belongs to, and <c>cwd</c>, <c>transcript_path</c> and the rest are ignored.
/// </summary>
internal sealed record HookInput
{
    /// <summary>The most input a hook reads; a larger one is refused (and logged) rather than parsed.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    public required string HookEventName { get; init; }

    public required string SessionId { get; init; }

    /// <summary>SessionStart: <c>startup</c>, <c>resume</c>, <c>clear</c>, <c>compact</c> or <c>fork</c>.</summary>
    public string? Source { get; init; }

    /// <summary>UserPromptSubmit: the prompt as the user typed it.</summary>
    public string? Prompt { get; init; }

    public string? ToolName { get; init; }

    public JsonObject? ToolInput { get; init; }

    /// <summary>PostToolUse: the tool's response (for Bash an object with <c>stdout</c>, <c>stderr</c>, <c>interrupted</c>).</summary>
    public JsonNode? ToolResponse { get; init; }

    /// <summary>PostToolUseFailure: the error, for Bash starting <c>Exit code N</c>.</summary>
    public string? Error { get; init; }

    /// <summary>PostToolUseFailure: the failure was an abort, not the tool's own verdict.</summary>
    public bool IsInterrupt { get; init; }

    /// <summary>Stop: Claude Code is already continuing because of a stop hook.</summary>
    public bool StopHookActive { get; init; }

    /// <summary>SessionEnd: why the session ended.</summary>
    public string? Reason { get; init; }

    /// <summary>Parses the hook input; a <see cref="FormatException"/> with a fixed message when it is not one.</summary>
    public static HookInput Parse(ReadOnlySpan<byte> json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new FormatException("the hook input is not JSON");
        }

        if (node is not JsonObject input)
        {
            throw new FormatException("the hook input is not a JSON object");
        }

        return new HookInput
        {
            HookEventName = Text(input, "hook_event_name") ?? throw new FormatException("the hook input has no hook_event_name"),
            SessionId = Text(input, "session_id") ?? throw new FormatException("the hook input has no session_id"),
            Source = Text(input, "source"),
            Prompt = Text(input, "prompt"),
            ToolName = Text(input, "tool_name"),
            ToolInput = input["tool_input"] as JsonObject,
            ToolResponse = input["tool_response"]?.DeepClone(),
            Error = Text(input, "error"),
            IsInterrupt = Flag(input, "is_interrupt"),
            StopHookActive = Flag(input, "stop_hook_active"),
            Reason = Text(input, "reason"),
        };
    }

    /// <summary>
    /// A PostToolUse response's outcome, as the CLI records a call: failed when the response says so (an error flag, an
    /// interruption, or a non-zero exit code field), with an error text whose class the library keeps; otherwise the
    /// result text (Bash's standard output, else the response as JSON).
    /// </summary>
    public (string? Result, string? Error, bool Interrupted) ToolOutcome()
    {
        switch (ToolResponse)
        {
            case null:
                return (null, null, false);
            case JsonValue value when value.TryGetValue<string>(out var text):
                return (text, null, false);
            case JsonObject response:
                var interrupted = Flag(response, "interrupted");
                var code = Integer(response, "exit_code") ?? Integer(response, "exitCode") ?? Integer(response, "returnCode") ?? Integer(response, "return_code");
                var failed = interrupted || Flag(response, "is_error") || Flag(response, "isError") || code is not null and not 0;
                if (!failed)
                {
                    return (Text(response, "stdout") ?? response.ToJsonString(), null, false);
                }

                var detail = Text(response, "error") ?? Text(response, "stderr");
                var error = interrupted ? "interrupted"
                    : code is { } exit ? "Exit code " + exit.ToString(CultureInfo.InvariantCulture) + (detail is null ? string.Empty : "\n" + detail)
                    : detail ?? "the tool reported an error";
                return (null, error, interrupted);
            default:
                return (ToolResponse.ToJsonString(), null, false);
        }
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static long? Integer(JsonObject node, string name) =>
        node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var number) ? number : null;
}
