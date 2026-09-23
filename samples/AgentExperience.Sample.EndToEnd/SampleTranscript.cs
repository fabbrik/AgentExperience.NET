using System.Globalization;
using System.Text;
using AgentExperience.Abstractions;

namespace AgentExperience.Sample.EndToEnd;

/// <summary>Which ports the sample was composed with.</summary>
public enum SampleStorageMode
{
    /// <summary>The sample's own in-memory demonstration doubles. No Docker, no database, no credentials.</summary>
    InMemory,

    /// <summary>The real PostgreSQL adapters, against the connection string in <c>AGENTEXPERIENCE_SAMPLE_POSTGRES</c>.</summary>
    Postgres,
}

/// <summary>
/// One of the sample's seven stages: what it did, and the library's own outcome values for it.
/// </summary>
/// <param name="Number">The stage's position, 1 through 7.</param>
/// <param name="Name">The stage name from the story's frozen narrative: <c>capture</c>, <c>verify</c>, <c>finalize</c>, <c>retrieve</c>, <c>inject</c>, or <c>feedback</c>.</param>
/// <param name="Summary">One line saying what this stage showed.</param>
/// <param name="Outcomes">The library's own outcome values, as <c>EnumType.Value</c>, in the order they were produced.</param>
/// <param name="Details">Supporting lines: identifiers, counts, and budgets. Content-free.</param>
public sealed record SampleStage(
    int Number,
    string Name,
    string Summary,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string> Details);

/// <summary>
/// Everything one execution of the sample produced: the seven stages, the identifiers the loop
/// turned on, and the reuse feedback it submitted.
/// </summary>
/// <param name="Mode">Which ports the run was composed with.</param>
/// <param name="Stages">The seven stages, in order.</param>
/// <param name="RunAId">The Experience Run the two attempts were captured on.</param>
/// <param name="RunBId">The Experience Run the record was injected into, read from the capture middleware's session state -- never from agent output.</param>
/// <param name="ExperienceId">The durable Experience Record the first run became.</param>
/// <param name="SubmittedFeedback">The reuse feedback exactly as it was submitted, so a caller can check what was and was not claimed.</param>
public sealed record SampleTranscript(
    SampleStorageMode Mode,
    IReadOnlyList<SampleStage> Stages,
    Guid RunAId,
    Guid RunBId,
    Guid ExperienceId,
    ExperienceReuseFeedback SubmittedFeedback)
{
    /// <summary>
    /// The line separator every rendered line ends with. Explicitly <c>'\n'</c> and never
    /// <see cref="System.Environment.NewLine"/>: "two runs print the same bytes" has to hold across
    /// operating systems as well as across executions, and
    /// <c>HistoricalReferenceWriter</c> makes the same choice for the same reason.
    /// </summary>
    public const char LineSeparator = '\n';

    /// <summary>
    /// The separator between the parts of a one-line outcome summary. Deliberately ASCII: the
    /// transcript is meant to be legible when it is piped, diffed, or read on a console that is not
    /// running a UTF-8 code page.
    /// </summary>
    public const string FieldSeparator = " | ";

    /// <summary>
    /// What the sample writes to standard output, and the exact text the sample's own tests assert
    /// on. Two executions of the sample render byte-identical text, on any operating system and
    /// under any current culture.
    /// </summary>
    public string Render()
    {
        var text = new StringBuilder();

        Line(text, "AgentExperience.NET -- end-to-end sample");
        Line(text, Mode == SampleStorageMode.InMemory
            ? "ports:  in-memory demonstration doubles (set AGENTEXPERIENCE_SAMPLE_POSTGRES to a connection string for the PostgreSQL adapters)"
            : "ports:  the PostgreSQL adapters, migrated before the first stage");
        Line(text, "clock:  a stepping fixture; identifiers: a counter. Two runs print the same bytes.");
        Line(text, string.Empty);

        foreach (var stage in Stages)
        {
            Line(text, string.Format(
                CultureInfo.InvariantCulture,
                "[{0}] {1,-8} {2}",
                stage.Number,
                stage.Name,
                stage.Summary));

            foreach (var outcome in stage.Outcomes)
            {
                Line(text, "             -> " + outcome);
            }

            foreach (var detail in stage.Details)
            {
                Line(text, "                " + detail);
            }

            Line(text, string.Empty);
        }

        Line(text, "What this run demonstrates: that the mechanism works end to end -- an Experience Run is");
        Line(text, "captured with both of its attempts, verified from host-supplied evidence, reflected on,");
        Line(text, "persisted, retrieved by a second run, injected as a labelled Historical Reference, and");
        Line(text, "that the exposure is recorded.");
        Line(text, string.Empty);
        Line(text, "What it does not demonstrate: anything at all about model quality. The model here is a");
        Line(text, "scripted fixture with no credentials and no network, the clock and the identifiers are");
        Line(text, "fixtures too, and the reuse feedback claims no benefit because none was measured.");
        Line(text, "Measuring what reuse does to a real model is story 4.4's job, not this sample's.");

        return text.ToString();
    }

    private static void Line(StringBuilder text, string content) => text.Append(content).Append(LineSeparator);
}
