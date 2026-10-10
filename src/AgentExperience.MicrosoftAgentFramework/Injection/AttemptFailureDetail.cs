namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// How much a <c>Tried:</c> line says about an attempt that ended with an error. See
/// <see cref="ExperienceInjectionOptions.FailureDetail"/>.
/// </summary>
/// <remarks>
/// A failed tool call's own marker on the line is <c>[failed: &lt;class&gt;]</c>, with only the class's first token, under
/// both <see cref="ErrorClass"/> and <see cref="Excerpt"/> -- an excerpt is never added to a call's marker -- and
/// <c>[failed]</c> under <see cref="None"/>.
/// </remarks>
public enum AttemptFailureDetail
{
    /// <summary>
    /// <c>failed (TimeoutException, exit 2)</c>: the error's class, built only from tokens the library recognises (an
    /// exception type name, an exit code, an HTTP status, a POSIX errno name, a timeout), never other text from the
    /// error. <c>failed (unclassified error)</c> when none is recognised. The default.
    /// </summary>
    ErrorClass = 0,

    /// <summary>
    /// The class, then the error's first non-blank line, cut to
    /// <see cref="HistoricalReferenceWriter.MaxErrorExcerptLength"/> characters, neutralized like an argument value and
    /// quoted. Captured error text then reaches the model, so opt in only where error messages carry nothing a later
    /// model should not read. Applies to the attempt's outcome only, never to a call's <c>[failed: …]</c> marker. A record
    /// borrowed through a sharing grant never shows an excerpt or a failed attempt.
    /// </summary>
    Excerpt = 1,

    /// <summary><c>failed</c>, with no class, and <c>[failed]</c> for a failed call.</summary>
    None = 2,
}
