using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentExperience.ReuseBaseline.Harness;

/// <summary>
/// The pre-registration file could not be read, or it does not say what a pre-registration has to
/// say. Thrown rather than defaulted: a harness that invents a trial count or a gate when the file
/// is missing has no pre-registration at all.
/// </summary>
/// <param name="message">What was wrong with the file.</param>
public sealed class PreregistrationException(string message) : Exception(message);

/// <summary>
/// The pre-registration changed between the moment the trials started and the moment the report was
/// asked to render. The report refuses to render rather than publishing numbers against a design
/// that is no longer the one they were produced under.
/// </summary>
/// <param name="message">Which digest was expected and which was found.</param>
public sealed class PreregistrationTamperedException(string message) : Exception(message);

/// <summary>Which condition labels the pre-registration fixed, and which one index 0 runs under.</summary>
/// <param name="MemoryEnabled">The label for the condition in which stored experience is injected.</param>
/// <param name="MemoryDisabled">The label for the condition in which it is not.</param>
/// <param name="StartingCondition">The label trial index 0 runs under. Must be one of the two above.</param>
public sealed record PreregisteredConditions(
    [property: JsonPropertyName("memoryEnabled")] string MemoryEnabled,
    [property: JsonPropertyName("memoryDisabled")] string MemoryDisabled,
    [property: JsonPropertyName("startingCondition")] string StartingCondition);

/// <summary>One arm the pre-registration declared, and the task set version it must run.</summary>
/// <param name="Id">The arm's identity.</param>
/// <param name="TaskSetVersion">The version of the task set that arm runs. An arm running another version is refused.</param>
/// <param name="Purpose">What the arm is for.</param>
public sealed record PreregisteredArm(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("taskSetVersion")] string TaskSetVersion,
    [property: JsonPropertyName("purpose")] string Purpose);

/// <summary>
/// One change made to this pre-registration after it was first committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A pre-registration that can be edited without saying so is not a pre-registration.</b> The
/// tamper check stops a file changing <em>during</em> a run; it says nothing about a file changed
/// between runs, which is the edit that matters. Every such change is recorded here and printed in
/// the report's header, directly under the blob identity, so a reader cannot take the file for one
/// that was fixed before any result existed unless it actually was.
/// </para>
/// <para>
/// <see cref="ResultsExisted"/> is the field that carries the weight. Adding a <em>control</em>
/// after seeing results is legitimate -- a control makes an existing measurement checkable rather
/// than manufacturing a result -- and choosing a metric after seeing results is not. A reader can
/// only tell the two apart if the report says which happened, so the flag is required rather than
/// optional and the report prints it for every entry.
/// </para>
/// </remarks>
/// <param name="Date">When the change was made.</param>
/// <param name="Change">What changed, in the file's own terms.</param>
/// <param name="Why">Why. Usually a review finding.</param>
/// <param name="ResultsExisted">Whether results already existed when the change was made.</param>
/// <param name="ResultsChanged">Which published numbers moved as a result, or that none did.</param>
public sealed record PreregistrationAmendment(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("why")] string Why,
    [property: JsonPropertyName("resultsExisted")] bool ResultsExisted,
    [property: JsonPropertyName("resultsChanged")] string ResultsChanged);

/// <summary>What the pre-registration says about the thresholds it deliberately does not fix.</summary>
/// <param name="Kind">The kind of gate, e.g. <c>direction-only</c>.</param>
/// <param name="Provisional">Whether the thresholds are labelled provisional.</param>
/// <param name="Note">Why they are what they are.</param>
public sealed record PreregisteredThresholds(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("provisional")] bool Provisional,
    [property: JsonPropertyName("note")] string Note);

/// <summary>
/// The design, fixed in a checked-in file before any result existed: each arm and the task set
/// version it must run, the trial count, the condition labels, the primary metric, the secondary
/// metrics, the guardrail metrics, and the one gate expression.
/// </summary>
/// <remarks>
/// <para>
/// This type is a reader for <c>preregistration.json</c> and nothing more. It has no defaults: every
/// field a gate or a report needs comes out of the file, so "no metric or subset was selected after
/// observing results" is a property of where the values live rather than a promise in prose.
/// </para>
/// <para>
/// <b>Which of these fields the gate actually reads.</b> <see cref="PrimaryMetric"/> and
/// <see cref="GuardrailMetrics"/> decide which terms <see cref="GateEvaluator"/> builds and in what
/// order; <see cref="Conditions"/> supplies the labels each term is worded with;
/// <see cref="MetricsExcludedFromGate"/> is enforced against <see cref="GateExpression"/> when the
/// file is parsed; and <see cref="GateExpression"/> is then compared, character for character,
/// against the terms that were built. Editing any of them changes what is gated on or stops the run.
/// <see cref="SecondaryMetrics"/>, <see cref="Thresholds"/>, <see cref="Attribution"/>,
/// <see cref="ConditionAssignment"/> and <see cref="TaskAssignment"/> are reported rather than
/// executed, and are described as such wherever the report prints them.
/// </para>
/// <para>
/// <see cref="PreregistrationSnapshot.GitBlobId"/> is the value a reader checks with
/// <c>git hash-object tests/AgentExperience.ReuseBaseline/preregistration.json</c>. It is the git
/// object identity of the file's exact bytes. A commit SHA is deliberately not used: the file is
/// introduced by the same commit the report is checked in under, so a report that named its own
/// commit could never be reproduced -- the value would change with the commit that contained it.
/// </para>
/// </remarks>
/// <param name="PreregistrationVersion">The schema version of this file.</param>
/// <param name="RegisteredFor">What the pre-registration is for.</param>
/// <param name="RegisteredAgainstCommit">The repository commit the design was registered against.</param>
/// <param name="Measures">The standing statement of what the experiment measures.</param>
/// <param name="TrialCount">How many trials each arm runs. A run whose plan is a different length is refused.</param>
/// <param name="Conditions">The condition labels and the starting condition.</param>
/// <param name="PrimaryMetric">The one metric the gate's first term is about.</param>
/// <param name="SecondaryMetrics">Reported, never gated on except where they are also guardrails.</param>
/// <param name="GuardrailMetrics">Reported, and gated on as non-regression terms.</param>
/// <param name="MetricsExcludedFromGate">Metrics that are reported and may never enter the gate.</param>
/// <param name="MetricsExcludedFromGateReason">Why they are excluded.</param>
/// <param name="GateExpression">The one predeclared expression, evaluated once. The report prints this string as it is read from the file.</param>
/// <param name="GateEvaluatedOnce">Whether the gate is evaluated exactly once. There is no second gate to fall back to.</param>
/// <param name="GateFailureVerdict">The verdict a failing gate produces.</param>
/// <param name="Thresholds">What the pre-registration says about magnitudes.</param>
/// <param name="ConditionAssignment">How a trial's condition is derived from its index.</param>
/// <param name="TaskAssignment">How a trial's task is derived from its index.</param>
/// <param name="Attribution">What the reference experiment submits to the reuse-feedback ledger.</param>
/// <param name="Arms">The arms this file declares. An arm that is not here is not a pre-registered arm; an arm added by an amendment says so in <paramref name="Amendments"/>.</param>
/// <param name="Amendments">Every change made to this file since it was first committed. Empty means never amended, and the report says which.</param>
public sealed record Preregistration(
    [property: JsonPropertyName("preregistrationVersion")] string PreregistrationVersion,
    [property: JsonPropertyName("registeredFor")] string RegisteredFor,
    [property: JsonPropertyName("registeredAgainstCommit")] string RegisteredAgainstCommit,
    [property: JsonPropertyName("measures")] string Measures,
    [property: JsonPropertyName("trialCount")] int TrialCount,
    [property: JsonPropertyName("conditions")] PreregisteredConditions Conditions,
    [property: JsonPropertyName("primaryMetric")] string PrimaryMetric,
    [property: JsonPropertyName("secondaryMetrics")] IReadOnlyList<string> SecondaryMetrics,
    [property: JsonPropertyName("guardrailMetrics")] IReadOnlyList<string> GuardrailMetrics,
    [property: JsonPropertyName("metricsExcludedFromGate")] IReadOnlyList<string> MetricsExcludedFromGate,
    [property: JsonPropertyName("metricsExcludedFromGateReason")] string MetricsExcludedFromGateReason,
    [property: JsonPropertyName("gateExpression")] string GateExpression,
    [property: JsonPropertyName("gateEvaluatedOnce")] bool GateEvaluatedOnce,
    [property: JsonPropertyName("gateFailureVerdict")] string GateFailureVerdict,
    [property: JsonPropertyName("thresholds")] PreregisteredThresholds Thresholds,
    [property: JsonPropertyName("conditionAssignment")] string ConditionAssignment,
    [property: JsonPropertyName("taskAssignment")] string TaskAssignment,
    [property: JsonPropertyName("attribution")] string Attribution,
    [property: JsonPropertyName("arms")] IReadOnlyList<PreregisteredArm> Arms,
    [property: JsonPropertyName("amendments")] IReadOnlyList<PreregistrationAmendment> Amendments)
{
    /// <summary>How many amendments were made after results already existed.</summary>
    public int AmendmentsAfterResults => Amendments.Count(amendment => amendment.ResultsExisted);

    /// <summary>The declared arm named <paramref name="id"/>.</summary>
    /// <param name="id">The arm's identity.</param>
    /// <exception cref="PreregistrationException">No arm with that identity was pre-registered.</exception>
    public PreregisteredArm ArmFor(string id) =>
        Arms.FirstOrDefault(arm => string.Equals(arm.Id, id, StringComparison.Ordinal))
        ?? throw new PreregistrationException(
            $"No arm '{id}' is pre-registered. Declared arms: [{string.Join(", ", Arms.Select(arm => arm.Id))}]. "
            + "An arm that was not registered before any result existed is not a pre-registered arm.");

    /// <summary>The label for the condition <paramref name="condition"/> names.</summary>
    /// <param name="condition">The condition to label.</param>
    public string LabelFor(TrialCondition condition) => condition == TrialCondition.MemoryEnabled
        ? Conditions.MemoryEnabled
        : Conditions.MemoryDisabled;

    /// <summary>The condition trial index 0 runs under, as the file fixed it.</summary>
    /// <exception cref="PreregistrationException">The starting condition is neither declared label.</exception>
    public TrialCondition StartingCondition =>
        string.Equals(Conditions.StartingCondition, Conditions.MemoryEnabled, StringComparison.Ordinal)
            ? TrialCondition.MemoryEnabled
            : string.Equals(Conditions.StartingCondition, Conditions.MemoryDisabled, StringComparison.Ordinal)
                ? TrialCondition.MemoryDisabled
                : throw new PreregistrationException(
                    $"conditions.startingCondition is '{Conditions.StartingCondition}', which is neither declared condition label.");
}

/// <summary>
/// The pre-registration as it stands on disk right now, with the digest of the exact bytes that were
/// read.
/// </summary>
/// <param name="Design">The parsed design.</param>
/// <param name="GitBlobId">The git object identity of the bytes, checkable with <c>git hash-object</c>.</param>
/// <param name="ByteCount">How many bytes were read.</param>
public sealed record PreregistrationSnapshot(Preregistration Design, string GitBlobId, int ByteCount);

/// <summary>
/// Where the pre-registration is read from, and -- crucially -- read from <em>again</em> when the
/// report is asked to render.
/// </summary>
/// <remarks>
/// The double read is the whole mechanism behind "the report fails to render if the file changed
/// after the trials ran". A source that returned a cached copy would make the check pass by
/// construction, so every implementation here re-reads its underlying bytes on every call.
/// </remarks>
public abstract class PreregistrationSource
{
    /// <summary>The bytes of the pre-registration as they are right now.</summary>
    public abstract byte[] ReadBytes();

    /// <summary>Where the bytes come from, for the report's own provenance line.</summary>
    public abstract string Description { get; }

    /// <summary>Reads, parses, and digests the pre-registration as it stands right now.</summary>
    /// <exception cref="PreregistrationException">The file is missing, unparseable, or incomplete.</exception>
    public PreregistrationSnapshot Read()
    {
        var bytes = ReadBytes();
        var design = Parse(bytes);
        return new PreregistrationSnapshot(design, GitBlobIdOf(bytes), bytes.Length);
    }

    /// <summary>
    /// The pre-registration checked into this repository, read from disk on every call.
    /// </summary>
    /// <remarks>
    /// Read from the working tree rather than from an embedded copy on purpose: an embedded copy is
    /// baked in at build time, so "the file changed after the trials ran" could not be a fact about
    /// the file. The path is resolved from this source file's own compile-time location, the way the
    /// 4.2 sample resolves its golden transcript for regeneration.
    /// </remarks>
    public static PreregistrationSource CheckedIn { get; } = new FileSource(DefaultPath());

    /// <summary>
    /// The same file source <see cref="CheckedIn"/> uses, over an arbitrary path.
    /// </summary>
    /// <remarks>
    /// It exists so the re-read property can be asserted against the <em>shipping</em> source rather
    /// than only against a test double that re-reads by construction. Giving this class a byte cache
    /// would make the tamper check pass by construction, and a double cannot notice that.
    /// </remarks>
    /// <param name="path">Where to read the pre-registration from, on every call.</param>
    public static PreregistrationSource ForFile(string path) => new FileSource(path);

    /// <summary>Where <see cref="CheckedIn"/> reads from.</summary>
    /// <param name="thisFile">Supplied by the compiler; never passed.</param>
    internal static string DefaultPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!, "preregistration.json");

    /// <summary>
    /// The git object identity of <paramref name="bytes"/>: <c>sha1("blob " + length + "\0" + bytes)</c>,
    /// which is exactly what <c>git hash-object</c> prints for a file with those bytes.
    /// </summary>
    /// <param name="bytes">The file's exact bytes.</param>
    public static string GitBlobIdOf(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var header = Encoding.ASCII.GetBytes(
            string.Format(CultureInfo.InvariantCulture, "blob {0}\0", bytes.Length));
        var buffer = new byte[header.Length + bytes.Length];
        header.CopyTo(buffer, 0);
        bytes.CopyTo(buffer, header.Length);

        // SHA-1 because that is the hash git names its objects with. It is an identity check against
        // an accidental edit, never a security claim.
        return Convert.ToHexStringLower(SHA1.HashData(buffer));
    }

    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private static Preregistration Parse(byte[] bytes)
    {
        Preregistration? design;
        try
        {
            design = JsonSerializer.Deserialize<Preregistration>(bytes, ParseOptions);
        }
        catch (JsonException ex)
        {
            throw new PreregistrationException($"The pre-registration is not valid JSON: {ex.Message}");
        }

        if (design is null)
        {
            throw new PreregistrationException("The pre-registration parsed to null.");
        }

        Require(design.PreregistrationVersion, nameof(design.PreregistrationVersion));
        Require(design.PrimaryMetric, nameof(design.PrimaryMetric));
        Require(design.GateExpression, nameof(design.GateExpression));
        Require(design.GateFailureVerdict, nameof(design.GateFailureVerdict));
        Require(design.Measures, nameof(design.Measures));

        if (design.Conditions is null)
        {
            throw new PreregistrationException("The pre-registration declares no conditions.");
        }

        Require(design.Conditions.MemoryEnabled, "conditions.memoryEnabled");
        Require(design.Conditions.MemoryDisabled, "conditions.memoryDisabled");
        Require(design.Conditions.StartingCondition, "conditions.startingCondition");

        if (design.TrialCount <= 0 || design.TrialCount % 2 != 0)
        {
            throw new PreregistrationException(
                $"trialCount is {design.TrialCount.ToString(CultureInfo.InvariantCulture)}; a balanced alternating design needs a positive even count.");
        }

        if (design.Thresholds is null)
        {
            throw new PreregistrationException("The pre-registration declares no thresholds section.");
        }

        if (design.SecondaryMetrics is null || design.GuardrailMetrics is null || design.MetricsExcludedFromGate is null)
        {
            throw new PreregistrationException("The pre-registration must declare its secondary, guardrail, and excluded metrics, even when a list is empty.");
        }

        if (design.Arms is not { Count: > 0 })
        {
            throw new PreregistrationException("The pre-registration declares no arms, so no run of the harness could be a pre-registered one.");
        }

        // Required even when empty, and never defaulted. A missing array would render as "never
        // amended", which is the one thing a silently amended file would most like the report to say.
        if (design.Amendments is null)
        {
            throw new PreregistrationException(
                "The pre-registration declares no 'amendments' array. It is required even when it is empty: an absent array "
                + "is indistinguishable from an empty one in the report, and 'never amended' is exactly what an amendment "
                + "made without recording it would want the report to print.");
        }

        for (var index = 0; index < design.Amendments.Count; index++)
        {
            var amendment = design.Amendments[index];
            var where = $"amendments[{index.ToString(CultureInfo.InvariantCulture)}]";

            Require(amendment?.Date, $"{where}.date");
            Require(amendment?.Change, $"{where}.change");
            Require(amendment?.Why, $"{where}.why");
            Require(amendment?.ResultsChanged, $"{where}.resultsChanged");
        }

        // Read back out of the expression itself rather than trusted: a gate that names a metric the
        // file excluded from the gate would be the exact failure this file exists to prevent.
        foreach (var excluded in design.MetricsExcludedFromGate)
        {
            if (design.GateExpression.Contains(excluded, StringComparison.Ordinal))
            {
                throw new PreregistrationException(
                    $"The gate expression names '{excluded}', which metricsExcludedFromGate forbids it from naming.");
            }
        }

        if (!design.GateExpression.Contains(design.PrimaryMetric, StringComparison.Ordinal))
        {
            throw new PreregistrationException(
                $"The gate expression does not name the primary metric '{design.PrimaryMetric}'.");
        }

        return design;
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PreregistrationException($"The pre-registration's '{field}' is missing or blank.");
        }
    }

    private sealed class FileSource(string path) : PreregistrationSource
    {
        public override string Description => Path.GetFileName(path);

        public override byte[] ReadBytes()
        {
            if (!File.Exists(path))
            {
                throw new PreregistrationException(
                    $"The pre-registration was not found at '{path}'. It is read from the working tree on every call, "
                    + "because an embedded copy is fixed at build time and could not tell you that the file changed after the trials ran.");
            }

            return File.ReadAllBytes(path);
        }
    }
}
