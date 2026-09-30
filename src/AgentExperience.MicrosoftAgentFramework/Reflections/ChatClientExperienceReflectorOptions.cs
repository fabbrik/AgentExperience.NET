using Microsoft.Extensions.AI;

namespace AgentExperience.MicrosoftAgentFramework.Reflections;

/// <summary>
/// How <see cref="ChatClientExperienceReflector"/> calls the model and how much captured content it sends.
/// Every value is checked when it is set, and the combination again when the reflector is constructed.
/// </summary>
public sealed class ChatClientExperienceReflectorOptions
{
    /// <summary>The default <see cref="Timeout"/>: 30 seconds.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The default <see cref="MaxQuotedLength"/>: 500 characters.</summary>
    public const int DefaultMaxQuotedLength = 500;

    /// <summary>The default <see cref="MaxPromptLength"/>: 16,000 characters.</summary>
    public const int DefaultMaxPromptLength = 16_000;

    /// <summary>The default <see cref="MaxOutputTokens"/>: 2,048.</summary>
    public const int DefaultMaxOutputTokens = 2_048;

    /// <summary>The smallest <see cref="MaxPromptLength"/> accepted: 1,000 characters.</summary>
    public const int MinPromptLength = 1_000;

    /// <summary>
    /// The model to ask for, set as <see cref="ChatOptions.ModelId"/>, or <see langword="null"/> to use the
    /// client's own default. Also the model named in <c>Producer</c> when the response names none.
    /// </summary>
    public string? ModelName
    {
        get;
        set => field = value is null || !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("ModelName must be null or non-blank.", nameof(value));
    }

    /// <summary>
    /// How long one model call may take. A call still running then fails the reflection with
    /// <see cref="ReflectionFailureKind.TimedOut"/>; the caller's own cancellation still propagates.
    /// Strictly positive and finite. Defaults to <see cref="DefaultTimeout"/>.
    /// </summary>
    public TimeSpan Timeout
    {
        get;
        set => field = value > TimeSpan.Zero && value.TotalMilliseconds <= int.MaxValue
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Timeout must be strictly positive and at most int.MaxValue milliseconds.");
    } = DefaultTimeout;

    /// <summary>
    /// The most characters of any one piece of captured text -- the task text, a result, an error -- the
    /// prompt quotes; longer text is clipped, ending in an ellipsis. At least 16. Defaults to
    /// <see cref="DefaultMaxQuotedLength"/>.
    /// </summary>
    public int MaxQuotedLength
    {
        get;
        set => field = value >= 16
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxQuotedLength must be at least 16.");
    } = DefaultMaxQuotedLength;

    /// <summary>
    /// The most characters of the data message sent to the model (the published
    /// <see cref="ChatClientExperienceReflector.SystemPrompt"/> is sent as well, and not counted). The
    /// oldest attempts are left out first to fit, and the message says how many were. At least
    /// <see cref="MinPromptLength"/> and greater than <see cref="MaxQuotedLength"/>. Defaults to
    /// <see cref="DefaultMaxPromptLength"/>.
    /// </summary>
    public int MaxPromptLength
    {
        get;
        set => field = value >= MinPromptLength
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"MaxPromptLength must be at least {MinPromptLength}.");
    } = DefaultMaxPromptLength;

    /// <summary>
    /// The sampling temperature, set as <see cref="ChatOptions.Temperature"/>, or <see langword="null"/> to
    /// leave it to the provider (some models accept none). Finite and not negative. Defaults to 0, the
    /// most repeatable; a model-backed reflection is never deterministic, though.
    /// </summary>
    public float? Temperature
    {
        get;
        set => field = value is null || (float.IsFinite(value.Value) && value.Value >= 0)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Temperature must be null, or finite and not negative.");
    } = 0f;

    /// <summary>
    /// The most tokens the model may answer with, set as <see cref="ChatOptions.MaxOutputTokens"/> on every call
    /// (after <see cref="ConfigureChatOptions"/>, which cannot raise it). Strictly positive. Defaults to
    /// <see cref="DefaultMaxOutputTokens"/>. Independently of it, a text answer over
    /// <see cref="ChatClientExperienceReflector.MaxAnswerBytes"/> is refused unread.
    /// </summary>
    public int MaxOutputTokens
    {
        get;
        set => field = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxOutputTokens must be strictly positive.");
    } = DefaultMaxOutputTokens;

    /// <summary>
    /// Applied to a fresh <see cref="ChatOptions"/> of every call after the reflector set its own
    /// (<see cref="ChatOptions.ResponseFormat"/>, <see cref="ChatOptions.ModelId"/>,
    /// <see cref="ChatOptions.Temperature"/>), so a host can add provider settings. The reflector then
    /// re-asserts that no tools are offered (<see cref="ChatOptions.Tools"/> <see langword="null"/>,
    /// <see cref="ChatOptions.ToolMode"/> <see cref="ChatToolMode.None"/>) and the <see cref="MaxOutputTokens"/>
    /// cap, so the callback cannot turn tools on or raise the cap. A callback that throws fails the reflection
    /// (<see cref="ReflectionFailureKind.ChatOptionsCallbackFailed"/>) before anything is sent. Anything else it
    /// changes is the host's responsibility: a response format the model then ignores fails as unparseable.
    /// </summary>
    public Action<ChatOptions>? ConfigureChatOptions { get; set; }

    /// <summary>A copy, so a reflector never sees later changes to the instance it was built from.</summary>
    internal ChatClientExperienceReflectorOptions Snapshot()
    {
        if (MaxQuotedLength >= MaxPromptLength)
        {
            throw new ArgumentException("MaxQuotedLength must be less than MaxPromptLength.", "options");
        }

        return new ChatClientExperienceReflectorOptions
        {
            ModelName = ModelName,
            Timeout = Timeout,
            MaxQuotedLength = MaxQuotedLength,
            MaxPromptLength = MaxPromptLength,
            Temperature = Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ConfigureChatOptions = ConfigureChatOptions,
        };
    }
}
