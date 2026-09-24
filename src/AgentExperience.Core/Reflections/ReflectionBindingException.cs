namespace AgentExperience.Core.Reflections;

/// <summary>
/// Thrown when a run, its evaluation and a reflection do not belong together: a
/// <see cref="ReflectionRequest"/> built from an evaluation computed for another run, or a
/// <see cref="AgentExperience.Abstractions.Reflection"/> that does not carry what its request asked
/// for (see <see cref="ReflectionRequest.EnsureMatches"/>).
/// </summary>
/// <remarks>
/// It is an <see cref="ArgumentException"/>, so it stays inside <see cref="IExperienceReflector"/>'s
/// documented "invalid input throws <see cref="ArgumentException"/>" contract, and a host can still
/// catch it by its own type. Its message names identifiers and field names only, never captured text.
/// </remarks>
public sealed class ReflectionBindingException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    public ReflectionBindingException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public ReflectionBindingException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ReflectionBindingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for <paramref name="runId"/>, naming the parameter and the field that does not match.</summary>
    /// <param name="message">The message.</param>
    /// <param name="paramName">The parameter that carried the mismatch (<c>Evaluation</c> for a request, <c>reflection</c> for <see cref="ReflectionRequest.EnsureMatches"/>).</param>
    /// <param name="runId">The run the request is for.</param>
    /// <param name="mismatchedField">The first field found not to match.</param>
    public ReflectionBindingException(string message, string? paramName, Guid runId, string mismatchedField)
        : base(message, paramName)
    {
        RunId = runId;
        MismatchedField = mismatchedField;
    }

    /// <summary>The run the request is for, or <see cref="Guid.Empty"/> when not supplied.</summary>
    public Guid RunId { get; }

    /// <summary>The first field found not to match (for example <c>RunId</c> or <c>EvidenceIds</c>), or <see langword="null"/> when not supplied.</summary>
    public string? MismatchedField { get; }
}
