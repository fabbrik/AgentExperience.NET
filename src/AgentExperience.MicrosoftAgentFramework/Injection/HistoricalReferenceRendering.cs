namespace AgentExperience.MicrosoftAgentFramework.Injection;

/// <summary>
/// How the injected Historical Reference block lays out each record. See
/// <see cref="ExperienceInjectionOptions.Rendering"/>.
/// </summary>
public enum HistoricalReferenceRendering
{
    /// <summary>
    /// The default. A two-line preamble addressed to the model, and per record: a header naming its task; a
    /// <c>Matched:</c> line naming its text relevance from the ranking; its confidence,
    /// verification status and lifecycle status; an <c>Environment:</c> line when the ranking's environment fit is below
    /// 1; then the <c>Shared:</c> line, the lesson, the <c>Tried:</c> and <c>Worked:</c> lines, reuse guidance,
    /// preconditions and warnings. The untrusted framing, the model-authored and unconfirmed fences and withdrawal
    /// notices are as in <see cref="Verbose"/>. Left out: identifiers, the ranking arithmetic, timestamps, the captured
    /// environment fingerprint, the evidence count, empty fields, and, for the default reflector's reflections only, a
    /// precondition it could not capture and its generic verification-scope warning.
    /// </summary>
    Compact = 0,

    /// <summary>
    /// The layout before compact rendering existed: the long preamble, and per record its source identifiers, the
    /// ranking arithmetic, timestamps, environment and evidence count. It is the earlier block byte for byte, except that
    /// a line of record text starting with <c>Matched:</c> is now neutralized like any other field label.
    /// </summary>
    Verbose = 1,
}
