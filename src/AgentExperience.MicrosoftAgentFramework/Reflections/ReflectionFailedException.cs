namespace AgentExperience.MicrosoftAgentFramework.Reflections;

/// <summary>Why <see cref="ChatClientExperienceReflector"/> produced no reflection.</summary>
public enum ReflectionFailureKind
{
    /// <summary>The model call threw, or cancelled itself while neither the caller nor the timeout had.</summary>
    ModelCallFailed,

    /// <summary>The model did not answer within <see cref="ChatClientExperienceReflectorOptions.Timeout"/>.</summary>
    TimedOut,

    /// <summary>The model's answer held no JSON object of the requested shape.</summary>
    Unparseable,

    /// <summary>The model's answer was readable, but its lesson was missing or blank.</summary>
    EmptyLesson,

    /// <summary>The response carried a function call or a function result: the model tried to use a tool, which a reflection never may.</summary>
    ToolCallAttempted,

    /// <summary>The model's text answer was longer than <see cref="ChatClientExperienceReflector.MaxAnswerBytes"/> UTF-8 bytes, so it was not read.</summary>
    OutputTooLarge,

    /// <summary>Not even the smallest data message fits <see cref="ChatClientExperienceReflectorOptions.MaxPromptLength"/>, so nothing was sent.</summary>
    PromptTooLarge,

    /// <summary>The host's <see cref="ChatClientExperienceReflectorOptions.ConfigureChatOptions"/> callback threw, so nothing was sent.</summary>
    ChatOptionsCallbackFailed,
}

/// <summary>
/// Thrown by <see cref="ChatClientExperienceReflector"/> when the model gave it nothing it can turn into a
/// reflection. Finalization handles it like any reflector that threw: the record is kept, quarantined,
/// with no lesson.
/// </summary>
/// <remarks>
/// The message is fixed text chosen by <see cref="Kind"/>: it never carries model output, prompt text,
/// or the message of the exception that caused it, and that exception is not attached as
/// <see cref="Exception.InnerException"/> (a provider's exception can quote the response body). Only its
/// type name is kept, in <see cref="CauseType"/>.
/// </remarks>
public sealed class ReflectionFailedException : Exception
{
    /// <summary>Creates the exception for <paramref name="kind"/>.</summary>
    /// <param name="kind">Why no reflection was produced.</param>
    /// <param name="causeType">The full type name of the exception behind the failure, if one was thrown; never its message.</param>
    public ReflectionFailedException(ReflectionFailureKind kind, string? causeType = null)
        : base(MessageFor(kind, causeType))
    {
        Kind = kind;
        CauseType = causeType;
    }

    /// <summary>Why no reflection was produced.</summary>
    public ReflectionFailureKind Kind { get; }

    /// <summary>The full type name of the exception behind the failure, or <see langword="null"/> when none was thrown.</summary>
    public string? CauseType { get; }

    private static string MessageFor(ReflectionFailureKind kind, string? causeType) => kind switch
    {
        ReflectionFailureKind.ModelCallFailed => causeType is null
            ? "The model call for the reflection failed."
            : $"The model call for the reflection failed with {causeType}.",
        ReflectionFailureKind.TimedOut => "The model did not answer the reflection request within the configured timeout.",
        ReflectionFailureKind.Unparseable => "The model's answer to the reflection request held no JSON object of the requested shape.",
        ReflectionFailureKind.EmptyLesson => "The model's answer to the reflection request had no lesson.",
        ReflectionFailureKind.ToolCallAttempted => "The model's answer to the reflection request carried a tool call or tool result; a reflection never uses tools.",
        ReflectionFailureKind.OutputTooLarge => "The model's answer to the reflection request was over the answer size limit and was not read.",
        ReflectionFailureKind.PromptTooLarge => "The reflection request's data message cannot fit MaxPromptLength even with its lists shortened; nothing was sent.",
        ReflectionFailureKind.ChatOptionsCallbackFailed => causeType is null
            ? "The ConfigureChatOptions callback failed; nothing was sent."
            : $"The ConfigureChatOptions callback failed with {causeType}; nothing was sent.",
        _ => "The model-backed reflection failed.",
    };
}
